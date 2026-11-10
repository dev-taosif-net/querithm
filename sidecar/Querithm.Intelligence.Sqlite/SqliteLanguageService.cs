using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Intelligence.Sqlite;

/// <summary>
/// SQLite dialect intelligence (Phase D1), on <see cref="SqliteTokenizer"/> tokens — never regex over query text. Tier 1
/// completion lives in the webview; tier 2 and signature help are <see cref="SqliteCompletion"/>. There is no SQLite parser
/// in the sidecar, so diagnostics stay empty and the engine reports syntax errors when it runs. The classifier finds destructive statements, DDL, transaction statements, and the
/// statements the file policy refuses outright (D0c): ATTACH / DETACH, VACUUM INTO, load_extension(), and pragmas that
/// change files or the security settings.
/// </summary>
/// <summary>
/// The one list of pragmas no statement may set (reading them stays allowed): the classifier's refusal
/// (<see cref="SqliteLanguageService"/>) and the native authorizer (SqliteHardening) both read it, so the two cannot drift.
/// </summary>
public static class SqlitePragmaPolicy
{
    /// <summary>
    /// Pragmas that write elsewhere on disk or loosen the hardening of untrusted files. PT2-1: temp_store too — the hardening
    /// keeps temporary tables and sort spills in memory, and temp_store = FILE would spill query data into the OS temp
    /// directory, outside &lt;appDataDir&gt;/querithm, even on a read-only grant.
    /// </summary>
    public static readonly IReadOnlySet<string> RefusedToSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "temp_store", "temp_store_directory", "data_store_directory", "writable_schema", "trusted_schema", "journal_mode",
        "schema_version", "legacy_file_format", "cell_size_check", "mmap_size", "locking_mode",
    };
}

public sealed class SqliteLanguageService : IDialectService
{
    static IReadOnlySet<string> RefusedPragmas => SqlitePragmaPolicy.RefusedToSet;

    /// <summary>
    /// L4: an encryption key (SQLCipher-style builds): <c>PRAGMA [schema.]key | rekey | hexkey | hexrekey | textkey | textrekey
    /// = '…'</c> (or <c>('…')</c>) and <c>ATTACH … KEY '…'</c>; the shared rules cover the rest.
    /// </summary>
    static readonly SecretRules SecretSyntax = new() { Extra = KeyLiteral, Triggers = ["KEY"] };

    static readonly HashSet<string> KeyPragmas = new(StringComparer.OrdinalIgnoreCase) { "key", "rekey", "hexkey", "hexrekey", "textkey", "textrekey" };

    static int KeyLiteral(IReadOnlyList<RedactToken> t, int i)
    {
        if (t[i].Is("PRAGMA"))
        {
            var j = i + 1;
            if (j + 2 < t.Count && t[j + 1].IsP(".")) j += 2;
            if (j + 2 < t.Count && t[j].Kind is RedactTokenKind.Word or RedactTokenKind.QuotedName && KeyPragmas.Contains(t[j].Name)
                && (t[j + 1].IsP("=") || t[j + 1].IsP("(")) && t[j + 2].Kind == RedactTokenKind.String)
                return j + 2;
            return -1;
        }
        if (!t[i].Is("KEY") || i + 1 >= t.Count || t[i + 1].Kind != RedactTokenKind.String) return -1;
        // KEY '…' only in ATTACH: walk back to the statement's first token.
        var s = i;
        while (s > 0 && !t[s - 1].IsP(";")) s--;
        return t[s].Is("ATTACH") ? i + 1 : -1;
    }

    /// <summary>M12: the PostgreSQL-family caches this engine's completion fills (documents, statement splits, USE / SET scans).</summary>
    public void Forget(string docId) => DocumentCaches.Forget(docId);

    public Redaction RedactSecrets(string text)
    {
        if (!SecretRedaction.MayContainSecrets(text, SecretSyntax)) return new Redaction(text, []);
        var list = new List<RedactToken>();
        foreach (var t in SqliteTokenizer.Tokenize(text))
        {
            RedactTokenKind? kind = t.Kind switch
            {
                SqliteTokenKind.Whitespace or SqliteTokenKind.Comment => null,
                SqliteTokenKind.Word => RedactTokenKind.Word,
                SqliteTokenKind.QuotedIdent => RedactTokenKind.QuotedName,
                SqliteTokenKind.String or SqliteTokenKind.Blob => RedactTokenKind.String,
                SqliteTokenKind.Number => RedactTokenKind.Number,
                SqliteTokenKind.Operator or SqliteTokenKind.Punct => RedactTokenKind.Punct,
                _ => RedactTokenKind.Other,
            };
            if (kind is not { } k) continue;
            list.Add(new RedactToken(k, t.Offset, t.Length, t.Text, k is RedactTokenKind.Word or RedactTokenKind.QuotedName ? SqliteTokenizer.Unquote(t) : ""));
        }
        return SecretRedaction.Apply(text, list, SecretSyntax);
    }

    public Classification Classify(string text)
    {
        var tokens = SqliteTokenizer.Tokenize(text);
        var statements = SqliteSplitter.Split(tokens);
        var destructive = new List<DestructiveFinding>();
        var batches = new List<BatchInfo>();
        // PT7-4: SQLite compiles the text as a C string, so it stops at a NUL: "DELETE FROM t\0 WHERE 0" deletes every row,
        // and Microsoft.Data.Sqlite never gets past the NUL (it prepares the same empty tail forever, a hang no Cancel ends).
        // Refused before it runs, whatever the user confirms.
        string? refusal = SqlChars.NulRefusal(text, "SQLite");
        bool ddl = false, begins = false, ends = false;
        foreach (var st in statements)
        {
            var code = st.Where(t => !t.IsTrivia).ToList();
            if (code.Count == 0) continue;
            var first = code[0];
            var last = st.Last(t => !t.IsTrivia);
            batches.Add(new BatchInfo
            {
                Index = batches.Count, StartLine = first.Line, EndLine = last.Line, StartOffset = first.Offset,
                Length = last.Offset + last.Length - first.Offset,
            });
            var verb = Verb(code);
            var stmtText = Excerpt(text, first, last);
            refusal ??= Refused(code, verb);
            switch (verb)
            {
                case "DROP":
                    ddl = true;
                    destructive.Add(Finding("drop", stmtText, first, "DROP removes the object and its data."));
                    break;
                case "ALTER":
                    ddl = true;
                    destructive.Add(Finding("alter", stmtText, first, "ALTER changes the table's definition."));
                    break;
                case "CREATE":
                    ddl = true;
                    break;
                case "DELETE" when !HasTopLevel(code, verb, "WHERE"):
                    destructive.Add(Finding("deleteWithoutWhere", stmtText, first, "DELETE without WHERE removes every row."));
                    break;
                case "UPDATE" when !HasTopLevel(code, verb, "WHERE"):
                    destructive.Add(Finding("updateWithoutWhere", stmtText, first, "UPDATE without WHERE changes every row."));
                    break;
                case "BEGIN":
                    begins = true;
                    break;
                case "COMMIT" or "END":
                    ends = true;
                    break;
                case "ROLLBACK" when !code.Skip(1).Any(t => t.Is("TO")):
                    ends = true;
                    break;
            }
        }
        return new Classification
        {
            Destructive = destructive.ToArray(), HasDdl = ddl, BeginsTransaction = begins, EndsTransaction = ends,
            Batches = batches.ToArray(), ParseErrors = [], Refusal = refusal,
        };
    }

    /// <summary>The statement's verb: the first keyword after an optional WITH … clause (WITH t AS (…) DELETE …).</summary>
    static string Verb(List<SqliteToken> code)
    {
        if (!code[0].Is("WITH")) return code[0].Text.ToUpperInvariant();
        var depth = 0;
        for (var i = 1; i < code.Count; i++)
        {
            var t = code[i];
            if (t.IsP("(")) depth++;
            else if (t.IsP(")")) depth--;
            else if (depth == 0 && t.Kind == SqliteTokenKind.Word && (t.Is("SELECT") || t.Is("INSERT") || t.Is("UPDATE") || t.Is("DELETE") || t.Is("REPLACE") || t.Is("VALUES")))
                return t.Text.ToUpperInvariant();
        }
        return "WITH";
    }

    /// <summary>
    /// A keyword at parenthesis depth 0 after the statement's verb (a WHERE inside a subquery does not count), with some code
    /// after it (PT7-1 safety net): a WHERE that ends the statement is no condition, whatever the tokenizer made of the text
    /// before it.
    /// </summary>
    static bool HasTopLevel(List<SqliteToken> code, string verb, string keyword)
    {
        var depth = 0;
        var seenVerb = false;
        for (var i = 0; i < code.Count; i++)
        {
            var t = code[i];
            if (t.IsP("(")) { depth++; continue; }
            if (t.IsP(")")) { depth--; continue; }
            if (depth != 0) continue;
            if (!seenVerb) { seenVerb = t.Is(verb); continue; }
            if (t.Is(keyword) && i + 1 < code.Count && !code[i + 1].IsP(";")) return true;
        }
        return false;
    }

    /// <summary>The file policy (D0c): what no grant allows, whatever the user confirms.</summary>
    static string? Refused(List<SqliteToken> code, string verb)
    {
        if (verb is "ATTACH" or "DETACH")
            return "ATTACH and DETACH are not available: Querithm opens only the database file you picked (other files need a grant of their own).";
        if (verb == "VACUUM" && code.Any(t => t.Is("INTO")))
            return "VACUUM INTO writes a new file, which no grant covers. Copy the database with your file manager instead.";
        for (var i = 0; i + 1 < code.Count; i++)
            // A quoted name calls the function too ("load_extension"(…), [load_extension](…)).
            if (code[i].Kind is SqliteTokenKind.Word or SqliteTokenKind.QuotedIdent
                && string.Equals(SqliteTokenizer.Unquote(code[i]), "load_extension", StringComparison.OrdinalIgnoreCase) && code[i + 1].IsP("("))
                return "Loading SQLite extensions is not allowed: they are native code that runs inside Querithm.";
        if (verb == "PRAGMA" && code.Count >= 2)
        {
            // PRAGMA [schema.]name [= value | (value)]
            var nameAt = code.Count >= 4 && code[2].IsP(".") ? 3 : 1;
            var name = SqliteTokenizer.Unquote(code[nameAt]);
            var sets = code.Count > nameAt + 1 && (code[nameAt + 1].IsP("=") || code[nameAt + 1].IsP("("));
            if (sets && RefusedPragmas.Contains(name))
                return $"PRAGMA {name} cannot be changed here: it writes files outside the granted database or loosens the protection of untrusted files.";
        }
        return null;
    }

    static DestructiveFinding Finding(string rule, string statement, SqliteToken at, string description) =>
        new() { Rule = rule, Statement = statement, Line = at.Line, Column = 1, Description = description };

    static string Excerpt(string text, SqliteToken first, SqliteToken last)
    {
        var s = text[first.Offset..(last.Offset + last.Length)];
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema)
    {
        using var _ = PgCompletionRules.Use(SqliteCompletion.Rules);
        return PgTier2Completion.Complete(p, schema);
    }

    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema)
    {
        using var _ = PgCompletionRules.Use(SqliteCompletion.Rules);
        return PgTier2Completion.Signature(p, schema);
    }

    public Diagnostic[] Diagnostics(DiagnosticsParams p) => [];
    public FormatResult Format(FormatParams p) => new() { Text = p.Text };

    /// <summary>One region per multi-line statement.</summary>
    public FoldingRange[] Folding(FoldingParams p) =>
        SqliteSplitter.Split(SqliteTokenizer.Tokenize(p.Text))
            .Select(st => st.Where(t => !t.IsTrivia).ToList()).Where(c => c.Count > 0 && c[^1].Line > c[0].Line)
            .Select(c => new FoldingRange { StartLine = c[0].Line, EndLine = c[^1].Line }).ToArray();
}

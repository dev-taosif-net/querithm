using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Intelligence.MySql;

/// <summary>
/// MySQL / MariaDB dialect intelligence (Phase D2) on <see cref="MySqlTokenizer"/> tokens, never regex over query text.
/// Executable comments are read as code (so <c>/*!50000 DROP TABLE t */</c> is a DROP); a client <c>DELIMITER</c> keeps
/// stored-program bodies whole. Destructive: DROP, TRUNCATE, DELETE / UPDATE without a top-level WHERE, ALTER, and what
/// a prepared statement runs: <c>PREPARE s FROM 'literal'</c> is classified like the script, while <c>PREPARE … FROM @var</c>
/// and <c>EXECUTE</c> of a statement not prepared in the text cannot be checked and count as destructive (<c>dynamicSql</c>). DDL (which
/// commits implicitly on these engines) drives the metadata refresh. <c>USE db</c> switches the session's database.
/// <c>LOAD DATA LOCAL INFILE</c> is refused outright (the driver keeps local-infile off; Phase D2 bulk-import boundary).
/// Tier 2 completion and signature help are <see cref="MySqlCompletion"/>.
/// </summary>
public sealed class MySqlLanguageService(MySqlLexMode? mode = null) : IDialectService
{
    readonly MySqlLexMode _mode = mode ?? MySqlLexMode.Default;

    static readonly HashSet<string> DdlVerbs = new(StringComparer.OrdinalIgnoreCase) { "CREATE", "ALTER", "DROP", "RENAME", "TRUNCATE" };

    /// <summary>L4: account syntax (IDENTIFIED BY / AS / USING, REPLACE, SET PASSWORD), replication and server options, and the password functions.</summary>
    static readonly SecretRules SecretSyntax = new()
    {
        IdentifiedBy = true,
        SecretArguments = new Dictionary<string, int[]> { ["PASSWORD"] = [0], ["OLD_PASSWORD"] = [0], ["AES_ENCRYPT"] = [1], ["AES_DECRYPT"] = [1] },
    };

    /// <summary>M12: the PostgreSQL-family caches this engine's completion fills (documents, statement splits, USE / SET scans).</summary>
    public void Forget(string docId) => DocumentCaches.Forget(docId);

    public Redaction RedactSecrets(string text)
    {
        if (!SecretRedaction.MayContainSecrets(text, SecretSyntax)) return new Redaction(text, []);
        var list = new List<RedactToken>();
        foreach (var t in MySqlTokenizer.Tokenize(text, _mode))
        {
            RedactTokenKind? kind = t.Kind switch
            {
                MySqlTokenKind.Whitespace or MySqlTokenKind.Comment or MySqlTokenKind.Hint => null,
                MySqlTokenKind.Word => RedactTokenKind.Word,
                MySqlTokenKind.QuotedIdent => RedactTokenKind.QuotedName,
                MySqlTokenKind.String => RedactTokenKind.String,
                MySqlTokenKind.Number => RedactTokenKind.Number,
                MySqlTokenKind.Variable => RedactTokenKind.Variable,
                MySqlTokenKind.Operator or MySqlTokenKind.Punct => RedactTokenKind.Punct,
                _ => RedactTokenKind.Other,
            };
            if (kind is not { } k) continue;
            var name = k switch
            {
                RedactTokenKind.Word => t.Text,
                RedactTokenKind.QuotedName => MySqlTokenizer.Unquote(t),
                RedactTokenKind.Variable => t.Text.TrimStart('@').Trim('`', '\'', '"'),
                _ => "",
            };
            list.Add(new RedactToken(k, t.Offset, t.Length, t.Text, name));
        }
        return SecretRedaction.Apply(text, list, SecretSyntax);
    }

    public Classification Classify(string text)
    {
        _text = text;
        var destructive = new List<DestructiveFinding>();
        var batches = new List<BatchInfo>();
        string? refusal = null, use = null;
        bool ddl = false, begins = false, ends = false;
        var prepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in MySqlSplitter.Split(text, _mode))
        {
            var code = st.Tokens.Where(t => !t.IsTrivia).ToList();
            if (code.Count == 0) continue;
            // Token lines count from where each statement was tokenized: lines are taken from offsets in the whole text.
            var startLine = LineAt(text, st.Start);
            batches.Add(new BatchInfo
            {
                Index = batches.Count, StartLine = startLine, EndLine = LineAt(text, st.End), StartOffset = st.Start, Length = st.End - st.Start,
            });
            var verb = Verb(code);
            var stmtText = text[st.Start..st.End];
            if (stmtText.Length > 200) stmtText = stmtText[..200] + "…";
            if (DdlVerbs.Contains(verb)) ddl = true;
            void Inner(string sql, string when)
            {
                var inner = new MySqlLanguageService(_mode).Classify(sql);
                ddl |= inner.HasDdl;
                refusal ??= inner.Refusal;
                foreach (var f in inner.Destructive)
                    destructive.Add(f with { Statement = stmtText, Line = LineAt(text, code[0].Offset), Description = f.Description + when });
            }
            switch (verb)
            {
                case "DROP":
                    destructive.Add(Finding("drop", stmtText, code[0], "DROP removes the object and its data."));
                    break;
                case "TRUNCATE":
                    destructive.Add(Finding("truncate", stmtText, code[0], "TRUNCATE removes every row (and commits)."));
                    break;
                case "ALTER":
                    destructive.Add(Finding("alter", stmtText, code[0], "ALTER changes the definition (and commits)."));
                    break;
                case "DELETE" when !HasTopLevel(code, verb, "WHERE", needsOperand: true):
                    destructive.Add(Finding("deleteWithoutWhere", stmtText, code[0], "DELETE without WHERE removes every row."));
                    break;
                case "UPDATE" when !HasTopLevel(code, verb, "WHERE", needsOperand: true):
                    destructive.Add(Finding("updateWithoutWhere", stmtText, code[0], "UPDATE without WHERE changes every row."));
                    break;
                case "USE" when code.Count > 1:
                    use = MySqlTokenizer.Unquote(code[1]);
                    break;
                case "BEGIN" when !(code.Count > 1 && code[1].Is("NOT")):
                case "START" when code.Count > 1 && code[1].Is("TRANSACTION"):
                    begins = true;
                    break;
                case "COMMIT":
                case "ROLLBACK" when !code.Any(t => t.Is("TO")):
                    ends = true;
                    break;
                case "PREPARE" when code.Count > 3 && code[2].Is("FROM"):
                    prepared.Add(MySqlTokenizer.Unquote(code[1]));
                    // The statement text is a literal (a charset introducer, _utf8mb4'…', included): classified as a script
                    // of its own (its lines are the PREPARE's).
                    if (Literal(code, 3) is { } prep && prep.Next == code.Count)
                        Inner(prep.Value, " It runs when the prepared statement is executed.");
                    else
                        destructive.Add(Finding("dynamicSql", stmtText, code[0], "PREPARE … FROM a variable prepares SQL text built at run time, which cannot be checked for destructive statements."));
                    break;
                // MariaDB: EXECUTE IMMEDIATE 'literal' [USING …] runs that text now — classified like PREPARE's (8a review).
                case "EXECUTE" when code.Count > 2 && code[1].Is("IMMEDIATE"):
                    if (Literal(code, 2) is { } now && (now.Next == code.Count || code[now.Next].Is("USING")))
                        Inner(now.Value, " It runs as EXECUTE IMMEDIATE's dynamic SQL.");
                    else
                        destructive.Add(Finding("dynamicSql", stmtText, code[0], "EXECUTE IMMEDIATE runs SQL text built at run time, which cannot be checked for destructive statements."));
                    break;
                case "EXECUTE" when code.Count > 1 && !prepared.Contains(MySqlTokenizer.Unquote(code[1])):
                    destructive.Add(Finding("dynamicSql", stmtText, code[0], "EXECUTE runs a prepared statement that is not prepared in this text, so it cannot be checked for destructive statements."));
                    break;
                case "LOAD" when code.Any(t => t.Is("LOCAL")):
                    refusal ??= "LOAD DATA LOCAL is turned off: it lets the server name any file on this computer to read. Use Import data… instead.";
                    break;
            }
        }
        return new Classification
        {
            Destructive = destructive.ToArray(), HasDdl = ddl, BeginsTransaction = begins, EndsTransaction = ends, UseDatabase = use,
            Batches = batches.ToArray(), ParseErrors = [], Refusal = refusal,
        };
    }

    /// <summary>
    /// The literal at <paramref name="i"/> and the index after it: a string, or one after a charset introducer
    /// (<c>_utf8mb4'…'</c>, <c>N'…'</c>: MySQL's literal syntax); null for anything else (a variable, an expression).
    /// </summary>
    static (string Value, int Next)? Literal(IReadOnlyList<MySqlToken> code, int i)
    {
        if (i < code.Count && code[i].Kind == MySqlTokenKind.String) return (StringValue(code[i].Text), i + 1);
        if (i + 1 < code.Count && code[i].Kind == MySqlTokenKind.Word && (code[i].Text.StartsWith('_') || code[i].Is("N"))
            && code[i + 1].Kind == MySqlTokenKind.String)
            return (StringValue(code[i + 1].Text), i + 2);
        return null;
    }

    /// <summary>The value of a quoted string token: outer quotes off, doubled quotes and backslash escapes undone.</summary>
    static string StringValue(string quoted)
    {
        if (quoted.Length < 2) return quoted;
        var q = quoted[0];
        var body = quoted[1..^1];
        var sb = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length) { sb.Append(body[++i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', var x => x }); continue; }
            if (c == q && i + 1 < body.Length && body[i + 1] == q) i++;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The verb after an optional WITH … clause (upper case; "WITH" when none follows).</summary>
    public static string Verb(IReadOnlyList<MySqlToken> code)
    {
        if (!code[0].Is("WITH")) return code[0].Text.ToUpperInvariant();
        var depth = 0;
        for (var i = 1; i < code.Count; i++)
        {
            var t = code[i];
            if (t.IsP("(")) depth++;
            else if (t.IsP(")")) depth--;
            else if (depth == 0 && (t.Is("SELECT") || t.Is("UPDATE") || t.Is("DELETE") || t.Is("INSERT") || t.Is("REPLACE"))) return t.Text.ToUpperInvariant();
        }
        return "WITH";
    }

    /// <summary>
    /// The keyword appears at parenthesis depth 0 after the verb. With <paramref name="needsOperand"/> (WHERE), only a keyword
    /// some code follows counts (PT7-1 safety net): a WHERE that ends the statement is not a condition, whatever the
    /// tokenizer made of the text before it.
    /// </summary>
    public static bool HasTopLevel(IReadOnlyList<MySqlToken> code, string verb, string keyword, bool needsOperand = false)
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
            if (t.Is(keyword) && (!needsOperand || (i + 1 < code.Count && !code[i + 1].IsTrivia && !code[i + 1].IsP(";")))) return true;
        }
        return false;
    }

    static int LineAt(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    DestructiveFinding Finding(string rule, string statement, MySqlToken at, string description) =>
        new() { Rule = rule, Statement = statement, Line = LineAt(_text!, at.Offset), Column = 1, Description = description };

    string? _text;

    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema)
    {
        var database = MySqlCompletion.DatabaseAt(p.DocId, p.Text ?? "", Math.Clamp(p.Offset, 0, (p.Text ?? "").Length), _mode) ?? p.Database;
        using var _ = PgCompletionRules.Use(MySqlCompletion.RulesFor(_mode, database ?? ""));
        return PgTier2Completion.Complete(p with { Database = database }, schema);
    }

    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema)
    {
        var database = MySqlCompletion.DatabaseAt(p.DocId, p.Text ?? "", Math.Clamp(p.Offset, 0, (p.Text ?? "").Length), _mode) ?? p.Database;
        using var _ = PgCompletionRules.Use(MySqlCompletion.RulesFor(_mode, database ?? ""));
        return PgTier2Completion.Signature(p with { Database = database }, schema);
    }

    public Diagnostic[] Diagnostics(DiagnosticsParams p) => [];
    public FormatResult Format(FormatParams p) => new() { Text = p.Text };

    public FoldingRange[] Folding(FoldingParams p) =>
        MySqlSplitter.Split(p.Text, _mode).Select(st => (Start: LineAt(p.Text, st.Start), End: LineAt(p.Text, st.End)))
            .Where(x => x.End > x.Start).Select(x => new FoldingRange { StartLine = x.Start, EndLine = x.End }).ToArray();
}

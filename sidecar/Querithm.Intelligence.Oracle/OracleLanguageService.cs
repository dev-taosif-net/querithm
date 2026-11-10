using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.Oracle;

/// <summary>
/// Oracle dialect intelligence (D4 S02) on <see cref="OracleTokenizer"/> tokens and <see cref="OracleSplitter"/>
/// statements, never regex over SQL text. One batch per SQL statement or PL/SQL unit, in the form the driver takes (no
/// trailing <c>;</c> on SQL, <c>END;</c> kept on PL/SQL). Destructive: <c>DROP</c> (<c>PURGE</c>, <c>CASCADE
/// CONSTRAINTS</c>), <c>TRUNCATE</c>, <c>DELETE</c> / <c>UPDATE</c> without a top-level <c>WHERE</c>, every <c>MERGE</c>
/// (it may delete), <c>ALTER TABLE … DROP | TRUNCATE | SET UNUSED</c>, <c>FLASHBACK</c>, <c>PURGE</c>. Inside an anonymous
/// block, the static DML is classified the same way and <c>EXECUTE IMMEDIATE 'literal'</c> as a script of its own;
/// <c>EXECUTE IMMEDIATE</c> of an expression and <c>DBMS_SQL</c> / <c>DBMS_UTILITY.EXEC_DDL_STATEMENT</c> cannot be
/// checked and count as destructive (<c>dynamicSql</c>). DDL (which commits before and after) drives the metadata refresh.
/// <c>ALTER SESSION SET CURRENT_SCHEMA</c> changes the completion schema (<see cref="CurrentSchemaAt"/>), never the
/// database. SQL*Plus commands are not sent: <c>SET SERVEROUTPUT ON|OFF</c> is reported by <see cref="ServerOutputToggles"/>,
/// every other command and any <c>&amp;substitution</c> variable is a refusal with the reason (O5).
/// Completion, diagnostics, signature help and formatting are later sessions (S06); they answer empty or unchanged.
/// </summary>
public sealed class OracleLanguageService : IDialectService
{
    /// <summary>Formatting Oracle SQL arrives with the editor work (D4 S06).</summary>
    public const string NoFormatter = "Formatting Oracle SQL is not available in this build of Querithm yet.";

    /// <summary>Kept for the provider's not-yet-implemented paths (ad-hoc parameters arrive in D4 S04).</summary>
    public const string NotReadYet = "Oracle ad-hoc parameters are not available in this build of Querithm yet.";

    /// <summary>Verbs whose statements are DDL (an implicit commit before and after).</summary>
    static readonly HashSet<string> DdlVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "COMMENT", "GRANT", "REVOKE", "ANALYZE", "AUDIT", "NOAUDIT", "PURGE",
        "FLASHBACK", "ASSOCIATE", "DISASSOCIATE",
    };

    public Classification Classify(string text)
    {
        text ??= "";
        var state = new ClassifyState(text);
        foreach (var st in OracleSplitter.Split(text))
        {
            if (st.Kind == OracleStatementKind.ClientCommand)
            {
                state.Refusal ??= st.Command!.Refusal;
                continue;
            }
            var code = st.Code.ToList();
            state.Batches.Add(new BatchInfo
            {
                Index = state.Batches.Count, StartLine = st.StartLine, EndLine = st.EndLine, StartOffset = st.Start, Length = st.End - st.Start,
            });
            state.Refusal ??= Substitution(code);
            var stmtText = Shorten(text[st.Start..st.End]);
            if (st.Kind == OracleStatementKind.PlSql) ClassifyPlSql(state, code, stmtText);
            else ClassifySql(state, code, stmtText);
        }
        return new Classification
        {
            Destructive = state.Destructive.ToArray(), HasDdl = state.Ddl, BeginsTransaction = state.Begins, EndsTransaction = state.Ends,
            UseDatabase = null, Batches = state.Batches.ToArray(), ParseErrors = [], Refusal = state.Refusal,
        };
    }

    sealed class ClassifyState(string text)
    {
        public readonly string Text = text;
        public readonly List<DestructiveFinding> Destructive = [];
        public readonly List<BatchInfo> Batches = [];
        public string? Refusal;
        public bool Ddl, Begins, Ends;

        public void Add(string rule, string statement, OracleToken at, string description) =>
            Destructive.Add(new DestructiveFinding { Rule = rule, Statement = statement, Line = at.Line, Column = 1, Description = description });
    }

    static string Shorten(string s) => s.Length > 200 ? s[..200] + "…" : s;

    /// <summary>A SQL*Plus <c>&amp;name</c> / <c>&amp;&amp;name</c> substitution outside strings and comments.</summary>
    static string? Substitution(List<OracleToken> code)
    {
        for (var i = 0; i + 1 < code.Count; i++)
        {
            if (!code[i].IsP("&")) continue;
            var j = i + 1;
            if (code[j].IsP("&") && code[j].Offset == code[i].End && j + 1 < code.Count) j++;
            if (code[j].Offset == code[j - 1].End && code[j].Kind is OracleTokenKind.Word or OracleTokenKind.Number)
                return $"&{code[j].Text} on line {code[i].Line} is a SQL*Plus substitution variable, which Querithm does not fill in. Use a bind variable (:{code[j].Text}) instead.";
        }
        return null;
    }

    /// <summary>The statement verb after an optional <c>WITH …</c> clause (upper case).</summary>
    public static string Verb(IReadOnlyList<OracleToken> code)
    {
        if (code.Count == 0) return "";
        if (!code[0].Is("WITH")) return code[0].Text.ToUpperInvariant();
        var depth = 0;
        for (var i = 1; i < code.Count; i++)
        {
            var t = code[i];
            if (t.IsP("(")) depth++;
            else if (t.IsP(")")) depth--;
            else if (depth == 0 && (t.Is("SELECT") || t.Is("UPDATE") || t.Is("DELETE") || t.Is("INSERT") || t.Is("MERGE"))) return t.Text.ToUpperInvariant();
        }
        return "WITH";
    }

    void ClassifySql(ClassifyState s, List<OracleToken> code, string stmtText)
    {
        if (code.Count == 0) return;
        var verb = Verb(code);
        var at = code[0];
        var session = verb == "ALTER" && code.Count > 1 && (code[1].Is("SESSION") || code[1].Is("SYSTEM"));
        if (DdlVerbs.Contains(verb) && !session) s.Ddl = true;
        switch (verb)
        {
            case "DROP":
                s.Add("drop", stmtText, at, code.Any(t => t.Is("PURGE"))
                    ? "DROP … PURGE removes the object and its data, bypassing the recycle bin: it cannot be flashed back."
                    : "DROP removes the object and its data (and commits).");
                break;
            case "TRUNCATE":
                s.Add("truncate", stmtText, at, "TRUNCATE removes every row (and commits); it cannot be rolled back.");
                break;
            case "DELETE" when !HasTopLevel(code, "DELETE", "WHERE"):
                s.Add("deleteWithoutWhere", stmtText, at, "DELETE without WHERE removes every row.");
                break;
            case "UPDATE" when !HasTopLevel(code, "UPDATE", "WHERE"):
                s.Add("updateWithoutWhere", stmtText, at, "UPDATE without WHERE changes every row.");
                break;
            case "MERGE":
                s.Add("merge", stmtText, at, "MERGE updates, inserts and may delete rows; it is always confirmed.");
                break;
            case "ALTER" when code.Count > 1 && code[1].Is("TABLE") && AlterTableDestroys(code):
                s.Add("alterDrop", stmtText, at, "ALTER TABLE … DROP / TRUNCATE / SET UNUSED removes columns, constraints, partitions or their data (and commits).");
                break;
            case "FLASHBACK":
                s.Add("flashback", stmtText, at, "FLASHBACK rewrites the table (or database) to an earlier state, or brings a dropped one back over the current name.");
                break;
            case "PURGE":
                s.Add("purge", stmtText, at, "PURGE removes dropped objects from the recycle bin for good: they cannot be flashed back.");
                break;
            case "COMMIT":
                s.Ends = true;
                break;
            case "ROLLBACK" when !code.Any(t => t.Is("TO")):
                s.Ends = true;
                break;
            case "SET" when code.Count > 1 && code[1].Is("TRANSACTION"):
                s.Begins = true;
                break;
        }
    }

    static bool AlterTableDestroys(List<OracleToken> code)
    {
        for (var i = 2; i < code.Count; i++)
        {
            if (code[i].Is("DROP") || code[i].Is("TRUNCATE")) return true;
            if (code[i].Is("SET") && i + 1 < code.Count && code[i + 1].Is("UNUSED")) return true;
        }
        return false;
    }

    /// <summary>
    /// A PL/SQL unit. Creating stored code runs none of it (DDL only). An anonymous block runs its body: static DML is
    /// classified as SQL, <c>EXECUTE IMMEDIATE 'literal'</c> as a script, and dynamic SQL that cannot be read counts as
    /// destructive.
    /// </summary>
    void ClassifyPlSql(ClassifyState s, List<OracleToken> code, string stmtText)
    {
        if (code.Count == 0) return;
        if (code[0].Is("CREATE")) { s.Ddl = true; return; }
        if (code[0].Is("WITH")) return; // WITH FUNCTION … SELECT: a query
        for (var i = 0; i < code.Count; i++)
        {
            var t = code[i];
            var prev = i > 0 ? code[i - 1] : default;
            var next = i + 1 < code.Count ? code[i + 1] : default;
            if (t.Is("EXECUTE") && next.Is("IMMEDIATE"))
            {
                if (LiteralSql(code, i + 2) is { } sql)
                {
                    var inner = new OracleLanguageService().Classify(sql);
                    s.Ddl |= inner.HasDdl;
                    foreach (var f in inner.Destructive)
                        s.Destructive.Add(f with { Statement = stmtText, Line = t.Line, Description = f.Description + " It runs as EXECUTE IMMEDIATE's dynamic SQL." });
                }
                else
                {
                    s.Ddl = true;
                    s.Add("dynamicSql", stmtText, t, "EXECUTE IMMEDIATE runs SQL text built at run time, which cannot be checked for destructive statements.");
                }
                i++;
                continue;
            }
            if (t.Is("DBMS_SQL") && next.IsP(".") || t.Is("DBMS_UTILITY") && next.IsP(".") && i + 2 < code.Count && code[i + 2].Is("EXEC_DDL_STATEMENT"))
            {
                s.Ddl = true;
                s.Add("dynamicSql", stmtText, t, $"{t.Text.ToUpperInvariant()} runs SQL text built at run time, which cannot be checked for destructive statements.");
                continue;
            }
            // Static DML: a DELETE / UPDATE / MERGE word that is not a collection method (v.DELETE) or FOR UPDATE.
            if ((t.Is("DELETE") || t.Is("UPDATE") || t.Is("MERGE")) && !prev.IsP(".") && !prev.Is("FOR") && !next.IsP(".") && !next.IsP("("))
            {
                var end = i;
                while (end < code.Count && !code[end].IsP(";")) end++;
                ClassifySql(s, code.GetRange(i, end - i), stmtText);
                i = end;
                continue;
            }
            if (t.Is("COMMIT") || (t.Is("ROLLBACK") && !(next.Is("TO") || (next.Is("WORK") && i + 2 < code.Count && code[i + 2].Is("TO"))))) s.Ends = true;
        }
    }

    /// <summary>
    /// The SQL text of <c>EXECUTE IMMEDIATE</c> when it is literal: one string, or strings joined with <c>||</c>, then the
    /// end of the call (<c>;</c>, <c>INTO</c>, <c>USING</c>, <c>RETURNING</c>, <c>BULK</c>). Null for anything else.
    /// </summary>
    static string? LiteralSql(List<OracleToken> code, int i)
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (i >= code.Count || code[i].Kind != OracleTokenKind.String) return null;
            sb.Append(OracleTokenizer.StringValue(code[i]));
            i++;
            if (i < code.Count && code[i].IsP("||")) { i++; continue; }
            break;
        }
        if (i >= code.Count || code[i].IsP(";") || code[i].Is("INTO") || code[i].Is("USING") || code[i].Is("RETURNING") || code[i].Is("RETURN") || code[i].Is("BULK"))
            return sb.ToString();
        return null;
    }

    /// <summary>The keyword at parenthesis depth 0 after the verb, with some code after it (a trailing WHERE is no condition).</summary>
    public static bool HasTopLevel(IReadOnlyList<OracleToken> code, string verb, string keyword)
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

    /// <summary>
    /// The <c>SET SERVEROUTPUT</c> commands in the text, each with the number of batches before it (D4 S04 turns
    /// DBMS_OUTPUT on or off at that point of the run).
    /// </summary>
    public static IReadOnlyList<(int BeforeBatch, bool On)> ServerOutputToggles(string text)
    {
        var list = new List<(int, bool)>();
        var batches = 0;
        foreach (var st in OracleSplitter.Split(text))
        {
            if (st.Kind != OracleStatementKind.ClientCommand) { batches++; continue; }
            if (st.Command!.ServerOutput is { } on) list.Add((batches, on));
        }
        return list;
    }

    /// <summary>
    /// The schema an <c>ALTER SESSION SET CURRENT_SCHEMA = x</c> before <paramref name="offset"/> selects, in dictionary
    /// spelling; null when none does. Completion resolves unqualified names there. It is not a database switch: the
    /// service stays the same.
    /// </summary>
    public static string? CurrentSchemaAt(string text, int offset)
    {
        string? schema = null;
        foreach (var st in OracleSplitter.Split(text))
        {
            if (st.Start >= offset) break;
            if (st.Kind != OracleStatementKind.Sql) continue;
            var c = st.Code.ToList();
            if (c.Count >= 6 && c[0].Is("ALTER") && c[1].Is("SESSION") && c[2].Is("SET") && c[3].Is("CURRENT_SCHEMA") && c[4].IsP("=")
                && c[5].Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent)
                schema = OracleIdentifiers.Identity(c[5]);
        }
        return schema;
    }

    /// <summary>
    /// The ad-hoc parameters a batch takes: its bind variables in order of first use, in dictionary spelling. Stored
    /// PL/SQL (<c>CREATE …</c>) takes none — <c>:NEW</c>, <c>:OLD</c> and <c>:PARENT</c> in a trigger are its row records,
    /// not binds.
    /// </summary>
    public static IReadOnlyList<string> Binds(string batchText)
    {
        var names = new List<string>();
        foreach (var st in OracleSplitter.Split(batchText))
        {
            if (st.Kind == OracleStatementKind.ClientCommand) continue;
            var code = st.Code.ToList();
            if (code.Count > 0 && code[0].Is("CREATE")) continue;
            foreach (var t in code)
                if (t.Kind == OracleTokenKind.Bind && OracleIdentifiers.Identity(t) is var n && !names.Contains(n)) names.Add(n);
        }
        return names;
    }

    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema) => [];
    public Diagnostic[] Diagnostics(DiagnosticsParams p) => [];
    public FormatResult Format(FormatParams p) => new() { Text = p.Text, Error = NoFormatter };
    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema) => new() { Signatures = [] };

    /// <summary>One range per statement or PL/SQL unit that spans more than one line.</summary>
    public FoldingRange[] Folding(FoldingParams p) =>
        OracleSplitter.Split(p.Text ?? "").Where(st => st.EndLine > st.StartLine)
            .Select(st => new FoldingRange { StartLine = st.StartLine, EndLine = st.EndLine }).ToArray();

    /// <summary>
    /// L4: <c>IDENTIFIED BY pw</c> (a bare word or a <c>"quoted"</c> password, Oracle's syntax), its <c>REPLACE old</c>,
    /// <c>IDENTIFIED BY VALUES 'hash'</c>, the database-link <c>CONNECT TO u IDENTIFIED BY pw</c>, and a SQL*Plus
    /// <c>CONNECT u/pw</c> line. The password token is handed to the shared redactor as a literal.
    /// </summary>
    public Redaction RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return new Redaction(text ?? "", []);
        if (!SecretRedaction.MayContainSecrets(text, SecretSyntax)) return new Redaction(text, []);
        var significant = OracleTokenizer.Tokenize(text).Where(t => !t.IsTrivia).ToList();
        var list = new List<RedactToken>(significant.Count);
        for (var i = 0; i < significant.Count; i++)
        {
            var t = significant[i];
            var password = i >= 2 && significant[i - 1].Is("BY") && significant[i - 2].Is("IDENTIFIED") && !t.Is("VALUES")
                || i >= 1 && significant[i - 1].Is("REPLACE") && significant.Take(i).Any(x => x.Is("IDENTIFIED"))
                || i >= 2 && significant[i - 1].IsP("/") && (significant[0].Is("CONNECT") || significant[0].Is("CONN"));
            if (password && t.Kind is OracleTokenKind.Word or OracleTokenKind.QuotedIdent or OracleTokenKind.Number)
            {
                var inner = t.Kind == OracleTokenKind.QuotedIdent ? t.Text : "\"" + t.Text + "\"";
                list.Add(new RedactToken(RedactTokenKind.String, t.Offset, t.Length, inner, ""));
                continue;
            }
            RedactTokenKind kind = t.Kind switch
            {
                OracleTokenKind.Word => RedactTokenKind.Word,
                OracleTokenKind.QuotedIdent => RedactTokenKind.QuotedName,
                OracleTokenKind.String => RedactTokenKind.String,
                OracleTokenKind.Number => RedactTokenKind.Number,
                OracleTokenKind.Bind => RedactTokenKind.Variable,
                OracleTokenKind.Operator or OracleTokenKind.Punct => RedactTokenKind.Punct,
                _ => RedactTokenKind.Other,
            };
            var name = kind switch
            {
                RedactTokenKind.Word or RedactTokenKind.QuotedName => OracleIdentifiers.Identity(t),
                RedactTokenKind.Variable => t.Text.TrimStart(':').Trim('"'),
                _ => "",
            };
            list.Add(new RedactToken(kind, t.Offset, t.Length, t.Text, name));
        }
        return SecretRedaction.Apply(text, list, SecretSyntax);
    }

    static readonly SecretRules SecretSyntax = new()
    {
        IdentifiedBy = true,
        Triggers = ["CONN"],
        // IDENTIFIED BY VALUES 'hash': the hash after VALUES.
        Extra = (tokens, i) => i >= 1 && tokens[i].Is("VALUES") && tokens[i - 1].Is("BY") && i + 1 < tokens.Count && tokens[i + 1].Kind == RedactTokenKind.String ? i + 1 : -1,
    };

    public void Forget(string docId) { }
}

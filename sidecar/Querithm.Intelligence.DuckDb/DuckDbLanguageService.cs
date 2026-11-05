using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Intelligence.DuckDb;

/// <summary>
/// DuckDB dialect intelligence (Phase D3), on <see cref="DuckDbTokenizer"/> tokens — never regex over query text. Tier 1
/// completion lives in the webview; tier 2 and signature help are PostgreSQL's token-scoped engine under DuckDB's rules
/// (case-insensitive names, no code in dollar strings, ';' statements, the schema the last USE / SET schema chose).
/// There is no DuckDB parser in the sidecar, so diagnostics stay empty and the engine reports syntax errors when it runs. The classifier finds destructive statements, DDL and transaction statements, and the
/// statements the file and configuration policy refuses outright: extensions, file reads and writes outside the database
/// (COPY, EXPORT / IMPORT DATABASE, ATTACH), secrets, settings, and the pragmas and functions that print to the sidecar's
/// stdout / stderr, write logs, or take raw pointers or SQL text the classifier cannot see.
/// </summary>
public sealed class DuckDbLanguageService : IDialectService
{
    static HashSet<string> Set(params string[] words) => new(words, StringComparer.OrdinalIgnoreCase);

    /// <summary>Pragmas that only read (everything else changes engine behaviour Querithm keeps fixed).</summary>
    internal static readonly HashSet<string> ReadOnlyPragmas = Set(
        "database_list", "database_size", "show_tables", "show_tables_expanded", "show_databases", "table_info", "show", "storage_info",
        "metadata_info", "collations", "functions", "version", "platform", "user_agent", "extension_versions", "force_checkpoint");

    /// <summary>
    /// Built-in functions refused anywhere in a statement: logging (a log path aborts the process), profiling (prints query
    /// text to stderr), parser switches, raw-pointer scans, and functions that run SQL text the classifier cannot see. Views
    /// and macros of an opened file are checked for the same names (DuckDbProvider).
    /// </summary>
    public static readonly HashSet<string> RefusedFunctions = Set(
        "enable_logging", "disable_logging", "truncate_duckdb_logs", "enable_profiling", "disable_profiling", "enable_peg_parser",
        "disable_peg_parser", "check_peg_parser", "arrow_scan", "arrow_scan_dumb", "json_execute_serialized_sql", "query", "query_table");

    /// <summary>The only settings a statement may change (per connection; every other setting is locked at open).</summary>
    static readonly HashSet<string> SettableOptions = Set("schema", "search_path");

    /// <summary>Words before <c>name(</c> that make it a definition or a column list, not a call (<c>CREATE TABLE query (…)</c>).</summary>
    static readonly HashSet<string> NotACall = Set("TABLE", "INTO", "VIEW", "INDEX", "ON", "REFERENCES", "MACRO", "FUNCTION", "AS", "EXISTS", "KEY", "TYPE", "SEQUENCE", "SCHEMA", "UPDATE");

    /// <summary>
    /// L4: CREATE SECRET's key material (<c>KEY_ID</c>, <c>SECRET</c>, <c>SESSION_TOKEN</c>, <c>TOKEN</c>, <c>CONNECTION_STRING</c>,
    /// <c>PASSWORD</c> …) — refused by the classifier, but the text still reaches history.
    /// </summary>
    static readonly SecretRules SecretSyntax = new()
    {
        SecretNames = Set("KEY_ID", "SESSION_TOKEN", "TOKEN", "ACCESS_TOKEN", "BEARER_TOKEN", "SAS_TOKEN", "CONNECTION_STRING", "ACCOUNT_KEY")
            .Select(s => s.ToUpperInvariant()).ToHashSet(),
        Triggers = ["KEY", "TOKEN", "CONNECTION_STRING", "ATTACH"],
        Extra = AttachConnectionString,
    };

    /// <summary>
    /// <c>ATTACH [DATABASE] [IF NOT EXISTS] 'conninfo' …</c> of a postgres / mysql database: the first literal, when it holds
    /// a credential (<see cref="SecretRedaction.HoldsCredential"/>) — a plain file path is kept.
    /// </summary>
    static int AttachConnectionString(IReadOnlyList<RedactToken> t, int i)
    {
        if (!t[i].Is("ATTACH")) return -1;
        var j = i + 1;
        while (j < t.Count && (t[j].Is("DATABASE") || t[j].Is("IF") || t[j].Is("NOT") || t[j].Is("EXISTS"))) j++;
        return j < t.Count && t[j].Kind == RedactTokenKind.String && SecretRedaction.HoldsCredential(t[j].Text) ? j : -1;
    }

    /// <summary>M12: the PostgreSQL-family caches this engine's completion fills (documents, statement splits, USE / SET scans).</summary>
    public void Forget(string docId) => DocumentCaches.Forget(docId);

    public Redaction RedactSecrets(string text) =>
        SecretRedaction.MayContainSecrets(text, SecretSyntax)
            ? SecretRedaction.Apply(text, PgRedaction.Tokens(DuckDbTokenizer.Tokenize(text)), SecretSyntax)
            : new Redaction(text, []);

    public Classification Classify(string text)
    {
        var tokens = DuckDbTokenizer.Tokenize(text);
        var findings = new List<DestructiveFinding>();
        var batches = new List<BatchInfo>();
        // PT7-4: DuckDB reads the SQL text as a C string, so it stops at a NUL and runs only what comes before it
        // ("DELETE FROM t\0 WHERE 0" deletes every row) — refused, whatever the user confirms.
        string? refusal = SqlChars.NulRefusal(text, "DuckDB");
        bool ddl = false, begins = false, ends = false;
        var prepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in DuckDbTokenizer.Split(tokens))
        {
            var code = st.Where(t => !t.IsTrivia).ToList();
            if (code.Count == 0 || (code.Count == 1 && code[0].Text == ";")) continue;
            var first = code[0];
            var last = code[^1];
            var length = last.Offset + last.Length - first.Offset;
            batches.Add(new BatchInfo
            {
                Index = batches.Count, StartLine = first.Line, EndLine = last.Line, StartOffset = first.Offset, Length = length,
            });
            var excerpt = Excerpt(text, first, last);
            refusal ??= Refused(code);
            Statement(code, excerpt, "", findings, prepared, ref ddl, ref begins, ref ends);
            // PT7-1: DuckDB maps Unicode spaces to ASCII spaces in each command text it is given (DuckDbTokenizer), and each
            // batch runs as a command of its own, so the batch alone can read differently from the whole text (an open quote
            // before it, in DuckDB's view, stops the mapping). Classify the batch as DuckDB reads it too; a finding from
            // either reading counts.
            var batchText = text.Substring(first.Offset, length);
            var seen = findings.Select(f => (f.Rule, f.Line)).ToHashSet();
            var alone = new List<DestructiveFinding>();
            foreach (var inner in DuckDbTokenizer.Split(DuckDbTokenizer.Tokenize(batchText)))
            {
                var innerCode = inner.Where(t => !t.IsTrivia)
                    .Select(t => t with { Offset = t.Offset + first.Offset, Line = t.Line + first.Line - 1 }).ToList();
                if (innerCode.Count == 0 || (innerCode.Count == 1 && innerCode[0].Text == ";")) continue;
                refusal ??= Refused(innerCode);
                Statement(innerCode, excerpt, "", alone, prepared, ref ddl, ref begins, ref ends);
            }
            findings.AddRange(alone.Where(f => seen.Add((f.Rule, f.Line))));
        }
        return new Classification
        {
            Destructive = findings.ToArray(), HasDdl = ddl, BeginsTransaction = begins, EndsTransaction = ends, Batches = batches.ToArray(),
            ParseErrors = [], Refusal = refusal,
        };
    }

    /// <summary>
    /// One statement's findings. <c>PREPARE name AS statement</c> classifies the statement it stores (it runs on EXECUTE);
    /// <c>EXECUTE</c> of a statement not prepared in this text cannot be checked and counts as destructive (<c>dynamicSql</c>).
    /// </summary>
    static void Statement(List<PgToken> code, string excerpt, string context, List<DestructiveFinding> findings, HashSet<string> prepared, ref bool ddl, ref bool begins, ref bool ends)
    {
        var first = code[0];
        void Add(string rule, string description) =>
            findings.Add(new DestructiveFinding { Rule = rule, Statement = excerpt, Line = first.Line, Column = 1, Description = description + context });
        if (first.Is("EXPLAIN"))
        {
            if (ExplainTarget(code) is { Count: > 0 } target) Statement(target, excerpt, " EXPLAIN ANALYZE runs the statement.", findings, prepared, ref ddl, ref begins, ref ends);
            return;
        }
        if (first.Is("PREPARE") && code.Count > 1)
        {
            if (PreparedBody(code) is { Count: > 0 } body) Statement(body, excerpt, " It runs when the prepared statement is executed.", findings, prepared, ref ddl, ref begins, ref ends);
            prepared.Add(DuckDbTokenizer.Unquote(code[1]));
            return;
        }
        if (first.Is("EXECUTE"))
        {
            if (code.Count < 2 || !prepared.Contains(DuckDbTokenizer.Unquote(code[1])))
                Add("dynamicSql", "EXECUTE runs a prepared statement that is not prepared in this text, so it cannot be checked for destructive statements.");
            return;
        }
        var verbAt = VerbIndex(code);
        var verb = code[verbAt];
        if (verb.Is("MERGE"))
        {
            if (ThenDelete(code, verbAt)) Add("mergeDelete", "MERGE … THEN DELETE removes every row its WHEN clause matches.");
        }
        else if (verb.Is("DELETE") && !HasTopLevel(code, verbAt, "WHERE", needsOperand: true)) Add("deleteWithoutWhere", "DELETE without a WHERE clause removes every row.");
        else if (verb.Is("UPDATE") && !HasTopLevel(code, verbAt, "WHERE", needsOperand: true)) Add("updateWithoutWhere", "UPDATE without a WHERE clause changes every row.");
        else if (first.Is("DROP")) { ddl = true; Add("drop", $"DROP {Word(code, 1)} permanently removes the object."); }
        else if (first.Is("TRUNCATE")) Add("truncate", "TRUNCATE removes every row.");
        else if (first.Is("ALTER")) { ddl = true; Add("alter", $"ALTER {Word(code, 1)} changes an existing object."); }
        else if (first.Is("CREATE"))
        {
            ddl = true;
            if (code.Count > 2 && code[1].Is("OR") && code[2].Is("REPLACE")) Add("alter", "CREATE OR REPLACE replaces an existing object (a table's rows with it).");
        }
        else if (first.Is("COMMENT")) ddl = true;
        else if (first.Is("BEGIN") || (first.Is("START") && code.Count > 1 && code[1].Is("TRANSACTION"))) begins = true;
        else if (first.Is("COMMIT") || first.Is("END") || first.Is("ABORT") || first.Is("ROLLBACK")) ends = true;
    }

    /// <summary>The statement's verb: the first statement keyword after an optional WITH … clause (WITH t AS (…) DELETE …).</summary>
    static int VerbIndex(List<PgToken> code)
    {
        if (!code[0].Is("WITH")) return 0;
        var depth = 0;
        for (var i = 1; i < code.Count; i++)
        {
            var t = code[i];
            if (t.Text == "(") depth++;
            else if (t.Text == ")") depth--;
            else if (depth == 0 && (t.Is("SELECT") || t.Is("INSERT") || t.Is("UPDATE") || t.Is("DELETE") || t.Is("MERGE") || t.Is("FROM") || t.Is("VALUES"))) return i;
        }
        return 0;
    }

    /// <summary>The statement after <c>PREPARE name [(types)] AS</c>, or null.</summary>
    static List<PgToken>? PreparedBody(List<PgToken> code)
    {
        var depth = 0;
        for (var i = 2; i < code.Count; i++)
        {
            if (code[i].Text == "(") depth++;
            else if (code[i].Text == ")") depth--;
            else if (depth == 0 && code[i].Is("AS")) return i + 1 < code.Count ? code.GetRange(i + 1, code.Count - i - 1) : null;
        }
        return null;
    }

    /// <summary>A WHEN … THEN DELETE action at depth 0 of a MERGE.</summary>
    static bool ThenDelete(List<PgToken> code, int from)
    {
        var depth = 0;
        for (var i = from + 1; i + 1 < code.Count; i++)
        {
            if (code[i].Text == "(") depth++;
            else if (code[i].Text == ")") depth--;
            else if (depth == 0 && code[i].Is("THEN") && code[i + 1].Is("DELETE")) return true;
        }
        return false;
    }

    /// <summary>
    /// The keyword at depth 0 after <paramref name="from"/>. With <paramref name="needsOperand"/> (WHERE), only a keyword some
    /// code follows counts (PT7-1 safety net): a WHERE that ends the statement is no condition.
    /// </summary>
    static bool HasTopLevel(List<PgToken> code, int from, string keyword, bool needsOperand = false)
    {
        var depth = 0;
        for (var i = from + 1; i < code.Count; i++)
        {
            if (code[i].Text == "(") depth++;
            else if (code[i].Text == ")") depth--;
            else if (depth == 0 && code[i].Is(keyword) && (!needsOperand || (i + 1 < code.Count && code[i + 1].Text != ";"))) return true;
        }
        return false;
    }

    /// <summary>
    /// The statement EXPLAIN executes, or null when it only plans it: <c>EXPLAIN ANALYZE stmt</c> and
    /// <c>EXPLAIN (ANALYZE[, …]) stmt</c> run it.
    /// </summary>
    static List<PgToken>? ExplainTarget(List<PgToken> code)
    {
        var i = 1;
        var analyze = false;
        if (i < code.Count && code[i].Is("ANALYZE")) { analyze = true; i++; }
        if (i < code.Count && code[i].Text == "(")
        {
            var depth = 0;
            for (; i < code.Count; i++)
            {
                if (code[i].Text == "(") depth++;
                else if (code[i].Text == ")" && --depth == 0) { i++; break; }
                else if (code[i].Is("ANALYZE") && code[i - 1].Text is "(" or ",") analyze = !(i + 1 < code.Count && (code[i + 1].Is("FALSE") || code[i + 1].Text == "0"));
            }
        }
        if (!analyze || i >= code.Count) return null;
        return code.GetRange(i, code.Count - i);
    }

    /// <summary>The file and configuration policy (Phase D3): what no grant allows, whatever the user confirms.</summary>
    internal static string? Refused(List<PgToken> code)
    {
        var first = code[0];
        var at = 0;
        if (first.Is("EXPLAIN") && ExplainTarget(code) is { Count: > 0 } target) return Refused(target);
        // A prepared statement runs on EXECUTE: what it stores is refused here, where the classifier can see it.
        if (first.Is("PREPARE") && PreparedBody(code) is { Count: > 0 } body) return Refused(body);
        if (first.Is("WITH")) at = VerbIndex(code);
        var verb = code[at];
        if (verb.Is("INSTALL") || verb.Is("LOAD") || (verb.Is("FORCE") && code.Count > at + 1 && code[at + 1].Is("INSTALL"))
            || (verb.Is("UPDATE") && code.Count > at + 1 && code[at + 1].Is("EXTENSIONS") && (code.Count == at + 2 || code[at + 2].Text is "(" or ";")))
            return "DuckDB extensions are not available: they are native code that runs inside Querithm. The extensions built into Querithm's DuckDB are already loaded.";
        if (verb.Is("COPY"))
            return HasTopLevel(code, at, "TO")
                ? "COPY … TO writes a file outside the database, which no grant covers. Export the results from the grid instead."
                : "COPY … FROM reads a file outside the database, which no grant covers. Use Import to load a CSV, JSON or Excel file.";
        if (verb.Is("EXPORT") || verb.Is("IMPORT"))
            return "EXPORT / IMPORT DATABASE read or write files outside the database, which no grant covers.";
        if (verb.Is("ATTACH") || verb.Is("DETACH"))
            return "ATTACH and DETACH are not available: Querithm opens only the database you picked (another file needs a connection of its own).";
        if (IsSecretStatement(code, at))
            return "DuckDB secrets are not available: a secret would sit in the SQL text, the history and on disk, outside the OS keyring.";
        if ((verb.Is("SET") || verb.Is("RESET")) && SettingName(code, at) is { } option && !SettableOptions.Contains(option))
            return $"The DuckDB setting {option} is fixed by Querithm for this connection (file access, extensions, memory and output): only schema and search_path can change.";
        if (verb.Is("PRAGMA") && code.Count > at + 1)
        {
            var name = DuckDbTokenizer.Unquote(code[at + 1]);
            var assigns = code.Count > at + 2 && code[at + 2].Text == "=";
            if (assigns || !ReadOnlyPragmas.Contains(name))
                return $"PRAGMA {name} is not available: it changes engine behaviour Querithm keeps fixed (settings, profiling and progress output, verification, logging).";
        }
        return CallRefusal(code) is { } fn
            ? $"{fn}() is not available in Querithm: it writes logs or prints to the engine's output, reads raw memory, or runs SQL text that cannot be checked first."
            : null;
    }

    /// <summary>The first refused function the tokens call (<c>name(</c>, qualified or quoted), skipping definitions and column lists.</summary>
    static string? CallRefusal(List<PgToken> code)
    {
        for (var i = 1; i < code.Count; i++)
        {
            if (code[i].Text != "(" || code[i - 1].Kind is not (PgTokenKind.Word or PgTokenKind.QuotedIdent)) continue;
            var name = DuckDbTokenizer.Unquote(code[i - 1]);
            if (!RefusedFunctions.Contains(name)) continue;
            var before = i >= 2 ? code[i - 2] : default;
            if (before.Text == ".") before = i >= 4 ? code[i - 4] : default;
            if (before.Kind == PgTokenKind.Word && before.Text != null && NotACall.Contains(before.Text)) continue;
            return name;
        }
        return null;
    }

    /// <summary>
    /// The first refused function a stored definition calls (a view's SQL, a macro's body), or null. An opened file is
    /// untrusted: a view would run the function when it is read, where the classifier never sees it.
    /// </summary>
    public static string? RefusedCall(string definition) => CallRefusal(DuckDbTokenizer.Tokenize(definition).Where(t => !t.IsTrivia).ToList());

    /// <summary>CREATE [OR REPLACE] [PERSISTENT | TEMPORARY | TEMP] SECRET … and DROP [PERSISTENT | TEMPORARY | TEMP] SECRET ….</summary>
    static bool IsSecretStatement(List<PgToken> code, int at)
    {
        if (!code[at].Is("CREATE") && !code[at].Is("DROP")) return false;
        var i = at + 1;
        if (code.Count > i + 1 && code[i].Is("OR") && code[i + 1].Is("REPLACE")) i += 2;
        if (code.Count > i && (code[i].Is("PERSISTENT") || code[i].Is("TEMPORARY") || code[i].Is("TEMP"))) i++;
        return code.Count > i && code[i].Is("SECRET");
    }

    /// <summary>The option a SET / RESET names: <c>SET [GLOBAL | SESSION | LOCAL] name …</c>; null for <c>SET VARIABLE</c>.</summary>
    static string? SettingName(List<PgToken> code, int at)
    {
        var i = at + 1;
        if (code.Count > i && (code[i].Is("GLOBAL") || code[i].Is("SESSION") || code[i].Is("LOCAL"))) i++;
        if (code.Count <= i || code[i].Is("VARIABLE")) return null;
        return DuckDbTokenizer.Unquote(code[i]);
    }

    static string Word(List<PgToken> code, int i) => i < code.Count ? code[i].Text.ToUpperInvariant() : "";

    static string Excerpt(string text, PgToken first, PgToken last)
    {
        var s = text[first.Offset..(last.Offset + last.Length)];
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    /// <summary>DuckDB 1.5.6's reserved and type/function-name keywords (duckdb_keywords()): never a bare name in completion inserts.</summary>
    internal static readonly HashSet<string> ReservedWords = Set((
        "ALL ANALYSE ANALYZE AND ANY ARRAY AS ASC ASYMMETRIC BOTH CASE CAST CHECK COLLATE COLUMN CONSTRAINT CREATE DEFAULT DEFERRABLE DESC " +
        "DESCRIBE DISTINCT DO ELSE END EXCEPT FALSE FETCH FOR FOREIGN FROM GROUP HAVING IN INITIALLY INTERSECT INTO LAMBDA LATERAL LEADING " +
        "LIMIT NOT NULL OFFSET ON ONLY OR ORDER PIVOT PIVOT_LONGER PIVOT_WIDER PLACING PRIMARY QUALIFY REFERENCES RETURNING SELECT SHOW SOME " +
        "SUMMARIZE SYMMETRIC TABLE THEN TO TRAILING TRUE UNION UNIQUE UNPIVOT USING VARIADIC WHEN WHERE WINDOW WITH ANTI ASOF AT " +
        "AUTHORIZATION BINARY BY COLLATION COLUMNS CONCURRENTLY CROSS FREEZE FULL GENERATED GLOB ILIKE INNER IS ISNULL JOIN LEFT LIKE MAP " +
        "NATURAL NOTNULL OUTER OVERLAPS POSITIONAL RIGHT SEMI SIMILAR STRUCT TABLESAMPLE TRY_CAST UNPACK VERBOSE").Split(' '));

    static bool Plain(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$') && !ReservedWords.Contains(name);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PgCompletionRules> RulesBySchema = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tier-2 rules with <paramref name="schema"/> as the default (one instance per schema: each caches its snapshot index).</summary>
    internal static PgCompletionRules RulesFor(string schema) => RulesBySchema.GetOrAdd(schema, s => new PgCompletionRules
    {
        Id = "duckdb:" + s.ToLowerInvariant(), Names = StringComparer.OrdinalIgnoreCase, FoldsUnquoted = false, DefaultSchema = s,
        IsReserved = ReservedWords.Contains, IsPlain = Plain, DollarBodiesAreCode = false, Tokenize = DuckDbTokenizer.Tokenize, Statements = (_, tokens) => DuckDbTokenizer.Split(tokens),
        BuiltinTypes =
        [
            "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "HUGEINT", "UINTEGER", "UBIGINT", "DECIMAL", "DOUBLE", "FLOAT", "BOOLEAN", "VARCHAR", "BLOB",
            "DATE", "TIME", "TIMESTAMP", "TIMESTAMPTZ", "INTERVAL", "UUID", "JSON", "BIT", "STRUCT", "MAP", "UNION", "ENUM",
        ],
    });

    static string SchemaAt(List<(int End, string Schema)> switches, int offset)
    {
        var schema = "main";
        foreach (var (end, s) in switches)
        {
            if (end > offset) break;
            schema = s;
        }
        return schema;
    }

    /// <summary>Each USE / SET schema statement of the text: where it ends and the schema unqualified names resolve to after it.</summary>
    static List<(int End, string Schema)> SchemaSwitches(string text, string? database)
    {
        var switches = new List<(int End, string Schema)>();
        foreach (var st in DuckDbTokenizer.Split(DuckDbTokenizer.Tokenize(text)))
        {
            var code = st.Where(t => !t.IsTrivia && t.Text != ";").ToList();
            if (code.Count < 2) continue;
            string? schema = null;
            if (code[0].Is("USE"))
            {
                var parts = new List<string>();
                for (var i = 1; i < code.Count; i += 2)
                {
                    if (code[i].Kind is not (PgTokenKind.Word or PgTokenKind.QuotedIdent)) break;
                    parts.Add(DuckDbTokenizer.Unquote(code[i]));
                    if (i + 1 >= code.Count || code[i + 1].Text != ".") break;
                }
                if (parts.Count >= 2) schema = parts[^1];
                else if (parts.Count == 1)
                    schema = parts[0] is var p && (string.Equals(p, database, StringComparison.OrdinalIgnoreCase) || p.Equals("memory", StringComparison.OrdinalIgnoreCase)
                                                    || p.Equals("temp", StringComparison.OrdinalIgnoreCase) || p.Equals("system", StringComparison.OrdinalIgnoreCase)) ? "main" : p;
            }
            else if (code[0].Is("SET") && SettingName(code, 0) is { } option && (option.Equals("schema", StringComparison.OrdinalIgnoreCase) || option.Equals("search_path", StringComparison.OrdinalIgnoreCase)))
            {
                var value = code.LastOrDefault(t => t.Kind is PgTokenKind.String or PgTokenKind.Word or PgTokenKind.QuotedIdent);
                var name = value.Kind == PgTokenKind.String ? DuckDbTokenizer.StringValue(value) : DuckDbTokenizer.Unquote(value);
                // search_path is a comma-separated list: its first entry (catalog.schema or schema) is where names resolve first.
                var first = name?.Split(',')[0].Trim();
                if (!string.IsNullOrEmpty(first)) schema = first.Contains('.') ? first[(first.LastIndexOf('.') + 1)..] : first;
            }
            if (schema != null) switches.Add((code[^1].Offset + code[^1].Length, schema));
        }
        return switches;
    }

    /// <summary>
    /// The schema unqualified names resolve to at <paramref name="offset"/>: the last <c>USE [catalog.]schema</c> or
    /// <c>SET schema / search_path = 'x'</c> before it, else main. A bare USE of a catalog (the session's database,
    /// memory, temp, system) leaves the schema at main.
    /// The text's switches are found once per document (<see cref="DocumentScan{T}"/>).
    /// </summary>
    static string SchemaFor(string? docId, string? text, int offset, string? database)
    {
        text ??= "";
        var switches = DocumentScan<List<(int End, string Schema)>>.Get($"duckdb|{database}|{docId}", text, t => SchemaSwitches(t, database));
        return SchemaAt(switches, Math.Clamp(offset, 0, text.Length));
    }

    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema)
    {
        using var _ = PgCompletionRules.Use(RulesFor(SchemaFor(p.DocId, p.Text, p.Offset, p.Database)));
        return PgTier2Completion.Complete(p, schema);
    }

    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema)
    {
        using var _ = PgCompletionRules.Use(RulesFor(SchemaFor(p.DocId, p.Text, p.Offset, p.Database)));
        return PgTier2Completion.Signature(p, schema);
    }

    public Diagnostic[] Diagnostics(DiagnosticsParams p) => [];
    public FormatResult Format(FormatParams p) => new() { Text = p.Text };

    /// <summary>One region per multi-line statement.</summary>
    public FoldingRange[] Folding(FoldingParams p) =>
        DuckDbTokenizer.Split(DuckDbTokenizer.Tokenize(p.Text))
            .Select(st => st.Where(t => !t.IsTrivia).ToList()).Where(c => c.Count > 0 && c[^1].Line > c[0].Line)
            .Select(c => new FoldingRange { StartLine = c[0].Line, EndLine = c[^1].Line }).ToArray();
}

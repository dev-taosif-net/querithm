using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;
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

    /// <summary>The four ways the server can read quotes and backslashes (sql_mode ANSI_QUOTES × NO_BACKSLASH_ESCAPES).</summary>
    public static readonly IReadOnlyList<MySqlLexMode> AllModes =
        [MySqlLexMode.Default, new(NoBackslashEscapes: true), new(AnsiQuotes: true), new(AnsiQuotes: true, NoBackslashEscapes: true)];

    /// <summary>The text holds a character sql_mode reads differently (<c>\</c> or <c>"</c>).</summary>
    static bool SqlModeMatters(string text) => text.IndexOf('\\') >= 0 || text.IndexOf('"') >= 0;

    /// <summary>
    /// The text reads the same in every reading: it holds no <c>\</c> and no <c>"</c>, the only characters ANSI_QUOTES and
    /// NO_BACKSLASH_ESCAPES read differently, and no version-gated executable comment (a server may skip it).
    /// </summary>
    public static bool ReadsTheSameInEveryMode(string text) => text.IndexOf('\\') < 0 && text.IndexOf('"') < 0 && !MySqlTokenizer.HasGatedComment(text);

    /// <summary>
    /// The readings that can differ from <paramref name="mode"/>'s on this text, <paramref name="mode"/> first: only a
    /// backslash makes NO_BACKSLASH_ESCAPES matter and only a double quote ANSI_QUOTES, so a text with one of the two is
    /// read two ways, not four (code audit 2026-11-25, L5), and a text with neither only one.
    /// </summary>
    /// <param name="everyVersion">
    /// Read every version the text's gated comments name, also when none of them changes the split (plans; the classifier
    /// reads those per statement instead, see <see cref="StatementGateReadings"/>).
    /// </param>
    public static IEnumerable<MySqlLexMode> ModesToRead(string text, MySqlLexMode mode, bool everyVersion = false)
    {
        bool backslash = text.IndexOf('\\') >= 0, doubleQuote = text.IndexOf('"') >= 0;
        // Code audit 2026-11-25, second round: a server older than /*!NNNNN's version skips that comment, and MySQL skips
        // MariaDB's /*M!, so `SELECT 1 /*!99999 ' */; DROP TABLE x; -- '` is one SELECT read as code and a DROP read as a
        // comment. Each reading is also read with such comments as comments (a uniform reading of a mixed server, as above).
        var gated = MySqlTokenizer.HasGatedComment(text);
        var bases = new List<MySqlLexMode> { mode with { CodeUpTo = int.MaxValue, MariaDb = false } };
        foreach (var m in AllModes)
            if (!(m.AnsiQuotes == mode.AnsiQuotes && m.NoBackslashEscapes == mode.NoBackslashEscapes)
                && (backslash || m.NoBackslashEscapes == mode.NoBackslashEscapes) && (doubleQuote || m.AnsiQuotes == mode.AnsiQuotes))
                bases.Add(m);
        yield return mode;
        foreach (var m in bases)
        {
            if (m != mode) yield return m;
            if (!gated) continue;
            foreach (var gate in GateReadings(text, m, everyVersion || MySqlTokenizer.SplitChangingGates(text, m).Count > 0))
                if (gate != mode) yield return gate;
        }
    }

    /// <summary>At most this many server versions are read per text; past it the text is a finding (<see cref="TooManyGates"/>).</summary>
    public const int MaxGateVersions = 16;

    /// <summary>
    /// The server readings of a text with gated comments, on top of <paramref name="m"/>: MySQL and MariaDB skipping every
    /// gated comment, and — when <paramref name="perVersion"/> — a MySQL and a MariaDB server of each version the text names.
    /// Third and fourth round: a real server runs some gated comments and skips others (MySQL 8.0 runs /*!80000 and skips
    /// /*!99999; MariaDB skips /*!50700–99999), so `/*!99999 EXPLAIN */ /*!80000 DROP TABLE users */` is a DROP on MySQL 8.
    /// The versions are found by a character scan, so a gate that another gate's string hides in one reading is counted.
    /// </summary>
    static IEnumerable<MySqlLexMode> GateReadings(string text, MySqlLexMode m, bool perVersion)
    {
        yield return m with { CodeUpTo = 0, MariaDb = false };
        yield return m with { CodeUpTo = 0, MariaDb = true };
        if (!perVersion) yield break;
        var (mysql, maria) = MySqlTokenizer.GateVersions(text);
        foreach (var v in mysql.Take(MaxGateVersions)) yield return m with { CodeUpTo = v, MariaDb = false };
        foreach (var v in maria.Take(MaxGateVersions)) yield return m with { CodeUpTo = v, MariaDb = true };
    }

    /// <summary>More gate versions than the readings cover (in any mode: the scan reads characters, not one reading's tokens).</summary>
    public static bool TooManyGates(string text)
    {
        if (!MySqlTokenizer.HasGatedComment(text)) return false;
        var (mysql, maria) = MySqlTokenizer.GateVersions(text);
        if (mysql.Count <= MaxGateVersions && maria.Count <= MaxGateVersions) return false;
        // Only where the text is read per version as a whole: some reading's gate changes the split (mysqldump's do not).
        return AllModes.Any(m => MySqlTokenizer.SplitChangingGates(text, m).Count > 0);
    }

    /// <summary>
    /// The statements of <paramref name="text"/> in reading <paramref name="m"/> that hold gated comments, each with the
    /// gate readings of its own text: one per version it names. With no gated comment that changes the split, every
    /// reading splits the text the same way, so mixing run and skipped gates can only change a statement from within —
    /// and a statement is read once per version it holds, not the whole text once per version in it.
    /// </summary>
    static IEnumerable<(int Start, int End, IReadOnlyList<MySqlLexMode> Readings)> StatementGateReadings(string text, MySqlLexMode m)
    {
        foreach (var st in MySqlSplitter.Split(text, m))
        {
            if (st.Tokens.Count == 0) continue;
            var start = st.Tokens[0].Offset;
            var end = st.Tokens[^1].Offset + st.Tokens[^1].Length;
            var part = text[start..end];
            if (!MySqlTokenizer.HasGatedComment(part)) continue;
            yield return (start, end, [.. GateReadings(part, m, perVersion: true)]);
        }
    }

    /// <summary>
    /// Completion audit 2026-11-25, S1: the session's sql_mode decides whether <c>\</c> escapes a quote and whether
    /// <c>"…"</c> is a string or a name, and the classifier does not know it — the session may have set it, or the server's
    /// default may. Read with backslash escapes, <c>SELECT 'a\'; DROP TABLE t; -- '</c> is one SELECT; a server with
    /// NO_BACKSLASH_ESCAPES runs a DROP, which the production guard never saw. So a text holding <c>\</c> or <c>"</c> (the
    /// only characters these modes read differently) is also read the other three ways, and every destructive statement
    /// any reading finds counts (refusals, DDL and transaction statements too). Batches and USE stay the configured
    /// reading's. Each reading is uniform, the server's is not: after a SET of sql_mode it reads the rest the new way, and
    /// a statement before the SET can shift every uniform reading's quotes so that none sees what follows. Such a SET (and
    /// a change of the client character set) is therefore a finding of its own, <c>readingChanged</c>.
    /// </summary>
    public Classification Classify(string text) => ClassifyAll(text, _mode, 0, false);

    /// <summary>How deep PREPARE / EXECUTE IMMEDIATE literals are read inside each other.</summary>
    internal const int MaxDynamicDepth = 32;

    /// <summary>
    /// Every reading of <paramref name="text"/>, <paramref name="primaryMode"/> first. Fourth round (C3): the text a PREPARE
    /// or EXECUTE IMMEDIATE literal holds is read this way too, not in its statement's reading only — its unescaped
    /// <c>/\*!99999 WHERE 1 */</c> is a gated comment the outer text never showed.
    /// </summary>
    static Classification ClassifyAll(string text, MySqlLexMode primaryMode, int depth, bool flagReadingChanges)
    {
        var lineAt = LineIndex(text);
        var mayMisread = !ReadsTheSameInEveryMode(text);
        // A SET of sql_mode matters only where \ or " is (a gated comment alone adds readings, not this finding: every
        // mysqldump file sets SQL_MODE inside /*!40101 … */).
        var modeMatters = flagReadingChanges || SqlModeMatters(text);
        var primary = ClassifyIn(text, primaryMode, lineAt, modeMatters, depth);
        if (!mayMisread) return primary;
        var findings = primary.Destructive.ToList();
        var seen = new HashSet<(string, int, string)>(findings.Select(Key));
        var refusal = primary.Refusal;
        var ddl = primary.HasDdl;
        bool begins = primary.BeginsTransaction, ends = primary.EndsTransaction;
        void Merge(Classification other, MySqlLexMode mode)
        {
            refusal ??= other.Refusal;
            ddl |= other.HasDdl;
            // A COMMIT or BEGIN only another reading sees still steps outside the transaction an actual plan rolls back (M7).
            begins |= other.BeginsTransaction;
            ends |= other.EndsTransaction;
            foreach (var f in other.Destructive)
                if (seen.Add(Key(f)))
                    findings.Add(f with { Description = f.Description + OtherReading(mode) });
        }
        var readings = ModesToRead(text, primaryMode).ToList();
        foreach (var mode in readings)
        {
            if (mode == primaryMode) continue;
            Merge(ClassifyIn(text, mode, lineAt, modeMatters, depth), mode);
        }
        // Gates that change no split: each statement that holds some is read once per version it names (C1).
        if (MySqlTokenizer.HasGatedComment(text))
            foreach (var m in readings.Where(r => r.AllCode))
            {
                if (MySqlTokenizer.SplitChangingGates(text, m).Count > 0) continue; // read per version above
                foreach (var (start, end, gates) in StatementGateReadings(text, m))
                {
                    var part = text[start..end];
                    int PartLine(int offset) => lineAt(start + offset);
                    foreach (var gate in gates) Merge(ClassifyIn(part, gate, PartLine, modeMatters, depth), gate);
                    var (mysql, maria) = MySqlTokenizer.GateVersions(part);
                    if ((mysql.Count > MaxGateVersions || maria.Count > MaxGateVersions) && seen.Add(("unparsed", lineAt(start), "")))
                        findings.Add(new DestructiveFinding
                        {
                            Rule = "unparsed", Statement = "", Line = lineAt(start), Column = 1,
                            Description = $"A statement holds version-gated comments of more than {MaxGateVersions} versions; Querithm read only the first, so it could not be checked for destructive statements.",
                        });
                }
            }
        if (TooManyGates(text))
            findings.Add(new DestructiveFinding
            {
                Rule = "unparsed", Statement = "", Line = 1, Column = 1,
                Description = $"The text holds version-gated comments (/*!NNNNN … */) of more than {MaxGateVersions} versions; Querithm read only the first, so it could not be checked for destructive statements.",
            });
        return primary with { Destructive = findings.ToArray(), Refusal = refusal, HasDdl = ddl, BeginsTransaction = begins, EndsTransaction = ends };
    }

    static (string, int, string) Key(DestructiveFinding f) => (f.Rule, f.Line, f.Statement);

    /// <summary>
    /// The statement changes how the server reads the statements after it: a SET of <c>sql_mode</c> (any scope, <c>@@…</c>,
    /// <c>:=</c>, MariaDB's <c>SET STATEMENT … FOR</c>) or of the client character set (<c>SET NAMES</c>, <c>SET CHARACTER
    /// SET</c> / <c>CHARSET</c>, <c>character_set_client</c>). A name is a target when '=' or ':=' follows it, so
    /// <c>SET @x = @@sql_mode</c> (a read) is none. Tokens, never text.
    /// </summary>
    /// <returns>
    /// Whether it changes the reading, and whether that change matters in any text: a client character set whose
    /// multibyte characters can end in an ASCII byte (gbk, big5, sjis, cp932, gb18030 — anything not known to be safe,
    /// DEFAULT and a variable included) makes the server take a backtick or quote after such a character as part of it,
    /// with no backslash or double quote in the text (code audit 2026-11-25, H1).
    /// </returns>
    static (bool Changes, bool Always, string? Unsafe) ChangesReading(List<MySqlToken> code, MySqlLexMode mode)
    {
        bool set = false, changes = false, always = false;
        string? unsafeName = null;
        void Charset(int value)
        {
            changes = true;
            // Fourth round (H4): a variable (mysqldump's closing SET CHARACTER_SET_CLIENT = @OLD_…: what the session had)
            // and DEFAULT stay a finding; a name in any quoting is checked; a number (a collation id) or an expression is
            // refused like an unsafe name — it may be gbk.
            if (value >= code.Count) { always = true; return; }
            var v = code[value];
            if (v.Kind == MySqlTokenKind.Variable || v.Is("DEFAULT")) { always = true; return; }
            string? name = v.Kind switch
            {
                MySqlTokenKind.Word => v.Text,
                MySqlTokenKind.String => StringValue(v.Text, mode),
                MySqlTokenKind.QuotedIdent => MySqlTokenizer.Unquote(v),
                _ => null,
            };
            if (name != null && SafeCharsets.Contains(name)) return;
            always = true;
            unsafeName ??= name ?? v.Text;
        }
        for (var i = 0; i < code.Count; i++)
        {
            var t = code[i];
            if (t.Is("SET"))
            {
                set = true;
                if (i + 1 < code.Count && (code[i + 1].Is("NAMES") || code[i + 1].Is("CHARSET"))) Charset(i + 2);
                else if (i + 2 < code.Count && code[i + 1].Is("CHARACTER") && code[i + 2].Is("SET")) Charset(i + 3);
                continue;
            }
            // An operator run is one token (`=-1` too): an assignment starts with '=' or ':='.
            if (!set || i + 1 >= code.Count || code[i + 1] is not { Kind: MySqlTokenKind.Operator } op || !(op.Text.StartsWith('=') || op.Text.StartsWith(":="))) continue;
            var name = t.Kind switch
            {
                MySqlTokenKind.Word => t.Text,
                MySqlTokenKind.QuotedIdent => MySqlTokenizer.Unquote(t),
                MySqlTokenKind.Variable => t.Text.TrimStart('@').Split('.')[^1],
                _ => null,
            };
            if (name is null) continue;
            if (name.Equals("sql_mode", StringComparison.OrdinalIgnoreCase)) changes = true;
            // "=-x" is one operator token: the value follows it only when the operator is exactly the assignment.
            else if (name.Equals("character_set_client", StringComparison.OrdinalIgnoreCase)) Charset(op.Text is "=" or ":=" ? i + 2 : code.Count);
        }
        return (changes, always, unsafeName);
    }

    /// <summary>
    /// Client character sets in which every byte of a multibyte character is 0x80 or above (UTF-8, the EUC sets) or every
    /// character is one byte: the server reads the ASCII quotes, backticks and backslashes of the text as the tokenizer does.
    /// </summary>
    static readonly HashSet<string> SafeCharsets = new(StringComparer.OrdinalIgnoreCase)
    {
        "utf8", "utf8mb3", "utf8mb4", "ascii", "binary", "latin1", "latin2", "latin5", "latin7", "cp1250", "cp1251", "cp1256",
        "cp1257", "cp850", "cp852", "cp866", "koi8r", "koi8u", "greek", "hebrew", "armscii8", "swe7", "dec8", "hp8", "keybcs2",
        "macce", "macroman", "geostd8", "tis620", "ujis", "eucjpms", "euckr", "gb2312",
    };

    static string OtherReading(MySqlLexMode mode) => (mode.AnsiQuotes, mode.NoBackslashEscapes) switch
    {
        _ when !mode.AllCode => OtherReading(mode with { CodeUpTo = int.MaxValue, MariaDb = false })
            + (mode.CodeUpTo == 0
                ? $" It reads this way on {(mode.MariaDb ? "MariaDB" : "MySQL")} when the server skips the version-gated comments (/*!NNNNN … */, /*M! … */)."
                : $" It reads this way on {(mode.MariaDb ? "MariaDB" : "MySQL")} {mode.CodeUpTo}, which runs the version-gated comments up to that version and skips the rest."),
        (true, true) => " The text reads this way when the server's sql_mode has ANSI_QUOTES and NO_BACKSLASH_ESCAPES.",
        (true, false) => " The text reads this way when the server's sql_mode has ANSI_QUOTES.",
        (false, true) => " The text reads this way when the server's sql_mode has NO_BACKSLASH_ESCAPES.",
        _ => " The text reads this way with the server's default sql_mode (no ANSI_QUOTES or NO_BACKSLASH_ESCAPES).",
    };

    /// <param name="flagReadingChanges">Report <c>readingChanged</c>: only when the text has characters the readings differ on.</param>
    static Classification ClassifyIn(string text, MySqlLexMode mode, Func<int, int> lineAt, bool flagReadingChanges, int depth)
    {
        var nesting = 0; // SET STATEMENT … FOR / ANALYZE / EXPLAIN ANALYZE inside each other
        var destructive = new List<DestructiveFinding>();
        var batches = new List<BatchInfo>();
        string? refusal = null, use = null;
        bool ddl = false, begins = false, ends = false;
        var prepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in MySqlSplitter.Split(text, mode))
        {
            var code = st.Tokens.Where(t => !t.IsTrivia).ToList();
            if (code.Count == 0) continue;
            // Token lines count from where each statement was tokenized: lines are taken from offsets in the whole text.
            var startLine = lineAt(st.Start);
            batches.Add(new BatchInfo
            {
                Index = batches.Count, StartLine = startLine, EndLine = lineAt(st.End), StartOffset = st.Start, Length = st.End - st.Start,
            });
            // C2: the batch is sent whole, and the server knows no client DELIMITER — a semicolon outside a stored program's
            // body ends a statement there, so each statement the server will see is classified on its own.
            foreach (var t in st.Tokens)
                if (t.Kind == MySqlTokenKind.Hint && MySqlTokenizer.HintEndsElsewhere(text, t))
                {
                    destructive.Add(new DestructiveFinding
                    {
                        Rule = "unparsed", Statement = Shorten(text, st.Start, st.End), Line = lineAt(t.Offset), Column = 1,
                        Description = "An optimizer hint (/*+ … */) holds a quote around */: MySQL's hint reader ends it at another */ than a comment ends, so what follows could not be checked for destructive statements.",
                    });
                    break;
                }
            var pieces = ServerStatements(code);
            for (var p = 0; p < pieces.Count; p++)
                Statement(pieces[p], p == 0 ? "" : HiddenStatement, inBlock: false);
        }
        return new Classification
        {
            Destructive = destructive.ToArray(), HasDdl = ddl, BeginsTransaction = begins, EndsTransaction = ends, UseDatabase = use,
            Batches = batches.ToArray(), ParseErrors = [], Refusal = refusal,
        };

        // One statement the server runs; inBlock: a statement inside a compound statement that runs now (BEGIN NOT ATOMIC …).
        void Statement(List<MySqlToken> code, string context, bool inBlock)
        {
            DestructiveFinding Finding(string rule, string statement, MySqlToken at, string description) =>
                new() { Rule = rule, Statement = statement, Line = lineAt(at.Offset), Column = 1, Description = description + context };
            var verb = Verb(code);
            var stmtText = Shorten(text, code[0].Offset, code[^1].Offset + code[^1].Length);
            if (DdlVerbs.Contains(verb)) ddl = true;
            var change = ChangesReading(code, mode);
            if (change.Changes && (flagReadingChanges || change.Always))
                destructive.Add(Finding("readingChanged", stmtText, code[0],
                    "This statement changes sql_mode or the client character set, which decides how the server reads quotes and backslashes in the statements after it, so they could not be checked for destructive statements."));
            // Code audit 2026-11-25, second round: the setting outlives the run (it is the session's), and Querithm always
            // sends UTF-8 text, so after a confirmed SET NAMES gbk every later run of the tab was read by the server as gbk
            // and by the guard as UTF-8. A character set whose characters can end in an ASCII byte is refused outright.
            if (change.Unsafe is { } cs)
                refusal ??= $"The client character set {cs} is turned off: Querithm always sends text as UTF-8, and a server reading it as {cs} can take a quote or backslash as part of the character before it, so no statement after it could be checked. Use utf8mb4.";
            void Inner(string sql, string when)
            {
                if (depth >= MaxDynamicDepth)
                {
                    destructive.Add(Finding("unparsed", stmtText, code[0], $"Dynamic SQL is nested more than {MaxDynamicDepth} levels deep here; Querithm did not read further, so it could not be checked for destructive statements."));
                    return;
                }
                var inner = ClassifyAll(sql, mode with { CodeUpTo = int.MaxValue, MariaDb = false }, depth + 1, flagReadingChanges);
                ddl |= inner.HasDdl;
                refusal ??= inner.Refusal;
                // L6: a COMMIT or START TRANSACTION the prepared text runs steps outside an actual plan's transaction too.
                begins |= inner.BeginsTransaction;
                ends |= inner.EndsTransaction;
                foreach (var f in inner.Destructive)
                    destructive.Add(f with { Statement = stmtText, Line = lineAt(code[0].Offset), Description = f.Description + when + context });
            }
            // The statement SET STATEMENT … FOR, ANALYZE and EXPLAIN ANALYZE run, at most 64 inside each other.
            void Nested(List<MySqlToken> sub, string why)
            {
                if (sub.Count == 0) return;
                if (nesting >= 64)
                {
                    destructive.Add(Finding("unparsed", stmtText, code[0], "Statements are nested more than 64 levels deep here; Querithm did not read further, so it could not be checked for destructive statements."));
                    return;
                }
                nesting++;
                try { Statement(sub, context + why, inBlock); }
                finally { nesting--; }
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
                // MariaDB's CREATE OR REPLACE TABLE drops the table and its rows first (code audit 2026-11-25); CREATE OR
                // REPLACE of any object replaces it, flagged as on PostgreSQL and DuckDB.
                case "CREATE" when code.Count > 2 && code[1].Is("OR") && code[2].Is("REPLACE"):
                    destructive.Add(Finding("alter", stmtText, code[0], "CREATE OR REPLACE replaces an existing object (a table with its rows, and commits)."));
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
                // Inside a compound statement BEGIN opens a block, never a transaction.
                case "BEGIN" when !inBlock && !(code.Count > 1 && code[1].Is("NOT")):
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
                    if (Literal(code, 3, mode) is { } prep && prep.Next == code.Count)
                        Inner(prep.Value, " It runs when the prepared statement is executed.");
                    else
                        destructive.Add(Finding("dynamicSql", stmtText, code[0], "PREPARE … FROM a variable prepares SQL text built at run time, which cannot be checked for destructive statements."));
                    break;
                // MariaDB: EXECUTE IMMEDIATE 'literal' [USING …] runs that text now — classified like PREPARE's (8a review).
                case "EXECUTE" when code.Count > 2 && code[1].Is("IMMEDIATE"):
                    if (Literal(code, 2, mode) is { } now && (now.Next == code.Count || code[now.Next].Is("USING")))
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
                // Fourth round (C4): MariaDB's SET STATEMENT var = … FOR <statement> runs the statement; MariaDB's ANALYZE
                // [FORMAT=JSON] <statement> and EXPLAIN / DESCRIBE ANALYZE <statement> run it too (not ANALYZE TABLE).
                case "SET" when code.Count > 2 && code[1].Is("STATEMENT"):
                    for (int k = 2, d = 0; k < code.Count; k++)
                    {
                        if (code[k].IsP("(")) d++;
                        else if (code[k].IsP(")")) d = Math.Max(0, d - 1);
                        else if (d == 0 && code[k].Is("FOR") && k + 1 < code.Count)
                        {
                            Nested(code.GetRange(k + 1, code.Count - k - 1), " It runs under SET STATEMENT … FOR.");
                            break;
                        }
                    }
                    break;
                case "ANALYZE" when code.Count > 1 && !(code[1].Is("TABLE") || code[1].Is("TABLES") || code[1].Is("LOCAL") || code[1].Is("NO_WRITE_TO_BINLOG")):
                    Nested(code.GetRange(AfterFormat(code, 1), code.Count - AfterFormat(code, 1)), " MariaDB's ANALYZE runs the statement.");
                    break;
                case "EXPLAIN" or "DESCRIBE" or "DESC" when code.Count > 2 && code[1].Is("ANALYZE"):
                    Nested(code.GetRange(AfterFormat(code, 2), code.Count - AfterFormat(code, 2)), " EXPLAIN ANALYZE runs the statement.");
                    break;
            }
            // A compound statement outside a stored program (MariaDB's BEGIN NOT ATOMIC, IF, CASE, LOOP, WHILE, REPEAT) runs
            // its body now; every statement in it (nested blocks included: the scan is flat) is classified.
            if (!inBlock && RunsNowAsBlock(code))
                foreach (var body in BlockStatements(code))
                    Statement(body, context + " It runs now, inside a compound statement.", inBlock: true);
        }
    }

    /// <summary>The index after an optional <c>FORMAT = x</c> at <paramref name="i"/> (ANALYZE FORMAT=JSON …, EXPLAIN ANALYZE FORMAT=TREE …).</summary>
    static int AfterFormat(List<MySqlToken> code, int i) =>
        i + 2 < code.Count && code[i].Is("FORMAT") && code[i + 1].IsP("=") ? i + 3 : i;

    /// <summary>At most 200 characters of the statement, copied without the rest (third round: a copy per nested statement was quadratic).</summary>
    static string Shorten(string text, int from, int to) => to - from > 200 ? text.Substring(from, 200) + "…" : text[from..to];

    const string HiddenStatement = " The server runs it as a statement of its own: it ignores the client DELIMITER, and a semicolon outside a stored program's body ends a statement.";

    /// <summary>
    /// The statements the server sees in one client statement: split at each semicolon outside parentheses and outside a
    /// compound block (BEGIN … END, CASE … END, IF … END IF, LOOP, WHILE, REPEAT, MariaDB's FOR — a stored program's body or
    /// MariaDB's anonymous block). A statement without a semicolon (every one under the default delimiter) is itself.
    /// Tokens, never text.
    /// </summary>
    /// <remarks>
    /// An opener is counted only where a compound statement can start (code audit 2026-11-25, second round): a missed one
    /// splits a body into statements of its own (findings the body does not have), a wrong one joins a statement the server
    /// runs to the one before (a guard bypass). BEGIN is not reserved, so <c>FROM t begin WHERE …</c> and
    /// <c>UPDATE begin SET …</c> name a table; it opens a block only at a statement start inside a block, at a stored
    /// program's body, at a handler's body, or as MariaDB's <c>BEGIN NOT ATOMIC</c>. THEN and ELSE of a CASE expression are
    /// value positions, never statement starts.
    /// </remarks>
    internal static List<List<MySqlToken>> ServerStatements(List<MySqlToken> code)
    {
        if (!code.Exists(t => t.IsP(";"))) return [code];
        var pieces = new List<List<MySqlToken>>();
        var body = ProgramBodyStart(code);
        var handlerBodies = new HashSet<int>();
        // The open blocks, innermost on top: true for a CASE expression (its THEN / ELSE take values).
        var blocks = new Stack<bool>();
        var opened = new HashSet<int>(); // the BEGIN tokens that opened a block
        int from = 0, parens = 0;
        bool StatementStart(int i)
        {
            if (i == from || i == body || handlerBodies.Contains(i)) return true;
            var p = code[i - 1];
            if (p.IsP(";")) return true;
            if (p.IsP(":")) return i >= 2 && code[i - 2].Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent; // label: (`lbl`: too, fourth round C5)
            if (p.Kind != MySqlTokenKind.Word) return false;
            if (p.Is("THEN") || p.Is("ELSE")) return !(blocks.Count > 0 && blocks.Peek());
            if (p.Is("BEGIN")) return opened.Contains(i - 1);
            return p.Is("ATOMIC") || p.Is("DO") || p.Is("LOOP") || p.Is("REPEAT");
        }
        for (var i = 0; i < code.Count; i++)
        {
            var t = code[i];
            var next = i + 1 < code.Count ? code[i + 1] : (MySqlToken?)null;
            if (t.IsP("(")) parens++;
            else if (t.IsP(")")) parens = Math.Max(0, parens - 1);
            else if (t.IsP(";"))
            {
                if (parens != 0 || blocks.Count != 0) continue;
                if (i > from) pieces.Add(code.GetRange(from, i - from));
                from = i + 1;
            }
            else if (t.Kind != MySqlTokenKind.Word) continue;
            else if (t.Is("END")) { if (blocks.Count > 0) blocks.Pop(); }
            else if (i > 0 && code[i - 1].Is("END")) { } // END IF, END CASE, END LOOP, END WHILE, END REPEAT, END FOR
            else if (t.Is("HANDLER") && next is { } f && f.Is("FOR")) handlerBodies.Add(AfterConditions(code, i + 2));
            else if (t.Is("CASE")) blocks.Push(!StatementStart(i)); // a CASE expression ends at END too
            else if (!StatementStart(i)) continue;
            else if (t.Is("LOOP") || t.Is("WHILE") || (t.Is("REPEAT") && !(next is { } n && n.IsP("(")))) blocks.Push(false);
            else if (t.Is("IF") && IfStatement(code, i)) blocks.Push(false);
            else if (t.Is("FOR") && ForLoop(code, i)) blocks.Push(false);
            else if (t.Is("BEGIN") && next is { } b && !(b.IsP(";") || b.Is("WORK"))
                     && (i == body || blocks.Count > 0 || handlerBodies.Contains(i) || b.Is("NOT")
                         || (i >= 2 && code[i - 1].IsP(":") && (i - 2 == body || handlerBodies.Contains(i - 2)))))
            {
                blocks.Push(false);
                opened.Add(i);
            }
        }
        if (from < code.Count) pieces.Add(code.GetRange(from, code.Count - from));
        return pieces;
    }

    /// <summary>
    /// Where a stored program's body starts (CREATE PROCEDURE / FUNCTION / TRIGGER / EVENT): after the parameter list, the
    /// RETURNS type and the characteristics; after FOR EACH ROW [FOLLOWS | PRECEDES t]; after an event's DO. -1 otherwise.
    /// </summary>
    static int ProgramBodyStart(List<MySqlToken> code)
    {
        if (code.Count < 3 || !code[0].Is("CREATE")) return -1;
        // The header ends at the first ';' of its level: a body starts before it (third round: `CREATE USER function; SELECT
        // … (SELECT 1 a) begin …` found FUNCTION, then the next statement's '(' as its parameter list, and BEGIN joined
        // the statements after it).
        var limit = 0;
        for (var depth = 0; limit < code.Count && !(depth == 0 && code[limit].IsP(";")); limit++)
            depth += code[limit].IsP("(") ? 1 : code[limit].IsP(")") ? -1 : 0;
        var body = HeaderEnd(limit < code.Count ? code.GetRange(0, limit) : code);
        return body >= 0 && body <= limit ? body : -1;
    }

    static int HeaderEnd(List<MySqlToken> code)
    {
        var k = 1;
        while (k < code.Count && !(code[k].Is("PROCEDURE") || code[k].Is("FUNCTION") || code[k].Is("TRIGGER") || code[k].Is("EVENT")))
        {
            if (code[k].Is("TABLE") || code[k].Is("VIEW") || code[k].Is("INDEX") || code[k].Is("DATABASE") || code[k].Is("SCHEMA") || code[k].IsP("(")) return -1;
            k++;
        }
        if (k >= code.Count) return -1;
        if (code[k].Is("TRIGGER"))
        {
            for (var j = k; j + 2 < code.Count; j++)
                if (code[j].Is("FOR") && code[j + 1].Is("EACH") && code[j + 2].Is("ROW"))
                    return j + 3 + (j + 4 < code.Count && (code[j + 3].Is("FOLLOWS") || code[j + 3].Is("PRECEDES")) ? 2 : 0);
            return -1;
        }
        if (code[k].Is("EVENT"))
        {
            for (int j = k, depth = 0; j < code.Count; j++)
                if (code[j].IsP("(")) depth++;
                else if (code[j].IsP(")")) depth--;
                else if (depth == 0 && code[j].Is("DO")) return j + 1;
            return -1;
        }
        // PROCEDURE / FUNCTION name (params) [RETURNS type] characteristics…
        var open = code.FindIndex(k, t => t.IsP("("));
        if (open < 0) return -1;
        var i = Close(code, open) + 1;
        if (i < code.Count && code[i].Is("RETURNS"))
        {
            i += 2; // RETURNS type
            if (i < code.Count && code[i].IsP("(")) i = Close(code, i) + 1;
            while (i < code.Count && code[i].Kind == MySqlTokenKind.Word && TypeWords.Contains(code[i].Text))
                i += code[i].Is("CHARSET") || code[i].Is("COLLATE") ? 2 : code[i].Is("CHARACTER") ? 3 : 1;
        }
        while (i < code.Count)
        {
            if (code[i].Is("COMMENT")) i += 2;
            else if (code[i].Kind == MySqlTokenKind.Word && Characteristics.Contains(code[i].Text)) i++;
            else break;
        }
        return i;
    }

    static readonly HashSet<string> TypeWords = new(StringComparer.OrdinalIgnoreCase) { "CHARSET", "CHARACTER", "COLLATE", "UNSIGNED", "SIGNED", "ZEROFILL", "BINARY", "ASCII", "UNICODE", "PRECISION", "VARYING" };

    static readonly HashSet<string> Characteristics = new(StringComparer.OrdinalIgnoreCase)
    {
        "LANGUAGE", "SQL", "NOT", "DETERMINISTIC", "CONTAINS", "NO", "READS", "MODIFIES", "DATA", "SECURITY", "DEFINER", "INVOKER", "AGGREGATE",
    };

    static int Close(List<MySqlToken> code, int open)
    {
        for (int j = open, depth = 0; j < code.Count; j++)
            if (code[j].IsP("(")) depth++;
            else if (code[j].IsP(")") && --depth == 0) return j;
        return code.Count;
    }

    /// <summary>
    /// The index after a handler's condition list (from <paramref name="i"/>, just after HANDLER FOR): SQLSTATE [VALUE]
    /// 'xxxxx', SQLWARNING, NOT FOUND, SQLEXCEPTION, an error number or a condition name, separated by commas.
    /// </summary>
    static int AfterConditions(List<MySqlToken> code, int i)
    {
        while (i < code.Count)
        {
            if (code[i].Is("SQLSTATE"))
            {
                i++;
                if (i < code.Count && code[i].Is("VALUE")) i++;
                i++;
            }
            else if (code[i].Is("NOT") && i + 1 < code.Count && code[i + 1].Is("FOUND")) i += 2;
            else i++;
            if (i < code.Count && code[i].IsP(",")) i++;
            else break;
        }
        return i;
    }

    /// <summary>IF cond THEN — a THEN follows at this depth before the statement or a CASE branch ends (IF(a, b, c) has none).</summary>
    static bool IfStatement(List<MySqlToken> code, int i)
    {
        var depth = 0;
        for (var j = i + 1; j < code.Count; j++)
        {
            var t = code[j];
            if (t.IsP("(")) depth++;
            else if (t.IsP(")")) { if (--depth < 0) return false; }
            else if (depth != 0) continue;
            else if (t.Is("THEN")) return true;
            else if (t.IsP(";") || t.Is("WHEN") || t.Is("ELSE") || t.Is("ELSEIF") || t.Is("END")) return false;
        }
        return false;
    }

    /// <summary>MariaDB's <c>FOR i IN [REVERSE] a..b DO</c> / <c>FOR rec IN cursor DO</c> (not FOR UPDATE, FOR EACH ROW, HANDLER FOR).</summary>
    static bool ForLoop(List<MySqlToken> code, int i) =>
        i + 2 < code.Count && code[i + 1].Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent && code[i + 2].Is("IN");

    /// <summary>
    /// A statement that runs a compound body now: MariaDB's <c>[label:] BEGIN NOT ATOMIC</c>, IF, CASE, LOOP, WHILE, REPEAT
    /// and FOR outside a stored program. Fail closed for sql_mode=ORACLE's anonymous blocks (MariaDB): a BEGIN that is no
    /// transaction start, or a leading DECLARE, also has its body read.
    /// </summary>
    static bool RunsNowAsBlock(List<MySqlToken> code)
    {
        var i = code.Count > 2 && code[0].Kind is MySqlTokenKind.Word or MySqlTokenKind.QuotedIdent && code[1].IsP(":") ? 2 : 0;
        var t = code[i];
        var next = i + 1 < code.Count ? code[i + 1] : (MySqlToken?)null;
        if (t.Is("BEGIN")) return next is { } b && !b.Is("WORK");
        if (t.Is("DECLARE")) return i == 0;
        if (t.Is("FOR")) return ForLoop(code, i);
        return t.Is("IF") || t.Is("CASE") || t.Is("LOOP") || t.Is("WHILE") || t.Is("REPEAT") && !(next is { } n && n.IsP("("));
    }

    /// <summary>Tokens after which a statement starts inside a compound statement.</summary>
    static readonly HashSet<string> BlockStarts = new(StringComparer.OrdinalIgnoreCase) { ";", ":", "BEGIN", "ATOMIC", "THEN", "ELSE", "DO", "LOOP", "REPEAT" };

    /// <summary>The verbs <see cref="ClassifyIn"/> acts on: a statement inside a compound statement is read when it starts with one.</summary>
    static readonly HashSet<string> StatementVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "DROP", "TRUNCATE", "ALTER", "DELETE", "UPDATE", "USE", "START", "COMMIT", "ROLLBACK", "PREPARE", "EXECUTE", "LOAD", "CREATE",
        "RENAME", "SET", "WITH",
    };

    /// <summary>
    /// Each statement inside a compound statement, nested ones included, up to its semicolon: one that starts with a verb
    /// the classifier acts on, after a token a statement follows (or as a handler's single-statement body:
    /// <c>DECLARE EXIT HANDLER FOR SQLEXCEPTION DROP TABLE x;</c>).
    /// </summary>
    /// <remarks>
    /// Linear (code audit 2026-11-25, third round): each statement used to run to its ';' from every start, so
    /// <c>IF 1 THEN</c> × 50,000 took about 10^10 steps and the run could neither start nor be cancelled. Compound
    /// openers (IF, CASE, LOOP …) start no statement here (their bodies' statements do), each statement ends at the next
    /// ';' of its own level (or the ')' closing that level), and at the next statement start at the latest, so no token is
    /// read twice.
    /// </remarks>
    static IEnumerable<List<MySqlToken>> BlockStatements(List<MySqlToken> code)
    {
        var handlerBodies = new HashSet<int>();
        var starts = new List<int>();
        for (var i = 1; i < code.Count; i++)
        {
            if (code[i].Is("HANDLER") && i + 1 < code.Count && code[i + 1].Is("FOR")) handlerBodies.Add(AfterConditions(code, i + 2));
            if (code[i].Kind != MySqlTokenKind.Word || !StatementVerbs.Contains(code[i].Text)) continue;
            if (!handlerBodies.Contains(i) && (!BlockStarts.Contains(code[i - 1].Text) || code[i - 1].Kind is not (MySqlTokenKind.Word or MySqlTokenKind.Punct or MySqlTokenKind.Operator))) continue;
            starts.Add(i);
        }
        if (starts.Count == 0) yield break;
        // The next ';' at each token's own parenthesis level, or the ')' that closes the level: one backward pass.
        var depth = new int[code.Count];
        for (int i = 0, d = 0; i < code.Count; i++)
        {
            depth[i] = d;
            if (code[i].IsP("(")) d++;
            else if (code[i].IsP(")") && d > 0) { d--; depth[i] = d + 1; }
        }
        var nextAt = new int[(depth.Length == 0 ? 0 : depth.Max()) + 2];
        Array.Fill(nextAt, code.Count);
        var end = new int[code.Count];
        for (var i = code.Count - 1; i >= 0; i--)
        {
            if (code[i].IsP(";") || code[i].IsP(")")) nextAt[depth[i]] = i;
            end[i] = nextAt[depth[i]];
        }
        for (var k = 0; k < starts.Count; k++)
        {
            var from = starts[k];
            var to = Math.Min(end[from], k + 1 < starts.Count ? starts[k + 1] : code.Count);
            if (to > from) yield return code.GetRange(from, to - from);
        }
    }

    /// <summary>
    /// The literal at <paramref name="i"/> and the index after it: a string, or one after a charset introducer
    /// (<c>_utf8mb4'…'</c>, <c>N'…'</c>: MySQL's literal syntax); null for anything else (a variable, an expression).
    /// </summary>
    static (string Value, int Next)? Literal(IReadOnlyList<MySqlToken> code, int i, MySqlLexMode mode)
    {
        if (i < code.Count && code[i].Kind == MySqlTokenKind.String) return (StringValue(code[i].Text, mode), i + 1);
        if (i + 1 < code.Count && code[i].Kind == MySqlTokenKind.Word && (code[i].Text.StartsWith('_') || code[i].Is("N"))
            && code[i + 1].Kind == MySqlTokenKind.String)
            return (StringValue(code[i + 1].Text, mode), i + 2);
        return null;
    }

    /// <summary>
    /// The value of a quoted string token: outer quotes off, doubled quotes undone, and backslash escapes undone unless
    /// <paramref name="mode"/> reads them as characters (NO_BACKSLASH_ESCAPES).
    /// </summary>
    static string StringValue(string quoted, MySqlLexMode mode)
    {
        if (quoted.Length < 2) return quoted;
        var q = quoted[0];
        var body = quoted[1..^1];
        var sb = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && !mode.NoBackslashEscapes && i + 1 < body.Length) { sb.Append(body[++i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', var x => x }); continue; }
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

    /// <summary>offset → 1-based line by binary search over line starts (a scan per statement was quadratic in a long script).</summary>
    static Func<int, int> LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return offset =>
        {
            var idx = starts.BinarySearch(Math.Clamp(offset, 0, text.Length));
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        };
    }

    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema, CancellationToken ct)
    {
        // A request already superseded does no work; the USE scan below stops on the token too.
        ct.ThrowIfCancellationRequested();
        using var cancel = Tier2Cancellation.Use(ct);
        var database = MySqlCompletion.DatabaseAt(p.DocId, p.Text ?? "", Math.Clamp(p.Offset, 0, (p.Text ?? "").Length), _mode) ?? p.Database;
        using var _ = PgCompletionRules.Use(MySqlCompletion.RulesFor(_mode, database ?? ""));
        return PgTier2Completion.Complete(p with { Database = database }, schema, ct);
    }

    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cancel = Tier2Cancellation.Use(ct);
        var database = MySqlCompletion.DatabaseAt(p.DocId, p.Text ?? "", Math.Clamp(p.Offset, 0, (p.Text ?? "").Length), _mode) ?? p.Database;
        using var _ = PgCompletionRules.Use(MySqlCompletion.RulesFor(_mode, database ?? ""));
        return PgTier2Completion.Signature(p with { Database = database }, schema, ct);
    }

    public Diagnostic[] Diagnostics(DiagnosticsParams p) => [];
    public FormatResult Format(FormatParams p) => new() { Text = p.Text };

    public FoldingRange[] Folding(FoldingParams p)
    {
        // One line index for the text: a scan from the start per statement was quadratic in a long script (Gemini review).
        var lineAt = LineIndex(p.Text);
        return MySqlSplitter.Split(p.Text, _mode).Select(st => (Start: lineAt(st.Start), End: lineAt(st.End)))
            .Where(x => x.End > x.Start).Select(x => new FoldingRange { StartLine = x.Start, EndLine = x.End }).ToArray();
    }
}

using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Intelligence.PgSql;

/// <summary>
/// PostgreSQL intelligence on tokens (§9): tier-2 completion and signature help are scope-aware on tokens
/// (<see cref="PgTier2Completion"/>), not a full parse; diagnostics stay empty (there is no PostgreSQL parser here).
/// Classification for the production guard is token based, never regex over raw text (§15 rule 6).
/// </summary>
public sealed class PgLanguageService : IDialectService
{
    public Redaction RedactSecrets(string text) => PgRedaction.Redact(text);

    public void Forget(string docId) => Completion.DocumentCaches.Forget(docId);

    /// <summary>
    /// Completion audit 2026-11-25 S1, as for MySQL's sql_mode: a session can <c>SET standard_conforming_strings = off</c>
    /// (deprecated, still accepted), and then a backslash escapes a quote in '…' too. Read the standard way,
    /// <c>SELECT 'a\'' ; DROP TABLE t; -- '</c> is one SELECT; such a server (and Npgsql, which follows the setting when it
    /// splits) runs a DROP the guard never saw. So a text holding <c>\</c> is also read that way, and every destructive
    /// statement either reading finds counts, and so does a BEGIN or COMMIT either reading finds (an actual plan refuses
    /// both, M7). Batches stay the standard reading's. Each reading is uniform, the server's is not: after
    /// <c>SET standard_conforming_strings</c> it reads the rest the new way, and a statement before the SET can shift the
    /// other reading's quotes so that neither sees what follows. So a statement that can change the setting is a finding
    /// of its own, <c>readingChanged</c> — only in a text holding <c>\</c>, the one character the readings differ on.
    /// </summary>
    public Classification Classify(string text)
    {
        var mayMisread = text.IndexOf('\\') >= 0;
        var standard = Classify(text, PgTokenizer.Tokenize(text), standardStrings: true, mayMisread, withBatches: true);
        if (!mayMisread) return standard;
        var other = Classify(text, PgTokenizer.Tokenize(text, standardStrings: false), standardStrings: false, flagReadingChanges: true, withBatches: false);
        var findings = standard.Destructive.ToList();
        var seen = new HashSet<(string, int, string)>(findings.Select(f => (f.Rule, f.Line, f.Statement)));
        foreach (var f in other.Destructive)
            if (seen.Add((f.Rule, f.Line, f.Statement)))
                findings.Add(f with { Description = f.Description + " The text reads this way when the session has standard_conforming_strings off." });
        return standard with
        {
            Destructive = findings.ToArray(), HasDdl = standard.HasDdl || other.HasDdl,
            BeginsTransaction = standard.BeginsTransaction || other.BeginsTransaction, EndsTransaction = standard.EndsTransaction || other.EndsTransaction,
        };
    }

    static Classification Classify(string text, List<PgToken> tokens, bool standardStrings, bool flagReadingChanges, bool withBatches)
    {
        var scan = new Scan(standardStrings);
        var lineOf = withBatches ? LineIndex(text) : null;
        // One batch per statement: Npgsql would split them anyway, and per-statement execution lets server error
        // positions (relative to the statement) map to exact editor lines.
        var batches = new List<BatchInfo>();
        foreach (var stmt in PgTokenizer.Statements(tokens))
        {
            var words = stmt.Where(t => !t.IsTrivia).ToList();
            if (words.Count == 0) continue;
            var snippet = Snippet(text, words);
            void Add(string rule, string description, int line) => scan.Findings.Add(new DestructiveFinding
            {
                Rule = rule, Statement = snippet, Line = line, Column = 1, Description = description,
            });
            if (flagReadingChanges && ChangesReading(words, standardStrings, 0))
                Add("readingChanged", "This statement can change standard_conforming_strings, which decides how the server reads backslashes in the statements after it, so they could not be checked for destructive statements.", words[0].Line);
            scan.Statement(words, Add, "");
            if (lineOf == null) continue;
            var from = words[0].Offset;
            var to = words[^1].Offset + words[^1].Length;
            batches.Add(new BatchInfo { Index = batches.Count, StartOffset = from, Length = to - from, StartLine = lineOf(from), EndLine = lineOf(to - 1) });
        }
        return new Classification
        {
            Destructive = scan.Findings.ToArray(), HasDdl = scan.HasDdl, BeginsTransaction = scan.Begins, EndsTransaction = scan.Ends,
            UseDatabase = scan.Use,
            Batches = batches.ToArray(),
            ParseErrors = [],
        };
    }

    static bool IsReadingSetting(string name) => name.Equals("standard_conforming_strings", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The statement can change standard_conforming_strings for the statements after it: SET / SET SESSION / SET LOCAL /
    /// RESET of it, RESET ALL, DISCARD ALL, <c>set_config('standard_conforming_strings', …)</c> or an UPDATE of
    /// <c>pg_settings</c> naming it — also inside a dollar-quoted body (a DO block runs now; a function may run later in
    /// the script) and an EXECUTE literal in one. A read (<c>SHOW</c>, <c>current_setting</c>) is none. Tokens, never text.
    /// </summary>
    /// <remarks>
    /// Code audit 2026-11-25 (C4 / M5) closed four ways past it, each now read as a change (fail closed): a body in
    /// single quotes (<c>DO '…'</c>, <c>CREATE FUNCTION … AS '…'</c>), the function named as a quoted identifier
    /// (<c>"set_config"</c>), a first argument of set_config that is not one literal (<c>'standard_' || '…'</c>), an UPDATE
    /// of pg_settings (its name test can be computed too), and a body nested deeper than the scan follows.
    /// </remarks>
    static bool ChangesReading(List<PgToken> words, bool standardStrings, int depth)
    {
        bool setOrReset = false, settingsTable = false, update = false;
        var isDo = words.Count > 0 && words[0].Is("DO");
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.Is("SET") || w.Is("RESET")) setOrReset = true;
            if (w.Is("UPDATE")) update = true;
            if (IsName(w, "pg_settings")) settingsTable = true;
            if ((w.Is("RESET") || w.Is("DISCARD")) && i + 1 < words.Count && words[i + 1].Is("ALL")) return true;
            // Third round: inside a body, EXECUTE of anything but one literal (format(…), ||, a variable) builds its text at
            // run time — `EXECUTE format($f$SET %s TO off$f$, $n$standard_conforming_strings$n$)` — so it may change it.
            if (depth > 0 && w.Is("EXECUTE") && !(i + 1 < words.Count && words[i + 1].Kind is PgTokenKind.String or PgTokenKind.DollarString
                && !UnicodeEscaped(words, i + 1)
                && (i + 2 == words.Count || words[i + 2].Is("INTO") || words[i + 2].Is("USING") || words[i + 2].Is("STRICT"))))
                return true;
            if (IsName(w, "set_config"))
            {
                // set_config(name, value, is_local): a literal name says which setting; anything else may be this one.
                if (i + 3 < words.Count && words[i + 1].Text == "(" && words[i + 2].Kind is PgTokenKind.String or PgTokenKind.DollarString && words[i + 3].Text == ",")
                {
                    if (IsReadingSetting(BodyText(words[i + 2].Text, standardStrings))) return true;
                }
                else if (i + 1 < words.Count && words[i + 1].Text == "(") return true;
            }
        }
        if (settingsTable && update) return true;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            switch (w.Kind)
            {
                // Fourth round (C6): U&"\0073tandard_conforming_strings" (or a U& set_config / pg_settings) names the setting
                // with escapes this scan does not decode — fail closed.
                case PgTokenKind.QuotedIdent when UnicodeEscaped(words, i):
                    return true;
                case PgTokenKind.Word when setOrReset && IsReadingSetting(w.Text):
                case PgTokenKind.QuotedIdent when setOrReset && IsReadingSetting(PgIdent.UnquoteDoubleQuoted(w.Text)):
                    return true;
                case PgTokenKind.DollarString:
                case PgTokenKind.String when i > 0 && (words[i - 1].Is("EXECUTE") || words[i - 1].Is("AS") || (isDo && !words[i - 1].Is("LANGUAGE"))):
                    // C7: 'BEGIN SET standard_'\n'conforming_strings = off; END' is one literal to the server.
                {
                    var last = i;
                    var body = w.Kind == PgTokenKind.String ? StringRun(words, i, standardStrings, out last) : BodyText(w.Text, standardStrings);
                    if (depth >= 4 || BodyChangesReading(body, standardStrings, depth)) return true;
                    i = last;
                    break;
                }
                // A Unicode-escaped body (U&'…') is not decoded: it may change the setting (second round, fail closed).
                case PgTokenKind.String when UnicodeEscaped(words, i) && i >= 3 && (words[i - 3].Is("AS") || words[i - 3].Is("EXECUTE") || isDo):
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The value of the '…' literal at <paramref name="i"/> joined with every '…' literal after it on a later line, as the
    /// server reads them (fourth round, C7: <c>DO 'BEGIN '</c> newline <c>'DROP TABLE users; END;'</c> is one body), and the
    /// index of the last one.
    /// </summary>
    static string StringRun(List<PgToken> words, int i, bool standardStrings, out int last)
    {
        var sb = new System.Text.StringBuilder(LiteralValue(words[i].Text, standardStrings));
        last = i;
        while (last + 1 < words.Count && words[last + 1].Kind == PgTokenKind.String && !UnicodeEscaped(words, last + 1)
               && words[last + 1].Line > words[last].Line + words[last].Text.Count(c => c == '\n'))
            sb.Append(LiteralValue(words[++last].Text, standardStrings));
        return sb.ToString();
    }

    /// <summary>The string at <paramref name="i"/> is the body of a <c>U&amp;'…'</c> literal: the word U and '&amp;' right before it, adjacent.</summary>
    static bool UnicodeEscaped(List<PgToken> words, int i) =>
        i >= 2 && words[i - 1].Text == "&" && words[i - 1].Offset + 1 == words[i].Offset
        && words[i - 2].Is("U") && words[i - 2].Offset + 1 == words[i - 1].Offset;

    /// <summary>The word or quoted identifier names <paramref name="name"/> (<c>set_config</c>, <c>"set_config"</c>).</summary>
    static bool IsName(PgToken t, string name) =>
        t.Kind == PgTokenKind.Word ? t.Text.Equals(name, StringComparison.OrdinalIgnoreCase)
        : t.Kind == PgTokenKind.QuotedIdent && PgIdent.UnquoteDoubleQuoted(t.Text) == name;

    static bool BodyChangesReading(string body, bool standardStrings, int depth) =>
        PgTokenizer.Statements(PgTokenizer.Tokenize(body, standardStrings)).Any(st =>
            st.Where(t => !t.IsTrivia).ToList() is { Count: > 0 } code && ChangesReading(code, standardStrings, depth + 1));

    /// <summary>
    /// The value of a '…' or E'…' literal token as the server reads it: '' is a quote; a backslash escapes in E'…' always
    /// and in '…' when <paramref name="standardStrings"/> is off (\b \f \n \r \t, octal \ooo, \xhh, \uXXXX, \UXXXXXXXX,
    /// otherwise the next character). A missing close quote is tolerated.
    /// </summary>
    internal static string LiteralValue(string s, bool standardStrings)
    {
        var escapes = s.Length > 0 && s[0] is 'E' or 'e' || !standardStrings;
        var from = s.Length > 0 && s[0] == '\'' ? 1 : Math.Min(2, s.Length);
        // The literal ends at its first quote that is neither doubled nor escaped. Code audit 2026-11-25 (M10): the last
        // character was dropped whenever it was a quote, so an unclosed E'abc\' lost the escaped quote it ends with.
        var to = s.Length;
        var sb = new System.Text.StringBuilder(Math.Max(0, to - from));
        for (var i = from; i < to; i++)
        {
            var c = s[i];
            if (c == '\'')
            {
                if (i + 1 < to && s[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                break; // the close quote
            }
            if (!escapes || c != '\\' || i + 1 >= to) { sb.Append(c); continue; }
            var n = s[++i];
            switch (n)
            {
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case >= '0' and <= '7':
                {
                    var code = 0;
                    var k = 0;
                    for (; k < 3 && i + k < to && s[i + k] is >= '0' and <= '7'; k++) code = code * 8 + (s[i + k] - '0');
                    sb.Append((char)(code & 0xFF));
                    i += k - 1;
                    break;
                }
                case 'x' or 'u' or 'U':
                {
                    var max = n == 'x' ? 2 : n == 'u' ? 4 : 8;
                    // M10: unsigned — \U80000041 overflowed an int to a negative code read as 'A'.
                    uint code = 0;
                    var k = 0;
                    for (; k < max && i + 1 + k < to && Uri.IsHexDigit(s[i + 1 + k]); k++) code = code * 16 + (uint)Uri.FromHex(s[i + 1 + k]);
                    if (k == 0) { sb.Append(n); break; }
                    if (code <= 0xFFFF) sb.Append((char)code);
                    else sb.Append(code <= 0x10FFFF ? char.ConvertFromUtf32((int)code) : "�");
                    i += k;
                    break;
                }
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Classifies one statement (its non-trivia tokens). Recurses into SQL that runs as part of the statement: data-modifying
    /// CTE bodies, the statement under EXPLAIN ANALYZE, the statement a PREPARE stores (it runs on EXECUTE) and the body of
    /// a DO block. Function bodies run later and are skipped. SQL that cannot be seen — EXECUTE of a statement not prepared
    /// in this text, a DO block's EXECUTE of text built at run time — counts as destructive (rule <c>dynamicSql</c>).
    /// </summary>
    /// <param name="standardStrings">
    /// The reading: a DO body and an EXECUTE literal in it are read the way the session reads the script (PL/pgSQL's
    /// scanner follows standard_conforming_strings too) — before, always the standard way, so the non-standard reading
    /// never saw a DROP hidden in a body (Gemini review 2026-11-25).
    /// </param>
    sealed class Scan(bool standardStrings)
    {
        public readonly List<DestructiveFinding> Findings = [];
        public bool HasDdl, Begins, Ends;
        public string? Use;
        /// <summary>Prepared statements of this text whose statement was classified at PREPARE.</summary>
        readonly HashSet<string> _prepared = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Words that start a destructive statement inside a PL/pgSQL body.</summary>
        static readonly HashSet<string> BodyVerbs = new(["DELETE", "UPDATE", "DROP", "TRUNCATE", "ALTER", "CREATE", "WITH", "MERGE", "EXECUTE"], StringComparer.OrdinalIgnoreCase);

        /// <summary>Tokens after which a PL/pgSQL statement starts (<c>BEGIN DELETE ...</c>, <c>THEN DROP ...</c>, <c>&lt;&lt;label&gt;&gt; ...</c>).</summary>
        static readonly HashSet<string> BodyStarts = new([";", "BEGIN", "THEN", "ELSE", "LOOP", ">>"], StringComparer.OrdinalIgnoreCase);

        /// <summary>How deep statements inside statements are read (CTE bodies, EXPLAIN, PREPARE, DO and EXECUTE literals).</summary>
        internal const int MaxNesting = 64;

        int _nesting;

        /// <summary>
        /// One statement, nested at most <see cref="MaxNesting"/> deep (third round: `WITH a AS (` × 10,000, DO → EXECUTE
        /// $q$ → DO …, PREPARE … AS PREPARE … recursed once per level and overflowed the stack, which ends the process).
        /// Deeper text is not read: a finding (fail closed).
        /// </summary>
        public void Statement(List<PgToken> words, Action<string, string, int> add, string context)
        {
            if (_nesting >= MaxNesting)
            {
                add("unparsed", $"Statements are nested more than {MaxNesting} levels deep here; Querithm did not read further, so it could not be checked for destructive statements.{context}", words[0].Line);
                return;
            }
            _nesting++;
            try { StatementCore(words, add, context); }
            finally { _nesting--; }
        }

        void StatementCore(List<PgToken> words, Action<string, string, int> add, string context)
        {
            var first = words[0];
            void Add(string rule, string description) => add(rule, description + context, first.Line);
            if (first.Is("EXPLAIN"))
            {
                if (ExplainTarget(words) is { } target) Statement(target, add, " EXPLAIN ANALYZE runs the statement.");
                return;
            }
            if (first.Is("DO"))
            {
                DoBlock(words, add);
                return;
            }
            // PREPARE name [(types)] AS statement: the statement runs on each EXECUTE (PREPARE TRANSACTION is two-phase commit).
            if (first.Is("PREPARE") && words.Count > 1 && !words[1].Is("TRANSACTION"))
            {
                for (var i = 2; i < words.Count; i++)
                {
                    if (words[i].Text == "(") { i = Close(words, i); continue; }
                    if (!words[i].Is("AS")) continue;
                    if (i + 1 < words.Count) Statement(words.GetRange(i + 1, words.Count - i - 1), add, " It runs when the prepared statement is executed.");
                    break;
                }
                _prepared.Add(words[1].Text);
                return;
            }
            if (first.Is("EXECUTE"))
            {
                if (words.Count < 2 || !_prepared.Contains(words[1].Text))
                    Add("dynamicSql", "EXECUTE runs a prepared statement that is not prepared in this text, so it cannot be checked for destructive statements.");
                return;
            }
            // WITH ... <verb>: find the main verb at depth 0 after the CTE list. Each CTE body runs even when the main
            // query never reads it, so a DELETE/UPDATE in one is classified like a statement of its own.
            var verbIndex = 0;
            if (first.Is("WITH"))
            {
                var depth = 0;
                for (var i = 1; i < words.Count; i++)
                {
                    var w = words[i];
                    if (w.Text == "(")
                    {
                        // name [(columns)] AS [[NOT] MATERIALIZED] (body)
                        if (depth == 0 && (words[i - 1].Is("AS") || words[i - 1].Is("MATERIALIZED")) && Close(words, i) - i - 1 is > 0 and var n)
                            Statement(words.GetRange(i + 1, n), add, context);
                        depth++;
                    }
                    else if (w.Text == ")") depth--;
                    else if (depth == 0 && (w.Is("SELECT") || w.Is("INSERT") || w.Is("UPDATE") || w.Is("DELETE") || w.Is("MERGE"))) { verbIndex = i; break; }
                }
            }
            var verb = words[verbIndex];
            bool HasTopLevel(string kw, int from)
            {
                var depth = 0;
                for (var i = from; i < words.Count; i++)
                {
                    if (words[i].Text == "(") depth++;
                    else if (words[i].Text == ")") depth--;
                    // PT7-1 safety net: a WHERE that ends the statement is no condition, whatever the tokenizer made of
                    // the text before it.
                    else if (depth == 0 && words[i].Is(kw) && i + 1 < words.Count && words[i + 1].Text != ";") return true;
                }
                return false;
            }
            bool ThenDelete()
            {
                var depth = 0;
                for (var i = verbIndex + 1; i + 1 < words.Count; i++)
                {
                    if (words[i].Text == "(") depth++;
                    else if (words[i].Text == ")") depth--;
                    else if (depth == 0 && words[i].Is("THEN") && words[i + 1].Is("DELETE")) return true;
                }
                return false;
            }
            if (verb.Is("MERGE"))
            {
                if (ThenDelete()) Add("mergeDelete", "MERGE … THEN DELETE removes every row its WHEN clause matches.");
            }
            else if (verb.Is("DELETE") && !HasTopLevel("WHERE", verbIndex)) Add("deleteWithoutWhere", "DELETE without a WHERE clause removes every row.");
            else if (verb.Is("UPDATE") && !HasTopLevel("WHERE", verbIndex)) Add("updateWithoutWhere", "UPDATE without a WHERE clause changes every row.");
            else if (first.Is("DROP")) { HasDdl = true; Add("drop", $"DROP {Next(words, 1)} permanently removes the object."); }
            else if (first.Is("TRUNCATE")) Add("truncate", "TRUNCATE removes every row.");
            else if (first.Is("ALTER")) { HasDdl = true; Add("alter", $"ALTER {Next(words, 1)} changes an existing object."); }
            else if (first.Is("CREATE"))
            {
                HasDdl = true;
                if (words.Count > 2 && words[1].Is("OR") && words[2].Is("REPLACE")) Add("alter", "CREATE OR REPLACE replaces an existing object.");
            }
            else if (first.Is("COMMENT") || first.Is("GRANT") || first.Is("REVOKE")) HasDdl = true;
            else if (first.Is("BEGIN") || (first.Is("START") && words.Count > 1 && words[1].Is("TRANSACTION"))) Begins = true;
            else if (first.Is("COMMIT") || first.Is("ROLLBACK") || first.Is("END") || first.Is("ABORT")) Ends = true;
            else if (first.Text == "\\" && words.Count > 1 && (words[1].Is("c") || words[1].Is("connect")) && words.Count > 2) Use = words[2].Text;
        }

        /// <summary>
        /// The statement EXPLAIN executes, or null when it only plans it. <c>EXPLAIN ANALYZE [VERBOSE] stmt</c> and
        /// <c>EXPLAIN (ANALYZE [true|on|1], ...) stmt</c> execute; <c>EXPLAIN (ANALYZE false) stmt</c> does not.
        /// </summary>
        static List<PgToken>? ExplainTarget(List<PgToken> words)
        {
            var analyze = false;
            var i = 1;
            if (i < words.Count && words[i].Text == "(")
            {
                var close = Close(words, i);
                for (var k = i + 1; k < close; k++)
                    if ((words[k].Is("ANALYZE") || words[k].Is("ANALYSE")) && words[k - 1].Text is "(" or ",")
                        analyze = !(k + 1 < close && (words[k + 1].Is("FALSE") || words[k + 1].Is("OFF") || words[k + 1].Is("NO") || words[k + 1].Text == "0"));
                i = close + 1;
            }
            else
                for (; i < words.Count && (words[i].Is("ANALYZE") || words[i].Is("ANALYSE") || words[i].Is("VERBOSE")); i++)
                    analyze |= !words[i].Is("VERBOSE");
            return analyze && i < words.Count ? words.GetRange(i, words.Count - i) : null;
        }

        /// <summary>
        /// <c>DO [LANGUAGE l] body</c> runs its body immediately (unlike CREATE FUNCTION), so each destructive statement
        /// in it is classified, with its line in the editor. <c>EXECUTE 'literal'</c> is classified as a statement; EXECUTE
        /// of text built at run time (<c>format(…)</c>, a variable, a concatenation) cannot be seen and counts as destructive.
        /// </summary>
        void DoBlock(List<PgToken> words, Action<string, string, int> add)
        {
            PgToken? body = null;
            for (var i = 1; i < words.Count && body is null; i++)
                if (words[i].Kind is PgTokenKind.DollarString or PgTokenKind.String && !words[i - 1].Is("LANGUAGE")) body = words[i];
            if (body is not { } b) return;
            // U&'…' UESCAPE '!' (code audit 2026-11-25, second round): its escapes (!0044 is 'D') are not decoded here, so the
            // body cannot be read — it runs now, unchecked. Fail closed.
            if (UnicodeEscaped(words, words.IndexOf(b)))
            {
                add("dynamicSql", "A DO block written as a Unicode-escaped string (U&'…') cannot be checked for destructive statements. It runs now, inside a DO block.", words[0].Line);
                return;
            }
            var bodyText = b.Kind == PgTokenKind.String ? StringRun(words, words.IndexOf(b), standardStrings, out _) : BodyText(b.Text, standardStrings);
            var tokens = PgTokenizer.Tokenize(bodyText, standardStrings).Where(t => !t.IsTrivia).ToList();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != PgTokenKind.Word || !BodyVerbs.Contains(tokens[i].Text)) continue;
                // Fourth round (C8): FOR r IN <query> LOOP and OPEN c FOR <query> run a query too — DELETE … RETURNING in one.
                var afterIn = i > 0 && tokens[i - 1].Is("IN");
                var afterOpenFor = i >= 3 && tokens[i - 1].Is("FOR") && tokens[i - 3].Is("OPEN");
                if (i > 0 && !BodyStarts.Contains(tokens[i - 1].Text) && !afterIn && !afterOpenFor) continue;
                var end = i;
                for (var depth = 0; end < tokens.Count && !(depth == 0 && (tokens[end].Text == ";" || (afterIn && tokens[end].Is("LOOP")))); end++)
                    depth += tokens[end].Text == "(" ? 1 : tokens[end].Text == ")" ? -1 : 0;
                var line = b.Line + tokens[i].Line - 1;
                if (tokens[i].Is("EXECUTE"))
                {
                    // EXECUTE 'text' [INTO …] [USING …]: one literal is a statement of its own; anything else is built at run time.
                    var literal = i + 1 < end && tokens[i + 1].Kind is PgTokenKind.String or PgTokenKind.DollarString
                                  && (i + 2 == end || tokens[i + 2].Is("INTO") || tokens[i + 2].Is("USING") || tokens[i + 2].Is("STRICT"));
                    if (!literal)
                        add("dynamicSql", "EXECUTE in a DO block runs SQL text built at run time, which cannot be checked for destructive statements. It runs now, inside a DO block.", line);
                    else
                        foreach (var inner in PgTokenizer.Statements(PgTokenizer.Tokenize(BodyText(tokens[i + 1].Text, standardStrings), standardStrings)))
                            if (inner.Where(t => !t.IsTrivia).ToList() is { Count: > 0 } code)
                                Statement(code, (rule, description, _) => add(rule, description, line), " It runs now, as dynamic SQL inside a DO block.");
                    i = end;
                    continue;
                }
                Statement(tokens.GetRange(i, end - i), (rule, description, _) => add(rule, description, line), " It runs now, inside a DO block.");
                i = end;
            }
        }

        /// <summary>Index of the ')' matching the '(' at <paramref name="open"/>, or the end when it is unclosed.</summary>
        static int Close(List<PgToken> words, int open)
        {
            var depth = 0;
            for (var i = open; i < words.Count; i++)
            {
                if (words[i].Text == "(") depth++;
                else if (words[i].Text == ")" && --depth == 0) return i;
            }
            return words.Count;
        }
    }

    /// <summary>
    /// The code inside a dollar-quoted or single-quoted body, without its quotes (a missing close quote is tolerated): a
    /// dollar body as written, a '…' or E'…' body as the server reads the literal (<see cref="LiteralValue"/>).
    /// </summary>
    static string BodyText(string s, bool standardStrings)
    {
        if (s.StartsWith('$'))
        {
            var tag = s[..(s.IndexOf('$', 1) + 1)];
            return s.Length >= 2 * tag.Length && s.EndsWith(tag, StringComparison.Ordinal) ? s[tag.Length..^tag.Length] : s[tag.Length..];
        }
        return LiteralValue(s, standardStrings);
    }

    static string Next(List<PgToken> words, int i) => i < words.Count ? words[i].Text.ToUpperInvariant() : "";

    /// <param name="real">The statement's non-trivia tokens.</param>
    static string Snippet(string text, List<PgToken> real)
    {
        if (real.Count == 0) return "";
        var from = real[0].Offset;
        var to = real[^1].Offset + real[^1].Length;
        var s = string.Join(" ", text[from..to].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    static Func<int, int> LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return o => { var idx = starts.BinarySearch(o); return (idx >= 0 ? idx : ~idx - 1) + 1; };
    }

    public Diagnostic[] Diagnostics(DiagnosticsParams p) => [];

    /// <summary>PostgreSQL formatting runs in the webview with sql-formatter (§10).</summary>
    public FormatResult Format(FormatParams p) => new() { Text = p.Text, Error = "client" };

    public FoldingRange[] Folding(FoldingParams p)
    {
        var tokens = PgTokenizer.Tokenize(p.Text);
        var list = new List<FoldingRange>();
        var stack = new Stack<int>();
        foreach (var t in tokens)
        {
            var endLine = t.Line + t.Text.Count(c => c == '\n');
            if (t.Kind is PgTokenKind.Comment or PgTokenKind.DollarString && endLine > t.Line)
                list.Add(new FoldingRange { StartLine = t.Line, EndLine = endLine, Kind = t.Kind == PgTokenKind.Comment ? "comment" : null });
            if (t.Text == "(") stack.Push(t.Line);
            else if (t.Text == ")" && stack.Count > 0)
            {
                var start = stack.Pop();
                if (t.Line > start) list.Add(new FoldingRange { StartLine = start, EndLine = t.Line });
            }
        }
        foreach (var stmt in PgTokenizer.Statements(tokens))
        {
            var real = stmt.Where(t => !t.IsTrivia).ToList();
            if (real.Count == 0) continue;
            var end = real[^1].Line + real[^1].Text.Count(c => c == '\n');
            if (end > real[0].Line) list.Add(new FoldingRange { StartLine = real[0].Line, EndLine = end });
        }
        return list.GroupBy(r => (r.StartLine, r.EndLine)).Select(g => g.First()).OrderBy(r => r.StartLine).ToArray();
    }

    /// <summary>Tier 2; an empty answer leaves the webview's tier 1 as the fallback (§9).</summary>
    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema, CancellationToken ct) => PgTier2Completion.Complete(p, schema, ct);

    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema, CancellationToken ct) => PgTier2Completion.Signature(p, schema, ct);
}

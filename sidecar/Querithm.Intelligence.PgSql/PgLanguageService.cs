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

    public Classification Classify(string text)
    {
        var tokens = PgTokenizer.Tokenize(text);
        var scan = new Scan();
        foreach (var stmt in PgTokenizer.Statements(tokens))
        {
            var words = stmt.Where(t => !t.IsTrivia).ToList();
            if (words.Count == 0) continue;
            var snippet = Snippet(text, stmt);
            scan.Statement(words, (rule, description, line) => scan.Findings.Add(new DestructiveFinding
            {
                Rule = rule, Statement = snippet, Line = line, Column = 1, Description = description,
            }), "");
        }
        var lineOf = LineIndex(text);
        // One batch per statement: Npgsql would split them anyway, and per-statement execution lets server error
        // positions (relative to the statement) map to exact editor lines.
        var batches = new List<BatchInfo>();
        foreach (var stmt in PgTokenizer.Statements(tokens))
        {
            var real = stmt.Where(t => !t.IsTrivia).ToList();
            if (real.Count == 0) continue;
            var from = real[0].Offset;
            var to = real[^1].Offset + real[^1].Length;
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

    /// <summary>
    /// Classifies one statement (its non-trivia tokens). Recurses into SQL that runs as part of the statement: data-modifying
    /// CTE bodies, the statement under EXPLAIN ANALYZE, the statement a PREPARE stores (it runs on EXECUTE) and the body of
    /// a DO block. Function bodies run later and are skipped. SQL that cannot be seen — EXECUTE of a statement not prepared
    /// in this text, a DO block's EXECUTE of text built at run time — counts as destructive (rule <c>dynamicSql</c>).
    /// </summary>
    sealed class Scan
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

        public void Statement(List<PgToken> words, Action<string, string, int> add, string context)
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
            var tokens = PgTokenizer.Tokenize(BodyText(b.Text)).Where(t => !t.IsTrivia).ToList();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != PgTokenKind.Word || !BodyVerbs.Contains(tokens[i].Text)) continue;
                if (i > 0 && !BodyStarts.Contains(tokens[i - 1].Text)) continue;
                var end = i;
                for (var depth = 0; end < tokens.Count && !(depth == 0 && tokens[end].Text == ";"); end++)
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
                        foreach (var inner in PgTokenizer.Statements(PgTokenizer.Tokenize(BodyText(tokens[i + 1].Text))))
                            if (inner.Where(t => !t.IsTrivia).ToList() is { Count: > 0 } code)
                                Statement(code, (rule, description, _) => add(rule, description, line), " It runs now, as dynamic SQL inside a DO block.");
                    i = end;
                    continue;
                }
                Statement(tokens.GetRange(i, end - i), (rule, description, _) => add(rule, description, line), " It runs now, inside a DO block.");
                i = end;
            }
        }

        /// <summary>The code inside a dollar-quoted or single-quoted body, without its quotes; a missing close quote is tolerated.</summary>
        static string BodyText(string s)
        {
            if (s.StartsWith('$'))
            {
                var tag = s[..(s.IndexOf('$', 1) + 1)];
                return s.Length >= 2 * tag.Length && s.EndsWith(tag, StringComparison.Ordinal) ? s[tag.Length..^tag.Length] : s[tag.Length..];
            }
            var from = s[0] == '\'' ? 1 : 2;
            var to = s.Length > from && s.EndsWith('\'') ? s.Length - 1 : s.Length;
            return s[from..to].Replace("''", "'");
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

    static string Next(List<PgToken> words, int i) => i < words.Count ? words[i].Text.ToUpperInvariant() : "";

    static string Snippet(string text, List<PgToken> stmt)
    {
        var real = stmt.Where(t => !t.IsTrivia).ToList();
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
    public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema) => PgTier2Completion.Complete(p, schema);

    public SignatureHelp Signature(SignatureParams p, ISchemaSource schema) => PgTier2Completion.Signature(p, schema);
}

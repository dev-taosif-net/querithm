using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Intelligence.TSql;

/// <summary>A parsed document: AST, tokens and errors, cached per text (§9: reused across lang.* calls).</summary>
public sealed class ParsedScript
{
    public required string Text { get; init; }
    public required TSqlScript? Script { get; init; }
    public required IList<TSqlParserToken> Tokens { get; init; }
    public required IList<ParseError> Errors { get; init; }
    public required BatchInfo[] Batches { get; init; }
}

public static class TSqlParsing
{
    sealed record CacheEntry(string Text, ParsedScript Parsed);

    /// <summary>
    /// M12: how many documents keep their last parse. Each entry holds a full ScriptDom AST and token stream, the heaviest
    /// per-document state in the sidecar (a 10,000-line procedure retains 24 MiB, GC.GetTotalMemory, 2026-11-03), and it only
    /// pays off while lang.* calls for the same text arrive: diagnostics and folding of the version being edited, folding
    /// again when a tab is shown. 16 covers the visible editors and the tabs switched between; lang.forget drops a closed
    /// tab's or deleted cell's entry at once. Before, every document ever opened kept its parse.
    /// </summary>
    public const int MaxDocuments = 16;

    /// <summary>
    /// Parses by text alone (lang.classify, the run's classification right after the editor parsed the same text): at least
    /// <see cref="MaxDocuments"/> (8a review), so every document still cached keeps its text reachable for the run that
    /// follows its edit. Mostly the same entries as <see cref="ByDoc"/>; dynamic SQL literals are never cached.
    /// </summary>
    public const int MaxTexts = MaxDocuments;

    static readonly LruCache<string, CacheEntry> ByDoc = new(MaxDocuments);
    static readonly LruCache<int, CacheEntry> ByHash = new(MaxTexts);

    /// <summary>Documents holding a parse (tests).</summary>
    internal static int CachedDocuments => ByDoc.Count;
    internal static int CachedTexts => ByHash.Count;
    internal static bool IsCached(string docId) => ByDoc.Contains(docId);
    internal static bool IsCachedText(string text) => ByHash.TryGet(text.GetHashCode(), out var e) && e.Text == text;

    public static TSqlParser NewParser() => new TSql160Parser(initialQuotedIdentifiers: true);

    /// <summary>
    /// Frames open at once past which text is not parsed: parentheses, CASE … END and BEGIN … END blocks, and a run of
    /// NOT / ~ (each nests one level). <see cref="DeepStack"/> holds 64,000 parentheses and 250,000 nested BEGINs or CASEs.
    /// </summary>
    public const int MaxNesting = 10_000;

    /// <summary>
    /// Chain operators (OR, AND, arithmetic, NOT, JOIN, UNION / EXCEPT / INTERSECT) in one statement, plus
    /// <see cref="ControlFlowWeight"/> per IF / WHILE in the batch, past which text is not parsed. A single chain of
    /// 1,000,000 terms parses on <see cref="DeepStack"/>, and 250,000 nested IFs do: one IF level costs about as much
    /// stack as four chain terms (on a pool thread 1,000 nested IFs parsed against 8,000 chain terms), so a chain inside
    /// nested IFs stays within the stack too. A statement's chain ends at its <c>;</c> or at the next statement verb outside
    /// parentheses (INSERT, UPDATE, DECLARE, EXEC …; not SELECT, which UNION chains), a batch's IFs at GO: a script of
    /// many short statements is not refused for its total size (completion audit 2026-11-25 review).
    /// </summary>
    public const int MaxChainOperators = 750_000;

    /// <summary>Chain terms one IF or WHILE counts for (see <see cref="MaxChainOperators"/>).</summary>
    public const int ControlFlowWeight = 4;

    /// <summary>
    /// The ScriptDom parse every caller uses: lexes once, refuses text nested or chained beyond what <see cref="DeepStack"/>
    /// holds (it then reports one parse error saying so — the classifier counts unparsed text as destructive on
    /// production, §6), and parses the tokens on a deep stack. Errors are what <c>Parse(TextReader)</c> reports: the
    /// lexer's when it has any (an unclosed string, bracket or comment), else the parser's.
    /// </summary>
    public static TSqlFragment? ParseText(TSqlParser parser, string text, out IList<ParseError> errors) =>
        ParseText(parser, text, out errors, out _, out _);

    /// <summary><see cref="ParseText(TSqlParser, string, out IList{ParseError})"/>, also giving the tokens it lexed and whether it refused them.</summary>
    static TSqlFragment? ParseText(TSqlParser parser, string text, out IList<ParseError> errors, out IList<TSqlParserToken> tokens, out bool refused)
    {
        if (Lex(parser, text, out var lexErrors) is not { } lexed)
        {
            errors = lexErrors;
            tokens = [];
            refused = true;
            return null;
        }
        tokens = lexed;
        if (TooDeep(lexed) is { } why)
        {
            errors = [new ParseError(0, 0, 1, 1, why)];
            refused = true;
            return null;
        }
        refused = false;
        var (fragment, parseErrors) = DeepStack.Run(() => (parser.Parse(lexed, out var e), e));
        errors = lexErrors.Count > 0 ? lexErrors : parseErrors;
        return fragment;
    }

    /// <summary>
    /// Comment openers (<c>/*</c>) past which <see cref="Lex"/> lexes on <see cref="DeepStack"/>. ScriptDom's lexer recurses
    /// once per nested comment, and one comment is one token, so <see cref="TooDeep(IList{TSqlParserToken})"/> never sees
    /// the depth. Measured 2026-11-26, ScriptDom 180.140.0, Windows: on a 1 MB stack 2,000 nested comments lex and 5,000
    /// overflow (ending the process); on DeepStack 10,000 lex in 84 ms and 1,000,000 in 14.5 s. Text with fewer openers
    /// cannot nest deeper than this, so it lexes inline with no thread start.
    /// </summary>
    public const int SmallStackComments = 200;

    /// <summary>
    /// The one way to lex T-SQL text: ScriptDom's token stream, lexed on <see cref="DeepStack"/> when the text holds more
    /// than <see cref="SmallStackComments"/> comment openers, or null (with one error saying why) when its comments nest
    /// more than <see cref="MaxNesting"/> levels deep. Every caller that lexes text it did not write goes through here.
    /// </summary>
    public static IList<TSqlParserToken>? Lex(TSqlParser parser, string text, out IList<ParseError> errors)
    {
        if (!MoreCommentOpeners(text, SmallStackComments)) return parser.GetTokenStream(new StringReader(text), out errors);
        if (CommentNesting(text) > MaxNesting)
        {
            errors = [new ParseError(0, 0, 1, 1, $"This script nests comments (/* inside /* … */) more than {MaxNesting:N0} levels deep, too deep to analyse.")];
            return null;
        }
        var (tokens, lexErrors) = DeepStack.Run(() => (parser.GetTokenStream(new StringReader(text), out var e), e));
        errors = lexErrors;
        return tokens;
    }

    /// <summary>Whether <paramref name="text"/> holds more than <paramref name="limit"/> <c>/*</c> (stops counting there).</summary>
    static bool MoreCommentOpeners(string text, int limit)
    {
        var count = 0;
        for (var i = text.IndexOf("/*", StringComparison.Ordinal); i >= 0; i = text.IndexOf("/*", i + 2, StringComparison.Ordinal))
            if (++count > limit) return true;
        return false;
    }

    /// <summary>
    /// How deep the text's block comments nest, by the lexer's own rules on characters: outside a comment a <c>--</c>
    /// comment, a <c>'…'</c> string, a <c>"…"</c> or <c>[…]</c> name (doubled closers escape) hide <c>/*</c>; inside one
    /// only <c>/*</c> and <c>*/</c> count. An unclosed string or name runs to the end, as ScriptDom reads it.
    /// </summary>
    internal static int CommentNesting(string text)
    {
        int depth = 0, max = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (depth > 0)
            {
                if (c == '/' && next == '*') { if (++depth > max) max = depth; i++; }
                else if (c == '*' && next == '/') { depth--; i++; }
                continue;
            }
            switch (c)
            {
                case '/' when next == '*':
                    depth = 1;
                    if (max == 0) max = 1;
                    i++;
                    break;
                case '-' when next == '-':
                    // ScriptDom ends a line comment at a lone \r too (measured 2026-11-26): `--x\r/* …` opens a comment.
                    i = text.IndexOfAny(['\r', '\n'], i + 2);
                    if (i < 0) return max;
                    break;
                case '\'' or '"' or '[':
                    var close = c == '[' ? ']' : c;
                    for (i++; ; i += 2)
                    {
                        i = text.IndexOf(close, i);
                        if (i < 0) return max;
                        if (i + 1 >= text.Length || text[i + 1] != close) break; // a doubled closer escapes
                    }
                    break;
            }
        }
        return max;
    }

    /// <summary>Why <paramref name="tokens"/> are too deep to parse safely, or null.</summary>
    public static string? TooDeep(IList<TSqlParserToken> tokens) => TooDeep(tokens, MaxNesting, MaxChainOperators);

    /// <summary><see cref="TooDeep(IList{TSqlParserToken})"/> with other limits (tests use small ones).</summary>
    /// <param name="controlWeight">
    /// Chain terms one IF / WHILE counts for: <see cref="ControlFlowWeight"/> for the parse (nested IFs cost stack); 0 for
    /// format, whose cost is a chain's length, not how many IFs a batch has (review 2026-11-26: a 5,001-line seed script
    /// of IF NOT EXISTS … INSERT was refused).
    /// </param>
    internal static string? TooDeep(IList<TSqlParserToken> tokens, int maxNesting, long maxChain, int controlWeight = ControlFlowWeight)
    {
        // Open frames: parentheses, CASE and BEGIN blocks (both closed by END), and the current run of NOT / ~.
        int parens = 0, blocks = 0, unary = 0;
        // The current statement's chain terms, and the batch's IF / WHILE count.
        long chain = 0, control = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var type = tokens[i].TokenType;
            if (type is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment) continue;
            // A run of prefix operators nests one level each. Unary + / - measured cheap (740,000 parse and walk on the deep
            // stack, 2026-11-26), but no real script holds 10,000 in a row, so they count too; a binary minus has an
            // operand before it, which ends the run.
            unary = type is TSqlTokenType.Not or TSqlTokenType.Tilde or TSqlTokenType.Plus or TSqlTokenType.Minus ? unary + 1 : 0;
            switch (type)
            {
                case TSqlTokenType.LeftParenthesis: parens++; break;
                case TSqlTokenType.RightParenthesis: if (parens > 0) parens--; break;
                case TSqlTokenType.Case: blocks++; break;
                case TSqlTokenType.Begin when OpensBlock(tokens, i): blocks++; break;
                case TSqlTokenType.End when !EndsConversation(tokens, i): if (blocks > 0) blocks--; break;
                case TSqlTokenType.If or TSqlTokenType.While: control++; break;
                // A statement's chain ends at its ';' (none inside parentheses), the batch's IFs at GO (batches parse apart).
                case TSqlTokenType.Semicolon when parens == 0: chain = 0; break;
                // T-SQL statements rarely end in ';': a verb that cannot sit between two operands of one chain starts a
                // new count too (a 10 MB generated INSERT batch without ';' was refused whole — review 2026-11-26). Not
                // SELECT: UNION / EXCEPT / INTERSECT chain SELECTs into one left-deep tree.
                // UPDATE( is a trigger's UPDATE(column) test inside an expression: `IF UPDATE(a) OR UPDATE(b) OR …` is one chain.
                case TSqlTokenType.Update when parens == 0 && NextCode(tokens, i)?.TokenType == TSqlTokenType.LeftParenthesis:
                    break;
                case TSqlTokenType.Insert or TSqlTokenType.Delete or TSqlTokenType.Update or TSqlTokenType.Merge or TSqlTokenType.Declare
                    or TSqlTokenType.Set or TSqlTokenType.Exec or TSqlTokenType.Execute or TSqlTokenType.Print or TSqlTokenType.Raiserror
                    or TSqlTokenType.Create or TSqlTokenType.Alter or TSqlTokenType.Drop or TSqlTokenType.Truncate or TSqlTokenType.Return
                    or TSqlTokenType.Use when parens == 0:
                    chain = 0;
                    break;
                case TSqlTokenType.Go: chain = 0; control = 0; break;
                case TSqlTokenType.Or or TSqlTokenType.And or TSqlTokenType.Not or TSqlTokenType.Tilde or TSqlTokenType.Join
                    or TSqlTokenType.Plus or TSqlTokenType.Minus or TSqlTokenType.Star or TSqlTokenType.Divide or TSqlTokenType.PercentSign
                    or TSqlTokenType.Ampersand or TSqlTokenType.VerticalLine or TSqlTokenType.Circumflex
                    or TSqlTokenType.Union or TSqlTokenType.Except or TSqlTokenType.Intersect
                    // CROSS / OUTER APPLY and PIVOT / UNPIVOT nest left-deep like JOIN (round-3 review 2026-11-26: 200,000
                    // APPLYs ended a 16 MiB stack); LEFT OUTER JOIN counts twice, which only refuses earlier.
                    or TSqlTokenType.Cross or TSqlTokenType.Outer or TSqlTokenType.Pivot or TSqlTokenType.Unpivot:
                    chain++;
                    break;
            }
            if (parens + blocks + unary > maxNesting)
                return $"This script nests parentheses, CASE, BEGIN … END and NOT more than {maxNesting:N0} levels deep, too deep to analyse.";
            if (chain + controlWeight * control > maxChain)
                return $"This script has a statement with more than {maxChain:N0} operators (OR, AND, +, UNION, JOIN, IF and similar), too many to analyse.";
        }
        return null;
    }

    /// <summary>
    /// Whether the BEGIN at <paramref name="i"/> opens a block that an END closes: not BEGIN TRAN / TRANSACTION,
    /// BEGIN DISTRIBUTED TRAN, BEGIN DIALOG [CONVERSATION] or BEGIN CONVERSATION TIMER, which are statements.
    /// </summary>
    static bool OpensBlock(IList<TSqlParserToken> tokens, int i)
    {
        var next = NextCode(tokens, i);
        if (next == null) return true;
        if (next.TokenType is TSqlTokenType.Tran or TSqlTokenType.Transaction or TSqlTokenType.Distributed) return false;
        return !(next.TokenType == TSqlTokenType.Identifier
                 && (next.Text.Equals("DIALOG", StringComparison.OrdinalIgnoreCase) || next.Text.Equals("CONVERSATION", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Whether the END at <paramref name="i"/> is the statement END CONVERSATION, which closes no block.</summary>
    static bool EndsConversation(IList<TSqlParserToken> tokens, int i) =>
        NextCode(tokens, i) is { TokenType: TSqlTokenType.Identifier } next && next.Text.Equals("CONVERSATION", StringComparison.OrdinalIgnoreCase);

    static TSqlParserToken? NextCode(IList<TSqlParserToken> tokens, int i)
    {
        for (var j = i + 1; j < tokens.Count; j++)
            if (tokens[j].TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
                return tokens[j];
        return null;
    }

    /// <summary>Parses, reusing the last parse of the same docId when the text is unchanged.</summary>
    public static ParsedScript Parse(string text, string? docId = null)
    {
        if (docId != null && ByDoc.TryGet(docId, out var hit) && hit.Text == text) return hit.Parsed;
        var h = text.GetHashCode();
        if (ByHash.TryGet(h, out var byHash) && byHash.Text == text)
        {
            if (docId != null) ByDoc.Set(docId, byHash);
            return byHash.Parsed;
        }
        var parsed = ParseUncached(text);
        var entry = new CacheEntry(text, parsed);
        if (docId != null) ByDoc.Set(docId, entry);
        ByHash.Set(h, entry);
        return parsed;
    }

    /// <summary>
    /// A parse no cache keeps: the literal text of dynamic SQL inside a script (8a review: each one took a cache slot, so a
    /// script with many EXEC('…') pushed every editor's parse out).
    /// </summary>
    public static ParsedScript ParseUncached(string text)
    {
        var parser = NewParser();
        // One tokenization: the parse's own token stream also drives batch splitting (also when the parse failed or was
        // refused: before 2026-11-25 the text was lexed a second time then).
        var script = ParseText(parser, text, out var errors, out var tokens, out var refused) as TSqlScript;
        if (script?.ScriptTokenStream is { } stream) tokens = stream;
        // Text the lexer refused (comments nested too deep) has no tokens: it runs as one batch, unsplit.
        var batches = refused && tokens.Count == 0 ? WholeBatch(text) : SplitBatches(text, tokens);
        // A refused text stays refused with its GO counts blanked: the limits count the same tokens.
        if (errors.Count > 0 && !refused && batches.Any(b => b.RepeatCount > 1))
        {
            // ScriptDom does not accept "GO n": blank the count (same length, so offsets and lines stay valid) and reparse.
            var chars = text.ToCharArray();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].TokenType != TSqlTokenType.Go) continue;
                for (var j = i + 1; j < tokens.Count; j++)
                {
                    var n = tokens[j];
                    if (n.TokenType == TSqlTokenType.WhiteSpace && !n.Text.Contains('\n')) continue;
                    if (n.TokenType == TSqlTokenType.Integer) for (var k = 0; k < n.Text.Length; k++) chars[n.Offset + k] = ' ';
                    break;
                }
            }
            script = ParseText(parser, new string(chars), out errors) as TSqlScript;
            tokens = script?.ScriptTokenStream ?? tokens;
        }
        return new ParsedScript { Text = text, Script = script, Tokens = tokens, Errors = errors, Batches = batches };
    }

    /// <summary>A closed tab or deleted notebook cell (lang.forget): its parse goes now, not when the LRU reaches it.</summary>
    public static void Forget(string docId)
    {
        if (!ByDoc.TryGet(docId, out var entry)) return;
        ByDoc.Remove(docId);
        // The same parse is also reachable by its text: drop it there too (a document with the same text keeps its own entry).
        ByHash.RemoveWhere((_, e) => ReferenceEquals(e, entry));
    }

    /// <summary>
    /// Splits on GO batch-separator tokens (§7 step 2) — never a regex, so GO inside strings or comments is ignored.
    /// "GO n" repeats the batch n times.
    /// </summary>
    public static BatchInfo[] SplitBatches(string text, IList<TSqlParserToken> tokens)
    {
        var batches = new List<BatchInfo>();
        var start = 0;
        var lineAt = LineIndex(text);
        // Batches are added in text order, so the scan resumes where the last one stopped: before 2026-11-25 every batch
        // scanned from the first token, quadratic in the number of GOs.
        var scan = 0;
        bool HasCode(int from, int to)
        {
            while (scan < tokens.Count && tokens[scan].Offset < from) scan++;
            for (var i = scan; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Offset >= to) break;
                if (t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile))
                    return true;
            }
            return false;
        }
        void Add(int from, int to, int repeat)
        {
            if (to <= from || !HasCode(from, to)) return;
            // Trim leading/trailing whitespace so reported lines match the editor.
            while (from < to && char.IsWhiteSpace(text[from])) from++;
            while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
            batches.Add(new BatchInfo
            {
                Index = batches.Count, StartOffset = from, Length = to - from, StartLine = lineAt(from), EndLine = lineAt(Math.Max(from, to - 1)),
                RepeatCount = repeat,
            });
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.TokenType != TSqlTokenType.Go) continue;
            var repeat = 1;
            var end = t.Offset + t.Text.Length;
            // Optional count on the same line: GO 5
            for (var j = i + 1; j < tokens.Count; j++)
            {
                var n = tokens[j];
                if (n.TokenType == TSqlTokenType.WhiteSpace && !n.Text.Contains('\n')) continue;
                if (n.TokenType == TSqlTokenType.Integer && int.TryParse(n.Text, out var count) && count > 0)
                {
                    repeat = count;
                    end = n.Offset + n.Text.Length;
                    i = j;
                }
                break;
            }
            Add(start, t.Offset, repeat);
            start = end;
        }
        Add(start, text.Length, 1);
        return batches.ToArray();
    }

    static BatchInfo[] WholeBatch(string text)
    {
        int from = 0, to = text.Length;
        while (from < to && char.IsWhiteSpace(text[from])) from++;
        while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
        if (from == to) return [];
        var lineAt = LineIndex(text);
        return [new BatchInfo { Index = 0, StartOffset = from, Length = to - from, StartLine = lineAt(from), EndLine = lineAt(to - 1), RepeatCount = 1 }];
    }

    /// <summary>offset → 1-based line, by binary search over line starts.</summary>
    public static Func<int, int> LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return offset =>
        {
            var idx = starts.BinarySearch(offset);
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        };
    }

    public static int EndLine(TSqlFragment f)
    {
        if (f.ScriptTokenStream != null && f.LastTokenIndex >= 0 && f.LastTokenIndex < f.ScriptTokenStream.Count)
        {
            var t = f.ScriptTokenStream[f.LastTokenIndex];
            return t.Line + t.Text.Count(c => c == '\n');
        }
        return f.StartLine;
    }

    public static string Snippet(string text, TSqlFragment f, int max = 200)
    {
        if (f.StartOffset < 0 || f.StartOffset >= text.Length) return "";
        var len = Math.Min(f.FragmentLength, text.Length - f.StartOffset);
        var s = text.Substring(f.StartOffset, len).Trim();
        s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > max ? s[..max] + "…" : s;
    }
}

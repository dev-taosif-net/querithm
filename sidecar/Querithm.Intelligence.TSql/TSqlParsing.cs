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
        // One tokenization: the parse's own token stream also drives batch splitting.
        var script = parser.Parse(new StringReader(text), out var errors) as TSqlScript;
        IList<TSqlParserToken> tokens = script?.ScriptTokenStream ?? parser.GetTokenStream(new StringReader(text), out _);
        var batches = SplitBatches(text, tokens);
        if (errors.Count > 0 && batches.Any(b => b.RepeatCount > 1))
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
            script = parser.Parse(new StringReader(new string(chars)), out errors) as TSqlScript;
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
        bool HasCode(int from, int to)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Offset < from) continue;
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

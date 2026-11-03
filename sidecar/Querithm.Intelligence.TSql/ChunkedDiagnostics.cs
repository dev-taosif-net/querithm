using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Querithm.Contract;

namespace Querithm.Intelligence.TSql;

/// <summary>
/// Diagnostics for large documents (§9, bench metric 6). A single ScriptDom parse of a 12k-line procedure costs
/// ~200–400 ms, so large texts are cut at statement boundaries (a ';' outside parentheses, strings, comments and
/// BEGIN/CASE blocks), each chunk is wrapped in the BEGIN/END blocks it sits in, parsed in parallel, and cached by
/// text. Cut points depend on the content of the statement before them, not on their offset, so after an edit the
/// boundaries resync at the next cut and only the edited chunk is parsed again.
/// </summary>
public static class ChunkedDiagnostics
{
    public const int MinLength = 100_000;
    const int MinChunk = 24_000;

    static readonly ConcurrentDictionary<string, ParseError[]> Cache = new(StringComparer.Ordinal);

    public readonly record struct Chunk(int Start, int End, int StartDepth, int EndDepth);

    public static Diagnostic[]? TryDiagnose(string text) => TryDiagnose(text, out _);

    /// <param name="parsed">How many chunks missed the cache and were parsed by this call.</param>
    internal static Diagnostic[]? TryDiagnose(string text, out int parsed)
    {
        parsed = 0;
        if (text.Length < MinLength) return null;
        var chunks = Split(text);
        if (chunks.Count < 2) return null;
        var lineOf = TSqlParsing.LineIndex(text);
        var lineStarts = LineStarts(text);
        var results = new ParseError[chunks.Count][];
        var parseTexts = chunks.Select(c => Wrap(text, c)).ToArray();
        var misses = 0;
        Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            results[i] = Cache.GetOrAdd(parseTexts[i], t =>
            {
                Interlocked.Increment(ref misses);
                TSqlParsing.NewParser().Parse(new StringReader(t), out var errors);
                return errors.ToArray();
            });
        });
        parsed = misses;
        if (Cache.Count > 512) Cache.Clear();
        var list = new List<Diagnostic>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var prefix = 6 * c.StartDepth;
            foreach (var e in results[i])
            {
                var offset = Math.Clamp(c.Start + Math.Max(0, e.Offset - prefix), c.Start, Math.Max(c.Start, c.End - 1));
                var line = lineOf(offset);
                var col = offset - lineStarts[line - 1] + 1;
                list.Add(new Diagnostic { Line = line, Column = col, EndLine = line, EndColumn = col + 1, Message = e.Message, Severity = Severity.Error });
            }
        }
        return list.ToArray();
    }

    /// <summary>The chunk inside the BEGIN blocks it sits in; its text starts <c>6 * StartDepth</c> characters in.</summary>
    public static string Wrap(string text, Chunk c) =>
        string.Concat(Enumerable.Repeat("BEGIN ", c.StartDepth)) + text[c.Start..c.End] + string.Concat(Enumerable.Repeat("\nEND", c.EndDepth));

    static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    /// <summary>Character-level scan for safe split points; never splits inside strings, comments, parentheses or before ELSE.</summary>
    public static List<Chunk> Split(string s) => Split(s, Environment.ProcessorCount);

    internal static List<Chunk> Split(string s, int cores)
    {
        // A power of two, so a small edit leaves the size (and with it every cut point) unchanged.
        var target = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(MinChunk, s.Length / Math.Max(2, cores * 2)));
        var half = (uint)(target / 2);
        var chunks = new List<Chunk>();
        int i = 0, paren = 0, depth = 0, chunkStart = 0, chunkDepth = 0, stmtStart = 0;
        string? pendingWord = null;
        bool lineStart = true;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\n') { lineStart = true; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var d = 0;
                do
                {
                    if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*') { d++; i += 2; }
                    else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/') { d--; i += 2; }
                    else i++;
                } while (i < s.Length && d > 0);
                continue;
            }
            var atLineStart = lineStart;
            lineStart = false;
            if (c == '\'') { i++; while (i < s.Length) { if (s[i] == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') { i += 2; continue; } i++; break; } i++; } continue; }
            if (c == '[') { i++; while (i < s.Length) { if (s[i] == ']') { if (i + 1 < s.Length && s[i + 1] == ']') { i += 2; continue; } i++; break; } i++; } continue; }
            if (c == '"') { i++; while (i < s.Length && s[i] != '"') i++; i++; continue; }
            if (c == '(') { paren++; i++; continue; }
            if (c == ')') { paren = Math.Max(0, paren - 1); i++; continue; }
            if (char.IsLetter(c) || c is '_' or '@' or '#')
            {
                var st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '@' or '#' or '$')) i++;
                var w = s[st..i].ToUpperInvariant();
                if (pendingWord == "BEGIN")
                {
                    // BEGIN TRAN / DISTRIBUTED / DIALOG / CONVERSATION are statements, not blocks.
                    if (w is "TRAN" or "TRANSACTION" or "DISTRIBUTED" or "DIALOG" or "CONVERSATION") depth--;
                }
                else if (pendingWord == "END" && w == "CONVERSATION") depth++;
                pendingWord = null;
                switch (w)
                {
                    case "BEGIN": depth++; pendingWord = "BEGIN"; break;
                    case "CASE": depth++; break;
                    case "END": if (depth > 0) depth--; pendingWord = "END"; break;
                    case "GO" when atLineStart && paren == 0:
                        // A batch separator is always a safe boundary.
                        if (st > chunkStart) chunks.Add(new Chunk(chunkStart, st, chunkDepth, 0));
                        while (i < s.Length && s[i] != '\n') i++;
                        chunkStart = stmtStart = i;
                        depth = 0;
                        chunkDepth = 0;
                        break;
                }
                continue;
            }
            if (c == ';' && paren == 0)
            {
                i++;
                // Past half the target, each statement ends a chunk with probability (its length / half), decided by a
                // hash of its text: chunks average ~target and an edit moves no cut point outside its own chunk.
                // Twice the target forces a cut so one chunk never grows unbounded.
                var len = i - chunkStart;
                var cut = len >= 2 * target || (len >= half && Hash(s, stmtStart, i) % half < (uint)(i - stmtStart));
                stmtStart = i;
                if (cut && !NextWordIs(s, i, "ELSE") && !NextWordIs(s, i, "WHEN") && depth >= 0)
                {
                    chunks.Add(new Chunk(chunkStart, i, chunkDepth, depth));
                    chunkStart = i;
                    chunkDepth = depth;
                }
                continue;
            }
            i++;
        }
        if (s.Length > chunkStart) chunks.Add(new Chunk(chunkStart, s.Length, chunkDepth, 0));
        return chunks;
    }

    /// <summary>FNV-1a over <c>s[start..end]</c> with a murmur3 finalizer to spread the low bits.</summary>
    static uint Hash(string s, int start, int end)
    {
        var h = 2166136261u;
        for (var k = start; k < end; k++) h = (h ^ s[k]) * 16777619u;
        h ^= h >> 16; h *= 0x85ebca6bu; h ^= h >> 13; h *= 0xc2b2ae35u; h ^= h >> 16;
        return h;
    }

    static bool NextWordIs(string s, int i, string word)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i + word.Length <= s.Length && string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
               && (i + word.Length == s.Length || !char.IsLetterOrDigit(s[i + word.Length]));
    }
}

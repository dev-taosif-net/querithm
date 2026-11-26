using System.Collections.Concurrent;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querithm.Intelligence.TSql.Completion;

/// <summary>
/// ScriptDom parses of single statements. Declarations above the cursor (DECLARE, CREATE TABLE #t, the module header)
/// are parsed again on every keystroke, so their parses are cached by exact text. The statement at the cursor is parsed
/// with a placeholder and a different repair each time — its text never comes back — so it is not cached
/// (<c>cache: false</c>): before 2026-11-25 every repair attempt was kept, up to 4,096 ASTs of the statement being edited.
/// </summary>
static class StatementParser
{
    public sealed record Parsed(TSqlScript? Script, int ErrorCount, int FirstErrorOffset)
    {
        public bool Ok => Script != null && ErrorCount == 0;
        public TSqlStatement? First => Script?.Batches.Count > 0 && Script.Batches[0].Statements.Count > 0 ? Script.Batches[0].Statements[0] : null;
    }

    /// <summary>A text this long is parsed but not kept: an AST holds tens of times its text.</summary>
    internal const int MaxCachedText = 32 * 1024;

    /// <summary>All cached texts together (see <see cref="ParseCache"/> for what happens when it is reached).</summary>
    internal const long MaxCachedChars = 512 * 1024;

    const int MaxCachedEntries = 4096;

    /// <summary>An entry unused this long may make room for a new text (an edited-away declaration).</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    static readonly ParseCache Cache = new(MaxCachedEntries, MaxCachedChars, MaxCachedText, StaleAfter, () => Environment.TickCount64);
    [ThreadStatic] static TSqlParser? parser;

    internal static IEnumerable<string> CachedTexts => Cache.Texts;
    internal static long CachedChars => Cache.Chars;

    /// <summary>Empties the shared cache (tests that measure it: other tests' declarations, still in use, fill it).</summary>
    internal static void ClearCache() => Cache.Clear();

    public static Parsed Parse(string text, bool cache = true) => Parse(text, cache ? Cache : null);

    internal static Parsed Parse(string text, ParseCache? cache)
    {
        if (cache != null && cache.TryGet(text, out var hit)) return hit;
        parser ??= TSqlParsing.NewParser();
        Parsed result;
        try
        {
            var script = TSqlParsing.ParseText(parser, text, out var errors) as TSqlScript;
            result = new Parsed(script, errors.Count, errors.Count > 0 ? errors[0].Offset : -1);
        }
        catch
        {
            // Not the text's answer (the deep-stack thread could not start, say): never cached, so the next keystroke tries again.
            return new Parsed(null, 1, 0);
        }
        cache?.Add(text, result);
        return result;
    }
}

/// <summary>
/// Parses by exact text, bounded by entry count and characters. When full, entries unused for <c>staleAfter</c> make
/// room; entries in use are never dropped for a newcomer, which is then not kept. Before 2026-11-25 a full cache was
/// cleared mid-request: when one keystroke's declarations add up to more than the budget, every keystroke cleared it
/// partway and the next started from the top again, so not one of them hit (review B#3). Now the ones admitted first
/// keep hitting. Mutations run under one lock so the character count stays exact.
/// </summary>
sealed class ParseCache(int maxEntries, long maxChars, int maxText, TimeSpan staleAfter, Func<long> clockMs)
{
    sealed class Entry(StatementParser.Parsed parsed, int chars, long now)
    {
        public readonly StatementParser.Parsed Parsed = parsed;
        public readonly int Chars = chars;
        public long LastUsed = now;
    }

    readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly object _gate = new();
    long _chars;
    // The earliest time an entry can have gone stale (under _gate): a full cache of entries in use is not walked again on
    // every miss before then — each miss scanned all 4,096 under the lock (review 2026-11-26). Uses only push it later.
    long _nextSweep = long.MinValue;
    int _sweeps;

    /// <summary>Walks of a full cache looking for stale entries (tests).</summary>
    public int Sweeps => Volatile.Read(ref _sweeps);

    public IEnumerable<string> Texts => _entries.Keys;
    public long Chars => Interlocked.Read(ref _chars);
    public int Count => _entries.Count;

    public bool TryGet(string text, out StatementParser.Parsed parsed)
    {
        if (_entries.TryGetValue(text, out var e))
        {
            Volatile.Write(ref e.LastUsed, clockMs());
            parsed = e.Parsed;
            return true;
        }
        parsed = null!;
        return false;
    }

    public void Add(string text, StatementParser.Parsed parsed)
    {
        if (text.Length > maxText) return;
        var now = clockMs();
        lock (_gate)
        {
            if (_entries.ContainsKey(text)) return;
            if (!Fits(text.Length))
            {
                if (now < _nextSweep) return;
                _sweeps++;
                var stale = (long)staleAfter.TotalMilliseconds;
                var cutoff = now - stale;
                var oldest = long.MaxValue;
                foreach (var (key, e) in _entries)
                {
                    var used = Volatile.Read(ref e.LastUsed);
                    if (used < cutoff && _entries.TryRemove(key, out _)) Interlocked.Add(ref _chars, -e.Chars);
                    else if (used < oldest) oldest = used;
                }
                // Dropped once unused for longer than staleAfter: the oldest kept one goes 1 ms past its mark.
                _nextSweep = oldest == long.MaxValue ? long.MinValue : oldest + stale + 1;
                if (!Fits(text.Length)) return;
            }
            if (_entries.TryAdd(text, new Entry(parsed, text.Length, now))) Interlocked.Add(ref _chars, text.Length);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Interlocked.Exchange(ref _chars, 0);
            _nextSweep = long.MinValue;
        }
    }

    bool Fits(int length) => _entries.Count < maxEntries && Interlocked.Read(ref _chars) + length <= maxChars;
}

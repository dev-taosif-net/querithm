using System.Collections.Concurrent;

namespace Querithm.Intelligence.PgSql.Completion;

/// <summary>
/// A fact found by scanning a whole document (the USE / SET schema switches of DuckDB and MySQL), kept per document while
/// its text is unchanged: rescanning a 10,000-statement script on every completion call made tier 2 miss its target
/// (bench #5.duckdb 264 ms p95; MySQL's scan never finished, see MySqlSplitter).
/// </summary>
public static class DocumentScan<T>
{
    static readonly ConcurrentDictionary<string, (string Text, T Value)> Cache = new(StringComparer.Ordinal);

    // Each instantiation registers its cache once, so lang.forget reaches every kind of scan (M12). Keys end in "|docId".
    static DocumentScan() => DocumentCaches.Register(docId =>
    {
        var suffix = "|" + docId;
        foreach (var key in Cache.Keys)
            if (key.EndsWith(suffix, StringComparison.Ordinal)) Cache.TryRemove(key, out _);
    }, docId => Cache.Keys.Any(k => k.EndsWith("|" + docId, StringComparison.Ordinal)));

    public static T Get(string key, string text, Func<string, T> scan)
    {
        if (Cache.TryGetValue(key, out var c) && string.Equals(c.Text, text, StringComparison.Ordinal)) return c.Value;
        var value = scan(text);
        if (Cache.Count > 64) Cache.Clear();
        Cache[key] = (text, value);
        return value;
    }
}

/// <summary>
/// M12, lang.forget: the per-document caches of the PostgreSQL-family completion (PostgreSQL, DuckDB, SQLite, MySQL share
/// it): <see cref="PgDocument"/> and every <see cref="DocumentScan{T}"/>.
/// </summary>
public static class DocumentCaches
{
    static readonly object Gate = new();
    static readonly List<(Action<string> Forget, Func<string, bool> Holds)> Scans = [];

    internal static void Register(Action<string> forget, Func<string, bool> holds)
    {
        lock (Gate) Scans.Add((forget, holds));
    }

    public static void Forget(string docId)
    {
        PgDocument.Forget(docId);
        List<(Action<string> Forget, Func<string, bool> Holds)> scans;
        lock (Gate) scans = [.. Scans];
        foreach (var s in scans) s.Forget(docId);
    }

    /// <summary>Whether any cache still holds the document (tests).</summary>
    internal static bool Holds(string docId)
    {
        if (PgDocument.IsCached(docId)) return true;
        lock (Gate) return Scans.Any(s => s.Holds(docId));
    }
}

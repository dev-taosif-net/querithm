using System.Security.Cryptography;
using System.Text;
using Querithm.Contract;

namespace Querithm.Core.Monitoring;

/// <summary>
/// One monitor's query texts (plan "Payload"): TextId = content hash of the exact 4 KB preview, never a server key.
/// Live texts (named by the latest snapshot) are never evicted; history references (B6) keep texts while their ticks are
/// retained; unreferenced texts sit in an LRU capped at 4 MB. The store never grows with uptime.
/// Sizes are the memory a text holds (UTF-16 characters plus <see cref="EntryOverheadBytes"/>), not its UTF-8 length:
/// a short statement holds ~6× its UTF-8 length on the heap, so a cap on UTF-8 bytes bounded nothing it claimed to.
/// </summary>
public sealed class TextStore
{
    public const int PreviewBytes = 4096;
    public const long UnreferencedCapBytes = 4L * 1024 * 1024;
    /// <summary>
    /// Heap per text beyond its characters: string header, id string, entry, LRU node, dictionary and sent-set slots with
    /// their growth slack (measured 300 B at 20,000 texts; up to ~50 B more right after a resize — MonitorMemoryTests).
    /// </summary>
    public const int EntryOverheadBytes = 360;

    sealed class Entry(string text, int bytes)
    {
        public string Text { get; } = text;
        public int Bytes { get; } = bytes;
        public int HistoryRefs { get; set; }
        public bool Live { get; set; }
        public LinkedListNode<string>? Lru { get; set; }
    }

    readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    readonly LinkedList<string> _lru = new();
    readonly HashSet<string> _sent = new(StringComparer.Ordinal);
    HashSet<string> _live = new(StringComparer.Ordinal);
    readonly Dictionary<SessionKey, (string Text, string Id)> _lastBySession = new();
    long _unrefBytes;

    public long UnreferencedCap { get; init; } = UnreferencedCapBytes;

    /// <summary>
    /// L4: masks secret literals in a text before it is kept (the engine's <see cref="Contract.Providers.IDialectService.RedactSecrets"/>):
    /// another session's <c>ALTER ROLE … PASSWORD '…'</c> must not sit in the monitor, its history or an export. Runs on the
    /// 4 KB preview, so its cost is bounded per text.
    /// </summary>
    public Func<string, string>? Redact { get; set; }
    /// <summary>Hashes computed (an unchanged session text is compared, not rehashed — S17).</summary>
    public long HashCount { get; private set; }
    public int Count => _byId.Count;
    public long UnreferencedBytes => _unrefBytes;
    public long LiveBytes => _byId.Values.Where(e => e.Live).Sum(e => (long)e.Bytes);
    public long HistoryBytes => _byId.Values.Where(e => e.HistoryRefs > 0 && !e.Live).Sum(e => (long)e.Bytes);
    public long TotalBytes => _byId.Values.Sum(e => (long)e.Bytes);

    /// <summary>The first 4 KB of UTF-8, cut on a character boundary (never a split surrogate pair — S5).</summary>
    public static string Preview(string text)
    {
        if (text.Length <= PreviewBytes / 4 || Encoding.UTF8.GetByteCount(text) <= PreviewBytes) return text;
        var bytes = 0;
        var i = 0;
        while (i < text.Length)
        {
            var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var n = Encoding.UTF8.GetByteCount(text.AsSpan(i, len));
            if (bytes + n > PreviewBytes) break;
            bytes += n;
            i += len;
        }
        return text[..i];
    }

    public static string IdOf(string preview) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview)).AsSpan(0, 16));

    public static bool IsValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Adds a text (not yet referenced) and returns its id.</summary>
    public string Intern(string text)
    {
        var preview = Preview(text);
        if (Redact != null) preview = Redact(preview);
        HashCount++;
        var id = IdOf(preview);
        Add(id, preview);
        return id;
    }

    /// <summary>Like <see cref="Intern"/>, but reuses the id when the session's text equals the previous tick's (ordinal).</summary>
    public string InternForSession(SessionKey key, string text)
    {
        if (_lastBySession.TryGetValue(key, out var last) && string.Equals(last.Text, text, StringComparison.Ordinal) && _byId.ContainsKey(last.Id))
            return last.Id;
        var id = Intern(text);
        _lastBySession[key] = (text, id);
        return id;
    }

    void Add(string id, string preview)
    {
        if (_byId.ContainsKey(id)) return;
        var e = new Entry(preview, 2 * preview.Length + EntryOverheadBytes);
        _byId[id] = e;
        ToLru(id, e);
    }

    void ToLru(string id, Entry e)
    {
        if (e.Lru != null || e.Live || e.HistoryRefs > 0) return;
        e.Lru = _lru.AddLast(id);
        _unrefBytes += e.Bytes;
    }

    void FromLru(Entry e)
    {
        if (e.Lru == null) return;
        _lru.Remove(e.Lru);
        e.Lru = null;
        _unrefBytes -= e.Bytes;
    }

    /// <summary>
    /// Ends a tick: <paramref name="liveIds"/> become the live set; returns the live texts not sent while in the store
    /// (null when none). Texts that left the live set without history references move to the LRU; the LRU is trimmed.
    /// </summary>
    public Dictionary<string, string>? Commit(IEnumerable<string> liveIds, IReadOnlyCollection<SessionKey>? liveSessions = null)
    {
        var next = new HashSet<string>(liveIds, StringComparer.Ordinal);
        Dictionary<string, string>? send = null;
        foreach (var id in next)
        {
            if (!_byId.TryGetValue(id, out var e)) continue;
            FromLru(e);
            e.Live = true;
            if (_sent.Add(id)) (send ??= new(StringComparer.Ordinal))[id] = e.Text;
        }
        foreach (var id in _live)
        {
            if (next.Contains(id) || !_byId.TryGetValue(id, out var e)) continue;
            e.Live = false;
            ToLru(id, e);
        }
        _live = next;
        Trim();
        if (liveSessions != null)
        {
            var keep = liveSessions as HashSet<SessionKey> ?? [.. liveSessions];
            foreach (var k in _lastBySession.Keys.Where(k => !keep.Contains(k)).ToList()) _lastBySession.Remove(k);
        }
        return send;
    }

    void Trim()
    {
        while (_unrefBytes > UnreferencedCap && _lru.First is { } oldest)
        {
            var e = _byId[oldest.Value];
            FromLru(e);
            _byId.Remove(oldest.Value);
            _sent.Remove(oldest.Value); // evicted: if it appears again it is new to the store and sent again (M11)
        }
    }

    /// <summary>B6: a retained history tick names these texts.</summary>
    public void AddHistoryRefs(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var e))
            {
                e.HistoryRefs++;
                FromLru(e);
            }
    }

    /// <summary>B6: a history tick was evicted; texts named by nothing else go to the LRU.</summary>
    public void RemoveHistoryRefs(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (_byId.TryGetValue(id, out var e) && e.HistoryRefs > 0)
            {
                e.HistoryRefs--;
                ToLru(id, e);
            }
        Trim();
    }

    public int HistoryBytesOf(string id) => _byId.TryGetValue(id, out var e) ? e.Bytes : 0;

    public (Dictionary<string, string> Found, string[] Missing) Get(IEnumerable<string> ids)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var id in ids.Distinct())
        {
            if (_byId.TryGetValue(id, out var e)) found[id] = e.Text;
            else missing.Add(id);
        }
        return (found, missing.ToArray());
    }

    public string? TryGet(string id) => _byId.TryGetValue(id, out var e) ? e.Text : null;
}

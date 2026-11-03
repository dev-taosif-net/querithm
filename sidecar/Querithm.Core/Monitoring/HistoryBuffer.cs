using System.IO.Compression;
using System.Text.Json;
using Querithm.Contract;

namespace Querithm.Core.Monitoring;

/// <summary>
/// B6: the last N minutes of snapshots in memory (1–60 min, default 10; capped at 50 MB including the texts only
/// history still names). Sessions are stored as diffs between ticks with a keyframe every 10 ticks, each part as
/// Deflate-compressed JSON — a storage choice, independent of the wire format: retained ticks hold no object graphs,
/// and the cap counts the bytes actually held (object graphs needed ~0.3 MB per 500-session tick, metric 29).
/// Oldest ticks go first; the cap being hit is reported.
/// </summary>
public sealed class HistoryBuffer
{
    public const long DefaultCapBytes = 50L * 1024 * 1024;
    const int KeyframeEvery = 10;
    /// <summary>Per-tick bookkeeping outside the compressed parts (the tick object, its arrays, list node).</summary>
    const long TickOverhead = 256;

    sealed class Tick
    {
        public long Seq;
        public long At;
        public long Wall;
        public bool GapBefore;
        public byte[]? Keyframe;
        public byte[]? Upserts;
        public byte[]? Removed;
        public required byte[] Rest;
        public required string[] TextIds;
        public long Bytes;
        public int Sessions;
        public int Blocked;
    }

    readonly LinkedList<Tick> _ticks = new();
    readonly TextStore _texts;
    Dictionary<SessionKey, ActivityRow> _last = new();
    long _bytes;
    int _sinceKeyframe;
    bool _gapPending;

    public HistoryBuffer(TextStore texts, int retentionMinutes = 10, long capBytes = DefaultCapBytes)
    {
        _texts = texts;
        RetentionMinutes = retentionMinutes;
        CapBytes = capBytes;
    }

    public int RetentionMinutes { get; set; }
    public long CapBytes { get; }
    public bool CapHit { get; private set; }
    public int Count => _ticks.Count;
    /// <summary>Tick bytes plus the texts only history references.</summary>
    public long Bytes => _bytes + _texts.HistoryBytes;

    /// <summary>The monitor lost its connection: the next tick is marked as following a gap (H4, never interpolated).</summary>
    public void MarkGap() => _gapPending = true;

    public void Add(MonitorSnapshot s, long atMs)
    {
        var rows = s.Sessions ?? [];
        var current = new Dictionary<SessionKey, ActivityRow>(rows.Length);
        foreach (var r in rows) current.TryAdd(r.Key, r);
        var keyframe = _ticks.Count == 0 || _sinceKeyframe >= KeyframeEvery - 1;
        var t = new Tick
        {
            Seq = s.Seq, At = atMs, Wall = s.At, GapBefore = _gapPending,
            Rest = Pack(s with { Sessions = null, Texts = null, NewDeadlocks = null, Capabilities = null }),
            TextIds = rows.Select(r => r.TextId).Concat((s.TopQueries ?? []).Select(q => q.TextId)).OfType<string>().Distinct().ToArray(),
            Sessions = rows.Length,
            Blocked = s.Blocking?.BlockedCount ?? rows.Count(r => r.BlockedBy.Length > 0),
        };
        _gapPending = false;
        if (keyframe)
        {
            t.Keyframe = Pack(rows);
            _sinceKeyframe = 0;
        }
        else
        {
            t.Upserts = Pack(rows.Where(r => !_last.TryGetValue(r.Key, out var old) || !RowEquals(old, r)).ToArray());
            t.Removed = Pack(_last.Keys.Where(k => !current.ContainsKey(k)).ToArray());
            _sinceKeyframe++;
        }
        _last = current;
        t.Bytes = Size(t);
        _ticks.AddLast(t);
        _bytes += t.Bytes;
        _texts.AddHistoryRefs(t.TextIds);
        Trim(atMs);
    }

    void Trim(long nowMs)
    {
        var horizon = nowMs - RetentionMinutes * 60_000L;
        while (_ticks.First is { } oldest && (oldest.Value.At < horizon || (Bytes > CapBytes && _ticks.Count > 1)))
        {
            if (Bytes > CapBytes && oldest.Value.At >= horizon) CapHit = true;
            Drop();
        }
    }

    void Drop()
    {
        var oldest = _ticks.First!.Value;
        var oldestRows = oldest.Keyframe != null ? Unpack<ActivityRow[]>(oldest.Keyframe) : null;
        _ticks.RemoveFirst();
        _bytes -= oldest.Bytes;
        _texts.RemoveHistoryRefs(oldest.TextIds);
        // The new oldest tick becomes a keyframe so every retained tick can still be rebuilt.
        if (_ticks.First is { } next && next.Value.Keyframe == null)
        {
            var rows = Apply(oldestRows ?? [], next.Value);
            _bytes -= next.Value.Bytes;
            next.Value.Keyframe = Pack(rows.Values.ToArray());
            next.Value.Upserts = null;
            next.Value.Removed = null;
            next.Value.Bytes = Size(next.Value);
            _bytes += next.Value.Bytes;
        }
    }

    static Dictionary<SessionKey, ActivityRow> Apply(ActivityRow[] previous, Tick t)
    {
        var rows = previous.ToDictionary(r => r.Key);
        if (t.Removed != null) foreach (var k in Unpack<SessionKey[]>(t.Removed)) rows.Remove(k);
        if (t.Upserts != null) foreach (var r in Unpack<ActivityRow[]>(t.Upserts)) rows[r.Key] = r;
        return rows;
    }

    /// <summary>Sessions at <paramref name="seq"/>, from the nearest keyframe at or before it (at most 9 diffs).</summary>
    ActivityRow[] Rebuild(long seq)
    {
        var start = KeyframeAtOrBefore(seq);
        Dictionary<SessionKey, ActivityRow>? rows = null;
        foreach (var t in _ticks)
        {
            if (t.Seq < start) continue;
            if (t.Seq > seq) break;
            if (t.Keyframe != null) rows = Unpack<ActivityRow[]>(t.Keyframe).ToDictionary(r => r.Key);
            else if (rows != null)
            {
                if (t.Removed != null) foreach (var k in Unpack<SessionKey[]>(t.Removed)) rows.Remove(k);
                if (t.Upserts != null) foreach (var r in Unpack<ActivityRow[]>(t.Upserts)) rows[r.Key] = r;
            }
        }
        return rows?.Values.ToArray() ?? [];
    }

    long KeyframeAtOrBefore(long seq)
    {
        long found = -1;
        foreach (var t in _ticks)
        {
            if (t.Seq > seq) break;
            if (t.Keyframe != null) found = t.Seq;
        }
        return found;
    }

    public HistoryTick[] Ticks() => _ticks.Select(t => new HistoryTick { Seq = t.Seq, At = t.Wall, GapBefore = t.GapBefore, Sessions = t.Sessions, Blocked = t.Blocked }).ToArray();

    /// <summary>The snapshot as it was at <paramref name="seq"/>, with the texts its rows name (null when not retained).</summary>
    public MonitorSnapshot? At(long seq)
    {
        var t = _ticks.FirstOrDefault(x => x.Seq == seq);
        if (t == null) return null;
        var rows = Rebuild(seq);
        var (texts, _) = _texts.Get(t.TextIds);
        return Unpack<MonitorSnapshot>(t.Rest) with { Sessions = rows, Texts = texts.Count > 0 ? texts : null };
    }

    /// <summary>Snapshots in [from, to] (inclusive), oldest first, for export.</summary>
    public IEnumerable<MonitorSnapshot> Range(long? fromSeq, long? toSeq)
    {
        foreach (var t in _ticks.ToList())
            if ((fromSeq == null || t.Seq >= fromSeq) && (toSeq == null || t.Seq <= toSeq) && At(t.Seq) is { } s) yield return s;
    }

    public void Clear()
    {
        foreach (var t in _ticks) _texts.RemoveHistoryRefs(t.TextIds);
        _ticks.Clear();
        _bytes = 0;
        _last = new();
    }

    static bool RowEquals(ActivityRow a, ActivityRow b) =>
        a with { BlockedBy = [] } == b with { BlockedBy = [] } && a.BlockedBy.AsSpan().SequenceEqual(b.BlockedBy);

    /// <summary>The bytes a tick holds: its compressed parts, its text-id array, and fixed bookkeeping.</summary>
    static long Size(Tick t) => TickOverhead + t.Rest.Length + (t.Keyframe?.Length ?? 0) + (t.Upserts?.Length ?? 0) + (t.Removed?.Length ?? 0) + 8L * t.TextIds.Length;

    internal static byte[] Pack<T>(T value)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            JsonSerializer.Serialize(z, value, Json.Options);
        return ms.ToArray();
    }

    internal static T Unpack<T>(byte[] data)
    {
        using var z = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(z, Json.Options)!;
    }
}

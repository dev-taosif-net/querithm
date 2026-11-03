using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Monitoring;

/// <summary>
/// PostgreSQL waits without pg_wait_sampling (tier 2): derived from the main tick's session rows, no query of its own
/// (D9). Each active, waiting backend counts as min(time since the previous tick, 2 × interval) of its wait; gaps beyond
/// that are never extrapolated (D10). A low-resolution estimate, labelled as such — never presented as exact.
/// </summary>
public sealed class TickWaitSampler
{
    long? _prevAt;

    public WaitDelta[]? Sample(IReadOnlyList<ActivityRow> rows, long atMs, long intervalMs, Func<string, string> category, Func<string, bool> benign)
    {
        var prev = _prevAt;
        _prevAt = atMs;
        if (prev == null) return null;
        var weight = Math.Min(atMs - prev.Value, 2 * intervalMs);
        var acc = new Dictionary<string, (long Ms, long Samples)>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r.Status != "active" || r.WaitType is not { } w || benign(w)) continue;
            var (ms, n) = acc.GetValueOrDefault(w);
            acc[w] = (ms + weight, n + 1);
        }
        return acc.Select(kv => new WaitDelta { Category = category(kv.Key), WaitType = kv.Key, WaitMsDelta = kv.Value.Ms, TasksDelta = kv.Value.Samples, SignalMsDelta = null })
            .OrderByDescending(d => d.WaitMsDelta).ToArray();
    }

    public void Reset() => _prevAt = null;
}

/// <summary>Overview metrics: cumulative counters become rates over the real elapsed time; point values pass through.</summary>
public sealed class MetricsBuilder
{
    readonly DeltaTracker<string> _cumulative = new();

    public (MetricSample Sample, PaneState State) Build(IReadOnlyList<MetricCounter> counters, long atMs, long wallMs, string? marker)
    {
        var cum = counters.Where(c => c.Cumulative && c.Value != null).Select(c => (c.Key, c.Value!.Value)).ToList();
        var r = _cumulative.Update(cum, atMs, marker);
        var values = new List<MetricValue>();
        string Pending() => r.First ? "collecting…" : "reset detected";
        foreach (var c in counters)
        {
            if (c.Key is "blksHit" or "blksRead") continue; // folded into cacheHit
            if (!c.Cumulative || c.Value == null)
            {
                values.Add(new MetricValue { Key = c.Key, Value = c.Value, Unit = c.Unit, Note = c.Note });
                continue;
            }
            if (!r.HasDeltas || r.ElapsedMs <= 0 || !r.Deltas.TryGetValue(c.Key, out var d))
            {
                values.Add(new MetricValue { Key = c.Key, Value = null, Unit = c.Unit, Note = Pending() });
                continue;
            }
            double? v = c.Unit switch
            {
                "perSec" or "bytesPerSec" => d * 1000.0 / r.ElapsedMs,
                "perMin" => d * 60_000.0 / r.ElapsedMs,
                _ => d,
            };
            values.Add(new MetricValue { Key = c.Key, Value = v, Unit = c.Unit, Note = c.Note });
        }
        if (counters.Any(c => c.Key == "blksHit"))
        {
            double? hit = null;
            string? note = null;
            if (!r.HasDeltas) note = Pending();
            else if (r.Deltas.TryGetValue("blksHit", out var h) && r.Deltas.TryGetValue("blksRead", out var rd))
            {
                if (h + rd > 0) hit = h * 100.0 / (h + rd);
                else note = "no block reads this interval";
            }
            values.Add(new MetricValue { Key = "cacheHit", Value = hit, Unit = "percent", Note = note });
        }
        var state = r.First ? PaneState.Collecting : r.Reset ? PaneState.ResetDetected : PaneState.Ok;
        return (new MetricSample { At = wallMs, Values = [.. values] }, state);
    }

    public void Reset() => _cumulative.Reset();
}

/// <summary>
/// Top queries (B4): deltas per plan-cache / statement entry (an entry is one plan's statement), grouped by query hash.
/// A full read drops entries that disappeared (evicted, T2); an incremental read leaves unseen entries unchanged. An
/// entry whose counters went down was re-created and counts from zero; a changed reset marker (pg_stat_statements_info)
/// or a full read missing ≥ 90 % of tracked entries is a reset (T1).
/// Memory: a 128-bit key and seven counters per tracked entry, no strings (the server's key strings took ~800 B per
/// entry, and the tracker holds every cached statement). Bounded on both sides: at most <see cref="MaxTrackedEntries"/>
/// entries (beyond that the pane stops and says why) and since-start totals for <see cref="MaxSinceStartGroups"/> groups.
/// </summary>
public sealed class TopQueriesTracker(int maxTrackedEntries = TopQueriesTracker.MaxTrackedEntries, int maxSinceStartGroups = TopQueriesTracker.MaxSinceStartGroups)
{
    public const int MaxTrackedEntries = 200_000;
    public const int MaxSinceStartGroups = 10_000;

    /// <summary>An entry's previous counters; a null counter is NaN / long.MinValue, so an entry holds no object.</summary>
    readonly record struct Prev(long Execs, double DurationMs, double CpuMs, long LogicalReads, long SharedBlocks, long Rows, double IoMs)
    {
        public static Prev Of(TopQueryCounter e) => new(e.Execs, e.DurationMs, e.CpuMs ?? double.NaN, e.LogicalReads ?? long.MinValue,
            e.SharedBlocks ?? long.MinValue, e.Rows ?? long.MinValue, e.IoMs ?? double.NaN);
    }

    readonly Dictionary<UInt128, Prev> _prev = [];
    readonly Dictionary<string, Totals> _sinceStart = new(StringComparer.Ordinal);
    bool _first = true;
    string? _marker;
    string? _overCap;
    long _updates;
    int _sinceStartDropped;

    public sealed class Totals
    {
        public long Execs;
        public double? CpuMs;
        public double DurationMs;
        public long? LogicalReads;
        public long? SharedBlocks;
        public long? Rows;
        public double? IoMs;
        public string QueryHash = "";
        public string? Database;
        public TopQueryCounter? Sample;
        public double SampleWeight;
        internal long LastUpdate;

        public void Add(TopQueryCounter d)
        {
            Execs += d.Execs;
            CpuMs = d.CpuMs is { } c ? (CpuMs ?? 0) + c : CpuMs;
            DurationMs += d.DurationMs;
            LogicalReads = d.LogicalReads is { } l ? (LogicalReads ?? 0) + l : LogicalReads;
            SharedBlocks = d.SharedBlocks is { } s ? (SharedBlocks ?? 0) + s : SharedBlocks;
            Rows = d.Rows is { } r ? (Rows ?? 0) + r : Rows;
            IoMs = d.IoMs is { } i ? (IoMs ?? 0) + i : IoMs;
        }
    }

    public int TrackedEntries => _prev.Count;
    public int SinceStartGroups => _sinceStart.Count;

    /// <summary>The latest update's note for the pane: why it stopped, or which since-start totals were dropped; else null.</summary>
    public string? Note { get; private set; }

    public (PaneState State, IReadOnlyList<Totals> Top) Update(TopQueryRead read, string orderBy, bool sinceStart, int max = 100)
    {
        Note = null;
        if (_overCap != null)
        {
            // Stopped over the cap: only a full read that fits can be a new baseline (incremental reads have no base).
            if (!read.Full || read.Entries.Length > maxTrackedEntries) return OverCap();
            Reset();
        }
        if (read.Full && read.Entries.Length > maxTrackedEntries) return OverCap();
        _updates++;
        var reset = !_first && read.ResetMarker != null && _marker != null && read.ResetMarker != _marker;
        if (read.ResetMarker != null) _marker = read.ResetMarker;
        var seen = read.Full ? new HashSet<UInt128>(read.Entries.Length) : null;
        var interval = new Dictionary<string, Totals>(StringComparer.Ordinal);
        var first = _first;
        _first = false;
        foreach (var e in read.Entries)
        {
            seen?.Add(e.Entry);
            var had = _prev.TryGetValue(e.Entry, out var p);
            _prev[e.Entry] = Prev.Of(e);
            // An empty query hash: the provider read the entry only to keep it tracked (it did not run since the cutoff).
            if (first || reset || e.QueryHash.Length == 0) continue;
            var d = !had || Decreased(e, p) ? e : Minus(e, p);
            if (d.Execs <= 0 && d.DurationMs <= 0) continue;
            var g = Group(e);
            if (!interval.TryGetValue(g, out var t)) interval[g] = t = new Totals { QueryHash = e.QueryHash, Database = e.Database };
            t.Add(d);
            var w = Weight(d, orderBy);
            if (t.Sample == null || w > t.SampleWeight) { t.Sample = e; t.SampleWeight = w; }
            if (!_sinceStart.TryGetValue(g, out var s)) _sinceStart[g] = s = new Totals { QueryHash = e.QueryHash, Database = e.Database };
            s.Add(d);
            s.LastUpdate = _updates;
            if (s.Sample == null || w >= s.SampleWeight) { s.Sample = e; s.SampleWeight = w; }
        }
        if (read.Full)
        {
            var gone = _prev.Keys.Where(k => !seen!.Contains(k)).ToList();
            if (!first && !reset && _prev.Count >= 10 && gone.Count >= 0.9 * _prev.Count) reset = true;
            foreach (var k in gone) _prev.Remove(k);
        }
        if (_prev.Count > maxTrackedEntries) return OverCap(); // incremental reads add new entries
        if (_sinceStart.Count > maxSinceStartGroups)
        {
            // The least recently active groups go first, a tenth at a time (no sort on every update).
            var drop = _sinceStart.OrderBy(kv => kv.Value.LastUpdate).Take(_sinceStart.Count - maxSinceStartGroups * 9 / 10).Select(kv => kv.Key).ToList();
            foreach (var k in drop) _sinceStart.Remove(k);
            _sinceStartDropped += drop.Count;
        }
        if (reset)
        {
            _sinceStart.Clear();
            _sinceStartDropped = 0;
            return (PaneState.ResetDetected, []);
        }
        if (first) return (PaneState.Collecting, []);
        if (sinceStart && _sinceStartDropped > 0)
            Note = $"Since-start totals cover the {maxSinceStartGroups:N0} most recently active query groups: {_sinceStartDropped:N0} older groups were dropped and count from zero if they run again.";
        var pool = sinceStart ? _sinceStart.Values : (IEnumerable<Totals>)interval.Values;
        return (PaneState.Ok, pool.OrderByDescending(t => Weight(t, orderBy)).ThenBy(t => t.QueryHash, StringComparer.Ordinal).Take(max).ToList());
    }

    (PaneState, IReadOnlyList<Totals>) OverCap()
    {
        if (_overCap == null)
        {
            _prev.Clear();
            _prev.TrimExcess();
            _sinceStart.Clear();
            _overCap = $"The server holds more than {maxTrackedEntries:N0} statement entries (plan cache / pg_stat_statements). Top queries keep the " +
                "previous counters of every entry to compute deltas, so the pane stops here to keep the monitor's memory bounded; " +
                "it starts again when a full read finds fewer.";
        }
        Note = _overCap;
        return (PaneState.Degraded, []);
    }

    static string Group(TopQueryCounter e) => $"{e.QueryHash}|{e.Database}";

    static bool Decreased(TopQueryCounter e, Prev p) => e.Execs < p.Execs || e.DurationMs < p.DurationMs - 1e-6;

    static double? N(double v) => double.IsNaN(v) ? null : v;
    static long? N(long v) => v == long.MinValue ? null : v;

    static TopQueryCounter Minus(TopQueryCounter e, Prev p) => e with
    {
        Execs = e.Execs - p.Execs,
        CpuMs = e.CpuMs - N(p.CpuMs),
        DurationMs = e.DurationMs - p.DurationMs,
        LogicalReads = e.LogicalReads - N(p.LogicalReads),
        SharedBlocks = e.SharedBlocks - N(p.SharedBlocks),
        Rows = e.Rows - N(p.Rows),
        IoMs = e.IoMs - N(p.IoMs),
    };

    static double Weight(TopQueryCounter d, string orderBy) => orderBy switch
    {
        "duration" => d.DurationMs,
        "reads" => d.LogicalReads ?? d.SharedBlocks ?? 0,
        "executions" => d.Execs,
        _ => d.CpuMs ?? d.DurationMs,
    };

    static double Weight(Totals t, string orderBy) => orderBy switch
    {
        "duration" => t.DurationMs,
        "reads" => t.LogicalReads ?? t.SharedBlocks ?? 0,
        "executions" => t.Execs,
        _ => t.CpuMs ?? t.DurationMs,
    };

    public void Reset()
    {
        _prev.Clear();
        _sinceStart.Clear();
        _first = true;
        _marker = null;
        _overCap = null;
        _sinceStartDropped = 0;
    }
}

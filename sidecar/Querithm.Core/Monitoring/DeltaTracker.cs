namespace Querithm.Core.Monitoring;

/// <summary>
/// Cumulative counters → deltas against the previous sample. The first sample yields no deltas ("collecting"); a
/// counter that went backwards, or a changed server reset marker (restart time, stats_reset), yields "reset detected"
/// for that sample instead of negative values (D1–D5).
/// </summary>
public sealed class DeltaTracker<TKey> where TKey : notnull
{
    Dictionary<TKey, double>? _prev;
    long _prevAt;
    string? _marker;

    public sealed record Result(bool First, bool Reset, long ElapsedMs, IReadOnlyDictionary<TKey, double> Deltas)
    {
        public bool HasDeltas => !First && !Reset;
    }

    static readonly IReadOnlyDictionary<TKey, double> None = new Dictionary<TKey, double>();

    /// <summary>
    /// <paramref name="atMs"/> is the sample time (monotonic); deltas are per sample, callers divide by
    /// <see cref="Result.ElapsedMs"/> (real elapsed time, not the nominal interval — D4). A key seen for the first time
    /// counts from zero (D5); keys no longer reported are dropped — unless nearly all of them went at once: reads skip
    /// zero counters, so a cleared set (an administrator clearing the wait statistics) shows up as vanished keys, not lower ones (D2).
    /// </summary>
    public Result Update(IEnumerable<(TKey Key, double Value)> values, long atMs, string? resetMarker = null)
    {
        var current = new Dictionary<TKey, double>();
        foreach (var (k, v) in values) current[k] = current.TryGetValue(k, out var x) ? x + v : v;
        var first = _prev == null;
        var markerChanged = !first && resetMarker != null && _marker != null && resetMarker != _marker;
        var elapsed = atMs - _prevAt;
        Result result;
        if (first) result = new Result(true, false, 0, None);
        else if (markerChanged || current.Any(kv => _prev!.TryGetValue(kv.Key, out var p) && kv.Value < p) || MostlyVanished(current))
            result = new Result(false, true, elapsed, None);
        else
        {
            var d = new Dictionary<TKey, double>(current.Count);
            foreach (var (k, v) in current) d[k] = _prev!.TryGetValue(k, out var p) ? v - p : v;
            result = new Result(false, false, elapsed, d);
        }
        _prev = current;
        _prevAt = atMs;
        if (resetMarker != null) _marker = resetMarker;
        return result;
    }

    bool MostlyVanished(Dictionary<TKey, double> current)
    {
        var gone = _prev!.Keys.Count(k => !current.ContainsKey(k));
        return _prev.Count >= 10 && gone >= 0.9 * _prev.Count;
    }

    /// <summary>Forget the baseline (reconnect): the next sample is a first sample again.</summary>
    public void Reset()
    {
        _prev = null;
        _marker = null;
    }
}

using Querithm.Core.Monitoring;

namespace Querithm.Tests.Fakes;

/// <summary>
/// Manual time for the monitor. <see cref="Advance"/> fires due waits and timeouts in time order, inline on the
/// calling thread, so a scheduler created with inline continuations runs deterministically — no sleeps.
/// </summary>
public sealed class FakeMonitorClock : IMonitorClock
{
    readonly object _gate = new();
    readonly List<(long Due, long Order, Action Fire)> _timers = [];
    long _order;
    long _now;

    public long NowMs { get { lock (_gate) return _now; } }

    public Task WaitUntilAsync(long dueMs, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        if (dueMs <= NowMs) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        lock (_gate) _timers.Add((dueMs, _order++, () => { reg.Dispose(); tcs.TrySetResult(); }));
        return tcs.Task;
    }

    public CancellationTokenSource After(long ms)
    {
        var cts = new CancellationTokenSource();
        if (ms <= 0) { cts.Cancel(); return cts; }
        lock (_gate) _timers.Add((_now + ms, _order++, () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }));
        return cts;
    }

    /// <summary>Moves time forward by <paramref name="ms"/>, firing everything due on the way.</summary>
    public void Advance(long ms)
    {
        long target;
        lock (_gate) target = _now + ms;
        while (true)
        {
            (long Due, long Order, Action Fire) next;
            lock (_gate)
            {
                var due = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).ThenBy(t => t.Order).ToList();
                if (due.Count == 0) break;
                next = due[0];
                _timers.Remove(next);
                _now = Math.Max(_now, next.Due);
            }
            next.Fire();
        }
        lock (_gate) _now = target;
    }

    /// <summary>Advances in steps of <paramref name="stepMs"/> until <paramref name="ms"/> passed.</summary>
    public void Run(long ms, long stepMs = 10)
    {
        for (long t = 0; t < ms; t += stepMs) Advance(Math.Min(stepMs, ms - t));
    }
}

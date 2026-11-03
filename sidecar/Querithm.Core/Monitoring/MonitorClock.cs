using System.Diagnostics;

namespace Querithm.Core.Monitoring;

/// <summary>Time for the monitor: real in the app, a fake in tests so scheduling is asserted without sleeps.</summary>
public interface IMonitorClock
{
    long NowMs { get; }

    /// <summary>Completes once the clock reaches <paramref name="dueMs"/>, or is cancelled.</summary>
    Task WaitUntilAsync(long dueMs, CancellationToken ct);

    /// <summary>A source cancelled once <paramref name="ms"/> have passed (a job's timeout).</summary>
    CancellationTokenSource After(long ms);
}

public sealed class SystemMonitorClock : IMonitorClock
{
    public static readonly SystemMonitorClock Instance = new();
    readonly Stopwatch _sw = Stopwatch.StartNew();

    public long NowMs => _sw.ElapsedMilliseconds;

    public Task WaitUntilAsync(long dueMs, CancellationToken ct)
    {
        var d = dueMs - NowMs;
        return d <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(d), ct);
    }

    public CancellationTokenSource After(long ms) => new(TimeSpan.FromMilliseconds(Math.Max(1, ms)));
}

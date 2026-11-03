namespace Querithm.Core.Monitoring;

/// <summary>
/// The only place that decides when work runs on a monitor connection (ADO.NET connections are not concurrent).
/// Job classes, highest first: kill/cancel (claims the idle connection, else the caller uses the control connection),
/// interactive (one pending per kind, superseded by a newer one, fails after 5 s in the queue), the main tick (never
/// queued twice: a late tick runs once and reports its lag), and secondary periodic jobs — after every main tick at
/// most one due secondary job runs, the most overdue first, even when the next tick is already due (plan B, C6–C8).
/// </summary>
public sealed class MonitorScheduler : IAsyncDisposable
{
    public const long QueueLimitMs = 5_000;
    public const long BudgetWindowMs = 10_000;
    const int MaxMultiplier = 8, MaxTickMultiplier = 4;

    public sealed class Periodic
    {
        internal Periodic(string name, long periodMs, long timeoutMs, Func<CancellationToken, Task> run, int throttleRank)
        {
            Name = name;
            BasePeriodMs = periodMs;
            TimeoutMs = timeoutMs;
            Run = run;
            ThrottleRank = throttleRank;
        }

        public string Name { get; }
        public long BasePeriodMs { get; set; }
        public int Multiplier { get; internal set; } = 1;
        public long PeriodMs => BasePeriodMs * Multiplier;
        public long NextDueMs { get; internal set; }
        public long TimeoutMs { get; }
        public bool Enabled { get; set; } = true;
        /// <summary>Lower ranks are throttled first.</summary>
        public int ThrottleRank { get; }
        public long Runs { get; internal set; }
        public long LastStartMs { get; internal set; } = -1;
        internal Func<CancellationToken, Task> Run { get; }
    }

    sealed class Pending(string kind, long enqueuedAt, long timeoutMs)
    {
        public string Kind { get; } = kind;
        public long EnqueuedAt { get; } = enqueuedAt;
        public long TimeoutMs { get; } = timeoutMs;
        public bool Started { get; set; }
        public required Func<CancellationToken, Task> Run { get; init; }
        public required Action<Exception> Fail { get; init; }
        public CancellationTokenSource QueueTimer { get; } = new();
    }

    readonly IMonitorClock _clock;
    readonly Func<CancellationToken, Task> _tick;
    readonly bool _inline;
    readonly object _gate = new();
    readonly CancellationTokenSource _stop = new();
    readonly List<Periodic> _periodic = [];
    readonly LinkedList<Pending> _interactive = new();
    readonly Queue<long> _lags = new();
    long _intervalMs;
    int _tickMultiplier = 1;
    long _nextTickMs;
    long _lastTickStart;
    bool _secondaryAllowed;
    /// <summary>A secondary job was already due when the last tick ended: it goes before the next tick (C8).</summary>
    bool _secondaryFirst;
    bool _paused;
    bool _busy;
    bool _claimed;
    bool _stopped;
    CancellationTokenSource? _running;
    TaskCompletionSource _wake;
    TaskCompletionSource? _idle;
    TaskCompletionSource? _claimReleased;
    Task? _loop;
    long _windowStart;
    long _busyMs;

    /// <param name="inlineContinuations">Tests: wake-ups resume on the waking thread, so a fake clock drives everything deterministically.</param>
    public MonitorScheduler(IMonitorClock clock, Func<CancellationToken, Task> tick, long intervalMs, bool inlineContinuations = false)
    {
        _clock = clock;
        _tick = tick;
        _intervalMs = intervalMs;
        _inline = inlineContinuations;
        _wake = NewSignal();
    }

    public long TickTimeoutMs { get; set; } = 5_000;
    public long IntervalMs { get { lock (_gate) return _intervalMs; } }
    public long LastLagMs { get; private set; }
    public long TickCount { get; private set; }
    public bool Throttled { get { lock (_gate) return _tickMultiplier > 1 || _periodic.Any(p => p.Enabled && p.Multiplier > 1); } }
    public bool Paused { get { lock (_gate) return _paused; } }
    public bool Busy { get { lock (_gate) return _busy; } }
    public IReadOnlyList<Periodic> Jobs { get { lock (_gate) return [.. _periodic]; } }
    public int TickMultiplier { get { lock (_gate) return _tickMultiplier; } }

    /// <summary>Recent tick lags (ms), newest last (bounded).</summary>
    public long[] RecentLags { get { lock (_gate) return [.. _lags]; } }

    TaskCompletionSource NewSignal() => new(_inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);

    public Periodic AddPeriodic(string name, long periodMs, long timeoutMs, Func<CancellationToken, Task> run, int throttleRank)
    {
        var p = new Periodic(name, periodMs, timeoutMs, run, throttleRank);
        lock (_gate)
        {
            p.NextDueMs = _clock.NowMs; // due right after the first tick
            _periodic.Add(p);
        }
        return p;
    }

    public void Start()
    {
        lock (_gate)
        {
            _nextTickMs = _clock.NowMs;
            _windowStart = _clock.NowMs;
        }
        _loop = _inline ? LoopAsync() : Task.Run(LoopAsync);
    }

    /// <summary>Applied from the next tick; a shorter interval brings the next tick forward (no restart).</summary>
    public void SetInterval(long ms)
    {
        lock (_gate)
        {
            _intervalMs = ms;
            _nextTickMs = Math.Min(_nextTickMs, _lastTickStart + ms * _tickMultiplier);
        }
        Signal();
    }

    public void Pause()
    {
        lock (_gate) _paused = true;
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            _nextTickMs = _clock.NowMs;
        }
        Signal();
    }

    /// <summary>Runs a user-initiated job ahead of the next tick. A newer request of the same kind supersedes a pending one.</summary>
    public Task<T> RunInteractiveAsync<T>(string kind, Func<CancellationToken, Task<T>> job, long timeoutMs = 5_000)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending p;
        Pending? superseded = null;
        lock (_gate)
        {
            if (_stopped) throw new QuerithmException("monitorStopped", "The monitor was stopped.");
            p = new Pending(kind, _clock.NowMs, timeoutMs)
            {
                Run = async ct =>
                {
                    try { result.TrySetResult(await job(ct).ConfigureAwait(false)); }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    {
                        result.TrySetException(new QuerithmException("timeout", $"The monitor query timed out after {timeoutMs / 1000.0:0.#} s."));
                    }
                    catch (Exception ex) { result.TrySetException(ex); }
                },
                Fail = ex => result.TrySetException(ex),
            };
            foreach (var old in _interactive)
                if (old.Kind == kind && !old.Started) { superseded = old; break; }
            if (superseded != null) _interactive.Remove(superseded);
            _interactive.AddLast(p);
        }
        superseded?.QueueTimer.Cancel();
        superseded?.Fail(new QuerithmException("superseded", "A newer request replaced this one."));
        // Never left pending: not started within 5 s → "monitor busy" and removed.
        _clock.WaitUntilAsync(p.EnqueuedAt + QueueLimitMs, p.QueueTimer.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            bool expired;
            lock (_gate) expired = !p.Started && _interactive.Remove(p);
            if (expired) p.Fail(new QuerithmException("monitorBusy", "The monitor is busy; try again."));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Signal();
        return result.Task;
    }

    sealed class Lease(MonitorScheduler owner) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.ReleaseClaim();
        }
    }

    /// <summary>Kill path: the connection if no job runs right now (the loop starts nothing until the lease is disposed); else null.</summary>
    public IDisposable? TryClaimIdle()
    {
        lock (_gate)
        {
            if (_busy || _claimed || _stopped) return null;
            _claimed = true;
        }
        return new Lease(this);
    }

    /// <summary>
    /// Kill fallback when no control connection could be opened: cancels the running (read-only) job and claims the
    /// connection once it stops, waiting at most <paramref name="boundMs"/>. Null when the job did not stop in time.
    /// </summary>
    public async Task<IDisposable?> PreemptAsync(long boundMs)
    {
        Task idle;
        CancellationTokenSource? running;
        lock (_gate)
        {
            if (_stopped || _claimed) return null;
            _claimed = true;
            if (!_busy) return new Lease(this);
            // The signal exists before the cancel: the job may end inline, on this thread, while it is being cancelled.
            idle = (_idle ??= NewSignal()).Task;
            running = _running;
        }
        try { running?.Cancel(); } catch (ObjectDisposedException) { /* the job ended meanwhile */ }
        using var bound = new CancellationTokenSource();
        await AnyAsync(idle, _clock.WaitUntilAsync(_clock.NowMs + boundMs, bound.Token)).ConfigureAwait(false);
        bound.Cancel();
        lock (_gate)
        {
            if (!_busy) return new Lease(this);
        }
        ReleaseClaim();
        return null;
    }

    void ReleaseClaim()
    {
        TaskCompletionSource? released;
        lock (_gate)
        {
            _claimed = false;
            released = _claimReleased;
            _claimReleased = null;
        }
        released?.TrySetResult();
        Signal();
    }

    void Signal()
    {
        TaskCompletionSource w;
        lock (_gate)
        {
            w = _wake;
            _wake = NewSignal();
        }
        w.TrySetResult();
    }

    // Task.WhenAny takes its continuation off the task that did not finish. A ContinueWith stays on it: the wake signal
    // completes only when something signals the loop, so every timer wait of an idle monitor kept ~300 B for good (soak).
    static Task AnyAsync(Task a, Task b) => Task.WhenAny(a, b);

    async Task LoopAsync()
    {
        while (true)
        {
            Func<CancellationToken, Task>? run = null;
            Pending? interactive = null;
            Periodic? secondary = null;
            var isTick = false;
            long due = 0, start = 0, waitUntil = long.MaxValue;
            Task wake;
            CancellationTokenSource? cts = null;
            lock (_gate)
            {
                if (_stopped) return;
                var now = _clock.NowMs;
                wake = _wake.Task;
                if (_claimed) wake = (_claimReleased ??= NewSignal()).Task;
                else if (_interactive.First is { } first)
                {
                    interactive = first.Value;
                    _interactive.RemoveFirst();
                    interactive.Started = true;
                    run = interactive.Run;
                    cts = Linked(interactive.TimeoutMs);
                }
                // A secondary job already due when the last tick ended runs before the next tick even if that tick is
                // already due: a tick longer than the interval would otherwise always win and starve them (C8). One that
                // became due later waits for the tick, so it does not delay ticks by its whole length (C7).
                else if (!_paused && _secondaryAllowed && (_secondaryFirst || now < _nextTickMs) && MostOverdue(now) is { } s)
                {
                    secondary = s;
                    _secondaryAllowed = false;
                    run = s.Run;
                    cts = Linked(s.TimeoutMs);
                }
                else if (!_paused && now >= _nextTickMs)
                {
                    isTick = true;
                    due = _nextTickMs;
                    run = _tick;
                    cts = Linked(TickTimeoutMs);
                }
                else if (!_paused)
                {
                    waitUntil = _nextTickMs;
                    if (_secondaryAllowed && NextSecondaryDue() is { } nd) waitUntil = Math.Min(waitUntil, nd);
                }
                if (run != null)
                {
                    _busy = true;
                    _running = cts;
                    start = now;
                }
            }
            if (run == null)
            {
                if (waitUntil == long.MaxValue || _claimed) await wake.ConfigureAwait(false);
                else
                {
                    using var delay = new CancellationTokenSource();
                    await AnyAsync(wake, _clock.WaitUntilAsync(waitUntil, delay.Token)).ConfigureAwait(false);
                    delay.Cancel();
                }
                continue;
            }
            interactive?.QueueTimer.Cancel();
            if (isTick)
            {
                LastLagMs = Math.Max(0, start - due);
                lock (_gate)
                {
                    _lastTickStart = start;
                    _lags.Enqueue(LastLagMs);
                    while (_lags.Count > 1_000) _lags.Dequeue();
                }
            }
            if (secondary != null) secondary.LastStartMs = start;
            try { await run(cts!.Token).ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested || cts!.IsCancellationRequested)
            {
                // stopped, timed out or preempted: only this job is affected
            }
            catch (Exception)
            {
                // jobs report their own failures; the loop keeps going
            }
            finally
            {
                TaskCompletionSource? idle;
                lock (_gate)
                {
                    var end = _clock.NowMs;
                    _busy = false;
                    _running = null;
                    if (isTick)
                    {
                        TickCount++;
                        // From the actual start, not the due time: a late tick runs once and is never followed by catch-up.
                        var next = start + _intervalMs * _tickMultiplier;
                        _nextTickMs = next < end ? end : next;
                        _secondaryAllowed = true;
                        _secondaryFirst = MostOverdue(end) != null;
                    }
                    if (secondary != null)
                    {
                        secondary.Runs++;
                        secondary.NextDueMs = start + secondary.PeriodMs;
                    }
                    Account(start, end);
                    idle = _idle;
                    _idle = null;
                }
                cts!.Dispose();
                idle?.TrySetResult();
            }
        }
    }

    CancellationTokenSource Linked(long timeoutMs)
    {
        var timeout = _clock.After(timeoutMs);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, timeout.Token);
        linked.Token.Register(() => timeout.Dispose());
        return linked;
    }

    Periodic? MostOverdue(long now)
    {
        Periodic? best = null;
        foreach (var p in _periodic)
            if (p.Enabled && p.NextDueMs <= now && (best == null || now - p.NextDueMs > now - best.NextDueMs)) best = p;
        return best;
    }

    long? NextSecondaryDue()
    {
        long? n = null;
        foreach (var p in _periodic)
            if (p.Enabled) n = n == null ? p.NextDueMs : Math.Min(n.Value, p.NextDueMs);
        return n;
    }

    /// <summary>Budget: scheduled work above 50 % of wall time over 10 s halves the lowest-priority periodic job; below 25 % restores one step.</summary>
    void Account(long start, long end)
    {
        _busyMs += end - start;
        var span = end - _windowStart;
        if (span < BudgetWindowMs) return;
        var share = _busyMs / (double)span;
        _windowStart = end;
        _busyMs = 0;
        if (share > 0.5)
        {
            // Only jobs that run: slowing a disabled one relieves nothing.
            var target = _periodic.Where(p => p.Enabled && p.Multiplier < MaxMultiplier).OrderBy(p => p.ThrottleRank).FirstOrDefault();
            if (target != null) target.Multiplier *= 2;
            else if (_tickMultiplier < MaxTickMultiplier) _tickMultiplier *= 2;
        }
        else if (share < 0.25)
        {
            if (_tickMultiplier > 1) _tickMultiplier /= 2;
            else if (_periodic.Where(p => p.Multiplier > 1).OrderByDescending(p => p.ThrottleRank).FirstOrDefault() is { } p) p.Multiplier /= 2;
        }
    }

    /// <summary>Cancels the running job and every pending request; the loop exits. Idempotent.</summary>
    public async Task StopAsync()
    {
        List<Pending> pending;
        Task? loop;
        lock (_gate)
        {
            if (_stopped && _loop == null) return;
            _stopped = true;
            pending = [.. _interactive];
            _interactive.Clear();
            loop = _loop;
            _loop = null;
        }
        _stop.Cancel();
        foreach (var p in pending)
        {
            p.QueueTimer.Cancel();
            p.Fail(new QuerithmException("monitorStopped", "The monitor was stopped."));
        }
        Signal();
        TaskCompletionSource? released;
        lock (_gate) { released = _claimReleased; _claimReleased = null; }
        released?.TrySetResult();
        if (loop != null)
            try { await loop.ConfigureAwait(false); } catch { /* stopped */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}

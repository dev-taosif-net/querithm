using Querithm.Core;
using Querithm.Core.Monitoring;
using Querithm.Tests.Fakes;
using Querithm.Contract.Providers;

namespace Querithm.Tests;

/// <summary>MonitorScheduler (plan "One connection, one scheduler"; matrix C1, C6–C14, L10, L11) on a fake clock: no sleeps.</summary>
public class MonitorSchedulerTests
{
    /// <summary>A fake monitor connection: throws (records) when two jobs overlap on it (C1).</summary>
    sealed class Conn
    {
        int _active;
        public int Overlaps;
        public readonly List<(string Job, long Start, long End)> Log = [];
        readonly List<(string Job, long Start)> _started = [];

        public async Task Use(FakeMonitorClock clock, string job, long ms, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _active) > 1) Interlocked.Increment(ref Overlaps);
            var start = clock.NowMs;
            lock (_started) _started.Add((job, start));
            try { await clock.WaitUntilAsync(start + ms, ct); }
            finally
            {
                Interlocked.Decrement(ref _active);
                lock (Log) Log.Add((job, start, clock.NowMs));
            }
        }

        public IEnumerable<long> Starts(string job) { lock (_started) return _started.Where(l => l.Job == job).Select(l => l.Start).ToList(); }
        public int StartCount { get { lock (_started) return _started.Count; } }
    }

    sealed record Durations(long Tick, long Locks, long Top, long TopFull, long Deadlocks);

    static (MonitorScheduler S, FakeMonitorClock Clock, Conn Conn) Rig(Durations d, long interval = 2_000)
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", d.Tick, ct), interval, inlineContinuations: true);
        var topRuns = 0;
        s.AddPeriodic("locks", 5 * interval, 5_000, ct => conn.Use(clock, "locks", d.Locks, ct), 2);
        s.AddPeriodic("topQueries", 5 * interval, 5_000, ct => conn.Use(clock, "topQueries", topRuns++ % 30 == 0 ? d.TopFull : d.Top, ct), 1);
        s.AddPeriodic("deadlocks", 30_000, 10_000, ct => conn.Use(clock, "deadlocks", d.Deadlocks, ct), 0);
        return (s, clock, conn);
    }

    /// <summary>
    /// Runs a simulation off xUnit's synchronization context: .NET only inlines await continuations where there is none,
    /// and the fake clock relies on inlining to stay deterministic.
    /// </summary>
    static Task Sim(Func<Task> body) => Task.Run(body);

    static readonly Durations Typical = new(100, 200, 300, 1_500, 2_000);

    [Fact]
    public Task C7_default_intervals_every_secondary_job_runs_within_its_period_plus_three_ticks_and_tick_lag_p95_is_within_200ms() => Sim(async () =>
    {
        var (s, clock, conn) = Rig(Typical);
        s.Start();
        clock.Run(10 * 60_000);
        foreach (var (job, period) in new[] { ("locks", 10_000L), ("topQueries", 10_000L), ("deadlocks", 30_000L) })
        {
            var starts = conn.Starts(job).ToList();
            Assert.True(starts.Count > 10, job);
            var maxGap = starts.Zip(starts.Skip(1), (a, b) => b - a).Max();
            Assert.True(maxGap <= period + 3 * 2_000, $"{job}: max gap {maxGap} ms");
        }
        var lags = s.RecentLags.Order().ToArray();
        var p95 = lags[(int)Math.Ceiling(lags.Length * 0.95) - 1];
        Assert.True(p95 <= 200, $"tick lag p95 {p95} ms");
        Assert.Equal(0, conn.Overlaps); // C1
        await s.StopAsync();
    });

    [Fact]
    public Task C8_slow_server_secondary_jobs_still_run_once_per_two_periods_and_late_ticks_run_once() => Sim(async () =>
    {
        // At 3× the durations the work exceeds the 50 % budget, so C13 throttles: the starvation bound applies to each
        // job's effective (throttled) period, recorded at every start (plan revision: C8 vs C13).
        var slow = new Durations(2_500, 600, 900, 4_500, 6_000); // the tick alone is longer than the 2 s interval
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", slow.Tick, ct), 2_000, inlineContinuations: true);
        var periods = new List<(string Job, long Start, long Period)>();
        MonitorScheduler.Periodic? locks = null, top = null, dl = null;
        var topRuns = 0;
        locks = s.AddPeriodic("locks", 10_000, 5_000, ct => { periods.Add(("locks", clock.NowMs, locks!.PeriodMs)); return conn.Use(clock, "locks", slow.Locks, ct); }, 2);
        top = s.AddPeriodic("topQueries", 10_000, 5_000, ct => { periods.Add(("topQueries", clock.NowMs, top!.PeriodMs)); return conn.Use(clock, "topQueries", topRuns++ % 30 == 0 ? slow.TopFull : slow.Top, ct); }, 1);
        dl = s.AddPeriodic("deadlocks", 30_000, 10_000, ct => { periods.Add(("deadlocks", clock.NowMs, dl!.PeriodMs)); return conn.Use(clock, "deadlocks", slow.Deadlocks, ct); }, 0);
        s.Start();
        clock.Run(10 * 60_000);
        Assert.True(s.Throttled);
        foreach (var job in new[] { "locks", "topQueries", "deadlocks" })
        {
            var runs = periods.Where(p => p.Job == job).ToList();
            Assert.True(runs.Count >= 3, job);
            foreach (var (prev, next) in runs.Zip(runs.Skip(1)))
                Assert.True(next.Start - prev.Start <= 2 * Math.Max(prev.Period, next.Period), $"{job}: gap {next.Start - prev.Start} ms > 2 × {Math.Max(prev.Period, next.Period)}");
        }
        var ticks = conn.Starts("tick").ToList();
        // No catch-up: ticks never start closer than one tick's duration apart, and the lag is reported.
        Assert.All(ticks.Zip(ticks.Skip(1), (a, b) => b - a), gap => Assert.True(gap >= slow.Tick));
        Assert.Contains(s.RecentLags, l => l > 0);
        Assert.Equal(0, conn.Overlaps);
        await s.StopAsync();
    });

    [Fact]
    public Task C6_secondary_jobs_due_during_a_tick_run_one_at_a_time_after_it_most_overdue_first() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", 100, ct), 2_000, inlineContinuations: true);
        s.AddPeriodic("a", 10_000, 5_000, ct => conn.Use(clock, "a", 50, ct), 1);
        s.AddPeriodic("b", 10_000, 5_000, ct => conn.Use(clock, "b", 50, ct), 2);
        s.Start();
        clock.Advance(50); // first tick running; a and b are both due
        clock.Advance(100);
        // After the tick exactly one secondary ran; the other waits for the next tick.
        Assert.Single(conn.Starts("a").Concat(conn.Starts("b")));
        clock.Advance(2_000);
        Assert.Single(conn.Starts("a"));
        Assert.Single(conn.Starts("b"));
        Assert.Equal(0, conn.Overlaps);
        await s.StopAsync();
    });

    [Fact]
    public Task C9_an_interactive_request_starts_as_soon_as_the_running_job_ends_and_the_delayed_tick_reports_lag() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", 1_900, ct), 2_000, inlineContinuations: true);
        s.Start();
        clock.Advance(100); // tick running until 1,900
        var detail = s.RunInteractiveAsync("detail", async ct => { await conn.Use(clock, "detail", 500, ct); return 42; });
        clock.Advance(1_799);
        Assert.False(detail.IsCompleted);
        Assert.Empty(conn.Starts("detail"));
        clock.Advance(1);
        Assert.Equal(1_900, Assert.Single(conn.Starts("detail")));
        clock.Advance(500);
        Assert.Equal(42, await detail);
        // The tick due at 2,000 started at 2,400.
        clock.Advance(10);
        Assert.Equal(400, s.LastLagMs);
        Assert.Equal(0, conn.Overlaps);
        await s.StopAsync();
    });

    [Fact]
    public Task C10_an_interactive_request_not_started_within_five_seconds_fails_busy_and_leaves_nothing_pending() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", 100, ct), 2_000, inlineContinuations: true);
        s.AddPeriodic("deadlocks", 30_000, 10_000, ct => conn.Use(clock, "deadlocks", 8_000, ct), 0);
        s.Start();
        clock.Advance(150); // tick done, the 8 s deadlock read runs
        var late = s.RunInteractiveAsync("detail", async ct => { await conn.Use(clock, "detail", 10, ct); return 1; });
        clock.Advance(5_000);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => late);
        Assert.Equal("monitorBusy", ex.Code);
        clock.Advance(3_000); // deadlock read ends; the expired request never runs
        Assert.Empty(conn.Starts("detail"));
        var next = s.RunInteractiveAsync("detail", async ct => { await conn.Use(clock, "detail", 10, ct); return 2; });
        clock.Advance(200); // after the tick that started when the read ended
        Assert.Equal(2, await next);
        await s.StopAsync();
    });

    [Fact]
    public Task C11_a_second_request_of_the_same_kind_supersedes_the_pending_one() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", 1_000, ct), 2_000, inlineContinuations: true);
        s.Start();
        clock.Advance(10);
        var first = s.RunInteractiveAsync("detail", async ct => { await conn.Use(clock, "first", 10, ct); return "a"; });
        var second = s.RunInteractiveAsync("detail", async ct => { await conn.Use(clock, "second", 10, ct); return "b"; });
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => first);
        Assert.Equal("superseded", ex.Code);
        clock.Advance(1_100);
        Assert.Equal("b", await second);
        Assert.Empty(conn.Starts("first"));
        await s.StopAsync();
    });

    [Fact]
    public Task C12_a_job_timeout_cancels_only_that_job_and_the_next_tick_is_normal() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var n = 0;
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", n++ == 0 ? 60_000 : 100, ct), 2_000, inlineContinuations: true) { TickTimeoutMs = 5_000 };
        s.Start();
        clock.Advance(5_000); // first tick hangs: cut at its 5 s timeout
        var first = conn.Log.Single(l => l.Job == "tick");
        Assert.Equal(5_000, first.End - first.Start);
        clock.Advance(200);
        Assert.Equal(2, conn.Starts("tick").Count());
        Assert.Equal(2, s.TickCount);
        await s.StopAsync();
    });

    [Fact]
    public Task C13_load_over_half_of_wall_time_throttles_the_lowest_priority_job_and_restores_when_it_drops() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var tickMs = 1_500L;
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", tickMs, ct), 2_000, inlineContinuations: true);
        var deadlocks = s.AddPeriodic("deadlocks", 30_000, 10_000, ct => conn.Use(clock, "deadlocks", 100, ct), 0);
        var locks = s.AddPeriodic("locks", 10_000, 5_000, ct => conn.Use(clock, "locks", 100, ct), 2);
        s.Start();
        clock.Run(25_000);
        Assert.True(s.Throttled);
        Assert.True(deadlocks.Multiplier > 1);
        Assert.Equal(1, locks.Multiplier); // lowest priority first
        tickMs = 50;
        clock.Run(120_000);
        Assert.False(s.Throttled);
        await s.StopAsync();
    });

    [Fact]
    public Task C14_stop_during_a_job_cancels_it_and_fails_pending_requests() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var cancelled = false;
        var s = new MonitorScheduler(clock, async ct =>
        {
            try { await clock.WaitUntilAsync(clock.NowMs + 60_000, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        }, 2_000, inlineContinuations: true);
        s.Start();
        clock.Advance(10);
        var pending = s.RunInteractiveAsync("detail", _ => Task.FromResult(1));
        await s.StopAsync();
        Assert.True(cancelled);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => pending);
        Assert.Equal("monitorStopped", ex.Code);
        // Refused synchronously once stopped (no task is created).
        Assert.IsType<QuerithmException>(Record.Exception(() => { _ = s.RunInteractiveAsync("detail", _ => Task.FromResult(1)); }));
    });

    [Fact]
    public Task L11_paused_no_job_runs_and_resume_ticks_at_once() => Sim(async () =>
    {
        var (s, clock, conn) = Rig(Typical);
        s.Start();
        clock.Advance(2_500);
        s.Pause();
        clock.Run(5_000); // the job running at pause time finishes
        var before = conn.StartCount;
        clock.Run(60_000);
        Assert.Equal(before, conn.StartCount);
        var ticks = conn.Starts("tick").Count();
        s.Resume();
        clock.Advance(1);
        Assert.Equal(ticks + 1, conn.Starts("tick").Count());
        await s.StopAsync();
    });

    [Fact]
    public Task L10_an_interval_change_applies_from_the_next_tick_without_restarting() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", 10, ct), 30_000, inlineContinuations: true);
        s.Start();
        clock.Advance(100);
        s.SetInterval(1_000);
        clock.Advance(1_000);
        Assert.Equal(2, conn.Starts("tick").Count());
        clock.Advance(1_000);
        Assert.Equal(3, conn.Starts("tick").Count());
        await s.StopAsync();
    });

    [Fact]
    public Task C2_C4_a_kill_claims_the_idle_connection_or_preempts_the_running_job_within_its_bound() => Sim(async () =>
    {
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", 10, ct), 2_000, inlineContinuations: true);
        s.AddPeriodic("deadlocks", 30_000, 10_000, ct => conn.Use(clock, "deadlocks", 8_000, ct), 0);
        s.Start();
        clock.Advance(1_000); // idle between jobs... the deadlock read started at 10
        Assert.Null(s.TryClaimIdle()); // busy: the caller goes to the control connection
        var pre = s.PreemptAsync(1_000);
        Assert.True(pre.IsCompleted); // the read-only job was cancelled at once (inline) and the connection claimed
        using (var lease = await pre)
        {
            Assert.NotNull(lease);
            clock.Advance(3_000);
            Assert.Single(conn.Starts("tick")); // nothing else runs while the kill holds the connection
        }
        clock.Advance(10);
        Assert.Equal(2, conn.Starts("tick").Count());
        var idle = s.TryClaimIdle();
        Assert.NotNull(idle);
        idle!.Dispose();
        Assert.Equal(0, conn.Overlaps);
        await s.StopAsync();
    });

    [Fact]
    public Task C8_a_tick_longer_than_the_interval_still_lets_each_secondary_job_start_within_two_of_its_periods() => Sim(async () =>
    {
        // Audit finding: the tick branch came first, and after a late tick the next one is due at once, so no secondary
        // job ran until the throttle had slowed every periodic job and then the tick (~130 s). C8 measured only the gaps
        // between runs, never the wait for the first one. Plan: "after every main tick, at most one due secondary job runs
        // before the next tick ... even if the next tick is already due".
        var (s, clock, conn) = Rig(new Durations(2_500, 600, 900, 4_500, 6_000));
        s.Start();
        clock.Run(60_000);
        foreach (var (job, period) in new[] { ("locks", 10_000L), ("topQueries", 10_000L), ("deadlocks", 30_000L) })
        {
            var first = conn.Starts(job).DefaultIfEmpty(long.MaxValue).Min();
            Assert.True(first <= 2 * period, $"{job}: first run at {first} ms");
        }
        // Still at most one secondary job between two ticks.
        var ticks = conn.Starts("tick").ToList();
        var secondaries = new[] { "locks", "topQueries", "deadlocks" }.SelectMany(conn.Starts).ToList();
        foreach (var (a, b) in ticks.Zip(ticks.Skip(1)))
            Assert.True(secondaries.Count(x => x > a && x < b) <= 1, $"more than one secondary job between ticks at {a} and {b}");
        Assert.Equal(0, conn.Overlaps);
        await s.StopAsync();
    });

    [Fact]
    public Task C13_the_throttle_slows_the_lowest_ranked_job_that_runs_never_a_disabled_one() => Sim(async () =>
    {
        // Audit finding: the throttle picked the lowest rank among all jobs, enabled or not, so with the deadlocks pane
        // closed the first throttle steps slowed a job that never runs while the header said "throttled".
        var clock = new FakeMonitorClock();
        var conn = new Conn();
        var s = new MonitorScheduler(clock, ct => conn.Use(clock, "tick", 1_500, ct), 2_000, inlineContinuations: true);
        var deadlocks = s.AddPeriodic("deadlocks", 30_000, 10_000, ct => conn.Use(clock, "deadlocks", 100, ct), 0);
        var locks = s.AddPeriodic("locks", 10_000, 5_000, ct => conn.Use(clock, "locks", 100, ct), 2);
        deadlocks.Enabled = false;
        s.Start();
        clock.Run(10_000 + 2_000);
        Assert.Equal(1, deadlocks.Multiplier);
        Assert.Equal(2, locks.Multiplier);
        Assert.True(s.Throttled);
        await s.StopAsync();
    });
}

/// <summary>Monitor memory over uptime (soak findings). The heap figure is process-wide, so these run serially.</summary>
[Collection("timing")]
public class MonitorMemoryTests
{
    [Fact]
    public void The_text_store_counts_the_memory_its_texts_hold_so_its_caps_bound_the_heap()
    {
        // Soak finding: the 4 MB cap counted UTF-8 text bytes; a short statement holds several times that on the heap
        // (UTF-16 text, id string, entry, LRU node, dictionary and sent-set slots). The count must never be below the heap.
        const int n = 20_000;
        var store = new TextStore { UnreferencedCap = long.MaxValue };
        var before = Heap.LiveBytes();
        Fill(store, n);
        var after = Heap.LiveBytes();
        Assert.Equal(n, store.Count);
        double heap = (after - before) / (double)n, counted = store.TotalBytes / (double)n;
        Assert.True(counted >= heap, $"counted {counted:0} B per text, the heap holds {heap:0} B");
        Assert.True(counted <= 1.5 * heap, $"counted {counted:0} B per text, the heap holds only {heap:0} B");
        GC.KeepAlive(store);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Fill(TextStore store, int n)
        {
            for (var i = 0; i < n; i++) store.Commit([store.Intern($"/*qchurn*/ SELECT {i * 7919} AS v, COUNT(*) FROM sys.all_objects WHERE {i} > 0")]);
            store.Commit([]); // each was live and sent once; now all unreferenced, in the LRU
        }
    }

    [Fact]
    public void The_top_queries_tracker_keeps_no_strings_per_tracked_entry()
    {
        // Soak finding: each tracked entry kept the provider record with its key, text-key and hash strings (~800 B; two
        // SQL Server monitors held 72,197 of them for a ~30,000-entry plan cache). Now a 128-bit key and seven counters.
        const int n = 50_000;
        var t = new TopQueriesTracker();
        var before = Heap.LiveBytes();
        Feed(t, n);
        var after = Heap.LiveBytes();
        Assert.Equal(n, t.TrackedEntries);
        var perEntry = (after - before) / (double)n;
        Assert.True(perEntry < 200, $"{perEntry:0} B retained per tracked entry");
        GC.KeepAlive(t);

        // The reads live in their own frame: a Debug build keeps a method's temporaries reachable until it returns.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Feed(TopQueriesTracker t, int n)
        {
            TopQueryRead Read(bool full, long execs) => new([.. Enumerable.Range(0, n).Select(i => new TopQueryCounter(
                TopQueryKey.Of("plan" + i), "0x00000000000000AA", null, $"0x{new string((char)(65 + i % 6), 128)}:{i}:{i + 40}", execs, execs, execs, execs, null, execs))], full, null);
            t.Update(Read(true, 1), "cpu", false);
            t.Update(Read(false, 2), "cpu", false); // every entry ran again: deltas for all of them
        }
    }

    [Fact]
    public Task An_idle_monitor_holds_no_more_memory_after_50000_waits_between_jobs() => Task.Run(async () =>
    {
        // Soak finding: each wait for the next due job left a continuation on the wake signal, which completes only when
        // something signals the loop (an interactive request, a kill). An idle monitor kept ~300 B per wait, for its lifetime.
        var clock = new FakeMonitorClock();
        var s = new MonitorScheduler(clock, ct => clock.WaitUntilAsync(clock.NowMs + 1, ct), 100, inlineContinuations: true);
        s.Start();
        clock.Run(1_000 * 100, 100);
        var before = Heap.LiveBytes();
        clock.Run(50_000L * 100, 100);
        var after = Heap.LiveBytes();
        Assert.True(s.TickCount > 50_000, $"{s.TickCount} ticks");
        Assert.True(after - before < 1_000_000, $"{(after - before) / 1024} KB retained after 50,000 waits");
        await s.StopAsync();
    });
}

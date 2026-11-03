using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Monitoring;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Regression tests for the Phase B hardening pass (audit of 6ddf0e5): monitor lifecycle races, the bounded connect,
/// rejected sign-ins, reconnect back-off, faults while processing a tick, which monitor answers the own-tab banner and
/// the banner check's bound, per-profile rollback tracking, RPC bounds and audit, the late Querithm-connection check,
/// the driver command-timeout backstop, staged-export cleanup and profile edits. Each failed (or hung) before its fix.
/// </summary>
public class MonitorHardeningTests
{
    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public FakeActivity Activity { get; } = new();
        public Profile Profile { get; }
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public FakeMonitorClock Clock { get; } = new();
        public MonitorService Monitors { get; }
        public MonitorServiceTests.CapturingLogger Log { get; } = new();
        public string DataDir { get; } = Directory.CreateTempSubdirectory("qmonh-").FullName;

        public Rig(Contract.Providers.ISchemaSource? schema = null)
        {
            Provider.Activity = Activity;
            var profiles = new ProfileStore(LocalStore.InMemory());
            Profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Querithm.Tests.Log.Null);
            Sessions.RememberSecret(Profile.Id, "x");
            Monitors = new MonitorService(Sessions, Notifier, Log, DataDir, schema, Clock) { InlineSchedulers = true };
        }

        public MonitorStartResult Start(int interval = 1_000, MonitorPane[]? panes = null, string? databaseFilter = null) =>
            Monitors.Start(new MonitorStartParams { ProfileId = Profile.Id, IntervalMs = interval, Panes = panes, DatabaseFilter = databaseFilter });

        public ActivityMonitor M(string id) => Monitors.Get(id);
        public IEnumerable<MonitorSnapshot> Snapshots(string id) => Notifier.Of<MonitorSnapshot>().Where(s => s.MonitorId == id);
        public int TickCalls => Activity.Calls.Count(c => c == "tick");

        public async ValueTask DisposeAsync()
        {
            await Monitors.DisposeAsync();
            await Sessions.DisposeAsync();
            try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Off xUnit's synchronization context, so the fake clock's inline continuations stay deterministic.</summary>
    static Task Sim(Func<Task> body) => Task.Run(body);

    static ActivityRow Row(long id, string status = "sleeping") =>
        new() { Key = new SessionKey { Id = id, Instance = FakeActivity.Inst(id) }, Status = status, BlockedBy = [], IsUserSession = true };

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public Task C14_a_stop_that_arrives_during_the_probe_ends_the_monitor_instead_of_leaving_its_scheduler_polling() => Sim(async () =>
    {
        // StopAsync cancels and then completes the lost-connection signal; a connect that finished its probe afterwards
        // installed a fresh signal and started a scheduler: a monitor no longer listed kept polling the server, and
        // (off the inline test scheduler) monitor.stop waited for it forever.
        await using var rig = new Rig();
        Task? stop = null;
        rig.Activity.NewSession = () =>
        {
            stop ??= rig.Monitors.StopAsync(rig.Monitors.All.Single().Id);
            return new FakeActivitySession();
        };
        rig.Start();
        Assert.NotNull(stop);
        await stop!.WaitAsync(TimeSpan.FromSeconds(5));
        var ticks = rig.TickCalls;
        rig.Clock.Run(10_000);
        Assert.Equal(ticks, rig.TickCalls);
        Assert.Equal(0, ticks);
        Assert.Empty(rig.Monitors.All);
        Assert.All(rig.Provider.Opened, c => Assert.Equal(1, c.DisposeCount));
    });

    [Fact]
    public Task L11_a_pause_made_while_the_monitor_reconnects_holds_for_the_new_connection() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start().MonitorId;
        rig.Clock.Run(3_000);
        rig.Provider.Opened.Last().Break();
        rig.Clock.Advance(1_000); // the next tick finds the connection broken: reconnect after the back-off
        Assert.Equal(MonitorConnectionState.Reconnecting, rig.M(id).Connection);
        rig.M(id).Pause();
        rig.Clock.Run(30_000);
        Assert.Equal(MonitorConnectionState.Connected, rig.M(id).Connection);
        var ticks = rig.TickCalls;
        rig.Clock.Run(30_000);
        Assert.Equal(ticks, rig.TickCalls); // the new scheduler started paused (L11: no queries while paused)
        Assert.True(rig.Snapshots(id).Last().Paused);
        rig.M(id).Resume();
        rig.Clock.Advance(1);
        Assert.True(rig.TickCalls > ticks);
    });

    [Fact]
    public Task L4_a_server_that_accepts_the_login_and_then_stalls_is_retried_instead_of_waited_on_forever() => Sim(async () =>
    {
        // Open, identity, session settings and probe had no time bound: the monitor stayed "connecting" with every kill
        // refused as disconnected.
        await using var rig = new Rig();
        var setup = rig.Activity.MonitorSessionSetupSql;
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText == setup) await Task.Delay(Timeout.Infinite, ct);
            return FakeResult.None;
        };
        var id = rig.Start().MonitorId;
        Assert.Equal(MonitorConnectionState.Connecting, rig.M(id).Connection);
        rig.Clock.Advance(45_500); // 2 × the profile's 15 s connect timeout + 15 s
        // Task.Delay's cancellation resumes on the thread pool, not inline on the fake clock: wait for it (bounded).
        Assert.True(SpinWait.SpinUntil(() => rig.Snapshots(id).Any(s => s.Connection == MonitorConnectionState.Reconnecting), TimeSpan.FromSeconds(5)), rig.M(id).Connection.ToString());
        Assert.Contains("took longer than 45 s", rig.Snapshots(id).Last(s => s.Connection == MonitorConnectionState.Reconnecting).ConnectionError);
        Assert.Equal(1, rig.Provider.Opened.First().DisposeCount);
    });

    [Fact]
    public Task L4_a_rejected_sign_in_stops_the_monitor_instead_of_retrying_into_an_account_lockout() => Sim(async () =>
    {
        await using var rig = new Rig();
        var attempts = 0;
        rig.Provider.OnOpen = _ => { attempts++; throw new FakeActivity.LoginRejectedException(); };
        var id = rig.Start().MonitorId;
        rig.Clock.Run(120_000);
        Assert.Equal(1, attempts); // before: a new sign-in every ≤ 30 s with the same remembered secret
        var stopped = Assert.Single(rig.Notifier.Of<MonitorStoppedEvent>(), e => e.MonitorId == id);
        Assert.Contains("rejected the monitor's sign-in", stopped.Reason);
        Assert.Equal(MonitorConnectionState.Stopped, rig.Snapshots(id).Last().Connection);
        // A monitor that stopped itself does not stay in the service's table once another starts (F17).
        rig.Provider.OnOpen = null;
        var next = rig.Start().MonitorId;
        Assert.Equal([next], rig.Monitors.All.Select(m => m.Id));
    });

    [Fact]
    public Task L4_an_unreachable_server_is_still_retried_with_a_growing_back_off() => Sim(async () =>
    {
        // The other side of the rule above: only a rejected sign-in is final.
        await using var rig = new Rig();
        var attempts = new List<long>();
        rig.Provider.OnOpen = _ => { attempts.Add(rig.Clock.NowMs); throw new InvalidOperationException("A network-related error occurred."); };
        rig.Start();
        rig.Clock.Run(60_000);
        Assert.Equal([0L, 1_000, 3_000, 7_000, 15_000, 31_000], attempts);
        Assert.Empty(rig.Notifier.Of<MonitorStoppedEvent>());
    });

    [Fact]
    public Task L4_a_server_that_drops_the_monitor_before_its_first_tick_gets_a_growing_back_off() => Sim(async () =>
    {
        // The back-off was reset on every successful connect, so a server that accepts and then drops the monitor (a
        // script that kills sessions named "Querithm Monitor") got a reconnect and a probe every second, for ever.
        await using var rig = new Rig();
        var opens = new List<long>();
        rig.Provider.OnOpen = _ => { opens.Add(rig.Clock.NowMs); return Task.CompletedTask; };
        rig.Activity.NewSession = () => new FakeActivitySession
        {
            OnTick = (_, _) => { rig.Provider.Opened.Last().Break(); throw new InvalidOperationException("connection reset"); },
        };
        rig.Start();
        rig.Clock.Run(40_000);
        var gaps = opens.Zip(opens.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps.Count >= 4, $"gaps {string.Join(", ", gaps)}");
        Assert.Equal([1_000L, 2_000, 4_000, 8_000], gaps.Take(4));
    });

    [Fact]
    public Task L15_a_fault_while_processing_a_tick_marks_its_panes_failed_instead_of_leaving_stale_data_as_ok() => Sim(async () =>
    {
        // The scheduler swallows job exceptions: a fault after the provider returned left the previous tick's data on
        // screen as "ok", with no error and no snapshot.
        await using var rig = new Rig();
        var id = rig.Start(1_000, [MonitorPane.Sessions]).MonitorId;
        rig.Clock.Run(2_000);
        Assert.Equal(PaneState.Ok, rig.Snapshots(id).Last(s => s.Sessions != null).Status.Single(x => x.Pane == MonitorPane.Sessions).State);
        rig.Activity.Session!.Rows.Add(Row(70, "running") with { BlockedBy = null! }); // a malformed row
        var seen = rig.Snapshots(id).Count();
        rig.Clock.Advance(1_000);
        var after = rig.Snapshots(id).Skip(seen).ToList();
        Assert.NotEmpty(after);
        var status = after.Last().Status.Single(x => x.Pane == MonitorPane.Sessions);
        Assert.Equal(PaneState.Error, status.State);
        Assert.Contains("could not process", status.Reason);
        Assert.Equal(MonitorConnectionState.Connected, rig.M(id).Connection);
        rig.Activity.Session.Rows.Clear();
        rig.Clock.Run(20_000);
        Assert.Equal(PaneState.Ok, rig.Snapshots(id).Last(s => s.Sessions != null).Status.Single(x => x.Pane == MonitorPane.Sessions).State);
    });

    // ------------------------------------------------------------------ blocking banner

    [Fact]
    public Task O13_only_a_monitor_reading_every_session_right_now_answers_the_banner() => Sim(async () =>
    {
        // RunningFor was "not stopped": a paused, reconnecting or filtered monitor silenced the shared check while no
        // snapshot answered — and a filtered snapshot cleared the banners of tabs it could not see.
        await using var rig = new Rig();
        var id = rig.Start(1_000).MonitorId;
        rig.Clock.Run(2_000);
        Assert.True(rig.Monitors.AnswersBannerFor(rig.Profile.Id));
        rig.M(id).Pause();
        rig.Clock.Run(10_000);
        Assert.False(rig.Monitors.AnswersBannerFor(rig.Profile.Id));
        rig.M(id).Resume();
        rig.Clock.Run(2_000);
        Assert.True(rig.Monitors.AnswersBannerFor(rig.Profile.Id));
        rig.Provider.OnOpen = _ => throw new InvalidOperationException("server down");
        rig.Provider.Opened.Last().Break();
        rig.Clock.Run(10_000);
        Assert.Equal(MonitorConnectionState.Reconnecting, rig.M(id).Connection);
        Assert.False(rig.Monitors.AnswersBannerFor(rig.Profile.Id));
        await rig.Monitors.StopAsync(id);

        await using var filtered = new Rig();
        var fid = filtered.Start(1_000, databaseFilter: "db1").MonitorId;
        var raised = 0;
        filtered.M(fid).Snapshot += (_, _) => raised++;
        filtered.Clock.Run(5_000);
        Assert.False(filtered.Monitors.AnswersBannerFor(filtered.Profile.Id));
        Assert.Equal(0, raised);
        Assert.Contains(filtered.Snapshots(fid), s => s.Sessions != null); // it still reads and publishes rows
    });

    [Fact]
    public Task O11_a_blocking_check_that_hangs_is_cut_off_and_its_connection_closed_not_returned() => Sim(async () =>
    {
        // The check ran with no bound on a pool connection without a command timeout: one hung query stopped the banner
        // on every profile (one cycle at a time) and kept a pool slot until restart.
        await using var rig = new Rig();
        var banner = new BlockingBannerService(rig.Sessions, rig.Notifier, Querithm.Tests.Log.Null, _ => false, timer: false) { CheckTimeout = TimeSpan.FromMilliseconds(200) };
        var s = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None);
        var pool = rig.Sessions.PoolFor(s);
        var hang = true;
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (hang && cmd.CommandText.StartsWith("FAKE blockedBy", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, ct);
            return FakeResult.None;
        };
        await banner.RunCycleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("did not finish", banner.LastError);
        var checkConn = Assert.Single(rig.Provider.Opened, c => c.Log.Any(l => l.StartsWith("FAKE blockedBy", StringComparison.Ordinal)));
        Assert.Equal(1, checkConn.DisposeCount);
        Assert.Equal(1, pool.Total); // only the tab's pinned connection is left
        hang = false;
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, banner.Checks);
        Assert.Equal(pool.Total, pool.IdleCount + 1);
    });

    // ------------------------------------------------------------------ kill

    [Fact]
    public Task K19_a_second_monitor_on_the_profile_refuses_to_kill_a_session_the_first_one_is_rolling_back() => Sim(async () =>
    {
        // The terminated set was per monitor: a second (or restarted) monitor did not know a killed sleeping session
        // (no request row while it rolls back) was rolling back, and sent KILL again, which the server accepts silently.
        await using var rig = new Rig();
        var target = Row(500).Key;
        var sessions = new List<FakeActivitySession>();
        rig.Activity.NewSession = () =>
        {
            var s = new FakeActivitySession();
            s.Rows.Add(Row(500));
            sessions.Add(s);
            return s;
        };
        var a = rig.Start().MonitorId;
        var b = rig.Start().MonitorId;
        rig.Clock.Run(2_000);
        var first = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = a, Target = target, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal(KillOutcome.Done, first.Outcome);
        rig.Clock.Run(2_000); // still there: rolling back
        var second = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = b, Target = target, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
        Assert.Equal(KillOutcome.Refused, second.Outcome);
        Assert.Equal("rollingBack", second.Code);
        Assert.Equal(1, sessions.Sum(s => s.Terminates));
    });

    [Fact]
    public Task K8_a_querithm_connection_registered_while_the_kill_waits_on_the_server_is_still_refused() => Sim(async () =>
    {
        // The registry was read once, before the kill lock, the control connection and the server-side preview.
        await using var rig = new Rig();
        var target = Row(600, "running").Key;
        var id = rig.Start().MonitorId;
        var session = rig.Activity.Session!;
        session.Rows.Add(Row(600, "running"));
        rig.Clock.Run(2_000);
        var late = new FakeDbConnection((_, _) => Task.FromResult(FakeResult.None), 600);
        session.OnPreview = (_, _) => { rig.Sessions.Registry.Register(late, target, ConnectionRegistry.Kinds.Pool, "600"); return Task.CompletedTask; };
        var r = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = target, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal(KillOutcome.Refused, r.Outcome);
        Assert.Equal("querithmConnection", r.Code);
        Assert.Equal(0, session.Terminates);
        GC.KeepAlive(late);
    });

    [Fact]
    public Task K12_K24_out_of_range_ids_and_overflowing_windows_are_rejected_and_boundary_refusals_are_audited() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start().MonitorId;
        rig.Clock.Run(2_000);
        // SQL Server narrows the id to int: 2^32 + 57 read session 57's data under a bogus id.
        var big = new SessionKey { Id = (1L << 32) + 57, Instance = FakeActivity.Inst((1L << 32) + 57) };
        foreach (var call in new Func<Task>[]
        {
            () => rig.Monitors.KillPreviewAsync(new MonitorSessionParams { MonitorId = id, Key = big }),
            () => rig.Monitors.RequestDetailAsync(new MonitorSessionParams { MonitorId = id, Key = big }),
            () => rig.Monitors.DiagnoseAsync(new MonitorSessionParams { MonitorId = id, Key = big }),
        })
            Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(call)).Code);
        // to - from overflowed past the 7-day check.
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            rig.Monitors.DeadlocksAsync(new DeadlocksParams { MonitorId = id, FromMs = long.MinValue, ToMs = long.MaxValue }))).Code);
        // K24: kills refused at the RPC boundary are audited too (ids and outcome only).
        var before = rig.Log.Lines.Count;
        Assert.Equal("notFound", (await Assert.ThrowsAsync<QuerithmException>(() => rig.Monitors.KillAsync(
            new MonitorKillParams { MonitorId = "nope", Target = big, Mode = KillMode.TerminateSession, ConfirmedKill = true }))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => rig.Monitors.KillAsync(
            new MonitorKillParams { MonitorId = id, Target = big, Mode = KillMode.TerminateSession, ConfirmedKill = true }))).Code);
        var audit = rig.Log.Lines.Skip(before).Where(l => l.StartsWith("Monitor kill:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, audit.Count);
        Assert.Contains("unknownMonitor", audit[0]);
        Assert.Contains("invalid", audit[1]);
    });

    [Fact]
    public Task Monitor_connections_carry_a_driver_command_timeout_above_every_job_timeout() => Sim(async () =>
    {
        // A cancel the server never acknowledges (SqlClient sends only an attention) held the job — and with it stop,
        // reconnect and kill preemption — for as long as the server stalled: monitor commands had no driver timeout.
        await using var rig = new Rig();
        rig.Start();
        rig.Clock.Run(1_000);
        var opened = Assert.Single(rig.Provider.OpenedProfiles);
        Assert.Equal(ActivityMonitor.CommandBackstopS, opened.CommandTimeoutS);
        Assert.True(ActivityMonitor.CommandBackstopS * 1_000L > 10_000); // above the longest job timeout (deadlocks, kill)
        Assert.Equal("Querithm Monitor", opened.Options!["appName"]);
        Assert.NotEqual(ActivityMonitor.CommandBackstopS, rig.Profile.CommandTimeoutS); // the user's profile is not changed
    });

    [Fact]
    public Task C14_stop_waits_for_a_running_kill_before_closing_the_connection_it_runs_on() => Sim(async () =>
    {
        // The kill's time limits were not linked to stop, and stop closed the monitor connection while a KILL still ran on
        // it (ADO.NET connections are not concurrent) — or left the control connection open for up to 15 s.
        await using var rig = new Rig();
        var target = Row(700).Key;
        var id = rig.Start().MonitorId;
        var session = rig.Activity.Session!;
        session.Rows.Add(Row(700));
        rig.Clock.Run(2_000);
        var monitorConn = rig.Provider.Opened.Last();
        var disposedWhileRunning = -1;
        var entered = new TaskCompletionSource();
        session.OnTerminate = async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                // A driver takes a moment to return after a cancel (SqlClient waits for the server's attention ack).
                await Task.Delay(200);
                disposedWhileRunning = monitorConn.DisposeCount;
                throw;
            }
            return new KillResult { Outcome = KillOutcome.Done, Message = "terminated" };
        };
        session.Rows.Add(Row(701));
        rig.Clock.Run(1_000);
        var kill = rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = target, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A second kill waits for the per-server kill lock behind the first.
        var queued = rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Row(701).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        await rig.Monitors.StopAsync(id).WaitAsync(TimeSpan.FromSeconds(10));
        var result = await kill.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, disposedWhileRunning); // the kill ended (cancelled by the stop) before its connection was closed
        Assert.Equal(1, monitorConn.DisposeCount);
        Assert.Equal("monitorStopped", result.Code);
        Assert.Contains("may still take effect", result.Message); // it had reached the server
        Assert.Equal("monitorStopped", second.Code);
        Assert.Contains("nothing was sent", second.Message); // it had not
        Assert.Equal(1, session.Terminates);
    });

    [Fact]
    public Task K19_every_monitor_ends_the_rollback_progress_it_showed_when_another_one_pruned_the_session() => Sim(async () =>
    {
        // With the terminated set shared, the first monitor whose read showed the session gone removed it, and the other
        // monitor never sent "done": its UI kept the last rollback progress for ever.
        await using var rig = new Rig();
        var target = Row(510).Key;
        var sessions = new List<FakeActivitySession>();
        rig.Activity.NewSession = () =>
        {
            var s = new FakeActivitySession();
            s.Rows.Add(Row(510));
            sessions.Add(s);
            return s;
        };
        var a = rig.Start().MonitorId;
        var b = rig.Start().MonitorId;
        rig.Clock.Run(2_000);
        Assert.Equal(KillOutcome.Done, (await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = a, Target = target, Mode = KillMode.TerminateSession, ConfirmedKill = true })).Outcome);
        rig.Clock.Run(2_000);
        foreach (var id in new[] { a, b }) Assert.Contains(rig.Snapshots(id), s => s.Rollbacks?.Any(r => r.Key == target && !r.Done) == true);
        foreach (var s in sessions) s.Rows.Clear(); // the rollback ended: the session is gone
        rig.Clock.Run(3_000);
        foreach (var id in new[] { a, b }) Assert.Contains(rig.Snapshots(id), s => s.Rollbacks?.Any(r => r.Key == target && r.Done) == true);
    });

    sealed class CountingSchema(string db, IEnumerable<SnapObject> objects) : Contract.Providers.ISchemaSource
    {
        public int DatabaseCalls, GetCalls;
        public IReadOnlyList<string> Databases(string? profileId) { Interlocked.Increment(ref DatabaseCalls); return [db]; }
        public SchemaSnapshot? Get(string? profileId, string? database)
        {
            Interlocked.Increment(ref GetCalls);
            return database == db ? new SchemaSnapshot { ProfileId = profileId ?? "", Database = db, Objects = [.. objects], Fks = [] } : null;
        }
    }

    [Fact]
    public Task B2_lock_names_come_from_one_metadata_cache_read_per_job_not_one_per_lock_row() => Sim(async () =>
    {
        // Each lock row read the cache from the local store (and scanned it) under the monitor's state lock: 2,000 rows held
        // up ticks, publishes and kills for that long.
        var schema = new CountingSchema("db1", Enumerable.Range(1, 500).Select(i => new SnapObject { Id = i, Schema = "dbo", Name = $"t{i}" }));
        await using var rig = new Rig(schema);
        var id = rig.Start(1_000, [MonitorPane.Sessions, MonitorPane.Locks]).MonitorId;
        rig.Activity.Session!.LockRows = [.. Enumerable.Range(0, 2_000).Select(i => new LockRow
        {
            Key = Row(800 + i % 50).Key, ResourceType = "OBJECT", Database = "db1", Object = $"db1.{1 + i % 500}", Mode = "X", Status = "granted", Count = 1,
        })];
        rig.Clock.Run(12_000); // the locks job runs every 5 ticks
        var locks = rig.Snapshots(id).Last(s => s.Locks is { Length: > 0 }).Locks!;
        Assert.Equal(2_000, locks.Length);
        Assert.All(locks, l => Assert.StartsWith("dbo.t", l.Object));
        var runs = rig.Snapshots(id).Count(s => s.Locks is { Length: > 0 });
        Assert.True(schema.DatabaseCalls <= runs && schema.GetCalls <= runs, $"{schema.DatabaseCalls} database lists and {schema.GetCalls} snapshots for {runs} lock reads");
    });

    [Fact]
    public Task O11_a_rejected_sign_in_pauses_the_blocking_check_until_the_connection_is_set_up_again() => Sim(async () =>
    {
        // The banner check retried a rejected sign-in every 10 s with the same secret (~360 failed logins an hour while a
        // tab kept a transaction open overnight after a password change): the account lockout the monitor no longer causes.
        await using var rig = new Rig();
        var banner = new BlockingBannerService(rig.Sessions, rig.Notifier, Querithm.Tests.Log.Null, _ => false, timer: false);
        var s = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None);
        var attempts = 0;
        rig.Provider.OnOpen = _ => { attempts++; throw new FakeActivity.LoginRejectedException(); };
        for (var i = 0; i < 5; i++) await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, attempts);
        var told = Assert.Single(rig.Notifier.Of<SessionBlockingEvent>(), e => e.Unavailable != null);
        Assert.Contains("rejected the sign-in", told.Unavailable);
        rig.Sessions.RememberSecret(rig.Profile.Id, "the new password"); // the connection is set up again
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
        // The user enters another password while a check's open (with the previous one) is still failing: the failure
        // counts against the credentials it was made with, so the next cycle checks with the new secret.
        rig.Sessions.RememberSecret(rig.Profile.Id, "a third password"); // un-mutes: the next cycle opens (attempt 3)
        rig.Provider.OnOpen = _ =>
        {
            attempts++;
            if (attempts == 3) rig.Sessions.RememberSecret(rig.Profile.Id, "the newest password");
            throw new FakeActivity.LoginRejectedException();
        };
        await banner.RunCycleAsync(CancellationToken.None);
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(4, attempts);
    });

    // ------------------------------------------------------------------ exports and profiles

    [Fact]
    public void H7_staged_exports_older_than_ten_minutes_are_deleted_and_nothing_else_is()
    {
        // Staged exports were never deleted (the webview cannot): every .xdl (query text, logins, hosts) and history
        // export stayed in app data for good, also after a cancelled save dialog.
        var root = Directory.CreateTempSubdirectory("qexp-").FullName;
        var outside = Directory.CreateTempSubdirectory("qexp-out-").FullName;
        try
        {
            var dir = Path.Combine(root, "exports");
            Directory.CreateDirectory(dir);
            var now = DateTime.UtcNow;
            string F(string name, TimeSpan age)
            {
                var p = Path.Combine(dir, name);
                File.WriteAllText(p, "x");
                File.SetLastWriteTimeUtc(p, now - age);
                return p;
            }
            var oldJson = F("history-old.json", TimeSpan.FromMinutes(11));
            var oldXdl = F("deadlock-old.xdl", TimeSpan.FromMinutes(30));
            var oldPartial = F("history-cut.json.partial", TimeSpan.FromMinutes(11));
            var fresh = F("history-new.json", TimeSpan.FromMinutes(2));
            var other = F("notes.txt", TimeSpan.FromDays(3));
            var userFile = Path.Combine(outside, "keep.json");
            File.WriteAllText(userFile, "the user's own file");
            File.SetLastWriteTimeUtc(userFile, now - TimeSpan.FromDays(3));
            var link = Path.Combine(dir, "link.json");
            var canLink = TryLink(() => File.CreateSymbolicLink(link, userFile)); // Windows without Developer Mode cannot

            var exporter = new MonitorExporter(dir);
            Assert.Equal(3, exporter.Sweep(now));
            Assert.False(File.Exists(oldJson));
            Assert.False(File.Exists(oldXdl));
            Assert.False(File.Exists(oldPartial));
            Assert.True(File.Exists(fresh)); // a save dialog may still be open
            Assert.True(File.Exists(other));
            if (canLink) Assert.NotNull(new FileInfo(link).LinkTarget); // a link is never followed or removed
            Assert.Equal("the user's own file", File.ReadAllText(userFile));
            // Each new export sweeps first.
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow - TimeSpan.FromMinutes(20));
            exporter.WriteText("history", ".json", "{}");
            Assert.False(File.Exists(fresh));

            // An exports folder that is itself a link: nothing is deleted or written through it.
            var linkedDir = Path.Combine(root, "exports-linked");
            var oldOutside = Path.Combine(outside, "old.json");
            File.WriteAllText(oldOutside, "x");
            File.SetLastWriteTimeUtc(oldOutside, now - TimeSpan.FromDays(3));
            if (TryLink(() => Directory.CreateSymbolicLink(linkedDir, outside)))
            {
                var linked = new MonitorExporter(linkedDir);
                Assert.Equal(0, linked.Sweep(now));
                Assert.True(File.Exists(oldOutside));
                Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => linked.WriteText("history", ".json", "{}")).Code);
            }
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    static bool TryLink(Action create)
    {
        try { create(); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    [Fact]
    public async Task Saving_a_profile_stops_its_monitors_which_would_keep_the_old_host_and_options()
    {
        // A monitor kept the profile it started with while kills read the environment fresh: after an edit it still
        // watched (and killed on) the old host, and a Prod → Dev edit dropped the typed confirmation for that server.
        var provider = new FakeDbProvider { Activity = new FakeActivity() };
        var notifier = new CapturingNotifier();
        var data = Directory.CreateTempSubdirectory("qhost-").FullName;
        try
        {
            await using var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), notifier, NullLoggerFactory.Instance, new ProviderRegistry([provider]), data);
            var rpc = new RpcServer(services, () => { });
            var p = await rpc.ProfileSave(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "old-host", Auth = AuthKind.Sql, Username = "u", Environment = EnvironmentKind.Prod });
            services.Sessions.RememberSecret(p.Id, "x");
            var m = await rpc.MonitorStart(new MonitorStartParams { ProfileId = p.Id, IntervalMs = 1_000 });
            // A rename or a password-retention change (the sign-in dialog saves it) builds the same connection: it keeps running.
            await rpc.ProfileSave(p with { Name = "renamed", PasswordRetention = "session" });
            Assert.DoesNotContain(notifier.Of<MonitorStoppedEvent>(), e => e.MonitorId == m.MonitorId);
            Assert.False(services.Monitors.Get(m.MonitorId).Stopped);
            await rpc.ProfileSave(p with { Host = "new-host", Environment = EnvironmentKind.Dev });
            var stopped = Assert.Single(notifier.Of<MonitorStoppedEvent>(), e => e.MonitorId == m.MonitorId);
            Assert.Contains("profile was changed", stopped.Reason);
            Assert.Equal("notFound", Assert.Throws<QuerithmException>(() => services.Monitors.Get(m.MonitorId)).Code);
        }
        finally { try { Directory.Delete(data, true); } catch { /* best effort */ } }
    }
}

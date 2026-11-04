using System.Data.Common;
using System.Text;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Monitoring;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;
using P = Querithm.Contract.Providers;

namespace Querithm.Tests;

/// <summary>
/// The monitor end to end in the sidecar with fake connections (Phase B matrix L*, K9/K16/K20/K21, C3–C5, O*, H5–H7,
/// M14): real MonitorService, ActivityMonitor, scheduler, SessionManager and pool; a fake clock where timing matters.
/// Serial: L13 measures the process heap, which other classes running alongside raised by ~30 MB.
/// </summary>
[Collection("timing")]
public class MonitorServiceTests
{
    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public FakeActivity Activity { get; } = new();
        public Profile Profile { get; }
        public ProfileStore Profiles { get; }
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public FakeMonitorClock Clock { get; } = new();
        public MonitorService Monitors { get; }
        public InvariantSink Invariants { get; } = FakeDbInvariants.Capture();
        public string DataDir { get; } = Directory.CreateTempSubdirectory("qmon-").FullName;
        public CapturingLogger Log { get; } = new();

        public Rig(EnvironmentKind env = EnvironmentKind.Dev, bool fakeClock = true)
        {
            Provider.Activity = Activity;
            var store = LocalStore.InMemory();
            Profiles = new ProfileStore(store);
            Profile = Profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u", Environment = env });
            Sessions = new SessionManager(Profiles, new ProviderRegistry([Provider]), Notifier, Querithm.Tests.Log.Null);
            Sessions.RememberSecret(Profile.Id, "x");
            Monitors = new MonitorService(Sessions, Notifier, Log, DataDir, null, fakeClock ? Clock : null) { InlineSchedulers = fakeClock };
        }

        public MonitorStartResult Start(int interval = 1_000, MonitorPane[]? panes = null) =>
            Monitors.Start(new MonitorStartParams { ProfileId = Profile.Id, IntervalMs = interval, Panes = panes });

        public ActivityMonitor M(string id) => Monitors.Get(id);
        public IEnumerable<MonitorSnapshot> Snapshots(string id) => Notifier.Of<MonitorSnapshot>().Where(s => s.MonitorId == id);

        public async ValueTask DisposeAsync()
        {
            await Monitors.DisposeAsync();
            await Sessions.DisposeAsync();
            try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
            Assert.Empty(Invariants.Violations);
        }
    }

    public sealed class CapturingLogger : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception));
    }

    /// <summary>Runs off xUnit's synchronization context so the fake clock's inline continuations stay deterministic.</summary>
    static Task Sim(Func<Task> body) => Task.Run(body);

    static ActivityRow Foreign(long id, string status = "sleeping") => new() { Key = new SessionKey { Id = id, Instance = FakeActivity.Inst(id) }, Status = status, BlockedBy = [], IsUserSession = true };

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public Task L1_L12_L14_two_monitors_on_one_profile_are_independent_the_cap_is_eight_and_bad_parameters_are_rejected() => Sim(async () =>
    {
        await using var rig = new Rig();
        var a = rig.Start();
        var b = rig.Start();
        Assert.NotEqual(a.MonitorId, b.MonitorId);
        Assert.Equal(2, rig.Provider.Opened.Count(c => rig.Sessions.Registry.Find(rig.Sessions.Registry.KeyOf(c)!)?.Kind == ConnectionRegistry.Kinds.Monitor));
        for (var i = 0; i < 6; i++) rig.Start();
        var ex = Assert.Throws<QuerithmException>(() => rig.Start());
        Assert.Equal("tooManyMonitors", ex.Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Start(999)).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Start(30_001)).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Start(2_000, [MonitorPane.QueryStore])).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Start(2_000, [(MonitorPane)99])).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Monitors.SetInterval(new MonitorSetIntervalParams { MonitorId = a.MonitorId, IntervalMs = 0 })).Code);
    });

    [Fact]
    public Task L11_paused_monitor_sends_no_queries() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start().MonitorId;
        rig.Clock.Run(5_000);
        var session = rig.Activity.Session!;
        Assert.True(session.Ticks >= 5);
        rig.M(id).Pause();
        var calls = rig.Activity.Calls.Count;
        rig.Clock.Run(60_000);
        Assert.Equal(calls, rig.Activity.Calls.Count);
        Assert.True(rig.Snapshots(id).Last().Paused);
        rig.M(id).Resume();
        rig.Clock.Advance(1);
        Assert.True(rig.Activity.Calls.Count > calls);
    });

    [Fact]
    public Task L2_C14_stop_while_a_tick_runs_cancels_it_closes_the_connection_and_sends_nothing_late() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start().MonitorId;
        var entered = new TaskCompletionSource();
        var cancelled = false;
        rig.Activity.Session!.OnTick = async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled = true; throw; }
        };
        rig.Clock.Advance(1_000);
        await entered.Task.WaitAsync(TestTime.Of(TimeSpan.FromSeconds(5)));
        var monitorConn = rig.Provider.Opened.Single(c => rig.Sessions.Registry.KeyOf(c) is { } k && k == rig.Activity.Session!.Self);
        await rig.Monitors.StopAsync(id);
        Assert.True(cancelled);
        Assert.Equal(1, monitorConn.DisposeCount);
        var after = rig.Snapshots(id).Count();
        rig.Clock.Run(10_000);
        Assert.Equal(after, rig.Snapshots(id).Count());
        Assert.Equal("notFound", Assert.Throws<QuerithmException>(() => rig.M(id)).Code); // K16
    });

    [Fact]
    public async Task L9_sidecar_shutdown_disposes_every_monitor_within_two_seconds()
    {
        var rig = new Rig(fakeClock: false);
        for (var i = 0; i < 8; i++) rig.Start();
        await Task.Delay(200);
        foreach (var s in rig.Activity.Session is { } ? new[] { rig.Activity.Session } : []) s!.OnTick = async (_, ct) => await Task.Delay(Timeout.Infinite, ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await rig.Monitors.DisposeAsync();
        Assert.True(sw.ElapsedMilliseconds < TestTime.Budget(2_000), $"{sw.ElapsedMilliseconds} ms");
        Assert.All(rig.Provider.Opened, c => Assert.Equal(1, c.DisposeCount));
        await rig.DisposeAsync();
    }

    [Fact]
    public Task L13_starting_and_stopping_a_thousand_times_leaks_no_connection_entry_or_memory() => Sim(async () =>
    {
        await using var rig = new Rig();
        for (var i = 0; i < 50; i++) await rig.Monitors.StopAsync(rig.Start().MonitorId); // warm up
        var before = Heap.LiveBytes();
        for (var i = 0; i < 1_000; i++)
        {
            var id = rig.Start().MonitorId;
            rig.Clock.Advance(1_000);
            await rig.Monitors.StopAsync(id);
        }
        rig.Sessions.Registry.Prune();
        var after = Heap.LiveBytes();
        Assert.All(rig.Provider.Opened, c => Assert.Equal(1, c.DisposeCount));
        Assert.Equal(0, rig.Sessions.Registry.Count);
        Assert.Empty(rig.Monitors.All);
        // The notifier keeps every snapshot it captured; subtract its share by clearing it before measuring growth.
        while (rig.Notifier.All.TryDequeue(out _)) { }
        var grew = Heap.LiveBytes() - before;
        Assert.True(grew < 8 * 1024 * 1024, $"grew {grew / 1024} KB ({(after - before) / 1024} KB with the captured snapshots)");
    });

    [Fact]
    public Task L15_a_faulted_pane_errors_with_back_off_while_the_other_panes_continue() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start(1_000, [MonitorPane.Sessions, MonitorPane.Waits]);
        var waitsRequested = new List<long>();
        rig.Activity.Session!.WaitsError = "Invalid object name 'sys.dm_os_wait_stats'";
        rig.Activity.Session.OnTick = (req, _) =>
        {
            if (req.Waits) waitsRequested.Add(rig.Clock.NowMs);
            return Task.CompletedTask;
        };
        rig.Clock.Run(20_000);
        var last = rig.Snapshots(id.MonitorId).Last(s => s.Status.Any(x => x.Pane == MonitorPane.Waits && x.State == PaneState.Error));
        var waits = last.Status.Single(x => x.Pane == MonitorPane.Waits);
        Assert.NotNull(waits.RetryInMs);
        Assert.Contains("Invalid object name", waits.Reason);
        // Back-off: the failing pane is asked less often than every tick, and sessions keep flowing.
        Assert.True(waitsRequested.Count < 10, $"{waitsRequested.Count} retries in 20 ticks");
        Assert.True(rig.Snapshots(id.MonitorId).Count(s => s.Sessions != null) >= 10);
    });

    [Fact]
    public Task L4_a_lost_connection_reconnects_with_back_off_and_probes_again() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var first = rig.Activity.Session!;
        var conn = rig.Provider.Opened.Single(c => rig.Sessions.Registry.KeyOf(c) == first.Self);
        conn.Break();
        rig.Clock.Advance(1_000); // the tick fails: connection broken
        var down = rig.Snapshots(id).Last();
        Assert.Equal(MonitorConnectionState.Reconnecting, down.Connection);
        Assert.Equal(1_000, down.RetryInMs);
        Assert.All(down.Status, s => Assert.Equal(PaneState.Error, s.State));
        rig.Provider.OnOpen = _ => throw new InvalidOperationException("server down");
        rig.Clock.Advance(1_000);
        Assert.Equal(2_000, rig.Snapshots(id).Last().RetryInMs); // 1 s → 2 s → … → 30 s
        rig.Provider.OnOpen = null;
        rig.Clock.Advance(2_000);
        Assert.NotSame(first, rig.Activity.Session); // probe re-run
        Assert.Equal(MonitorConnectionState.Connected, rig.Snapshots(id).Last().Connection);
        Assert.NotNull(rig.Snapshots(id).Last(s => s.Capabilities != null));
    });

    // ------------------------------------------------------------------ kill paths

    [Fact]
    public Task K20_K21_two_kills_of_one_target_send_one_command_and_a_disconnected_monitor_refuses() => Sim(async () =>
    {
        await using var rig = new Rig(fakeClock: true);
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Foreign(55)); return s; };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var gate = new TaskCompletionSource<Querithm.Contract.KillResult>();
        rig.Activity.Session!.OnTerminate = (_, _) => gate.Task;
        var p = new MonitorKillParams { MonitorId = id, Target = Foreign(55).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true };
        var first = rig.Monitors.KillAsync(p);
        var second = await rig.Monitors.KillAsync(p);
        Assert.Equal("inProgress", second.Code);
        gate.SetResult(new KillResult { Outcome = KillOutcome.Done, Message = "ok" });
        Assert.Equal(KillOutcome.Done, (await first).Outcome);
        Assert.Equal(1, rig.Activity.Session.Terminates);
        rig.Provider.Opened.Single(c => rig.Sessions.Registry.KeyOf(c) == rig.Activity.Session.Self).Break();
        rig.Clock.Advance(1_000);
        var r = await rig.Monitors.KillAsync(p);
        Assert.Equal("disconnected", r.Code);
    });

    [Fact]
    public Task C2_C3_a_kill_during_a_long_job_uses_a_control_connection_outside_an_exhausted_pool_and_changes_no_pool_state() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Foreign(66)); return s; };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        // Eight tabs hold every pool slot; a ninth borrower is queued.
        var pool = rig.Sessions.PoolFor(rig.Profile, rig.Provider, null);
        var held = new List<PhysicalConnection>();
        for (var i = 0; i < 8; i++) held.Add(await pool.BorrowAsync(null, null, CancellationToken.None));
        var queued = pool.BorrowAsync(null, null, CancellationToken.None);
        // A long job runs on the monitor connection.
        var inJob = new TaskCompletionSource();
        rig.Activity.Session!.OnTick = async (_, ct) => { inJob.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        rig.Clock.Advance(1_000);
        await inJob.Task;
        var opened = rig.Provider.Opened.Count;
        var r = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(66).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal(KillOutcome.Done, r.Outcome);
        Assert.Equal("control", r.Path);
        Assert.Equal(opened + 1, rig.Provider.Opened.Count);
        var control = rig.Provider.Opened.Last();
        Assert.Equal(1, control.DisposeCount); // closed right after
        Assert.True(rig.Activity.WasPrepared(control)); // errors raise on it (SQL Server: no user errors as messages)
        Assert.Equal(8, pool.Total);
        Assert.Equal(1, pool.WaiterCount);
        Assert.False(queued.IsCompleted);
        foreach (var c in held) pool.Return(c);
        pool.Return(await queued);
    });

    [Fact]
    public Task C4_when_no_control_connection_opens_the_running_job_is_preempted_and_the_kill_runs_on_the_monitor_connection() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Foreign(67)); return s; };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var cancelled = new TaskCompletionSource();
        rig.Activity.Session!.OnTick = async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        };
        rig.Clock.Advance(1_000);
        rig.Provider.OnOpen = _ => throw new InvalidOperationException("too many connections");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(67).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal(KillOutcome.Done, r.Outcome);
        Assert.Equal("fallback", r.Path);
        Assert.True(cancelled.Task.IsCompleted);
        Assert.True(sw.ElapsedMilliseconds <= ActivityMonitor.ControlConnectTimeoutMs + TestTime.Budget(1_000)); // only the slack scales
    });

    [Fact]
    public async Task C5_two_kills_on_one_profile_are_serialised_and_the_second_gives_up_after_five_seconds()
    {
        await using var rig = new Rig(fakeClock: false);
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.AddRange([Foreign(70), Foreign(71)]); return s; };
        var id = rig.Start(30_000).MonitorId;
        await Task.Delay(300);
        var gate = new TaskCompletionSource<KillResult>();
        rig.Activity.Session!.OnTerminate = (k, _) => k.Id == 70 ? gate.Task : Task.FromResult(new KillResult { Outcome = KillOutcome.Done, Message = "ok" });
        var first = rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(70).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        await Task.Delay(100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var second = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(71).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal("inProgress", second.Code);
        Assert.Contains("Another kill", second.Message);
        Assert.InRange(sw.ElapsedMilliseconds, 4_500, 6_500);
        gate.SetResult(new KillResult { Outcome = KillOutcome.Done, Message = "ok" });
        Assert.Equal(KillOutcome.Done, (await first).Outcome);
    }

    [Fact]
    public Task P3_P4_a_high_rollback_risk_recomputed_on_the_server_needs_the_extra_confirm() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Activity.NewSession = () =>
        {
            var s = new FakeActivitySession { Preview = new KillPreview { Key = Foreign(80).Key, TxCount = 1, LogBytesUsed = 500L * 1024 * 1024, Risk = RollbackRisk.Low } };
            s.Rows.Add(Foreign(80));
            return s;
        };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        // The client believed the risk was low (P4); the sidecar's own preview says high (P3).
        var r = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(80).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = false });
        Assert.Equal("longRollback", r.Code);
        Assert.Equal(0, rig.Activity.Session!.Terminates);
        var ok = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(80).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
        Assert.Equal(KillOutcome.Done, ok.Outcome);
    });

    [Fact]
    public Task K24_every_kill_attempt_is_logged_without_query_text_login_or_host() => Sim(async () =>
    {
        await using var rig = new Rig();
        const string text = "UPDATE k24_payroll SET salary = 1 WHERE emp = 'k24_text_marker'";
        rig.Activity.NewSession = () =>
        {
            var s = new FakeActivitySession();
            s.Rows.Add(Foreign(90) with { Login = "alice_secret_login", Host = "secret-host-01", Program = "secret-app", TextId = "t90" });
            return s;
        };
        rig.Provider.Activity = ReshapingActivity.WithTexts(rig.Activity, new Dictionary<string, string> { ["t90"] = text });
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        // The row really carries the text, login and host into the monitor (otherwise the audit check below proves nothing).
        var row = rig.Snapshots(id).Last(s => s.Sessions != null).Sessions!.Single(r => r.Key.Id == 90);
        Assert.NotNull(row.TextId);
        Assert.Equal(text, rig.Monitors.Texts(new MonitorTextsParams { MonitorId = id, Ids = [row.TextId!] }).Texts[row.TextId!]);
        Assert.Equal(("alice_secret_login", "secret-host-01"), (row.Login, row.Host));
        await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(90).Key, Mode = KillMode.TerminateSession, ConfirmedKill = false });
        await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(90).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        var kills = rig.Log.Lines.Where(l => l.StartsWith("Monitor kill", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, kills.Count);
        Assert.All(kills, l => Assert.Contains("session 90", l));
        // Nothing the monitor logged — audit lines or any other — carries the text, its id, the login, host or program.
        Assert.All(rig.Log.Lines, l =>
        {
            foreach (var secret in new[] { "k24_text_marker", "k24_payroll", row.TextId!, "alice", "secret-host", "secret-app" }) Assert.DoesNotContain(secret, l);
        });
    });

    [Fact]
    public Task M14_the_header_reports_one_connection_and_two_while_a_kill_holds_the_control_connection() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Foreign(95)); return s; };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        Assert.Equal(1, rig.M(id).ConnectionCount);
        rig.Activity.Session!.OnTick = async (_, ct) => await Task.Delay(Timeout.Infinite, ct);
        rig.Clock.Advance(1_000);
        var during = 0;
        rig.Activity.Session.OnTerminate = (_, _) => { during = rig.M(id).ConnectionCount; return Task.FromResult(new KillResult { Outcome = KillOutcome.Done, Message = "ok" }); };
        await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(95).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal(2, during);
        Assert.Equal(1, rig.M(id).ConnectionCount);
    });

    // ------------------------------------------------------------------ registry / registration window

    [Fact]
    public Task K9_a_pool_connection_killed_in_its_registration_window_is_an_ordinary_open_failure() => Sim(async () =>
    {
        await using var rig = new Rig();
        var pool = rig.Sessions.PoolFor(rig.Profile, rig.Provider, null);
        var once = 1;
        rig.Activity.OnIdentity = (c, _) =>
        {
            if (Interlocked.Exchange(ref once, 0) == 1) { ((FakeDbConnection)c).Break(); throw new InvalidOperationException("killed during login"); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.BorrowAsync(null, null, CancellationToken.None));
        Assert.Equal(0, pool.Total);
        Assert.Equal(1, rig.Provider.Opened.First().DisposeCount); // nothing orphaned
        var ok = await pool.BorrowAsync(null, null, CancellationToken.None);
        Assert.NotNull(rig.Sessions.Registry.KeyOf(ok.Db));
        pool.Return(ok);
    });

    [Fact]
    public Task An_identity_read_the_server_refuses_keeps_the_connection_for_queries_and_refuses_every_kill_until_it_closes() => Sim(async () =>
    {
        // §6 rule 7 (revised): a working connection whose server refuses the own-row read (an explicit DENY, a compatible server
        // without the view) still serves queries; the monitor cannot tell its server session from a foreign one, so kills are
        // refused while it is open — with the reason, nothing sent.
        await using var rig = new Rig();
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Foreign(55)); return s; };
        var id = rig.Start().MonitorId; // the monitor's own read works
        rig.Clock.Advance(10);
        var refused = 0;
        rig.Activity.OnIdentity = (_, _) => { Interlocked.Increment(ref refused); throw new P.IdentityUnavailableException("the server refused it (test)"); };
        var pool = rig.Sessions.PoolFor(rig.Profile, rig.Provider, null);
        var conn = await pool.BorrowAsync(null, null, CancellationToken.None);
        Assert.Equal(System.Data.ConnectionState.Open, conn.Db.State);
        Assert.Equal(0, ((FakeDbConnection)conn.Db).DisposeCount);
        Assert.Null(rig.Sessions.Registry.KeyOf(conn.Db));
        Assert.True(rig.Sessions.Registry.IsUnidentified(conn.Db));
        Assert.Equal("the server refused it (test)", rig.Sessions.Registry.UnidentifiedReason());

        var r = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(55).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal((KillOutcome.Refused, "querithmUnidentified"), (r.Outcome, r.Code));
        Assert.Contains("the server refused it (test)", r.Message);
        Assert.Equal(0, rig.Activity.Session!.Terminates);

        // Reuse and the check after every run read the identity again only when the driver reconnected: no I/O per query.
        pool.Return(conn);
        var again = await pool.BorrowAsync(null, null, CancellationToken.None);
        Assert.Same(conn, again);
        await rig.Sessions.ReidentifyAsync(again, CancellationToken.None);
        Assert.Equal(1, refused);
        // A driver reconnect reads it again; when the server now answers, the connection is identified like any other.
        ((FakeDbConnection)again.Db).Reconnect();
        rig.Activity.OnIdentity = null;
        await rig.Sessions.ReidentifyAsync(again, CancellationToken.None);
        Assert.NotNull(rig.Sessions.Registry.KeyOf(again.Db));
        Assert.Null(rig.Sessions.Registry.UnidentifiedReason());
        // Unidentified again after another reconnect; closing it is what lets kills through again.
        ((FakeDbConnection)again.Db).Reconnect();
        rig.Activity.OnIdentity = (_, _) => throw new P.IdentityUnavailableException("refused again (test)");
        await rig.Sessions.ReidentifyAsync(again, CancellationToken.None);
        Assert.Null(rig.Sessions.Registry.KeyOf(again.Db));
        Assert.Equal("refused again (test)", rig.Sessions.Registry.UnidentifiedReason());
        await pool.DiscardAsync(again);
        Assert.Null(rig.Sessions.Registry.UnidentifiedReason());
        var done = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(55).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal(KillOutcome.Done, done.Outcome);
        Assert.Equal(1, rig.Activity.Session!.Terminates);
    });

    [Fact]
    public Task A_refused_identity_read_still_fails_the_open_when_the_connection_broke_and_stops_a_monitor_that_cannot_identify_itself() => Sim(async () =>
    {
        await using var rig = new Rig();
        var pool = rig.Sessions.PoolFor(rig.Profile, rig.Provider, null);
        // Broken during the read (K9's registration window): an ordinary open failure, nothing kept, nothing orphaned.
        rig.Activity.OnIdentity = (c, _) => { ((FakeDbConnection)c).Break(); throw new P.IdentityUnavailableException("no row (test)"); };
        await Assert.ThrowsAsync<P.IdentityUnavailableException>(() => pool.BorrowAsync(null, null, CancellationToken.None));
        Assert.Equal(0, pool.Total);
        Assert.Equal(1, rig.Provider.Opened.Last().DisposeCount);
        Assert.Null(rig.Sessions.Registry.UnidentifiedReason());
        // The monitor's own connection: the monitor must recognise itself (K6), so it stops with the reason instead of
        // retrying every 30 s against a server that refuses the read.
        rig.Activity.OnIdentity = (_, _) => throw new P.IdentityUnavailableException("The server refused this connection's read of its own row (test).");
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!rig.Notifier.Of<MonitorStoppedEvent>().Any(e => e.MonitorId == id) && DateTime.UtcNow < deadline) await Task.Delay(10);
        var stopped = rig.Notifier.Of<MonitorStoppedEvent>().Single(e => e.MonitorId == id);
        Assert.Contains("refused this connection's read of its own row", stopped.Reason);
        Assert.Contains("stopped", stopped.Reason);
        Assert.All(rig.Provider.Opened, c => Assert.Equal(1, c.DisposeCount)); // nothing kept open
        Assert.Null(rig.Sessions.Registry.UnidentifiedReason());
    });

    [Fact]
    public Task O17_O19_a_driver_reopen_records_the_new_identity_and_the_registry_stays_bounded() => Sim(async () =>
    {
        await using var rig = new Rig();
        var reg = new ConnectionRegistry();
        var conn = new FakeDbConnection((_, _) => Task.FromResult(FakeResult.None), 500);
        var oldKey = new SessionKey { Id = 500, Instance = "1:500" };
        reg.Register(conn, oldKey, ConnectionRegistry.Kinds.Pool, "t1");
        var newKey = new SessionKey { Id = 501, Instance = "1:501" };
        reg.Register(conn, newKey, ConnectionRegistry.Kinds.Pool, "t2");
        Assert.Null(reg.Find(oldKey));
        Assert.Equal(ConnectionRegistry.Kinds.Pool, reg.Find(newKey)!.Kind);
        for (var i = 0; i < 1_000; i++)
        {
            var c = new FakeDbConnection((_, _) => Task.FromResult(FakeResult.None), 1_000 + i);
            reg.Register(c, new SessionKey { Id = 1_000 + i, Instance = $"1:{1_000 + i}" }, ConnectionRegistry.Kinds.Pool, "t");
            await c.DisposeAsync();
        }
        reg.Prune();
        Assert.Equal(1, reg.Count);
        GC.KeepAlive(conn);
    });

    [Fact]
    public Task O15_O16_the_owner_badge_follows_the_lease_and_the_pin() => Sim(async () =>
    {
        await using var rig = new Rig();
        var a = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        var b = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        var (ca, _) = await rig.Sessions.AcquireAsync(a, null, CancellationToken.None);
        var key = rig.Sessions.Registry.KeyOf(ca.Db)!;
        Assert.Equal(a.Id, rig.Sessions.Registry.Find(key)!.LeasedBy);
        await rig.Sessions.ReleaseAsync(a, ca, true);
        Assert.True(rig.Sessions.Registry.Find(key)!.Idle);
        Assert.Null(rig.Sessions.Registry.Find(key)!.LeasedBy);
        var (cb, _) = await rig.Sessions.AcquireAsync(b, null, CancellationToken.None);
        Assert.Same(ca, cb); // pool of one connection reused
        Assert.Equal(b.Id, rig.Sessions.Registry.Find(key)!.LeasedBy);
        await rig.Sessions.ReleaseAsync(b, cb, true);
        await rig.Sessions.TxAsync(a.Id, "begin", CancellationToken.None);
        Assert.Equal(a.Id, rig.Sessions.Registry.Find(rig.Sessions.Registry.KeyOf(a.Pinned!.Db)!)!.LeasedBy); // kept while pinned
        await rig.Sessions.TxAsync(a.Id, "rollback", CancellationToken.None);
        Assert.Null(rig.Sessions.Registry.Find(key)!.LeasedBy);
    });

    // ------------------------------------------------------------------ blocking banner

    static (BlockingBannerService Banner, Rig Rig) Banner(Rig rig, Func<string, bool>? monitorRunning = null) =>
        (new BlockingBannerService(rig.Sessions, rig.Notifier, Querithm.Tests.Log.Null, monitorRunning ?? (_ => false), timer: false), rig);

    [Fact]
    public Task O6_O7_O12_one_shared_check_per_profile_only_for_idle_tabs_with_an_open_transaction() => Sim(async () =>
    {
        await using var rig = new Rig();
        var (banner, _) = Banner(rig);
        var sessions = Enumerable.Range(0, 5).Select(_ => rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId)).ToList();
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, rig.Activity.BlockedByCalls); // O7: no open transaction, no check
        foreach (var s in sessions) await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None);
        sessions[4].ActiveExecutionId = "running";
        var opened = rig.Provider.Opened.Count;
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, rig.Activity.BlockedByCalls); // O12: one query for all tabs
        var call = rig.Activity.Calls.Last(c => c.StartsWith("blockedBy", StringComparison.Ordinal));
        Assert.Equal(4, call["blockedBy ".Length..].Split(',').Length); // O6: the executing tab is left out
        Assert.True(rig.Provider.Opened.Count <= opened + 1); // at most one extra connection
        foreach (var s in sessions) Assert.DoesNotContain(s.Pinned!.Db, rig.Provider.Opened.Where(c => c.Log.Any(l => l.StartsWith("FAKE blockedBy", StringComparison.Ordinal))));
    });

    [Fact]
    public Task O10_a_tab_whose_server_refused_its_identity_read_is_told_once_that_the_banner_cannot_check_it() => Sim(async () =>
    {
        await using var rig = new Rig();
        var (banner, _) = Banner(rig);
        rig.Activity.OnIdentity = (_, _) => throw new P.IdentityUnavailableException("refused (test)");
        var s = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None); // pins an unidentified connection
        rig.Activity.OnIdentity = null;
        await banner.RunCycleAsync(CancellationToken.None);
        await banner.RunCycleAsync(CancellationToken.None);
        var told = Assert.Single(rig.Notifier.Of<SessionBlockingEvent>(), e => e.SessionId == s.Id);
        Assert.Contains("did not return its own session identity", told.Unavailable);
        Assert.Equal(0, rig.Activity.BlockedByCalls); // nothing to ask the server about: its session id is unknown
    });

    [Fact]
    public Task O4_O5_O11_O13_O14_banner_shows_after_five_seconds_clears_and_check_failures_or_a_full_pool_are_silent() => Sim(async () =>
    {
        await using var rig = new Rig();
        var monitorRunning = false;
        var (banner, _) = Banner(rig, _ => monitorRunning);
        var s = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None);
        var spid = rig.Sessions.Registry.KeyOf(s.Pinned!.Db)!.Id;
        rig.Activity.BlockedByResult = ([new Contract.Providers.BlockedBySummary(spid, 3, 4_000)], null);
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.DoesNotContain(rig.Notifier.Of<SessionBlockingEvent>(), e => e.BlockedCount > 0); // < 5 s
        rig.Activity.BlockedByResult = ([new Contract.Providers.BlockedBySummary(spid, 3, 45_000)], null);
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(3, rig.Notifier.Of<SessionBlockingEvent>().Last().BlockedCount);
        rig.Activity.BlockedByResult = ([], null);
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, rig.Notifier.Of<SessionBlockingEvent>().Last().BlockedCount); // O5: cleared within one check
        // O11: a failing check is silent and leaves no borrowed connection behind.
        var pool = rig.Sessions.PoolFor(s);
        rig.Activity.OnIdentity = null;
        var events = rig.Notifier.Of<SessionBlockingEvent>().Count();
        var orig = rig.Activity.BlockedByResult;
        rig.Provider.Handler = (cmd, _) => cmd.CommandText.StartsWith("FAKE blockedBy", StringComparison.Ordinal) ? throw new InvalidOperationException("check failed") : Task.FromResult(FakeResult.None);
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(events, rig.Notifier.Of<SessionBlockingEvent>().Count());
        Assert.Equal(pool.Total, pool.IdleCount + 1); // only the pinned one is out
        rig.Provider.Handler = (_, _) => Task.FromResult(FakeResult.None);
        // O14: pool full → waits ≤ 1 s, skips, removes its waiter.
        var held = new List<PhysicalConnection>();
        while (pool.Total < rig.Sessions.MaxPerProfile) held.Add(await pool.BorrowAsync(null, null, CancellationToken.None));
        foreach (var c in pool.Total == rig.Sessions.MaxPerProfile && pool.IdleCount > 0 ? new[] { await pool.BorrowAsync(null, null, CancellationToken.None) } : []) held.Add(c);
        var calls = rig.Activity.BlockedByCalls;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.InRange(sw.ElapsedMilliseconds, 900, 2_500);
        Assert.Equal(calls, rig.Activity.BlockedByCalls);
        Assert.Equal(0, pool.WaiterCount);
        foreach (var c in held) pool.Return(c);
        // O13: a running monitor answers; no check of its own.
        monitorRunning = true;
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(calls, rig.Activity.BlockedByCalls);
        _ = orig;
    });

    [Fact]
    public Task O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool() => Sim(async () =>
    {
        // Regression: the 1 s bound covered opening the check connection too, so an open slower than 1 s was cancelled
        // and treated as "pool full" on every cycle; the banner never checked (seen on PostgreSQL in O4).
        await using var rig = new Rig();
        var (banner, _) = Banner(rig);
        var s = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        await rig.Sessions.TxAsync(s.Id, "begin", CancellationToken.None);
        var pool = rig.Sessions.PoolFor(s);
        Assert.Equal(0, pool.IdleCount); // the check must open a connection
        rig.Provider.OnOpen = ct => Task.Delay((int)BlockingBannerService.BorrowWaitMs + 300, ct);
        await banner.RunCycleAsync(CancellationToken.None);
        Assert.Equal(1, banner.Checks);
        Assert.Equal(1, rig.Activity.BlockedByCalls);
        Assert.Equal(pool.Total, pool.IdleCount + 1);
    });

    // ------------------------------------------------------------------ matrix rows covered here (S1, K23, L3, L7, X13, X16, O2, O3, O18)

    static DeadlockEvent Dl(string id, long at) => new() { Id = id, At = at, Victims = [], Processes = [], Resources = [] };

    [Fact]
    public Task S1_no_other_sessions_is_an_empty_list_without_error() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start(1_000, [MonitorPane.Sessions]).MonitorId;
        rig.Clock.Run(3_000);
        var s = rig.Snapshots(id).Last(x => x.Sessions != null);
        Assert.Equal([rig.Activity.Session!.Self], s.Sessions!.Select(r => r.Key)); // only the monitor itself
        Assert.Equal(PaneState.Ok, s.Status.Single(x => x.Pane == MonitorPane.Sessions).State);
    });

    [Fact]
    public Task K23_the_database_filter_is_not_a_security_boundary_only_snapshot_membership_is() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Foreign(55) with { Database = "other" }); return s; };
        var id = rig.Monitors.Start(new MonitorStartParams { ProfileId = rig.Profile.Id, IntervalMs = 1_000, DatabaseFilter = "sales" }).MonitorId;
        rig.Clock.Advance(10);
        MonitorKillParams Kill(long spid) => new() { MonitorId = id, Target = Foreign(spid).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true };
        Assert.Equal(KillOutcome.Done, (await rig.Monitors.KillAsync(Kill(55))).Outcome); // in the snapshot, other database: allowed
        Assert.Equal("notInSnapshot", (await rig.Monitors.KillAsync(Kill(56))).Code);       // not shown: refused, whatever its database
        Assert.Equal(1, rig.Activity.Session!.Terminates);
    });

    [Fact]
    public Task K5_a_session_that_ended_after_the_snapshot_is_gone_not_a_rollback_confirm_and_nothing_is_sent() => Sim(async () =>
    {
        // Regression: the server-side preview of an ended session read as "risk unknown", so the kill asked for the
        // long-rollback confirm instead of saying the session had ended.
        await using var rig = new Rig();
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Foreign(55)); return s; };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        rig.Activity.Session!.Preview = new KillPreview { Key = Foreign(55).Key, SessionEnded = true, Reason = "The session ended." };
        var r = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(55).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal((KillOutcome.Gone, "gone"), (r.Outcome, r.Code));
        Assert.Equal(0, rig.Activity.Session.Terminates);
        Assert.Equal(RollbackRisk.None, KillRisk.Classify(rig.Activity.Session.Preview));
    });

    [Fact]
    public Task The_monitor_connection_is_prepared_by_the_provider_before_its_first_query() => Sim(async () =>
    {
        // The control connection is checked in C2_C3 (it only opens while a job holds the monitor connection).
        await using var rig = new Rig();
        rig.Start();
        rig.Clock.Advance(10);
        var monitorConn = rig.Provider.Opened.Single(c => rig.Sessions.Registry.KeyOf(c) == rig.Activity.Session!.Self);
        Assert.True(rig.Activity.WasPrepared(monitorConn));
    });

    [Fact]
    public Task K7_a_kill_while_one_of_querithms_connections_reconnected_and_is_not_identified_again_is_refused() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(Foreign(55)); return s; };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var tab = new FakeDbConnection((_, _) => Task.FromResult(FakeResult.None), 300);
        await tab.OpenAsync();
        rig.Sessions.Registry.Register(tab, new SessionKey { Id = 300, Instance = FakeActivity.Inst(300) }, ConnectionRegistry.Kinds.Pool, "300", rig.Activity.ConnectionToken);
        tab.Reconnect(); // mid-run: its new server session is not in the registry yet
        var r = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = Foreign(55).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal((KillOutcome.Refused, "querithmReconnecting"), (r.Outcome, r.Code));
        Assert.Equal(0, rig.Activity.Session!.Terminates);
        GC.KeepAlive(tab);
    });

    [Fact]
    public Task L3_disconnecting_the_profile_stops_its_monitors_and_closes_their_connections() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var session = rig.Activity.Session!;
        var conn = rig.Provider.Opened.Single(c => rig.Sessions.Registry.KeyOf(c) == session.Self);
        rig.Sessions.ForgetProfile(rig.Profile.Id); // what disconnecting the server does
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rig.Monitors.All.Any() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Empty(rig.Monitors.All);
        Assert.Equal(1, conn.DisposeCount);
        Assert.Contains(rig.Notifier.Of<MonitorStoppedEvent>(), e => e.MonitorId == id);
        var ticks = session.Ticks;
        rig.Clock.Advance(10_000);
        Assert.Equal(ticks, session.Ticks); // the loop is gone
    });

    [Fact]
    public Task L7_a_dmv_query_that_never_returns_is_cut_at_its_time_limit_its_pane_says_so_and_the_other_panes_continue() => Sim(async () =>
    {
        // Regression: a cancelled secondary job left its pane as it was, so the last lock list kept looking current.
        await using var rig = new Rig();
        var id = rig.Start(1_000, [MonitorPane.Sessions, MonitorPane.Locks]).MonitorId;
        rig.Clock.Advance(10);
        rig.Activity.Session!.OnLocks = ct => rig.Clock.WaitUntilAsync(long.MaxValue, ct); // never returns (fake time)
        var before = rig.Snapshots(id).Count(s => s.Sessions != null);
        rig.Clock.Run(30_000);
        var locks = rig.Snapshots(id).Last().Status.Single(x => x.Pane == MonitorPane.Locks);
        Assert.Equal(PaneState.Error, locks.State);
        Assert.Contains("time limit", locks.Reason);
        Assert.True(rig.Snapshots(id).Count(s => s.Sessions != null) - before >= 5); // sessions kept coming between the cut-off jobs
    });

    [Fact]
    public Task X13_more_than_200_deadlocks_return_the_newest_200_and_say_truncated() => Sim(async () =>
    {
        await using var rig = new Rig();
        var now = Time.NowMs();
        rig.Activity.NewSession = () =>
        {
            var s = new FakeActivitySession();
            for (var i = 0; i < 250; i++) s.DeadlockEvents.Add(Dl($"d{i}", now - 1_000L * (250 - i)));
            return s;
        };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var r = await rig.Monitors.DeadlocksAsync(new DeadlocksParams { MonitorId = id });
        Assert.Equal(200, r.Events.Length);
        Assert.True(r.Truncated);
        Assert.Equal(PaneState.Truncated, r.Status.State);
        Assert.Equal(250, r.Status.Total);
        Assert.Equal("d249", r.Events[0].Id); // newest first
        Assert.DoesNotContain(r.Events, e => e.Id == "d49");
    });

    [Fact]
    public Task X16_a_new_deadlock_is_sent_once_and_not_again_on_the_next_poll() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start(1_000, [MonitorPane.Sessions, MonitorPane.Deadlocks]).MonitorId;
        rig.Clock.Run(31_000); // first poll: nothing yet
        rig.Activity.Session!.DeadlockEvents.Add(Dl("new1", Time.NowMs() - 500));
        rig.Clock.Run(31_000); // the next poll finds it
        rig.Clock.Run(31_000); // and the one after that sees it again
        Assert.Equal(1, rig.Snapshots(id).Where(s => s.NewDeadlocks != null).SelectMany(s => s.NewDeadlocks!).Count(e => e.Id == "new1"));
        // The periodic poll reads only what is new since its last position (a full read costs with the files' size); the
        // pane reads everything, on demand.
        Assert.All(rig.Activity.Session!.DeadlockReads, recent => Assert.True(recent));
        await rig.Monitors.DeadlocksAsync(new DeadlocksParams { MonitorId = id });
        Assert.False(rig.Activity.Session.DeadlockReads[^1]);
    });

    [Fact]
    public Task O1_a_tab_connection_appears_as_a_querithm_pool_connection_of_its_tab_session() => Sim(async () =>
    {
        await using var rig = new Rig();
        var s = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        var (c, b) = await rig.Sessions.AcquireAsync(s, null, CancellationToken.None);
        var key = rig.Sessions.Registry.KeyOf(c.Db)!;
        rig.Activity.NewSession = () => { var a = new FakeActivitySession(); a.Rows.Add(new ActivityRow { Key = key, Status = "sleeping", BlockedBy = [], IsUserSession = true }); return a; };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var row = rig.Snapshots(id).Last(x => x.Sessions != null).Sessions!.Single(r => r.Key == key);
        Assert.Equal(ConnectionRegistry.Kinds.Pool, row.QuerithmKind); // the badge and "Go to tab" read these two
        Assert.Equal(s.Id, row.OwnSessionId);
        await rig.Sessions.ReleaseAsync(s, c, b);
    });

    [Fact]
    public Task O2_O3_a_reconnected_tab_is_known_by_its_new_identity_and_a_reused_spid_is_not_querithm() => Sim(async () =>
    {
        await using var rig = new Rig();
        var s = rig.Sessions.Get(rig.Sessions.Open(new SessionOpenParams { ProfileId = rig.Profile.Id, Secret = "x" }).SessionId);
        var (c1, b1) = await rig.Sessions.AcquireAsync(s, null, CancellationToken.None);
        var oldKey = rig.Sessions.Registry.KeyOf(c1.Db)!;
        ((FakeDbConnection)c1.Db).Break();
        await rig.Sessions.ReleaseAsync(s, c1, b1); // broken: discarded
        var (c2, b2) = await rig.Sessions.AcquireAsync(s, null, CancellationToken.None);
        var newKey = rig.Sessions.Registry.KeyOf(c2.Db)!;
        Assert.NotEqual(oldKey, newKey);
        Assert.Null(rig.Sessions.Registry.Find(oldKey));                          // O2: the old key maps to nothing
        Assert.Equal(s.Id, rig.Sessions.Registry.Find(newKey)!.LeasedBy);
        Assert.Null(rig.Sessions.Registry.Find(newKey with { Instance = "2:9" })); // O3: same spid, other start time: not ours
        await rig.Sessions.ReleaseAsync(s, c2, b2);
    });

    [Fact]
    public Task O18_dedicated_connections_are_labelled_by_kind_and_never_killable() => Sim(async () =>
    {
        await using var rig = new Rig();
        var dedicated = await rig.Sessions.OpenDedicatedAsync(rig.Profile, rig.Provider, null, CancellationToken.None);
        var key = rig.Sessions.Registry.KeyOf(dedicated)!;
        rig.Activity.NewSession = () => { var s = new FakeActivitySession(); s.Rows.Add(new ActivityRow { Key = key, Status = "sleeping", BlockedBy = [], IsUserSession = true }); return s; };
        var id = rig.Start().MonitorId;
        rig.Clock.Advance(10);
        var snap = rig.Snapshots(id).Last(x => x.Sessions != null);
        Assert.Equal(ConnectionRegistry.Kinds.Dedicated, snap.Sessions!.Single(r => r.Key == key).QuerithmKind);
        Assert.Equal(ConnectionRegistry.Kinds.Monitor, snap.Sessions!.Single(r => r.Key == rig.Activity.Session!.Self).QuerithmKind);
        var r = await rig.Monitors.KillAsync(new MonitorKillParams { MonitorId = id, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal("querithmConnection", r.Code);
        Assert.Equal(0, rig.Activity.Session!.Terminates);
        await dedicated.DisposeAsync();
    });

    // ------------------------------------------------------------------ export

    [Fact]
    public Task H5_H6_H7_exports_hold_no_text_login_or_host_unless_asked_stay_in_the_exports_folder_and_leave_nothing_on_failure() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Profiles.Save(rig.Profile with { Name = "..\\..\\evil/../name" });
        const string text = "SELECT h5_text_marker FROM payroll_h5 WHERE id = 1";
        rig.Activity.NewSession = () =>
        {
            var s = new FakeActivitySession();
            s.Rows.Add(Foreign(42) with { Login = "carol", Host = "laptop-7", Program = "excel", TextId = "t42" });
            return s;
        };
        rig.Provider.Activity = ReshapingActivity.WithTexts(rig.Activity, new Dictionary<string, string> { ["t42"] = text });
        var id = rig.Start().MonitorId;
        rig.Clock.Run(5_000);
        // The history really holds the text and its id (otherwise "no text in the file" proves nothing).
        var textId = rig.Snapshots(id).Last(s => s.Sessions != null).Sessions!.Single(r => r.Key.Id == 42).TextId;
        Assert.NotNull(textId);
        var file = rig.Monitors.Export(new MonitorExportParams { MonitorId = id, IncludeText = false });
        var exports = Path.GetFullPath(Path.Combine(rig.DataDir, "exports"));
        Assert.StartsWith(exports + Path.DirectorySeparatorChar, file.Path);
        Assert.DoesNotContain("..", Path.GetFileName(file.Path));
        var json = File.ReadAllText(file.Path);
        foreach (var secret in new[] { "carol", "laptop-7", "excel", "h5_text_marker", "payroll_h5", textId! }) Assert.DoesNotContain(secret, json);
        Assert.Contains("\"ticks\"", json);
        Assert.Contains("\"sessions\"", json); // the rows themselves are exported, only without those fields
        var withText = File.ReadAllText(rig.Monitors.Export(new MonitorExportParams { MonitorId = id, IncludeText = true }).Path);
        Assert.Contains("carol", withText);
        Assert.Contains("laptop-7", withText);
        Assert.Contains("h5_text_marker", withText); // asked for: the text travels with the file
        // H7: disk full mid-write → the partial file is deleted, the error is reported.
        var failing = new MonitorExporter(Path.Combine(rig.DataDir, "exports2"), p => new FailingStream(p));
        var ex = Assert.Throws<QuerithmException>(() => failing.WriteText("x", ".json", new string('x', 100_000)));
        Assert.Equal("exportFailed", ex.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.DataDir, "exports2")));
    });

    sealed class FailingStream(string path) : FileStream(path, FileMode.CreateNew)
    {
        long _n;
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if ((_n += buffer.Length) > 10_000) throw new IOException("There is not enough space on the disk.");
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    }

    [Fact]
    public Task M12_monitor_texts_rejects_too_many_or_malformed_ids() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start().MonitorId;
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Monitors.Texts(new MonitorTextsParams { MonitorId = id, Ids = Enumerable.Repeat(new string('a', 32), 501).ToArray() })).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Monitors.Texts(new MonitorTextsParams { MonitorId = id, Ids = ["ABCDEF" + new string('0', 26)] })).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => rig.Monitors.Texts(new MonitorTextsParams { MonitorId = id, Ids = ["../etc/passwd"] })).Code);
        Assert.Empty(rig.Monitors.Texts(new MonitorTextsParams { MonitorId = id, Ids = [new string('a', 32)] }).Texts);
    });

    // ------------------------------------------------------------------ history gaps, tick-sampled waits (H4, D9, D11)

    [Fact]
    public Task H4_a_reconnect_marks_a_gap_before_the_first_tick_after_it_and_nowhere_else() => Sim(async () =>
    {
        await using var rig = new Rig();
        var id = rig.Start().MonitorId;
        rig.Clock.Run(3_000);
        var before = rig.M(id).GetHistory(null).Ticks;
        Assert.True(before.Length >= 3, $"{before.Length} ticks before the drop");
        Assert.DoesNotContain(before, t => t.GapBefore);
        var conn = rig.Provider.Opened.Single(c => rig.Sessions.Registry.KeyOf(c) == rig.Activity.Session!.Self);
        conn.Break();
        rig.Clock.Advance(1_000); // the tick fails: reconnecting (L4)
        Assert.Equal(MonitorConnectionState.Reconnecting, rig.Snapshots(id).Last().Connection);
        rig.Clock.Run(5_000);     // reconnected after the 1 s back-off, ticking again
        Assert.Equal(MonitorConnectionState.Connected, rig.Snapshots(id).Last().Connection);
        var ticks = rig.M(id).GetHistory(null).Ticks;
        var lastBefore = before[^1].Seq;
        var newer = ticks.Where(t => t.Seq > lastBefore).ToList();
        Assert.True(newer.Count >= 2, $"{newer.Count} ticks after the reconnect");
        Assert.True(newer[0].GapBefore, "the first tick after the reconnect is not marked as following a gap");
        Assert.All(newer.Skip(1), t => Assert.False(t.GapBefore));
        Assert.DoesNotContain(ticks.Where(t => t.Seq <= lastBefore), t => t.GapBefore);
    });

    static FakeActivitySession WaitingSession(bool sampled = true)
    {
        var s = new FakeActivitySession { WaitsFromSessions = sampled };
        s.Rows.Add(Foreign(55, "active") with { WaitType = "LCK_M_X" });
        return s;
    }

    [Fact]
    public Task D9_waits_sampled_from_sessions_read_the_sessions_every_tick_even_with_the_sessions_pane_hidden() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Activity.NewSession = () => WaitingSession();
        var id = rig.Start(1_000, [MonitorPane.Waits]).MonitorId;
        var requests = new List<P.TickRequest>();
        rig.Activity.Session!.OnTick = (req, _) => { requests.Add(req); return Task.CompletedTask; };
        rig.Clock.Run(5_000);
        Assert.True(requests.Count >= 4, $"{requests.Count} ticks");
        Assert.All(requests, r => Assert.True(r.Sessions && r.Waits, $"tick asked sessions={r.Sessions} waits={r.Waits}"));
        var s = rig.Snapshots(id).Last(x => x.Waits != null);
        Assert.Equal(("tick-sampled", 1_000, false), (s.WaitCoverage!.Source, s.WaitCoverage.PeriodMs, s.WaitCoverage.Partial));
        var lockWait = Assert.Single(s.Waits!, w => w.WaitType == "LCK_M_X");
        Assert.Equal("Lock", lockWait.Category);
        Assert.Equal(1_000, lockWait.WaitMsDelta); // one active waiter × the 1 s since the previous tick
        Assert.Equal(PaneState.Ok, s.Status.Single(x => x.Pane == MonitorPane.Waits).State);
        // A server-profile source asks for no sessions while that pane is hidden.
        await using var other = new Rig();
        other.Activity.NewSession = () => WaitingSession(sampled: false);
        other.Start(1_000, [MonitorPane.Waits]);
        var asked = new List<P.TickRequest>();
        other.Activity.Session!.OnTick = (req, _) => { asked.Add(req); return Task.CompletedTask; };
        other.Clock.Run(3_000);
        Assert.NotEmpty(asked);
        Assert.All(asked, r => Assert.False(r.Sessions));
    });

    [Fact]
    public Task D11_tick_sampled_waits_from_a_truncated_sessions_read_are_marked_partial() => Sim(async () =>
    {
        await using var rig = new Rig();
        rig.Activity.NewSession = () => WaitingSession();
        var truncate = true;
        rig.Provider.Activity = new ReshapingActivity(rig.Activity, (_, r) =>
            truncate && r.Sessions?.Value is { } rows ? r with { Sessions = P.PaneResult<ActivityRow[]>.Truncated(rows, rows.Length + 1_000) } : r);
        var id = rig.Start(1_000, [MonitorPane.Sessions, MonitorPane.Waits]).MonitorId;
        rig.Clock.Run(3_000);
        var s = rig.Snapshots(id).Last(x => x.WaitCoverage != null);
        Assert.Equal(PaneState.Truncated, s.Status.Single(x => x.Pane == MonitorPane.Sessions).State);
        Assert.Equal("tick-sampled", s.WaitCoverage!.Source);
        Assert.True(s.WaitCoverage.Partial, "waits sampled from a truncated session list are not marked partial");
        truncate = false;
        rig.Clock.Run(2_000);
        Assert.False(rig.Snapshots(id).Last(x => x.WaitCoverage != null).WaitCoverage!.Partial);
    });
}

/// <summary>
/// Passes everything to a <see cref="FakeActivity"/> but lets a test reshape each tick's result — what the shared fake
/// does not produce on its own: a truncated sessions read, or the server texts for rows that carry a text key.
/// </summary>
internal sealed class ReshapingActivity(FakeActivity inner, Func<P.TickRequest, P.TickResult, P.TickResult> reshape) : P.IActivityProvider
{
    /// <summary>Every tick that reads sessions also returns these texts, keyed as the rows' provisional TextId.</summary>
    public static ReshapingActivity WithTexts(FakeActivity inner, IReadOnlyDictionary<string, string> texts) =>
        new(inner, (_, r) => r.Sessions == null ? r : r with { SessionTexts = texts.ToDictionary(kv => kv.Key, kv => (string?)kv.Value) });

    public Task<SessionKey> IdentityAsync(DbConnection connection, CancellationToken ct) => inner.IdentityAsync(connection, ct);
    public string ConnectionToken(DbConnection connection) => inner.ConnectionToken(connection);
    public string MonitorSessionSetupSql => inner.MonitorSessionSetupSql;
    public void PrepareMonitorConnection(DbConnection connection) => inner.PrepareMonitorConnection(connection);
    public bool CredentialsRejected(Exception ex) => inner.CredentialsRejected(ex);
    public string WaitCategory(string waitType) => inner.WaitCategory(waitType);
    public bool IsBenignWait(string waitType) => inner.IsBenignWait(waitType);
    public bool IsValidInstance(string instance) => inner.IsValidInstance(instance);
    public bool IsValidStatementStart(string statementStart) => inner.IsValidStatementStart(statementStart);
    public async Task<P.IActivitySession> ProbeAsync(DbConnection connection, CancellationToken ct) => new Session(await inner.ProbeAsync(connection, ct), reshape);
    public Task<(P.BlockedBySummary[]? Rows, string? Unavailable)> BlockedByAsync(DbConnection connection, long[] ids, CancellationToken ct) => inner.BlockedByAsync(connection, ids, ct);

    sealed class Session(P.IActivitySession s, Func<P.TickRequest, P.TickResult, P.TickResult> reshape) : P.IActivitySession
    {
        public MonitorCapabilities Capabilities => s.Capabilities;
        public string? StartRefusal => s.StartRefusal;
        public SessionKey Self => s.Self;
        public bool WaitsFromSessions => s.WaitsFromSessions;
        public async Task<P.TickResult> TickAsync(DbConnection connection, P.TickRequest request, CancellationToken ct) => reshape(request, await s.TickAsync(connection, request, ct));
        public Task<P.PaneResult<LockRow[]>> LocksAsync(DbConnection connection, int max, CancellationToken ct) => s.LocksAsync(connection, max, ct);
        public Task<IReadOnlyDictionary<(string Database, long ObjectId), string?>> ResolveObjectNamesAsync(DbConnection connection, IReadOnlyList<(string Database, long ObjectId)> ids, CancellationToken ct) =>
            s.ResolveObjectNamesAsync(connection, ids, ct);
        public Task<P.PaneResult<P.TopQueryRead>> TopQueriesAsync(DbConnection connection, CancellationToken ct) => s.TopQueriesAsync(connection, ct);
        public Task<IReadOnlyDictionary<string, P.TopQueryText>> TopQueryTextsAsync(DbConnection connection, IReadOnlyList<string> textKeys, CancellationToken ct) => s.TopQueryTextsAsync(connection, textKeys, ct);
        public Task<P.PaneResult<DeadlockEvent[]>> DeadlocksAsync(DbConnection connection, long fromMs, long toMs, int max, CancellationToken ct, bool recentOnly = false) =>
            s.DeadlocksAsync(connection, fromMs, toMs, max, ct, recentOnly);
        public Task<string?> DeadlockXmlAsync(DbConnection connection, string eventId, long fromMs, long toMs, CancellationToken ct) => s.DeadlockXmlAsync(connection, eventId, fromMs, toMs, ct);
        public Task<KillResult> CancelRequestAsync(DbConnection connection, SessionKey key, string? statementStart, CancellationToken ct) => s.CancelRequestAsync(connection, key, statementStart, ct);
        public Task<KillResult> TerminateSessionAsync(DbConnection connection, SessionKey key, CancellationToken ct) => s.TerminateSessionAsync(connection, key, ct);
        public Task<P.RollbackStatus?> RollbackStatusAsync(DbConnection connection, SessionKey key, CancellationToken ct) => s.RollbackStatusAsync(connection, key, ct);
        public Task<KillPreview> KillPreviewAsync(DbConnection connection, SessionKey key, CancellationToken ct) => s.KillPreviewAsync(connection, key, ct);
        public Task<RequestDetail> RequestDetailAsync(DbConnection connection, SessionKey key, CancellationToken ct) => s.RequestDetailAsync(connection, key, ct);
    }
}

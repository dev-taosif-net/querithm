using MySqlConnector;
using Querithm.Contract;
using static Querithm.Tests.Integration.MySqlIntegrationTests;
using MyHost = Querithm.Tests.Integration.MySqlIntegrationTests.Host;

namespace Querithm.Tests.Integration;

/// <summary>
/// Phase D2 activity monitor on MySQL / MariaDB (QUERITHM_IT=1): sessions with InnoDB blocking chains, locks, metrics and
/// digests on MySQL; identity-checked cancel and terminate; MariaDB (performance_schema off) shows sessions and blocking from
/// PROCESSLIST / INNODB_LOCK_WAITS and refuses kills with the reason.
/// </summary>
[Collection("monitor-mysql")]
public sealed class MySqlMonitorTests
{
    public static TheoryData<string> MySqlProducts => new() { "mysql", "mysql97" };

    sealed class Rig(string product) : IAsyncDisposable
    {
        public MyHost H { get; } = new();
        public Profile P { get; private set; } = null!;
        public string Sid { get; private set; } = "";
        readonly List<MySqlConnection> _foreign = [];

        public async Task InitAsync()
        {
            await EnsureDatabaseAsync(product);
            P = await H.Rpc.ProfileSave(ProfileFor(product));
            Sid = (await H.Rpc.SessionOpen(new SessionOpenParams { ProfileId = P.Id, Secret = TestHost.MySqlPassword })).SessionId;
            await using var c = await ForeignAsync("setup");
            await ExecAsync(c, "CREATE TABLE IF NOT EXISTS mon_it (id INT PRIMARY KEY, v INT) ENGINE=InnoDB; INSERT IGNORE INTO mon_it VALUES (1, 1), (2, 2), (3, 3);");
            _foreign.Remove(c);
        }

        public async Task<MySqlConnection> ForeignAsync(string app)
        {
            var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False;Database={Db(product)};Application Name={app}");
            await c.OpenAsync();
            _foreign.Add(c);
            return c;
        }

        public static async Task ExecAsync(MySqlConnection c, string sql, int timeoutS = 30)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutS;
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<MonitorSnapshot> WaitSnapshotAsync(string monitorId, Func<MonitorSnapshot, bool> until, int timeoutMs = 20_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var hit = H.Notifier.Of<MonitorSnapshot>().LastOrDefault(s => s.MonitorId == monitorId && until(s));
                if (hit != null) return hit;
                await Task.Delay(100);
            }
            var last = H.Notifier.Of<MonitorSnapshot>().LastOrDefault(s => s.MonitorId == monitorId);
            throw new TimeoutException($"No matching snapshot; last: {string.Join(", ", last?.Status.Select(s => $"{s.Pane}={s.State}:{s.Reason}") ?? [])}; " +
                $"error {last?.ConnectionError}; sessions {string.Join(" | ", last?.Sessions?.Select(r => $"{r.Key.Id}/{r.Key.Instance} {r.Status} {r.Command} blockedBy={string.Join(',', r.BlockedBy)}") ?? [])}");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in _foreign) try { await c.DisposeAsync(); } catch { /* killed */ }
            await H.DisposeAsync();
        }
    }

    static long Id(MySqlConnection c) => c.ServerThread;

    [IntegrationTheory]
    [MemberData(nameof(MySqlProducts))]
    public async Task Blocking_chains_locks_metrics_and_a_guarded_terminate(string product)
    {
        await using var rig = new Rig(product);
        await rig.InitAsync();
        var holder = await rig.ForeignAsync("mon-holder");
        var victim = await rig.ForeignAsync("mon-victim");
        await Rig.ExecAsync(holder, "START TRANSACTION; UPDATE mon_it SET v = v + 1 WHERE id = 1;");
        var victimDone = Task.Run(() => Rig.ExecAsync(victim, "SET SESSION innodb_lock_wait_timeout = 40; UPDATE mon_it SET v = 0 WHERE id = 1;", 60));

        var m = await rig.H.Rpc.MonitorStart(new MonitorStartParams { ProfileId = rig.P.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions, MonitorPane.Locks, MonitorPane.Metrics, MonitorPane.TopQueries, MonitorPane.Waits] });
        Assert.Equal(Engine.MySql, m.Engine);
        var snap = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Sessions?.Any(r => r.Key.Id == Id(victim) && r.BlockedBy.Contains(Id(holder))) == true);
        var v = snap.Sessions!.Single(r => r.Key.Id == Id(victim));
        Assert.Equal("wait/lock/innodb/row", v.WaitType);
        var h = snap.Sessions!.Single(r => r.Key.Id == Id(holder));
        Assert.Equal("idle in transaction", h.Status);
        Assert.False(string.IsNullOrEmpty(h.Key.Instance));

        var locks = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Locks?.Any(l => l.Key.Id == Id(victim) && l.Status == "waiting") == true);
        Assert.Contains(locks.Locks!, l => l.Key.Id == Id(holder) && l.Status == "granted" && l.Object?.StartsWith("mon_it") == true);
        var metrics = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Metrics?.Values.Any(x => x.Key == "activeConnections" && x.Value > 0) == true);
        Assert.Contains(metrics.Metrics!.Values, x => x.Key == "cpu" && x.Value == null && x.Note != null);

        // What a terminate would undo, then the terminate itself (identity re-checked on the server); the victim proceeds.
        var preview = await rig.H.Rpc.MonitorKillPreview(new MonitorSessionParams { MonitorId = m.MonitorId, Key = h.Key });
        Assert.Equal((RollbackRisk.Low, true), (preview.Risk, preview.HasWrites));
        var wrong = h.Key with { Instance = (long.Parse(h.Key.Instance) + 100_000).ToString() };
        var refused = await rig.H.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = wrong, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal(KillOutcome.Refused, refused.Outcome);
        var done = await rig.H.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = h.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
        Assert.Equal(KillOutcome.Done, done.Outcome);
        await victimDone.WaitAsync(TimeSpan.FromSeconds(15));
        await rig.H.Rpc.MonitorStop(new MonitorRef { MonitorId = m.MonitorId });
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlProducts))]
    public async Task A_running_statement_is_cancelled_and_its_session_stays(string product)
    {
        await using var rig = new Rig(product);
        await rig.InitAsync();
        var busy = await rig.ForeignAsync("mon-sleep");
        var running = Task.Run(async () =>
        {
            await using var cmd = busy.CreateCommand();
            cmd.CommandText = "SELECT SLEEP(30)";
            cmd.CommandTimeout = 60;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        });
        var m = await rig.H.Rpc.MonitorStart(new MonitorStartParams { ProfileId = rig.P.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions] });
        var snap = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Sessions?.Any(r => r.Key.Id == Id(busy) && r.StatementStart != null) == true);
        var row = snap.Sessions!.Single(r => r.Key.Id == Id(busy));
        Assert.True(row.ElapsedMs >= 0);
        // A statement that is no longer the running one is not cancelled.
        var stale = await rig.H.Rpc.MonitorKill(new MonitorKillParams
        {
            MonitorId = m.MonitorId, Target = row.Key, Mode = KillMode.CancelRequest, ConfirmedKill = true, ExpectedStatementStart = (ulong.Parse(row.StatementStart!) + 1000).ToString(),
        });
        Assert.NotEqual(KillOutcome.Done, stale.Outcome);
        Assert.False(running.IsCompleted);
        var r = await rig.H.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = row.Key, Mode = KillMode.CancelRequest, ConfirmedKill = true, ExpectedStatementStart = row.StatementStart });
        Assert.Equal(KillOutcome.Done, r.Outcome);
        Assert.Equal(1L, await running.WaitAsync(TimeSpan.FromSeconds(10))); // SLEEP returns 1 when interrupted
        await Rig.ExecAsync(busy, "SELECT 1"); // the session stays connected
        await rig.H.Rpc.MonitorStop(new MonitorRef { MonitorId = m.MonitorId });
    }

    [IntegrationFact]
    public async Task Mariadb_without_performance_schema_shows_blocking_and_refuses_kills_with_the_reason()
    {
        await using var rig = new Rig("mariadb");
        await rig.InitAsync();
        var holder = await rig.ForeignAsync("mon-holder");
        var victim = await rig.ForeignAsync("mon-victim");
        await Rig.ExecAsync(holder, "START TRANSACTION; UPDATE mon_it SET v = v + 1 WHERE id = 2;");
        var victimDone = Task.Run(() => Rig.ExecAsync(victim, "SET SESSION innodb_lock_wait_timeout = 40; UPDATE mon_it SET v = 0 WHERE id = 2;", 60));
        var m = await rig.H.Rpc.MonitorStart(new MonitorStartParams { ProfileId = rig.P.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions, MonitorPane.Locks, MonitorPane.Metrics] });
        var snap = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Sessions?.Any(r => r.Key.Id == Id(victim) && r.BlockedBy.Contains(Id(holder))) == true);
        Assert.Contains("performance_schema", snap.Capabilities?.TerminateReason ?? rig.H.Notifier.Of<MonitorSnapshot>().Last(s => s.Capabilities != null).Capabilities!.TerminateReason);
        Assert.Contains(snap.Status, s => s.Pane == MonitorPane.Locks && s.State == PaneState.Degraded);
        var h = snap.Sessions!.Single(r => r.Key.Id == Id(holder));
        var kill = await rig.H.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = h.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
        Assert.Equal(KillOutcome.Refused, kill.Outcome);
        Assert.Equal(1L, Convert.ToInt64(await new MySqlCommand("SELECT 1", holder).ExecuteScalarAsync())); // untouched
        await Rig.ExecAsync(holder, "ROLLBACK");
        await victimDone.WaitAsync(TimeSpan.FromSeconds(15));
        await rig.H.Rpc.MonitorStop(new MonitorRef { MonitorId = m.MonitorId });
    }
}

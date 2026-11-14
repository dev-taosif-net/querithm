using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S12 activity monitor on the Oracle Free fixture (QUERITHM_IT=1), at the three privilege levels the fixture has
/// without any extra grant: SYSTEM (every V$ view and ALTER SYSTEM) sees a TX row-lock chain, its locks, waits, metrics and
/// top queries, previews and terminates the holder (identity re-checked on the server) and cancels a running statement by
/// its SQL_ID and execution id; <c>querithm_mon</c> (V$ views, no ALTER SYSTEM) sees the same and is refused every kill with
/// the reason; <c>querithm_app</c> (no V$ view) gets a monitor that stops naming the grant it needs.
/// </summary>
[Collection("monitor-oracle")]
public sealed class OracleMonitorTests
{
    const string Table = "Q_S12_MON";

    sealed class Rig : IAsyncDisposable
    {
        public TestHost H { get; } = new(TestOracle.Supported(Directory.CreateTempSubdirectory("qora-mon-").FullName));
        public Profile P { get; private set; } = null!;
        readonly List<OracleConnection> _foreign = [];
        readonly CancellationTokenSource _stop = new();

        /// <summary>
        /// Cancelled first on dispose: a foreign statement still running (a test failed before its cancel or kill) gets a
        /// break and ends, instead of its connection's Dispose waiting on it for ever (ODP.NET holds the connection lock
        /// while a command runs; one failure hung the whole integration run, CI 43577279333).
        /// </summary>
        public CancellationToken Stop => _stop.Token;

        /// <summary>The monitor's profile signs in as <paramref name="user"/>; the sessions it watches are the app user's.</summary>
        public async Task InitAsync(string user)
        {
            P = await H.Rpc.ProfileSave(new Profile
            {
                Id = "", Name = "ora-monitor-" + user, Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password,
                Username = user, DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
            });
            await H.Rpc.SessionOpen(new SessionOpenParams { ProfileId = P.Id, Secret = SecretOf(user) });
            await using var c = await ForeignAsync("setup");
            await ExecAsync(c, $"BEGIN EXECUTE IMMEDIATE 'CREATE TABLE {Table} (id NUMBER(10) PRIMARY KEY, v NUMBER(10))'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -955 THEN RAISE; END IF; END;");
            await ExecAsync(c, $"MERGE INTO {Table} t USING (SELECT level id FROM dual CONNECT BY level <= 3) s ON (t.id = s.id) WHEN NOT MATCHED THEN INSERT VALUES (s.id, s.id)");
            await ExecAsync(c, "COMMIT");
            _foreign.Remove(c);
        }

        static string SecretOf(string user) => user switch
        {
            "system" => FixtureSecrets.Password("oracle-admin"),
            OracleIntegrationTests.MonitorUser => FixtureSecrets.Password("oracle-mon"),
            _ => FixtureSecrets.Password("oracle"),
        };

        /// <summary>An app-user connection outside Querithm (not registered, so the monitor may kill it).</summary>
        public async Task<OracleConnection> ForeignAsync(string module)
        {
            var c = new OracleConnection(OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser));
            await c.OpenAsync();
            _foreign.Add(c);
            await ExecAsync(c, $"BEGIN DBMS_APPLICATION_INFO.SET_MODULE('{module}', NULL); END;");
            return c;
        }

        public static async Task ExecAsync(OracleConnection c, string sql, int timeoutS = 30, CancellationToken ct = default)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutS;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<MonitorSnapshot> WaitSnapshotAsync(string monitorId, Func<MonitorSnapshot, bool> until, int timeoutMs = 30_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var hit = H.Notifier.Of<MonitorSnapshot>().LastOrDefault(s => s.MonitorId == monitorId && until(s));
                if (hit != null) return hit;
                if (H.Notifier.Of<MonitorStoppedEvent>().FirstOrDefault(e => e.MonitorId == monitorId) is { } stopped)
                    throw new InvalidOperationException("The monitor stopped: " + stopped.Reason);
                await Task.Delay(100);
            }
            var all = H.Notifier.Of<MonitorSnapshot>().Where(s => s.MonitorId == monitorId).ToList();
            var last = all.LastOrDefault();
            var trail = string.Join(" / ", all.Select(s => $"{s.Seq}:{s.Connection}:{s.Sessions?.Length}:{string.Join(",", s.Status.Where(x => x.State is PaneState.Error).Select(x => $"{x.Pane} {x.Reason}"))}").Take(40));
            throw new TimeoutException($"No matching snapshot ({all.Count}: {trail}); last: {string.Join(", ", last?.Status.Select(s => $"{s.Pane}={s.State}:{s.Reason}") ?? [])}; " +
                $"error {last?.ConnectionError}; sessions {string.Join(" | ", last?.Sessions?.Select(r => $"{r.Key.Id}/{r.Key.Instance} {r.Status} {r.WaitType} blockedBy={string.Join(',', r.BlockedBy)}") ?? [])}");
        }

        public MonitorCapabilities Caps(string monitorId) => H.Notifier.Of<MonitorSnapshot>().Last(s => s.MonitorId == monitorId && s.Capabilities != null).Capabilities!;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            // ODP.NET's Dispose blocks its thread: off the test's thread, and bounded, so a stuck one is abandoned.
            foreach (var c in _foreign)
                try { await Task.Run(c.Dispose).WaitAsync(TimeSpan.FromSeconds(30)); } catch { /* killed, or abandoned */ }
            await H.DisposeAsync();
            _stop.Dispose();
        }
    }

    static async Task<long> SidAsync(OracleConnection c)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SYS_CONTEXT('USERENV', 'SID') FROM DUAL";
        return long.Parse(Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
    }

    /// <summary>A holder with an uncommitted UPDATE and a victim waiting for the same row.</summary>
    /// <remarks>The SIDs are read first: ODP.NET runs one call at a time per connection, so a read on the victim would wait for its UPDATE.</remarks>
    static async Task<(OracleConnection Holder, OracleConnection Victim, Task VictimDone, long H, long V)> BlockAsync(Rig rig, int id)
    {
        var holder = await rig.ForeignAsync("mon-holder");
        var victim = await rig.ForeignAsync("mon-victim");
        var (h, v) = (await SidAsync(holder), await SidAsync(victim));
        // ODP.NET autocommits outside a transaction: the holder's UPDATE would release its row at once.
        holder.BeginTransaction();
        victim.BeginTransaction();
        await Rig.ExecAsync(holder, $"UPDATE {Table} SET v = v + 1 WHERE id = {id}");
        var victimDone = Task.Run(() => Rig.ExecAsync(victim, $"UPDATE {Table} SET v = 0 WHERE id = {id}", 90, rig.Stop));
        return (holder, victim, victimDone, h, v);
    }

    static readonly MonitorPane[] Panes = [MonitorPane.Sessions, MonitorPane.Locks, MonitorPane.Metrics, MonitorPane.TopQueries, MonitorPane.Waits, MonitorPane.Io, MonitorPane.Deadlocks];

    [IntegrationFact]
    public async Task A_row_lock_chain_its_locks_waits_metrics_and_top_queries_and_a_guarded_terminate()
    {
        await using var rig = new Rig();
        await rig.InitAsync("system");
        var (holder, victim, victimDone, h, v) = await BlockAsync(rig, 1);

        var m = await rig.H.Rpc.MonitorStart(new MonitorStartParams { ProfileId = rig.P.Id, IntervalMs = 1_000, Panes = Panes });
        Assert.Equal(Engine.Oracle, m.Engine);
        var snap = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Sessions?.Any(r => r.Key.Id == v && r.BlockedBy.Contains(h)) == true);
        var victimRow = snap.Sessions!.Single(r => r.Key.Id == v);
        Assert.Equal("enq: TX - row lock contention", victimRow.WaitType);
        Assert.Equal($"QUERITHM_APP.{Table}", victimRow.WaitResource);
        Assert.Equal("active", victimRow.Status);
        Assert.NotNull(victimRow.StatementStart);
        Assert.Equal("QUERITHM_APP", victimRow.Login);
        var holderRow = snap.Sessions!.Single(r => r.Key.Id == h);
        Assert.Equal("idle in transaction", holderRow.Status);
        Assert.Equal(1, holderRow.OpenTx);
        Assert.True(holderRow.TxElapsedMs >= 0);
        Assert.Matches(@"^\d+:\d{14}$", holderRow.Key.Instance);
        Assert.Contains(h, snap.Blocking!.HeadBlockers);
        Assert.Contains(snap.Sessions!, r => r.QuerithmKind == "monitor");

        var caps = rig.Caps(m.MonitorId);
        Assert.True(caps.CanTerminate && caps.CanCancelRequest && caps.AllSessionsVisible);
        Assert.StartsWith("Oracle", caps.ServerVersion);

        var locks = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Locks?.Any(l => l.Key.Id == v && l.Status == "waiting") == true);
        Assert.Contains(locks.Locks!, l => l.Key.Id == h && l.Status == "granted" && l.ResourceType == "TM (table)" && l.Object == $"QUERITHM_APP.{Table}");
        Assert.Contains(locks.Locks!, l => l.Key.Id == h && l.Status == "granted" && l.ResourceType == "TX (transaction)" && l.Mode == "exclusive (X)");
        Assert.Contains(locks.Locks!, l => l.Key.Id == v && l.Status == "waiting" && l.ResourceType == "TX (transaction)" && l.Object == $"QUERITHM_APP.{Table}");

        var metrics = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Metrics?.Values.Any(x => x.Key == "queries" && x.Value != null) == true);
        Assert.Contains(metrics.Metrics!.Values, x => x.Key == "activeConnections" && x.Value >= 1);
        Assert.Contains(metrics.Metrics!.Values, x => x.Key == "idleInTx" && x.Value >= 1);
        Assert.Contains(metrics.Metrics!.Values, x => x.Key == "cacheHit");
        Assert.Contains(metrics.Metrics!.Values, x => x.Key == "deadlocks" && x.Value == null && x.Note!.Contains("alert log"));
        await rig.WaitSnapshotAsync(m.MonitorId, s => s.TopQueries is { Length: > 0 } && s.Waits != null);
        var degraded = rig.H.Notifier.Of<MonitorSnapshot>().Last(s => s.MonitorId == m.MonitorId);
        Assert.Contains(degraded.Status, s => s.Pane == MonitorPane.Io && s.State == PaneState.Degraded && s.Reason!.Contains("Oracle"));
        Assert.Contains(degraded.Status, s => s.Pane == MonitorPane.Deadlocks && s.State == PaneState.Degraded && s.Reason!.Contains("ORA-00060"));

        // What a terminate would undo, a stale identity refused, then the terminate itself; the victim gets the row.
        var preview = await rig.H.Rpc.MonitorKillPreview(new MonitorSessionParams { MonitorId = m.MonitorId, Key = holderRow.Key });
        Assert.Equal((RollbackRisk.Low, true, 1), (preview.Risk, preview.HasWrites, preview.TxCount));
        Assert.Contains("undo record", preview.Reason);
        Assert.Equal(1, preview.BlockedCount);
        var serial = long.Parse(holderRow.Key.Instance.Split(':')[0], CultureInfo.InvariantCulture);
        var wrong = holderRow.Key with { Instance = $"{serial + 1}:{holderRow.Key.Instance.Split(':')[1]}" };
        var refused = await rig.H.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = wrong, Mode = KillMode.TerminateSession, ConfirmedKill = true });
        Assert.Equal(KillOutcome.Refused, refused.Outcome);
        var done = await rig.H.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = holderRow.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
        Assert.True(done.Outcome == KillOutcome.Done, done.Message);
        await victimDone.WaitAsync(TimeSpan.FromSeconds(30));
        await Rig.ExecAsync(victim, "ROLLBACK");
        var dead = await Assert.ThrowsAnyAsync<Exception>(() => Rig.ExecAsync(holder, "SELECT 1 FROM DUAL"));
        Assert.True(dead is OracleException or InvalidOperationException, dead.ToString());
        await rig.H.Rpc.MonitorStop(new MonitorRef { MonitorId = m.MonitorId });
    }

    [IntegrationFact]
    public async Task A_running_statement_is_cancelled_by_its_execution_and_its_session_stays()
    {
        await using var rig = new Rig();
        await rig.InitAsync("system");
        var busy = await rig.ForeignAsync("mon-busy");
        var b = await SidAsync(busy);
        var running = Task.Run(async () =>
        {
            await using var cmd = busy.CreateCommand();
            cmd.CommandText = "SELECT /* q_s12_long */ COUNT(*) FROM all_objects a, all_objects b, all_objects c";
            cmd.CommandTimeout = 120;
            await cmd.ExecuteScalarAsync(rig.Stop);
        });
        var m = await rig.H.Rpc.MonitorStart(new MonitorStartParams { ProfileId = rig.P.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions] });
        var snap = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Sessions?.Any(r => r.Key.Id == b && r.StatementStart != null && r.Command == "SELECT") == true);
        var row = snap.Sessions!.Single(r => r.Key.Id == b);
        Assert.Matches("^[0-9a-z]{13}:[0-9]+$", row.StatementStart);
        Assert.Equal(row.StatementStart![..13], row.QueryHash);
        Assert.True(row.ElapsedMs >= 0);

        // Another execution of the statement (or a later one) is not cancelled.
        var parts = row.StatementStart!.Split(':');
        var stale = await rig.H.Rpc.MonitorKill(new MonitorKillParams
        {
            MonitorId = m.MonitorId, Target = row.Key, Mode = KillMode.CancelRequest, ConfirmedKill = true, ExpectedStatementStart = $"{parts[0]}:{long.Parse(parts[1], CultureInfo.InvariantCulture) + 1000}",
        });
        Assert.NotEqual(KillOutcome.Done, stale.Outcome);
        Assert.False(running.IsCompleted);

        var r = await rig.H.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = row.Key, Mode = KillMode.CancelRequest, ConfirmedKill = true, ExpectedStatementStart = row.StatementStart });
        Assert.True(r.Outcome == KillOutcome.Done, r.Message);
        // ODP.NET reports the server's ORA-01013 as an OperationCanceledException around the OracleException.
        var cancelled = await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1013, (cancelled as OracleException ?? cancelled.InnerException as OracleException)?.Number);
        await Rig.ExecAsync(busy, "SELECT 1 FROM DUAL"); // the session stays connected
        await rig.H.Rpc.MonitorStop(new MonitorRef { MonitorId = m.MonitorId });
    }

    [IntegrationFact]
    public async Task Without_alter_system_the_monitor_shows_the_chain_and_refuses_kills_with_the_reason()
    {
        await using var rig = new Rig();
        await rig.InitAsync(OracleIntegrationTests.MonitorUser);
        var (holder, victim, victimDone, h, v) = await BlockAsync(rig, 2);
        var m = await rig.H.Rpc.MonitorStart(new MonitorStartParams { ProfileId = rig.P.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions, MonitorPane.Locks, MonitorPane.Metrics] });
        var snap = await rig.WaitSnapshotAsync(m.MonitorId, s => s.Sessions?.Any(r => r.Key.Id == v && r.BlockedBy.Contains(h)) == true);
        var caps = rig.Caps(m.MonitorId);
        Assert.False(caps.CanTerminate || caps.CanCancelRequest);
        Assert.Contains("ALTER SYSTEM", caps.TerminateReason);
        Assert.Equal("QUERITHM_MON", caps.Login);

        var holderRow = snap.Sessions!.Single(r => r.Key.Id == h);
        var kill = await rig.H.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = holderRow.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
        Assert.Equal((KillOutcome.Refused, "unsupported"), (kill.Outcome, kill.Code));
        Assert.Contains("ALTER SYSTEM", kill.Message);
        await Rig.ExecAsync(holder, "SELECT 1 FROM DUAL"); // untouched
        await Rig.ExecAsync(holder, "ROLLBACK");
        await victimDone.WaitAsync(TimeSpan.FromSeconds(30));
        await Rig.ExecAsync(victim, "ROLLBACK");
        await rig.H.Rpc.MonitorStop(new MonitorRef { MonitorId = m.MonitorId });
    }

    [IntegrationFact]
    public async Task Without_v_session_the_monitor_stops_naming_the_grant_and_the_banner_says_why()
    {
        await using var rig = new Rig();
        await rig.InitAsync(OracleIntegrationTests.AppUser);
        var m = await rig.H.Rpc.MonitorStart(new MonitorStartParams { ProfileId = rig.P.Id, IntervalMs = 1_000 });
        var deadline = DateTime.UtcNow.AddSeconds(30);
        MonitorStoppedEvent? stopped = null;
        while (stopped == null && DateTime.UtcNow < deadline)
        {
            stopped = rig.H.Notifier.Of<MonitorStoppedEvent>().FirstOrDefault(e => e.MonitorId == m.MonitorId);
            await Task.Delay(100);
        }
        Assert.NotNull(stopped);
        Assert.Contains("V_$SESSION", stopped.Reason);

        // The own-tab blocking check on such a profile says why instead of reporting nobody blocked.
        await using var app = await rig.ForeignAsync("mon-banner");
        var (rows, unavailable) = await new OracleActivity().BlockedByAsync(app, [await SidAsync(app)], CancellationToken.None);
        Assert.Null(rows);
        Assert.Contains("V_$SESSION", unavailable);
    }

    [IntegrationFact]
    public async Task The_blocking_banner_counts_the_sessions_a_session_blocks()
    {
        await using var rig = new Rig();
        await rig.InitAsync("system");
        var (holder, victim, victimDone, h, _) = await BlockAsync(rig, 3);
        await using var admin = new OracleConnection(new OracleConnectionStringBuilder(OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser))
        {
            UserID = "system", Password = FixtureSecrets.Password("oracle-admin"),
        }.ConnectionString);
        await admin.OpenAsync();
        BlockedBySummary[]? rows = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && rows is not [_])
        {
            (rows, var unavailable) = await new OracleActivity().BlockedByAsync(admin, [h, 999_999], CancellationToken.None);
            Assert.Null(unavailable);
            await Task.Delay(200);
        }
        var row = Assert.Single(rows!);
        Assert.Equal((h, 1), (row.BlockerId, row.BlockedCount));
        Assert.True(row.MaxWaitMs >= 0);
        await Rig.ExecAsync(holder, "ROLLBACK");
        await victimDone.WaitAsync(TimeSpan.FromSeconds(30));
        await Rig.ExecAsync(victim, "ROLLBACK");
    }
}

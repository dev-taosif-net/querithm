using System.Data.Common;
using System.Diagnostics;
using Querithm.Contract;
using Querithm.Core.Monitoring;

namespace Querithm.Tests.Integration;

/// <summary>
/// More activity-monitor behaviour on the real fixture engines (Phase B matrix type I): blocking chains, own-tab banner,
/// diagnosis, text identity, locks under schema locks, counter resets, catalog differences between PostgreSQL versions,
/// SSH, and a server restart. Enable with QUERITHM_IT=1. Uses the table monitor_it2 so it never contends with
/// MonitorIntegrationTests (same collection per engine: they run one after the other).
/// </summary>
public class MonitorScenarioTests
{
    public abstract class Scenarios(Engine engine) : MonitorIntegrationTests.Fixture(engine)
    {
        protected string T2 => Ms ? "dbo.monitor_it2" : "monitor_it2";

        protected async Task EnsureTable2Async()
        {
            await using var c = await OpenForeignAsync("setup2");
            await ExecAsync(c, Ms
                ? "IF OBJECT_ID('dbo.monitor_it2') IS NULL CREATE TABLE dbo.monitor_it2(id int PRIMARY KEY, v int); IF NOT EXISTS (SELECT 1 FROM dbo.monitor_it2) INSERT dbo.monitor_it2 VALUES (1,1),(2,2),(3,3),(4,4);"
                : "CREATE TABLE IF NOT EXISTS monitor_it2(id int PRIMARY KEY, v int); INSERT INTO monitor_it2 VALUES (1,1),(2,2),(3,3),(4,4) ON CONFLICT DO NOTHING;");
            Foreign.Remove(c);
        }

        string Begin => Ms ? "BEGIN TRAN;" : "BEGIN;";

        [IntegrationFact]
        public async Task B1_B2_B8_S9_a_three_level_chain_has_the_idle_holder_as_its_only_head()
        {
            await EnsureTable2Async();
            var m = await StartAsync(MonitorPane.Sessions);
            var a = await OpenForeignAsync("chain-a");
            await ExecAsync(a, $"{Begin} UPDATE {T2} SET v = v + 1 WHERE id = 1;");
            var b = await OpenForeignAsync("chain-b");
            await ExecAsync(b, $"{Begin} UPDATE {T2} SET v = v + 1 WHERE id = 2;");
            Busy[b] = Task.Run(async () => { try { await ExecAsync(b, $"UPDATE {T2} SET v = v + 1 WHERE id = 1"); } catch { } });
            var c = await OpenForeignAsync("chain-c");
            Busy[c] = Task.Run(async () => { try { await ExecAsync(c, $"UPDATE {T2} SET v = v + 1 WHERE id = 2"); } catch { } });
            long A = Spid(a), B = Spid(b), C = Spid(c);
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == C && r.BlockedBy.Contains(B)) == true
                && x.Sessions.Any(r => r.Key.Id == B && r.BlockedBy.Contains(A)));
            Assert.Equal([A], s.Blocking!.HeadBlockers.Where(h => h == A || h == B || h == C).ToArray());
            var head = s.Sessions!.Single(r => r.Key.Id == A);
            Assert.True(head.OpenTx >= 1);                                  // S9: idle with an open transaction, flagged
            Assert.True(head.Status is "sleeping" or "idle in transaction");
            Assert.NotNull(head.TxElapsedMs);
            Assert.Equal(2, BlockingGraph.VictimCount(s.Sessions!, A));
            // W6: "Why is it slow?" on C names its blocker; on B it says the head is idle in a transaction.
            var dc = await Host.Rpc.MonitorDiagnose(new MonitorSessionParams { MonitorId = m.MonitorId, Key = s.Sessions!.Single(r => r.Key.Id == C).Key });
            Assert.Equal(B, dc.Findings[0].BlockerId);
            var db = await Host.Rpc.MonitorDiagnose(new MonitorSessionParams { MonitorId = m.MonitorId, Key = s.Sessions!.Single(r => r.Key.Id == B).Key });
            Assert.Equal("idleBlocker", db.Findings[0].Rule);
            // P1: a session without an open transaction has no rollback risk.
            var idle = await OpenForeignAsync("no-tx");
            await ExecAsync(idle, "SELECT 1");
            // Matched by spid and application: SQL Server reuses a spid at once, and a just-ended session can still be in the snapshot.
            bool Mine(ActivityRow r) => r.Key.Id == Spid(idle) && r.Program == "no-tx";
            var s2 = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(Mine) == true);
            var p = await Host.Rpc.MonitorKillPreview(new MonitorSessionParams { MonitorId = m.MonitorId, Key = s2.Sessions!.Single(Mine).Key });
            Assert.True(p.Risk == RollbackRisk.None, $"{p.Risk}: {p.Reason}");
        }

        [IntegrationFact]
        public async Task O4_O5_O12_the_own_tab_banner_check_counts_the_sessions_a_tab_blocks_and_clears_after_commit()
        {
            await EnsureTable2Async();
            await Host.Rpc.TxBegin(new SessionRef { SessionId = SessionId });
            var (done, _, _) = await Host.RunAsync(SessionId, $"UPDATE {T2} SET v = v + 1 WHERE id = 3");
            Assert.Equal(ExecState.Done, done.State);
            var victim = await OpenForeignAsync("banner-victim");
            Busy[victim] = Task.Run(async () => { try { await ExecAsync(victim, $"UPDATE {T2} SET v = v + 10 WHERE id = 3"); } catch { } });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            SessionBlockingEvent? shown = null;
            var cycles = new List<string>();
            while (DateTime.UtcNow < deadline && shown == null)
            {
                await Task.Delay(1_000);
                var before = Host.Services.Banner.Checks;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Host.Services.Banner.RunCycleAsync(CancellationToken.None);
                cycles.Add($"{sw.ElapsedMilliseconds}ms/{Host.Services.Banner.Checks - before}");
                shown = Host.Notifier.Of<SessionBlockingEvent>().LastOrDefault(e => e.SessionId == SessionId && e.BlockedCount > 0);
            }
            if (shown == null)
            {
                var s = Host.Services.Sessions.Get(SessionId);
                var key = Host.Services.Sessions.Registry.KeyOf(s.Pinned!.Db);
                var pool = Host.Services.Sessions.PoolFor(s);
                var c = await pool.BorrowAsync(null, null, CancellationToken.None);
                string why;
                try { var r = await s.Provider.Activity!.BlockedByAsync(c.Db, [key!.Id], CancellationToken.None); why = $"rows {string.Join(",", r.Rows?.Select(x => $"{x.BlockerId}:{x.BlockedCount}:{x.MaxWaitMs}") ?? [])} unavailable {r.Unavailable}"; }
                catch (Exception e) { why = e.ToString(); }
                finally { pool.Return(c); }
                await using var admin = await OpenForeignAsync("banner-diag");
                var activity = new List<string>();
                await using (var cmd = admin.CreateCommand())
                {
                    cmd.CommandText = Ms ? "SELECT CONCAT(r.session_id, ' blocked by ', r.blocking_session_id, ' ', r.wait_type) FROM sys.dm_exec_requests r WHERE r.blocking_session_id <> 0"
                        : "SELECT pid || ' ' || coalesce(application_name,'') || ' ' || coalesce(state,'') || ' ' || coalesce(wait_event_type,'') || ' ' || pg_blocking_pids(pid)::text FROM pg_stat_activity WHERE backend_type = 'client backend'";
                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync()) activity.Add(rd.GetString(0));
                }
                Assert.Fail($"no banner event; tx {s.TxState}, pinned key {key?.Id}, checks {Host.Services.Banner.Checks} cycles [{string.Join(" ", cycles)}], last error {Host.Services.Banner.LastError}, direct check: {why}; activity: {string.Join(" | ", activity)}");
            }
            Assert.Equal(1, shown!.BlockedCount);
            Assert.True(shown.MaxWaitMs >= BlockingBannerService.ShowAfterMs);
            Assert.Equal("check", shown.Source);
            await Host.Rpc.TxCommit(new SessionRef { SessionId = SessionId });
            await Busy[victim].WaitAsync(TimeSpan.FromSeconds(10));
            await Host.Services.Banner.RunCycleAsync(CancellationToken.None);
            Assert.Equal(0, Host.Notifier.Of<SessionBlockingEvent>().Last(e => e.SessionId == SessionId).BlockedCount); // O5
        }

        [IntegrationFact]
        public async Task M14_a_kill_on_the_control_connection_and_banner_checks_keep_the_profile_within_its_connection_budget()
        {
            // Budget per profile: 1 monitor connection, +1 control connection while a pre-empting kill runs, +1 for the shared
            // banner check — beside the tabs' own connections. Both transient paths are forced and counted on the server.
            await EnsureTable2Async();
            // The profile's pool connections (its tab, the banner check) carry an application name of their own, so other
            // tests' Querithm connections are not counted; the monitor and its control connection are "Querithm Monitor".
            var app = "m14-" + Guid.NewGuid().ToString("N")[..8];
            var profile = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "m14 budget", Options = new Dictionary<string, string>(Profile.Options ?? []) { ["appName"] = app } });
            var tab = await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = profile.Id, Secret = Ms ? TestHost.MssqlPassword : TestHost.PgPassword, Database = Profile.DatabaseDefault });
            // A tab with an open transaction, so the banner check runs for this profile. The monitor reads one database only,
            // so its snapshots do not answer for the tab (O13): the check really borrows a pool connection.
            await Host.Rpc.TxBegin(new SessionRef { SessionId = tab.SessionId });
            var (done, _, _) = await Host.RunAsync(tab.SessionId, $"UPDATE {T2} SET v = v + 1 WHERE id = 3");
            Assert.Equal(ExecState.Done, done.State);
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = profile.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions], DatabaseFilter = Ms ? "MonitorIT" : "postgres" });
            var holder = await OpenForeignAsync("budget-holder");
            await ExecAsync(holder, $"{Begin} UPDATE {T2} SET v = v + 1 WHERE id = 4;");
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(holder)) == true);
            Assert.False(Host.Services.Monitors.AnswersBannerFor(profile.Id));

            await using var admin = await OpenForeignAsync("budget-admin");
            var (maxMonitor, maxPool, maxTotal, samples) = (0, 0, 0, 0);
            using var stopCounting = new CancellationTokenSource();
            var counting = Task.Run(async () =>
            {
                while (!stopCounting.IsCancellationRequested)
                {
                    await using var cmd = admin.CreateCommand();
                    cmd.CommandText = Ms
                        ? $"SELECT SUM(CASE WHEN program_name = N'Querithm Monitor' THEN 1 ELSE 0 END), SUM(CASE WHEN program_name = N'{app}' THEN 1 ELSE 0 END) FROM sys.dm_exec_sessions"
                        : $"SELECT count(*) FILTER (WHERE application_name = 'Querithm Monitor'), count(*) FILTER (WHERE application_name = '{app}') FROM pg_stat_activity";
                    await using var r = await cmd.ExecuteReaderAsync();
                    await r.ReadAsync();
                    int mon = Convert.ToInt32(r.GetValue(0)), pool = Convert.ToInt32(r.GetValue(1));
                    (maxMonitor, maxPool, maxTotal, samples) = (Math.Max(maxMonitor, mon), Math.Max(maxPool, pool), Math.Max(maxTotal, mon + pool), samples + 1);
                    await Task.Delay(10);
                }
            });

            // The kill arrives while a job holds the monitor connection, so it runs on the control connection.
            var monitor = Host.Services.Monitors.Get(m.MonitorId);
            var holding = new TaskCompletionSource();
            var job = monitor.InteractiveAsync("m14-hold", async (_, c, ct) =>
            {
                holding.SetResult();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = Ms ? "WAITFOR DELAY '00:00:03'" : "SELECT pg_sleep(3)";
                await cmd.ExecuteNonQueryAsync(ct);
                return 0;
            }, 10_000);
            await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var kill = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = s.Sessions!.Single(x => x.Key.Id == Spid(holder)).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            await job;
            // Banner checks run on the service's own 10 s timer (a direct call could overlap it, which the product never does).
            var checks = Host.Services.Banner.Checks;
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (Host.Services.Banner.Checks < checks + 2 && DateTime.UtcNow < deadline) await Task.Delay(200);
            var ran = Host.Services.Banner.Checks - checks;
            stopCounting.Cancel();
            await counting;
            await Host.Rpc.TxRollback(new SessionRef { SessionId = tab.SessionId });

            Assert.Equal(KillOutcome.Done, kill.Outcome);
            Assert.Equal("control", kill.Path);
            Assert.True(ran >= 2, $"{ran} banner checks in 25 s (last error {Host.Services.Banner.LastError})");
            var seen = $"max monitor {maxMonitor}, max pool {maxPool}, max total {maxTotal} over {samples} samples";
            Assert.InRange(maxMonitor, 1, 2);  // the monitor, plus the control connection only while the kill ran
            Assert.InRange(maxPool, 1, 2);     // the tab's pinned connection, plus one for every banner check (reused, never a second)
            Assert.True(maxTotal <= 4, seen);
            Assert.True(samples >= 100, seen);
        }

        [IntegrationFact]
        public async Task O15_two_tabs_that_borrow_the_same_pool_connection_in_turn_each_own_it_only_during_their_run()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var other = await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = Profile.Id, Secret = Ms ? TestHost.MssqlPassword : TestHost.PgPassword, Database = Profile.DatabaseDefault });
            var sleep = Ms ? "WAITFOR DELAY '00:00:03'" : "SELECT pg_sleep(3)";
            static bool Leased(ActivityRow r, string session) => r.QuerithmKind == ConnectionRegistry.Kinds.Pool && r.OwnSessionId == session;
            async Task<ActivityRow> DuringRun(string session)
            {
                var run = Host.RunAsync(session, sleep);
                var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => Leased(r, session)) == true);
                Assert.Equal(ExecState.Done, (await run).Done.State);
                return s.Sessions!.Single(r => Leased(r, session));
            }
            var a = await DuringRun(SessionId);
            // Between the runs the connection is idle in the pool: the same server session, owned by no tab.
            var idle = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key == a.Key && r.OwnSessionId == null) == true);
            Assert.Equal(ConnectionRegistry.Kinds.Pool, idle.Sessions!.Single(r => r.Key == a.Key).QuerithmKind);
            var b = await DuringRun(other.SessionId);
            Assert.Equal(a.Key, b.Key); // the same physical connection, now owned by the other tab
        }

        [IntegrationFact]
        public async Task A_login_whose_identity_read_is_refused_still_queries_and_kills_are_refused_until_its_connection_closes()
        {
            // §6 rule 7 (revised). SQL Server: DENY SELECT on sys.dm_exec_sessions (error 229 — or no row on a query-tab
            // connection, which reports user errors as messages). PostgreSQL: SELECT on pg_stat_activity revoked inside one
            // database (42501); other databases keep it.
            var db = Ms ? "MonitorIT" : "monitor_noident";
            await using (var admin = await OpenForeignAsync("noident-admin"))
            {
                if (Ms)
                    await ExecAsync(admin, "USE master; IF SUSER_ID('it_noident') IS NULL CREATE LOGIN it_noident WITH PASSWORD = 'It_Noident_2026!', CHECK_POLICY = OFF; "
                        + "IF USER_ID('it_noident') IS NULL CREATE USER it_noident FOR LOGIN it_noident; DENY SELECT ON OBJECT::sys.dm_exec_sessions TO it_noident; "
                        + "USE MonitorIT; IF USER_ID('it_noident') IS NULL CREATE USER it_noident FOR LOGIN it_noident;");
                else
                {
                    await ExecAsync(admin, "DO $$BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'it_noident') THEN CREATE ROLE it_noident LOGIN PASSWORD 'It_Noident_2026!'; END IF; END$$;");
                    if (Convert.ToInt64(await ScalarAsync(admin, "SELECT count(*) FROM pg_database WHERE datname = 'monitor_noident'")) == 0)
                        await ExecAsync(admin, "CREATE DATABASE monitor_noident");
                    await using var inDb = await OpenForeignAsync("noident-setup", pgDb: db);
                    await ExecAsync(inDb, "REVOKE SELECT ON pg_catalog.pg_stat_activity FROM PUBLIC;");
                }
            }
            var p = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "no identity", Username = "it_noident", DatabaseDefault = db });
            var tab = await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "It_Noident_2026!", Database = db });
            var (done, batches, _) = await Host.RunAsync(tab.SessionId, "SELECT 42");
            Assert.True(done.State == ExecState.Done, done.Error?.Message);
            Assert.Equal(42, ((Apache.Arrow.Int32Array)batches[0].Column(0)).GetValue(0));
            var why = Host.Services.Sessions.Registry.UnidentifiedReason();
            Assert.Contains(Ms ? "sys.dm_exec_sessions" : "pg_stat_activity", why);

            // A monitor on the same server (a login that can read its own row): every kill is refused with the reason.
            var m = await StartAsync(MonitorPane.Sessions);
            var victim = await OpenForeignAsync("noident-victim");
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(victim)) == true);
            var key = s.Sessions!.Single(r => r.Key.Id == Spid(victim)).Key;
            var kill = new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true };
            var refused = await Host.Rpc.MonitorKill(kill);
            Assert.Equal((KillOutcome.Refused, "querithmUnidentified"), (refused.Outcome, refused.Code));
            Assert.Contains(why!, refused.Message);
            Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(victim, "SELECT 1"))); // nothing was sent

            // Its connection closed (the tab closed and the profile's idle connections with it): kills work again.
            await Host.Rpc.SessionClose(new SessionCloseParams { SessionId = tab.SessionId, Force = true });
            Host.Services.Sessions.ForgetPools(p.Id);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Host.Services.Sessions.Registry.UnidentifiedReason() != null && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Null(Host.Services.Sessions.Registry.UnidentifiedReason());
            Assert.Equal(KillOutcome.Done, (await Host.Rpc.MonitorKill(kill)).Outcome);
        }
    }

    [Collection("monitor-mssql")]
    public class SqlServer() : Scenarios(Engine.Mssql)
    {
        [IntegrationFact]
        public async Task S6_an_encrypted_module_shows_text_unavailable()
        {
            await using (var c = await OpenForeignAsync("enc-setup"))
                await ExecAsync(c, "CREATE OR ALTER PROCEDURE dbo.monitor_enc WITH ENCRYPTION AS WAITFOR DELAY '00:00:06';");
            var m = await StartAsync(MonitorPane.Sessions);
            var runner = await OpenForeignAsync("enc-run");
            Busy[runner] = Task.Run(async () => { try { await ExecAsync(runner, "EXEC dbo.monitor_enc"); } catch { } });
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(runner) && r.TextNote != null) == true);
            var row = s.Sessions!.Single(r => r.Key.Id == Spid(runner));
            Assert.Equal("text unavailable (encrypted)", row.TextNote);
            Assert.Null(row.TextId);
        }

        [IntegrationFact]
        public async Task S16_altering_a_procedure_that_keeps_its_offsets_gets_a_new_text_id_and_the_new_text()
        {
            await using var setup = await OpenForeignAsync("alter-setup");
            await ExecAsync(setup, "CREATE OR ALTER PROCEDURE dbo.monitor_alt AS WAITFOR DELAY '00:00:04';");
            var m = await StartAsync(MonitorPane.Sessions);
            async Task<(string Id, string Text)> RunAndRead()
            {
                var r = await OpenForeignAsync("alter-run");
                var t = Task.Run(async () => { try { await ExecAsync(r, "EXEC dbo.monitor_alt"); } catch { } });
                // Matched by program name too: a snapshot from before this connection opened can hold the spid's previous
                // owner (it read "SELECT DB_NAME(), …" from another test's connection once).
                bool Mine(ActivityRow row) => row.Key.Id == Spid(r) && row.Program == "alter-run";
                var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(row => Mine(row) && row.TextId != null && row.Command != null) == true);
                var id = s.Sessions!.Single(Mine).TextId!;
                var text = (await Host.Rpc.MonitorTexts(new MonitorTextsParams { MonitorId = m.MonitorId, Ids = [id] })).Texts[id];
                await t;
                return (id, text);
            }
            var first = await RunAndRead();
            Assert.Contains("00:00:04", first.Text);
            await ExecAsync(setup, "CREATE OR ALTER PROCEDURE dbo.monitor_alt AS WAITFOR DELAY '00:00:05';"); // same length, same offsets
            var second = await RunAndRead();
            Assert.Contains("00:00:05", second.Text);
            Assert.NotEqual(first.Id, second.Id);
        }

        [IntegrationFact]
        public async Task M3_the_lock_pane_returns_under_a_schema_modification_lock_and_shows_the_id_not_a_hang()
        {
            await EnsureTable2Async();
            var m = await StartAsync(MonitorPane.Sessions, MonitorPane.Locks);
            var ddl = await OpenForeignAsync("schmod");
            await ExecAsync(ddl, "BEGIN TRAN; ALTER TABLE dbo.monitor_it2 ADD tmp_col int NULL;");
            var sw = Stopwatch.StartNew();
            var s = await WaitSnapshot(m.MonitorId, x => x.Locks?.Any(l => l.Key.Id == Spid(ddl) && l.Mode == "Sch-M") == true, 30_000);
            var lockRow = s.Locks!.First(l => l.Key.Id == Spid(ddl) && l.Mode == "Sch-M" && l.ResourceType == "OBJECT");
            Assert.StartsWith("MonitorIT.", lockRow.Object);
            // The name comes from the metadata cache or the lazy resolver (under LOCK_TIMEOUT: "name locked" when the
            // catalog is blocked) — either way the pane keeps answering and the monitor keeps ticking: no hang.
            var later = await WaitSnapshot(m.MonitorId, x => x.Seq > s.Seq + 5 && x.Locks?.Any(l => l.Key.Id == Spid(ddl) && l.Mode == "Sch-M" && l.ResourceType == "OBJECT"
                && (l.ObjectNote is "name locked" or "name from metadata cache" || l.Object == "dbo.monitor_it2")) == true, 40_000);
            var resolved = later.Locks!.First(l => l.Key.Id == Spid(ddl) && l.Mode == "Sch-M" && l.ResourceType == "OBJECT");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "querithm-m3.txt"), DateTime.UtcNow.ToString("O") + " Sch-M object shown as " + resolved.Object + " (" + resolved.ObjectNote + ")" + Environment.NewLine);
            Assert.Equal(PaneState.Ok, later.Status.Single(x => x.Pane == MonitorPane.Sessions).State);
            await ExecAsync(ddl, "ROLLBACK");
            _ = sw;
        }

        [IntegrationFact]
        public async Task D2_clearing_wait_statistics_is_reported_as_a_reset_not_negative_waits()
        {
            var m = await StartAsync(MonitorPane.Waits);
            await WaitSnapshot(m.MonitorId, x => x.Status.Any(s => s.Pane == MonitorPane.Waits && s.State == PaneState.Ok));
            await using var admin = await OpenForeignAsync("clear");
            await ExecAsync(admin, "DBCC SQLPERF('sys.dm_os_wait_stats', CLEAR)");
            var r = await WaitSnapshot(m.MonitorId, x => x.Status.Any(s => s.Pane == MonitorPane.Waits && s.State == PaneState.ResetDetected));
            Assert.Null(r.Waits);
            Assert.DoesNotContain(Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId).SelectMany(x => x.Waits ?? []), w => w.WaitMsDelta < 0);
        }

        [IntegrationFact]
        public async Task K11_K17_system_sessions_and_logins_without_alter_any_connection_are_refused_before_anything_is_sent()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => !r.IsUserSession) == true);
            var sys = s.Sessions!.First(r => !r.IsUserSession);
            var r1 = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = sys.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.Equal("systemSession", r1.Code);
            await using var admin = await OpenForeignAsync("perm-admin");
            await ExecAsync(admin, "USE master; IF SUSER_ID('it_perf') IS NULL CREATE LOGIN it_perf WITH PASSWORD = 'It_Perf_2026!', CHECK_POLICY = OFF; GRANT VIEW SERVER PERFORMANCE STATE TO it_perf; USE MonitorIT; IF USER_ID('it_perf') IS NULL CREATE USER it_perf FOR LOGIN it_perf;");
            var low = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "perf only", Username = "it_perf" });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = low.Id, Secret = "It_Perf_2026!" });
            var victim = await OpenForeignAsync("perm-victim");
            var m2 = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = low.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions] });
            var s2 = await WaitSnapshot(m2.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(victim)) == true);
            var r2 = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m2.MonitorId, Target = s2.Sessions!.Single(r => r.Key.Id == Spid(victim)).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.Equal("unsupported", r2.Code);
            Assert.Contains("ALTER ANY CONNECTION", r2.Message);
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(victim, "SELECT 1")));
        }

        [IntegrationFact]
        public async Task X8_with_system_health_stopped_the_deadlock_pane_says_so()
        {
            await using var admin = await OpenForeignAsync("xe-admin");
            await ExecAsync(admin, "ALTER EVENT SESSION system_health ON SERVER STATE = STOP;"); // the test, not the monitor
            try
            {
                var m = await StartAsync(MonitorPane.Sessions, MonitorPane.Deadlocks);
                await WaitSnapshot(m.MonitorId, x => x.Connection == MonitorConnectionState.Connected && x.Sessions != null);
                var r = await Host.Rpc.MonitorDeadlocks(new DeadlocksParams { MonitorId = m.MonitorId });
                Assert.Equal(PaneState.Degraded, r.Status.State);
                Assert.Contains("system_health", r.Status.Reason);
            }
            finally { await ExecAsync(admin, "ALTER EVENT SESSION system_health ON SERVER STATE = START;"); }
        }
    }

    [Collection("monitor-pg")]
    public class Postgres() : Scenarios(Engine.Pg)
    {
        [IntegrationFact("QUERITHM_IT_LONG")]
        public async Task P2_P3_P8_a_write_transaction_open_ten_minutes_is_a_high_rollback_risk_and_a_read_only_one_stays_low()
        {
            // PostgreSQL reports no per-transaction log size, so a write transaction is high risk by age alone (≥ 10 min):
            // opt-in and long (QUERITHM_IT_LONG=1, ~10 min). Run it alone; the soak checks the same at its minute 10.5.
            await using (var setup = await OpenForeignAsync("p2-setup"))
                await ExecAsync(setup, "CREATE TABLE IF NOT EXISTS monitor_p2(id int PRIMARY KEY, v int); INSERT INTO monitor_p2 VALUES (1,1) ON CONFLICT DO NOTHING;");
            var writer = await OpenForeignAsync("p2-writer");
            await ExecAsync(writer, "BEGIN; UPDATE monitor_p2 SET v = v + 1 WHERE id = 1;");
            var reader = await OpenForeignAsync("p8-reader");
            await ExecAsync(reader, "BEGIN ISOLATION LEVEL REPEATABLE READ; SELECT count(*) FROM monitor_p2;");
            var m = await StartAsync(MonitorPane.Sessions);
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(writer)) == true && x.Sessions.Any(r => r.Key.Id == Spid(reader)));
            var w = s.Sessions!.Single(r => r.Key.Id == Spid(writer)).Key;
            var rd = s.Sessions!.Single(r => r.Key.Id == Spid(reader)).Key;
            Task<KillPreview> Preview(SessionKey k) => Host.Rpc.MonitorKillPreview(new MonitorSessionParams { MonitorId = m.MonitorId, Key = k });

            var early = await Preview(w);
            Assert.Equal(RollbackRisk.Low, early.Risk);
            Assert.True(early.HasWrites);
            await Task.Delay(TimeSpan.FromMilliseconds(KillRisk.HighTxMs - early.TxElapsedMs!.Value + 5_000));
            var late = await Preview(w);
            Assert.Equal(RollbackRisk.High, late.Risk);                // P2: age measured on the server clock
            Assert.True(late.TxElapsedMs >= KillRisk.HighTxMs, $"{late.TxElapsedMs} ms");
            var readOnly = await Preview(rd);
            Assert.Equal(RollbackRisk.Low, readOnly.Risk);             // P8: no xid, nothing to roll back, whatever its age
            Assert.False(readOnly.HasWrites);
            Assert.True(readOnly.TxElapsedMs >= KillRisk.HighTxMs);

            var refused = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = w, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.Equal("longRollback", refused.Code);                // P3, enforced in the sidecar
            var killed = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = w, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            Assert.Equal(KillOutcome.Done, killed.Outcome);
            await Assert.ThrowsAnyAsync<Exception>(() => ExecAsync(writer, "SELECT 1"));
        }

        [IntegrationFact]
        public async Task B4_a_backend_blocked_by_two_others_lists_both()
        {
            await EnsureTable2Async();
            var m = await StartAsync(MonitorPane.Sessions);
            var a = await OpenForeignAsync("share-a");
            await ExecAsync(a, "BEGIN; LOCK TABLE monitor_it2 IN SHARE MODE;");
            var c = await OpenForeignAsync("share-c");
            await ExecAsync(c, "BEGIN; LOCK TABLE monitor_it2 IN SHARE MODE;");
            var b = await OpenForeignAsync("excl-b");
            Busy[b] = Task.Run(async () => { try { await ExecAsync(b, "BEGIN; LOCK TABLE monitor_it2 IN EXCLUSIVE MODE; ROLLBACK;"); } catch { } });
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(b) && r.BlockedBy.Length == 2) == true);
            Assert.Equal(new[] { Spid(a), Spid(c) }.Order(), s.Sessions!.Single(r => r.Key.Id == Spid(b)).BlockedBy);
            Assert.Equal(1, s.Blocking!.BlockedCount);
        }

        [IntegrationFact]
        public async Task S15_two_backends_with_the_same_query_id_and_different_literals_get_distinct_text_ids()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var x = await OpenForeignAsync("lit-1");
            var y = await OpenForeignAsync("lit-2");
            Busy[x] = Task.Run(async () => { try { await ExecAsync(x, "SELECT pg_sleep(4), 1001 AS lit"); } catch { } });
            Busy[y] = Task.Run(async () => { try { await ExecAsync(y, "SELECT pg_sleep(4), 2002 AS lit"); } catch { } });
            // Both active: an idle backend still shows its last query and that query's id (Npgsql's type-loading query on open),
            // so a tick between the two starts once paired x's loader with y's pg_sleep (CI). Program guards against pid reuse.
            bool Running(ActivityRow r, DbConnection c, string app) =>
                r.Key.Id == Spid(c) && r.Program == app && r.Status == "active" && r.TextId != null && r.QueryHash != null;
            var s = await WaitSnapshot(m.MonitorId, q => q.Sessions?.Count(r => Running(r, x, "lit-1") || Running(r, y, "lit-2")) == 2);
            var rx = s.Sessions!.Single(r => r.Key.Id == Spid(x));
            var ry = s.Sessions!.Single(r => r.Key.Id == Spid(y));
            Assert.Equal(rx.QueryHash, ry.QueryHash);
            Assert.NotEqual(rx.TextId, ry.TextId);
            var texts = await Host.Rpc.MonitorTexts(new MonitorTextsParams { MonitorId = m.MonitorId, Ids = [rx.TextId!, ry.TextId!] });
            Assert.Contains("1001", texts.Texts[rx.TextId!]);
            Assert.Contains("2002", texts.Texts[ry.TextId!]);
        }

        [IntegrationFact]
        public async Task LK1_LK2_locks_in_another_database_are_never_named_after_a_local_relation_and_shared_catalogs_are_named()
        {
            await using (var admin = await OpenForeignAsync("lk-admin"))
                if (await ScalarAsync(admin, "SELECT 1 FROM pg_database WHERE datname = 'monitor_other'") == null)
                    try { await ExecAsync(admin, "CREATE DATABASE monitor_other"); } catch (Npgsql.PostgresException) { /* created concurrently */ }
            var other = await OpenForeignAsync("lk-other", pgDb: "monitor_other");
            await ExecAsync(other, "CREATE TABLE IF NOT EXISTS far_away(id int); BEGIN; LOCK TABLE far_away IN ACCESS EXCLUSIVE MODE;");
            var oid = Convert.ToInt64(await ScalarAsync(other, "SELECT 'far_away'::regclass::oid::bigint"));
            var shared = await OpenForeignAsync("lk-shared");
            await ExecAsync(shared, "BEGIN; LOCK TABLE pg_catalog.pg_database IN ACCESS SHARE MODE;");
            var m = await StartAsync(MonitorPane.Locks, MonitorPane.Sessions);
            var s = await WaitSnapshot(m.MonitorId, x => x.Locks?.Any(l => l.Key.Id == Spid(other) && l.ResourceType == "relation") == true
                && x.Locks.Any(l => l.Key.Id == Spid(shared) && l.ResourceType == "relation"), 30_000);
            var far = s.Locks!.First(l => l.Key.Id == Spid(other) && l.ResourceType == "relation" && l.Mode == "AccessExclusiveLock");
            Assert.Equal($"monitor_other.{oid}", far.Object);  // LK1/LK3: never a local name, no connection to that database
            Assert.Equal("other database", far.ObjectNote);
            Assert.Contains(s.Locks!, l => l.Key.Id == Spid(shared) && l.Object == "pg_catalog.pg_database"); // LK2
        }

        [IntegrationFact]
        public async Task K18_a_role_with_pg_signal_backend_cannot_terminate_a_superuser_backend_and_is_told_why()
        {
            await using var admin = await OpenForeignAsync("sig-admin");
            await ExecAsync(admin, "DO $$BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'it_sig') THEN CREATE ROLE it_sig LOGIN PASSWORD 'It_Sig_2026!'; END IF; END$$; GRANT pg_read_all_stats, pg_signal_backend TO it_sig;");
            var sig = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "signal", Username = "it_sig" });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = sig.Id, Secret = "It_Sig_2026!" });
            var su = await OpenForeignAsync("superuser-victim");
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = sig.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions] });
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(su) && r.Key.Instance != "") == true);
            var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = s.Sessions!.Single(x => x.Key.Id == Spid(su)).Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.Equal(KillOutcome.Failed, r.Outcome);
            Assert.Equal("permission", r.Code);
            Assert.Contains("refused", r.Message);
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(su, "SELECT 1"))); // no retry, nothing else happened
        }

        [IntegrationFact]
        public async Task P8_a_read_only_transaction_is_low_risk_however_old()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var ro = await OpenForeignAsync("ro-tx");
            await ExecAsync(ro, "BEGIN; SELECT count(*) FROM pg_class;");
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(ro)) == true);
            var p = await Host.Rpc.MonitorKillPreview(new MonitorSessionParams { MonitorId = m.MonitorId, Key = s.Sessions!.Single(r => r.Key.Id == Spid(ro)).Key });
            Assert.Equal(false, p.HasWrites);
            Assert.Equal(RollbackRisk.Low, p.Risk);
        }

        [IntegrationFact]
        public async Task X11_a_real_postgresql_deadlock_shows_in_the_deadlocks_counter()
        {
            await EnsureTable2Async();
            var m = await StartAsync(MonitorPane.Metrics, MonitorPane.Sessions);
            await WaitSnapshot(m.MonitorId, x => x.Metrics?.Values.Any(v => v.Key == "deadlocks" && v.Value != null) == true);
            var a = await OpenForeignAsync("dl-a");
            var b = await OpenForeignAsync("dl-b");
            await ExecAsync(a, "BEGIN; UPDATE monitor_it2 SET v = v WHERE id = 1;");
            await ExecAsync(b, "BEGIN; UPDATE monitor_it2 SET v = v WHERE id = 2;");
            string? state = null;
            var ta = Task.Run(async () => { try { await ExecAsync(a, "UPDATE monitor_it2 SET v = v WHERE id = 2"); } catch (Npgsql.PostgresException e) { state ??= e.SqlState; } });
            await Task.Delay(200);
            var tb = Task.Run(async () => { try { await ExecAsync(b, "UPDATE monitor_it2 SET v = v WHERE id = 1"); } catch (Npgsql.PostgresException e) { state ??= e.SqlState; } });
            await Task.WhenAll(ta, tb).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("40P01", state); // a real deadlock
            // B0: a backend flushes its statistics (the deadlock count too) only once it is idle outside a transaction
            // block, so the counter moves when the victim's transaction ends, not when the deadlock is detected.
            var seq = Host.Notifier.Of<MonitorSnapshot>().Last(x => x.MonitorId == m.MonitorId).Seq;
            await ExecAsync(a, "ROLLBACK");
            await ExecAsync(b, "ROLLBACK");
            // A per-tick delta: look at every snapshot since, not only the latest one (the stats flush can take up to 10 s).
            double Counted() => Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.Seq > seq && x.Metrics != null)
                .Sum(x => x.Metrics!.Values.Where(v => v.Key == "deadlocks").Sum(v => v.Value ?? 0));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Counted() < 1 && DateTime.UtcNow < deadline) await Task.Delay(200);
            Assert.True(Counted() >= 1, "the deadlocks counter did not move within 30 s");
        }

        [IntegrationFact]
        public async Task D8_D13_T6_postgresql_17_uses_the_checkpointer_view_renamed_statement_columns_and_tick_sampled_waits()
        {
            await using (var setup = await OpenForeignAsync("pg17-setup", pgPort: 54330))
                await ExecAsync(setup, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements; CREATE TABLE IF NOT EXISTS w(id int PRIMARY KEY); INSERT INTO w VALUES (1) ON CONFLICT DO NOTHING;");
            var p17 = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "pg17", Port = 54330 });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p17.Id, Secret = TestHost.PgPassword });
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = p17.Id, IntervalMs = 1_000 });
            var holder = await OpenForeignAsync("pg17-holder", pgPort: 54330);
            await ExecAsync(holder, "BEGIN; UPDATE w SET id = id WHERE id = 1;");
            var victim = await OpenForeignAsync("pg17-victim", pgPort: 54330);
            Busy[victim] = Task.Run(async () => { try { await ExecAsync(victim, "UPDATE w SET id = id WHERE id = 1"); } catch { } });
            var s = await WaitSnapshot(m.MonitorId, x => x.Waits?.Any(w => w.Category == "Lock" && w.WaitMsDelta > 0) == true, 30_000);
            Assert.Equal("tick-sampled", s.WaitCoverage!.Source);                                               // D8
            Assert.Contains(s.Status, st => st.Pane == MonitorPane.Waits && st.Reason!.Contains("low resolution"));
            var metrics = await WaitSnapshot(m.MonitorId, x => x.Metrics?.Values.Any(v => v.Key == "checkpoints" && v.Value != null) == true, 20_000);
            Assert.NotNull(metrics);                                                                             // D13: pg_stat_checkpointer read
            var top = await WaitSnapshot(m.MonitorId, x => x.Status.Any(st => st.Pane == MonitorPane.TopQueries && st.State == PaneState.Ok), 30_000);
            Assert.NotNull(top);                                                                                 // T6: renamed columns mapped
            var io = await WaitSnapshot(m.MonitorId, x => x.Io != null, 20_000);
            Assert.All(io.Io!, r => { Assert.Null(r.ReadStallMs); Assert.Null(r.WriteStallMs); });              // D14: track_io_timing off → null, not 0
            Assert.Contains(io.Status, st => st.Pane == MonitorPane.Io && st.Reason!.Contains("track_io_timing"));
            await ExecAsync(holder, "ROLLBACK");
        }

        [IntegrationFact]
        public async Task T4_D19_on_a_server_without_preloaded_extensions_top_queries_and_query_ids_say_why()
        {
            await using (var setup = await OpenForeignAsync("bare-setup", pgPort: 54331))
                await ExecAsync(setup, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;");
            var bare = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "bare", Port = 54331 });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = bare.Id, Secret = TestHost.PgPassword });
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = bare.Id, IntervalMs = 1_000 });
            var s = await WaitSnapshot(m.MonitorId, x => x.Capabilities != null);
            Assert.Contains("shared_preload_libraries", s.Capabilities!.Panes.Single(p => p.Pane == MonitorPane.TopQueries).Reason);
            Assert.Contains("compute_query_id", s.Capabilities.QueryHashReason);
            var rows = await WaitSnapshot(m.MonitorId, x => x.Sessions != null);
            Assert.All(rows.Sessions!.Where(r => r.TextId != null), r => Assert.Null(r.QueryHash)); // text identity unaffected
        }

        [IntegrationFact("QUERITHM_IT_DISRUPTIVE")]
        public async Task L5_a_server_restart_reconnects_and_reports_the_counter_reset()
        {
            // Restarts the PostgreSQL 17 container, which W8 also uses: opt-in, run alone (QUERITHM_IT_DISRUPTIVE=1), like
            // the SQL Server L5. Run beside the suite it cut W8's connection mid-handshake.
            var p17 = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "pg17 restart", Port = 54330 });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p17.Id, Secret = TestHost.PgPassword });
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = p17.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions, MonitorPane.Metrics] });
            var before = await WaitSnapshot(m.MonitorId, x => x.Metrics?.Values.Any(v => v.Key == "tps" && v.Value != null) == true, 20_000);
            using (var p = Process.Start(new ProcessStartInfo("docker", "restart querithm-fixture-postgres17-1") { RedirectStandardOutput = true, RedirectStandardError = true })!)
                Assert.True(p.WaitForExit(60_000) && p.ExitCode == 0);
            await WaitSnapshot(m.MonitorId, x => x.Seq > before.Seq && x.Connection == MonitorConnectionState.Reconnecting, 30_000);
            await WaitSnapshot(m.MonitorId, x => x.Connection == MonitorConnectionState.Connected && x.Metrics != null, 60_000);
            // After the restart the first sample is a new baseline: no negative or invented rates.
            Assert.DoesNotContain(Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId).SelectMany(x => x.Metrics?.Values ?? []), v => v.Value < 0);
        }

        [IntegrationFact]
        public async Task L6_B0_3_through_an_ssh_tunnel_the_monitor_works_reconnects_after_a_tunnel_drop_and_opens_a_control_connection_quickly()
        {
            var ssh = new SshSettings { Host = "127.0.0.1", Port = 22220, Username = "querithm", Auth = SshAuthKind.Password };
            var viaSsh = Profile with { Id = "", Name = "pg via ssh", Host = "postgres", Port = 5432, Ssh = ssh };
            var test = await Host.Rpc.ProfileTest(new ProfileTestParams { Profile = viaSsh, Secret = TestHost.PgPassword, SshSecret = FixtureSecrets.Password("ssh") });
            if (test.UntrustedHostKey is { } key) await Host.Rpc.SshTrustHostKey(key);
            var saved = await Host.Rpc.ProfileSave(viaSsh);
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = TestHost.PgPassword, SshSecret = FixtureSecrets.Password("ssh") });
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = saved.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions] });
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.QuerithmKind == ConnectionRegistry.Kinds.Monitor) == true, 30_000);
            // B0 item 3: a control connection through the tunnel while the profile's pool is exhausted, within 5 s.
            var profile = Host.Services.Sessions.GetProfile(saved.Id);
            var provider = Host.Services.Sessions.Providers.Get(Engine.Pg);
            var pool = Host.Services.Sessions.PoolFor(profile, provider, "postgres");
            var held = new List<Querithm.Core.Sessions.PhysicalConnection>();
            for (var i = 0; i < Host.Services.Sessions.MaxPerProfile; i++) held.Add(await pool.BorrowAsync("postgres", null, CancellationToken.None));
            var sw = Stopwatch.StartNew();
            await using (var control = await Host.Services.Sessions.OpenDedicatedAsync(profile, provider, "postgres", new CancellationTokenSource(5_000).Token, ConnectionRegistry.Kinds.Control))
                Assert.Equal(System.Data.ConnectionState.Open, control.State);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < ActivityMonitor.ControlConnectTimeoutMs, $"{sw.ElapsedMilliseconds} ms");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "querithm-b0-control-ssh.txt"), $"{DateTime.UtcNow:O} control connection via SSH with an exhausted pool: {sw.ElapsedMilliseconds} ms\n");
            foreach (var c in held) pool.Return(c);
            // L6: the tunnel drops; the monitor reconnects (the tunnel is reopened by the existing tunnel logic).
            Host.Services.Sessions.Tunnels!.Close(saved.Id);
            await WaitSnapshot(m.MonitorId, x => x.Seq > s.Seq && x.Connection == MonitorConnectionState.Reconnecting, 30_000);
            await WaitSnapshot(m.MonitorId, x => x.Connection == MonitorConnectionState.Connected && x.Sessions != null, 60_000);
        }
    }
}

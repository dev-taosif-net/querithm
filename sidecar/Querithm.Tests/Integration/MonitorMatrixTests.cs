using System.Data.Common;
using Querithm.Contract;
using Querithm.Core.Monitoring;

namespace Querithm.Tests.Integration;

/// <summary>
/// Matrix rows typed I that the first integration pass left without a test: K5, S2, S7, S10, S14, B9, D7, X7, P5, O10.
/// Enable with QUERITHM_IT=1 (same fixture and collections as <see cref="MonitorIntegrationTests"/>).
/// </summary>
public class MonitorMatrixTests
{
    public abstract class Rows(Engine engine) : MonitorIntegrationTests.Fixture(engine)
    {
        protected string T2 => Ms ? "dbo.monitor_it2" : "monitor_it2";
        protected string Begin => Ms ? "BEGIN TRAN;" : "BEGIN;";

        protected async Task EnsureTable2Async()
        {
            await using var c = await OpenForeignAsync("setup2");
            await ExecAsync(c, Ms
                ? "IF OBJECT_ID('dbo.monitor_it2') IS NULL CREATE TABLE dbo.monitor_it2(id int PRIMARY KEY, v int); IF NOT EXISTS (SELECT 1 FROM dbo.monitor_it2) INSERT dbo.monitor_it2 VALUES (1,1),(2,2),(3,3),(4,4);"
                : "CREATE TABLE IF NOT EXISTS monitor_it2(id int PRIMARY KEY, v int); INSERT INTO monitor_it2 VALUES (1,1),(2,2),(3,3),(4,4) ON CONFLICT DO NOTHING;");
            Foreign.Remove(c);
        }

        async Task<long> CountAppAsync(string app)
        {
            await using var admin = await OpenForeignAsync("count-admin");
            Foreign.Remove(admin);
            return Convert.ToInt64(await ScalarAsync(admin, Ms
                ? $"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = N'{app}'"
                : $"SELECT count(*) FROM pg_stat_activity WHERE application_name = '{app}'"));
        }

        [IntegrationFact]
        public async Task K4_K5_a_session_that_ends_after_the_snapshot_is_reported_gone_and_new_sessions_on_its_id_are_untouched()
        {
            var c = await OpenForeignAsync("k5-ends");
            var id = Spid(c);
            // A 30 s interval: the first tick sees the session and no second tick runs before the kill.
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = Profile.Id, IntervalMs = 30_000, Panes = [MonitorPane.Sessions] });
            bool Mine(ActivityRow r) => r.Key.Id == id && r.Program == "k5-ends";
            var key = (await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(Mine) == true)).Sessions!.Single(Mine).Key;
            Foreign.Remove(c);
            await c.DisposeAsync();
            for (var i = 0; i < 50 && await CountAppAsync("k5-ends") > 0; i++) await Task.Delay(100);
            var others = new List<DbConnection>();
            for (var i = 0; i < 5; i++) others.Add(await OpenForeignAsync($"k5-new{i}")); // SQL Server hands out the freed id again
            var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.True(r.Outcome == KillOutcome.Gone, $"{r.Outcome} {r.Code}: {r.Message}");
            foreach (var o in others) Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(o, "SELECT 1")));
        }

        [IntegrationFact]
        public async Task S10_K11_background_and_system_sessions_are_marked_and_never_killable()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Count(r => !r.IsUserSession) >= 2);
            var system = s.Sessions!.Where(r => !r.IsUserSession).ToList();
            if (!Ms) Assert.Contains(system, r => r.BackendType is "checkpointer" or "walwriter" or "background writer" or "autovacuum launcher");
            Assert.All(system, r => Assert.Null(r.QuerithmKind));
            foreach (var row in system.Take(3))
            {
                var k = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = row.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
                Assert.Equal("systemSession", k.Code);
            }
        }

        [IntegrationFact]
        public async Task B9_a_deadlock_resolved_between_ticks_leaves_no_stale_node_in_the_tree()
        {
            await EnsureTable2Async();
            var m = await StartAsync(MonitorPane.Sessions);
            var a = await OpenForeignAsync("b9-a");
            var b = await OpenForeignAsync("b9-b");
            await ExecAsync(a, $"{Begin} UPDATE {T2} SET v = v WHERE id = 3;");
            await ExecAsync(b, $"{Begin} UPDATE {T2} SET v = v WHERE id = 4;");
            var ta = Busy[a] = Task.Run(async () => { try { await ExecAsync(a, $"UPDATE {T2} SET v = v WHERE id = 4"); } catch { /* victim */ } });
            await Task.Delay(300);
            var tb = Busy[b] = Task.Run(async () => { try { await ExecAsync(b, $"UPDATE {T2} SET v = v WHERE id = 3"); } catch { /* victim */ } });
            await Task.WhenAll(ta, tb).WaitAsync(TimeSpan.FromSeconds(30)); // the detector picked a victim
            long A = Spid(a), B = Spid(b);
            var after = await WaitSnapshot(m.MonitorId, x => x.Sessions != null && x.Blocking != null);
            await WaitSnapshot(m.MonitorId, x => x.Seq > after.Seq + 1);
            var s = Host.Notifier.Of<MonitorSnapshot>().Last(x => x.MonitorId == m.MonitorId && x.Sessions != null);
            Assert.DoesNotContain(s.Sessions!, r => r.BlockedBy.Contains(A) || r.BlockedBy.Contains(B));
            Assert.DoesNotContain(s.Blocking!.HeadBlockers, h => h == A || h == B);
            Assert.DoesNotContain(s.Blocking.Cycles, c => c.Contains(A) || c.Contains(B));
            // SQL Server already rolled the victim back (1205); PostgreSQL leaves both blocks open.
            var end = Ms ? "IF @@TRANCOUNT > 0 ROLLBACK" : "ROLLBACK";
            await ExecAsync(a, end);
            await ExecAsync(b, end);
        }

        [IntegrationFact]
        public async Task T1_clearing_the_statement_statistics_is_reported_as_a_reset_promptly()
        {
            var m = await StartAsync(MonitorPane.TopQueries, MonitorPane.Sessions);
            await using var c = await OpenForeignAsync("t1-load");
            for (var i = 0; i < 30; i++) await ScalarAsync(c, Ms ? $"SELECT COUNT(*) FROM sys.objects WHERE object_id > {i} AND name <> N't1_{i}'" : $"SELECT count(*) FROM pg_class WHERE oid > {i} AND relname <> 't1_{i}'");
            var ok = await WaitSnapshot(m.MonitorId, x => x.Status.Any(st => st.Pane == MonitorPane.TopQueries && st.State == PaneState.Ok), 30_000);
            await using var admin = await OpenForeignAsync("t1-admin");
            // A foreign session clears them (the monitor never does, X15).
            await ExecAsync(admin, Ms ? "DBCC FREEPROCCACHE WITH NO_INFOMSGS;" : "SELECT pg_stat_statements_reset();");
            var reset = await WaitSnapshot(m.MonitorId, x => x.Seq > ok.Seq && x.Status.Any(st => st.Pane == MonitorPane.TopQueries && st.State == PaneState.ResetDetected), 30_000);
            Assert.DoesNotContain(Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.TopQueries != null).SelectMany(x => x.TopQueries!), q => q.ExecDelta < 0);
            _ = reset;
        }

        [IntegrationFact]
        public async Task C2_C3_a_kill_while_a_job_holds_the_monitor_connection_and_every_pool_slot_is_blocked_runs_on_the_control_connection()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var holder = await OpenForeignAsync("c3-holder");
            await ExecAsync(holder, Ms ? $"BEGIN TRAN; UPDATE {Table} SET v = v WHERE id = 2;" : $"BEGIN; UPDATE {Table} SET v = v WHERE id = 2;");
            // Nine tab sessions on the profile run an UPDATE the holder blocks: eight fill the pool, the ninth queues (C3).
            var tabs = new List<string>();
            for (var i = 0; i < 9; i++) tabs.Add((await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = Profile.Id, Secret = Ms ? TestHost.MssqlPassword : TestHost.PgPassword })).SessionId);
            var runs = new List<string>();
            foreach (var t in tabs) runs.Add((await Host.Rpc.QueryExecute(new QueryExecuteParams { SessionId = t, Sql = $"UPDATE {Table} SET v = v WHERE id = 2" })).ExecutionId!);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!Host.Notifier.Of<QueryQueuedEvent>().Any(q => runs.Contains(q.ExecutionId)) && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.Contains(Host.Notifier.Of<QueryQueuedEvent>(), q => runs.Contains(q.ExecutionId));
            bool Mine(ActivityRow r) => r.Key.Id == Spid(holder) && r.Program == "c3-holder";
            var key = (await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(Mine) == true)).Sessions!.Single(Mine).Key;
            // A job holds the monitor connection (C2): the kill cannot claim it and opens the control connection.
            var monitor = Host.Services.Monitors.Get(m.MonitorId);
            var busy = monitor.InteractiveAsync("hold", async (_, conn, ct) =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = Ms ? "WAITFOR DELAY '00:00:04'" : "SELECT pg_sleep(4)";
                await cmd.ExecuteNonQueryAsync(ct);
                return 0;
            }, 10_000);
            await Task.Delay(500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            sw.Stop();
            Assert.True(r.Outcome == KillOutcome.Done, $"{r.Outcome} {r.Code}: {r.Message}");
            Assert.Equal("control", r.Path);
            // C2: within 500 ms. Opening the control connection is part of it: run the fixture by IP (QUERITHM_IT_*_HOST=127.0.0.1)
            // on machines where a localhost connect is slow (measured 2.3 s for PostgreSQL on the Windows dev box, §16).
            Assert.True(sw.ElapsedMilliseconds < 500, $"kill took {sw.ElapsedMilliseconds} ms while a job ran");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "querithm-c2.txt"), $"{DateTime.UtcNow:O} {EngineKind} kill via control while a job ran: {sw.ElapsedMilliseconds} ms{Environment.NewLine}");
            // The blocked tabs, the queued one included, proceed once the blocker is gone.
            foreach (var e in runs)
            {
                var d = await Host.Notifier.WaitDone(e, 30_000);
                Assert.True(d.State == ExecState.Done, $"{d.State} {d.Error?.Code}: {d.Error?.Message}");
            }
            await busy;
        }

        [IntegrationFact]
        public async Task O2_a_tab_whose_connection_was_killed_is_known_by_its_new_identity_and_the_old_one_maps_to_nothing()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var first = (await Host.RunAsync(SessionId, "SELECT 1")).Done;
            Assert.True(first.State == ExecState.Done, $"{first.State} {first.Error?.Code}: {first.Error?.Message}");
            var spid1 = (await Host.Rpc.SessionStateGet(new SessionRef { SessionId = SessionId })).Spid!.Value;
            var old = (await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == spid1 && r.QuerithmKind == ConnectionRegistry.Kinds.Pool) == true))
                .Sessions!.Single(r => r.Key.Id == spid1).Key;
            await using (var admin = await OpenForeignAsync("o2-admin"))
                await ExecAsync(admin, Ms ? $"KILL {spid1}" : $"SELECT pg_terminate_backend({spid1})");
            // The next run may fail on the dead connection (it is discarded); a later one opens a new connection.
            QueryDoneEvent? done = null;
            for (var i = 0; i < 3 && done?.State != ExecState.Done; i++) done = (await Host.RunAsync(SessionId, "SELECT 1")).Done;
            Assert.Equal(ExecState.Done, done!.State);
            var spid2 = (await Host.Rpc.SessionStateGet(new SessionRef { SessionId = SessionId })).Spid!.Value;
            var fresh = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == spid2 && r.QuerithmKind == ConnectionRegistry.Kinds.Pool && r.Key != old) == true);
            Assert.NotNull(fresh);
            Assert.Null(Host.Services.Sessions.Registry.Find(old)); // the old identity maps to nothing
        }

        [IntegrationFact]
        public async Task S14_revoking_the_monitor_logins_server_state_permission_degrades_the_next_tick_without_a_crash()
        {
            await using var admin = await OpenForeignAsync("s14-admin");
            if (Ms)
                await ExecAsync(admin, "USE master; IF SUSER_ID('it_s14') IS NULL CREATE LOGIN it_s14 WITH PASSWORD = 'It_S14_2026!', CHECK_POLICY = OFF; GRANT VIEW SERVER STATE TO it_s14; USE MonitorIT; IF USER_ID('it_s14') IS NULL CREATE USER it_s14 FOR LOGIN it_s14;");
            else
                await ExecAsync(admin, "DO $$BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'it_s14') THEN CREATE ROLE it_s14 LOGIN PASSWORD 'It_S14_2026!'; END IF; END$$; GRANT pg_read_all_stats TO it_s14;");
            var p = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "s14", Username = "it_s14" });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "It_S14_2026!" });
            var other = await OpenForeignAsync("s14-other");
            try
            {
                var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = p.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions, MonitorPane.Waits] });
                bool Other(ActivityRow r) => r.Key.Id == Spid(other) && r.Program == "s14-other";
                var before = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => Other(r) && r.Key.Instance != "") == true);
                await ExecAsync(admin, Ms ? "USE master; REVOKE VIEW SERVER STATE FROM it_s14; USE MonitorIT;" : "REVOKE pg_read_all_stats FROM it_s14;");
                if (Ms)
                {
                    // The probe said "full"; the next queries fail 300 and the panes say why, the loop keeps running.
                    var s = await WaitSnapshot(m.MonitorId, x => x.Seq > before.Seq && x.Status.Any(st => st.Pane == MonitorPane.Sessions && st.State is PaneState.Error or PaneState.Degraded), 20_000);
                    Assert.Contains("VIEW SERVER STATE", s.Status.Single(st => st.Pane == MonitorPane.Sessions).Reason);
                    var later = await WaitSnapshot(m.MonitorId, x => x.Seq > s.Seq + 1, 20_000);
                    Assert.Equal(MonitorConnectionState.Connected, later.Connection);
                }
                else
                {
                    // Other roles' rows lose their identity on the next tick: shown, but not killable (K29).
                    var s = await WaitSnapshot(m.MonitorId, x => x.Seq > before.Seq && x.Sessions?.Any(r => r.Key.Id == Spid(other) && r.IdentityHidden != null) == true, 20_000);
                    Assert.Equal("", s.Sessions!.Single(r => r.Key.Id == Spid(other)).Key.Instance);
                }
            }
            finally
            {
                if (Ms) await ExecAsync(admin, "USE master; GRANT VIEW SERVER STATE TO it_s14; USE MonitorIT;");
                else await ExecAsync(admin, "GRANT pg_read_all_stats TO it_s14;");
            }
        }

        [IntegrationFact]
        public async Task O10_a_login_that_cannot_see_other_sessions_gets_no_banner_but_the_reason()
        {
            await EnsureTable2Async();
            await using var admin = await OpenForeignAsync("o10-admin");
            if (Ms)
                await ExecAsync(admin, "USE master; IF SUSER_ID('it_o10') IS NULL CREATE LOGIN it_o10 WITH PASSWORD = 'It_O10_2026!', CHECK_POLICY = OFF; USE MonitorIT; IF USER_ID('it_o10') IS NULL CREATE USER it_o10 FOR LOGIN it_o10; GRANT SELECT, UPDATE ON dbo.monitor_it2 TO it_o10;");
            else
                await ExecAsync(admin, "DO $$BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'it_o10') THEN CREATE ROLE it_o10 LOGIN PASSWORD 'It_O10_2026!'; END IF; END$$; REVOKE pg_read_all_stats FROM it_o10; GRANT SELECT, UPDATE ON monitor_it2 TO it_o10;");
            var p = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "o10", Username = "it_o10" });
            var tab = await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "It_O10_2026!" });
            await Host.Rpc.TxBegin(new SessionRef { SessionId = tab.SessionId });
            var (done, _, _) = await Host.RunAsync(tab.SessionId, $"UPDATE {T2} SET v = v + 1 WHERE id = 2");
            Assert.Equal(ExecState.Done, done.State);
            var victim = await OpenForeignAsync("o10-victim");
            _ = Blocked(victim, $"UPDATE {T2} SET v = v + 1 WHERE id = 2");
            await Task.Delay(6_000); // blocked longer than the 5 s threshold
            await Host.Services.Banner.RunCycleAsync(CancellationToken.None);
            var events = Host.Notifier.Of<SessionBlockingEvent>().Where(e => e.SessionId == tab.SessionId).ToList();
            Assert.DoesNotContain(events, e => e.BlockedCount > 0);
            Assert.Contains(events, e => e.Unavailable != null);
            await Host.Rpc.TxRollback(new SessionRef { SessionId = tab.SessionId });
        }
    }

    [Collection("monitor-mssql")]
    public class SqlServer() : Rows(Engine.Mssql)
    {
        [IntegrationFact]
        public async Task S2_more_than_1000_sessions_show_the_first_1000_by_priority_and_say_truncated()
        {
            await EnsureTable2Async();
            var holder = await OpenForeignAsync("s2-holder");
            await ExecAsync(holder, $"BEGIN TRAN; UPDATE {T2} SET v = v WHERE id = 1;");
            var victim = await OpenForeignAsync("s2-victim");
            // No client timeout: opening 1,000 connections plus the first 1,000-session tick can outlast the driver's 30 s
            // default, which cancelled the UPDATE — the victim then stayed listed (low session id) as idle, with no blocker.
            var victimDone = Blocked(victim, $"UPDATE {T2} SET v = v WHERE id = 1");
            await WaitBlockedAsync(victim, holder); // blocked on the server before the load starts
            var opens = Enumerable.Range(0, 1_000).Select(i => OpenForeignAsync($"s2-idle{i % 10}"));
            foreach (var chunk in opens.Chunk(50)) await Task.WhenAll(chunk);
            Assert.False(victimDone.IsCompleted, "the victim's UPDATE ended while the idle sessions were opened");
            long V = Spid(victim), H = Spid(holder);
            var m = await StartAsync(MonitorPane.Sessions);
            var s = await WaitSnapshot(m.MonitorId, x => x.Status.Any(st => st.Pane == MonitorPane.Sessions && st.State == PaneState.Truncated)
                && x.Sessions?.Any(r => r.Key.Id == V && r.BlockedBy.Contains(H)) == true, 60_000);
            var st = s.Status.Single(x => x.Pane == MonitorPane.Sessions);
            Assert.True(st.Total > 1_000, $"total {st.Total}");
            Assert.Equal(1_000, s.Sessions!.Length);
            // Priority: the blocked session and its blocker are kept, whatever their ids.
            Assert.Contains(s.Sessions, r => r.Key.Id == V && r.BlockedBy.Contains(H));
            Assert.Contains(s.Sessions, r => r.Key.Id == H);
            Assert.Contains(H, s.Blocking!.HeadBlockers);
            // The victim and its blocker sort ahead of every idle session (blocked = 0, blocker = 1, idle = 3) — they are not
            // merely inside the cap because their ids are low.
            var order = s.Sessions!.Select((r, i) => (r, i)).ToList();
            var firstIdle = order.First(x => x.r.Program?.StartsWith("s2-idle", StringComparison.Ordinal) == true).i;
            Assert.True(order.Single(x => x.r.Key.Id == V).i < firstIdle, $"victim at {order.Single(x => x.r.Key.Id == V).i}, first idle at {firstIdle}");
            Assert.True(order.Single(x => x.r.Key.Id == H).i < firstIdle, $"holder at {order.Single(x => x.r.Key.Id == H).i}, first idle at {firstIdle}");
            await ExecAsync(holder, "ROLLBACK");
            await victimDone.WaitAsync(TimeSpan.FromSeconds(15));
        }

        [IntegrationFact]
        public async Task S7_B5_a_parallel_query_is_one_row_with_its_longest_task_wait_and_never_blocks_itself()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var c = await OpenForeignAsync("s7-parallel");
            await ExecAsync(c, MonitorIntegrationTests.EnsureNumbersSql);
            _ = Blocked(c, MonitorIntegrationTests.LongParallelSql); // runs until killed: no 30 s client timeout
            var id = Spid(c);
            await using var admin = await OpenForeignAsync("s7-admin");
            var tasks = 0;
            for (var i = 0; i < 50 && tasks < 2; i++)
            {
                await Task.Delay(200);
                tasks = Convert.ToInt32(await ScalarAsync(admin, $"SELECT COUNT(*) FROM sys.dm_os_tasks WHERE session_id = {id}"));
            }
            Assert.True(tasks >= 2, $"the query did not go parallel ({tasks} task)");
            // A tick can catch every task running (no wait is then correct): wait for one that shows the longest task wait.
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == id && r.ElapsedMs != null && r.WaitType != null) == true);
            Assert.All(Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.Sessions != null),
                x => Assert.True(x.Sessions!.Count(r => r.Key.Id == id) <= 1, $"seq {x.Seq}: the parallel query as several rows")); // S7, every tick
            var row = s.Sessions!.Single(r => r.Key.Id == id);
            Assert.DoesNotContain(id, row.BlockedBy); // B5: exchange waits name the session itself; never an edge
            Assert.DoesNotContain(id, s.Blocking?.HeadBlockers ?? []);
            Assert.True(row.Dop is null or >= 2, $"dop {row.Dop}");
            await ExecAsync(admin, $"KILL {id}");
        }

        [IntegrationFact]
        public async Task K17_a_kill_the_server_refuses_after_the_probe_is_reported_as_failed_never_done()
        {
            // Regression: with user errors reported as messages, KILL's error 6102 (permission) came back as "terminated".
            await using var admin = await OpenForeignAsync("k17b-admin");
            await ExecAsync(admin, "USE master; IF SUSER_ID('it_k17b') IS NULL CREATE LOGIN it_k17b WITH PASSWORD = 'It_K17b_2026!', CHECK_POLICY = OFF; GRANT VIEW SERVER STATE TO it_k17b; GRANT ALTER ANY CONNECTION TO it_k17b; USE MonitorIT; IF USER_ID('it_k17b') IS NULL CREATE USER it_k17b FOR LOGIN it_k17b;");
            var p = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "k17b", Username = "it_k17b" });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "It_K17b_2026!" });
            var victim = await OpenForeignAsync("k17b-victim");
            try
            {
                var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = p.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions] });
                bool Mine(ActivityRow r) => r.Key.Id == Spid(victim) && r.Program == "k17b-victim";
                var key = (await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(Mine) == true)).Sessions!.Single(Mine).Key;
                await ExecAsync(admin, "USE master; REVOKE ALTER ANY CONNECTION FROM it_k17b; USE MonitorIT;");
                var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
                Assert.True(r.Outcome == KillOutcome.Failed && r.Code == "permission", $"{r.Outcome} {r.Code}: {r.Message}");
                Assert.Equal(1, Convert.ToInt32(await ScalarAsync(victim, "SELECT 1"))); // untouched
            }
            finally { await ExecAsync(admin, "USE master; GRANT ALTER ANY CONNECTION TO it_k17b; USE MonitorIT;"); }
        }

        [IntegrationFact]
        public async Task B12_a_parallel_scan_blocked_by_two_sessions_lists_both_blockers_once()
        {
            await using var setup = await OpenForeignAsync("b12-setup");
            await ExecAsync(setup, MonitorIntegrationTests.EnsureNumbersSql);
            await ExecAsync(setup, "IF OBJECT_ID('dbo.monitor_b12') IS NULL BEGIN CREATE TABLE dbo.monitor_b12(id int PRIMARY KEY, pad char(800) NOT NULL DEFAULT 'x'); INSERT dbo.monitor_b12(id) SELECT n FROM dbo.monitor_nums WHERE n <= 40000; END");
            var m = await StartAsync(MonitorPane.Sessions);
            var h1 = await OpenForeignAsync("b12-h1");
            var h2 = await OpenForeignAsync("b12-h2");
            await ExecAsync(h1, "BEGIN TRAN; UPDATE dbo.monitor_b12 SET pad = 'y' WHERE id = 50;");
            await ExecAsync(h2, "BEGIN TRAN; UPDATE dbo.monitor_b12 SET pad = 'y' WHERE id = 39950;");
            var scan = await OpenForeignAsync("b12-scan");
            _ = Blocked(scan, "SELECT COUNT(*) FROM dbo.monitor_b12 WITH (READCOMMITTEDLOCK) WHERE pad <> 'z' OPTION (MAXDOP 4, USE HINT('ENABLE_PARALLEL_PLAN_PREFERENCE'))");
            long S = Spid(scan), A = Spid(h1), B = Spid(h2);
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == S && r.BlockedBy.Contains(A) && r.BlockedBy.Contains(B)) == true, 20_000);
            var row = s.Sessions!.Single(r => r.Key.Id == S);
            Assert.Equal(row.BlockedBy.Distinct().Count(), row.BlockedBy.Length);
            Assert.Equal(1, s.Sessions!.Count(r => r.Key.Id == S));
            var g = Querithm.Core.Monitoring.BlockingGraph.Build(s.Sessions!);
            Assert.Contains(A, g.HeadBlockers);
            Assert.Contains(B, g.HeadBlockers);
            await ExecAsync(h1, "ROLLBACK");
            await ExecAsync(h2, "ROLLBACK");
        }

        [IntegrationFact]
        public async Task T9_a_statement_running_across_a_top_queries_poll_appears_in_the_next_incremental_read_with_one_execution()
        {
            await using var c = await OpenForeignAsync("t9-long");
            await ExecAsync(c, MonitorIntegrationTests.EnsureNumbersSql);
            // Calibrate a statement of about 8 s on this machine (the top-queries job runs every 5 s at a 1 s interval).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ScalarAsync(c, "SELECT COUNT_BIG(*) FROM dbo.monitor_nums a CROSS JOIN dbo.monitor_nums b WHERE a.n <= 200 AND (a.n * 7 + b.n) % 1000003 = 5 OPTION (MAXDOP 4)");
            var lim = Math.Clamp((int)(200 * 8_000 / Math.Max(1, sw.ElapsedMilliseconds)), 200, 100_000);
            var m = await StartAsync(MonitorPane.TopQueries, MonitorPane.Sessions);
            await WaitSnapshot(m.MonitorId, x => x.Status.Any(st => st.Pane == MonitorPane.TopQueries && st.State == PaneState.Ok), 30_000);
            var tag = $"t9_{Guid.NewGuid():N}"[..18];
            var id = Spid(c);
            sw.Restart();
            var run = ScalarAsync(c, $"DECLARE @lim int = {lim}; SELECT COUNT_BIG(*) AS {tag} FROM dbo.monitor_nums a CROSS JOIN dbo.monitor_nums b WHERE a.n <= @lim AND (a.n * 7 + b.n) % 1000003 = 5 OPTION (MAXDOP 4)");
            var refreshesWhileRunning = 0;
            while (!run.IsCompleted)
            {
                await Task.Delay(200);
                refreshesWhileRunning = Host.Notifier.Of<MonitorSnapshot>().Count(x => x.MonitorId == m.MonitorId && x.TopQueries != null && x.Sessions?.Any(r => r.Key.Id == id && r.ElapsedMs != null) == true);
            }
            await run;
            var took = sw.ElapsedMilliseconds;
            Assert.True(refreshesWhileRunning >= 1, $"no top-queries refresh while the statement ran ({took} ms): calibrate longer");
            List<TopQueryRow> Rows() => Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.TopQueries != null)
                .SelectMany(x => x.TopQueries!.Where(q => q.TextId != null && (Host.Rpc.MonitorTexts(new MonitorTextsParams { MonitorId = m.MonitorId, Ids = [q.TextId] }).Result.Texts.GetValueOrDefault(q.TextId) ?? "").Contains(tag))).ToList();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Rows().Sum(q => q.ExecDelta) < 1 && DateTime.UtcNow < deadline) await Task.Delay(500);
            Assert.Equal(1, Rows().Sum(q => q.ExecDelta));
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "querithm-t9.txt"), $"{DateTime.UtcNow:O} statement ran {took} ms across {refreshesWhileRunning} refresh(es); exec delta 1{Environment.NewLine}");
        }

        [IntegrationFact]
        public async Task L6_through_an_ssh_tunnel_the_sql_server_monitor_reconnects_after_a_tunnel_drop()
        {
            var ssh = new SshSettings { Host = "127.0.0.1", Port = 22220, Username = "querithm", Auth = SshAuthKind.Password };
            var viaSsh = Profile with { Id = "", Name = "mssql via ssh", Host = "mssql", Port = 1433, TlsMode = TlsMode.Mandatory, TrustCert = true, Ssh = ssh };
            var test = await Host.Rpc.ProfileTest(new ProfileTestParams { Profile = viaSsh, Secret = TestHost.MssqlPassword, SshSecret = FixtureSecrets.Password("ssh") });
            if (test.UntrustedHostKey is { } hk) await Host.Rpc.SshTrustHostKey(hk);
            var saved = await Host.Rpc.ProfileSave(viaSsh);
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = TestHost.MssqlPassword, SshSecret = FixtureSecrets.Password("ssh") });
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = saved.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions] });
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.QuerithmKind == Querithm.Core.Monitoring.ConnectionRegistry.Kinds.Monitor) == true, 30_000);
            Host.Services.Sessions.Tunnels!.Close(saved.Id);
            await WaitSnapshot(m.MonitorId, x => x.Seq > s.Seq && x.Connection == MonitorConnectionState.Reconnecting, 30_000);
            await WaitSnapshot(m.MonitorId, x => x.Connection == MonitorConnectionState.Connected && x.Sessions != null, 60_000);
        }

        [IntegrationFact("QUERITHM_IT_DISRUPTIVE")]
        public async Task L5_a_sql_server_restart_reconnects_and_reports_the_counter_reset()
        {
            // Restarts the shared SQL Server container: opt-in, run alone (QUERITHM_IT_DISRUPTIVE=1).
            var m = await StartAsync(MonitorPane.Sessions, MonitorPane.Waits, MonitorPane.Metrics);
            var before = await WaitSnapshot(m.MonitorId, x => x.Metrics?.Values.Any(v => v.Key == "batchRequests" && v.Value != null) == true, 20_000);
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", "restart querithm-fixture-mssql-1") { RedirectStandardOutput = true, RedirectStandardError = true })!)
                Assert.True(p.WaitForExit(120_000) && p.ExitCode == 0);
            await WaitSnapshot(m.MonitorId, x => x.Seq > before.Seq && x.Connection == MonitorConnectionState.Reconnecting, 60_000);
            var back = await WaitSnapshot(m.MonitorId, x => x.Connection == MonitorConnectionState.Connected && x.Metrics != null, 120_000);
            Assert.NotNull(back);
            var all = Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId).ToList();
            Assert.DoesNotContain(all.SelectMany(x => x.Metrics?.Values ?? []), v => v.Value < 0);
            Assert.DoesNotContain(all.SelectMany(x => x.Waits ?? []), w => w.WaitMsDelta < 0);
        }

        [IntegrationFact]
        public async Task P5_a_terminated_session_rolling_back_says_so_in_the_preview_and_a_second_kill_is_refused()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var writer = await OpenForeignAsync("p5-writer");
            await ExecAsync(writer, "IF OBJECT_ID('dbo.monitor_big') IS NULL CREATE TABLE dbo.monitor_big(id int IDENTITY PRIMARY KEY, pad char(4000) NOT NULL DEFAULT 'x');");
            await ExecAsync(writer, "BEGIN TRAN; INSERT dbo.monitor_big(pad) SELECT TOP (60000) 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b;");
            var id = Spid(writer);
            bool Mine(ActivityRow r) => r.Key.Id == id && r.Program == "p5-writer";
            var key = (await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(Mine) == true)).Sessions!.Single(Mine).Key;
            var done = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            Assert.Equal(KillOutcome.Done, done.Outcome);
            var preview = await Host.Rpc.MonitorKillPreview(new MonitorSessionParams { MonitorId = m.MonitorId, Key = key });
            Assert.True(preview.RollingBack, $"rolling back {preview.RollingBack}, ended {preview.SessionEnded}");
            var again = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            Assert.Equal("rollingBack", again.Code);
            await WaitSnapshot(m.MonitorId, x => x.Sessions?.All(r => r.Key != key) == true, 120_000);
            await using var cleanup = await OpenForeignAsync("p5-cleanup");
            await ExecAsync(cleanup, "TRUNCATE TABLE dbo.monitor_big");
        }

        [IntegrationFact]
        public async Task X7_a_deadlock_in_a_database_the_monitor_login_cannot_open_still_shows_its_graph()
        {
            await EnsureTable2Async();
            await using var admin = await OpenForeignAsync("x7-admin");
            // VIEW SERVER STATE (system_health) but no user in MonitorIT.
            await ExecAsync(admin, "USE master; IF SUSER_ID('it_x7') IS NULL CREATE LOGIN it_x7 WITH PASSWORD = 'It_X7_2026!', CHECK_POLICY = OFF; GRANT VIEW SERVER STATE TO it_x7; USE MonitorIT; IF USER_ID('it_x7') IS NOT NULL DROP USER it_x7;");
            var p = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "x7", Username = "it_x7", DatabaseDefault = "master" });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "It_X7_2026!" });
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = p.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions, MonitorPane.Deadlocks] });
            var a = await OpenForeignAsync("x7-a");
            var b = await OpenForeignAsync("x7-b");
            await ExecAsync(a, "BEGIN TRAN; UPDATE dbo.monitor_it2 SET v = v WHERE id = 1;");
            await ExecAsync(b, "BEGIN TRAN; UPDATE dbo.monitor_it2 SET v = v WHERE id = 2;");
            var ta = Task.Run(async () => { try { await ExecAsync(a, "UPDATE dbo.monitor_it2 SET v = v WHERE id = 2; COMMIT;"); } catch { } });
            await Task.Delay(300);
            var tb = Task.Run(async () => { try { await ExecAsync(b, "UPDATE dbo.monitor_it2 SET v = v WHERE id = 1; COMMIT;"); } catch { } });
            await Task.WhenAll(ta, tb).WaitAsync(TimeSpan.FromSeconds(30));
            DeadlockEvent? ev = null;
            for (var i = 0; i < 30 && ev == null; i++)
            {
                var r = await Host.Rpc.MonitorDeadlocks(new DeadlocksParams { MonitorId = m.MonitorId, FromMs = Querithm.Core.Time.NowMs() - 120_000 });
                ev = r.Events.FirstOrDefault(e => e.Processes.Any(x => x.Spid == Spid(a)) && e.Processes.Any(x => x.Spid == Spid(b)));
                if (ev == null) await Task.Delay(1_000);
            }
            Assert.NotNull(ev);
            var key = ev!.Resources.First(x => x.Kind == "keylock");
            // B0 row 8: the server writes the object's name into the deadlock report itself; database access does not matter.
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "querithm-x7.txt"), $"{DateTime.UtcNow:O} keylock object '{key.Object}' database '{key.Database}'{Environment.NewLine}");
            Assert.False(string.IsNullOrEmpty(key.Object));
            Assert.Equal("MonitorIT", key.Database);
        }
    }

    [Collection("monitor-pg")]
    public class Postgres() : Rows(Engine.Pg)
    {
        [IntegrationFact]
        public async Task K17_a_role_without_pg_signal_backend_is_told_what_it_needs_and_nothing_is_sent()
        {
            await using var admin = await OpenForeignAsync("k17pg-admin");
            await ExecAsync(admin, "DO $$BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'it_k17pg') THEN CREATE ROLE it_k17pg LOGIN PASSWORD 'It_K17pg_2026!'; END IF; END$$; GRANT pg_read_all_stats TO it_k17pg; REVOKE pg_signal_backend FROM it_k17pg;");
            var p = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "k17pg", Username = "it_k17pg" });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "It_K17pg_2026!" });
            var victim = await OpenForeignAsync("k17pg-victim");
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = p.Id, IntervalMs = 1_000, Panes = [MonitorPane.Sessions] });
            bool Mine(ActivityRow r) => r.Key.Id == Spid(victim) && r.Program == "k17pg-victim";
            var key = (await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(Mine) == true)).Sessions!.Single(Mine).Key;
            var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.NotEqual(KillOutcome.Done, r.Outcome);
            Assert.Contains("pg_signal_backend", r.Message);
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(victim, "SELECT 1")));
        }

        [IntegrationFact]
        public async Task D2_clearing_postgresql_statistics_and_the_wait_profile_is_a_reset_not_negative_deltas()
        {
            var m = await StartAsync(MonitorPane.Waits, MonitorPane.Metrics, MonitorPane.Sessions);
            var ok = await WaitSnapshot(m.MonitorId, x => x.Status.Any(st => st.Pane == MonitorPane.Waits && st.State == PaneState.Ok) && x.Metrics?.Values.Any(v => v.Key == "tps" && v.Value != null) == true, 30_000);
            await using var admin = await OpenForeignAsync("d2pg-admin");
            await ExecAsync(admin, "SELECT pg_stat_reset(); SELECT pg_wait_sampling_reset_profile();");
            await WaitSnapshot(m.MonitorId, x => x.Seq > ok.Seq && x.Status.Any(st => st.Pane == MonitorPane.Waits && st.State == PaneState.ResetDetected), 20_000);
            var later = Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.Seq > ok.Seq).ToList();
            Assert.DoesNotContain(later.SelectMany(x => x.Waits ?? []), w => w.WaitMsDelta < 0);
            Assert.DoesNotContain(later.SelectMany(x => x.Metrics?.Values ?? []), v => v.Value < 0);
        }

        [IntegrationFact]
        public async Task D7_pg_wait_sampling_lock_wait_deltas_match_a_generated_lock_wait_within_10_percent()
        {
            await EnsureTable2Async();
            await using (var reset = await OpenForeignAsync("d7-reset"))
                await ExecAsync(reset, "SELECT pg_wait_sampling_reset_profile()"); // the fixture's profile grows with churn (B0 row 16)
            var m = await StartAsync(MonitorPane.Waits, MonitorPane.Sessions);
            var first = await WaitSnapshot(m.MonitorId, x => x.WaitCoverage?.Source == "server-profile" && x.Waits != null);
            var holder = await OpenForeignAsync("d7-holder");
            await ExecAsync(holder, "BEGIN; UPDATE monitor_it2 SET v = v WHERE id = 3;");
            var victim = await OpenForeignAsync("d7-victim");
            var seq0 = Host.Notifier.Of<MonitorSnapshot>().Last(x => x.MonitorId == m.MonitorId).Seq;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var waiting = Task.Run(() => ExecAsync(victim, "UPDATE monitor_it2 SET v = v WHERE id = 3"));
            await Task.Delay(3_000);
            await ExecAsync(holder, "ROLLBACK");
            await waiting;
            var waitedMs = sw.ElapsedMilliseconds;
            var end = Host.Notifier.Of<MonitorSnapshot>().Last(x => x.MonitorId == m.MonitorId).Seq;
            await WaitSnapshot(m.MonitorId, x => x.Seq > end + 2);
            var lockMs = Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.Seq > seq0 && x.Waits != null)
                .Sum(x => x.Waits!.Where(w => w.Category == "Lock").Sum(w => w.WaitMsDelta));
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "querithm-d7.txt"), $"{DateTime.UtcNow:O} generated {waitedMs} ms, server-profile Lock {lockMs} ms{Environment.NewLine}");
            Assert.InRange(lockMs, waitedMs * 0.9, waitedMs * 1.1);
            _ = first;
        }
    }
}

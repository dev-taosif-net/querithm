using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using Querithm.Contract;
using Querithm.Core.Monitoring;

namespace Querithm.Tests.Integration;

/// <summary>
/// Activity monitor against the real fixture engines (Phase B, type I in the plan's matrix). Enable with QUERITHM_IT=1.
/// Foreign sessions are raw driver connections (not Querithm's), so they are what a user would see and kill.
/// </summary>
public class MonitorIntegrationTests
{
    /// <summary>A user numbers table: plans over system catalog views stay serial (sys.all_objects cross joins ran DOP 1).</summary>
    public const string EnsureNumbersSql = "IF OBJECT_ID('dbo.monitor_nums') IS NULL SELECT TOP (100000) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n INTO dbo.monitor_nums FROM sys.all_objects a CROSS JOIN sys.all_objects b;";

    /// <summary>Runs in parallel (DOP 4) until killed; a plain COUNT of a catalog cross join finished in seconds on 2022.</summary>
    public const string LongParallelSql = "SELECT COUNT_BIG(*) FROM dbo.monitor_nums a CROSS JOIN dbo.monitor_nums b WHERE (a.n * 7 + b.n) % 1000003 = 5 OPTION (MAXDOP 4)";

    /// <summary>Fixture helpers shared by the monitor integration classes (no tests of its own).</summary>
    public abstract class Fixture(Engine engine) : IAsyncLifetime
    {
        protected readonly TestHost Host = new();
        protected Profile Profile = null!;
        protected string SessionId = "";
        protected readonly List<DbConnection> Foreign = [];
        protected readonly Dictionary<DbConnection, Task> Busy = [];
        protected Engine EngineKind => engine;
        protected bool Ms => engine == Engine.Mssql;
        protected string Table => Ms ? "dbo.monitor_it" : "monitor_it";

        public async Task InitializeAsync()
        {
            if (!TestHost.IntegrationEnabled) return;
            if (Ms) await Host.EnsureDatabaseAsync(Engine.Mssql, "MonitorIT");
            (Profile, SessionId) = Ms ? await Host.OpenMssqlAsync("MonitorIT") : await Host.OpenPgAsync();
            await using var c = await OpenForeignAsync("setup");
            await ExecAsync(c, Ms
                ? "IF OBJECT_ID('dbo.monitor_it') IS NULL CREATE TABLE dbo.monitor_it(id int PRIMARY KEY, v int); IF NOT EXISTS (SELECT 1 FROM dbo.monitor_it) INSERT dbo.monitor_it VALUES (1,1),(2,2),(3,3);"
                : "CREATE TABLE IF NOT EXISTS monitor_it(id int PRIMARY KEY, v int); INSERT INTO monitor_it VALUES (1,1),(2,2),(3,3) ON CONFLICT DO NOTHING;");
        }

        public async Task DisposeAsync()
        {
            // A connection with a command in flight on another thread is not disposed under it (Npgsql would wait forever):
            // lock holders go first, which lets the blocked commands finish, then the rest.
            foreach (var c in Foreign.Where(c => !Busy.ContainsKey(c))) try { await c.DisposeAsync(); } catch { /* killed */ }
            // A finished command's connection may still hold locks another one waits on: close those, then wait again.
            var pending = new Dictionary<DbConnection, Task>(Busy);
            for (var round = 0; round < 30 && pending.Count > 0; round++)
            {
                foreach (var (c, t) in pending.Where(kv => kv.Value.IsCompleted).ToList())
                {
                    try { await c.DisposeAsync(); } catch { /* killed */ }
                    pending.Remove(c);
                }
                if (pending.Count > 0) try { await Task.WhenAny(pending.Values).WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* still waiting */ }
            }
            await Host.DisposeAsync();
        }

        protected async Task<DbConnection> OpenForeignAsync(string app, string? user = null, string? password = null, int pgPort = 54329, string pgDb = "postgres")
        {
            DbConnection c = Ms
                ? new SqlConnection($"Server={TestHost.MssqlHost},14333;Database=MonitorIT;User ID={user ?? "sa"};Password={password ?? TestHost.MssqlPassword};TrustServerCertificate=True;Encrypt=Optional;Pooling=false;Application Name={app};ConnectRetryCount=0")
                : new NpgsqlConnection($"Host=127.0.0.1;Port={pgPort};Database={pgDb};Username={user ?? "postgres"};Password={password ?? TestHost.PgPassword};Pooling=false;Application Name={app}");
            await c.OpenAsync();
            Foreign.Add(c);
            return c;
        }

        /// <param name="timeoutSeconds">0 = no client timeout. The drivers' default (30 s) cancels a statement that is meant to
        /// stay blocked for the whole test: the blocked session then sits idle, still listed, with no blocker (S2 flake).</param>
        protected static async Task ExecAsync(DbConnection c, string sql, int? timeoutSeconds = null)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            if (timeoutSeconds is { } t) cmd.CommandTimeout = t;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Runs a statement that blocks (a victim's UPDATE) in the background with no client timeout; released when its
        /// holder ends (or the fixture closes it). Tracked in <see cref="Busy"/> so disposal releases holders first.
        /// </summary>
        protected Task Blocked(DbConnection c, string sql)
        {
            var t = Task.Run(async () => { try { await ExecAsync(c, sql, timeoutSeconds: 0); } catch { /* released, killed or chosen as victim */ } });
            Busy[c] = t;
            return t;
        }

        /// <summary>Waits until the server shows <paramref name="victim"/> waiting on <paramref name="holder"/> (checked on its own connection).</summary>
        protected async Task WaitBlockedAsync(DbConnection victim, DbConnection holder, int timeoutMs = 20_000)
        {
            await using var admin = Ms
                ? (DbConnection)new SqlConnection($"Server={TestHost.MssqlHost},14333;Database=master;User ID=sa;Password={TestHost.MssqlPassword};TrustServerCertificate=True;Encrypt=Optional;Pooling=false;Application Name=it-blockcheck;ConnectRetryCount=0")
                : new NpgsqlConnection($"Host=127.0.0.1;Port=54329;Database=postgres;Username=postgres;Password={TestHost.PgPassword};Pooling=false;Application Name=it-blockcheck");
            await admin.OpenAsync();
            var sql = Ms
                ? $"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = {Spid(victim)} AND blocking_session_id = {Spid(holder)}"
                : $"SELECT count(*) FROM pg_stat_activity WHERE pid = {Spid(victim)} AND {Spid(holder)} = ANY(pg_blocking_pids(pid))";
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Convert.ToInt64(await ScalarAsync(admin, sql)) == 0)
            {
                if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"session {Spid(victim)} is not blocked by {Spid(holder)} after {timeoutMs} ms");
                await Task.Delay(100);
            }
        }

        protected static async Task<object?> ScalarAsync(DbConnection c, string sql)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return await cmd.ExecuteScalarAsync();
        }

        protected static long Spid(DbConnection c) => c is SqlConnection s ? s.ServerProcessId : ((NpgsqlConnection)c).ProcessID;

        /// <summary>A foreign session holding a row lock in an open transaction, and another one blocked on it.</summary>
        protected async Task<(DbConnection Holder, DbConnection Victim, Task VictimDone)> BlockingPairAsync(int row = 1)
        {
            var holder = await OpenForeignAsync("it-holder");
            await ExecAsync(holder, Ms ? $"BEGIN TRAN; UPDATE {Table} SET v = v + 1 WHERE id = {row};" : $"BEGIN; UPDATE {Table} SET v = v + 1 WHERE id = {row};");
            var victim = await OpenForeignAsync("it-victim");
            var done = Blocked(victim, $"UPDATE {Table} SET v = v + 10 WHERE id = {row}");
            return (holder, victim, done);
        }

        protected async Task<MonitorSnapshot> WaitSnapshot(string monitorId, Func<MonitorSnapshot, bool> until, int timeoutMs = 20_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            // The newest matching snapshot among those since the call (newest first), not only the newest at each poll: a
            // state that lasts one tick (ResetDetected) was missed when this loop resumed late under the parallel suite (D2).
            // Newest first keeps the old answer for lasting states: the first tick showing a state can lack detail later
            // ticks have (B1 failed once on the first tick with the pair: the victim's row had no text yet).
            var from = Math.Max(0, Host.Notifier.Of<MonitorSnapshot>().Count(s => s.MonitorId == monitorId) - 1);
            while (DateTime.UtcNow < deadline)
            {
                var all = Host.Notifier.Of<MonitorSnapshot>().Where(s => s.MonitorId == monitorId).ToList();
                for (var i = all.Count - 1; i >= from; i--)
                    if (until(all[i])) return all[i];
                from = Math.Max(from, all.Count - 1); // the newest stays a candidate: a predicate on other state may turn true
                await Task.Delay(100);
            }
            var latest = Host.Notifier.Of<MonitorSnapshot>().LastOrDefault(s => s.MonitorId == monitorId);
            throw new Xunit.Sdk.XunitException($"No matching snapshot in {timeoutMs} ms. Last: connection {latest?.Connection} {latest?.ConnectionError}; status " +
                string.Join(", ", latest?.Status.Select(x => $"{x.Pane}={x.State}:{x.Reason}") ?? []) + $"; sessions {latest?.Sessions?.Length}"
                + "; metrics " + string.Join(", ", Host.Notifier.Of<MonitorSnapshot>().LastOrDefault(x => x.MonitorId == monitorId && x.Metrics != null)?.Metrics?.Values.Select(v => $"{v.Key}={v.Value}({v.Note})") ?? [])
                + "; top " + string.Join(" | ", Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == monitorId && x.TopQueries != null).Select(x => x.TopQueries!.Length)));
        }

        protected Task<MonitorStartResult> StartAsync(params MonitorPane[] panes) =>
            Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = Profile.Id, IntervalMs = 1_000, Panes = panes.Length > 0 ? panes : null });

    }

    public abstract class Base(Engine engine) : Fixture(engine)
    {
        static bool Blocks(MonitorSnapshot s, long holder, long victim) =>
            s.Sessions?.Any(r => r.Key.Id == victim && r.BlockedBy.Contains(holder)) == true;

        [IntegrationFact]
        public async Task H3_on_a_real_server_history_keeps_the_ticks_of_a_blocking_pair_and_replays_their_snapshot()
        {
            var m = await StartAsync();
            var (holder, victim, _) = await BlockingPairAsync();
            long h = Spid(holder), v = Spid(victim);
            await WaitSnapshot(m.MonitorId, x => Blocks(x, h, v));
            await Task.Delay(3_000); // a few more 1 s ticks while the pair stands
            var hist = await Host.Rpc.MonitorHistory(new HistoryParams { MonitorId = m.MonitorId });
            var withPair = hist.Ticks.Where(t => t.Blocked > 0).ToList();
            Assert.True(withPair.Count >= 2, $"{hist.Ticks.Length} ticks; blocked per tick: {string.Join(",", hist.Ticks.Select(t => t.Blocked))}");
            var tick = await Host.Rpc.MonitorHistory(new HistoryParams { MonitorId = m.MonitorId, Seq = withPair[^1].Seq });
            Assert.Contains(h, tick.Snapshot!.Blocking!.HeadBlockers);
            Assert.Contains(tick.Snapshot.Sessions!, r => r.Key.Id == v && r.BlockedBy.Contains(h));
        }

        [IntegrationFact]
        public async Task B1_LK4_M5_sessions_a_blocking_pair_its_locks_and_its_text_sent_once_appear_in_snapshots()
        {
            var m = await StartAsync();
            var (holder, victim, _) = await BlockingPairAsync();
            long h = Spid(holder), v = Spid(victim);
            var s = await WaitSnapshot(m.MonitorId, x => Blocks(x, h, v));

            Assert.Contains(h, s.Blocking!.HeadBlockers);
            Assert.True(s.Blocking.BlockedCount >= 1);
            var vrow = s.Sessions!.Single(r => r.Key.Id == v);
            Assert.True(vrow.IsUserSession);
            Assert.NotNull(vrow.TextId);
            Assert.Equal(32, vrow.TextId!.Length);
            Assert.NotEmpty(vrow.Key.Instance);
            var self = s.Sessions!.Single(r => r.QuerithmKind == ConnectionRegistry.Kinds.Monitor);
            Assert.Equal("Querithm Monitor", self.Program);
            // The text travelled once (M5): it is in some snapshot's Texts, and monitor.texts returns it.
            var texts = await Host.Rpc.MonitorTexts(new MonitorTextsParams { MonitorId = m.MonitorId, Ids = [vrow.TextId] });
            Assert.Contains("UPDATE", texts.Texts[vrow.TextId], StringComparison.OrdinalIgnoreCase);
            var sentIn = Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.Texts?.ContainsKey(vrow.TextId) == true).Count();
            Assert.Equal(1, sentIn);
            // Locks pane (LK4): the holder's granted and the victim's waiting lock.
            var withLocks = await WaitSnapshot(m.MonitorId, x => x.Locks?.Any(l => l.Key.Id == v && l.Status == "waiting") == true);
            Assert.Contains(withLocks.Locks!, l => l.Key.Id == h && l.Status == "granted");
            Assert.Equal(1, s.ConnectionCount);
            await Host.Rpc.MonitorStop(new MonitorRef { MonitorId = m.MonitorId });
        }

        [IntegrationFact]
        public async Task K13_K28_terminating_the_foreign_blocker_ends_its_connection_and_the_victim_proceeds()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var (holder, victim, victimDone) = await BlockingPairAsync(2);
            long h = Spid(holder), v = Spid(victim);
            var s = await WaitSnapshot(m.MonitorId, x => Blocks(x, h, v));
            var key = s.Sessions!.Single(r => r.Key.Id == h).Key;

            // K13: the same key with the last digit changed is refused (not in the snapshot) and nothing is sent.
            var wrong = key with { Instance = key.Instance[..^1] + (key.Instance[^1] == '9' ? '8' : (char)(key.Instance[^1] + 1)) };
            var refused = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = wrong, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.Equal(KillOutcome.Refused, refused.Outcome);
            Assert.Equal("notInSnapshot", refused.Code);
            Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(holder, "SELECT 1")));

            var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            Assert.Equal(KillOutcome.Done, r.Outcome);
            Assert.Equal("monitor", r.Path);
            await victimDone.WaitAsync(TimeSpan.FromSeconds(15)); // K28: B proceeds
            await Assert.ThrowsAnyAsync<Exception>(() => ScalarAsync(holder, "SELECT 1"));
            await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(row => row.Key == key) == false && x.Sessions?.Length > 0);
        }

        [IntegrationFact]
        public async Task O1_O16_K6_K7_querithm_connections_are_labelled_and_never_killable_from_the_monitor()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            // A tab with an open transaction: its pinned pool connection is leased by the session (O1, O16).
            await Host.Rpc.TxBegin(new SessionRef { SessionId = SessionId });
            var spid = (await Host.Rpc.SessionStateGet(new SessionRef { SessionId = SessionId })).Spid;
            var pinnedKey = Host.Services.Sessions.Registry.KeyOf(Host.Services.Sessions.Get(SessionId).Pinned!.Db)!;
            // A tick that read the server between login and registration shows no badge for that one tick (registration window).
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key == pinnedKey && r.QuerithmKind != null) == true);
            var row = s.Sessions!.Single(r => r.Key == pinnedKey);
            Assert.Equal(ConnectionRegistry.Kinds.Pool, row.QuerithmKind);
            Assert.Equal(SessionId, row.OwnSessionId);
            var r1 = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = row.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            Assert.Equal("querithmConnection", r1.Code);
            await Host.Rpc.TxRollback(new SessionRef { SessionId = SessionId });
            // Back in the pool and idle: still Querithm's, no owner (O15/O16).
            var idle = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key == pinnedKey && r.OwnSessionId == null) == true);
            Assert.Equal("idle", idle.Sessions!.Single(r => r.Key == pinnedKey).QuerithmNote);
            // The monitor's own session (K6).
            var self = idle.Sessions!.Single(r => r.QuerithmKind == ConnectionRegistry.Kinds.Monitor);
            var r2 = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = self.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.Equal(KillOutcome.Refused, r2.Outcome);
            Assert.True(r2.Code is "ownMonitor" or "querithmConnection");
            _ = spid;
        }

        [IntegrationFact]
        public async Task M1_M2_L4_monitor_settings_are_applied_and_the_connection_reconnects_after_being_killed()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.QuerithmKind == ConnectionRegistry.Kinds.Monitor) == true);
            var self = s.Sessions!.Single(r => r.QuerithmKind == ConnectionRegistry.Kinds.Monitor);
            await using var admin = await OpenForeignAsync("it-admin");
            await AssertMonitorSettingsAsync(admin, self.Key.Id, m.MonitorId);

            // L4: killed externally → error state, reconnect with back-off, probe re-run, settings re-applied (M1).
            await ExecAsync(admin, Ms ? $"KILL {self.Key.Id}" : $"SELECT pg_terminate_backend({self.Key.Id})");
            await WaitSnapshot(m.MonitorId, x => x.Connection == MonitorConnectionState.Reconnecting && x.RetryInMs != null);
            var back = await WaitSnapshot(m.MonitorId, x => x.Connection == MonitorConnectionState.Connected
                && x.Sessions?.Any(r => r.QuerithmKind == ConnectionRegistry.Kinds.Monitor && r.Key != self.Key) == true, 30_000);
            var newSelf = back.Sessions!.Single(r => r.QuerithmKind == ConnectionRegistry.Kinds.Monitor);
            await AssertMonitorSettingsAsync(admin, newSelf.Key.Id, m.MonitorId);
            Assert.Contains(Host.Notifier.Of<MonitorSnapshot>(), x => x.MonitorId == m.MonitorId && x.Capabilities != null && x.Seq > s.Seq);
        }

        async Task AssertMonitorSettingsAsync(DbConnection admin, long spid, string monitorId)
        {
            if (Ms)
            {
                await using var cmd = admin.CreateCommand();
                cmd.CommandText = $"SELECT transaction_isolation_level, lock_timeout, deadlock_priority, program_name FROM sys.dm_exec_sessions WHERE session_id = {spid}";
                await using var r = await cmd.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                Assert.Equal(1, Convert.ToInt32(r.GetValue(0)));   // READ UNCOMMITTED
                Assert.Equal(2000, Convert.ToInt32(r.GetValue(1)));
                Assert.Equal(-5, Convert.ToInt32(r.GetValue(2)));  // LOW
                Assert.Equal("Querithm Monitor", r.GetString(3));
                return;
            }
            // M1: PostgreSQL keeps a session's settings private, so they are read on the monitor's own connection, through
            // its scheduler (an interactive job: never beside another monitor query).
            var settings = await Host.Services.Monitors.Get(monitorId).InteractiveAsync("test-settings", async (_, c, ct) =>
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT pg_backend_pid(), current_setting('statement_timeout'), current_setting('lock_timeout'), current_setting('application_name')";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                Assert.True(await r.ReadAsync(ct));
                return (Pid: (long)r.GetInt32(0), Statement: r.GetString(1), Lock: r.GetString(2), App: r.GetString(3));
            });
            Assert.Equal((spid, "5s", "2s", "Querithm Monitor"), settings);
            // M2: between jobs the monitor holds no snapshot. It must be seen idle (between jobs) at least once, and every
            // idle sighting must have a null backend_xmin; a run that never catches it idle fails instead of passing.
            var idleSeen = 0;
            for (var i = 0; i < 100 && idleSeen < 3; i++)
            {
                await using var cmd = admin.CreateCommand();
                cmd.CommandText = $"SELECT application_name, backend_xmin IS NULL, state FROM pg_stat_activity WHERE pid = {spid}";
                await using var r = await cmd.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync(), $"monitor backend {spid} is gone");
                Assert.Equal("Querithm Monitor", r.GetString(0));
                if (!r.IsDBNull(2) && r.GetString(2) == "idle")
                {
                    Assert.True(r.GetBoolean(1), $"the idle monitor backend {spid} holds a snapshot (backend_xmin is set)");
                    idleSeen++;
                }
                await Task.Delay(50);
            }
            Assert.True(idleSeen > 0, $"the monitor backend {spid} was never seen idle in 100 polls over ~5 s");
        }

        [IntegrationFact]
        public async Task S18_K29_a_login_without_server_state_sees_only_its_own_sessions_and_panes_degrade_with_reasons()
        {
            await using var admin = await OpenForeignAsync("it-admin");
            if (Ms)
                await ExecAsync(admin, "IF SUSER_ID('it_low') IS NULL CREATE LOGIN it_low WITH PASSWORD = 'It_Low_2026!', CHECK_POLICY = OFF; IF USER_ID('it_low') IS NULL CREATE USER it_low FOR LOGIN it_low;");
            else
                await ExecAsync(admin, "DO $$BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'it_low') THEN CREATE ROLE it_low LOGIN PASSWORD 'It_Low_2026!'; END IF; END$$; REVOKE pg_read_all_stats FROM it_low;");
            var low = await Host.Rpc.ProfileSave(Profile with { Id = "", Name = "low", Username = "it_low" });
            await Host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = low.Id, Secret = "It_Low_2026!" });
            var (holder, victim, _) = await BlockingPairAsync(3);
            var m = await Host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = low.Id, IntervalMs = 1_000 });
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions != null && x.Capabilities == null
                && Host.Notifier.Of<MonitorSnapshot>().Any(c => c.MonitorId == m.MonitorId && c.Capabilities != null));
            var caps = Host.Notifier.Of<MonitorSnapshot>().First(c => c.MonitorId == m.MonitorId && c.Capabilities != null).Capabilities!;
            Assert.False(caps.AllSessionsVisible);
            Assert.NotNull(caps.SessionsReason);
            if (Ms)
            {
                // S18: own sessions only (the holder and victim are invisible); server-wide panes say what they need.
                Assert.DoesNotContain(s.Sessions!, r => r.Key.Id == Spid(holder));
                var self = Assert.Single(s.Sessions!, r => r.QuerithmKind == ConnectionRegistry.Kinds.Monitor); // its own row is visible
                Assert.Equal("query text requires VIEW SERVER STATE", self.TextNote);
                Assert.Equal(PaneState.Ok, s.Status.Single(x => x.Pane == MonitorPane.Sessions).State);
                var waits = s.Status.Single(x => x.Pane == MonitorPane.Waits);
                Assert.Equal(PaneState.Degraded, waits.State);
                Assert.Contains("VIEW SERVER STATE", waits.Reason);
            }
            else
            {
                // B0 row 4 / K29: other roles' rows are visible with their identity hidden, and cannot be killed.
                var foreign = s.Sessions!.Single(r => r.Key.Id == Spid(holder));
                Assert.Equal("", foreign.Key.Instance);
                Assert.NotNull(foreign.IdentityHidden);
                var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = foreign.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
                Assert.Equal("identityHidden", r.Code);
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(holder, "SELECT 1")));
            }
            _ = victim;
        }

        [IntegrationFact]
        public async Task T11_top_queries_show_a_query_run_n_times_with_an_execution_delta_of_n()
        {
            var m = await StartAsync(MonitorPane.TopQueries, MonitorPane.Sessions);
            var tag = $"topq_{Guid.NewGuid():N}"[..20];
            await using var c = await OpenForeignAsync("it-top");
            // pg_stat_statements' queryid ignores aliases and constants and keeps the first text it saw, so an alias tag
            // lands on an entry from an earlier run; a temp table (its own OID, dropped on disconnect) gives this run its own queryid.
            if (!Ms) await ExecAsync(c, $"CREATE TEMP TABLE {tag} (v int)");
            await WaitSnapshot(m.MonitorId, x => x.Status.Any(st => st.Pane == MonitorPane.TopQueries && st.State is PaneState.Ok or PaneState.Collecting), 20_000);
            // Wait until the first (baseline) read happened, then run the query N times.
            await WaitSnapshot(m.MonitorId, x => x.Status.Any(st => st.Pane == MonitorPane.TopQueries && st.State == PaneState.Ok) || x.TopQueries != null, 20_000);
            // SQL Server auto-parameterises a plain SELECT and caches it under the normalised text (comment stripped); a
            // batch with a variable is cached as written.
            for (var i = 0; i < 7; i++)
                // The statement text ends at its last token (a trailing comment is not part of it): the tag is an alias.
                await ScalarAsync(c, Ms ? $"DECLARE @x int = -{i + 1}; SELECT COUNT(*) AS {tag} FROM {Table} WHERE v >= @x" : $"SELECT COUNT(*) FROM {tag} WHERE v >= -{i + 1}");
            // T11: each refresh carries its own deltas once, so their sum over the run is N; one more refresh adds nothing.
            List<TopQueryRow> Rows() => Host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.TopQueries != null)
                .SelectMany(x => x.TopQueries!.Where(q => q.TextId != null && IsTheQuery(TextOf(m.MonitorId, x, q.TextId) ?? ""))).ToList();
            // The CREATE TEMP TABLE carries the tag too (pg_stat_statements tracks utility statements): when the baseline read
            // came before it, it showed up as one more execution ("exec deltas 1,7").
            bool IsTheQuery(string text) => text.Contains(tag) && text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (Rows().Sum(q => q.ExecDelta) < 7 && DateTime.UtcNow < deadline) await Task.Delay(200);
            var refreshes = Host.Notifier.Of<MonitorSnapshot>().Count(x => x.MonitorId == m.MonitorId && x.TopQueries != null);
            await WaitSnapshot(m.MonitorId, x => x.TopQueries != null && Host.Notifier.Of<MonitorSnapshot>().Count(y => y.MonitorId == m.MonitorId && y.TopQueries != null) > refreshes, 30_000);
            var rows = Rows();
            Assert.True(rows.Sum(q => q.ExecDelta) == 7, $"exec deltas {string.Join(",", rows.Select(q => q.ExecDelta))}");
            if (Ms) Assert.All(rows, q => Assert.NotNull(q.CpuMsDelta)); else Assert.All(rows, q => Assert.Null(q.CpuMsDelta));
        }

        string? TextOf(string monitorId, MonitorSnapshot s, string id) =>
            s.Texts?.GetValueOrDefault(id) ?? Host.Rpc.MonitorTexts(new MonitorTextsParams { MonitorId = monitorId, Ids = [id] }).Result.Texts.GetValueOrDefault(id);

        [IntegrationFact]
        public async Task D14_D15_D20_waits_metrics_and_io_report_values_with_their_source_and_scope()
        {
            if (!Ms)
                await using (var admin = await OpenForeignAsync("waits-reset"))
                    await ExecAsync(admin, "SELECT pg_wait_sampling_reset_profile()"); // the fixture's profile grows with churn (B0 row 16)
            var m = await StartAsync(MonitorPane.Waits, MonitorPane.Metrics, MonitorPane.Io, MonitorPane.Sessions);
            var (holder, victim, victimDone) = await BlockingPairAsync(1);
            if (Ms)
            {
                // SQL Server counts a wait when it completes: let the victim wait ~3 s, then release it.
                await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(victim) && r.WaitMs >= 2_000) == true);
                await ExecAsync(holder, "ROLLBACK");
                await victimDone.WaitAsync(TimeSpan.FromSeconds(10));
            }
            var s = await WaitSnapshot(m.MonitorId, x => x.Waits?.Any(w => w.Category == "Lock" && w.WaitMsDelta > 0) == true, 30_000);
            if (!Ms) Assert.Equal("server-profile", s.WaitCoverage!.Source);
            var metrics = await WaitSnapshot(m.MonitorId, x => x.Metrics?.Values.Any(v => v.Key is "batchRequests" or "tps" && v.Value is > 0) == true, 20_000);
            var cpu = metrics.Metrics!.Values.Single(v => v.Key == "cpu");
            if (Ms) Assert.Equal("SQL Server process", cpu.Note);
            else { Assert.Null(cpu.Value); Assert.NotNull(cpu.Note); }
            var io = await WaitSnapshot(m.MonitorId, x => x.Io != null, 20_000);
            Assert.All(io.Io!, r => Assert.Equal(Ms ? "file" : "cluster", r.Scope));
            if (Ms) Assert.All(io.Io!, r => { Assert.NotNull(r.File); Assert.Null(r.BackendType); });
            else Assert.All(io.Io!, r => { Assert.Null(r.Database); Assert.Null(r.File); Assert.NotNull(r.BackendType); });
            _ = holder; _ = victim;
        }

        [IntegrationFact]
        public async Task RD1_request_detail_returns_the_text_and_a_plan_or_the_reason_there_is_none()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            await using var c = await OpenForeignAsync("it-long");
            if (Ms) await ExecAsync(c, EnsureNumbersSql);
            var running = Busy[c] = Task.Run(async () =>
            {
                // Runs until killed whatever the shared test table holds (other tests change it).
                try { await ExecAsync(c, Ms ? LongParallelSql : "SELECT pg_sleep(8), count(*) FROM monitor_it"); }
                catch { /* cancelled */ }
            });
            var id = Spid(c);
            bool Mine(ActivityRow r) => r.Key.Id == id && r.Program == "it-long" && r.ElapsedMs != null;
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(Mine) == true);
            var key = s.Sessions!.Single(Mine).Key;
            var d = await Host.Rpc.MonitorRequestDetail(new MonitorSessionParams { MonitorId = m.MonitorId, Key = key });
            Assert.False(string.IsNullOrEmpty(d.Text));
            if (Ms)
            {
                Assert.True(d.Plan != null, $"no plan: {d.PlanReason}");
                Assert.True(d.PlanSource is "live" or "cached");
            }
            else
            {
                Assert.Null(d.Plan);
                Assert.Contains("EXPLAIN", d.PlanReason);
            }
            try { await ExecAsync(await OpenForeignAsync("it-admin2"), Ms ? $"KILL {id}" : $"SELECT pg_cancel_backend({id})"); } catch { }
            await running.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [Collection("monitor-mssql")]
    public class SqlServer() : Base(Engine.Mssql)
    {
        [IntegrationFact]
        public async Task K10_cancel_request_is_refused_with_the_engine_reason_and_nothing_is_sent()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var (holder, victim, _) = await BlockingPairAsync(1);
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == Spid(victim) && r.BlockedBy.Length > 0) == true);
            var key = s.Sessions!.Single(r => r.Key.Id == Spid(victim)).Key;
            var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.CancelRequest, ConfirmedKill = true });
            Assert.Equal(KillOutcome.Refused, r.Outcome);
            Assert.Equal("unsupported", r.Code);
            Assert.Contains("terminate the whole session", r.Message);
            _ = holder;
        }

        [IntegrationFact]
        public async Task P2_P3_K19_terminating_a_long_write_transaction_needs_the_rollback_confirm_and_reports_rollback_progress()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var writer = await OpenForeignAsync("it-writer");
            await ExecAsync(writer, "IF OBJECT_ID('dbo.monitor_big') IS NULL CREATE TABLE dbo.monitor_big(id int IDENTITY PRIMARY KEY, pad char(4000) NOT NULL DEFAULT 'x');");
            await ExecAsync(writer, "BEGIN TRAN; INSERT dbo.monitor_big(pad) SELECT TOP (30000) 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b;");
            var id = Spid(writer);
            // By program too: a snapshot from before the writer opened can hold an earlier session that had the same spid
            // (reused under the parallel suite), whose key then reads as ended and the preview as risk None.
            bool Mine(ActivityRow r) => r.Key.Id == id && r.Program == "it-writer";
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(Mine) == true);
            var key = s.Sessions!.Single(Mine).Key;
            var preview = await Host.Rpc.MonitorKillPreview(new MonitorSessionParams { MonitorId = m.MonitorId, Key = key });
            Assert.Equal(RollbackRisk.High, preview.Risk); // ≥ 100 MB of log (P2)
            Assert.True(preview.LogBytesUsed >= KillRisk.HighLogBytes);
            var refused = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true });
            Assert.Equal("longRollback", refused.Code); // P3: enforced in the sidecar
            var done = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            Assert.Equal(KillOutcome.Done, done.Outcome);
            // K19: a repeated kill while it rolls back is refused (the server would accept it silently).
            var again = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = key, Mode = KillMode.TerminateSession, ConfirmedKill = true, ConfirmedLongRollback = true });
            Assert.True(again.Code is "rollingBack" or "notInSnapshot", again.Code);
            await WaitSnapshot(m.MonitorId, x => x.Rollbacks?.Any(p => p.Key == key && p.Done) == true || x.Sessions?.All(r => r.Key != key) == true, 60_000);
        }

        [IntegrationFact]
        public async Task X1_X14_a_real_deadlock_appears_in_the_deadlock_history_with_its_victim_and_exports_inside_app_data()
        {
            var m = await StartAsync(MonitorPane.Sessions, MonitorPane.Deadlocks);
            var a = await OpenForeignAsync("it-dl-a");
            var b = await OpenForeignAsync("it-dl-b");
            await ExecAsync(a, "BEGIN TRAN; UPDATE dbo.monitor_it SET v = v WHERE id = 1;");
            await ExecAsync(b, "BEGIN TRAN; UPDATE dbo.monitor_it SET v = v WHERE id = 2;");
            var ta = Task.Run(async () => { try { await ExecAsync(a, "UPDATE dbo.monitor_it SET v = v WHERE id = 2; COMMIT;"); } catch { } });
            await Task.Delay(300);
            var tb = Task.Run(async () => { try { await ExecAsync(b, "UPDATE dbo.monitor_it SET v = v WHERE id = 1; COMMIT;"); } catch { } });
            await Task.WhenAll(ta, tb).WaitAsync(TimeSpan.FromSeconds(30));
            DeadlocksResult? r = null;
            for (var i = 0; i < 30; i++)
            {
                r = await Host.Rpc.MonitorDeadlocks(new DeadlocksParams { MonitorId = m.MonitorId, FromMs = Querithm.Core.Time.NowMs() - 120_000 });
                if (r.Events.Any(e => e.Processes.Any(p => p.Spid == Spid(a)) && e.Processes.Any(p => p.Spid == Spid(b)))) break;
                await Task.Delay(1_000);
            }
            var ev = r!.Events.First(e => e.Processes.Any(p => p.Spid == Spid(a)) && e.Processes.Any(p => p.Spid == Spid(b)));
            Assert.Single(ev.Victims);
            Assert.Contains(ev.Processes, p => p.Victim);
            // X16: the 30 s poll delivers it too (as a new deadlock), even when the ring buffer's XML is full and leaves out
            // the newest events (B0 row 15) — the poll resumes in the event files.
            var polled = DateTime.UtcNow.AddSeconds(45);
            while (!Host.Notifier.Of<MonitorSnapshot>().Any(x => x.MonitorId == m.MonitorId && x.NewDeadlocks?.Any(d => d.Id == ev.Id) == true) && DateTime.UtcNow < polled) await Task.Delay(500);
            Assert.Contains(Host.Notifier.Of<MonitorSnapshot>(), x => x.MonitorId == m.MonitorId && x.NewDeadlocks?.Any(d => d.Id == ev.Id) == true);
            Assert.NotEmpty(ev.Resources);
            Assert.False(ev.Partial);
            var file = await Host.Rpc.MonitorDeadlockXml(new DeadlockXmlParams { MonitorId = m.MonitorId, EventId = ev.Id, ConfirmedText = true });
            Assert.StartsWith(Path.Combine(Host.DataDir, "exports"), file.Path);
            Assert.StartsWith("<deadlock", File.ReadAllText(file.Path));
        }
    }

    [Collection("monitor-pg")]
    public class Postgres() : Base(Engine.Pg)
    {
        [IntegrationFact]
        public async Task K25_K26_cancelling_a_foreign_running_query_keeps_its_session_and_a_stale_statement_is_never_cancelled()
        {
            var m = await StartAsync(MonitorPane.Sessions);
            var (holder, victim, victimDone) = await BlockingPairAsync(1);
            long v = Spid(victim);
            var s = await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(r => r.Key.Id == v && r.StatementStart != null && r.BlockedBy.Length > 0) == true);
            var row = s.Sessions!.Single(r => r.Key.Id == v);
            // K26 at the server: a statement start that is 1 µs off matches nothing; nothing is sent.
            var stale = (long.Parse(row.StatementStart!) - 1).ToString();
            var guarded = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = row.Key, Mode = KillMode.CancelRequest, ConfirmedKill = true, ExpectedStatementStart = stale });
            Assert.Equal(KillOutcome.StatementFinished, guarded.Outcome);
            Assert.False(victimDone.IsCompleted);
            var r = await Host.Rpc.MonitorKill(new MonitorKillParams { MonitorId = m.MonitorId, Target = row.Key, Mode = KillMode.CancelRequest, ConfirmedKill = true, ExpectedStatementStart = row.StatementStart });
            Assert.Equal(KillOutcome.Done, r.Outcome);
            await victimDone.WaitAsync(TimeSpan.FromSeconds(10));
            // K25: the session stays connected with the same identity.
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(victim, "SELECT 1")));
            await WaitSnapshot(m.MonitorId, x => x.Sessions?.Any(rr => rr.Key == row.Key && rr.Status != "active") == true);
            _ = holder;
        }

        [IntegrationFact]
        public async Task X11_deadlock_history_degrades_with_the_reason()
        {
            var m = await StartAsync(MonitorPane.Deadlocks, MonitorPane.Sessions);
            var s = await WaitSnapshot(m.MonitorId, x => x.Status.Any(st => st.Pane == MonitorPane.Deadlocks && st.State == PaneState.Degraded));
            Assert.Contains("no deadlock history", s.Status.Single(st => st.Pane == MonitorPane.Deadlocks).Reason);
        }
    }
}

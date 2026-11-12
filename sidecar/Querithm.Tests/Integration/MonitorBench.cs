using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Monitoring;
using Querithm.Host;

namespace Querithm.Tests.Integration;

/// <summary>
/// Activity-monitor benchmarks (§14 metrics 28–32) on the Docker fixture, with idle and active workloads: SQL Server,
/// PostgreSQL and MySQL 8.4 (Phase D2; deadlock history, metric 30, is SQL Server only — MySQL degrades that pane).
/// Opt-in: QUERITHM_BENCH_MONITOR=1 (opens 500 connections, fills the plan cache, seeds deadlocks; minutes).
/// Writes bench/results/&lt;date&gt;-monitor-&lt;os&gt;.json. The webview half of metric 28 (parse + Glide) is measured by
/// scripts/bench/run.mjs --ui (monitorApply).
/// </summary>
[Collection("monitor-bench")]
public class MonitorBench
{
    static readonly Dictionary<string, object> Results = new();
    static List<double> Polls = [];
    static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    static double P(IEnumerable<double> xs, double q)
    {
        var s = xs.Order().ToArray();
        return s.Length == 0 ? double.NaN : s[Math.Min(s.Length - 1, (int)Math.Ceiling(s.Length * q) - 1)];
    }

    static void Save()
    {
        var dir = Path.Combine(Root, "bench", "results");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyy-MM-dd-HH-mm-ss}-monitor-{(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux")}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            date = DateTime.UtcNow, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, cores = Environment.ProcessorCount,
            fixture = "scripts/bench/docker/compose.yml (SQL Server 2022, PostgreSQL 16, MySQL 8.4)", results = Results,
        }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    static Engine EngineOf(string name) => name switch { "pg" => Engine.Pg, "mysql" => Engine.MySql, _ => Engine.Mssql };

    /// <summary>MySQL's fixture product (MySqlIntegrationTests): mysql84, its port and test database.</summary>
    const string MySqlProduct = "mysql";
    const string MySqlContainer = "querithm-fixture-mysql84-1";

    /// <summary>A direct fixture connection outside the sidecar (load, fill, idle sessions), unpooled.</summary>
    static DbConnection Connect(Engine engine, string? app = null, string? database = null)
    {
        var name = app == null ? "" : $";Application Name={app}";
        return engine switch
        {
            Engine.Mssql => new SqlConnection($"Server={TestHost.MssqlHost},14333;Database={database ?? "master"};User ID=sa;Password={TestHost.MssqlPassword};TrustServerCertificate=True;Encrypt=Optional;Pooling=false{name}"),
            Engine.Pg => new NpgsqlConnection($"Host=127.0.0.1;Port=54329;Database={database ?? "postgres"};Username=postgres;Password={TestHost.PgPassword};Pooling=false{name}"),
            _ => new MySqlConnection($"Server=127.0.0.1;Port={MySqlIntegrationTests.Port(MySqlProduct)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False;Database={database ?? MySqlIntegrationTests.Db(MySqlProduct)}{name}"),
        };
    }

    /// <summary>The services, notifier and a saved profile with an open session: TestHost serves SQL Server and PostgreSQL, the MySQL integration host MySQL.</summary>
    sealed record BenchHost(RpcServer Rpc, Services Services, CapturingNotifier Notifier, Profile Profile, IAsyncDisposable Owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Owner.DisposeAsync();
    }

    static async Task<BenchHost> OpenHostAsync(Engine engine, string? mssqlDatabase = null)
    {
        if (engine == Engine.MySql)
        {
            var (h, p, _) = await MySqlIntegrationTests.OpenAsync(MySqlProduct);
            return new(h.Rpc, h.Services, h.Notifier, p, h);
        }
        var host = new TestHost();
        try
        {
            if (engine == Engine.Mssql && mssqlDatabase != null) await host.EnsureDatabaseAsync(Engine.Mssql, mssqlDatabase);
            var (profile, _) = engine == Engine.Mssql ? await host.OpenMssqlAsync("master") : await host.OpenPgAsync();
            return new(host.Rpc, host.Services, host.Notifier, profile, host);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    static async Task<List<DbConnection>> OpenIdleAsync(Engine engine, int n)
    {
        var list = new List<DbConnection>();
        for (var i = 0; i < n; i += 25)
        {
            var batch = Enumerable.Range(0, Math.Min(25, n - i)).Select(async _ =>
            {
                var c = Connect(engine, "bench-idle");
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
                return c;
            });
            list.AddRange(await Task.WhenAll(batch));
        }
        return list;
    }

    /// <summary>Churn while measuring (out of process, in the server container): sessions come and go, SQL text changes every statement, a blocking pair stands.</summary>
    internal static async Task<(CancellationTokenSource Stop, Task Load, DbConnection Holder)> ActiveLoadAsync(Engine engine)
    {
        var holder = Connect(engine, "bench-holder", engine == Engine.Mssql ? "MonitorIT" : null);
        await holder.OpenAsync();
        await using (var cmd = holder.CreateCommand())
        {
            cmd.CommandText = engine switch
            {
                Engine.Mssql => "IF OBJECT_ID('dbo.monitor_it') IS NULL CREATE TABLE dbo.monitor_it(id int PRIMARY KEY, v int); IF NOT EXISTS (SELECT 1 FROM dbo.monitor_it) INSERT dbo.monitor_it VALUES (1,1),(2,2),(3,3); BEGIN TRAN; UPDATE dbo.monitor_it SET v = v WHERE id = 1;",
                Engine.Pg => "CREATE TABLE IF NOT EXISTS monitor_it(id int PRIMARY KEY, v int); INSERT INTO monitor_it VALUES (1,1),(2,2),(3,3) ON CONFLICT DO NOTHING; BEGIN; UPDATE monitor_it SET v = v WHERE id = 1;",
                // InnoDB row lock held by an open transaction (the connection closes at the end, which rolls it back).
                _ => "CREATE TABLE IF NOT EXISTS monitor_it(id INT PRIMARY KEY, v INT) ENGINE=InnoDB; INSERT IGNORE INTO monitor_it VALUES (1,1),(2,2),(3,3); START TRANSACTION; UPDATE monitor_it SET v = v + 1 WHERE id = 1;",
            };
            await cmd.ExecuteNonQueryAsync();
        }
        // The churn runs inside the server's container, out of this process: the sidecar CPU figure is then the monitor's
        // own (an in-process load generator counted its own client work, 10 % on PostgreSQL). Eight clients reconnect for
        // every statement with new literal text; a second session retries an UPDATE the holder blocks (2 s lock timeout).
        // The load stops through a stop file, never by signals from outside: the postgres container runs the postmaster as
        // PID 1, so a load process whose parent was killed first is re-parented to it, and the postmaster treats any child
        // that dies by a signal as a crashed backend and restarts the server (pkill did this in the bench and the soak).
        // Each loop ends after its current statement; the script kills only pgbench, its own child, and reaps everything.
        // MySQL (mysqld is PID 1 there) runs the same shape as SQL Server: `mysql` client loops over the local socket, one
        // connection per statement, and the blocked UPDATE gives up after InnoDB's 2 s lock wait.
        var container = engine switch { Engine.Mssql => "querithm-fixture-mssql-1", Engine.Pg => "querithm-fixture-postgres-1", _ => MySqlContainer };
        var stopFile = $"/tmp/qchurn-{Guid.NewGuid():N}.stop";
        var sqlcmd = $"/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '{TestHost.MssqlPassword}' -C -d MonitorIT";
        var mysql = $"mysql -uroot --comments {MySqlIntegrationTests.Db(MySqlProduct)}";
        var script = engine switch
        {
            Engine.Mssql => $$"""
              for w in 1 2 3 4 5 6 7 8; do ( while [ ! -e {{stopFile}} ]; do {{sqlcmd}} -Q "/*qchurn*/ SELECT $RANDOM$RANDOM AS v, COUNT(*) FROM sys.all_objects WHERE $RANDOM > 0" > /dev/null 2>&1; done ) & done
              ( while [ ! -e {{stopFile}} ]; do {{sqlcmd}} -Q "/*qchurn*/ SET LOCK_TIMEOUT 2000; UPDATE dbo.monitor_it SET v = v WHERE id = 1" > /dev/null 2>&1; done ) &
              while [ ! -e {{stopFile}} ]; do sleep 0.2; done
              wait
              rm -f {{stopFile}}
              """,
            Engine.MySql => $$"""
              export MYSQL_PWD='{{TestHost.MySqlPassword}}'
              for w in 1 2 3 4 5 6 7 8; do ( while [ ! -e {{stopFile}} ]; do {{mysql}} -e "/*qchurn*/ SELECT $RANDOM$RANDOM AS v, COUNT(*) FROM information_schema.SCHEMATA WHERE $RANDOM > 0" > /dev/null 2>&1; done ) & done
              ( while [ ! -e {{stopFile}} ]; do {{mysql}} -e "/*qchurn*/ SET SESSION innodb_lock_wait_timeout = 2; UPDATE monitor_it SET v = v + 1 WHERE id = 1" > /dev/null 2>&1; done ) &
              while [ ! -e {{stopFile}} ]; do sleep 0.2; done
              wait
              rm -f {{stopFile}}
              """,
            _ => $$"""
              printf '\\set r random(1, 1000000000)\nSELECT :r AS v, count(*) FROM pg_class WHERE :r > 0;\n' > /tmp/qchurn.sql
              ( while [ ! -e {{stopFile}} ]; do psql -U postgres -q -c "/*qchurn*/ SET lock_timeout = '2s'; UPDATE monitor_it SET v = v WHERE id = 1" > /dev/null 2>&1; done ) &
              pgbench -U postgres -n -c 8 -j 2 -C -M simple -T 3600 -f /tmp/qchurn.sql postgres > /dev/null 2>&1 &
              p=$!
              while [ ! -e {{stopFile}} ]; do sleep 0.2; done
              kill $p
              wait
              rm -f {{stopFile}}
              """,
        };
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "exec", container, "bash", "-c", "# qchurn\n" + script }) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        var stop = new CancellationTokenSource();
        var load = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
            var stopPsi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "exec", container, "touch", stopFile }) stopPsi.ArgumentList.Add(a);
            using (var touch = Process.Start(stopPsi)!)
                await touch.WaitForExitAsync();
            // Each loop finishes its statement first (at most the 2 s lock timeout).
            try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException)
            {
                proc.Kill(true);
                throw new InvalidOperationException($"The load in {container} did not stop within 20 s of its stop file: its processes may still run there.");
            }
            finally { proc.Dispose(); }
        });
        await Task.Delay(1_000); // the clients are connecting
        return (stop, load, holder);
    }

    [OptInTheory("QUERITHM_BENCH_MONITOR")]
    [InlineData("mssql", false)]
    [InlineData("mssql", true)]
    [InlineData("pg", false)]
    [InlineData("pg", true)]
    [InlineData("mysql", false)]
    [InlineData("mysql", true)]
    public async Task M28_M32_snapshot_of_500_sessions_payload_lag_and_cpu(string engineName, bool active)
    {
        var engine = EngineOf(engineName);
        await using var host = await OpenHostAsync(engine, "MonitorIT");
        var profile = host.Profile;
        if (engine == Engine.Pg)
        {
            // A fresh wait profile: with profile_pid = on it keeps every past backend, and earlier churn would bloat it (B0 row 16).
            await using var r = new NpgsqlConnection($"Host=127.0.0.1;Port=54329;Database=postgres;Username=postgres;Password={TestHost.PgPassword};Pooling=false");
            await r.OpenAsync();
            await using var c = r.CreateCommand();
            c.CommandText = "SELECT pg_wait_sampling_reset_profile()";
            await c.ExecuteNonQueryAsync();
        }
        var idle = await OpenIdleAsync(engine, 500);
        (CancellationTokenSource Stop, Task Load, DbConnection Holder)? load = active ? await ActiveLoadAsync(engine) : null;
        try
        {
            var proc = Process.GetCurrentProcess();
            var m = await host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = profile.Id, IntervalMs = 2_000 });
            var monitor = host.Services.Monitors.Get(m.MonitorId);
            // Warm up (JIT, first full read), then measure 60 s of ticks.
            await Task.Delay(6_000);
            var startSeq = monitor.Seq;
            var cpu0 = proc.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            await Task.Delay(60_000);
            var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            var snaps = host.Notifier.Of<MonitorSnapshot>().Where(s => s.MonitorId == m.MonitorId && s.Sessions != null).ToList();
            var measured = snaps.Where(s => s.Seq > startSeq).ToList();
            var first = snaps.First();
            var opts = Json.Options;
            long Bytes(MonitorSnapshot s) => JsonSerializer.SerializeToUtf8Bytes(s, opts).Length;
            var noText = measured.Where(s => s.Texts == null || s.Texts.Count == 0).Select(s => (double)Bytes(s)).ToList();
            var all = measured.Select(s => (double)Bytes(s)).ToList();
            var lags = monitor.Scheduler?.RecentLags.Select(x => (double)x).ToList() ?? [];

            // Server query time and sidecar processing: the same tick batch timed against the session directly.
            var session = await host.Services.Monitors.Get(m.MonitorId).InteractiveAsync("bench", async (s, c, ct) =>
            {
                // 61 ticks: with 15 the "p95" of 14 samples was the slowest one (0.8–14.8 ms across runs, median steady).
                var server = new List<double>();
                var processing = new List<double>();
                var withGc = 0;
                var store = new TextStore();
                for (var i = 0; i < 61; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var r = await s.TickAsync(c, new Contract.Providers.TickRequest(true, true, true, true, ActivityMonitor.MaxSessions), ct);
                    server.Add(sw.Elapsed.TotalMilliseconds);
                    var gcs = GC.CollectionCount(0);
                    sw.Restart();
                    var rows = r.Sessions!.Value!;
                    var keyed = rows.Select(x => x.TextId != null && r.SessionTexts!.GetValueOrDefault(x.TextId) is { } t ? x with { TextId = store.InternForSession(x.Key, t) } : x).ToArray();
                    store.Commit(keyed.Select(x => x.TextId).OfType<string>(), keyed.Select(x => x.Key).ToHashSet());
                    BlockingGraph.Build(keyed);
                    processing.Add(sw.Elapsed.TotalMilliseconds);
                    if (i > 0 && GC.CollectionCount(0) != gcs) withGc++;
                }
                return (server, processing, withGc);
            }, 120_000);
            Results[$"28-{engineName}-{(active ? "active" : "idle")}"] = new
            {
                sessions = first.Sessions!.Length,
                serverQueryMs = new { median = P(session.server, 0.5), p95 = P(session.server, 0.95), target = 100 },
                sidecarProcessingMs = new { median = P(session.processing.Skip(1), 0.5), p95 = P(session.processing.Skip(1), 0.95), max = session.processing.Skip(1).Max(), samplesWithAGc = session.withGc, samples = session.processing.Count - 1, target = 10 },
                // Under churn every tick carries new texts: then there is no "without new texts" sample (null, not NaN).
                payloadBytesPerTickWithoutNewTexts = noText.Count > 0 ? new { median = (double?)P(noText, 0.5), max = (double?)noText.Max(), target = 512 * 1024 } : new { median = (double?)null, max = (double?)null, target = 512 * 1024 },
                payloadBytesPerTick = new { median = P(all, 0.5), max = all.Max() },
                firstSnapshotBytes = new { value = Bytes(first), target = 2_500 * 1024 },
                tickLagMs = new { p95 = P(lags, 0.95), max = lags.Count > 0 ? lags.Max() : 0, target = 200 },
                sidecarCpuPercent = new { value = cpu, target = 2, note = "whole test process (sidecar services in-process), all cores" },
                ticks = measured.Count,
            };
            Save();
            Assert.True(first.Sessions!.Length >= 500);
            // M13: the payload budget is a size, not a timing, so it is asserted here, not only recorded.
            Assert.True(noText.Count == 0 || noText.Max() <= 512 * 1024, $"tick without new texts: {(noText.Count > 0 ? noText.Max() : 0)} bytes");
            Assert.True(Bytes(first) <= 2_500 * 1024, $"first snapshot: {Bytes(first)} bytes");
        }
        finally
        {
            if (load is { } l)
            {
                l.Stop.Cancel();
                Exception? loadError = null;
                try { await l.Load.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception e) { loadError = e; }
                await l.Holder.DisposeAsync();
                foreach (var c in idle) await c.DisposeAsync();
                if (loadError != null) throw new InvalidOperationException("The active load did not stop cleanly", loadError);
            }
            else foreach (var c in idle) await c.DisposeAsync();
        }
    }

    /// <summary>
    /// D4 S12: metrics 28, 31 and 32 on the Oracle Free fixture, monitored as SYSTEM. Oracle Free runs with PROCESSES = 200
    /// (about 90 taken by the instance), so the session list holds 100 idle app-user sessions, not 500 (§14 row 28.oracle).
    /// 31: 20,000 distinct statements parsed into the shared pool first, then V$SQLSTATS read in full (Oracle has no
    /// incremental cutoff that would find an aged-out entry). 32: the sidecar's CPU over 60 s with every pane at 2 s.
    /// </summary>
    [OptInFact("QUERITHM_BENCH_MONITOR")]
    public async Task M28_M31_M32_oracle_100_sessions_full_sqlstats_and_cpu()
    {
        await using var host = new TestHost(TestOracle.Supported(Directory.CreateTempSubdirectory("qora-bench-").FullName));
        var profile = await host.Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "ora-bench", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password,
            Username = "system", DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
        });
        await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = profile.Id, Secret = FixtureSecrets.Password("oracle-admin") });
        var appString = OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser);
        await using (var fill = new Oracle.ManagedDataAccess.Client.OracleConnection(appString))
        {
            await fill.OpenAsync();
            await using var cmd = fill.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText = "DECLARE n NUMBER; BEGIN FOR i IN 1 .. 20000 LOOP EXECUTE IMMEDIATE 'SELECT /* q_bench31 */ COUNT(*) FROM dual WHERE ' || i || ' > 0' INTO n; END LOOP; END;";
            await cmd.ExecuteNonQueryAsync();
        }
        var idle = new List<Oracle.ManagedDataAccess.Client.OracleConnection>();
        try
        {
            for (var i = 0; i < 100; i += 10)
                idle.AddRange(await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
                {
                    var c = new Oracle.ManagedDataAccess.Client.OracleConnection(appString);
                    await c.OpenAsync();
                    return c;
                })));
            var proc = Process.GetCurrentProcess();
            var m = await host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = profile.Id, IntervalMs = 2_000 });
            var monitor = host.Services.Monitors.Get(m.MonitorId);
            await Task.Delay(6_000);
            var startSeq = monitor.Seq;
            var cpu0 = proc.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            await Task.Delay(60_000);
            var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            var snaps = host.Notifier.Of<MonitorSnapshot>().Where(x => x.MonitorId == m.MonitorId && x.Sessions != null).ToList();
            var measured = snaps.Where(x => x.Seq > startSeq).ToList();
            var opts = Json.Options;
            long Bytes(MonitorSnapshot x) => JsonSerializer.SerializeToUtf8Bytes(x, opts).Length;
            var lags = monitor.Scheduler?.RecentLags.Select(x => (double)x).ToList() ?? [];
            var r = await monitor.InteractiveAsync("bench", async (sess, c, ct) =>
            {
                var server = new List<double>();
                var processing = new List<double>();
                var store = new TextStore();
                for (var i = 0; i < 31; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var t = await sess.TickAsync(c, new TickRequest(true, true, true, true, ActivityMonitor.MaxSessions), ct);
                    server.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    var keyed = t.Sessions!.Value!.Select(x => x.TextId != null && t.SessionTexts!.GetValueOrDefault(x.TextId) is { } text ? x with { TextId = store.InternForSession(x.Key, text) } : x).ToArray();
                    store.Commit(keyed.Select(x => x.TextId).OfType<string>(), keyed.Select(x => x.Key).ToHashSet());
                    BlockingGraph.Build(keyed);
                    processing.Add(sw.Elapsed.TotalMilliseconds);
                }
                // Profile of the tick's parts, each read alone (the tick runs them one after another).
                var parts = new Dictionary<string, List<double>> { ["sessions"] = [], ["waits"] = [], ["metrics"] = [] };
                for (var i = 0; i < 21; i++)
                    foreach (var (name, req) in new[]
                    {
                        ("sessions", new TickRequest(true, false, false, false, ActivityMonitor.MaxSessions)),
                        ("waits", new TickRequest(false, true, false, false, ActivityMonitor.MaxSessions)),
                        ("metrics", new TickRequest(false, false, true, false, ActivityMonitor.MaxSessions)),
                    })
                    {
                        var sw = Stopwatch.StartNew();
                        await sess.TickAsync(c, req, ct);
                        if (i > 0) parts[name].Add(sw.Elapsed.TotalMilliseconds);
                    }
                var full = new List<double>();
                var delta = new List<double>();
                var entries = 0;
                var tracker = new TopQueriesTracker();
                for (var i = 0; i < 8; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var read = await sess.TopQueriesAsync(c, ct);
                    full.Add(sw.Elapsed.TotalMilliseconds);
                    entries = Math.Max(entries, read.Value!.Entries.Length);
                    sw.Restart();
                    var (_, top) = tracker.Update(read.Value!, "cpu", false, 100);
                    delta.Add(sw.Elapsed.TotalMilliseconds);
                    var keys = top.Select(x => x.Sample!.TextKey).ToList();
                    if (keys.Count > 0) await sess.TopQueryTextsAsync(c, keys, ct);
                }
                return (server, processing, full, delta, entries, parts);
            }, 600_000);
            Results["28-oracle-idle"] = new
            {
                sessions = snaps.First().Sessions!.Length,
                serverQueryMs = new { median = P(r.server, 0.5), p95 = P(r.server, 0.95), target = 100 },
                sidecarProcessingMs = new { median = P(r.processing.Skip(1), 0.5), p95 = P(r.processing.Skip(1), 0.95), target = 10 },
                payloadBytesPerTick = new { median = P(measured.Select(x => (double)Bytes(x)), 0.5), max = measured.Max(Bytes), target = 512 * 1024 },
                tickLagMs = new { p95 = P(lags, 0.95), max = lags.Count > 0 ? lags.Max() : 0, target = 200 },
                ticks = measured.Count,
                partsMs = r.parts.ToDictionary(kv => kv.Key, kv => new { median = P(kv.Value, 0.5), p95 = P(kv.Value, 0.95) }),
                note = "Oracle Free: PROCESSES = 200, so 100 idle sessions instead of 500",
            };
            Results["31-oracle"] = new
            {
                // Oracle Free's shared pool keeps only part of the 20,000 parsed statements: the read is over what it keeps.
                entries = r.entries,
                fullReadMs = new { median = P(r.full, 0.5), p95 = P(r.full, 0.95), samples = r.full.Count, target = 1_500 },
                deltaAndTop100Ms = new { median = P(r.delta.Skip(1), 0.5), p95 = P(r.delta.Skip(1), 0.95), target = 20 },
                note = "V$SQLSTATS read in full every refresh (no incremental cutoff); SQL Server's full-read target",
            };
            Results["32-oracle"] = new { sidecarCpuPercent = new { value = cpu, target = 1, note = "whole test process, all cores; every pane at 2 s, 100 idle sessions" } };
            Save();
            Assert.True(snaps.First().Sessions!.Length >= 100);
            Assert.True(r.entries > 0, "V$SQLSTATS read no entry");
        }
        finally
        {
            foreach (var c in idle) await c.DisposeAsync();
        }
    }

    [OptInFact("QUERITHM_BENCH_MONITOR")]
    public void M29_history_of_10_minutes_at_2_s_with_500_sessions_stays_under_50MB_and_scrubs_fast()
    {
        var texts = new TextStore();
        var history = new HistoryBuffer(texts);
        var rnd = new Random(1);
        var before = Heap.LiveBytes();
        for (var tick = 1; tick <= 300; tick++)
        {
            var rows = Enumerable.Range(1, 500).Select(i => new ActivityRow
            {
                Key = new SessionKey { Id = i, Instance = $"46321:{3076143 + i}" }, Login = $"user{i % 20}", Host = $"host{i % 7}", Program = "app", Database = "SalesDB",
                Status = rnd.Next(4) == 0 ? "running" : "sleeping", Command = "SELECT", WaitType = rnd.Next(5) == 0 ? "LCK_M_S" : null, WaitMs = rnd.Next(5) == 0 ? rnd.Next(10_000) : null,
                BlockedBy = i % 50 == 0 ? [1] : [], TextId = texts.InternForSession(new SessionKey { Id = i, Instance = $"46321:{3076143 + i}" }, $"SELECT * FROM t WHERE id = {(i % 10 == 0 ? tick : 0) + i}"),
                ElapsedMs = rnd.Next(100_000), CpuMs = tick * 10 + i, Reads = tick * 100L + i, IsUserSession = true,
            }).ToArray();
            texts.Commit(rows.Select(r => r.TextId!), rows.Select(r => r.Key).ToHashSet());
            history.Add(new MonitorSnapshot { MonitorId = "m", Seq = tick, At = tick * 2_000L, Sessions = rows, Status = [] }, tick * 2_000L);
        }
        var after = Heap.LiveBytes();
        var scrub = new List<double>();
        foreach (var t in history.Ticks().Where((_, i) => i % 10 == 0))
        {
            var sw = Stopwatch.StartNew();
            Assert.NotNull(history.At(t.Seq));
            scrub.Add(sw.Elapsed.TotalMilliseconds);
        }
        Results["29"] = new
        {
            ticks = history.Count, estimatedBytes = history.Bytes, measuredHeapDeltaBytes = after - before, targetBytes = 50L * 1024 * 1024,
            scrubMs = new { median = P(scrub, 0.5), p95 = P(scrub, 0.95), max = scrub.Max(), target = 50 },
        };
        Save();
        Assert.True(after - before <= 50L * 1024 * 1024);
    }

    [OptInFact("QUERITHM_BENCH_MONITOR")]
    public async Task M30_deadlock_history_of_200_events_reads_and_parses_within_budget()
    {
        await using var host = new TestHost();
        await host.EnsureDatabaseAsync(Engine.Mssql, "MonitorIT");
        var (profile, _) = await host.OpenMssqlAsync("MonitorIT");
        string Cs(string app) => $"Server={TestHost.MssqlHost},14333;Database=MonitorIT;User ID=sa;Password={TestHost.MssqlPassword};TrustServerCertificate=True;Encrypt=Optional;Pooling=false;Application Name={app}";
        await using (var c = new SqlConnection(Cs("dl-setup")))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "IF OBJECT_ID('dbo.dl_seed') IS NULL CREATE TABLE dbo.dl_seed(pair int, id int, v int, PRIMARY KEY(pair, id)); IF NOT EXISTS (SELECT 1 FROM dbo.dl_seed) INSERT dbo.dl_seed SELECT p.n, i.n, 0 FROM (SELECT TOP 20 ROW_NUMBER() OVER (ORDER BY (SELECT 1)) n FROM sys.all_objects) p CROSS JOIN (VALUES (1),(2)) i(n);";
            await cmd.ExecuteNonQueryAsync();
        }
        // Seed deadlocks (fixture helper): one at a time, opposite update order. Deadlocks that form together are resolved in
        // one lock-monitor pass and reported in one multi-victim xml_deadlock_report (20 concurrent pairs gave ~1 report per
        // 10 deadlocks), so concurrency would seed far fewer events than deadlocks. After the first one the lock monitor
        // checks every ~100 ms, so this stays quick.
        for (var seeded = 0; seeded < 210; seeded++)
        {
            var pair = seeded % 20 + 1;
            await using var a = new SqlConnection(Cs("dl-a"));
            await using var b = new SqlConnection(Cs("dl-b"));
            await a.OpenAsync(); await b.OpenAsync();
            async Task Run(SqlConnection c, string sql) { await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
            await Run(a, $"BEGIN TRAN; UPDATE dbo.dl_seed SET v = v + 1 WHERE pair = {pair} AND id = 1;");
            await Run(b, $"BEGIN TRAN; UPDATE dbo.dl_seed SET v = v + 1 WHERE pair = {pair} AND id = 2;");
            var ta = Task.Run(async () => { try { await Run(a, $"UPDATE dbo.dl_seed SET v = v + 1 WHERE pair = {pair} AND id = 2; COMMIT;"); } catch { } });
            var tb = Task.Run(async () => { try { await Run(b, $"UPDATE dbo.dl_seed SET v = v + 1 WHERE pair = {pair} AND id = 1; COMMIT;"); } catch { } });
            await Task.WhenAll(ta, tb);
        }
        var m = await host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = profile.Id, IntervalMs = 30_000, Panes = [MonitorPane.Sessions] });
        await Task.Delay(2_000);
        var monitor = host.Services.Monitors.Get(m.MonitorId);
        var (readMs, parseMs, events, perEvent) = await monitor.InteractiveAsync("bench", async (s, c, ct) =>
        {
            var sw = Stopwatch.StartNew();
            var r = await s.DeadlocksAsync(c, Time.NowMs() - 3_600_000, Time.NowMs(), 200, ct);
            var total = sw.Elapsed.TotalMilliseconds;
            // The 30 s poll (B0 row 15): the first primes its file position with a full read, later ones read only what is new.
            var poll = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                sw.Restart();
                await s.DeadlocksAsync(c, Time.NowMs() - 3_600_000, Time.NowMs(), 200, ct, recentOnly: true);
                poll.Add(sw.Elapsed.TotalMilliseconds);
            }
            Polls = poll;
            // Parse cost alone: re-parse what the server returned, per event.
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT t.target_data FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address WHERE s.name = N'system_health' AND t.target_name = N'ring_buffer'";
            var ring = (string)(await cmd.ExecuteScalarAsync(ct))!;
            var graphs = Querithm.Providers.SqlServer.SqlServerDeadlocks.Graphs(ring).ToList();
            var per = new List<double>();
            var parse = Stopwatch.StartNew();
            foreach (var (g, at, cut) in graphs)
            {
                var one = Stopwatch.StartNew();
                Querithm.Providers.SqlServer.SqlServerDeadlocks.ParseGraph(g, at, "ring buffer", cut);
                per.Add(one.Elapsed.TotalMilliseconds);
            }
            return (total, parse.Elapsed.TotalMilliseconds, r.Value?.Length ?? 0, per);
        }, 120_000);
        Results["30"] = new
        {
            events, serverReadAndParseMs = new { value = readMs, target = 2_000 }, parseTotalMs = new { value = parseMs, target = 200 },
            pollReadMs = new { first = Polls.Count > 0 ? Polls[0] : double.NaN, later = Polls.Skip(1).ToArray(), note = "the 30 s poll: the first primes its position in the event files (a full read), later ones read only new events" },
            parsePerEventMs = new { median = P(perEvent, 0.5), target = 5 },
            note = "graph render of a 10-process deadlock is measured in the webview (scripts/bench/run.mjs --ui)",
        };
        Save();
        Assert.True(events >= 150, $"{events} events");
    }

    [OptInTheory("QUERITHM_BENCH_MONITOR")]
    [InlineData("mssql")]
    [InlineData("pg")]
    [InlineData("mysql")]
    public async Task M31_top_queries_refresh_with_a_full_plan_cache(string engineName)
    {
        var engine = EngineOf(engineName);
        await using var host = await OpenHostAsync(engine);
        var profile = host.Profile;
        long? capacity = null;
        // Fixture helper: ~50,000 plan-cache entries (SQL Server) / pg_stat_statements at its max (PG) / the statement
        // digest table at its max (MySQL).
        if (engine == Engine.Mssql)
        {
            await using var c = Connect(Engine.Mssql);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText = "DECLARE @i int = 0, @s nvarchar(200); WHILE @i < 50000 BEGIN SET @s = N'SELECT ' + CAST(@i AS nvarchar(10)) + N' AS v FROM sys.objects WHERE object_id = ' + CAST(@i AS nvarchar(10)); EXEC (@s); SET @i += 1; END";
            await cmd.ExecuteNonQueryAsync();
        }
        else if (engine == Engine.MySql)
        {
            // events_statements_summary_by_digest keeps one row per (schema, digest) up to performance_schema_digests_size;
            // statements past it are counted in one digest-less row (DIGEST NULL, which the monitor does not read). The digest
            // folds literals and value lists but keeps identifiers, so each statement gets an alias of its own; 50 per round trip.
            await using var c = Connect(Engine.MySql);
            await c.OpenAsync();
            async Task<object?> Exec(string sql)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandTimeout = 600;
                cmd.CommandText = sql;
                return await cmd.ExecuteScalarAsync();
            }
            capacity = Convert.ToInt64(await Exec("SELECT @@performance_schema_digests_size"));
            await Exec("TRUNCATE TABLE performance_schema.events_statements_summary_by_digest");
            for (var i = 0; i < capacity + 500; i += 50)
                await Exec(string.Join("; ", Enumerable.Range(i, 50).Select(j => $"SELECT {j} AS q_bench_{j} FROM DUAL WHERE FALSE")));
        }
        else
        {
            await using var c = Connect(Engine.Pg);
            await c.OpenAsync();
            // Distinct statements must differ in structure: pg_stat_statements folds constants, comments and aliases into one
            // entry (the earlier helper made ~100 entries out of 5,500 statements). 100 target-list lengths × 55 AND chains.
            await using (var reset = c.CreateCommand()) { reset.CommandText = "SELECT pg_stat_statements_reset()"; await reset.ExecuteNonQueryAsync(); }
            for (var i = 0; i < 5_500; i++)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT {string.Join(", ", Enumerable.Repeat("1", 1 + i % 100))} FROM pg_class WHERE false" + string.Concat(Enumerable.Repeat(" AND true", i / 100));
                await cmd.ExecuteNonQueryAsync();
            }
        }
        var m = await host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = profile.Id, IntervalMs = 30_000, Panes = [MonitorPane.Sessions] });
        await Task.Delay(1_000);
        var monitor = host.Services.Monitors.Get(m.MonitorId);
        var r = await monitor.InteractiveAsync("bench", async (s, c, ct) =>
        {
            var tracker = new TopQueriesTracker();
            var full = new List<double>();
            var incr = new List<double>();
            var delta = new List<double>();
            int entries = 0;
            TopQueryRead? fullRead = null;
            for (var i = 0; i < 12; i++)
            {
                var sw = Stopwatch.StartNew();
                var read = await s.TopQueriesAsync(c, ct);
                (read.Value!.Full ? full : incr).Add(sw.Elapsed.TotalMilliseconds);
                entries = Math.Max(entries, read.Value!.Entries.Length);
                if (read.Value!.Full) fullRead ??= read.Value;
                sw.Restart();
                var (_, top) = tracker.Update(read.Value!, "cpu", false, 100);
                var keys = top.Select(t => t.Sample!.TextKey).ToList();
                delta.Add(sw.Elapsed.TotalMilliseconds);
                if (keys.Count > 0) await s.TopQueryTextsAsync(c, keys, ct);
                await Task.Delay(500, ct);
            }
            // The 30th run's full read through the tracker: every entry checked for eviction (the loop above times incremental ones).
            var sweep = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var t2 = new TopQueriesTracker();
                t2.Update(fullRead!, "cpu", false, 100);
                var sw = Stopwatch.StartNew();
                t2.Update(fullRead!, "cpu", false, 100);
                sweep.Add(sw.Elapsed.TotalMilliseconds);
            }
            return (full, incr, delta, entries, sweep);
        }, 600_000);
        // §14 names SQL Server and PostgreSQL targets only; MySQL takes PostgreSQL's (both read the whole statement table).
        Results[$"31-{engineName}"] = new
        {
            entries = r.entries, capacity,
            fullReadMs = new { median = P(r.full, 0.5), p95 = P(r.full, 0.95), samples = r.full.Count, target = engine == Engine.Mssql ? 1_500 : 100 },
            incrementalReadMs = new { median = P(r.incr, 0.5), p95 = P(r.incr, 0.95), target = engine == Engine.Mssql ? 300 : 100 },
            deltaAndTop100Ms = new { median = P(r.delta.Skip(1), 0.5), p95 = P(r.delta.Skip(1), 0.95), target = 20 },
            fullReadTrackerUpdateMs = new { median = P(r.sweep, 0.5), max = r.sweep.Max(), target = 20 },
            note = r.incr.Count == 0 ? "every refresh is a full read of the statement table (no incremental cutoff on this engine): fullReadMs is the refresh cost" : null,
        };
        Save();
        // The fill must have filled the table (the digest-less overflow row takes a slot), or the numbers measure less.
        if (capacity is { } cap) Assert.True(r.entries >= cap - 10, $"{r.entries} digests read, the table holds {cap}");
        if (engine != Engine.Mssql) return;

        // T10: a request that keeps running holds the incremental cutoff at its start, so every entry executed since is read
        // again on each run. 5,000 new statements stand in for ten minutes of activity under that request.
        await using var longReq = new SqlConnection($"Server={TestHost.MssqlHost},14333;Database=master;User ID=sa;Password={TestHost.MssqlPassword};TrustServerCertificate=True;Encrypt=Optional;Pooling=false;Application Name=bench-long");
        await longReq.OpenAsync();
        var waiting = Task.Run(async () =>
        {
            try { await using var w = longReq.CreateCommand(); w.CommandTimeout = 0; w.CommandText = "WAITFOR DELAY '00:10:00'"; await w.ExecuteNonQueryAsync(); }
            catch { /* ended by the dispose below */ }
        });
        await Task.Delay(500);
        await using (var c = new SqlConnection($"Server={TestHost.MssqlHost},14333;Database=master;User ID=sa;Password={TestHost.MssqlPassword};TrustServerCertificate=True;Encrypt=Optional;Pooling=false"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText = "DECLARE @i int = 0, @s nvarchar(200); WHILE @i < 5000 BEGIN SET @s = N'SELECT ' + CAST(@i AS nvarchar(10)) + N' AS w FROM sys.objects WHERE object_id = -' + CAST(@i AS nvarchar(10)); EXEC (@s); SET @i += 1; END";
            await cmd.ExecuteNonQueryAsync();
        }
        // What the server holds that ran since the long request started: the incremental read must return at least that
        // (single-use plans get evicted under cache pressure, so not all 5,000 survive).
        long expected;
        await using (var c = new SqlConnection($"Server={TestHost.MssqlHost},14333;Database=master;User ID=sa;Password={TestHost.MssqlPassword};TrustServerCertificate=True;Encrypt=Optional;Pooling=false"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT_BIG(*) FROM sys.dm_exec_query_stats WHERE last_execution_time >= (SELECT MIN(r.start_time) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions x ON x.session_id = r.session_id WHERE x.program_name = N'bench-long')";
            expected = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        var t10 = await monitor.InteractiveAsync("bench", async (s, c, ct) =>
        {
            var incr = new List<double>();
            var rows = 0;
            for (var i = 0; i < 6; i++)
            {
                var sw = Stopwatch.StartNew();
                var read = await s.TopQueriesAsync(c, ct);
                if (!read.Value!.Full) { incr.Add(sw.Elapsed.TotalMilliseconds); rows = Math.Max(rows, read.Value.Entries.Length); }
                await Task.Delay(300, ct);
            }
            return (incr, rows);
        }, 600_000);
        Results["31-mssql-T10-long-running-request"] = new
        {
            entriesReadPerIncrementalRun = t10.rows, entriesSinceTheLongRequestStarted = expected,
            incrementalReadMs = new { median = P(t10.incr, 0.5), p95 = P(t10.incr, 0.95), target = 300 },
        };
        Save();
        await longReq.DisposeAsync();
        try { await waiting.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* cancelled */ }
        Assert.True(t10.rows >= expected * 0.95, $"the incremental read under a long request returned {t10.rows} entries, the server holds {expected} since its start: the cutoff did not hold");
    }
}

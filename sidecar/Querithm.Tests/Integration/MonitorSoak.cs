using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// Bounded soak (plan step 10): two monitors per engine with every Phase B pane, under churn (sessions coming and going,
/// new SQL text on every statement, a standing blocking pair), for QUERITHM_SOAK_MINUTES. Every 30 s it samples the
/// monitor connections on each server, threads, thread-pool backlog, managed heap, working set and the connection
/// registry, and asserts they stay flat; afterwards nothing may be left on either server.
/// Opt-in: QUERITHM_IT=1 QUERITHM_SOAK_MINUTES=&lt;n&gt;. Writes bench/results/&lt;date&gt;-monitor-soak-&lt;os&gt;.json.
/// </summary>
[Collection("monitor-bench")]
public class MonitorSoak
{
    static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    static async Task<long> MonitorConnectionsAsync(Engine engine)
    {
        if (engine == Engine.Mssql)
        {
            await using var c = new SqlConnection($"Server={TestHost.MssqlHost},14333;Database=master;User ID=sa;Password={TestHost.MssqlPassword};TrustServerCertificate=True;Encrypt=Optional;Pooling=false;Application Name=soak-count");
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = N'Querithm Monitor'";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        await using var p = new NpgsqlConnection($"Host=127.0.0.1;Port=54329;Database=postgres;Username=postgres;Password={TestHost.PgPassword};Pooling=false;Application Name=soak-count");
        await p.OpenAsync();
        await using var q = p.CreateCommand();
        q.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'Querithm Monitor'";
        return Convert.ToInt64(await q.ExecuteScalarAsync());
    }

    sealed record Sample(double Minute, long MssqlMonitorConns, long PgMonitorConns, int Threads, int PoolThreads, long PendingWork,
        long HeapBytes, long HistoryBytes, long WorkingSetBytes, int Registry, int Snapshots, int PaneErrors, long LagP95Ms,
        int TextCount, long TextBytes, Dictionary<string, string> ErrorsByPane, long GcCommittedBytes, long GcHeapBytes, long PrivateBytes,
        long GcFragmentedBytes);

    /// <summary>History retention for the soak: the buffers fill within it, so later growth is not retention (B6 keeps 10 min by default).</summary>
    const int HistoryMinutes = 1;

    static long P95(IEnumerable<long> xs)
    {
        var a = xs.Order().ToArray();
        return a.Length == 0 ? 0 : a[Math.Min(a.Length - 1, (int)Math.Ceiling(a.Length * 0.95) - 1)];
    }

    [IntegrationFact("QUERITHM_SOAK_MINUTES")]
    public async Task Four_monitors_under_churn_keep_connections_threads_and_memory_flat_and_leave_nothing_behind()
    {
        Assert.True(double.TryParse(Environment.GetEnvironmentVariable("QUERITHM_SOAK_MINUTES"), System.Globalization.CultureInfo.InvariantCulture, out var minutes), "QUERITHM_SOAK_MINUTES is not a number");
        var host = new TestHost();
        await host.EnsureDatabaseAsync(Engine.Mssql, "MonitorIT");
        var (ms, _) = await host.OpenMssqlAsync("MonitorIT");
        var (pg, _) = await host.OpenPgAsync();
        var ids = new List<string>();
        foreach (var p in new[] { ms, ms, pg, pg })
            ids.Add((await host.Rpc.MonitorStart(new MonitorStartParams { ProfileId = p.Id, IntervalMs = 1_000 })).MonitorId);
        foreach (var id in ids) await host.Rpc.MonitorHistorySettings(new HistorySettingsParams { MonitorId = id, RetentionMinutes = HistoryMinutes });
        var loads = new[] { await MonitorBench.ActiveLoadAsync(Engine.Mssql), await MonitorBench.ActiveLoadAsync(Engine.Pg) };
        // P2 (PostgreSQL): a write transaction older than 10 min is a high rollback risk; the soak gives it the time.
        var writer = new NpgsqlConnection($"Host=127.0.0.1;Port=54329;Database=postgres;Username=postgres;Password={TestHost.PgPassword};Pooling=false;Application Name=soak-p2");
        await writer.OpenAsync();
        await using (var w = writer.CreateCommand()) { w.CommandText = "BEGIN; UPDATE monitor_it SET v = v WHERE id = 3;"; await w.ExecuteNonQueryAsync(); }
        SessionKey? p2Key = null;
        string? p2Monitor = null;
        var samples = new List<Sample>();
        var proc = Process.GetCurrentProcess();
        var wall = Stopwatch.StartNew();
        KillPreview? p2 = null;
        try
        {
            while (wall.Elapsed.TotalMinutes < minutes)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                // The capturing notifier keeps every notification: count this interval's, then drop them (test harness memory).
                var snaps = new List<MonitorSnapshot>();
                while (host.Notifier.All.TryDequeue(out var n)) if (n.Payload is MonitorSnapshot s) snaps.Add(s);
                if (snaps.SelectMany(x => (x.Sessions ?? []).Select(r => (x.MonitorId, r))).LastOrDefault(t => t.r.Program == "soak-p2") is { r: not null } hit) (p2Monitor, p2Key) = (hit.MonitorId, hit.r.Key);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                proc.Refresh();
                samples.Add(new Sample(Math.Round(wall.Elapsed.TotalMinutes, 2), await MonitorConnectionsAsync(Engine.Mssql), await MonitorConnectionsAsync(Engine.Pg),
                    proc.Threads.Count, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, GC.GetTotalMemory(true),
                    ids.Sum(id => host.Services.Monitors.Get(id).GetHistory(null).Bytes), proc.WorkingSet64,
                    host.Services.Sessions.Registry.Count, snaps.Count,
                    snaps.Sum(s => s.Status.Count(st => st.State == PaneState.Error)),
                    P95(ids.SelectMany(id => host.Services.Monitors.Get(id).Scheduler?.RecentLags ?? [])),
                    ids.Sum(id => host.Services.Monitors.Get(id).TextStats().Count), ids.Sum(id => host.Services.Monitors.Get(id).TextStats().Bytes),
                    // Which panes were in error this interval, how often, and the latest reason (the soak must explain them).
                    snaps.SelectMany(s => s.Status.Where(st => st.State == PaneState.Error)).GroupBy(st => st.Pane.ToString())
                        .ToDictionary(g => g.Key, g => $"{g.Count()}× {g.Last().Reason}"),
                    // Working set vs what the GC holds: if the working set grows and these do not, the growth is native.
                    GC.GetGCMemoryInfo().TotalCommittedBytes, GC.GetGCMemoryInfo().HeapSizeBytes, proc.PrivateMemorySize64,
                    // HeapBytes counts free space between large objects too (a heap dump held 16.5 MB where it read 92 MB):
                    // fragmentation after the sample's full GC, so growth in HeapBytes can be told apart from reachable growth.
                    GC.GetGCMemoryInfo().FragmentedBytes));
                Assert.All(ids, id => Assert.Contains(snaps, s => s.MonitorId == id)); // every monitor still ticks
            }
            if (p2Key != null && minutes >= 10.5) p2 = await host.Rpc.MonitorKillPreview(new MonitorSessionParams { MonitorId = p2Monitor!, Key = p2Key });
        }
        finally
        {
            await writer.DisposeAsync();
            Exception? loadError = null;
            foreach (var l in loads)
            {
                l.Stop.Cancel();
                try { await l.Load.WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception e) { loadError ??= e; }
                await l.Holder.DisposeAsync();
            }
            foreach (var id in ids) await host.Rpc.MonitorStop(new MonitorRef { MonitorId = id });
            await host.DisposeAsync();
            if (loadError != null) throw new InvalidOperationException("The active load did not stop cleanly", loadError);
        }
        await Task.Delay(3_000);
        var leftMs = await MonitorConnectionsAsync(Engine.Mssql);
        var leftPg = await MonitorConnectionsAsync(Engine.Pg);

        // After the history buffers are full (HistoryMinutes) and JIT/caches warmed: growth from here on would be a leak.
        var warm = samples.Where(x => x.Minute >= HistoryMinutes + 1.5).DefaultIfEmpty(samples.Last()).ToList();
        var result = new
        {
            date = DateTime.UtcNow, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, cores = Environment.ProcessorCount,
            minutes, monitors = ids.Count, intervalMs = 1_000, historyMinutes = HistoryMinutes, load = "MonitorBench.ActiveLoadAsync on both engines (8 churn workers, new SQL text per statement, a blocking pair)",
            samples,
            heapGrowthBytes = warm.Last().HeapBytes - warm.First().HeapBytes,
            workingSetGrowthBytes = warm.Last().WorkingSetBytes - warm.First().WorkingSetBytes,
            threadGrowth = warm.Last().Threads - warm.First().Threads,
            leftOnServer = new { mssql = leftMs, pg = leftPg },
            p2PostgresLongWriteTx = p2 == null ? null : new { risk = p2.Risk.ToString(), txElapsedMs = p2.TxElapsedMs, hasWrites = p2.HasWrites },
        };
        var dir = Path.Combine(Root, "bench", "results");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{DateTime.UtcNow:yyyy-MM-dd-HH-mm-ss}-monitor-soak-{(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux")}.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        Assert.All(samples, s => { Assert.Equal(2, s.MssqlMonitorConns); Assert.Equal(2, s.PgMonitorConns); }); // one per monitor, never more
        // No pane stays in error: a pane in error in more than 2 consecutive 30 s samples (over a minute) is a failure the
        // soak must not pass (defect 45: the deadlocks pane was in error in every sample of a 20-min soak that passed).
        foreach (var pane in samples.SelectMany(s => s.ErrorsByPane.Keys).Distinct())
        {
            var run = 0;
            foreach (var s in samples)
            {
                run = s.ErrorsByPane.TryGetValue(pane, out var why) ? run + 1 : 0;
                Assert.True(run <= 2, $"pane {pane} in error in {run} consecutive samples up to minute {s.Minute}: {why}");
            }
        }
        // Tick lag: plan §14 row 28 and C7 set p95 ≤ 200 ms. Checked once warm (the first samples include connect and JIT,
        // and each sample's p95 is over every monitor's last 1,000 lags).
        Assert.All(warm, s => Assert.True(s.LagP95Ms <= 200, $"tick lag p95 {s.LagP95Ms} ms at minute {s.Minute} (target ≤ 200 ms)"));
        Assert.True(result.heapGrowthBytes < 32L * 1024 * 1024, $"managed heap grew {result.heapGrowthBytes / 1024} KB");
        Assert.True(result.threadGrowth < 20, $"threads grew by {result.threadGrowth}");
        Assert.All(warm, s => Assert.True(s.PendingWork < 100, $"thread-pool backlog {s.PendingWork}"));
        Assert.True(warm.Max(s => s.Registry) <= warm.Min(s => s.Registry) + 20, "connection registry grew");
        Assert.Equal(0, leftMs);
        Assert.Equal(0, leftPg);
        if (minutes >= 10.5) Assert.True(p2?.Risk == RollbackRisk.High, $"P2: a {p2?.TxElapsedMs} ms write transaction read as {p2?.Risk}");
    }
}

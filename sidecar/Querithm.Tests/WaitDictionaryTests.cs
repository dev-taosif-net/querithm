using System.Diagnostics;
using Npgsql;
using Querithm.Contract;
using Querithm.Core.Monitoring;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>The offline wait-type dictionary (B3b, W8–W10).</summary>
public class WaitDictionaryTests
{
    static readonly WaitDictionary D = WaitDictionary.Load();

    [Fact]
    public void W8_entries_are_complete_and_cover_every_benign_and_notable_sql_server_wait()
    {
        foreach (var name in SqlServerWaits.Benign.Concat(SqlServerWaits.Notable))
            Assert.True(D.SqlServer.ContainsKey(name), $"missing SQL Server wait {name}");
        foreach (var (name, e) in D.SqlServer.Concat(D.Postgres))
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Meaning), name);
            Assert.NotEmpty(e.Causes);
            Assert.NotEmpty(e.Check);
            Assert.DoesNotMatch("<[A-Za-z/!]", e.Meaning); // plain text, no markup
            Assert.DoesNotContain("://", e.Meaning);         // no URLs (offline)
        }
        Assert.True(D.SqlServer.Count >= 150);
    }

    [Fact]
    public void W8_categories_match_the_providers_category_rules()
    {
        foreach (var (name, e) in D.SqlServer) Assert.True(SqlServerWaits.Category(name) == e.Category, $"{name}: {e.Category} vs {SqlServerWaits.Category(name)}");
        foreach (var (key, e) in D.Postgres)
        {
            var (t, ev) = PostgresWaits.Split(key);
            Assert.True(PostgresWaits.Category(t, ev) == e.Category, $"{key}: {e.Category} vs {PostgresWaits.Category(t, ev)}");
        }
        Assert.Equal("Log IO", D.Postgres["IO:WalWrite"].Category);   // PG 17 spelling
        Assert.Equal("Log IO", D.Postgres["IO:WALWrite"].Category);   // PG 12–16 spelling
        Assert.Equal("Parallelism", D.Postgres["IPC:BgworkerStartup"].Category);
    }

    [Fact]
    public void W9_an_unknown_wait_has_no_description_yet_and_never_fails()
    {
        var info = D.Info(Engine.Mssql, "NOT_A_REAL_WAIT_TYPE");
        Assert.False(info.Known);
        Assert.Equal("no description yet", info.Meaning);
        Assert.False(D.Info(Engine.Pg, "").Known);
        Assert.True(D.Info(Engine.Pg, "Lock:transactionid").Known);
        Assert.True(D.Info(Engine.Mssql, "lck_m_x").Known); // SQL Server names compare case-insensitively
    }

    [IntegrationFact]
    public async Task W8_every_wait_event_postgresql_17_reports_is_described()
    {
        await using var c = new NpgsqlConnection($"Host=127.0.0.1;Port=54330;Database=postgres;Username=postgres;Password={TestHost.PgPassword};Pooling=false");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type || ':' || name FROM pg_wait_events"; // PG 17+: the server's own list
        var missing = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) if (!D.Postgres.ContainsKey(r.GetString(0))) missing.Add(r.GetString(0));
        Assert.Empty(missing);
    }

    [IntegrationFact]
    public void W10_the_dictionary_loads_in_the_published_trimmed_sidecar()
    {
        // needs `node scripts/run.mjs build-sidecar --release` (minutes)
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        const string how = "run node scripts/run.mjs build-sidecar --release first";
        var publish = Path.Combine(root, "sidecar", "Querithm.Host", "bin", "publish");
        // A fresh checkout has no publish folder: say so instead of failing inside Directory.GetFiles.
        Assert.True(Directory.Exists(publish), $"No trimmed release publish: {publish} does not exist ({how}).");
        // This machine's RID only: a cross-RID publish (osx-arm64 from Linux, to measure its size) cannot run here.
        var rid = $"{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")}-{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        var exe = Directory.GetFiles(publish, OperatingSystem.IsWindows() ? "querithm-sidecar.exe" : "querithm-sidecar", SearchOption.AllDirectories)
            .Where(p => p.Contains($"{rid}-release", StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        Assert.True(exe != null, $"No trimmed release sidecar under {publish} ({how}).");
        var psi = new ProcessStartInfo(exe!, "--self-test") { RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30_000));
        // Each module's line first, so a failure names the module (D4: "oracle FAILED" once broke only the exit code).
        foreach (var module in new[] { "sqlite ok", "arrow ok", "mysql ok", "duckdb ok", "oracle ok" }) Assert.Contains(module, err);
        Assert.Equal(0, p.ExitCode);
        Assert.Contains($"waitDictionary sqlserver={D.SqlServer.Count} postgres={D.Postgres.Count}", err);
        Assert.Contains("monitorJson ok", err);    // the export's snapshot serialization survives trimming
        Assert.Contains("monitorHistory ok", err); // and so does the compressed history
    }
}

using System.Diagnostics;
using DuckDB.NET.Data;
using Querithm.Providers.DuckDb;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// DuckDB catalog reads (§8), completion audit 2026-11-25 M23: the stamps of a large catalog grow linearly. A guard, not a
/// reproduction: before the fix the ratio stayed under the bound (400 tables 39.5 ms, 3,200 tables 350 ms: 8.9×); after
/// it, 39.1 ms and 143 ms (this machine, 2026-11-25).
/// </summary>
[Collection("timing")] // a wall-clock ratio
public sealed class DuckDbMetadataTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static DuckDBConnection Open(int tables)
    {
        var c = new DuckDBConnection("Data Source=:memory:");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "BEGIN;\n" + string.Join("\n", Enumerable.Range(0, tables).Select(i =>
            $"CREATE TABLE t{i} ({string.Join(", ", Enumerable.Range(0, 10).Select(k => $"c{k} INTEGER"))}); CREATE INDEX ix{i} ON t{i} (c1);")) + "\nCOMMIT;";
        cmd.ExecuteNonQuery();
        return c;
    }

    static double Ms(Func<Task> work)
    {
        work().GetAwaiter().GetResult(); // warm
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            work().GetAwaiter().GetResult();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void Stamps_grow_linearly_with_the_number_of_tables()
    {
        var meta = new DuckDbMetadata();
        using var small = Open(400);
        using var big = Open(3200);
        var smallMs = Ms(() => meta.GetStampsAsync(small, CancellationToken.None));
        var bigMs = Ms(() => meta.GetStampsAsync(big, CancellationToken.None));
        output.WriteLine($"400 tables {smallMs:F1} ms, 3,200 tables {bigMs:F1} ms");
        Assert.Equal(3200, meta.GetStampsAsync(big, CancellationToken.None).Result.Count(s => s.Kind == Contract.ObjectKind.Table));
        // 8× the tables: linear work is about 8×, a scan of every entry per table about 64×.
        Assert.True(bigMs < smallMs * 20, $"400 tables {smallMs:F1} ms, 3,200 tables {bigMs:F1} ms");
    }
}

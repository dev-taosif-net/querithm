using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>meta.rowCounts (the ER diagram's "Load row counts") on both fixture engines.</summary>
[Collection("dataedit")]
public class RowCountsIntegrationTests
{
    const string Db = "querithm_edit";

    static async Task<long> ScalarAsync(TestHost h, string sid, string sql)
    {
        var (done, b, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        return b[0].Column(0) switch { Int32Array i => i.GetValue(0)!.Value, Int64Array l => l.GetValue(0)!.Value, var c => throw new InvalidOperationException(c.GetType().Name) };
    }

    [IntegrationFact]
    public async Task Sql_server_counts_come_from_partition_stats()
    {
        await using var h = new TestHost();
        var (_, master) = await h.OpenMssqlAsync("master");
        await h.RunAsync(master, $"IF DB_ID('{Db}') IS NULL CREATE DATABASE {Db}");
        var (_, sid) = await h.OpenMssqlAsync(Db);
        await h.RunAsync(sid, "DROP TABLE IF EXISTS dbo.q_rows; CREATE TABLE dbo.q_rows (id int PRIMARY KEY); INSERT dbo.q_rows SELECT TOP 37 ROW_NUMBER() OVER (ORDER BY (SELECT 1)) FROM sys.all_objects;");
        var id = await ScalarAsync(h, sid, "SELECT OBJECT_ID('dbo.q_rows')");
        var counts = await h.Rpc.MetaRowCounts(new RowCountsParams { SessionId = sid, ObjectIds = [id, 999999999] });
        var c = Assert.Single(counts);
        Assert.Equal((id, (long?)37, false), (c.ObjectId, c.Rows, c.Estimated));
    }

    [IntegrationFact]
    public async Task Postgres_counts_are_planner_estimates()
    {
        await using var h = new TestHost();
        var (_, postgres) = await h.OpenPgAsync("postgres");
        await h.RunAsync(postgres, "DROP TABLE IF EXISTS q_rows; CREATE TABLE q_rows (id int PRIMARY KEY); CREATE TABLE IF NOT EXISTS q_rows_never (id int);");
        var fresh = await ScalarAsync(h, postgres, "SELECT 'q_rows_never'::regclass::oid::bigint");
        await h.RunAsync(postgres, "INSERT INTO q_rows SELECT generate_series(1, 1000); ANALYZE q_rows;");
        var id = await ScalarAsync(h, postgres, "SELECT 'q_rows'::regclass::oid::bigint");
        var counts = await h.Rpc.MetaRowCounts(new RowCountsParams { SessionId = postgres, ObjectIds = [id, fresh] });
        var analyzed = Assert.Single(counts, c => c.ObjectId == id);
        Assert.Equal((1000L, true), (analyzed.Rows!.Value, analyzed.Estimated));
        // Never analyzed: reltuples is -1, reported as unknown rather than 0.
        Assert.Null(Assert.Single(counts, c => c.ObjectId == fresh).Rows);
        await h.RunAsync(postgres, "DROP TABLE q_rows; DROP TABLE q_rows_never;");
    }
}

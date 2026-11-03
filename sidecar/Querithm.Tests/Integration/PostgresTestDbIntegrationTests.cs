using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// The hand-written test database (scripts/fixture/pg-testdb.sql) through the PostgreSQL provider. Enable with QUERITHM_IT=1
/// after loading it with <c>node scripts/fixture/load-testdb.mjs</c>.
/// </summary>
[Collection("pg")]
public class PostgresTestDbIntegrationTests
{
    private const string Db = "querithm_test";

    private static async Task<(TestHost Host, Profile Profile, string SessionId)> OpenAsync()
    {
        var h = new TestHost();
        var (p, sid) = await h.OpenPgAsync();
        var (_, b, _) = await h.RunAsync(sid, $"SELECT count(*)::int FROM pg_database WHERE datname = '{Db}'");
        Assert.True(((Int32Array)b[0].Column(0)).GetValue(0) == 1, $"{Db} is missing: run node scripts/fixture/load-testdb.mjs");
        await h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = Db });
        return (h, p, sid);
    }

    [IntegrationFact]
    public async Task Seeded_data_is_deterministic()
    {
        var (h, _, sid) = await OpenAsync();
        await using var _h = h;
        var (done, b, _) = await h.RunAsync(sid, """
            SELECT (SELECT count(*) FROM sales.orders)::int, (SELECT count(*) FROM sales.order_lines)::int,
                   (SELECT count(*) FROM sales.customers)::int, (SELECT count(*) FROM sales.orders_default)::int
            """);
        Assert.Equal(ExecState.Done, done.State);
        Assert.Equal(new int?[] { 20_000, 50_000, 2_000, 0 }, Enumerable.Range(0, 4).Select(i => ((Int32Array)b[0].Column(i)).GetValue(0)));
    }

    [IntegrationFact]
    public async Task Type_zoo_rows_cross_ipc()
    {
        var (h, _, sid) = await OpenAsync();
        await using var _h = h;
        var (done, b, id) = await h.RunAsync(sid, "SELECT * FROM public.type_zoo ORDER BY id");
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        Assert.Equal(3, b.Sum(x => x.Length));
        var batch = b[0];
        // Long-text columns travel with a "<name>__truncated" flag column; the table itself has 38 columns.
        var data = Enumerable.Range(0, batch.ColumnCount).Where(i => !batch.Schema.GetFieldByIndex(i).Name.EndsWith("__truncated")).ToList();
        Assert.Equal(38, data.Count);
        // Row 2 is all NULL apart from the key.
        Assert.All(data.Skip(1), i => Assert.True(batch.Column(i).IsNull(1), batch.Schema.GetFieldByIndex(i).Name));
        // A user composite (no CLR mapping in Npgsql) reads as psql text instead of failing the whole query.
        var composite = (StringArray)batch.Column(batch.Schema.GetFieldIndex("c_composite"));
        Assert.Equal("(\"1 Main St\",,Dhaka,1205,BD)", composite.GetString(0));
        // Unconstrained numeric keeps NaN and values wider than System.Decimal.
        var numeric = (StringArray)batch.Column(batch.Schema.GetFieldIndex("c_numeric"));
        Assert.Equal("12345.678901234567890", numeric.GetString(0));
        Assert.Equal("NaN", numeric.GetString(2));
    }

    [IntegrationFact]
    public async Task Raised_errors_keep_their_hint_and_procedures_with_commit_run()
    {
        var (h, _, sid) = await OpenAsync();
        await using var _h = h;
        var (failed, _, _) = await h.RunAsync(sid, "SELECT public.fail_with('boom')");
        Assert.Equal(ExecState.Failed, failed.State);
        Assert.Contains("boom", failed.Error!.Message);

        var (done, _, id) = await h.RunAsync(sid, "CALL inventory.restock(4::smallint)");
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        Assert.Contains(h.Messages(id), m => m.Contains("restocked"));
    }

    [IntegrationFact]
    public async Task Metadata_sees_quoted_schemas_partitions_and_overloads()
    {
        var (h, p, _) = await OpenAsync();
        await using var _h = h;
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = Db, Full = true });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, Db);
        var snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = Db });
        Assert.Contains(snap.Objects, o => o.Schema == "Reporting Mixed" && o.Name == "Monthly Summary");
        Assert.Contains(snap.Objects, o => o.Schema == "sales" && o.Name == "orders");
        Assert.Contains(snap.Objects, o => o.Schema == "sales" && o.Name == "monthly_revenue");
        Assert.Equal(2, snap.Objects.Count(o => o.Schema == "public" && o.Name == "add" && o.Kind == ObjectKind.Func));
        var emp = Assert.Single(snap.Objects, o => o.Schema == "hr" && o.Name == "employees");
        Assert.Contains(emp.Columns!, c => c.Name == "employee_id" && c.Pk && c.Identity);
    }
}

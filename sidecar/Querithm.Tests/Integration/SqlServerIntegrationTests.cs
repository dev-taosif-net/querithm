using System.Diagnostics;
using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>Runs against the docker fixture (scripts/bench/docker/compose.yml). Enable with QUERITHM_IT=1.</summary>
[Collection("mssql")]
public class SqlServerIntegrationTests
{
    [IntegrationFact]
    public async Task Executes_multiple_result_sets_with_messages()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var (done, batches, id) = await h.RunAsync(sid, "SELECT 1 AS a, N'x' AS b; PRINT 'hello'; SELECT TOP 3 name FROM sys.objects ORDER BY name;");
        Assert.Equal(ExecState.Done, done.State);
        Assert.Equal(2, done.ResultSetCount);
        Assert.Equal(2, batches.Count);
        Assert.Equal(1, ((Int32Array)batches[0].Column(0)).GetValue(0));
        Assert.Contains("hello", h.Messages(id));
    }

    [IntegrationFact]
    public async Task Statements_without_semicolons_run_as_separate_statements()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var (done, batches, id) = await h.RunAsync(sid, "SELECT * FROM sys.schemas sch\nSELECT * FROM sys.objects obj");
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        Assert.Equal(2, done.ResultSetCount);
        Assert.NotNull(batches[0].Schema.GetFieldByName("schema_id"));
        Assert.NotNull(batches[^1].Schema.GetFieldByName("object_id"));
    }

    [IntegrationFact]
    public async Task Go_batches_run_in_order_and_errors_map_to_editor_lines()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var sql = "SELECT 1;\nGO\n\nSELECT 1/0;\nSELECT 2;\nGO\nSELECT 3";
        var (done, batches, id) = await h.RunAsync(sid, sql);
        Assert.Equal(ExecState.Failed, done.State);
        Assert.Equal(4, done.Error!.EditorLine);
        Assert.Equal(8134, done.Error.Number);
        // SSMS semantics: statement after the error and the next batch still ran.
        Assert.Equal(3, batches.Count);
    }

    [IntegrationFact]
    public async Task Row_limit_truncates_and_reports()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var (done, batches, _) = await h.RunAsync(sid, "SELECT TOP 30000 a.object_id FROM sys.all_objects a CROSS JOIN sys.all_objects b", new ExecuteOptions { MaxRows = 25_000 });
        Assert.True(done.Truncated);
        Assert.Equal(25_000, batches.Sum(b => b.Length));
        Assert.Equal(new[] { 10_000, 10_000, 5_000 }, batches.Select(b => b.Length));
    }

    [IntegrationFact]
    public async Task Cancel_stops_a_long_query_quickly()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "WAITFOR DELAY '00:01:00'" });
        await Task.Delay(500);
        var sw = Stopwatch.StartNew();
        Assert.True((await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! })).Cancelled);
        var done = await h.Notifier.WaitDone(r.ExecutionId!, 10_000);
        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.True(sw.ElapsedMilliseconds < 500, $"cancel took {sw.ElapsedMilliseconds} ms (bench metric 15)");
        // The session is usable afterwards.
        var (again, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Equal(ExecState.Done, again.State);
    }

    [IntegrationFact]
    public async Task Transactions_pin_a_connection_and_text_commit_unpins()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync("tempdb");
        await h.RunAsync(sid, "IF OBJECT_ID('dbo.q_tx') IS NULL CREATE TABLE dbo.q_tx (id int)");
        var st = await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        Assert.Equal(TxState.Open, st.TxState);
        Assert.True(st.Pinned);
        await h.RunAsync(sid, "INSERT dbo.q_tx VALUES (1)");
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
        var (_, b, _) = await h.RunAsync(sid, "SELECT COUNT(*) FROM dbo.q_tx");
        Assert.Equal(0, ((Int32Array)b[0].Column(0)).GetValue(0));

        // BEGIN TRAN typed in the editor pins too; COMMIT text releases.
        await h.RunAsync(sid, "BEGIN TRAN; INSERT dbo.q_tx VALUES (2);");
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.SessionClose(new SessionCloseParams { SessionId = sid }));
        await h.RunAsync(sid, "COMMIT");
        var final = await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid });
        Assert.Equal(TxState.None, final.TxState);
        Assert.False(final.Pinned);
        await h.RunAsync(sid, "DROP TABLE dbo.q_tx");
    }

    [IntegrationFact]
    public async Task Change_database_is_one_roundtrip_and_use_updates_session()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync("master");
        await h.RunAsync(sid, "SELECT 1"); // warm the pool
        var r = await h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = "tempdb" });
        Assert.Equal("tempdb", r.Database);
        Assert.True(r.ElapsedMs < 200, $"changeDatabase took {r.ElapsedMs} ms");
        var (done, _, _) = await h.RunAsync(sid, "USE msdb;");
        Assert.Equal("msdb", done.Database);
        Assert.Equal("msdb", (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).Database);
    }

    [IntegrationFact]
    public async Task Types_map_to_arrow()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var (done, b, id) = await h.RunAsync(sid, """
            SELECT CAST(12.34 AS decimal(18,2)) d, CAST(1 AS bit) bt, CAST('2026-10-19T14:22:01.4020000' AS datetime2) dt2,
                   CAST('2026-10-19T14:22:01+05:30' AS datetimeoffset) dto, CAST('2026-10-19' AS date) dd, CAST('13:45:00' AS time) tt,
                   NEWID() g, CAST(0x0102 AS varbinary(10)) vb, REPLICATE(CAST(N'x' AS nvarchar(max)), 1100000) big,
                   CAST(12345678901234567890123456789012.123456 AS decimal(38,6)) wide, CAST(N'<a/>' AS xml) x, CAST(1 AS sql_variant) v,
                   geography::Point(47.65, -122.34, 4326) geo, CAST(1.5 AS money) m
            """);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        var batch = b[0];
        Assert.Equal(12.34m, ((Decimal128Array)batch.Column("d")).GetValue(0));
        Assert.True(((BooleanArray)batch.Column("bt")).GetValue(0));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 14, 22, 1, 402, TimeSpan.Zero), ((TimestampArray)batch.Column("dt2")).GetTimestamp(0));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 8, 52, 1, TimeSpan.Zero), ((TimestampArray)batch.Column("dto")).GetTimestamp(0));
        Assert.Equal((short)330, ((Int16Array)batch.Column("dto__offset")).GetValue(0));
        Assert.True(((BooleanArray)batch.Column("big__truncated")).GetValue(0));
        Assert.Equal(1024 * 1024, ((StringArray)batch.Column("big")).GetString(0).Length);
        var cell = await h.Rpc.QueryCell(new CellParams { ExecutionId = id, ResultIndex = 0, Row = 0, Col = 8 });
        Assert.Equal(1_100_000, cell.Value!.Length);
        Assert.Equal("12345678901234567890123456789012.123456", ((Decimal128Array)batch.Column("wide")).GetString(0));
        Assert.StartsWith("0x", ((StringArray)batch.Column("geo")).GetString(0));
        Assert.Equal(1.5m, ((Decimal128Array)batch.Column("m")).GetValue(0));
    }

    [IntegrationFact]
    public async Task Statistics_io_and_time_are_parsed()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var (done, _, _) = await h.RunAsync(sid, "SELECT COUNT(*) FROM sys.objects", new ExecuteOptions { StatisticsIo = true, StatisticsTime = true });
        Assert.NotNull(done.Statistics);
        Assert.Contains(done.Statistics!, s => s.Kind == "time");
        Assert.NotNull(done.ServerElapsedMs);
    }

    [IntegrationFact]
    public async Task Actual_plan_arrives_as_plan_notification()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var (done, batches, id) = await h.RunAsync(sid, "SELECT TOP 10 o.name FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id", new ExecuteOptions { ActualPlan = true });
        Assert.Equal(ExecState.Done, done.State);
        Assert.Single(batches);
        var plan = Assert.Single(h.Notifier.Of<QueryPlanEvent>(), p => p.ExecutionId == id);
        Assert.NotNull(plan.Plan.Statements[0].Root);
        Assert.NotNull(plan.Plan.Statements[0].Root!.ActualRows);
        var est = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT * FROM sys.objects WHERE name = 'x'", Mode = PlanMode.Estimated });
        Assert.NotNull(est.Tree.Statements[0].Root);
    }
}

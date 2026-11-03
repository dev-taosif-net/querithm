using Apache.Arrow;
using Querithm.Contract;
using Querithm.Core;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// plan.get on the in-process engines (H1), against real DuckDB and SQLite files: an actual plan leaves no change behind,
/// one inside an open DuckDB transaction is refused with the reason (DuckDB has no savepoints), and the query guard —
/// file-policy refusals, Prod first run, destructive confirm — applies to plans as to query.execute. The DuckDB delete
/// case failed before the fix: EXPLAIN ANALYZE ran in autocommit and the row was gone.
/// </summary>
public class PlanGuardFileEngineTests
{
    static long Count(List<RecordBatch> batches) => batches[0].Column(0) switch
    {
        Int64Array a => a.GetValue(0)!.Value,
        Int32Array a => a.GetValue(0)!.Value,
        StringArray a => long.Parse(a.GetString(0)!, System.Globalization.CultureInfo.InvariantCulture), // SQLite: dynamic columns travel as text
        var other => throw new InvalidOperationException(other.GetType().Name),
    };

    [Fact]
    public async Task A_duckdb_actual_plan_of_a_delete_leaves_the_rows()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t AS SELECT range AS x FROM range(3);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);

        var plan = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Actual, ConfirmedRun = true });

        Assert.Equal(ExecState.Done, plan.Done.State);
        Assert.NotEmpty(plan.Tree.Statements);
        Assert.Equal(3, Count((await h.RunAsync(sid, "SELECT count(*) FROM t")).Batches));
        Assert.Equal(TxState.None, h.Services.Sessions.Get(sid).TxState);
    }

    [Fact]
    public async Task A_duckdb_actual_plan_inside_an_open_transaction_is_refused_with_the_reason_and_keeps_it()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t AS SELECT range AS x FROM range(3);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        Assert.Null((await h.RunAsync(sid, "INSERT INTO t VALUES (10)")).Done.Error);

        var ex = await Assert.ThrowsAsync<QuerithmException>(() =>
            h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Actual, ConfirmedRun = true }));

        Assert.Equal("unsupported", ex.Code);
        Assert.Contains("no savepoints", ex.Message);
        Assert.Equal(TxState.Open, h.Services.Sessions.Get(sid).TxState);
        Assert.Equal(4, Count((await h.RunAsync(sid, "SELECT count(*) FROM t")).Batches)); // the user's insert is still there
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
        Assert.Equal(3, Count((await h.RunAsync(sid, "SELECT count(*) FROM t")).Batches));
    }

    [Fact]
    public async Task A_duckdb_estimated_plan_works_inside_an_open_transaction()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t AS SELECT range AS x FROM range(3);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var plan = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Estimated });
        Assert.Equal(ExecState.Done, plan.Done.State);
        Assert.Equal(TxState.Open, h.Services.Sessions.Get(sid).TxState);
        Assert.Equal(3, Count((await h.RunAsync(sid, "SELECT count(*) FROM t")).Batches));
    }

    [Fact]
    public async Task A_duckdb_plan_on_prod_is_guarded_and_the_file_policy_refuses_what_it_refuses_for_queries()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t AS SELECT range AS x FROM range(3);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite, EnvironmentKind.Prod);
        var p = new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Actual, ConfirmedRun = true };
        Assert.Equal(GuardReason.ProdFirstRun, (await h.Rpc.PlanGet(p)).Blocked?.Reason);
        Assert.Equal(GuardReason.Destructive, (await h.Rpc.PlanGet(p with { ConfirmedProd = true })).Blocked?.Reason);
        // A refused function stays refused under EXPLAIN (it would run under EXPLAIN ANALYZE), confirmed or not.
        var refused = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanGet(p with
        {
            Sql = "SELECT * FROM query('SELECT 1')", ConfirmedProd = true, ConfirmedDestructive = true,
        }));
        Assert.Equal("refused", refused.Code);
        var plan = await h.Rpc.PlanAsync(h.Notifier, p with { ConfirmedProd = true, ConfirmedDestructive = true });
        Assert.Equal(ExecState.Done, plan.Done.State);
        Assert.Equal(3, Count((await h.RunAsync(sid, "SELECT count(*) FROM t", new ExecuteOptions { ConfirmedProd = true })).Batches));
    }

    [Fact]
    public async Task Cancel_ends_a_duckdb_actual_plan_and_rolls_it_back()
    {
        await using var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t AS SELECT range AS x FROM range(3);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite);
        const string slow = "DELETE FROM t WHERE x IN (SELECT a.range FROM range(100000000) a, range(1000) b WHERE a.range + b.range < 0)";
        var r = await h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = slow, Mode = PlanMode.Actual, ConfirmedRun = true });
        await Task.Delay(300);
        Assert.True((await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! })).Cancelled);
        var done = await h.Notifier.WaitDone(r.ExecutionId!, 15_000);
        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.Equal(TxState.None, h.Services.Sessions.Get(sid).TxState);
        Assert.Equal(3, Count((await h.RunAsync(sid, "SELECT count(*) FROM t")).Batches));
    }

    [Fact]
    public async Task A_sqlite_plan_on_prod_is_guarded_and_never_runs_the_statement()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v); INSERT INTO t VALUES (1, 'a'), (2, 'b');");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite, EnvironmentKind.Prod);
        var p = new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Estimated };
        Assert.Equal(GuardReason.ProdFirstRun, (await h.Rpc.PlanGet(p)).Blocked?.Reason);
        Assert.Equal(GuardReason.Destructive, (await h.Rpc.PlanGet(p with { ConfirmedProd = true })).Blocked?.Reason);
        var plan = await h.Rpc.PlanAsync(h.Notifier, p with { ConfirmedProd = true, ConfirmedDestructive = true });
        Assert.Equal(ExecState.Done, plan.Done.State);
        Assert.Equal(2, Count((await h.RunAsync(sid, "SELECT count(*) FROM t", new ExecuteOptions { ConfirmedProd = true })).Batches));
    }

    [Fact]
    public async Task A_sqlite_actual_plan_and_a_policy_refusal_are_refused_before_anything_runs()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase();
        var (_, sid) = await h.OpenFileAsync(path);
        var actual = await Assert.ThrowsAsync<QuerithmException>(() =>
            h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = "SELECT * FROM t", Mode = PlanMode.Actual, ConfirmedRun = true }));
        Assert.Equal("unsupported", actual.Code);
        Assert.Contains("no actual plans", actual.Message);
        var attach = await Assert.ThrowsAsync<QuerithmException>(() =>
            h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = "ATTACH DATABASE 'other.db' AS o", Mode = PlanMode.Estimated }));
        Assert.Equal("refused", attach.Code);
    }
}

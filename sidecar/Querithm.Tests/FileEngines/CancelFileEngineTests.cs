using Querithm.Contract;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// In-process engines degrade the cancel escalation (§7 step 4): their connection is never closed under a running native
/// command (unsafe), and their interrupt stops the command, so Cancel alone ends the run.
/// </summary>
public class CancelFileEngineTests
{
    static async Task AssertCancelEndsItWithoutClosing(Func<QueryExecuteParams, Task<QueryExecuteResult>> execute, Querithm.Core.Execution.ExecutionService executions,
        CapturingNotifier notifier, string sid, string endless)
    {
        var r = await execute(new QueryExecuteParams { SessionId = sid, Sql = endless });
        await Task.Delay(500); // running inside the engine
        Assert.False(executions.CloseConnectionOf(r.ExecutionId!));
        Assert.True(executions.Cancel(r.ExecutionId!).Cancelled);
        Assert.Equal(ExecState.Cancelled, (await notifier.WaitDone(r.ExecutionId!, 10_000)).State);
    }

    [Fact]
    public async Task Sqlite_is_never_closed_mid_command_and_cancel_alone_stops_it()
    {
        await using var h = new SqliteHost();
        var (_, sid) = await h.OpenMemoryAsync();
        await AssertCancelEndsItWithoutClosing(h.Rpc.QueryExecute, h.Services.Executions, h.Notifier, sid,
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c");
        var (next, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Equal(ExecState.Done, next.State);
    }

    [Fact]
    public async Task DuckDb_is_never_closed_mid_command_and_cancel_alone_stops_it()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenMemoryAsync();
        await AssertCancelEndsItWithoutClosing(h.Rpc.QueryExecute, h.Services.Executions, h.Notifier, sid,
            "SELECT count(*) FROM range(100000000000)");
        var (next, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Equal(ExecState.Done, next.State);
    }
}

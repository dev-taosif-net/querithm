using System.Diagnostics;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// The cancel escalation against the real drivers (§7 step 4). SQL Server: closing the connection of a run parked on the
/// server ends the run promptly, frees the tab and says an open transaction is gone (a real server always acknowledges a
/// cancel, so the step is called directly). PostgreSQL and MySQL are never closed mid-command — their drivers' Close is no
/// abort (Npgsql drains the command; MySqlConnector breaks the pending call) and both break a connection whose cancel is
/// not acknowledged after their own CancellationTimeout — and Cancel alone ends the run.
/// </summary>
public class CancelEscalationIntegrationTests
{
    static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    static async Task<string> StartParkedAsync(Func<QueryExecuteParams, Task<QueryExecuteResult>> execute, string sid, string sql)
    {
        var r = await execute(new QueryExecuteParams { SessionId = sid, Sql = sql });
        await Task.Delay(700); // the statement is running on the server
        return r.ExecutionId!;
    }

    static async Task AssertEndsPromptly(CapturingNotifier notifier, string id, Stopwatch since, ExecState? state = null)
    {
        var done = await notifier.WaitDone(id, 15_000);
        Assert.True(since.Elapsed < Prompt, $"ended after {since.Elapsed.TotalMilliseconds:0} ms");
        if (state is { } s) Assert.Equal(s, done.State);
        else Assert.NotEqual(ExecState.Done, done.State);
    }

    [IntegrationFact]
    public async Task SqlServer_closing_the_connection_of_a_run_parked_on_the_server_ends_it_and_the_tab_runs_again()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();

        var id = await StartParkedAsync(h.Rpc.QueryExecute, sid, "WAITFOR DELAY '00:00:30'");
        var since = Stopwatch.StartNew();
        Assert.True(h.Services.Executions.CloseConnectionOf(id));
        await AssertEndsPromptly(h.Notifier, id, since);

        var (next, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Equal(ExecState.Done, next.State);
    }

    [IntegrationFact]
    public async Task SqlServer_closing_the_connection_of_a_run_inside_a_transaction_unpins_the_session_and_says_the_transaction_is_gone()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);

        var id = await StartParkedAsync(h.Rpc.QueryExecute, sid, "WAITFOR DELAY '00:00:30'");
        var since = Stopwatch.StartNew();
        Assert.True(h.Services.Executions.CloseConnectionOf(id));
        await AssertEndsPromptly(h.Notifier, id, since);

        Assert.Contains(h.Messages(id), m => m.Contains("any open transaction was rolled back"));
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        var (next, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Equal(ExecState.Done, next.State);
    }

    [IntegrationFact]
    public async Task Postgres_is_never_closed_mid_command_and_cancel_alone_ends_the_run()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();

        var id = await StartParkedAsync(h.Rpc.QueryExecute, sid, "SELECT pg_sleep(30)");
        Assert.False(h.Services.Executions.CloseConnectionOf(id));
        var since = Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = id });
        await AssertEndsPromptly(h.Notifier, id, since, ExecState.Cancelled);
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task MySql_is_never_closed_mid_command_and_cancel_alone_ends_the_run(string product)
    {
        var (h, _, sid) = await MySqlIntegrationTests.OpenAsync(product);
        await using var _ = h;

        var id = await StartParkedAsync(h.Rpc.QueryExecute, sid, "SELECT SLEEP(30)");
        Assert.False(h.Services.Executions.CloseConnectionOf(id));
        var since = Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = id });
        await AssertEndsPromptly(h.Notifier, id, since, ExecState.Cancelled);
    }
}

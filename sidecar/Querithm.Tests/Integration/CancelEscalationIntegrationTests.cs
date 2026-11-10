using System.Diagnostics;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// The cancel escalation against the real drivers (§7 step 4). SQL Server: closing the connection of a run parked on the
/// server ends the run promptly, frees the tab and says an open transaction is gone (a real server always acknowledges a
/// cancel, so the step is called directly). PostgreSQL, MySQL and Oracle are never closed mid-command — their drivers' Close
/// is no abort (Npgsql drains the command; MySqlConnector breaks the pending call; ODP.NET's Close waits for the running
/// call) — and Cancel alone ends the run.
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

    /// <summary>D4 S03: ODP.NET's Close waits for the running call (measured), so Oracle joins PostgreSQL and MySQL.</summary>
    [IntegrationFact]
    public async Task Oracle_is_never_closed_mid_command_and_cancel_alone_ends_the_run()
    {
        await using var h = new TestHost(new Querithm.Providers.Oracle.OracleProvider(Directory.CreateTempSubdirectory("qora-cancel-").FullName));
        var profile = await h.Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "ora", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password,
            Username = OracleIntegrationTests.AppUser, DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable,
        });
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = profile.Id, Secret = FixtureSecrets.Password("oracle") })).SessionId;

        // A busy block: the break lands at once (measured 4 ms). DBMS_SESSION.SLEEP notices it only between its sleep
        // intervals (measured 4.5 s), which OracleProviderIntegrationTests covers with its own bound.
        var id = await StartParkedAsync(h.Rpc.QueryExecute, sid, "DECLARE n NUMBER := 0; BEGIN LOOP n := n + 1; END LOOP; END;");
        Assert.False(h.Services.Executions.CloseConnectionOf(id));
        var since = Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = id });
        await AssertEndsPromptly(h.Notifier, id, since, ExecState.Cancelled);
        var (next, _, _) = await h.RunAsync(sid, "SELECT 1 FROM dual");
        Assert.Equal(ExecState.Done, next.State);
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

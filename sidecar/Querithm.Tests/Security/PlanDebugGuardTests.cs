using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.TSql;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 7 for every RPC that runs caller SQL (H1): plan.get and debug.attach are guarded like query.execute — the
/// refusal, the first run on Prod, the destructive confirm — and answer the same <c>{blocked}</c>. Each engine's real
/// classifier and plan provider run over fake connections, so the commands the "server" saw are asserted. Before the fix
/// every blocked case here ran (plan.get checked only ConfirmedRun; debug.attach nothing).
/// </summary>
public class PlanDebugGuardTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    const string PgPlanJson = """[{"Plan": {"Node Type": "Delete", "Total Cost": 1.5, "Plans": [{"Node Type": "Seq Scan", "Total Cost": 1.0}]}}]""";
    const string MySqlPlanJson = """{"query_block": {"select_id": 1, "table": {"table_name": "t", "access_type": "ALL"}}}""";

    /// <summary>A fake server for <paramref name="engine"/>: plans answer that engine's EXPLAIN shape, everything else one row.</summary>
    static TestHostWithFake Host(Engine engine)
    {
        var h = new TestHostWithFake();
        switch (engine)
        {
            case Engine.Mssql: h.Provider.As(Engine.Mssql, new TSqlLanguageService(), new SqlServerPlans()); break;
            case Engine.Pg: h.Provider.As(Engine.Pg, new PgLanguageService(), new PostgresPlans(), new FakePgTransactions()); break;
            case Engine.MySql: h.Provider.As(Engine.MySql, new MySqlLanguageService(), new MySqlPlans()); break;
        }
        h.Provider.Handler = (cmd, _) => Task.FromResult(cmd.CommandText.Trim() switch
        {
            var t when t.StartsWith("EXPLAIN", StringComparison.Ordinal) => FakeResult.Value(engine == Engine.MySql ? MySqlPlanJson : PgPlanJson),
            var t when engine == Engine.Mssql && t.Contains("DELETE", StringComparison.Ordinal) =>
                new FakeResult([FakeResult.Table([("Microsoft SQL Server 2005 XML Showplan", typeof(string))], [PlanParseRpcTests.ShowPlan()])]),
            _ => FakeResult.Rows(("a", typeof(int))),
        });
        return h;
    }

    static async Task<(Profile Profile, string SessionId)> Open(TestHostWithFake h, Engine engine, EnvironmentKind env = EnvironmentKind.Prod)
    {
        var p = await h.Rpc.ProfileSave(new Profile
        {
            Id = "", Name = "billing", Engine = engine, Host = "db.internal", Auth = AuthKind.Sql, Username = "u", Environment = env,
        });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        return (p, s.SessionId);
    }

    static IEnumerable<string> Commands(TestHostWithFake h) => h.Provider.Opened.SelectMany(c => c.Log);

    public static TheoryData<Engine, PlanMode> GuardedPlans => new()
    {
        { Engine.Mssql, PlanMode.Estimated },
        { Engine.Pg, PlanMode.Estimated },
        { Engine.Pg, PlanMode.Actual },
        { Engine.MySql, PlanMode.Estimated },
    };

    [Theory]
    [MemberData(nameof(GuardedPlans))]
    public async Task A_plan_of_a_delete_on_prod_is_blocked_until_both_confirms_then_runs(Engine engine, PlanMode mode)
    {
        await using var h = Host(engine);
        var (_, sid) = await Open(h, engine);
        var p = new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = mode, ConfirmedRun = mode == PlanMode.Actual };

        var first = await h.Rpc.PlanGet(p);
        Assert.Equal(GuardReason.ProdFirstRun, first.Blocked?.Reason);
        Assert.Null(first.ExecutionId);
        var second = await h.Rpc.PlanGet(p with { ConfirmedProd = true });
        Assert.Equal(GuardReason.Destructive, second.Blocked?.Reason);
        Assert.Contains(second.Blocked!.Findings, f => f.Rule == "deleteWithoutWhere");
        Assert.Empty(Commands(h)); // nothing reached the server while blocked

        var run = await h.Rpc.PlanAsync(h.Notifier, p with { ConfirmedProd = true, ConfirmedDestructive = true });
        Assert.Equal(ExecState.Done, run.Done.State);
        Assert.NotEmpty(run.Tree.Statements);
        Assert.Contains(Commands(h), c => engine == Engine.Mssql ? c == "SET SHOWPLAN_XML ON" : c.StartsWith("EXPLAIN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_actual_plan_of_a_write_on_mysql_is_refused_before_anything_runs()
    {
        await using var h = Host(Engine.MySql);
        var (_, sid) = await Open(h, Engine.MySql, EnvironmentKind.Dev);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() =>
            h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Actual, ConfirmedRun = true }));
        Assert.Equal("unsupported", ex.Code);
        Assert.Contains("only SELECT and TABLE", ex.Message);
        Assert.Empty(Commands(h));
    }

    [Theory]
    [InlineData("SET SHOWPLAN_XML OFF\nGO\nDELETE FROM t")]
    [InlineData("SET SHOWPLAN_ALL OFF; SELECT 1")]
    [InlineData("set showplan_text off")]
    [InlineData("SET STATISTICS XML ON\nGO\nSELECT 1")]
    [InlineData("SET STATISTICS PROFILE ON; SELECT 1")]
    [InlineData("SET SHOWPLAN_XML OFF\nGO\nDELETE FROM t WHERE")] // unparsable, with a SET in it
    public async Task SqlServer_plan_input_that_switches_the_plan_options_is_refused(string sql)
    {
        await using var h = Host(Engine.Mssql);
        var (_, sid) = await Open(h, Engine.Mssql, EnvironmentKind.Dev);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = sql, Mode = PlanMode.Estimated }));
        Assert.Equal("unsupported", ex.Code);
        Assert.Empty(Commands(h)); // SHOWPLAN never went on, so nothing could run after an OFF
    }

    [Fact]
    public async Task SqlServer_plans_statistics_io_and_time_and_refuses_actual_plans_through_plan_get()
    {
        await using var h = Host(Engine.Mssql);
        var (_, sid) = await Open(h, Engine.Mssql, EnvironmentKind.Dev);
        var run = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SET STATISTICS IO ON;\nDELETE FROM t WHERE id = 1", Mode = PlanMode.Estimated });
        Assert.Equal(ExecState.Done, run.Done.State);
        Assert.Equal(["SET SHOWPLAN_XML ON", "SET STATISTICS IO ON;\nDELETE FROM t WHERE id = 1", "SET SHOWPLAN_XML OFF"], Commands(h).Where(c => c.StartsWith("SET", StringComparison.Ordinal)));
        var ex = await Assert.ThrowsAsync<QuerithmException>(() =>
            h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = "SELECT 1", Mode = PlanMode.Actual, ConfirmedRun = true }));
        Assert.Contains("Include actual plan", ex.Message);
    }

    [Fact]
    public async Task A_postgres_actual_plan_inside_an_open_transaction_uses_a_savepoint_and_keeps_the_transaction()
    {
        await using var h = Host(Engine.Pg);
        var (_, sid) = await Open(h, Engine.Pg, EnvironmentKind.Dev);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        var pinned = h.Services.Sessions.Get(sid).Pinned!;

        var run = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Actual, ConfirmedRun = true });

        Assert.Equal(ExecState.Done, run.Done.State);
        var log = ((FakeDbConnection)pinned.Db).Log.Where(c => !c.StartsWith("SELECT", StringComparison.Ordinal)).ToList();
        Assert.Equal(["BEGIN", "SAVEPOINT querithm_plan", "EXPLAIN (ANALYZE, BUFFERS, VERBOSE, FORMAT JSON) DELETE FROM t",
            "ROLLBACK TO SAVEPOINT querithm_plan", "RELEASE SAVEPOINT querithm_plan"], log);
        var s = h.Services.Sessions.Get(sid);
        Assert.Equal(TxState.Open, s.TxState); // the user's transaction and the tab's state are intact
        Assert.Same(pinned, s.Pinned);
    }

    [Fact]
    public async Task A_postgres_actual_plan_outside_a_transaction_runs_in_one_that_is_rolled_back()
    {
        await using var h = Host(Engine.Pg);
        var (_, sid) = await Open(h, Engine.Pg, EnvironmentKind.Dev);
        var run = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Actual, ConfirmedRun = true });
        Assert.Equal(ExecState.Done, run.Done.State);
        var log = Commands(h).Where(c => !c.StartsWith("SELECT", StringComparison.Ordinal)).ToList();
        Assert.Equal(["BEGIN", "EXPLAIN (ANALYZE, BUFFERS, VERBOSE, FORMAT JSON) DELETE FROM t", "ROLLBACK"], log);
        Assert.Equal(TxState.None, h.Services.Sessions.Get(sid).TxState);
    }

    sealed class InFailedTransaction() : DbException("current transaction is aborted, commands ignored until end of transaction block");

    [Fact]
    public async Task An_actual_plan_in_a_failed_postgres_transaction_is_refused_with_the_reason_and_runs_nothing()
    {
        await using var h = Host(Engine.Pg);
        var answer = h.Provider.Handler;
        h.Provider.Handler = (cmd, ct) => cmd.CommandText.StartsWith("SAVEPOINT querithm_plan", StringComparison.Ordinal) ? throw new InFailedTransaction() : answer(cmd, ct);
        var (_, sid) = await Open(h, Engine.Pg, EnvironmentKind.Dev);
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });

        var run = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "DELETE FROM t", Mode = PlanMode.Actual, ConfirmedRun = true });

        Assert.Equal(ExecState.Failed, run.Done.State);
        Assert.Contains("has failed", Assert.Single(run.Errors));
        Assert.DoesNotContain(Commands(h), c => c.StartsWith("EXPLAIN", StringComparison.Ordinal) || c.StartsWith("ROLLBACK", StringComparison.Ordinal));
        Assert.Equal(TxState.Open, h.Services.Sessions.Get(sid).TxState);
    }

    [Theory]
    [InlineData("BEGIN; DELETE FROM t")]
    [InlineData("COMMIT")]
    [InlineData("ROLLBACK")]
    public async Task An_actual_plan_of_text_that_ends_or_begins_a_transaction_is_refused(string sql)
    {
        await using var h = Host(Engine.Pg);
        var (_, sid) = await Open(h, Engine.Pg, EnvironmentKind.Dev);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = sql, Mode = PlanMode.Actual, ConfirmedRun = true }));
        Assert.Contains("begins or ends a transaction", ex.Message);
        Assert.Empty(Commands(h));
    }

    // ------------------------------------------------------------------ every RPC that runs caller SQL

    /// <summary>RPC methods whose params carry SQL text the sidecar runs, and how it is guarded.</summary>
    static readonly Dictionary<string, string> CallerSql = new()
    {
        ["query.execute"] = "QueryGuard.Check",
        ["plan.get"] = "QueryGuard.Check (plus the provider's plan refusal)",
        ["debug.attach"] = "QueryGuard.Check",
        ["dataCompare.start"] = "one SELECT only, run in a read-only transaction or checked SELECT-only per engine (IDataCompareDialect)",
    };

    /// <summary>
    /// A new RPC whose params take SQL text (<c>Sql</c>, <c>CallSql</c>, <c>Query</c>, also one level down) fails here
    /// until it is listed with its guard — the bypass H1 found in plan.get and debug.attach.
    /// </summary>
    [Fact]
    public void Every_rpc_that_takes_sql_text_is_listed_with_its_guard()
    {
        static bool CarriesSql(Type t, int depth) => t.GetProperties().Any(p =>
            p.PropertyType == typeof(string) && p.Name is "Sql" or "CallSql" or "Query"
            || depth < 1 && p.PropertyType.Namespace == typeof(PlanParams).Namespace && p.PropertyType.IsClass && CarriesSql(p.PropertyType, depth + 1));
        var methods = typeof(IQuerithmRpc).GetMethods()
            .Where(m => m.GetParameters() is [var param] && CarriesSql(param.ParameterType, 0))
            .Select(m => m.GetCustomAttributes(typeof(RpcMethodAttribute), false).Cast<RpcMethodAttribute>().Single().Name)
            .Order().ToArray();
        Assert.Equal(CallerSql.Keys.Order(), methods);
    }

    // ------------------------------------------------------------------ debug.attach

    /// <summary>Records what the debugger was asked to run; opens nothing.</summary>
    sealed class RecordingDebugger : IDebugProvider
    {
        public readonly List<string> Calls = [];
        public Task<CapabilityInfo> CheckAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(CapabilityInfo.Yes);

        public Task<IDebugSession> AttachAsync(Func<CancellationToken, Task<DbConnection>> open, long functionOid, string callSql, Action<DebugEvent> onEvent, string debugId, CancellationToken ct)
        {
            lock (Calls) Calls.Add(callSql);
            return Task.FromResult<IDebugSession>(new Nop());
        }

        sealed class Nop : IDebugSession
        {
            public Task SetBreakpointAsync(long functionOid, int line, CancellationToken ct) => Task.CompletedTask;
            public Task DropBreakpointAsync(long functionOid, int line, CancellationToken ct) => Task.CompletedTask;
            public Task StepAsync(DebugStepKind kind, CancellationToken ct) => Task.CompletedTask;
            public Task<string> GetSourceAsync(long functionOid, CancellationToken ct) => Task.FromResult("");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Debug_attach_with_a_destructive_call_is_blocked_on_prod_until_confirmed()
    {
        await using var h = Host(Engine.Pg);
        var debugger = new RecordingDebugger();
        h.Provider.Debugger = debugger;
        var (_, sid) = await Open(h, Engine.Pg);
        var p = new DebugAttachParams { SessionId = sid, FunctionOid = 42, CallSql = "DELETE FROM orders; SELECT public.recalc(1)" };

        Assert.Equal(GuardReason.ProdFirstRun, (await h.Rpc.DebugAttach(p)).Blocked?.Reason);
        var destructive = await h.Rpc.DebugAttach(p with { ConfirmedProd = true });
        Assert.Equal(GuardReason.Destructive, destructive.Blocked?.Reason);
        Assert.Null(destructive.DebugId);
        Assert.Empty(debugger.Calls); // no target connection ran the call

        var attached = await h.Rpc.DebugAttach(p with { ConfirmedProd = true, ConfirmedDestructive = true });
        Assert.NotNull(attached.DebugId);
        Assert.Equal([p.CallSql], debugger.Calls);
        await h.Rpc.DebugDetach(new DebugRef { DebugId = attached.DebugId! });
    }

    // ------------------------------------------------------------------ L1: the guard reads the stored profile

    [Fact]
    public async Task The_prod_guard_reads_the_stored_environment_and_a_changed_target_asks_again()
    {
        await using var h = Host(Engine.Mssql);
        var (profile, sid) = await Open(h, Engine.Mssql, EnvironmentKind.Dev);
        async Task<GuardReason?> Run(bool confirmed = false) =>
            (await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1", Options = new ExecuteOptions { ConfirmedProd = confirmed } })).Blocked?.Reason;

        Assert.Null(await Run());
        // Marked Prod while the tab is open: the tab's own snapshot still says Dev, the guard does not.
        var prod = await h.Rpc.ProfileSave(profile with { Environment = EnvironmentKind.Prod });
        Assert.Equal(GuardReason.ProdFirstRun, await Run());
        Assert.Null(await Run(confirmed: true));
        Assert.Null(await Run()); // confirmed once for this target

        // Another host is another target: the first-run confirm is asked again.
        var moved = await h.Rpc.ProfileSave(prod with { Host = "db2.internal" });
        Assert.Equal(GuardReason.ProdFirstRun, await Run());
        Assert.Null(await Run(confirmed: true));

        // Out of Prod and back: the confirm given before is gone.
        await h.Rpc.ProfileSave(moved with { Environment = EnvironmentKind.Dev });
        await h.Rpc.ProfileSave(moved);
        Assert.Equal(GuardReason.ProdFirstRun, await Run());
    }

    [Fact]
    public async Task The_guard_keeps_the_stored_profile_in_memory_and_reads_it_again_after_a_save_or_delete()
    {
        await using var h = Host(Engine.Mssql);
        var (profile, sid) = await Open(h, Engine.Mssql, EnvironmentKind.Dev);
        async Task<GuardReason?> Run() => (await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" })).Blocked?.Reason;
        Assert.Null(await Run());

        // Written behind ProfileStore's back: query.execute reads no store.db per run (8a review), so it is not seen ...
        using (var c = h.Services.Store.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE profile SET environment = 'prod'";
            cmd.ExecuteNonQuery();
        }
        Assert.Null(await Run());
        // ... while every save through the store drops the copy, so the edit guards the open tab at once.
        var prod = await h.Rpc.ProfileSave(profile with { Environment = EnvironmentKind.Prod });
        Assert.Equal(GuardReason.ProdFirstRun, await Run());
        // A deleted profile falls back to the tab's own snapshot (Dev) at once. (Deleting closes the tab, so the copy is
        // read through the session manager here.)
        var session = h.Services.Sessions.Get(sid);
        h.Services.Profiles.Delete(prod.Id);
        Assert.Equal(EnvironmentKind.Dev, h.Services.Sessions.EnvironmentOf(session));
    }

    [Fact]
    public async Task A_tab_opened_as_prod_stays_guarded_after_the_profile_is_marked_dev()
    {
        await using var h = Host(Engine.Mssql);
        var (profile, sid) = await Open(h, Engine.Mssql, EnvironmentKind.Prod);
        await h.Rpc.ProfileSave(profile with { Environment = EnvironmentKind.Dev });
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Equal(GuardReason.ProdFirstRun, r.Blocked?.Reason);
        Assert.Equal(EnvironmentKind.Prod, r.Blocked!.Environment);
    }

    [Fact]
    public async Task Another_database_on_a_prod_server_asks_for_the_first_run_again()
    {
        await using var h = Host(Engine.Mssql);
        var (_, sid) = await Open(h, Engine.Mssql);
        var ok = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1", Options = new ExecuteOptions { ConfirmedProd = true } });
        Assert.NotNull(ok.ExecutionId);
        await h.Notifier.WaitDone(ok.ExecutionId!, (int)Wait.TotalMilliseconds);
        await h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = "reporting" });
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT 1" });
        Assert.Equal(GuardReason.ProdFirstRun, r.Blocked?.Reason);
        Assert.Equal("reporting", r.Blocked!.Database);
    }
}

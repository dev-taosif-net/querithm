using System.Diagnostics;
using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>PostgreSQL provider against the docker fixture (pldebugger preloaded). Enable with QUERITHM_IT=1.</summary>
[Collection("pg")]
public class PostgresIntegrationTests
{
    [IntegrationFact]
    public async Task Executes_multiple_statements_with_notices_and_types()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        var (done, batches, id) = await h.RunAsync(sid, """
            DO $$ BEGIN RAISE NOTICE 'hello from plpgsql'; END $$;
            SELECT 1::int AS a, 'x'::text AS b, 12.34::numeric(10,2) AS n, 99.5::numeric AS free, now() AS ts, '2026-10-19'::date AS d,
                   interval '1 day 2 hours' AS iv, ARRAY[1,2,3] AS arr, '{"k":1}'::jsonb AS j, gen_random_uuid() AS u;
            SELECT generate_series(1, 25000) AS n;
            """);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        Assert.Contains("hello from plpgsql", h.Messages(id));
        Assert.Equal(2, done.ResultSetCount);
        var first = batches[0];
        Assert.Equal(1, ((Int32Array)first.Column("a")).GetValue(0));
        Assert.Equal(12.34m, ((Decimal128Array)first.Column("n")).GetValue(0));
        Assert.Equal("99.5", ((StringArray)first.Column("free")).GetString(0));
        Assert.Equal("1 day 02:00:00", ((StringArray)first.Column("iv")).GetString(0));
        Assert.Equal("{1,2,3}", ((StringArray)first.Column("arr")).GetString(0));
        Assert.Equal(25_000, batches.Skip(1).Sum(b => b.Length));
    }

    [IntegrationFact]
    public async Task Statements_without_semicolons_run_as_separate_statements()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        var (done, batches, id) = await h.RunAsync(sid, "SELECT ns.nspname FROM pg_catalog.pg_namespace ns\nSELECT cls.relname FROM pg_catalog.pg_class cls");
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        Assert.Equal(2, done.ResultSetCount);
        Assert.NotNull(batches[0].Schema.GetFieldByName("nspname"));
        Assert.NotNull(batches[^1].Schema.GetFieldByName("relname"));
    }

    [IntegrationFact]
    public async Task Errors_map_position_to_line()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        var (done, _, _) = await h.RunAsync(sid, "SELECT 1;\nSELECT 2;\nSELECT nope FROM pg_class;");
        Assert.Equal(ExecState.Failed, done.State);
        Assert.Equal(3, done.Error!.EditorLine);
        Assert.Contains("42703", done.Error.Message);
    }

    [IntegrationFact]
    public async Task Cancel_and_transactions_work()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT pg_sleep(60)" });
        await Task.Delay(400);
        var sw = Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
        Assert.Equal(ExecState.Cancelled, (await h.Notifier.WaitDone(r.ExecutionId!, 10_000)).State);
        Assert.True(sw.ElapsedMilliseconds < 500, $"cancel {sw.ElapsedMilliseconds} ms");

        await h.RunAsync(sid, "CREATE TABLE IF NOT EXISTS q_tx (id int)");
        await h.RunAsync(sid, "BEGIN; INSERT INTO q_tx VALUES (1);");
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
        var (_, b, _) = await h.RunAsync(sid, "SELECT count(*)::int FROM q_tx");
        Assert.Equal(0, ((Int32Array)b[0].Column(0)).GetValue(0));
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        await h.RunAsync(sid, "DROP TABLE q_tx");
    }

    [IntegrationFact]
    public async Task Metadata_plans_and_definitions()
    {
        await using var h = new TestHost();
        var (p, sid) = await h.OpenPgAsync();
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = "postgres", Full = true });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, "postgres");
        var snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = "postgres" });
        var orders = Assert.Single(snap.Objects, o => o.Name == "orders");
        Assert.Contains(orders.Columns!, c => c.Name == "order_id" && c.Pk && c.Identity);
        var fn = Assert.Single(snap.Objects, o => o.Name == "calc_discount");
        Assert.Equal(ObjectKind.Func, fn.Kind);
        Assert.Contains(fn.Params!, x => x.Name == "p_pct" && x.HasDefault);
        Assert.True(snap.Objects.Length > 1000);

        var def = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "public", Name = "calc_discount", Kind = ObjectKind.Func, ObjectId = fn.Id } });
        Assert.StartsWith("CREATE OR REPLACE FUNCTION", def.Sql);
        var view = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "public", Name = "v1", Kind = ObjectKind.View } });
        Assert.StartsWith("CREATE OR REPLACE VIEW public.v1 AS", view.Sql);
        var deps = await h.Rpc.MetaDependencies(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "public", Name = "t1", Kind = ObjectKind.Table } });
        Assert.Contains(deps, d => d.Direction == DependencyDirection.UsedBy && d.Object.Name == "v1");

        var est = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT * FROM t1 a JOIN t2 b ON b.id = a.c1 WHERE a.id < 10", Mode = PlanMode.Estimated });
        Assert.NotNull(est.Tree.Statements[0].Root);
        var act = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "DELETE FROM t5 WHERE id < 0; SELECT count(*) FROM big", Mode = PlanMode.Actual, ConfirmedRun = true });
        Assert.Equal(2, act.Tree.Statements.Length);
        Assert.NotNull(act.Tree.Statements[1].Root!.ActualRows);
    }

    [IntegrationFact]
    public async Task Database_switch_opens_a_pool_for_the_new_database()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        await h.RunAsync(sid, "SELECT 1");
        var r = await h.Rpc.SessionChangeDatabase(new ChangeDatabaseParams { SessionId = sid, Database = "template1" });
        Assert.Equal("template1", r.Database);
        var (_, b, _) = await h.RunAsync(sid, "SELECT current_database()");
        Assert.Equal("template1", ((StringArray)b[0].Column(0)).GetString(0));
    }

    /// <summary>
    /// H1 against a real server: an actual plan inside the tab's open transaction goes back to a savepoint, so the user's
    /// uncommitted rows survive it (before the fix, BEGIN … ROLLBACK on the pinned connection discarded them); one in a
    /// failed transaction is refused with the reason, leaving the transaction for the user to roll back.
    /// </summary>
    [IntegrationFact]
    public async Task An_actual_plan_inside_an_open_transaction_keeps_the_users_work()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        Assert.Null((await h.RunAsync(sid, "DROP TABLE IF EXISTS plan_tx; CREATE TABLE plan_tx (id int)")).Done.Error);
        async Task<long> Count() => ((Int64Array)(await h.RunAsync(sid, "SELECT count(*) FROM plan_tx")).Batches[0].Column(0)).GetValue(0)!.Value;

        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        Assert.Null((await h.RunAsync(sid, "INSERT INTO plan_tx VALUES (1), (2)")).Done.Error);
        var plan = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "DELETE FROM plan_tx", Mode = PlanMode.Actual, ConfirmedRun = true });
        Assert.Equal(ExecState.Done, plan.Done.State);
        Assert.NotNull(plan.Tree.Statements[0].Root!.ActualRows);
        Assert.Equal(2, await Count());
        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);

        // A failed statement aborts the block: the plan cannot set its savepoint and says why.
        await h.RunAsync(sid, "SELECT 1 / 0");
        var refused = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT 1", Mode = PlanMode.Actual, ConfirmedRun = true });
        Assert.Equal(ExecState.Failed, refused.Done.State);
        Assert.Contains("has failed", Assert.Single(refused.Errors));
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
        Assert.Equal(0, await Count());

        // Outside a transaction the plan's own transaction is rolled back.
        Assert.Null((await h.RunAsync(sid, "INSERT INTO plan_tx VALUES (3)")).Done.Error);
        Assert.Equal(ExecState.Done, (await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "DELETE FROM plan_tx", Mode = PlanMode.Actual, ConfirmedRun = true })).Done.State);
        Assert.Equal(1, await Count());
    }
}

/// <summary>
/// pldebugger's proxy (pldbgapi readn) fails any call whose select() is interrupted by a signal, and DROP DATABASE elsewhere
/// on the server signals every backend (ProcSignalBarrier). Run alone so the parallel schema-compare tests can't break it.
/// </summary>
[CollectionDefinition("pgdebug", DisableParallelization = true)]
public class PgDebugCollection;

[Collection("pgdebug")]
public class PostgresDebuggerIntegrationTests
{
    [IntegrationFact]
    public async Task Pldebugger_steps_through_a_function()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid });
        Assert.True(caps.Debugger.Available, caps.Debugger.Reason);
        var (_, b, _) = await h.RunAsync(sid, "SELECT 'public.calc_discount(numeric,integer)'::regprocedure::oid::bigint");
        var oid = ((Int64Array)b[0].Column(0)).GetValue(0)!.Value;

        var attached = await h.Rpc.DebugAttach(new DebugAttachParams { SessionId = sid, FunctionOid = oid, CallSql = "SELECT public.calc_discount(1500, 10)" });
        var dbg = new DebugRef { DebugId = attached.DebugId! };
        DebugEvent Last() => h.Notifier.Of<DebugEvent>().Last(e => e.DebugId == dbg.DebugId && e.Kind is DebugEventKind.Paused or DebugEventKind.Finished);
        var paused = Last();
        Assert.Equal(DebugEventKind.Paused, paused.Kind);
        Assert.Contains(paused.Variables!, v => v.Name == "p_total");
        var src = await h.Rpc.DebugSourceGet(new DebugSourceParams { DebugId = dbg.DebugId, FunctionOid = oid });
        Assert.Contains("v_rate", src.Source);

        var firstLine = paused.Line;
        await h.Rpc.DebugStep(new DebugStepParams { DebugId = dbg.DebugId, Kind = DebugStepKind.Over });
        Assert.NotEqual(firstLine, Last().Line);
        await h.Rpc.DebugStep(new DebugStepParams { DebugId = dbg.DebugId, Kind = DebugStepKind.Continue });
        for (var i = 0; i < 50 && Last().Kind != DebugEventKind.Finished; i++) await Task.Delay(100);
        Assert.Equal(DebugEventKind.Finished, Last().Kind);
        Assert.Contains("1275", Last().Message);
        await h.Rpc.DebugDetach(dbg);
    }
}

[Collection("mssql")]
public class SqlServerTraceAndObjectTests
{
    [IntegrationFact]
    public async Task Trace_captures_statements_of_a_procedure()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync("SalesDB");
        var caps = await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid });
        Assert.True(caps.Trace.Available, caps.Trace.Reason);
        var trace = await h.Rpc.TraceStart(new TraceStartParams { SessionId = sid });
        var state = await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid });
        Assert.True(state.Pinned);
        var (done, _, _) = await h.RunAsync(sid, "DECLARE @c int; EXEC dbo.usp_p1 @Id = 1, @Count = @c OUTPUT; SELECT @c;");
        Assert.Equal(ExecState.Done, done.State);
        TraceEvent[] events = [];
        for (var i = 0; i < 40 && !events.Any(e => e.ProcName == "dbo.usp_p1"); i++)
        {
            await Task.Delay(250);
            events = h.Notifier.Of<TraceEventsEvent>().Where(e => e.TraceId == trace.TraceId).SelectMany(e => e.Events).ToArray();
        }
        Assert.Contains(events, e => e.ProcName == "dbo.usp_p1" && e.EventName == "sp_statement_completed" && e.Line > 0);
        Assert.Contains(events, e => e.EventName == "module_end");
        await h.Rpc.TraceStop(trace);
        Assert.False((await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).Pinned);
    }

    [IntegrationFact]
    public async Task Definition_scripting_and_dependencies()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync("SalesDB");
        var def = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "dbo", Name = "usp_p2", Kind = ObjectKind.Proc } });
        // SQL Server stores CREATE OR ALTER as "CREATE   PROCEDURE"; the editable script puts OR ALTER back.
        Assert.Matches(@"^CREATE OR ALTER\s+PROCEDURE dbo\.usp_p2", def.Sql.TrimStart());
        var create = await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = new ObjectRef { Schema = "dbo", Name = "Orders", Kind = ObjectKind.Table }, Kind = ScriptKind.Create });
        Assert.Contains("CREATE TABLE", create.Sql);
        Assert.Contains("[CustomerId]", create.Sql);
        var exec = await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = new ObjectRef { Schema = "dbo", Name = "usp_p2", Kind = ObjectKind.Proc }, Kind = ScriptKind.Execute });
        Assert.Contains("@Count = @Count OUTPUT", exec.Sql);
        var deps = await h.Rpc.MetaDependencies(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "dbo", Name = "v1", Kind = ObjectKind.View } });
        Assert.Contains(deps, d => d.Direction == DependencyDirection.Uses && d.Object.Name == "t1");
        var dbs = await h.Rpc.MetaDatabases(new SessionRef { SessionId = sid });
        Assert.Contains(dbs, d => d.Name == "SalesDB");
    }
}

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S09 against the Oracle Free fixture (QUERITHM_IT=1). Plans: PLAN_TABLE is the session's own (a global temporary
/// table) and keeps no row of ours; the estimated plan leaves no transaction behind, and inside the user's transaction
/// leaves it as it was; the actual plan reads V$SQL_PLAN_STATISTICS_ALL with the catalog-role user and refuses with the
/// reason — before the statement runs — without the grants; a DELETE without WHERE is guarded like a run and rolled back;
/// cancel ends an actual plan. Tier 2 on the live snapshot: columns through a synonym, standalone and package signatures.
/// </summary>
public sealed class OraclePlanIntegrationTests
{
    const string App = OracleIntegrationTests.AppUser, Mon = OracleIntegrationTests.MonitorUser;
    const string Table = "Q_S09_T";
    /// <summary>The fixture's table for the catalog-role user's plans (01-users.sh: 50 rows, SELECT and DELETE granted; the tests grant nothing).</summary>
    const string Qualified = "QUERITHM_OWN.PLAN_T";

    static Profile ProfileOf(string user) => new()
    {
        Id = "", Name = $"ora-plan-{user}", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password, Username = user,
        DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
    };

    static string Secret(string user) => FixtureSecrets.Password(user == App ? "oracle" : "oracle-mon");

    static CancellationToken Timeout(int seconds = 120) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    static async Task<DbConnection> OpenAsync(string user = App) =>
        await TestOracle.Supported(Directory.CreateTempSubdirectory("qora-plan-").FullName).OpenAsync(ProfileOf(user), Secret(user), null, null, Timeout());

    static async Task<object?> ScalarAsync(DbConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(Timeout());
        return v is DBNull ? null : v;
    }

    static async Task ExecAsync(DbConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(Timeout());
    }

    static async Task<long> CountFromOutsideAsync()
    {
        await using var c = new OracleConnection(OracleIntegrationTests.ConnectionString(Mon));
        await c.OpenAsync();
        return Convert.ToInt64(await ScalarAsync(c, $"SELECT COUNT(*) FROM {Qualified}"), CultureInfo.InvariantCulture);
    }

    static readonly SemaphoreSlim Once = new(1, 1);

    /// <summary>The app user's own objects: 50 rows with a primary key, a NOCACHE sequence (its values are never rolled back), a synonym, a routine and a package. Nothing is granted.</summary>
    static async Task FreshAsync()
    {
        await Once.WaitAsync();
        try
        {
            await using var c = await OpenAsync();
            await ExecAsync(c, $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {Table} PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE <> -942 THEN RAISE; END IF; END;");
            await ExecAsync(c, "BEGIN EXECUTE IMMEDIATE 'DROP SEQUENCE Q_S09_SEQ'; EXCEPTION WHEN OTHERS THEN IF SQLCODE <> -2289 THEN RAISE; END IF; END;");
            await ExecAsync(c, $"CREATE TABLE {Table} (id NUMBER(10) PRIMARY KEY, v VARCHAR2(20))");
            await ExecAsync(c, $"INSERT INTO {Table} SELECT level, 'v' || level FROM dual CONNECT BY level <= 50");
            await ExecAsync(c, "CREATE SEQUENCE Q_S09_SEQ NOCACHE");
            await ExecAsync(c, $"CREATE OR REPLACE SYNONYM Q_S09_SYN FOR {Table}");
            await ExecAsync(c, "CREATE OR REPLACE PROCEDURE Q_S09_PROC(p_id IN NUMBER, p_note IN VARCHAR2 DEFAULT 'x') IS BEGIN NULL; END;");
            await ExecAsync(c, """
                CREATE OR REPLACE PACKAGE Q_S09_PKG IS
                  PROCEDURE p(a IN NUMBER);
                  PROCEDURE p(a IN VARCHAR2, b OUT NUMBER);
                  FUNCTION f(x IN NUMBER) RETURN NUMBER;
                END;
                """);
        }
        finally { Once.Release(); }
    }

    static TestHost NewHost() => new(TestOracle.Supported(Directory.CreateTempSubdirectory("qora-plan-host-").FullName));

    static async Task<string> SessionAsync(TestHost h, string user, EnvironmentKind environment = EnvironmentKind.Dev)
    {
        var saved = await h.Rpc.ProfileSave(ProfileOf(user) with { Environment = environment });
        return (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = Secret(user) })).SessionId;
    }

    static PlanNode? Find(PlanNode node, Func<PlanNode, bool> match) =>
        match(node) ? node : node.Children.Select(c => Find(c, match)).FirstOrDefault(n => n != null);

    // ---------------------------------------------------------------------------------------------------- estimated

    [IntegrationFact]
    public async Task Plan_table_is_a_session_private_temporary_table_and_keeps_no_row_of_ours()
    {
        await FreshAsync();
        await using var c = await OpenAsync();
        // PUBLIC.PLAN_TABLE → SYS.PLAN_TABLE$, temporary for the session (measured: Free 23.26).
        Assert.Equal("Y SYS$SESSION", await ScalarAsync(c, "SELECT temporary || ' ' || duration FROM all_tables WHERE owner = 'SYS' AND table_name = 'PLAN_TABLE$'"));
        Assert.Equal("SYS.PLAN_TABLE$", await ScalarAsync(c, "SELECT table_owner || '.' || table_name FROM all_synonyms WHERE owner = 'PUBLIC' AND synonym_name = 'PLAN_TABLE'"));

        var plan = await new OraclePlans().GetPlanAsync(c, $"SELECT * FROM {Table} WHERE id = :id;\nSELECT COUNT(*) FROM {Table}", PlanMode.Estimated, Timeout());
        Assert.Equal(OraclePlans.Format, plan.Format);
        Assert.Equal(2, plan.Plan.Statements.Length);
        var first = plan.Plan.Statements[0].Root!;
        Assert.Equal("SELECT STATEMENT", first.Operator);
        Assert.NotNull(Find(first, n => n.Operator == "INDEX UNIQUE SCAN")); // through the primary key
        Assert.Equal($"QUERITHM_APP.{Table}", Find(first, n => n.Operator.StartsWith("TABLE ACCESS", StringComparison.Ordinal))!.ObjectName);
        Assert.True(plan.Plan.Statements[1].SubtreeCost > 0);
        Assert.Null(Find(first, n => n.ActualRows != null));
        // Nothing of ours is left in the session's PLAN_TABLE, and autocommit left no transaction open.
        Assert.Equal(0m, await ScalarAsync(c, "SELECT COUNT(*) FROM plan_table"));
        Assert.Null(await ScalarAsync(c, "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM dual"));
        // plan.parse reads the raw text back the same.
        Assert.Equal(plan.Plan.Statements[0].Root!.Children.Length, new OraclePlans().Parse(plan.Raw).Statements[0].Root!.Children.Length);
    }

    [IntegrationFact]
    public async Task A_statement_that_fails_to_explain_leaves_no_rows_and_says_why()
    {
        await using var c = await OpenAsync();
        var ex = await Assert.ThrowsAsync<OracleException>(() => new OraclePlans().GetPlanAsync(c, "SELECT * FROM q_s09_no_such_table", PlanMode.Estimated, Timeout()));
        Assert.Equal(942, ex.Number);
        Assert.Equal(0m, await ScalarAsync(c, "SELECT COUNT(*) FROM plan_table"));
    }

    [IntegrationFact]
    public async Task Plans_inside_an_open_transaction_leave_it_and_its_changes_intact()
    {
        await FreshAsync();
        await using var h = NewHost();
        var sid = await SessionAsync(h, Mon);
        Assert.Equal(TxState.Open, (await h.Rpc.TxBegin(new SessionRef { SessionId = sid })).TxState);
        var (del, _, delId) = await h.RunAsync(sid, $"DELETE FROM {Qualified} WHERE id = 1");
        Assert.True(del.State == ExecState.Done, string.Join("\n", h.Messages(delId)) + del.Error?.Message);

        var est = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = $"SELECT * FROM {Qualified}", Mode = PlanMode.Estimated });
        Assert.Equal("SELECT STATEMENT", est.Tree.Statements[0].Root!.Operator);
        // The actual plan's DELETE goes back to a savepoint: the user's own DELETE stays, the plan's is undone.
        var act = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = $"DELETE FROM {Qualified} WHERE id = 2", Mode = PlanMode.Actual, ConfirmedRun = true });
        Assert.True(act.Done.State == ExecState.Done, string.Join(" | ", act.Errors));
        Assert.Equal(1d, Find(act.Tree.Statements[0].Root!, n => n.Operator.StartsWith("DELETE", StringComparison.Ordinal))?.ActualExecutions);

        Assert.Equal(TxState.Open, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        var (check, batches, checkId) = await h.RunAsync(sid, $"SELECT COUNT(CASE WHEN id = 1 THEN 1 END) AS one, COUNT(CASE WHEN id = 2 THEN 1 END) AS two FROM {Qualified}");
        Assert.True(check.State == ExecState.Done, string.Join("\n", h.Messages(checkId)) + check.Error?.Message);
        var row = batches.Single();
        Assert.Equal("0", Cell(row, 0));
        Assert.Equal("1", Cell(row, 1));
        await h.Rpc.TxRollback(new SessionRef { SessionId = sid });
        Assert.Equal(50, await CountFromOutsideAsync());
    }

    static string Cell(Apache.Arrow.RecordBatch b, int column) => b.Column(column) switch
    {
        Apache.Arrow.Decimal128Array d => d.GetValue(0)!.Value.ToString(CultureInfo.InvariantCulture),
        Apache.Arrow.StringArray s => s.GetString(0),
        Apache.Arrow.Int64Array l => l.GetValue(0)!.Value.ToString(CultureInfo.InvariantCulture),
        Apache.Arrow.DoubleArray f => f.GetValue(0)!.Value.ToString(CultureInfo.InvariantCulture),
        var other => throw new InvalidOperationException($"unexpected column type {other.GetType().Name}"),
    };

    // ------------------------------------------------------------------------------------------------------- actual

    [IntegrationFact]
    public async Task The_actual_plan_has_the_last_runs_rows_starts_and_buffers_with_the_grants()
    {
        await FreshAsync();
        await using var h = NewHost();
        var sid = await SessionAsync(h, Mon);
        var r = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = $"SELECT v FROM {Qualified} WHERE id > 10", Mode = PlanMode.Actual, ConfirmedRun = true });
        Assert.True(r.Done.State == ExecState.Done, string.Join(" | ", r.Errors));
        var root = r.Tree.Statements[0].Root!;
        Assert.Equal("SELECT STATEMENT", root.Operator);
        Assert.Equal(40d, root.ActualRows);
        var access = Find(root, n => n.ObjectName == Qualified || n.Operator.StartsWith("INDEX", StringComparison.Ordinal))!;
        Assert.Equal(1d, access.ActualExecutions);
        Assert.NotNull(access.LogicalReads);
        Assert.NotNull(r.Tree.Statements[0].ElapsedMs);
        Assert.Contains("\"actual\":true", r.Raw);

        // After a WITH clause the hint goes on the main query, and its statistics are still gathered.
        var w = await h.Rpc.PlanAsync(h.Notifier, new PlanParams
        {
            SessionId = sid, Sql = $"WITH a AS (SELECT /*+ MATERIALIZE */ id FROM {Qualified}) SELECT COUNT(*) FROM a", Mode = PlanMode.Actual, ConfirmedRun = true,
        });
        Assert.True(w.Done.State == ExecState.Done, string.Join(" | ", w.Errors));
        Assert.Equal(1d, w.Tree.Statements[0].Root!.ActualRows);
        Assert.NotNull(Find(w.Tree.Statements[0].Root!, n => n.ActualRows == 50));

        // plan.parse on the raw text from the server.
        var parsed = await h.Rpc.PlanParse(new PlanParseParams { Engine = Engine.Oracle, Raw = r.Raw! });
        Assert.Equal("oracle-json", parsed.Format);
        Assert.Equal(40d, parsed.Plan.Statements[0].Root!.ActualRows);
    }

    [IntegrationFact]
    public async Task Without_the_grants_the_actual_plan_says_which_and_the_statement_never_runs()
    {
        await FreshAsync();
        await using var c = await OpenAsync(App);
        var before = Convert.ToInt64(await ScalarAsync(c, "SELECT last_number FROM user_sequences WHERE sequence_name = 'Q_S09_SEQ'"), CultureInfo.InvariantCulture);
        await using var h = NewHost();
        var sid = await SessionAsync(h, App);
        var r = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT Q_S09_SEQ.NEXTVAL FROM dual", Mode = PlanMode.Actual, ConfirmedRun = true });
        Assert.Equal(ExecState.Failed, r.Done.State);
        Assert.Contains(OraclePlans.NoStatistics, string.Join(" | ", r.Errors));
        Assert.Null(r.Plan);
        // A sequence value is never rolled back: the number is unchanged only if the statement did not run.
        Assert.Equal(before, Convert.ToInt64(await ScalarAsync(c, "SELECT last_number FROM user_sequences WHERE sequence_name = 'Q_S09_SEQ'"), CultureInfo.InvariantCulture));
        // The estimated plan needs no grant.
        var est = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = "SELECT Q_S09_SEQ.NEXTVAL FROM dual", Mode = PlanMode.Estimated });
        Assert.Contains(est.Tree.Statements[0].Root!.Children, n => n.Operator == "SEQUENCE");
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
    }

    [IntegrationFact]
    public async Task An_actual_plan_of_a_delete_without_where_is_guarded_like_a_run_and_rolled_back()
    {
        await FreshAsync();
        await using var h = NewHost();
        var sid = await SessionAsync(h, Mon, EnvironmentKind.Prod);
        var p = new PlanParams { SessionId = sid, Sql = $"DELETE FROM {Qualified}", Mode = PlanMode.Actual, ConfirmedRun = true };
        // An actual plan without the run confirmation is refused before the guard.
        Assert.Equal("confirmRun", (await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanGet(p with { ConfirmedRun = false }))).Code);
        // The sidecar enforces the guard at the RPC (§13 rule 7): on Prod the first run, then the destructive statement, each
        // need their confirmation, and no execution starts until both are given.
        var first = await h.Rpc.PlanGet(p);
        Assert.Equal(GuardReason.ProdFirstRun, first.Blocked?.Reason);
        Assert.Null(first.ExecutionId);
        var destructive = await h.Rpc.PlanGet(p with { ConfirmedProd = true });
        Assert.Equal(GuardReason.Destructive, destructive.Blocked?.Reason);
        Assert.Null(destructive.ExecutionId);
        Assert.Contains(destructive.Blocked!.Findings, d => d.Rule == "deleteWithoutWhere");
        Assert.Equal(50, await CountFromOutsideAsync());

        var r = await h.Rpc.PlanAsync(h.Notifier, p with { ConfirmedProd = true, ConfirmedDestructive = true });
        Assert.True(r.Done.State == ExecState.Done, string.Join(" | ", r.Errors));
        // The scan under the DELETE read all 50 rows (a DML operator itself outputs none).
        var root = r.Tree.Statements[0].Root!;
        Assert.Equal("DELETE STATEMENT", root.Operator);
        Assert.NotNull(Find(root, n => n.ActualRows == 50 && n.ActualExecutions == 1));
        Assert.Equal(50, await CountFromOutsideAsync()); // the DELETE ran and was rolled back
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
    }

    [IntegrationFact]
    public async Task Ddl_and_plsql_are_refused_before_anything_runs()
    {
        await using var h = NewHost();
        var sid = await SessionAsync(h, Mon);
        var ddl = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.PlanGet(new PlanParams
        {
            SessionId = sid, Sql = $"CREATE TABLE q_s09_ctas AS SELECT * FROM {Qualified}", Mode = PlanMode.Actual, ConfirmedRun = true,
        }));
        Assert.Equal("unsupported", ddl.Code);
        Assert.Contains("commits before and after", ddl.Message);
        // Estimated, the same CTAS explains without creating anything.
        var est = await h.Rpc.PlanAsync(h.Notifier, new PlanParams { SessionId = sid, Sql = $"CREATE TABLE q_s09_ctas AS SELECT * FROM {Qualified}", Mode = PlanMode.Estimated });
        Assert.Equal("CREATE TABLE STATEMENT", est.Tree.Statements[0].Root!.Operator);
        await using var c = await OpenAsync(Mon);
        Assert.Equal(0m, await ScalarAsync(c, "SELECT COUNT(*) FROM user_tables WHERE table_name = 'Q_S09_CTAS'"));
        Assert.Contains("PL/SQL unit", (await Assert.ThrowsAsync<QuerithmException>(() =>
            h.Rpc.PlanGet(new PlanParams { SessionId = sid, Sql = "BEGIN NULL; END;", Mode = PlanMode.Estimated }))).Message);
    }

    [IntegrationFact]
    public async Task Cancel_ends_an_actual_plan_and_the_tab_runs_again()
    {
        await using var h = NewHost();
        var sid = await SessionAsync(h, Mon);
        var r = await h.Rpc.PlanGet(new PlanParams
        {
            // A cartesian product of the dictionary: minutes of work (CONNECT BY at this size runs out of memory instead).
            SessionId = sid, Sql = "SELECT COUNT(*) FROM all_objects a, all_objects b, all_objects c", Mode = PlanMode.Actual, ConfirmedRun = true,
        });
        var id = r.ExecutionId!;
        await Task.Delay(1500);
        var sw = Stopwatch.StartNew();
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = id });
        var done = await h.Notifier.WaitDone(id, 60_000);
        Assert.True(done.State == ExecState.Cancelled, $"{done.State}: {string.Join(" | ", h.Messages(id))} {done.Error?.Message}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"cancel took {sw.Elapsed}");
        Assert.Equal(TxState.None, (await h.Rpc.SessionStateGet(new SessionRef { SessionId = sid })).TxState);
        var (next, _, nextId) = await h.RunAsync(sid, "SELECT 1 AS one FROM dual");
        Assert.True(next.State == ExecState.Done, string.Join("\n", h.Messages(nextId)) + next.Error?.Message);
    }

    // ------------------------------------------------------------------------------------- tier 2 on the live snapshot

    [IntegrationFact]
    public async Task Tier_2_completes_from_the_live_snapshot_through_a_synonym_and_shows_routine_and_package_signatures()
    {
        await FreshAsync();
        var dir = Directory.CreateTempSubdirectory("qora-tier2-").FullName;
        await using var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance,
            new ProviderRegistry(TestOracle.ShippedProviders(dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle")), dir);
        var rpc = new RpcServer(services, () => { });
        var p = await rpc.ProfileSave(ProfileOf(App));
        await rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = Secret(App) });
        var db = OracleIntegrationTests.Service;
        await rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = db, Full = true });
        await services.Metadata.WaitAsync(p.Id, db);
        var snap = await rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = db });
        var table = Assert.Single(snap.Objects, o => o.Schema == "QUERITHM_APP" && o.Name == Table && o.Kind == ObjectKind.Table);
        Assert.Equal(table.Id, Assert.Single(snap.Objects, o => o.Name == "Q_S09_SYN").TargetObjectId);

        async Task<string[]> Labels(string marked)
        {
            var at = marked.IndexOf('|');
            var items = await rpc.LangComplete(new CompleteParams
            {
                DocId = Guid.NewGuid().ToString(), Text = marked.Remove(at, 1), Offset = at, Dialect = Dialect.Oracle, ProfileId = p.Id, Database = db, Schema = "QUERITHM_APP",
            });
            return items.Select(i => i.Label).Order(StringComparer.Ordinal).ToArray();
        }
        async Task<SignatureHelp> Sig(string marked)
        {
            var at = marked.IndexOf('|');
            return await rpc.LangSignature(new SignatureParams
            {
                DocId = Guid.NewGuid().ToString(), Text = marked.Remove(at, 1), Offset = at, Dialect = Dialect.Oracle, ProfileId = p.Id, Database = db, Schema = "QUERITHM_APP",
            });
        }

        Assert.Equal(["ID", "V"], await Labels("select t.| from q_s09_t t"));
        Assert.Equal(["ID", "V"], await Labels("select s.| from q_s09_syn s"));
        Assert.Contains("Q_S09_SYN", await Labels("SELECT * FROM |"));
        Assert.Equal(["ID", "V"], await Labels("BEGIN\n  FOR r IN (SELECT t.| FROM q_s09_t t) LOOP NULL; END LOOP;\nEND;"));
        Assert.Empty(await Labels("SELECT q'[t.|]' FROM q_s09_t t"));

        var proc = Assert.Single((await Sig("BEGIN q_s09_proc(1, |")).Signatures);
        Assert.Equal("QUERITHM_APP.Q_S09_PROC(P_ID NUMBER, P_NOTE VARCHAR2 DEFAULT …)", proc.Label);
        var overloads = (await Sig("BEGIN q_s09_pkg.p(|")).Signatures.Select(s => s.Label).ToArray();
        Assert.Equal(["QUERITHM_APP.Q_S09_PKG.P(A NUMBER)", "QUERITHM_APP.Q_S09_PKG.P(A VARCHAR2, B OUT NUMBER)"], overloads);
        Assert.Equal("returns NUMBER", Assert.Single((await Sig("SELECT q_s09_pkg.f(|) FROM dual")).Signatures).Documentation);
    }
}

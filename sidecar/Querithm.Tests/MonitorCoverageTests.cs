using System.Data;
using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Monitoring;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Phase B matrix rows typed U that need a provider's real reading code or the diagnoser: the SQL Server tick read
/// through a fake connection (S2, S8, S11, S12, B5, B12), top-query SQL (T3, T8, T10), deadlock sources (X9, X10) and
/// "Why is it slow?" (W1–W5).
/// </summary>
public class MonitorCoverageTests
{
    static readonly WaitDictionary Dict = WaitDictionary.Load();
    static SessionKey K(long id) => new() { Id = id, Instance = "1:1" };

    // ------------------------------------------------------------------ "Why is it slow?" (B3b)

    static ActivityRow R(long id, string status = "running", long[]? by = null, string? wait = null, long? waitMs = null, int? openTx = null,
        long? txMs = null, string? own = null, string? note = null, string? program = null) => new()
    {
        Key = K(id), Status = status, BlockedBy = by ?? [], IsUserSession = true, WaitType = wait, WaitMs = waitMs, OpenTx = openTx,
        TxElapsedMs = txMs, OwnSessionId = own, BlockedByNote = note, Program = program,
    };

    static RequestDetail NoPlan(ActivityRow row) => new() { Key = row.Key, PlanReason = "no plan (test)" };

    static Diagnosis Run(ActivityRow row, IReadOnlyList<ActivityRow>? rows = null, RequestDetail? detail = null, Engine engine = Engine.Mssql) =>
        Diagnoser.Run(row.Key, row, rows ?? [row], null, detail ?? NoPlan(row), null, engine, Dict);

    static PlanResult PlanWith(string warning) => new()
    {
        Raw = "<ShowPlanXML/>", Format = "xml",
        Plan = new PlanTree
        {
            Statements = [new PlanStatement
            {
                Warnings = [warning], MissingIndexes = [],
                Root = new PlanNode { Id = "0", Operator = "Scan", PhysicalOp = "Clustered Index Scan", Warnings = [], Properties = [], Children = [] },
            }],
        },
    };

    public static TheoryData<string> Rules => new()
    {
        "blocked by an own tab", "idleBlocker", "hiddenBlocker (not visible)", "hiddenBlocker (lock wait)", "pseudo blocker", "memoryGrant",
        "parallelSkew", "scan", "planWarning", "idleInTx (PG)", "longTx", "waitClass (PG)", "waitType", "onCpu",
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void W1_each_rule_fires_on_its_fixture_input_with_its_evidence(string rule)
    {
        Diagnosis d;
        DiagnosisFinding f;
        switch (rule)
        {
            case "blocked by an own tab":
                d = Run(R(2, by: [1], wait: "LCK_M_U", waitMs: 9_000), [R(2, by: [1]), R(1, "sleeping", openTx: 1, own: "tab-1")]);
                f = d.Findings[0];
                Assert.Equal(("blocked", 1L, "tab-1"), (f.Rule, f.BlockerId!.Value, f.OwnSessionId!));
                Assert.Contains("blocked by 1", f.Evidence);
                break;
            case "idleBlocker":
                d = Run(R(2, by: [1]), [R(2, by: [1]), R(1, "sleeping", openTx: 1, txMs: 120_000, program: "app <x>")]);
                f = d.Findings[0];
                Assert.Equal("idleBlocker", f.Rule);
                Assert.Contains("blocker status sleeping", f.Evidence);
                Assert.Contains("blocker application app <x>", f.Evidence); // data, never markup (W7)
                Assert.Contains("blocker transaction open 120 s", f.Evidence);
                break;
            case "hiddenBlocker (not visible)":
                f = Run(R(2, by: [9])).Findings[0];
                Assert.Equal(("hiddenBlocker", 9L), (f.Rule, f.BlockerId!.Value));
                break;
            case "hiddenBlocker (lock wait)":
                f = Run(R(2, wait: "LCK_M_X", waitMs: 4_000)).Findings[0];
                Assert.Equal("hiddenBlocker", f.Rule);
                Assert.Contains("wait LCK_M_X for 4,000 ms", f.Evidence);
                break;
            case "pseudo blocker":
                f = Run(R(2, note: SqlServerActivitySession.BlockerNote(-2))).Findings[0];
                Assert.Equal("blocked", f.Rule);
                Assert.Contains("blocking_session_id -2", f.Evidence);
                Assert.Null(f.BlockerId); // not a session
                break;
            case "memoryGrant":
                f = Run(R(2, wait: "RESOURCE_SEMAPHORE", waitMs: 3_000) with { RequestedMemoryKb = 1_024 }).Findings[0];
                Assert.Equal("memoryGrant", f.Rule);
                Assert.Contains("requested 1,024 KB", f.Evidence);
                Assert.Contains("granted none yet", f.Evidence);
                break;
            case "parallelSkew":
                f = Run(R(2, wait: "CXPACKET", waitMs: 2_000) with { Dop = 8 }).Findings[0];
                Assert.Equal("parallelSkew", f.Rule);
                Assert.Contains("DOP 8", f.Evidence);
                break;
            case "scan":
                f = Run(R(2) with { LogicalReads = 500_000, RowCount = 10 }).Findings[0];
                Assert.Equal("scan", f.Rule);
                Assert.Contains("500,000 logical reads", f.Evidence);
                break;
            case "planWarning":
                var row = R(2);
                f = Run(row, detail: new RequestDetail { Key = row.Key, Plan = PlanWith("Type conversion in expression may affect cardinality") }).Findings[0];
                Assert.Equal("planWarning", f.Rule);
                Assert.Contains("Type conversion in expression may affect cardinality", f.Evidence);
                break;
            case "idleInTx (PG)":
                d = Run(R(1, "idle in transaction", txMs: 60_000), [R(1, "idle in transaction"), R(2, by: [1])], engine: Engine.Pg);
                f = d.Findings[0];
                Assert.Equal(("idleInTx", "high"), (f.Rule, f.Severity));
                Assert.Contains("1 waiting", f.Evidence);
                break;
            case "longTx":
                f = Run(R(2, txMs: 11 * 60_000)).Findings[0];
                Assert.Equal("longTx", f.Rule);
                Assert.Contains("transaction open 660 s", f.Evidence);
                break;
            case "waitClass (PG)":
                f = Run(R(2, wait: "IO:DataFileRead", waitMs: 50), engine: Engine.Pg).Findings[0];
                Assert.Equal("waitClass", f.Rule);
                var meaning = Dict.Info(Engine.Pg, "IO:DataFileRead").Meaning;
                Assert.NotNull(meaning);
                Assert.Contains(meaning, f.Message);
                break;
            case "waitType":
                f = Run(R(2, wait: "PAGEIOLATCH_SH", waitMs: 2_000)).Findings[0];
                Assert.Equal(("waitType", "low"), (f.Rule, f.Severity));
                var waitMeaning = Dict.Info(Engine.Mssql, "PAGEIOLATCH_SH").Meaning;
                Assert.NotNull(waitMeaning);
                Assert.Contains(waitMeaning, f.Message);
                break;
            case "onCpu":
                f = Run(R(2) with { CpuMs = 1_234, ElapsedMs = 2_000 }).Findings.Single();
                Assert.Equal("onCpu", f.Rule);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(rule));
        }
    }

    [Fact]
    public void W2_several_matching_rules_are_ordered_by_severity_and_deterministic()
    {
        var row = R(2, by: [9], wait: "RESOURCE_SEMAPHORE", waitMs: 5_000) with { LogicalReads = 900_000, RowCount = 1 };
        var a = Run(row).Findings.Select(f => f.Rule).ToArray();
        Assert.Equal(["hiddenBlocker", "memoryGrant", "scan"], a);
        Assert.Equal(a, Run(row).Findings.Select(f => f.Rule).ToArray());
    }

    [Fact]
    public void W3_nothing_matching_says_no_obvious_cause_with_the_numbers()
    {
        var f = Run(R(2) with { CpuMs = 1_234, LogicalReads = 50, ElapsedMs = 2_000 }).Findings.Single();
        Assert.Equal("onCpu", f.Rule);
        Assert.StartsWith("No obvious cause", f.Message);
        Assert.Equal(["CPU 1,234 ms", "50 logical reads", "elapsed 2,000 ms"], f.Evidence);
    }

    [Fact]
    public void W4_a_session_that_ended_says_so()
    {
        var d = Diagnoser.Run(K(7), null, [], null, null, null, Engine.Mssql, Dict);
        Assert.Equal("sessionEnded", d.Findings.Single().Rule);
    }

    [Fact]
    public void W5_without_a_plan_the_plan_rules_are_skipped_and_noted()
    {
        var row = R(2) with { CpuMs = 10 };
        var d = Run(row, detail: new RequestDetail { Key = row.Key, PlanReason = "The live plan needs VIEW SERVER STATE." });
        Assert.DoesNotContain(d.Findings, f => f.Rule == "planWarning");
        Assert.Contains("The live plan needs VIEW SERVER STATE.", d.Notes!);
        var noDetail = Diagnoser.Run(row.Key, row, [row], null, null, null, Engine.Mssql, Dict);
        Assert.Contains(noDetail.Notes!, n => n.Contains("plan rules skipped"));
    }

    // ------------------------------------------------------------------ SQL Server tick read (fake connection)

    static readonly (string, Type)[] SessionColumns =
    [
        ("session_id", typeof(short)), ("login_time", typeof(DateTime)), ("login_name", typeof(string)), ("host_name", typeof(string)),
        ("program_name", typeof(string)), ("database_name", typeof(string)), ("status", typeof(string)), ("command", typeof(string)),
        ("is_user_process", typeof(bool)), ("wait_type", typeof(string)), ("wait_resource", typeof(string)), ("wait_ms", typeof(long)),
        ("text", typeof(string)), ("encrypted", typeof(int)), ("query_hash", typeof(string)), ("elapsed_ms", typeof(long)), ("tx_ms", typeof(long)),
        ("state_ms", typeof(long)), ("cpu_ms", typeof(long)), ("reads", typeof(long)), ("writes", typeof(long)), ("logical_reads", typeof(long)),
        ("row_count", typeof(long)), ("open_transaction_count", typeof(int)), ("percent_complete", typeof(double)), ("rolling_back", typeof(int)),
        ("tempdb_kb", typeof(long)), ("granted_kb", typeof(long)), ("requested_kb", typeof(long)), ("dop", typeof(int)), ("rollback_running", typeof(int)), ("total", typeof(int)),
    ];

    static readonly DateTime Login = new(2026, 10, 28, 3, 0, 0, DateTimeKind.Unspecified);

    static object?[] Session(short id, int total, string status = "running", long? elapsed = 10, string? text = "SELECT 1", int rollbackRunning = 0) =>
        [id, Login, "app", "host", "prog", "db", status, status == "sleeping" ? null : "SELECT", true, null, null, null, text, 0, null,
         elapsed, null, 5L, 1L, 1L, 0L, 1L, 1L, 0, null, 0, null, null, null, null, rollbackRunning, total];

    static async Task<(TickResult Result, string Sql, List<DbParameter> Params)> TickAsync(DataTable sessions, DataTable blockers)
    {
        string sql = "";
        var ps = new List<DbParameter>();
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            sql = cmd.CommandText;
            ps.AddRange(cmd.Parameters.Cast<DbParameter>());
            return Task.FromResult(new FakeResult([sessions, blockers]));
        }, 1);
        await conn.OpenAsync();
        var session = SqlServerActivitySessionAccess.Session(conn);
        var r = await session.TickAsync(conn, new TickRequest(true, false, false, false, ActivityMonitor.MaxSessions), CancellationToken.None);
        return (r, sql, ps);
    }

    static DataTable Blockers(params (short Session, short Blocker)[] pairs) =>
        FakeResult.Table([("session_id", typeof(short)), ("blocking_session_id", typeof(short))], [.. pairs.Select(p => new object?[] { p.Session, p.Blocker })]);

    [Fact]
    public async Task S2_more_than_1000_sessions_keep_the_first_1000_in_server_priority_order_and_say_truncated()
    {
        var rows = Enumerable.Range(1, 1_005).Select(i => Session((short)i, 1_005)).ToArray();
        var (r, sql, ps) = await TickAsync(FakeResult.Table(SessionColumns, rows), Blockers());
        Assert.Equal(PaneState.Truncated, r.Sessions!.State);
        Assert.Equal(1_005, r.Sessions.Total);
        Assert.Equal(1_000, r.Sessions.Value!.Length);
        Assert.Equal(Enumerable.Range(1, 1_000).Select(i => (long)i), r.Sessions.Value.Select(x => x.Key.Id)); // the server's order is kept
        Assert.Equal(1_001, ps.Single(p => p.ParameterName == "@max").Value);                                  // one more to detect truncation
        Assert.Contains("ORDER BY CASE WHEN ISNULL(r.blocking_session_id, 0) <> 0", sql);                      // blocked, blockers, active, idle
    }

    [Fact]
    public async Task S8_a_session_with_two_request_rows_is_one_row_never_duplicated()
    {
        // A MARS session has a dm_exec_requests row per active request; the sessions query then returns it twice.
        var (r, _, _) = await TickAsync(FakeResult.Table(SessionColumns, Session(60, 3), Session(60, 3, "suspended"), Session(61, 3, "sleeping", null)), Blockers());
        Assert.Equal([60L, 61L], r.Sessions!.Value!.Select(x => x.Key.Id));
        Assert.Equal("running", r.Sessions.Value![0].Status); // the first (highest priority) row
        Assert.Equal(PaneState.Ok, r.Sessions.State);
    }

    [Fact]
    public async Task S11_S12_durations_come_from_the_server_clock_as_long_milliseconds_beyond_24_days()
    {
        var month = 30L * 24 * 3_600_000; // > int.MaxValue ms
        var (r, sql, _) = await TickAsync(FakeResult.Table(SessionColumns, Session(60, 1, elapsed: month)), Blockers());
        Assert.Equal(month, r.Sessions!.Value!.Single().ElapsedMs);
        Assert.Contains("DATEDIFF_BIG(millisecond, r.start_time, SYSDATETIME()) AS elapsed_ms", sql);
        Assert.Contains("DATEDIFF_BIG(millisecond, tx.begin_time, SYSDATETIME()) AS tx_ms", sql);
        Assert.DoesNotContain("DATEDIFF(", sql);
        Assert.DoesNotContain("@now", sql); // no client clock is sent
    }

    [Fact]
    public async Task While_a_rollback_runs_the_session_transaction_dmv_is_not_read_and_the_pane_says_why()
    {
        // B0 row 9: sys.dm_tran_session_transactions blocks until a terminated idle session's rollback ends, and a client
        // cancel is not processed meanwhile; reading it froze every tick (and the kill preview) for the rollback's length.
        var (r, sql, _) = await TickAsync(FakeResult.Table(SessionColumns, Session(60, 1, rollbackRunning: 1)), Blockers());
        Assert.StartsWith("DECLARE @rb bit = CASE WHEN EXISTS (SELECT 1 FROM sys.dm_tran_active_transactions WHERE transaction_state = 7)", sql);
        Assert.Contains("WHERE @rb = 0 AND st.session_id = s.session_id", sql);
        Assert.Equal(PaneState.Ok, r.Sessions!.State);
        Assert.Equal(SqlServerActivitySession.TxAgesHidden, r.Sessions.Reason);
        var (quiet, _, _) = await TickAsync(FakeResult.Table(SessionColumns, Session(60, 1)), Blockers());
        Assert.Null(quiet.Sessions!.Reason);
    }

    [Fact]
    public async Task S18_without_server_state_the_sessions_query_reads_no_denied_dmv_and_rows_say_why_text_is_missing()
    {
        // Regression: the own-sessions query applied dm_exec_sql_text, which needs VIEW SERVER PERFORMANCE STATE even for
        // the login's own session (300); with errors reported as messages the pane showed an empty list as "ok".
        string sql = "";
        await using var conn = new FakeDbConnection((cmd, _) => { sql = cmd.CommandText; return Task.FromResult(new FakeResult([FakeResult.Table(SessionColumns, Session(60, 1, text: null)), Blockers()])); }, 1);
        await conn.OpenAsync();
        var probe = new SqlServerActivity.Probe(3, 16, "16.0", false, false, false, false, false, false, false, "master");
        var s = new SqlServerActivitySession(probe, SqlServerActivity.Capabilities(probe), K(1), new SqlServerPlans(), new SqlServerDataEdit());
        var r = await s.TickAsync(conn, new TickRequest(true, false, false, false, ActivityMonitor.MaxSessions), CancellationToken.None);
        Assert.DoesNotContain("dm_exec_sql_text", sql);
        Assert.DoesNotContain("dm_exec_connections", sql);
        Assert.DoesNotContain("dm_os_waiting_tasks", sql);
        Assert.Equal(SqlServerActivitySession.OwnTextNote, r.Sessions!.Value!.Single().TextNote);
        Assert.Equal(SqlServerActivitySession.OwnTextNote, (await s.RequestDetailAsync(conn, K(60), CancellationToken.None)).TextNote);
    }

    [Fact]
    public async Task B5_B12_a_self_edge_is_ignored_and_a_parallel_request_blocked_by_two_sessions_lists_both_once()
    {
        var rows = FakeResult.Table(SessionColumns, Session(60, 3), Session(61, 3), Session(62, 3));
        // 60's tasks: one waits on itself (exchange), two on 61, one on 62 (the second result set unions tasks and requests).
        var (r, _, _) = await TickAsync(rows, Blockers((60, 60), (60, 61), (60, 62), (60, 61)));
        var s60 = r.Sessions!.Value!.Single(x => x.Key.Id == 60);
        Assert.Equal([61L, 62L], s60.BlockedBy);
        Assert.Empty(r.Sessions.Value!.Single(x => x.Key.Id == 61).BlockedBy);
        var g = BlockingGraph.Build(r.Sessions.Value!, null);
        Assert.Equal([61L, 62L], g.HeadBlockers.Order());
        Assert.Equal(1, g.BlockedCount); // counted once
    }

    // ------------------------------------------------------------------ top queries SQL

    static PostgresActivity.Probe PgProbe(string[] pssColumns) => new(160_000, "PostgreSQL 16.15", true, true, true, false, true, 1024, "auto",
        true, true, false, true, true, "public", null, pssColumns, true, null, null, null, "postgres", "postgres");

    static IActivitySession PgSession(PostgresActivity.Probe p) =>
        new PostgresActivitySession(p, PostgresActivity.Capabilities(p), K(1), new PostgresDataEdit());

    static FakeResult EmptyTop() => new([
        FakeResult.Table([("k", typeof(string)), ("q", typeof(string)), ("d", typeof(string)), ("c", typeof(long)), ("t", typeof(double)), ("r", typeof(long)),
            ("b", typeof(long)), ("io", typeof(double)), ("tk", typeof(string))]),
        FakeResult.Table([("reset", typeof(string))])]);

    [Fact]
    public async Task T3_T6_pg_stat_statements_columns_are_mapped_from_the_probed_names()
    {
        var sql = new List<string>();
        await using var conn = new FakeDbConnection((cmd, _) => { sql.Add(cmd.CommandText); return Task.FromResult(EmptyTop()); }, 1);
        await conn.OpenAsync();
        string[] old = ["userid", "dbid", "queryid", "query", "calls", "total_time", "rows", "shared_blks_hit", "shared_blks_read", "blk_read_time", "blk_write_time"];
        string[] v17 = ["userid", "dbid", "toplevel", "queryid", "query", "calls", "total_exec_time", "rows", "shared_blks_hit", "shared_blks_read", "shared_blk_read_time", "shared_blk_write_time"];
        await PgSession(PgProbe(old)).TopQueriesAsync(conn, CancellationToken.None);
        await PgSession(PgProbe(v17)).TopQueriesAsync(conn, CancellationToken.None);
        Assert.Contains("total_time::float8", sql[0]);
        Assert.Contains("blk_read_time + blk_write_time", sql[0]);
        Assert.Contains("'t'", sql[0]);                         // no toplevel column before 1.9
        Assert.Contains("total_exec_time::float8", sql[1]);
        Assert.Contains("shared_blk_read_time + shared_blk_write_time", sql[1]);
        Assert.Contains("s.toplevel::text", sql[1]);
    }

    [Fact]
    public async Task T8_postgresql_polls_without_text_and_fetches_each_text_once_per_key()
    {
        var sql = new List<string>();
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            sql.Add(cmd.CommandText);
            return Task.FromResult(cmd.CommandText.Contains("pg_stat_statements(true)", StringComparison.Ordinal)
                ? new FakeResult([FakeResult.Table([("k", typeof(string)), ("q", typeof(string))], ["10:5:42", "SELECT $1"])])
                : EmptyTop());
        }, 1);
        await conn.OpenAsync();
        var s = PgSession(PgProbe(["userid", "dbid", "toplevel", "queryid", "query", "calls", "total_exec_time", "rows", "shared_blks_hit", "shared_blks_read"]));
        await s.TopQueriesAsync(conn, CancellationToken.None);
        Assert.Contains("pg_stat_statements(false)", sql[0]);
        Assert.DoesNotContain("left(s.query", sql[0]);
        Assert.Equal("SELECT $1", (await s.TopQueryTextsAsync(conn, ["10:5:42"], CancellationToken.None))["10:5:42"].Text);
        Assert.Equal("SELECT $1", (await s.TopQueryTextsAsync(conn, ["10:5:42"], CancellationToken.None))["10:5:42"].Text);
        Assert.Single(sql, q => q.Contains("pg_stat_statements(true)", StringComparison.Ordinal)); // the second call was served from the cache
    }

    [Fact]
    public async Task T10_the_incremental_cutoff_is_the_earliest_running_request_start_in_server_time()
    {
        var tenMinutesAgo = new DateTime(2026, 10, 28, 2, 50, 0);
        var cutoffs = new List<object?>();
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            cutoffs.Add(cmd.Parameters.Cast<DbParameter>().Single(p => p.ParameterName == "@cutoff").Value);
            return Task.FromResult(new FakeResult([FakeResult.Table([("c", typeof(DateTime))], [tenMinutesAgo]), FakeResult.Table([("p", typeof(string))])]));
        }, 1);
        await conn.OpenAsync();
        var s = SqlServerActivitySessionAccess.Session(conn);
        var first = await s.TopQueriesAsync(conn, CancellationToken.None);
        var second = await s.TopQueriesAsync(conn, CancellationToken.None);
        Assert.True(first.Value!.Full);
        Assert.Equal(DBNull.Value, cutoffs[0]);                // first run: full read
        Assert.False(second.Value!.Full);
        Assert.Equal(tenMinutesAgo, cutoffs[1]);               // the server's MIN(start_time) of the 10-minute request, unchanged
    }

    [Fact]
    public void Monitor_connections_on_sql_server_raise_user_errors_instead_of_reporting_them_as_messages()
    {
        // Regression: the provider opens every connection with FireInfoMessageEventOnUserErrors (query tabs stream errors as
        // messages), so on monitor connections a denied DMV read (300) or a refused KILL (6102) read as an empty success.
        using var conn = new Microsoft.Data.SqlClient.SqlConnection { FireInfoMessageEventOnUserErrors = true };
        new SqlServerActivity(new SqlServerPlans(), new SqlServerDataEdit()).PrepareMonitorConnection(conn);
        Assert.False(conn.FireInfoMessageEventOnUserErrors);
    }

    // ------------------------------------------------------------------ more provider reads (fake connection)

    static async Task<(T Result, List<string> Sql)> WithFake<T>(Func<FakeDbCommand, FakeResult> respond, Func<DbConnection, Task<T>> run)
    {
        var sql = new List<string>();
        await using var conn = new FakeDbConnection((cmd, _) => { sql.Add(cmd.CommandText); return Task.FromResult(respond(cmd)); }, 1);
        await conn.OpenAsync();
        return (await run(conn), sql);
    }

    static PostgresActivity.Probe PgProbeWith(bool checkpointer = true, bool opBytes = true, bool ioTiming = true) => new(170_000, "PostgreSQL 17.11", true, true, true, false,
        ioTiming, 1024, "auto", true, opBytes, checkpointer, true, true, "public", null, ["queryid", "calls", "total_exec_time"], true, null, null, null, "postgres", "postgres");

    [Fact]
    public async Task S6_encrypted_module_text_is_marked_unavailable_not_shown_empty()
    {
        var row = Session(60, 1, text: null);
        row[13] = 1; // encrypted
        var (r, _, _) = await TickAsync(FakeResult.Table(SessionColumns, row), Blockers());
        var x = r.Sessions!.Value!.Single();
        Assert.Null(x.TextId);
        Assert.Equal("text unavailable (encrypted)", x.TextNote);
    }

    [Fact]
    public async Task K18_a_postgresql_permission_refusal_is_mapped_to_a_clear_failure_and_not_retried()
    {
        var calls = 0;
        var (r, _) = await WithFake(_ => { calls++; throw new Npgsql.PostgresException("permission denied to terminate process", "ERROR", "ERROR", "42501"); },
            c => PgSession(PgProbeWith()).TerminateSessionAsync(c, new SessionKey { Id = 42, Instance = "1" }, CancellationToken.None));
        Assert.Equal((KillOutcome.Failed, "permission"), (r.Outcome, r.Code));
        Assert.Contains("permission denied to terminate process", r.Message);
        Assert.Equal(1, calls); // no retry
    }

    [Fact]
    public async Task LK1_LK2_LK3_postgresql_locks_in_other_databases_keep_their_oid_shared_catalogs_are_named_and_no_connection_is_opened()
    {
        var at = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);
        var table = FakeResult.Table([("pid", typeof(int)), ("bs", typeof(DateTime)), ("t", typeof(string)), ("db", typeof(string)), ("rel", typeof(string)), ("oid", typeof(long)),
            ("mode", typeof(string)), ("g", typeof(bool)), ("n", typeof(long)), ("total", typeof(int)), ("shared", typeof(bool))],
            [101, at, "relation", "db2", null, 16400L, "AccessShareLock", true, 1L, 2, false],
            [102, at, "relation", null, "pg_catalog.pg_database", 1262L, "AccessShareLock", true, 1L, 2, true]);
        var opened = 0;
        var (r, sql) = await WithFake(_ => { opened++; return new FakeResult([table]); }, c => PgSession(PgProbeWith()).LocksAsync(c, 2_000, CancellationToken.None));
        var other = r.Value!.Single(l => l.Key.Id == 101);
        Assert.Equal(("db2.16400", "other database"), (other.Object, other.ObjectNote)); // LK1 / LK3: never a local name
        Assert.Equal("pg_catalog.pg_database", r.Value!.Single(l => l.Key.Id == 102).Object); // LK2
        Assert.Contains("LEFT JOIN pg_class c ON c.oid = l.relation AND (l.database = (SELECT oid FROM me) OR l.database = 0)", sql.Single());
        Assert.Equal(1, opened); // one query on the monitor connection; no connection to db2
    }

    static FakeResult PgMetrics() => new([
        FakeResult.Table([("tps", typeof(long)), ("hit", typeof(long)), ("read", typeof(long)), ("temp", typeof(long)), ("dl", typeof(long)), ("reset", typeof(string))], [10L, 9L, 1L, 0L, 0L, "r1"]),
        FakeResult.Table([("a", typeof(long)), ("i", typeof(long))], [1L, 0L]),
        FakeResult.Table([("c", typeof(long)), ("reset", typeof(string))], [5L, "r2"])]);

    [Fact]
    public async Task D13_postgresql_reads_checkpoints_from_the_view_its_catalog_has()
    {
        var req = new TickRequest(false, false, true, false, ActivityMonitor.MaxSessions);
        var (v17, sql17) = await WithFake(_ => PgMetrics(), c => PgSession(PgProbeWith(checkpointer: true)).TickAsync(c, req, CancellationToken.None));
        var (v16, sql16) = await WithFake(_ => PgMetrics(), c => PgSession(PgProbeWith(checkpointer: false)).TickAsync(c, req, CancellationToken.None));
        Assert.Contains("FROM pg_stat_checkpointer", sql17.Single());
        Assert.DoesNotContain("pg_stat_bgwriter", sql17.Single());
        Assert.Contains("FROM pg_stat_bgwriter", sql16.Single());
        Assert.All(new[] { v17, v16 }, t => Assert.Equal(5, t.Metrics!.Value!.Single(m => m.Key == "checkpoints").Value));
    }

    static FakeResult PgIo() => new([FakeResult.Table([("bt", typeof(string)), ("o", typeof(string)), ("c", typeof(string)), ("r", typeof(long)), ("w", typeof(long)), ("rt", typeof(double)), ("wt", typeof(double))],
        ["client backend", "relation", "normal", 8192L, 0L, 0.0, 0.0])]);

    [Fact]
    public async Task A_pg_wait_sampling_profile_that_got_costly_to_read_falls_back_to_tick_sampled_waits_and_says_why()
    {
        // B0 row 16: profile_pid = on keeps every past backend; after churn the profile read took 241 ms per tick.
        var probe = PgProbeWith() with { PwsSchema = "public", PwsUnavailable = null, PwsPeriodMs = 10 };
        var s = (PostgresActivitySession)PgSession(probe);
        Assert.False(s.WaitsFromSessions);
        var profileReads = 0;
        await using var conn = new FakeDbConnection(async (cmd, _) =>
        {
            if (!cmd.CommandText.Contains("pg_wait_sampling_profile", StringComparison.Ordinal)) return PgMetrics();
            profileReads++;
            await Task.Delay((int)PostgresActivitySession.ProfileReadLimitMs + 30);
            return new FakeResult([FakeResult.Table([("t", typeof(string)), ("e", typeof(string)), ("n", typeof(long))], ["Lock", "transactionid", 5L])]);
        }, 1);
        await conn.OpenAsync();
        var req = new TickRequest(false, true, true, false, ActivityMonitor.MaxSessions);
        for (var i = 0; i < 3; i++) Assert.NotNull((await s.TickAsync(conn, req, CancellationToken.None)).Waits);
        Assert.True(s.WaitsFromSessions); // three slow reads: tick-sampled from now on
        Assert.Contains("pg_wait_sampling", s.Capabilities.Panes.Single(p => p.Pane == MonitorPane.Waits).Reason);
        await s.TickAsync(conn, req with { Sessions = true }, CancellationToken.None);
        Assert.Equal(3, profileReads); // the profile is not read any more
    }

    [Fact]
    public async Task D14_postgresql_io_is_cluster_scoped_bytes_follow_the_probed_columns_and_stalls_are_null_without_track_io_timing()
    {
        var req = new TickRequest(false, false, false, true, ActivityMonitor.MaxSessions);
        var (on, sqlOn) = await WithFake(_ => PgIo(), c => PgSession(PgProbeWith(opBytes: true, ioTiming: true)).TickAsync(c, req, CancellationToken.None));
        var (off, sqlOff) = await WithFake(_ => PgIo(), c => PgSession(PgProbeWith(opBytes: false, ioTiming: false)).TickAsync(c, req, CancellationToken.None));
        Assert.Contains("op_bytes", sqlOn.Single());
        Assert.Contains("read_bytes", sqlOff.Single());
        var a = on.Io!.Value!.Single();
        Assert.Equal(("cluster", null, null, "client backend"), (a.Scope, a.Database, a.File, a.BackendType));
        Assert.Equal(0, a.ReadStallMs); // measured
        var b = off.Io!.Value!.Single();
        Assert.Null(b.ReadStallMs);  // not measured: null, never 0
        Assert.Null(b.WriteStallMs);
        Assert.Contains("track_io_timing", off.Io.Reason);
    }

    [Fact]
    public async Task D15_sql_server_io_rows_are_per_file_with_database_and_file_and_no_grouping_fields()
    {
        var io = FakeResult.Table([("db", typeof(string)), ("f", typeof(string)), ("r", typeof(long)), ("w", typeof(long)), ("rs", typeof(long)), ("ws", typeof(long))], ["SalesDB", "SalesDB_log", 1L, 2L, 3L, 4L]);
        var (r, sql) = await WithFake(_ => new FakeResult([io]), c => SqlServerActivitySessionAccess.Session(c).TickAsync(c, new TickRequest(false, false, false, true, ActivityMonitor.MaxSessions), CancellationToken.None));
        Assert.Contains("sys.dm_io_virtual_file_stats", sql.Single());
        var x = r.Io!.Value!.Single();
        Assert.Equal(("file", "SalesDB", "SalesDB_log"), (x.Scope, x.Database, x.File));
        Assert.Null(x.BackendType);
        Assert.Null(x.Object);
        Assert.Null(x.Context);
    }

    [Fact]
    public async Task X8_with_system_health_stopped_the_deadlock_pane_is_degraded_with_the_reason()
    {
        var (r, sql) = await WithFake(_ => new FakeResult([FakeResult.Table([("n", typeof(int))], [0]), FakeResult.Table([("x", typeof(string))]), FakeResult.Table([("f", typeof(string))])]),
            c => SqlServerDeadlocks.ReadAsync(c, 0, long.MaxValue, 200, CancellationToken.None));
        Assert.Equal(PaneState.Degraded, r.State);
        Assert.Contains("system_health event session is not running", r.Reason);
        Assert.Single(sql); // the event file is not read
    }

    [Fact]
    public void Over_the_tracking_cap_top_queries_stop_with_the_reason_and_start_again_from_a_full_read_that_fits()
    {
        TopQueryCounter C(int i, long execs) => new(TopQueryKey.Of("e" + i), "h", null, "t", execs, execs, execs, execs, null, execs);
        const int cap = 1_000; // the production cap is 200,000; the logic is the same
        var big = Enumerable.Range(0, cap + 1).Select(i => C(i, 1)).ToArray();
        var t = new TopQueriesTracker(cap);
        var (s0, top0) = t.Update(new TopQueryRead(big, true, null), "cpu", false);
        Assert.Equal(PaneState.Degraded, s0);
        Assert.Empty(top0);
        Assert.Contains("statement entries", t.Note);
        Assert.Equal(0, t.TrackedEntries); // nothing kept over the cap
        // An incremental read has no baseline to subtract from: still stopped, still saying why.
        Assert.Equal(PaneState.Degraded, t.Update(new TopQueryRead([C(1, 5)], false, null), "cpu", false).State);
        Assert.NotNull(t.Note);
        // A full read that fits is a new baseline; deltas resume after it.
        Assert.Equal(PaneState.Collecting, t.Update(new TopQueryRead([C(1, 5), C(2, 5)], true, null), "cpu", false).State);
        Assert.Null(t.Note);
        var (s3, top3) = t.Update(new TopQueryRead([C(1, 7)], false, null), "cpu", false);
        Assert.Equal(PaneState.Ok, s3);
        Assert.Equal(2, Assert.Single(top3).Execs);
        // New entries from incremental reads that take the tracker over the cap stop it too.
        var t2 = new TopQueriesTracker(cap);
        t2.Update(new TopQueryRead([C(-1, 1)], true, null), "cpu", false);
        Assert.Equal(PaneState.Degraded, t2.Update(new TopQueryRead(big, false, null), "cpu", false).State);
        Assert.Equal(0, t2.TrackedEntries);
    }

    [Fact]
    public void Since_start_totals_keep_the_most_recently_active_query_groups_and_say_how_many_were_dropped()
    {
        // Since-start totals grew with every new query shape over the monitor's uptime; they are bounded now.
        TopQueryCounter C(string hash, long execs) => new(TopQueryKey.Of(hash), hash, null, "t", execs, execs, execs, execs, null, execs);
        const int cap = 100; // the production cap is 10,000
        var t = new TopQueriesTracker(maxSinceStartGroups: cap);
        t.Update(new TopQueryRead([C("keep", 1)], true, null), "cpu", true);
        var groups = cap + 1;
        for (var i = 0; i < groups; i += 10) // new groups that each run once; "keep" runs on every read
            t.Update(new TopQueryRead([C("keep", 2 + i), .. Enumerable.Range(i, Math.Min(10, groups - i)).Select(j => C("g" + j, 1))], false, null), "cpu", true);
        Assert.True(t.SinceStartGroups <= cap, $"{t.SinceStartGroups} groups");
        var (_, top) = t.Update(new TopQueryRead([C("keep", 100_000)], false, null), "executions", true);
        Assert.Equal("keep", top[0].QueryHash);
        Assert.Equal(99_999, top[0].Execs); // the active group's totals are intact
        Assert.Contains("dropped", t.Note);
        t.Update(new TopQueryRead([C("keep", 100_001)], false, null), "cpu", false);
        Assert.Null(t.Note); // the interval view drops nothing
    }

    [Fact]
    public async Task SQL_Server_top_queries_carry_hash_and_text_key_only_for_entries_that_ran_since_the_cutoff()
    {
        byte[] a = [.. Enumerable.Repeat((byte)0xA1, 64)], b = [.. Enumerable.Repeat((byte)0xB2, 64)]; // plan handles
        await using var conn = new FakeDbConnection((cmd, _) => Task.FromResult(new FakeResult([
            FakeResult.Table([("c", typeof(DateTime))], [new DateTime(2026, 10, 28, 3, 0, 0)]),
            FakeResult.Table([("k", typeof(byte[])), ("ran", typeof(int)), ("s", typeof(int)), ("e", typeof(int)), ("h", typeof(string)), ("q", typeof(string)),
                    ("n", typeof(long)), ("cpu", typeof(long)), ("dur", typeof(long)), ("reads", typeof(long)), ("rows", typeof(long))],
                [a, 1, 0, -1, "0x0200AB", "0x00000000000000AA", 3L, 3_000L, 4_000L, 30L, 3L],
                [b, 0, 10, 20, DBNull.Value, DBNull.Value, 1L, 1_000L, 1_000L, 10L, 1L]),
            FakeResult.Table([("n", typeof(long))], [5_000L])])), 1);
        await conn.OpenAsync();
        var read = (await SqlServerActivitySessionAccess.Session(conn).TopQueriesAsync(conn, CancellationToken.None)).Value!;
        var ra = read.Entries.Single(x => x.Entry == SqlServerActivitySessionAccess.EntryKey(a, 0, -1));
        Assert.Equal(("0x00000000000000AA", "0x0200AB:0:-1"), (ra.QueryHash, ra.TextKey));
        var rb = read.Entries.Single(x => x.Entry == SqlServerActivitySessionAccess.EntryKey(b, 10, 20));
        Assert.Equal(("", ""), (rb.QueryHash, rb.TextKey)); // did not run since the cutoff: no strings
        // The tracker never takes a delta from such an entry, even one it did not track before.
        var t = new TopQueriesTracker();
        t.Update(new TopQueryRead([ra with { Execs = 1, CpuMs = 1, DurationMs = 1 }], true, null), "cpu", false);
        var (_, top) = t.Update(read, "cpu", false);
        Assert.Equal("0x00000000000000AA", Assert.Single(top).QueryHash);
        Assert.Equal(2, top[0].Execs);
    }

    [Fact]
    public async Task T1_a_plan_cache_flush_seen_on_an_incremental_read_triggers_a_full_read_at_once()
    {
        var cache = 5_000L;
        var cutoffs = new List<object?>();
        var fulls = new List<object?>();
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            cutoffs.Add(cmd.Parameters.Cast<DbParameter>().Single(p => p.ParameterName == "@cutoff").Value);
            fulls.Add(cmd.Parameters.Cast<DbParameter>().Single(p => p.ParameterName == "@full").Value);
            return Task.FromResult(new FakeResult([FakeResult.Table([("c", typeof(DateTime))], [new DateTime(2026, 10, 28, 3, 0, 0)]),
                FakeResult.Table([("p", typeof(string))]), FakeResult.Table([("n", typeof(long))], [cache])]));
        }, 1);
        await conn.OpenAsync();
        var s = SqlServerActivitySessionAccess.Session(conn);
        Assert.True((await s.TopQueriesAsync(conn, CancellationToken.None)).Value!.Full);  // first: full
        Assert.False((await s.TopQueriesAsync(conn, CancellationToken.None)).Value!.Full); // incremental
        cache = 3;                                                                           // DBCC FREEPROCCACHE
        var after = await s.TopQueriesAsync(conn, CancellationToken.None);
        Assert.True(after.Value!.Full);
        Assert.Equal(4, cutoffs.Count);           // the incremental read, then the full one in the same call
        Assert.Equal(true, fulls[3]);
        Assert.Equal(cutoffs[2], cutoffs[3]);     // a full read keeps the cutoff: entries that ran since it still yield deltas
        // The tracker then reports the reset (≥ 90 % of entries gone on a full read, T1/T2).
        var t = new TopQueriesTracker();
        TopQueryCounter Q(string k) => new(TopQueryKey.Of(k), "h" + k, null, k, 1, 1, 1, 1, null, 1);
        t.Update(new TopQueryRead([.. Enumerable.Range(0, 20).Select(i => Q("p" + i))], true, null), "cpu", false);
        Assert.Equal(PaneState.ResetDetected, t.Update(new TopQueryRead([Q("p0")], true, null), "cpu", false).State);
    }

    // ------------------------------------------------------------------ deadlock sources

    /// <summary>The system_health sources batch: session running, event-file path, the file being written.</summary>
    static FakeResult DeadlockSources(int running = 1, string? path = "system_health.xel", string? current = "/log/system_health_0_1.xel") => new([
        FakeResult.Table([("n", typeof(int))], [running]),
        FakeResult.Table([("f", typeof(string))], path == null ? [] : [new object?[] { path }]),
        FakeResult.Table([("c", typeof(string))], current == null ? [] : [new object?[] { current }])]);

    static bool IsRingRead(DbCommand cmd) => cmd.CommandText.Contains("ring_buffer", StringComparison.Ordinal);

    [Fact]
    public async Task X9_an_unreadable_event_file_falls_back_to_the_ring_buffer_with_the_reason()
    {
        var calls = 0;
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            calls++;
            if (cmd.CommandText.Contains("fn_xe_file_target_read_file", StringComparison.Ordinal)) throw new FakeDbException("Access is denied to the log directory");
            return Task.FromResult(IsRingRead(cmd) ? FakeResult.Value(MonitorTests.Deadlock) : DeadlockSources());
        }, 1);
        await conn.OpenAsync();
        var r = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None);
        Assert.Equal(PaneState.Ok, r.State);
        Assert.Single(r.Value!);
        Assert.Contains("event file could not be read", r.Reason);
        Assert.Contains("ring buffer only", r.Reason);
        Assert.Equal(3, calls); // sources, the failed file read, the ring buffer
    }

    [Fact]
    public async Task X9_the_ring_buffer_is_read_only_when_the_event_files_cannot_be()
    {
        // Soak finding: the ring buffer's XML (up to ~4 MB) was read on every poll — ~43 MB allocated and ~0.7 s per read on
        // the fixture — although the event files hold every event it holds. Read it only when they cannot be read.
        var sql = new List<string>();
        var fileFails = false;
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            sql.Add(cmd.CommandText);
            if (IsRingRead(cmd)) return Task.FromResult(FakeResult.Value(MonitorTests.Deadlock));
            if (!cmd.CommandText.Contains("fn_xe_file_target_read_file", StringComparison.Ordinal)) return Task.FromResult(DeadlockSources());
            if (fileFails) throw new FakeDbException("The file could not be opened");
            return Task.FromResult(new FakeResult([FakeResult.Table([("e", typeof(string)), ("f", typeof(string)), ("o", typeof(long))],
                [MonitorTests.Deadlock, "/log/system_health_0_1.xel", 100L])]));
        }, 1);
        await conn.OpenAsync();
        var cursor = new SqlServerDeadlocks.FileCursor();
        var full = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None);  // the pane's full read
        var priming = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None, cursor);
        var resumed = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None, cursor);
        Assert.Equal(6, sql.Count);
        Assert.DoesNotContain(sql, s => s.Contains("ring_buffer", StringComparison.Ordinal));
        Assert.Single(full.Value!);
        Assert.Single(priming.Value!);
        Assert.Null(resumed.Reason);
        // The files fail: this read falls back to the ring buffer and says so; the export finds the event there too.
        fileFails = true;
        sql.Clear();
        var fallback = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None, cursor);
        Assert.Equal(3, sql.Count);
        Assert.Contains("ring_buffer", sql[2]);
        Assert.Single(fallback.Value!);
        Assert.Contains("ring buffer only", fallback.Reason);
        Assert.StartsWith("<deadlock", await SqlServerDeadlocks.XmlAsync(conn, fallback.Value![0].Id, 0, long.MaxValue, CancellationToken.None));
        // No event_file target (a customised system_health): the ring buffer is the only source.
        await using var ringOnly = new FakeDbConnection((cmd, _) =>
            Task.FromResult(IsRingRead(cmd) ? FakeResult.Value(MonitorTests.Deadlock) : DeadlockSources(path: null, current: null)), 1);
        await ringOnly.OpenAsync();
        Assert.Single((await SqlServerDeadlocks.ReadAsync(ringOnly, 0, long.MaxValue, 200, CancellationToken.None)).Value!);
    }

    [Fact]
    public async Task X12_no_deadlocks_or_none_in_the_time_window_is_an_empty_ok_result()
    {
        // X12: an empty history and a window that excludes every event are the empty state, not an error.
        var events = MonitorTests.Deadlock;
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            if (IsRingRead(cmd)) return Task.FromResult(FakeResult.None);
            if (!cmd.CommandText.Contains("fn_xe_file_target_read_file", StringComparison.Ordinal)) return Task.FromResult(DeadlockSources());
            var rows = events == "" ? [] : new[] { new object?[] { events, null, null } };
            return Task.FromResult(new FakeResult([FakeResult.Table([("e", typeof(string)), ("f", typeof(string)), ("o", typeof(long))], rows)]));
        }, 1);
        await conn.OpenAsync();
        var at = Assert.Single((await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None)).Value!).At;
        foreach (var (from, to) in new[] { (at + 1, long.MaxValue), (0L, at - 1) })
        {
            var outside = await SqlServerDeadlocks.ReadAsync(conn, from, to, 200, CancellationToken.None);
            Assert.Equal(PaneState.Ok, outside.State);
            Assert.Empty(outside.Value!);
            Assert.Null(outside.Reason);
        }
        events = "";
        var none = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None);
        Assert.Equal(PaneState.Ok, none.State);
        Assert.Empty(none.Value!);
        Assert.Null(none.Reason);
    }

    [Fact]
    public async Task X16_the_poll_reads_the_event_files_once_then_only_what_came_after_its_last_position()
    {
        // The ring buffer's XML is capped at 4 MB and then leaves out the newest events, so the poll must read the files —
        // but re-reading all of them every 30 s costs in proportion to their size (B0 row 15): resume after a cursor.
        var fileReads = new List<(object? File, object? Offset)>();
        var paths = new List<object?>();
        var newDeadlock = MonitorTests.Deadlock.Replace("<RingBufferTarget>", "").Replace("</RingBufferTarget>", "");
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            if (!cmd.CommandText.Contains("fn_xe_file_target_read_file", StringComparison.Ordinal))
                return Task.FromResult(DeadlockSources()); // "/log/system_health_0_1.xel" is the file being written
            var ps = cmd.Parameters.Cast<DbParameter>().ToDictionary(p => p.ParameterName, p => p.Value);
            fileReads.Add((ps["@file"], ps["@offset"]));
            paths.Add(ps["@path"]);
            var rows = fileReads.Count == 1
                ? new[] { new object?[] { "", "/log/system_health_0_1.xel", 100L }, new object?[] { "", "/log/system_health_0_1.xel", 200L } }
                : new[] { new object?[] { newDeadlock, "/log/system_health_0_1.xel", 300L } };
            return Task.FromResult(new FakeResult([FakeResult.Table([("e", typeof(string)), ("f", typeof(string)), ("o", typeof(long))], rows)]));
        }, 1);
        await conn.OpenAsync();
        var cursor = new SqlServerDeadlocks.FileCursor();
        var first = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None, cursor);
        Assert.Empty(first.Value!);
        Assert.Equal((DBNull.Value, DBNull.Value), fileReads[0]);                      // primes with a full read…
        Assert.Equal("/log/system_health_0_1.xel", paths[0]);                         // …of the current file only (soak)
        Assert.Equal(("/log/system_health_0_1.xel", 200L), (cursor.File, cursor.Offset));  // the last event of any kind
        var second = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None, cursor);
        Assert.Equal(("/log/system_health_0_1.xel", (object)200L), (fileReads[1].File, fileReads[1].Offset)); // resumes after it
        Assert.Equal("system_health*.xel", paths[1]);                                 // across rollover files
        Assert.Single(second.Value!);                                                  // the new deadlock
        Assert.Equal(300L, cursor.Offset);
    }

    [Fact]
    public async Task X16_a_priming_read_cut_twice_by_the_poll_limit_stops_reading_the_files_and_says_why()
    {
        // Soak finding: a priming read cut by the job's time limit reset the cursor, so every poll repeated it and failed.
        var fileReads = 0;
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            if (IsRingRead(cmd)) return Task.FromResult(FakeResult.None);
            if (!cmd.CommandText.Contains("fn_xe_file_target_read_file", StringComparison.Ordinal)) return Task.FromResult(DeadlockSources());
            fileReads++;
            throw new OperationCanceledException(); // the poll job's limit fired during the read
        }, 1);
        await conn.OpenAsync();
        var cursor = new SqlServerDeadlocks.FileCursor();
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<OperationCanceledException>(() => SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None, cursor));
        Assert.True(cursor.GaveUp);
        var third = await SqlServerDeadlocks.ReadAsync(conn, 0, long.MaxValue, 200, CancellationToken.None, cursor);
        Assert.Equal(2, fileReads);                       // not asked again
        Assert.Contains("ring buffer only", third.Reason); // and says so
    }

    [Fact]
    public void X10_azure_sql_database_has_no_server_deadlock_history_and_says_why()
    {
        var probe = new SqlServerActivity.Probe(5, 12, "12.0", true, true, true, true, true, true, true, "db");
        var deadlocks = SqlServerActivity.Capabilities(probe).Panes.Single(p => p.Pane == MonitorPane.Deadlocks);
        Assert.False(deadlocks.Available);
        Assert.False(string.IsNullOrEmpty(deadlocks.Reason));
    }

    // ------------------------------------------------------------------ PostgreSQL sessions read (fake connection)

    static readonly (string, Type)[] PgSessionColumns =
    [
        ("pid", typeof(int)), ("backend_start", typeof(DateTime)), ("usename", typeof(string)), ("client_hostname", typeof(string)), ("client_addr", typeof(string)),
        ("application_name", typeof(string)), ("datname", typeof(string)), ("state", typeof(string)), ("backend_type", typeof(string)),
        ("wait_event_type", typeof(string)), ("wait_event", typeof(string)), ("blockers", typeof(int[])), ("query", typeof(string)), ("query_bytes", typeof(int)),
        ("query_id", typeof(long)), ("elapsed_ms", typeof(long)), ("tx_ms", typeof(long)), ("state_ms", typeof(long)), ("query_start", typeof(DateTime)), ("total", typeof(int)),
    ];

    /// <summary>
    /// pg_stat_activity rows. Npgsql reads timestamptz as DateTimeKind.Utc; a DataTable column keeps that only in Utc mode
    /// (its default turns the values into local times, shifting every identity by the machine's UTC offset).
    /// </summary>
    static DataTable PgSessions(params object?[][] rows)
    {
        var t = FakeResult.Table(PgSessionColumns);
        foreach (DataColumn c in t.Columns) if (c.DataType == typeof(DateTime)) c.DateTimeMode = DataSetDateTime.Utc;
        foreach (var r in rows) t.Rows.Add(r.Select(v => v ?? DBNull.Value).ToArray());
        return t;
    }

    static async Task<(TickResult Result, string Sql, FakeDbCommand Cmd)> PgTickAsync(DataTable sessions, TickRequest req, PostgresActivity.Probe? probe = null)
    {
        string sql = "";
        FakeDbCommand? seen = null;
        await using var conn = new FakeDbConnection((cmd, _) => { sql = cmd.CommandText; seen = cmd; return Task.FromResult(new FakeResult([sessions])); }, 1);
        await conn.OpenAsync();
        var r = await PgSession(probe ?? PgProbeWith()).TickAsync(conn, req, CancellationToken.None);
        return (r, sql, seen!);
    }

    [Fact]
    public async Task S2_S3_B4_K11_K29_postgresql_sessions_read_maps_identity_backend_type_null_names_blockers_hidden_identity_and_truncation()
    {
        // The PostgreSQL counterpart of the SQL Server reader tests above: pg_stat_activity rows as the sessions query returns them.
        var start201 = new DateTime(2026, 10, 28, 3, 0, 1, DateTimeKind.Utc).AddTicks(1_234_560); // microsecond precision, as PostgreSQL sends
        var start202 = new DateTime(2026, 10, 28, 2, 59, 0, DateTimeKind.Utc).AddTicks(70);
        var start90 = new DateTime(2026, 10, 27, 23, 0, 0, DateTimeKind.Utc);
        var qs201 = new DateTime(2026, 10, 28, 3, 0, 5, DateTimeKind.Utc).AddTicks(9_990);
        var rows = PgSessions(
            // A client backend waiting on a lock: pg_blocking_pids may name a pid twice and the backend itself (parallel workers).
            [201, start201, "app", null, "10.0.0.5", "billing", "sales", "active", "client backend", "Lock", "transactionid", new[] { 203, 202, 203, 201 },
             "UPDATE t SET v = 1 WHERE id = 1", 31, 4242L, 9_000L, 9_500L, 9_000L, qs201, 7],
            // Its blocker: idle in transaction, a statement the server cut at track_activity_query_size.
            [202, start202, "app", "laptop-7", "10.0.0.6", "psql", "sales", "idle in transaction", "client backend", "Client", "ClientRead", null,
             new string('x', 1023), 1023, null, null, 60_000L, 59_000L, null, 7],
            // A background process: NULL user, host, database and state, empty application name and query.
            [90, start90, null, null, null, "", null, null, "autovacuum launcher", "Activity", "AutoVacuumMain", null, "", 0, null, null, null, null, null, 7],
            // Another role's backend without pg_read_all_stats: backend_start and state hidden, query replaced.
            [300, null, "other_role", null, null, "app2", "sales", null, "client backend", null, null, null, "<insufficient privilege>", 24, null, null, null, null, null, 7],
            // Beyond the cap: the query asks for one row more than it keeps.
            [301, start90, "app", null, null, "x", "sales", "idle", "client backend", "Client", "ClientRead", null, "SELECT 1", 8, null, null, null, 1_000L, null, 7]);
        var (r, sql, cmd) = await PgTickAsync(rows, new TickRequest(true, false, false, false, 4));

        // S2: truncation keeps the server's priority order and reports the total; the query asks for max + 1.
        Assert.Equal(PaneState.Truncated, r.Sessions!.State);
        Assert.Equal(7, r.Sessions.Total);
        Assert.Equal([201L, 202L, 90L, 300L], r.Sessions.Value!.Select(x => x.Key.Id));
        Assert.Equal(5, cmd.Param("max"));
        Assert.Equal(DBNull.Value, cmd.Param("db"));
        Assert.Contains("ORDER BY CASE WHEN cardinality(a.blockers) > 0 THEN 0 WHEN b.pid IS NOT NULL THEN 1 WHEN a.state = 'active' THEN 2 ELSE 3 END, a.pid", sql);
        Assert.Contains("LIMIT @max", sql);
        Assert.Contains("clock_timestamp() - a.query_start", sql); // S11: durations on the server clock
        var byId = r.Sessions.Value!.ToDictionary(x => x.Key.Id);

        // Identity (K13/K4): pid + backend_start in microseconds, round-tripping exactly.
        var v = byId[201];
        Assert.Equal(PostgresActivity.FormatMicros(start201), v.Key.Instance);
        Assert.True(PostgresActivity.TryParseMicros(v.Key.Instance, out var back) && back == start201);
        Assert.Null(v.IdentityHidden);
        Assert.Equal(("app", "10.0.0.5", "billing", "sales", "active"), (v.Login, v.Host, v.Program, v.Database, v.Status)); // host falls back to the address
        Assert.True(v.IsUserSession);
        Assert.Equal("client backend", v.BackendType);
        Assert.Equal(PostgresWaits.Name("Lock", "transactionid"), v.WaitType);
        // B4: every blocker once, ordered, never the backend itself.
        Assert.Equal([202L, 203L], v.BlockedBy);
        Assert.Equal("p201", v.TextId);
        Assert.Equal("UPDATE t SET v = 1 WHERE id = 1", r.SessionTexts!["p201"]);
        Assert.Equal("4242", v.QueryHash);
        Assert.Equal((9_000L, 9_500L, 1), (v.ElapsedMs, v.TxElapsedMs, v.OpenTx));
        Assert.Equal(PostgresActivity.FormatMicros(qs201), v.StatementStart); // the K26 cancel guard compares this

        var blocker = byId[202];
        Assert.Empty(blocker.BlockedBy);
        Assert.Equal("laptop-7", blocker.Host);
        Assert.Null(blocker.StatementStart); // not running: nothing to cancel (K27)
        Assert.Null(blocker.ElapsedMs);
        Assert.Equal(1, blocker.OpenTx);
        Assert.Contains("track_activity_query_size = 1024", blocker.TextNote);

        // S3 / S10 / K11: a background process has no user, host, database or text and is not a user session.
        var bg = byId[90];
        Assert.Equal((null, null, null, null), (bg.Login, bg.Host, bg.Program, bg.Database));
        Assert.Equal("autovacuum launcher", bg.Status); // NULL state: the backend type is shown instead
        Assert.False(bg.IsUserSession);
        Assert.Equal("autovacuum launcher", bg.BackendType);
        Assert.Null(bg.TextId);
        Assert.Equal(0, bg.OpenTx);
        Assert.False(r.SessionTexts!.ContainsKey("p90"));

        // K29: another role's backend keeps its row but no identity, so the kill guard can refuse it at the boundary.
        var hidden = byId[300];
        Assert.Equal("", hidden.Key.Instance);
        Assert.Equal("requires pg_read_all_stats", hidden.IdentityHidden);
        Assert.Equal("hidden", hidden.Status);
        Assert.Null(hidden.TextId);
        Assert.Equal("hidden (requires pg_read_all_stats)", hidden.TextNote);
        Assert.Null(hidden.OpenTx);
        Assert.True(hidden.IsUserSession);
        Assert.False(r.SessionTexts!.ContainsKey("p300"));
        // The empty identity passes the shape check on purpose and is refused by the guard with its reason, before any SQL.
        var p = new MonitorKillParams { MonitorId = "m", Target = hidden.Key, Mode = KillMode.TerminateSession, ConfirmedKill = true };
        Assert.Null(KillGuard.ValidateShape(p, new PostgresProvider().Activity!, Engine.Pg));
        var refusal = KillGuard.Check(new KillGuard.Context(p, EnvironmentKind.Dev, true, byId.Values.ToDictionary(x => x.Key), new SessionKey { Id = 1, Instance = "1" },
            new ConnectionRegistry(), PostgresActivity.Capabilities(PgProbeWith()), new HashSet<SessionKey>()));
        Assert.Equal("identityHidden", refusal!.Code);
    }

    [Fact]
    public async Task S1_S2_postgresql_sessions_read_under_the_cap_is_ok_and_the_database_filter_is_a_typed_parameter()
    {
        var at = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);
        var rows = PgSessions(
            [201, at, "app", null, null, "a", "sales", "active", "client backend", null, null, null, "SELECT 1", 8, null, 5L, null, 5L, at, 1]);
        var (r, _, cmd) = await PgTickAsync(rows, new TickRequest(true, false, false, false, ActivityMonitor.MaxSessions, "sales"));
        Assert.Equal(PaneState.Ok, r.Sessions!.State);
        Assert.Null(r.Sessions.Total);
        Assert.Null(Assert.Single(r.Sessions.Value!).WaitType); // no wait event: no wait type (the sampler then counts nothing)
        Assert.Equal("sales", cmd.Param("db"));
        var (empty, _, _) = await PgTickAsync(PgSessions(), new TickRequest(true, false, false, false, ActivityMonitor.MaxSessions));
        Assert.Equal(PaneState.Ok, empty.Sessions!.State);
        Assert.Empty(empty.Sessions.Value!);
    }

    [Fact]
    public async Task D19_without_query_ids_postgresql_rows_have_no_query_hash_keep_their_own_text_and_say_why()
    {
        // compute_query_id = off: pg_stat_activity.query_id is NULL. Rows are not grouped (QueryHash null), each keeps its own
        // text (Core's TextId is the text's content hash, never the query id) and the capabilities say why.
        var at = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);
        var rows = PgSessions(
            [201, at, "app", null, null, "a", "sales", "active", "client backend", null, null, null, "SELECT 1001 AS lit", 18, null, 5L, null, 5L, at, 2],
            [202, at, "app", null, null, "a", "sales", "active", "client backend", null, null, null, "SELECT 2002 AS lit", 18, null, 5L, null, 5L, at, 2]);
        var off = PgProbeWith() with { ComputeQueryId = "off" };
        var (r, _, _) = await PgTickAsync(rows, new TickRequest(true, false, false, false, ActivityMonitor.MaxSessions), off);
        Assert.All(r.Sessions!.Value!, x => Assert.Null(x.QueryHash));
        Assert.Equal(["SELECT 1001 AS lit", "SELECT 2002 AS lit"], r.Sessions.Value!.Select(x => r.SessionTexts![x.TextId!]));
        Assert.NotEqual(TextStore.IdOf("SELECT 1001 AS lit"), TextStore.IdOf("SELECT 2002 AS lit"));
        Assert.Contains("compute_query_id is off", PostgresActivity.Capabilities(off).QueryHashReason);
        Assert.Null(PostgresActivity.Capabilities(PgProbeWith() with { ComputeQueryId = "on" }).QueryHashReason);
        Assert.Contains("PostgreSQL 14", PostgresActivity.Capabilities(PgProbeWith() with { HasQueryId = false }).QueryHashReason);
    }

    // ------------------------------------------------------------------ identity read refused (§6 rule 7)

    [Fact]
    public async Task An_identity_read_the_server_refuses_is_reported_as_unavailable_and_a_connection_failure_is_not()
    {
        var ms = new SqlServerProvider().Activity!;
        var pg = new PostgresProvider().Activity!;
        // No own row — SQL Server query-tab connections report a denied read (229) as a message, i.e. no rows.
        await using (var empty = new FakeDbConnection((_, _) => Task.FromResult(new FakeResult([FakeResult.Table([("id", typeof(short)), ("t", typeof(DateTime))])])), 1))
        {
            await empty.OpenAsync();
            Assert.Contains("sys.dm_exec_sessions", (await Assert.ThrowsAsync<IdentityUnavailableException>(() => ms.IdentityAsync(empty, CancellationToken.None))).Message);
            Assert.Contains("pg_stat_activity", (await Assert.ThrowsAsync<IdentityUnavailableException>(() => pg.IdentityAsync(empty, CancellationToken.None))).Message);
        }
        // PostgreSQL: the view revoked, or a compatible server without the view, column or function.
        foreach (var state in new[] { "42501", "42P01", "42703", "42883" })
        {
            await using var c = new FakeDbConnection((_, _) => throw new Npgsql.PostgresException("refused", "ERROR", "ERROR", state), 1);
            await c.OpenAsync();
            Assert.Contains(state, (await Assert.ThrowsAsync<IdentityUnavailableException>(() => pg.IdentityAsync(c, CancellationToken.None))).Message);
        }
        // A terminated backend (killed during its registration window, K9) stays a connection failure.
        await using var killed = new FakeDbConnection((_, _) => throw new Npgsql.PostgresException("terminating connection", "FATAL", "FATAL", "57P01"), 1);
        await killed.OpenAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => pg.IdentityAsync(killed, CancellationToken.None));
    }

    // ------------------------------------------------------------------ request detail (B5, fake connection)

    const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    static string ShowPlan(string op) =>
        $"""<ShowPlanXML xmlns="{ShowPlanNs}"><BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT b FROM u"><QueryPlan><RelOp NodeId="0" PhysicalOp="{op}" LogicalOp="{op}" EstimateRows="1" EstimatedTotalSubtreeCost="1"/></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";

    static readonly (string, Type)[] DetailColumns =
    [
        ("text", typeof(string)), ("encrypted", typeof(int)), ("start", typeof(int)), ("end", typeof(int)), ("plan_handle", typeof(string)),
        ("open_tx", typeof(int)), ("iso", typeof(short)), ("tx_ms", typeof(long)), ("truncated", typeof(int)),
    ];

    /// <summary>A running request: the second statement of its batch (byte offset 34 = character 17), plan handle as varchar.</summary>
    static DataTable DetailRow(string? planHandle = "0x06000500AABBCCDD") =>
        FakeResult.Table(DetailColumns, ["SELECT a FROM t; SELECT b FROM u", 0, 34, -1, planHandle, 1, (short)2, 1_500L, 0]);

    static async Task<(RequestDetail Detail, List<string> Sql, FakeDbCommand? Read)> DetailAsync(DataTable row, object? live, object? cached,
        SqlServerActivity.Probe? probe = null)
    {
        var sql = new List<string>();
        FakeDbCommand? read = null;
        await using var conn = new FakeDbConnection((cmd, _) =>
        {
            sql.Add(cmd.CommandText);
            if (cmd.CommandText.Contains("dm_exec_query_statistics_xml", StringComparison.Ordinal)) return Task.FromResult(FakeResult.Value(live));
            if (cmd.CommandText.Contains("dm_exec_query_plan", StringComparison.Ordinal)) return Task.FromResult(FakeResult.Value(cached));
            read = cmd;
            return Task.FromResult(new FakeResult([row]));
        }, 1);
        await conn.OpenAsync();
        var p = probe ?? new SqlServerActivity.Probe(3, 16, "16.0", true, true, true, true, true, true, true, "master");
        var session = new SqlServerActivitySession(p, SqlServerActivity.Capabilities(p), K(1), new SqlServerPlans(), new SqlServerDataEdit());
        var key = new SessionKey { Id = 57, Instance = "46000:1200" };
        return (await session.RequestDetailAsync(conn, key, CancellationToken.None), sql, read);
    }

    [Fact]
    public async Task B5_request_detail_prefers_the_live_plan_then_the_cached_plan_then_gives_the_reason()
    {
        // Live (lightweight profiling) first: the cached plan is not read when the live one is there.
        var (live, sql, read) = await DetailAsync(DetailRow(), ShowPlan("Hash Match"), ShowPlan("Table Scan"));
        Assert.Equal("live", live.PlanSource);
        Assert.Equal("Hash Match", live.Plan!.Plan.Statements[0].Root!.PhysicalOp);
        Assert.Null(live.PlanReason);
        Assert.Equal(2, sql.Count);
        // The text, the running statement inside it, and the transaction come with the plan.
        Assert.Equal("SELECT a FROM t; SELECT b FROM u", live.Text);
        Assert.Equal((17, 32), (live.StatementStart, live.StatementEnd));
        Assert.Equal((1, 1_500L, "read committed"), (live.OpenTx, live.TxElapsedMs, live.IsolationLevel));
        // Identity: the session id and its login time are typed parameters of the same statement (a reused spid misses).
        Assert.Contains("s.session_id = @id AND s.login_time = @start", read!.CommandText);
        Assert.Equal(57, read.Param("id"));
        // datetime, never datetime2: a converted value compares exactly and misses (B0 row 1).
        Assert.Equal(SqlDbType.DateTime, ((Microsoft.Data.SqlClient.SqlParameter)read.Params.Single(p => p.ParameterName == "@start")).SqlDbType);

        // No live plan for this request → the cached plan.
        var (cached, sql2, _) = await DetailAsync(DetailRow(), null, ShowPlan("Table Scan"));
        Assert.Equal("cached", cached.PlanSource);
        Assert.Equal("Table Scan", cached.Plan!.Plan.Statements[0].Root!.PhysicalOp);
        Assert.Equal(3, sql2.Count);

        // Neither → the reason, no plan.
        var (none, _, _) = await DetailAsync(DetailRow(), null, null);
        Assert.Null(none.Plan);
        Assert.Null(none.PlanSource);
        Assert.Contains("no plan for this request", none.PlanReason);

        // Lightweight profiling off: the live DMV is not asked, the cached plan is shown.
        var noProfiling = new SqlServerActivity.Probe(3, 16, "16.0", true, true, true, true, true, true, false, "master");
        var (off, sql3, _) = await DetailAsync(DetailRow(), ShowPlan("Hash Match"), ShowPlan("Table Scan"), noProfiling);
        Assert.Equal("cached", off.PlanSource);
        Assert.DoesNotContain(sql3, s => s.Contains("dm_exec_query_statistics_xml", StringComparison.Ordinal));

        // A plan that does not parse → the reason, never a half-read plan.
        var (bad, _, _) = await DetailAsync(DetailRow(), null, "<ShowPlanXML");
        Assert.Null(bad.Plan);
        Assert.Null(bad.PlanSource);
        Assert.Contains("could not be read", bad.PlanReason);
    }

    [Fact]
    public async Task B5_request_detail_says_why_there_is_no_plan_for_an_idle_session_an_ended_one_or_without_server_state()
    {
        // Idle (no request, so no plan handle): no plan query at all.
        var (idle, sql, _) = await DetailAsync(DetailRow(planHandle: null), ShowPlan("Hash Match"), ShowPlan("Table Scan"));
        Assert.Single(sql);
        Assert.Equal("The session is not running a request.", idle.PlanReason);
        Assert.Equal("SELECT a FROM t; SELECT b FROM u", idle.Text);

        // Ended, or the spid now belongs to another login (the identity predicate matched no row): "session ended".
        var (ended, sql2, _) = await DetailAsync(FakeResult.Table(DetailColumns), ShowPlan("Hash Match"), ShowPlan("Table Scan"));
        Assert.Single(sql2);
        Assert.Equal("The session ended.", ended.TextNote);
        Assert.Equal("The session ended.", ended.PlanReason);
        Assert.Null(ended.Text);

        // Without VIEW SERVER STATE nothing is read: the reason names the permission.
        var limited = new SqlServerActivity.Probe(3, 16, "16.0", false, false, true, true, true, true, true, "master");
        var (denied, sql3, _) = await DetailAsync(DetailRow(), ShowPlan("Hash Match"), ShowPlan("Table Scan"), limited);
        Assert.Empty(sql3);
        Assert.Contains("VIEW SERVER STATE", denied.PlanReason);
        Assert.Null(denied.Plan);
    }

    [Fact]
    public async Task D9_postgresql_tick_sampled_waits_issue_the_sessions_query_and_no_profile_query_of_their_own()
    {
        // Without pg_wait_sampling the waits come from the tick's own sessions rows: a waits-only tick still reads
        // pg_stat_activity (the Sessions pane may be hidden) and never queries a wait profile.
        var probe = PgProbeWith(); // no pg_wait_sampling schema: tick-sampled
        Assert.Equal("tick-sampled", PostgresActivity.WaitSource(probe));
        var at = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);
        var rows = PgSessions(
            [201, at, "app", null, null, "a", "sales", "active", "client backend", "Lock", "relation", new[] { 202 }, "LOCK t", 6, null, 5L, 5L, 5L, at, 1]);
        var (r, sql, _) = await PgTickAsync(rows, new TickRequest(false, true, false, false, ActivityMonitor.MaxSessions), probe);
        Assert.Contains("FROM pg_stat_activity a WHERE", sql);
        Assert.DoesNotContain("pg_wait_sampling", sql);
        Assert.Equal(201, Assert.Single(r.Sessions!.Value!).Key.Id);
    }
}

/// <summary>A provider error a fake can throw (SqlException has no public constructor).</summary>
public sealed class FakeDbException(string message) : DbException(message);

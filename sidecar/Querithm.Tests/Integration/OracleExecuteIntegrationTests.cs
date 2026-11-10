using System.Diagnostics;
using Apache.Arrow;
using Apache.Arrow.Types;
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
/// D4 S04 against the Oracle Free fixture (QUERITHM_IT=1, plain TCP profile): every Oracle type through the Arrow
/// encoder, typed or as exact text; LOBs streamed and cancelled; DBMS_OUTPUT on, off and bounded; compile errors from
/// ALL_ERRORS at their line; binds by name; implicit result sets.
/// </summary>
public sealed class OracleExecuteIntegrationTests
{
    static string AppPassword => FixtureSecrets.Password("oracle");

    sealed class Host : IAsyncDisposable
    {
        public CapturingNotifier Notifier { get; } = new();
        public CapturingFrames Frames { get; } = new();
        public Services Services { get; }
        public RpcServer Rpc { get; }
        public string Dir { get; } = Directory.CreateTempSubdirectory("qora-ex-").FullName;

        public Host()
        {
            Services = Services.Create(LocalStore.InMemory(), Frames, Notifier, NullLoggerFactory.Instance,
                new ProviderRegistry(EngineSetup.Providers(Dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle")), Dir);
            Rpc = new RpcServer(Services, () => { });
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            try { Directory.Delete(Dir, true); } catch { /* best effort */ }
        }
    }

    static Profile Tcp() => new()
    {
        Id = "", Name = "ora-ex", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password,
        Username = OracleIntegrationTests.AppUser, DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
    };

    static async Task<(Host H, string Sid)> OpenAsync()
    {
        var h = new Host();
        var p = await h.Rpc.ProfileSave(Tcp());
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = AppPassword });
        return (h, s.SessionId);
    }

    sealed record Ran(QueryDoneEvent Done, List<(ushort Result, RecordBatch Batch)> Batches, string Id, List<ServerMessage> Messages, List<QueryResultSetEvent> Sets);

    static async Task<Ran> Run(Host h, string sid, string sql, ExecuteOptions? options = null, int timeoutMs = 120_000)
    {
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql, Options = options });
        Assert.Null(r.Blocked);
        var done = await h.Notifier.WaitDone(r.ExecutionId!, timeoutMs);
        var batches = h.Frames.For(r.ExecutionId!).Where(f => f.Kind == 0x02).Select(f => (f.ResultIndex, TestHost.ReadBatch(f.Payload))).ToList();
        var messages = h.Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == r.ExecutionId).ToList();
        var sets = h.Notifier.Of<QueryResultSetEvent>().Where(m => m.ExecutionId == r.ExecutionId).ToList();
        return new Ran(done, batches, r.ExecutionId!, messages, sets);
    }

    static async Task ExecAsync(string sql)
    {
        await using var c = new OracleConnection(OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser));
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }

    static Task DropAsync(string name, string kind = "TABLE") => ExecAsync(
        $"BEGIN EXECUTE IMMEDIATE 'DROP {kind} {name}{(kind == "TABLE" ? " PURGE" : "")}'; EXCEPTION WHEN OTHERS THEN IF SQLCODE NOT IN (-942, -4043, -4080) THEN RAISE; END IF; END;");

    static int Col(RecordBatch b, string name) => b.Schema.GetFieldIndex(name);
    static string? Text(RecordBatch b, string name, int row = 0) => ((StringArray)b.Column(Col(b, name))).GetString(row);

    // ---- types ----

    [IntegrationFact]
    public async Task Oracle_types_arrive_typed_or_as_exact_text()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, """
            SELECT CAST(42 AS NUMBER(10)) n10, CAST(1.25 AS NUMBER(12,3)) n12_3, CAST(123456.5 AS NUMBER(5,-2)) nneg, CAST(0.00123 AS NUMBER(3,5)) nfrac,
                   CAST(7 AS INTEGER) i, CAST(1/3 AS NUMBER) n, CAST(0.1 AS FLOAT) f,
                   BINARY_FLOAT_NAN bf, -BINARY_DOUBLE_INFINITY bd,
                   TO_DATE('2024-02-29 13:14:15', 'YYYY-MM-DD HH24:MI:SS') d,
                   TO_DATE('-4000-03-04 05:06:07', 'SYYYY-MM-DD HH24:MI:SS') bc,
                   CAST(TO_TIMESTAMP('2024-01-02 03:04:05.123456', 'YYYY-MM-DD HH24:MI:SS.FF6') AS TIMESTAMP(6)) ts6,
                   TO_TIMESTAMP('2024-01-02 03:04:05.123456789', 'YYYY-MM-DD HH24:MI:SS.FF9') ts9,
                   TO_TIMESTAMP_TZ('2024-07-01 02:30:00.5 America/New_York', 'YYYY-MM-DD HH24:MI:SS.FF TZR') tzr,
                   TO_TIMESTAMP_TZ('2024-07-01 02:30:00 +05:30', 'YYYY-MM-DD HH24:MI:SS TZH:TZM') tzo,
                   CAST(TIMESTAMP '2024-01-02 03:04:05' AS TIMESTAMP(3) WITH LOCAL TIME ZONE) ltz,
                   INTERVAL '-3-7' YEAR TO MONTH iym, INTERVAL '1 02:03:04.123456789' DAY(2) TO SECOND(9) ids,
                   HEXTORAW('ABCD') r, TO_CLOB('clob text') cl, TO_NCLOB(N'nclob ü') ncl, TO_BLOB(HEXTORAW('0102')) bl,
                   ROWIDTOCHAR(ROWID) rid, CAST('a' AS CHAR(3)) c3, N'nv' nv, CAST('' AS VARCHAR2(5)) empty,
                   JSON('{"a":1}') j, XMLTYPE('<a>1</a>') x, TRUE b,
                   TO_VECTOR('[1.5,2,3]', 3, FLOAT64) v,
                   SDO_GEOMETRY(2001, NULL, SDO_POINT_TYPE(1, 2, NULL), NULL, NULL) g,
                   SYS.ODCIVARCHAR2LIST('a', 'b') coll
            FROM dual
            """);
        Assert.Equal(ExecState.Done, run.Done.State);
        var b = run.Batches.Single().Batch;
        var cols = run.Sets.Single().Schema.ToDictionary(c => c.Name);

        Assert.Equal("decimal128(10,0)", cols["N10"].ArrowType);
        Assert.Equal("NUMBER(10)", cols["N10"].TypeName);
        Assert.Equal(42m, ((Decimal128Array)b.Column(Col(b, "N10"))).GetValue(0));
        Assert.Equal("decimal128(12,3)", cols["N12_3"].ArrowType); // ODP.NET's Double, never used
        Assert.Equal(1.25m, ((Decimal128Array)b.Column(Col(b, "N12_3"))).GetValue(0));
        Assert.Equal("decimal128(7,0)", cols["NNEG"].ArrowType);
        Assert.Equal(123500m, ((Decimal128Array)b.Column(Col(b, "NNEG"))).GetValue(0));
        Assert.Equal("decimal128(5,5)", cols["NFRAC"].ArrowType);
        Assert.Equal(0.00123m, ((Decimal128Array)b.Column(Col(b, "NFRAC"))).GetValue(0));
        Assert.Equal("decimal128(38,0)", cols["I"].ArrowType);
        // Unconstrained NUMBER and FLOAT: exact text, marked numeric.
        Assert.Equal("utf8", cols["N"].ArrowType);
        Assert.Equal("0." + new string('3', 40), Text(b, "N"));
        Assert.Equal("1", b.Schema.GetFieldByName("N").Metadata["q:numeric"]);
        Assert.Equal("0.1", Text(b, "F"));

        Assert.True(float.IsNaN(((FloatArray)b.Column(Col(b, "BF"))).GetValue(0)!.Value));
        Assert.Equal(double.NegativeInfinity, ((DoubleArray)b.Column(Col(b, "BD"))).GetValue(0));

        // DATE keeps its time; a BC date (outside DateTime) still arrives, in ISO numbering (Oracle -4000 = astronomical -3999).
        Assert.Equal("timestamp[us]", cols["D"].ArrowType);
        Assert.Equal("DATE", cols["D"].TypeName);
        var d = (TimestampArray)b.Column(Col(b, "D"));
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 13, 14, 15, TimeSpan.Zero), d.GetTimestamp(0));
        var bc = (TimestampArray)b.Column(Col(b, "BC"));
        Assert.Equal(OracleTypes.Micros(-4000, 3, 4, 5, 6, 7, 0), bc.GetValue(0));
        var ts6 = (TimestampArray)b.Column(Col(b, "TS6"));
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560), ts6.GetTimestamp(0));
        Assert.Equal("TIMESTAMP(9)", cols["TS9"].TypeName);
        Assert.Equal("2024-01-02 03:04:05.123456789", Text(b, "TS9"));
        Assert.Equal("2024-07-01 02:30:00.500000000 America/New_York", Text(b, "TZR"));
        Assert.Equal("2024-07-01 02:30:00.000000000 +05:30", Text(b, "TZO")); // TO_TIMESTAMP_TZ is TIMESTAMP(9) WITH TIME ZONE
        Assert.Equal("TIMESTAMP(3) WITH LOCAL TIME ZONE", cols["LTZ"].TypeName);
        Assert.Equal("timestamp[us]", cols["LTZ"].ArrowType);

        Assert.Equal("-03-07", Text(b, "IYM"));
        Assert.Equal("+01 02:03:04.123456789", Text(b, "IDS"));

        Assert.Equal(new byte[] { 0xAB, 0xCD }, ((BinaryArray)b.Column(Col(b, "R"))).GetBytes(0).ToArray());
        Assert.Equal("clob text", Text(b, "CL"));
        Assert.Equal("nclob ü", Text(b, "NCL"));
        Assert.Equal(new byte[] { 1, 2 }, ((BinaryArray)b.Column(Col(b, "BL"))).GetBytes(0).ToArray());
        Assert.Equal(18, Text(b, "RID")!.Length);
        Assert.Equal("a  ", Text(b, "C3"));
        Assert.Equal("nv", Text(b, "NV"));
        Assert.True(b.Column(Col(b, "EMPTY")).IsNull(0)); // '' is NULL in Oracle: no special casing
        Assert.Equal("""{"a":1}""", Text(b, "J"));
        Assert.Equal("<a>1</a>", Text(b, "X"));
        Assert.True(((BooleanArray)b.Column(Col(b, "B"))).GetValue(0));
        Assert.Equal("[1.5,2,3]", Text(b, "V"));
        Assert.Equal("MDSYS.SDO_GEOMETRY", cols["G"].TypeName);
        Assert.Contains("value not shown", Text(b, "G"));
        Assert.Contains("SDO_UTIL.TO_WKTGEOMETRY", Text(b, "G"));
        Assert.Contains("value not shown", Text(b, "COLL"));
    }

    [IntegrationFact]
    public async Task Number_38_max_and_a_40_digit_unconstrained_number_are_exact()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, """
            SELECT CAST(99999999999999999999999999999999999999 AS NUMBER(38)) maxp, CAST(-99999999999999999999999999999999999999 AS NUMBER(38)) minp,
                   CAST(-12345678901234567890.123456789012345678 AS NUMBER(38,18)) frac,
                   CAST(1234567890123456789012345678901234567890 AS NUMBER) forty, 1e125 big, 1e-130 tiny
            FROM dual
            """);
        Assert.Equal(ExecState.Done, run.Done.State);
        var b = run.Batches.Single().Batch;
        Assert.Equal("decimal128(38,0)", run.Sets.Single().Schema.Single(c => c.Name == "MAXP").ArrowType);
        var maxp = (Decimal128Array)b.Column(Col(b, "MAXP"));
        Assert.Equal("99999999999999999999999999999999999999", maxp.GetString(0));
        Assert.Equal("-99999999999999999999999999999999999999", ((Decimal128Array)b.Column(Col(b, "MINP"))).GetString(0));
        Assert.Equal("-12345678901234567890.123456789012345678", ((Decimal128Array)b.Column(Col(b, "FRAC"))).GetString(0));
        Assert.Equal("1234567890123456789012345678901234567890", Text(b, "FORTY"));
        Assert.Equal("1" + new string('0', 125), Text(b, "BIG"));
        Assert.Equal("0." + new string('0', 129) + "1", Text(b, "TINY"));
    }

    [IntegrationFact]
    public async Task Long_and_long_raw_columns_arrive_inline()
    {
        await DropAsync("q_s04_long");
        await ExecAsync("CREATE TABLE q_s04_long (id NUMBER(5), l LONG)");
        await ExecAsync("DECLARE v VARCHAR2(32767) := RPAD('x', 5000, 'y'); BEGIN INSERT INTO q_s04_long VALUES (1, v); COMMIT; END;");
        await DropAsync("q_s04_lraw");
        await ExecAsync("CREATE TABLE q_s04_lraw (id NUMBER(5), r LONG RAW)");
        await ExecAsync("INSERT INTO q_s04_lraw VALUES (1, HEXTORAW('DEADBEEF'))");
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, "SELECT l FROM q_s04_long;\nSELECT r FROM q_s04_lraw");
        Assert.Equal(ExecState.Done, run.Done.State);
        Assert.Equal(5000, Text(run.Batches[0].Batch, "L")!.Length);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, ((BinaryArray)run.Batches[1].Batch.Column(0)).GetBytes(0).ToArray());
    }

    // ---- LOBs ----

    /// <summary>A 50 MB (52,427,200 characters) CLOB, built once on the server.</summary>
    static async Task BigClobAsync()
    {
        await using var c = new OracleConnection(OracleIntegrationTests.ConnectionString(OracleIntegrationTests.AppUser));
        await c.OpenAsync();
        await using var check = c.CreateCommand();
        check.CommandText = "SELECT DBMS_LOB.GETLENGTH(c) FROM q_s04_big";
        try
        {
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) == 52_427_200) return;
        }
        catch (OracleException) { /* not there yet */ }
        await DropAsync("q_s04_big");
        await ExecAsync("CREATE TABLE q_s04_big (id NUMBER(5), c CLOB)");
        await ExecAsync("""
            DECLARE c CLOB; chunk VARCHAR2(32767) := RPAD('x', 32766, 'y') || 'z';
            BEGIN
              DBMS_LOB.CREATETEMPORARY(c, TRUE);
              FOR i IN 1..1600 LOOP DBMS_LOB.WRITEAPPEND(c, 32767, chunk); END LOOP;
              DBMS_LOB.WRITEAPPEND(c, 1600, RPAD('t', 1600, 't'));
              INSERT INTO q_s04_big VALUES (1, c);
              COMMIT;
            END;
            """);
    }

    [IntegrationFact]
    public async Task A_50_mb_clob_is_streamed_truncated_and_cancelled_mid_stream_then_the_connection_is_reused()
    {
        await BigClobAsync();
        var (h, sid) = await OpenAsync();
        await using var _ = h;

        // Whole: the grid gets the first 1 MB and the flag; the run completes.
        var whole = await Run(h, sid, "SELECT id, c FROM q_s04_big");
        Assert.Equal(ExecState.Done, whole.Done.State);
        var b = whole.Batches.Single().Batch;
        Assert.Equal(Core.Execution.ResultSetEncoder.TextLimitChars, Text(b, "C")!.Length);
        Assert.True(((BooleanArray)b.Column(Col(b, "C__truncated"))).GetValue(0));

        // Cancelled while the value is read (the schema frame is out, the batch is not): it stops, and the session's next run works.
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "SELECT id, c FROM q_s04_big" });
        var sw = Stopwatch.StartNew();
        while (!h.Notifier.Of<QueryResultSetEvent>().Any(e => e.ExecutionId == r.ExecutionId) && sw.ElapsedMilliseconds < 30_000) await Task.Delay(5);
        await h.Rpc.QueryCancel(new ExecutionRef { ExecutionId = r.ExecutionId! });
        var done = await h.Notifier.WaitDone(r.ExecutionId!, 60_000);
        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.DoesNotContain(h.Frames.For(r.ExecutionId!), f => f.Kind == 0x02);

        var after = await Run(h, sid, "SELECT 1 one FROM dual");
        Assert.Equal(ExecState.Done, after.Done.State);
    }

    // ---- DBMS_OUTPUT ----

    static List<string> Lines(Ran run) => run.Messages.Where(m => m.Severity == Severity.Info).Select(m => m.Message).ToList();

    [IntegrationFact]
    public async Task Dbms_output_follows_set_serveroutput_and_the_run_option_and_stays_with_the_session()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        const string Put = "BEGIN DBMS_OUTPUT.PUT_LINE('hello'); DBMS_OUTPUT.PUT_LINE(NULL); DBMS_OUTPUT.PUT('a'); DBMS_OUTPUT.PUT_LINE('b'); END;";

        var off = await Run(h, sid, Put);
        Assert.Equal(ExecState.Done, off.Done.State);
        Assert.Empty(Lines(off));

        var on = await Run(h, sid, "SET SERVEROUTPUT ON\n" + Put + "\n/");
        Assert.Equal(ExecState.Done, on.Done.State);
        Assert.Equal(["hello", "", "ab"], Lines(on));

        var still = await Run(h, sid, Put); // the session keeps it on
        Assert.Equal(["hello", "", "ab"], Lines(still));

        var optionOff = await Run(h, sid, Put, new ExecuteOptions { ServerOutput = false });
        Assert.Empty(Lines(optionOff));

        var optionOn = await Run(h, sid, Put, new ExecuteOptions { ServerOutput = true });
        Assert.Equal(["hello", "", "ab"], Lines(optionOn));

        // Toggled between statements of one script: only the middle block's line is read back.
        var mixed = await Run(h, sid, "SET SERVEROUTPUT OFF\nBEGIN DBMS_OUTPUT.PUT_LINE('one'); END;\n/\nSET SERVEROUTPUT ON\nBEGIN DBMS_OUTPUT.PUT_LINE('two'); END;\n/\nSET SERVEROUTPUT OFF\nBEGIN DBMS_OUTPUT.PUT_LINE('three'); END;\n/");
        Assert.Equal(["two"], Lines(mixed));
        var afterScript = await Run(h, sid, Put); // the trailing OFF stayed
        Assert.Empty(Lines(afterScript));
    }

    [IntegrationFact]
    public async Task Dbms_output_is_bounded_per_statement_and_the_rest_is_discarded()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var flood = await Run(h, sid, "BEGIN FOR i IN 1..25000 LOOP DBMS_OUTPUT.PUT_LINE('line ' || i); END LOOP; END;", new ExecuteOptions { ServerOutput = true });
        Assert.Equal(ExecState.Done, flood.Done.State);
        var lines = Lines(flood);
        Assert.Equal(OracleOutput.MaxLinesPerBatch, lines.Count);
        Assert.Equal("line 1", lines[0]);
        Assert.Equal("line " + OracleOutput.MaxLinesPerBatch, lines[^1]);
        Assert.Contains(flood.Messages, m => m.Severity == Severity.Warning && m.Message.Contains("rest were discarded"));
        // The discarded lines never surface later; output is still on.
        var next = await Run(h, sid, "BEGIN DBMS_OUTPUT.PUT_LINE('next'); END;");
        Assert.Equal(["next"], Lines(next));
    }

    [IntegrationFact]
    public async Task What_a_failing_block_printed_is_shown_with_its_error_not_after_a_later_statement()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var failed = await Run(h, sid, "BEGIN DBMS_OUTPUT.PUT_LINE('before'); RAISE_APPLICATION_ERROR(-20001, 'boom'); END;", new ExecuteOptions { ServerOutput = true });
        Assert.Equal(ExecState.Failed, failed.Done.State);
        Assert.Contains("ORA-20001", failed.Done.Error?.Message);
        Assert.Equal(["before"], Lines(failed));
        var next = await Run(h, sid, "BEGIN DBMS_OUTPUT.PUT_LINE('after'); END;");
        Assert.Equal(["after"], Lines(next));
    }

    // ---- compile errors ----

    [IntegrationFact]
    public async Task A_procedure_created_with_errors_reports_each_at_its_line_and_column()
    {
        await DropAsync("q_s04_bad", "PROCEDURE");
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, "SELECT 1 FROM dual;\n\nCREATE OR REPLACE PROCEDURE q_s04_bad AS\nBEGIN\n  x := 1;\nEND;\n/\nSELECT 2 FROM dual");
        Assert.Equal(ExecState.Failed, run.Done.State);
        var errors = run.Messages.Where(m => m.Severity == Severity.Error).ToList();
        var pls = Assert.Single(errors, m => m.Message.Contains("PLS-00201"));
        Assert.Equal(5, pls.EditorLine); // "  x := 1;" is line 5 of the script
        Assert.Contains("column 3", pls.Message);
        Assert.Equal(3, pls.EditorColumn); // S06: the Messages pane jumps to line 5, column 3
        Assert.Equal(5, run.Done.Error?.EditorLine);
        Assert.Equal(3, run.Done.Error?.EditorColumn);
        Assert.Contains(run.Messages, m => m.Severity == Severity.Warning && m.Message.Contains("PROCEDURE Q_S04_BAD was created with compilation errors"));
        Assert.Equal(2, run.Batches.Count); // the script went on, as SQL*Plus does: the unit exists, invalid

        var good = await Run(h, sid, "CREATE OR REPLACE PROCEDURE q_s04_bad AS\nBEGIN\n  NULL;\nEND;");
        Assert.Equal(ExecState.Done, good.Done.State);
        Assert.DoesNotContain(good.Messages, m => m.Severity is Severity.Error or Severity.Warning);
    }

    [IntegrationFact]
    public async Task A_package_body_with_errors_is_read_from_the_body_not_the_spec()
    {
        await DropAsync("q_s04_pkg", "PACKAGE");
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, "CREATE OR REPLACE PACKAGE q_s04_pkg AS\n  PROCEDURE p;\nEND;\n/\nCREATE OR REPLACE PACKAGE BODY q_s04_pkg AS\n  PROCEDURE p IS\n  BEGIN\n    y := 2;\n  END;\nEND;\n/");
        Assert.Equal(ExecState.Failed, run.Done.State);
        var pls = Assert.Single(run.Messages, m => m.Severity == Severity.Error && m.Message.Contains("PLS-00201"));
        Assert.Equal(8, pls.EditorLine);
        Assert.Contains(run.Messages, m => m.Message.Contains("PACKAGE BODY Q_S04_PKG was created with compilation errors"));
    }

    [IntegrationFact]
    public async Task A_trigger_created_with_errors_reports_them_at_the_line_in_the_script()
    {
        await DropAsync("q_s04_trg_e");
        await ExecAsync("CREATE TABLE q_s04_trg_e (id NUMBER(5))");
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, """
            -- a comment
            CREATE OR REPLACE TRIGGER q_s04_trg_e_bi
              BEFORE INSERT ON q_s04_trg_e FOR EACH ROW
            DECLARE
              n NUMBER;
            BEGIN
              n := zz_nothing;
            END;
            /
            """);
        Assert.Equal(ExecState.Failed, run.Done.State);
        var pls = Assert.Single(run.Messages, m => m.Severity == Severity.Error && m.Message.Contains("PLS-00201"));
        Assert.Equal(7, pls.EditorLine);
    }

    // ---- parameters ----

    [IntegrationFact]
    public async Task Named_and_numbered_binds_are_bound_by_name()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, "SELECT :b || '-' || :a || '-' || :1 AS s, :n + 1 AS n1 FROM dual WHERE ':skip' = ':skip' /* :c */", new ExecuteOptions
        {
            Parameters =
            [
                new QueryParameter { Name = "a", Value = "A" }, new QueryParameter { Name = ":b", Value = "B" }, new QueryParameter { Name = "1", Value = "one" },
                new QueryParameter { Name = "n", Value = "41" },
            ],
        });
        Assert.Equal(ExecState.Done, run.Done.State);
        var b = run.Batches.Single().Batch;
        Assert.Equal("B-A-one", Text(b, "S"));
        Assert.Equal("42", Text(b, "N1"));
    }

    [IntegrationFact]
    public async Task A_trigger_using_new_is_created_with_the_runs_parameters_kept_away()
    {
        await DropAsync("q_s04_trg_t");
        await ExecAsync("CREATE TABLE q_s04_trg_t (id NUMBER(5), note VARCHAR2(20))");
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, "CREATE OR REPLACE TRIGGER q_s04_trg BEFORE INSERT ON q_s04_trg_t FOR EACH ROW\nBEGIN\n  :NEW.note := 'set';\nEND;\n/\nINSERT INTO q_s04_trg_t (id) VALUES (:id);\nSELECT note FROM q_s04_trg_t WHERE id = :id",
            new ExecuteOptions { Parameters = [new QueryParameter { Name = "id", Value = "7" }] });
        Assert.Equal(ExecState.Done, run.Done.State);
        Assert.DoesNotContain(run.Messages, m => m.Severity == Severity.Error);
        Assert.Equal("set", Text(run.Batches.Single().Batch, "NOTE"));
        await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "ROLLBACK" });
    }

    [IntegrationFact]
    public async Task A_bind_without_a_value_fails_with_the_servers_reason()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, "SELECT :missing FROM dual");
        Assert.Equal(ExecState.Failed, run.Done.State);
        Assert.Contains("ORA-01008", run.Done.Error?.Message);
    }

    // ---- the splitter's batches on the server (S02 open item) ----

    [IntegrationFact]
    public async Task The_splitters_batches_execute_unchanged_on_the_server()
    {
        await DropAsync("q_s04_split_t");
        await DropAsync("q_s04_pt", "TYPE");
        await DropAsync("q_s04_sp", "PACKAGE");
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, """
            CREATE TABLE q_s04_split_t (id NUMBER(5), note VARCHAR2(40));
            CREATE OR REPLACE TYPE q_s04_pt AS OBJECT (x NUMBER, y NUMBER);
            /
            CREATE OR REPLACE PACKAGE q_s04_sp AS
              FUNCTION twice(n NUMBER) RETURN NUMBER;
            END q_s04_sp;
            /
            CREATE OR REPLACE PACKAGE BODY q_s04_sp AS
              FUNCTION twice(n NUMBER) RETURN NUMBER IS BEGIN RETURN n * 2; END;
            END q_s04_sp;
            /
            CREATE OR REPLACE TRIGGER q_s04_split_trg FOR INSERT ON q_s04_split_t COMPOUND TRIGGER
              BEFORE EACH ROW IS BEGIN :NEW.note := 'q''[;/]'' kept'; END BEFORE EACH ROW;
            END q_s04_split_trg;
            /
            INSERT INTO q_s04_split_t (id) VALUES (q_s04_sp.twice(21));
            WITH FUNCTION plus1(n NUMBER) RETURN NUMBER IS BEGIN RETURN n + 1; END;
            SELECT plus1(id) AS v, note FROM q_s04_split_t
            /
            SELECT q'[a;b/c]' AS q FROM dual;
            ROLLBACK
            """);
        Assert.True(run.Done.State == ExecState.Done, string.Join(Environment.NewLine, run.Messages.Select(m => m.Message)));
        Assert.DoesNotContain(run.Messages, m => m.Severity is Severity.Error or Severity.Warning);
        var withFunction = run.Batches.Single(x => x.Batch.Schema.GetFieldIndex("V") >= 0).Batch;
        Assert.Equal("43", Text(withFunction, "V")); // a function's NUMBER result: unconstrained, exact text
        Assert.Equal("q'[;/]' kept", Text(withFunction, "NOTE"));
        Assert.Equal("a;b/c", Text(run.Batches.Single(x => x.Batch.Schema.GetFieldIndex("Q") >= 0).Batch, "Q"));
    }

    // ---- implicit results ----

    [IntegrationFact]
    public async Task Implicit_result_sets_arrive_as_result_sets_of_their_own()
    {
        var (h, sid) = await OpenAsync();
        await using var _ = h;
        var run = await Run(h, sid, """
            DECLARE c1 SYS_REFCURSOR; c2 SYS_REFCURSOR;
            BEGIN
              OPEN c1 FOR SELECT CAST(1 AS NUMBER(3)) a FROM dual;
              DBMS_SQL.RETURN_RESULT(c1);
              OPEN c2 FOR SELECT 'x' b, TO_DATE('2024-01-01', 'YYYY-MM-DD') d FROM dual;
              DBMS_SQL.RETURN_RESULT(c2);
            END;
            """);
        Assert.Equal(ExecState.Done, run.Done.State);
        Assert.Equal(2, run.Done.ResultSetCount);
        Assert.Equal("decimal128(3,0)", run.Sets[0].Schema.Single().ArrowType);
        Assert.Equal(["B", "D"], run.Sets[1].Schema.Select(c => c.Name));
        Assert.Equal("timestamp[us]", run.Sets[1].Schema[1].ArrowType);
        Assert.Equal("x", Text(run.Batches.Single(x => x.Result == 1).Batch, "B"));
    }
}

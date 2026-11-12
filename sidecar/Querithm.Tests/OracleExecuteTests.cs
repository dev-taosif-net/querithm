using System.Data;
using System.Data.Common;
using Apache.Arrow;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Intelligence.Oracle;
using Querithm.Providers.Oracle;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// D4 S04 without a server: the Oracle type-mapping table, its exact-text and timestamp readers, bind collection and
/// binding, the compile-error unit a CREATE names, SET SERVEROUTPUT toggles; and the Core hooks they ride on (provider
/// column shapes, prepared commands, server output per session, after-batch errors).
/// </summary>
public class OracleExecuteTests
{
    // ---- type mapping ----

    [Theory]
    [InlineData(10, 0, "decimal", "NUMBER(10)", 10, 0)]
    [InlineData(12, 3, "decimal", "NUMBER(12,3)", 12, 3)] // ODP.NET: Double
    [InlineData(38, 0, "decimal", "NUMBER(38)", 38, 0)] // INTEGER
    [InlineData(38, 38, "decimal", "NUMBER(38,38)", 38, 38)]
    [InlineData(5, -2, "decimal", "NUMBER(5,-2)", 7, 0)] // whole numbers of 7 digits
    [InlineData(3, 5, "decimal", "NUMBER(3,5)", 5, 5)] // 0.00ddd
    [InlineData(38, -84, "text", "NUMBER(38,-84)", null, null)] // beyond Decimal128
    [InlineData(38, 127, "text", "NUMBER", null, null)] // NUMBER without precision
    [InlineData(0, -127, "text", "NUMBER", null, null)] // an expression
    [InlineData(4, 127, "text", "FLOAT", null, null)] // FLOAT(10)
    public void Number_columns_are_decimal128_when_they_fit_and_exact_text_otherwise(int p, int s, string kind, string typeName, int? dp, int? ds)
    {
        var c = OracleTypes.Map(OracleDbType.Decimal, p, s, 22, null);
        Assert.Equal(typeName, c.TypeName);
        Assert.Equal(typeof(decimal), c.Shape!.Clr); // q:numeric on the text form
        if (kind == "decimal")
        {
            Assert.Equal(OracleKind.Decimal, c.Kind);
            Assert.Equal(ColumnHint.Default, c.Shape.Hint);
            Assert.Equal(dp, c.Shape.Precision);
            Assert.Equal(ds, c.Shape.Scale);
        }
        else
        {
            Assert.Equal(OracleKind.NumberText, c.Kind);
            Assert.Equal(ColumnHint.ProviderString, c.Shape.Hint);
        }
    }

    [Fact]
    public void Every_number_flavour_odp_reports_is_read_as_a_number_never_a_double()
    {
        foreach (var t in new[] { OracleDbType.Byte, OracleDbType.Int16, OracleDbType.Int32, OracleDbType.Int64, OracleDbType.Double, OracleDbType.Single })
            Assert.Equal(OracleKind.Decimal, OracleTypes.Map(t, 12, 3, 22, null).Kind);
        Assert.Equal(typeof(float), OracleTypes.Map(OracleDbType.BinaryFloat, null, null, 4, null).Shape!.Clr);
        Assert.Equal(typeof(double), OracleTypes.Map(OracleDbType.BinaryDouble, null, null, 8, null).Shape!.Clr);
    }

    [Theory]
    [InlineData(OracleDbType.Date, null, "Timestamp", "DATE")]
    [InlineData(OracleDbType.TimeStamp, 6, "Timestamp", "TIMESTAMP(6)")]
    [InlineData(OracleDbType.TimeStamp, 0, "Timestamp", "TIMESTAMP(0)")]
    [InlineData(OracleDbType.TimeStamp, 9, "TimestampText", "TIMESTAMP(9)")]
    [InlineData(OracleDbType.TimeStampLTZ, 3, "Timestamp", "TIMESTAMP(3) WITH LOCAL TIME ZONE")]
    [InlineData(OracleDbType.TimeStampLTZ, 7, "TimestampText", "TIMESTAMP(7) WITH LOCAL TIME ZONE")]
    [InlineData(OracleDbType.TimeStampTZ, 6, "TimestampTzText", "TIMESTAMP(6) WITH TIME ZONE")]
    public void Dates_and_timestamps_are_microsecond_timestamps_or_exact_text(OracleDbType type, int? scale, string kind, string typeName)
    {
        var c = OracleTypes.Map(type, null, scale, 11, null);
        Assert.Equal(kind, c.Kind.ToString());
        Assert.Equal(typeName, c.TypeName);
        if (kind == nameof(OracleKind.Timestamp))
        {
            Assert.Equal(typeof(DateTime), c.Shape!.Clr);
            Assert.NotNull(c.Shape.TimestampMicros); // BC dates: never through DateTime
        }
        else Assert.Equal(ColumnHint.ProviderString, c.Shape!.Hint);
    }

    [Fact]
    public void Lobs_stream_and_the_rest_map_to_text_binary_or_a_reason()
    {
        var clob = OracleTypes.Map(OracleDbType.Clob, null, null, int.MaxValue, null);
        Assert.Equal(ColumnHint.LargeText, clob.Shape!.Hint);
        Assert.NotNull(clob.Shape.OpenText);
        Assert.NotNull(OracleTypes.Map(OracleDbType.NClob, null, null, int.MaxValue, null).Shape!.OpenText);
        var blob = OracleTypes.Map(OracleDbType.Blob, null, null, int.MaxValue, null);
        Assert.Equal(typeof(byte[]), blob.Shape!.Clr);
        Assert.NotNull(blob.Shape.OpenBinary);
        Assert.Equal(typeof(byte[]), OracleTypes.Map(OracleDbType.Raw, null, null, 16, null).Shape!.Clr);
        Assert.Equal(typeof(byte[]), OracleTypes.Map(OracleDbType.LongRaw, null, null, 0, null).Shape!.Clr);
        foreach (var t in new[] { OracleDbType.Long, OracleDbType.XmlType, OracleDbType.Json })
            Assert.Equal(ColumnHint.LargeText, OracleTypes.Map(t, null, null, 0, null).Shape!.Hint);
        Assert.Equal("VARCHAR2(30)", OracleTypes.Map(OracleDbType.Varchar2, null, null, 30, null).TypeName);
        Assert.Equal(OracleKind.IntervalYm, OracleTypes.Map(OracleDbType.IntervalYM, 2, null, 5, null).Kind);
        Assert.Equal("INTERVAL DAY(2) TO SECOND(9)", OracleTypes.Map(OracleDbType.IntervalDS, 2, 9, 11, null).TypeName);
        foreach (var t in new[] { OracleDbType.Vector, OracleDbType.Vector_Float64, OracleDbType.Vector_Int8, OracleDbType.Vector_Float32, OracleDbType.Vector_Binary })
            Assert.Equal(OracleKind.Vector, OracleTypes.Map(t, null, null, 0, null).Kind);
        var geo = OracleTypes.Map(OracleDbType.Object, null, null, 0, "MDSYS.SDO_GEOMETRY");
        Assert.Equal(OracleKind.Unreadable, geo.Kind);
        Assert.Equal("MDSYS.SDO_GEOMETRY", geo.TypeName);
        Assert.Equal(typeof(bool), OracleTypes.Map(OracleDbType.Boolean, null, null, 1, null).Shape!.Clr);
    }

    [Fact]
    public void Unreadable_values_say_what_they_are_and_how_to_see_them()
    {
        var text = OracleTypes.Unreadable("MDSYS.SDO_GEOMETRY");
        Assert.Contains("MDSYS.SDO_GEOMETRY value not shown", text);
        Assert.Contains("SDO_UTIL.TO_WKTGEOMETRY", text);
        Assert.Contains("DBMS_SQL.RETURN_RESULT", OracleTypes.Unreadable("CURSOR"));
    }

    [Fact]
    public void Micros_follow_the_proleptic_calendar_including_bc_dates()
    {
        Assert.Equal(0, OracleTypes.Micros(1970, 1, 1, 0, 0, 0, 0));
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 13, 14, 15, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000 + 123,
            OracleTypes.Micros(2024, 2, 29, 13, 14, 15, 123_456));
        Assert.Equal(new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000, OracleTypes.Micros(1, 1, 1, 0, 0, 0, 0));
        // Oracle has no year 0: -1 (1 BC) is the year before 1, 366 days (a leap year in the proleptic calendar).
        Assert.Equal(OracleTypes.Micros(1, 1, 1, 0, 0, 0, 0) - 366L * 86_400_000_000, OracleTypes.Micros(-1, 1, 1, 0, 0, 0, 0));
        Assert.True(OracleTypes.Micros(-4712, 1, 1, 0, 0, 0, 0) < OracleTypes.Micros(-4000, 3, 4, 5, 6, 7, 0));
    }

    [Fact]
    public void Exact_timestamp_text_keeps_every_digit_the_scale_has()
    {
        Assert.Equal("2024-01-02 03:04:05.123456789", OracleTypes.Stamp(2024, 1, 2, 3, 4, 5, 123_456_789, 9));
        Assert.Equal("2024-01-02 03:04:05.1234567", OracleTypes.Stamp(2024, 1, 2, 3, 4, 5, 123_456_789, 7));
        Assert.Equal("2024-01-02 03:04:05", OracleTypes.Stamp(2024, 1, 2, 3, 4, 5, 0, 0));
        Assert.Equal("-4000-03-04 05:06:07", OracleTypes.Stamp(-4000, 3, 4, 5, 6, 7, 0, 0));
        Assert.Equal("0099-12-31 00:00:00.000", OracleTypes.Stamp(99, 12, 31, 0, 0, 0, 0, 3));
    }

    [Fact]
    public void Vectors_print_as_oracle_does()
    {
        Assert.Equal("[1.5,2,3]", OracleTypes.Vector(new[] { 1.5, 2.0, 3.0 }));
        Assert.Equal("[0.1,-2]", OracleTypes.Vector(new[] { 0.1f, -2f }));
        Assert.Equal("[1,-2]", OracleTypes.Vector(new short[] { 1, -2 }));
        Assert.Equal("[]", OracleTypes.Vector(System.Array.Empty<double>()));
    }

    // ---- binds ----

    [Fact]
    public void Binds_skip_strings_comments_q_literals_and_trigger_row_records()
    {
        Assert.Equal(["A", "1"], OracleLanguageService.Binds("SELECT :a, ':b', q'[:c]', /* :d */ :1 -- :e\nFROM dual"));
        Assert.Equal(["X"], OracleLanguageService.Binds("SELECT nq'{:y}' FROM dual WHERE :x = 1"));
        Assert.Empty(OracleLanguageService.Binds("CREATE OR REPLACE TRIGGER t BEFORE INSERT ON x FOR EACH ROW BEGIN :NEW.a := :OLD.a; END;"));
        Assert.Empty(OracleLanguageService.Binds("BEGIN v := 1; END;")); // := is assignment
    }

    [Fact]
    public void Parameters_bind_by_name_and_only_those_the_statement_names_are_sent()
    {
        IDbProvider p = TestOracle.Supported();
        using var cmd = new OracleCommand("SELECT :a, :\"Odd\", :1 FROM dual");
        p.AddParameter(cmd, new QueryParameter { Name = "a", Value = "x" });
        p.AddParameter(cmd, new QueryParameter { Name = ":\"Odd\"", Value = "y" });
        p.AddParameter(cmd, new QueryParameter { Name = "1", Value = new string('z', 40_000) });
        p.AddParameter(cmd, new QueryParameter { Name = "unused", IsNull = true });
        p.PrepareCommand(cmd);
        Assert.True(cmd.BindByName);
        Assert.Equal(OracleProvider.FetchSizeBytes, cmd.FetchSize);
        Assert.Equal(OracleProvider.InitialLobFetchSize, cmd.InitialLOBFetchSize);
        Assert.Equal(-1, cmd.InitialLONGFetchSize);
        Assert.Equal(["a", "\"Odd\"", "1"], cmd.Parameters.Cast<OracleParameter>().Select(x => x.ParameterName));
        Assert.Equal(OracleDbType.Clob, cmd.Parameters[2].OracleDbType); // past VARCHAR2's 32,767
        Assert.Equal(OracleDbType.Varchar2, cmd.Parameters[0].OracleDbType);

        using var trigger = new OracleCommand("CREATE OR REPLACE TRIGGER t BEFORE INSERT ON x FOR EACH ROW BEGIN :NEW.a := 1; END;");
        p.AddParameter(trigger, new QueryParameter { Name = "NEW", Value = "x" });
        p.PrepareCommand(trigger);
        Assert.Empty(trigger.Parameters);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":")]
    public void A_parameter_without_a_name_is_refused(string name) =>
        Assert.Throws<ArgumentException>(() => ((IDbProvider)TestOracle.Supported()).AddParameter(new OracleCommand("SELECT 1 FROM dual"), new QueryParameter { Name = name, Value = "x" }));

    // ---- compile errors ----

    [Theory]
    [InlineData("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;", null, "P", "PROCEDURE", 1)]
    [InlineData("CREATE FUNCTION app.\"f\" RETURN NUMBER AS BEGIN RETURN 1; END;", "APP", "f", "FUNCTION", 1)]
    [InlineData("CREATE OR REPLACE EDITIONABLE PACKAGE BODY pk AS END;", null, "PK", "PACKAGE BODY", 1)]
    [InlineData("CREATE OR REPLACE PACKAGE pk AS END;", null, "PK", "PACKAGE", 1)]
    [InlineData("CREATE OR REPLACE TYPE BODY ty AS END;", null, "TY", "TYPE BODY", 1)]
    [InlineData("-- c\nCREATE OR REPLACE\nTRIGGER tr BEFORE INSERT ON t\nFOR EACH ROW\nBEGIN NULL; END;", null, "TR", "TRIGGER", 5)]
    [InlineData("CREATE OR REPLACE FORCE VIEW v AS SELECT x FROM nowhere", null, "V", "VIEW", 1)]
    [InlineData("CREATE PROCEDURE IF NOT EXISTS p2 AS BEGIN NULL; END;", null, "P2", "PROCEDURE", 1)]
    public void The_unit_a_create_makes_is_read_from_its_tokens(string sql, string? owner, string name, string type, int sourceLine)
    {
        var u = OracleOutput.CreatedUnit(sql);
        Assert.NotNull(u);
        Assert.Equal(owner, u.Owner);
        Assert.Equal(name, u.Name);
        Assert.Equal(type, u.Type);
        Assert.Equal(sourceLine, u.SourceLine);
    }

    [Theory]
    [InlineData("CREATE TABLE t (id NUMBER)")]
    [InlineData("CREATE VIEW v AS SELECT 1 x FROM dual")] // no FORCE: an invalid view is not created
    [InlineData("SELECT 'CREATE PROCEDURE p' FROM dual")]
    [InlineData("BEGIN NULL; END;")]
    public void Other_statements_name_no_unit(string sql) => Assert.Null(OracleOutput.CreatedUnit(sql));

    // ---- SET SERVEROUTPUT ----

    [Fact]
    public void Set_serveroutput_toggles_are_classified_before_the_batch_they_precede()
    {
        var c = new OracleLanguageService().Classify("SET SERVEROUTPUT ON\nSELECT 1 FROM dual;\nSET SERVEROUT OFF SIZE 1000\nSELECT 2 FROM dual;\nSET SERVEROUTPUT ON");
        Assert.Equal(2, c.Batches.Length);
        Assert.Null(c.Refusal);
        Assert.Equal([(0, true), (1, false), (2, true)], c.ServerOutput!.Select(t => (t.BeforeBatch, t.On)));
        Assert.Null(new OracleLanguageService().Classify("SELECT 1 FROM dual").ServerOutput);
    }

    // ---- Core hooks ----

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public CapturingFrames Frames { get; } = new();
        public ExecutionService Executions { get; }
        readonly Profile _profile;

        public Rig()
        {
            Provider.As(Engine.Mssql, new OracleLanguageService()); // Oracle's splitter and SET SERVEROUTPUT, over fake connections
            var store = LocalStore.InMemory();
            var profiles = new ProfileStore(store);
            _profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Log.Null);
            Sessions.RememberSecret(_profile.Id, "x");
            Executions = new ExecutionService(Sessions, Frames, Notifier, new HistoryStore(store), Log.Null);
        }

        public Session Open() => Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = _profile.Id, Secret = "x" }).SessionId);

        public Task<QueryDoneEvent> Run(Session s, string sql, ExecuteOptions? options = null) =>
            Notifier.WaitDone(Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = sql, Options = options }).ExecutionId!);

        public async ValueTask DisposeAsync()
        {
            await Executions.DisposeAsync();
            await Sessions.DisposeAsync();
        }
    }

    [Fact]
    public async Task Server_output_follows_the_option_and_the_scripts_toggles_and_stays_with_the_session()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        await rig.Run(s, "SELECT 1 FROM dual");
        Assert.Equal([false], rig.Provider.ServerOutputSets); // every run sets the borrowed connection to the session's state
        rig.Provider.ServerOutputSets.Clear();

        await rig.Run(s, "SELECT 1 FROM dual;\nSET SERVEROUTPUT ON\nSELECT 2 FROM dual;\nSET SERVEROUTPUT OFF\nSET SERVEROUTPUT ON");
        Assert.Equal([false, true, false, true], rig.Provider.ServerOutputSets);
        Assert.True(s.ServerOutput); // the trailing ON is the session's from now on
        rig.Provider.ServerOutputSets.Clear();

        await rig.Run(s, "SELECT 1 FROM dual", new ExecuteOptions { ServerOutput = false });
        Assert.Equal([false], rig.Provider.ServerOutputSets);
        Assert.False(s.ServerOutput);
    }

    [Fact]
    public async Task An_error_read_after_a_batch_fails_the_run_at_its_editor_line_and_the_script_goes_on()
    {
        await using var rig = new Rig();
        rig.Provider.AfterBatchText = text => text.Contains("PROCEDURE")
            ? [new ProviderMessage(Severity.Error, "PLS-00201: identifier 'X' must be declared", Line: 3, Number: 201, FailsRun: true)]
            : [];
        var s = rig.Open();
        var done = await rig.Run(s, "SELECT 1 FROM dual;\nCREATE PROCEDURE p AS\nBEGIN\n  x := 1;\nEND;\n/\nSELECT 2 FROM dual", new ExecuteOptions { LineOffset = 10 });
        Assert.Equal(ExecState.Failed, done.State);
        Assert.Equal(13, done.Error?.EditorLine); // batch line 3 of the CREATE (script line 4), the selection starting at editor line 10
        Assert.Equal(3, rig.Provider.Prepared.Count); // and the last SELECT still ran
        var m = rig.Notifier.Of<ServerMessage>().Single(x => x.Number == 201);
        Assert.Equal(13, m.EditorLine);
    }

    [Fact]
    public async Task An_error_row_read_after_a_batch_that_does_not_fail_the_run_is_only_shown()
    {
        // D4 S13 regression (every engine): MySQL / MariaDB's SHOW WARNINGS still lists the 1317 of a cancelled statement after a
        // table-less SELECT (which does not clear the diagnostics area); since S04 that failed the next run. Only a message the
        // provider marks FailsRun (Oracle's compile errors) fails it.
        await using var rig = new Rig();
        rig.Provider.AfterBatchText = _ => [new ProviderMessage(Severity.Error, "Query execution was interrupted", Number: 1317)];
        var s = rig.Open();
        var done = await rig.Run(s, "SELECT 1", new ExecuteOptions());
        Assert.Equal(ExecState.Done, done.State);
        Assert.Null(done.Error);
        Assert.Equal(Severity.Error, rig.Notifier.Of<ServerMessage>().Single(x => x.Number == 1317).Severity);
    }

    [Fact]
    public void A_described_column_takes_the_providers_shape_over_the_readers_type()
    {
        var t = FakeResult.Table([("n", typeof(decimal)), ("d", typeof(DateTime)), ("big", typeof(decimal)), ("t", typeof(string))],
            [1.25m, new DateTime(2024, 1, 2, 3, 4, 5), 7m, "abc"]); // no precision on the reader: without the shape, "n" would be text
        var provider = new FakeDbProvider
        {
            Shapes = _ =>
            [
                new ColumnShape(typeof(decimal), "NUMBER(12,3)", ColumnHint.Default, 12, 3),
                new ColumnShape(typeof(DateTime), "DATE") { TimestampMicros = (_, _) => -1_000_000 }, // "DATE" is no date-only here
                new ColumnShape(typeof(decimal), "NUMBER", ColumnHint.ProviderString),
                null, // the default mapping
            ],
        };
        using var r = t.CreateDataReader();
        var enc = new ResultSetEncoder(r, provider, new TruncatedCellStore(), 0);
        Assert.Equal(["decimal128(12,3)", "timestamp[us]", "utf8", "utf8"], enc.Columns.Where(c => !c.Hidden).Select(c => c.ArrowType));
        Assert.Equal(["NUMBER(12,3)", "DATE", "NUMBER"], enc.Columns.Take(3).Select(c => c.TypeName));
        Assert.Equal("1", enc.Schema.GetFieldByName("big").Metadata["q:numeric"]);
        r.Read();
        enc.AppendRow(r, 0);
        var b = enc.Flush();
        Assert.Equal(1.25m, ((Decimal128Array)b.Column(0)).GetValue(0));
        Assert.Equal(-1_000_000, ((TimestampArray)b.Column(1)).GetValue(0)); // 1969-12-31 23:59:59
    }

    /// <summary>A text value that never ends, counting the chunks read.</summary>
    internal sealed class EndlessText(Action? onRead = null) : TextReader
    {
        public int Reads;
        public override int Read(char[] buffer, int index, int count)
        {
            Reads++;
            onRead?.Invoke();
            System.Array.Fill(buffer, 'x', index, count);
            return count;
        }
    }

    [Fact]
    public void A_streamed_large_value_stops_between_chunks_when_the_run_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var reads = 0;
        var endless = new EndlessText(() => { if (++reads == 3) cts.Cancel(); });
        var t = FakeResult.Table([("c", typeof(string))], ["ignored"]);
        var provider = new FakeDbProvider { Shapes = _ => [new ColumnShape(typeof(string), "CLOB", ColumnHint.LargeText) { OpenText = (_, _) => endless }] };
        using var r = t.CreateDataReader();
        var enc = new ResultSetEncoder(r, provider, new TruncatedCellStore(), 0, ct: cts.Token);
        r.Read();
        Assert.Throws<OperationCanceledException>(() => enc.AppendRow(r, 0));
        Assert.Equal(3, endless.Reads);
    }

    [Fact]
    public void A_streamed_binary_value_stops_between_chunks_when_the_run_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var reads = 0;
        var t = FakeResult.Table([("b", typeof(byte[]))], [new byte[] { 1 }]);
        var provider = new FakeDbProvider
        {
            Shapes = _ => [new ColumnShape(typeof(byte[]), "BLOB") { OpenBinary = (_, _) => new EndlessBytes(() => { if (++reads == 2) cts.Cancel(); }) }],
        };
        using var r = t.CreateDataReader();
        var enc = new ResultSetEncoder(r, provider, new TruncatedCellStore(), 0, ct: cts.Token);
        r.Read();
        Assert.Throws<OperationCanceledException>(() => enc.AppendRow(r, 0));
        Assert.Equal(2, reads);
    }

    internal sealed class EndlessBytes(Action onRead) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) { onRead(); return count; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

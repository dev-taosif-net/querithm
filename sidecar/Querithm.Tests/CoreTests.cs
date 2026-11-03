using System.Data;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Logging;
using Querithm.Core.Store;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

public class UlidTests
{
    [Fact]
    public void Parse_accepts_lowercase()
    {
        var u = Ulid.New();
        Assert.Equal(u.Bytes, Ulid.Parse(u.ToString().ToLowerInvariant()).Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEFGHJKMNPQR")]   // 25
    [InlineData("0123456789ABCDEFGHJKMNPQRST")] // 27
    public void Parse_rejects_wrong_length(string s) => Assert.Throws<FormatException>(() => Ulid.Parse(s));

    [Theory]
    [InlineData('I')]
    [InlineData('L')]
    [InlineData('O')]
    [InlineData('U')]
    [InlineData('-')]
    public void Parse_rejects_characters_outside_crockford_base32(char bad)
    {
        Assert.Throws<FormatException>(() => Ulid.Parse(new string('0', 25) + bad));
    }

    [Fact]
    public void Extremes_roundtrip()
    {
        Assert.All(Ulid.Parse(new string('0', 26)).Bytes, b => Assert.Equal(0, b));
        var max = Ulid.Parse("7" + new string('Z', 25));
        Assert.All(max.Bytes, b => Assert.Equal(0xFF, b));
        Assert.Equal("7" + new string('Z', 25), max.ToString());
    }

    [Fact]
    public void Parse_rejects_values_above_128_bits()
    {
        Assert.Throws<FormatException>(() => Ulid.Parse("8" + new string('0', 25)));
    }

    [Fact]
    public void New_encodes_the_current_time_in_the_first_48_bits()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var b = Ulid.New().Bytes;
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long ms = 0;
        for (var i = 0; i < 6; i++) ms = (ms << 8) | b[i];
        Assert.InRange(ms, before, after);
    }

    [Fact]
    public void Ids_are_unique_and_sort_by_creation_time()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Ulid.NewId()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // Same-millisecond ids order randomly, but the 10-char time prefix never goes backwards.
        Assert.Equal(ids.Select(x => x[..10]).Order(StringComparer.Ordinal), ids.Select(x => x[..10]));
    }
}

public class JsonWireFormatTests
{
    [Fact]
    public void Enums_are_camel_case_strings_and_nulls_are_omitted()
    {
        var json = JsonSerializer.Serialize(new Profile { Id = "p", Name = "n", Engine = Engine.Mssql, Auth = AuthKind.EntraInteractive, TlsMode = TlsMode.VerifyFull }, Json.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("mssql", root.GetProperty("engine").GetString());
        Assert.Equal("entraInteractive", root.GetProperty("auth").GetString());
        Assert.Equal("verifyFull", root.GetProperty("tlsMode").GetString());
        Assert.False(root.TryGetProperty("host", out _));
        Assert.False(root.TryGetProperty("Host", out _));
    }

    [Fact]
    public void Integer_enum_values_are_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Profile>("""{"id":"p","name":"n","engine":1}""", Json.Options));
    }

    [Fact]
    public void Property_names_are_case_insensitive_on_read()
    {
        var p = JsonSerializer.Deserialize<Profile>("""{"ID":"p","Name":"n","ENGINE":"pg"}""", Json.Options)!;
        Assert.Equal(("p", Engine.Pg), (p.Id, p.Engine));
    }

    [Fact]
    public void Named_floating_point_literals_roundtrip()
    {
        var json = JsonSerializer.Serialize(new HistoryEntry { Sql = "x", Status = "done", ElapsedMs = double.NaN }, Json.Options);
        Assert.Contains("\"NaN\"", json);
        Assert.True(double.IsNaN(JsonSerializer.Deserialize<HistoryEntry>(json, Json.Options)!.ElapsedMs!.Value));
    }

    [Fact]
    public void Profile_roundtrips_with_options()
    {
        var p = new Profile
        {
            Id = "p", Name = "n", Engine = Engine.Pg, Host = "h", Port = 5432, Auth = AuthKind.Password, Environment = EnvironmentKind.Prod,
            TlsMode = TlsMode.VerifyCa, TrustCert = true, CaPath = "/ca", TimeoutS = 3, Options = new() { ["appName"] = "x" }, CreatedAt = 1, UpdatedAt = 2,
        };
        var back = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p, Json.Options), Json.Options)!;
        Assert.Equal("x", back.Options!["appName"]);
        Assert.Equal(p with { Options = back.Options }, back);
    }

    [Fact]
    public void Default_batch_repeat_count_survives_a_missing_field()
    {
        var b = JsonSerializer.Deserialize<BatchInfo>("""{"index":2,"startLine":1}""", Json.Options)!;
        Assert.Equal((2, 1), (b.Index, b.RepeatCount));
    }

    [Fact]
    public void Source_generated_context_matches_the_reflection_options()
    {
        ProfileGroup[] groups = [new() { Name = "Ops", Environment = EnvironmentKind.Prod }, new() { Name = "Dev" }];
        FkColumnPair[] fks = [new() { Parent = "a", Referenced = "b" }];
        Assert.Equal(JsonSerializer.Serialize(groups, Json.Options), JsonSerializer.Serialize(groups, Json.Context.ProfileGroupArray));
        Assert.Equal(JsonSerializer.Serialize(fks, Json.Options), JsonSerializer.Serialize(fks, Json.Context.FkColumnPairArray));
        Assert.Equal(EnvironmentKind.Prod, JsonSerializer.Deserialize("""{"NAME":"x","environment":"prod"}""", Json.Context.ProfileGroup)!.Environment);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"name":"x","environment":3}""", Json.Context.ProfileGroup));
    }
}

public class LogScrubberTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("SELECT name, passwordHash FROM users WHERE id = 1", "SELECT name, passwordHash FROM users WHERE id = 1")]
    [InlineData("the password column was renamed", "the password column was renamed")]
    [InlineData("Server=h;Database=d;User ID=u", "Server=h;Database=d;User ID=u")]
    public void Text_without_secrets_is_unchanged(string? input, string expected)
    {
        Assert.Equal(expected, LogScrubber.Scrub(input));
    }

    [Theory]
    [InlineData("PASSWORD = Abc123;Server=x", "PASSWORD=***;Server=x")]
    [InlineData("Pwd=\"a b\";x=1", "Pwd=***;x=1")]
    [InlineData("password='q w e' rest", "password=*** rest")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz trailing", "Authorization: *** trailing")]
    [InlineData("{\"accessToken\":\"t\\\"ok\",\"n\":1}", "{\"accessToken\":\"***\",\"n\":1}")]
    [InlineData("{\"Password\" : \"pw-json\"}", "{\"Password\" : \"***\"}")]
    [InlineData("AccessToken=xyz.123;Server=s", "AccessToken=***;Server=s")]
    public void Secrets_are_replaced_and_the_rest_is_kept(string input, string expected)
    {
        Assert.Equal(expected, LogScrubber.Scrub(input));
    }

    [Fact]
    public void Every_occurrence_is_masked()
    {
        var s = LogScrubber.Scrub("Password=one-x; then pwd=two-y and Password=three-z");
        Assert.DoesNotContain("one-x", s);
        Assert.DoesNotContain("two-y", s);
        Assert.DoesNotContain("three-z", s);
    }

    [Fact]
    public void Very_short_or_empty_secrets_are_not_registered()
    {
        LogScrubber.RegisterSecret(null);
        LogScrubber.RegisterSecret("");
        LogScrubber.RegisterSecret("q7");
        Assert.Equal("q7 is a short word", LogScrubber.Scrub("q7 is a short word"));
    }

    [Fact]
    public void Registered_secret_is_matched_case_sensitively()
    {
        var secret = "Kx" + Guid.NewGuid().ToString("N")[..10];
        LogScrubber.RegisterSecret(secret);
        Assert.Equal("a *** b *** c", LogScrubber.Scrub($"a {secret} b {secret} c"));
        Assert.Equal(secret.ToUpperInvariant(), LogScrubber.Scrub(secret.ToUpperInvariant()));
    }
}

public class StatisticsParserTests
{
    [Fact]
    public void Lob_reads_do_not_shadow_logical_reads()
    {
        var s = StatisticsParser.Parse("Table 'Docs'. Scan count 2, logical reads 3, physical reads 4, read-ahead reads 5, lob logical reads 6, lob physical reads 7.", 4)!;
        Assert.Equal((4, "Docs"), (s.BatchIndex, s.Table));
        Assert.Equal((2L, 3L, 4L, 5L, 6L), (s.ScanCount, s.LogicalReads, s.PhysicalReads, s.ReadAheadReads, s.LobLogicalReads));
    }

    [Fact]
    public void Missing_counters_are_null()
    {
        var s = StatisticsParser.Parse("Table 'Worktable'. Scan count 0.", 0)!;
        Assert.Equal(0, s.ScanCount);
        Assert.Null(s.LogicalReads);
        Assert.Null(s.LobLogicalReads);
    }

    [Fact]
    public void Table_names_with_spaces_and_dots_are_kept()
    {
        Assert.Equal("#tmp_____0001.x y", StatisticsParser.Parse("Table '#tmp_____0001.x y'. Scan count 1, logical reads 1.", 0)!.Table);
    }

    [Fact]
    public void Time_message_carries_cpu_and_elapsed()
    {
        var s = StatisticsParser.Parse(" SQL Server Execution Times:\r\n   CPU time = 31 ms,  elapsed time = 40 ms.", 1)!;
        Assert.Equal(("time", 31d, 40d, 1), (s.Kind, s.CpuMs, s.ElapsedMs, s.BatchIndex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("(1 row affected)")]
    [InlineData("Warning: Null value is eliminated by an aggregate.")]
    [InlineData("Table Orders scan count 1")]
    public void Other_messages_are_not_statistics(string msg) => Assert.Null(StatisticsParser.Parse(msg, 0));
}

public class EnumTextTests
{
    [Theory]
    [InlineData(TlsMode.VerifyFull, "verifyFull")]
    [InlineData(AuthKind.EntraDeviceCode, "entraDeviceCode")]
    [InlineData(Engine.Pg, "pg")]
    public void Of_writes_camel_case<T>(T value, string text) where T : struct, Enum => Assert.Equal(text, EnumText.Of(value));

    [Theory]
    [InlineData("verifyFull", TlsMode.VerifyFull)]
    [InlineData("VERIFYFULL", TlsMode.VerifyFull)]
    [InlineData(null, TlsMode.Prefer)]
    [InlineData("nonsense", TlsMode.Prefer)]
    public void Parse_is_case_insensitive_with_fallback(string? text, TlsMode expected)
    {
        Assert.Equal(expected, EnumText.Parse(text, TlsMode.Prefer));
    }
}

public class TruncatedCellStoreTests
{
    [Fact]
    public void Budget_is_enforced_and_clear_resets_it()
    {
        var s = new TruncatedCellStore();
        Assert.True(s.TryAdd(0, 0, 0, "a", TruncatedCellStore.BudgetBytes - 10));
        Assert.True(s.TryAdd(0, 1, 0, "b", 10));
        Assert.False(s.TryAdd(0, 2, 0, "c", 1));
        Assert.Null(s.Get(0, 2, 0));
        s.Clear();
        Assert.Null(s.Get(0, 0, 0));
        Assert.True(s.TryAdd(0, 2, 0, "c", TruncatedCellStore.BudgetBytes));
    }

    [Fact]
    public void Cells_are_keyed_by_result_row_and_column()
    {
        var s = new TruncatedCellStore();
        s.TryAdd(1, 2, 3, "x", 1);
        Assert.Equal("x", s.Get(1, 2, 3));
        Assert.Null(s.Get(0, 2, 3));
        Assert.Null(s.Get(1, 3, 2));
    }
}

public class ResultSetEncoderTests
{
    static (ResultSetEncoder Enc, RecordBatch Batch) Encode(DataTable t, TruncatedCellStore? cells = null)
    {
        using var r = t.CreateDataReader();
        var enc = new ResultSetEncoder(r, new SqlServerProvider(), cells ?? new TruncatedCellStore(), 0);
        long row = 0;
        while (r.Read()) enc.AppendRow(r, row++);
        var batch = TestHost.ReadBatch(ResultSetEncoder.ToIpc(enc.Schema, enc.Flush()));
        return (enc, batch);
    }

    [Fact]
    public void Primitive_types_map_to_arrow_types()
    {
        var t = new DataTable();
        t.Columns.Add("i32", typeof(int));
        t.Columns.Add("i16", typeof(short));
        t.Columns.Add("u8", typeof(byte));
        t.Columns.Add("f64", typeof(double));
        t.Columns.Add("f32", typeof(float));
        t.Rows.Add(-7, (short)3, (byte)250, 1.25, 0.5f);
        t.Rows.Add(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        var (enc, b) = Encode(t);
        Assert.Equal(["int32", "int16", "uint8", "double", "float"], enc.Columns.Select(c => c.ArrowType));
        Assert.Equal(-7, ((Int32Array)b.Column("i32")).GetValue(0));
        Assert.Equal((byte)250, ((UInt8Array)b.Column("u8")).GetValue(0));
        Assert.Equal(1.25, ((DoubleArray)b.Column("f64")).GetValue(0));
        Assert.All(Enumerable.Range(0, 5), i => Assert.True(b.Column(i).IsNull(1)));
    }

    [Fact]
    public void Guids_are_text_and_column_metadata_records_the_ordinal()
    {
        var t = new DataTable();
        t.Columns.Add("a", typeof(int));
        t.Columns.Add("g", typeof(Guid));
        var g = Guid.NewGuid();
        t.Rows.Add(1, g);
        var (enc, b) = Encode(t);
        Assert.Equal(g.ToString("D"), ((StringArray)b.Column("g")).GetString(0));
        Assert.Equal("1", enc.Schema.GetFieldByName("g").Metadata["q:ordinal"]);
        Assert.Equal(1, enc.Columns.Single(c => c.Name == "g").Ordinal);
    }

    [Fact]
    public void Flag_column_name_avoids_collisions_with_real_columns()
    {
        var t = new DataTable();
        t.Columns.Add("Blob__truncated", typeof(int));
        t.Columns.Add("Blob", typeof(byte[]));
        t.Rows.Add(1, new byte[] { 9 });
        var (enc, b) = Encode(t);
        var blob = enc.Columns.Single(c => c.Name == "Blob");
        Assert.Equal("Blob__truncated_1", blob.TruncatedFlagColumn);
        Assert.Equal(1, ((Int32Array)b.Column("Blob__truncated")).GetValue(0));
        Assert.False(((BooleanArray)b.Column("Blob__truncated_1")).GetValue(0));
        Assert.Equal(enc.Schema.FieldsList.Select(f => f.Name), enc.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Binary_at_the_limit_is_not_truncated()
    {
        var t = new DataTable();
        t.Columns.Add("b", typeof(byte[]));
        t.Rows.Add(new byte[ResultSetEncoder.BinaryLimitBytes]);
        t.Rows.Add(new byte[ResultSetEncoder.BinaryLimitBytes + 1]);
        var cells = new TruncatedCellStore();
        var (_, b) = Encode(t, cells);
        var flag = (BooleanArray)b.Column("b__truncated");
        Assert.False(flag.GetValue(0));
        Assert.True(flag.GetValue(1));
        Assert.Null(cells.Get(0, 0, 0));
        Assert.Equal(ResultSetEncoder.BinaryLimitBytes + 1, ((byte[])cells.Get(0, 1, 0)!).Length);
    }

    [Fact]
    public void Unbounded_text_over_the_limit_is_truncated_and_kept_in_full()
    {
        var t = new DataTable();
        t.Columns.Add("s", typeof(string)); // no MaxLength → unbounded
        var big = new string('x', ResultSetEncoder.TextLimitChars + 5);
        t.Rows.Add("short");
        t.Rows.Add(big);
        var cells = new TruncatedCellStore();
        var (enc, b) = Encode(t, cells);
        Assert.Equal("s__truncated", enc.Columns.Single(c => c.Name == "s").TruncatedFlagColumn);
        var s = (StringArray)b.Column("s");
        Assert.Equal("short", s.GetString(0));
        Assert.Equal(ResultSetEncoder.TextLimitChars, s.GetString(1).Length);
        Assert.True(((BooleanArray)b.Column("s__truncated")).GetValue(1));
        Assert.Equal(big, cells.Get(0, 1, 0));
    }

    [Fact]
    public void Should_flush_at_the_row_limit_and_flush_resets()
    {
        var t = new DataTable();
        t.Columns.Add("n", typeof(int));
        for (var i = 0; i < ResultSetEncoder.MaxBatchRows; i++) t.Rows.Add(i);
        using var r = t.CreateDataReader();
        var enc = new ResultSetEncoder(r, new SqlServerProvider(), new TruncatedCellStore(), 0);
        long row = 0;
        while (r.Read())
        {
            Assert.False(enc.ShouldFlush);
            enc.AppendRow(r, row++);
        }
        Assert.True(enc.ShouldFlush);
        using var batch = enc.Flush();
        Assert.Equal(ResultSetEncoder.MaxBatchRows, batch.Length);
        Assert.Equal(0, enc.PendingRows);
        Assert.False(enc.ShouldFlush);
        Assert.Equal(0, enc.EstimatedBytes);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("bool")]
    [InlineData("binary")]
    [InlineData("time64[us]")]
    [InlineData("timestamp[us]")]
    [InlineData("timestamp[us,UTC]")]
    [InlineData("decimal128(18,2)")]
    public void Describe_names_arrow_types(string expected)
    {
        IArrowType type = expected switch
        {
            "utf8" => StringType.Default,
            "bool" => BooleanType.Default,
            "binary" => BinaryType.Default,
            "time64[us]" => new Time64Type(TimeUnit.Microsecond),
            "timestamp[us]" => new TimestampType(TimeUnit.Microsecond, (string?)null),
            "timestamp[us,UTC]" => new TimestampType(TimeUnit.Microsecond, "UTC"),
            _ => new Decimal128Type(18, 2),
        };
        Assert.Equal(expected, ResultSetEncoder.Describe(type));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("s", "s")]
    [InlineData(new byte[] { 0xAB, 0x01 }, "0xAB01")]
    [InlineData(1.5, "1.5")]
    [InlineData(new[] { 1, 2, 3 }, "{1,2,3}")]
    public void Format_value_is_culture_invariant(object? value, string expected)
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try { Assert.Equal(expected, ColumnEncoder.FormatValue(value)); }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }
}

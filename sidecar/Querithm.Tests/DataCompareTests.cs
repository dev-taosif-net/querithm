using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Compare;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Data compare without a server: key order and value normalisation, the merge join, and each engine's rules.</summary>
public class DataCompareTests
{
    static readonly DataCompareOptions O = new();

    // ------------------------------------------------------------------ key order

    [Fact]
    public void Text_keys_sort_by_code_point_like_bin2_and_collate_c()
    {
        string[] words = ["a", "B", "c", "Zeta", "alpha", "Ärger", "zz", "_x", "0", "€", "\U0001F600", ""];
        var sorted = words.OrderBy(w => (IComparable)KeyComparers.Normalize(ValueKind.Text, w, false, "w"), Comparer<IComparable>.Create((a, b) => a.CompareTo(b))).ToArray();
        // Code point order: digits, upper case, underscore, lower case, then Ä, €, the private-use U+E000 and the emoji (U+1F600) last.
        Assert.Equal(["0", "B", "Zeta", "_x", "a", "alpha", "c", "zz", "Ärger", "€", "", "\U0001F600"], sorted);
        Assert.True(string.CompareOrdinal("\U0001F600", "") < 0); // UTF-16 ordinal would get this one wrong
    }

    [Fact]
    public void Padded_engines_ignore_trailing_spaces_in_text_keys()
    {
        var a = KeyComparers.Normalize(ValueKind.Text, "ab  ", padded: true, "k");
        var b = KeyComparers.Normalize(ValueKind.Text, "ab", padded: true, "k");
        Assert.Equal(0, a.CompareTo(b));
        Assert.NotEqual(0, KeyComparers.Normalize(ValueKind.Text, "ab  ", false, "k").CompareTo(KeyComparers.Normalize(ValueKind.Text, "ab", false, "k")));
        Assert.Equal(0, KeyComparers.Normalize(ValueKind.PaddedText, "ab  ", false, "k").CompareTo(b));
    }

    [Fact]
    public void Guid_keys_compare_by_canonical_text_whatever_the_case()
    {
        var upper = KeyComparers.Normalize(ValueKind.Guid, "6F9619FF-8B86-D011-B42D-00C04FC964FF", false, "g");
        var lower = KeyComparers.Normalize(ValueKind.Guid, "6f9619ff-8b86-d011-b42d-00c04fc964ff", false, "g");
        Assert.Equal(0, upper.CompareTo(lower));
        // Text order, not SQL Server's byte order (which compares the last six bytes first).
        var a = KeyComparers.Normalize(ValueKind.Guid, "00000000-0000-0000-0000-000000000001", false, "g");
        var b = KeyComparers.Normalize(ValueKind.Guid, "00000001-0000-0000-0000-000000000000", false, "g");
        Assert.True(a.CompareTo(b) < 0);
    }

    [Fact]
    public void Numbers_dates_and_offsets_order_by_value()
    {
        int C(ValueKind k, string x, string y) => KeyComparers.CompareKeys([KeyComparers.Normalize(k, x, false, "k")], [KeyComparers.Normalize(k, y, false, "k")]);
        Assert.True(C(ValueKind.Integer, "9", "10") < 0);
        Assert.Equal(0, C(ValueKind.Decimal, "1.50", "1.5"));
        Assert.True(C(ValueKind.DateTime, "2026-10-21 09:00:00.0000000", "2026-10-21 10:00:00") < 0);
        Assert.Equal(0, C(ValueKind.DateTimeOffset, "2026-10-21 12:00:00.0000000 +02:00", "2026-10-21 10:00:00+00"));
        Assert.Equal(0, KeyComparers.CompareKeys([KeyComparers.Normalize(ValueKind.Integer, "3", false, "k")], [KeyComparers.Normalize(ValueKind.Float, "3", false, "k")]));
        Assert.Equal("compareKey", Assert.Throws<QuerithmException>(() => KeyComparers.Normalize(ValueKind.Integer, "abc", false, "id")).Code);
        Assert.Equal("compareKey", Assert.Throws<QuerithmException>(() => KeyComparers.Normalize(ValueKind.Unordered, "<x/>", false, "doc")).Code);
    }

    // ------------------------------------------------------------------ value equality

    [Theory]
    [InlineData(ValueKind.Decimal, ValueKind.Decimal, "1.50", "1.5", true)]
    [InlineData(ValueKind.Decimal, ValueKind.Integer, "2.00", "2", true)]
    [InlineData(ValueKind.Bool, ValueKind.Bool, "1", "true", true)]
    [InlineData(ValueKind.Bool, ValueKind.Bool, "0", "true", false)]
    [InlineData(ValueKind.Guid, ValueKind.Guid, "6F9619FF-8B86-D011-B42D-00C04FC964FF", "6f9619ff-8b86-d011-b42d-00c04fc964ff", true)]
    [InlineData(ValueKind.Binary, ValueKind.Binary, "0x0AFF", "\\x0aff", true)]
    [InlineData(ValueKind.DateTime, ValueKind.DateTime, "2026-10-21 10:00:00.0000000", "2026-10-21 10:00:00", true)]
    [InlineData(ValueKind.DateTimeOffset, ValueKind.DateTimeOffset, "2026-10-21 10:00:00.0000000 +00:00", "2026-10-21 10:00:00+00", true)]
    [InlineData(ValueKind.PaddedText, ValueKind.Text, "ab   ", "ab", true)]
    [InlineData(ValueKind.Text, ValueKind.Text, "ab ", "ab", false)]
    [InlineData(ValueKind.Text, ValueKind.Text, "Ab", "ab", false)]
    [InlineData(ValueKind.Float, ValueKind.Float, "0.1", "0.1000000000000001", true)]
    [InlineData(ValueKind.Float, ValueKind.Float, "0.1", "0.1001", false)]
    [InlineData(ValueKind.Float, ValueKind.Decimal, "1.0000000000000000e+010", "10000000000", true)]
    public void Values_are_equal_after_normalising_their_type(ValueKind ks, ValueKind kt, string a, string b, bool equal) =>
        Assert.Equal(equal, KeyComparers.ValuesEqual(ks, kt, a, b, O));

    [Fact]
    public void Options_loosen_case_and_time()
    {
        Assert.True(KeyComparers.ValuesEqual(ValueKind.Text, ValueKind.Text, "Ab", "ab", O with { IgnoreCase = true }));
        // SQL Server datetime keeps 1/300 s; PostgreSQL timestamp microseconds.
        Assert.False(KeyComparers.ValuesEqual(ValueKind.DateTime, ValueKind.DateTime, "2026-10-21 10:00:00.003", "2026-10-21 10:00:00.0012", O));
        Assert.True(KeyComparers.ValuesEqual(ValueKind.DateTime, ValueKind.DateTime, "2026-10-21 10:00:00.003", "2026-10-21 10:00:00.0012", O with { TimeToleranceMs = 3 }));
        Assert.True(KeyComparers.ValuesEqual(ValueKind.Text, ValueKind.Text, null, null, O));
        Assert.False(KeyComparers.ValuesEqual(ValueKind.Text, ValueKind.Text, null, "", O));
        Assert.False(KeyComparers.ValuesEqual(ValueKind.PaddedText, ValueKind.Text, "ab  ", "ab", O with { TrimCharPadding = false }));
    }

    // ------------------------------------------------------------------ merge join

    static async IAsyncEnumerable<string?[]> Rows(IEnumerable<string?[]> rows)
    {
        foreach (var r in rows) { await Task.Yield(); yield return r; }
    }

    static ComparePlan Plan(int values, ValueKind key = ValueKind.Integer, int keys = 1) => new(
        Enumerable.Range(0, keys).Select(i => $"k{i}").ToArray(), Enumerable.Repeat(key, keys).ToArray(), Enumerable.Repeat(key, keys).ToArray(),
        Enumerable.Range(0, values).Select(i => $"v{i}").ToArray(), Enumerable.Repeat(ValueKind.Text, values).ToArray(), Enumerable.Repeat(ValueKind.Text, values).ToArray(),
        false, O);

    static async Task<(CompareCounts Counts, List<DiffRow> Diffs)> Merge(ComparePlan plan, string?[][] source, string?[][] target)
    {
        var counts = new CompareCounts();
        var diffs = new List<DiffRow>();
        await DataComparer.MergeAsync(Rows(source).GetAsyncEnumerator(), Rows(target).GetAsyncEnumerator(), plan, counts, d => { diffs.Add(d); return ValueTask.CompletedTask; }, CancellationToken.None);
        return (counts, diffs);
    }

    [Fact]
    public async Task The_merge_classes_every_row_and_counts_identical_ones_only()
    {
        var (c, d) = await Merge(Plan(1),
            [["1", "a"], ["2", "b"], ["4", "d"], ["6", null]],
            [["1", "a"], ["2", "B"], ["3", "c"], ["6", null], ["7", "g"]]);
        Assert.Equal((2L, 1L, 1L, 2L, 4L, 5L), (c.Identical, c.Different, c.SourceOnly, c.TargetOnly, c.SourceRows, c.TargetRows));
        Assert.Equal([DiffKind.Different, DiffKind.TargetOnly, DiffKind.SourceOnly, DiffKind.TargetOnly], d.Select(x => x.Kind));
        Assert.True(d[0].Differs(0));
        Assert.Null(d[1].Source);
        Assert.Null(d[2].Target);
    }

    [Fact]
    public async Task Composite_keys_and_wide_rows()
    {
        var plan = Plan(70, ValueKind.Text, keys: 2);
        string?[] Row(string a, string b, int changed = -1) => [a, b, .. Enumerable.Range(0, 70).Select(i => i == changed ? "X" : $"v{i}")];
        var (c, d) = await Merge(plan, [Row("a", "1"), Row("a", "2"), Row("b", "1")], [Row("a", "1"), Row("a", "2", changed: 66), Row("b", "1", changed: 3)]);
        Assert.Equal((1L, 2L), (c.Identical, c.Different));
        Assert.Equal(9, d[0].Mask.Length);
        Assert.True(d[0].Differs(66));
        Assert.False(d[0].Differs(65));
        Assert.Equal([3], Enumerable.Range(0, 70).Where(d[1].Differs));
    }

    [Fact]
    public async Task Repeated_null_and_unordered_keys_stop_the_compare_with_a_reason()
    {
        var dup = await Assert.ThrowsAsync<QuerithmException>(() => Merge(Plan(1), [["1", "a"], ["1", "b"]], []));
        Assert.Contains("not unique in the source", dup.Message);
        var nul = await Assert.ThrowsAsync<QuerithmException>(() => Merge(Plan(1), [], [[null, "a"]]));
        Assert.Contains("NULL in a target row", nul.Message);
        var order = await Assert.ThrowsAsync<QuerithmException>(() => Merge(Plan(1), [["2", "a"], ["1", "b"]], []));
        Assert.Equal("compareOrder", order.Code);
    }

    // ------------------------------------------------------------------ engines

    [Theory]
    [InlineData("nvarchar(50)", ValueKind.Text)]
    [InlineData("nchar(5)", ValueKind.PaddedText)]
    [InlineData("bigint", ValueKind.Integer)]
    [InlineData("decimal(10,2)", ValueKind.Decimal)]
    [InlineData("money", ValueKind.Decimal)]
    [InlineData("float", ValueKind.Float)]
    [InlineData("bit", ValueKind.Bool)]
    [InlineData("uniqueidentifier", ValueKind.Guid)]
    [InlineData("datetime2(3)", ValueKind.DateTime)]
    [InlineData("datetimeoffset", ValueKind.DateTimeOffset)]
    [InlineData("varbinary(max)", ValueKind.Binary)]
    [InlineData("xml", ValueKind.Unordered)]
    public void Sql_server_kinds(string type, ValueKind kind) => Assert.Equal(kind, new SqlServerDataCompare().KindOf(type));

    [Theory]
    [InlineData("character varying(50)", ValueKind.Text)]
    [InlineData("character(5)", ValueKind.PaddedText)]
    [InlineData("integer", ValueKind.Integer)]
    [InlineData("numeric(10,2)", ValueKind.Decimal)]
    [InlineData("double precision", ValueKind.Float)]
    [InlineData("boolean", ValueKind.Bool)]
    [InlineData("uuid", ValueKind.Guid)]
    [InlineData("timestamp without time zone", ValueKind.DateTime)]
    [InlineData("timestamp with time zone", ValueKind.DateTimeOffset)]
    [InlineData("bytea", ValueKind.Binary)]
    [InlineData("jsonb", ValueKind.Unordered)]
    [InlineData("integer[]", ValueKind.Unordered)]
    [InlineData("mood", ValueKind.Text)]
    public void Postgres_kinds(string type, ValueKind kind) => Assert.Equal(kind, new PostgresDataCompare().KindOf(type));

    [Fact]
    public void Keys_are_ordered_in_a_collation_free_order_on_each_engine()
    {
        var ss = new SqlServerDataCompare();
        Assert.Equal("CONVERT(nvarchar(max), [w]) COLLATE Latin1_General_100_BIN2", ss.OrderExpression("[w]", ValueKind.Text));
        Assert.Equal("CONVERT(char(36), [g]) COLLATE Latin1_General_100_BIN2", ss.OrderExpression("[g]", ValueKind.Guid));
        Assert.Equal("[id]", ss.OrderExpression("[id]", ValueKind.Integer));
        Assert.True(ss.PadsTrailingSpaces);
        var pg = new PostgresDataCompare();
        Assert.Equal("(\"w\")::text COLLATE \"C\"", pg.OrderExpression("\"w\"", ValueKind.Text));
        Assert.Equal("(\"g\")::text COLLATE \"C\"", pg.OrderExpression("\"g\"", ValueKind.Guid));
        Assert.False(pg.PadsTrailingSpaces);
    }

    [Theory]
    [InlineData("SELECT id, name FROM dbo.t WHERE id > 1", null)]
    [InlineData("WITH c AS (SELECT 1 AS id) SELECT id FROM c;", null)]
    [InlineData("DELETE FROM dbo.t", "exactly one SELECT")]
    [InlineData("SELECT 1 AS id; DROP TABLE dbo.t", "exactly one SELECT")]
    [InlineData("SELECT id INTO dbo.copy FROM dbo.t", "INTO")]
    [InlineData("SELECT @x = id FROM dbo.t", "variables")]
    [InlineData("SELECT FROM", "does not parse")]
    public void Sql_server_queries_must_be_one_select(string sql, string? refusal)
    {
        var r = new SqlServerDataCompare().QueryRefusal(sql);
        if (refusal == null) Assert.Null(r); else Assert.Contains(refusal, r);
    }

    [Theory]
    [InlineData("SELECT id, name FROM t WHERE id > 1", null)]
    [InlineData("WITH c AS (SELECT 1 AS id) SELECT id FROM c;", null)]
    [InlineData("VALUES (1, 'a')", null)]
    [InlineData("DELETE FROM t", "SELECT")]
    [InlineData("SELECT 1; DROP TABLE t", "one statement")]
    [InlineData("SELECT ';' AS semi", null)]
    public void Postgres_queries_must_be_one_statement(string sql, string? refusal)
    {
        var r = new PostgresDataCompare().QueryRefusal(sql);
        if (refusal == null) Assert.Null(r); else Assert.Contains(refusal, r);
    }

    [Fact]
    public void Identity_inserts_per_engine()
    {
        Assert.Equal(("SET IDENTITY_INSERT [dbo].[t] ON;", "SET IDENTITY_INSERT [dbo].[t] OFF;"), new SqlServerDataCompare().IdentityInsert("[dbo].[t]"));
        Assert.Null(new SqlServerDataCompare().InsertOverriding);
        Assert.Null(new PostgresDataCompare().IdentityInsert("\"t\""));
        Assert.Equal("OVERRIDING SYSTEM VALUE", new PostgresDataCompare().InsertOverriding);
        Assert.StartsWith("SET XACT_ABORT ON;", new SqlServerDataCompare().SyncTransaction.Begin);
    }

    [Fact]
    public async Task An_engine_without_data_compare_says_so()
    {
        await using var h = new TestHost();
        var store = Querithm.Core.Store.LocalStore.InMemory();
        var profiles = new Querithm.Core.Store.ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" });
        var provider = new MetadataRefreshTests.FakeProvider(new MetadataRefreshTests.SlowMetadata());
        var sessions = new Querithm.Core.Sessions.SessionManager(profiles, new Querithm.Core.Sessions.ProviderRegistry([provider]), new CapturingNotifier(), Log.Null);
        var metaStore = new Querithm.Core.Store.MetadataStore(store);
        var metadata = new Querithm.Core.Metadata.MetadataService(sessions, metaStore, new Querithm.Core.Metadata.SchemaCache(metaStore), new CapturingNotifier(), Log.Null);
        var service = new DataCompareService(sessions, metadata, h.Services.Executions, new CapturingNotifier(), Log.Null);
        var sid = sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Database = "db" }).SessionId;
        var side = new DataCompareSide { SessionId = sid, Table = "t" };
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => service.StartAsync(new DataCompareParams { Source = side, Target = side }, CancellationToken.None));
        Assert.Equal("compareUnsupported", ex.Code);
        Assert.Contains("not available", ex.Message);
    }

    /// <summary>A data plane whose reader stopped taking batches: a batch waits until its token fires; other frames pass.</summary>
    sealed class StalledBatches : Querithm.Core.IFrameSink
    {
        public readonly TaskCompletionSource BatchParked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly System.Collections.Concurrent.ConcurrentQueue<(byte Kind, string Id)> Written = new();

        public async ValueTask WriteFrameAsync(byte kind, Ulid executionId, ushort resultIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (kind == Querithm.Core.FrameKind.Batch)
            {
                BatchParked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            Written.Enqueue((kind, executionId.ToString()));
        }
    }

    [Fact]
    public async Task Closing_the_compare_s_execution_stops_its_producer_even_while_the_data_plane_is_full()
    {
        // H3: the tab closed (query.close) while the diff stream was parked on backpressure; before, the producer wrote
        // with a token query.close never cancelled, so it held the data plane and both server connections for good.
        var provider = new Fakes.FakeDbProvider { DataCompareImpl = new SqlServerDataCompare(), DataEditImpl = new SqlServerDataEdit() };
        var rows = Enumerable.Range(1, 5_000).Select(i => new object?[] { i.ToString(), $"v{i}" }).ToArray();
        var reads = 0;
        provider.Handler = (cmd, ct) =>
        {
            if (!cmd.CommandText.Contains("ORDER BY")) return Task.FromResult(Fakes.FakeResult.None);
            Interlocked.Increment(ref reads);
            var source = cmd.CommandText.Contains("[s]");
            return Task.FromResult(new Fakes.FakeResult([Fakes.FakeResult.Table([("c0", typeof(string)), ("c1", typeof(string))], source ? rows : [])]));
        };
        var frames = new StalledBatches();
        var notifier = new CapturingNotifier();
        await using var services = Querithm.Host.Services.Create(Querithm.Core.Store.LocalStore.InMemory(), frames, notifier,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new Querithm.Core.Sessions.ProviderRegistry([provider]));
        var profile = services.Profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        services.Metadata.Store.ApplyRefresh(profile.Id, "db", true,
            [new ObjectStamp(1, "dbo", "s", ObjectKind.Table, 1, null), new ObjectStamp(2, "dbo", "t", ObjectKind.Table, 1, null)], [],
            new MetadataBundle(
            [
                new ColumnRow(1, 1, "id", "int", null, null, null, false, false, false, null), new ColumnRow(1, 2, "v", "nvarchar", 50, null, null, true, false, false, null),
                new ColumnRow(2, 1, "id", "int", null, null, null, false, false, false, null), new ColumnRow(2, 2, "v", "nvarchar", 50, null, null, true, false, false, null),
            ], [], [], []), "16");
        services.Metadata.Cache.Invalidate(profile.Id, "db");
        var sid = services.Sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Secret = "x", Database = "db" }).SessionId;

        var start = await services.DataCompare.StartAsync(new DataCompareParams
        {
            Source = new DataCompareSide { SessionId = sid, Database = "db", Schema = "dbo", Table = "s" },
            Target = new DataCompareSide { SessionId = sid, Database = "db", Schema = "dbo", Table = "t" },
            Key = ["id"],
        }, CancellationToken.None);
        await frames.BatchParked.Task.WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, reads); // both sides are streaming

        services.Executions.Close(start.ExecutionId);

        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        DataCompareProgressEvent? done;
        while ((done = notifier.Of<DataCompareProgressEvent>().FirstOrDefault(e => e.CompareId == start.CompareId && e.Done)) == null)
        {
            Assert.True(DateTime.UtcNow < end, "the compare did not stop after its execution was closed");
            await Task.Delay(10);
        }
        Assert.True(done.Cancelled);
        Assert.Contains((Querithm.Core.FrameKind.EndOfExecution, start.ExecutionId), frames.Written);
        Assert.Equal(0, notifier.Of<DataCompareProgressEvent>().Count(e => e.CompareId == start.CompareId && e.Done && !e.Cancelled));
    }

    [Fact]
    public async Task Merge_caps_recorded_diff_rows_while_counting_every_row()
    {
        var plan = Plan(1);
        var source = Enumerable.Range(1, 20).Select(i => new string?[] { i.ToString(), $"s{i}" }).ToArray();
        var target = Enumerable.Range(1, 20).Select(i => new string?[] { i.ToString(), i <= 5 ? $"s{i}" : $"t{i}" }).ToArray();
        var counts = new CompareCounts();
        var diffs = new List<DiffRow>();
        await DataComparer.MergeAsync(Rows(source).GetAsyncEnumerator(), Rows(target).GetAsyncEnumerator(), plan, counts,
            d => { if (diffs.Count < 4) diffs.Add(d); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.Equal(5, counts.Identical);
        Assert.Equal(15, counts.Different);
        Assert.Equal(20, counts.SourceRows);
        Assert.Equal(20, counts.TargetRows);
        Assert.Equal(4, diffs.Count);
    }

    [Fact]
    public void Sync_preview_literals_and_cross_timezone_equality()
    {
        var ss = new SqlServerDataEdit();
        Assert.Equal("NULL", ss.PreviewLiteral("int", new GridValue { IsNull = true }));
        Assert.Equal("42", ss.PreviewLiteral("int", new GridValue { Text = "42" }));
        Assert.Equal("N'it''s'", ss.PreviewLiteral("nvarchar(20)", new GridValue { Text = "it's" }));
        Assert.Equal("0x0AFF", ss.PreviewLiteral("varbinary(10)", new GridValue { Text = "0x0AFF" }));
        // 8a review: an exact numeric written with an exponent goes out as the plain number it is ('1E5' fails as varchar
        // to numeric), digit by digit; float keeps its exponent; an int never takes one; an absurd exponent stays quoted.
        Assert.Equal("100000", ss.PreviewLiteral("decimal(18,2)", new GridValue { Text = "1E5" }));
        Assert.Equal("-0.00250", ss.PreviewLiteral("numeric(10,5)", new GridValue { Text = "-2.50e-3" }));
        Assert.Equal("15", ss.PreviewLiteral("money", new GridValue { Text = "1.5E+1" }));
        Assert.Equal("0.5", ss.PreviewLiteral("smallmoney", new GridValue { Text = ".5e0" }));
        Assert.Equal("123456789012345678901234567890.12", ss.PreviewLiteral("decimal(38,2)", new GridValue { Text = "1.2345678901234567890123456789012E29" }));
        Assert.Equal("1E5", ss.PreviewLiteral("float", new GridValue { Text = "1E5" }));
        Assert.Equal("'1E5'", ss.PreviewLiteral("int", new GridValue { Text = "1E5" }));
        Assert.Equal("'1E999'", ss.PreviewLiteral("decimal(38,0)", new GridValue { Text = "1E999" }));
        Assert.Equal("'NaN'", ss.PreviewLiteral("decimal(18,2)", new GridValue { Text = "NaN" }));
        // The parameter the apply sends reads the same number.
        Assert.Equal(new System.Data.SqlTypes.SqlDecimal(100000m), SqlServerDataEdit.Convert(SqlServerDataEdit.TypeSpec.Parse("decimal(18,2)"), "1E5"));
        Assert.Equal(-0.25m, SqlServerDataEdit.Convert(SqlServerDataEdit.TypeSpec.Parse("money"), "-2.5e-1"));

        var pg = new PostgresDataEdit();
        Assert.Equal("NULL", pg.PreviewLiteral("integer", new GridValue { IsNull = true }));
        Assert.Equal("'it''s'", pg.PreviewLiteral("text", new GridValue { Text = "it's" }));
        // L7: an escape string, so \x reads the same whatever standard_conforming_strings is.
        Assert.Equal("E'\\\\x0aff'", pg.PreviewLiteral("bytea", new GridValue { Text = "0x0aff" }));

        // Same UTC instant in +02:00 and -04:00 compares equal.
        Assert.True(KeyComparers.ValuesEqual(ValueKind.DateTimeOffset, ValueKind.DateTimeOffset,
            "2026-10-21 12:00:00 +02:00", "2026-10-21 06:00:00 -04:00", O));
        Assert.True(KeyComparers.ValuesEqual(ValueKind.DateTimeOffset, ValueKind.DateTimeOffset,
            "2026-10-21 12:00:00.002 +02:00", "2026-10-21 06:00:00.000 -04:00", O with { TimeToleranceMs = 3 }));
    }

    [Theory]
    [InlineData("SELECT id FROM dbo.t;", "", "SELECT id FROM dbo.t")]
    [InlineData("WITH c AS (SELECT 1 AS id) SELECT id FROM c;", "WITH c AS (SELECT 1 AS id)\n", "SELECT id FROM c")]
    [InlineData("SELECT id FROM dbo.t ORDER BY id DESC", "", "SELECT id FROM dbo.t")]
    [InlineData("SELECT TOP (5) id FROM dbo.t ORDER BY id", "", "SELECT TOP (5) id FROM dbo.t ORDER BY id")]
    [InlineData("SELECT id FROM dbo.t ORDER BY id OFFSET 0 ROWS", "", "SELECT id FROM dbo.t ORDER BY id OFFSET 0 ROWS")]
    [InlineData("SELECT id FROM dbo.t -- trailing note", "", "SELECT id FROM dbo.t")]
    [InlineData("SELECT 1 AS id UNION ALL SELECT 2 ORDER BY id; /* done */", "", "SELECT 1 AS id UNION ALL SELECT 2")]
    public void Sql_server_custom_queries_become_valid_derived_tables(string sql, string prefix, string body)
    {
        var ss = new SqlServerDataCompare();
        Assert.Null(ss.QueryRefusal(sql));
        Assert.Equal((prefix, body), ss.DerivedQuery(sql));
        // What the service runs parses as one SELECT.
        var (p, b) = ss.DerivedQuery(sql);
        Assert.Null(ss.QueryRefusal($"{p}SELECT * FROM (\n{b}\n) AS q WHERE 1 = 0"));
    }

    [Theory]
    [InlineData("SELECT id FROM t;", "SELECT id FROM t")]
    [InlineData("WITH c AS (SELECT 1 AS id) SELECT id FROM c ORDER BY id;", "WITH c AS (SELECT 1 AS id) SELECT id FROM c ORDER BY id")]
    [InlineData("SELECT ';' AS semi -- note\n;", "SELECT ';' AS semi")]
    [InlineData("  VALUES (1, 'a') /* c */", "VALUES (1, 'a')")]
    public void Postgres_custom_queries_keep_with_and_order_by_and_lose_trailing_noise(string sql, string body)
    {
        var pg = new PostgresDataCompare();
        Assert.Null(pg.QueryRefusal(sql));
        Assert.Equal(("", body), pg.DerivedQuery(sql));
    }

    // ------------------------------------------------------------------ batch and kept-diff sizes (L21)

    /// <summary>A fake SQL Server with tables dbo.s and dbo.t (id int, v nvarchar) whose ordered reads return the rows given.</summary>
    static async Task<(Querithm.Host.Services Services, CapturingNotifier Notifier, string Sid)> FakeCompareAsync(IFrameSink frames, object?[][] source, object?[][] target)
    {
        var provider = new Fakes.FakeDbProvider { DataCompareImpl = new SqlServerDataCompare(), DataEditImpl = new SqlServerDataEdit() };
        provider.Handler = (cmd, ct) =>
        {
            if (!cmd.CommandText.Contains("ORDER BY")) return Task.FromResult(Fakes.FakeResult.None);
            var rows = cmd.CommandText.Contains("[s]") ? source : target;
            return Task.FromResult(new Fakes.FakeResult([Fakes.FakeResult.Table([("c0", typeof(string)), ("c1", typeof(string))], rows)]));
        };
        var notifier = new CapturingNotifier();
        var services = Querithm.Host.Services.Create(Querithm.Core.Store.LocalStore.InMemory(), frames, notifier,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new Querithm.Core.Sessions.ProviderRegistry([provider]));
        var profile = services.Profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        services.Metadata.Store.ApplyRefresh(profile.Id, "db", true,
            [new ObjectStamp(1, "dbo", "s", ObjectKind.Table, 1, null), new ObjectStamp(2, "dbo", "t", ObjectKind.Table, 1, null)], [],
            new MetadataBundle(
            [
                new ColumnRow(1, 1, "id", "int", null, null, null, false, false, false, null), new ColumnRow(1, 2, "v", "nvarchar", -1, null, null, true, false, false, null),
                new ColumnRow(2, 1, "id", "int", null, null, null, false, false, false, null), new ColumnRow(2, 2, "v", "nvarchar", -1, null, null, true, false, false, null),
            ], [], [], []), "16");
        services.Metadata.Cache.Invalidate(profile.Id, "db");
        var sid = services.Sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Secret = "x", Database = "db" }).SessionId;
        return (services, notifier, sid);
    }

    static async Task<(DataCompareStart Start, DataCompareProgressEvent Done)> CompareAsync(Querithm.Host.Services services, CapturingNotifier notifier, string sid)
    {
        var start = await services.DataCompare.StartAsync(new DataCompareParams
        {
            Source = new DataCompareSide { SessionId = sid, Database = "db", Schema = "dbo", Table = "s" },
            Target = new DataCompareSide { SessionId = sid, Database = "db", Schema = "dbo", Table = "t" },
            Key = ["id"],
        }, CancellationToken.None);
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        DataCompareProgressEvent? done;
        while ((done = notifier.Of<DataCompareProgressEvent>().FirstOrDefault(e => e.CompareId == start.CompareId && e.Done)) == null)
        {
            Assert.True(DateTime.UtcNow < end, "the compare did not finish");
            await Task.Delay(10);
        }
        Assert.Null(done.Error);
        return (start, done);
    }

    static object?[][] Rows(int count, int chars) => Enumerable.Range(1, count).Select(i => new object?[] { i.ToString(), new string('x', chars) + i }).ToArray();

    [Fact]
    public async Task Diff_batches_flush_at_4_MB_as_query_batches_do_not_only_at_1000_rows()
    {
        // Before, a batch was 1,000 diff rows whatever their size: 300 rows of 20,000-character values went out as one ~12 MB
        // frame, and long enough values reached the 256 MiB frame cap.
        var frames = new CapturingFrames();
        var (services, notifier, sid) = await FakeCompareAsync(frames, Rows(300, 20_000), []);
        await using var _ = services;
        var (start, done) = await CompareAsync(services, notifier, sid);
        Assert.Equal(300, done.SourceOnly);
        var batches = frames.For(start.ExecutionId).Where(f => f.Kind == FrameKind.Batch).ToList();
        Assert.True(batches.Count >= 3, $"{batches.Count} batch(es)");
        Assert.All(batches, b => Assert.True(b.Payload.Length < Querithm.Core.Execution.ResultSetEncoder.MaxBatchBytes + 100_000, $"{b.Payload.Length} bytes"));
        Assert.Equal(300, batches.Sum(b => TestHost.ReadBatch(b.Payload).Length));
    }

    [Fact]
    public async Task Kept_differences_stop_at_the_byte_budget_with_a_note_while_every_row_is_still_counted()
    {
        var frames = new CapturingFrames();
        var (services, notifier, sid) = await FakeCompareAsync(frames, Rows(2_000, 1_000), Rows(10, 5));
        await using var _ = services;
        services.DataCompare.KeptBytesLimit = 500_000;
        var (start, done) = await CompareAsync(services, notifier, sid);
        Assert.Equal(1_990 + 10, done.SourceOnly + done.Different);
        Assert.True(done.Truncated);
        Assert.InRange(done.Kept, 100, 400); // ~2.2 KB each
        Assert.Contains("0.5 MB one comparison may keep", done.TruncatedNote);
        Assert.Contains($"first {done.Kept:N0} differences", done.TruncatedNote);
        // The grid gets exactly what was kept, and the sync script says it covers only those.
        Assert.Equal(done.Kept, frames.For(start.ExecutionId).Where(f => f.Kind == FrameKind.Batch).Sum(b => TestHost.ReadBatch(b.Payload).Length));
        var sync = services.DataCompare.SyncSql(new DataSyncSqlParams { CompareId = start.CompareId, Insert = true, Update = true, Delete = false });
        Assert.Contains(sync.Notes, n => n.Contains($"first {done.Kept:N0} differences only"));
        Assert.Equal(done.Kept, sync.Rows);
    }

    [Fact]
    public async Task The_row_cap_note_names_keep()
    {
        var frames = new CapturingFrames();
        var (services, notifier, sid) = await FakeCompareAsync(frames, Rows(30, 3), []);
        await using var _ = services;
        var start = await services.DataCompare.StartAsync(new DataCompareParams
        {
            Source = new DataCompareSide { SessionId = sid, Database = "db", Schema = "dbo", Table = "s" },
            Target = new DataCompareSide { SessionId = sid, Database = "db", Schema = "dbo", Table = "t" },
            Key = ["id"], Options = new DataCompareOptions { MaxDiffRows = 10 },
        }, CancellationToken.None);
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        DataCompareProgressEvent? done;
        while ((done = notifier.Of<DataCompareProgressEvent>().FirstOrDefault(e => e.CompareId == start.CompareId && e.Done)) == null)
        {
            Assert.True(DateTime.UtcNow < end, "the compare did not finish");
            await Task.Delay(10);
        }
        Assert.True(done.Truncated);
        Assert.Equal(10, done.Kept);
        Assert.Equal("Showing the first 10 differences (Keep): raise Keep to see more.", done.TruncatedNote);
        Assert.Equal(30, done.SourceOnly);
    }

    // ------------------------------------------------------------- a snapshot older than the table's DDL (Oracle ORA-01466)

    sealed class SnapshotError : System.Data.Common.DbException { public SnapshotError() : base("ORA-01466") { } }

    static Task<System.Data.Common.DbDataReader> Reader() => Task.FromResult<System.Data.Common.DbDataReader>(new System.Data.DataTable().CreateDataReader());

    [Fact]
    public async Task A_select_refused_for_its_snapshot_runs_once_more_in_a_new_one()
    {
        var (runs, snapshots) = (0, 0);
        var r = await DataCompareService.ExecuteWithSnapshotRetryAsync(
            _ => ++runs == 1 ? throw new SnapshotError() : Reader(), ex => ex is SnapshotError, TimeSpan.FromMilliseconds(10),
            _ => { snapshots++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal((2, 1), (runs, snapshots));
    }

    [Fact]
    public async Task Other_errors_and_a_second_refusal_are_the_compares_error()
    {
        var snapshots = 0;
        Task NewSnapshot(CancellationToken _) { snapshots++; return Task.CompletedTask; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => DataCompareService.ExecuteWithSnapshotRetryAsync(
            _ => throw new InvalidOperationException("denied"), ex => ex is SnapshotError, TimeSpan.Zero, NewSnapshot, CancellationToken.None));
        Assert.Equal(0, snapshots);
        var runs = 0;
        await Assert.ThrowsAsync<SnapshotError>(() => DataCompareService.ExecuteWithSnapshotRetryAsync(
            _ => { runs++; throw new SnapshotError(); }, ex => ex is SnapshotError, TimeSpan.Zero, NewSnapshot, CancellationToken.None));
        Assert.Equal((2, 1), (runs, snapshots)); // one retry, never a loop
    }

    [Fact]
    public async Task A_cancel_during_the_wait_ends_the_read()
    {
        using var cts = new CancellationTokenSource();
        var runs = 0;
        var t = DataCompareService.ExecuteWithSnapshotRetryAsync(
            _ => { runs++; throw new SnapshotError(); }, ex => ex is SnapshotError, TimeSpan.FromMinutes(5), _ => Task.CompletedTask, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, runs);
    }
}

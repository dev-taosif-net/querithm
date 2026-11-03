using Apache.Arrow;

namespace Querithm.Tests.FileEngines;

/// <summary>M11 on the file engines (no Docker): which DuckDB columns travel as numeric text, and SQLite's dynamic columns.</summary>
public sealed class NumericTextFileEngineTests
{
    static string? Numeric(RecordBatch b, string column) => b.Schema.GetFieldByName(column).Metadata.GetValueOrDefault("q:numeric");

    [Fact]
    public async Task Duckdb_numbers_sent_as_text_are_marked_numeric()
    {
        await using var h = new DuckDbHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (done, batches, _) = await h.RunAsync(sid, """
            SELECT 170141183460469231731687303715884105727::HUGEINT AS h, 340282366920938463463374607431768211455::UHUGEINT AS uh,
                   18446744073709551615::UBIGINT AS ub, 65535::USMALLINT AS us, 1.50::DECIMAL(10,2) AS d,
                   12345678901234567890.123456789012345678::DECIMAL(38,18) AS d38, 7::INTEGER AS i, 2::BIGINT AS bi, 0.5::DOUBLE AS f, 'x' AS t
            """);
        Assert.Null(done.Error);
        var b = Assert.Single(batches);
        Assert.All(["h", "uh", "ub", "us", "d", "d38"], c =>
        {
            Assert.IsType<StringArray>(b.Column(c));
            Assert.Equal("1", Numeric(b, c));
        });
        Assert.Equal("18446744073709551615", ((StringArray)b.Column("ub")).GetString(0));
        Assert.All(["i", "bi", "f", "t"], c => Assert.Null(Numeric(b, c)));
    }

    [Fact]
    public async Task Sqlite_columns_are_dynamic_and_never_marked_numeric()
    {
        await using var h = new SqliteHost();
        var (_, sid) = await h.OpenMemoryAsync();
        var (done, batches, _) = await h.RunAsync(sid, "SELECT 9223372036854775807 AS big, 1.5 AS r, 'x' AS t");
        Assert.Null(done.Error);
        var b = Assert.Single(batches);
        Assert.All(["big", "r", "t"], c =>
        {
            Assert.Equal("1", b.Schema.GetFieldByName(c).Metadata["q:dynamic"]);
            Assert.Null(Numeric(b, c));
        });
    }
}

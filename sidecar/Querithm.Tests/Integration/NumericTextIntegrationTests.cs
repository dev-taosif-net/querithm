using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// M11 against the Docker fixture (QUERITHM_IT=1): the numbers each server engine sends as exact text are marked
/// <c>q:numeric</c>; typed numbers (Decimal128, Int64, doubles) are not.
/// </summary>
public sealed class NumericTextIntegrationTests
{
    static string? Numeric(RecordBatch b, string column) => b.Schema.GetFieldByName(column).Metadata.GetValueOrDefault("q:numeric");

    [IntegrationFact]
    public async Task SqlServer_numbers_are_all_typed_so_none_is_marked()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var (done, batches, _) = await h.RunAsync(sid, "SELECT CAST(1 AS decimal(38,2)) AS d, CAST(1 AS bigint) AS b, CAST(1 AS money) AS m, CAST(1.5 AS float) AS f, CAST(1 AS sql_variant) AS v");
        Assert.Equal(ExecState.Done, done.State);
        var b = Assert.Single(batches);
        Assert.IsType<Decimal128Array>(b.Column("d"));
        Assert.All(["d", "b", "m", "f", "v"], c => Assert.Null(Numeric(b, c)));
    }

    [IntegrationFact]
    public async Task Postgres_unconstrained_and_wide_numeric_are_marked()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        var (done, batches, _) = await h.RunAsync(sid,
            "SELECT 10::numeric AS n, 'NaN'::numeric AS nan, 123456789012345678901234567890123456789012::numeric(45,0) AS wide, 1.5::numeric(10,2) AS d, 1::bigint AS b, 2.5::float8 AS f, '1 mon 2 days'::interval(3) AS iv");
        // The reader names a type with its modifiers ("numeric(45)", "interval(3)"): the provider reads such columns by OID
        // (2026-11-03: the wide numeric was read as System.Decimal and the run failed with "does not fit").
        Assert.Null(done.Error?.Message);
        Assert.Equal(ExecState.Done, done.State);
        var b = Assert.Single(batches);
        Assert.All(["n", "nan", "wide"], c => Assert.Equal("1", Numeric(b, c)));
        Assert.Equal("NaN", ((StringArray)b.Column("nan")).GetString(0));
        Assert.Equal("123456789012345678901234567890123456789012", ((StringArray)b.Column("wide")).GetString(0));
        Assert.Equal("1 mon 2 days", ((StringArray)b.Column("iv")).GetString(0));
        Assert.IsType<Decimal128Array>(b.Column("d"));
        Assert.All(["d", "b", "f"], c => Assert.Null(Numeric(b, c)));
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task MySql_unsigned_bigint_and_wide_decimal_are_marked(string product)
    {
        var (h, _, sid) = await MySqlIntegrationTests.OpenAsync(product);
        await using var _ = h;
        var (done, batches, _) = await MySqlIntegrationTests.Run(h, sid,
            "SELECT CAST(18446744073709551615 AS UNSIGNED) AS u, CAST(1 AS DECIMAL(65,30)) AS wide, CAST(1.5 AS DECIMAL(10,2)) AS d, CAST(1 AS SIGNED) AS s");
        Assert.Null(done.Error);
        var b = Assert.Single(batches);
        Assert.Equal("1", Numeric(b, "u"));
        Assert.Equal("18446744073709551615", ((StringArray)b.Column("u")).GetString(0));
        Assert.Equal("1", Numeric(b, "wide"));
        Assert.All(["d", "s"], c => Assert.Null(Numeric(b, c)));
    }
}

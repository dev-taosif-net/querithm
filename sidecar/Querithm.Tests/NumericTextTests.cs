using System.Data;
using System.Numerics;
using Apache.Arrow;
using Querithm.Contract.Providers;
using Querithm.Core.Execution;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// M11: a number the sidecar sends as exact text (no Arrow type holds it: beyond Decimal128 or Int64) carries field
/// metadata <c>q:numeric</c>, so the webview aligns and sorts it as a number; typed numbers and real text never do.
/// </summary>
public class NumericTextTests
{
    static Schema SchemaOf(DataTable t, IDbProvider? provider = null) =>
        new ResultSetEncoder(t.CreateDataReader(), provider ?? new SqlServerProvider(), new TruncatedCellStore(), 0).Schema;

    static string? Numeric(Schema s, string column) => s.GetFieldByName(column).Metadata.GetValueOrDefault("q:numeric");

    [Fact]
    public void Numbers_sent_as_text_are_marked_numeric()
    {
        var t = new DataTable();
        t.Columns.Add("dec", typeof(decimal));         // no declared precision (PostgreSQL numeric, DuckDB DECIMAL): exact text
        t.Columns.Add("u64", typeof(ulong));           // MySQL BIGINT UNSIGNED, DuckDB UBIGINT
        t.Columns.Add("u16", typeof(ushort));          // MySQL SMALLINT UNSIGNED, DuckDB USMALLINT
        t.Columns.Add("huge", typeof(BigInteger));     // DuckDB HUGEINT
        t.Columns.Add("i128", typeof(Int128));
        t.Columns.Add("i64", typeof(long));
        t.Columns.Add("dbl", typeof(double));
        t.Columns.Add("txt", typeof(string));
        var s = SchemaOf(t);
        Assert.All(["dec", "u64", "u16", "huge", "i128"], c =>
        {
            Assert.IsType<Apache.Arrow.Types.StringType>(s.GetFieldByName(c).DataType);
            Assert.Equal("1", Numeric(s, c));
        });
        Assert.All(["i64", "dbl", "txt"], c => Assert.Null(Numeric(s, c)));
    }

    [Fact]
    public void A_dynamically_typed_column_keeps_its_per_cell_classes_instead()
    {
        // SQLite (Phase D0f): the class sibling says which cells are numbers; the column itself is not marked.
        var t = new DataTable();
        t.Columns.Add("v", typeof(decimal));
        var s = SchemaOf(t, new FakeDbProvider { Hint = _ => ColumnHint.Dynamic });
        Assert.Equal("1", s.GetFieldByName("v").Metadata["q:dynamic"]);
        Assert.Null(Numeric(s, "v"));
    }

    [Fact]
    public void A_text_column_a_provider_reads_itself_is_not_a_number()
    {
        // sql_variant, geography, intervals and the like reach the grid as the provider's text; only their CLR type is a number's.
        var t = new DataTable();
        t.Columns.Add("variant", typeof(object));
        Assert.Null(Numeric(SchemaOf(t, new FakeDbProvider { Hint = _ => ColumnHint.ProviderString }), "variant"));
    }
}

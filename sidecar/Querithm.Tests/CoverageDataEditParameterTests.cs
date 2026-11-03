using System.Data;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;
using Querithm.Contract;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Grid edits bind typed parameters from the catalog type, never SQL text; each engine maps its own type names.</summary>
public class DataEditParameterTests
{
    static SqlParameter Ms(string type, string text)
    {
        using var cmd = new SqlCommand();
        new SqlServerDataEdit().AddValue(cmd, 3, type, new GridValue { Text = text });
        return Assert.Single(cmd.Parameters.Cast<SqlParameter>());
    }

    [Theory]
    [InlineData("bigint", "5", SqlDbType.BigInt)]
    [InlineData("int", "5", SqlDbType.Int)]
    [InlineData("smallint", "5", SqlDbType.SmallInt)]
    [InlineData("tinyint", "5", SqlDbType.TinyInt)]
    [InlineData("bit", "1", SqlDbType.Bit)]
    [InlineData("money", "1.5", SqlDbType.Money)]
    [InlineData("smallmoney", "1.5", SqlDbType.SmallMoney)]
    [InlineData("float", "1.5", SqlDbType.Float)]
    [InlineData("float(24)", "1.5", SqlDbType.Real)]
    [InlineData("real", "1.5", SqlDbType.Real)]
    [InlineData("date", "2026-01-02", SqlDbType.Date)]
    [InlineData("datetime", "2026-01-02 03:04:05", SqlDbType.DateTime)]
    [InlineData("smalldatetime", "2026-01-02 03:04:00", SqlDbType.SmallDateTime)]
    [InlineData("uniqueidentifier", "8c4a2d0b-1f2e-4c3d-9a1b-0123456789ab", SqlDbType.UniqueIdentifier)]
    [InlineData("image", "0x01", SqlDbType.Image)]
    [InlineData("timestamp", "0x0000000000000001", SqlDbType.Binary)]
    [InlineData("rowversion", "0x0000000000000001", SqlDbType.Binary)]
    [InlineData("text", "abc", SqlDbType.Text)]
    [InlineData("ntext", "abc", SqlDbType.NText)]
    [InlineData("xml", "<a/>", SqlDbType.Xml)]
    [InlineData("hierarchyid", "/1/", SqlDbType.NVarChar)]
    public void SqlServer_parameter_type_follows_the_column_type(string type, string text, SqlDbType expected)
    {
        var p = Ms(type, text);
        Assert.Equal(expected, p.SqlDbType);
        Assert.Equal("@p3", p.ParameterName);
        Assert.NotEqual(DBNull.Value, p.Value);
    }

    [Fact]
    public void SqlServer_sized_types_carry_precision_scale_and_length()
    {
        var dec = Ms("decimal(12,4)", "1.5");
        Assert.Equal((SqlDbType.Decimal, (byte)12, (byte)4), (dec.SqlDbType, dec.Precision, dec.Scale));
        Assert.Equal(SqlDbType.Decimal, Ms("numeric", "1").SqlDbType);
        Assert.Equal((byte)3, Ms("datetime2(3)", "2026-01-02 03:04:05.123").Scale);
        Assert.Equal((byte)7, Ms("datetime2", "2026-01-02 03:04:05").Scale);
        Assert.Equal((SqlDbType.DateTimeOffset, (byte)2), (Ms("datetimeoffset(2)", "2026-01-02 03:04:05 +02:00").SqlDbType, Ms("datetimeoffset(2)", "2026-01-02 03:04:05 +02:00").Scale));
        Assert.Equal((SqlDbType.Time, (byte)5), (Ms("time(5)", "03:04:05").SqlDbType, Ms("time(5)", "03:04:05").Scale));
        Assert.Equal(4, Ms("binary(4)", "0x01020304").Size);
        Assert.Equal(-1, Ms("varbinary(max)", "0x01").Size);
        Assert.Equal(16, Ms("varbinary(16)", "0x01").Size);
        Assert.Equal(8, Ms("timestamp", "0x0000000000000001").Size);
        Assert.Equal((SqlDbType.Char, 3), (Ms("char(3)", "abc").SqlDbType, Ms("char(3)", "abc").Size));
        Assert.Equal((SqlDbType.NChar, 3), (Ms("nchar(3)", "abc").SqlDbType, Ms("nchar(3)", "abc").Size));
        Assert.Equal(-1, Ms("varchar(max)", "abc").Size);
        Assert.Equal(20, Ms("varchar(20)", "abc").Size);
        Assert.Equal(-1, Ms("nvarchar(max)", "abc").Size);
        Assert.Equal(-1, Ms("sql_variant", "abc").Size);
    }

    [Fact]
    public void SqlServer_null_and_invalid_values()
    {
        using var cmd = new SqlCommand();
        new SqlServerDataEdit().AddValue(cmd, 0, "int", new GridValue { IsNull = true });
        Assert.Equal(DBNull.Value, cmd.Parameters[0].Value);
        var ex = Assert.Throws<FormatException>(() => Ms("int", "twelve"));
        Assert.Equal("'twelve' is not a valid int value.", ex.Message);
        Assert.Throws<FormatException>(() => Ms("varbinary(max)", "0x0102…"));
    }

    [Theory]
    [InlineData("integer", "5", "5")]
    [InlineData("text", "abc", "abc")]
    [InlineData("bytea", "0xDEADBEEF", "\\xDEADBEEF")]
    [InlineData("bytea", "\\xDEADBEEF", "\\xDEADBEEF")]
    [InlineData(null, "free", "free")]
    public void Postgres_values_are_untyped_text_so_the_server_resolves_the_type(string? type, string text, string sent)
    {
        using var cmd = new NpgsqlCommand();
        new PostgresDataEdit().AddValue(cmd, 1, type, new GridValue { Text = text });
        var p = Assert.Single(cmd.Parameters.Cast<NpgsqlParameter>());
        Assert.Equal(NpgsqlDbType.Unknown, p.NpgsqlDbType);
        Assert.Equal(sent, p.Value);
    }

    [Fact]
    public void Postgres_null_and_shortened_binary()
    {
        using var cmd = new NpgsqlCommand();
        new PostgresDataEdit().AddValue(cmd, 1, "text", new GridValue { IsNull = true });
        Assert.Equal(DBNull.Value, cmd.Parameters[0].Value);
        Assert.Throws<FormatException>(() => new PostgresDataEdit().AddValue(new NpgsqlCommand(), 2, "bytea", new GridValue { Text = "0x0102…" }));
    }

    [Fact]
    public void Placeholders_and_quoting_differ_per_engine()
    {
        var ms = new SqlServerDataEdit();
        var pg = new PostgresDataEdit();
        Assert.Equal("@p4", ms.Placeholder(4));
        Assert.Equal("@p4", ms.TypedPlaceholder(4, "int"));
        Assert.Equal("[a]]b]", ms.Quote("a]b"));
        Assert.Equal("\"a\"\"b\"", pg.Quote("a\"b"));
        Assert.Equal(("rowversion", "rv"), ms.Token(new SnapObject { Id = 1, Schema = "dbo", Name = "t", Kind = ObjectKind.Table, Columns = [new SnapColumn { Name = "rv", Type = "timestamp" }] }));
        Assert.Equal((null, null), ms.Token(new SnapObject { Id = 1, Schema = "dbo", Name = "t", Kind = ObjectKind.Table, Columns = [new SnapColumn { Name = "a", Type = "int" }] }));
        Assert.Equal(("xmin", "xmin"), pg.Token(new SnapObject { Id = 1, Schema = "public", Name = "t", Kind = ObjectKind.Table, Columns = [] }));
    }

    [Theory]
    [InlineData("timestamp", true)]
    [InlineData("rowversion", true)]
    [InlineData("geography", true)]
    [InlineData("hierarchyid", true)]
    [InlineData("sql_variant", true)]
    [InlineData("nvarchar(20)", false)]
    public void SqlServer_locks_columns_the_grid_cannot_write(string type, bool locked)
    {
        var reason = new SqlServerDataEdit().LockReason(new SnapColumn { Name = "c", Type = type });
        Assert.Equal(locked, reason != null);
        if (locked) Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("datetime", "CONVERT(varchar(23), x, 121)")]
    [InlineData("datetime2(3)", "CONVERT(varchar(40), x, 121)")]
    [InlineData("varbinary(max)", "CONVERT(varchar(max), CAST(x AS varbinary(max)), 1)")]
    [InlineData("float", "CONVERT(varchar(30), x, 3)")]
    [InlineData("geography", "x.ToString()")]
    [InlineData("text", "CAST(x AS varchar(max))")]
    [InlineData("int", "CAST(x AS nvarchar(max))")]
    public void SqlServer_reads_values_back_as_the_same_text_the_grid_shows(string type, string expected) =>
        Assert.Equal(expected, new SqlServerDataEdit().ValueText("x", type));
}

using System.Data;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;
using Querithm.Contract;
using Querithm.Core.Execution;
using Querithm.Core.Logging;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Parameter binding per engine (never string substitution) and the log scrubber's secret shapes.</summary>
public class ParameterBindingTests
{
    static SqlParameter MssqlParam(QueryParameter p)
    {
        using var cmd = new SqlCommand();
        new SqlServerProvider().AddParameter(cmd, p);
        return Assert.Single(cmd.Parameters.Cast<SqlParameter>());
    }

    [Theory]
    [InlineData("int", SqlDbType.Int, 0)]
    [InlineData("nvarchar(50)", SqlDbType.NVarChar, 50)]
    [InlineData("VarChar ( 20 )", SqlDbType.VarChar, 20)]
    [InlineData("nvarchar(max)", SqlDbType.NVarChar, -1)]
    [InlineData("varbinary(max)", SqlDbType.VarBinary, -1)]
    [InlineData("small int", SqlDbType.SmallInt, 0)]
    [InlineData("datetime2", SqlDbType.DateTime2, 0)]
    public void SqlServer_parameter_types_and_sizes_come_from_the_declared_type(string type, SqlDbType expected, int size)
    {
        var prm = MssqlParam(new QueryParameter { Name = "p", Type = type, Value = "1" });
        Assert.Equal(expected, prm.SqlDbType);
        if (size != 0) Assert.Equal(size, prm.Size);
        Assert.Equal("@p", prm.ParameterName);
        Assert.Equal("1", prm.Value);
    }

    [Fact]
    public void SqlServer_decimal_parameter_carries_precision_and_scale()
    {
        var prm = MssqlParam(new QueryParameter { Name = "@d", Type = "decimal(12, 3)", Value = "1.5" });
        Assert.Equal(SqlDbType.Decimal, prm.SqlDbType);
        Assert.Equal(12, prm.Precision);
        Assert.Equal(3, prm.Scale);
        Assert.Equal("@d", prm.ParameterName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not_a_real_type")]
    [InlineData("int(((")]
    public void SqlServer_unknown_or_missing_type_falls_back_to_nvarchar(string? type)
    {
        var prm = MssqlParam(new QueryParameter { Name = "x", Type = type, Value = "v" });
        Assert.Equal(SqlDbType.NVarChar, prm.SqlDbType);
    }

    [Fact]
    public void SqlServer_null_value_binds_DBNull()
    {
        Assert.Equal(DBNull.Value, MssqlParam(new QueryParameter { Name = "x", Type = "int", IsNull = true, Value = "5" }).Value);
        Assert.Equal(DBNull.Value, MssqlParam(new QueryParameter { Name = "x", Type = "int", Value = null }).Value);
    }

    [Theory]
    [InlineData(":name", "name")]
    [InlineData("@name", "name")]
    [InlineData("name", "name")]
    public void Postgres_named_parameters_are_untyped_text_and_strip_sigils(string given, string bound)
    {
        using var cmd = new NpgsqlCommand();
        new PostgresProvider().AddParameter(cmd, new QueryParameter { Name = given, Value = "42" });
        var prm = Assert.Single(cmd.Parameters.Cast<NpgsqlParameter>());
        Assert.Equal(bound, prm.ParameterName);
        Assert.Equal(NpgsqlDbType.Unknown, prm.NpgsqlDbType);
        Assert.Equal("42", prm.Value);
    }

    [Fact]
    public void Postgres_positional_parameter_has_no_name_and_null_binds_DBNull()
    {
        using var cmd = new NpgsqlCommand();
        new PostgresProvider().AddParameter(cmd, new QueryParameter { Name = "$1", IsNull = true });
        var prm = Assert.Single(cmd.Parameters.Cast<NpgsqlParameter>());
        Assert.True(string.IsNullOrEmpty(prm.ParameterName) || !prm.ParameterName.Contains('$'));
        Assert.Equal(DBNull.Value, prm.Value);
    }
}

public class LogScrubberShapesTests
{
    [Theory]
    [InlineData("Server=x;Password=\"p;w d\";Trusted=1", "p;w d")]
    [InlineData("Server=x;pwd='single quoted';A=1", "single quoted")]
    [InlineData("Server=x;Password={br;ace};A=1", "br;ace")]
    [InlineData("Server=x;PASSWORD = bare123;A=1", "bare123")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("authorization : Basic dXNlcjpwdw==", "dXNlcjpwdw==")]
    [InlineData("Authorization: rawtoken999", "rawtoken999")]
    [InlineData("{\"token\": \"a\\\"b-secret\"}", "b-secret")]
    [InlineData("{\"accessToken\":\"tok-123\", \"x\": 1}", "tok-123")]
    [InlineData("{\"Secret\" : \"s3cr3t\"}", "s3cr3t")]
    [InlineData("Access Token=eyJhbGciOi; Other=1", "eyJhbGciOi")]
    [InlineData("accesstoken=zzz999", "zzz999")]
    public void Secret_shapes_are_masked(string input, string secret)
    {
        var scrubbed = LogScrubber.Scrub(input);
        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("***", scrubbed);
    }

    [Fact]
    public void Text_without_secrets_and_empty_input_pass_through()
    {
        Assert.Equal("", LogScrubber.Scrub(null));
        Assert.Equal("", LogScrubber.Scrub(""));
        Assert.Equal("SELECT 1 FROM passwords", LogScrubber.Scrub("SELECT 1 FROM passwords"));
    }

    [Fact]
    public void Registered_secrets_are_masked_wherever_they_appear_and_short_ones_are_ignored()
    {
        LogScrubber.RegisterSecret("zq-unique-secret-77");
        LogScrubber.RegisterSecret("ab");
        LogScrubber.RegisterSecret(null);
        Assert.Equal("x *** y ***", LogScrubber.Scrub("x zq-unique-secret-77 y zq-unique-secret-77"));
        Assert.Equal("ab cd", LogScrubber.Scrub("ab cd"));
    }
}

public class StatisticsParserShapesTests
{
    [Fact]
    public void Io_line_parses_every_counter()
    {
        var s = StatisticsParser.Parse("Table 'Orders'. Scan count 3, logical reads 40, physical reads 2, read-ahead reads 7, lob logical reads 9.", 2)!;
        Assert.Equal("io", s.Kind);
        Assert.Equal("Orders", s.Table);
        Assert.Equal(2, s.BatchIndex);
        Assert.Equal(3, s.ScanCount);
        Assert.Equal(40, s.LogicalReads);
        Assert.Equal(2, s.PhysicalReads);
        Assert.Equal(7, s.ReadAheadReads);
        Assert.Equal(9, s.LobLogicalReads);
    }

    [Fact]
    public void Io_line_with_missing_counters_leaves_them_null()
    {
        var s = StatisticsParser.Parse("Table 'T'. Scan count 1, logical reads 5.", 0)!;
        Assert.Equal(5, s.LogicalReads);
        Assert.Null(s.PhysicalReads);
        Assert.Null(s.LobLogicalReads);
    }

    [Theory]
    [InlineData("SQL Server Execution Times:\n   CPU time = 15 ms,  elapsed time = 20 ms.", "time", 15, 20)]
    [InlineData("SQL Server parse and compile time: \n   CPU time = 3 ms, elapsed time = 4 ms.", "compile", 3, 4)]
    public void Time_lines_distinguish_compile_from_execution(string msg, string kind, double cpu, double elapsed)
    {
        var s = StatisticsParser.Parse(msg, 1)!;
        Assert.Equal(kind, s.Kind);
        Assert.Equal(cpu, s.CpuMs);
        Assert.Equal(elapsed, s.ElapsedMs);
    }

    [Fact]
    public void Unrelated_messages_are_not_statistics() => Assert.Null(StatisticsParser.Parse("(1 row affected)", 0));
}

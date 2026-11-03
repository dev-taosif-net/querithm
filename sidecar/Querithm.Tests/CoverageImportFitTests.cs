using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Import compatibility verdicts, per engine: what fits, what needs a wider type, what rejects rows.</summary>
public class ImportFitMatrixTests
{
    static IImportDialect D(string engine) => engine == "pg" ? new PostgresImport() : new SqlServerImport();

    static ImportColumnProfile P(ImportKind kind, int maxLen = 5, int precision = 0, int scale = 0, string? min = null, string? max = null) =>
        new() { Name = "c", Kind = kind, MaxLength = maxLen, Precision = precision, Scale = scale, Min = min, Max = max };

    static SnapColumn T(string type, bool nullable = true) => new() { Name = "c", Type = type, Nullable = nullable };

    // engine, kind, precision, scale, min, max, target, verdict, widenTo (null = don't care)
    [Theory]
    // --- whole numbers
    [InlineData("mssql", ImportKind.Int32, 0, 0, "0", "200", "tinyint", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Int32, 0, 0, "0", "300", "tinyint", ImportVerdictKind.Widen, "smallint")]
    [InlineData("mssql", ImportKind.Int64, 0, 0, "0", "5000000000", "int", ImportVerdictKind.Widen, "bigint")]
    [InlineData("mssql", ImportKind.Int64, 0, 0, "0", "99999999999999999999", "bigint", ImportVerdictKind.Widen, "decimal(38,0)")]
    [InlineData("mssql", ImportKind.Decimal, 12, 0, null, null, "int", ImportVerdictKind.Widen, "decimal(12,0)")]
    [InlineData("mssql", ImportKind.Decimal, 12, 2, null, null, "int", ImportVerdictKind.Mismatch, null)]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "smallint", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.Int32, 0, 0, "0", "70000", "smallint", ImportVerdictKind.Widen, "bigint")]
    [InlineData("pg", ImportKind.Int64, 0, 0, "0", "5000000000", "integer", ImportVerdictKind.Widen, "bigint")]
    [InlineData("pg", ImportKind.Int64, 0, 0, "0", "99999999999999999999", "bigint", ImportVerdictKind.Widen, "numeric")]
    [InlineData("pg", ImportKind.Decimal, 12, 0, null, null, "integer", ImportVerdictKind.Widen, "numeric(12,0)")]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "integer", ImportVerdictKind.Mismatch, null)]
    // --- decimals
    [InlineData("mssql", ImportKind.Decimal, 6, 2, null, null, "decimal(10,2)", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Decimal, 12, 4, null, null, "numeric(10,2)", ImportVerdictKind.Widen, "decimal(12,4)")]
    [InlineData("mssql", ImportKind.Int32, 0, 0, "1", "123456", "decimal(4,0)", ImportVerdictKind.Widen, "decimal(6,0)")]
    [InlineData("mssql", ImportKind.Int32, 0, 0, "1", "9", "money", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Decimal, 20, 4, null, null, "smallmoney", ImportVerdictKind.Widen, null)]
    [InlineData("mssql", ImportKind.Double, 0, 0, null, null, "decimal(10,2)", ImportVerdictKind.Widen, "float")]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "decimal(10,2)", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.Decimal, 6, 2, null, null, "numeric(10,2)", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Decimal, 12, 4, null, null, "numeric(10,2)", ImportVerdictKind.Widen, "numeric(12,4)")]
    [InlineData("pg", ImportKind.Int32, 0, 0, "1", "9", "numeric", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Double, 0, 0, null, null, "numeric(10,2)", ImportVerdictKind.Widen, "numeric")]
    [InlineData("pg", ImportKind.Double, 0, 0, null, null, "numeric", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "numeric(10,2)", ImportVerdictKind.Mismatch, null)]
    // --- floats
    [InlineData("mssql", ImportKind.Decimal, 5, 2, null, null, "float", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "real", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.Double, 0, 0, null, null, "double precision", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Date, 0, 0, null, null, "real", ImportVerdictKind.Mismatch, null)]
    // --- booleans
    [InlineData("mssql", ImportKind.Bool, 0, 0, null, null, "bit", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "bit", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.Bool, 0, 0, null, null, "boolean", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "bool", ImportVerdictKind.Mismatch, null)]
    // --- dates and times
    [InlineData("mssql", ImportKind.Date, 0, 0, null, null, "date", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.DateTime, 3, 0, null, null, "date", ImportVerdictKind.Widen, "datetime2(3)")]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "date", ImportVerdictKind.Mismatch, null)]
    [InlineData("mssql", ImportKind.DateTime, 3, 0, null, null, "datetime", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.DateTime, 6, 0, null, null, "datetime", ImportVerdictKind.Widen, "datetime2(6)")]
    [InlineData("mssql", ImportKind.DateTime, 1, 0, null, null, "smalldatetime", ImportVerdictKind.Widen, "datetime2(1)")]
    [InlineData("mssql", ImportKind.DateTime, 5, 0, null, null, "datetime2(7)", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.DateTimeOffset, 3, 0, null, null, "datetime2", ImportVerdictKind.Mismatch, null)]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "datetime2", ImportVerdictKind.Mismatch, null)]
    [InlineData("mssql", ImportKind.Date, 0, 0, null, null, "datetimeoffset", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "datetimeoffset", ImportVerdictKind.Mismatch, null)]
    [InlineData("mssql", ImportKind.DateTimeOffset, 3, 0, null, null, "datetimeoffset(7)", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.DateTimeOffset, 7, 0, null, null, "datetimeoffset(2)", ImportVerdictKind.Widen, "datetimeoffset(7)")]
    [InlineData("mssql", ImportKind.Time, 3, 0, null, null, "time(7)", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Time, 7, 0, null, null, "time(2)", ImportVerdictKind.Widen, "time(7)")]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "time", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.Date, 0, 0, null, null, "date", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.DateTime, 3, 0, null, null, "date", ImportVerdictKind.Widen, "timestamp")]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "date", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.DateTime, 3, 0, null, null, "timestamp", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.DateTime, 7, 0, null, null, "timestamp without time zone", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.DateTimeOffset, 3, 0, null, null, "timestamp", ImportVerdictKind.Widen, "timestamptz")]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "timestamp", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.Date, 0, 0, null, null, "timestamptz", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.DateTimeOffset, 3, 0, null, null, "timestamp with time zone", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "timestamptz", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.Time, 3, 0, null, null, "time", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "time", ImportVerdictKind.Mismatch, null)]
    // --- uuid
    [InlineData("mssql", ImportKind.Uuid, 0, 0, null, null, "uniqueidentifier", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "uniqueidentifier", ImportVerdictKind.Mismatch, null)]
    [InlineData("pg", ImportKind.Uuid, 0, 0, null, null, "uuid", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "uuid", ImportVerdictKind.Mismatch, null)]
    // --- text-ish and pass-through
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "xml", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "ntext", ImportVerdictKind.Fits, null)]
    [InlineData("mssql", ImportKind.Text, 0, 0, null, null, "hierarchyid", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "jsonb", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "inet", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Text, 0, 0, null, null, "varchar", ImportVerdictKind.Fits, null)]
    // --- all NULL
    [InlineData("mssql", ImportKind.Empty, 0, 0, null, null, "int", ImportVerdictKind.Fits, null)]
    [InlineData("pg", ImportKind.Empty, 0, 0, null, null, "integer", ImportVerdictKind.Fits, null)]
    public void Verdicts(string engine, ImportKind kind, int precision, int scale, string? min, string? max, string target, ImportVerdictKind verdict, string? widen)
    {
        var fit = D(engine).Check(P(kind, precision: precision, scale: scale, min: min, max: max), T(target));
        Assert.Equal(verdict, fit.Kind);
        Assert.False(string.IsNullOrEmpty(fit.Message));
        if (widen != null) Assert.Equal(widen, fit.WidenTo);
    }

    [Theory]
    [InlineData("mssql", "char(5)", 12, ImportVerdictKind.Widen, "varchar(50)")]
    [InlineData("mssql", "nchar(5)", 12, ImportVerdictKind.Widen, "nvarchar(50)")]
    [InlineData("mssql", "nvarchar(5)", 12, ImportVerdictKind.Widen, "nvarchar(50)")]
    [InlineData("mssql", "varchar(5)", 5000, ImportVerdictKind.Widen, "varchar(8000)")]
    [InlineData("mssql", "varchar(50)", 12, ImportVerdictKind.Fits, null)]
    [InlineData("pg", "char(5)", 12, ImportVerdictKind.Widen, "varchar(12)")]
    [InlineData("pg", "character(5)", 12, ImportVerdictKind.Widen, "varchar(12)")]
    [InlineData("pg", "varchar(50)", 12, ImportVerdictKind.Fits, null)]
    public void Fixed_width_text_columns(string engine, string target, int maxLen, ImportVerdictKind verdict, string? widen)
    {
        var fit = D(engine).Check(P(ImportKind.Text, maxLen), T(target));
        Assert.Equal(verdict, fit.Kind);
        if (widen != null) Assert.Equal(widen, fit.WidenTo);
    }

    [Theory]
    [InlineData(ImportKind.Bool, "bit", "boolean")]
    [InlineData(ImportKind.Int32, "int", "integer")]
    [InlineData(ImportKind.Int64, "bigint", "bigint")]
    [InlineData(ImportKind.Double, "float", "double precision")]
    [InlineData(ImportKind.Date, "date", "date")]
    [InlineData(ImportKind.Uuid, "uniqueidentifier", "uuid")]
    public void New_table_types_per_engine(ImportKind kind, string mssql, string pg)
    {
        Assert.Equal(mssql, D("mssql").TypeFor(P(kind, precision: 3, scale: 1)));
        Assert.Equal(pg, D("pg").TypeFor(P(kind, precision: 3, scale: 1)));
    }

    [Theory]
    [InlineData(ImportKind.DateTimeOffset, "datetimeoffset(3)")]
    [InlineData(ImportKind.Time, "time(3)")]
    [InlineData(ImportKind.DateTime, "datetime2(3)")]
    public void Sql_server_temporal_types_keep_their_precision(ImportKind kind, string expected) =>
        Assert.Equal(expected, D("mssql").TypeFor(P(kind, precision: 3)));

    [Fact]
    public void Sql_server_text_type_sizes()
    {
        var d = D("mssql");
        Assert.Equal("nvarchar(50)", d.TypeFor(P(ImportKind.Text, maxLen: 1)));
        Assert.Equal("nvarchar(4000)", d.TypeFor(P(ImportKind.Text, maxLen: 3000)));
        Assert.Equal("nvarchar(max)", d.TypeFor(P(ImportKind.Text, maxLen: 4001)));
    }

    [Theory]
    [InlineData("mssql", "int", true)]
    [InlineData("mssql", "decimal(10,2)", true)]
    [InlineData("mssql", "date", true)]
    [InlineData("mssql", "nvarchar(20)", false)]
    [InlineData("mssql", "varchar(max)", false)]
    [InlineData("mssql", "xml", false)]
    [InlineData("mssql", null, false)]
    [InlineData("pg", "integer", true)]
    [InlineData("pg", "uuid", true)]
    [InlineData("pg", "text", false)]
    public void Blank_cells_are_null_only_for_non_text_columns(string engine, string? type, bool expected) =>
        Assert.Equal(expected, D(engine).BlankIsNull(type));

    [Fact]
    public void Ddl_helpers_quote_identifiers_per_engine()
    {
        var cols = new List<(string Name, string Type, bool NotNull)> { ("Id", "int", true), ("we]ird\"", "text", false) };
        var ms = D("mssql");
        var create = ms.CreateTable("dbo", "t]x", cols, ["Id"]);
        Assert.Contains("CREATE TABLE [dbo].[t]]x]", create);
        Assert.Contains("[we]]ird\"] text NULL", create);
        Assert.Contains("CONSTRAINT [PK_t]]x] PRIMARY KEY ([Id])", create);
        Assert.Contains("ALTER TABLE [dbo].[t] ALTER COLUMN [c] nvarchar(50) NOT NULL;", ms.AlterColumnType("dbo", "t", new SnapColumn { Name = "c", Type = "x", Nullable = false }, "nvarchar(50)"));
        Assert.Equal("ALTER TABLE [dbo].[t] ADD [n] int NULL;", ms.AddColumn("dbo", "t", "n", "int"));

        var pg = D("pg");
        var pcreate = pg.CreateTable("public", "t\"x", cols, ["Id"]);
        Assert.Contains("\"public\".\"t\"\"x\"", pcreate);
        Assert.Contains("\"we]ird\"\"\" text", pcreate);
        Assert.Contains("CONSTRAINT \"t\"\"x_pkey\" PRIMARY KEY (\"Id\")", pcreate);
        Assert.Contains("ALTER TABLE \"public\".\"t\" ALTER COLUMN \"c\"", pg.AlterColumnType("public", "t", new SnapColumn { Name = "c", Type = "x", Nullable = true }, "varchar(50)"));
        Assert.Contains("ADD COLUMN \"n\" integer", pg.AddColumn("public", "t", "n", "integer"));
    }

    [Fact]
    public void Only_sql_server_substitutes_defaults_for_nulls() => Assert.True(D("mssql").NullUsesDefault);

    [Fact]
    public void Both_engines_have_a_native_bulk_loader()
    {
        Assert.NotNull(D("mssql").Loader);
        Assert.NotNull(D("pg").Loader);
    }
}

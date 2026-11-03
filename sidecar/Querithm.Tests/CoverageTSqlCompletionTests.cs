using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql.Completion;
using Xunit.Abstractions;

namespace Querithm.Tests;

/// <summary>T-SQL completion: expression type inference, token-level fallbacks for text that does not parse.</summary>
public class TSqlCompletionInferenceTests(ITestOutputHelper output)
{
    static readonly ISchemaSource Sales = new FakeSchema(FakeSchema.SalesDb(), "SalesDB", "master");

    static CompletionItem[] Complete(string sql)
    {
        var i = sql.IndexOf('|');
        var text = sql.Remove(i, 1);
        return TSqlCompletion.Complete(new CompleteParams { DocId = "cov-" + Guid.NewGuid(), Text = text, Offset = i, Dialect = Dialect.Tsql, ProfileId = "p1", Database = "SalesDB" }, Sales);
    }

    static Dictionary<string, string?> Types(string sql) => Complete(sql).Where(i => i.Kind == CompletionKind.Column).GroupBy(i => i.Label).ToDictionary(g => g.Key, g => g.First().Detail);

    [Fact]
    public void Expression_type_inference_in_cte_projection()
    {
        var t = Types("""
            DECLARE @v nvarchar(30), @n int;
            WITH c AS (SELECT
                CAST(1 AS bigint) AS a_cast, TRY_CAST('1' AS int) AS b_trycast, CONVERT(varchar(10), 1) AS c_conv, TRY_CONVERT(date, '2020-01-01') AS d_tryconv,
                PARSE('1' AS decimal(10,2)) AS e_parse,
                5000000000 AS f_big, 7 AS g_int, 1.25 AS h_num, 1e3 AS i_real, $5 AS j_money, 0x1F AS k_bin, 'ab' AS l_vc, N'abc' AS m_nv,
                @v AS n_var, @n AS o_var2, @@ROWCOUNT AS p_glob, (1) AS q_paren, -(1) AS r_unary,
                IIF(1 = 1, 1, 2) AS s_iif, COALESCE(NULL, 'a') AS t_coalesce, NULLIF(1, 2) AS u_nullif,
                CASE WHEN 1 = 1 THEN 1.5 ELSE 2 END AS v_case, CASE @n WHEN 1 THEN 'x' ELSE 'y' END AS w_simplecase,
                LEFT('abc', 2) AS x_left, RIGHT('abc', 2) AS y_right, (SELECT 1 AS one) AS z_sub,
                1 + 1.5 AS aa_bin, dbo.fn_CustomerTier(1) AS bb_udf, UPPER('x') AS cc_builtin, NULL AS dd_null,
                o.OrderDate AS ee_col, o.Nope AS ff_missing
              FROM dbo.Orders o)
            SELECT c.| FROM c
            """);
        output.WriteLine(string.Join("\n", t.Select(kv => kv.Key + " = " + kv.Value)));
        Assert.Equal("bigint", t["a_cast"]);
        Assert.Equal("int", t["b_trycast"]);
        Assert.Equal("varchar(10)", t["c_conv"]);
        Assert.Equal("date", t["d_tryconv"]);
        Assert.Equal("decimal(10,2)", t["e_parse"]);
        Assert.Equal("numeric(10,0)", t["f_big"]);
        Assert.Equal("int", t["g_int"]);
        Assert.Equal("numeric(3,2)", t["h_num"]);
        Assert.Equal("float", t["i_real"]);
        Assert.Equal("money", t["j_money"]);
        Assert.Equal("varbinary", t["k_bin"]);
        Assert.Equal("varchar(2)", t["l_vc"]);
        Assert.Equal("nvarchar(3)", t["m_nv"]);
        Assert.Equal("nvarchar(30)", t["n_var"]);
        Assert.Equal("int", t["o_var2"]);
        Assert.Equal("int", t["p_glob"]);
        Assert.Equal("int", t["q_paren"]);
        Assert.Equal("int", t["r_unary"]);
        Assert.Equal("int", t["s_iif"]);
        Assert.Equal("varchar(1)", t["t_coalesce"]);
        Assert.Equal("int", t["u_nullif"]);
        Assert.Equal("numeric(2,1)", t["v_case"]);
        Assert.Equal("varchar(1)", t["w_simplecase"]);
        Assert.Equal("varchar(3)", t["x_left"]);
        Assert.Equal("varchar(3)", t["y_right"]);
        Assert.Equal("int", t["z_sub"]);
        Assert.Equal("numeric(2,1)", t["aa_bin"]);
        Assert.Equal("nvarchar(10)", t["bb_udf"]);
        Assert.Equal("datetime2", t["ee_col"]);
        Assert.Equal("varchar(1)", t["cc_builtin"]);
        Assert.Null(t["dd_null"]);
        Assert.Null(t["ff_missing"]);
    }

    [Fact]
    public void Table_valued_sources_expose_their_columns()
    {
        var tvf = Types("SELECT f.| FROM dbo.tvf_OrdersFor(1) AS f");
        Assert.Equal("bigint", tvf["OrderId"]);
        Assert.Equal("money", tvf["Total"]);

        var split = Types("SELECT s.| FROM STRING_SPLIT('a,b', ',') AS s");
        Assert.Equal("nvarchar(4000)", split["value"]);

        var series = Types("SELECT g.| FROM GENERATE_SERIES(1, 10) AS g");
        Assert.Equal("int", series["value"]);

        var json = Types("SELECT j.| FROM OPENJSON(N'[]') AS j");
        Assert.Equal("nvarchar(max)", json["value"]);
        Assert.Equal("int", json["type"]);

        var jsonWith = Types("SELECT j.| FROM OPENJSON(N'[]') WITH (Id int '$.id', Name nvarchar(20) '$.n') AS j");
        Assert.Equal("int", jsonWith["Id"]);
        Assert.Equal("nvarchar(20)", jsonWith["Name"]);

        var values = Types("SELECT v.| FROM (VALUES (1, 'a'), (2, 'b')) AS v (Num, Txt)");
        Assert.Equal("int", values["Num"]);
        Assert.Equal("varchar(1)", values["Txt"]);
    }

    [Fact]
    public void Pivot_and_unpivot_expose_generated_columns()
    {
        var p = Types("SELECT pv.| FROM (SELECT CustomerId, Status, TotalAmount FROM dbo.Orders) AS src PIVOT (SUM(TotalAmount) FOR Status IN ([Open], [Closed])) AS pv");
        Assert.Contains("Open", p.Keys);
        Assert.Contains("Closed", p.Keys);
        Assert.Contains("CustomerId", p.Keys);
        var u = Types("SELECT up.| FROM (SELECT CustomerId, OrderId, TotalAmount FROM dbo.Orders) AS src UNPIVOT (Val FOR Kind IN (OrderId, TotalAmount)) AS up");
        Assert.Contains("Val", u.Keys);
        Assert.Contains("Kind", u.Keys);
        Assert.Contains("CustomerId", u.Keys);
    }

    [Fact]
    public void Variables_declared_by_text_that_does_not_parse_are_still_known()
    {
        // The dangling "= ," makes the DECLARE unparseable, forcing the token-level reader.
        var vars = Complete("DECLARE @a int = , @b varchar(20), @c AS date\nSELECT @|");
        Assert.Equal("int", vars.First(i => i.Label == "@a").Detail);
        Assert.Equal("varchar(20)", vars.First(i => i.Label == "@b").Detail);
        Assert.Equal("date", vars.First(i => i.Label == "@c").Detail);
    }

    [Fact]
    public void Table_variable_with_unparseable_body_reads_columns_from_tokens()
    {
        var items = Complete("DECLARE @t TABLE (Id int,, Name nvarchar(10), CONSTRAINT x PRIMARY KEY (Id)) garbage\nSELECT t.| FROM @t AS t");
        var cols = items.Where(i => i.Kind == CompletionKind.Column).ToDictionary(i => i.Label, i => i.Detail);
        Assert.Equal("int", cols["Id"]);
        Assert.Equal("nvarchar(10)", cols["Name"]);
        Assert.DoesNotContain("x", cols.Keys);
    }

    [Fact]
    public void Partially_typed_create_procedure_exposes_its_parameters()
    {
        var items = Complete("CREATE PROCEDURE dbo.usp_New @a int, @b varchar(10) = 'x' OUTPUT, @c decimal(10, 2) READONLY, @d AS bit\nAS BEGIN\n  SELECT @|");
        var ps = items.Where(i => i.Kind == CompletionKind.Parameter).ToDictionary(i => i.Label, i => i.Detail);
        Assert.Equal("int", ps["@a"]);
        Assert.Equal("varchar(10) OUTPUT", ps["@b"]);
        Assert.Equal("decimal(10,2) READONLY", ps["@c"]);
        Assert.Equal("bit", ps["@d"]);
    }

    [Fact]
    public void Parsed_create_procedure_parameters_carry_types_and_defaults()
    {
        var items = Complete("CREATE OR ALTER PROCEDURE dbo.usp_P @a int, @b nvarchar(20) = N'x' OUTPUT, @t dbo.Tbl READONLY AS BEGIN SELECT @| FROM dbo.Orders END");
        var a = items.First(i => i.Label == "@a");
        Assert.Equal("int", a.Detail);
        Assert.Contains(items, i => i.Label == "@b");
        Assert.Contains(items, i => i.Label == "@t");
    }

    [Fact]
    public void Temp_table_with_unparseable_definition_reads_columns_from_tokens()
    {
        var items = Complete("CREATE TABLE #w (Id int,, Name varchar(20), CONSTRAINT pk PRIMARY KEY (Id), Amt decimal(10,2) bogus bogus)\nSELECT w.| FROM #w w");
        var cols = items.Where(i => i.Kind == CompletionKind.Column).ToDictionary(i => i.Label, i => i.Detail);
        Assert.Equal("int", cols["Id"]);
        Assert.Equal("varchar(20)", cols["Name"]);
        Assert.Equal("decimal(10,2)", cols["Amt"]);
        Assert.DoesNotContain("pk", cols.Keys);
    }

    [Fact]
    public void Declared_cursor_names_are_offered_after_cursor_statements()
    {
        var items = Complete("DECLARE c1 CURSOR bogus FOR SELECT 1;\nDECLARE c2 INSENSITIVE CURSOR FOR SELECT 1;\nOPEN |");
        Assert.Contains(items, i => i is { Label: "c1", Kind: CompletionKind.Cursor });
        Assert.Contains(items, i => i is { Label: "c2", Kind: CompletionKind.Cursor });
    }

    [Theory]
    [InlineData("COUNT(*)", "int")]
    [InlineData("LEN(o.Status)", "int")]
    [InlineData("DATEDIFF(day, o.OrderDate, GETDATE())", "int")]
    [InlineData("CHARINDEX('a', o.Status)", "int")]
    [InlineData("COUNT_BIG(*)", "bigint")]
    [InlineData("ROW_NUMBER() OVER (ORDER BY o.OrderId)", "bigint")]
    [InlineData("DATEDIFF_BIG(second, o.OrderDate, GETDATE())", "bigint")]
    [InlineData("SUM(o.TotalAmount)", "decimal(18,2)")]
    [InlineData("MAX(o.OrderDate)", "datetime2")]
    [InlineData("ISNULL(o.Status, N'none')", "nvarchar(20)")]
    [InlineData("UPPER(o.Status)", "nvarchar(20)")]
    [InlineData("ROUND(o.TotalAmount, 1)", "decimal(18,2)")]
    [InlineData("LAG(o.CustomerId) OVER (ORDER BY o.OrderId)", "int")]
    [InlineData("DATEADD(day, 1, o.OrderDate)", "datetime2")]
    [InlineData("CHOOSE(2, 'a', o.Status)", "varchar(1)")]
    [InlineData("STRING_AGG(o.Status, ',')", "nvarchar(max)")]
    [InlineData("STRING_AGG(CAST(o.OrderId AS varchar(10)), ',')", "varchar(max)")]
    [InlineData("CONCAT(o.Status, 'x')", "nvarchar(4000)")]
    [InlineData("FORMAT(o.OrderDate, 'yyyy')", "nvarchar(4000)")]
    [InlineData("DATENAME(month, o.OrderDate)", "nvarchar(30)")]
    [InlineData("STR(o.TotalAmount)", "varchar(10)")]
    [InlineData("GETDATE()", "datetime")]
    [InlineData("SYSDATETIME()", "datetime2(7)")]
    [InlineData("SYSDATETIMEOFFSET()", "datetimeoffset(7)")]
    [InlineData("DATEFROMPARTS(2026, 1, 1)", "date")]
    [InlineData("EOMONTH(o.OrderDate)", "date")]
    [InlineData("NEWID()", "uniqueidentifier")]
    [InlineData("SCOPE_IDENTITY()", "numeric(38,0)")]
    [InlineData("DB_NAME()", "nvarchar(128)")]
    [InlineData("JSON_VALUE(o.Status, '$.a')", "nvarchar(4000)")]
    [InlineData("JSON_QUERY(o.Status, '$.a')", "nvarchar(max)")]
    [InlineData("HASHBYTES('SHA2_256', o.Status)", "varbinary(8000)")]
    [InlineData("SQRT(o.TotalAmount)", "float")]
    [InlineData("GREATEST(o.CustomerId, 5)", "int")]
    public void Built_in_function_calls_have_the_documented_result_type(string expression, string expected)
    {
        var t = Types($"WITH c AS (SELECT {expression} AS v FROM dbo.Orders o)\nSELECT c.| FROM c");
        Assert.Equal(expected, t["v"]);
    }

    [Fact]
    public void Cte_names_are_found_in_text_that_does_not_parse()
    {
        var items = Complete("WITH recent (a, b) AS (SELECT 1,, first AS (SELECT 2 SELECT * FROM |");
        Assert.Contains(items, i => i.Label == "recent");
    }
}

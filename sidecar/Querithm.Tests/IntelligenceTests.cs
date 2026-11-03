using Querithm.Contract;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

public class BatchSplittingTests
{
    [Fact]
    public void Splits_on_go_tokens_only()
    {
        var sql = "SELECT 'GO' AS x\n-- GO in a comment\nGO\n/* GO\n*/ SELECT 2\ngo\nSELECT [GO]";
        var b = TSqlParsing.Parse(sql).Batches;
        Assert.Equal(3, b.Length);
        Assert.Equal("SELECT 'GO' AS x\n-- GO in a comment", sql.Substring(b[0].StartOffset, b[0].Length));
        Assert.StartsWith("/* GO", sql.Substring(b[1].StartOffset, b[1].Length));
        Assert.Equal(4, b[1].StartLine);
        Assert.Equal("SELECT [GO]", sql.Substring(b[2].StartOffset, b[2].Length));
        Assert.Equal(7, b[2].StartLine);
    }

    [Fact]
    public void Go_with_count_repeats_and_parses_clean()
    {
        var parsed = TSqlParsing.Parse("INSERT INTO t VALUES (1)\nGO 5\nSELECT 1");
        Assert.Equal(5, parsed.Batches[0].RepeatCount);
        Assert.Empty(parsed.Errors);
    }

    [Fact]
    public void Empty_batches_are_skipped()
    {
        Assert.Single(TSqlParsing.Parse("GO\n\nGO\nSELECT 1\nGO\n-- only comment\nGO").Batches);
    }
}

public class ClassifierTests
{
    static Classification C(string sql) => new TSqlLanguageService().Classify(sql);

    [Fact]
    public void Detects_ddl_use_and_transactions()
    {
        var c = C("USE SalesDB;\nBEGIN TRAN;\nCREATE TABLE t (id int);\nCOMMIT;");
        Assert.True(c.HasDdl);
        Assert.True(c.BeginsTransaction);
        Assert.True(c.EndsTransaction);
        Assert.Equal("SalesDB", c.UseDatabase);
        Assert.Empty(c.Destructive);
    }

    [Fact]
    public void Statements_inside_procedure_bodies_are_not_destructive_now()
    {
        var c = C("CREATE PROCEDURE dbo.purge AS BEGIN DELETE FROM dbo.Log; TRUNCATE TABLE dbo.Tmp; DROP TABLE #x; END");
        Assert.Empty(c.Destructive);
        Assert.True(c.HasDdl);
        Assert.False(C("CREATE PROCEDURE p AS BEGIN TRAN; COMMIT").BeginsTransaction);
    }

    [Fact]
    public void Merge_with_where_in_subquery_is_not_flagged()
    {
        Assert.Empty(C("DELETE FROM dbo.Orders WHERE OrderId IN (SELECT OrderId FROM dbo.Old)").Destructive);
    }

    [Fact]
    public void Line_numbers_point_at_the_statement()
    {
        var c = C("SELECT 1;\n\nDELETE dbo.Orders;");
        Assert.Equal(3, Assert.Single(c.Destructive).Line);
    }

    [Theory]
    [InlineData("DELETE FROM orders", 1)]
    [InlineData("DELETE FROM orders WHERE id = 1", 0)]
    [InlineData("UPDATE orders SET x = 1 WHERE id IN (SELECT 1)", 0)]
    [InlineData("UPDATE orders SET x = (SELECT 1 WHERE true)", 1)]
    [InlineData("DROP TABLE orders; TRUNCATE t", 2)]
    [InlineData("CREATE OR REPLACE FUNCTION f() RETURNS int AS $$ BEGIN DELETE FROM t; RETURN 1; END $$ LANGUAGE plpgsql", 1)]
    [InlineData("SELECT 'DROP TABLE x'; -- DELETE FROM y", 0)]
    public void Postgres_classifier_works_on_tokens(string sql, int findings)
    {
        Assert.Equal(findings, new PgLanguageService().Classify(sql).Destructive.Length);
    }

    [Fact]
    public void Postgres_tokenizer_handles_dollar_quotes_casts_and_nested_comments()
    {
        var t = PgTokenizer.Tokenize("SELECT $tag$ ; ' $tag$::text /* a /* b */ c */ , $1, :name, E'it\\'s'");
        Assert.Contains(t, x => x.Kind == PgTokenKind.DollarString && x.Text == "$tag$ ; ' $tag$");
        Assert.Contains(t, x => x.Kind == PgTokenKind.Comment && x.Text == "/* a /* b */ c */");
        Assert.Contains(t, x => x.Kind == PgTokenKind.Param && x.Text == "$1");
        Assert.Contains(t, x => x.Kind == PgTokenKind.Param && x.Text == ":name");
        Assert.Contains(t, x => x.Kind == PgTokenKind.String && x.Text == "E'it\\'s'");
        Assert.Single(PgTokenizer.Statements(t));
    }
}

public class FormattingAndFoldingTests
{
    [Fact]
    public void Format_uppercases_keywords_and_keeps_comments()
    {
        var input = "-- header comment\nselect a,b from dbo.t where a=1;\n/* keep me */\nselect 2";
        var r = Formatting.Format(input, new FormatOptions { KeywordCase = KeywordCase.Upper });
        Assert.Null(r.Error);
        Assert.Contains("-- header comment", r.Text);
        Assert.Contains("/* keep me */", r.Text);
        Assert.Contains("SELECT", r.Text);
        Assert.Contains("FROM", r.Text);
    }

    [Fact]
    public void Statement_with_inner_comment_is_kept_verbatim()
    {
        var input = "select a, -- the a column\n b from t";
        Assert.Equal(input, Formatting.Format(input, null).Text);
    }

    [Fact]
    public void Format_refuses_broken_sql()
    {
        Assert.NotNull(Formatting.Format("SELECT FROM WHERE", null).Error);
    }

    [Fact]
    public void Folding_covers_begin_end_cte_and_case()
    {
        var sql = "CREATE PROCEDURE p AS\nBEGIN\n  WITH c AS (\n    SELECT 1 AS x\n  )\n  SELECT CASE\n    WHEN x = 1 THEN 'a'\n  END FROM c;\nEND";
        var ranges = Folding.Ranges(TSqlParsing.Parse(sql));
        Assert.Contains(ranges, r => r.StartLine == 2 && r.EndLine == 9);  // BEGIN/END
        Assert.Contains(ranges, r => r.StartLine == 3 && r.EndLine == 5);  // CTE
        Assert.Contains(ranges, r => r.StartLine == 6 && r.EndLine == 8);  // CASE
    }

    [Fact]
    public void Editable_script_rewrites_create_on_tokens()
    {
        var meta = new Providers.SqlServer.SqlServerMetadata();
        var caps = new Capabilities { CreateOrAlter = true, CompletionTier = "", Trace = new(), Debugger = new(), EstimatedPlan = new(), ActualPlan = new() };
        Assert.Equal("/* create */\nCREATE OR ALTER PROCEDURE p AS SELECT 'CREATE'", meta.ToEditableScript("/* create */\nCREATE PROCEDURE p AS SELECT 'CREATE'", caps));
        Assert.Equal("ALTER PROCEDURE p AS SELECT 1", meta.ToEditableScript("CREATE PROCEDURE p AS SELECT 1", caps with { CreateOrAlter = false }));
        Assert.Equal("CREATE OR ALTER VIEW v AS SELECT 1", meta.ToEditableScript("CREATE OR ALTER VIEW v AS SELECT 1", caps));
    }

    [Fact]
    public void Pg_editable_script_replaces_triggers_from_14_but_never_constraint_triggers()
    {
        var meta = new Providers.Postgres.PostgresMetadata();
        var caps = new Capabilities { CompletionTier = "", Trace = new(), Debugger = new(), EstimatedPlan = new(), ActualPlan = new(), MajorVersion = 14 };
        const string trigger = "CREATE TRIGGER t AFTER UPDATE ON x FOR EACH ROW EXECUTE FUNCTION f();";
        Assert.Equal("CREATE OR REPLACE TRIGGER t AFTER UPDATE ON x FOR EACH ROW EXECUTE FUNCTION f();", meta.ToEditableScript(trigger, caps));
        Assert.Equal(trigger, meta.ToEditableScript(trigger, caps with { MajorVersion = 13 }));
        // PostgreSQL rejects CREATE OR REPLACE CONSTRAINT TRIGGER; the scripter drops and creates instead.
        const string constraint = "CREATE CONSTRAINT TRIGGER t AFTER UPDATE ON x DEFERRABLE FOR EACH ROW EXECUTE FUNCTION f();";
        Assert.Equal(constraint, meta.ToEditableScript(constraint, caps));
    }
}

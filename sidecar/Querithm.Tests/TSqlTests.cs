using Querithm.Contract;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

public class TSqlParsingTests
{
    [Theory]
    [InlineData("a\nb\nc", 0, 1)]
    [InlineData("a\nb\nc", 1, 1)] // the newline belongs to its line
    [InlineData("a\nb\nc", 2, 2)]
    [InlineData("a\nb\nc", 4, 3)]
    [InlineData("a\n\n\nb", 4, 4)]
    [InlineData("", 0, 1)]
    public void Line_index_maps_offsets_to_one_based_lines(string text, int offset, int line)
    {
        Assert.Equal(line, TSqlParsing.LineIndex(text)(offset));
    }

    [Fact]
    public void Same_doc_and_text_reuse_the_parse()
    {
        var doc = "doc-" + Guid.NewGuid().ToString("N");
        var text = $"SELECT {Guid.NewGuid().GetHashCode()} AS n";
        var a = TSqlParsing.Parse(text, doc);
        Assert.Same(a, TSqlParsing.Parse(text, doc));
        Assert.Same(a, TSqlParsing.Parse(text)); // also cached by text
        Assert.NotSame(a, TSqlParsing.Parse(text + " ", doc));
        TSqlParsing.Forget(doc);
    }

    [Fact]
    public void Batches_report_start_and_end_lines_after_trimming()
    {
        var sql = "\n\n  SELECT 1\n  FROM t  \n\nGO\nSELECT 2";
        var b = TSqlParsing.Parse(sql).Batches;
        Assert.Equal(2, b.Length);
        Assert.Equal((3, 4), (b[0].StartLine, b[0].EndLine));
        Assert.Equal("SELECT 1\n  FROM t", sql.Substring(b[0].StartOffset, b[0].Length));
        Assert.Equal((7, 7), (b[1].StartLine, b[1].EndLine));
        Assert.Equal([0, 1], b.Select(x => x.Index));
    }

    [Fact]
    public void Go_count_applies_only_to_its_own_batch()
    {
        var b = TSqlParsing.Parse("SELECT 1\nGO 3\nSELECT 2\nGO\nSELECT 3").Batches;
        Assert.Equal([3, 1, 1], b.Select(x => x.RepeatCount));
    }

    [Fact]
    public void Text_without_go_is_one_batch()
    {
        var sql = "SELECT 1; SELECT 2;";
        var b = Assert.Single(TSqlParsing.Parse(sql).Batches);
        Assert.Equal((0, sql.Length, 1), (b.StartOffset, b.Length, b.RepeatCount));
    }

    // The same newline-separated script that PostgreSQL splits into batches: SQL Server runs it as one batch.
    [Fact]
    public void Statements_without_semicolons_parse_as_one_batch_of_separate_statements()
    {
        var sql = "SELECT * FROM saas.empEmployeeBasicInfo ebi\nSELECT * FROM saas.empEmployeeBasicInfoDetails ebid";
        var parsed = TSqlParsing.Parse(sql);
        var b = Assert.Single(parsed.Batches);
        Assert.Equal((0, sql.Length, 1, 2), (b.StartOffset, b.Length, b.StartLine, b.EndLine));
        Assert.Empty(parsed.Errors);
        Assert.Equal(2, Assert.Single(parsed.Script!.Batches).Statements.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData("-- nothing\n/* here */")]
    public void Text_without_code_has_no_batches(string sql) => Assert.Empty(TSqlParsing.Parse(sql).Batches);
}

public class TSqlClassifierTests
{
    static Classification C(string sql) => new TSqlLanguageService().Classify(sql);

    [Theory]
    [InlineData("DROP TABLE IF EXISTS dbo.t", "drop", "DROP TABLE permanently removes the object.")]
    [InlineData("DROP PROCEDURE dbo.p", "drop", "DROP PROCEDURE permanently removes the object.")]
    [InlineData("ALTER VIEW v AS SELECT 1 AS x", "alter", "ALTER VIEW changes an existing object.")]
    [InlineData("CREATE OR ALTER VIEW v AS SELECT 1 AS x", "alter", "CREATE OR ALTER VIEW changes an existing object.")]
    [InlineData("TRUNCATE TABLE dbo.t", "truncate", "TRUNCATE TABLE removes every row.")]
    [InlineData("UPDATE o SET x = 1 FROM dbo.o JOIN dbo.p ON p.id = o.id", "updateWithoutWhere", "UPDATE without a WHERE clause changes every row.")]
    [InlineData("DELETE TOP (10) FROM dbo.t", "deleteWithoutWhere", "DELETE without a WHERE clause removes every row.")]
    public void Rules_and_descriptions(string sql, string rule, string description)
    {
        var f = Assert.Single(C(sql).Destructive);
        Assert.Equal((rule, description), (f.Rule, f.Description));
    }

    // T-SQL counterparts of the PostgreSQL CTE / DO / EXPLAIN ANALYZE cases: statements nested in control flow run now,
    // a CTE can only feed the main DELETE, and STATISTICS (unlike SHOWPLAN) still executes the statement.
    [Theory]
    [InlineData("WITH c AS (SELECT * FROM dbo.t) DELETE FROM c", "deleteWithoutWhere")]
    [InlineData("BEGIN DELETE FROM dbo.t END", "deleteWithoutWhere")]
    [InlineData("IF 1 = 1 BEGIN DROP TABLE dbo.t END", "drop")]
    [InlineData("BEGIN TRY TRUNCATE TABLE dbo.t END TRY BEGIN CATCH SELECT 1 END CATCH", "truncate")]
    [InlineData("WHILE 1 = 0 UPDATE dbo.t SET x = 1", "updateWithoutWhere")]
    [InlineData("SET STATISTICS XML ON; DELETE FROM dbo.t", "deleteWithoutWhere")]
    public void Statements_that_run_inside_another_are_flagged(string sql, string rule)
    {
        Assert.Equal(rule, Assert.Single(C(sql).Destructive).Rule);
    }

    [Fact]
    public void Altering_a_module_flags_the_alter_but_not_its_body()
    {
        var f = Assert.Single(C("ALTER PROCEDURE dbo.p AS BEGIN DELETE FROM dbo.Log; DROP TABLE dbo.x; END").Destructive);
        Assert.Equal("alter", f.Rule);
        Assert.Empty(C("CREATE TRIGGER tr ON dbo.t AFTER INSERT AS DELETE FROM dbo.t").Destructive);
    }

    [Fact]
    public void Findings_after_a_module_are_still_reported()
    {
        var c = C("CREATE PROCEDURE p AS DELETE FROM t;\nGO\nDELETE FROM t;");
        Assert.Equal(3, Assert.Single(c.Destructive).Line);
    }

    [Fact]
    public void Columns_point_at_the_statement()
    {
        var f = Assert.Single(C("SELECT 1;   DELETE FROM t").Destructive);
        Assert.Equal((1, 13), (f.Line, f.Column));
    }

    [Theory]
    [InlineData("CREATE INDEX ix ON dbo.t (a)")]
    [InlineData("CREATE TABLE #t (id int)")]
    [InlineData("CREATE PROCEDURE p AS SELECT 1")]
    public void Create_is_ddl_but_not_destructive(string sql)
    {
        var c = C(sql);
        Assert.True(c.HasDdl);
        Assert.Empty(c.Destructive);
    }

    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("INSERT INTO t (a) VALUES (1)")]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET t.a = s.a;")]
    [InlineData("SELECT 'DELETE FROM t' AS q -- DROP TABLE t")]
    public void Non_destructive_statements(string sql)
    {
        var c = C(sql);
        Assert.Empty(c.Destructive);
        Assert.False(c.HasDdl);
    }

    [Fact]
    public void Rollback_ends_and_the_last_use_wins()
    {
        var c = C("USE a;\nUSE [b];\nROLLBACK TRANSACTION;");
        Assert.True(c.EndsTransaction);
        Assert.False(c.BeginsTransaction);
        Assert.Equal("b", c.UseDatabase);
    }

    [Fact]
    public void Parse_errors_are_reported_as_diagnostics()
    {
        var c = C("SELECT 1\nSELECT FROM WHERE");
        var e = Assert.Single(c.ParseErrors);
        Assert.Equal(2, e.Line);
        Assert.Equal(Severity.Error, e.Severity);
        Assert.Equal(e.Column + 1, e.EndColumn);
    }

    [Fact]
    public void Statement_snippet_collapses_whitespace_and_is_capped()
    {
        Assert.Equal("DELETE FROM t", Assert.Single(C("DELETE\n   FROM\tt").Destructive).Statement);
        var longOne = Assert.Single(C("DELETE FROM dbo." + new string('x', 120) + " OUTPUT deleted." + new string('y', 120)).Destructive).Statement;
        Assert.Equal(201, longOne.Length);
        Assert.EndsWith("…", longOne);
    }
}

public class TSqlFormattingTests
{
    [Fact]
    public void Lowercase_keywords()
    {
        var r = Formatting.Format("SELECT a FROM t WHERE a = 1", new FormatOptions { KeywordCase = KeywordCase.Lower });
        Assert.Null(r.Error);
        Assert.Contains("select", r.Text);
        Assert.Contains("where", r.Text);
        Assert.DoesNotContain("SELECT", r.Text);
    }

    [Fact]
    public void Leading_commas_move_to_the_next_line()
    {
        var r = Formatting.Format("select a, b, c from t", new FormatOptions { LeadingCommas = true }).Text;
        var lines = r.Split('\n');
        Assert.DoesNotContain(lines, l => l.TrimEnd().EndsWith(','));
        Assert.Contains(lines, l => l.TrimStart().StartsWith(", b", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_crlf_output_is_normalised_and_leading_commas_still_move()
    {
        // ScriptDom writes Environment.NewLine, i.e. CRLF on Windows.
        var r = Formatting.Tidy("SELECT a,\r\n       b,\r\n       c\r\nFROM   t\r\n", leadingCommas: true);
        Assert.Equal("SELECT a\n       , b\n       , c\nFROM   t", r);
    }

    [Fact]
    public void Formatted_text_uses_lf_line_endings()
    {
        var r = Formatting.Format("select a, b from t\n\nselect 1", null).Text;
        Assert.DoesNotContain('\r', r);
    }

    [Fact]
    public void Error_names_the_line()
    {
        var r = Formatting.Format("select 1\nselect from where", null);
        Assert.StartsWith("Cannot format: line 2:", r.Error);
        Assert.Equal("select 1\nselect from where", r.Text);
    }

    [Fact]
    public void Go_separators_and_comments_between_batches_survive()
    {
        var r = Formatting.Format("select 1\nGO\n-- next\nselect 2", null);
        Assert.Null(r.Error);
        var lines = r.Text.Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Contains("GO", lines);
        Assert.Contains("-- next", lines);
        Assert.Equal(2, lines.Count(l => l.StartsWith("SELECT", StringComparison.Ordinal)));
    }

    [Fact]
    public void Formatting_is_idempotent()
    {
        var once = Formatting.Format("select a,b from dbo.t where a=1;select 2;", null).Text;
        Assert.Equal(once, Formatting.Format(once, null).Text);
    }

    [Fact]
    public void Empty_text_formats_to_empty()
    {
        var r = Formatting.Format("", null);
        Assert.Null(r.Error);
        Assert.Equal("", r.Text);
    }

    [Fact]
    public void A_blank_line_between_statements_is_kept()
    {
        Assert.Contains("\n\n", Formatting.Format("select 1;\n\nselect 2;", null).Text);
    }

    [Theory]
    [InlineData("select 1;\n\n\n\n\nselect 2;", "SELECT 1;\n\nSELECT 2;")]
    [InlineData("select 1;\nselect 2;", "SELECT 1;\nSELECT 2;")]
    [InlineData("select 1;\n-- c\n\n\n\n\nselect 2;", "SELECT 1;\n-- c\n\nSELECT 2;")]
    [InlineData("select 1;\n\n\n-- c\nselect 2;", "SELECT 1;\n\n-- c\nSELECT 2;")]
    [InlineData("select 1;\nGO\n\n\nselect 2;", "SELECT 1;\nGO\n\nSELECT 2;")]
    [InlineData("select 1;\r\n  \r\n\t\r\n\r\nselect 2;", "SELECT 1;\n\nSELECT 2;")]
    public void Runs_of_blank_lines_collapse_to_one_wherever_they_are(string sql, string expected)
    {
        Assert.Equal(expected, Formatting.Format(sql, null).Text);
    }

    [Fact]
    public void Statement_terminators_are_kept()
    {
        Assert.Equal(2, Formatting.Format("select 1;\nselect 2;", null).Text.Count(c => c == ';'));
    }

    [Fact]
    public void Formatted_merge_still_parses()
    {
        var r = Formatting.Format("merge t using s on t.id = s.id when matched then update set t.a = s.a;\nselect 1", null);
        Assert.Null(r.Error);
        Assert.Empty(TSqlParsing.Parse(r.Text).Errors);
    }

    [Fact]
    public void Formatted_text_parses_again()
    {
        var r = Formatting.Format("select a, b from dbo.t t join dbo.u u on u.id = t.id where t.a > 1 order by b desc\nupdate t set a = 1 where b = 2", null);
        Assert.Null(r.Error);
        Assert.Empty(TSqlParsing.Parse(r.Text).Errors);
    }
}

public class TSqlFoldingTests
{
    static FoldingRange[] F(string sql) => Folding.Ranges(TSqlParsing.Parse(sql));

    [Fact]
    public void Region_comments_fold_and_nest()
    {
        var r = F("--region outer\nSELECT 1;\n-- region inner\nSELECT 2;\n--endregion\n--endregion");
        Assert.Contains(r, x => (x.StartLine, x.EndLine, x.Kind) == (1, 6, "region"));
        Assert.Contains(r, x => (x.StartLine, x.EndLine, x.Kind) == (3, 5, "region"));
    }

    [Fact]
    public void Unmatched_endregion_is_ignored()
    {
        Assert.DoesNotContain(F("SELECT 1;\n--endregion\nSELECT 2;"), x => x.Kind == "region");
    }

    [Fact]
    public void Multiline_comments_fold_as_comments()
    {
        var r = F("/* one\ntwo\nthree */\nSELECT 1");
        Assert.Contains(r, x => (x.StartLine, x.EndLine, x.Kind) == (1, 3, "comment"));
    }

    [Fact]
    public void Single_line_constructs_do_not_fold()
    {
        Assert.Empty(F("SELECT CASE WHEN 1 = 1 THEN (SELECT 1) END /* c */"));
    }

    [Fact]
    public void Folds_are_unique_and_outermost_first()
    {
        var r = F("IF 1 = 1\nBEGIN\n  SELECT 1\n  FROM t\nEND");
        Assert.Equal(r.Length, r.Select(x => (x.StartLine, x.EndLine)).Distinct().Count());
        Assert.Contains(r, x => (x.StartLine, x.EndLine) == (1, 5)); // IF
        Assert.Contains(r, x => (x.StartLine, x.EndLine) == (2, 5)); // BEGIN/END
        Assert.Contains(r, x => (x.StartLine, x.EndLine) == (3, 4)); // SELECT
        Assert.All(r, x => Assert.True(x.EndLine > x.StartLine));
    }

    [Fact]
    public void Broken_sql_still_folds_comments()
    {
        Assert.Contains(F("/* a\nb */\nSELECT FROM WHERE"), x => x.Kind == "comment");
    }
}

public class TSqlDiagnosticsTests
{
    static readonly TSqlLanguageService Svc = new();

    [Fact]
    public void Short_text_is_not_chunked()
    {
        Assert.Null(ChunkedDiagnostics.TryDiagnose("SELECT FROM WHERE"));
        Assert.Null(ChunkedDiagnostics.TryDiagnose(new string(' ', ChunkedDiagnostics.MinLength - 1)));
    }

    [Fact]
    public void Chunks_tile_the_text_without_gaps()
    {
        var text = LargeDocumentTests.BigProc();
        var chunks = ChunkedDiagnostics.Split(text);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(text.Length, chunks[^1].End);
        for (var i = 1; i < chunks.Count; i++) Assert.Equal(chunks[i - 1].End, chunks[i].Start);
        Assert.All(chunks, c => Assert.True(c.StartDepth >= 0 && c.EndDepth >= 0));
        // Every inner cut is right after a statement-ending semicolon.
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.Equal(';', text[c.End - 1]));
    }

    [Fact]
    public void Semicolons_inside_strings_comments_and_brackets_are_never_cut_points()
    {
        var filler = string.Concat(Enumerable.Repeat("SELECT '" + new string(';', 60) + "' AS [a;b] /* ; */ -- ;\n", 4000));
        var chunks = ChunkedDiagnostics.Split(filler);
        Assert.Single(chunks);
    }

    [Fact]
    public void Go_lines_end_a_chunk()
    {
        var part = string.Concat(Enumerable.Repeat("SELECT 1\n", 2000));
        var text = part + "GO\n" + part;
        var chunks = ChunkedDiagnostics.Split(text);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(part.Length, chunks[0].End);
        Assert.DoesNotContain("GO", text[chunks[1].Start..chunks[1].End]);
    }

    [Fact]
    public void Diagnostics_span_the_offending_token()
    {
        var d = Assert.Single(Svc.Diagnostics(new DiagnosticsParams { DocId = "d", Text = "SELECT 1\nSELECT * FROM FROM t" }));
        Assert.Equal(2, d.Line);
        Assert.Equal(d.Column + "FROM".Length, d.EndColumn);
        Assert.Equal(Severity.Error, d.Severity);
    }

    [Fact]
    public void Valid_text_has_no_diagnostics()
    {
        Assert.Empty(Svc.Diagnostics(new DiagnosticsParams { DocId = "d", Text = "SELECT a FROM t WHERE a = 1\nGO 2" }));
    }
}

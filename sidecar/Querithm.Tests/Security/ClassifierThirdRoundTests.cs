using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Providers.MySql;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 2, code audit 2026-11-25 third round: MySQL quoted user variables, optimizer hints, mixed version gates,
/// nested comments in a skipped gate, a stored-program header past its ';', linear compound bodies; PostgreSQL EXECUTE of
/// built text in a body and nesting limits; Oracle quoted package names, scheduler / job packages, WITH FUNCTION bodies
/// and EXECUTE IMMEDIATE nesting.
/// </summary>
public class ClassifierThirdRoundTests
{
    static readonly MySqlLanguageService My = new();
    static readonly PgLanguageService Pg = new();
    static readonly OracleLanguageService Ora = new();

    static Classification Bounded(Func<Classification> classify)
    {
        var task = Task.Run(classify);
        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "classification did not end within 30 s");
        return task.Result;
    }

    // ------------------------------------------------------------------------------------------------------------ MySQL

    /// <summary>@'…' and 'u'@'h…' are read like strings of the reading: a backslash escapes the quote unless NO_BACKSLASH_ESCAPES.</summary>
    [Theory]
    [InlineData(@"SELECT @'\' ' ; DROP TABLE t; -- '")]
    [InlineData(@"SELECT 'u'@'h\' ' ; DROP TABLE t; -- '")]
    [InlineData(@"SELECT @""\"" "" ; DROP TABLE t; -- """)]
    public void A_drop_behind_an_escaped_quote_in_a_quoted_variable_is_found(string text) =>
        Assert.Contains(My.Classify(text).Destructive, f => f.Rule == "drop");

    [Fact]
    public void A_backtick_variable_name_has_no_escapes()
    {
        // `\` ends at the second backtick in every reading: the DROP is code.
        Assert.Contains(My.Classify(@"SELECT @`\`; DROP TABLE t;").Destructive, f => f.Rule == "drop");
        Assert.Empty(My.Classify("SELECT @`a`, @'b', 'u'@'localhost';").Destructive);
    }

    /// <summary>MySQL's hint reader treats `…` and "…" as quoted: a */ inside them does not end the hint.</summary>
    [Fact]
    public void A_hint_that_ends_elsewhere_for_the_hint_reader_is_a_finding_and_gets_no_plan()
    {
        const string text = "SELECT /*+ BKA(`*/ ' `) */ 1; DROP TABLE t; -- '";
        Assert.Contains(My.Classify(text).Destructive, f => f.Rule == "unparsed");
        IPlanProvider plans = new MySqlPlans();
        Assert.Equal(MySqlPlans.ReadingRefusal, plans.Refusal(text, PlanMode.Estimated));
        // Hints whose quotes hold no */ read the same both ways.
        Assert.Empty(My.Classify("SELECT /*+ BKA(t1) INDEX(`t` `i`) */ 1 FROM t;").Destructive);
        Assert.Null(plans.Refusal("SELECT /*+ INDEX(`t` `i`) */ 1 FROM t", PlanMode.Estimated));
    }

    /// <summary>A real server runs some gated comments and skips others (MySQL 8 runs /*!50000, skips /*!99999).</summary>
    [Theory]
    [InlineData("SELECT 1 /*!50000 -- */ '\n*/ /*!99999 ' # */ ; DROP TABLE t; -- '")]
    // A skipped gated comment may hold one nested comment.
    [InlineData("SELECT 1 /*!99999 /* x */ ' */ ; DROP TABLE t; -- '")]
    public void A_drop_behind_a_mix_of_run_and_skipped_gated_comments_is_found(string text)
    {
        Assert.Contains(My.Classify(text).Destructive, f => f.Rule == "drop");
        IPlanProvider plans = new MySqlPlans();
        Assert.Equal(MySqlPlans.ReadingRefusal, plans.Refusal(text, PlanMode.Estimated));
    }

    [Fact]
    public void Mysqldump_reads_with_three_readings_only()
    {
        // Gated comments that change only their own content get the all-comment reading, not one per version.
        var dump = "/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;\n/*!50503 SET NAMES utf8mb4 */;\n/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;\nINSERT INTO t VALUES (1);";
        Assert.Empty(MySqlTokenizer.SplitChangingGates(dump, MySqlLexMode.Default));
        Assert.Empty(My.Classify(dump).Destructive);
        // The reading as written and the MySQL and MariaDB servers that skip every gated comment (fourth round adds MariaDB).
        Assert.Equal(3, MySqlLanguageService.ModesToRead(dump, MySqlLexMode.Default).Count());
    }

    [Fact]
    public void A_stored_program_header_ends_at_its_semicolon()
    {
        // CREATE USER function: FUNCTION is a user name, and the next statement's '(' is no parameter list.
        Assert.Contains(My.Classify("DELIMITER $$\nCREATE USER function; SELECT * FROM (SELECT 1 a) begin, (SELECT 2 b) x; DROP TABLE t $$").Destructive,
            f => f.Rule == "drop");
    }

    /// <summary>`IF 1 THEN` × 50,000 took about 10^10 steps: the run could neither start nor be cancelled.</summary>
    [Fact]
    public void Deeply_nested_compound_statements_classify_in_linear_time()
    {
        var sb = new StringBuilder("DELIMITER $$\nBEGIN NOT ATOMIC ");
        for (var i = 0; i < 50_000; i++) sb.Append("IF 1 THEN ");
        sb.Append("DELETE FROM t; ");
        for (var i = 0; i < 50_000; i++) sb.Append("END IF; ");
        sb.Append("END $$");
        var c = Bounded(() => My.Classify(sb.ToString()));
        Assert.Contains(c.Destructive, f => f.Rule == "deleteWithoutWhere");

        var flat = "DELIMITER $$\nBEGIN NOT ATOMIC " + string.Concat(Enumerable.Repeat("SELECT 1 THEN DELETE FROM t WHERE 1 ", 50_000)) + "; END $$";
        Bounded(() => My.Classify(flat));
    }

    // ------------------------------------------------------------------------------------------------------- PostgreSQL

    [Fact]
    public void An_execute_of_built_text_in_a_body_is_a_change_of_reading()
    {
        var text = "CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE format($f$SET %s TO off$f$, $n$standard_conforming_strings$n$); END $$;\nSELECT f();\nSELECT 'C:\\dir';";
        Assert.Contains(Pg.Classify(text).Destructive, f => f.Rule == "readingChanged");
        Assert.Contains(Pg.Classify("DO $$ BEGIN EXECUTE 'SET ' || 'standard_conforming_strings = off'; END $$;\nSELECT 'C:\\dir';").Destructive, f => f.Rule == "readingChanged");
        // One literal is read as it is.
        Assert.DoesNotContain(Pg.Classify("DO $$ BEGIN EXECUTE 'SELECT 1'; END $$;\nSELECT 'C:\\dir';").Destructive, f => f.Rule == "readingChanged");
    }

    [Theory]
    [InlineData("with")]
    [InlineData("prepare")]
    [InlineData("do")]
    public void Deep_nesting_is_a_finding_not_a_crash(string kind)
    {
        var text = kind switch
        {
            "with" => string.Concat(Enumerable.Repeat("WITH a AS (", 10_000)) + "SELECT 1" + new string(')', 10_000) + " SELECT 1",
            "prepare" => string.Concat(Enumerable.Repeat("PREPARE p AS ", 10_000)) + "SELECT 1",
            _ => Nested(200),
        };
        Assert.Contains(Bounded(() => Pg.Classify(text)).Destructive, f => f.Rule == "unparsed");

        static string Nested(int levels)
        {
            var inner = "SELECT 1";
            for (var k = levels; k > 0; k--) inner = $"DO $q{k}$ BEGIN EXECUTE $e{k}$ {inner} $e{k}$; END $q{k}$";
            return inner;
        }
    }

    [Fact]
    public void A_deeply_parenthesized_from_list_completes_without_a_crash()
    {
        var text = "SELECT  FROM " + new string('(', 100_000) + "t" + new string(')', 100_000);
        var items = Pg.Complete(new CompleteParams { DocId = "parens-" + Guid.NewGuid(), Text = text, Offset = 7, Dialect = Dialect.Pg, ProfileId = "p", Database = "shop" },
            new FakeSchema(new SchemaSnapshot { ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Objects = [], Fks = [] }, "shop"), CancellationToken.None);
        Assert.NotNull(items);
    }

    // ----------------------------------------------------------------------------------------------------------- Oracle

    [Theory]
    [InlineData("BEGIN \"DBMS_SQL\".PARSE(c, 'DROP TABLE t', 1); END;")]
    [InlineData("BEGIN sys.\"DBMS_UTILITY\".\"EXEC_DDL_STATEMENT\"('DROP TABLE t'); END;")]
    [InlineData("BEGIN DBMS_SCHEDULER.CREATE_JOB(job_name => 'j', job_type => 'PLSQL_BLOCK', job_action => 'BEGIN DELETE FROM t; END;', enabled => TRUE); END;")]
    [InlineData("DECLARE j NUMBER; BEGIN DBMS_JOB.SUBMIT(j, 'DELETE FROM t;'); END;")]
    [InlineData("BEGIN DBMS_DDL.CREATE_WRAPPED('CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;'); END;")]
    public void A_call_that_runs_built_text_is_dynamic_sql(string text) =>
        Assert.Contains(Ora.Classify(text).Destructive, f => f.Rule == "dynamicSql");

    [Fact]
    public void A_cursor_handed_back_runs_no_sql()
    {
        Assert.DoesNotContain(Ora.Classify("DECLARE c SYS_REFCURSOR; BEGIN OPEN c FOR SELECT 1 FROM dual; DBMS_SQL.RETURN_RESULT(c); END;").Destructive, f => f.Rule == "dynamicSql");
        // A user object named "dbms_sql" (quoted lower case) is not the package.
        Assert.DoesNotContain(Ora.Classify("BEGIN \"dbms_sql\".run(1); END;").Destructive, f => f.Rule == "dynamicSql");
    }

    /// <summary>WITH FUNCTION … SELECT runs the function; with PRAGMA AUTONOMOUS_TRANSACTION its DML commits.</summary>
    [Fact]
    public void A_with_function_body_is_classified()
    {
        var text = "WITH FUNCTION f RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN DELETE FROM t; COMMIT; RETURN 1; END;\nSELECT f() FROM dual\n/";
        Assert.Contains(Ora.Classify(text).Destructive, f => f.Rule == "deleteWithoutWhere");
        var dynamic = "WITH FUNCTION f RETURN NUMBER IS BEGIN EXECUTE IMMEDIATE v; RETURN 1; END;\nSELECT f() FROM dual\n/";
        Assert.Contains(Ora.Classify(dynamic).Destructive, f => f.Rule == "dynamicSql");
    }

    [Fact]
    public void Deeply_nested_execute_immediate_is_a_finding_not_a_crash()
    {
        var delimiters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var inner = "DELETE FROM t WHERE id = 1";
        foreach (var d in delimiters) inner = $"BEGIN EXECUTE IMMEDIATE q'{d}{inner}{d}'; END;";
        Assert.Contains(Bounded(() => Ora.Classify(inner)).Destructive, f => f.Rule == "unparsed");
    }
}

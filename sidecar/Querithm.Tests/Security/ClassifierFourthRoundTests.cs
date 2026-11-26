using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;
using Querithm.Intelligence.Sqlite;
using Querithm.Providers.MySql;
using Querithm.Providers.Sqlite;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 2, code audit 2026-11-25 fourth round (Gemini): MySQL gate mixes, hints in other readings, gated text inside
/// PREPARE, SET STATEMENT / ANALYZE, quoted labels, charset values, executable comments across ';'; PostgreSQL U&amp;"…",
/// string continuation and FOR … IN / OPEN … FOR bodies; Oracle CALL and DBMS_SYS_SQL; SQLite names trigger / begin;
/// DuckDB ANALYSE and nesting.
/// </summary>
public class ClassifierFourthRoundTests
{
    static readonly MySqlLanguageService My = new();
    static readonly PgLanguageService Pg = new();
    static readonly OracleLanguageService Ora = new();
    static readonly SqliteLanguageService Lite = new();
    static readonly DuckDbLanguageService Duck = new();

    static Classification Bounded(Func<Classification> classify)
    {
        var task = Task.Run(classify);
        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "classification did not end within 30 s");
        return task.Result;
    }

    // ------------------------------------------------------------------------------------------------------------ MySQL

    /// <summary>MySQL 8.0 runs /*!80000 and skips /*!99999: a statement made of gates is what that server reads (C1).</summary>
    [Theory]
    [InlineData("/*!99999 EXPLAIN */ /*!80000 DROP TABLE users */", "drop")]
    [InlineData("/*!80000 DELETE FROM users */ /*!99999 WHERE 1 */", "deleteWithoutWhere")]
    [InlineData("SELECT 1 /*!90000 ' */ /*!80000 ; DROP TABLE t; SELECT 1 -- '\n*/ + 1", "drop")]
    // MariaDB runs /*M!50500 (a five-digit MariaDB gate) and skips /*!50700 (MySQL 5.7's).
    [InlineData("/*!50700 EXPLAIN */ /*M!50500 DROP TABLE users */", "drop")]
    public void A_statement_a_real_server_reads_from_a_mix_of_gates_is_found(string text, string rule) =>
        Assert.Contains(My.Classify(text).Destructive, f => f.Rule == rule);

    [Fact]
    public void A_plan_reads_every_gate_version()
    {
        IPlanProvider plans = new MySqlPlans();
        Assert.Equal(MySqlPlans.ReadingRefusal, plans.Refusal("/*!99999 SELECT 1 */ /*!80000 DELETE FROM t */", PlanMode.Estimated));
        Assert.Equal(MySqlPlans.ReadingRefusal, plans.Refusal("SELECT 1 /*!90000 ' */ /*!80000 ; DROP TABLE t; SELECT 1 -- '\n*/ + 1", PlanMode.Estimated));
        Assert.Null(plans.Refusal("SELECT /*!40001 SQL_NO_CACHE */ * FROM t", PlanMode.Actual));
    }

    /// <summary>A hint another reading reveals (C2), and backslash escapes in a hint's quotes (H3).</summary>
    [Theory]
    [InlineData("SELECT \"x\\\" /*+ `*/ ' ` */ ; DROP TABLE t; -- '\"")]
    [InlineData("SELECT /*+ SET_VAR(x='\\' */ ' ) */ 1; DROP TABLE users; -- '")]
    public void A_hint_any_reading_ends_elsewhere_is_a_finding_and_gets_no_plan(string text)
    {
        Assert.Contains(My.Classify(text).Destructive, f => f.Rule is "unparsed" or "drop");
        IPlanProvider plans = new MySqlPlans();
        Assert.NotNull(plans.Refusal(text, PlanMode.Estimated));
    }

    /// <summary>The text a PREPARE holds is read every way too: its unescaped /*!99999 WHERE 1 */ is a gate (C3).</summary>
    [Fact]
    public void A_gated_comment_inside_a_prepared_literal_is_read_both_ways()
    {
        Assert.Contains(My.Classify(@"PREPARE s FROM 'DELETE FROM users /\*!99999 WHERE 1 */'; EXECUTE s;").Destructive, f => f.Rule == "deleteWithoutWhere");
        Assert.Contains(My.Classify("EXECUTE IMMEDIATE 'DELETE FROM users /*!99999 WHERE 1 */';").Destructive, f => f.Rule == "deleteWithoutWhere");
    }

    [Fact]
    public void Prepared_literals_nested_past_the_limit_are_a_finding()
    {
        // Quotes alternate, so the text doubles every second level only (about 2.6 MB at 34 levels).
        var sql = "SELECT 1";
        for (var k = 0; k < 34; k++)
        {
            var q = k % 2 == 0 ? "'" : "\"";
            sql = "EXECUTE IMMEDIATE " + q + sql.Replace(q, q + q) + q;
        }
        Assert.Contains(Bounded(() => My.Classify(sql)).Destructive, f => f.Rule == "unparsed");
    }

    /// <summary>MariaDB's SET STATEMENT … FOR and ANALYZE, and EXPLAIN ANALYZE, run the statement they hold (C4).</summary>
    [Theory]
    [InlineData("SET STATEMENT max_statement_time = 10 FOR DROP TABLE users", "drop")]
    [InlineData("SET STATEMENT max_statement_time = (SELECT 1), sql_mode = '' FOR DELETE FROM users", "deleteWithoutWhere")]
    [InlineData("ANALYZE DELETE FROM users", "deleteWithoutWhere")]
    [InlineData("ANALYZE FORMAT=JSON UPDATE users SET a = 1", "updateWithoutWhere")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM users", "deleteWithoutWhere")]
    public void A_statement_another_statement_runs_is_classified(string text, string rule) =>
        Assert.Contains(My.Classify(text).Destructive, f => f.Rule == rule);

    [Fact]
    public void Set_statement_does_not_hide_load_data_local_and_analyze_table_stays_quiet()
    {
        Assert.Contains("LOAD DATA LOCAL", My.Classify("SET STATEMENT max_statement_time = 10 FOR LOAD DATA LOCAL INFILE '/etc/passwd' INTO TABLE t").Refusal);
        Assert.Empty(My.Classify("ANALYZE TABLE t; ANALYZE NO_WRITE_TO_BINLOG TABLE t;").Destructive);
    }

    [Fact]
    public void A_quoted_label_opens_a_block_that_runs_now() =>
        Assert.Contains(My.Classify("DELIMITER $$\n`lbl`: BEGIN NOT ATOMIC DROP TABLE users; END `lbl` $$").Destructive, f => f.Rule == "drop");

    [Theory]
    [InlineData("SET NAMES `gbk`;")]
    [InlineData("SET character_set_client = 28;")]
    [InlineData("SET character_set_client = ('gbk');")]
    [InlineData("SET character_set_client = CONCAT('g', 'bk');")]
    public void A_client_character_set_in_any_spelling_the_guard_cannot_follow_is_refused(string text) =>
        Assert.Contains("is turned off", My.Classify(text).Refusal);

    [Theory]
    [InlineData("SET NAMES `utf8mb4`;")]
    [InlineData("SET NAMES DEFAULT;")]
    [InlineData("SET character_set_client = @old;")]
    public void A_safe_default_or_restoring_character_set_is_not_refused(string text) => Assert.Null(My.Classify(text).Refusal);

    /// <summary>An executable comment holding a ';' stays open across it (M13): its */ is no statement of its own.</summary>
    [Fact]
    public void An_executable_comment_stays_open_across_a_semicolon()
    {
        var c = My.Classify("/*!50000 SELECT 1; DROP TABLE users; */");
        Assert.Contains(c.Destructive, f => f.Rule == "drop");
        Assert.Equal(2, c.Batches.Length);
    }

    // ------------------------------------------------------------------------------------------------------- PostgreSQL

    [Theory]
    [InlineData("SET U&\"\\0073tandard_conforming_strings\" = off;")]
    [InlineData("SELECT U&\"\\0073et_config\"('standard_conforming_strings', 'off', false);")]
    [InlineData("DO 'BEGIN SET standard_'\n'conforming_strings = off; END';")]
    public void A_change_of_reading_behind_escapes_or_a_continued_string_is_a_finding(string set) =>
        Assert.Contains(Pg.Classify(set + "\nSELECT 'C:\\dir';").Destructive, f => f.Rule == "readingChanged");

    /// <summary>'…' newline '…' is one literal to PostgreSQL (C7).</summary>
    [Fact]
    public void A_do_body_continued_on_the_next_line_is_read_whole() =>
        Assert.Contains(Pg.Classify("DO 'BEGIN '\n'DROP TABLE users; END;';").Destructive, f => f.Rule == "drop");

    /// <summary>FOR r IN DELETE … RETURNING * LOOP and OPEN c FOR … run the query now (C8); the loop body's WHERE is not its.</summary>
    [Theory]
    [InlineData("DO $$ DECLARE r record; BEGIN FOR r IN DELETE FROM users RETURNING * LOOP PERFORM 1 FROM other WHERE true; END LOOP; END $$;")]
    [InlineData("DO $$ DECLARE c refcursor; BEGIN OPEN c FOR DELETE FROM users RETURNING *; END $$;")]
    public void A_query_a_loop_or_cursor_runs_in_a_do_block_is_classified(string text) =>
        Assert.Contains(Pg.Classify(text).Destructive, f => f.Rule == "deleteWithoutWhere");

    [Fact]
    public void Select_for_update_in_a_do_block_is_no_update() =>
        Assert.DoesNotContain(Pg.Classify("DO $$ DECLARE c refcursor; BEGIN OPEN c FOR SELECT * FROM t FOR UPDATE; END $$;").Destructive, f => f.Rule == "updateWithoutWhere");

    // ----------------------------------------------------------------------------------------------------------- Oracle

    [Theory]
    [InlineData("CALL DBMS_UTILITY.EXEC_DDL_STATEMENT('DROP TABLE users')")]
    [InlineData("CALL DBMS_DDL.CREATE_WRAPPED('CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;')")]
    [InlineData("BEGIN DBMS_SYS_SQL.PARSE_AS_USER(c, 'DROP TABLE t', 1, 1); END;")]
    [InlineData("BEGIN UPDATE t SET x = DBMS_SQL.EXECUTE(c) WHERE id = 1; END;")]
    public void A_dynamic_sql_package_call_anywhere_is_dynamic_sql(string text) =>
        Assert.Contains(Ora.Classify(text).Destructive, f => f.Rule == "dynamicSql");

    [Fact]
    public void An_execute_immediate_commit_ends_the_transaction() =>
        Assert.True(Ora.Classify("BEGIN EXECUTE IMMEDIATE 'COMMIT'; END;").EndsTransaction);

    [Fact]
    public void Insert_with_plsql_is_one_unit_and_a_cte_named_function_is_sql()
    {
        Assert.Single(OracleSplitter.Split("INSERT /*+ WITH_PLSQL */ INTO t WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; SELECT f() FROM dual\n/"));
        Assert.Equal(2, OracleSplitter.Split("WITH function AS (SELECT 1 x FROM dual) SELECT * FROM function;\nSELECT 2 FROM dual;").Count);
    }

    [Fact]
    public void A_trailing_double_dash_is_a_comment_not_a_continuation()
    {
        var st = OracleSplitter.Split("SET SERVEROUTPUT ON --\nSELECT 1 FROM dual;");
        Assert.Equal(2, st.Count);
        Assert.Equal(OracleStatementKind.Sql, st[1].Kind);
    }

    // ----------------------------------------------------------------------------------------------------------- SQLite

    /// <summary>trigger and begin are names in SQLite: a table called trigger kept the ';' after it "inside a trigger" (C12).</summary>
    [Theory]
    [InlineData("CREATE TEMP TABLE trigger (begin INT); DROP TABLE users;")]
    [InlineData("CREATE TABLE t (trigger INT, begin INT); DROP TABLE users;")]
    [InlineData("CREATE TRIGGER trg AFTER INSERT ON t WHEN NEW.begin = 1 BEGIN UPDATE u SET a = 1 WHERE id = NEW.id; END; DROP TABLE users;")]
    public void A_name_trigger_or_begin_keeps_no_statement_open(string text)
    {
        var c = Lite.Classify(text);
        Assert.Contains(c.Destructive, f => f.Rule == "drop");
        Assert.Equal(2, c.Batches.Length);
    }

    [Fact]
    public void A_trigger_body_stays_one_statement() =>
        Assert.Single(Lite.Classify("CREATE TRIGGER trg AFTER INSERT ON t BEGIN UPDATE u SET a = CASE WHEN NEW.x = 1 THEN 2 END WHERE id = NEW.id; DELETE FROM v WHERE id = 1; END;").Batches);

    [Theory]
    [InlineData("CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT DEFAULT 'WITHOUT ROWID')", false)]
    [InlineData("CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT) WITHOUT\n  ROWID", true)]
    [InlineData("CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT) STRICT, WITHOUT ROWID", true)]
    [InlineData("CREATE TABLE t (a INTEGER PRIMARY KEY) -- WITHOUT ROWID", false)]
    public void Without_rowid_is_read_from_the_table_options(string sql, bool expected) =>
        Assert.Equal(expected, SqliteMetadata.IsWithoutRowid(sql));

    // ----------------------------------------------------------------------------------------------------------- DuckDB

    [Fact]
    public void Explain_analyse_runs_the_statement_and_analyze_off_does_not()
    {
        Assert.Contains(Duck.Classify("EXPLAIN ANALYSE DELETE FROM users;").Destructive, f => f.Rule == "deleteWithoutWhere");
        Assert.Contains(Duck.Classify("EXPLAIN (ANALYSE) DELETE FROM users;").Destructive, f => f.Rule == "deleteWithoutWhere");
        Assert.NotNull(Duck.Classify("EXPLAIN ANALYSE COPY (SELECT 1) TO 'x.csv';").Refusal);
        Assert.DoesNotContain(Duck.Classify("EXPLAIN (ANALYZE OFF) DELETE FROM users;").Destructive, f => f.Rule == "deleteWithoutWhere");
    }

    [Theory]
    [InlineData("EXPLAIN ANALYZE ")]
    [InlineData("PREPARE p AS ")]
    public void Deep_duckdb_nesting_is_a_finding_and_a_refusal_not_a_crash(string level)
    {
        var text = string.Concat(Enumerable.Repeat(level, 15_000)) + "SELECT 1";
        var c = Bounded(() => Duck.Classify(text));
        Assert.Contains(c.Destructive, f => f.Rule == "unparsed");
        Assert.NotNull(c.Refusal);
    }

    // -------------------------------------------------------------------------------------------------- background read

    /// <summary>M1: a release that throws no longer keeps the read in the table, and a failing read reaches its waiter.</summary>
    [Fact]
    public void A_background_read_ends_cleanly_when_its_release_or_its_work_throws()
    {
        var docId = "bb-" + Guid.NewGuid();
        Assert.Equal("v", BackgroundBuild.Run("doc:t|" + docId, "text", () => "v", _ => { }, _ => { }, release: () => throw new InvalidOperationException("release")));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (BackgroundBuild.Count > 0 && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(10);
        Assert.Throws<InvalidOperationException>(() => BackgroundBuild.Run<string>("doc:t|" + docId + "b", "text", () => throw new InvalidOperationException("work"), _ => { }, _ => { }));
    }
}

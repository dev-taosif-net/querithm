using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;
using Querithm.Providers.MySql;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 2, code audit 2026-11-25 second round, MySQL / MariaDB: version-gated executable comments a server skips,
/// BEGIN as a name, MariaDB's FOR loop and sql_mode=ORACLE blocks, a handler's single-statement body, a client character
/// set the guard cannot follow, and MariaDB's CREATE OR REPLACE TABLE.
/// </summary>
public class MySqlSecondRoundTests
{
    static readonly MySqlLanguageService L = new();

    /// <summary>A server older than 9.99.99 skips /*!99999 … */ and MySQL skips /*M! … */: the quote inside is no quote there.</summary>
    [Theory]
    [InlineData("SELECT 1 /*!99999 ' */; DROP TABLE x; -- '", "drop")]
    [InlineData("SELECT 1 /*M! ' */; DROP TABLE x; -- '", "drop")]
    [InlineData("SELECT 1 /*M!100000 ' */; DROP TABLE x; -- '", "drop")]
    [InlineData("DELETE FROM t /*!99999 WHERE id = 1 */", "deleteWithoutWhere")]
    [InlineData("DELIMITER $$\nSELECT 1 /*!99999 CASE */; DROP TABLE x $$", "drop")]
    [InlineData("DELIMITER $$\nSELECT 1 /*!99999 ( */; DROP TABLE x $$", "drop")]
    // Read as code too: a server new enough runs it.
    [InlineData("/*!50000 DROP TABLE x */", "drop")]
    [InlineData("/*! DROP TABLE x */", "drop")]
    public void A_statement_a_server_reads_past_a_gated_comment_is_found(string text, string rule) =>
        Assert.Contains(L.Classify(text).Destructive, f => f.Rule == rule);

    [Theory]
    [InlineData(PlanMode.Estimated)]
    [InlineData(PlanMode.Actual)]
    public void A_plan_of_a_text_a_gated_comment_splits_differently_is_refused(PlanMode mode)
    {
        IPlanProvider plans = new MySqlPlans();
        Assert.Equal(MySqlPlans.ReadingRefusal, plans.Refusal("SELECT 1 /*!99999 ' */; DROP TABLE x; -- '", mode));
        // mysqldump's gated comments that change no split keep their plan.
        Assert.Null(plans.Refusal("SELECT /*!40001 SQL_NO_CACHE */ * FROM t", mode));
    }

    [Fact]
    public void Mysqldump_gated_comments_stay_quiet()
    {
        var c = L.Classify("/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;\n/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;\nSELECT /*!40001 SQL_NO_CACHE */ * FROM t;");
        Assert.Empty(c.Destructive);
        Assert.Null(c.Refusal);
    }

    /// <summary>BEGIN is not reserved: a table or alias named begin opens no block.</summary>
    [Theory]
    [InlineData("DELIMITER $$\nSELECT * FROM t begin WHERE 1 = 1; DROP TABLE x $$")]
    [InlineData("DELIMITER $$\nUPDATE begin SET a = 1 WHERE id = 1; DROP TABLE x $$")]
    [InlineData("DELIMITER $$\nSELECT CASE WHEN a THEN begin END FROM t; DROP TABLE x $$")]
    [InlineData("DELIMITER $$\nCREATE PROCEDURE p() SELECT * FROM t begin; DROP TABLE x $$")]
    [InlineData("DELIMITER $$\nCREATE TRIGGER trg BEFORE UPDATE ON t FOR EACH ROW SET NEW.a = (SELECT begin FROM u); DROP TABLE x $$")]
    public void A_name_begin_joins_no_statements(string text) =>
        Assert.Contains(L.Classify(text).Destructive, f => f.Rule == "drop");

    [Theory]
    [InlineData("DELIMITER $$\nFOR i IN 1..3 DO DROP TABLE x; END FOR $$", "drop")]
    [InlineData("DELIMITER $$\nlbl: FOR i IN REVERSE 1..3 DO DELETE FROM t; END FOR lbl $$", "deleteWithoutWhere")]
    // sql_mode=ORACLE (MariaDB): BEGIN … END and DECLARE … BEGIN … END run now. Fail closed: their bodies are read.
    [InlineData("DELIMITER /\nBEGIN DELETE FROM t; END;\n/", "deleteWithoutWhere")]
    [InlineData("DELIMITER /\nDECLARE n INT; BEGIN DROP TABLE t; END;\n/", "drop")]
    // A handler's body is one statement; it can run now.
    [InlineData("DELIMITER $$\nBEGIN NOT ATOMIC DECLARE EXIT HANDLER FOR SQLEXCEPTION DROP TABLE x; SELECT 1; END $$", "drop")]
    [InlineData("DELIMITER $$\nBEGIN NOT ATOMIC DECLARE CONTINUE HANDLER FOR SQLSTATE VALUE '23000', NOT FOUND TRUNCATE TABLE x; SELECT 1; END $$", "truncate")]
    public void A_compound_body_that_runs_now_is_classified(string text, string rule) =>
        Assert.Contains(L.Classify(text).Destructive, f => f.Rule == rule);

    [Theory]
    [InlineData("DELIMITER $$\nCREATE PROCEDURE p() BEGIN FOR i IN 1..3 DO DELETE FROM t; END FOR; DECLARE EXIT HANDLER FOR SQLEXCEPTION DROP TABLE u; END $$")]
    [InlineData("DELIMITER $$\nCREATE PROCEDURE p() lbl: BEGIN DELETE FROM t; END lbl $$")]
    [InlineData("DELIMITER $$\nCREATE TRIGGER trg BEFORE UPDATE ON t FOR EACH ROW FOLLOWS other BEGIN DELETE FROM u; END $$")]
    [InlineData("DELIMITER $$\nCREATE FUNCTION f(a INT) RETURNS VARCHAR(10) CHARSET utf8mb4 DETERMINISTIC COMMENT 'x' BEGIN DELETE FROM t; RETURN 'a'; END $$")]
    public void A_stored_program_body_stays_quiet(string text) => Assert.Empty(L.Classify(text).Destructive);

    /// <summary>
    /// The client character set outlives the run (it is the session's), and Querithm always sends UTF-8: after a confirmed
    /// SET NAMES gbk the server read every later run as gbk while the guard read it as UTF-8. Refused now.
    /// </summary>
    [Theory]
    [InlineData("SET NAMES gbk;")]
    [InlineData("SET NAMES 'big5';")]
    [InlineData("SET CHARACTER SET sjis;")]
    [InlineData("SET SESSION character_set_client = gb18030;")]
    [InlineData("/*!40101 SET NAMES cp932 */;")]
    public void A_client_character_set_the_guard_cannot_follow_is_refused(string text) =>
        Assert.Contains("is turned off", L.Classify(text).Refusal);

    [Theory]
    [InlineData("SET NAMES utf8mb4;")]
    [InlineData("SET NAMES latin1;")]
    // mysqldump's closing line (a variable: what the session had before) and DEFAULT stay a finding, not a refusal.
    [InlineData("/*!40101 SET CHARACTER_SET_CLIENT = @OLD_CHARACTER_SET_CLIENT */;")]
    [InlineData("SET NAMES DEFAULT;")]
    public void A_safe_or_restoring_character_set_change_is_not_refused(string text) => Assert.Null(L.Classify(text).Refusal);

    /// <summary>MariaDB's CREATE OR REPLACE TABLE drops the table and its rows first (flagged on PostgreSQL and DuckDB too).</summary>
    [Fact]
    public void Create_or_replace_is_destructive()
    {
        Assert.Contains(L.Classify("CREATE OR REPLACE TABLE t (id INT);").Destructive, f => f.Rule == "alter");
        Assert.Contains(L.Classify("CREATE OR REPLACE VIEW v AS SELECT 1;").Destructive, f => f.Rule == "alter");
        Assert.Empty(L.Classify("CREATE TABLE t (id INT);").Destructive);
    }
}

using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.PgSql;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 2 for the actual plan (completion audit 2026-11-25, M7): the S1 fix merged only the destructive findings of
/// the other readings. An actual plan runs the text inside a transaction Querithm rolls back, and refuses a text that
/// begins or ends one — but that check, and MySQL's read-only check, read the configured reading only. Under
/// NO_BACKSLASH_ESCAPES (or standard_conforming_strings off) a COMMIT or an UPDATE hidden in a string ran for real.
/// </summary>
public class ReadingModePlanTests
{
    static readonly MySqlLanguageService My = new();
    static readonly PgLanguageService Pg = new();

    [Fact]
    public void Mysql_transaction_statements_of_any_reading_count()
    {
        Assert.True(My.Classify("""SELECT 'a\'; COMMIT; -- '""").EndsTransaction);
        Assert.True(My.Classify("""SELECT 'a\'; START TRANSACTION; -- '""").BeginsTransaction);
        Assert.False(My.Classify("""SELECT 'a\\b' FROM t""").EndsTransaction);
    }

    [Fact]
    public void Pg_transaction_statements_of_either_reading_count()
    {
        Assert.True(Pg.Classify("""SELECT 'a\'' ; COMMIT; -- '""").EndsTransaction);
        Assert.True(Pg.Classify("""SELECT 'a\'' ; BEGIN; -- '""").BeginsTransaction);
        Assert.False(Pg.Classify("""SELECT 'C:\dir' FROM t""").EndsTransaction);
    }

    [Fact]
    public void Mysql_actual_plan_refuses_a_text_the_readings_split_differently()
    {
        IPlanProvider plans = new MySqlPlans();
        // Default reading: one SELECT. NO_BACKSLASH_ESCAPES: SELECT 'a\', COMMIT, an UPDATE that commits.
        Assert.NotNull(plans.Refusal("""SELECT 'a\'; COMMIT; UPDATE t SET x = 1 WHERE id = 1; -- '""", PlanMode.Actual));
        // ANSI_QUOTES: "a\" is a name and the UPDATE is outside it.
        Assert.NotNull(plans.Refusal("""SELECT "a\"; UPDATE t SET x = 1 WHERE id = 1; -- " """, PlanMode.Actual));
        // Every reading splits these the same way and finds only SELECTs.
        Assert.Null(plans.Refusal("""SELECT 'a\\b', "x" FROM t""", PlanMode.Actual));
    }

    /// <summary>
    /// Code audit 2026-11-25 (H1 / C1): an estimated plan sends the text too (EXPLAIN FORMAT=JSON + text), and the server
    /// splits it by its own sql_mode, so <c>SELECT 'a\'; DROP TABLE t; -- '</c> ran a DROP under NO_BACKSLASH_ESCAPES with
    /// no transaction around it. The reading check now holds in both modes; only the read-only check is the actual plan's.
    /// </summary>
    [Fact]
    public void Mysql_estimated_plan_refuses_a_text_the_readings_split_differently()
    {
        IPlanProvider plans = new MySqlPlans();
        var why = plans.Refusal("""SELECT 'a\'; DROP TABLE t; -- '""", PlanMode.Estimated);
        Assert.Equal(MySqlPlans.ReadingRefusal, why);
        Assert.DoesNotContain("estimated plan", why, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(plans.Refusal("""SELECT "a\"; UPDATE t SET x = 1 WHERE id = 1; -- " """, PlanMode.Estimated));
        // A write every reading splits the same way gets its estimated plan (EXPLAIN does not run it).
        Assert.Null(plans.Refusal("UPDATE t SET x = 1 WHERE id = 1", PlanMode.Estimated));
        Assert.Null(plans.Refusal("""SELECT 'a\\b', "x" FROM t""", PlanMode.Estimated));
    }

    /// <summary>
    /// Code audit 2026-11-25 (C2): the server knows no client DELIMITER. Under <c>DELIMITER $$</c> the text
    /// <c>SELECT 1; DROP TABLE t $$</c> is one statement to the splitter and two to the server, so EXPLAIN + it ran the DROP
    /// (an implicit commit), in either plan mode.
    /// </summary>
    [Theory]
    [InlineData(PlanMode.Estimated)]
    [InlineData(PlanMode.Actual)]
    public void Mysql_plan_refuses_a_statement_holding_a_semicolon_under_a_client_delimiter(PlanMode mode)
    {
        IPlanProvider plans = new MySqlPlans();
        Assert.Equal(MySqlPlans.DelimiterRefusal, plans.Refusal("DELIMITER $$\nSELECT 1; DROP TABLE t $$", mode));
        Assert.Equal(MySqlPlans.DelimiterRefusal, plans.Refusal("DELIMITER //\nSELECT 1 //\nSELECT 2; DELETE FROM t //", mode));
        Assert.Null(plans.Refusal("DELIMITER $$\nSELECT 1 $$\nSELECT 2 $$", mode));
    }

    [Fact]
    public void Pg_actual_plan_refuses_a_text_the_readings_split_differently()
    {
        IPlanProvider plans = new PostgresPlans();
        Assert.NotNull(plans.Refusal("""SELECT 'a\'' ; UPDATE t SET x = 1 WHERE id = 1; -- '""", PlanMode.Actual));
        Assert.Null(plans.Refusal("""SELECT 'C:\dir' FROM t""", PlanMode.Actual));
    }

    /// <summary>Code audit 2026-11-25 (H1 / C3): Npgsql splits the estimated plan's text by the session's setting too.</summary>
    [Fact]
    public void Pg_estimated_plan_refuses_a_text_the_readings_split_differently()
    {
        IPlanProvider plans = new PostgresPlans();
        var why = plans.Refusal("""SELECT 'a\'' ; UPDATE t SET x = 1 WHERE id = 1; -- '""", PlanMode.Estimated);
        Assert.Equal(PostgresPlans.ReadingRefusal, why);
        Assert.DoesNotContain("estimated plan", why, StringComparison.OrdinalIgnoreCase);
        Assert.Null(plans.Refusal("""SELECT 'C:\dir' FROM t""", PlanMode.Estimated));
        Assert.Null(plans.Refusal("UPDATE t SET x = 1", PlanMode.Estimated));
    }
}

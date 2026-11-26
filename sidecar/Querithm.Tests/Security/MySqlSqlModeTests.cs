using Querithm.Intelligence.MySql;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 2 (the sidecar enforces the destructive guard), completion audit 2026-11-25 S1 / S4: the MySQL classifier
/// read every text with backslash escapes and "…" as strings, whatever the session's sql_mode. With NO_BACKSLASH_ESCAPES
/// (or ANSI_QUOTES) the server reads a statement the guard never saw. Now every reading's destructive statements count.
/// The classifier also kept the text in a field of the shared service, so two runs classified at once could get each
/// other's line numbers.
/// </summary>
public class MySqlSqlModeTests
{
    static readonly MySqlLanguageService L = new();

    [Fact]
    public void A_drop_hidden_behind_a_backslash_is_found_for_no_backslash_escapes()
    {
        var c = L.Classify(@"SELECT 'a\'; DROP TABLE t; -- '");
        var drop = Assert.Single(c.Destructive, f => f.Rule == "drop");
        Assert.Contains("NO_BACKSLASH_ESCAPES", drop.Description);
    }

    [Fact]
    public void A_drop_hidden_in_double_quotes_is_found()
    {
        // "a\" ends at the second quote whenever the backslash is no escape: under ANSI_QUOTES (a name) and under
        // NO_BACKSLASH_ESCAPES (a string). The finding is reported once, with the first reading that shows it.
        var c = L.Classify(@"SELECT ""a\""; DROP TABLE t; -- """);
        var drop = Assert.Single(c.Destructive, f => f.Rule == "drop");
        Assert.Contains("sql_mode has", drop.Description);
    }

    [Fact]
    public void A_server_in_ansi_mode_is_checked_against_the_default_reading_too()
    {
        // A service configured for ANSI_QUOTES still counts what a default-mode server would run.
        var ansi = new MySqlLanguageService(new MySqlLexMode(AnsiQuotes: true));
        Assert.Contains(ansi.Classify(@"SELECT ""x""; DROP TABLE t; -- """).Destructive, f => f.Rule == "drop");
    }

    [Fact]
    public void Ordinary_quotes_and_backslashes_find_nothing()
    {
        Assert.Empty(L.Classify(@"SELECT 'it''s', ""x"", 'C:\\dir\\file', 'tab\there' FROM t WHERE a = 'b';").Destructive);
        Assert.Empty(L.Classify("SELECT 'no backslash or double quote here' FROM t;").Destructive);
    }

    [Fact]
    public void A_finding_every_reading_shares_is_reported_once()
    {
        var c = L.Classify("SELECT \"x\" FROM t;\nDROP TABLE u;");
        Assert.Single(c.Destructive, f => f.Rule == "drop");
    }

    [Fact]
    public void Prepared_text_is_read_without_backslash_escapes_in_that_reading()
    {
        // PREPARE s FROM 'SELECT \'x\'; DROP TABLE t' : with backslash escapes one SELECT; without, the DROP is outside the literal.
        var c = L.Classify(@"PREPARE s FROM 'SELECT 1 WHERE a = \'; DROP TABLE t; -- ';");
        Assert.Contains(c.Destructive, f => f.Rule == "drop");
    }

    /// <summary>
    /// The four readings are uniform, the server's is not: after a SET of sql_mode later statements are read the new way.
    /// A first statement the server reads in its default mode shifts the quotes of every uniform reading by one, so none
    /// of them saw the DROP (probe 2026-11-25). The SET itself is now a finding, in whichever reading sees it.
    /// </summary>
    [Fact]
    public void A_set_of_sql_mode_partway_through_the_script_is_a_finding()
    {
        var c = L.Classify("""SELECT 'x\'y'; SET SESSION sql_mode = CONCAT(@@sql_mode, ',NO_BACKSLASH_ESCAPES');""" + "\n" + """SELECT 'a\'; DROP TABLE zz_nbe; -- ';""");
        var f = Assert.Single(c.Destructive, f => f.Rule == "readingChanged");
        Assert.StartsWith("SET SESSION sql_mode", f.Statement);
        Assert.Equal(1, f.Line);
    }

    [Theory]
    [InlineData("SET sql_mode = 'NO_BACKSLASH_ESCAPES';")]
    [InlineData("SET SESSION sql_mode = 'ANSI_QUOTES';")]
    [InlineData("SET LOCAL sql_mode='';")]
    [InlineData("SET GLOBAL sql_mode = '';")]
    [InlineData("SET PERSIST sql_mode = '';")]
    [InlineData("SET @@sql_mode = '';")]
    [InlineData("SET @@session.sql_mode := '';")]
    [InlineData("SET @@SESSION.`sql_mode` = '';")]
    [InlineData("SET @a = 1, sql_mode = '';")]
    [InlineData("SET STATEMENT sql_mode='NO_BACKSLASH_ESCAPES' FOR SELECT 1;")]
    [InlineData("SET NAMES gbk;")]
    [InlineData("SET CHARACTER SET gbk;")]
    [InlineData("SET CHARSET gbk;")]
    [InlineData("SET character_set_client = gbk;")]
    [InlineData("/*!40101 SET SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;")]
    public void Every_way_to_change_the_reading_is_a_finding(string set)
    {
        var c = L.Classify(set + "\nSELECT 'C:\\dir';");
        Assert.Contains(c.Destructive, f => f.Rule == "readingChanged");
    }

    [Theory]
    [InlineData("SET @x = @@session.sql_mode;\nSELECT 'a\\b';")]   // reads the mode, changes nothing
    [InlineData("SET autocommit = 0;\nSELECT \"x\";")]
    [InlineData("SELECT @@sql_mode, 'a\\b';")]
    // No backslash and no double quote: every reading is the same text, so a change of reading hides nothing.
    [InlineData("SET sql_mode = 'NO_BACKSLASH_ESCAPES';\nSELECT 1;")]
    public void A_text_that_cannot_change_the_reading_stays_quiet(string text) =>
        Assert.DoesNotContain(L.Classify(text).Destructive, f => f.Rule == "readingChanged");

    /// <summary>
    /// Code audit 2026-11-25 (H1): a multibyte client character set whose second byte can be ASCII (gbk, big5, sjis,
    /// cp932, gb18030) changes the reading without any backslash or double quote — the UTF-8 bytes of 丁 end in 0x81, a
    /// gbk lead byte that takes the next backtick as its second byte. Such a change is a finding in any text.
    /// </summary>
    [Theory]
    [InlineData("SET NAMES gbk;\nSELECT `丁` ` ; DROP TABLE t; -- `;")]
    [InlineData("SET NAMES 'big5';\nSELECT 1;")]
    [InlineData("SET CHARACTER SET sjis;\nSELECT 1;")]
    [InlineData("SET CHARSET cp932;\nSELECT 1;")]
    [InlineData("SET SESSION character_set_client = gb18030;\nSELECT 1;")]
    [InlineData("SET @@character_set_client = @cs;\nSELECT 1;")]
    [InlineData("SET NAMES DEFAULT;\nSELECT 1;")]
    public void A_change_to_a_character_set_that_can_swallow_a_quote_is_a_finding_without_backslashes(string text) =>
        Assert.Contains(L.Classify(text).Destructive, f => f.Rule == "readingChanged");

    [Theory]
    [InlineData("SET NAMES utf8mb4;\nSELECT 1;")]
    [InlineData("SET NAMES 'utf8mb4' COLLATE 'utf8mb4_unicode_ci';\nSELECT 1;")]
    [InlineData("/*!40101 SET NAMES utf8 */;\nSELECT 1;")]
    [InlineData("SET character_set_client = latin1;\nSELECT 1;")]
    public void A_change_to_an_ascii_safe_character_set_stays_quiet_without_backslashes(string text) =>
        Assert.DoesNotContain(L.Classify(text).Destructive, f => f.Rule == "readingChanged");

    /// <summary>
    /// Code audit 2026-11-25 (L5): only a backslash makes NO_BACKSLASH_ESCAPES matter and only a double quote ANSI_QUOTES,
    /// so a text with one of them is read the two ways that differ on it; findings stay the same.
    /// </summary>
    [Fact]
    public void A_text_with_only_one_of_the_two_characters_still_finds_what_that_reading_hides()
    {
        Assert.Contains(L.Classify(@"SELECT 'a\'; DROP TABLE t; -- '").Destructive, f => f.Rule == "drop");
        Assert.True(L.Classify(@"SELECT 'a\'; COMMIT; -- '").EndsTransaction);
        // Under ANSI_QUOTES "…" is a name, so PREPARE … FROM it prepares text built at run time.
        Assert.Contains(L.Classify("PREPARE s FROM \"SELECT 1\"; EXECUTE s;").Destructive, f => f.Rule == "dynamicSql");
        var ansi = new MySqlLanguageService(new MySqlLexMode(AnsiQuotes: true));
        Assert.Contains(ansi.Classify(@"SELECT 'a\'; DROP TABLE t; -- '").Destructive, f => f.Rule == "drop");
    }

    [Fact]
    public void Concurrent_classifications_on_the_shared_service_keep_their_own_lines()
    {
        var a = "SELECT 1;\nDROP TABLE a;";            // DROP on line 2
        var b = "SELECT 1;\n\n\n\nDROP TABLE b;";      // DROP on line 5
        Parallel.For(0, 400, i =>
        {
            var (text, line) = i % 2 == 0 ? (a, 2) : (b, 5);
            var drop = Assert.Single(L.Classify(text).Destructive, f => f.Rule == "drop");
            Assert.Equal(line, drop.Line);
        });
    }
}

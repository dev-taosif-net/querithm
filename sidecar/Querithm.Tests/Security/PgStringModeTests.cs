using Querithm.Intelligence.PgSql;

namespace Querithm.Tests.Security;

/// <summary>
/// §13 rule 2, completion audit 2026-11-25 S1 for PostgreSQL: with <c>standard_conforming_strings = off</c> (a session can
/// still set it) a backslash escapes a quote in '…', so a statement can hide from a classifier that reads the standard
/// way. Both readings' destructive statements count now.
/// </summary>
public class PgStringModeTests
{
    static readonly PgLanguageService L = new();

    [Fact]
    public void A_drop_hidden_from_the_standard_reading_is_found()
    {
        // Standard: 'a\'' is the string a\' plus the rest up to the last quote — one SELECT. With escapes: 'a\'' is a', then DROP.
        var c = L.Classify(@"SET standard_conforming_strings = off; SELECT 'a\'' ; DROP TABLE t; -- '");
        var drop = Assert.Single(c.Destructive, f => f.Rule == "drop");
        Assert.Contains("standard_conforming_strings", drop.Description);
    }

    /// <summary>
    /// The two readings are uniform, the server's is not: after <c>SET standard_conforming_strings = off</c> it reads the
    /// later statements with escapes. A first statement read the standard way shifts the other reading's quotes, and
    /// neither reading saw the DROP (probe 2026-11-25). The SET itself is now a finding.
    /// </summary>
    [Fact]
    public void A_change_of_standard_conforming_strings_partway_through_the_script_is_a_finding()
    {
        var c = L.Classify("""SELECT 'p\', ' /*'; SET standard_conforming_strings = off;""" + "\n" + """SELECT 'a\'' ; DROP TABLE zz_scs; -- ';""");
        var f = Assert.Single(c.Destructive, f => f.Rule == "readingChanged");
        Assert.StartsWith("SET standard_conforming_strings", f.Statement);
    }

    [Theory]
    [InlineData("SET standard_conforming_strings = off;")]
    [InlineData("SET SESSION standard_conforming_strings TO off;")]
    [InlineData("SET LOCAL standard_conforming_strings = 'off';")]
    [InlineData("set \"standard_conforming_strings\" = off;")]
    [InlineData("RESET standard_conforming_strings;")]
    [InlineData("RESET ALL;")]
    [InlineData("DISCARD ALL;")]
    [InlineData("SELECT set_config('standard_conforming_strings', 'off', false);")]
    [InlineData("UPDATE pg_settings SET setting = 'off' WHERE name = 'standard_conforming_strings';")]
    [InlineData("DO $$ BEGIN SET standard_conforming_strings = off; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE 'SET standard_conforming_strings = off'; END $$;")]
    [InlineData("DO $$ BEGIN PERFORM set_config('standard_conforming_strings', 'off', false); END $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE sql AS $$ SET standard_conforming_strings = off $$;")]
    public void Every_way_to_change_the_reading_is_a_finding(string set)
    {
        var c = L.Classify(set + "\nSELECT 'C:\\dir';");
        Assert.Contains(c.Destructive, f => f.Rule == "readingChanged");
    }

    /// <summary>Code audit 2026-11-25 (C4 / M5): the ways past the backstop, each a change of reading now (fail closed).</summary>
    [Theory]
    [InlineData("DO 'BEGIN SET standard_conforming_strings = off; END';")]
    [InlineData("DO LANGUAGE plpgsql 'BEGIN PERFORM set_config(''standard_conforming_strings'', ''off'', false); END';")]
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE sql AS 'SELECT set_config(''standard_conforming_strings'',''off'',false)';")]
    [InlineData("CREATE PROCEDURE p() LANGUAGE sql AS 'SET standard_conforming_strings = off';")]
    [InlineData("SELECT \"set_config\"('standard_conforming_strings', 'off', false);")]
    [InlineData("SELECT pg_catalog.\"set_config\"('standard_conforming_strings', 'off', false);")]
    [InlineData("SELECT set_config('standard_' || 'conforming_strings', 'off', false);")]
    [InlineData("SELECT set_config($$standard_conforming_strings$$, 'off', false);")]
    [InlineData("SELECT set_config(name, 'off', false) FROM names;")]
    [InlineData("UPDATE pg_settings SET setting = 'off' WHERE name = 'standard_' || 'conforming_strings';")]
    // Nested deeper than the scan follows: five dollar bodies, the SET in the innermost.
    [InlineData("DO $a$ BEGIN EXECUTE $b$ DO $c$ BEGIN EXECUTE $d$ DO $e$ BEGIN SET standard_conforming_strings = off; END $e$ $d$; END $c$ $b$; END $a$;")]
    // Second round: a Unicode-escaped body is not decoded, so it may change the setting.
    [InlineData("DO U&'BEGIN SET standard_conforming_strings = off; END';")]
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE sql AS U&'SET standard_conforming_strings = off';")]
    public void A_hidden_change_of_the_reading_is_a_finding(string set)
    {
        var c = L.Classify(set + "\nSELECT 'C:\\dir';");
        Assert.Contains(c.Destructive, f => f.Rule == "readingChanged");
    }

    [Theory]
    [InlineData("SELECT set_config('search_path', 'public', false), 'a\\b';")]
    [InlineData("SELECT setting FROM pg_settings WHERE name = 'standard_conforming_strings' AND 'a\\b' <> '';")]
    [InlineData("CREATE FUNCTION f() RETURNS text LANGUAGE sql AS 'SELECT ''a\\b''';")]
    public void A_body_or_call_that_changes_another_setting_stays_quiet(string text) =>
        Assert.DoesNotContain(L.Classify(text).Destructive, f => f.Rule == "readingChanged");

    /// <summary>
    /// Code audit 2026-11-25 second round: <c>U&amp;'…' UESCAPE '!'</c> spells the body with escapes (!0044 is D), which the
    /// classifier does not decode — the DROP ran unseen. The DO block counts as unchecked dynamic SQL now (fail closed).
    /// </summary>
    [Theory]
    [InlineData("DO U&'BEGIN !0044ROP TABLE t; END' UESCAPE '!';")]
    [InlineData("DO LANGUAGE plpgsql U&'BEGIN \\0044ROP TABLE t; END';")]
    public void A_unicode_escaped_do_block_is_unchecked_dynamic_sql(string text) =>
        Assert.Contains(L.Classify(text).Destructive, f => f.Rule == "dynamicSql");

    [Fact]
    public void A_plain_do_block_is_still_read()
    {
        var c = L.Classify("DO 'BEGIN PERFORM 1; END';");
        Assert.DoesNotContain(c.Destructive, f => f.Rule == "dynamicSql");
        // A name ending in u before a string (a column, then &) is no U& literal.
        Assert.DoesNotContain(L.Classify("SELECT mu & 'x' FROM t;").Destructive, f => f.Rule == "dynamicSql");
    }

    /// <summary>Code audit 2026-11-25 (M10): literal values as the server reads them.</summary>
    [Theory]
    [InlineData(@"E'\U80000041'", "\uFFFD")]   // past U+10FFFF: an int overflowed to a negative code read as 'A'
    [InlineData(@"E'\U0001F600'", "\U0001F600")]
    [InlineData(@"E'\x41\u0042'", "AB")]
    [InlineData(@"E'abc\'", "abc'")]           // unclosed: the escaped quote it ends with stays
    [InlineData(@"E'abc\''", "abc'")]
    [InlineData("'it''s'", "it's")]
    [InlineData("'open", "open")]
    public void Literal_values_read_like_the_server(string literal, string value) =>
        Assert.Equal(value, PgLanguageService.LiteralValue(literal, standardStrings: true));

    [Theory]
    [InlineData("SHOW standard_conforming_strings;\nSELECT 'a\\b';")]
    [InlineData("SELECT current_setting('standard_conforming_strings'), 'a\\b';")]
    [InlineData("SET search_path = public;\nSELECT 'a\\b';")]
    // No backslash: both readings are the same text, so a change of reading hides nothing.
    [InlineData("SET standard_conforming_strings = off;\nSELECT 1;")]
    public void A_text_that_cannot_change_the_reading_stays_quiet(string text) =>
        Assert.DoesNotContain(L.Classify(text).Destructive, f => f.Rule == "readingChanged");

    /// <summary>
    /// A DO block's body (and an EXECUTE literal in it) was always tokenized the standard way, so a session with
    /// standard_conforming_strings off ran a DROP the guard read as part of a string (Gemini review 2026-11-25).
    /// </summary>
    [Fact]
    public void A_drop_hidden_in_a_do_block_is_found_in_the_non_standard_reading()
    {
        var c = L.Classify("""DO $$ BEGIN PERFORM 'a\''; DROP TABLE users; -- '; END $$;""");
        var drop = Assert.Single(c.Destructive, f => f.Rule == "drop");
        Assert.Contains("standard_conforming_strings", drop.Description);
    }

    [Fact]
    public void An_execute_literal_is_read_with_the_escapes_of_its_reading()
    {
        // E'…' always escapes: \x3b is a ';', so the literal runs a SELECT and then a DROP.
        Assert.Contains(L.Classify("""DO $$ BEGIN EXECUTE E'SELECT 1\x3b DROP TABLE users'; END $$;""").Destructive, f => f.Rule == "drop");
        // With standard_conforming_strings off, \' in '…' is a quote: the literal is SELECT 'a'; DROP TABLE users.
        var c = L.Classify("""DO $$ BEGIN EXECUTE 'SELECT \'a\'; DROP TABLE users; -- \''; END $$;""");
        Assert.Contains(c.Destructive, f => f.Rule == "drop" && f.Description.Contains("standard_conforming_strings"));
    }

    [Fact]
    public void Escape_strings_and_text_without_backslashes_read_as_before()
    {
        Assert.Empty(L.Classify(@"SELECT E'a\'b', 'C:\dir' FROM t WHERE x = 'it''s';").Destructive);
        Assert.Single(L.Classify("SELECT 1; DROP TABLE t;").Destructive, f => f.Rule == "drop");
    }

    [Fact]
    public void Standard_tokens_keep_backslashes_as_characters_and_the_other_reading_escapes_them()
    {
        Assert.Equal(PgTokenKind.String, PgTokenizer.Tokenize(@"'a\'").Single().Kind);
        // Standard: \ is a character and '' a doubled quote, so the string stays open to the end of the text.
        Assert.Equal(@"'a\'' x", PgTokenizer.Tokenize(@"'a\'' x")[0].Text);
        // standard_conforming_strings off: \' is the quote, and the next ' closes the string.
        Assert.Equal(@"'a\''", PgTokenizer.Tokenize(@"'a\'' x", standardStrings: false)[0].Text);
    }
}

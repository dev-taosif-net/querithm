using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests.Security;

/// <summary>
/// L7: literals rendered into preview, sync and migration scripts. A value must end its literal exactly where the
/// renderer meant on every server setting (PostgreSQL's standard_conforming_strings, MySQL's NO_BACKSLASH_ESCAPES), and
/// only a plain number is ever written bare.
/// </summary>
public class LiteralRenderingTests
{
    static GridValue V(string text) => new() { Text = text };

    /// <summary>
    /// Where a PostgreSQL string literal starting at <paramref name="start"/> ends, read the way the server reads it:
    /// E'…' always takes backslash escapes; '…' takes them only when standard_conforming_strings is off.
    /// </summary>
    static int PgLiteralEnd(string sql, int start, bool standardConforming)
    {
        var escapes = !standardConforming;
        if (sql[start] is 'E' or 'e') { escapes = true; start++; }
        Assert.Equal('\'', sql[start]);
        for (var i = start + 1; i < sql.Length; i++)
        {
            if (escapes && sql[i] == '\\') { i++; continue; }
            if (sql[i] != '\'') continue;
            if (i + 1 < sql.Length && sql[i + 1] == '\'') { i++; continue; }
            return i;
        }
        return -1;
    }

    public static TheoryData<string> Hostile => new() { "a\\'; DROP TABLE t; --", "\\", "it's", "C:\\temp\\new", "\\\\'' ''\\", "plain" };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Postgres_preview_literals_end_where_meant_whatever_standard_conforming_strings_is(string text)
    {
        var lit = new PostgresDataEdit().PreviewLiteral("text", V(text));
        foreach (var scs in new[] { true, false })
            Assert.Equal(lit.Length - 1, PgLiteralEnd(lit, 0, scs));
        Assert.Equal(text.Contains('\\'), lit.StartsWith("E'", StringComparison.Ordinal));
    }

    [Fact]
    public void Postgres_bytea_preview_keeps_its_hex_input_form()
    {
        // 0x… in the grid is \x… on input: an escape string, read the same in both modes.
        Assert.Equal("E'\\\\xdeadbeef'", new PostgresDataEdit().PreviewLiteral("bytea", V("0xdeadbeef")));
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Postgres_migration_literals_end_where_meant_whatever_standard_conforming_strings_is(string label)
    {
        var sql = new PostgresSchemaCompare().CreateType(new ModelType { Schema = "public", Name = "e", Kind = "enum", Values = [label] });
        var start = sql.IndexOf('(') + 1;
        foreach (var scs in new[] { true, false })
            Assert.Equal(sql.LastIndexOf(')') - 1, PgLiteralEnd(sql, start, scs));
    }

    public static TheoryData<string, string, string> SqlServerNumbers => new()
    {
        { "float", "NaN", "'NaN'" }, { "float", "Infinity", "'Infinity'" }, { "float", "-Infinity", "'-Infinity'" }, { "real", "∞", "'∞'" },
        { "float", "1e5", "1e5" }, { "float", "-1.5E-3", "-1.5E-3" }, { "int", "1e5", "'1e5'" }, { "decimal", "12.5", "12.5" },
        { "int", " 42 ", "42" }, { "int", "١٢", "'١٢'" }, { "int", "1,000", "'1,000'" }, { "money", "-.5", "-.5" }, { "bit", "1", "1" },
        { "int", "1; DROP TABLE t", "'1; DROP TABLE t'" }, { "float", "1e", "'1e'" }, { "int", "+", "'+'" },
    };

    [Theory]
    [MemberData(nameof(SqlServerNumbers))]
    public void Sql_server_writes_only_plain_ascii_numbers_bare(string type, string text, string expected) =>
        Assert.Equal(expected, new SqlServerDataEdit().PreviewLiteral(type, V(text)));

    [Fact]
    public void Mysql_text_with_a_backslash_is_hex_so_no_backslash_escapes_setting_changes_it()
    {
        var d = new MySqlDataEdit();
        Assert.Equal("X'" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("a\\'; --")) + "'", d.PreviewLiteral("varchar(20)", V("a\\'; --")));
        Assert.Equal("'it''s'", d.PreviewLiteral("varchar(20)", V("it's")));
        Assert.Equal("'NaN'", d.PreviewLiteral("double", V("NaN")));
        Assert.Equal("'Infinity'", d.PreviewLiteral("int", V("Infinity")));
    }

    [Fact]
    public void Sqlite_literals_take_no_backslash_escapes_and_never_a_bare_number()
    {
        var d = new SqliteDataEdit();
        Assert.Equal("'a\\''; --'", d.PreviewLiteral("TEXT", V("a\\'; --")));
        Assert.Equal("'NaN'", d.PreviewLiteral("REAL", V("NaN")));
        Assert.Equal("'Infinity'", d.PreviewLiteral("INTEGER", V("Infinity")));
    }

    [Fact]
    public void Duckdb_literals_take_no_backslash_escapes_and_never_a_bare_number()
    {
        var d = new DuckDbDataEdit();
        Assert.Equal("'a\\''; --'", d.PreviewLiteral("VARCHAR", V("a\\'; --")));
        Assert.Equal("'NaN'", d.PreviewLiteral("DOUBLE", V("NaN")));
        Assert.Equal("'-Infinity'", d.PreviewLiteral("BIGINT", V("-Infinity")));
    }
}

using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// 8a review: a statement history kept with its secret masked must not run when the user re-runs it — the old
/// <c>'***'</c> mask was a literal, so <c>ALTER ROLE app PASSWORD '***'</c> set the password to <c>***</c>. Here the masked
/// text goes to each in-process engine (and SQL Server's own parser): every one refuses it as a syntax error. The server
/// engines run against the Docker fixture in RedactedTextIntegrationTests.
/// </summary>
public class RedactedTextFileEngineTests
{
    [Theory]
    [InlineData("ALTER LOGIN sa WITH PASSWORD = 'Hunter2!'")]
    [InlineData("ALTER LOGIN app WITH PASSWORD = N'n3w' OLD_PASSWORD = N'0ld'")]
    [InlineData("CREATE LOGIN app WITH PASSWORD = 0x0200A1B2 HASHED")]
    [InlineData("EXEC sp_setapprole 'r', 'ar1'")]
    [InlineData("DECLARE @pwd nvarchar(50) = N'v4r'; SET @password='v5r';")]
    [InlineData("SELECT ENCRYPTBYPASSPHRASE('phr', 'data')")]
    public void Sql_server_refuses_the_masked_statement(string sql)
    {
        var masked = new TSqlLanguageService().RedactSecrets(sql).Text;
        Assert.Contains("<redacted>", masked);
        new TSql160Parser(true).Parse(new StringReader(masked), out var errors);
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("PRAGMA key = 'k3y'")]
    [InlineData("PRAGMA main.rekey('n3w')")]
    [InlineData("UPDATE users SET password='pw1' WHERE id = 1")]
    public void Sqlite_refuses_the_masked_statement(string sql)
    {
        var masked = new SqliteLanguageService().RedactSecrets(sql).Text;
        Assert.Contains("<redacted>", masked);
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using (var setup = c.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, password TEXT); INSERT INTO users VALUES (1, 'old');";
            setup.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = masked;
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("syntax error", ex.Message);
        cmd.CommandText = "SELECT password FROM users WHERE id = 1";
        Assert.Equal("old", cmd.ExecuteScalar());
    }

    [Theory]
    [InlineData("CREATE SECRET (TYPE s3, KEY_ID 'AKIAEXAMPLE', SECRET 'wJalr', REGION 'eu-west-1')")]
    [InlineData("CREATE SECRET (TYPE postgres, HOST 'h', PASSWORD 'pg1')")]
    [InlineData("UPDATE users SET password='pw1' WHERE id = 1")]
    public void Duckdb_refuses_the_masked_statement(string sql)
    {
        var masked = new DuckDbLanguageService().RedactSecrets(sql).Text;
        Assert.Contains("<redacted>", masked);
        using var c = new DuckDBConnection("Data Source=:memory:");
        c.Open();
        using (var setup = c.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, password VARCHAR); INSERT INTO users VALUES (1, 'old');";
            setup.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = masked;
        var ex = Assert.ThrowsAny<Exception>(() => cmd.ExecuteNonQuery());
        Assert.Contains("syntax error", ex.Message, StringComparison.OrdinalIgnoreCase);
        cmd.CommandText = "SELECT password FROM users WHERE id = 1";
        Assert.Equal("old", cmd.ExecuteScalar());
    }
}

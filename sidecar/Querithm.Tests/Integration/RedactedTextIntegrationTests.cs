using MySqlConnector;
using Npgsql;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.PgSql;

namespace Querithm.Tests.Integration;

/// <summary>
/// 8a review against the Docker fixture (QUERITHM_IT=1): the text history keeps for a statement with a secret is refused
/// by the server as a syntax error, so re-running it from history never sets a password to the mask (the old <c>'***'</c>
/// did). Only the parse is exercised: the statements name accounts that need not exist.
/// </summary>
public sealed class RedactedTextIntegrationTests
{
    [IntegrationTheory]
    [InlineData("ALTER ROLE querithm_no_such_role PASSWORD 'Hunter2!' VALID UNTIL '2027-01-01'")]
    [InlineData("CREATE USER MAPPING FOR u SERVER s OPTIONS (user 'remote', password 'fdw1')")]
    [InlineData("SELECT crypt('pw1', 'salt'), pgp_sym_encrypt('data', 'k3y')")]
    [InlineData("CREATE ROLE r PASSWORD $$dollar$$")]
    public async Task Postgres_refuses_the_masked_statement(string sql)
    {
        var masked = new PgLanguageService().RedactSecrets(sql).Text;
        Assert.Contains("<redacted>", masked);
        await using var c = new NpgsqlConnection($"Host={TestHost.PgHost};Port=54329;Username=postgres;Password={TestHost.PgPassword};Database=postgres");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = masked;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.SyntaxError, ex.SqlState);
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Mysql_and_mariadb_refuse_the_masked_statement(string product)
    {
        var language = new MySqlLanguageService();
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={MySqlIntegrationTests.Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False;AllowUserVariables=true");
        await c.OpenAsync();
        foreach (var sql in new[]
                 {
                     "ALTER USER 'querithm_no_such_user'@'%' IDENTIFIED BY 'Hunter2!'",
                     "SET PASSWORD FOR 'querithm_no_such_user'@'%' = 'pw9'",
                     "SET @pwd='v1'",
                     "SELECT AES_ENCRYPT('data', 'k3y')",
                 })
        {
            var masked = language.RedactSecrets(sql).Text;
            Assert.Contains("<redacted>", masked);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = masked;
            var ex = await Assert.ThrowsAsync<MySqlException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(MySqlErrorCode.ParseError, ex.ErrorCode);
        }
    }
}

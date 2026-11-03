using Microsoft.Data.SqlClient;
using Npgsql;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>What each engine's provider builds from a profile, and how it words transactions and session options.</summary>
public class ProviderConnectionBuildTests
{
    static Profile P(Engine e, Action<Profile>? _ = null) => new() { Id = "x", Name = "n", Engine = e, Host = "db.example.com", Port = 1234, Username = "alice", DatabaseDefault = "main" };

    [Theory]
    [InlineData(TlsMode.Strict, "Strict")]
    [InlineData(TlsMode.Optional, "False")]
    [InlineData(TlsMode.Disable, "False")]
    [InlineData(TlsMode.Mandatory, "True")]
    [InlineData(TlsMode.Default, "True")]
    public void SqlServer_tls_mode_maps_to_the_driver_encrypt_option(TlsMode mode, string expected)
    {
        var cs = new SqlServerProvider().BuildConnectionString(P(Engine.Mssql) with { Auth = AuthKind.Sql, TlsMode = mode }, "pw", null);
        Assert.Equal(expected, new SqlConnectionStringBuilder(cs).Encrypt.ToString());
    }

    [Fact]
    public void SqlServer_credentials_options_and_database_end_up_in_the_connection_string()
    {
        var profile = P(Engine.Mssql) with
        {
            Auth = AuthKind.Sql, TrustCert = true,
            Options = new Dictionary<string, string> { ["hostNameInCertificate"] = "*.example.com", ["applicationIntent"] = "readOnly", ["multiSubnetFailover"] = "true" },
        };
        var b = new SqlConnectionStringBuilder(new SqlServerProvider().BuildConnectionString(profile, "s3cret", "other"));
        Assert.Equal("alice", b.UserID);
        Assert.Equal("s3cret", b.Password);
        Assert.Equal("other", b.InitialCatalog);
        Assert.Equal("*.example.com", b.HostNameInCertificate);
        Assert.Equal(ApplicationIntent.ReadOnly, b.ApplicationIntent);
        Assert.True(b.MultiSubnetFailover);
        Assert.True(b.TrustServerCertificate);
        Assert.False(b.Pooling);   // Core pools connections itself
    }

    [Theory]
    [InlineData(AuthKind.Windows, true, SqlAuthenticationMethod.NotSpecified)]
    [InlineData(AuthKind.Kerberos, true, SqlAuthenticationMethod.NotSpecified)]
    [InlineData(AuthKind.EntraInteractive, false, SqlAuthenticationMethod.ActiveDirectoryInteractive)]
    [InlineData(AuthKind.EntraDeviceCode, false, SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow)]
    [InlineData(AuthKind.EntraToken, false, SqlAuthenticationMethod.NotSpecified)]
    public void SqlServer_authentication_kinds(AuthKind auth, bool integrated, SqlAuthenticationMethod method)
    {
        var b = new SqlConnectionStringBuilder(new SqlServerProvider().BuildConnectionString(P(Engine.Mssql) with { Auth = auth }, "token-or-pw", null));
        Assert.Equal(integrated, b.IntegratedSecurity);
        Assert.Equal(method, b.Authentication);
        Assert.DoesNotContain("token-or-pw", b.ConnectionString);   // tokens travel on the connection, never in the string
    }

    [Theory]
    [InlineData(TlsMode.Disable, "Disable")]
    [InlineData(TlsMode.Prefer, "Prefer")]
    [InlineData(TlsMode.Optional, "Prefer")]
    [InlineData(TlsMode.VerifyCa, "VerifyCA")]
    [InlineData(TlsMode.VerifyFull, "VerifyFull")]
    [InlineData(TlsMode.Default, "Require")]
    [InlineData(TlsMode.Mandatory, "Require")]
    public void Postgres_tls_mode_maps_to_ssl_mode(TlsMode mode, string expected)
    {
        var cs = new PostgresProvider().BuildConnectionString(P(Engine.Pg) with { Auth = AuthKind.Password, TlsMode = mode }, "pw", null);
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(cs).SslMode.ToString());
    }

    [Fact]
    public void Postgres_credentials_ca_and_options_end_up_in_the_connection_string()
    {
        var profile = P(Engine.Pg) with
        {
            Auth = AuthKind.Password, CaPath = "/etc/ca.pem", TlsMode = TlsMode.VerifyFull, TimeoutS = 7, CommandTimeoutS = 90,
            Options = new Dictionary<string, string> { ["appName"] = "etl" },
        };
        var b = new NpgsqlConnectionStringBuilder(new PostgresProvider().BuildConnectionString(profile, "s3cret", "other"));
        Assert.Equal("alice", b.Username);
        Assert.Equal("s3cret", b.Password);
        Assert.Equal("other", b.Database);
        Assert.Equal("/etc/ca.pem", b.RootCertificate);
        Assert.Equal(7, b.Timeout);
        Assert.Equal(90, b.CommandTimeout);
        Assert.Equal("etl", b.ApplicationName);
        Assert.False(b.Pooling);
        var kerberos = new NpgsqlConnectionStringBuilder(new PostgresProvider().BuildConnectionString(
            P(Engine.Pg) with { Auth = AuthKind.Kerberos, Options = new Dictionary<string, string> { ["kerberosServiceName"] = "pgsvc" } }, "ignored", null));
        Assert.Equal("pgsvc", kerberos.KerberosServiceName);
        Assert.True(string.IsNullOrEmpty(kerberos.Password));
    }

    [Fact]
    public void Statistics_options_wrap_the_batch_on_sql_server_and_do_nothing_on_postgres()
    {
        var ms = new SqlServerProvider();
        Assert.Equal((null, null), ms.SessionOptionsSql(new ExecuteOptions()));
        var (before, after) = ms.SessionOptionsSql(new ExecuteOptions { StatisticsIo = true, StatisticsTime = true, ActualPlan = true });
        Assert.Equal("SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;", before);
        Assert.Equal("SET STATISTICS IO OFF; SET STATISTICS TIME OFF; SET STATISTICS XML OFF;", after);
        Assert.Equal((null, null), new PostgresProvider().SessionOptionsSql(new ExecuteOptions { StatisticsIo = true, StatisticsTime = true }));
    }

    static async Task<List<string>> Statements(ITransactionControl tx)
    {
        var seen = new List<string>();
        var c = new Fakes.FakeDbConnection((cmd, _) => { seen.Add(cmd.CommandText); return Task.FromResult(Fakes.FakeResult.None); });
        await tx.BeginAsync(c, default);
        await tx.SavepointAsync(c, "q_sp", default);
        await tx.RollbackToSavepointAsync(c, "q_sp", default);
        await tx.ReleaseSavepointAsync(c, "q_sp", default);
        await tx.CommitAsync(c, default);
        await tx.RollbackAsync(c, default);
        seen.Add(await tx.BeginReadOnlyAsync(c, default) ? "<read only>" : "<no read only>");
        return seen;
    }

    [Fact]
    public async Task Transaction_statements_commit_strictly_and_roll_back_idempotently()
    {
        // Phase D0b: one provider-owned API. A commit fails when the server already ended the transaction (never reported
        // as committed); a rollback is a no-op when nothing is open.
        Assert.Equal(["BEGIN TRANSACTION", "SAVE TRANSACTION q_sp", "ROLLBACK TRANSACTION q_sp", "COMMIT TRANSACTION", "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", "<no read only>"],
            await Statements(new SqlServerProvider().Transactions));
        Assert.Equal(["BEGIN", "SAVEPOINT q_sp", "ROLLBACK TO SAVEPOINT q_sp", "RELEASE SAVEPOINT q_sp", "COMMIT", "ROLLBACK", "BEGIN TRANSACTION READ ONLY", "<read only>"],
            await Statements(new PostgresProvider().Transactions));
    }

    [Theory]
    [InlineData("x; DROP TABLE t")]
    [InlineData("")]
    [InlineData("1sp")]
    public async Task Savepoint_names_are_never_quoted_into_sql(string name)
    {
        var c = new Fakes.FakeDbConnection((_, _) => Task.FromResult(Fakes.FakeResult.None));
        await Assert.ThrowsAsync<ArgumentException>(() => new SqlServerProvider().Transactions.SavepointAsync(c, name, default));
        await Assert.ThrowsAsync<ArgumentException>(() => new PostgresProvider().Transactions.RollbackToSavepointAsync(c, name, default));
    }

    [Fact]
    public void Transaction_traits_describe_each_engine()
    {
        var ms = new SqlServerProvider().Transactions.Traits;
        var pg = new PostgresProvider().Transactions.Traits;
        Assert.True(ms.AutocommitDefault && pg.AutocommitDefault);
        Assert.False(ms.DdlCommitsImplicitly || pg.DdlCommitsImplicitly); // both have transactional DDL
        Assert.Equal((false, true), (ms.ReleaseSavepoint, pg.ReleaseSavepoint));
        Assert.Equal((false, true), (ms.ReadOnlyTransactions, pg.ReadOnlyTransactions));
        Assert.Equal((false, true), (ms.FailedStatementAbortsTransaction, pg.FailedStatementAbortsTransaction));
    }

    [Fact]
    public async Task Postgres_connections_are_bound_to_one_database()
    {
        var pg = new PostgresProvider();
        Assert.True(pg.DatabaseScopedConnections);
        Assert.False(new SqlServerProvider().DatabaseScopedConnections);
        await Assert.ThrowsAsync<NotSupportedException>(() => pg.ChangeDatabaseAsync(new NpgsqlConnection(), "other", CancellationToken.None));
    }

    [Fact]
    public void Exceptions_are_translated_to_provider_messages_without_leaking_types()
    {
        var generic = new InvalidOperationException("kaput");
        var ms = Assert.Single(new SqlServerProvider().TranslateException(generic));
        var pg = Assert.Single(new PostgresProvider().TranslateException(generic));
        Assert.Equal("kaput", ms.Message);
        Assert.Equal("kaput", pg.Message);
        Assert.Equal(Severity.Error, ms.Severity);
        Assert.Equal(Severity.Error, pg.Severity);
        var withInner = new NpgsqlException("outer", new IOException("socket closed"));
        Assert.Equal("outer socket closed", Assert.Single(new PostgresProvider().TranslateException(withInner)).Message);
    }

    [Fact]
    public void Tunnel_routing_refuses_what_one_ssh_forward_cannot_carry()
    {
        var ms = new SqlServerProvider();
        var pg = new PostgresProvider();
        var (msHost, msPort, msRefusal) = ms.TunnelTarget(P(Engine.Mssql) with { Host = "tcp:db.example.com", Port = 1500 });
        Assert.Null(msRefusal);
        Assert.Equal(1500, msPort);
        Assert.DoesNotContain("tcp:", msHost);
        var (pgHost, pgPort, pgRefusal) = pg.TunnelTarget(P(Engine.Pg));
        Assert.Null(pgRefusal);
        Assert.Equal(("db.example.com", 1234), (pgHost, pgPort));
        Assert.NotNull(pg.TunnelTarget(P(Engine.Pg) with { Host = "a.example.com,b.example.com" }).Refusal);
        Assert.NotNull(pg.TunnelTarget(P(Engine.Pg) with { Host = "/var/run/postgresql" }).Refusal);
        var (routed, _) = pg.RouteThrough(P(Engine.Pg), "127.0.0.1", 40000);
        Assert.Equal("127.0.0.1", routed.Host);
        Assert.Equal(40000, routed.Port);
        var (msRouted, _) = ms.RouteThrough(P(Engine.Mssql), "127.0.0.1", 40001);
        Assert.Contains("127.0.0.1", msRouted.Host);
        Assert.Equal(40001, msRouted.Port);
    }
}

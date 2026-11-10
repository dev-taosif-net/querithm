using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Core.Logging;
using Querithm.Intelligence.Oracle;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Security;

/// <summary>
/// D4 S02, §13 rule 7 for Oracle: the sidecar re-classifies the text it is sent and blocks a destructive Oracle statement on
/// a Prod profile unless <c>confirmedDestructive</c> is set, whatever the UI did. SQL*Plus client commands other than
/// <c>SET SERVEROUTPUT</c> are hard refusals no flag lifts. No server is needed: the guard runs before anything dials.
/// D4 S03, §13 rules 1 and 2: TCPS with the certificate and server name checked is the default and every weaker mode is
/// explicit; the driver's secret-bearing keywords (Password, Proxy Password, Wallet_Password) are masked in logs; a
/// pasted string's wallet, proxy, token and TNS settings never reach a connection.
/// </summary>
public sealed class OracleSecurityTests
{
    static async Task Run(Func<RpcServer, string, Task> body)
    {
        var dir = Directory.CreateTempSubdirectory("qora-sec-").FullName;
        var registry = new ProviderRegistry(EngineSetup.Providers(dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle"));
        await using (var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, registry, dir))
        {
            var rpc = new RpcServer(services, () => { });
            var saved = await rpc.ProfileSave(new Profile
            {
                Id = "", Name = "ora-prod", Engine = Engine.Oracle, Host = "db.invalid", Port = 1521, Auth = AuthKind.Password, Username = "u",
                Environment = EnvironmentKind.Prod,
            });
            var session = await rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = "pw" });
            await body(rpc, session.SessionId);
        }
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("DROP TABLE t PURGE", "drop")]
    [InlineData("drop table t cascade constraints", "drop")]
    [InlineData("TRUNCATE TABLE t", "truncate")]
    [InlineData("DELETE FROM t", "deleteWithoutWhere")]
    [InlineData("DELETE t -- WHERE id = 1", "deleteWithoutWhere")]
    [InlineData("UPDATE t SET v = 0;", "updateWithoutWhere")]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.v = s.v", "merge")]
    [InlineData("ALTER TABLE t DROP COLUMN v", "alterDrop")]
    [InlineData("PURGE RECYCLEBIN", "purge")]
    [InlineData("SELECT 'a;b' FROM dual;\nDELETE FROM t;", "deleteWithoutWhere")]
    [InlineData("BEGIN\n  EXECUTE IMMEDIATE 'DROP TABLE t';\nEND;\n/", "drop")]
    [InlineData("BEGIN\n  EXECUTE IMMEDIATE v_sql;\nEND;\n/", "dynamicSql")]
    [InlineData("BEGIN\n  DELETE FROM t;\nEND;\n/", "deleteWithoutWhere")]
    public async Task A_destructive_statement_is_blocked_without_confirmedDestructive(string sql, string rule)
    {
        await Run(async (rpc, sid) =>
        {
            var first = await rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql });
            Assert.Equal(GuardReason.ProdFirstRun, first.Blocked?.Reason);
            var second = await rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql, Options = new ExecuteOptions { ConfirmedProd = true } });
            Assert.Equal(GuardReason.Destructive, second.Blocked?.Reason);
            Assert.Contains(rule, second.Blocked!.Findings.Select(f => f.Rule));
            Assert.Null(second.ExecutionId);
        });
    }

    [Theory]
    [InlineData("SPOOL out.txt")]
    [InlineData("@install.sql")]
    [InlineData("HOST rm -rf /")]
    [InlineData("SELECT * FROM t WHERE id = &id")]
    [InlineData("CONNECT sys/pw AS SYSDBA")]
    public async Task Client_commands_are_refused_even_with_every_flag(string sql)
    {
        await Run(async (rpc, sid) =>
        {
            var ex = await Assert.ThrowsAsync<QuerithmException>(() => rpc.QueryExecute(new QueryExecuteParams
            {
                SessionId = sid, Sql = sql, Options = new ExecuteOptions { ConfirmedProd = true, ConfirmedDestructive = true },
            }));
            Assert.Equal("refused", ex.Code);
        });
    }

    [Fact]
    public async Task A_confirmed_destructive_statement_passes_the_guard()
    {
        await Run(async (rpc, sid) =>
        {
            var r = await rpc.QueryExecute(new QueryExecuteParams
            {
                SessionId = sid, Sql = "DROP TABLE t PURGE", Options = new ExecuteOptions { ConfirmedProd = true, ConfirmedDestructive = true },
            });
            Assert.Null(r.Blocked);
            Assert.NotNull(r.ExecutionId); // it then fails to connect (D4 S03); the guard let it through
        });
    }

    [Fact]
    public void Identified_by_passwords_are_redacted_quoted_or_not()
    {
        var lang = new OracleLanguageService();
        Assert.DoesNotContain("s3cret", lang.RedactSecrets("CREATE USER app IDENTIFIED BY \"s3cret-Value\"").Text);
        Assert.DoesNotContain("s3cretWord", lang.RedactSecrets("ALTER USER app IDENTIFIED BY s3cretWord REPLACE oldS3cret").Text);
        Assert.DoesNotContain("oldS3cret", lang.RedactSecrets("ALTER USER app IDENTIFIED BY s3cretWord REPLACE oldS3cret").Text);
        Assert.DoesNotContain("linkPw1", lang.RedactSecrets("CREATE DATABASE LINK l CONNECT TO u IDENTIFIED BY linkPw1 USING 'svc'").Text);
        var kept = lang.RedactSecrets("SELECT 'IDENTIFIED BY x' FROM dual");
        Assert.Equal("SELECT 'IDENTIFIED BY x' FROM dual", kept.Text);
    }

    [Fact]
    public void A_new_profile_connects_over_tcps_with_the_certificate_and_name_checked()
    {
        var dir = Directory.CreateTempSubdirectory("qora-tls-").FullName;
        try
        {
            var p = new Profile { Id = "p", Name = "ora", Engine = Engine.Oracle, Host = "db.example.test", Auth = AuthKind.Password, Username = "u", DatabaseDefault = "S" };
            Assert.Equal(TlsMode.Default, p.TlsMode);
            var ds = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(new OracleProvider(dir).BuildConnectionString(p, "pw", null)).DataSource;
            Assert.Contains("(PROTOCOL=TCPS)", ds);
            Assert.Contains("(SSL_SERVER_DN_MATCH=TRUE)", ds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("User Id=app;Password=fake-ora-pw-1;Data Source=//h/S", "fake-ora-pw-1")]
    [InlineData("Proxy User Id=p;Proxy Password=fake-ora-proxy-2", "fake-ora-proxy-2")]
    [InlineData("WALLET_LOCATION=C:\\w;WALLET_PASSWORD=fake-ora-wallet-3", "fake-ora-wallet-3")]
    [InlineData("Wallet Password=fake-ora-wallet-4", "fake-ora-wallet-4")]
    public void Oracle_secret_keywords_are_masked_in_logs(string text, string secret) =>
        Assert.DoesNotContain(secret, LogScrubber.Scrub($"open failed: {text}"));

    [Fact]
    public void A_built_connection_string_logged_by_mistake_shows_no_password()
    {
        var dir = Directory.CreateTempSubdirectory("qora-log-").FullName;
        try
        {
            var p = new Profile { Id = "p", Name = "ora", Engine = Engine.Oracle, Host = "h", Auth = AuthKind.Password, Username = "u", DatabaseDefault = "S" };
            var cs = new OracleProvider(dir).BuildConnectionString(p, "fake ora;spaced=pw", null);
            Assert.DoesNotContain("fake ora", LogScrubber.Scrub(cs));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("BEGIN wallet_password := 'fake-ora-w1'; END;", "fake-ora-w1")]
    [InlineData("BEGIN v_pwd := 'fake-ora-w2'; END;", "fake-ora-w2")]
    public void Oracle_secret_names_and_credential_literals_are_redacted_in_sql(string sql, string secret) =>
        Assert.DoesNotContain(secret, new OracleLanguageService().RedactSecrets(sql).Text);

    [Fact]
    public void A_pasted_proxy_or_wallet_password_is_dropped_not_kept_anywhere()
    {
        var r = new OracleProvider().ParseConnectionString("Data Source=//h/S;User Id=app;Password=fake-main;Proxy User Id=p;Proxy Password=fake-proxy;WALLET_PASSWORD=fake-wallet",
            new Profile { Id = "", Name = "x", Engine = Engine.Oracle, Auth = AuthKind.Password });
        Assert.Equal("fake-main", r.Secret);
        var rest = System.Text.Json.JsonSerializer.Serialize(new { r.Profile, r.Unknown, r.NotApplied, r.Notes });
        Assert.DoesNotContain("fake-proxy", rest);
        Assert.DoesNotContain("fake-wallet", rest);
        Assert.DoesNotContain("fake-main", rest);
    }
}

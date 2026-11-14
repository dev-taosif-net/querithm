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
/// D4 S10, §13 rule 9 (untrusted input, generated SQL): grid patches name only the recorded table's columns, quoted by the
/// Oracle quoting service (a name holding <c>"</c> is refused: Oracle has no escape for it); every value is a bound
/// parameter, and the preview's literals double the quote and take no backslash escapes.
/// </summary>
public sealed class OracleSecurityTests
{
    static async Task Run(Func<RpcServer, string, Task> body)
    {
        var dir = Directory.CreateTempSubdirectory("qora-sec-").FullName;
        var registry = new ProviderRegistry(TestOracle.ShippedProviders(dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle"));
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
    [InlineData("BEGIN\n  UPDATE (SELECT v FROM t) SET v = 0;\nEND;\n/", "updateWithoutWhere")]
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
            var ds = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(TestOracle.Supported(dir).BuildConnectionString(p, "pw", null)).DataSource;
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
            var cs = TestOracle.Supported(dir).BuildConnectionString(p, "fake ora;spaced=pw", null);
            Assert.DoesNotContain("fake ora", LogScrubber.Scrub(cs));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("BEGIN wallet_password := 'fake-ora-w1'; END;", "fake-ora-w1")]
    [InlineData("BEGIN v_pwd := 'fake-ora-w2'; END;", "fake-ora-w2")]
    // Named notation (=>): DBMS_CREDENTIAL and any routine with a secret-named parameter.
    [InlineData("BEGIN DBMS_CREDENTIAL.CREATE_CREDENTIAL(credential_name => 'C', username => 'u', password => 'fake-ora-w3'); END;", "fake-ora-w3")]
    [InlineData("BEGIN app.set_secret(p_secret => q'[fake-ora-w4]'); END;", "fake-ora-w4")]
    // Positional: CREATE_CREDENTIAL(name, username, password), UPDATE_CREDENTIAL(name, attribute, value).
    [InlineData("BEGIN DBMS_CREDENTIAL.CREATE_CREDENTIAL('C', 'u', 'fake-ora-w5'); END;", "fake-ora-w5")]
    [InlineData("BEGIN DBMS_CREDENTIAL.UPDATE_CREDENTIAL('C', 'password', 'fake-ora-w7'); END;", "fake-ora-w7")]
    public void Oracle_secret_names_and_credential_literals_are_redacted_in_sql(string sql, string secret) =>
        Assert.DoesNotContain(secret, new OracleLanguageService().RedactSecrets(sql).Text);

    [Fact]
    public void A_named_password_argument_is_masked_and_the_other_arguments_are_kept() =>
        Assert.Equal("BEGIN DBMS_CREDENTIAL.CREATE_CREDENTIAL(credential_name => 'C', username => 'u', password => <redacted>); END;",
            new OracleLanguageService().RedactSecrets("BEGIN DBMS_CREDENTIAL.CREATE_CREDENTIAL(credential_name => 'C', username => 'u', password => 'fake-ora-w6'); END;").Text);

    [Fact]
    public void A_pasted_proxy_or_wallet_password_is_dropped_not_kept_anywhere()
    {
        var r = TestOracle.Supported().ParseConnectionString("Data Source=//h/S;User Id=app;Password=fake-main;Proxy User Id=p;Proxy Password=fake-proxy;WALLET_PASSWORD=fake-wallet",
            new Profile { Id = "", Name = "x", Engine = Engine.Oracle, Auth = AuthKind.Password });
        Assert.Equal("fake-main", r.Secret);
        var rest = System.Text.Json.JsonSerializer.Serialize(new { r.Profile, r.Unknown, r.NotApplied, r.Notes });
        Assert.DoesNotContain("fake-proxy", rest);
        Assert.DoesNotContain("fake-wallet", rest);
        Assert.DoesNotContain("fake-main", rest);
    }

    // ---------------------------------------------------------------------------------------------------- D4 S10

    static readonly OracleDataEdit Edit = new();

    static ResultEditInfo Recorded() => new()
    {
        Editable = true, Schema = "APP", Table = "T", KeyColumns = ["ID"], KeyOrdinals = [0],
        Columns =
        [
            new EditColumn { Ordinal = 0, Column = "ID", Type = "NUMBER(10)", Key = true, Comparable = true },
            new EditColumn { Ordinal = 1, Column = "NAME", Type = "VARCHAR2(40)", Nullable = true, Comparable = true },
        ],
    };

    [Theory]
    [InlineData("PASSWORD")]
    [InlineData("NAME\" = 1; DROP TABLE x; --")]
    [InlineData("name")] // the folded spelling is another name
    public void Grid_patches_cannot_name_columns_outside_the_recorded_table(string column)
    {
        var patch = new GridPatchParams
        {
            SessionId = "s", ExecutionId = "e",
            Updates = [new GridRowUpdate { Key = [new GridValue { Text = "1" }], Changes = new() { [column] = new GridValue { Text = "x" } }, Original = new() { [column] = new GridValue { Text = "y" } } }],
        };
        Assert.StartsWith("Unknown column", Assert.Throws<QuerithmException>(() => Querithm.Core.Execution.PatchBuilder.Build(Edit, Recorded(), patch, false)).Message);
    }

    [Fact]
    public void Values_never_reach_the_sql_text_and_preview_literals_cannot_end_their_string()
    {
        const string hostile = @"x'); DELETE FROM t; --\'";
        var patch = new GridPatchParams
        {
            SessionId = "s", ExecutionId = "e",
            Updates = [new GridRowUpdate { Key = [new GridValue { Text = "1" }], Changes = new() { ["NAME"] = new GridValue { Text = hostile } }, Original = new() { ["NAME"] = new GridValue { Text = hostile } } }],
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["NAME"] = new GridValue { Text = hostile } } }],
        };
        foreach (var st in Querithm.Core.Execution.PatchBuilder.Build(Edit, Recorded(), patch, false))
        {
            Assert.DoesNotContain("DELETE", st.Sql);
            Assert.Contains(":p0", st.Sql);
        }
        Assert.Equal(@"'x''); DELETE FROM t; --\'''", Edit.PreviewLiteral("VARCHAR2(40)", new GridValue { Text = hostile }));
        Assert.Equal("'NaN'", Edit.PreviewLiteral("NUMBER", new GridValue { Text = "NaN" }));
        Assert.Throws<ArgumentException>(() => Edit.Quote("a\"b"));
        Assert.Throws<ArgumentException>(() => new OracleImport().CreateTable("APP", "t\" (x", [("c", "NUMBER", false)], []));
    }
    // ------------------------------------------------------------------------------------------ D4 S11

    [Fact]
    public void Execute_dialog_values_stay_one_typed_literal_and_only_the_dictionarys_parameters_are_named()
    {
        const string hostile = "x'); DROP TABLE t; --";
        var args = new OracleArgument[] { new(1, "S", "VARCHAR2", "IN", false), new(2, "N", "NUMBER", "IN", false), new(3, "IO", "VARCHAR2", "IN/OUT", false) };
        ScriptArgument A(string n, string t) => new() { Name = n, Value = new GridValue { Text = t } };
        var block = OracleScripter.ExecuteBlock("APP.P", false, args, [A("S", hostile), A("IO", hostile)]);
        Assert.Contains("S => 'x''); DROP TABLE t; --',", block);
        Assert.Contains(":= 'x''); DROP TABLE t; --';", block);
        // The classifier (which guards Prod and destructive runs) sees no DROP: the text never left its literal.
        var flagged = new OracleLanguageService().Classify(block).Destructive;
        Assert.True(flagged.Length == 0, string.Join(" / ", flagged.Select(f => f.ToString())));
        // A number value is parsed, never pasted; a name the routine does not have is refused, never written.
        Assert.Throws<ArgumentException>(() => OracleScripter.ExecuteBlock("APP.P", false, args, [A("N", "1); DROP TABLE t; --")]));
        Assert.Contains("no parameter", Assert.Throws<ArgumentException>(() => OracleScripter.ExecuteBlock("APP.P", false, args, [A("X => 1); DROP TABLE t; --", "1")])).Message);
    }

    [Fact]
    public void Compare_and_rename_DDL_quote_every_name_and_refuse_ones_that_cannot_be_quoted()
    {
        var d = new OracleSchemaCompare();
        var t = new Querithm.Contract.Providers.ModelTable { Schema = "APP", Name = "T\"; DROP TABLE x; --", Columns = [] };
        Assert.Throws<ArgumentException>(() => d.DropTable(t));
        Assert.Equal("ALTER TABLE \"APP\".\"T\" RENAME COLUMN \"a b\" TO \"DROP\";", d.RenameColumn(t with { Name = "T" }, "a b", "DROP"));
        Assert.Throws<ArgumentException>(() => new OracleReferenceFinder().Rename("x", "y\" --"));
    }
    /// <summary>
    /// D4 S12, §13 rule 7 (monitor kills): the terminate and cancel blocks bind the identity and build ALTER SYSTEM only from the
    /// V$SESSION row that still matches it; an identity that is not "SERIAL#:YYYYMMDDHH24MISS" binds nothing.
    /// </summary>
    [Fact]
    public void Oracle_kills_are_built_on_the_server_from_the_verified_session_row()
    {
        foreach (var sql in new[] { OracleActivitySession.KillSql, OracleActivitySession.CancelSql })
        {
            Assert.Contains("s.SID = :sid AND s.SERIAL# = :serial AND s.LOGON_TIME = TO_DATE(:logon, 'YYYYMMDDHH24MISS')", sql);
            var alter = sql[sql.IndexOf("EXECUTE IMMEDIATE", StringComparison.Ordinal)..sql.IndexOf(":p_done := 1", StringComparison.Ordinal)];
            // Only the block's own variables, read from V$SESSION, reach the statement text: no bind, no literal from outside.
            Assert.DoesNotContain(":", alter.Replace("TO_CHAR(", "").Replace("'TM9'", ""));
            Assert.Contains("v_sid", alter);
            Assert.Contains("v_serial", alter);
        }
        Assert.Contains("s.SQL_ID = :sqlid AND s.SQL_EXEC_ID = :execid", OracleActivitySession.CancelSql);
        Assert.Contains("|| v_sql ||", OracleActivitySession.CancelSql);

        foreach (var bad in new[] { "1:2026' OR 1=1 --", "1:99999999999999", "1:20261340000000", "-1:20261112061739", "1:2026111206173", "" })
        {
            using var cmd = new Oracle.ManagedDataAccess.Client.OracleCommand();
            Assert.False(OracleActivitySession.BindIdentity(cmd, new SessionKey { Id = 66, Instance = bad }), bad);
            Assert.Empty(cmd.Parameters);
        }
        using (var cmd = new Oracle.ManagedDataAccess.Client.OracleCommand())
            Assert.False(OracleActivitySession.BindIdentity(cmd, new SessionKey { Id = 0, Instance = "441:20261112061739" }));
        using (var ok = new Oracle.ManagedDataAccess.Client.OracleCommand())
        {
            Assert.True(OracleActivitySession.BindIdentity(ok, new SessionKey { Id = 66, Instance = "441:20261112061739" }));
            Assert.Equal((66L, 441L, "20261112061739"), ((long)ok.Parameters["sid"].Value, (long)ok.Parameters["serial"].Value, (string)ok.Parameters["logon"].Value));
        }
    }

    /// <summary>D4 S12: the probe's dynamic SQL names only its own constant view list.</summary>
    [Fact]
    public void Oracle_monitor_probe_runs_only_its_own_view_names()
    {
        var probe = OracleActivity.ProbeSql;
        Assert.DoesNotContain("{", probe);
        foreach (var line in probe.Split('\n').Where(l => l.Contains("can('", StringComparison.Ordinal)))
            Assert.Matches(@"can\('V\$[A-Z_]+'\)", line);
    }
}

using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.Oracle;

namespace Querithm.Tests;

/// <summary>
/// D4 S03 provider core without a server: the connection string Querithm builds (TCPS by default, the downgrade, the SID
/// option, TNS_ADMIN pinned to an empty folder, extras refused), what a pasted string can and cannot set, the profile checks
/// at the RPC boundary, ORA- error positions and the identity format. The server side is OracleProviderIntegrationTests.
/// </summary>
public sealed class OracleProviderTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qora-prov-").FullName;
    readonly OracleProvider _oracle;

    public OracleProviderTests() => _oracle = TestOracle.Supported(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    static Profile P(TlsMode tls = TlsMode.Default, int? port = null) => new()
    {
        Id = "p", Name = "ora", Engine = Engine.Oracle, Host = "db.example.test", Port = port, Auth = AuthKind.Password,
        Username = "app", DatabaseDefault = "SALESPDB", TlsMode = tls,
    };

    OracleConnectionStringBuilder Built(Profile p, string? secret = "pw") => new(_oracle.BuildConnectionString(p, secret, null));

    [Fact]
    public void Tcps_with_the_server_name_checked_is_the_default_on_port_2484()
    {
        var b = Built(P());
        Assert.Equal("(DESCRIPTION=(CONNECT_TIMEOUT=15)(TRANSPORT_CONNECT_TIMEOUT=15)(RETRY_COUNT=0)(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example.test)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=SALESPDB))(SECURITY=(SSL_SERVER_DN_MATCH=TRUE)))", b.DataSource);
        Assert.False(b.Pooling);
        Assert.Equal(("app", "pw"), (b.UserID, b.Password));
        Assert.Equal(2484, _oracle.Traits.DefaultPort);
    }

    [Fact]
    public void Plain_tcp_is_the_explicit_disable_downgrade_on_port_1521()
    {
        var ds = Built(P(TlsMode.Disable)).DataSource;
        Assert.Contains("(PROTOCOL=TCP)(HOST=db.example.test)(PORT=1521)", ds);
        Assert.DoesNotContain("SECURITY", ds);
        Assert.Contains("(PORT=1600)", Built(P(TlsMode.Disable, 1600)).DataSource);
    }

    [Fact]
    public void Verify_ca_keeps_tcps_and_turns_only_the_name_check_off()
    {
        var ds = Built(P(TlsMode.VerifyCa)).DataSource;
        Assert.Contains("(PROTOCOL=TCPS)", ds);
        Assert.Contains("(SSL_SERVER_DN_MATCH=FALSE)", ds);
    }

    [Theory]
    [InlineData(TlsMode.Require)]
    [InlineData(TlsMode.Mandatory)]
    [InlineData(TlsMode.Prefer)]
    [InlineData(TlsMode.Optional)]
    public void Modes_the_driver_cannot_honour_are_refused_with_a_reason(TlsMode mode)
    {
        Assert.NotNull(_oracle.ValidateProfile(P(mode)));
        Assert.Throws<ArgumentException>(() => _oracle.BuildConnectionString(P(mode), "pw", null));
    }

    [Fact]
    public void Trusting_any_certificate_is_refused()
    {
        var why = _oracle.ValidateProfile(P() with { TrustCert = true });
        Assert.Contains("CA file", why);
    }

    [Fact]
    public void A_sid_is_used_only_through_the_explicit_option()
    {
        Assert.Contains("(CONNECT_DATA=(SERVICE_NAME=SALESPDB))", Built(P()).DataSource);
        var sid = P() with { Options = new() { [OracleConnectionStrings.SidOption] = "true" } };
        Assert.Contains("(CONNECT_DATA=(SID=SALESPDB))", Built(sid).DataSource);
    }

    [Fact]
    public void Tns_admin_is_an_empty_querithm_folder_whatever_the_environment_says()
    {
        var hostile = Directory.CreateTempSubdirectory("qora-tns-").FullName;
        File.WriteAllText(Path.Combine(hostile, "sqlnet.ora"), "SSL_SERVER_DN_MATCH=FALSE\n");
        var before = Environment.GetEnvironmentVariable("TNS_ADMIN");
        Environment.SetEnvironmentVariable("TNS_ADMIN", hostile);
        try
        {
            var tns = Built(P()).TnsAdmin;
            Assert.StartsWith(Path.Combine(_dir, "oracle"), tns);
            Assert.Empty(Directory.EnumerateFileSystemEntries(tns));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TNS_ADMIN", before);
            Directory.Delete(hostile, true);
        }
    }

    [Fact]
    public void A_file_dropped_into_the_tns_folder_is_refused_not_read()
    {
        var tns = Built(P()).TnsAdmin;
        File.WriteAllText(Path.Combine(tns, "tnsnames.ora"), "X=(DESCRIPTION=(ADDRESS=(HOST=evil)(PORT=1)))");
        var ex = Assert.Throws<InvalidOperationException>(() => _oracle.BuildConnectionString(P(), "pw", null));
        Assert.Contains("must be empty", ex.Message);
    }

    [Fact]
    public void Profile_extras_never_reach_the_built_string()
    {
        var hostile = P() with
        {
            Options = new()
            {
                ["TNS_ADMIN"] = "C:\\evil", ["WALLET_LOCATION"] = "C:\\evil", ["Wallet_Password"] = "x", ["DBA Privilege"] = "SYSDBA",
                ["Proxy User Id"] = "p", ["Proxy Password"] = "x", ["TOKEN_AUTH"] = "OCI_TOKEN", ["Pooling"] = "true",
            },
        };
        var b = Built(hostile);
        Assert.DoesNotContain("evil", b.ConnectionString);
        Assert.Equal("", b.DBAPrivilege ?? "");
        Assert.Equal("", b.ProxyUserId ?? "");
        Assert.Equal("", b.WalletLocation ?? "");
        Assert.False(b.Pooling);
    }

    [Theory]
    [InlineData("db)(HOST=evil")]
    [InlineData("db(x")]
    [InlineData("db=1")]
    [InlineData("db host")]
    public void A_host_that_could_rewrite_the_descriptor_is_refused(string host)
    {
        Assert.NotNull(_oracle.ValidateProfile(P() with { Host = host }));
        Assert.Throws<ArgumentException>(() => _oracle.BuildConnectionString(P() with { Host = host }, "pw", null));
    }

    [Theory]
    [InlineData("S)(SID=X")]
    [InlineData("S=1")]
    [InlineData("S'x")]
    public void A_service_that_could_rewrite_the_descriptor_is_refused(string service)
    {
        Assert.NotNull(_oracle.ValidateProfile(P() with { DatabaseDefault = service }));
        Assert.Throws<ArgumentException>(() => _oracle.BuildConnectionString(P() with { DatabaseDefault = service }, "pw", null));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("proxy[client]")]
    [InlineData("scott/tiger")]
    [InlineData("scott@db")]
    public void Os_and_proxy_sign_in_through_the_user_name_are_refused(string user) =>
        Assert.NotNull(_oracle.ValidateProfile(P() with { Username = user }));

    [Theory]
    [InlineData(AuthKind.Windows)]
    [InlineData(AuthKind.Kerberos)]
    [InlineData(AuthKind.EntraToken)]
    public void Wallet_os_and_token_sign_in_are_refused_with_a_reason(AuthKind auth) =>
        Assert.Contains("password", _oracle.ValidateProfile(P() with { Auth = auth }));

    [Fact]
    public void A_service_name_is_required_to_build()
    {
        var ex = Assert.Throws<ArgumentException>(() => _oracle.BuildConnectionString(P() with { DatabaseDefault = null }, "pw", null));
        Assert.Contains("service name", ex.Message);
    }

    [Fact]
    public void A_ca_file_becomes_a_pem_trust_wallet_under_app_data()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Test CA", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var caFile = Path.Combine(_dir, "ca.pem");
        File.WriteAllText(caFile, ca.ExportCertificatePem());
        var b = Built(P() with { CaPath = caFile });
        Assert.StartsWith(Path.Combine(_dir, "oracle", "trust"), b.WalletLocation);
        var wallet = File.ReadAllText(Path.Combine(b.WalletLocation, "ewallet.pem"));
        Assert.Contains("BEGIN CERTIFICATE", wallet);
        Assert.DoesNotContain("PRIVATE KEY", wallet);
    }

    // ---- pasted connection strings ----

    static Profile Basis => new() { Id = "", Name = "x", Engine = Engine.Oracle, Auth = AuthKind.Password };

    [Theory]
    [InlineData("Data Source=//db.local:2484/SALES", "db.local", null, "SALES")]
    [InlineData("Data Source=tcps://db.local:2485/SALES", "db.local", 2485, "SALES")]
    [InlineData("Data Source=db.local/SALES", "db.local", null, "SALES")]
    [InlineData("Data Source=[::1]:2484/SALES", "::1", null, "SALES")]
    public void Easy_connect_fills_host_port_and_service(string cs, string host, int? port, string service)
    {
        var r = _oracle.ParseConnectionString(cs, Basis);
        Assert.Equal((host, port, service), (r.Profile.Host, r.Profile.Port, r.Profile.DatabaseDefault));
    }

    [Fact]
    public void Plain_tcp_in_a_pasted_string_is_a_warned_downgrade()
    {
        var r = _oracle.ParseConnectionString("Data Source=tcp://db.local:1521/SALES;User Id=app", Basis);
        Assert.Equal(TlsMode.Disable, r.Profile.TlsMode);
        Assert.Contains(r.Notes, n => n.Contains("not encrypted"));
        Assert.Null(r.Profile.Port); // the TCP default
    }

    [Fact]
    public void A_descriptor_with_one_address_is_read_and_its_other_settings_named()
    {
        const string cs = "Data Source=(DESCRIPTION=(RETRY_COUNT=20)(ADDRESS=(PROTOCOL=TCPS)(HOST=db.local)(PORT=2484))(CONNECT_DATA=(SID=ORCL))(SECURITY=(SSL_SERVER_DN_MATCH=FALSE)));User Id=app;Password=pw";
        var r = _oracle.ParseConnectionString(cs, Basis);
        Assert.Equal(("db.local", "ORCL", TlsMode.VerifyCa), (r.Profile.Host, r.Profile.DatabaseDefault, r.Profile.TlsMode));
        Assert.Equal("true", r.Profile.Options![OracleConnectionStrings.SidOption]);
        Assert.Contains(r.Notes, n => n.Contains("RETRY_COUNT"));
        Assert.Contains(r.Notes, n => n.Contains("host name"));
        Assert.Equal("pw", r.Secret);
    }

    [Theory]
    [InlineData("Data Source=SALES_ALIAS", "tnsnames.ora")]
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=a)(PORT=1))(ADDRESS=(PROTOCOL=TCP)(HOST=b)(PORT=1)))(CONNECT_DATA=(SERVICE_NAME=S)))", "2 addresses")]
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=a)(PORT=1))", "not closed")]
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=IPC)(KEY=x))(CONNECT_DATA=(SERVICE_NAME=S)))", "IPC")]
    [InlineData("Data Source=a,b:1521/S", "several hosts")]
    [InlineData("Data Source=//h:1521/S)(HOST=evil", "service name")]
    public void Aliases_lists_and_malformed_descriptors_are_refused_with_the_reason(string cs, string reason)
    {
        var ex = Assert.Throws<ArgumentException>(() => _oracle.ParseConnectionString(cs, Basis));
        Assert.Contains(reason, ex.Message);
    }

    [Theory]
    [InlineData("TNS_ADMIN=C:\\x", "TNS_ADMIN")]
    [InlineData("WALLET_LOCATION=C:\\w", "WALLET_LOCATION")]
    [InlineData("DBA Privilege=SYSDBA", "DBA Privilege")]
    [InlineData("Proxy User Id=p;Proxy Password=fake-proxy-secret", "Proxy")]
    [InlineData("TOKEN_AUTH=OCI_TOKEN", "TOKEN_AUTH")]
    public void Extras_that_would_bypass_the_sign_in_or_the_descriptor_are_named_not_applied(string extra, string named)
    {
        var r = _oracle.ParseConnectionString($"Data Source=//db/S;User Id=app;{extra}", Basis);
        Assert.Contains(r.NotApplied, n => n.Contains(named, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Notes, n => n.Contains(named, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("fake-proxy-secret", System.Text.Json.JsonSerializer.Serialize(new { r.Profile, r.NotApplied, r.Notes, r.Unknown }));
        Assert.Null(r.Profile.Options?.GetValueOrDefault("TNS_ADMIN"));
    }

    [Theory]
    [InlineData("User Id=/")]
    [InlineData("User Id=proxy[client]")]
    public void Os_and_proxy_user_names_in_a_pasted_string_are_refused(string user) =>
        Assert.Throws<ArgumentException>(() => _oracle.ParseConnectionString($"Data Source=//db/S;{user}", Basis));

    [Fact]
    public void An_oracle_string_is_recognised_as_oracle()
    {
        var a = _oracle.ConnectionStringAffinity(ConnectionStringKeywords.Tokenize("Data Source=//db:2484/S;User Id=app;Password=x"));
        Assert.True(a.Distinctive > 0);
    }

    // ---- errors ----

    [Theory]
    [InlineData("SELECT x FROM dual", 7, 1, 8)]
    [InlineData("SELECT 1\nFROM dual\nWHERE y = 1", 25, 3, 7)]
    [InlineData("SELECT 1 FROM dual", 0, null, null)]
    [InlineData("SELECT 1", 99, null, null)]
    public void A_parse_offset_becomes_a_line_and_column(string text, int offset, int? line, int? column) =>
        Assert.Equal((line, column), OracleErrors.Position(text, offset));

    [Fact]
    public void The_help_link_line_is_dropped()
    {
        Assert.Equal("ORA-00942: table or view \"X\" does not exist", OracleErrors.Clean("ORA-00942: table or view \"X\" does not exist\nHelp: https://docs.oracle.com/error-help/db/ora-00942/"));
    }

    [Theory]
    [InlineData("ORA-12514: Cannot connect to database. Service X is not registered", 12514)]
    [InlineData("  ORA-12541: Cannot connect. No listener", 12541)]
    [InlineData("ORA-1251: short", null)]
    [InlineData("Failed with ORA-12514 inside", null)]
    [InlineData(null, null)]
    public void The_listeners_own_code_is_read_from_the_start_of_a_driver_message(string? message, int? code) =>
        Assert.Equal(code, OracleErrors.LeadingCode(message));

    [Fact]
    public void Versions_are_reduced_to_their_number_and_old_releases_are_known()
    {
        Assert.Equal("23.26.0.0.0", OracleProvider.CleanVersion("23.26.0.0.0 evil\u0007"));
        Assert.Equal("unknown", OracleProvider.CleanVersion(null));
        Assert.Equal(18, OracleProvider.MajorOf("18.4.0.0.0"));
        Assert.True(OracleProvider.MajorOf("18.4.0.0.0") < OracleProvider.MinimumMajor);
    }

    // ---- databases, features, identity ----

    [Fact]
    public async Task Changing_database_is_refused_with_the_reason()
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => _oracle.ChangeDatabaseAsync(new OracleConnection(), "OTHER", CancellationToken.None));
        Assert.Equal(OracleProvider.ChangeDatabaseRefusal, ex.Message);
        Assert.Equal(OracleProvider.ChangeDatabaseRefusal, _oracle.FeatureRefusal(Feature.ChangeDatabase));
        Assert.Null(_oracle.CurrentDatabase(new OracleConnection()));
    }

    [Fact]
    public void Transactions_are_available_and_their_traits_are_oracles()
    {
        Assert.Null(_oracle.FeatureRefusal(Feature.Transactions));
        var t = _oracle.Transactions.Traits;
        Assert.True(t.AutocommitDefault && t.DdlCommitsImplicitly && t.Savepoints && !t.ReleaseSavepoint);
        Assert.DoesNotContain(t.UserStatements, s => s.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Savepoint_names_are_checked_and_release_sends_nothing()
    {
        await _oracle.Transactions.ReleaseSavepointAsync(new OracleConnection(), "qsp1", CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => _oracle.Transactions.ReleaseSavepointAsync(new OracleConnection(), "x; DROP", CancellationToken.None));
    }

    [Theory]
    [InlineData("12345:20261110081500", true)]
    [InlineData("1:20261110081500", true)]
    [InlineData("", false)]
    [InlineData("12345", false)]
    [InlineData("x:20261110081500", false)]
    [InlineData("1:2026111008150", false)]
    public void The_identity_instance_is_serial_and_logon_time(string instance, bool valid) =>
        Assert.Equal(valid, _oracle.Activity!.IsValidInstance(instance));

    [Fact]
    public void Rejected_sign_ins_are_recognised_so_the_secret_is_not_retried() =>
        Assert.False(_oracle.Activity!.CredentialsRejected(new InvalidOperationException("ORA-01017")));
}

using System.ComponentModel;
using System.Data.Common;
using System.Security.Authentication;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Providers.MySql;
using Querithm.Providers.Oracle;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// TLS certificate classification, readable error translation and structured RPC recovery across all server engines
/// (SQL Server, PostgreSQL, MySQL / MariaDB, Oracle), keeping §13 rule 2 (TLS verified by default) intact.
/// </summary>
public sealed class TlsCertificateRecoveryTests
{
    static Profile P(Engine engine, TlsMode tls = TlsMode.Default, bool trust = false, string host = "198.51.100.10", int? port = null) => new()
    {
        Id = "tls-rec-" + engine,
        Name = "TLS test",
        Engine = engine,
        Host = host,
        Port = port,
        Auth = engine == Engine.Mssql ? AuthKind.Sql : AuthKind.Password,
        Username = "app",
        Environment = EnvironmentKind.Dev,
        TlsMode = tls,
        TrustCert = trust,
        TimeoutS = 5,
    };

    // ---- Shared classifier ----

    [Theory]
    [InlineData("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot", TlsCertReason.UntrustedRoot)]
    [InlineData("The remote certificate is invalid according to the validation procedure: RemoteCertificateNameMismatch", TlsCertReason.NameMismatch)]
    [InlineData("The remote certificate is invalid because of errors in the certificate chain: NotTimeValid", TlsCertReason.Expired)]
    [InlineData("SSL: CERTIFICATE_VERIFY_FAILED: self-signed certificate", TlsCertReason.UntrustedRoot)]
    [InlineData("The certificate chain was issued by an authority that is not trusted.", TlsCertReason.UntrustedRoot)]
    [InlineData("The target principal name is incorrect.", TlsCertReason.NameMismatch)]
    [InlineData("The remote certificate is invalid according to the validation procedure.", TlsCertReason.Other)]
    public void Classifier_identifies_certificate_errors_and_reasons(string message, TlsCertReason expected)
    {
        var wrapped = new InvalidOperationException(
            "A connection was successfully established with the server, but then an error occurred during the pre-login handshake. (provider: TCP Provider, error: 35 - An internal exception was caught)",
            new AuthenticationException(message));

        var m = TlsCertificateClassifier.TryClassify(wrapped);
        Assert.NotNull(m);
        Assert.Equal(expected, m.Value.Reason);
        Assert.Contains(message[..20], m.Value.Detail);
    }

    [Theory]
    [InlineData(unchecked((int)0x80090325), TlsCertReason.UntrustedRoot)] // SEC_E_UNTRUSTED_ROOT
    [InlineData(unchecked((int)0x800B0109), TlsCertReason.UntrustedRoot)] // CERT_E_UNTRUSTEDROOT
    [InlineData(unchecked((int)0x800B010F), TlsCertReason.NameMismatch)]  // CERT_E_CN_NO_MATCH
    [InlineData(unchecked((int)0x800B0101), TlsCertReason.Expired)]       // CERT_E_EXPIRED
    public void Classifier_identifies_windows_sni_win32_codes(int nativeCode, TlsCertReason expected)
    {
        var ex = new InvalidOperationException("pre-login handshake failed", new Win32Exception(nativeCode, "TLS error"));
        var m = TlsCertificateClassifier.TryClassify(ex);
        Assert.NotNull(m);
        Assert.Equal(expected, m.Value.Reason);
    }

    [Theory]
    [InlineData("Authentication failed because the remote party sent a TLS alert: 'ProtocolVersion'.")]
    [InlineData("SSL Handshake failed with OpenSSL error - SSL_ERROR_SSL: unsupported protocol")]
    [InlineData("Unexpected EOF while reading from socket")]
    [InlineData("Login failed for user 'app'.")]
    [InlineData("Connection refused")]
    public void Classifier_ignores_non_certificate_tls_and_network_errors(string message)
    {
        var ex = new InvalidOperationException(
            "pre-login handshake error: 35 - An internal exception was caught",
            new AuthenticationException(message));
        Assert.Null(TlsCertificateClassifier.TryClassify(ex));
    }

    [Fact]
    public void Classifier_prefers_specific_inner_reason_over_generic_outer_other()
    {
        var ex = new InvalidOperationException(
            "The remote certificate is invalid according to the validation procedure.",
            new AuthenticationException("The remote certificate is invalid: UntrustedRoot"));
        var m = TlsCertificateClassifier.TryClassify(ex);
        Assert.NotNull(m);
        Assert.Equal(TlsCertReason.UntrustedRoot, m.Value.Reason);
    }

    [Theory]
    [InlineData("tcp:   ", 1433, 1433, "localhost:1433")]
    [InlineData("::1", 1433, 1433, "[::1]:1433")]
    [InlineData("[::1]", 1433, 1433, "[::1]:1433")]
    [InlineData("[::1]:1433", 1433, 1433, "[::1]:1433")]
    [InlineData("db.internal:1433", 1433, 1433, "db.internal:1433")]
    [InlineData("198.51.100.10,1433", 1433, 1433, "198.51.100.10,1433")]
    public void FormatEndpoint_handles_tcp_prefix_host_port_and_ipv6(string host, int? port, int? dflt, string expected)
    {
        Assert.Equal(expected, TlsCertificateClassifier.FormatEndpoint(host, port, dflt));
    }

    [Fact]
    public void CleanDetail_handles_null_and_truncates_multiline_values()
    {
        Assert.Equal("", TlsCertificateClassifier.CleanDetail(null));
        Assert.Equal("first line", TlsCertificateClassifier.CleanDetail("first line\r\nsecond line"));
    }

    [Fact]
    public void TranslateException_does_not_hijack_query_or_non_tls_errors_mentioning_certificates()
    {
        var userErr = new InvalidOperationException("Webhook failed: certificate has expired");
        // Even with commandText == null (ExecutionService/DataEditorService/ImportService), non-TLS exceptions are untouched.
        Assert.Equal("Webhook failed: certificate has expired", new MySqlProvider().TranslateException(userErr, null)[0].Message);
        Assert.Equal("Webhook failed: certificate has expired", new PostgresProvider().TranslateException(userErr, null)[0].Message);
        Assert.Equal("Webhook failed: certificate has expired", new SqlServerProvider().TranslateException(userErr, null)[0].Message);

        // SQL Server client-TLS guard: allows SNI/connect errors (Number <= 0 or AuthenticationException inner), rejects server errors (Number > 0) and query errors.
        Assert.True(SqlServerProvider.IsClientTlsSqlError(0, null, null));
        Assert.True(SqlServerProvider.IsClientTlsSqlError(-2146893019, null, null));
        Assert.True(SqlServerProvider.IsClientTlsSqlError(20, new AuthenticationException("UntrustedRoot"), null));
        Assert.False(SqlServerProvider.IsClientTlsSqlError(50000, null, null)); // severity 20+ server RAISERROR
        Assert.False(SqlServerProvider.IsClientTlsSqlError(0, new AuthenticationException("UntrustedRoot"), "SELECT 1"));
    }

    [Fact]
    public void Oracle_does_not_misclassify_non_certificate_protocol_error_as_cert_failure()
    {
        var protocolErr = new AuthenticationException("SSL Handshake failed: unsupported protocol");
        Assert.Null(new OracleProvider().ClassifyTlsCertificateError(protocolErr, P(Engine.Oracle, TlsMode.Default)));
    }

    // ---- Per-engine classification ----

    [Fact]
    public void SqlServer_classifies_default_mandatory_and_strict_modes()
    {
        var provider = new SqlServerProvider();
        var auth = new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot");

        var def = provider.ClassifyTlsCertificateError(auth, P(Engine.Mssql, TlsMode.Default, trust: false, port: 1433));
        Assert.NotNull(def);
        Assert.Equal(Engine.Mssql, def.Engine);
        Assert.Equal("198.51.100.10:1433", def.Host);
        Assert.Equal(TlsCertReason.UntrustedRoot, def.Reason);
        Assert.True(def.CanTrustCert);
        Assert.False(def.StrictMode);
        Assert.False(def.SupportsCaPath);
        Assert.Contains("self-signed", def.Message, StringComparison.OrdinalIgnoreCase);

        var strict = provider.ClassifyTlsCertificateError(auth, P(Engine.Mssql, TlsMode.Strict, trust: false));
        Assert.NotNull(strict);
        Assert.False(strict.CanTrustCert);
        Assert.True(strict.StrictMode);
        Assert.Contains("Strict", strict.Message);

        // Already trusting in non-Strict mode: do not loop on offering TrustCert again.
        Assert.Null(provider.ClassifyTlsCertificateError(auth, P(Engine.Mssql, TlsMode.Mandatory, trust: true)));
    }

    [Fact]
    public void MySql_classifies_verify_modes_only()
    {
        var provider = new MySqlProvider();
        var auth = new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot");

        var def = provider.ClassifyTlsCertificateError(auth, P(Engine.MySql, TlsMode.Default));
        Assert.NotNull(def);
        Assert.False(def.CanTrustCert);
        Assert.True(def.CanSwitchToRequire);
        Assert.True(def.SupportsCaPath);
        Assert.Contains("Require", def.Message);

        Assert.Null(provider.ClassifyTlsCertificateError(auth, P(Engine.MySql, TlsMode.Require)));
        Assert.Null(provider.ClassifyTlsCertificateError(auth, P(Engine.MySql, TlsMode.Disable)));
    }

    [Fact]
    public void Postgres_classifies_verify_modes_only()
    {
        var provider = new PostgresProvider();
        var auth = new AuthenticationException("The remote certificate is invalid according to the validation procedure: RemoteCertificateNameMismatch");

        var vf = provider.ClassifyTlsCertificateError(auth, P(Engine.Pg, TlsMode.VerifyFull));
        Assert.NotNull(vf);
        Assert.Equal(TlsCertReason.NameMismatch, vf.Reason);
        Assert.False(vf.CanTrustCert);
        Assert.True(vf.CanSwitchToRequire);
        Assert.True(vf.SupportsCaPath);

        // PostgreSQL default (Require) does not verify certs.
        Assert.Null(provider.ClassifyTlsCertificateError(auth, P(Engine.Pg, TlsMode.Default)));
        Assert.Null(provider.ClassifyTlsCertificateError(auth, P(Engine.Pg, TlsMode.Require)));
    }

    [Fact]
    public void Oracle_classifies_tcps_modes_only_without_trust_bypass()
    {
        var provider = new OracleProvider();
        var auth = new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot");

        var tcps = provider.ClassifyTlsCertificateError(auth, P(Engine.Oracle, TlsMode.Default));
        Assert.NotNull(tcps);
        Assert.False(tcps.CanTrustCert);
        Assert.False(tcps.CanSwitchToRequire);
        Assert.True(tcps.SupportsCaPath);
        Assert.Contains("certificate could not be verified", tcps.Message);

        Assert.Null(provider.ClassifyTlsCertificateError(auth, P(Engine.Oracle, TlsMode.Disable)));
    }

    // ---- End-to-end SessionManager + RpcServer (ProfileTest & session.state) ----

    [Fact]
    public async Task ProfileTest_and_SessionState_surface_structured_tls_cert_error_and_scrub_secrets()
    {
        var secret = "S3cret-Tls-Pass-991!";
        var fake = new FakeTlsFailProvider(new InvalidOperationException(
            "pre-login handshake (provider: TCP Provider, error: 35 - An internal exception was caught)",
            new AuthenticationException($"The remote certificate is invalid: UntrustedRoot; Password={secret}")));

        await using var host = new TestHost(fake);
        var profile = P(Engine.Mssql, TlsMode.Default, trust: false, host: "198.51.100.10", port: 1433) with { Id = "" };

        var tested = await host.Rpc.ProfileTest(new ProfileTestParams { Profile = profile, Secret = secret });
        Assert.False(tested.Ok);
        Assert.NotNull(tested.UntrustedCertificate);
        Assert.True(tested.UntrustedCertificate!.CanTrustCert);
        Assert.Equal(TlsCertReason.UntrustedRoot, tested.UntrustedCertificate.Reason);
        Assert.Equal("198.51.100.10:1433", tested.UntrustedCertificate.Host);
        Assert.DoesNotContain(secret, tested.Error ?? "");
        Assert.DoesNotContain(secret, tested.UntrustedCertificate.Detail ?? "");

        var saved = await host.Rpc.ProfileSave(profile);
        var opened = await host.Rpc.SessionOpen(new SessionOpenParams { ProfileId = saved.Id, Secret = secret });
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.SessionStateGet(new SessionRef { SessionId = opened.SessionId }));
        Assert.Equal("tlsCertificateUntrusted", ex.Code);
        var payload = Assert.IsType<TlsCertError>(ex.DataPayload);
        Assert.True(payload.CanTrustCert);
        Assert.DoesNotContain(secret, ex.Message);
        Assert.DoesNotContain(secret, payload.Detail ?? "");
    }

    sealed class FakeTlsFailProvider(Exception failure) : IDbProvider
    {
        readonly SqlServerProvider _inner = new();
        public Engine Engine => Engine.Mssql;
        public Dialect Dialect => Dialect.Tsql;
        public bool DatabaseScopedConnections => false;
        public IDialectService Language => _inner.Language;
        public bool StopOnBatchError => false;
        public IMetadataProvider Metadata => _inner.Metadata;
        public IPlanProvider Plans => _inner.Plans;
        public IObjectScripter Scripter => _inner.Scripter;
        public ITraceProvider? Trace => null;
        public IDebugProvider? Debugger => null;
        public IDataEditDialect DataEdit => _inner.DataEdit;
        public IImportDialect Import => _inner.Import;
        public ISchemaCompareDialect? SchemaCompare => null;
        public IDataCompareDialect? DataCompare => null;
        public IReferenceFinder? References => null;
        public IActivityProvider? Activity => null;
        public string? FeatureRefusal(Feature feature) => null;
        public string? ValidateProfile(Profile profile) => null;
        public ProductInfo Product(DbConnection connection) => new() { Name = "SQL Server", Version = "15.0" };
        public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.Pooled;
        public EngineTraits Traits => _inner.Traits;
        public (Profile Profile, string? Note) RouteThrough(Profile profile, string host, int port) => _inner.RouteThrough(profile, host, port);
        public (string Host, int Port, string? Refusal) TunnelTarget(Profile profile) => _inner.TunnelTarget(profile);
        public string BuildConnectionString(Profile profile, string? secret, string? database) => _inner.BuildConnectionString(profile, secret, database);
        public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis) => _inner.ParseConnectionString(connectionString, basis);
        public TracedOrigins ResultOrigins(IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot, string? scope) => TracedOrigins.None;
        public Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct) => Task.FromException<DbConnection>(failure);
        public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct) => Task.CompletedTask;
        public string? CurrentDatabase(DbConnection connection) => null;
        public string ServerVersion(DbConnection connection) => "15.0";
        public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<int?>(1);
        public ITransactionControl Transactions => _inner.Transactions;
        public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage) => new DummyDisp();
        public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null) => _inner.TranslateException(ex, commandText);
        public TlsCertError? ClassifyTlsCertificateError(Exception ex, Profile profile) => _inner.ClassifyTlsCertificateError(ex, profile);
        public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);
        public void AddParameter(DbCommand command, QueryParameter parameter) { }
        public ColumnHint GetColumnHint(DbColumn column) => ColumnHint.Default;
        public string? ReadAsString(DbDataReader reader, int ordinal) => null;
        public bool IsPlanResultSet(DbDataReader reader) => false;
        public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct) => throw new NotImplementedException();
        public Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(Array.Empty<DatabaseInfo>());
        public Task ValidateAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;
        public void Cancel(DbCommand command) { }
        sealed class DummyDisp : IDisposable { public void Dispose() { } }
    }
}

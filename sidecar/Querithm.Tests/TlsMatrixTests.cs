using System.Reflection;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// §13 rule 2, every server engine × every TlsMode × trustCert / caPath: the driver settings each provider builds, TLS on by
/// default, and how a pasted connection string changes (or keeps) the mode (M3). No handshake runs here: these read the
/// builders only, so they say what the driver is asked to do, not what a server accepts.
/// </summary>
public class TlsMatrixTests
{
    static Profile P(Engine engine, TlsMode tls = TlsMode.Default, bool trust = false, string? ca = null) => new()
    {
        Id = "tls-matrix", Name = "m", Engine = engine, Host = "db.example.test", Auth = engine == Engine.Mssql ? AuthKind.Sql : AuthKind.Password,
        Username = "u", Environment = EnvironmentKind.Dev, TlsMode = tls, TrustCert = trust, CaPath = ca, TimeoutS = 15,
    };

    // ---- SQL Server (Microsoft.Data.SqlClient)

    /// <summary>The private Build with the Strict probe set as OpenAsync would set it (true on the first try of Default).</summary>
    static SqlConnectionStringBuilder SqlBuild(Profile p, bool? strict)
    {
        var m = typeof(SqlServerProvider).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (SqlConnectionStringBuilder)m.Invoke(new SqlServerProvider(), [p, "pw", null, strict])!;
    }

    public static TheoryData<TlsMode, bool, bool?, string, bool> SqlServerRows => new()
    {
        // mode, trustCert, strict probe, Encrypt, TrustServerCertificate
        { TlsMode.Default, false, true, "Strict", false },
        { TlsMode.Default, false, false, "Mandatory", false }, // fallback after a server without TDS 8
        { TlsMode.Default, false, null, "Mandatory", false }, // BuildConnectionString (no probe)
        { TlsMode.Default, true, true, "Mandatory", true }, // Strict would ignore TrustServerCertificate: never probed with it
        { TlsMode.Default, true, false, "Mandatory", true },
        { TlsMode.Strict, false, null, "Strict", false },
        { TlsMode.Strict, true, null, "Strict", true }, // SqlClient: "except with Encrypt=strict, where validation is always performed"
        { TlsMode.Mandatory, false, null, "Mandatory", false },
        { TlsMode.Mandatory, true, null, "Mandatory", true },
        { TlsMode.Optional, false, null, "Optional", false },
        { TlsMode.Optional, true, null, "Optional", true },
        // Modes the editor does not offer for SQL Server still map to something at least as strong as their meaning.
        { TlsMode.Disable, false, null, "Optional", false },
        { TlsMode.Require, false, null, "Mandatory", false },
        { TlsMode.VerifyCa, false, null, "Mandatory", false },
        { TlsMode.VerifyFull, false, null, "Mandatory", false },
        { TlsMode.Prefer, false, null, "Mandatory", false },
    };

    static SqlConnectionEncryptOption Enc(string name) => name switch
    {
        "Strict" => SqlConnectionEncryptOption.Strict,
        "Mandatory" => SqlConnectionEncryptOption.Mandatory,
        "Optional" => SqlConnectionEncryptOption.Optional,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(SqlServerRows))]
    public void SqlServer_encrypt_and_trust(TlsMode mode, bool trust, bool? strict, string encrypt, bool trustServerCert)
    {
        var b = SqlBuild(P(Engine.Mssql, mode, trust), strict);
        Assert.Equal(Enc(encrypt), b.Encrypt);
        Assert.Equal(trustServerCert, b.TrustServerCertificate);
        // The public builder agrees with the private one for every non-probing case.
        if (strict is null)
        {
            var s = new SqlConnectionStringBuilder(new SqlServerProvider().BuildConnectionString(P(Engine.Mssql, mode, trust), "pw", null));
            Assert.Equal(Enc(encrypt), s.Encrypt);
            Assert.Equal(trustServerCert, s.TrustServerCertificate);
        }
    }

    [Fact]
    public void SqlServer_has_no_CA_setting_a_caPath_changes_nothing()
    {
        var a = new SqlServerProvider().BuildConnectionString(P(Engine.Mssql), "pw", null);
        var b = new SqlServerProvider().BuildConnectionString(P(Engine.Mssql, ca: "/etc/ssl/ca.pem"), "pw", null);
        Assert.Equal(a, b);
    }

    // ---- PostgreSQL (Npgsql)

    [Theory]
    [InlineData(TlsMode.Default, SslMode.Require)]
    [InlineData(TlsMode.Require, SslMode.Require)]
    [InlineData(TlsMode.VerifyCa, SslMode.VerifyCA)]
    [InlineData(TlsMode.VerifyFull, SslMode.VerifyFull)]
    [InlineData(TlsMode.Prefer, SslMode.Prefer)]
    [InlineData(TlsMode.Disable, SslMode.Disable)]
    [InlineData(TlsMode.Optional, SslMode.Prefer)]
    [InlineData(TlsMode.Strict, SslMode.Require)]
    [InlineData(TlsMode.Mandatory, SslMode.Require)]
    public void Postgres_ssl_mode_and_root_certificate(TlsMode mode, SslMode expected)
    {
        var none = new NpgsqlConnectionStringBuilder(new PostgresProvider().BuildConnectionString(P(Engine.Pg, mode), "pw", null));
        Assert.Equal(expected, none.SslMode);
        Assert.True(string.IsNullOrEmpty(none.RootCertificate));
        var withCa = new NpgsqlConnectionStringBuilder(new PostgresProvider().BuildConnectionString(P(Engine.Pg, mode, ca: "/ca/root.crt"), "pw", null));
        Assert.Equal(expected, withCa.SslMode);
        Assert.Equal("/ca/root.crt", withCa.RootCertificate);
        // trustCert is SQL Server's only: it never reaches Npgsql.
        var trusted = new NpgsqlConnectionStringBuilder(new PostgresProvider().BuildConnectionString(P(Engine.Pg, mode, trust: true), "pw", null));
        Assert.Equal(expected, trusted.SslMode);
#pragma warning disable CS0618 // TrustServerCertificate is obsolete in Npgsql; it must stay off
        Assert.False(trusted.TrustServerCertificate);
#pragma warning restore CS0618
    }

    // ---- MySQL / MariaDB (MySqlConnector)

    [Theory]
    [InlineData(TlsMode.Default, MySqlSslMode.VerifyFull)]
    [InlineData(TlsMode.VerifyFull, MySqlSslMode.VerifyFull)]
    [InlineData(TlsMode.VerifyCa, MySqlSslMode.VerifyCA)]
    [InlineData(TlsMode.Require, MySqlSslMode.Required)]
    [InlineData(TlsMode.Prefer, MySqlSslMode.Preferred)]
    [InlineData(TlsMode.Disable, MySqlSslMode.None)]
    [InlineData(TlsMode.Strict, MySqlSslMode.VerifyFull)]
    [InlineData(TlsMode.Mandatory, MySqlSslMode.Required)]
    [InlineData(TlsMode.Optional, MySqlSslMode.Preferred)]
    public void MySql_ssl_mode_and_ca(TlsMode mode, MySqlSslMode expected)
    {
        var none = new MySqlConnectionStringBuilder(new MySqlProvider().BuildConnectionString(P(Engine.MySql, mode), "pw", null));
        Assert.Equal(expected, none.SslMode);
        Assert.True(string.IsNullOrEmpty(none.SslCa));
        var withCa = new MySqlConnectionStringBuilder(new MySqlProvider().BuildConnectionString(P(Engine.MySql, mode, ca: "/ca/ca.pem"), "pw", null));
        Assert.Equal(expected, withCa.SslMode);
        Assert.Equal("/ca/ca.pem", withCa.SslCa);
    }

    // ---- §13.2: TLS on by default (a profile that names nothing, as the editor's new profile and the store's fallback)

    [Fact]
    public void Tls_is_on_by_default_for_every_server_engine()
    {
        Assert.Equal(default, P(Engine.Mssql).TlsMode);
        Assert.False(P(Engine.Mssql).TrustCert);
        var sql = SqlBuild(P(Engine.Mssql), strict: true);
        Assert.Equal(SqlConnectionEncryptOption.Strict, sql.Encrypt);
        Assert.False(sql.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, SqlBuild(P(Engine.Mssql), strict: false).Encrypt);
        Assert.Equal(SslMode.Require, new NpgsqlConnectionStringBuilder(new PostgresProvider().BuildConnectionString(P(Engine.Pg), "pw", null)).SslMode);
        Assert.Equal(MySqlSslMode.VerifyFull, new MySqlConnectionStringBuilder(new MySqlProvider().BuildConnectionString(P(Engine.MySql), "pw", null)).SslMode);
    }

    // ---- SSH tunnel: the mode is kept; VerifyFull still checks the real host name through 127.0.0.1 (PT5-4)

    [Theory]
    [InlineData(TlsMode.Default, TlsMode.Default)]       // PT5-4: VerifyFull keeps the real host name (no drop to CA only)
    [InlineData(TlsMode.VerifyFull, TlsMode.VerifyFull)]
    [InlineData(TlsMode.VerifyCa, TlsMode.VerifyCa)]
    [InlineData(TlsMode.Require, TlsMode.Require)]
    [InlineData(TlsMode.Prefer, TlsMode.Prefer)]
    [InlineData(TlsMode.Disable, TlsMode.Disable)]
    public void MySql_through_a_tunnel(TlsMode mode, TlsMode routed)
    {
        var (p, note) = new MySqlProvider().RouteThrough(P(Engine.MySql, mode), "127.0.0.1", 40000);
        Assert.Equal(routed, p.TlsMode);
        Assert.Equal(mode != routed, note != null);
    }

    [Theory]
    [InlineData(TlsMode.Default, TlsMode.Default)]
    [InlineData(TlsMode.VerifyFull, TlsMode.VerifyFull)] // PT5-4
    [InlineData(TlsMode.VerifyCa, TlsMode.VerifyCa)]
    [InlineData(TlsMode.Prefer, TlsMode.Prefer)]
    [InlineData(TlsMode.Disable, TlsMode.Disable)]
    public void Postgres_through_a_tunnel(TlsMode mode, TlsMode routed)
    {
        var (p, note) = new PostgresProvider().RouteThrough(P(Engine.Pg, mode, ca: "/ca"), "127.0.0.1", 40000);
        Assert.Equal(routed, p.TlsMode);
        Assert.Equal("/ca", p.CaPath);
        Assert.Equal(mode != routed, note != null);
    }

    // ---- Connection-string import (M3): a weaker named keyword is applied with a note; an absent one keeps the mode

    static readonly TlsMode[] SqlModes = [TlsMode.Default, TlsMode.Strict, TlsMode.Mandatory, TlsMode.Optional];
    static readonly TlsMode[] PgModes = [TlsMode.Default, TlsMode.VerifyFull, TlsMode.VerifyCa, TlsMode.Prefer, TlsMode.Disable];
    static readonly TlsMode[] MyModes = [TlsMode.Default, TlsMode.VerifyCa, TlsMode.Require, TlsMode.Prefer, TlsMode.Disable];

    [Theory]
    [InlineData("Server=h;Encrypt=False", TlsMode.Optional, null, "Encrypt=False")]
    [InlineData("Server=h;Encrypt=Optional", TlsMode.Optional, null, "Encrypt=False")]
    [InlineData("Server=h;Encrypt=no", TlsMode.Optional, null, "Encrypt=False")]
    [InlineData("Server=h;TrustServerCertificate=True", null, true, "TrustServerCertificate=True")]
    [InlineData("Server=h;Trust Server Certificate=yes", null, true, "TrustServerCertificate=True")]
    [InlineData("Server=h;Encrypt=True", TlsMode.Mandatory, null, null)]
    [InlineData("Server=h;Encrypt=Mandatory", TlsMode.Mandatory, null, null)]
    [InlineData("Server=h;Encrypt=Strict", TlsMode.Strict, null, null)]
    [InlineData("Server=h;TrustServerCertificate=False", null, false, null)]
    public void SqlServer_import(string cs, TlsMode? mode, bool? trust, string? noteNames)
    {
        foreach (var basisMode in SqlModes)
            foreach (var basisTrust in new[] { false, true })
            {
                var r = new SqlServerProvider().ParseConnectionString(cs, P(Engine.Mssql, basisMode, basisTrust));
                Assert.Equal(mode ?? basisMode, r.Profile.TlsMode);
                Assert.Equal(trust ?? basisTrust, r.Profile.TrustCert);
                if (noteNames == null) Assert.Empty(r.Notes);
                else Assert.Contains(r.Notes, n => n.StartsWith(noteNames, StringComparison.Ordinal));
            }
    }

    [Theory]
    [InlineData("Host=h;SSL Mode=Disable", TlsMode.Disable, "SSL Mode=Disable: the connection is not encrypted")]
    [InlineData("Host=h;sslmode=disable", TlsMode.Disable, "SSL Mode=Disable")]
    [InlineData("Host=h;SslMode=Allow", TlsMode.Prefer, "SSL Mode=Allow: TLS is used only when")]
    [InlineData("Host=h;SSL Mode=Prefer", TlsMode.Prefer, "SSL Mode=Prefer: TLS is used only when")]
    [InlineData("postgresql://u@h/db?sslmode=disable", TlsMode.Disable, "SSL Mode=Disable")]
    [InlineData("postgresql://u@h/db?sslmode=prefer", TlsMode.Prefer, "SSL Mode=Prefer")]
    [InlineData("Host=h;SSL Mode=Require", TlsMode.Default, null)]
    [InlineData("Host=h;SSL Mode=VerifyCA", TlsMode.VerifyCa, null)]
    [InlineData("Host=h;SSL Mode=VerifyFull", TlsMode.VerifyFull, null)]
    [InlineData("postgresql://u@h/db?sslmode=verify-full", TlsMode.VerifyFull, null)]
    public void Postgres_import(string cs, TlsMode mode, string? note)
    {
        foreach (var basis in PgModes)
        {
            var r = new PostgresProvider().ParseConnectionString(cs, P(Engine.Pg, basis, ca: "/keep"));
            Assert.Equal(mode, r.Profile.TlsMode);
            Assert.Equal("/keep", r.Profile.CaPath);
            if (note == null) Assert.Empty(r.Notes);
            else Assert.Contains(r.Notes, n => n.StartsWith(note, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("SslMode=None", TlsMode.Disable, "SslMode=None: the connection is not encrypted")]
    [InlineData("SslMode=Disabled", TlsMode.Disable, "SslMode=None")]
    [InlineData("SslMode=Preferred", TlsMode.Prefer, "SslMode=Preferred: TLS is used only when")]
    [InlineData("SSL Mode=Required", TlsMode.Require, "SslMode=Required: the connection is encrypted, but the server's certificate is not checked")]
    [InlineData("sslmode=required", TlsMode.Require, "SslMode=Required")]
    [InlineData("SslMode=VerifyCA", TlsMode.VerifyCa, "SslMode=VerifyCA: the certificate chain is checked, but not the server's host name.")]
    [InlineData("SslMode=VerifyFull", TlsMode.Default, null)]
    public void MySql_import(string tls, TlsMode mode, string? note)
    {
        foreach (var basis in MyModes)
        {
            var r = new MySqlProvider().ParseConnectionString($"Server=h;User ID=u;{tls}", P(Engine.MySql, basis, ca: "/keep"));
            Assert.Equal(mode, r.Profile.TlsMode);
            Assert.Equal("/keep", r.Profile.CaPath);
            if (note == null) Assert.Empty(r.Notes);
            else Assert.Contains(r.Notes, n => n.StartsWith(note, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void An_absent_tls_keyword_keeps_every_mode_and_adds_no_note()
    {
        foreach (var m in SqlModes)
            foreach (var t in new[] { false, true })
            {
                var r = new SqlServerProvider().ParseConnectionString("Server=h,1433;Database=d;User ID=u;Password=pw", P(Engine.Mssql, m, t));
                Assert.Equal((m, t), (r.Profile.TlsMode, r.Profile.TrustCert));
                Assert.Empty(r.Notes);
            }
        foreach (var m in PgModes)
        {
            foreach (var cs in new[] { "Host=h;Database=d;Username=u;Password=pw", "postgresql://u:pw@h/d" })
            {
                var r = new PostgresProvider().ParseConnectionString(cs, P(Engine.Pg, m, ca: "/ca"));
                Assert.Equal(m, r.Profile.TlsMode);
                Assert.Equal("/ca", r.Profile.CaPath);
                Assert.Empty(r.Notes);
            }
        }
        foreach (var m in MyModes)
        {
            var r = new MySqlProvider().ParseConnectionString("Server=h;Port=3306;User ID=u;Password=pw;Database=d", P(Engine.MySql, m, ca: "/ca"));
            Assert.Equal(m, r.Profile.TlsMode);
            Assert.Equal("/ca", r.Profile.CaPath);
            Assert.Empty(r.Notes);
        }
    }

    [Theory]
    [InlineData("Server=h;Database=d;User ID=u;Password=pw;Encrypt=False", Engine.Mssql, TlsMode.Optional, "Encrypt=False")]
    [InlineData("Server=h;Database=d;User ID=u;Password=pw;TrustServerCertificate=True", Engine.Mssql, TlsMode.Default, "TrustServerCertificate=True")]
    [InlineData("Host=h;Database=d;Username=u;Password=pw;SSL Mode=Disable", Engine.Pg, TlsMode.Disable, "SSL Mode=Disable")]
    [InlineData("postgres://u:pw@h/d?sslmode=prefer", Engine.Pg, TlsMode.Prefer, "SSL Mode=Prefer")]
    [InlineData("Server=h;User ID=u;Password=pw;Database=d;SslMode=Required", Engine.MySql, TlsMode.Require, "SslMode=Required")]
    public async Task Rpc_import_reports_the_weaker_setting_whatever_engine_the_editor_was_on(string cs, Engine engine, TlsMode mode, string note)
    {
        await using var host = new TestHost(new SqlServerProvider(), new PostgresProvider(), new MySqlProvider());
        foreach (var basis in new[] { Engine.Mssql, Engine.Pg, Engine.MySql })
        {
            var r = await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = P(basis, basis == Engine.Pg ? TlsMode.VerifyFull : TlsMode.Default), ConnectionString = cs });
            Assert.Equal(engine, r.Profile.Engine);
            Assert.Equal(mode, r.Profile.TlsMode);
            Assert.Contains(r.Notes, n => n.StartsWith(note, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Rpc_import_onto_another_engine_never_carries_the_editors_weak_mode()
    {
        // The editor is on MySQL Require (cert not checked); a SQL Server string without TLS keywords lands on the default.
        await using var host = new TestHost(new SqlServerProvider(), new PostgresProvider(), new MySqlProvider());
        var r = await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams
        {
            Profile = P(Engine.MySql, TlsMode.Require), ConnectionString = "Server=h,1433;Initial Catalog=d;User ID=u;Password=pw;ApplicationIntent=ReadOnly",
        });
        Assert.Equal(Engine.Mssql, r.Profile.Engine);
        Assert.Equal(TlsMode.Default, r.Profile.TlsMode);
        Assert.False(r.Profile.TrustCert);
    }

    // ---- File engines: no transport, so no TLS setting reaches them

    public static TheoryData<string> FileEngines => new() { "sqlite", "duckDb" };

    [Theory]
    [MemberData(nameof(FileEngines))]
    public void File_engines_have_no_connection_string_and_no_tls(string engine)
    {
        IDbProvider provider = engine == "sqlite" ? new SqliteProvider() : new DuckDbProvider();
        var p = P(provider.Engine, TlsMode.Disable, trust: true, ca: "/ca") with { Host = null, Location = LocationKind.Memory };
        Assert.Throws<NotSupportedException>(() => provider.BuildConnectionString(p, null, null));
        Assert.Throws<ArgumentException>(() => provider.ParseConnectionString("Data Source=x;SslMode=None", p));
    }
}

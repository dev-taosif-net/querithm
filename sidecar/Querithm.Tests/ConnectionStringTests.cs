using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Logging;

namespace Querithm.Tests;

/// <summary>profile.parseConnectionString: each driver's keyword rules, engine detection, and merge into the editor's profile.</summary>
public class ConnectionStringTests
{
    static Profile Basis(Engine engine) => new()
    {
        Id = "", Name = "Mine", Engine = engine, Host = "localhost", Auth = engine == Engine.Pg ? AuthKind.Password : AuthKind.Sql,
        Username = engine == Engine.Pg ? "postgres" : "sa", Environment = EnvironmentKind.Staging, GroupName = "Team", TimeoutS = 15,
    };

    static async Task<ParseConnectionStringResult> Parse(string cs, Engine engine = Engine.Mssql)
    {
        await using var host = new TestHost();
        return await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = Basis(engine), ConnectionString = cs });
    }

    [Fact]
    public async Task SqlServer_keywords_map_onto_profile_fields()
    {
        var r = await Parse("Server=tcp:db.example.com,1500;Initial Catalog=Sales;User ID=app;Password=\"p;w\";Encrypt=Strict;TrustServerCertificate=True;Connect Timeout=30;ApplicationIntent=ReadOnly;MultiSubnetFailover=True;Application Name=Etl");
        var p = r.Profile;
        Assert.Equal(Engine.Mssql, p.Engine);
        Assert.Equal("tcp:db.example.com", p.Host);
        Assert.Equal(1500, p.Port);
        Assert.Equal("Sales", p.DatabaseDefault);
        Assert.Equal(AuthKind.Sql, p.Auth);
        Assert.Equal("app", p.Username);
        Assert.Equal("p;w", r.Secret);
        Assert.Equal(TlsMode.Strict, p.TlsMode);
        Assert.True(p.TrustCert);
        Assert.Equal(30, p.TimeoutS);
        Assert.Equal("readOnly", p.Options!["applicationIntent"]);
        Assert.Equal("true", p.Options!["multiSubnetFailover"]);
        Assert.Equal("Etl", p.Options!["appName"]);
        Assert.Empty(r.Unknown);
        Assert.Empty(r.NotApplied);
        // Identity fields are the editor's, not the string's.
        Assert.Equal("Mine", p.Name);
        Assert.Equal(EnvironmentKind.Staging, p.Environment);
        Assert.Equal("Team", p.GroupName);
    }

    [Fact]
    public async Task SqlServer_integrated_and_entra_auth()
    {
        Assert.Equal(AuthKind.Windows, (await Parse("Data Source=srv\\SQL2022;Integrated Security=SSPI")).Profile.Auth);
        Assert.Equal("srv\\SQL2022", (await Parse("Data Source=srv\\SQL2022;Trusted_Connection=yes")).Profile.Host);
        Assert.Equal(AuthKind.EntraInteractive, (await Parse("Server=x.database.windows.net;Authentication=Active Directory Interactive;User ID=me@example.com")).Profile.Auth);
        var unsupported = await Parse("Server=x;Authentication=Active Directory Password;User ID=a;Password=b");
        Assert.Contains(unsupported.NotApplied, n => n.StartsWith("Authentication=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Postgres_keywords_are_detected_even_when_editor_is_on_sql_server()
    {
        var r = await Parse("Host=pg.local;Port=6432;Database=app;Username=svc;Password=s3cret;SSL Mode=VerifyFull;Root Certificate=/ca.crt;Timeout=20;Pooling=false", Engine.Mssql);
        var p = r.Profile;
        Assert.Equal(Engine.Pg, p.Engine);
        Assert.Equal("pg.local", p.Host);
        Assert.Equal(6432, p.Port);
        Assert.Equal("app", p.DatabaseDefault);
        Assert.Equal("svc", p.Username);
        Assert.Equal(AuthKind.Password, p.Auth);
        Assert.Equal("s3cret", r.Secret);
        Assert.Equal(TlsMode.VerifyFull, p.TlsMode);
        Assert.Equal("/ca.crt", p.CaPath);
        Assert.Equal(20, p.TimeoutS);
        Assert.Equal(["Pooling"], r.NotApplied);
        Assert.Empty(r.Unknown);
    }

    [Fact]
    public async Task Postgres_uri_is_accepted()
    {
        var r = await Parse("postgresql://svc:p%40ss@[::1]:5433/my%20db?sslmode=verify-ca&application_name=etl&foo=1");
        Assert.Equal(Engine.Pg, r.Profile.Engine);
        Assert.Equal("::1", r.Profile.Host);
        Assert.Equal(5433, r.Profile.Port);
        Assert.Equal("my db", r.Profile.DatabaseDefault);
        Assert.Equal("svc", r.Profile.Username);
        Assert.Equal("p@ss", r.Secret);
        Assert.Equal(TlsMode.VerifyCa, r.Profile.TlsMode);
        Assert.Equal("etl", r.Profile.Options!["appName"]);
        Assert.Equal(["foo"], r.Unknown);
    }

    [Fact]
    public async Task Ambiguous_keywords_keep_the_editors_engine_and_unnamed_fields()
    {
        var pg = await Parse("Server=h;Database=d", Engine.Pg);
        Assert.Equal(Engine.Pg, pg.Profile.Engine);
        Assert.Equal("postgres", pg.Profile.Username); // not named → kept
        Assert.Null(pg.Secret);
        Assert.Equal(Engine.Mssql, (await Parse("Server=h;Database=d", Engine.Mssql)).Profile.Engine);
    }

    [Fact]
    public async Task Keywords_both_drivers_accept_go_to_the_engine_that_applies_more_of_them()
    {
        // Npgsql knows TrustServerCertificate but Querithm does not apply it, so SQL Server wins over the editor's PostgreSQL.
        var r = await Parse("Server=localhost,14333;Database=master;User ID=sa;Password=pw;TrustServerCertificate=True", Engine.Pg);
        Assert.Equal(Engine.Mssql, r.Profile.Engine);
        Assert.Equal(14333, r.Profile.Port);
        Assert.Equal(AuthKind.Sql, r.Profile.Auth);
    }

    [Fact]
    public async Task Unknown_keywords_are_reported_not_thrown()
    {
        var r = await Parse("Server=h;Frobnicate=1;Connect Timeout=abc");
        Assert.Contains("Frobnicate", r.Unknown);
        Assert.Contains(r.Unknown, u => u.Contains("invalid value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("this is not a connection string")]
    [InlineData("Frobnicate=1;Wibble=2")]
    [InlineData("Server=h;Password='unterminated")]
    public async Task Malformed_strings_are_rejected(string cs)
    {
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Parse(cs));
        Assert.DoesNotContain("unterminated", ex.Message);
    }

    // ---- M3: a TLS keyword the string does not name never weakens the mode; one that weakens it is reported.

    static ParseConnectionStringResult ParseMySql(string cs, TlsMode basisTls = TlsMode.Default) =>
        new Providers.MySql.MySqlProvider().ParseConnectionString(cs, Basis(Engine.MySql) with { Auth = AuthKind.Password, Username = "root", TlsMode = basisTls });

    [Fact]
    public void MySql_keywords_map_onto_profile_fields()
    {
        var r = ParseMySql("Server=my.example.com;Port=3307;User ID=app;Password=pw;Database=shop;SslMode=VerifyCA;SslCa=/ca.pem");
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Equal("my.example.com", r.Profile.Host);
        Assert.Equal(3307, r.Profile.Port);
        Assert.Equal("app", r.Profile.Username);
        Assert.Equal("shop", r.Profile.DatabaseDefault);
        Assert.Equal("pw", r.Secret);
        Assert.Equal(TlsMode.VerifyCa, r.Profile.TlsMode);
        Assert.Equal("/ca.pem", r.Profile.CaPath);
        Assert.Equal("Mine", r.Profile.Name);
    }

    [Theory]
    [InlineData(TlsMode.Default)]
    [InlineData(TlsMode.VerifyCa)]
    [InlineData(TlsMode.VerifyFull)]
    public void MySql_string_without_SslMode_keeps_the_editors_tls_mode(TlsMode basisTls)
    {
        // MySqlConnector reads an absent SslMode as Preferred; the profile must keep VerifyFull (the default) or the user's choice.
        var r = ParseMySql("Server=db;User ID=app;Password=pw;Database=shop", basisTls);
        Assert.Equal(basisTls, r.Profile.TlsMode);
        Assert.Empty(r.Notes);
    }

    [Theory]
    [InlineData("SslMode=None", TlsMode.Disable, "not encrypted")]
    [InlineData("SSL Mode=None", TlsMode.Disable, "not encrypted")]
    [InlineData("SslMode=Preferred", TlsMode.Prefer, "only when the server offers it")]
    [InlineData("SslMode=Required", TlsMode.Require, "certificate is not checked")]
    [InlineData("ssl mode=VerifyCA", TlsMode.VerifyCa, "host name")]
    public void MySql_weaker_SslMode_is_applied_and_reported(string setting, TlsMode expected, string warning)
    {
        var r = ParseMySql($"Server=db;User ID=app;{setting}");
        Assert.Equal(expected, r.Profile.TlsMode);
        Assert.Contains(r.Notes, n => n.Contains(warning, StringComparison.Ordinal));
        Assert.DoesNotContain(r.NotApplied, n => n.Contains("ssl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MySql_SslMode_VerifyFull_is_the_default_and_not_reported()
    {
        var r = ParseMySql("Server=db;User ID=app;SslMode=VerifyFull", TlsMode.Require);
        Assert.Equal(TlsMode.Default, r.Profile.TlsMode);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public async Task SqlServer_absent_tls_keywords_keep_the_editors_choice()
    {
        await using var host = new TestHost();
        var basis = Basis(Engine.Mssql) with { TlsMode = TlsMode.Strict, TrustCert = false };
        var r = await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = "Server=h,1433;Database=d;User ID=u;Password=p" });
        Assert.Equal(TlsMode.Strict, r.Profile.TlsMode);
        Assert.False(r.Profile.TrustCert);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public async Task SqlServer_weaker_tls_keywords_are_reported()
    {
        var r = await Parse("Server=h;Database=d;User ID=u;Encrypt=False;TrustServerCertificate=True");
        Assert.Equal(TlsMode.Optional, r.Profile.TlsMode);
        Assert.True(r.Profile.TrustCert);
        Assert.Contains(r.Notes, n => n.StartsWith("Encrypt=False", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.StartsWith("TrustServerCertificate=True", StringComparison.Ordinal));
        Assert.Empty((await Parse("Server=h;Database=d;User ID=u;Encrypt=Strict;TrustServerCertificate=False")).Notes);
    }

    [Fact]
    public async Task Postgres_absent_ssl_mode_keeps_the_editors_choice()
    {
        await using var host = new TestHost();
        var basis = Basis(Engine.Pg) with { TlsMode = TlsMode.VerifyFull };
        var r = await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = "Host=pg;Database=app;Username=svc" });
        Assert.Equal(TlsMode.VerifyFull, r.Profile.TlsMode);
        Assert.Empty(r.Notes);
        var uri = await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = "postgresql://svc@pg/app" });
        Assert.Equal(TlsMode.VerifyFull, uri.Profile.TlsMode);
    }

    [Theory]
    [InlineData("Host=pg;Username=svc;SSL Mode=Disable", TlsMode.Disable, "not encrypted")]
    [InlineData("Host=pg;Username=svc;SSL Mode=Prefer", TlsMode.Prefer, "only when the server offers it")]
    [InlineData("Host=pg;Username=svc;SSL Mode=Allow", TlsMode.Prefer, "only when the server offers it")]
    [InlineData("postgres://svc@pg/app?sslmode=disable", TlsMode.Disable, "not encrypted")]
    public async Task Postgres_weaker_ssl_mode_is_applied_and_reported(string cs, TlsMode expected, string warning)
    {
        var r = await Parse(cs, Engine.Pg);
        Assert.Equal(expected, r.Profile.TlsMode);
        Assert.Contains(r.Notes, n => n.Contains(warning, StringComparison.Ordinal));
        // Require is the PostgreSQL default (§13 rule 2): naming it is not a downgrade.
        Assert.Empty((await Parse("Host=pg;Username=svc;SSL Mode=Require", Engine.Pg)).Notes);
    }

    // §13 rule 1: a secret that arrived inside a connection string is scrubbed from logs afterwards.
    [Fact]
    public async Task Parsed_secret_is_registered_with_the_scrubber()
    {
        await Parse("postgres://u:Zq9-uri-secret@h/db");
        Assert.DoesNotContain("Zq9-uri-secret", LogScrubber.Scrub("failed for Zq9-uri-secret"));
    }
}

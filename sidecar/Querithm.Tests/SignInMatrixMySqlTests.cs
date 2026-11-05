using MySqlConnector;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Logging;
using Querithm.Core.Sessions;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// Sign-in matrix, MySQL / MariaDB: the one sign-in (user + password), the driver settings Querithm builds from a
/// profile and its secret, and profile.parseConnectionString with every shipped provider registered (as Program.cs does),
/// where SQL Server, PostgreSQL and MySQL keyword strings overlap.
/// </summary>
public sealed class SignInMatrixMySqlTests : IAsyncDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qsignin-my-").FullName;

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    static Profile MyProfile() => new()
    {
        Id = "p1", Name = "shop", Engine = Engine.MySql, Host = "db.local", Auth = AuthKind.Password, Username = "root",
        Environment = EnvironmentKind.Dev, TlsMode = TlsMode.Default, TimeoutS = 15, CommandTimeoutS = 0,
    };

    static MySqlConnectionStringBuilder Built(Profile p, string? secret = "pw", string? database = null) =>
        new(new MySqlProvider().BuildConnectionString(p, secret, database));

    // The registry the shipped sidecar builds (Program.cs), in the same order.
    TestHost AllEngines() => new(new SqlServerProvider(), new PostgresProvider(), new SqliteProvider(), new MySqlProvider(), new DuckDbProvider(Path.Combine(_dir, "duck")));

    // ---------------------------------------------------------------------------------------------- the one sign-in

    [Fact]
    public void The_engine_offers_only_password_sign_in_on_a_server()
    {
        var t = new MySqlProvider().Traits;
        Assert.Equal([AuthKind.Password], t.AuthKinds);
        Assert.Equal(AuthKind.Password, t.DefaultAuth);
        Assert.Equal(3306, t.DefaultPort);
        Assert.Equal([LocationKind.Server], t.Locations);
        Assert.True(ProfileRules.NeedsSecret(AuthKind.Password));
    }

    [Theory]
    [InlineData(AuthKind.Password, true)]
    [InlineData(AuthKind.Sql, true)] // a profile switched from SQL Server keeps working: same user + password
    [InlineData(AuthKind.Windows, false)]
    [InlineData(AuthKind.Kerberos, false)]
    [InlineData(AuthKind.EntraToken, false)]
    [InlineData(AuthKind.EntraInteractive, false)]
    [InlineData(AuthKind.None, false)]
    public void Only_a_user_and_password_sign_in_is_accepted(AuthKind auth, bool ok)
    {
        var reason = ProfileRules.Validate(MyProfile() with { Auth = auth }, new MySqlProvider());
        if (ok) Assert.Null(reason);
        else Assert.Equal("MySQL / MariaDB signs in with a user name and password.", reason);
    }

    [Theory]
    [InlineData(LocationKind.File)]
    [InlineData(LocationKind.Memory)]
    public void A_file_or_memory_location_is_refused(LocationKind where)
    {
        var p = MyProfile() with { Location = where, File = where == LocationKind.File ? new FileLocation { GrantId = "g1" } : null };
        Assert.Equal("A MySQL / MariaDB profile connects to a server.", ProfileRules.Validate(p, new MySqlProvider()));
    }

    // ---------------------------------------------------------------------------------------------- driver settings

    [Fact]
    public void Profile_and_secret_become_the_driver_settings()
    {
        var b = Built(MyProfile() with { Port = 3307, Username = "app", DatabaseDefault = "shop", TimeoutS = 20, CommandTimeoutS = 90 }, "s3cret");
        Assert.Equal("db.local", b.Server);
        Assert.Equal(3307u, b.Port);
        Assert.Equal("app", b.UserID);
        Assert.Equal("s3cret", b.Password);
        Assert.Equal("shop", b.Database);
        Assert.Equal(20u, b.ConnectionTimeout);
        Assert.Equal(90u, b.DefaultCommandTimeout);
        Assert.Equal("Querithm", b.ApplicationName);
        Assert.Equal(MySqlConnectionProtocol.Sockets, b.ConnectionProtocol);
        // Hardening every MySQL connection carries, whatever the profile says.
        Assert.False(b.Pooling);
        Assert.False(b.AllowLoadLocalInfile);
        Assert.False(b.AllowPublicKeyRetrieval);
        Assert.True(b.AllowUserVariables);
    }

    [Fact]
    public void The_profiles_defaults_fill_in_port_host_user_and_database()
    {
        var b = Built(MyProfile() with { Host = "  ", Port = null, Username = null, DatabaseDefault = null }, null);
        Assert.Equal("localhost", b.Server);
        Assert.Equal(3306u, b.Port);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.Password); // no secret: an empty password, never a placeholder
        Assert.Equal("", b.Database);
        Assert.Equal(15u, Built(MyProfile() with { TimeoutS = null }).ConnectionTimeout);
        Assert.Equal(0u, Built(MyProfile() with { CommandTimeoutS = null }).DefaultCommandTimeout);
    }

    [Fact]
    public void The_database_asked_for_wins_over_the_profiles_default()
    {
        Assert.Equal("other", Built(MyProfile() with { DatabaseDefault = "shop" }, "pw", "other").Database);
    }

    [Fact]
    public void A_secret_with_connection_string_metacharacters_stays_one_password()
    {
        var b = Built(MyProfile(), "p;w=\"x'");
        Assert.Equal("p;w=\"x'", b.Password);
        Assert.Equal("root", b.UserID);
        Assert.Equal("", b.Database);
    }

    [Fact]
    public void A_user_name_cannot_smuggle_a_setting_in()
    {
        var b = Built(MyProfile() with { Username = "root;AllowLoadLocalInfile=true" });
        Assert.Equal("root;AllowLoadLocalInfile=true", b.UserID);
        Assert.False(b.AllowLoadLocalInfile);
    }

    [Theory]
    [InlineData(TlsMode.Default, MySqlSslMode.VerifyFull)]
    [InlineData(TlsMode.VerifyFull, MySqlSslMode.VerifyFull)]
    [InlineData(TlsMode.VerifyCa, MySqlSslMode.VerifyCA)]
    [InlineData(TlsMode.Require, MySqlSslMode.Required)]
    [InlineData(TlsMode.Prefer, MySqlSslMode.Preferred)]
    [InlineData(TlsMode.Disable, MySqlSslMode.None)]
    public void Each_tls_mode_the_editor_offers_maps_to_the_driver(TlsMode mode, MySqlSslMode expected)
    {
        Assert.Equal(expected, Built(MyProfile() with { TlsMode = mode }).SslMode);
    }

    [Fact]
    public void The_ca_file_is_passed_and_trust_cert_has_no_effect()
    {
        Assert.Equal("/ca.pem", Built(MyProfile() with { TlsMode = TlsMode.VerifyCa, CaPath = "/ca.pem" }).SslCa);
        Assert.Equal("", Built(MyProfile()).SslCa);
        Assert.Equal(MySqlSslMode.VerifyFull, Built(MyProfile() with { TrustCert = true }).SslMode);
    }

    [Fact]
    public void A_host_that_is_a_path_is_a_unix_socket()
    {
        var b = Built(MyProfile() with { Host = "/var/run/mysqld/mysqld.sock" });
        Assert.Equal(MySqlConnectionProtocol.UnixSocket, b.ConnectionProtocol);
        Assert.Equal("/var/run/mysqld/mysqld.sock", b.Server);
    }

    [Fact]
    public void Extras_in_options_never_reach_the_driver()
    {
        var b = Built(MyProfile() with { Options = new() { ["AllowLoadLocalInfile"] = "true", ["Pooling"] = "true", ["AllowPublicKeyRetrieval"] = "true" } });
        Assert.False(b.AllowLoadLocalInfile);
        Assert.False(b.Pooling);
        Assert.False(b.AllowPublicKeyRetrieval);
    }

    // ---------------------------------------------------------------------------------------------- parseConnectionString

    static Profile Basis(Engine engine) => engine switch
    {
        Engine.MySql => new Profile { Id = "", Name = "", Engine = Engine.MySql, Host = "localhost", Auth = AuthKind.Password, Username = "root", Environment = EnvironmentKind.Dev, TimeoutS = 15 },
        Engine.Pg => new Profile { Id = "", Name = "", Engine = Engine.Pg, Host = "localhost", Auth = AuthKind.Password, Username = "postgres", DatabaseDefault = "postgres", Environment = EnvironmentKind.Dev, TimeoutS = 15 },
        // A new profile with no server type chosen sends the editor's placeholder engine (DEFAULT_ENGINE = mssql).
        _ => new Profile { Id = "", Name = "", Engine = Engine.Mssql, Host = "localhost", Auth = AuthKind.Sql, Username = "sa", Environment = EnvironmentKind.Dev, TimeoutS = 15 },
    };

    async Task<ParseConnectionStringResult> Parse(string cs, Engine editor)
    {
        await using var host = AllEngines();
        return await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = Basis(editor), ConnectionString = cs });
    }

    [Fact]
    public async Task The_mysql_example_the_editor_shows_is_read_as_mysql_while_mysql_is_chosen()
    {
        var r = await Parse("Server=my.example.com;Port=3307;User ID=app;Password=pw;Database=shop", Engine.MySql);
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Equal("my.example.com", r.Profile.Host);
        Assert.Equal(3307, r.Profile.Port);
        Assert.Equal("app", r.Profile.Username);
        Assert.Equal(AuthKind.Password, r.Profile.Auth);
        Assert.Equal("shop", r.Profile.DatabaseDefault);
        Assert.Equal("pw", r.Secret);
        Assert.Equal(TlsMode.Default, r.Profile.TlsMode); // no SslMode named: VerifyFull stays
        Assert.Empty(r.Unknown);
        Assert.Empty(r.NotApplied);
    }

    [Fact]
    public async Task The_default_port_is_stored_as_the_engines_default()
    {
        var r = await Parse("Server=h;Port=3306;User ID=app;Password=pw", Engine.MySql);
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Null(r.Profile.Port);
    }

    [Theory]
    // Keywords SqlClient and MySqlConnector both read the same way: the editor's engine wins the tie.
    [InlineData("Server=h;User ID=u;Password=pw;Database=d", Engine.MySql, Engine.MySql)]
    [InlineData("Server=h;User ID=u;Password=pw;Database=d", Engine.Mssql, Engine.Mssql)]
    [InlineData("Server=h;Database=d", Engine.MySql, Engine.MySql)]
    // A keyword only SqlClient knows is decisive.
    [InlineData("Server=h;User ID=u;Password=pw;Encrypt=Strict", Engine.MySql, Engine.Mssql)]
    [InlineData("Server=h,1433;User ID=u;Password=pw;Initial Catalog=d;MultiSubnetFailover=True", Engine.MySql, Engine.Mssql)]
    public async Task Server_and_user_id_strings_go_to_the_engine_whose_keywords_they_use(string cs, Engine editor, Engine expected)
    {
        Assert.Equal(expected, (await Parse(cs, editor)).Profile.Engine);
    }

    [Fact]
    public async Task A_mysql_only_keyword_makes_it_mysql_even_with_the_editor_on_sql_server()
    {
        // SslMode is not a SqlClient keyword: SQL Server reports it unknown, so it cannot win.
        var r = await Parse("Server=h;User ID=u;Password=pw;SslMode=VerifyCA;SslCa=/ca.pem", Engine.Mssql);
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Equal(TlsMode.VerifyCa, r.Profile.TlsMode);
        Assert.Equal("/ca.pem", r.Profile.CaPath);
        Assert.Equal(AuthKind.Password, r.Profile.Auth);
    }

    [Fact]
    public async Task A_string_read_as_mysql_resets_sql_server_only_fields()
    {
        var basis = Basis(Engine.Mssql) with { TrustCert = true, TlsMode = TlsMode.Optional, Options = new() { ["applicationIntent"] = "readOnly" } };
        await using var host = AllEngines();
        var r = await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = "Server=h;User ID=u;Password=pw;SslMode=Required;SslCa=/ca.pem" });
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Equal(AuthKind.Password, r.Profile.Auth);
        Assert.False(r.Profile.TrustCert);
        Assert.Null(r.Profile.Options);
        Assert.Equal(TlsMode.Require, r.Profile.TlsMode);
    }

    [Fact]
    public async Task A_string_without_a_user_keeps_the_editors_user_and_has_no_secret()
    {
        var r = await Parse("Server=h;Database=d", Engine.MySql);
        Assert.Equal("root", r.Profile.Username);
        Assert.Null(r.Secret);
    }

    [Fact]
    public async Task The_password_from_a_mysql_string_is_scrubbed_from_logs()
    {
        await Parse("Server=h;User ID=u;Password=Zq9-my-secret;Database=d", Engine.MySql);
        Assert.DoesNotContain("Zq9-my-secret", LogScrubber.Scrub("failed for Zq9-my-secret"));
    }

    [Fact]
    public async Task File_engines_never_read_a_connection_string()
    {
        await using var host = new TestHost(new SqliteProvider(), new DuckDbProvider(Path.Combine(_dir, "duck2")));
        var basis = new Profile { Id = "", Name = "", Engine = Engine.Sqlite, Auth = AuthKind.None, Location = LocationKind.File, Environment = EnvironmentKind.Dev };
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis, ConnectionString = "Data Source=/tmp/x.db" }));
        Assert.Contains("file dialog", ex.Message);
    }
}

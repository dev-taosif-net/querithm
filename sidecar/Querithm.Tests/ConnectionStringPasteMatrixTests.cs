using System.Diagnostics;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Logging;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// profile.parseConnectionString through the RPC with every engine registered: per-engine round trips (host, port,
/// database, user, secret, TLS keywords), detection from a new profile's placeholder engine, and refusal of junk input
/// (§13 rule: validate untrusted RPC input). SQLite and DuckDB never read a connection string.
/// </summary>
public sealed class ConnectionStringPasteMatrixTests : IAsyncDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qcsm-").FullName;
    readonly TestHost _host;

    public ConnectionStringPasteMatrixTests() =>
        _host = new TestHost(new SqlServerProvider(), new PostgresProvider(), new MySqlProvider(), new SqliteProvider(), new DuckDbProvider(_dir));

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    static Profile Basis(Engine engine) => new()
    {
        Id = "", Name = "", Engine = engine, Host = "localhost",
        Auth = engine switch { Engine.Mssql => AuthKind.Sql, Engine.Pg or Engine.MySql => AuthKind.Password, _ => AuthKind.None },
        Username = null, Environment = EnvironmentKind.Dev, TimeoutS = 15,
    };

    Task<ParseConnectionStringResult> Parse(string cs, Engine engine = Engine.Mssql, Profile? basis = null) =>
        _host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis ?? Basis(engine), ConnectionString = cs });

    // ---- round trips, each engine, from each editor engine (a new profile's placeholder is SQL Server) ----

    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.Pg)]
    [InlineData(Engine.MySql)]
    [InlineData(Engine.Sqlite)]
    public async Task Sql_server_string_round_trips(Engine editor)
    {
        var r = await Parse("Server=tcp:sql.example.test,14330;Initial Catalog=Sales;User ID=app;Password=s3cret;Encrypt=Strict;TrustServerCertificate=False", editor);
        Assert.Equal(Engine.Mssql, r.Profile.Engine);
        Assert.Equal(("tcp:sql.example.test", 14330, "Sales", "app"), (r.Profile.Host, r.Profile.Port, r.Profile.DatabaseDefault, r.Profile.Username));
        Assert.Equal(AuthKind.Sql, r.Profile.Auth);
        Assert.Equal("s3cret", r.Secret);
        Assert.Equal(TlsMode.Strict, r.Profile.TlsMode);
        Assert.False(r.Profile.TrustCert);
        Assert.Empty(r.Notes);
    }

    [Theory]
    [InlineData("Encrypt=Mandatory", TlsMode.Mandatory, false)]
    [InlineData("Encrypt=True", TlsMode.Mandatory, false)]
    [InlineData("Encrypt=Optional", TlsMode.Optional, true)]
    public async Task Sql_server_encrypt_keywords(string setting, TlsMode expected, bool noted)
    {
        var r = await Parse($"Server=h;User ID=u;Password=pw;{setting}");
        Assert.Equal(expected, r.Profile.TlsMode);
        Assert.Equal(noted, r.Notes.Length > 0);
    }

    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.Pg)]
    [InlineData(Engine.MySql)]
    public async Task Postgres_keyword_string_round_trips(Engine editor)
    {
        var r = await Parse("Host=pg.example.test;Port=6432;Database=app;Username=svc;Password=s3cret;SSL Mode=VerifyFull;Root Certificate=/ca.crt", editor);
        Assert.Equal(Engine.Pg, r.Profile.Engine);
        Assert.Equal(("pg.example.test", 6432, "app", "svc"), (r.Profile.Host, r.Profile.Port, r.Profile.DatabaseDefault, r.Profile.Username));
        Assert.Equal("s3cret", r.Secret);
        Assert.Equal(TlsMode.VerifyFull, r.Profile.TlsMode);
        Assert.Equal("/ca.crt", r.Profile.CaPath);
    }

    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.MySql)]
    public async Task Postgres_uri_round_trips_from_any_editor_engine(Engine editor)
    {
        var r = await Parse("postgresql://svc:s3cret@pg.example.test:6432/app?sslmode=verify-ca", editor);
        Assert.Equal(Engine.Pg, r.Profile.Engine);
        Assert.Equal(("pg.example.test", 6432, "app", "svc"), (r.Profile.Host, r.Profile.Port, r.Profile.DatabaseDefault, r.Profile.Username));
        Assert.Equal("s3cret", r.Secret);
        Assert.Equal(TlsMode.VerifyCa, r.Profile.TlsMode);
    }

    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.Pg)]
    [InlineData(Engine.MySql)]
    public async Task MySql_string_with_mysql_only_keywords_round_trips(Engine editor)
    {
        var r = await Parse("Server=my.example.test;Port=3307;User ID=app;Password=s3cret;Database=shop;SslMode=VerifyCA;SslCa=/ca.pem", editor);
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Equal(("my.example.test", 3307, "shop", "app"), (r.Profile.Host, r.Profile.Port, r.Profile.DatabaseDefault, r.Profile.Username));
        Assert.Equal("s3cret", r.Secret);
        Assert.Equal(TlsMode.VerifyCa, r.Profile.TlsMode);
        Assert.Equal("/ca.pem", r.Profile.CaPath);
        Assert.Contains(r.Notes, n => n.Contains("host name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MySql_editor_keeps_mysql_for_a_string_both_drivers_read()
    {
        var r = await Parse("Server=my.example.test;User ID=app;Password=pw;Database=shop", Engine.MySql);
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Equal("pw", r.Secret);
    }

    [Theory]
    [InlineData("SslMode=None", TlsMode.Disable)]
    [InlineData("SslMode=Preferred", TlsMode.Prefer)]
    [InlineData("SslMode=Required", TlsMode.Require)]
    [InlineData("SslMode=VerifyFull", TlsMode.Default)]
    public async Task MySql_ssl_keywords_through_the_rpc(string setting, TlsMode expected)
    {
        var r = await Parse($"Server=h;Port=3307;User ID=u;{setting}", Engine.MySql);
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Equal(expected, r.Profile.TlsMode);
        Assert.Equal(expected != TlsMode.Default, r.Notes.Length > 0);
    }

    [Theory]
    [InlineData(Engine.Mssql, "Server=h;Password=s3cret")]
    [InlineData(Engine.Pg, "Host=h;Password=s3cret")]
    [InlineData(Engine.MySql, "Server=h;Port=3307;Password=s3cret")]
    public async Task Parsed_secret_never_appears_in_unknown_notApplied_or_notes(Engine editor, string cs)
    {
        var r = await Parse(cs, editor);
        Assert.Equal("s3cret", r.Secret);
        Assert.DoesNotContain(r.Unknown.Concat(r.NotApplied).Concat(r.Notes), s => s.Contains("s3cret", StringComparison.Ordinal));
    }

    // ---- junk and oversized input ----

    [Theory]
    [InlineData("just words")]
    [InlineData("=;=;=")]
    [InlineData("Frobnicate=1")]
    [InlineData("mysql://u:p@h/db")]
    public async Task Junk_is_refused_with_an_invalid_error(string cs)
    {
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Parse(cs));
        Assert.Equal("invalid", ex.Code);
    }

    [Fact]
    public async Task A_refusal_never_echoes_the_secret()
    {
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Parse("Frobnicate=1;Password='Zq7-never-echo"));
        Assert.DoesNotContain("Zq7-never-echo", ex.Message);
    }

    [Theory]
    [InlineData(Engine.Sqlite)]
    [InlineData(Engine.DuckDb)]
    public async Task File_engines_never_take_a_connection_string_by_themselves(Engine engine)
    {
        // Data Source= is SQLite's own keyword: it must still not become a file profile (§13 rule 6: files are picked).
        var r = await Parse("Data Source=/tmp/x.db", engine);
        Assert.NotEqual(Engine.Sqlite, r.Profile.Engine);
        Assert.NotEqual(Engine.DuckDb, r.Profile.Engine);
    }

    [Theory]
    [InlineData(Engine.Mssql, "Server=bad\u0001host;User ID=u;Password=pw")]
    [InlineData(Engine.Pg, "Host=bad\u0001host;Username=u;Password=pw")]
    [InlineData(Engine.MySql, "Server=bad\u0001host;Port=3307;User ID=u;Password=pw")]
    public async Task A_control_character_host_is_refused_by_the_parser_or_on_test(Engine editor, string cs)
    {
        ParseConnectionStringResult r;
        try { r = await Parse(cs, editor); }
        catch (QuerithmException ex) { Assert.Equal("invalid", ex.Code); return; }
        var t = await _host.Rpc.ProfileTest(new ProfileTestParams { Profile = r.Profile, Secret = r.Secret });
        Assert.False(t.Ok);
        Assert.Contains("control characters", t.Error);
    }

    // ---- regressions: engine detection when several drivers read every keyword ----

    public static TheoryData<Engine> EveryBasis => new() { Engine.Mssql, Engine.Pg, Engine.MySql, Engine.Sqlite, Engine.DuckDb };

    // Npgsql reads Server / Port / User ID / Password / Database too: before the fix this tied at 0 unknown and
    // PostgreSQL won by registration order from a new profile's placeholder (SQL Server) basis.
    [Theory]
    [MemberData(nameof(EveryBasis))]
    public async Task Typical_mysql_string_is_mysql_from_every_basis(Engine editor)
    {
        foreach (var cs in new[]
        {
            "Server=localhost;Port=3306;User ID=root;Password=pw;Database=shop",
            "Server=localhost;Port=3306;Uid=root;Pwd=pw;Database=shop",
            "Server=db.example.test;Uid=root;Pwd=pw;Database=shop;SslMode=Required",
        })
        {
            var r = await Parse(cs, editor);
            Assert.True(r.Profile.Engine == Engine.MySql, $"{editor}: '{cs}' read as {r.Profile.Engine}");
            Assert.Equal("pw", r.Secret);
            Assert.Equal("shop", r.Profile.DatabaseDefault);
        }
    }

    [Theory]
    [MemberData(nameof(EveryBasis))]
    public async Task Typical_postgres_strings_are_postgres_from_every_basis(Engine editor)
    {
        foreach (var cs in new[]
        {
            "Host=localhost;Port=5432;Username=postgres;Password=pw;Database=app",
            "Server=localhost;Port=5432;User ID=postgres;Password=pw;Database=app",
            "Host=localhost;Username=postgres;Password=pw;Database=app",
            "postgresql://postgres:pw@localhost/app",
        })
        {
            var r = await Parse(cs, editor);
            Assert.True(r.Profile.Engine == Engine.Pg, $"{editor}: '{cs}' read as {r.Profile.Engine}");
            Assert.Equal("pw", r.Secret);
        }
    }

    [Theory]
    [MemberData(nameof(EveryBasis))]
    public async Task Typical_sql_server_strings_are_sql_server_from_every_basis(Engine editor)
    {
        foreach (var cs in new[]
        {
            "Server=localhost,1433;Database=app;User ID=sa;Password=pw",
            "Server=tcp:sql.example.test,1433;Initial Catalog=app;User ID=sa;Password=pw;Encrypt=True",
            "Data Source=localhost;Initial Catalog=app;User ID=sa;Password=pw",
            "Server=srv\\SQL2022;Database=app;User ID=sa;Password=pw",
            "Server=localhost;Database=app;User ID=sa;Password=pw;TrustServerCertificate=True",
        })
        {
            var r = await Parse(cs, editor);
            Assert.True(r.Profile.Engine == Engine.Mssql, $"{editor}: '{cs}' read as {r.Profile.Engine}");
            Assert.Equal("pw", r.Secret);
        }
    }

    // A string every driver reads alike keeps the editor's engine (the only tie-break left).
    [Theory]
    [InlineData(Engine.Mssql)]
    [InlineData(Engine.Pg)]
    [InlineData(Engine.MySql)]
    public async Task A_string_every_driver_reads_alike_keeps_the_editors_engine(Engine editor)
    {
        Assert.Equal(editor, (await Parse("Server=h;Database=d;Password=pw", editor)).Profile.Engine);
    }

    // With SQL Server out (Port is not a SqlClient keyword) and no default port, MySQL's spellings beat a placeholder basis.
    [Fact]
    public async Task Server_and_user_id_with_another_port_prefer_mysql_over_postgres_from_the_placeholder()
    {
        var r = await Parse("Server=h;Port=3307;User ID=u;Password=pw;Database=d", Engine.Mssql);
        Assert.Equal(Engine.MySql, r.Profile.Engine);
        Assert.Equal(3307, r.Profile.Port);
    }

    // ---- regressions: a quoted value holding ";" or "=" never leaks into Unknown / NotApplied ----

    [Theory]
    [InlineData(Engine.MySql, "Server=h;Port=3306;User ID=u;Password=\"ab;cd-secretpart\"", "ab;cd-secretpart")]
    [InlineData(Engine.MySql, "Server=h;Port=3306;User ID=u;Password='ab;cd=secretpart'", "ab;cd=secretpart")]
    [InlineData(Engine.MySql, "Server=h;Port=3306;User ID=u;Password=\"a\"\"b;c-secretpart\"", "a\"b;c-secretpart")]
    [InlineData(Engine.Mssql, "Server=h,1433;User ID=u;Password=\"ab;cd-secretpart\"", "ab;cd-secretpart")]
    [InlineData(Engine.Pg, "Host=h;Username=u;Password=\"ab;cd-secretpart\"", "ab;cd-secretpart")]
    public async Task Quoted_secret_with_separators_stays_whole(Engine engine, string cs, string secret)
    {
        foreach (var editor in new[] { Engine.Mssql, Engine.Pg, Engine.MySql })
        {
            var r = await Parse(cs, editor);
            Assert.Equal(engine, r.Profile.Engine);
            Assert.Equal(secret, r.Secret);
            Assert.DoesNotContain(r.Unknown.Concat(r.NotApplied).Concat(r.Notes), s => s.Contains("secretpart", StringComparison.Ordinal));
        }
        // And the MySQL driver alone (its own parse path).
        var my = new MySqlProvider().ParseConnectionString(cs.Replace("Host=", "Server=").Replace("Username=", "User ID="), Basis(Engine.MySql));
        Assert.DoesNotContain(my.Unknown.Concat(my.NotApplied), s => s.Contains("secretpart", StringComparison.Ordinal));
    }

    // ---- regression: a MySQL string that names no server keeps the editor's host and port (as PostgreSQL and SQL Server do) ----

    [Fact]
    public void MySql_string_without_server_or_port_keeps_the_basis_host_and_port()
    {
        var basis = Basis(Engine.MySql) with { Host = "my.example.test", Port = 3307 };
        var r = new MySqlProvider().ParseConnectionString("User ID=app;Password=pw;Database=shop", basis);
        Assert.Equal(("my.example.test", 3307), (r.Profile.Host, r.Profile.Port));
        Assert.Equal(("app", "shop", "pw"), (r.Profile.Username, r.Profile.DatabaseDefault, r.Secret));

        var portOnly = new MySqlProvider().ParseConnectionString("Port=3308;User ID=app", basis);
        Assert.Equal(("my.example.test", 3308), (portOnly.Profile.Host, portOnly.Profile.Port));
        var serverOnly = new MySqlProvider().ParseConnectionString("Server=other.example.test;User ID=app", basis);
        Assert.Equal(("other.example.test", (int?)null), (serverOnly.Profile.Host, serverOnly.Profile.Port));
    }

    [Fact]
    public void Postgres_and_sql_server_keep_the_basis_host_and_port_too()
    {
        var pg = new PostgresProvider().ParseConnectionString("Username=app;Password=pw", Basis(Engine.Pg) with { Host = "pg.example.test", Port = 6432 });
        Assert.Equal(("pg.example.test", 6432), (pg.Profile.Host, pg.Profile.Port));
        var ms = new SqlServerProvider().ParseConnectionString("User ID=app;Password=pw", Basis(Engine.Mssql) with { Host = "sql.example.test", Port = 14330 });
        Assert.Equal(("sql.example.test", 14330), (ms.Profile.Host, ms.Profile.Port));
    }

    // ---- regression: libpq only percent-decodes URI values ("+" is a plus) ----

    [Theory]
    [InlineData("postgres://app@h/db?password=a+b", "a+b")]
    [InlineData("postgres://app@h/db?password=a%2Bb", "a+b")]
    [InlineData("postgres://app@h/db?password=a%20b", "a b")]
    [InlineData("postgres://app:a+b@h/db", "a+b")]
    public async Task Postgres_uri_values_are_only_percent_decoded(string uri, string secret)
    {
        var r = await Parse(uri);
        Assert.Equal(Engine.Pg, r.Profile.Engine);
        Assert.Equal(secret, r.Secret);
    }

    [Fact]
    public async Task Postgres_uri_application_name_keeps_its_plus()
    {
        var r = await Parse("postgres://app@h/db?application_name=etl+nightly");
        Assert.Equal("etl+nightly", r.Profile.Options!["appName"]);
    }

    // ---- regressions: input validation at the RPC boundary (§13: untrusted input) ----

    [Fact]
    public async Task A_string_over_the_cap_is_refused_unread()
    {
        var cs = "Server=h;Password=zq-over-cap-secret;Application Name=" + new string('a', Querithm.Host.RpcServer.MaxConnectionStringChars);
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Parse(cs));
        Assert.Equal("invalid", ex.Code);
        Assert.Contains("longer than 8 KB", ex.Message);
        Assert.DoesNotContain("zq-over-cap-secret", ex.Message);
        // At the cap it is still read.
        var atCap = "Server=h,1433;Application Name=" + new string('a', Querithm.Host.RpcServer.MaxConnectionStringChars - "Server=h,1433;Application Name=".Length);
        Assert.Equal(Engine.Mssql, (await Parse(atCap)).Profile.Engine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_or_null_string_is_refused_as_invalid(string? cs)
    {
        var ex = await Assert.ThrowsAsync<QuerithmException>(() =>
            _host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = Basis(Engine.Mssql), ConnectionString = cs! }));
        Assert.Equal("invalid", ex.Code);
    }

    [Fact]
    public async Task Null_params_or_profile_is_refused_as_invalid()
    {
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() => _host.Rpc.ProfileParseConnectionString(null!))).Code);
        Assert.Equal("invalid", (await Assert.ThrowsAsync<QuerithmException>(() =>
            _host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = null!, ConnectionString = "Server=h" }))).Code);
    }

    [Theory]
    [InlineData("Server=h;Password='zq-unclosed-secret")]
    [InlineData("Server=h;Password=\"zq-unclosed-secret")]
    [InlineData("Server=h;zq-unclosed-secret")]
    public async Task Bad_syntax_is_explained_without_the_drivers_text_or_the_string(string cs)
    {
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => Parse(cs));
        Assert.Equal("invalid", ex.Code);
        Assert.StartsWith("This connection string could not be read: ", ex.Message);
        Assert.DoesNotContain("initialization string", ex.Message);
        Assert.DoesNotContain("zq-unclosed-secret", ex.Message);
    }

    /// <summary>A pasted string is a few hundred characters; a megabyte must be refused at the boundary, quickly.</summary>
    [Fact]
    public async Task Oversized_string_is_handled_within_a_bound()
    {
        var huge = "Server=h;Application Name=" + new string('a', 1_000_000);
        var sw = Stopwatch.StartNew();
        try { await Parse(huge); } catch (QuerithmException) { /* refused: fine */ }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
}

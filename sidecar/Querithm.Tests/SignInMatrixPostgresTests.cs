using Npgsql;
using Querithm.Contract;
using Querithm.Core.Logging;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Providers.Sqlite;
using Querithm.Providers.DuckDb;

namespace Querithm.Tests;

/// <summary>
/// PostgreSQL: every way to sign in (password / SCRAM, Kerberos, Entra access token) as the provider turns a profile and
/// its secret into Npgsql settings and a cached data source (L19: the password is never in the cache key), and
/// profile.parseConnectionString for PostgreSQL keyword strings and postgres:// URIs (the secret comes back on its own,
/// never inside the profile). No server is reached: data sources connect lazily.
/// </summary>
public class SignInMatrixPostgresTests
{
    static readonly PostgresProvider Pg = new();

    static Profile P(AuthKind auth, string? user = "svc", Dictionary<string, string>? options = null, string? db = null) => new()
    {
        Id = "signin-" + Guid.NewGuid().ToString("N"), Name = "pg", Engine = Engine.Pg, Host = "127.0.0.1", Port = 1, Auth = auth, Username = user,
        DatabaseDefault = db ?? "si_" + Guid.NewGuid().ToString("N"), Environment = EnvironmentKind.Dev, TimeoutS = 1, Options = options,
    };

    static NpgsqlConnectionStringBuilder Built(Profile p, string? secret) => new(Pg.BuildConnectionString(p, secret, null));

    // ---- Npgsql settings per AuthKind ----

    [Fact]
    public void The_engine_offers_password_kerberos_and_entra_token_with_password_first()
    {
        Assert.Equal([AuthKind.Password, AuthKind.Kerberos, AuthKind.EntraToken], Pg.Traits.AuthKinds);
        Assert.Equal(AuthKind.Password, Pg.Traits.DefaultAuth);
        Assert.Equal("postgres", Pg.Traits.DefaultDatabase);
    }

    [Theory]
    [InlineData(AuthKind.Password)]
    [InlineData(AuthKind.Sql)] // an older profile's password sign-in
    [InlineData(AuthKind.EntraToken)] // Azure: the access token travels as the password
    public void Password_like_sign_ins_send_the_user_and_the_secret_as_the_password(AuthKind auth)
    {
        var b = Built(P(auth, "app@tenant"), "s3cret-value");
        Assert.Equal("app@tenant", b.Username);
        Assert.Equal("s3cret-value", b.Password);
        Assert.False(b.Pooling); // Core pools (§6)
        Assert.DoesNotContain("Integrated Security", b.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Password_with_no_secret_sends_none_so_trust_and_peer_still_work()
    {
        var b = Built(P(AuthKind.Password), null);
        Assert.Null(b.Password);
        Assert.Null(Pg.ValidateProfile(P(AuthKind.Password)));
    }

    [Fact]
    public void Kerberos_never_sends_a_password_even_if_a_stale_secret_is_passed()
    {
        var b = Built(P(AuthKind.Kerberos, "alice"), "stale-pw");
        Assert.Null(b.Password);
        Assert.DoesNotContain("stale-pw", b.ConnectionString, StringComparison.Ordinal);
        Assert.Equal("alice", b.Username);
        // Npgsql negotiates GSSAPI when the server asks; the default service name stays the driver's.
        Assert.Equal(new NpgsqlConnectionStringBuilder().KerberosServiceName, b.KerberosServiceName);
    }

    [Fact]
    public void Kerberos_takes_its_service_name_from_the_profile_options_and_may_omit_the_user()
    {
        var b = Built(P(AuthKind.Kerberos, user: null, options: new() { ["kerberosServiceName"] = "pgsvc" }), null);
        Assert.Equal("pgsvc", b.KerberosServiceName);
        Assert.Null(b.Username);
        Assert.Null(b.Password);
    }

    [Fact]
    public void The_service_name_option_is_ignored_for_a_password_sign_in()
    {
        var b = Built(P(AuthKind.Password, options: new() { ["kerberosServiceName"] = "pgsvc" }), "pw1");
        Assert.Equal(new NpgsqlConnectionStringBuilder().KerberosServiceName, b.KerberosServiceName);
    }

    [Theory]
    [InlineData(AuthKind.Password)]
    [InlineData(AuthKind.Kerberos)]
    [InlineData(AuthKind.EntraToken)]
    public void Each_offered_sign_in_validates_and_none_is_refused(AuthKind auth)
    {
        Assert.Null(Pg.ValidateProfile(P(auth)));
        Assert.Contains("needs a sign-in", Pg.ValidateProfile(P(AuthKind.None)));
    }

    [Fact]
    public void An_empty_host_and_database_fall_back_to_localhost_and_postgres()
    {
        var b = new NpgsqlConnectionStringBuilder(Pg.BuildConnectionString(P(AuthKind.Password) with { Host = null, Port = null, DatabaseDefault = null }, "pw", null));
        Assert.Equal("localhost", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("postgres", b.Database);
    }

    // ---- data source cache (L19): the password provider carries the secret, the key never does ----

    [Theory]
    [InlineData(AuthKind.Password, "Pw-Value-9431")]
    [InlineData(AuthKind.Sql, "Pw-Value-9432")]
    [InlineData(AuthKind.EntraToken, "eyJ.fake-token.9433")]
    [InlineData(AuthKind.Kerberos, "Stale-Value-9434")]
    public void No_cache_key_or_source_connection_string_holds_the_secret(AuthKind auth, string secret)
    {
        var p = P(auth);
        var source = PostgresProvider.DataSourceFor(p.Id, Pg.BuildConnectionString(p, secret, null));
        var mine = PostgresProvider.CacheSnapshot().Where(k => k.StartsWith(p.Id + "\u001f", StringComparison.Ordinal)).ToList();
        Assert.Single(mine);
        Assert.DoesNotContain(secret, mine[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Password", mine[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, source.ConnectionString, StringComparison.Ordinal);
        // The password provider sends the secret for password-like sign-ins and nothing for Kerberos.
        Assert.Equal(auth == AuthKind.Kerberos ? null : secret, PostgresProvider.PasswordOf(source));
        Pg.ForgetConnections(p.Id);
    }

    [Fact]
    public void A_refreshed_entra_token_reuses_the_source_and_the_next_open_sends_the_newest()
    {
        var p = P(AuthKind.EntraToken, "app@tenant");
        var first = PostgresProvider.DataSourceFor(p.Id, Pg.BuildConnectionString(p, "tok-0", null));
        for (var i = 1; i <= 3; i++) Assert.Same(first, PostgresProvider.DataSourceFor(p.Id, Pg.BuildConnectionString(p, $"tok-{i}", null)));
        Assert.Equal("tok-3", PostgresProvider.PasswordOf(first));
        Assert.Single(PostgresProvider.CacheSnapshot(), k => k.StartsWith(p.Id + "\u001f", StringComparison.Ordinal));
        Pg.ForgetConnections(p.Id);
    }

    [Fact]
    public void Switching_a_profile_from_password_to_kerberos_stops_sending_the_old_password()
    {
        var p = P(AuthKind.Password, "alice");
        var a = PostgresProvider.DataSourceFor(p.Id, Pg.BuildConnectionString(p, "old-pw-77", null));
        Assert.Equal("old-pw-77", PostgresProvider.PasswordOf(a));
        var k = p with { Auth = AuthKind.Kerberos };
        var b = PostgresProvider.DataSourceFor(k.Id, Pg.BuildConnectionString(k, "old-pw-77", null));
        Assert.Null(PostgresProvider.PasswordOf(b));
        Pg.ForgetConnections(p.Id);
        Assert.DoesNotContain(PostgresProvider.CacheSnapshot(), x => x.StartsWith(p.Id + "\u001f", StringComparison.Ordinal));
    }

    // ---- profile.parseConnectionString ----

    static Profile Basis(Engine engine = Engine.Pg, AuthKind? auth = null) => new()
    {
        Id = "", Name = "Mine", Engine = engine, Host = "localhost", Auth = auth ?? (engine == Engine.Pg ? AuthKind.Password : AuthKind.Sql),
        Username = engine == Engine.Pg ? "postgres" : "sa", DatabaseDefault = engine == Engine.Pg ? "postgres" : null, Environment = EnvironmentKind.Dev, TimeoutS = 15,
    };

    static async Task<ParseConnectionStringResult> Parse(string cs, Profile? basis = null, bool allEngines = false)
    {
        await using var host = allEngines
            ? new TestHost(new SqlServerProvider(), new PostgresProvider(), new SqliteProvider(), new MySqlProvider(), new DuckDbProvider())
            : new TestHost();
        return await host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = basis ?? Basis(), ConnectionString = cs });
    }

    /// <summary>The secret never rides inside the returned profile: not in a field, not in an option.</summary>
    static void AssertNotInProfile(Profile p, string secret)
    {
        Assert.DoesNotContain(secret, p.ToString(), StringComparison.Ordinal);
        foreach (var (key, value) in p.Options ?? []) Assert.DoesNotContain(secret, key + "=" + value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_keyword_string_maps_every_sign_in_field_and_returns_the_password_apart()
    {
        var r = await Parse("Host=pg.example.test;Port=5433;Database=sales;Username=app;Password=Kw-S3cret;SSL Mode=VerifyFull;Root Certificate=/ca/root.pem");
        var p = r.Profile;
        Assert.Equal(Engine.Pg, p.Engine);
        Assert.Equal(("pg.example.test", 5433, "sales", "app"), (p.Host, p.Port, p.DatabaseDefault, p.Username));
        Assert.Equal(AuthKind.Password, p.Auth);
        Assert.Equal(TlsMode.VerifyFull, p.TlsMode);
        Assert.Equal("/ca/root.pem", p.CaPath);
        Assert.Equal("Kw-S3cret", r.Secret);
        AssertNotInProfile(p, "Kw-S3cret");
        Assert.Empty(r.Unknown);
        Assert.Empty(r.NotApplied);
    }

    [Fact]
    public async Task A_postgres_uri_maps_user_host_port_database_and_sslmode()
    {
        var r = await Parse("postgres://app:Uri-S3cret@pg.example.test:5433/sales?sslmode=verify-full");
        var p = r.Profile;
        Assert.Equal(Engine.Pg, p.Engine);
        Assert.Equal(("pg.example.test", 5433, "sales", "app"), (p.Host, p.Port, p.DatabaseDefault, p.Username));
        Assert.Equal(TlsMode.VerifyFull, p.TlsMode);
        Assert.Equal("Uri-S3cret", r.Secret);
        AssertNotInProfile(p, "Uri-S3cret");
        Assert.Empty(r.Notes);
    }

    [Theory]
    [InlineData("disable", TlsMode.Disable)]
    [InlineData("prefer", TlsMode.Prefer)]
    [InlineData("require", TlsMode.Default)]
    [InlineData("verify-ca", TlsMode.VerifyCa)]
    [InlineData("verify-full", TlsMode.VerifyFull)]
    public async Task Each_uri_sslmode_maps_to_the_profile_tls_mode(string mode, TlsMode expected) =>
        Assert.Equal(expected, (await Parse($"postgresql://app:pw1@h/db?sslmode={mode}")).Profile.TlsMode);

    [Theory]
    [InlineData("p%40ss", "p@ss")]
    [InlineData("a%3Ab", "a:b")]
    [InlineData("%2Fx%3F%23", "/x?#")]
    [InlineData("100%25", "100%")]
    [InlineData("sp%20ace", "sp ace")]
    [InlineData("plus+sign", "plus+sign")] // '+' in the user info is a literal plus
    [InlineData("%C3%A9t%C3%A9", "été")]
    public async Task Url_encoded_passwords_are_decoded(string encoded, string expected)
    {
        var r = await Parse($"postgres://app:{encoded}@pg.example.test/sales");
        Assert.Equal(expected, r.Secret);
        Assert.Equal("app", r.Profile.Username);
        AssertNotInProfile(r.Profile, expected);
    }

    [Fact]
    public async Task An_encoded_user_name_is_decoded_too()
    {
        var r = await Parse("postgres://app%40corp:pw1@pg.example.test/sales");
        Assert.Equal("app@corp", r.Profile.Username);
        Assert.Equal("pw1", r.Secret);
    }

    [Theory]
    [InlineData("postgres://app@pg.example.test/sales")]
    [InlineData("postgres://app:@pg.example.test/sales")]
    [InlineData("Host=pg.example.test;Username=app;Database=sales")]
    [InlineData("Host=pg.example.test;Username=app;Password=;Database=sales")]
    public async Task A_missing_or_empty_password_returns_no_secret_and_keeps_the_password_sign_in(string cs)
    {
        var r = await Parse(cs);
        Assert.Null(r.Secret);
        Assert.Equal(AuthKind.Password, r.Profile.Auth);
        Assert.Equal("app", r.Profile.Username);
        Assert.Equal("sales", r.Profile.DatabaseDefault);
    }

    [Fact]
    public async Task A_string_without_a_user_keeps_the_editor_user()
    {
        var r = await Parse("postgres://pg.example.test/sales");
        Assert.Equal("postgres", r.Profile.Username);
        Assert.Null(r.Secret);
    }

    [Fact]
    public async Task A_password_turns_a_kerberos_profile_into_password_sign_in()
    {
        var r = await Parse("postgres://app:pw1@h/db", Basis(auth: AuthKind.Kerberos));
        Assert.Equal(AuthKind.Password, r.Profile.Auth);
        Assert.Equal("pw1", r.Secret);
    }

    [Fact]
    public async Task A_password_keeps_an_entra_token_profile_as_entra_token()
    {
        var r = await Parse("Host=h.postgres.database.azure.com;Username=app@tenant;Password=eyJ.fake.tok", Basis(auth: AuthKind.EntraToken));
        Assert.Equal(AuthKind.EntraToken, r.Profile.Auth);
        Assert.Equal("eyJ.fake.tok", r.Secret);
        AssertNotInProfile(r.Profile, "eyJ.fake.tok");
    }

    [Theory]
    [InlineData("Host=h;Username=alice;Kerberos Service Name=pgsvc")]
    [InlineData("postgres://alice@h/db?krbsrvname=pgsvc")]
    public async Task A_kerberos_service_name_chooses_kerberos_and_lands_in_options(string cs)
    {
        var r = await Parse(cs);
        Assert.Equal(AuthKind.Kerberos, r.Profile.Auth);
        Assert.Equal("pgsvc", r.Profile.Options!["kerberosServiceName"]);
        Assert.Null(r.Secret);
    }

    [Fact]
    public async Task The_parsed_secret_is_registered_with_the_log_scrubber()
    {
        await Parse("postgres://app:Scrub-Me-6620@h/db");
        Assert.DoesNotContain("Scrub-Me-6620", LogScrubber.Scrub("failed with Scrub-Me-6620"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgres://app:Det-pw1@pg.example.test:5433/sales")]
    [InlineData("Host=pg.example.test;Port=5433;Username=app;Password=Det-pw1;Database=sales;SSL Mode=Require")]
    public async Task From_another_engine_with_every_provider_registered_a_postgres_string_picks_postgres(string cs)
    {
        var r = await Parse(cs, Basis(Engine.Mssql), allEngines: true);
        Assert.Equal(Engine.Pg, r.Profile.Engine);
        Assert.Equal(AuthKind.Password, r.Profile.Auth);
        Assert.Equal("Det-pw1", r.Secret);
        AssertNotInProfile(r.Profile, "Det-pw1");
    }

    [Fact]
    public async Task A_multi_host_uri_is_refused_without_echoing_the_password()
    {
        await using var host = new TestHost();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            host.Rpc.ProfileParseConnectionString(new ParseConnectionStringParams { Profile = Basis(), ConnectionString = "postgres://app:Multi-pw-5512@h1:5432,h2:5432/db" }));
        Assert.DoesNotContain("Multi-pw-5512", ex.Message, StringComparison.Ordinal);
    }
}

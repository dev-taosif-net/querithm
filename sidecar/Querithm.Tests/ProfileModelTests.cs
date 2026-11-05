using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Phase D0a: the profile model, its store and the rules around it — enum spellings, locations, unsupported profiles,
/// auth-aware readiness, provider validation and the feature capabilities every RPC enforces.
/// </summary>
public class ProfileModelTests
{
    static readonly JsonSerializerOptions Wire = Json.Create();

    // ---------------------------------------------------------------------------------------------- wire and store spellings

    [Theory]
    [InlineData(AuthKind.None, "none")]
    [InlineData(AuthKind.Sql, "sql")]
    [InlineData(AuthKind.EntraDeviceCode, "entraDeviceCode")]
    public void Auth_kinds_round_trip_on_the_wire_and_in_the_store(AuthKind kind, string text)
    {
        Assert.Equal($"\"{text}\"", JsonSerializer.Serialize(kind, Wire));
        Assert.Equal(kind, JsonSerializer.Deserialize<AuthKind>($"\"{text}\"", Wire));
        Assert.Equal(text, EnumText.Of(kind));
        Assert.True(EnumText.TryParse<AuthKind>(text, out var back));
        Assert.Equal(kind, back);
    }

    [Theory]
    [InlineData(LocationKind.Server, "server")]
    [InlineData(LocationKind.File, "file")]
    [InlineData(LocationKind.Memory, "memory")]
    public void Locations_round_trip_on_the_wire_and_in_the_store(LocationKind kind, string text)
    {
        Assert.Equal($"\"{text}\"", JsonSerializer.Serialize(kind, Wire));
        Assert.Equal(text, EnumText.Of(kind));
        Assert.True(EnumText.TryParse<LocationKind>(text, out var back));
        Assert.Equal(kind, back);
    }

    [Fact]
    public void Existing_engine_and_dialect_spellings_are_unchanged()
    {
        Assert.Equal("\"mssql\"", JsonSerializer.Serialize(Engine.Mssql, Wire));
        Assert.Equal("\"pg\"", JsonSerializer.Serialize(Engine.Pg, Wire));
        Assert.Equal(("mssql", "pg"), (EnumText.Of(Engine.Mssql), EnumText.Of(Engine.Pg)));
        Assert.Equal(("tsql", "pg"), (EnumText.Of(Dialect.Tsql), EnumText.Of(Dialect.Pg)));
        Assert.Equal(AuthKind.Sql, default(AuthKind)); // never None: the default stays the SQL login
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("mssql,pg")]
    [InlineData("oracle")]
    [InlineData("")]
    [InlineData(null)]
    public void Strict_enum_parsing_refuses_numbers_lists_and_unknown_names(string? text) =>
        Assert.False(EnumText.TryParse<Engine>(text, out _));

    // ---------------------------------------------------------------------------------------------- store round trips

    static Profile Strip(Profile p) => p with { CreatedAt = 0, UpdatedAt = 0 };

    public static TheoryData<string, Engine, string> HostEncodings => new()
    {
        { "mssql tcp", Engine.Mssql, "tcp:db.example.test,14330" },
        { "mssql named pipe", Engine.Mssql, @"np:\\db\pipe\sql\query" },
        { "mssql shared memory", Engine.Mssql, "lpc:." },
        { "mssql instance", Engine.Mssql, @"db\SQLEXPRESS" },
        { "mssql host,port", Engine.Mssql, "db,1500" },
        { "pg socket directory", Engine.Pg, "/var/run/postgresql" },
        { "pg abstract socket", Engine.Pg, "@pgsock" },
        { "pg failover list", Engine.Pg, "h1:5432,h2:5433" },
    };

    [Theory]
    [MemberData(nameof(HostEncodings))]
    public void Server_profiles_round_trip_unchanged(string label, Engine engine, string host)
    {
        _ = label;
        var store = new ProfileStore(LocalStore.InMemory());
        var input = new Profile
        {
            Id = "", Name = "p", Engine = engine, Host = host, Port = engine == Engine.Pg ? 5432 : null,
            Auth = engine == Engine.Pg ? AuthKind.Password : AuthKind.Sql, Username = "u", DatabaseDefault = "d", Environment = EnvironmentKind.Prod,
            Color = "#c00", GroupName = "g", TlsMode = engine == Engine.Pg ? TlsMode.VerifyFull : TlsMode.Strict, TrustCert = true, CaPath = "/etc/ca.pem",
            TimeoutS = 7, CommandTimeoutS = 9, Options = new() { ["appName"] = "x" },
            Ssh = new SshSettings { Host = "bastion", Port = 2222, Username = "jump", Auth = SshAuthKind.PrivateKey, KeyPath = "/k" },
            PasswordRetention = "duration", PasswordRetentionMinutes = 60,
        };
        var saved = store.Save(input);
        var read = store.Get(saved.Id)!;
        Assert.Equal(Strip(saved) with { Options = null, Ssh = null }, Strip(read) with { Options = null, Ssh = null });
        Assert.Equal(saved.Options, read.Options);
        Assert.Equal(saved.Ssh, read.Ssh);
        Assert.Null(read.Location); // a server, as before Phase D
        Assert.Null(read.Unsupported);
    }

    [Fact]
    public void File_and_memory_locations_round_trip()
    {
        var store = new ProfileStore(LocalStore.InMemory());
        var file = store.Save(new Profile { Id = "", Name = "f", Engine = Engine.Pg, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "g1", DisplayName = "orders.db" } });
        var mem = store.Save(new Profile { Id = "", Name = "m", Engine = Engine.Pg, Auth = AuthKind.None, Location = LocationKind.Memory });
        Assert.Equal((LocationKind.File, "g1", "orders.db", AuthKind.None), (store.Get(file.Id)!.Location, store.Get(file.Id)!.File?.GrantId, store.Get(file.Id)!.File?.DisplayName, store.Get(file.Id)!.Auth));
        Assert.Equal((LocationKind.Memory, (FileLocation?)null), (store.Get(mem.Id)!.Location, store.Get(mem.Id)!.File));
    }

    static void Raw(LocalStore store, string sql, params (string, object?)[] ps)
    {
        using var c = store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("engine", "oracle")]
    [InlineData("engine", "1")]
    [InlineData("auth", "passkey")]
    [InlineData("location", "cloud")]
    public void A_stored_value_this_build_does_not_know_makes_the_profile_unsupported(string column, string value)
    {
        var local = LocalStore.InMemory();
        var store = new ProfileStore(local);
        var saved = store.Save(new Profile { Id = "", Name = "p", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql });
        Raw(local, $"UPDATE profile SET {column} = $v WHERE id = $id", ("$v", value), ("$id", saved.Id));
        var read = store.Get(saved.Id)!;
        Assert.NotNull(read.Unsupported);
        Assert.Contains(value, read.Unsupported);
        Assert.Throws<ArgumentException>(() => store.Save(read));
    }

    [Fact]
    public void A_legacy_row_without_auth_is_still_a_sql_login()
    {
        var local = LocalStore.InMemory();
        var store = new ProfileStore(local);
        var saved = store.Save(new Profile { Id = "", Name = "p", Engine = Engine.Mssql, Host = "h" });
        Raw(local, "UPDATE profile SET auth = NULL WHERE id = $id", ("$id", saved.Id));
        var read = store.Get(saved.Id)!;
        Assert.Equal(AuthKind.Sql, read.Auth);
        Assert.Null(read.Unsupported);
    }

    [Fact]
    public async Task An_unsupported_profile_is_never_opened_tested_or_overwritten_but_can_be_deleted()
    {
        await using var host = new TestHost();
        var saved = await host.Rpc.ProfileSave(new Profile { Id = "", Name = "p", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql });
        Raw(host.Services.Store, "UPDATE profile SET engine = 'oracle' WHERE id = $id", ("$id", saved.Id));

        var listed = Assert.Single(await host.Rpc.ProfileList());
        Assert.NotNull(listed.Unsupported);
        var open = Assert.Throws<QuerithmException>(() => host.Services.Sessions.Open(new SessionOpenParams { ProfileId = saved.Id, Secret = "x" }));
        Assert.Equal("unsupportedProfile", open.Code);
        var test = await host.Rpc.ProfileTest(new ProfileTestParams { ProfileId = saved.Id, Secret = "x" });
        Assert.False(test.Ok);
        Assert.Contains("oracle", test.Error);
        var save = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileSave(saved with { Name = "renamed" }));
        Assert.Equal("unsupportedProfile", save.Code);
        Assert.Equal("oracle", Scalar(host.Services.Store, "SELECT engine FROM profile WHERE id = $id", saved.Id));

        await host.Rpc.ProfileDelete(new ProfileRef { ProfileId = saved.Id });
        Assert.Empty(await host.Rpc.ProfileList());
    }

    static object? Scalar(LocalStore store, string sql, string id)
    {
        using var c = store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar();
    }

    [Fact]
    public async Task A_profile_for_an_engine_without_a_provider_is_listed_as_unsupported_and_refused()
    {
        var providers = new ProviderRegistry([new SqlServerProvider()]);
        var dir = Directory.CreateTempSubdirectory("qhost-").FullName;
        await using var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), new CapturingNotifier(), NullLoggerFactory.Instance, providers, dir);
        var rpc = new RpcServer(services, () => { });
        var pg = services.Profiles.Save(new Profile { Id = "", Name = "pg", Engine = Engine.Pg, Host = "h", Auth = AuthKind.Password });

        var listed = Assert.Single(await rpc.ProfileList());
        Assert.Equal("The PostgreSQL engine is not available in this build of Querithm.", listed.Unsupported);
        Assert.Equal("unsupportedProfile", Assert.Throws<QuerithmException>(() => services.Sessions.Open(new SessionOpenParams { ProfileId = pg.Id })).Code);
        var save = await Assert.ThrowsAsync<QuerithmException>(() => rpc.ProfileSave(pg with { Id = "" }));
        Assert.Equal("invalidProfile", save.Code);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_snippet_of_an_unknown_dialect_is_not_offered_as_tsql()
    {
        var local = LocalStore.InMemory();
        var snippets = new SnippetStore(local);
        var mine = snippets.Save(new Snippet { Id = "", Title = "t", Prefix = "mine", Body = "SELECT 1", Dialect = Dialect.Pg });
        Raw(local, "INSERT INTO snippet (id, title, prefix, body, dialect, tags_json, created_at, updated_at) VALUES ('future', 't', 'fut', 'x', 'klingon', '[]', 1, 1)");
        var list = snippets.List();
        Assert.Contains(list, s => s.Id == mine.Id && s.Dialect == Dialect.Pg);
        Assert.DoesNotContain(list, s => s.Id == "future");
    }

    [Fact]
    public void A_v3_store_gains_the_location_columns_and_keeps_its_profiles()
    {
        var dir = Directory.CreateTempSubdirectory("qmig-").FullName;
        var path = Path.Combine(dir, "v3.db");
        _ = new LocalStore(path);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            // A v3 profile table: the same columns without location / file_grant_id.
            cmd.CommandText = """
                DROP TABLE profile;
                CREATE TABLE profile (id TEXT PRIMARY KEY, name TEXT NOT NULL, engine TEXT NOT NULL, host TEXT, port INTEGER, auth TEXT, username TEXT,
                  database_default TEXT, environment TEXT, color TEXT, group_name TEXT, tls_mode TEXT, trust_cert INTEGER, timeout_s INTEGER, options_json TEXT,
                  created_at INTEGER, updated_at INTEGER);
                INSERT INTO profile VALUES ('p1', 'Billing', 'mssql', 'tcp:db,1433', NULL, 'windows', NULL, 'Sales', 'prod', NULL, NULL, 'strict', 0, 15, NULL, 1, 2);
                PRAGMA user_version = 3;
                """;
            cmd.ExecuteNonQuery();
        }
        var store = new LocalStore(path);
        var p = new ProfileStore(store).Get("p1")!;
        Assert.Equal(("Billing", Engine.Mssql, "tcp:db,1433", AuthKind.Windows, EnvironmentKind.Prod, (LocationKind?)null, (string?)null),
            (p.Name, p.Engine, p.Host, p.Auth, p.Environment, p.Location, p.Unsupported));
        Assert.Equal((long)LocalStore.Version, Scalar(store, "PRAGMA user_version", ""));
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    // ---------------------------------------------------------------------------------------------- validation

    [Fact]
    public async Task Server_engines_refuse_file_and_memory_locations_and_the_no_credentials_sign_in()
    {
        await using var host = new TestHost();
        async Task<string> Refused(Profile p) => (await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileSave(p))).Code;
        Assert.Equal("invalidProfile", await Refused(new Profile { Id = "", Name = "a", Engine = Engine.Mssql, Auth = AuthKind.None, Host = "h" }));
        Assert.Equal("invalidProfile", await Refused(new Profile { Id = "", Name = "b", Engine = Engine.Pg, Auth = AuthKind.None, Host = "h" }));
        Assert.Equal("invalidProfile", await Refused(new Profile { Id = "", Name = "c", Engine = Engine.Mssql, Auth = AuthKind.Sql, Location = LocationKind.Memory }));
        Assert.Equal("invalidProfile", await Refused(new Profile { Id = "", Name = "d", Engine = Engine.Pg, Auth = AuthKind.Password, Location = LocationKind.File, File = new FileLocation { GrantId = "g" } }));
        Assert.Equal("invalidProfile", await Refused(new Profile { Id = "", Name = "e", Engine = Engine.Pg, Auth = AuthKind.Password, Host = "h", File = new FileLocation { GrantId = "g" } }));
        var test = await host.Rpc.ProfileTest(new ProfileTestParams { Profile = new Profile { Id = "", Name = "t", Engine = Engine.Mssql, Auth = AuthKind.None, Host = "h" } });
        Assert.False(test.Ok);
        // The legacy aliases keep working: a PostgreSQL profile with the SQL-login kind, an explicit Server location.
        await host.Rpc.ProfileSave(new Profile { Id = "", Name = "ok1", Engine = Engine.Pg, Auth = AuthKind.Sql, Host = "h" });
        await host.Rpc.ProfileSave(new Profile { Id = "", Name = "ok2", Engine = Engine.Mssql, Auth = AuthKind.Windows, Host = "h", Location = LocationKind.Server });
    }

    // L25: every server engine refuses a port outside 1–65535 and a host with control characters at the RPC boundary,
    // naming the field; file and in-memory engines have no address to check.
    public static TheoryData<Engine, AuthKind> ServerEngines => new() { { Engine.Mssql, AuthKind.Sql }, { Engine.Pg, AuthKind.Password }, { Engine.MySql, AuthKind.Password } };

    static TestHost ServerHost() => new(new SqlServerProvider(), new PostgresProvider(), new Querithm.Providers.MySql.MySqlProvider());

    static string? FieldOf(QuerithmException e) => (e.DataPayload as IDictionary<string, string>)?["field"];

    [Theory]
    [MemberData(nameof(ServerEngines))]
    public async Task Server_engines_refuse_a_port_outside_1_to_65535_and_name_the_field(Engine engine, AuthKind auth)
    {
        await using var host = ServerHost();
        Profile P(int? port, string? h = "db.local", SshSettings? ssh = null) => new() { Id = "", Name = "p", Engine = engine, Auth = auth, Host = h, Port = port, Ssh = ssh };
        foreach (var bad in new[] { 0, -1, 65536, 70000, int.MaxValue })
        {
            var e = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileSave(P(bad)));
            Assert.Equal(("invalidProfile", "port"), (e.Code, FieldOf(e)));
            Assert.Contains("1 to 65535", e.Message);
            var test = await host.Rpc.ProfileTest(new ProfileTestParams { Profile = P(bad) });
            Assert.False(test.Ok);
            Assert.Contains("1 to 65535", test.Error);
        }
        var badHost = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileSave(P(null, "db\nlocal")));
        Assert.Equal(("invalidProfile", "host"), (badHost.Code, FieldOf(badHost)));
        var longHost = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileSave(P(null, new string('h', ProfileRules.MaxHostLength + 1))));
        Assert.Equal("host", FieldOf(longHost));
        var sshPort = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileSave(P(null, ssh: new SshSettings { Host = "bastion", Port = 99999, Username = "u" })));
        Assert.Equal(("invalidProfile", "sshPort"), (sshPort.Code, FieldOf(sshPort)));
        // The edges, the engine's default (no port) and an empty host (the driver's default) are kept.
        foreach (var ok in new int?[] { 1, 65535, null }) await host.Rpc.ProfileSave(P(ok));
        await host.Rpc.ProfileSave(P(1433, ""));
        // session.open checks a stored profile with the same rules (a store edited outside the app).
        Assert.Equal("The port must be a whole number from 1 to 65535.", ProfileRules.Validate(P(0), host.Services.Sessions.Providers.TryGet(engine)));
    }

    [Fact]
    public async Task A_profile_without_a_name_is_refused_naming_the_field()
    {
        await using var host = new TestHost();
        var e = await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.ProfileSave(new Profile { Id = "", Name = " ", Engine = Engine.Pg, Auth = AuthKind.Password, Host = "h" }));
        Assert.Equal("name", FieldOf(e));
    }

    [Theory]
    [InlineData(Engine.Sqlite)]
    [InlineData(Engine.DuckDb)]
    public void File_engines_have_no_address_to_check(Engine engine)
    {
        IDbProvider provider = engine == Engine.Sqlite ? new Querithm.Providers.Sqlite.SqliteProvider() : new Querithm.Providers.DuckDb.DuckDbProvider();
        var memory = new Profile { Id = "", Name = "m", Engine = engine, Auth = AuthKind.None, Location = LocationKind.Memory };
        Assert.Null(ProfileRules.Validate(memory, provider));
        Assert.Equal(new ProfileProblem("Pick the database file.", "file"), ProfileRules.Check(memory with { Location = LocationKind.File }, provider));
    }

    [Fact]
    public void Generic_location_rules_hold_for_every_engine()
    {
        var provider = new FakeDbProvider();
        Profile P(LocationKind loc, FileLocation? file = null, SshSettings? ssh = null) =>
            new() { Id = "", Name = "x", Engine = Engine.Mssql, Auth = AuthKind.None, Location = loc, File = file, Ssh = ssh };
        var ssh = new SshSettings { Host = "b", Username = "u" };
        Assert.Equal("Pick the database file.", ProfileRules.Validate(P(LocationKind.File), provider));
        Assert.NotNull(ProfileRules.Validate(P(LocationKind.File, new FileLocation { GrantId = "g" }, ssh), provider));
        Assert.NotNull(ProfileRules.Validate(P(LocationKind.Memory, new FileLocation { GrantId = "g" }), provider));
        Assert.NotNull(ProfileRules.Validate(P(LocationKind.Memory, ssh: ssh), provider));
        Assert.Null(ProfileRules.Validate(P(LocationKind.File, new FileLocation { GrantId = "g" }), provider));
        Assert.Null(ProfileRules.Validate(P(LocationKind.Memory), provider));
        Assert.NotNull(ProfileRules.Validate(P(LocationKind.Memory), null));
        Assert.NotNull(ProfileRules.Validate(P(LocationKind.Memory) with { Auth = (AuthKind)99 }, provider));
    }

    [Fact]
    public void Every_auth_kind_has_a_secret_decision()
    {
        foreach (var kind in Enum.GetValues<AuthKind>()) _ = ProfileRules.NeedsSecret(kind);
        Assert.Equal([AuthKind.Sql, AuthKind.EntraToken, AuthKind.Password], Enum.GetValues<AuthKind>().Where(ProfileRules.NeedsSecret));
    }

    // ---------------------------------------------------------------------------------------------- readiness

    sealed class CountingMetadata : IMetadataProvider
    {
        public int Reads;
        public Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<IReadOnlyList<ObjectStamp>>([]);
        }
        public Task<MetadataBundle> GetDetailsAsync(System.Data.Common.DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => Task.FromResult(new MetadataBundle([], [], [], []));
        public Task<string?> GetDefinitionAsync(System.Data.Common.DbConnection connection, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(System.Data.Common.DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dependency[]> GetDependenciesAsync(System.Data.Common.DbConnection connection, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public string ToEditableScript(string definition, Capabilities caps) => definition;
        public Task<RowCount[]> GetRowCountsAsync(System.Data.Common.DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
    }

    sealed record Readiness(SessionManager Sessions, MetadataService Metadata, CountingMetadata Counter, ProfileStore Profiles);

    static Readiness Rig()
    {
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var counter = new CountingMetadata();
        var sessions = new SessionManager(profiles, new ProviderRegistry([new MetadataRefreshTests.FakeProvider(counter)]), new CapturingNotifier(), Log.Null);
        var metaStore = new MetadataStore(store);
        return new Readiness(sessions, new MetadataService(sessions, metaStore, new SchemaCache(metaStore), new CapturingNotifier(), Log.Null), counter, profiles);
    }

    static async Task<int> SnapshotReads(Readiness r, Profile p)
    {
        _ = r.Metadata.Snapshot(new SnapshotParams { ProfileId = p.Id, Database = "db" });
        await r.Metadata.WaitAsync(p.Id, "db").WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        return r.Counter.Reads;
    }

    [Fact]
    public async Task A_windows_auth_profile_refreshes_in_the_background_before_its_first_open()
    {
        var r = Rig();
        var p = r.Profiles.Save(new Profile { Id = "", Name = "w", Engine = Engine.Pg, Host = "h", Auth = AuthKind.Windows });
        Assert.True(r.Sessions.CanOpen(p));
        Assert.Equal(1, await SnapshotReads(r, p));
        await r.Sessions.DisposeAsync();
    }

    [Fact]
    public async Task A_password_profile_waits_for_its_secret_and_after_forget_waits_again()
    {
        var r = Rig();
        var p = r.Profiles.Save(new Profile { Id = "", Name = "s", Engine = Engine.Pg, Host = "h", Auth = AuthKind.Password });
        Assert.False(r.Sessions.CanOpen(p));
        Assert.Equal(0, await SnapshotReads(r, p));
        r.Sessions.RememberSecret(p.Id, "pw");
        Assert.True(r.Sessions.CanOpen(p));
        r.Sessions.ForgetProfile(p.Id);
        Assert.False(r.Sessions.CanOpen(p));
        await r.Sessions.DisposeAsync();
    }

    [Fact]
    public async Task A_browser_sign_in_never_runs_unattended_until_the_user_signed_in_once()
    {
        var r = Rig();
        var p = r.Profiles.Save(new Profile { Id = "", Name = "e", Engine = Engine.Pg, Host = "h", Auth = AuthKind.EntraInteractive });
        Assert.False(r.Sessions.CanOpen(p));
        Assert.Equal(0, await SnapshotReads(r, p));
        // The user opens a session and it connects: the driver holds the token now.
        var s = r.Sessions.Open(new SessionOpenParams { ProfileId = p.Id });
        await r.Sessions.StateAsync(s.SessionId, CancellationToken.None);
        Assert.True(r.Sessions.CanOpen(p));
        r.Sessions.ForgetProfile(p.Id);
        Assert.False(r.Sessions.CanOpen(p));
        await r.Sessions.DisposeAsync();
    }

    [Fact]
    public async Task Memory_and_ungranted_file_databases_are_never_opened_by_a_helper()
    {
        var r = Rig();
        var mem = r.Profiles.Save(new Profile { Id = "", Name = "m", Engine = Engine.Pg, Auth = AuthKind.None, Location = LocationKind.Memory });
        var file = r.Profiles.Save(new Profile { Id = "", Name = "f", Engine = Engine.Pg, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "g" } });
        Assert.False(r.Sessions.CanOpen(mem));
        Assert.False(r.Sessions.CanOpen(file));
        Assert.Equal(0, await SnapshotReads(r, mem));
        Assert.Equal(0, await SnapshotReads(r, file));
        await r.Sessions.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------------------- capabilities

    sealed class CapsRig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public SessionManager Sessions { get; }
        public ObjectService Objects { get; }
        public Profile Profile { get; }

        public CapsRig()
        {
            var store = LocalStore.InMemory();
            var profiles = new ProfileStore(store);
            Profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Windows });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), new CapturingNotifier(), Log.Null);
            var metaStore = new MetadataStore(store);
            Objects = new ObjectService(Sessions, new MetadataService(Sessions, metaStore, new SchemaCache(metaStore), new CapturingNotifier(), Log.Null));
        }

        public string Open() => Sessions.Open(new SessionOpenParams { ProfileId = Profile.Id, Database = "db" }).SessionId;

        public ValueTask DisposeAsync() => Sessions.DisposeAsync();
    }

    [Fact]
    public async Task Capabilities_report_every_feature_with_the_reason_and_the_product()
    {
        await using var rig = new CapsRig();
        rig.Provider.Refused[Feature.DataEdit] = "Grid editing is not available for this engine.";
        var caps = await rig.Objects.CapabilitiesAsync(new SessionRef { SessionId = rig.Open() }, CancellationToken.None);
        Assert.False(caps.DataEdit.Available);
        Assert.Equal("Grid editing is not available for this engine.", caps.DataEdit.Reason);
        Assert.True(caps.ChangeDatabase.Available);
        Assert.True(caps.Transactions.Available);
        // No dialect / finder / activity on the fake: those features say why.
        Assert.False(caps.SchemaCompare.Available);
        Assert.False(caps.References.Available);
        Assert.False(caps.Monitor.Available);
        Assert.Equal("Fake", caps.Product?.Name);
        Assert.Equal(NamespaceModel.Schemas, caps.Namespaces);
    }

    [Fact]
    public async Task Capabilities_are_read_again_after_the_credential_epoch_changes()
    {
        await using var rig = new CapsRig();
        var sid = rig.Open();
        var first = await rig.Objects.CapabilitiesAsync(new SessionRef { SessionId = sid }, CancellationToken.None);
        rig.Provider.Refused[Feature.Transactions] = "No transactions here.";
        Assert.Same(first, await rig.Objects.CapabilitiesAsync(new SessionRef { SessionId = sid }, CancellationToken.None));
        rig.Sessions.ForgetPools(rig.Profile.Id); // a profile edit, a new secret or a changed grant
        var again = await rig.Objects.CapabilitiesAsync(new SessionRef { SessionId = sid }, CancellationToken.None);
        Assert.False(again.Transactions.Available);
    }

    [Fact]
    public async Task Refused_features_are_enforced_in_the_sidecar_with_the_same_reason()
    {
        await using var rig = new CapsRig();
        rig.Provider.Refused[Feature.ChangeDatabase] = "One database per file.";
        rig.Provider.Refused[Feature.Transactions] = "No transactions here.";
        var sid = rig.Open();
        var change = await Assert.ThrowsAsync<QuerithmException>(() => rig.Sessions.ChangeDatabaseAsync(new ChangeDatabaseParams { SessionId = sid, Database = "other" }, CancellationToken.None));
        Assert.Equal(("unsupported", "One database per file."), (change.Code, change.Message));
        var tx = await Assert.ThrowsAsync<QuerithmException>(() => rig.Sessions.TxAsync(sid, "begin", CancellationToken.None));
        Assert.Equal(("unsupported", "No transactions here."), (tx.Code, tx.Message));
        Assert.Equal(TxState.None, rig.Sessions.Get(sid).TxState);
        Assert.Empty(rig.Provider.Opened); // refused before anything was dialed
    }

    [Fact]
    public async Task A_refused_data_edit_marks_results_read_only_with_the_reason()
    {
        await using var host = new TestHost();
        var provider = new FakeDbProvider();
        provider.Refused[Feature.DataEdit] = "Grid editing is not available for this engine.";
        var session = new Session("s", new Profile { Id = "p", Name = "p", Engine = Engine.Mssql }, provider, "db");
        var edit = host.Services.DataEditor.Decide(session, [], null, null);
        Assert.Equal((false, "Grid editing is not available for this engine."), (edit!.Editable, edit.Reason));
    }

    [Fact]
    public async Task The_session_reports_its_product_once_connected()
    {
        await using var rig = new CapsRig();
        var sid = rig.Open();
        Assert.Null(rig.Sessions.Get(sid).State().Product);
        var state = await rig.Sessions.StateAsync(sid, CancellationToken.None);
        Assert.Equal("Fake", state.Product?.Name);
    }
}

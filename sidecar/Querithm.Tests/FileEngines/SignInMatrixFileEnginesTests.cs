using Apache.Arrow;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Host;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// Sign-in matrix, SQLite and DuckDB: these engines take no sign-in, so "signing in" is the file grant the shell injects
/// (read-only, then read-write after "Allow writes…") or an in-memory database. Each case really opens a temp-dir
/// database through the RPC surface, for both engines.
/// </summary>
public sealed class SignInMatrixFileEnginesTests
{
    /// <summary>The two hosts have the same shape but no shared type: this is the part the matrix needs.</summary>
    sealed class Rig(Engine engine) : IAsyncDisposable
    {
        readonly SqliteHost? _sl = engine == Engine.Sqlite ? new SqliteHost() : null;
        readonly DuckDbHost? _dk = engine == Engine.DuckDb ? new DuckDbHost() : null;

        public Engine Engine => engine;
        public RpcServer Rpc => _sl?.Rpc ?? _dk!.Rpc;
        public Services Services => _sl?.Services ?? _dk!.Services;
        public string Dir => _sl?.Dir ?? _dk!.Dir;

        public string NewDatabase() => _sl != null
            ? _sl.NewDatabase("sales.db", "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1, 'a');")
            : _dk!.NewDatabase("sales.duckdb", "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR); INSERT INTO t VALUES (1, 'a');");

        public static FileGrant Grant(string path, FileAccessMode mode = FileAccessMode.ReadOnly, string id = "g1") => SqliteHost.Grant(path, mode, id);

        public Task<(Profile Profile, string SessionId)> OpenFileAsync(string path, FileAccessMode mode = FileAccessMode.ReadOnly) =>
            _sl != null ? _sl.OpenFileAsync(path, mode) : _dk!.OpenFileAsync(path, mode);

        public Task<(Profile Profile, string SessionId)> OpenMemoryAsync() => _sl != null ? _sl.OpenMemoryAsync() : _dk!.OpenMemoryAsync();

        public async Task<(QueryDoneEvent Done, List<RecordBatch> Batches)> RunAsync(string sid, string sql)
        {
            if (_sl != null) { var (d, b, _) = await _sl.RunAsync(sid, sql); return (d, b); }
            var (d2, b2, _) = await _dk!.RunAsync(sid, sql);
            return (d2, b2);
        }

        public Profile FileProfile(string grantId = "g1") => new()
        {
            Id = "", Name = "sales", Engine = engine, Auth = AuthKind.None, Location = LocationKind.File,
            File = new FileLocation { GrantId = grantId, DisplayName = "sales" }, Environment = EnvironmentKind.Dev,
        };

        public async ValueTask DisposeAsync()
        {
            if (_sl != null) await _sl.DisposeAsync();
            if (_dk != null) await _dk.DisposeAsync();
        }
    }

    static string? Text(RecordBatch b, string col, int row = 0) => b.Column(col) switch
    {
        StringArray s => s.GetString(row),
        Int32Array n => n.GetValue(row)?.ToString(),
        Int64Array n => n.GetValue(row)?.ToString(),
        var a => a.IsNull(row) ? null : a.ToString(),
    };

    static async Task<long> CountAsync(Rig r, string sid, string table = "t")
    {
        var (done, batches) = await r.RunAsync(sid, $"SELECT CAST(COUNT(*) AS INTEGER) AS n FROM {table}");
        Assert.Null(done.Error);
        return long.Parse(Text(batches.Single(), "n")!, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static TheoryData<Engine> Engines => new() { Engine.Sqlite, Engine.DuckDb };

    // ---------------------------------------------------------------------------------------------- read-only grant

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_read_only_grant_reads_and_refuses_a_write_leaving_the_file_unchanged(Engine engine)
    {
        await using var r = new Rig(engine);
        var path = r.NewDatabase();
        var before = await File.ReadAllBytesAsync(path);
        var (_, sid) = await r.OpenFileAsync(path);
        Assert.Equal(1, await CountAsync(r, sid));
        var (w, _) = await r.RunAsync(sid, "INSERT INTO t VALUES (2, 'b')");
        Assert.NotNull(w.Error);
        Assert.Contains("read-only", w.Error!.Message, StringComparison.OrdinalIgnoreCase);
        // DDL is a write too.
        var (ddl, _) = await r.RunAsync(sid, "CREATE TABLE u (x INTEGER)");
        Assert.Contains("read-only", ddl.Error?.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await CountAsync(r, sid)); // the session goes on after the refusal
        await r.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    // ---------------------------------------------------------------------------------------------- read-write grant

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_read_write_grant_writes_and_the_write_is_in_the_file(Engine engine)
    {
        await using var r = new Rig(engine);
        var path = r.NewDatabase();
        var (p, sid) = await r.OpenFileAsync(path, FileAccessMode.ReadWrite);
        Assert.Null((await r.RunAsync(sid, "INSERT INTO t VALUES (2, 'b')")).Done.Error);
        await r.Rpc.SessionClose(new SessionCloseParams { SessionId = sid });
        // A fresh read-only session over the same file sees it.
        var ro = await r.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, FileGrant = Rig.Grant(path) });
        Assert.Equal(2, await CountAsync(r, ro.SessionId));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Allow_writes_upgrades_the_grant_and_the_next_session_writes(Engine engine)
    {
        // "Read-only, then Allow writes…": the shell re-injects the same grant id with ReadWrite on the next open.
        await using var r = new Rig(engine);
        var path = r.NewDatabase();
        var (p, ro) = await r.OpenFileAsync(path);
        Assert.Contains("read-only", (await r.RunAsync(ro, "INSERT INTO t VALUES (2, 'b')")).Done.Error?.Message ?? "", StringComparison.OrdinalIgnoreCase);
        await r.Rpc.SessionClose(new SessionCloseParams { SessionId = ro });

        var rw = await r.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, FileGrant = Rig.Grant(path, FileAccessMode.ReadWrite) });
        Assert.Equal(FileAccessMode.ReadWrite, r.Services.Sessions.FileGrantFor(p.Id)!.Mode);
        Assert.Null((await r.RunAsync(rw.SessionId, "INSERT INTO t VALUES (2, 'b')")).Done.Error);
        Assert.Equal(2, await CountAsync(r, rw.SessionId));
        await r.Rpc.SessionClose(new SessionCloseParams { SessionId = rw.SessionId });

        // And back to read-only (makeReadOnly): the next session refuses writes again.
        var back = await r.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, FileGrant = Rig.Grant(path) });
        Assert.Contains("read-only", (await r.RunAsync(back.SessionId, "INSERT INTO t VALUES (3, 'c')")).Done.Error?.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await CountAsync(r, back.SessionId));
    }

    // ---------------------------------------------------------------------------------------------- in-memory

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Each_in_memory_session_gets_its_own_database(Engine engine)
    {
        await using var r = new Rig(engine);
        var (p, a) = await r.OpenMemoryAsync();
        Assert.Null((await r.RunAsync(a, "CREATE TABLE only_a (x INTEGER); INSERT INTO only_a VALUES (1);")).Done.Error);
        Assert.Equal(1, await CountAsync(r, a, "only_a")); // kept between statements of the same session

        var b = await r.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id });
        var (q, _) = await r.RunAsync(b.SessionId, "SELECT * FROM only_a");
        Assert.NotNull(q.Error); // the second tab's database is empty
        Assert.Null((await r.RunAsync(b.SessionId, "CREATE TABLE only_a (x INTEGER)")).Done.Error);
        Assert.Equal(0, await CountAsync(r, b.SessionId, "only_a"));
        Assert.Equal(1, await CountAsync(r, a, "only_a"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task An_in_memory_database_writes_nothing_to_disk_and_needs_no_grant(Engine engine)
    {
        await using var r = new Rig(engine);
        var before = Directory.GetFiles(r.Dir, "*", SearchOption.TopDirectoryOnly);
        var (p, sid) = await r.OpenMemoryAsync();
        Assert.Null(p.File);
        Assert.Null((await r.RunAsync(sid, "CREATE TABLE m (x INTEGER); INSERT INTO m VALUES (1);")).Done.Error);
        Assert.Equal(before, Directory.GetFiles(r.Dir, "*", SearchOption.TopDirectoryOnly));
        var test = await r.Rpc.ProfileTest(new ProfileTestParams { Profile = p with { Id = "" } });
        Assert.True(test.Ok, test.Error);
    }

    // ---------------------------------------------------------------------------------------------- refusals

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_file_profile_opened_without_its_grant_is_refused_with_a_clear_message(Engine engine)
    {
        await using var r = new Rig(engine);
        var p = await r.Rpc.ProfileSave(r.FileProfile());
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => r.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id }));
        Assert.Equal("fileGrant", ex.Code);
        Assert.Contains("Pick the file again", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_grant_for_another_grant_id_is_refused(Engine engine)
    {
        await using var r = new Rig(engine);
        var path = r.NewDatabase();
        var p = await r.Rpc.ProfileSave(r.FileProfile("mine"));
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => r.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, FileGrant = Rig.Grant(path, id: "someone-else") }));
        Assert.Equal("fileGrant", ex.Code);
        Assert.Contains("Re-pick file", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_revoked_grant_closes_the_session_and_the_next_open_is_refused(Engine engine)
    {
        await using var r = new Rig(engine);
        var path = r.NewDatabase();
        var (p, sid) = await r.OpenFileAsync(path, FileAccessMode.ReadWrite);
        Assert.Equal(1, await CountAsync(r, sid));
        var revoked = await r.Rpc.GrantRevoke(new GrantRevokeParams { ProfileId = p.Id });
        Assert.Equal(1, revoked.ClosedSessions);
        Assert.Null(r.Services.Sessions.FileGrantFor(p.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc.SessionStateGet(new SessionRef { SessionId = sid }));
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => r.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id }));
        Assert.Equal("fileGrant", ex.Code);
        Assert.Contains("Pick the file again", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_picked_file_that_was_deleted_is_reported_and_never_created(Engine engine)
    {
        await using var r = new Rig(engine);
        var path = r.NewDatabase();
        var (_, sid) = await r.OpenFileAsync(path, FileAccessMode.ReadWrite);
        File.Delete(path);
        var (done, _) = await r.RunAsync(sid, "SELECT 1");
        Assert.NotNull(done.Error);
        Assert.Contains("Re-pick file", done.Error!.Message);
        Assert.False(File.Exists(path));
    }

    // ---------------------------------------------------------------------------------------------- profile.test (the editor's Test)

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Test_connection_opens_the_granted_file_and_refuses_without_a_grant(Engine engine)
    {
        await using var r = new Rig(engine);
        var path = r.NewDatabase();
        var unsaved = r.FileProfile();
        var refused = await r.Rpc.ProfileTest(new ProfileTestParams { Profile = unsaved });
        Assert.False(refused.Ok);
        Assert.Equal("Pick the database file first (no access to it was granted).", refused.Error);
        var ok = await r.Rpc.ProfileTest(new ProfileTestParams { Profile = unsaved, FileGrant = Rig.Grant(path) });
        Assert.True(ok.Ok, ok.Error);
        Assert.False(string.IsNullOrWhiteSpace(ok.ServerVersion));
        var other = await r.Rpc.ProfileTest(new ProfileTestParams { Profile = unsaved, FileGrant = Rig.Grant(path, id: "other") });
        Assert.False(other.Ok);
    }

    // ---------------------------------------------------------------------------------------------- no sign-in fields

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_sign_in_on_a_file_engine_is_refused(Engine engine)
    {
        await using var r = new Rig(engine);
        var name = engine == Engine.Sqlite ? "SQLite" : "DuckDB";
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => r.Rpc.ProfileSave(r.FileProfile() with { Auth = AuthKind.Password, Username = "u" }));
        Assert.Contains($"A {name} database takes no sign-in.", ex.Message);
        var t = await r.Rpc.ProfileTest(new ProfileTestParams { Profile = r.FileProfile() with { Auth = AuthKind.Password } });
        Assert.False(t.Ok);
        Assert.Contains("takes no sign-in", t.Error);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_file_profile_without_a_picked_file_and_a_memory_profile_with_one_are_refused(Engine engine)
    {
        await using var r = new Rig(engine);
        var noFile = await Assert.ThrowsAsync<QuerithmException>(() => r.Rpc.ProfileSave(r.FileProfile() with { File = null }));
        Assert.Contains("Pick the database file.", noFile.Message);
        var memWithFile = await Assert.ThrowsAsync<QuerithmException>(() => r.Rpc.ProfileSave(r.FileProfile() with { Location = LocationKind.Memory }));
        Assert.Contains("An in-memory database has no file.", memWithFile.Message);
        var server = await Assert.ThrowsAsync<QuerithmException>(() => r.Rpc.ProfileSave(r.FileProfile() with { Location = LocationKind.Server, File = null, Host = "h" }));
        Assert.Contains("database file or an in-memory database", server.Message);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_editors_save_payload_for_a_file_profile_is_accepted_as_sent(Engine engine)
    {
        // What ProfileEditor sends for a new file profile: no host, port, user or TLS; environment chosen.
        await using var r = new Rig(engine);
        var saved = await r.Rpc.ProfileSave(r.FileProfile() with { Host = null, Port = null, Username = null, TlsMode = TlsMode.Default, Environment = EnvironmentKind.Test });
        Assert.Equal(LocationKind.File, saved.Location);
        Assert.Equal("g1", saved.File!.GrantId);
        Assert.Null(saved.Host);
        Assert.Null(saved.Username);
        Assert.Equal(AuthKind.None, saved.Auth);
        Assert.Equal(EnvironmentKind.Test, saved.Environment);
    }
}

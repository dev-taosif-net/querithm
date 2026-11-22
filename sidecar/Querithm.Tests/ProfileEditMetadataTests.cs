using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Security;

namespace Querithm.Tests;

/// <summary>
/// An edit that moves a profile's connection (another server, file, login or tunnel) leaves it showing what a new profile
/// would: the schema read through the old connection goes, and a refresh still reading the old one keeps nothing
/// (regression: the old server's objects stayed in the explorer and completion for up to 10 minutes, and a refresh then
/// diffed the new server against them). A rename keeps the schema.
/// </summary>
public class ProfileEditMetadataTests
{
    static readonly TimeSpan Wait = TestTime.Of(TimeSpan.FromSeconds(10));

    public static TheoryData<Engine> Engines => new() { Engine.Mssql, Engine.Pg, Engine.MySql, Engine.Oracle, Engine.Sqlite, Engine.DuckDb };

    static TestHost Host() => new(new SqlServerProvider(), new PostgresProvider(), new MySqlProvider(), TestOracle.Supported(), new SqliteProvider(), new DuckDbProvider());

    static bool IsFile(Engine e) => e is Engine.Sqlite or Engine.DuckDb;

    static Profile Make(Engine engine) => IsFile(engine)
        ? new() { Id = "", Name = "file", Engine = engine, Auth = AuthKind.None, Location = LocationKind.File, File = new FileLocation { GrantId = "g1", DisplayName = "a.db" } }
        : new() { Id = "", Name = "srv", Engine = engine, Host = "old.invalid", Auth = engine == Engine.Mssql ? AuthKind.Sql : AuthKind.Password, Username = "u", DatabaseDefault = "svc" };

    /// <summary>The edit that points the profile somewhere else: another server, or a re-picked file.</summary>
    static Profile Moved(Profile p) => IsFile(p.Engine)
        ? p with { File = new FileLocation { GrantId = "g2", DisplayName = "b.db" } }
        : p with { Host = "new.invalid" };

    static void Seed(TestHost h, string profileId) =>
        h.Services.Metadata.Store.ApplyRefresh(profileId, "db", true, [new(1, "app", "old_orders", ObjectKind.Table, 1, null)], [],
            new(Array.Empty<ColumnRow>(), [], [], []), "16");

    static Task<SchemaSnapshot> Snapshot(TestHost h, string profileId) => h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = profileId, Database = "db" });

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task An_edit_that_moves_the_connection_shows_what_a_new_profile_would(Engine engine)
    {
        await using var h = Host();
        var p = await h.Rpc.ProfileSave(Make(engine));
        Seed(h, p.Id);
        p = await h.Rpc.ProfileSave(p with { Name = "renamed", Color = "#00ff00", Environment = EnvironmentKind.Test });
        Assert.Equal("old_orders", Assert.Single((await Snapshot(h, p.Id)).Objects).Name); // a rename keeps the schema

        var moved = await h.Rpc.ProfileSave(Moved(p));
        var created = await h.Rpc.ProfileSave(moved with { Id = "", Name = "fresh" });
        foreach (var id in new[] { moved.Id, created.Id })
        {
            var snap = await Snapshot(h, id);
            Assert.Equal(SnapshotStatus.Empty, snap.Status);
            Assert.Empty(snap.Objects);
        }
        Assert.Empty(h.Services.Metadata.Store.KnownDatabases(moved.Id));
        // Open explorers read the snapshot again.
        Assert.Contains(h.Notifier.Of<MetaUpdatedEvent>(), e => e.ProfileId == moved.Id && e.Database == "db" && e.Status == SnapshotStatus.Empty && e.Full);
    }

    /// <summary>A catalog whose read waits until released: a refresh caught reading the old server.</summary>
    sealed class GatedCatalog : IMetadataProvider
    {
        public readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail;

        public async Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection c, CancellationToken ct)
        {
            Reading.TrySetResult();
            await Release.Task.WaitAsync(ct);
            if (Fail) throw new InvalidOperationException("the old server went away");
            return [new ObjectStamp(1, "dbo", "old_orders", ObjectKind.Table, 1, null)];
        }

        public Task<MetadataBundle> GetDetailsAsync(DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => Task.FromResult(new MetadataBundle([], [], [], []));
        public Task<string?> GetDefinitionAsync(DbConnection c, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dependency[]> GetDependenciesAsync(DbConnection c, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public string ToEditableScript(string definition, Capabilities caps) => definition;
        public Task<RowCount[]> GetRowCountsAsync(DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>
    /// A refresh reading the old server when the host is edited finishes on its own (the save does not wait for it) and
    /// keeps nothing: neither the old server's objects nor its failure, and no row left 'refreshing'.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refresh_still_reading_the_old_server_keeps_nothing(bool fails)
    {
        await using var h = new TestHostWithFake();
        var catalog = new GatedCatalog { Fail = fails };
        h.Provider.MetadataImpl = catalog;
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "srv", Engine = Engine.Mssql, Host = "old.invalid", Auth = AuthKind.Sql, Username = "u" });
        h.Services.Sessions.RememberSecret(p.Id, "pw-zz1");
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = "db" });
        await catalog.Reading.Task.WaitAsync(Wait);

        await h.Rpc.ProfileSave(p with { Host = "new.invalid" }).WaitAsync(Wait);
        catalog.Release.SetResult();
        await h.Services.Metadata.WaitAsync(p.Id, "db").WaitAsync(Wait);

        Assert.Null(h.Services.Metadata.Store.GetSnapshotRow(p.Id, "db"));
        var snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = "db" });
        Assert.Equal(SnapshotStatus.Empty, snap.Status);
        Assert.Empty(snap.Objects);
        // Its last word is the empty snapshot, so the explorer does not stay at 'refreshing' or show the old error.
        var last = h.Notifier.Of<MetaUpdatedEvent>().Last(e => e.ProfileId == p.Id);
        Assert.Equal(SnapshotStatus.Empty, last.Status);
    }
}

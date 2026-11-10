using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using static Querithm.Tests.MetadataRefreshTests;

namespace Querithm.Tests;

/// <summary>
/// D4 S05: what a provider could not read (<see cref="IMetadataProvider.Notes"/>) is kept with the snapshot and sent with
/// meta.updated, and a refresh with nothing missing clears it; an object's note (invalid, materialized view) is stored
/// with the object. Other engines report no notes and keep working as before.
/// </summary>
public class MetadataNotesTests
{
    /// <summary>Two objects; the first refresh also says triggers could not be read, the next one says nothing.</summary>
    sealed class NotingMetadata : IMetadataProvider
    {
        public int Reads;
        public IReadOnlyList<string> Notes(DbConnection connection) =>
            Volatile.Read(ref Reads) == 1 ? ["Triggers are not listed: this login cannot read ALL_TRIGGERS (ORA-00942)."] : [];

        public Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<IReadOnlyList<ObjectStamp>>(
            [
                new ObjectStamp(1, "APP", "T", ObjectKind.Table, 1, null),
                new ObjectStamp(2, "APP", "F", ObjectKind.Func, 1, "NUMBER", Note: "invalid"),
            ]);
        }

        public Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => Task.FromResult(new MetadataBundle([], [], [], []));
        public Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public string ToEditableScript(string definition, Capabilities caps) => definition;
        public Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_refresh_keeps_what_the_provider_could_not_read_with_the_snapshot_and_the_next_one_clears_it()
    {
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" });
        var noting = new NotingMetadata();
        var notifier = new CapturingNotifier();
        var sessions = new SessionManager(profiles, new ProviderRegistry([new FakeProvider(noting)]), notifier, Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var metaStore = new MetadataStore(store);
        var metadata = new MetadataService(sessions, metaStore, new SchemaCache(metaStore), notifier, Log.Null);
        var p = new RefreshParams { ProfileId = profile.Id, Database = "db", Full = true };

        metadata.Refresh(p);
        await metadata.WaitAsync(profile.Id, "db").WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        var snap = metadata.Snapshot(new SnapshotParams { ProfileId = profile.Id, Database = "db" });
        // Fresh, not failed: the objects it could read are there, and the note says what is missing.
        Assert.Null(snap.Error);
        Assert.Equal(["Triggers are not listed: this login cannot read ALL_TRIGGERS (ORA-00942)."], snap.Notes!);
        Assert.Equal("invalid", snap.Objects.Single(o => o.Id == 2).Note);
        Assert.Null(snap.Objects.Single(o => o.Id == 1).Note);
        var fresh = notifier.All.Select(e => e.Payload).OfType<MetaUpdatedEvent>().Last(e => e.Status == SnapshotStatus.Fresh);
        Assert.Equal(snap.Notes, fresh.Notes);

        metadata.Refresh(p with { Full = false });
        await metadata.WaitAsync(profile.Id, "db").WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        Assert.Null(metadata.Snapshot(new SnapshotParams { ProfileId = profile.Id, Database = "db" }).Notes);
        Assert.Null(notifier.All.Select(e => e.Payload).OfType<MetaUpdatedEvent>().Last(e => e.Status == SnapshotStatus.Fresh).Notes);
    }

    [Fact]
    public void Notes_and_object_notes_survive_the_store_and_a_provider_without_notes_reports_none()
    {
        var store = new MetadataStore(LocalStore.InMemory());
        store.ApplyRefresh("p", "db", true, [new ObjectStamp(1, "APP", "MV", ObjectKind.View, 1, null, Note: "materialized view")], [],
            new MetadataBundle([], [], [], []), "23", ["A", "B"]);
        var snap = store.Load("p", "db");
        Assert.Equal(["A", "B"], snap.Notes!);
        Assert.Equal("materialized view", Assert.Single(snap.Objects).Note);
        // A refresh without notes clears them; an object no longer noted loses its note.
        store.ApplyRefresh("p", "db", false, [new ObjectStamp(1, "APP", "MV", ObjectKind.View, 2, null)], [], new MetadataBundle([], [], [], []), "23");
        snap = store.Load("p", "db");
        Assert.Null(snap.Notes!);
        Assert.Null(Assert.Single(snap.Objects).Note);
        Assert.Empty(((IMetadataProvider)new SlowMetadata()).Notes(new FakeConnection()));
    }

    [Fact]
    public void A_damaged_notes_cell_loses_its_notes_not_the_snapshot()
    {
        var local = LocalStore.InMemory();
        var store = new MetadataStore(local);
        store.ApplyRefresh("p", "db", true, [new ObjectStamp(1, "APP", "T", ObjectKind.Table, 1, null)], [], new MetadataBundle([], [], [], []), "23", ["A"]);
        using (var c = local.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE schema_snapshot SET notes = '[not json'";
            cmd.ExecuteNonQuery();
        }
        var snap = store.Load("p", "db");
        Assert.Null(snap.Notes!);
        Assert.Single(snap.Objects);
    }
}

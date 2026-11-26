using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using static Querithm.Tests.MetadataRefreshTests;

namespace Querithm.Tests.Security;

/// <summary>
/// Completion audit 2026-11-25, S2 and S3: the id-array metadata reads are bounded at the RPC boundary (§13 rule 7), and
/// a refresh's error is scrubbed before it is stored and again when a snapshot sends it (§13: no secret in store.db).
/// </summary>
public sealed class MetadataRpcBoundsTests
{
    static (MetadataService Metadata, ObjectService Objects, MetadataStore Store, string ProfileId) Services(Exception? openFails = null)
    {
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" });
        var slow = new SlowMetadata();
        slow.Gate.SetResult();
        var sessions = new SessionManager(profiles, new ProviderRegistry([new FakeProvider(slow, openFails)]), new CapturingNotifier(), Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var metaStore = new MetadataStore(store);
        var metadata = new MetadataService(sessions, metaStore, new SchemaCache(metaStore), new CapturingNotifier(), Log.Null);
        return (metadata, new ObjectService(sessions, metadata), metaStore, profile.Id);
    }

    static long[] Ids(int n) => Enumerable.Range(1, n).Select(i => (long)i).ToArray();

    [Fact]
    public void Meta_objects_takes_a_bounded_id_array_and_bounded_names()
    {
        var (metadata, _, _, profileId) = Services();
        Assert.Empty(metadata.Objects(new ObjectsParams { ProfileId = profileId, Database = "db", Ids = Ids(300) }));
        Assert.Empty(metadata.Objects(new ObjectsParams { ProfileId = profileId, Database = "db", Ids = Ids(MetadataService.MaxObjectIds) }));
        Assert.Throws<QuerithmException>(() => metadata.Objects(new ObjectsParams { ProfileId = profileId, Database = "db", Ids = Ids(MetadataService.MaxObjectIds + 1) }));
        Assert.Throws<QuerithmException>(() => metadata.Objects(new ObjectsParams { ProfileId = profileId, Database = "db", Ids = null! }));
        Assert.Throws<QuerithmException>(() => metadata.Objects(new ObjectsParams { ProfileId = profileId, Database = new string('d', 2000), Ids = [1] }));
        Assert.Throws<QuerithmException>(() => metadata.Objects(new ObjectsParams { ProfileId = new string('p', 2000), Database = "db", Ids = [1] }));
    }

    [Fact]
    public void Meta_foreign_keys_takes_a_bounded_id_array_and_bounded_names()
    {
        // §13 rule 13 names meta.foreignKeys too; until M12 nothing tested it.
        var (metadata, _, _, profileId) = Services();
        Assert.Empty(metadata.ForeignKeys(new ForeignKeysParams { ProfileId = profileId, Database = "db", Ids = Ids(MetadataService.MaxObjectIds) }));
        var big = Assert.Throws<QuerithmException>(() => metadata.ForeignKeys(new ForeignKeysParams { ProfileId = profileId, Database = "db", Ids = Ids(MetadataService.MaxObjectIds + 1) }));
        Assert.Equal("invalid", big.Code);
        Assert.Throws<QuerithmException>(() => metadata.ForeignKeys(new ForeignKeysParams { ProfileId = profileId, Database = "db", Ids = null! }));
        Assert.Throws<QuerithmException>(() => metadata.ForeignKeys(new ForeignKeysParams { ProfileId = profileId, Database = new string('d', 2000), Ids = [1] }));
        Assert.Throws<QuerithmException>(() => metadata.ForeignKeys(new ForeignKeysParams { ProfileId = new string('p', 2000), Database = "db", Ids = [1] }));
        // Checked before the session is looked up: a long session id is refused as invalid, never looked for.
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() =>
            metadata.ForeignKeys(new ForeignKeysParams { ProfileId = profileId, Database = "db", Ids = [1], SessionId = new string('s', 2000) })).Code);
    }

    [Fact]
    public async Task Meta_row_counts_refuses_an_unbounded_id_array_before_it_reaches_a_session()
    {
        var (_, objects, _, _) = Services();
        // Refused at the boundary, before the session is looked up (no session exists here: a NotFound would mean the check came later).
        var big = await Assert.ThrowsAsync<QuerithmException>(() => objects.RowCountsAsync(new RowCountsParams { SessionId = "s", ObjectIds = Ids(MetadataService.MaxObjectIds + 1) }, CancellationToken.None));
        Assert.Equal("invalid", big.Code);
        var name = await Assert.ThrowsAsync<QuerithmException>(() => objects.RowCountsAsync(new RowCountsParams { SessionId = "s", Database = new string('d', 2000), ObjectIds = [1] }, CancellationToken.None));
        Assert.Equal("invalid", name.Code);
        var none = await Assert.ThrowsAsync<QuerithmException>(() => objects.RowCountsAsync(new RowCountsParams { SessionId = "s", ObjectIds = null! }, CancellationToken.None));
        Assert.Equal("invalid", none.Code);
    }

    [Fact]
    public async Task A_refresh_error_is_scrubbed_before_it_is_stored()
    {
        var (metadata, _, store, profileId) = Services(new InvalidOperationException("no route to host; Password=hunter2"));
        metadata.Refresh(new RefreshParams { ProfileId = profileId, Database = "db" });
        await metadata.WaitAsync(profileId, "db").WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        var row = store.GetSnapshotRow(profileId, "db")!;
        Assert.Equal(SnapshotStatus.Error, row.Status);
        Assert.StartsWith("no route to host", row.Error);
        Assert.DoesNotContain("hunter2", row.Error);
    }

    [Fact]
    public void A_stored_error_an_older_build_kept_raw_is_scrubbed_when_a_snapshot_sends_it()
    {
        var (metadata, _, store, profileId) = Services();
        store.SetStatus(profileId, "old", SnapshotStatus.Error, "login failed; Password=hunter2");
        var snap = metadata.Snapshot(new SnapshotParams { ProfileId = profileId, Database = "old" });
        Assert.NotNull(snap.Error);
        Assert.StartsWith("login failed", snap.Error);
        Assert.DoesNotContain("hunter2", snap.Error);
    }
}

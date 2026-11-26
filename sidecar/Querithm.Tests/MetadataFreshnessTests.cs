using Querithm.Contract;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using static Querithm.Tests.MetadataRefreshTests;

namespace Querithm.Tests;

/// <summary>
/// Completion audit 2026-11-25, metadata freshness after DDL run through the app (§8 step 5): DDL inside a pooled
/// session's open transaction is read once the transaction ends, not before (H11); a run that moved to another database
/// (USE) reads the one it started in too (M22).
/// </summary>
public class MetadataFreshnessTests
{
    static (MetadataService Metadata, MetadataStore Store, SlowMetadata Slow, Session Session, CapturingNotifier Events) Rig() => Rig(LocalStore.InMemory());

    static (MetadataService Metadata, MetadataStore Store, SlowMetadata Slow, Session Session, CapturingNotifier Events) Rig(LocalStore store)
    {
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" });
        var slow = new SlowMetadata();
        slow.Gate.SetResult();
        var events = new CapturingNotifier();
        var sessions = new SessionManager(profiles, new ProviderRegistry([new FakeProvider(slow)]), events, Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var metaStore = new MetadataStore(store);
        var metadata = new MetadataService(sessions, metaStore, new SchemaCache(metaStore), events, Log.Null);
        var session = sessions.Get(sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Secret = "x" }).SessionId);
        session.Database = "db";
        metaStore.SetStatus(profile.Id, "db", SnapshotStatus.Fresh, refreshedAt: Core.Time.NowMs());
        return (metadata, metaStore, slow, session, events);
    }

    static Task Settled(MetadataService m, Session s, string db) => m.WaitAsync(s.Profile.Id, db).WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));

    [Fact]
    public async Task Ddl_in_a_pooled_transaction_is_read_when_the_transaction_ends_and_not_before()
    {
        var (metadata, store, slow, session, events) = Rig();
        session.TxState = TxState.Open;
        metadata.OnDdlExecuted(session, "db", session.Database);
        await Settled(metadata, session, "db");
        // No read on a pool connection: it cannot see the uncommitted CREATE TABLE and would call the snapshot fresh.
        Assert.Equal(0, Volatile.Read(ref slow.Reads));
        Assert.Equal(SnapshotStatus.Stale, store.GetSnapshotRow(session.Profile.Id, "db")!.Status);
        Assert.Contains(events.Of<MetaUpdatedEvent>(), e => e.Database == "db" && e.Status == SnapshotStatus.Stale);

        // A refresh that runs meanwhile (meta.snapshot found it old, the user pressed Refresh) keeps it stale.
        metadata.Refresh(new RefreshParams { ProfileId = session.Profile.Id, Database = "db" });
        await Settled(metadata, session, "db");
        Assert.Equal(1, Volatile.Read(ref slow.Reads));
        Assert.Equal(SnapshotStatus.Stale, store.GetSnapshotRow(session.Profile.Id, "db")!.Status);

        // COMMIT (or ROLLBACK): read now.
        session.TxState = TxState.None;
        metadata.OnTransactionEnded(session);
        await Settled(metadata, session, "db");
        Assert.Equal(2, Volatile.Read(ref slow.Reads));
        Assert.Equal(SnapshotStatus.Fresh, store.GetSnapshotRow(session.Profile.Id, "db")!.Status);
        Assert.False(session.RefreshDeferred);
    }

    [Fact]
    public async Task Ddl_after_a_use_reads_the_database_the_run_started_in_and_the_one_it_ended_in()
    {
        var (metadata, store, slow, session, _) = Rig();
        session.Database = "other"; // USE other; at the end of the run
        metadata.OnDdlExecuted(session, "db", session.Database);
        await Settled(metadata, session, "db");
        await Settled(metadata, session, "other");
        Assert.Equal(2, Volatile.Read(ref slow.Reads));
        Assert.Equal(SnapshotStatus.Fresh, store.GetSnapshotRow(session.Profile.Id, "db")!.Status);
        Assert.Equal(SnapshotStatus.Fresh, store.GetSnapshotRow(session.Profile.Id, "other")!.Status);
    }

    [Fact]
    public async Task A_deferred_read_covers_both_databases_once_the_transaction_ends()
    {
        var (metadata, _, slow, session, _) = Rig();
        session.Database = "other";
        session.TxState = TxState.Open;
        metadata.OnDdlExecuted(session, "db", session.Database);
        await Settled(metadata, session, "db");
        Assert.Equal(0, Volatile.Read(ref slow.Reads));
        session.TxState = TxState.None;
        metadata.OnTransactionEnded(session);
        await Settled(metadata, session, "db");
        await Settled(metadata, session, "other");
        Assert.Equal(2, Volatile.Read(ref slow.Reads));
    }

    [Fact]
    public async Task Ddl_with_no_open_transaction_is_read_at_once()
    {
        var (metadata, store, slow, session, _) = Rig();
        metadata.OnDdlExecuted(session, "db", session.Database);
        await Settled(metadata, session, "db");
        Assert.Equal(1, Volatile.Read(ref slow.Reads));
        Assert.Equal(SnapshotStatus.Fresh, store.GetSnapshotRow(session.Profile.Id, "db")!.Status);
    }

    [Fact]
    public async Task A_refresh_of_the_database_a_transaction_left_after_its_ddl_stays_stale_until_it_ends()
    {
        // G#6 / A#6: BEGIN TRAN; CREATE TABLE … (in db); USE other; — the deferral covers db, but the "stays stale" check
        // compared only the session's current database (other), so a read of db meanwhile called it Fresh without the table.
        var (metadata, store, slow, session, _) = Rig();
        session.TxState = TxState.Open;
        session.Database = "other";
        metadata.OnDdlExecuted(session, "db", "other");
        await Settled(metadata, session, "db");
        Assert.Equal(0, Volatile.Read(ref slow.Reads));

        metadata.Refresh(new RefreshParams { ProfileId = session.Profile.Id, Database = "db" }); // another tab, the focus check, Refresh
        await Settled(metadata, session, "db");
        Assert.Equal(1, Volatile.Read(ref slow.Reads));
        Assert.Equal(SnapshotStatus.Stale, store.GetSnapshotRow(session.Profile.Id, "db")!.Status);

        session.TxState = TxState.None;
        metadata.OnTransactionEnded(session);
        await Settled(metadata, session, "db");
        Assert.Equal(SnapshotStatus.Fresh, store.GetSnapshotRow(session.Profile.Id, "db")!.Status);
    }

    [Fact]
    public void A_store_that_cannot_be_read_does_not_fault_the_run_that_ran_ddl()
    {
        // A#7: raised from the execution's finally — an exception here ended the run's task before the edit recheck started.
        var local = LocalStore.InMemory();
        var (metadata, _, _, session, _) = Rig(local);
        session.TxState = TxState.Open;
        using (var c = local.Open()) c.Cmd("DROP TABLE schema_snapshot").ExecuteNonQuery(); // the store fails every read now
        Assert.Null(Record.Exception(() => metadata.OnDdlExecuted(session, "db", "db")));
    }

    [Fact]
    public async Task The_ddl_database_is_the_one_the_run_ended_in_not_where_the_session_is_by_the_time_it_is_read()
    {
        // G#5: the handler ran after the run let go of the tab; a queued run's USE had moved the session by then, and the
        // database the DDL ran in (reached by a USE inside the run) was never read again.
        var (metadata, store, slow, session, _) = Rig();
        store.SetStatus(session.Profile.Id, "b", SnapshotStatus.Fresh, refreshedAt: Core.Time.NowMs());
        session.Database = "c"; // where the next run's USE took it
        metadata.OnDdlExecuted(session, "a", "b");
        await Settled(metadata, session, "a");
        await Settled(metadata, session, "b");
        Assert.Equal(2, Volatile.Read(ref slow.Reads));
        Assert.Null(store.GetSnapshotRow(session.Profile.Id, "c")); // never touched
    }
}

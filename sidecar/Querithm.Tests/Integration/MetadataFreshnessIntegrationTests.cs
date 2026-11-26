using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// Completion audit 2026-11-25 against the PostgreSQL fixture (QUERITHM_IT=1): an incremental refresh sees index, key and
/// default changes (H13), and DDL inside an open transaction reaches the snapshot when it commits (H11).
/// </summary>
[Collection("pg")]
public class MetadataFreshnessIntegrationTests
{
    const string Db = "querithm_fresh";

    static async Task Ok(TestHost h, string sid, string sql)
    {
        var (done, _, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)) + done.Error?.Message + "\n" + sql);
    }

    static int Mark(TestHost h) => h.Notifier.Of<MetaUpdatedEvent>().Count(e => e.Status == SnapshotStatus.Fresh);

    /// <summary>Refreshes and returns what every refresh finished since <paramref name="mark"/> changed (DDL run through the app starts one too).</summary>
    static async Task<long[]> RefreshAsync(TestHost h, Profile p, bool full, int mark)
    {
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = Db, Full = full });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, Db);
        return h.Notifier.Of<MetaUpdatedEvent>().Where(e => e.Status == SnapshotStatus.Fresh).Skip(mark).SelectMany(e => e.ChangedIds ?? []).ToArray();
    }

    static SnapObject Table(TestHost h, Profile p, string name) =>
        h.Services.Metadata.Cache.Get(p.Id, Db)!.Objects.Single(o => o.Name == name && o.Kind == ObjectKind.Table);

    [IntegrationFact]
    public async Task An_incremental_refresh_sees_a_new_index_a_second_foreign_key_a_default_and_a_dropped_index()
    {
        await using var h = new TestHost();
        await h.EnsureDatabaseAsync(Engine.Pg, Db);
        var (p, sid) = await h.OpenPgAsync(Db);
        await Ok(h, sid, """
            DROP TABLE IF EXISTS q_h13_child; DROP TABLE IF EXISTS q_h13_parent;
            CREATE TABLE q_h13_parent (id int PRIMARY KEY, code int NOT NULL UNIQUE);
            CREATE TABLE q_h13_child (id int, parent_id int REFERENCES q_h13_parent (id), code int);
            """);
        await RefreshAsync(h, p, full: true, Mark(h));
        var child = Table(h, p, "q_h13_child");

        async Task Changes(string ddl)
        {
            var mark = Mark(h);
            await Ok(h, sid, ddl);
            var changed = await RefreshAsync(h, p, full: false, mark);
            Assert.True(changed.Contains(child.Id), $"`{ddl}` did not mark q_h13_child changed");
        }

        await Changes("CREATE UNIQUE INDEX q_h13_child_code ON q_h13_child (code)");
        Assert.Contains(h.Services.Metadata.Store.Details(p.Id, Db, child.Id).Indexes, i => i.Name == "q_h13_child_code");
        await Changes("ALTER TABLE q_h13_child ADD CONSTRAINT q_h13_fk2 FOREIGN KEY (code) REFERENCES q_h13_parent (code)");
        Assert.Equal(2, h.Services.Metadata.Cache.Get(p.Id, Db)!.Fks.Count(f => f.Parent == child.Id));
        await Changes("ALTER TABLE q_h13_child ALTER COLUMN code SET DEFAULT 7");
        await Changes("DROP INDEX q_h13_child_code");
        Assert.DoesNotContain(h.Services.Metadata.Store.Details(p.Id, Db, child.Id).Indexes, i => i.Name == "q_h13_child_code");
        await Changes("ALTER TABLE q_h13_child DROP CONSTRAINT q_h13_fk2");
        Assert.Equal(1, h.Services.Metadata.Cache.Get(p.Id, Db)!.Fks.Count(f => f.Parent == child.Id));
        // G#12: a rename changes only the index's own pg_class row (neither the table's row nor its pg_index row).
        await Changes("CREATE INDEX q_h13_child_ix ON q_h13_child (parent_id)");
        await Changes("ALTER INDEX q_h13_child_ix RENAME TO q_h13_child_ix2");
        Assert.Contains(h.Services.Metadata.Store.Details(p.Id, Db, child.Id).Indexes, i => i.Name == "q_h13_child_ix2");
        Assert.DoesNotContain(h.Services.Metadata.Store.Details(p.Id, Db, child.Id).Indexes, i => i.Name == "q_h13_child_ix");
        // And a constraint rename, for completeness (its pg_constraint row changes).
        await Changes("ALTER TABLE q_h13_child RENAME CONSTRAINT q_h13_child_parent_id_fkey TO q_h13_child_parent_fk");
        Assert.Contains(h.Services.Metadata.Cache.Get(p.Id, Db)!.Fks, f => f.Parent == child.Id && f.Name.StartsWith("q_h13_child_parent_fk", StringComparison.Ordinal)); // "name@table": PostgreSQL names an FK per table
    }

    [IntegrationFact]
    public async Task A_table_created_inside_a_transaction_reaches_the_snapshot_when_it_commits()
    {
        await using var h = new TestHost();
        await h.EnsureDatabaseAsync(Engine.Pg, Db);
        var (p, sid) = await h.OpenPgAsync(Db);
        await Ok(h, sid, "DROP TABLE IF EXISTS q_h11_new");
        await RefreshAsync(h, p, full: true, Mark(h));

        // The toolbar's Begin and Commit (tx.begin / tx.commit): no statement of the COMMIT goes through the classifier.
        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Ok(h, sid, "CREATE TABLE q_h11_new (id int)");
        await h.Services.Metadata.WaitAsync(p.Id, Db);
        Assert.DoesNotContain(h.Services.Metadata.Cache.Get(p.Id, Db)!.Objects, o => o.Name == "q_h11_new");

        await h.Rpc.TxCommit(new SessionRef { SessionId = sid });
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!h.Services.Metadata.Cache.Get(p.Id, Db)!.Objects.Any(o => o.Name == "q_h11_new"))
        {
            Assert.True(DateTime.UtcNow < until, "the committed table never reached the snapshot (no refresh after the COMMIT)");
            await Task.Delay(50);
            await h.Services.Metadata.WaitAsync(p.Id, Db);
        }
        Assert.Equal(SnapshotStatus.Fresh, h.Services.Metadata.Store.GetSnapshotRow(p.Id, Db)!.Status);
    }
}

using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// Code audit follow-up 2026-11-26 against the SQL Server fixture (QUERITHM_IT=1): two schemas' foreign keys of the same
/// name stay apart (they were grouped by name, so one key got both keys' columns), and DDL inside an open transaction
/// reaches the snapshot when it commits — the PostgreSQL case is in <see cref="MetadataFreshnessIntegrationTests"/>.
/// </summary>
[Collection("mssql")]
public class SqlServerMetadataAuditIntegrationTests
{
    const string Db = "querithm_fresh";

    static async Task Ok(TestHost h, string sid, string sql)
    {
        var (done, _, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)) + done.Error?.Message + "\n" + sql);
    }

    static async Task RefreshAsync(TestHost h, Profile p)
    {
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = Db, Full = true });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, Db);
    }

    [IntegrationFact]
    public async Task Foreign_keys_of_the_same_name_in_two_schemas_keep_their_own_columns()
    {
        await using var h = new TestHost();
        await h.EnsureDatabaseAsync(Engine.Mssql, Db);
        var (p, sid) = await h.OpenMssqlAsync(Db);
        foreach (var s in new[] { "q_fk_a", "q_fk_b" })
        {
            await Ok(h, sid, $"IF SCHEMA_ID('{s}') IS NULL EXEC('CREATE SCHEMA {s}')");
            await Ok(h, sid, $"""
                IF OBJECT_ID('{s}.child') IS NOT NULL DROP TABLE {s}.child;
                IF OBJECT_ID('{s}.parent') IS NOT NULL DROP TABLE {s}.parent;
                CREATE TABLE {s}.parent (id int PRIMARY KEY, code int NOT NULL UNIQUE);
                CREATE TABLE {s}.child (id int, {(s == "q_fk_a" ? "parent_id" : "parent_code")} int,
                    CONSTRAINT fk_same FOREIGN KEY ({(s == "q_fk_a" ? "parent_id" : "parent_code")}) REFERENCES {s}.parent ({(s == "q_fk_a" ? "id" : "code")}));
                """);
        }
        await RefreshAsync(h, p);
        var snap = h.Services.Metadata.Cache.Get(p.Id, Db)!;
        long Child(string schema) => snap.Objects.Single(o => o.Schema == schema && o.Name == "child").Id;
        var a = snap.Fks.Single(f => f.Parent == Child("q_fk_a"));
        var b = snap.Fks.Single(f => f.Parent == Child("q_fk_b"));
        Assert.Equal(("parent_id", "id"), (a.Columns.Single().Parent, a.Columns.Single().Referenced));
        Assert.Equal(("parent_code", "code"), (b.Columns.Single().Parent, b.Columns.Single().Referenced));
    }

    [IntegrationFact]
    public async Task A_table_created_inside_a_transaction_reaches_the_snapshot_when_it_commits()
    {
        await using var h = new TestHost();
        await h.EnsureDatabaseAsync(Engine.Mssql, Db);
        var (p, sid) = await h.OpenMssqlAsync(Db);
        await Ok(h, sid, "IF OBJECT_ID('dbo.q_h11_new') IS NOT NULL DROP TABLE dbo.q_h11_new");
        await RefreshAsync(h, p);

        await h.Rpc.TxBegin(new SessionRef { SessionId = sid });
        await Ok(h, sid, "CREATE TABLE dbo.q_h11_new (id int)");
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

using MySqlConnector;
using Querithm.Contract;
using static Querithm.Tests.Integration.MySqlIntegrationTests;

namespace Querithm.Tests.Integration;

/// <summary>
/// Phase D2 find usages and rename on MySQL 8.4, MySQL 9.7 and MariaDB 11.8 (QUERITHM_IT=1): definitions from SHOW CREATE,
/// the rename script (RENAME TABLE / RENAME COLUMN plus rewritten modules) runs, and the modules work afterwards.
/// </summary>
public sealed class MySqlRefactorTests
{
    static string Db2(string product) => $"q_rf_{product.Replace('-', '_')}";

    static async Task ExecAsync(string product, string sql, string? database)
    {
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={Port(product)};User ID=root;Password={TestHost.MySqlPassword};SslMode=Required;Pooling=False{(database != null ? $";Database={database}" : "")}");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<(MySqlIntegrationTests.Host H, Profile P, string Sid)> SetupAsync(string product)
    {
        await EnsureDatabaseAsync(product);
        var db = Db2(product);
        await ExecAsync(product, $"DROP DATABASE IF EXISTS {db}; CREATE DATABASE {db};", null);
        foreach (var s in new[]
        {
            "CREATE TABLE item (id INT PRIMARY KEY, price DECIMAL(10,2), qty INT)",
            "CREATE VIEW v_cheap AS SELECT id, price FROM item WHERE price < 10",
            "CREATE PROCEDURE p_total(OUT t DECIMAL(12,2)) BEGIN SELECT SUM(price * qty) INTO t FROM item; END",
            "CREATE TRIGGER trg_item BEFORE INSERT ON item FOR EACH ROW SET NEW.qty = IFNULL(NEW.qty, 1)",
            "INSERT INTO item VALUES (1, 5.00, 2), (2, 20.00, 1)",
        }) await ExecAsync(product, s, db);
        var h = new MySqlIntegrationTests.Host();
        var p = await h.Rpc.ProfileSave(ProfileFor(product, database: db));
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = TestHost.MySqlPassword })).SessionId;
        await h.Rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = db, SessionId = sid, Full = true });
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = db, SessionId = sid });
            if (snap.Objects.Any(o => o.Name == "item" && o.Columns is { Length: > 0 }) && snap.Objects.Any(o => o.Name == "trg_item")) break;
            await Task.Delay(50);
        }
        return (h, p, sid);
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Usages_of_a_column_are_found_in_views_routines_triggers_and_tabs(string product)
    {
        var (h, _, sid) = await SetupAsync(product);
        await using var _ = h;
        Assert.True((await h.Rpc.SessionCapabilities(new SessionRef { SessionId = sid })).References.Available);
        var r = await h.Rpc.RefactorFindUsages(new FindUsagesParams
        {
            SessionId = sid, Target = new RefactorTarget { Kind = RefactorKind.Column, Schema = Db2(product), Name = "item", Column = "qty" },
            Tabs = [new OpenTabText { TabId = "t1", Title = "Query 1", Text = "SELECT qty FROM item; SELECT 'qty' AS x" }],
        });
        var where = r.Hits.Select(x => x.Object?.Name ?? x.TabId).ToHashSet();
        Assert.Contains("p_total", where);
        Assert.Contains("trg_item", where);
        Assert.Contains("t1", where);
        Assert.DoesNotContain("v_cheap", where); // the view reads id and price only
        Assert.Single(r.Hits, x => x.TabId == "t1"); // not the string 'qty'
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Dependencies_are_the_real_references_in_definitions(string product)
    {
        var (h, _, sid) = await SetupAsync(product);
        await using var _ = h;
        await ExecAsync(product, "CREATE TABLE note (id INT PRIMARY KEY, item INT)", Db2(product)); // a column named item is not a use of item
        async Task<string[]> Deps(string name, ObjectKind kind, DependencyDirection d) =>
            (await h.Rpc.MetaDependencies(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = Db2(product), Name = name, Kind = kind } }))
            .Where(x => x.Direction == d).Select(x => x.Object.Name).Order().ToArray();
        Assert.Equal(["item"], await Deps("v_cheap", ObjectKind.View, DependencyDirection.Uses));
        Assert.Equal(["item"], await Deps("p_total", ObjectKind.Proc, DependencyDirection.Uses));
        Assert.Equal(["item"], await Deps("trg_item", ObjectKind.Trigger, DependencyDirection.Uses));
        Assert.Equal(["p_total", "trg_item", "v_cheap"], await Deps("item", ObjectKind.Table, DependencyDirection.UsedBy));
        Assert.Empty(await Deps("note", ObjectKind.Table, DependencyDirection.UsedBy));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Row_counts_and_modify_apply_of_a_view_a_procedure_and_a_trigger(string product)
    {
        var (h, p, sid) = await SetupAsync(product);
        await using var _ = h;
        await ExecAsync(product, "ANALYZE TABLE item", Db2(product));
        var snap = await h.Rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = Db2(product), SessionId = sid });
        var item = snap.Objects.Single(o => o.Name == "item");
        var count = Assert.Single(await h.Rpc.MetaRowCounts(new RowCountsParams { SessionId = sid, ObjectIds = [item.Id] }));
        Assert.True(count.Estimated); // InnoDB TABLE_ROWS is an estimate
        Assert.Equal(2L, count.Rows);
        // Modify → Apply re-runs each editable definition unchanged, then with an edit.
        foreach (var (name, kind) in new[] { ("v_cheap", ObjectKind.View), ("p_total", ObjectKind.Proc), ("trg_item", ObjectKind.Trigger) })
        {
            var def = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = Db2(product), Name = name, Kind = kind } });
            var (done, _, id) = await Run(h, sid, def.Sql);
            Assert.True(done.Error == null, $"{name}: {done.Error?.Message}\n{def.Sql}");
        }
        var proc = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = Db2(product), Name = "p_total", Kind = ObjectKind.Proc } });
        Assert.StartsWith("DROP PROCEDURE IF EXISTS", proc.Sql);
        Assert.Null((await Run(h, sid, proc.Sql.Replace("SUM(price * qty)", "SUM(qty)"))).Done.Error);
        var (check, batches, _) = await Run(h, sid, "CALL p_total(@t); SELECT @t AS t");
        Assert.Null(check.Error);
        Assert.Equal("3.00", Text(batches[^1], "t"));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Renaming_a_table_rewrites_the_modules_and_the_script_runs(string product)
    {
        var (h, _, sid) = await SetupAsync(product);
        await using var _ = h;
        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = sid, Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = Db2(product), Name = "item" }, NewName = "article",
        });
        Assert.Contains("RENAME TABLE", preview.Sql);
        Assert.Contains(preview.Objects, o => o.Object.Name == "v_cheap");
        Assert.Contains(preview.Objects, o => o.Object.Name == "p_total");
        var (done, _, id) = await Run(h, sid, preview.Sql);
        Assert.True(done.Error == null, $"{done.Error?.Message}\n{string.Join("\n", h.Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == id).Select(m => m.Message))}\n{preview.Sql}");
        // The rewritten modules work against the renamed table.
        var (check, batches, _) = await Run(h, sid, "SELECT COUNT(*) AS n FROM v_cheap; CALL p_total(@t); SELECT @t AS t; INSERT INTO article (id, price) VALUES (3, 1.00); SELECT qty FROM article WHERE id = 3");
        Assert.Null(check.Error);
        Assert.Equal("1", Text(batches[0], "n"));
    }

    [IntegrationTheory]
    [MemberData(nameof(Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task Tier_two_completion_and_signature_help_read_the_live_snapshot(string product)
    {
        var (h, p, sid) = await SetupAsync(product);
        await using var _ = h;
        var db = Db2(product);
        const string text = "SELECT i. FROM `item` AS i";
        var items = await h.Rpc.LangComplete(new CompleteParams { DocId = "d", Text = text, Offset = 9, Dialect = Dialect.MySql, ProfileId = p.Id, Database = db, SessionId = sid });
        Assert.Equal(["id", "price", "qty"], items.Select(i => i.Label).Order(StringComparer.Ordinal));
        const string call = "CALL p_total(";
        var help = await h.Rpc.LangSignature(new SignatureParams { DocId = "d", Text = call, Offset = call.Length, Dialect = Dialect.MySql, ProfileId = p.Id, Database = db, SessionId = sid });
        Assert.StartsWith($"{db}.p_total(t decimal", Assert.Single(help.Signatures).Label, StringComparison.OrdinalIgnoreCase);
    }
}

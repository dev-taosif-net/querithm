using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// Triggers in the metadata snapshot (Phase A1) on both engines: listed with their table, picked up by an
/// incremental refresh after ALTER / CREATE OR REPLACE, scripted (Modify, ENABLE / DISABLE, DROP and CREATE), and part
/// of find usages and a column rename.
/// </summary>
[Collection("triggers")]
public class TriggerIntegrationTests
{
    const string Db = "querithm_trg";

    public static TheoryData<string> Engines => TestEngines.Names;

    static async Task Ok(TestHost h, string sid, string sql)
    {
        var (done, _, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)) + done.Error?.Message + "\n" + sql);
    }

    static async Task<string?> Text(TestHost h, string sid, string sql)
    {
        var (done, b, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        return ((StringArray)b[0].Column(0)).GetString(0);
    }

    const string MssqlSetup = """
        DROP TABLE IF EXISTS dbo.q_trg_audit; DROP TABLE IF EXISTS dbo.q_trg;
        GO
        CREATE TABLE dbo.q_trg (id int PRIMARY KEY, price int NULL, qty int NULL);
        CREATE TABLE dbo.q_trg_audit (id int, old_price int);
        GO
        CREATE TRIGGER dbo.q_trg_price ON dbo.q_trg AFTER UPDATE AS
        BEGIN
          SET NOCOUNT ON;
          IF UPDATE(price) INSERT INTO dbo.q_trg_audit (id, old_price) SELECT d.id, d.price FROM deleted d JOIN inserted i ON i.id = d.id;
        END
        GO
        INSERT dbo.q_trg (id, price, qty) VALUES (1, 10, 1);
        """;

    const string PgSetup = """
        DROP TABLE IF EXISTS q_trg_audit; DROP TABLE IF EXISTS q_trg CASCADE; DROP FUNCTION IF EXISTS q_trg_log() CASCADE;
        CREATE TABLE q_trg (id int PRIMARY KEY, price int, qty int);
        CREATE TABLE q_trg_audit (id int, old_price int);
        CREATE FUNCTION q_trg_log() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO q_trg_audit (id) VALUES (OLD.id); RETURN NEW; END $$;
        CREATE TRIGGER q_trg_price AFTER UPDATE OF price ON q_trg FOR EACH ROW WHEN (OLD.price IS DISTINCT FROM NEW.price) EXECUTE FUNCTION q_trg_log();
        INSERT INTO q_trg VALUES (1, 10, 1);
        """;

    static async Task<(TestHost Host, Profile Profile, string Session)> SetupAsync(string engine)
    {
        var h = new TestHost();
        Profile p;
        string sid;
        if (engine == "mssql")
        {
            await h.EnsureDatabaseAsync(Engine.Mssql, Db);
            (p, sid) = await h.OpenMssqlAsync(Db);
            await Ok(h, sid, MssqlSetup);
        }
        else
        {
            await h.EnsureDatabaseAsync(Engine.Pg, Db);
            (p, sid) = await h.OpenPgAsync(Db);
            await Ok(h, sid, PgSetup);
        }
        await RefreshAsync(h, p, full: true);
        return (h, p, sid);
    }

    static int Mark(TestHost h) => h.Notifier.Of<MetaUpdatedEvent>().Count(e => e.Status == SnapshotStatus.Fresh);

    /// <summary>
    /// Refreshes and returns every finished refresh since <paramref name="mark"/>: DDL run through the app already starts
    /// one by itself, which may be the one that saw the change.
    /// </summary>
    static async Task<(long[] Changed, long[] Removed)> RefreshAsync(TestHost h, Profile p, bool full, int? mark = null)
    {
        var before = mark ?? Mark(h);
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = Db, Full = full });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, Db);
        var events = h.Notifier.Of<MetaUpdatedEvent>().Where(e => e.Status == SnapshotStatus.Fresh).Skip(before).ToList();
        return (events.SelectMany(e => e.ChangedIds ?? []).ToArray(), events.SelectMany(e => e.RemovedIds ?? []).ToArray());
    }

    static (SnapObject Table, SnapObject Trigger) Find(TestHost h, Profile p)
    {
        var snap = h.Services.Metadata.Cache.Get(p.Id, Db)!;
        return (snap.Objects.Single(o => o.Name == "q_trg" && o.Kind == ObjectKind.Table), snap.Objects.Single(o => o.Name == "q_trg_price" && o.Kind == ObjectKind.Trigger));
    }

    static ObjectRef Ref(Profile p, SnapObject o) => new() { ProfileId = p.Id, Database = Db, Schema = o.Schema, Name = o.Name, Kind = o.Kind, ObjectId = o.Id };

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task The_snapshot_lists_a_trigger_under_its_table_and_an_incremental_refresh_sees_it_change(string engine)
    {
        var (h, p, sid) = await SetupAsync(engine);
        await using var _h = h;
        var (table, trigger) = Find(h, p);
        Assert.Equal(table.Id, trigger.ParentObjectId);
        Assert.Null(table.ParentObjectId);
        // The trigger function stays a function on PostgreSQL.
        if (engine == "pg") Assert.Contains(h.Services.Metadata.Cache.Get(p.Id, Db)!.Objects, o => o.Name == "q_trg_log" && o.Kind == ObjectKind.Func);

        await Task.Delay(20); // modify_date has millisecond resolution
        var mark = Mark(h);
        await Ok(h, sid, engine == "mssql"
            ? "ALTER TRIGGER dbo.q_trg_price ON dbo.q_trg AFTER UPDATE, INSERT AS BEGIN SET NOCOUNT ON; END"
            : "CREATE OR REPLACE TRIGGER q_trg_price AFTER UPDATE OF price, qty ON q_trg FOR EACH ROW EXECUTE FUNCTION q_trg_log()");
        var updated = await RefreshAsync(h, p, full: false, mark);
        Assert.Contains(trigger.Id, updated.Changed);
        // SQL Server also bumps the parent table's modify_date when a trigger on it is altered; PostgreSQL does not.
        if (engine == "pg") Assert.DoesNotContain(table.Id, updated.Changed);
        var after = Find(h, p).Trigger;
        Assert.Equal((trigger.Id, table.Id), (after.Id, after.ParentObjectId!.Value));

        // Dropping the table drops its trigger: both leave the snapshot.
        mark = Mark(h);
        await Ok(h, sid, engine == "mssql" ? "DROP TABLE dbo.q_trg" : "DROP TABLE q_trg");
        var dropped = await RefreshAsync(h, p, full: false, mark);
        Assert.Contains(trigger.Id, dropped.Removed);
        Assert.DoesNotContain(h.Services.Metadata.Cache.Get(p.Id, Db)!.Objects, o => o.Kind == ObjectKind.Trigger && o.Name == "q_trg_price");
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task A_trigger_is_modified_scripted_and_enabled_or_disabled_by_generated_text(string engine)
    {
        var (h, p, sid) = await SetupAsync(engine);
        await using var _h = h;
        var (_, trigger) = Find(h, p);
        var r = Ref(p, trigger);

        var editable = await h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = r });
        Assert.StartsWith(engine == "mssql" ? "CREATE OR ALTER TRIGGER" : "CREATE OR REPLACE TRIGGER", editable.Sql.TrimStart());
        await Ok(h, sid, editable.Sql); // re-applying the definition works as is

        async Task<string> Script(ScriptKind k) => (await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = r, Kind = k })).Sql;
        var disable = await Script(ScriptKind.Disable);
        Assert.Contains(engine == "mssql" ? "DISABLE TRIGGER [dbo].[q_trg_price] ON [dbo].[q_trg]" : "ALTER TABLE public.q_trg DISABLE TRIGGER q_trg_price", disable);
        await Ok(h, sid, disable);
        var state = engine == "mssql"
            ? "SELECT CASE is_disabled WHEN 1 THEN N'off' ELSE N'on' END FROM sys.triggers WHERE name = 'q_trg_price'"
            : "SELECT CASE tgenabled WHEN 'D' THEN 'off' ELSE 'on' END FROM pg_trigger WHERE tgname = 'q_trg_price'";
        Assert.Equal("off", await Text(h, sid, state));
        await Ok(h, sid, await Script(ScriptKind.Enable));
        Assert.Equal("on", await Text(h, sid, state));

        Assert.Contains("TRIGGER", await Script(ScriptKind.Create));
        Assert.Contains(engine == "mssql" ? "DROP TRIGGER IF EXISTS [dbo].[q_trg_price]" : "DROP TRIGGER IF EXISTS q_trg_price ON public.q_trg", await Script(ScriptKind.Drop));

        // Enable/Disable are trigger-only; a trigger cannot be executed.
        var table = Ref(p, Find(h, p).Table);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = table, Kind = ScriptKind.Enable }));
        await Assert.ThrowsAnyAsync<Exception>(() => Script(ScriptKind.Execute));

        // PostgreSQL "Modify" opens the trigger function, found through the trigger's dependencies.
        var deps = await h.Rpc.MetaDependencies(new DefinitionParams { SessionId = sid, Object = r });
        if (engine == "pg") Assert.Contains(deps, d => d.Direction == DependencyDirection.Uses && d.Object.Kind == ObjectKind.Func && d.Object.Name == "q_trg_log");
        Assert.Contains(deps, d => d.Direction == DependencyDirection.Uses && d.Object.Name.StartsWith("q_trg", StringComparison.Ordinal));

        // DROP and CREATE runs as one script and leaves the trigger enabled (with a new object id).
        await Ok(h, sid, await Script(ScriptKind.DropCreate));
        Assert.Equal("on", await Text(h, sid, state));
    }

    [IntegrationFact]
    public async Task Pg_partition_clones_stay_hidden_view_triggers_refuse_enable_and_constraint_triggers_drop_and_create()
    {
        var (h, p, sid) = await SetupAsync("pg");
        await using var _h = h;
        await Ok(h, sid, """
            DROP TABLE IF EXISTS q_trg_part CASCADE; DROP VIEW IF EXISTS q_trg_v;
            CREATE TABLE q_trg_part (id int, k int) PARTITION BY RANGE (k);
            CREATE TABLE q_trg_part_a PARTITION OF q_trg_part FOR VALUES FROM (0) TO (10);
            CREATE TABLE q_trg_part_b PARTITION OF q_trg_part FOR VALUES FROM (10) TO (20);
            CREATE TRIGGER q_trg_part_row AFTER UPDATE ON q_trg_part FOR EACH ROW EXECUTE FUNCTION q_trg_log();
            CREATE VIEW q_trg_v AS SELECT id, price FROM q_trg;
            CREATE TRIGGER q_trg_v_ins INSTEAD OF INSERT ON q_trg_v FOR EACH ROW EXECUTE FUNCTION q_trg_log();
            CREATE CONSTRAINT TRIGGER q_trg_check AFTER UPDATE ON q_trg DEFERRABLE FOR EACH ROW EXECUTE FUNCTION q_trg_log();
            """);
        await RefreshAsync(h, p, full: true);
        var snap = h.Services.Metadata.Cache.Get(p.Id, Db)!;
        SnapObject Trigger(string name) => Assert.Single(snap.Objects, o => o.Kind == ObjectKind.Trigger && o.Name == name);

        // One trigger under the partitioned table, none cloned under its partitions.
        var parent = snap.Objects.Single(o => o.Kind == ObjectKind.Table && o.Name == "q_trg_part");
        Assert.Equal(parent.Id, Trigger("q_trg_part_row").ParentObjectId);

        async Task<string> Script(SnapObject o, ScriptKind k) => (await h.Rpc.MetaScript(new ScriptParams { SessionId = sid, Object = Ref(p, o), Kind = k })).Sql;
        var onView = Trigger("q_trg_v_ins");
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => Script(onView, ScriptKind.Disable));
        Assert.Contains("view", refused.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => Script(onView, ScriptKind.Enable));

        var alter = await Script(Trigger("q_trg_check"), ScriptKind.Alter);
        Assert.StartsWith("DROP TRIGGER IF EXISTS q_trg_check ON public.q_trg;", alter);
        await Ok(h, sid, alter);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Renaming_a_column_a_trigger_uses_lists_the_trigger_and_it_still_fires_afterwards(string engine)
    {
        var (h, p, sid) = await SetupAsync(engine);
        await using var _h = h;
        var target = new RefactorTarget { Kind = RefactorKind.Column, Schema = engine == "mssql" ? "dbo" : "public", Name = "q_trg", Column = "price" };
        var usages = await h.Rpc.RefactorFindUsages(new FindUsagesParams { SessionId = sid, Database = Db, Target = target });
        Assert.Contains(usages.Hits, x => x.Object?.Kind == ObjectKind.Trigger && x.Object.Name == "q_trg_price");

        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = sid, Database = Db, Target = target, NewName = "unit_price" });
        var trigger = Assert.Single(preview.Objects, o => o.Object.Kind == ObjectKind.Trigger);
        Assert.Equal(engine == "pg", trigger.ServerUpdated); // pg_trigger stores attnums; T-SQL bodies are rewritten
        await Ok(h, sid, preview.Sql);

        await Ok(h, sid, engine == "mssql" ? "UPDATE dbo.q_trg SET unit_price = 11 WHERE id = 1" : "UPDATE q_trg SET unit_price = 11 WHERE id = 1");
        // The trigger fired once for row 1 (its WHEN / IF UPDATE now names unit_price).
        Assert.Equal("1", await Text(h, sid, engine == "mssql"
            ? "SELECT CAST(COUNT(*) AS nvarchar(20)) FROM dbo.q_trg_audit WHERE id = 1"
            : "SELECT count(*)::text FROM q_trg_audit WHERE id = 1"));
    }
}

using Apache.Arrow;
using Querithm.Contract;

namespace Querithm.Tests.Integration;

/// <summary>
/// Find usages and rename on both engines: a column used by three routines and a view (and, on SQL Server, a
/// schema-bound view, a computed column, a check and a filtered index) is renamed by the previewed script, everything
/// still runs afterwards, and a module that cannot be recreated makes the whole rename roll back.
/// </summary>
[Collection("refactor")]
public class RefactorIntegrationTests
{
    const string Db = "querithm_edit";

    public static TheoryData<string> Engines => TestEngines.Names;

    static async Task Ok(TestHost h, string sid, string sql)
    {
        var (done, _, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)) + done.Error?.Message + "\n" + sql);
    }

    static async Task<long> Scalar(TestHost h, string sid, string sql)
    {
        var (done, b, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        return b[0].Column(0) switch { Int32Array a => a.GetValue(0) ?? -1, Int64Array a => a.GetValue(0) ?? -1, _ => throw new InvalidOperationException() };
    }

    const string MssqlSetup = """
        DROP VIEW IF EXISTS dbo.q_rf_avw; DROP VIEW IF EXISTS dbo.q_rf_zvw; DROP FUNCTION IF EXISTS dbo.q_rf_afn;
        DROP VIEW IF EXISTS dbo.q_rf_v; DROP VIEW IF EXISTS dbo.q_rf_sb; DROP VIEW IF EXISTS dbo.q_rf_broken;
        DROP PROCEDURE IF EXISTS dbo.q_rf_p1; DROP PROCEDURE IF EXISTS dbo.q_rf_p2; DROP PROCEDURE IF EXISTS dbo.q_rf_p3; DROP PROCEDURE IF EXISTS dbo.q_rf_dyn;
        DROP TABLE IF EXISTS dbo.q_rf_gone; DROP TABLE IF EXISTS dbo.q_rf; DROP TABLE IF EXISTS dbo.q_rf_orders;
        GO
        CREATE TABLE dbo.q_rf (id int PRIMARY KEY, order_date date NULL, qty int NULL, total AS (DATEDIFF(day, '2000-01-01', order_date)),
          CONSTRAINT ck_q_rf CHECK (order_date > '2000-01-01'));
        CREATE INDEX ix_q_rf_recent ON dbo.q_rf (id) WHERE order_date > '2020-01-01';
        INSERT dbo.q_rf (id, order_date, qty) VALUES (1, '2026-10-21', 3);
        GO
        CREATE PROCEDURE dbo.q_rf_p1 AS SELECT r.order_date FROM dbo.q_rf r; -- order_date in a comment
        GO
        CREATE PROCEDURE dbo.q_rf_p2 @d date AS UPDATE dbo.q_rf SET order_date = @d WHERE id = 1;
        GO
        CREATE PROCEDURE dbo.q_rf_p3 AS BEGIN INSERT INTO dbo.q_rf (id, order_date) VALUES (2, '2026-01-01'); DELETE FROM dbo.q_rf WHERE id = 2; END
        GO
        CREATE VIEW dbo.q_rf_v AS SELECT id, order_date FROM dbo.q_rf;
        GO
        CREATE VIEW dbo.q_rf_sb WITH SCHEMABINDING AS SELECT id, order_date FROM dbo.q_rf;
        GO
        CREATE PROCEDURE dbo.q_rf_dyn AS EXEC (N'SELECT order_date FROM dbo.q_rf');
        GO
        """;

    const string PgSetup = """
        DROP VIEW IF EXISTS q_rf_avw; DROP VIEW IF EXISTS q_rf_zvw; DROP FUNCTION IF EXISTS q_rf_afn(int);
        DROP VIEW IF EXISTS q_rf_v;
        DROP FUNCTION IF EXISTS q_rf_p1(); DROP FUNCTION IF EXISTS q_rf_p2(date); DROP PROCEDURE IF EXISTS q_rf_p3();
        DROP FUNCTION IF EXISTS q_rf_dyn(); DROP FUNCTION IF EXISTS q_rf_broken();
        DROP TABLE IF EXISTS q_rf_gone; DROP TABLE IF EXISTS q_rf CASCADE; DROP TABLE IF EXISTS q_rf_orders CASCADE;
        CREATE TABLE q_rf (id int PRIMARY KEY, order_date date, qty int, total int GENERATED ALWAYS AS (qty * 2) STORED,
          CONSTRAINT ck_q_rf CHECK (order_date > '2000-01-01'));
        CREATE INDEX ix_q_rf_recent ON q_rf (id) WHERE order_date > '2020-01-01';
        INSERT INTO q_rf (id, order_date, qty) VALUES (1, '2026-10-21', 3);
        CREATE FUNCTION q_rf_p1() RETURNS SETOF date LANGUAGE sql AS $$ SELECT r.order_date FROM q_rf r $$;
        CREATE FUNCTION q_rf_p2(d date) RETURNS void LANGUAGE plpgsql AS $$ BEGIN UPDATE q_rf SET order_date = d WHERE id = 1; END $$;
        CREATE PROCEDURE q_rf_p3() LANGUAGE sql AS $$ INSERT INTO q_rf (id, order_date) VALUES (2, '2026-01-01'); DELETE FROM q_rf WHERE id = 2 $$;
        CREATE VIEW q_rf_v AS SELECT id, order_date FROM q_rf;
        CREATE FUNCTION q_rf_dyn() RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE 'SELECT order_date FROM q_rf'; END $$;
        """;

    static async Task<(TestHost Host, Profile Profile, string Session)> SetupAsync(string engine, string? extra = null)
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
        if (extra != null) await Ok(h, sid, extra);
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = Db, Full = true });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, Db);
        return (h, p, sid);
    }

    static RefactorTarget Column(string engine) => new() { Kind = RefactorKind.Column, Schema = engine == "mssql" ? "dbo" : "public", Name = "q_rf", Column = "order_date" };

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Find_usages_lists_every_module_and_what_cannot_be_checked(string engine)
    {
        var (h, _, sid) = await SetupAsync(engine);
        await using var _h = h;
        const string tab = "SELECT x.order_date, 'order_date' FROM q_rf x; -- order_date";
        var r = await h.Rpc.RefactorFindUsages(new FindUsagesParams
        {
            SessionId = sid, Database = Db, Target = Column(engine), Tabs = [new OpenTabText { TabId = "tab1", Title = "Query", Text = tab }],
        });
        var objects = r.Hits.Where(x => x.Object != null).Select(x => x.Object!.Name).Distinct().Order().ToArray();
        Assert.Equal(engine == "mssql" ? ["q_rf_p1", "q_rf_p2", "q_rf_p3", "q_rf_sb", "q_rf_v"] : ["q_rf_p1", "q_rf_p2", "q_rf_p3", "q_rf_v"], objects);
        var tabHit = Assert.Single(r.Hits, x => x.TabId == "tab1");
        Assert.Equal((1, 10), (tabHit.Line, tabHit.Column));
        Assert.Contains(r.Unchecked, u => u.Object.Name == "q_rf_dyn" && u.Reason.Contains("run time"));
        if (engine == "pg")
        {
            Assert.Contains(r.ServerUpdated, o => o.Name == "q_rf_v");
            Assert.Contains(r.Hits, x => x.Object?.Name == "q_rf_p2" && x.Confidence == UsageConfidence.Possible); // PL/pgSQL: may be a variable
        }
        else Assert.All(r.Hits, x => Assert.Equal(UsageConfidence.Exact, x.Confidence));

        // The cursor resolves to the column (through the alias) and a local alias renames in the editor only.
        var at = await h.Rpc.RefactorSymbolAt(new SymbolAtParams { SessionId = sid, Database = Db, Dialect = engine == "mssql" ? Dialect.Tsql : Dialect.Pg, Text = tab, Offset = 9 });
        Assert.Equal(Column(engine) with { Schema = at.Target!.Schema }, at.Target);
        var local = await h.Rpc.RefactorSymbolAt(new SymbolAtParams { SessionId = sid, Database = Db, Dialect = engine == "mssql" ? Dialect.Tsql : Dialect.Pg, Text = tab, Offset = 7 });
        Assert.Equal(2, local.Local!.Length);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task A_column_rename_rewrites_every_module_and_everything_still_runs(string engine)
    {
        var (h, _, sid) = await SetupAsync(engine);
        await using var _h = h;
        var probe = await h.Rpc.RefactorFindUsages(new FindUsagesParams { SessionId = sid, Database = Db, Target = Column(engine) });
        var possible = probe.Hits.Where(x => x.Confidence == UsageConfidence.Possible).Select(x => x.Id).ToArray();
        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = sid, Database = Db, Target = Column(engine), NewName = "placed_at", Include = possible,
            Tabs = [new OpenTabText { TabId = "t", Title = "q", Text = "SELECT order_date FROM q_rf" }],
        });
        Assert.True(preview.Transactional);
        Assert.Contains("placed_at", preview.Objects.First(o => o.Object.Name == "q_rf_p1").After);
        if (engine == "mssql") Assert.Contains("-- order_date in a comment", preview.Objects.First(o => o.Object.Name == "q_rf_p1").After);
        Assert.Equal("placed_at", Assert.Single(Assert.Single(preview.Tabs).Edits).Text);
        if (engine == "pg") Assert.True(preview.Objects.Single(o => o.Object.Name == "q_rf_v").ServerUpdated);
        await Ok(h, sid, preview.Sql);

        if (engine == "mssql")
        {
            Assert.Equal(1, await Scalar(h, sid, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.q_rf') AND name = 'placed_at'"));
            await Ok(h, sid, "EXEC dbo.q_rf_p1; EXEC dbo.q_rf_p2 '2026-02-02'; EXEC dbo.q_rf_p3; SELECT placed_at FROM dbo.q_rf_v; SELECT placed_at FROM dbo.q_rf_sb; SELECT total FROM dbo.q_rf;");
            Assert.Equal(1, await Scalar(h, sid, "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'ck_q_rf'"));
            Assert.Equal(1, await Scalar(h, sid, "SELECT COUNT(*) FROM sys.indexes WHERE name = 'ix_q_rf_recent' AND has_filter = 1"));
            Assert.Equal(0, await Scalar(h, sid, "SELECT COUNT(*) FROM sys.sql_modules WHERE definition LIKE '%r.order_date%'"));
        }
        else
        {
            Assert.Equal(1, await Scalar(h, sid, "SELECT count(*)::int FROM information_schema.columns WHERE table_name = 'q_rf' AND column_name = 'placed_at'"));
            // The view follows the renamed column but keeps its output name, so its own readers keep working.
            await Ok(h, sid, "SELECT q_rf_p1(); SELECT q_rf_p2('2026-02-02'); CALL q_rf_p3(); SELECT order_date FROM q_rf_v;");
            Assert.Contains(preview.Warnings, w => w.Contains("q_rf_v") && w.Contains("output column name"));
        }
        Assert.Equal(new DateTime(2026, 2, 2), DateTime.Parse(((StringArray)(await h.RunAsync(sid, engine == "mssql"
            ? "SELECT CONVERT(varchar(10), placed_at, 23) FROM dbo.q_rf WHERE id = 1" : "SELECT placed_at::text FROM q_rf WHERE id = 1")).Batches[0].Column(0)).GetString(0)!));
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task A_module_that_cannot_be_recreated_rolls_the_whole_rename_back(string engine)
    {
        var broken = engine == "mssql"
            ? """
              CREATE TABLE dbo.q_rf_gone (id int);
              GO
              CREATE VIEW dbo.q_rf_broken AS SELECT r.order_date FROM dbo.q_rf r JOIN dbo.q_rf_gone g ON g.id = r.id;
              GO
              DROP TABLE dbo.q_rf_gone;
              """
            : """
              CREATE TABLE q_rf_gone (id int);
              CREATE FUNCTION q_rf_broken() RETURNS int LANGUAGE sql AS $$ SELECT count(*)::int FROM q_rf r JOIN q_rf_gone g ON g.id = r.id WHERE r.order_date IS NOT NULL $$;
              DROP TABLE q_rf_gone;
              """;
        var (h, _, sid) = await SetupAsync(engine, broken);
        await using var _h = h;
        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = sid, Database = Db, Target = Column(engine), NewName = "placed_at" });
        Assert.Contains(preview.Objects, o => o.Object.Name == "q_rf_broken");
        await h.RunAsync(sid, preview.Sql);
        if (engine == "pg") await h.RunAsync(sid, "ROLLBACK");
        var stillThere = engine == "mssql"
            ? "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.q_rf') AND name = 'order_date'"
            : "SELECT count(*)::int FROM information_schema.columns WHERE table_name = 'q_rf' AND column_name = 'order_date'";
        Assert.Equal(1, await Scalar(h, sid, stillThere));
        var untouched = engine == "mssql"
            ? "SELECT COUNT(*) FROM sys.sql_modules WHERE object_id = OBJECT_ID('dbo.q_rf_p1') AND definition LIKE '%r.order_date%'"
            : "SELECT count(*)::int FROM pg_proc WHERE proname = 'q_rf_p1' AND prosrc LIKE '%r.order_date%'";
        Assert.Equal(1, await Scalar(h, sid, untouched));
        if (engine == "mssql") Assert.Equal(1, await Scalar(h, sid, "SELECT COUNT(*) FROM sys.views WHERE name = 'q_rf_sb'")); // the dropped schema-bound view came back
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task A_table_rename_rewrites_its_callers(string engine)
    {
        var (h, _, sid) = await SetupAsync(engine);
        await using var _h = h;
        var schema = engine == "mssql" ? "dbo" : "public";
        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = sid, Database = Db, Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = schema, Name = "q_rf" }, NewName = "q_rf_orders",
        });
        await Ok(h, sid, preview.Sql);
        if (engine == "mssql")
        {
            await Ok(h, sid, "EXEC dbo.q_rf_p1; EXEC dbo.q_rf_p2 '2026-03-03'; EXEC dbo.q_rf_p3; SELECT order_date FROM dbo.q_rf_v; SELECT order_date FROM dbo.q_rf_sb;");
            Assert.Equal(1, await Scalar(h, sid, "SELECT COUNT(*) FROM sys.tables WHERE name = 'q_rf_orders'"));
        }
        else
        {
            await Ok(h, sid, "SELECT q_rf_p1(); SELECT q_rf_p2('2026-03-03'); CALL q_rf_p3(); SELECT order_date FROM q_rf_v;");
            Assert.Equal(1, await Scalar(h, sid, "SELECT count(*)::int FROM pg_class WHERE relname = 'q_rf_orders'"));
        }
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Dependent_modules_are_dropped_and_recreated_in_dependency_order(string engine)
    {
        // Name order is wrong both ways: q_rf_afn is referenced by q_rf_zvw, which q_rf_avw reads.
        var extra = engine == "mssql"
            ? """
              CREATE FUNCTION dbo.q_rf_afn(@id int) RETURNS date WITH SCHEMABINDING AS BEGIN RETURN (SELECT order_date FROM dbo.q_rf WHERE id = @id); END
              GO
              CREATE VIEW dbo.q_rf_zvw WITH SCHEMABINDING AS SELECT id, order_date, dbo.q_rf_afn(id) AS d2 FROM dbo.q_rf;
              GO
              CREATE VIEW dbo.q_rf_avw WITH SCHEMABINDING AS SELECT v.id, r.order_date FROM dbo.q_rf_zvw v JOIN dbo.q_rf r ON r.id = v.id;
              GO
              """
            : """
              CREATE FUNCTION q_rf_afn(i int) RETURNS date LANGUAGE sql AS $$ SELECT order_date FROM q_rf WHERE id = i $$;
              CREATE VIEW q_rf_zvw AS SELECT id, order_date, q_rf_afn(id) AS d2 FROM q_rf;
              CREATE VIEW q_rf_avw AS SELECT v.id, r.order_date FROM q_rf_zvw v JOIN q_rf r ON r.id = v.id;
              """;
        var (h, _, sid) = await SetupAsync(engine, extra);
        await using var _h = h;
        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = sid, Database = Db, Target = Column(engine), NewName = "placed_on" });
        if (engine == "mssql")
        {
            int At(string what) { var k = preview.Sql.IndexOf(what, StringComparison.Ordinal); Assert.True(k >= 0, $"{what} missing in\n{preview.Sql}"); return k; }
            Assert.True(At("DROP VIEW [dbo].[q_rf_avw]") < At("DROP VIEW [dbo].[q_rf_zvw]"), preview.Sql);
            Assert.True(At("DROP VIEW [dbo].[q_rf_zvw]") < At("DROP FUNCTION [dbo].[q_rf_afn]"), preview.Sql);
            Assert.True(At("CREATE OR ALTER FUNCTION dbo.q_rf_afn") < At("CREATE OR ALTER VIEW dbo.q_rf_zvw"), preview.Sql);
            Assert.True(At("CREATE OR ALTER VIEW dbo.q_rf_zvw") < At("CREATE OR ALTER VIEW dbo.q_rf_avw"), preview.Sql);
        }
        await Ok(h, sid, preview.Sql);
        var probe = engine == "mssql" ? "SELECT placed_on, d2 FROM dbo.q_rf_zvw; SELECT placed_on FROM dbo.q_rf_avw;" : "SELECT order_date, d2 FROM q_rf_zvw; SELECT order_date FROM q_rf_avw;";
        await Ok(h, sid, probe);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task A_rename_onto_a_taken_name_is_refused_before_any_script(string engine)
    {
        var (h, _, sid) = await SetupAsync(engine);
        await using var _h = h;
        var schema = engine == "mssql" ? "dbo" : "public";
        var col = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = sid, Database = Db, Target = Column(engine), NewName = "qty" }));
        Assert.Equal("refactorConflict", col.Code);
        var obj = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = sid, Database = Db, Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = schema, Name = "q_rf" }, NewName = "q_rf_v",
        }));
        Assert.Equal("refactorConflict", obj.Code);
    }
}

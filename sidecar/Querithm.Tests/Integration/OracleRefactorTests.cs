using Querithm.Contract;
using static Querithm.Tests.Integration.OracleScriptIntegrationTests;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S11 find usages and rename against the Oracle Free fixture (QUERITHM_IT=1): definitions from ALL_SOURCE / ALL_VIEWS,
/// hits in a view, a procedure, a trigger's :NEW, a package body and a tab, never in strings; dynamic SQL listed as
/// unchecked; the rename script (ALTER TABLE … RENAME [COLUMN], or a view / routine created under the new name and the old
/// one dropped, plus every rewritten unit) runs through the sidecar and every object is VALID after it.
/// </summary>
public sealed class OracleRefactorTests
{
    const string Schema = "QUERITHM_APP";

    static readonly (string Kind, string Name)[] Objects =
    [
        ("TRIGGER", "q_s11r_trg"), ("VIEW", "q_s11r_v"), ("VIEW", "q_s11r_v2"), ("VIEW", "q_s11r_cheap"), ("PROCEDURE", "q_s11r_p"), ("PROCEDURE", "q_s11r_p2"),
        ("PROCEDURE", "q_s11r_caller"), ("PROCEDURE", "q_s11r_dyn"), ("PACKAGE", "q_s11r_pkg"), ("SYNONYM", "q_s11r_syn"),
        ("TABLE", "q_s11r_item"), ("TABLE", "q_s11r_item2"),
    ];

    static async Task SetupAsync()
    {
        await using var c = await OpenAsync();
        foreach (var (k, n) in Objects) await DropAsync(c, k, n);
        await RunScriptAsync(c, """
            CREATE TABLE q_s11r_item (id NUMBER PRIMARY KEY, price NUMBER(10,2), qty NUMBER);
            INSERT INTO q_s11r_item VALUES (1, 5, 2);
            INSERT INTO q_s11r_item VALUES (2, 20, 1);
            COMMIT;
            CREATE VIEW q_s11r_v AS SELECT id, price FROM q_s11r_item WHERE price < 10;
            CREATE VIEW q_s11r_v2 AS SELECT i.id, i.qty * 2 AS twice FROM q_s11r_item i;
            CREATE SYNONYM q_s11r_syn FOR q_s11r_item;
            CREATE PROCEDURE q_s11r_p (t OUT NUMBER) AS
              qty_text VARCHAR2(20) := 'qty';
            BEGIN
              SELECT SUM(price * qty) INTO t FROM q_s11r_item;
            END q_s11r_p;
            /
            CREATE PROCEDURE q_s11r_caller AS
              x NUMBER;
            BEGIN
              q_s11r_p(x);
              FOR r IN (SELECT id, qty FROM q_s11r_item) LOOP
                x := x + r.qty;
              END LOOP;
            END;
            /
            CREATE PROCEDURE q_s11r_dyn AS
            BEGIN
              EXECUTE IMMEDIATE 'UPDATE q_s11r_item SET qty = 0';
            END;
            /
            CREATE TRIGGER q_s11r_trg BEFORE INSERT ON q_s11r_item FOR EACH ROW
            BEGIN
              :NEW.qty := NVL(:NEW.qty, 1);
            END;
            /
            CREATE PACKAGE q_s11r_pkg AS FUNCTION total RETURN NUMBER; END;
            /
            CREATE PACKAGE BODY q_s11r_pkg AS
              FUNCTION total RETURN NUMBER IS n NUMBER;
              BEGIN
                SELECT SUM(qty) INTO n FROM q_s11r_item;
                RETURN n;
              END;
            END;
            /
            """);
    }

    static RefactorTarget Column(string column) => new() { Kind = RefactorKind.Column, Schema = Schema, Name = "Q_S11R_ITEM", Column = column };

    static async Task AllValidAsync()
    {
        await using var c = await OpenAsync();
        var invalid = new List<string>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT object_name || ' ' || object_type FROM user_objects WHERE object_name LIKE 'Q\\_S11R\\_%' ESCAPE '\\' AND status <> 'VALID'";
        await using var r = await cmd.ExecuteReaderAsync(Timeout());
        while (await r.ReadAsync(Timeout())) invalid.Add(r.GetString(0));
        Assert.Empty(invalid);
    }

    [IntegrationFact]
    public async Task Usages_of_a_column_are_found_in_routines_triggers_package_bodies_and_tabs_and_dynamic_SQL_is_unchecked()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        Assert.True((await h.Rpc.SessionCapabilities(new SessionRef { SessionId = h.Sid })).References.Available);
        var r = await h.Rpc.RefactorFindUsages(new FindUsagesParams
        {
            SessionId = h.Sid, Database = OracleS11Host.Db, Target = Column("QTY"),
            Tabs = [new OpenTabText { TabId = "t1", Title = "Query 1", Text = "SELECT qty FROM q_s11r_item;\nSELECT 'qty' AS x FROM dual;" }],
        });
        var where = r.Hits.Select(x => x.Object?.Name ?? x.TabId).ToHashSet();
        Assert.Contains("Q_S11R_P", where);
        Assert.Contains("Q_S11R_TRG", where);
        Assert.Contains("Q_S11R_PKG", where); // the body
        Assert.Contains("Q_S11R_V2", where);
        Assert.Contains("t1", where);
        Assert.DoesNotContain("Q_S11R_V", where); // reads id and price only
        Assert.Single(r.Hits, x => x.TabId == "t1"); // not the string 'qty'
        Assert.Single(r.Hits, x => x.Object?.Name == "Q_S11R_P"); // not the 'qty' literal or qty_text
        Assert.Contains(r.Hits, x => x.Object?.Name == "Q_S11R_CALLER" && x.Confidence == UsageConfidence.Possible); // r.qty
        Assert.Contains(r.Unchecked, u => u.Object.Name == "Q_S11R_DYN" && u.Reason.Contains("EXECUTE IMMEDIATE", StringComparison.Ordinal));
    }

    [IntegrationFact]
    public async Task A_column_rename_runs_and_every_rewritten_unit_compiles()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = h.Sid, Database = OracleS11Host.Db, Target = Column("QTY"), NewName = "quantity" });
        Assert.Contains("ALTER TABLE \"QUERITHM_APP\".\"Q_S11R_ITEM\" RENAME COLUMN \"QTY\" TO \"QUANTITY\";", preview.Sql);
        Assert.False(preview.Transactional);
        var caller = preview.Objects.Single(o => o.Object.Name == "Q_S11R_CALLER");
        Assert.Contains("r.qty", caller.After); // a possible hit is left unless ticked
        var hitId = caller.Hits.Single(x => x.Confidence == UsageConfidence.Possible).Id;
        preview = await h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = h.Sid, Database = OracleS11Host.Db, Target = Column("QTY"), NewName = "quantity", Include = [hitId] });
        Assert.Contains("SELECT id, QUANTITY FROM", preview.Objects.Single(o => o.Object.Name == "Q_S11R_CALLER").After);
        Assert.Contains(":NEW.QUANTITY", preview.Objects.Single(o => o.Object.Name == "Q_S11R_TRG").After);
        await h.RunOkAsync(preview.Sql);
        await AllValidAsync();
        await using var c = await OpenAsync();
        await ExecAsync(c, "BEGIN q_s11r_caller; END;");
    }

    [IntegrationFact]
    public async Task A_table_rename_rewrites_its_readers_and_names_the_synonym_that_breaks()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = h.Sid, Database = OracleS11Host.Db, Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = Schema, Name = "Q_S11R_ITEM" }, NewName = "q_s11r_item2",
        });
        Assert.Contains("ALTER TABLE \"QUERITHM_APP\".\"Q_S11R_ITEM\" RENAME TO \"Q_S11R_ITEM2\";", preview.Sql);
        Assert.Contains(preview.Warnings, w => w.Contains("Synonym QUERITHM_APP.Q_S11R_SYN", StringComparison.Ordinal));
        Assert.Contains("BEFORE INSERT ON Q_S11R_ITEM2", preview.Objects.Single(o => o.Object.Name == "Q_S11R_TRG").After);
        await h.RunOkAsync(preview.Sql);
        await using var c = await OpenAsync();
        await RunScriptAsync(c, "CREATE OR REPLACE SYNONYM q_s11r_syn FOR q_s11r_item2;");
        await AllValidAsync();
    }

    [IntegrationFact]
    public async Task A_procedure_and_a_view_are_created_under_the_new_name_and_the_old_one_dropped()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        var proc = await h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = h.Sid, Database = OracleS11Host.Db, Target = new RefactorTarget { Kind = RefactorKind.Procedure, Schema = Schema, Name = "Q_S11R_P" }, NewName = "q_s11r_p2",
        });
        Assert.Contains("PROCEDURE QUERITHM_APP.Q_S11R_P2", proc.Sql);
        Assert.Contains("END Q_S11R_P2;", proc.Sql);
        Assert.Contains("Q_S11R_P2(x);", proc.Objects.Single(o => o.Object.Name == "Q_S11R_CALLER").After);
        Assert.Contains(proc.Warnings, w => w.Contains("grants on it are not carried over", StringComparison.Ordinal));
        Assert.True(proc.Sql.IndexOf("PROCEDURE QUERITHM_APP.Q_S11R_P2", StringComparison.Ordinal) < proc.Sql.IndexOf("DROP PROCEDURE \"QUERITHM_APP\".\"Q_S11R_P\";", StringComparison.Ordinal));
        await h.RunOkAsync(proc.Sql);
        await h.RefreshAsync();
        var view = await h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = h.Sid, Database = OracleS11Host.Db, Target = new RefactorTarget { Kind = RefactorKind.View, Schema = Schema, Name = "Q_S11R_V" }, NewName = "Q_S11R_CHEAP",
        });
        Assert.Contains("CREATE OR REPLACE VIEW QUERITHM_APP.Q_S11R_CHEAP", view.Sql);
        await h.RunOkAsync(view.Sql);
        await using var c = await OpenAsync();
        Assert.Null(await ScalarAsync(c, "SELECT object_name FROM user_objects WHERE object_name IN ('Q_S11R_P', 'Q_S11R_V')"));
        Assert.Equal(1m, Convert.ToDecimal(await ScalarAsync(c, "SELECT COUNT(*) FROM q_s11r_cheap")));
        await AllValidAsync();
    }

    [IntegrationFact]
    public async Task A_name_taken_in_the_schemas_one_name_space_a_package_and_a_quoted_new_name()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        var taken = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = h.Sid, Database = OracleS11Host.Db, Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = Schema, Name = "Q_S11R_ITEM" }, NewName = "q_s11r_p",
        }));
        Assert.Contains("already exists", taken.Message); // a procedure's name: one name space with tables
        var pkg = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.RefactorFindUsages(new FindUsagesParams
        {
            SessionId = h.Sid, Database = OracleS11Host.Db, Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = Schema, Name = "Q_S11R_PKG" },
        }));
        Assert.Contains("package", pkg.Message);
        var quoted = await h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = h.Sid, Database = OracleS11Host.Db, Target = Column("PRICE"), NewName = "\"Price\"" });
        Assert.Contains("RENAME COLUMN \"PRICE\" TO \"Price\";", quoted.Sql);
        Assert.Contains("\"Price\"", quoted.Objects.Single(o => o.Object.Name == "Q_S11R_V").After);
    }

    [IntegrationFact]
    public async Task Dependencies_by_RPC_are_the_servers_own()
    {
        await SetupAsync();
        await using var h = await OracleS11Host.OpenAsync();
        async Task<string[]> Deps(string name, ObjectKind kind, DependencyDirection d) =>
            (await h.Rpc.MetaDependencies(new DefinitionParams { SessionId = h.Sid, Object = new ObjectRef { ProfileId = h.ProfileId, Database = OracleS11Host.Db, Schema = Schema, Name = name, Kind = kind } }))
            .Where(x => x.Direction == d).Select(x => x.Object.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Q_S11R_CALLER", "Q_S11R_DYN", "Q_S11R_P", "Q_S11R_PKG", "Q_S11R_SYN", "Q_S11R_TRG", "Q_S11R_V", "Q_S11R_V2"],
            (await Deps("Q_S11R_ITEM", ObjectKind.Table, DependencyDirection.UsedBy)).Where(n => n != "Q_S11R_DYN").Append("Q_S11R_DYN").Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["Q_S11R_ITEM", "Q_S11R_P"], await Deps("Q_S11R_CALLER", ObjectKind.Proc, DependencyDirection.Uses));
    }
}

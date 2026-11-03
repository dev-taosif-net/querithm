using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

/// <summary>Find usages and rename through trigger definitions (T-SQL bodies, PostgreSQL pg_get_triggerdef).</summary>
public class TriggerReferenceTests
{
    static RefContext Ctx(string defaultSchema, Dictionary<(string, string), string[]> tables, StringComparer names) => new()
    {
        DefaultSchema = defaultSchema,
        Columns = (s, t) => tables.FirstOrDefault(kv => names.Equals(kv.Key.Item1, s) && names.Equals(kv.Key.Item2, t)).Value,
        SchemasOf = n => tables.Keys.Where(k => names.Equals(k.Item2, n)).Select(k => k.Item1).Distinct().ToArray(),
    };

    static readonly RefContext Ss = Ctx("dbo", new()
    {
        [("dbo", "Orders")] = ["Id", "Price", "Qty"],
        [("dbo", "OrderAudit")] = ["Id", "OldPrice"],
    }, StringComparer.OrdinalIgnoreCase);

    static readonly RefContext Pg = Ctx("public", new()
    {
        [("public", "items")] = ["id", "price", "qty"],
        [("public", "audit")] = ["id", "price"],
    }, StringComparer.Ordinal);

    static string[] Texts(string sql, RefScan scan) => scan.Hits.Select(h => sql.Substring(h.Offset, h.Length)).ToArray();

    const string TsqlTrigger = """
        CREATE TRIGGER dbo.trg_Orders_price ON dbo.Orders AFTER UPDATE AS
        BEGIN
          SET NOCOUNT ON;
          IF UPDATE(Price)
            INSERT INTO dbo.OrderAudit (Id, OldPrice)
            SELECT d.Id, d.Price FROM deleted d JOIN inserted i ON i.Id = d.Id WHERE i.Price <> d.Price;
        END
        """;

    [Fact]
    public void Tsql_a_column_used_only_inside_a_trigger_is_found_through_inserted_deleted_and_update()
    {
        var scan = new TSqlReferenceFinder().Find(TsqlTrigger, new RefTarget(RefKind.Column, "dbo", "Orders", "Price"), Ss);
        Assert.False(scan.ParseFailed);
        // UPDATE(Price), d.Price, i.Price, d.Price — deleted/inserted stand for the trigger's table.
        Assert.Equal(["Price", "Price", "Price", "Price"], Texts(TsqlTrigger, scan));
        Assert.All(scan.Hits, h => Assert.Equal(RefConfidence.Exact, h.Confidence));
        // OldPrice belongs to the audit table, Qty is never named.
        Assert.Empty(new TSqlReferenceFinder().Find(TsqlTrigger, new RefTarget(RefKind.Column, "dbo", "Orders", "Qty"), Ss).Hits);
    }

    [Fact]
    public void Tsql_a_large_trigger_past_the_chunking_threshold_still_finds_columns_through_inserted_deleted_and_update()
    {
        // Regression: past ChunkedDiagnostics.MinLength later chunks lost the trigger header (and its table).
        var padding = string.Concat(Enumerable.Range(0, 8000).Select(i => $"  SET @n = {i};\n"));
        var sql = $"""
            CREATE TRIGGER dbo.trg_Orders_big ON dbo.Orders AFTER UPDATE AS
            BEGIN
              DECLARE @n int;
            {padding}
              IF UPDATE(Price) SELECT i.Price FROM inserted i;
              SELECT d.Price FROM deleted d;
            END
            """;
        Assert.True(sql.Length >= ChunkedDiagnostics.MinLength);
        var scan = new TSqlReferenceFinder().Find(sql, new RefTarget(RefKind.Column, "dbo", "Orders", "Price"), Ss);
        Assert.False(scan.ParseFailed);
        Assert.Equal(["Price", "Price", "Price"], Texts(sql, scan));
    }

    [Fact]
    public void Tsql_the_trigger_table_is_a_reference_but_another_table_column_is_not_its_column()
    {
        var table = new TSqlReferenceFinder().Find(TsqlTrigger, new RefTarget(RefKind.Table, "dbo", "Orders"), Ss);
        Assert.Equal(["Orders"], Texts(TsqlTrigger, table));
        Assert.False(Assert.Single(table.Hits).Definition);
        var audit = new TSqlReferenceFinder().Find(TsqlTrigger, new RefTarget(RefKind.Column, "dbo", "OrderAudit", "Id"), Ss);
        Assert.Equal(["Id"], Texts(TsqlTrigger, audit)); // the INSERT column list only, not i.Id / d.Id
    }

    [Fact]
    public void Tsql_inserted_outside_a_trigger_is_an_unknown_table()
    {
        const string sql = "CREATE PROCEDURE dbo.p AS SELECT i.Price FROM inserted i;";
        Assert.Empty(new TSqlReferenceFinder().Find(sql, new RefTarget(RefKind.Column, "dbo", "Orders", "Price"), Ss).Hits);
    }

    const string PgTrigger =
        "CREATE TRIGGER items_price BEFORE INSERT OR UPDATE OF price, qty ON public.items FOR EACH ROW " +
        "WHEN ((new.price IS DISTINCT FROM old.price)) EXECUTE FUNCTION public.items_touch();";

    [Fact]
    public void Pg_update_of_columns_and_the_when_condition_name_columns_of_the_trigger_table()
    {
        var f = new PgReferenceFinder();
        Assert.Equal(["price", "price", "price"], Texts(PgTrigger, f.Find(PgTrigger, new RefTarget(RefKind.Column, "public", "items", "price"), Pg)));
        Assert.Equal(["qty"], Texts(PgTrigger, f.Find(PgTrigger, new RefTarget(RefKind.Column, "public", "items", "qty"), Pg)));
        // Another table's column of the same name is not referenced, and UPDATE OF is not read as a table.
        Assert.Empty(f.Find(PgTrigger, new RefTarget(RefKind.Column, "public", "audit", "price"), Pg).Hits);
        Assert.Equal(["items"], Texts(PgTrigger, f.Find(PgTrigger, new RefTarget(RefKind.Table, "public", "items"), Pg)));
        Assert.Equal(["items_touch"], Texts(PgTrigger, f.Find(PgTrigger, new RefTarget(RefKind.Function, "public", "items_touch"), Pg)));
    }

    [Fact]
    public void Pg_triggers_follow_renames_on_the_server()
    {
        Assert.True(new PgReferenceFinder().ServerTracked(PgTrigger, ObjectKind.Trigger));
        Assert.False(new TSqlReferenceFinder().ServerTracked(TsqlTrigger, ObjectKind.Trigger));
    }

    [Fact]
    public void Pg_a_statement_that_is_not_a_trigger_is_read_as_before()
    {
        const string sql = "UPDATE public.items SET price = 1 WHERE qty > 0;";
        Assert.Equal(["price"], Texts(sql, new PgReferenceFinder().Find(sql, new RefTarget(RefKind.Column, "public", "items", "price"), Pg)));
    }

    // ------------------------------------------------------------------ through RefactorService (cached definitions)

    public static TheoryData<string> Engines => TestEngines.Names;

    [Theory, MemberData(nameof(Engines))]
    public async Task Find_usages_and_rename_include_triggers_and_report_one_whose_definition_is_not_visible(string engine)
    {
        await using var h = new TestHost();
        var pg = engine == "pg";
        var s = pg ? "public" : "dbo";
        var table = pg ? "items" : "Orders";
        var col = pg ? "price" : "Price";
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = pg ? Engine.Pg : Engine.Mssql, Host = "h" });
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" })).SessionId;
        h.Services.Metadata.Store.ApplyRefresh(p.Id, "db", true,
            [
                new ObjectStamp(1, s, table, ObjectKind.Table, 1, null),
                new ObjectStamp(2, s, pg ? "items_price" : "trg_Orders_price", ObjectKind.Trigger, 1, null, 1),
                // PostgreSQL: a trigger named like its table must not stand in for the table in name lookups.
                new ObjectStamp(3, s, pg ? "items" : "trg_hidden", ObjectKind.Trigger, 1, null, 1),
            ], [],
            new MetadataBundle(
            [
                new ColumnRow(1, 1, pg ? "id" : "Id", "int", null, null, null, false, true, false, null),
                new ColumnRow(1, 2, col, "int", null, null, null, true, false, false, null),
            ], [], [], []), "16");
        h.Services.Metadata.Store.CacheDefinitions(p.Id, "db", [(2, pg ? PgTrigger : TsqlTrigger), (3, null)]);
        h.Services.Metadata.Cache.Invalidate(p.Id, "db");

        var target = new RefactorTarget { Kind = RefactorKind.Column, Schema = s, Name = table, Column = col };
        var usages = await h.Rpc.RefactorFindUsages(new FindUsagesParams { SessionId = sid, Database = "db", Target = target });
        Assert.NotEmpty(usages.Hits);
        Assert.All(usages.Hits, x => Assert.Equal((ObjectKind.Trigger, 2L), (x.Object!.Kind, x.Object.ObjectId!.Value)));
        // No VIEW DEFINITION (or encrypted): listed as unchecked with the reason, never silently skipped.
        var hidden = Assert.Single(usages.Unchecked);
        Assert.Equal(3L, hidden.Object.ObjectId);
        Assert.Contains("not visible", hidden.Reason);

        if (pg)
        {
            var preview = await h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = sid, Database = "db", Target = target, NewName = "unit_price" });
            var trigger = Assert.Single(preview.Objects);
            Assert.Equal(ObjectKind.Trigger, trigger.Object.Kind);
            // pg_trigger keeps attnums and the WHEN tree: the server follows the rename, nothing is rewritten.
            Assert.True(trigger.ServerUpdated);
            Assert.DoesNotContain("TRIGGER", preview.Sql);
        }
        else
        {
            // A column rename reads the catalog model (RefactorIntegrationTests); a table rename does not.
            var preview = await h.Rpc.RefactorPreviewRename(new RenameParams
            {
                SessionId = sid, Database = "db", Target = new RefactorTarget { Kind = RefactorKind.Table, Schema = s, Name = table }, NewName = "PurchaseOrders",
            });
            var trigger = Assert.Single(preview.Objects);
            Assert.Equal(ObjectKind.Trigger, trigger.Object.Kind);
            Assert.False(trigger.ServerUpdated);
            Assert.Contains("CREATE OR ALTER TRIGGER dbo.trg_Orders_price ON dbo.PurchaseOrders", preview.Sql);
            Assert.Contains("FROM deleted d JOIN inserted i", trigger.After);
        }
    }
}

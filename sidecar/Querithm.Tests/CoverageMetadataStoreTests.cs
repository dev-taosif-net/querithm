using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Store;

namespace Querithm.Tests;

/// <summary>The local metadata cache (explorer, autocomplete, search): the same behaviour whatever engine filled it.</summary>
public class MetadataStoreSearchAndDetailsTests
{
    static MetadataStore Filled(string profile = "p", string db = "db")
    {
        var store = new MetadataStore(LocalStore.InMemory());
        ObjectStamp S(long id, string schema, string name, ObjectKind kind) => new(id, schema, name, kind, id, null);
        store.ApplyRefresh(profile, db, true,
            [
                S(1, "dbo", "Orders", ObjectKind.Table), S(2, "dbo", "OrderLines", ObjectKind.Table), S(3, "sales", "Orders", ObjectKind.Table),
                S(4, "dbo", "vw_Orders", ObjectKind.View), S(5, "dbo", "usp_Order_Create", ObjectKind.Proc), S(6, "dbo", "100%_done", ObjectKind.Table),
                S(7, "dbo", "fn_total", ObjectKind.Func),
            ], [],
            new MetadataBundle(
                [new ColumnRow(1, 1, "Id", "int", null, 10, 0, false, true, false, null), new ColumnRow(1, 2, "Total", "decimal", null, 10, 2, true, false, false, "0")],
                [new ParamRow(5, 1, "@id", "int", null, false, false)],
                [new IndexRow(1, "PK_Orders", true, true, ["Id"]), new IndexRow(1, "IX_Total", false, false, ["Total", "Id"])],
                [new FkRow("FK_Lines_Orders", 2, 1, [new FkColumnPair { Parent = "OrderId", Referenced = "Id" }])]),
            "16.0");
        return store;
    }

    [Fact]
    public void Search_ranks_prefix_matches_first_then_shorter_names()
    {
        var hits = Filled().Search("p", "db", "order", null, 50);
        Assert.Equal(["Orders", "Orders", "OrderLines", "vw_Orders", "usp_Order_Create"], hits.Select(h => h.Name));
        Assert.All(hits, h => Assert.Equal("db", h.Database));
    }

    [Fact]
    public void Search_can_be_limited_to_kinds_and_a_schema_qualifier()
    {
        var store = Filled();
        Assert.Equal(["usp_Order_Create"], store.Search("p", "db", "order", [ObjectKind.Proc], 50).Select(h => h.Name));
        Assert.Equal(["Orders", "OrderLines", "vw_Orders"], store.Search("p", "db", "order", [ObjectKind.Table, ObjectKind.View], 50).Where(h => h.Schema == "dbo").Select(h => h.Name));
        var qualified = store.Search("p", "db", "[sales].[orders]", null, 50);
        Assert.Equal("sales", Assert.Single(qualified).Schema);
        Assert.Equal(["Orders"], store.Search("p", "db", "\"sales\".Ord", null, 50).Select(h => h.Name));
    }

    [Fact]
    public void Search_treats_like_wildcards_literally_and_honours_the_limit()
    {
        var store = Filled();
        Assert.Equal(["100%_done"], store.Search("p", "db", "100%", null, 50).Select(h => h.Name));
        Assert.All(store.Search("p", "db", "%", null, 50), h => Assert.Equal("100%_done", h.Name));
        Assert.All(store.Search("p", "db", "_", null, 50), h => Assert.Contains("_", h.Name));
        Assert.Single(store.Search("p", "db", "order", null, 1));
        Assert.Empty(store.Search("other-profile", "db", "order", null, 50));
        Assert.Empty(store.Search("p", "other-db", "order", null, 50));
    }

    [Fact]
    public void Object_details_list_columns_indexes_and_foreign_keys_in_both_directions()
    {
        var store = Filled();
        var orders = store.Details("p", "db", 1);
        Assert.Equal(["Id", "Total"], orders.Columns.Select(c => c.Name));
        Assert.Equal(["PK_Orders", "IX_Total"], orders.Indexes.Select(i => i.Name));
        Assert.Empty(orders.ForeignKeys);
        var referencing = Assert.Single(orders.ReferencedBy);
        Assert.Equal("FK_Lines_Orders", referencing.Name);
        var lines = store.Details("p", "db", 2);
        Assert.Equal("FK_Lines_Orders", Assert.Single(lines.ForeignKeys).Name);
        Assert.Empty(lines.ReferencedBy);
        Assert.Equal("@id", Assert.Single(store.Details("p", "db", 5).Params).Name);
        var missing = store.Details("p", "db", 999);
        Assert.Empty(missing.Columns);
        Assert.Empty(missing.Indexes);
    }

    [Fact]
    public void Definitions_are_cached_per_object_and_unavailable_ones_are_remembered()
    {
        var store = Filled();
        Assert.Equal([4L, 5L, 7L], store.MissingDefinitionIds("p", "db").Order());
        store.CacheDefinition("p", "db", 4, "CREATE VIEW ...");
        store.CacheDefinitions("p", "db", [(5, "CREATE PROCEDURE ..."), (7, null)]);
        Assert.Empty(store.MissingDefinitionIds("p", "db"));
        var cached = store.Definitions("p", "db");
        Assert.Equal("CREATE VIEW ...", cached[4]);
        Assert.Null(cached[7]);
        Assert.Contains(5L, cached.Keys);
        store.CacheDefinition("p", "db", 4, "CREATE VIEW v2");
        Assert.Equal("CREATE VIEW v2", store.Definitions("p", "db")[4]);
    }

    [Fact]
    public void Known_databases_are_listed_per_profile_in_order()
    {
        var store = Filled("p", "zeta");
        store.SetStatus("p", "alpha", SnapshotStatus.Empty);
        store.SetStatus("q", "other", SnapshotStatus.Empty);
        Assert.Equal(["alpha", "zeta"], store.KnownDatabases("p"));
        Assert.Equal(["other"], store.KnownDatabases("q"));
        Assert.Empty(store.KnownDatabases("none"));
    }

    [Fact]
    public void Snapshot_status_round_trips_and_keeps_earlier_values_when_not_given()
    {
        var store = new MetadataStore(LocalStore.InMemory());
        Assert.Null(store.GetSnapshotRow("p", "db"));
        store.SetStatus("p", "db", SnapshotStatus.Refreshing, serverVersion: "16.0", refreshedAt: 1234);
        store.SetStatus("p", "db", SnapshotStatus.Error, error: "boom");
        var row = store.GetSnapshotRow("p", "db")!;
        Assert.Equal(SnapshotStatus.Error, row.Status);
        Assert.Equal("boom", row.Error);
        Assert.Equal("16.0", row.ServerVersion);
        Assert.Equal(1234, row.RefreshedAt);
    }
}

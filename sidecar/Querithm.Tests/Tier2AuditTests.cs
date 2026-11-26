using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Metadata;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

/// <summary>
/// Completion audit 2026-11-25, Phase 0 regressions in tier 2 that are not T-SQL parsing: SET targets (M9), the 500-item
/// cap keeping prefix matches (M3), bounded per-schema rules caches (M13). Every engine where the behaviour applies.
/// </summary>
public class Tier2AuditTests
{
    static SnapColumn C(string name, string type = "integer") => new() { Name = name, Type = type };

    /// <summary>orders / customers in the engine's default schema, spelled as its catalog spells them.</summary>
    static SchemaSnapshot Shop(string engine)
    {
        var upper = engine == "oracle";
        string N(string s) => upper ? s.ToUpperInvariant() : s;
        var schema = engine switch { "tsql" => "dbo", "pg" => "public", "mysql" => "shop", "oracle" => "APP", _ => "main" };
        return new SchemaSnapshot
        {
            ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Fks = [],
            Objects =
            [
                new SnapObject { Id = 1, Schema = schema, Name = N("orders"), Kind = ObjectKind.Table, Columns = [C(N("id")), C(N("customer_id")), C(N("status"), "text"), C(N("total"), "numeric")] },
                new SnapObject { Id = 2, Schema = schema, Name = N("customers"), Kind = ObjectKind.Table, Columns = [C(N("customer_id")), C(N("full_name"), "text")] },
            ],
        };
    }

    static IDialectService Service(string engine) => engine switch
    {
        "tsql" => new TSqlLanguageService(), "pg" => new PgLanguageService(), "mysql" => new MySqlLanguageService(),
        "oracle" => new OracleLanguageService(), "sqlite" => new SqliteLanguageService(), _ => new DuckDbLanguageService(),
    };

    static Dialect DialectOf(string engine) => engine switch
    {
        "tsql" => Dialect.Tsql, "pg" => Dialect.Pg, "mysql" => Dialect.MySql, "oracle" => Dialect.Oracle, "sqlite" => Dialect.Sqlite, _ => Dialect.DuckDb,
    };

    static string[] Labels(string engine, string marked, SchemaSnapshot? snap = null)
    {
        var at = marked.IndexOf('|');
        var items = Service(engine).Complete(new CompleteParams
        {
            DocId = "audit-" + Guid.NewGuid(), Text = marked.Remove(at, 1), Offset = at, Dialect = DialectOf(engine), ProfileId = "p", Database = "shop",
            Schema = engine == "oracle" ? "APP" : null,
        }, new FakeSchema(snap ?? Shop(engine), "shop"), CancellationToken.None);
        return items.Select(i => i.Label).ToArray();
    }

    static void Offers(string engine, string marked, string[] must, string[] mustNot)
    {
        var labels = Labels(engine, marked);
        foreach (var m in must) Assert.True(labels.Contains(m), $"{engine}: `{marked}` should offer {m}; got [{string.Join(", ", labels)}]");
        foreach (var m in mustNot) Assert.False(labels.Contains(m), $"{engine}: `{marked}` should not offer {m}; got [{string.Join(", ", labels)}]");
    }

    // ------------------------------------------------------------------------------------------- M9: SET targets

    /// <summary>PostgreSQL, SQLite, DuckDB reject a qualified SET target and only the target's columns can be set.</summary>
    [Theory]
    [InlineData("pg")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public void Update_from_set_target_offers_only_the_targets_columns_bare(string engine)
    {
        Offers(engine, "UPDATE orders o SET | FROM customers c WHERE c.customer_id = o.customer_id",
            must: ["status", "total"], mustNot: ["o.status", "c.full_name", "full_name", "c.customer_id"]);
        // After a comma: the next target.
        Offers(engine, "UPDATE orders o SET status = 'x', | FROM customers c",
            must: ["total"], mustNot: ["o.total", "c.full_name", "full_name"]);
        // The value side still sees every source.
        Offers(engine, "UPDATE orders o SET status = | FROM customers c", must: ["c.full_name", "o.total"], mustNot: []);
    }

    [Theory]
    [InlineData("pg")]
    [InlineData("oracle")]
    public void Merge_update_set_target_offers_only_the_merge_targets_columns(string engine)
    {
        var sql = engine == "oracle"
            ? "MERGE INTO orders o USING customers c ON (c.customer_id = o.customer_id) WHEN MATCHED THEN UPDATE SET |"
            : "MERGE INTO orders o USING customers c ON c.customer_id = o.customer_id WHEN MATCHED THEN UPDATE SET |";
        var n = (string s) => engine == "oracle" ? s.ToUpperInvariant() : s;
        Offers(engine, sql, must: [n("status"), n("total")], mustNot: ["c." + n("full_name"), n("full_name"), "o." + n("status")]);
    }

    [Theory]
    [InlineData("pg")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public void On_conflict_do_update_set_target_offers_the_insert_targets_columns(string engine) =>
        Offers(engine, "INSERT INTO orders AS o (id) VALUES (1) ON CONFLICT (id) DO UPDATE SET |", must: ["status", "total"], mustNot: ["o.status"]);

    [Fact]
    public void Mysql_single_table_update_sets_bare_columns_and_a_multi_table_update_offers_every_tables_columns()
    {
        Offers("mysql", "UPDATE orders o SET |", must: ["status", "total"], mustNot: ["o.status"]);
        Offers("mysql", "INSERT INTO orders (id) VALUES (1) ON DUPLICATE KEY UPDATE |", must: ["status", "total"], mustNot: []);
        // UPDATE a JOIN b … SET a.x = …, b.y = …: both tables can be set, qualified.
        Offers("mysql", "UPDATE orders o JOIN customers c ON c.customer_id = o.customer_id SET |", must: ["o.status", "c.full_name"], mustNot: []);
    }

    [Fact]
    public void Tsql_update_from_set_target_offers_only_the_targets_columns()
    {
        var snap = new SchemaSnapshot
        {
            ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Fks = [],
            Objects =
            [
                new SnapObject { Id = 1, Schema = "dbo", Name = "Orders", Kind = ObjectKind.Table, Columns = [C("OrderId"), C("CustomerId"), C("Status", "nvarchar(20)")] },
                new SnapObject { Id = 2, Schema = "dbo", Name = "Customers", Kind = ObjectKind.Table, Columns = [C("CustomerId"), C("FullName", "nvarchar(100)")] },
            ],
        };
        var labels = Labels("tsql", "UPDATE o SET | FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId", snap);
        Assert.Contains(labels, l => l is "Status" or "o.Status");
        Assert.DoesNotContain(labels, l => l is "c.FullName" or "FullName");
    }

    // ------------------------------------------------------------------------------------------- M3: the 500 cap

    [Fact]
    public void The_cap_keeps_a_prefix_match_that_sorts_after_many_substring_matches()
    {
        // 600 substring matches whose sort text comes first, then the one name that starts with the word.
        var items = Enumerable.Range(0, 600)
            .Select(i => new CompletionItem { Label = $"dbo.AOrders{i:D3}", Kind = CompletionKind.Table, SortText = $"1_a{i:D3}" })
            .Append(new CompletionItem { Label = "dbo.Orders", Kind = CompletionKind.Table, SortText = "1_z" })
            .Append(new CompletionItem { Label = "dbo.OpenRequests", Kind = CompletionKind.Table, SortText = "1_zz" }) // camel hump "OR"
            .ToArray();
        var text = "SELECT * FROM Orders";
        var kept = CompletionFilter.Apply(items, text, text.Length);
        Assert.Equal(CompletionFilter.MaxItems, kept.Length);
        Assert.Contains(kept, i => i.Label == "dbo.Orders");
        Assert.Equal("dbo.Orders", kept[0].Label); // prefix first, then the rest in sort order
        var hump = CompletionFilter.Apply(items, "SELECT * FROM OR", "SELECT * FROM OR".Length);
        Assert.Contains(hump, i => i.Label == "dbo.OpenRequests");
    }

    // ------------------------------------------------------------------------------------------- M4: deferred docs

    [Fact]
    public void Documentation_is_built_only_for_the_items_the_answer_keeps()
    {
        var built = 0;
        var items = Enumerable.Range(0, 2000)
            .Select(i => DeferredDocs.Defer(new CompletionItem { Label = $"t{i:D4}", Kind = CompletionKind.Table, SortText = $"2{i:D4}" }, () => { built++; return "cols"; }))
            .ToArray();
        var kept = CompletionFilter.Apply(items, "SELECT * FROM t", "SELECT * FROM t".Length);
        Assert.Equal(CompletionFilter.MaxItems, kept.Length);
        Assert.Equal(CompletionFilter.MaxItems, built);
        Assert.All(kept, i => Assert.Equal("cols", i.Documentation));
    }

    [Fact]
    public void A_tsql_table_kept_by_the_filter_still_shows_its_columns()
    {
        var cols = Enumerable.Range(0, 3).Select(c => new SnapColumn { Name = $"col{c}", Type = "int", Ordinal = c + 1 }).ToArray();
        var snap = new SchemaSnapshot
        {
            ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Fks = [],
            Objects = [.. Enumerable.Range(0, 1200).Select(n => new SnapObject { Id = n + 1, Schema = "dbo", Name = $"Table{n:D4}", Kind = ObjectKind.Table, Columns = cols })],
        };
        const string text = "SELECT * FROM Table01";
        var raw = new TSqlLanguageService().Complete(new CompleteParams { DocId = "m4", Text = text, Offset = text.Length, Dialect = Dialect.Tsql, ProfileId = "p", Database = "shop" },
            new FakeSchema(snap, "shop"), CancellationToken.None);
        Assert.All(raw.Where(i => i.Kind == CompletionKind.Table), i => Assert.Null(i.Documentation)); // nothing built before the filter
        var kept = CompletionFilter.Apply(raw, text, text.Length);
        var t = Assert.Single(kept, i => i.Label == "Table0123");
        Assert.Contains("col0", t.Documentation);
    }

    // ------------------------------------------------------------------------------------------- M13: rules caches

    [Fact]
    public void Typing_a_schema_name_letter_by_letter_keeps_a_bounded_number_of_rules()
    {
        var oracle = new OracleLanguageService();
        var duck = new DuckDbLanguageService();
        var mysql = new MySqlLanguageService();
        var name = "";
        foreach (var ch in "HUMAN_RESOURCES_ARCHIVE_2026_SCHEMA_WITH_A_LONG_NAME")
        {
            name += ch;
            var o = $"ALTER SESSION SET CURRENT_SCHEMA = {name};\nSELECT * FROM |";
            var at = o.IndexOf('|');
            oracle.Complete(new CompleteParams { DocId = "m13o", Text = o.Remove(at, 1), Offset = at, Dialect = Dialect.Oracle, ProfileId = "p", Database = "shop", Schema = name + "_LOGIN" }, new FakeSchema(Shop("oracle"), "shop"), CancellationToken.None);
            var d = $"USE shop.{name.ToLowerInvariant()};\nSELECT * FROM |";
            at = d.IndexOf('|');
            duck.Complete(new CompleteParams { DocId = "m13d", Text = d.Remove(at, 1), Offset = at, Dialect = Dialect.DuckDb, ProfileId = "p", Database = "shop" }, new FakeSchema(Shop("duckdb"), "shop"), CancellationToken.None);
            var m = $"USE {name.ToLowerInvariant()};\nSELECT * FROM |";
            at = m.IndexOf('|');
            mysql.Complete(new CompleteParams { DocId = "m13m", Text = m.Remove(at, 1), Offset = at, Dialect = Dialect.MySql, ProfileId = "p", Database = "shop" }, new FakeSchema(Shop("mysql"), "shop"), CancellationToken.None);
        }
        Assert.InRange(OracleCompletion.CachedRules, 1, RulesCache.DefaultCapacity);
        Assert.InRange(DuckDbLanguageService.CachedRules, 1, RulesCache.DefaultCapacity);
        Assert.InRange(MySqlCompletion.CachedRules, 1, RulesCache.DefaultCapacity);
    }

    [Fact]
    public void A_rules_cache_keeps_its_recently_used_entries_and_drops_the_oldest()
    {
        var made = 0;
        var cache = new RulesCache<string>(k => { made++; return PgCompletionRules.Postgres; }, StringComparer.Ordinal, capacity: 3);
        cache.Get("a"); cache.Get("b"); cache.Get("c");
        cache.Get("a"); // a is recent again
        cache.Get("d"); // b goes
        Assert.Equal(3, cache.Count);
        Assert.Equal(4, made);
        cache.Get("a"); cache.Get("c"); cache.Get("d");
        Assert.Equal(4, made); // all three still cached
        cache.Get("b");
        Assert.Equal(5, made); // b was dropped
    }
}

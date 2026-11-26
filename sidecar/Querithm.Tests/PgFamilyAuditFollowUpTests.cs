using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Tests;

/// <summary>
/// Tier 2 of the PostgreSQL family, code audit 2026-11-25 follow-up: MySQL's multi-table <c>UPDATE a, b SET</c> (M8),
/// <c>STRAIGHT_JOIN |</c> and the UPDATE modifiers before the target (M9), DuckDB's case-insensitive default schema (M12),
/// <c>SET a=-|</c> (L6), <c>IS DISTINCT FROM</c> read as a FROM clause (L8), a comma inside <c>ARRAY[…]</c> (L3) and a
/// derived table inside parenthesized joins (L5).
/// </summary>
public class PgFamilyAuditFollowUpTests
{
    static SnapColumn C(string name) => new() { Name = name, Type = "integer" };

    static SchemaSnapshot Shop(string engine, string? schemaName = null)
    {
        var upper = engine == "oracle";
        string N(string s) => upper ? s.ToUpperInvariant() : s;
        var schema = schemaName ?? engine switch { "pg" => "public", "mysql" => "shop", "oracle" => "APP", _ => "main" };
        return new SchemaSnapshot
        {
            ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Fks = [],
            Objects =
            [
                new SnapObject { Id = 1, Schema = schema, Name = N("orders"), Kind = ObjectKind.Table, Columns = [C(N("id")), C(N("customer_id")), C(N("status")), C(N("total"))] },
                new SnapObject { Id = 2, Schema = schema, Name = N("customers"), Kind = ObjectKind.Table, Columns = [C(N("customer_id")), C(N("full_name"))] },
            ],
        };
    }

    static IDialectService Service(string engine) => engine switch
    {
        "pg" => new PgLanguageService(), "mysql" => new MySqlLanguageService(), "oracle" => new OracleLanguageService(),
        "sqlite" => new SqliteLanguageService(), _ => new DuckDbLanguageService(),
    };

    static Dialect DialectOf(string engine) => engine switch
    {
        "pg" => Dialect.Pg, "mysql" => Dialect.MySql, "oracle" => Dialect.Oracle, "sqlite" => Dialect.Sqlite, _ => Dialect.DuckDb,
    };

    static HashSet<string> Labels(string engine, string marked, SchemaSnapshot? snap = null, string? docId = null)
    {
        var at = marked.IndexOf('|');
        var items = Service(engine).Complete(new CompleteParams
        {
            DocId = docId ?? "audit-" + Guid.NewGuid(), Text = marked.Remove(at, 1), Offset = at, Dialect = DialectOf(engine), ProfileId = "p", Database = "shop",
            Schema = engine == "oracle" ? "APP" : null,
        }, new FakeSchema(snap ?? Shop(engine), "shop"), CancellationToken.None);
        return items.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);
    }

    static void Offers(string engine, string marked, string[] must, string[] mustNot, SchemaSnapshot? snap = null)
    {
        string N(string s) => engine == "oracle" ? s.ToUpperInvariant() : s;
        var labels = Labels(engine, marked, snap);
        var missing = must.Select(N).Where(m => !labels.Contains(m)).ToArray();
        var wrong = mustNot.Select(N).Where(labels.Contains).ToArray();
        Assert.True(missing.Length == 0 && wrong.Length == 0,
            $"{engine} `{marked}`: missing [{string.Join(", ", missing)}], should not offer [{string.Join(", ", wrong)}]; got [{string.Join(", ", labels.Take(40))}]");
    }

    public static TheoryData<string> Family => new() { "pg", "mysql", "oracle", "sqlite", "duckdb" };

    [Fact]
    public void A_multi_table_update_sees_every_table_of_its_list()
    {
        Offers("mysql", "UPDATE orders o, customers c SET c.|", ["customer_id", "full_name"], ["status"]);
        Offers("mysql", "UPDATE orders o, customers c SET o.status = c.|", ["full_name"], []);
        Offers("mysql", "UPDATE orders o, |", ["customers"], []);
        // Every table's columns in the SET list (MultiTableUpdate), qualified.
        Offers("mysql", "UPDATE orders o, customers c SET |", ["o.status", "c.full_name"], []);
    }

    [Theory]
    [InlineData("pg")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    [InlineData("oracle")]
    public void A_comma_after_an_update_target_elsewhere_is_no_table_list(string engine) =>
        // UPDATE a, b is MySQL's alone: a comma after the target is no FROM list.
        Offers(engine, "UPDATE orders o, |", [], ["customers"]);

    [Theory]
    [InlineData("mysql", "SELECT * FROM orders o STRAIGHT_JOIN |")]
    [InlineData("mysql", "UPDATE IGNORE |")]
    [InlineData("mysql", "UPDATE LOW_PRIORITY IGNORE |")]
    [InlineData("sqlite", "UPDATE OR REPLACE |")]
    [InlineData("sqlite", "UPDATE OR IGNORE |")]
    public void A_table_slot_after_a_join_word_or_an_update_modifier_offers_tables(string engine, string marked) =>
        Offers(engine, marked, ["orders", "customers"], []);

    [Fact]
    public void Update_modifiers_are_not_words_of_postgresql()
    {
        // A PostgreSQL table may be called ignore: UPDATE ignore | names it, the next word is no table.
        Offers("pg", "UPDATE ignore |", [], ["orders"]);
    }

    /// <summary>
    /// DuckDB caches its rules by schema case-insensitively: after <c>USE SALESM12</c> the rules' default schema is
    /// "SALESM12" while the catalog says "SalesM12", and an ordinal test qualified every table of it.
    /// </summary>
    [Fact]
    public void A_duckdb_default_schema_typed_in_another_case_still_leaves_its_tables_bare()
    {
        var snap = Shop("duckdb", "SalesM12");
        var labels = Labels("duckdb", "USE SALESM12;\nSELECT * FROM |", snap);
        Assert.Contains("orders", labels);
        Assert.DoesNotContain("SalesM12.orders", labels);
    }

    /// <summary>`=-` is one operator token: the value side of `SET a=-|` was read as the next SET target.</summary>
    [Theory]
    [InlineData("pg")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public void The_value_after_an_assignment_run_sees_every_source(string engine) =>
        Offers(engine, "UPDATE orders SET status=-| FROM customers c WHERE c.customer_id = orders.customer_id", ["c.full_name"], []);

    [Theory, MemberData(nameof(Family))]
    public void Is_distinct_from_leaves_the_set_list_open(string engine)
    {
        // The next SET target: the target's columns, no tables.
        Offers(engine, "UPDATE orders SET status = total IS DISTINCT FROM id, |", ["customer_id"], ["customers"]);
        Offers(engine, "UPDATE orders SET status = total IS NOT DISTINCT FROM id, |", ["customer_id"], ["customers"]);
        // And the compared name is no source: WHERE … IS DISTINCT FROM customers names a column, not a table.
        Offers(engine, "SELECT | FROM orders WHERE status IS DISTINCT FROM customers", ["status"], ["customers.full_name", "full_name"]);
    }

    [Fact]
    public void A_comma_inside_an_array_is_no_set_list_comma()
    {
        // The value side: every source (here customers through FROM), not the next target.
        Offers("pg", "UPDATE orders SET status = ARRAY[1, |] FROM customers c", ["c.full_name"], []);
        Offers("duckdb", "UPDATE orders SET status = [1, |] FROM customers c", ["c.full_name"], []);
    }

    /// <summary>
    /// `JOIN (b JOIN (SELECT …) d ON …)`: a derived table that is not LATERAL sees neither the items of its group nor those
    /// before the group — they are one FROM level (every engine but DuckDB rejects o.* there).
    /// </summary>
    [Theory]
    [InlineData("pg")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    [InlineData("oracle")]
    public void A_derived_table_inside_parenthesized_joins_does_not_see_the_items_before_the_group(string engine) =>
        Offers(engine, "SELECT * FROM orders o JOIN (customers c JOIN (SELECT | FROM customers x) d ON d.customer_id = c.customer_id) ON c.customer_id = o.customer_id",
            ["x.full_name"], ["o.status", "c.full_name"]);

    /// <summary>
    /// Code audit 2026-11-25 (L7): the document cache was keyed by the rules' Id, which holds the schema, so a script that
    /// switches schemas was tokenized and held once per schema it completed in.
    /// </summary>
    [Theory]
    [InlineData("mysql", "USE shop;\nSELECT * FROM orders WHERE |;\nUSE other;\nSELECT * FROM orders WHERE |;")]
    [InlineData("duckdb", "USE main;\nSELECT * FROM orders WHERE |;\nUSE other;\nSELECT * FROM orders WHERE |;")]
    [InlineData("oracle", "SELECT * FROM orders WHERE |;\nALTER SESSION SET CURRENT_SCHEMA = OTHER;\nSELECT * FROM orders WHERE |;")]
    public void A_script_that_switches_schemas_is_tokenized_once(string engine, string marked)
    {
        var docId = "l7-" + Guid.NewGuid();
        var first = marked.IndexOf('|');
        var second = marked.LastIndexOf('|');
        var text = marked.Replace("|", "");
        var service = Service(engine);
        foreach (var at in new[] { first, second - 1 })
            service.Complete(new CompleteParams
            {
                DocId = docId, Text = text, Offset = at, Dialect = DialectOf(engine), ProfileId = "p", Database = "shop", Schema = engine == "oracle" ? "APP" : null,
            }, new FakeSchema(Shop(engine), "shop"), CancellationToken.None);
        // At most one (another test may clear the bounded cache in between, never add a copy).
        Assert.True(Intelligence.PgSql.Completion.PgDocument.CachedCopies(docId) <= 1);
        service.Forget(docId);
    }

    /// <summary>Oracle's CROSS / OUTER APPLY (…) d is a derived table (L9): its columns through d, and it sees the items before it.</summary>
    [Theory]
    [InlineData("CROSS")]
    [InlineData("OUTER")]
    public void An_oracle_apply_derived_table_has_its_columns(string kind)
    {
        Offers("oracle", $"SELECT d.| FROM orders o {kind} APPLY (SELECT c.full_name FROM customers c WHERE c.customer_id = o.customer_id) d", ["full_name"], ["status"]);
        Offers("oracle", $"SELECT * FROM orders o {kind} APPLY (SELECT c.full_name FROM customers c WHERE c.customer_id = o.|) d", ["status"], []);
    }

    /// <summary>Fourth round (M9): an alias compares by the dialect's rules — `O.*` is `o` on MySQL, SQLite and DuckDB.</summary>
    [Theory]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public void A_star_through_an_alias_in_another_case_has_the_columns(string engine) =>
        Offers(engine, "WITH c AS (SELECT O.* FROM orders AS o) SELECT c.| FROM c", ["status", "total"], []);

    /// <summary>Fourth round (H5): chained casts and nested bodies are read in a loop or to a bound, not recursed per level.</summary>
    [Fact]
    public void Deep_casts_and_nested_bodies_complete_without_a_crash()
    {
        var casts = "WITH c AS (SELECT 1" + string.Concat(Enumerable.Repeat("::int", 20_000)) + " AS a) SELECT c.| FROM c";
        Assert.Contains("a", Labels("pg", casts));
        var bodies = new StringBuilder();
        for (var k = 0; k < 3_000; k++) bodies.Append($"DO $a{k}$ ");
        bodies.Append("CREATE TEMP TABLE zz (x int);");
        for (var k = 2_999; k >= 0; k--) bodies.Append($" $a{k}$;");
        var text = bodies + "\nSELECT | FROM orders";
        Assert.NotNull(Labels("pg", text));
    }

    [Fact]
    public void A_duckdb_derived_table_inside_parenthesized_joins_still_sees_the_items_before_it() =>
        Offers("duckdb", "SELECT * FROM orders o JOIN (customers c JOIN (SELECT | FROM customers x) d ON d.customer_id = c.customer_id) ON c.customer_id = o.customer_id",
            ["o.status"], []);
}

using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Tests;

/// <summary>
/// Tier 2 of the PostgreSQL family after the 2026-11-25 review of the completion audit's fixes: a derived table hides
/// only the FROM level it sits in (M1 had hidden every enclosing level too), DuckDB's derived tables are lateral without
/// the keyword, MySQL's <c>VALUES(col)</c> and <c>STRAIGHT_JOIN</c> inside UPDATE lists, and the INSERT / UPDATE
/// modifiers (MySQL IGNORE / LOW_PRIORITY, SQLite OR …) that hid the target table. What each engine accepts was checked
/// against the engines themselves (SQLite, DuckDB in process; PostgreSQL, MySQL and Oracle 23 on the fixture).
/// </summary>
public class PgFamilyScopeTests
{
    static SnapColumn C(string name) => new() { Name = name, Type = "integer" };

    static SchemaSnapshot Shop(string engine)
    {
        var upper = engine == "oracle";
        string N(string s) => upper ? s.ToUpperInvariant() : s;
        var schema = engine switch { "pg" => "public", "mysql" => "shop", "oracle" => "APP", _ => "main" };
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

    static HashSet<string> Labels(string engine, string marked, SchemaSnapshot? snap = null)
    {
        var at = marked.IndexOf('|');
        var items = Service(engine).Complete(new CompleteParams
        {
            DocId = "scope-" + Guid.NewGuid(), Text = marked.Remove(at, 1), Offset = at, Dialect = DialectOf(engine), ProfileId = "p", Database = "shop",
            Schema = engine == "oracle" ? "APP" : null,
        }, new FakeSchema(snap ?? Shop(engine), "shop"), CancellationToken.None);
        return items.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);
    }

    static void Offers(string engine, string marked, string[] must, string[] mustNot)
    {
        string N(string s) => engine == "oracle" ? s.ToUpperInvariant() : s;
        var labels = Labels(engine, marked);
        var missing = must.Select(N).Where(m => !labels.Contains(m)).ToArray();
        var wrong = mustNot.Select(N).Where(labels.Contains).ToArray();
        Assert.True(missing.Length == 0 && wrong.Length == 0,
            $"{engine} `{marked}`: missing [{string.Join(", ", missing)}], should not offer [{string.Join(", ", wrong)}]; got [{string.Join(", ", labels.Take(40))}]");
    }

    public static TheoryData<string> Family => new() { "pg", "mysql", "oracle", "sqlite", "duckdb" };
    public static TheoryData<string> Sealing => new() { "pg", "mysql", "oracle", "sqlite" };

    [Theory, MemberData(nameof(Family))]
    public void A_derived_table_inside_a_subquery_sees_the_enclosing_query(string engine) =>
        // Every engine of the family accepts c.customer_id here (an enclosing level, not a sibling of d).
        Offers(engine, "SELECT * FROM customers c WHERE EXISTS (SELECT 1 FROM (SELECT o.id FROM orders o WHERE o.customer_id = c.|) d)",
            ["customer_id", "full_name"], ["status"]);

    [Theory, MemberData(nameof(Family))]
    public void A_derived_table_inside_a_subquery_lists_the_enclosing_sources_too(string engine) =>
        Offers(engine, "SELECT * FROM customers c WHERE EXISTS (SELECT 1 FROM (SELECT | FROM orders o) d)",
            ["o.status", "c.full_name"], []);

    [Theory, MemberData(nameof(Sealing))]
    public void A_derived_table_still_hides_its_siblings(string engine) =>
        // PostgreSQL, MySQL, Oracle and SQLite reject o.id here ("invalid reference", ORA-00904, "no such column").
        Offers(engine, "SELECT * FROM orders o JOIN (SELECT | FROM customers) d ON 1 = 1", ["full_name"], ["o.status", "status", "o.id"]);

    [Theory]
    [InlineData("SELECT * FROM orders o, (SELECT o.| ) x")]
    [InlineData("SELECT * FROM orders o JOIN (SELECT o.| FROM customers) d ON 1 = 1")]
    public void Duckdb_derived_tables_are_lateral_without_the_keyword(string marked) =>
        Offers("duckdb", marked, ["status", "total"], []);

    [Fact]
    public void Mysql_values_of_a_column_inside_on_duplicate_key_update_keeps_the_set_slot() =>
        Offers("mysql", "INSERT INTO orders (id, status) VALUES (1, 'x') ON DUPLICATE KEY UPDATE status = VALUES(status), |",
            ["total", "customer_id"], ["orders.total"]);

    [Fact]
    public void Mysql_straight_join_makes_a_multi_table_update() =>
        Offers("mysql", "UPDATE orders o STRAIGHT_JOIN customers c ON c.customer_id = o.customer_id SET |", ["o.status", "c.full_name"], []);

    [Theory]
    [InlineData("mysql", "UPDATE IGNORE orders SET |")]
    [InlineData("mysql", "UPDATE LOW_PRIORITY orders SET |")]
    [InlineData("mysql", "UPDATE LOW_PRIORITY IGNORE orders SET total = 1, |")]
    [InlineData("mysql", "INSERT IGNORE INTO orders (id, status) VALUES (1, 'x') ON DUPLICATE KEY UPDATE |")]
    [InlineData("sqlite", "UPDATE OR IGNORE orders SET |")]
    [InlineData("sqlite", "UPDATE OR REPLACE orders SET total = 1, |")]
    [InlineData("sqlite", "INSERT OR IGNORE INTO orders (id) VALUES (1) ON CONFLICT (id) DO UPDATE SET |")]
    public void A_modifier_before_the_target_still_finds_the_set_targets(string engine, string marked) =>
        Offers(engine, marked, ["status", "customer_id"], ["orders.status", "IGNORE.status"]);

    [Theory]
    [InlineData("mysql", "INSERT IGNORE INTO orders (|")]
    [InlineData("mysql", "INSERT LOW_PRIORITY INTO orders (id, |")]
    [InlineData("mysql", "REPLACE INTO orders (|")]
    [InlineData("sqlite", "INSERT OR REPLACE INTO orders (|")]
    [InlineData("sqlite", "INSERT OR IGNORE INTO orders (id, |")]
    public void A_modifier_before_into_still_lists_the_target_columns(string engine, string marked) =>
        Offers(engine, marked, ["status", "total"], ["full_name"]);

    static string Chain(string head, Func<int, string> item, int n) =>
        string.Join("\n", Enumerable.Range(1, n).Select(item)) + "\n" + head;

    /// <summary>
    /// Gemini review 2026-11-25: the resolver's recursion guard answers nothing past a depth of 8. Its answer must not be
    /// kept for a shallower read of the same object in the same request. CTEs and derived tables were already safe (their
    /// memo is per asking scope, and every query builds its own); temp tables were cached by table alone, so the chain
    /// t12 → … → t3 left t3 empty for the direct read of t3 that followed.
    /// </summary>
    [Theory, MemberData(nameof(Family))]
    public void A_temp_table_cut_off_at_the_depth_limit_still_resolves_when_read_directly(string engine)
    {
        string N(string s) => engine == "oracle" ? s.ToUpperInvariant() : s;
        var create = engine switch { "oracle" => "CREATE GLOBAL TEMPORARY TABLE", "mysql" => "CREATE TEMPORARY TABLE", _ => "CREATE TEMP TABLE" };
        var script = Chain("SELECT | FROM t12 x, t3 y",
            i => i == 1 ? $"{create} t1 AS SELECT 1 AS a0, 2 AS a1;" : $"{create} t{i} AS SELECT * FROM t{i - 1};", 12);
        var labels = Labels(engine, script);
        Assert.Contains(N("y.a0"), labels);
    }

    [Theory, MemberData(nameof(Family))]
    public void A_cte_cut_off_at_the_depth_limit_still_resolves_when_read_directly(string engine)
    {
        string N(string s) => engine == "oracle" ? s.ToUpperInvariant() : s;
        var ctes = string.Join(",\n", Enumerable.Range(1, 12).Select(i => i == 1 ? "c1 AS (SELECT 1 AS a0, 2 AS a1)" : $"c{i} AS (SELECT * FROM c{i - 1})"));
        var labels = Labels(engine, $"WITH {ctes}\nSELECT | FROM c12 x, c3 y");
        Assert.Contains(N("y.a0"), labels);
    }

    [Theory]
    [InlineData("UPDATE ignore SET |")]
    [InlineData("UPDATE ignore i SET |")]
    public void Postgresql_reads_those_words_as_names(string marked)
    {
        // A PostgreSQL table may be called "ignore": only MySQL's and SQLite's rules skip the modifiers.
        var snap = Shop("pg");
        snap = snap with { Objects = [.. snap.Objects, new SnapObject { Id = 3, Schema = "public", Name = "ignore", Kind = ObjectKind.Table, Columns = [C("flag")] }] };
        Assert.Contains("flag", Labels("pg", marked, snap));
    }
}

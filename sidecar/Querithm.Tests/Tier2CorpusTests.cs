using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

/// <summary>
/// Tier-2 completion corpus (completion audit 2026-11-25, Phase 0 item 7): at least 40 snippets per engine, each with what
/// it must offer and what it must not, the audit's tier-2 repros among them. One shop schema per engine, spelled as its
/// catalog spells it. Findings still open (M10–M12, M14) are left out until their phase.
/// </summary>
public class Tier2CorpusTests
{
    public sealed record Case(string Id, string Sql, string[] Must, string[] MustNot);

    static readonly string[] AllEngines = ["tsql", "pg", "mysql", "oracle", "sqlite", "duckdb"];
    static readonly string[] PgFamily = ["pg", "mysql", "oracle", "sqlite", "duckdb"];

    static SnapColumn C(string name, string type = "integer") => new() { Name = name, Type = type };

    static SchemaSnapshot Shop(string engine)
    {
        var upper = engine == "oracle";
        string N(string s) => upper ? s.ToUpperInvariant() : s;
        var main = engine switch { "tsql" => "dbo", "pg" => "public", "mysql" => "shop", "oracle" => "APP", _ => "main" };
        var sales = N("sales");
        SnapObject T(long id, string schema, string name, params string[] cols) =>
            new() { Id = id, Schema = schema, Name = N(name), Kind = ObjectKind.Table, Columns = [.. cols.Select(c => C(N(c)))] };
        var objects = new List<SnapObject>
        {
            T(1, main, "orders", "id", "customer_id", "status", "total", "order_date"),
            T(2, main, "customers", "customer_id", "full_name", "email"),
            T(3, main, "order_lines", "id", "order_id", "product_id", "qty"),
            T(4, main, "products", "product_id", "title", "price"),
        };
        if (engine != "sqlite") objects.Add(T(5, sales, "regions", "region_id", "region_name"));
        return new SchemaSnapshot
        {
            ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Objects = [.. objects],
            Fks =
            [
                new SnapFk { Name = "fk_orders_customers", Parent = 1, Referenced = 2, Columns = [new FkColumnPair { Parent = N("customer_id"), Referenced = N("customer_id") }] },
                new SnapFk { Name = "fk_lines_orders", Parent = 3, Referenced = 1, Columns = [new FkColumnPair { Parent = N("order_id"), Referenced = N("id") }] },
            ],
        };
    }

    // ------------------------------------------------------------------------------------------- the cases

    /// <summary>Patterns every engine answers the same way (labels upper-cased for Oracle, which folds unquoted names).</summary>
    static IEnumerable<Case> Common(string engine)
    {
        var withSchemas = engine != "sqlite";
        yield return new("select-list single table", "SELECT | FROM orders", ["status", "total"], ["full_name"]);
        yield return new("alias dot", "SELECT o.| FROM orders o", ["id", "status", "order_date"], ["full_name"]);
        yield return new("alias dot with AS", "SELECT o.| FROM orders AS o", ["status"], ["full_name"]);
        yield return new("single alias qualifies", "SELECT | FROM orders o", ["o.status"], ["full_name"]);
        yield return new("join second alias", "SELECT c.| FROM orders o JOIN customers c ON c.customer_id = o.customer_id", ["full_name", "email"], ["status"]);
        yield return new("join both sources", "SELECT | FROM orders o JOIN customers c ON c.customer_id = o.customer_id", ["o.status", "c.full_name"], []);
        yield return new("comma join both sources", "SELECT | FROM orders o, customers c", ["o.total", "c.email"], []);
        yield return new("where alias dot", "SELECT * FROM orders o WHERE o.|", ["status", "total"], ["full_name"]);
        yield return new("where bare", "SELECT * FROM orders o WHERE |", ["o.status"], ["c.full_name"]);
        yield return new("where after AND", "SELECT * FROM orders o WHERE o.status = 'x' AND o.|", ["total"], ["full_name"]);
        yield return new("cursor before FROM", "SELECT o.|, o.status FROM orders o", ["total", "id"], ["full_name"]);
        yield return new("from lists tables", "SELECT * FROM |", ["orders", "customers", "order_lines"], ["status", "full_name"]);
        yield return new("join lists tables", "SELECT * FROM orders o JOIN |", ["customers", "products"], ["status"]);
        yield return new("on clause alias dot", "SELECT * FROM orders o JOIN customers c ON c.|", ["customer_id"], ["status"]);
        yield return new("left join where", "SELECT * FROM orders o LEFT JOIN customers c ON c.customer_id = o.customer_id WHERE c.|", ["full_name"], ["status"]);
        yield return new("three-way join", "SELECT l.| FROM orders o JOIN order_lines l ON l.order_id = o.id JOIN products p ON p.product_id = l.product_id", ["qty", "product_id"], ["title", "status"]);
        yield return new("group by", "SELECT o.status, count(*) FROM orders o GROUP BY o.|", ["status"], ["full_name"]);
        yield return new("order by", "SELECT * FROM orders o ORDER BY o.|", ["total", "order_date"], ["full_name"]);
        yield return new("unknown alias", "SELECT x.| FROM orders o", [], ["status", "o.status"]);
        yield return new("union operands are separate", "SELECT o.| FROM orders o UNION SELECT c.customer_id FROM customers c", ["status"], ["full_name"]);
        yield return new("cte columns", "WITH c AS (SELECT id, status FROM orders) SELECT c.| FROM c", ["id", "status"], ["total"]);
        yield return new("cte alias column", "WITH c AS (SELECT id AS order_ref FROM orders) SELECT c.| FROM c", ["order_ref"], ["id"]);
        yield return new("cte chain", "WITH a AS (SELECT id, status FROM orders), b AS (SELECT status FROM a) SELECT b.| FROM b", ["status"], ["id", "total"]);
        yield return new("cte in from", "WITH recent AS (SELECT id FROM orders) SELECT * FROM |", ["recent", "orders"], []);
        yield return new("derived table", "SELECT d.| FROM (SELECT id, total FROM orders) d", ["id", "total"], ["status"]);
        yield return new("derived alias column", "SELECT d.| FROM (SELECT total AS amount FROM orders) d", ["amount"], ["total"]);
        yield return new("subquery in IN", "SELECT * FROM orders o WHERE o.customer_id IN (SELECT c.| FROM customers c)", ["customer_id", "full_name"], ["status"]);
        yield return new("correlated subquery sees outer", "SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM customers c WHERE c.customer_id = o.|)", ["customer_id", "status"], ["full_name"]);
        yield return new("update set", "UPDATE orders SET |", ["status", "total"], ["full_name"]);
        yield return new("update set after comma", "UPDATE orders SET status = 'x', |", ["total"], ["full_name"]);
        yield return new("update where", "UPDATE orders SET status = 'x' WHERE |", ["status", "total"], ["full_name"]);
        yield return new("delete where", "DELETE FROM orders WHERE |", ["status"], ["full_name"]);
        yield return new("insert column list", "INSERT INTO orders (|", ["id", "status", "total"], ["full_name"]);
        yield return new("insert into lists tables", "INSERT INTO |", ["orders", "customers"], ["status"]);
        yield return new("update lists tables", "UPDATE |", ["orders", "products"], ["status"]);
        yield return new("statement after a finished one", "SELECT 1 FROM customers;\nSELECT o.| FROM orders o", ["status"], ["full_name"]);
        yield return new("second statement scope", "SELECT c.full_name FROM customers c;\nSELECT | FROM orders o", ["o.status"], ["c.full_name"]);
        yield return new("unclosed case (M2)", "SELECT CASE WHEN o.| FROM orders o", ["status"], ["full_name"]);
        // DuckDB runs `JOIN (SELECT o.x FROM customers) d` (its derived tables see the items before them without LATERAL,
        // checked on the engine 2026-11-25); every other engine rejects o there. No engine lets d see itself.
        yield return engine == "duckdb"
            ? new("derived table sees outer (DuckDB lateral)", "SELECT * FROM orders o JOIN (SELECT | FROM customers) d ON 1 = 1", ["customers.full_name", "o.status"], ["d.full_name"])
            : new("derived table does not see outer (M1)", "SELECT * FROM orders o JOIN (SELECT | FROM customers) d ON 1 = 1", ["full_name"], ["o.status"]);
        if (withSchemas)
        {
            yield return new("schema dot lists its tables", "SELECT * FROM sales.|", ["regions"], ["orders"]);
            yield return new("schema-qualified source", "SELECT r.| FROM sales.regions r", ["region_id", "region_name"], ["status"]);
        }
    }

    /// <summary>What each engine adds: its own syntax, and the audit's engine-specific repros.</summary>
    static IEnumerable<Case> Own(string engine)
    {
        switch (engine)
        {
            case "tsql":
                yield return new("SET NOCOUNT ON preamble (H4)", "CREATE PROCEDURE dbo.p AS\nSET NOCOUNT ON\nUPDATE orders SET total = 0 WHERE |", ["status"], ["full_name"]);
                yield return new("WITH … INSERT … SELECT (H5)", "WITH c AS (SELECT id, total AS amt FROM orders) INSERT INTO order_lines (id, qty) SELECT c.| FROM c", ["amt", "id"], ["status"]);
                yield return new("update from sets the target only (M9)", "UPDATE o SET | FROM orders o JOIN customers c ON c.customer_id = o.customer_id", ["status"], ["c.full_name", "full_name"]);
                yield return new("temp table columns", "CREATE TABLE #w (wid int, amount money);\nSELECT w.| FROM #w w", ["wid", "amount"], ["status"]);
                yield return new("table variable columns", "DECLARE @t TABLE (k int, v nvarchar(10));\nSELECT t.| FROM @t t", ["k", "v"], ["status"]);
                yield return new("top n select list", "SELECT TOP 10 | FROM orders", ["status"], ["full_name"]);
                yield return new("output inserted", "UPDATE orders SET status = 'x' OUTPUT inserted.| WHERE id = 1", ["status", "total"], ["full_name"]);
                yield return new("cross apply sees the left side", "SELECT * FROM orders o CROSS APPLY (SELECT TOP 1 l.qty FROM order_lines l WHERE l.order_id = o.|) x", ["id"], ["full_name"]);
                yield return new("over order by takes no select alias", "SELECT o.total AS amt, ROW_NUMBER() OVER (ORDER BY |) FROM orders o", ["o.total"], ["amt"]);
                yield return new("bracketed alias", "SELECT [o].| FROM orders [o]", ["status"], ["full_name"]);
                break;
            case "pg":
                yield return new("update from sets the target only (M9)", "UPDATE orders o SET | FROM customers c WHERE c.customer_id = o.customer_id", ["status", "total"], ["o.status", "c.full_name", "full_name"]);
                yield return new("update from value side", "UPDATE orders o SET status = | FROM customers c", ["c.full_name", "o.total"], []);
                yield return new("on conflict do update set", "INSERT INTO orders AS o (id) VALUES (1) ON CONFLICT (id) DO UPDATE SET |", ["status"], ["o.status"]);
                yield return new("returning", "UPDATE orders o SET status = 'x' RETURNING |", ["o.status"], []);
                yield return new("merge update set (M9)", "MERGE INTO orders o USING customers c ON c.customer_id = o.customer_id WHEN MATCHED THEN UPDATE SET |", ["status"], ["c.full_name"]);
                yield return new("lateral sees the left side", "SELECT * FROM orders o, LATERAL (SELECT l.qty FROM order_lines l WHERE l.order_id = o.|) x", ["id"], ["full_name"]);
                yield return new("cast type", "SELECT total::| FROM orders", ["integer", "numeric"], ["status"]);
                yield return new("values alias columns", "SELECT v.| FROM (VALUES (1, 'a')) AS v(n, label)", ["n", "label"], ["status"]);
                // Other dialects' clause words stay aliases here: start and qualify are names in PostgreSQL (H10).
                yield return new("start is an alias (H10)", "SELECT start.| FROM orders start", ["status"], ["full_name"]);
                yield return new("qualify is an alias (H10)", "SELECT | FROM orders qualify", ["qualify.status"], ["status"]);
                break;
            case "mysql":
                yield return new("single-table update sets bare (M9)", "UPDATE orders o SET |", ["status", "total"], ["o.status"]);
                yield return new("multi-table update sets both tables", "UPDATE orders o JOIN customers c ON c.customer_id = o.customer_id SET |", ["o.status", "c.full_name"], []);
                yield return new("on duplicate key update", "INSERT INTO orders (id) VALUES (1) ON DUPLICATE KEY UPDATE |", ["status", "total"], []);
                yield return new("backtick alias", "SELECT `o`.| FROM orders `o`", ["status"], ["full_name"]);
                yield return new("use switches the default schema", "USE sales;\nSELECT * FROM |", ["regions"], []);
                yield return new("other schema qualified from the default", "SELECT * FROM shop.|", ["orders"], ["regions"]);
                yield return new("limit after where", "SELECT * FROM orders o WHERE o.| LIMIT 10", ["status"], ["full_name"]);
                // Reserved words end the FROM item instead of naming it (H10).
                yield return new("index hint is no alias (H10)", "SELECT | FROM orders USE INDEX (ix_status) WHERE status = 'x'", ["status"], ["USE.status", "`USE`.status"]);
                yield return new("force index is no alias (H10)", "SELECT | FROM orders FORCE INDEX (ix_status)", ["status"], ["FORCE.status", "`FORCE`.status"]);
                yield return new("partition is no alias (H10)", "SELECT | FROM orders PARTITION (p0)", ["status", "id"], ["p0", "PARTITION.p0"]);
                yield return new("lock in share mode is no alias (H10)", "SELECT | FROM orders LOCK IN SHARE MODE", ["status"], ["LOCK.status", "`LOCK`.status"]);
                break;
            case "oracle":
                yield return new("merge update set (M9)", "MERGE INTO orders o USING customers c ON (c.customer_id = o.customer_id) WHEN MATCHED THEN UPDATE SET |", ["STATUS", "TOTAL"], ["C.FULL_NAME"]);
                yield return new("current schema switch", "ALTER SESSION SET CURRENT_SCHEMA = sales;\nSELECT * FROM |", ["REGIONS"], []);
                yield return new("fetch first", "SELECT o.| FROM orders o FETCH FIRST 10 ROWS ONLY", ["STATUS"], ["FULL_NAME"]);
                yield return new("quoted upper alias", "SELECT \"O\".| FROM orders \"O\"", ["STATUS"], ["FULL_NAME"]);
                yield return new("cursor query in a block", "BEGIN\n  FOR r IN (SELECT o.| FROM orders o) LOOP NULL; END LOOP;\nEND;", ["STATUS"], ["FULL_NAME"]);
                yield return new("nothing inside a q-quote", "SELECT q'[o.|]' FROM orders o", [], ["STATUS"]);
                // Clause words end the FROM item instead of naming it (H10): before, `FROM orders START WITH …` made START the
                // alias, and the list offered START.STATUS (ORA-00904 once accepted).
                yield return new("start with is no alias (H10)", "SELECT | FROM orders START WITH id = 1 CONNECT BY PRIOR id = customer_id", ["STATUS"], ["START.STATUS", "\"START\".STATUS"]);
                yield return new("connect by is no alias (H10)", "SELECT | FROM orders CONNECT BY PRIOR id = customer_id", ["STATUS"], ["CONNECT.STATUS", "\"CONNECT\".STATUS"]);
                yield return new("connect by where (H10)", "SELECT * FROM orders START WITH id = 1 CONNECT BY PRIOR id = | ", ["CUSTOMER_ID"], ["START.CUSTOMER_ID"]);
                yield return new("minus operands are separate (H10)", "SELECT | FROM orders MINUS SELECT customer_id FROM customers", ["STATUS"], ["FULL_NAME", "CUSTOMERS.FULL_NAME", "MINUS.STATUS"]);
                yield return new("minus second operand (H10)", "SELECT id FROM orders MINUS SELECT | FROM customers", ["FULL_NAME"], ["STATUS", "ORDERS.STATUS"]);
                yield return new("flashback as of is no alias (H10)", "SELECT | FROM orders AS OF TIMESTAMP SYSDATE - 1", ["STATUS"], ["OF.STATUS", "\"OF\".STATUS"]);
                yield return new("partition is no alias (H10)", "SELECT | FROM orders PARTITION (p1)", ["STATUS", "ID"], ["P1", "PARTITION.P1"]);
                yield return new("sample is no alias (H10)", "SELECT | FROM orders SAMPLE (10)", ["STATUS"], ["\"SAMPLE\".STATUS", "SAMPLE.STATUS"]);
                yield return new("hierarchy with an alias", "SELECT o.| FROM orders o START WITH o.id = 1 CONNECT BY PRIOR o.id = o.customer_id", ["STATUS"], ["FULL_NAME"]);
                break;
            case "sqlite":
                yield return new("update from sets the target only (M9)", "UPDATE orders AS o SET | FROM customers c WHERE c.customer_id = o.customer_id", ["status"], ["c.full_name", "o.status"]);
                yield return new("upsert do update set", "INSERT INTO orders (id) VALUES (1) ON CONFLICT (id) DO UPDATE SET |", ["status"], []);
                yield return new("returning", "DELETE FROM orders WHERE id = 1 RETURNING |", ["status"], ["full_name"]);
                yield return new("bracket alias", "SELECT [o].| FROM orders [o]", ["status"], ["full_name"]);
                yield return new("limit offset", "SELECT o.| FROM orders o LIMIT 5 OFFSET 2", ["status"], ["full_name"]);
                // INDEXED BY ends the FROM item instead of naming it (H10).
                yield return new("indexed by is no alias (H10)", "SELECT | FROM orders INDEXED BY ix_status WHERE status = 'x'", ["status"], ["INDEXED.status", "\"INDEXED\".status"]);
                yield return new("not indexed keeps the alias", "SELECT o.| FROM orders o NOT INDEXED", ["status"], ["full_name"]);
                break;
            case "duckdb":
                yield return new("update from sets the target only (M9)", "UPDATE orders o SET | FROM customers c WHERE c.customer_id = o.customer_id", ["status"], ["c.full_name", "o.status"]);
                yield return new("on conflict do update set", "INSERT INTO orders (id) VALUES (1) ON CONFLICT (id) DO UPDATE SET |", ["status"], []);
                yield return new("cast type", "SELECT total::| FROM orders", ["INTEGER", "DOUBLE"], ["status"]);
                yield return new("use switches the default schema", "USE shop.sales;\nSELECT * FROM |", ["regions"], []);
                yield return new("returning", "INSERT INTO orders (id) VALUES (1) RETURNING |", ["status"], []);
                // Reserved words and USING SAMPLE end the FROM item instead of naming it (H10).
                yield return new("qualify is no alias (H10)", "SELECT | FROM orders QUALIFY row_number() OVER () = 1", ["status"], ["QUALIFY.status", "\"QUALIFY\".status"]);
                yield return new("asof join keeps both sources (H10)", "SELECT | FROM orders ASOF JOIN customers c ON c.customer_id = orders.customer_id", ["orders.status", "c.full_name"], ["ASOF.status", "\"ASOF\".status"]);
                yield return new("semi join is no alias (H10)", "SELECT | FROM orders SEMI JOIN customers c ON c.customer_id = orders.customer_id", ["orders.status"], ["SEMI.status", "\"SEMI\".status"]);
                yield return new("using sample is no source (H10)", "SELECT | FROM orders USING SAMPLE 10%", ["status"], ["orders.status"]);
                yield return new("using sample method is no source (H10)", "SELECT | FROM orders USING SAMPLE reservoir(50 ROWS)", ["status"], ["orders.status"]);
                break;
        }
    }

    static string Up(string engine, string label) => engine == "oracle" ? label.ToUpperInvariant() : label;

    static IEnumerable<Case> CasesOf(string engine) =>
        Common(engine).Select(c => c with { Must = [.. c.Must.Select(m => Up(engine, m))], MustNot = [.. c.MustNot.Select(m => Up(engine, m))] }).Concat(Own(engine));

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var e in AllEngines)
            foreach (var c in CasesOf(e)) data.Add(e, c.Id);
        return data;
    }

    // ------------------------------------------------------------------------------------------- running them

    static IDialectService Service(string engine) => engine switch
    {
        "tsql" => new TSqlLanguageService(), "pg" => new PgLanguageService(), "mysql" => new MySqlLanguageService(),
        "oracle" => new OracleLanguageService(), "sqlite" => new SqliteLanguageService(), _ => new DuckDbLanguageService(),
    };

    static Dialect DialectOf(string engine) => engine switch
    {
        "tsql" => Dialect.Tsql, "pg" => Dialect.Pg, "mysql" => Dialect.MySql, "oracle" => Dialect.Oracle, "sqlite" => Dialect.Sqlite, _ => Dialect.DuckDb,
    };

    [Theory, MemberData(nameof(Cases))]
    public void Offers_what_it_must_and_nothing_it_must_not(string engine, string id)
    {
        var c = CasesOf(engine).Single(x => x.Id == id);
        var at = c.Sql.IndexOf('|');
        var items = Service(engine).Complete(new CompleteParams
        {
            DocId = $"corpus-{engine}-{Guid.NewGuid()}", Text = c.Sql.Remove(at, 1), Offset = at, Dialect = DialectOf(engine), ProfileId = "p", Database = "shop",
            Schema = engine == "oracle" ? "APP" : null,
        }, new FakeSchema(Shop(engine), "shop"), CancellationToken.None);
        var labels = items.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);
        var missing = c.Must.Where(m => !labels.Contains(m)).ToArray();
        var wrong = c.MustNot.Where(labels.Contains).ToArray();
        Assert.True(missing.Length == 0 && wrong.Length == 0,
            $"{engine} `{c.Sql}`: missing [{string.Join(", ", missing)}], should not offer [{string.Join(", ", wrong)}]; got [{string.Join(", ", labels.Take(40))}]");
    }

    [Fact]
    public void Every_engine_has_at_least_40_cases()
    {
        foreach (var e in AllEngines) Assert.True(CasesOf(e).Count() >= 40, $"{e}: {CasesOf(e).Count()} cases");
        Assert.All(AllEngines, e => Assert.Equal(CasesOf(e).Count(), CasesOf(e).Select(c => c.Id).Distinct().Count()));
        _ = PgFamily;
    }
}

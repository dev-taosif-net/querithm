using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;

namespace Querithm.Tests;

/// <summary>
/// PostgreSQL tier-2 completion (Phase A2, §9): a snippet corpus like the T-SQL one — CTE columns, LATERAL, derived tables,
/// DECLARE variables, RETURNS TABLE, temp tables, $tag$ quoting, :: casts, quoted mixed-case names — plus signature help.
/// </summary>
public class PgTier2CompletionTests
{
    static SnapColumn C(string name, string type, bool pk = false) => new() { Name = name, Type = type, Pk = pk };

    static readonly SchemaSnapshot Shop = new()
    {
        ProfileId = "p1", Database = "shop", Status = SnapshotStatus.Fresh, Fks = [],
        Objects =
        [
            new SnapObject { Id = 1, Schema = "public", Name = "orders", Kind = ObjectKind.Table,
                Columns = [C("id", "bigint", true), C("customer_id", "integer"), C("order_date", "timestamp with time zone"), C("total_amount", "numeric(18,2)"), C("status", "text"), C("order", "integer")] },
            new SnapObject { Id = 2, Schema = "public", Name = "customers", Kind = ObjectKind.Table,
                Columns = [C("customer_id", "integer", true), C("full_name", "text"), C("email", "text")] },
            new SnapObject { Id = 3, Schema = "sales", Name = "regions", Kind = ObjectKind.Table, Columns = [C("region_id", "integer"), C("name", "text")] },
            new SnapObject { Id = 4, Schema = "public", Name = "OrderLines", Kind = ObjectKind.Table, Columns = [C("id", "integer"), C("LineNo", "integer"), C("qty", "integer")] },
            new SnapObject { Id = 5, Schema = "public", Name = "orders_for", Kind = ObjectKind.Tvf, ReturnType = "SETOF record",
                Params = [new SnapParam { Name = "p_customer", Type = "integer", Ordinal = 1 }], Columns = [C("order_id", "bigint"), C("total", "numeric")] },
            new SnapObject { Id = 6, Schema = "public", Name = "add_order", Kind = ObjectKind.Proc,
                Params = [new SnapParam { Name = "p_customer", Type = "integer", Ordinal = 1 }, new SnapParam { Name = "p_total", Type = "numeric", Ordinal = 2, HasDefault = true },
                          new SnapParam { Name = "p_id", Type = "bigint", Ordinal = 3, Output = true }] },
            new SnapObject { Id = 7, Schema = "public", Name = "calc_discount", Kind = ObjectKind.Func, ReturnType = "numeric",
                Params = [new SnapParam { Name = "p_total", Type = "numeric", Ordinal = 1 }, new SnapParam { Name = "p_pct", Type = "integer", Ordinal = 2, HasDefault = true }] },
            new SnapObject { Id = 8, Schema = "public", Name = "calc_discount", Kind = ObjectKind.Func, ReturnType = "numeric",
                Params = [new SnapParam { Name = "p_total", Type = "numeric", Ordinal = 1 }] },
            new SnapObject { Id = 9, Schema = "public", Name = "mood", Kind = ObjectKind.Type },
            new SnapObject { Id = 10, Schema = "public", Name = "orders", Kind = ObjectKind.Trigger, ParentObjectId = 1 },
        ],
    };

    static readonly ISchemaSource Schema = new FakeSchema(Shop, "shop");
    static readonly ISchemaSource None = new FakeSchema(null);

    static (string Text, int Offset) Mark(string sql)
    {
        var i = sql.IndexOf('|');
        Assert.True(i >= 0, "test SQL needs a | cursor marker");
        return (sql.Remove(i, 1), i);
    }

    internal static CompletionItem[] Complete(string sql, ISchemaSource? schema = null)
    {
        var (text, offset) = Mark(sql);
        return new PgLanguageService().Complete(new CompleteParams { DocId = "pg2-" + Guid.NewGuid(), Text = text, Offset = offset, Dialect = Dialect.Pg, ProfileId = "p1", Database = "shop" }, schema ?? Schema);
    }

    static SignatureHelp Sig(string sql)
    {
        var (text, offset) = Mark(sql);
        return new PgLanguageService().Signature(new SignatureParams { DocId = "pg2s-" + Guid.NewGuid(), Text = text, Offset = offset, Dialect = Dialect.Pg, ProfileId = "p1", Database = "shop" }, Schema);
    }

    static string Labels(CompletionItem[] items) => string.Join(", ", items.Select(i => i.Label));

    const string Fn = "CREATE OR REPLACE FUNCTION public.f(p_id int, p_name text DEFAULT 'x') RETURNS int LANGUAGE plpgsql AS $$\n";

    /// <summary>(case, SQL with | at the cursor, labels that must be offered, labels that must not).</summary>
    public static TheoryData<string, string, string[], string[]> Corpus => new()
    {
        // ---- CTEs
        { "cte explicit columns", "WITH x(a, b) AS (SELECT id, status FROM orders) SELECT x.| FROM x", ["a", "b"], ["id", "status"] },
        { "cte inferred columns", "WITH r AS (SELECT o.id, o.total_amount AS amount FROM orders o) SELECT r.| FROM r", ["id", "amount"], ["status"] },
        { "cte bare alias", "WITH r AS (SELECT o.total_amount amount, count(*) n FROM orders o GROUP BY 1) SELECT r.| FROM r", ["amount", "n"], [] },
        { "cte star expansion", "WITH x AS (SELECT * FROM customers) SELECT x.| FROM x", ["customer_id", "full_name", "email"], [] },
        { "cte qualified star", "WITH x AS (SELECT c.*, 1 AS one FROM customers c) SELECT x.| FROM x", ["full_name", "one"], [] },
        { "second cte reads the first", "WITH a AS (SELECT id, status FROM orders), b AS (SELECT a.status AS s FROM a) SELECT b.| FROM b", ["s"], ["status"] },
        { "recursive cte", "WITH RECURSIVE t(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM t WHERE n < 5) SELECT t.| FROM t", ["n"], [] },
        { "materialized cte", "WITH m AS MATERIALIZED (SELECT customer_id FROM customers) SELECT m.| FROM m", ["customer_id"], [] },
        { "cte names in FROM", "WITH recent AS (SELECT id FROM orders) SELECT * FROM |", ["recent", "orders", "customers", "sales.regions"], [] },
        { "data-modifying cte returning", "WITH gone AS (DELETE FROM orders WHERE status = 'x' RETURNING id, status) SELECT g.| FROM gone g", ["id", "status"], ["customer_id"] },
        // ---- derived tables and LATERAL
        { "derived table", "SELECT d.| FROM (SELECT customer_id, count(*) AS n FROM orders GROUP BY customer_id) d", ["customer_id", "n"], ["status"] },
        { "derived column alias list", "SELECT d.| FROM (SELECT 1, 'x') AS d(a, b)", ["a", "b"], [] },
        { "values list", "SELECT v.| FROM (VALUES (1, 'a'), (2, 'b')) AS v(num, letter)", ["num", "letter"], [] },
        { "lateral subquery columns", "SELECT l.| FROM customers c CROSS JOIN LATERAL (SELECT o.id AS order_id FROM orders o WHERE o.customer_id = c.customer_id) l", ["order_id"], ["full_name"] },
        { "lateral sees the outer alias", "SELECT * FROM customers c, LATERAL (SELECT 1 FROM orders o WHERE o.customer_id = c.|) x", ["full_name", "email"], ["status"] },
        { "sibling subquery alias is not visible", "SELECT * FROM (SELECT 1 FROM orders o) a, (SELECT o.| FROM customers c) b", [], ["status"] },
        // ---- joins, qualifiers, schemas
        { "join alias", "SELECT c.| FROM orders o JOIN customers c ON c.customer_id = o.customer_id", ["full_name", "email"], ["status"] },
        { "table name as qualifier", "SELECT orders.| FROM orders", ["status", "total_amount"], [] },
        { "schema-qualified source", "SELECT * FROM sales.regions r WHERE r.|", ["region_id", "name"], [] },
        { "schema.table.column", "SELECT sales.regions.| FROM sales.regions", ["region_id", "name"], [] },
        { "schema qualifier lists its objects", "SELECT * FROM sales.|", ["regions"], ["orders"] },
        { "unqualified columns of two sources are alias-prefixed", "SELECT | FROM orders o JOIN customers c ON true", ["o.status", "c.full_name"], ["status"] },
        { "case expression keeps the scope", "SELECT CASE WHEN o.id > 1 THEN o.| ELSE 0 END FROM orders o", ["status"], [] },
        // ---- quoting and casts
        { "quoted mixed-case table", "SELECT ol.| FROM \"OrderLines\" ol", ["LineNo", "qty"], [] },
        { "quoted alias", "SELECT \"O\".| FROM orders \"O\"", ["status"], [] },
        { "cast does not hide the next column", "SELECT o.id::text, o.| FROM orders o", ["status"], [] },
        { "cast names the cte column", "WITH x AS (SELECT o.id::text AS sid, o.status::varchar(10) FROM orders o) SELECT x.| FROM x", ["sid", "status"], [] },
        // ---- DML
        { "insert column list", "INSERT INTO orders (|", ["id", "status", "order"], [] },
        { "insert column list skips listed columns", "INSERT INTO orders (id, status, |", ["customer_id"], ["id", "status"] },
        { "update set", "UPDATE orders SET |", ["status", "total_amount"], [] },
        { "insert returning", "INSERT INTO orders (id) VALUES (1) RETURNING |", ["id", "status"], [] },
        { "delete returning alias", "DELETE FROM orders o WHERE o.id = 1 RETURNING o.|", ["status"], [] },
        { "update from", "UPDATE orders o SET status = 'x' FROM customers c WHERE c.|", ["full_name"], ["status"] },
        { "merge using", "MERGE INTO orders t USING customers s ON s.|", ["full_name"], [] },
        // ---- functions in FROM
        { "set-returning function columns", "SELECT f.| FROM public.orders_for(1) f", ["order_id", "total"], [] },
        { "column definition list", "SELECT x.| FROM json_to_record('{}') AS x(a int, b text)", ["a", "b"], [] },
        // ---- PL/pgSQL
        { "declare variables", Fn + "DECLARE v_total numeric; v_n int; BEGIN v_| END $$;", ["v_total", "v_n", "p_id", "p_name"], [] },
        { "function parameters", Fn + "BEGIN RETURN p_|; END $$;", ["p_id", "p_name"], [] },
        { "unnamed parameters", "CREATE FUNCTION g(int, text) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN |; END $$;", ["$1", "$2"], [] },
        { "returns table columns are variables", "CREATE FUNCTION g() RETURNS TABLE (out_id int, out_label text) LANGUAGE plpgsql AS $$ BEGIN out_| END $$;", ["out_id", "out_label"], [] },
        { "for loop record fields", Fn + "DECLARE rec record; BEGIN FOR rec IN SELECT o.id, o.status AS st FROM orders o LOOP RAISE NOTICE '%', rec.|; END LOOP; END $$;", ["id", "st"], ["customer_id"] },
        { "rowtype fields", Fn + "DECLARE r orders%ROWTYPE; BEGIN r.| END $$;", ["status", "total_amount"], [] },
        { "table row type", Fn + "DECLARE r public.customers; BEGIN r.| END $$;", ["full_name"], [] },
        { "select into a record", Fn + "DECLARE rec record; BEGIN SELECT c.customer_id, c.email INTO rec FROM customers c LIMIT 1; RETURN rec.|; END $$;", ["customer_id", "email"], ["full_name"] },
        { "variables in a where clause", Fn + "DECLARE v_limit int := 10; BEGIN PERFORM 1 FROM orders o WHERE o.id < | ; END $$;", ["v_limit", "p_id", "status"], [] },
        { "trigger function variables", "CREATE FUNCTION t() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF tg_| THEN RETURN NEW; END IF; END $$;", ["tg_op", "new", "old"], [] },
        { "do block", "DO $$ DECLARE v_count bigint; BEGIN SELECT count(*) INTO v_| FROM orders; END $$;", ["v_count"], [] },
        { "named dollar tag", "CREATE FUNCTION h() RETURNS int LANGUAGE plpgsql AS $fn$ DECLARE v_x int; BEGIN RETURN v|; END $fn$;", ["v_x"], [] },
        { "nested dollar quote in the body", "CREATE FUNCTION h() RETURNS int LANGUAGE plpgsql AS $fn$ DECLARE v_x text := $q$ a; b $q$; v_y int; BEGIN RETURN v|; END $fn$;", ["v_x", "v_y"], [] },
        { "variables declared later are not offered", Fn + "DECLARE a int; BEGIN | ; DECLARE later int; BEGIN NULL; END; END $$;", ["a"], ["later"] },
        { "sql function parameters", "CREATE FUNCTION s(p_customer int) RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM orders o WHERE o.customer_id = p_| $$;", ["p_customer"], [] },
        // ---- temp tables
        { "temp table with columns", "CREATE TEMP TABLE tmp_x (a int, b text NOT NULL); SELECT t.| FROM tmp_x t", ["a", "b"], [] },
        { "temp table as select", "CREATE TEMPORARY TABLE tmp_y AS SELECT id, status FROM orders; SELECT y.| FROM tmp_y y", ["id", "status"], ["customer_id"] },
        { "temp table like", "CREATE TEMP TABLE tmp_c (LIKE customers, extra int); SELECT z.| FROM tmp_c z", ["full_name", "extra"], [] },
        { "temp table in FROM", "CREATE TEMP TABLE tmp_x (a int); SELECT * FROM |", ["tmp_x"], [] },
        { "temp table created in a body", Fn + "BEGIN CREATE TEMP TABLE tmp_b (k int, v text) ON COMMIT DROP; INSERT INTO tmp_b (|", ["k", "v"], [] },
        // ---- calls
        { "call named arguments", "CALL public.add_order(|", ["p_customer =>", "p_total =>", "p_id =>"], [] },
        { "call skips named arguments already given", "CALL add_order(p_customer => 1, |", ["p_total =>"], ["p_customer =>"] },
        { "function named arguments", "SELECT calc_discount(|", ["p_total =>", "p_pct =>"], [] },
        // ---- nothing to say
        { "inside a string", "SELECT 'o.|' FROM orders o", [], ["status"] },
        { "inside a comment", "SELECT 1 -- o.| \nFROM orders o", [], ["status"] },
        { "alias being named", "SELECT o.id AS | FROM orders o", [], ["status"] },
        { "declare name slot", Fn + "DECLARE | END $$;", [], ["p_id"] },
    };

    [Theory, MemberData(nameof(Corpus))]
    public void Snippet(string name, string sql, string[] expected, string[] absent)
    {
        var items = Complete(sql);
        foreach (var e in expected) Assert.True(items.Any(i => i.Label == e), $"{name}: expected '{e}' in [{Labels(items)}]");
        foreach (var a in absent) Assert.False(items.Any(i => i.Label == a), $"{name}: did not expect '{a}' in [{Labels(items)}]");
    }

    [Theory]
    // Regression: a nested projection lost the enclosing statement's CTEs.
    [InlineData("WITH a AS (SELECT id, status FROM orders), b AS (SELECT * FROM a) SELECT b.| FROM b", new[] { "id", "status" }, new[] { "email" })]
    [InlineData("WITH a AS (SELECT id, status FROM orders) SELECT d.| FROM (SELECT * FROM a) d", new[] { "id", "status" }, new[] { "email" })]
    [InlineData("WITH a AS (SELECT id FROM orders), b AS (WITH a AS (SELECT email FROM customers) SELECT * FROM a) SELECT b.| FROM b", new[] { "email" }, new[] { "id" })]
    [InlineData("WITH RECURSIVE a AS (SELECT id FROM orders UNION ALL SELECT * FROM a) SELECT a.| FROM a", new[] { "id" }, new[] { "email" })]
    // Regression: UNION / INTERSECT / EXCEPT operands shared their aliases.
    [InlineData("SELECT x.id FROM orders x UNION ALL SELECT x.| FROM customers x", new[] { "full_name", "email" }, new[] { "status" })]
    [InlineData("SELECT x.| FROM orders x EXCEPT SELECT x.customer_id FROM customers x", new[] { "status" }, new[] { "email" })]
    [InlineData("WITH a AS (SELECT id FROM orders) SELECT 1 FROM customers c UNION SELECT a.| FROM a", new[] { "id" }, new[] { "email" })]
    [InlineData("SELECT * FROM (SELECT x.id FROM orders x INTERSECT SELECT x.| FROM customers x) t", new[] { "email" }, new[] { "status" })]
    public void Nested_cte_scope_and_set_operation_operands(string sql, string[] expected, string[] absent)
    {
        var labels = Complete(sql).Select(i => i.Label).ToHashSet();
        foreach (var e in expected) Assert.Contains(e, labels);
        foreach (var a in absent) Assert.DoesNotContain(a, labels);
    }

    [Fact]
    public void The_corpus_has_at_least_forty_snippets() => Assert.True(Corpus.Count >= 40, $"{Corpus.Count} snippets");

    [Fact]
    public void Columns_carry_types_and_quoted_insert_text()
    {
        var items = Complete("SELECT ol.| FROM \"OrderLines\" ol");
        var lineNo = items.Single(i => i.Label == "LineNo");
        Assert.Equal((CompletionKind.Column, "integer", "\"LineNo\""), (lineNo.Kind, lineNo.Detail, lineNo.InsertText));
        Assert.Null(items.Single(i => i.Label == "qty").InsertText);
        // A reserved word needs quotes too.
        Assert.Equal("\"order\"", Complete("SELECT o.| FROM orders o").Single(i => i.Label == "order").InsertText);
        var cte = Complete("WITH x AS (SELECT o.id::text AS sid, count(*) AS n, 1.5 AS r, 'a' AS t, o.total_amount FROM orders o GROUP BY 1, 5) SELECT x.| FROM x");
        Assert.Equal(["text", "bigint", "numeric", "text", "numeric(18,2)"], new[] { "sid", "n", "r", "t", "total_amount" }.Select(n => cte.Single(i => i.Label == n).Detail));
    }

    [Fact]
    public void Variables_and_parameters_have_kinds_and_types()
    {
        var items = Complete(Fn + "DECLARE v_total numeric(10,2) := 0; v_status orders.status%TYPE; c CURSOR FOR SELECT 1; BEGIN | END $$;");
        Assert.Equal((CompletionKind.Variable, "numeric(10, 2)"), (items.Single(i => i.Label == "v_total").Kind, items.Single(i => i.Label == "v_total").Detail));
        Assert.Equal("text", items.Single(i => i.Label == "v_status").Detail);
        Assert.Equal(CompletionKind.Cursor, items.Single(i => i.Label == "c").Kind);
        Assert.Equal((CompletionKind.Parameter, "int"), (items.Single(i => i.Label == "p_id").Kind, items.Single(i => i.Label == "p_id").Detail));
    }

    [Fact]
    public void Declare_type_slot_offers_types()
    {
        var items = Complete(Fn + "DECLARE v_x |");
        Assert.Contains(items, i => i.Label == "integer" && i.Kind == CompletionKind.Type);
        Assert.Contains(items, i => i.Label == "mood" && i.Kind == CompletionKind.Type);
        Assert.Contains(Complete("SELECT 1::|"), i => i.Label == "jsonb");
        Assert.Contains(Complete("SELECT CAST(1 AS |"), i => i.Label == "numeric");
    }

    [Fact]
    public void Triggers_never_stand_in_for_their_table()
    {
        // The snapshot has a trigger named like its table; columns still come from the table.
        Assert.Contains(Complete("SELECT o.| FROM orders o"), i => i.Label == "status");
        Assert.DoesNotContain(Complete("SELECT * FROM |"), i => i.Kind != CompletionKind.Table && i.Label == "orders");
    }

    [Fact]
    public void Without_a_snapshot_the_script_still_answers()
    {
        var items = Complete("WITH x(a) AS (SELECT 1) SELECT x.| FROM x", None);
        Assert.Contains(items, i => i.Label == "a");
        Assert.Contains(Complete(Fn + "DECLARE v int; BEGIN | END $$;", None), i => i.Label == "v");
        Assert.Empty(Complete("SELECT o.| FROM orders o", None)); // unknown table: tier 1 decides
    }

    [Fact]
    public void An_unterminated_dollar_body_runs_to_the_end_and_completion_still_answers()
    {
        // While typing, the closing $$ is not there yet: the rest of the document is the body.
        var items = Complete(Fn + "DECLARE v_total numeric; rec record;\nBEGIN\n  FOR rec IN SELECT o.id, o.status FROM orders o LOOP\n    v_total := v_| ");
        Assert.Contains(items, i => i.Label == "v_total");
        Assert.Contains(Complete(Fn + "DECLARE rec record; BEGIN FOR rec IN SELECT o.id, o.status FROM orders o LOOP rec.|"), i => i.Label == "status");
        // Right after the opening tag there is nothing to complete yet; it must not throw.
        Assert.Empty(Complete("DO $$|"));
    }

    [Fact]
    public void Malformed_text_never_throws()
    {
        foreach (var sql in new[] { "|", "(((|", "SELECT (SELECT (|", "WITH x AS (|", "FROM |", "INSERT INTO (|", "CALL (|", "$$ $$ $$|", "\"unterminated|", "'x|", "/* open|", Fn + "DECLARE |" })
        {
            var ex = Record.Exception(() => Complete(sql));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Signature_help_shows_overloads_and_the_active_parameter()
    {
        var help = Sig("SELECT calc_discount(100, |)");
        Assert.Equal(2, help.Signatures.Length);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Contains("p_pct integer DEFAULT", help.Signatures[help.ActiveSignature].Label);
        var named = Sig("CALL add_order(p_total => |)");
        Assert.Equal(1, named.ActiveParameter);
        Assert.Equal("procedure", Assert.Single(named.Signatures).Documentation);
        Assert.Equal(3, named.Signatures[0].Parameters.Length); // a procedure takes its OUT argument too
        Assert.Empty(Sig("SELECT nope(|)").Signatures);
        Assert.Empty(Sig("SELECT 'calc_discount(|'").Signatures);
    }

    [Fact]
    public void A_database_qualified_call_resolves_by_its_schema_and_name()
    {
        // Regression (D4 cross-check): db.schema.fn( found nothing once three-part chains went to the Oracle-only
        // pkg.member lookup; PostgreSQL and DuckDB name the catalog first and must keep signature help.
        Assert.Equal(2, Sig("SELECT shop.public.calc_discount(100, |)").Signatures.Length);
        var items = Complete("SELECT shop.public.calc_discount(|").Select(i => i.Label).ToList();
        Assert.Contains("p_total =>", items);
    }
}

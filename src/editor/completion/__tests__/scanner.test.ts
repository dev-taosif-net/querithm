import { describe, it, expect } from "vitest";
import { ctxOf } from "./fixture";
import { tokenize } from "../scanner";

const names = (c: ReturnType<typeof ctxOf>) => c.tablesInScope.map((t) => (t.alias ? `${t.name} ${t.alias}` : t.name));

describe("scanner: clauses (T-SQL)", () => {
  it("qualifier before FROM resolves alias from the whole statement", () => {
    const c = ctxOf("SELECT o.| FROM dbo.Orders o");
    expect(c.clause).toBe("select");
    expect(c.qualifier).toBe("o");
    expect(c.tablesInScope).toHaveLength(1);
    expect(c.tablesInScope[0]).toMatchObject({ name: "Orders", schema: "dbo", alias: "o" });
  });
  it("aliases with and without AS", () => {
    const c = ctxOf("SELECT | FROM dbo.Orders AS o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId");
    expect(c.clause).toBe("select");
    expect(names(c)).toEqual(["Orders o", "Customers c"]);
  });
  it("from", () => expect(ctxOf("SELECT * FROM |").clause).toBe("from"));
  it("join", () => expect(ctxOf("SELECT * FROM dbo.Orders o JOIN |").clause).toBe("join"));
  it("left outer join", () => expect(ctxOf("SELECT * FROM dbo.Orders o LEFT OUTER JOIN |").clause).toBe("join"));
  it("on", () => expect(ctxOf("SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON |").clause).toBe("on"));
  it("where", () => expect(ctxOf("SELECT * FROM dbo.Orders WHERE |").clause).toBe("where"));
  it("group by", () => expect(ctxOf("SELECT Status, COUNT(*) FROM dbo.Orders GROUP BY |").clause).toBe("groupBy"));
  it("having", () => expect(ctxOf("SELECT Status FROM dbo.Orders GROUP BY Status HAVING |").clause).toBe("having"));
  it("order by", () => expect(ctxOf("SELECT * FROM dbo.Orders ORDER BY |").clause).toBe("orderBy"));
  it("update target", () => expect(ctxOf("UPDATE |").clause).toBe("update"));
  it("update ... set", () => {
    const c = ctxOf("UPDATE dbo.Orders SET |");
    expect(c.clause).toBe("set");
    expect(names(c)).toEqual(["Orders"]);
  });
  it("UPDATE alias ... FROM dedupes the alias target", () => {
    const c = ctxOf("UPDATE o SET o.Status = 'x' FROM dbo.Orders o WHERE o.|");
    expect(c.qualifier).toBe("o");
    expect(names(c)).toEqual(["Orders o"]);
  });
  it("delete from", () => expect(ctxOf("DELETE FROM |").clause).toBe("delete"));
  it("delete ... where", () => {
    const c = ctxOf("DELETE FROM dbo.Orders WHERE |");
    expect(c.clause).toBe("where");
    expect(names(c)).toEqual(["Orders"]);
  });
  it("insert into", () => expect(ctxOf("INSERT INTO |").clause).toBe("insertInto"));
  it("insert column list", () => {
    const c = ctxOf("INSERT INTO dbo.Orders (|");
    expect(c.clause).toBe("insertColumns");
    expect(c.insertTarget).toEqual({ name: "Orders", schema: "dbo" });
  });
  it("insert column list with used columns", () => {
    const c = ctxOf("INSERT INTO dbo.Orders (CustomerId, |)");
    expect(c.clause).toBe("insertColumns");
    expect(c.insertColumnList).toEqual(["CustomerId"]);
  });
  it("after insert column list → values", () => expect(ctxOf("INSERT INTO dbo.Orders (CustomerId, OrderDate) |").clause).toBe("values"));
  it("after insert target → values", () => expect(ctxOf("INSERT INTO dbo.Orders |").clause).toBe("values"));
  it("inside VALUES (...) is a value position", () => {
    const c = ctxOf("INSERT INTO dbo.Orders (CustomerId) VALUES (|");
    expect(c.clause).toBe("values");
    expect(c.slot).toBe("argValue");
  });
  it("exec", () => expect(ctxOf("EXEC |").clause).toBe("exec"));
  it("exec with schema qualifier", () => {
    const c = ctxOf("EXEC dbo.|");
    expect(c.clause).toBe("exec");
    expect(c.qualifier).toBe("dbo");
  });
  it("exec args", () => {
    const c = ctxOf("EXEC dbo.usp_GetCustomerOrders |");
    expect(c.clause).toBe("execArgs");
    expect(c.execTarget).toBe("dbo.usp_GetCustomerOrders");
    expect(c.execArgsUsed).toEqual([]);
  });
  it("exec args used", () => {
    const c = ctxOf("EXEC dbo.usp_GetCustomerOrders @CustomerId = 1, |");
    expect(c.execArgsUsed).toEqual(["@CustomerId"]);
  });
  it("EXECUTE with @prefix", () => {
    const c = ctxOf("EXECUTE usp_GetCustomerOrders @StartDate = '2024-01-01', @Cu|");
    expect(c.clause).toBe("execArgs");
    expect(c.prefix).toBe("@Cu");
    expect(c.execArgsUsed).toEqual(["@StartDate"]);
  });
  it("exec arg value position with return-status variable", () => {
    const c = ctxOf("EXEC @rc = dbo.usp_GetCustomerOrders @CustomerId = |");
    expect(c.clause).toBe("execArgs");
    expect(c.execTarget).toBe("dbo.usp_GetCustomerOrders");
    expect(c.slot).toBe("argValue");
  });
  it("declare type position", () => expect(ctxOf("DECLARE @x |").clause).toBe("declare"));
  it("declare initializer is an expression", () => expect(ctxOf("DECLARE @total decimal(18,2) = |").clause).toBe("select"));
  it("declare name position", () => expect(ctxOf("DECLARE |").slot).toBe("name"));
  it("use", () => expect(ctxOf("USE |").clause).toBe("use"));
  it("CAST(... AS |) is a type position", () => expect(ctxOf("SELECT CAST(o.TotalAmount AS |) FROM dbo.Orders o").clause).toBe("declare"));
  it("after a table reference", () => {
    const c = ctxOf("SELECT * FROM dbo.Orders o |");
    expect(c.clause).toBe("from");
    expect(c.slot).toBe("afterReference");
  });
  it("after AS in FROM is an alias position", () => expect(ctxOf("SELECT * FROM dbo.Orders AS |").slot).toBe("alias"));
});

describe("scanner: CTEs, derived tables, subqueries", () => {
  it("CTE with an explicit column list", () => {
    const c = ctxOf("WITH recent (Id, Total) AS (SELECT OrderId, TotalAmount FROM dbo.Orders) SELECT r.| FROM recent r");
    expect(c.ctes).toEqual([{ name: "recent", columns: ["Id", "Total"] }]);
    const r = c.tablesInScope.find((t) => t.alias === "r")!;
    expect(r.isCte).toBe(true);
    expect(r.columns).toEqual(["Id", "Total"]);
  });
  it("CTE columns from a simple projection", () => {
    const c = ctxOf("WITH recent AS (SELECT OrderId, TotalAmount AS Total, o.Status FROM dbo.Orders o) SELECT | FROM recent");
    expect(c.ctes[0].columns).toEqual(["OrderId", "Total", "Status"]);
  });
  it("multiple CTEs, ;WITH", () => {
    const c = ctxOf("SELECT 1;\n;WITH a AS (SELECT 1 AS x), b (y) AS (SELECT 2) SELECT * FROM a JOIN b ON |");
    expect(c.ctes.map((x) => x.name)).toEqual(["a", "b"]);
    expect(c.tablesInScope.every((t) => t.isCte)).toBe(true);
  });
  it("derived table projection", () => {
    const c = ctxOf("SELECT d.| FROM (SELECT CustomerId, COUNT(*) AS Cnt FROM dbo.Orders GROUP BY CustomerId) d");
    const d = c.tablesInScope.find((t) => t.alias === "d")!;
    expect(d.isDerived).toBe(true);
    expect(d.columns).toEqual(["CustomerId", "Cnt"]);
  });
  it("subquery aliases are in scope", () => {
    const c = ctxOf("SELECT * FROM dbo.Orders o WHERE o.CustomerId IN (SELECT c.| FROM dbo.Customers c)");
    expect(c.qualifier).toBe("c");
    expect(names(c)).toEqual(["Orders o", "Customers c"]);
  });
  it("clause inside a correlated subquery", () => {
    const c = ctxOf("SELECT * FROM dbo.Orders o WHERE EXISTS (SELECT 1 FROM dbo.OrderItems i WHERE i.OrderId = o.OrderId AND |)");
    expect(c.clause).toBe("where");
    expect(names(c)).toEqual(["Orders o", "OrderItems i"]);
  });
  it("table-valued function with alias", () => {
    const c = ctxOf("SELECT t.| FROM dbo.ufn_CustomerOrders(1) AS t");
    expect(c.tablesInScope[0]).toMatchObject({ name: "ufn_CustomerOrders", schema: "dbo", alias: "t" });
  });
  it("table hints are not aliases", () => {
    const c = ctxOf("SELECT * FROM dbo.Orders o WITH (NOLOCK) JOIN dbo.Customers AS c WITH (NOLOCK) ON c.|");
    expect(names(c)).toEqual(["Orders o", "Customers c"]);
    expect(c.clause).toBe("on");
  });
  it("database-qualified names", () => {
    const c = ctxOf("SELECT x.| FROM SalesDB.dbo.Orders x");
    expect(c.tablesInScope[0]).toMatchObject({ database: "SalesDB", schema: "dbo", name: "Orders", alias: "x" });
  });
  it("CASE ... END does not end the statement", () => {
    const c = ctxOf("SELECT CASE WHEN o.Status = 'a' THEN 1 ELSE 0 END AS f, o.| FROM dbo.Orders o");
    expect(names(c)).toEqual(["Orders o"]);
  });
  it("cursor declaration FOR SELECT stays one statement", () => {
    const c = ctxOf("DECLARE c CURSOR FOR SELECT o.| FROM dbo.Orders o");
    expect(names(c)).toEqual(["Orders o"]);
  });
});

describe("scanner: identifiers", () => {
  it("bracketed qualifier and table", () => {
    const c = ctxOf("SELECT [o].| FROM [dbo].[Orders] [o]");
    expect(c.qualifier).toBe("o");
    expect(c.tablesInScope[0]).toMatchObject({ name: "Orders", schema: "dbo", alias: "o" });
  });
  it("open bracket prefix", () => {
    const { text } = { text: "SELECT * FROM [dbo].[Ord" };
    const c = ctxOf("SELECT * FROM [dbo].[Ord|");
    expect(c.clause).toBe("from");
    expect(c.qualifier).toBe("dbo");
    expect(c.prefix).toBe("Ord");
    expect(c.prefixStart).toBe(text.length - 3);
  });
  it("prefix of a partly typed word", () => {
    const c = ctxOf("SELECT * FROM dbo.Orders WHERE Cust|");
    expect(c.prefix).toBe("Cust");
    expect(c.clause).toBe("where");
  });
});

describe("scanner: statements and batches", () => {
  it("semicolon-separated statements", () => {
    const c = ctxOf("SELECT * FROM dbo.Customers c;\nSELECT | FROM dbo.Orders o");
    expect(names(c)).toEqual(["Orders o"]);
  });
  it("statements without semicolons", () => {
    const c = ctxOf("SELECT * FROM dbo.Customers c\nSELECT o.| FROM dbo.Orders o");
    expect(names(c)).toEqual(["Orders o"]);
  });
  it("GO resets variables", () => {
    const c = ctxOf("DECLARE @a int\nGO\nDECLARE @b int\nSELECT |");
    expect(c.variables.map((v) => v.name)).toEqual(["@b"]);
  });
  it("INSERT ... SELECT is one statement", () => {
    const c = ctxOf("INSERT INTO dbo.Orders (CustomerId)\nSELECT c.| FROM dbo.Customers c");
    expect(c.insertTarget).toEqual({ name: "Orders", schema: "dbo" });
    expect(c.clause).toBe("select");
    expect(c.tablesInScope.map((t) => t.name)).toContain("Customers");
  });
  it("UNION ALL keeps one statement", () => {
    const c = ctxOf("SELECT CustomerId FROM dbo.Orders\nUNION ALL\nSELECT | FROM dbo.Customers");
    expect(names(c)).toEqual(["Orders", "Customers"]);
  });
  it("IF / BEGIN / END boundaries", () => {
    const sql = "IF @x = 1\nBEGIN\n  UPDATE dbo.Orders SET Status = 'a' WHERE |\nEND";
    const c = ctxOf(sql);
    expect(c.clause).toBe("where");
    expect(c.statementStart).toBe(sql.indexOf("UPDATE"));
  });
  it("SET NOCOUNT ON then UPDATE ... SET", () => {
    const sql = "SET NOCOUNT ON\nUPDATE dbo.Orders SET |";
    const c = ctxOf(sql);
    expect(c.clause).toBe("set");
    expect(c.statementStart).toBe(sql.indexOf("UPDATE"));
  });
  it("procedure header parameters and body statement", () => {
    const sql = "CREATE PROCEDURE dbo.p @CustomerId int, @From datetime2 = NULL AS\nBEGIN\n  SELECT * FROM dbo.Orders WHERE CustomerId = |\nEND";
    const c = ctxOf(sql);
    expect(c.clause).toBe("where");
    expect(c.statementStart).toBe(sql.indexOf("SELECT"));
    expect(c.variables).toEqual([
      { name: "@CustomerId", type: "int", kind: "parameter" },
      { name: "@From", type: "datetime2", kind: "parameter" },
    ]);
  });
  it("DECLARE variables with types", () => {
    const c = ctxOf("DECLARE @a int, @b nvarchar(50) = N'x';\nDECLARE @t TABLE (Id int);\nSELECT |");
    expect(c.variables).toEqual([
      { name: "@a", type: "int", kind: "variable" },
      { name: "@b", type: "nvarchar(50)", kind: "variable" },
      { name: "@t", type: "TABLE", kind: "variable" },
    ]);
  });
  it("MERGE: ON clause with both tables", () => {
    const c = ctxOf("MERGE INTO dbo.Orders AS t USING dbo.Customers AS s ON t.CustomerId = s.|");
    expect(c.clause).toBe("on");
    expect(names(c)).toEqual(["Orders t", "Customers s"]);
  });
  it("MERGE: WHEN MATCHED THEN UPDATE SET", () => {
    const c = ctxOf("MERGE dbo.Orders AS t USING (SELECT CustomerId FROM dbo.Customers) AS s ON t.CustomerId = s.CustomerId WHEN MATCHED THEN UPDATE SET |");
    expect(c.clause).toBe("set");
    expect(c.tablesInScope.find((x) => x.alias === "s")?.columns).toEqual(["CustomerId"]);
  });
  it("MERGE: WHEN NOT MATCHED THEN INSERT (", () => {
    const c = ctxOf("MERGE dbo.Orders AS t USING dbo.Customers AS s ON t.CustomerId = s.CustomerId WHEN NOT MATCHED THEN INSERT (|");
    expect(c.clause).toBe("insertColumns");
    expect(c.insertTarget).toEqual({ name: "Orders", schema: "dbo" });
  });
});

describe("scanner: strings, comments, broken SQL", () => {
  it("in a string", () => expect(ctxOf("SELECT 'abc|' FROM t").inStringOrComment).toBe(true));
  it("in a line comment", () => expect(ctxOf("SELECT 1 -- comment |").inStringOrComment).toBe(true));
  it("in a block comment", () => expect(ctxOf("/* SELECT | */ SELECT 1").inStringOrComment).toBe(true));
  it("after a block comment", () => expect(ctxOf("/* x */| SELECT 1").inStringOrComment).toBe(false));
  it("in an unterminated string", () => expect(ctxOf("SELECT 'abc FROM |").inStringOrComment).toBe(true));
  it("in N'' string with escaped quote", () => expect(ctxOf("SELECT N'it''s |'").inStringOrComment).toBe(true));
  it("broken SQL does not throw", () => {
    const c = ctxOf("SELECT ((( FROM WHERE o.| ,,, JOIN");
    expect(c.qualifier).toBe("o");
  });
  it("garbage input does not throw", () => {
    for (const s of ["|", ")))|(((", "SELECT [|", "\"|", "EXEC|", "INSERT INTO (|", "WITH|", "@@|", "GO|", "/*|"]) {
      expect(() => ctxOf(s)).not.toThrow();
    }
  });
  it("tokenizer: GO alone on a line with count; not inside a line", () => {
    const toks = tokenize("SELECT 1\nGO 5\nSELECT GO FROM t", "tsql");
    const go = toks.filter((t) => t.kind === "go");
    expect(go).toHaveLength(1);
    expect(go[0].count).toBe(5);
    expect(toks.filter((t) => t.kind === "ident" && t.upper === "GO")).toHaveLength(1);
  });
  it("tokenizer: variables, temp tables, brackets", () => {
    const toks = tokenize("SELECT @x, @@ROWCOUNT, [a]]b], #tmp FROM t", "tsql");
    expect(toks.filter((t) => t.kind === "variable").map((t) => t.value)).toEqual(["@x", "@@ROWCOUNT"]);
    expect(toks.find((t) => t.kind === "qident")?.value).toBe("a]b");
    expect(toks.some((t) => t.kind === "ident" && t.value === "#tmp")).toBe(true);
  });
});

describe("scanner: PostgreSQL", () => {
  it("alias qualifier", () => {
    const c = ctxOf("SELECT o.| FROM public.orders o", "pg");
    expect(c.qualifier).toBe("o");
    expect(c.tablesInScope[0]).toMatchObject({ name: "orders", schema: "public", alias: "o" });
  });
  it("quoted identifiers", () => {
    const c = ctxOf('SELECT "o".| FROM "public"."orders" AS "o"', "pg");
    expect(c.qualifier).toBe("o");
    expect(c.tablesInScope[0]).toMatchObject({ name: "orders", schema: "public", alias: "o" });
  });
  it(":: cast is a type position", () => expect(ctxOf("SELECT total::| FROM orders", "pg").clause).toBe("declare"));
  it("function body in $$ is scanned as SQL with params and DECLAREs", () => {
    const sql = "CREATE FUNCTION f(p_customer int) RETURNS int AS $$\nDECLARE v_total numeric;\nBEGIN\n  SELECT o.| FROM orders o WHERE o.customer_id = p_customer;\nEND\n$$ LANGUAGE plpgsql;";
    const c = ctxOf(sql, "pg");
    expect(c.inStringOrComment).toBe(false);
    expect(c.qualifier).toBe("o");
    expect(c.tablesInScope[0].name).toBe("orders");
    expect(c.statementStart).toBe(sql.indexOf("SELECT o."));
    expect(c.variables.map((v) => v.name)).toEqual(["p_customer", "v_total"]);
  });
  it("DO $tag$ body", () => {
    const c = ctxOf("DO $body$ BEGIN PERFORM 1 FROM customers c WHERE c.| ; END $body$;", "pg");
    expect(c.qualifier).toBe("c");
    expect(c.clause).toBe("where");
  });
  it("$$ literal that is not a body is a string", () => expect(ctxOf("SELECT $$ some | text $$", "pg").inStringOrComment).toBe(true));
  it("nested block comments", () => expect(ctxOf("/* a /* b */ | */ SELECT 1", "pg").inStringOrComment).toBe(true));
  it("CALL args", () => {
    const c = ctxOf("CALL refresh_stats(p_day => now(), |", "pg");
    expect(c.clause).toBe("execArgs");
    expect(c.execTarget).toBe("refresh_stats");
    expect(c.execArgsUsed).toEqual(["p_day"]);
  });
  it("INSERT column list", () => {
    const c = ctxOf("INSERT INTO orders (|", "pg");
    expect(c.clause).toBe("insertColumns");
    expect(c.insertTarget).toEqual({ name: "orders" });
  });
  it("ON CONFLICT DO UPDATE SET stays in the INSERT", () => {
    const c = ctxOf("INSERT INTO orders (order_id) VALUES (1) ON CONFLICT (order_id) DO UPDATE SET |", "pg");
    expect(c.clause).toBe("set");
    expect(c.insertTarget).toEqual({ name: "orders" });
  });
  it("tokenizer: E'' escapes, $n, :name, ::", () => {
    const toks = tokenize("SELECT E'it\\'s', $1::int, :name FROM t", "pg");
    expect(toks.map((t) => t.kind)).toEqual(["keyword", "string", "punct", "param", "operator", "ident", "punct", "param", "keyword", "ident"]);
  });
});

it.each(["tsql", "pg"] as const)("GROUP BY suggestions use the current SELECT and only proven column references (%s)", (dialect) => {
  expect(ctxOf("WITH c AS (SELECT inner_col FROM t) SELECT outer_col, COUNT(*) FROM c GROUP BY |", dialect).nonAggregatedColumns).toEqual(["outer_col"]);
  expect(ctxOf("SELECT CAST(x AS int) AS n, t.*, x + SUM(y), z AS label FROM t GROUP BY |", dialect).nonAggregatedColumns).toEqual(["z"]);
  expect(ctxOf("SELECT a FROM t WHERE EXISTS (SELECT b, COUNT(*) FROM u GROUP BY |)", dialect).nonAggregatedColumns).toEqual(["b"]);
});

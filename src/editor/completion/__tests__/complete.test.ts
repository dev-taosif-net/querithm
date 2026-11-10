import { describe, it, expect } from "vitest";
import { IDX, PG_IDX, SNAPSHOT, at, byLabel, itemsOf, labels } from "./fixture";
import { buildSchemaIndex } from "../schemaIndex";
import { complete } from "../complete";
import { humpMatch, matchRank } from "../rank";
import type { SnapObject } from "../../../ipc/contract";

const colLabels = (sql: string, dialect: "tsql" | "pg" = "tsql") => labels(itemsOf(sql, dialect).filter((i) => i.kind === "column"));
const firstIndex = (items: ReturnType<typeof itemsOf>, pred: (l: string) => boolean) => items.findIndex((i) => pred(i.label));

describe("complete: qualifier contexts", () => {
  it("alias qualifier → that table's columns only", () => {
    const items = itemsOf("SELECT o.| FROM dbo.Orders o");
    expect(new Set(labels(items))).toEqual(new Set(["OrderId", "CustomerId", "OrderDate", "TotalAmount", "Status"]));
    expect(items.every((i) => i.kind === "column")).toBe(true);
    expect(byLabel(items, "TotalAmount")?.detail).toBe("decimal(18,2) NOT NULL");
    expect(byLabel(items, "OrderId")?.detail).toContain("PK");
    expect(byLabel(items, "Status")?.detail).toBe("nvarchar(20) NULL");
  });
  it("CTE qualifier → CTE columns", () => {
    expect(labels(itemsOf("WITH recent (Id, Total) AS (SELECT OrderId, TotalAmount FROM dbo.Orders) SELECT r.| FROM recent r"))).toEqual(["Id", "Total"]);
  });
  it("derived-table qualifier → projected columns", () => {
    expect(labels(itemsOf("SELECT d.| FROM (SELECT CustomerId, COUNT(*) AS Cnt FROM dbo.Orders GROUP BY CustomerId) d"))).toEqual(["Cnt", "CustomerId"]);
  });
  it("TVF alias → TVF columns", () => {
    expect(new Set(labels(itemsOf("SELECT t.| FROM dbo.ufn_CustomerOrders(1) AS t")))).toEqual(new Set(["OrderId", "OrderDate", "TotalAmount"]));
  });
  it("schema qualifier in FROM → that schema's objects, unqualified", () => {
    const items = itemsOf("SELECT * FROM sales.|");
    expect(labels(items)).toEqual(["Products"]);
  });
  it("bracketed schema qualifier with open bracket prefix inserts raw names", () => {
    const items = itemsOf("SELECT * FROM [dbo].[Ord|");
    const o = byLabel(items, "Orders")!;
    expect(o).toBeDefined();
    expect(o.insertText).toBeUndefined();
    expect(byLabel(items, "Order Details")?.insertText).toBeUndefined();
  });
  it("database qualifier → schemas of the current database", () => {
    expect(new Set(labels(itemsOf("SELECT * FROM SalesDB.|")))).toEqual(new Set(["dbo", "sales"]));
    expect(labels(itemsOf("SELECT * FROM SalesDB.sales.|"))).toEqual(["Products"]);
    expect(itemsOf("SELECT * FROM Archive.|")).toEqual([]);
  });
  it("schema qualifier in an expression → functions and relations", () => {
    const items = itemsOf("SELECT dbo.| ");
    expect(byLabel(items, "fn_OrderTotal")?.kind).toBe("func");
    expect(byLabel(items, "Orders")).toBeDefined();
  });
  it("table name used as qualifier without alias", () => {
    expect(colLabels("SELECT Customers.| FROM dbo.Customers")).toContain("FullName");
  });
});

describe("complete: expression contexts", () => {
  it("multiple tables → alias-prefixed columns, aliases, functions, keywords", () => {
    const items = itemsOf("SELECT | FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId");
    const oid = byLabel(items, "o.OrderId")!;
    expect(oid.insertText).toBeUndefined();
    expect(oid.filterText).toBe("OrderId");
    expect(byLabel(items, "c.FullName")).toBeDefined();
    expect(byLabel(items, "o")?.kind).toBe("alias");
    expect(byLabel(items, "c")?.kind).toBe("alias");
    expect(byLabel(items, "COUNT")?.kind).toBe("func");
    expect(byLabel(items, "FROM")?.kind).toBe("keyword");
  });
  it("single table → bare column names", () => {
    const l = colLabels("SELECT | FROM dbo.Orders");
    expect(l).toContain("OrderId");
    expect(l.some((x) => x.includes("."))).toBe(false);
  });
  it("cursor before FROM: nearest table is the first after the cursor", () => {
    const items = itemsOf("SELECT | FROM dbo.Orders o JOIN dbo.Customers c ON 1 = 1");
    expect(firstIndex(items, (l) => l.startsWith("o."))).toBeLessThan(firstIndex(items, (l) => l.startsWith("c.")));
  });
  it("nearest preceding table's columns first", () => {
    const items = itemsOf("SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId WHERE |");
    expect(firstIndex(items, (l) => l.startsWith("c."))).toBeLessThan(firstIndex(items, (l) => l.startsWith("o.")));
    expect(items[0].kind).toBe("column");
  });
  it("usage count breaks ties within a rank", () => {
    const items = itemsOf("SELECT | FROM dbo.Orders", "tsql", IDX, new Map([["status", 10]]));
    expect(items[0].label).toBe("Status");
  });
  it("UPDATE ... SET → target columns", () => {
    expect(new Set(colLabels("UPDATE dbo.Customers SET |"))).toEqual(new Set(["CustomerId", "FullName", "Email"]));
  });
  it("DELETE ... WHERE → columns", () => {
    expect(colLabels("DELETE FROM dbo.Orders WHERE |")).toContain("TotalAmount");
  });
  it("variables and proc parameters", () => {
    const items = itemsOf("CREATE PROCEDURE dbo.p @CustomerId int AS\nBEGIN\nDECLARE @Limit int = 5;\nSELECT TOP (@|) * FROM dbo.Orders\nEND");
    expect(byLabel(items, "@Limit")?.kind).toBe("variable");
    expect(byLabel(items, "@CustomerId")?.kind).toBe("parameter");
    expect(byLabel(items, "@CustomerId")?.detail).toBe("int");
    expect(items.every((i) => i.label.startsWith("@"))).toBe(true);
  });
  it("scalar function is schema-qualified on insert", () => {
    const f = byLabel(itemsOf("SELECT fn_| FROM dbo.Orders"), "fn_OrderTotal")!;
    expect(f.kind).toBe("func");
    expect(f.insertText).toBe("dbo.fn_OrderTotal(${1:@OrderId})");
    expect(f.isSnippet).toBe(true);
  });
  it("after AS (column alias) offers nothing", () => expect(itemsOf("SELECT OrderId AS | FROM dbo.Orders")).toEqual([]));
  it("no schema index: keywords and built-ins only, no throw", () => {
    const items = itemsOf("SELECT | FROM t", "tsql", null);
    expect(items.length).toBeGreaterThan(0);
    expect(items.some((i) => i.kind === "column")).toBe(false);
  });
});

describe("complete: auto-alias", () => {
  const ins = (sql: string, label: string, autoAlias?: boolean) => {
    const { text, offset } = at(sql);
    return byLabel(complete({ text, offset, dialect: "tsql", schema: IDX, autoAlias }).items, label);
  };
  it("FROM/JOIN tables get an alias", () => {
    expect(ins("SELECT * FROM Ord|", "Orders")?.insertText).toBe("Orders ord");
    expect(ins("SELECT * FROM Ord|", "Orders")?.alias).toBe("ord");
    expect(ins("SELECT * FROM sales.Prod|", "Products")?.insertText).toBe("Products pro");
    expect(ins("SELECT * FROM Orders o JOIN sales.|", "Products")?.insertText).toBe("Products pro");
  });
  it("aliases already in the statement are numbered", () => {
    expect(ins("SELECT * FROM Orders ord JOIN Ord|", "Orders")?.insertText).toBe("Orders ord2");
    expect(ins("SELECT * FROM Ord| JOIN Customers ord ON 1 = 1", "Orders")?.insertText).toBe("Orders ord2");
  });
  it("no alias when one already follows, mid-word, in DML targets, or when turned off", () => {
    expect(ins("SELECT * FROM Ord| o", "Orders")?.insertText).toBeUndefined();
    expect(ins("SELECT * FROM Ord| AS o", "Orders")?.insertText).toBeUndefined();
    expect(ins("SELECT * FROM Ord|ers", "Orders")?.insertText).toBeUndefined();
    expect(ins("SELECT * FROM Ord| WHERE 1 = 1", "Orders")?.insertText).toBe("Orders ord");
    expect(ins("UPDATE Ord|", "Orders")?.insertText).toBeUndefined();
    expect(ins("SELECT * FROM Ord|", "Orders", false)?.insertText).toBeUndefined();
  });
});

describe("complete: table contexts", () => {
  it("FROM → tables, views, TVFs, schemas; qualified when not dbo", () => {
    const items = itemsOf("SELECT * FROM |");
    const l = labels(items);
    for (const x of ["Orders", "Customers", "OrderItems", "sales.Products", "vCustomerOrders", "ufn_CustomerOrders", "sales"]) expect(l).toContain(x);
    expect(byLabel(items, "vCustomerOrders")?.kind).toBe("view");
    expect(byLabel(items, "sales.Products")?.detail).toBe("table · sales");
    expect(byLabel(items, "ufn_CustomerOrders")?.insertText).toBe("ufn_CustomerOrders(${1:@CustomerId}) uc");
    expect(l).not.toContain("usp_GetCustomerOrders");
  });
  it("names needing brackets are bracketed", () => {
    expect(byLabel(itemsOf("SELECT * FROM Order|"), "Order Details")?.insertText).toBe("[Order Details] od");
  });
  it("DELETE FROM → no TVFs", () => {
    const l = labels(itemsOf("DELETE FROM |"));
    expect(l).toContain("Orders");
    expect(l).not.toContain("ufn_CustomerOrders");
  });
  it("INSERT INTO / UPDATE → tables", () => {
    expect(labels(itemsOf("INSERT INTO |"))).toContain("OrderItems");
    expect(labels(itemsOf("UPDATE Cus|"))).toContain("Customers");
  });
  it("CTE names are offered first in FROM", () => {
    const items = itemsOf("WITH recent AS (SELECT 1 AS x) SELECT * FROM |");
    expect(items[0]).toMatchObject({ label: "recent", kind: "cte" });
  });
  it("after a complete reference → clause keywords, no tables", () => {
    const items = itemsOf("SELECT * FROM dbo.Orders o |");
    expect(labels(items)).toContain("WHERE");
    expect(labels(items)).toContain("INNER JOIN");
    expect(items.every((i) => i.kind === "keyword")).toBe(true);
  });
  it("after AS in FROM offers nothing", () => expect(itemsOf("SELECT * FROM dbo.Orders AS |")).toEqual([]));
});

describe("complete: ON with FK join snippets", () => {
  it("child joined to parent", () => {
    const items = itemsOf("SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON |");
    expect(items[0]).toMatchObject({ label: "c.CustomerId = o.CustomerId", kind: "snippet" });
  });
  it("parent joined to child (other direction)", () => {
    expect(itemsOf("SELECT * FROM dbo.Customers c JOIN dbo.Orders o ON |")[0].label).toBe("o.CustomerId = c.CustomerId");
  });
  it("three tables, FK to the nearest", () => {
    const items = itemsOf("SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId JOIN dbo.OrderItems i ON |");
    expect(items[0].label).toBe("i.OrderId = o.OrderId");
    expect(labels(items)).not.toContain("c.CustomerId = o.CustomerId");
  });
  it("ON also offers columns", () => {
    expect(labels(itemsOf("SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON |"))).toContain("o.TotalAmount");
  });
});

describe("complete: EXEC", () => {
  it("procedures sorted by usage", () => {
    const items = itemsOf("EXEC |", "tsql", IDX, new Map([["usp_getorderdetails", 5], ["usp_getcustomerorders", 2]]));
    const procs = labels(items.filter((i) => i.kind === "proc"));
    expect(procs).toEqual(["usp_GetOrderDetails", "usp_GetCustomerOrders", "sales.usp_RefreshPrices"]);
  });
  it("schema-qualified EXEC", () => expect(labels(itemsOf("EXEC sales.|"))).toEqual(["usp_RefreshPrices"]));
  it("underscore humps: usp_gco → usp_GetCustomerOrders", () => {
    expect(itemsOf("EXEC usp_gco|")[0].label).toBe("usp_GetCustomerOrders");
  });
  it("parameters as snippets with types, OUTPUT marked, ordinal order", () => {
    const items = itemsOf("EXEC dbo.usp_GetCustomerOrders |").filter((i) => i.kind === "parameter");
    expect(labels(items)).toEqual(["@CustomerId", "@StartDate", "@MinTotal"]);
    expect(items[0]).toMatchObject({ insertText: "@CustomerId = ", detail: "int" });
    expect(items[1].detail).toBe("datetime2 = default");
    expect(items[2]).toMatchObject({ detail: "decimal(18,2) OUTPUT", isSnippet: true, insertText: "@MinTotal = ${1:@MinTotal} OUTPUT" });
  });
  it("all-parameters snippet", () => {
    const s = byLabel(itemsOf("EXEC dbo.usp_GetCustomerOrders |"), "(all parameters)")!;
    expect(s.insertText).toBe("@CustomerId = ${1:int}, @StartDate = ${2:datetime2}, @MinTotal = ${3:@MinTotal} OUTPUT");
  });
  it("used parameters are excluded", () => {
    const l = labels(itemsOf("EXEC dbo.usp_GetCustomerOrders @CustomerId = 1, |").filter((i) => i.kind === "parameter"));
    expect(l).toEqual(["@StartDate", "@MinTotal"]);
  });
  it("value position offers variables, not parameters", () => {
    const items = itemsOf("DECLARE @cust int;\nEXEC dbo.usp_GetCustomerOrders @CustomerId = |");
    expect(byLabel(items, "@cust")?.kind).toBe("variable");
    expect(items.some((i) => i.kind === "parameter")).toBe(false);
  });
});

describe("complete: DECLARE, USE, INSERT", () => {
  it("types including user-defined types", () => {
    const items = itemsOf("DECLARE @x |");
    for (const t of ["int", "nvarchar", "decimal", "datetime2", "Phone"]) expect(byLabel(items, t)?.kind).toBe("type");
    expect(byLabel(items, "decimal")?.insertText).toBe("decimal(${1:18}, ${2:2})");
  });
  it("USE → databases", () => {
    expect(new Set(labels(itemsOf("USE |")))).toEqual(new Set(["SalesDB", "master", "Archive"]));
  });
  it("insert columns: non-identity first, identity last, computed excluded", () => {
    const items = itemsOf("INSERT INTO dbo.Orders (|");
    const cols = labels(items.filter((i) => i.kind === "column"));
    expect(cols).toEqual(["CustomerId", "OrderDate", "Status", "TotalAmount", "OrderId"]);
    expect(labels(itemsOf("INSERT INTO dbo.OrderItems (|"))).not.toContain("LineTotal");
  });
  it("insert columns exclude already listed ones", () => {
    expect(labels(itemsOf("INSERT INTO dbo.Orders (CustomerId, |)").filter((i) => i.kind === "column"))).not.toContain("CustomerId");
  });
  it("VALUES placeholder snippet: one per non-identity, non-computed column", () => {
    const items = itemsOf("INSERT INTO dbo.Orders |");
    expect(byLabel(items, "VALUES (…)")?.insertText).toBe("VALUES (${1:CustomerId}, ${2:OrderDate}, ${3:TotalAmount}, ${4:Status})");
    expect(byLabel(items, "(columns) VALUES (…)")?.insertText).toBe("(CustomerId, OrderDate, TotalAmount, Status) VALUES (${1:CustomerId}, ${2:OrderDate}, ${3:TotalAmount}, ${4:Status})");
    expect(items[0].kind).toBe("snippet");
  });
  it("VALUES snippet follows the explicit column list", () => {
    expect(byLabel(itemsOf("INSERT INTO dbo.OrderItems (OrderId, Quantity) |"), "VALUES (…)")?.insertText).toBe("VALUES (${1:OrderId}, ${2:Quantity})");
  });
  it("VALUES excludes identity and computed columns", () => {
    expect(byLabel(itemsOf("INSERT INTO dbo.OrderItems |"), "VALUES (…)")?.insertText).toBe("VALUES (${1:OrderId}, ${2:ProductId}, ${3:Quantity}, ${4:UnitPrice})");
  });
  it("MERGE ... THEN INSERT ( → target columns", () => {
    const l = labels(itemsOf("MERGE dbo.Orders AS t USING dbo.Customers AS s ON t.CustomerId = s.CustomerId WHEN NOT MATCHED THEN INSERT (|"));
    expect(l).toContain("TotalAmount");
  });
});

describe("complete: strings, comments, statements", () => {
  it("cursor in string → no items", () => expect(itemsOf("SELECT 'o.|' FROM dbo.Orders o")).toEqual([]));
  it("cursor in comment → no items", () => expect(itemsOf("SELECT * FROM dbo.Orders o -- o.|")).toEqual([]));
  it("statement start → statement keywords", () => {
    const l = labels(itemsOf("SELECT 1;\n|"));
    expect(l).toContain("SELECT");
    expect(l).toContain("INSERT INTO");
  });
  it("GO batch then completion in the second batch", () => {
    expect(colLabels("SELECT * FROM dbo.Customers\nGO\nSELECT x.| FROM dbo.Orders x")).toContain("TotalAmount");
  });
});

describe("ranking", () => {
  it("matchRank: prefix > camel hump > substring", () => {
    expect(matchRank("cid", "CidCode")).toBe(0);
    expect(matchRank("cId", "CustomerId")).toBe(1);
    expect(matchRank("cid", "AcidLevel")).toBe(2);
    expect(matchRank("xyz", "CustomerId")).toBe(-1);
    expect(humpMatch("usp_gco", "usp_GetCustomerOrders")).toBe(true);
    expect(humpMatch("gco", "usp_GetCustomerOrders")).toBe(false);
  });
  it("prefix vs hump vs substring order in items", () => {
    const l = colLabels("SELECT cid| FROM dbo.Metrics");
    expect(l).toEqual(["CidCode", "CustomerId", "AcidLevel"]);
  });
  it("keywords are never ranked above objects in FROM (even with a better match)", () => {
    const items = itemsOf("SELECT * FROM op|");
    const shops = firstIndex(items, (l) => l === "Shops");
    const kw = items.findIndex((i) => i.kind === "keyword");
    expect(shops).toBeGreaterThanOrEqual(0);
    expect(kw).toBeGreaterThan(shops);
    const items2 = itemsOf("SELECT * FROM O|");
    const lastObj = items2.map((i) => i.kind).lastIndexOf("table");
    expect(items2.findIndex((i) => i.kind === "keyword")).toBeGreaterThan(lastObj);
  });
  it("items are sorted by zero-padded sortText", () => {
    const items = itemsOf("SELECT | FROM dbo.Orders o JOIN dbo.Customers c ON 1 = 1", "tsql", IDX, new Map([["fullname", 3]]));
    const sorted = [...items].sort((a, b) => (a.sortText < b.sortText ? -1 : a.sortText > b.sortText ? 1 : 0));
    expect(items).toEqual(sorted);
  });
  it("items not matching the prefix are dropped; list capped at 500", () => {
    expect(labels(itemsOf("SELECT zzqq| FROM dbo.Orders"))).toEqual([]);
    const objs: SnapObject[] = [];
    for (let i = 0; i < 2000; i++) objs.push({ id: 1000 + i, schema: "dbo", name: `T${i}`, kind: "table", columns: [] });
    const big = buildSchemaIndex({ ...SNAPSHOT, objects: objs, fks: [] }, "tsql", []);
    expect(itemsOf("SELECT * FROM |", "tsql", big).length).toBe(500);
  });
});

describe("complete: PostgreSQL", () => {
  it("public is the default schema", () => {
    const l = labels(itemsOf("SELECT * FROM |", "pg"));
    expect(l).toContain("orders");
    expect(l).toContain("customers");
    expect(l).toContain("reporting.daily_sales");
  });
  it("alias columns", () => {
    expect(new Set(colLabels("SELECT o.| FROM orders o", "pg"))).toEqual(new Set(["order_id", "customer_id", "created_at", "total"]));
  });
  it("FK join snippet", () => {
    expect(itemsOf("SELECT * FROM orders o JOIN customers c ON |", "pg")[0].label).toBe("c.customer_id = o.customer_id");
  });
  it("CALL args as name => snippets", () => {
    const p = byLabel(itemsOf("CALL refresh_stats(|", "pg"), "p_day")!;
    expect(p.insertText).toBe("p_day => ");
  });
  it("types after ::", () => {
    expect(byLabel(itemsOf("SELECT total::| FROM orders", "pg"), "numeric")?.kind).toBe("type");
  });
  it("mixed-case names are double-quoted", () => {
    expect(byLabel(itemsOf("SELECT * FROM Ord|", "pg"), "OrderArchive")?.insertText).toBe('"OrderArchive" oa');
  });
  it("completion inside a $$ function body", () => {
    expect(colLabels("CREATE OR REPLACE FUNCTION f() RETURNS void AS $$ BEGIN UPDATE orders SET | ; END $$ LANGUAGE plpgsql;", "pg")).toContain("total");
  });
  it("pg index uses the given dialect", () => expect(PG_IDX.defaultSchema).toBe("public"));
});

// Regression: a statement without ';' followed by a new one (`... FROM hr.employees emp\nsele`) offered nothing.
describe("complete: next statement without a semicolon", () => {
  const cases = { tsql: ["dbo.Orders", "o", "OrderId"], pg: ["public.orders", "o", "order_id"] } as const;
  for (const [dialect, [table, alias, col]] of Object.entries(cases) as [("tsql" | "pg"), readonly string[]][]) {
    describe(dialect, () => {
      it.each([
        `SELECT * FROM ${table} ${alias}\nsele|`,
        `SELECT * FROM ${table}\nsele|`,
        `SELECT * FROM ${table} ${alias} WHERE ${alias}.${col} = 1\nsele|`,
        `SELECT * FROM ${table} ${alias} WHERE ${alias}.${col} IS NULL\nsele|`,
        `SELECT * FROM ${table} ${alias} ORDER BY ${col} DESC\nsele|`,
        `SELECT COUNT(*) FROM ${table}\nsele|`,
        `DELETE FROM ${table}\nsele|`,
      ])("offers SELECT in %j", (sql) => {
        expect(labels(itemsOf(sql, dialect))).toContain("SELECT");
      });

      it("offers statement keywords below the clause keywords on a new line", () => {
        const items = itemsOf(`SELECT * FROM ${table} ${alias}\n|`, dialect);
        const ls = labels(items);
        expect(ls).toContain("INSERT INTO");
        expect(ls.indexOf("WHERE")).toBeLessThan(ls.indexOf("INSERT INTO"));
      });

      it.each([
        `SELECT * FROM ${table} ${alias} WHERE ${alias}.${col} IN (SELECT 1 FROM ${table}\nins|`,
        `SELECT * FROM ${table} ${alias} WHERE ${alias}.${col} =\nins|`,
        `SELECT * FROM ${table} ${alias} WHERE ${alias}.${col} = 1 AND\nins|`,
        `SELECT * FROM\nins|`,
        `UPDATE ${table}\nins|`,
      ])("does not offer statement keywords where the statement cannot end: %j", (sql) => {
        expect(labels(itemsOf(sql, dialect))).not.toContain("INSERT INTO");
      });

      it("keeps a mid-line empty-prefix list free of statement keywords", () => {
        expect(labels(itemsOf(`SELECT * FROM ${table} ${alias} |`, dialect))).not.toContain("INSERT INTO");
      });
    });
  }
});

describe("complete: dbForge SQL Complete advanced features", () => {
  it("In-Editor JOIN Clause Auto-Generation suggests full JOIN with ON condition from foreign key", () => {
    const items = itemsOf("SELECT * FROM dbo.Orders o JOIN |");
    const joinSnippet = items.find((i) => i.label.startsWith("Customers ON") || i.detail?.includes("JOIN · FK"));
    expect(joinSnippet).toBeDefined();
    expect(joinSnippet?.kind).toBe("snippet");
    expect(joinSnippet?.insertText).toContain("Customers");
    expect(joinSnippet?.insertText).toContain("ON");
    expect(joinSnippet?.insertText).toContain("CustomerId");
  });

  it("GROUP BY suggests non-aggregated columns and (all non-aggregated columns) snippet", () => {
    const items = itemsOf("SELECT Status, COUNT(*) FROM dbo.Orders GROUP BY |");
    const allSnippet = byLabel(items, "(all non-aggregated columns)");
    expect(allSnippet).toBeDefined();
    expect(allSnippet?.insertText).toBe("Status");

    const statusCol = byLabel(items, "Status");
    expect(statusCol).toBeDefined();
    // Non-aggregated column Status should be prioritized over other columns like OrderDate
    const statusIdx = items.findIndex((i) => i.label === "Status");
    const orderDateIdx = items.findIndex((i) => i.label === "OrderDate");
    expect(statusIdx).toBeLessThan(orderDateIdx);
  });

  it("Statement snippet templates expand on keyword and shorthand triggers", () => {
    const items = itemsOf("ins|");
    const insItem = items.find((i) => i.label.startsWith("INSERT INTO"));
    expect(insItem).toBeDefined();
    expect(insItem?.insertText).toContain("INSERT INTO");

    const ssfItems = itemsOf("ssf|");
    const ssfItem = ssfItems.find((i) => i.label.startsWith("SELECT"));
    expect(ssfItem).toBeDefined();
    expect(ssfItem?.insertText).toContain("SELECT");
    expect(ssfItem?.insertText).toContain("FROM");
  });
});


describe("complete: SQLite and MySQL", () => {
  const snap = (objects: SnapObject[], database: string) => ({ profileId: "x", database, status: "fresh" as const, objects, fks: [] });
  const col = (name: string, ordinal: number) => ({ name, type: "TEXT", nullable: true, pk: false, identity: false, computed: false, ordinal });
  const sqliteIdx = buildSchemaIndex(snap([{ id: 1, schema: "main", name: "Order", kind: "table", columns: [col("id", 1), col("Total Due", 2)] }], "main"), "sqlite", ["main"]);
  const mysqlIdx = buildSchemaIndex(snap([{ id: 1, schema: "shop", name: "order", kind: "table", columns: [col("id", 1), col("key", 2)] }], "shop"), "mySql", ["shop"]);

  it("SQLite: names in the default schema (main) come unqualified, quoted with \"…\" where needed; no procedure-call template", () => {
    const tables = itemsOf("SELECT * FROM |", "sqlite", sqliteIdx);
    expect(byLabel(tables, "Order")?.insertText).toMatch(/^"Order"/);
    expect(byLabel(itemsOf('SELECT o.| FROM "Order" o', "sqlite", sqliteIdx), "Total Due")?.insertText).toBe('"Total Due"');
    const start = labels(itemsOf("|", "sqlite", sqliteIdx));
    expect(start).not.toContain("EXEC …");
    expect(start).not.toContain("CALL …");
  });

  it("MySQL: the snapshot's database is the default schema; reserved names in backticks; a CALL template", () => {
    const tables = itemsOf("SELECT * FROM |", "mySql", mysqlIdx);
    expect(byLabel(tables, "order")?.insertText).toMatch(/^`order`/);
    expect(byLabel(itemsOf("SELECT o.| FROM `order` o", "mySql", mysqlIdx), "key")?.insertText).toBe("`key`");
    expect(labels(itemsOf("|", "mySql", mysqlIdx))).toContain("CALL …");
  });
});

// D4 S06: Oracle tier 1 — the signed-in user's schema is the default (the user is the schema), names fold to upper
// case, so a mixed-case or reserved catalog name is inserted "quoted"; PL/SQL keywords; :binds are not columns.
describe("complete: Oracle", () => {
  const snap = (objects: SnapObject[]) => ({ profileId: "o", database: "FREEPDB1", status: "fresh" as const, objects, fks: [] });
  const col = (name: string, ordinal: number) => ({ name, type: "NUMBER", nullable: true, pk: false, identity: false, computed: false, ordinal });
  const oraIdx = buildSchemaIndex(snap([
    { id: 1, schema: "QUERITHM_APP", name: "ORDERS", kind: "table", columns: [col("ORDER_ID", 1), col("TYPE", 2), col("Mixed", 3)] },
    { id: 2, schema: "QUERITHM_APP", name: "Audit Log", kind: "table", columns: [col("ID", 1)] },
    { id: 3, schema: "QUERITHM_OWN", name: "SHARED_T", kind: "table", columns: [col("ID", 1)] },
  ]), "oracle", ["FREEPDB1"], "QUERITHM_APP");

  it("the login's schema is the default: its tables come bare, another schema's qualified, case-sensitive names quoted", () => {
    expect(oraIdx.defaultSchema).toBe("QUERITHM_APP");
    const tables = itemsOf("SELECT * FROM |", "oracle", oraIdx);
    expect(byLabel(tables, "ORDERS")?.insertText ?? "ORDERS").toMatch(/^ORDERS/);
    expect(byLabel(tables, "Audit Log")?.insertText).toMatch(/^"Audit Log"/);
    const shared = tables.find((i) => i.label === "SHARED_T" || i.label === "QUERITHM_OWN.SHARED_T");
    expect(shared?.insertText ?? shared?.label).toMatch(/QUERITHM_OWN\.SHARED_T/);
  });

  it("columns that are reserved or not upper case are quoted; plain upper-case ones are not", () => {
    const cols = itemsOf("SELECT o.| FROM orders o", "oracle", oraIdx);
    expect(byLabel(cols, "ORDER_ID")?.insertText ?? "ORDER_ID").toBe("ORDER_ID");
    expect(byLabel(cols, "TYPE")?.insertText).toBe('"TYPE"');
    expect(byLabel(cols, "Mixed")?.insertText).toBe('"Mixed"');
  });

  it("offers PL/SQL statement starts and nothing inside a q'…' literal", () => {
    expect(labels(itemsOf("|", "oracle", oraIdx))).toEqual(expect.arrayContaining(["DECLARE", "BEGIN"]));
    expect(itemsOf("SELECT q'[FROM |]' FROM dual", "oracle", oraIdx)).toEqual([]);
  });

  it("without a login (no user name) the service is the fallback default, as for an engine whose database is the schema", () => {
    expect(buildSchemaIndex(snap([]), "oracle", ["FREEPDB1"]).defaultSchema).toBe("FREEPDB1");
    // Other dialects ignore a login schema.
    expect(buildSchemaIndex({ ...snap([]), database: "SalesDB" }, "tsql", [], "QUERITHM_APP").defaultSchema).toBe("dbo");
  });
});

// Regression: PostgreSQL and DuckDB do not reserve EXISTS, so `EXISTS (` lexed as a function call and the FROM inside
// it was skipped — no table suggestions in `WHERE EXISTS (SELECT 1 FROM |`.
describe("complete: tables in a subquery opened after a non-reserved word, every engine", () => {
  const ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb"] as const;
  const tableName = (d: (typeof ENGINES)[number]) => (d === "pg" ? "orders" : "Orders");
  const outer = (d: (typeof ENGINES)[number]) => (d === "pg" ? "customers c" : "dbo.Customers c");
  const SHAPES = [
    (o: string) => `SELECT * FROM ${o} WHERE EXISTS (SELECT 1 FROM |`,
    (o: string) => `SELECT * FROM ${o} WHERE EXISTS (SELECT 1 FROM |)`,
    (o: string) => `SELECT * FROM ${o} WHERE NOT EXISTS (SELECT 1 FROM | WHERE 1 = 1)`,
    (o: string) => `SELECT * FROM ${o} WHERE EXISTS (SELECT 1 FROM dbo.Orders x JOIN |`,
    (o: string) => `SELECT * FROM ${o}\nWHERE EXISTS (\n  SELECT 1\n  FROM |\n)\nORDER BY 1`,
  ];
  for (const d of ENGINES) {
    it(`${d}: EXISTS (SELECT … FROM |) offers tables`, () => {
      for (const shape of SHAPES) {
        const sql = shape(outer(d));
        const items = itemsOf(d === "pg" ? sql.replace("dbo.Orders", "orders") : sql, d);
        expect(labels(items.filter((i) => i.kind === "table")), sql).toContain(tableName(d));
      }
    });
    it(`${d}: a FROM inside a real function call still is not a table slot`, () => {
      const sql = "SELECT EXTRACT(YEAR FROM |) FROM t";
      expect(itemsOf(sql, d).some((i) => i.kind === "table" && i.label === tableName(d))).toBe(false);
    });
  }
  it("pg: a name before a subquery paren (ARRAY(SELECT …), my_fn((SELECT …))) keeps the subquery's FROM", () => {
    expect(labels(itemsOf("SELECT ARRAY(SELECT 1 FROM |)", "pg"))).toContain("orders");
    expect(labels(itemsOf("SELECT my_fn((SELECT 1 FROM |))", "pg"))).toContain("orders");
  });
  it("pg: the subquery's table alias is in scope for its columns", () => {
    const items = itemsOf("SELECT * FROM customers c WHERE EXISTS (SELECT 1 FROM orders o WHERE o.|)", "pg");
    expect(labels(items)).toContain("order_id");
  });
});

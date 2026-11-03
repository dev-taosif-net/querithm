import { describe, expect, it } from "vitest";
import { firstStatementLine, statementTitle } from "../statementTitle";

describe("statementTitle (D24: verb-first title for an untitled tab)", () => {
  it.each([
    ["UPDATE dbo.Invoice SET Total = 1 WHERE Id = 2;", "tsql", "UPDATE Invoice"],
    ["update [sales].[Invoice] set Total = 1", "tsql", "UPDATE sales.Invoice"],
    ["SELECT c.Name FROM dbo.Customers AS c JOIN dbo.Orders o ON o.c = c.id", "tsql", "SELECT Customers"],
    ["select * from public.orders where id in (select id from x)", "pg", "SELECT orders"],
    ['SELECT * FROM "Sales"."Order Lines"', "pg", "SELECT Sales.Order Lines"],
    ["INSERT INTO dbo.Log (a) VALUES (1)", "tsql", "INSERT Log"],
    ["DELETE FROM stage.Load WHERE 1 = 1", "tsql", "DELETE stage.Load"],
    ["TRUNCATE TABLE dbo.Staging", "tsql", "TRUNCATE Staging"],
    ["MERGE INTO dbo.Target AS t USING s ON 1 = 1 WHEN MATCHED THEN DELETE;", "tsql", "MERGE Target"],
    ["EXEC @rc = dbo.usp_PostOrders @Id = 1", "tsql", "EXEC usp_PostOrders"],
    ["EXECUTE sales.usp_Close", "tsql", "EXEC sales.usp_Close"],
    ["CREATE OR ALTER PROCEDURE dbo.usp_X AS SELECT 1", "tsql", "CREATE usp_X"],
    ["DROP TABLE IF EXISTS public.tmp_orders", "pg", "DROP tmp_orders"],
    ["ALTER VIEW sales.v_open AS SELECT 1", "tsql", "ALTER sales.v_open"],
    ["SalesDB.dbo.Orders", "tsql", undefined],
  ] as const)("%s → %s", (sql, dialect, title) => {
    expect(statementTitle(sql, dialect)).toBe(title);
  });

  it("skips BEGIN TRAN, SET, USE, DECLARE and comments to the first statement that does something", () => {
    const sql = "-- fix totals\nBEGIN TRAN;\nSET NOCOUNT ON;\nDECLARE @x int = 1;\n/* go */\nUPDATE t SET t.Total = s.Total FROM dbo.OrderTotals AS t;";
    expect(statementTitle(sql, "tsql")).toBe("UPDATE t");
    expect(statementTitle("USE SalesDB\nGO\nSELECT * FROM dbo.Big", "tsql")).toBe("SELECT Big");
  });

  it("a CTE takes its main statement's verb", () => {
    expect(statementTitle("WITH x AS (SELECT * FROM a) SELECT * FROM dbo.Orders o JOIN x ON 1 = 1", "tsql")).toBe("SELECT Orders");
    expect(statementTitle("WITH x AS (SELECT 1) DELETE FROM public.t", "pg")).toBe("DELETE t");
  });

  it("names nothing it cannot read: SELECT 1, an empty tab, a bare word", () => {
    expect(statementTitle("SELECT 1", "tsql")).toBeUndefined();
    expect(statementTitle("", "pg")).toBeUndefined();
    expect(statementTitle("-- only a comment", "tsql")).toBeUndefined();
    expect(statementTitle("SELECT * FROM (SELECT 1) x", "tsql")).toBeUndefined();
  });

  it("never reads the SQL inside strings or comments as a statement", () => {
    expect(statementTitle("SELECT 'DROP TABLE x' AS s FROM dbo.Log", "tsql")).toBe("SELECT Log");
    expect(statementTitle("/* DELETE FROM dbo.Everything */ SELECT * FROM dbo.Safe", "tsql")).toBe("SELECT Safe");
  });
});

describe("firstStatementLine (Ctrl+P preview)", () => {
  it("joins the first statement's tokens on one line, without comments", () => {
    expect(firstStatementLine("-- note\nSELECT  region,\n  SUM(amount)\nFROM sales.orders\nGROUP BY region;\nSELECT 2", "pg")).toBe("SELECT region, SUM(amount) FROM sales.orders GROUP BY region");
    expect(firstStatementLine("", "tsql")).toBe("");
  });
  it("is cut at `max` characters", () => {
    const line = firstStatementLine(`SELECT ${Array.from({ length: 80 }, (_, i) => `c${i}`).join(", ")} FROM t`, "tsql", 40);
    expect(line).toHaveLength(40);
    expect(line.endsWith("…")).toBe(true);
  });
});

describe("statementTitle on SQLite and MySQL", () => {
  it("leaves out SQLite's main schema; MySQL has no default schema, so the database stays in the title", () => {
    expect(statementTitle('UPDATE main."Order Lines" SET x = 1', "sqlite")).toBe("UPDATE Order Lines");
    expect(statementTitle("DELETE FROM shop.`orders` WHERE id = 1", "mySql")).toBe("DELETE shop.orders");
    expect(statementTitle("# note\nSELECT * FROM `t`", "mySql")).toBe("SELECT t");
  });
});

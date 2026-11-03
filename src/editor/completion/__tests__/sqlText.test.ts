import { describe, it, expect } from "vitest";
import { IDX, at } from "./fixture";
import { detectUse, identifierAt, resolveObjectAt, statementBoundsAt, undeclaredParameters } from "../sqlText";

const bounds = (sql: string, dialect: "tsql" | "pg" = "tsql") => {
  const { text, offset } = at(sql);
  const b = statementBoundsAt(text, offset, dialect);
  return text.slice(b.start, b.end);
};

describe("statementBoundsAt", () => {
  it("semicolon-separated", () => {
    expect(bounds("SELECT 1; SELECT * FROM dbo.Or|ders; SELECT 3;")).toBe("SELECT * FROM dbo.Orders");
  });
  it("right after the semicolon → the statement it terminates", () => {
    expect(bounds("SELECT 1;\nSELECT 2;|\nSELECT 3")).toBe("SELECT 2");
  });
  it("GO batches", () => {
    expect(bounds("SELECT 1\nGO\nUPDATE dbo.Orders SET Status = 'x' WHERE Ord|erId = 1\nGO\nSELECT 3")).toBe("UPDATE dbo.Orders SET Status = 'x' WHERE OrderId = 1");
  });
  it("no semicolons", () => {
    expect(bounds("SELECT * FROM dbo.Customers\nUPDATE dbo.Orders SET Sta|tus = 'a'\nDELETE FROM dbo.Orders")).toBe("UPDATE dbo.Orders SET Status = 'a'");
  });
  it("inside a procedure body", () => {
    const sql = "CREATE PROCEDURE p AS\nBEGIN\n  SET NOCOUNT ON\n  SELECT * FROM dbo.Orders o WHERE o.Sta|tus = 'a'\n  RETURN 0\nEND";
    expect(bounds(sql)).toBe("SELECT * FROM dbo.Orders o WHERE o.Status = 'a'");
  });
  it("BEGIN TRAN / COMMIT around an UPDATE", () => {
    expect(bounds("BEGIN TRAN\nUPDATE dbo.Orders SET Status = 'x' |\nCOMMIT")).toBe("UPDATE dbo.Orders SET Status = 'x'");
  });
  it("cursor at the start of a statement", () => {
    expect(bounds("SELECT 1\n|SELECT 2")).toBe("SELECT 2");
  });
  it("WITH CTE + INSERT ... SELECT is one statement", () => {
    expect(bounds("WITH a AS (SELECT 1 AS x)\nINSERT INTO t (x)\nSELECT x FROM a|")).toBe("WITH a AS (SELECT 1 AS x)\nINSERT INTO t (x)\nSELECT x FROM a");
  });
  it("pg: $$ body is part of the CREATE FUNCTION statement", () => {
    const sql = "SELECT 1;\nCREATE FUNCTION f() RETURNS int AS $$ SELECT 1; $$ LANGUAGE sql|;\nSELECT 2;";
    expect(bounds(sql, "pg")).toBe("CREATE FUNCTION f() RETURNS int AS $$ SELECT 1; $$ LANGUAGE sql");
  });
  it("empty text", () => expect(statementBoundsAt("", 0, "tsql")).toEqual({ start: 0, end: 0 }));
});

describe("detectUse", () => {
  it("last USE wins, brackets stripped", () => {
    expect(detectUse("USE master;\nSELECT 1\nGO\nUSE [SalesDB]\nSELECT 2", "tsql")).toBe("SalesDB");
  });
  it("ignores comments and strings", () => {
    expect(detectUse("USE master\n-- USE other\nSELECT 'USE x'", "tsql")).toBe("master");
  });
  it("pg → undefined", () => expect(detectUse("USE x", "pg")).toBeUndefined());
});

describe("undeclaredParameters", () => {
  it("T-SQL: used but not declared, excluding @@globals", () => {
    expect(undeclaredParameters("DECLARE @a int = 1;\nSELECT * FROM t WHERE x = @a AND y = @b AND z = @@ROWCOUNT AND w = @C AND v = @b", "tsql")).toEqual(["@b", "@C"]);
  });
  it("T-SQL: procedure header parameters are declared", () => {
    expect(undeclaredParameters("CREATE PROCEDURE p @x int, @y int OUTPUT AS SELECT @x, @y, @z", "tsql")).toEqual(["@z"]);
  });
  it("T-SQL: named EXEC arguments are not variables", () => {
    expect(undeclaredParameters("EXEC dbo.usp_GetCustomerOrders @CustomerId = @cust, @MinTotal = @m OUTPUT", "tsql")).toEqual(["@cust", "@m"]);
  });
  it("T-SQL: declarations are batch-scoped", () => {
    expect(undeclaredParameters("DECLARE @a int\nSELECT @a\nGO\nSELECT @a", "tsql")).toEqual(["@a"]);
  });
  it("T-SQL: table variables and strings", () => {
    expect(undeclaredParameters("DECLARE @t TABLE (Id int); SELECT * FROM @t WHERE x = '@notvar'", "tsql")).toEqual([]);
  });
  it("pg: $n in numeric order then :name", () => {
    expect(undeclaredParameters("SELECT * FROM t WHERE a = $2 AND b = $1 AND c = :name AND d = $1::int AND e = '$3'", "pg")).toEqual(["$1", "$2", ":name"]);
  });
});

describe("identifierAt / resolveObjectAt", () => {
  const sql = "SELECT o.OrderId, @x FROM [dbo].[Orders] o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId";
  const pos = (needle: string, delta = 1) => sql.indexOf(needle) + delta;
  it("multi-part identifier", () => {
    expect(identifierAt(sql, pos("OrderId"), "tsql")).toEqual({ parts: ["o", "OrderId"], start: sql.indexOf("o.OrderId"), end: sql.indexOf("o.OrderId") + 9, partIndex: 1 });
    expect(identifierAt(sql, pos("[Orders]"), "tsql")?.parts).toEqual(["dbo", "Orders"]);
    expect(identifierAt(sql, pos("SELECT"), "tsql")).toBeNull();
  });
  it("column through an alias", () => {
    const r = resolveObjectAt(sql, pos("OrderId"), "tsql", IDX)!;
    expect(r.column?.table.name).toBe("Orders");
    expect(r.column?.name).toBe("OrderId");
  });
  it("alias part resolves to the table", () => {
    expect(resolveObjectAt(sql, pos("o.OrderId", 0), "tsql", IDX)?.object?.name).toBe("Orders");
  });
  it("schema-qualified table", () => {
    expect(resolveObjectAt(sql, pos("Customers"), "tsql", IDX)?.object?.name).toBe("Customers");
  });
  it("variable", () => expect(resolveObjectAt(sql, pos("@x"), "tsql", IDX)).toEqual({ variable: "@x" }));
  it("procedure in EXEC and unknown names", () => {
    const e = "EXEC dbo.usp_GetCustomerOrders @CustomerId = 1";
    expect(resolveObjectAt(e, e.indexOf("usp_") + 2, "tsql", IDX)?.object?.kind).toBe("proc");
    expect(resolveObjectAt("SELECT nothing FROM nowhere", 9, "tsql", IDX)).toBeNull();
  });
});

describe("SQLite and MySQL text helpers", () => {
  it("SQLite parameters: ?, ?NNN, :name, @name, $name in SQLite's index order, each once", () => {
    expect(undeclaredParameters("SELECT * FROM t WHERE a = ? AND b = :b AND c = ? AND d = @d AND e = $e AND f = :b", "sqlite"))
      .toEqual(["?1", ":b", "?3", "@d", "$e"]);
    expect(undeclaredParameters("SELECT ?3, ?, ?1", "sqlite")).toEqual(["?1", "?3", "?4"]);
    expect(undeclaredParameters("SELECT '?' , \"?x\" -- ?\n", "sqlite")).toEqual([]);
  });
  it("DuckDB parameters: ? by position (?1, ?2…), then $n in order, then $name once, case-insensitively", () => {
    expect(undeclaredParameters("SELECT ?, ? FROM t", "duckDb")).toEqual(["?1", "?2"]);
    expect(undeclaredParameters("SELECT $2, $1, $Name, $name, '$x', $t$ $y $t$", "duckDb")).toEqual(["$1", "$2", "$Name"]);
    expect(resolveObjectAt("SELECT $id", 9, "duckDb", null)).toEqual({ variable: "$id" });
  });
  it("MySQL asks for no parameters: @x is a user variable the script sets itself", () => {
    expect(undeclaredParameters("SET @x = 1; SELECT @x, ?", "mySql")).toEqual([]);
  });
  it("USE: MySQL switches the database (backticks stripped); SQLite has no USE", () => {
    expect(detectUse("USE shop;\nSELECT 1;\nUSE `Sales DB`;", "mySql")).toBe("Sales DB");
    expect(detectUse("USE main;", "sqlite")).toBeUndefined();
  });
  it("hover reads SQLite parameters and MySQL @variables as variables", () => {
    expect(resolveObjectAt("SELECT :id", 9, "sqlite", null)).toEqual({ variable: ":id" });
    expect(resolveObjectAt("SELECT @n", 8, "mySql", null)).toEqual({ variable: "@n" });
  });
  it("statement bounds split SQLite and MySQL scripts at semicolons", () => {
    const text = "SELECT 1;\nSELECT `a;b` FROM t;";
    const b = statementBoundsAt(text, text.indexOf("FROM"), "mySql");
    expect(text.slice(b.start, b.end)).toBe("SELECT `a;b` FROM t");
    const s = "INSERT INTO t VALUES (';');\nDELETE FROM t";
    const c = statementBoundsAt(s, s.length - 2, "sqlite");
    expect(s.slice(c.start, c.end)).toBe("DELETE FROM t");
  });
  // Regression: FROM is a DuckDB statement keyword (FROM-first queries), and the statement at the cursor in the FROM or
  // WHERE clause was cut down to `FROM t WHERE …` — which DuckDB runs as SELECT * (Ctrl+Shift+Enter ran another query).
  it("DuckDB: FROM, PIVOT and UNPIVOT inside a statement belong to it; a FROM-first query keeps its SELECT", () => {
    const duck = (text: string, offset: number) => {
      const b = statementBoundsAt(text, offset, "duckDb");
      return text.slice(b.start, b.end);
    };
    const q = "SELECT a FROM t WHERE x = 1;\nDELETE FROM t WHERE id = 2;";
    for (const at of [3, q.indexOf("FROM"), q.indexOf("x =")]) expect(duck(q, at)).toBe("SELECT a FROM t WHERE x = 1");
    expect(duck(q, q.indexOf("DELETE"))).toBe("DELETE FROM t WHERE id = 2");
    expect(duck(q, q.lastIndexOf("id"))).toBe("DELETE FROM t WHERE id = 2");
    const p = "SELECT * FROM t PIVOT (sum(v) FOR k IN ('a'));\nFROM t SELECT a, b;\nFROM u;\nPIVOT t ON k USING sum(v);";
    expect(duck(p, p.indexOf("PIVOT") + 2)).toBe("SELECT * FROM t PIVOT (sum(v) FOR k IN ('a'))");
    expect(duck(p, p.indexOf("SELECT a"))).toBe("FROM t SELECT a, b");
    expect(duck(p, p.indexOf("FROM u") + 1)).toBe("FROM u");
    expect(duck(p, p.length - 3)).toBe("PIVOT t ON k USING sum(v)");
    // Other dialects never list FROM as a statement keyword: unchanged.
    expect(statementBoundsAt("SELECT a FROM t", 12, "pg")).toEqual({ start: 0, end: 15 });
  });
});

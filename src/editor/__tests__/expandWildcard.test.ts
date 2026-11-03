import { describe, it, expect } from "vitest";
import { IDX } from "../completion/__tests__/fixture";
import { expandWildcardAtOffset } from "../expandWildcard";

describe("expandWildcardAtOffset", () => {
  it("expands single-table SELECT * to bare column names", () => {
    const sql = "SELECT * FROM dbo.Orders";
    const offset = sql.indexOf("*");
    const exp = expandWildcardAtOffset(sql, offset, "tsql", IDX);
    expect(exp).not.toBeNull();
    expect(exp?.text).toBe("OrderId, CustomerId, OrderDate, TotalAmount, Status");
    expect(sql.slice(exp!.start, exp!.end)).toBe("*");
  });

  it("expands multi-table SELECT * with table/alias prefixes", () => {
    const sql = "SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId";
    const offset = sql.indexOf("*");
    const exp = expandWildcardAtOffset(sql, offset, "tsql", IDX);
    expect(exp).not.toBeNull();
    expect(exp?.text).toContain("o.OrderId, o.CustomerId");
    expect(exp?.text).toContain("c.CustomerId, c.FullName");
  });

  it("expands qualified alias wildcard o.* to that table's columns only", () => {
    const sql = "SELECT o.* FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId";
    const offset = sql.indexOf("*");
    const exp = expandWildcardAtOffset(sql, offset, "tsql", IDX);
    expect(exp).not.toBeNull();
    expect(exp?.text).toBe("o.OrderId, o.CustomerId, o.OrderDate, o.TotalAmount, o.Status");
    expect(sql.slice(exp!.start, exp!.end)).toBe("o.*");
  });

  it("expands CTE columns if defined", () => {
    const sql = "WITH cte (Id, Val) AS (SELECT 1, 2) SELECT * FROM cte";
    const offset = sql.lastIndexOf("*");
    const exp = expandWildcardAtOffset(sql, offset, "tsql", IDX);
    expect(exp).not.toBeNull();
    expect(exp?.text).toBe("Id, Val");
  });

  it("returns null when not in a SELECT clause", () => {
    const sql = "SELECT 5 * 10 FROM dbo.Orders";
    const offset = sql.indexOf("*");
    const exp = expandWildcardAtOffset(sql, offset, "tsql", IDX);
    expect(exp).toBeNull();
    // 5 * 10 is multiplication, not SELECT * projection
    // Even if clause is select, the preceding token is 5 (number), not SELECT or comma
    // Let's verify how expandWildcardAtOffset handles this or non-SELECT
    const nonSelect = "DELETE FROM dbo.Orders WHERE OrderId = 1";
    expect(expandWildcardAtOffset(nonSelect, 5, "tsql", IDX)).toBeNull();
  });
});


describe("wildcard safety", () => {
  it.each(["tsql", "pg"] as const)("rejects expressions, function stars and remote/unknown tables (%s)", (dialect) => {
    for (const sql of ["SELECT COUNT(*) FROM dbo.Orders", "SELECT 5 * 10 FROM dbo.Orders", "SELECT * FROM missing", "SELECT * FROM dbo.Orders JOIN missing m ON 1=1", "SELECT * FROM otherdb.dbo.Orders"]) {
      expect(expandWildcardAtOffset(sql, sql.indexOf("*"), dialect, IDX), sql).toBeNull();
    }
  });
  it("rejects a cursor beyond the star", () => {
    const sql = "SELECT *   FROM dbo.Orders";
    expect(expandWildcardAtOffset(sql, sql.indexOf("*") + 3, "tsql", IDX)).toBeNull();
  });
  it("accepts whitespace around a qualifier dot", () => {
    const sql = "SELECT o . * FROM dbo.Orders o";
    const result = expandWildcardAtOffset(sql, sql.indexOf("*"), "tsql", IDX);
    expect(result?.text).toContain("o.OrderId");
    expect(sql.slice(result!.start, result!.end)).toBe("o . *");
  });
});

it("does not include nested query tables in an outer wildcard", () => {
  const sql = "SELECT * FROM dbo.Orders o WHERE EXISTS (SELECT 1 FROM dbo.Customers c WHERE c.CustomerId=o.CustomerId)";
  const result = expandWildcardAtOffset(sql, sql.indexOf("*"), "tsql", IDX);
  expect(result?.text).toBe("OrderId, CustomerId, OrderDate, TotalAmount, Status");
});

it("skips an earlier scalar subquery when finding the wildcard's SELECT", () => {
  const sql = "SELECT (SELECT TOP 1 CustomerId FROM dbo.Customers), * FROM dbo.Orders";
  expect(expandWildcardAtOffset(sql, sql.indexOf("*"), "tsql", IDX)?.text).toBe("OrderId, CustomerId, OrderDate, TotalAmount, Status");
});
it("folds unquoted PostgreSQL aliases and table names without changing their meaning", async () => {
  const { PG_IDX } = await import("../completion/__tests__/fixture");
  const sql = "SELECT O.* FROM public.ORDERS O";
  const result = expandWildcardAtOffset(sql, sql.indexOf("*"), "pg", PG_IDX);
  expect(result?.text).toContain("o.order_id");
  const wrongCase = 'SELECT * FROM public."Orders"';
  expect(expandWildcardAtOffset(wrongCase, wrongCase.indexOf("*"), "pg", PG_IDX)).toBeNull();
});

describe("projection head before the star", () => {
  const cols = "OrderId, CustomerId, OrderDate, TotalAmount, Status";
  it.each([
    "SELECT TOP 10 * FROM dbo.Orders",
    "SELECT TOP (10) * FROM dbo.Orders",
    "SELECT DISTINCT TOP (5) PERCENT * FROM dbo.Orders",
    "SELECT TOP (5) WITH TIES * FROM dbo.Orders ORDER BY OrderDate",
  ])("expands %s", (sql) => {
    expect(expandWildcardAtOffset(sql, sql.indexOf("*"), "tsql", IDX)?.text).toBe(cols);
  });
  it("expands after PostgreSQL DISTINCT ON (…)", async () => {
    const { PG_IDX } = await import("../completion/__tests__/fixture");
    const sql = "SELECT DISTINCT ON (customer_id) * FROM public.orders";
    expect(expandWildcardAtOffset(sql, sql.indexOf("*"), "pg", PG_IDX)?.text).toContain("order_id");
  });
  it.each([
    "SELECT TOP 10 a * FROM dbo.Orders",
    "SELECT OrderId * FROM dbo.Orders",
    "SELECT 2 * FROM dbo.Orders",
  ])("still refuses multiplication-like %s", (sql) => {
    expect(expandWildcardAtOffset(sql, sql.indexOf("*"), "tsql", IDX)).toBeNull();
  });
});

import { describe, it, expect } from "vitest";
import { IDX } from "../completion/__tests__/fixture";
import { tier1SignatureHelp } from "../signature";

describe("tier1SignatureHelp", () => {
  it("resolves stored procedure signature help after EXEC", () => {
    // In fixture.ts, usp_GetCustomerOrders has params: @CustomerId int, @StartDate datetime2 = default, @MinTotal decimal(18,2) OUTPUT
    const sql = "EXEC dbo.usp_GetCustomerOrders ";
    const offset = sql.length;
    const help = tier1SignatureHelp(sql, offset, "tsql", IDX);
    expect(help).not.toBeNull();
    expect(help?.label).toContain("usp_GetCustomerOrders");
    expect(help?.activeParameter).toBe(0);
    expect(help?.parameters).toHaveLength(3);
    expect(help?.parameters[0].label).toContain("@CustomerId");
  });

  it("updates activeParameter when typing commas after procedure name", () => {
    const sql = "EXEC dbo.usp_GetCustomerOrders 123, ";
    const offset = sql.length;
    const help = tier1SignatureHelp(sql, offset, "tsql", IDX);
    expect(help).not.toBeNull();
    expect(help?.activeParameter).toBe(1);
    expect(help?.parameters[1].label).toContain("@StartDate");
  });

  it("resolves function signature help inside parentheses", () => {
    // In fixture.ts, fn_OrderTotal has param: @OrderId bigint
    const sql = "SELECT dbo.fn_OrderTotal(";
    const offset = sql.length;
    const help = tier1SignatureHelp(sql, offset, "tsql", IDX);
    expect(help).not.toBeNull();
    expect(help?.label).toContain("fn_OrderTotal");
    expect(help?.activeParameter).toBe(0);
    expect(help?.parameters[0].label).toContain("@OrderId");
  });

  it("returns null when no matching function or procedure is found", () => {
    const sql = "SELECT non_existent_fn(";
    const offset = sql.length;
    expect(tier1SignatureHelp(sql, offset, "tsql", IDX)).toBeNull();
  });
});

it("handles PostgreSQL CALL and keeps the active parameter before an existing closing parenthesis", async () => {
  const { PG_IDX } = await import("../completion/__tests__/fixture");
  const sql = "CALL public.refresh_stats('2026-01-01', )";
  expect(tier1SignatureHelp(sql, sql.indexOf(")"), "pg", PG_IDX)?.activeParameter).toBe(1);
});
it("does not reuse a signature from a preceding statement", () => {
  const sql = "SELECT dbo.fn_OrderTotal(; SELECT 1";
  expect(tier1SignatureHelp(sql, sql.length, "tsql", IDX)).toBeNull();
});

it("provides PostgreSQL signatures within a routine body", async () => {
  const { PG_IDX } = await import("../completion/__tests__/fixture");
  const sql = "DO $$ BEGIN CALL public.refresh_stats('2026-01-01', ); END $$";
  expect(tier1SignatureHelp(sql, sql.indexOf(")"), "pg", PG_IDX)?.activeParameter).toBe(1);
});

it("resolves named parameters instead of assuming positional order", async () => {
  const sql = "EXEC dbo.usp_GetCustomerOrders @MinTotal = 1";
  expect(tier1SignatureHelp(sql, sql.length, "tsql", IDX)?.activeParameter).toBe(2);
  const { PG_IDX } = await import("../completion/__tests__/fixture");
  const pg = "CALL public.refresh_stats(p_force => true";
  expect(tier1SignatureHelp(pg, pg.length, "pg", PG_IDX)?.activeParameter).toBe(1);
});
it("does not guess the signature of another database's function", () => {
  const sql = "SELECT otherdb.dbo.fn_OrderTotal(";
  expect(tier1SignatureHelp(sql, sql.length, "tsql", IDX)).toBeNull();
});

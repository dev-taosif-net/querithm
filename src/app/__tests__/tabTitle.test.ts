import { describe, expect, it } from "vitest";
import { isDebugTitle, middleCut, objectLabel, tabName, tabTitleText } from "../tabTitle";
import { txAgeLong, txAgeShort } from "../txAge";

describe("tab title text", () => {
  it("drops .sql and the debugger marker, nothing else", () => {
    expect(tabTitleText("fix_invoice_totals.sql")).toBe("fix_invoice_totals");
    expect(tabTitleText("Report.SQL")).toBe("Report");
    expect(tabTitleText("🐞 usp_PostOrders")).toBe("usp_PostOrders");
    expect(tabTitleText("notes.sql.bak")).toBe("notes.sql.bak");
    expect(tabTitleText(".sql")).toBe(".sql");
    expect(tabTitleText("Q3 revenue (copy).sql")).toBe("Q3 revenue (copy)");
    expect(isDebugTitle("🐞 x")).toBe(true);
    expect(isDebugTitle("x")).toBe(false);
  });
});

describe("tab name", () => {
  it.each([
    ["DROP Orders.sql", "DROP", true, "Orders"],
    ["DROP and CREATE sales.Orders.sql", "DROP and CREATE", true, "sales.Orders"],
    ["DELETE Invoice.sql", "DELETE", true, "Invoice"],
    ["TRUNCATE stage.Load.sql", "TRUNCATE", true, "stage.Load"],
    ["CREATE orders_daily.sql", "CREATE", false, "orders_daily"],
    ["EXEC usp_PostOrders.sql", "EXEC", false, "usp_PostOrders"],
    ["UPDATE Invoice", "UPDATE", false, "Invoice"],
  ])("%s: verb %s (danger %s), name %s", (title, verb, danger, name) => {
    expect(tabName(title)).toEqual({ verb, danger, name });
  });
  it.each(["Dropbox report.sql", "drop old rows", "SELECT", "SQLQuery5.sql", "Session 53.sql", "DROP "])("%s has no verb", (title) => {
    const n = tabName(title);
    expect(n.verb).toBeUndefined();
    expect(n.danger).toBe(false);
    expect(n.name).toBe(tabTitleText(title));
  });
});

describe("middle cut", () => {
  it("keeps the last characters whole, by code point", () => {
    expect(middleCut("reconcile_invoice_payments_2026_10", 7)).toEqual(["reconcile_invoice_payments_", "2026_10"]);
    expect(middleCut("short", 7)).toEqual(["short", ""]);
    expect(middleCut("inventory", 0)).toEqual(["inventory", ""]);
    expect(middleCut("Bericht für Größe😀", 3)).toEqual(["Bericht für Grö", "ße😀"]);
  });
});

describe("object label in script titles", () => {
  it.each([
    ["dbo", "Orders", "mssql", "Orders"],
    ["sales", "Orders", "mssql", "sales.Orders"],
    ["public", "orders", "pg", "orders"],
    ["dbo", "orders", "pg", "dbo.orders"],
    ["public", "Orders", "mssql", "public.Orders"],
    [null, "Orders", "mssql", "Orders"],
    ["dbo", "Orders", undefined, "dbo.Orders"],
  ] as const)("%s.%s on %s → %s", (schema, name, engine, label) => {
    expect(objectLabel(schema, name, engine)).toBe(label);
  });
});

describe("transaction age", () => {
  it.each([
    [0, "", "less than a minute"],
    [59_999, "", "less than a minute"],
    [60_000, "1m", "1 min"],
    [12 * 60_000 + 59_000, "12m", "12 min"],
    [3_599_999, "59m", "59 min"],
    [3_600_000, "1h", "1 h"],
    [2 * 3_600_000 + 5 * 60_000, "2h", "2 h 5 min"],
    [Number.NaN, "", "less than a minute"],
  ])("%d ms: %s / %s", (ms, short, long) => {
    expect(txAgeShort(ms)).toBe(short);
    expect(txAgeLong(ms)).toBe(long);
  });
});

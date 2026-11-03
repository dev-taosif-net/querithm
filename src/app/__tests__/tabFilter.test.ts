import { describe, expect, it } from "vitest";
import type { Engine, Profile } from "../../ipc/contract";
import type { TabState } from "../../store/tabs";
import { groupByConnection, matchesTab, type TabRow } from "../tabFilter";

const prof = (id: string, engine: Engine, over: Partial<Profile> = {}) => ({ id, name: id, engine, environment: "dev", host: "localhost", ...over }) as Profile;
const row = (id: string, profile?: Profile, over: Partial<TabState> = {}): TabRow => ({ id, tab: { id, title: `${id}.sql`, profileId: profile?.id, ...over } as TabState, profile });

const ms = prof("Bench", "mssql", { environment: "prod", host: "sql01" });
const pg = prof("PG fixture", "pg", { groupName: "Analytics" });

describe("open tabs: search", () => {
  it("matches title, file path and database", () => {
    const r = row("Orders", ms, { filePath: "/work/reports/orders.sql", database: "SalesDB" });
    for (const q of ["orders", "reports", "salesdb", "ORD sales"]) expect(matchesTab(r, q)).toBe(true);
    expect(matchesTab(r, "invoices")).toBe(false);
  });
  it("matches the connection name, host, group and environment", () => {
    expect(matchesTab(row("q", ms), "bench sql01 prod")).toBe(true);
    expect(matchesTab(row("q", pg), "analytics")).toBe(true);
  });
  it.each<[Engine, string]>([["mssql", "sql server"], ["pg", "postgres"]])("finds %s tabs by engine name only", (engine, word) => {
    const rows = [row("a", ms), row("b", pg)];
    expect(rows.filter((r) => matchesTab(r, word)).map((r) => r.profile?.engine)).toEqual([engine]);
  });
  it("unconnected tabs match 'no connection'", () => {
    expect(matchesTab(row("q"), "no conn")).toBe(true);
    expect(matchesTab(row("q", ms), "no conn")).toBe(false);
  });
});

describe("open tabs: grouping", () => {
  it("one group per connection in first-seen order, keeping row order, unconnected last", () => {
    const groups = groupByConnection([row("1"), row("2", pg), row("3", ms), row("4", pg), row("5")]);
    expect(groups.map((g) => [g.profile?.id ?? "-", g.rows.map((r) => r.tab.id)])).toEqual([["PG fixture", ["2", "4"]], ["Bench", ["3"]], ["-", ["1", "5"]]]);
  });
});

describe("open tabs: search in SQL text and SPID (D18)", () => {
  it("matches the start of the tab's SQL and its session id", () => {
    const r = row("a", undefined, { spid: 53 });
    expect(matchesTab(r, "invoice", "update dbo.invoice set total = 1")).toBe(true);
    expect(matchesTab(r, "invoice")).toBe(false);
    expect(matchesTab(r, "53")).toBe(true);
    expect(matchesTab(r, "spid 53")).toBe(true);
    expect(matchesTab(row("b"), "53")).toBe(false);
  });
});

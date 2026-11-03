import { describe, it, expect, vi, beforeEach } from "vitest";
import type { CompareItem } from "../../ipc/contract";
import { buildRows, counts, defaultSelection, filterItems, itemName, renameKey, sideReady, sideTitle, swapSides, whitespaceOnly } from "../schemaCompare";

const { sessionFor } = vi.hoisted(() => ({ sessionFor: vi.fn(async (id: string) => `session-of-${id}`) }));
vi.mock("../../store/connections", () => ({ useConnections: { getState: () => ({ sessionFor }) } }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn() }));
vi.mock("../../ipc/transport", () => ({ isTauri: false, transport: () => ({ invoke: vi.fn() }) }));
vi.mock("../../app/actions", () => ({ activeTabState: () => undefined }));
vi.mock("../../app/toolActions", () => ({ openTool: vi.fn() }));
vi.mock("../../app/saveBytes", () => ({ saveBytes: vi.fn() }));
vi.mock("../../store/ui", () => ({ toast: vi.fn() }));

const item = (over: Partial<CompareItem>): CompareItem => ({
  key: over.key ?? `${over.kind ?? "table"}:dbo.${over.name ?? "t"}`, kind: "table", schema: "dbo", name: "t", class: "different", tags: ["columns"], changes: [], dataLoss: false, ...over,
});

const items: CompareItem[] = [
  item({ name: "orders", class: "different", tags: ["columns"] }),
  item({ name: "old", class: "targetOnly", tags: [], dataLoss: true }),
  item({ name: "new", class: "sourceOnly", tags: [] }),
  item({ name: "same", class: "identical", tags: [] }),
  item({ kind: "view", name: "v_ws", class: "different", tags: ["whitespaceOnly"] }),
  item({ kind: "function", name: "f", class: "different", tags: ["body"], detail: "x integer" }),
  item({ kind: "trigger", name: "t_ins", class: "sourceOnly", tags: [], detail: "orders" }),
];

describe("schema compare: list", () => {
  it("filters by class, whitespace-only, identical and name", () => {
    const f = { cls: "all" as const, hideWhitespace: false, hideIdentical: true, search: "" };
    expect(filterItems(items, f).map((i) => i.name)).not.toContain("same");
    expect(filterItems(items, { ...f, hideIdentical: false }).map((i) => i.name)).toContain("same");
    expect(filterItems(items, { ...f, hideWhitespace: true }).map((i) => i.name)).not.toContain("v_ws");
    expect(filterItems(items, { ...f, cls: "targetOnly" }).map((i) => i.name)).toEqual(["old"]);
    expect(filterItems(items, { ...f, search: "ORD" }).map((i) => i.name)).toEqual(["orders"]);
  });

  it("groups by kind or by action, and collapsed groups keep only their header", () => {
    const byKind = buildRows(items, "kind", new Set());
    expect(byKind.filter((r) => r.t === "group").map((r) => r.t === "group" && r.label)).toEqual(["Tables", "Views", "Functions", "Triggers"]);
    const byAction = buildRows(items, "action", new Set(["g:identical"]));
    expect(byAction.filter((r) => r.t === "group").map((r) => r.t === "group" && r.label)).toEqual(["Create", "Change", "Drop", "Identical"]);
    expect(byAction.some((r) => r.t === "item" && r.item.name === "same")).toBe(false);
    const tables = byKind.find((r) => r.t === "group" && r.label === "Tables");
    expect(tables?.t === "group" && tables.count).toBe(4);
  });

  it("checks everything that differs except cosmetic changes", () => {
    const sel = defaultSelection(items);
    expect(sel.has("table:dbo.orders")).toBe(true);
    expect(sel.has("table:dbo.old")).toBe(true);
    expect(sel.has("table:dbo.same")).toBe(false);
    expect(sel.has("view:dbo.v_ws")).toBe(false);
    expect(whitespaceOnly(items[4])).toBe(true);
    expect(whitespaceOnly(items[0])).toBe(false);
  });

  it("counts, names and rename keys", () => {
    expect(counts(items)).toEqual({ identical: 1, different: 3, sourceOnly: 2, targetOnly: 1 });
    expect(itemName(items[5])).toBe("dbo.f(x integer)");
    expect(itemName(items[6])).toBe("dbo.t_ins on orders");
    expect(renameKey({ tableKey: "Table:dbo.t", from: "a", to: "b", type: "int" })).toBe("Table:dbo.t|a|b");
  });
});

describe("schema compare: sides", () => {
  beforeEach(() => sessionFor.mockClear());

  it("is ready with a connection and database, or a file", () => {
    expect(sideReady({ kind: "live" })).toBe(false);
    expect(sideReady({ kind: "live", profileId: "p" })).toBe(false);
    expect(sideReady({ kind: "live", profileId: "p", database: "db" })).toBe(true);
    expect(sideReady({ kind: "file", token: "t", name: "x.qschema.json" })).toBe(true);
    expect(sideTitle({ kind: "live", profileId: "p", database: "db", schema: "sales" }, () => "Prod")).toBe("Prod · db.sales");
  });

  it("swaps source and target", () => {
    const a = { kind: "live" as const, profileId: "a", database: "x" };
    const b = { kind: "file" as const, token: "t", name: "f" };
    expect(swapSides({ source: a, target: b })).toEqual({ source: b, target: a });
  });

  it("sends live sides on the explorer session and files as a token only (never a path)", async () => {
    const { sideParams } = await import("../compareActions");
    expect(await sideParams({ kind: "live", profileId: "p1", database: "Sales", schema: "dbo" })).toEqual({ sessionId: "session-of-p1", database: "Sales", schema: "dbo" });
    expect(sessionFor).toHaveBeenCalledWith("p1");
    const file = await sideParams({ kind: "file", token: "tok", name: "a.qschema.json" });
    expect(file).toEqual({ fileToken: "tok" });
    expect(JSON.stringify(file)).not.toContain("filePath");
    await expect(sideParams({ kind: "live", profileId: "p1" })).rejects.toThrow(/database/);
  });
});

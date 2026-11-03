// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render } from "@testing-library/react";
import { erdSnapshot } from "./fixture";
import { buildGraph } from "../erdLayout";
import { buildSvg, svgToPng } from "../erdExport";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useProfiles } from "../../store/profiles";
import { useTabs } from "../../store/tabs";
import { useToolTabs } from "../../store/toolTabs";

const m = vi.hoisted(() => ({ openTool: vi.fn(), activeTabState: vi.fn(), toast: vi.fn(), getModel: vi.fn(), editorForTab: vi.fn() }));
vi.mock("@xyflow/react", async () => (await import("./fakeFlow")).flowMock);
vi.mock("../../app/toolActions", () => ({ openTool: m.openTool }));
vi.mock("../../app/actions", () => ({ activeTabState: m.activeTabState }));
vi.mock("../../store/ui", () => ({ toast: m.toast }));
vi.mock("../../editor/editors", () => ({ getModel: m.getModel, editorForTab: m.editorForTab }));

const actions = await import("../erdActions");
const { TableNodeCard, handleId } = await import("../TableNodeCard");

beforeEach(() => vi.clearAllMocks());
afterEach(cleanup);

describe("erdActions", () => {
  it("opens an ER diagram around a table and for a database or one schema", () => {
    actions.openErdForTable("p", "db", 5, "Orders");
    expect(m.openTool).toHaveBeenCalledWith("erd", "ERD · Orders", { profileId: "p", database: "db", params: { mode: "neighbourhood", rootId: 5 } });
    actions.openErdForDatabase("p", "db");
    expect(m.openTool).toHaveBeenLastCalledWith("erd", "ERD · db", { profileId: "p", database: "db", params: { mode: "schema", schema: null } });
    actions.openErdForDatabase("p", "db", "sales");
    expect(m.openTool).toHaveBeenLastCalledWith("erd", "ERD · sales", { profileId: "p", database: "db", params: { mode: "schema", schema: "sales" } });
  });

  it("queryTableIds is empty without a cached schema", () => {
    useSnapshots.setState({ entries: {} });
    expect(actions.queryTableIds("select 1", 0, "tsql", "p", "db")).toEqual([]);
  });

  describe("Ctrl+Shift+E", () => {
    const orders = { id: 2, schema: "dbo", name: "Orders", kind: "table", columns: [] };
    const idx = (dialect: "tsql" | "pg") => ({
      profileId: "p", database: "db", dialect, objects: [orders], byName: new Map([["orders", [orders]]]), byQualified: new Map([["dbo.orders", orders]]),
      byId: new Map([[2, orders]]), schemas: ["dbo"], fks: [], databases: [], defaultSchema: dialect === "pg" ? "public" : "dbo",
    });
    const withEditor = (text: string, offset: number) => {
      m.getModel.mockReturnValue({ getValue: () => text, getOffsetAt: () => offset });
      m.editorForTab.mockReturnValue({ getPosition: () => ({ lineNumber: 1, column: offset + 1 }) });
    };

    it("asks for a connection when the tab has none", () => {
      m.activeTabState.mockReturnValue({ id: "t" });
      actions.openErdFromActiveTab();
      expect(m.toast).toHaveBeenCalledWith("info", "Pick a connection and database for this tab first.");
      m.activeTabState.mockReturnValue(undefined);
      actions.openErdFromActiveTab();
      expect(m.toast).toHaveBeenCalledTimes(2);
      expect(m.openTool).not.toHaveBeenCalled();
    });

    it("opens query mode with the statement's tables (T-SQL)", () => {
      m.activeTabState.mockReturnValue({ id: "t", profileId: "p", database: "db" });
      useProfiles.setState({ profiles: [{ id: "p", engine: "mssql" } as never] });
      useSnapshots.setState({ entries: { [snapKey("p", "db")]: { key: "k", index: idx("tsql") } as never } });
      withEditor("select * from dbo.Orders", 10);
      actions.openErdFromActiveTab();
      expect(m.openTool).toHaveBeenCalledWith("erd", "ERD · query", { profileId: "p", database: "db", params: { mode: "query", queryTables: [2] } });
    });

    it("works for PostgreSQL profiles too", () => {
      m.activeTabState.mockReturnValue({ id: "t", profileId: "p", database: "db" });
      useProfiles.setState({ profiles: [{ id: "p", engine: "pg" } as never] });
      const o = { ...orders, schema: "public" };
      const i = { ...idx("pg"), objects: [o], byName: new Map([["orders", [o]]]), byQualified: new Map([["public.orders", o]]) };
      useSnapshots.setState({ entries: { [snapKey("p", "db")]: { key: "k", index: i } as never } });
      withEditor("select * from public.orders", 10);
      actions.openErdFromActiveTab();
      expect(m.openTool).toHaveBeenCalledWith("erd", "ERD · query", expect.objectContaining({ params: { mode: "query", queryTables: [2] } }));
    });

    it("falls back to the whole database when the statement has no known tables or there is no editor", () => {
      m.activeTabState.mockReturnValue({ id: "t", profileId: "p", database: "db" });
      useSnapshots.setState({ entries: { [snapKey("p", "db")]: { key: "k", index: idx("tsql") } as never } });
      withEditor("select 1", 3);
      actions.openErdFromActiveTab();
      expect(m.openTool).toHaveBeenLastCalledWith("erd", "ERD · db", expect.objectContaining({ params: { mode: "schema", schema: null } }));
      m.getModel.mockReturnValue(undefined);
      actions.openErdFromActiveTab();
      expect(m.openTool).toHaveBeenCalledTimes(2);
    });
  });
});

describe("TableNodeCard", () => {
  const snap = erdSnapshot();
  const node = (id: number, hidden = 0) => ({ ...buildGraph(snap, new Set([id]), false).nodes[0], hidden });
  const data = (over: Record<string, unknown> = {}) => ({ table: node(2), highlight: new Set<string>(), selected: false, matched: false, dimmed: false, ...over }) as never;
  const card = (d: unknown) => render(<TableNodeCard {...({ data: d } as any)} />);

  it("renders schema-qualified name, markers, types and column count", () => {
    const { container } = card(data());
    expect(container.textContent).toContain("dbo.Orders");
    expect(container.textContent).toContain("2c");
    expect(container.querySelectorAll("[data-handle]").length).toBeGreaterThan(2);
    expect(container.querySelector('[title="NOT NULL"]')).not.toBeNull();
    expect(container.textContent).toContain("PK");
    expect(container.textContent).toContain("FK");
  });

  it("styles selected, matched and dimmed states", () => {
    expect(card(data({ selected: true })).container.querySelector(".ring-accent\\/60")).not.toBeNull();
    cleanup();
    expect(card(data({ matched: true })).container.querySelector(".ring-warn\\/50")).not.toBeNull();
    cleanup();
    expect(card(data({ dimmed: true })).container.querySelector(".opacity-40")).not.toBeNull();
  });

  it("shows exact and estimated row counts", () => {
    expect(card(data({ rowCount: { rows: 1500, estimated: false } })).container.textContent).toContain((1500).toLocaleString());
    cleanup();
    const est = card(data({ rowCount: { rows: 2000, estimated: true } }));
    expect(est.container.textContent).toContain("~" + (2000).toLocaleString());
  });

  it("offers an expand button for hidden neighbours, singular and plural", () => {
    const onExpand = vi.fn();
    const one = card(data({ table: node(2, 1), onExpand }));
    const btn = one.getByText("+1");
    expect(btn.getAttribute("title")).toBe("Show 1 more related table");
    btn.click();
    expect(onExpand).toHaveBeenCalledWith(2);
    cleanup();
    expect(card(data({ table: node(2, 3), onExpand })).getByText("+3").getAttribute("title")).toBe("Show 3 more related tables");
    cleanup();
    expect(card(data({ table: node(2, 3) })).container.textContent).not.toContain("+3");
  });

  it("explains a compact card without key columns", () => {
    const t = { ...node(2), columns: [] };
    expect(card(data({ table: t })).container.textContent).toContain("no key columns");
    expect(handleId("in", "OrderId")).toBe("in:orderid");
  });
});

describe("svgToPng", () => {
  const realCreate = URL.createObjectURL, realRevoke = URL.revokeObjectURL, RealImage = globalThis.Image;
  afterEach(() => {
    URL.createObjectURL = realCreate; URL.revokeObjectURL = realRevoke; globalThis.Image = RealImage;
    vi.restoreAllMocks();
  });
  function stub(opts: { loadFails?: boolean; blob?: Blob | null }) {
    URL.createObjectURL = vi.fn(() => "blob:x");
    URL.revokeObjectURL = vi.fn();
    globalThis.Image = class {
      width = 100; height = 50; onload: (() => void) | null = null; onerror: (() => void) | null = null;
      set src(_: string) { queueMicrotask(() => (opts.loadFails ? this.onerror?.() : this.onload?.())); }
    } as never;
    const ctx = { scale: vi.fn(), drawImage: vi.fn() };
    const canvas = { width: 0, height: 0, getContext: () => ctx, toBlob: (cb: (b: Blob | null) => void) => cb(opts.blob === undefined ? new Blob([new Uint8Array([9, 8, 7])]) : opts.blob) };
    const orig = document.createElement.bind(document);
    vi.spyOn(document, "createElement").mockImplementation((tag: string) => (tag === "canvas" ? (canvas as never) : orig(tag)));
    return { ctx, canvas };
  }

  it("rasterises the SVG at the requested scale and releases the object URL", async () => {
    const { ctx, canvas } = stub({});
    const bytes = await svgToPng("<svg/>", 3);
    expect([...bytes]).toEqual([9, 8, 7]);
    expect(canvas.width).toBe(300);
    expect(canvas.height).toBe(150);
    expect(ctx.scale).toHaveBeenCalledWith(3, 3);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:x");
  });
  it("fails visibly when the image cannot load or the canvas is too large", async () => {
    stub({ loadFails: true });
    await expect(svgToPng("<svg/>")).rejects.toThrow("could not be rendered");
    expect(URL.revokeObjectURL).toHaveBeenCalled();
    stub({ blob: null });
    await expect(svgToPng("<svg/>")).rejects.toThrow("too large");
  });
});

describe("buildSvg edge cases", () => {
  it("skips nodes the layout did not position", () => {
    const g = buildGraph(erdSnapshot(), new Set([1, 6]), false);
    const svg = buildSvg(g, [{ id: 1, x: 0, y: 0 }], { bg: "#000", card: "#111", header: "#222", line: "#333", fg: "#fff", muted: "#aaa", pk: "#f00", fk: "#00f", edge: "#999" });
    expect(svg).toContain("dbo.Customers");
    expect(svg).not.toContain("dbo.Isolated");
  });
});

void useTabs; void useToolTabs;

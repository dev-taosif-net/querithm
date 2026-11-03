// @vitest-environment jsdom
// Schema / data compare on Phase D engines. SQLite and DuckDB have neither: the sidecar's capabilities say why, and the
// tab shows that reason before anything runs (a file profile's explorer session knows it). An in-memory database has no
// explorer session: its side goes through the query tab session it was opened from (Phase D0d), reads that session's
// own snapshot, and the sidecar's refusal of the call is what the tab shows. Server engines keep the explorer's session
// and the shared snapshot.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createElement } from "react";
import type { Engine, Profile, SnapObject } from "../../ipc/contract";
import { useConnections, type ServerConn } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useTabs, type TabState } from "../../store/tabs";
import { useToolTabs, type ToolTab } from "../../store/toolTabs";

const m = vi.hoisted(() => ({ rpc: vi.fn(), toast: vi.fn(), newQueryTab: vi.fn(), activeTabState: vi.fn(), openTool: vi.fn(), sessionFor: vi.fn() }));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, onNotify: () => () => {}, shell: {},
  RpcError: class RpcError extends Error { constructor(message: string, readonly code = -32000, readonly appCode?: string) { super(message); } },
}));
vi.mock("../../ipc/transport", () => ({ isTauri: false, transport: () => ({ invoke: vi.fn() }) }));
vi.mock("../../store/ui", () => ({ toast: m.toast }));
vi.mock("../../app/actions", () => ({ newQueryTab: m.newQueryTab, activeTabState: m.activeTabState }));
vi.mock("../../app/toolActions", () => ({ openTool: m.openTool }));
vi.mock("../../app/saveBytes", () => ({ saveBytes: vi.fn() }));
vi.mock("../../editor/editors", () => ({ languageFor: () => "sql" }));
vi.mock("../../editor/monaco", () => {
  const model = () => ({ setValue: vi.fn(), getValue: () => "", dispose: vi.fn() });
  const editor = () => ({ setModel: vi.fn(), getModel: () => null, setValue: vi.fn(), dispose: vi.fn() });
  return { initMonaco: vi.fn(), monaco: { editor: { createModel: model, createDiffEditor: editor, create: editor } } };
});
vi.mock("../../results/useExecutionStream", () => ({ useExecutionStream: () => undefined }));
vi.mock("../../results/ResultGrid", () => ({ ResultGrid: () => createElement("div") }));

const { RpcError } = await import("../../ipc/client");
const { SchemaCompareTab } = await import("../SchemaCompareTab");
const { DataCompareTab } = await import("../DataCompareTab");
const A = await import("../compareActions");

const SQLITE_REASON = "SQLite has no schema compare in Querithm: compare its file with a schema model instead.";
const DUCKDB_REASON = "DuckDB has no data compare in Querithm yet.";

const profile = (engine: Engine, location: Profile["location"]) => ({ id: "p", name: "Local", engine, location, auth: "none" }) as never;
const caps = (o: { schemaCompare?: string; dataCompare?: string } = {}) => ({
  schemaCompare: { available: !o.schemaCompare, reason: o.schemaCompare ?? null },
  dataCompare: { available: !o.dataCompare, reason: o.dataCompare ?? null },
});
function servers(server: Partial<ServerConn>) {
  useConnections.setState({ servers: { p: { profileId: "p", status: "connected", databases: [], ...server } as ServerConn }, connect: vi.fn(), sessionFor: m.sessionFor } as never);
}
const tab = (o: Partial<TabState>) => useTabs.getState().add({ id: o.id ?? "t1", title: "q.sql", profileId: "p", ...o });
const obj = (schema: string, name: string, kind: SnapObject["kind"] = "table"): SnapObject => ({ id: name.length, schema, name, kind });
const snapshot = (database: string, objects: SnapObject[], sessionId?: string) => ({
  [snapKey("p", database, sessionId)]: { key: snapKey("p", database, sessionId), profileId: "p", database, sessionId, status: "fresh", version: 1, snapshot: { profileId: "p", database, status: "fresh", objects, fks: [] } } as never,
});
function schemaTab(side: Record<string, unknown>) {
  const tool: ToolTab = { id: "tool1", kind: "schemaCompare", title: "Schema compare", params: { source: { kind: "live", ...side }, target: { kind: "live", ...side } } };
  useToolTabs.setState({ tools: { tool1: tool } });
  return render(<SchemaCompareTab tool={tool} />);
}
function dataTab(side: Record<string, unknown>) {
  const tool: ToolTab = { id: "tool1", kind: "dataCompare", title: "Data compare", params: { source: { mode: "table", ...side }, target: { mode: "table", ...side } } };
  useToolTabs.setState({ tools: { tool1: tool } });
  return render(<DataCompareTab tool={tool} />);
}
const compareButton = () => screen.getByText("Compare") as HTMLButtonElement;
const options = (label: string) => within(screen.getByLabelText(label)).getAllByRole("option").map((o) => (o as HTMLOptionElement).value);

beforeEach(() => {
  vi.clearAllMocks();
  m.rpc.mockReset();
  m.sessionFor.mockResolvedValue("explorer");
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class { observe() {} disconnect() {} };
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useSnapshots.setState({ entries: {} });
});
afterEach(cleanup);

describe("schema compare", () => {
  it("a SQLite file: the sidecar's reason is shown and Compare stays off (no empty result)", () => {
    useProfiles.setState({ profiles: [profile("sqlite", "file")] });
    servers({ sessionId: "explorer", databases: [{ name: "main", isSystem: false } as never], caps: caps({ schemaCompare: SQLITE_REASON }) as never });
    schemaTab({ profileId: "p", database: "main" });
    expect(screen.getByRole("note").textContent).toBe(SQLITE_REASON);
    expect(compareButton().disabled).toBe(true);
    expect(compareButton().title).toBe(SQLITE_REASON);
    expect(m.rpc).not.toHaveBeenCalledWith("schemaCompare.run", expect.anything());
  });

  it("an in-memory DuckDB database: the tab session's schemas, the call goes through that session, the refusal is shown", async () => {
    useProfiles.setState({ profiles: [profile("duckDb", "memory")] });
    servers({ memory: true }); // no explorer session, so no capabilities to read up front
    tab({ sessionId: "s1", database: "memory" });
    useSnapshots.setState({ entries: snapshot("memory", [obj("main", "orders"), obj("crm", "region")], "s1") });
    m.rpc.mockRejectedValue(new RpcError("DuckDB has no schema compare in Querithm.", -32000, "unsupported"));
    schemaTab({ profileId: "p", database: "memory", schema: null, sessionId: "s1" });
    expect(options("Source database")).toContain("memory"); // listed nowhere else: each tab has its own
    expect(options("Source schema")).toEqual(["", "crm", "main"]);
    fireEvent.click(compareButton());
    expect((await screen.findByRole("alert")).textContent).toBe("DuckDB has no schema compare in Querithm.");
    const side = { sessionId: "s1", database: "memory", schema: null };
    expect(m.rpc).toHaveBeenCalledWith("schemaCompare.run", { source: side, target: side });
    expect(m.sessionFor).not.toHaveBeenCalled();
    expect(screen.queryByText(/checked ·/)).toBeNull();
  });

  it("an in-memory database whose tab session is gone is refused with the reason, never compared as a new empty one", async () => {
    useProfiles.setState({ profiles: [profile("sqlite", "memory")] });
    servers({ memory: true });
    useConnections.setState({ sessionFor: useConnections.getInitialState().sessionFor } as never); // the real one
    schemaTab({ profileId: "p", database: "main", sessionId: "gone" });
    fireEvent.click(compareButton());
    expect((await screen.findByRole("alert")).textContent).toMatch(/^In-memory: each tab has its own database/);
    expect(m.rpc).not.toHaveBeenCalled();
  });

  it("regression: PostgreSQL keeps the explorer's session and the shared snapshot, with no reason shown", async () => {
    useProfiles.setState({ profiles: [profile("pg", null)] });
    servers({ sessionId: "explorer", databases: [{ name: "shop", isSystem: false } as never], caps: caps() as never });
    tab({ sessionId: "s1", database: "shop" });
    useSnapshots.setState({ entries: { ...snapshot("shop", [obj("public", "orders"), obj("sales", "region")]), ...snapshot("shop", [obj("own", "x")], "s1") } });
    m.rpc.mockResolvedValue({ compareId: "c", source: {}, target: {}, items: [], possibleRenames: [], notes: [], transactionalDdl: true, elapsedMs: 1 });
    schemaTab({ profileId: "p", database: "shop", sessionId: "s1" });
    expect(screen.queryByRole("note")).toBeNull();
    expect(options("Source schema")).toEqual(["", "public", "sales"]);
    fireEvent.click(compareButton());
    await screen.findByText(/checked ·/);
    expect(m.rpc).toHaveBeenCalledWith("schemaCompare.run", { source: { sessionId: "explorer", database: "shop", schema: null }, target: { sessionId: "explorer", database: "shop", schema: null } });
  });
});

describe("data compare", () => {
  it("a DuckDB file: the sidecar's reason is shown and Compare stays off", () => {
    useProfiles.setState({ profiles: [profile("duckDb", "file")] });
    servers({ sessionId: "explorer", databases: [{ name: "sales", isSystem: false } as never], caps: caps({ dataCompare: DUCKDB_REASON }) as never });
    dataTab({ profileId: "p", database: "sales", table: "main.orders" });
    expect(screen.getByRole("note").textContent).toBe(DUCKDB_REASON);
    expect(compareButton().disabled).toBe(true);
    expect(compareButton().title).toBe(DUCKDB_REASON);
  });

  it("an in-memory SQLite database: tables from the tab session's view, the call through that session, the refusal shown", async () => {
    useProfiles.setState({ profiles: [profile("sqlite", "memory")] });
    servers({ memory: true });
    tab({ sessionId: "s1", database: "main" });
    useSnapshots.setState({ entries: snapshot("main", [obj("main", "orders"), obj("temp", "scratch"), obj("main", "big", "view")], "s1") });
    m.rpc.mockRejectedValue(new RpcError("SQLite has no data compare in Querithm.", -32000, "unsupported"));
    dataTab({ profileId: "p", database: "main", table: "main.orders", sessionId: "s1" });
    const list = document.getElementById("dc-tables-Source")!;
    expect(Array.from(list.querySelectorAll("option")).map((o) => o.value)).toEqual(["main.big", "main.orders", "temp.scratch"]);
    expect(options("Source database")).toContain("main");
    fireEvent.click(compareButton());
    expect((await screen.findByRole("alert")).textContent).toBe("SQLite has no data compare in Querithm.");
    expect(m.rpc).toHaveBeenCalledWith("dataCompare.start", expect.objectContaining({ source: { sessionId: "s1", database: "main", schema: "main", table: "orders" } }));
    expect(m.sessionFor).not.toHaveBeenCalled();
  });

  it("regression: MySQL keeps the explorer's session and the shared snapshot", async () => {
    useProfiles.setState({ profiles: [profile("mySql", null)] });
    servers({ sessionId: "explorer", databases: [{ name: "shop", isSystem: false } as never], caps: caps() as never });
    tab({ sessionId: "s1", database: "shop" });
    useSnapshots.setState({ entries: snapshot("shop", [obj("shop", "orders"), obj("shop", "order_lines")]) });
    m.rpc.mockResolvedValue({ compareId: "c", executionId: "e", key: [], columns: [], notes: [] });
    dataTab({ profileId: "p", database: "shop", table: "shop.orders", sessionId: "s1" });
    expect(screen.queryByRole("note")).toBeNull();
    const list = document.getElementById("dc-tables-Source")!;
    expect(Array.from(list.querySelectorAll("option")).map((o) => o.value)).toEqual(["shop.order_lines", "shop.orders"]);
    fireEvent.click(compareButton());
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("dataCompare.start", expect.objectContaining({ source: { sessionId: "explorer", database: "shop", schema: "shop", table: "orders" } })));
  });
});

describe("opening a compare binds the tab session", () => {
  it("from an in-memory tab: both sides keep its session; from the explorer: the active tab's on that database", () => {
    useProfiles.setState({ profiles: [profile("sqlite", "memory")] });
    m.activeTabState.mockReturnValue({ id: "t1", profileId: "p", database: "main", sessionId: "s1" });
    tab({ id: "t1", sessionId: "s1", database: "main" });
    A.openSchemaCompare();
    expect(m.openTool.mock.calls[0][2].params.source).toEqual({ kind: "live", profileId: "p", database: "main", schema: null, sessionId: "s1" });
    A.openDataCompare();
    expect(m.openTool.mock.calls[1][2].params).toEqual({
      source: { mode: "table", profileId: "p", database: "main", table: undefined, sessionId: "s1" },
      target: { mode: "table", profileId: "p", database: "main", sessionId: "s1" },
    });
    useTabs.setState({ activeId: "t1" });
    A.openSchemaCompare({ profileId: "p", database: "main", schema: "main" });
    expect(m.openTool.mock.calls[2][2].params.source).toEqual({ kind: "live", profileId: "p", database: "main", schema: "main", sessionId: "s1" });
  });

  it("regression: a server engine's sides carry no session", () => {
    useProfiles.setState({ profiles: [profile("mssql", null)] });
    tab({ id: "t1", sessionId: "s1", database: "Sales" });
    useTabs.setState({ activeId: "t1" });
    m.activeTabState.mockReturnValue({ id: "t1", profileId: "p", database: "Sales", sessionId: "s1" });
    A.openSchemaCompare();
    expect(m.openTool.mock.calls[0][2].params.source).toEqual({ kind: "live", profileId: "p", database: "Sales", schema: null });
    A.openDataCompare({ profileId: "p", database: "Sales", table: "dbo.Orders" });
    expect(m.openTool.mock.calls[1][2].params.source).toEqual({ mode: "table", profileId: "p", database: "Sales", table: "dbo.Orders" });
  });
});

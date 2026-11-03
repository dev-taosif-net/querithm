// @vitest-environment jsdom
// ER diagrams on Phase D engines. An in-memory SQLite / DuckDB database exists only in its query tab's session, so its
// diagram draws that session's own snapshot (Phase D0d); a file database falls back to the shared snapshot once the
// session is gone; server engines (MySQL here) keep reading the shared snapshot. Labels and JOIN quoting per engine.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { Engine, Profile, SchemaSnapshot } from "../../ipc/contract";
import { duckDbSnapshot, mySqlSnapshot, sqliteSnapshot } from "./fixture";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useProfiles } from "../../store/profiles";
import { useTabs, type TabState } from "../../store/tabs";
import { useToolTabs, type ToolTab } from "../../store/toolTabs";
import { buildSchemaIndex } from "../../editor/completion/schemaIndex";

const m = vi.hoisted(() => ({
  toast: vi.fn(), newQueryTab: vi.fn(), activeTabState: vi.fn(), openTool: vi.fn(), saveBytes: vi.fn(), rpc: vi.fn(), sessionFor: vi.fn(),
  getModel: vi.fn(), editorForTab: vi.fn(),
}));
vi.mock("@xyflow/react", async () => (await import("./fakeFlow")).flowMock);
vi.mock("@xyflow/react/dist/style.css", () => ({}));
vi.mock("../erdLayout.worker?worker", async () => {
  const { layoutGraph } = await import("../erdLayout");
  return {
    default: class {
      onmessage: ((e: { data: unknown }) => void) | null = null;
      postMessage(req: Parameters<typeof layoutGraph>[0]) { queueMicrotask(() => this.onmessage?.({ data: { positions: layoutGraph(req) } })); }
      terminate() {}
    },
  };
});
vi.mock("../../store/ui", () => ({ toast: m.toast }));
vi.mock("../../app/actions", () => ({ newQueryTab: m.newQueryTab, activeTabState: m.activeTabState }));
vi.mock("../../app/toolActions", () => ({ openTool: m.openTool }));
vi.mock("../../app/saveBytes", () => ({ saveBytes: m.saveBytes }));
vi.mock("../../editor/editors", () => ({ getModel: m.getModel, editorForTab: m.editorForTab }));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../store/connections", () => ({ useConnections: { getState: () => ({ sessionFor: m.sessionFor, servers: {} }) }, isDisconnected: () => false }));

const { ErdTab, MEMORY_GONE } = await import("../ErdTab");
const actions = await import("../erdActions");

const profile = (engine: Engine, location: Profile["location"]) => ({ id: "p", name: "Local", engine, location, auth: "none" }) as never;
const tab = (o: Partial<TabState>) => useTabs.getState().add({ id: o.id ?? "t1", title: "q.sql", profileId: "p", ...o });
const entry = (database: string, snapshot: SchemaSnapshot, sessionId?: string) => ({
  [snapKey("p", database, sessionId)]: { key: snapKey("p", database, sessionId), profileId: "p", database, sessionId, status: "fresh", snapshot, version: 1 } as never,
});
function show(o: { database: string; sessionId?: string; params?: Record<string, unknown> }) {
  const tool: ToolTab = { id: "tool1", kind: "erd", title: "ERD", profileId: "p", database: o.database, ...(o.sessionId ? { sessionId: o.sessionId } : {}), params: o.params ?? { mode: "schema", schema: null } };
  useToolTabs.setState({ tools: { tool1: tool } });
  return render(<ErdTab tool={tool} />);
}
const nodes = () => screen.queryAllByTestId(/^node-/);

beforeEach(() => {
  vi.clearAllMocks();
  m.sessionFor.mockResolvedValue("explorer");
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useSnapshots.setState({ entries: {} });
});
afterEach(cleanup);

describe("an in-memory database draws its tab session's view", () => {
  it("SQLite: the session's tables (temporary ones too), main.<name> labels, row counts through the tab's session", async () => {
    useProfiles.setState({ profiles: [profile("sqlite", "memory")] });
    tab({ sessionId: "s1", database: "main" });
    useSnapshots.setState({ entries: entry("main", sqliteSnapshot(), "s1") }); // no shared entry: there is none
    show({ database: "main", sessionId: "s1" });
    await waitFor(() => expect(nodes()).toHaveLength(5));
    for (const label of ["main.customer", "main.order", "main.order_line", "main.product", "temp.scratch"]) expect(screen.getByText(label)).toBeTruthy();
    expect(screen.getByText(/5 tables · 3 FKs/)).toBeTruthy();
    expect(m.rpc).not.toHaveBeenCalledWith("meta.snapshot", expect.anything());
    m.rpc.mockResolvedValueOnce([{ objectId: 2, rows: 7, estimated: false }]);
    fireEvent.click(screen.getByText("Load row counts"));
    await waitFor(() => expect(screen.getByText("7")).toBeTruthy());
    expect(m.rpc).toHaveBeenCalledWith("meta.rowCounts", { sessionId: "s1", database: "main", objectIds: expect.arrayContaining([1, 2, 3, 4, 5]) });
    expect(m.sessionFor).not.toHaveBeenCalled();
  });

  it("DuckDB: the session's tables; the generated JOIN double-quotes the reserved word and leaves main out", async () => {
    useProfiles.setState({ profiles: [profile("duckDb", "memory")] });
    tab({ sessionId: "s1", database: "memory" });
    useSnapshots.setState({ entries: entry("memory", duckDbSnapshot("memory"), "s1") });
    show({ database: "memory", sessionId: "s1" });
    await waitFor(() => expect(nodes()).toHaveLength(3));
    expect(screen.getByText("crm.region")).toBeTruthy();
    expect(screen.getByText("DECIMAL(10,2)")).toBeTruthy();
    fireEvent.click(screen.getByTestId("node-22"));
    fireEvent.click(screen.getByTestId("node-21"), { shiftKey: true });
    fireEvent.click(screen.getByText("Generate SELECT"));
    expect(m.newQueryTab.mock.calls[0][0].text).toBe('SELECT o.*, c.*\nFROM "order" o\nJOIN customer c ON o.customer_id = c.id;');
  });

  it("loads the session's own snapshot when it is not cached yet, naming the session", async () => {
    useProfiles.setState({ profiles: [profile("sqlite", "memory")] });
    tab({ sessionId: "s1", database: "main" });
    m.rpc.mockResolvedValue(sqliteSnapshot());
    show({ database: "main", sessionId: "s1" });
    await waitFor(() => expect(nodes()).toHaveLength(5));
    expect(m.rpc).toHaveBeenCalledWith("meta.snapshot", { profileId: "p", database: "main", sessionId: "s1" });
    expect(m.rpc).not.toHaveBeenCalledWith("meta.snapshot", { profileId: "p", database: "main" });
  });

  it("says why there is nothing to draw once the tab's session is gone (and before one exists)", async () => {
    useProfiles.setState({ profiles: [profile("sqlite", "memory")] });
    tab({ sessionId: "s1", database: "main" });
    useSnapshots.setState({ entries: entry("main", sqliteSnapshot(), "s1") });
    show({ database: "main", sessionId: "s1" });
    await waitFor(() => expect(nodes()).toHaveLength(5));
    act(() => useTabs.getState().update("t1", { sessionId: undefined })); // the engine restarted, or the tab closed it
    expect(await screen.findByText(MEMORY_GONE)).toBeTruthy();
    cleanup();
    show({ database: "main" });
    expect(screen.getByText(MEMORY_GONE)).toBeTruthy();
    expect(m.rpc).not.toHaveBeenCalledWith("meta.snapshot", expect.anything());
  });
});

describe("a file database and server engines", () => {
  it("a SQLite file whose tab session is gone falls back to the shared file snapshot", async () => {
    useProfiles.setState({ profiles: [profile("sqlite", "file")] });
    const shared = sqliteSnapshot();
    shared.objects = shared.objects.filter((o) => o.schema !== "temp");
    useSnapshots.setState({ entries: entry("main", shared) });
    show({ database: "main", sessionId: "closed" });
    await waitFor(() => expect(nodes()).toHaveLength(4));
    expect(screen.queryByText("temp.scratch")).toBeNull();
  });

  it("regression: MySQL reads the shared snapshot even with a tab session open; labels and JOIN use the database and backticks", async () => {
    useProfiles.setState({ profiles: [profile("mySql", null)] });
    tab({ sessionId: "s1", database: "shop" });
    useSnapshots.setState({ entries: { ...entry("shop", mySqlSnapshot()), ...entry("shop", { ...mySqlSnapshot(), objects: [] }, "s1") } });
    show({ database: "shop", sessionId: "s1" });
    await waitFor(() => expect(nodes()).toHaveLength(3));
    expect(screen.getByText("shop.order")).toBeTruthy();
    expect(screen.getByText("bit(8)")).toBeTruthy();
    fireEvent.click(screen.getByTestId("node-13"));
    fireEvent.click(screen.getByTestId("node-12"), { shiftKey: true });
    fireEvent.click(screen.getByText("Generate SELECT"));
    expect(m.newQueryTab.mock.calls[0][0].text).toBe("SELECT o.*, mc.*\nFROM shop.`order` o\nJOIN shop.m_child mc ON o.`key` = mc.id;");
    m.rpc.mockResolvedValueOnce([]);
    fireEvent.click(screen.getByText("Load row counts"));
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("meta.rowCounts", expect.objectContaining({ sessionId: "explorer", database: "shop" })));
  });
});

describe("opening a diagram binds the tab session", () => {
  const withEditor = (text: string, offset: number) => {
    m.getModel.mockReturnValue({ getValue: () => text, getOffsetAt: () => offset });
    m.editorForTab.mockReturnValue({ getPosition: () => ({ lineNumber: 1, column: offset + 1 }) });
  };

  it("Ctrl+Shift+E on an in-memory DuckDB tab: the statement's tables from the tab's own view, bound to its session", () => {
    useProfiles.setState({ profiles: [profile("duckDb", "memory")] });
    const t = { id: "t1", profileId: "p", database: "memory", sessionId: "s1" };
    m.activeTabState.mockReturnValue(t);
    useSnapshots.setState({ entries: { [snapKey("p", "memory", "s1")]: { key: "k", index: buildSchemaIndex(duckDbSnapshot("memory"), "duckDb", []) } as never } });
    withEditor('SELECT * FROM "order" o JOIN customer c ON c.id = o.customer_id', 20);
    actions.openErdFromActiveTab();
    expect(m.openTool).toHaveBeenCalledWith("erd", "ERD · query", { profileId: "p", database: "memory", sessionId: "s1", params: { mode: "query", queryTables: [22, 21] } });
    withEditor("SELECT 1", 3);
    actions.openErdFromActiveTab();
    expect(m.openTool).toHaveBeenLastCalledWith("erd", "ERD · memory", { profileId: "p", database: "memory", sessionId: "s1", params: { mode: "schema", schema: null } });
  });

  it("an in-memory tab with no session yet binds none (another tab's database is another database)", () => {
    useProfiles.setState({ profiles: [profile("sqlite", "memory")] });
    tab({ id: "other", sessionId: "s9", database: "main" });
    m.activeTabState.mockReturnValue({ id: "t1", profileId: "p", database: "main" });
    m.getModel.mockReturnValue(undefined);
    actions.openErdFromActiveTab();
    expect(m.openTool).toHaveBeenCalledWith("erd", "ERD · main", { profileId: "p", database: "main", params: { mode: "schema", schema: null } });
  });

  it("from the explorer: the active tab's session on that database; regression: none for a server engine", () => {
    useProfiles.setState({ profiles: [profile("sqlite", "file")] });
    tab({ id: "a", sessionId: "sA", database: "main" });
    tab({ id: "b", sessionId: "sB", database: "main" });
    useTabs.setState({ activeId: "b" });
    actions.openErdForTable("p", "main", 2, "order");
    expect(m.openTool).toHaveBeenLastCalledWith("erd", "ERD · order", { profileId: "p", database: "main", sessionId: "sB", params: { mode: "neighbourhood", rootId: 2 } });
    useProfiles.setState({ profiles: [profile("mySql", null)] });
    useTabs.setState({ tabs: {}, order: [], activeId: undefined });
    tab({ id: "c", sessionId: "sC", database: "shop" });
    actions.openErdForDatabase("p", "shop");
    expect(m.openTool).toHaveBeenLastCalledWith("erd", "ERD · shop", { profileId: "p", database: "shop", params: { mode: "schema", schema: null } });
  });
});

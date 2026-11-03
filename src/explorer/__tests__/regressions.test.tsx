// @vitest-environment jsdom
// Regression tests for the bugs found in the 2026-10-29 Explorer review (B1–B18). Each one failed before the redesign.
import { Profiler } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import type { Engine, ObjectDetails, SnapObject } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  newQueryTab: vi.fn(),
  scriptObject: vi.fn(async (..._a: unknown[]) => {}),
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../app/actions", () => ({
  changeConnection: vi.fn(async () => {}), changeDatabase: vi.fn(async () => {}), disconnectServer: vi.fn(async () => {}),
  newQueryTab: m.newQueryTab, openDefinition: vi.fn(async () => {}), scriptObject: m.scriptObject,
}));
vi.mock("../../debug/startDebug", () => ({ startDebug: vi.fn() }));
vi.mock("../../editor/editors", () => ({ editorForTab: () => undefined }));
vi.mock("../../erd/erdActions", () => ({ openErdForDatabase: vi.fn(), openErdForTable: vi.fn() }));
vi.mock("../../compare/compareActions", () => ({ openDataCompare: vi.fn(), openSchemaCompare: vi.fn() }));
vi.mock("../../refactor/refactorActions", () => ({ findUsagesOf: vi.fn(), renameOf: vi.fn() }));

import { Explorer } from "../Explorer";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";
import { useExplorer } from "../explorerState";

const col = (name: string, over: object = {}) => ({ name, type: "int", nullable: true, identity: false, computed: false, pk: false, ordinal: 1, ...over });
const orders = (id: number, schema: string): SnapObject => ({ id, schema, name: "Orders", kind: "table", columns: [col("Id", { pk: true, nullable: false }), col("Total")] });

function profile(id: string, engine: Engine, over: object = {}) {
  return { id, name: `Srv-${id}`, engine, environment: "dev", databaseDefault: "AppDb", groupName: null, color: null, ...over } as never;
}
function connect(id: string, objects: SnapObject[] | null, status: "connected" | "connecting" | "error" = "connected", entry: object = {}) {
  useConnections.setState((s) => ({
    servers: { ...s.servers, [id]: { profileId: id, status, databases: [{ name: "AppDb", isSystem: false }], sessionId: "s", error: status === "error" ? "Connection timed out after 15 s." : undefined } as never },
    order: s.order.includes(id) ? s.order : [...s.order, id],
  }));
  if (objects)
    useSnapshots.setState((s) => ({
      entries: { ...s.entries, [snapKey(id, "AppDb")]: { key: snapKey(id, "AppDb"), profileId: id, database: "AppDb", status: "fresh", snapshot: { objects, status: "fresh" } as never, index: { byId: new Map(objects.map((o) => [o.id, o])) } as never, version: 1, ...entry } as never },
    }));
}
const searchBox = () => screen.getByRole("textbox");
const typeSearch = (text: string) => fireEvent.change(searchBox(), { target: { value: text } });
/** The tree's scroll container (before the redesign: the `.overflow-y-auto` list; after: `role="tree"`). */
const treeBox = () => (document.querySelector('[role="tree"]') ?? document.querySelector(".overflow-y-auto")) as HTMLElement;
/** The deepest element in the tree whose own text is exactly `text` (rows split a schema prefix and a name into spans). */
const textEl = (text: string | RegExp) => {
  // The old rows drew their carets as text (▸ ▾) next to the label.
  const is = (n: Element) => { const t = (n.textContent ?? "").replace(/[▸▾]/g, ""); return typeof text === "string" ? t === text : text.test(t); };
  const all = within(treeBox()).queryAllByText((_, el) => !!el && is(el) && ![...el.children].some(is));
  return all[0];
};
/** Clicks `text` unless `child` is already on screen (the old tree auto-expanded some levels, the new one does not). */
const openUntil = (text: string | RegExp, child: string | RegExp) => {
  if (!textEl(child)) fireEvent.click(textEl(text)!);
};

beforeEach(() => {
  vi.stubGlobal("ResizeObserver", class { observe() {} disconnect() {} unobserve() {} });
  m.rpc.mockReset();
  m.rpc.mockImplementation(async () => ({ columns: [], params: [], indexes: [], foreignKeys: [], referencedBy: [] }) satisfies ObjectDetails);
  useProfiles.setState({ profiles: [], groups: [] });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useSnapshots.setState({ entries: {} });
  useTabs.setState({ activeId: undefined, tabs: {}, order: [] } as never);
  useSettings.setState({ explorerGroupByEnv: false, explorerSchemaGroups: [], explorerVisible: true, explorerAutoHide: false, pinnedProfiles: [] });
  useUi.setState({ dialogs: [], toasts: [], explorerWidth: 280 });
  // The Explorer's view state lives in a store (it outlives a mount: the workspace saves the expansion).
  useExplorer.setState({ expanded: new Set(), collapsedEnvs: new Set(), details: {}, kind: "all", folderFilters: {}, query: "", uncapped: new Set(), selected: undefined });
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe.each<Engine>(["mssql", "pg"])("Explorer regressions (%s)", (engine) => {
  const dflt = engine === "pg" ? "public" : "dbo";

  it("B1: search never falls back to another server when no query tab is active", () => {
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)]);
    render(<Explorer />);
    typeSearch("Ord");
    expect(screen.queryByText(/\bmatch(es)?\b/i)).toBeNull();
    expect(screen.getAllByText(/query tab/i).length).toBeGreaterThan(0);
  });

  it("B2: searching a database whose catalog is not loaded asks for it and says it is loading", () => {
    const ensure = vi.spyOn(useSnapshots.getState(), "ensure").mockImplementation(async () => {});
    useSnapshots.setState({ ensure } as never);
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", null);
    useTabs.setState({ activeId: "t1", tabs: { t1: { id: "t1", profileId: "p1", database: "AppDb" } }, order: ["t1"] } as never);
    render(<Explorer />);
    typeSearch("Ord");
    expect(ensure).toHaveBeenCalledWith("p1", "AppDb", undefined); // no session: the shared snapshot
    expect(screen.queryByText(/0 match/i)).toBeNull();
  });

  it("B3: a kind capped at 300 results says how many there are", () => {
    const many: SnapObject[] = Array.from({ length: 686 }, (_, i) => ({ id: i + 1, schema: dflt, name: `t${i}`, kind: "table" }));
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", many);
    useTabs.setState({ activeId: "t1", tabs: { t1: { id: "t1", profileId: "p1", database: "AppDb" } }, order: ["t1"] } as never);
    render(<Explorer />);
    typeSearch("t");
    expect(screen.queryByText(/\(300\)/)).toBeNull();
    expect(screen.getByText(/300 of 686/i)).toBeTruthy();
  });

  it("B7: the search field's border is the 3:1 field border", () => {
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)]);
    render(<Explorer />);
    const field = searchBox().closest("div")!;
    expect(`${field.className} ${searchBox().className}`).toMatch(/border-line-strong/);
  });

  it("B8: a refresh error over cached objects is shown on the database row", () => {
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)], "connected", { status: "error", error: "Timeout expired" });
    render(<Explorer />);
    openUntil("Srv-p1", "AppDb");
    expect(screen.getAllByText(/cached|refresh failed/i).length).toBeGreaterThan(0);
  });

  it("B9: Keys that failed to load are fetched again when reopened", async () => {
    m.rpc.mockImplementation(async () => {
      throw new Error("timeout");
    });
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)]);
    render(<Explorer />);
    openUntil("Srv-p1", "AppDb");
    openUntil("AppDb", "Tables");
    openUntil("Tables", /^(dbo\.|public\.)?Orders$/);
    openUntil(/^(dbo\.|public\.)?Orders$/, "Keys");
    await act(async () => void fireEvent.click(textEl("Keys")!));
    await act(async () => void fireEvent.click(textEl("Keys")!));
    await act(async () => void fireEvent.click(textEl("Keys")!));
    expect(m.rpc.mock.calls.filter((c) => c[0] === "meta.objectDetails").length).toBe(2);
  });

  it("B10: an engine restart keeps the expanded tree while the server reconnects", () => {
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)]);
    render(<Explorer />);
    openUntil("Srv-p1", "AppDb");
    openUntil("AppDb", "Tables");
    act(() => useConnections.setState((s) => ({ servers: { ...s.servers, p1: { ...s.servers.p1, status: "connecting", sessionId: undefined } } })));
    expect(textEl("Tables")).toBeTruthy();
  });

  it("B11: an object recreated with a new id keeps its expansion", () => {
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)]);
    render(<Explorer />);
    openUntil("Srv-p1", "AppDb");
    openUntil("AppDb", "Tables");
    openUntil("Tables", /^(dbo\.|public\.)?Orders$/);
    openUntil(/^(dbo\.|public\.)?Orders$/, "Columns");
    fireEvent.click(textEl("Columns")!);
    expect(textEl("Total")).toBeTruthy();
    act(() => connect("p1", [orders(101, dflt)], "connected", { version: 2 }));
    expect(textEl("Total")).toBeTruthy();
  });

  it("B13: scrolling inside the rendered window does not re-render the Explorer", () => {
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)]);
    const onRender = vi.fn();
    render(<Profiler id="x" onRender={onRender}><Explorer /></Profiler>);
    const tree = treeBox();
    onRender.mockClear();
    tree.scrollTop = 10;
    fireEvent.scroll(tree);
    expect(onRender).not.toHaveBeenCalled();
  });

  it("B14 / B16: a pinned profile stays in the tree, and a failed server says so on its collapsed row", () => {
    useProfiles.setState({ profiles: [profile("p1", engine), profile("p2", engine, { environment: "staging" })] });
    useSettings.setState({ pinnedProfiles: ["p1"] });
    connect("p2", null, "error");
    render(<Explorer />);
    expect(textEl("Srv-p1")).toBeTruthy(); // pinned, not connected
    expect(screen.getByText("Failed")).toBeTruthy();
  });

  it("B5: the selected row is a highlighted row (.q-hl)", () => {
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)]);
    render(<Explorer />);
    fireEvent.click(textEl("Srv-p1")!);
    const selected = document.querySelector('[aria-selected="true"]') ?? textEl("Srv-p1")!.closest(".absolute");
    expect(selected?.className ?? "").toMatch(/\bq-hl\b/);
  });

  it("B18: a table's Refresh names the database it refreshes", () => {
    useProfiles.setState({ profiles: [profile("p1", engine)] });
    connect("p1", [orders(1, dflt)]);
    render(<Explorer />);
    openUntil("Srv-p1", "AppDb");
    openUntil("AppDb", "Tables");
    openUntil("Tables", /^(dbo\.|public\.)?Orders$/);
    fireEvent.contextMenu(textEl(/^(dbo\.|public\.)?Orders$/)!);
    expect(within(screen.getByRole("menu")).getByText(/Refresh.*AppDb/)).toBeTruthy();
  });
});

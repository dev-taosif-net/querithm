// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, createEvent, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { Engine, ObjectDetails, ObjectKind, SnapObject } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  changeConnection: vi.fn(async (..._a: unknown[]) => {}),
  changeDatabase: vi.fn(async (..._a: unknown[]) => {}),
  disconnectServer: vi.fn(async (..._a: unknown[]) => {}),
  newQueryTab: vi.fn(),
  openDefinition: vi.fn(async (..._a: unknown[]) => {}),
  scriptObject: vi.fn(async (..._a: unknown[]) => {}),
  startDebug: vi.fn(async (..._a: unknown[]) => {}),
  openErdForDatabase: vi.fn(),
  openErdForTable: vi.fn(),
  openDataCompare: vi.fn(),
  openSchemaCompare: vi.fn(),
  findUsagesOf: vi.fn(),
  renameOf: vi.fn(),
  editor: undefined as unknown,
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../app/actions", () => ({
  changeConnection: m.changeConnection, changeDatabase: m.changeDatabase, disconnectServer: m.disconnectServer, activateTab: vi.fn(),
  newQueryTab: m.newQueryTab, openDefinition: m.openDefinition, scriptObject: m.scriptObject,
}));
vi.mock("../../app/tabActions", () => ({ tabForNewConnection: () => undefined }));
vi.mock("../../debug/startDebug", () => ({ startDebug: m.startDebug }));
vi.mock("../../editor/editors", () => ({ editorForTab: () => m.editor }));
vi.mock("../../erd/erdActions", () => ({ openErdForDatabase: m.openErdForDatabase, openErdForTable: m.openErdForTable }));
vi.mock("../../compare/compareActions", () => ({ openDataCompare: m.openDataCompare, openSchemaCompare: m.openSchemaCompare }));
vi.mock("../../refactor/refactorActions", () => ({ findUsagesOf: m.findUsagesOf, renameOf: m.renameOf }));
vi.mock("../../monitor/monitorActions", () => ({ openActivityMonitor: vi.fn() }));

import { Explorer, generateDmlTemplate, modifyTrigger } from "../Explorer";
import { explorerHooks } from "../explorerWindow";
import { useExplorer } from "../explorerState";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";

const col = (name: string, over: object = {}) => ({ name, type: "int", nullable: true, identity: false, computed: false, pk: false, ordinal: 1, ...over });
const obj = (id: number, schema: string, name: string, kind: ObjectKind, over: Partial<SnapObject> = {}): SnapObject => ({ id, schema, name, kind, ...over });

function objectsFor(engine: Engine): SnapObject[] {
  const s = engine === "pg" ? "public" : "dbo";
  return [
    obj(1, s, "Orders", "table", { columns: [col("Id", { pk: true, nullable: false }), col("Total", { type: "money" })] }),
    obj(2, s, "orders_view", "view", { columns: [col("Id")] }),
    obj(3, s, "do_work", "proc", { params: [{ name: "@a", type: "int", ordinal: 1, output: true, hasDefault: true }, { name: "", type: "text", ordinal: 2, output: false, hasDefault: false }] }),
    obj(4, s, "calc", "func", { returnType: "int", params: [] }),
    obj(5, s, "tab_fn", "tvf", { params: [], columns: [col("X")] }),
    obj(6, s, "mood", "type"),
    obj(7, s, "seq1", "sequence"),
    obj(8, s, "syn1", "synonym"),
    obj(9, "sales", "Weird Name]", "table", { columns: [] }),
    obj(10, s, "empty_proc", "proc"),
  ];
}

const details: ObjectDetails = {
  columns: [], params: [],
  indexes: [{ name: "PK_Orders", isUnique: true, isPrimary: true, columns: ["Id"] }, { name: "IX_T", isUnique: true, isPrimary: false, columns: ["Total"] }, { name: "IX_U", isUnique: false, isPrimary: false, columns: ["Id", "Total"] }],
  foreignKeys: [{ name: "FK_a@1", parent: 1, referenced: 9, columns: [{ parent: "Id", referenced: "Id" }] }, { name: "FK_unknown", parent: 1, referenced: 99, columns: [] }],
  referencedBy: [{ name: "FK_b@2", parent: 9, referenced: 1, columns: [] }, { name: "FK_c", parent: 77, referenced: 1, columns: [] }],
};

function seed(engine: Engine, opts: { id?: string; env?: "dev" | "prod" | "local" | "staging"; objects?: SnapObject[]; status?: string; dbs?: { name: string; isSystem?: boolean; state?: string }[]; conn?: object } = {}) {
  const id = opts.id ?? "p1";
  const objects = opts.objects ?? objectsFor(engine);
  useProfiles.setState({
    profiles: [...useProfiles.getState().profiles.filter((p) => p.id !== id), { id, name: `Srv-${id}`, engine, environment: opts.env ?? "dev", databaseDefault: "AppDb", groupName: null, color: null } as never],
    groups: [],
  });
  const dbs = (opts.dbs ?? [{ name: "AppDb" }]).map((d) => ({ isSystem: false, ...d }));
  useConnections.setState((s) => ({
    servers: { ...s.servers, [id]: { profileId: id, status: "connected", databases: dbs, sessionId: "s", ...opts.conn } as never },
    order: s.order.includes(id) ? s.order : [...s.order, id],
  }));
  useSnapshots.setState((s) => ({
    entries: { ...s.entries, [snapKey(id, "AppDb")]: { key: snapKey(id, "AppDb"), profileId: id, database: "AppDb", status: (opts.status ?? "fresh") as never, snapshot: { objects, status: "fresh" } as never, index: { byId: new Map(objects.map((o) => [o.id, o])) } as never, version: 1 } },
  }));
}

const tree = () => screen.getByRole("tree");
const item = (name: string | RegExp) => within(tree()).getByRole("treeitem", { name });
const maybe = (name: string | RegExp) => within(tree()).queryByRole("treeitem", { name });
const names = () => within(tree()).queryAllByRole("treeitem").map((r) => r.getAttribute("aria-label"));
const click = (name: string | RegExp) => fireEvent.click(item(name));
const dbl = (name: string | RegExp) => fireEvent.doubleClick(item(name));
const key = (k: string, o: object = {}) => fireEvent.keyDown(tree(), { key: k, ...o });
const openMenu = (name: string | RegExp) => fireEvent.contextMenu(item(name), { clientX: 10, clientY: 10 });
const menu = () => screen.getByRole("menu", { name: /^Actions for/ });
const menuEntries = (box: HTMLElement = menu()) => [...box.querySelectorAll<HTMLElement>('[role^="menuitem"]')];
const menuLabels = () => menuEntries().map((b) => b.getAttribute("aria-label"));
const menuClick = (label: string | RegExp, box?: HTMLElement) => {
  const el = menuEntries(box).find((b) => (typeof label === "string" ? b.getAttribute("aria-label") === label || b.getAttribute("aria-label")?.startsWith(`${label},`) : label.test(b.getAttribute("aria-label") ?? "")));
  if (!el) throw new Error(`menu item ${label} not in ${menuLabels().join(" | ")}`);
  fireEvent.click(el);
};
const header = () => within(screen.getByRole("toolbar", { name: "Object Explorer" }));
const scopeLine = () => within(document.getElementById("q-explorer-scope")!);
const searchBox = () => screen.getByRole("textbox", { name: /^Search objects/ });
const ex = () => useExplorer.getState();

beforeEach(() => {
  vi.stubGlobal("ResizeObserver", class { observe() {} disconnect() {} unobserve() {} });
  Object.values(m).forEach((f) => typeof f === "function" && "mockClear" in f && (f as { mockClear(): void }).mockClear());
  m.rpc.mockImplementation(async () => details);
  m.editor = undefined;
  useProfiles.setState({ profiles: [], groups: [] });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useSnapshots.setState({ entries: {} });
  useTabs.setState({ activeId: undefined, tabs: {}, order: [] } as never);
  useSettings.setState({ explorerGroupByEnv: false, explorerSchemaGroups: [], explorerVisible: true, explorerAutoHide: false, explorerShowAll: false, pinnedProfiles: [], explorerHoverOpen: true });
  useUi.setState({ dialogs: [], toasts: [], explorerWidth: 280 });
  useExplorer.setState({ expanded: new Set(), collapsedEnvs: new Set(), details: {}, kind: "all", folderFilters: {}, query: "", uncapped: new Set(), selected: undefined });
  Object.defineProperty(navigator, "clipboard", { value: { writeText: vi.fn(async () => {}) }, configurable: true });
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Explorer: first run and empty states", () => {
  it("with no profile, one primary button creates the first connection; search is not offered", () => {
    render(<Explorer />);
    expect(screen.getByText("No connections yet")).toBeTruthy();
    expect(screen.getByText(/Passwords stay in your OS keyring/)).toBeTruthy();
    expect(screen.queryByRole("textbox")).toBeNull();
    fireEvent.click(within(screen.getByText("No connections yet").parentElement!).getByRole("button", { name: "New connection…" }));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "profile" });
  });

  it("with saved profiles but none in use or pinned, it says how many are saved and opens the Connect dialog", () => {
    useProfiles.setState({ profiles: [{ id: "x", name: "X", engine: "pg", environment: "dev" }, { id: "y", name: "Y", engine: "mssql", environment: "prod" }] as never });
    render(<Explorer />);
    expect(screen.getByText("No servers in use")).toBeTruthy();
    expect(screen.getByText(/2 saved connections/)).toBeTruthy();
    fireEvent.click(within(screen.getByText("No servers in use").parentElement!).getByRole("button", { name: "Connect…" }));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "connect" });
  });

  it("the header's + opens the Connect dialog, or the new-profile form when nothing is saved", () => {
    const { unmount } = render(<Explorer />);
    fireEvent.click(header().getByRole("button", { name: "New connection…" }));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "profile" });
    unmount();
    useProfiles.setState({ profiles: [{ id: "x", name: "X", engine: "pg", environment: "dev" }] as never });
    render(<Explorer />);
    fireEvent.click(header().getByRole("button", { name: "Connect…" }));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "connect" });
  });

  it("title buttons toggle pin and close the window", () => {
    render(<Explorer />);
    fireEvent.click(screen.getByRole("button", { name: "Auto hide" }));
    expect(useSettings.getState().explorerAutoHide).toBe(true);
    expect(screen.getByRole("button", { name: "Dock (pin)" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /^Close/ }));
    expect(useSettings.getState().explorerVisible).toBe(false);
  });

  it("registers a search-focus hook while mounted and removes it after", () => {
    seed("mssql");
    const { unmount } = render(<Explorer />);
    explorerHooks.focusSearch!();
    expect(document.activeElement).toBe(searchBox());
    unmount();
    expect(explorerHooks.focusSearch).toBeUndefined();
  });

  it("the View ⋯ menu toggles grouping, all saved profiles and hover-open, and collapses everything", () => {
    seed("mssql");
    render(<Explorer />);
    fireEvent.click(screen.getByRole("button", { name: "View options" }));
    const view = screen.getByRole("menu", { name: "View options" });
    expect(within(view).getByRole("menuitemcheckbox", { name: "Group active database by schema, No active database" }).getAttribute("aria-disabled")).toBe("true");
    fireEvent.click(within(view).getByRole("menuitemcheckbox", { name: "Group by environment" }));
    expect(useSettings.getState().explorerGroupByEnv).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "View options" }));
    fireEvent.click(within(screen.getByRole("menu", { name: "View options" })).getByRole("menuitemcheckbox", { name: "Show all saved profiles" }));
    expect(useSettings.getState().explorerShowAll).toBe(true);
    fireEvent.click(screen.getByRole("button", { name: "View options" }));
    fireEvent.click(within(screen.getByRole("menu", { name: "View options" })).getByRole("menuitem", { name: "Collapse all" }));
    expect(ex().expanded.size).toBe(0);
  });
});

describe.each<Engine>(["mssql", "pg"])("Explorer tree (%s)", (engine) => {
  const dflt = engine === "pg" ? "public" : "dbo";
  const engineName = engine === "pg" ? "PostgreSQL" : "SQL Server";

  it("a server the user connects opens one level: its databases, nothing deeper (§12)", () => {
    seed(engine);
    render(<Explorer />);
    expect(item(`DEV, Srv-p1, ${engineName}, connected`).getAttribute("aria-expanded")).toBe("true");
    expect(names()).toEqual([`DEV, Srv-p1, ${engineName}, connected`, "Database AppDb"]);
    expect(item("Database AppDb").getAttribute("aria-expanded")).toBe("false");
  });

  it("a background reconnect (startup, engine restart) does not open the server", () => {
    seed(engine, { conn: { quiet: true } });
    render(<Explorer />);
    expect(names()).toEqual([`DEV, Srv-p1, ${engineName}, connected`]);
  });

  it("opening a database loads its catalog once and lists the main categories, the others when they hold objects", () => {
    seed(engine);
    useSnapshots.setState({ entries: {} });
    const ensure = vi.fn();
    useSnapshots.setState({ ensure } as never);
    render(<Explorer />);
    click("Database AppDb");
    expect(ensure).toHaveBeenCalledWith("p1", "AppDb");
    expect(item("Loading metadata…")).toBeTruthy();
    act(() => seed(engine));
    for (const c of ["Tables, 2", "Views, 1", "Procedures, 2", "Functions, 2", "Types, 1", "Sequences, 1", "Synonyms, 1"]) expect(item(c)).toBeTruthy();
  });

  it("objects in the engine's default schema are bare; others keep their schema; columns, keys and indexes load lazily", async () => {
    seed(engine);
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    expect(item(`table ${dflt}.Orders`).textContent).toBe("Orders");
    expect(item("table sales.Weird Name]").textContent).toBe("sales.Weird Name]");
    click(`table ${dflt}.Orders`);
    click("Columns");
    expect(item("Id, int NOT NULL")).toBeTruthy();
    expect(item("Total, money")).toBeTruthy();
    await act(async () => void click("Keys"));
    await waitFor(() => expect(item(/^PK_Orders, PK \(Id\)/)).toBeTruthy());
    expect(item(/^FK_a, → sales\.Weird Name\]/)).toBeTruthy();
    expect(item(/^FK_unknown, → 99/)).toBeTruthy();
    expect(item(/^FK_b, ← sales\.Weird Name\]/)).toBeTruthy();
    expect(item(/^FK_c, ← 77/)).toBeTruthy();
    click("Indexes");
    expect(item("IX_T, UNIQUE (Total)")).toBeTruthy();
    expect(item("IX_U, (Id, Total)")).toBeTruthy();
    expect(m.rpc.mock.calls.filter((c) => c[0] === "meta.objectDetails")).toHaveLength(1); // one read serves both folders
  });

  it("a details failure shows the reason and a Retry row that reads again", async () => {
    seed(engine);
    m.rpc.mockRejectedValueOnce(new Error("permission denied"));
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    click(`table ${dflt}.Orders`);
    await act(async () => void click("Keys"));
    await waitFor(() => expect(item("permission denied")).toBeTruthy());
    await act(async () => void click("Retry"));
    await waitFor(() => expect(item(/^PK_Orders/)).toBeTruthy());
  });

  it("empty notes: no keys, no indexes, no columns, no parameters", async () => {
    seed(engine);
    m.rpc.mockImplementation(async () => ({ ...details, indexes: [], foreignKeys: [], referencedBy: [] }));
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    click("table sales.Weird Name]");
    click("Columns");
    expect(item("No columns")).toBeTruthy();
    await act(async () => void click("Keys"));
    await waitFor(() => expect(item("No keys")).toBeTruthy());
    click("Indexes");
    expect(item("No indexes")).toBeTruthy();
    click("Procedures, 2");
    click(`proc ${dflt}.empty_proc`);
    click("Parameters");
    expect(item("No parameters")).toBeTruthy();
  });

  it("views list columns; procedures and functions list parameters and return types; TVFs both", () => {
    seed(engine);
    render(<Explorer />);
    click("Database AppDb");
    click("Procedures, 2");
    click(`proc ${dflt}.do_work`);
    click("Parameters");
    expect(item("@a, int OUTPUT = default")).toBeTruthy();
    expect(item("$2, text")).toBeTruthy();
    click(`proc ${dflt}.do_work`); // close it: one Parameters folder on screen at a time
    click("Functions, 2");
    click(`func ${dflt}.calc`);
    click("Parameters");
    expect(item("RETURNS, int")).toBeTruthy();
    click(`func ${dflt}.calc`);
    click(`tvf ${dflt}.tab_fn`);
    expect(item("Parameters")).toBeTruthy();
    click("Columns");
    expect(item("X, int")).toBeTruthy();
  });

  it("double-click opens SELECT TOP for tables and views and the definition for programmable objects", () => {
    seed(engine);
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    dbl(`table ${dflt}.Orders`);
    expect(m.scriptObject).toHaveBeenCalledWith("p1", "AppDb", { schema: dflt, name: "Orders", kind: "table", objectId: 1 }, "selectTop", true);
    click("Procedures, 2");
    dbl(`proc ${dflt}.do_work`);
    expect(m.openDefinition).toHaveBeenCalledWith("p1", "AppDb", { schema: dflt, name: "do_work", kind: "proc", objectId: 3 }, true);
  });

  it("a double-click toggles a folder once (the second click does not close it again)", () => {
    seed(engine);
    render(<Explorer />);
    fireEvent.click(item("Database AppDb"), { detail: 1 });
    fireEvent.click(item("Database AppDb"), { detail: 2 });
    expect(item("Database AppDb").getAttribute("aria-expanded")).toBe("true");
  });

  it("double-clicking a database binds the active tab: changes database on the same server, gives an unconnected tab the connection, else a new tab", () => {
    seed(engine);
    render(<Explorer />);
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1" } }, order: ["t"] } as never);
    dbl("Database AppDb");
    expect(m.changeDatabase).toHaveBeenCalledWith("t", "AppDb");
    useTabs.setState({ activeId: "u", tabs: { u: { id: "u" } }, order: ["u"] } as never);
    dbl("Database AppDb");
    expect(m.changeConnection).toHaveBeenCalledWith("u", "p1", "AppDb");
    useTabs.setState({ activeId: "v", tabs: { v: { id: "v", profileId: "other" } }, order: ["v"] } as never);
    dbl("Database AppDb");
    expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "p1", database: "AppDb" });
  });

  it("marks the active tab's database, and says it on the row", () => {
    seed(engine);
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "AppDb" } }, order: ["t"] } as never);
    render(<Explorer />);
    expect(item("Database AppDb, active in tab").textContent).toContain("ACTIVE");
  });

  it("matches the active database by its exact name (D0e identity: \"appdb\" is another database)", () => {
    seed(engine);
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "appdb" } }, order: ["t"] } as never);
    render(<Explorer />);
    expect(item("Database AppDb").textContent).not.toContain("ACTIVE");
  });

  it("system databases fold into one folder", () => {
    seed(engine, { dbs: [{ name: "AppDb" }, { name: "master", isSystem: true }, { name: "msdb", isSystem: true }] });
    render(<Explorer />);
    expect(names()).toContain("System databases, 2");
    expect(maybe("Database master")).toBeNull();
    click("System databases, 2");
    expect(item("Database master")).toBeTruthy();
  });

  it("an offline database says so and does not expand", () => {
    seed(engine, { dbs: [{ name: "AppDb" }, { name: "Archive", state: "OFFLINE" }] });
    render(<Explorer />);
    expect(item("Database Archive, offline").getAttribute("aria-expanded")).toBeNull();
    click("Database Archive, offline");
    expect(names()).not.toContain("Loading metadata…");
  });

  it("groups by schema with the engine's default schema first and only non-empty categories", () => {
    seed(engine);
    useSettings.setState({ explorerSchemaGroups: ["p1|AppDb"] });
    render(<Explorer />);
    click("Database AppDb");
    expect(names().filter((n) => n?.startsWith("Schema"))).toEqual([`Schema ${dflt}, 9 objects`, "Schema sales, 1 objects"]);
    click("Schema sales, 1 objects");
    expect(item("Tables, 1")).toBeTruthy();
    expect(maybe("Views, 0")).toBeNull();
  });

  it("the kind filter keeps one category in the tree and says so in the scope line", () => {
    seed(engine);
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "AppDb" } }, order: ["t"] } as never);
    render(<Explorer />);
    click("Database AppDb, active in tab");
    fireEvent.click(screen.getByRole("button", { name: "Filter by kind" }));
    fireEvent.click(within(screen.getByRole("menu", { name: "Filter by kind" })).getByRole("menuitemcheckbox", { name: "Views" }));
    expect(names().filter((n) => /^(Tables|Views|Procedures|Functions)/.test(n ?? ""))).toEqual(["Views, 1"]);
    fireEvent.click(screen.getByRole("button", { name: "Clear kind filter Views" }));
    expect(ex().kind).toBe("all");
  });

  it("Synonyms in the kind filter are unavailable on PostgreSQL, with the reason", () => {
    seed(engine);
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "AppDb" } }, order: ["t"] } as never);
    render(<Explorer />);
    fireEvent.click(screen.getByRole("button", { name: "Filter by kind" }));
    const syn = within(screen.getByRole("menu", { name: "Filter by kind" })).getByRole("menuitemcheckbox", { name: /^Synonyms/ });
    expect(syn.getAttribute("aria-disabled")).toBe(engine === "pg" ? "true" : null);
  });

  it("collapses and re-expands a server and a database", () => {
    seed(engine);
    render(<Explorer />);
    click(/^DEV, Srv-p1/);
    expect(maybe("Database AppDb")).toBeNull();
    click(/^DEV, Srv-p1/);
    click("Database AppDb");
    expect(item("Tables, 2")).toBeTruthy();
    click("Database AppDb");
    expect(maybe("Tables, 2")).toBeNull();
  });

  it("states on the server row: connecting (busy), failed (says so collapsed; reason, Reconnect and Edit profile when opened)", () => {
    seed(engine, { conn: { status: "connecting", databases: [] } });
    seed(engine, { id: "p2", env: "staging", conn: { status: "error", error: "Connection timed out after 15 s." } });
    render(<Explorer />);
    expect(item(`DEV, Srv-p1, ${engineName}, connecting`).getAttribute("aria-busy")).toBe("true");
    const failed = item(`STAGE, Srv-p2, ${engineName}, connection failed: Connection timed out after 15 s.`);
    expect(failed.textContent).toContain("Failed");
    expect(failed.getAttribute("aria-expanded")).toBe("false");
    fireEvent.click(failed.querySelector("[aria-hidden]")!); // the caret
    expect(item("Connection timed out after 15 s.")).toBeTruthy();
    click("Edit profile…");
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "profile", profileId: "p2" });
  });

  it("a failed server's context menu opens with Reconnect as its default and says it is failed in its header", () => {
    seed(engine, { env: "staging", conn: { status: "error", error: "Connection timed out after 15 s." } });
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    render(<Explorer />);
    openMenu(/^STAGE, Srv-p1/);
    expect(menu().getAttribute("aria-label")).toBe("Actions for STAGE Srv-p1 · connection failed");
    expect(menuLabels().slice(0, 4)).toEqual(["Reconnect", "Edit profile…", "Duplicate profile…", "Copy error"]);
    expect(menuLabels()).toContain("New query, Not connected");
    menuClick("Reconnect");
    expect(connect).toHaveBeenCalledWith("p1");
  });

  it("the menu header's environment label uses the color fitted to the menu surface, not the raw environment color", () => {
    // The light theme's colors: the raw dev green is 2.5:1 on the menu's white surface.
    const root = document.documentElement.style;
    const vars = { "--q-env-dev": "#10b981", "--q-panel": "#f7f8fa", "--q-hover": "#eceef2", "--q-row-sel": "#dde4f5", "--q-panel-2": "#f0f2f5", "--q-raised": "#ffffff" };
    Object.entries(vars).forEach(([k, v]) => root.setProperty(k, v));
    try {
      seed(engine);
      render(<Explorer />);
      openMenu("Database AppDb");
      const color = within(menu()).getByText("DEV").style.color;
      expect(color).not.toBe("var(--q-env-dev)");
      expect(color).not.toBe("rgb(16, 185, 129)");
    } finally {
      Object.keys(vars).forEach((k) => root.removeProperty(k));
    }
  });

  it("a failed or not-connected server: a click selects it (never dials); Enter or double-click connects", () => {
    seed(engine, { conn: { status: "error", error: "x" } });
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    render(<Explorer />);
    click(/^DEV, Srv-p1/);
    expect(connect).not.toHaveBeenCalled();
    expect(item(/^DEV, Srv-p1/).getAttribute("aria-selected")).toBe("true");
    key("Enter");
    expect(connect).toHaveBeenCalledWith("p1");
    dbl(/^DEV, Srv-p1/);
    expect(connect).toHaveBeenCalledTimes(2);
  });

  it("a pinned profile that is not connected stays as a row; the rest of the saved profiles get no row (the header's Connect… opens them)", () => {
    useProfiles.setState({ profiles: [{ id: "a", name: "A", engine, environment: "dev" }, { id: "b", name: "B", engine, environment: "prod" }, { id: "c", name: "C", engine, environment: "test" }] as never });
    useSettings.setState({ pinnedProfiles: ["b"] });
    render(<Explorer />);
    expect(names()).toEqual([`PROD, B, ${engineName}, not connected, pinned`]);
    expect(screen.queryByText(/saved connection/)).toBeNull();
    fireEvent.click(header().getByRole("button", { name: "Connect…" }));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "connect" });
  });

  it("loading, reading and error notes for a database; a stale catalog after a failed refresh is marked", () => {
    seed(engine, { status: "error" });
    useSnapshots.setState((s) => ({ entries: { ...s.entries, [snapKey("p1", "AppDb")]: { ...s.entries[snapKey("p1", "AppDb")], error: "Timeout expired", snapshot: { objects: [], status: "error" } as never } } }));
    render(<Explorer />);
    click("Database AppDb");
    expect(item("Timeout expired")).toBeTruthy();
    act(() => seed(engine, { status: "error" }));
    expect(item("Database AppDb, refresh failed, cached catalog").textContent).toContain("cached");
  });
});

describe.each<Engine>(["mssql", "pg"])("Explorer search (%s)", (engine) => {
  const dflt = engine === "pg" ? "public" : "dbo";
  const quoted = engine === "pg" ? "public.orders_view" : "dbo.orders_view";
  beforeEach(() => {
    seed(engine);
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "AppDb" } }, order: ["t"] } as never);
  });

  it("a tab with no connection says so (not \"no query tab\"), with a dashed field and no results", () => {
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t" } }, order: ["t"] } as never);
    render(<Explorer />);
    expect(scopeLine().getByText("The active tab has no connection (Ctrl+Shift+U)")).toBeTruthy();
    expect(searchBox().getAttribute("placeholder")).toBe("Search needs a connection");
    fireEvent.change(searchBox(), { target: { value: "orders" } });
    expect(screen.getByText("The active tab has no connection")).toBeTruthy();
    expect(screen.queryAllByRole("treeitem")).toEqual([]);
  });

  it("names the scope (server › database) before typing, then lists matches grouped by kind with true counts", () => {
    render(<Explorer />);
    expect(scopeLine().getByText("Srv-p1")).toBeTruthy();
    expect(scopeLine().getByText("AppDb")).toBeTruthy();
    expect(searchBox().getAttribute("placeholder")).toBe("Search AppDb on Srv-p1…");
    fireEvent.change(searchBox(), { target: { value: "orders" } });
    expect(names()).toEqual(["TABLES · 1", `table ${dflt}.Orders`, "VIEWS · 1", `view ${dflt}.orders_view`]);
    expect(scopeLine().getByText("2 matches")).toBeTruthy();
  });

  it("Enter opens the first result; ↓ moves into the results; Esc clears", () => {
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "orders" } });
    fireEvent.keyDown(searchBox(), { key: "Enter" });
    expect(m.scriptObject).toHaveBeenCalledWith("p1", "AppDb", expect.objectContaining({ name: "Orders" }), "selectTop", true);
    fireEvent.keyDown(searchBox(), { key: "ArrowDown" });
    expect(document.activeElement).toBe(tree());
    expect(item(`table ${dflt}.Orders`).getAttribute("aria-selected")).toBe("true");
    fireEvent.keyDown(searchBox(), { key: "Escape" });
    expect(searchBox()).toHaveProperty("value", "");
  });

  it("Ctrl+Enter inserts the qualified name at the editor selection using engine quoting", () => {
    const executeEdits = vi.fn();
    m.editor = { getSelection: () => ({ x: 1 }), executeEdits, focus: vi.fn() };
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "orders_v" } });
    fireEvent.keyDown(searchBox(), { key: "Enter", ctrlKey: true });
    expect(executeEdits).toHaveBeenCalledWith("explorer", [{ range: { x: 1 }, text: quoted, forceMoveMarkers: true }]);
  });

  it("no match: says so in the scope, and offers all kinds when a kind filter hid them", () => {
    render(<Explorer />);
    useExplorer.setState({ kind: "tables" });
    fireEvent.change(searchBox(), { target: { value: "orders_view" } });
    expect(screen.getByText("No tables match “orders_view”")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Search all kinds" }));
    expect(names()).toContain(`view ${dflt}.orders_view`);
  });

  it("the active tab's server is unreachable: says so and offers Reconnect, never another server's results", () => {
    seed(engine, { id: "p2" });
    useConnections.setState((s) => ({ servers: { ...s.servers, p1: { ...s.servers.p1, status: "error", error: "down" } } }));
    useSnapshots.setState({ entries: {} });
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "orders" } });
    expect(screen.getByText("Srv-p1 isn’t connected")).toBeTruthy();
    expect(screen.queryByRole("tree")).toBeNull();
  });

  it("a search result can be revealed in the tree, which opens its path and selects it", () => {
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "orders_view" } });
    openMenu(`view ${dflt}.orders_view`);
    menuClick("Reveal in tree");
    expect(searchBox()).toHaveProperty("value", "");
    expect(item(`view ${dflt}.orders_view`).getAttribute("aria-selected")).toBe("true");
    expect(item("Views, 1").getAttribute("aria-expanded")).toBe("true");
  });

  it("capped kinds offer Show all", () => {
    seed(engine, { objects: Array.from({ length: 320 }, (_, i) => obj(i + 1, dflt, `t${i}`, "table")) });
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "t" } });
    expect(item("TABLES · FIRST 300 OF 320")).toBeTruthy();
    click("TABLES · FIRST 300 OF 320");
    expect(item("TABLES · 320")).toBeTruthy();
  });
});

describe("Explorer environment grouping and ordering", () => {
  it("orders servers Local to Prod, pinned first within an environment, and groups them under collapsible environment rows", () => {
    seed("mssql", { id: "p1", env: "prod" });
    seed("pg", { id: "p2", env: "dev" });
    seed("pg", { id: "p3", env: "local" });
    seed("mssql", { id: "p4", env: "dev" });
    useSettings.setState({ pinnedProfiles: ["p4"] });
    useConnections.setState((s) => ({ servers: Object.fromEntries(Object.entries(s.servers).map(([k, v]) => [k, { ...v, quiet: true }])) }));
    const { rerender } = render(<Explorer />);
    expect(names().map((n) => n!.split(",").slice(0, 2).join(","))).toEqual(["LOCAL, Srv-p3", "DEV, Srv-p4", "DEV, Srv-p2", "PROD, Srv-p1"]);
    act(() => useSettings.setState({ explorerGroupByEnv: true }));
    rerender(<Explorer />);
    expect(names()[0]).toBe("Local, 1 server");
    click("Dev, 2 servers");
    expect(names().some((n) => n?.includes("Srv-p2"))).toBe(false);
  });
});

describe.each<Engine>(["mssql", "pg"])("Explorer context menus (%s)", (engine) => {
  const dflt = engine === "pg" ? "public" : "dbo";
  beforeEach(() => seed(engine, { env: "prod" }));

  it("open on right-click with a header naming the target; items dispatch with the object's identity", () => {
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    openMenu(`table ${dflt}.Orders`);
    expect(menu().getAttribute("aria-label")).toBe(`Actions for PROD Srv-p1 › AppDb · ${dflt}.Orders`);
    expect(within(menu()).getByText(`Srv-p1 › AppDb · ${dflt}.Orders`)).toBeTruthy();
    menuClick("View ER diagram");
    expect(m.openErdForTable).toHaveBeenCalledWith("p1", "AppDb", 1, "Orders");
    openMenu(`table ${dflt}.Orders`);
    menuClick("Compare data…");
    expect(m.openDataCompare).toHaveBeenCalledWith({ profileId: "p1", database: "AppDb", table: `${dflt}.Orders` });
    openMenu(`table ${dflt}.Orders`);
    menuClick(/^Script as/);
    menuClick("INSERT", screen.getByRole("menu", { name: "Script as" }));
    // The engine's default schema is left out of the title (owner revision 1 of the tab strip).
    expect(m.newQueryTab).toHaveBeenCalledWith(expect.objectContaining({ profileId: "p1", database: "AppDb", title: "INSERT Orders.sql" }));
  });

  it("Shift+F10 opens the menu of the selected row from the keyboard", () => {
    render(<Explorer />);
    click("Database AppDb");
    key("F10", { shiftKey: true });
    expect(menuLabels()[0]).toBe("Use in active tab");
  });

  it("the database menu toggles schema grouping and runs its tools", () => {
    render(<Explorer />);
    openMenu("Database AppDb");
    menuClick("Schema compare…");
    expect(m.openSchemaCompare).toHaveBeenCalledWith({ profileId: "p1", database: "AppDb" });
    openMenu("Database AppDb");
    menuClick("Group by schema");
    expect(useSettings.getState().explorerSchemaGroups).toEqual(["p1|AppDb"]);
  });

  it("notes and actions open no menu; Escape closes an open one", () => {
    render(<Explorer />);
    click("Database AppDb");
    openMenu("Database AppDb");
    fireEvent.keyDown(document.activeElement!, { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
  });
});

describe.each<Engine>(["mssql", "pg"])("Explorer keyboard (%s)", (engine) => {
  const dflt = engine === "pg" ? "public" : "dbo";
  beforeEach(() => seed(engine));

  it("is one Tab stop with the selected row as aria-activedescendant; ↑/↓/Home/End move; →/← open, enter and leave levels", () => {
    render(<Explorer />);
    fireEvent.focus(tree());
    expect(tree().getAttribute("aria-activedescendant")).toBe(item(/^DEV, Srv-p1/).id);
    key("ArrowDown");
    expect(item("Database AppDb").getAttribute("aria-selected")).toBe("true");
    key("ArrowRight");
    expect(item("Database AppDb").getAttribute("aria-expanded")).toBe("true");
    key("ArrowRight");
    expect(item("Tables, 2").getAttribute("aria-selected")).toBe("true");
    key("ArrowLeft");
    expect(item("Database AppDb").getAttribute("aria-selected")).toBe("true");
    key("ArrowLeft");
    expect(item("Database AppDb").getAttribute("aria-expanded")).toBe("false");
    key("End");
    expect(item("Database AppDb").getAttribute("aria-selected")).toBe("true");
    key("Home");
    expect(item(/^DEV, Srv-p1/).getAttribute("aria-selected")).toBe("true");
    expect(item(/^DEV, Srv-p1/).getAttribute("aria-level")).toBe("1");
    expect(item("Database AppDb").getAttribute("aria-level")).toBe("2");
  });

  it("Enter runs the default action; Ctrl+Enter inserts the name, Ctrl+Alt+Enter the column list", () => {
    const executeEdits = vi.fn();
    m.editor = { getSelection: () => ({}), executeEdits, focus: vi.fn() };
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t" } }, order: ["t"] } as never);
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    click(`table ${dflt}.Orders`);
    key("Enter");
    expect(m.scriptObject).toHaveBeenCalledWith("p1", "AppDb", expect.objectContaining({ name: "Orders" }), "selectTop", true);
    key("Enter", { ctrlKey: true });
    key("Enter", { ctrlKey: true, altKey: true });
    expect(executeEdits.mock.calls.map((c) => c[1][0].text)).toEqual([engine === "pg" ? "public.\"Orders\"" : "dbo.Orders", engine === "pg" ? "\"Id\", \"Total\"" : "[Id], [Total]"]);
  });

  it("F2 renames, Shift+F12 finds usages, F5 refreshes the database, Ctrl+C copies the qualified name", async () => {
    const refresh = vi.fn(async () => {});
    useSnapshots.setState({ refresh } as never);
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    click(`table ${dflt}.Orders`);
    key("F2");
    expect(m.renameOf).toHaveBeenCalledWith("p1", "AppDb", { schema: dflt, name: "Orders", kind: "table", column: undefined });
    key("F12", { shiftKey: true });
    expect(m.findUsagesOf).toHaveBeenCalledWith("p1", "AppDb", { schema: dflt, name: "Orders", kind: "table", column: undefined });
    key("F5");
    expect(refresh).toHaveBeenCalledWith("p1", "AppDb", false);
    await act(async () => void key("c", { ctrlKey: true }));
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith(engine === "pg" ? 'public."Orders"' : "dbo.Orders");
  });

  it("typing inside a folder filters it in place; Backspace edits, Escape clears; elsewhere typing jumps to a row", () => {
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    key("w");
    key("e");
    expect(item("Tables, 1 of 2, filtered by we")).toBeTruthy();
    expect(item("Filter we, 1 of 2")).toBeTruthy();
    expect(maybe(`table ${dflt}.Orders`)).toBeNull();
    key("Backspace");
    expect(item("Tables, 1 of 2, filtered by w")).toBeTruthy();
    key("Escape");
    expect(item("Tables, 2")).toBeTruthy();
    click(/^DEV, Srv-p1/); // collapse; type-ahead on the server level
    click(/^DEV, Srv-p1/);
    click("Database AppDb");
    click("Database AppDb");
    fireEvent.click(item(/^DEV, Srv-p1/), { detail: 2 }); // select without toggling
    key("a");
    expect(item("Database AppDb").getAttribute("aria-selected")).toBe("true");
  });

  it("a selected row that disappears hands the selection to its nearest ancestor", () => {
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    click(`table ${dflt}.Orders`);
    act(() => ex().toggle(`c:p1:AppDb:tables`, false));
    expect(item("Tables, 2").getAttribute("aria-selected")).toBe("true");
  });
});

describe("Explorer drag and drop", () => {
  const drag = (el: HTMLElement, alt = false) => {
    const data = new Map<string, string>();
    const ev = createEvent.dragStart(el, { dataTransfer: { setData: (t: string, v: string) => data.set(t, v) } });
    if (alt) Object.defineProperty(ev, "altKey", { value: true });
    fireEvent(el, ev);
    return data.get("text/plain");
  };

  it.each<[Engine, string, string]>([["mssql", "dbo.Orders", "[Id], [Total]"], ["pg", 'public."Orders"', '"Id", "Total"']])("a %s table drags its qualified name; Alt-drag and the Columns folder drag its column list", (engine, name, cols) => {
    seed(engine);
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    const t = item(`table ${engine === "pg" ? "public" : "dbo"}.Orders`);
    expect(drag(t)).toBe(name);
    expect(drag(t, true)).toBe(cols);
    click(`table ${engine === "pg" ? "public" : "dbo"}.Orders`);
    expect(drag(item("Columns"))).toBe(cols);
    click("Columns");
    expect(drag(item("Id, int NOT NULL"))).toBe("Id");
  });
});

describe("Explorer lifecycle", () => {
  it("a server that leaves and comes back opens one level again; another server keeps its expansion", () => {
    seed("mssql", { id: "p1" });
    seed("pg", { id: "p2" });
    render(<Explorer />);
    act(() => ex().setExpanded((e) => new Set([...e, "db:p1:AppDb", "db:p2:AppDb"])));
    act(() => useConnections.setState((s) => ({ servers: { p2: s.servers.p2 }, order: ["p2"] })));
    expect(ex().expanded.has("db:p1:AppDb")).toBe(false);
    expect(ex().expanded.has("db:p2:AppDb")).toBe(true);
    act(() => seed("mssql", { id: "p1" }));
    expect(ex().expanded.has("s:p1")).toBe(true);
    expect(ex().expanded.has("db:p1:AppDb")).toBe(false);
  });

  it("only the rows in the scrolled window are rendered", () => {
    const many = Array.from({ length: 400 }, (_, i) => obj(i + 1, "dbo", `t${String(i).padStart(3, "0")}`, "table"));
    seed("mssql", { objects: many });
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 400");
    expect(names().length).toBeLessThan(120);
    expect(maybe("table dbo.t399")).toBeNull();
    act(() => {
      tree().scrollTop = 395 * 24;
      fireEvent.scroll(tree());
    });
    expect(maybe("table dbo.t390")).toBeTruthy();
  });
});

describe.each<Engine>(["mssql", "pg"])("Explorer triggers (%s)", (engine) => {
  const s = engine === "pg" ? "public" : "dbo";
  const objects = () => [
    obj(1, s, "Orders", "table", { columns: [col("Id")] }),
    obj(2, s, "orders_view", "view", { columns: [col("Id")] }),
    obj(3, s, "b_audit", "trigger", { parentObjectId: 1 }),
    obj(4, s, "a_check", "trigger", { parentObjectId: 1 }),
    obj(5, s, "v_instead", "trigger", { parentObjectId: 2 }),
    obj(6, s, "plain_view", "view", { columns: [] }),
  ];
  beforeEach(() => seed(engine, { objects: objects() }));

  it("lists triggers in a Triggers folder under their table, sorted, never at the top level; a view shows the folder only with triggers", () => {
    render(<Explorer />);
    click("Database AppDb");
    expect(names().some((n) => n?.startsWith("Triggers,"))).toBe(false);
    click("Tables, 1");
    click(`table ${s}.Orders`);
    click("Triggers");
    expect(names().filter((n) => n?.startsWith("trigger"))).toEqual([`trigger ${s}.a_check`, `trigger ${s}.b_audit`]);
    click("Views, 2");
    click(`view ${s}.plain_view`);
    expect(names().filter((n) => n === "Triggers")).toHaveLength(1);
  });

  it("search lists matching triggers", () => {
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "AppDb" } }, order: ["t"] } as never);
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "audit" } });
    expect(names()).toEqual(["TRIGGERS · 1", `trigger ${s}.b_audit`]);
  });

  it("Reveal in tree on a trigger result opens its table's Triggers folder and selects it there", () => {
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "AppDb" } }, order: ["t"] } as never);
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "audit" } });
    openMenu(`trigger ${s}.b_audit`);
    menuClick("Reveal in tree");
    expect(item(`table ${s}.Orders`).getAttribute("aria-expanded")).toBe("true");
    expect(item(`trigger ${s}.b_audit`).getAttribute("aria-selected")).toBe("true");
  });

  it("a trigger on a view found by search: ENABLE / DISABLE say why on PostgreSQL, as in the tree", () => {
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "AppDb" } }, order: ["t"] } as never);
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "v_instead" } });
    openMenu(`trigger ${s}.v_instead`);
    menuClick(/^Script as/);
    const labels = menuEntries(screen.getByRole("menu", { name: "Script as" })).map((b) => b.getAttribute("aria-label"));
    expect(labels).toContain(engine === "pg" ? "ENABLE, ALTER TABLE does not accept views" : "ENABLE");
  });

  it(engine === "pg" ? "Modify opens the trigger function found through its dependencies" : "Modify opens the trigger itself", async () => {
    m.rpc.mockImplementation(async (method: unknown) => (method === "meta.dependencies" ? [{ direction: "uses", object: { schema: s, name: "audit_fn", kind: "func", objectId: 40 } }] : details));
    await modifyTrigger("p1", "AppDb", objects()[2]);
    expect(m.openDefinition).toHaveBeenCalledWith("p1", "AppDb", engine === "pg" ? { schema: s, name: "audit_fn", kind: "func", objectId: 40 } : { schema: s, name: "b_audit", kind: "trigger", objectId: 3 }, true);
  });
});

describe("Explorer triggers: failure paths", () => {
  beforeEach(() => seed("pg"));
  it("PostgreSQL Modify without a visible trigger function says so", async () => {
    m.rpc.mockImplementation(async () => []);
    await modifyTrigger("p1", "AppDb", obj(3, "public", "t", "trigger"));
    expect(useUi.getState().toasts.at(-1)?.text).toMatch(/function of trigger t was not found/);
  });
  it("a dependency lookup failure is reported, not swallowed", async () => {
    m.rpc.mockRejectedValueOnce(new Error("boom"));
    await modifyTrigger("p1", "AppDb", obj(3, "public", "t", "trigger"));
    expect(useUi.getState().toasts.at(-1)?.text).toBe("boom");
  });
});

describe("generateDmlTemplate", () => {
  const table = (engine: "mssql" | "pg", overCols?: object[]): SnapObject => ({
    id: 1,
    schema: engine === "pg" ? "public" : "dbo",
    name: "Orders",
    kind: "table",
    columns: (overCols ?? [
      { name: "Id", type: "int", pk: true, identity: true, computed: false, nullable: false, ordinal: 1 },
      { name: "Customer", type: "varchar", pk: false, identity: false, computed: false, nullable: false, ordinal: 2 },
      { name: "Total", type: "money", pk: false, identity: false, computed: false, nullable: true, ordinal: 3 },
      { name: "Tax", type: "money", pk: false, identity: false, computed: true, nullable: true, ordinal: 4 },
    ]) as never,
  });

  it("generates INSERT statement skipping identity and computed columns", () => {
    const tMssql = table("mssql");
    const insertMssql = generateDmlTemplate(tMssql, "mssql", "insert");
    expect(insertMssql).toBe("INSERT INTO dbo.Orders ([Customer], [Total])\nVALUES (/* Customer (varchar) */ <value>, /* Total (money) */ <value>);\n");

    const tPg = table("pg");
    const insertPg = generateDmlTemplate(tPg, "pg", "insert");
    expect(insertPg).toBe('INSERT INTO public."Orders" ("Customer", "Total")\nVALUES (/* Customer (varchar) */ <value>, /* Total (money) */ <value>);\n');
  });

  it("generates UPDATE statement updating non-PK columns with WHERE clause for PKs", () => {
    const tMssql = table("mssql");
    const updateMssql = generateDmlTemplate(tMssql, "mssql", "update");
    expect(updateMssql).toBe("UPDATE dbo.Orders\nSET\n    [Customer] = /* varchar */ <value>,\n    [Total] = /* money */ <value>\nWHERE [Id] = /* int */ <value>;\n");

    const tPg = table("pg");
    const updatePg = generateDmlTemplate(tPg, "pg", "update");
    expect(updatePg).toBe('UPDATE public."Orders"\nSET\n    "Customer" = /* varchar */ <value>,\n    "Total" = /* money */ <value>\nWHERE "Id" = /* int */ <value>;\n');
  });

  it("generates DELETE statement with WHERE clause for PKs", () => {
    const tMssql = table("mssql");
    const delMssql = generateDmlTemplate(tMssql, "mssql", "delete");
    expect(delMssql).toBe("DELETE FROM dbo.Orders\nWHERE [Id] = /* int */ <value>;\n");

    const tPg = table("pg");
    const delPg = generateDmlTemplate(tPg, "pg", "delete");
    expect(delPg).toBe('DELETE FROM public."Orders"\nWHERE "Id" = /* int */ <value>;\n');
  });

  it("handles tables without PK by falling back to the first column", () => {
    const t = table("mssql", [
      { name: "Code", type: "varchar", pk: false, identity: false, computed: false, nullable: false, ordinal: 1 },
      { name: "Desc", type: "varchar", pk: false, identity: false, computed: false, nullable: true, ordinal: 2 },
    ]);
    const del = generateDmlTemplate(t, "mssql", "delete");
    expect(del).toBe("DELETE FROM dbo.Orders\nWHERE [Code] = /* varchar */ <value>;\n");
  });

  it("handles tables without columns gracefully", () => {
    const empty: SnapObject = { id: 2, schema: "dbo", name: "Empty", kind: "table", columns: [] };
    expect(generateDmlTemplate(empty, "mssql", "insert")).toContain("/* columns */");
    expect(generateDmlTemplate(empty, "mssql", "update")).toContain("/* column = <value> */");
    expect(generateDmlTemplate(empty, "mssql", "delete")).toContain("/* specify condition */");
  });
});

it.each(["mssql", "pg"] as const)("DML templates quote keywords and do not reintroduce generated columns (%s)", (engine) => {
  const obj: SnapObject = { id: 1, schema: "public", name: "order", kind: "table", columns: [{ name: "id", type: "int", identity: true, computed: false, nullable: false, pk: true, ordinal: 0 }] };
  expect(generateDmlTemplate(obj, engine, "insert")).toContain("DEFAULT VALUES");
  expect(generateDmlTemplate(obj, engine, "update")).toContain("No writable");
  expect(generateDmlTemplate(obj, engine, "delete")).toContain(engine === "pg" ? '"order"' : '[order]');
  obj.columns![0] = { ...obj.columns![0], identity: false, type: 'odd*/type' };
  expect(generateDmlTemplate(obj, engine, "insert")).toContain("odd* /type");
});

describe.each<Engine>(["mssql", "pg"])("Explorer review fixes, 2026-10-30 (%s)", (engine) => {
  const dflt = engine === "pg" ? "public" : "dbo";
  beforeEach(() => seed(engine));
  const objectDetailCalls = () => m.rpc.mock.calls.filter((c) => c[0] === "meta.objectDetails").length;

  it("a press on a row with nothing selected selects that row: focusing does not select the first row and jump to the top", () => {
    render(<Explorer />);
    const row = item("Database AppDb");
    fireEvent.pointerDown(row);
    act(() => tree().focus());
    expect(ex().selected).toBeUndefined();
    fireEvent.pointerUp(row);
    fireEvent.click(row);
    expect(ex().selected).toBe("db:p1:AppDb");
    // Focus from the keyboard (Tab) still selects the first row.
    act(() => tree().blur());
    useExplorer.setState({ selected: undefined });
    act(() => tree().focus());
    expect(ex().selected).toBe("s:p1");
  });

  it("a double-click on a folder leaves it open (its first click opened it; the double-click does not close it again)", () => {
    render(<Explorer />);
    click("Database AppDb");
    for (const name of ["Tables, 2", /^DEV, Srv-p1/]) {
      useExplorer.getState().toggle(name === "Tables, 2" ? "c:p1:AppDb:tables" : "s:p1", false);
      const el = item(name);
      fireEvent.click(el, { detail: 1 });
      fireEvent.click(el, { detail: 2 });
      fireEvent.doubleClick(el);
      expect(item(name).getAttribute("aria-expanded")).toBe("true");
    }
  });

  it("an environment group closes and opens again from a click", () => {
    useSettings.setState({ explorerGroupByEnv: true });
    render(<Explorer />);
    const env = () => item("Dev, 1 server");
    click("Dev, 1 server");
    expect(env().getAttribute("aria-expanded")).toBe("false");
    click("Dev, 1 server");
    expect(env().getAttribute("aria-expanded")).toBe("true");
    expect(maybe(/^DEV, Srv-p1/)).toBeTruthy();
  });

  it("while its server reconnects, Enter, double-click, F2 and Shift+F12 on an object say so and dial nothing", () => {
    seed(engine, { conn: { status: "connecting", sessionId: undefined } });
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    useExplorer.setState({ expanded: new Set(["s:p1", "db:p1:AppDb", "c:p1:AppDb:tables"]) });
    render(<Explorer />);
    click(`table ${dflt}.Orders`);
    key("Enter");
    dbl(`table ${dflt}.Orders`);
    key("F2");
    key("F12", { shiftKey: true });
    expect(m.scriptObject).not.toHaveBeenCalled();
    expect(m.renameOf).not.toHaveBeenCalled();
    expect(m.findUsagesOf).not.toHaveBeenCalled();
    expect(connect).not.toHaveBeenCalled();
    expect(useUi.getState().toasts.at(-1)?.text).toBe("Waiting for Srv-p1 to reconnect.");
  });

  it("Ctrl+Enter on a column inserts it quoted for the engine, as its menu's Insert name does", () => {
    const executeEdits = vi.fn();
    m.editor = { getSelection: () => ({}), executeEdits, focus: vi.fn() };
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t" } }, order: ["t"] } as never);
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    click(`table ${dflt}.Orders`);
    click("Columns");
    click("Id, int NOT NULL");
    key("Enter", { ctrlKey: true });
    expect(executeEdits.mock.calls.map((c) => c[1][0].text)).toEqual([engine === "pg" ? '"Id"' : "[Id]"]);
  });

  it("keys read once stay through a status-only catalog update; a changed object is read again", async () => {
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    click(`table ${dflt}.Orders`);
    await act(async () => void click("Keys"));
    await waitFor(() => expect(item(/^PK_Orders, PK \(Id\)/)).toBeTruthy());
    const calls = objectDetailCalls();
    const k = snapKey("p1", "AppDb");
    const bump = (patch: object) => act(() => useSnapshots.setState((st) => ({ entries: { ...st.entries, [k]: { ...st.entries[k], ...patch, version: st.entries[k].version + 1 } } })));
    bump({ status: "refreshing" });
    bump({ status: "fresh" });
    expect(objectDetailCalls()).toBe(calls);
    expect(item(/^PK_Orders, PK \(Id\)/)).toBeTruthy();
    // The refresh changed the table: its record is replaced, so its keys are read again.
    const e = useSnapshots.getState().entries[k];
    const objects = e.snapshot!.objects.map((o) => (o.id === 1 ? { ...o } : o));
    bump({ snapshot: { ...e.snapshot, objects } });
    await waitFor(() => expect(objectDetailCalls()).toBe(calls + 1));
  });

  it("a late reply for an older record does not overwrite the newer read", async () => {
    let late!: (d: ObjectDetails) => void;
    m.rpc.mockImplementationOnce(() => new Promise((r) => (late = r as never)));
    render(<Explorer />);
    click("Database AppDb");
    click("Tables, 2");
    click(`table ${dflt}.Orders`);
    act(() => click("Keys"));
    expect(objectDetailCalls()).toBe(1);
    const k = snapKey("p1", "AppDb");
    const e = useSnapshots.getState().entries[k];
    act(() => useSnapshots.setState((st) => ({ entries: { ...st.entries, [k]: { ...e, snapshot: { ...e.snapshot!, objects: e.snapshot!.objects.map((o) => (o.id === 1 ? { ...o } : o)) }, version: e.version + 1 } } })));
    await waitFor(() => expect(item(/^PK_Orders, PK \(Id\)/)).toBeTruthy());
    await act(async () => late({ ...details, indexes: [{ name: "OLD_PK", isUnique: true, isPrimary: true, columns: ["Id"] }] }));
    expect(maybe(/^OLD_PK/)).toBeNull();
    expect(item(/^PK_Orders, PK \(Id\)/)).toBeTruthy();
    expect(objectDetailCalls()).toBe(2);
  });

  it("Reveal in tree clears what would hide the result: a collapsed environment group and its folder's filter", () => {
    useSettings.setState({ explorerGroupByEnv: true });
    useTabs.setState({ activeId: "t", tabs: { t: { id: "t", profileId: "p1", database: "AppDb" } }, order: ["t"] } as never);
    useExplorer.setState({ collapsedEnvs: new Set(["dev"]), folderFilters: { "c:p1:AppDb:views": "zzz" } });
    render(<Explorer />);
    fireEvent.change(searchBox(), { target: { value: "orders_v" } });
    openMenu(`view ${dflt}.orders_view`);
    menuClick("Reveal in tree");
    expect(item(`view ${dflt}.orders_view`).getAttribute("aria-selected")).toBe("true");
    expect(ex().folderFilters["c:p1:AppDb:views"]).toBe("");
    expect(ex().collapsedEnvs.has("dev")).toBe(false);
  });

  it("restored database keys under a collapsed server read no catalog until shown", () => {
    const ensure = vi.fn();
    useSnapshots.setState({ entries: {}, ensure } as never);
    useConnections.setState((st) => ({ servers: { ...st.servers, p1: { ...st.servers.p1, databases: [{ name: "AppDb", isSystem: false }, { name: "Other", isSystem: false }] } } }));
    useExplorer.setState({ expanded: new Set(["db:p1:AppDb", "db:p1:Other"]) });
    render(<Explorer />);
    // The server opened (a fresh connect opens one level): both restored databases are now in view.
    expect(ensure.mock.calls.map((c) => c[1]).sort()).toEqual(["AppDb", "Other"]);
    ensure.mockClear();
    act(() => useExplorer.getState().toggle("s:p1", false));
    act(() => useSnapshots.setState({ entries: {} }));
    expect(ensure).not.toHaveBeenCalled();
  });
});

describe("session-owned engines (SQLite, Phase D0d)", () => {
  const detailCalls = () => m.rpc.mock.calls.filter((c) => c[0] === "meta.objectDetails").map((c) => c[1]);
  const expandKeys = async () => {
    click(/^Database AppDb/);
    click("Tables, 2");
    click("table dbo.Orders");
    await act(async () => void click("Keys"));
  };

  it("an object's details are read through the active SQLite tab's session (its temporary tables live only there)", async () => {
    seed("sqlite");
    useTabs.setState({ activeId: "t1", tabs: { t1: { id: "t1", title: "q", profileId: "p1", database: "AppDb", sessionId: "s1" } }, order: ["t1"] } as never);
    render(<Explorer />);
    await expandKeys();
    await waitFor(() => expect(detailCalls()).toEqual([{ profileId: "p1", database: "AppDb", objectId: 1, sessionId: "s1" }]));
  });

  it("a server engine's tab never sends its session", async () => {
    seed("mssql");
    useTabs.setState({ activeId: "t1", tabs: { t1: { id: "t1", title: "q", profileId: "p1", database: "AppDb", sessionId: "s1" } }, order: ["t1"] } as never);
    render(<Explorer />);
    await expandKeys();
    await waitFor(() => expect(detailCalls()).toEqual([{ profileId: "p1", database: "AppDb", objectId: 1 }]));
  });
});

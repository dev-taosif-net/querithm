// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  tauri: false,
  files: new Map<string, string>(),
  models: new Map<string, any>(),
  contentListeners: [] as ((id: string, m: any) => void)[],
  save: vi.fn(),
  open: vi.fn(),
  allOrder: undefined as undefined | (() => string[]),
  appInfo: vi.fn(async (): Promise<any> => ({ workspaceOwner: true })),
}));

vi.mock("../../ipc/transport", () => ({ get isTauri() { return h.tauri; } }));
vi.mock("@tauri-apps/plugin-fs", () => ({
  BaseDirectory: { Data: 1 },
  mkdir: vi.fn(async () => {}),
  writeTextFile: vi.fn(async (p: string, t: string) => { h.files.set(p, t); }),
  readTextFile: vi.fn(async (p: string) => { if (!h.files.has(p)) throw new Error("ENOENT"); return h.files.get(p)!; }),
  remove: vi.fn(async (p: string) => { h.files.delete(p); }),
  rename: vi.fn(async (a: string, b: string) => { if (!h.files.has(a)) throw new Error("ENOENT"); h.files.set(b, h.files.get(a)!); h.files.delete(a); }),
  readDir: vi.fn(async (d: string) => [...h.files.keys()].filter((k) => k.startsWith(`${d}/`) && !k.slice(d.length + 1).includes("/")).map((k) => ({ name: k.slice(d.length + 1), isFile: true, isDirectory: false, isSymlink: false }))),
}));
vi.mock("../appInfo", () => ({ getAppInfo: () => h.appInfo() }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ save: (...a: any[]) => h.save(...a), open: (...a: any[]) => h.open(...a) }));
vi.mock("../../editor/editors", () => ({
  getModel: (id: string) => h.models.get(id),
  editorForTab: (id: string) => (id === "a" ? { getPosition: () => ({ lineNumber: 7 }) } : undefined),
  onModelContent: (l: any) => { h.contentListeners.push(l); return () => h.contentListeners.splice(h.contentListeners.indexOf(l), 1); },
}));
const newQueryTab = vi.fn((o: any) => {
  const { useTabs } = require_tabs();
  useTabs.getState().add({ id: o.id ?? "new", title: o.title ?? "t", profileId: o.profileId, database: o.database, pinned: o.pinned, filePath: o.filePath });
  h.models.set(o.id ?? "new", mkModel(o.text ?? ""));
  return o.id ?? "new";
});
vi.mock("../actions", () => ({
  newQueryTab: (o: any) => newQueryTab(o),
  activeTabState: () => require_tabs().useTabs.getState().tabs[require_tabs().useTabs.getState().activeId!],
  workspaceHooks: { get allOrder() { return h.allOrder; } },
}));

vi.mock("../../ipc/client", () => ({ rpc: vi.fn(), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
// Notebooks (Phase C3): the runtime and its files are tested on their own; here, only how persistence drives them.
const nb = vi.hoisted(() => ({
  markDirty: vi.fn(),
  newNotebookTab: vi.fn(),
  serializeTab: vi.fn((id: string) => `{"nb":"${id}"}`),
  openNotebookText: vi.fn(() => "nb1" as string | null),
  openNotebookPath: vi.fn(),
  saveNotebook: vi.fn(async () => true),
}));
vi.mock("../../notebook/runtime", () => ({
  markDirty: nb.markDirty,
  newNotebookTab: nb.newNotebookTab,
  serializeTab: nb.serializeTab,
  ownerOfKey: (k: string) => (k.includes("::") ? k.slice(0, k.indexOf("::")) : undefined),
}));
vi.mock("../../notebook/files", () => ({ openNotebookText: nb.openNotebookText, openNotebookPath: nb.openNotebookPath, saveNotebook: nb.saveNotebook }));

import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";
import { useSettings } from "../../store/settings";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { flushAutosave, installAutosave, LIVE_BEAT_MS, LIVE_STALE_MS, openFile, RENAME_RETRIES, RENAME_RETRY_MS, resetPersistence, restoreWorkspace, saveAs, saveFile, saveWorkspaceNow } from "../persistence";
import { useExplorer } from "../../explorer/explorerState";
import { useNotebooks } from "../../notebook/store";

function require_tabs() { return { useTabs }; }
function mkModel(text: string) {
  const m = { text, disposed: false, getValue: () => m.text, isDisposed: () => m.disposed };
  return m;
}
const tick = () => new Promise((r) => setTimeout(r, 0));

beforeEach(() => {
  h.tauri = false;
  h.files.clear();
  h.models.clear();
  h.contentListeners.length = 0;
  h.allOrder = undefined;
  h.save.mockReset();
  h.open.mockReset();
  newQueryTab.mockClear();
  localStorage.clear();
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useUi.setState({ toasts: [], explorerWidth: 290, banner: null });
  resetPersistence();
  h.appInfo.mockReset();
  h.appInfo.mockImplementation(async () => ({ workspaceOwner: true }));
  useSettings.setState({ autosave: true } as any);
  connect.mockClear();
  useConnections.setState({ servers: {}, order: [], disconnected: {}, connect } as any);
  useProfiles.setState({ byId: (id?: string) => (id === "ms1" || id === "pg1" ? ({ id } as any) : undefined) } as any);
});
const connect = vi.fn(async () => true);
afterEach(() => vi.useRealTimers());

describe("restoreWorkspace", () => {
  it("returns false with no saved workspace or an empty/corrupt one", async () => {
    expect(await restoreWorkspace()).toBe(false);
    localStorage.setItem("querithm.workspace", "{not json");
    expect(await restoreWorkspace()).toBe(false);
    localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, tabs: [] }));
    expect(await restoreWorkspace()).toBe(false);
    // A hand-edited file whose tabs is not a list opens nothing (a string has a length but no tabs).
    for (const tabs of ["abc", { length: 2 }, null]) {
      localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, tabs }));
      expect(await restoreWorkspace()).toBe(false);
    }
    expect(Object.keys(useTabs.getState().tabs)).toHaveLength(0);
  });

  it("restores tabs with their text, binding, active tab and explorer width (browser storage)", async () => {
    localStorage.setItem("querithm.workspace", JSON.stringify({
      version: 1, activeId: "b", explorerWidth: 333,
      tabs: [{ id: "a", title: "a.sql", profileId: "ms1", database: "db" }, { id: "b", title: "b.sql", profileId: "pg1", pinned: true }],
    }));
    localStorage.setItem("querithm.script.a", "select 1");
    expect(await restoreWorkspace()).toBe(true);
    expect(newQueryTab.mock.calls[0][0]).toMatchObject({ id: "a", text: "select 1", activate: false, atEnd: true, profileId: "ms1" });
    expect(newQueryTab.mock.calls[1][0]).toMatchObject({ id: "b", text: "", activate: true, pinned: true });
    expect(useTabs.getState().activeId).toBe("b");
    expect(useUi.getState().explorerWidth).toBe(333);
  });

  it("reconnects the explorer's servers quietly (both engines), skipping deleted profiles, duplicates and junk", async () => {
    localStorage.setItem("querithm.workspace", JSON.stringify({
      version: 1, tabs: [{ id: "a", title: "a" }], servers: ["ms1", "gone", 42, "pg1", "ms1", null],
    }));
    expect(await restoreWorkspace()).toBe(true);
    expect(connect.mock.calls).toEqual([["ms1", { quiet: true }], ["pg1", { quiet: true }]]);
  });

  it("reconnects servers even when no tab was saved; a workspace without servers connects nothing", async () => {
    localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, tabs: [], servers: ["pg1"] }));
    expect(await restoreWorkspace()).toBe(false);
    expect(connect).toHaveBeenCalledWith("pg1", { quiet: true });
    connect.mockClear();
    localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, tabs: [{ id: "a", title: "a" }], servers: "ms1" }));
    await restoreWorkspace();
    expect(connect).not.toHaveBeenCalled();
  });

  it("does not wait for the reconnect: startup goes on while a server is still dialing", async () => {
    connect.mockImplementationOnce(() => new Promise<boolean>(() => {}));
    localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, tabs: [{ id: "a", title: "a" }], servers: ["ms1"] }));
    expect(await restoreWorkspace()).toBe(true);
  });

  it("restores through the app-data files under Tauri, and tolerates a missing script file", async () => {
    h.tauri = true;
    h.files.set("querithm/scripts/workspace.json", JSON.stringify({ version: 1, tabs: [{ id: "a", title: "a" }, { id: "z", title: "z" }] }));
    h.files.set("querithm/scripts/a.sql", "select now()");
    expect(await restoreWorkspace()).toBe(true);
    expect(newQueryTab.mock.calls.map((c) => c[0].text)).toEqual(["select now()", ""]);
  });
});

describe("installAutosave", () => {
  it("writes a changed script after 2 s, and marks file-backed tabs dirty", () => {
    vi.useFakeTimers();
    useTabs.getState().add({ id: "a", title: "a.sql", filePath: "/x.sql" });
    const off = installAutosave();
    const m = mkModel("select 2");
    h.models.set("a", m);
    h.contentListeners.forEach((l) => l("a", m));
    expect(useTabs.getState().tabs.a.dirty).toBe(true);
    vi.advanceTimersByTime(1999);
    expect(localStorage.getItem("querithm.script.a")).toBeNull();
    vi.advanceTimersByTime(10);
    expect(localStorage.getItem("querithm.script.a")).toBe("select 2");
    off();
  });

  it("does not write when autosave is off, for debugger tabs, or a disposed model", () => {
    vi.useFakeTimers();
    const off = installAutosave();
    useTabs.getState().add({ id: "a", title: "a.sql" });
    useTabs.getState().add({ id: "d", title: "🐞 debug" });
    localStorage.clear();
    useSettings.setState({ autosave: false } as any);
    h.contentListeners.forEach((l) => l("a", mkModel("x")));
    useSettings.setState({ autosave: true } as any);
    h.contentListeners.forEach((l) => l("d", mkModel("x")));
    const dead = mkModel("y");
    dead.disposed = true;
    h.contentListeners.forEach((l) => l("a", dead));
    h.contentListeners.forEach((l) => l("unknown", mkModel("z")));
    vi.advanceTimersByTime(3000);
    expect(localStorage.getItem("querithm.script.a")).toBeNull();
    expect(localStorage.getItem("querithm.script.d")).toBeNull();
    off();
  });

  it("saves new tabs immediately, removes the script of closed tabs and snapshots the workspace", () => {
    vi.useFakeTimers();
    const off = installAutosave();
    h.models.set("a", mkModel("select 1"));
    useTabs.getState().add({ id: "a", title: "a.sql", profileId: "ms1", pinned: true });
    useTabs.getState().add({ id: "d", title: "🐞 dbg" });
    h.allOrder = () => ["a"];
    vi.advanceTimersByTime(1100);
    expect(localStorage.getItem("querithm.script.a")).toBe("select 1");
    const ws = JSON.parse(localStorage.getItem("querithm.workspace")!);
    expect(ws.tabs).toHaveLength(1);
    expect(ws.tabs[0]).toMatchObject({ id: "a", profileId: "ms1", pinned: true, cursorLine: 7 });
    useTabs.getState().remove("a");
    expect(localStorage.getItem("querithm.script.a")).toBeNull();
    off();
  });

  it("saves the explorer's servers when they change, so the next launch reconnects exactly those", () => {
    vi.useFakeTimers();
    const off = installAutosave();
    useConnections.setState({ order: ["ms1", "pg1"] });
    vi.advanceTimersByTime(1100);
    expect(JSON.parse(localStorage.getItem("querithm.workspace")!).servers).toEqual(["ms1", "pg1"]);
    // A disconnect removes the server: it is not reconnected next time.
    useConnections.setState({ order: ["pg1"] });
    vi.advanceTimersByTime(1100);
    expect(JSON.parse(localStorage.getItem("querithm.workspace")!).servers).toEqual(["pg1"]);
    off();
    useConnections.setState({ order: [] });
    vi.advanceTimersByTime(1100);
    expect(JSON.parse(localStorage.getItem("querithm.workspace")!).servers).toEqual(["pg1"]);
  });

  it("saves the Explorer's expansion by name when it changes, and restores it after the servers rejoin (§10)", async () => {
    vi.useFakeTimers();
    const off = installAutosave();
    useExplorer.setState({ expanded: new Set(["s:ms1", "db:ms1:SalesDB", "c:ms1:SalesDB:tables", "s:pg1"]) });
    vi.advanceTimersByTime(1100);
    const saved = JSON.parse(localStorage.getItem("querithm.workspace")!).explorerExpanded;
    expect(saved).toEqual(["s:ms1", "db:ms1:SalesDB", "c:ms1:SalesDB:tables", "s:pg1"]);
    off();
    vi.useRealTimers();
    useExplorer.setState({ expanded: new Set() });
    localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, tabs: [], servers: ["ms1"], explorerExpanded: [...saved, 42, "x".repeat(5000), "evil:key", null] }));
    await restoreWorkspace();
    expect([...useExplorer.getState().expanded]).toEqual(["s:ms1", "db:ms1:SalesDB", "c:ms1:SalesDB:tables", "s:pg1"]);
  });

  it("flushes the workspace on beforeunload until disposed", () => {
    const off = installAutosave();
    useTabs.getState().add({ id: "a", title: "a" });
    localStorage.removeItem("querithm.workspace");
    window.dispatchEvent(new Event("beforeunload"));
    expect(localStorage.getItem("querithm.workspace")).toContain('"a"');
    off();
    localStorage.removeItem("querithm.workspace");
    window.dispatchEvent(new Event("beforeunload"));
    expect(localStorage.getItem("querithm.workspace")).toBeNull();
  });

  it("quitting writes a script still waiting on its debounce, then the workspace; before autosave is installed nothing (H4)", async () => {
    vi.useFakeTimers();
    await flushAutosave(); // the workspace is not restored yet: writing it would replace the saved tabs
    expect(localStorage.getItem("querithm.workspace")).toBeNull();
    useTabs.getState().add({ id: "a", title: "a.sql" });
    const off = installAutosave();
    const m = mkModel("select 3");
    h.models.set("a", m);
    h.contentListeners.forEach((l) => l("a", m));
    expect(localStorage.getItem("querithm.script.a")).toBeNull();
    await flushAutosave();
    expect(localStorage.getItem("querithm.script.a")).toBe("select 3");
    expect(localStorage.getItem("querithm.workspace")).toContain('"a"');
    // The debounce it replaced does not fire any more.
    m.text = "typed after the flush";
    vi.advanceTimersByTime(3000);
    expect(localStorage.getItem("querithm.script.a")).toBe("select 3");
    off();
  });

  it("beforeunload also writes the scripts still waiting on their debounce", () => {
    vi.useFakeTimers();
    useTabs.getState().add({ id: "a", title: "a.sql" });
    const off = installAutosave();
    const m = mkModel("select 4");
    h.models.set("a", m);
    h.contentListeners.forEach((l) => l("a", m));
    window.dispatchEvent(new Event("beforeunload"));
    expect(localStorage.getItem("querithm.script.a")).toBe("select 4");
    off();
  });

  it("writes scripts to app-data files under Tauri", async () => {
    h.tauri = true;
    vi.useFakeTimers();
    const off = installAutosave();
    useTabs.getState().add({ id: "a", title: "a.sql" });
    const m = mkModel("select 9");
    h.contentListeners.forEach((l) => l("a", m));
    await vi.advanceTimersByTimeAsync(2100);
    expect(h.files.get("querithm/scripts/a.sql")).toBe("select 9");
    useTabs.getState().remove("a");
    await vi.advanceTimersByTimeAsync(1100);
    expect(h.files.has("querithm/scripts/a.sql")).toBe(false);
    expect(h.files.get("querithm/scripts/workspace.json")).toContain('"version"');
    off();
  });
});

describe("saveAs / saveFile / openFile", () => {
  it("do nothing without a tab or model", async () => {
    await saveAs();
    await saveFile();
    await saveAs("nope");
    expect(h.save).not.toHaveBeenCalled();
  });

  it("browser: Save As downloads <title>.sql", async () => {
    URL.createObjectURL = vi.fn(() => "blob:x");
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    useTabs.getState().add({ id: "a", title: "q" });
    h.models.set("a", mkModel("select 1"));
    await saveAs("a");
    expect(click).toHaveBeenCalled();
    expect(URL.createObjectURL).toHaveBeenCalled();
    useTabs.getState().update("a", { title: "q.sql" });
    await saveFile(); // browser: always Save As
    expect(click).toHaveBeenCalledTimes(2);
    click.mockRestore();
  });

  it("Tauri: Save As writes the chosen path and renames the tab; cancelling does nothing", async () => {
    h.tauri = true;
    useTabs.getState().add({ id: "a", title: "q" });
    h.models.set("a", mkModel("select 1"));
    h.save.mockResolvedValueOnce(null);
    await saveAs("a");
    expect(useTabs.getState().tabs.a.filePath).toBeUndefined();
    h.save.mockResolvedValueOnce("/home/u/my.sql");
    await saveAs("a");
    expect(h.files.get("/home/u/my.sql")).toBe("select 1");
    expect(useTabs.getState().tabs.a).toMatchObject({ filePath: "/home/u/my.sql", title: "my.sql", dirty: false });
    expect(useUi.getState().toasts[0].text).toMatch(/Saved \/home\/u\/my.sql/);
  });

  it("Tauri: Save writes to the existing path, or falls back to Save As", async () => {
    h.tauri = true;
    useTabs.getState().add({ id: "a", title: "a", filePath: "C:\\w\\a.sql", dirty: true });
    h.models.set("a", mkModel("select 5"));
    await saveFile();
    expect(h.files.get("C:\\w\\a.sql")).toBe("select 5");
    expect(useTabs.getState().tabs.a.dirty).toBe(false);
    // Restored tab whose external path lacks Tauri fs scope in the new session falls back to Save As.
    const fsMod = await import("@tauri-apps/plugin-fs");
    vi.mocked(fsMod.writeTextFile).mockRejectedValueOnce(new Error("path not allowed on the configured scope"));
    h.save.mockResolvedValueOnce("C:\\w\\a.sql");
    await saveFile();
    expect(h.save).toHaveBeenCalledWith(expect.objectContaining({ defaultPath: "C:\\w\\a.sql" }));
    h.save.mockClear();
    useTabs.getState().update("a", { filePath: undefined });
    h.save.mockResolvedValueOnce(null);
    await saveFile();
    expect(h.save).toHaveBeenCalled();
  });

  it("browser: openFile opens a picked file in a new tab", async () => {
    const input = { click: vi.fn(), files: [{ name: "f.sql", text: async () => "select 3" }] } as any;
    const create = vi.spyOn(document, "createElement").mockReturnValue(input);
    await openFile();
    expect(input.accept).toContain(".sql");
    await input.onchange();
    expect(newQueryTab).toHaveBeenCalledWith({ text: "select 3", title: "f.sql" });
    input.files = [];
    newQueryTab.mockClear();
    await input.onchange();
    expect(newQueryTab).not.toHaveBeenCalled();
    create.mockRestore();
  });

  it("Tauri: openFile reads the picked path; cancel and multi-select are ignored", async () => {
    h.tauri = true;
    h.open.mockResolvedValueOnce(null);
    await openFile();
    h.open.mockResolvedValueOnce(["a", "b"]);
    await openFile();
    expect(newQueryTab).not.toHaveBeenCalled();
    h.files.set("/p/x.sql", "select 4");
    h.open.mockResolvedValueOnce("/p/x.sql");
    await openFile();
    expect(newQueryTab).toHaveBeenCalledWith({ text: "select 4", title: "x.sql", filePath: "/p/x.sql" });
    await tick();
  });
});

describe("saveWorkspaceNow (before an update restart)", () => {
  const tabs = () => {
    useTabs.setState({
      tabs: { a: { id: "a", title: "a.sql" } as any, d: { id: "d", title: "🐞 debug" } as any, b: { id: "b", title: "b.sql", profileId: "pg1" } as any },
      order: ["a", "d", "b"], activeId: "b",
    });
    h.models.set("a", mkModel("select 1"));
    h.models.set("d", mkModel("debug"));
    h.models.set("b", mkModel("select 2"));
  };

  it("writes every tab's text and the workspace, even with autosave off, so the restart restores them", async () => {
    useSettings.setState({ autosave: false } as any);
    tabs();
    await saveWorkspaceNow();
    expect(localStorage.getItem("querithm.script.a")).toBe("select 1");
    expect(localStorage.getItem("querithm.script.b")).toBe("select 2");
    expect(localStorage.getItem("querithm.script.d")).toBeNull();
    const ws = JSON.parse(localStorage.getItem("querithm.workspace")!);
    expect(ws).toMatchObject({ activeId: "b", tabs: [{ id: "a" }, { id: "b", profileId: "pg1" }] });
    useTabs.setState({ tabs: {}, order: [], activeId: undefined });
    expect(await restoreWorkspace()).toBe(true);
    expect(newQueryTab).toHaveBeenCalledWith(expect.objectContaining({ id: "b", text: "select 2", activate: true }));
  });

  it("writes through the app-data files in the app", async () => {
    h.tauri = true;
    tabs();
    await saveWorkspaceNow();
    expect(h.files.get("querithm/scripts/a.sql")).toBe("select 1");
    expect(JSON.parse(h.files.get("querithm/scripts/workspace.json")!).tabs).toHaveLength(2);
  });

  it("a failed write rejects, so the update is not installed", async () => {
    h.tauri = true;
    tabs();
    const fs = await import("@tauri-apps/plugin-fs");
    vi.mocked(fs.writeTextFile).mockRejectedValueOnce(new Error("disk full"));
    await expect(saveWorkspaceNow()).rejects.toThrow("disk full");
  });
});

describe("notebooks (Phase C3)", () => {
  const notebookTab = (over: Record<string, unknown> = {}) => {
    useNotebooks.getState().init("n1", { cells: [{ id: "c1", kind: "sql", source: "select 1", status: "idle" }] });
    useTabs.getState().add({ id: "n1", title: "report.sqlbook", notebook: true, profileId: "p1", database: "Sales", ...over } as never);
  };
  beforeEach(() => {
    useNotebooks.setState({ docs: {} });
    vi.clearAllMocks();
  });

  it("the workspace lists a notebook with its unsaved state; its recovery copy is <id>.sqlbook", async () => {
    notebookTab({ dirty: true, filePath: "/w/report.sqlbook" });
    await saveWorkspaceNow();
    expect(localStorage.getItem("querithm.notebook.n1")).toBe('{"nb":"n1"}');
    const ws = JSON.parse(localStorage.getItem("querithm.workspace")!);
    expect(ws.tabs).toEqual([expect.objectContaining({ id: "n1", notebook: true, dirty: true, filePath: "/w/report.sqlbook", profileId: "p1" })]);
  });

  it("restores a notebook from its recovery copy, keeping its connection when the profile still exists", async () => {
    useProfiles.setState({ byId: (id?: string) => (id === "p1" ? ({ id } as any) : undefined) } as any);
    localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, activeId: "n1", tabs: [{ id: "n1", title: "report.sqlbook", notebook: true, profileId: "p1", database: "Sales", dirty: true }] }));
    localStorage.setItem("querithm.notebook.n1", "DOC");
    expect(await restoreWorkspace()).toBe(true);
    expect(nb.openNotebookText).toHaveBeenCalledWith("DOC", expect.objectContaining({ id: "n1", profileId: "p1", database: "Sales", dirty: true, activate: true, atEnd: true }));
    expect(newQueryTab).not.toHaveBeenCalled();
  });

  it("a missing or unreadable copy opens an empty notebook that says so; a deleted profile opens it disconnected", async () => {
    useProfiles.setState({ byId: () => undefined } as any);
    localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, tabs: [{ id: "n1", title: "a.sqlbook", notebook: true, profileId: "gone" }, { id: "n2", title: "b.sqlbook", notebook: true }] }));
    localStorage.setItem("querithm.notebook.n2", "not json");
    nb.openNotebookText.mockReturnValueOnce(null);
    await restoreWorkspace();
    expect(nb.newNotebookTab).toHaveBeenCalledWith(expect.objectContaining({ id: "n1", profileId: "", notices: [expect.stringMatching(/not found/)] }));
    expect(nb.newNotebookTab).toHaveBeenCalledWith(expect.objectContaining({ id: "n2", notices: [expect.stringMatching(/could not be read/)] }));
  });

  it("typing in a cell marks the notebook modified and writes its copy 2 s later; cell changes do too", () => {
    vi.useFakeTimers();
    try {
      notebookTab();
      const off = installAutosave();
      h.contentListeners.forEach((l) => l("n1::c1", mkModel("select 2")));
      expect(nb.markDirty).toHaveBeenCalledWith("n1");
      vi.advanceTimersByTime(1900);
      expect(localStorage.getItem("querithm.notebook.n1")).toBeNull();
      vi.advanceTimersByTime(200);
      expect(localStorage.getItem("querithm.notebook.n1")).toBe('{"nb":"n1"}');
      localStorage.removeItem("querithm.notebook.n1");
      useNotebooks.getState().patch("n1", { saveOutputs: true });
      vi.advanceTimersByTime(2100);
      expect(localStorage.getItem("querithm.notebook.n1")).toBe('{"nb":"n1"}');
      off();
    } finally {
      vi.useRealTimers();
    }
  });

  it("closing a notebook removes its copy; Save and Save As route to the notebook", async () => {
    notebookTab();
    const off = installAutosave();
    localStorage.setItem("querithm.notebook.n1", "x");
    useTabs.getState().remove("n1");
    await tick();
    expect(localStorage.getItem("querithm.notebook.n1")).toBeNull();
    off();
    notebookTab();
    useTabs.getState().setActive("n1");
    await saveFile();
    expect(nb.saveNotebook).toHaveBeenCalledWith("n1");
    await saveAs("n1");
    expect(nb.saveNotebook).toHaveBeenCalledWith("n1", true);
  });

  it("Open accepts .sqlbook files and opens them as notebooks", async () => {
    h.tauri = true;
    h.open.mockResolvedValueOnce("/w/Report.SQLBOOK");
    await openFile();
    expect(nb.openNotebookPath).toHaveBeenCalledWith("/w/Report.SQLBOOK");
    expect(newQueryTab).not.toHaveBeenCalled();
  });
});

describe("M23: atomic writes, the backup, recovery, a second window", () => {
  const W = "querithm/scripts/workspace.json";
  const tmps = () => [...h.files.keys()].filter((k) => k.endsWith(".tmp"));

  it("a recovery copy is written to a temporary file of its own and renamed into place", async () => {
    h.tauri = true;
    const fs = await import("@tauri-apps/plugin-fs");
    h.models.set("a", mkModel("select 1"));
    useTabs.getState().add({ id: "a", title: "a.sql" });
    await saveWorkspaceNow();
    const write = vi.mocked(fs.writeTextFile).mock.calls.find((c) => String(c[0]).startsWith("querithm/scripts/a.sql."))!;
    expect(write[0]).toMatch(/^querithm\/scripts\/a\.sql\.\d{13}-\d+\.tmp$/);
    expect(vi.mocked(fs.rename)).toHaveBeenCalledWith(write[0], "querithm/scripts/a.sql", { oldPathBaseDir: 1, newPathBaseDir: 1 });
    expect(vi.mocked(fs.rename).mock.calls.map((c) => c[1])).toContain(W);
    expect(h.files.get("querithm/scripts/a.sql")).toBe("select 1");
    expect(tmps()).toEqual([]);
    // Two writes never share a temporary name.
    const names = vi.mocked(fs.writeTextFile).mock.calls.map((c) => String(c[0]));
    expect(new Set(names).size).toBe(names.length);
  });

  it("a write that fails leaves the old file, removes its temporary and warns at most once a minute", async () => {
    h.tauri = true;
    const fs = await import("@tauri-apps/plugin-fs");
    h.files.set("querithm/scripts/a.sql", "old text");
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-11-03T10:00:00Z"));
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    const rename = vi.mocked(fs.rename).getMockImplementation()!;
    vi.mocked(fs.rename).mockRejectedValue(new Error("failed to rename old path: /home/u/x.tmp to new path: /home/u/a.sql with error: No space left on device (os error 28)"));
    // Every toast shown, whether or not it has timed out since.
    const shown: string[] = [];
    const offToasts = useUi.subscribe((st, prev) => { for (const t of st.toasts) if (!prev.toasts.includes(t)) shown.push(t.text); });
    useTabs.getState().add({ id: "a", title: "a.sql" });
    const off = installAutosave();
    const m = mkModel("new text");
    h.models.set("a", m);
    try {
      // Each write fails after its rename was tried again RENAME_RETRIES times (W9).
      const writeAndRetries = 2100 + RENAME_RETRIES * RENAME_RETRY_MS;
      for (let i = 0; i < 3; i++) {
        h.contentListeners.forEach((l) => l("a", m));
        await vi.advanceTimersByTimeAsync(writeAndRetries);
      }
      expect(h.files.get("querithm/scripts/a.sql")).toBe("old text");
      expect(tmps()).toEqual([]);
      const warning = "Autosave failed (No space left on device (os error 28)). Your tabs stay open; Querithm keeps trying. Save important scripts with Save As.";
      expect(shown.filter((t) => t.startsWith("Autosave failed"))).toEqual([warning]);
      await vi.advanceTimersByTimeAsync(61_000);
      h.contentListeners.forEach((l) => l("a", m));
      await vi.advanceTimersByTimeAsync(writeAndRetries);
      expect(shown.filter((t) => t.startsWith("Autosave failed"))).toEqual([warning, warning]);
      expect(err).toHaveBeenCalled();
    } finally {
      vi.mocked(fs.rename).mockImplementation(rename);
      err.mockRestore();
      offToasts();
      off();
    }
  });

  it("a cut workspace.json is restored from its backup, and says so", async () => {
    h.tauri = true;
    h.files.set(W, '{"version":1,"tabs":[{"id":"a","tit');
    h.files.set(`${W}.bak`, JSON.stringify({ version: 1, tabs: [{ id: "a", title: "a.sql" }], activeId: "a" }));
    h.files.set("querithm/scripts/a.sql", "select 1");
    expect(await restoreWorkspace()).toBe(true);
    expect(newQueryTab.mock.calls[0][0]).toMatchObject({ id: "a", text: "select 1" });
    expect(useUi.getState().toasts.map((t) => t.text)).toContain("The saved tabs could not be read; the previous copy of them was restored.");
    // The cut file is never copied over the backup.
    expect(JSON.parse(h.files.get(`${W}.bak`)!).tabs[0].id).toBe("a");
  });

  it("a workspace that parses becomes the backup; with neither readable the user is told", async () => {
    h.tauri = true;
    const good = JSON.stringify({ version: 1, tabs: [{ id: "a", title: "a.sql" }] });
    h.files.set(W, good);
    await restoreWorkspace();
    await vi.waitFor(() => expect(h.files.get(`${W}.bak`)).toBe(good));
    h.files.set(W, "{");
    h.files.set(`${W}.bak`, "");
    useUi.setState({ toasts: [] });
    expect(await restoreWorkspace()).toBe(false);
    expect(useUi.getState().toasts.map((t) => t.text)).toContain("The saved tabs could not be read. Unsaved scripts are offered for recovery.");
  });

  it("a script no saved tab names is offered back; Recover opens it as a new tab and removes the old copy", async () => {
    h.tauri = true;
    h.files.set(W, JSON.stringify({ version: 1, tabs: [{ id: "a", title: "a.sql" }] }));
    h.files.set("querithm/scripts/a.sql", "select 1");
    h.files.set("querithm/scripts/torphan.sql", "select 'left behind'");
    h.files.set("querithm/scripts/tempty.sql", "  \n");
    h.files.set("querithm/scripts/notes.txt", "not a script");
    expect(await restoreWorkspace()).toBe(true);
    await vi.waitFor(() => expect(useUi.getState().banner).not.toBeNull());
    const banner = useUi.getState().banner!;
    expect(banner.text).toBe("1 unsaved script from an earlier session was not restored.");
    expect(banner.action!.label).toBe("Recover 1 unsaved script");
    // Nothing to recover in an empty one: it goes; the other stays until the user decides.
    expect(h.files.has("querithm/scripts/tempty.sql")).toBe(false);
    expect(h.files.has("querithm/scripts/torphan.sql")).toBe(true);
    newQueryTab.mockClear();
    banner.action!.run();
    await vi.waitFor(() => expect(h.files.has("querithm/scripts/torphan.sql")).toBe(false));
    expect(newQueryTab).toHaveBeenCalledWith({ text: "select 'left behind'", title: "Recovered.sql" });
    expect(newQueryTab.mock.calls[0][0].id).toBeUndefined();
    expect(useUi.getState().banner).toBeNull();
    expect(h.files.get("querithm/scripts/notes.txt")).toBe("not a script");
  });

  it("the recovery offer waits for a banner already showing (a store reset)", async () => {
    localStorage.setItem("querithm.workspace", JSON.stringify({ version: 1, tabs: [] }));
    localStorage.setItem("querithm.script.tlost", "select 2");
    useUi.getState().setBanner({ kind: "warn", text: "store reset" });
    await restoreWorkspace();
    await tick();
    await tick();
    expect(useUi.getState().banner!.text).toBe("store reset");
    useUi.getState().setBanner(null);
    expect(useUi.getState().banner!.text).toBe("1 unsaved script from an earlier session was not restored.");
  });

  it("a temporary file a crash left is removed after an hour; a recent one may be a write going on", async () => {
    h.tauri = true;
    const old = `querithm/scripts/a.sql.${Date.now() - 2 * 3_600_000}-1.tmp`;
    const recent = `querithm/scripts/a.sql.${Date.now() - 1000}-2.tmp`;
    h.files.set(old, "x");
    h.files.set(recent, "y");
    await restoreWorkspace();
    await vi.waitFor(() => expect(h.files.has(old)).toBe(false));
    expect(h.files.has(recent)).toBe(true);
  });

  it("a rename an antivirus briefly refuses is tried again before the write fails (W9)", async () => {
    h.tauri = true;
    const fs = await import("@tauri-apps/plugin-fs");
    const rename = vi.mocked(fs.rename).getMockImplementation()!;
    let refusals = RENAME_RETRIES;
    vi.mocked(fs.rename).mockImplementation(async (a: any, b: any, o: any) => {
      if (refusals-- > 0) throw new Error("failed to rename: The process cannot access the file because it is being used by another process. (os error 32)");
      return rename(a, b, o);
    });
    try {
      h.models.set("a", mkModel("select 1"));
      useTabs.getState().add({ id: "a", title: "a.sql" });
      await saveWorkspaceNow();
      expect(h.files.get("querithm/scripts/a.sql")).toBe("select 1");
      expect(tmps()).toEqual([]);
    } finally {
      vi.mocked(fs.rename).mockImplementation(rename);
    }
  });

  it("a second window's live scripts are never offered back or removed by the next first window (W5)", async () => {
    h.tauri = true;
    // Window B runs second (A owns the workspace): its copies are listed in its beat before they are written.
    h.appInfo.mockImplementation(async () => ({ workspaceOwner: false }));
    await restoreWorkspace();
    h.models.set("b", mkModel("select 'b'"));
    useTabs.getState().add({ id: "b", title: "b.sql" });
    const off = installAutosave();
    await saveWorkspaceNow();
    const beatName = [...h.files.keys()].find((k) => k.endsWith(".live"))!;
    expect(beatName).toMatch(/^querithm\/scripts\/[A-Za-z0-9_-]+\.live$/);
    expect(JSON.parse(h.files.get(beatName)!).ids).toEqual(["b"]);
    expect(h.files.get("querithm/scripts/b.sql")).toBe("select 'b'");
    // A quits; C starts and owns the workspace while B still runs.
    resetPersistence();
    h.appInfo.mockImplementation(async () => ({ workspaceOwner: true }));
    h.files.set("querithm/scripts/workspace.json", JSON.stringify({ version: 1, tabs: [] }));
    h.files.set("querithm/scripts/gone.sql", "select 'crashed'");
    await restoreWorkspace();
    await vi.waitFor(() => expect(useUi.getState().banner).not.toBeNull());
    // Only the real orphan is offered: before W5, B's live script was offered too, and Recover deleted it under B.
    expect(useUi.getState().banner!.text).toBe("1 unsaved script from an earlier session was not restored.");
    newQueryTab.mockClear();
    useUi.getState().banner!.action!.run();
    await vi.waitFor(() => expect(h.files.has("querithm/scripts/gone.sql")).toBe(false));
    expect(newQueryTab).toHaveBeenCalledTimes(1);
    expect(h.files.get("querithm/scripts/b.sql")).toBe("select 'b'");
    off();
  });

  it("a beat gone stale belongs to a window that is gone: it goes, and its scripts are offered; a second window removes its beat when it quits (W5)", async () => {
    h.tauri = true;
    h.files.set("querithm/scripts/workspace.json", JSON.stringify({ version: 1, tabs: [] }));
    h.files.set("querithm/scripts/wdead.live", JSON.stringify({ at: Date.now() - LIVE_STALE_MS - 1000, ids: ["x"] }));
    h.files.set("querithm/scripts/x.sql", "select 'x'");
    await restoreWorkspace();
    await vi.waitFor(() => expect(useUi.getState().banner?.text).toBe("1 unsaved script from an earlier session was not restored."));
    expect(h.files.has("querithm/scripts/wdead.live")).toBe(false);

    // A second window keeps its beat fresh while it runs and takes it away when it quits.
    vi.useFakeTimers();
    try {
      resetPersistence();
      h.appInfo.mockImplementation(async () => ({ workspaceOwner: false }));
      await restoreWorkspace();
      const off = installAutosave();
      await vi.advanceTimersByTimeAsync(0);
      const beatName = () => [...h.files.keys()].find((k) => k.endsWith(".live"));
      const first = JSON.parse(h.files.get(beatName()!)!).at;
      await vi.advanceTimersByTimeAsync(LIVE_BEAT_MS);
      expect(JSON.parse(h.files.get(beatName()!)!).at).toBeGreaterThan(first);
      await flushAutosave();
      expect(beatName()).toBeUndefined();
      off();
    } finally {
      vi.useRealTimers();
    }
  });

  it("a second window starts with a new tab and never writes the first one's workspace", async () => {
    h.tauri = true;
    h.appInfo.mockImplementation(async () => ({ workspaceOwner: false }));
    const saved = JSON.stringify({ version: 1, tabs: [{ id: "a", title: "a.sql" }] });
    h.files.set(W, saved);
    h.files.set("querithm/scripts/torphan.sql", "select 3");
    expect(await restoreWorkspace()).toBe(false);
    expect(newQueryTab).not.toHaveBeenCalled();
    expect(useUi.getState().toasts.map((t) => t.text)).toContain("Another Querithm window has your saved tabs open; this window starts with a new tab.");
    h.models.set("b", mkModel("select 4"));
    useTabs.getState().add({ id: "b", title: "b.sql" });
    await saveWorkspaceNow();
    expect(h.files.get(W)).toBe(saved);
    // Its own tabs still get recovery copies, which the first window can offer back later; nothing was offered here.
    expect(h.files.get("querithm/scripts/b.sql")).toBe("select 4");
    expect(useUi.getState().banner).toBeNull();
    expect(h.files.has("querithm/scripts/torphan.sql")).toBe(true);
  });
});

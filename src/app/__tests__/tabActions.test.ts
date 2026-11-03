import { beforeEach, describe, expect, it, vi } from "vitest";
import { useTabs, type TabState } from "../../store/tabs";
import { useUi } from "../../store/ui";

const { hooks, closeTab, activateTab, newQueryTab, texts } = vi.hoisted(() => ({
  hooks: {} as { stripOrder?: (id?: string) => string[]; allOrder?: () => string[]; moveTab?: (id: string, i: number) => void; focusedTab?: () => string | undefined },
  closeTab: vi.fn(async (_id: string, _o?: { batch?: string }) => true),
  activateTab: vi.fn(),
  newQueryTab: vi.fn((_o: any) => "new1"),
  texts: {} as Record<string, string>,
}));
vi.mock("../actions", () => ({ workspaceHooks: hooks, closeTab, activateTab, newQueryTab }));
vi.mock("../../editor/editors", () => ({ getModel: (id: string) => (id in texts ? { getValue: () => texts[id] } : undefined) }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(), onNotify: () => () => {} }));

const { closeTabs, gotoTab, nextTab, switchTab, pickFromSwitcher, togglePin, tabForNewConnection, reopenClosed, middleClickClose, moveTabBy } = await import("../tabActions");
const { useToolTabs } = await import("../../store/toolTabs");
const { useAnnounce, resetAnnouncer } = await import("../../store/announce");
const { useClosedTabs } = await import("../../store/closedTabs");
const { useProfiles } = await import("../../store/profiles");

/** A strip whose order the fake Dockview keeps, so moves are visible. */
let strip: string[] = [];
const listeners = new Map<string, ((e: KeyboardEvent) => void)[]>();
vi.stubGlobal("window", {
  addEventListener: (t: string, f: (e: KeyboardEvent) => void) => listeners.set(t, [...(listeners.get(t) ?? []), f]),
  removeEventListener: (t: string, f: (e: KeyboardEvent) => void) => listeners.set(t, (listeners.get(t) ?? []).filter((x) => x !== f)),
});
const fire = (type: string, e: Partial<KeyboardEvent>) => [...(listeners.get(type) ?? [])].forEach((f) => f({ preventDefault() {}, stopPropagation() {}, ...e } as KeyboardEvent));

function setTabs(list: Partial<TabState>[], activeId?: string, mru?: string[]) {
  const tabs = Object.fromEntries(list.map((t) => [t.id!, { title: t.id, txState: "none", busy: false, dirty: false, ...t } as TabState]));
  strip = list.map((t) => t.id!);
  useTabs.setState({ tabs, order: [...strip], activeId, mru: mru ?? [] });
}

beforeEach(() => {
  closeTab.mockClear();
  activateTab.mockClear();
  for (const k of Object.keys(texts)) delete texts[k];
  listeners.clear();
  useUi.setState({ dialogs: [], switcher: null });
  hooks.stripOrder = () => strip;
  hooks.allOrder = () => strip;
  hooks.moveTab = (id, i) => {
    strip = strip.filter((x) => x !== id);
    strip.splice(i, 0, id);
  };
});

describe("tab actions: position", () => {
  it("next/previous follow the on-screen order, not the store's", () => {
    setTabs([{ id: "a" }, { id: "b" }, { id: "c" }], "a");
    strip = ["c", "a", "b"];
    nextTab(1);
    expect(activateTab).toHaveBeenLastCalledWith("b");
    nextTab(-1);
    expect(activateTab).toHaveBeenLastCalledWith("c");
  });
  it("Alt+N and Alt+0", () => {
    setTabs([{ id: "a" }, { id: "b" }, { id: "c" }], "a");
    gotoTab(2);
    expect(activateTab).toHaveBeenLastCalledWith("b");
    gotoTab(0);
    expect(activateTab).toHaveBeenLastCalledWith("c");
    activateTab.mockClear();
    gotoTab(7);
    expect(activateTab).not.toHaveBeenCalled();
  });
});

describe("tab actions: tool tabs and moves (D17, D22)", () => {
  it("next/previous step from a tool tab as from a query tab", () => {
    setTabs([{ id: "a" }, { id: "b" }], undefined);
    strip = ["a", "erd", "b"];
    useToolTabs.setState({ tools: { erd: { id: "erd", kind: "erd", title: "ERD", params: {} } }, activeToolId: "erd" });
    nextTab(1);
    expect(activateTab).toHaveBeenLastCalledWith("b");
    nextTab(-1);
    expect(activateTab).toHaveBeenLastCalledWith("a");
    useToolTabs.setState({ tools: {}, activeToolId: undefined });
  });
  it("moves a tab one place; never across the pinned tabs; keeps keyboard focus on it", () => {
    setTabs([{ id: "p", pinned: true }, { id: "a" }, { id: "b" }], "a");
    hooks.focusedTab = () => "b";
    const focusTab = vi.fn();
    (hooks as any).focusTab = focusTab;
    moveTabBy("b", -1);
    expect(strip).toEqual(["p", "b", "a"]);
    expect(focusTab).toHaveBeenCalledWith("b");
    moveTabBy("b", -1); // would cross the pin
    expect(strip).toEqual(["p", "b", "a"]);
    moveTabBy("a", 1); // already last
    expect(strip).toEqual(["p", "b", "a"]);
    moveTabBy(undefined, 1);
    hooks.focusedTab = undefined;
  });
});

describe("tab actions: pins", () => {
  it("pinning moves the tab to the end of the pinned run; unpinning moves it after them", () => {
    setTabs([{ id: "p", pinned: true }, { id: "a" }, { id: "b" }], "a");
    togglePin("b");
    expect(strip).toEqual(["p", "b", "a"]);
    expect(useTabs.getState().tabs.b.pinned).toBe(true);
    togglePin("p");
    expect(strip).toEqual(["b", "p", "a"]);
  });
});

describe("tab actions: bulk close", () => {
  it("closes without asking when nothing unsaved would be lost, and keeps pinned tabs", async () => {
    setTabs([{ id: "pin", pinned: true }, { id: "a" }, { id: "b", filePath: "/b.sql" }], "a");
    Object.assign(texts, { pin: "x", a: "", b: "select 1" });
    await closeTabs("all", "a");
    expect(useUi.getState().dialogs).toEqual([]);
    expect(closeTab.mock.calls.map((c) => c[0])).toEqual(["a", "b"]);
  });
  it("asks once, naming tabs with unsaved SQL; cancel closes nothing", async () => {
    setTabs([{ id: "a" }, { id: "b" }, { id: "c" }], "a");
    Object.assign(texts, { a: "", b: "select 1", c: "select 2" });
    const done = closeTabs("others", "a");
    const dialog = useUi.getState().dialogs[0] as { kind: string; body: string; resolve: (ok: boolean) => void };
    expect(dialog.kind).toBe("confirm");
    expect(dialog.body).toContain("b, c");
    dialog.resolve(false);
    await done;
    expect(closeTab).not.toHaveBeenCalled();
  });
  it("tabs closed together share a batch, so they reopen as one entry (D28); a single close has none", async () => {
    setTabs([{ id: "a" }, { id: "b" }, { id: "c" }], "a");
    await closeTabs("others", "a");
    const batches = closeTab.mock.calls.map((c) => (c as any[])[1]?.batch);
    expect(batches).toHaveLength(2);
    expect(batches[0]).toBeTruthy();
    expect(batches[1]).toBe(batches[0]);
    closeTab.mockClear();
    setTabs([{ id: "a" }, { id: "b" }], "a");
    await closeTabs("others", "a");
    expect((closeTab.mock.calls[0] as any[])[1]?.batch).toBeUndefined();
  });

  it("keeps running and open-transaction tabs, closes the rest and reports both (D20)", async () => {
    resetAnnouncer();
    useUi.setState({ toasts: [] });
    setTabs([{ id: "a" }, { id: "run", busy: true }, { id: "tx", txState: "open", sessionId: "s1" }, { id: "b" }], "a");
    await closeTabs("all", "a");
    expect(closeTab.mock.calls.map((c) => c[0])).toEqual(["a", "b"]);
    expect(useAnnounce.getState().text).toBe("Closed 2; kept 2: 1 running, 1 open transaction.");
    expect(useUi.getState().toasts.map((t) => t.text)).toEqual(["Closed 2; kept 2: 1 running, 1 open transaction."]);
  });
  it("reports when every tab in scope is kept, and announces a plain close without a toast", async () => {
    resetAnnouncer();
    useUi.setState({ toasts: [] });
    setTabs([{ id: "a" }, { id: "run", busy: true }], "a");
    await closeTabs("right", "a");
    expect(closeTab).not.toHaveBeenCalled();
    expect(useAnnounce.getState().text).toBe("Closed 0; kept 1: 1 running.");
    resetAnnouncer();
    useUi.setState({ toasts: [] });
    setTabs([{ id: "a" }, { id: "b" }], "a");
    await closeTabs("others", "a");
    expect(useAnnounce.getState().text).toBe("Closed 1 tab.");
    expect(useUi.getState().toasts).toEqual([]);
  });
  it("confirming closes them in order and stops at a tab kept open (open transaction)", async () => {
    setTabs([{ id: "a" }, { id: "b" }, { id: "c" }], "a");
    Object.assign(texts, { b: "select 1" });
    closeTab.mockImplementation(async (id: string) => id !== "b");
    const done = closeTabs("right", "a");
    (useUi.getState().dialogs[0] as { resolve: (ok: boolean) => void }).resolve(true);
    await done;
    expect(closeTab.mock.calls.map((c) => c[0])).toEqual(["b"]);
    closeTab.mockImplementation(async () => true);
  });
});

describe("tab actions: middle-click", () => {
  it("closes an idle tab like Ctrl+W", () => {
    setTabs([{ id: "a" }], "a");
    middleClickClose("a");
    expect(closeTab).toHaveBeenCalledWith("a");
  });
  it.each([
    [{ pinned: true }, undefined],
    [{ busy: true }, /is running a query; press Ctrl\+W/],
    [{ txState: "open" as const }, /has an open transaction; press Ctrl\+W/],
  ])("does nothing on a %o tab and says how to close it", (over, says) => {
    useUi.setState({ toasts: [] });
    setTabs([{ id: "a", ...over }], "a");
    middleClickClose("a");
    expect(closeTab).not.toHaveBeenCalled();
    const text = useUi.getState().toasts.map((t) => t.text).join();
    if (says) expect(text).toMatch(says);
    else expect(text).toBe("");
  });
});

describe("tab actions: Ctrl+Tab switcher", () => {
  it("a quick press and release flips to the previous tab", () => {
    setTabs([{ id: "a" }, { id: "b" }, { id: "c" }], "c", ["c", "a", "b"]);
    switchTab(1);
    expect(useUi.getState().switcher).toEqual({ ids: ["c", "a", "b"], index: 1 });
    fire("keyup", { key: "Control", ctrlKey: false });
    expect(activateTab).toHaveBeenLastCalledWith("a");
    expect(useUi.getState().switcher).toBeNull();
  });
  it("more presses move the highlight (wrapping); Shift goes back; releasing Shift alone does nothing", () => {
    setTabs([{ id: "a" }, { id: "b" }, { id: "c" }], "a", ["a", "b", "c"]);
    switchTab(1);
    switchTab(1);
    switchTab(1);
    expect(useUi.getState().switcher?.index).toBe(0);
    switchTab(-1);
    expect(useUi.getState().switcher?.index).toBe(2);
    fire("keyup", { key: "Shift", ctrlKey: true });
    expect(activateTab).not.toHaveBeenCalled();
    fire("keyup", { key: "Meta", ctrlKey: false, metaKey: false });
    expect(activateTab).toHaveBeenLastCalledWith("c");
  });
  it("Escape cancels; a click picks a row", () => {
    setTabs([{ id: "a" }, { id: "b" }], "a", ["a", "b"]);
    switchTab(1);
    fire("keydown", { key: "Escape" });
    expect(useUi.getState().switcher).toBeNull();
    expect(activateTab).not.toHaveBeenCalled();
    switchTab(1);
    pickFromSwitcher(0);
    expect(activateTab).toHaveBeenLastCalledWith("a");
    expect(listeners.get("keyup")).toEqual([]);
  });
  it("does nothing with a single tab", () => {
    setTabs([{ id: "a" }], "a", ["a"]);
    switchTab(1);
    expect(useUi.getState().switcher).toBeNull();
  });
});

describe("tab actions: connecting reuses a spare tab", () => {
  it("picks an empty unconnected tab over opening a new one", () => {
    setTabs([{ id: "a", profileId: "p" }, { id: "spare" }], "a");
    texts.spare = "";
    expect(tabForNewConnection()).toBe("spare");
  });
});

describe("tab actions: reopen a closed tab", () => {
  const ms: any = { id: "ms1", name: "Sales", engine: "mssql", environment: "dev", auth: "windows" };
  const pg: any = { id: "pg1", name: "Pg", engine: "pg", environment: "dev", auth: "trust" };
  const bound: any = { profileId: "pg1", database: "app", schema: "public", name: "f", kind: "func", definitionHash: "h" };

  beforeEach(() => {
    newQueryTab.mockClear();
    useClosedTabs.setState({ list: [] });
    useProfiles.setState({ profiles: [ms, pg], groups: [] });
    useUi.setState({ toasts: [] });
    setTabs([{ id: "new1" }], "new1");
  });

  it("a closed notebook reopens through the notebook runtime with its document and place", async () => {
    const { notebookHooks } = await import("../../notebook/hooks");
    notebookHooks.reopen = vi.fn(() => "nb9");
    (hooks as any).place = vi.fn();
    useClosedTabs.getState().push({ closedAt: 2, title: "r.sqlbook", profileId: "ms1", database: "Sales", sql: '{"format":"querithm.sqlbook"}', notebook: true, dirty: true, groupId: "g1", index: 2 });
    expect(reopenClosed()).toBe("nb9");
    expect(notebookHooks.reopen).toHaveBeenCalledWith({ text: '{"format":"querithm.sqlbook"}', title: "r.sqlbook", filePath: undefined, profileId: "ms1", database: "Sales", dirty: true, activate: true, atEnd: true });
    expect((hooks as any).place).toHaveBeenCalledWith("nb9", "g1", 2);
    expect(newQueryTab).not.toHaveBeenCalled();
  });

  it("does nothing when no tab was closed", () => {
    expect(reopenClosed()).toBeUndefined();
    expect(newQueryTab).not.toHaveBeenCalled();
  });

  it.each([
    ["ms1", "Sales", "SELECT TOP 1 * FROM [dbo].[t]", undefined],
    ["pg1", "app", 'SELECT * FROM "public"."t" LIMIT 1', bound],
  ])("reopens the newest tab on %s with its text, file, object and binding", (profileId, database, sql, boundObject) => {
    useClosedTabs.getState().push({ closedAt: 1, title: "old.sql", sql: "x" });
    useClosedTabs.getState().push({ closedAt: 2, title: "q.sql", profileId, database, sql, filePath: "/w/q.sql", boundObject, readOnly: true });
    expect(reopenClosed()).toBe("new1");
    expect(newQueryTab).toHaveBeenCalledWith({ profileId, database, text: sql, title: "q.sql", filePath: "/w/q.sql", boundObject, readOnly: true, activate: true, atEnd: false });
    expect(useClosedTabs.getState().list.map((s) => s.title)).toEqual(["old.sql"]);
    expect(useUi.getState().toasts).toEqual([]);
  });

  it("a bulk close comes back whole, in its old order and place, as one entry (D28)", () => {
    const place = vi.fn();
    (hooks as any).place = place;
    let n = 0;
    newQueryTab.mockImplementation(() => `r${++n}`);
    useClosedTabs.getState().push({ closedAt: 1, title: "solo.sql", sql: "s" });
    for (const [title, index] of [["c.sql", 3], ["a.sql", 1], ["b.sql", 2]] as const) {
      useClosedTabs.getState().push({ closedAt: 2, title, sql: title, batch: "b1", groupId: "g2", index });
    }
    resetAnnouncer();
    expect(reopenClosed()).toBe("r3");
    expect(newQueryTab.mock.calls.map((c) => [c[0].title, c[0].activate, c[0].atEnd])).toEqual([["a.sql", false, true], ["b.sql", false, true], ["c.sql", false, true]]);
    expect(place.mock.calls).toEqual([["r1", "g2", 1], ["r2", "g2", 2], ["r3", "g2", 3]]);
    expect(activateTab).toHaveBeenLastCalledWith("r3");
    expect(useAnnounce.getState().text).toBe("Reopened 3 tabs.");
    expect(useClosedTabs.getState().list.map((x) => x.title)).toEqual(["solo.sql"]);
    newQueryTab.mockImplementation(() => "new1");
    delete (hooks as any).place;
  });

  it("reopens a chosen entry and keeps it marked as changed since saved", () => {
    useClosedTabs.getState().push({ closedAt: 1, title: "a.sql", sql: "a", filePath: "/w/a.sql", dirty: true });
    useClosedTabs.getState().push({ closedAt: 2, title: "b.sql", sql: "b" });
    reopenClosed(1);
    expect(newQueryTab).toHaveBeenCalledWith(expect.objectContaining({ title: "a.sql", text: "a" }));
    expect(useTabs.getState().tabs.new1.dirty).toBe(true);
    expect(useClosedTabs.getState().list.map((s) => s.title)).toEqual(["b.sql"]);
  });

  it("an unconnected tab stays unconnected instead of taking the active tab's connection", () => {
    useClosedTabs.getState().push({ closedAt: 1, title: "scratch.sql", sql: "select 1" });
    reopenClosed();
    expect(newQueryTab).toHaveBeenCalledWith(expect.objectContaining({ profileId: "", database: undefined }));
  });

  it("a tab whose connection profile was deleted opens without a connection and says why", () => {
    useClosedTabs.getState().push({ closedAt: 1, title: "q.sql", profileId: "gone", database: "db", sql: "select 1", boundObject: { ...bound, profileId: "gone" } });
    reopenClosed();
    expect(newQueryTab).toHaveBeenCalledWith(expect.objectContaining({ profileId: "", database: undefined, boundObject: undefined, text: "select 1" }));
    expect(useUi.getState().toasts.map((t) => t.text).join()).toMatch(/no longer exists/);
  });
});

import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

const { rpc, onNotify, handlers } = vi.hoisted(() => {
  const handlers = new Map<string, (p: unknown) => void>();
  return {
    handlers,
    rpc: vi.fn(),
    onNotify: vi.fn((m: string, h: (p: unknown) => void) => {
      handlers.set(m, h);
      return () => handlers.delete(m);
    }),
  };
});
vi.mock("../../ipc/client", () => ({ rpc, onNotify, shell: {}, RpcError: class extends Error {} }));

import { useDiag, installDiagListeners } from "../diag";
import { useSettings, useSettingsStatus, defaultSettings, flushSettings, retrySettingsSave, schemaGroupKey, settingLimits } from "../settings";
import { useToolTabs, isToolId, newToolId } from "../toolTabs";
import { useUsages } from "../usages";
import { useUi, ask, toast, toastDuration, TOAST_MAX_MS, TOAST_MS } from "../ui";

describe("diag store", () => {
  beforeEach(() => {
    useDiag.setState({ traces: {}, debugs: {} });
    handlers.clear();
  });
  const dbg = (over = {}) => ({ debugId: "d1", tabId: "t", functionOid: 1, state: "attaching" as const, stack: [], variables: [], log: [], breakpoints: [], ...over });

  it("accumulates trace events, keeps the last 5000 and sticky stopped", () => {
    const off = installDiagListeners();
    const t = handlers.get("trace.event")!;
    t({ traceId: "x", sessionId: "s", events: [{ n: 1 }], stopped: false });
    t({ traceId: "x", sessionId: "s", events: [{ n: 2 }], stopped: true, error: "boom" });
    t({ traceId: "x", sessionId: "s", events: [], stopped: false });
    const tr = useDiag.getState().traces.x;
    expect(tr.events).toHaveLength(2);
    expect(tr.stopped).toBe(true);
    t({ traceId: "x", sessionId: "s", events: Array.from({ length: 6000 }, (_, i) => ({ i })), stopped: false });
    expect(useDiag.getState().traces.x.events).toHaveLength(5000);
    useDiag.getState().clearTrace("x");
    expect(useDiag.getState().traces.x.events).toEqual([]);
    useDiag.getState().clearTrace("missing");
    expect(useDiag.getState().traces.missing).toBeUndefined();
    off();
    expect(handlers.size).toBe(0);
  });

  it("applies debug events by kind", () => {
    installDiagListeners();
    const e = handlers.get("debug.event")!;
    useDiag.getState().putDebug(dbg());
    const st = () => useDiag.getState().debugs.d1;
    e({ debugId: "d1", kind: "paused", line: 7, functionOid: 9, stack: [{ f: 1 }], variables: [{ v: 1 }] });
    expect(st()).toMatchObject({ state: "paused", line: 7, currentOid: 9 });
    expect(st().stack).toHaveLength(1);
    e({ debugId: "d1", kind: "paused", line: 8 });
    expect(st().stack).toEqual([]);
    e({ debugId: "d1", kind: "running" });
    expect(st().state).toBe("running");
    e({ debugId: "d1", kind: "attached" });
    expect(st().state).toBe("paused");
    e({ debugId: "d1", kind: "error", message: "bad" });
    expect(st()).toMatchObject({ state: "error", line: null });
    expect(st().log).toEqual(["Error: bad"]);
    e({ debugId: "d1", kind: "finished", message: "done" });
    expect(st().state).toBe("finished");
    expect(st().log).toEqual(["Error: bad", "done"]);
    e({ debugId: "d1", kind: "finished" });
    expect(st().log).toHaveLength(2);
  });

  it("replays events that arrive before the debug session is registered (capped at 50)", () => {
    installDiagListeners();
    const e = handlers.get("debug.event")!;
    for (let i = 0; i < 60; i++) e({ debugId: "late", kind: "paused", line: i });
    useDiag.getState().putDebug(dbg({ debugId: "late" }));
    expect(useDiag.getState().debugs.late.line).toBe(59);
    // stash is consumed
    useDiag.getState().putDebug(dbg({ debugId: "late", line: 1 }));
    expect(useDiag.getState().debugs.late.line).toBe(1);
  });

  it("patchDebug ignores unknown ids", () => {
    useDiag.getState().patchDebug("nope", { line: 1 });
    expect(useDiag.getState().debugs.nope).toBeUndefined();
    useDiag.getState().putDebug(dbg());
    useDiag.getState().patchDebug("d1", { breakpoints: [3] });
    expect(useDiag.getState().debugs.d1.breakpoints).toEqual([3]);
  });
});

describe("settings store", () => {
  beforeEach(() => {
    rpc.mockReset();
    rpc.mockResolvedValue(undefined);
    useSettings.setState({ ...defaultSettings, loaded: false });
    useSettingsStatus.setState({ save: "saved", error: undefined, loadFailed: false });
    useUi.setState({ toasts: [] });
    vi.useFakeTimers();
  });
  afterEach(() => vi.useRealTimers());

  it("schemaGroupKey joins profile and database", () => {
    expect(schemaGroupKey("p", "Sales")).toBe("p|Sales");
  });

  it("load merges saved values over defaults", async () => {
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ theme: "light", editorFontSize: 20 }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState()).toMatchObject({ theme: "light", editorFontSize: 20, minimap: true, loaded: true });
  });

  it("load without saved entry or on failure still marks loaded", async () => {
    rpc.mockResolvedValueOnce([]);
    await useSettings.getState().load();
    expect(useSettings.getState().loaded).toBe(true);
    useSettings.setState({ loaded: false });
    rpc.mockRejectedValueOnce(new Error("down"));
    await useSettings.getState().load();
    expect(useSettings.getState().loaded).toBe(true);
    expect(useSettings.getState().theme).toBe("dark");
  });

  it("set debounces persistence and saves only plain values", async () => {
    useSettings.getState().set({ minimap: false });
    useSettings.getState().set({ indentSize: 2 });
    expect(rpc).not.toHaveBeenCalled();
    vi.advanceTimersByTime(400);
    expect(rpc).toHaveBeenCalledTimes(1);
    const [method, arg] = rpc.mock.calls[0];
    expect(method).toBe("settings.set");
    const saved = JSON.parse(arg.valueJson);
    expect(saved).toMatchObject({ minimap: false, indentSize: 2 });
    expect(saved).not.toHaveProperty("loaded");
    expect(saved).not.toHaveProperty("load");
  });

  it("a failed save keeps the value, reports the failure once, and a retry saves it", async () => {
    rpc.mockRejectedValue(new Error("engine down"));
    useSettings.getState().set({ wordWrap: true });
    await vi.advanceTimersByTimeAsync(400);
    expect(useSettings.getState().wordWrap).toBe(true);
    expect(useSettingsStatus.getState()).toMatchObject({ save: "failed", error: "engine down" });
    expect(useUi.getState().toasts.filter((t) => t.text.includes("Settings could not be saved"))).toHaveLength(1);
    useSettings.getState().set({ minimap: false });
    await vi.advanceTimersByTimeAsync(400);
    expect(useUi.getState().toasts.filter((t) => t.text.includes("Settings could not be saved"))).toHaveLength(1);
    rpc.mockReset();
    rpc.mockResolvedValue(undefined);
    await retrySettingsSave();
    expect(rpc).toHaveBeenCalledWith("settings.set", expect.anything());
    expect(JSON.parse(rpc.mock.calls[0][1].valueJson)).toMatchObject({ wordWrap: true, minimap: false });
    expect(useSettingsStatus.getState().save).toBe("saved");
  });

  it("when the saved settings could not be read, defaults are never written over them (read again first)", async () => {
    rpc.mockRejectedValueOnce(new Error("engine down"));
    await useSettings.getState().load();
    expect(useSettingsStatus.getState().loadFailed).toBe(true);
    useSettings.getState().set({ editorFontSize: 16 });
    rpc.mockReset();
    rpc.mockImplementation(async (method: string) =>
      method === "settings.get" ? [{ key: "ui.settings", valueJson: JSON.stringify({ theme: "light", editorFontSize: 20, indentSize: 2 }) }] : undefined,
    );
    await vi.advanceTimersByTimeAsync(400);
    const saved = JSON.parse(rpc.mock.calls.find((c) => c[0] === "settings.set")![1].valueJson);
    // What was on disk survives; the change made meanwhile wins over it.
    expect(saved).toMatchObject({ theme: "light", indentSize: 2, editorFontSize: 16 });
    expect(useSettings.getState()).toMatchObject({ theme: "light", indentSize: 2, editorFontSize: 16 });
    expect(useSettingsStatus.getState().loadFailed).toBe(false);
  });

  it("when the saved settings are still unreadable, nothing is written", async () => {
    rpc.mockRejectedValue(new Error("engine down"));
    await useSettings.getState().load();
    useSettings.getState().set({ editorFontSize: 16 });
    await vi.advanceTimersByTimeAsync(400);
    expect(rpc.mock.calls.some((c) => c[0] === "settings.set")).toBe(false);
    expect(useSettingsStatus.getState().save).toBe("failed");
  });

  it("load cleans untrusted values: wrong types and unknown choices take defaults, numbers are clamped", async () => {
    rpc.mockResolvedValueOnce([
      { key: "ui.settings", valueJson: JSON.stringify({ theme: "blue", editorFontSize: 0, indentSize: 99, maxRows: -5, minimap: "yes", pinnedProfiles: [1] }) },
    ]);
    await useSettings.getState().load();
    expect(useSettings.getState()).toMatchObject({ theme: "dark", editorFontSize: 9, indentSize: 8, maxRows: 0, minimap: true, pinnedProfiles: [] });
  });

  it("an unknown or removed syntax theme in store.db falls back to Standard; a known one loads", async () => {
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ syntaxTheme: "rainbow" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().syntaxTheme).toBe("standard");
    useSettings.getState().set({ syntaxTheme: 42 as never });
    expect(useSettings.getState().syntaxTheme).toBe("standard");
    // Classic was removed on 2026-11-01; a profile that saved it opens on Standard.
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ syntaxTheme: "classic" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().syntaxTheme).toBe("standard");
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ syntaxTheme: "forest" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().syntaxTheme).toBe("forest");
  });

  it("set clamps numbers and ignores invalid values", () => {
    useSettings.getState().set({ editorFontSize: 0, maxRows: -1, theme: "neon" as never });
    expect(useSettings.getState()).toMatchObject({ editorFontSize: 9, maxRows: 0, theme: "dark" });
    useSettings.getState().set({ maxRows: 1e12 });
    expect(useSettings.getState().maxRows).toBe(settingLimits.maxRows.max);
  });

  it("saved shortcuts that can't fire or would trap the keyboard are dropped; explicit unbinds stay", async () => {
    rpc.mockResolvedValueOnce([
      { key: "ui.settings", valueJson: JSON.stringify({ keybindings: { "query.run": "Tab", "query.format": "K, Ctrl+Alt+F", "tab.pin": " , ", x: 5 } }) },
    ]);
    await useSettings.getState().load();
    expect(useSettings.getState().keybindings).toEqual({ "query.format": "Ctrl+Alt+F", "tab.pin": "," });
  });

  it("keys from a newer version are kept and written back; store fields are never saved", async () => {
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ theme: "light", futureThing: { a: 1 } }) }]);
    await useSettings.getState().load();
    useSettings.getState().set({ minimap: false });
    await vi.advanceTimersByTimeAsync(400);
    const saved = JSON.parse(rpc.mock.calls.find((c) => c[0] === "settings.set")![1].valueJson);
    expect(saved).toMatchObject({ theme: "light", minimap: false, futureThing: { a: 1 } });
    expect(Object.keys(saved).filter((k) => !(k in defaultSettings) && k !== "futureThing")).toEqual([]);
  });

  it("unreadable JSON on disk loads defaults", async () => {
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: "{not json" }]);
    await useSettings.getState().load();
    expect(useSettings.getState()).toMatchObject({ theme: "dark", loaded: true });
    expect(useSettingsStatus.getState().loadFailed).toBe(false);
  });

  it("flushSettings writes a pending change at once (app closing, update restart)", async () => {
    useSettings.getState().set({ wordWrap: true });
    expect(rpc).not.toHaveBeenCalled();
    await flushSettings();
    expect(rpc).toHaveBeenCalledWith("settings.set", expect.anything());
    rpc.mockClear();
    await flushSettings();
    expect(rpc).not.toHaveBeenCalled();
  });
});

describe("toolTabs store", () => {
  beforeEach(() => useToolTabs.setState({ tools: {}, activeToolId: undefined }));
  const tab = (id: string) => ({ id, kind: "erd" as const, title: id, params: {} });

  it("adds, activates, updates and removes", () => {
    const s = () => useToolTabs.getState();
    s().add(tab("a"));
    s().add(tab("b"));
    expect(s().activeToolId).toBe("b");
    expect(isToolId("a")).toBe(true);
    expect(isToolId("zz")).toBe(false);
    expect(isToolId(undefined)).toBe(false);
    s().update("a", { title: "A2" });
    s().update("missing", { title: "x" });
    expect(s().tools.a.title).toBe("A2");
    expect(s().tools.missing).toBeUndefined();
    s().remove("a");
    expect(s().activeToolId).toBe("b");
    s().remove("b");
    expect(s().activeToolId).toBeUndefined();
    s().setActive("q");
    expect(s().activeToolId).toBe("q");
  });

  it("newToolId is unique", () => {
    expect(newToolId()).not.toBe(newToolId());
  });
});

describe("usages store", () => {
  it("sets and clears per tab", () => {
    useUsages.getState().set("t1", { running: true });
    useUsages.getState().set("t2", { running: false, error: "e" });
    expect(useUsages.getState().byTab.t1.running).toBe(true);
    useUsages.getState().clear("t1");
    expect(useUsages.getState().byTab.t1).toBeUndefined();
    expect(useUsages.getState().byTab.t2.error).toBe("e");
  });
});

describe("ui store", () => {
  beforeEach(() => {
    useUi.setState({ dialogs: [], toasts: [], palette: null, switcher: null, banner: null, explorerWidth: 290, explorerFlyout: false });
    vi.useFakeTimers();
  });
  afterEach(() => vi.useRealTimers());

  it("opens and closes dialogs (last, or a specific one)", () => {
    const a = { kind: "settings" } as const;
    const b = { kind: "history" } as const;
    useUi.getState().openDialog(a);
    useUi.getState().openDialog(b);
    useUi.getState().closeDialog(a);
    expect(useUi.getState().dialogs).toEqual([b]);
    useUi.getState().closeDialog();
    expect(useUi.getState().dialogs).toEqual([]);
  });

  it("ask resolves with the dialog's answer and closes it", async () => {
    const p = ask<boolean>((resolve) => ({ kind: "confirm", title: "t", body: "b", resolve }));
    const d = useUi.getState().dialogs[0];
    expect(d.kind).toBe("confirm");
    (d as { resolve: (v: boolean) => void }).resolve(true);
    await expect(p).resolves.toBe(true);
    expect(useUi.getState().dialogs).toEqual([]);
  });

  it("toasts expire (warnings and errors later, long ones later still, at most 30 s), and only five are kept", () => {
    toast("info", "i");
    toast("error", "e");
    toast("warn", "w");
    expect(useUi.getState().toasts.map((t) => t.text)).toEqual(["i", "e", "w"]);
    vi.advanceTimersByTime(TOAST_MS.info);
    expect(useUi.getState().toasts.map((t) => t.text)).toEqual(["e", "w"]);
    vi.advanceTimersByTime(TOAST_MS.warn - TOAST_MS.info);
    expect(useUi.getState().toasts.map((t) => t.text)).toEqual(["e"]);
    vi.advanceTimersByTime(TOAST_MS.error - TOAST_MS.warn);
    expect(useUi.getState().toasts).toEqual([]);
    expect([toastDuration("error", "x".repeat(80)), toastDuration("error", "x".repeat(180)), toastDuration("info", "x".repeat(5000))]).toEqual([15_000, 20_000, TOAST_MAX_MS]);
    for (let i = 0; i < 8; i++) toast("warn", `w${i}`);
    expect(useUi.getState().toasts).toHaveLength(5);
    expect(useUi.getState().toasts.at(-1)!.text).toBe("w7");
  });

  it("palette, switcher, banner and explorer state", () => {
    const u = useUi.getState();
    u.setPalette({ mode: "tabs" });
    u.setSwitcher({ ids: ["a"], index: 0 });
    u.setBanner({ kind: "warn", text: "x" });
    u.setExplorer({ explorerWidth: 400, explorerFlyout: true });
    expect(useUi.getState()).toMatchObject({ palette: { mode: "tabs" }, switcher: { index: 0 }, banner: { text: "x" }, explorerWidth: 400, explorerFlyout: true });
  });
});

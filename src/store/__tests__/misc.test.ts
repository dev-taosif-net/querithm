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
import { useSettings, useSettingsStatus, defaultSettings, flushSettings, parseProdGuardSetting, retrySettingsSave, schemaGroupKey, settingLimits, SETTINGS_RPC_LIMIT_MS, TAB_LAYOUTS, isSingleLineLayout } from "../settings";
import { fakeSettingsDb, patchesOf } from "./fakeSettingsDb";
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

  describe("production confirmations (sidecar-owned, guard.prodConfirmations)", () => {
    it("defaults to risky and is read from the sidecar's key, never from ui.settings", async () => {
      expect(defaultSettings.prodConfirmations).toBe("risky");
      rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ prodConfirmations: "off" }) }]);
      await useSettings.getState().load();
      expect(rpc).toHaveBeenCalledWith("settings.get", { keys: ["ui.settings", "guard.prodConfirmations"] });
      expect(useSettings.getState().prodConfirmations).toBe("risky");

      for (const [stored, expected] of [
        ['"strict"', "strict"],
        ['"risky"', "risky"],
        ['"off"', "off"],
        ["true", "strict"],
        ["false", "off"],
      ] as const) {
        useSettings.setState({ loaded: false });
        rpc.mockResolvedValueOnce([{ key: "guard.prodConfirmations", valueJson: stored }]);
        await useSettings.getState().load();
        expect(useSettings.getState().prodConfirmations).toBe(expected);
      }
    });

    it("unrecognised stored values fail closed to strict, matching the sidecar", async () => {
      rpc.mockResolvedValueOnce([{ key: "guard.prodConfirmations", valueJson: '"bogus"' }]);
      await useSettings.getState().load();
      expect(useSettings.getState().prodConfirmations).toBe("strict");
    });

    it("changing the mode writes the sidecar's key at once, and ui.settings never carries it", async () => {
      useSettings.getState().set({ prodConfirmations: "off" });
      expect(rpc).toHaveBeenCalledWith("settings.set", { key: "guard.prodConfirmations", valueJson: '"off"' });
      // Alone it has nothing for ui.settings: no read and write of an unchanged object.
      await vi.advanceTimersByTimeAsync(400);
      expect(rpc.mock.calls.map(([m, a]) => `${m} ${a?.key ?? a?.keys}`)).toEqual(["settings.set guard.prodConfirmations"]);
      expect(useSettingsStatus.getState().save).toBe("saved");
      useSettings.getState().set({ prodConfirmations: "strict", minimap: false });
      expect(rpc).toHaveBeenCalledWith("settings.set", { key: "guard.prodConfirmations", valueJson: '"strict"' });
      await vi.advanceTimersByTimeAsync(400);
      const [patch] = patchesOf(rpc.mock.calls);
      expect(patch.set).toEqual({ minimap: "false" });
      expect(patch.setWhenNew).not.toHaveProperty("prodConfirmations");
      // An old copy in ui.settings (written before the sidecar owned it) is dropped.
      expect(patch.remove).toEqual(["prodConfirmations"]);
    });

    it("when the sidecar does not take the change, the mode goes back to what it still enforces and says so", async () => {
      rpc.mockImplementation(async (m: string, a: { key: string }) => {
        if (m === "settings.set" && a.key === "guard.prodConfirmations") throw new Error("engine down");
      });
      useSettings.getState().set({ prodConfirmations: "off" });
      await vi.advanceTimersByTimeAsync(0);
      expect(useSettings.getState().prodConfirmations).toBe("risky");
      expect(useUi.getState().toasts.at(-1)?.text).toMatch(/production confirmation setting could not be saved: engine down\. It is still Risky only/);
    });

    it("serializes rapid mode switches, awaits them in flushSettings, rolls back to the last confirmed mode, and bounds stalled writes", async () => {
      const pending: Array<{ value: string; resolve: () => void; reject: (e: Error) => void }> = [];
      rpc.mockImplementation((m: string, a: { key: string; valueJson: string }) => {
        if (m !== "settings.set" || a.key !== "guard.prodConfirmations") return Promise.resolve(undefined);
        return new Promise<void>((resolve, reject) => pending.push({ value: a.valueJson, resolve, reject }));
      });

      // Rapid risky -> strict -> off sends only "strict" while in flight, then "off" after it resolves.
      useSettings.getState().set({ prodConfirmations: "strict" });
      useSettings.getState().set({ prodConfirmations: "off" });
      expect(pending.map((p) => p.value)).toEqual(['"strict"']);

      let flushed = false;
      const flush = flushSettings().then(() => { flushed = true; });
      await Promise.resolve();
      expect(flushed).toBe(false);

      pending[0].resolve();
      await vi.waitFor(() => expect(pending.map((p) => p.value)).toEqual(['"strict"', '"off"']));
      pending[1].resolve();
      await flush;
      expect(flushed).toBe(true);

      // Rapid off -> strict (fails) -> risky (fails) rolls back to last confirmed ("off"), never unpersisted "strict".
      pending.length = 0;
      useSettings.getState().set({ prodConfirmations: "strict" });
      useSettings.getState().set({ prodConfirmations: "risky" });
      pending[0].reject(new Error("down 1"));
      await vi.waitFor(() => expect(pending).toHaveLength(2));
      pending[1].reject(new Error("down 2"));
      await vi.advanceTimersByTimeAsync(0);
      expect(useSettings.getState().prodConfirmations).toBe("off");
      expect(useUi.getState().toasts.at(-1)?.text).toMatch(/down 2\. It is still Off/);

      // A stalled RPC is bounded by SETTINGS_RPC_LIMIT_MS and rolls back cleanly.
      pending.length = 0;
      useSettings.getState().set({ prodConfirmations: "strict" });
      await vi.advanceTimersByTimeAsync(SETTINGS_RPC_LIMIT_MS + 1);
      expect(useSettings.getState().prodConfirmations).toBe("off");
      expect(useUi.getState().toasts.at(-1)?.text).toMatch(/did not answer/);
    });

    it("parseProdGuardSetting tolerates null as default", () => {
      expect(parseProdGuardSetting(null)).toBe("risky");
      expect(parseProdGuardSetting(undefined)).toBe("risky");
    });

    it("loadFailed recovery does not clobber a prodConfirmations change started while settings.get is in flight", async () => {
      useSettingsStatus.setState({ loadFailed: true, save: "saved" });
      let resolveGet!: (v: unknown) => void;
      let resolveSet!: () => void;
      rpc.mockImplementation((m: string, a: { key?: string }) => {
        if (m === "settings.get") return new Promise((r) => (resolveGet = r));
        if (m === "settings.set" && a.key === "guard.prodConfirmations") return new Promise<void>((r) => (resolveSet = r));
        return Promise.resolve(undefined);
      });
      useSettings.getState().set({ wordWrap: true });
      const flushing = flushSettings();
      await vi.waitFor(() => expect(resolveGet).toBeDefined());
      // User switches production confirmations while settings.get is still in flight.
      useSettings.getState().set({ prodConfirmations: "strict" });
      resolveGet([{ key: "guard.prodConfirmations", valueJson: '"off"' }]);
      await vi.waitFor(() => expect(resolveSet).toBeDefined());
      resolveSet();
      await flushing;
      expect(useSettings.getState().prodConfirmations).toBe("strict");
    });
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
    await vi.advanceTimersByTimeAsync(400);
    // One debounced save: one patch of the changed keys, which the sidecar merges into what is stored under its write
    // lock (another window's keys are kept). Values travel JSON-encoded, as settings.set's do.
    expect(rpc.mock.calls.map((c) => c[0])).toEqual(["settings.patch"]);
    const [patch] = patchesOf(rpc.mock.calls);
    expect(patch.set).toEqual({ minimap: "false", indentSize: "2" });
    expect(patch.set).not.toHaveProperty("loaded");
    expect(patch.set).not.toHaveProperty("load");
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
    expect(patchesOf(rpc.mock.calls)[0].set).toMatchObject({ wordWrap: "true", minimap: "false" });
    expect(useSettingsStatus.getState().save).toBe("saved");
  });

  it("when the saved settings could not be read, defaults are never written over them (read again first)", async () => {
    rpc.mockRejectedValueOnce(new Error("engine down"));
    await useSettings.getState().load();
    expect(useSettingsStatus.getState().loadFailed).toBe(true);
    useSettings.getState().set({ editorFontSize: 16 });
    rpc.mockReset();
    const db = fakeSettingsDb({ theme: "light", editorFontSize: 20, indentSize: 2 });
    rpc.mockImplementation(db.handle);
    await vi.advanceTimersByTimeAsync(400);
    // What was on disk survives; the change made meanwhile wins over it, and only it is sent.
    expect(patchesOf(rpc.mock.calls)[0].set).toEqual({ editorFontSize: "16" });
    expect(db.read()).toMatchObject({ theme: "light", indentSize: 2, editorFontSize: 16 });
    expect(useSettings.getState()).toMatchObject({ theme: "light", indentSize: 2, editorFontSize: 16 });
    expect(useSettingsStatus.getState().loadFailed).toBe(false);
  });

  it("when the saved settings are still unreadable, nothing is written", async () => {
    rpc.mockRejectedValue(new Error("engine down"));
    await useSettings.getState().load();
    useSettings.getState().set({ editorFontSize: 16 });
    await vi.advanceTimersByTimeAsync(400);
    expect(rpc.mock.calls.some((c) => c[0] === "settings.set" || c[0] === "settings.patch")).toBe(false);
    expect(useSettingsStatus.getState().save).toBe("failed");
  });

  it("load cleans untrusted values: wrong types and unknown choices take defaults, numbers are clamped", async () => {
    rpc.mockResolvedValueOnce([
      { key: "ui.settings", valueJson: JSON.stringify({ theme: "blue", editorFontSize: 0, indentSize: 99, maxRows: -5, minimap: "yes", profileOrder: [1], groupOrder: [2] }) },
    ]);
    await useSettings.getState().load();
    expect(useSettings.getState()).toMatchObject({ theme: "dark", editorFontSize: 9, indentSize: 8, maxRows: 0, minimap: true, profileOrder: [], groupOrder: [] });
  });

  it("the layout for several result sets: Tabs by default; an unknown value in store.db or from set() is refused", async () => {
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ resultLayout: "grid-of-grids" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().resultLayout).toBe("tabs");
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ resultLayout: "stacked" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().resultLayout).toBe("stacked");
    useSettings.getState().set({ resultLayout: 7 as never });
    expect(useSettings.getState().resultLayout).toBe("stacked");
    useSettings.getState().set({ resultLayout: "side" });
    expect(useSettings.getState().resultLayout).toBe("side");
    useSettings.getState().set({ resultLayout: "tabs" });
  });

  it("the tab layout: Single line by default; an unknown value in store.db or from set() is refused", async () => {
    expect(defaultSettings.tabLayout).toBe("singleLine");
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ tabLayout: "threeLines" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().tabLayout).toBe("singleLine");
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ tabLayout: "numbered" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().tabLayout).toBe("numbered");
    useSettings.getState().set({ tabLayout: "<script>" as never });
    expect(useSettings.getState().tabLayout).toBe("numbered");
    for (const l of TAB_LAYOUTS) {
      useSettings.getState().set({ tabLayout: l });
      expect(useSettings.getState().tabLayout).toBe(l);
    }
    expect(TAB_LAYOUTS.filter(isSingleLineLayout)).toEqual(["singleLine", "numbered"]);
    useSettings.getState().set({ tabLayout: "singleLine" });
  });

  it("a saved syntax theme from before Syntax colors: unknown, removed or Standard become Default; another one is kept", async () => {
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ syntaxTheme: "rainbow" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().syntaxColors).toBe("default");
    useSettings.getState().set({ syntaxColors: 42 as never });
    expect(useSettings.getState().syntaxColors).toBe("default");
    // Classic was removed on 2026-11-01; a profile that saved it opens on Default (before: Standard).
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ syntaxTheme: "classic" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().syntaxColors).toBe("default");
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: JSON.stringify({ syntaxTheme: "forest" }) }]);
    await useSettings.getState().load();
    expect(useSettings.getState().syntaxColors).toBe("forest");
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

  it("keys from a newer version stay as stored; only settings keys are ever sent", async () => {
    const db = fakeSettingsDb({ theme: "light", syntaxColors: "default", futureThing: { a: 1 } });
    rpc.mockImplementation(db.handle);
    await useSettings.getState().load();
    useSettings.getState().set({ minimap: false, syntaxColors: "default" });
    await vi.advanceTimersByTimeAsync(400);
    expect(db.read()).toMatchObject({ theme: "light", minimap: false, futureThing: { a: 1 } });
    const [patch] = patchesOf(rpc.mock.calls);
    // syntaxTheme is the mirror older builds read (plans/app-themes §3.5): Default reads there as Standard.
    expect(JSON.parse(patch.set!.syntaxTheme)).toBe("standard");
    expect(db.read()!.syntaxTheme).toBe("standard");
    const sent = [...Object.keys(patch.set!), ...Object.keys(patch.setWhenNew!)];
    expect(sent.filter((k) => !(k in defaultSettings) && k !== "syntaxTheme")).toEqual([]);
  });

  it("unreadable JSON on disk loads defaults", async () => {
    rpc.mockResolvedValueOnce([{ key: "ui.settings", valueJson: "{not json" }]);
    await useSettings.getState().load();
    expect(useSettings.getState()).toMatchObject({ theme: "dark", loaded: true });
    expect(useSettingsStatus.getState().loadFailed).toBe(false);
  });

  it("writes never overlap: a change made during a slow write is saved after it, and only the change is sent", async () => {
    const db = fakeSettingsDb({});
    const gate: (() => void)[] = [];
    rpc.mockImplementation(async (m: string, a: unknown) => {
      if (m === "settings.patch") await new Promise<void>((r) => gate.push(r)); // the sidecar answers when the test says so
      return db.handle(m, a);
    });
    useSettings.getState().set({ minimap: false });
    await vi.advanceTimersByTimeAsync(400); // write 1 sent, not answered
    useSettings.getState().set({ indentSize: 2 });
    await vi.advanceTimersByTimeAsync(400); // write 2 is due, but waits for write 1
    expect(patchesOf(rpc.mock.calls)).toHaveLength(1);
    gate.shift()!();
    await vi.advanceTimersByTimeAsync(0);
    // Write 2 starts only now, with the key changed since write 1 (minimap is no longer pending).
    expect(patchesOf(rpc.mock.calls).map((p) => p.set)).toEqual([{ minimap: "false" }, { indentSize: "2" }]);
    gate.shift()!();
    await vi.advanceTimersByTimeAsync(0);
    expect(db.read()).toMatchObject({ minimap: false, indentSize: 2 });
    expect(useSettingsStatus.getState().save).toBe("saved");
  });

  it("a settings write the engine never answers fails the save at the time limit, and the next change saves both (CLAUDE §4)", async () => {
    const db = fakeSettingsDb({});
    let hang = true;
    rpc.mockImplementation((m: string, a: unknown) => (m === "settings.patch" && hang ? new Promise(() => {}) : db.handle(m, a)));
    useSettings.getState().set({ wordWrap: true });
    await vi.advanceTimersByTimeAsync(400);
    expect(useSettingsStatus.getState().save).toBe("saving");
    let flushed = false;
    void flushSettings().then(() => (flushed = true));
    await vi.advanceTimersByTimeAsync(SETTINGS_RPC_LIMIT_MS - 1);
    expect(flushed).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    // The update restart and the close wait on this: they go on, and the user is told the save failed.
    expect(flushed).toBe(true);
    expect(useSettingsStatus.getState()).toMatchObject({ save: "failed", error: `the engine did not answer the settings write within ${SETTINGS_RPC_LIMIT_MS / 1000} s` });
    hang = false;
    useSettings.getState().set({ minimap: false });
    await vi.advanceTimersByTimeAsync(400);
    expect(patchesOf(rpc.mock.calls).at(-1)!.set).toEqual({ wordWrap: "true", minimap: "false" });
    expect(db.read()).toMatchObject({ wordWrap: true, minimap: false });
    expect(useSettingsStatus.getState().save).toBe("saved");
  });

  it("a settings read the engine never answers ends the load at the time limit: defaults shown, nothing written over the store", async () => {
    rpc.mockImplementation(() => new Promise(() => {}));
    let done = false;
    void useSettings.getState().load().then(() => (done = true));
    await vi.advanceTimersByTimeAsync(SETTINGS_RPC_LIMIT_MS);
    expect(done).toBe(true);
    expect(useSettings.getState()).toMatchObject({ loaded: true, theme: "dark" });
    expect(useSettingsStatus.getState().loadFailed).toBe(true);
  });

  it("flushSettings waits for a write already in flight (closing never cuts it off)", async () => {
    let answer: (() => void) | undefined;
    rpc.mockImplementation(async (m: string) => {
      if (m === "settings.get") return [];
      if (m === "settings.patch") await new Promise<void>((r) => (answer = r));
    });
    useSettings.getState().set({ wordWrap: true });
    await vi.advanceTimersByTimeAsync(400);
    expect(answer).toBeDefined();
    let flushed = false;
    void flushSettings().then(() => (flushed = true));
    await vi.advanceTimersByTimeAsync(0);
    expect(flushed).toBe(false);
    answer!();
    await vi.advanceTimersByTimeAsync(0);
    expect(flushed).toBe(true);
  });

  it("flushSettings writes a pending change at once (app closing, update restart)", async () => {
    useSettings.getState().set({ wordWrap: true });
    expect(rpc).not.toHaveBeenCalled();
    await flushSettings();
    expect(rpc).toHaveBeenCalledWith("settings.patch", expect.objectContaining({ key: "ui.settings", set: { wordWrap: "true" } }));
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

import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

const panel = vi.hoisted(() => ({ stepOutput: vi.fn(), stepError: vi.fn(), compareNext: vi.fn() }));
import { commands, eventToShortcut, installKeybindings, keysFor } from "../commands";
import { useSettings } from "../../store/settings";
import { useUi } from "../../store/ui";

// isMac is read once at import, and Node's navigator.platform reports the host OS (e.g. "MacIntel" on macOS CI),
// so pin a non-mac platform before commands.ts loads. commands.mac.test.ts covers the macOS mapping.
vi.hoisted(() => vi.stubGlobal("navigator", { platform: "Linux x86_64" }));

const { actions } = vi.hoisted(() => ({
  actions: {
    activeTabState: vi.fn((): { id: string } | undefined => ({ id: "t1" })),
    runQuery: vi.fn(), cancelQuery: vi.fn(), applyProcedure: vi.fn(), closeTab: vi.fn(), duplicateTab: vi.fn(), formatTab: vi.fn(),
    newQueryTab: vi.fn(), showPlan: vi.fn(), toggleBottom: vi.fn(), toggleMaximize: vi.fn(), stepRun: vi.fn(), toggleTrace: vi.fn(), txAction: vi.fn(),
  },
}));
// commands.ts wires every app action; replace the ones that need Monaco, Tauri or the sidecar.
const { tabActions } = vi.hoisted(() => ({
  tabActions: { closeTabs: vi.fn(), gotoTab: vi.fn(), nextTab: vi.fn(), reopenClosed: vi.fn(), switchTab: vi.fn(), togglePin: vi.fn() },
}));
vi.mock("../actions", () => actions);
vi.mock("../persistence", () => ({ openFile: vi.fn(), saveAs: vi.fn(), saveFile: vi.fn() }));
vi.mock("../tabActions", () => tabActions);
vi.mock("../../debug/DebugPanel", () => ({ stepDebug: vi.fn(), stopDebug: vi.fn() }));
const { editorActions } = vi.hoisted(() => ({
  editorActions: { goToDefinition: vi.fn(), insertPrintForVariable: vi.fn(), expandWildcardAtCursor: vi.fn(), commentLines: vi.fn() },
}));
vi.mock("../../editor/editorActions", () => editorActions);
vi.mock("../../erd/erdActions", () => ({ openErdFromActiveTab: vi.fn() }));
vi.mock("../../compare/compareActions", () => ({ openSchemaCompare: vi.fn(), openDataCompare: vi.fn() }));
vi.mock("../../refactor/refactorActions", () => ({ findUsagesAtCursor: vi.fn(), renameAtCursor: vi.fn() }));
vi.mock("../../explorer/explorerWindow", () => ({ explorerAction: vi.fn() }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => undefined), onNotify: () => () => {} }));
const { nav } = vi.hoisted(() => ({ nav: { goBack: vi.fn(), goForward: vi.fn() } }));
vi.mock("../navigation", () => nav);
vi.mock("../../results/panelActions", () => panel);
const { regions } = vi.hoisted(() => ({ regions: { cycleFocus: vi.fn() } }));
vi.mock("../focusRegions", () => regions);

type KeyInit = Partial<Pick<KeyboardEvent, "key" | "code" | "ctrlKey" | "metaKey" | "altKey" | "shiftKey" | "isComposing">> & { target?: unknown };
const key = (o: KeyInit) => ({
  key: "", code: "", ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, isComposing: false, target: null,
  preventDefault: vi.fn(), stopPropagation: vi.fn(), ...o,
}) as unknown as KeyboardEvent & { preventDefault: ReturnType<typeof vi.fn> };
const cmd = (id: string) => commands.find((c) => c.id === id)!;

describe("commands: shortcuts from key events (non-mac)", () => {
  it("modifiers come first in a fixed order", () => {
    expect(eventToShortcut(key({ key: "Enter", code: "Enter", ctrlKey: true, shiftKey: true }))).toBe("Ctrl+Shift+Enter");
    expect(eventToShortcut(key({ key: "P", code: "KeyP", shiftKey: true, altKey: true, ctrlKey: true }))).toBe("Ctrl+Alt+Shift+P");
  });
  it("letters and digits use the physical key (Alt/Shift produce other characters)", () => {
    expect(eventToShortcut(key({ key: "a", code: "KeyA", ctrlKey: true }))).toBe("Ctrl+A");
    expect(eventToShortcut(key({ key: "ß", code: "KeyS", altKey: true }))).toBe("Alt+S");
    expect(eventToShortcut(key({ key: "!", code: "Digit1", shiftKey: true }))).toBe("Shift+1");
    expect(eventToShortcut(key({ key: "<", code: "Comma", ctrlKey: true, shiftKey: true }))).toBe("Ctrl+Shift+,");
  });
  it("space, Break/Cancel and function keys", () => {
    expect(eventToShortcut(key({ key: " ", code: "Space" }))).toBe("Space");
    expect(eventToShortcut(key({ key: "Cancel", code: "Pause", ctrlKey: true }))).toBe("Ctrl+Pause");
    expect(eventToShortcut(key({ key: "F5", code: "F5" }))).toBe("F5");
  });
  it("arrow keys drop the Arrow prefix; Minus is - with or without Shift", () => {
    expect(eventToShortcut(key({ key: "ArrowLeft", code: "ArrowLeft", altKey: true }))).toBe("Alt+Left");
    expect(eventToShortcut(key({ key: "ArrowRight", code: "ArrowRight", altKey: true }))).toBe("Alt+Right");
    expect(eventToShortcut(key({ key: "_", code: "Minus", ctrlKey: true, shiftKey: true }))).toBe("Ctrl+Shift+-");
  });
  it("Equal is = with or without Shift (Ctrl++ on a US keyboard is Ctrl+Shift+=)", () => {
    expect(eventToShortcut(key({ key: "=", code: "Equal", ctrlKey: true }))).toBe("Ctrl+=");
    expect(eventToShortcut(key({ key: "+", code: "Equal", ctrlKey: true, shiftKey: true }))).toBe("Ctrl+Shift+=");
    expect(eventToShortcut(key({ key: "-", code: "NumpadSubtract", ctrlKey: true }))).toBe("Ctrl+-");
  });
  it("metaKey is not Ctrl outside macOS", () =>expect(eventToShortcut(key({ key: "s", code: "KeyS", metaKey: true }))).toBe("S"));
});

describe("commands: default shortcuts", () => {
  it("no two commands share a default shortcut", () => {
    const seen = new Map<string, string>();
    for (const c of commands)
      for (const k of c.keys ?? []) {
        expect(seen.get(k.toLowerCase()), `${k} is bound to ${seen.get(k.toLowerCase())} and ${c.id}`).toBeUndefined();
        seen.set(k.toLowerCase(), c.id);
      }
  });
});

describe("commands: keysFor", () => {
  afterEach(() => useSettings.setState({ keybindings: {} }));

  it("defaults when there is no override", () => expect(keysFor(cmd("query.run"))).toEqual(["F5", "Ctrl+Enter"]));
  it("commands without a default shortcut", () => expect(keysFor(cmd("query.actualPlan"))).toEqual([]));
  it("an override replaces the defaults and is comma separated", () => {
    useSettings.setState({ keybindings: { "query.run": "Ctrl+Q ,  F9" } });
    expect(keysFor(cmd("query.run"))).toEqual(["Ctrl+Q", "F9"]);
  });
  it("an override of only separators unbinds the command", () => {
    useSettings.setState({ keybindings: { "query.run": " , " } });
    expect(keysFor(cmd("query.run"))).toEqual([]);
  });
  it("an override of \"Ctrl+,\" binds Ctrl+Comma", () => {
    useSettings.setState({ keybindings: { "view.settings": "Ctrl+," } });
    expect(keysFor(cmd("view.settings"))).toEqual(["Ctrl+,"]);
    useSettings.setState({ keybindings: { "view.settings": "Ctrl+, , F9" } });
    expect(keysFor(cmd("view.settings"))).toEqual(["Ctrl+,", "F9"]);
  });
});

describe("commands: keyboard dispatch", () => {
  let handler: (e: KeyboardEvent) => void;
  let uninstall: () => void;
  const win = { addEventListener: vi.fn(), removeEventListener: vi.fn() };

  beforeEach(() => {
    vi.stubGlobal("window", win);
    win.addEventListener.mockImplementation((_t: string, h: (e: KeyboardEvent) => void) => (handler = h));
    Object.values(actions).forEach((f) => f.mockClear());
    actions.activeTabState.mockReturnValue({ id: "t1" });
    useUi.setState({ dialogs: [], palette: null });
    useSettings.setState({ keybindings: {} });
    uninstall = installKeybindings();
  });
  afterEach(() => {
    uninstall();
    vi.unstubAllGlobals();
  });

  it("F6 / Shift+F6 move focus to the next / previous area, also from inside the editor (Tab indents there)", () => {
    const monaco = { tagName: "TEXTAREA", closest: (s: string) => (s === ".monaco-editor" ? {} : null) };
    const next = key({ key: "F6", code: "F6", target: monaco });
    handler(next);
    handler(key({ key: "F6", code: "F6", shiftKey: true }));
    expect(regions.cycleFocus.mock.calls).toEqual([[1], [-1]]);
    expect(next.preventDefault).toHaveBeenCalled();
  });

  it("results-panel keys act only while focus is in the panel: Alt+PageUp/PageDown step the ledger, Ctrl+Alt+PageUp/PageDown the runs", () => {
    const inPanel = { tagName: "BUTTON", closest: (s: string) => (s === '[data-focus-region="results"]' ? {} : null) };
    const inEditor = { tagName: "TEXTAREA", closest: (s: string) => (s === ".monaco-editor" ? {} : null) };
    const outside = key({ key: "PageDown", code: "PageDown", altKey: true, target: inEditor });
    handler(outside);
    expect(panel.stepOutput).not.toHaveBeenCalled();
    expect(outside.preventDefault).not.toHaveBeenCalled(); // the editor keeps the key
    handler(key({ key: "PageDown", code: "PageDown", altKey: true, target: inPanel }));
    handler(key({ key: "PageUp", code: "PageUp", altKey: true, target: inPanel }));
    expect(panel.stepOutput.mock.calls).toEqual([["t1", 1], ["t1", -1]]);
    handler(key({ key: "PageUp", code: "PageUp", altKey: true, ctrlKey: true, target: inPanel }));
    expect(actions.stepRun).toHaveBeenCalledWith("t1", -1);
  });

  it("F4 / Shift+F4 go to the next / previous error of the run; Ctrl+Shift+R maximizes the panel", () => {
    handler(key({ key: "F4", code: "F4" }));
    handler(key({ key: "F4", code: "F4", shiftKey: true }));
    expect(panel.stepError.mock.calls).toEqual([["t1", 1], ["t1", -1]]);
    handler(key({ key: "R", code: "KeyR", ctrlKey: true, shiftKey: true }));
    expect(actions.toggleMaximize).toHaveBeenCalledWith("t1");
  });

  it("Alt+Left / Alt+Right go back and forward, also from inside the editor", () => {
    const monaco = { tagName: "TEXTAREA", closest: (s: string) => (s === ".monaco-editor" ? {} : null) };
    const back = key({ key: "ArrowLeft", code: "ArrowLeft", altKey: true, target: monaco });
    handler(back);
    handler(key({ key: "ArrowRight", code: "ArrowRight", altKey: true }));
    expect(nav.goBack).toHaveBeenCalledTimes(1);
    expect(nav.goForward).toHaveBeenCalledTimes(1);
    // The webview would otherwise treat Alt+Left as page history.
    expect(back.preventDefault).toHaveBeenCalled();
  });
  it("tab navigation keys: Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+PageDown/PageUp, Alt+1…9, Alt+0, Ctrl+P", () => {
    Object.values(tabActions).forEach((f) => f.mockClear());
    handler(key({ key: "Tab", code: "Tab", ctrlKey: true }));
    handler(key({ key: "Tab", code: "Tab", ctrlKey: true, shiftKey: true }));
    expect(tabActions.switchTab.mock.calls).toEqual([[1], [-1]]);
    handler(key({ key: "PageDown", code: "PageDown", ctrlKey: true }));
    handler(key({ key: "PageUp", code: "PageUp", ctrlKey: true }));
    expect(tabActions.nextTab.mock.calls).toEqual([[1], [-1]]);
    handler(key({ key: "¡", code: "Digit1", altKey: true }));
    handler(key({ key: "9", code: "Digit9", altKey: true }));
    handler(key({ key: "0", code: "Digit0", altKey: true }));
    expect(tabActions.gotoTab.mock.calls).toEqual([[1], [9], [0]]);
    handler(key({ key: "p", code: "KeyP", ctrlKey: true }));
    expect(useUi.getState().palette).toEqual({ mode: "tabs" });
  });
  it("Ctrl+Shift+T reopens the last closed tab; the palette entry opens the tab list", () => {
    tabActions.reopenClosed.mockClear();
    const e = key({ key: "T", code: "KeyT", ctrlKey: true, shiftKey: true });
    handler(e);
    expect(tabActions.reopenClosed).toHaveBeenCalledTimes(1);
    expect(e.preventDefault).toHaveBeenCalled();
    useUi.setState({ palette: null });
    void cmd("tab.showClosed").run();
    expect(useUi.getState().palette).toEqual({ mode: "tabs" });
  });
  it("Ctrl+Tab still works while the switcher is open (it is not a dialog)", () => {
    tabActions.switchTab.mockClear();
    useUi.setState({ switcher: { ids: ["a", "b"], index: 1 } });
    handler(key({ key: "Tab", code: "Tab", ctrlKey: true }));
    expect(tabActions.switchTab).toHaveBeenCalledWith(1);
    useUi.setState({ switcher: null });
  });
  it("registers in the capture phase and unregisters the same handler", () => {
    expect(win.addEventListener).toHaveBeenCalledWith("keydown", handler, true);
    uninstall();
    expect(win.removeEventListener).toHaveBeenCalledWith("keydown", handler, true);
  });
  it("runs the matching command for the active tab and swallows the event", () => {
    const e = key({ key: "Enter", code: "Enter", ctrlKey: true });
    handler(e);
    expect(actions.runQuery).toHaveBeenCalledWith("t1");
    expect(e.preventDefault).toHaveBeenCalled();
  });
  it("tab commands do nothing without an active tab", () => {
    actions.activeTabState.mockReturnValue(undefined);
    handler(key({ key: "Enter", code: "Enter", ctrlKey: true }));
    expect(actions.runQuery).not.toHaveBeenCalled();
  });
  it("overrides are matched case-insensitively and replace the default", () => {
    useSettings.setState({ keybindings: { "query.run": "ctrl+shift+q" } });
    handler(key({ key: "Enter", code: "Enter", ctrlKey: true }));
    expect(actions.runQuery).not.toHaveBeenCalled();
    handler(key({ key: "Q", code: "KeyQ", ctrlKey: true, shiftKey: true }));
    expect(actions.runQuery).toHaveBeenCalledTimes(1);
  });
  it("ignores bare modifiers, IME composition, open dialogs and the palette", () => {
    const e = key({ key: "Control", code: "ControlLeft", ctrlKey: true });
    handler(e);
    handler(key({ key: "F5", code: "F5", isComposing: true }));
    useUi.setState({ dialogs: [{ kind: "connect" }] });
    handler(key({ key: "F5", code: "F5" }));
    useUi.setState({ dialogs: [], palette: { mode: "commands" } });
    handler(key({ key: "F5", code: "F5" }));
    expect(actions.runQuery).not.toHaveBeenCalled();
    expect(e.preventDefault).not.toHaveBeenCalled();
  });
  it("in a plain text field only Ctrl chords and function keys are app shortcuts", () => {
    const input = { tagName: "INPUT", closest: () => null };
    const alt = key({ key: "Pause", code: "Pause", altKey: true, target: input });
    handler(alt);
    expect(actions.cancelQuery).not.toHaveBeenCalled();
    expect(alt.preventDefault).not.toHaveBeenCalled();
    handler(key({ key: "F5", code: "F5", target: input }));
    handler(key({ key: "Enter", code: "Enter", ctrlKey: true, target: input }));
    expect(actions.runQuery).toHaveBeenCalledTimes(2);
  });
  it("an editable result grid keeps its editing keys (Ctrl+S applies staged changes there, not Save)", async () => {
    const persistence = await import("../persistence");
    const grid = { tagName: "CANVAS", closest: (sel: string) => (sel === "[data-q-grid-edit]" ? {} : null) };
    for (const k of [
      { key: "s", code: "KeyS", ctrlKey: true }, { key: "0", code: "Digit0", ctrlKey: true }, { key: "z", code: "KeyZ", ctrlKey: true },
      { key: "Delete", code: "Delete", ctrlKey: true }, { key: "Insert", code: "Insert", altKey: true },
    ]) {
      const e = key({ ...k, target: grid });
      handler(e);
      expect(e.preventDefault).not.toHaveBeenCalled();
    }
    expect(persistence.saveFile).not.toHaveBeenCalled();
    handler(key({ key: "F5", code: "F5", target: grid }));
    expect(actions.runQuery).toHaveBeenCalledWith("t1");
    handler(key({ key: "s", code: "KeyS", ctrlKey: true, target: { tagName: "DIV", closest: () => null } }));
    expect(persistence.saveFile).toHaveBeenCalledTimes(1);
  });
  it("in the grid's cell editor (Glide's #portal) Ctrl+S and Ctrl+0 never save the script or reset the zoom", async () => {
    const persistence = await import("../persistence");
    useSettings.setState({ uiScale: 110 });
    const editor = { tagName: "TEXTAREA", closest: (sel: string) => (sel === "#portal" ? {} : null) };
    const saves = vi.mocked(persistence.saveFile).mock.calls.length;
    let gridOpen = true;
    vi.stubGlobal("document", { querySelector: (sel: string) => (sel === "[data-q-grid-edit]" && gridOpen ? {} : null) });
    try {
      for (const k of [{ key: "s", code: "KeyS", ctrlKey: true }, { key: "0", code: "Digit0", ctrlKey: true }, { key: "d", code: "KeyD", ctrlKey: true }]) {
        const e = key({ ...k, target: editor });
        handler(e);
        expect(e.preventDefault).not.toHaveBeenCalled();
      }
      expect(vi.mocked(persistence.saveFile).mock.calls.length).toBe(saves);
      expect(useSettings.getState().uiScale).toBe(110);
      // With no editable grid open, #portal (other popups) keeps the app keys.
      gridOpen = false;
      handler(key({ key: "0", code: "Digit0", ctrlKey: true, target: editor }));
      expect(useSettings.getState().uiScale).toBe(100);
    } finally {
      vi.unstubAllGlobals();
      vi.stubGlobal("window", win);
    }
  });
  it("Ctrl+= / Ctrl+- / Ctrl+0 change the interface scale, also in the editor; Ctrl+0 in an editable grid stays the grid's NULL (L26)", () => {
    useSettings.setState({ uiScale: 100 });
    const monaco = { tagName: "TEXTAREA", closest: (s: string) => (s === ".monaco-editor" ? {} : null) };
    const plus = key({ key: "=", code: "Equal", ctrlKey: true, target: monaco });
    handler(plus);
    expect(plus.preventDefault).toHaveBeenCalled();
    handler(key({ key: "+", code: "Equal", ctrlKey: true, shiftKey: true }));
    expect(useSettings.getState().uiScale).toBe(120);
    handler(key({ key: "-", code: "Minus", ctrlKey: true }));
    expect(useSettings.getState().uiScale).toBe(110);
    const grid = { tagName: "CANVAS", closest: (sel: string) => (sel === "[data-q-grid-edit]" ? {} : null) };
    const nullKey = key({ key: "0", code: "Digit0", ctrlKey: true, target: grid });
    handler(nullKey);
    expect(nullKey.preventDefault).not.toHaveBeenCalled();
    expect(useSettings.getState().uiScale).toBe(110);
    handler(key({ key: "0", code: "Digit0", ctrlKey: true }));
    expect(useSettings.getState().uiScale).toBe(100);
  });
  it("the Object Explorer's tree keeps its own keys: Ctrl+Enter inserts a name there instead of running the query (B4)", () => {
    actions.runQuery.mockClear();
    const inside = (sel: string, tagName: string) => ({ tagName, closest: (q: string) => (q === sel ? {} : null) });
    for (const k of [
      { key: "Enter", code: "Enter", ctrlKey: true }, { key: "Enter", code: "Enter", ctrlKey: true, altKey: true }, { key: "F5", code: "F5" },
      { key: "F2", code: "F2" }, { key: "F12", code: "F12", shiftKey: true }, { key: "c", code: "KeyC", ctrlKey: true },
    ]) {
      const e = key({ ...k, target: inside("[data-q-explorer-tree]", "DIV") });
      handler(e);
      expect(e.preventDefault).not.toHaveBeenCalled();
    }
    // The search box owns only the insert keys.
    for (const k of [{ key: "Enter", code: "Enter", ctrlKey: true }, { key: "Enter", code: "Enter", ctrlKey: true, altKey: true }]) {
      const e = key({ ...k, target: inside("[data-q-explorer-search]", "INPUT") });
      handler(e);
      expect(e.preventDefault).not.toHaveBeenCalled();
    }
    expect(actions.runQuery).not.toHaveBeenCalled();
  });
  it("in a notebook, Shift+F5 and Shift+Enter are the notebook's (run all, run and move on); F5 still runs", () => {
    actions.runQuery.mockClear();
    const inNotebook = { tagName: "TEXTAREA", closest: (q: string) => (q === "[data-q-notebook]" || q === ".monaco-editor" ? {} : null) };
    for (const k of [{ key: "F5", code: "F5", shiftKey: true }, { key: "Enter", code: "Enter", shiftKey: true }]) {
      const e = key({ ...k, target: inNotebook });
      handler(e);
      expect(e.preventDefault).not.toHaveBeenCalled();
    }
    const f5 = key({ key: "F5", code: "F5", target: inNotebook });
    handler(f5);
    expect(f5.preventDefault).toHaveBeenCalled();
    expect(actions.runQuery).toHaveBeenCalledTimes(1);
  });
  it("F5 in the Explorer's search box or on its buttons still runs the query, never the browser's reload", () => {
    actions.runQuery.mockClear();
    for (const target of [
      { tagName: "INPUT", closest: (q: string) => (q === "[data-q-explorer-search]" || q === "[data-q-explorer]" ? {} : null) },
      { tagName: "BUTTON", closest: (q: string) => (q === "[data-q-explorer]" ? {} : null) },
    ]) {
      const e = key({ key: "F5", code: "F5", target });
      handler(e);
      expect(e.preventDefault).toHaveBeenCalled();
    }
    expect(actions.runQuery).toHaveBeenCalledTimes(2);
  });
  it("keys the Explorer does not own still reach the app from inside it (F6 moves focus on)", () => {
    handler(key({ key: "F6", code: "F6", target: { tagName: "DIV", closest: (q: string) => (q === "[data-q-explorer-tree]" ? {} : null) } }));
    expect(regions.cycleFocus).toHaveBeenCalled();
  });
  it("comment shortcuts: Ctrl+/ toggles a line comment and Shift+Alt+A a block comment, also from inside the editor", () => {
    editorActions.commentLines.mockClear();
    const monaco = { tagName: "TEXTAREA", closest: (s: string) => (s === ".monaco-editor" ? {} : null) };
    const e = key({ key: "/", code: "Slash", ctrlKey: true, target: monaco });
    handler(e);
    handler(key({ key: "A", code: "KeyA", shiftKey: true, altKey: true, target: monaco }));
    expect(editorActions.commentLines.mock.calls).toEqual([["t1", "toggle"], ["t1", "block"]]);
    expect(e.preventDefault).toHaveBeenCalled();
  });
  it("Ctrl+/ is matched by the physical key on layouts where it produces another character", () => {
    expect(eventToShortcut(key({ key: "-", code: "Slash", ctrlKey: true }))).toBe("Ctrl+/");
  });
  it("the second stroke of a Monaco Ctrl+K chord goes to Monaco (Ctrl+K Ctrl+U uncomments, not Change database)", () => {
    const monaco = { tagName: "TEXTAREA", closest: (s: string) => (s === ".monaco-editor" ? {} : null) };
    const ctrlU = () => key({ key: "u", code: "KeyU", ctrlKey: true, target: monaco });
    handler(key({ key: "k", code: "KeyK", ctrlKey: true, target: monaco }));
    const tail = ctrlU();
    handler(tail);
    expect(tail.preventDefault).not.toHaveBeenCalled();
    expect(useUi.getState().palette).toBeNull();
    // The chord is over: Ctrl+U alone is the app shortcut again.
    handler(ctrlU());
    expect(useUi.getState().palette).toEqual({ mode: "databases" });
  });
  it("a Ctrl+K outside the editor does not swallow the next shortcut", () => {
    handler(key({ key: "k", code: "KeyK", ctrlKey: true }));
    handler(key({ key: "u", code: "KeyU", ctrlKey: true }));
    expect(useUi.getState().palette).toEqual({ mode: "databases" });
  });
  it("chord shortcuts are never matched as their last stroke (Ctrl+K Ctrl+C is not Ctrl+C)", () => {
    editorActions.commentLines.mockClear();
    const copy = key({ key: "c", code: "KeyC", ctrlKey: true });
    handler(copy);
    expect(copy.preventDefault).not.toHaveBeenCalled();
    expect(editorActions.commentLines).not.toHaveBeenCalled();
  });
  it("the comment commands (palette) run on the active tab", () => {
    editorActions.commentLines.mockClear();
    void cmd("editor.comment").run();
    void cmd("editor.uncomment").run();
    expect(editorActions.commentLines.mock.calls).toEqual([["t1", "add"], ["t1", "remove"]]);
  });
  it("Monaco's hidden textarea is not a plain field", () => {
    const monaco = { tagName: "TEXTAREA", closest: (sel: string) => (sel === ".monaco-editor" ? {} : null) };
    handler(key({ key: "Pause", code: "Pause", altKey: true, target: monaco }));
    expect(actions.cancelQuery).toHaveBeenCalledWith("t1");
  });
});

describe("commands: catalogue", () => {
  it("ids are unique", () => expect(new Set(commands.map((c) => c.id)).size).toBe(commands.length));
  it("no two commands share a default shortcut", () => {
    const seen = new Map<string, string>();
    for (const c of commands) for (const k of c.keys ?? []) {
      expect(seen.get(k.toLowerCase()), `${k} on ${c.id}`).toBeUndefined();
      seen.set(k.toLowerCase(), c.id);
    }
  });
});

describe("commands: import data (Phase D0d)", () => {
  it("an in-memory SQLite tab imports through its own session; a server engine's tab names none", async () => {
    const { useProfiles } = await import("../../store/profiles");
    useProfiles.setState({ profiles: [{ id: "mem", engine: "sqlite", location: "memory" } as never, { id: "ms", engine: "mssql" } as never] });
    useUi.setState({ dialogs: [] });
    actions.activeTabState.mockReturnValue({ id: "t1", profileId: "mem", database: "main", sessionId: "s1" } as never);
    cmd("import.open").run();
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "import", profileId: "mem", database: "main", sessionId: "s1" });
    actions.activeTabState.mockReturnValue({ id: "t1", profileId: "ms", database: "Sales", sessionId: "s2" } as never);
    cmd("import.open").run();
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "import", profileId: "ms", database: "Sales" });
    actions.activeTabState.mockReturnValue({ id: "t1" });
  });
});

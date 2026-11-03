// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// One shared editor (as in the app): switching tabs swaps its model and restores that tab's last cursor.
const h = vi.hoisted(() => {
  type Pos = { lineNumber: number; column: number };
  const models: Record<string, { tabId: string; getLineCount: () => number }> = {};
  const lastPos: Record<string, Pos> = {};
  let cursorListener: (e: { position: Pos }) => void = () => {};
  const editor = {
    model: undefined as undefined | { tabId: string },
    getModel: () => editor.model ?? null,
    onDidChangeCursorPosition: (l: typeof cursorListener) => { cursorListener = l; },
    setPosition: vi.fn((p: Pos) => editor.move(p.lineNumber, p.column)),
    revealLineInCenterIfOutsideViewport: vi.fn(),
    focus: vi.fn(),
    /** A cursor move in the current model, as Monaco reports it. */
    move(lineNumber: number, column = 1) {
      lastPos[editor.model!.tabId] = { lineNumber, column };
      cursorListener({ position: { lineNumber, column } });
    },
    show(tabId: string) {
      editor.model = models[tabId];
      const p = lastPos[tabId] ?? { lineNumber: 1, column: 1 };
      cursorListener({ position: p }); // restoreViewState
    },
  };
  return { models, lastPos, editor, attachDelay: 0, activateTab: vi.fn() };
});

vi.mock("../actions", () => ({ activateTab: (id: string) => h.activateTab(id) }));
vi.mock("../../editor/editors", () => ({
  getModel: (id: string) => h.models[id],
  editorForTab: (id: string) => (h.editor.model?.tabId === id ? h.editor : undefined),
  tabIdOfModel: (m: { tabId: string } | null) => m?.tabId,
  onEditorCreated: (l: (e: unknown) => void) => { l(h.editor); return () => {}; },
}));

import { goBack, goForward, installNavigation, navStack, resetNav, useNav } from "../navigation";
import { useTabs } from "../../store/tabs";
import { useToolTabs } from "../../store/toolTabs";
import { useUi } from "../../store/ui";

const base = { txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.35, actualPlan: false };
const query = (id: string) => {
  h.models[id] = { tabId: id, getLineCount: () => 200 };
  useTabs.setState((s) => ({ tabs: { ...s.tabs, [id]: { ...base, id, title: id } as any }, order: [...s.order, id] }));
};
/** What Dockview + QueryTab do on a switch: the stores follow, then the editor shows the model (maybe a frame later). */
function show(id: string) {
  const tool = !!useToolTabs.getState().tools[id];
  useToolTabs.getState().setActive(tool ? id : undefined);
  useTabs.getState().setActive(tool ? undefined : id);
  if (tool) return;
  if (h.attachDelay) setTimeout(() => h.editor.show(id), h.attachDelay);
  else h.editor.show(id);
}

let uninstall: () => void;
beforeEach(() => {
  vi.useFakeTimers();
  for (const k in h.models) delete h.models[k];
  for (const k in h.lastPos) delete h.lastPos[k];
  h.editor.model = undefined;
  h.attachDelay = 0;
  h.activateTab.mockReset().mockImplementation(show);
  h.editor.setPosition.mockClear();
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useToolTabs.setState({ tools: {}, activeToolId: undefined });
  useUi.setState({ dialogs: [] });
  resetNav();
  uninstall = installNavigation();
});
afterEach(() => {
  uninstall();
  vi.useRealTimers();
});

describe("navigation: tabs", () => {
  it("goes back to the previous tab and its cursor, and forward again", () => {
    query("a");
    query("b");
    show("a");
    h.editor.move(8, 7);
    show("b");
    h.editor.move(3);
    expect(useNav.getState()).toEqual({ canBack: true, canForward: false });

    goBack();
    expect(h.activateTab).toHaveBeenLastCalledWith("a");
    expect(h.editor.setPosition).toHaveBeenLastCalledWith({ lineNumber: 8, column: 7 });
    expect(useNav.getState()).toEqual({ canBack: false, canForward: true });

    goForward();
    expect(useTabs.getState().activeId).toBe("b");
    expect(h.editor.setPosition).toHaveBeenLastCalledWith({ lineNumber: 3, column: 1 });
    // Moving between entries is not new history.
    expect(navStack().entries.map((e) => e.tabId)).toEqual(["a", "b"]);
  });

  it("waits for the editor to show the tab before placing the cursor", () => {
    query("a");
    query("b");
    show("a");
    h.editor.move(9);
    show("b");
    h.attachDelay = 30;
    goBack();
    expect(h.editor.setPosition).not.toHaveBeenCalled();
    vi.advanceTimersByTime(100);
    expect(h.editor.setPosition).toHaveBeenCalledWith({ lineNumber: 9, column: 1 });
    expect(navStack().entries).toHaveLength(2);
  });

  it("includes diagram and compare tabs", () => {
    query("a");
    useToolTabs.setState({ tools: { erd: { id: "erd", kind: "erd", title: "ERD", params: {} } } });
    show("a");
    show("erd");
    goBack();
    expect(useTabs.getState().activeId).toBe("a");
    goForward();
    expect(useToolTabs.getState().activeToolId).toBe("erd");
  });

  it("forgets closed tabs", () => {
    query("a");
    query("b");
    query("c");
    show("a");
    show("b");
    show("c");
    useTabs.getState().remove("b");
    expect(navStack().entries.map((e) => e.tabId)).toEqual(["a", "c"]);
    goBack();
    expect(useTabs.getState().activeId).toBe("a");
  });

  it("closing every other tab leaves nothing to go back to", () => {
    query("a");
    query("b");
    show("a");
    show("b");
    useTabs.getState().remove("a");
    expect(useNav.getState().canBack).toBe(false);
    goBack();
    expect(h.activateTab).not.toHaveBeenCalled();
  });
});

describe("navigation: jumps inside a tab", () => {
  it("a jump of more than 10 lines is an entry; going back returns before the jump", () => {
    query("a");
    show("a");
    h.editor.move(5);
    h.editor.move(6);
    h.editor.move(120, 4); // e.g. clicking an error in Messages, or a usage
    expect(navStack().entries).toHaveLength(2);
    goBack();
    expect(h.editor.setPosition).toHaveBeenLastCalledWith({ lineNumber: 6, column: 1 });
    goForward();
    expect(h.editor.setPosition).toHaveBeenLastCalledWith({ lineNumber: 120, column: 4 });
  });

  it("typing and arrowing line by line do not add entries", () => {
    query("a");
    show("a");
    for (let l = 1; l <= 60; l++) h.editor.move(l);
    expect(navStack().entries).toEqual([{ tabId: "a", line: 60, column: 1 }]);
  });

  it("a line that no longer exists goes to the last line", () => {
    query("a");
    query("b");
    show("a");
    h.editor.move(150);
    show("b");
    h.models.a.getLineCount = () => 20;
    goBack();
    expect(h.editor.setPosition).toHaveBeenLastCalledWith({ lineNumber: 20, column: 1 });
  });

  it("recording after going back drops forward history", () => {
    query("a");
    query("b");
    query("c");
    show("a");
    show("b");
    goBack();
    show("c");
    expect(navStack().entries.map((e) => e.tabId)).toEqual(["a", "c"]);
    expect(useNav.getState().canForward).toBe(false);
  });
});

describe("navigation: mouse buttons", () => {
  const press = (button: number) => {
    const down = new MouseEvent("mousedown", { button, cancelable: true });
    const up = new MouseEvent("mouseup", { button, cancelable: true });
    window.dispatchEvent(down);
    window.dispatchEvent(up);
    return [down, up];
  };

  it("back (3) and forward (4) navigate, and the webview does not act on them", () => {
    query("a");
    query("b");
    show("a");
    show("b");
    const events = press(3);
    expect(useTabs.getState().activeId).toBe("a");
    expect(events.every((e) => e.defaultPrevented)).toBe(true);
    press(4);
    expect(useTabs.getState().activeId).toBe("b");
  });

  it("other buttons and open dialogs are left alone", () => {
    query("a");
    query("b");
    show("a");
    show("b");
    const [down] = press(0);
    expect(down.defaultPrevented).toBe(false);
    useUi.setState({ dialogs: [{ kind: "connect" } as any] });
    press(3);
    expect(h.activateTab).not.toHaveBeenCalled();
  });

  it("the palette and the Ctrl+Tab switcher own the mouse buttons while open", () => {
    query("a");
    query("b");
    show("a");
    show("b");
    useUi.setState({ palette: { mode: "tabs" } });
    press(3);
    useUi.setState({ palette: null, switcher: { ids: ["a", "b"], index: 0 } });
    press(3);
    expect(h.activateTab).not.toHaveBeenCalled();
    useUi.setState({ switcher: null });
  });
});

describe("navigation: quick repeated steps", () => {
  it("a newer step drops the cursor placement an earlier one was still waiting to make", () => {
    query("a");
    query("b");
    query("c");
    show("a");
    show("b");
    h.editor.move(5);
    show("c");
    h.attachDelay = 30;
    goBack(); // to b@5, its editor attaches later
    goBack(); // to a before b's editor is up
    vi.advanceTimersByTime(45);
    expect(useTabs.getState().activeId).toBe("a");
    expect(h.editor.setPosition).toHaveBeenCalledTimes(1);
    // The user switches to b themselves: the abandoned step must not move their cursor there.
    h.attachDelay = 0;
    show("b");
    vi.advanceTimersByTime(1100);
    expect(h.editor.setPosition).toHaveBeenCalledTimes(1);
  });
});

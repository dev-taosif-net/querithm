// Go Back / Go Forward (Alt+Left / Alt+Right, mouse back/forward buttons): wires the pure history in
// navHistory.ts to the tab stores and the shared editors. Recording runs on every cursor move, so the
// stack lives outside React and only the two "can go" flags are store state (no re-render per keystroke).
import { create } from "zustand";
import { activateTab } from "./actions";
import { editorForTab, getModel, onEditorCreated, tabIdOfModel } from "../editor/editors";
import { useTabs } from "../store/tabs";
import { useToolTabs } from "../store/toolTabs";
import { useUi } from "../store/ui";
import { canGoBack, canGoForward, emptyNav, forgetNav, recordNav, type NavEntry, type NavStack } from "./navHistory";

export const useNav = create<{ canBack: boolean; canForward: boolean }>(() => ({ canBack: false, canForward: false }));

let stack: NavStack = emptyNav();
/** True while navigate() moves to an entry: the tab switch and cursor moves it causes are not new history. */
let navigating = false;
/** Bumped by every navigate(): a newer step supersedes the cursor placement of one still waiting for its editor. */
let generation = 0;

function commit(next: NavStack) {
  if (next === stack) return;
  stack = next;
  const canBack = canGoBack(next);
  const canForward = canGoForward(next);
  const s = useNav.getState();
  if (s.canBack !== canBack || s.canForward !== canForward) useNav.setState({ canBack, canForward });
}

export function recordLocation(e: NavEntry) {
  if (!navigating) commit(recordNav(stack, e));
}

export function forgetTab(tabId: string) {
  commit(forgetNav(stack, tabId));
}

export function navStack(): NavStack {
  return stack;
}

export function resetNav() {
  stack = emptyNav();
  navigating = false;
  generation++;
  useNav.setState({ canBack: false, canForward: false });
}

const isOpen = (id: string) => !!useTabs.getState().tabs[id] || !!useToolTabs.getState().tools[id];
const isActive = (id: string) => useTabs.getState().activeId === id || useToolTabs.getState().activeToolId === id;

/** Moves `dir` steps (-1 back, 1 forward): activates the entry's tab, then puts the cursor back. */
export function navigate(dir: -1 | 1) {
  const index = stack.index + dir;
  const target = stack.entries[index];
  if (!target) return;
  if (!isOpen(target.tabId)) {
    // A tab closed without the store noticing yet: drop it and try again.
    forgetTab(target.tabId);
    return navigate(dir);
  }
  commit({ entries: stack.entries, index });
  navigating = true;
  const gen = ++generation;
  activateTab(target.tabId);
  // The panel shows its editor after React renders it, so wait for the model to be attached.
  const settle = (left: number) => {
    if (gen !== generation) return;
    const ed = editorForTab(target.tabId);
    const model = getModel(target.tabId);
    const ready = isActive(target.tabId) && (!model || (ed && ed.getModel() === model));
    if (!ready && left > 0) return void setTimeout(() => settle(left - 1), 20);
    if (ready && ed && model && target.line !== undefined) {
      const lineNumber = Math.min(target.line, model.getLineCount());
      ed.setPosition({ lineNumber, column: target.column ?? 1 });
      ed.revealLineInCenterIfOutsideViewport(lineNumber);
      ed.focus();
    }
    navigating = false;
  };
  settle(50);
}

export const goBack = () => navigate(-1);
export const goForward = () => navigate(1);

/** Records tab switches and cursor jumps, forgets closed tabs, and maps the mouse back/forward buttons. */
export function installNavigation() {
  const offTabs = useTabs.subscribe((s, prev) => {
    if (s.tabs !== prev.tabs) for (const id in prev.tabs) if (!s.tabs[id]) forgetTab(id);
    if (s.activeId && s.activeId !== prev.activeId) recordLocation({ tabId: s.activeId });
  });
  const offTools = useToolTabs.subscribe((s, prev) => {
    if (s.tools !== prev.tools) for (const id in prev.tools) if (!s.tools[id]) forgetTab(id);
    if (s.activeToolId && s.activeToolId !== prev.activeToolId) recordLocation({ tabId: s.activeToolId });
  });
  const offEditors = onEditorCreated((editor) => {
    editor.onDidChangeCursorPosition((e) => {
      const tabId = tabIdOfModel(editor.getModel());
      // Only the active tab's editor is where the user is (a split group can show another tab).
      if (tabId && useTabs.getState().activeId === tabId) recordLocation({ tabId, line: e.position.lineNumber, column: e.position.column });
    });
  });
  // Mouse buttons 3/4 are "back"/"forward"; the webview must not treat them as page navigation.
  const onMouse = (e: MouseEvent) => {
    if (e.button !== 3 && e.button !== 4) return;
    e.preventDefault();
    // Like the keyboard shortcuts: dialogs, the palette and the Ctrl+Tab switcher own the input while open.
    const ui = useUi.getState();
    if (e.type !== "mouseup" || ui.dialogs.length || ui.palette || ui.switcher) return;
    navigate(e.button === 3 ? -1 : 1);
  };
  for (const t of ["mousedown", "mouseup", "auxclick"] as const) window.addEventListener(t, onMouse, true);
  return () => {
    offTabs();
    offTools();
    offEditors();
    for (const t of ["mousedown", "mouseup", "auxclick"] as const) window.removeEventListener(t, onMouse, true);
  };
}

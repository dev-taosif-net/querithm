// Every command with its default shortcut (§12 Keyboard model). Shortcuts are remappable via settings.keybindings.
// A capture-phase listener dispatches them, so they also work while Monaco has focus.
import { activeTabState, applyProcedure, cancelQuery, closeTab, duplicateTab, formatTab, newQueryTab, runQuery, showPlan, stepRun, toggleBottom, toggleMaximize, toggleTrace, txAction, workspaceHooks } from "./actions";
import { useTabs } from "../store/tabs";
import { sessionParam, tabScope } from "../store/snapshots";
import { useUi } from "../store/ui";
import { useSettings } from "../store/settings";
import { useDiag } from "../store/diag";
import { explorerAction } from "../explorer/explorerWindow";
import { stepDebug, stopDebug } from "../debug/DebugPanel";
import { commentLines, expandWildcardAtCursor, goToDefinition, insertPrintForVariable } from "../editor/editorActions";
import { openFile, saveAs, saveFile } from "./persistence";
import { activePanelId, closeTabs, gotoTab, moveTabBy, nextTab, reopenClosed, switchTab, togglePin } from "./tabActions";
import { useToolTabs } from "../store/toolTabs";
import { closeTool } from "./toolActions";
import { openErdFromActiveTab } from "../erd/erdActions";
import { openDataCompare, openSchemaCompare } from "../compare/compareActions";
import { openActivityMonitor } from "../monitor/monitorActions";
import { findUsagesAtCursor, renameAtCursor } from "../refactor/refactorActions";
import { goBack, goForward } from "./navigation";
import { cycleFocus } from "./focusRegions";
import { checkForUpdates } from "../store/updates";
import { canonical, eventToShortcut, isChord, isMac, splitBinding } from "./shortcuts";
import { showResultAs } from "../results/resultViews";
import { compareNext, stepError, stepOutput } from "../results/panelActions";
import { stepUiScale } from "./uiScale";

export interface Command {
  id: string;
  title: string;
  keys?: string[];
  category?: string;
  run: () => void | Promise<void>;
  enabled?: () => boolean;
  /** Only while focus is inside the results panel (its keys mean something else in the editor). */
  scope?: "results";
}

const withTab = (f: (id: string) => unknown) => () => {
  const t = activeTabState();
  if (t) void f(t.id);
};

function debugging() {
  const t = activeTabState();
  const d = t?.debugId ? useDiag.getState().debugs[t.debugId] : undefined;
  return d && d.state !== "finished" && d.state !== "error" ? t : undefined;
}

export const commands: Command[] = [
  { id: "query.run", title: "Execute selection or all", keys: ["F5", "Ctrl+Enter"], category: "Query", run: () => { const t = debugging(); if (t) void stepDebug(t.id, "continue"); else withTab((id) => runQuery(id))(); } },
  { id: "query.runStatement", title: "Execute current statement", keys: ["Ctrl+Shift+Enter"], category: "Query", run: withTab((id) => runQuery(id, { mode: "statement" })) },
  { id: "query.cancel", title: "Cancel execution", keys: ["Alt+Pause", "Ctrl+Shift+C"], category: "Query", run: withTab(cancelQuery) },
  { id: "query.estimatedPlan", title: "Show estimated plan", keys: ["Ctrl+L"], category: "Query", run: withTab((id) => showPlan(id, "estimated")) },
  {
    id: "query.toggleActualPlan", title: "Toggle include actual plan", keys: ["Ctrl+M"], category: "Query",
    run: withTab((id) => useTabs.getState().update(id, { actualPlan: !useTabs.getState().tabs[id].actualPlan })),
  },
  { id: "query.actualPlan", title: "Run with actual plan", category: "Query", run: withTab((id) => showPlan(id, "actual")) },
  { id: "query.format", title: "Format document", keys: ["Ctrl+Shift+F"], category: "Editor", run: withTab(formatTab) },
  { id: "editor.toggleComment", title: "Toggle line comment", keys: ["Ctrl+/"], category: "Editor", run: withTab((id) => commentLines(id, "toggle")) },
  // The Ctrl+K chords (SSMS layout) are Monaco's own bindings and are listed here for display: the dispatcher matches
  // single strokes only, so a remap adds a single-stroke shortcut next to the chord.
  { id: "editor.comment", title: "Comment selected lines", keys: ["Ctrl+K Ctrl+C"], category: "Editor", run: withTab((id) => commentLines(id, "add")) },
  { id: "editor.uncomment", title: "Uncomment selected lines", keys: ["Ctrl+K Ctrl+U"], category: "Editor", run: withTab((id) => commentLines(id, "remove")) },
  { id: "editor.blockComment", title: "Toggle block comment", keys: ["Shift+Alt+A"], category: "Editor", run: withTab((id) => commentLines(id, "block")) },
  { id: "results.toggle", title: "Toggle results panel", keys: ["Ctrl+R"], category: "View", run: withTab(toggleBottom) },
  { id: "results.grid", title: "Show the result as a grid", keys: ["Ctrl+1"], category: "View", run: withTab((id) => showResultAs(id, "grid")) },
  { id: "results.chart", title: "Show the result as a chart", keys: ["Ctrl+2"], category: "View", run: withTab((id) => showResultAs(id, "chart")) },
  { id: "panel.maximize", title: "Maximize or restore the results panel", keys: ["Ctrl+Shift+R"], category: "View", run: withTab(toggleMaximize) },
  { id: "panel.nextOutput", title: "Results panel: next result set or error", keys: ["Alt+PageDown"], category: "View", scope: "results", run: withTab((id) => stepOutput(id, 1)) },
  { id: "panel.previousOutput", title: "Results panel: previous result set or error", keys: ["Alt+PageUp"], category: "View", scope: "results", run: withTab((id) => stepOutput(id, -1)) },
  { id: "panel.nextRun", title: "Results panel: next kept run", keys: ["Ctrl+Alt+PageDown"], category: "View", scope: "results", run: withTab((id) => stepRun(id, 1)) },
  { id: "panel.previousRun", title: "Results panel: previous kept run", keys: ["Ctrl+Alt+PageUp"], category: "View", scope: "results", run: withTab((id) => stepRun(id, -1)) },
  { id: "panel.compare", title: "Compare the shown result set with another", category: "View", run: withTab(compareNext) },
  { id: "query.nextError", title: "Go to the next error of the run", keys: ["F4"], category: "Query", run: withTab((id) => stepError(id, 1)) },
  { id: "query.previousError", title: "Go to the previous error of the run", keys: ["Shift+F4"], category: "Query", run: withTab((id) => stepError(id, -1)) },
  { id: "proc.apply", title: "Apply procedure to server", keys: ["Ctrl+Shift+S"], category: "Procedure", run: withTab(applyProcedure) },
  { id: "editor.definition", title: "Go to definition", keys: ["F12"], category: "Editor", run: withTab(goToDefinition) },
  // As in VS Code: Alt+Left/Right, and Ctrl+-/Ctrl+Shift+- on macOS where Option+Left moves by word.
  { id: "nav.back", title: "Go back", keys: isMac ? ["MacCtrl+-"] : ["Alt+Left"], category: "Go", run: goBack },
  { id: "nav.forward", title: "Go forward", keys: isMac ? ["MacCtrl+Shift+-"] : ["Alt+Right"], category: "Go", run: goForward },
  { id: "editor.findUsages", title: "Find usages (object, column or local name at the cursor)", keys: ["Shift+F12"], category: "Editor", run: withTab(findUsagesAtCursor) },
  { id: "editor.rename", title: "Rename (previewed; local names in the editor)", keys: ["F2"], category: "Editor", run: withTab(renameAtCursor) },
  { id: "editor.insertPrint", title: "Insert PRINT/SELECT for @variable at cursor", category: "Editor", run: withTab(insertPrintForVariable) },
  { id: "editor.expandWildcard", title: "Expand '*' to column list", category: "Editor", run: withTab(expandWildcardAtCursor) },
  { id: "tab.new", title: "New query tab on the same connection", keys: ["Ctrl+N"], category: "Tabs", run: () => void newQueryTab() },
  {
    id: "tab.close", title: "Close tab", keys: ["Ctrl+W"], category: "Tabs",
    run: () => { const tool = useToolTabs.getState().activeToolId; if (tool) closeTool(tool); else withTab(closeTab)(); },
  },
  { id: "tab.list", title: "Go to open tab…", keys: ["Ctrl+P"], category: "Tabs", run: () => useUi.getState().setPalette({ mode: "tabs" }) },
  { id: "tab.reopenClosed", title: "Reopen closed tab", keys: ["Ctrl+Shift+T"], category: "Tabs", run: () => void reopenClosed() },
  // The open-tabs list shows the recently closed ones under the open tabs.
  { id: "tab.showClosed", title: "Reopen closed tab…", category: "Tabs", run: () => useUi.getState().setPalette({ mode: "tabs" }) },
  // MacCtrl+Tab: on macOS Cmd+Tab belongs to the system, so the switcher uses the Control key there.
  { id: "tab.switchRecent", title: "Switch to recent tab (hold Ctrl)", keys: ["Ctrl+Tab", "MacCtrl+Tab"], category: "Tabs", run: () => switchTab(1) },
  { id: "tab.switchRecentBack", title: "Switch to recent tab, backwards", keys: ["Ctrl+Shift+Tab", "MacCtrl+Shift+Tab"], category: "Tabs", run: () => switchTab(-1) },
  { id: "tab.next", title: "Next tab", keys: ["Ctrl+PageDown"], category: "Tabs", run: () => nextTab(1) },
  { id: "tab.previous", title: "Previous tab", keys: ["Ctrl+PageUp"], category: "Tabs", run: () => nextTab(-1) },
  { id: "tab.moveLeft", title: "Move tab left", keys: ["Ctrl+Shift+PageUp"], category: "Tabs", run: () => moveTabBy(workspaceHooks.focusedTab?.() ?? activePanelId(), -1) },
  { id: "tab.moveRight", title: "Move tab right", keys: ["Ctrl+Shift+PageDown"], category: "Tabs", run: () => moveTabBy(workspaceHooks.focusedTab?.() ?? activePanelId(), 1) },
  ...[1, 2, 3, 4, 5, 6, 7, 8, 9].map((n): Command => ({ id: `tab.goto${n}`, title: `Go to tab ${n}`, keys: [`Alt+${n}`], category: "Tabs", run: () => gotoTab(n) })),
  { id: "tab.gotoLast", title: "Go to last tab", keys: ["Alt+0"], category: "Tabs", run: () => gotoTab(0) },
  { id: "tab.pin", title: "Pin/unpin tab", category: "Tabs", run: withTab(togglePin) },
  { id: "tab.duplicate", title: "Duplicate tab", category: "Tabs", run: withTab(duplicateTab) },
  { id: "tab.closeOthers", title: "Close other tabs", category: "Tabs", run: withTab((id) => closeTabs("others", id)) },
  { id: "tab.closeLeft", title: "Close tabs to the left", category: "Tabs", run: withTab((id) => closeTabs("left", id)) },
  { id: "tab.closeRight", title: "Close tabs to the right", category: "Tabs", run: withTab((id) => closeTabs("right", id)) },
  { id: "tab.closeSaved", title: "Close saved and empty tabs", category: "Tabs", run: withTab((id) => closeTabs("saved", id)) },
  { id: "tab.closeEmpty", title: "Close empty tabs", category: "Tabs", run: withTab((id) => closeTabs("empty", id)) },
  { id: "tab.closeConnection", title: "Close tabs on this connection", category: "Tabs", run: withTab((id) => closeTabs("connection", id)) },
  { id: "tab.closeAll", title: "Close all tabs (keeps pinned)", category: "Tabs", run: withTab((id) => closeTabs("all", id)) },
  { id: "file.open", title: "Open SQL file…", keys: ["Ctrl+O"], category: "File", run: () => void openFile() },
  { id: "notebook.new", title: "New notebook", category: "Notebook", run: () => void import("../notebook/runtime").then((m) => m.newNotebookTab()) },
  { id: "notebook.open", title: "Open notebook…", category: "Notebook", run: () => void import("../notebook/files").then((m) => m.openNotebookFile()) },
  { id: "notebook.importIpynb", title: "Import Jupyter notebook (.ipynb)…", category: "Notebook", run: () => void import("../notebook/files").then((m) => m.importIpynb()) },
  {
    id: "notebook.runAll", title: "Notebook: run all cells (Shift+F5 in the notebook)", category: "Notebook",
    run: withTab((id) => useTabs.getState().tabs[id]?.notebook && import("../notebook/runtime").then((m) => m.runAll(id))),
  },
  { id: "file.save", title: "Save to file", keys: ["Ctrl+S"], category: "File", run: () => void saveFile() },
  { id: "file.saveAs", title: "Save as…", keys: ["Ctrl+Alt+S"], category: "File", run: () => void saveAs() },
  { id: "palette.commands", title: "Command palette", keys: ["Ctrl+Shift+P"], category: "View", run: () => useUi.getState().setPalette({ mode: "commands" }) },
  { id: "palette.objects", title: "Go to object", keys: ["Ctrl+T"], category: "View", run: () => useUi.getState().setPalette({ mode: "objects" }) },
  { id: "palette.databases", title: "Change database", keys: ["Ctrl+U"], category: "Connection", run: () => useUi.getState().setPalette({ mode: "databases" }) },
  { id: "palette.connections", title: "Change connection", keys: ["Ctrl+Shift+U"], category: "Connection", run: () => useUi.getState().setPalette({ mode: "connections" }) },
  { id: "connection.connect", title: "Connect to a profile…", category: "Connection", run: () => useUi.getState().openDialog({ kind: "connect" }) },
  { id: "connection.newProfile", title: "New connection profile…", category: "Connection", run: () => useUi.getState().openDialog({ kind: "profile" }) },
  { id: "tx.begin", title: "Begin transaction", category: "Transaction", run: withTab((id) => txAction(id, "begin")) },
  { id: "tx.commit", title: "Commit transaction", category: "Transaction", run: withTab((id) => txAction(id, "commit")) },
  { id: "tx.rollback", title: "Roll back transaction", category: "Transaction", run: withTab((id) => txAction(id, "rollback")) },
  { id: "trace.toggle", title: "Start/stop Trace (Extended Events)", category: "Diagnostics", run: withTab(toggleTrace) },
  { id: "debug.stepOver", title: "Debugger: step over", keys: ["F10"], category: "Diagnostics", run: () => { const t = debugging(); if (t) void stepDebug(t.id, "over"); } },
  { id: "debug.stepInto", title: "Debugger: step into", keys: ["F11"], category: "Diagnostics", run: () => { const t = debugging(); if (t) void stepDebug(t.id, "into"); } },
  { id: "debug.stop", title: "Debugger: stop", keys: ["Shift+F5"], category: "Diagnostics", run: () => { const t = debugging(); if (t) void stopDebug(t.id); } },
  { id: "view.history", title: "Query history", keys: ["Ctrl+H"], category: "View", run: () => useUi.getState().openDialog({ kind: "history" }) },
  { id: "snippet.insert", title: "Insert snippet…", category: "Snippets", run: () => useUi.getState().setPalette({ mode: "snippets" }) },
  { id: "snippet.saveSelection", title: "Save selection as snippet", category: "Snippets", run: () => import("../snippets/snippetActions").then((m) => m.saveSelectionAsSnippet()) },
  { id: "snippet.manage", title: "Manage snippets…", category: "Snippets", run: () => useUi.getState().openDialog({ kind: "snippets" }) },
  { id: "erd.open", title: "ER diagram (current query's tables, else the database)", keys: ["Ctrl+Shift+E"], category: "View", run: () => openErdFromActiveTab() },
  { id: "compare.schema", title: "Schema compare (the tab's database as the source)", keys: ["Ctrl+Shift+D"], category: "Data", run: () => openSchemaCompare() },
  { id: "compare.data", title: "Data compare (two tables or queries)", keys: [], category: "Data", run: () => openDataCompare() },
  { id: "monitor.open", title: "Activity monitor (the active tab's server)", keys: [], category: "Server", run: () => openActivityMonitor() },
  {
    id: "import.open", title: "Import data from a file…", keys: ["Ctrl+Alt+I"], category: "Data",
    run: () => {
      const t = activeTabState();
      if (!t?.profileId || !t.database) return void useUi.getState().toast("info", "Pick a connection and database for this tab first.");
      // A session-owned engine's tab imports through its own session (its in-memory database, its temporary tables).
      useUi.getState().openDialog({ kind: "import", profileId: t.profileId, database: t.database, ...sessionParam(tabScope(t)?.sessionId) });
    },
  },
  { id: "view.settings", title: "Settings", keys: ["Ctrl+,"], category: "View", run: () => useUi.getState().openDialog({ kind: "settings" }) },
  { id: "view.shortcuts", title: "Keyboard shortcuts", category: "View", run: () => useUi.getState().openDialog({ kind: "settings", page: "keyboard" }) },
  { id: "help.about", title: "About Querithm (version)", category: "Help", run: () => useUi.getState().openDialog({ kind: "settings", page: "about" }) },
  {
    id: "help.checkUpdates", title: "Check for updates…", category: "Help",
    run: () => {
      useUi.getState().openDialog({ kind: "settings", page: "about", focus: "setting-checkUpdates" });
      void checkForUpdates(true);
    },
  },
  { id: "view.focusNext", title: "Focus next area (top bar, Object Explorer, editor, results)", keys: ["F6"], category: "View", run: () => cycleFocus(1) },
  { id: "view.focusPrevious", title: "Focus previous area", keys: ["Shift+F6"], category: "View", run: () => cycleFocus(-1) },
  { id: "view.explorer", title: "Toggle object explorer", keys: ["Ctrl+B"], category: "View", run: () => explorerAction("toggle") },
  { id: "view.explorer.focus", title: "Object explorer: show and search", keys: ["F8"], category: "View", run: () => explorerAction("show") },
  { id: "view.explorer.autoHide", title: "Object explorer: toggle auto hide", category: "View", run: () => explorerAction("togglePin") },
  { id: "view.explorer.pinned", title: "Object explorer: dock (always show)", category: "View", run: () => explorerAction({ mode: "pinned" }) },
  // Interface scale (L26). Ctrl+0 in an editable grid stays "set NULL" (GRID_KEYS); Ctrl+Shift+= is Ctrl++ on US layouts.
  { id: "view.zoomIn", title: "Interface scale: larger", keys: ["Ctrl+=", "Ctrl+Shift+="], category: "View", run: () => stepUiScale(1) },
  { id: "view.zoomOut", title: "Interface scale: smaller", keys: ["Ctrl+-"], category: "View", run: () => stepUiScale(-1) },
  { id: "view.zoomReset", title: "Interface scale: back to 100 %", keys: ["Ctrl+0"], category: "View", run: () => stepUiScale(0) },
  {
    id: "view.theme", title: "Toggle dark/light theme", category: "View",
    run: () => useSettings.getState().set({ theme: document.documentElement.dataset.theme === "light" ? "dark" : "light" }),
  },
];

export { displayKeys, eventToShortcut } from "./shortcuts";

export function keysFor(c: Command): string[] {
  const override = useSettings.getState().keybindings[c.id];
  return override ? splitBinding(override) : (c.keys ?? []);
}

/** Shortcuts an editable grid handles itself. */
export const GRID_KEYS = new Set(["Ctrl+S", "Ctrl+0", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Alt+Insert", "Ctrl+Delete"]);

/** Shortcuts the Object Explorer's tree handles itself while it has focus (§12): Ctrl+Enter inserts the name instead of
 *  running the query, F5 refreshes, F2 renames, Shift+F12 finds usages, Ctrl+C copies the name. */
/** Keys a focused strip tab handles itself (D22): F2 renames the tab there, not the symbol at the editor's cursor. */
export const STRIP_TAB_KEYS = new Set(["F2", "Shift+F10", "Delete", "Escape", "Enter", "Space"]);
export const EXPLORER_TREE_KEYS = new Set(["Ctrl+Enter", "Ctrl+Alt+Enter", "F5", "F2", "Shift+F12", "Ctrl+C"]);
/** In the Explorer's search box only the insert keys are its own; F5 and the rest keep their app meaning there. */
export const EXPLORER_SEARCH_KEYS = new Set(["Ctrl+Enter", "Ctrl+Alt+Enter"]);
/** Keys a notebook handles itself while focus is in it (Phase C3): run all (Shift+F5 stops the debugger elsewhere) and
 *  run the cell then move to the next. F5 / Ctrl+Enter stay app keys: Run acts on the focused cell. */
export const NOTEBOOK_KEYS = new Set(["Shift+F5", "Shift+Enter"]);

export function installKeybindings() {
  let monacoChord = false;
  const handler = (e: KeyboardEvent) => {
    if (e.isComposing || ["Control", "Shift", "Alt", "Meta"].includes(e.key)) return;
    if (useUi.getState().dialogs.length || useUi.getState().palette) return; // dialogs own the keyboard
    const sc = eventToShortcut(e);
    const target = e.target as HTMLElement | null;
    // The second stroke of a Monaco Ctrl+K chord belongs to Monaco (Ctrl+K Ctrl+U uncomments; Ctrl+U alone is an app key).
    const inMonaco = !!target?.closest?.(".monaco-editor");
    const chordTail = monacoChord && inMonaco;
    monacoChord = inMonaco && sc === "Ctrl+K";
    if (chordTail) return;
    const inField = !!target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT") && !target.closest(".monaco-editor");
    // An editable result grid owns its editing keys (Ctrl+S applies the staged changes there, not Save).
    if (target?.closest?.("[data-q-grid-edit]") && GRID_KEYS.has(sc)) return;
    if (target?.closest?.("[data-q-explorer-tree]") && EXPLORER_TREE_KEYS.has(sc)) return;
    if (target?.closest?.('.dv-tab[role="tab"]') && STRIP_TAB_KEYS.has(sc)) return;
    if (target?.closest?.("[data-q-explorer-search]") && EXPLORER_SEARCH_KEYS.has(sc)) return;
    if (target?.closest?.("[data-q-notebook]") && NOTEBOOK_KEYS.has(sc)) return;
    for (const c of commands) {
      if (!keysFor(c).some((k) => !isChord(k) && canonical(k) === sc)) continue;
      if (c.scope === "results" && !target?.closest?.('[data-focus-region="results"]')) continue;
      // In plain inputs only function keys and Ctrl chords are app shortcuts.
      if (inField && !/^(Ctrl|F\d)/.test(sc)) return;
      e.preventDefault();
      e.stopPropagation();
      void c.run();
      return;
    }
  };
  window.addEventListener("keydown", handler, true);
  return () => window.removeEventListener("keydown", handler, true);
}

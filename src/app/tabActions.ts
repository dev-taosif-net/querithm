// Tab navigation, pins, bulk close and the Ctrl+Tab switcher, on top of the Dockview hooks in workspaceHooks.
import { activateTab, closeTab, newQueryTab, workspaceHooks } from "./actions";
import { getModel } from "../editor/editors";
import { notebookHooks } from "../notebook/hooks";
import { useTabs } from "../store/tabs";
import { useToolTabs } from "../store/toolTabs";
import { closeTool } from "./toolActions";
import { entryAt, useClosedTabs } from "../store/closedTabs";
import { useProfiles } from "../store/profiles";
import { ask, toast, useUi } from "../store/ui";
import { closePlan, closeReport, nthTab, pinnedFirst, reusableTab, stepTab, switcherOrder, unsavedAmong, type ClosePlan, type CloseScope } from "./tabNav";
import { announce } from "../store/announce";

/** A tab's text: its editor's, or a notebook's cells. */
export const textOf = (id: string) => getModel(id)?.getValue() ?? notebookHooks.text?.(id) ?? "";

/** On-screen order of the strip holding `tabId` (store order before the workspace mounts). */
export function stripOrder(tabId?: string): string[] {
  return workspaceHooks.stripOrder?.(tabId) ?? useTabs.getState().order;
}

export function allTabsOrder(): string[] {
  return workspaceHooks.allOrder?.() ?? useTabs.getState().order;
}

function activate(id: string | undefined) {
  if (id) activateTab(id);
}

/** The tab the active strip shows: a query tab, or a tool tab (diagram, compare, monitor). */
export function activePanelId(): string | undefined {
  return useTabs.getState().activeId ?? useToolTabs.getState().activeToolId;
}

/** Ctrl+PageDown / Ctrl+PageUp: next/previous tab in the active strip, wrapping; tool tabs count (D17). */
export function nextTab(delta: 1 | -1) {
  activate(stepTab(stripOrder(), activePanelId(), delta));
}

/**
 * Ctrl+Shift+PageUp / PageDown: moves a tab one place in its strip (the focused tab, else the shown one). Pinned
 * tabs stay first, so a move never crosses between pinned and unpinned tabs. Keyboard focus stays on the tab.
 */
export function moveTabBy(id: string | undefined, delta: -1 | 1) {
  if (!id) return;
  const order = stripOrder(id);
  const i = order.indexOf(id);
  const j = i + delta;
  if (i < 0 || j < 0 || j >= order.length) return;
  const tabs = useTabs.getState().tabs;
  if (!!tabs[order[j]]?.pinned !== !!tabs[id]?.pinned) return;
  const focused = workspaceHooks.focusedTab?.() === id;
  workspaceHooks.moveTab?.(id, j);
  if (focused) workspaceHooks.focusTab?.(id);
}

/** Alt+1…Alt+9: that position in the active strip; Alt+0: the last tab. */
export function gotoTab(n: number) {
  activate(nthTab(stripOrder(), n));
}

/** Moves pinned tabs to the front of their strip, keeping everything else in place. */
export function applyPinnedOrder(tabId?: string) {
  const move = workspaceHooks.moveTab;
  if (!move) return;
  const tabs = useTabs.getState().tabs;
  const desired = pinnedFirst(stripOrder(tabId), (id) => !!tabs[id]?.pinned);
  desired.forEach((id, i) => {
    if (stripOrder(tabId)[i] !== id) move(id, i);
  });
}

export function togglePin(id: string) {
  const t = useTabs.getState().tabs[id];
  if (!t) return;
  // Pinning keeps a preview tab (D25).
  useTabs.getState().update(id, { pinned: !t.pinned, preview: false });
  applyPinnedOrder(id);
}

/**
 * Closes a set of tabs. Pinned tabs are never in the set; running and open-transaction tabs are kept and reported
 * (D20), so a bulk close never cancels a query or asks about transactions tab by tab. SQL that exists only in a tab
 * is lost on close, so a single confirmation lists those tabs first.
 */
export async function closeTabs(scope: CloseScope, tabId: string): Promise<void> {
  const plan = planClose(scope, tabId);
  const { tabs } = useTabs.getState();
  const targets = plan.close;
  if (!targets.length) {
    if (plan.running.length || plan.inTx.length) report(0, plan);
    return;
  }
  const unsaved = unsavedAmong(targets, tabs, textOf);
  if (unsaved.length) {
    const names = unsaved.slice(0, 5).map((id) => tabs[id].title).join(", ") + (unsaved.length > 5 ? ` and ${unsaved.length - 5} more` : "");
    const ok = await ask<boolean>((resolve) => ({
      kind: "confirm",
      title: `Close ${targets.length} tab${targets.length === 1 ? "" : "s"}?`,
      body: `${unsaved.length === 1 ? "This tab has" : `${unsaved.length} tabs have`} SQL that is not saved to a file and will be lost: ${names}.`,
      danger: true,
      ok: "Close tabs",
      resolve,
    }));
    if (!ok) return;
  }
  // The tabs of one bulk close come back together: one "Reopen 29 tabs" entry (D28).
  const batch = targets.length > 1 ? `bulk-${Date.now().toString(36)}-${++batches}` : undefined;
  let closed = 0;
  for (const id of targets) {
    if (useToolTabs.getState().tools[id]) closeTool(id);
    else if (!(await closeTab(id, { batch }))) break;
    closed++;
  }
  report(closed, plan);
}
let batches = 0;

/** What a bulk close from `tabId` would do (the menu shows its counts). */
export function planClose(scope: CloseScope, tabId: string): ClosePlan {
  const { tabs } = useTabs.getState();
  const order = scope === "right" || scope === "left" ? stripOrder(tabId) : allTabsOrder();
  return closePlan(scope, { order, tabs, tools: useToolTabs.getState().tools, tabId, textOf });
}

/** Says what a bulk close did: always to screen readers, and on screen too when tabs were kept. */
function report(closed: number, plan: Pick<ClosePlan, "running" | "inTx">) {
  const text = closeReport(closed, plan);
  announce(text);
  if (plan.running.length || plan.inTx.length) toast("info", `${text}.`);
}

/**
 * Middle-click closes like Ctrl+W, except on tabs where a stray click would end work: pinned tabs, and tabs that are
 * running a query or hold an open transaction (D20). Those say how to close them instead.
 */
export function middleClickClose(id: string) {
  const t = useTabs.getState().tabs[id];
  if (!t || t.pinned) return;
  if (t.busy || t.txState !== "none") {
    toast("info", `"${t.title}" ${t.busy ? "is running a query" : t.txState === "open" ? "has an open transaction" : "may have an open transaction"}; press Ctrl+W to close it.`);
    return;
  }
  void closeTab(id);
}

/**
 * Ctrl+Shift+T: reopens a recently closed tab (the newest by default) with its text, title, file and binding. A tab
 * whose connection profile was deleted since opens without a connection, and says so.
 */
export function reopenClosed(index = 0): string | undefined {
  const closed = useClosedTabs.getState();
  const entry = entryAt(closed.list, index);
  if (!entry.length) return undefined;
  // A bulk close comes back whole, in its old order (D28); each tab goes back to its strip and position.
  const snaps = entry.map((i) => closed.list[i]).sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
  closed.removeMany(entry);
  const gone = snaps.filter((s) => s.profileId && !useProfiles.getState().byId(s.profileId)).map((s) => s.title);
  if (gone.length) toast("warn", `The connection of "${gone.join('", "')}" no longer exists; ${gone.length === 1 ? "the tab" : "those tabs"} opened without a connection.`);
  let last: string | undefined;
  for (const s of snaps) {
    const profile = useProfiles.getState().byId(s.profileId);
    if (s.notebook) {
      const nb = notebookHooks.reopen?.({
        text: s.sql, title: s.title, filePath: s.filePath, profileId: profile?.id ?? "", database: profile ? s.database : undefined, dirty: s.dirty,
        activate: snaps.length === 1, atEnd: s.index !== undefined,
      });
      if (nb && s.index !== undefined) workspaceHooks.place?.(nb, s.groupId, s.index);
      last = nb ?? last;
      continue;
    }
    const id = newQueryTab({
      // "" = no connection (undefined would inherit the active tab's).
      profileId: profile?.id ?? "", database: profile ? s.database : undefined,
      text: s.sql, title: s.title, filePath: s.filePath, boundObject: profile ? s.boundObject : undefined, readOnly: s.readOnly,
      activate: snaps.length === 1, atEnd: s.index !== undefined,
    });
    if (s.index !== undefined) workspaceHooks.place?.(id, s.groupId, s.index);
    if (s.dirty) useTabs.getState().update(id, { dirty: true });
    last = id;
  }
  if (snaps.length > 1) {
    if (last) activateTab(last);
    announce(`Reopened ${snaps.length} tabs`);
  }
  return last;
}

/** The tab a new connection should use: an existing unconnected tab when there is one, else undefined (open a new tab). */
export function tabForNewConnection(): string | undefined {
  const s = useTabs.getState();
  return reusableTab(allTabsOrder(), s.tabs, s.activeId, textOf);
}

// ------------------------------------------------------------------------------------------------- Ctrl+Tab switcher

/**
 * Ctrl+Tab opens the switcher on the previous tab (Ctrl+Shift+Tab on the oldest); each further press moves the
 * highlight. Releasing Ctrl switches, Escape or leaving the window cancels. A quick press-and-release flips between
 * the two most recent tabs.
 */
let finishSwitcher: ((commit: boolean) => void) | undefined;

export function switchTab(delta: 1 | -1) {
  const ui = useUi.getState();
  const cur = ui.switcher;
  if (cur) {
    ui.setSwitcher({ ...cur, index: (((cur.index + delta) % cur.ids.length) + cur.ids.length) % cur.ids.length });
    return;
  }
  const ids = switcherOrder(useTabs.getState().mru, allTabsOrder());
  if (ids.length < 2) return;
  ui.setSwitcher({ ids, index: delta > 0 ? 1 : ids.length - 1 });
  const done = (commit: boolean) => {
    finishSwitcher = undefined;
    window.removeEventListener("keyup", onUp, true);
    window.removeEventListener("keydown", onDown, true);
    window.removeEventListener("blur", onBlur);
    const s = useUi.getState().switcher;
    useUi.getState().setSwitcher(null);
    if (commit && s) activate(s.ids[s.index]);
  };
  const onUp = (e: KeyboardEvent) => {
    if ((e.key === "Control" || e.key === "Meta") && !e.ctrlKey && !e.metaKey) done(true);
  };
  const onDown = (e: KeyboardEvent) => {
    if (e.key === "Escape") {
      e.preventDefault();
      e.stopPropagation();
      done(false);
    } else if (e.key === "Enter") {
      e.preventDefault();
      e.stopPropagation();
      done(true);
    }
  };
  const onBlur = () => done(false);
  finishSwitcher = done;
  window.addEventListener("keyup", onUp, true);
  window.addEventListener("keydown", onDown, true);
  window.addEventListener("blur", onBlur);
}

/** A click in the switcher picks that tab. */
export function pickFromSwitcher(index: number) {
  const s = useUi.getState().switcher;
  if (!s) return;
  useUi.getState().setSwitcher({ ...s, index });
  finishSwitcher?.(true);
}

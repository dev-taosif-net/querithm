// Tab navigation and bulk-close rules, kept pure (no Dockview, Monaco or stores) so they are testable.
// `order` is always the on-screen order of the tab strip, which the user can change by dragging.
import type { TabState } from "../store/tabs";

/** The tab `delta` steps from `current` in `order`, wrapping around; undefined when there are no tabs. */
export function stepTab(order: readonly string[], current: string | undefined, delta: number): string | undefined {
  if (!order.length) return undefined;
  const i = current ? order.indexOf(current) : -1;
  if (i < 0) return delta >= 0 ? order[0] : order[order.length - 1];
  return order[(((i + delta) % order.length) + order.length) % order.length];
}

/** Alt+1…Alt+9 pick that position; 0 picks the last tab (as browsers do with Ctrl+9). */
export function nthTab(order: readonly string[], n: number): string | undefined {
  if (n === 0) return order[order.length - 1];
  return order[n - 1];
}

/** Ctrl+Tab list: most recently used first, then any open tab the MRU list has not seen, in strip order. */
export function switcherOrder(mru: readonly string[], order: readonly string[]): string[] {
  const open = new Set(order);
  const seen = mru.filter((id) => open.has(id));
  const rest = order.filter((id) => !seen.includes(id));
  return [...seen, ...rest];
}

/** Where a new tab opens: first in the strip, right after the pinned tabs. */
export function newTabIndex(order: readonly string[], isPinned: (id: string) => boolean): number {
  return order.filter(isPinned).length;
}

/** Pinned tabs first (keeping their relative order), then the rest. */
export function pinnedFirst(order: readonly string[], isPinned: (id: string) => boolean): string[] {
  return [...order.filter(isPinned), ...order.filter((id) => !isPinned(id))];
}

/**
 * What closing a tab would lose:
 * - "empty": no text;
 * - "saved": the text is in its file unchanged, or it is a read-only definition the server can show again;
 * - "unsaved": SQL that exists only in this tab (closing deletes its autosaved copy).
 */
export type TabContent = "empty" | "saved" | "unsaved";

export function tabContent(t: Pick<TabState, "filePath" | "dirty" | "readOnly">, text: string): TabContent {
  if (!text.trim()) return "empty";
  if (t.readOnly || (t.filePath && !t.dirty)) return "saved";
  return "unsaved";
}

export type CloseScope = "others" | "right" | "left" | "saved" | "empty" | "connection" | "all";

export interface CloseContext {
  /** On-screen order of the strip that holds `tabId` (for "right"), or of every tab. */
  order: readonly string[];
  tabs: Readonly<Record<string, TabState>>;
  /** Tool tabs (diagram, compare, monitor): closed like query tabs, with nothing to lose (D17). */
  tools?: Readonly<Record<string, { profileId?: string }>>;
  /** The tab the menu was opened on. */
  tabId: string;
  textOf: (id: string) => string;
}

/** What a bulk close does: the tabs it closes, and the ones in its scope it keeps because closing would end work. */
export interface ClosePlan {
  close: string[];
  /** Running a query: closing would cancel it. */
  running: string[];
  /** Holding an open (or possibly open) transaction: closing would have to commit or roll it back. */
  inTx: string[];
}

/**
 * Splits a bulk close's scope (pinned tabs are never in it) into the tabs it closes and the ones it keeps (D20): a
 * bulk close never cancels a query or asks about a transaction tab by tab; those tabs stay and are reported.
 */
export function closePlan(scope: CloseScope, c: CloseContext): ClosePlan {
  const at = c.order.indexOf(c.tabId);
  const selfProfile = c.tabs[c.tabId]?.profileId ?? c.tools?.[c.tabId]?.profileId;
  const plan: ClosePlan = { close: [], running: [], inTx: [] };
  c.order.forEach((id, i) => {
    const tool = c.tools?.[id];
    if (tool && !c.tabs[id]) {
      // A tool tab holds no SQL and no session: "saved" (nothing lost), never "empty".
      const take = scope === "others" ? id !== c.tabId : scope === "right" ? at >= 0 && i > at : scope === "left" ? at >= 0 && i < at
        : scope === "saved" || scope === "all" ? true : scope === "connection" ? !!selfProfile && tool.profileId === selfProfile : false;
      if (take) plan.close.push(id);
      return;
    }
    const t = c.tabs[id];
    if (!t || t.pinned) return;
    const inScope = (() => {
      switch (scope) {
        case "others":
          return id !== c.tabId;
        case "right":
          return at >= 0 && i > at;
        case "left":
          return at >= 0 && i < at;
        case "saved":
          return tabContent(t, c.textOf(id)) !== "unsaved";
        case "empty":
          return tabContent(t, c.textOf(id)) === "empty";
        case "connection":
          return !!selfProfile && t.profileId === selfProfile;
        case "all":
          return true;
      }
    })();
    if (!inScope) return;
    if (t.busy) plan.running.push(id);
    // An unknown transaction state counts as open (M9): closing would have to end whatever the connection holds.
    else if (t.txState !== "none") plan.inTx.push(id);
    else plan.close.push(id);
  });
  return plan;
}

/** Tabs a bulk close removes. Pinned, running and open-transaction tabs are never included. */
export function closeTargets(scope: CloseScope, c: CloseContext): string[] {
  return closePlan(scope, c).close;
}

/** "Closed 9 tabs" / "Closed 9; kept 2: 1 running, 1 open transaction" (D20). */
export function closeReport(closed: number, plan: Pick<ClosePlan, "running" | "inTx">): string {
  const kept = plan.running.length + plan.inTx.length;
  const head = `Closed ${closed}${kept ? "" : ` tab${closed === 1 ? "" : "s"}`}`;
  if (!kept) return head;
  const why = [
    plan.running.length && `${plan.running.length} running`,
    plan.inTx.length && `${plan.inTx.length} open transaction${plan.inTx.length === 1 ? "" : "s"}`,
  ].filter(Boolean);
  return `${head}; kept ${kept}: ${why.join(", ")}`;
}

/** Of `ids`, the tabs whose SQL would be lost, for the confirmation. */
export function unsavedAmong(ids: readonly string[], tabs: Readonly<Record<string, TabState>>, textOf: (id: string) => string): string[] {
  return ids.filter((id) => tabs[id] && tabContent(tabs[id], textOf(id)) === "unsaved");
}

/**
 * An existing tab a new connection can take over instead of opening another one: the active tab when it is
 * unconnected, otherwise the first unconnected, empty, idle, unpinned tab with no file.
 */
export function reusableTab(order: readonly string[], tabs: Readonly<Record<string, TabState>>, activeId: string | undefined, textOf: (id: string) => string): string | undefined {
  const free = (t: TabState | undefined) => !!t && !t.profileId && !t.busy && !t.readOnly && !t.boundObject;
  const active = activeId ? tabs[activeId] : undefined;
  if (free(active)) return activeId;
  return order.find((id) => {
    const t = tabs[id];
    return free(t) && !t.pinned && !t.filePath && tabContent(t, textOf(id)) === "empty";
  });
}

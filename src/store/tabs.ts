import { create } from "zustand";
import type { ObjectKind, TxState } from "../ipc/contract";
import type { ChartSpec } from "../charts/chartSpec";

/**
 * The bottom panel's five fixed views (§12 bottom panel): Results, Messages and Plan show the shown run; Runtime
 * (Trace | Debug) and Usages belong to the query tab.
 */
export type BottomTab = "results" | "messages" | "plan" | "runtime" | "usages";
/** The Runtime view's two tools; an engine without one shows why in its place. */
export type RuntimeTool = "trace" | "debug";

/** The object a tab was opened from (§10 Open for edit). */
export interface BoundObject {
  profileId: string;
  database: string;
  objectId?: number | null;
  schema: string;
  name: string;
  kind: ObjectKind;
  definitionHash: string;
}

/** How a result set is shown (Phase C1): the grid or a chart, and the chart's spec (kept while the grid shows). */
export interface ResultChart {
  view: "grid" | "chart";
  spec: ChartSpec | null;
}

/** Per-tab state (§12). Switching tabs is a pointer change: every tab keeps its own slice. */
export interface TabState {
  id: string;
  title: string;
  profileId?: string;
  database?: string;
  sessionId?: string;
  txState: TxState;
  txStartedAt?: number;
  spid?: number;
  busy: boolean;
  boundObject?: BoundObject;
  /** Read-only definition tab (go to definition). */
  readOnly?: boolean;
  filePath?: string;
  dirty: boolean;
  executions: string[];
  activeExecutionId?: string;
  bottomTab: BottomTab;
  bottomOpen: boolean;
  /** Fraction of the tab height used by the bottom panel. */
  bottomSize: number;
  /** The panel fills the tab; the editor shrinks to a one-line strip (Ctrl+Shift+R). */
  bottomMax?: boolean;
  /** The Runtime view's tool; unset = the engine's own (Trace on SQL Server, Debug on PostgreSQL). */
  runtimeTool?: RuntimeTool;
  /** The ledger shows this error (index among the run's errors) instead of a result set. */
  ledgerError?: number;
  /** A second result set shown beside the first (compare), from any run the tab keeps. */
  compare?: { executionId: string; resultIndex: number };
  /** The execution whose notice line was dismissed (it returns on the next run). */
  noticeDismissed?: string;
  maxRows?: number;
  actualPlan: boolean;
  /**
   * Phase D4: the tab's server-output toggle (Oracle DBMS_OUTPUT), sent with every run on an engine that has one
   * (EngineTraits.serverOutput); unset is on. SET SERVEROUTPUT in a script changes it for the rest of that run only.
   */
  serverOutput?: boolean;
  traceId?: string;
  debugId?: string;
  sessionError?: string;
  /**
   * The tab's in-memory database was lost (engine restart, session gone; Phase D0d): its results and schema were
   * cleared and the reason shown. Marked until its next session opens, whose database is a new, empty one.
   */
  memoryLost?: boolean;
  /** Last error line markers (editor lines). */
  errorLines?: number[];
  /** Pinned tabs stay at the left of the strip, compact, and bulk closes skip them. */
  pinned?: boolean;
  /** The object a read-only definition shows (Go to definition): a second jump to it reuses this tab (D25). */
  definitionOf?: { schema: string; name: string };
  /** The group's preview tab: the next read-only definition replaces it, until it is kept (D25). */
  preview?: boolean;
  /** The result set shown in the Results panel (index into the execution's result sets). */
  resultIndex?: number;
  /** Grid or chart per result index, with the chart spec; saved with the workspace (specs only, never data). */
  charts?: Record<number, ResultChart>;
  /** How this tab lays out several result sets; unset = the Settings default. Saved with the workspace. */
  resultLayout?: "tabs" | "stacked" | "side";
  /** Stacked / Side by side: the selected set fills the panel for now (Restore or Esc goes back). */
  resultMaximized?: boolean;
  /** Side by side: the run's result indexes its columns show (the next sets in order fill the rest). */
  sideSets?: number[];
  /** Sets of kept runs that stay on screen across runs (Stacked, Side by side), at most two; dropped with their run. */
  pins?: { executionId: string; resultIndex: number }[];
  /** A SQL notebook (Phase C3): the panel shows cells instead of one editor; no model of its own. */
  notebook?: boolean;
}

/** `id` moved to the front of a most-recently-used list. */
export function touchMru(mru: readonly string[], id: string): string[] {
  return mru[0] === id ? (mru as string[]) : [id, ...mru.filter((x) => x !== id)];
}

let counter = 0;
export function newTabId() {
  counter++;
  return `t${Date.now().toString(36)}${counter.toString(36)}`;
}

/** A user-typed tab name, tidied: one line, trimmed, ≤ 120 chars; null when nothing is left. */
export function cleanTabTitle(input: string): string | null {
  // A leading 🐞 marks debugger tabs (not saved with the workspace), so a user title cannot start with one.
  const t = input.replace(/\s+/g, " ").trim().replace(/^(?:🐞\s*)+/u, "").slice(0, 120).trim();
  return t || null;
}

interface TabsStore {
  tabs: Record<string, TabState>;
  order: string[];
  activeId?: string;
  /** Tab ids, most recently active first (Ctrl+Tab and the open-tabs list). */
  mru: string[];
  add(t: Partial<TabState> & { id: string; title: string }): TabState;
  update(id: string, patch: Partial<TabState>): void;
  /** Renames a tab (its label; a file on disk keeps its name). False when the name is empty or the tab is gone. */
  rename(id: string, input: string): boolean;
  remove(id: string): void;
  setActive(id: string | undefined): void;
  /** Puts a tool tab (not a query tab) at the front of the recent-tabs list. */
  touch(id: string): void;
  /** Drops a closed tool tab from the recent-tabs list. */
  forget(id: string): void;
  bySession(sessionId: string): TabState | undefined;
}

export const useTabs = create<TabsStore>((set, get) => ({
  tabs: {},
  order: [],
  activeId: undefined,
  mru: [],
  add(t) {
    const tab: TabState = {
      txState: "none",
      busy: false,
      dirty: false,
      executions: [],
      bottomTab: "results",
      bottomOpen: true,
      bottomSize: 0.35,
      actualPlan: false,
      ...t,
    };
    set((s) => ({ tabs: { ...s.tabs, [tab.id]: tab }, order: [...s.order, tab.id], activeId: tab.id, mru: touchMru(s.mru, tab.id) }));
    return tab;
  },
  update(id, patch) {
    set((s) => (s.tabs[id] ? { tabs: { ...s.tabs, [id]: { ...s.tabs[id], ...patch } } } : s));
  },
  rename(id, input) {
    const title = cleanTabTitle(input);
    if (!title || !get().tabs[id]) return false;
    get().update(id, { title });
    return true;
  },
  remove(id) {
    set((s) => {
      const { [id]: _, ...rest } = s.tabs;
      const order = s.order.filter((x) => x !== id);
      const activeId = s.activeId === id ? order[Math.max(0, s.order.indexOf(id) - 1)] : s.activeId;
      return { tabs: rest, order, activeId, mru: s.mru.filter((x) => x !== id) };
    });
  },
  setActive(id) {
    set((s) => ({ activeId: id, mru: id ? touchMru(s.mru, id) : s.mru }));
  },
  touch(id) {
    set((s) => ({ mru: touchMru(s.mru, id) }));
  },
  forget(id) {
    set((s) => (s.mru.includes(id) ? { mru: s.mru.filter((x) => x !== id) } : s));
  },
  bySession(sessionId) {
    return Object.values(get().tabs).find((t) => t.sessionId === sessionId);
  },
}));

export function activeTab(): TabState | undefined {
  const s = useTabs.getState();
  return s.activeId ? s.tabs[s.activeId] : undefined;
}

export function useActiveTab(): TabState | undefined {
  return useTabs((s) => (s.activeId ? s.tabs[s.activeId] : undefined));
}

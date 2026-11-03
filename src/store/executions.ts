import { create } from "zustand";
import type { ErrorInfo, ExecState, PlanTree, ResultEditInfo, ServerMessage, StatementStats } from "../ipc/contract";

/** One execution as the UI sees it (§12 ExecutionView). Row data lives in results/model.ts, not here. */
export interface ExecutionView {
  id: string;
  tabId: string;
  sessionId: string;
  state: ExecState;
  startedAt: number;
  elapsedMs?: number;
  serverElapsedMs?: number | null;
  serverCpuMs?: number | null;
  affectedRows?: number | null;
  resultSetCount: number;
  truncated: boolean;
  error?: ErrorInfo | null;
  messages: ServerMessage[];
  statistics: StatementStats[];
  plans: { batchIndex: number; plan: PlanTree; raw: string }[];
  sql: string;
  lineOffset: number;
  queued?: boolean;
  /** Whether the grid may edit each result set, by result index. */
  edits?: Record<number, ResultEditInfo>;
  /**
   * How many messages had arrived when each result set began, by result index: the ledger and the Messages log put
   * sets and errors in the order they happened from it.
   */
  setAfter?: Record<number, number>;
  /** Lines the webview wrote itself (a refused statement, a lost database): nothing ran, so there is no run summary. */
  notRun?: boolean;
}

interface ExecStore {
  views: Record<string, ExecutionView>;
  put(v: ExecutionView): void;
  patch(id: string, p: Partial<ExecutionView> | ((v: ExecutionView) => Partial<ExecutionView>)): void;
  drop(id: string): void;
}

export const useExecutions = create<ExecStore>((set) => ({
  views: {},
  put(v) {
    set((s) => ({ views: { ...s.views, [v.id]: v } }));
  },
  patch(id, p) {
    set((s) => {
      const v = s.views[id];
      if (!v) return s;
      const patch = typeof p === "function" ? p(v) : p;
      return { views: { ...s.views, [id]: { ...v, ...patch } } };
    });
  },
  drop(id) {
    set((s) => {
      const { [id]: _, ...rest } = s.views;
      return { views: rest };
    });
  },
}));

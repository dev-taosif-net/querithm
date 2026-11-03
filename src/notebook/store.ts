// Notebook state (Phase C3), one entry per notebook tab. Cell sources live in the cells' Monaco models while the user
// types (no store update per keystroke) and are read back from them when the notebook is saved or run.
import { create } from "zustand";
import type { ChartSpec } from "../charts/chartSpec";
import { newCellId, type CellKind, type SavedOutput } from "./format";

export type CellStatus = "idle" | "queued" | "running" | "done" | "failed" | "cancelled";

export interface CellState {
  id: string;
  kind: CellKind;
  /** The source as last read (a SQL cell's model is the truth while it exists). */
  source: string;
  chart?: ChartSpec | null;
  view?: "grid" | "chart";
  /** The live execution whose results the cell shows. */
  executionId?: string;
  status: CellStatus;
  /** The run counter shown in the gutter ([3]). */
  runNumber?: number;
  /** A result read from the file (text), shown until the cell runs. */
  saved?: SavedOutput;
  /** Why a queued cell did not run ("an earlier cell failed", "cancelled"). */
  note?: string;
  /** A markdown cell shows its source for editing. */
  editing?: boolean;
}

export interface NotebookState {
  cells: CellState[];
  /** The cell with the keyboard (it holds the shared editor when it is a SQL cell). */
  focused?: string;
  /** Write results into the file (opt-in; results are user data). */
  saveOutputs: boolean;
  /** Shown under the toolbar: why the notebook opened without its connection, warnings from the file. */
  notices: string[];
  /** Cells left to run (the running one first). */
  queue: string[];
  running?: string;
  counter: number;
}

export function newCell(kind: CellKind, source = ""): CellState {
  return { id: newCellId(), kind, source, status: "idle", editing: kind === "markdown" && !source };
}

/** `cell` inserted after `afterId` (at the end when it is missing; at the start when null). */
export function insertCell(cells: readonly CellState[], cell: CellState, afterId?: string | null): CellState[] {
  if (afterId === null) return [cell, ...cells];
  const i = afterId === undefined ? -1 : cells.findIndex((c) => c.id === afterId);
  if (i < 0) return [...cells, cell];
  return [...cells.slice(0, i + 1), cell, ...cells.slice(i + 1)];
}

/** The cell moved up (-1) or down (+1); unchanged at either end. */
export function moveCell(cells: readonly CellState[], id: string, delta: -1 | 1): CellState[] {
  const i = cells.findIndex((c) => c.id === id);
  const j = i + delta;
  if (i < 0 || j < 0 || j >= cells.length) return cells as CellState[];
  const out = [...cells];
  [out[i], out[j]] = [out[j], out[i]];
  return out;
}

export function deleteCell(cells: readonly CellState[], id: string): CellState[] {
  return cells.filter((c) => c.id !== id);
}

/** The cell that takes focus after `id` is deleted: the next one, else the previous one. */
export function neighbourAfterDelete(cells: readonly CellState[], id: string): string | undefined {
  const i = cells.findIndex((c) => c.id === id);
  return cells[i + 1]?.id ?? cells[i - 1]?.id;
}

interface NotebookStore {
  docs: Record<string, NotebookState>;
  init(tabId: string, state: Partial<NotebookState> & { cells: CellState[] }): void;
  patch(tabId: string, p: Partial<NotebookState> | ((s: NotebookState) => Partial<NotebookState>)): void;
  patchCell(tabId: string, cellId: string, p: Partial<CellState>): void;
  remove(tabId: string): void;
}

export const useNotebooks = create<NotebookStore>((set) => ({
  docs: {},
  init(tabId, state) {
    set((s) => ({ docs: { ...s.docs, [tabId]: { saveOutputs: false, notices: [], queue: [], counter: 0, ...state } } }));
  },
  patch(tabId, p) {
    set((s) => {
      const d = s.docs[tabId];
      if (!d) return s;
      return { docs: { ...s.docs, [tabId]: { ...d, ...(typeof p === "function" ? p(d) : p) } } };
    });
  },
  patchCell(tabId, cellId, p) {
    set((s) => {
      const d = s.docs[tabId];
      if (!d || !d.cells.some((c) => c.id === cellId)) return s;
      return { docs: { ...s.docs, [tabId]: { ...d, cells: d.cells.map((c) => (c.id === cellId ? { ...c, ...p } : c)) } } };
    });
  },
  remove(tabId) {
    set((s) => {
      if (!s.docs[tabId]) return s;
      const { [tabId]: _, ...rest } = s.docs;
      return { docs: rest };
    });
  },
}));

export const notebookOf = (tabId: string): NotebookState | undefined => useNotebooks.getState().docs[tabId];

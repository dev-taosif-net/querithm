// Notebook runtime (Phase C3): cells and their Monaco models, running cells through the tab's one session (the same
// guarded path as a query tab), the run-all queue with cancel and stop-on-error, and the .sqlbook document. Loaded at
// start (it registers the shell's notebook hooks); the panel's React code loads with the first notebook shown.
import { executeOnTab, disposeExecution, newQueryTabBinding, trackExecution, workspaceHooks } from "../app/actions";
import { createModel, disposeModel, getModel, languageFor, setModelLanguage } from "../editor/editors";
import { monaco } from "../editor/monaco";
import { executionData } from "../results/model";
import { useExecutions, type ExecutionView } from "../store/executions";
import { useProfiles } from "../store/profiles";
import { useSettings } from "../store/settings";
import { ensureTabSnapshot } from "../store/snapshots";
import { newTabId, useTabs, type TabState } from "../store/tabs";
import { toast, useUi } from "../store/ui";
import { capOutput, serializeNotebook, type CellKind, type NotebookCellFile, type NotebookFile, type SavedOutput } from "./format";
import { notebookHooks } from "./hooks";
import { deleteCell, insertCell, moveCell, neighbourAfterDelete, newCell, notebookOf, useNotebooks, type CellState, type NotebookState } from "./store";

/** The Monaco model key of a SQL cell; the model is owned by the notebook tab (completion reads its connection). */
export const cellKey = (tabId: string, cellId: string) => `${tabId}::${cellId}`;
export const ownerOfKey = (key: string) => (key.includes("::") ? key.slice(0, key.indexOf("::")) : undefined);

const tab = (id: string): TabState | undefined => useTabs.getState().tabs[id];
const engineOf = (t: TabState | undefined) => useProfiles.getState().byId(t?.profileId)?.engine;

let untitled = 0;

/** The model of a SQL cell, created on first use with the cell's source. */
export function cellModel(tabId: string, cell: CellState): monaco.editor.ITextModel {
  return getModel(cellKey(tabId, cell.id)) ?? createModel(cellKey(tabId, cell.id), cell.source, languageFor(engineOf(tab(tabId))), tabId);
}

/** A cell's current source: its model's text while it has one. */
export function cellSource(tabId: string, cell: CellState): string {
  return cell.kind === "sql" ? (getModel(cellKey(tabId, cell.id))?.getValue() ?? cell.source) : cell.source;
}

/** Copies the models' text into the store (before saving or serializing). */
export function syncSources(tabId: string) {
  const doc = notebookOf(tabId);
  if (!doc) return;
  const cells = doc.cells.map((c) => {
    const s = cellSource(tabId, c);
    return s === c.source ? c : { ...c, source: s };
  });
  if (cells.some((c, i) => c !== doc.cells[i])) useNotebooks.getState().patch(tabId, { cells });
}

export function markDirty(tabId: string) {
  const t = tab(tabId);
  if (t && !t.dirty) useTabs.getState().update(tabId, { dirty: true });
}

export interface NewNotebookOptions {
  id?: string;
  title?: string;
  /** undefined = the active tab's connection; "" = none. */
  profileId?: string;
  database?: string;
  filePath?: string;
  cells?: NotebookCellFile[];
  saveOutputs?: boolean;
  notices?: string[];
  activate?: boolean;
  atEnd?: boolean;
  pinned?: boolean;
  dirty?: boolean;
}

/** Opens a notebook tab (a new one has one empty SQL cell). Returns its tab id. */
export function newNotebookTab(o: NewNotebookOptions = {}): string {
  const { profileId, database } = newQueryTabBinding(o.profileId, o.database);
  const id = o.id ?? newTabId();
  const cells: CellState[] = o.cells?.length
    ? o.cells.map((c) => ({ id: c.id, kind: c.kind, source: c.source, chart: c.chart ?? null, view: c.view, saved: c.output, status: "idle" }))
    : [newCell("sql")];
  useNotebooks.getState().init(id, { cells, focused: cells[0]?.id, saveOutputs: !!o.saveOutputs, notices: o.notices ?? [] });
  useTabs.getState().add({ id, title: o.title ?? `Notebook${++untitled}.sqlbook`, profileId, database, filePath: o.filePath, notebook: true, pinned: o.pinned, dirty: !!o.dirty });
  ensureTabSnapshot(tab(id));
  workspaceHooks.onTabAdded?.(id, o.activate !== false, !!o.atEnd);
  return id;
}

// ------------------------------------------------------------------------------------------------- editing

export function focusCell(tabId: string, cellId: string | undefined) {
  if (notebookOf(tabId)?.focused !== cellId) useNotebooks.getState().patch(tabId, { focused: cellId });
}

export function addCell(tabId: string, kind: CellKind, afterId?: string | null, source = ""): string | undefined {
  const doc = notebookOf(tabId);
  if (!doc) return;
  if (doc.cells.length >= 2_000) {
    toast("warn", "A notebook holds at most 2,000 cells.");
    return;
  }
  const cell = newCell(kind, source);
  useNotebooks.getState().patch(tabId, { cells: insertCell(doc.cells, cell, afterId ?? doc.focused), focused: cell.id });
  markDirty(tabId);
  return cell.id;
}

export function removeCell(tabId: string, cellId: string) {
  const doc = notebookOf(tabId);
  const cell = doc?.cells.find((c) => c.id === cellId);
  if (!doc || !cell) return;
  if (doc.running === cellId) {
    toast("warn", "Cancel the running cell before deleting it.");
    return;
  }
  const focus = doc.focused === cellId ? neighbourAfterDelete(doc.cells, cellId) : doc.focused;
  useNotebooks.getState().patch(tabId, { cells: deleteCell(doc.cells, cellId), focused: focus, queue: doc.queue.filter((q) => q !== cellId) });
  if (cell.executionId) disposeExecution(cell.executionId);
  disposeModel(cellKey(tabId, cellId));
  markDirty(tabId);
}

export function shiftCell(tabId: string, cellId: string, delta: -1 | 1) {
  const doc = notebookOf(tabId);
  if (!doc) return;
  const cells = moveCell(doc.cells, cellId, delta);
  if (cells === doc.cells) return;
  useNotebooks.getState().patch(tabId, { cells });
  markDirty(tabId);
}

/** SQL ↔ markdown: the text stays; a SQL cell's results go. */
export function setCellKind(tabId: string, cellId: string, kind: CellKind) {
  const doc = notebookOf(tabId);
  const cell = doc?.cells.find((c) => c.id === cellId);
  if (!doc || !cell || cell.kind === kind || doc.running === cellId) return;
  const source = cellSource(tabId, cell);
  if (cell.executionId) disposeExecution(cell.executionId);
  disposeModel(cellKey(tabId, cellId));
  useNotebooks.getState().patchCell(tabId, cellId, { kind, source, executionId: undefined, saved: undefined, status: "idle", note: undefined, runNumber: undefined, editing: kind === "markdown" });
  markDirty(tabId);
}

export function setCellSource(tabId: string, cellId: string, source: string) {
  const cell = notebookOf(tabId)?.cells.find((c) => c.id === cellId);
  if (!cell || cell.source === source) return;
  useNotebooks.getState().patchCell(tabId, cellId, { source });
  markDirty(tabId);
}

export function setCellChart(tabId: string, cellId: string, p: Partial<Pick<CellState, "chart" | "view">>) {
  const cell = notebookOf(tabId)?.cells.find((c) => c.id === cellId);
  if (!cell) return;
  if ((p.view === undefined || p.view === cell.view) && (p.chart === undefined || JSON.stringify(p.chart) === JSON.stringify(cell.chart))) return;
  useNotebooks.getState().patchCell(tabId, cellId, p);
  markDirty(tabId);
}

export function setSaveOutputs(tabId: string, on: boolean) {
  if (notebookOf(tabId)?.saveOutputs === on) return;
  useNotebooks.getState().patch(tabId, { saveOutputs: on });
  markDirty(tabId);
}

// ------------------------------------------------------------------------------------------------- running

/** A run that the next cell should not follow: failed, errors in its messages, or cancelled. */
export function cellFailed(v: Pick<ExecutionView, "state" | "error" | "messages"> | undefined): boolean {
  if (!v) return true;
  return v.state === "failed" || v.state === "cancelled" || !!v.error || v.messages.some((m) => m.severity === "error");
}

const TERMINAL = new Set(["done", "failed", "cancelled"]);

/** The execution's view once it has ended (undefined if it was dropped). */
function whenDone(executionId: string): Promise<ExecutionView | undefined> {
  return new Promise((resolve) => {
    const check = () => {
      const v = useExecutions.getState().views[executionId];
      if (!v || TERMINAL.has(v.state)) {
        off();
        resolve(v);
        return true;
      }
      return false;
    };
    const off = useExecutions.subscribe(() => void check());
    check();
  });
}

/** Error markers on the cell's model, from the server's messages (lines are the cell's own). */
function markCell(model: monaco.editor.ITextModel | undefined, v: ExecutionView | undefined) {
  if (!model || model.isDisposed()) return;
  const markers = (v?.messages ?? [])
    .filter((m) => m.severity === "error" && m.editorLine && m.editorLine <= model.getLineCount())
    .map((m) => ({
      startLineNumber: m.editorLine!, endLineNumber: m.editorLine!, startColumn: 1, endColumn: model.getLineMaxColumn(m.editorLine!),
      message: m.message, severity: monaco.MarkerSeverity.Error, source: "server",
    }));
  monaco.editor.setModelMarkers(model, "execution", markers);
}

/** Notebooks with a Cancel pending between cells. */
const cancelled = new Set<string>();

/**
 * Runs SQL cells in order on the notebook's session (Phase C3). Each cell is one execution through `executeOnTab`
 * (Prod and destructive guards per cell, enforced by the sidecar). The queue stops on Cancel, on a cell the user did not
 * let run, and — with the "stop at the first failing cell" setting — on a failed cell; the cells it did not reach say why.
 */
export async function runCells(tabId: string, ids: string[]): Promise<void> {
  const doc = notebookOf(tabId);
  const t = tab(tabId);
  if (!doc || !t) return;
  if (doc.running) {
    toast("info", "Cells are already running; Cancel stops them.");
    return;
  }
  if (!useProfiles.getState().byId(t.profileId)) {
    useUi.getState().setPalette({ mode: "connections" });
    toast("info", "Pick a connection for this notebook first.");
    return;
  }
  const byId = new Map(doc.cells.map((c) => [c.id, c]));
  const todo = ids.filter((id) => {
    const c = byId.get(id);
    return c?.kind === "sql" && cellSource(tabId, c).trim();
  });
  if (!todo.length) return;
  cancelled.delete(tabId);
  const store = useNotebooks.getState();
  store.patch(tabId, (d) => ({ queue: todo, cells: d.cells.map((c) => (todo.includes(c.id) ? { ...c, status: "queued", note: undefined } : c)) }));
  const notRun = (from: number, note: string) => {
    for (const id of todo.slice(from)) store.patchCell(tabId, id, { status: "idle", note });
  };
  const position = (id: string) => (notebookOf(tabId)?.cells.findIndex((c) => c.id === id) ?? -1) + 1;
  try {
    for (let i = 0; i < todo.length; i++) {
      const id = todo[i];
      if (cancelled.has(tabId)) {
        notRun(i, "Not run: cancelled.");
        break;
      }
      const cell = notebookOf(tabId)?.cells.find((c) => c.id === id);
      if (!cell || !tab(tabId)) continue; // deleted while queued
      const sql = cellSource(tabId, cell);
      store.patch(tabId, { running: id, queue: todo.slice(i) });
      store.patchCell(tabId, id, { status: "running", note: undefined });
      const model = getModel(cellKey(tabId, id));
      // A refusal (ATTACH on SQLite, a lost in-memory database) is the cell's own reason, shown on the cell.
      let refused: string | undefined;
      const run = await executeOnTab(tabId, sql, 1, undefined, () => model && monaco.editor.setModelMarkers(model, "execution", []), (why) => (refused = why));
      if (!run) {
        store.patchCell(tabId, id, refused ? { status: "failed", note: `Refused: ${refused}` } : { status: "idle", note: "Not run." });
        notRun(i + 1, `Not run: cell ${position(id)} did not run.`);
        break;
      }
      const prev = notebookOf(tabId)?.cells.find((c) => c.id === id)?.executionId;
      if (prev) disposeExecution(prev);
      trackExecution(tabId, run.sessionId, run.executionId, sql, 1, () => {
        const counter = (notebookOf(tabId)?.counter ?? 0) + 1;
        store.patch(tabId, { counter });
        store.patchCell(tabId, id, { executionId: run.executionId, status: "running", runNumber: counter, saved: undefined });
        // The status bar's timer and row count follow the running cell.
        useTabs.getState().update(tabId, { activeExecutionId: run.executionId, busy: true });
      }, run.notes);
      const v = await whenDone(run.executionId);
      const failed = cellFailed(v);
      store.patchCell(tabId, id, { status: v?.state === "cancelled" ? "cancelled" : failed ? "failed" : "done" });
      markCell(model, v);
      if (v?.state === "cancelled" || cancelled.has(tabId)) {
        notRun(i + 1, "Not run: cancelled.");
        break;
      }
      if (failed && useSettings.getState().notebookStopOnError) {
        notRun(i + 1, `Not run: cell ${position(id)} failed.`);
        break;
      }
    }
  } finally {
    cancelled.delete(tabId);
    if (notebookOf(tabId)) store.patch(tabId, { running: undefined, queue: [] });
  }
}

/** Every SQL cell, top to bottom (Shift+F5); text cells being edited show their formatted text. */
export function runAll(tabId: string) {
  const doc = notebookOf(tabId);
  if (doc?.cells.some((c) => c.kind === "markdown" && c.editing && c.source.trim())) {
    useNotebooks.getState().patch(tabId, (d) => ({ cells: d.cells.map((c) => (c.kind === "markdown" && c.editing && c.source.trim() ? { ...c, editing: false } : c)) }));
  }
  return runCells(tabId, notebookOf(tabId)?.cells.map((c) => c.id) ?? []);
}

/** Ctrl+Enter: the cell; `advance` (Shift+Enter) moves focus to the next cell. A markdown cell is rendered. */
export async function runCell(tabId: string, cellId: string, advance = false) {
  const doc = notebookOf(tabId);
  const cell = doc?.cells.find((c) => c.id === cellId);
  if (!doc || !cell) return;
  if (advance) {
    const i = doc.cells.findIndex((c) => c.id === cellId);
    focusCell(tabId, doc.cells[i + 1]?.id ?? cellId);
  }
  if (cell.kind === "markdown") {
    useNotebooks.getState().patchCell(tabId, cellId, { editing: false });
    return;
  }
  await runCells(tabId, [cellId]);
}

export function cancelNotebook(tabId: string) {
  if (notebookOf(tabId)?.running) cancelled.add(tabId);
}

// ------------------------------------------------------------------------------------------------- document

/** A cell's live result as a saved output (first result set, as text, cut to the file limits). */
function liveOutput(cell: CellState): SavedOutput | undefined {
  const rs = cell.executionId ? executionData.get(cell.executionId)?.resultSets[0] : undefined;
  if (!rs?.columns.length) return cell.saved;
  const cols = rs.columns.slice(0, 50);
  const rows: (string | null)[][] = [];
  for (let r = 0; r < Math.min(rs.rowCount, 1_000); r++) rows.push(cols.map((_, c) => rs.textPhysical(r, c)));
  return capOutput(cols.map((c) => c.name), rows, rs.rowCount);
}

/** The notebook as a document; outputs only when the notebook saves them. */
export function notebookFile(tabId: string): Omit<NotebookFile, "format" | "version"> | undefined {
  const doc = notebookOf(tabId);
  const t = tab(tabId);
  if (!doc || !t) return;
  return {
    connection: t.profileId ? { profileId: t.profileId, database: t.database ?? null } : null,
    cells: doc.cells.map((c) => ({
      id: c.id, kind: c.kind, source: cellSource(tabId, c), chart: c.chart ?? undefined, view: c.view,
      output: doc.saveOutputs && c.kind === "sql" ? liveOutput(c) : undefined,
    })),
  };
}

export function serializeTab(tabId: string, outputs?: boolean): string | undefined {
  const f = notebookFile(tabId);
  return f && serializeNotebook(f, outputs ?? notebookOf(tabId)?.saveOutputs ?? false);
}

/** The tab is closing: its cells' models and results go. */
export function disposeNotebook(tabId: string) {
  const doc = notebookOf(tabId);
  if (!doc) return;
  cancelled.delete(tabId);
  for (const c of doc.cells) {
    if (c.executionId) disposeExecution(c.executionId);
    disposeModel(cellKey(tabId, c.id));
  }
  useNotebooks.getState().remove(tabId);
}

// ------------------------------------------------------------------------------------------------- hooks

notebookHooks.run = async (tabId) => {
  const doc = notebookOf(tabId);
  if (!doc) return;
  if (doc.focused && doc.cells.some((c) => c.id === doc.focused)) await runCell(tabId, doc.focused);
  else await runAll(tabId);
};
notebookHooks.cancel = cancelNotebook;
notebookHooks.text = (tabId) => notebookOf(tabId)?.cells.map((c) => cellSource(tabId, c)).join("\n\n") ?? "";
notebookHooks.serialize = (tabId) => serializeTab(tabId, false) ?? "";
notebookHooks.dispose = disposeNotebook;
// Phase D0d: the in-memory database the cells ran on is gone; their results go with it and each cell says why.
notebookHooks.outputsLost = (tabId, note) => {
  const doc = notebookOf(tabId);
  if (!doc) return;
  for (const c of doc.cells) if (c.executionId) disposeExecution(c.executionId);
  useNotebooks.getState().patch(tabId, (d) => ({
    cells: d.cells.map((c) => (c.kind === "sql" && (c.executionId || c.status !== "idle") ? { ...c, executionId: undefined, status: "idle", note } : c)),
  }));
};

// A notebook moved to another engine: its cells' models switch language with it.
useTabs.subscribe((s, prev) => {
  for (const id of Object.keys(s.tabs)) {
    const t = s.tabs[id];
    if (!t.notebook || t.profileId === prev.tabs[id]?.profileId) continue;
    const lang = languageFor(engineOf(t));
    for (const c of notebookOf(id)?.cells ?? []) setModelLanguage(cellKey(id, c.id), lang);
  }
});

export type { NotebookState };

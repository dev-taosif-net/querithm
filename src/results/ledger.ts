// The statement ledger and the notice line of the bottom panel (§12 bottom panel). Pure functions over what the UI
// already holds for a run (the execution view and its Arrow result sets): no contract change, no React.
import type { ServerMessage } from "../ipc/contract";
import type { ExecutionView } from "../store/executions";
import type { TabState } from "../store/tabs";
import type { ExecutionData } from "./model";
import { errorsIn } from "./messageLog";

/** One chip of the ledger: a result set, or an error, in the order they happened. */
export type LedgerItem =
  | {
      kind: "set";
      key: string;
      resultIndex: number;
      rows: number;
      complete: boolean;
      truncated: boolean;
      interrupted: boolean;
      /** Rows minus the same result set's rows in the previous run of the same SQL; undefined when not comparable. */
      delta?: number;
    }
  | { kind: "error"; key: string; errorIndex: number; message: ServerMessage };

/** A notice line entry, most important first. */
export type Notice =
  | { kind: "older"; executionId: string; startedAt: number; position: number; count: number }
  | { kind: "error"; message: ServerMessage; count: number }
  | { kind: "limit"; resultIndex: number; rows: number };

const FINAL = new Set(["done", "failed", "cancelled"]);
export const isFinal = (view: Pick<ExecutionView, "state"> | undefined) => !!view && FINAL.has(view.state);

/** The run's errors in arrival order. Kept per message list and extended as messages arrive: read-only. */
export function runErrors(view: Pick<ExecutionView, "messages"> | undefined): readonly ServerMessage[] {
  return view ? errorsIn(view.messages).list : [];
}

/** The editor line of a message ("line 12"), or the server's batch line when the editor line is unknown. */
export function lineOf(m: ServerMessage): number | undefined {
  return m.editorLine ?? m.line ?? undefined;
}

/** The run kept just before `executionId` in the tab (the "previous run" deltas compare with). */
export function previousRun(tab: Pick<TabState, "executions">, executionId: string | undefined): string | undefined {
  if (!executionId) return undefined;
  const i = tab.executions.indexOf(executionId);
  return i > 0 ? tab.executions[i - 1] : undefined;
}

export const isLatestRun = (tab: Pick<TabState, "executions" | "activeExecutionId">) =>
  !tab.activeExecutionId || tab.executions[tab.executions.length - 1] === tab.activeExecutionId;

const sameSql = (a: string, b: string) => a.trim() === b.trim();

/**
 * The ledger of a run: its result sets and errors in arrival order. A result set is placed after the messages that
 * had arrived when it began (`view.setAfter`); without that record (a run the bench injected) sets come first.
 * A set's delta compares it with the same result index of the previous run, only when both ran the same SQL and both
 * sets are complete and untruncated — a delta from different text or a cut-off count would mislead.
 */
export function ledgerItems(
  view: ExecutionView | undefined,
  data: ExecutionData | undefined,
  prev?: { view: ExecutionView | undefined; data: ExecutionData | undefined },
): LedgerItem[] {
  if (!view) return [];
  const comparable = !!prev?.view && !!prev.data && sameSql(prev.view.sql, view.sql);
  const keyed: { at: number; item: LedgerItem }[] = [];
  for (const rs of data?.resultSets ?? []) {
    let delta: number | undefined;
    const before = comparable ? prev!.data!.byIndex(rs.index) : undefined;
    if (before && before.complete && !before.truncated && !before.interrupted && rs.complete && !rs.truncated && !rs.interrupted) delta = rs.rowCount - before.rowCount;
    const after = view.setAfter?.[rs.index];
    keyed.push({
      at: after == null ? -1 - 1 / (rs.index + 2) : after - 0.5 + rs.index / 1e6,
      item: { kind: "set", key: `s${rs.index}`, resultIndex: rs.index, rows: rs.rowCount, complete: rs.complete, truncated: rs.truncated, interrupted: rs.interrupted, delta },
    });
  }
  const errors = errorsIn(view.messages);
  errors.list.forEach((m, n) => keyed.push({ at: errors.at[n], item: { kind: "error", key: `e${n}`, errorIndex: n, message: m } }));
  return keyed.sort((a, b) => a.at - b.at).map((k) => k.item);
}

/** The ledger item the panel shows: the tab's chosen error, else its result set, else the first item. */
export function selectedItem(items: LedgerItem[], tab: Pick<TabState, "ledgerError" | "resultIndex">): LedgerItem | undefined {
  if (tab.ledgerError != null) {
    const e = items.find((i) => i.kind === "error" && i.errorIndex === tab.ledgerError);
    if (e) return e;
  }
  const want = tab.resultIndex ?? 0;
  return items.find((i) => i.kind === "set" && i.resultIndex === want) ?? items.find((i) => i.kind === "set") ?? items[0];
}

/**
 * The notice line's entries in priority order: an older run (everything on screen is from then), the run's first error,
 * a result set cut off by the row limit. Errors and the limit appear only once the run has ended, so the line is added
 * once and never moves the grid while rows stream or the user scrolls.
 */
export function noticesFor(
  tab: Pick<TabState, "executions" | "activeExecutionId">,
  view: ExecutionView | undefined,
  data: ExecutionData | undefined,
): Notice[] {
  if (!view) return [];
  const out: Notice[] = [];
  if (!isLatestRun(tab)) {
    out.push({ kind: "older", executionId: view.id, startedAt: view.startedAt, position: tab.executions.indexOf(view.id) + 1, count: tab.executions.length });
  }
  if (!isFinal(view)) return out;
  const errors = runErrors(view);
  if (errors.length) out.push({ kind: "error", message: errors[0], count: errors.length });
  const cut = data?.resultSets.find((rs) => rs.truncated);
  if (cut) out.push({ kind: "limit", resultIndex: cut.index, rows: cut.rowCount });
  return out;
}

/** Steps through the ledger (Alt+PageUp / Alt+PageDown): the tab patch that selects the item `delta` away. */
export function stepLedger(items: LedgerItem[], tab: Pick<TabState, "ledgerError" | "resultIndex">, delta: number): Partial<TabState> | undefined {
  if (!items.length) return undefined;
  const cur = selectedItem(items, tab);
  const i = cur ? items.indexOf(cur) : -1;
  const next = items[Math.min(items.length - 1, Math.max(0, i + delta))];
  if (!next || next === cur) return undefined;
  return selectPatch(next);
}

/** The tab patch that shows `item`. */
export function selectPatch(item: LedgerItem): Partial<TabState> {
  return item.kind === "set" ? { resultIndex: item.resultIndex, ledgerError: undefined } : { ledgerError: item.errorIndex };
}

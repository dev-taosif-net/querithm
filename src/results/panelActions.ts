// Keyboard actions of the bottom panel (§12 bottom panel): step through the ledger, jump between errors, compare.
import { useTabs } from "../store/tabs";
import { useExecutions } from "../store/executions";
import { toast } from "../store/ui";
import { editorForTab } from "../editor/editors";
import { setBottomTab } from "../app/actions";
import { executionData } from "./model";
import { ledgerItems, previousRun, runErrors, stepLedger } from "./ledger";
import { revealEditorLine } from "./reveal";

function itemsOf(tabId: string) {
  const t = useTabs.getState().tabs[tabId];
  if (!t?.activeExecutionId) return { t, items: [] };
  const views = useExecutions.getState().views;
  const prev = previousRun(t, t.activeExecutionId);
  const items = ledgerItems(views[t.activeExecutionId], executionData.get(t.activeExecutionId), prev ? { view: views[prev], data: executionData.get(prev) } : undefined);
  return { t, items };
}

/** Alt+PageDown / Alt+PageUp (focus in the panel): the next or previous result set or error of the shown run. */
export function stepOutput(tabId: string, delta: 1 | -1) {
  const { t, items } = itemsOf(tabId);
  if (!t) return;
  const patch = stepLedger(items, t, delta);
  if (patch) useTabs.getState().update(tabId, patch);
}

/**
 * F4 / Shift+F4: the editor moves to the next (previous) error line of the shown run after (before) the cursor,
 * wrapping around, and the ledger shows that error. A run without errors says so.
 */
export function stepError(tabId: string, delta: 1 | -1) {
  const t = useTabs.getState().tabs[tabId];
  const view = t?.activeExecutionId ? useExecutions.getState().views[t.activeExecutionId] : undefined;
  const errors = runErrors(view).map((m, i) => ({ m, i })).filter((e) => e.m.editorLine);
  if (!t || !errors.length) {
    toast("info", "The shown run has no errors with an editor line.");
    return;
  }
  const at = editorForTab(tabId)?.getPosition()?.lineNumber ?? 0;
  const sorted = [...errors].sort((a, b) => a.m.editorLine! - b.m.editorLine!);
  const next = delta > 0 ? (sorted.find((e) => e.m.editorLine! > at) ?? sorted[0]) : ([...sorted].reverse().find((e) => e.m.editorLine! < at) ?? sorted[sorted.length - 1]);
  useTabs.getState().update(tabId, { ledgerError: next.i, bottomOpen: true });
  revealEditorLine(tabId, next.m.editorLine, next.m.editorColumn);
}

/**
 * Compare (palette): the shown result set beside the next one of the same run, else beside the same result set of the
 * previous run. Says why when there is nothing to compare with.
 */
export function compareNext(tabId: string) {
  const t = useTabs.getState().tabs[tabId];
  const id = t?.activeExecutionId;
  if (!t || !id) return toast("info", "Run a query first.");
  const sets = executionData.get(id)?.resultSets ?? [];
  const shown = t.resultIndex ?? 0;
  const other = sets.find((s) => s.index !== shown);
  const prev = previousRun(t, id);
  const target = other ? { executionId: id, resultIndex: other.index } : prev && executionData.get(prev)?.byIndex(shown) ? { executionId: prev, resultIndex: shown } : undefined;
  if (!target) return toast("info", "Nothing to compare with: run a query that returns two result sets, or run this one again.");
  useTabs.getState().update(tabId, { compare: target, ledgerError: undefined });
  setBottomTab(tabId, "results");
}

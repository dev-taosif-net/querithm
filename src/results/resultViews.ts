// Grid or chart per result set (Phase C1): Ctrl+1 / Ctrl+2 and the Results toolbar's toggle. The choice and the
// chart spec live in the tab, keyed by result index, so re-running the query keeps the chart.
import type { ChartSpec } from "../charts/chartSpec";
import { validateSpec } from "../charts/chartSpec";
import { useTabs, type ResultChart, type TabState } from "../store/tabs";
import { executionData } from "./model";

export function setResultChart(tabId: string, resultIndex: number, patch: Partial<ResultChart>) {
  const t = useTabs.getState().tabs[tabId];
  if (!t) return;
  const cur: ResultChart = t.charts?.[resultIndex] ?? { view: "grid", spec: null };
  const next = { ...cur, ...patch };
  if (cur.view === next.view && JSON.stringify(cur.spec) === JSON.stringify(next.spec)) return;
  useTabs.getState().update(tabId, { charts: { ...t.charts, [resultIndex]: next } });
}

/** The result index of the set the Results panel shows, if any. */
export function shownResultIndex(t: TabState): number | undefined {
  const sets = t.activeExecutionId ? executionData.get(t.activeExecutionId)?.resultSets : undefined;
  if (!sets?.length) return undefined;
  return sets[Math.min(t.resultIndex ?? 0, sets.length - 1)].index;
}

/** Ctrl+1 / Ctrl+2: the shown result set as a grid or a chart; the Results panel opens if it was hidden. */
export function showResultAs(tabId: string, view: ResultChart["view"]) {
  const t = useTabs.getState().tabs[tabId];
  if (!t) return;
  const index = shownResultIndex(t);
  if (index === undefined) return;
  setResultChart(tabId, index, { view });
  useTabs.getState().update(tabId, { bottomTab: "results", bottomOpen: true });
}

export function setChartSpec(tabId: string, resultIndex: number, spec: ChartSpec) {
  setResultChart(tabId, resultIndex, { spec });
}

/** Charts read back from the workspace file: untrusted, so every spec is validated and bad entries dropped. */
export function cleanCharts(raw: unknown): Record<number, ResultChart> | undefined {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return undefined;
  const out: Record<number, ResultChart> = {};
  for (const [k, v] of Object.entries(raw).slice(0, 64)) {
    const i = Number(k);
    if (!Number.isInteger(i) || i < 0 || i > 10_000 || !v || typeof v !== "object") continue;
    const r = v as Record<string, unknown>;
    out[i] = { view: r.view === "chart" ? "chart" : "grid", spec: validateSpec(r.spec) };
  }
  return Object.keys(out).length ? out : undefined;
}

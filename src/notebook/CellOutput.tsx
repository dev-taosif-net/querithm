// A SQL cell's output (Phase C3/C4): what the run did (rows, time, errors, messages), then its result as a compact grid
// or a chart. A result read from the file is shown as a plain table until the cell runs again.
import { lazy, memo, Suspense, useCallback, useState, useSyncExternalStore } from "react";
import { useExecutions } from "../store/executions";
import { useExecutionData } from "../results/ResultsPanel";
import type { ArrowResultSet } from "../results/model";
import type { ChartSpec } from "../charts/chartSpec";
import type { SavedOutput } from "./format";
import { setCellChart } from "./runtime";
import type { CellState } from "./store";
import type { Engine } from "../ipc/contract";

const ResultGrid = lazy(() => import("../results/ResultGrid").then((m) => ({ default: m.ResultGrid })));
const ChartView = lazy(() => import("../charts/ChartView").then((m) => ({ default: m.ChartView })));

const ROW_H = 24;
const GRID_MAX = 300;
export const CHART_HEIGHT = 260;
const SAVED_ROWS_SHOWN = 200;

const noSubscribe = () => () => {};
const zero = () => 0;

export const CellOutput = memo(function CellOutput({ tabId, cell, engine }: { tabId: string; cell: CellState; engine: Engine }) {
  if (cell.executionId) return <LiveOutput tabId={tabId} cell={cell} executionId={cell.executionId} engine={engine} />;
  if (cell.saved) return <SavedTable saved={cell.saved} />;
  return null;
});

function LiveOutput({ tabId, cell, executionId, engine }: { tabId: string; cell: CellState; executionId: string; engine: Engine }) {
  const view = useExecutions((s) => s.views[executionId]);
  const data = useExecutionData(executionId);
  const [selected, setSelected] = useState(0);
  const [allMessages, setAllMessages] = useState(false);
  const sets = data?.resultSets ?? [];
  const rs: ArrowResultSet | undefined = sets[Math.min(selected, Math.max(0, sets.length - 1))];
  useSyncExternalStore(rs?.subscribe ?? noSubscribe, rs?.getVersion ?? zero);
  const onSpec = useCallback((spec: ChartSpec) => setCellChart(tabId, cell.id, { chart: spec }), [tabId, cell.id]);
  if (!view) return null;
  const errors = view.messages.filter((m) => m.severity === "error");
  const others = view.messages.filter((m) => m.severity !== "error");
  const running = view.state === "queued" || view.state === "running" || view.state === "streaming";
  const rows = sets.reduce((n, s) => n + s.rowCount, 0);
  const summary = running
    ? view.state === "queued" ? "Queued — waiting for a free connection…" : sets.length ? `Running… ${rows.toLocaleString()} rows so far` : "Running…"
    : view.state === "cancelled" ? "Cancelled"
    : view.state === "failed" || view.error || errors.length ? "Failed"
    : sets.length ? `${sets.length > 1 ? `${sets.length} result sets · ` : ""}${rows.toLocaleString()} row${rows === 1 ? "" : "s"}`
    : view.affectedRows != null ? `${view.affectedRows.toLocaleString()} row(s) affected` : "Done, no result set";
  const failed = !running && (view.state === "failed" || !!view.error || errors.length > 0);
  const chartOn = cell.view === "chart" && !!rs;
  return (
    <div className="mt-1 border-t border-line pt-1 text-[11px]" data-q-cell-output>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 min-h-[22px]">
        <span className={failed ? "text-err font-semibold" : view.state === "cancelled" ? "text-warn" : "text-muted"} role={failed ? "alert" : undefined}>{summary}</span>
        {view.elapsedMs != null && !running && <span className="text-muted">{view.elapsedMs.toLocaleString()} ms</span>}
        {rs?.interrupted && <span className="text-warn">(incomplete — execution stopped)</span>}
        {rs?.truncated && <span className="text-warn">row limit reached</span>}
        {sets.length > 1 && (
          <span className="flex items-center gap-1" role="tablist" aria-label="Result sets">
            {sets.map((s, i) => (
              <button
                key={s.index}
                role="tab"
                aria-selected={i === selected}
                className={`h-[20px] px-1.5 rounded ${i === selected ? "bg-row-sel text-fg font-semibold" : "text-muted hover:bg-hover"}`}
                onClick={() => setSelected(i)}
              >
                #{s.index + 1} · {s.rowCount.toLocaleString()}
              </button>
            ))}
          </span>
        )}
        {rs && (
          <div className="ml-auto flex items-center rounded border border-line-strong overflow-hidden h-[20px]" role="radiogroup" aria-label="Show the result as">
            {(["grid", "chart"] as const).map((v) => (
              <button
                key={v}
                role="radio"
                aria-checked={(cell.view ?? "grid") === v}
                className={`h-full px-2 ${(cell.view ?? "grid") === v ? "bg-accent-solid text-white font-semibold" : "text-fg hover:bg-hover"}`}
                onClick={() => setCellChart(tabId, cell.id, { view: v })}
              >
                {v === "grid" ? "Grid" : "Chart"}
              </button>
            ))}
          </div>
        )}
      </div>
      {(errors.length > 0 || view.error) && (
        <div className="mt-1 space-y-0.5">
          {(errors.length ? errors.slice(0, 3).map((m) => m.message) : [view.error!.message]).map((m, i) => (
            <div key={i} className="text-err q-mono whitespace-pre-wrap break-words">{m}</div>
          ))}
        </div>
      )}
      {others.length > 0 && (
        <div className="mt-1">
          <button className="text-accent hover:underline" onClick={() => setAllMessages(!allMessages)} aria-expanded={allMessages}>
            {allMessages ? "Hide messages" : `Messages (${others.length.toLocaleString()})`}
          </button>
          {allMessages && (
            <div className="mt-1 max-h-40 overflow-y-auto q-mono text-muted space-y-0.5 pr-2">
              {others.slice(0, 500).map((m, i) => <div key={i} className="whitespace-pre-wrap break-words">{m.message}</div>)}
            </div>
          )}
        </div>
      )}
      {rs && rs.columns.length > 0 && (
        <div className="mt-1">
          <Suspense fallback={<div className="h-[60px]" />}>
            {chartOn ? (
              <div className="flex flex-col border border-line rounded" style={{ height: CHART_HEIGHT + 60 }}>
                <ChartView key={`${executionId}:${rs.index}`} rs={rs} spec={cell.chart} onSpecChange={onSpec} title={`cell-${cell.runNumber ?? 0}`} height={CHART_HEIGHT} />
              </div>
            ) : (
              <div className="relative border border-line rounded overflow-hidden" style={{ height: Math.min(GRID_MAX, (rs.visibleRows + 1) * ROW_H + 14) }}>
                <ResultGrid key={`${executionId}:${rs.index}`} rs={rs} executionId={executionId} engine={engine} />
              </div>
            )}
          </Suspense>
        </div>
      )}
    </div>
  );
}

/** A result saved in the file: text only, the first rows shown. */
function SavedTable({ saved }: { saved: SavedOutput }) {
  const shown = saved.rows.slice(0, SAVED_ROWS_SHOWN);
  return (
    <div className="mt-1 border-t border-line pt-1 text-[11px]" data-q-cell-output>
      <div className="text-muted mb-1">
        Saved result · {saved.rowCount.toLocaleString()} row{saved.rowCount === 1 ? "" : "s"}
        {saved.rowCount > shown.length ? ` (showing ${shown.length.toLocaleString()})` : ""} — run the cell to refresh it
      </div>
      <div className="max-h-[300px] overflow-auto border border-line rounded">
        <table className="q-mono text-[11px] border-collapse">
          <thead className="sticky top-0 bg-panel-2">
            <tr>{saved.columns.map((c, i) => <th key={i} className="text-left font-semibold px-2 py-0.5 border-b border-line whitespace-nowrap">{c}</th>)}</tr>
          </thead>
          <tbody>
            {shown.map((r, i) => (
              <tr key={i} className="border-b border-line/50">
                {r.map((v, j) => <td key={j} className={`px-2 py-0.5 whitespace-nowrap max-w-[320px] truncate ${v == null ? "text-muted italic" : ""}`}>{v ?? "NULL"}</td>)}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

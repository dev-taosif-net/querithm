// Chart view of one result set (Phase C1): the spec controls, the plot and what the plot does not show (partial data,
// skipped NULLs, down-sampling, folded categories, precision). The data never passes through React: the worker
// aggregates the Arrow batches and only the computed chart (≤ 4 times a second while streaming) reaches state.
import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import type { ArrowResultSet } from "../results/model";
import { aggLabel, CHART_AGGS, CHART_KINDS, isChartable, isContinuous, isNumeric, kindLabel, MAX_SCATTER_SERIES, MAX_Y, mixedNote, reconcileSpec, specProblem, withKind, type ChartAgg, type ChartColumn, type ChartKind, type ChartSpec } from "./chartSpec";
import { chartCsv, MAX_CATEGORIES, pointsFor, type ChartData } from "./chartData";
import { ChartFeed, chartColumns, chartColumnsKey, wantedColumns } from "./chartFeed";
import { canvasPng, drawChart, renderChartImage, type DrawnChart } from "./render";
import { chartTheme } from "./theme";
import type { Hit } from "./draw/common";
import { useThemeKey } from "../app/tabStyle";
import { useDismiss } from "../app/dismiss";
import { DropMenu } from "../app/ToolbarMenu";
import { saveBytes } from "../app/saveBytes";
import { toast } from "../store/ui";

/** Paint times of the chart view (bench metric 34 reads them through window.__bench). */
export const chartTimings = { painted: [] as number[], listeners: new Set<() => void>() };

/** How the computation went: what the status line reports. */
interface Computed {
  data: ChartData;
  workerMs: number;
}

export interface ChartViewProps {
  rs: ArrowResultSet;
  /** The saved spec (null = infer from the columns). */
  spec: ChartSpec | null | undefined;
  onSpecChange: (spec: ChartSpec) => void;
  /** Names the exported files and the image title. */
  title: string;
  /** Notebook cells: a fixed plot height. */
  height?: number;
}

/** Keeps the computed chart for a result set and spec, recomputing in the worker as batches arrive. */
function useChartData(rs: ArrowResultSet, columns: readonly ChartColumn[], spec: ChartSpec | null, maxPoints: number | undefined): { computed: Computed | null; error: string | null; pending: boolean } {
  const [computed, setComputed] = useState<Computed | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(true);
  const feed = useRef<ChartFeed | null>(null);
  const specRef = useRef(spec);
  specRef.current = spec;
  const pointsRef = useRef(maxPoints);
  pointsRef.current = maxPoints;
  const wantedKey = spec ? JSON.stringify(wantedColumns(spec, columns)) : "";
  useEffect(() => {
    if (!spec || !wantedKey) return;
    setPending(true);
    setError(null);
    const f = new ChartFeed(
      rs,
      JSON.parse(wantedKey),
      (data, workerMs) => {
        setComputed({ data, workerMs });
        setPending(false);
        setError(null);
      },
      (message) => {
        setError(message);
        setPending(false);
      },
    );
    feed.current = f;
    let timer: ReturnType<typeof setTimeout> | undefined;
    // Nothing is computed before the plot's width is known (it sets the points per line).
    const run = () => {
      if (specRef.current && pointsRef.current) void f.update(specRef.current, pointsRef.current);
    };
    run();
    // While rows stream in, recompute at most every 250 ms; the end of the result set triggers the final pass now.
    const off = rs.subscribe(() => {
      if (rs.complete) {
        clearTimeout(timer);
        timer = undefined;
        run();
      } else if (!timer) {
        timer = setTimeout(() => {
          timer = undefined;
          run();
        }, 250);
      }
    });
    return () => {
      off();
      clearTimeout(timer);
      f.dispose();
      if (feed.current === f) feed.current = null;
    };
    // The spec's other fields (kind, aggregation, bins) recompute below without re-sending the batches.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rs, wantedKey]);
  const specKey = spec ? JSON.stringify(spec) : "";
  useEffect(() => {
    if (spec && maxPoints && feed.current) void feed.current.update(spec, maxPoints);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [specKey, maxPoints]);
  return { computed: spec ? computed : null, error, pending: !!spec && pending };
}

/** The tooltip, written imperatively (hover never re-renders React). Text only: names are user data. */
function showTip(el: HTMLDivElement | null, hit: Hit | null, left: number, top: number, box: HTMLElement | null) {
  if (!el) return;
  if (!hit) {
    el.hidden = true;
    return;
  }
  const rows = hit.rows.slice(0, 10).map((r) => {
    const row = document.createElement("div");
    row.className = "flex items-center gap-2";
    const sw = document.createElement("span");
    sw.className = "inline-block w-2.5 h-2.5 rounded-full shrink-0";
    sw.style.background = r.color;
    const label = document.createElement("span");
    label.className = "text-muted truncate max-w-[180px]";
    label.textContent = r.label;
    const value = document.createElement("span");
    value.className = "ml-auto pl-3 text-fg q-mono";
    value.textContent = r.value;
    row.append(sw, label, value);
    return row;
  });
  const title = document.createElement("div");
  title.className = "font-semibold text-fg mb-0.5 truncate max-w-[260px]";
  title.textContent = hit.title;
  el.replaceChildren(title, ...rows);
  el.hidden = false;
  const w = el.offsetWidth;
  const maxLeft = (box?.clientWidth ?? 0) + (box?.scrollLeft ?? 0) - w - 4;
  el.style.left = `${Math.max(4, Math.min(left + 14, maxLeft))}px`;
  el.style.top = `${Math.max(4, top + 14)}px`;
}

function notes(rs: ArrowResultSet, spec: ChartSpec, data: ChartData | undefined): { text: string; warn?: boolean }[] {
  const out: { text: string; warn?: boolean }[] = [];
  if (rs.interrupted) out.push({ text: "Partial data (incomplete — execution stopped)", warn: true });
  else if (!rs.complete) out.push({ text: `Streaming… ${rs.rowCount.toLocaleString()} rows so far` });
  if (rs.truncated) out.push({ text: `Row limit reached: the chart shows the first ${rs.rowCount.toLocaleString()} rows`, warn: true });
  if (!data) return out;
  const m = data.meta;
  if (m.lossy) out.push({ text: "Precision: some values are beyond 2^53 and are drawn approximately", warn: true });
  if (m.nullsSkipped) out.push({ text: `${m.nullsSkipped.toLocaleString()} row${m.nullsSkipped === 1 ? "" : "s"} skipped (NULL)` });
  if (m.sampled) out.push({ text: spec.kind === "scatter" ? `Sampled to ${m.sampled.toLocaleString()} points per series` : `Down-sampled to ${m.sampled.toLocaleString()} points per series (extremes kept)` });
  if (m.foldedCategories) out.push({ text: data.shape === "category" && data.kind === "pie" ? `${m.foldedCategories} smaller slices in Other` : `Top ${MAX_CATEGORIES} categories; ${m.foldedCategories.toLocaleString()} more in Other` });
  if (m.foldedSeries) out.push({ text: spec.kind === "bar" ? `${m.foldedSeries} smaller series in Other` : `${m.foldedSeries} more series not drawn` });
  if (m.duplicates) out.push({ text: `${m.duplicates.toLocaleString()} repeated categories show their first value; choose an aggregation`, warn: true });
  if (m.negativeSkipped) out.push({ text: `${m.negativeSkipped} zero or negative values left out` });
  if (spec.series && spec.y.length > 1 && spec.kind !== "histogram" && spec.kind !== "pie") out.push({ text: `Split by ${spec.series}: only ${spec.y[0]} is plotted` });
  return out;
}

function describe(spec: ChartSpec, data: ChartData | undefined): string {
  const what = spec.kind === "histogram" ? `Histogram of ${spec.x}` : `${kindLabel[spec.kind]} chart of ${spec.y.length ? spec.y.join(", ") : "row count"} by ${spec.x}`;
  return data ? `${what}, ${data.meta.plotted.toLocaleString()} rows plotted. The Grid view has every value.` : what;
}

export const ChartView = memo(function ChartView({ rs, spec: saved, onSpecChange, title, height }: ChartViewProps) {
  // A dynamically typed column's kind follows the values that have arrived (a text row turns a number column mixed).
  const classesKey = useSyncExternalStore(rs.subscribe, () => chartColumnsKey(rs));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const columns = useMemo(() => chartColumns(rs), [rs, rs.columns, classesKey]);
  const { spec, inferred } = useMemo(() => reconcileSpec(saved, columns), [saved, columns]);
  const problem = spec ? specProblem(spec, columns) : "Nothing in this result can be charted: it has no number, date or text column.";
  const plotBox = useRef<HTMLDivElement>(null);
  const host = useRef<HTMLDivElement>(null);
  const tip = useRef<HTMLDivElement>(null);
  const drawn = useRef<DrawnChart | null>(null);
  const [size, setSize] = useState<{ w: number; h: number } | null>(null);
  const { computed, error, pending } = useChartData(rs, columns, problem ? null : spec, size && size.w > 0 ? pointsFor(size.w) : undefined);
  const themeKey = useThemeKey();

  // A saved spec that no longer fits is replaced by the inferred one, and that one is kept.
  useEffect(() => {
    if (spec && (inferred || JSON.stringify(spec) !== JSON.stringify(saved))) onSpecChange(spec);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [spec, inferred]);

  useLayoutEffect(() => {
    const el = plotBox.current;
    if (!el) return;
    let frame = 0;
    const measure = () => {
      frame = 0;
      setSize((s) => (s && s.w === el.clientWidth && s.h === el.clientHeight ? s : { w: el.clientWidth, h: el.clientHeight }));
    };
    measure();
    if (typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(() => {
      if (!frame) frame = requestAnimationFrame(measure);
    });
    ro.observe(el);
    return () => {
      ro.disconnect();
      cancelAnimationFrame(frame);
    };
  }, []);

  useEffect(() => {
    drawn.current?.destroy();
    drawn.current = null;
    if (!host.current || !computed || !size) return;
    const t = chartTheme();
    drawn.current = drawChart(host.current, computed.data, size.w, size.h, t, (hit, left, top) => showTip(tip.current, hit, left, top, plotBox.current));
    requestAnimationFrame(() => {
      chartTimings.painted.push(performance.now());
      chartTimings.listeners.forEach((l) => l());
    });
    return () => {
      drawn.current?.destroy();
      drawn.current = null;
    };
  }, [computed, size, themeKey]);

  const set = (patch: Partial<ChartSpec>) => spec && onSpecChange({ ...spec, ...patch });
  const exportAs = async (what: "png" | "csv") => {
    if (!computed || !spec) return;
    const base = `${title}-chart`;
    try {
      if (what === "csv") {
        await saveBytes(`${base}.csv`, new TextEncoder().encode(chartCsv(computed.data)), { name: "CSV", extension: "csv", mime: "text/csv" });
        return;
      }
      const img = renderChartImage(computed.data, describe(spec, undefined), Math.max(480, size?.w ?? 800), Math.max(320, Math.min(size?.h ?? 400, 900)), chartTheme());
      if (!img) return void toast("info", "There is nothing to export yet.");
      await saveBytes(`${base}.png`, await canvasPng(img), { name: "PNG image", extension: "png", mime: "image/png" });
    } catch (e) {
      toast("error", `Could not export the chart: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const data = computed?.data;
  const shown: { text: string; warn?: boolean }[] = spec && !problem ? notes(rs, spec, data) : rs.interrupted ? [{ text: "Partial data (incomplete — execution stopped)", warn: true }] : [];
  for (const c of columns) if (c.mixed) shown.push({ text: mixedNote(c) });
  return (
    <div className="flex-1 flex flex-col min-h-0 min-w-0" data-q-chart>
      {spec && <ChartControls spec={spec} columns={columns} onChange={onSpecChange} set={set} onExport={(w) => void exportAs(w)} canExport={!!data && data.shape !== "empty"} />}
      <div
        ref={plotBox}
        className="relative flex-1 min-h-0 overflow-y-auto overflow-x-hidden q-chart"
        style={height ? { height, flex: "none" } : undefined}
        role="img"
        aria-label={spec ? describe(spec, data) : "Chart"}
      >
        <div ref={host} className="absolute inset-x-0 top-0" />
        {(problem || error || (data?.shape === "empty") || (pending && !data)) && (
          <div className="absolute inset-0 flex items-center justify-center text-xs p-6 text-center pointer-events-none">
            <span className={error ? "text-err" : "text-muted"}>
              {problem ?? (error ? `The chart could not be computed: ${error}` : data?.shape === "empty" ? data.reason : "Computing the chart…")}
            </span>
          </div>
        )}
        <div ref={tip} hidden className="absolute z-20 pointer-events-none bg-raised border border-line rounded shadow-xl px-2 py-1.5 text-[11px] min-w-[120px]" />
      </div>
      {shown.length > 0 && (
        <div className="min-h-[22px] px-3 py-0.5 border-t border-line flex flex-wrap items-center gap-x-3 text-[11px] shrink-0" data-q-chart-notes>
          {shown.map((n) => (
            <span key={n.text} className={n.warn ? "text-warn" : "text-muted"}>{n.text}</span>
          ))}
        </div>
      )}
    </div>
  );
});

function ChartControls({ spec, columns, onChange, set, onExport, canExport }: {
  spec: ChartSpec; columns: ChartColumn[]; onChange: (s: ChartSpec) => void; set: (p: Partial<ChartSpec>) => void; onExport: (what: "png" | "csv") => void; canExport: boolean;
}) {
  const chartable = columns.filter(isChartable);
  const xChoices = spec.kind === "histogram" || spec.kind === "scatter" ? chartable.filter(isContinuous) : chartable;
  const numbers = columns.filter(isNumeric);
  const maxY = spec.kind === "pie" ? 1 : spec.kind === "scatter" ? MAX_SCATTER_SERIES : MAX_Y;
  return (
    <div className="min-h-[30px] py-1 px-2 border-b border-line flex flex-wrap items-center gap-x-2 gap-y-1 shrink-0 text-[11px]" data-q-chart-controls>
      <div className="flex items-center rounded border border-line-strong overflow-hidden" role="radiogroup" aria-label="Chart type">
        {CHART_KINDS.map((k: ChartKind) => (
          <button
            key={k}
            role="radio"
            aria-checked={spec.kind === k}
            className={`h-[22px] px-2 ${spec.kind === k ? "bg-accent-solid text-white font-semibold" : "text-fg hover:bg-hover"}`}
            onClick={() => spec.kind !== k && onChange(withKind(spec, k, columns))}
          >
            {kindLabel[k]}
          </button>
        ))}
      </div>
      <label className="flex items-center gap-1">
        <span className="text-muted">{spec.kind === "histogram" ? "Values" : spec.kind === "pie" ? "Slices" : "X"}</span>
        <select className="q-input h-[22px] py-0 max-w-[160px]" value={spec.x ?? ""} onChange={(e) => set({ x: e.target.value })}>
          {xChoices.map((c) => (
            <option key={c.name} value={c.name}>{c.name}</option>
          ))}
        </select>
      </label>
      {spec.kind !== "histogram" && (
        <ValuePicker spec={spec} numbers={numbers.filter((c) => c.name !== spec.x)} max={maxY} onChange={(y) => set({ y, agg: !y.length ? "count" : spec.agg === "count" && spec.y.length === 0 ? "sum" : spec.agg })} />
      )}
      {spec.kind !== "histogram" && (
        <label className="flex items-center gap-1">
          <span className="text-muted">Aggregate</span>
          <select className="q-input h-[22px] py-0" value={spec.agg} onChange={(e) => set({ agg: e.target.value as ChartAgg })}>
            {CHART_AGGS.filter((a) => !(a === "none" && spec.kind === "pie") && !(a !== "count" && !spec.y.length)).map((a) => (
              <option key={a} value={a}>{aggLabel[a]}</option>
            ))}
          </select>
        </label>
      )}
      {spec.kind !== "histogram" && spec.kind !== "pie" && (
        <label className="flex items-center gap-1">
          <span className="text-muted">Series</span>
          <select className="q-input h-[22px] py-0 max-w-[140px]" value={spec.series ?? ""} onChange={(e) => set({ series: e.target.value || null })}>
            <option value="">None</option>
            {chartable.filter((c) => c.name !== spec.x).map((c) => (
              <option key={c.name} value={c.name}>{c.name}</option>
            ))}
          </select>
        </label>
      )}
      {spec.kind === "histogram" && (
        <label className="flex items-center gap-1">
          <span className="text-muted">Bins</span>
          <input
            type="number"
            className="q-input h-[22px] py-0 w-14"
            min={1}
            max={200}
            defaultValue={spec.bins ?? 20}
            key={spec.bins ?? 20}
            onBlur={(e) => {
              const n = Math.round(Number(e.target.value));
              if (Number.isFinite(n) && n >= 1 && n <= 200) set({ bins: n });
              else e.target.value = String(spec.bins ?? 20);
            }}
            onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
          />
        </label>
      )}
      <ExportMenu disabled={!canExport} onExport={onExport} />
    </div>
  );
}

function ValuePicker({ spec, numbers, max, onChange }: { spec: ChartSpec; numbers: ChartColumn[]; max: number; onChange: (y: string[]) => void }) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useDismiss(open, [box], () => setOpen(false));
  const label = spec.y.length ? spec.y.join(", ") : "Row count";
  const toggle = (n: string) => {
    if (spec.y.includes(n)) onChange(spec.y.filter((x) => x !== n));
    else if (max === 1) onChange([n]);
    else if (spec.y.length < max) onChange([...spec.y, n]);
  };
  return (
    <div className="relative flex items-center gap-1" ref={box}>
      <span className="text-muted">{spec.kind === "pie" ? "Size" : "Y"}</span>
      <button className="q-btn h-[22px] max-w-[200px]" aria-haspopup="listbox" aria-expanded={open} onClick={() => setOpen(!open)} title={label}>
        <span className="truncate">{label}</span> <span className="text-muted text-[10px]">▾</span>
      </button>
      {open && (
        <div className="absolute left-0 top-7 z-30 bg-raised border border-line rounded shadow-xl w-56 max-h-72 overflow-y-auto py-1" role="listbox" aria-multiselectable={max > 1}>
          {numbers.map((c) => {
            const on = spec.y.includes(c.name);
            const full = !on && max > 1 && spec.y.length >= max;
            return (
              <label key={c.name} className={`flex items-center gap-2 px-3 py-0.5 ${full ? "text-muted" : "hover:bg-hover cursor-pointer"}`} title={full ? `At most ${max} value columns` : c.typeName}>
                <input type={max === 1 ? "radio" : "checkbox"} checked={on} disabled={full} onChange={() => toggle(c.name)} />
                <span className="truncate q-mono">{c.name}</span>
              </label>
            );
          })}
          <label className="flex items-center gap-2 px-3 py-0.5 hover:bg-hover cursor-pointer border-t border-line mt-1 pt-1">
            <input type={max === 1 ? "radio" : "checkbox"} checked={!spec.y.length} onChange={() => onChange([])} />
            <span>Row count</span>
          </label>
          {!numbers.length && <div className="px-3 py-1 text-muted">No number columns</div>}
        </div>
      )}
    </div>
  );
}

/** The app's menu (ToolbarMenu: ↑/↓/Home/End, Esc back to the button), not a list of its own (L32). */
function ExportMenu({ disabled, onExport }: { disabled: boolean; onExport: (what: "png" | "csv") => void }) {
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  return (
    <div className="relative ml-auto">
      <button ref={trigger} className="q-btn h-[22px] text-accent" disabled={disabled} onClick={() => setOpen(!open)} aria-haspopup="menu" aria-expanded={open}>Export chart ▾</button>
      {open && (
        <DropMenu
          anchor={trigger}
          label="Export chart"
          items={[{ label: "PNG image", run: () => onExport("png") }, { label: "Chart data (CSV)", run: () => onExport("csv") }]}
          onClose={() => setOpen(false)}
        />
      )}
    </div>
  );
}


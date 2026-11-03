// Chart spec (Phase C1): which columns a result-set chart plots and how. Columns are referenced by name, so a spec
// survives re-running the query; it is saved with the workspace and in notebook cells, never with the data.
import type { CellKind } from "../results/model";

export const CHART_KINDS = ["bar", "line", "area", "scatter", "pie", "histogram"] as const;
export type ChartKind = (typeof CHART_KINDS)[number];

export const CHART_AGGS = ["none", "sum", "count", "avg", "min", "max"] as const;
export type ChartAgg = (typeof CHART_AGGS)[number];

export interface ChartSpec {
  kind: ChartKind;
  /** Category / x column. */
  x: string | null;
  /** Value columns (numbers). Empty with `agg: "count"` counts rows. */
  y: string[];
  /** Splits the rows into one series per distinct value. */
  series?: string | null;
  agg: ChartAgg;
  /** Histogram bin count. */
  bins?: number;
}

/** What a chart needs to know about a result column. */
export interface ChartColumn {
  name: string;
  kind: CellKind;
  typeName: string;
  /** A dynamically typed column (SQLite) holding numbers and also text or blobs: not a number column. */
  mixed?: boolean;
}

/** At most this many value columns (one color each; the categorical palette has 8 slots). */
export const MAX_Y = 8;
/** Scatter plots keep their series apart only for the first 3 palette slots (all-pairs CVD check). */
export const MAX_SCATTER_SERIES = 3;
export const DEFAULT_BINS = 20;
export const MAX_BINS = 200;
const MAX_NAME = 256;

export const isNumeric = (c: ChartColumn) => c.kind === "number";
/** Columns that can sit on a continuous x axis. */
export const isContinuous = (c: ChartColumn) => c.kind === "number" || c.kind === "temporal";
/** Columns a chart can use at all (binary values have no position or label). */
export const isChartable = (c: ChartColumn) => c.kind !== "binary";

/** Names that look like row identifiers: a poor default for a value axis when other numbers exist. */
const looksLikeId = (name: string) => /(^|_)(id|key|no)$/i.test(name) || /^id_/i.test(name) || /[a-z]Id\d*$/.test(name);

/**
 * The chart a result set most likely wants (Phase C1): time + number → line, text + number → bar, two numbers →
 * scatter, one number → histogram, text only → bar of row counts. Null when nothing can be plotted.
 */
export function inferSpec(columns: readonly ChartColumn[]): ChartSpec | null {
  const usable = columns.filter(isChartable);
  const temporal = usable.filter((c) => c.kind === "temporal");
  const numbers = usable.filter(isNumeric);
  const values = numbers.filter((c) => !looksLikeId(c.name));
  const pick = (n: number, exclude?: string) => {
    const pool = (values.length ? values : numbers).filter((c) => c.name !== exclude);
    return pool.slice(0, n).map((c) => c.name);
  };
  const categories = usable.filter((c) => c.kind === "text" || c.kind === "bool");
  if (temporal.length && numbers.length) return { kind: "line", x: temporal[0].name, y: pick(2), series: null, agg: "none" };
  if (categories.length && numbers.length) return { kind: "bar", x: categories[0].name, y: pick(1), series: null, agg: "sum" };
  if (numbers.length >= 2) {
    const x = (values.length >= 2 ? values : numbers)[0].name;
    return { kind: "scatter", x, y: pick(1, x), series: null, agg: "none" };
  }
  if (numbers.length === 1) return { kind: "histogram", x: numbers[0].name, y: [], series: null, agg: "none", bins: DEFAULT_BINS };
  if (temporal.length) return { kind: "histogram", x: temporal[0].name, y: [], series: null, agg: "none", bins: DEFAULT_BINS };
  if (categories.length) return { kind: "bar", x: categories[0].name, y: [], series: null, agg: "count" };
  return null;
}

/** Why a dynamically typed column with numbers in it is still not offered as numbers. */
export const mixedNote = (c: Pick<ChartColumn, "name">) => `Mixed value types: ${c.name} is not numeric in every row`;

const name = (v: unknown): string | null => (typeof v === "string" && v.length > 0 && v.length <= MAX_NAME ? v : null);

/** A spec read from an untrusted place (workspace file, notebook file): the cleaned spec, or null when unusable. */
export function validateSpec(raw: unknown): ChartSpec | null {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return null;
  const r = raw as Record<string, unknown>;
  if (!CHART_KINDS.includes(r.kind as ChartKind)) return null;
  const agg = CHART_AGGS.includes(r.agg as ChartAgg) ? (r.agg as ChartAgg) : "none";
  const x = r.x == null ? null : name(r.x);
  if (r.x != null && x == null) return null;
  if (r.y != null && !Array.isArray(r.y)) return null;
  const y = Array.isArray(r.y) ? r.y.map(name) : [];
  if (y.some((v) => v == null) || y.length > MAX_Y) return null;
  const series = r.series == null ? null : name(r.series);
  if (r.series != null && series == null) return null;
  const spec: ChartSpec = { kind: r.kind as ChartKind, x, y: [...new Set(y as string[])], series, agg };
  if (r.bins != null) {
    if (typeof r.bins !== "number" || !Number.isInteger(r.bins) || r.bins < 1 || r.bins > MAX_BINS) return null;
    spec.bins = r.bins;
  }
  return spec;
}

/** Why a spec cannot be drawn on these columns, or null when it can. */
export function specProblem(spec: ChartSpec, columns: readonly ChartColumn[]): string | null {
  const col = (n: string | null | undefined) => (n ? columns.find((c) => c.name === n) : undefined);
  const x = col(spec.x);
  if (!x) return "Choose a column for the x axis.";
  if (!isChartable(x)) return `${x.name} holds binary values, which cannot be plotted.`;
  const ys = spec.y.map(col);
  if (ys.some((c) => !c)) return "A value column is no longer in the result.";
  const mixedY = ys.find((c) => c?.mixed);
  if (mixedY) return `${mixedNote(mixedY)}.`;
  if (ys.some((c) => c && !isNumeric(c))) return "Value columns must hold numbers.";
  if (spec.series && !col(spec.series)) return "The series column is no longer in the result.";
  const mixedX = x.mixed ? `${mixedNote(x)}.` : null;
  if (spec.kind === "histogram") return isContinuous(x) ? null : mixedX ?? "A histogram needs a number or date column.";
  if (!ys.length && spec.agg !== "count") return "Choose a value column, or count rows.";
  if (spec.kind === "scatter" && !isContinuous(x)) return mixedX ?? "A scatter plot needs a number or date column on the x axis.";
  return null;
}

/**
 * The spec for these columns: the saved one when it still fits (columns it names are dropped when gone), else the
 * inferred one. `inferred` tells the caller the saved spec no longer applied.
 */
export function reconcileSpec(spec: ChartSpec | null | undefined, columns: readonly ChartColumn[]): { spec: ChartSpec | null; inferred: boolean } {
  if (spec) {
    const has = (n: string) => columns.some((c) => c.name === n);
    const kept: ChartSpec = { ...spec, y: spec.y.filter(has), series: spec.series && has(spec.series) ? spec.series : null };
    if (!specProblem(kept, columns)) return { spec: kept, inferred: false };
  }
  return { spec: inferSpec(columns), inferred: true };
}

/** `spec` switched to `kind`, its columns adjusted to what that kind can draw (x axis type, number of values). */
export function withKind(spec: ChartSpec, kind: ChartKind, columns: readonly ChartColumn[]): ChartSpec {
  const col = (n: string | null | undefined) => columns.find((c) => c.name === n);
  const firstContinuous = columns.find((c) => isContinuous(c) && c.name !== spec.y[0]) ?? columns.find(isContinuous);
  let x = spec.x;
  if ((kind === "histogram" || kind === "scatter") && !(col(x) && isContinuous(col(x)!))) x = firstContinuous?.name ?? x;
  if (kind === "histogram") return { kind, x, y: spec.y, series: spec.series ?? null, agg: spec.agg, bins: spec.bins ?? DEFAULT_BINS };
  let y = spec.y.filter((n) => n !== x);
  if (kind === "pie") y = y.slice(0, 1);
  if (kind === "scatter") y = y.slice(0, MAX_SCATTER_SERIES);
  let agg = spec.agg;
  // A scatter plot has no row-count form: it needs a value column.
  if (kind === "scatter" && agg === "count") agg = "none";
  if (!y.length && agg !== "count") {
    const first = columns.find((c) => isNumeric(c) && c.name !== x);
    if (first) y = [first.name];
    else agg = "count";
  }
  return { kind, x, y, series: kind === "pie" ? null : spec.series ?? null, agg, bins: spec.bins };
}

/** The columns a spec reads, in a stable order (the worker extracts only these). */
export function specColumns(spec: ChartSpec): string[] {
  const out: string[] = [];
  for (const n of [spec.x, ...(spec.kind === "histogram" ? [] : spec.y), spec.kind === "histogram" ? null : spec.series]) if (n && !out.includes(n)) out.push(n);
  return out;
}

export const kindLabel: Record<ChartKind, string> = { bar: "Bar", line: "Line", area: "Area", scatter: "Scatter", pie: "Donut", histogram: "Histogram" };
export const aggLabel: Record<ChartAgg, string> = { none: "No aggregation", sum: "Sum", count: "Count", avg: "Average", min: "Minimum", max: "Maximum" };

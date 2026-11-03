// Chart data (Phase C1): aggregation, binning and down-sampling of result columns. Pure functions over typed arrays,
// run in chartData.worker.ts so a 1M-row result is never walked on the main thread.
import { DEFAULT_BINS, MAX_BINS, MAX_SCATTER_SERIES, type ChartAgg, type ChartSpec } from "./chartSpec";

/** x values: numbers (NaN = NULL; dates and timestamps as epoch ms) or labels (null = NULL). */
export type XInput =
  | { type: "num"; values: Float64Array; temporal: boolean }
  | { type: "cat"; values: (string | null)[] };

export interface ChartInput {
  rows: number;
  x: XInput;
  /** Value columns, NaN = NULL. */
  ys: { name: string; values: Float64Array }[];
  /** Series labels per row (null = NULL). */
  series?: (string | null)[];
  /** A value lost precision on the way to a double (bigint or decimal beyond 2^53). */
  lossy: boolean;
}

/** "date": temporal values that are all midnights; "category": labels, positions are their index. */
export type XKind = "number" | "time" | "date" | "category";

export interface ChartMeta {
  rows: number;
  /** Rows that contributed a point or a value. */
  plotted: number;
  /** Rows skipped because x, or every value, was NULL. */
  nullsSkipped: number;
  lossy: boolean;
  /** Points per series after down-sampling, or null when every point is drawn. */
  sampled: number | null;
  /** Categories folded into "Other". */
  foldedCategories: number;
  /** Series folded into "Other" (bars) or not drawn (lines). */
  foldedSeries: number;
  /** No aggregation: categories that appeared more than once (the first value is shown). */
  duplicates: number;
  /** Donut: categories left out because their value was zero or negative. */
  negativeSkipped: number;
}

export interface SeriesData {
  name: string;
  /** NaN = no value at this position. */
  values: Float64Array;
  /** The folded remainder ("Other"): drawn in a neutral color. */
  other?: boolean;
}

export type ChartData =
  | { shape: "xy"; kind: "line" | "area" | "scatter"; xKind: XKind; x: Float64Array; labels?: string[]; xName: string; series: SeriesData[]; meta: ChartMeta }
  | { shape: "category"; kind: "bar" | "pie"; xKind: XKind; xName: string; categories: string[]; series: SeriesData[]; meta: ChartMeta }
  | { shape: "histogram"; xKind: "number" | "time" | "date"; xName: string; edges: Float64Array; counts: Float64Array; meta: ChartMeta }
  | { shape: "empty"; reason: string; meta: ChartMeta };

export const MAX_POINTS = 10_000;
/** Fewest points per series a line keeps, however narrow the chart. */
export const MIN_POINTS = 512;

/**
 * Points per series for a plot `width` CSS pixels wide: 1.5 per pixel, between 512 and 10,000. More points than
 * pixels draw nothing new, and a stroked polyline's raster cost grows with its segments (measured in headless Chrome:
 * two 20,000-point lines held a frame for 433 ms, two 2,800-point lines for 17–50 ms).
 */
export function pointsFor(width: number): number {
  return Math.max(MIN_POINTS, Math.min(MAX_POINTS, Math.ceil((width * 1.5) / 256) * 256));
}
export const MAX_CATEGORIES = 200;
export const MAX_SERIES = 8;
export const MAX_SLICES = 8;
export const OTHER = "Other";

const DAY = 86_400_000;

export function emptyMeta(rows = 0, lossy = false): ChartMeta {
  return { rows, plotted: 0, nullsSkipped: 0, lossy, sampled: null, foldedCategories: 0, foldedSeries: 0, duplicates: 0, negativeSkipped: 0 };
}

function xKindOf(x: XInput, positions?: ArrayLike<number>): XKind {
  if (x.type === "cat") return "category";
  if (!x.temporal) return "number";
  const v = x.values;
  const n = positions ? positions.length : v.length;
  for (let i = 0; i < n; i++) {
    const t = v[positions ? positions[i] : i];
    if (!Number.isNaN(t) && t % DAY !== 0) return "time";
  }
  return "date";
}

// ------------------------------------------------------------------------------------------------- formatting

const pad = (n: number, w = 2) => String(n).padStart(w, "0");

/** A value on the x axis as text: dates as yyyy-mm-dd, timestamps to the second, numbers as they are. */
export function formatX(v: number, kind: XKind): string {
  if (kind === "date" || kind === "time") {
    const d = new Date(v);
    if (Number.isNaN(d.getTime())) return String(v);
    const day = `${d.getUTCFullYear().toString().padStart(4, "0")}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
    if (kind === "date") return day;
    const ms = d.getUTCMilliseconds();
    return `${day} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}${ms ? `.${pad(ms, 3)}` : ""}`;
  }
  return formatNumber(v);
}

/** Full-precision-ish number text for labels, tooltips and CSV (no exponent below 1e21). */
export function formatNumber(v: number): string {
  if (!Number.isFinite(v)) return String(v);
  if (Number.isInteger(v)) return String(v);
  return String(Math.round(v * 1e10) / 1e10);
}

/** Axis ticks: 0 · 1.5K · 2M · 3.1B. */
export function formatCompact(v: number): string {
  const a = Math.abs(v);
  const f = (x: number, s: string) => `${Number.isInteger(x) ? x : Number(x.toFixed(x < 10 ? 2 : 1))}${s}`;
  if (a >= 1e12) return f(v / 1e12, "T");
  if (a >= 1e9) return f(v / 1e9, "B");
  if (a >= 1e6) return f(v / 1e6, "M");
  if (a >= 1e3) return f(v / 1e3, "K");
  if (a === 0) return "0";
  if (a < 0.001) return v.toExponential(1);
  return String(Number(v.toPrecision(3)));
}

/** Rounded ticks covering [min, max]: about `count` of them at a 1 / 2 / 5 × 10^n step. */
export function niceTicks(min: number, max: number, count = 5): number[] {
  if (!Number.isFinite(min) || !Number.isFinite(max)) return [];
  if (min === max) {
    if (min === 0) return [0, 1];
    const d = Math.abs(min) / 2;
    min -= d;
    max += d;
  }
  const raw = (max - min) / Math.max(1, count);
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= raw) ?? 10 * mag;
  const start = Math.floor(min / step) * step;
  const end = Math.ceil(max / step) * step;
  const out: number[] = [];
  for (let t = start, i = 0; t <= end + step / 2 && i < 100; t += step, i++) out.push(Math.round(t / step) * step);
  return out;
}

// ------------------------------------------------------------------------------------------------- down-sampling

/**
 * Largest-Triangle-Three-Buckets over `idx` (positions sorted by x, every one with a value): at most `threshold`
 * positions, always the first and the last, plus the series' minimum and maximum, so a spike is never dropped.
 */
export function lttb(xs: Float64Array, ys: Float64Array, idx: Uint32Array, threshold: number): Uint32Array {
  const n = idx.length;
  if (threshold >= n || threshold < 3) return idx.slice();
  const out: number[] = [idx[0]];
  const bucket = (n - 2) / (threshold - 2);
  let a = 0;
  for (let i = 0; i < threshold - 2; i++) {
    const avgStart = Math.floor((i + 1) * bucket) + 1;
    const avgEnd = Math.min(Math.floor((i + 2) * bucket) + 1, n);
    let avgX = 0;
    let avgY = 0;
    for (let j = avgStart; j < avgEnd; j++) {
      avgX += xs[idx[j]];
      avgY += ys[idx[j]];
    }
    const len = Math.max(1, avgEnd - avgStart);
    avgX /= len;
    avgY /= len;
    const start = Math.floor(i * bucket) + 1;
    const end = Math.min(Math.floor((i + 1) * bucket) + 1, n - 1);
    const ax = xs[idx[a]];
    const ay = ys[idx[a]];
    let best = start;
    let max = -1;
    for (let j = start; j < end; j++) {
      const area = Math.abs((ax - avgX) * (ys[idx[j]] - ay) - (ax - xs[idx[j]]) * (avgY - ay));
      if (area > max) {
        max = area;
        best = j;
      }
    }
    out.push(idx[best]);
    a = best;
  }
  out.push(idx[n - 1]);
  return withExtremes(out, ys, idx);
}

/** Every `step`-th position, plus the first, the last and the extremes (scatter: no line to preserve). */
export function strideSample(ys: Float64Array, idx: Uint32Array, max: number): Uint32Array {
  const n = idx.length;
  if (n <= max) return idx.slice();
  const step = n / (max - 2);
  const out: number[] = [];
  for (let k = 0; k < max - 2; k++) out.push(idx[Math.floor(k * step)]);
  out.push(idx[n - 1]);
  return withExtremes(out, ys, idx);
}

function withExtremes(picked: number[], ys: Float64Array, idx: Uint32Array): Uint32Array {
  let lo = idx[0];
  let hi = idx[0];
  for (let j = 1; j < idx.length; j++) {
    const p = idx[j];
    if (ys[p] < ys[lo]) lo = p;
    if (ys[p] > ys[hi]) hi = p;
  }
  const set = new Set(picked);
  set.add(lo);
  set.add(hi);
  return Uint32Array.from([...set].sort((x, y) => x - y));
}

// ------------------------------------------------------------------------------------------------- aggregation

/** Per-series accumulators, one slot per group. */
class Acc {
  sum: number[] = [];
  cnt: number[] = [];
  min: number[] = [];
  max: number[] = [];
  first: number[] = [];
  add(g: number, v: number) {
    while (this.sum.length <= g) {
      this.sum.push(0);
      this.cnt.push(0);
      this.min.push(Infinity);
      this.max.push(-Infinity);
      this.first.push(NaN);
    }
    if (this.cnt[g] === 0) this.first[g] = v;
    this.sum[g] += v;
    this.cnt[g]++;
    if (v < this.min[g]) this.min[g] = v;
    if (v > this.max[g]) this.max[g] = v;
  }
  value(g: number, agg: ChartAgg, folded: boolean): number {
    const c = this.cnt[g] ?? 0;
    if (!c) return NaN;
    switch (agg) {
      case "sum":
        return this.sum[g];
      case "count":
        return c;
      case "avg":
        return this.sum[g] / c;
      case "min":
        return this.min[g];
      case "max":
        return this.max[g];
      default:
        // No aggregation shows a row's own value; a folded "Other" group can only be shown as a total.
        return folded ? this.sum[g] : this.first[g];
    }
  }
}

interface Grouped {
  /** Group keys: x values (number x) or category indices in first-seen order. */
  keys: number[];
  labels: string[] | null;
  seriesNames: string[];
  accs: Acc[];
  plotted: number;
  nullsSkipped: number;
}

/**
 * Groups rows by x (and by series): series are the value columns, or one per distinct series label when a series
 * column is set (then only the first value column is used); count without value columns counts rows.
 */
function group(spec: ChartSpec, input: ChartInput): Grouped {
  const { x, ys, series } = input;
  const countRows = spec.agg === "count" && ys.length === 0;
  const splitBySeries = !!series && spec.kind !== "pie";
  const valueCols = countRows ? [] : splitBySeries ? ys.slice(0, 1) : ys;
  const seriesNames: string[] = splitBySeries ? [] : countRows ? ["Count"] : valueCols.map((c) => c.name);
  const seriesIndex = new Map<string, number>();
  const accs: Acc[] = seriesNames.map(() => new Acc());
  const keyIndex = new Map<string | number, number>();
  const keys: number[] = [];
  const labels: string[] | null = x.type === "cat" ? [] : null;
  // Groups and series are created when a value lands in them, so all-NULL rows add no empty bar or legend entry.
  const groupOf = (key: string | number): number => {
    let g = keyIndex.get(key);
    if (g === undefined) {
      g = keys.length;
      keyIndex.set(key, g);
      if (labels) {
        labels.push(key as string);
        keys.push(g);
      } else keys.push(key as number);
    }
    return g;
  };
  const seriesOf = (r: number): number => {
    const label = series![r] ?? "NULL";
    let s = seriesIndex.get(label);
    if (s === undefined) {
      s = seriesNames.length;
      seriesIndex.set(label, s);
      seriesNames.push(label);
      accs.push(new Acc());
    }
    return s;
  };
  let plotted = 0;
  let nullsSkipped = 0;
  for (let r = 0; r < input.rows; r++) {
    const key = x.values[r];
    if (key == null || (typeof key === "number" && Number.isNaN(key))) {
      nullsSkipped++;
      continue;
    }
    if (countRows) {
      accs[splitBySeries ? seriesOf(r) : 0].add(groupOf(key), 1);
      plotted++;
      continue;
    }
    let any = false;
    for (let k = 0; k < valueCols.length; k++) {
      const v = valueCols[k].values[r];
      if (Number.isNaN(v)) continue;
      // count with value columns counts their non-NULL values.
      accs[splitBySeries ? seriesOf(r) : k].add(groupOf(key), spec.agg === "count" ? 1 : v);
      any = true;
    }
    if (any) plotted++;
    else nullsSkipped++;
  }
  return { keys, labels, seriesNames, accs, plotted, nullsSkipped };
}

/** Group order: continuous x ascending; categories as the query returned them. */
function groupOrder(gr: Grouped): number[] {
  const order = gr.keys.map((_, i) => i);
  if (!gr.labels) order.sort((a, b) => gr.keys[a] - gr.keys[b]);
  return order;
}

function totals(gr: Grouped, agg: ChartAgg, groups: number[]): Float64Array {
  const t = new Float64Array(groups.length);
  groups.forEach((g, i) => {
    for (const acc of gr.accs) {
      const v = acc.value(g, agg, false);
      if (!Number.isNaN(v)) t[i] += Math.abs(v);
    }
  });
  return t;
}

/** Keeps the `max` biggest series (by total) and folds the rest into one "Other" series. */
function foldSeries(gr: Grouped, agg: ChartAgg, max: number, foldAsOther: boolean): { names: string[]; accs: Acc[]; other: boolean; folded: number } {
  const n = gr.seriesNames.length;
  if (n <= max) return { names: gr.seriesNames, accs: gr.accs, other: false, folded: 0 };
  const groups = gr.keys.map((_, i) => i);
  const size = gr.accs.map((acc) => groups.reduce((s, g) => s + (Number.isNaN(acc.value(g, agg, false)) ? 0 : Math.abs(acc.value(g, agg, false))), 0));
  const rank = size.map((_, i) => i).sort((a, b) => size[b] - size[a] || a - b);
  const keep = new Set(rank.slice(0, foldAsOther ? max - 1 : max));
  const kept = gr.seriesNames.map((_, i) => i).filter((i) => keep.has(i));
  const names = kept.map((i) => gr.seriesNames[i]);
  const accs = kept.map((i) => gr.accs[i]);
  if (!foldAsOther) return { names, accs, other: false, folded: n - kept.length };
  const other = new Acc();
  for (let i = 0; i < n; i++) if (!keep.has(i)) for (const g of groups) if ((gr.accs[i].cnt[g] ?? 0) > 0) mergeAcross(other, g, gr.accs[i], g);
  return { names: [...names, OTHER], accs: [...accs, other], other: true, folded: n - kept.length };
}

/** Folds group `fg` of `from` into group `g` of `into`. */
function mergeAcross(into: Acc, g: number, from: Acc, fg: number) {
  if ((from.cnt[fg] ?? 0) === 0) return;
  if ((into.cnt[g] ?? 0) === 0) {
    into.add(g, from.first[fg]);
    into.sum[g] = from.sum[fg];
    into.cnt[g] = from.cnt[fg];
    into.min[g] = from.min[fg];
    into.max[g] = from.max[fg];
    return;
  }
  into.sum[g] += from.sum[fg];
  into.cnt[g] += from.cnt[fg];
  into.min[g] = Math.min(into.min[g], from.min[fg]);
  into.max[g] = Math.max(into.max[g], from.max[fg]);
}

function duplicatesOf(accs: Acc[]): number {
  let d = 0;
  for (const acc of accs) for (const c of acc.cnt) if (c > 1) d++;
  return d;
}

function categoryChart(spec: ChartSpec, input: ChartInput, xName: string): ChartData {
  const gr = group(spec, input);
  const meta = emptyMeta(input.rows, input.lossy);
  meta.plotted = gr.plotted;
  meta.nullsSkipped = gr.nullsSkipped;
  const xKind = xKindOf(input.x);
  if (!gr.keys.length) return { shape: "empty", reason: "Every row has NULL in the chosen columns.", meta };
  const pie = spec.kind === "pie";
  if (spec.agg === "none") meta.duplicates = duplicatesOf(gr.accs);
  const fs = foldSeries(gr, spec.agg, MAX_SERIES, true);
  meta.foldedSeries = fs.folded;
  let order = groupOrder(gr);
  const label = (g: number) => (gr.labels ? gr.labels[g] : formatX(gr.keys[g], xKind));
  let otherGroup: number[] = [];
  if (pie) {
    // A donut shows parts of a whole: positive values only, biggest first, at most 8 slices.
    const acc = fs.accs[0];
    const positive = order.filter((g) => acc.value(g, spec.agg, false) > 0);
    meta.negativeSkipped = order.length - positive.length;
    positive.sort((a, b) => acc.value(b, spec.agg, false) - acc.value(a, spec.agg, false));
    if (positive.length > MAX_SLICES) {
      otherGroup = positive.slice(MAX_SLICES - 1);
      order = positive.slice(0, MAX_SLICES - 1);
    } else order = positive;
    if (!order.length) return { shape: "empty", reason: "A donut needs positive values; every value is zero or negative.", meta };
  } else if (order.length > MAX_CATEGORIES) {
    const t = totals(gr, spec.agg, order);
    const rank = order.map((_, i) => i).sort((a, b) => t[b] - t[a] || a - b);
    const keep = new Set(rank.slice(0, MAX_CATEGORIES).map((i) => order[i]));
    otherGroup = order.filter((g) => !keep.has(g));
    order = order.filter((g) => keep.has(g));
  }
  meta.foldedCategories = otherGroup.length;
  const categories = order.map(label);
  const n = categories.length + (otherGroup.length ? 1 : 0);
  const series: SeriesData[] = fs.names.map((name, s) => {
    const acc = fs.accs[s];
    const values = new Float64Array(n);
    order.forEach((g, i) => (values[i] = acc.value(g, spec.agg, false)));
    if (otherGroup.length) {
      const o = new Acc();
      for (const g of otherGroup) mergeAcross(o, 0, acc, g);
      values[n - 1] = o.value(0, spec.agg, true);
    }
    return { name, values, other: fs.other && s === fs.names.length - 1 };
  });
  if (otherGroup.length) categories.push(OTHER);
  return { shape: "category", kind: pie ? "pie" : "bar", xKind, xName, categories, series: pie ? series.slice(0, 1) : series, meta };
}

function histogram(spec: ChartSpec, input: ChartInput, xName: string): ChartData {
  const meta = emptyMeta(input.rows, input.lossy);
  if (input.x.type !== "num") return { shape: "empty", reason: "A histogram needs a number or date column.", meta };
  const v = input.x.values;
  let lo = Infinity;
  let hi = -Infinity;
  for (let i = 0; i < input.rows; i++) {
    const t = v[i];
    if (Number.isNaN(t)) {
      meta.nullsSkipped++;
      continue;
    }
    meta.plotted++;
    if (t < lo) lo = t;
    if (t > hi) hi = t;
  }
  if (!meta.plotted) return { shape: "empty", reason: "Every row has NULL in the chosen column.", meta };
  const bins = Math.max(1, Math.min(MAX_BINS, spec.bins ?? DEFAULT_BINS));
  const width = hi > lo ? (hi - lo) / bins : 1;
  const edges = new Float64Array(bins + 1);
  for (let b = 0; b <= bins; b++) edges[b] = lo + b * width;
  const counts = new Float64Array(bins);
  for (let i = 0; i < input.rows; i++) {
    const t = v[i];
    if (Number.isNaN(t)) continue;
    counts[Math.min(bins - 1, Math.floor((t - lo) / width))]++;
  }
  const kind = xKindOf(input.x);
  return { shape: "histogram", xKind: kind === "category" ? "number" : kind, xName, edges, counts, meta };
}

/** Positions 0..n-1 of rows whose x is not NULL, ordered by x (stable). */
function sortedPositions(x: Float64Array, rows: number): { perm: Uint32Array; nulls: number } {
  const list: number[] = [];
  let sorted = true;
  let prev = -Infinity;
  for (let r = 0; r < rows; r++) {
    const v = x[r];
    if (Number.isNaN(v)) continue;
    if (v < prev) sorted = false;
    prev = v;
    list.push(r);
  }
  const perm = Uint32Array.from(list);
  if (!sorted) perm.sort((a, b) => x[a] - x[b] || a - b);
  return { perm, nulls: rows - perm.length };
}

/** Line, area and scatter: points ordered by x, down-sampled per series, aligned on one x array (NaN = gap). */
function xyChart(spec: ChartSpec, input: ChartInput, xName: string, maxPoints: number): ChartData {
  const kind = spec.kind as "line" | "area" | "scatter";
  const meta = emptyMeta(input.rows, input.lossy);
  const continuous = input.x.type === "num";
  let X: Float64Array;
  let columns: { name: string; values: Float64Array; member?: Int32Array; s?: number }[];
  let labels: string[] | undefined;
  let seriesCount: number;
  let xKind: XKind;
  if (!continuous || spec.agg !== "none") {
    // Grouped: one point per x (per series).
    const gr = group(spec, input);
    meta.plotted = gr.plotted;
    meta.nullsSkipped = gr.nullsSkipped;
    if (spec.agg === "none") meta.duplicates = duplicatesOf(gr.accs);
    const fs = foldSeries(gr, spec.agg, kind === "scatter" ? MAX_SCATTER_SERIES : MAX_SERIES, false);
    meta.foldedSeries = fs.folded;
    const order = groupOrder(gr);
    X = Float64Array.from(gr.labels ? order.map((_, i) => i) : order.map((g) => gr.keys[g]));
    if (gr.labels) labels = order.map((g) => gr.labels![g]);
    columns = fs.names.map((name, s) => ({ name, values: Float64Array.from(order, (g) => fs.accs[s].value(g, spec.agg, false)) }));
    seriesCount = columns.length;
    xKind = xKindOf(input.x);
  } else {
    const xs = (input.x as { values: Float64Array }).values;
    const { perm } = sortedPositions(xs, input.rows);
    X = Float64Array.from(perm, (r) => xs[r]);
    xKind = xKindOf(input.x, perm);
    if (input.series) {
      // One series per label, all reading the first value column; a position belongs to its row's series.
      const source = input.ys[0];
      const index = new Map<string, number>();
      const member = new Int32Array(perm.length);
      const counts: number[] = [];
      for (let p = 0; p < perm.length; p++) {
        const label = input.series[perm[p]] ?? "NULL";
        let s = index.get(label);
        if (s === undefined) {
          s = index.size;
          index.set(label, s);
          counts.push(0);
        }
        member[p] = s;
        if (!Number.isNaN(source.values[perm[p]])) counts[s]++;
      }
      const names = [...index.keys()];
      const cap = kind === "scatter" ? MAX_SCATTER_SERIES : MAX_SERIES;
      const rank = names.map((_, i) => i).sort((a, b) => counts[b] - counts[a] || a - b).slice(0, cap).sort((a, b) => a - b);
      meta.foldedSeries = names.length - rank.length;
      const values = Float64Array.from(perm, (r) => source.values[r]);
      columns = rank.map((s) => ({ name: names[s], values, member, s }));
    } else {
      columns = input.ys.map((c) => ({ name: c.name, values: Float64Array.from(perm, (r) => c.values[r]) }));
    }
    seriesCount = columns.length;
    let plotted = 0;
    for (let p = 0; p < perm.length; p++) {
      if (columns.some((c) => (c.member ? c.member[p] === c.s : true) && !Number.isNaN(c.values[p]))) plotted++;
    }
    meta.plotted = plotted;
    meta.nullsSkipped = input.rows - plotted;
  }
  if (!X.length || !seriesCount) return { shape: "empty", reason: "Every row has NULL in the chosen columns.", meta };
  // Down-sample each series over its own positions, then draw the union of what any series kept.
  const keep = new Set<number>();
  let sampled = false;
  for (const c of columns) {
    const own: number[] = [];
    for (let p = 0; p < X.length; p++) if ((c.member ? c.member[p] === c.s : true) && !Number.isNaN(c.values[p])) own.push(p);
    const idx = Uint32Array.from(own);
    const picked = idx.length > maxPoints ? (kind === "scatter" ? strideSample(c.values, idx, maxPoints) : lttb(X, c.values, idx, maxPoints - 2)) : idx;
    if (picked.length < idx.length) sampled = true;
    for (const p of picked) keep.add(p);
  }
  const positions = Uint32Array.from(keep).sort();
  meta.sampled = sampled ? maxPoints : null;
  const x = Float64Array.from(positions, (p) => X[p]);
  const series: SeriesData[] = columns.map((c) => ({
    name: c.name,
    values: Float64Array.from(positions, (p) => (c.member && c.member[p] !== c.s ? NaN : c.values[p])),
  }));
  return { shape: "xy", kind, xKind, x, labels, xName, series, meta };
}

/** The chart for a spec over extracted columns; `maxPoints` caps each line's points (see pointsFor). */
export function computeChart(spec: ChartSpec, input: ChartInput, maxPoints = MAX_POINTS): ChartData {
  const xName = spec.x ?? "";
  if (!input.rows) return { shape: "empty", reason: "The result has no rows.", meta: emptyMeta(0, input.lossy) };
  if (spec.kind === "histogram") return histogram(spec, input, xName);
  if (spec.kind === "bar" || spec.kind === "pie") return categoryChart(spec, input, xName);
  return xyChart(spec, input, xName, Math.max(MIN_POINTS, Math.min(MAX_POINTS, Math.round(maxPoints))));
}

/** The plotted (aggregated) data as CSV: the x column, then one column per series. */
export function chartCsv(data: ChartData): string {
  const cell = (v: string) => (/[",\r\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v);
  const num = (v: number) => (Number.isNaN(v) ? "" : formatNumber(v));
  const lines: string[] = [];
  if (data.shape === "histogram") {
    lines.push(["bin_start", "bin_end", "count"].join(","));
    for (let b = 0; b < data.counts.length; b++) lines.push([cell(formatX(data.edges[b], data.xKind)), cell(formatX(data.edges[b + 1], data.xKind)), num(data.counts[b])].join(","));
  } else if (data.shape === "category") {
    lines.push([cell(data.xName), ...data.series.map((s) => cell(s.name))].join(","));
    data.categories.forEach((c, i) => lines.push([cell(c), ...data.series.map((s) => num(s.values[i]))].join(",")));
  } else if (data.shape === "xy") {
    lines.push([cell(data.xName), ...data.series.map((s) => cell(s.name))].join(","));
    for (let i = 0; i < data.x.length; i++) {
      const x = data.labels ? data.labels[data.x[i]] ?? "" : formatX(data.x[i], data.xKind);
      lines.push([cell(x), ...data.series.map((s) => num(s.values[i]))].join(","));
    }
  }
  return lines.join("\r\n") + (lines.length ? "\r\n" : "");
}

/** Typed arrays of a result (posted back from the worker without copying). */
export function transferables(data: ChartData): ArrayBuffer[] {
  const out: ArrayBuffer[] = [];
  if (data.shape === "xy") out.push(data.x.buffer as ArrayBuffer, ...data.series.map((s) => s.values.buffer as ArrayBuffer));
  if (data.shape === "category") out.push(...data.series.map((s) => s.values.buffer as ArrayBuffer));
  if (data.shape === "histogram") out.push(data.edges.buffer as ArrayBuffer, data.counts.buffer as ArrayBuffer);
  // A buffer shared by two arrays may be listed once only.
  return [...new Set(out)];
}

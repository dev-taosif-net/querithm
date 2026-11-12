// Chart worker (Phase C1): holds the columns one chart reads, appended batch by batch as the main thread forwards
// them (Arrow IPC of those columns only), and computes the chart off the main thread.
import { tableFromIPC } from "apache-arrow";
import { buildColumns } from "../results/model";
import { computeChart, transferables, type ChartInput } from "./chartData";
import { labelChunk, numericChunk } from "./extract";
import type { ChartSpec } from "./chartSpec";

export interface WantedColumn {
  name: string;
  /** Extract as doubles (x on a continuous axis, values). */
  num: boolean;
  /** Extract as display labels (categories, series). */
  label: boolean;
}

export type ChartWorkerIn =
  | { type: "reset"; columns: WantedColumn[] }
  | { type: "append"; batches: ArrayBuffer[] }
  | { type: "compute"; seq: number; spec: ChartSpec; maxPoints?: number };

export type ChartWorkerOut =
  | { type: "result"; seq: number; data: import("./chartData").ChartData; ms: number }
  | { type: "error"; seq: number; message: string };

interface Held {
  want: WantedColumn;
  nums: Float64Array[];
  labels: (string | null)[][];
  temporal: boolean;
  lossy: boolean;
  nonFinite: number;
  /** Concatenations, dropped when a batch arrives. */
  numAll?: Float64Array;
  labelAll?: (string | null)[];
}

/** The worker's state, exported so tests drive it without a Worker. */
export class ChartColumns {
  rows = 0;
  private held = new Map<string, Held>();

  reset(columns: WantedColumn[]) {
    this.rows = 0;
    this.held = new Map(columns.map((w) => [w.name, { want: w, nums: [], labels: [], temporal: false, lossy: false, nonFinite: 0 }]));
  }

  append(ipc: Uint8Array) {
    const table = tableFromIPC(ipc);
    const cols = buildColumns(table.schema);
    for (const batch of table.batches) {
      const vectors = batch.schema.fields.map((_, i) => batch.getChildAt(i)!);
      for (const h of this.held.values()) {
        const col = cols.find((c) => c.name === h.want.name);
        if (!col) throw new Error(`column ${h.want.name} is missing from the forwarded batch`);
        const vec = vectors[col.field];
        if (h.want.num) {
          const c = numericChunk(vec, col, col.classField != null ? vectors[col.classField] : undefined);
          h.nums.push(c.values);
          h.temporal ||= c.temporal;
          h.lossy ||= c.lossy;
          h.nonFinite += c.nonFinite;
        }
        if (h.want.label) h.labels.push(labelChunk(vec, col, vectors));
        h.numAll = undefined;
        h.labelAll = undefined;
      }
      this.rows += batch.numRows;
    }
  }

  private nums(name: string): Float64Array {
    const h = this.held.get(name);
    if (!h?.want.num) throw new Error(`column ${name} was not extracted as numbers`);
    if (!h.numAll) {
      if (h.nums.length === 1) h.numAll = h.nums[0];
      else {
        const all = new Float64Array(this.rows);
        let off = 0;
        for (const c of h.nums) {
          all.set(c, off);
          off += c.length;
        }
        h.numAll = all;
        h.nums = [all];
      }
    }
    return h.numAll;
  }

  private labels(name: string): (string | null)[] {
    const h = this.held.get(name);
    if (!h?.want.label) throw new Error(`column ${name} was not extracted as labels`);
    if (!h.labelAll) {
      h.labelAll = h.labels.length === 1 ? h.labels[0] : h.labels.flat();
      h.labels = [h.labelAll];
    }
    return h.labelAll;
  }

  input(spec: ChartSpec): ChartInput {
    const x = this.held.get(spec.x ?? "");
    if (!x) throw new Error("the x column was not extracted");
    const ys = spec.kind === "histogram" ? [] : spec.y.map((n) => ({ name: n, values: this.nums(n) }));
    const used = [x, ...(spec.kind === "histogram" ? [] : spec.y.map((n) => this.held.get(n)))];
    const lossy = used.some((h) => !!h?.lossy);
    const nonFinite = used.reduce((n, h) => n + (h?.want.num ? h.nonFinite : 0), 0);
    return {
      rows: this.rows,
      x: x.want.num ? { type: "num", values: this.nums(x.want.name), temporal: x.temporal } : { type: "cat", values: this.labels(x.want.name) },
      ys,
      series: spec.series && spec.kind !== "histogram" ? this.labels(spec.series) : undefined,
      lossy,
      nonFinite,
    };
  }
}

const state = new ChartColumns();

/** Answers one message from the main thread (exported for tests). */
export function handle(m: ChartWorkerIn, post: (msg: ChartWorkerOut, transfer?: ArrayBuffer[]) => void, cols = state) {
  try {
    if (m.type === "reset") cols.reset(m.columns);
    else if (m.type === "append") for (const b of m.batches) cols.append(new Uint8Array(b));
    else if (m.type === "compute") {
      const t0 = performance.now();
      const data = computeChart(m.spec, cols.input(m.spec), m.maxPoints);
      post({ type: "result", seq: m.seq, data, ms: performance.now() - t0 }, transferables(data));
    }
  } catch (err) {
    post({ type: "error", seq: m.type === "compute" ? m.seq : -1, message: err instanceof Error ? err.message : String(err) });
  }
}

if (typeof self !== "undefined") self.onmessage = (e: MessageEvent<ChartWorkerIn>) => {
  handle(e.data, (msg, transfer = []) => (self as unknown as Worker).postMessage(msg, transfer));
};

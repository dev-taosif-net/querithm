// Main-thread side of a chart (Phase C1): forwards the result set's batches to the chart worker (only the columns the
// spec reads, a few batches per task so no task blocks a frame) and asks it to recompute. React never sees batches.
import { Table, tableToIPC } from "apache-arrow";
import { classBit, StorageClass, type ArrowResultSet } from "../results/model";
import { MAX_POINTS, type ChartData } from "./chartData";
import { specColumns, type ChartColumn, type ChartSpec } from "./chartSpec";
import type { ChartWorkerIn, ChartWorkerOut, WantedColumn } from "./chartData.worker";
import ChartWorker from "./chartData.worker?worker";

const NUMBER_CLASSES = classBit(StorageClass.Integer) | classBit(StorageClass.Real);
const OTHER_CLASSES = classBit(StorageClass.Text) | classBit(StorageClass.Blob);

/**
 * The result's columns as a chart sees them. A dynamically typed column (SQLite) is a number column only while every
 * value in it is an integer or a real (and there is at least one); a column that also holds text or blobs is `mixed`.
 * Rows still streaming in can turn a number column mixed: callers re-read this as batches arrive.
 */
export function chartColumns(rs: ArrowResultSet): ChartColumn[] {
  return rs.columns.map((c, i) => {
    if (c.classField == null) return { name: c.name, kind: c.kind, typeName: c.typeName };
    const seen = rs.classesSeen(i);
    const numbers = (seen & NUMBER_CLASSES) !== 0;
    const other = (seen & OTHER_CLASSES) !== 0;
    return { name: c.name, kind: numbers && !other ? "number" : c.kind, typeName: c.typeName, mixed: numbers && other };
  });
}

/** What a dynamically typed column's chart kind depends on (the classes seen so far); "" when the result has none. */
export function chartColumnsKey(rs: ArrowResultSet): string {
  let key = "";
  rs.columns.forEach((c, i) => {
    if (c.classField != null) key += `${i}:${rs.classesSeen(i)},`;
  });
  return key;
}

/** How the worker extracts each column the spec reads (`columns` as `chartColumns` gives them). */
export function wantedColumns(spec: ChartSpec, columns: readonly Pick<ChartColumn, "name" | "kind">[]): WantedColumn[] {
  const out = new Map<string, WantedColumn>();
  const want = (name: string | null | undefined, num: boolean, label: boolean) => {
    if (!name || !columns.some((c) => c.name === name)) return;
    const w = out.get(name) ?? { name, num: false, label: false };
    w.num ||= num;
    w.label ||= label;
    out.set(name, w);
  };
  const x = columns.find((c) => c.name === spec.x);
  const continuous = !!x && (x.kind === "number" || x.kind === "temporal");
  want(spec.x, continuous, !continuous);
  if (spec.kind !== "histogram") {
    for (const y of spec.y) want(y, true, false);
    want(spec.series, false, true);
  }
  return specColumns(spec)
    .map((n) => out.get(n))
    .filter((w): w is WantedColumn => !!w);
}

/** Lets a frame happen between chunks of work. */
const nextTask = () => new Promise<void>((r) => setTimeout(r, 0));

/** Main-thread work per task while forwarding batches (ms). */
const SLICE_MS = 8;

export class ChartFeed {
  private worker: Worker;
  private sent = 0;
  private seq = 0;
  private chain: Promise<void> = Promise.resolve();
  private fields: number[];
  private last = { spec: "", rows: -1, complete: false, maxPoints: 0 };
  private disposed = false;

  constructor(
    private rs: ArrowResultSet,
    wanted: WantedColumn[],
    private onResult: (data: ChartData, workerMs: number) => void,
    private onError: (message: string) => void,
    makeWorker: () => Worker = () => new ChartWorker(),
  ) {
    // A dynamically typed column travels with its storage-class sibling: the worker reads numbers by class.
    this.fields = wanted.flatMap((w) => {
      const c = rs.columns.find((col) => col.name === w.name)!;
      return c.classField != null ? [c.field, c.classField] : [c.field];
    });
    this.worker = makeWorker();
    this.worker.onmessage = (e: MessageEvent<ChartWorkerOut>) => {
      const m = e.data;
      if (this.disposed || m.seq !== this.seq) return; // a newer computation is on its way
      if (m.type === "result") this.onResult(m.data, m.ms);
      else this.onError(m.message);
    };
    this.worker.onerror = (e) => this.onError(e.message || "The chart worker failed.");
    this.post({ type: "reset", columns: wanted });
  }

  private post(m: ChartWorkerIn, transfer: Transferable[] = []) {
    if (!this.disposed) this.worker.postMessage(m, transfer);
  }

  /** Forwards the batches that arrived since the last call. */
  private forward = async () => {
    while (!this.disposed && this.sent < this.rs.batches.length) {
      const t0 = performance.now();
      const out: ArrayBuffer[] = [];
      while (this.sent < this.rs.batches.length && performance.now() - t0 < SLICE_MS) {
        const ipc = tableToIPC(new Table([this.rs.batches[this.sent++].selectAt(this.fields)]), "stream");
        out.push(ipc.buffer.slice(ipc.byteOffset, ipc.byteOffset + ipc.byteLength) as ArrayBuffer);
      }
      this.post({ type: "append", batches: out }, out);
      if (this.sent < this.rs.batches.length) await nextTask();
    }
  };

  /** Forwards new batches, then recomputes; nothing happens when neither the rows nor the spec changed. */
  update(spec: ChartSpec, maxPoints = MAX_POINTS) {
    const key = JSON.stringify(spec);
    this.chain = this.chain
      .then(this.forward)
      .then(() => {
        const rows = this.rs.rowCount;
        if (key === this.last.spec && rows === this.last.rows && this.rs.complete === this.last.complete && maxPoints === this.last.maxPoints) return;
        this.last = { spec: key, rows, complete: this.rs.complete, maxPoints };
        this.post({ type: "compute", seq: ++this.seq, spec, maxPoints });
      })
      .catch((e) => this.onError(e instanceof Error ? e.message : String(e)));
    return this.chain;
  }

  dispose() {
    this.disposed = true;
    this.worker.terminate();
  }
}

/** One computation of a finished result set (notebook export): a worker of its own, closed afterwards. */
export function computeChartOnce(rs: ArrowResultSet, spec: ChartSpec, makeWorker?: () => Worker): Promise<ChartData> {
  return new Promise((resolve, reject) => {
    const feed = new ChartFeed(
      rs,
      wantedColumns(spec, chartColumns(rs)),
      (data) => {
        feed.dispose();
        resolve(data);
      },
      (message) => {
        feed.dispose();
        reject(new Error(message));
      },
      makeWorker,
    );
    void feed.update(spec);
  });
}

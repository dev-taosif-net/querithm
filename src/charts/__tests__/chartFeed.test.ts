import { describe, expect, it, vi } from "vitest";
import { Int32, tableFromIPC, Utf8 } from "apache-arrow";
import { ArrowResultSet } from "../../results/model";
import { dynamicColumn, makeBatch, type DynamicCell } from "../../results/__tests__/fixtures";
import { ChartFeed, chartColumns, chartColumnsKey, wantedColumns } from "../chartFeed";
import type { ChartWorkerIn } from "../chartData.worker";
import type { ChartSpec } from "../chartSpec";

vi.mock("../chartData.worker?worker", () => ({ default: class {} }));

class FakeWorker {
  sent: ChartWorkerIn[] = [];
  terminated = false;
  onmessage: ((e: MessageEvent) => void) | null = null;
  onerror: ((e: ErrorEvent) => void) | null = null;
  postMessage(m: ChartWorkerIn) {
    this.sent.push(m);
  }
  terminate() {
    this.terminated = true;
  }
  reply(data: unknown) {
    this.onmessage?.({ data } as MessageEvent);
  }
  of<T extends ChartWorkerIn["type"]>(type: T) {
    return this.sent.filter((m): m is Extract<ChartWorkerIn, { type: T }> => m.type === type);
  }
}

const batch = (n: number) =>
  makeBatch([
    { name: "id", type: new Int32(), values: Array.from({ length: n }, (_, i) => i) },
    { name: "region", type: new Utf8(), values: Array.from({ length: n }, (_, i) => `r${i % 3}`) },
    { name: "sales", type: new Int32(), values: Array.from({ length: n }, (_, i) => i * 10) },
  ]);
const spec: ChartSpec = { kind: "bar", x: "region", y: ["sales"], series: null, agg: "sum" };

function setup(batches = 3) {
  const rs = new ArrowResultSet(0);
  for (let i = 0; i < batches; i++) rs.append(batch(4));
  const w = new FakeWorker();
  const results: unknown[] = [];
  const errors: string[] = [];
  const feed = new ChartFeed(rs, wantedColumns(spec, rs.columns), (d) => results.push(d), (e) => errors.push(e), () => w as unknown as Worker);
  return { rs, w, feed, results, errors };
}

describe("chart feed (main thread → chart worker)", () => {
  it("asks for each column once, as numbers or labels by its type", () => {
    const rs = new ArrowResultSet(0);
    rs.append(batch(1));
    expect(wantedColumns({ kind: "line", x: "id", y: ["sales"], series: "region", agg: "none" }, rs.columns)).toEqual([
      { name: "id", num: true, label: false },
      { name: "sales", num: true, label: false },
      { name: "region", num: false, label: true },
    ]);
    expect(wantedColumns({ kind: "bar", x: "region", y: ["sales"], series: "sales", agg: "sum" }, rs.columns)).toEqual([
      { name: "region", num: false, label: true },
      { name: "sales", num: true, label: true },
    ]);
  });

  it("forwards every batch with only the spec's columns, then computes", async () => {
    const { w, feed } = setup();
    expect(w.sent[0]).toEqual({ type: "reset", columns: [{ name: "region", num: false, label: true }, { name: "sales", num: true, label: false }] });
    await feed.update(spec);
    const forwarded = w.of("append").flatMap((m) => m.batches);
    expect(forwarded).toHaveLength(3);
    const t = tableFromIPC(new Uint8Array(forwarded[0]));
    expect(t.schema.fields.map((f) => f.name)).toEqual(["region", "sales"]);
    expect(t.numRows).toBe(4);
    expect(w.of("compute")).toEqual([{ type: "compute", seq: 1, spec, maxPoints: 10_000 }]);
  });

  it("does nothing when neither rows nor spec changed; new rows are forwarded alone", async () => {
    const { rs, w, feed } = setup();
    await feed.update(spec);
    await feed.update({ ...spec });
    expect(w.of("compute")).toHaveLength(1);
    rs.append(batch(2));
    await feed.update(spec);
    expect(w.of("append").flatMap((m) => m.batches)).toHaveLength(4);
    expect(w.of("compute")).toHaveLength(2);
    await feed.update({ ...spec, agg: "avg" });
    expect(w.of("compute")).toHaveLength(3);
    // A wider or narrower plot changes the points per line: recomputed, nothing re-sent.
    await feed.update({ ...spec, agg: "avg" }, 1_536);
    expect(w.of("compute").at(-1)).toMatchObject({ maxPoints: 1_536 });
    expect(w.of("append").flatMap((m) => m.batches)).toHaveLength(4);
  });

  it("delivers the newest computation only, and errors", async () => {
    const { w, feed, results, errors } = setup();
    await feed.update(spec);
    await feed.update({ ...spec, agg: "max" });
    w.reply({ type: "result", seq: 1, data: "old", ms: 1 });
    w.reply({ type: "result", seq: 2, data: "new", ms: 1 });
    w.reply({ type: "error", seq: 2, message: "boom" });
    expect(results).toEqual(["new"]);
    expect(errors).toEqual(["boom"]);
  });

  it("stops talking to the worker once disposed", async () => {
    const { w, feed, results } = setup();
    feed.dispose();
    expect(w.terminated).toBe(true);
    const before = w.sent.length;
    await feed.update(spec);
    expect(w.sent.length).toBe(before);
    w.reply({ type: "result", seq: 0, data: "late", ms: 1 });
    expect(results).toEqual([]);
  });

  describe("dynamically typed columns (Phase D0f, SQLite)", () => {
    const region = (n: number) => ({ name: "region", type: new Utf8(), values: Array.from({ length: n }, (_, i) => `r${i}`), meta: { "q:typeName": "nvarchar" } });
    const set = (...batches: DynamicCell[][]) => {
      const rs = new ArrowResultSet(0);
      for (const cells of batches) rs.append(makeBatch([region(cells.length), ...dynamicColumn("v", cells, 1)]));
      return rs;
    };

    it("a column of integers and reals (NULLs aside) is a number column; text or blobs in it make it mixed", () => {
      expect(chartColumns(set([[1, "1"], [0, null], [2, "2.5"]]))).toEqual([
        { name: "region", kind: "text", typeName: "nvarchar" },
        { name: "v", kind: "number", typeName: "", mixed: false },
      ]);
      expect(chartColumns(set([[1, "1"], [3, "n/a"]]))[1]).toMatchObject({ kind: "text", mixed: true });
      expect(chartColumns(set([[2, "1.5"], [4, "00ff"]]))[1]).toMatchObject({ kind: "text", mixed: true });
      // Only text, or only NULL: not a number column, and nothing mixed to explain.
      expect(chartColumns(set([[3, "a"], [3, "b"]]))[1]).toMatchObject({ kind: "text", mixed: false });
      expect(chartColumns(set([[0, null]]))[1]).toMatchObject({ kind: "text", mixed: false });
    });

    it("a text row arriving later turns the number column mixed, and the key changes with it", () => {
      const rs = set([[1, "1"], [1, "2"]]);
      const before = chartColumnsKey(rs);
      expect(chartColumns(rs)[1].kind).toBe("number");
      rs.append(makeBatch([region(1), ...dynamicColumn("v", [[3, "three"]], 1)]));
      expect(chartColumnsKey(rs)).not.toBe(before);
      expect(chartColumns(rs)[1]).toMatchObject({ kind: "text", mixed: true });
    });

    it("columns that are not dynamic are as before and give an empty key", () => {
      const rs = new ArrowResultSet(0);
      rs.append(batch(2));
      expect(chartColumns(rs)).toEqual(rs.columns.map((c) => ({ name: c.name, kind: c.kind, typeName: c.typeName })));
      expect(chartColumnsKey(rs)).toBe("");
    });

    it("a numeric dynamic x is extracted as numbers, and its class sibling travels with it to the worker", async () => {
      const rs = set([[1, "1"], [2, "2.5"]]);
      const hist: ChartSpec = { kind: "histogram", x: "v", y: [], series: null, agg: "none" };
      expect(wantedColumns(hist, chartColumns(rs))).toEqual([{ name: "v", num: true, label: false }]);
      const w = new FakeWorker();
      const feed = new ChartFeed(rs, wantedColumns(hist, chartColumns(rs)), () => {}, () => {}, () => w as unknown as Worker);
      await feed.update(hist);
      const t = tableFromIPC(new Uint8Array(w.of("append")[0].batches[0]));
      expect(t.schema.fields.map((f) => f.name)).toEqual(["v", "v__class"]);
    });
  });
});

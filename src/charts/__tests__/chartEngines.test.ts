// Charts per Phase D engine: each provider's typical result shapes (results/__tests__/fixtures.ts, as the sidecar's
// encoder sends them) through the whole chart path — the columns a chart offers, the inferred chart, the main-thread
// feed (only the wanted columns, a dynamic column with its class sibling) and the worker's extraction and computation.
import { describe, expect, it, vi } from "vitest";
import { ArrowResultSet } from "../../results/model";
import { duckDbShapes, mySqlShapes, oracleShapes, sqliteShapes, utcMicros } from "../../results/__tests__/fixtures";
import { chartColumns, computeChartOnce } from "../chartFeed";
import { ChartColumns, handle, type ChartWorkerIn } from "../chartData.worker";
import { inferSpec, specProblem, type ChartSpec } from "../chartSpec";
import { formatX, type ChartData } from "../chartData";
import type { RecordBatch } from "apache-arrow";

vi.mock("../chartData.worker?worker", () => ({ default: class {} }));

/** The chart worker in-process: the same message handler and column store, replies after the current task. */
class LocalWorker {
  onmessage: ((e: MessageEvent) => void) | null = null;
  onerror: ((e: ErrorEvent) => void) | null = null;
  private cols = new ChartColumns();
  postMessage(m: ChartWorkerIn) {
    handle(m, (msg) => queueMicrotask(() => this.onmessage?.({ data: msg } as MessageEvent)), this.cols);
  }
  terminate() {}
}

const resultOf = (b: RecordBatch) => {
  const rs = new ArrowResultSet(0);
  rs.append(b);
  rs.finish(false);
  return rs;
};
const draw = (rs: ArrowResultSet, spec: ChartSpec) => computeChartOnce(rs, spec, () => new LocalWorker() as unknown as Worker);
const kinds = (rs: ArrowResultSet) => Object.fromEntries(chartColumns(rs).map((c) => [c.name, c.mixed ? "mixed" : c.kind]));
const values = (d: ChartData, i = 0) => ("series" in d ? [...d.series[i].values] : []);

describe("SQLite (dynamic columns)", () => {
  const rs = resultOf(sqliteShapes());

  it("offers integer / real columns as numbers, a column also holding text as mixed, blobs and text as labels", () => {
    expect(kinds(rs)).toEqual({ id: "number", name: "text", price: "number", note: "mixed", data: "text" });
    expect(inferSpec(chartColumns(rs))).toEqual({ kind: "bar", x: "name", y: ["price"], series: null, agg: "sum" });
  });

  it("draws the inferred bar from the numbers' text by class; the NULL category is skipped", async () => {
    const d = await draw(rs, inferSpec(chartColumns(rs))!);
    expect(d).toMatchObject({ shape: "category", categories: ["apple", 'say "hi", ok'], meta: { nullsSkipped: 1, lossy: false } });
    expect(values(d)).toEqual([3, 2.5]);
  });

  it("refuses the mixed column with its reason; a numeric x draws a line of reals", async () => {
    expect(specProblem({ kind: "line", x: "id", y: ["note"], series: null, agg: "none" }, chartColumns(rs))).toBe("Mixed value types: note is not numeric in every row.");
    const d = await draw(rs, { kind: "line", x: "id", y: ["price"], series: null, agg: "none" });
    expect(d).toMatchObject({ shape: "xy", xKind: "number" });
    expect([...(d as { x: Float64Array }).x]).toEqual([1, 2, 3]);
    expect(values(d)).toEqual([3, 2.5, 0.30000000000000004]);
  });
});

describe("MySQL / MariaDB", () => {
  const rs = resultOf(mySqlShapes());

  it("offers integers, decimals, BIT and unsigned BIGINT as numbers (wide ones arrive as numeric text, M11); dates and JSON as text", () => {
    expect(kinds(rs)).toEqual({ id: "number", tiny: "number", amount: "number", wide: "number", dt: "text", ts: "text", doc: "text", flags: "number", big: "number" });
    // No temporal column (DATETIME / TIMESTAMP are the server's text): a bar over the first text column.
    expect(inferSpec(chartColumns(rs))).toEqual({ kind: "bar", x: "dt", y: ["tiny"], series: null, agg: "sum" });
    expect(specProblem({ kind: "bar", x: "dt", y: ["doc"], series: null, agg: "sum" }, chartColumns(rs))).toBe("Value columns must hold numbers.");
  });

  it("draws unsigned BIGINT from its exact text, flagged approximate beyond a double's digits", async () => {
    const d = await draw(rs, { kind: "bar", x: "dt", y: ["big"], series: null, agg: "sum" });
    expect(d).toMatchObject({ meta: { lossy: true } });
    expect(values(d, 0)).toEqual([18446744073709551615, 0]); // the category whose only value is NULL is skipped
  });

  it("draws exact decimals and signed TINYINT; a NULL value is skipped; dates are categories", async () => {
    const d = await draw(rs, { kind: "bar", x: "dt", y: ["amount", "tiny"], series: null, agg: "sum" });
    expect(d).toMatchObject({ shape: "category", categories: ["2026-10-31 12:34:56", "0000-00-00 00:00:00", "2026-11-01 00:00:00"], meta: { lossy: false } });
    expect(values(d, 0).map((v) => (Number.isNaN(v) ? null : v))).toEqual([12.34, -9.95, null]);
    expect(values(d, 1)).toEqual([-128, 0, 127]);
  });
});

describe("DuckDB", () => {
  const rs = resultOf(duckDbShapes());

  it("offers integers, exact decimals and HUGEINT (numeric text, M11), timestamps and booleans; intervals and nested values are text; blobs not at all", () => {
    expect(kinds(rs)).toEqual({
      id: "number", d: "number", d38: "number", h: "number", iv: "text", l: "text", st: "text", mp: "text", ts: "temporal", tz: "temporal", bl: "binary", b: "bool",
    });
    expect(inferSpec(chartColumns(rs))?.x).toBe("ts");
    expect(specProblem({ kind: "bar", x: "ts", y: ["iv"], series: null, agg: "sum" }, chartColumns(rs))).toBe("Value columns must hold numbers.");
    expect(specProblem({ kind: "bar", x: "bl", y: ["id"], series: null, agg: "sum" }, chartColumns(rs))).toBe("bl holds binary values, which cannot be plotted.");
  });

  it("puts TIMESTAMP and TIMESTAMPTZ on a time axis (epoch ms), skipping NULL instants", async () => {
    const line = await draw(rs, inferSpec(chartColumns(rs))!);
    expect(line).toMatchObject({ shape: "xy", kind: "line", xKind: "time", meta: { plotted: 2, nullsSkipped: 1 } });
    expect([...(line as { x: Float64Array }).x]).toEqual([1_793_442_030_000, 1_793_528_430_123.456]);
    const tz = await draw(rs, { kind: "scatter", x: "tz", y: ["id"], series: null, agg: "none" });
    expect([...(tz as { x: Float64Array }).x].sort((a, b) => a - b)).toEqual([0, 1_793_420_430_123.456]);
  });

  it("labels nested values and intervals with DuckDB's own text", async () => {
    const d = await draw(rs, { kind: "bar", x: "st", y: [], series: null, agg: "count" });
    expect(d).toMatchObject({ shape: "category", categories: ["{'a': 1, 'b': 'x'}", "{'a': NULL, 'b': NULL}"] });
    const iv = await draw(rs, { kind: "bar", x: "iv", y: ["id"], series: null, agg: "sum" });
    expect(iv).toMatchObject({ categories: ["1 year 2 months 3 days 04:05:06.789", "00:00:00"] });
  });
});

describe("Oracle (D4 S12)", () => {
  const rs = resultOf(oracleShapes());

  it("offers NUMBER (Decimal128 or numeric text), BINARY_DOUBLE and DATE as numbers / time; zone, TIMESTAMP(9) and INTERVAL text as labels; RAW not at all", () => {
    expect(kinds(rs)).toEqual({
      ID: "number", AMT: "number", BIG: "number", N: "number", BD: "number", D: "temporal", TZ: "text", TS9: "text", IDS: "text", R: "binary", B: "bool", C3: "text",
    });
    expect(inferSpec(chartColumns(rs))?.x).toBe("D");
    expect(specProblem({ kind: "bar", x: "D", y: ["TZ"], series: null, agg: "sum" }, chartColumns(rs))).toBe("Value columns must hold numbers.");
  });

  it("BINARY_DOUBLE NaN and -Infinity are not drawn and are said apart from NULL", async () => {
    const bar = await draw(rs, { kind: "bar", x: "IDS", y: ["BD"], series: null, agg: "sum" });
    // The category whose only value is NaN is skipped, as one whose only value is NULL; the note counts both values.
    expect(bar).toMatchObject({ shape: "category", categories: ["+01 02:03:04.123456"], meta: { nonFinite: 2 } });
    expect(values(bar, 0)).toEqual([1.5]);
    // A histogram's bins stay finite: -Infinity would make every edge NaN and drop every count.
    const h = await draw(rs, { kind: "histogram", x: "BD", y: [], series: null, agg: "count", bins: 4 });
    expect(h.shape).toBe("histogram");
    const hist = h as Extract<ChartData, { shape: "histogram" }>;
    expect([...hist.edges].every(Number.isFinite)).toBe(true);
    expect([...hist.counts].reduce((a, b) => a + b, 0)).toBe(1);
    expect(hist.meta.nonFinite).toBe(2);
  });

  it("puts DATE on a time axis, a BC date included, and labels it with its sign", async () => {
    const d = await draw(rs, { kind: "scatter", x: "D", y: ["ID"], series: null, agg: "none" });
    const x = [...(d as { x: Float64Array }).x].sort((a, b) => a - b);
    expect(x).toEqual([Number(utcMicros(-3999, 3, 4, 5, 6, 7) / 1000n), Number(utcMicros(2024, 2, 29, 13, 14, 15) / 1000n)]);
    expect(formatX(x[0], "time")).toBe("-3999-03-04 05:06:07");
    expect(d.meta).toMatchObject({ nonFinite: 0, nullsSkipped: 1 });
  });

  it("draws NUMBER(38) from its decimal, flagged approximate beyond a double's digits; zone text stays a category", async () => {
    const d = await draw(rs, { kind: "bar", x: "TZ", y: ["BIG", "AMT"], series: null, agg: "sum" });
    expect(d).toMatchObject({ shape: "category", categories: ["2024-07-01 02:30:00.500000 America/New_York", "2024-07-01 02:30:00.000000 +05:30"], meta: { lossy: true } });
    expect(values(d, 1)).toEqual([1.25, -0.005]);
  });
});

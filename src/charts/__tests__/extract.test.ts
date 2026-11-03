import { describe, expect, it } from "vitest";
import {
  Bool, DateDay, Decimal, Field, Float64, Int32, Int64, makeData, RecordBatch, Schema, Struct, Table, tableToIPC, TimestampMicrosecond, Utf8, vectorFromArray, type Data,
} from "apache-arrow";
import { buildColumns } from "../../results/model";
import { dynamicColumn, makeBatch, type DynamicCell } from "../../results/__tests__/fixtures";
import { labelChunk, numericChunk } from "../extract";
import { ChartColumns } from "../chartData.worker";
import { computeChart } from "../chartData";

/** A validity bitmap with bit i set when `valid[i]`. */
const bitmap = (valid: boolean[]) => {
  const b = new Uint8Array(Math.ceil(valid.length / 8));
  valid.forEach((v, i) => v && (b[i >> 3] |= 1 << (i & 7)));
  return b;
};

function batchOf(fields: { name: string; data: Data; typeName?: string }[]): RecordBatch {
  const schema = new Schema(fields.map((f) => new Field(f.name, f.data.type, true, new Map([["q:typeName", f.typeName ?? String(f.data.type)]]))));
  return new RecordBatch(schema, makeData({ type: new Struct(schema.fields), length: fields[0].data.length, nullCount: 0, children: fields.map((f) => f.data) }));
}

/** Decimal128 words for unscaled integers (two's complement, little-endian 32-bit words). */
function decimalWords(values: bigint[]): Uint32Array {
  const w = new Uint32Array(values.length * 4);
  values.forEach((v, i) => {
    let u = v < 0n ? (1n << 128n) + v : v;
    for (let k = 0; k < 4; k++) {
      w[i * 4 + k] = Number(u & 0xffffffffn);
      u >>= 32n;
    }
  });
  return w;
}

const colOf = (b: RecordBatch, name: string) => buildColumns(b.schema).find((c) => c.name === name)!;

describe("numbers for charts (Phase C1)", () => {
  it("int and float columns; NULL is NaN", () => {
    const b = batchOf([
      { name: "i", data: vectorFromArray([1, null, 3], new Int32()).data[0] },
      { name: "f", data: vectorFromArray([1.5, 2.5, null], new Float64()).data[0] },
    ]);
    expect([...numericChunk(b.getChild("i")!, colOf(b, "i")).values].map((v) => (Number.isNaN(v) ? null : v))).toEqual([1, null, 3]);
    expect([...numericChunk(b.getChild("f")!, colOf(b, "f")).values].map((v) => (Number.isNaN(v) ? null : v))).toEqual([1.5, 2.5, null]);
  });

  it("bigint converts to a double and warns when a value is beyond 2^53", () => {
    const safe = makeData({ type: new Int64(), length: 2, nullCount: 0, data: BigInt64Array.from([5n, -(2n ** 52n)]) });
    const big = makeData({ type: new Int64(), length: 2, nullCount: 0, data: BigInt64Array.from([1n, 2n ** 60n + 1n]) });
    const b = batchOf([{ name: "safe", data: safe, typeName: "bigint" }, { name: "big", data: big, typeName: "bigint" }]);
    const s = numericChunk(b.getChild("safe")!, colOf(b, "safe"));
    expect([...s.values]).toEqual([5, -(2 ** 52)]);
    expect(s.lossy).toBe(false);
    const g = numericChunk(b.getChild("big")!, colOf(b, "big"));
    expect(g.values[1]).toBe(2 ** 60);
    expect(g.lossy).toBe(true);
  });

  it("decimal: scaled exactly within 2^53 (negatives too); beyond it approximately, with the warning", () => {
    const type = new Decimal(2, 38, 128);
    const words = decimalWords([12345n, -995n, 0n, 10n ** 30n]);
    const d = makeData({ type, length: 4, nullCount: 1, nullBitmap: bitmap([true, true, false, true]), data: words });
    const b = batchOf([{ name: "amount", data: d, typeName: "decimal" }]);
    const c = numericChunk(b.getChild("amount")!, colOf(b, "amount"));
    expect(c.values[0]).toBe(123.45);
    expect(c.values[1]).toBe(-9.95);
    expect(Number.isNaN(c.values[2])).toBe(true);
    expect(c.values[3]).toBeCloseTo(1e28, -15);
    expect(c.lossy).toBe(true);
    const small = makeData({ type, length: 1, nullCount: 0, data: decimalWords([-(2n ** 50n)]) });
    const b2 = batchOf([{ name: "amount", data: small, typeName: "decimal" }]);
    const neg = numericChunk(b2.getChild("amount")!, colOf(b2, "amount"));
    expect(neg.lossy).toBe(false);
    expect([...neg.values]).toEqual([-(2 ** 50) / 100]);
  });

  it("timestamps (µs) and dates are epoch milliseconds and mark the column temporal", () => {
    const ts = makeData({ type: new TimestampMicrosecond(), length: 2, nullCount: 1, nullBitmap: bitmap([true, false]), data: BigInt64Array.from([1_700_000_000_123_000n, 0n]) });
    const day = makeData({ type: new DateDay(), length: 2, nullCount: 0, data: Int32Array.from([19_000, 0]) });
    const b = batchOf([{ name: "ts", data: ts, typeName: "datetime2" }, { name: "d", data: day, typeName: "date" }]);
    const t = numericChunk(b.getChild("ts")!, colOf(b, "ts"));
    expect(t.temporal).toBe(true);
    expect(t.values[0]).toBe(1_700_000_000_123);
    expect(Number.isNaN(t.values[1])).toBe(true);
    const d = numericChunk(b.getChild("d")!, colOf(b, "d"));
    expect(d.temporal).toBe(true);
    expect([...d.values]).toEqual([19_000 * 86_400_000, 0]);
  });

  it("booleans are 1 and 0", () => {
    const b = batchOf([{ name: "ok", data: vectorFromArray([true, false, null], new Bool()).data[0] }]);
    expect([...numericChunk(b.getChild("ok")!, colOf(b, "ok")).values].map((v) => (Number.isNaN(v) ? null : v))).toEqual([1, 0, null]);
  });

  it("labels read exactly as the grid shows them", () => {
    const b = batchOf([
      { name: "s", data: vectorFromArray(["a", null], new Utf8()).data[0] },
      { name: "n", data: vectorFromArray([7, 8], new Int32()).data[0] },
    ]);
    const vectors = b.schema.fields.map((_, i) => b.getChildAt(i)!);
    expect(labelChunk(b.getChild("s")!, colOf(b, "s"), vectors)).toEqual(["a", null]);
    expect(labelChunk(b.getChild("n")!, colOf(b, "n"), vectors)).toEqual(["7", "8"]);
  });
});

describe("dynamically typed columns for charts (Phase D0f, SQLite)", () => {
  const dyn = (cells: DynamicCell[]) => makeBatch(dynamicColumn("v", cells));
  const chunk = (b: RecordBatch) => {
    const col = colOf(b, "v");
    return numericChunk(b.getChildAt(col.field)!, col, b.getChildAt(col.classField!)!);
  };

  it("integer and real cells parse from their text; NULL, text and blob cells are NaN", () => {
    const c = chunk(dyn([[1, "42"], [2, "1.5"], [0, null], [3, "7"], [4, "00ff"], [2, "-2E+3"]]));
    expect([...c.values]).toEqual([42, 1.5, NaN, NaN, NaN, -2000]);
    expect(c).toMatchObject({ lossy: false, temporal: false });
  });

  it("an integer beyond 2^53 is drawn approximately and says so; a large real does not", () => {
    expect(chunk(dyn([[1, "9223372036854775807"]])).lossy).toBe(true);
    expect(chunk(dyn([[2, "1e300"]])).lossy).toBe(false);
  });

  it("the worker store reads a forwarded dynamic column by class, and labels it by its text", () => {
    const store = new ChartColumns();
    store.reset([{ name: "v", num: true, label: false }, { name: "k", num: false, label: true }]);
    const [v, , cls] = dynamicColumn("v", [[1, "3"], [2, "4.5"], [0, null]]);
    const [k, , kcls] = dynamicColumn("k", [[3, "a"], [4, "0a"], [1, "1"]]);
    store.append(tableToIPC(new Table([makeBatch([v, cls, k, kcls])]), "stream"));
    const input = store.input({ kind: "bar", x: "k", y: ["v"], series: null, agg: "sum" });
    expect(input.x).toEqual({ type: "cat", values: ["a", "0a", "1"] });
    expect([...input.ys[0].values]).toEqual([3, 4.5, NaN]);
  });
});

describe("the chart worker's column store", () => {
  const ipc = (b: RecordBatch) => tableToIPC(new Table([b]), "stream");
  const part = (offset: number, regions: (string | null)[]) =>
    batchOf([
      { name: "region", data: vectorFromArray(regions, new Utf8()).data[0], typeName: "nvarchar" },
      { name: "sales", data: vectorFromArray(regions.map((_, i) => offset + i), new Int32()).data[0], typeName: "int" },
    ]);

  it("appends batches, concatenates on demand and feeds the computation", () => {
    const store = new ChartColumns();
    store.reset([{ name: "region", num: false, label: true }, { name: "sales", num: true, label: false }]);
    store.append(ipc(part(0, ["n", "s"])));
    store.append(ipc(part(10, ["n", null])));
    expect(store.rows).toBe(4);
    const spec = { kind: "bar" as const, x: "region", y: ["sales"], series: null, agg: "sum" as const };
    const d = computeChart(spec, store.input(spec));
    expect(d).toMatchObject({ shape: "category", categories: ["n", "s"], meta: { nullsSkipped: 1, plotted: 3 } });
    expect([...(d as { series: { values: Float64Array }[] }).series[0].values]).toEqual([10, 1]);
    // More rows after a computation: the concatenation is rebuilt.
    store.append(ipc(part(100, ["s"])));
    expect([...(computeChart(spec, store.input(spec)) as { series: { values: Float64Array }[] }).series[0].values]).toEqual([10, 101]);
  });

  it("refuses a column it was not asked to extract", () => {
    const store = new ChartColumns();
    store.reset([{ name: "region", num: false, label: true }]);
    store.append(ipc(part(0, ["n"])));
    expect(() => store.input({ kind: "bar", x: "region", y: ["sales"], agg: "sum" })).toThrow(/not extracted/);
  });
});

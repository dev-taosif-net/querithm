import { describe, it, expect, vi } from "vitest";
import {
  Binary, Bool, Decimal, Field, Float32, Float64, Int16, Int32, makeData, RecordBatch, Schema, Struct, TimeMicrosecond, TimestampMicrosecond, Type, Utf8,
  vectorFromArray, type Data, type DataType,
} from "apache-arrow";
import { ArrowResultSet, buildColumns, classBit, exactNumber, ExecutionData, numericTextKey, shortestFloat32, shortestFloat32Uncached, StorageClass, __test } from "../model";
import { decimalData, dynamicColumn, makeBatch, utcMicros, type DynamicCell } from "./fixtures";

const { decimalToString, formatMicros, formatTimeMicros } = __test;

interface ColSpec {
  name: string;
  type: DataType;
  values?: unknown[];
  /** Prebuilt column data (for types vectorFromArray cannot build from plain values). */
  data?: Data;
  meta?: Record<string, string>;
}

function batch(cols: ColSpec[]): RecordBatch {
  const fields = cols.map((c) => new Field(c.name, c.type, true, new Map(Object.entries(c.meta ?? {}))));
  const children = cols.map((c) => c.data ?? vectorFromArray(c.values!, c.type).data[0]);
  const length = children[0].length;
  return new RecordBatch(new Schema(fields), makeData({ type: new Struct(fields), length, nullCount: 0, children }));
}

const people = (ids: (number | null)[], names: (string | null)[]) =>
  batch([{ name: "id", type: new Int32(), values: ids }, { name: "name", type: new Utf8(), values: names }]);

/** Micro-second timestamps (nullable via `null`). */
function micros(values: (bigint | null)[]): Data {
  const v = vectorFromArray(values.map((x) => (x == null ? null : 0)), new Int32()); // borrow a validity bitmap
  return makeData({ type: new TimestampMicrosecond(), length: values.length, nullCount: v.nullCount, nullBitmap: v.data[0].nullBitmap, data: BigInt64Array.from(values.map((x) => x ?? 0n)) });
}

/** -n as a 128-bit two's-complement decimal (4 little-endian 32-bit words). */
function negWords(n: bigint): Uint32Array {
  const v = (1n << 128n) - n;
  return Uint32Array.from([0, 1, 2, 3].map((k) => Number((v >> BigInt(32 * k)) & 0xffffffffn)));
}

describe("model: decimals", () => {
  it("scale places the decimal point", () => {
    expect(decimalToString(Uint32Array.from([12345, 0, 0, 0]), 2)).toBe("123.45");
    expect(decimalToString(Uint32Array.from([12345, 0, 0, 0]), 0)).toBe("12345");
  });
  it("pads with leading zeros when the scale exceeds the digits", () => {
    expect(decimalToString(Uint32Array.from([5, 0, 0, 0]), 3)).toBe("0.005");
    expect(decimalToString(Uint32Array.from([0, 0, 0, 0]), 2)).toBe("0.00");
  });
  it("negative values (two's complement)", () => {
    expect(decimalToString(negWords(12345n), 2)).toBe("-123.45");
    expect(decimalToString(negWords(5n), 3)).toBe("-0.005");
  });
  it("values wider than 64 bits stay exact", () => {
    expect(decimalToString(Uint32Array.from([0, 0, 1, 0]), 0)).toBe((1n << 64n).toString());
  });
});

describe("model: timestamps and times", () => {
  it("epoch, with trailing fractional zeros removed", () => {
    expect(formatMicros(0n, null, null)).toBe("1970-01-01 00:00:00");
    expect(formatMicros(1_500n, null, null)).toBe("1970-01-01 00:00:00.0015");
    expect(formatMicros(120_000n, null, null)).toBe("1970-01-01 00:00:00.12");
  });
  it("a year before 1 AD prints with its sign and four digits (Oracle BC dates, ISO numbering: 0 = 1 BC)", () => {
    expect(formatMicros(utcMicros(-3999, 3, 4, 5, 6, 7), null, null)).toBe("-3999-03-04 05:06:07");
    expect(formatMicros(utcMicros(-45, 12, 31, 23, 59, 59) + 250_000n, null, null)).toBe("-0045-12-31 23:59:59.25");
    expect(formatMicros(utcMicros(0, 1, 1), null, null)).toBe("0000-01-01 00:00:00");
    expect(formatMicros(utcMicros(5, 6, 7), null, null)).toBe("0005-06-07 00:00:00");
  });
  it("a timezone-aware column without an offset prints +00:00", () => expect(formatMicros(0n, null, "UTC")).toBe("1970-01-01 00:00:00 +00:00"));
  it("datetimeoffset shifts to local time and prints the offset", () => {
    expect(formatMicros(0n, 330, null)).toBe("1970-01-01 05:30:00 +05:30");
    expect(formatMicros(0n, -480, "UTC")).toBe("1969-12-31 16:00:00 -08:00");
  });
  it("one microsecond before the epoch borrows from the millisecond", () => expect(formatMicros(-1n, null, null)).toBe("1969-12-31 23:59:59.999999"));
  it("years below 1000 are zero-padded", () => {
    // setUTCFullYear, because Date.UTC maps years 0-99 to 19xx.
    const year1 = BigInt(new Date(Date.UTC(2000, 0, 1)).setUTCFullYear(1)) * 1000n;
    expect(formatMicros(year1, null, null)).toBe("0001-01-01 00:00:00");
  });
  it("time of day", () => {
    expect(formatTimeMicros(0n)).toBe("00:00:00");
    expect(formatTimeMicros(3_661_500_000n)).toBe("01:01:01.5");
    expect(formatTimeMicros(86_399_999_999n)).toBe("23:59:59.999999");
  });
  it("MySQL / MariaDB TIME durations: the sign once in front, hours past 24, the full range (L11)", () => {
    expect(formatTimeMicros(-9_000_000_000n)).toBe("-02:30:00"); // was -2:-30:00
    expect(formatTimeMicros(-1n)).toBe("-00:00:00.000001");
    expect(formatTimeMicros(-500_000n)).toBe("-00:00:00.5");
    expect(formatTimeMicros(90_000_000_000n)).toBe("25:00:00");
    const max = ((838n * 3600n + 59n * 60n + 59n) * 1_000_000n) + 999_999n;
    expect(formatTimeMicros(max)).toBe("838:59:59.999999");
    expect(formatTimeMicros(-max)).toBe("-838:59:59.999999");
  });
  it("a Time column shows and sorts negative durations by value", () => {
    const rs = new ArrowResultSet(0);
    const data = makeData({ type: new TimeMicrosecond(), length: 3, nullCount: 0, data: BigInt64Array.from([3_600_000_000n, -9_000_000_000n, 0n]) });
    rs.append(batch([{ name: "t", type: new TimeMicrosecond(), data }]));
    expect([0, 1, 2].map((r) => rs.text(r, 0))).toEqual(["01:00:00", "-02:30:00", "00:00:00"]);
    rs.setSort(0, "asc");
    expect([0, 1, 2].map((r) => rs.text(r, 0))).toEqual(["-02:30:00", "00:00:00", "01:00:00"]);
  });
});

describe("model: numbers sent as exact text and Decimal128 sort keys (M11)", () => {
  const cmp = (a: string, b: string) => {
    const ka = numericTextKey(a);
    const kb = numericTextKey(b);
    return ka < kb ? -1 : ka > kb ? 1 : 0;
  };
  it("orders decimal text like the numbers: length, sign, digits, no float conversion", () => {
    const shuffled = [
      "10", "NaN", "9", "-0.05", "1234567890123456789012345678901234567891", "-0.5", "0", "Infinity", ".5", "-12.30", "0.000001",
      "-1000000000000000000000000000000000000001", "999", "1e3", "-12.3001", "+7", "-Infinity", "1234567890123456789012345678901234567890", "0.00",
    ];
    expect([...shuffled].sort(cmp)).toEqual([
      "-Infinity", "-1000000000000000000000000000000000000001", "-12.3001", "-12.30", "-0.5", "-0.05", "0", "0.00", "0.000001", ".5", "+7", "9", "10",
      "999", "1e3", "1234567890123456789012345678901234567890", "1234567890123456789012345678901234567891", "Infinity", "NaN",
    ]);
  });
  it("equal values have equal keys whatever their spelling; text that is not a number sorts after every number", () => {
    expect(numericTextKey("-0")).toBe(numericTextKey("0.000"));
    expect(numericTextKey("12.50")).toBe(numericTextKey("12.5"));
    expect(numericTextKey("1e2")).toBe(numericTextKey("100"));
    expect(cmp("NaN", "abc")).toBe(-1);
    expect(cmp("abc", "abd")).toBe(-1);
  });
  it("a q:numeric text column is a number column: right-aligned kind and numeric order", () => {
    const rs = new ArrowResultSet(0);
    rs.append(batch([
      { name: "n", type: new Utf8(), values: ["10", "9", "-0.5", "-0.05", null, "123456789012345678901234567890123456789012"], meta: { "q:numeric": "1" } },
      { name: "s", type: new Utf8(), values: ["10", "9", "-0.5", "-0.05", null, "x"] },
    ]));
    expect(rs.columns.map((c) => c.kind)).toEqual(["number", "text"]);
    rs.setSort(0, "asc");
    expect([0, 1, 2, 3, 4, 5].map((r) => rs.text(r, 0))).toEqual(["-0.5", "-0.05", "9", "10", "123456789012345678901234567890123456789012", null]);
    rs.setSort(1, "asc"); // a text column still sorts as text
    expect([0, 1, 2, 3].map((r) => rs.text(r, 1))).toEqual(["-0.05", "-0.5", "10", "9"]);
    expect(rs.text(0, 0)).toBe("-0.05"); // the text is shown as sent
  });
  it("a dynamic column keeps its per-cell classes even if marked", () => {
    const cols = buildColumns(batch([{ name: "v", type: new Utf8(), values: ["1"], meta: { "q:dynamic": "1", "q:numeric": "1" } }]).schema);
    expect(cols[0].kind).toBe("text");
  });
  it("Decimal128 values that differ beyond 15 digits sort exactly (BigInt keys, not Number)", () => {
    const rs = new ArrowResultSet(0);
    const type = new Decimal(2, 38, 128);
    rs.append(batch([{ name: "d", type, data: decimalData([12345678901234567891n, 12345678901234567890n, -12345678901234567891n, -12345678901234567890n, null], 2) }]));
    rs.setSort(0, "asc");
    expect([0, 1, 2, 3, 4].map((r) => rs.text(r, 0))).toEqual([
      "-123456789012345678.91", "-123456789012345678.90", "123456789012345678.90", "123456789012345678.91", null,
    ]);
    rs.setSort(0, "desc");
    expect(rs.text(0, 0)).toBe("123456789012345678.91");
  });
});

describe("model: Float32 shows its shortest round-trip decimal (L12)", () => {
  it("shortestFloat32 reads back as the same float", () => {
    for (const v of [0.1, 3.14159, 16777216, -2.5, 1e-45, 3.4028235e38, 0.3, 123.456]) {
      const f = Math.fround(v);
      expect(Math.fround(shortestFloat32(f))).toBe(f);
    }
    expect(shortestFloat32(Math.fround(0.1))).toBe(0.1);
    expect(shortestFloat32(Math.fround(0.3))).toBe(0.3);
    expect(shortestFloat32(Number.NaN)).toBeNaN();
    expect(shortestFloat32(0)).toBe(0);
  });
  it("its cache answers exactly as the search does, for any float32, seen once or many times (W9)", () => {
    const f = new Float32Array(1);
    const bits = new Uint32Array(f.buffer);
    let seed = 12345;
    const values: number[] = [];
    for (let i = 0; i < 50_000; i++) {
      seed = (Math.imul(seed, 1103515245) + 12345) >>> 0;
      bits[0] = seed;
      if (Number.isFinite(f[0])) values.push(f[0]);
    }
    // Colliding slots, repeats (a redraw), negative zero, subnormals, the extremes.
    values.push(-0, 1e-45, -1e-45, 3.4028234663852886e38, Math.fround(0.1), Math.fround(19.99));
    const differ: number[] = [];
    for (let round = 0; round < 3; round++) for (const v of values) if (!Object.is(shortestFloat32(v), shortestFloat32Uncached(v))) differ.push(v);
    expect(differ).toEqual([]);
    expect(shortestFloat32(Math.fround(19.99))).toBe(19.99);
  });
  it("the grid text and copied text use it; Float64 is unchanged", () => {
    const rs = new ArrowResultSet(0);
    rs.append(batch([
      { name: "r", type: new Float32(), values: [0.1, 3.14159, 16777216, -2.5, null] },
      { name: "d", type: new Float64(), values: [0.1, 0.30000000000000004, 3, -2.5, null] },
    ]));
    expect(rs.columns[0]).toMatchObject({ kind: "number", single: true });
    expect(rs.columns[1].single).toBeUndefined();
    expect([0, 1, 2, 3, 4].map((r) => rs.text(r, 0))).toEqual(["0.1", "3.14159", "16777216", "-2.5", null]); // was 0.10000000149011612
    expect([0, 1].map((r) => rs.text(r, 1))).toEqual(["0.1", "0.30000000000000004"]);
    expect(rs.copyText([0, 1], [0], false)).toBe("0.1\n3.14159");
  });
});

describe("model: buildColumns", () => {
  it("reads sidecar metadata and skips hidden helper columns, keeping physical field indices", () => {
    const b = batch([
      { name: "Body", type: new Utf8(), values: ["x"], meta: { "q:typeName": "nvarchar(max)", "q:ordinal": "3", "q:nullable": "0", "q:pk": "1", "q:source": "dbo.T.Body", "q:fk": "dbo.U.Id" } },
      { name: "Body__truncated", type: new Bool(), values: [true], meta: { "q:hidden": "1" } },
      { name: "Note", type: new Utf8(), values: ["y"] },
    ]);
    const cols = buildColumns(b.schema);
    expect(cols.map((c) => c.name)).toEqual(["Body", "Note"]);
    expect(cols[0]).toMatchObject({ typeName: "nvarchar(max)", ordinal: 3, nullable: false, pk: true, source: "dbo.T.Body", fk: "dbo.U.Id", flagField: 1, field: 0 });
    expect(cols[1]).toMatchObject({ field: 2, nullable: null, pk: false, source: null, fk: null, flagField: undefined });
    // Without q:ordinal the ordinal is the visible column index, not the physical field index.
    expect(cols[1].ordinal).toBe(1);
  });
  it("maps Arrow types to cell kinds", () => {
    const b = batch([
      { name: "i", type: new Int32(), values: [1] },
      { name: "f", type: new Float64(), values: [1] },
      { name: "b", type: new Bool(), values: [true] },
      { name: "s", type: new Utf8(), values: ["a"] },
      { name: "x", type: new Binary(), values: [new Uint8Array([1])] },
      { name: "t", type: new TimestampMicrosecond(), data: micros([0n]) },
    ]);
    expect(buildColumns(b.schema).map((c) => c.kind)).toEqual(["number", "number", "bool", "text", "binary", "temporal"]);
  });
});

describe("model: ArrowResultSet", () => {
  const twoBatches = () => {
    const rs = new ArrowResultSet(0);
    rs.append(people([3, null, 1], ["Cara", "bob", null]));
    rs.append(people([2, 5], ["Abe", "BOBBY"]));
    return rs;
  };

  it("takes its schema from the first batch and counts rows across batches", () => {
    const rs = twoBatches();
    expect(rs.columns.map((c) => c.name)).toEqual(["id", "name"]);
    expect(rs.rowCount).toBe(5);
    expect(rs.starts).toEqual([0, 3]);
    expect(rs.text(3, 1)).toBe("Abe");
    expect(rs.text(0, 1)).toBe("Cara"); // jump back to batch 0 after reading batch 1
    expect(rs.text(4, 0)).toBe("5");
  });
  it("SQL NULL is null in text() and NULL in copied text", () => {
    const rs = twoBatches();
    expect(rs.text(1, 0)).toBeNull();
    expect(rs.copyText([1, 2], [0, 1], true)).toBe("id\tname\nNULL\tbob\n1\tNULL");
    expect(rs.copyText([0], [1], false)).toBe("Cara");
  });
  it("sort ascending and descending keeps NULLs last either way", () => {
    const rs = twoBatches();
    rs.setSort(0, "asc");
    expect(Array.from({ length: rs.visibleRows }, (_, r) => rs.text(r, 0))).toEqual(["1", "2", "3", "5", null]);
    rs.setSort(0, "desc");
    expect(Array.from({ length: rs.visibleRows }, (_, r) => rs.text(r, 0))).toEqual(["5", "3", "2", "1", null]);
    rs.setSort(null);
    expect(rs.physicalRow(0)).toBe(0);
  });
  it("sort is stable for equal keys", () => {
    const rs = new ArrowResultSet(0);
    rs.append(people([1, 2, 3, 4], ["b", "a", "b", "a"]));
    rs.setSort(1, "asc");
    expect([0, 1, 2, 3].map((r) => rs.physicalRow(r))).toEqual([1, 3, 0, 2]);
  });
  it("filter is a case-insensitive substring over every column and never matches NULL", () => {
    const rs = twoBatches();
    rs.setFilter("BoB");
    expect(rs.visibleRows).toBe(2);
    expect([0, 1].map((r) => rs.text(r, 1))).toEqual(["bob", "BOBBY"]);
    rs.setFilter("null");
    expect(rs.visibleRows).toBe(0);
    rs.setFilter("");
    expect(rs.visibleRows).toBe(5);
  });
  it("filter can be limited to one column or several; none chosen means every column", () => {
    const rs = new ArrowResultSet(0);
    rs.append(people([12, 3, 1], ["x1", "a12", "b"]));
    rs.setFilter("1", [0]); // id only
    expect(rs.visibleRows).toBe(2);
    expect([0, 1].map((r) => rs.text(r, 0))).toEqual(["12", "1"]);
    rs.setFilter("1", [1]); // name only
    expect([0, 1].map((r) => rs.text(r, 1))).toEqual(["x1", "a12"]);
    rs.setFilter("12", [1, 0]); // several, in any order
    expect(rs.filterColumns).toEqual([0, 1]);
    expect(rs.visibleRows).toBe(2);
    rs.setFilter("b", []);
    expect(rs.filterColumns).toBeNull();
    expect(rs.visibleRows).toBe(1);
  });
  it("changing only the text keeps the chosen columns; changing the columns re-filters with the current text", () => {
    const rs = new ArrowResultSet(0);
    rs.append(people([1, 2], ["2", "x"]));
    rs.setFilter("2", [1]);
    expect(rs.visibleRows).toBe(1);
    rs.setFilter("x");
    expect(rs.filterColumns).toEqual([1]);
    expect(rs.text(0, 1)).toBe("x");
    rs.setFilter("2", null);
    expect(rs.visibleRows).toBe(2);
  });
  it("viewVersion changes whenever rows are re-filtered or re-sorted (the grid clears its selection)", () => {
    const rs = twoBatches();
    const v0 = rs.viewVersion;
    rs.setFilter("b");
    rs.setSort(1, "asc");
    rs.setFilter("b", [1]);
    expect(rs.viewVersion).toBe(v0 + 3);
    rs.append(people([9], ["z"]));
    expect(rs.viewVersion).toBe(v0 + 3); // appending leaves existing view rows in place
  });
  it("filter and sort combine", () => {
    const rs = twoBatches();
    rs.setFilter("b");
    rs.setSort(1, "desc");
    expect(Array.from({ length: rs.visibleRows }, (_, r) => rs.text(r, 1))).toEqual(["bob", "BOBBY", "Abe"]);
  });
  it("rows appended under a sort or filter join the view only when the result set finishes", () => {
    const rs = new ArrowResultSet(0);
    rs.append(people([2], ["x"]));
    rs.setSort(0, "asc");
    rs.append(people([1], ["y"]));
    expect(rs.visibleRows).toBe(1);
    rs.finish(false);
    expect(rs.visibleRows).toBe(2);
    expect(rs.text(0, 0)).toBe("1");
    expect(rs.complete).toBe(true);
  });
  it("without a sort or filter, appended rows are visible immediately", () => {
    const rs = twoBatches();
    rs.append(people([9], ["z"]));
    expect(rs.visibleRows).toBe(6);
  });
  it("notifies subscribers on every change and stops after unsubscribe", () => {
    const rs = new ArrowResultSet(0);
    const cb = vi.fn();
    const off = rs.subscribe(cb);
    rs.append(people([1], ["a"])); // schema + batch
    expect(cb).toHaveBeenCalledTimes(2);
    expect(rs.getVersion()).toBe(2);
    off();
    rs.finish(true);
    expect(cb).toHaveBeenCalledTimes(2);
    expect(rs.truncated).toBe(true);
  });
  it("a second schema is ignored", () => {
    const rs = twoBatches();
    const v = rs.version;
    rs.setSchema(batch([{ name: "other", type: new Utf8(), values: ["a"] }]).schema);
    expect(rs.columns.map((c) => c.name)).toEqual(["id", "name"]);
    expect(rs.version).toBe(v);
  });
  it("truncation flags come from the hidden __truncated column", () => {
    const rs = new ArrowResultSet(0);
    rs.append(batch([
      { name: "Body", type: new Utf8(), values: ["long…", "short"] },
      { name: "Body__truncated", type: new Bool(), values: [true, false], meta: { "q:hidden": "1" } },
      { name: "n", type: new Int32(), values: [1, 2] },
    ]));
    expect(rs.isTruncated(0, 0)).toBe(true);
    expect(rs.isTruncated(1, 0)).toBe(false);
    expect(rs.isTruncated(0, 1)).toBe(false);
  });
  it("formats floats, booleans and binary", () => {
    const rs = new ArrowResultSet(0);
    const big = new Uint8Array(65).fill(0xab);
    rs.append(batch([
      { name: "f", type: new Float64(), values: [3, 2.5] },
      { name: "b", type: new Bool(), values: [true, false] },
      { name: "x", type: new Binary(), values: [new Uint8Array([0x0a, 0xff]), big] },
    ]));
    expect([rs.text(0, 0), rs.text(1, 0)]).toEqual(["3", "2.5"]);
    expect([rs.text(0, 1), rs.text(1, 1)]).toEqual(["1", "0"]);
    expect(rs.text(0, 2)).toBe("0x0AFF");
    expect(rs.text(1, 2)).toBe(`0x${"AB".repeat(64)}…`);
  });
  it("timestamps use the companion __offset column and sort by raw value", () => {
    const rs = new ArrowResultSet(0);
    rs.append(batch([
      { name: "at", type: new TimestampMicrosecond(), data: micros([3_600_000_000n, 0n, null]) },
      { name: "at__offset", type: new Int16(), values: [60, null, null], meta: { "q:hidden": "1" } },
    ]));
    expect(rs.columns[0]).toMatchObject({ arrowTypeId: Type.Timestamp, offsetField: 1 });
    expect(rs.text(0, 0)).toBe("1970-01-01 02:00:00 +01:00");
    expect(rs.text(1, 0)).toBe("1970-01-01 00:00:00");
    expect(rs.text(2, 0)).toBeNull();
    rs.setSort(0, "asc");
    expect([0, 1, 2].map((r) => rs.physicalRow(r))).toEqual([1, 0, 2]);
  });
});

describe("model: dynamically typed columns (Phase D0f, SQLite)", () => {
  const { Null, Integer, Real, Text, Blob } = StorageClass;
  /** id int plus a dynamic column v, one row per cell. */
  const dynamicSet = (cells: DynamicCell[]) => {
    const rs = new ArrowResultSet(0);
    rs.append(makeBatch([{ name: "id", type: new Int32(), values: cells.map((_, i) => i), meta: { "q:typeName": "INTEGER" } }, ...dynamicColumn("v", cells, 1)]));
    rs.finish(false);
    return rs;
  };
  const column = (rs: ArrowResultSet, col: number) => Array.from({ length: rs.visibleRows }, (_, r) => rs.text(r, col));

  it("keeps the __class sibling hidden and points the column at it; other columns have none", () => {
    const rs = dynamicSet([[Integer, "1"], [Text, "a"]]);
    expect(rs.columns.map((c) => c.name)).toEqual(["id", "v"]);
    expect(rs.columns[1]).toMatchObject({ field: 1, flagField: 2, classField: 3, kind: "text" });
    expect(rs.columns[0].classField).toBeUndefined();
    expect(rs.copyText([0, 1], [0, 1], true)).toBe("id\tv\n0\t1\n1\ta");
  });

  it("finds a renamed sibling through q:of, and ignores a class column without q:dynamic", () => {
    const [v, flag, cls] = dynamicColumn("v", [[Integer, "1"]]);
    const renamed = buildColumns(makeBatch([v, flag, { ...cls, name: "v__class1" }]).schema);
    expect(renamed.map((c) => c.name)).toEqual(["v"]);
    expect(renamed[0].classField).toBe(2);
    const plain = buildColumns(makeBatch([{ ...v, meta: { "q:typeName": "TEXT" } }, flag, cls]).schema);
    expect(plain[0].classField).toBeUndefined();
  });

  it("reads each cell's storage class; a non-dynamic column has none", () => {
    const rs = dynamicSet([[Null, null], [Integer, "7"], [Real, "1.5"], [Text, "x"], [Blob, "0aff"]]);
    expect([0, 1, 2, 3, 4].map((r) => rs.storageClass(r, 1))).toEqual([Null, Integer, Real, Text, Blob]);
    expect(rs.storageClass(0, 0)).toBeNull();
    rs.setSort(1, "desc"); // view rows follow the sort
    expect(rs.storageClass(0, 1)).toBe(Blob);
  });

  it("sorts NULL < numbers (integers and reals by value, big integers exactly) < text < blob, and desc reverses it", () => {
    const rs = dynamicSet([
      [Text, "abc"], [Integer, "10"], [Blob, "0a"], [Real, "2.5"], [Null, null], [Integer, "-3"], [Text, "Abc"], [Real, "1e19"],
      [Integer, "9223372036854775807"], [Blob, "00ff"], [Integer, "2"], [Real, "1E+3"],
      // 2^53 + 1 rounds to 2^53 as a double: compared exactly, the integer is the larger, although it comes first.
      [Integer, "9007199254740993"], [Real, "9007199254740992"],
    ]);
    const asc = [null, "-3", "2", "2.5", "10", "1E+3", "9007199254740992", "9007199254740993", "9223372036854775807", "1e19", "Abc", "abc", "00ff", "0a"];
    rs.setSort(1, "asc");
    expect(column(rs, 1)).toEqual(asc);
    rs.setSort(1, "desc");
    expect(column(rs, 1)).toEqual([...asc].reverse());
  });

  it("equal numbers keep row order; an integer equal to a real sorts as equal", () => {
    const rs = dynamicSet([[Real, "2"], [Integer, "2"], [Integer, "1"], [Real, "2"]]);
    rs.setSort(1, "asc");
    expect([0, 1, 2, 3].map((r) => rs.physicalRow(r))).toEqual([2, 0, 1, 3]);
  });

  it("filters and shows the text: numbers as their digits, blobs as their hex", () => {
    const rs = dynamicSet([[Integer, "42"], [Text, "forty-two"], [Blob, "00ff42"], [Null, null], [Real, "4.2"]]);
    expect(column(rs, 1)).toEqual(["42", "forty-two", "00ff42", null, "4.2"]);
    rs.setFilter("42", [1]);
    expect(column(rs, 1)).toEqual(["42", "00ff42"]);
    rs.setFilter("FORTY", [1]);
    expect(column(rs, 1)).toEqual(["forty-two"]);
  });

  it("a text column that is not dynamic sorts as before: text order, NULLs last either way", () => {
    const rs = new ArrowResultSet(0);
    rs.append(makeBatch([{ name: "s", type: new Utf8(), values: ["10", null, "2", "abc"], meta: { "q:typeName": "nvarchar" } }]));
    rs.setSort(0, "asc");
    expect(column(rs, 0)).toEqual(["10", "2", "abc", null]);
    rs.setSort(0, "desc");
    expect(column(rs, 0)).toEqual(["abc", "2", "10", null]);
    expect(rs.classesSeen(0)).toBe(0);
  });

  it("classesSeen collects the classes batch by batch", () => {
    const rs = new ArrowResultSet(0);
    rs.append(makeBatch(dynamicColumn("v", [[Integer, "1"], [Null, null], [Real, "0.5"]])));
    expect(rs.classesSeen(0)).toBe(classBit(Null) | classBit(Integer) | classBit(Real));
    rs.append(makeBatch(dynamicColumn("v", [[Text, "x"]])));
    expect(rs.classesSeen(0)).toBe(classBit(Null) | classBit(Integer) | classBit(Real) | classBit(Text));
  });

  it("exactNumber: integers within 2^53 and finite reals convert; big integers, NaN, infinities, text and blobs do not", () => {
    expect(exactNumber(Integer, "42")).toBe(42);
    expect(exactNumber(Integer, "-9007199254740991")).toBe(-9007199254740991);
    expect(exactNumber(Integer, "9007199254740993")).toBeNull();
    expect(exactNumber(Integer, "9223372036854775807")).toBeNull();
    expect(exactNumber(Real, "1.5")).toBe(1.5);
    expect(exactNumber(Real, "0.30000000000000004")).toBe(0.1 + 0.2);
    expect(exactNumber(Real, "1E+20")).toBe(1e20);
    expect(exactNumber(Real, "3")).toBe(3);
    for (const t of ["NaN", "Infinity", "-Infinity", "", " 1", "0x10"]) expect(exactNumber(Real, t)).toBeNull();
    expect(exactNumber(Text, "42")).toBeNull();
    expect(exactNumber(Blob, "00")).toBeNull();
    expect(exactNumber(null, "1")).toBeNull();
  });
});

describe("model: ExecutionData", () => {
  it("creates result sets on demand, ordered by index, and notifies once per new set", () => {
    const d = new ExecutionData("e1");
    const cb = vi.fn();
    d.subscribe(cb);
    const r2 = d.get(2);
    const r0 = d.get(0);
    expect(d.get(2)).toBe(r2);
    expect(d.resultSets).toEqual([r0, r2]);
    expect(cb).toHaveBeenCalledTimes(2);
  });
  it("totalRows sums every result set", () => {
    const d = new ExecutionData("e1");
    expect(d.totalRows).toBe(0);
    d.get(0).append(people([1, 2], ["a", "b"]));
    d.get(1).append(people([3], ["c"]));
    expect(d.totalRows).toBe(3);
  });
});

// Control-plane notifications and Arrow frames may arrive in either order.
it("only removes empty unfinished sets after the Arrow stream ends", () => {
  const data = new ExecutionData("review");
  const emptyComplete = data.get(0);
  emptyComplete.finish(false);
  const partial = data.get(1);
  partial.rowCount = 10000;
  data.get(2);
  data.finishStream();
  expect(data.streamEnded).toBe(true);
  expect(data.resultSets.map((r) => r.index)).toEqual([0, 1]);
  expect(partial.interrupted).toBe(true);
  expect(partial.complete).toBe(false);
});

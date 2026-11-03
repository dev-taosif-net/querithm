import { describe, it, expect } from "vitest";
import { Binary, Field, makeData, RecordBatch, Schema, Struct, Uint8, Utf8, vectorFromArray, type DataType } from "apache-arrow";
import { ArrowResultSet } from "../../results/model";
import { dataSideReady, diffTone, DIFF_DIFFERENT, DIFF_SOURCE_ONLY, DIFF_TARGET_ONLY, parseKey, sideToParams } from "../dataCompare";

describe("data compare: sides", () => {
  it("is ready with a table or a query", () => {
    expect(dataSideReady({ mode: "table" })).toBe(false);
    expect(dataSideReady({ mode: "table", profileId: "p", database: "db" })).toBe(false);
    expect(dataSideReady({ mode: "table", profileId: "p", database: "db", table: "dbo.t" })).toBe(true);
    expect(dataSideReady({ mode: "query", profileId: "p", database: "db", query: "  " })).toBe(false);
    expect(dataSideReady({ mode: "query", profileId: "p", database: "db", query: "SELECT 1 AS id" })).toBe(true);
  });

  it("splits schema.table at the first dot and sends queries as text", () => {
    expect(sideToParams({ mode: "table", profileId: "p", database: "db", table: "sales.order.lines" }, "s1")).toEqual({ sessionId: "s1", database: "db", schema: "sales", table: "order.lines" });
    expect(sideToParams({ mode: "query", profileId: "p", database: "db", query: " SELECT 1 AS id " }, "s1")).toEqual({ sessionId: "s1", database: "db", query: "SELECT 1 AS id" });
  });

  it("reads the key list, empty meaning the primary key", () => {
    expect(parseKey(" id , part ")).toEqual(["id", "part"]);
    expect(parseKey("  ")).toBeNull();
  });
});

describe("data compare: diff grid", () => {
  const changed = new Uint8Array([0b0000_0100, 0b0000_0001]); // compared columns 2 and 8 differ
  it("lights changed pairs on different rows", () => {
    expect(diffTone(DIFF_DIFFERENT, changed, { bit: 2, side: "source", isKind: false })).toBe("changed");
    expect(diffTone(DIFF_DIFFERENT, changed, { bit: 8, side: "target", isKind: false })).toBe("changed");
    expect(diffTone(DIFF_DIFFERENT, changed, { bit: 3, side: "source", isKind: false })).toBeNull();
    expect(diffTone(DIFF_DIFFERENT, changed, { bit: null, side: null, isKind: false })).toBeNull(); // key column
    expect(diffTone(DIFF_DIFFERENT, changed, { bit: null, side: null, isKind: true })).toBe("changed");
  });

  it("colours the side a one-sided row has and dims the other", () => {
    expect(diffTone(DIFF_SOURCE_ONLY, null, { bit: 0, side: "source", isKind: false })).toBe("added");
    expect(diffTone(DIFF_SOURCE_ONLY, null, { bit: 0, side: "target", isKind: false })).toBe("muted");
    expect(diffTone(DIFF_TARGET_ONLY, null, { bit: 0, side: "source", isKind: false })).toBe("muted");
    expect(diffTone(DIFF_TARGET_ONLY, null, { bit: null, side: null, isKind: false })).toBe("removed");
  });

  it("reads hidden fields of a diff frame and filters by kind", () => {
    const cols: { name: string; type: DataType; values: unknown[]; meta?: Record<string, string> }[] = [
      { name: "diff", type: new Utf8(), values: ["≠", "+ source", "− target"] },
      { name: "_diff_kind", type: new Uint8(), values: [1, 2, 3], meta: { "q:hidden": "1" } },
      { name: "_diff_mask", type: new Binary(), values: [new Uint8Array([1]), new Uint8Array([0]), new Uint8Array([0])], meta: { "q:hidden": "1" } },
      { name: "id", type: new Utf8(), values: ["1", "2", "3"] },
    ];
    const fields = cols.map((c) => new Field(c.name, c.type, true, new Map(Object.entries(c.meta ?? {}))));
    const children = cols.map((c) => vectorFromArray(c.values as never, c.type).data[0]);
    const rs = new ArrowResultSet(0);
    rs.append(new RecordBatch(new Schema(fields), makeData({ type: new Struct(fields), length: 3, nullCount: 0, children })));
    rs.finish(false);
    expect(rs.columns.map((c) => c.name)).toEqual(["diff", "id"]); // the bookkeeping fields stay hidden
    expect(rs.raw(1, 1)).toBe(2);
    expect(Array.from(rs.raw(0, 2) as Uint8Array)).toEqual([1]);
    rs.setFilter("+ source", [0]);
    expect(rs.visibleRows).toBe(1);
    expect(rs.raw(0, 1)).toBe(2); // view row 0 is the source-only row
  });
});

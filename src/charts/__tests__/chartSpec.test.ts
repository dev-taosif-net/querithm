import { describe, expect, it } from "vitest";
import { inferSpec, reconcileSpec, specColumns, specProblem, validateSpec, withKind, type ChartColumn } from "../chartSpec";

const col = (name: string, kind: ChartColumn["kind"], typeName = kind): ChartColumn => ({ name, kind, typeName });

describe("default spec inference (Phase C1)", () => {
  it("time + numbers → line over time, at most two value columns, ids left out", () => {
    const s = inferSpec([col("OrderId", "number"), col("CreatedAt", "temporal"), col("Qty", "number"), col("Amount", "number"), col("Ratio", "number")]);
    expect(s).toEqual({ kind: "line", x: "CreatedAt", y: ["Qty", "Amount"], series: null, agg: "none" });
  });
  it("text + number → bar of sums", () => {
    expect(inferSpec([col("Region", "text"), col("Sales", "number")])).toEqual({ kind: "bar", x: "Region", y: ["Sales"], series: null, agg: "sum" });
  });
  it("two numbers → scatter, the second number on y", () => {
    expect(inferSpec([col("height", "number"), col("weight", "number")])).toEqual({ kind: "scatter", x: "height", y: ["weight"], series: null, agg: "none" });
  });
  it("one number → histogram; one date → histogram of dates", () => {
    expect(inferSpec([col("price", "number")])).toMatchObject({ kind: "histogram", x: "price", bins: 20 });
    expect(inferSpec([col("born", "temporal")])).toMatchObject({ kind: "histogram", x: "born" });
  });
  it("text only → bar of row counts; booleans count as categories", () => {
    expect(inferSpec([col("status", "text")])).toEqual({ kind: "bar", x: "status", y: [], series: null, agg: "count" });
    expect(inferSpec([col("active", "bool"), col("n", "number")])).toMatchObject({ kind: "bar", x: "active", y: ["n"] });
  });
  it("only ids still chart (an id is better than nothing); binary alone does not", () => {
    expect(inferSpec([col("customer_id", "number"), col("name", "text")])).toMatchObject({ kind: "bar", x: "name", y: ["customer_id"] });
    expect(inferSpec([col("blob", "binary")])).toBeNull();
    expect(inferSpec([])).toBeNull();
  });
});

describe("spec validation (untrusted workspace and notebook files)", () => {
  it("keeps a good spec and ignores unknown fields", () => {
    expect(validateSpec({ kind: "bar", x: "a", y: ["b"], series: null, agg: "sum", extra: 1 })).toEqual({ kind: "bar", x: "a", y: ["b"], series: null, agg: "sum" });
  });
  it("refuses wrong shapes", () => {
    for (const bad of [null, 1, "bar", [], { kind: "radar", x: "a", y: [] }, { kind: "bar", x: 5, y: [] }, { kind: "bar", x: "a", y: "b" }, { kind: "bar", x: "a", y: [1] },
      { kind: "bar", x: "a", y: Array.from({ length: 9 }, (_, i) => `c${i}`) }, { kind: "histogram", x: "a", y: [], bins: 0 }, { kind: "histogram", x: "a", y: [], bins: 2.5 },
      { kind: "histogram", x: "a", y: [], bins: 500 }, { kind: "bar", x: "x".repeat(300), y: [] }, { kind: "bar", x: "a", y: [], series: 3 }]) {
      expect(validateSpec(bad), JSON.stringify(bad)).toBeNull();
    }
  });
  it("an unknown aggregation falls back to none; duplicate value columns collapse", () => {
    expect(validateSpec({ kind: "line", x: "t", y: ["a", "a"], agg: "median" })).toEqual({ kind: "line", x: "t", y: ["a"], series: null, agg: "none" });
  });
});

describe("reconciling a saved spec with the result's columns", () => {
  const cols = [col("t", "temporal"), col("a", "number"), col("b", "number"), col("region", "text")];
  it("keeps a spec that still fits, dropping value columns that are gone", () => {
    const r = reconcileSpec({ kind: "line", x: "t", y: ["a", "gone"], series: "region", agg: "none" }, cols);
    expect(r).toEqual({ spec: { kind: "line", x: "t", y: ["a"], series: "region", agg: "none" }, inferred: false });
  });
  it("falls back to inference when the x column is gone", () => {
    expect(reconcileSpec({ kind: "bar", x: "missing", y: ["a"], agg: "sum" }, cols)).toMatchObject({ inferred: true, spec: { kind: "line", x: "t" } });
  });
  it("reports why a spec cannot be drawn", () => {
    expect(specProblem({ kind: "histogram", x: "region", y: [], agg: "none" }, cols)).toMatch(/number or date/);
    expect(specProblem({ kind: "bar", x: "region", y: ["region"], agg: "sum" }, cols)).toMatch(/numbers/);
    expect(specProblem({ kind: "bar", x: "region", y: [], agg: "sum" }, cols)).toMatch(/count rows/);
    expect(specProblem({ kind: "bar", x: "region", y: [], agg: "count" }, cols)).toBeNull();
    expect(specProblem({ kind: "scatter", x: "region", y: ["a"], agg: "none" }, cols)).toMatch(/scatter/);
  });
  it("a mixed dynamic column (numbers and text, SQLite) is refused with its reason, on y and on a numeric x", () => {
    const mixed = [...cols, { name: "v", kind: "text" as const, typeName: "", mixed: true }];
    const reason = "Mixed value types: v is not numeric in every row.";
    expect(specProblem({ kind: "bar", x: "region", y: ["v"], agg: "sum" }, mixed)).toBe(reason);
    expect(specProblem({ kind: "histogram", x: "v", y: [], agg: "none" }, mixed)).toBe(reason);
    expect(specProblem({ kind: "scatter", x: "v", y: ["a"], agg: "none" }, mixed)).toBe(reason);
    expect(specProblem({ kind: "bar", x: "v", y: ["a"], agg: "sum" }, mixed)).toBeNull(); // still a category axis
    expect(reconcileSpec({ kind: "bar", x: "region", y: ["v"], agg: "sum" }, mixed)).toMatchObject({ inferred: true });
  });
  it("switching kind adapts the columns", () => {
    const bar = { kind: "bar" as const, x: "region", y: ["a", "b"], series: "t", agg: "sum" as const };
    expect(withKind(bar, "pie", cols)).toEqual({ kind: "pie", x: "region", y: ["a"], series: null, agg: "sum", bins: undefined });
    expect(withKind(bar, "histogram", cols)).toMatchObject({ kind: "histogram", x: "t", bins: 20 });
    expect(withKind({ ...bar, y: [], agg: "count" }, "scatter", cols)).toMatchObject({ kind: "scatter", x: "t", y: ["a"], agg: "none" });
  });
  it("lists the columns a spec reads once each", () => {
    expect(specColumns({ kind: "bar", x: "a", y: ["b", "a"], series: "c", agg: "sum" })).toEqual(["a", "b", "c"]);
    expect(specColumns({ kind: "histogram", x: "a", y: ["b"], series: "c", agg: "none" })).toEqual(["a"]);
  });
});

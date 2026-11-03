import { describe, expect, it } from "vitest";
import { chartCsv, computeChart, formatCompact, formatX, lttb, MAX_CATEGORIES, MAX_POINTS, MIN_POINTS, niceTicks, OTHER, pointsFor, strideSample, transferables, type ChartData, type ChartInput } from "../chartData";
import type { ChartSpec } from "../chartSpec";

const nums = (...v: (number | null)[]) => Float64Array.from(v, (x) => (x == null ? NaN : x));
const input = (x: ChartInput["x"], ys: [string, Float64Array][], series?: (string | null)[]): ChartInput => ({
  rows: x.values.length, x, ys: ys.map(([name, values]) => ({ name, values })), series, lossy: false,
});
const cat = (...v: (string | null)[]) => ({ type: "cat" as const, values: v });
const num = (v: Float64Array, temporal = false) => ({ type: "num" as const, values: v, temporal });
const spec = (s: Partial<ChartSpec>): ChartSpec => ({ kind: "bar", x: "x", y: ["y"], series: null, agg: "sum", ...s });
const as = <S extends ChartData["shape"]>(d: ChartData, shape: S) => {
  expect(d.shape).toBe(shape);
  return d as Extract<ChartData, { shape: S }>;
};

describe("aggregation", () => {
  const data = input(cat("a", "b", "a", "c", "b", "a"), [["y", nums(1, 10, 2, 5, null, 3)]]);
  it.each([
    ["sum", [6, 10, 5]],
    ["count", [3, 1, 1]],
    ["avg", [2, 10, 5]],
    ["min", [1, 10, 5]],
    ["max", [3, 10, 5]],
  ] as const)("%s per category, in the order the query returned them", (agg, expected) => {
    const d = as(computeChart(spec({ agg }), data), "category");
    expect(d.categories).toEqual(["a", "b", "c"]);
    expect([...d.series[0].values]).toEqual(expected);
  });
  it("no aggregation shows the first value and counts repeated categories", () => {
    const d = as(computeChart(spec({ agg: "none" }), data), "category");
    expect([...d.series[0].values]).toEqual([1, 10, 5]);
    expect(d.meta.duplicates).toBe(1); // "a" (b's second row is NULL and adds nothing)
  });
  it("count without value columns counts rows", () => {
    const d = as(computeChart(spec({ y: [], agg: "count" }), input(cat("x", "y", "x", null), [])), "category");
    expect(d.series).toHaveLength(1);
    expect([...d.series[0].values]).toEqual([2, 1]);
    expect(d.meta.nullsSkipped).toBe(1);
  });
  it("numbers and dates on a bar chart sort ascending and are labelled as values", () => {
    const day = 86_400_000;
    const d = as(computeChart(spec({}), input(num(nums(3 * day, day, 2 * day), true), [["y", nums(1, 2, 3)]])), "category");
    expect(d.categories).toEqual(["1970-01-02", "1970-01-03", "1970-01-04"]);
    expect([...d.series[0].values]).toEqual([2, 3, 1]);
  });
  it("a series column splits the values into one series per label", () => {
    const d = as(computeChart(spec({ series: "s" }), input(cat("q1", "q1", "q2"), [["y", nums(1, 2, 4)]], ["north", "south", "north"])), "category");
    expect(d.series.map((s) => s.name)).toEqual(["north", "south"]);
    expect([...d.series[0].values]).toEqual([1, 4]);
    expect(d.series[1].values[0]).toBe(2);
    expect(Number.isNaN(d.series[1].values[1])).toBe(true);
  });
  it("keeps the top 200 categories by size and folds the rest into Other", () => {
    const n = MAX_CATEGORIES + 50;
    const labels = Array.from({ length: n }, (_, i) => `c${i}`);
    const d = as(computeChart(spec({}), input(cat(...labels), [["y", Float64Array.from({ length: n }, (_, i) => i + 1)]])), "category");
    expect(d.categories).toHaveLength(MAX_CATEGORIES + 1);
    expect(d.categories.at(-1)).toBe(OTHER);
    expect(d.meta.foldedCategories).toBe(50);
    expect(d.series[0].values.at(-1)).toBe((50 * 51) / 2); // the 50 smallest: 1..50
    expect(d.categories[0]).toBe("c50"); // query order kept among the survivors
  });
  it("more than 8 series fold into an Other series drawn neutral", () => {
    const labels = Array.from({ length: 12 }, (_, i) => `s${i}`);
    const d = as(computeChart(spec({ series: "s" }), input(cat(...labels.map(() => "k")), [["y", Float64Array.from(labels, (_, i) => i + 1)]], labels)), "category");
    expect(d.series).toHaveLength(8);
    expect(d.series.at(-1)).toMatchObject({ name: OTHER, other: true });
    expect(d.series.at(-1)!.values[0]).toBe(1 + 2 + 3 + 4 + 5); // the 5 smallest of 12
    expect(d.meta.foldedSeries).toBe(5);
  });
});

describe("donut", () => {
  it("keeps positive values, biggest first, 8 slices at most", () => {
    const labels = Array.from({ length: 11 }, (_, i) => `p${i}`);
    const values = Float64Array.from(labels, (_, i) => (i === 3 ? -5 : i === 4 ? 0 : i + 1));
    const d = as(computeChart(spec({ kind: "pie" }), input(cat(...labels), [["y", values]])), "category");
    expect(d.kind).toBe("pie");
    expect(d.categories).toHaveLength(8);
    expect(d.categories[0]).toBe("p10");
    expect(d.categories.at(-1)).toBe(OTHER);
    expect(d.meta.negativeSkipped).toBe(2);
  });
  it("is empty when nothing is positive", () => {
    expect(computeChart(spec({ kind: "pie" }), input(cat("a"), [["y", nums(-1)]])).shape).toBe("empty");
  });
});

describe("histogram binning", () => {
  it("splits min..max into equal bins; the maximum lands in the last bin; NULLs are counted", () => {
    const d = as(computeChart(spec({ kind: "histogram", bins: 4 }), input(num(nums(0, 1, 2, 3, 4, 5, 6, 7, 8, null)), [])), "histogram");
    expect([...d.edges]).toEqual([0, 2, 4, 6, 8]);
    expect([...d.counts]).toEqual([2, 2, 2, 3]);
    expect(d.meta.nullsSkipped).toBe(1);
    expect(d.meta.plotted).toBe(9);
  });
  it("one distinct value is one bin", () => {
    const d = as(computeChart(spec({ kind: "histogram", bins: 5 }), input(num(nums(7, 7, 7)), [])), "histogram");
    expect([...d.counts]).toEqual([3, 0, 0, 0, 0]);
  });
  it("dates bin as dates", () => {
    expect(as(computeChart(spec({ kind: "histogram" }), input(num(nums(0, 86_400_000), true), [])), "histogram").xKind).toBe("date");
  });
  it("every NULL → empty with the reason", () => {
    expect(computeChart(spec({ kind: "histogram" }), input(num(nums(null, null)), []))).toMatchObject({ shape: "empty", meta: { nullsSkipped: 2 } });
  });
});

describe("LTTB down-sampling", () => {
  const n = 50_000;
  const xs = Float64Array.from({ length: n }, (_, i) => i);
  const ys = Float64Array.from({ length: n }, (_, i) => Math.sin(i / 300) * 100 + (i % 7));
  ys[12_345] = 10_000; // a spike
  ys[33_333] = -10_000; // a dip
  const idx = Uint32Array.from({ length: n }, (_, i) => i);
  it("returns at most threshold + 2 points, sorted, keeping first, last, min and max", () => {
    const out = lttb(xs, ys, idx, 1000);
    expect(out.length).toBeLessThanOrEqual(1002);
    expect(out[0]).toBe(0);
    expect(out[out.length - 1]).toBe(n - 1);
    expect(out).toContain(12_345);
    expect(out).toContain(33_333);
    for (let i = 1; i < out.length; i++) expect(out[i]).toBeGreaterThan(out[i - 1]);
  });
  it("leaves short series alone", () => {
    expect([...lttb(xs, ys, idx.subarray(0, 10), 100)]).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
  });
  it("stride sampling (scatter) keeps the extremes too", () => {
    const out = strideSample(ys, idx, 500);
    expect(out.length).toBeLessThanOrEqual(502);
    expect(out).toContain(12_345);
    expect(out).toContain(33_333);
    expect(out[out.length - 1]).toBe(n - 1);
  });
});

describe("line, area and scatter", () => {
  it("orders points by x, down-samples big series and aligns two series on one x array", () => {
    const n = 40_000;
    const x = Float64Array.from({ length: n }, (_, i) => n - i); // descending: must be sorted
    const d = as(computeChart(spec({ kind: "line", y: ["a", "b"], agg: "none" }), input(num(x), [["a", Float64Array.from(x, (v) => v * 2)], ["b", Float64Array.from(x, (v) => -v)]])), "xy");
    expect(d.meta.sampled).toBe(MAX_POINTS);
    expect(d.x.length).toBeLessThanOrEqual(2 * MAX_POINTS + 4);
    for (let i = 1; i < d.x.length; i++) expect(d.x[i]).toBeGreaterThan(d.x[i - 1]);
    expect(d.x[0]).toBe(1);
    expect(d.series[0].values[0]).toBe(2);
    expect(d.series[1].values[0]).toBe(-1);
  });
  it("the points per line follow the plot width (1.5 per pixel, 512–10,000)", () => {
    expect(pointsFor(1200)).toBe(2_048); // 1,800 rounded up to a multiple of 256
    expect(pointsFor(100)).toBe(MIN_POINTS);
    expect(pointsFor(20_000)).toBe(MAX_POINTS);
    const n = 40_000;
    const x = Float64Array.from({ length: n }, (_, i) => i);
    const d = as(computeChart(spec({ kind: "line", y: ["a"], agg: "none" }), input(num(x), [["a", Float64Array.from(x, (v) => v % 97)]]), 1_024), "xy");
    expect(d.meta.sampled).toBe(1_024);
    expect(d.x.length).toBeLessThanOrEqual(1_024 + 2);
  });
  it("skips and counts rows whose x or every value is NULL", () => {
    const d = as(computeChart(spec({ kind: "line", y: ["a"], agg: "none" }), input(num(nums(1, null, 3, 4)), [["a", nums(10, 20, null, 40)]])), "xy");
    expect([...d.x]).toEqual([1, 4]);
    expect(d.meta.nullsSkipped).toBe(2);
    expect(d.meta.plotted).toBe(2);
  });
  it("a series column gives each series its own points (gaps elsewhere)", () => {
    const d = as(computeChart(spec({ kind: "line", y: ["v"], series: "s", agg: "none" }), input(num(nums(1, 2, 3, 4)), [["v", nums(10, 20, 30, 40)]], ["a", "b", "a", "b"])), "xy");
    expect(d.series.map((s) => s.name)).toEqual(["a", "b"]);
    expect([...d.x]).toEqual([1, 2, 3, 4]);
    expect([...d.series[0].values].map((v) => (Number.isNaN(v) ? null : v))).toEqual([10, null, 30, null]);
  });
  it("scatter keeps at most three series apart (CVD-safe palette slots)", () => {
    const labels = ["a", "b", "c", "d", "a", "a"];
    const d = as(computeChart(spec({ kind: "scatter", y: ["v"], series: "s", agg: "none" }), input(num(nums(1, 2, 3, 4, 5, 6)), [["v", nums(1, 2, 3, 4, 5, 6)]], labels)), "xy");
    expect(d.series).toHaveLength(3);
    expect(d.meta.foldedSeries).toBe(1);
  });
  it("a text x on a line chart becomes positions with labels; aggregation groups by x", () => {
    const d = as(computeChart(spec({ kind: "line", agg: "sum" }), input(cat("mon", "tue", "mon"), [["y", nums(1, 2, 3)]])), "xy");
    expect([...d.x]).toEqual([0, 1]);
    expect(d.labels).toEqual(["mon", "tue"]);
    expect([...d.series[0].values]).toEqual([4, 2]);
  });
  it("timestamps with a time of day are a time axis", () => {
    expect(as(computeChart(spec({ kind: "line", agg: "none" }), input(num(nums(1_000, 2_000), true), [["y", nums(1, 2)]])), "xy").xKind).toBe("time");
  });
});

describe("output", () => {
  it("chart data as CSV, quoted where needed", () => {
    const d = computeChart(spec({}), input(cat('a,"b"', "c"), [["y", nums(1.5, 2)]]));
    expect(chartCsv(d)).toBe('x,y\r\n"a,""b""",1.5\r\nc,2\r\n');
    const h = computeChart(spec({ kind: "histogram", bins: 2 }), input(num(nums(0, 10)), []));
    expect(chartCsv(h)).toBe("bin_start,bin_end,count\r\n0,5,1\r\n5,10,1\r\n");
  });
  it("lists each buffer once for transfer", () => {
    const d = computeChart(spec({ kind: "line", agg: "none" }), input(num(nums(1, 2)), [["y", nums(1, 2)]]));
    expect(transferables(d)).toHaveLength(2);
  });
  it("formats axis values and nice ticks", () => {
    expect(formatCompact(1500)).toBe("1.5K");
    expect(formatCompact(2_000_000)).toBe("2M");
    expect(formatCompact(0)).toBe("0");
    expect(formatX(1_700_000_000_123, "time")).toBe("2023-11-14 22:13:20.123");
    expect(niceTicks(0, 97, 5)).toEqual([0, 20, 40, 60, 80, 100]);
    expect(niceTicks(-3, 3, 3)).toEqual([-4, -2, 0, 2, 4]);
    expect(niceTicks(5, 5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
  });
});

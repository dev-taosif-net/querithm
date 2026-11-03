import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { computeChart, type ChartData } from "../chartData";
import { barLayout, hitBar } from "../draw/bar";
import { histLayout, hitHistogram } from "../draw/histogram";
import { hitPie, pieLayout } from "../draw/pie";
import { ellipsize, estimateMeasure } from "../draw/common";
import type { ChartTheme } from "../theme";
import { contrastRatio } from "../../app/tabStyle";
import { lineWidthFor } from "../uplotAdapter";

const theme: ChartTheme = {
  dark: true, surface: "#000", text: "#fff", muted: "#999", grid: "#333", axis: "#666", other: "#888", font: "11px sans", mono: "11px mono",
  series: ["#1", "#2", "#3", "#4", "#5", "#6", "#7", "#8"],
};
const cat = (labels: string[], ...series: number[][]) =>
  computeChart(
    { kind: "bar", x: "x", y: series.map((_, i) => `y${i}`), series: null, agg: "sum" },
    { rows: labels.length, x: { type: "cat", values: labels }, ys: series.map((v, i) => ({ name: `y${i}`, values: Float64Array.from(v) })), lossy: false },
  ) as Extract<ChartData, { shape: "category" }>;

describe("bar layout", () => {
  it("bars grow from zero, are at most 24 px thick and keep a 2 px gap in a group", () => {
    const d = cat(["a", "b", "c"], [10, -5, 20], [1, 2, 3]);
    const l = barLayout(d, 600, 300, estimateMeasure);
    expect(l.domain[0]).toBeLessThanOrEqual(-5);
    expect(l.rects).toHaveLength(6);
    const scale = (v: number) => l.plot.left + ((v - l.domain[0]) / (l.domain[1] - l.domain[0])) * (l.plot.right - l.plot.left);
    for (const r of l.rects) {
      expect(r.h).toBeLessThanOrEqual(24);
      const zero = scale(0);
      if (r.value >= 0) expect(r.x).toBeCloseTo(zero);
      else expect(r.x + r.w).toBeCloseTo(zero);
    }
    const [a0, a1] = l.rects.filter((r) => r.category === 0);
    expect(a1.y - (a0.y + a0.h)).toBeCloseTo(2);
    expect(l.tipLabels).toBe(false); // two series: the legend and tooltip carry values
  });
  it("a long list grows taller than the view (it scrolls) instead of squeezing bars", () => {
    const labels = Array.from({ length: 150 }, (_, i) => `c${i}`);
    const l = barLayout(cat(labels, labels.map((_, i) => i)), 600, 300, estimateMeasure);
    expect(l.height).toBeGreaterThan(300);
    expect(l.band).toBeGreaterThanOrEqual(18);
  });
  it("hit test names the category and its values", () => {
    const d = cat(["north", "south"], [3, 4]);
    const l = barLayout(d, 400, 200, estimateMeasure);
    expect(hitBar(l, d, 200, l.plot.top + l.band * 1.5, theme)).toEqual({ title: "south", rows: [{ color: "#1", label: "y0", value: "4" }] });
    expect(hitBar(l, d, 200, l.plot.bottom + 5, theme)).toBeNull();
  });
});

describe("histogram layout", () => {
  const d = computeChart({ kind: "histogram", x: "v", y: [], agg: "none", bins: 4 }, { rows: 8, x: { type: "num", values: Float64Array.from([0, 1, 2, 3, 4, 5, 6, 8]), temporal: false }, ys: [], lossy: false }) as Extract<ChartData, { shape: "histogram" }>;
  it("one column per bin, heights proportional, 2 px apart", () => {
    const l = histLayout(d, 400, 200, estimateMeasure);
    expect(l.cols).toHaveLength(4);
    expect(l.cols[1].x - (l.cols[0].x + l.cols[0].w)).toBeCloseTo(2);
    expect(l.cols[3].h).toBeCloseTo(l.cols[0].h);
    expect(l.xLabels[0]).toBe(0);
  });
  it("hit test gives the bin range and count", () => {
    const l = histLayout(d, 400, 200, estimateMeasure);
    expect(hitHistogram(l, d, l.plot.right - 1, l.plot.bottom - 1, theme)).toEqual({ title: "6 – 8 (inclusive)", rows: [{ color: "#1", label: "Rows", value: "2" }] });
  });
});

describe("donut layout", () => {
  it("slices cover the circle in proportion; the legend and the ring both hit", () => {
    const d = computeChart({ kind: "pie", x: "x", y: ["y"], agg: "sum" }, { rows: 3, x: { type: "cat", values: ["a", "b", "c"] }, ys: [{ name: "y", values: Float64Array.from([1, 1, 2]) }], lossy: false }) as Extract<ChartData, { shape: "category" }>;
    const l = pieLayout(d, 500, 240);
    const sweep = l.slices.reduce((s, x) => s + (x.end - x.start), 0);
    expect(sweep).toBeCloseTo(Math.PI * 2);
    expect(l.slices[0].end - l.slices[0].start).toBeCloseTo(Math.PI); // "c" (2 of 4) first: biggest first
    const mid = (l.r + l.inner) / 2;
    expect(hitPie(l, d, l.cx + 1, l.cy - mid, theme)?.title).toBe("c");
    expect(hitPie(l, d, l.legend.x + 10, l.legend.y + 25, theme)?.title).toBe(d.categories[1]);
    expect(hitPie(l, d, l.cx, l.cy, theme)).toBeNull(); // the hole
  });
});

describe("line width", () => {
  it("2 px lines; 1 px once a line was down-sampled (it fills its pixel columns anyway; half the raster cost)", () => {
    const line = (sampled: number | null) => ({ meta: { sampled } }) as never;
    expect(lineWidthFor(line(null))).toBe(2);
    expect(lineWidthFor(line(1_792))).toBe(1);
  });
});

describe("helpers and palette", () => {
  it("ellipsizes to a width", () => {
    expect(ellipsize("short", 100, estimateMeasure)).toBe("short");
    const cut = ellipsize("a very long category name", 60, estimateMeasure);
    expect(cut.endsWith("…")).toBe(true);
    expect(estimateMeasure(cut)).toBeLessThanOrEqual(60);
  });
  it("the chart tokens are the validated palette, ≥ 3:1 on the dark surfaces", () => {
    const css = readFileSync(join(process.cwd(), "src/theme/tokens.css"), "utf8");
    const block = (sel: string) => css.slice(css.indexOf(`${sel} {`), css.indexOf("}", css.indexOf(`${sel} {`)));
    const vars = (sel: string) => Object.fromEntries([...block(sel).matchAll(/(--q-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
    const dark = vars(':root[data-theme="dark"]');
    const light = vars(':root[data-theme="light"]');
    const slots = (v: Record<string, string>) => [1, 2, 3, 4, 5, 6, 7, 8].map((i) => v[`--q-chart-${i}`]);
    // Order matters: it is what keeps neighbours apart for colour-blind readers (validate_palette.js, both modes).
    expect(slots(dark)).toEqual(["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"]);
    expect(slots(light)).toEqual(["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"]);
    const rgb = (h: string): [number, number, number] => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)) as [number, number, number];
    for (const surface of [dark["--q-bg"], dark["--q-panel"]]) for (const c of slots(dark)) expect(contrastRatio(rgb(c), rgb(surface)), `${c} on ${surface}`).toBeGreaterThanOrEqual(3);
  });
});

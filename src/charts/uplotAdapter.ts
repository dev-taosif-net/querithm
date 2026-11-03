// Line, area and scatter charts on uPlot (Phase C1): canvas rendering that stays fast at the 10,000 points per series
// the worker sends, with drag-to-zoom on x (double-click resets) and a hover readout.
import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import { formatCompact, formatNumber, formatX, type ChartData } from "./chartData";
import type { Hit } from "./draw/common";
import { seriesColor, withAlpha, type ChartTheme } from "./theme";

type XYData = Extract<ChartData, { shape: "xy" }>;

export interface XYChart {
  destroy(): void;
  resize(width: number, height: number): void;
  /** The plot canvas (axes and marks; the legend is DOM). */
  canvas(): HTMLCanvasElement;
}

/** uPlot reads null as a gap; the worker sends NaN. */
export function toAligned(data: XYData): uPlot.AlignedData {
  const ys = data.series.map((s) => {
    const out: (number | null)[] = new Array(s.values.length);
    for (let i = 0; i < s.values.length; i++) out[i] = Number.isNaN(s.values[i]) ? null : s.values[i];
    return out;
  });
  return [data.x, ...ys] as uPlot.AlignedData;
}

export function xLabel(data: XYData, v: number): string {
  if (data.labels) return data.labels[Math.round(v)] ?? "";
  return formatX(v, data.xKind);
}

export function xyOptions(data: XYData, width: number, height: number, t: ChartTheme, onHover: (hit: Hit | null, left: number, top: number) => void): uPlot.Options {
  const scatter = data.kind === "scatter";
  const time = data.xKind === "time" || data.xKind === "date";
  // A down-sampled line fills its pixel columns anyway: drawn 1 px wide it reads the same, and its stroke costs half
  // the raster (a 1M-row line with two series held a frame 35–50 ms at 2 px, 21–22 ms at 1 px; §14 metric 34).
  const lineWidth = lineWidthFor(data);
  const axis = (side: "x" | "y"): uPlot.Axis => ({
    stroke: t.muted,
    font: t.font,
    grid: { stroke: t.grid, width: 1 },
    ticks: { stroke: t.grid, width: 1, size: 4 },
    ...(side === "y" ? { values: (_u: uPlot, splits: number[]) => splits.map(formatCompact), size: 56 } : {}),
    ...(side === "x" && data.labels ? { values: (_u: uPlot, splits: number[]) => splits.map((v) => (Number.isInteger(v) ? data.labels![v] ?? "" : "")) } : {}),
  });
  return {
    width,
    height,
    ms: 1,
    scales: { x: { time, ...(data.labels ? { distr: 2 } : {}) } },
    legend: { show: data.series.length > 1, live: false },
    cursor: { drag: { x: true, y: false }, points: { size: 8, width: 2, stroke: t.surface } },
    series: [
      { label: data.xName },
      ...data.series.map((s, i): uPlot.Series => {
        const color = seriesColor(t, i, s.other);
        return {
          label: s.name,
          stroke: color,
          width: scatter ? 0 : lineWidth,
          spanGaps: !scatter,
          fill: data.kind === "area" ? withAlpha(color, 0.1) : undefined,
          ...(scatter ? { paths: () => null, points: { show: true, size: 8, width: 2, fill: color, stroke: t.surface } } : { points: { show: false } }),
        };
      }),
    ],
    axes: [axis("x"), axis("y")],
    hooks: {
      setCursor: [
        (u) => {
          const i = u.cursor.idx;
          if (i == null || u.cursor.left == null || u.cursor.left < 0) return onHover(null, 0, 0);
          const rows = data.series
            .map((s, k) => ({ color: seriesColor(t, k, s.other), label: s.name, value: Number.isNaN(s.values[i]) ? "" : formatNumber(s.values[i]) }))
            .filter((r) => r.value !== "");
          if (!rows.length) return onHover(null, 0, 0);
          onHover({ title: xLabel(data, data.x[i]), rows }, u.cursor.left + u.over.offsetLeft, (u.cursor.top ?? 0) + u.over.offsetTop);
        },
      ],
    },
  };
}

/** 2 px lines (mark spec), 1 px for a down-sampled, dense one. */
export function lineWidthFor(data: XYData): number {
  return data.meta.sampled != null ? 1 : 2;
}

export function createXYChart(el: HTMLElement, data: XYData, width: number, height: number, t: ChartTheme, onHover: (hit: Hit | null, left: number, top: number) => void): XYChart {
  // The legend (two or more series) takes about one line under the plot.
  const legendH = data.series.length > 1 ? 26 : 0;
  const u = new uPlot(xyOptions(data, width, Math.max(80, height - legendH), t, onHover), toAligned(data), el);
  return {
    destroy: () => u.destroy(),
    resize: (w, h) => u.setSize({ width: w, height: Math.max(80, h - legendH) }),
    canvas: () => u.ctx.canvas,
  };
}

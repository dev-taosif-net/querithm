// Histogram (Phase C1): contiguous bins as columns with a 2 px surface gap, counts on the y axis.
import { formatCompact, formatNumber, formatX, niceTicks, type ChartData } from "../chartData";
import type { ChartTheme } from "../theme";
import { barPath, type Ctx, type Hit, type Measure } from "./common";

type HistData = Extract<ChartData, { shape: "histogram" }>;

export interface HistLayout {
  width: number;
  height: number;
  plot: { left: number; right: number; top: number; bottom: number };
  yTicks: number[];
  /** Indices of the bin edges labelled on the x axis. */
  xLabels: number[];
  cols: { x: number; y: number; w: number; h: number; bin: number }[];
}

export function histLayout(data: HistData, width: number, height: number, measure: Measure): HistLayout {
  let max = 0;
  for (const c of data.counts) max = Math.max(max, c);
  const yTicks = niceTicks(0, max, Math.max(2, Math.floor(height / 50)));
  const top = yTicks.length ? yTicks[yTicks.length - 1] : 1;
  const left = Math.max(32, ...yTicks.map((t) => measure(formatCompact(t)) + 12));
  const plot = { left, right: width - 12, top: 10, bottom: height - 26 };
  const bins = data.counts.length;
  const step = (plot.right - plot.left) / bins;
  const cols = Array.from(data.counts, (count, bin) => {
    const h = top ? (count / top) * (plot.bottom - plot.top) : 0;
    return { x: plot.left + bin * step + 1, y: plot.bottom - h, w: Math.max(1, step - 2), h, bin };
  });
  // Edge labels: as many as fit without touching.
  const sample = formatX(data.edges[0], data.xKind);
  const every = Math.max(1, Math.ceil((measure(sample) + 16) / step));
  const xLabels: number[] = [];
  for (let e = 0; e <= bins; e += every) xLabels.push(e);
  return { width, height, plot, yTicks, xLabels, cols };
}

export function paintHistogram(ctx: Ctx, l: HistLayout, data: HistData, t: ChartTheme) {
  ctx.font = t.font;
  ctx.lineWidth = 1;
  const top = l.yTicks[l.yTicks.length - 1] || 1;
  ctx.textAlign = "right";
  ctx.textBaseline = "middle";
  for (const tick of l.yTicks) {
    const y = Math.round(l.plot.bottom - (tick / top) * (l.plot.bottom - l.plot.top)) + 0.5;
    ctx.strokeStyle = tick === 0 ? t.axis : t.grid;
    ctx.beginPath();
    ctx.moveTo(l.plot.left, y);
    ctx.lineTo(l.plot.right, y);
    ctx.stroke();
    ctx.fillStyle = t.muted;
    ctx.fillText(formatCompact(tick), l.plot.left - 6, y);
  }
  ctx.fillStyle = t.series[0];
  for (const c of l.cols) {
    if (c.h <= 0) continue;
    barPath(ctx, c.x, c.y, c.w, c.h, "top");
    ctx.fill();
  }
  ctx.fillStyle = t.muted;
  ctx.textAlign = "center";
  ctx.textBaseline = "top";
  const step = (l.plot.right - l.plot.left) / data.counts.length;
  for (const e of l.xLabels) ctx.fillText(formatX(data.edges[e], data.xKind), l.plot.left + e * step, l.plot.bottom + 6);
}

export function hitHistogram(l: HistLayout, data: HistData, x: number, y: number, t: ChartTheme): Hit | null {
  if (x < l.plot.left || x > l.plot.right || y < l.plot.top || y > l.plot.bottom) return null;
  const bin = Math.min(data.counts.length - 1, Math.floor(((x - l.plot.left) / (l.plot.right - l.plot.left)) * data.counts.length));
  const last = bin === data.counts.length - 1;
  return {
    title: `${formatX(data.edges[bin], data.xKind)} – ${formatX(data.edges[bin + 1], data.xKind)}${last ? " (inclusive)" : ""}`,
    rows: [{ color: t.series[0], label: "Rows", value: formatNumber(data.counts[bin]) }],
  };
}

// Horizontal bar chart (Phase C1): one band per category, grouped bars per series, a shared value axis from zero.
// Long category names stay readable on the left, and a long list scrolls instead of shrinking the bars.
import { formatCompact, formatNumber, niceTicks, type ChartData } from "../chartData";
import type { ChartTheme } from "../theme";
import { seriesColor } from "../theme";
import { barPath, ellipsize, type Ctx, type Hit, type Measure } from "./common";

type CategoryData = Extract<ChartData, { shape: "category" }>;

export interface BarRect {
  x: number;
  y: number;
  w: number;
  h: number;
  category: number;
  series: number;
  value: number;
}

export interface BarLayout {
  width: number;
  height: number;
  labelWidth: number;
  plot: { left: number; right: number; top: number; bottom: number };
  band: number;
  domain: [number, number];
  ticks: number[];
  rects: BarRect[];
  /** Value labels at the bar tips (one series, not too many bars). */
  tipLabels: boolean;
}

const TOP = 6;
const AXIS = 22;
const MAX_THICK = 24;
const GAP = 2;

/** Geometry for `width` × at least `minHeight` (taller when the categories need it; the view then scrolls). */
export function barLayout(data: CategoryData, width: number, minHeight: number, measure: Measure): BarLayout {
  const n = data.categories.length;
  const s = data.series.length;
  const labelWidth = Math.min(Math.max(60, ...data.categories.map((c) => measure(c) + 12)), Math.max(60, width * 0.35));
  let lo = 0;
  let hi = 0;
  for (const se of data.series) for (const v of se.values) if (!Number.isNaN(v)) [lo, hi] = [Math.min(lo, v), Math.max(hi, v)];
  const ticks = niceTicks(lo, hi, Math.max(2, Math.floor((width - labelWidth) / 90)));
  const domain: [number, number] = ticks.length ? [ticks[0], ticks[ticks.length - 1]] : [0, 1];
  const tipLabels = s === 1 && n <= 40;
  const right = width - (tipLabels ? 56 : 16);
  const avail = Math.max(1, minHeight - TOP - AXIS);
  const band = Math.max(s > 1 ? 10 + s * 8 : 18, Math.min(s > 1 ? 16 + s * 26 : 44, Math.floor(avail / Math.max(1, n))));
  const height = Math.max(minHeight, TOP + AXIS + band * n);
  const plot = { left: labelWidth, right, top: TOP, bottom: TOP + band * n };
  const scale = (v: number) => plot.left + ((v - domain[0]) / (domain[1] - domain[0] || 1)) * (plot.right - plot.left);
  const zero = scale(Math.max(domain[0], Math.min(0, domain[1])));
  const thick = Math.max(2, Math.min(MAX_THICK, (band - 6 - GAP * (s - 1)) / s));
  const rects: BarRect[] = [];
  for (let c = 0; c < n; c++) {
    const groupH = s * thick + (s - 1) * GAP;
    let y = plot.top + c * band + (band - groupH) / 2;
    for (let k = 0; k < s; k++) {
      const v = data.series[k].values[c];
      if (!Number.isNaN(v)) {
        const x1 = scale(v);
        rects.push({ x: Math.min(zero, x1), y, w: Math.max(1, Math.abs(x1 - zero)), h: thick, category: c, series: k, value: v });
      }
      y += thick + GAP;
    }
  }
  return { width, height, labelWidth, plot, band, domain, ticks, rects, tipLabels };
}

export function paintBars(ctx: Ctx, l: BarLayout, data: CategoryData, t: ChartTheme, measure: Measure) {
  ctx.font = t.font;
  ctx.textBaseline = "middle";
  const scale = (v: number) => l.plot.left + ((v - l.domain[0]) / (l.domain[1] - l.domain[0] || 1)) * (l.plot.right - l.plot.left);
  // Hairline grid at each tick, the zero line one step stronger.
  ctx.lineWidth = 1;
  for (const tick of l.ticks) {
    const x = Math.round(scale(tick)) + 0.5;
    ctx.strokeStyle = tick === 0 ? t.axis : t.grid;
    ctx.beginPath();
    ctx.moveTo(x, l.plot.top);
    ctx.lineTo(x, l.plot.bottom);
    ctx.stroke();
    ctx.fillStyle = t.muted;
    ctx.textAlign = "center";
    ctx.fillText(formatCompact(tick), x, l.plot.bottom + AXIS / 2 + 2);
  }
  ctx.textAlign = "right";
  data.categories.forEach((c, i) => {
    ctx.fillStyle = data.categories.length - 1 === i && data.meta.foldedCategories ? t.muted : t.text;
    ctx.fillText(ellipsize(c, l.labelWidth - 12, measure), l.labelWidth - 8, l.plot.top + i * l.band + l.band / 2);
  });
  for (const r of l.rects) {
    const se = data.series[r.series];
    ctx.fillStyle = seriesColor(t, r.series, se.other || (data.meta.foldedCategories > 0 && r.category === data.categories.length - 1 && data.series.length === 1));
    barPath(ctx, r.x, r.y, r.w, r.h, r.value < 0 ? "left" : "right");
    ctx.fill();
    if (l.tipLabels) {
      ctx.fillStyle = t.muted;
      ctx.textAlign = r.value < 0 ? "right" : "left";
      ctx.fillText(formatCompact(r.value), r.value < 0 ? r.x - 4 : r.x + r.w + 4, r.y + r.h / 2);
    }
  }
}

export function hitBar(l: BarLayout, data: CategoryData, x: number, y: number, t: ChartTheme): Hit | null {
  if (y < l.plot.top || y > l.plot.bottom || x < 0 || x > l.width) return null;
  const c = Math.floor((y - l.plot.top) / l.band);
  if (c < 0 || c >= data.categories.length) return null;
  return {
    title: data.categories[c],
    rows: data.series
      .map((s, k) => ({ color: seriesColor(t, k, s.other), label: s.name, value: Number.isNaN(s.values[c]) ? "—" : formatNumber(s.values[c]) }))
      .filter((r) => r.value !== "—"),
  };
}

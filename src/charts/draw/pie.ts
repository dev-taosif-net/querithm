// Donut (Phase C1): parts of a whole, at most 8 slices (the rest is "Other"), each separated by a 2 px surface gap,
// with a legend naming every slice with its value and share.
import { formatNumber, type ChartData } from "../chartData";
import type { ChartTheme } from "../theme";
import { seriesColor } from "../theme";
import { ellipsize, type Ctx, type Hit, type Measure } from "./common";

type CategoryData = Extract<ChartData, { shape: "category" }>;

export interface PieLayout {
  width: number;
  height: number;
  cx: number;
  cy: number;
  r: number;
  inner: number;
  total: number;
  slices: { start: number; end: number; value: number; index: number }[];
  legend: { x: number; y: number; width: number };
}

export function pieLayout(data: CategoryData, width: number, height: number): PieLayout {
  const values = data.series[0]?.values ?? new Float64Array();
  let total = 0;
  for (const v of values) if (v > 0) total += v;
  const legendWidth = Math.min(280, Math.max(160, width * 0.4));
  const r = Math.max(10, Math.min((height - 24) / 2, (width - legendWidth - 32) / 2));
  const cx = 16 + r;
  const cy = height / 2;
  let a = -Math.PI / 2;
  const slices: PieLayout["slices"] = [];
  values.forEach((v, index) => {
    if (!(v > 0) || !total) return;
    const sweep = (v / total) * Math.PI * 2;
    slices.push({ start: a, end: a + sweep, value: v, index });
    a += sweep;
  });
  return { width, height, cx, cy, r, inner: r * 0.6, total, slices, legend: { x: cx + r + 28, y: Math.max(12, cy - (slices.length * 20) / 2), width: width - (cx + r + 28) - 8 } };
}

const share = (v: number, total: number) => `${((v / total) * 100).toFixed(v / total < 0.1 ? 1 : 0)}%`;

export function paintPie(ctx: Ctx, l: PieLayout, data: CategoryData, t: ChartTheme, measure: Measure) {
  const last = data.categories.length - 1;
  const colorOf = (i: number) => seriesColor(t, i, data.meta.foldedCategories > 0 && i === last);
  for (const s of l.slices) {
    ctx.beginPath();
    ctx.arc(l.cx, l.cy, l.r, s.start, s.end);
    ctx.arc(l.cx, l.cy, l.inner, s.end, s.start, true);
    ctx.closePath();
    ctx.fillStyle = colorOf(s.index);
    ctx.fill();
    // The gap between slices, in the surface color.
    ctx.strokeStyle = t.surface;
    ctx.lineWidth = 2;
    ctx.stroke();
  }
  ctx.font = t.font;
  ctx.textBaseline = "middle";
  ctx.textAlign = "left";
  l.slices.forEach((s, row) => {
    const y = l.legend.y + row * 20 + 10;
    ctx.fillStyle = colorOf(s.index);
    ctx.beginPath();
    ctx.arc(l.legend.x + 5, y, 5, 0, Math.PI * 2);
    ctx.fill();
    const tail = `${formatNumber(s.value)} · ${share(s.value, l.total)}`;
    const tailW = measure(tail);
    ctx.fillStyle = t.text;
    ctx.fillText(ellipsize(data.categories[s.index], Math.max(20, l.legend.width - tailW - 30), measure), l.legend.x + 16, y);
    ctx.fillStyle = t.muted;
    ctx.textAlign = "right";
    ctx.fillText(tail, l.legend.x + l.legend.width, y);
    ctx.textAlign = "left";
  });
}

export function hitPie(l: PieLayout, data: CategoryData, x: number, y: number, t: ChartTheme): Hit | null {
  const dx = x - l.cx;
  const dy = y - l.cy;
  const d = Math.hypot(dx, dy);
  let slice: PieLayout["slices"][number] | undefined;
  if (d >= l.inner && d <= l.r) {
    let a = Math.atan2(dy, dx);
    if (a < -Math.PI / 2) a += Math.PI * 2;
    slice = l.slices.find((s) => a >= s.start && a < s.end);
  } else if (x >= l.legend.x) {
    slice = l.slices[Math.floor((y - l.legend.y) / 20)];
  }
  if (!slice) return null;
  const last = data.categories.length - 1;
  return {
    title: data.categories[slice.index],
    rows: [{ color: seriesColor(t, slice.index, data.meta.foldedCategories > 0 && slice.index === last), label: data.series[0].name, value: `${formatNumber(slice.value)} (${share(slice.value, l.total)})` }],
  };
}

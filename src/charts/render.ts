// One entry point for drawing a computed chart into a host element (ChartView, notebook cells) and for rendering it
// as an image (PNG export, notebook export). Bars, donuts and histograms are in-house canvas; lines on uPlot.
import type { ChartData } from "./chartData";
import { ctxMeasure, setupCanvas, type Hit } from "./draw/common";
import { barLayout, hitBar, paintBars } from "./draw/bar";
import { hitPie, paintPie, pieLayout } from "./draw/pie";
import { histLayout, hitHistogram, paintHistogram } from "./draw/histogram";
import { createXYChart } from "./uplotAdapter";
import { seriesColor, type ChartTheme } from "./theme";
import { ellipsize } from "./draw/common";

export interface DrawnChart {
  /** The canvas holding the plot (for a bar list taller than the view, the whole list). */
  canvas(): HTMLCanvasElement;
  destroy(): void;
}

export type HoverFn = (hit: Hit | null, left: number, top: number) => void;

/** Draws `data` into `host` (emptied first) at `width` × `height` CSS pixels. Null for an empty chart. */
export function drawChart(host: HTMLElement, data: ChartData, width: number, height: number, t: ChartTheme, onHover: HoverFn): DrawnChart | null {
  host.replaceChildren();
  if (data.shape === "empty" || width < 40 || height < 40) return null;
  if (data.shape === "xy") {
    const chart = createXYChart(host, data, width, height, t, onHover);
    return { canvas: chart.canvas, destroy: chart.destroy };
  }
  const canvas = document.createElement("canvas");
  canvas.style.display = "block";
  host.append(canvas);
  const probe = canvas.getContext("2d");
  if (!probe) return null;
  probe.font = t.font;
  const measure = ctxMeasure(probe);
  let hit: (x: number, y: number) => Hit | null;
  if (data.shape === "histogram") {
    const l = histLayout(data, width, height, measure);
    const ctx = setupCanvas(canvas, width, height)!;
    paintHistogram(ctx, l, data, t);
    hit = (x, y) => hitHistogram(l, data, x, y, t);
  } else if (data.kind === "pie") {
    const l = pieLayout(data, width, height);
    const ctx = setupCanvas(canvas, width, height)!;
    paintPie(ctx, l, data, t, measure);
    hit = (x, y) => hitPie(l, data, x, y, t);
  } else {
    const l = barLayout(data, width, height, measure);
    const ctx = setupCanvas(canvas, width, l.height)!;
    ctx.font = t.font;
    paintBars(ctx, l, data, t, ctxMeasure(ctx));
    hit = (x, y) => hitBar(l, data, x, y, t);
  }
  const move = (e: MouseEvent) => {
    const r = canvas.getBoundingClientRect();
    const h = hit(e.clientX - r.left, e.clientY - r.top);
    onHover(h, e.clientX - r.left + canvas.offsetLeft, e.clientY - r.top + canvas.offsetTop);
  };
  const leave = () => onHover(null, 0, 0);
  canvas.addEventListener("mousemove", move);
  canvas.addEventListener("mouseleave", leave);
  return {
    canvas: () => canvas,
    destroy: () => {
      canvas.removeEventListener("mousemove", move);
      canvas.removeEventListener("mouseleave", leave);
      canvas.remove();
    },
  };
}

/**
 * The chart as a standalone image: a title, the plot and, for two or more series, a legend, on the theme surface.
 * Drawn off screen (PNG export, notebook export); `scale` multiplies the pixel density.
 */
export function renderChartImage(data: ChartData, title: string, width: number, height: number, t: ChartTheme, scale = 2): HTMLCanvasElement | null {
  if (data.shape === "empty") return null;
  const host = document.createElement("div");
  // uPlot measures its root: lay it out off screen while it draws.
  Object.assign(host.style, { position: "fixed", left: "-10000px", top: "0", width: `${width}px`, height: `${height}px` });
  document.body.append(host);
  try {
    const drawn = drawChart(host, data, width, height, t, () => {});
    if (!drawn) return null;
    const plot = drawn.canvas();
    const plotH = plot.height / (plot.width / width || 1);
    const series = data.shape === "category" && data.kind === "bar" ? data.series : data.shape === "xy" ? data.series : [];
    const legendH = series.length > 1 ? 22 : 0;
    const titleH = 26;
    const out = document.createElement("canvas");
    out.width = Math.round(width * scale);
    out.height = Math.round((titleH + plotH + legendH + 8) * scale);
    const ctx = out.getContext("2d");
    if (!ctx) return null;
    ctx.scale(scale, scale);
    ctx.fillStyle = t.surface;
    ctx.fillRect(0, 0, width, titleH + plotH + legendH + 8);
    ctx.font = `600 ${t.font}`;
    ctx.fillStyle = t.text;
    ctx.textBaseline = "middle";
    ctx.fillText(ellipsize(title, width - 16, ctxMeasure(ctx)), 8, titleH / 2);
    ctx.drawImage(plot, 0, titleH, width, plotH);
    if (legendH) {
      ctx.font = t.font;
      let x = 8;
      const y = titleH + plotH + legendH / 2 + 2;
      series.forEach((s, i) => {
        if (x > width - 40) return;
        ctx.fillStyle = seriesColor(t, i, s.other);
        ctx.fillRect(x, y - 1, 12, 3);
        ctx.fillStyle = t.text;
        const label = ellipsize(s.name, 140, ctxMeasure(ctx));
        ctx.fillText(label, x + 16, y);
        x += 16 + ctx.measureText(label).width + 16;
      });
    }
    drawn.destroy();
    return out;
  } finally {
    host.remove();
  }
}

/** A canvas as PNG bytes. */
export function canvasPng(canvas: HTMLCanvasElement): Promise<Uint8Array> {
  return new Promise((resolve, reject) =>
    canvas.toBlob((blob) => (blob ? blob.arrayBuffer().then((b) => resolve(new Uint8Array(b)), reject) : reject(new Error("The chart could not be rendered as PNG."))), "image/png"),
  );
}

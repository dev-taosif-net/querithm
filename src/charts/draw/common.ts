// Shared canvas helpers for the in-house chart renderers (bar, donut, histogram).

export type Ctx = CanvasRenderingContext2D;

/** Sizes a canvas for the device pixel ratio; drawing then uses CSS pixels. */
export function setupCanvas(canvas: HTMLCanvasElement, width: number, height: number, dpr = window.devicePixelRatio || 1): Ctx | null {
  canvas.width = Math.max(1, Math.round(width * dpr));
  canvas.height = Math.max(1, Math.round(height * dpr));
  canvas.style.width = `${width}px`;
  canvas.style.height = `${height}px`;
  const ctx = canvas.getContext("2d");
  if (!ctx) return null;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, width, height);
  return ctx;
}

/** Text width in CSS pixels; a fixed estimate where there is no canvas (tests). */
export type Measure = (text: string) => number;
export const estimateMeasure: Measure = (t) => t.length * 6.2;
export const ctxMeasure = (ctx: Ctx): Measure => (t) => ctx.measureText(t).width;

/** `text` cut with an ellipsis to fit `max` pixels. */
export function ellipsize(text: string, max: number, measure: Measure): string {
  if (measure(text) <= max) return text;
  let lo = 0;
  let hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (measure(`${text.slice(0, mid)}…`) <= max) lo = mid;
    else hi = mid - 1;
  }
  return lo > 0 ? `${text.slice(0, lo)}…` : "…";
}

/**
 * A rectangle whose data end has 4 px rounded corners and whose baseline end is square (dataviz mark spec).
 * `end`: which side is the data end.
 */
export function barPath(ctx: Ctx, x: number, y: number, w: number, h: number, end: "left" | "right" | "top" | "bottom", radius = 4) {
  const r = Math.max(0, Math.min(radius, Math.abs(w) / 2, Math.abs(h) / 2));
  ctx.beginPath();
  if (end === "right") {
    ctx.moveTo(x, y);
    ctx.lineTo(x + w - r, y);
    ctx.arcTo(x + w, y, x + w, y + r, r);
    ctx.lineTo(x + w, y + h - r);
    ctx.arcTo(x + w, y + h, x + w - r, y + h, r);
    ctx.lineTo(x, y + h);
  } else if (end === "left") {
    ctx.moveTo(x + w, y);
    ctx.lineTo(x + r, y);
    ctx.arcTo(x, y, x, y + r, r);
    ctx.lineTo(x, y + h - r);
    ctx.arcTo(x, y + h, x + r, y + h, r);
    ctx.lineTo(x + w, y + h);
  } else if (end === "top") {
    ctx.moveTo(x, y + h);
    ctx.lineTo(x, y + r);
    ctx.arcTo(x, y, x + r, y, r);
    ctx.lineTo(x + w - r, y);
    ctx.arcTo(x + w, y, x + w, y + r, r);
    ctx.lineTo(x + w, y + h);
  } else {
    ctx.moveTo(x, y);
    ctx.lineTo(x, y + h - r);
    ctx.arcTo(x, y + h, x + r, y + h, r);
    ctx.lineTo(x + w - r, y + h);
    ctx.arcTo(x + w, y + h, x + w, y + h - r, r);
    ctx.lineTo(x + w, y);
  }
  ctx.closePath();
}

/** What the pointer is over: shown as a tooltip. */
export interface Hit {
  title: string;
  rows: { color: string; label: string; value: string }[];
}

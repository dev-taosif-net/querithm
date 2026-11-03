// Chart colors and type, read from the theme tokens (src/theme/tokens.css) at draw time so a theme switch redraws in
// the new palette. Series take the categorical slots in order; text uses the text tokens, never a series color.

export interface ChartTheme {
  dark: boolean;
  /** The chart's surface (gaps and rings are drawn in it). */
  surface: string;
  text: string;
  muted: string;
  grid: string;
  axis: string;
  series: string[];
  other: string;
  font: string;
  mono: string;
}

const FALLBACK_DARK = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];

export function chartTheme(el: Element = document.documentElement): ChartTheme {
  const css = getComputedStyle(el);
  const v = (name: string, fallback: string) => css.getPropertyValue(name).trim() || fallback;
  const dark = document.documentElement.dataset.theme !== "light";
  return {
    dark,
    surface: v("--q-bg", dark ? "#0f1117" : "#ffffff"),
    text: v("--q-fg", dark ? "#e6edf3" : "#1f2328"),
    muted: v("--q-muted", dark ? "#8c949e" : "#59636e"),
    grid: v("--q-line", dark ? "#30363d" : "#d0d7de"),
    axis: v("--q-line-strong", dark ? "#6e7681" : "#768390"),
    series: FALLBACK_DARK.map((f, i) => v(`--q-chart-${i + 1}`, f)),
    other: v("--q-chart-other", dark ? "#8c949e" : "#768390"),
    font: `11px ${v("--font-sans", "system-ui, sans-serif")}`,
    mono: `11px ${v("--font-mono", "monospace")}`,
  };
}

/** The color of series `i` ("Other" is neutral). */
export const seriesColor = (t: ChartTheme, i: number, other?: boolean) => (other ? t.other : t.series[i % t.series.length]);

/** `#rrggbb` at an opacity, for area washes. */
export function withAlpha(hex: string, alpha: number): string {
  const m = /^#([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return hex;
  const n = parseInt(m[1], 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

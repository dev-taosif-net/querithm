// Colour fitting for text drawn in a connection's colour (tab strip, Explorer, overlays, top bar), and the one theme
// subscription those colours re-render on. CSS variables are read once per theme, never during a render pass after
// that (D26).
import { useSyncExternalStore } from "react";

/** Surfaces a tab sits on (rest, hover, active): its text colours are fitted on all three (D4). */
export const TAB_SURFACES = ["var(--q-panel)", "var(--q-hover)", "var(--q-row-sel)"] as const;
/** Surfaces of the Ctrl+P and Ctrl+Tab rows (the list, and the highlighted row over it). */
export const OVERLAY_SURFACES = ["var(--q-raised)", "var(--q-hover)", "var(--q-overlay-sel)"] as const;

/** Minimum WCAG contrast of the connection line on its tab. */
export const SUBTITLE_CONTRAST = 4.5;

type Rgb = [number, number, number];

function parseHex(c: string): Rgb | null {
  const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(c.trim());
  if (!m) return null;
  const h = m[1].length === 3 ? [...m[1]].map((x) => x + x).join("") : m[1];
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)) as Rgb;
}

const toHex = (c: Rgb) => `#${c.map((x) => Math.round(x).toString(16).padStart(2, "0")).join("")}`;
const toLinear = (x: number) => (x <= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4);
const fromLinear = (x: number) => (x <= 0.0031308 ? 12.92 * x : 1.055 * x ** (1 / 2.4) - 0.055);

function luminance(c: Rgb): number {
  const [r, g, b] = c.map((x) => toLinear(x / 255));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** WCAG contrast ratio of two colors. */
export function contrastRatio(a: Rgb, b: Rgb): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

/** sRGB (0–255) → OKLCH [L, C, h in radians]. */
export function toOklch(c: Rgb): [number, number, number] {
  const [r, g, b] = c.map((x) => toLinear(x / 255));
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  const L = 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s;
  const A = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s;
  const B = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s;
  return [L, Math.hypot(A, B), Math.atan2(B, A)];
}

/** OKLCH → linear sRGB (0–1, may be out of gamut). */
function oklchToLinear(L: number, C: number, h: number): Rgb {
  const A = C * Math.cos(h);
  const B = C * Math.sin(h);
  const l = (L + 0.3963377774 * A + 0.2158037573 * B) ** 3;
  const m = (L - 0.1055613458 * A - 0.0638541728 * B) ** 3;
  const s = (L - 0.0894841775 * A - 1.291485548 * B) ** 3;
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
}

const inGamut = (c: Rgb) => c.every((x) => x >= -1e-4 && x <= 1 + 1e-4);

/** The color at lightness L with hue h and as much of chroma C as sRGB can show. */
function atLightness(L: number, C: number, h: number): Rgb {
  let lo = 0;
  let hi = C;
  if (!inGamut(oklchToLinear(L, hi, h))) {
    for (let i = 0; i < 16; i++) {
      const mid = (lo + hi) / 2;
      if (inGamut(oklchToLinear(L, mid, h))) lo = mid;
      else hi = mid;
    }
    hi = lo;
  }
  // Rounded here, so the contrast check sees the color that is painted.
  return oklchToLinear(L, hi, h).map((x) => Math.round(Math.min(255, Math.max(0, fromLinear(Math.min(1, Math.max(0, x))) * 255)))) as Rgb;
}

/**
 * `color` moved in lightness only (OKLCH; hue kept, chroma kept as far as sRGB allows) just far enough to reach
 * `min` contrast on `bg`: lighter on a dark background, darker on a light one. A color that already passes is
 * returned unchanged.
 */
export function readableOn(color: string, bg: string, min = SUBTITLE_CONTRAST): string | null {
  const fg = parseHex(color);
  const back = parseHex(bg);
  if (!fg || !back) return null;
  if (contrastRatio(fg, back) >= min) return toHex(fg);
  const [L0, C, h] = toOklch(fg);
  const lighter = luminance(back) < 0.18;
  let lo = L0;
  let hi = lighter ? 1 : 0;
  let best = atLightness(hi, C, h);
  for (let i = 0; i < 24; i++) {
    const mid = (lo + hi) / 2;
    const c = atLightness(mid, C, h);
    if (contrastRatio(c, back) >= min) {
      best = c;
      hi = mid;
    } else lo = mid;
  }
  return toHex(best);
}

// ---------------------------------------------------------------------------------------------------- theme

let themeVersion = 0;
const themeListeners = new Set<() => void>();
let themeObserver: MutationObserver | undefined;

/** One observer for the whole app; it bumps the version (and empties the caches) when data-theme changes. */
function subscribeTheme(cb: () => void): () => void {
  themeListeners.add(cb);
  if (!themeObserver && typeof MutationObserver !== "undefined" && typeof document !== "undefined") {
    themeObserver = new MutationObserver(() => {
      themeVersion++;
      vars.clear();
      fitted.clear();
      for (const l of [...themeListeners]) l();
    });
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  }
  return () => {
    themeListeners.delete(cb);
    if (!themeListeners.size) {
      themeObserver?.disconnect();
      themeObserver = undefined;
    }
  };
}

/** Current `data-theme`; components that fit colours re-render when the theme changes. */
export function useThemeKey(): string {
  return useSyncExternalStore(subscribeTheme, () => (typeof document === "undefined" ? "" : (document.documentElement.dataset.theme ?? "")));
}

/** Number of theme observers alive (tests: one for any number of tab headers). */
export function themeObserverCount(): number {
  return themeObserver ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------------- fitting

const vars = new Map<string, string>();
const fitted = new Map<string, string | null>();

/** Resolves `var(--x)` (one level) to its value on the document root, read once per theme. */
function resolve(color: string): string {
  const m = /^var\((--[\w-]+)\)$/.exec(color.trim());
  if (!m || typeof document === "undefined") return color;
  let v = vars.get(m[1]);
  if (v === undefined) {
    v = getComputedStyle(document.documentElement).getPropertyValue(m[1]).trim();
    // An empty value (no stylesheet yet) is not cached, so it is read again once the theme is in.
    if (v) vars.set(m[1], v);
  }
  return v;
}

/** Forget every resolved variable and fitted colour (tests that change variables without a theme switch). */
export function clearColorCache() {
  vars.clear();
  fitted.clear();
}

/** `color` (a hex or `var(--x)`) kept in hue and adjusted to `min` contrast on `surface`; null when either cannot be read. */
export function readableOnSurface(color: string, surface: string, min = SUBTITLE_CONTRAST): string | null {
  const fg = resolve(color);
  const bg = resolve(surface);
  const key = `${fg}|${bg}|${min}`;
  if (!fitted.has(key)) fitted.set(key, readableOn(fg, bg, min));
  return fitted.get(key) ?? null;
}

/**
 * `color` adjusted to `min` contrast on every one of `surfaces` (all dark or all light): a connection's colour reads
 * the same at rest, on hover and selected. Null when a colour cannot be read.
 */
export function readableOnAll(color: string, surfaces: readonly string[], min = SUBTITLE_CONTRAST): string | null {
  let c: string | null = resolve(color);
  for (const s of surfaces) {
    c = c && readableOnSurface(c, s, min);
    if (!c) return null;
  }
  // A later surface can move the color off an earlier one's minimum only when they differ in polarity; check once more.
  return surfaces.every((s) => {
    const fg = parseHex(c!);
    const bg = parseHex(resolve(s));
    return fg && bg && contrastRatio(fg, bg) >= min;
  }) ? c : null;
}

/**
 * `color` fitted to `min` contrast on a pill tinted with itself: `color-mix(in srgb, color <amount>, transparent)` drawn
 * over `surface` (the profile editor's environment pill). Null when a colour cannot be read.
 */
export function readableOnTint(color: string, surface: string, amount: number, min = SUBTITLE_CONTRAST): string | null {
  const fg = parseHex(resolve(color));
  const bg = parseHex(resolve(surface));
  if (!fg || !bg) return null;
  const tinted = toHex(fg.map((x, i) => x * amount + bg[i] * (1 - amount)) as Rgb);
  return readableOnSurface(color, tinted, min);
}

/** A fill of `color` (same hue, darkened as needed) under which white text keeps 4.5:1: an environment badge. */
export function fillForWhite(color: string): string {
  return readableOnSurface(color, "#ffffff") ?? color;
}

/** The text colour of a connection on a tab or in a tab list: its environment colour fitted to 4.5:1 there. */
export function connectionText(color: string, surfaces: readonly string[] = TAB_SURFACES): string {
  return readableOnAll(color, surfaces) ?? color;
}

/** The Prod band on a tab (D3): non-text, so 3:1 on every tab surface; the raw colour when it already passes. */
export function bandColor(color: string): string {
  return readableOnAll(color, TAB_SURFACES, 3) ?? color;
}

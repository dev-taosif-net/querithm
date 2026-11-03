// Interface scale (L26, §12): the webview's own page zoom (Tauri's setZoom), not CSS `zoom` on the root. Page zoom
// keeps getBoundingClientRect, pointer coordinates and fixed-position popups in one coordinate space in both engines,
// so Monaco, the grid's hit-testing and every anchored menu stay where they belong; CSS zoom on the root would mix
// zoomed and unzoomed coordinates (popup positions in Chromium, pointer positions in WebKit). WebView2 (Chromium) reports page zoom in devicePixelRatio, so canvases (the results grid, charts) draw
// at the zoomed resolution. WebKit (WebKitGTK, WKWebView) does not: there devicePixelRatio is reported as the screen's
// ratio times the scale, or the grid's canvas would be drawn at the screen's ratio and enlarged (blurred).
// In a browser (dev bridge) nothing is applied; the setting is kept for the desktop app.
// W8: the settings live in the engine's store and load only after it answers, so the scale last applied is also kept in
// the webview's own storage and applied at start (main.tsx), before the window has shown anything worth scaling; the
// setting stays the source of truth and corrects it once loaded. Known gap (§16): under WebKit the patched
// devicePixelRatio no longer agrees with `(resolution: …dppx)` media queries at a scale other than 100 %, so Monaco's
// pixel-ratio monitor (a media query built from devicePixelRatio) does not see a move to a monitor with another ratio:
// its minimap and overview ruler keep the old ratio until the window reloads (text is DOM and stays sharp; the grid
// reads devicePixelRatio each time it draws).
import { isTauri } from "../ipc/transport";
import { useSettings, settingLimits } from "../store/settings";
import { toast } from "../store/ui";
import { announce } from "../store/announce";

/** Ctrl+= / Ctrl+- move to the next multiple of this (95 % → 100 %, then 110 %). */
export const UI_SCALE_STEP = 10;

/** WebKit: page zoom leaves devicePixelRatio at the screen's ratio. */
export function zoomLeavesPixelRatio(ua = typeof navigator === "undefined" ? "" : navigator.userAgent): boolean {
  return /AppleWebKit/.test(ua) && !/(Chrome|Chromium|Edg)\//.test(ua);
}

let applied: number | null = null;
let ratioScale = 1;
let patched = false;
/** devicePixelRatio as the engine defined it on the window itself, if it did (put back by resetUiScale). */
let windowOwn: PropertyDescriptor | undefined;

/** From now on devicePixelRatio reads as the screen's ratio (still live: a move to another monitor) times `scale`. */
function scalePixelRatio(scale: number) {
  ratioScale = scale;
  if (patched) return;
  let owner: object | null = window;
  let own: PropertyDescriptor | undefined;
  while (owner && !(own = Object.getOwnPropertyDescriptor(owner, "devicePixelRatio"))) owner = Object.getPrototypeOf(owner);
  if (owner === window) windowOwn = own;
  const screenRatio = own?.get ? () => Number(own!.get!.call(window)) || 1 : () => Number(own?.value) || 1;
  try {
    Object.defineProperty(window, "devicePixelRatio", { configurable: true, get: () => screenRatio() * ratioScale });
    patched = true;
  } catch {
    // A ratio the engine will not let us redefine: canvases draw at the screen's ratio (enlarged), nothing else changes.
  }
}

/** Where the scale last applied is kept for the next start (W8): a number, not user data. */
export const UI_SCALE_CACHE = "querithm.uiScale";

/** The scale the window had when it last ran, if one in the setting's limits was kept. */
export function cachedUiScale(): number | null {
  try {
    const raw = localStorage.getItem(UI_SCALE_CACHE);
    const v = raw == null ? NaN : Number(raw);
    const { min, max } = settingLimits.uiScale;
    return Number.isFinite(v) && v >= min && v <= max ? v : null;
  } catch {
    return null; // storage refused (a locked-down webview): the window starts at 100 %
  }
}

function keepUiScale(percent: number) {
  try {
    localStorage.setItem(UI_SCALE_CACHE, String(percent));
  } catch {
    // Not kept: the next start applies the setting once it has loaded, as before W8.
  }
}

/** At start, before the first render: the scale the window last had (nothing at 100 %, or with none kept). */
export function applyCachedUiScale(): Promise<void> {
  const cached = cachedUiScale();
  return cached == null ? Promise.resolve() : applyUiScale(cached);
}

/** Applies `percent` to the window (the settings store has clamped it). Resolves once the webview took it. */
export async function applyUiScale(percent: number): Promise<void> {
  const scale = percent / 100;
  // At start the webview is at 100 %: the default costs no call to the shell.
  if (!isTauri || scale === (applied ?? 1)) return;
  const before = applied;
  applied = scale;
  // Before the zoom: the resize it causes redraws every canvas with the ratio that matches it.
  if (zoomLeavesPixelRatio()) scalePixelRatio(scale);
  try {
    const { getCurrentWebview } = await import("@tauri-apps/api/webview");
    await getCurrentWebview().setZoom(scale);
    keepUiScale(percent);
  } catch (e) {
    applied = before;
    if (patched) ratioScale = before ?? 1;
    toast("warn", `The interface scale could not be changed: ${e instanceof Error ? e.message : String(e)}`);
  }
}

/** The scale one step up (1) or down (-1) from `percent`, on multiples of the step, within the setting's limits. */
export function nextUiScale(percent: number, dir: 1 | -1): number {
  const { min, max } = settingLimits.uiScale;
  const next = dir > 0 ? Math.floor(percent / UI_SCALE_STEP) * UI_SCALE_STEP + UI_SCALE_STEP : Math.ceil(percent / UI_SCALE_STEP) * UI_SCALE_STEP - UI_SCALE_STEP;
  return Math.min(max, Math.max(min, next));
}

/** Ctrl+= (1), Ctrl+- (-1), Ctrl+0 (0, back to 100 %): changes the setting and says the new scale. */
export function stepUiScale(dir: 1 | -1 | 0) {
  const s = useSettings.getState();
  const to = dir === 0 ? 100 : nextUiScale(s.uiScale, dir);
  if (to !== s.uiScale) s.set({ uiScale: to });
  announce(`Interface scale ${to} %`);
}

/** Tests: forget what was applied and give devicePixelRatio back. */
export function resetUiScale() {
  applied = null;
  ratioScale = 1;
  if (patched) {
    if (windowOwn) Object.defineProperty(window, "devicePixelRatio", windowOwn);
    else delete (window as { devicePixelRatio?: number }).devicePixelRatio;
  }
  patched = false;
  windowOwn = undefined;
}

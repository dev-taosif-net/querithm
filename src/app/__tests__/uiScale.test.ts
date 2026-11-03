// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({ tauri: true, setZoom: vi.fn(async (_z: number) => {}) }));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return h.tauri; } }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => undefined) }));
vi.mock("@tauri-apps/api/webview", () => ({ getCurrentWebview: () => ({ setZoom: (z: number) => h.setZoom(z) }) }));

import { applyCachedUiScale, applyUiScale, cachedUiScale, nextUiScale, resetUiScale, stepUiScale, UI_SCALE_CACHE, zoomLeavesPixelRatio } from "../uiScale";
import { defaultSettings, useSettings } from "../../store/settings";
import { useUi } from "../../store/ui";
import { resetAnnouncer, useAnnounce } from "../../store/announce";

const WEBKITGTK = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15";
const WEBVIEW2 = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";
const ua = (s: string) => Object.defineProperty(navigator, "userAgent", { value: s, configurable: true });

beforeEach(() => {
  h.tauri = true;
  h.setZoom.mockReset();
  h.setZoom.mockResolvedValue(undefined);
  resetUiScale();
  resetAnnouncer();
  useSettings.setState({ uiScale: 100 });
  useUi.setState({ toasts: [] });
  ua(WEBVIEW2);
  localStorage.removeItem(UI_SCALE_CACHE);
});
afterEach(() => resetUiScale());

describe("interface scale (L26)", () => {
  it("is a setting of 90–150 %, 100 by default; the store clamps what it is given", () => {
    expect(defaultSettings.uiScale).toBe(100);
    useSettings.getState().set({ uiScale: 40 });
    expect(useSettings.getState().uiScale).toBe(90);
    useSettings.getState().set({ uiScale: 133.4 });
    expect(useSettings.getState().uiScale).toBe(133);
    useSettings.getState().set({ uiScale: 900 });
    expect(useSettings.getState().uiScale).toBe(150);
  });

  it("sets the webview's page zoom, once per change, and makes no call at the default 100 %", async () => {
    await applyUiScale(100);
    expect(h.setZoom).not.toHaveBeenCalled();
    await applyUiScale(125);
    await applyUiScale(125);
    expect(h.setZoom.mock.calls).toEqual([[1.25]]);
    await applyUiScale(100);
    expect(h.setZoom.mock.calls).toEqual([[1.25], [1]]);
  });

  it("does nothing in a browser (the dev bridge): the browser's own zoom applies there", async () => {
    h.tauri = false;
    await applyUiScale(150);
    expect(h.setZoom).not.toHaveBeenCalled();
  });

  it("Chromium (WebView2) reports page zoom in devicePixelRatio itself: it is left alone", async () => {
    const before = window.devicePixelRatio;
    await applyUiScale(150);
    expect(zoomLeavesPixelRatio(WEBVIEW2)).toBe(false);
    expect(window.devicePixelRatio).toBe(before);
  });

  it("WebKit (WebKitGTK) does not: devicePixelRatio reads as the screen's ratio times the scale, so canvases stay sharp", async () => {
    ua(WEBKITGTK);
    expect(zoomLeavesPixelRatio()).toBe(true);
    const screen = window.devicePixelRatio;
    await applyUiScale(125);
    expect(window.devicePixelRatio).toBeCloseTo(screen * 1.25);
    // The glide grid sizes its canvas by Math.ceil(devicePixelRatio): 2× at 125 % on a 1× screen, not an enlarged 1×.
    expect(Math.ceil(window.devicePixelRatio)).toBe(Math.ceil(screen * 1.25));
    await applyUiScale(100);
    expect(window.devicePixelRatio).toBeCloseTo(screen);
    resetUiScale();
    expect(window.devicePixelRatio).toBe(screen);
  });

  it("a webview that refuses the zoom says so, and the next change tries again", async () => {
    ua(WEBKITGTK);
    const screen = window.devicePixelRatio;
    h.setZoom.mockRejectedValueOnce(new Error("not allowed"));
    await applyUiScale(150);
    expect(useUi.getState().toasts.map((t) => `${t.kind}:${t.text}`)).toEqual(["warn:The interface scale could not be changed: not allowed"]);
    expect(window.devicePixelRatio).toBeCloseTo(screen); // back to what the webview shows
    await applyUiScale(150);
    expect(h.setZoom).toHaveBeenCalledTimes(2);
  });

  it("keeps the scale it applied for the next start, and applies a kept one at start (W8)", async () => {
    expect(cachedUiScale()).toBeNull();
    await applyCachedUiScale();
    expect(h.setZoom).not.toHaveBeenCalled(); // nothing kept: the window starts at 100 % with no call
    await applyUiScale(130);
    expect(localStorage.getItem(UI_SCALE_CACHE)).toBe("130");
    h.setZoom.mockRejectedValueOnce(new Error("not allowed"));
    await applyUiScale(140);
    expect(localStorage.getItem(UI_SCALE_CACHE)).toBe("130"); // a scale the webview refused is not kept
    resetUiScale(); // the next start
    h.setZoom.mockClear();
    await applyCachedUiScale();
    expect(h.setZoom.mock.calls).toEqual([[1.3]]);
    // Junk or a value outside the setting's limits is ignored.
    for (const junk of ["abc", "40", "900", ""]) {
      localStorage.setItem(UI_SCALE_CACHE, junk);
      expect(cachedUiScale()).toBeNull();
    }
  });

  it("steps by 10 % on multiples of 10 within the limits, resets to 100 %, and says the new scale", () => {
    expect([nextUiScale(100, 1), nextUiScale(95, 1), nextUiScale(95, -1), nextUiScale(90, -1), nextUiScale(150, 1), nextUiScale(147, 1)]).toEqual([110, 100, 90, 90, 150, 150]);
    stepUiScale(1);
    expect(useSettings.getState().uiScale).toBe(110);
    expect(useAnnounce.getState().text).toBe("Interface scale 110 %.");
    stepUiScale(-1);
    stepUiScale(-1);
    expect(useSettings.getState().uiScale).toBe(90);
    stepUiScale(0);
    expect(useSettings.getState().uiScale).toBe(100);
  });
});

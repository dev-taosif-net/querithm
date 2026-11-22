// @vitest-environment jsdom
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The element under test is the one index.html ships, so a renamed id there fails here.
const html = readFileSync(resolve(process.cwd(), "index.html"), "utf8");

type Splash = typeof import("../splash");
let splash: Splash;
const el = () => document.getElementById("splash")!;
const text = () => document.getElementById("splash-text")!.textContent;
const elapsed = () => document.getElementById("splash-elapsed")!.textContent;

/** A fresh page: index.html's start screen, and the module with nothing remembered. */
async function page(withSplash = true) {
  document.body.innerHTML = "";
  if (withSplash) document.body.appendChild(document.importNode(new DOMParser().parseFromString(html, "text/html").getElementById("splash")!, true));
  vi.resetModules();
  splash = await import("../splash");
}

beforeEach(async () => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "performance"] });
  await page();
});
afterEach(() => vi.useRealTimers());

describe("start screen (§16)", () => {
  it("is on the page from the first frame, at the first step, before any code has run", () => {
    expect(el().hidden).toBe(false);
    expect(el().dataset.stage).toBe("1");
    expect(text()).toBe("Starting the engine…");
    expect(el().querySelector('[role="status"]')).not.toBeNull();
  });

  it("names each step of the start on the element the tiers are styled from", () => {
    splash.showSplash();
    const seen: [string | undefined, string | null][] = [];
    for (const stage of [1, 2, 3, 4] as const) {
      splash.splashStage(stage);
      seen.push([el().dataset.stage, text()]);
    }
    expect(seen).toEqual([["1", "Starting the engine…"], ["2", "Loading your settings…"], ["3", "Restoring your workspace…"], ["4", "Ready"]]);
  });

  it("shows the seconds waited only once a start has taken 3 s", () => {
    splash.showSplash();
    vi.advanceTimersByTime(2000);
    expect(elapsed()).toBe("");
    vi.advanceTimersByTime(1000);
    expect(elapsed()).toBe(" · 3 s");
    vi.advanceTimersByTime(4000);
    expect(elapsed()).toBe(" · 7 s");
  });

  it("fades out when the app is ready, then stops drawing and counting", () => {
    splash.showSplash();
    splash.splashStage(4);
    splash.hideSplash();
    expect(el().dataset.leaving).toBe("");
    expect(el().hidden).toBe(false);
    vi.advanceTimersByTime(200);
    expect(el().hidden).toBe(true);
    expect(el().dataset.leaving).toBeUndefined();
    expect(vi.getTimerCount()).toBe(0);
    // A late step (a boot still finishing behind an error) changes nothing on a screen that is gone.
    splash.splashStage(2);
    expect(el().dataset.stage).toBe("4");
  });

  it("comes back at the first step, counting from zero, when the engine is restarted", () => {
    splash.showSplash();
    vi.advanceTimersByTime(5000);
    splash.hideSplash();
    vi.advanceTimersByTime(200);
    splash.showSplash();
    expect(el().hidden).toBe(false);
    expect(el().dataset.stage).toBe("1");
    expect(text()).toBe("Starting the engine…");
    expect(elapsed()).toBe("");
    vi.advanceTimersByTime(3000);
    expect(elapsed()).toBe(" · 3 s");
  });

  it("a restart during the fade keeps the screen instead of hiding it a moment later", () => {
    splash.showSplash();
    splash.hideSplash();
    vi.advanceTimersByTime(100);
    splash.showSplash();
    vi.advanceTimersByTime(1000);
    expect(el().hidden).toBe(false);
    expect(el().dataset.leaving).toBeUndefined();
  });

  it("showing a screen that is already there does not restart its count", () => {
    splash.showSplash();
    vi.advanceTimersByTime(4000);
    splash.showSplash();
    vi.advanceTimersByTime(1000);
    expect(elapsed()).toBe(" · 5 s");
  });

  it("does nothing on a page without the element", async () => {
    await page(false);
    expect(() => { splash.showSplash(); splash.splashStage(2); splash.hideSplash(); }).not.toThrow();
    expect(vi.getTimerCount()).toBe(0);
  });
});

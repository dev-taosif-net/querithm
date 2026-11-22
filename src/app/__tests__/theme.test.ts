// @vitest-environment jsdom
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({ tauri: true, themeKeep: vi.fn(async (_t: string) => {}) }));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return h.tauri; } }));
vi.mock("../../ipc/client", () => ({ shell: { themeKeep: (t: string) => h.themeKeep(t) } }));

import { cachedTheme, isDark, keepTheme, resetKeptTheme, THEME_CACHE } from "../theme";

function osDark(dark: boolean) {
  window.matchMedia = ((q: string) => ({ matches: dark, media: q, addEventListener() {}, removeEventListener() {} })) as any;
}

beforeEach(() => {
  h.tauri = true;
  h.themeKeep.mockReset();
  h.themeKeep.mockResolvedValue(undefined);
  resetKeptTheme();
  localStorage.removeItem(THEME_CACHE);
  document.documentElement.removeAttribute("data-theme");
  osDark(false);
});
afterEach(() => vi.restoreAllMocks());

describe("the theme kept for the next start (§16 start screen)", () => {
  it("reads back only the three theme words", () => {
    expect(cachedTheme()).toBeNull();
    for (const t of ["dark", "light", "system"] as const) {
      localStorage.setItem(THEME_CACHE, t);
      expect(cachedTheme()).toBe(t);
    }
    for (const bad of ["", "Light", "blue", "null", "<script>"]) {
      localStorage.setItem(THEME_CACHE, bad);
      expect(cachedTheme()).toBeNull();
    }
  });

  it("a webview that refuses storage keeps nothing and fails nobody", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("denied"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("denied"); });
    expect(cachedTheme()).toBeNull();
    expect(() => keepTheme("light")).not.toThrow();
    // The shell is still told: its copy does not depend on the webview's.
    expect(h.themeKeep.mock.calls).toEqual([["light"]]);
  });

  it("keeps the theme in the webview and tells the shell once per start and once per change", () => {
    keepTheme("light");
    keepTheme("light");
    expect(localStorage.getItem(THEME_CACHE)).toBe("light");
    expect(h.themeKeep.mock.calls).toEqual([["light"]]);
    keepTheme("system");
    expect(localStorage.getItem(THEME_CACHE)).toBe("system");
    expect(h.themeKeep.mock.calls).toEqual([["light"], ["system"]]);
  });

  it("tells the shell even when the webview's copy already matches (the shell's file may be older)", () => {
    localStorage.setItem(THEME_CACHE, "light");
    keepTheme("light");
    expect(h.themeKeep.mock.calls).toEqual([["light"]]);
  });

  it("a shell that could not keep it is told again the next time", async () => {
    h.themeKeep.mockRejectedValueOnce(new Error("disk full"));
    keepTheme("light");
    await Promise.resolve();
    await Promise.resolve();
    keepTheme("light");
    expect(h.themeKeep.mock.calls).toEqual([["light"], ["light"]]);
    expect(localStorage.getItem(THEME_CACHE)).toBe("light");
  });

  it("in a browser (the dev bridge) there is no shell to tell", () => {
    h.tauri = false;
    keepTheme("light");
    expect(localStorage.getItem(THEME_CACHE)).toBe("light");
    expect(h.themeKeep).not.toHaveBeenCalled();
  });

  it("system follows the OS; dark and light do not", () => {
    osDark(true);
    expect([isDark("dark"), isDark("light"), isDark("system")]).toEqual([true, false, true]);
    osDark(false);
    expect([isDark("dark"), isDark("light"), isDark("system")]).toEqual([true, false, false]);
  });
});

// public/boot-theme.js runs before the first paint, long before this bundle: it must come to the same answer as the app.
describe("boot-theme.js", () => {
  const source = readFileSync(resolve(process.cwd(), "public/boot-theme.js"), "utf8");
  const run = () => new Function(source)();

  it("reads the key the app writes", () => {
    expect(source).toContain(`localStorage.getItem("${THEME_CACHE}")`);
  });

  it.each([
    [null, false, "dark"], // nothing kept: the setting's default
    [null, true, "dark"],
    ["dark", false, "dark"],
    ["light", true, "light"],
    ["system", true, "dark"],
    ["system", false, "light"],
    ["blue", false, "dark"], // anything else: the default
  ])("kept %s with OS dark=%s names %s on the document, as the app would", (kept, os, expected) => {
    osDark(os);
    if (kept != null) localStorage.setItem(THEME_CACHE, kept);
    run();
    expect(document.documentElement.dataset.theme).toBe(expected);
    expect(isDark(cachedTheme() ?? "dark") ? "dark" : "light").toBe(expected);
  });

  it("starts in the default when storage is refused", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("denied"); });
    expect(run).not.toThrow();
    expect(document.documentElement.dataset.theme).toBe("dark");
  });
});

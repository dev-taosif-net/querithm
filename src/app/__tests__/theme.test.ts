// @vitest-environment jsdom
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({ tauri: true, themeKeep: vi.fn(async (_t: string) => {}) }));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return h.tauri; } }));
vi.mock("../../ipc/client", () => ({ shell: { themeKeep: (t: string) => h.themeKeep(t) } }));

import { bootColors, cachedTheme, cachedThemeChoice, isDark, keepTheme, keepThemeBoot, resetKeptTheme, shellThemeText, SHELL_KEEP_LIMIT_MS, THEME_BOOT_CACHE, THEME_CACHE } from "../theme";
import { APP_THEMES, appThemeById } from "../../theme/appThemes";

const Q_DARK = appThemeById("dark")!;
const Q_LIGHT = appThemeById("light")!;
/** The shell's writes are one at a time: let a resolved call finish and the next one start. */
const settle = () => new Promise((r) => setTimeout(r, 0));

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

  it("a webview that refuses storage keeps nothing and fails nobody", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("denied"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("denied"); });
    expect(cachedTheme()).toBeNull();
    expect(() => keepTheme("light", Q_DARK, Q_LIGHT)).not.toThrow();
    // The shell is still told: its copy does not depend on the webview's.
    await settle();
    expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"]]);
  });

  it("keeps the theme in the webview and tells the shell once per start and once per change", async () => {
    keepTheme("light", Q_DARK, Q_LIGHT);
    keepTheme("light", Q_DARK, Q_LIGHT);
    expect(localStorage.getItem(THEME_CACHE)).toBe("light");
    await settle();
    expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"]]);
    keepTheme("system", Q_DARK, Q_LIGHT);
    expect(localStorage.getItem(THEME_CACHE)).toBe("system");
    await settle();
    expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"], ["system #0f1117 #ffffff"]]);
  });

  it("tells the shell both picks' window colours, and again when only a pick changes (plans/app-themes §6.3)", async () => {
    keepTheme("system", appThemeById("cortado")!, appThemeById("vellum")!);
    await settle();
    keepTheme("system", appThemeById("cortado")!, Q_LIGHT);
    await settle();
    expect(h.themeKeep.mock.calls).toEqual([["system #221a13 #ffede0"], ["system #221a13 #ffffff"]]);
    expect(localStorage.getItem(THEME_CACHE)).toBe("system");
  });

  it("tells the shell even when the webview's copy already matches (the shell's file may be older)", async () => {
    localStorage.setItem(THEME_CACHE, "light");
    keepTheme("light", Q_DARK, Q_LIGHT);
    await settle();
    expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"]]);
  });

  it("a shell that could not keep it is told again the next time", async () => {
    h.themeKeep.mockRejectedValueOnce(new Error("disk full"));
    keepTheme("light", Q_DARK, Q_LIGHT);
    await settle();
    keepTheme("light", Q_DARK, Q_LIGHT);
    await settle();
    expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"], ["light #0f1117 #ffffff"]]);
    expect(localStorage.getItem(THEME_CACHE)).toBe("light");
  });

  it("one write at a time, the latest line only: stepping through cards never lands an older theme last", async () => {
    let finish!: () => void;
    h.themeKeep.mockImplementationOnce(() => new Promise<void>((r) => (finish = r)));
    keepTheme("dark", appThemeById("cortado")!, Q_LIGHT);
    keepTheme("dark", appThemeById("fathom")!, Q_LIGHT);
    keepTheme("dark", appThemeById("inkwell")!, Q_LIGHT);
    await settle();
    expect(h.themeKeep.mock.calls).toEqual([["dark #221a13 #ffffff"]]);
    finish();
    await settle();
    expect(h.themeKeep.mock.calls).toEqual([["dark #221a13 #ffffff"], ["dark #1a0621 #ffffff"]]);
  });

  it("a shell that never answers holds the next change back for 2 s at most (CLAUDE §4)", async () => {
    vi.useFakeTimers();
    try {
      h.themeKeep.mockImplementationOnce(() => new Promise<void>(() => {}));
      keepTheme("light", Q_DARK, Q_LIGHT);
      keepTheme("light", Q_DARK, appThemeById("floe")!);
      await vi.advanceTimersByTimeAsync(SHELL_KEEP_LIMIT_MS - 1);
      expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"]]);
      await vi.advanceTimersByTimeAsync(1);
      expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"], ["light #0f1117 #e8f6f6"]]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("in a browser (the dev bridge) there is no shell to tell", async () => {
    h.tauri = false;
    keepTheme("light", Q_DARK, Q_LIGHT);
    expect(localStorage.getItem(THEME_CACHE)).toBe("light");
    await settle();
    expect(h.themeKeep).not.toHaveBeenCalled();
  });

  it("every App Theme's line is one the shell reads: a word, two #rrggbb, within its length cap", () => {
    // The grammar of parse() in src-tauri/src/theme.rs; the cap is read from it so the two cannot drift apart.
    const cap = Number(/const MAX_LEN: usize = (\d+);/.exec(readFileSync(resolve(process.cwd(), "src-tauri/src/theme.rs"), "utf8"))?.[1]);
    expect(cap).toBeGreaterThan(0);
    for (const word of ["dark", "light", "system"] as const)
      for (const dark of APP_THEMES.filter((t) => t.mode === "dark"))
        for (const light of APP_THEMES.filter((t) => t.mode === "light")) {
          const line = shellThemeText(word, dark, light);
          expect(line).toMatch(/^(dark|light|system) #[0-9a-f]{6} #[0-9a-f]{6}$/);
          expect(line.length).toBeLessThanOrEqual(cap);
        }
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

  it("names the kept App Theme of the shown mode and its start colours, and the syntax choice (plans/app-themes §6.2)", () => {
    const root = document.documentElement;
    root.removeAttribute("data-app-theme");
    root.removeAttribute("style");
    osDark(false);
    localStorage.setItem(THEME_CACHE, "system");
    keepThemeBoot(appThemeById("cortado")!, appThemeById("vellum")!, "standard");
    run();
    expect(root.dataset.theme).toBe("light");
    expect(root.dataset.appTheme).toBe("vellum");
    expect(root.style.getPropertyValue("--boot-bg")).toBe(appThemeById("vellum")!.ui.bg);
    expect(root.dataset.syntax).toBe("standard");
    expect(bootColors(appThemeById("dark")!)).toEqual({ id: "dark", bg: "#0f1117", fg: "#e6edf3", muted: "#8c949e", idle: "#1e222e", track: "#1b1f2a" });
  });

  it("ignores a damaged or hostile boot cache: the mode alone, Querithm's colours", () => {
    const root = document.documentElement;
    root.removeAttribute("data-app-theme");
    root.removeAttribute("style");
    osDark(true);
    for (const bad of ["{", JSON.stringify({ v: 2 }), JSON.stringify({ v: 1, dark: { id: "x\"><script>", bg: "#000", fg: "red" } })]) {
      localStorage.setItem(THEME_BOOT_CACHE, bad);
      run();
      expect(root.dataset.appTheme).toBeUndefined();
      expect(root.style.getPropertyValue("--boot-bg")).toBe("");
    }
  });

  it("takes the syntax choice only from a version-1 cache, as the app does", () => {
    const root = document.documentElement;
    root.removeAttribute("data-syntax");
    localStorage.setItem(THEME_BOOT_CACHE, JSON.stringify({ v: 2, syntax: "forest" }));
    run();
    expect(root.dataset.syntax).toBeUndefined();
    expect(cachedThemeChoice().syntaxColors).toBeUndefined();
  });
});

// @vitest-environment jsdom
// App Theme data and generation (plans/app-themes §2, §13): the legacy mirror matches tokens.css, ids stay stable, the
// generated blocks are complete and scoped, and building them costs little at start.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  APP_THEMES, APP_THEME_STYLE_ID, appThemeCss, appThemeSelector, appThemeStylesInstalled, DARK_APP_THEME_IDS, ENV_TOKENS,
  installAppThemeStyles, LEGACY_APP_THEME, LIGHT_APP_THEME_IDS, STATE_TOKENS, tokenVars, UI_TOKENS,
} from "../appThemes";

const css = readFileSync(join(process.cwd(), "src/theme/tokens.css"), "utf8");
function block(selector: string): Record<string, string> {
  const start = css.indexOf(`${selector} {`);
  const body = css.slice(start, css.indexOf("}", start));
  return Object.fromEntries([...body.matchAll(/(--q-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
}
const LEGACY_CSS = { dark: block(':root[data-theme="dark"]'), light: block(':root[data-theme="light"]') };

describe("App Theme data", () => {
  it("ids are stable and in Settings order: Querithm first, then the new themes, dark before light (T11)", () => {
    expect(APP_THEMES.map((t) => t.id)).toEqual(["dark", "cortado", "fathom", "inkwell", "damson", "juniper", "light", "floe", "vellum"]);
    expect(DARK_APP_THEME_IDS).toEqual(["dark", "cortado", "fathom", "inkwell", "damson", "juniper"]);
    expect(LIGHT_APP_THEME_IDS).toEqual(["light", "floe", "vellum"]);
    expect(LEGACY_APP_THEME.dark.id).toBe("dark");
    expect(LEGACY_APP_THEME.light.id).toBe("light");
  });

  it("Querithm Dark and Light mirror their tokens.css blocks exactly; the blocks stay as they were (T3)", () => {
    for (const mode of ["dark", "light"] as const) {
      const t = LEGACY_APP_THEME[mode];
      expect(t.legacy).toBe(true);
      for (const [key, token] of Object.entries(UI_TOKENS)) {
        if (token === "--q-info") continue; // not in the legacy blocks: consumers fall back to --q-accent
        expect(t.ui[key as keyof typeof UI_TOKENS], `${mode} ${token}`).toBe(LEGACY_CSS[mode][token]);
      }
      expect(t.ui.info).toBe(t.ui.accent);
      expect(t.states).toBeUndefined();
      expect(tokenVars(t)).toEqual({});
    }
    // The frozen blocks carry no App Theme token: nothing for Querithm Dark / Light changed in them.
    for (const v of [...Object.values(STATE_TOKENS), "--q-info", "--q-warn-mark"]) {
      if (v === "--q-env-prod" || v === "--q-env-prod-fill") continue; // family tokens: present in both blocks since before
      expect(LEGACY_CSS.dark[v], v).toBeUndefined();
      expect(LEGACY_CSS.light[v], v).toBeUndefined();
    }
  });

  it("every value is a CSS color the tests can read, names fit Settings, and descriptions fit one line", () => {
    for (const t of APP_THEMES) {
      expect(t.name.length, t.id).toBeLessThanOrEqual(18);
      expect(t.description.length, t.id).toBeGreaterThan(20);
      expect(t.description.length, t.id).toBeLessThanOrEqual(90);
      for (const [k, v] of Object.entries(t.ui)) expect(v, `${t.id}.${k}`).toMatch(/^#[0-9a-f]{6}$|^rgba\(\d+, \d+, \d+, [\d.]+\)$/i);
      for (const [k, v] of Object.entries(t.sql)) expect(v, `${t.id}.sql.${k}`).toMatch(/^#[0-9a-f]{6}$/i);
      for (const [k, v] of Object.entries(t.states ?? {})) {
        if (k === "env") for (const [e, c] of Object.entries(v as object)) expect(c, `${t.id}.env.${e}`).toMatch(/^#[0-9a-f]{6}$/i);
        else if (typeof v === "number") expect(v, `${t.id}.${k}`).toBeGreaterThan(0);
        else expect(v, `${t.id}.${k}`).toMatch(/^#[0-9a-f]{6}$/i);
      }
    }
  });
});

describe("generated theme blocks", () => {
  it("a new theme's block sets every core token, the derived wait ages and its states; alphas are percentages", () => {
    for (const t of APP_THEMES.filter((x) => !x.legacy)) {
      const v = tokenVars(t);
      for (const token of Object.values(UI_TOKENS)) expect(v[token], `${t.id} ${token}`).toBeDefined();
      expect(v["--q-wait-short"]).toBe(t.ui.ok);
      expect(v["--q-wait-long"]).toBe(t.ui.err);
      expect(v["--q-toast-alpha"], t.id).toMatch(/^\d+(\.\d+)?%$/);
      expect(v["--q-disabled-opacity"], t.id).toMatch(/^0\.\d+$/);
      for (const token of Object.values(ENV_TOKENS)) expect(v[token], `${t.id} ${token}`).toMatch(/^#/);
    }
  });

  it("each block applies only when the mode and the id agree (I4), and starts with its color scheme", () => {
    const out = appThemeCss();
    for (const t of APP_THEMES) {
      if (t.legacy) expect(out).not.toContain(`data-app-theme="${t.id}"`);
      else expect(out).toContain(`${appThemeSelector(t)} {\n  color-scheme: ${t.mode};`);
    }
    expect(appThemeSelector(APP_THEMES[1])).toBe(':root[data-theme="dark"][data-app-theme="cortado"]');
    expect(out.match(/\{/g)).toHaveLength(7);
  });

  it("every legacy token a block leaves out is a family token it inherits from its mode (environment marks, icons, charts)", () => {
    const FAMILY = /^--q-(env-|obj-|chart-|edge-shadow)/;
    for (const t of APP_THEMES.filter((x) => !x.legacy)) {
      const v = tokenVars(t);
      const missing = Object.keys(LEGACY_CSS[t.mode]).filter((k) => !(k in v) && !FAMILY.test(k));
      expect(missing, t.id).toEqual([]);
    }
  });

  it("installing is idempotent (HMR) and reports success", () => {
    expect(installAppThemeStyles(document)).toBe(true);
    expect(installAppThemeStyles(document)).toBe(true);
    expect(document.querySelectorAll(`#${APP_THEME_STYLE_ID}`)).toHaveLength(1);
    expect(document.getElementById(APP_THEME_STYLE_ID)!.textContent).toBe(appThemeCss());
    expect(appThemeStylesInstalled()).toBe(true);
  });

  it("a block the browser refused (no style sheet made, as under a style-src policy) is not reported as installed", () => {
    const el = document.getElementById(APP_THEME_STYLE_ID) ?? document.head.appendChild(Object.assign(document.createElement("style"), { id: APP_THEME_STYLE_ID }));
    Object.defineProperty(el, "sheet", { configurable: true, get: () => null });
    try {
      expect(installAppThemeStyles(document)).toBe(false);
      expect(appThemeStylesInstalled()).toBe(false);
    } finally {
      delete (el as { sheet?: unknown }).sheet; // the prototype's getter again
      expect(installAppThemeStyles(document)).toBe(true);
    }
  });

  it("building the stylesheet stays well under a millisecond-scale budget at start (measured, not assumed)", () => {
    for (let i = 0; i < 5; i++) appThemeCss(); // warm
    const runs = Array.from({ length: 50 }, () => {
      const t0 = performance.now();
      appThemeCss();
      return performance.now() - t0;
    }).sort((a, b) => a - b);
    const p95 = runs[Math.floor(runs.length * 0.95)];
    // Measured locally at about 0.05–0.2 ms; the bound leaves CI headroom (cold start budget: 1.5 s, CLAUDE.md §4).
    expect(p95).toBeLessThan(5);
  });
});

// @vitest-environment jsdom
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderHook } from "@testing-library/react";
import {
  TAB_SURFACES, bandColor, clearColorCache, connectionText, contrastRatio, fillForWhite, readableOn, readableOnAll, readableOnSurface, readableOnTint, themeObserverCount, toOklch,
  useThemeKey,
} from "../tabStyle";

const css = readFileSync(join(process.cwd(), "src/theme/tokens.css"), "utf8");
const rgb = (h: string) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)) as [number, number, number];

const themes = [...css.matchAll(/--q-bg:/g)].slice(0, 2).map((m) => {
  const block = css.slice(m.index, m.index + 4000);
  const get = (name: string) => block.match(new RegExp(`${name}:\\s*(#[0-9a-f]{6})`, "i"))![1];
  return {
    panel: get("--q-panel"), hover: get("--q-hover"), rowSel: get("--q-row-sel"), accent: get("--q-accent"), lineStrong: get("--q-line-strong"),
    warnSolid: get("--q-warn-solid"), warnSolidFg: get("--q-warn-solid-fg"), raised: get("--q-raised"), overlaySel: get("--q-overlay-sel"),
    envs: ["local", "dev", "test", "staging", "prod"].map((e) => get(`--q-env-${e}`)),
  };
});

describe("tab surfaces (strip redesign D1–D3)", () => {
  it("the active tab is the Explorer's selection; every other tab is transparent on the panel", () => {
    expect(css).toContain("--dv-activegroup-visiblepanel-tab-background-color: var(--q-row-sel);");
    for (const v of ["activegroup-hiddenpanel", "inactivegroup-visiblepanel", "inactivegroup-hiddenpanel"]) {
      expect(css).toContain(`--dv-${v}-tab-background-color: transparent;`);
    }
  });

  it("the strip's scroll thumb uses the variable Dockview reads, in --q-line-strong", () => {
    expect(css).toContain("--dv-scrollbar-background-color: var(--q-line-strong);");
    expect(css).not.toContain("--dv-tabs-container-scrollbar-color");
  });

  it("the close slot sits on line 1 and every tab keeps its room, so activation never changes the name's width (D12, revised in c2b56db)", () => {
    const rule = (sel: string) => css.slice(css.indexOf(`${sel} {`), css.indexOf("}", css.indexOf(`${sel} {`)));
    expect(rule(".q-tab-slot")).toContain("position: absolute;");
    // 26 px in the tab's width (+38 px in all), kept by every tab. A width, not a flex-basis: the content size a tab
    // takes its width from ignores flex-basis (a basis alone made mid-length tabs 22 px narrower).
    const spacer = rule(".q-tab-top::after");
    expect(spacer).toContain("width: 26px;");
    expect(spacer).toContain("min-width: 26px;");
    expect(spacer).toContain("flex-shrink: 0;");
    expect(spacer).not.toMatch(/flex(-basis)?:/);
    // Drawn over a name, the × hides it behind the tab's own surface.
    expect(rule(".q-tab-x")).toContain("background: var(--q-row-bg);");
  });

  it.each([0, 1])("theme %i: the active bar reaches 3:1 on the active tab, the split bar 3:1 on the panel", (i) => {
    const t = themes[i];
    expect(contrastRatio(rgb(t.accent), rgb(t.rowSel))).toBeGreaterThanOrEqual(3);
    expect(contrastRatio(rgb(t.lineStrong), rgb(t.panel))).toBeGreaterThanOrEqual(3);
  });

  it.each([0, 1])("theme %i: the TX pill's text reaches 4.5:1 on it, and the pill 3:1 on every tab surface", (i) => {
    const t = themes[i];
    expect(contrastRatio(rgb(t.warnSolidFg), rgb(t.warnSolid))).toBeGreaterThanOrEqual(4.5);
    for (const s of [t.panel, t.hover, t.rowSel]) expect(contrastRatio(rgb(t.warnSolid), rgb(s))).toBeGreaterThanOrEqual(3);
  });
});

const hueShift = (a: string, b: string) => {
  const d = Math.abs(toOklch(rgb(a))[2] - toOklch(rgb(b))[2]) * (180 / Math.PI);
  return Math.min(d, 360 - d);
};

describe("connection text and the Prod band on a tab", () => {
  it("reads both themes from the tokens", () => expect(themes).toHaveLength(2));

  it("the database text reaches 4.5:1 on every tab surface for every environment and custom colours, keeping the hue", () => {
    for (const t of themes) {
      const surfaces = [t.panel, t.hover, t.rowSel];
      for (const env of [...t.envs, "#ff00ff", "#0ea5e9", "#facc15"]) {
        const out = readableOnAll(env, surfaces)!;
        for (const bg of surfaces) expect(contrastRatio(rgb(out), rgb(bg)), `${env} on ${bg}`).toBeGreaterThanOrEqual(4.5);
        expect(hueShift(env, out), `${env} → ${out}`).toBeLessThan(12);
      }
    }
  });

  it("the Ctrl+P and Ctrl+Tab rows fit the same way on the list and its highlighted row", () => {
    for (const t of themes) {
      const surfaces = [t.raised, t.hover, t.overlaySel];
      for (const env of t.envs) {
        const out = readableOnAll(env, surfaces)!;
        for (const bg of surfaces) expect(contrastRatio(rgb(out), rgb(bg)), `${env} on ${bg}`).toBeGreaterThanOrEqual(4.5);
      }
    }
  });

  it("keeps the color saturated instead of washing it toward the text color", () => {
    const dark = themes[0];
    const prod = readableOn(dark.envs[4], dark.rowSel)!;
    expect(toOklch(rgb(prod))[1]).toBeGreaterThan(0.08);
    expect(toOklch(rgb(readableOn(dark.envs[1], dark.rowSel)!))[1]).toBeGreaterThan(0.08);
  });

  it("leaves a color that already passes unchanged; lightens on dark, darkens on light", () => {
    expect(readableOn("#ffffff", "#000000")).toBe("#ffffff");
    const onDark = readableOn("#ef4444", "#264476")!;
    const onLight = readableOn("#d97706", "#bcd2fa")!;
    expect(toOklch(rgb(onDark))[0]).toBeGreaterThan(toOklch(rgb("#ef4444"))[0]);
    expect(toOklch(rgb(onLight))[0]).toBeLessThan(toOklch(rgb("#d97706"))[0]);
    expect(readableOn("#abc", "#000")).toBe("#aabbcc");
    expect(readableOn("red", "#000")).toBeNull();
  });

  describe("on the page", () => {
    const root = document.documentElement.style;
    const vars = (t: (typeof themes)[number]) => {
      root.setProperty("--q-panel", t.panel);
      root.setProperty("--q-hover", t.hover);
      root.setProperty("--q-row-sel", t.rowSel);
      root.setProperty("--q-env-prod", t.envs[4]);
      root.setProperty("--q-env-dev", t.envs[1]);
      clearColorCache();
    };
    afterEach(() => {
      for (const v of ["--q-panel", "--q-hover", "--q-row-sel", "--q-env-prod", "--q-env-dev", "--q-muted"]) root.removeProperty(v);
      clearColorCache();
    });

    it.each([0, 1])("theme %i: the Prod band reaches 3:1 on every tab surface; the raw colour is kept when it passes", (i) => {
      const t = themes[i];
      vars(t);
      const band = bandColor("var(--q-env-prod)");
      for (const s of [t.panel, t.hover, t.rowSel]) expect(contrastRatio(rgb(band), rgb(s))).toBeGreaterThanOrEqual(3);
      expect(band).toBe(t.envs[4]);
    });

    it.each([0, 1])("theme %i: connectionText fits a theme variable on the tab's three surfaces", (i) => {
      const t = themes[i];
      vars(t);
      const dev = connectionText("var(--q-env-dev)");
      for (const s of [t.panel, t.hover, t.rowSel]) expect(contrastRatio(rgb(dev), rgb(s))).toBeGreaterThanOrEqual(4.5);
      expect(connectionText("var(--q-env-nope)")).toBe("var(--q-env-nope)");
      expect(TAB_SURFACES).toEqual(["var(--q-panel)", "var(--q-hover)", "var(--q-row-sel)"]);
    });

    it("reads each CSS variable once per theme, not on every call (D26)", () => {
      vars(themes[0]);
      const spy = vi.spyOn(window, "getComputedStyle");
      for (let i = 0; i < 50; i++) connectionText("var(--q-env-dev)");
      expect(spy.mock.calls.length).toBeLessThanOrEqual(4);
      spy.mockRestore();
    });

    it("one theme observer serves every subscriber, and a theme switch empties the caches", async () => {
      const a = renderHook(() => useThemeKey());
      const b = renderHook(() => useThemeKey());
      expect(themeObserverCount()).toBe(1);
      vars(themes[0]);
      const dark = connectionText("var(--q-env-dev)");
      root.setProperty("--q-env-dev", themes[1].envs[1]);
      root.setProperty("--q-panel", themes[1].panel);
      root.setProperty("--q-hover", themes[1].hover);
      root.setProperty("--q-row-sel", themes[1].rowSel);
      expect(connectionText("var(--q-env-dev)")).toBe(dark); // cached until the theme changes
      document.documentElement.dataset.theme = "light";
      await new Promise((r) => setTimeout(r, 0));
      expect(connectionText("var(--q-env-dev)")).not.toBe(dark);
      a.unmount();
      b.unmount();
      expect(themeObserverCount()).toBe(0);
      delete document.documentElement.dataset.theme;
    });

    it("darkens a fill under white text; unknown variables give null or the input", () => {
      root.setProperty("--q-env-dev", "#10b981");
      clearColorCache();
      const fill = fillForWhite("var(--q-env-dev)");
      expect(contrastRatio([255, 255, 255], rgb(fill))).toBeGreaterThanOrEqual(4.5);
      expect(hueShift("#10b981", fill)).toBeLessThan(8);
      expect(fillForWhite("var(--q-env-nope)")).toBe("var(--q-env-nope)");
      expect(readableOnSurface("var(--q-env-nope)", "var(--q-panel)")).toBeNull();
    });
  });
});

describe("readableOnTint (the profile editor's environment pill)", () => {
  const panels = [...css.matchAll(/--q-bg:/g)].slice(0, 2).map((m) => {
    const block = css.slice(m.index, m.index + 4000);
    const get = (name: string) => block.match(new RegExp(`${name}:\\s*(#[0-9a-f]{6})`, "i"))![1];
    return { panel2: get("--q-panel-2"), envs: ["local", "dev", "test", "staging", "prod"].map((e) => get(`--q-env-${e}`)) };
  });
  const tint = (fg: string, bg: string, a: number) => rgb(fg).map((x, i) => x * a + rgb(bg)[i] * (1 - a)) as [number, number, number];

  it("reaches 4.5:1 on a 14% tint of the environment over the side panel, in both themes, keeping the hue", () => {
    expect(panels).toHaveLength(2);
    for (const t of panels)
      for (const env of t.envs) {
        const fit = readableOnTint(env, t.panel2, 0.14)!;
        expect(contrastRatio(rgb(fit), tint(env, t.panel2, 0.14)), `${env} on ${t.panel2}`).toBeGreaterThanOrEqual(4.5);
        expect(hueShift(fit, env), env).toBeLessThan(12);
      }
  });

  it("returns null when a color cannot be read", () => {
    expect(readableOnTint("not-a-color", panels[0].panel2, 0.14)).toBeNull();
  });
});

describe("readableOnAll (Explorer environment labels, B6)", () => {
  const tree = [...css.matchAll(/--q-bg:/g)].slice(0, 2).map((m) => {
    const block = css.slice(m.index, m.index + 4000);
    const get = (name: string) => block.match(new RegExp(`${name}:\\s*(#[0-9a-f]{6})`, "i"))![1];
    return { surfaces: [get("--q-panel"), get("--q-hover"), get("--q-row-sel"), get("--q-panel-2")], envs: ["local", "dev", "test", "staging", "prod"].map((e) => get(`--q-env-${e}`)) };
  });

  it("reaches 4.5:1 at rest, on hover and selected for every environment and custom colors, keeping the hue", () => {
    expect(tree).toHaveLength(2);
    for (const t of tree)
      for (const env of [...t.envs, "#ff00ff", "#0ea5e9", "#facc15"]) {
        const fit = readableOnAll(env, t.surfaces)!;
        for (const s of t.surfaces) expect(contrastRatio(rgb(fit), rgb(s)), `${env} on ${s}`).toBeGreaterThanOrEqual(4.5);
        expect(hueShift(fit, env), env).toBeLessThan(12);
      }
  });

  it("a color fitted to the panel alone fails on the selected row (the bug it fixes)", () => {
    const dark = tree[0];
    const prod = readableOn(dark.envs[4], dark.surfaces[0])!;
    expect(contrastRatio(rgb(prod), rgb(dark.surfaces[2]))).toBeLessThan(4.5);
  });

  it("returns null when a color cannot be read", () => {
    expect(readableOnAll("not-a-color", tree[0].surfaces)).toBeNull();
  });
});

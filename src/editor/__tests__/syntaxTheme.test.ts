import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
// Monaco's own base themes: the rules our theme inherits from (inherit: true). The module ships no types.
// @ts-expect-error untyped internal module of monaco-editor
import { vs as vsTheme, vs_dark as vsDarkTheme } from "monaco-editor/esm/vs/editor/standalone/common/themes.js";
import {
  DEFAULT_SYNTAX_THEME, PREVIEW, roleColor, SUGGEST_COLORS, SYNTAX_THEMES, syntaxRules, syntaxThemeById, TOKEN_ROLES,
  type SyntaxThemeId,
} from "../syntaxTheme";
import { oracleLanguage, pgLanguage, pgLanguageConfiguration, tsqlLanguage, tsqlLanguageConfiguration } from "../languages";
import { contrast, deltaE2000, oklch, over, parseHex, simulate, toHex, CVD } from "./colorScience";

const css = readFileSync(new URL("../../theme/tokens.css", import.meta.url), "utf8");

function themeVars(selector: string): Record<string, string> {
  const start = css.indexOf(`${selector} {`);
  const body = css.slice(start, css.indexOf("}", start));
  return Object.fromEntries([...body.matchAll(/(--q-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
}
const MODES = { dark: themeVars(':root[data-theme="dark"]'), light: themeVars(':root[data-theme="light"]') };
type BaseTheme = { base: string; rules: { token: string }[] };
const BASE_THEMES = [vsTheme, vsDarkTheme] as BaseTheme[];

/** Every surface code is read on: editor, current line, a selection on each, and a changed line in a diff (tint twice). */
function editorLayers(v: Record<string, string>) {
  const bg = parseHex(v["--q-bg"]), line = parseHex(v["--q-panel"]);
  const twice = (tint: string) => over(tint, over(tint, bg));
  return {
    background: bg, "current line": line, selection: over(v["--q-sel"], bg), "selection on current line": over(v["--q-sel"], line),
    "diff insert": twice(v["--q-diff-insert"]), "diff remove": twice(v["--q-diff-remove"]),
  };
}

const ROLES = ["keyword", "type", "string", "number", "comment", "variable", "function", "operator", "ident"] as const;
/** Pairs a reader must tell apart at a glance (panel vote V11; variable/type added in round 2). */
const KEY_PAIRS = [
  ["keyword", "ident"], ["keyword", "function"], ["keyword", "type"], ["string", "ident"], ["variable", "ident"], ["function", "ident"],
  ["type", "ident"], ["number", "string"], ["comment", "ident"], ["string", "keyword"], ["number", "ident"], ["variable", "string"],
  ["operator", "ident"], ["variable", "type"],
] as const;

/** What each theme promises on top of the base rules (Settings describes these to the user). */
interface TypePromise {
  minContrast?: number;
  keyFloorSim?: number;
  allFloorSim?: number;
  /** OKLCH hue band per role, in degrees. */
  hueBands?: Partial<Record<(typeof ROLES)[number], [number, number]>>;
  /** Minimum CIEDE2000 distance of every role from the UI's error and production reds. */
  redDistance?: number;
  /** At most this many chromatic colors (OKLCH C >= 0.04) among the nine roles. */
  maxChromatic?: number;
  /** Under protan, deutan and tritan simulation these roles stay this far (CIEDE2000) from every other role. */
  anchorsSim?: { roles: readonly (typeof ROLES)[number][]; floor: number };
}
/** The 2026-11-01 panel's rule: commented-out SQL never reads as live code and keywords never merge into another role. */
const ANCHORED: TypePromise = { anchorsSim: { roles: ["keyword", "comment"], floor: 8 }, redDistance: 10 };
const PROMISES: Record<SyntaxThemeId, TypePromise> = {
  standard: {},
  "color-blind": { keyFloorSim: 12, allFloorSim: 8 },
  neon: ANCHORED,
  forest: ANCHORED,
  ember: ANCHORED,
  ocean: ANCHORED,
};

const cases = SYNTAX_THEMES.flatMap((t) => (["dark", "light"] as const).map((mode) => [t.id, mode, t[mode], t] as const));
const pairs = () => ROLES.flatMap((a, i) => ROLES.slice(i + 1).map((b) => [a, b] as const));
const isKey = (a: string, b: string) => KEY_PAIRS.some(([x, y]) => (x === a && y === b) || (x === b && y === a));

/** Every token string a Monarch tokenizer can emit (actions, cases and brackets). */
function emittedTokens(lang: ReturnType<typeof tsqlLanguage>): Set<string> {
  const out = new Set<string>();
  const walk = (a: unknown): void => {
    if (typeof a === "string") out.add(a);
    else if (Array.isArray(a)) a.slice(1).forEach(walk);
    else if (a && typeof a === "object") {
      const o = a as { token?: string; cases?: Record<string, unknown> };
      if (o.token) out.add(o.token);
      if (o.cases) Object.values(o.cases).forEach(walk);
    }
  };
  for (const rules of Object.values(lang.tokenizer)) for (const r of rules) walk(r);
  for (const b of (lang.brackets ?? []) as { token: string }[]) out.add(b.token);
  return out;
}
const UNSTYLED = new Set(["", "white", "@brackets"]);
/** How specific a rule is for a token, as Monaco ranks it: segments matched, or -1 when it does not match. */
const specificity = (rule: string, token: string) => (rule === "" ? 0 : token === rule || token.startsWith(`${rule}.`) ? rule.split(".").length : -1);

describe("SQL syntax themes: token coverage", () => {
  it.each([["tsql", tsqlLanguage()], ["pgsql", pgLanguage()], ["oracle", oracleLanguage()]])("every %s token kind has a rule (identifiers are not left as plain text)", (_, lang) => {
    const missing = [...emittedTokens(lang)].filter((t) => !UNSTYLED.has(t) && !TOKEN_ROLES.some((r) => specificity(r.token, t) > 0));
    expect(missing).toEqual([]);
  });

  it.each([["tsql", tsqlLanguage()], ["pgsql", pgLanguage()], ["oracle", oracleLanguage()]])("on %s our rule out-ranks Monaco's base theme for every token, so hex numbers follow the theme", (_, lang) => {
    // 0x1F took the base theme's number.hex color (5BB498 / 3030C0) before an explicit rule existed.
    const tokens = [...emittedTokens(lang)].filter((t) => !UNSTYLED.has(t)).map((t) => `${t}.${lang.tokenPostfix!.slice(1)}`);
    for (const base of BASE_THEMES) {
      const lost = tokens.filter((t) => {
        const ours = Math.max(...TOKEN_ROLES.map((r) => specificity(r.token, t)));
        const theirs = Math.max(...base.rules.map((r) => specificity(r.token, t)));
        return theirs > ours;
      });
      expect(lost, base.base).toEqual([]);
    }
    expect(TOKEN_ROLES.find((r) => r.token === "number.hex")?.role).toBe("number");
  });

  it("T-SQL [names] are not bracket pairs, so the ]] escape in [a]]b] is never painted as an unexpected bracket", () => {
    expect(tsqlLanguageConfiguration.brackets).toEqual([["(", ")"]]);
    expect(tsqlLanguageConfiguration.autoClosingPairs).toContainEqual({ open: "[", close: "]" });
    expect(tsqlLanguageConfiguration.surroundingPairs).toContainEqual({ open: "[", close: "]" });
  });

  it("PostgreSQL keeps [ ] as pairs for arrays; both dialects colorize only ( )", () => {
    expect(pgLanguageConfiguration.brackets).toContainEqual(["[", "]"]);
    expect(tsqlLanguageConfiguration.colorizedBracketPairs).toEqual([["(", ")"]]);
    expect(pgLanguageConfiguration.colorizedBracketPairs).toEqual([["(", ")"]]);
  });
});

describe("SQL syntax themes: registry", () => {
  it("Standard is the default and comes first; an unknown id falls back to it", () => {
    expect(DEFAULT_SYNTAX_THEME).toBe("standard");
    expect(SYNTAX_THEMES[0].id).toBe("standard");
    expect(syntaxThemeById("no-such-theme").id).toBe("standard");
    expect(syntaxThemeById(undefined).id).toBe("standard");
    expect(new Set(SYNTAX_THEMES.map((t) => t.id)).size).toBe(SYNTAX_THEMES.length);
    for (const t of SYNTAX_THEMES) expect(Object.keys(PROMISES)).toContain(t.id);
  });

  it.each(["high-contrast", "classic", "minimal"])("the removed %s theme, still saved in an older store.db, falls back to Standard", (id) => {
    expect(SYNTAX_THEMES.some((t) => t.id === (id as SyntaxThemeId))).toBe(false);
    expect(syntaxThemeById(id).id).toBe("standard");
  });

  it.each(SYNTAX_THEMES.map((t) => [t.id, t] as const))("%s has a short Settings name, a description and the shared styles", (_, t) => {
    expect(t.label.length).toBeLessThanOrEqual(18);
    expect(t.description.length).toBeGreaterThan(20);
    expect(t.description.length).toBeLessThanOrEqual(90);
    expect(t.fontStyle.keyword).toMatch(/bold/);
    expect(t.fontStyle.comment).toMatch(/italic/);
  });

  it.each(cases)("%s %s: every color is #rrggbb and every Monaco rule gets one", (_, mode, p, t) => {
    for (const [role, hex] of Object.entries(p)) expect(hex, role).toMatch(/^#[0-9a-f]{6}$/i);
    for (const r of syntaxRules(t, mode, MODES[mode]["--q-fg"])) expect(r.foreground, r.token).toMatch(/^[0-9a-f]{6}$/i);
    expect(syntaxRules(t, mode, "#123456").find((r) => r.token === "delimiter")?.foreground).toBe(p.delimiter?.slice(1) ?? "123456");
  });

  it("the Settings preview only uses roles a palette colors", () => {
    const p = SYNTAX_THEMES[0].dark;
    for (const tok of PREVIEW.flat()) if (tok.role !== "plain" && tok.role !== "paren") expect(roleColor(p, tok.role, "#000000")).toMatch(/^#/);
  });
});

describe("SQL syntax themes: readability", () => {
  it.each(cases)("%s %s: every role, comments included, is readable on every editor layer", (id, mode, p) => {
    const min = PROMISES[id].minContrast ?? 4.5;
    const failures: string[] = [];
    const colors = { ...p, temp: roleColor(p, "temp", ""), batch: roleColor(p, "batch", ""), escape: roleColor(p, "escape", "") };
    for (const [layer, bg] of Object.entries(editorLayers(MODES[mode])))
      for (const [role, hex] of Object.entries(colors)) {
        const r = contrast(hex, bg);
        if (r < min) failures.push(`${role} ${hex} on ${layer} (${toHex(bg)}): ${r.toFixed(2)}`);
      }
    expect(failures).toEqual([]);
  });

  it.each(cases)("%s %s: roles stay apart in normal vision and under protan, deutan and tritan simulation", (id, _mode, p) => {
    const promise = PROMISES[id];
    const failures: string[] = [];
    const vision = { normal: (h: string) => h, ...Object.fromEntries(Object.keys(CVD).map((k) => [k, (h: string) => simulate(h, k as keyof typeof CVD)])) };
    for (const [name, see] of Object.entries(vision))
      for (const [a, b] of pairs()) {
        if (p[a] === p[b]) continue; // merged on purpose (one color for both roles)
        const d = deltaE2000(see(p[a]), see(p[b]));
        const keyFloor = name === "normal" ? 15 : promise.keyFloorSim ?? 8;
        const anchored = name !== "normal" && promise.anchorsSim?.roles.some((r) => r === a || r === b);
        const allFloor = name === "normal" ? 12 : Math.max(promise.allFloorSim ?? 0, anchored ? promise.anchorsSim!.floor : 0);
        if (isKey(a, b) && d < keyFloor) failures.push(`${name} key pair ${a}/${b}: ΔE ${d.toFixed(1)} < ${keyFloor}`);
        else if (d < allFloor) failures.push(`${name} ${a}/${b}: ΔE ${d.toFixed(1)} < ${allFloor}`);
      }
    expect(failures).toEqual([]);
  });

  it.each(cases)("%s %s: keeps its type's promise", (id, mode, p) => {
    const promise = PROMISES[id];
    for (const [role, [lo, hi]] of Object.entries(promise.hueBands ?? {})) {
      const { h, C } = oklch(p[role as (typeof ROLES)[number]]);
      expect(C, `${role} is chromatic`).toBeGreaterThanOrEqual(0.04);
      expect(h, `${role} hue`).toBeGreaterThanOrEqual(lo);
      expect(h, `${role} hue`).toBeLessThanOrEqual(hi);
    }
    if (promise.redDistance)
      for (const red of ["--q-err", "--q-err-solid", "--q-env-prod"])
        for (const [role, hex] of Object.entries(p)) expect(deltaE2000(hex!, MODES[mode][red]), `${role} vs ${red}`).toBeGreaterThanOrEqual(promise.redDistance);
    if (promise.maxChromatic) {
      const chromatic = new Set(ROLES.map((r) => p[r].toLowerCase()).filter((h) => oklch(h).C >= 0.04));
      expect(chromatic.size).toBeLessThanOrEqual(promise.maxChromatic);
    }
  });
});

describe("UI colors the syntax theme never touches", () => {
  const SURFACES = ["--q-bg", "--q-panel", "--q-panel-2", "--q-raised", "--q-hover", "--q-row-sel", "--q-active", "--q-overlay-sel"];

  it.each(Object.entries(MODES))("%s: identifier text in dialogs, plans and the Explorer reads on every surface, selected rows included", (_, v) => {
    // The light value aliased a syntax color and fell to 3.25:1 on a selected row before --q-ident existed.
    for (const s of SURFACES) expect(contrast(v["--q-ident"], v[s]), s).toBeGreaterThanOrEqual(4.5);
    expect(css).toMatch(/--color-ident:\s*var\(--q-ident\)/);
  });

  it.each(Object.entries(MODES))("%s suggest popup: matched characters read on a row and on the selected row", (_, v) => {
    // The light keyword color used here was 3.54:1 on the selected row.
    const match = v[SUGGEST_COLORS["editorSuggestWidget.highlightForeground"]];
    expect(SUGGEST_COLORS["editorSuggestWidget.focusHighlightForeground"]).toBe("--q-suggest-match");
    for (const s of ["--q-raised", "--q-active"]) expect(contrast(match, v[s]), s).toBeGreaterThanOrEqual(4.5);
  });

  it.each(Object.entries(MODES))("%s suggest popup: the selected row stands out from the popup and from a hovered row", (_, vars) => {
    const c = (key: string) => vars[SUGGEST_COLORS[key]];
    const selected = c("editorSuggestWidget.selectedBackground");
    expect(contrast(selected, c("editorSuggestWidget.background"))).toBeGreaterThanOrEqual(1.3);
    expect(contrast(selected, c("list.hoverBackground"))).toBeGreaterThanOrEqual(1.2);
    expect(contrast(c("editorSuggestWidget.selectedForeground"), selected)).toBeGreaterThanOrEqual(7);
  });
});

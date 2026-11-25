// Every SQL palette a user can pick reads on every App Theme (plans/app-themes T8): the theme's own Default palette and
// the six syntax themes, on the editor, the current line, a selection (alone and over the current line), both diff
// tints, the find match and the word highlight; plus the rules the Default palettes promise (side-by-side pairs, CTE
// names, squiggle marks).
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { APP_THEMES, tokenVars } from "../appThemes";
import { appSqlPalette } from "../resolve";
import { roleColor, SYNTAX_THEMES, type SyntaxPalette } from "../../editor/syntaxTheme";
import { WARN_MARK_FALLBACK } from "../../editor/themeState";
import { contrast, deltaE2000, over, parseHex, simulate, CVD, type Rgb } from "../../editor/__tests__/colorScience";

const css = readFileSync(new URL("../tokens.css", import.meta.url), "utf8");
function block(selector: string): Record<string, string> {
  const start = css.indexOf(`${selector} {`);
  const body = css.slice(start, css.indexOf("}", start));
  return Object.fromEntries([...body.matchAll(/(--q-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
}
const LEGACY = { dark: block(':root[data-theme="dark"]'), light: block(':root[data-theme="light"]') };
const vars = (t: (typeof APP_THEMES)[number]) => ({ ...LEGACY[t.mode], ...tokenVars(t) });
const tint = (hex: string, a: number, under: Rgb) => over(`rgba(${parseHex(hex).join(", ")}, ${a})`, under);

function layers(v: Record<string, string>) {
  const bg = parseHex(v["--q-bg"]), line = parseHex(v["--q-panel"]);
  const twice = (t: string) => over(t, over(t, bg));
  return {
    editor: bg, "current line": line, selection: over(v["--q-sel"], bg), "selection on current line": over(v["--q-sel"], line),
    "diff insert": twice(v["--q-diff-insert"]), "diff remove": twice(v["--q-diff-remove"]),
  };
}
const overlays = (v: Record<string, string>) => {
  const bg = parseHex(v["--q-bg"]), line = parseHex(v["--q-panel"]);
  return { "find match": tint(v["--q-warn"], 0.1, bg), "find match on current line": tint(v["--q-warn"], 0.1, line), "word highlight": tint(v["--q-fg"], 0.09, bg) };
};
const roles = (p: SyntaxPalette) => ({ ...p, temp: roleColor(p, "temp", ""), batch: roleColor(p, "batch", ""), escape: roleColor(p, "escape", "") });

describe.each(APP_THEMES.map((t) => [t.id, t] as const))("%s", (_, theme) => {
  const v = vars(theme);
  const palettes: [string, SyntaxPalette][] = [["default", appSqlPalette(theme)], ...SYNTAX_THEMES.map((s) => [s.id, s[theme.mode]] as [string, SyntaxPalette])];

  it("every palette's every role reads at 4.5:1 on every editor layer", () => {
    const failures: string[] = [];
    for (const [id, p] of palettes)
      for (const [layer, bg] of Object.entries(layers(v)))
        for (const [role, hex] of Object.entries(roles(p))) {
          if (!hex) continue;
          const r = contrast(hex, bg);
          if (r < 4.5) failures.push(`${id}.${role} ${hex} on ${layer}: ${r.toFixed(2)}`);
        }
    expect(failures).toEqual([]);
  });

  it("the Default palette reads on the find match (comments 4.0) and the word highlight", () => {
    const failures: string[] = [];
    for (const [layer, bg] of Object.entries(overlays(v)))
      for (const [role, hex] of Object.entries(roles(appSqlPalette(theme)))) {
        if (!hex) continue;
        const floor = role === "comment" ? 4.0 : 4.5;
        const r = contrast(hex, bg);
        if (r < floor) failures.push(`${role} ${hex} on ${layer}: ${r.toFixed(2)}`);
      }
    expect(failures).toEqual([]);
  });

  it("the Default palette keeps side-by-side roles apart (IS NULL, NULLIF(…NULL), FROM sales.x / FROM cte) and CTE names off identifiers", () => {
    const s = theme.sql;
    const failures: string[] = [];
    for (const [a, b] of [["constant", "operator"], ["constant", "function"], ["cte", "schema"]] as const) {
      if (deltaE2000(s[a], s[b]) < 15) failures.push(`${a}/${b} ΔE ${deltaE2000(s[a], s[b]).toFixed(1)}`);
      for (const k of Object.keys(CVD) as (keyof typeof CVD)[]) {
        const d = deltaE2000(simulate(s[a], k), simulate(s[b], k));
        if (d < 5) failures.push(`${a}/${b} ${k} ΔE ${d.toFixed(1)}`);
      }
    }
    if (deltaE2000(s.cte, s.ident) < 18) failures.push(`cte/ident ΔE ${deltaE2000(s.cte, s.ident).toFixed(1)}`);
    expect(failures).toEqual([]);
  });

  it("error, warning and info squiggles reach 3:1 on the editor layers; warning and error stay apart for color-blind users", () => {
    const mark = v["--q-warn-mark"] ?? WARN_MARK_FALLBACK[theme.mode];
    const info = v["--q-info"] ?? v["--q-accent"];
    const failures: string[] = [];
    for (const [layer, bg] of Object.entries(layers(v)))
      for (const [name, hex] of [["error", v["--q-err"]], ["warning", mark], ["info", info]] as const) {
        if (layer.startsWith("diff")) continue;
        const r = contrast(hex, bg);
        if (r < 3) failures.push(`${name} ${hex} on ${layer}: ${r.toFixed(2)}`);
      }
    if (!theme.legacy)
      for (const k of Object.keys(CVD) as (keyof typeof CVD)[]) {
        const d = deltaE2000(simulate(mark, k), simulate(v["--q-err"], k));
        if (d < 10) failures.push(`warning mark / error ${k} ΔE ${d.toFixed(1)}`);
      }
    expect(failures).toEqual([]);
  });
});

#!/usr/bin/env node
// Shared color checker for the Querithm App Theme plan.
// Usage: node plans/app-themes/check.mjs plans/app-themes/themes.json [--verbose] [--theme <id>]
// themes.json: { "themes": [ Theme, ... ] }  (schema: plans/APP-THEMES.md, "Data file")
// Metrics match the repo's test math (src/editor/__tests__/colorScience.ts): WCAG 2 contrast, CIEDE2000,
// Machado 2009 CVD simulation (severity 1), OKLCH. APCA is SAPC/APCA-W3 0.0.98G-4g.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
// The six existing syntax themes (exported from src/editor/syntaxTheme.ts): a user may pick any of them with any App
// Theme, so their unchanged hexes must still read on every new theme's editor layers.
const LEGACY_SYNTAX = JSON.parse(readFileSync(new URL("./legacy-syntax.json", import.meta.url), "utf8"));

// ---------- color math ----------
const parseHex = (hex) => { const h = hex.trim().replace("#", ""); return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)); };
const toHex = (c) => `#${c.map((v) => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, "0")).join("")}`;
function parseColor(s) {
  s = s.trim();
  if (s.startsWith("#")) { const h = s.slice(1); const a = h.length === 8 ? parseInt(h.slice(6, 8), 16) / 255 : 1; return [...parseHex(s.slice(0, 7)), a]; }
  const m = /rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)(?:[\s,/]+([\d.]+))?\s*\)/.exec(s);
  if (!m) throw new Error(`unsupported color ${s}`);
  return [+m[1], +m[2], +m[3], m[4] === undefined ? 1 : +m[4]];
}
/** color (any) over an opaque rgb */
const over = (color, under) => { const [r, g, b, a] = parseColor(color); return [r, g, b].map((c, i) => c * a + under[i] * (1 - a)); };
const rgb = (c) => (typeof c === "string" ? parseColor(c).slice(0, 3) : c);
const lin = (c) => ((c /= 255) <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const unlin = (c) => 255 * (c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055);
const lum = (c) => { const [r, g, b] = rgb(c).map(lin); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
export const contrast = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
// APCA-W3 0.0.98G-4g
function apcaY(c) { const [r, g, b] = rgb(c).map((v) => (v / 255) ** 2.4); return 0.2126729 * r + 0.7151522 * g + 0.072175 * b; }
export function apca(txt, bg) {
  let t = apcaY(txt), b = apcaY(bg);
  const clamp = (y) => (y > 0.022 ? y : y + (0.022 - y) ** 1.414);
  t = clamp(t); b = clamp(b);
  if (Math.abs(b - t) < 0.0005) return 0;
  let out;
  if (b > t) { const s = (b ** 0.56 - t ** 0.57) * 1.14; out = s < 0.1 ? 0 : s - 0.027; }
  else { const s = (b ** 0.65 - t ** 0.62) * 1.14; out = s > -0.1 ? 0 : s + 0.027; }
  return out * 100;
}
function toLab(c) {
  const [r, g, b] = rgb(c).map(lin);
  const X = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047, Y = 0.2126729 * r + 0.7151522 * g + 0.072175 * b, Z = (0.0193339 * r + 0.119192 * g + 0.9503041 * b) / 1.08883;
  const f = (t) => (t > 216 / 24389 ? Math.cbrt(t) : ((24389 / 27) * t + 16) / 116);
  return [116 * f(Y) - 16, 500 * (f(X) - f(Y)), 200 * (f(Y) - f(Z))];
}
export function dE(h1, h2) {
  const [L1, a1, b1] = toLab(h1), [L2, a2, b2] = toLab(h2); const rad = Math.PI / 180;
  const Cb = (Math.hypot(a1, b1) + Math.hypot(a2, b2)) / 2; const G = 0.5 * (1 - Math.sqrt(Cb ** 7 / (Cb ** 7 + 25 ** 7)));
  const ap1 = (1 + G) * a1, ap2 = (1 + G) * a2; const Cp1 = Math.hypot(ap1, b1), Cp2 = Math.hypot(ap2, b2);
  const hue = (b, a) => (b === 0 && a === 0 ? 0 : (Math.atan2(b, a) / rad + 360) % 360); const hp1 = hue(b1, ap1), hp2 = hue(b2, ap2);
  let dh = 0; if (Cp1 * Cp2 !== 0) dh = hp2 - hp1 > 180 ? hp2 - hp1 - 360 : hp2 - hp1 < -180 ? hp2 - hp1 + 360 : hp2 - hp1;
  const dL = L2 - L1, dC = Cp2 - Cp1, dH = 2 * Math.sqrt(Cp1 * Cp2) * Math.sin((dh / 2) * rad); const Lb = (L1 + L2) / 2, Cpb = (Cp1 + Cp2) / 2;
  let hb = hp1 + hp2; if (Cp1 * Cp2 !== 0) hb = (Math.abs(hp1 - hp2) > 180 ? (hb < 360 ? hb + 360 : hb - 360) : hb) / 2;
  const T = 1 - 0.17 * Math.cos((hb - 30) * rad) + 0.24 * Math.cos(2 * hb * rad) + 0.32 * Math.cos((3 * hb + 6) * rad) - 0.2 * Math.cos((4 * hb - 63) * rad);
  const Rc = 2 * Math.sqrt(Cpb ** 7 / (Cpb ** 7 + 25 ** 7));
  const Sl = 1 + (0.015 * (Lb - 50) ** 2) / Math.sqrt(20 + (Lb - 50) ** 2), Sc = 1 + 0.045 * Cpb, Sh = 1 + 0.015 * Cpb * T;
  const Rt = -Math.sin(2 * 30 * Math.exp(-(((hb - 275) / 25) ** 2)) * rad) * Rc;
  return Math.sqrt((dL / Sl) ** 2 + (dC / Sc) ** 2 + (dH / Sh) ** 2 + Rt * (dC / Sc) * (dH / Sh));
}
const CVD = {
  protan: [[0.152286, 1.052583, -0.204868], [0.114503, 0.786281, 0.099216], [-0.003882, -0.048116, 1.051998]],
  deutan: [[0.367322, 0.860646, -0.227968], [0.280085, 0.672501, 0.047413], [-0.01182, 0.04294, 0.968881]],
  tritan: [[1.255528, -0.076749, -0.178779], [-0.078411, 0.930809, 0.147602], [0.004733, 0.691367, 0.3039]],
};
const sim = (hex, k) => { const c = parseHex(hex).map(lin); return toHex(CVD[k].map((r) => unlin(Math.max(0, Math.min(1, r[0] * c[0] + r[1] * c[1] + r[2] * c[2]))))); };
export function oklch(c) {
  const [r, g, b] = rgb(c).map(lin);
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b), m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b), s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  const A = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s, B = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s;
  return { L: 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s, C: Math.hypot(A, B), h: ((Math.atan2(B, A) * 180) / Math.PI + 360) % 360 };
}

// ---------- reference data ----------
// Hexes of the inspiration themes (from memory of their published palettes). No theme may reuse one exactly,
// and no background may sit within ΔE00 5 of an inspiration theme's main background.
const ORIGINAL_BGS = {
  "GitHub Dark": "#0d1117", "GitHub Dark sidebar": "#010409", "GitHub Dark Dimmed": "#22272e", "GitHub Light": "#ffffff", "GitHub Light panel": "#f6f8fa",
  "One Dark Pro": "#282c34", "One Dark Pro sidebar": "#21252b", "Dracula": "#282a36", "Dracula sidebar": "#21222c", "Dracula deep": "#191a21",
  "Ayu Dark": "#0b0e14", "Ayu Dark (old)": "#0a0e14", "Ayu Mirage": "#1f2430", "Ayu Mirage panel": "#242936", "Ayu Light": "#fcfcfc", "Ayu Light panel": "#f8f9fa",
  "Night Owl": "#011627", "Night Owl sidebar": "#0b2942", "Light Owl": "#fbfbfb",
  "Tokyo Night": "#1a1b26", "Tokyo Night sidebar": "#16161e", "Tokyo Night Storm": "#24283b", "Tokyo Night Day": "#e1e2e7",
  "Solarized Light": "#fdf6e3", "Solarized Dark": "#002b36",
  "Catppuccin Mocha": "#1e1e2e", "Catppuccin Mocha mantle": "#181825", "Catppuccin Mocha crust": "#11111b", "Catppuccin Macchiato": "#24273a", "Catppuccin Frappe": "#303446", "Catppuccin Latte": "#eff1f5", "Catppuccin Latte mantle": "#e6e9ef",
};
const ORIGINAL_HEXES = new Set(`#0d1117 #010409 #161b22 #21262d #30363d #e6edf3 #c9d1d9 #8b949e #58a6ff #1f6feb #ff7b72 #a5d6ff #d2a8ff #79c0ff #ffa657 #7ee787
#ffffff #f6f8fa #1f2328 #24292f #cf222e #0a3069 #8250df #0550ae #6e7781 #953800 #116329 #0969da #d0d7de
#282c34 #21252b #abb2bf #c678dd #98c379 #e5c07b #61afef #e06c75 #d19a66 #56b6c2 #5c6370 #7f848e #2c313a #3e4451 #be5046
#282a36 #21222c #191a21 #44475a #f8f8f2 #6272a4 #8be9fd #50fa7b #ffb86c #ff79c6 #bd93f9 #ff5555 #f1fa8c
#0b0e14 #0a0e14 #0d1017 #bfbdb6 #ff8f40 #e6b450 #aad94c #39bae6 #59c2ff #ffb454 #f07178 #d2a6ff #95e6cb #f29668 #626a73 #acb6bf
#1f2430 #242936 #cccac2 #ffad66 #ffcc66 #d5ff80 #73d0ff #5ccfe6 #ffd173 #f28779 #dfbfff #fcfcfc #f8f9fa #5c6166 #fa8d3e #f2ae49 #86b300 #399ee6 #55b4d4 #a37acc #f07171 #787b80
#011627 #01111d #0b2942 #d6deeb #c792ea #82aaff #addb67 #ecc48d #f78c6c #7fdbca #637777 #ffcb8b #21c7a8 #ef5350 #fbfbfb #403f53
#1a1b26 #16161e #24283b #a9b1d6 #c0caf5 #7aa2f7 #bb9af7 #9ece6a #e0af68 #f7768e #ff9e64 #7dcfff #2ac3de #565f89 #9aa5ce #73daca #b4f9f8 #414868 #e1e2e7
#1e1e2e #181825 #11111b #313244 #45475a #585b70 #cdd6f4 #bac2de #a6adc8 #cba6f7 #f5c2e7 #f38ba8 #eba0ac #fab387 #f9e2af #a6e3a1 #94e2d5 #89dceb #74c7ec #89b4fa #b4befe #f2cdcd #9399b2 #7f849c #6c7086
#fdf6e3 #eee8d5 #002b36 #073642 #586e75 #657b83 #839496 #93a1a1 #b58900 #cb4b16 #dc322f #d33682 #6c71c4 #268bd2 #2aa198 #859900
#eff1f5 #e6e9ef #dce0e8 #4c4f69 #8839ef #d20f39 #fe640b #df8e1d #40a02b #179299 #04a5e5 #1e66f5 #7287fd #ea76cb #24273a #303446`.split(/\s+/).filter(Boolean));

// Querithm's current Dark and Light (stay as the "Querithm Dark" / "Querithm Light" themes); new themes must differ.
const LEGACY_BG = { "Querithm Dark (legacy)": "#0f1117", "Querithm Light (legacy)": "#ffffff" };

// Dark-family and light-family tokens that are NOT redesigned per theme (env tags, object icons, chart slots).
// The checker verifies them on each theme's surfaces so the plan knows where per-theme overrides are needed.
const FAMILY = {
  dark: { envFg: ["#94a3b8", "#34d399", "#a78bfa", "#fbbf24", "#f87171"], obj: ["#38bdf8", "#c084fc", "#fbbf24", "#34d399", "#f472b6", "#2dd4bf", "#9ca3af", "#5eead4"], chart: ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"] },
  light: { envFg: ["#475569", "#065f46", "#6d28d9", "#92400e", "#b91c1c"], obj: ["#0369a1", "#9333ea", "#b45309", "#047857", "#db2777", "#0f766e", "#6b7280", "#0f766e"], chart: ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"] },
};

// ---------- rules ----------
const UI_REQUIRED = ["bg", "panel", "panel2", "raised", "hover", "line", "lineStrong", "fg", "muted", "mutedHl", "faint", "accent", "accentSolid", "accentFg", "info", "ok", "warn", "err", "errSolid", "null", "sel", "active", "rowSel", "overlaySel", "warnSolid", "warnSolidFg", "ident", "suggestMatch", "diffInsert", "diffRemove"];
const SQL_REQUIRED = ["keyword", "function", "type", "string", "number", "constant", "ident", "operator", "comment", "cte", "schema", "parameter", "variable", "bracket1", "bracket2", "bracket3"];
// roles compared for separation (bracket colors and optional ones excluded)
const SQL_ROLES = ["keyword", "function", "type", "string", "number", "constant", "ident", "operator", "comment", "cte", "schema", "parameter", "variable"];
const KEY_PAIRS = [["keyword", "ident"], ["keyword", "function"], ["keyword", "type"], ["string", "ident"], ["variable", "ident"], ["function", "ident"], ["type", "ident"], ["number", "string"], ["comment", "ident"], ["string", "keyword"], ["number", "ident"], ["variable", "string"], ["operator", "ident"], ["variable", "type"],
  ["constant", "keyword"], ["constant", "string"], ["cte", "ident"], ["schema", "ident"], ["parameter", "ident"], ["comment", "string"], ["cte", "function"]];
const isKey = (a, b) => KEY_PAIRS.some(([x, y]) => (x === a && y === b) || (x === b && y === a));
// pairs allowed to share a color on purpose (the theme may set them equal)
const MERGEABLE = new Set(["parameter|variable", "variable|parameter"]);

function checkTheme(t) {
  const F = [], W = [], M = {}; // failures, warnings, metrics
  const u = t.ui ?? {}, s = t.sql ?? {};
  for (const k of UI_REQUIRED) if (!u[k]) F.push(`ui.${k} missing`);
  const hasSql = !!t.sql;
  if (hasSql) for (const k of SQL_REQUIRED) if (!s[k]) F.push(`sql.${k} missing`);
  if (F.length) return { F, W, M };
  const dark = t.mode === "dark";
  const B = (k) => rgb(u[k]);
  const surfaces = { bg: B("bg"), panel: B("panel"), panel2: B("panel2"), raised: B("raised") };
  const surfacesHover = { ...surfaces, hover: B("hover") };
  const need = (cond, msg) => { if (!cond) F.push(msg); };
  const soft = (cond, msg) => { if (!cond) (t.legacy ? W : F).push(t.legacy ? `[legacy, unchanged] ${msg}` : msg); };
  const r2 = (x) => Math.round(x * 100) / 100;

  // --- UI text tokens: 4.5:1 on every surface (and hover)
  for (const k of ["fg", "muted", "accent", "info", "ok", "warn", "err", "ident", "null"])
    for (const [sn, sv] of Object.entries(surfacesHover)) { const c = contrast(u[k], sv); soft(c >= 4.5, `ui.${k} ${u[k]} on ${sn}: ${r2(c)} < 4.5`); }
  // text tokens on their own 15% tint over bg
  for (const k of ["accent", "ok", "warn", "err"]) { const tint = over(`rgba(${parseColor(u[k]).slice(0, 3).join(",")},0.15)`, B("bg")); const c = contrast(u[k], tint); soft(c >= 4.5, `ui.${k} on its 15% tint: ${r2(c)} < 4.5`); }
  soft(contrast(u.mutedHl, B("active")) >= 4.5, `ui.mutedHl on active: ${r2(contrast(u.mutedHl, B("active")))} < 4.5`);
  soft(contrast(u.fg, B("active")) >= 7, `ui.fg on active: ${r2(contrast(u.fg, B("active")))} < 7 (suggest-list selected row)`);
  soft(contrast(u.fg, B("overlaySel")) >= 4.5, `ui.fg on overlaySel < 4.5`);
  for (const k of ["err", "warn", "accent"]) soft(contrast(u[k], B("rowSel")) >= 4.5, `ui.${k} on rowSel: ${r2(contrast(u[k], B("rowSel")))} < 4.5`);
  soft(contrast(u.accentFg, B("accentSolid")) >= 4.5, `accentFg on accentSolid: ${r2(contrast(u.accentFg, B("accentSolid")))} < 4.5`);
  soft(contrast("#ffffff", B("errSolid")) >= 4.5, `white on errSolid: ${r2(contrast("#ffffff", B("errSolid")))} < 4.5`);
  soft(contrast(u.warnSolidFg, B("warnSolid")) >= 4.5, `warnSolidFg on warnSolid: ${r2(contrast(u.warnSolidFg, B("warnSolid")))} < 4.5`);
  for (const s2 of ["raised", "active"]) soft(contrast(u.suggestMatch, B(s2)) >= 4.5, `suggestMatch on ${s2}: ${r2(contrast(u.suggestMatch, B(s2)))} < 4.5`);
  // boundaries 3:1
  for (const [sn, sv] of Object.entries(surfaces)) { const c = contrast(u.lineStrong, sv); soft(c >= 3, `lineStrong on ${sn}: ${r2(c)} < 3`); }
  soft(contrast(u.accent, B("bg")) >= 3 && contrast(u.accent, B("panel")) >= 3, `accent focus ring < 3:1`);
  // structural separations
  soft(contrast(B("active"), B("raised")) >= 1.3, `active vs raised: ${r2(contrast(B("active"), B("raised")))} < 1.3 (selected suggest row must stand out)`);
  soft(contrast(B("active"), B("hover")) >= 1.2, `active vs hover: ${r2(contrast(B("active"), B("hover")))} < 1.2`);
  soft(dE(u.line, u.hover) >= 3, `line vs hover ΔE ${r2(dE(u.line, u.hover))} < 3 (bordered control loses outline on hover)`);
  soft(dE(u.bg, u.panel) >= 1.5, `bg vs panel ΔE ${r2(dE(u.bg, u.panel))} < 1.5 (current-line highlight invisible)`);
  soft(dE(u.bg, u.panel) <= 9, `bg vs panel ΔE ${r2(dE(u.bg, u.panel))} > 9 (current-line highlight too loud)`);
  // faint is decorative: must be below muted
  soft(contrast(u.faint, B("bg")) < contrast(u.muted, B("bg")), `faint must be dimmer than muted`);

  // --- Eye comfort (Eye Doctor rules)
  const bgL = oklch(u.bg).L, fgOnBg = contrast(u.fg, u.bg), fgLc = apca(u.fg, u.bg);
  M.bgOklchL = r2(bgL); M.fgContrast = r2(fgOnBg); M.fgApcaLc = r2(fgLc);
  for (const k of ["bg", "panel", "panel2", "raised", "fg"]) { const h = u[k].toLowerCase(); soft(h !== "#000000" && h !== "#ffffff", `ui.${k} is pure ${h} (halation / glare)`); }
  if (dark) {
    soft(bgL >= 0.16 && bgL <= 0.30, `dark bg OKLCH L ${r2(bgL)} outside 0.16–0.30 (too black → halation, too grey → low contrast)`);
    soft(fgOnBg >= 10 && fgOnBg <= 15.5, `dark fg/bg contrast ${r2(fgOnBg)} outside 10–15.5 (glare above, strain below)`);
    soft(oklch(u.fg).L <= 0.94, `dark fg OKLCH L ${r2(oklch(u.fg).L)} > 0.94 (near-white text halates on dark)`);
  } else {
    soft(bgL >= 0.93 && bgL <= 0.985, `light bg OKLCH L ${r2(bgL)} outside 0.93–0.985 (glare above, murky below)`);
    soft(fgOnBg >= 10 && fgOnBg <= 16, `light fg/bg contrast ${r2(fgOnBg)} outside 10–16`);
  }

  // --- SQL palette
  const layers = { editor: B("bg"), "current line": B("panel"), selection: over(u.sel, B("bg")), "sel+current line": over(u.sel, B("panel")),
    "diff insert": over(u.diffInsert, over(u.diffInsert, B("bg"))), "diff remove": over(u.diffRemove, over(u.diffRemove, B("bg"))) };
  crossCheck();
  if (!hasSql) { W.push("no sql block yet: SQL checks skipped"); return finishOriginality(); }
  const sql = { ...s };
  function crossCheck() {
  // [cross] every existing syntax theme, unchanged, on this theme's editor layers (Standard etc. stay selectable)
  M.crossMin = {};
  for (const lt of LEGACY_SYNTAX) {
    const pal = lt[t.mode]; let worst = Infinity, where = "";
    const roles = { ...pal, temp: pal.temp ?? pal.ident, batch: pal.batch ?? pal.keyword, escape: pal.escape ?? pal.string };
    for (const [role, hex] of Object.entries(roles)) for (const [ln, lv] of Object.entries(layers)) { const c = contrast(hex, lv); if (c < worst) { worst = c; where = `${role} ${hex} on ${ln}`; } }
    M.crossMin[lt.id] = r2(worst);
    if (worst < 4.5) F.push(`[cross] syntax theme "${lt.id}" unreadable here: ${where} ${r2(worst)} < 4.5`);
  }
  }
  // Overlays the plan themes in monaco.ts (review DA2 R1/V4): find match = warn 10 % (+ border), word highlight = fg 9 %.
  const tint = (hex, a) => `rgba(${parseColor(hex).slice(0, 3).join(",")},${a})`;
  const overlays = { "find match": over(tint(u.warn, 0.10), B("bg")), "find match on current line": over(tint(u.warn, 0.10), B("panel")),
    "word highlight": over(tint(u.fg, 0.09), B("bg")) };
  // squiggles drawn in the theme's err / warn / info (review DA2 R2): 3:1 non-text floor on the four main layers
  for (const k of ["err", "warn", "info"]) for (const ln of ["editor", "current line", "selection", "sel+current line"]) {
    const c = contrast(u[k], layers[ln]); soft(c >= 3, `${k} squiggle on ${ln}: ${r2(c)} < 3`);
  }
  M.minTokenContrast = Infinity; M.maxTokenChroma = 0;
  for (const [role, hex] of Object.entries(sql)) {
    for (const [ln, lv] of Object.entries(layers)) { const c = contrast(hex, lv); M.minTokenContrast = Math.min(M.minTokenContrast, c); need(c >= 4.5, `sql.${role} ${hex} on ${ln}: ${r2(c)} < 4.5`); }
    for (const [on, lv] of Object.entries(overlays)) { const c = contrast(hex, lv), floor = role === "comment" ? 4.0 : 4.5; if (c < floor) F.push(`sql.${role} ${hex} on ${on}: ${r2(c)} < ${floor}`); }
    const ch = oklch(hex); if (role !== "comment") M.maxTokenChroma = Math.max(M.maxTokenChroma, ch.C);
    if (dark) {
      if (ch.C > 0.17) F.push(`sql.${role} ${hex} OKLCH C ${r2(ch.C)} > 0.17 (neon vibration on dark)`);
      if (ch.h >= 245 && ch.h <= 290 && ch.C > 0.14 && ch.L > 0.6) W.push(`sql.${role} ${hex} saturated blue-violet (h ${Math.round(ch.h)}, C ${r2(ch.C)}): blue-light spike`);
      const lc = Math.abs(apca(hex, u.bg)); if (role !== "comment" && lc > 92) W.push(`sql.${role} APCA Lc ${r2(lc)} > 92 (glare)`);
    }
  }
  M.minTokenContrast = r2(M.minTokenContrast); M.maxTokenChroma = r2(M.maxTokenChroma);
  // comments: quiet but readable
  const cC = contrast(sql.comment, u.bg), iC = contrast(sql.ident, u.bg);
  M.commentContrast = r2(cC); M.commentApcaLc = r2(Math.abs(apca(sql.comment, u.bg)));
  need(cC <= iC - 1.5, `comment ${r2(cC)} not quieter than ident ${r2(iC)} by ≥1.5 (comments too loud)`);
  if (oklch(sql.comment).C > 0.06) W.push(`comment chroma ${r2(oklch(sql.comment).C)} > 0.06 (a colorful comment can read as live code)`);
  // blue-light share on dark: fraction of roles in blue band with chroma
  if (dark) { const blue = SQL_ROLES.filter((r) => { const o = oklch(sql[r]); return o.h >= 235 && o.h <= 295 && o.C >= 0.08; }); M.blueRoles = blue.length; if (blue.length > 4) W.push(`${blue.length} of ${SQL_ROLES.length} roles are saturated blue/violet (blue-light load)`); }
  // separation
  const vision = { normal: (h) => h, protan: (h) => sim(h, "protan"), deutan: (h) => sim(h, "deutan"), tritan: (h) => sim(h, "tritan") };
  for (const [vn, see] of Object.entries(vision))
    for (let i = 0; i < SQL_ROLES.length; i++) for (let j = i + 1; j < SQL_ROLES.length; j++) {
      const a = SQL_ROLES[i], b = SQL_ROLES[j];
      if (sql[a].toLowerCase() === sql[b].toLowerCase()) { if (vn === "normal" && !MERGEABLE.has(`${a}|${b}`)) F.push(`sql.${a} and sql.${b} share ${sql[a]}`); continue; }
      const d = dE(see(sql[a]), see(sql[b]));
      if (vn === "normal") { if (isKey(a, b) && d < 15) F.push(`normal key pair ${a}/${b}: ΔE ${r2(d)} < 15`); else if (d < 12) F.push(`normal ${a}/${b}: ΔE ${r2(d)} < 12`); }
      else {
        // a pair of two Standard hexes on a legacy theme is Standard's own (frozen, and the repo promises Standard nothing here)
        const std = LEGACY_SYNTAX[0][t.mode];
        const frozen = t.legacy && std[a] && std[b] && std[a].toLowerCase() === sql[a].toLowerCase() && std[b].toLowerCase() === sql[b].toLowerCase();
        const push = (m) => (frozen ? W.push(`[Standard's own pair, pre-existing] ${m}`) : F.push(m));
        if (isKey(a, b) && d < 8) push(`${vn} key pair ${a}/${b}: ΔE ${r2(d)} < 8`);
        if ((a === "keyword" || a === "comment" || b === "keyword" || b === "comment") && d < 8 && !isKey(a, b)) push(`${vn} anchor ${a}/${b}: ΔE ${r2(d)} < 8`);
      }
    }
  // co-located pairs (review DA2 P1): they sit side by side in real SQL
  for (const [a, b] of [["constant", "operator"], ["constant", "function"], ["cte", "schema"]]) {
    const d = dE(sql[a], sql[b]); if (d < 15) F.push(`co-located ${a}/${b}: ΔE ${r2(d)} < 15`);
    for (const k of ["protan", "deutan", "tritan"]) { const dd = dE(sim(sql[a], k), sim(sql[b], k)); if (dd < 5) F.push(`co-located ${a}/${b} ${k}: ΔE ${r2(dd)} < 5`); }
  }
  // CTE names are structure (review DA2 P2)
  { const d = dE(sql.cte, sql.ident); if (d < 18) F.push(`cte/ident ΔE ${r2(d)} < 18 (CTE names read as plain identifiers)`); }
  // comments must not blur on dark (review Eye Doctor): APCA |Lc| ≥ 48 for the italic comment
  if (dark) { const lc = Math.abs(apca(sql.comment, u.bg)); const frozen = t.legacy && sql.comment.toLowerCase() === LEGACY_SYNTAX[0][t.mode].comment.toLowerCase();
    if (lc < 48) (frozen ? W : F).push(`${frozen ? "[Standard's own comment, pre-existing] " : ""}comment APCA Lc ${r2(lc)} < 48 on bg (small italic light-on-dark blurs first)`); }
  // tokens vs error squiggle / production red
  for (const r of SQL_ROLES) { const d = dE(sql[r], u.err); if (d < 10) F.push(`sql.${r} within ΔE ${r2(d)} of ui.err (reads as an error squiggle)`); }
  // brackets distinct from each other
  need(dE(sql.bracket1, sql.bracket2) >= 12 && dE(sql.bracket2, sql.bracket3) >= 12 && dE(sql.bracket1, sql.bracket3) >= 12, `bracket colors not ≥12 apart`);

  return finishOriginality();

  function finishOriginality() {
  // --- family tokens on this theme's surfaces (env text 4.5, icons 3, chart 3 on bg)
  const fam = FAMILY[t.mode];
  const envSurfaces = { ...surfacesHover, active: B("active") };
  for (const c of fam.envFg) for (const [sn, sv] of Object.entries(envSurfaces)) if (contrast(c, sv) < 4.5) W.push(`family env text ${c} on ${sn}: ${r2(contrast(c, sv))} < 4.5 → needs a per-theme override`);
  for (const c of fam.obj) for (const [sn, sv] of Object.entries(surfaces)) if (contrast(c, sv) < 3) W.push(`family object icon ${c} on ${sn}: ${r2(contrast(c, sv))} < 3 → per-theme override`);
  for (const c of fam.chart) if (contrast(c, B("bg")) < 3) W.push(`family chart slot ${c} on bg: ${r2(contrast(c, B("bg")))} < 3`);

  // --- originality
  const all = [...Object.values(u), ...Object.values(s)].filter((v) => v.startsWith("#")).map((v) => v.toLowerCase().slice(0, 7));
  const uiHexes = new Set(Object.values(u).filter((v) => v.startsWith("#")).map((v) => v.toLowerCase().slice(0, 7)));
  for (const h of new Set(all)) if (ORIGINAL_HEXES.has(h)) { if (t.legacy && uiHexes.has(h)) W.push(`[legacy, unchanged] ui hex ${h} also in an inspiration theme`); else F.push(`exact hex ${h} copied from an inspiration theme`); }
  for (const [name, h] of Object.entries({ ...ORIGINAL_BGS, ...LEGACY_BG })) { if (/sidebar|panel|mantle|crust|deep|old/.test(name)) continue; const d = dE(u.bg, h); if (d < 5) (t.legacy ? W : F).push(`${t.legacy ? "[legacy, unchanged] " : ""}bg ${u.bg} within ΔE ${r2(d)} of ${name} ${h} (needs ≥ 5)`); }
  M.nearestOriginal = Object.entries(ORIGINAL_BGS).map(([n, h]) => [n, dE(u.bg, h)]).sort((a, b) => a[1] - b[1]).slice(0, 2).map(([n, d]) => `${n} ${r2(d)}`).join(", ");
  return { F, W, M };
  }
}

function distinctness(themes) {
  const out = [];
  for (let i = 0; i < themes.length; i++) for (let j = i + 1; j < themes.length; j++) {
    const a = themes[i], b = themes[j]; if (a.mode !== b.mode) continue;
    const both = a.sql && b.sql;
    const bgD = dE(a.ui.bg, b.ui.bg), accD = dE(a.ui.accent, b.ui.accent), kwD = both ? dE(a.sql.keyword, b.sql.keyword) : NaN;
    const ha = oklch(a.ui.bg), hb = oklch(b.ui.bg);
    const sig = (t) => SQL_ROLES.map((r) => t.sql[r]);
    const palD = both ? sig(a).reduce((acc, h, k) => acc + dE(h, sig(b)[k]), 0) / SQL_ROLES.length : NaN;
    const r2 = (x) => Math.round(x * 10) / 10;
    const row = `${a.id} vs ${b.id}: bg ΔE ${r2(bgD)} (L ${r2(ha.L * 100)}/${r2(hb.L * 100)}, h ${Math.round(ha.h)}/${Math.round(hb.h)}), accent ΔE ${r2(accD)}, keyword ΔE ${r2(kwD)}, mean token ΔE ${r2(palD)}`;
    const fails = [];
    if (bgD < 6) fails.push("backgrounds look the same (ΔE < 6)");
    // same undertone family: both tinted, hue within 35°, and lightness within 0.05 → reads as a variant of the other
    const hd = Math.min(Math.abs(ha.h - hb.h), 360 - Math.abs(ha.h - hb.h));
    if (ha.C >= 0.012 && hb.C >= 0.012 && hd < 35 && Math.abs(ha.L - hb.L) < 0.05) fails.push(`same undertone (hue Δ ${Math.round(hd)}°, L Δ ${r2(Math.abs(ha.L - hb.L) * 100)})`);
    if (accD < 15) fails.push("accents too close (ΔE < 15)");
    if (both && kwD < 15) fails.push("keyword colors too close (ΔE < 15)");
    if (both && palD < 15) fails.push("SQL palettes too similar (mean ΔE < 15)");
    out.push({ row, fails });
  }
  return out;
}

// ---------- main ----------
const args = process.argv.slice(2);
// The file is the one argument that is neither a flag nor the value after --theme (either order works).
const file = args.find((a, i) => !a.startsWith("--") && args[i - 1] !== "--theme");
const verbose = args.includes("--verbose");
const only = args.includes("--theme") ? args[args.indexOf("--theme") + 1] : null;
const data = JSON.parse(readFileSync(file, "utf8"));
const themes = (data.themes ?? data).filter((t) => !only || t.id === only);
// The two legacy themes always join the distinctness check, so a new theme can't be a variant of them.
const LEGACY = [{ id: "querithm-dark (legacy)", mode: "dark", legacy: true, ui: { bg: "#0f1117", panel: "#161922", accent: "#60a5fa" }, sql: null },
  { id: "querithm-light (legacy)", mode: "light", legacy: true, ui: { bg: "#ffffff", panel: "#f6f8fa", accent: "#2252cd" }, sql: null }];
let total = 0;
for (const t of themes) {
  const { F, W, M } = checkTheme(t);
  total += F.length;
  console.log(`\n=== ${t.id} (${t.mode}) — ${F.length} fail, ${W.length} warn ===`);
  console.log(`metrics: ${JSON.stringify(M)}`);
  for (const f of F) console.log(`  FAIL ${f}`);
  for (const w of verbose ? W : W.slice(0, 8)) console.log(`  warn ${w}`);
  if (!verbose && W.length > 8) console.log(`  … ${W.length - 8} more warnings (--verbose)`);
}
if (!only) {
  console.log(`\n=== distinctness (same mode) ===`);
  const pool = themes.some((t) => t.legacy) ? themes : [...themes, ...LEGACY];
  for (const d of distinctness(pool)) { console.log(`${d.fails.length ? "FAIL" : " ok "} ${d.row}${d.fails.length ? " — " + d.fails.join("; ") : ""}`); total += d.fails.length; }
}
console.log(`\nTOTAL FAILURES: ${total}`);
process.exitCode = total ? 1 : 0;

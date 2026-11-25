#!/usr/bin/env node
// State checker for Querithm App Themes: buttons, controls and status/feedback colors per theme.
// Usage: node plans/app-themes/check-states.mjs plans/app-themes/themes.json [--verbose] [--theme id] [--table]
// Reads each theme's `ui` (plans/app-themes/themes.json) and its optional `states` block (the new per-theme component
// tokens). Resolves every component state the way tokens.css / the components draw it today unless `states` overrides
// it, then checks the rules from src/theme/__tests__/contrast.test.ts plus the state rules of this design round.
import { readFileSync } from "node:fs";

// ---------- color math (same as check.mjs) ----------
const parseHex = (h) => [0, 2, 4].map((i) => parseInt(h.replace("#", "").slice(i, i + 2), 16));
const toHex = (c) => `#${c.map((v) => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, "0")).join("")}`;
function parse(s) {
  s = s.trim();
  if (s.startsWith("#")) { const h = s.slice(1); return [...parseHex(s.slice(0, 7)), h.length === 8 ? parseInt(h.slice(6, 8), 16) / 255 : 1]; }
  const m = /rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)(?:[\s,/]+([\d.]+))?\s*\)/.exec(s);
  if (!m) throw new Error(`unsupported color ${s}`);
  return [+m[1], +m[2], +m[3], m[4] === undefined ? 1 : +m[4]];
}
const over = (c, under) => { const [r, g, b, a] = parse(c); const u = typeof under === "string" ? parse(under).slice(0, 3) : under; return toHex([r, g, b].map((x, i) => x * a + u[i] * (1 - a))); };
const tint = (hex, p, under) => over(`rgba(${parse(hex).slice(0, 3).join(",")},${p})`, under);
const mix = (a, b, p) => { const x = parse(a), y = parse(b); return toHex([0, 1, 2].map((i) => x[i] * p + y[i] * (1 - p))); }; // color-mix in srgb, p of a
const lin = (c) => ((c /= 255) <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const unlin = (c) => 255 * (c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055);
const lum = (h) => { const [r, g, b] = parse(h).slice(0, 3).map(lin); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
const contrast = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
function toLab(h) { const [r, g, b] = parse(h).slice(0, 3).map(lin); const X = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047, Y = 0.2126729 * r + 0.7151522 * g + 0.072175 * b, Z = (0.0193339 * r + 0.119192 * g + 0.9503041 * b) / 1.08883; const f = (t) => (t > 216 / 24389 ? Math.cbrt(t) : ((24389 / 27) * t + 16) / 116); return [116 * f(Y) - 16, 500 * (f(X) - f(Y)), 200 * (f(Y) - f(Z))]; }
function dE(h1, h2) {
  const [L1, a1, b1] = toLab(h1), [L2, a2, b2] = toLab(h2); const rad = Math.PI / 180;
  const Cb = (Math.hypot(a1, b1) + Math.hypot(a2, b2)) / 2; const G = 0.5 * (1 - Math.sqrt(Cb ** 7 / (Cb ** 7 + 25 ** 7)));
  const ap1 = (1 + G) * a1, ap2 = (1 + G) * a2; const Cp1 = Math.hypot(ap1, b1), Cp2 = Math.hypot(ap2, b2);
  const hue = (b, a) => (b === 0 && a === 0 ? 0 : (Math.atan2(b, a) / rad + 360) % 360); const hp1 = hue(b1, ap1), hp2 = hue(b2, ap2);
  let dh = 0; if (Cp1 * Cp2 !== 0) dh = hp2 - hp1 > 180 ? hp2 - hp1 - 360 : hp2 - hp1 < -180 ? hp2 - hp1 + 360 : hp2 - hp1;
  const dL = L2 - L1, dC = Cp2 - Cp1, dH = 2 * Math.sqrt(Cp1 * Cp2) * Math.sin((dh / 2) * rad); const Lb = (L1 + L2) / 2, Cpb = (Cp1 + Cp2) / 2;
  let hb = hp1 + hp2; if (Cp1 * Cp2 !== 0) hb = (Math.abs(hp1 - hp2) > 180 ? (hb < 360 ? hb + 360 : hb - 360) : hb) / 2;
  const T = 1 - 0.17 * Math.cos((hb - 30) * rad) + 0.24 * Math.cos(2 * hb * rad) + 0.32 * Math.cos((3 * hb + 6) * rad) - 0.2 * Math.cos((4 * hb - 63) * rad);
  const Rc = 2 * Math.sqrt(Cpb ** 7 / (Cpb ** 7 + 25 ** 7)); const Sl = 1 + (0.015 * (Lb - 50) ** 2) / Math.sqrt(20 + (Lb - 50) ** 2), Sc = 1 + 0.045 * Cpb, Sh = 1 + 0.015 * Cpb * T;
  const Rt = -Math.sin(2 * 30 * Math.exp(-(((hb - 275) / 25) ** 2)) * rad) * Rc;
  return Math.sqrt((dL / Sl) ** 2 + (dC / Sc) ** 2 + (dH / Sh) ** 2 + Rt * (dC / Sc) * (dH / Sh));
}
const CVD = { protan: [[0.152286, 1.052583, -0.204868], [0.114503, 0.786281, 0.099216], [-0.003882, -0.048116, 1.051998]], deutan: [[0.367322, 0.860646, -0.227968], [0.280085, 0.672501, 0.047413], [-0.01182, 0.04294, 0.968881]], tritan: [[1.255528, -0.076749, -0.178779], [-0.078411, 0.930809, 0.147602], [0.004733, 0.691367, 0.3039]] };
const sim = (h, k) => { const c = parse(h).slice(0, 3).map(lin); return toHex(CVD[k].map((r) => unlin(Math.max(0, Math.min(1, r[0] * c[0] + r[1] * c[1] + r[2] * c[2]))))); };

// ---------- resolution: what each component draws, today's formulas unless `states` overrides ----------
const LEGACY_ENV = {
  dark: { local: "#94a3b8", dev: "#34d399", test: "#a78bfa", staging: "#fbbf24", prod: "#f87171" },
  light: { local: "#475569", dev: "#065f46", test: "#6d28d9", staging: "#92400e", prod: "#b91c1c" },
};
const LEGACY_PROD_FILL = { dark: "#dc2626", light: "#b91c1c" };
export function resolve(t) {
  const u = t.ui, s = t.states ?? {};
  const surf = { bg: u.bg, panel: u.panel, panel2: u.panel2, raised: u.raised };
  const r = {
    // .q-btn (default): raised fill, line border, fg label; hover = --q-hover; pressed = states.pressed ?? hover
    default: { rest: u.raised, hover: s.btnHover ?? u.hover, pressed: s.btnPressed ?? s.btnHover ?? u.hover, label: u.fg, border: u.line, borderHover: s.btnBorderHover ?? u.line },
    // .q-btn-primary: accentSolid + accentFg; hover today = color-mix(accentSolid 85%, black)
    primary: { rest: u.accentSolid, hover: s.accentSolidHover ?? mix(u.accentSolid, "#000000", 0.85), pressed: s.accentSolidPressed ?? s.accentSolidHover ?? mix(u.accentSolid, "#000000", 0.85), label: u.accentFg },
    // .q-btn-danger: errSolid + white; hover today = mix(errSolid 85%, black)
    danger: { rest: u.errSolid, hover: s.errSolidHover ?? mix(u.errSolid, "#000000", 0.85), pressed: s.errSolidPressed ?? s.errSolidHover ?? mix(u.errSolid, "#000000", 0.85), label: s.errSolidFg ?? "#ffffff" },
    // .q-btn-run / .q-btn-stop: ok / err tint 10 % (hover 15 %) over the surface, full-hue border and label
    run: { tintRest: s.runTint ?? 0.10, tintHover: s.runTintHover ?? 0.15, hue: u.ok },
    stop: { tintRest: s.stopTint ?? 0.10, tintHover: s.stopTintHover ?? 0.15, hue: u.err },
    // .q-btn-ghost / .q-tb: transparent; hover = --q-hover
    ghost: { hover: s.ghostHover ?? u.hover, label: u.fg },
    focusRing: s.focusRing ?? u.accent,
    disabledOpacity: s.disabledOpacity ?? 0.45,
    input: { border: u.lineStrong, focus: s.inputFocus ?? u.accent, error: s.inputError ?? u.err, bg: u.bg, placeholder: u.muted },
    switchOn: { track: s.switchOnTrack ?? u.fg, knob: s.switchOnKnob ?? u.panel },
    toast: { alpha: s.toastAlpha ?? 0.15, border: s.toastBorderAlpha ?? 0.5 },
    banner: { alpha: s.bannerAlpha ?? 0.15 },
    rowTint: { err: s.errRowAlpha ?? 0.10, warn: s.warnRowAlpha ?? 0.15 },
    env: { ...LEGACY_ENV[t.mode], ...(s.env ?? {}) },
    prodFill: s.envProdFill ?? LEGACY_PROD_FILL[t.mode],
    surf,
  };
  return r;
}

function check(t) {
  const F = [], W = [], u = t.ui, R = resolve(t), r2 = (x) => Math.round(x * 100) / 100;
  const legacy = !!t.legacy;
  const need = (ok, msg) => { if (!ok) (legacy && !t.states ? W : F).push(legacy && !t.states ? `[today] ${msg}` : msg); };
  const SURF = Object.entries(R.surf);
  // --- solid buttons: label ≥ 4.5 at rest, hover, pressed; hover visibly different; fill edge ≥ 3:1 on bg/panel (soft)
  for (const v of ["primary", "danger"]) {
    const b = R[v];
    for (const st of ["rest", "hover", "pressed"]) need(contrast(b.label, b[st]) >= 4.5, `${v} label on ${st} ${b[st]}: ${r2(contrast(b.label, b[st]))} < 4.5`);
    need(dE(b.rest, b.hover) >= 4, `${v} hover ${b.hover} too close to rest ${b.rest} (ΔE ${r2(dE(b.rest, b.hover))} < 4)`);
    if (b.pressed !== b.hover) need(dE(b.hover, b.pressed) >= 3, `${v} pressed too close to hover (ΔE ${r2(dE(b.hover, b.pressed))} < 3)`);
    for (const [sn, sv] of [["bg", u.bg], ["panel", u.panel], ["raised", u.raised]]) { const c = contrast(b.rest, sv); if (c < 3) W.push(`${v} fill on ${sn}: ${r2(c)} < 3 (control edge; WCAG 1.4.11 asks 3:1 when the fill is the only edge)`); }
  }
  // --- default + ghost: label on rest/hover/pressed over every surface; hover visible
  const d = R.default;
  for (const st of ["rest", "hover", "pressed"]) need(contrast(d.label, d[st]) >= 4.5, `default button label on ${st}: ${r2(contrast(d.label, d[st]))} < 4.5`);
  need(dE(d.rest, d.hover) >= 3, `default button hover ${d.hover} too close to rest ${d.rest} (ΔE ${r2(dE(d.rest, d.hover))} < 3)`);
  need(dE(d.hover, d.borderHover) >= 3, `default button border ${d.borderHover} disappears on hover (ΔE ${r2(dE(d.hover, d.borderHover))} < 3)`);
  for (const [sn, sv] of SURF) { need(dE(R.ghost.hover, sv) >= 3, `ghost/toolbar hover ${R.ghost.hover} invisible on ${sn} (ΔE ${r2(dE(R.ghost.hover, sv))} < 3)`); need(contrast(R.ghost.label, R.ghost.hover) >= 4.5, `ghost label on hover < 4.5`); }
  // --- tinted run/stop: label ≥ 4.5 on tint over every surface, rest and hover; border ≥ 3; hover visible
  for (const v of ["run", "stop"]) {
    const b = R[v];
    for (const [sn, sv] of SURF) for (const [st, a] of [["rest", b.tintRest], ["hover", b.tintHover]]) {
      const fill = tint(b.hue, a, sv); const c = contrast(b.hue, fill); need(c >= 4.5, `${v} label on ${st} tint over ${sn}: ${r2(c)} < 4.5`);
    }
    for (const [sn, sv] of SURF) need(contrast(b.hue, sv) >= 3, `${v} border on ${sn} < 3`);
    need(dE(tint(b.hue, b.tintRest, u.panel), tint(b.hue, b.tintHover, u.panel)) >= 2, `${v} hover tint not visible (ΔE < 2)`);
  }
  // --- focus ring ≥ 3:1 on every surface and against the primary fill (ring sits outside with offset, soft)
  for (const [sn, sv] of SURF) need(contrast(R.focusRing, sv) >= 3, `focus ring on ${sn}: ${r2(contrast(R.focusRing, sv))} < 3`);
  // --- inputs
  need(contrast(R.input.focus, u.bg) >= 3, `input focus border < 3 on bg`);
  need(contrast(R.input.error, u.bg) >= 3, `input error border < 3 on bg`);
  need(dE(R.input.focus, R.input.error) >= 15, `input focus and error borders look alike (ΔE ${r2(dE(R.input.focus, R.input.error))})`);
  // --- switch
  for (const [sn, sv] of SURF) need(contrast(R.switchOn.track, sv) >= 3, `switch on-track on ${sn} < 3`);
  need(contrast(R.switchOn.knob, R.switchOn.track) >= 3, `switch knob on track < 3`);
  // --- status family: text on surfaces + hover, on toast/banner/row tints, CVD separation
  const STATUS = { info: u.info, ok: u.ok, warn: u.warn, err: u.err };
  for (const [k, hex] of Object.entries(STATUS)) {
    for (const [sn, sv] of [...SURF, ["hover", u.hover]]) need(contrast(hex, sv) >= 4.5, `${k} text on ${sn}: ${r2(contrast(hex, sv))} < 4.5`);
    if (k !== "info") for (const [sn, sv] of SURF) {
      const toast = tint(hex, R.toast.alpha, sv); need(contrast(hex, toast) >= 4.5, `${k} toast text on ${R.toast.alpha * 100}% tint over ${sn}: ${r2(contrast(hex, toast))} < 4.5`);
      const banner = tint(hex, R.banner.alpha, sv); need(contrast(hex, banner) >= 4.5, `${k} banner text on tint over ${sn}: ${r2(contrast(hex, banner))} < 4.5`);
      const tb = tint(hex, R.toast.border, sv); if (contrast(tb, toast) < 1.5) W.push(`${k} toast border barely visible on ${sn}`);
    }
  }
  // info toast today: raised + fg
  need(contrast(u.fg, u.raised) >= 4.5, `info toast text < 4.5`);
  // message rows: fg and muted-hl on err/warn row tints over bg
  for (const [k, a] of [["err", R.rowTint.err], ["warn", R.rowTint.warn]]) { const row = tint(STATUS[k], a, u.bg); need(contrast(u.fg, row) >= 4.5, `fg on ${k} row tint < 4.5`); need(contrast(u.mutedHl, row) >= 4.5, `mutedHl on ${k} row tint: ${r2(contrast(u.mutedHl, row))} < 4.5`); need(contrast(STATUS[k], row) >= 4.5, `${k} text on its row tint < 4.5`); }
  // CVD: error vs warning must not merge; success vs error should not merge (icons + words still required)
  const pairs = [["warn", "err", 10], ["ok", "err", 8], ["ok", "warn", 8], ["info", "ok", 8], ["info", "warn", 8], ["info", "err", 8]];
  for (const [a, b, floor] of pairs) {
    const dn = dE(STATUS[a], STATUS[b]); need(dn >= 15, `${a}/${b} normal ΔE ${r2(dn)} < 15`);
    for (const k of ["protan", "deutan", "tritan"]) { const dd = dE(sim(STATUS[a], k), sim(STATUS[b], k)); need(dd >= floor, `${a}/${b} ${k} ΔE ${r2(dd)} < ${floor}`); }
  }
  // accent must not read as a status (accent ≠ ok / warn / err)
  for (const k of ["ok", "warn", "err"]) need(dE(u.accent, STATUS[k]) >= 15, `accent ${u.accent} reads like ${k} ${STATUS[k]} (ΔE ${r2(dE(u.accent, STATUS[k]))} < 15)`);
  // solid pills: PROD (prodFill + white), TXN OPEN (warnSolid + warnSolidFg)
  need(contrast("#ffffff", R.prodFill) >= 4.5, `PROD pill white on ${R.prodFill}: ${r2(contrast("#ffffff", R.prodFill))} < 4.5`);
  need(contrast(u.warnSolidFg, u.warnSolid) >= 4.5, `TXN pill text < 4.5`);
  // env tag text (Connect dialog, contrast.test.ts:168-183): ≥ 4.5 on panel, raised (hovered row), rowSel (active row), panel2
  const CONNECT = [["panel", u.panel], ["raised", u.raised], ["rowSel", u.rowSel], ["panel2", u.panel2]];
  for (const [env, hex] of Object.entries(R.env)) for (const [sn, sv] of CONNECT) {
    const c = contrast(hex, sv); need(c >= 4.5, `env ${env} text ${hex} on ${sn}: ${r2(c)} < 4.5`);
  }
  // Production stripe (--q-env-prod mark) ≥ 3:1 on every Connect row surface
  const stripe = (t.states ?? {}).envProdMark ?? (t.mode === "dark" ? "#ef4444" : "#dc2626");
  for (const [sn, sv] of CONNECT) need(contrast(stripe, sv) >= 3, `Production stripe ${stripe} on ${sn}: ${r2(contrast(stripe, sv))} < 3`);
  // env prod must stay red-hued and apart from the other env colors
  for (const env of ["local", "dev", "test", "staging"]) need(dE(R.env.prod, R.env[env]) >= 15, `env prod vs ${env} ΔE < 15`);
  return { F: F.filter(Boolean), W };
}

function table(t) {
  const R = resolve(t), u = t.ui, r2 = (x) => Math.round(x * 10) / 10;
  const row = (name, rest, hover, pressed, label) => `  ${name.padEnd(9)} rest ${rest}  hover ${hover}  pressed ${pressed}  label ${label}  (${r2(contrast(label, rest))}/${r2(contrast(label, hover))}:1)`;
  return [row("default", R.default.rest, R.default.hover, R.default.pressed, R.default.label), row("primary", R.primary.rest, R.primary.hover, R.primary.pressed, R.primary.label),
    row("danger", R.danger.rest, R.danger.hover, R.danger.pressed, R.danger.label),
    `  run       tint ok ${u.ok} ${R.run.tintRest * 100}%→${R.run.tintHover * 100}%   stop tint err ${u.err} ${R.stop.tintRest * 100}%→${R.stop.tintHover * 100}%   ghost hover ${R.ghost.hover}   focus ${R.focusRing}`,
    `  status    info ${u.info}  ok ${u.ok}  warn ${u.warn}  err ${u.err}   PROD ${R.prodFill}  env ${JSON.stringify(R.env)}`].join("\n");
}

const args = process.argv.slice(2);
// The file is the one argument that is neither a flag nor the value after --theme (either order works).
const file = args.find((a, i) => !a.startsWith("--") && args[i - 1] !== "--theme");
const only = args.includes("--theme") ? args[args.indexOf("--theme") + 1] : null;
const data = JSON.parse(readFileSync(file, "utf8"));
let total = 0;
for (const t of (data.themes ?? data).filter((x) => !only || x.id === only)) {
  const { F, W } = check(t); total += F.length;
  console.log(`\n=== ${t.id} (${t.mode}) — ${F.length} fail, ${W.length} warn ===`);
  if (args.includes("--table")) console.log(table(t));
  for (const f of F) console.log(`  FAIL ${f}`);
  for (const w of args.includes("--verbose") ? W : W.slice(0, 6)) console.log(`  warn ${w}`);
}
console.log(`\nTOTAL FAILURES: ${total}`);
process.exitCode = total ? 1 : 0;

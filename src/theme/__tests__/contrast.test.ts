import { readFileSync, readdirSync } from "node:fs";
import { join, relative } from "node:path";
import ts from "typescript";
import { describe, expect, it } from "vitest";
import { contrastRatio } from "../../app/tabStyle";
import { SYNTAX_THEMES } from "../../editor/syntaxTheme";

// WCAG AA for body text; every button and chip label in the app is below the 18.66 px bold "large text" size.
const MIN = 4.5;
const css = readFileSync(join(process.cwd(), "src/theme/tokens.css"), "utf8");

type Rgba = [number, number, number, number]; // sRGB 0–255, alpha 0–1
type Vars = Record<string, string>;

function block(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  return css.slice(start, css.indexOf("}", start));
}
const varsOf = (selector: string): Vars => Object.fromEntries([...block(selector).matchAll(/(--q-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
const themes = { dark: varsOf(':root[data-theme="dark"]'), light: varsOf(':root[data-theme="light"]') };
/** Tailwind color name → token, from the `@theme` block (`bg-accent` → `--q-accent`). */
const twColors: Record<string, string> = Object.fromEntries([...block("@theme").matchAll(/--color-([\w-]+):\s*var\((--q-[\w-]+)\)/g)].map((m) => [m[1], m[2]]));
/** The surfaces a translucent fill or a transparent button can sit on. */
const SURFACES = ["--q-bg", "--q-panel", "--q-panel-2", "--q-raised"];

const NAMED: Record<string, Rgba> = { white: [255, 255, 255, 1], black: [0, 0, 0, 1], transparent: [0, 0, 0, 0] };
/** Variables a component sets to the surface it sits on (`.q-tab`: --q-panel, --q-hover or --q-row-sel). A fill drawn
 *  in one is that surface, so it is read as transparent: on every surface. */
const SURFACE_VARS = new Set(["--q-row-bg"]);

/** color-mix with premultiplied alpha, as CSS Color 5 interpolates (mixing with `transparent` only lowers alpha). */
function mix(a: Rgba, b: Rgba, p: number): Rgba {
  const alpha = a[3] * p + b[3] * (1 - p);
  if (alpha === 0) return [0, 0, 0, 0];
  const ch = [0, 1, 2].map((i) => (a[i] * a[3] * p + b[i] * b[3] * (1 - p)) / alpha);
  return [ch[0], ch[1], ch[2], alpha];
}
const over = (top: Rgba, bottom: Rgba): Rgba => [0, 1, 2].map((i) => top[i] * top[3] + bottom[i] * (1 - top[3])).concat(1) as Rgba;
const withAlpha = (c: Rgba, a: number): Rgba => [c[0], c[1], c[2], c[3] * a];

function color(value: string, vars: Vars): Rgba {
  const v = value.trim();
  if (v in NAMED) return NAMED[v];
  const ref = /^var\((--[\w-]+)\)$/.exec(v);
  if (ref && !(ref[1] in vars) && SURFACE_VARS.has(ref[1])) return NAMED.transparent;
  if (ref) return color(vars[ref[1]] ?? `missing ${ref[1]}`, vars);
  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(v);
  if (hex) {
    const h = hex[1].length === 3 ? [...hex[1]].map((x) => x + x).join("") : hex[1];
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16), 1];
  }
  const rgba = /^rgba?\(\s*(\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\s*\)$/.exec(v);
  if (rgba) return [+rgba[1], +rgba[2], +rgba[3], rgba[4] === undefined ? 1 : +rgba[4]];
  const cm = /^color-mix\(in srgb,\s*(.+?)\s+([\d.]+)%,\s*(.+)\)$/.exec(v);
  if (cm) return mix(color(cm[1], vars), color(cm[3], vars), +cm[2] / 100);
  throw new Error(`unsupported color: ${value}`);
}
const ratio = (fg: Rgba, bg: Rgba) => {
  const solid = over(fg, bg);
  return contrastRatio([solid[0], solid[1], solid[2]], [bg[0], bg[1], bg[2]]);
};
/** Worst contrast of text over a fill: an opaque fill is the background; a translucent one is composited on each surface. */
function worst(fg: Rgba, fill: Rgba | null, vars: Vars): { value: number; on: string } {
  const bgs = fill && fill[3] === 1 ? [["fill", fill] as const] : SURFACES.map((s) => [s, fill ? over(fill, color(`var(${s})`, vars)) : color(`var(${s})`, vars)] as const);
  return bgs.map(([on, bg]) => ({ value: ratio(fg, bg), on })).sort((a, b) => a.value - b.value)[0];
}

// ---- .q-btn rules from tokens.css, applied in cascade order (specificity, then source order).
const rules = [...css.matchAll(/(\.q-btn[\w-]*)((?::[\w-]+(?:\([^)]*\))?)*)\s*\{([^}]*)\}/g)].map((m, order) => ({
  cls: m[1],
  pseudo: m[2],
  order,
  specificity: (`${m[1]}${m[2]}`.match(/[.:][\w-]+/g) ?? []).filter((s) => s !== ":not").length,
  decls: Object.fromEntries([...m[3].matchAll(/(?:^|;)\s*(background|color)\s*:\s*([^;]+)/g)].map((d) => [d[1], d[2].trim()])) as { background?: string; color?: string },
}));
const variants = [...new Set(rules.filter((r) => !r.pseudo && r.cls !== ".q-btn").map((r) => r.cls))];

/** The background and text color of `.q-btn` + `variant` in a state; `background` is null when the button is transparent to its surface. */
function button(variant: string | null, state: "base" | "hover", vars: Vars) {
  const applies = (r: (typeof rules)[number]) =>
    (r.cls === ".q-btn" || r.cls === variant) && (r.pseudo === "" || (state === "hover" && r.pseudo.includes(":hover") && !/:disabled(?!\))/.test(r.pseudo)));
  const decls = rules.filter(applies).sort((a, b) => a.specificity - b.specificity || a.order - b.order).reduce((acc, r) => ({ ...acc, ...r.decls }), {} as { background?: string; color?: string });
  const bg = color(decls.background!, vars);
  return { background: bg[3] === 0 ? null : bg, color: color(decls.color!, vars) };
}

describe("button contrast (tokens.css)", () => {
  it("finds the button variants", () => expect(variants).toEqual(expect.arrayContaining([".q-btn-primary", ".q-btn-danger", ".q-btn-run", ".q-btn-stop", ".q-btn-ghost"])));

  it.each(Object.entries(themes))("%s: every .q-btn variant keeps its label readable at rest and on hover", (theme, vars) => {
    const failures: string[] = [];
    for (const v of [null, ...variants])
      for (const state of ["base", "hover"] as const) {
        const b = button(v, state, vars);
        const w = worst(b.color, b.background, vars);
        if (w.value < MIN) failures.push(`${v ?? ".q-btn"} ${state} on ${w.on}: ${w.value.toFixed(2)}`);
      }
    expect(failures, theme).toEqual([]);
  });

  it.each(Object.entries(themes))("%s: muted text in a highlighted row (.q-hl) is readable on every highlight", (theme, vars) => {
    const failures: string[] = [];
    const hl = color("var(--q-muted-hl)", vars);
    for (const [name, fill] of [["accent/25", withAlpha(color("var(--q-accent)", vars), 0.25)], ["err/10", withAlpha(color("var(--q-err)", vars), 0.1)], ["warn/15", withAlpha(color("var(--q-warn)", vars), 0.15)], ["--q-active", color("var(--q-active)", vars)]] as const) {
      const w = worst(hl, fill, vars);
      if (w.value < MIN) failures.push(`--q-muted-hl on ${name} over ${w.on}: ${w.value.toFixed(2)}`);
    }
    expect(failures, theme).toEqual([]);
  });

  it.each(Object.entries(themes))("%s: text tokens are readable on every surface", (theme, vars) => {
    const failures: string[] = [];
    for (const t of ["--q-fg", "--q-muted", "--q-accent", "--q-ok", "--q-warn", "--q-err", "--q-wait-short", "--q-wait-mid", "--q-wait-long"]) {
      const w = worst(color(`var(${t})`, vars), null, vars);
      if (w.value < MIN) failures.push(`${t} on ${w.on}: ${w.value.toFixed(2)}`);
    }
    expect(failures, theme).toEqual([]);
  });
});

// ---- Object Explorer rows (§12): every row surface — at rest (--q-panel), hovered, selected (--q-row-sel, a .q-hl
// row) and a search kind header (--q-panel-2) — keeps status and label text at 4.5:1 and kind icons at 3:1.
const ROW_SURFACES = ["--q-panel", "--q-hover", "--q-row-sel", "--q-panel-2"] as const;

describe("Object Explorer row contrast (tokens.css)", () => {
  it.each(Object.entries(themes))("%s: label, muted and status text reach 4.5:1 on every row surface", (theme, vars) => {
    const failures: string[] = [];
    for (const s of ROW_SURFACES) {
      const bg = color(`var(${s})`, vars);
      const muted = s === "--q-row-sel" ? "--q-muted-hl" : "--q-muted"; // the selected row is a .q-hl row
      for (const t of ["--q-fg", muted, "--q-accent", "--q-ok", "--q-warn", "--q-err"]) {
        const r = ratio(color(`var(${t})`, vars), bg);
        if (r < MIN) failures.push(`${t} on ${s}: ${r.toFixed(2)}`);
      }
    }
    expect(failures, theme).toEqual([]);
  });

  it.each(Object.entries(themes))("%s: kind icons and the focus ring reach 3:1 on every row surface", (theme, vars) => {
    const failures: string[] = [];
    const icons = Object.keys(vars).filter((k) => k.startsWith("--q-obj-"));
    expect(icons.length).toBeGreaterThanOrEqual(8);
    for (const s of ROW_SURFACES)
      for (const t of [...icons, "--q-accent", "--q-muted"]) {
        const r = ratio(color(`var(${t})`, vars), color(`var(${s})`, vars));
        if (r < NON_TEXT) failures.push(`${t} on ${s}: ${r.toFixed(2)}`);
      }
    expect(failures, theme).toEqual([]);
  });

  it.each(Object.entries(themes))("%s: the selected row is told apart from a hovered one", (theme, vars) => {
    expect(ratio(color("var(--q-row-sel)", vars), color("var(--q-panel)", vars)), theme).toBeGreaterThan(1.05);
    expect(vars["--q-row-sel"], theme).not.toBe(vars["--q-hover"]);
  });
});

// ---- WCAG 1.4.11: control boundaries and the focus ring reach 3:1 against the surface they sit on.
const NON_TEXT = 3;

describe("non-text contrast (tokens.css)", () => {
  it.each(Object.entries(themes))("%s: field / launch-strip borders, the Run and Cancel borders and the focus ring reach 3:1 on every surface", (theme, vars) => {
    const failures: string[] = [];
    for (const t of ["--q-line-strong", "--q-ok", "--q-err", "--q-warn", "--q-accent"]) {
      const w = worst(color(`var(${t})`, vars), null, vars);
      if (w.value < NON_TEXT) failures.push(`${t} on ${w.on}: ${w.value.toFixed(2)}`);
    }
    expect(failures, theme).toEqual([]);
  });

  it.each(Object.entries(themes))("%s: a .q-input field's border reaches 3:1 on every surface (it was --q-line, about 1.4:1)", (theme, vars) => {
    const border = /\.q-input\s*\{[^}]*border:\s*1px solid ([^;]+);/.exec(css)![1].trim();
    const w = worst(color(border, vars), null, vars);
    expect(w.value, `${theme} ${border} on ${w.on}`).toBeGreaterThanOrEqual(NON_TEXT);
  });

  it.each(Object.entries(themes))("%s: a bordered control keeps its outline under the hover fill", (theme, vars) => {
    const line = color("var(--q-line)", vars);
    const hover = color("var(--q-hover)", vars);
    expect(ratio(line, hover), theme).toBeGreaterThanOrEqual(1.1);
  });
});

// ---- Diff editors (schema compare, rename preview, diff dialog): Monaco paints --q-diff-* on the changed line and the
// same tint again on its changed text, as #RRGGBBAA (alpha rounded to a byte), over --q-bg (the viewers draw no
// current-line fill). Its default tints left comments at 2.3:1 and functions at 3.9:1.
describe("diff tint contrast (tokens.css)", () => {
  const painted = (token: string, vars: Vars): Rgba => {
    const c = color(`var(${token})`, vars);
    return [c[0], c[1], c[2], Math.round(c[3] * 255) / 255];
  };
  it.each(Object.entries(themes))("%s: every syntax color, the text and the line numbers reach 4.5:1 on a changed line and on its changed text", (theme, vars) => {
    const failures: string[] = [];
    const bg = color("var(--q-bg)", vars);
    // Every syntax theme's colors in this mode (src/editor/syntaxTheme.ts), then the text and line-number tokens.
    const syntax = SYNTAX_THEMES.flatMap((t) => Object.entries(t[theme as "dark" | "light"]).map(([role, hex]) => [`${t.id}.${role}`, hex] as const));
    expect(syntax.length).toBeGreaterThanOrEqual(9 * SYNTAX_THEMES.length);
    const fgs = [...syntax, ...["--q-fg", "--q-muted"].map((v) => [v, `var(${v})`] as const)];
    for (const tint of ["--q-diff-insert", "--q-diff-remove"]) {
      const t = painted(tint, vars);
      expect(t[3], `${theme} ${tint} is a tint`).toBeGreaterThan(0);
      const line = over(t, bg), text = over(t, line);
      for (const [name, fg] of fgs)
        for (const [layer, b] of [["line", line], ["text", text]] as const) {
          const r = ratio(color(fg, vars), b);
          if (r < MIN) failures.push(`${name} on ${tint} ${layer}: ${r.toFixed(2)}`);
        }
    }
    expect(failures, theme).toEqual([]);
  });
});

// ---- React Flow (ER diagram, plan view): the zoom controls were React Flow's white box in the dark theme (1.17:1).
describe("React Flow controls (tokens.css)", () => {
  const flow = Object.fromEntries([...block(".react-flow").matchAll(/(--xy-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
  it.each(Object.entries(themes))("%s: icons reach 3:1 on the button at rest and on hover; the group's edge 3:1 on the canvas", (theme, vars) => {
    const canvas = color("var(--q-bg)", vars);
    const icon = color(flow["--xy-controls-button-color"], vars);
    for (const bg of ["--xy-controls-button-background-color", "--xy-controls-button-background-color-hover"])
      expect(ratio(icon, color(flow[bg], vars)), `${theme} icon on ${bg}`).toBeGreaterThanOrEqual(NON_TEXT);
    expect(ratio(color(flow["--xy-controls-button-color-hover"], vars), color(flow["--xy-controls-button-background-color-hover"], vars))).toBeGreaterThanOrEqual(NON_TEXT);
    const edge = /var\(--q-[\w-]+\)/.exec(flow["--xy-controls-box-shadow"])![0];
    expect(ratio(color(edge, vars), canvas), `${theme} control edge`).toBeGreaterThanOrEqual(NON_TEXT);
    // A themed surface, not React Flow's light default.
    expect(ratio(color(flow["--xy-controls-button-background-color"], vars), canvas), `${theme} button vs canvas`).toBeLessThan(1.5);
    expect(flow["--xy-minimap-background-color"]).toMatch(/^var\(--q-/);
  });
});

// ---- Scrollbars: the thumb was --q-hover (~1.2:1), so a scrolling panel (the import dialog at 960×560) looked complete.
describe("scrollbar thumb (tokens.css)", () => {
  const thumb = /::-webkit-scrollbar-thumb\s*\{([^}]*)\}/.exec(css)![1];
  const hover = /::-webkit-scrollbar-thumb:hover\s*\{([^}]*)\}/.exec(css)![1];
  const fill = (decls: string) => /background:\s*([^;]+);/.exec(decls)![1].trim();
  it.each(Object.entries(themes))("%s: the thumb reaches 3:1 on every surface, at rest and on hover", (theme, vars) => {
    for (const s of SURFACES) {
      expect(ratio(color(fill(thumb), vars), color(`var(${s})`, vars)), `${theme} thumb on ${s}`).toBeGreaterThanOrEqual(NON_TEXT);
      expect(ratio(color(fill(hover), vars), color(`var(${s})`, vars)), `${theme} hovered thumb on ${s}`).toBeGreaterThanOrEqual(NON_TEXT);
    }
  });
});

// ---- An unavailable destructive button (Kill session before a session is picked, the kill dialog before its confirms)
// is a plain button: the dimmed red fill read as armed.
describe("disabled danger button (tokens.css)", () => {
  const rule = /\.q-btn-danger:disabled,\s*\.q-btn-danger\[aria-disabled="true"\]\s*\{([^}]*)\}/.exec(css);
  it("both disabled forms get the neutral button surface", () => {
    expect(rule, "a .q-btn-danger:disabled / [aria-disabled] rule").not.toBeNull();
    const decls = Object.fromEntries([...rule![1].matchAll(/(background|color|border-color)\s*:\s*([^;]+);/g)].map((d) => [d[1], d[2].trim()]));
    expect(decls).toMatchObject({ background: "var(--q-raised)", color: "var(--q-fg)", "border-color": "var(--q-line)" });
  });
  it("hover never repaints an aria-disabled button", () => {
    for (const cls of [".q-btn", ".q-btn-danger"]) {
      const hover = new RegExp(`\\${cls}:hover([^{]*)\\{`).exec(css);
      expect(hover?.[1], cls).toContain(':not([aria-disabled="true"])');
    }
    expect(css).toMatch(/\.q-btn\[aria-disabled="true"\]\s*\{\s*opacity:/);
  });
});

// ---- Tailwind fill + text combinations in the components.
/** A class string and the outcome of the conditions that chose it (condition source text → branch taken). */
type Alt = { text: string; conds: Record<string, boolean> };
/** A class string; `ancestors`: the class strings (alternatives) of its JSX parents, nearest first, up to a component
 *  or a parent painted by an inline style. */
type Classes = { text: string; line: number; hasText: boolean | null; conds?: Record<string, boolean>; ancestors?: Alt[][] };

/** Component classes other than .q-btn that paint a color (`.q-kbd` → --q-muted): utilities override them. */
const componentPaint: Record<string, { background?: string; color?: string }> = Object.fromEntries(
  [...css.matchAll(/\.(q-(?!btn)[\w-]+)\s*\{([^}]*)\}/g)]
    .map((m) => [m[1], Object.fromEntries([...m[2].matchAll(/(?:^|;)\s*(background|color)\s*:\s*([^;]+)/g)].map((d) => [d[1], d[2].trim()]))] as const)
    .filter(([, d]) => d.background || d.color),
);

/** Every class string an expression can produce: each branch of a conditional joined to the static part of its template. */
function classAlts(e: ts.Expression, conds: Record<string, boolean> = {}): Alt[] {
  if (ts.isStringLiteral(e) || ts.isNoSubstitutionTemplateLiteral(e)) return [{ text: e.text, conds }];
  if (ts.isParenthesizedExpression(e)) return classAlts(e.expression, conds);
  if (ts.isConditionalExpression(e)) {
    const c = e.condition.getText();
    return [...classAlts(e.whenTrue, { ...conds, [c]: true }), ...classAlts(e.whenFalse, { ...conds, [c]: false })];
  }
  if (ts.isBinaryExpression(e))
    return e.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken ? classAlts(e.right, { ...conds, [e.left.getText()]: true }) : [...classAlts(e.left, conds), ...classAlts(e.right, conds)];
  if (ts.isTemplateExpression(e)) {
    const fixed = [e.head.text, ...e.templateSpans.map((s) => s.literal.text)].join(" ");
    return [{ text: fixed, conds }, ...e.templateSpans.flatMap((s) => classAlts(s.expression, conds).map((alt) => ({ text: `${fixed} ${alt.text}`, conds: alt.conds })))];
  }
  return [];
}
/** Two alternatives can render together unless they took different branches of the same condition. */
const compatible = (a: Record<string, boolean>, b: Record<string, boolean>) => Object.keys(a).every((k) => !(k in b) || a[k] === b[k]);

/** Whether a JSX element renders text of its own: text or an expression child (element children paint their own). */
function jsxHasText(attr: ts.JsxAttribute): boolean {
  const el = attr.parent.parent;
  if (ts.isJsxSelfClosingElement(el)) return false;
  const children = (el.parent as ts.JsxElement).children;
  return children.some((c) => (ts.isJsxText(c) && c.text.trim() !== "") || ts.isJsxExpression(c));
}

/** Whether a JSX element paints its background from an inline style (an environment color badge). */
const styledFill = (attrs: ts.JsxAttributes, sf: ts.SourceFile) =>
  attrs.properties.some((a) => ts.isJsxAttribute(a) && a.name.getText(sf) === "style" && /\bbackground/.test(a.initializer?.getText(sf) ?? ""));

/** Class strings of the intrinsic JSX elements around an attribute's element, nearest first; a component or a parent with
 *  an inline-style background ends the chain. */
function jsxAncestors(attr: ts.JsxAttribute, sf: ts.SourceFile): Alt[][] | undefined {
  if (styledFill(attr.parent, sf)) return undefined;
  const out: Alt[][] = [];
  let n: ts.Node = attr.parent.parent.parent; // the element's JsxElement (or the parent of a self-closing one)
  if (ts.isJsxElement(n)) n = n.parent;
  for (; n && out.length < 8; n = n.parent) {
    if (ts.isJsxExpression(n) || ts.isJsxFragment(n) || ts.isParenthesizedExpression(n) || ts.isConditionalExpression(n) || ts.isBinaryExpression(n)) continue;
    if (!ts.isJsxElement(n)) break;
    const tag = n.openingElement.tagName.getText(sf);
    if (!/^[a-z]/.test(tag) || styledFill(n.openingElement.attributes, sf)) break; // a component: what it renders around its children is unknown here
    const cls = n.openingElement.attributes.properties.find((a): a is ts.JsxAttribute => ts.isJsxAttribute(a) && a.name.getText(sf) === "className");
    const init = cls?.initializer && (ts.isJsxExpression(cls.initializer) ? cls.initializer.expression : cls.initializer);
    out.push(init ? classAlts(init as ts.Expression) : []);
  }
  return out;
}

function sourceClasses(): Classes[] {
  const out: Classes[] = [];
  const walkDir = (dir: string): string[] =>
    readdirSync(dir, { withFileTypes: true }).flatMap((d) => (d.isDirectory() ? (d.name === "__tests__" ? [] : walkDir(join(dir, d.name))) : d.name.endsWith(".tsx") ? [join(dir, d.name)] : []));
  for (const file of walkDir(join(process.cwd(), "src"))) {
    const sf = ts.createSourceFile(file, readFileSync(file, "utf8"), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    const rel = relative(process.cwd(), file);
    const at = (n: ts.Node) => sf.getLineAndCharacterOfPosition(n.getStart(sf)).line + 1;
    const seen = new Set<ts.Node>();
    const add = (e: ts.Expression, hasText: boolean | null, ancestors?: Alt[][]) => {
      seen.add(e);
      for (const a of classAlts(e)) out.push({ text: `${rel}:${at(e)}\t${a.text}`, line: at(e), hasText, conds: a.conds, ancestors });
    };
    const visit = (n: ts.Node): void => {
      if (ts.isJsxAttribute(n) && n.name.getText(sf) === "className" && n.initializer) {
        const init = ts.isJsxExpression(n.initializer) ? n.initializer.expression : n.initializer;
        if (init) add(init as ts.Expression, jsxHasText(n), jsxAncestors(n, sf));
      } else if ((ts.isTemplateExpression(n) || ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) && ![...seen].some((s) => s.pos <= n.pos && n.end <= s.end)) {
        // Class strings built outside the attribute (`const cls = …`): their element is unknown.
        if (!ts.isImportDeclaration(n.parent) && !ts.isTemplateSpan(n.parent)) add(n, null);
        if (ts.isTemplateExpression(n)) return;
      }
      ts.forEachChild(n, visit);
    };
    visit(sf);
  }
  return out;
}

type Paint = { fill?: Rgba; text?: Rgba; btn?: string; exempt?: boolean; compFill?: Rgba; compText?: Rgba };
const SKIP_VARIANTS = new Set(["disabled", "placeholder", "selection", "file", "marker"]);
// --q-faint is the tier for disabled controls, placeholders and decorative hints, and text-line paints separator glyphs
// (WCAG 1.4.3 exempts inactive and incidental text): a class string that sets them is not a readable-text claim.
const EXEMPT_TEXT = new Set(["faint", "line"]);

/** Fill and text color per state (`""` = at rest, else the variant chain such as `hover`). */
function paints(classes: string, vars: Vars): Map<string, Paint> {
  const states = new Map<string, Paint>();
  for (const token of classes.split(/\s+/).filter(Boolean)) {
    const parts = token.split(":");
    const util = parts.pop()!;
    if (parts.some((p) => SKIP_VARIANTS.has(p))) continue;
    const key = parts.join(":");
    const p = states.get(key) ?? {};
    const btn = /^q-btn(-[\w-]+)?$/.exec(util);
    if (btn && key === "") p.btn = btn[1] ? `.q-btn${btn[1]}` : (p.btn ?? ".q-btn");
    const comp = key === "" ? componentPaint[util] : undefined;
    if (comp?.background) p.compFill = color(comp.background, vars);
    if (comp?.color) p.compText = color(comp.color, vars);
    const m = /^(bg|text)-([\w-]+?)(?:\/(\d+))?$/.exec(util);
    const name = m?.[2];
    if (m?.[1] === "text" && name && EXEMPT_TEXT.has(name)) p.exempt = true;
    else if (m && name && (name in twColors || name in NAMED)) {
      const c = withAlpha(name in NAMED ? NAMED[name] : color(`var(${twColors[name]})`, vars), m[3] ? +m[3] / 100 : 1);
      if (m[1] === "bg") p.fill = c;
      else p.text = c;
    }
    states.set(key, p);
  }
  for (const p of states.values()) {
    p.fill ??= p.compFill;
    p.text ??= p.compText;
  }
  return states;
}

/** The fill, text color and .q-btn colors of one state of a class string. */
function stateOf(states: Map<string, Paint>, key: string, vars: Vars) {
  const s = states.get(key) ?? {}, base = states.get("") ?? {};
  const hover = key.split(":").some((v) => v.endsWith("hover"));
  const btn = base.btn ? button(base.btn === ".q-btn" ? null : base.btn, hover ? "hover" : "base", vars) : null;
  return { fill: s.fill ?? base.fill ?? btn?.background ?? null, painted: !!(s.fill ?? base.fill ?? btn), text: s.text ?? base.text ?? btn?.color ?? null, exempt: (s.exempt ?? base.exempt) && !s.text };
}

/** The theme as seen inside a highlighted row: `.q-hl` remaps the muted text color. */
const highlighted = (vars: Vars): Vars => ({ ...vars, "--q-muted": vars["--q-muted-hl"] });
const hasClass = (classes: string, name: string) => classes.split(/\s+/).includes(name);

/** Class strings whose text falls below MIN on their fill, at rest or in a hover state; an element without a fill of its
 *  own is checked on the fill of its nearest painted JSX parent (a `q-kbd` hint inside a primary button). */
function failuresOf(all: Classes[], themeVars: Vars): string[] {
  const failures = new Set<string>();
  const short = (x: string) => x.trim().replace(/\s+/g, " ");
  for (const c of all) {
    const [where, classes] = c.text.split("\t");
    const vars = hasClass(classes, "q-hl") ? highlighted(themeVars) : themeVars;
    const states = paints(classes, vars);
    for (const key of new Set(["", ...states.keys()])) {
      const s = states.get(key) ?? {};
      if (key !== "" && !s.fill && !s.text) continue;
      const own = stateOf(states, key, vars);
      if (own.exempt) continue;
      if (own.painted) {
        // An element with no class text color inherits the body text color; unknown elements are only checked when they set one.
        const text = own.text ?? (c.hasText ? color("var(--q-fg)", vars) : null);
        if (!text) continue;
        const w = worst(text, own.fill, vars);
        if (w.value < MIN) failures.add(`${where} [${key || "rest"}] ${short(classes)} → ${w.value.toFixed(2)} on ${w.on}`);
        continue;
      }
      if (!c.hasText || !c.ancestors) continue;
      // Text of its own on a parent's fill: the nearest parent that paints one, in each of its alternatives and states. A
      // color the element inherits from a parent in between is read in the painted parent's theme (.q-hl remaps it).
      const between: Alt[] = [];
      for (const alts of c.ancestors) {
        const parents = alts.filter((a) => compatible(c.conds ?? {}, a.conds)).map((a) => ({ a: a.text, st: paints(a.text, vars) }));
        const painted = parents.filter(({ st }) => [...new Set(["", ...st.keys()])].some((k) => stateOf(st, k, vars).painted));
        if (!painted.length) {
          between.push(...alts.filter((a) => compatible(c.conds ?? {}, a.conds)));
          continue;
        }
        for (const { a, st } of painted)
          for (const k of new Set(["", ...st.keys()])) {
            const own = st.get(k) ?? {};
            if (k !== "" && !own.fill && !own.text) continue; // a variant that paints nothing (`[&>button]:shrink-0`)
            const p = stateOf(st, k, vars);
            if (!p.painted || p.exempt) continue;
            const inside = hasClass(a, "q-hl") ? highlighted(vars) : vars;
            const textIn = (x: string) => stateOf(paints(x, inside), "", inside);
            const mine = stateOf(paints(classes, inside), key, inside);
            if (mine.exempt) continue;
            const inherited = between.map((b) => textIn(b.text)).find((t) => t.text || t.exempt);
            if (!mine.text && inherited?.exempt) continue;
            const fg = mine.text ?? inherited?.text ?? p.text ?? color("var(--q-fg)", vars);
            const w = worst(fg, p.fill, vars);
            if (w.value < MIN) failures.add(`${where} [${k || "rest"} of parent "${short(a)}"] ${short(classes)} → ${w.value.toFixed(2)} on ${w.on}`);
          }
        break;
      }
    }
  }
  return [...failures];
}

describe("fill + text contrast (components)", () => {
  const all = sourceClasses();
  const sample = (text: string, hasText: boolean | null = true, ancestors?: (string | Alt)[][], conds: Record<string, boolean> = {}): Classes =>
    ({ text: `sample.tsx:1\t${text}`, line: 1, hasText, conds, ancestors: ancestors?.map((alts) => alts.map((a) => (typeof a === "string" ? { text: a, conds: {} } : a))) });

  it("finds the class strings of the components", () => expect(all.length).toBeGreaterThan(500));

  it.each(Object.entries(themes))("%s: flags unreadable samples and passes readable ones", (_, vars) => {
    expect(failuresOf([sample("bg-err text-err")], vars)).toHaveLength(1); // a fill with text of its own color
    expect(failuresOf([sample("bg-err/40 text-err")], vars)).toHaveLength(1); // a same-hue tint above 15 %
    expect(failuresOf([sample("px-2 text-fg hover:bg-fg")], vars)).toHaveLength(1); // only the hover state is unreadable
    expect(failuresOf([sample("q-btn q-btn-primary bg-white")], vars)).toHaveLength(1); // a utility overriding the button fill
    expect(failuresOf([sample("rounded bg-accent-solid")], vars)).toHaveLength(1); // inherited body text on a solid fill
    expect(failuresOf([sample("rounded bg-accent-solid", false), sample("bg-accent-solid", null)], vars)).toEqual([]); // no text of its own / unknown element
    expect(failuresOf([sample("bg-err/15 text-err"), sample("bg-accent-solid text-white"), sample("q-btn q-btn-danger"), sample("bg-panel text-faint")], vars)).toEqual([]);
    // A child without a fill of its own is read on its parent's: a muted key hint inside a primary button, a muted detail
    // on a highlighted row (also when only the hover state paints it); the parent's own text color passes.
    expect(failuresOf([sample("q-kbd", true, [["q-btn q-btn-primary"]])], vars)).toHaveLength(1);
    expect(failuresOf([sample("ml-2 text-muted", true, [["truncate"], ["px-3 bg-accent-solid", "px-3"]])], vars)).toHaveLength(1);
    expect(failuresOf([sample("text-muted", true, [["px-3 hover:bg-accent-solid"]])], vars)).toHaveLength(1);
    expect(failuresOf([sample("font-bold", true, [["q-btn q-btn-primary"]]), sample("q-kbd", false, [["q-btn q-btn-primary"]]), sample("text-faint", true, [["q-btn q-btn-primary"]])], vars)).toEqual([]);
    // Branches of one condition pair up: the muted subtitle of the plain header never sits on the danger header's fill.
    const header = [[{ text: "px-4 bg-err-solid text-white", conds: { danger: true } }, { text: "px-4 border-b", conds: { danger: false } }]];
    expect(failuresOf([sample("text-muted", true, header, { danger: false }), sample("text-white", true, header, { danger: true })], vars)).toEqual([]);
    expect(failuresOf([sample("text-muted", true, header, { danger: true })], vars)).toHaveLength(1);
    // A highlighted row's muted text uses the highlight's muted color, on the row and in its children.
    expect(failuresOf([sample("text-muted", true, [["px-3 bg-accent/25"]])], vars)).toHaveLength(1);
    expect(failuresOf([sample("text-muted", true, [["px-3 bg-accent/25 q-hl"]]), sample("px-3 bg-accent/25 q-hl text-muted")], vars)).toEqual([]);
    // …also when the muted class sits on a parent in between, and a faint one there exempts the text.
    expect(failuresOf([sample("truncate", true, [["text-muted"], ["px-3 bg-accent/25 q-hl"]])], vars)).toEqual([]);
    expect(failuresOf([sample("truncate", true, [["text-muted"], ["px-3 bg-accent/25"]])], vars)).toHaveLength(1);
    expect(failuresOf([sample("truncate", true, [["text-faint"], ["q-btn q-btn-primary"]])], vars)).toEqual([]);
    // A variant that paints nothing is not a state of its own.
    expect(failuresOf([sample("text-fg", true, [["bg-panel [&>button]:shrink-0"]])], vars)).toEqual([]);
  });

  it.each(Object.entries(themes))("%s: every class string that paints a fill keeps its text readable at rest and on hover", (theme, vars) => {
    expect(failuresOf(all, vars), theme).toEqual([]);
  });

  // §12: --q-faint (about 2.3:1 in the dark theme) is for disabled, placeholder and decorative text. Timestamps, counts,
  // hints, line numbers and notes had drifted onto it; they read on the muted tier. The Explorer is checked by its own suite.
  it("no informative text sits on the faint tier: text-faint only behind disabled: or placeholder:", () => {
    const misuse = all
      .filter((c) => !c.text.startsWith(join("src", "explorer")))
      .filter((c) => c.text.split("\t")[1].split(/\s+/).some((t) => t === "text-faint" || (t.endsWith(":text-faint") && !/^(disabled|placeholder):/.test(t))))
      .map((c) => c.text.split("\t")[0]);
    expect(misuse).toEqual([]);
  });
});

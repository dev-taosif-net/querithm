import { describe, expect, it } from "vitest";
// @ts-expect-error plain .mjs bench script without type declarations
import { collectLayout, contrast, effectiveContrast, fieldBoundaryOk, findLayoutIssues, minContrast, parseColor, shadowRings } from "../bench/layout-check.mjs";

type Rect = { l: number; t: number; r: number; b: number };
const rect = (l: number, t: number, w: number, h: number): Rect => ({ l, t, r: l + w, b: t + h });
const WHITE = { color: "rgb(255, 255, 255)", image: false, opacity: 1 };
const run = (over: Record<string, unknown> = {}) => ({ rects: [rect(12, 12, 40, 12)], vis: [rect(12, 12, 40, 12)], layer: 0, inControls: [0], ellipsis: false, desc: "span", color: "rgb(0, 0, 0)", fontSize: 11, fontWeight: 500, bgs: [WHITE], ...over });
const control = (i: number, r: Rect, over: Record<string, unknown> = {}) => ({ i, desc: `button ${i}`, layer: 0, rect: r, vis: r, clippedBy: null, hits: [], disabled: false, inside: [], texts: [], ...over });
const snap = (over: Record<string, unknown>) => ({ viewport: { w: 1440, h: 900 }, controls: [], texts: [], lines: [], exemptColors: [], ...over });
const kinds = (s: unknown) => findLayoutIssues(s).map((i: { kind: string }) => i.kind);

describe("parseColor", () => {
  it("reads the forms Chrome computes", () => {
    expect(parseColor("rgb(30, 34, 46)")).toEqual([30, 34, 46, 1]);
    expect(parseColor("rgba(0, 0, 0, 0.5)")).toEqual([0, 0, 0, 0.5]);
    expect(parseColor("transparent")).toEqual([0, 0, 0, 0]);
    expect(parseColor("#2563eb")).toEqual([37, 99, 235, 1]);
    expect(parseColor("color(srgb 1 0 0 / 0.15)")).toEqual([255, 0, 0, 0.15]);
    // Tailwind v4 opacity modifiers compute to oklab; its palette to oklch.
    const accent = parseColor("oklab(0.5461 -0.0267 -0.2135 / 0.15)"); // #2563eb at 15 %
    expect(accent[3]).toBeCloseTo(0.15);
    accent.slice(0, 3).forEach((x: number, i: number) => expect(Math.abs(x - [37, 99, 235][i])).toBeLessThan(1));
    expect(parseColor("oklch(1 0 0)").slice(0, 3).map(Math.round)).toEqual([255, 255, 255]);
    expect(() => parseColor("lab(50 0 0)")).toThrow(/unsupported/);
  });
});

describe("effectiveContrast", () => {
  it("composites translucent fills down to the first opaque ancestor", () => {
    expect(effectiveContrast(run())).toBeCloseTo(21);
    // White text on a 50 % black tint over white: the same as on #808080.
    const tinted = effectiveContrast(run({ color: "rgb(255, 255, 255)", bgs: [{ color: "rgba(0, 0, 0, 0.5)", image: false, opacity: 1 }, WHITE] }));
    expect(tinted).toBeCloseTo(contrast([255, 255, 255], [128, 128, 128]), 1);
  });

  it("dims the text by the opacity of the elements in between", () => {
    const dimmed = effectiveContrast(run({ bgs: [{ color: "transparent", image: false, opacity: 0.5 }, WHITE] }));
    expect(dimmed).toBeCloseTo(contrast([128, 128, 128], [255, 255, 255]), 1);
  });

  it("is unknown over a background image", () => expect(effectiveContrast(run({ bgs: [{ color: "transparent", image: true, opacity: 1 }, WHITE] }))).toBeNull());

  it("asks 3:1 of large text only", () => {
    expect(minContrast(run())).toBe(4.5);
    expect(minContrast(run({ fontSize: 24 }))).toBe(3);
    expect(minContrast(run({ fontSize: 19, fontWeight: 700 }))).toBe(3);
    expect(minContrast(run({ fontSize: 19, fontWeight: 400 }))).toBe(4.5);
    // An icon glyph is non-text contrast (3:1); a label with a letter or digit is text.
    expect(minContrast(run({ text: "◇" }))).toBe(3);
    expect(minContrast(run({ text: "× ⇄" }))).toBe(3);
    expect(minContrast(run({ text: "F5" }))).toBe(4.5);
    expect(minContrast(run({ text: "Données" }))).toBe(4.5);
  });
});

describe("findLayoutIssues", () => {
  it("passes a clean layout", () => {
    expect(findLayoutIssues(snap({ controls: [control(0, rect(10, 10, 60, 24), { texts: [run()] }), control(1, rect(80, 10, 60, 24))] }))).toEqual([]);
  });

  it("flags a control covered by an element of its own layer, not by a popup above it", () => {
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24), { hits: [{ desc: "div.badge", layer: 0 }] })] }))).toEqual(["covered"]);
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24), { hits: [{ desc: "div.menu", layer: 3 }] })] }))).toEqual([]);
  });

  it("flags overlapping controls unless one contains the other or they sit in different layers", () => {
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24)), control(1, rect(60, 14, 60, 24))] }))).toEqual(["overlap"]);
    expect(findLayoutIssues(snap({ controls: [control(0, rect(10, 10, 60, 24)), control(1, rect(60, 14, 60, 24))] }))[0]).toMatchObject({ other: "button 1", detail: "10×20 px", at: { l: 60, t: 14, r: 70, b: 34 } });
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24)), control(1, rect(60, 14, 60, 24), { layer: 2 })] }))).toEqual([]);
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 100, 40)), control(1, rect(20, 14, 20, 20), { inside: [0] })] }))).toEqual([]);
    // Touching edges are not an overlap.
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24)), control(1, rect(70, 10, 60, 24))] }))).toEqual([]);
  });

  it("a marked in-field button (data-layout-adornment) may sit inside its field, never outside it", () => {
    // A password box's show / hide button in the box's right padding.
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 200, 32)), control(1, rect(178, 14, 28, 24), { adornment: true })] }))).toEqual([]);
    // Unmarked, the same button is an overlap; marked but sticking out of the field, too.
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 200, 32)), control(1, rect(178, 14, 28, 24))] }))).toEqual(["overlap"]);
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 200, 32)), control(1, rect(196, 14, 28, 24), { adornment: true })] }))).toEqual(["overlap"]);
  });

  it("flags text under a control, not text beside it or inside it", () => {
    const text = (r: Rect, over = {}) => run({ rects: [r], vis: [r], inControls: [], desc: "span.title", ...over });
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24))], texts: [text(rect(40, 14, 80, 12))] }))).toEqual(["text-overlap"]);
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24))], texts: [text(rect(80, 14, 80, 12))] }))).toEqual([]);
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24))], texts: [text(rect(40, 14, 80, 12), { layer: 1 })] }))).toEqual([]);
  });

  it("flags a line through a control, not one along its edge or its own border", () => {
    const line = (seg: Rect, over = {}) => ({ desc: "div.toolbar border-bottom", layer: 0, color: "rgb(38, 43, 56)", seg, related: [], ...over });
    const btn = control(0, rect(10, 10, 60, 24));
    expect(kinds(snap({ controls: [btn], lines: [line(rect(0, 20, 200, 1))] }))).toEqual(["line-through"]);
    expect(kinds(snap({ controls: [btn], lines: [line(rect(40, 0, 1, 100))] }))).toEqual(["line-through"]);
    expect(kinds(snap({ controls: [btn], lines: [line(rect(0, 34, 200, 1))] }))).toEqual([]); // just below
    expect(kinds(snap({ controls: [btn], lines: [line(rect(0, 33.5, 200, 1))] }))).toEqual([]); // flush with the bottom edge
    expect(kinds(snap({ controls: [btn], lines: [line(rect(0, 20, 200, 1), { related: [0] })] }))).toEqual([]);
    expect(kinds(snap({ controls: [btn], lines: [line(rect(0, 20, 200, 1), { color: "rgba(0, 0, 0, 0)" })] }))).toEqual([]);
    // Another control's border: the overlap of the two controls, not a line through one.
    expect(kinds(snap({ controls: [btn], lines: [line(rect(0, 20, 200, 1), { related: [1] })] }))).toEqual([]);
    // The border of a row (a control) that the button sits inside still counts.
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24), { inside: [1] })], lines: [line(rect(0, 20, 200, 1), { related: [1] })] }))).toEqual(["line-through"]);
  });

  it("flags a control cut off by a container that cannot scroll", () => {
    const cut = (scrollable: boolean) => control(0, rect(10, 10, 100, 24), { vis: rect(10, 10, 50, 24), clippedBy: { desc: "div.toolbar", scrollable } });
    expect(kinds(snap({ controls: [cut(false)] }))).toEqual(["clipped"]);
    expect(kinds(snap({ controls: [cut(true)] }))).toEqual([]);
  });

  it("flags a popup its host cuts off, even to nothing; not one that fits or only runs past the window", () => {
    const list = (vis: Rect, clippedBy: string | null = "div.panel-row") => ({ desc: 'div "All columns"', screen: rect(600, 650, 240, 288), vis, clippedBy });
    // The results tool row once held the column picker's list: 28 px of the row, the rest of the list gone.
    expect(findLayoutIssues(snap({ popups: [list(rect(600, 650, 240, 0))] }))).toEqual([
      { kind: "popup-clipped", control: 'div "All columns"', other: "div.panel-row", detail: "0% visible", at: rect(600, 650, 240, 288) },
    ]);
    expect(kinds(snap({ popups: [list(rect(600, 650, 240, 100))] }))).toEqual(["popup-clipped"]);
    expect(kinds(snap({ popups: [list(rect(600, 650, 240, 288))] }))).toEqual([]);
    expect(kinds(snap({ popups: [list(rect(600, 650, 240, 250), null)] }))).toEqual([]);
  });

  it("flags a label that wraps onto a second line inside its control", () => {
    const two = (ellipsis: boolean) => control(0, rect(10, 10, 40, 40), { texts: [run({ rects: [rect(12, 12, 30, 12), rect(12, 26, 20, 12)], vis: [rect(12, 12, 30, 12)], ellipsis })] });
    expect(kinds(snap({ controls: [two(false)] }))).toEqual(["label-wrap"]);
    expect(kinds(snap({ controls: [two(true)] }))).toEqual([]);
    // Two fragments on one line (same top, give or take a pixel) are not a wrap.
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 80, 24), { texts: [run({ rects: [rect(12, 12, 30, 12), rect(42, 13, 30, 12)] })] })] }))).toEqual([]);
  });

  it("flags a label running past its control unless it truncates with an ellipsis", () => {
    const wide = (ellipsis: boolean) => control(0, rect(10, 10, 40, 24), { texts: [run({ rects: [rect(12, 14, 80, 12)], ellipsis })] });
    expect(kinds(snap({ controls: [wide(false)] }))).toEqual(["text-spill"]);
    expect(kinds(snap({ controls: [wide(true)] }))).toEqual([]);
  });

  it("flags an unreadable label, except on a disabled control or in the exempt faint color", () => {
    const dark = run({ color: "rgb(30, 64, 175)", bgs: [{ color: "rgb(30, 58, 138)", image: false, opacity: 1 }] });
    expect(findLayoutIssues(snap({ controls: [control(0, rect(10, 10, 60, 24), { texts: [dark] })] }))).toEqual([
      { kind: "contrast", control: "button 0", detail: expect.stringMatching(/^1\.\d\d:1 \(needs 4\.5\)$/), at: rect(10, 10, 60, 24) },
    ]);
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24), { texts: [dark], disabled: true })] }))).toEqual([]);
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24), { texts: [dark] })], exemptColors: ["#1e40af"] }))).toEqual([]);
  });

  it("reads text outside controls on its backdrop, except exempt colors and text that is not on screen", () => {
    const dim = (over = {}) => run({ inControls: [], desc: "span.hint", color: "rgb(140, 148, 158)", bgs: [{ color: "rgb(44, 63, 92)", image: false, opacity: 1 }], ...over });
    expect(findLayoutIssues(snap({ texts: [dim()] }))).toEqual([{ kind: "contrast", control: "span.hint", detail: expect.stringMatching(/:1 \(needs 4\.5\)$/), at: rect(12, 12, 40, 12) }]);
    expect(kinds(snap({ texts: [dim()], exemptColors: ["#8c949e"] }))).toEqual([]);
    expect(kinds(snap({ texts: [dim({ vis: [] })] }))).toEqual([]);
    expect(kinds(snap({ texts: [dim({ color: "rgb(230, 237, 243)" })] }))).toEqual([]);
  });

  it("ignores controls that are not on screen", () => {
    expect(kinds(snap({ controls: [control(0, rect(10, 10, 60, 24), { vis: rect(0, 0, 0, 0), hits: [{ desc: "x", layer: 0 }] })] }))).toEqual([]);
  });
});

describe("field edges and icons (WCAG 1.4.11)", () => {
  const PANEL = { color: "rgb(22, 25, 34)", image: false, opacity: 1 }; // --q-panel, dark
  const link = (bg: string, borders: string[], backdrop = [PANEL]) => ({ bg, borders, backdrop });
  const field = (chain: unknown[], over: Record<string, unknown> = {}) => ({ desc: "input", vis: rect(10, 10, 120, 24), disabled: false, chain, ...over });

  it("a field whose border is too faint on its backdrop is a finding; --q-line-strong is enough", () => {
    expect(fieldBoundaryOk(field([link("rgb(15, 17, 23)", ["rgb(48, 54, 61)"])]))).toBe(false); // --q-line on --q-bg / --q-panel
    expect(fieldBoundaryOk(field([link("rgb(15, 17, 23)", ["rgb(110, 118, 129)"])]))).toBe(true); // --q-line-strong
    expect(kinds(snap({ fields: [field([link("rgb(15, 17, 23)", ["rgb(48, 54, 61)"])])] }))).toEqual(["boundary"]);
  });

  it("the frame may be drawn by a wrapper (a search box's border around a transparent input)", () => {
    expect(fieldBoundaryOk(field([link("rgba(0, 0, 0, 0)", []), link("rgb(15, 17, 23)", ["rgb(110, 118, 129)"])]))).toBe(true);
  });

  it("a disabled field is not a finding", () => {
    expect(kinds(snap({ fields: [field([link("rgb(15, 17, 23)", ["rgb(48, 54, 61)"])], { disabled: true })] }))).toEqual([]);
  });

  it("an icon under 3:1 is a finding; a faint (decorative) or disabled one is not", () => {
    const icon = (color: string, over: Record<string, unknown> = {}) => ({ desc: "button", vis: rect(10, 10, 14, 14), color, bgs: [PANEL], disabled: false, ...over });
    expect(kinds(snap({ icons: [icon("rgb(75, 82, 96)")] }))).toEqual(["icon-contrast"]); // --q-faint on --q-panel
    expect(kinds(snap({ icons: [icon("rgb(75, 82, 96)")], exemptColors: ["#4b5260"] }))).toEqual([]);
    expect(kinds(snap({ icons: [icon("rgb(75, 82, 96)", { disabled: true })] }))).toEqual([]);
    expect(kinds(snap({ icons: [icon("rgb(140, 148, 158)")] }))).toEqual([]); // --q-muted
  });
});

describe("box-shadow rings as frames", () => {
  it("reads a spread-only ring as a frame color and ignores soft or offset shadows", () => {
    expect(shadowRings("rgb(96, 165, 250) 0px 0px 0px 1px, rgba(62, 65, 86, 0.4) 0px 0px 1px 0px, rgba(62, 65, 86, 0.15) 0px 6px 12px 0px")).toEqual(["rgb(96, 165, 250)"]);
    expect(shadowRings("none")).toEqual([]);
    expect(shadowRings("rgba(0, 0, 0, 0.5) 2px 2px 0px 1px")).toEqual([]);
    expect(shadowRings("rgb(1, 2, 3) 0px 0px 0px 2px inset")).toEqual(["rgb(1, 2, 3)"]);
  });

  it("the in-page copy in collectLayout is the same code", () => {
    const exported = shadowRings.toString().replace(/\s+/g, " ");
    expect(collectLayout.toString().replace(/\s+/g, " ")).toContain(exported);
  });
});


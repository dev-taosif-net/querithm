// Layout audit for the UI smoke: controls covered by or overlapping other elements, lines through controls, clipped,
// spilling or wrapped labels, unreadable text, field edges and icons under 3:1 (WCAG 1.4.11). `collectLayout` runs in the page (self-contained: puppeteer serializes
// it); `findLayoutIssues` is the pure analysis of what it returns (scripts/__tests__/layout-check.test.ts).

/** Colors of spread-only box-shadow rings in a computed box-shadow value; collectLayout carries the same code in-page. */
export const shadowRings = (v) => (v && v !== "none" ? v.split(/,(?![^(]*\))/) : []).flatMap((part) => {
    const color = part.match(/rgba?\([^)]*\)|#[0-9a-f]{3,8}\b/i)?.[0];
    const n = part.replace(/rgba?\([^)]*\)|#[0-9a-f]{3,8}\b|inset/gi, "").trim().split(/\s+/).map(Number.parseFloat);
    return color && n.length >= 4 && n[0] === 0 && n[1] === 0 && n[2] === 0 && n[3] > 0 ? [color] : [];
  });

/** In-page: the geometry and colors of every visible control, text run and line. */
export function collectLayout() {
  const CONTROL = 'button, [role="button"], a[href], select, input:not([type="hidden"]), textarea, [role="tab"], [role="menuitem"], [role="option"]';
  const FLOATING_ROLES = new Set(["dialog", "alertdialog", "menu", "listbox", "tooltip"]);
  // Monaco and SVG graphs lay out their own content; their text and widgets are not audited.
  const SKIP = ".monaco-editor, svg";
  const vw = innerWidth, vh = innerHeight;
  const styles = new Map();
  const cs = (el) => { let s = styles.get(el); if (!s) styles.set(el, (s = getComputedStyle(el))); return s; };
  // Colors of spread-only box-shadow rings ("0 0 0 1px <color>"): solid frames. The same as the exported shadowRings (tested).
  const shadowRings = (v) => (v && v !== "none" ? v.split(/,(?![^(]*\))/) : []).flatMap((part) => {
    const color = part.match(/rgba?\([^)]*\)|#[0-9a-f]{3,8}\b/i)?.[0];
    const n = part.replace(/rgba?\([^)]*\)|#[0-9a-f]{3,8}\b|inset/gi, "").trim().split(/\s+/).map(Number.parseFloat);
    return color && n.length >= 4 && n[0] === 0 && n[1] === 0 && n[2] === 0 && n[3] > 0 ? [color] : [];
  });
  const r = (b) => ({ l: b.left, t: b.top, r: b.right, b: b.bottom });
  const meet = (a, b) => ({ l: Math.max(a.l, b.l), t: Math.max(a.t, b.t), r: Math.min(a.r, b.r), b: Math.min(a.b, b.b) });
  const area = (a) => Math.max(0, a.r - a.l) * Math.max(0, a.b - a.t);
  const desc = (el) => {
    const label = el.getAttribute("aria-label") || el.getAttribute("title") || "";
    const text = (el.innerText ?? el.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 40);
    const tid = el.getAttribute("data-testid");
    const cls = typeof el.className === "string" ? el.className.split(/\s+/).filter(Boolean).slice(0, 4).join(".") : "";
    return `${el.tagName.toLowerCase()}${tid ? `[${tid}]` : ""}${cls ? `.${cls}` : ""}${text ? ` "${text}"` : ""}${label && label !== text ? ` (${label})` : ""}`;
  };
  // Layer: the nearest fixed, high z-index absolute, or popup-role ancestor; elements only conflict within one layer.
  const layers = new Map();
  const layerOf = (el) => {
    if (!el || el === document.documentElement) return 0;
    if (layers.has(el)) return layers.get(el);
    const s = cs(el);
    const z = Number.parseInt(s.zIndex, 10);
    // `data-layout-layer`: an overlay by design (a sticky header covers the rows scrolled under it).
    const floating = s.position === "fixed" || (s.position === "absolute" && z >= 20) || FLOATING_ROLES.has(el.getAttribute("role") ?? "") || el.hasAttribute("data-layout-layer");
    const id = floating ? layers.size + 1 : layerOf(el.parentElement);
    layers.set(el, id);
    return id;
  };
  const opacity = (el) => { let o = 1; for (let e = el; e; e = e.parentElement) o *= Number(cs(e).opacity); return o; };
  const visible = (el) => { const s = cs(el); return s.display !== "none" && s.visibility === "visible" && opacity(el) > 0.05; };
  /** The rect left after clipping by the node's overflow ancestors and the viewport, and the first ancestor that cut it
   *  (for a text node, its own element counts: a `truncate` span clips the text it holds). */
  const clipOf = (el, rect) => {
    let vis = meet(rect, { l: 0, t: 0, r: vw, b: vh }), by = null;
    for (let e = el.parentElement; e && e !== document.documentElement; e = e.parentElement) {
      const s = cs(e);
      if (s.overflowX === "visible" && s.overflowY === "visible") continue;
      const next = meet(vis, r(e.getBoundingClientRect()));
      if (!by && area(next) < area(vis) - 1) {
        // `data-layout-scroller`: scrolled by script behind overflow:hidden (Dockview's tab strip, whose cut-off tabs
        // the strip's end button lists and its edge fades show); it scrolls like an overflow:auto box.
        const byScript = e.hasAttribute("data-layout-scroller");
        const scrollable = ((byScript || /(auto|scroll)/.test(s.overflowY)) && e.scrollHeight > e.clientHeight + 1) || ((byScript || /(auto|scroll)/.test(s.overflowX)) && e.scrollWidth > e.clientWidth + 1);
        by = { desc: desc(e), scrollable };
      }
      vis = next;
    }
    return { vis, by };
  };
  const all = [...document.querySelectorAll("body *")].filter((el) => !el.closest(SKIP));
  const controls = all.filter((el) => el.matches(CONTROL) && visible(el));
  const index = new Map(controls.map((el, i) => [el, i]));
  const controlsAround = (node) => { const out = []; for (let e = node.parentElement; e; e = e.parentElement) if (index.has(e)) out.push(index.get(e)); return out; };
  const bgStack = (el) => {
    const out = [];
    for (let e = el; e; e = e.parentElement) {
      const s = cs(e);
      out.push({ color: s.backgroundColor, image: s.backgroundImage !== "none", opacity: Number(s.opacity) });
    }
    return out;
  };
  const textRuns = (root, own) => {
    const out = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      if (!n.textContent.trim() || !n.parentElement || n.parentElement.closest(SKIP) || n.parentElement.closest('[data-contrast="logotype"]') || !visible(n.parentElement)) continue;
      const around = controlsAround(n);
      if (own !== undefined && around[0] !== own) continue; // a nested control's text belongs to that control
      const range = document.createRange();
      range.selectNodeContents(n);
      const rects = [...range.getClientRects()].map(r).filter((x) => area(x) > 0);
      if (!rects.length) continue;
      const p = n.parentElement, s = cs(p);
      let ellipsis = false;
      for (let e = p; e && (own === undefined || e !== controls[own].parentElement); e = e.parentElement) if (cs(e).textOverflow === "ellipsis") { ellipsis = true; break; }
      out.push({ rects, vis: rects.map((x) => clipOf(n, x).vis).filter((x) => area(x) > 0), layer: layerOf(p), inControls: around, ellipsis,
        desc: `${desc(p)}`, text: n.textContent.trim().slice(0, 40), color: s.color, fontSize: Number.parseFloat(s.fontSize), fontWeight: Number(s.fontWeight) || 400, bgs: bgStack(p) });
    }
    return out;
  };
  const outControls = controls.map((el, i) => {
    const rect = r(el.getBoundingClientRect());
    const { vis, by } = clipOf(el, rect);
    const hits = [];
    if (area(vis) > 0) {
      const ys = [(vis.t + vis.b) / 2];
      for (const x of [vis.l + (vis.r - vis.l) * 0.2, (vis.l + vis.r) / 2, vis.r - (vis.r - vis.l) * 0.2])
        for (const y of ys) {
          const hit = document.elementFromPoint(x, y);
          if (!hit || hit === el || el.contains(hit) || hit.contains(el)) continue;
          hits.push({ desc: desc(hit), layer: layerOf(hit) });
        }
    }
    const disabled = el.matches(":disabled") || el.getAttribute("aria-disabled") === "true";
    return { i, desc: desc(el), layer: layerOf(el), rect, vis, clippedBy: by, hits, disabled, inside: controlsAround(el), texts: textRuns(el, i) };
  });
  // Lines: visible border sides and hairline elements (separators), each with the controls it belongs to.
  const lines = [];
  for (const el of all) {
    if (!visible(el)) continue;
    const s = cs(el);
    const b = r(el.getBoundingClientRect());
    if (area(meet(b, { l: 0, t: 0, r: vw, b: vh })) === 0 && b.r - b.l > 0) continue;
    const related = [...controlsAround(el), ...(index.has(el) ? [index.get(el)] : [])];
    const layer = layerOf(el);
    const sides = [
      ["top", s.borderTopWidth, s.borderTopStyle, s.borderTopColor, { l: b.l, t: b.t, r: b.r, b: b.t + Number.parseFloat(s.borderTopWidth) }],
      ["bottom", s.borderBottomWidth, s.borderBottomStyle, s.borderBottomColor, { l: b.l, t: b.b - Number.parseFloat(s.borderBottomWidth), r: b.r, b: b.b }],
      ["left", s.borderLeftWidth, s.borderLeftStyle, s.borderLeftColor, { l: b.l, t: b.t, r: b.l + Number.parseFloat(s.borderLeftWidth), b: b.b }],
      ["right", s.borderRightWidth, s.borderRightStyle, s.borderRightColor, { l: b.r - Number.parseFloat(s.borderRightWidth), t: b.t, r: b.r, b: b.b }],
    ];
    for (const [side, w, style, color, seg] of sides) {
      if (Number.parseFloat(w) > 0 && style !== "none" && style !== "hidden") lines.push({ desc: `${desc(el)} border-${side}`, layer, color, seg: clipOf(el, seg).vis, related });
    }
    const thin = Math.min(b.r - b.l, b.b - b.t);
    if (thin > 0 && thin <= 2 && Math.max(b.r - b.l, b.b - b.t) >= 6) lines.push({ desc: desc(el), layer, color: s.backgroundColor, seg: clipOf(el, b).vis, related });
  }
  const texts = textRuns(document.body, undefined).filter((t) => t.inControls.length === 0);
  // Popups (a floating role, out of flow) and the ancestor that cuts them: a popup drawn inside a box that clips (the
  // results tool row, 28 px and overflow hidden) shows a sliver or nothing, its controls never reach the checks above.
  const popups = all.filter((el) => FLOATING_ROLES.has(el.getAttribute("role") ?? "") && /^(absolute|fixed)$/.test(cs(el).position) && visible(el)).map((el) => {
    const rect = r(el.getBoundingClientRect());
    const { vis, by } = clipOf(el, rect);
    return { desc: desc(el), screen: meet(rect, { l: 0, t: 0, r: vw, b: vh }), vis, clippedBy: by?.desc ?? null };
  });
  // Fields: the field, or one of the two wrappers that may draw its frame, must stand out from its backdrop (1.4.11).
  const FIELD = 'input:not([type="hidden"]):not([type="checkbox"]):not([type="radio"]):not([type="range"]):not([type="file"]), textarea, select';
  const frameOf = (el) => {
    const s = cs(el);
    const borders = ["Top", "Right", "Bottom", "Left"].filter((k) => Number.parseFloat(s[`border${k}Width`]) > 0 && !["none", "hidden"].includes(s[`border${k}Style`])).map((k) => s[`border${k}Color`]);
    // A spread-only shadow (0 0 0 1px) draws a solid ring, as a border would (the grid's cell editor frames itself so).
    borders.push(...shadowRings(s.boxShadow));
    return { bg: s.backgroundColor, borders, backdrop: el.parentElement ? bgStack(el.parentElement) : [] };
  };
  const fields = controls.filter((el) => el.matches(FIELD)).map((el) => {
    const chain = [];
    // The field and two wrappers; farther up only a box that hugs the field (each edge within 16 px), such as the grid's
    // cell-editor bubble — never a panel that merely contains it.
    const fb = r(el.getBoundingClientRect());
    const hugs = (e) => { const b = r(e.getBoundingClientRect()); return b.l <= fb.l && b.t <= fb.t && b.r >= fb.r && b.b >= fb.b && fb.l - b.l <= 16 && fb.t - b.t <= 16 && b.r - fb.r <= 16 && b.b - fb.b <= 16; };
    for (let e = el, n = 0; e && n < 6 && e !== document.body; e = e.parentElement, n++) if (n < 3 || hugs(e)) chain.push(frameOf(e));
    return { desc: desc(el), vis: clipOf(el, r(el.getBoundingClientRect())).vis, disabled: el.matches(":disabled") || el.getAttribute("aria-disabled") === "true" || !!el.closest('[aria-disabled="true"]'), chain };
  });
  // Icons drawn in currentColor (the app's icon family) up to 32 px: 3:1 on their backdrop. Brand marks with their own
  // colors, charts and diagrams are not icons here.
  const icons = [];
  for (const svg of document.querySelectorAll("svg")) {
    if (svg.closest(".monaco-editor") || svg.parentElement?.closest("svg") || !visible(svg)) continue;
    const b = r(svg.getBoundingClientRect());
    if (area(b) === 0 || b.r - b.l > 32 || b.b - b.t > 32) continue;
    const painted = svg.getAttribute("stroke") === "currentColor" || svg.getAttribute("fill") === "currentColor" || !!svg.querySelector('[stroke="currentColor"], [fill="currentColor"]');
    if (!painted) continue;
    const vis = clipOf(svg, b).vis;
    if (area(vis) === 0) continue;
    icons.push({ desc: desc(svg.parentElement), vis, color: cs(svg).color, bgs: bgStack(svg.parentElement), disabled: !!svg.closest('[aria-disabled="true"], :disabled') });
  }
  const root = getComputedStyle(document.documentElement);
  // Faint (disabled, placeholder, decorative) text and separator glyphs in the line color are not readable-text claims.
  return { viewport: { w: vw, h: vh }, controls: outControls, texts, lines, fields, icons, popups, exemptColors: ["--q-faint", "--q-line"].map((v) => root.getPropertyValue(v).trim()) };
}

// ---- Node side.

/** CSS color → [r, g, b, a] in sRGB 0–255 (rgb/rgba, #hex, color(srgb), oklab, oklch, transparent). */
export function parseColor(value) {
  const v = String(value).trim().toLowerCase();
  if (v === "transparent" || v === "") return [0, 0, 0, 0];
  const hex = /^#([0-9a-f]{3,8})$/.exec(v);
  if (hex) {
    let h = hex[1];
    if (h.length <= 4) h = [...h].map((x) => x + x).join("");
    const n = (i) => parseInt(h.slice(i, i + 2), 16);
    return [n(0), n(2), n(4), h.length === 8 ? n(6) / 255 : 1];
  }
  const fn = /^([a-z-]+)\((.*)\)$/.exec(v);
  if (!fn) throw new Error(`unsupported color: ${value}`);
  const [main, alphaPart] = fn[2].split("/");
  const parts = main.replace(/,/g, " ").trim().split(/\s+/);
  const num = (p, scale = 1) => (p.endsWith("%") ? (Number.parseFloat(p) / 100) * scale : Number.parseFloat(p));
  let alpha = alphaPart !== undefined ? num(alphaPart.trim(), 1) : 1;
  if (fn[1] === "rgb" || fn[1] === "rgba") {
    if (parts.length === 4) alpha = num(parts[3], 1);
    return [num(parts[0], 255), num(parts[1], 255), num(parts[2], 255), alpha];
  }
  const enc = (x) => 255 * Math.min(1, Math.max(0, x <= 0.0031308 ? 12.92 * x : 1.055 * x ** (1 / 2.4) - 0.055));
  if (fn[1] === "color" && parts[0] === "srgb") return [num(parts[1], 1) * 255, num(parts[2], 1) * 255, num(parts[3], 1) * 255, alpha];
  if (fn[1] === "oklab" || fn[1] === "oklch") {
    const L = num(parts[0], 1);
    let a, b;
    if (fn[1] === "oklab") [a, b] = [num(parts[1], 0.4), num(parts[2], 0.4)];
    else {
      const c = num(parts[1], 0.4), h = (Number.parseFloat(parts[2]) * Math.PI) / 180;
      [a, b] = [c * Math.cos(h), c * Math.sin(h)];
    }
    const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3, m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3, s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3;
    return [enc(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s), enc(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s), enc(-0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s), alpha];
  }
  throw new Error(`unsupported color: ${value}`);
}

const lin = (x) => ((x /= 255) <= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4);
const luminance = (c) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
export const contrast = (a, b) => { const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
const over = (top, bottom) => [0, 1, 2].map((i) => top[i] * top[3] + bottom[i] * (1 - top[3])).concat(1);

/** The opaque color an ancestor stack paints, and the opacity of the elements in it; null over an image. */
function backdropOf(bgs) {
  let backdrop = null, alpha = 1;
  const stack = [];
  for (const b of bgs) {
    if (b.image) return null;
    alpha *= b.opacity;
    const c = parseColor(b.color);
    stack.push(c);
    if (c[3] >= 1) { backdrop = c; break; }
  }
  if (!backdrop) backdrop = [255, 255, 255, 1];
  for (let i = stack.length - 2; i >= 0; i--) backdrop = over(stack[i], backdrop);
  return { color: backdrop, alpha };
}

/** Text color over the backdrop its ancestors paint, with the opacity of the elements between; null when unknown (images). */
export function effectiveContrast(run) {
  const b = backdropOf(run.bgs);
  if (!b) return null;
  const fg = parseColor(run.color);
  return contrast(over([fg[0], fg[1], fg[2], fg[3] * b.alpha], b.color), b.color);
}

/** Whether a field stands out at 3:1: its own fill or a border of it, or of one of the wrappers that frame it. */
export function fieldBoundaryOk(field) {
  return field.chain.some((link) => {
    const b = backdropOf(link.backdrop);
    if (!b) return true; // over an image: unknown, not a finding
    const fill = over(parseColor(link.bg), b.color);
    if (contrast(fill, b.color) >= 3) return true;
    return link.borders.some((c) => contrast(over(parseColor(c), fill), fill) >= 3 || contrast(over(parseColor(c), b.color), b.color) >= 3);
  });
}

const meet = (a, b) => ({ l: Math.max(a.l, b.l), t: Math.max(a.t, b.t), r: Math.min(a.r, b.r), b: Math.min(a.b, b.b) });
const w = (a) => Math.max(0, a.r - a.l), h = (a) => Math.max(0, a.b - a.t);
const area = (a) => w(a) * h(a);
/** WCAG AA: 3:1 for large text (24 px, or 18.66 px bold) and for an icon glyph (no letter or digit: non-text contrast),
 *  4.5:1 otherwise. */
export const minContrast = (run) =>
  run.fontSize >= 24 || (run.fontSize >= 18.66 && run.fontWeight >= 700) || (run.text !== undefined && !/[\p{L}\p{N}]/u.test(run.text)) ? 3 : 4.5;

/** Issues in one snapshot: { kind, control, other?, detail?, at } (`at`: where on screen, for the marked screenshot). */
export function findLayoutIssues(snap) {
  const issues = [];
  const add = (kind, c, other, detail, at = c.vis) => issues.push({ kind, control: c.desc, ...(other ? { other } : {}), ...(detail ? { detail } : {}), at });
  const shown = snap.controls.filter((c) => area(c.vis) > 0);
  const exempt = new Set((snap.exemptColors ?? []).filter(Boolean).map((c) => parseColor(c).slice(0, 3).map(Math.round).join(",")));
  for (const c of shown) {
    const hit = c.hits.find((x) => x.layer === c.layer);
    if (hit) add("covered", c, hit.desc);
    if (c.clippedBy && !c.clippedBy.scrollable && area(c.vis) < area(c.rect) * 0.98) add("clipped", c, c.clippedBy.desc, `${Math.round((100 * area(c.vis)) / area(c.rect))}% visible`);
    for (const t of c.texts) {
      const out = t.rects.some((x) => x.l < c.rect.l - 1 || x.r > c.rect.r + 1 || x.t < c.rect.t - 1 || x.b > c.rect.b + 1);
      if (out && !t.ellipsis) add("text-spill", c, null, `"${t.desc}" runs past the control edge`);
      // A label broken onto a second line inside its control ("Dismiss (5 min)" squeezed by its row).
      const lines = t.rects.map((x) => x.t).sort((a, b) => a - b).filter((y, k, all) => k === 0 || y - all[k - 1] > 2).length;
      if (lines > 1 && !t.ellipsis) add("label-wrap", c, null, `"${t.desc}" wraps onto ${lines} lines`);
      if (c.disabled || t.vis.length === 0) continue;
      if (exempt.has(parseColor(t.color).slice(0, 3).map(Math.round).join(","))) continue;
      const ratio = effectiveContrast(t);
      if (ratio !== null && ratio < minContrast(t)) add("contrast", c, null, `${ratio.toFixed(2)}:1 (needs ${minContrast(t)})`);
    }
  }
  // Text outside controls (list rows, hints, headers) is read on its backdrop too.
  for (const t of snap.texts) {
    if (t.vis.length === 0 || exempt.has(parseColor(t.color).slice(0, 3).map(Math.round).join(","))) continue;
    const ratio = effectiveContrast(t);
    if (ratio !== null && ratio < minContrast(t)) add("contrast", { desc: t.desc, vis: t.vis[0] }, null, `${ratio.toFixed(2)}:1 (needs ${minContrast(t)})`);
  }
  for (let a = 0; a < shown.length; a++)
    for (let b = a + 1; b < shown.length; b++) {
      const x = shown[a], y = shown[b];
      if (x.layer !== y.layer || x.inside.includes(y.i) || y.inside.includes(x.i)) continue;
      const m = meet(x.vis, y.vis);
      if (w(m) >= 2 && h(m) >= 2) add("overlap", x, y.desc, `${Math.round(w(m))}×${Math.round(h(m))} px`, m);
    }
  for (const t of snap.texts)
    for (const c of shown) {
      if (t.layer !== c.layer || t.inControls.includes(c.i)) continue;
      const m = t.vis.map((x) => meet(x, c.vis)).find((x) => w(x) >= 2 && h(x) >= 2);
      if (m) add("text-overlap", c, t.desc, null, m);
    }
  for (const f of snap.fields ?? []) {
    if (f.disabled || area(f.vis) === 0) continue;
    if (!fieldBoundaryOk(f)) add("boundary", { desc: f.desc, vis: f.vis }, null, "the field's edge is under 3:1 on its backdrop");
  }
  for (const ic of snap.icons ?? []) {
    if (ic.disabled || exempt.has(parseColor(ic.color).slice(0, 3).map(Math.round).join(","))) continue;
    const ratio = effectiveContrast(ic);
    if (ratio !== null && ratio < 3) add("icon-contrast", { desc: ic.desc, vis: ic.vis }, null, `${ratio.toFixed(2)}:1 (needs 3)`);
  }
  // Marked where the popup should be: what is cut away is the finding.
  for (const p of snap.popups ?? []) {
    if (p.clippedBy && area(p.screen) > 0 && area(p.vis) < area(p.screen) * 0.98)
      add("popup-clipped", p, p.clippedBy, `${Math.round((100 * area(p.vis)) / area(p.screen))}% visible`, p.screen);
  }
  for (const l of snap.lines) {
    if (area(l.seg) === 0 || parseColor(l.color)[3] === 0) continue;
    for (const c of shown) {
      // A line of another control (its border, or inside it) is reported as that pair's overlap.
      if (l.layer !== c.layer || l.related.includes(c.i) || l.related.some((r) => !c.inside.includes(r))) continue;
      const v = c.vis, s = l.seg;
      // A line through the control's interior; one along or just outside its edge is not.
      const horizontal = w(s) >= h(s);
      const through = horizontal ? s.t < v.b - 1 && s.b > v.t + 1 && w(meet(s, v)) >= 4 : s.l < v.r - 1 && s.r > v.l + 1 && h(meet(s, v)) >= 4;
      if (through) add("line-through", c, l.desc, null, meet(s, v));
    }
  }
  return issues;
}

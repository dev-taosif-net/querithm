// Monaco bootstrap (§12): editor core + contributions, SQL languages only, workers bundled via Vite ?worker.
import "monaco-editor/esm/vs/editor/edcore.main";
import * as monaco from "monaco-editor/esm/vs/editor/editor.api";
import EditorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import { LANGUAGES } from "./languages";
import { DIALECT_LIST, DIALECTS } from "../engines";
import { rulesFor, SUGGEST_COLORS } from "./syntaxTheme";
import { resolveDocumentAppTheme, resolveSyntax } from "../theme/resolve";
import { paletteChanged, semanticWanted, setSemanticWanted, WARN_MARK_FALLBACK } from "./themeState";
import { ignoreMonacoCancellations } from "./cancellation";

export { WARN_MARK_FALLBACK };

declare global {
  interface Window {
    MonacoEnvironment?: monaco.Environment;
  }
}

self.MonacoEnvironment = { getWorker: () => new EditorWorker() };

let initialized = false;

export function initMonaco() {
  if (initialized) return monaco;
  initialized = true;
  ignoreMonacoCancellations();
  for (const d of DIALECT_LIST) {
    const id = DIALECTS[d].language;
    const l = LANGUAGES[d];
    monaco.languages.register({ id, aliases: l.aliases, extensions: l.extensions });
  }
  for (const d of DIALECT_LIST) monaco.languages.setMonarchTokensProvider(DIALECTS[d].language, LANGUAGES[d].tokens());
  for (const d of DIALECT_LIST) monaco.languages.setLanguageConfiguration(DIALECTS[d].language, LANGUAGES[d].configuration);
  applyMonacoTheme();
  remeasureOnFontLoad(document.fonts);
  return monaco;
}

/**
 * Monaco caches glyph widths when the first editor is created. The bundled webfont loads lazily, so on Windows that
 * first measure uses the Cascadia Code fallback and selections/cursor end up offset from the JetBrains Mono glyphs
 * (a selection stops short of the last character). Re-measure whenever a font finishes loading.
 */
export function remeasureOnFontLoad(fonts: FontFaceSet | undefined) {
  if (!fonts) return;
  fonts.addEventListener("loadingdone", () => monaco.editor.remeasureFonts());
  fonts
    .load('14px "JetBrains Mono Variable"')
    .then(() => monaco.editor.remeasureFonts())
    .catch(() => {});
}

function cssVar(name: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v.startsWith("#") ? v.slice(1) : v;
}

function hex(name: string): string {
  const v = cssVar(name);
  if (/^[0-9a-f]{3}$/i.test(v)) return `#${v[0]}${v[0]}${v[1]}${v[1]}${v[2]}${v[2]}`;
  if (/^[0-9a-f]{4}$/i.test(v)) return `#${v[0]}${v[0]}${v[1]}${v[1]}${v[2]}${v[2]}${v[3]}${v[3]}`;
  if (/^[0-9a-f]{6}([0-9a-f]{2})?$/i.test(v)) return `#${v}`;
  // rgba(...) → keep as is for colors: Monaco accepts #RRGGBBAA only; approximate translucent selection.
  const m = /rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)(?:[\s,/]+([\d.]+))?\s*\)/.exec(v);
  if (m) {
    const a = Math.round(Number(m[4] ?? 1) * 255);
    return `#${[m[1], m[2], m[3]].map((x) => Number(x).toString(16).padStart(2, "0")).join("")}${a.toString(16).padStart(2, "0")}`;
  }
  return "#888888";
}

const warnMark = (dark: boolean) => (cssVar("--q-warn-mark") ? hex("--q-warn-mark") : WARN_MARK_FALLBACK[dark ? "dark" : "light"]);
/** A token's color as #rrggbb plus a two-digit alpha. */
const tint = (name: string, alpha: string) => `${hex(name).slice(0, 7)}${alpha}`;

/**
 * The Monaco theme (§12 Theme): editor chrome from the same CSS variables as the UI, token colors from the syntax
 * theme named by `data-syntax` (Settings → Syntax colors) in the mode named by `data-theme`.
 */
let lastPaletteKey = "";
/** The theme definition last given to Monaco (JSON), to skip an identical one. */
let lastDefinition = "";

/**
 * The Monaco theme (plans/app-themes §4.3): chrome from the CSS variables of the App Theme on the document; token rules
 * from Syntax colors (data-syntax: "default" = the App Theme's own palette, else one of the six, unchanged) in the
 * theme's mode; comments upright when data-comments="upright". Returns whether semantic coloring was switched on or off.
 */
export function applyMonacoTheme(): boolean {
  const root = document.documentElement;
  const app = resolveDocumentAppTheme(root);
  const dark = app.mode === "dark";
  const syn = resolveSyntax(root.dataset.syntax, app, root.dataset.comments === "upright");
  const palette = syn.palette;
  const definition: monaco.editor.IStandaloneThemeData = {
    base: dark ? "vs-dark" : "vs",
    inherit: true,
    rules: rulesFor(syn.palette, syn.fontStyle, hex("--q-fg")),
    colors: {
      "editor.background": hex("--q-bg"),
      "editor.foreground": hex("--q-fg"),
      // Line numbers are read (errors and usages cite them): the muted tier, not the decorative one.
      "editorLineNumber.foreground": hex("--q-muted"),
      "editorLineNumber.activeForeground": hex("--q-fg"),
      "editor.lineHighlightBackground": hex("--q-panel"),
      "editor.selectionBackground": hex("--q-sel"),
      // A selection keeps its fill when Run takes the focus (Monaco's default pales it), so what Run runs stays plain.
      "editor.inactiveSelectionBackground": hex("--q-sel"),
      // Subtle snippet tabstop tint; hide the final-tabstop border (Monaco's default bar looks like a stray cursor).
      "editor.snippetTabstopHighlightBackground": `${hex("--q-accent").slice(0, 7)}14`,
      "editor.snippetTabstopHighlightBorder": `${hex("--q-accent").slice(0, 7)}40`,
      "editor.snippetFinalTabstopHighlightBackground": "#00000000",
      "editor.snippetFinalTabstopHighlightBorder": "#00000000",
      "editorCursor.foreground": hex("--q-fg"),
      "editorWidget.background": hex("--q-raised"),
      "editorWidget.border": hex("--q-line"),
      "editorWidget.foreground": hex("--q-fg"),
      ...Object.fromEntries(Object.entries(SUGGEST_COLORS).map(([key, v]) => [key, hex(v)])),
      "editorHoverWidget.background": hex("--q-raised"),
      "editorHoverWidget.foreground": hex("--q-fg"),
      "editorHoverWidget.border": hex("--q-line"),
      "editorGutter.background": hex("--q-bg"),
      "minimap.background": hex("--q-bg"),
      "scrollbarSlider.background": `${hex("--q-hover").slice(0, 7)}aa`,
      // Monaco's own diff tints drop syntax colors below 4.5:1; the line and its changed text stack the same tint.
      "diffEditor.insertedLineBackground": hex("--q-diff-insert"),
      "diffEditor.insertedTextBackground": hex("--q-diff-insert"),
      "diffEditor.removedLineBackground": hex("--q-diff-remove"),
      "diffEditor.removedTextBackground": hex("--q-diff-remove"),
      "focusBorder": hex("--q-accent"),
      // Find (plans/app-themes §4.3, DA2 R1): Monaco's opaque default dropped tokens to 2.3–2.7:1. A warn tint at 10 % keeps
      // every token at 4.5:1 (comments 4.0) and is never read as the selection or the accent; the current match gets a
      // 2 px warn border, the others 1 px at 60 %.
      "editor.findMatchBackground": tint("--q-warn", "1a"),
      "editor.findMatchBorder": hex("--q-warn"),
      "editor.findMatchHighlightBackground": tint("--q-warn", "1a"),
      "editor.findMatchHighlightBorder": tint("--q-warn", "99"),
      "editorOverviewRuler.findMatchForeground": tint("--q-warn", "99"),
      // Squiggles and their ruler marks in the theme's colors (DA2 R2). Warnings use their own amber mark, never --q-warn:
      // errors and warnings share the wavy shape, and on Querithm Light --q-warn is ΔE 1.3 from the error red for
      // deuteranopes (§4.3 D16).
      "editorError.foreground": hex("--q-err"),
      "editorWarning.foreground": warnMark(dark),
      "editorInfo.foreground": cssVar("--q-info") ? hex("--q-info") : hex("--q-accent"),
      "editorOverviewRuler.errorForeground": hex("--q-err"),
      "editorOverviewRuler.warningForeground": warnMark(dark),
      "editorOverviewRuler.infoForeground": cssVar("--q-info") ? hex("--q-info") : hex("--q-accent"),
      // Word highlight (fg 9 % + a line-strong edge) and bracket match (an edge, no fill): Monaco's greys dropped schema
      // prefixes to 4.06:1 and were 1.6–2.0:1 on light themes (DA2 V4, V5).
      "editor.wordHighlightBackground": tint("--q-fg", "17"),
      "editor.wordHighlightStrongBackground": tint("--q-fg", "17"),
      "editor.wordHighlightTextBackground": tint("--q-fg", "17"),
      "editor.wordHighlightTextBorder": hex("--q-line-strong"),
      "editorBracketMatch.border": hex("--q-line-strong"),
      "editorBracketMatch.background": "#00000000",
      "editorIndentGuide.background1": hex("--q-line"),
      "editorIndentGuide.activeBackground1": hex("--q-line-strong"),
      // Only ( ) are colorized (languages.ts); 4–6 are transparent, which Monaco drops, so depth cycles 1–2–3.
      "editorBracketHighlight.foreground1": palette.bracket1,
      "editorBracketHighlight.foreground2": palette.bracket2,
      "editorBracketHighlight.foreground3": palette.bracket3,
      "editorBracketHighlight.foreground4": "#00000000",
      "editorBracketHighlight.foreground5": "#00000000",
      "editorBracketHighlight.foreground6": "#00000000",
      "editorBracketHighlight.unexpectedBracket.foreground": hex("--q-err"),
    },
  };
  // The same theme again (a boot applies it up to three times: initMonaco, the settings effect, after the load) is not
  // defined again: Monaco answers every setTheme with a new color map, which re-tokenizes and re-renders every model.
  // Compared by its definition, not by the theme ids, so a CSS variable that changed under the same ids still applies.
  const text = JSON.stringify(definition);
  const themeSet = text !== lastDefinition;
  if (themeSet) {
    monaco.editor.defineTheme("querithm", definition);
    monaco.editor.setTheme("querithm");
    lastDefinition = text;
  }
  // V7: semantic tokens already drawn hold color ids of the previous color map; drop them when the palette changed. Only
  // together with a setTheme: its color-theme event is what makes Monaco ask for them again. Without one the color map
  // is unchanged, the drawn tokens are still right, and dropping them would leave them blank until a scroll or an edit
  // (no data-comments yet reads as italic: the same theme). A re-apply with the same palette (an OS flip under a fixed
  // theme) drops nothing either.
  const key = `${app.id}|${app.mode}|${syn.choice}|${syn.fontStyle.comment ?? ""}`;
  if (key !== lastPaletteKey) {
    if (lastPaletteKey && themeSet) paletteChanged();
    lastPaletteKey = key;
  }
  const changed = semanticWanted() !== syn.semantic;
  setSemanticWanted(syn.semantic);
  return changed;
}

export { monaco };

// Monaco bootstrap (§12): editor core + contributions, SQL languages only, workers bundled via Vite ?worker.
import "monaco-editor/esm/vs/editor/edcore.main";
import * as monaco from "monaco-editor/esm/vs/editor/editor.api";
import EditorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import { LANGUAGES } from "./languages";
import { DIALECT_LIST, DIALECTS } from "../engines";
import { SUGGEST_COLORS, syntaxRules, syntaxThemeById } from "./syntaxTheme";
import { ignoreMonacoCancellations } from "./cancellation";

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

/**
 * The Monaco theme (§12 Theme): editor chrome from the same CSS variables as the UI, token colors from the syntax
 * theme named by `data-syntax` (Settings → Syntax colors) in the mode named by `data-theme`.
 */
export function applyMonacoTheme() {
  const root = document.documentElement;
  const dark = root.dataset.theme !== "light";
  const syntax = syntaxThemeById(root.dataset.syntax);
  const palette = dark ? syntax.dark : syntax.light;
  monaco.editor.defineTheme("querithm", {
    base: dark ? "vs-dark" : "vs",
    inherit: true,
    rules: syntaxRules(syntax, dark ? "dark" : "light", hex("--q-fg")),
    colors: {
      "editor.background": hex("--q-bg"),
      "editor.foreground": hex("--q-fg"),
      // Line numbers are read (errors and usages cite them): the muted tier, not the decorative one.
      "editorLineNumber.foreground": hex("--q-muted"),
      "editorLineNumber.activeForeground": hex("--q-fg"),
      "editor.lineHighlightBackground": hex("--q-panel"),
      "editor.selectionBackground": hex("--q-sel"),
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
      // Only ( ) are colorized (languages.ts); 4–6 are transparent, which Monaco drops, so depth cycles 1–2–3.
      "editorBracketHighlight.foreground1": palette.bracket1,
      "editorBracketHighlight.foreground2": palette.bracket2,
      "editorBracketHighlight.foreground3": palette.bracket3,
      "editorBracketHighlight.foreground4": "#00000000",
      "editorBracketHighlight.foreground5": "#00000000",
      "editorBracketHighlight.foreground6": "#00000000",
      "editorBracketHighlight.unexpectedBracket.foreground": hex("--q-err"),
    },
  });
  monaco.editor.setTheme("querithm");
}

export { monaco };

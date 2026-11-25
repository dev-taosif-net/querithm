// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("monaco-editor/esm/vs/editor/edcore.main", () => ({}));
vi.mock("monaco-editor/esm/vs/editor/editor.worker?worker", () => ({ default: class FakeWorker {} }));
vi.mock("monaco-editor/esm/vs/editor/editor.api", async () => {
  const { fake } = await import("./fakeMonaco");
  return fake.monaco;
});

const { fake } = await import("./fakeMonaco");
const m = await import("../monaco");
const { SYNTAX_THEMES } = await import("../syntaxTheme");

function theme() {
  const call = fake.monaco.editor.defineTheme.mock.calls.at(-1)!;
  return call[1] as { base: string; colors: Record<string, string>; rules: Array<{ token: string; foreground: string; fontStyle?: string }> };
}

describe("monaco bootstrap", () => {
  beforeEach(() => {
    document.documentElement.removeAttribute("data-theme");
    document.documentElement.removeAttribute("data-syntax");
    document.documentElement.removeAttribute("style");
  });

  it("registers every dialect's SQL language once and defines the theme", () => {
    m.initMonaco();
    m.initMonaco();
    const ids = fake.monaco.languages.register.mock.calls.map((c) => c[0].id);
    expect(ids).toEqual(["tsql", "pgsql", "sqlite", "mysql", "duckdb", "oracle"]);
    expect(fake.monaco.languages.setMonarchTokensProvider).toHaveBeenCalledTimes(6);
    expect(fake.monaco.languages.setLanguageConfiguration).toHaveBeenCalledTimes(6);
    expect(fake.monaco.editor.defineTheme).toHaveBeenCalledTimes(1);
    expect(fake.monaco.editor.setTheme).toHaveBeenCalledWith("querithm");
    expect((self as any).MonacoEnvironment.getWorker()).toBeTruthy();
  });

  it("gives Monaco an identical theme once (each setTheme re-colors every model); a changed variable applies again", () => {
    const s = document.documentElement.style;
    s.setProperty("--q-bg", "#121212");
    m.applyMonacoTheme();
    const defined = fake.monaco.editor.defineTheme.mock.calls.length;
    const set = fake.monaco.editor.setTheme.mock.calls.length;
    m.applyMonacoTheme();
    m.applyMonacoTheme();
    expect(fake.monaco.editor.defineTheme.mock.calls.length).toBe(defined);
    expect(fake.monaco.editor.setTheme.mock.calls.length).toBe(set);
    s.setProperty("--q-bg", "#131313"); // same theme ids, another value: still applied
    m.applyMonacoTheme();
    expect(fake.monaco.editor.defineTheme.mock.calls.length).toBe(defined + 1);
    expect(fake.monaco.editor.setTheme.mock.calls.length).toBe(set + 1);
    expect(theme().colors["editor.background"]).toBe("#131313");
  });

  it("drops drawn semantic tokens only with a theme Monaco re-colors by (V7): the same theme under a new attribute drops none", async () => {
    const { setPaletteChangeHook } = await import("../themeState");
    const dropped = vi.fn();
    setPaletteChangeHook(dropped);
    const root = document.documentElement;
    delete root.dataset.comments;
    m.applyMonacoTheme();
    dropped.mockClear();
    const set = fake.monaco.editor.setTheme.mock.calls.length;
    // The document gets data-comments after the first apply (initMonaco runs first): no attribute already read as
    // italic, so Monaco gets no new theme, asks for no tokens, and the drawn ones must stay.
    root.dataset.comments = "italic";
    m.applyMonacoTheme();
    expect(fake.monaco.editor.setTheme.mock.calls.length).toBe(set);
    expect(dropped).not.toHaveBeenCalled();
    root.dataset.comments = "upright"; // a real change: a new theme, then the drop
    m.applyMonacoTheme();
    expect(fake.monaco.editor.setTheme.mock.calls.length).toBe(set + 1);
    expect(dropped).toHaveBeenCalledTimes(1);
    m.applyMonacoTheme();
    expect(dropped).toHaveBeenCalledTimes(1);
    delete root.dataset.comments;
    setPaletteChangeHook(() => {});
  });

  it("derives theme colors from the CSS variables: hex, rgba and a fallback", () => {
    const s = document.documentElement.style;
    s.setProperty("--q-bg", "#101010");
    s.setProperty("--q-fg", "#FFFFFF");
    s.setProperty("--q-sel", "rgba(255, 0, 128, 0.5)");
    s.setProperty("--q-line", "rgb(1, 2, 3)");
    m.applyMonacoTheme();
    const t = theme();
    expect(t.base).toBe("vs-dark");
    expect(t.colors["editor.background"]).toBe("#101010");
    expect(t.colors["editor.foreground"]).toBe("#FFFFFF");
    expect(t.colors["editor.selectionBackground"]).toBe("#ff008080");
    expect(t.colors["editorWidget.border"]).toBe("#010203ff");
    expect(t.colors["editorGutter.background"]).toBe("#101010");
    expect(t.colors["editorLineNumber.foreground"]).toBe("#888888");
    expect(t.rules.length).toBeGreaterThan(5);
    expect(t.rules.every((r) => /^[0-9a-f]{6}$/i.test(r.foreground))).toBe(true);
  });

  it("diff tints come from the --q-diff-* tokens (Monaco's own drop syntax colors below 4.5:1); line numbers are muted, not faint", () => {
    const s = document.documentElement.style;
    s.setProperty("--q-diff-insert", "rgba(16, 185, 129, 0.045)");
    s.setProperty("--q-diff-remove", "rgba(220, 38, 38, 0.085)");
    s.setProperty("--q-muted", "#8c949e");
    s.setProperty("--q-faint", "#4b5260");
    m.applyMonacoTheme();
    const c = theme().colors;
    expect(c["diffEditor.insertedLineBackground"]).toBe("#10b9810b");
    expect(c["diffEditor.insertedTextBackground"]).toBe("#10b9810b");
    expect(c["diffEditor.removedLineBackground"]).toBe("#dc262616");
    expect(c["diffEditor.removedTextBackground"]).toBe("#dc262616");
    expect(c["editorLineNumber.foreground"]).toBe("#8c949e");
  });

  it("find, squiggles, word highlight and bracket match follow the theme tokens (plans/app-themes §4.3)", () => {
    const s = document.documentElement.style;
    s.setProperty("--q-warn", "#f59e0b");
    s.setProperty("--q-err", "#f97b7b");
    s.setProperty("--q-accent", "#60a5fa");
    s.setProperty("--q-fg", "#e6edf3");
    s.setProperty("--q-line", "#30363d");
    s.setProperty("--q-line-strong", "#6e7681");
    m.applyMonacoTheme();
    let c = theme().colors;
    // Find: a warn wash, never Monaco's opaque grey, a solid border on the current match and 60 % on the others.
    expect(c["editor.findMatchBackground"]).toBe("#f59e0b1a");
    expect(c["editor.findMatchBorder"]).toBe("#f59e0b");
    expect(c["editor.findMatchHighlightBorder"]).toBe("#f59e0b99");
    // Squiggles: error in --q-err; warning in its own amber mark (never --q-warn); info falls back to the accent.
    expect(c["editorError.foreground"]).toBe("#f97b7b");
    expect(c["editorWarning.foreground"]).toBe(m.WARN_MARK_FALLBACK.dark);
    expect(c["editorInfo.foreground"]).toBe("#60a5fa");
    expect(c["editorOverviewRuler.errorForeground"]).toBe("#f97b7b");
    expect(c["editor.wordHighlightBackground"]).toBe("#e6edf317");
    expect(c["editorBracketMatch.border"]).toBe("#6e7681");
    expect(c["editorBracketMatch.background"]).toBe("#00000000");
    // A theme that sets its own mark and info color gets them; light mode falls back to the deeper amber.
    s.setProperty("--q-warn-mark", "#e3cf55");
    s.setProperty("--q-info", "#7ab9e5");
    m.applyMonacoTheme();
    c = theme().colors;
    expect(c["editorWarning.foreground"]).toBe("#e3cf55");
    expect(c["editorInfo.foreground"]).toBe("#7ab9e5");
    s.removeProperty("--q-warn-mark");
    document.documentElement.dataset.theme = "light";
    m.applyMonacoTheme();
    expect(theme().colors["editorWarning.foreground"]).toBe(m.WARN_MARK_FALLBACK.light);
  });

  it("re-measures fonts once the editor webfont loads so selections line up with glyphs", async () => {
    fake.monaco.editor.remeasureFonts.mockClear();
    const listeners: Record<string, () => void> = {};
    const fonts = {
      load: vi.fn(() => Promise.resolve([])),
      addEventListener: vi.fn((type: string, l: () => void) => { listeners[type] = l; }),
    } as unknown as FontFaceSet;
    m.remeasureOnFontLoad(fonts);
    expect(fonts.load).toHaveBeenCalledWith('14px "JetBrains Mono Variable"');
    await Promise.resolve();
    expect(fake.monaco.editor.remeasureFonts).toHaveBeenCalledTimes(1);
    listeners.loadingdone();
    expect(fake.monaco.editor.remeasureFonts).toHaveBeenCalledTimes(2);
    m.remeasureOnFontLoad(undefined);
  });

  it("uses the light base theme when the document is light", () => {
    document.documentElement.dataset.theme = "light";
    m.applyMonacoTheme();
    expect(theme().base).toBe("vs");
  });

  it("handles CSS minifier shorthand (#rgb, #rgba, #rrggbbaa) produced by production builds", () => {
    document.documentElement.dataset.theme = "light";
    const s = document.documentElement.style;
    s.setProperty("--q-bg", "#fff");
    s.setProperty("--q-panel", "#f6f8fa");
    s.setProperty("--q-sel", "#2563eb26");
    s.setProperty("--q-line", "#abcd");
    m.applyMonacoTheme();
    const t = theme();
    expect(t.base).toBe("vs");
    expect(t.colors["editor.background"]).toBe("#ffffff");
    expect(t.colors["editorGutter.background"]).toBe("#ffffff");
    expect(t.colors["minimap.background"]).toBe("#ffffff");
    expect(t.colors["editor.lineHighlightBackground"]).toBe("#f6f8fa");
    expect(t.colors["editor.selectionBackground"]).toBe("#2563eb26");
    // The selection looks the same unfocused (Run takes the focus).
    expect(t.colors["editor.inactiveSelectionBackground"]).toBe("#2563eb26");
    expect(t.colors["editorWidget.border"]).toBe("#aabbccdd");
    // Snippet tabstops use a subtle accent tint and hide the final-tabstop cursor-like bar.
    expect(t.colors["editor.snippetFinalTabstopHighlightBackground"]).toBe("#00000000");
    expect(t.colors["editor.snippetFinalTabstopHighlightBorder"]).toBe("#00000000");
    expect(t.colors["editor.snippetTabstopHighlightBackground"]).toMatch(/^#[0-9a-f]{6}14$/i);
    expect(t.colors["editor.snippetTabstopHighlightBorder"]).toMatch(/^#[0-9a-f]{6}40$/i);
  });

  it("token colors come from the syntax theme named by data-syntax, in the document's mode; brackets cycle its three colors", () => {
    const std = SYNTAX_THEMES.find((t) => t.id === "standard")!;
    document.documentElement.dataset.syntax = "standard";
    document.documentElement.style.setProperty("--q-err", "#f97b7b");
    document.documentElement.style.setProperty("--q-fg", "#e6edf3");
    m.applyMonacoTheme();
    let t = theme();
    const rule = (token: string) => t.rules.find((r) => r.token === token)!;
    expect(rule("keyword").foreground).toBe(std.dark.keyword.slice(1));
    expect(rule("keyword").fontStyle).toBe("bold");
    expect(rule("number.hex").foreground).toBe(std.dark.number.slice(1));
    expect(rule("identifier.temp")).toMatchObject({ foreground: std.dark.ident.slice(1), fontStyle: "italic" });
    expect(rule("delimiter").foreground).toBe("e6edf3");
    expect(t.colors["editorBracketHighlight.foreground1"]).toBe(std.dark.bracket1);
    expect(t.colors["editorBracketHighlight.foreground3"]).toBe(std.dark.bracket3);
    expect(t.colors["editorBracketHighlight.foreground4"]).toBe("#00000000");
    expect(t.colors["editorBracketHighlight.unexpectedBracket.foreground"]).toBe("#f97b7b");
    document.documentElement.dataset.theme = "light";
    m.applyMonacoTheme();
    t = theme();
    expect(t.rules.find((r) => r.token === "keyword")!.foreground).toBe(std.light.keyword.slice(1));
    expect(t.colors["editorBracketHighlight.foreground2"]).toBe(std.light.bracket2);
  });

  it("an unknown syntax theme id (old store.db, hand edit) falls back to Standard", () => {
    document.documentElement.dataset.syntax = "no-such-theme";
    m.applyMonacoTheme();
    expect(theme().rules.find((r) => r.token === "keyword")!.foreground).toBe(SYNTAX_THEMES[0].dark.keyword.slice(1));
  });
});

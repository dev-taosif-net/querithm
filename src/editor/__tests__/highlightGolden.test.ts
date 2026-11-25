// @vitest-environment jsdom
// T1 highlight golden (plans/app-themes §9 T1, invariant I1): what every character of a per-dialect corpus looks like
// under each of the six syntax themes in both modes, through Monaco's real Monarch tokenizer and Monaco's real theme
// service (the one `applyMonacoTheme` drives: base theme + editor.foreground + our rules, inherit: true). The snapshot
// files must stay byte-identical: a lexer or rule change that keeps the look passes, one that changes a colour or a
// font style fails. Update them (`vitest run -u`) only for a visible change the owner has accepted.
//
// jsdom: the theme service needs a window (it writes the token CSS); it only reads matchMedia from it.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { beforeAll, describe, expect, it } from "vitest";
import type * as Monaco from "monaco-editor/esm/vs/editor/editor.api";
// @ts-expect-error untyped internal module of monaco-editor
import { StandaloneThemeService } from "monaco-editor/esm/vs/editor/standalone/browser/standaloneThemeService.js";
// @ts-expect-error untyped internal module of monaco-editor
import { TokenMetadata } from "monaco-editor/esm/vs/editor/common/encodedTokenAttributes.js";
import type { Dialect } from "../../ipc/contract";
import { DIALECT_LIST, DIALECTS } from "../../engines";
import { LANGUAGES } from "../languages";
import { SYNTAX_THEMES, syntaxRules, type SyntaxTheme } from "../syntaxTheme";
import { lines, monarch, tokenLines, type CompiledLexer, type Tokenizer } from "./monarch";

// jsdom gives import.meta.url an http: scheme, so files are read from the repo root (as tabStyle.test.ts does).
const css = readFileSync(join(process.cwd(), "src/theme/tokens.css"), "utf8");
function themeVars(selector: string): Record<string, string> {
  const start = css.indexOf(`${selector} {`);
  const body = css.slice(start, css.indexOf("}", start));
  return Object.fromEntries([...body.matchAll(/(--q-[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
}
const MODES = ["dark", "light"] as const;
type Mode = (typeof MODES)[number];
/** The editor colours `applyMonacoTheme` gives the token theme: --q-fg (the "" rule and `delimiter`) and --q-bg. */
const EDITOR: Record<Mode, { fg: string; bg: string }> = {
  dark: { fg: themeVars(':root[data-theme="dark"]')["--q-fg"], bg: themeVars(':root[data-theme="dark"]')["--q-bg"] },
  light: { fg: themeVars(':root[data-theme="light"]')["--q-fg"], bg: themeVars(':root[data-theme="light"]')["--q-bg"] },
};
const COMBOS = SYNTAX_THEMES.flatMap((t) => MODES.map((mode) => ({ t, mode, name: `${t.id}/${mode}` })));

const corpusFile = (name: string) => readFileSync(join(process.cwd(), "src/editor/__tests__/highlight", name), "utf8");
/** One dialect's corpus: the shared stress seed and the §5.2 forms, then the dialect's own rule-by-rule file. */
function corpus(d: Dialect): [string, string][] {
  return ["stress.sql", "forms.sql", `${DIALECTS[d].language}.sql`].map((f) => [f, corpusFile(f)]);
}

// The real theme service, as in the app: defineTheme("querithm", …) then setTheme("querithm") (monaco.ts).
let service: { defineTheme(n: string, d: unknown): void; setTheme(n: string): void; getColorTheme(): { tokenTheme: TokenTheme } };
interface TokenTheme { getColorMap(): unknown[]; match(languageId: number, token: string): number }
beforeAll(() => {
  window.matchMedia ??= ((query: string) =>
    ({ matches: false, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }) as unknown as MediaQueryList);
  service = new StandaloneThemeService();
});
function applyTheme(t: SyntaxTheme, mode: Mode) {
  const { fg, bg } = EDITOR[mode];
  service.defineTheme("querithm", {
    base: mode === "dark" ? "vs-dark" : "vs",
    inherit: true,
    rules: syntaxRules(t, mode, fg),
    colors: { "editor.foreground": fg, "editor.background": bg },
  });
  service.setTheme("querithm");
}

const fontStyleName = (bits: number) => `${bits & 2 ? "b" : ""}${bits & 1 ? "i" : ""}${bits & 4 ? "u" : ""}${bits & 8 ? "s" : ""}`;
/** Each character's "rrggbb style" under the current theme, from the metadata Monaco renders (tokenizeEncoded). */
function charStyles(tokenizer: Tokenizer, text: string): string[][] {
  const colors = service.getColorTheme().tokenTheme.getColorMap();
  let state = tokenizer.getInitialState();
  return lines(text).map((line) => {
    const r = tokenizer.tokenizeEncoded(line, true, state);
    state = r.endState;
    const out: string[] = new Array(line.length);
    for (let i = 0; i < r.tokens.length; i += 2) {
      const end = i + 2 < r.tokens.length ? r.tokens[i + 2] : line.length;
      const meta = r.tokens[i + 1];
      const style = `${String(colors[TokenMetadata.getForeground(meta)]).slice(1)} ${fontStyleName(TokenMetadata.getFontStyle(meta))}`.trim();
      out.fill(style, r.tokens[i], end);
    }
    return out;
  });
}

const tokenizers = new Map<Dialect, Tokenizer>();
function tokenizer(d: Dialect) {
  if (!tokenizers.has(d)) tokenizers.set(d, monarch(DIALECTS[d].language, LANGUAGES[d].tokens(), service).tokenizer);
  return tokenizers.get(d)!;
}
/** styles[combo][file][line][char]. */
function styleTable(d: Dialect): string[][][][] {
  return COMBOS.map(({ t, mode }) => {
    applyTheme(t, mode);
    return corpus(d).map(([, text]) => charStyles(tokenizer(d), text));
  });
}

const IDS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
/**
 * The golden text: a legend of style vectors (one style per theme and mode), then each corpus line and its runs
 * `start-end id` (end exclusive). Lossless: a character's style under every theme is its run's legend row.
 */
function golden(d: Dialect): string {
  const table = styleTable(d);
  const legend = new Map<string, string>();
  const body: string[] = [];
  corpus(d).forEach(([file, text], f) => {
    body.push("", `== ${file}`);
    lines(text).forEach((line, l) => {
      const runs: string[] = [];
      let start = 0;
      let prev = "";
      for (let c = 0; c <= line.length; c++) {
        const vector = c < line.length ? table.map((combo) => combo[f][l][c]).join("|") : "";
        if (c > 0 && vector !== prev) {
          if (!legend.has(prev)) legend.set(prev, IDS[legend.size]);
          runs.push(`${start}-${c} ${legend.get(prev)}`);
          start = c;
        }
        prev = vector;
      }
      const n = String(l + 1).padStart(4);
      body.push(`${n} | ${line}`.trimEnd(), `${" ".repeat(4)} | ${runs.join("  ")}`.trimEnd());
    });
  });
  expect(legend.size, "more styles than legend ids").toBeLessThanOrEqual(IDS.length);
  const widths = COMBOS.map((c) => c.name.length);
  const row = (cells: string[]) => cells.map((c, i) => c.padEnd(widths[i])).join("  ").trimEnd();
  return [
    `# T1 highlight golden, ${DIALECTS[d].language} (plans/app-themes §9 T1, I1). Written by highlightGolden.test.ts:`,
    "# never update it to make a change pass. Each style is the foreground and font style (b bold, i italic,",
    "# u underline, s strikethrough) Monaco resolves for a character, under each syntax theme and mode.",
    `id  ${row(COMBOS.map((c) => c.name))}`,
    ...[...legend].map(([vector, id]) => `${id}   ${row(vector.split("|"))}`),
    ...body,
    "",
  ].join("\n");
}

describe("T1 highlight golden: every character keeps its colour and font style under the six syntax themes", () => {
  it("the editor colours come from tokens.css as #rrggbb, as applyMonacoTheme passes them", () => {
    for (const m of MODES) {
      expect(EDITOR[m].fg).toMatch(/^#[0-9a-f]{6}$/i);
      expect(EDITOR[m].bg).toMatch(/^#[0-9a-f]{6}$/i);
    }
  });

  it.each(DIALECT_LIST)("%s matches the committed golden", async (d) => {
    await expect(golden(d)).toMatchFileSnapshot(`./__snapshots__/highlightGolden.${DIALECTS[d].language}.txt`);
  });

  it("the styles are Monaco's resolution of our rules over the base theme (spot checks)", () => {
    const at = (d: Dialect, t: SyntaxTheme, mode: Mode, text: string, col: number) => {
      applyTheme(t, mode);
      return charStyles(tokenizer(d), text)[0][col];
    };
    const [standard, colorBlind] = SYNTAX_THEMES;
    expect(at("tsql", standard, "dark", "SELECT 1", 0)).toBe("a5aaff b");
    expect(at("tsql", standard, "light", "-- note", 3)).toBe("5e636d i");
    expect(at("pg", colorBlind, "dark", "SELECT $1", 7)).toBe("d09ec7 i");
    expect(at("sqlite", standard, "dark", "SELECT 0x1F", 8)).toBe("ec9563");
    // A character no rule names takes the editor foreground: Monaco's "" rule from editor.foreground.
    expect(at("mySql", standard, "dark", "SELECT ?", 7)).toBe(EDITOR.dark.fg.slice(1).toLowerCase());
    expect(at("mySql", standard, "light", "SELECT ?", 7)).toBe(EDITOR.light.fg.slice(1).toLowerCase());
  });
});

/** Every state the lexer can be in: its start state and every `next` target (a `name.$n` state reads its base). */
function enteredStates(def: Monaco.languages.IMonarchLanguage, start: string): Set<string> {
  const out = new Set([start]);
  const walk = (a: unknown): void => {
    if (Array.isArray(a)) a.forEach(walk);
    else if (a && typeof a === "object") {
      const o = a as { next?: string; cases?: Record<string, unknown> };
      if (o.next && !["@push", "@pop", "@popall"].includes(o.next)) out.add(o.next.replace(/^@/, "").split(".")[0]);
      if (o.cases) Object.values(o.cases).forEach(walk);
    }
  };
  for (const rules of Object.values(def.tokenizer)) for (const r of rules) walk(Array.isArray(r) ? r.slice(1) : r);
  return out;
}
/** A state's source rules with `include`s expanded in place, as monarchCompile.js flattens them. */
function flatRules(def: Monaco.languages.IMonarchLanguage, state: string): unknown[] {
  return def.tokenizer[state].flatMap((r) => ("include" in (r as object) ? flatRules(def, (r as { include: string }).include.replace(/^@/, "")) : [r]));
}
const sourceAction = (r: unknown) => (Array.isArray(r) ? r[1] : (r as { action?: unknown }).action);

/**
 * Rules no input can fire, with the reason. None today. Until 2026-11-24 MySQL's variable rule was one: Monarch reads
 * `@@` in a regex as an escaped `@` (monarchCompile.js compileRegExp), so the system-variable rule written `/@@[\w$.]+/`
 * was `/@[\w$.]+/` and took every `@x` first; it is now written `/@@@@[\w$.]+/`.
 */
const UNREACHABLE: Partial<Record<Dialect, string[]>> = {};

describe("T1 corpus: every Monarch rule and every case fires", () => {
  it.each(DIALECT_LIST)("%s", (d) => {
    const def = LANGUAGES[d].tokens();
    const { lexer, tokenizer: tk } = monarch(DIALECTS[d].language, def) as { lexer: CompiledLexer; tokenizer: Tokenizer };
    const hits = new Map<string, { hit: boolean; outcomes: Set<unknown>; cases: number }>();
    for (const state of enteredStates(def, lexer.start)) {
      const source = flatRules(def, state);
      const compiled = lexer.tokenizer[state];
      expect(compiled.length, state).toBe(source.length);
      compiled.forEach((rule, i) => {
        const action = rule.action as { test?: (...a: unknown[]) => unknown };
        const cases = (sourceAction(source[i]) as { cases?: object } | undefined)?.cases;
        const entry = { hit: false, outcomes: new Set<unknown>(), cases: cases ? Object.keys(cases).length : 0 };
        hits.set(hits.has(rule.name) ? `${rule.name} #${i}` : rule.name, entry);
        const wrapped = typeof action === "object" && typeof action?.test === "function"
          ? { ...action, test: (...a: unknown[]) => { const v = action.test!(...a); entry.outcomes.add(v); return v; } }
          : action;
        Object.defineProperty(rule, "action", { configurable: true, get: () => ((entry.hit = true), wrapped) });
      });
    }
    for (const [file, text] of corpus(d)) {
      tokenLines(tk, text);
      // No form in our own files may leave a string or comment open: the rest of the file would test nothing. The seed
      // is kept verbatim: on PostgreSQL its `$$dollar -- text$$` stays open, since a body is code and `--` hides the tag.
      if (file === "stress.sql") continue;
      let state = tk.getInitialState();
      for (const line of lines(text)) state = tk.tokenize(line, true, state).endState;
      expect((state as { stack: { depth: number } }).stack.depth, `${file} ends inside a state`).toBe(1);
    }
    const missed = [...hits].filter(([, e]) => !e.hit).map(([name]) => name);
    const partial = [...hits].filter(([, e]) => e.hit && e.outcomes.size < e.cases).map(([name, e]) => `${name}: ${e.outcomes.size}/${e.cases} cases`);
    expect(missed).toEqual(UNREACHABLE[d] ?? []);
    expect(partial).toEqual([]);
  });
});

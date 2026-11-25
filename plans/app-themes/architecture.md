# Querithm App Themes: theme state, schema and migration architecture (v2)

Seat: Theme State & Schema Architect. Scope: data model, settings schema and migration, resolution, editor chrome,
tokenizer and semantic tokens, start screen and shell, Settings UI, file-by-file plan, tests, slices, risks, doc text.
Palette values (hexes) belong to the palette seat (`plans/app-themes/themes.json`).
This plan does not depend on the values: `<theme-id>` stands for the seven new ids.
Every claim about current code cites `path:line` in `C:/Taosif/Project/querithm`. This document replaces v1.

---

## 0. Changes from v1

| # | Change | Driven by |
|---|---|---|
| C1 | **D2 overruled.** Querithm Dark and Light now carry their own Default palette: Standard's 12 hexes plus explicit `constant`, `wordOperator`, `cast`, `bind`, `systemVariable`, `cte` and `schema`, with semantic tokens on. Standard itself stays byte-identical. Rewritten as a result: I2, §2.3, §3.4, T1, T2, T12, §7.2 and the release note. | owner ruling, DA2 §1 |
| C2 | **The migration from Standard to Default is now visible.** It runs **exactly once**, is written at once, and shows a one-time notice with a **Use Standard** action. Toasts gain an optional action. | DA2 V10 |
| C3 | **Word operators are bold** in every Default palette: `{ ...STYLE, wordOperator: "bold", ...app.fontStyle }`. | DA2 V3 |
| C4 | **Editor chrome is themed from tokens** (§4.3). The find match uses `--q-warn` at 10 % with a warn border (DA2 R1 is picked over Eye W2). The error and info squiggles and their overview-ruler marks use `--q-err` / `--q-info` (DA2 R2, Eye W1). The **warning squiggle uses a separate non-text mark**, `--q-warn-mark`, falling back to `#cca700` (dark) / `#a87000` (light): Eye W1 is picked over DA2 R2, because `--q-warn` is too close to the error red under deuteranopia (§4.3 has the numbers). Word highlight is fg at 9 % with a line-strong border (V4). Bracket match is a line-strong border with no fill, and indent guides use the line tokens (V5). Querithm's decoration fills drop to 12 % (V6). | DA2 R1, R2, V4–V6; Eye W1, W2 |
| C5 | **`--q-info` is added** (D10 reversed). It is required in the generated blocks; the legacy blocks get no new line and Monaco falls back to accent there. | owner, DA2 R2 |
| C6 | **T8 gains** the overlay floors, the squiggle-mark rules and the co-located pairs (constant/operator, constant/function, cte/schema), plus cte/ident ≥ 18. | DA2 R1, R2, V4–V6, P1, P2 |
| C7 | **Stale semantic tokens are dropped** synchronously whenever the palette changes, through `monacoInternals.clearSemanticTokens`. | DA2 V7 |
| C8 | **The boot cache also keeps the syntax choice.** It seeds a failed settings read, and `boot-theme.js` names `data-syntax`. | DA2 V8 |
| C9 | **`syntaxTheme` is mirror-written** from `syntaxColors`, and a load **adopts** a real choice made by an older build. This replaces v1's "old key stays frozen". | DA2 V9 |
| C10 | **Settings are written per changed key.** `persist()` re-reads `ui.settings` and merges only the keys set in this session. This replaces whole-object writes and `extra`, and covers V12. | DA2 V11, V12 |
| C11 | **MySQL `?` becomes `delimiter.bind`** (`legacy: "delimiter"`), so D7 is lifted. | DA2 O6 |
| C12 | **D11 is resolved.** The semantic option is global configuration, so every editor follows it. | DA2 O5 |
| C13 | **New setting "Italic comments"** (`italicComments`, default on), written to the document as `data-comments="upright"`. | Eye A5 |
| C14 | **Per-theme guidance.** Each card's description is its comfort hint (≤ 90 chars), and the Theme row gets a "Which theme should I use?" disclosure. | Eye guidance |
| C15 | **A dark comment readability floor joins T12**: APCA \|Lc\| ≥ 47.5 on bg and ≥ 46.5 on the current line, which is Querithm Dark's own level. | Eye R1–R4 rule |
| C16 | **Rename:** the cool light theme is renamed (proposed "Floe", id `glacier` → `floe`) before its first release. T11 snapshots ids at that release. | DA1 §4 |
| C17 | **Slices reordered.** The chrome fixes ship first (they fix bugs on today's themes). Lexical and semantic tokens land before the legacy Default palettes that use them. Release boundaries are explicit. | DA2 §1 degradation note |
| C18 | **Tooling:** `check.mjs:188` and `render.mjs:117` must model the warn-10 % find layer, not accent 30 %. This is the palette seat's tooling, not repo code. | DA2 R1 |

### How V7–V12 were ruled
| Finding | Ruling | One-line reason |
|---|---|---|
| V7 stale semantic colours after a switch | **Accept** | The colour ids baked into applied semantic tokens are wrong against the new colour map. They are cleared only when the palette key changes, so a no-op re-apply (OS change under a fixed theme) causes no flicker. |
| V8 failed read shows Default instead of the user's syntax | **Accept** | One optional field in the existing boot cache, no `v` bump. It also lets the pre-load Monaco theme match. |
| V9 a choice made in an older build is lost on re-upgrade | **Accept; supersedes "frozen old key"** | Mirror-writing keeps older builds showing the user's current choice. The adoption rule ignores `"standard"`, the only value an older build writes as a fallback, so it never flips a newer choice. |
| V10 one-time migration and notice | **Accept** | Required now that the migration is visible (C1). |
| V11 two instances revert each other's keys | **Accept, as a general settings change in its own slice** | The bug predates this work but now hits theme picks too. The `loadFailed` path already re-reads before writing (`settings.ts:287-298`). Writing only changed keys also means a saved key is, from now on, an explicit choice, which ends the ambiguity behind §3.4 for future default changes. |
| V12 unknown future ids lost on downgrade | **Accept, satisfied by V11** | Keys this session did not change are written back from the fresh read verbatim, unknown values included. No separate `unknownChoice` map is needed. |

---

## 1. Goals and invariants

### Goals
- **G1** Nine App Themes:
  - `dark` (Querithm Dark) and `light` (Querithm Light), with unchanged ids and token values;
  - 7 new ones.

  Each has a mode (`dark` | `light`) and a complete `--q-*` token set. Every one of the 9 defines its own Default SQL
  palette.
- **G2** The Theme setting keeps Dark / Light / Match system and adds which dark App Theme and which light App Theme are
  used.
- **G3** A new Syntax colors choice, **Default**, made the default: the active App Theme's own SQL palette.
- **G4** New token kinds:
  - lexical: constant, word operator, `::`, bind parameter, `@@` system variable;
  - semantic: CTE name, schema prefix.

  Every existing syntax theme renders them exactly as today.
- **G5** Editor chrome (find, squiggles, word and bracket highlight, decorations) follows the App Theme and stays
  readable for colour-vision-deficient users.
- **G6** Saved settings, the start screen and the Rust shell's start colour work for any App Theme. Adding a theme
  touches no Rust and no boot file.

### Invariants. Each one is enforced by a test named in §9.
| # | Invariant | Enforced by |
|---|---|---|
| I1 | **Standard is unchanged.** The six syntax themes keep their id, hexes and font styles. The Monaco rules for the 16 tokens they style today stay the same. Through real Monarch and Monaco's real `TokenTheme`, every character of a per-dialect corpus gets the same colour and font style as before. | T1 golden, T2 frozen |
| I2 | **Querithm Dark and Light are unchanged.** Their blocks in `tokens.css:52-137` and `:139-205` stay byte-identical. Their Default palette is Standard's 12 hexes plus Default-only colours for constant, word operator, cast, bind, system variable, CTE and schema, with semantic tokens on. | T3, T2 |
| I3 | `data-theme` always means the **mode** (`dark` \| `light`). Every current reader keeps working unchanged (§4.5). | T4 |
| I4 | A theme's token block applies **only when `data-theme` equals its mode**. A mismatch, an unknown id or missing styles fall back to the Querithm theme of that mode, in CSS (selector) and in TS (`resolveDocumentAppTheme`) alike. Tokens are never mixed. | T5 |
| I5 | **Explicit choices survive every reload.** The Standard → Default migration runs exactly once per saved object. An explicit Standard chosen afterwards is never turned back. | R4, N1 |
| I6 | **Upgrade → downgrade → upgrade loses no choice.** Older builds keep the new keys, see the closest choice they know (mirror), and a real choice made there is adopted on return. | R9, R9b |
| I7 | **Only keys changed in this session are written.** Keys written by another instance or a newer version are kept verbatim. | R13, R14 |
| I8 | The pre-paint layers (shell, `boot-theme.js`, `boot-splash.css`) **never know theme ids or palettes**: the webview tells them colours. | T6, Rust tests |
| I9 | A theme switch never re-renders React on keystrokes or grid scroll (CLAUDE §4). The semantic provider runs only in Monaco's debounced viewport request, within a §14 budget. Semantic tokens never show a colour from a previous palette. | perf test, bench, V7 test |
| I10 | **Contrast.** Every App Theme passes every contrast test that Dark and Light pass today. Every legacy syntax theme stays ≥ 4.5:1 on every editor layer of every App Theme of its mode. Editor chrome overlays and marks keep the T8 floors. | T7, T8 |
| I11 | Every Monarch token type any of the 6 tokenizers can emit has an explicit rule in every palette, at least as specific as any rule in Monaco's base themes. | T9 |

---

## 2. Data model

### 2.1 Where the token data lives: trade-off and pick
**Today.** The legacy token blocks are CSS (`tokens.css:52-205`), and seven tests parse that text:
`syntaxTheme.test.ts:13-20`, `contrast.test.ts:10-20`, `startScreen.test.ts:14-19`, `draw.test.ts:99-104`,
`monitor/model.test.ts:150-152`, `tabStyle.test.ts:11-17` and `ToolTab.test.tsx:125`. Monaco, Glide, the charts and
`tabStyle` read the values at run time through `getComputedStyle` (`monaco.ts:50-53`, `ResultGrid.tsx:44`,
`charts/theme.ts:21`, `tabStyle.ts:156`).

| Option | For | Against |
|---|---|---|
| A. Hand-written CSS blocks for the 7 new themes | CSS-native; no build step | Data in three places (CSS tokens, TS metadata and SQL palette, TS preview swatches); block completeness is not typed; `wait-*` copied by hand; tests must regex-parse 9 blocks |
| B. TS data → CSS generated at build time and checked in | One typed source; a static file | A generator, a `gen:themes` step, a stale-file test, and a second generated artefact next to `contract.ts` |
| **C. TS data → one `<style>` built at start (pick)** | One typed source: `Record<UiKey,string>` makes a missing token a compile error. Tests read TS directly. No generator, no stale file. Legacy blocks untouched. | About 2 KB of string building at start. The style must exist before the first render, and a missing style must degrade safely. |

**Pick: C.**
- **One source.** `src/theme/appThemes.ts` holds all 9 themes' metadata and SQL palettes, and the 7 new themes' tokens.
- **Legacy themes.** Querithm Dark and Light keep their hand-written CSS blocks: byte-identical, and still the fallback
  when no JS has run. TS mirrors their `ui` values for previews and tests, and the sync test T3 holds the mirror to the
  CSS.
- **Install.** `installAppThemeStyles()` runs in `src/main.tsx` before `createRoot`, next to `applyCachedUiScale()`
  (`main.tsx:9`). It inserts one `<style id="q-app-themes">`, replacing it on a second call, which keeps HMR safe.
- **CSP.** `style-src 'self' 'unsafe-inline'` (`src-tauri/tauri.conf.json:27`) allows the element. Monaco's injected
  styles depend on the same rule. `index.html` stays free of inline `<style>` (`startScreen.test.ts:66-70`).
- **No visible flash.** Before the bundle runs, the splash covers the window (`boot-splash.css` `#splash`,
  `inset: 0; z-index: 10000`).
- **Failure.** If the install throws, the flag `appThemeStylesInstalled()` stays false, and `resolveDocumentAppTheme`
  falls back to the mode's legacy theme, so Monaco and the UI agree (I4). The error goes through `reportRenderError`.

### 2.2 Selector rule (I4)
```css
/* legacy, unchanged */            :root, :root[data-theme="dark"] { … }   :root[data-theme="light"] { … }
/* generated, one per new theme */ :root[data-theme="<mode>"][data-app-theme="<theme-id>"] { color-scheme: <mode>; --q-…: …; }
```
- Specificity (0,3,0) beats both legacy blocks, (0,1,0) and (0,2,0), whatever the order of the stylesheets.
- Because the mode is part of the selector, a block whose mode disagrees with `data-theme` does not apply.
- **Family fallback.** A token that a new block does not declare comes from the mode's legacy block. This is used on
  purpose for the family tokens (§2.3).

### 2.3 TypeScript types (`src/theme/appThemes.ts`, new)
```ts
import { SYNTAX_THEMES, type FontStyle, type SyntaxPalette, type SyntaxRole } from "../editor/syntaxTheme";

export type ThemeMode = "dark" | "light";

/** The 30 core tokens every App Theme declares (the design JSON's `ui`). `info` is new: the legacy blocks have no
 *  --q-info line (frozen), so for them `ui.info` mirrors `ui.accent` and Monaco falls back to --q-accent (§4.3). */
export const UI_TOKENS = {
  bg: "--q-bg", panel: "--q-panel", panel2: "--q-panel-2", raised: "--q-raised", hover: "--q-hover",
  line: "--q-line", lineStrong: "--q-line-strong", fg: "--q-fg", muted: "--q-muted", mutedHl: "--q-muted-hl",
  faint: "--q-faint", accent: "--q-accent", accentSolid: "--q-accent-solid", accentFg: "--q-accent-fg",
  info: "--q-info", ok: "--q-ok", warn: "--q-warn", err: "--q-err", errSolid: "--q-err-solid", null: "--q-null",
  sel: "--q-sel", active: "--q-active", rowSel: "--q-row-sel", overlaySel: "--q-overlay-sel",
  warnSolid: "--q-warn-solid", warnSolidFg: "--q-warn-solid-fg", ident: "--q-ident", suggestMatch: "--q-suggest-match",
  diffInsert: "--q-diff-insert", diffRemove: "--q-diff-remove",
} as const;                         // explicit map: auto-kebab would make panel2 → --q-panel2
export type UiKey = keyof typeof UI_TOKENS;
/** "#rrggbb" or "rgba(r, g, b, a)" only (T10). `warnMark` is optional (below). */
export type UiTokens = Record<UiKey, string> & { warnMark?: string };

/** Derived, never authored: the activity monitor's wait ages copy ok / warn / err, as both legacy blocks do. */
export const DERIVED = { "--q-wait-short": "ok", "--q-wait-mid": "warn", "--q-wait-long": "err" } as const;

/** The warning squiggle and its overview-ruler mark: a non-text mark (≥ 3:1), apart from the error mark under
 *  protan / deutan / tritan (T8). A theme may set ui.warnMark (emitted as --q-warn-mark); otherwise this per-mode
 *  colour is used. Dark is Monaco's own warning colour today, so Querithm Dark's squiggle does not change. */
export const WARN_MARK_FALLBACK: Record<ThemeMode, string> = { dark: "#cca700", light: "#a87000" };

/** Marks whose meaning must not change between themes (prod is red, a view is violet, chart order is CVD-validated).
 *  They are inherited from the mode's Querithm block through the cascade unless a theme overrides one where a T7 test
 *  fails. 29 tokens: --q-env-* (11), --q-obj-* (8), --q-chart-1..8 and -other (9), --q-edge-shadow. */
export const FAMILY_TOKENS = [/* the 29 names */] as const;
export type FamilyToken = (typeof FAMILY_TOKENS)[number];
// Legacy block: 29 (UI_TOKENS without info) + 3 derived + 29 family = 61 names (T3).
// New block: the same plus --q-info, and --q-warn-mark when set.

export interface AppTheme {
  id: string;                       // kebab case; stable from its first release (saved in store.db and the boot cache)
  name: string;                     // ≤ 18 chars ("Querithm Dark")
  mode: ThemeMode;
  /** The card's one line, ≤ 90 chars: what it looks like and when it is restful (Eye guidance, §7.1). */
  description: string;
  /** Querithm Dark / Light: their tokens live in tokens.css; `ui` mirrors them (T3) and nothing is generated. */
  legacy?: true;
  ui: UiTokens;
  family?: Partial<Record<FamilyToken, string>>;
  /** The theme's own Default SQL palette. Every theme sets constant, wordOperator, cast, bind, systemVariable, cte, schema. */
  sql: SyntaxPalette & Required<Pick<SyntaxPalette, "constant" | "wordOperator" | "cast" | "bind" | "systemVariable" | "cte" | "schema">>;
  /** Over { ...STYLE, wordOperator: "bold" } (§4.1). */
  fontStyle?: Partial<Record<SyntaxRole, FontStyle>>;
  /** Start-screen tiers (boot-splash.css --s-idle / --s-track); default ui.raised / ui.panel2. Legacy: today's values. */
  splash?: { idle: string; track: string };
}

export const APP_THEMES = [
  { id: "dark", name: "Querithm Dark", mode: "dark", legacy: true, description: "Classic high-contrast dark.",
    ui: { /* tokens.css:54-136, info = accent */ }, splash: { idle: "#1e222e", track: "#1b1f2a" },
    // Standard's 12 hexes, plus the Default-only roles (current values; themes.v2.json is authoritative):
    sql: { ...SYNTAX_THEMES[0].dark, constant: "#f8acb5", wordOperator: "#66bccc", cast: "#66bccc", bind: "#e6a6d6",
           systemVariable: "#e6a6d6", cte: "#8fd5ff", schema: "#c5ac98" } },
  { id: "light", name: "Querithm Light", mode: "light", legacy: true,
    description: "Classic pure white, the brightest theme. For long sessions, try a softer light theme.",
    ui: { /* tokens.css:141-204, info = accent */ }, splash: { idle: "#e3e8ef", track: "#eef1f6" },
    sql: { ...SYNTAX_THEMES[0].light, constant: "#832543", wordOperator: "#0e6584", cast: "#0e6584", bind: "#543763",
           systemVariable: "#8a1f7c", cte: "#155ed0", schema: "#6c5644" } },
  // 5 dark + 2 light, in Settings order: { id: "<theme-id>", name, mode, description, ui, family?, sql, fontStyle? }
] as const satisfies readonly AppTheme[];

export type AppThemeId = (typeof APP_THEMES)[number]["id"];
export type DarkAppThemeId = Extract<(typeof APP_THEMES)[number], { mode: "dark" }>["id"];
export type LightAppThemeId = Extract<(typeof APP_THEMES)[number], { mode: "light" }>["id"];
export const DARK_APP_THEME_IDS: readonly DarkAppThemeId[];   // derived by filter
export const LIGHT_APP_THEME_IDS: readonly LightAppThemeId[];
export const LEGACY_APP_THEME: Record<ThemeMode, AppTheme>;   // { dark: APP_THEMES[0], light: APP_THEMES[1] }
export function appThemeById(id: string | undefined): AppTheme | undefined;

/** The core + derived (+ warn-mark) values as CSS variables; legacy too (mirror test, previews). */
export function uiVars(t: AppTheme): Record<string, string>;
/** What the generated block declares: {} for legacy; uiVars + family overrides otherwise. */
export function tokenVars(t: AppTheme): Record<string, string>;
/** The whole generated stylesheet: one block per non-legacy theme (§2.2), `color-scheme` first. */
export function appThemeCss(themes: readonly AppTheme[] = APP_THEMES): string;

export interface BootColors { id: string; bg: string; fg: string; muted: string; idle: string; track: string }
export function bootColors(t: AppTheme): BootColors; // { id, bg: ui.bg, fg: ui.fg, muted: ui.muted, idle: splash?.idle ?? ui.raised, track: splash?.track ?? ui.panel2 }
```
- The legacy `sql` is a **new object**, not Standard's reference.
- Graceful degradation: if the semantic slice (S4, §10) had to slip behind the legacy palettes, they would ship without
  `cte` and `schema`. `effectiveRole` then falls back to `ident`, semantic stays off, and nothing else changes. With the
  §10 order this does not arise.

### 2.4 Syntax palette extension (`src/editor/syntaxTheme.ts`)
```ts
export type SyntaxRole =
  | "keyword" | "type" | "string" | "number" | "comment" | "variable" | "function" | "operator" | "ident"
  | "temp" | "batch" | "delimiter" | "escape"
  // Default palettes only. A palette without them colours their tokens as before (TOKEN_ROLES.legacy, §4.2).
  | "constant" | "wordOperator" | "cast" | "bind" | "systemVariable" | "cte" | "schema";
type Extended = "constant" | "wordOperator" | "cast" | "bind" | "systemVariable" | "cte" | "schema";
export type SyntaxPalette = Record<Base, string> & Partial<Record<"temp" | "batch" | "delimiter" | "escape" | Extended, string>>
  & { bracket1: string; bracket2: string; bracket3: string };

export type SyntaxChoice = "default" | SyntaxThemeId;
export const SYNTAX_CHOICES: readonly SyntaxChoice[] = ["default", ...SYNTAX_THEME_IDS];
export const DEFAULT_SYNTAX_CHOICE: SyntaxChoice = "default";
// DEFAULT_SYNTAX_THEME stays "standard" with its old meaning: the fallback of syntaxThemeById (syntaxTheme.ts:139-145).
// SYNTAX_THEMES (syntaxTheme.ts:34-137) is not touched.
```
How the design JSON maps onto `AppTheme.sql`:
- `wordOperator` = `cast` = `sql.operator`;
- `bind` = `sql.parameter`;
- `systemVariable` = `sql.variable`.

They are explicit fields, so a designer can later give one of them its own colour.

---

## 3. Settings schema, persistence and migration

### 3.1 Fields (`src/store/settings.ts:8-90`)
```ts
export interface Settings {
  /** Unchanged meaning (settings.ts:9): show the dark pick, the light pick, or follow the OS between them. */
  theme: "dark" | "light" | "system";
  /** The App Theme for dark mode (and an OS in dark mode under "system"). Default "dark" = Querithm Dark. */
  darkTheme: DarkAppThemeId;
  /** The App Theme for light mode. Default "light" = Querithm Light. */
  lightTheme: LightAppThemeId;
  /** SQL token colors: "default" (the App Theme's own palette) or a syntax theme that keeps its colors on every App
   *  Theme. Replaces `syntaxTheme` (settings.ts:11); `syntaxTheme` is still written, as a mirror for older builds (§3.5). */
  syntaxColors: SyntaxChoice;
  /** Comments in the SQL editor are italic (every palette); off = upright (Eye A5). */
  italicComments: boolean;
  // … all other fields unchanged
}
defaultSettings: { theme: "dark", darkTheme: "dark", lightTheme: "light", syntaxColors: "default", italicComments: true, … }
const choices = {
  theme: ["dark", "light", "system"],                 // unchanged
  darkTheme: DARK_APP_THEME_IDS, lightTheme: LIGHT_APP_THEME_IDS,
  syntaxColors: SYNTAX_CHOICES,                       // replaces syntaxTheme: SYNTAX_THEME_IDS (settings.ts:166)
  panelDock, resultLayout, keywordCase, tabLayout,    // unchanged
};
```
**Why a mode plus two picks, rather than one `theme: AppThemeId | "system"`:**
- `theme` keeps its three values, so nothing to do with it needs migrating.
- An older build reads `theme` as valid, while an id would make it fall back to `"dark"` (`clean()`,
  `settings.ts:176-177`).
- "Match system" reuses the two picks.
- The toggle command (`commands.ts:183-185`) keeps flipping between the user's dark pick and light pick.
- The theme in use is always one of the two picks (§7.1).

### 3.2 `clean()` and `sanitizeSettings()`
- **Choice keys.** `clean("darkTheme" | "lightTheme" | "syntaxColors", v)` goes through the choices path
  (`settings.ts:176-177`). A non-string, an unknown id or an id of the **other mode** returns `undefined`, so the key
  takes its default. The same holds for `set()` (`settings.ts:341-350`). `italicComments` takes the boolean path
  (`:193`).
- **Return value.** `sanitizeSettings(raw)` (`settings.ts:202-216`) now returns
  `{ values, migrated: boolean, adopted: boolean }`. `extra` is gone, because §3.5 writes from a fresh read.
- **Legacy keys.** `syntaxTheme` is consumed by the two blocks below and is never a store key.

  ```ts
  const has = (k: string) => Object.prototype.hasOwnProperty.call(raw, k);
  const old = (raw as Record<string, unknown>).syntaxTheme;
  if (!has("syntaxColors")) {
    // 2026-11 (§16): once per saved object. An explicit non-Standard theme is kept; Standard, a removed id, junk or
    // nothing become Default.
    values.syntaxColors = migrateSyntaxTheme(old);
    migrated = true;
  } else if (typeof old === "string" && old !== "standard" && (SYNTAX_THEME_IDS as readonly string[]).includes(old)
             && old !== legacySyntaxMirror(String(raw.syntaxColors))) {
    // V9: an older build changed it (its only fallback value is "standard"), so this is a real pick: adopt it.
    values.syntaxColors = old as SyntaxThemeId;
    adopted = true;
  }

  export const migrateSyntaxTheme = (v: unknown): SyntaxChoice =>
    typeof v === "string" && v !== "standard" && (SYNTAX_THEME_IDS as readonly string[]).includes(v) ? (v as SyntaxThemeId) : "default";
  /** What older builds read in `syntaxTheme`: the same id for the six 2026-11 themes, "standard" for anything else
   *  ("default" and any later id). A contract for every later version (§16). */
  export const legacySyntaxMirror = (c: string): SyntaxThemeId =>
    (["color-blind", "neon", "forest", "ember", "ocean"] as string[]).includes(c) ? (c as SyntaxThemeId) : "standard";
  ```
- **After a successful `load()`** (`settings.ts:328-340`), when a saved object existed and either `migrated` or
  `adopted` is true:
  1. call `set({ syntaxColors: values.syntaxColors })`, which makes the key dirty (§3.5);
  2. call `void flushSettings()` at once, so the migration is written exactly once (I5);
  3. if `migrated && values.syntaxColors === "default"`, queue the notice (§3.6).

  With no saved object at all (fresh install), nothing is written and no notice is shown.
- **Load failure (D12).** The catch at `settings.ts:333-339` becomes `set({ loaded: true, ...cachedThemeChoice() })`.
  That covers theme, both picks and syntax (V8). These values are not dirty, so the next successful read overrides them
  (§3.5).

### 3.3 Old → new mapping
| Saved before (ui.settings) | Loaded as | Visible at upgrade? | Note |
|---|---|---|---|
| `theme: "dark"` / `"light"` / `"system"` | the same `theme`; `darkTheme: "dark"`, `lightTheme: "light"` | no | Querithm Dark / Light |
| `theme` missing or invalid (`"blue"`) | `"dark"` | no | as today (`misc.test.ts:220-225`) |
| no `ui.settings` at all | defaults: dark / dark / light / `"default"` / italic on | — (fresh install) | Querithm Dark with its Default palette; no notice; nothing written |
| `syntaxTheme: "standard"`, no `syntaxColors` | `syntaxColors: "default"`; written once | **yes**, the 7 roles in §3.4; one-time notice | |
| `syntaxTheme` missing (saved before 2026-10-30), no `syntaxColors` | `"default"`; written once | yes; notice | |
| `syntaxTheme: "color-blind" \| "neon" \| "forest" \| "ember" \| "ocean"`, no `syntaxColors` | the same id; written once | no | explicit choice kept; no notice |
| `syntaxTheme: "high-contrast" \| "classic" \| "minimal"` (removed 2026-11-01), unknown, non-string | `"default"`; written once | yes; notice | before: Standard |
| `syntaxColors` valid; `syntaxTheme` equal to its mirror | as saved | — | the normal state after the upgrade |
| `syntaxColors` present; `syntaxTheme` a non-Standard legacy id that is not its mirror | the `syntaxTheme` id (adopted) | — | an older build changed it (V9) |
| `syntaxColors` present but invalid; `syntaxTheme` absent or equal to its mirror | `"default"` | — | as for every choice |
| `darkTheme` / `lightTheme`: unknown, wrong mode or wrong type | `"dark"` / `"light"` for display; **the stored value is kept** unless this session changes it | — | V12, through §3.5 |
| localStorage `querithm.theme` | unchanged meaning (one of three words) | — | `src/app/theme.ts:15` |
| localStorage `querithm.themeBoot` (new) | §6 | — | an older build ignores it |
| shell file `<config>/theme` holding one word | read as that word with the Querithm colours | — | §6.3 |

### 3.4 Why `"standard"` → `"default"`, now that the change is visible
1. **Saved data cannot tell an explicit Standard from the old default.** The whole object was saved on every change
   (`settings.ts:302`). So everyone who ever changed any setting has `"standard"`.
2. **The owner made Default the default**, tied to the App Theme, so every user should get it. Keeping `"standard"`
   would leave every user who ever saved a setting without Default.
3. **The change on Querithm Dark/Light is bounded.** It covers 7 roles: TRUE/FALSE/NULL, the word operators (still
   bold, V3), `::`, `@@x`, bind parameters, CTE names and schema prefixes. Strings, numbers, comments, functions, types,
   identifiers, brackets and every other keyword keep Standard's exact hexes (I2).
4. **It is announced once, with an undo.** A non-modal notice with **Use Standard** appears (§3.6). After the upgrade
   the user's choice is permanent (I5), and Standard stays one card away in Settings.
5. **Rename, not version marker.** With a marker, an older build would write `syntaxTheme: "standard"` (its fallback for
   the unknown `"default"`) and keep the marker. The next upgrade would then trust the marker and show Standard. With
   the rename plus the mirror (§3.5), round trips are exact (I6).
6. **The ambiguity ends here.** §3.5 writes only the keys a user changes, so from now on a saved key means an explicit
   choice. A future default change can tell the two apart.

### 3.5 Persistence: write only what this session changed (V11, V12, V9 mirror)
Today `persist()` writes `{ ...extra, ...persisted(state) }`, the whole store (`settings.ts:274-276,302`). Two
instances (M23 allows a second window) therefore revert each other's keys, and every default is frozen into the blob.

New design:
```ts
/** Keys set since their last successful write, with a version, so a set() during a write keeps the key dirty.
 *  Replaces `changedUnread` (settings.ts:239) and `extra` (:237). */
const dirty = new Map<keyof Settings, number>();
let version = 0;
// set(): for each cleaned key k (settings.ts:344-349), dirty.set(k, ++version).

async function persist(): Promise<void> {
  saveTimer = undefined;
  const pending = new Map(dirty);
  if (!pending.size) { useSettingsStatus.setState({ save: "saved", error: undefined }); return; }
  useSettingsStatus.setState({ save: "saving" });
  try {
    // ui.settings as stored now: {} when absent or unreadable JSON (settings.ts:249-254); throws when the read fails.
    const fresh = await readRawSettings();
    if (useSettingsStatus.getState().loadFailed) {             // as settings.ts:287-298: adopt every key not changed here
      const r = sanitizeSettings(fresh);
      useSettings.setState({ ...r.values, ...pick(useSettings.getState(), [...dirty.keys()]) });
      useSettingsStatus.setState({ loadFailed: false });
    }
    const s = useSettings.getState();
    const out: Record<string, unknown> = { ...fresh };         // other instances' and newer versions' keys, verbatim
    for (const k of pending.keys()) out[k] = s[k];
    for (const k of sidecarOwned) delete out[k];                // never in ui.settings (settings.ts:197)
    if (pending.has("syntaxColors")) out.syntaxTheme = legacySyntaxMirror(s.syntaxColors);   // V9 mirror
    await rpc("settings.set", { key: KEY, valueJson: JSON.stringify(out) });
    for (const [k, v] of pending) if (dirty.get(k) === v) dirty.delete(k);
    warned = false;
    useSettingsStatus.setState({ save: saveTimer ? "pending" : "saved", error: undefined });
  } catch (e) { failed(e); }                                    // the existing path: retried at the next change or engine restart
}
```
- **Cost.** One extra `settings.get` per debounced save (400 ms, `settings.ts:353`). Saves are user actions, not a §14
  hot path.
- **Failure.** If the re-read fails, the save fails like any failed write (`failed()`, `settings.ts:278-283`), and the
  keys stay dirty. There is never a blind whole-object write.
- **V12 by construction.** A key this session did not change is written from `fresh`, verbatim. That includes a
  `darkTheme` id this build does not know, written by a newer build.
- **Mirror contract.** Every later version that writes `syntaxColors` also writes `syntaxTheme` with
  `legacySyntaxMirror`: the six 2026-11 ids as themselves, anything else as `"standard"`. The adoption rule in §3.2
  relies on it.
- **Known limit.** Instance A still *shows* its stale in-memory value for a key B changed, until A restarts or changes
  that key itself. Live sync between instances is out of scope.

### 3.6 Migration notice (V10)
- `Toast` (`src/store/ui.ts:7-13`) gains an optional `action?: { label: string; run: () => void }`, and `Toasts`
  (`src/app/Palette.tsx:205`) renders it as a button. Duration follows `toastDuration` (`ui.ts:25-27`), which pauses
  while hovered or focused.
- `boot()` calls `notifySyntaxMigration()` after `splashStage(4)` (`App.tsx:86`), so the toast appears on the ready app.
  - Text (info): "Syntax colors now follow your color theme (Default): NULL, AND/OR, CTE names and schema prefixes have
    their own colors."
  - Action **Use Standard** → `set({ syntaxColors: "standard" })`.
- **Once.** The migrated value is flushed immediately (§3.2). The notice shows only on the run that migrated. If that
  write fails, the next start migrates and notifies again (accepted, and told by the save-failed toast).

---

## 4. Resolution logic and editor chrome

### 4.1 Pure resolvers (`src/theme/resolve.ts`, new; no DOM, no store)
```ts
export interface ThemeChoice { theme: "dark" | "light" | "system"; darkTheme: string; lightTheme: string }

/** The App Theme to show. Unknown or wrong-mode ids resolve to Querithm Dark / Light. */
export function resolveAppTheme(c: ThemeChoice, osDark: boolean): AppTheme {
  const mode: ThemeMode = c.theme === "system" ? (osDark ? "dark" : "light") : c.theme === "light" ? "light" : "dark";
  const t = appThemeById(mode === "dark" ? c.darkTheme : c.lightTheme);
  return t && t.mode === mode ? t : LEGACY_APP_THEME[mode];
}

/** The App Theme the document shows, read the way the CSS selector reads it (I4). */
export function resolveDocumentAppTheme(root: HTMLElement = document.documentElement): AppTheme {
  const mode: ThemeMode = root.dataset.theme === "light" ? "light" : "dark";
  const t = appThemeById(root.dataset.appTheme);
  return t && t.mode === mode && (t.legacy || appThemeStylesInstalled()) ? t : LEGACY_APP_THEME[mode];
}

export interface ResolvedSyntax { choice: SyntaxChoice; palette: SyntaxPalette; fontStyle: Partial<Record<SyntaxRole, FontStyle>>; semantic: boolean }

/** A legacy id keeps its palette in the App Theme's mode. "default" and anything unknown use the App Theme's palette.
 *  `uprightComments` (Italic comments off) removes the comment italic from either. */
export function resolveSyntax(choice: string | undefined, app: AppTheme, uprightComments = false): ResolvedSyntax {
  const legacy = SYNTAX_THEMES.find((t) => t.id === choice);
  const r: ResolvedSyntax = legacy
    ? { choice: legacy.id, palette: legacy[app.mode], fontStyle: legacy.fontStyle, semantic: false }
    : {
        choice: "default", palette: app.sql,
        fontStyle: { ...STYLE, wordOperator: "bold", ...app.fontStyle },          // V3
        semantic: app.sql.cte !== undefined || app.sql.schema !== undefined,      // every Default palette (C1)
      };
  return uprightComments ? { ...r, fontStyle: { ...r.fontStyle, comment: "" } } : r;
}
```
- `syntaxThemeById` (`syntaxTheme.ts:143-145`) keeps its fallback to Standard for legacy callers.
- An unknown `data-syntax` now resolves to **Default**, which is also the settings default. Default on Querithm Dark
  keeps Standard's keyword hex, so the `monaco.test.ts:143-147` keyword assertion still holds. Its title becomes
  "an unknown syntax id falls back to Default".

### 4.2 Monaco rules (`syntaxTheme.ts:152-186`)
```ts
export const TOKEN_ROLES: { token: string; role: SyntaxRole; legacy?: SyntaxRole }[] = [
  /* the 16 entries of syntaxTheme.ts:153-168, unchanged and in the same order */,
  { token: "keyword.constant",    role: "constant",       legacy: "keyword" },   // TRUE FALSE NULL that are keywords
  { token: "identifier.constant", role: "constant",       legacy: "ident" },     // TRUE / FALSE where they are not keywords
  { token: "keyword.operator",    role: "wordOperator",   legacy: "keyword" },   // AND OR NOT IN LIKE BETWEEN IS …
  { token: "type.cast",           role: "cast",           legacy: "type" },      // PG / DuckDB ::
  { token: "variable.bind",       role: "bind",           legacy: "variable" },  // $1 :p ? @p (dialect forms, §5)
  { token: "delimiter.bind",      role: "bind",           legacy: "delimiter" }, // MySQL ? (was untokenized: fg)
  { token: "predefined.global",   role: "systemVariable", legacy: "function" },  // @@x (T-SQL, MySQL)
  { token: "identifier.cte",      role: "cte",            legacy: "ident" },     // semantic (§5.3)
  { token: "identifier.schema",   role: "schema",         legacy: "ident" },     // semantic (§5.3)
];
/** The role a token takes in a palette: its own when the palette colours it, else what it was before (I1). */
export const effectiveRole = (p: SyntaxPalette, e: (typeof TOKEN_ROLES)[number]) => (e.legacy && p[e.role] === undefined ? e.legacy : e.role);
export function rulesFor(p: SyntaxPalette, fontStyle: Partial<Record<SyntaxRole, FontStyle>>, fg: string) {
  return TOKEN_ROLES.map((e) => { const r = effectiveRole(p, e); return { token: e.token, foreground: roleColor(p, r, fg).slice(1, 7), fontStyle: fontStyle[r] ?? "" }; });
}
export const syntaxRules = (t: SyntaxTheme, mode: ThemeMode, fg: string) => rulesFor(t[mode], t.fontStyle, fg); // kept for tests
```
- **Names avoid Monaco's base rules.** None of the new names equals a rule of `vs`, `vs-dark` or `hc-black`. Those base
  themes include `variable.parameter`, `variable.predefined`, `constant` and `string.sql`
  (`node_modules/monaco-editor/esm/vs/editor/standalone/common/themes.js:16-18,78-81`).
- **Every name still gets an explicit rule (I11).** With `inherit: true`, Monaco puts the base rules before ours
  (`standaloneThemeService.js:109-126`), and the most specific match wins.
- **Font style falls back with the colour.** Under Standard, `NULL` and `AND` stay bold keywords and `::` stays a plain
  type. MySQL `?` stays editor fg: none of the six themes sets `delimiter`, so `roleColor` returns fg
  (`syntaxTheme.ts:177`), as the `""` token gets today.

### 4.3 `applyMonacoTheme()` and the editor chrome (`src/editor/monaco.ts:73-125`)
```ts
let semanticOn = false, lastPaletteKey = "";
export const semanticEnabled = () => semanticOn;
/** Returns true when semantic tokens were switched on or off (the caller refreshes the editors' options). */
export function applyMonacoTheme(): boolean {
  const root = document.documentElement;
  const app = resolveDocumentAppTheme(root);
  const syn = resolveSyntax(root.dataset.syntax, app, root.dataset.comments === "upright");
  const tint = (v: string, aa: string) => `${hex(v).slice(0, 7)}${aa}`;
  const info = cssVar("--q-info") ? hex("--q-info") : hex("--q-accent");                         // legacy blocks: no --q-info
  const warnMark = cssVar("--q-warn-mark") ? hex("--q-warn-mark") : WARN_MARK_FALLBACK[app.mode];
  monaco.editor.defineTheme("querithm", {
    base: app.mode === "dark" ? "vs-dark" : "vs", inherit: true,
    rules: rulesFor(syn.palette, syn.fontStyle, hex("--q-fg")),
    colors: {
      /* monaco.ts:83-121 unchanged; bracket1..3 from syn.palette */
      // R1: find. A warn tint, so it is never read as the selection or the accent. The current match gets a 2 px solid
      // border (findWidget.css), the others a 1 px border at 60 %.
      "editor.findMatchBackground": tint("--q-warn", "1a"),
      "editor.findMatchBorder": hex("--q-warn"),
      "editor.findMatchHighlightBackground": tint("--q-warn", "1a"),
      "editor.findMatchHighlightBorder": tint("--q-warn", "99"),
      "editorOverviewRuler.findMatchForeground": tint("--q-warn", "99"),
      // R2: squiggles and their overview-ruler marks.
      "editorError.foreground": hex("--q-err"),
      "editorWarning.foreground": warnMark,
      "editorInfo.foreground": info,
      "editorOverviewRuler.errorForeground": hex("--q-err"),
      "editorOverviewRuler.warningForeground": warnMark,
      "editorOverviewRuler.infoForeground": info,
      // V4: word highlight (occurrencesHighlight "singleFile", editors.ts:110; Text kind highlights, providers.ts:703).
      "editor.wordHighlightBackground": tint("--q-fg", "17"),
      "editor.wordHighlightStrongBackground": tint("--q-fg", "17"),
      "editor.wordHighlightTextBackground": tint("--q-fg", "17"),
      "editor.wordHighlightTextBorder": hex("--q-line-strong"),
      // V5: bracket match and indent guides.
      "editorBracketMatch.border": hex("--q-line-strong"),
      "editorBracketMatch.background": "#00000000",
      "editorIndentGuide.background1": hex("--q-line"),
      "editorIndentGuide.activeBackground1": hex("--q-line-strong"),
    },
  });
  monaco.editor.setTheme("querithm");
  // V7: applied semantic tokens hold colour ids of the previous colour map; drop them when the palette changed. The
  // debounced re-request (100–500 ms, viewportSemanticTokens.js:35-36) paints them again in the new colours.
  const key = `${app.id}|${app.mode}|${syn.choice}|${root.dataset.comments ?? ""}`;
  if (key !== lastPaletteKey) { for (const m of monaco.editor.getModels()) clearSemanticTokens(m); lastPaletteKey = key; }
  const changed = semanticOn !== syn.semantic;
  semanticOn = syn.semantic;
  return changed;
}
```
- **The internal call.** `clearSemanticTokens(model)` goes in `src/editor/monacoInternals.ts`, "the one place Querithm
  reads a Monaco internal" (`monacoInternals.ts:1-6`). It calls `model.tokenization.setSemanticTokens(null, false)`
  (`textModel.js:150`, `tokenizationTextModelPart.js:171`; the same call Monaco makes at
  `viewportSemanticTokens.js:94,100`) inside a try. If an upgrade renames it, the call does nothing, and
  `monacoInternals.test.ts` fails on the bundled sources first.
- **Switching semantic tokens on.** `editors.ts:131` becomes `"semanticHighlighting.enabled": semanticEnabled()`.
  Standalone themes report `semanticHighlighting = false` (`standaloneThemeService.js:28`), so the option is the only
  switch. `updateOptions` writes it into the **global** standalone configuration
  (`standaloneServices.js:517-535`, `standaloneCodeEditor.js:215`). Every editor therefore follows it (C12).
- **Which source for which colour.**

  | Item | Pick | Why |
  |---|---|---|
  | Find match | **DA2 R1** (warn tint) over Eye W2 (Monaco's `#ea5c0055` plus an accent border) | Non-comment tokens stay ≥ 4.5 (Eye's: ≥ 3.6). The find layer keeps ΔE ≥ 8.3 from the selection. An accent border would read as a focus ring or the selection edge. Eye's real concern, accent fills, is met: warn is not accent. |
  | Error and info squiggles | `--q-err` / `--q-info` (DA2 R2 = Eye W1) | The two reviews agree. |
  | **Warning squiggle** | **Eye W1** (a dedicated amber mark) over DA2 R2 (`--q-warn`) | See below. |

- **The warning squiggle in detail.** Errors and warnings use the same wavy shape, so colour is the only cue.
  - `--q-warn` against `--q-err` under deutan simulation (ΔE): **Querithm Light 1.3**, Vellum 3.1, the cool light
    theme 4.5, Cortado tritan 3.7 (a design-session script on `legacy-themes.json` and `da2-proposal.json`).
  - Querithm Light's warn is frozen, so no token fix is possible there.
  - A squiggle is a non-text mark and needs only 3:1, so it can be a lighter, yellower amber.
  - With `WARN_MARK_FALLBACK`, all 9 themes reach ≥ 3.24:1 on the four squiggle layers. Every pair among err, mark and
    info stays ≥ ΔE 10.5 in normal vision and under protan, deutan and tritan (a design-session script; `plans/app-themes/check.mjs` re-measures the marks).
  - Dark keeps `#cca700`, Monaco's current colour, so Querithm Dark's squiggles do not change.
- **Decoration fills (V6).** Outside the frozen blocks, in `tokens.css:1110-1133`, the fills become:
  - `.q-debug-line`: warn 12 % (was 18 %);
  - `.q-trace-line`: accent 12 % (was 18 %);
  - `.q-error-line`: err 12 % (was 14 %);
  - `.q-pair-match`: accent 12 % (was 22 %);
  - `.q-insert-match`: accent 12 % (was 25 %).

  The border and the dashed outline stay. `.q-run-scope` and `.q-run-scope-prod` are already 12 %.
- **Future CSS consumers of info** use `var(--q-info, var(--q-accent))`.

### 4.4 `syncDocumentTheme()` and when everything runs (`src/app/App.tsx:44-52,100-125`)
```ts
/** Before the settings load: the choice the window last had (localStorage), never the store's defaults (§16). */
function choiceToShow(s = useSettings.getState()) {
  const live = { theme: s.theme, darkTheme: s.darkTheme, lightTheme: s.lightTheme, syntaxColors: s.syntaxColors };
  return { ...(s.loaded ? live : { ...live, ...cachedThemeChoice() }), italicComments: s.italicComments };
}
function syncDocumentTheme(c = choiceToShow()) {
  const root = document.documentElement;
  const app = resolveAppTheme(c, osPrefersDark());
  setIfChanged(root, "theme", app.mode);                          // data-theme (I3)
  setIfChanged(root, "appTheme", app.id);                         // data-app-theme
  setIfChanged(root, "syntax", c.syntaxColors);                   // data-syntax
  setIfChanged(root, "comments", c.italicComments ? undefined : "upright");   // data-comments (absent = italic, as today)
  setBootColors(root, bootColors(app));                           // --boot-*: the splash shown again by Restart engine (§6)
  if (applyMonacoTheme()) refreshEditorOptions();                 // semantic tokens on/off
}
```
- `setIfChanged` writes an attribute only when its value differs (`undefined` removes it). Today every syntax change
  re-writes `data-theme` (`App.tsx:49`) and flushes `tabStyle`'s caches (`tabStyle.ts:120-123`).
- `SettingsEffects` selects **primitives** (each the cached value before load): `theme`, `darkTheme`, `lightTheme`,
  `syntaxColors`, `italicComments`. A selector returning a fresh object would re-render forever.
- `cachedThemeChoice()` is read once, lazily.
- Two effects:
  - `[loaded, unread, theme, darkTheme, lightTheme, syntaxColors]` → `keepTheme(choice)`, gated as at `App.tsx:107`;
  - `[theme, darkTheme, lightTheme, syntaxColors, italicComments]` → `apply()` plus the `matchMedia` listener
    (`App.tsx:108-114`).

| Trigger | What re-runs | What does not |
|---|---|---|
| App Theme switch | attributes; CSS cascade; `MutationObserver` → `tabStyle` caches cleared and `useThemeKey()` consumers re-render once; `applyMonacoTheme` (one global theme, so diff editors follow) plus clearing semantic tokens; `keepTheme` (localStorage and shell) | no re-tokenization; no re-render of components that do not read the theme |
| OS light/dark change | when `theme === "system"`: the same as a switch, without `keepTheme` (the choice did not change) | under a fixed theme: no attribute mutation; `defineTheme` again (cheap); **no** semantic clear (the palette key is unchanged) |
| Syntax switch or Italic comments | `data-syntax` / `data-comments`; `applyMonacoTheme` plus clear; maybe `refreshEditorOptions`. Syntax also runs `keepTheme` (localStorage only: the shell text has no syntax, so `told` dedupes it) | no CSS change, no observers |
| Settings load (`boot()`, `App.tsx:73`) | `syncDocumentTheme()` with the loaded choice | the cached choice was shown before |
| `initMonaco()` (`monaco.ts:31`) | `applyMonacoTheme()` from the attributes `boot-theme.js` set, including `data-syntax` (V8) | |

### 4.5 `dataset.*` attributes: what each names
| Attribute | Value | Written by | Read by |
|---|---|---|---|
| `data-theme` | the mode: `dark` \| `light` (unchanged meaning) | `boot-theme.js`, `syncDocumentTheme`, `withDocumentTheme` (export) | `monaco.ts` (through `resolveDocumentAppTheme`), `charts/theme.ts:23`, `tabStyle.ts:124,137`, `SyntaxThemePicker.tsx:8,11`, `commands.ts:184`, `exportNotebook.ts:118`, `PlanView.tsx:206` (through `useThemeMode`), `boot-splash.css`, `smoke-ui.mjs` |
| `data-app-theme` (new) | the resolved App Theme id | `boot-theme.js` (valid cache only), `syncDocumentTheme`, `withDocumentTheme` | the CSS selector (§2.2), `resolveDocumentAppTheme`, the `tabStyle` observer, the pickers |
| `data-syntax` | the Syntax colors setting: `default` \| a legacy id | `boot-theme.js` (V8), `syncDocumentTheme` | `applyMonacoTheme` |
| `data-comments` (new) | `upright` when Italic comments is off; absent otherwise | `syncDocumentTheme` | `applyMonacoTheme`, the pickers' previews |
| inline `--boot-bg/-fg/-muted/-idle/-track` on `<html>` | colours of the shown App Theme | `boot-theme.js`, `syncDocumentTheme` | `boot-splash.css` only |

**Theme-keyed caches must watch both theme attributes.**
- `tabStyle.ts:124`: `attributeFilter: ["data-theme", "data-app-theme"]`; the snapshot at `:137` becomes
  `` `${theme}|${appTheme}` ``.
- New `useThemeMode()` for `PlanView.tsx:206`, which compares the key with `"light"`.
- `ResultGrid.tsx:240-241` (keyed on the Theme setting) → `useThemeKey()`.
- `SessionsPane.tsx:31` (`useMemo(gridTheme, [])`) → `[useThemeKey()]`.
- `SyntaxThemePicker.tsx:6-11` watches both, plus `data-comments`.
- `ErdTab.tsx:173` and `OverviewStrip.tsx:49` read at draw time, so they need nothing.
- `exportNotebook.ts:116-125` becomes `withDocumentTheme({ mode: "light", appTheme: "light" }, draw)`. It sets and
  restores both attributes, so exported charts are Querithm Light on white.

### 4.6 Fallbacks for unknown ids, by layer
| Layer | Unknown / invalid | Result |
|---|---|---|
| store (`clean`) | `darkTheme`, `lightTheme`, `syntaxColors`, `theme` | display `"dark"`, `"light"`, `"default"`, `"dark"`; the stored value is kept unless changed (§3.5) |
| document | `data-app-theme` unknown, wrong mode, or styles not installed | CSS: the mode's legacy block; TS: `LEGACY_APP_THEME[mode]` |
| syntax | `data-syntax` unknown or missing | Default of the resolved App Theme |
| boot cache | bad JSON, `v !== 1`, a field of the wrong type or format | that field (or mode entry) ignored: the legacy look for the mode until the settings load |
| shell file | not the §6.3 grammar | `DARK_BG` (`theme.rs:51-57`) |

Ids are stable from a theme's first release. A removed theme degrades through every row above, and T11 snapshots the
id list.

---

## 5. Tokenizer and semantic tokens

### 5.1 What Monarch can safely do, and what it cannot
Monarch (`languages.ts`) is the existing highlighting tokenizer. It may decide a token kind **from the lexeme alone**:
word lists, sigils, operator spelling.

Context decisions are different: "this identifier is a CTE" or "this part is a schema" would be a semantic
classification by regex over SQL (CLAUDE §2). Monarch also cannot make them reliably: lines carry only a state stack,
a `WITH` spans lines, and names are resolved later in the statement. These go to the token/AST layer (§5.3).

### 5.2 Lexical token kinds per dialect (Monarch)
The shared `wordCases` (`languages.ts:243`) and the inline word cases (`:83-90`, `:149-156`, `:196-199`) become one
per-dialect builder. Cases are tried in this order (Monarch takes the first match):
```
"@constantKeywords" → keyword.constant   (constants ∩ keywords)
"@wordOperators"    → keyword.operator   (must be ⊆ keywords: the legacy fallback "keyword" is then exact)
"@keywords"         → keyword
"@typeKeywords"     → type
"@builtinFunctions" → predefined
"@constantIdents"   → identifier.constant (constants \ keywords: only words that were `identifier` before)
"@default"          → identifier
```

| Dialect (postfix) | Constants | Word operators (`AND OR NOT IN LIKE BETWEEN IS` +) | Bind parameters | Other |
|---|---|---|---|---|
| T-SQL `.tsql` | `NULL` (keyword) | — | — (`@x` stays `variable`, `:71`) | `@@x` → `predefined.global` (was `predefined`, `:70`) |
| PostgreSQL `.pgsql` | `TRUE FALSE NULL` (keywords) | `ILIKE SIMILAR ISNULL NOTNULL` | `$1` (`:138`), `:name` (`:140`) → `variable.bind` | `::type` (`:139`) becomes `[/(::)(\s*)([\w"]+(?:\[\])?)/, ["type.cast", "type", "type"]]`; dollar-body words (`:196-199`) use the same lists |
| SQLite `.sqlite` | `NULL` (keyword); `TRUE FALSE` → `identifier.constant` | `GLOB REGEXP MATCH ISNULL NOTNULL` | `?`, `?N`, `:x`, `@x`, `$x` (`:259-260`) → `variable.bind` | — |
| MySQL `.mysql` | `TRUE FALSE NULL` (keywords) | `XOR DIV MOD REGEXP RLIKE` | `?` → `delimiter.bind` (new rule; today untokenized fg) | `@@x` → `predefined.global` (`:329`); `@x` stays `variable` (user variable, `:330`) |
| DuckDB `.duckdb` | `TRUE FALSE NULL` (keywords) | `ILIKE GLOB SIMILAR` | `$1`, `$name`, `?` (`:482-484`) → `variable.bind` | `::type` split as for PG (`:485`) |
| Oracle `.oracle` | `NULL` (keyword); `TRUE FALSE` → `identifier.constant` (PL/SQL BOOLEAN) | — | `:name`, `:1`, `:"x"` (`:603-604`) → `variable.bind` | — |

- **Accepted lexical outcomes.** `NOT NULL` in DDL reads as operator + constant. PL/pgSQL `FOR r IN` and PG
  `(LIKE other)` take the operator colour.
- **No other consumers.** Nothing reads Monarch token types outside tests. `autoClosingPairs notIn` keys on "string" and
  "comment", which do not change.

### 5.3 Semantic kinds: CTE names and schema prefixes, from the webview's tier-1 Analyzer
**Where.** A new export, `semanticSpans(text, dialect, from, to, schema?)` in `src/editor/completion/scanner.ts`,
re-exported from `completion/index.ts`. It is built on:
- `getStream` (`lexer.ts:591`, incremental and cached);
- `locateStatement` (`scanner.ts:287`) and `Splitter` (`:86`);
- the private `Analyzer` (`:582`): `parseCtes` (`:771-812`) and `collectRefs` (`:1070`), with `parseRef`'s name chain
  (`:990-1030`) and the CTE matching that already ignores qualified names (`:1158-1162`).

**Why not the sidecar.**
- Tier 1 already parses CTEs and table references synchronously, for all six dialects, for completion (§9 tier 1). The
  sidecar is uneven: scope builders for T-SQL and PG; mostly tokenizers for MySQL, SQLite, DuckDB and Oracle.
- No IPC per edit and no new RPC; it works disconnected.
- Completion and colouring agree on what a CTE is.
- It is token/AST-based, not regex (CLAUDE §2). **Contract change: none.**

**Classification (v1)**, per statement overlapping `[from, to]`:
- **`cte`:**
  1. each definition name (`WITH [RECURSIVE] name [(cols)] AS (`), with the token index recorded by `parseCtes`;
  2. each unqualified table reference that resolves to a CTE (`Ref.isCte`);
  3. a qualifier `name.` in an expression, when `name` is such a reference written without an alias.
- **`schema`:** every non-final part of a dotted name in an **object position**:
  - Analyzer table refs (FROM, JOIN, APPLY, UPDATE, INSERT INTO, DELETE FROM, MERGE INTO, USING), with `parseRef`
    recording `partIdx[]`;
  - DDL headers (`CREATE | ALTER | DROP [OR REPLACE] <kind> a.b`) and `TRUNCATE TABLE a.b`;
  - `EXEC`, `EXECUTE` and `CALL` targets (T-SQL, PG, MySQL, DuckDB, SQLite).
- **Catalog-confirmed only:** Oracle 2-part EXEC/CALL targets (schema or package?) and `a.b(` calls. They are coloured
  only when the part is in the connection's cached `SchemaIndex.schemas` / `.databases` (`completion/types.ts:68-90`,
  via `docContext(model).schema`, `providers.ts:44-61`).
- **Never coloured:** aliases, columns, strings, comments, Oracle `name@link`.
- **PG `$$` bodies** after `AS` / `DO` are analysed as their own script, as `scanBody` does (`scanner.ts:1702`). Monarch
  paints them as code (`languages.ts:187-201`). DuckDB's `$$` stays a string (`:522-526`).
- **v1 limits:** a `WITH` that is not at the start of its statement (PG `INSERT … WITH`, a `(WITH …)` subquery), and
  spans that cross a line (Monaco semantic tokens cannot span lines) are skipped.

**Provider** (`src/editor/semanticTokens.ts`, new, registered by `installProviders`, `providers.ts:618`):
```ts
const legend = { tokenTypes: ["identifier"], tokenModifiers: ["cte", "schema"] };
// Styled by _match([type, ...modifiers].join(".")) (standaloneThemeService.js:137-139): "identifier.cte" /
// "identifier.schema", the TOKEN_ROLES rules of §4.2; if a rule is missing, "identifier" applies.
monaco.languages.registerDocumentRangeSemanticTokensProvider(lang, {
  getLegend: () => legend,
  onDidChange: changed.event,   // fired when useSnapshots entries change (catalog-confirmed parts), as providers.ts:629
  provideDocumentRangeSemanticTokens(model, range) {
    if (!semanticEnabled()) return null;
    const ctx = docContext(model); if (!ctx) return null;
    const spans = semanticSpans(model.getValue(), ctx.dialect, model.getOffsetAt(range.getStartPosition()), model.getOffsetAt(range.getEndPosition()), ctx.schema);
    return { data: encodeRelative(model, spans) };   // [Δline, Δchar, length, 0, 1 << modifier] per span
  },
});
```
- **Gating.** The provider is asked only while `semanticHighlighting.enabled` is true, which means Default on any App
  Theme (C1). The six syntax themes never enable it.
- **Why gating matters.** A semantic token always overrides the Monarch style (`standaloneThemeService.js:137-148`).
  Under Standard, a CTE named `source` (a T-SQL keyword, `languages.ts:17`) would otherwise lose its bold keyword look.
  Under Default, overriding it is the intent.
- **Stale spans after a palette change** are dropped (V7, §4.3).
- **Performance (CLAUDE §4, I9).**
  - Never on the keystroke path: viewport requests run in a `RunOnceScheduler` debounced 100–500 ms
    (`viewportSemanticTokens.js:35-36`) and are cancelled on the next edit (`:49-52`).
  - No React state is involved.
  - Per call: one `model.getValue()` (completion already does it) plus incremental `getStream`. Only statements
    overlapping the viewport are analysed, at most 200 per call.
  - Because Default is now on for nearly every user, the budget matters. §14 row 31: warm p95 ≤ 4 ms per call; first
    call ≤ 30 ms; metric 3 unchanged.

### 5.4 Other Monaco editors
- The diff and dialog editors (`Dialogs.tsx:416,633`, `SchemaCompareTab.tsx:263,323`, `RenameDialog.tsx:173`,
  `GridDialogs.tsx:14`) share the one global theme, so they get the lexical colours and the themed chrome.
- They also share the global semantic configuration (§4.3), so their SQL models get CTE and schema colours too, whenever
  `docContext` resolves for the model. Otherwise the provider returns `null`: lexical colours only.

---

## 6. Start screen and shell

### 6.1 What each layer shows over time
| Moment | Layer | Today | New |
|---|---|---|---|
| t0 window shown, before the webview paints | Rust `theme::apply` (`main.rs:44-46`) | `DARK_BG` / `LIGHT_BG` from a one-word file (`theme.rs:21-22,51-57`) | the kept **colour** of the dark or light pick, chosen by mode or OS (§6.3) |
| t1 first frame | `boot-theme.js` + `boot-splash.css` | `data-theme` from `querithm.theme`; splash in two hard-coded palettes | also `data-app-theme`, `data-syntax` and `--boot-*` from `querithm.themeBoot`; splash and `html` use them, with today's hexes as fallbacks |
| t2 bundle CSS applied (render-blocking) | `tokens.css` | the legacy tokens | the same; new-theme tokens are absent until t3, under the splash |
| t3 `main.tsx` runs | `installAppThemeStyles()` | — | every new-theme block present before the first React render |
| t4 settings loaded | `syncDocumentTheme` | the setting replaces the cache | the same, for mode, picks, syntax and comments; `keepTheme` refreshes the caches |
| t5 ready | the splash fades (`splash.ts`); migration notice (§3.6) | | |

### 6.2 Webview boot cache (`src/app/theme.ts`)
- **`querithm.theme`** (`THEME_CACHE`, `theme.ts:15`) keeps its exact meaning: one of the three words.
- **`querithm.themeBoot`** (new, `THEME_BOOT_CACHE`) holds
  `{"v":1,"dark":BootColors,"light":BootColors,"syntax":"<choice>"}`, where
  `BootColors = { id, bg, fg, muted, idle, track }`.
  - Both picks are stored, so `"system"` resolves fresh at the next start.
  - `syntax` is optional (V8) and needs no `v` bump.
- **Writes.** `keepTheme(choice)` writes both keys, only once the saved settings have loaded (the gate at
  `App.tsx:107`), and each only when its value differs.
- **Reads.** `cachedThemeChoice(): Partial<ThemeChoice & { syntaxColors: SyntaxChoice }>` returns:
  - the word from `THEME_CACHE`;
  - the picks, only if they are ids of the right mode;
  - `syntaxColors`, only if it is in `SYNTAX_CHOICES`.

  Storage that throws counts as nothing kept (`theme.ts:21-26`).
- **Shell writes: single flight, latest value only.** Today's fire-and-forget call (`theme.ts:44-48`) can race when
  arrow keys step through the theme cards.
  ```ts
  let told: string | null = null, wanted: string | null = null, pumping = false;
  function tellShell(text: string) { wanted = text; if (!pumping) void pump(); }
  async function pump() {
    pumping = true;
    while (wanted !== null && wanted !== told) {
      const t = wanted;
      try { await withTimeout(shell.themeKeep(t), 2000); told = t; }   // bounded wait (CLAUDE §4)
      catch { told = null; break; }                                     // told again at the next change or start
    }
    pumping = false;
  }
  // text = `${choice.theme} ${dark.ui.bg} ${light.ui.bg}`, e.g. "system #rrggbb #rrggbb"
  ```
- **`public/boot-theme.js`** stays ES5 and in its own file (CSP):
  ```js
  var word = null, boot = null;
  try { word = localStorage.getItem("querithm.theme"); boot = JSON.parse(localStorage.getItem("querithm.themeBoot") || "null"); } catch (e) {}
  var dark = word === "light" ? false : word === "system" ? window.matchMedia("(prefers-color-scheme: dark)").matches : true;
  var root = document.documentElement, mode = dark ? "dark" : "light";
  root.setAttribute("data-theme", mode);
  var ok1 = boot && typeof boot === "object" && boot.v === 1, p = ok1 ? boot[mode] : null, hex = /^#[0-9a-f]{6}$/i;
  function ok(c) { return typeof c === "string" && hex.test(c); }
  // typeof first: /^[a-z]…$/.test(undefined) tests the string "undefined", which would pass.
  if (p && typeof p.id === "string" && /^[a-z][a-z0-9-]{0,31}$/.test(p.id) && ok(p.bg) && ok(p.fg) && ok(p.muted) && ok(p.idle) && ok(p.track)) {
    root.setAttribute("data-app-theme", p.id);
    root.style.setProperty("--boot-bg", p.bg); /* … --boot-fg, --boot-muted, --boot-idle, --boot-track */
  }
  if (ok1 && typeof boot.syntax === "string" && /^[a-z][a-z-]{0,23}$/.test(boot.syntax)) root.setAttribute("data-syntax", boot.syntax);
  ```
  - CSSOM `style.setProperty` is not subject to CSP inline checks.
  - The script needs no theme list: an id with no block falls back (I4), and an unknown syntax resolves to Default
    (§4.6).
- **`public/boot-splash.css`** uses each legacy hex as the fallback of a `var()`:
  - `html { background: var(--boot-bg, #0f1117) }` and `html[data-theme="light"] { background: var(--boot-bg, #ffffff) }`;
  - `#splash { --s-bg: var(--boot-bg, #0f1117); --s-fg: var(--boot-fg, #e6edf3); --s-muted: var(--boot-muted, #8c949e); --s-idle: var(--boot-idle, #1e222e); --s-track: var(--boot-track, #1b1f2a); … }`;
  - the light rule likewise.

  The mark's gradient stays per mode. `syncDocumentTheme` keeps `--boot-*` current, for the splash shown again by
  Restart engine (`App.tsx:146-151`).

### 6.3 Rust shell (`src-tauri/src/theme.rs`): colours, never ids
```rust
pub const DARK_BG / LIGHT_BG: unchanged (Querithm Dark / Light; defaults for a missing or one-word file)
pub enum Mode { Dark, Light, System }
pub struct Kept { pub mode: Mode, pub dark: (u8, u8, u8), pub light: (u8, u8, u8) }
/// "<dark|light|system>" (files written before App Themes) or "<word> #rrggbb #rrggbb"; anything else is None.
pub fn parse(text: &str) -> Option<Kept> {
    if text.len() > 64 { return None; }
    let mut it = text.split_ascii_whitespace();
    let mode = match it.next()? { "dark" => Mode::Dark, "light" => Mode::Light, "system" => Mode::System, _ => return None };
    match (it.next(), it.next(), it.next()) {
        (None, None, None) => Some(Kept { mode, dark: DARK_BG, light: LIGHT_BG }),
        (Some(d), Some(l), None) => Some(Kept { mode, dark: rgb(d)?, light: rgb(l)? }),
        _ => None,
    }
}
fn rgb(s: &str) -> Option<(u8, u8, u8)>        // exactly '#' + 6 ASCII hex digits
fn serialize(k: &Kept) -> String              // "<word> #rrggbb #rrggbb", from the parsed values only
pub fn background(kept: Option<Kept>, os_dark: bool) -> (u8, u8, u8) // None → DARK_BG; Dark → dark; Light → light; System → os
#[tauri::command] pub async fn app_theme_keep(app, theme: String)    // parse(&theme) before any std::fs; writes serialize(&kept)
```
- The command name and argument name stay (`main.rs:76`). `startScreen.test.ts:85-93` keeps its structure (parse
  before `std::fs::`, the parsed value written) and asserts `serialize(&kept)`.
- `apply()` (`theme.rs:66-74`) asks the OS only for `Mode::System`.
- **Size.** About 50 more lines of Rust, and no change for any later theme (I8). An id → colour table would grow with
  every theme, and CLAUDE §1 says to keep `src-tauri` smaller.
- `src/ipc/client.ts:106` takes a `string`. Tauri commands are not in `Querithm.Contract`, so `gen:contract` is not
  needed.
- **Downgrade.** An older shell rejects the three-field file and starts dark for one frame. Its webview then writes one
  word, which this shell reads back with the legacy colours.

---

## 7. Settings UI (Appearance › Theme)

### 7.1 Color theme row (`registry.ts:68-73` → `{ kind: "appTheme" }`, keys `["theme","darkTheme","lightTheme"]`)
`src/settings/AppThemePicker.tsx` (new), modelled on GitHub's Single theme / Sync with system.
- **Theme mode** is a segmented control: "Single theme | Match system" (radios).
  - Turning Match system on: `set({ theme: "system" })`.
  - Turning it off: `set({ theme: <mode shown now> })`.
- **Single theme:** one radiogroup, "Color theme", with 9 cards in two labelled sections, Dark (6) and Light (3).
  - The checked card is `theme === "dark" ? darkTheme : lightTheme`.
  - A card sets `{ theme: card.mode, [card.mode === "dark" ? "darkTheme" : "lightTheme"]: card.id }`. The other pick is
    kept, so the toggle command (`commands.ts:183-185`) still works.
- **Match system:** two radiogroups, "When the system is dark" (6) and "When the system is light" (3). A card sets only
  that mode's pick; the theme shown right now gets the badge **In use**.
- **Invariant:** the theme in use is always one of the two picks.
- **Card:** `data-app-theme-card={id}`, with the name and `description` (the comfort line, ≤ 90 chars).
  - Its mini window is drawn **only with inline styles from TS data**: a panel strip with an accent-underlined tab, a
    `muted` gutter, a `panel` current-line band, and three SQL lines in the theme's Default palette and font styles
    (bold word operators; upright comments when `data-comments="upright"`).
  - The card border and ring use the active theme's tokens (`SyntaxThemePicker.tsx:41`).
  - Badges: "Default" on Querithm Dark; "In use".
- **Help:** a "Which theme should I use?" disclosure (`<details>`) under the cards. It holds the Eye Doctor's help
  bullets (night, daylight, astigmatism, light sensitivity, Querithm Light users, red-green colour vision, a suggested
  Match system pair) from `src/settings/themeGuidance.ts`, as plain text. Theme names in it are checked against
  `APP_THEMES` (T11), so a rename cannot leave a dead name.
- **Keyboard:** native radios, as at `SyntaxThemePicker.tsx:31-40`. Arrow keys move and apply at once; shell writes are
  coalesced (§6.2).
- **Reset:** `defaultsOf` resets all three keys (`registry.ts:277-279`), giving Single theme, Querithm Dark.
  `defaultText` is "Querithm Dark". Theme names join the search keywords (`registry.ts:72`).

### 7.2 Syntax colors row (`registry.ts:74-79`: id and keys → `syntaxColors`; control kind `syntaxTheme` kept)
- `SyntaxThemePicker` (`SyntaxThemePicker.tsx:19-72`) lists **Default first**, with the "Default" badge (moved from
  Standard, `:44`), then the six themes.
- **Default card:** its preview is `resolveSyntax("default", resolveDocumentAppTheme())`, updated live by the observer
  on `data-theme`, `data-app-theme` and `data-comments`.
  - Description on a new theme: "Follows the color theme: {app.name}'s own colors."
  - On Querithm Dark / Light: "Standard's colors, plus their own for NULL, CTE names and schema prefixes."
  - Both ≤ 90 chars.
- **The six cards:** their palette in the current mode, on `bg-bg` (the active App Theme's background).
- **`PREVIEW`** (`syntaxTheme.ts:189-209`) gains one line: `WITH recent AS (SELECT … FROM sales.Orders … IS NOT NULL)`.
  Every token in it has a `role` plus a `legacy`, so the six cards show what the editor shows.
- **Row description:** "How SQL is colored in the editor. Default uses the color theme's own colors; the others keep
  their colors on every theme."
- `SettingRow.tsx:45-46` (`defaultText`) returns "Default". `SettingRow.tsx:125-135` calls `set({ syntaxColors: v })`,
  and `appTheme` is `wide` too.

### 7.3 Italic comments row (new, Appearance › Theme)
- `{ id: "italicComments", kind: "switch" }`, default on.
- Description: "Comments in the SQL editor are italic. Upright comments can be easier to read with astigmatism or
  dyslexia."
- Keywords: italic, comments, astigmatism, dyslexia.
- It applies to every palette (the six themes included), as an explicit user choice. With the default (on), Standard
  is unchanged (I1).

### 7.4 Migration notice
The one-time toast of §3.6 (non-modal) with its **Use Standard** action.

---

## 8. File-by-file change list

**New files**
| Path | Contents |
|---|---|
| `src/theme/appThemes.ts` | §2.3: types, `UI_TOKENS` (30), `DERIVED`, `WARN_MARK_FALLBACK`, `FAMILY_TOKENS`, `APP_THEMES` (9, each with its own Default `sql`), id lists, `LEGACY_APP_THEME`, `appThemeById`, `uiVars`, `tokenVars`, `appThemeCss`, `bootColors` |
| `src/theme/resolve.ts` | §4.1: `resolveAppTheme`, `resolveDocumentAppTheme`, `resolveSyntax` (V3 bold, upright comments), `ThemeChoice` |
| `src/editor/semanticTokens.ts` | §5.3: legend, range provider, `encodeRelative`, `onDidChange` wired to `useSnapshots` |
| `src/settings/AppThemePicker.tsx` | §7.1 |
| `src/settings/themeGuidance.ts` | the help text of §7.1 (plain text) |
| `src/theme/__tests__/appThemes.test.ts`, `src/theme/__tests__/resolve.test.ts` | T3, T5, T10, T11, T12 |
| `src/editor/__tests__/highlightGolden.test.ts` + `fixtures/highlight/{tsql,pgsql,sqlite,mysql,duckdb,oracle}.sql` + `__snapshots__/` | T1 |
| `src/editor/__tests__/editorChrome.test.ts` | T8 overlay, mark and pair rules (pure colour maths over `APP_THEMES` and `SYNTAX_THEMES`) |
| `src/editor/__tests__/semanticTokens.test.ts`, `src/editor/completion/__tests__/semantic.test.ts` (+ perf) | §5.3 |
| `src/store/__tests__/settings.themes.test.ts` | R1–R14, N1 |
| `src/settings/__tests__/AppThemePicker.test.tsx` | §7 |

**Changed files**
| Path | Change |
|---|---|
| `src/editor/syntaxTheme.ts` | roles and palette type (§2.4); `TOKEN_ROLES` gains 9 entries with `legacy`; `effectiveRole`, `rulesFor`; `syntaxRules` becomes a wrapper; `SYNTAX_CHOICES`, `SyntaxChoice`, `DEFAULT_SYNTAX_CHOICE`; a `PREVIEW` line with `legacy`. **`SYNTAX_THEMES` unchanged** |
| `src/editor/languages.ts` | per-dialect `constantKeywords`, `constantIdents`, `wordOperators` and `cases` (§5.2); `@@` → `predefined.global`; binds → `variable.bind`; MySQL `?` → `delimiter.bind`; `::` split; one word-case builder replacing `:243` and the inline cases (`:83-90`, `:149-156`, `:196-199`) |
| `src/editor/monaco.ts` | `applyMonacoTheme` (§4.3): resolved app theme and syntax, the chrome colours (R1, R2, V4, V5), the `--q-info` / `--q-warn-mark` fallbacks, the V7 clear keyed on the palette; `semanticEnabled()` |
| `src/editor/monacoInternals.ts` | `clearSemanticTokens(model)` (V7) |
| `src/editor/editors.ts:131` | `"semanticHighlighting.enabled": semanticEnabled()` |
| `src/editor/providers.ts:618-633` | `installSemanticTokens()` per language |
| `src/editor/completion/scanner.ts` | `Ref.partIdx` (`parseRef`, `:990-1030`), the CTE token index (`parseCtes`), export `semanticSpans`; re-export from `completion/index.ts` |
| `src/store/settings.ts` | fields, defaults, `choices` (§3.1); `sanitizeSettings` with migration, adoption and `legacySyntaxMirror` (§3.2); dirty-key `persist()` replacing `extra` and `changedUnread` (§3.5); a failed load seeded from the cache; after load, flush plus the notice flag |
| `src/store/ui.ts:7-13`, `src/app/Palette.tsx:205` | `Toast.action`, and its button |
| `src/app/theme.ts` | `THEME_BOOT_CACHE` (with `syntax`), `cachedThemeChoice`, `keepTheme(choice)` with single-flight shell writes, `installAppThemeStyles` plus its flag, `withDocumentTheme`, `osPrefersDark`, `notifySyntaxMigration`; `cachedTheme` and `isDark` kept |
| `src/app/App.tsx:44-52,86,100-125` | `choiceToShow`, `syncDocumentTheme` (§4.4), primitive selectors, effect dependencies; notice after `splashStage(4)` |
| `src/main.tsx:9` | `installAppThemeStyles()` before `createRoot` |
| `public/boot-theme.js`, `public/boot-splash.css` | §6.2 |
| `src-tauri/src/theme.rs` | §6.3, with unit tests |
| `src/ipc/client.ts:106` | `themeKeep: (kept: string) => …` |
| `src/app/tabStyle.ts:124,137` | observe both attributes; key `theme\|appTheme`; `useThemeMode()` |
| `src/plans/PlanView.tsx:206` | `colorMode` from `useThemeMode()` |
| `src/results/ResultGrid.tsx:240-241` | `const themeKey = useThemeKey()` |
| `src/monitor/SessionsPane.tsx:31` | `useMemo(gridTheme, [useThemeKey()])` |
| `src/notebook/exportNotebook.ts:116-125` | `withDocumentTheme({ mode: "light", appTheme: "light" }, …)` |
| `src/settings/SyntaxThemePicker.tsx` | Default card, an observer on 3 attributes, `SyntaxChoice`, a legacy-aware preview |
| `src/settings/registry.ts:29-36,68-79` | the `appTheme` control; the `syntaxColors` row; the `italicComments` row; keywords |
| `src/settings/SettingRow.tsx:10-11,45-46,125-135` | render `appTheme`; `syntaxColors`; `defaultText` |
| `src/theme/tokens.css` | **only `:1110-1133`** (V6 fills to 12 %). The `@theme` block and the legacy token blocks are unchanged (T3 digest) |
| `scripts/bench/smoke-ui.mjs:103-118,125-155` | first frame for a kept new dark theme and a kept new light theme (bg = its `ui.bg`); one layout audit per new App Theme on the main screen, results and Settings › Appearance (both attributes set); find / squiggle screenshot |
| tests | `syntaxTheme.test.ts`, `contrast.test.ts`, `theme.test.ts`, `startScreen.test.ts`, `monaco.test.ts`, `monacoInternals.test.ts`, `App.test.tsx`, `misc.test.ts:194-207,260-272,291` (dirty-key writes), `SettingsDialog.test.tsx:119-136`, `registry.test.ts:83-85`, `tabStyle.test.ts:197-215`, `draw.test.ts:98-111`, `oracleLanguage.test.ts:60-65` (`variable.bind`) |
| `docs/ARCHITECTURE.md` | §12 paragraph, §14 rows 31–32, §16 rows (§12 of this plan); the release-notes line (§11) |

---

## 9. Test plan

### Golden and Standard-unchanged tests (S0, committed before any other change)
- **T1 Highlight golden** (`highlightGolden.test.ts`).
  - **Corpus**, one per dialect: every lexical form plus the new ones: TRUE/FALSE/NULL, every word operator, `::int`,
    `::"T"[]`, `$1`, `:p`, `?`, `?1`, `@p`, `$x`, `@@x`, MySQL `?`, `WITH recent AS`, `sales.Orders`, PG `$$` bodies,
    and Oracle q-quotes and hints. It is seeded from `plans/app-themes/stress.sql` and every Monarch rule.
  - **Method.** Run the real `compile` and `MonarchTokenizer` (as `oracleLanguage.test.ts:3-33` does). Style each
    character with Monaco's real `TokenTheme.createFromRawTokenTheme([...baseRules, { token: "", foreground: fg },
    ...rules])` (`tokenization.js:133-148`), composed as `standaloneThemeService.js:109-126` does.
  - **Snapshot:** runs of `start-end fg fontStyle` for **6 syntax themes × 2 modes × 6 dialects**. It must stay
    byte-identical through every later slice.
  - **Added in S5:** Default on Querithm Dark/Light produces the same runs as Standard for every character whose token's
    effective role is not one of constant, wordOperator, cast, bind, systemVariable, cte or schema.
- **T2 Frozen literal.**
  - `SYNTAX_THEMES` deep-equals a frozen copy: 6 ids, all hexes and font styles.
  - `syntaxRules(t, mode, fg)` restricted to the 16 pre-existing tokens deep-equals the frozen rule list for all six.
  - For each legacy App Theme: `sql` has the 9 base roles and 3 brackets deep-equal to `SYNTAX_THEMES[0][mode]`, and
    all 7 new roles are defined; `resolveSyntax("default", t).semantic === true`.
- **T9 Coverage** (extends `syntaxTheme.test.ts:91-109` from 3 dialects to **all 6**):
  - every emitted token has a rule, and our rules out-rank `vs`, `vs-dark` and `hc-black`;
  - each `legacy` equals the role of the longest pre-existing prefix;
  - per dialect: `wordOperators ⊆ keywords`, `constantKeywords ⊆ keywords`, `constantIdents ∩ keywords = ∅`.

### Theme data and resolution
- **T3 Mirror and completeness.**
  - Legacy `uiVars(t)` without `--q-info` equals the parsed `tokens.css` block, and `t.ui.info === t.ui.accent`.
  - Each legacy effective set = the 61 names; each new theme = the 61 names plus `--q-info` (plus `--q-warn-mark` when
    set).
  - The legacy blocks and the `@theme` block hash-equal a committed digest.
- **T10 Generated CSS.**
  - `appThemeCss()` parses back to `tokenVars`.
  - Selectors are `:root[data-theme="<mode>"][data-app-theme="<id>"]` with `color-scheme`.
  - Values match `^#[0-9a-f]{6}$` or `^rgba\(\d{1,3}, \d{1,3}, \d{1,3}, (0|1|0?\.\d+)\)$`.
  - Installing twice leaves one element.
- **T11 Ids and text.**
  - A snapshot of the ids, taken at the first release, with `glacier` renamed before it.
  - Kebab case; names ≤ 18; descriptions 20–90 characters; 6 dark and 3 light.
  - Every theme name mentioned in `themeGuidance.ts` exists in `APP_THEMES`.
- **T12 Eye comfort and distinctness (repo rules from the brief).**
  - Background: dark L 0.16–0.30; light L 0.93–0.985.
  - Text: fg/bg 10–15.5 (dark) and 10–16 (light); dark fg L ≤ 0.94; no `#000000` / `#ffffff` surface or fg in new
    themes.
  - Dark backgrounds pairwise ΔE00 ≥ 6, and not the same undertone.
  - Accents and keywords pairwise ΔE ≥ 15.
  - Mean SQL ΔE ≥ 15 between any two Default palettes and Standard, **skipping the pair (legacy Default, Standard)**.
  - **New (Eye):** dark Default comments APCA |Lc| ≥ 47.5 on bg and ≥ 46.5 on the current line. Querithm Dark itself
    is 47.8 / 47.0. This needs `apca()` added to `src/editor/__tests__/colorScience.ts`.
- **T5 `resolve.test.ts`.**
  - `resolveAppTheme` table: 3 modes × OS dark/light × valid, unknown, wrong-mode and `undefined` picks.
  - `resolveDocumentAppTheme`: a mismatched mode, an unknown id or missing styles give legacy.
  - `resolveSyntax`:
    - legacy id × App Theme → `legacy[app.mode]`;
    - `"default"`, unknown or `undefined` → `app.sql`, with `fontStyle.wordOperator === "bold"`;
    - `semantic` true exactly for Default;
    - `uprightComments` removes the comment italic from both kinds.

### Contrast and chrome
- **T7.** In `contrast.test.ts:20`, `themes` becomes all 9: legacy CSS vars overlaid with `tokenVars`. `--q-info`
  follows accent for legacy themes, as Monaco does.
  - Every `it.each(Object.entries(themes))` then covers every theme. The diff-tint test (`:231-234`) iterates the six
    themes plus that theme's Default.
  - `env-prod` hue stays red (OKLCH h 10–40).
  - `draw.test.ts`:
    - legacy chart slots stay exact;
    - each dark theme's slots are ≥ 3:1 on its bg and panel;
    - light themes keep Querithm Light's three-slot exception;
    - overrides keep adjacent CVD ΔE ≥ 8.4 and normal ΔE ≥ 19.3.
- **T8 Syntax readability and editor chrome** (`syntaxTheme.test.ts` plus `editorChrome.test.ts`).
  - **Persistent layers:**
    - each Default palette on its own theme's six layers, ≥ 4.5 for every role;
    - [cross]: each of the six palettes × each App Theme of its mode, ≥ 4.5.
  - **Separation:**
    - all pairs ≥ 12, except colours merged on purpose;
    - key pairs ≥ 15, and ≥ 8 under CVD;
    - ANCHORED for keyword and comment;
    - redDistance ≥ 10 from that theme's err, err-solid and env-prod;
    - dark chroma ≤ 0.17; comment quieter than ident by ≥ 1.5 contrast.
  - **Co-located pairs** (Default palettes): constant/operator, constant/function and cte/schema ΔE ≥ 15 in normal
    vision and ≥ 5 under each of protan, deutan and tritan; **cte/ident ≥ 18**.
  - **Transient overlays.** Default palettes are tested on their own theme; the six palettes on each App Theme of their
    mode.

    | Layer | Composition | Default palette floor | Six-palette floor |
    |---|---|---|---|
    | find match | warn 10 % over bg, and over the current line | non-comment ≥ 4.5, comment ≥ 4.0 | non-comment ≥ 4.3, comment ≥ 4.0 |
    | word highlight | fg 9 % over bg, and over the current line | ident, cte, schema, variable, bind, temp ≥ 4.5 | ident, variable ≥ 4.5 |
    | decorations (V6) | accent / warn / err / env-prod 12 % over bg | non-comment ≥ 4.5, comment ≥ 4.0 | non-comment ≥ 4.3, comment ≥ 4.0 |

    The six-palette floors must be confirmed in S1 with `da2-overlays.mjs` extended to the six palettes. A failing pair
    moves the App Theme's token, never the frozen syntax theme.
  - **Non-text marks:**
    - the find border (warn) ≥ 3:1 on bg and on the current line;
    - the find layer ≥ ΔE 8 from the selection;
    - squiggle marks err, warn mark (`--q-warn-mark` or `WARN_MARK_FALLBACK[mode]`) and info (`--q-info` or accent)
      ≥ 3:1 on bg, current line, selection, and selection + current line;
    - each pair among the three marks ≥ ΔE 8 in normal vision and under protan, deutan and tritan.

    Measured on the proposals: ≥ 3.24:1 and ≥ ΔE 10.5 (`squig3.mjs`).
  - The UI-only checks (`:207-229`) iterate all 9.
- **`monaco.test.ts`.**
  - With `data-app-theme=<new>` and `data-syntax=default`, the rules carry `<new>.sql`; switching changes them.
  - `standard` on `<new>` gives Standard's colours.
  - A mismatched or unknown app theme gives legacy.
  - The chrome colours (find, squiggles with both fallbacks, word highlight, bracket match, indent guides) are set from
    the CSS variables.
  - `data-comments=upright` gives comment fontStyle `""`.
  - **V7:** after a `data-app-theme` change, `clearSemanticTokens` is called once per model. A second apply with the
    same palette key does not call it.
  - The return value flips exactly when semantic changes.
- **`monacoInternals.test.ts`:** the bundled `tokenizationTextModelPart.js` still defines `setSemanticTokens` and
  `textModel.js` still exposes `tokenization`.

### Persistence round trips (`settings.themes.test.ts`, through `load()`, `set()` and `flushSettings()` with a fake `rpc` store)
- **R1** No saved settings → defaults; nothing written; no notice.
- **R2** `{theme:"system", syntaxTheme:"standard", editorFontSize:15}` loads as `syntaxColors:"default"` and writes once
  at load. The JSON written is the saved object plus `syntaxColors:"default"` and the mirror `syntaxTheme:"standard"`;
  `editorFontSize` is untouched.
- **R3** Reloading R2's JSON gives the same state and does **not** write or notify (idempotent).
- **R4 Explicit Standard sticks:** `set({syntaxColors:"standard"})` → flush → reload → `"standard"`.
- **R5** `{syntaxTheme:"ember"}` → `"ember"`, written once, no notice.
- **R6** `{syntaxTheme:"classic"}` and `{syntaxTheme:42}` → `"default"`.
- **R7** Two cases with an unknown `syntaxColors`:
  - `{syntaxTheme:"neon", syntaxColors:"rainbow"}` → `"neon"` (adopted). Under the mirror contract, a newer build that
    wrote `"rainbow"` also wrote `syntaxTheme:"standard"`. So `"neon"` is a real pick made later in an older build.
  - `{syntaxTheme:"standard", syntaxColors:"rainbow"}` → `"default"` for display, and the stored `"rainbow"` is kept
    unless this session changes it (R14).
- **R8** Every dark and light id round-trips. A wrong-mode, unknown or `null` pick displays its default.
  `set({darkTheme:"<light-id>"})` is ignored.
- **R9** A JSON written by an older build that kept unknown keys,
  `{theme:"dark", syntaxTheme:"standard", syntaxColors:"default", darkTheme:"<id>", futureThing:1}`, restores the picks
  and keeps `futureThing` on the next write.
- **R9b** Adoption:
  - `{syntaxColors:"default", syntaxTheme:"ocean"}` → `"ocean"`;
  - `{syntaxColors:"neon", syntaxTheme:"standard"}` → `"neon"` (a fallback, never a flip);
  - `{syntaxColors:"ember", syntaxTheme:"ember"}` → `"ember"`, no write.
- **R10** `loadFailed`: a `darkTheme` changed while unreadable wins after the re-read, and the other saved keys survive
  (extends `misc.test.ts:194-207`).
- **R11** A failed read seeds theme, picks and syntax from the boot cache. Nothing is written until a read succeeds, and
  `keepTheme` is not called.
- **R12** Boot cache round trip: `keepTheme` → `cachedThemeChoice` gives the same ids and syntax. `boot-theme.js` with
  that cache names the same `data-theme`, `data-app-theme`, `data-syntax` and `--boot-bg` as `resolveAppTheme` and
  `bootColors`.
- **R13 (V11):** two store instances on one fake rpc store. A sets `darkTheme`, then B sets `editorFontSize`. The stored
  object holds both. B's in-memory `darkTheme` is stale, as documented.
- **R14 (V12):** a stored `darkTheme:"future-x"` loads as `"dark"`. After `set({editorFontSize:16})` the stored value is
  still `"future-x"`; after `set({darkTheme:"dark"})` it is `"dark"`.
- **R15** A `set()` during an in-flight write keeps that key dirty, and it is written by the next save (version map).
- **N1 Migration notice:**
  - `{syntaxTheme:"standard"}` → one info toast with the **Use Standard** action, after `splashStage(4)`;
  - the action sets `syntaxColors:"standard"`;
  - a reload of the written blob shows no toast;
  - `{syntaxTheme:"neon"}` migrates without a toast; a fresh install shows none;
  - a failed migration write shows the toast again at the next start.

### App, boot and shell
- **T4 `App.test.tsx`:**
  - the resolution table (`:299-311`) is extended with picks;
  - an OS change under system switches picks;
  - a `darkTheme` change re-applies, with `data-app-theme` updated and `data-theme` untouched;
  - while loading, an observer on both attributes never sees the default (extends `:327-346`);
  - after a failed read, the cache is untouched and `keepTheme` is not called (`:360-373`);
  - Default follows the App Theme: `applyMonacoTheme` runs with the new `data-app-theme`;
  - `data-syntax` is `"default"` by default (`:378-386`);
  - `italicComments:false` sets `data-comments="upright"`.
- **T6 `theme.test.ts`:**
  - `keepTheme` writes both caches and the shell text once per change; a syntax-only change writes localStorage only;
  - single flight: during a pending call only the latest value is written next, and a rejection is retried at the next
    change;
  - `cachedThemeChoice` rejects bad JSON, `v:2`, unknown or wrong-mode ids, bad hex, `id: undefined` and an unknown
    syntax;
  - the `boot-theme.js` table (`:93-114`) is extended to word × OS × cache.
- **`startScreen.test.ts`:**
  - each `var()` fallback hex equals the legacy `--q-bg` and `DARK_BG` / `LIGHT_BG`;
  - `bootColors(t).bg === t.ui.bg` for all 9;
  - the command still runs `parse(&theme)` before `std::fs::` and writes `serialize(&kept)`;
  - there is still no inline `<style>` or `<script>`.
- **Rust `theme.rs` tests:**
  - legacy one-word files and the three-field form are read;
  - rejected: `"dark #12345 #000000"`, `"dark #gggggg #000000"`, `"system #000000 #ffffff extra"`, `"light dark"`,
    more than 64 chars, `"../grants.json"`, NUL;
  - `serialize(parse(x)) == x`;
  - `background` for each mode × OS.

### Settings UI and semantic tokens
- **UI** (`AppThemePicker.test.tsx`, `SettingsDialog.test.tsx`):
  - the Single and Match radiogroups, by name;
  - a card sets the right keys in both modes and in single and match;
  - turning Match off keeps the mode shown; "In use" sits on the resolved theme;
  - reset gives the three defaults;
  - the Default card's preview follows `data-app-theme` and shows bold word operators;
  - Italic comments toggles `data-comments` and the previews;
  - the help disclosure renders;
  - search "dark theme" → `["theme", "syntaxColors"]`.
- **Semantic spans** (`semantic.test.ts`, per dialect × 6):
  - CTE definition, reference and unaliased qualifier;
  - schema parts in FROM, JOIN, INSERT, UPDATE, MERGE, DDL and EXEC;
  - nothing for aliases, columns, strings, comments, or `dbo.orders` shadowing a CTE;
  - Oracle 2-part EXEC only with the catalog;
  - a PG `$$` body.
- **Semantic provider** (`semanticTokens.test.ts`):
  - relative encoding; `null` when disabled;
  - `editorOptions()["semanticHighlighting.enabled"]` is `false` for the six syntax themes and `true` for Default on
    every App Theme.
- **Perf** (§14 row 31): viewport p95 ≤ 4 ms warm, first call ≤ 30 ms, on `perf.test.ts`'s `bigScript()`.

### Gate (CLAUDE §5)
- `pnpm test` (Vitest + `dotnet test`; the sidecar is unchanged) and `cargo test` in `src-tauri`;
- `node scripts/bench/smoke-ui.mjs out/` (first frame, layout per theme, find and squiggle shots);
- `pnpm bench` (metrics 3, 31 and 32);
- no new dependency, so no audit step.

---

## 10. Vertical slices, in order
Every slice ships with its tests green.

| Slice | Content | Visible change |
|---|---|---|
| **S0 Safety net** | T1 golden with today's code (the corpus already holds the new forms), T2 frozen, T9 on all 6 dialects. Commit the snapshots. | none |
| **S1 Editor chrome** | R1, R2, V4, V5, V6 in `applyMonacoTheme` and `tokens.css:1110-1133`; `--q-info` / `--q-warn-mark` fallbacks; T8 overlay and mark rules for Querithm Dark/Light with the six palettes. **Can ship on its own:** it fixes today's unreadable find match and the light warning squiggle. | find, squiggles, word and bracket highlight, decoration fills |
| **S2 Settings persistence** | dirty-key `persist()` (V11, V12), replacing `extra` and `changedUnread`; R10, R13–R15; misc tests updated. A general change. | none |
| **S3 Lexical token kinds** | `languages.ts`, `TOKEN_ROLES` with `legacy`, `rulesFor`; per-dialect tokenizer tests. T1 and T2 stay byte-identical. | none |
| **S4 Semantic infrastructure** | scanner `semanticSpans`, the provider, gating, V7 clearing, perf test and bench `31sem`. No palette colours cte/schema yet, so it is gated off. | none |
| **S5 Theme state and Default** | `appThemes.ts` with the two legacy themes and their Default palettes; `resolve.ts`; settings fields, one-time migration, adoption and mirror (§3.2); notice (§3.6, toast action); `data-app-theme`; `syncDocumentTheme`; observers and keys (`tabStyle`, `ResultGrid`, `SessionsPane`, `PlanView`); `withDocumentTheme`; Default card in `SyntaxThemePicker`; Italic comments row. Tests R1–R9b, N1, T3, T5, App and monaco. | Default on Querithm Dark/Light; notice once |
| **S6 Start screen and shell** | `themeBoot` cache with syntax (V8), single-flight `keepTheme`, `boot-theme.js`, `boot-splash.css`, Rust `Kept`. Tests T6, R11, R12, Rust, first-frame smoke. | none on legacy themes |
| **S7 The seven App Themes** | palette data from `themes.v2.json` (`family` overrides only where T7 fails); `appThemeCss` / `installAppThemeStyles`; T7, T8 with [cross], T10–T12; draw.test overrides. Not reachable from the UI yet. | none |
| **S8 App Theme picker** | `AppThemePicker`, guidance and disclosure, registry and `SettingRow`; UI tests; smoke layout passes per theme. | the seven themes become available |
| **S9 Docs** | §12, §14 rows 31–32, §16 rows; the release-notes lines. | — |

**Release boundaries.**
- S1 may ship alone.
- S2 may ship alone.
- **S3–S5 ship together** (release A: "Default"). The notice is truthful only once the lexical and semantic tokens it
  names are live.
- **S6–S8 ship together** (release B: "App Themes"). S6 must precede S7 so the first frame is right for a new theme.
- S9 goes with each release.

---

## 11. Risks and open decisions for the owner

### Decided in this round
| Item | Decision |
|---|---|
| D1 Migration from Standard to Default | Migrate once, with the notice and **Use Standard** (§3.6) |
| D2 Querithm Dark/Light's own Default | **Overruled:** their own Default palettes, semantic on (C1) |
| D7 MySQL `?` | Lifted: `delimiter.bind`, which still renders fg under the six themes |
| D10 `--q-info` | Added; the legacy blocks fall back to accent |
| D11 Other editors and semantic colours | Resolved: the option is global |
| D13 Downgrade to fewer themes | Resolved by V11/V12: stored ids are kept unless changed |

### Open, with recommendations
1. **D3 Picker model:** GitHub-like Single theme / Match system with two picks.
   → **Recommend.** The `theme` values stay as they are.
2. **D4 Env/object/chart colours:** inherit the mode's Querithm family, overriding only where a T7 test fails; Prod
   stays red-hued.
   → **Recommend.**
3. **D5 Semantic scope v1:** as in §5.3. Later: T-SQL procedure parameters as `bind`, and `WITH` inside a statement.
   → **Recommend v1.**
4. **D6 Word operator and constant lists** (§5.2).
   → **Recommend as listed.**
5. **D8 Release notes** (CLAUDE §6: a changed default gets its own line), release A:
   > "Syntax colors now follow your color theme (Default). On Querithm Dark and Light the colors you know stay;
   > TRUE/FALSE/NULL, AND/OR/NOT/IN/LIKE, CTE names and schema prefixes get their own. Choose Standard to keep the
   > previous look on every theme."

   Also: "Search matches, error, warning and info underlines, and matching brackets are now readable on every theme."
   And: "New setting: Italic comments."
6. **D9 Font styles of the new roles:** word operators bold (V3); constants regular. A theme may override.
   → **Recommend.**
7. **D14 Exported charts:** always Querithm Light on white.
   → **Recommend.**
8. **D15 (new) Writing only changed keys (V11)** changes how every setting is saved: one extra read per save, and a
   saved key now means an explicit choice.
   → **Recommend.** It needs its own §16 row.
9. **D16 (new) Warning squiggle colour:** a per-mode amber mark (`#cca700` / `#a87000`), overridable per theme, rather
   than `--q-warn` (§4.3).
   → **Recommend.**
10. **D17 (new) Italic comments setting**, default on.
    → **Recommend.**

### Risks
- **A gap in the T1 corpus** could let a lexical form change silently.
  → The corpus is seeded from `stress.sql` and every Monarch rule; T9 enumerates the tokens.
- **The six-palette overlay floors (T8) are not yet measured on every new theme.**
  → Measure in S1; a failure moves the App Theme's token.
- **Semantic colours arrive 100–500 ms after typing or a switch** (Monaco's debounce). After a switch they show the
  Monarch colours meanwhile, never a wrong role (V7).
- **The cost of a theme switch with 30 tabs.**
  → §14 row 32: p95 ≤ 50 ms.
- **`installAppThemeStyles` fails.**
  → The legacy fallback (I4); reported through `reportRenderError`.
- **A Monaco upgrade renames `setSemanticTokens`.**
  → `monacoInternals.test.ts` fails on the bundled sources first; meanwhile V7 degrades to the 100–500 ms stale window.
- **Two instances.** On-disk reverts are fixed (V11), but each instance shows its own in-memory values until restart.
  The caches and the shell file are last-write-wins, which is harmless because each is only a copy.

---

## 12. Text for docs/ARCHITECTURE.md

### §12 Theme: replaces the start of the paragraph at `ARCHITECTURE.md:1677-1685`, up to "…so commented-out SQL never reads as live code."
> Theme tokens are CSS variables (`--q-*`), one full set per App Theme; Monaco's editor chrome, Glide, Dockview and the
> charts read them. Nine App Themes ship (`src/theme/appThemes.ts`): Querithm Dark and Querithm Light, whose blocks stay
> in `src/theme/tokens.css` (`:root, :root[data-theme="dark"]` / `:root[data-theme="light"]`), and seven more whose
> tokens are data in `appThemes.ts`, made into one stylesheet before the first render (`installAppThemeStyles`, selector
> `:root[data-theme="<mode>"][data-app-theme="<id>"]`). The document names the mode in `data-theme` (`dark` / `light`,
> what every mode reader uses) and the App Theme in `data-app-theme`; a theme's block applies only when both agree, so
> an unknown id, or a mode switched for an export, shows Querithm Dark or Light, never a mix. Environment, object-icon
> and chart colors come from the mode's Querithm block unless a theme overrides one, so Prod stays red and the chart
> order stays the validated one. Status colors are `--q-ok`, `--q-warn`, `--q-err` and `--q-info` (Querithm Dark and
> Light have no `--q-info`; their accent stands in). The Theme setting is Dark / Light / Match system with one dark and
> one light App Theme (`darkTheme`, `lightTheme`); Settings shows it as Single theme or Match system, with a line per
> theme on when it is restful. SQL token colors come from Settings → Appearance → Syntax colors (`syntaxColors`, on the
> document as `data-syntax`): **Default**, the default, uses the App Theme's own SQL palette, and every App Theme has
> one, Querithm Dark and Light included (Standard's colors plus their own for constants, word operators, `::`, bind
> parameters, `@@` system variables, CTE names and schema prefixes; word operators bold). The six syntax themes
> (Standard and Color-blind safe, picked by a five-expert panel; Neon, Forest, Ember and Ocean, picked by a
> seven-designer panel that gave each its own keyword hue) keep their colors on every App Theme and show the new token
> kinds exactly as before (`TOKEN_ROLES.legacy`); an unknown id is Default. CTE names and schema prefixes are semantic
> tokens from the tier-1 analyzer (§9), requested only for Default. "Italic comments" (on by default) can make comments
> upright in every palette. Editor chrome follows the theme: the find match is a 10 % `--q-warn` tint with a warn
> border; error and info underlines and their overview-ruler marks use `--q-err` and `--q-info`; the warning underline
> uses a non-text amber mark (`--q-warn-mark`, by default `#cca700` dark / `#a87000` light) that stays apart from the
> error red for color-blind users; word highlights are a 9 % `--q-fg` tint, and bracket matches and indent guides use
> the line tokens.

The rest of the paragraph ("Every syntax theme keeps every token color …") stays, read as "every syntax theme on every
App Theme of its mode, and every Default palette on its own App Theme". Add after it: "Transient layers (find match,
word highlight, Querithm's 12 % decoration fills) keep non-comment tokens at 4.5:1 and comments at 4:1 (the six syntax
themes: 4.3:1 on find and fills); squiggle marks reach 3:1 and stay ΔE 8 apart under protan, deutan and tritan
(`src/editor/__tests__/editorChrome.test.ts`)."

### §14: new rows
| # | Metric | How measured | Initial target |
|---|---|---|---|
| 31 | Semantic SQL colors (CTE names, schema prefixes) for the visible range of the 12k-line procedure | Vitest perf test on `bigScript()` and `pnpm bench` metric `31sem`, 200 calls after one cold call | warm p95 ≤ 4 ms; first call ≤ 30 ms; metric 3 unchanged |
| 32 | App Theme switch with 30 tabs open | `pnpm bench` metric `32theme`: click a theme card → next paint, 20 samples | p95 ≤ 50 ms |

### §16: new rows
| Change | Spec said | Now | Why |
|---|---|---|---|
| App Themes (2026-11-xx) | §12 Theme: tokens "defined once for dark and light"; Theme setting `dark` / `light` / `system` | Nine App Themes. Querithm Dark and Light unchanged in `tokens.css`; seven more as data in `src/theme/appThemes.ts`, installed as one `<style>` before the first render, selector `:root[data-theme="<mode>"][data-app-theme="<id>"]`. `data-theme` keeps naming the mode; `data-app-theme` names the theme; a mismatch or unknown id falls back to the mode's Querithm theme. New settings `darkTheme` / `lightTheme`; `theme` keeps its three values. Environment, object and chart colors inherit the mode's Querithm family unless overridden. New token `--q-info`, absent from the frozen blocks (accent stands in). Theme-keyed caches watch both attributes (`tabStyle`, results and monitor grids, plan view). | Owner request: original, distinct themes for long sessions, each with its own status colors. A mode plus two picks needs no migration, survives a downgrade and keeps the toggle command and Match system working. Typed data makes a missing token a compile error. The legacy blocks stay CSS, so saved settings and the tests that parse them keep working. The style needs `style-src 'unsafe-inline'`, which Monaco already requires; there is no new permission or library. |
| Syntax colors: Default (2026-11-xx) | §16 "Syntax colors (§12)": Standard is the default; setting `syntaxTheme` | Setting `syntaxColors`: `default` (new default: the App Theme's own palette; Querithm Dark and Light: Standard's 12 colors plus their own for constants, word operators, `::`, bind parameters, `@@` variables, CTE names and schema prefixes) or one of the six themes, unchanged. A saved object without `syntaxColors` is migrated once and written at once: `syntaxTheme: "standard"`, a removed id or nothing become `default`, and a one-time notice offers **Use Standard**; any other id is kept. `syntaxTheme` is mirror-written (the six 2026-11 ids as themselves, anything else `standard`, a contract for later versions), and a non-Standard `syntaxTheme` that is not the mirror is adopted as a choice an older build made. New Monarch tokens (`keyword.constant`, `identifier.constant`, `keyword.operator`, `type.cast`, `variable.bind`, `delimiter.bind`, `predefined.global`) each name the role they had before, so the six themes render byte-identically (golden test, six dialects). Word operators are bold in Default palettes. | The owner asked that every App Theme, the Querithm ones included, color booleans, NULL, CTE names, schema prefixes and parameters. The saved value cannot tell an explicit Standard from the old default (the whole object was saved), so everyone gets Default once, is told, and is one click from Standard, which is then kept forever. Mirror plus adoption keeps downgrade round trips exact. |
| Editor chrome follows the theme (2026-11-xx) | `monaco.ts` set no find, squiggle, word-highlight, bracket-match or indent-guide colors: Monaco's defaults | The find match is `--q-warn` 10 % with a warn border (the current match 2 px, others 1 px at 60 %). Error and info squiggles and their overview-ruler marks use `--q-err` / `--q-info`. Warning: `--q-warn-mark`, by default `#cca700` / `#a87000`. Word highlight: `--q-fg` 9 % with a `--q-line-strong` border. Bracket match: `--q-line-strong` border, no fill. Indent guides: `--q-line` / `--q-line-strong`. The debug, trace, error, pair and insert decoration fills drop to 12 %. | Monaco's opaque current-match fill put tokens at 2.3–2.7:1 on every theme. Its light warning squiggle was 2.8:1. Its error red was not the theme's. `--q-warn` as a squiggle is ΔE 1.3 from Querithm Light's error under deutan simulation, so warnings and errors (same shape) looked identical to deuteranopes. A non-text mark needs only 3:1, so a lighter amber keeps them apart. |
| Start screen in any App Theme (2026-11-xx) | §16 "Start screen in the window's theme": a one-word cache and a one-word shell file with two hard-coded backgrounds | `localStorage` `querithm.theme` keeps its word. New `querithm.themeBoot` (`{v:1,dark,light,syntax}`) holds both picks' id and start colors and the syntax choice, validated by `boot-theme.js`, which sets `data-app-theme`, `data-syntax` and `--boot-*` (CSSOM, no inline script or style); `boot-splash.css` uses them with today's hexes as fallbacks. The cache also seeds a failed settings read. The shell file holds `<word> #dark-bg #light-bg` (one-word files still read as Querithm colors); `app_theme_keep` parses it strictly and writes what it parsed; calls are single-flight, latest value only, bounded at 2 s. | The shell and the boot files never know theme ids, so adding a theme changes no Rust and no boot file. Single flight stops an older theme from landing last while arrow keys step through the cards. |
| Semantic SQL colors (2026-11-xx) | `src/editor/editors.ts`: `semanticHighlighting.enabled: false` | A range semantic-tokens provider per SQL language marks CTE names and schema prefixes from the tier-1 lexer and analyzer (`semanticSpans`, `scanner.ts`), for all six dialects; ambiguous parts (Oracle packages, `a.b(` calls) only when the connection's cached schema confirms them. On for Default on every App Theme, never for the six syntax themes. Applied semantic tokens are dropped when the palette changes (`monacoInternals.clearSemanticTokens`), so none shows a previous palette's color. No RPC and no contract change. | A CTE or a schema needs statement context, which a Monarch regex must not decide (token/AST only). Tier 1 already parses CTEs and references for completion in every dialect, without a round trip. Measured as §14 row 31. |
| Settings saved per changed key (2026-11-xx) | `src/store/settings.ts`: every save wrote the whole settings object (plus unknown keys kept in `extra`) | `persist()` re-reads `ui.settings` and writes it back with only the keys set in this session (a version per key, so a change during a write is not lost). Keys another window or a newer version wrote are kept verbatim, unknown ids included. A failed re-read fails the save, as a failed write does. | Two windows reverted each other's settings, and whole-object writes froze every default into the saved object, which is why an explicit Standard could not be told from the old default. Now a saved key is an explicit choice. One extra local read per debounced save; not a hot path. |

---

## 13. Component states: buttons, focus, fields and the status family (round 2)

Round 2 added each new theme's button states and its status colors on every surface:
- **Button states:** rest, hover, pressed, focus and disabled.
- **Status colors:** info, success, warning and error.

Two design seats and one State Critic produced them.
- **Where the values are:** each theme's `states` block in `plans/app-themes/themes.json`, plus a few changed `ui`
  status hues.
- **How they are checked:** `node plans/app-themes/check-states.mjs plans/app-themes/themes.json` prints
  `TOTAL FAILURES: 0`. Today's formulas, applied to the 7 new themes under the same rules, gave 117 failures.

### 13.1 New tokens
Only the new themes' generated blocks declare these tokens. The legacy blocks do not. Every consumer reads a token
through a `var()` fallback equal to today's formula, so Querithm Dark and Light keep their component colors (I2).

| `states` key | CSS token | Fallback (today) | Consumer |
|---|---|---|---|
| `btnHover` / `btnPressed` / `btnBorderHover` | `--q-btn-hover` / `--q-btn-pressed` / `--q-btn-border-hover` | `--q-hover` / hover / `--q-line` | `.q-btn` |
| `ghostHover` / `ghostPressed` | `--q-ghost-hover` / `--q-ghost-pressed` | `--q-hover` / hover | `.q-btn-ghost`, `.q-tb`, `.q-strip-menu` |
| `accentSolidHover` / `accentSolidPressed` | `--q-accent-solid-hover` / `-pressed` | `color-mix(in srgb, var(--q-accent-solid) 85%, black)` | `.q-btn-primary` |
| `errSolidHover` / `errSolidPressed` / `errSolidFg` | `--q-err-solid-hover` / `-pressed` / `--q-err-solid-fg` | the same mix / `#fff` | `.q-btn-danger` |
| `focusRing`, `inputFocus` | `--q-focus-ring`, `--q-input-focus` | `--q-accent` | `:focus-visible`, `.q-input:focus` |
| `disabledOpacity` | `--q-disabled-opacity` | `0.45` | every disabled control |
| `runTint*`, `stopTint*` (numbers) | `--q-run-tint`, `--q-run-tint-hover`, `--q-run-tint-pressed`, `--q-stop-tint…` (emitted as `%`) | `10%` / `15%` / `15%` | `.q-btn-run`, `.q-btn-stop`, the top-bar Run split |
| `toastAlpha`, `toastBorderAlpha`, `bannerAlpha` | `--q-toast-alpha`, `--q-toast-border-alpha`, `--q-banner-alpha` | `15%`, `50%`, `15%` | toasts, banners, same-hue text badges |
| `errRowAlpha`, `warnRowAlpha` | `--q-err-row-alpha`, `--q-warn-row-alpha` | `10%`, `15%` | every in-flow error and warning tint |
| `env.*`, `envProdMark`, `envProdFill` | `--q-env-*-fg`, `--q-env-prod`, `--q-env-prod-fill` (family overrides, §2.3) | the mode's Querithm family | Connect dialog, tabs, stripes |
| `warnMark` | `--q-warn-mark` (§4.3) | `#cca700` dark / `#a87000` light | warning squiggle and overview ruler |

Generation:
- `appThemeCss` (§2.3) emits the `states` block next to `ui`.
- `UI_TOKENS` gains a `STATE_TOKENS` map.
- A number is emitted as a percentage (`0.12` → `12%`), so it can sit inside `color-mix`.

### 13.2 CSS in `tokens.css` (outside the frozen token blocks; slice S1)
```css
.q-btn:hover:not(:disabled):not([aria-disabled="true"]) { background: var(--q-btn-hover, var(--q-hover)); border-color: var(--q-btn-border-hover, var(--q-line)); }
.q-btn:active:not(:disabled):not([aria-disabled="true"]) { background: var(--q-btn-pressed, var(--q-btn-hover, var(--q-hover))); border-color: var(--q-btn-border-hover, var(--q-line)); }
.q-btn-primary:hover:not(:disabled):not([aria-disabled="true"]) { background: var(--q-accent-solid-hover, color-mix(in srgb, var(--q-accent-solid) 85%, black)); }
.q-btn-primary:active:not(:disabled):not([aria-disabled="true"]) { background: var(--q-accent-solid-pressed, var(--q-accent-solid-hover, color-mix(in srgb, var(--q-accent-solid) 85%, black))); }
/* .q-btn-danger likewise, with --q-err-solid-hover / -pressed and color: var(--q-err-solid-fg, #fff) */
.q-btn-run { background: color-mix(in srgb, var(--q-ok) var(--q-run-tint, 10%), transparent); }
/* .q-btn-run :hover → --q-run-tint-hover, :active → --q-run-tint-pressed; .q-btn-stop the same with --q-err and --q-stop-tint* */
.q-btn-ghost:hover:not(:disabled):not([aria-disabled="true"]), .q-tb:hover:not([aria-disabled="true"]), .q-tb[aria-expanded="true"] { background: var(--q-ghost-hover, var(--q-hover)); }
.q-btn-ghost:active, .q-tb:active { background: var(--q-ghost-pressed, var(--q-ghost-hover, var(--q-hover))); }
/* Disabled Primary, Run and Stop are drawn as the plain button, as Danger already is (State Critic V5). */
.q-btn-primary:is(:disabled, [aria-disabled="true"]),
.q-btn-run:is(:disabled, [aria-disabled="true"]),
.q-btn-stop:is(:disabled, [aria-disabled="true"]) { background: var(--q-raised); border-color: var(--q-line); color: var(--q-fg); font-weight: 500; }
.q-btn:disabled, .q-btn[aria-disabled="true"], .q-tb[aria-disabled="true"] { opacity: var(--q-disabled-opacity, 0.45); }
/* The Primary and Danger ring was ΔE 2.6–3.9 from their fill (V6). */
.q-btn-primary:focus-visible, .q-btn-danger:focus-visible { outline-offset: 2px; }
:focus-visible { outline-color: var(--q-focus-ring, var(--q-accent)); }
.q-input:focus { border-color: var(--q-input-focus, var(--q-accent)); }
/* Toasts, banners and status rows: the hue sits on an edge, and the fill is a wash on an opaque surface (V2, V3). */
.q-toast { background: linear-gradient(color-mix(in srgb, var(--q-kind) var(--q-toast-alpha, 15%), transparent) 0 0), var(--q-raised);
  border: 1px solid color-mix(in srgb, var(--q-kind) var(--q-toast-border-alpha, 50%), transparent); border-left: 3px solid var(--q-kind); color: var(--q-kind); }
.q-banner { background: color-mix(in srgb, var(--q-kind) var(--q-banner-alpha, 15%), transparent); border-left: 3px solid var(--q-kind); }
.q-status-row { border-left: 2px solid var(--q-kind); }
/* warn → --q-warn, success → --q-ok. The info toast is raised + fg, with no edge. */
.q-toast[data-kind="error"], .q-banner[data-kind="error"], .q-status-row[data-kind="error"] { --q-kind: var(--q-err); }
```
- `:active` rules apply only while the pointer or a key is down.
- `[aria-pressed="true"]` and `[aria-expanded="true"]` keep the hover token (R3).

### 13.3 Component changes (slice S1, all themes)
- **Visible kind glyph (State Critic R1).**
  - Every toast, banner and Messages-panel line shows ✖ / ⚠ / ✓ / ℹ before its text, `aria-hidden`, in the kind's
    color. The sr-only prefix stays.
  - Sites: `Palette.tsx:191-196`, `Palette.tsx:273`, and `EntryLine` in `MessagesPanel.tsx`. In `EntryLine` that means
    warning lines, and error lines that have no "Msg n" header (`messageLog.ts:17`).
  - The status hues clear the color-blind floors by only +0.3 to +0.6. That thin margin is accepted only with this rule.
- **White on the accent fill (R2).**
  - `text-white` becomes `text-accent-fg` at `panelKit.tsx:99`, `panelKit.tsx:338`, `ChartView.tsx:283`,
    `CellOutput.tsx:81` and `CellViewerPanel.tsx:88`, and in the `contrast.test.ts:512` sample.
  - Today these read at 1.8–2.2:1 on Cortado, Fathom, Damson and Juniper.
- **Toasts and banners.**
  - Toasts use `.q-toast[data-kind]`, which replaces `TOAST_TONE`.
  - Drop `backdropFilter: blur(6px)` (`Palette.tsx:238`).
  - Banners use `.q-banner[data-kind]`.
- **Literal tints become tokens (V4), about 25 sites.**
  - Error tints `bg-err/10` and `bg-err/15` use `--q-err-row-alpha`; warning tints `bg-warn/10` and `bg-warn/15` use
    `--q-warn-row-alpha`. Each of these also gets `.q-status-row`.
  - Sites:
    - the TopBar TXN group (`TopBar.tsx:501`)
    - RunNotice
    - `QueryTab.tsx:203`
    - `ResultsPanel.tsx:537/566`
    - `CellViewerPanel.tsx:101`
    - `PlanView.tsx:32/327`
    - `BlockingBanner`
    - `SidePanel.tsx:41`
    - `Dialogs.tsx:186`
    - `UpdateDialog.tsx:74`
    - `ImportWizardDialog.tsx:352/455`
  - A selected error item uses `calc(var(--q-err-row-alpha) + 5%)`.
  - Same-hue text badges use `--q-toast-alpha`: `PlanView.tsx:41,164,283`, `BottomPanel.tsx:129,188`,
    `panelKit.tsx:338` and `QueryTab.tsx:207`.
  - The top-bar Run split (`TopBar.tsx:227,306`) uses the run and stop tint tokens.

### 13.4 Rules (invariants for §9)
| # | Rule |
|---|---|
| S1 | Every label reaches ≥ 4.5:1 on its button at rest, on hover and when pressed. That covers the variant's own label, and `fg`, `err`, `accent` and `muted` on `--q-btn-*` and `--q-ghost-*`. |
| S2 | Hover is visible: ΔE ≥ 3 for default and ghost (on every surface), and ≥ 4 for solid fills. Pressed differs from hover by ΔE ≥ 3, through hue or chroma; it never moves toward the label's lightness. |
| S3 | Each step moves the fill away from the surface: lighter on dark themes, darker on light ones. The exception is a label that cannot keep 4.5:1 for two steps that way: then the step moves away from the label. Today that is only Danger on dark themes. |
| S4 | The focus ring reaches ≥ 3:1 on every surface and on every selection fill. Primary and Danger take a 2 px offset. Input focus vs input error is ΔE ≥ 15 in normal vision and ≥ 10 under each color-blind simulation. |
| S5 | Status text reaches ≥ 4.5:1 on every surface, on hover, and on its toast, banner and row tints. Status hues are ≥ ΔE 15 apart in normal vision. Under protan, deutan and tritan simulation they stay ≥ 8 apart, and warn/err ≥ 10. Accent is ≥ 15 from every status hue. |
| S6 | Every status surface carries a visible glyph and a word. Color is never the only cue. |
| S7 | `--q-warn-mark` is its own token, never an alias of `--q-warn`. Against err it is ≥ 15 apart in normal vision and ≥ 12 under each simulation. On light themes the mark/err luminance ratio is ≥ 1.8. The mark reaches ≥ 3:1 on the four squiggle layers. |
| S8 | Environment words reach ≥ 4.5:1 on panel, raised, row-sel and panel-2. The Production stripe reaches ≥ 3:1. PROD stays red-hued and ≥ ΔE 15 from the other environments. The PROD fill carries white at ≥ 4.5:1. |
| S9 | Any class list with `bg-accent-solid` carries `text-accent-fg`. A test over the component sources enforces it. |

### 13.5 Tests
- **`contrast.test.ts`, per App Theme (all 9):**
  - `.q-btn` variants at rest, on hover and on `:active`, parsed from the new rules;
  - the S1 label set on `--q-btn-*` and `--q-ghost-*`;
  - disabled Primary, Run and Stop drawn plain;
  - S4, S5, S7 and S8;
  - the S9 source scan.
- **`Palette.test.tsx`:** each toast kind and each banner kind renders its glyph, with no `backdropFilter`.
- **`MessagesPanel.test.tsx`:** warning lines, and error lines with no header, render their glyph.
- **`appThemes.test.ts`:** every `states` key maps to a `STATE_TOKENS` entry, and alphas are emitted as percentages.

### 13.6 Slices
- **S1** grows from "editor chrome" into "chrome and component states": the §13.2 CSS with today's fallbacks, and the
  §13.3 component changes. It can still ship alone.
- On Querithm Dark and Light, S1 changes things visibly only where it fixes something:
  - glyphs appear;
  - toasts become opaque and get a left edge;
  - disabled Primary, Run and Stop become plain;
  - the Primary focus offset becomes 2 px;
  - white-on-accent labels move to `accent-fg`, which is the same white on the legacy themes.
- **S7** adds each new theme's `states` values to its generated block.

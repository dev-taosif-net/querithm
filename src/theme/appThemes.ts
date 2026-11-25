// App Themes (plans/app-themes §2, §13): nine themes, each a full --q-* token set with a mode (dark | light).
// Querithm Dark and Light are the hand-written blocks in tokens.css (unchanged; `ui` here only mirrors them). The seven
// new themes are generated into one <style> at start (installAppThemeStyles), each block applying only when
// data-theme (the mode) and data-app-theme (the id) agree, so a wrong or unknown id shows Querithm Dark/Light, never a
// mix. Tokens a new block does not set (environment marks, object icons, chart slots) come from its mode's legacy block.
import { APP_THEME_DATA } from "./appThemeData";

export type ThemeMode = "dark" | "light";

/** Design key → CSS variable for the core UI tokens every App Theme defines (explicit: panel2 is --q-panel-2). */
export const UI_TOKENS = {
  bg: "--q-bg", panel: "--q-panel", panel2: "--q-panel-2", raised: "--q-raised", hover: "--q-hover",
  line: "--q-line", lineStrong: "--q-line-strong", fg: "--q-fg", muted: "--q-muted", mutedHl: "--q-muted-hl",
  faint: "--q-faint", accent: "--q-accent", accentSolid: "--q-accent-solid", accentFg: "--q-accent-fg", info: "--q-info",
  ok: "--q-ok", warn: "--q-warn", err: "--q-err", errSolid: "--q-err-solid", null: "--q-null", sel: "--q-sel",
  active: "--q-active", rowSel: "--q-row-sel", overlaySel: "--q-overlay-sel", warnSolid: "--q-warn-solid",
  warnSolidFg: "--q-warn-solid-fg", ident: "--q-ident", suggestMatch: "--q-suggest-match",
  diffInsert: "--q-diff-insert", diffRemove: "--q-diff-remove",
} as const;
export type UiKey = keyof typeof UI_TOKENS;

/** Component-state keys (§13.1) → CSS variable. Alphas are numbers (0.12) and are emitted as percentages. */
export const STATE_TOKENS = {
  btnHover: "--q-btn-hover", btnPressed: "--q-btn-pressed", btnBorderHover: "--q-btn-border-hover",
  ghostHover: "--q-ghost-hover", ghostPressed: "--q-ghost-pressed",
  accentSolidHover: "--q-accent-solid-hover", accentSolidPressed: "--q-accent-solid-pressed",
  errSolidHover: "--q-err-solid-hover", errSolidPressed: "--q-err-solid-pressed", errSolidFg: "--q-err-solid-fg",
  focusRing: "--q-focus-ring", inputFocus: "--q-input-focus", disabledOpacity: "--q-disabled-opacity",
  runTint: "--q-run-tint", runTintHover: "--q-run-tint-hover", runTintPressed: "--q-run-tint-pressed",
  stopTint: "--q-stop-tint", stopTintHover: "--q-stop-tint-hover", stopTintPressed: "--q-stop-tint-pressed",
  toastAlpha: "--q-toast-alpha", toastBorderAlpha: "--q-toast-border-alpha", bannerAlpha: "--q-banner-alpha",
  errRowAlpha: "--q-err-row-alpha", errRowSelAlpha: "--q-err-row-sel-alpha", warnRowAlpha: "--q-warn-row-alpha",
  markAlpha: "--q-mark-alpha", markDimAlpha: "--q-mark-dim-alpha",
  envProdFill: "--q-env-prod-fill", envProdMark: "--q-env-prod", warnMark: "--q-warn-mark",
} as const;
export type StateKey = keyof typeof STATE_TOKENS;
/** State keys whose values are alphas (a percentage in CSS); disabledOpacity stays a plain number. */
const PERCENT_KEYS = new Set<StateKey>(["runTint", "runTintHover", "runTintPressed", "stopTint", "stopTintHover", "stopTintPressed", "toastAlpha", "toastBorderAlpha", "bannerAlpha", "errRowAlpha", "errRowSelAlpha", "warnRowAlpha", "markAlpha", "markDimAlpha"]);
/** Connect-dialog environment words (family tokens a theme may override). */
export const ENV_TOKENS = { local: "--q-env-local-fg", dev: "--q-env-dev-fg", test: "--q-env-test-fg", staging: "--q-env-staging-fg", prod: "--q-env-prod-fg" } as const;
/** Derived, never authored: the activity monitor's wait ages copy ok / warn / err, as both legacy blocks do. */
export const DERIVED = { "--q-wait-short": "ok", "--q-wait-mid": "warn", "--q-wait-long": "err" } as const;

export type AppThemeStates = Partial<Record<StateKey, string | number>> & { env?: Partial<Record<keyof typeof ENV_TOKENS, string>> };

/** The theme's own SQL palette: what Syntax colors = Default shows (word operators and :: take `operator`, binds `parameter`). */
export interface AppThemeSql {
  keyword: string; type: string; string: string; number: string; comment: string; variable: string; function: string;
  operator: string; ident: string; constant: string; cte: string; schema: string; parameter: string;
  bracket1: string; bracket2: string; bracket3: string;
}

export interface AppThemeData {
  /** Stable forever: saved in store.db and the boot cache. */
  id: string;
  /** Settings name, ≤ 18 characters. */
  name: string;
  mode: ThemeMode;
  /** One Settings line (≤ 90 characters): when the theme suits (the Eye Doctor's guidance). */
  description: string;
  /** Querithm Dark / Light: tokens live in tokens.css; nothing is generated for them. */
  legacy?: true;
  ui: Record<UiKey, string>;
  states?: AppThemeStates;
  sql: AppThemeSql;
}
export type AppTheme = AppThemeData;

export const APP_THEMES: readonly AppTheme[] = APP_THEME_DATA;
export const DARK_APP_THEME_IDS: readonly string[] = APP_THEMES.filter((t) => t.mode === "dark").map((t) => t.id);
export const LIGHT_APP_THEME_IDS: readonly string[] = APP_THEMES.filter((t) => t.mode === "light").map((t) => t.id);

export function appThemeById(id: string | undefined): AppTheme | undefined {
  return id ? APP_THEMES.find((t) => t.id === id) : undefined;
}
/** The Querithm theme of each mode: the fallback for an unknown, wrong-mode or not-installed id. */
export const LEGACY_APP_THEME: Record<ThemeMode, AppTheme> = { dark: appThemeById("dark")!, light: appThemeById("light")! };

const pct = (n: number) => `${Math.round(n * 1000) / 10}%`;

/** The variables a theme's generated block declares: none for a legacy theme. */
export function tokenVars(t: AppTheme): Record<string, string> {
  if (t.legacy) return {};
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(UI_TOKENS)) out[v] = t.ui[k as UiKey];
  for (const [v, from] of Object.entries(DERIVED)) out[v] = t.ui[from];
  const s = t.states ?? {};
  for (const [k, v] of Object.entries(STATE_TOKENS)) {
    const value = s[k as StateKey];
    if (value === undefined) continue;
    out[v] = typeof value === "number" ? (PERCENT_KEYS.has(k as StateKey) ? pct(value) : String(value)) : value;
  }
  for (const [k, v] of Object.entries(s.env ?? {})) out[ENV_TOKENS[k as keyof typeof ENV_TOKENS]] = v;
  return out;
}

/** The selector of a generated block: it applies only when the mode and the id agree (I4). */
export const appThemeSelector = (t: AppTheme) => `:root[data-theme="${t.mode}"][data-app-theme="${t.id}"]`;

/** One block per new theme; `color-scheme` first so native controls follow the mode. */
export function appThemeCss(themes: readonly AppTheme[] = APP_THEMES): string {
  return themes
    .filter((t) => !t.legacy)
    .map((t) => `${appThemeSelector(t)} {\n  color-scheme: ${t.mode};\n${Object.entries(tokenVars(t)).map(([k, v]) => `  ${k}: ${v};`).join("\n")}\n}`)
    .join("\n");
}

export const APP_THEME_STYLE_ID = "q-app-themes";
let installed = false;
/** Whether the generated theme blocks are in the document; without them a new theme id resolves to Querithm Dark/Light. */
export const appThemeStylesInstalled = () => installed;

/**
 * Inserts (or replaces, for HMR) the one <style> holding every new theme's block. Runs before the first render.
 * Reports what the browser did, not what was asked: a content security policy that refuses inline styles (style-src
 * without 'unsafe-inline') blocks the element silently, and then the browser makes no style sheet for it (HTML "update a
 * style block" checks the policy first), so `sheet` stays null. Today's policy allows them (src-tauri/tauri.conf.json);
 * a stricter one shows Querithm Dark / Light instead of a theme whose blocks never applied.
 */
export function installAppThemeStyles(doc: Document = document): boolean {
  try {
    let el = doc.getElementById(APP_THEME_STYLE_ID) as HTMLStyleElement | null;
    if (!el) {
      el = doc.createElement("style");
      el.id = APP_THEME_STYLE_ID;
      doc.head.appendChild(el);
    }
    el.textContent = appThemeCss();
    installed = el.sheet != null;
  } catch {
    installed = false; // the document shows Querithm Dark/Light (resolveDocumentAppTheme agrees)
  }
  return installed;
}

// Which App Theme and which SQL palette show (plans/app-themes §4.1). Pure: no store; the document is passed in.
import { appThemeById, appThemeStylesInstalled, LEGACY_APP_THEME, type AppTheme, type ThemeMode } from "./appThemes";
import { BASE_FONT_STYLE, SYNTAX_THEMES, type FontStyle, type SyntaxChoice, type SyntaxPalette, type SyntaxRole } from "../editor/syntaxTheme";

export interface ThemeChoice {
  theme: "dark" | "light" | "system";
  darkTheme: string;
  lightTheme: string;
}

/** The App Theme to show. An unknown or wrong-mode pick never reaches the document: it is Querithm Dark / Light. */
export function resolveAppTheme(c: ThemeChoice, osDark: boolean): AppTheme {
  const mode: ThemeMode = c.theme === "system" ? (osDark ? "dark" : "light") : c.theme === "light" ? "light" : "dark";
  const t = appThemeById(mode === "dark" ? c.darkTheme : c.lightTheme);
  return t && t.mode === mode ? t : LEGACY_APP_THEME[mode];
}

/** The App Theme the document shows, read the way the CSS selector reads it (I4): the mode and the id must agree, and a
 *  new theme's block must be installed; otherwise Querithm Dark / Light of that mode. */
export function resolveDocumentAppTheme(root: HTMLElement = document.documentElement): AppTheme {
  const mode: ThemeMode = root.dataset.theme === "light" ? "light" : "dark";
  const t = appThemeById(root.dataset.appTheme);
  return t && t.mode === mode && (t.legacy || appThemeStylesInstalled()) ? t : LEGACY_APP_THEME[mode];
}

/** An App Theme's Default SQL palette in the editor's roles: word operators and `::` take the operator color, binds the
 *  parameter color, `@@x` the variable color. */
export function appSqlPalette(t: AppTheme): SyntaxPalette {
  const s = t.sql;
  return {
    keyword: s.keyword, type: s.type, string: s.string, number: s.number, comment: s.comment, variable: s.variable,
    function: s.function, operator: s.operator, ident: s.ident, bracket1: s.bracket1, bracket2: s.bracket2, bracket3: s.bracket3,
    constant: s.constant, wordOperator: s.operator, cast: s.operator, bind: s.parameter, systemVariable: s.variable, cte: s.cte, schema: s.schema,
  };
}

export interface ResolvedSyntax {
  choice: SyntaxChoice;
  palette: SyntaxPalette;
  fontStyle: Partial<Record<SyntaxRole, FontStyle>>;
  /** Semantic tokens (CTE names, schema prefixes) are requested only when the palette colors them. */
  semantic: boolean;
}

/**
 * Syntax colors on an App Theme. One of the six syntax themes keeps its own palette in the App Theme's mode (Standard
 * unchanged); "default" and anything unknown use the App Theme's own palette, word operators bold (DA2 V3).
 * `uprightComments` (Settings › Italic comments off) draws comments upright in every palette.
 */
export function resolveSyntax(choice: string | undefined, app: AppTheme, uprightComments = false): ResolvedSyntax {
  const legacy = SYNTAX_THEMES.find((t) => t.id === choice);
  const upright = (fs: Partial<Record<SyntaxRole, FontStyle>>) => (uprightComments ? { ...fs, comment: "" as FontStyle } : fs);
  if (legacy) return { choice: legacy.id, palette: legacy[app.mode], fontStyle: upright(legacy.fontStyle), semantic: false };
  return { choice: "default", palette: appSqlPalette(app), fontStyle: upright({ ...BASE_FONT_STYLE, wordOperator: "bold" }), semantic: true };
}

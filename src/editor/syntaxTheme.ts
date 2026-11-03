// SQL syntax color themes (§12 Theme). Kept free of Monaco imports so tests can check coverage, contrast and
// separation, and so Settings can preview every theme without an editor. Standard and Color-blind safe were chosen by a
// five-expert panel (2026-10-30/31: proposals, then an anonymous ranked vote per type); Neon, Forest, Ember and Ocean by
// a seven-designer panel (2026-11-01: one keyword hue each, then a ranked vote). The rules each type promises are
// enforced in src/editor/__tests__/syntaxTheme.test.ts.

export type SyntaxThemeId = "standard" | "color-blind" | "neon" | "forest" | "ember" | "ocean";

/** What a token is, for coloring. `delimiter` defaults to the editor foreground, `temp` to ident, `batch` to keyword, `escape` to string. */
export type SyntaxRole =
  | "keyword" | "type" | "string" | "number" | "comment" | "variable" | "function" | "operator" | "ident"
  | "temp" | "batch" | "delimiter" | "escape";

/** Hex colors (#rrggbb) per role, plus the three colors `( )` cycle through by depth. */
export type SyntaxPalette = Record<Exclude<SyntaxRole, "temp" | "batch" | "delimiter" | "escape">, string> &
  Partial<Record<"temp" | "batch" | "delimiter" | "escape", string>> & { bracket1: string; bracket2: string; bracket3: string };

export type FontStyle = "" | "bold" | "italic" | "bold italic" | "underline" | "bold underline";

export interface SyntaxTheme {
  id: SyntaxThemeId;
  /** Settings name. */
  label: string;
  /** One Settings line: who the theme is for. */
  description: string;
  dark: SyntaxPalette;
  light: SyntaxPalette;
  fontStyle: Partial<Record<SyntaxRole, FontStyle>>;
}

/** The shared structure (panel votes C3, V2–V5): keywords and GO bold, comments and #temp italic, escapes bold. */
const STYLE: SyntaxTheme["fontStyle"] = { keyword: "bold", batch: "bold", comment: "italic", temp: "italic", escape: "bold" };

export const SYNTAX_THEMES: readonly SyntaxTheme[] = [
  {
    id: "standard",
    label: "Standard",
    description: "Balanced colors for everyday SQL. Every color reads on selected text.",
    dark: {
      keyword: "#a5aaff", type: "#6cc0a8", string: "#aed992", number: "#ec9563", comment: "#939ca8",
      variable: "#e6a6d6", function: "#e3c27a", operator: "#66bccc", ident: "#dcdde3",
      bracket1: "#c2c7d1", bracket2: "#d9b360", bracket3: "#c49cf0",
    },
    light: {
      keyword: "#22309a", type: "#205f58", string: "#23703a", number: "#9c4a00", comment: "#5e636d",
      variable: "#8a1f7c", function: "#5f5300", operator: "#0e6584", ident: "#24272d",
      bracket1: "#4a505a", bracket2: "#7d5a00", bracket3: "#7b35b0",
    },
    fontStyle: STYLE,
  },
  {
    id: "color-blind",
    label: "Color-blind safe",
    description: "For color blindness: red-green and blue-yellow stay distinct.",
    dark: {
      keyword: "#b9aafc", type: "#81c38e", string: "#c9fdb7", number: "#f8914f", comment: "#b1aaa8",
      variable: "#d09ec7", function: "#e3dd73", operator: "#9fddf9", ident: "#f3f6f3",
      bracket1: "#c4c8d0", bracket2: "#8fb4ff", bracket3: "#e8c35a",
    },
    light: {
      keyword: "#320a7f", type: "#003d3e", string: "#5f5a00", number: "#5f3631", comment: "#585857",
      variable: "#7c32a1", function: "#3a2d01", operator: "#2a5e73", ident: "#080804",
      bracket1: "#4a505a", bracket2: "#1f4fb8", bracket3: "#7a5200",
    },
    // Variables are italic too: the @ sigil and the slant back up hue where variable/type sit closest.
    fontStyle: { ...STYLE, variable: "italic" },
  },
  {
    id: "neon",
    // Hot-pink keywords (OKLCH h ≈ 345, the most saturated role). Comments are a warm grey: the panel's lavender read as
    // the keyword under deutan simulation (ΔE 2.8), so commented-out SQL looked live.
    label: "Neon",
    description: "Synthwave-bright: every token glows. For night sessions.",
    dark: {
      keyword: "#ff67c3", type: "#f0e070", string: "#acf56a", number: "#00e4ff", comment: "#aaa596",
      variable: "#bb86ff", function: "#3db5ff", operator: "#fe7b18", ident: "#eceef2",
      bracket1: "#ffd23f", bracket2: "#f084ee", bracket3: "#5cffc0",
    },
    light: {
      keyword: "#b80086", type: "#795d00", string: "#456800", number: "#036680", comment: "#665b64",
      variable: "#4c009f", function: "#0858d6", operator: "#9e4600", ident: "#1a1d2c",
      bracket1: "#7a5800", bracket2: "#962a9c", bracket3: "#006b4b",
    },
    fontStyle: STYLE,
  },
  {
    id: "forest",
    // Green keywords (h ≈ 150), so strings are berry rather than green.
    label: "Forest",
    description: "Green keywords, berry strings, low glare. Calm for long sessions.",
    dark: {
      keyword: "#4bbf70", type: "#af94d2", string: "#e2a6ba", number: "#f8dc90", comment: "#a1a293",
      variable: "#87deed", function: "#8fbce9", operator: "#fbc2ac", ident: "#ebebe7",
      bracket1: "#c7aa8e", bracket2: "#d2a6cf", bracket3: "#8ab8bf",
    },
    light: {
      keyword: "#006c2e", type: "#735390", string: "#71223b", number: "#573d00", comment: "#5f5f54",
      variable: "#004e62", function: "#093c76", operator: "#5b4036", ident: "#1f251b",
      bracket1: "#6d5441", bracket2: "#633555", bracket3: "#036780",
    },
    fontStyle: STYLE,
  },
  {
    id: "ember",
    // Orange keywords (h ≈ 55) among warm roles; teal types and lavender numbers are the only cool notes.
    label: "Ember",
    description: "Warm tones with orange keywords, for anyone who finds blue themes cold.",
    dark: {
      keyword: "#f88f03", type: "#77b3a4", string: "#a3c77b", number: "#a7a5f9", comment: "#af9b83",
      variable: "#f19fe0", function: "#f8d977", operator: "#ffbe98", ident: "#edebe5",
      bracket1: "#c9b9a3", bracket2: "#e3a857", bracket3: "#8fbfcf",
    },
    light: {
      keyword: "#9e4c00", type: "#004745", string: "#456947", number: "#403c7a", comment: "#6c6260",
      variable: "#8a4670", function: "#3f3c00", operator: "#5f3b2a", ident: "#13110f",
      bracket1: "#5c544c", bracket2: "#8a5300", bracket3: "#2f5f7a",
    },
    fontStyle: STYLE,
  },
  {
    id: "ocean",
    // Electric-blue keywords (h ≈ 255) in a cool palette; sand types and coral numbers are the warm literals.
    label: "Ocean",
    description: "Cool blues and aquas with warm literals: the familiar look, sharpened.",
    dark: {
      keyword: "#47a1ff", type: "#f2da63", string: "#6bebb2", number: "#fa9160", comment: "#949fab",
      variable: "#c3baff", function: "#33ecfe", operator: "#30c5f0", ident: "#eceff0",
      bracket1: "#a9bccf", bracket2: "#4fd1c5", bracket3: "#dca6e6",
    },
    light: {
      keyword: "#0057bf", type: "#7b5b00", string: "#00704a", number: "#a24601", comment: "#575b62",
      variable: "#562c8a", function: "#004745", operator: "#13687c", ident: "#13161a",
      bracket1: "#4b5d70", bracket2: "#0b6e68", bracket3: "#8a4598",
    },
    fontStyle: STYLE,
  },
];

export const DEFAULT_SYNTAX_THEME: SyntaxThemeId = "standard";
export const SYNTAX_THEME_IDS = SYNTAX_THEMES.map((t) => t.id);

/** An unknown or missing id (an old store.db, a hand-edited value) falls back to Standard. */
export function syntaxThemeById(id: string | undefined): SyntaxTheme {
  return SYNTAX_THEMES.find((t) => t.id === id) ?? SYNTAX_THEMES[0];
}

/**
 * Monaco token prefix → role. A rule also matches longer tokens ("identifier" covers identifier.quote and the
 * ".tsql" postfix); the most specific rule wins, so `number.hex` and `delimiter` need rules of their own or Monaco's
 * base theme colors them (0x1F ignored the theme before 2026-10-31).
 */
export const TOKEN_ROLES: { token: string; role: SyntaxRole }[] = [
  { token: "keyword", role: "keyword" },
  { token: "keyword.batch", role: "batch" },
  { token: "type", role: "type" },
  { token: "string", role: "string" },
  { token: "string.quote", role: "string" },
  { token: "string.dollar", role: "string" },
  { token: "string.escape", role: "escape" },
  { token: "number", role: "number" },
  { token: "number.hex", role: "number" },
  { token: "comment", role: "comment" },
  { token: "variable", role: "variable" },
  { token: "predefined", role: "function" },
  { token: "operator", role: "operator" },
  { token: "delimiter", role: "delimiter" },
  { token: "identifier", role: "ident" },
  { token: "identifier.temp", role: "temp" },
];

/** A role's color in a palette, with the defaults for the derived roles. `fg` is the editor foreground. */
export function roleColor(p: SyntaxPalette, role: SyntaxRole, fg: string): string {
  switch (role) {
    case "temp": return p.temp ?? p.ident;
    case "batch": return p.batch ?? p.keyword;
    case "escape": return p.escape ?? p.string;
    case "delimiter": return p.delimiter ?? fg;
    default: return p[role];
  }
}

/** Monaco theme rules for a theme in one mode (foreground without '#', as Monaco wants). */
export function syntaxRules(theme: SyntaxTheme, mode: "dark" | "light", fg: string) {
  const p = theme[mode];
  return TOKEN_ROLES.map(({ token, role }) => ({ token, foreground: roleColor(p, role, fg).slice(1, 7), fontStyle: theme.fontStyle[role] ?? "" }));
}

/** The Settings preview: a few lines already split into roles, so the picker needs no editor or tokenizer. */
export const PREVIEW: { text: string; role: SyntaxRole | "paren" | "plain" }[][] = [
  [{ text: "-- Orders in the last 30 days", role: "comment" }],
  [
    { text: "DECLARE", role: "keyword" }, { text: " ", role: "plain" }, { text: "@since", role: "variable" }, { text: " ", role: "plain" },
    { text: "DATE", role: "type" }, { text: " ", role: "plain" }, { text: "=", role: "operator" }, { text: " ", role: "plain" },
    { text: "GETDATE", role: "function" }, { text: "(", role: "paren" }, { text: ")", role: "paren" }, { text: " ", role: "plain" },
    { text: "-", role: "operator" }, { text: " ", role: "plain" }, { text: "30", role: "number" }, { text: ";", role: "delimiter" },
  ],
  [
    { text: "SELECT", role: "keyword" }, { text: " c", role: "ident" }, { text: ".", role: "delimiter" }, { text: "Name", role: "ident" },
    { text: ",", role: "delimiter" }, { text: " ", role: "plain" }, { text: "COUNT", role: "function" }, { text: "(", role: "paren" },
    { text: "*", role: "operator" }, { text: ")", role: "paren" }, { text: " ", role: "plain" }, { text: "AS", role: "keyword" },
    { text: " n ", role: "ident" }, { text: "FROM", role: "keyword" }, { text: " ", role: "plain" }, { text: "#top", role: "temp" },
    { text: " c", role: "ident" },
  ],
  [
    { text: "WHERE", role: "keyword" }, { text: " c", role: "ident" }, { text: ".", role: "delimiter" }, { text: "Status", role: "ident" },
    { text: " ", role: "plain" }, { text: "<>", role: "operator" }, { text: " ", role: "plain" }, { text: "N'void'", role: "string" },
    { text: ";", role: "delimiter" },
  ],
];

/**
 * Suggest/list widget colors → CSS variables. Every suggest color is set: the base themes pair a white selected
 * foreground with their own dark-blue selection, which turns unreadable on our neutral surfaces in the light theme.
 * The selected row uses --q-active (accent tint), never --q-hover: hover sits ~1.1:1 on --q-raised and is invisible.
 * The match highlight is a UI token (--q-suggest-match), never a syntax color: a syntax theme must not change chrome,
 * and the old keyword color fell to 3.54:1 on the selected row in the light theme.
 */
export const SUGGEST_COLORS: Record<string, string> = {
  "editorSuggestWidget.background": "--q-raised",
  "editorSuggestWidget.border": "--q-accent",
  "editorSuggestWidget.foreground": "--q-fg",
  "editorSuggestWidget.selectedBackground": "--q-active",
  "editorSuggestWidget.selectedForeground": "--q-fg",
  "editorSuggestWidget.selectedIconForeground": "--q-fg",
  "editorSuggestWidget.highlightForeground": "--q-suggest-match",
  "editorSuggestWidget.focusHighlightForeground": "--q-suggest-match",
  "editorSuggestWidgetStatus.foreground": "--q-muted",
  "list.activeSelectionBackground": "--q-active",
  "list.activeSelectionForeground": "--q-fg",
  "list.hoverBackground": "--q-hover",
  "list.hoverForeground": "--q-fg",
};

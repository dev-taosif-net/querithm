// Every user preference shown in Settings, declared once (settings redesign 2026-10-30): the page draws its rows from
// here, search reads the labels, descriptions and keywords, and "modified" / reset compare with defaultSettings. No
// React and no app imports beyond the settings store and the pure Explorer layout rules.
import { defaultSettings, settingLimits, type Settings } from "../store/settings";
import { modeOf } from "../explorer/layoutState";
import { ENGINES } from "../engines";
import type { Engine } from "../ipc/contract";

export type PageId = "appearance" | "editor" | "formatting" | "execution" | "tabs" | "explorer" | "keyboard" | "about";

export interface Page {
  id: PageId;
  title: string;
  /** Words that find the page itself in search ("version" finds About). */
  keywords?: string[];
}

export const pages: Page[] = [
  { id: "appearance", title: "Appearance", keywords: ["theme", "dark", "light", "accessibility", "screen reader"] },
  { id: "editor", title: "Editor", keywords: ["intellisense", "completion"] },
  { id: "formatting", title: "Formatting", keywords: ["format", "style"] },
  { id: "execution", title: "Query execution", keywords: ["run", "results"] },
  { id: "tabs", title: "Tabs & recovery", keywords: ["autosave", "restore"] },
  { id: "explorer", title: "Object Explorer", keywords: ["tree", "servers", "connections"] },
  { id: "keyboard", title: "Keyboard shortcuts", keywords: ["keys", "keybindings", "hotkeys", "shortcut"] },
  { id: "about", title: "About", keywords: ["version", "update", "updates", "license", "credit", "made by", "platform"] },
];

/** A row's control. `rowLimit`, `explorerMode` and `syntaxTheme` are custom rows drawn by the page itself. */
export type Control =
  | { kind: "switch" }
  | { kind: "segmented"; options: { value: string; label: string }[] }
  | { kind: "number"; min: number; max: number; unit: string; width?: number }
  | { kind: "rowLimit" }
  | { kind: "explorerMode" }
  | { kind: "syntaxTheme" };

export interface SettingDef {
  /** A Settings key, or a custom id whose value lives in `keys`. */
  id: keyof Settings | "explorerMode";
  /** The stored keys this row reads and resets. */
  keys: (keyof Settings)[];
  page: PageId;
  group: string;
  label: string;
  description: string;
  control: Control;
  /** Set when the setting does something on one engine only; `other` says what to do on the rest. */
  engine?: { only: Engine; other: string };
  keywords?: string[];
  /** Why the row can't be changed right now (it depends on another row), or null. */
  disabledWhy?: (s: Settings) => string | null;
  /** A note for a value that carries a risk, or null. */
  warning?: (s: Settings) => string | null;
  /** A neutral note that depends on the value, or null. */
  note?: (s: Settings) => string | null;
}

const ROW_LIMIT_WARN = 1_000_000;

export const settingDefs: SettingDef[] = [
  // Appearance
  {
    id: "theme", keys: ["theme"], page: "appearance", group: "Theme", label: "Color theme",
    description: "Match system follows your operating system's light or dark setting.",
    control: { kind: "segmented", options: [{ value: "dark", label: "Dark" }, { value: "light", label: "Light" }, { value: "system", label: "Match system" }] },
    keywords: ["dark", "light", "colour", "color", "appearance"],
  },
  {
    id: "syntaxTheme", keys: ["syntaxTheme"], page: "appearance", group: "Theme", label: "Syntax colors",
    description: "How SQL is colored in the editor. Each choice has a dark and a light version and follows the color theme.",
    control: { kind: "syntaxTheme" },
    keywords: ["syntax", "highlighting", "colour", "color", "editor", "color blind", "colour blind", "deuteranopia", "protanopia", "neon", "forest", "ember", "ocean"],
  },
  {
    id: "reducedDecorations", keys: ["reducedDecorations"], page: "appearance", group: "Display", label: "Reduce visual effects",
    description: "Turns off the minimap, line and occurrence highlights, sticky scroll and animations. Helps over remote desktop, in VMs and on slow GPUs.",
    control: { kind: "switch" }, keywords: ["performance", "gpu", "rdp", "animation", "decorations", "webkitgtk"],
  },
  {
    id: "uiScale", keys: ["uiScale"], page: "appearance", group: "Display", label: "Interface scale",
    description: "Makes all of Querithm larger or smaller: menus, the results grid and the editor. Ctrl+= and Ctrl+- change it in steps of 10 %, Ctrl+0 goes back to 100 %.",
    control: { kind: "number", ...settingLimits.uiScale, unit: "%" }, keywords: ["zoom", "scale", "text size", "font", "bigger", "smaller", "magnify", "accessibility", "low vision"],
    // The layout is laid out for windows of at least 960 × 560; scaled up, the window needs as much more room.
    note: (s) => (s.uiScale > 100 ? `At ${s.uiScale} % the layout has the room of the smallest window (960 × 560) only in a window of at least ${Math.round(9.6 * s.uiScale)} × ${Math.round(5.6 * s.uiScale)}.` : null),
  },
  {
    id: "announceBackground", keys: ["announceBackground"], page: "appearance", group: "Accessibility", label: "Announce background events",
    description: "Screen readers hear when a query finishes, a server drops or a transaction is left open on a tab you are not looking at.",
    control: { kind: "switch" }, keywords: ["screen reader", "accessibility", "nvda", "voiceover", "orca"],
  },
  // Editor
  {
    id: "editorFontSize", keys: ["editorFontSize"], page: "editor", group: "Text", label: "Font size",
    description: "Text in the SQL editor. The results grid keeps its own size.",
    control: { kind: "number", ...settingLimits.editorFontSize, unit: "px" }, keywords: ["font", "zoom", "text size"],
  },
  {
    id: "wordWrap", keys: ["wordWrap"], page: "editor", group: "Text", label: "Wrap long lines",
    description: "Long lines wrap at the edge of the editor instead of scrolling sideways.",
    control: { kind: "switch" }, keywords: ["word wrap"],
  },
  {
    id: "minimap", keys: ["minimap"], page: "editor", group: "Text", label: "Show minimap",
    description: "A zoomed-out overview of the script at the right edge of the editor.",
    control: { kind: "switch" },
    disabledWhy: (s) => (s.reducedDecorations ? "Off while Reduce visual effects is on (Appearance)." : null),
  },
  {
    id: "tier2", keys: ["tier2"], page: "editor", group: "IntelliSense", label: "Deep completions",
    description: "Columns of CTEs, derived and temp tables, parameters and variables, worked out by the engine from the whole script.",
    control: { kind: "switch" }, keywords: ["intellisense", "autocomplete", "tier-2", "ast", "completion"],
  },
  {
    id: "autoAlias", keys: ["autoAlias"], page: "editor", group: "IntelliSense", label: "Add an alias when completing a table",
    description: "Completing a table after FROM or JOIN adds a short alias, as in Orders ord.",
    control: { kind: "switch" }, keywords: ["intellisense", "autocomplete", "alias", "completion"],
  },
  // Formatting
  {
    id: "keywordCase", keys: ["keywordCase"], page: "formatting", group: "Keywords", label: "Keyword case",
    description: "Used by Format document, by completions and while typing.",
    control: { kind: "segmented", options: [{ value: "upper", label: "UPPER" }, { value: "lower", label: "lower" }, { value: "preserve", label: "Preserve" }] },
    keywords: ["uppercase", "lowercase", "capital"],
    note: (s) => (s.keywordCase === "preserve" ? "SQL Server: Format document writes keywords in Pascal case (Select). PostgreSQL keeps them as typed." : null),
  },
  {
    id: "keywordCaseOnType", keys: ["keywordCaseOnType"], page: "formatting", group: "Keywords", label: "Change keyword case as I type",
    description: "Reserved words take the keyword case as soon as you finish typing them.",
    control: { kind: "switch" },
    disabledWhy: (s) => (s.keywordCase === "preserve" ? "Nothing to change while Keyword case is Preserve." : null),
  },
  {
    id: "indentSize", keys: ["indentSize"], page: "formatting", group: "Layout", label: "Indent",
    description: "Spaces per level in Format document.",
    control: { kind: "number", ...settingLimits.indentSize, unit: "spaces" }, keywords: ["tab", "spaces", "indentation"],
  },
  {
    id: "leadingCommas", keys: ["leadingCommas"], page: "formatting", group: "Layout", label: "Commas at line start",
    description: "Format document puts each comma at the start of the next line.",
    control: { kind: "switch" }, engine: { only: "mssql", other: "PostgreSQL formatting keeps commas at the end of the line." },
  },
  {
    id: "newlineBeforeJoin", keys: ["newlineBeforeJoin"], page: "formatting", group: "Layout", label: "JOIN on its own line",
    description: "Format document starts every JOIN on a new line.",
    control: { kind: "switch" }, engine: { only: "mssql", other: "PostgreSQL formatting decides JOIN line breaks itself." },
  },
  // Query execution
  {
    id: "maxRows", keys: ["maxRows"], page: "execution", group: "Results", label: "Rows fetched per result set",
    description: "Querithm stops reading after this many rows. It is not TOP or LIMIT: the server still runs the whole query.",
    control: { kind: "rowLimit" }, keywords: ["row limit", "max rows", "unlimited", "fetch", "50000"],
    warning: (s) =>
      s.maxRows === 0
        ? "No limit keeps every fetched row in memory; a very large result can run Querithm out of memory."
        : s.maxRows > ROW_LIMIT_WARN
          ? `More than ${ROW_LIMIT_WARN.toLocaleString("en-US")} rows are kept in memory; a very large result can run Querithm out of memory.`
          : null,
  },
  {
    id: "resultLayout", keys: ["resultLayout"], page: "execution", group: "Results", label: "Several result sets",
    description: "How a run that returns more than one result set is laid out, unless a tab chose its own in the Results tools.",
    control: { kind: "segmented", options: [{ value: "tabs", label: "Tabs" }, { value: "stacked", label: "Stacked (SSMS)" }, { value: "side", label: "Side by side" }] },
    keywords: ["multiple result sets", "layout", "ssms", "stacked", "side by side", "tabs", "grid"],
  },
  {
    id: "statisticsTime", keys: ["statisticsTime"], page: "execution", group: "Server statistics", label: "Server CPU and elapsed time",
    description: "Runs SET STATISTICS TIME ON, so the status bar and Messages show the time the server spent.",
    control: { kind: "switch" }, engine: { only: "mssql", other: "PostgreSQL: use Explain ▸ Actual plan for server timings." },
    keywords: ["set statistics time", "timing", "cpu"],
  },
  {
    id: "statisticsIo", keys: ["statisticsIo"], page: "execution", group: "Server statistics", label: "Reads per table",
    description: "Runs SET STATISTICS IO ON, so Messages show logical and physical reads for every table.",
    control: { kind: "switch" }, engine: { only: "mssql", other: "PostgreSQL: use Explain ▸ Actual plan (buffers) for reads." },
    keywords: ["set statistics io", "reads", "logical reads"],
  },
  {
    id: "notebookStopOnError", keys: ["notebookStopOnError"], page: "execution", group: "Notebooks", label: "Run all stops at the first failing cell",
    description: "Cells after a cell that failed are not run. Turn off to run every cell; a cancelled cell always stops the run.",
    control: { kind: "switch" }, keywords: ["notebook", "sqlbook", "run all", "error"],
  },
  // Tabs & recovery
  {
    id: "autosave", keys: ["autosave"], page: "tabs", group: "Recovery", label: "Recover unsaved tabs after a restart",
    description: "Keeps a copy of every tab's text in Querithm's data folder. It never writes to your .sql files.",
    control: { kind: "switch" }, keywords: ["autosave", "backup", "crash"],
    warning: (s) => (s.autosave ? null : "Unsaved text is lost if Querithm or the computer stops unexpectedly."),
  },
  {
    id: "autoTitle", keys: ["autoTitle"], page: "tabs", group: "Tab names", label: "Name untitled tabs from their first statement",
    description: "At the first run, SQLQuery3 takes a name such as SELECT Orders. A name you gave is never changed.",
    control: { kind: "switch" }, keywords: ["title", "rename"],
  },
  {
    id: "tabListGroupByConnection", keys: ["tabListGroupByConnection"], page: "tabs", group: "Tab names", label: "Group the open-tabs list by connection",
    description: "Ctrl+P lists tabs under their server and database.",
    control: { kind: "switch" }, keywords: ["ctrl+p", "tab list"],
  },
  // Object Explorer
  {
    id: "explorerMode", keys: ["explorerVisible", "explorerAutoHide"], page: "explorer", group: "Window", label: "Object Explorer window",
    description: "Auto hide keeps it as a tab at the left edge. Closed: Ctrl+B opens it again.",
    control: { kind: "explorerMode" }, keywords: ["dock", "auto hide", "pin", "sidebar"],
  },
  {
    id: "explorerHoverOpen", keys: ["explorerHoverOpen"], page: "explorer", group: "Window", label: "Open on mouse over",
    description: "The auto-hidden explorer slides out when the pointer rests on its side tab.",
    control: { kind: "switch" }, keywords: ["hover"],
    disabledWhy: (s) => (modeOf({ visible: s.explorerVisible, autoHide: s.explorerAutoHide }) === "autoHide" ? null : "Only used while the explorer auto hides."),
  },
  {
    id: "explorerGroupByEnv", keys: ["explorerGroupByEnv"], page: "explorer", group: "Servers", label: "Group servers by environment",
    description: "Servers sit under Prod, Staging, Test, Dev and Local group rows.",
    control: { kind: "switch" }, keywords: ["environment", "prod"],
  },
  {
    id: "explorerShowAll", keys: ["explorerShowAll"], page: "explorer", group: "Servers", label: "Show all saved connections",
    description: "Lists every saved connection, not only the servers in use and pinned ones.",
    control: { kind: "switch" }, keywords: ["profiles", "saved"],
  },
  // About (the rest of the page is drawn by the page itself)
  {
    id: "autoUpdateCheck", keys: ["autoUpdateCheck"], page: "about", group: "Updates", label: "Check for updates automatically",
    description: "At start and every 6 hours Querithm reads the public release list. Nothing about you or your data is sent.",
    control: { kind: "switch" }, keywords: ["update", "upgrade", "release"],
    warning: (s) => (s.autoUpdateCheck ? null : "You won't hear about fixes and security updates."),
  },
];

/**
 * Settings keys deliberately not shown as rows: keybindings has its own page; the others are saved state rather than
 * preferences (pins, per-database grouping, the Explorer window flags behind explorerMode, where the results panel is
 * docked), or have no reader yet.
 */
export const notRows: (keyof Settings)[] = ["keybindings", "explorerSchemaGroups", "pinnedProfiles", "formatOnSave", "panelDock"];

export const defOf = (id: SettingDef["id"]) => settingDefs.find((d) => d.id === id)!;

/** Whether a row differs from its default. */
export function isModified(def: SettingDef, s: Settings): boolean {
  if (def.id === "explorerMode") return modeOf({ visible: s.explorerVisible, autoHide: s.explorerAutoHide }) !== modeOf({ visible: defaultSettings.explorerVisible, autoHide: defaultSettings.explorerAutoHide });
  return def.keys.some((k) => JSON.stringify(s[k]) !== JSON.stringify(defaultSettings[k]));
}

/** The default values of a row's keys (what Reset writes). */
export function defaultsOf(def: SettingDef): Partial<Settings> {
  return Object.fromEntries(def.keys.map((k) => [k, defaultSettings[k]])) as Partial<Settings>;
}

const norm = (t: string) => t.toLowerCase().replace(/\s+/g, " ").trim();

/** Rows whose label, description, group, engine note or keywords contain every word of the query. */
export function searchSettings(query: string): SettingDef[] {
  const words = norm(query).split(" ").filter(Boolean);
  if (!words.length) return [];
  return settingDefs.filter((d) => {
    const hay = norm([d.label, d.description, d.group, d.engine?.other ?? "", d.engine ? ENGINES[d.engine.only].shortLabel : "", ...(d.keywords ?? [])].join(" "));
    return words.every((w) => hay.includes(w));
  });
}

/** Pages found by their own title or keywords (About for "version", Keyboard shortcuts for "keys"). */
export function searchPages(query: string): Page[] {
  const q = norm(query);
  if (!q) return [];
  return pages.filter((p) => norm(p.title).includes(q) || (p.keywords ?? []).some((k) => k.includes(q) || q.includes(k)));
}

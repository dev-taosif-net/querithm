import { create } from "zustand";
import type { KeywordCase } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { joinBinding, splitBinding, UNBOUND, usableShortcut } from "../app/shortcuts";
import { toast } from "./ui";
import { DEFAULT_SYNTAX_THEME, SYNTAX_THEME_IDS, type SyntaxThemeId } from "../editor/syntaxTheme";

export interface Settings {
  theme: "dark" | "light" | "system";
  /** SQL token colors (§12 Theme); each theme has a dark and a light variant. */
  syntaxTheme: SyntaxThemeId;
  editorFontSize: number;
  minimap: boolean;
  wordWrap: boolean;
  /** Default row limit per result set; 0 = unlimited (§7 Row fetch limits). */
  maxRows: number;
  statisticsIo: boolean;
  statisticsTime: boolean;
  keywordCase: KeywordCase;
  /** Apply keywordCase to reserved words as they are typed (and to keyword completions). */
  keywordCaseOnType: boolean;
  indentSize: number;
  leadingCommas: boolean;
  newlineBeforeJoin: boolean;
  formatOnSave: boolean;
  /** Append a generated alias (`Orders ord`) to tables accepted in FROM/JOIN. */
  autoAlias: boolean;
  /** Ask the sidecar for tier-2 (AST) completions. */
  tier2: boolean;
  autosave: boolean;
  /** Command id → shortcut ("Ctrl+Shift+Enter"). Overrides the defaults (§12 Keyboard model). */
  keybindings: Record<string, string>;
  /** Linux WebKitGTK gate (§14): reduced decorations. */
  reducedDecorations: boolean;
  /** Object Explorer window state (§12): shown at all, and docked vs auto-hide. */
  explorerVisible: boolean;
  explorerAutoHide: boolean;
  /** Slide the auto-hidden explorer out when the pointer rests on its side tab (Visual Studio default). */
  explorerHoverOpen: boolean;
  /** Show connected servers under environment group rows (otherwise a flat list ordered by environment). */
  explorerGroupByEnv: boolean;
  /** List every saved profile in the Object Explorer, not only the servers in use and pinned ones (§12). */
  explorerShowAll: boolean;
  /** Databases whose tree is grouped by schema, as schemaGroupKey(profileId, database). */
  explorerSchemaGroups: string[];
  /** Profile ids pinned to the top of the Connect dialog, in pin order. */
  pinnedProfiles: string[];
  /** Open-tabs list (Ctrl+P) groups tabs under their connection. */
  tabListGroupByConnection: boolean;
  /** Ask the release server for a newer version at start and every 6 hours (§16 updater). */
  autoUpdateCheck: boolean;
  /** Tell screen readers about tabs out of sight: a query finished, a server dropped, a transaction left open (D23). */
  announceBackground: boolean;
  /** Untitled tabs take a verb-first name from their first statement at the first run or save (D24). */
  autoTitle: boolean;
  /** Notebooks (Phase C3): Run all stops at the first cell that fails. */
  notebookStopOnError: boolean;
  /** Where query tabs put their results panel (§12 bottom panel): under the editor, or on its right. */
  panelDock: "bottom" | "right";
  /** Interface scale in percent (L26): the webview's page zoom, so text, the grid and the editor grow together (src/app/uiScale.ts). */
  uiScale: number;
}

export const schemaGroupKey = (profileId: string, database: string) => `${profileId}|${database}`;

export const defaultSettings: Settings = {
  theme: "dark",
  syntaxTheme: DEFAULT_SYNTAX_THEME,
  editorFontSize: 13,
  minimap: true,
  wordWrap: false,
  maxRows: 50_000,
  statisticsIo: false,
  statisticsTime: true,
  keywordCase: "upper",
  keywordCaseOnType: true,
  indentSize: 4,
  leadingCommas: false,
  newlineBeforeJoin: true,
  formatOnSave: false,
  autoAlias: true,
  tier2: true,
  autosave: true,
  keybindings: {},
  reducedDecorations: false,
  explorerVisible: true,
  explorerAutoHide: false,
  explorerHoverOpen: true,
  explorerGroupByEnv: false,
  explorerShowAll: false,
  explorerSchemaGroups: [],
  pinnedProfiles: [],
  tabListGroupByConnection: false,
  autoUpdateCheck: true,
  announceBackground: true,
  autoTitle: true,
  notebookStopOnError: true,
  panelDock: "bottom",
  uiScale: 100,
};

/** Numeric ranges: the store clamps to them on load and on every change, and the Settings page shows them. */
export const settingLimits = {
  editorFontSize: { min: 9, max: 24 },
  indentSize: { min: 1, max: 8 },
  /** 0 = no limit. */
  maxRows: { min: 0, max: 10_000_000 },
  uiScale: { min: 90, max: 150 },
} as const satisfies Partial<Record<keyof Settings, { min: number; max: number }>>;

const choices: Partial<Record<keyof Settings, readonly string[]>> = {
  theme: ["dark", "light", "system"],
  panelDock: ["bottom", "right"],
  syntaxTheme: SYNTAX_THEME_IDS,
  keywordCase: ["upper", "lower", "preserve"],
};

/** One value read from store.db or passed to set(): the cleaned value, or undefined when it is unusable. */
function clean<K extends keyof Settings>(key: K, v: unknown): Settings[K] | undefined {
  const d = defaultSettings[key];
  const limit = (settingLimits as Partial<Record<keyof Settings, { min: number; max: number }>>)[key];
  if (limit) return (typeof v === "number" && Number.isFinite(v) ? Math.min(limit.max, Math.max(limit.min, Math.round(v))) : undefined) as Settings[K] | undefined;
  const allowed = choices[key];
  if (allowed) return (typeof v === "string" && allowed.includes(v) ? v : undefined) as Settings[K] | undefined;
  if (key === "keybindings") {
    if (!v || typeof v !== "object" || Array.isArray(v)) return undefined;
    // A shortcut that can't fire or traps the keyboard (Tab, a bare letter) is dropped; the command keeps its default.
    const out: Record<string, string> = {};
    for (const [id, b] of Object.entries(v)) {
      if (typeof b !== "string") continue;
      const all = splitBinding(b);
      const keys = all.filter(usableShortcut);
      if (!all.length && b.trim()) out[id] = UNBOUND;
      else if (keys.length) out[id] = joinBinding(keys);
    }
    return out as Settings[K];
  }
  if (Array.isArray(d)) return (Array.isArray(v) && v.every((x) => typeof x === "string") ? v : undefined) as Settings[K] | undefined;
  return (typeof v === typeof d ? v : undefined) as Settings[K] | undefined;
}

const settingKeys = Object.keys(defaultSettings) as (keyof Settings)[];

/** Saved settings are untrusted: wrong types and unknown choices fall back to defaults, numbers are clamped. */
export function sanitizeSettings(raw: unknown): { values: Partial<Settings>; extra: Record<string, unknown> } {
  const values: Partial<Settings> = {};
  const extra: Record<string, unknown> = {};
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return { values, extra };
  for (const [k, v] of Object.entries(raw)) {
    if (!(k in defaultSettings)) {
      extra[k] = v;
      continue;
    }
    const c = clean(k as keyof Settings, v);
    if (c !== undefined) (values as Record<string, unknown>)[k] = c;
  }
  return { values, extra };
}

const KEY = "ui.settings";

interface SettingsStore extends Settings {
  loaded: boolean;
  load(): Promise<void>;
  set(patch: Partial<Settings>): void;
}

/**
 * Persistence state, kept out of the settings store so it never lands in ui.settings. `loadFailed`: the saved settings
 * could not be read, so nothing is written until a later read succeeds (defaults must never overwrite them).
 */
export const useSettingsStatus = create<{ save: "saved" | "pending" | "saving" | "failed"; error?: string; loadFailed: boolean }>(() => ({
  save: "saved",
  loadFailed: false,
}));

let saveTimer: ReturnType<typeof setTimeout> | undefined;
/** Keys a newer app version wrote: kept and written back, so running an older version loses nothing. */
let extra: Record<string, unknown> = {};
/** Keys changed while the saved settings were unreadable; they win over what a later read returns. */
const changedUnread = new Set<keyof Settings>();
let warned = false;

async function readSaved(): Promise<Partial<Settings>> {
  const entries = await rpc("settings.get", { keys: [KEY] });
  const saved = entries.find((e) => e.key === KEY);
  if (!saved) return {};
  let raw: unknown;
  try {
    raw = JSON.parse(saved.valueJson);
  } catch {
    raw = null; // unreadable JSON holds nothing to keep
  }
  const r = sanitizeSettings(raw);
  extra = r.extra;
  return r.values;
}

function persisted(s: Settings): Settings {
  return Object.fromEntries(settingKeys.map((k) => [k, s[k]])) as unknown as Settings;
}

function failed(e: unknown) {
  const error = e instanceof Error ? e.message : String(e);
  useSettingsStatus.setState({ save: "failed", error });
  if (!warned) toast("error", `Settings could not be saved: ${error}. Querithm tries again when you change a setting or the engine restarts.`);
  warned = true;
}

async function persist(): Promise<void> {
  saveTimer = undefined;
  if (useSettingsStatus.getState().loadFailed) {
    try {
      const saved = await readSaved();
      const cur = useSettings.getState();
      const mine = Object.fromEntries([...changedUnread].map((k) => [k, cur[k]]));
      useSettings.setState({ ...saved, ...mine });
      changedUnread.clear();
      useSettingsStatus.setState({ loadFailed: false });
    } catch (e) {
      failed(e);
      return;
    }
  }
  useSettingsStatus.setState({ save: "saving" });
  try {
    await rpc("settings.set", { key: KEY, valueJson: JSON.stringify({ ...extra, ...persisted(useSettings.getState()) }) });
    warned = false;
    // A change made while this write was in flight has its own timer; it reports "pending" until then.
    useSettingsStatus.setState({ save: saveTimer ? "pending" : "saved", error: undefined });
  } catch (e) {
    failed(e);
  }
}

/** Writes a pending change now (app closing, update restart) instead of after the debounce. */
export async function flushSettings(): Promise<void> {
  if (!saveTimer) return;
  clearTimeout(saveTimer);
  await persist();
}

/** Tries a failed save again (Retry in Settings, engine restarted). */
export async function retrySettingsSave(): Promise<void> {
  if (useSettingsStatus.getState().save !== "failed") return;
  clearTimeout(saveTimer);
  await persist();
}

export const useSettings = create<SettingsStore>((set) => ({
  ...defaultSettings,
  loaded: false,
  async load() {
    try {
      const saved = await readSaved();
      set({ ...defaultSettings, ...saved, loaded: true });
      useSettingsStatus.setState({ loadFailed: false });
    } catch {
      // Run on defaults, but keep what is on disk: it is read again before the first write.
      set({ loaded: true });
      useSettingsStatus.setState({ loadFailed: true });
    }
  },
  set(patch) {
    const clean_: Partial<Settings> = {};
    for (const [k, v] of Object.entries(patch)) {
      const c = clean(k as keyof Settings, v);
      if (c === undefined) continue;
      (clean_ as Record<string, unknown>)[k] = c;
      if (useSettingsStatus.getState().loadFailed) changedUnread.add(k as keyof Settings);
    }
    set(clean_);
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => void persist(), 400);
    if (useSettingsStatus.getState().save !== "failed") useSettingsStatus.setState({ save: "pending" });
  },
}));

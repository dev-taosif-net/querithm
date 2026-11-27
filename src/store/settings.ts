import { create } from "zustand";
import type { KeywordCase } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { withTimeLimit } from "../ipc/timeLimit";
import { joinBinding, splitBinding, UNBOUND, usableShortcut } from "../app/shortcuts";
import { toast } from "./ui";
import { DEFAULT_SYNTAX_CHOICE, SYNTAX_CHOICES, SYNTAX_THEME_IDS, type SyntaxChoice, type SyntaxThemeId } from "../editor/syntaxTheme";
import { DARK_APP_THEME_IDS, LIGHT_APP_THEME_IDS } from "../theme/appThemes";

export interface Settings {
  /** The mode (§12 Theme): the dark pick, the light pick, or follow the OS between them ("system"). */
  theme: "dark" | "light" | "system";
  /** The App Theme shown in dark mode (plans/app-themes §3): a dark theme id; "dark" = Querithm Dark. */
  darkTheme: string;
  /** The App Theme shown in light mode: a light theme id; "light" = Querithm Light. */
  lightTheme: string;
  /**
   * SQL token colors: "default" (the App Theme's own palette, following the theme) or one of the six syntax themes,
   * which keep their colors on every App Theme. Replaces `syntaxTheme`, which is still written as a mirror for older
   * builds (`legacySyntaxMirror`).
   */
  syntaxColors: SyntaxChoice;
  /** Comments in the SQL editor are italic; off draws them upright (astigmatism, dyslexia). */
  italicComments: boolean;
  /** Move cursor down one line after toggling/commenting a single line with no selection. */
  commentAdvanceCursor: boolean;
  /** On Ctrl+/, a partial selection within a single line wraps/unwraps with `/* ... *\/` instead of line-commenting. */
  commentInlineBlock: boolean;
  /** Where `-- ` is placed when commenting lines: aligned to common indent, or at column 1. */
  commentPlacement: "indent" | "column1";
  /** Skip empty/whitespace-only lines when commenting a multi-line selection. */
  commentSkipBlankLines: boolean;
  /** Line comment prefix used on MySQL connections (`-- ` or `# `). */
  mysqlLineCommentStyle: "-- " | "# ";
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
  /** Grouped connections sit under their group (Group › Environment › Connection) in the Object Explorer and the Connect
   *  dialog; connections without a group stay where they are. Has no visible effect until a connection has a group. */
  showGroups: boolean;
  /** List every saved profile in the Object Explorer, not only the servers in use (§12). */
  explorerShowAll: boolean;
  /** Databases whose tree is grouped by schema, as schemaGroupKey(profileId, database). */
  explorerSchemaGroups: string[];
  /** Custom display order of connection groups (group names), set by dragging in the Object Explorer. */
  groupOrder: string[];
  /** Custom display order of profiles/connections (profile ids), set by dragging in the Object Explorer. */
  profileOrder: string[];
  /**
   * Profile ids by last successful connect, newest first (at most RECENT_PROFILES_MAX). The Connect dialog opens on the
   * newest one that is listed and breaks search ties with it; it has no Recent section of its own.
   */
  recentProfiles: string[];
  /** Profiles whose "name says prod, tag says otherwise" hint the user turned off in the Connect dialog. */
  prodHintMuted: string[];
  /** Open-tabs list (Ctrl+P) groups tabs under their connection. */
  tabListGroupByConnection: boolean;
  /** Ask the release server for a newer version at start and every 6 hours (§16 updater). */
  autoUpdateCheck: boolean;
  /** Tell screen readers about tabs out of sight: a query finished, a server dropped, a transaction left open (D23). */
  announceBackground: boolean;
  /** Untitled tabs take a verb-first name from their first statement at the first run or save (D24). */
  autoTitle: boolean;
  /**
   * How an editor tab is drawn (§16 2026-11-19): one line with the database at its end (default), the same with untitled
   * tabs that keep their number (no D24 name), the two-line strip of D18, or two lines with PRD / STG written out.
   */
  tabLayout: TabLayout;
  /** Notebooks (Phase C3): Run all stops at the first cell that fails. */
  notebookStopOnError: boolean;
  /** Where query tabs put their results panel (§12 bottom panel): under the editor, or on its right. */
  panelDock: "bottom" | "right";
  /**
   * How a run with several result sets is laid out (§12 bottom panel): one at a time behind the ledger chips, one under
   * another (as SSMS shows them), or side by side. New tabs use it; each tab can choose its own in the Results tools.
   */
  resultLayout: "tabs" | "stacked" | "side";
  /** Interface scale in percent (L26): the webview's page zoom, so text, the grid and the editor grow together (src/app/uiScale.ts). */
  uiScale: number;
  /**
   * Production confirmations (§13 rule 7, §16 2026-11-18 / 2026-11-27): "strict" (first run + destructive + writes),
   * "risky" (default: destructive + writes, SELECT runs without asking), or "off". The sidecar enforces it and owns
   * the value (store.db key `guard.prodConfirmations`, PROD_GUARD_KEY), so it is not part of ui.settings.
   */
  prodConfirmations: ProdConfirmMode;
}

export const PROD_CONFIRM_MODES = ["strict", "risky", "off"] as const;
export type ProdConfirmMode = (typeof PROD_CONFIRM_MODES)[number];
export const PROD_CONFIRM_LABELS: Record<ProdConfirmMode, string> = {
  strict: "Strict",
  risky: "Risky only",
  off: "Off",
};

/** Parses the sidecar's `guard.prodConfirmations` value identically to QueryGuard.ParseSetting (unknown fails closed to strict). */
export function parseProdGuardSetting(valueJson: string | null | undefined): ProdConfirmMode {
  if (valueJson == null) return defaultSettings.prodConfirmations;
  const t = valueJson.trim();
  if (t === '"risky"') return "risky";
  if (t === '"strict"' || t === "true") return "strict";
  if (t === '"off"' || t === "false") return "off";
  return "strict";
}

export const TAB_LAYOUTS = ["singleLine", "numbered", "twoLine", "guard"] as const;
export type TabLayout = (typeof TAB_LAYOUTS)[number];
/** One line, 32 px high; the two-line layouts are 40 px (the strip and the Object Explorer's header follow it). */
export const isSingleLineLayout = (l: TabLayout) => l === "singleLine" || l === "numbered";

/** The sidecar's own key for `prodConfirmations` (QueryGuard.SettingKey). */
export const PROD_GUARD_KEY = "guard.prodConfirmations";

export const schemaGroupKey = (profileId: string, database: string) => `${profileId}|${database}`;

/** How many profiles `recentProfiles` remembers. */
export const RECENT_PROFILES_MAX = 20;

/** `recent` with `id` moved to the front, capped at RECENT_PROFILES_MAX. */
export function touchRecent(recent: readonly string[], id: string): string[] {
  return [id, ...recent.filter((x) => x !== id)].slice(0, RECENT_PROFILES_MAX);
}

export const defaultSettings: Settings = {
  theme: "dark",
  darkTheme: "dark",
  lightTheme: "light",
  syntaxColors: DEFAULT_SYNTAX_CHOICE,
  italicComments: true,
  commentAdvanceCursor: false,
  commentInlineBlock: true,
  commentPlacement: "indent",
  commentSkipBlankLines: true,
  mysqlLineCommentStyle: "-- ",
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
  showGroups: true,
  explorerShowAll: false,
  explorerSchemaGroups: [],
  groupOrder: [],
  profileOrder: [],
  recentProfiles: [],
  prodHintMuted: [],
  tabListGroupByConnection: false,
  autoUpdateCheck: true,
  announceBackground: true,
  autoTitle: true,
  tabLayout: "singleLine",
  notebookStopOnError: true,
  panelDock: "bottom",
  resultLayout: "tabs",
  uiScale: 100,
  prodConfirmations: "risky",
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
  resultLayout: ["tabs", "stacked", "side"],
  darkTheme: DARK_APP_THEME_IDS,
  lightTheme: LIGHT_APP_THEME_IDS,
  syntaxColors: SYNTAX_CHOICES,
  keywordCase: ["upper", "lower", "preserve"],
  tabLayout: TAB_LAYOUTS,
  commentPlacement: ["indent", "column1"],
  mysqlLineCommentStyle: ["-- ", "# "],
  prodConfirmations: PROD_CONFIRM_MODES,
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
  if (key === "recentProfiles") return (Array.isArray(v) && v.every((x) => typeof x === "string") ? [...new Set(v)].slice(0, RECENT_PROFILES_MAX) : undefined) as Settings[K] | undefined;
  if (Array.isArray(d)) return (Array.isArray(v) && v.every((x) => typeof x === "string") ? v : undefined) as Settings[K] | undefined;
  return (typeof v === typeof d ? v : undefined) as Settings[K] | undefined;
}

/** Settings whose value the sidecar owns under its own key; never written into ui.settings. */
const sidecarOwned: (keyof Settings)[] = ["prodConfirmations"];

/** Keys of saved settings that are dropped, never kept: on a plain object they reach its prototype. */
const UNSAFE_KEYS = new Set(["__proto__", "constructor", "prototype"]);
const hasOwn = (o: object, k: string) => Object.prototype.hasOwnProperty.call(o, k);

/** The value older builds read in `syntaxTheme`: the five other 2026-11 themes as themselves, anything else (Default, a
 *  later id) as "standard". A contract for every later version (plans/app-themes §3.5). */
export const legacySyntaxMirror = (c: string): SyntaxThemeId =>
  c !== "standard" && (SYNTAX_THEME_IDS as readonly string[]).includes(c) ? (c as SyntaxThemeId) : "standard";

/** Old `syntaxTheme` → `syntaxColors`: an explicit non-Standard theme is kept; Standard (which cannot be told from the old
 *  default), a removed id, junk or nothing become Default (plans/app-themes §3.4). */
export const migrateSyntaxTheme = (v: unknown): SyntaxChoice =>
  typeof v === "string" && v !== "standard" && (SYNTAX_THEME_IDS as readonly string[]).includes(v) ? (v as SyntaxThemeId) : "default";

/**
 * Saved settings are untrusted: wrong types and unknown choices fall back to defaults, numbers are clamped.
 * `migrated`: the object predates `syntaxColors` and was given one (once; written back at once by load()).
 * `adopted`: an older build changed `syntaxTheme` after this one wrote it (a real pick there, kept here).
 */
export function sanitizeSettings(raw: unknown): { values: Partial<Settings>; extra: Record<string, unknown>; migrated: boolean; adopted: boolean } {
  const values: Partial<Settings> = {};
  const extra: Record<string, unknown> = {};
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return { values, extra, migrated: false, adopted: false };
  for (const [k, v] of Object.entries(raw)) {
    // JSON.parse keeps "__proto__" as an ordinary key; assigning it to `extra` would replace that object's prototype.
    if (UNSAFE_KEYS.has(k)) continue;
    if (sidecarOwned.includes(k as keyof Settings)) continue; // read from the sidecar's key, never from ui.settings
    // Own keys only: `in` also matches Object.prototype's names (toString, constructor), which are no settings.
    if (!hasOwn(defaultSettings, k)) {
      extra[k] = v;
      continue;
    }
    const c = clean(k as keyof Settings, v);
    if (c !== undefined) (values as Record<string, unknown>)[k] = c;
  }
  const has = (k: string) => hasOwn(raw, k);
  const old = (raw as Record<string, unknown>).syntaxTheme;
  let migrated = false, adopted = false;
  if (!has("syntaxColors")) {
    values.syntaxColors = migrateSyntaxTheme(old);
    migrated = true;
  } else if (typeof old === "string" && old !== "standard" && (SYNTAX_THEME_IDS as readonly string[]).includes(old) && old !== legacySyntaxMirror(String((raw as Record<string, unknown>).syntaxColors))) {
    values.syntaxColors = old as SyntaxThemeId;
    adopted = true;
  }
  return { values, extra, migrated, adopted };
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

/**
 * The longest one settings call may take before it counts as failed (CLAUDE §4: no unbounded waits). Longer than the
 * store's own busy timeout (30 s, LocalStore), so a write waiting on another instance's lock is answered first (written,
 * or storeBusy): reaching this limit means the engine is stuck, not merely waiting its turn.
 */
export const SETTINGS_RPC_LIMIT_MS = 35_000;
const limited = <T>(p: Promise<T>, what: string) =>
  withTimeLimit(p, SETTINGS_RPC_LIMIT_MS, `the engine did not answer ${what} within ${SETTINGS_RPC_LIMIT_MS / 1000} s`);

let saveTimer: ReturnType<typeof setTimeout> | undefined;
/**
 * Keys set in this session since their last successful write, each with the version of its last set() (plans/app-themes
 * §3.5): a save sends only these, merged into ui.settings by the sidecar under the store's write lock (settings.patch),
 * so a second window's or a newer version's keys are kept, and from now on a saved key means a choice. A set() during a
 * write keeps the key dirty.
 */
const dirty = new Map<keyof Settings, number>();
let dirtyVersion = 0;
let warned = false;
/** What the last successful read found: whether ui.settings existed and whether it was migrated or adopted (§3.2). */
let lastRead = { hadSaved: false, migrated: false, adopted: false };
/** The one-time "Syntax colors now follow your color theme" notice is due (§3.6). */
let syntaxNoticeDue = false;

async function readSaved(): Promise<Partial<Settings>> {
  const entries = await limited(rpc("settings.get", { keys: [KEY, PROD_GUARD_KEY] }), "the settings read");
  // As the sidecar reads it (QueryGuard.ParseSetting): missing = risky, legacy true/false = strict/off, unknown = strict.
  const guard = entries.find((e) => e.key === PROD_GUARD_KEY);
  const owned: Partial<Settings> = guard ? { prodConfirmations: parseProdGuardSetting(guard.valueJson) } : {};
  const saved = entries.find((e) => e.key === KEY);
  if (!saved) {
    lastRead = { hadSaved: false, migrated: false, adopted: false };
    return owned;
  }
  let raw: unknown;
  try {
    raw = JSON.parse(saved.valueJson);
  } catch {
    raw = null; // unreadable JSON holds nothing to keep
  }
  const r = sanitizeSettings(raw);
  lastRead = { hadSaved: !!raw && typeof raw === "object" && !Array.isArray(raw), migrated: r.migrated, adopted: r.adopted };
  return { ...r.values, ...owned };
}

/** True once after a load that moved a saved Standard (or nothing) to Default; the app shows its notice then. */
export function takeSyntaxMigrationNotice(): boolean {
  const due = syntaxNoticeDue;
  syntaxNoticeDue = false;
  return due;
}

/** In-flight `guard.prodConfirmations` write (null when idle); rapid picks are serialized and coalesced to the latest mode. */
let prodInFlight: Promise<void> | null = null;
/** Last value confirmed on the sidecar for the current in-flight write burst (null when idle). */
let prodConfirmed: ProdConfirmMode | null = null;
/** Latest mode requested while a `guard.prodConfirmations` write is already in flight. */
let prodQueued: ProdConfirmMode | null = null;

/**
 * Writes the Prod confirmations mode to the sidecar at once (no debounce: the guard follows it), serialized one RPC at
 * a time with a bounded wait so rapid clicks never finish out of order or hang forever. On failure, rolls back to the
 * last value confirmed on the sidecar and tells the user.
 */
function writeProdConfirmations(mode: ProdConfirmMode, before: ProdConfirmMode): void {
  if (prodInFlight) {
    prodQueued = mode;
    return;
  }
  prodConfirmed = before;
  let current: ProdConfirmMode | null = mode;
  prodInFlight = (async () => {
    try {
      while (current !== null) {
        const target: ProdConfirmMode = current;
        prodQueued = null;
        try {
          await limited(rpc("settings.set", { key: PROD_GUARD_KEY, valueJson: JSON.stringify(target) }), "the production confirmation write");
          prodConfirmed = target;
        } catch (e) {
          if (prodQueued === null && useSettings.getState().prodConfirmations === target) {
            useSettings.setState({ prodConfirmations: prodConfirmed! });
            const error = e instanceof Error ? e.message : String(e);
            toast("error", `The production confirmation setting could not be saved: ${error}. It is still ${PROD_CONFIRM_LABELS[prodConfirmed!]}.`);
          }
        }
        const next: ProdConfirmMode | null = prodQueued;
        current = next !== null && next !== prodConfirmed ? next : null;
      }
    } finally {
      prodInFlight = null;
      prodConfirmed = null;
      prodQueued = null;
    }
  })();
}

function failed(e: unknown) {
  const error = e instanceof Error ? e.message : String(e);
  useSettingsStatus.setState({ save: "failed", error });
  if (!warned) toast("error", `Settings could not be saved: ${error}. Querithm tries again when you change a setting or the engine restarts.`);
  warned = true;
}

/** The write in flight or queued last (resolves, never rejects): writes run one after another, never side by side. */
let writing: Promise<void> = Promise.resolve();
/** A write queued behind the one in flight, not started yet: later calls join it (it reads `dirty` when it starts). */
let queued: Promise<void> | undefined;

/**
 * Saves this session's changes. One write at a time: two side by side could finish out of order, and the older one
 * would put back a value the newer one had just saved (and clear it from `dirty`). Each call is bounded
 * (SETTINGS_RPC_LIMIT_MS), so one the engine never answers fails the save instead of holding every later one.
 */
function persist(): Promise<void> {
  clearTimeout(saveTimer);
  saveTimer = undefined;
  if (!queued) {
    const next = writing.then(() => {
      queued = undefined;
      return writeSettings();
    });
    queued = next;
    writing = next.catch(() => {});
  }
  return queued;
}

async function writeSettings(): Promise<void> {
  if (useSettingsStatus.getState().loadFailed) {
    try {
      while (prodInFlight) await prodInFlight;
      const saved = await readSaved();
      // If a prod confirmation write started while readSaved() was in flight, keep the newer optimistic mode.
      if (prodInFlight) delete saved.prodConfirmations;
      const cur = useSettings.getState();
      const mine = Object.fromEntries([...dirty.keys()].map((k) => [k, cur[k]]));
      useSettings.setState({ ...saved, ...mine });
      useSettingsStatus.setState({ loadFailed: false });
    } catch (e) {
      failed(e);
      return;
    }
  }
  useSettingsStatus.setState({ save: "saving" });
  const pending = new Map(dirty);
  try {
    const s = useSettings.getState();
    const json = (v: unknown) => JSON.stringify(v);
    const set: Record<string, string> = {};
    for (const k of pending.keys()) if (!sidecarOwned.includes(k)) set[k] = json(s[k]);
    if (pending.has("syntaxColors")) set.syntaxTheme = json(legacySyntaxMirror(s.syntaxColors));
    // Nothing saved yet (a fresh install's first save): the syntax colors and their mirror go in too, or the next load
    // would read an object without them as an older build's and move it to Default with the one-time notice. An object
    // that has keys but no syntaxColors is an older build's: load() migrates it, and shows the notice with it (§3.2). The
    // sidecar decides "nothing saved yet" under the same lock as the write (SettingsPatchParams.setWhenNew).
    const setWhenNew = { syntaxColors: json(s.syntaxColors), syntaxTheme: json(legacySyntaxMirror(s.syntaxColors)) };
    // One call, merged by the sidecar under the store's write lock: keys another window or a newer version wrote stay as
    // they are, even when that window saves at the same moment (a read here and a write in the next call could not
    // promise that). The sidecar-owned keys never belong in ui.settings, so an old copy there is dropped.
    await limited(rpc("settings.patch", { key: KEY, set, setWhenNew, remove: [...sidecarOwned] }), "the settings write");
    for (const [k, v] of pending) if (dirty.get(k) === v) dirty.delete(k);
    warned = false;
    // A change made while this write was in flight has its own timer; it reports "pending" until then.
    useSettingsStatus.setState({ save: saveTimer ? "pending" : "saved", error: undefined });
  } catch (e) {
    failed(e);
  }
}

/**
 * Writes a pending change now (app closing, update restart) instead of after the debounce, after the write in flight;
 * with nothing pending it still waits for that write, so closing never cuts one off.
 */
export async function flushSettings(): Promise<void> {
  await (saveTimer ? persist() : writing);
  while (prodInFlight) await prodInFlight;
}

/** Tries a failed save again (Retry in Settings, engine restarted). */
export async function retrySettingsSave(): Promise<void> {
  if (useSettingsStatus.getState().save !== "failed") return;
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
      // A saved object that predates Syntax colors (or one an older build changed) is written back once, now, so the
      // migration never runs twice and its notice shows once (plans/app-themes §3.2, §3.6).
      if (lastRead.hadSaved && (lastRead.migrated || lastRead.adopted)) {
        useSettings.getState().set({ syntaxColors: saved.syntaxColors });
        if (lastRead.migrated && saved.syntaxColors === "default") syntaxNoticeDue = true;
        void flushSettings();
      }
    } catch {
      // Run on defaults, but keep what is on disk: it is read again before the first write.
      // loadFailed first: what follows `loaded` (the theme kept for the next start, App.tsx) must not take the
      // defaults for the saved settings.
      useSettingsStatus.setState({ loadFailed: true });
      set({ loaded: true });
    }
  },
  set(patch) {
    const prodBefore = useSettings.getState().prodConfirmations;
    const clean_: Partial<Settings> = {};
    let uiChange = false;
    for (const [k, v] of Object.entries(patch)) {
      const c = clean(k as keyof Settings, v);
      if (c === undefined) continue;
      (clean_ as Record<string, unknown>)[k] = c;
      if (sidecarOwned.includes(k as keyof Settings)) continue; // written to the sidecar's own key below
      dirty.set(k as keyof Settings, ++dirtyVersion);
      uiChange = true;
    }
    set(clean_);
    if (clean_.prodConfirmations !== undefined && clean_.prodConfirmations !== prodBefore) void writeProdConfirmations(clean_.prodConfirmations, prodBefore);
    // Nothing for ui.settings (only a sidecar-owned key, or no usable value): no read and write of an unchanged object,
    // unless a failed save is waiting for the next change to be tried again.
    if (!uiChange && useSettingsStatus.getState().save !== "failed") return;
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => void persist(), 400);
    if (useSettingsStatus.getState().save !== "failed") useSettingsStatus.setState({ save: "pending" });
  },
}));

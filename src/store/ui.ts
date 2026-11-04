import { create } from "zustand";
import type { GridConflictRow, GuardBlock, SshHostKey, Dialect, Engine, RefactorTarget, Snippet, TextSpan } from "../ipc/contract";
import type { GridStaging } from "../results/staging";
import type { PageId as SettingsPage } from "../settings/registry";
import { announce } from "./announce";

export interface Toast {
  id: number;
  kind: "info" | "error" | "success" | "warn";
  text: string;
  /** How many times the same message came while it was shown (M20: identical toasts are joined, not stacked). */
  count?: number;
}

/**
 * How long a toast stays (M20, §12): 5 s for info and success, 10 s for a warning, 15 s for an error, plus 50 ms for
 * each character past 80 (about 20 characters a second), at most 30 s. The time stops while the pointer is over the
 * toasts or focus is inside them, and at least 2 s is left when it starts again.
 */
export const TOAST_MS: Record<Toast["kind"], number> = { info: 5000, success: 5000, warn: 10_000, error: 15_000 };
export const TOAST_MS_PER_CHAR = 50;
export const TOAST_MAX_MS = 30_000;
export const TOAST_RESUME_MS = 2000;

export function toastDuration(kind: Toast["kind"], text: string): number {
  return Math.min(TOAST_MAX_MS, TOAST_MS[kind] + Math.max(0, text.length - 80) * TOAST_MS_PER_CHAR);
}

export type Dialog =
  | { kind: "connect" }
  | { kind: "profile"; profileId?: string; preset?: Engine; copyOf?: string }
  | { kind: "password"; profileId: string; resolve: (secret: string | null) => void; error?: string; ssh?: boolean }
  | { kind: "hostKey"; hostKey: SshHostKey; resolve: (trust: boolean) => void }
  | { kind: "guard"; block: GuardBlock; resolve: (ok: boolean) => void }
  /** `quit`: asked while the app closes (H4), not for one tab. */
  | { kind: "txClose"; tabId: string; quit?: boolean; resolve: (choice: "commit" | "rollback" | "cancel") => void }
  /** Closing a tab whose query is still running: true = cancel the query and close, false = keep the tab. */
  | { kind: "busyClose"; tabId: string; quit?: boolean; resolve: (cancelAndClose: boolean) => void }
  | { kind: "txSwitch"; tabId: string; resolve: (ok: boolean) => void }
  | { kind: "params"; names: string[]; resolve: (values: { name: string; value: string; isNull: boolean; type?: string }[] | null) => void }
  | { kind: "confirm"; title: string; body: string; danger?: boolean; ok?: string; cancel?: string; resolve: (ok: boolean) => void }
  | { kind: "diff"; title: string; left: string; right: string; leftTitle: string; rightTitle: string; resolve?: (ok: boolean) => void; okLabel?: string; language: string }
  | { kind: "execProc"; tabId?: string; profileId: string; database: string; objectId: number }
  | { kind: "cellViewer"; title: string; value: string; language?: string; onSave?: (value: string) => void }
  | { kind: "gridPreview"; title: string; sql: string; notes: string[]; engine: Engine; resolve: (apply: boolean) => void }
  | { kind: "gridConflicts"; conflicts: GridConflictRow[]; staging: GridStaging; resolve: (choice: "overwrite" | "discard" | "cancel") => void }
  | { kind: "gridPending"; tabId: string; summary: string; resolve: (choice: "apply" | "discard" | "cancel") => void }
  /** `sessionId`: a session-owned engine's query tab session the wizard reads and imports through (Phase D0d). */
  | { kind: "import"; profileId: string; database: string; schema?: string; table?: string; sessionId?: string }
  /** Settings, opened at a page (default: the last one used this session); `focus` puts focus on that setting or control. */
  | { kind: "settings"; page?: SettingsPage; focus?: string }
  | { kind: "update" }
  | { kind: "history" }
  | { kind: "dependencies"; profileId: string; database: string; schema: string; name: string; objectId?: number | null; kind2: string }
  | { kind: "authPrompt"; message: string; url?: string | null; code?: string | null }
  | { kind: "debugCall"; title: string; sql: string; resolve: (sql: string | null) => void }
  | { kind: "snippets" }
  | { kind: "snippetEdit"; snippet: Snippet }
  | {
      kind: "rename"; profileId?: string; database?: string; target?: RefactorTarget; fromTabId?: string;
      /** A local symbol of one tab: renamed in the editor only. */
      local?: { tabId: string; spans: TextSpan[]; original: string; dialect: Dialect };
    };

/** `only` (tabs mode): list just these tab ids, e.g. the tabs scrolled out of a strip. */
export type Palette = { mode: "commands" | "objects" | "databases" | "connections" | "tabs" | "snippets"; initial?: string; only?: string[] } | null;

/** Ctrl+Tab switcher: tab ids in MRU order and the highlighted one. Open while Ctrl is held. */
export type Switcher = { ids: string[]; index: number } | null;

interface UiStore {
  dialogs: Dialog[];
  palette: Palette;
  switcher: Switcher;
  toasts: Toast[];
  banner: { kind: "warn" | "error"; text: string; action?: { label: string; run: () => void } } | null;
  explorerWidth: number;
  /** The auto-hidden explorer is slid out (transient; the mode lives in settings). */
  explorerFlyout: boolean;
  openDialog(d: Dialog): void;
  closeDialog(d?: Dialog): void;
  setPalette(p: Palette): void;
  setSwitcher(s: Switcher): void;
  toast(kind: Toast["kind"], text: string): void;
  dismissToast(id: number): void;
  /** The pointer is over the toasts, or focus is inside them: their time stops until both have left. */
  holdToasts(why: "pointer" | "focus", on: boolean): void;
  setBanner(b: UiStore["banner"]): void;
  setExplorer(p: Partial<Pick<UiStore, "explorerWidth" | "explorerFlyout">>): void;
}

let toastId = 0;
/** Each shown toast's time left, and its timer while it runs. Outside the store: a tick must not re-render the list. */
const toastTimers = new Map<number, { left: number; due: number; timer?: ReturnType<typeof setTimeout> }>();
const toastHolds = new Set<"pointer" | "focus">();

function runToast(id: number) {
  const t = toastTimers.get(id);
  if (!t || toastHolds.size) return;
  clearTimeout(t.timer);
  t.due = Date.now() + t.left;
  t.timer = setTimeout(() => useUi.getState().dismissToast(id), t.left);
}

function stopToast(id: number) {
  const t = toastTimers.get(id);
  if (!t?.timer) return;
  clearTimeout(t.timer);
  t.timer = undefined;
  t.left = Math.max(0, t.due - Date.now());
}

export const useUi = create<UiStore>((set, get) => ({
  dialogs: [],
  palette: null,
  switcher: null,
  toasts: [],
  banner: null,
  explorerWidth: 280,
  explorerFlyout: false,
  openDialog(d) {
    set({ dialogs: [...get().dialogs, d] });
  },
  closeDialog(d) {
    const list = get().dialogs;
    set({ dialogs: d ? list.filter((x) => x !== d) : list.slice(0, -1) });
  },
  setPalette(p) {
    set({ palette: p });
  },
  setSwitcher(s) {
    set({ switcher: s });
  },
  toast(kind, text) {
    // The same message while it is still shown: counted on that toast, its time starts over, and it is not read again.
    const same = get().toasts.find((t) => t.kind === kind && t.text === text);
    if (same) {
      set({ toasts: get().toasts.map((t) => (t === same ? { ...t, count: (t.count ?? 1) + 1 } : t)) });
      const timer = toastTimers.get(same.id);
      if (timer) {
        stopToast(same.id);
        timer.left = toastDuration(kind, text);
        runToast(same.id);
      }
      return;
    }
    const id = ++toastId;
    // With no toast shown nothing can be under the pointer or hold focus, whatever leave event went missing.
    if (!get().toasts.length) toastHolds.clear();
    const kept = get().toasts.slice(-4);
    for (const t of get().toasts) {
      if (kept.includes(t)) continue;
      stopToast(t.id);
      toastTimers.delete(t.id);
    }
    set({ toasts: [...kept, { id, kind, text }] });
    toastTimers.set(id, { left: toastDuration(kind, text), due: 0 });
    runToast(id);
    announce(kind === "error" ? `Error: ${text}` : kind === "warn" ? `Warning: ${text}` : text, kind === "error" || kind === "warn" ? "assertive" : "polite");
  },
  dismissToast(id) {
    stopToast(id);
    toastTimers.delete(id);
    const toasts = get().toasts.filter((t) => t.id !== id);
    // The last toast gone under the pointer or with focus in it: no leave event may follow, so nothing holds the next one.
    if (!toasts.length) toastHolds.clear();
    set({ toasts });
  },
  holdToasts(why, on) {
    const was = toastHolds.size > 0;
    if (on) toastHolds.add(why);
    else toastHolds.delete(why);
    if (was === toastHolds.size > 0) return;
    for (const id of toastTimers.keys()) {
      if (toastHolds.size) stopToast(id);
      else {
        const t = toastTimers.get(id)!;
        t.left = Math.max(t.left, TOAST_RESUME_MS);
        runToast(id);
      }
    }
  },
  setBanner(b) {
    // A new banner (or new text in it) is read at once: they say the engine stopped or restarted, or a file was damaged.
    if (b && b.text !== get().banner?.text) announce(b.text, "assertive");
    set({ banner: b });
  },
  setExplorer(p) {
    set(p);
  },
}));

/** Opens a dialog and resolves when the dialog calls its resolve callback. */
export function ask<T>(make: (resolve: (v: T) => void) => Dialog): Promise<T> {
  return new Promise<T>((resolve) => {
    const ui = useUi.getState();
    let dialog: Dialog;
    const done = (v: T) => {
      useUi.getState().closeDialog(dialog);
      resolve(v);
    };
    dialog = make(done);
    ui.openDialog(dialog);
  });
}

export const toast = (kind: Toast["kind"], text: string) => useUi.getState().toast(kind, text);

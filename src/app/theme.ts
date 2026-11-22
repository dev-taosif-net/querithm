// The theme at start (§16). The setting lives in the engine's store and loads only after the engine answers; until
// then the window showed the default (dark), so a light window opened dark and jumped. The theme last shown is
// therefore kept in two more places, both only a copy (the setting stays the source of truth and corrects them):
// - the webview's own storage, read by public/boot-theme.js before the first paint (same key) and by the app until
//   the settings have loaded;
// - a one-word file the shell keeps (src-tauri/src/theme.rs), for the colour of the window before the webview has
//   painted anything.
import { isTauri } from "../ipc/transport";
import { shell } from "../ipc/client";
import type { Settings } from "../store/settings";

export type Theme = Settings["theme"];

/** Where the theme last shown is kept for the next start: one of three words, not user data. */
export const THEME_CACHE = "querithm.theme";

const THEMES: readonly string[] = ["dark", "light", "system"] satisfies Theme[];

/** The theme the window had when it last ran, if a known one was kept. */
export function cachedTheme(): Theme | null {
  try {
    const raw = localStorage.getItem(THEME_CACHE);
    return raw != null && THEMES.includes(raw) ? (raw as Theme) : null;
  } catch {
    return null; // storage refused (a locked-down webview): the window starts in the default
  }
}

/** Whether `theme` shows dark now ("system" follows the OS). */
export function isDark(theme: Theme): boolean {
  return theme === "dark" || (theme === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
}

/** What the shell was last told in this run: it is told once per start (its file may be older than ours) and per change. */
let told: Theme | null = null;

/** Keeps the loaded setting for the next start. Never fails the caller: a copy not kept only costs the next start its theme. */
export function keepTheme(theme: Theme) {
  try {
    if (localStorage.getItem(THEME_CACHE) !== theme) localStorage.setItem(THEME_CACHE, theme);
  } catch {
    // Not kept: the next start opens in the default until the settings have loaded, as before.
  }
  if (!isTauri || told === theme) return;
  told = theme;
  shell.themeKeep(theme).catch(() => {
    if (told === theme) told = null; // told again at the next change or start
  });
}

/** Tests: forget what the shell was told. */
export function resetKeptTheme() {
  told = null;
}

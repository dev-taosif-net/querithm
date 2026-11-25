// The theme at start (§16). The setting lives in the engine's store and loads only after the engine answers; until
// then the window showed the default (dark), so a light window opened dark and jumped. The theme last shown is
// therefore kept in two more places, both only a copy (the setting stays the source of truth and corrects them):
// - the webview's own storage, read by public/boot-theme.js before the first paint (same key) and by the app until
//   the settings have loaded;
// - a one-line file the shell keeps (src-tauri/src/theme.rs): the setting's word and both picks' backgrounds, for the
//   colour of the window before the webview has painted anything.
import { isTauri } from "../ipc/transport";
import { shell } from "../ipc/client";
import { withTimeLimit } from "../ipc/timeLimit";
import type { Settings } from "../store/settings";
import { appThemeById, type AppTheme } from "../theme/appThemes";
import { SYNTAX_CHOICES, type SyntaxChoice } from "../editor/syntaxTheme";

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

/**
 * The line the shell keeps (src-tauri/src/theme.rs, plans/app-themes §6.3): the setting's word and the window colour of
 * the dark and of the light pick, e.g. "system #221a13 #ffede0". Colours, never theme ids: a new App Theme changes no Rust.
 */
export function shellThemeText(theme: Theme, dark: AppTheme, light: AppTheme): string {
  return `${theme} ${dark.ui.bg.toLowerCase()} ${light.ui.bg.toLowerCase()}`;
}

/** The longest the shell waits on one write before the next change may be sent (CLAUDE §4: no unbounded waits). */
export const SHELL_KEEP_LIMIT_MS = 2000;
/** What the shell was last told in this run: it is told once per start (its file may be older than ours) and per change. */
let told: string | null = null;
/**
 * The latest line to tell; only the latest is sent, one call at a time. A call given up on (SHELL_KEEP_LIMIT_MS) may
 * still be writing: the shell writes one line at a time, in the order the calls reach it (theme.rs `keep`), so the newer
 * line still lands last.
 */
let wanted: string | null = null;
let telling = false;

async function tellShell() {
  telling = true;
  try {
    while (wanted !== null && wanted !== told) {
      const text = wanted;
      try {
        await withTimeLimit(shell.themeKeep(text), SHELL_KEEP_LIMIT_MS, "no answer from the shell");
        told = text;
      } catch {
        told = null;
        if (wanted === text) break; // told again at the next change or start
      }
    }
  } finally {
    telling = false;
  }
}

/**
 * Keeps the loaded setting and both picks' window colours for the next start. Never fails the caller: a copy not kept
 * only costs the next start its theme.
 */
export function keepTheme(theme: Theme, dark: AppTheme, light: AppTheme) {
  try {
    if (localStorage.getItem(THEME_CACHE) !== theme) localStorage.setItem(THEME_CACHE, theme);
  } catch {
    // Not kept: the next start opens in the default until the settings have loaded, as before.
  }
  if (!isTauri) return;
  wanted = shellThemeText(theme, dark, light);
  if (!telling) void tellShell();
}

/** Tests: forget what the shell was told. */
export function resetKeptTheme() {
  told = null;
  wanted = null;
}

/** The App Themes' start colours for public/boot-theme.js (plans/app-themes §6.2): same key, same shape. */
export const THEME_BOOT_CACHE = "querithm.themeBoot";
export interface BootColors {
  id: string;
  bg: string;
  fg: string;
  muted: string;
  /** The start screen's idle tier and track (boot-splash.css --s-idle / --s-track). */
  idle: string;
  track: string;
}
/** Querithm Dark / Light keep the splash colours they always had. */
const LEGACY_SPLASH: Record<string, { idle: string; track: string }> = { dark: { idle: "#1e222e", track: "#1b1f2a" }, light: { idle: "#e3e8ef", track: "#eef1f6" } };
export function bootColors(t: AppTheme): BootColors {
  const splash = LEGACY_SPLASH[t.id] ?? { idle: t.ui.raised, track: t.ui.panel2 };
  return { id: t.id, bg: t.ui.bg, fg: t.ui.fg, muted: t.ui.muted, ...splash };
}

/**
 * The choice the window last showed, read back from both boot caches for the time before the settings have loaded
 * (plans/app-themes §6.2), so the app keeps what public/boot-theme.js put on the document instead of switching to the
 * defaults and back. Each part only when valid: the Theme word, each pick only if it is an App Theme of its mode, the
 * syntax colors only if a known choice. Storage that throws or a damaged cache gives nothing.
 */
export function cachedThemeChoice(): { theme?: Theme; darkTheme?: string; lightTheme?: string; syntaxColors?: SyntaxChoice } {
  const out: ReturnType<typeof cachedThemeChoice> = {};
  const theme = cachedTheme();
  if (theme) out.theme = theme;
  let boot: unknown = null;
  try {
    boot = JSON.parse(localStorage.getItem(THEME_BOOT_CACHE) ?? "null");
  } catch {
    return out;
  }
  if (!boot || typeof boot !== "object" || (boot as { v?: unknown }).v !== 1) return out;
  const b = boot as { dark?: { id?: unknown }; light?: { id?: unknown }; syntax?: unknown };
  const pick = (mode: "dark" | "light") => {
    const id = b[mode]?.id;
    return typeof id === "string" && appThemeById(id)?.mode === mode ? id : undefined;
  };
  const dark = pick("dark");
  const light = pick("light");
  if (dark) out.darkTheme = dark;
  if (light) out.lightTheme = light;
  if (typeof b.syntax === "string" && (SYNTAX_CHOICES as readonly string[]).includes(b.syntax)) out.syntaxColors = b.syntax as SyntaxChoice;
  return out;
}

/** The start screen's colours (`--boot-*`, boot-splash.css) follow the theme shown, for the splash Restart engine shows
 *  again; boot-theme.js set them only for the theme of the start, and not at all without a boot cache. Written only
 *  when they change, and never read back from computed style (no forced style recalculation). */
export function setBootColors(root: HTMLElement, t: AppTheme) {
  const c = bootColors(t);
  const vars: [string, string][] = [["--boot-bg", c.bg], ["--boot-fg", c.fg], ["--boot-muted", c.muted], ["--boot-idle", c.idle], ["--boot-track", c.track]];
  for (const [name, value] of vars) if (root.style.getPropertyValue(name) !== value) root.style.setProperty(name, value);
}

/**
 * Keeps both picks' start colours and the syntax choice for the next start, so its first frame is the App Theme the
 * window had. Written only from the loaded settings (as keepTheme), and only when it changed. Never fails the caller.
 */
export function keepThemeBoot(dark: AppTheme, light: AppTheme, syntax: string) {
  const text = JSON.stringify({ v: 1, dark: bootColors(dark), light: bootColors(light), syntax });
  try {
    if (localStorage.getItem(THEME_BOOT_CACHE) !== text) localStorage.setItem(THEME_BOOT_CACHE, text);
  } catch {
    // Not kept: the next start shows Querithm Dark / Light until the settings have loaded, as before.
  }
}

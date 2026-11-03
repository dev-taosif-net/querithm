// Shortcut strings (§12 Keyboard model): how a key event is written, how saved bindings are read, and which shortcuts a
// user may bind. No app imports, so the settings store can clean saved bindings without loading the command table.

/** Read once at import; tests pin it by stubbing navigator before the import. */
export const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform);

/** Shortcuts written with "Ctrl" mean Cmd on macOS. */
export function displayKeys(k: string) {
  // MacCtrl first: its "Ctrl+" would otherwise read as Cmd ("Mac⌘Tab").
  return isMac ? k.replace(/MacCtrl\+/g, "⌃").replace(/Ctrl\+/g, "⌘").replace(/Shift\+/g, "⇧").replace(/Alt\+/g, "⌥") : k;
}

/** Spoken form for screen readers: "Command Shift F", never the ⌘⇧ glyphs. */
export function spokenKeys(k: string) {
  const { mods, key } = splitShortcut(k);
  const words: Record<string, string> = { Ctrl: isMac ? "Command" : "Control", MacCtrl: "Control", Alt: isMac ? "Option" : "Alt", Shift: "Shift" };
  const keyWords: Record<string, string> = { "/": "Slash", ",": "Comma", "-": "Minus", "+": "Plus", ".": "Period", "`": "Backquote" };
  return [...mods.map((m) => words[m] ?? m), keyWords[key] ?? key].join(" ");
}

/** A saved override is a comma list ("F5, Ctrl+Enter"); the comma of "Ctrl+," stays part of its shortcut. */
export function splitBinding(binding: string): string[] {
  return binding.split(/(?<!\+\s*)\s*,\s*/).map((s) => s.trim()).filter(Boolean);
}

/** An override with no keys: the command is unbound (an empty override would mean "use the defaults"). */
export const UNBOUND = ",";

export function joinBinding(keys: string[]): string {
  return keys.length ? keys.join(", ") : UNBOUND;
}

/** "Ctrl+Shift+F" → mods [Ctrl, Shift], key F; "Ctrl++" → key "+". */
export function splitShortcut(sc: string): { mods: string[]; key: string } {
  const s = sc.trim();
  const i = s.length > 1 ? s.lastIndexOf("+", s.length - 2) : -1;
  return { mods: i < 0 ? [] : s.slice(0, i).split("+").map((p) => p.trim()).filter(Boolean), key: i < 0 ? s : s.slice(i + 1) };
}

function normalizeKey(key: string): string {
  if (key === " ") return "Space";
  if (key.length === 1) return key.toUpperCase();
  if (key === "Break" || key === "Cancel") return "Pause";
  if (key.startsWith("Arrow")) return key.slice(5);
  return key;
}

export function eventToShortcut(e: KeyboardEvent): string {
  const parts: string[] = [];
  if (isMac ? e.metaKey : e.ctrlKey) parts.push("Ctrl");
  if (isMac && e.ctrlKey) parts.push("MacCtrl");
  if (e.altKey) parts.push("Alt");
  if (e.shiftKey) parts.push("Shift");
  let key = normalizeKey(e.key);
  // Shifted punctuation and Alt-modified letters: fall back to the physical key code.
  if (e.code.startsWith("Key")) key = e.code.slice(3);
  else if (e.code.startsWith("Digit")) key = e.code.slice(5);
  else if (e.code === "Comma") key = ",";
  else if (e.code === "Minus") key = "-";
  else if (e.code === "Equal") key = "=";
  else if (e.code === "Slash") key = "/";
  parts.push(key);
  return parts.join("+");
}

/** "Ctrl+K Ctrl+C": a two-stroke chord, handled by Monaco and never matched against a single key event. */
export function isChord(s: string): boolean {
  return /\+\S*\s+\S*\+/.test(s.trim());
}

const MOD_ORDER = ["Ctrl", "MacCtrl", "Alt", "Shift"];

/** The form eventToShortcut produces: modifiers in a fixed order, a one-character key upper-cased. */
export function canonical(s: string): string {
  const { mods, key: rawKey } = splitShortcut(s);
  const key = rawKey.toLowerCase() === "comma" ? "," : rawKey.length === 1 ? rawKey.toUpperCase() : rawKey;
  return [...MOD_ORDER.filter((m) => mods.some((p) => p.toLowerCase() === m.toLowerCase())), key].join("+");
}

/** Text editing everywhere (editor, grid, fields): binding one would break it, since app shortcuts win in Monaco. */
const EDITING_KEYS = new Set(["Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z"]);
const MODIFIER_KEYS = new Set(["Control", "Shift", "Alt", "Meta", "OS", "AltGraph", "CapsLock"]);
const FUNCTION_KEY = /^F([1-9]|1\d|2[0-4])$/;

/**
 * Why a single stroke can't be a user shortcut, or null when it can. A shortcut needs Ctrl, Alt or (macOS) Control, or a
 * function key: a bare letter, Enter or Shift+letter would fire while typing, Tab and Escape move focus and close
 * dialogs (a keyboard trap if bound), the system keeps Alt+F4 and Cmd+Q, and copy, paste, undo, select all and the
 * editor's Ctrl+K chords keep working.
 */
export function shortcutProblem(sc: string): string | null {
  const { mods, key } = splitShortcut(sc);
  if (!key || MODIFIER_KEYS.has(key)) return "Press a key together with the modifier keys.";
  if (mods.some((m) => !MOD_ORDER.includes(m))) return `Unknown modifier in ${sc}.`;
  if (key === "Escape") return "Escape cancels and closes dialogs, so it can't be a shortcut.";
  if (key === "Tab" && !mods.includes("Ctrl") && !mods.includes("MacCtrl")) return "Tab moves focus, so it needs Ctrl to be a shortcut.";
  const c = canonical(sc);
  if (c === "Alt+F4" || (isMac && c === "Ctrl+Q")) return `${displayKeys(c)} belongs to the system.`;
  if (EDITING_KEYS.has(c)) return `${displayKeys(c)} is for copying, pasting, undoing and selecting text, so it can't be a shortcut.`;
  if (c === "Ctrl+K") return `${displayKeys(c)} starts the editor's two-key shortcuts (Ctrl+K Ctrl+C), so it can't be a shortcut.`;
  if (FUNCTION_KEY.test(key) || key === "Pause") return null;
  if (!mods.includes("Ctrl") && !mods.includes("Alt") && !mods.includes("MacCtrl"))
    return `Add ${isMac ? "Command, Option or Control" : "Ctrl or Alt"}: on its own ${displayKeys(c)} types or moves in the editor.`;
  return null;
}

/** Whether a saved binding string (one stroke or a chord) is usable; unusable saved bindings are dropped on load. */
export function usableShortcut(s: string): boolean {
  return s.trim().split(/\s+/).every((stroke) => shortcutProblem(stroke) === null);
}

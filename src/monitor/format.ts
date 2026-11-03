// Display helpers for the activity monitor. Missing values are "—", never 0 (plan step 7).
import type { ActivityRow, SessionKey } from "../ipc/contract";

export const DASH = "—";

export const keyOf = (k: SessionKey) => `${k.id}|${k.instance}`;

export function num(n: number | null | undefined): string {
  return n == null ? DASH : Math.round(n).toLocaleString("en-US");
}

export function ms(n: number | null | undefined): string {
  if (n == null) return DASH;
  if (n < 1_000) return `${Math.round(n)} ms`;
  const s = n / 1_000;
  if (s < 60) return `${s.toFixed(s < 10 ? 1 : 0)} s`;
  const m = s / 60;
  if (m < 60) return `${Math.floor(m)} min ${Math.round(s % 60)} s`;
  const h = m / 60;
  if (h < 24) return `${Math.floor(h)} h ${Math.round(m % 60)} min`;
  return `${Math.floor(h / 24)} d ${Math.round(h % 24)} h`;
}

export function bytes(n: number | null | undefined): string {
  if (n == null) return DASH;
  const u = ["B", "KB", "MB", "GB", "TB"];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(i === 0 || v >= 100 ? 0 : 1)} ${u[i]}`;
}

export function kb(n: number | null | undefined): string {
  return n == null ? DASH : bytes(n * 1024);
}

export const text = (s: string | null | undefined) => (s == null || s === "" ? DASH : s);

/**
 * Control characters, bidi overrides and other invisible code points (zero-width, word joiner, invisible operators, BOM,
 * soft hyphen, line / paragraph separators, tag characters) shown as escapes, so a server-returned name or query text
 * can never corrupt the grid, hide text or reorder what is drawn (S4). The result is still drawn as text, never markup.
 * Line breaks and tabs become spaces (one line per cell).
 */
export function escapeForDisplay(s: string): string {
  return escapeText(s, false);
}

/**
 * The same escapes for text shown as a block (statement text in the kill dialog, deadlock cards, request detail, server
 * messages): line breaks and tabs stay, CR LF and a lone CR become LF. Copy and "open in a new tab" keep the original.
 */
export function escapeBlock(s: string): string {
  return escapeText(s, true);
}

function escapeText(s: string, block: boolean): string {
  // Usually nothing needs escaping: return the string itself instead of rebuilding it (metric 28ui). The scan reads
  // code units; tag characters are the only astral code points escaped, and all start with the high surrogate 0xDB40.
  let i = 0;
  for (; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c === 0xdb40 || (needsEscape(c) && !(block && (c === 0x0a || c === 0x09)))) break;
  }
  if (i === s.length) return s;
  let out = s.slice(0, i);
  while (i < s.length) {
    const c = s.codePointAt(i)!; // a whole surrogate pair; a lone surrogate is copied as it is
    const units = c > 0xffff ? 2 : 1;
    if (c === 0x0a || c === 0x0d || c === 0x09) {
      if (!block) out += " ";
      else if (c === 0x0d) {
        out += "\n";
        if (s.charCodeAt(i + 1) === 0x0a) i++;
      } else out += s[i];
    } else out += needsEscape(c) ? escapeCode(c) : s.slice(i, i + units);
    i += units;
  }
  return out;
}

const escapeCode = (c: number) =>
  c < 0x20 || c === 0x7f || (c >= 0x80 && c < 0xa0) ? `\\x${c.toString(16).padStart(2, "0")}`
  : c > 0xffff ? `\\u{${c.toString(16)}}` : `\\u${c.toString(16).padStart(4, "0")}`;

/** Code points (not code units) that are escaped. */
const needsEscape = (c: number) =>
  c < 0x20 || c === 0x7f || (c >= 0x80 && c < 0xa0) || c === 0xad || c === 0x061c
  || (c >= 0x200b && c <= 0x200f) || c === 0x2028 || c === 0x2029 || (c >= 0x202a && c <= 0x202e)
  || (c >= 0x2060 && c <= 0x2064) || (c >= 0x2066 && c <= 0x2069) || c === 0xfeff || (c >= 0xe0000 && c <= 0xe007f);

/** Wait age bucket for colour (< 5 s, 5–30 s, > 30 s): theme tokens, light and dark (U6). */
export function waitColorVar(waitMs: number | null | undefined): string {
  if (waitMs == null) return "var(--q-muted)";
  if (waitMs < 5_000) return "var(--q-wait-short)";
  if (waitMs <= 30_000) return "var(--q-wait-mid)";
  return "var(--q-wait-long)";
}

export function isSleepingWithTx(r: ActivityRow) {
  return (r.status === "sleeping" || r.status.startsWith("idle in transaction")) && (r.openTx ?? 0) > 0;
}

export function isRunning(r: ActivityRow) {
  return r.status === "running" || r.status === "runnable" || r.status === "suspended" || r.status === "active";
}

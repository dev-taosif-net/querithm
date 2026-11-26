// FNV-1a hashes that name a piece of a model's text in the tier-2 cache key (providers.ts), without keeping the text.
import { commonPrefix } from "./completion/lexer";

const FNV_BASIS = 0x811c9dc5;

/** Characters hashed so far (tests: one keystroke must not re-hash a whole script). */
export const prefixHashStats = { hashed: 0 };

function fnv(s: string, from: number, to: number, h: number): number {
  for (let i = from; i < to; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193); }
  if (to > from) prefixHashStats.hashed += to - from;
  return h;
}

/** FNV-1a over a string: identifies a piece of text without keeping it. */
export function textHash(s: string): string {
  return (fnv(s, 0, s.length, FNV_BASIS) >>> 0).toString(36) + ":" + s.length;
}

/**
 * Per owner (a model): the FNV-1a states of its text every PREFIX_STEP characters (`states[i]`: after i × PREFIX_STEP),
 * for the text last hashed. A keystroke changes the text near the cursor, so hashing the text before the word starts
 * again from the last checkpoint before the first change: hashing all of it, twice per keystroke, cost a median 2.6 ms
 * on a 1.3 MB script (2026-11-25, completion audit M16).
 */
export const PREFIX_STEP = 4096;
const prefixStates = new WeakMap<object, { text: string; states: number[] }>();

/**
 * Characters of each end of a long tail that tailHash reads: the statement after the word is part of tier 2's key (its
 * FROM aliases, M15), but a 1 MB `INSERT … VALUES` after the cursor was hashed whole on every keystroke.
 */
export const TAIL_WINDOW = 8192;

/**
 * A key for text.slice(from, to): textHash of it while it is at most 2 × TAIL_WINDOW long, else of its first and last
 * TAIL_WINDOW characters and its length. An edit in the middle of a longer tail that keeps its length goes unseen.
 */
export function tailHash(text: string, from: number, to: number): string {
  const n = to - from;
  if (n <= 0) return "";
  if (n <= 2 * TAIL_WINDOW) return textHash(text.slice(from, to));
  return textHash(text.slice(from, from + TAIL_WINDOW)) + "/" + textHash(text.slice(to - TAIL_WINDOW, to)) + "/" + n;
}

/**
 * textHash(text.slice(0, end)) for the text of `owner`: the same value, re-reading only what changed since its last text.
 * An `end` past the text is its length (the text shrank while a tier-2 request was in flight).
 */
export function prefixHash(owner: object, text: string, end: number): string {
  end = Math.max(0, Math.min(end, text.length));
  let e = prefixStates.get(owner);
  if (!e) prefixStates.set(owner, (e = { text, states: [FNV_BASIS] }));
  else if (e.text !== text) {
    e.states.length = Math.min(e.states.length, Math.floor(commonPrefix(e.text, text) / PREFIX_STEP) + 1);
    e.text = text;
  }
  const states = e.states;
  const last = Math.floor(end / PREFIX_STEP);
  while (states.length <= last) {
    const i = states.length;
    states.push(fnv(text, (i - 1) * PREFIX_STEP, i * PREFIX_STEP, states[i - 1]));
  }
  return (fnv(text, last * PREFIX_STEP, end, states[last]) >>> 0).toString(36) + ":" + end;
}

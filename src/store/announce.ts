// One live region for things that happen out of sight (a bulk close's report, a background tab's query finishing)
// and for what a run returned: screen readers hear them without focus moving. At most one message a second; messages
// that arrive sooner are joined into the next one, so a burst is read once instead of cutting itself off (at most
// ANNOUNCE_MAX of them, then "… and N more."). A message is
// polite unless it says a statement failed, which interrupts (assertive); a joined burst takes the most urgent level.
// Toasts and the banner speak through here too (M20): errors and warnings assertive, the rest polite. A message already
// waiting in the burst, or read less than a second ago, is not said again, so a burst of identical toasts is read once.
import { create } from "zustand";

export const ANNOUNCE_GAP_MS = 1000;
/** W9: a burst reads at most this many messages, then "… and N more." (a flood of different toasts stays one short utterance). */
export const ANNOUNCE_MAX = 5;

export type AnnounceLevel = "polite" | "assertive";

interface AnnounceStore {
  /** The text in the live region; `n` changes on every message so the same text is read again. */
  text: string;
  n: number;
  level: AnnounceLevel;
}

export const useAnnounce = create<AnnounceStore>(() => ({ text: "", n: 0, level: "polite" }));

let last = -Infinity;
let lastText = "";
let queued: string[] = [];
/** Messages of this burst past ANNOUNCE_MAX: counted, not read (the set only joins identical ones). */
let more = new Set<string>();
let urgent = false;
let timer: ReturnType<typeof setTimeout> | undefined;

function flush() {
  timer = undefined;
  if (!queued.length) return;
  const text = more.size ? `${queued.join(" ")} … and ${more.size} more.` : queued.join(" ");
  const level: AnnounceLevel = urgent ? "assertive" : "polite";
  queued = [];
  more = new Set();
  urgent = false;
  last = Date.now();
  lastText = text;
  useAnnounce.setState((s) => ({ text, n: s.n + 1, level }));
}

/** Reads `text` to screen-reader users through the app's status region. */
export function announce(text: string, level: AnnounceLevel = "polite") {
  const t = text.trim();
  if (!t) return;
  const said = /[.!?]$/.test(t) ? t : `${t}.`;
  if (queued.includes(said) || more.has(said) || (said === lastText && Date.now() - last < ANNOUNCE_GAP_MS)) return;
  if (queued.length < ANNOUNCE_MAX) queued.push(said);
  else more.add(said);
  if (level === "assertive") urgent = true;
  if (timer) return;
  const wait = last + ANNOUNCE_GAP_MS - Date.now();
  if (wait <= 0) flush();
  else timer = setTimeout(flush, wait);
}

/** Tests: forget pending messages and the rate limit. */
export function resetAnnouncer() {
  if (timer) clearTimeout(timer);
  timer = undefined;
  queued = [];
  more = new Set();
  urgent = false;
  last = -Infinity;
  lastText = "";
  useAnnounce.setState({ text: "", n: 0, level: "polite" });
}

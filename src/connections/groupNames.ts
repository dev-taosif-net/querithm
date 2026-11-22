// Connection group names as the editor shows them before saving. The sidecar's GroupNames is the rule (§13 PT8-3):
// it cleans every name it stores and returns one spelling per group, so the UI groups by exact name and never folds
// case itself. This module only drives the editor's hint, the blur canonicalization and sorting; the shared fixture
// (__tests__/fixtures/group-names.json) keeps both sides in step.
import { GROUP_LIMITS, type ProfileGroup } from "../ipc/contract";

/** As the sidecar's GroupNames.MaxRawLength: longer text is refused before it is normalized. */
const MAX_RAW = 1024;

const REFUSAL = `The group name must be at most ${GROUP_LIMITS.maxNameLength} characters, without line breaks, control characters or invisible formatting characters.`;
const INVISIBLE = "The group name needs at least one visible letter, digit or symbol.";

/** Control, format, line and paragraph separators (the sidecar's CleanText with visible: true). */
const HIDDEN = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u;
/** Anything that draws: not only marks, spaces or the Hangul / Braille fillers. */
const VISIBLE = /[^\p{Mn}\p{Me}\p{Mc}\p{Zs}ᅟᅠㅤﾠ⠀]/u;

function hasLoneSurrogate(s: string): boolean {
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) {
      const n = s.charCodeAt(i + 1);
      if (!(n >= 0xdc00 && n <= 0xdfff)) return true;
      i++;
    } else if (c >= 0xdc00 && c <= 0xdfff) return true;
  }
  return false;
}

/** Trimmed, white space collapsed, NFC; `name` null = no group. `error` when the sidecar would refuse it. */
export function cleanGroupName(raw: string | null | undefined): { name: string | null; error?: string } {
  if (!raw) return { name: null };
  if (raw.length > MAX_RAW || hasLoneSurrogate(raw) || HIDDEN.test(raw)) return { name: null, error: REFUSAL };
  const collapsed = raw.replace(/\s+/g, " ").trim();
  if (!collapsed) return { name: null };
  const nfc = collapsed.normalize("NFC");
  if (nfc.length > GROUP_LIMITS.maxNameLength) return { name: null, error: REFUSAL };
  if (!VISIBLE.test(nfc)) return { name: null, error: INVISIBLE };
  return { name: nfc };
}

/** What two names share when they are one group: NFKC, then simple (one-to-one) upper-casing, as the sidecar's GroupNames.Key. */
export function groupKey(name: string): string {
  let out = "";
  for (const ch of name.normalize("NFKC")) {
    const up = ch.toUpperCase();
    out += [...up].length === 1 ? up : ch; // "ß" stays "ß": .NET's ToUpperInvariant maps code points one to one
  }
  return out;
}

/** The stored group a typed name joins, if any. */
export function findGroup(groups: readonly ProfileGroup[], raw: string | null | undefined): ProfileGroup | undefined {
  const { name } = cleanGroupName(raw);
  if (!name) return undefined;
  const key = groupKey(name);
  return groups.find((g) => groupKey(g.name) === key);
}

/**
 * What a Group field says about a typed name: it joins a group (with how many others are in it) or makes a new one.
 * `alreadyIn` is the group the profile is in now, so its own membership is not counted twice. Null for an empty or
 * refused name.
 */
export function describeGroupChoice(groups: readonly ProfileGroup[], raw: string | null | undefined, alreadyIn?: string | null): { joins: boolean; name: string; text: string } | null {
  const clean = cleanGroupName(raw);
  if (!clean.name || clean.error) return null;
  const g = findGroup(groups, clean.name);
  if (!g) return { joins: false, name: clean.name, text: `Creates group “${clean.name}”` };
  const already = !!alreadyIn && groupKey(alreadyIn) === groupKey(g.name);
  const others = Math.max(0, already ? g.members - 1 : g.members);
  const count = others === 0 ? "no other connections" : `${others} ${already ? "other " : ""}connection${others === 1 ? "" : "s"}`;
  return { joins: true, name: g.name, text: `${already ? "In" : "Joins"} “${g.name}” · ${count}` };
}

/** The order groups are listed in: ignoring case and accents, numbers by value ("Team 2" before "Team 10"). */
export const compareGroupNames = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "base", numeric: true }) || (a < b ? -1 : a > b ? 1 : 0);

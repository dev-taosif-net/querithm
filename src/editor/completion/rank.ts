// Tier-1 ranking (§9): exact prefix > camel-hump > substring; within a rank: group (nearest table's columns
// first), then usage count, then a per-item order, then alphabetical. Encoded into a lexicographic sortText.

export const MATCH_PREFIX = 0;
export const MATCH_HUMP = 1;
export const MATCH_SUBSTRING = 2;
export const MATCH_NONE = -1;

function isUpper(c: number): boolean { return c >= 65 && c <= 90; }
function isLower(c: number): boolean { return c >= 97 && c <= 122; }
function isDigit(c: number): boolean { return c >= 48 && c <= 57; }
function isAlnum(c: number): boolean { return isUpper(c) || isLower(c) || isDigit(c) || c > 127; }
function lower(c: number): number { return isUpper(c) ? c + 32 : c; }

/** Hump starts: index 0, an uppercase letter after a lowercase letter/digit, the last capital of an acronym
 *  followed by lowercase (XMLParser → P), a letter/digit after a separator (`_ . - $ space`), a digit run start. */
export function humpStarts(s: string): number[] {
  const out: number[] = [];
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (i === 0) { out.push(0); continue; }
    const p = s.charCodeAt(i - 1);
    if (!isAlnum(c)) continue;
    if (!isAlnum(p)) { out.push(i); continue; }
    if (isUpper(c) && (isLower(p) || isDigit(p))) { out.push(i); continue; }
    if (isUpper(c) && isUpper(p) && i + 1 < s.length && isLower(s.charCodeAt(i + 1))) { out.push(i); continue; }
    if (isDigit(c) && !isDigit(p)) { out.push(i); continue; }
  }
  return out;
}

/** Camel/underscore hump match: `cId` → CustomerId, `usp_gco` → usp_GetCustomerOrders. First char must match. */
export function humpMatch(prefix: string, cand: string): boolean {
  const pl = prefix.length, cl = cand.length;
  if (pl === 0) return true;
  if (pl > cl) return false;
  if (lower(prefix.charCodeAt(0)) !== lower(cand.charCodeAt(0))) return false;
  const humps = humpStarts(cand);
  const isHump = new Uint8Array(cl);
  for (const h of humps) isHump[h] = 1;
  const failed = new Uint8Array((pl + 1) * (cl + 1));
  const go = (pi: number, ci: number): boolean => {
    if (pi === pl) return true;
    if (ci >= cl) return false;
    const key = pi * (cl + 1) + ci;
    if (failed[key]) return false;
    const pc = lower(prefix.charCodeAt(pi));
    // continue the current hump
    if (lower(cand.charCodeAt(ci)) === pc && go(pi + 1, ci + 1)) return true;
    // jump to a later hump start (or a later separator char typed literally)
    for (let h = ci + 1; h < cl; h++) {
      if ((isHump[h] && lower(cand.charCodeAt(h)) === pc) || (!isAlnum(pc) && cand.charCodeAt(h) === pc)) {
        if (go(pi + 1, h + 1)) return true;
      }
    }
    failed[key] = 1;
    return false;
  };
  return go(1, 1);
}

/** Match rank of `prefix` against one candidate string (case-insensitive). */
export function matchRank(prefix: string, cand: string): number {
  if (!prefix) return MATCH_PREFIX;
  if (!cand) return MATCH_NONE;
  const p = prefix.toLowerCase();
  const c = cand.toLowerCase();
  if (c.startsWith(p)) return MATCH_PREFIX;
  if (humpMatch(prefix, cand)) return MATCH_HUMP;
  if (c.includes(p)) return MATCH_SUBSTRING;
  return MATCH_NONE;
}

/** Best (lowest) match rank over several candidate strings (label, bare name, filterText...). */
export function bestMatch(prefix: string, cands: readonly string[]): number {
  let best = MATCH_NONE;
  for (const c of cands) {
    const r = matchRank(prefix, c);
    if (r !== MATCH_NONE && (best === MATCH_NONE || r < best)) best = r;
    if (best === MATCH_PREFIX) break;
  }
  return best;
}

const pad = (n: number, w: number): string => {
  const s = String(Math.max(0, Math.floor(n)));
  return s.length >= w ? s.slice(-w) : "0".repeat(w - s.length) + s;
};

export interface RankKey {
  /** 0 = schema objects/columns/etc, 1 = keywords demoted below every object (object-expecting contexts). */
  tier: number;
  match: number;
  /** Lower = earlier: e.g. 0 FK snippets, 1 nearest table's columns, 2 other columns ... 8 keywords. */
  group: number;
  usage: number;
  /** Secondary order inside a group (e.g. parameter ordinal). */
  order: number;
  label: string;
}

/** Lexicographic sort key; zero-padded so string order equals numeric order. */
export function sortText(k: RankKey): string {
  const usage = Math.max(0, Math.min(999999, Math.floor(k.usage)));
  return `${k.tier}${k.match}${pad(k.group, 2)}${pad(999999 - usage, 6)}${pad(k.order, 4)}${k.label.toLowerCase()}`;
}

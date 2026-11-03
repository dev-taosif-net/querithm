// Auto-alias for table references accepted in FROM/JOIN (§9). Pure; never throws.
import {
  ANCHOR_KEYWORDS, CONTINUATION_PREV, OBJECT_KIND_WORDS, PROJECTION_END, TABLE_HINTS, builtinFunctions, dataTypes,
  reservedWords, statementKeywords,
} from "./keywords";
import { LANGUAGES } from "../languages";
import { DIALECT_LIST } from "../../engines";

/** Words an alias must never be: every keyword, type and built-in function either dialect knows (uppercase). */
const NOT_AN_ALIAS: Set<string> = (() => {
  const s = new Set<string>();
  const add = (words: Iterable<string>) => { for (const w of words) for (const p of w.split(/[\s(]+/)) if (p) s.add(p.toUpperCase()); };
  for (const d of DIALECT_LIST) {
    add(reservedWords(d));
    add(statementKeywords(d));
    add(builtinFunctions(d));
    add(dataTypes(d).map((t) => t.name));
    for (const w of LANGUAGES[d].words) add(w);
  }
  for (const w of [ANCHOR_KEYWORDS, CONTINUATION_PREV, OBJECT_KIND_WORDS, PROJECTION_END, TABLE_HINTS]) add(w);
  // Short non-reserved words that still read as SQL in a FROM clause.
  add(`GO NO KEY ROW ROWS FIRST LAST NULLS LIMIT MINUS QUALIFY TOP PERCENT TIES ABSOLUTE RELATIVE PRIOR CUBE ROLLUP
    GROUPING SETS VALUE NAME TYPE DATA ZONE AT ASC DESC BY EACH ALSO ONLY LOCAL GLOBAL TEMP SIMPLE FULL PATH RAW AUTO
    XML JSON SYSTEM_TIME PERIOD BEFORE AFTER INSTEAD INOUT OUT MOVE LOCK SHOW RESET`.split(/\s+/));
  return s;
})();

export function isAliasKeyword(alias: string): boolean {
  return NOT_AN_ALIAS.has(alias.toUpperCase());
}

/**
 * Short alias for `name`, unique (case-insensitive) among `taken` and never a keyword:
 * snake_case initials, else the capitals of a mixed-case name, else the first 3 letters; numbered on collision.
 */
export function suggestAlias(name: string, taken: Iterable<string> = []): string {
  const used = new Set<string>();
  for (const t of taken) used.add(t.toLowerCase());
  const base = notKeyword(name.replace(/[^\p{L}\p{N}_]/gu, ""));
  if (!used.has(base)) return base;
  for (let n = 2; n <= 99; n++) {
    const c = `${base}${n}`;
    if (!used.has(c) && !isAliasKeyword(c)) return c;
  }
  return base;
}

function mainReading(clean: string): string {
  const parts = clean.split("_").filter((p) => /[\p{L}\p{N}]/u.test(p));
  if (parts.length >= 2) return parts.slice(0, 4).map((p) => p[0]).join("");
  const letters = clean.replace(/[^\p{L}]/gu, "");
  const caps = letters.replace(/[^\p{Lu}]/gu, "");
  if (caps.length >= 2 && caps.length < letters.length) return caps.slice(0, 4);
  return clean.replace(/_/g, "").slice(0, 3);
}

/** The main reading, or the first fallback that is a valid identifier and not a keyword. */
function notKeyword(clean: string): string {
  const alnum = clean.replace(/_/g, "");
  const first = alnum[0] ?? "";
  const skeleton = first + alnum.slice(1).replace(/[^\p{L}]|[aeiouy]/giu, "").slice(0, 2);
  for (const c of [mainReading(clean), skeleton, alnum.slice(0, 4), first]) {
    const a = c.toLowerCase();
    if (a && /^\p{L}/u.test(a) && !isAliasKeyword(a)) return a;
  }
  return "t";
}

// Token-based SQL text helpers for the UI (execute current statement, USE detection, parameter prompts,
// hover / go-to-definition). Built on the tier-1 tokenizer (§15 rule 6: never regex over raw query text).
import type { Dialect, SnapObject } from "../../ipc/contract";
import type { SchemaIndex } from "./types";
import { getStream, type Token } from "./lexer";
import { CONTINUATION_PREV, statementKeywords } from "./keywords";
import {
  collectVariables, isNameTok, isOp, isPunct, lastStartingBefore, locateStatement, scan, W,
} from "./scanner";
import { ALL_KINDS, findObject, resolveTable } from "./schemaIndex";
import { assertNever, DIALECTS, knownDialect } from "../../engines";

const norm = knownDialect;
/** The prefixes that mark a variable or parameter name under the cursor (hover shows it as one). */
const VARIABLE_PREFIXES: Record<Dialect, string[]> = { tsql: ["@"], pg: ["$", ":"], sqlite: ["?", ":", "@", "$"], mySql: ["@"], duckDb: ["$", "?"], oracle: [":"] };
const eq = (a: string | undefined, b: string | undefined): boolean => (a ?? "").toLowerCase() === (b ?? "").toLowerCase();

/** Offsets of the statement around `offset` (for "execute current statement"); empty range at offset if none. */
export function statementBoundsAt(text: string, offset: number, dialect: Dialect): { start: number; end: number } {
  const src = typeof text === "string" ? text : "";
  const off = Math.max(0, Math.min(Number.isFinite(offset) ? offset : 0, src.length));
  try {
    const T = getStream(src, norm(dialect)).code;
    let k = lastStartingBefore(T, off);
    if (k >= 0 && T[k].end < off) {
      // Cursor in whitespace: prefer the statement starting right at / later on the cursor's line.
      const next = T[k + 1];
      const gapBefore = src.slice(T[k].end, off);
      if (next && (next.start === off || (gapBefore.includes("\n") && !src.slice(off, next.start).includes("\n")))) k = k + 1;
      else if (isPunct(T[k], ";")) k = k - 1;
    } else if (k >= 0 && isPunct(T[k], ";") && off >= T[k].end) {
      k = k - 1;
    }
    if (k >= 0 && T[k].kind === "go") {
      const next = T[k + 1];
      if (!next || next.kind === "go") return { start: off, end: off };
    }
    // When the cursor token itself starts a statement, locate from it (k points at it, so it is "before" the cursor).
    const loc = locateStatement(T, k, -1, norm(dialect));
    if (loc.start < 0) return { start: off, end: off };
    return { start: T[loc.start].start, end: T[loc.end].end };
  } catch {
    return { start: off, end: off };
  }
}

/** The database named by the last `USE db` in the text (dialects with a USE statement: T-SQL). */
export function detectUse(text: string, dialect: Dialect): string | undefined {
  const d = norm(dialect);
  if (!DIALECTS[d].useStatement || typeof text !== "string") return undefined;
  try {
    const T = getStream(text, d).code;
    let depth = 0;
    let found: string | undefined;
    for (let i = 0; i < T.length; i++) {
      const t = T[i];
      if (isPunct(t, "(")) depth++;
      else if (isPunct(t, ")")) depth = Math.max(0, depth - 1);
      else if (t.kind === "go" || isPunct(t, ";")) depth = 0;
      else if (depth === 0 && W(t) === "USE") {
        const n = T[i + 1];
        if (n && (isNameTok(n) || n.kind === "keyword") && W(n) !== "PLAN" && W(n) !== "HINT") found = n.value;
      }
    }
    return found;
  } catch {
    return undefined;
  }
}

/**
 * Parameters the user must supply before running the text.
 * T-SQL: `@names` used but neither DECLAREd in their batch nor parameters of a CREATE/ALTER PROCEDURE/FUNCTION
 * header in that batch (excludes @@globals, DECLAREd table variables and named EXEC arguments `@p = ...`).
 * PostgreSQL: `$1..$n` (numeric order) then `:name` placeholders (first-appearance order).
 * SQLite: `?`, `?NNN`, `:name`, `@name`, `$name`, in SQLite's own index order (see sqliteParams).
 * MySQL: none. `@x` is a user variable there, which the script sets itself (`SET @x = …`) and the session keeps; a
 * prompt would bind nothing.
 */
export function undeclaredParameters(text: string, dialect: Dialect): string[] {
  if (typeof text !== "string") return [];
  try {
    const d = norm(dialect);
    const T = getStream(text, d).code;
    switch (d) {
      case "pg": return pgParams(T);
      case "tsql": return tsqlUndeclared(text, T);
      case "sqlite": return sqliteParams(T);
      case "mySql": return [];
      case "duckDb": return duckDbParams(T);
      // Phase D4 placeholder: :binds (minus :NEW / :OLD in trigger bodies) are found by the sidecar's scanner (S02, S04).
      case "oracle": return [];
      default: return assertNever(d);
    }
  } catch {
    return [];
  }
}

/**
 * MySQL user variables (`@name`) the text reads but never assigns, each once in first-appearance order (names compare
 * case-insensitively, as MySQL's do). MySQL binds no parameters by name, so a script that means `@name` as a parameter
 * reads NULL unless it sets it first; the run says so instead of prompting. Assignments are read from tokens:
 * `SET @a = …` / `SET … , @b = …`, `@a := …` anywhere, and `… INTO @a, @b`. `@@system` variables are not user ones.
 * Other dialects: none (their parameters are prompted for, see undeclaredParameters).
 */
export function unsetUserVariables(text: string, dialect: Dialect): string[] {
  if (typeof text !== "string" || norm(dialect) !== "mySql") return [];
  try {
    const T = getStream(text, "mySql").code;
    const assigned = new Set<string>();
    const read: string[] = [];
    let inSet = false;
    let inInto = false;
    for (let i = 0; i < T.length; i++) {
      const t = T[i];
      if (isPunct(t, ";")) {
        inSet = inInto = false;
        continue;
      }
      if (t.kind !== "variable") {
        const w = W(t);
        if (w === "SET") inSet = true;
        else if (w === "WHERE") inSet = false;
        inInto = w === "INTO" || (inInto && isPunct(t, ","));
        continue;
      }
      if (t.value.startsWith("@@") || t.value.length < 2) continue;
      const key = t.value.toLowerCase();
      const next = T[i + 1];
      if (inInto || isOp(next, ":=") || (inSet && isOp(next, "="))) assigned.add(key);
      else read.push(t.value);
    }
    const seen = new Set<string>();
    return read.filter((v) => {
      const key = v.toLowerCase();
      if (assigned.has(key) || seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  } catch {
    return [];
  }
}

/**
 * SQLite numbers parameters as it compiles: `?NNN` takes index NNN; a bare `?` takes one more than the largest index
 * so far (named here `?<index>`); a name (`:a`, `@a`, `$a`, distinct by prefix) takes the next index the first time.
 * Returned in index order, each once.
 */
function sqliteParams(T: Token[]): string[] {
  const byName = new Map<string, number>();
  let max = 0;
  for (const t of T) {
    if (t.kind !== "param") continue;
    const v = t.value;
    if (v === "?") {
      max++;
      byName.set(`?${max}`, max);
    } else if (v.startsWith("?")) {
      const index = Number(v.slice(1));
      if (!Number.isSafeInteger(index) || index < 1) continue;
      if (!byName.has(`?${index}`)) byName.set(`?${index}`, index);
      max = Math.max(max, index);
    } else if (!byName.has(v)) {
      byName.set(v, ++max);
    }
  }
  return [...byName].sort((a, b) => a[1] - b[1]).map(([name]) => name);
}

/**
 * DuckDB binds by position (`?` in order as ?1, ?2…; `$1`, `$2`) or by name (`$name`, compared case-insensitively); the
 * sidecar binds names starting with ? or a digit by position, in this order.
 */
function duckDbParams(T: Token[]): string[] {
  let question = 0;
  const nums = new Set<number>();
  const named: string[] = [];
  const seen = new Set<string>();
  for (const t of T) {
    if (t.kind !== "param") continue;
    const v = t.value;
    if (v === "?") question++;
    else if (/^\$\d+$/.test(v)) nums.add(Number(v.slice(1)));
    else if (v.startsWith("$") && !seen.has(v.toLowerCase())) { seen.add(v.toLowerCase()); named.push(v); }
  }
  const positional = [...Array(question).keys()].map((i) => `?${i + 1}`);
  return positional.concat([...nums].sort((a, b) => a - b).map((n) => `$${n}`), named);
}

function pgParams(T: Token[]): string[] {
  const nums = new Set<number>();
  const named: string[] = [];
  const seen = new Set<string>();
  for (const t of T) {
    if (t.kind !== "param") continue;
    if (t.value.startsWith("$")) nums.add(Number(t.value.slice(1)));
    else if (!seen.has(t.value.toLowerCase())) { seen.add(t.value.toLowerCase()); named.push(t.value); }
  }
  return [...nums].sort((a, b) => a - b).map((n) => `$${n}`).concat(named);
}

function tsqlUndeclared(text: string, T: Token[]): string[] {
  const out: string[] = [];
  const reported = new Set<string>();
  const stmt = statementKeywords("tsql");
  let bs = 0;
  while (bs <= T.length) {
    let be = bs;
    while (be < T.length && T[be].kind !== "go") be++;
    // declared in this batch: header params + every DECLARE (variables are batch-scoped)
    const declared = new Set<string>();
    if (be > bs) for (const v of collectVariables(text, T, be - 1, "tsql")) declared.add(v.name.toLowerCase());
    // also DECLAREs that appear after a use in the batch are already included (collectVariables scans the batch)
    let execArgs = false;
    let chainEnd = -1;
    let depth = 0;
    for (let i = bs; i < be; i++) {
      const t = T[i];
      if (isPunct(t, ";")) { execArgs = false; depth = 0; continue; }
      if (isPunct(t, "(")) depth++;
      else if (isPunct(t, ")")) depth = Math.max(0, depth - 1);
      const u = W(t);
      if (u === "EXEC" || u === "EXECUTE") {
        if (!isPunct(T[i + 1], "(") && !CONTINUATION_PREV.has(W(T[i - 1])) && W(T[i + 1]) !== "AS") {
          let j = i + 1;
          if (T[j]?.kind === "variable" && isOp(T[j + 1], "=")) j += 2;
          while (j < be && (isNameTok(T[j]) || isPunct(T[j], "."))) j++;
          chainEnd = j - 1;
          execArgs = true;
        }
      } else if (execArgs && depth === 0 && u && stmt.has(u) && i > chainEnd) {
        execArgs = false;
      }
      if (t.kind !== "variable") continue;
      const v = t.value;
      if (v.length < 2 || v.startsWith("@@")) continue;
      if (execArgs && i > chainEnd && isOp(T[i + 1], "=") && (i === chainEnd + 1 || isPunct(T[i - 1], ","))) continue; // named argument
      const key = v.toLowerCase();
      if (declared.has(key) || reported.has(key)) continue;
      reported.add(key);
      out.push(v);
    }
    bs = be + 1;
    if (be >= T.length) break;
  }
  return out;
}

export interface IdentifierAt {
  parts: string[];
  start: number;
  end: number;
  /** Index into `parts` of the part under the cursor. */
  partIndex: number;
}

function nameLike(t: Token | undefined): boolean {
  return t !== undefined && (t.kind === "ident" || t.kind === "qident" || t.kind === "variable" || t.kind === "keyword" || t.kind === "param");
}

/** Multi-part dotted identifier under the cursor (brackets/quotes stripped), or null. */
export function identifierAt(text: string, offset: number, dialect: Dialect): IdentifierAt | null {
  if (typeof text !== "string") return null;
  try {
    const T = getStream(text, norm(dialect)).code;
    const idx = lastStartingBefore(T, offset);
    let i = -1;
    const a = idx >= 0 ? T[idx] : undefined;
    const b = T[idx + 1];
    if (a && a.end >= offset && nameLike(a)) i = idx;
    else if (b && b.start === offset && nameLike(b)) i = idx + 1;
    if (i < 0) return null;
    const glued = (x: number): boolean => T[x] !== undefined && T[x + 1] !== undefined && T[x].end === T[x + 1].start;
    const tok = T[i];
    const isChained = (isPunct(T[i - 1], ".") && glued(i - 1)) || (isPunct(T[i + 1], ".") && glued(i));
    if (tok.kind === "keyword" && !isChained) return null;
    // expand left
    let j = i;
    const left: string[] = [];
    while (j - 1 >= 0 && isPunct(T[j - 1], ".") && glued(j - 1)) {
      if (j - 2 >= 0 && nameLike(T[j - 2]) && glued(j - 2) && !isPunct(T[j - 2], ".")) { left.unshift(T[j - 2].value); j -= 2; }
      else if (j - 2 >= 0 && isPunct(T[j - 2], ".") && glued(j - 2)) { left.unshift(""); j -= 1; }
      else break;
    }
    // expand right
    let e = i;
    const right: string[] = [];
    while (isPunct(T[e + 1], ".") && glued(e)) {
      if (nameLike(T[e + 2]) && glued(e + 1)) { right.push(T[e + 2].value); e += 2; }
      else if (isPunct(T[e + 2], ".") && glued(e + 1)) { right.push(""); e += 1; }
      else break;
    }
    const parts = [...left, tok.value, ...right];
    return { parts, start: T[j].start, end: T[e].end, partIndex: left.length };
  } catch {
    return null;
  }
}

export interface ResolvedAt {
  object?: SnapObject;
  column?: { table: SnapObject; name: string };
  variable?: string;
}

/** Resolve the identifier under the cursor to a snapshot object / column / variable (hover, go-to-definition). */
export function resolveObjectAt(text: string, offset: number, dialect: Dialect, schema: SchemaIndex | null): ResolvedAt | null {
  try {
    const d = norm(dialect);
    const id = identifierAt(text, offset, d);
    if (!id) return null;
    const parts = id.parts.slice(0, id.partIndex + 1);
    const cur = parts[parts.length - 1];
    if (VARIABLE_PREFIXES[d].some((p) => cur.startsWith(p))) return { variable: cur };
    if (!schema) return null;
    const ctx = scan(text, id.start, d);
    const tables = ctx.tablesInScope;
    const findInScope = (q: string) =>
      tables.find((t) => eq(t.alias, q)) ?? tables.find((t) => !t.alias && eq(t.name, q)) ?? tables.find((t) => eq(t.name, q));
    const colOf = (o: SnapObject | undefined, name: string): ResolvedAt | null => {
      const c = o?.columns?.find((x) => eq(x.name, name));
      return o && c ? { column: { table: o, name: c.name } } : null;
    };
    const n = parts.length;
    if (n === 1) {
      const p = parts[0];
      const t = findInScope(p);
      if (t) {
        const o = resolveTable(schema, t);
        if (o) return { object: o };
        if (t.isCte || t.isDerived) return null;
      }
      for (const tt of tables) {
        const r = colOf(resolveTable(schema, tt), p);
        if (r) return r;
      }
      const o = findObject(schema, p, undefined, ALL_KINDS);
      return o ? { object: o } : null;
    }
    if (n === 2) {
      const [a, b] = parts;
      const t = findInScope(a);
      if (t) {
        const r = colOf(resolveTable(schema, t), b);
        if (r) return r;
        if (t.isCte || t.isDerived) return null;
      }
      const o = findObject(schema, b, a, ALL_KINDS);
      if (o) return { object: o };
      return colOf(findObject(schema, a), b);
    }
    if (n === 3) {
      const [a, b, c] = parts;
      const r = colOf(findObject(schema, b, a), c);
      if (r) return r;
      if (eq(a, schema.database)) {
        const o = findObject(schema, c, b || schema.defaultSchema, ALL_KINDS);
        return o ? { object: o } : null;
      }
      return null;
    }
    const [db, s, t, c] = parts.slice(-4);
    if (!eq(db, schema.database)) return null;
    return colOf(findObject(schema, t, s || schema.defaultSchema), c);
  } catch {
    return null;
  }
}

// Tier-1 cursor-context scanner (§9). Synchronous and tolerant: it never throws on broken or partial SQL.
// Work per call: one cached/incremental token stream, a backward boundary search from the cursor, a forward
// statement split over the enclosing statement only, and one analysis pass over that statement.
import type { Dialect } from "../../ipc/contract";
import type { Clause, CursorContext, TableInScope } from "./types";
import { getStream, tokenize, type Token } from "./lexer";
import {
  ANCHOR_KEYWORDS, CONTINUATION_PREV, OBJECT_KIND_WORDS, PROJECTION_END, TABLE_HINTS, statementKeywords,
} from "./keywords";
import { knownDialect } from "../../engines";

export { tokenize };
export type { Token, TokenKind } from "./lexer";

/**
 * Which statement grammar a dialect follows here: PostgreSQL's (PL/pgSQL blocks and DECLARE sections, `$tag$` routine
 * bodies scanned as their own script, named arguments with `=>`) or T-SQL's (procedure headers, `@variables`, MERGE
 * ending in `;`, table hints). SQLite has no procedural code and MySQL scripts end statements with `;`: both take the
 * T-SQL rules, whose extra cases (hints, `@x =` arguments) never match their text.
 */
const PG_RULES: Record<Dialect, boolean> = { tsql: false, pg: true, sqlite: false, mySql: false, duckDb: false };

// ---------------------------------------------------------------------------------------------
// Token helpers
// ---------------------------------------------------------------------------------------------

/** Uppercased word for keyword/ident tokens, '' otherwise. */
export function W(t: Token | undefined): string {
  return t !== undefined && (t.kind === "keyword" || t.kind === "ident") ? t.upper : "";
}
export function isPunct(t: Token | undefined, ch: string): boolean {
  return t !== undefined && t.kind === "punct" && t.value === ch;
}
export function isOp(t: Token | undefined, op: string): boolean {
  return t !== undefined && t.kind === "operator" && t.value === op;
}
/** Identifier usable as a name (unreserved word or quoted/bracketed identifier). */
export function isNameTok(t: Token | undefined): boolean {
  return t !== undefined && (t.kind === "ident" || t.kind === "qident");
}

/** Index of the last token with start < pos (-1 if none). */
export function lastStartingBefore(arr: Token[], pos: number): number {
  let lo = 0, hi = arr.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (arr[mid].start < pos) lo = mid + 1; else hi = mid;
  }
  return lo - 1;
}

const TABLE_CLAUSES = new Set<Clause>(["from", "join", "update", "insertInto", "delete"]);
const PROC_WORDS = new Set(["PROC", "PROCEDURE", "FUNCTION", "TRIGGER"]);
const BEGIN_TX = new Set(["TRAN", "TRANSACTION", "DISTRIBUTED", "DIALOG", "CONVERSATION", "WORK", "ISOLATION", "ATOMIC"]);
const END_SUFFIX = new Set(["TRY", "CATCH", "IF", "LOOP", "CASE", "CONVERSATION"]);
const DROP_CONT = new Set(["COLUMN", "CONSTRAINT", "DEFAULT", "NOT", "IDENTITY", "EXPRESSION", "PERIOD"]);
const FK_ACTION = new Set(["CASCADE", "SET", "NO", "RESTRICT", "ROWS"]);
const FUNC_KEYWORDS = new Set([
  "EXTRACT", "SUBSTRING", "TRIM", "POSITION", "OVERLAY", "CAST", "TRY_CAST", "CONVERT", "TRY_CONVERT", "COALESCE",
  "NULLIF", "LEFT", "RIGHT",
]);

// ---------------------------------------------------------------------------------------------
// Statement splitter (§9 rules: ';', GO, a statement-starting keyword at paren depth 0, with context rules)
// ---------------------------------------------------------------------------------------------

export const CONT = 0, START = 1, SEMI = 2, GO = 3;

export class Splitter {
  private readonly tsql: boolean;
  private readonly stmt: Set<string>;
  started = false;
  forceNext = true;
  kind = "";
  private depth = 0;
  private caseDepth = 0;
  private insertPending = false;
  private updateSetPending = false;
  private withPending = false;
  private explainPending = false;
  /** A DuckDB FROM-first query (`FROM t SELECT a`): its SELECT continues it. */
  private fromFirst = false;
  private header = false;
  private ddl = false;
  private merge = false;
  private forceAfter = -1;

  constructor(private readonly T: Token[], dialect: Dialect) {
    this.tsql = !PG_RULES[dialect];
    this.stmt = statementKeywords(dialect);
  }

  private reset(): void {
    this.started = false;
    this.kind = "";
    this.depth = 0;
    this.caseDepth = 0;
    this.insertPending = this.updateSetPending = this.withPending = this.explainPending = this.fromFirst = false;
    this.header = this.ddl = this.merge = false;
    this.forceAfter = -1;
  }

  private begin(i: number, neutral: boolean): void {
    this.reset();
    this.started = true;
    this.forceNext = false;
    if (neutral) return;
    const T = this.T;
    const u = W(T[i]);
    this.kind = u;
    switch (u) {
      case "INSERT": this.insertPending = true; break;
      case "UPDATE": this.updateSetPending = true; break;
      case "WITH": this.withPending = true; break;
      case "EXPLAIN": this.explainPending = true; break;
      case "FROM": this.fromFirst = true; break;
      case "MERGE": this.merge = this.tsql; break;
      case "CREATE":
      case "ALTER": {
        let j = i + 1;
        if (W(T[j]) === "OR") j += 2;
        const obj = W(T[j]);
        if (this.tsql && PROC_WORDS.has(obj)) this.header = true;
        else if (u === "ALTER" || !this.tsql) this.ddl = true;
        break;
      }
      case "BEGIN": {
        const nx = W(T[i + 1]);
        if (nx === "TRY" || nx === "CATCH") this.forceAfter = i + 1;
        else if (!BEGIN_TX.has(nx) && !isPunct(T[i + 1], ";")) this.forceNext = true;
        break;
      }
      case "END": {
        const nx = W(T[i + 1]);
        if (END_SUFFIX.has(nx)) this.forceAfter = i + 1;
        else this.forceNext = true;
        break;
      }
      case "ELSE": this.forceNext = true; break;
      case "LOOP": if (!this.tsql) this.forceNext = true; break;
    }
  }

  /** Classify token i: CONT, START (begins a statement), SEMI (';' terminator) or GO (batch separator). */
  step(i: number, neutral = false): number {
    const t = this.T[i];
    if (t.kind === "go") { this.reset(); this.forceNext = true; return GO; }
    if (t.kind === "punct" && t.value === ";") { this.reset(); this.forceNext = true; return SEMI; }
    let r = CONT;
    if (this.forceNext || !this.started) { this.begin(i, neutral); r = START; }
    else if (!neutral) r = this.mid(i);
    if (this.forceAfter === i) { this.forceAfter = -1; this.forceNext = true; }
    return r;
  }

  private mid(i: number): number {
    const T = this.T;
    const t = T[i];
    if (t.kind === "punct") {
      if (t.value === "(") this.depth++;
      else if (t.value === ")") this.depth = Math.max(0, this.depth - 1);
      return CONT;
    }
    const u = W(t);
    if (!u) return CONT;
    if (u === "CASE") { this.caseDepth++; return CONT; }
    if (u === "END" && this.caseDepth > 0) { this.caseDepth--; return CONT; }
    // CASE … ELSE … END: that ELSE is the expression's, never T-SQL / PL/pgSQL / MySQL IF … ELSE starting a statement.
    if (u === "ELSE" && this.caseDepth > 0) return CONT;
    if (this.depth > 0 || this.merge) return CONT;
    if (this.header && u === "AS") {
      const prev = T[i - 1];
      const pu = W(prev);
      if (prev && prev.kind !== "variable" && pu !== "EXECUTE" && pu !== "EXEC") { this.header = false; this.forceNext = true; }
      return CONT;
    }
    if (!this.tsql && (u === "THEN" || u === "LOOP") && this.caseDepth === 0) { this.forceNext = true; return CONT; }
    if (this.isBoundary(i, u)) { this.begin(i, false); return START; }
    switch (u) {
      case "SELECT": case "EXEC": case "EXECUTE": case "VALUES":
        this.insertPending = this.fromFirst = false;
        break;
      case "WITH":
        if (isCtePattern(T, i)) this.withPending = true;
        break;
    }
    if (u === "SELECT" || u === "INSERT" || u === "UPDATE" || u === "DELETE" || u === "MERGE") {
      this.explainPending = false;
      if (this.withPending) {
        this.withPending = false;
        if (u === "INSERT") this.insertPending = true;
        else if (u === "UPDATE") this.updateSetPending = true;
        else if (u === "MERGE") this.merge = this.tsql;
      }
    }
    if (u === "UPDATE" && W(T[i + 1]) === "SET") this.updateSetPending = true;
    else if (u === "SET" && this.updateSetPending) this.updateSetPending = false;
    return CONT;
  }

  private isBoundary(i: number, u: string): boolean {
    if (!this.stmt.has(u)) return false;
    const T = this.T;
    const prev = T[i - 1];
    const pu = W(prev);
    if (isPunct(prev, ",") || CONTINUATION_PREV.has(pu)) return false;
    const nu = W(T[i + 1]);
    switch (u) {
      case "SELECT": return !this.insertPending && !this.withPending && !this.explainPending && !this.fromFirst;
      // DuckDB (the only dialect listing them): FROM begins a statement only where one begins (after ';' or at the top:
      // a FROM-first query); inside one it is the FROM clause (SELECT … FROM, DELETE FROM), and PIVOT / UNPIVOT there
      // follow a FROM. DuckDB requires ';' between statements, so a mid-statement keyword never starts another one.
      case "FROM": case "PIVOT": case "UNPIVOT": return false;
      case "EXEC": case "EXECUTE": return !this.insertPending;
      case "WITH": return !this.header && this.kind !== "CREATE" && this.kind !== "ALTER" && isCtePattern(T, i);
      case "INSERT": case "UPDATE": case "DELETE": case "MERGE":
        if (this.withPending || this.explainPending) return false;
        if (pu === "ON" && FK_ACTION.has(nu)) return false;
        if (u === "UPDATE" && pu === "KEY") return false; // MySQL INSERT … ON DUPLICATE KEY UPDATE
        return pu !== "COMMIT";
      case "SET": return !this.updateSetPending && !this.ddl && pu !== "DELETE" && pu !== "UPDATE";
      case "ALTER": return !(this.ddl && nu === "COLUMN");
      case "DROP": return !(this.ddl && DROP_CONT.has(nu)) && pu !== "COMMIT";
      case "IF": return !OBJECT_KIND_WORDS.has(pu);
      case "FETCH": {
        const n2 = W(T[i + 2]);
        return !((nu === "FIRST" || nu === "NEXT") && n2 !== "FROM" && n2 !== "IN");
      }
      case "COMMIT": case "ROLLBACK": return pu !== "ON";
      default: return true;
    }
  }
}

/** `WITH name [(cols)] AS (` or `WITH RECURSIVE` — a CTE prefix rather than a table hint / option. */
function isCtePattern(T: Token[], i: number): boolean {
  const next = T[i + 1];
  if (W(next) === "RECURSIVE") return true;
  if (!isNameTok(next)) return false;
  const n2 = T[i + 2];
  return W(n2) === "AS" || isPunct(n2, "(");
}

function isAnchorAt(T: Token[], j: number): boolean {
  const prev = T[j - 1];
  const pu = W(prev);
  if (isPunct(prev, ",") || CONTINUATION_PREV.has(pu)) return false;
  const u = T[j].upper;
  if (u === "IF") return !OBJECT_KIND_WORDS.has(pu);
  return true;
}

export interface StmtLoc {
  /** First token index of the statement, -1 when the statement is empty. */
  start: number;
  /** Last token index (inclusive, terminator excluded). */
  end: number;
  /** The statement begins after the cursor (cursor sits at the start of a new statement). */
  afterCursor: boolean;
  /** Splitter kind of the statement's first keyword. */
  kind: string;
}

/**
 * Locate the statement around a cursor whose last preceding token is `k` (`skip` = the token being typed,
 * treated as a neutral word). Searches backward for a safe restart point (';', GO, or an anchor keyword at the
 * outermost relative paren depth), then splits forward to the end of the enclosing statement.
 */
export function locateStatement(T: Token[], k: number, skip: number, dialect: Dialect): StmtLoc {
  let s0 = 0;
  let d = 0, m = 0;
  for (let j = k; j >= 0; j--) {
    const t = T[j];
    if (t.kind === "go" || (t.kind === "punct" && t.value === ";")) { s0 = j + 1; break; }
    if (t.kind === "punct") {
      if (t.value === ")") d++;
      else if (t.value === "(") { d--; if (d < m) m = d; }
      continue;
    }
    if (d === m && (t.kind === "keyword" || t.kind === "ident") && ANCHOR_KEYWORDS.has(t.upper) && isAnchorAt(T, j)) {
      s0 = j;
      break;
    }
  }
  const sp = new Splitter(T, dialect);
  let start = -1, end = -1, kind = "";
  let afterCursor = false;
  const n = T.length;
  for (let i = s0; i < n; i++) {
    const r = sp.step(i, i === skip);
    if (i <= k) {
      if (r === START) { start = i; kind = sp.kind; }
      else if (r === SEMI || r === GO) start = -1;
      if (i === k) {
        if (sp.forceNext) start = -1;
        end = start >= 0 ? k : -1;
      }
      continue;
    }
    if (start < 0) {
      if (r === START) { start = i; end = i; kind = sp.kind; afterCursor = true; continue; }
      break;
    }
    if (r !== CONT) break;
    end = i;
  }
  if (start >= 0 && end < start) end = start;
  return { start, end, afterCursor, kind };
}

// ---------------------------------------------------------------------------------------------
// Declarations: T-SQL DECLARE @x type / proc headers, PL/pgSQL DECLARE sections / function headers
// ---------------------------------------------------------------------------------------------

export interface VarDecl { name: string; type?: string; kind: "variable" | "parameter" }

function skipParens(T: Token[], j: number, limit: number): number {
  // T[j] is '(' → index after the matching ')'
  let depth = 0;
  for (let x = j; x <= limit; x++) {
    if (isPunct(T[x], "(")) depth++;
    else if (isPunct(T[x], ")")) { depth--; if (depth === 0) return x + 1; }
  }
  return limit + 1;
}

const TYPE_CONT = new Set(["PRECISION", "VARYING", "WITH", "WITHOUT", "TIME", "ZONE"]);

/** Read a data type starting at j: `[schema.]name [(args)]`; returns text and the index after it. */
function readType(text: string, T: Token[], j: number, limit: number, pg: boolean): { type?: string; next: number } {
  const first = T[j];
  if (!first || j > limit || !(first.kind === "ident" || first.kind === "keyword" || first.kind === "qident")) return { next: j };
  let x = j + 1;
  while (x <= limit && isPunct(T[x], ".") && T[x + 1] && (isNameTok(T[x + 1]) || T[x + 1].kind === "keyword")) x += 2;
  if (pg) while (x <= limit && TYPE_CONT.has(W(T[x]))) x++;
  if (W(first) !== "TABLE" && x <= limit && isPunct(T[x], "(")) x = skipParens(T, x, limit);
  if (pg) {
    while (x <= limit && isOp(T[x], "[")) { x++; if (isOp(T[x], "]")) x++; }
    if (isOp(T[x], "%") && (W(T[x + 1]) === "ROWTYPE" || W(T[x + 1]) === "TYPE")) x += 2;
  }
  const last = T[Math.min(x, limit + 1) - 1];
  const type = text.slice(first.start, last.end).replace(/\s+/g, " ");
  return { type, next: x };
}

/** T-SQL `DECLARE @a [AS] type [= expr], @b ...` starting at the DECLARE token. */
function parseTsqlDeclare(text: string, T: Token[], i: number, limit: number, stmt: Set<string>, out: VarDecl[]): number {
  let j = i + 1;
  while (j <= limit) {
    const t = T[j];
    if (!t || t.kind !== "variable" || t.value.length < 2) return j;
    const name = t.value;
    j++;
    if (W(T[j]) === "AS") j++;
    const { type, next } = readType(text, T, j, limit, false);
    out.push({ name, type, kind: "variable" });
    j = next;
    let depth = 0;
    for (; j <= limit; j++) {
      const x = T[j];
      if (x.kind === "go" || isPunct(x, ";")) return j;
      if (isPunct(x, "(")) depth++;
      else if (isPunct(x, ")")) depth = Math.max(0, depth - 1);
      else if (depth === 0 && isPunct(x, ",")) break;
      else if (depth === 0 && stmt.has(W(x))) return j;
    }
    if (!isPunct(T[j], ",")) return j;
    j++;
  }
  return j;
}

/**
 * Memo of parsed DECLAREs keyed by the DECLARE token object. Token objects survive incremental re-lexing only
 * when their text is unchanged, so an entry is valid while every consumed token is still the same object.
 */
const declareMemo = new WeakMap<Token, { tokens: Token[]; vars: VarDecl[] }>();

function parseTsqlDeclareMemo(text: string, T: Token[], i: number, limit: number, stmt: Set<string>, out: VarDecl[]): void {
  const m = declareMemo.get(T[i]);
  if (m && i + m.tokens.length - 1 < limit) {
    let ok = true;
    for (let x = 0; x < m.tokens.length; x++) if (T[i + x] !== m.tokens[x]) { ok = false; break; }
    if (ok) { for (const v of m.vars) out.push(v); return; }
  }
  const vars: VarDecl[] = [];
  const end = parseTsqlDeclare(text, T, i, limit, stmt, vars);
  for (const v of vars) out.push(v);
  if (end < limit) declareMemo.set(T[i], { tokens: T.slice(i, end + 1), vars });
}

/** T-SQL `CREATE|ALTER PROC|FUNCTION name [(] @p [AS] type [VARYING] [= default] [OUT|OUTPUT] [READONLY], ...`. */
function parseTsqlHeader(text: string, T: Token[], i: number, limit: number, out: VarDecl[]): void {
  let j = i + 1;
  if (W(T[j]) === "OR") j += 2;
  if (!PROC_WORDS.has(W(T[j]))) return;
  j++;
  while (j <= limit && (isNameTok(T[j]) || isPunct(T[j], "."))) j++;
  if (W(T[j]) === "AS" || W(T[j]) === "WITH") return;
  if (isPunct(T[j], "(")) j++;
  while (j <= limit) {
    const t = T[j];
    if (!t || t.kind !== "variable") return;
    j++;
    if (W(T[j]) === "AS") j++;
    const { type, next } = readType(text, T, j, limit, false);
    j = next;
    let typeText = type;
    let depth = 0;
    for (; j <= limit; j++) {
      const x = T[j];
      const u = W(x);
      if (isPunct(x, "(")) depth++;
      else if (isPunct(x, ")")) { if (depth === 0) break; depth--; }
      else if (depth === 0 && (isPunct(x, ",") || u === "AS" || u === "RETURNS" || u === "WITH" || u === "FOR")) break;
      else if (depth === 0 && (u === "OUTPUT" || u === "OUT")) typeText = (typeText ?? "") + " OUTPUT";
    }
    out.push({ name: t.value, type: typeText, kind: "parameter" });
    if (!isPunct(T[j], ",")) return;
    j++;
  }
}

/** PL/pgSQL `DECLARE name [CONSTANT] type [...]; ...` up to BEGIN. */
function parsePgDeclare(text: string, T: Token[], i: number, limit: number, out: VarDecl[]): void {
  let j = i + 1;
  while (j <= limit) {
    const t = T[j];
    if (!t || W(t) === "BEGIN" || !(isNameTok(t) || t.kind === "keyword")) return;
    j++;
    if (W(T[j]) === "CONSTANT") j++;
    let type: string | undefined;
    if (W(T[j]) === "ALIAS") type = "alias";
    else type = readType(text, T, j, limit, true).type;
    out.push({ name: t.value, type, kind: "variable" });
    while (j <= limit && !isPunct(T[j], ";")) j++;
    j++;
  }
}

/** PostgreSQL `CREATE [OR REPLACE] FUNCTION|PROCEDURE name ( [mode] [name] type [DEFAULT ...], ... )`. */
export function parsePgHeader(text: string, T: Token[], i: number, limit: number, out: VarDecl[]): void {
  let j = i + 1;
  if (W(T[j]) === "OR") j += 2;
  const w = W(T[j]);
  if (w !== "FUNCTION" && w !== "PROCEDURE") return;
  j++;
  while (j <= limit && (isNameTok(T[j]) || isPunct(T[j], "."))) j++;
  if (!isPunct(T[j], "(")) return;
  const close = skipParens(T, j, limit) - 1;
  let itemStart = j + 1;
  let depth = 0;
  for (let x = j + 1; x <= close; x++) {
    const t = T[x];
    if (isPunct(t, "(")) depth++;
    else if (isPunct(t, ")") && x < close) depth--;
    if (x === close || (depth === 0 && isPunct(t, ","))) {
      let a = itemStart;
      const e = x - 1;
      const mode = W(T[a]);
      if (mode === "IN" || mode === "OUT" || mode === "INOUT" || mode === "VARIADIC") a++;
      let stop = e;
      for (let y = a; y <= e; y++) if (W(T[y]) === "DEFAULT" || isOp(T[y], "=")) { stop = y - 1; break; }
      if (stop > a && isNameTok(T[a])) {
        const tt = readType(text, T, a + 1, stop, true).type;
        out.push({ name: T[a].value, type: mode === "OUT" || mode === "INOUT" ? `${tt ?? ""} ${mode}`.trim() : tt, kind: "parameter" });
      }
      itemStart = x + 1;
    }
  }
}

/** Variables visible at token k: proc/function header params + DECLAREs earlier in the batch. */
export function collectVariables(text: string, T: Token[], k: number, dialect: Dialect, marks?: Token[]): VarDecl[] {
  const declares: number[] = [];
  let header = -1;
  if (marks && k >= 0) {
    // Indexed path: walk only the DECLARE/CREATE/ALTER/GO marks before the cursor.
    const kStart = T[k].start;
    for (let m = lastStartingBefore(marks, kStart + 1); m >= 0; m--) {
      const t = marks[m];
      if (t.kind === "go") break;
      const j = lastStartingBefore(T, t.start + 1);
      if (j < 0 || T[j] !== t) continue;
      const u = t.upper;
      if (u === "DECLARE") declares.push(j);
      else if (header < 0) {
        let x = j + 1;
        if (W(T[x]) === "OR") x += 2;
        if (PROC_WORDS.has(W(T[x]))) header = j;
      }
    }
  } else for (let j = k; j >= 0; j--) {
    const t = T[j];
    if (t.kind === "go") break;
    if (t.kind !== "keyword" && t.kind !== "ident") continue;
    const u = t.upper;
    if (u === "DECLARE") declares.push(j);
    else if ((u === "CREATE" || u === "ALTER") && header < 0) {
      let x = j + 1;
      if (W(T[x]) === "OR") x += 2;
      if (PROC_WORDS.has(W(T[x]))) header = j;
    }
  }
  const out: VarDecl[] = [];
  const pg = PG_RULES[dialect];
  if (header >= 0 && !pg) parseTsqlHeader(text, T, header, k, out);
  const stmt = statementKeywords(dialect);
  for (let x = declares.length - 1; x >= 0; x--) {
    if (pg) parsePgDeclare(text, T, declares[x], k, out);
    else parseTsqlDeclareMemo(text, T, declares[x], k, stmt, out);
  }
  return dedupeVars(out);
}

function dedupeVars(list: VarDecl[]): VarDecl[] {
  const seen = new Set<string>();
  const out: VarDecl[] = [];
  for (const v of list) {
    const key = v.name.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(v);
  }
  return out;
}

// ---------------------------------------------------------------------------------------------
// Statement analysis: CTEs, table references, INSERT/MERGE targets, clause at the cursor
// ---------------------------------------------------------------------------------------------

interface Ref extends TableInScope {
  offset: number;
  role?: "update" | "delete" | "insert" | "merge" | "into";
  endIdx: number;
}

interface InsertInfo {
  kw: number;
  target?: { name: string; schema?: string };
  targetEnd: number;
  colsOpen: number;
  colsClose: number;
  cols: string[];
  source: number;
}

type FrameFn = "" | "cast" | "convert" | "call" | "values" | "colDefs" | "insertCols" | "execCall" | "sub";
/** `cases`: CASE expressions open in this frame; their WHEN / THEN / ELSE / END never change the clause. */
interface WFrame { clause: Clause; fn: FrameFn; open: number; execKw: number; cases?: number }

class Analyzer {
  readonly pm: Int32Array;
  readonly ctes: { name: string; columns?: string[] }[] = [];
  readonly refs: Ref[] = [];
  insert?: InsertInfo;
  mergeTarget?: Ref;
  private skipUntil = -1;
  readonly pg: boolean;

  constructor(
    readonly text: string,
    readonly T: Token[],
    readonly a: number,
    readonly b: number,
    readonly skip: number,
    readonly dialect: Dialect,
  ) {
    this.pg = PG_RULES[dialect];
    const len = Math.max(0, b - a + 1);
    this.pm = new Int32Array(len).fill(-1);
    const stack: number[] = [];
    for (let i = a; i <= b; i++) {
      const t = T[i];
      if (t.kind !== "punct") continue;
      if (t.value === "(") stack.push(i);
      else if (t.value === ")" && stack.length) {
        const o = stack.pop()!;
        this.pm[o - a] = i;
        this.pm[i - a] = o;
      }
    }
  }

  match(i: number): number {
    return i >= this.a && i <= this.b ? this.pm[i - this.a] : -1;
  }

  /**
   * The `(` at `i` opens a query (`EXISTS (SELECT`, `ARRAY(SELECT`, `name(WITH …`): never a function's argument list,
   * whatever word precedes it. PostgreSQL and DuckDB do not reserve EXISTS, so it lexes as a name like a function's.
   */
  opensQuery(i: number): boolean {
    if (i + 1 > this.b || i + 1 === this.skip) return false;
    const n = W(this.T[i + 1]);
    return n === "SELECT" || n === "WITH";
  }

  private tok(i: number): Token | undefined {
    return i >= this.a && i <= this.b && i !== this.skip ? this.T[i] : undefined;
  }

  // ---- projections ----

  /** Column names projected by the SELECT at `s` up to (exclusive) `e`; undefined if unknown (`*`). */
  projection(s: number, e: number): string[] | undefined {
    const T = this.T;
    let i = s + 1;
    const w0 = W(T[i]);
    if (w0 === "DISTINCT" || w0 === "ALL") i++;
    if (W(T[i]) === "TOP") {
      i++;
      if (isPunct(T[i], "(")) i = (this.match(i) < 0 ? e : this.match(i)) + 1; else i++;
      if (W(T[i]) === "PERCENT") i++;
      if (W(T[i]) === "WITH" && W(T[i + 1]) === "TIES") i += 2;
    }
    const items: [number, number][] = [];
    let itemStart = i;
    for (; i < e; i++) {
      const t = T[i];
      if (isPunct(t, "(")) { const m = this.match(i); if (m < 0) { i = e; break; } i = m; continue; }
      if (t.kind === "keyword" && PROJECTION_END.has(t.upper)) break;
      if (isPunct(t, ",")) { items.push([itemStart, i]); itemStart = i + 1; }
    }
    items.push([itemStart, i]);
    const cols: string[] = [];
    for (const [x, y] of items) {
      if (this.skip >= x && this.skip < y) continue;
      const nm = this.itemName(x, y);
      if (nm === "*") return undefined;
      if (nm) cols.push(nm);
    }
    return cols;
  }

  private itemName(x: number, y: number): string | null {
    const T = this.T;
    if (y <= x) return null;
    const last = T[y - 1];
    if (isOp(last, "*") && (y - 1 === x || isPunct(T[y - 2], "."))) return "*";
    if (y - x >= 2 && W(T[y - 2]) === "AS" && (isNameTok(last) || last.kind === "string")) {
      return last.kind === "string" ? this.text.slice(last.start + 1, last.end - 1) : last.value;
    }
    if (!this.pg && y - x >= 3 && isNameTok(T[x]) && isOp(T[x + 1], "=")) return T[x].value;
    let chain = true;
    for (let z = x; z < y; z++) {
      const even = (z - x) % 2 === 0;
      if (even ? !isNameTok(T[z]) : !isPunct(T[z], ".")) { chain = false; break; }
    }
    if (chain && (y - x) % 2 === 1) return last.value;
    if (isNameTok(last) && y - x >= 2) {
      const p = T[y - 2];
      if (!isPunct(p, ".") && p.kind !== "operator" && !isPunct(p, ",")) return last.value;
    }
    return null;
  }

  nonAggregatedProjections(open: number, cursor: number): string[] {
    const T = this.T;
    let s = -1;
    for (let i = Math.max(this.a, open + 1); i <= cursor; i++) {
      if (isPunct(T[i], "(")) { const end = this.match(i); if (end > i) { i = end; continue; } }
      if (W(T[i]) === "SELECT") s = i;
    }
    if (s < 0) return [];
    let i = s + 1;
    const w0 = W(T[i]);
    if (w0 === "DISTINCT" || w0 === "ALL") i++;
    if (W(T[i]) === "TOP") {
      i++;
      if (isPunct(T[i], "(")) i = (this.match(i) < 0 ? this.b : this.match(i)) + 1; else i++;
      if (W(T[i]) === "PERCENT") i++;
      if (W(T[i]) === "WITH" && W(T[i + 1]) === "TIES") i += 2;
    }
    const items: [number, number][] = [];
    let itemStart = i;
    for (; i <= this.b; i++) {
      const t = T[i];
      if (isPunct(t, "(")) { const m = this.match(i); if (m < 0) break; i = m; continue; }
      if (t.kind === "keyword" && PROJECTION_END.has(t.upper)) break;
      if (isPunct(t, ",")) { items.push([itemStart, i]); itemStart = i + 1; }
    }
    if (itemStart < i) items.push([itemStart, i]);

    const cols: string[] = [];
    for (const [x, y] of items) {
      // Only a proven column name/qualified name. Expressions (including aggregate,
      // window and user-defined functions) need AST semantics, so do not guess.
      if (!isNameTok(T[x])) continue;
      let end = x + 1;
      while (end + 1 < y && isPunct(T[end], ".") && isNameTok(T[end + 1])) end += 2;
      const alias = end === y || (end + 1 === y && isNameTok(T[end])) ||
        (end + 2 === y && W(T[end]) === "AS" && isNameTok(T[end + 1]));
      if (!alias) continue;
      const raw = this.text.slice(T[x].start, T[end - 1].end);
      if (!cols.includes(raw)) cols.push(raw);
    }
    return cols;
  }

  // ---- CTEs ----

  parseCtes(): void {
    const T = this.T;
    let i = this.a;
    if (W(T[i]) !== "WITH") return;
    i++;
    if (W(T[i]) === "RECURSIVE") i++;
    while (i <= this.b) {
      const nameTok = this.tok(i);
      if (!isNameTok(nameTok)) return;
      const name = nameTok!.value;
      i++;
      let cols: string[] | undefined;
      if (isPunct(T[i], "(")) {
        const m = this.match(i);
        cols = [];
        const e = m < 0 ? this.b + 1 : m;
        for (let x = i + 1; x < e; x++) if (isNameTok(T[x]) && x !== this.skip) cols.push(T[x].value);
        i = e + 1;
      }
      if (W(T[i]) !== "AS") { this.ctes.push({ name, columns: cols }); return; }
      i++;
      if (W(T[i]) === "NOT") i++;
      if (W(T[i]) === "MATERIALIZED") i++;
      if (!isPunct(T[i], "(")) { this.ctes.push({ name, columns: cols }); return; }
      const m = this.match(i);
      const e = m < 0 ? this.b + 1 : m;
      if (!cols) {
        for (let x = i + 1; x < e; x++) {
          if (isPunct(T[x], "(")) { const mm = this.match(x); if (mm < 0) break; x = mm; continue; }
          if (W(T[x]) === "SELECT") { cols = this.projection(x, e); break; }
        }
      }
      this.ctes.push({ name, columns: cols });
      if (m < 0) return;
      i = m + 1;
      if (!isPunct(T[i], ",")) return;
      i++;
    }
  }

  // ---- table references ----

  private skipTop(j: number): number {
    const T = this.T;
    if (W(T[j]) === "TOP") {
      if (isPunct(T[j + 1], "(")) { const m = this.match(j + 1); j = m < 0 ? j + 2 : m + 1; }
      else j += 2;
      if (W(T[j]) === "PERCENT") j++;
    }
    return j;
  }

  private parseAlias(n: number): { alias?: string; cols?: string[]; next: number } {
    const T = this.T;
    let x = n;
    let viaAs = false;
    if (W(this.tok(x)) === "AS") { x++; viaAs = true; }
    const t = this.tok(x);
    if (!t || x > this.b || !(isNameTok(t) || (viaAs && t.kind === "keyword"))) return { next: n };
    const alias = t.value;
    x++;
    let cols: string[] | undefined;
    if (isPunct(T[x], "(") && x <= this.b) {
      const m = this.match(x);
      const e = m < 0 ? this.b + 1 : m;
      cols = [];
      for (let y = x + 1; y < e; y++) if (isNameTok(T[y]) && y !== this.skip) cols.push(T[y].value);
      x = e + 1;
    }
    return { alias, cols, next: x };
  }

  private isHintList(open: number, close: number): boolean {
    const T = this.T;
    if (close <= open + 1) return false;
    for (let x = open + 1; x < close; x++) {
      const t = T[x];
      if (isPunct(t, ",")) continue;
      if (W(t) && TABLE_HINTS.has(W(t))) {
        if (isPunct(T[x + 1], "(")) { const m = this.match(x + 1); if (m < 0) return false; x = m; }
        continue;
      }
      return false;
    }
    return true;
  }

  parseRef(j: number, role?: Ref["role"], noCall = false): Ref | undefined {
    const T = this.T;
    if (j > this.b || j === this.skip) return undefined;
    while (W(T[j]) === "ONLY" || W(T[j]) === "LATERAL") j++;
    if (j > this.b || j === this.skip) return undefined;
    const first = T[j];
    if (isPunct(first, "(")) {
      const m = this.match(j);
      if (m < 0) return undefined;
      const inner = W(T[j + 1]);
      let cols: string[] | undefined;
      if (inner === "SELECT") cols = this.projection(j + 1, m);
      else if (inner === "WITH") {
        for (let x = j + 2; x < m; x++) {
          if (isPunct(T[x], "(")) { const mm = this.match(x); if (mm < 0) break; x = mm; continue; }
          if (W(T[x]) === "SELECT") { cols = this.projection(x, m); break; }
        }
      } else if (inner !== "VALUES") {
        if (isNameTok(T[j + 1])) this.parseRef(j + 1);
        return undefined;
      }
      const al = this.parseAlias(m + 1);
      if (!al.alias) return undefined;
      const ref: Ref = {
        name: al.alias, alias: al.alias, isCte: false, isDerived: true, columns: al.cols ?? cols,
        offset: first.start, endIdx: al.next - 1, role,
      };
      this.refs.push(ref);
      return ref;
    }
    // dotted name chain: [db.][schema.]name, allowing empty parts (db..t); temp #t and table variables @t
    const parts: string[] = [];
    let x = j;
    let last = -1;
    for (;;) {
      if (x === this.skip || x > this.b) return undefined;
      const t = T[x];
      if (isNameTok(t) || (t.kind === "variable" && parts.length === 0)) {
        parts.push(t.value);
        last = x;
        x++;
        if (isPunct(T[x], ".") && x <= this.b) { x++; if (isPunct(T[x], ".")) { parts.push(""); x++; } continue; }
        break;
      }
      return undefined; // not a name, or the chain ends with '.'
    }
    let n = last + 1;
    if (!noCall && isPunct(T[n], "(") && n <= this.b) {
      const m = this.match(n);
      if (m < 0) return undefined;
      n = m + 1;
    }
    if (!this.pg && W(T[n]) === "WITH" && isPunct(T[n + 1], "(")) { const m = this.match(n + 1); n = m < 0 ? n + 2 : m + 1; }
    const al = noCall && role === "insert" ? { next: n } as { alias?: string; cols?: string[]; next: number } : this.parseAlias(n);
    let end = al.next - 1;
    if (!this.pg && W(T[al.next]) === "WITH" && isPunct(T[al.next + 1], "(")) {
      const m = this.match(al.next + 1);
      end = m < 0 ? end : m;
    }
    const name = parts[parts.length - 1];
    const ref: Ref = {
      name,
      schema: parts.length >= 2 ? parts[parts.length - 2] : undefined,
      database: parts.length >= 3 ? parts[parts.length - 3] : undefined,
      alias: al.alias,
      isCte: false,
      isDerived: false,
      offset: first.start,
      endIdx: Math.max(last, end),
      role,
    };
    if (al.cols) ref.columns = al.cols;
    this.refs.push(ref);
    this.skipUntil = Math.max(this.skipUntil, ref.endIdx);
    return ref;
  }

  private parseRefWithHints(j: number, role?: Ref["role"]): Ref | undefined {
    // `FROM t (NOLOCK)` vs TVF `dbo.f (1)`: decide by the parenthesized content
    const T = this.T;
    let x = j;
    while (x <= this.b && (isNameTok(T[x]) || isPunct(T[x], "."))) x++;
    if (!this.pg && x > j && isPunct(T[x], "(")) {
      const m = this.match(x);
      if (m > 0 && this.isHintList(x, m)) {
        const ref = this.parseRef(j, role, true);
        if (ref && ref.endIdx < m) {
          const al = this.parseAlias(m + 1);
          if (al.alias) { ref.alias = al.alias; ref.endIdx = al.next - 1; }
          else ref.endIdx = m;
        }
        return ref;
      }
    }
    return this.parseRef(j, role);
  }

  updateOk(i: number): boolean {
    const T = this.T;
    const prev = T[i - 1];
    const pu = i - 1 >= this.a ? W(prev) : "";
    const nu = W(T[i + 1]);
    // `UPDATE SET …` with no target is MERGE's `THEN UPDATE SET`, PostgreSQL's `DO UPDATE SET`, MySQL's `KEY UPDATE`
    // (ruled out by the previous word below) or a table name the user has not written yet: `UPDATE | SET x = 1`.
    if (nu === "STATISTICS" || (nu !== "SET" && FK_ACTION.has(nu))) return false;
    if (pu === "KEY" || (nu === "SET" && pu === "THEN")) return false;
    return !(pu === "ON" || pu === "FOR" || pu === "OF" || pu === "OR" || pu === "DO" || pu === "AFTER" ||
      pu === "BEFORE" || pu === "INSTEAD" || pu === "GRANT" || pu === "REVOKE" || pu === "DENY" || isPunct(prev, ","));
  }

  collectRefs(): void {
    const T = this.T;
    const frames: ("from" | "other" | "call")[] = ["other"];
    for (let i = this.a; i <= this.b; i++) {
      if (i === this.skip) continue;
      const t = T[i];
      if (t.kind === "punct") {
        if (t.value === "(") {
          const prev = T[i - 1];
          frames.push(i > this.a && !this.opensQuery(i) && (isNameTok(prev) || FUNC_KEYWORDS.has(W(prev))) ? "call" : "other");
        } else if (t.value === ")") { if (frames.length > 1) frames.pop(); }
        else if (t.value === "," && frames[frames.length - 1] === "from") this.parseRefWithHints(i + 1);
        continue;
      }
      const u = W(t);
      if (!u) continue;
      const top = frames.length - 1;
      if (i <= this.skipUntil && u !== "SELECT" && u !== "VALUES") continue;
      switch (u) {
        case "FROM":
          if (frames[top] === "call") break;
          frames[top] = "from";
          this.parseRefWithHints(i + 1, W(T[i - 1]) === "DELETE" ? "delete" : undefined);
          break;
        case "JOIN": case "APPLY":
          frames[top] = "from";
          this.parseRefWithHints(i + 1);
          break;
        case "WHERE": case "GROUP": case "ORDER": case "HAVING": case "SET": case "WINDOW": case "UNION":
        case "EXCEPT": case "INTERSECT": case "LIMIT": case "RETURNING": case "OUTPUT": case "WHEN":
          frames[top] = "other";
          break;
        case "SELECT": case "VALUES": case "EXEC": case "EXECUTE":
          if (frames[top] !== "call") frames[top] = "other";
          if (this.insert && this.insert.source < 0 && i > this.insert.targetEnd && i > this.insert.colsClose) this.insert.source = i;
          break;
        case "UPDATE":
          if (this.updateOk(i)) this.parseRefWithHints(this.skipTop(i + 1), "update");
          break;
        case "DELETE": {
          if (!this.updateOk(i)) break;
          const j = this.skipTop(i + 1);
          if (W(T[j]) !== "FROM") this.parseRef(j, "delete");
          break;
        }
        case "INSERT": {
          if (this.mergeTarget) {
            const colsOpen = isPunct(T[i + 1], "(") ? i + 1 : -1;
            this.setInsert(i, { name: this.mergeTarget.name, schema: this.mergeTarget.schema }, i, colsOpen);
            break;
          }
          let j = this.skipTop(i + 1);
          if (W(T[j]) === "INTO") j++;
          const ref = this.parseRef(j, "insert", true);
          const targetEnd = ref ? ref.endIdx : -1;
          const colsOpen = ref && isPunct(T[targetEnd + 1], "(") && W(T[targetEnd + 2]) !== "SELECT" ? targetEnd + 1 : -1;
          this.setInsert(i, ref ? { name: ref.name, schema: ref.schema } : undefined, targetEnd, colsOpen);
          break;
        }
        case "INTO": {
          const pu = W(T[i - 1]);
          if (pu === "INSERT" || pu === "MERGE") break;
          this.parseRef(i + 1, "into", true);
          break;
        }
        case "MERGE": {
          let j = this.skipTop(i + 1);
          if (W(T[j]) === "INTO") j++;
          this.mergeTarget = this.parseRefWithHints(j, "merge");
          break;
        }
        case "USING":
          if (this.mergeTarget || W(T[this.a]) === "DELETE") {
            frames[top] = "from";
            if (!isPunct(T[i + 1], "(") || W(T[i + 2]) === "SELECT") this.parseRef(i + 1);
          }
          break;
      }
    }
    // CTE references
    if (this.ctes.length) {
      for (const r of this.refs) {
        if (r.isDerived || r.schema !== undefined) continue;
        const c = this.ctes.find((x) => x.name.toLowerCase() === r.name.toLowerCase());
        if (c) { r.isCte = true; if (!r.columns) r.columns = c.columns; }
      }
    }
  }

  private setInsert(kw: number, target: InsertInfo["target"], targetEnd: number, colsOpen: number): void {
    const T = this.T;
    let colsClose = -1;
    const cols: string[] = [];
    if (colsOpen >= 0) {
      colsClose = this.match(colsOpen);
      const e = colsClose < 0 ? this.b + 1 : colsClose;
      for (let x = colsOpen + 1; x < e; x++) if (isNameTok(T[x]) && x !== this.skip) cols.push(T[x].value);
    }
    this.insert = { kw, target, targetEnd, colsOpen, colsClose, cols, source: -1 };
  }

  tables(): TableInScope[] {
    const refs = this.refs;
    const aliasSet = new Set<string>();
    for (const r of refs) if (r.alias) aliasSet.add(r.alias.toLowerCase());
    const out: TableInScope[] = [];
    const seen = new Set<string>();
    for (const r of refs) {
      if ((r.role === "update" || r.role === "delete") && r.schema === undefined && !r.alias && aliasSet.has(r.name.toLowerCase())) continue;
      const key = `${r.database ?? ""}.${r.schema ?? ""}.${r.name}|${r.alias ?? ""}`.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      const t: TableInScope = { name: r.name, isCte: r.isCte, isDerived: r.isDerived, offset: r.offset };
      if (r.schema !== undefined) t.schema = r.schema;
      if (r.database !== undefined) t.database = r.database;
      if (r.alias !== undefined) t.alias = r.alias;
      if (r.columns !== undefined) t.columns = r.columns;
      out.push(t);
    }
    return out;
  }

  // ---- clause at the cursor ----

  walk(k: number): { frame: WFrame; parent?: WFrame } {
    const T = this.T;
    const st: WFrame[] = [{ clause: "unknown", fn: "", open: this.a - 1, execKw: -1 }];
    const stmtKind = W(T[this.a]);
    let createTable = false;
    if (stmtKind === "CREATE") {
      for (let x = this.a + 1; x <= Math.min(this.a + 4, this.b); x++) if (W(T[x]) === "TABLE") { createTable = true; break; }
    }
    let rootParenSeen = false;
    const merge = !!this.mergeTarget;
    for (let i = this.a; i <= k; i++) {
      if (i === this.skip) continue;
      const t = T[i];
      const top = st[st.length - 1];
      if (t.kind === "punct") {
        if (t.value === "(") {
          const prev = i > this.a ? T[i - 1] : undefined;
          const pu = W(prev);
          const f: WFrame = { clause: top.clause, fn: "sub", open: i, execKw: -1 };
          if (pu === "CAST" || pu === "TRY_CAST") { f.fn = "cast"; if (TABLE_CLAUSES.has(top.clause)) f.clause = "select"; }
          else if (pu === "CONVERT" || pu === "TRY_CONVERT") { f.fn = "convert"; f.clause = "declare"; }
          else if (this.insert && i === this.insert.colsOpen) { f.fn = "insertCols"; f.clause = "insertColumns"; }
          else if (top.clause === "values" && top.fn !== "values" && (pu === "VALUES" || isPunct(prev, ","))) { f.fn = "values"; f.clause = "values"; }
          else if (top.clause === "exec" && top.execKw >= 0 && this.pg) { f.fn = "execCall"; f.clause = "execArgs"; f.execKw = top.execKw; }
          else if (pu === "TABLE" || (createTable && st.length === 1 && !rootParenSeen)) { f.fn = "colDefs"; f.clause = "unknown"; }
          else if (prev && !this.opensQuery(i) && (isNameTok(prev) || FUNC_KEYWORDS.has(pu))) {
            f.fn = "call";
            if (TABLE_CLAUSES.has(top.clause)) f.clause = "select";
          }
          if (st.length === 1) rootParenSeen = true;
          st.push(f);
        } else if (t.value === ")") {
          if (st.length > 1) {
            const popped = st.pop()!;
            if (popped.fn === "insertCols") st[st.length - 1].clause = "values";
          }
        } else if (t.value === ",") {
          if (top.fn === "convert") top.clause = "select";
          else if (st.length === 1 && stmtKind === "DECLARE") top.clause = "declare";
        }
        continue;
      }
      if (t.kind === "operator") {
        if ((t.value === "=" || t.value === ":=") && top.clause === "declare" && top.fn !== "colDefs") top.clause = "select";
        continue;
      }
      const u = W(t);
      if (!u) continue;
      // A CASE expression stays in its clause: `SELECT CASE … END, c.|` is still the select list (END / ELSE there
      // would otherwise read as a block's END / IF … ELSE).
      if (u === "CASE") { top.cases = (top.cases ?? 0) + 1; continue; }
      if (top.cases) {
        if (u === "END") { top.cases--; if (W(T[i + 1]) === "CASE") i++; continue; } // END CASE (a procedure's CASE)
        if (u === "WHEN" || u === "THEN" || u === "ELSE") continue;
      }
      const next = T[i + 1];
      switch (u) {
        case "SELECT": top.clause = "select"; break;
        case "FROM":
          if (top.fn === "call") break;
          top.clause = W(T[i - 1]) === "DELETE" && i - 1 >= this.a ? "delete" : "from";
          break;
        case "JOIN": case "APPLY": top.clause = "join"; break;
        case "ON":
          if (TABLE_CLAUSES.has(top.clause) || top.clause === "on" || merge) top.clause = W(next) === "CONFLICT" ? "unknown" : "on";
          break;
        case "USING": top.clause = isPunct(next, "(") && W(T[i + 2]) !== "SELECT" ? "on" : "from"; break;
        case "WHERE": top.clause = "where"; break;
        case "GROUP": top.clause = "groupBy"; break;
        case "ORDER": top.clause = "orderBy"; break;
        case "HAVING": top.clause = "having"; break;
        case "PARTITION": if (W(next) === "BY") top.clause = "groupBy"; break;
        case "UPDATE":
          if (W(T[i - 1]) === "KEY") top.clause = "set"; // ON DUPLICATE KEY UPDATE col = …: the insert target's columns
          else if (this.updateOk(i)) top.clause = "update";
          break;
        case "SET": top.clause = "set"; break;
        case "INSERT": top.clause = "insertInto"; break;
        case "INTO": top.clause = W(T[i - 1]) === "MERGE" ? "from" : "insertInto"; break;
        case "VALUES": top.clause = "values"; break;
        case "DELETE": if (this.updateOk(i)) top.clause = "delete"; break;
        case "MERGE": top.clause = "from"; break;
        case "EXEC": case "EXECUTE": case "CALL":
          if (!this.pg && isPunct(next, "(")) { top.clause = "unknown"; break; }
          if (i > this.a && CONTINUATION_PREV.has(W(T[i - 1]))) break;
          if (W(next) === "AS") break;
          top.clause = "exec";
          top.execKw = i;
          break;
        case "DECLARE": top.clause = "declare"; break;
        case "USE": top.clause = "use"; break;
        case "IF": case "WHILE": case "ELSIF": top.clause = "where"; break;
        case "WHEN": if (merge && st.length === 1) top.clause = "on"; break;
        case "PRINT": case "RETURN": case "RAISERROR": case "THROW": case "RAISE": case "PERFORM":
        case "OUTPUT": case "RETURNING":
          top.clause = "select";
          break;
        case "AS": if (top.fn === "cast") top.clause = "declare"; break;
        case "TABLE": {
          const pu = W(T[i - 1]);
          if (pu === "TRUNCATE") top.clause = "delete";
          else if (pu === "DROP" || pu === "ALTER") top.clause = "from";
          break;
        }
        case "CREATE": case "ALTER": case "DROP": case "GRANT": case "REVOKE": case "BEGIN": case "END": case "ELSE":
        case "COMMIT": case "ROLLBACK":
          top.clause = "unknown";
          break;
      }
    }
    return { frame: st[st.length - 1], parent: st.length > 1 ? st[st.length - 2] : undefined };
  }
}

// ---------------------------------------------------------------------------------------------
// scan()
// ---------------------------------------------------------------------------------------------

function emptyContext(offset: number): CursorContext {
  return {
    clause: "unknown", statementStart: offset, statementEnd: offset, tablesInScope: [], ctes: [], variables: [],
    prefix: "", prefixStart: offset, inStringOrComment: false,
  };
}

/** Compute the cursor context at `offset` (§9). Never throws. */
export function scan(text: string, offset: number, dialect: Dialect): CursorContext {
  const src = typeof text === "string" ? text : "";
  const off = Math.max(0, Math.min(Number.isFinite(offset) ? offset : 0, src.length));
  try {
    return scanImpl(src, off, knownDialect(dialect), 0);
  } catch {
    return emptyContext(off);
  }
}

function stmtBounds(T: Token[], loc: StmtLoc, fallback: number): { start: number; end: number } {
  if (loc.start < 0) return { start: fallback, end: fallback };
  return { start: T[loc.start].start, end: T[loc.end].end };
}

function scanImpl(text: string, offset: number, dialect: Dialect, depth: number): CursorContext {
  const stream = getStream(text, dialect);
  const T = stream.code;
  const idx = lastStartingBefore(T, offset);
  const cur = idx >= 0 ? T[idx] : undefined;

  // Inside a string (or a pg function body) / comment?
  if (cur && cur.kind === "string" && (offset < cur.end || (cur.open && offset === cur.end))) {
    if (PG_RULES[dialect] && cur.tag && depth < 3) {
      const pu = W(T[idx - 1]);
      const bs = cur.start + cur.tag;
      const be = cur.open ? cur.end : cur.end - cur.tag;
      if ((pu === "AS" || pu === "DO") && offset >= bs && offset <= be) return scanBody(text, T, idx, bs, be, offset, depth, dialect);
    }
    return inStringContext(T, idx, offset, dialect);
  }
  const ci = lastStartingBefore(stream.comments, offset);
  if (ci >= 0) {
    const c = stream.comments[ci];
    if (offset < c.end || (offset === c.end && (c.line || c.open))) return inStringContext(T, idx, offset, dialect);
  }

  // The word being typed
  let pi = -1;
  let prefix = "";
  let prefixStart = offset;
  if (cur && offset <= cur.end) {
    if (cur.kind === "qident") {
      if (cur.open || offset < cur.end) {
        pi = idx;
        prefixStart = cur.start + 1;
        prefix = text.slice(prefixStart, offset);
      }
    } else if (cur.kind === "ident" || cur.kind === "keyword" || cur.kind === "variable" || cur.kind === "param") {
      pi = idx;
      prefixStart = cur.start;
      prefix = text.slice(cur.start, offset);
    }
  }
  const k = pi >= 0 ? pi - 1 : idx;

  // Qualifier: name chain glued to the prefix (or cursor) by '.'
  let qualifier: string | undefined;
  {
    const anchor = pi >= 0 ? T[pi].start : offset;
    let j = k;
    if (j >= 0 && isPunct(T[j], ".") && T[j].end === anchor) {
      const parts: string[] = [];
      j--;
      while (j >= 0) {
        const t = T[j];
        if ((isNameTok(t) || t.kind === "variable" || t.kind === "keyword") && t.end === T[j + 1].start) {
          parts.unshift(t.value);
          j--;
          if (j >= 0 && isPunct(T[j], ".") && T[j].end === T[j + 1].start) { j--; continue; }
          break;
        }
        if (isPunct(t, ".") && t.end === T[j + 1].start) { parts.unshift(""); j--; continue; }
        break;
      }
      if (parts.length && parts.some((p) => p !== "")) qualifier = parts.join(".");
    }
  }

  const loc = locateStatement(T, k, pi, dialect);
  const ctx: CursorContext = {
    clause: "unknown",
    statementStart: loc.start < 0 || loc.afterCursor ? prefixStart : T[loc.start].start,
    statementEnd: loc.start < 0 ? offset : Math.max(T[loc.end].end, offset),
    tablesInScope: [],
    ctes: [],
    variables: [],
    prefix,
    prefixStart,
    inStringOrComment: false,
  };
  if (qualifier !== undefined) ctx.qualifier = qualifier;
  ctx.variables = collectVariables(text, T, k, dialect, stream.marks).map((v) => (v.type !== undefined ? { name: v.name, type: v.type, kind: v.kind } : { name: v.name, kind: v.kind }));
  if (loc.start < 0) return ctx;

  const an = new Analyzer(text, T, loc.start, loc.end, pi, dialect);
  an.parseCtes();
  an.collectRefs();
  ctx.ctes = an.ctes;
  ctx.tablesInScope = an.tables();
  const ins = an.insert;
  if (ins?.target) {
    ctx.insertTarget = ins.target.schema !== undefined ? { name: ins.target.name, schema: ins.target.schema } : { name: ins.target.name };
    if (ins.colsOpen >= 0) ctx.insertColumnList = ins.cols;
  }
  if (k < loc.start || loc.afterCursor) return ctx;

  const { frame, parent } = an.walk(k);
  let clause: Clause = frame.clause;
  let slot: CursorContext["slot"];
  const prev = T[k];
  const pu = W(prev);
  const stmtKind = W(T[loc.start]);

  const isHeader = !an.pg && (stmtKind === "CREATE" || stmtKind === "ALTER") && (() => {
    let x = loc.start + 1;
    if (W(T[x]) === "OR") x += 2;
    return PROC_WORDS.has(W(T[x]));
  })();

  if (isOp(prev, "::")) {
    clause = "declare";
  } else if (frame.fn === "colDefs") {
    const p2 = T[k - 1];
    if (isNameTok(prev) && k - 1 >= frame.open && (isPunct(p2, "(") || isPunct(p2, ","))) clause = "declare";
    else if (isPunct(prev, "(") || isPunct(prev, ",")) { clause = "unknown"; slot = "name"; }
    else clause = "unknown";
  } else if (isHeader && frame.fn !== "call" && frame.fn !== "sub" && clause === "unknown") {
    if (prev.kind === "variable" || (pu === "AS" && T[k - 1]?.kind === "variable")) clause = "declare";
    else if (isOp(prev, "=")) clause = "select";
    else if (isPunct(prev, ",") || isNameTok(prev) || isPunct(prev, "(")) slot = "name";
  } else if (clause === "declare" && frame.fn === "") {
    if (pu === "DECLARE" || isPunct(prev, ",")) slot = "name";
    else if (an.pg && isNameTok(prev) && (W(T[k - 1]) === "DECLARE" || isPunct(T[k - 1], ";"))) clause = "declare";
  } else if (an.pg && clause === "unknown" && k === loc.start && isNameTok(prev) && inPgDeclareSection(T, loc.start)) {
    clause = "declare"; // PL/pgSQL declare section: `name |` → type
  }
  if (clause === "unknown" && isOp(prev, ":=")) clause = "select";

  // EXEC / CALL
  const execFrame = frame.fn === "execCall" ? parent : frame;
  if ((clause === "exec" || frame.fn === "execCall") && execFrame && execFrame.execKw >= 0) {
    const e = execFrame.execKw;
    let j = e + 1;
    if (T[j]?.kind === "variable" && isOp(T[j + 1], "=") && j + 1 <= k) j += 2;
    const chainStart = j;
    let chainEnd = j - 1;
    const parts: string[] = [];
    while (j <= loc.end && j !== pi) {
      const t = T[j];
      if (isNameTok(t) || (t.kind === "keyword" && parts.length > 0)) {
        parts.push(t.value);
        chainEnd = j;
        j++;
        if (isPunct(T[j], ".") && j !== pi) { chainEnd = j; j++; if (isPunct(T[j], ".")) { parts.push(""); chainEnd = j; j++; } continue; }
        break;
      }
      break;
    }
    if (frame.fn !== "execCall" && (k < chainStart || (k <= chainEnd && isPunct(T[k], ".")) || parts.length === 0)) {
      clause = "exec";
    } else {
      clause = "execArgs";
      ctx.execTarget = parts.join(".");
      const used: string[] = [];
      for (let x = chainEnd + 1; x <= loc.end; x++) {
        if (x === pi) continue;
        const t = T[x];
        if (!an.pg && t.kind === "variable" && isOp(T[x + 1], "=")) used.push(t.value);
        else if (an.pg && isNameTok(t) && isOp(T[x + 1], "=>")) used.push(t.value);
      }
      ctx.execArgsUsed = used;
      if (isOp(prev, "=") || isOp(prev, "=>")) slot = "argValue";
    }
  }

  // INSERT target / column list / values
  if (ins && ins.target && k >= ins.targetEnd && clause !== "execArgs" && clause !== "exec") {
    if (ins.colsOpen >= 0 && k >= ins.colsOpen && (ins.colsClose < 0 || k < ins.colsClose)) {
      if (frame.fn === "insertCols") clause = "insertColumns";
    } else if ((ins.source < 0 || ins.source > k) && k >= Math.max(ins.targetEnd, ins.colsClose) && (clause === "insertInto" || clause === "values" || clause === "from")) {
      clause = "values";
    }
  }
  if (clause === "values" && frame.fn === "values") slot = "argValue";

  // Table-reference positions: object, alias (after AS) or after a complete reference
  if (TABLE_CLAUSES.has(clause) && qualifier === undefined && slot === undefined) {
    if (pu === "AS") slot = "alias";
    else if (isNameTok(prev) || prev.kind === "variable" || isPunct(prev, ")")) slot = "afterReference";
  } else if (qualifier === undefined && slot === undefined && pu === "AS" && frame.fn !== "cast" &&
    (clause === "select" || clause === "where" || clause === "on" || clause === "groupBy" || clause === "orderBy" || clause === "having")) {
    slot = "alias";
  }

  ctx.clause = clause;
  if (clause === "groupBy") {
    ctx.nonAggregatedColumns = an.nonAggregatedProjections(frame.open, k);
  }
  if (slot) ctx.slot = slot;
  return ctx;
}

function inPgDeclareSection(T: Token[], s: number): boolean {
  for (let j = s - 1; j >= 0 && j >= s - 5000; j--) {
    const u = W(T[j]);
    if (u === "BEGIN") return false;
    if (u === "DECLARE") return true;
  }
  return false;
}

function inStringContext(T: Token[], idx: number, offset: number, dialect: Dialect): CursorContext {
  const ctx = emptyContext(offset);
  ctx.inStringOrComment = true;
  try {
    const loc = locateStatement(T, idx, -1, dialect);
    const b = stmtBounds(T, loc, offset);
    ctx.statementStart = b.start;
    ctx.statementEnd = Math.max(b.end, offset);
  } catch { /* keep defaults */ }
  return ctx;
}

/** PostgreSQL: the cursor is inside a `$$ ... $$` function/DO body — scan the body as its own script. */
function scanBody(text: string, T: Token[], idx: number, bs: number, be: number, offset: number, depth: number, dialect: Dialect): CursorContext {
  const body = text.slice(bs, be);
  const inner = scanImpl(body, offset - bs, dialect, depth + 1);
  inner.statementStart += bs;
  inner.statementEnd += bs;
  inner.prefixStart += bs;
  for (const t of inner.tablesInScope) if (t.offset !== undefined) t.offset += bs;
  if (!inner.inStringOrComment) {
    const loc = locateStatement(T, idx - 1, -1, dialect);
    if (loc.start >= 0 && (W(T[loc.start]) === "CREATE" || W(T[loc.start]) === "ALTER")) {
      const params: VarDecl[] = [];
      parsePgHeader(text, T, loc.start, idx, params);
      if (params.length) {
        const have = new Set(inner.variables.map((v) => v.name.toLowerCase()));
        const extra: CursorContext["variables"] = params.filter((p) => !have.has(p.name.toLowerCase()))
          .map((p) => (p.type !== undefined ? { name: p.name, type: p.type, kind: p.kind } : { name: p.name, kind: p.kind }));
        inner.variables = extra.concat(inner.variables);
      }
    }
  }
  return inner;
}

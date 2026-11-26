// Tier-1 cursor-context scanner (§9). Synchronous and tolerant: it never throws on broken or partial SQL.
// Work per call: one cached/incremental token stream, a backward boundary search from the cursor, a forward
// statement split over the enclosing statement only, and one analysis pass over that statement.
import type { Dialect } from "../../ipc/contract";
import type { Clause, CursorContext, SchemaIndex, TableInScope } from "./types";
import { getStream, tokenize, type Token } from "./lexer";
import {
  ANCHOR_KEYWORDS, BLOCK_PREV, CONTINUATION_PREV, NEWLINE_VERBS, OBJECT_KIND_WORDS, OPERAND_PREV, PROJECTION_END, ROUTINE_PREV, TABLE_HINTS,
  functionVerbs, reservedWords, statementKeywords,
} from "./keywords";
import { DIALECTS, foldIdentifier, knownDialect } from "../../engines";

export { tokenize };
export type { Token, TokenKind } from "./lexer";

/**
 * Which statement grammar a dialect follows here: PostgreSQL's (PL/pgSQL blocks and DECLARE sections, `$tag$` routine
 * bodies scanned as their own script, named arguments with `=>`) or T-SQL's (procedure headers, `@variables`, MERGE
 * ending in `;`, table hints). SQLite has no procedural code and MySQL scripts end statements with `;`: both take the
 * T-SQL rules, whose extra cases (hints, `@x =` arguments) never match their text.
 */
const PG_RULES: Record<Dialect, boolean> = { tsql: false, pg: true, sqlite: false, mySql: false, duckDb: false, oracle: false };

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

/**
 * Words that continue a FROM clause and are never an alias written without AS, in every dialect. Most are reserved in
 * every dialect, but not all: Oracle's reserved list (V$RESERVED_WORDS) has no JOIN, LEFT, USING, … so the lexer reads
 * them as names.
 */
const NEVER_ALIAS = new Set([
  "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "USING", "ON",
  "WHERE", "GROUP", "ORDER", "HAVING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "LIMIT", "OFFSET",
  "FETCH", "RETURNING", "LATERAL", "TABLESAMPLE", "SET", "VALUES", "WHEN", "FOR", "INTO",
]);

/**
 * Clause words of one dialect that are plain names in another (completion audit 2026-11-25, H10): Oracle's START WITH
 * starts a hierarchical query, while `FROM orders start` names the alias start in PostgreSQL. With one list for every
 * dialect, PostgreSQL lost such aliases.
 */
const DIALECT_NEVER_ALIAS: Record<Dialect, ReadonlySet<string>> = {
  tsql: new Set(["APPLY", "PIVOT", "UNPIVOT", "OUTPUT", "OPTION"]),
  oracle: new Set(["CONNECT", "START", "MINUS", "APPLY", "PIVOT", "UNPIVOT", "PARTITION"]),
  duckDb: new Set(["POSITIONAL", "ASOF", "ANTI", "SEMI", "QUALIFY", "PIVOT", "UNPIVOT"]),
  mySql: new Set(["PARTITION"]),
  pg: new Set(),
  sqlite: new Set(),
};

/**
 * Words that start a clause after a FROM item, after AS or not, only before one of these tokens ("(" a parenthesis),
 * and are names elsewhere (H10): Oracle's flashback `AS OF TIMESTAMP …` and `SAMPLE (10)`.
 */
const CLAUSE_BEFORE: Partial<Record<Dialect, Readonly<Record<string, readonly string[]>>>> = {
  oracle: { OF: ["TIMESTAMP", "SCN", "PERIOD"], SAMPLE: ["(", "BLOCK"], SUBPARTITION: ["(", "FOR"], VERSIONS: ["BETWEEN", "PERIOD"] },
};

/** `t` is a clause word after a FROM item in `dialect`, never an alias written without AS. */
export function neverAlias(t: Token, dialect: Dialect): boolean {
  return t.kind === "ident" && (NEVER_ALIAS.has(t.upper) || DIALECT_NEVER_ALIAS[dialect].has(t.upper));
}

/** The token at `x` starts one of `dialect`'s CLAUSE_BEFORE clauses. */
function clauseBefore(T: Token[], x: number, dialect: Dialect): boolean {
  const before = CLAUSE_BEFORE[dialect]?.[W(T[x])];
  return before !== undefined && before.some((w) => (w === "(" ? isPunct(T[x + 1], "(") : W(T[x + 1]) === w));
}

/** Words between INSERT and INTO (MySQL), or after INSERT OR (SQLite conflict resolution). */
const INSERT_MODIFIERS = new Set(["IGNORE", "LOW_PRIORITY", "DELAYED", "HIGH_PRIORITY", "ABORT", "FAIL", "ROLLBACK"]);

/** Engines whose GROUP BY may name a select-list alias. */
const GROUP_BY_ALIASES: Record<Dialect, boolean> = { tsql: false, oracle: false, pg: true, mySql: true, sqlite: true, duckDb: true };

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

  private readonly fnVerbs: Set<string>;
  /** MySQL `INSERT [INTO] t SET a = 1`: that SET is the insert's, not a statement (completion audit M19). */
  private readonly insertSet: boolean;
  /** The dialect's reserved words: a verb outside them may be a column's name (verbOnNewLine). */
  private readonly reserved: Set<string>;

  /** `text`: what the tokens index, to tell a verb that starts a new line (NEWLINE_VERBS); without it none does. */
  constructor(private readonly T: Token[], dialect: Dialect, private readonly text = "") {
    this.tsql = !PG_RULES[dialect];
    this.stmt = statementKeywords(dialect);
    this.fnVerbs = functionVerbs(dialect);
    this.insertSet = dialect === "mySql";
    this.reserved = reservedWords(dialect);
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
      // MySQL / SQLite `REPLACE INTO t …` is an insert: its SELECT or SET continues it.
      case "INSERT": case "REPLACE": this.insertPending = true; break;
      case "UPDATE": this.updateSetPending = true; break;
      case "WITH": this.withPending = true; break;
      case "EXPLAIN": this.explainPending = true; break;
      case "FROM": this.fromFirst = true; break;
      // MERGE runs to its `;` on every engine: its WHEN … THEN never ends it (PL/pgSQL's THEN rule would).
      case "MERGE": this.merge = true; break;
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
        else if (u === "MERGE") this.merge = true;
      }
    }
    if (u === "UPDATE" && W(T[i + 1]) === "SET") this.updateSetPending = true;
    else if (u === "SET" && this.updateSetPending) this.updateSetPending = false;
    // MySQL `INSERT … SET a = 1` takes no query after its SET: a SELECT on the next line is a statement of its own (code
    // audit 2026-11-26, M16; before, the insert still waited for its SELECT and swallowed it).
    else if (u === "SET" && this.insertSet && this.insertPending && (this.kind === "INSERT" || this.kind === "REPLACE")) this.insertPending = false;
    return CONT;
  }

  private isBoundary(i: number, u: string): boolean {
    if (!this.stmt.has(u)) return false;
    const T = this.T;
    const prev = T[i - 1];
    const pu = W(prev);
    if (isPunct(prev, ",")) return false;
    // `… OR ⏎UPDATE t`, `… IN ⏎DELETE …`: an operand slot left open, as after WHERE (H12) — not CREATE OR ALTER /
    // REPLACE (T-SQL, PostgreSQL) or FOR … IN SELECT / EXECUTE (PL/pgSQL), which go on.
    if (CONTINUATION_PREV.has(pu) && !this.slotVerbAfter(i, u, pu)) return false;
    // MySQL's DESC is ORDER BY's direction mid-statement; first on its line before a table name, it is DESCRIBE (L4),
    // also after an operand slot left open at the line's end (M21).
    if (u === "DESC") return this.describeOnNewLine(i);
    // A name, not a verb (completion audit 2026-11-25, H6): a qualified part (`p.comment`, `e.open`), an operand
    // (`SELECT comment`, `… AND open`, `= close`), a routine (`CALL refresh(`), a verb-named function (MySQL `IF(`,
    // `REPLACE(`, `TRUNCATE(`), or MySQL's `DESC`, which mid-statement is ORDER BY's direction.
    // An operand slot left open at the end of a line (`… WHERE ⏎UPDATE t`, T-SQL `retry: ⏎UPDATE`) does not hold a
    // NEWLINE_VERBS verb: that line is the next statement (second audit review: the slot swallowed it, so the
    // completion list and Run current statement took in the next statement's tables).
    if (ROUTINE_PREV.has(pu) || (isOperandAt(T, i) && !this.verbOnNewLine(i, u))) return false;
    // A CASE's THEN / ELSE opens no block: `CASE WHEN … THEN TRUNCATE(x, 0)` is the function (M16).
    if (isPunct(T[i + 1], "(") && this.fnVerbs.has(u) && !(BLOCK_PREV.has(pu) && this.caseDepth === 0)) return false;
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
      case "SET":
        if (this.insertSet && this.insertPending && (this.kind === "INSERT" || this.kind === "REPLACE")) return false;
        return !this.updateSetPending && !this.ddl && pu !== "DELETE" && pu !== "UPDATE";
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

  /**
   * Token i is a NEWLINE_VERBS verb first on its line, not `UNION DISTINCT`'s query. A verb the dialect does not reserve
   * (Oracle lexes MERGE, TRUNCATE, CALL, EXEC / EXECUTE as names; PostgreSQL and MySQL reserve some of them only) counts
   * only when the words after it are that statement's (unreservedVerbAt): it may be a column's name (M16).
   */
  private verbOnNewLine(i: number, u: string): boolean {
    const T = this.T, prev = T[i - 1];
    if (!this.text || !prev || !NEWLINE_VERBS.has(u)) return false;
    if (!this.reserved.has(u) && !unreservedVerbAt(T, i)) return false;
    if (W(prev) === "DISTINCT" && CONTINUATION_PREV.has(W(T[i - 2]))) return false;
    return this.lineBreakBefore(i);
  }

  /** After OR / IN (`pu`), token i is a NEWLINE_VERBS verb on the next line, not the statement going on (H12). */
  private slotVerbAfter(i: number, u: string, pu: string): boolean {
    const before = W(this.T[i - 2]);
    if (pu === "OR") return before !== "CREATE" && before !== "INSERT" && this.verbOnNewLine(i, u);
    if (pu === "IN") return u !== "SELECT" && u !== "WITH" && u !== "EXECUTE" && u !== "VALUES" && this.verbOnNewLine(i, u);
    return false;
  }

  /** A line break between token i and the one before it. */
  private lineBreakBefore(i: number): boolean {
    const T = this.T, prev = T[i - 1];
    if (!this.text || !prev) return false;
    for (let p = prev.end; p < T[i].start; p++) {
      const ch = this.text.charCodeAt(p);
      if (ch === 10 || ch === 13) return true;
    }
    return false;
  }

  /**
   * MySQL `DESC t` first on its line: a name follows (ORDER BY's direction is followed by `,`, LIMIT, the end, …). A
   * column named NULLS stays a direction's (`DESC NULLS LAST` elsewhere).
   */
  private describeOnNewLine(i: number): boolean {
    const next = this.T[i + 1];
    return isNameTok(next) && next.upper !== "NULLS" && !NEVER_ALIAS.has(next.upper) && this.lineBreakBefore(i);
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

/**
 * Token `i` sits where an operand goes: right after a `.` (`p.comment`; `a.` then EXEC on the next line is an unfinished
 * name and a new statement), after an operator, or after a word such as SELECT / WHERE / AND (OPERAND_PREV).
 */
function isOperandAt(T: Token[], i: number): boolean {
  const prev = T[i - 1];
  if (prev?.kind === "operator") {
    // `SELECT *` / `, *` / `t.*` is a select list's star, which a statement may follow; any other operator wants an operand.
    if (prev.value !== "*") return true;
    const before = T[i - 2];
    return !(W(before) === "SELECT" || W(before) === "DISTINCT" || isPunct(before, ",") || isPunct(before, "."));
  }
  return (isPunct(prev, ".") && prev.end === T[i].start) || OPERAND_PREV.has(W(prev));
}

/**
 * An unreserved verb (lexed as a name) at token i is followed by its statement's words: MERGE INTO, TRUNCATE [TABLE |
 * ONLY | CLUSTER] name, CALL name( / name., EXEC / EXECUTE IMMEDIATE or a routine name. A column of that name is followed
 * by an operator, `,`, IS, AND, FROM, …
 */
function unreservedVerbAt(T: Token[], i: number): boolean {
  const next = T[i + 1], after = T[i + 2];
  const nu = W(next);
  const call = isNameTok(next) && (after === undefined || isPunct(after, "(") || isPunct(after, ".") || isPunct(after, ";"));
  switch (T[i].upper) {
    case "MERGE": return nu === "INTO";
    case "TRUNCATE": return nu === "TABLE" || nu === "ONLY" || nu === "CLUSTER" || (isNameTok(next) && (after === undefined || isPunct(after, ";")));
    case "CALL": return isNameTok(next) && (isPunct(after, "(") || isPunct(after, "."));
    case "EXEC": case "EXECUTE": return nu === "IMMEDIATE" || call;
    default: return false;
  }
}

function isAnchorAt(T: Token[], j: number): boolean {
  const prev = T[j - 1];
  const pu = W(prev);
  if (isPunct(prev, ",") || CONTINUATION_PREV.has(pu)) return false;
  // MySQL `SELECT IF(…)`, `= TRUNCATE(x, 2)`, `t.create`: a function or a name, not a statement to restart at.
  if (isOperandAt(T, j)) return false;
  const u = T[j].upper;
  // `TRUNCATE (` is MySQL's function (no TRUNCATE statement has a parenthesis next), and `THEN IF (` / `ELSE IF (` may
  // be MySQL's IF() in a CASE: the restart point moves back further, which is always safe (M16: the split began at the
  // function, and the statement lost its FROM).
  if (isPunct(T[j + 1], "(") && (u === "TRUNCATE" || (u === "IF" && (pu === "THEN" || pu === "ELSE")))) return false;
  if (u === "IF") return !OBJECT_KIND_WORDS.has(pu);
  return true;
}

/**
 * A safe place to start splitting for the statement holding token `k`: after the nearest ';' or GO before it, or an anchor
 * keyword at the outermost relative paren depth (0 when there is none).
 */
function restartPoint(T: Token[], k: number): number {
  let d = 0, m = 0;
  for (let j = k; j >= 0; j--) {
    const t = T[j];
    if (t.kind === "go" || (t.kind === "punct" && t.value === ";")) return j + 1;
    if (t.kind === "punct") {
      if (t.value === ")") d++;
      else if (t.value === "(") { d--; if (d < m) m = d; }
      continue;
    }
    if (d === m && (t.kind === "keyword" || t.kind === "ident") && ANCHOR_KEYWORDS.has(t.upper) && isAnchorAt(T, j)) return j;
  }
  return 0;
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
export function locateStatement(T: Token[], k: number, skip: number, dialect: Dialect, text = ""): StmtLoc {
  const s0 = restartPoint(T, k);
  const sp = new Splitter(T, dialect, text);
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
  /** The query level the reference belongs to: its enclosing "(" (the statement's root: a - 1) and set-operation branch. */
  scopeOpen?: number;
  scopeBranch?: number;
  /** A derived table whose select list is `*`: the "(" of its query, whose own tables give its columns. */
  starOpen?: number;
  /** A derived table: the token indexes of its "(" and ")" (inside them it is not a source of itself). */
  derivedOpen?: number;
  derivedClose?: number;
  /** A named reference: the token index of each written part of its name (`db..t` has two), for semantic colours. */
  partAt?: number[];
  /** The name is called: `dbo.f(1)`, `o.x.nodes(…)` (a column's method on T-SQL APPLY), Oracle `pkg.f(…)`. */
  call?: boolean;
}

/** Clauses whose cursor is in an expression over the statement's sources. */
const EXPRESSION_CLAUSES = new Set<Clause>(["select", "where", "groupBy", "having", "orderBy", "on"]);

/** Words that start the next branch of a compound query. */
const SET_OPERATORS = new Set(["UNION", "EXCEPT", "INTERSECT", "MINUS"]);

interface InsertInfo {
  kw: number;
  target?: { name: string; schema?: string };
  targetEnd: number;
  colsOpen: number;
  colsClose: number;
  cols: string[];
  source: number;
}

/**
 * `convert`: T-SQL `CONVERT(type, expr)`, a type first. `convertExpr`: MySQL `CONVERT(expr, type)` and
 * `CONVERT(expr USING charset)`, an expression first (completion audit M19).
 */
type FrameFn = "" | "cast" | "convert" | "convertExpr" | "call" | "values" | "colDefs" | "insertCols" | "execCall" | "sub";
/**
 * `cases`: CASE expressions open in this frame; their WHEN / THEN / ELSE / END never change the clause. `charset`: after
 * `CONVERT(expr USING`, where a character set name comes (no table, column or keyword).
 */
interface WFrame { clause: Clause; fn: FrameFn; open: number; execKw: number; cases?: number; charset?: boolean }

class Analyzer {
  readonly pm: Int32Array;
  readonly ctes: { name: string; columns?: string[]; quotedColumns?: string[]; quoted?: boolean }[] = [];
  /** The token index of each CTE's name in its definition, in `ctes` order. */
  readonly cteAt: number[] = [];
  /** CTE name (lower case) → the "(" of its body, for a body whose select list is `*`. */
  private readonly cteStar = new Map<string, number>();
  readonly refs: Ref[] = [];
  /** Every named reference parseRef read, in order; a PIVOT's source stays here after it leaves `refs`. */
  readonly named: Ref[] = [];
  insert?: InsertInfo;
  mergeTarget?: Ref;
  private skipUntil = -1;
  /** While collectRefs walks: each enclosing level's "(" and its set-operation branch (root first). */
  private scopes: { open: number; branch: number }[] = [];
  /**
   * The "(" of each query level that sees nothing outside it: a derived table of a FROM list that is not LATERAL (nor a
   * T-SQL / Oracle APPLY), and a CTE body. `JOIN (SELECT | FROM customers) d` offered the outer query's `o.*`, which
   * no engine accepts there (completion audit 2026-11-25, M1, found in tier 1 on every engine).
   */
  private readonly sealed = new Set<number>();
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

  /**
   * Column names projected by the SELECT at `s` up to (exclusive) `e`; undefined if unknown (`*`). The ones written
   * quoted are added to `quoted` (TableInScope.quotedColumns).
   */
  projection(s: number, e: number, quoted?: string[]): string[] | undefined {
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
      // A clause word ends the list, except as a label right after AS (`SELECT 1 AS order FROM …`).
      if (t.kind === "keyword" && PROJECTION_END.has(t.upper) && W(T[i - 1]) !== "AS") break;
      if (isPunct(t, ",")) { items.push([itemStart, i]); itemStart = i + 1; }
    }
    items.push([itemStart, i]);
    const cols: string[] = [];
    for (const [x, y] of items) {
      if (this.skip >= x && this.skip < y) continue;
      this.itemQuoted = false;
      const nm = this.itemName(x, y);
      if (nm === "*") return undefined;
      if (nm) { cols.push(nm); if (this.itemQuoted) quoted?.push(nm); }
    }
    return cols;
  }

  /** Set by itemName: the name it returned was a quoted identifier token. */
  private itemQuoted = false;

  private itemName(x: number, y: number): string | null {
    const T = this.T;
    if (y <= x) return null;
    const last = T[y - 1];
    this.itemQuoted = last.kind === "qident";
    if (isOp(last, "*") && (y - 1 === x || isPunct(T[y - 2], "."))) return "*";
    // After AS any word is a label (PostgreSQL: `SELECT 1 AS order`); a string is MySQL's and SQLite's `AS 'x'`.
    if (y - x >= 2 && W(T[y - 2]) === "AS" && (isNameTok(last) || last.kind === "string" || last.kind === "keyword")) {
      if (last.kind === "string") return this.text.slice(last.start + 1, last.end - 1);
      return last.kind === "keyword" ? this.text.slice(last.start, last.end) : last.value;
    }
    if (!this.pg && y - x >= 3 && isNameTok(T[x]) && isOp(T[x + 1], "=")) { this.itemQuoted = T[x].kind === "qident"; return T[x].value; }
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

  /**
   * Aliases the select list of the cursor's query block defines (`expr AS a`, `expr a`): ORDER BY may name them, and
   * GROUP BY too on PostgreSQL, MySQL, SQLite and DuckDB.
   */
  selectAliases(open: number, cursor: number): { name: string; quoted: boolean }[] {
    const T = this.T;
    let s = -1;
    for (let i = Math.max(this.a, open + 1); i <= cursor; i++) {
      if (isPunct(T[i], "(")) { const end = this.match(i); if (end > i) { i = end; continue; } }
      if (W(T[i]) === "SELECT") s = i;
    }
    if (s < 0) return [];
    // Whether each was delimited, kept with it: `AS "Total"` is Total exactly, `AS Total` folds (code audit R3-1).
    const out: { name: string; quoted: boolean }[] = [];
    let itemStart = s + 1;
    const flush = (y: number) => {
      const x = itemStart;
      if (y - x < 2) return;
      const last = T[y - 1];
      const p = T[y - 2];
      // After AS a keyword is a label too (PostgreSQL `SELECT total AS order`): written as it is (M21).
      if (W(p) === "AS" && last.kind === "keyword") {
        const label = this.text.slice(last.start, last.end);
        if (label && !out.some((a) => a.name === label)) out.push({ name: label, quoted: false });
        return;
      }
      if (!isNameTok(last)) return;
      // `expr AS a` or `expr a` (a name right after an expression, not after a dot or an operator).
      if (W(p) === "AS" || (!isPunct(p, ".") && p.kind !== "operator" && !isPunct(p, ",") && y - x >= 2 && !(y - x === 2 && W(T[x]) === "DISTINCT"))) {
        if (!out.some((a) => a.name === last.value)) out.push({ name: last.value, quoted: last.kind === "qident" });
      }
    };
    for (let i = s + 1; i <= this.b && i < T.length; i++) {
      const t = T[i];
      if (isPunct(t, "(")) { const m = this.match(i); if (m < 0) break; i = m; continue; }
      // A clause word right after AS is a label, not the select list's end (`SELECT 1 AS order, b`, M21).
      if (t.kind === "keyword" && PROJECTION_END.has(t.upper) && W(T[i - 1]) !== "AS") { flush(i); return out; }
      if (isPunct(t, ",")) { flush(i); itemStart = i + 1; }
    }
    flush(Math.min(this.b + 1, T.length));
    return out;
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
      if (t.kind === "keyword" && PROJECTION_END.has(t.upper) && W(T[i - 1]) !== "AS") break;
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
        (end + 2 === y && W(T[end]) === "AS" && (isNameTok(T[end + 1]) || T[end + 1].kind === "keyword"));
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
      const quoted = nameTok!.kind === "qident";
      this.cteAt.push(i); // every path below records this CTE in `ctes`
      i++;
      let cols: string[] | undefined;
      const qcols: string[] = [];
      if (isPunct(T[i], "(")) {
        const m = this.match(i);
        cols = [];
        const e = m < 0 ? this.b + 1 : m;
        for (let x = i + 1; x < e; x++) if (isNameTok(T[x]) && x !== this.skip) { cols.push(T[x].value); if (T[x].kind === "qident") qcols.push(T[x].value); }
        i = e + 1;
      }
      const push = () => this.ctes.push({ name, columns: cols, ...(qcols.length ? { quotedColumns: qcols } : {}), ...(quoted ? { quoted } : {}) });
      if (W(T[i]) !== "AS") { push(); return; }
      i++;
      if (W(T[i]) === "NOT") i++;
      if (W(T[i]) === "MATERIALIZED") i++;
      if (!isPunct(T[i], "(")) { push(); return; }
      this.sealed.add(i); // a CTE body never sees the statement it belongs to
      const m = this.match(i);
      const e = m < 0 ? this.b + 1 : m;
      if (!cols) {
        for (let x = i + 1; x < e; x++) {
          if (isPunct(T[x], "(")) { const mm = this.match(x); if (mm < 0) break; x = mm; continue; }
          if (W(T[x]) === "SELECT") {
            cols = this.projection(x, e, qcols);
            if (!cols) this.cteStar.set(name.toLowerCase(), i);
            break;
          }
        }
      }
      push();
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

  /** INSERT / REPLACE at token `i`: its target and column list. */
  private collectInsert(i: number): void {
    const T = this.T;
    let j = this.skipTop(i + 1);
    // SQLite INSERT OR REPLACE / IGNORE / … INTO; MySQL INSERT IGNORE / LOW_PRIORITY / … INTO.
    if (W(T[j]) === "OR") j += 2;
    while (INSERT_MODIFIERS.has(W(T[j]))) j++;
    if (W(T[j]) === "INTO") j++;
    const ref = this.parseRef(j, "insert", true);
    const targetEnd = ref ? ref.endIdx : -1;
    const colsOpen = ref && isPunct(T[targetEnd + 1], "(") && W(T[targetEnd + 2]) !== "SELECT" ? targetEnd + 1 : -1;
    this.setInsert(i, ref ? { name: ref.name, schema: ref.schema } : undefined, targetEnd, colsOpen);
  }

  private parseAlias(n: number): { alias?: string; aliasQuoted?: boolean; cols?: string[]; qcols?: string[]; next: number } {
    const T = this.T;
    let x = n;
    let viaAs = false;
    if (W(this.tok(x)) === "AS") { x++; viaAs = true; }
    const t = this.tok(x);
    if (!t || x > this.b || !(isNameTok(t) || (viaAs && t.kind === "keyword"))) return { next: n };
    if ((!viaAs && neverAlias(t, this.dialect)) || clauseBefore(T, x, this.dialect)) return { next: n };
    const alias = t.value;
    const aliasQuoted = t.kind === "qident";
    x++;
    let cols: string[] | undefined;
    let qcols: string[] | undefined;
    if (isPunct(T[x], "(") && x <= this.b) {
      const m = this.match(x);
      const e = m < 0 ? this.b + 1 : m;
      cols = [];
      for (let y = x + 1; y < e; y++) {
        if (!isNameTok(T[y]) || y === this.skip) continue;
        cols.push(T[y].value);
        if (T[y].kind === "qident") (qcols ??= []).push(T[y].value);
      }
      x = e + 1;
    }
    return { alias, ...(aliasQuoted ? { aliasQuoted } : {}), cols, ...(qcols ? { qcols } : {}), next: x };
  }

  /** One value of a temporal clause (`'2024-01-01'`, `@d`, `SYSUTCDATETIME()`): the index after it. */
  private skipValue(i: number): number {
    if (isPunct(this.T[i + 1], "(")) { const m = this.match(i + 1); return m < 0 ? i + 2 : m + 1; }
    return i + 1;
  }

  /**
   * T-SQL `FOR SYSTEM_TIME AS OF v | FROM v TO v | BETWEEN v AND v | CONTAINED IN (v, v) | ALL` after a temporal table,
   * starting after SYSTEM_TIME: the index after the clause, where the alias is.
   */
  private skipSystemTime(i: number): number {
    const T = this.T;
    switch (W(T[i])) {
      case "ALL": return i + 1;
      case "AS": return W(T[i + 1]) === "OF" ? this.skipValue(i + 2) : i;
      case "FROM": case "BETWEEN": {
        const a = this.skipValue(i + 1);
        return W(T[a]) === "TO" || W(T[a]) === "AND" ? this.skipValue(a + 1) : a;
      }
      case "CONTAINED": {
        if (W(T[i + 1]) !== "IN" || !isPunct(T[i + 2], "(")) return i;
        const m = this.match(i + 2);
        return m < 0 ? i + 3 : m + 1;
      }
      default: return i;
    }
  }

  /**
   * T-SQL `src PIVOT (agg(v) FOR c IN ([a], [b])) AS p` / `src UNPIVOT (v FOR c IN (a, b)) AS u` at token `i`: the result
   * replaces its source in scope, with the source's known columns less the pivoted ones, plus the new ones.
   */
  private parsePivot(i: number): void {
    const T = this.T;
    const open = i + 1;
    if (!isPunct(T[open], "(")) return;
    const close = this.match(open);
    if (close < 0) return;
    const unpivot = W(T[i]) === "UNPIVOT";
    let forAt = -1;
    for (let x = open + 1; x < close; x++) {
      if (isPunct(T[x], "(")) { const m = this.match(x); if (m > x) x = m; continue; }
      if (W(T[x]) === "FOR") { forAt = x; break; }
    }
    if (forAt < 0 || !isNameTok(T[forAt + 1]) || W(T[forAt + 2]) !== "IN" || !isPunct(T[forAt + 3], "(")) return;
    const inClose = this.match(forAt + 3);
    const inList: string[] = [];
    for (let x = forAt + 4; x < (inClose < 0 ? close : inClose); x++) if (isNameTok(T[x])) inList.push(T[x].value);
    const forCol = T[forAt + 1].value;
    // PIVOT: the aggregate's argument columns; UNPIVOT: the value column.
    const used: string[] = [];
    for (let x = open + 1; x < forAt; x++) if (isNameTok(T[x]) && !isPunct(T[x + 1], "(")) used.push(T[x].value);
    const al = this.parseAlias(close + 1);
    if (!al.alias) return;
    const scope = this.scopes[this.scopes.length - 1];
    let src: Ref | undefined;
    for (let r = this.refs.length - 1; r >= 0; r--) {
      const x = this.refs[r];
      if (x.scopeOpen === scope?.open && x.scopeBranch === scope?.branch && x.endIdx < i) { src = x; break; }
    }
    const known = src?.columns ?? [];
    const drop = new Set((unpivot ? inList : [...used, forCol]).map((c) => c.toLowerCase()));
    const columns = [...known.filter((c) => !drop.has(c.toLowerCase())), ...(unpivot ? [...used, forCol] : inList)];
    if (src) this.refs.splice(this.refs.indexOf(src), 1);
    const ref: Ref = { name: al.alias, alias: al.alias, isCte: false, isDerived: true, columns, offset: T[i].start, endIdx: al.next - 1 };
    if (al.aliasQuoted) { ref.aliasQuoted = true; ref.quoted = true; }
    this.tagScope(ref);
    this.refs.push(ref);
    this.skipUntil = Math.max(this.skipUntil, ref.endIdx);
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
      const inner = W(T[j + 1]);
      // A derived table's query sees only its own sources unless LATERAL (or APPLY) lets it see the items before it;
      // sealed also while its ")" or alias is not typed yet. DuckDB makes LATERAL optional (it detects the reference).
      const lateral = this.dialect === "duckDb" || W(T[j - 1]) === "LATERAL" || W(T[j - 1]) === "APPLY";
      if (!lateral && (inner === "SELECT" || inner === "WITH" || inner === "VALUES")) this.sealed.add(j);
      const m = this.match(j);
      if (m < 0) return undefined;
      let cols: string[] | undefined;
      const qcols: string[] = [];
      if (inner === "SELECT") cols = this.projection(j + 1, m, qcols);
      else if (inner === "WITH") {
        for (let x = j + 2; x < m; x++) {
          if (isPunct(T[x], "(")) { const mm = this.match(x); if (mm < 0) break; x = mm; continue; }
          if (W(T[x]) === "SELECT") { cols = this.projection(x, m, qcols); break; }
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
      const quotedCols = al.cols ? al.qcols : qcols;
      if (quotedCols?.length) ref.quotedColumns = quotedCols;
      if (al.aliasQuoted) { ref.aliasQuoted = true; ref.quoted = true; }
      if (inner === "SELECT" && !cols && !al.cols) ref.starOpen = j;
      ref.derivedOpen = j;
      ref.derivedClose = m;
      this.tagScope(ref);
      this.refs.push(ref);
      return ref;
    }
    // dotted name chain: [db.][schema.]name, allowing empty parts (db..t); temp #t and table variables @t
    const parts: string[] = [];
    const quoted: boolean[] = [];
    const partAt: number[] = [];
    let x = j;
    let last = -1;
    for (;;) {
      if (x === this.skip || x > this.b) return undefined;
      const t = T[x];
      if (isNameTok(t) || (t.kind === "variable" && parts.length === 0)) {
        parts.push(t.value);
        quoted.push(t.kind === "qident");
        partAt.push(x);
        last = x;
        x++;
        if (isPunct(T[x], ".") && x <= this.b) { x++; if (isPunct(T[x], ".")) { parts.push(""); quoted.push(false); x++; } continue; }
        break;
      }
      return undefined; // not a name, or the chain ends with '.'
    }
    let n = last + 1;
    let call = false;
    if (!noCall && isPunct(T[n], "(") && n <= this.b) {
      const m = this.match(n);
      if (m < 0) return undefined;
      n = m + 1;
      call = true;
    }
    if (!this.pg && W(T[n]) === "WITH" && isPunct(T[n + 1], "(")) { const m = this.match(n + 1); n = m < 0 ? n + 2 : m + 1; }
    if (!this.pg && W(T[n]) === "FOR" && W(T[n + 1]) === "SYSTEM_TIME") n = this.skipSystemTime(n + 2);
    const al = noCall && role === "insert" ? { next: n } as { alias?: string; aliasQuoted?: boolean; cols?: string[]; qcols?: string[]; next: number } : this.parseAlias(n);
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
    if (al.cols) { ref.columns = al.cols; if (al.qcols) ref.quotedColumns = al.qcols; }
    if (al.aliasQuoted) ref.aliasQuoted = true;
    if (quoted[parts.length - 1]) ref.quoted = true;
    if (parts.length >= 2 && quoted[parts.length - 2]) ref.schemaQuoted = true;
    ref.partAt = partAt;
    if (call) ref.call = true;
    this.tagScope(ref);
    this.refs.push(ref);
    this.named.push(ref);
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
    this.scopes = [{ open: this.a - 1, branch: 0 }];
    for (let i = this.a; i <= this.b; i++) {
      if (i === this.skip) continue;
      const t = T[i];
      if (t.kind === "punct") {
        if (t.value === "(") {
          const prev = T[i - 1];
          frames.push(i > this.a && !this.opensQuery(i) && (isNameTok(prev) || FUNC_KEYWORDS.has(W(prev))) ? "call" : "other");
          this.scopes.push({ open: i, branch: 0 });
        } else if (t.value === ")") {
          if (frames.length > 1) frames.pop();
          if (this.scopes.length > 1) this.scopes.pop();
        }
        else if (t.value === "," && frames[frames.length - 1] === "from") this.parseRefWithHints(i + 1);
        continue;
      }
      const u = W(t);
      if (!u) continue;
      const top = frames.length - 1;
      if (SET_OPERATORS.has(u)) this.scopes[this.scopes.length - 1].branch++;
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
        case "PIVOT": case "UNPIVOT":
          if (!this.pg && frames[top] === "from") this.parsePivot(i);
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
        case "REPLACE":
          // MySQL / SQLite `REPLACE INTO t (…)`: an insert. `REPLACE(…)` is the string function.
          if (W(T[i + 1]) === "INTO" && !CONTINUATION_PREV.has(W(T[i - 1])) && W(T[i - 1]) !== "OR") this.collectInsert(i);
          break;
        case "INSERT": {
          if (this.dialect === "mySql" && isPunct(T[i + 1], "(") && !this.mergeTarget) break; // INSERT(str, …): the string function
          if (this.mergeTarget) {
            const colsOpen = isPunct(T[i + 1], "(") ? i + 1 : -1;
            this.setInsert(i, { name: this.mergeTarget.name, schema: this.mergeTarget.schema }, i, colsOpen);
            break;
          }
          this.collectInsert(i);
          break;
        }
        case "INTO": {
          const pu = W(T[i - 1]);
          if (pu === "INSERT" || pu === "MERGE" || pu === "REPLACE" || INSERT_MODIFIERS.has(pu) || W(T[i - 2]) === "OR") break;
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
        if (c) { r.isCte = true; if (!r.columns) { r.columns = c.columns; if (c.quotedColumns) r.quotedColumns = c.quotedColumns; } }
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

  private tagScope(ref: Ref): void {
    const s = this.scopes[this.scopes.length - 1];
    ref.scopeOpen = s ? s.open : this.a - 1;
    ref.scopeBranch = s ? s.branch : 0;
  }

  /**
   * The query levels the cursor (after token `k`) can see, outermost first: its own and every enclosing one, each in the
   * set-operation branch the cursor is in. A subquery in an expression sees the outer query's tables (correlation); a
   * sealed level (a derived table that is not LATERAL, a CTE body) does not see the query whose FROM (or WITH) holds it
   * — nor a parenthesized join around it — but still the queries around that one: `EXISTS (SELECT 1 FROM (SELECT … c.|)
   * d)` names the outer `c` (second audit review: every level outside was dropped). The outer query never sees a
   * subquery's, a CTE body's or another UNION branch's.
   */
  private visibleScopes(k: number): { open: number; branch: number }[] {
    const T = this.T;
    const stack: { open: number; branch: number }[] = [{ open: this.a - 1, branch: 0 }];
    for (let i = this.a; i <= Math.min(k, this.b); i++) {
      if (i === this.skip) continue;
      const t = T[i];
      if (isPunct(t, "(")) stack.push({ open: i, branch: 0 });
      else if (isPunct(t, ")")) { if (stack.length > 1) stack.pop(); }
      else if (SET_OPERATORS.has(W(t))) stack[stack.length - 1].branch++;
    }
    const out: { open: number; branch: number }[] = [];
    let hide = false;
    for (let s = stack.length - 1; s >= 0; s--) {
      const level = stack[s];
      if (!hide || s === stack.length - 1) out.push(level);
      // Hidden up to and including the next query level out (its FROM items are the sealed level's siblings).
      else if (s === 0 || this.opensQuery(level.open)) hide = false;
      if (this.sealed.has(level.open)) hide = true;
    }
    return out.reverse();
  }

  /**
   * Tables the cursor (after token `k`) can refer to. `exprClause`: the cursor is in an expression, where an INSERT /
   * SELECT INTO target is not a source (`INSERT INTO t (…) SELECT | FROM s` reads s).
   */
  tables(k = this.b, exprClause = false): TableInScope[] {
    const levels = this.visibleScopes(k);
    const visible = new Map(levels.map((s) => [s.open, s.branch]));
    const depth = new Map(levels.map((s, i) => [s.open, i]));
    const refs = this.refs.filter((r) => {
      if (r.scopeOpen !== undefined && visible.get(r.scopeOpen) !== r.scopeBranch) return false;
      // A lateral derived table (LATERAL, APPLY, any on DuckDB) sees what is before it, never itself.
      if (r.derivedOpen !== undefined && r.derivedClose !== undefined && k >= r.derivedOpen && k < r.derivedClose) return false;
      if (exprClause && (r.role === "into" || (r.role === "insert" && this.insert && this.insert.source >= 0 && k >= this.insert.source))) return false;
      return true;
    });
    const aliasSet = new Set<string>();
    for (const r of refs) if (r.alias) aliasSet.add(r.alias.toLowerCase());
    const out: TableInScope[] = [];
    const seen = new Set<string>();
    for (const r of refs) {
      if ((r.role === "update" || r.role === "delete") && r.schema === undefined && !r.alias && aliasSet.has(r.name.toLowerCase())) continue;
      const key = `${r.database ?? ""}.${r.schema ?? ""}.${r.name}|${r.alias ?? ""}`.toLowerCase() + (r.quoted ? `|"${r.name}` : "");
      if (seen.has(key)) continue;
      seen.add(key);
      const t: TableInScope = { name: r.name, isCte: r.isCte, isDerived: r.isDerived, offset: r.offset };
      if (r.scopeOpen !== undefined) t.depth = depth.get(r.scopeOpen) ?? 0;
      if (r.schema !== undefined) t.schema = r.schema;
      if (r.database !== undefined) t.database = r.database;
      if (r.alias !== undefined) t.alias = r.alias;
      if (r.columns !== undefined) { t.columns = r.columns; if (r.quotedColumns) t.quotedColumns = r.quotedColumns; }
      if (r.quoted) t.quoted = true;
      if (r.schemaQuoted) t.schemaQuoted = true;
      if (r.aliasQuoted) t.aliasQuoted = true;
      // `SELECT *` in a derived table or CTE body: its columns are those of the tables that query reads.
      const star = r.starOpen ?? (r.isCte && !r.columns ? this.cteStar.get(r.name.toLowerCase()) : undefined);
      if (star !== undefined) {
        const sources = this.refs.filter((x) => x.scopeOpen === star && x !== r).map((x) => {
          const s: TableInScope = { name: x.name, isCte: x.isCte, isDerived: x.isDerived };
          if (x.schema !== undefined) s.schema = x.schema;
          if (x.database !== undefined) s.database = x.database;
          if (x.columns) { s.columns = x.columns; if (x.quotedColumns) s.quotedColumns = x.quotedColumns; }
          if (x.quoted) s.quoted = true;
          if (x.schemaQuoted) s.schemaQuoted = true;
          return s;
        });
        if (sources.length) t.starSources = sources;
      }
      out.push(t);
    }
    return out;
  }

  /**
   * The table a SET list before `at` writes, among `tables` (tables()): the nearest MERGE or UPDATE target, else the
   * INSERT's (`ON CONFLICT … DO UPDATE SET`, MySQL `ON DUPLICATE KEY UPDATE`, `INSERT … SET`). An UPDATE that names a
   * FROM alias (T-SQL `UPDATE o SET … FROM orders o`) writes that table. Undefined for a MySQL multi-table UPDATE, which
   * may set a column of any of its tables.
   */
  setTarget(tables: TableInScope[], at: number, k = this.b): TableInScope | undefined {
    // A ref the cursor's level cannot see (a sealed CTE body's `UPDATE staging`) is no target, not even through an
    // outer alias of the same name (`FROM customers staging`, M22): the alias fallback is for T-SQL's `UPDATE o … FROM
    // orders o` at the same level.
    const visible = new Map(this.visibleScopes(k).map((s) => [s.open, s.branch]));
    const sees = (ref: Ref) => ref.scopeOpen === undefined || visible.get(ref.scopeOpen) === ref.scopeBranch;
    const resolve = (ref: Ref) => tables.find((t) => t.offset === ref.offset && t.name === ref.name)
      ?? (ref.alias === undefined && ref.schema === undefined && sees(ref) ? tables.find((t) => t.alias?.toLowerCase() === ref.name.toLowerCase()) : undefined);
    // Only an UPDATE / MERGE the cursor's level sees: one inside a CTE body (`WITH u AS (UPDATE …) INSERT … DO UPDATE
    // SET |`) writes another table (second audit review).
    let r: Ref | undefined;
    let target: TableInScope | undefined;
    for (const x of this.refs) {
      if ((x.role === "update" || x.role === "merge") && x.offset < at && (!r || x.offset > r.offset)) {
        const t = resolve(x);
        if (t) { r = x; target = t; }
      }
    }
    if (r && r.role === "update" && this.dialect === "mySql") return undefined;
    if (r) return target;
    if (!this.insert?.target) return undefined;
    // The INSERT the cursor's level sees: one in a CTE body before it (`WITH s AS (INSERT …) INSERT … DO UPDATE SET |`)
    // is not in `tables`, and the first insert ref found was that one (M16).
    for (let i = this.refs.length - 1; i >= 0; i--) {
      const x = this.refs[i];
      if (x.role !== "insert") continue;
      const t = resolve(x);
      if (t) return t;
    }
    return undefined;
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
          else if ((pu === "CONVERT" || pu === "TRY_CONVERT") && this.dialect === "tsql") { f.fn = "convert"; f.clause = "declare"; }
          else if (pu === "CONVERT" && this.dialect === "mySql") { f.fn = "convertExpr"; f.clause = "select"; }
          else if (this.insert && i === this.insert.colsOpen) { f.fn = "insertCols"; f.clause = "insertColumns"; }
          else if (top.clause === "values" && top.fn !== "values" && (pu === "VALUES" || isPunct(prev, ","))) { f.fn = "values"; f.clause = "values"; }
          // `CALL p(…)` (PostgreSQL, MySQL, Oracle, DuckDB) and Oracle's `EXEC p(…)`: the routine's arguments, never
          // routine names (completion audit M19). T-SQL's EXEC takes no parentheses.
          else if (top.clause === "exec" && top.execKw >= 0 && (this.pg || this.dialect !== "tsql")) { f.fn = "execCall"; f.clause = "execArgs"; f.execKw = top.execKw; }
          else if (pu === "TABLE" || (createTable && st.length === 1 && !rootParenSeen)) { f.fn = "colDefs"; f.clause = "unknown"; }
          // ON CONFLICT (…): the insert target's columns (it is in scope, like ON DUPLICATE KEY UPDATE).
          else if (pu === "CONFLICT") { f.fn = "sub"; f.clause = "set"; }
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
          else if (top.fn === "convertExpr") top.clause = "declare";
          // `JOIN t ON a = b, |`: the comma ends the join condition and lists the next table.
          else if (top.clause === "on" && !merge && (top.fn === "" || top.fn === "sub") && st.length === 1) top.clause = "from";
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
        case "USING":
          if (top.fn === "convertExpr") { top.clause = "unknown"; top.charset = true; break; }
          top.clause = isPunct(next, "(") && W(T[i + 2]) !== "SELECT" ? "on" : "from";
          break;
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
        case "INSERT":
          // MySQL's INSERT(str, pos, len, new) is a string function; MERGE's INSERT (cols) stays an insert.
          if (!(this.dialect === "mySql" && isPunct(next, "(") && !merge)) top.clause = "insertInto";
          break;
        case "REPLACE": if (W(next) === "INTO" && W(T[i - 1]) !== "OR") top.clause = "insertInto"; break;
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
    const ctx = scanImpl(src, off, knownDialect(dialect), 0);
    if (inNumberAt(src, off, knownDialect(dialect))) ctx.inNumber = true;
    return ctx;
  } catch {
    return emptyContext(off);
  }
}

/** The cursor is inside or right after a number token (`> 1.|`, `1.5|`), from the cached token stream. */
function inNumberAt(text: string, offset: number, dialect: Dialect): boolean {
  const T = getStream(text, dialect).code;
  const cur = T[lastStartingBefore(T, offset)];
  return cur !== undefined && cur.kind === "number" && offset > cur.start && offset <= cur.end;
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
    return inStringContext(text, T, idx, offset, dialect);
  }
  const ci = lastStartingBefore(stream.comments, offset);
  if (ci >= 0) {
    const c = stream.comments[ci];
    if (offset < c.end || (offset === c.end && (c.line || c.open))) return inStringContext(text, T, idx, offset, dialect);
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

  const loc = locateStatement(T, k, pi, dialect, text);
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
  ctx.tablesInScope = an.tables(k);
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
        else if (isNameTok(t) && isOp(T[x + 1], "=>")) used.push(t.value);
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
    // A clause word the lexer reads as a name (Oracle: JOIN, LEFT, USING are not reserved there) starts the next
    // reference; it never completes one.
    else if ((isNameTok(prev) && !neverAlias(prev, dialect)) || prev.kind === "variable" || isPunct(prev, ")")) slot = "afterReference";
  } else if (qualifier === undefined && slot === undefined && pu === "AS" && frame.fn !== "cast" &&
    (clause === "select" || clause === "where" || clause === "on" || clause === "groupBy" || clause === "orderBy" || clause === "having")) {
    slot = "alias";
  }

  // MySQL `CONVERT(expr USING |)`: a character set's name comes, nothing from the schema.
  if (frame.charset && qualifier === undefined) slot = "name";

  ctx.clause = clause;
  // In an expression an INSERT / SELECT INTO target is not a source of rows.
  if (EXPRESSION_CLAUSES.has(clause)) ctx.tablesInScope = an.tables(k, true);
  // A SET list's target (`SET |`, `SET a = 1, |`, MySQL `ON DUPLICATE KEY UPDATE |`) is a column of the written table,
  // named bare: PostgreSQL, SQLite and DuckDB reject `SET o.status`, and no engine sets another source's column (an
  // UPDATE … FROM's, a MERGE's USING, an INSERT … SELECT's) — completion audit M9. The value after `=` keeps every source.
  if (clause === "set" && qualifier === undefined && slot === undefined && frame.fn === "" && setTargetSlot(T, k)) {
    const target = an.setTarget(ctx.tablesInScope, T[k].start, k);
    if (target) {
      ctx.tablesInScope = [target];
      ctx.setTarget = true;
    }
  }
  if (clause === "groupBy") {
    ctx.nonAggregatedColumns = an.nonAggregatedProjections(frame.open, k);
  }
  if ((clause === "orderBy" || (clause === "groupBy" && GROUP_BY_ALIASES[dialect])) && qualifier === undefined) {
    const aliases = an.selectAliases(frame.open, k);
    if (aliases.length) ctx.selectAliases = aliases;
  }
  if (slot) ctx.slot = slot;
  if (dialect === "tsql") attachTempColumns(ctx.tablesInScope, text, T, k, dialect, stream.marks);
  return ctx;
}

/**
 * Token `k` (the one before the word being typed) opens a SET list's target: SET itself, a `,` between assignments, or
 * MySQL's `ON DUPLICATE KEY UPDATE`. Anything else (`=`, an operator, a name) puts the cursor in a value.
 */
function setTargetSlot(T: Token[], k: number): boolean {
  const t = T[k];
  if (isPunct(t, ",")) return true;
  const u = W(t);
  return u === "SET" || (u === "UPDATE" && W(T[k - 1]) === "KEY");
}

/** Words that start a table constraint or index in a column list, not a column. */
const NOT_A_COLUMN = new Set(["CONSTRAINT", "PRIMARY", "UNIQUE", "INDEX", "CHECK", "FOREIGN", "PERIOD"]);

/** Column names of a `( … )` column-definition list opening at `open`: the first name of each top-level item. */
function columnDefNames(T: Token[], open: number): string[] {
  const out: string[] = [];
  let depth = 0;
  let itemStart = true;
  for (let i = open + 1; i < T.length; i++) {
    const t = T[i];
    if (isPunct(t, "(")) { depth++; itemStart = false; continue; }
    if (isPunct(t, ")")) { if (depth === 0) break; depth--; continue; }
    if (t.kind === "go" || isPunct(t, ";")) break;
    if (depth === 0 && isPunct(t, ",")) { itemStart = true; continue; }
    if (itemStart && depth === 0) {
      if (isNameTok(t) && !NOT_A_COLUMN.has(t.upper)) out.push(t.value);
      itemStart = false;
    }
  }
  return out;
}

/**
 * T-SQL temporary tables and table variables have no catalog entry the snapshot holds: their columns come from the batch
 * above the cursor — `CREATE TABLE #t (…)`, `DECLARE @t TABLE (…)` (found through the DECLARE / CREATE marks), then, for
 * a name still without columns, `SELECT … INTO #t` (a backward walk over the batch, only in that case).
 */
function attachTempColumns(tables: TableInScope[], text: string, T: Token[], k: number, dialect: Dialect, marks: Token[]): void {
  const want = tables.filter((t) => !t.columns?.length && (t.name.startsWith("#") || t.name.startsWith("@")));
  if (!want.length || k < 0) return;
  const defs = new Map<string, string[]>();
  const kStart = T[k].start;
  for (let m = lastStartingBefore(marks, kStart + 1); m >= 0; m--) {
    const t = marks[m];
    if (t.kind === "go") break;
    const j = lastStartingBefore(T, t.start + 1);
    if (j < 0 || T[j] !== t) continue;
    if (t.upper === "CREATE" && W(T[j + 1]) === "TABLE" && isNameTok(T[j + 2]) && isPunct(T[j + 3], "(")) {
      const name = T[j + 2].value.toLowerCase();
      if (!defs.has(name)) defs.set(name, columnDefNames(T, j + 3));
    } else if (t.upper === "DECLARE") {
      // DECLARE @a int, @t TABLE (…), @u AS TABLE (…)
      for (let x = j + 1; x < T.length && x <= k; x++) {
        const v = T[x];
        if (v.kind === "go" || isPunct(v, ";") || (v.kind === "keyword" && v.upper !== "TABLE" && v.upper !== "AS" && x > j + 1 && isPunct(T[x - 1], ")"))) break;
        if (v.kind !== "variable") continue;
        let y = x + 1;
        if (W(T[y]) === "AS") y++;
        if (W(T[y]) === "TABLE" && isPunct(T[y + 1], "(")) {
          const name = v.value.toLowerCase();
          if (!defs.has(name)) defs.set(name, columnDefNames(T, y + 1));
        }
      }
    }
  }
  if (want.some((t) => !defs.has(t.name.toLowerCase()))) {
    // SELECT … INTO #t FROM …: the select list names the columns.
    for (let j = k; j >= 0; j--) {
      const t = T[j];
      if (t.kind === "go") break;
      if (W(t) !== "INTO" || !isNameTok(T[j + 1]) || !T[j + 1].value.startsWith("#")) continue;
      const name = T[j + 1].value.toLowerCase();
      if (defs.has(name)) continue;
      let depth = 0;
      let sel = -1;
      for (let x = j - 1; x >= 0 && j - x < 20000; x--) {
        const u = T[x];
        if (isPunct(u, ")")) depth++;
        else if (isPunct(u, "(")) { if (depth === 0) break; depth--; }
        else if (depth === 0 && W(u) === "SELECT") { sel = x; break; }
        else if (u.kind === "go" || isPunct(u, ";")) break;
      }
      if (sel < 0) continue;
      const cols = new Analyzer(text, T, sel, j, -1, dialect).projection(sel, j);
      if (cols?.length) defs.set(name, cols);
    }
  }
  for (const t of want) {
    const cols = defs.get(t.name.toLowerCase());
    if (cols?.length) t.columns = cols;
  }
}

function inPgDeclareSection(T: Token[], s: number): boolean {
  for (let j = s - 1; j >= 0 && j >= s - 5000; j--) {
    const u = W(T[j]);
    if (u === "BEGIN") return false;
    if (u === "DECLARE") return true;
  }
  return false;
}

function inStringContext(text: string, T: Token[], idx: number, offset: number, dialect: Dialect): CursorContext {
  const ctx = emptyContext(offset);
  ctx.inStringOrComment = true;
  try {
    const loc = locateStatement(T, idx, -1, dialect, text);
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
  // The number test reads the body's tokens: the outer text holds the body as one string (code audit 2026-11-26, M15).
  if (inNumberAt(body, offset - bs, dialect)) inner.inNumber = true;
  if (inner.body) inner.body.base += bs; // a body inside this one: the innermost is the cursor's
  else inner.body = { text: body, base: bs };
  inner.statementStart += bs;
  inner.statementEnd += bs;
  inner.prefixStart += bs;
  for (const t of inner.tablesInScope) if (t.offset !== undefined) t.offset += bs;
  if (!inner.inStringOrComment) {
    const loc = locateStatement(T, idx - 1, -1, dialect, text);
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

// ---------------------------------------------------------------------------------------------
// Semantic spans (App Themes §5.3): CTE names and schema prefixes for the editor's semantic tokens. The same statement
// split and Analyzer as completion, so both agree on what a CTE is; tokens only, never a regex over the text.
// ---------------------------------------------------------------------------------------------

export type SemanticKind = "cte" | "schema";

/** One name to colour: `[start, end)` in the text, on one line, never inside a string or a comment. */
export interface SemanticSpan { start: number; end: number; kind: SemanticKind }

/** Statements one call analyses at most (the statements of PostgreSQL `$$` bodies included). */
export const SEMANTIC_MAX_STATEMENTS = 200;

/** Words between CREATE / ALTER / DROP [OR REPLACE] and the object kind. */
const DDL_MODIFIERS = new Set([
  "TEMP", "TEMPORARY", "GLOBAL", "LOCAL", "UNLOGGED", "UNIQUE", "CLUSTERED", "NONCLUSTERED", "COLUMNSTORE", "MATERIALIZED",
  "RECURSIVE", "FOREIGN", "VIRTUAL", "CONSTRAINT", "FULLTEXT", "SPATIAL", "BITMAP", "PERSISTENT", "EDITIONABLE",
  "NONEDITIONABLE", "EDITIONING", "FORCE", "NOFORCE", "AGGREGATE",
]);
/** Object kinds whose name follows CREATE / ALTER / DROP. */
const DDL_KINDS = new Set([
  "TABLE", "VIEW", "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "INDEX", "SEQUENCE", "TYPE", "SYNONYM", "PACKAGE", "MACRO",
  "DOMAIN", "EVENT",
]);

interface SpanCtx {
  /** The text the tokens index: the document, or a PostgreSQL `$$` body in it. */
  text: string;
  /** Where `text` starts in the document. */
  base: number;
  /** The requested document range. */
  from: number;
  to: number;
  dialect: Dialect;
  schema: SchemaIndex | null;
  out: SemanticSpan[];
  /** Statements this call may still analyse. */
  budget: { left: number };
}

/**
 * The CTE names and schema prefixes of the statements overlapping `[from, to)` (offsets in `text`), sorted, for the
 * semantic-token provider (App Themes §5.3). Never throws.
 * - `cte`: a CTE's name where it is defined (`WITH [RECURSIVE] name …`), an unqualified table reference that resolves to
 *   one, and a qualifier `name.` when `name` is such a reference written without an alias.
 * - `schema`: every non-final part of a dotted name in an object position (FROM, JOIN, APPLY, UPDATE, INSERT INTO,
 *   DELETE, MERGE INTO, USING, SELECT … INTO, DDL headers with an index's or trigger's ON table and REFERENCES,
 *   TRUNCATE, EXEC / EXECUTE / CALL targets).
 * - Only where `schema` (the connection's cached catalog) names the part: `a.b(` calls anywhere (a schema's function, an
 *   Oracle package's, a T-SQL column's method), Oracle two-part EXEC / CALL targets, and PostgreSQL / Oracle `INTO a.b`
 *   (a record field in PL/pgSQL and PL/SQL).
 * - Never: aliases, columns, strings, comments, Oracle `name@link`, a span that crosses a line.
 * PostgreSQL `$$` routine and DO bodies are analysed as scripts of their own. At most SEMANTIC_MAX_STATEMENTS statements.
 */
export function semanticSpans(text: string, dialect: Dialect, from: number, to: number, schema?: SchemaIndex | null): SemanticSpan[] {
  const src = typeof text === "string" ? text : "";
  const n = src.length;
  let lo = Math.max(0, Math.min(Number.isFinite(from) ? from : 0, n));
  let hi = Math.max(0, Math.min(Number.isFinite(to) ? to : n, n));
  if (lo > hi) [lo, hi] = [hi, lo];
  const d = knownDialect(dialect);
  const c: SpanCtx = { text: src, base: 0, from: lo, to: hi, dialect: d, schema: schema ?? null, out: [], budget: { left: SEMANTIC_MAX_STATEMENTS } };
  try {
    scriptSpans(c, getStream(src, d).code, 0);
  } catch (e) {
    // Tolerant: the spans found so far are kept. An analyzer bug shows here (CTE and schema colors stop part way) in a
    // dev build; the shipped app stays quiet. Never the SQL text: it is user data (CLAUDE §3).
    if (import.meta.env.DEV) console.debug("[semantic] spans stopped early:", e instanceof Error ? e.message : String(e));
  }
  const out = c.out.sort((x, y) => x.start - y.start);
  let w = 0;
  for (const s of out) if (w === 0 || s.start >= out[w - 1].end) out[w++] = s;
  out.length = w;
  return out;
}

/** The statements of one script (the document or a `$$` body) that overlap the range. */
function scriptSpans(c: SpanCtx, T: Token[], depth: number): void {
  const n = T.length;
  if (!n) return;
  const lo = c.from - c.base, hi = c.to - c.base;
  const sp = new Splitter(T, c.dialect, c.text);
  let start = -1;
  for (let i = restartPoint(T, Math.max(0, lastStartingBefore(T, lo))); i <= n; i++) {
    const r = i < n ? sp.step(i) : GO;
    if (r === CONT) continue;
    if (start >= 0 && T[i - 1].end > lo) {
      if (c.budget.left <= 0) return;
      c.budget.left--;
      statementSpans(c, T, start, i - 1, depth);
    }
    start = r === START ? i : -1;
    if (i < n && T[i].start >= hi) return;
  }
}

function statementSpans(c: SpanCtx, T: Token[], a: number, b: number, depth: number): void {
  const an = new Analyzer(c.text, T, a, b, -1, c.dialect);
  an.parseCtes();
  an.collectRefs();
  /** Tokens of names in object positions: never a qualifier. */
  const object = new Uint8Array(b - a + 1);
  const take = (i: number) => { if (i >= a && i <= b) object[i - a] = 1; };
  const cteNames = new Set<string>();
  for (const x of an.ctes) cteNames.add(x.name.toLowerCase());
  for (const at of an.cteAt) emit(c, T[at], "cte");
  // A qualifier `name.` is a CTE's only when an unaliased reference to the CTE is the one thing in the statement so named.
  const cteUnaliased = new Set<string>();
  const otherNames = new Set<string>();
  for (const r of an.refs) if (r.alias) otherNames.add(r.alias.toLowerCase());
  const pgOrOracle = PG_RULES[c.dialect] || c.dialect === "oracle";
  for (const r of an.named) {
    const at = r.partAt ?? [];
    if (!at.length) continue;
    // `a IS DISTINCT FROM b.c`: an expression, not a FROM clause.
    if (W(T[at[0] - 1]) === "FROM" && W(T[at[0] - 2]) === "DISTINCT") continue;
    for (const p of at) take(p);
    if (r.alias) otherNames.add(r.alias.toLowerCase());
    const last = at[at.length - 1];
    if (dbLink(c, T, last + 1)) continue;
    if (at.length === 1) {
      const t = T[last];
      // `FROM f(1)` calls a function: a CTE takes no arguments, so a CTE named f does not make this one.
      if (isNameTok(t) && !r.call && cteNames.has(t.value.toLowerCase())) {
        emit(c, t, "cte");
        if (!r.alias) cteUnaliased.add(t.value.toLowerCase());
      } else if (!r.alias) otherNames.add(r.name.toLowerCase());
      continue;
    }
    if (!r.alias) otherNames.add(r.name.toLowerCase());
    const confirm = r.call || (r.role === "into" && pgOrOracle);
    for (let p = 0; p < at.length - 1; p++) if (!confirm || catalogHas(c, T[at[p]])) emit(c, T[at[p]], "schema");
  }
  const head = W(T[a]);
  if (head === "CREATE" || head === "ALTER" || head === "DROP") ddlSpans(c, T, a, b, head, take);
  else if (head === "TRUNCATE") {
    let j = a + 1;
    if (W(T[j]) === "TABLE") j++;
    if (W(T[j]) === "ONLY") j++;
    j = objectChain(c, T, j, b, take, true);
    while (j < b && isPunct(T[j], ",")) j = objectChain(c, T, j + 1, b, take, true);
  }
  for (let i = a; i <= b; i++) {
    if (object[i - a]) continue;
    const t = T[i];
    if (t.kind === "string") {
      if (t.tag && depth < 3 && DIALECTS[c.dialect].dollarQuotedBodies && i > 0 && (W(T[i - 1]) === "AS" || W(T[i - 1]) === "DO")) bodySpans(c, t, depth);
      continue;
    }
    const u = W(t);
    if (u === "EXEC" || u === "EXECUTE" || u === "CALL") { execSpans(c, T, i, a, b, take); continue; }
    if (!isNameTok(t) || i >= b || !isPunct(T[i + 1], ".")) continue;
    // A part after a dot is not a chain's start; a dot before this statement's first token is another statement's.
    if (i > a && (isPunct(T[i - 1], ".") || isOp(T[i - 1], "::"))) continue;
    const ch = nameChain(T, i, b)!;
    if (dbLink(c, T, ch.next)) {
      // `hr.pkg@link(1)`: names on another database (Oracle) are never colored, as in an object position.
    } else if (ch.parts.length >= 2 && ch.next <= b && isPunct(T[ch.next], "(")) {
      // `a.b(`: a schema's function, a package's, or a column's method — only the catalog can tell.
      for (let p = 0; p < ch.parts.length - 1; p++) if (catalogHas(c, T[ch.parts[p]])) emit(c, T[ch.parts[p]], "schema");
    } else {
      const key = t.value.toLowerCase();
      if (cteUnaliased.has(key) && !otherNames.has(key)) emit(c, t, "cte");
    }
    i = ch.parts[ch.parts.length - 1];
  }
}

/** `CREATE | ALTER | DROP [OR REPLACE] [modifiers] <kind> [IF [NOT] EXISTS] a.b`, an index's or trigger's ON table, REFERENCES. */
function ddlSpans(c: SpanCtx, T: Token[], a: number, b: number, head: string, take: (i: number) => void): void {
  let j = a + 1;
  if (W(T[j]) === "OR") j += 2;
  for (let guard = 0; guard < 8 && j <= b; guard++) {
    const w = W(T[j]);
    if (DDL_MODIFIERS.has(w)) { j++; continue; }
    // MySQL: ALGORITHM = x, DEFINER = user, SQL SECURITY x, before VIEW, PROCEDURE, FUNCTION, TRIGGER or EVENT.
    if ((w === "ALGORITHM" || w === "DEFINER") && isOp(T[j + 1], "=")) {
      j += 2;
      while (j <= b && j < a + 16 && !DDL_KINDS.has(W(T[j]))) j++;
      continue;
    }
    if (w === "SQL" && W(T[j + 1]) === "SECURITY") { j += 3; continue; }
    break;
  }
  const kind = W(T[j]);
  if (DDL_KINDS.has(kind)) {
    j++;
    if ((kind === "PACKAGE" || kind === "TYPE") && W(T[j]) === "BODY") j++;
    if (kind === "INDEX" && W(T[j]) === "CONCURRENTLY") j++;
    let ok = true;
    if (W(T[j]) === "IF") {
      j++;
      if (W(T[j]) === "NOT") j++;
      ok = W(T[j]) === "EXISTS";
      j++;
    }
    if (ok) {
      if (W(T[j]) === "ONLY") j++;
      // T-SQL `DROP INDEX t.ix` (the old form) names the index's table, not a schema.
      const mark = !(c.dialect === "tsql" && kind === "INDEX" && head === "DROP");
      j = objectChain(c, T, j, b, take, mark);
      if (head === "DROP") while (j < b && isPunct(T[j], ",")) j = objectChain(c, T, j + 1, b, take, mark);
      if (kind === "INDEX" || kind === "TRIGGER") {
        let depth = 0;
        for (let x = j; x <= b; x++) {
          if (isPunct(T[x], "(")) depth++;
          else if (isPunct(T[x], ")")) depth--;
          else if (depth === 0 && W(T[x]) === "ON") {
            objectChain(c, T, W(T[x + 1]) === "ONLY" ? x + 2 : x + 1, b, take, true);
            break;
          }
        }
      }
    }
  }
  for (let x = a + 1; x < b; x++) if (W(T[x]) === "REFERENCES") objectChain(c, T, x + 1, b, take, true);
}

/** EXEC / EXECUTE / CALL at `i`: its target's schema (Oracle: the schema of `s.pkg.p`, or of `x.p` when the catalog has x). */
function execSpans(c: SpanCtx, T: Token[], i: number, a: number, b: number, take: (i: number) => void): void {
  // `x.exec`: a name, not the statement. Only within this statement: `SELECT a.` left unfinished before `EXEC dbo.p`
  // ends at the dot, and the EXEC that follows is a statement of its own.
  if (i > a && isPunct(T[i - 1], ".")) return;
  let j = i + 1;
  const next = T[j];
  const nu = W(next);
  if (W(T[i]) === "EXECUTE" && (nu === "FUNCTION" || nu === "PROCEDURE")) j++; // PostgreSQL triggers: EXECUTE FUNCTION s.f()
  else {
    if (i > a && CONTINUATION_PREV.has(W(T[i - 1]))) return; // GRANT EXECUTE, WITH EXECUTE AS
    if (nu === "AS") return; // EXECUTE AS USER
    if (!PG_RULES[c.dialect] && isPunct(next, "(")) return; // EXEC ('dynamic SQL')
    if (next?.kind === "variable" && isOp(T[j + 1], "=")) j += 2; // EXEC @rc = dbo.p
  }
  const ch = nameChain(T, j, b);
  if (!ch) return;
  for (const p of ch.parts) take(p);
  if (ch.parts.length < 2 || dbLink(c, T, ch.next)) return;
  if (c.dialect === "oracle") {
    if (ch.parts.length >= 3 || catalogHas(c, T[ch.parts[0]])) emit(c, T[ch.parts[0]], "schema");
    return;
  }
  for (let p = 0; p < ch.parts.length - 1; p++) emit(c, T[ch.parts[p]], "schema");
}

/** A dotted name at `j` in an object position: its parts are names, never qualifiers. Returns the index after it. */
function objectChain(c: SpanCtx, T: Token[], j: number, b: number, take: (i: number) => void, mark: boolean): number {
  const ch = nameChain(T, j, b);
  if (!ch) return j;
  for (const p of ch.parts) take(p);
  if (mark && !dbLink(c, T, ch.next)) for (let p = 0; p < ch.parts.length - 1; p++) emit(c, T[ch.parts[p]], "schema");
  return ch.next;
}

/** `a.b.c` / `db..t` at `j` (a name first, then names or keywords): the token index of each written part, and the index after. */
function nameChain(T: Token[], j: number, b: number): { parts: number[]; next: number } | null {
  if (j > b || !isNameTok(T[j])) return null;
  const parts = [j];
  let x = j + 1;
  while (x < b && isPunct(T[x], ".")) {
    const nx = T[x + 1];
    if (isNameTok(nx) || nx.kind === "keyword") { parts.push(x + 1); x += 2; }
    else if (isPunct(nx, ".")) x++;
    else break;
  }
  return { parts, next: x };
}

/** Oracle `name@link`: a remote object, never coloured. */
function dbLink(c: SpanCtx, T: Token[], x: number): boolean {
  return c.dialect === "oracle" && isOp(T[x], "@");
}

const catalogNames = new WeakMap<SchemaIndex, { exact: Set<string>; lower: Set<string> }>();

/** The cached catalog has a schema or database of this name (spelled as the server folds it, where it folds names). */
function catalogHas(c: SpanCtx, t: Token): boolean {
  const idx = c.schema;
  if (!idx || !isNameTok(t)) return false;
  let m = catalogNames.get(idx);
  if (!m) {
    m = { exact: new Set(), lower: new Set() };
    for (const s of [...(idx.schemas ?? []), ...(idx.databases ?? [])]) {
      if (typeof s !== "string") continue;
      m.exact.add(s);
      m.lower.add(s.toLowerCase());
    }
    catalogNames.set(idx, m);
  }
  return DIALECTS[c.dialect].identifierFold
    ? m.exact.has(foldIdentifier(t.value, t.kind === "qident", c.dialect))
    : m.lower.has(t.value.toLowerCase());
}

/**
 * A body's code tokens, kept per body token: a token object survives incremental re-lexing only while its text is
 * unchanged (lexer.ts), so a body that was not edited is not lexed again.
 */
const bodyTokens = new WeakMap<Token, { dialect: Dialect; length: number; code: Token[] }>();

/** A PostgreSQL `AS $$ … $$` / `DO $$ … $$` body: analysed as a script of its own (as scanBody does for completion). */
function bodySpans(c: SpanCtx, t: Token, depth: number): void {
  const bs = t.start + (t.tag ?? 0);
  const be = t.open ? t.end : t.end - (t.tag ?? 0);
  if (be <= bs || c.base + be <= c.from || c.base + bs >= c.to) return;
  const body = c.text.slice(bs, be);
  let memo = bodyTokens.get(t);
  if (!memo || memo.dialect !== c.dialect || memo.length !== t.end - t.start) {
    memo = { dialect: c.dialect, length: t.end - t.start, code: tokenize(body, c.dialect).filter((x) => x.kind !== "comment") };
    bodyTokens.set(t, memo);
  }
  scriptSpans({ ...c, text: body, base: c.base + bs }, memo.code, depth + 1);
}

function emit(c: SpanCtx, t: Token, kind: SemanticKind): void {
  if (t.kind !== "ident" && t.kind !== "qident") return;
  const s = c.base + t.start, e = c.base + t.end;
  if (e <= c.from || s >= c.to || e <= s) return;
  // Monaco's semantic tokens cannot cross a line.
  for (let x = t.start; x < t.end; x++) {
    const ch = c.text.charCodeAt(x);
    if (ch === 10 || ch === 13) return;
  }
  c.out.push({ start: s, end: e, kind });
}

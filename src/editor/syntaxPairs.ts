import { sqlRegionAtOffset } from "./completion/sqlRegion";
import type { Dialect } from "../ipc/contract";
import { getStream, type Token, type TokenStream } from "./completion/lexer";
import { isPunct, lastStartingBefore, W } from "./completion/scanner";
import { TABLE_HINTS } from "./completion/keywords";

/**
 * Block grammar per dialect: PL/pgSQL's (`IF … ELSE … END IF`, and `BEGIN;` is a transaction command) or T-SQL's
 * (`IF` has no END IF: an `IF` pairs with its `ELSE`). MySQL's compound statements read like PL/pgSQL (END IF,
 * END LOOP, `BEGIN;` starts a transaction); SQLite has no IF statement, and its `BEGIN;` is a transaction too.
 */
const PLPGSQL_BLOCKS: Record<Dialect, boolean> = { tsql: false, pg: true, sqlite: true, mySql: true, duckDb: true, oracle: true };

export interface Span {
  start: number;
  end: number;
}

export interface InsertMatchResult {
  col: Span;
  /** Primary matched value span (the active value when cursor is on a value, or the first row's value when cursor is on a column). */
  val: Span;
  /** All matched value spans (1 item when cursor is on a value; every matched row/branch when cursor is on a column, capped). */
  vals: Span[];
}

const MAX_HIGHLIGHT_VALS = 500;

/** Previous words that mean INSERT / REPLACE is a DDL/DCL/trigger/clause keyword, not a statement start. */
const NON_INSERT_PREV = new Set([
  "BEFORE", "AFTER", "OF", "FOR", "ON", "GRANT", "REVOKE", "DENY", "OR", "AND", "EXEC", "EXECUTE", "CALL",
]);

/** MySQL modifiers that may appear between INSERT/REPLACE and INTO/table. */
const MYSQL_INSERT_MODIFIERS = new Set(["LOW_PRIORITY", "DELAYED", "HIGH_PRIORITY", "IGNORE"]);

/** T-SQL rowset functions usable as an INSERT target (`INSERT INTO OPENROWSET(...) (cols) ...`). */
const TSQL_ROWSET_TARGETS = new Set(["OPENROWSET", "OPENQUERY", "OPENDATASOURCE"]);

/** Words that end a target table clause (never a bare table alias after `INSERT INTO tbl`). */
const POST_TARGET_STOP = new Set([
  "VALUES", "VALUE", "SELECT", "SET", "DEFAULT", "WITH", "OUTPUT", "OVERRIDING", "BY",
  "PARTITION", "SUBPARTITION", "FROM", "RETURNING", "ON", "EXEC", "EXECUTE", "TABLE",
  "INTO", "WHEN", "ELSE", "WHERE", "UNION", "INTERSECT", "EXCEPT", "MINUS",
]);

/** Set operators joining compound query branches. */
const SET_OPS = new Set(["UNION", "INTERSECT", "EXCEPT", "MINUS"]);

/** Clause keywords that unconditionally end a SELECT projection list at depth 0. */
const SELECT_CLAUSE_END = new Set([
  "FROM", "INTO", "WHERE", "HAVING", "LIMIT", "OFFSET", "FETCH", "FOR",
  "UNION", "INTERSECT", "EXCEPT", "MINUS", "RETURNING", "OPTION",
]);

/** Statement verbs that end a semicolon-less SELECT / SET / query branch when met at depth 0. */
const STMT_BREAK_VERBS = new Set([
  "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP", "DECLARE", "SET",
  "EXEC", "EXECUTE", "IF", "ELSE", "ELSIF", "ELSEIF", "WHILE", "BEGIN", "END", "PRINT", "RETURN",
  "THROW", "RAISERROR", "TRUNCATE", "USE", "COMMIT", "ROLLBACK", "REPLACE", "CALL", "EXPLAIN",
  "PRAGMA", "GRANT", "REVOKE", "DENY", "WITH",
]);

/** Expression-continuing keywords: a verb immediately after one is part of the expression, not a new statement. */
const EXPR_CONT_PREV = new Set([
  "AS", "AND", "OR", "NOT", "IS", "IN", "LIKE", "ILIKE", "SIMILAR", "TO", "BETWEEN",
  "WHEN", "THEN", "ELSE", "CASE", "COLLATE", "OVER", "FILTER", "AT", "TIME", "ZONE",
  "ESCAPE", "DISTINCT", "ALL", "ANY", "SOME", "EXISTS", "PRIOR", "CONNECT_BY_ROOT",
]);

/** MySQL-specific SELECT modifiers. */
const MYSQL_SELECT_MODS = new Set([
  "DISTINCTROW", "HIGH_PRIORITY", "STRAIGHT_JOIN",
  "SQL_SMALL_RESULT", "SQL_BIG_RESULT", "SQL_BUFFER_RESULT", "SQL_NO_CACHE", "SQL_CACHE", "SQL_CALC_FOUND_ROWS",
]);

/** Oracle SQL*Plus `/` alone on its line acts as a statement terminator. */
function isOracleSlashLine(text: string, t: Token | undefined): boolean {
  if (!t || t.kind !== "operator" || t.value !== "/") return false;
  for (let p = t.start - 1; p >= 0; p--) {
    const c = text.charCodeAt(p);
    if (c === 10 || c === 13) break;
    if (c !== 32 && c !== 9 && c !== 0xfeff) return false;
  }
  for (let p = t.end; p < text.length; p++) {
    const c = text.charCodeAt(p);
    if (c === 10 || c === 13) return true;
    if (c !== 32 && c !== 9) return false;
  }
  return true;
}

function isStmtTerminator(text: string, t: Token | undefined, dialect: Dialect): boolean {
  if (!t) return true;
  if (t.kind === "go" || isPunct(t, ";")) return true;
  return dialect === "oracle" && isOracleSlashLine(text, t);
}

/** Given `T[openIdx]` is `(`, returns the index of the matching `)` within `[openIdx, limit]`, or -1 if unclosed. */
function skipMatchingParen(T: Token[], openIdx: number, limit: number): number {
  let depth = 0;
  for (let i = openIdx; i <= limit; i++) {
    const t = T[i];
    if (t.kind === "punct") {
      if (t.value === "(") depth++;
      else if (t.value === ")") {
        depth--;
        if (depth === 0) return i;
      }
    }
  }
  return -1;
}

/** True if `T[i]` is a valid INSERT / REPLACE statement start (not a function call, trigger clause, or dotted name). */
function isInsertStart(T: Token[], i: number, dialect: Dialect): boolean {
  const w = W(T[i]);
  const isReplace = w === "REPLACE" && (dialect === "sqlite" || dialect === "mySql");
  if (w !== "INSERT" && !isReplace) return false;

  const prev = T[i - 1];
  if (prev) {
    if (isPunct(prev, ".") || isPunct(prev, ",") || prev.kind === "operator") return false;
    if (NON_INSERT_PREV.has(W(prev))) return false;
  }

  // Exclude string functions: MySQL `INSERT(str, pos, len, new)` and SQLite/MySQL `REPLACE(str, a, b)`.
  if (isPunct(T[i + 1], "(")) {
    if (isReplace) return false;
    if (dialect === "mySql" && W(prev) !== "THEN") return false;
  }
  return true;
}

/** True if every token inside `(open .. close)` is a T-SQL table hint (e.g. `INSERT INTO t (TABLOCK) (a, b)`). */
function isTsqlHintParen(T: Token[], open: number, close: number): boolean {
  if (close <= open + 1) return false;
  for (let x = open + 1; x < close; x++) {
    const t = T[x];
    if (isPunct(t, ",")) continue;
    const u = W(t);
    if (u && TABLE_HINTS.has(u)) {
      if (isPunct(T[x + 1], "(")) {
        const m = skipMatchingParen(T, x + 1, close - 1);
        if (m < 0) return false;
        x = m;
      }
      continue;
    }
    return false;
  }
  return true;
}

/** True if `(` at `open` opens a query (`(SELECT ...)`, `(WITH ...)`, `(VALUES ...)`), not a column list. */
function isParenQuery(T: Token[], open: number): boolean {
  let k = open + 1;
  while (k < T.length && isPunct(T[k], "(")) k++;
  const u = W(T[k]);
  return u === "SELECT" || u === "WITH" || u === "VALUES" || u === "FROM";
}

/** Parse comma-separated column spans inside `(colsOpen .. colsClose)`. Preserves empty slots as `null`. */
function parseColumnList(T: Token[], colsOpen: number, limit: number): { cols: (Span | null)[]; close: number } | null {
  const cols: (Span | null)[] = [];
  let colsClose = -1;
  let pDepth = 1;
  let bDepth = 0;
  let cDepth = 0;
  let colStart = -1;
  let colEnd = -1;
  let sawComma = false;

  for (let i = colsOpen + 1; i <= limit; i++) {
    const t = T[i];
    if (t.kind === "punct" && t.value === "(") {
      pDepth++;
      if (colStart < 0) colStart = t.start;
      colEnd = t.end;
    } else if (t.kind === "punct" && t.value === ")") {
      pDepth--;
      if (pDepth === 0) {
        if (colStart >= 0) cols.push({ start: colStart, end: colEnd });
        else if (sawComma) cols.push(null);
        colsClose = i;
        break;
      }
      colEnd = t.end;
    } else if (t.kind === "operator" && t.value === "[") {
      bDepth++;
      if (colStart < 0) colStart = t.start;
      colEnd = t.end;
    } else if (t.kind === "operator" && t.value === "]") {
      bDepth = Math.max(0, bDepth - 1);
      colEnd = t.end;
    } else if (t.kind === "operator" && t.value === "{") {
      cDepth++;
      if (colStart < 0) colStart = t.start;
      colEnd = t.end;
    } else if (t.kind === "operator" && t.value === "}") {
      cDepth = Math.max(0, cDepth - 1);
      colEnd = t.end;
    } else if (pDepth === 1 && bDepth === 0 && cDepth === 0 && isPunct(t, ",")) {
      cols.push(colStart >= 0 ? { start: colStart, end: colEnd } : null);
      colStart = -1;
      colEnd = -1;
      sawComma = true;
    } else {
      if (colStart < 0) colStart = t.start;
      colEnd = t.end;
    }
  }
  if (colsClose < 0 || !cols.some((c) => c !== null)) return null;
  return { cols, close: colsClose };
}

/**
 * Skips the target table expression and any post-table clauses (hints, partitions, aliases) starting at `j`.
 * Returns the token index right after the target (at `(cols)`, `SET`, `VALUES`, `SELECT`, etc.).
 */
function skipInsertTarget(T: Token[], start: number, limit: number, dialect: Dialect): number {
  let j = start;
  if (j > limit) return j;

  // PostgreSQL optional ONLY
  if (W(T[j]) === "ONLY") j++;
  if (j > limit) return j;

  // T-SQL rowset function target: OPENROWSET(...), OPENQUERY(...), OPENDATASOURCE(...)
  if (dialect === "tsql" && TSQL_ROWSET_TARGETS.has(W(T[j])) && isPunct(T[j + 1], "(")) {
    const close = skipMatchingParen(T, j + 1, limit);
    j = close < 0 ? limit + 1 : close + 1;
  } else if (isPunct(T[j], "(")) {
    // Oracle inline view target: `INSERT INTO (SELECT ...) [alias] ...` — never a column list.
    const close = skipMatchingParen(T, j, limit);
    j = close < 0 ? limit + 1 : close + 1;
  } else {
    // Dotted table name: `[db.][schema.]table`, `db..table`, `@tableVar`, `#temp`, Oracle `table@dblink`
    while (j <= limit) {
      const t = T[j];
      const nameLike =
        t.kind === "ident" ||
        t.kind === "qident" ||
        t.kind === "variable" ||
        (t.kind === "keyword" && !POST_TARGET_STOP.has(W(t)));
      if (nameLike) {
        j++;
        if (j <= limit && isPunct(T[j], ".")) {
          j++;
          if (j <= limit && isPunct(T[j], ".")) j++; // T-SQL db..table
          continue;
        }
        if (dialect === "oracle" && j <= limit && t.kind !== "variable" && T[j].kind === "operator" && T[j].value === "@") {
          j++; // Oracle @dblink
          continue;
        }
        break;
      }
      break;
    }
  }

  // Post-target clauses before column list: PARTITION (...), SUBPARTITION (...), WITH (hint), AS alias, bare alias, T-SQL (hint)
  for (;;) {
    if (j > limit) break;
    const u = W(T[j]);
    if (u === "PARTITION" || u === "SUBPARTITION") {
      j++;
      if (j <= limit && W(T[j]) === "FOR") j++;
      if (j <= limit && isPunct(T[j], "(")) {
        const close = skipMatchingParen(T, j, limit);
        if (close < 0) return limit + 1;
        j = close + 1;
        continue;
      }
      continue;
    }
    if (dialect === "tsql" && u === "WITH" && j + 1 <= limit && isPunct(T[j + 1], "(")) {
      const close = skipMatchingParen(T, j + 1, limit);
      if (close < 0) return limit + 1;
      j = close + 1;
      continue;
    }
    if (u === "AS" && j + 1 <= limit && (T[j + 1].kind === "ident" || T[j + 1].kind === "qident")) {
      j += 2;
      continue;
    }
    if ((T[j].kind === "ident" || T[j].kind === "qident") && !POST_TARGET_STOP.has(u)) {
      // Bare table alias (e.g. Oracle `INSERT INTO emp e (id, name)`, PG `INSERT INTO t a (...)`).
      j++;
      continue;
    }
    if (dialect === "tsql" && isPunct(T[j], "(")) {
      const close = skipMatchingParen(T, j, limit);
      if (close > j && close + 1 <= limit && isPunct(T[close + 1], "(") && !isParenQuery(T, close + 1) && isTsqlHintParen(T, j, close)) {
        j = close + 1;
        continue;
      }
    }
    break;
  }

  return j;
}

/** Skip an inline `WITH [RECURSIVE] cte [(cols)] AS [NOT] [MATERIALIZED] (...) [, ...]` prefix before a query. */
function skipInlineWith(T: Token[], start: number, limit: number): number {
  let k = start + 1; // past WITH
  if (k <= limit && W(T[k]) === "RECURSIVE") k++;
  while (k <= limit) {
    // CTE name
    if (T[k].kind !== "ident" && T[k].kind !== "qident" && T[k].kind !== "keyword") break;
    k++;
    // Optional CTE column list `(c1, c2)`
    if (k <= limit && isPunct(T[k], "(")) {
      const close = skipMatchingParen(T, k, limit);
      if (close < 0) return limit + 1;
      k = close + 1;
    }
    if (k <= limit && W(T[k]) === "AS") k++;
    if (k <= limit && W(T[k]) === "NOT") k++;
    if (k <= limit && W(T[k]) === "MATERIALIZED") k++;
    if (k <= limit && isPunct(T[k], "(")) {
      const close = skipMatchingParen(T, k, limit);
      if (close < 0) return limit + 1;
      k = close + 1;
    } else {
      break;
    }
    // Optional Oracle SEARCH / CYCLE clauses after CTE body
    while (k <= limit && (W(T[k]) === "SEARCH" || W(T[k]) === "CYCLE")) {
      k++;
      while (k <= limit && !isPunct(T[k], ",") && W(T[k]) !== "SELECT" && W(T[k]) !== "VALUES" && W(T[k]) !== "SEARCH" && W(T[k]) !== "CYCLE" && !isPunct(T[k], ";")) {
        if (isPunct(T[k], "(")) {
          const close = skipMatchingParen(T, k, limit);
          if (close < 0) return limit + 1;
          k = close + 1;
        } else {
          k++;
        }
      }
    }
    if (k <= limit && isPunct(T[k], ",")) {
      k++;
      continue;
    }
    break;
  }
  return k;
}

/**
 * Skip clauses between column list `)` and the value source (`OVERRIDING ... VALUE`, T-SQL `OUTPUT ...`,
 * DuckDB `BY POSITION`, inline `WITH ...`). Returns -1 if disallowed (e.g. DuckDB `BY NAME`).
 */
function skipPreSourceClauses(text: string, T: Token[], start: number, limit: number, dialect: Dialect): number {
  let k = start;
  for (;;) {
    if (k > limit || isStmtTerminator(text, T[k], dialect)) return k;
    const u = W(T[k]);
    if (u === "OVERRIDING") {
      k++;
      if (k <= limit && (W(T[k]) === "SYSTEM" || W(T[k]) === "USER")) k++;
      if (k <= limit && W(T[k]) === "VALUE") k++;
      continue;
    }
    if (dialect === "duckDb" && u === "BY" && k + 1 <= limit) {
      const next = W(T[k + 1]);
      if (next === "POSITION") { k += 2; continue; }
      if (next === "NAME") return -1;
    }
    if (dialect === "tsql" && u === "OUTPUT") {
      k++;
      let caseDepth = 0;
      while (k <= limit && !isStmtTerminator(text, T[k], dialect)) {
        const t = T[k];
        const w = W(t);
        if (isPunct(t, "(")) {
          const prev = T[k - 1];
          if (caseDepth === 0 && !isPunct(prev, ",") && prev?.kind !== "operator" && isParenQuery(T, k)) break;
          const close = skipMatchingParen(T, k, limit);
          if (close < 0) return limit + 1;
          k = close + 1;
          continue;
        }
        if (w === "CASE") { caseDepth++; k++; continue; }
        if (w === "END" && caseDepth > 0) { caseDepth--; k++; continue; }
        if (caseDepth === 0 && (w === "VALUES" || w === "SELECT" || w === "DEFAULT" || w === "EXEC" || w === "EXECUTE" || w === "WITH")) {
          break;
        }
        k++;
      }
      continue;
    }
    if (u === "WITH") {
      k = skipInlineWith(T, k, limit);
      continue;
    }
    break;
  }
  return k;
}

/** Parse one parenthesized `VALUES` tuple starting at `T[openIdx] === '('`. Tolerates an unclosed `(` while typing. */
function parseValuesTuple(
  text: string,
  T: Token[],
  openIdx: number,
  limit: number,
  dialect: Dialect,
): { row: (Span | null)[]; next: number; closed: boolean } {
  const row: (Span | null)[] = [];
  let pDepth = 1;
  let bDepth = 0;
  let cDepth = 0;
  let caseDepth = 0;
  let itemStart = -1;
  let itemEnd = -1;
  let sawComma = false;

  for (let i = openIdx + 1; i <= limit; i++) {
    const t = T[i];
    if (pDepth === 1 && bDepth === 0 && cDepth === 0 && caseDepth === 0 && isStmtTerminator(text, t, dialect)) {
      if (itemStart >= 0) row.push({ start: itemStart, end: itemEnd });
      else if (sawComma) row.push(null);
      return { row, next: i, closed: false };
    }
    if (t.kind === "punct" && t.value === "(") {
      pDepth++;
      if (itemStart < 0) itemStart = t.start;
      itemEnd = t.end;
    } else if (t.kind === "punct" && t.value === ")") {
      pDepth--;
      if (pDepth === 0) {
        if (itemStart >= 0) row.push({ start: itemStart, end: itemEnd });
        else if (sawComma) row.push(null);
        return { row, next: i + 1, closed: true };
      }
      itemEnd = t.end;
    } else if (t.kind === "operator" && t.value === "[") {
      bDepth++;
      if (itemStart < 0) itemStart = t.start;
      itemEnd = t.end;
    } else if (t.kind === "operator" && t.value === "]") {
      bDepth = Math.max(0, bDepth - 1);
      itemEnd = t.end;
    } else if (t.kind === "operator" && t.value === "{") {
      cDepth++;
      if (itemStart < 0) itemStart = t.start;
      itemEnd = t.end;
    } else if (t.kind === "operator" && t.value === "}") {
      cDepth = Math.max(0, cDepth - 1);
      itemEnd = t.end;
    } else if (pDepth === 1 && bDepth === 0 && cDepth === 0 && caseDepth === 0 && isPunct(t, ",")) {
      row.push(itemStart >= 0 ? { start: itemStart, end: itemEnd } : null);
      itemStart = -1;
      itemEnd = -1;
      sawComma = true;
    } else {
      const u = W(t);
      if (u === "CASE") caseDepth++;
      else if (u === "END" && caseDepth > 0) caseDepth--;
      if (itemStart < 0) itemStart = t.start;
      itemEnd = t.end;
    }
  }
  if (itemStart >= 0) row.push({ start: itemStart, end: itemEnd });
  else if (sawComma) row.push(null);
  return { row, next: limit + 1, closed: false };
}

/** True if `T[i]` ends a SELECT projection list at depth 0 (given whether an expression has started). */
function isSelectListBoundary(
  text: string,
  T: Token[],
  i: number,
  limit: number,
  dialect: Dialect,
  hasExpr: boolean,
): boolean {
  const t = T[i];
  if (isStmtTerminator(text, t, dialect) || isPunct(t, ")")) return true;
  // A dotted member (`s.from`, `s.end`, `s.set`) is a column/field name, never a clause or statement boundary.
  if (isPunct(T[i - 1], ".")) return false;
  const u = W(t);
  if (!u) return false;
  if (SELECT_CLAUSE_END.has(u)) return true;
  if (u === "GROUP" && i + 1 <= limit && W(T[i + 1]) === "BY") return true;
  if (u === "ORDER" && i + 1 <= limit && W(T[i + 1]) === "BY") return true;
  if (u === "WINDOW" && dialect !== "oracle") return true;
  if (u === "QUALIFY" && dialect === "duckDb") return true;
  if (u === "ON" && i + 1 <= limit && (W(T[i + 1]) === "CONFLICT" || W(T[i + 1]) === "DUPLICATE")) return true;
  if (u === "LOG" && dialect === "oracle" && i + 1 <= limit && W(T[i + 1]) === "ERRORS") return true;

  // Statement-starting verb without a semicolon (e.g. `INSERT INTO t (a) SELECT 'x'\nSELECT 1`).
  if (hasExpr && STMT_BREAK_VERBS.has(u)) {
    const prev = T[i - 1];
    if (prev && prev.kind !== "operator" && !EXPR_CONT_PREV.has(W(prev))) {
      return true;
    }
  }
  return false;
}

/** Parse one `SELECT` branch's projection list starting at `T[selectIdx] === 'SELECT'`. */
function parseSelectBranch(
  text: string,
  T: Token[],
  selectIdx: number,
  limit: number,
  dialect: Dialect,
): { row: (Span | null)[]; next: number } {
  let k = selectIdx + 1;

  // Skip SELECT modifiers before the first expression
  while (k <= limit) {
    const u = W(T[k]);
    const nx = T[k + 1];
    if (u === "DISTINCT") {
      k++;
      if (k <= limit && W(T[k]) === "ON" && k + 1 <= limit && isPunct(T[k + 1], "(")) {
        const close = skipMatchingParen(T, k + 1, limit);
        if (close < 0) return { row: [], next: limit + 1 };
        k = close + 1;
      }
      continue;
    }
    if (u === "ALL" && nx && !isPunct(nx, ",") && W(nx) !== "FROM" && nx.kind !== "operator" && !isStmtTerminator(text, nx, dialect)) {
      k++;
      continue;
    }
    if (dialect === "oracle" && u === "UNIQUE" && nx && !isPunct(nx, ",") && W(nx) !== "FROM" && nx.kind !== "operator") {
      k++;
      continue;
    }
    if (dialect === "mySql" && MYSQL_SELECT_MODS.has(u)) {
      k++;
      continue;
    }
    if (dialect === "tsql" && u === "TOP") {
      k++;
      if (k <= limit && isPunct(T[k], "(")) {
        const close = skipMatchingParen(T, k, limit);
        if (close < 0) return { row: [], next: limit + 1 };
        k = close + 1;
      } else if (k <= limit) {
        k++;
      }
      if (k <= limit && W(T[k]) === "PERCENT") k++;
      if (k + 1 <= limit && W(T[k]) === "WITH" && W(T[k + 1]) === "TIES") k += 2;
      continue;
    }
    break;
  }

  const row: (Span | null)[] = [];
  let pDepth = 0;
  let bDepth = 0;
  let cDepth = 0;
  let caseDepth = 0;
  let itemStart = -1;
  let itemEnd = -1;
  let itemFirstIdx = -1;
  let itemWildcard = false;
  let hitWildcard = false;
  let sawComma = false;

  const pushItem = () => {
    if (hitWildcard) return;
    if (itemWildcard) {
      hitWildcard = true;
      return;
    }
    if (itemStart >= 0) row.push({ start: itemStart, end: itemEnd });
    else if (sawComma) row.push(null);
  };

  let i = k;
  for (; i <= limit; i++) {
    const t = T[i];
    const atTop = pDepth === 0 && bDepth === 0 && cDepth === 0 && caseDepth === 0;
    if (atTop && isSelectListBoundary(text, T, i, limit, dialect, itemStart >= 0)) {
      pushItem();
      return { row, next: i };
    }
    if (atTop && isPunct(t, ",")) {
      pushItem();
      itemStart = -1;
      itemEnd = -1;
      itemFirstIdx = -1;
      itemWildcard = false;
      sawComma = true;
      continue;
    }
    if (t.kind === "punct" && t.value === "(") {
      pDepth++;
    } else if (t.kind === "punct" && t.value === ")") {
      pDepth = Math.max(0, pDepth - 1);
    } else if (t.kind === "operator" && t.value === "[") {
      bDepth++;
    } else if (t.kind === "operator" && t.value === "]") {
      bDepth = Math.max(0, bDepth - 1);
    } else if (t.kind === "operator" && t.value === "{") {
      cDepth++;
    } else if (t.kind === "operator" && t.value === "}") {
      cDepth = Math.max(0, cDepth - 1);
    } else {
      const u = W(t);
      if (u === "CASE") caseDepth++;
      else if (u === "END" && caseDepth > 0) caseDepth--;
    }

    if (itemStart < 0) {
      itemStart = t.start;
      itemFirstIdx = i;
      if (t.kind === "operator" && t.value === "*") itemWildcard = true;
    } else if (atTop && t.kind === "operator" && t.value === "*" && isPunct(T[i - 1], ".")) {
      // Check that everything from itemFirstIdx to i - 1 is just a dotted qualifier (`tbl.*` or `schema.tbl.*`).
      let onlyQualifier = true;
      for (let q = itemFirstIdx; q < i; q++) {
        const qt = T[q];
        if (qt.kind !== "ident" && qt.kind !== "qident" && !isPunct(qt, ".")) {
          onlyQualifier = false;
          break;
        }
      }
      if (onlyQualifier) itemWildcard = true;
    }
    itemEnd = t.end;
  }

  pushItem();
  return { row, next: i };
}

/**
 * After a SELECT branch's projection list ends at `start`, skip `FROM ... WHERE ... GROUP BY ... HAVING ...`
 * until a top-level set operator (`UNION` / `INTERSECT` / `EXCEPT` / `MINUS`) or end of query.
 */
function skipToSetOperator(text: string, T: Token[], start: number, limit: number, dialect: Dialect): number {
  let pDepth = 0;
  let bDepth = 0;
  let cDepth = 0;
  let caseDepth = 0;
  for (let i = start; i <= limit; i++) {
    const t = T[i];
    const atTop = pDepth === 0 && bDepth === 0 && cDepth === 0 && caseDepth === 0;
    if (atTop) {
      if (isStmtTerminator(text, t, dialect) || isPunct(t, ")")) return i;
      if (isPunct(T[i - 1], ".")) { continue; }
      const u = W(t);
      if (SET_OPS.has(u)) return i;
      if (u === "RETURNING" || u === "OPTION") return i;
      if (u === "ON" && i + 1 <= limit && (W(T[i + 1]) === "CONFLICT" || W(T[i + 1]) === "DUPLICATE")) return i;
      if (u === "LOG" && dialect === "oracle" && i + 1 <= limit && W(T[i + 1]) === "ERRORS") return i;
      if (u === "ORDER" && i + 1 <= limit && W(T[i + 1]) === "BY") return i;
      if (u === "LIMIT" || u === "OFFSET" || u === "FETCH") return i;
      if (u === "FOR" && !(i + 1 <= limit && W(T[i + 1]) === "SYSTEM_TIME")) return i;
      // Stop on a new statement verb (e.g. semicolon-less T-SQL `INSERT ... SELECT 1 FROM s\nSELECT 2`).
      // Allow T-SQL `WITH (NOLOCK)` table hints inside FROM and operands after operators / WHERE / HAVING / ON / AND / OR.
      if (STMT_BREAK_VERBS.has(u)) {
        if (u === "WITH" && i + 1 <= limit && isPunct(T[i + 1], "(")) {
          const close = skipMatchingParen(T, i + 1, limit);
          if (close < 0) return limit + 1;
          i = close;
          continue;
        }
        const prev = T[i - 1];
        const pu = W(prev);
        if (u === "SET" && pu === "CHARACTER") continue;
        if (prev && (prev.kind === "operator" || EXPR_CONT_PREV.has(pu) || pu === "WHERE" || pu === "HAVING" || pu === "ON")) {
          continue;
        }
        return i;
      }
    }
    if (t.kind === "punct" && t.value === "(") pDepth++;
    else if (t.kind === "punct" && t.value === ")") pDepth = Math.max(0, pDepth - 1);
    else if (t.kind === "operator" && t.value === "[") bDepth++;
    else if (t.kind === "operator" && t.value === "]") bDepth = Math.max(0, bDepth - 1);
    else if (t.kind === "operator" && t.value === "{") cDepth++;
    else if (t.kind === "operator" && t.value === "}") cDepth = Math.max(0, cDepth - 1);
    else {
      const u = W(t);
      if (u === "CASE") caseDepth++;
      else if (u === "END" && caseDepth > 0) caseDepth--;
    }
  }
  return limit + 1;
}

interface SourceParseOpts {
  /** If >= 0, we only care about the single row containing `seekOffset` (fast-path for huge multi-row VALUES). */
  seekOffset: number;
  /** Cap on collected rows when highlighting all rows for a clicked column. */
  maxRows: number;
}

/**
 * Parse a query/values source (`VALUES (...)`, `SELECT ...`, parenthesized query, DuckDB `FROM ... SELECT ...`,
 * and compound queries joined by `UNION [ALL]` / `INTERSECT` / `EXCEPT` / `MINUS`).
 */
function parseValueSource(
  text: string,
  T: Token[],
  start: number,
  limit: number,
  dialect: Dialect,
  opts: SourceParseOpts,
  rows: (Span | null)[][],
  recursionDepth = 0,
): number {
  if (recursionDepth > 8) return start;
  let k = start;

  while (k <= limit && rows.length < opts.maxRows) {
    if (isStmtTerminator(text, T[k], dialect) || isPunct(T[k], ")")) break;
    const u = W(T[k]);

    if (u === "WITH") {
      k = skipInlineWith(T, k, limit);
      continue;
    }

    if (u === "VALUES" || (dialect === "mySql" && u === "VALUE")) {
      k++;
      while (k <= limit && rows.length < opts.maxRows) {
        if (W(T[k]) === "ROW" && k + 1 <= limit && isPunct(T[k + 1], "(")) k++;
        if (!isPunct(T[k], "(")) break;

        // Fast-path when cursor is in values of a huge multi-row VALUES: skip tuples that end strictly before cursor.
        if (opts.seekOffset >= 0) {
          const close = skipMatchingParen(T, k, limit);
          if (close >= 0 && T[close].end < opts.seekOffset) {
            k = close + 1;
            if (k <= limit && isPunct(T[k], ",")) { k++; continue; }
            break;
          }
          const parsed = parseValuesTuple(text, T, k, limit, dialect);
          if (parsed.row.length) rows.push(parsed.row);
          k = parsed.next;
          // If the tuple started past seekOffset or contained seekOffset, no need to scan thousands more tuples.
          if (T[k - 1] && T[k - 1].end >= opts.seekOffset) return k;
          if (parsed.closed && k <= limit && isPunct(T[k], ",")) { k++; continue; }
          break;
        }

        const parsed = parseValuesTuple(text, T, k, limit, dialect);
        if (parsed.row.length) rows.push(parsed.row);
        k = parsed.next;
        if (parsed.closed && k <= limit && isPunct(T[k], ",")) {
          k++;
          continue;
        }
        break;
      }
    } else if (u === "SELECT") {
      const parsed = parseSelectBranch(text, T, k, limit, dialect);
      if (parsed.row.length) rows.push(parsed.row);
      k = skipToSetOperator(text, T, parsed.next, limit, dialect);
    } else if (dialect === "duckDb" && u === "FROM") {
      // DuckDB FROM-first query: `INSERT INTO t (a, b) FROM src WHERE ... SELECT x, y`
      let pDepth = 0;
      let bDepth = 0;
      let cDepth = 0;
      k++;
      while (k <= limit) {
        const t = T[k];
        const atTop = pDepth === 0 && bDepth === 0 && cDepth === 0;
        if (atTop) {
          if (isStmtTerminator(text, t, dialect) || isPunct(t, ")")) break;
          const w = W(t);
          if (w === "SELECT" || SET_OPS.has(w) || w === "RETURNING") break;
          if (w === "ON" && k + 1 <= limit && W(T[k + 1]) === "CONFLICT") break;
        }
        if (isPunct(t, "(")) pDepth++;
        else if (isPunct(t, ")")) pDepth = Math.max(0, pDepth - 1);
        else if (t.kind === "operator" && t.value === "[") bDepth++;
        else if (t.kind === "operator" && t.value === "]") bDepth = Math.max(0, bDepth - 1);
        else if (t.kind === "operator" && t.value === "{") cDepth++;
        else if (t.kind === "operator" && t.value === "}") cDepth = Math.max(0, cDepth - 1);
        k++;
      }
      if (k <= limit && W(T[k]) === "SELECT") {
        const parsed = parseSelectBranch(text, T, k, limit, dialect);
        if (parsed.row.length) rows.push(parsed.row);
        k = skipToSetOperator(text, T, parsed.next, limit, dialect);
      } else {
        break;
      }
    } else if (isPunct(T[k], "(")) {
      const close = skipMatchingParen(T, k, limit);
      const innerLimit = close < 0 ? limit : close - 1;
      parseValueSource(text, T, k + 1, innerLimit, dialect, opts, rows, recursionDepth + 1);
      if (close < 0) return limit + 1;
      k = skipToSetOperator(text, T, close + 1, limit, dialect);
    } else {
      break;
    }

    // Check for compound query set operator (`UNION [ALL|DISTINCT]`, `INTERSECT`, `EXCEPT`, `MINUS`).
    if (k <= limit && SET_OPS.has(W(T[k]))) {
      k++;
      if (k <= limit && (W(T[k]) === "ALL" || W(T[k]) === "DISTINCT")) k++;
      // DuckDB `UNION BY NAME` matches by alias, not position — stop positional collection.
      if (dialect === "duckDb" && k + 1 <= limit && W(T[k]) === "BY" && W(T[k + 1]) === "NAME") break;
      continue;
    }
    break;
  }

  return k;
}

/** Match cursor `offset` against `cols` and parsed value `rows`. */
function matchColsAndRows(cols: (Span | null)[], rows: (Span | null)[][], offset: number): InsertMatchResult | null {
  if (!cols.length || !rows.length) return null;

  for (let i = 0; i < cols.length; i++) {
    const c = cols[i];
    if (c && offset >= c.start && offset <= c.end) {
      const vals: Span[] = [];
      for (const row of rows) {
        const v = i < row.length ? row[i] : null;
        if (v) {
          vals.push(v);
          if (vals.length >= MAX_HIGHLIGHT_VALS) break;
        }
      }
      if (vals.length) return { col: c, val: vals[0], vals };
      return null;
    }
  }

  for (const row of rows) {
    const n = Math.min(row.length, cols.length);
    for (let i = 0; i < n; i++) {
      const v = row[i];
      if (v && offset >= v.start && offset <= v.end) {
        const c = cols[i];
        if (c) return { col: c, val: v, vals: [v] };
        return null;
      }
    }
  }

  return null;
}

/** MySQL / MariaDB `INSERT/REPLACE [INTO] tbl [PARTITION (...)] SET col1 = expr1, col2 = expr2 ...`. */
function matchInsertSet(text: string, T: Token[], setIdx: number, limit: number, offset: number): InsertMatchResult | null {
  let pDepth = 0;
  let bDepth = 0;
  let cDepth = 0;
  let caseDepth = 0;
  let lhsStart = -1;
  let lhsEnd = -1;
  let rhsStart = -1;
  let rhsEnd = -1;
  let seenEq = false;

  const checkPair = (): InsertMatchResult | null => {
    if (lhsStart >= 0 && rhsStart >= 0) {
      const col: Span = { start: lhsStart, end: lhsEnd };
      const val: Span = { start: rhsStart, end: rhsEnd };
      if ((offset >= col.start && offset <= col.end) || (offset >= val.start && offset <= val.end)) {
        return { col, val, vals: [val] };
      }
    }
    return null;
  };

  for (let i = setIdx + 1; i <= limit; i++) {
    const t = T[i];
    const atTop = pDepth === 0 && bDepth === 0 && cDepth === 0 && caseDepth === 0;
    if (atTop) {
      if (isStmtTerminator(text, t, "mySql") || isPunct(t, ")")) break;
      const u = W(t);
      if (u === "ON" && i + 1 <= limit && W(T[i + 1]) === "DUPLICATE") break;
      if (u === "RETURNING") break;
      if (seenEq && rhsStart >= 0 && STMT_BREAK_VERBS.has(u)) {
        const prev = T[i - 1];
        if (prev && prev.kind !== "operator" && !isPunct(prev, ".") && !EXPR_CONT_PREV.has(W(prev))) break;
      }
      if (isPunct(t, ",")) {
        const hit = checkPair();
        if (hit) return hit;
        lhsStart = lhsEnd = rhsStart = rhsEnd = -1;
        seenEq = false;
        continue;
      }
      if (!seenEq && t.kind === "operator" && (t.value === "=" || t.value === ":=")) {
        seenEq = true;
        continue;
      }
    }

    if (isPunct(t, "(")) pDepth++;
    else if (isPunct(t, ")")) pDepth = Math.max(0, pDepth - 1);
    else if (t.kind === "operator" && t.value === "[") bDepth++;
    else if (t.kind === "operator" && t.value === "]") bDepth = Math.max(0, bDepth - 1);
    else if (t.kind === "operator" && t.value === "{") cDepth++;
    else if (t.kind === "operator" && t.value === "}") cDepth = Math.max(0, cDepth - 1);
    else {
      const u = W(t);
      if (u === "CASE") caseDepth++;
      else if (u === "END" && caseDepth > 0) caseDepth--;
    }

    if (!seenEq) {
      if (lhsStart < 0) lhsStart = t.start;
      lhsEnd = t.end;
    } else {
      if (rhsStart < 0) rhsStart = t.start;
      rhsEnd = t.end;
    }
  }

  return checkPair();
}

/** Oracle multi-table `INSERT ALL` / `INSERT FIRST` (conditional and unconditional). */
function matchOracleMultiInsert(
  text: string,
  T: Token[],
  insertIdx: number,
  limit: number,
  offset: number,
): InsertMatchResult | null {
  interface Branch {
    cols: (Span | null)[];
    rows?: (Span | null)[][];
  }
  const branches: Branch[] = [];
  let k = insertIdx + 2; // past `INSERT ALL` or `INSERT FIRST`
  let pDepth = 0;
  let caseDepth = 0;
  let inWhenCond = false;
  let selectStart = -1;

  while (k <= limit) {
    const t = T[k];
    if (pDepth === 0 && caseDepth === 0) {
      if (isStmtTerminator(text, t, "oracle") || isPunct(t, ")")) break;
      const u = W(t);
      if (u === "WHEN") { inWhenCond = true; k++; continue; }
      if (u === "THEN") { inWhenCond = false; k++; continue; }
      if (!inWhenCond && (u === "SELECT" || u === "WITH" || (isPunct(t, "(") && isParenQuery(T, k)))) {
        selectStart = k;
        break;
      }
      if (u === "INTO") {
        const afterTarget = skipInsertTarget(T, k + 1, limit, "oracle");
        let cols: (Span | null)[] | null = null;
        let next = afterTarget;
        if (next <= limit && isPunct(T[next], "(") && !isParenQuery(T, next)) {
          const parsedCols = parseColumnList(T, next, limit);
          if (parsedCols) {
            cols = parsedCols.cols;
            next = parsedCols.close + 1;
          }
        }
        let rows: (Span | null)[][] | undefined;
        if (next <= limit && W(T[next]) === "VALUES") {
          next++;
          if (next <= limit && isPunct(T[next], "(")) {
            const parsedTuple = parseValuesTuple(text, T, next, limit, "oracle");
            if (parsedTuple.row.length) rows = [parsedTuple.row];
            next = parsedTuple.next;
          }
        }
        if (cols) {
          if (rows) {
            const hit = matchColsAndRows(cols, rows, offset);
            if (hit) return hit;
          }
          branches.push({ cols, rows });
        }
        k = Math.max(next, k + 1);
        continue;
      }
    }
    if (isPunct(t, "(")) pDepth++;
    else if (isPunct(t, ")")) pDepth = Math.max(0, pDepth - 1);
    else {
      const u = W(t);
      if (u === "CASE") caseDepth++;
      else if (u === "END" && caseDepth > 0) caseDepth--;
    }
    k++;
  }

  // Branches without their own `VALUES (...)` insert directly from the trailing `SELECT` query.
  const selectBranches = branches.filter((b) => !b.rows);
  if (selectBranches.length && selectStart >= 0) {
    const selectRows: (Span | null)[][] = [];
    parseValueSource(
      text, T, selectStart, limit, "oracle",
      { seekOffset: -1, maxRows: MAX_HIGHLIGHT_VALS },
      selectRows,
    );
    if (selectRows.length) {
      for (const b of selectBranches) {
        const hit = matchColsAndRows(b.cols, selectRows, offset);
        if (hit) return hit;
      }
    }
  }
  return null;
}

/**
 * Finds matching column and value positions in an INSERT / REPLACE statement across all supported SQL dialects:
 * - `INSERT [INTO] tbl (cols) VALUES (...), (...), ...` (single/multi-row, partial typing, MySQL `VALUE` / `ROW()`)
 * - `INSERT [INTO] tbl (cols) SELECT ... [UNION/INTERSECT/EXCEPT/MINUS ...]` (including modifiers, CTEs, DuckDB `FROM`-first)
 * - `MERGE ... WHEN NOT MATCHED THEN INSERT (cols) VALUES (...)`
 * - `REPLACE [INTO] tbl (cols) VALUES/SELECT ...` (SQLite, MySQL)
 * - `INSERT/REPLACE [INTO] tbl SET col = expr, ...` (MySQL)
 * - `INSERT ALL / FIRST ... INTO tbl (cols) [VALUES (...)] SELECT ...` (Oracle)
 */
function findInsertMatchingHighlightInRegion(
  text: string,
  offset: number,
  dialect: Dialect,
): InsertMatchResult | null {
  const stream = getStream(text, dialect);
  const T = stream.code;
  if (!T.length) return null;

  let curIdx = lastStartingBefore(T, offset + 1);
  if (curIdx < 0) return null;
  // If cursor sits right at the end of a token immediately before `;` / `GO` / Oracle `/` (e.g. `SELECT 'asd'|;`),
  // step back to that token so the trailing terminator doesn't abort the backward search.
  if (curIdx > 0 && T[curIdx].start === offset && isStmtTerminator(text, T[curIdx], dialect)) {
    curIdx--;
  }

  // Locate the nearest enclosing INSERT / REPLACE at the cursor's paren level or outside it.
  // Tracking depth/minDepth ensures a sibling closed `(...)` (e.g. a writable CTE before an outer SELECT) is skipped.
  let insertIdx = -1;
  let depth = 0;
  let minDepth = 0;
  for (let i = curIdx; i >= 0; i--) {
    const t = T[i];
    if (isStmtTerminator(text, t, dialect)) break;
    if (t.kind === "punct") {
      if (t.value === ")") { depth++; continue; }
      if (t.value === "(") { depth--; if (depth < minDepth) minDepth = depth; continue; }
    }
    if (depth === minDepth && isInsertStart(T, i, dialect)) {
      insertIdx = i;
      break;
    }
  }
  if (insertIdx < 0) return null;

  const limit = T.length - 1;

  // Oracle multi-table INSERT ALL / INSERT FIRST
  if (dialect === "oracle" && W(T[insertIdx]) === "INSERT") {
    const nextW = W(T[insertIdx + 1]);
    if (nextW === "ALL" || nextW === "FIRST") {
      return matchOracleMultiInsert(text, T, insertIdx, limit, offset);
    }
  }

  let j = insertIdx + 1;

  // MERGE ... WHEN NOT MATCHED [BY TARGET] THEN INSERT (cols) [OVERRIDING ...] VALUES (...)
  const isMergeInsert = j <= limit && isPunct(T[j], "(");
  if (!isMergeInsert) {
    // T-SQL `INSERT TOP (n) [PERCENT]`
    if (dialect === "tsql" && j <= limit && W(T[j]) === "TOP") {
      j++;
      if (j <= limit && isPunct(T[j], "(")) {
        const close = skipMatchingParen(T, j, limit);
        if (close < 0) return null;
        j = close + 1;
      } else if (j <= limit) {
        j++;
      }
      if (j <= limit && W(T[j]) === "PERCENT") j++;
    }

    // SQLite / DuckDB `INSERT OR REPLACE|IGNORE|ABORT|FAIL|ROLLBACK`
    if ((dialect === "sqlite" || dialect === "duckDb") && j <= limit && W(T[j]) === "OR") {
      j += 2;
    }

    // MySQL `INSERT/REPLACE [LOW_PRIORITY|DELAYED|HIGH_PRIORITY] [IGNORE]`
    if (dialect === "mySql") {
      while (j <= limit && MYSQL_INSERT_MODIFIERS.has(W(T[j]))) j++;
    }

    // Optional `INTO`
    if (j <= limit && W(T[j]) === "INTO") j++;

    // Target table + post-target clauses (partitions, hints, aliases)
    j = skipInsertTarget(T, j, limit, dialect);
  }

  if (j > limit) return null;

  // MySQL `INSERT/REPLACE [INTO] tbl [PARTITION (...)] SET col = val, ...`
  if (dialect === "mySql" && W(T[j]) === "SET") {
    return matchInsertSet(text, T, j, limit, offset);
  }

  // Must have an explicit column list `(...)` (not a parenthesized query `(SELECT ...)`).
  if (!isPunct(T[j], "(") || isParenQuery(T, j)) return null;

  const parsedCols = parseColumnList(T, j, limit);
  if (!parsedCols) return null;
  const { cols, close: colsClose } = parsedCols;

  const sourceStart = skipPreSourceClauses(text, T, colsClose + 1, limit, dialect);
  if (sourceStart < 0 || sourceStart > limit) return null;

  const cursorInCols = offset >= T[j].start && offset <= T[colsClose].end;
  // If cursor sits between column list and source (e.g. inside T-SQL OUTPUT or inline CTE), nothing can match.
  if (!cursorInCols && offset < T[sourceStart].start) return null;

  const rows: (Span | null)[][] = [];
  parseValueSource(
    text, T, sourceStart, limit, dialect,
    {
      seekOffset: cursorInCols ? -1 : offset,
      maxRows: MAX_HIGHLIGHT_VALS,
    },
    rows,
  );

  return matchColsAndRows(cols, rows, offset);
}

const pairCache = new WeakMap<TokenStream, Map<number, Span[]>>();

/** Build once per token-stream version. Cursor moves only do binary search + lookup. */
function pairsFor(stream: TokenStream): Map<number, Span[]> {
  const cached = pairCache.get(stream);
  if (cached) return cached;
  const pairs = new Map<number, Span[]>();
  const blocks: { index: number; kind: string; span: Span; middle?: { index: number; span: Span } }[] = [];
  const ifs: { index: number; depth: number }[] = [];
  const T = stream.code;
  const plpg = PLPGSQL_BLOCKS[stream.dialect];
  const span = (i: number, compound = false): Span => ({ start: T[i].start, end: T[i + (compound ? 1 : 0)].end });
  for (let i = 0; i < T.length; i++) {
    const w = W(T[i]), next = W(T[i + 1]);
    if (T[i].kind === "go") { blocks.length = 0; ifs.length = 0; continue; }
    if (w === "CASE" || w === "BEGIN") {
      if (w === "BEGIN" && ["TRAN", "TRANSACTION", "DISTRIBUTED", "WORK"].includes(next)) continue;
      // PostgreSQL BEGIN; is a transaction command; PL/pgSQL blocks have no semicolon here.
      if (plpg && w === "BEGIN" && T[i + 1]?.value === ";") continue;
      const compound = w === "BEGIN" && (next === "TRY" || next === "CATCH");
      blocks.push({ index: i, kind: compound ? next : w, span: span(i, compound) });
      if (compound) i++;
    } else if (w === "END") {
      if (next === "LOOP") { i++; continue; }
      const open = blocks.pop();
      const compound = next === "TRY" || next === "CATCH" || next === "IF" || next === "CASE";
      if (open && (compound ? open.kind === next : open.kind === "BEGIN" || open.kind === "CASE")) {
        const pair = open.middle ? [open.span, open.middle.span, span(i, compound)] : [open.span, span(i, compound)];
        pairs.set(open.index, pair); pairs.set(i, pair);
        if (open.middle) pairs.set(open.middle.index, pair);
        if (compound) {
          if (open.kind === "TRY" || open.kind === "CATCH") pairs.set(open.index + 1, pair);
          pairs.set(i + 1, pair);
        }
      }
      while (ifs.length && ifs[ifs.length - 1].depth > blocks.length) ifs.pop();
      if (compound) i++;
    } else if (w === "IF" && W(T[i - 1]) !== "END") {
      if (!plpg) ifs.push({ index: i, depth: blocks.length });
      else if (next !== "EXISTS" && next !== "NOT") blocks.push({ index: i, kind: "IF", span: span(i) });
    } else if (w === "ELSE" && plpg && blocks.at(-1)?.kind === "IF") {
      blocks[blocks.length - 1].middle = { index: i, span: span(i) };
    } else if (w === "ELSE" && blocks.at(-1)?.kind !== "CASE") {
      const open = ifs.at(-1);
      if (open && open.depth === blocks.length) {
        ifs.pop();
        const pair = [span(open.index), span(i)];
        pairs.set(open.index, pair); pairs.set(i, pair);
      }
    }
  }
  pairCache.set(stream, pairs);
  return pairs;
}

function findSyntaxPairHighlightInRegion(text: string, offset: number, dialect: Dialect): Span[] | null {
  const stream = getStream(text, dialect);
  const idx = lastStartingBefore(stream.code, offset + 1);
  const tok = stream.code[idx];
  if (!tok || offset < tok.start || offset > tok.end) return null;
  if (!["BEGIN", "CASE", "END", "TRY", "CATCH", "IF", "ELSE"].includes(W(tok))) return null;
  return pairsFor(stream).get(idx) ?? null;
}

export function findInsertMatchingHighlight(text: string, offset: number, dialect: Dialect): InsertMatchResult | null {
  const region = sqlRegionAtOffset(text, offset, dialect);
  const match = findInsertMatchingHighlightInRegion(region.text, region.offset, dialect);
  return match
    ? {
        col: { start: match.col.start + region.base, end: match.col.end + region.base },
        val: { start: match.val.start + region.base, end: match.val.end + region.base },
        vals: match.vals.map((v) => ({ start: v.start + region.base, end: v.end + region.base })),
      }
    : null;
}
export function findSyntaxPairHighlight(text: string, offset: number, dialect: Dialect): Span[] | null {
  const region = sqlRegionAtOffset(text, offset, dialect);
  const pairs = findSyntaxPairHighlightInRegion(region.text, region.offset, dialect);
  return pairs?.map((span) => ({ start: span.start + region.base, end: span.end + region.base })) ?? null;
}

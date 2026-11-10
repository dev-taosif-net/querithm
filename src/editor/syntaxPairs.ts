import { sqlRegionAtOffset } from "./completion/sqlRegion";
import type { Dialect } from "../ipc/contract";
import { getStream, type TokenStream } from "./completion/lexer";
import { lastStartingBefore, W } from "./completion/scanner";

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
  val: Span;
}

/**
 * Finds matching column and value positions in an INSERT INTO ... VALUES (...) statement.
 * If offset is inside column i, returns { col: col[i], val: val[i] }.
 * If offset is inside value i, returns { col: col[i], val: val[i] }.
 */
function findInsertMatchingHighlightInRegion(
  text: string,
  offset: number,
  dialect: Dialect,
): InsertMatchResult | null {
  const stream = getStream(text, dialect);
  const T = stream.code;
  if (!T.length) return null;

  const curIdx = lastStartingBefore(T, offset + 1);
  if (curIdx < 0) return null;

  // The nearest INSERT before the cursor: T-SQL scripts often separate statements without semicolons.
  let insertIdx = -1;
  for (let i = curIdx; i >= 0; i--) {
    if (T[i].kind === "go" || (T[i].kind === "punct" && T[i].value === ";")) break;
    if (W(T[i]) === "INSERT") {
      insertIdx = i;
      break;
    }
  }
  if (insertIdx < 0) return null;

  // Find column list open paren `(`
  let colsOpen = -1;
  for (let i = insertIdx + 1; i < T.length; i++) {
    const u = W(T[i]);
    if (u === "VALUES" || u === "SELECT" || (T[i].kind === "punct" && T[i].value === ";")) break;
    if (T[i].kind === "punct" && T[i].value === "(") {
      colsOpen = i;
      break;
    }
  }
  if (colsOpen < 0) return null;

  // Collect columns inside column list
  const cols: Span[] = [];
  let colsClose = -1;
  let pDepth = 1;
  let colStart = -1;

  for (let i = colsOpen + 1; i < T.length; i++) {
    const t = T[i];
    if (t.kind === "punct" && t.value === "(") {
      pDepth++;
    } else if (t.kind === "punct" && t.value === ")") {
      pDepth--;
      if (pDepth === 0) {
        if (colStart >= 0) {
          cols.push({ start: colStart, end: T[i - 1].end });
        }
        colsClose = i;
        break;
      }
    } else if (pDepth === 1 && t.kind === "punct" && t.value === ",") {
      if (colStart >= 0) {
        cols.push({ start: colStart, end: T[i - 1].end });
        colStart = -1;
      }
    } else if (pDepth === 1) {
      if (colStart < 0) colStart = t.start;
    }
  }
  if (colsClose < 0 || !cols.length) return null;

  // Find VALUES keyword
  let valuesIdx = -1;
  for (let i = colsClose + 1; i < T.length; i++) {
    if (T[i].kind === "go" || T[i].value === ";" || W(T[i]) === "SELECT" || W(T[i]) === "INSERT") break;
    if (W(T[i]) === "VALUES") {
      valuesIdx = i;
      break;
    }
  }
  if (valuesIdx < 0) return null;

  // Find values list open paren `(`
  let valsOpen = -1;
  for (let i = valuesIdx + 1; i < T.length; i++) {
    if (T[i].kind === "punct" && T[i].value === "(") {
      valsOpen = i;
      break;
    }
  }
  if (valsOpen < 0) return null;

  // Collect value expressions inside values paren
  const vals: Span[] = [];
  pDepth = 1;
  let valStart = -1;
  for (let i = valsOpen + 1; i < T.length; i++) {
    const t = T[i];
    if (t.kind === "punct" && t.value === "(") {
      pDepth++;
      if (valStart < 0) valStart = t.start;
    } else if (t.kind === "punct" && t.value === ")") {
      pDepth--;
      if (pDepth === 0) {
        if (valStart >= 0) {
          vals.push({ start: valStart, end: T[i - 1].end });
        }
        break;
      }
    } else if (pDepth === 1 && t.kind === "punct" && t.value === ",") {
      if (valStart >= 0) {
        vals.push({ start: valStart, end: T[i - 1].end });
        valStart = -1;
      }
    } else if (pDepth === 1) {
      if (valStart < 0) valStart = t.start;
    }
  }
  if (!vals.length || vals.length !== cols.length) return null;

  // Check if offset is on column i
  for (let i = 0; i < cols.length; i++) {
    const c = cols[i];
    if (offset >= c.start && offset <= c.end) {
      if (i < vals.length) {
        return { col: c, val: vals[i] };
      }
    }
  }

  // Check if offset is on value i
  for (let i = 0; i < vals.length; i++) {
    const v = vals[i];
    if (offset >= v.start && offset <= v.end) {
      if (i < cols.length) {
        return { col: cols[i], val: v };
      }
    }
  }

  return null;
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
  return match ? { col: { start: match.col.start + region.base, end: match.col.end + region.base }, val: { start: match.val.start + region.base, end: match.val.end + region.base } } : null;
}
export function findSyntaxPairHighlight(text: string, offset: number, dialect: Dialect): Span[] | null {
  const region = sqlRegionAtOffset(text, offset, dialect);
  const pairs = findSyntaxPairHighlightInRegion(region.text, region.offset, dialect);
  return pairs?.map((span) => ({ start: span.start + region.base, end: span.end + region.base })) ?? null;
}

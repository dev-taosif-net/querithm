// Token-aware SQL commenting engine across all active dialects (no regex over raw SQL).
// Uses the tier-1 lexer (`getStream`) and `statementBoundsAt` so strings, dollar-quotes, Oracle hints (`/*+ */`, `--+`),
// MySQL executable comments (`/*! */`), MySQL `#` / `-- ` rules, and nested vs non-nested block comments are respected.
// Operates strictly on local line spans and O(log N) token binary searches — never splits or copies the whole document.
import type { Dialect } from "../ipc/contract";
import { getStream, type Token, type TokenStream } from "./completion/lexer";
import { statementBoundsAt } from "./completion/sqlText";
import { knownDialect } from "../engines";

export type CommentMode = "toggle" | "add" | "remove" | "block" | "statement" | "strip" | "section" | "docHeader" | "convert";

export type CommentPlacement = "indent" | "column1";
export type MysqlLineCommentStyle = "-- " | "# ";

export interface CommentOptions {
  /** Move cursor down one line after toggling/commenting a single line with no selection. */
  advanceCursor: boolean;
  /** On Ctrl+/, a partial selection within a single code line wraps/unwraps with `/* ... *\/` instead of line-commenting. */
  inlineBlock: boolean;
  /** Where `-- ` is placed when commenting lines: aligned to common indent, or at column 1. */
  placement: CommentPlacement;
  /** Skip empty/whitespace-only lines when commenting a multi-line selection. */
  skipBlankLines: boolean;
  /** Prefix inserted when line-commenting MySQL scripts (`-- ` or `# `). */
  mysqlLineStyle: MysqlLineCommentStyle;
  /** Optional YYYY-MM-DD date override used by `docHeader` (defaults to today's local date). */
  date?: string;
}

export const DEFAULT_COMMENT_OPTIONS: CommentOptions = {
  advanceCursor: false,
  inlineBlock: true,
  placement: "indent",
  skipBlankLines: true,
  mysqlLineStyle: "-- ",
};

export interface TextEdit {
  start: number;
  end: number;
  text: string;
}

export interface SelectionSpan {
  start: number;
  end: number;
}

export interface CommentResult {
  edits: TextEdit[];
  /** Optional post-edit selections (character offsets in the resulting text). */
  nextSelections?: SelectionSpan[];
  /** Optional informational toast message (e.g. non-nested fallback, string guard, nothing to strip). */
  notice?: string;
}

export interface StatementCommentScope {
  start: number;
  end: number;
  action: "comment" | "uncomment";
  kind: "block" | "lines";
  startLine: number;
  endLine: number;
}

interface LineSpan {
  /** Offset of the first character on the line. */
  start: number;
  /** Offset right before `\r\n` or `\n` (or end of text). */
  end: number;
  /** Offset right after `\r\n` or `\n` (or end of text). */
  eolEnd: number;
}

const DIALECT_LABEL: Record<Dialect, string> = {
  tsql: "SQL Server",
  pg: "PostgreSQL",
  mySql: "MySQL",
  oracle: "Oracle",
  sqlite: "SQLite",
  duckDb: "DuckDB",
};

function supportsNestedBlockComments(d: Dialect): boolean {
  return d === "tsql" || d === "pg" || d === "duckDb";
}

function linePrefixFor(d: Dialect, opts: CommentOptions): string {
  return d === "mySql" ? opts.mysqlLineStyle : "-- ";
}

function isHorizontalSpaceCode(c: number): boolean {
  return c === 32 || c === 9;
}

function isHorizontalSpace(ch: string | undefined): boolean {
  return ch === " " || ch === "\t";
}

/** O(lineLength) line span lookup without splitting the document. */
function lineSpanAt(text: string, offset: number): LineSpan {
  const n = text.length;
  const off = Math.max(0, Math.min(offset, n));
  const start = off > 0 ? text.lastIndexOf("\n", off - 1) + 1 : 0;
  const nl = text.indexOf("\n", start);
  if (nl < 0) return { start, end: n, eolEnd: n };
  const end = nl > start && text.charCodeAt(nl - 1) === 13 ? nl - 1 : nl;
  return { start, end, eolEnd: nl + 1 };
}

function prevLineSpan(text: string, line: LineSpan): LineSpan | null {
  if (line.start === 0) return null;
  return lineSpanAt(text, line.start - 1);
}

function nextLineSpan(text: string, line: LineSpan): LineSpan | null {
  if (line.eolEnd >= text.length) {
    // Trailing newline at EOF yields a final empty line only when line had a line break.
    if (line.end < text.length) return { start: text.length, end: text.length, eolEnd: text.length };
    return null;
  }
  return lineSpanAt(text, line.eolEnd);
}

/** Collects only the lines touched by `[sel.start, sel.end]` (standard editor rule: end at column 1 excludes that line). */
function linesForSelection(text: string, sel: SelectionSpan): LineSpan[] {
  const first = lineSpanAt(text, sel.start);
  if (sel.start === sel.end) return [first];
  const lastRaw = lineSpanAt(text, sel.end);
  const lastEnd = sel.end > sel.start && lastRaw.start > first.start && sel.end === lastRaw.start
    ? (prevLineSpan(text, lastRaw) ?? first)
    : lastRaw;
  const out: LineSpan[] = [first];
  let cur = first;
  while (cur.start < lastEnd.start) {
    const next = nextLineSpan(text, cur);
    if (!next) break;
    out.push(next);
    cur = next;
  }
  return out;
}

function countNewlinesBetween(text: string, from: number, to: number): number {
  let line = 0;
  let pos = from - 1;
  while ((pos = text.indexOf("\n", pos + 1)) >= 0 && pos < to) line++;
  return line;
}

function firstNonSpaceOffset(text: string, line: LineSpan): number {
  let p = line.start;
  while (p < line.end && isHorizontalSpaceCode(text.charCodeAt(p))) p++;
  return p;
}

function isBlankLine(text: string, line: LineSpan): boolean {
  return firstNonSpaceOffset(text, line) === line.end;
}

function isHintOrVersionedComment(text: string, tok: Token, dialect: Dialect): boolean {
  if (dialect === "oracle") {
    if (tok.line && text.startsWith("--+", tok.start)) return true;
    if (!tok.line && text.startsWith("/*+", tok.start)) return true;
  }
  if (dialect === "mySql" && !tok.line && text.startsWith("/*!", tok.start)) return true;
  return false;
}

function stringContaining(code: Token[], offset: number): Token | undefined {
  let lo = 0;
  let hi = code.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const t = code[mid];
    if (t.end <= offset) lo = mid + 1;
    else if (t.start >= offset) hi = mid - 1;
    else return t.kind === "string" ? t : undefined;
  }
  return undefined;
}

function lineCommentAt(comments: Token[], offset: number): Token | undefined {
  let lo = 0;
  let hi = comments.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const t = comments[mid];
    if (t.start < offset) lo = mid + 1;
    else if (t.start > offset) hi = mid - 1;
    else return t.line ? t : undefined;
  }
  return undefined;
}

/** O(log M) binary search for a block comment covering `[start, end]`. */
function blockCommentCovering(comments: Token[], start: number, end: number): Token | undefined {
  let lo = 0;
  let hi = comments.length - 1;
  let idx = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (comments[mid].start <= start) {
      idx = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  if (idx < 0) return undefined;
  const t = comments[idx];
  return !t.line && t.start <= start && end <= t.end ? t : undefined;
}

/** First token index whose `end > offset` (binary search over sorted non-overlapping tokens). */
function firstTokenEndingAfter(tokens: Token[], offset: number): number {
  let lo = 0;
  let hi = tokens.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (tokens[mid].end <= offset) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

/** Length of the removable line comment marker at `firstNonSpace`, or 0 if not a removable line comment. */
function removableLineMarkerLength(text: string, line: LineSpan, stream: TokenStream, dialect: Dialect): number {
  const pos = firstNonSpaceOffset(text, line);
  if (pos >= line.end) return 0;
  const tok = lineCommentAt(stream.comments, pos);
  if (!tok || isHintOrVersionedComment(text, tok, dialect)) return 0;
  if (text.startsWith("--", pos)) {
    return pos + 2 < line.end && isHorizontalSpaceCode(text.charCodeAt(pos + 2)) ? 3 : 2;
  }
  if (dialect === "mySql" && text.charCodeAt(pos) === 35 /* # */) {
    return pos + 1 < line.end && isHorizontalSpaceCode(text.charCodeAt(pos + 1)) ? 2 : 1;
  }
  return 0;
}

function unwrapBlockCommentEdits(text: string, tok: Token): TextEdit[] {
  const edits: TextEdit[] = [];
  if (text.startsWith("/*", tok.start)) {
    const hasOpenSpace = tok.start + 2 < tok.end && text.charCodeAt(tok.start + 2) === 32;
    const openEnd = hasOpenSpace && (tok.open || tok.start + 3 <= tok.end - 2) ? tok.start + 3 : tok.start + 2;
    edits.push({ start: tok.start, end: openEnd, text: "" });
  }
  if (!tok.open && tok.end >= tok.start + 4 && text.slice(tok.end - 2, tok.end) === "*/") {
    const openEnd = edits[0]?.end ?? tok.start + 2;
    const hasCloseSpace = tok.end - 3 >= openEnd && text.charCodeAt(tok.end - 3) === 32;
    const closeStart = hasCloseSpace ? tok.end - 3 : tok.end - 2;
    edits.push({ start: closeStart, end: tok.end, text: "" });
  }
  return edits;
}

function trimSpan(text: string, sel: SelectionSpan): SelectionSpan {
  let start = sel.start;
  let end = sel.end;
  while (start < end) {
    const c = text.charCodeAt(start);
    if (c === 32 || c === 9 || c === 10 || c === 13) start++;
    else break;
  }
  while (end > start) {
    const c = text.charCodeAt(end - 1);
    if (c === 32 || c === 9 || c === 10 || c === 13) end--;
    else break;
  }
  return { start, end };
}

function commonIndentLength(text: string, spans: LineSpan[]): number {
  let min = Infinity;
  for (let i = 0; i < spans.length; i++) {
    const li = spans[i];
    const first = firstNonSpaceOffset(text, li);
    if (first === li.end) continue;
    const indent = first - li.start;
    if (indent < min) min = indent;
  }
  return Number.isFinite(min) ? min : 0;
}

function commentLineSpans(
  text: string,
  spans: LineSpan[],
  action: "toggle" | "add" | "remove",
  stream: TokenStream,
  dialect: Dialect,
  opts: CommentOptions,
): { edits: TextEdit[]; notice?: string } {
  if (spans.length === 0) return { edits: [] };
  // Guard: if the first touched line starts strictly inside a multi-line string literal, inserting `--` would corrupt data.
  if (stringContaining(stream.code, spans[0].start)) {
    return { edits: [], notice: "Cursor is inside a string literal — skipped commenting." };
  }

  const multi = spans.length > 1;
  const nonBlank = multi ? spans.filter((li) => !isBlankLine(text, li)) : isBlankLine(text, spans[0]) ? [] : spans;
  const targetSpans = multi && opts.skipBlankLines && nonBlank.length > 0 ? nonBlank : spans;

  let shouldUncomment: boolean;
  if (action === "add") {
    shouldUncomment = false;
  } else if (action === "remove") {
    shouldUncomment = true;
  } else {
    const checkSet = nonBlank.length > 0 ? nonBlank : spans;
    shouldUncomment = checkSet.every((li) => removableLineMarkerLength(text, li, stream, dialect) > 0);
  }

  const edits: TextEdit[] = [];
  if (shouldUncomment) {
    for (let i = 0; i < spans.length; i++) {
      const li = spans[i];
      const len = removableLineMarkerLength(text, li, stream, dialect);
      if (len > 0) {
        const pos = firstNonSpaceOffset(text, li);
        edits.push({ start: pos, end: pos + len, text: "" });
      }
    }
  } else {
    const prefix = linePrefixFor(dialect, opts);
    const col = opts.placement === "column1" ? 0 : commonIndentLength(text, targetSpans);
    for (let i = 0; i < targetSpans.length; i++) {
      const li = targetSpans[i];
      const at = li.start + Math.min(col, li.end - li.start);
      edits.push({ start: at, end: at, text: prefix });
    }
  }
  return { edits };
}

// Checks if wrapping raw text inside `/* ... */` would break syntax due to `*/` (non-nested) or unbalanced `/*`/`*/` (nested).
function hasUnsafeBlockDelimiters(body: string, nested: boolean): boolean {
  if (!nested) return body.includes("*/");
  let depth = 0;
  for (let i = 0; i < body.length - 1; i++) {
    const c = body.charCodeAt(i);
    const c1 = body.charCodeAt(i + 1);
    if (c === 47 /* / */ && c1 === 42 /* * */) {
      depth++;
      i++;
    } else if (c === 42 /* * */ && c1 === 47 /* / */) {
      depth--;
      if (depth < 0) return true;
      i++;
    }
  }
  return depth !== 0;
}

// O(log M + K) check for block delimiters inside `[start, end]` that would break wrapping in `/* ... */`.
function hasUnsafeInnerBlockComment(text: string, stream: TokenStream, start: number, end: number, nested: boolean): boolean {
  const comments = stream.comments;
  for (let i = firstTokenEndingAfter(comments, start); i < comments.length; i++) {
    const c = comments[i];
    if (c.start >= end) break;
    if (!c.line) {
      if (!nested) {
        if (c.end <= end && !c.open && text.slice(c.end - 2, c.end) === "*/") return true;
      } else {
        // On nested dialects, an inner block comment only stays balanced if it lies completely within [start, end] and is closed.
        if (c.open || c.start < start || c.end > end) return true;
      }
    }
  }
  return false;
}

function blockCommentSelection(
  text: string,
  sel: SelectionSpan,
  stream: TokenStream,
  dialect: Dialect,
  opts: CommentOptions,
): CommentResult {
  const empty = sel.start === sel.end;
  if (empty) {
    const enclosing = blockCommentCovering(stream.comments, sel.start, sel.start);
    if (enclosing && !isHintOrVersionedComment(text, enclosing, dialect)) {
      return { edits: unwrapBlockCommentEdits(text, enclosing) };
    }
    const li = lineSpanAt(text, sel.start);
    if (stringContaining(stream.code, sel.start)) {
      return { edits: [], notice: "Cursor is inside a string literal — skipped commenting." };
    }
    if (isBlankLine(text, li)) {
      return {
        edits: [{ start: sel.start, end: sel.start, text: "/*  */" }],
        nextSelections: [{ start: sel.start + 3, end: sel.start + 3 }],
      };
    }
    const start = firstNonSpaceOffset(text, li);
    let end = li.end;
    while (end > start && isHorizontalSpaceCode(text.charCodeAt(end - 1))) end--;
    return blockCommentSelection(text, { start, end }, stream, dialect, opts);
  }

  const trimmed = trimSpan(text, sel);
  if (trimmed.start < trimmed.end) {
    const covering = blockCommentCovering(stream.comments, trimmed.start, trimmed.end);
    if (covering && !isHintOrVersionedComment(text, covering, dialect)) {
      return { edits: unwrapBlockCommentEdits(text, covering) };
    }
  }

  if (stringContaining(stream.code, sel.start) || stringContaining(stream.code, sel.end)) {
    return { edits: [], notice: "Selection crosses a string literal — skipped commenting." };
  }

  const nested = supportsNestedBlockComments(dialect);
  if (hasUnsafeInnerBlockComment(text, stream, sel.start, sel.end, nested)) {
    const spans = linesForSelection(text, sel);
    const res = commentLineSpans(text, spans, "add", stream, dialect, opts);
    return {
      edits: res.edits,
      notice:
        res.notice ??
        (nested
          ? "Selection splits an existing block comment — used line comments instead."
          : `Block comments cannot nest in ${DIALECT_LABEL[dialect]} — used line comments instead.`),
    };
  }

  // On single-line selections, trim leading/trailing horizontal spaces outside `/* ... */` so wrap → unwrap is lossless.
  let wrapStart = sel.start;
  let wrapEnd = sel.end;
  const startLine = lineSpanAt(text, sel.start);
  if (sel.end <= startLine.end && trimmed.start < trimmed.end) {
    wrapStart = trimmed.start;
    wrapEnd = trimmed.end;
  }

  const openText = isHorizontalSpace(text[wrapStart]) || text[wrapStart] === "\n" || text[wrapStart] === "\r" ? "/*" : "/* ";
  const closeText = isHorizontalSpace(text[wrapEnd - 1]) || text[wrapEnd - 1] === "\n" || text[wrapEnd - 1] === "\r" ? "*/" : " */";
  return {
    edits: [
      { start: wrapStart, end: wrapStart, text: openText },
      { start: wrapEnd, end: wrapEnd, text: closeText },
    ],
  };
}

function isInlinePartialSelection(text: string, sel: SelectionSpan): boolean {
  if (sel.start >= sel.end) return false;
  const li = lineSpanAt(text, sel.start);
  if (sel.end > li.end) return false;
  const trimmed = trimSpan(text, sel);
  if (trimmed.start >= trimmed.end) return false;
  const lineFirst = firstNonSpaceOffset(text, li);
  let lineLast = li.end;
  while (lineLast > lineFirst && isHorizontalSpaceCode(text.charCodeAt(lineLast - 1))) lineLast--;
  return sel.start > lineFirst || sel.end < lineLast;
}

function resolveStatementCommentScope(
  text: string,
  offset: number,
  dialect: Dialect,
  stream: TokenStream,
): Omit<StatementCommentScope, "startLine" | "endLine"> | null {
  const off = Math.max(0, Math.min(offset, text.length));

  // 1. Inside a removable block comment → unwrap it.
  const block = blockCommentCovering(stream.comments, off, off);
  if (block && !isHintOrVersionedComment(text, block, dialect)) {
    return { start: block.start, end: block.end, action: "uncomment", kind: "block" };
  }

  // 2. On a line-commented line → uncomment the contiguous commented block around the cursor.
  const cur = lineSpanAt(text, off);
  if (removableLineMarkerLength(text, cur, stream, dialect) > 0) {
    let top = cur;
    let bot = cur;
    let p = prevLineSpan(text, top);
    while (p && removableLineMarkerLength(text, p, stream, dialect) > 0) {
      top = p;
      p = prevLineSpan(text, top);
    }
    let nx = nextLineSpan(text, bot);
    while (nx && removableLineMarkerLength(text, nx, stream, dialect) > 0) {
      bot = nx;
      nx = nextLineSpan(text, bot);
    }
    return { start: top.start, end: bot.end, action: "uncomment", kind: "lines" };
  }

  // 3. Otherwise locate the SQL statement at the cursor via token-based bounds.
  const bounds = statementBoundsAt(text, off, dialect);
  if (bounds.end <= bounds.start || !text.slice(bounds.start, bounds.end).trim()) return null;
  const first = lineSpanAt(text, bounds.start);
  const last = lineSpanAt(text, Math.max(bounds.start, bounds.end - 1));
  return { start: first.start, end: last.end, action: "comment", kind: "lines" };
}

/** Resolves what "Toggle current statement comment" will act on (used for both execution and hover preview). */
export function statementCommentScope(text: string, offset: number, dialect: Dialect): StatementCommentScope | null {
  const d = knownDialect(dialect);
  const stream = getStream(text, d);
  const base = resolveStatementCommentScope(text, offset, d, stream);
  if (!base) return null;
  const startLine = countNewlinesBetween(text, 0, base.start);
  const endLine = startLine + countNewlinesBetween(text, base.start, Math.max(base.start, base.end - 1));
  return { ...base, startLine, endLine };
}

/** O(log N) check: does `[rangeStart, rangeEnd)` overlap any code token or any non-removed comment token? */
function hasKeptTokenInRange(stream: TokenStream, targetSet: Set<Token>, rangeStart: number, rangeEnd: number): boolean {
  const ci = firstTokenEndingAfter(stream.code, rangeStart);
  if (ci < stream.code.length && stream.code[ci].start < rangeEnd) return true;
  for (let mi = firstTokenEndingAfter(stream.comments, rangeStart); mi < stream.comments.length; mi++) {
    const c = stream.comments[mi];
    if (c.start >= rangeEnd) break;
    if (!targetSet.has(c)) return true;
  }
  return false;
}

function stripComments(
  text: string,
  selections: SelectionSpan[],
  stream: TokenStream,
  dialect: Dialect,
): CommentResult {
  const hasSelection = selections.some((s) => s.start < s.end);
  const targets: Token[] = [];
  const targetSet = new Set<Token>();

  if (!hasSelection) {
    for (let i = 0; i < stream.comments.length; i++) {
      const c = stream.comments[i];
      if (!isHintOrVersionedComment(text, c, dialect)) {
        targets.push(c);
        targetSet.add(c);
      }
    }
  } else {
    for (const r of selections) {
      if (r.start >= r.end) continue;
      for (let i = firstTokenEndingAfter(stream.comments, r.start); i < stream.comments.length; i++) {
        const c = stream.comments[i];
        if (c.start >= r.end) break;
        if (!targetSet.has(c) && !isHintOrVersionedComment(text, c, dialect)) {
          targets.push(c);
          targetSet.add(c);
        }
      }
    }
    targets.sort((a, b) => a.start - b.start);
  }

  if (targets.length === 0) {
    return { edits: [], notice: "No removable comments found." };
  }

  const rawEdits: TextEdit[] = [];
  for (let i = 0; i < targets.length; i++) {
    const tok = targets[i];
    const lineA = lineSpanAt(text, tok.start);
    const lineB = tok.end - 1 <= lineA.end ? lineA : lineSpanAt(text, Math.max(tok.start, tok.end - 1));

    // Whole comment-only line(s): remove the line(s) including line break cleanly.
    if (!hasKeptTokenInRange(stream, targetSet, lineA.start, lineB.end)) {
      const start = lineA.start;
      const end = lineB.eolEnd;
      if (end === text.length && end === lineB.end && lineA.start > 0) {
        const prev = prevLineSpan(text, lineA);
        rawEdits.push({ start: prev ? prev.end : start, end, text: "" });
      } else {
        rawEdits.push({ start, end, text: "" });
      }
      continue;
    }

    // Check kept (code or preserved hint/versioned) tokens on lineA before tok and lineB after tok,
    // so multiple adjacent comments like `SELECT 1 /* a */ /* b */` leave zero trailing spaces.
    const hasBeforeOnA = hasKeptTokenInRange(stream, targetSet, lineA.start, tok.start);
    const hasAfterOnB = hasKeptTokenInRange(stream, targetSet, tok.end, lineB.end);

    if (hasBeforeOnA && !hasAfterOnB) {
      let wsStart = tok.start;
      while (wsStart > lineA.start && isHorizontalSpaceCode(text.charCodeAt(wsStart - 1))) wsStart--;
      rawEdits.push({ start: wsStart, end: tok.end, text: "" });
    } else if (!hasBeforeOnA && hasAfterOnB && lineA.start === lineB.start) {
      let wsEnd = tok.end;
      while (wsEnd < lineB.end && isHorizontalSpaceCode(text.charCodeAt(wsEnd))) wsEnd++;
      rawEdits.push({ start: tok.start, end: wsEnd, text: "" });
    } else {
      const prevCh = tok.start > 0 ? text[tok.start - 1] : undefined;
      const nextCh = tok.end < text.length ? text[tok.end] : undefined;
      const prevSpace = prevCh === undefined || isHorizontalSpace(prevCh) || prevCh === "\n" || prevCh === "\r";
      const nextSpace = nextCh === undefined || isHorizontalSpace(nextCh) || nextCh === "\n" || nextCh === "\r";
      if (!prevSpace && !nextSpace) {
        rawEdits.push({ start: tok.start, end: tok.end, text: " " });
      } else if (isHorizontalSpace(prevCh) && isHorizontalSpace(nextCh)) {
        rawEdits.push({ start: tok.start, end: tok.end + 1, text: "" });
      } else {
        rawEdits.push({ start: tok.start, end: tok.end, text: "" });
      }
    }
  }

  return { edits: mergeEdits(rawEdits) };
}

function detectEol(text: string): string {
  const nl = text.indexOf("\n");
  return nl > 0 && text.charCodeAt(nl - 1) === 13 ? "\r\n" : "\n";
}

function insertSectionHeader(
  text: string,
  sel: SelectionSpan,
  dialect: Dialect,
  opts: CommentOptions,
): CommentResult {
  const prefix = linePrefixFor(dialect, opts);
  const eol = detectEol(text);
  const curLine = lineSpanAt(text, sel.start);
  const indentEnd = firstNonSpaceOffset(text, curLine);
  const indent = text.slice(curLine.start, indentEnd);

  const hasSingleLineSel = sel.start < sel.end && sel.end <= curLine.end;
  const selectedLabel = hasSingleLineSel ? text.slice(sel.start, sel.end).trim() : "";
  const label = selectedLabel || "Section";
  const dashCount = Math.max(4, 52 - label.length);
  const banner = `${indent}${prefix}── ${label} ${"─".repeat(dashCount)}`;

  if (hasSingleLineSel && selectedLabel) {
    let lineLast = curLine.end;
    while (lineLast > indentEnd && isHorizontalSpaceCode(text.charCodeAt(lineLast - 1))) lineLast--;
    if (sel.start <= indentEnd && sel.end >= lineLast) {
      return {
        edits: [{ start: curLine.start, end: curLine.end, text: banner }],
        nextSelections: [{ start: curLine.start + banner.length, end: curLine.start + banner.length }],
      };
    }
  }

  const labelOffset = `${indent}${prefix}── `.length;
  if (indentEnd === curLine.end) {
    const start = curLine.start;
    return {
      edits: [{ start, end: curLine.end, text: banner }],
      nextSelections: [{ start: start + labelOffset, end: start + labelOffset + label.length }],
    };
  }

  const insertAt = curLine.start;
  return {
    edits: [{ start: insertAt, end: insertAt, text: `${banner}${eol}` }],
    nextSelections: [{ start: insertAt + labelOffset, end: insertAt + labelOffset + label.length }],
  };
}

function todayIsoDate(): string {
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, "0");
  const d = String(now.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

function insertDocHeader(text: string, sel: SelectionSpan, opts: CommentOptions): CommentResult {
  const eol = detectEol(text);
  const curLine = lineSpanAt(text, sel.start);
  const indentEnd = firstNonSpaceOffset(text, curLine);
  const indent = text.slice(curLine.start, indentEnd);
  const date = opts.date ?? todayIsoDate();

  const hasSingleLineSel = sel.start < sel.end && sel.end <= curLine.end;
  const selectedSummary = hasSingleLineSel ? text.slice(sel.start, sel.end).trim().replace(/\*\//g, "* /") : "";
  const summary = selectedSummary || "Summary";
  const block = [
    `${indent}/*`,
    `${indent} * ${summary}`,
    `${indent} * Date: ${date}`,
    `${indent} */`,
  ].join(eol);
  const summaryOffset = `${indent}/*${eol}${indent} * `.length;

  if (indentEnd === curLine.end) {
    const start = curLine.start;
    return {
      edits: [{ start, end: curLine.end, text: block }],
      nextSelections: [{ start: start + summaryOffset, end: start + summaryOffset + summary.length }],
    };
  }

  const insertAt = curLine.start;
  return {
    edits: [{ start: insertAt, end: insertAt, text: `${block}${eol}` }],
    nextSelections: [{ start: insertAt + summaryOffset, end: insertAt + summaryOffset + summary.length }],
  };
}

function convertCommentStyle(
  text: string,
  sel: SelectionSpan,
  stream: TokenStream,
  dialect: Dialect,
  opts: CommentOptions,
): CommentResult {
  const eol = detectEol(text);
  const trimmed = sel.start < sel.end ? trimSpan(text, sel) : sel;

  // 1. Block comment (`/* ... */`) -> line comments (`-- ` or `# `).
  const block = blockCommentCovering(stream.comments, trimmed.start, trimmed.end);
  if (block && !isHintOrVersionedComment(text, block, dialect)) {
    const endLine = lineSpanAt(text, Math.max(block.start, block.end - 1));
    let hasTrailingCode = false;
    for (let p = block.end; p < endLine.end; p++) {
      if (!isHorizontalSpaceCode(text.charCodeAt(p))) { hasTrailingCode = true; break; }
    }
    if (hasTrailingCode) {
      return { edits: [], notice: "Cannot convert an inline block comment with code after it to a line comment." };
    }

    const startLine = lineSpanAt(text, block.start);
    const startIndentEnd = firstNonSpaceOffset(text, startLine);
    const baseIndent = text.slice(startLine.start, startIndentEnd);
    const prefix = linePrefixFor(dialect, opts);
    const rawInner = text.slice(
      block.start + (text.startsWith("/*", block.start) ? 2 : 0),
      block.end - (!block.open && text.slice(block.end - 2, block.end) === "*/" ? 2 : 0),
    );
    const rawLines = rawInner.split(/\r?\n/);

    if (rawLines.length === 1) {
      const body = rawLines[0].trim();
      const replacement = body ? `${prefix}${body}` : prefix.trimEnd();
      return { edits: [{ start: block.start, end: block.end, text: replacement }] };
    }

    const cleaned = rawLines.map((l) => l.replace(/^\s*\*\s?/, ""));
    while (cleaned.length > 0 && cleaned[0].trim() === "") cleaned.shift();
    while (cleaned.length > 0 && cleaned[cleaned.length - 1].trim() === "") cleaned.pop();
    const outLines = cleaned.length > 0
      ? cleaned.map((l) => (l.trim() === "" ? `${baseIndent}${prefix.trimEnd()}` : `${baseIndent}${prefix}${l.trimStart()}`))
      : [`${baseIndent}${prefix.trimEnd()}`];

    if (block.start === startIndentEnd) {
      return { edits: [{ start: startLine.start, end: block.end, text: outLines.join(eol) }] };
    }
    const firstStripped = outLines[0].slice(baseIndent.length);
    return { edits: [{ start: block.start, end: block.end, text: [firstStripped, ...outLines.slice(1)].join(eol) }] };
  }

  // 2. Line comment(s) (`--` or MySQL `#`) -> block comment (`/* ... */`).
  let spans: LineSpan[];
  if (sel.start === sel.end) {
    const cur = lineSpanAt(text, sel.start);
    if (removableLineMarkerLength(text, cur, stream, dialect) === 0) {
      return { edits: [], notice: "Place the cursor on a comment to convert between -- and /* */." };
    }
    let top = cur;
    let bot = cur;
    let p = prevLineSpan(text, top);
    while (p && removableLineMarkerLength(text, p, stream, dialect) > 0) {
      top = p;
      p = prevLineSpan(text, top);
    }
    let nx = nextLineSpan(text, bot);
    while (nx && removableLineMarkerLength(text, nx, stream, dialect) > 0) {
      bot = nx;
      nx = nextLineSpan(text, bot);
    }
    spans = linesForSelection(text, { start: top.start, end: bot.end });
  } else {
    spans = linesForSelection(text, sel);
    let hasCommented = false;
    for (const li of spans) {
      if (isBlankLine(text, li)) continue;
      if (removableLineMarkerLength(text, li, stream, dialect) === 0) {
        return { edits: [], notice: "Place the cursor on a comment to convert between -- and /* */." };
      }
      hasCommented = true;
    }
    if (!hasCommented) {
      return { edits: [], notice: "Place the cursor on a comment to convert between -- and /* */." };
    }
  }

  const baseIndentLen = commonIndentLength(text, spans);
  const indentSource = spans.find((li) => !isBlankLine(text, li)) ?? spans[0];
  const baseIndent = text.slice(indentSource.start, indentSource.start + Math.min(baseIndentLen, indentSource.end - indentSource.start));
  const bodies: string[] = [];
  for (const li of spans) {
    if (isBlankLine(text, li)) {
      bodies.push("");
      continue;
    }
    const pos = firstNonSpaceOffset(text, li);
    const len = removableLineMarkerLength(text, li, stream, dialect);
    const extraIndent = text.slice(li.start + baseIndent.length, pos);
    bodies.push(extraIndent + text.slice(pos + len, li.end));
  }

  const nested = supportsNestedBlockComments(dialect);
  if (hasUnsafeBlockDelimiters(bodies.join("\n"), nested)) {
    return {
      edits: [],
      notice: nested
        ? "Comment text contains unbalanced '/*' or '*/' — cannot convert to a block comment."
        : `Comment text contains '*/' — cannot convert to a block comment in ${DIALECT_LABEL[dialect]}.`,
    };
  }

  const replacement =
    bodies.length === 1
      ? `${baseIndent}/* ${bodies[0].trim()} */`
      : [`${baseIndent}/*`, ...bodies.map((b) => (b.trim() === "" ? "" : `${baseIndent}  ${b}`)), `${baseIndent}*/`].join(eol);

  return { edits: [{ start: spans[0].start, end: spans[spans.length - 1].end, text: replacement }] };
}

/** Sorts edits in ascending offset order and merges overlapping/redundant edits so Monaco `executeEdits` always succeeds. */
function mergeEdits(edits: TextEdit[]): TextEdit[] {
  if (edits.length <= 1) return edits;
  const sorted = [...edits].sort((a, b) => a.start - b.start || a.end - b.end);
  const out: TextEdit[] = [{ ...sorted[0] }];
  for (let i = 1; i < sorted.length; i++) {
    const prev = out[out.length - 1];
    const cur = sorted[i];
    if (cur.start < prev.end) {
      // Truly overlapping edits: extend end and preserve a separating space if either edit needed one.
      prev.end = Math.max(prev.end, cur.end);
      if (prev.text === "" && cur.text === " ") prev.text = " ";
    } else if (cur.start === prev.end) {
      // Adjacent edits: coalesce pure deletions or adjacent separator spaces, but never drop distinct insertions.
      if (prev.text === "" && cur.text === "") {
        prev.end = cur.end;
      } else if (prev.text === " " && cur.text === " ") {
        prev.end = cur.end;
      } else if (prev.text === " " && cur.text === "" && cur.end > cur.start) {
        prev.end = cur.end;
      } else if (prev.text === "" && cur.text === " " && prev.end > prev.start) {
        prev.end = cur.end;
        prev.text = " ";
      } else {
        out.push({ ...cur });
      }
    } else {
      out.push({ ...cur });
    }
  }
  return out;
}

/** Applies a set of sorted non-overlapping offset edits to `text` in O(N) using chunk assembly. */
export function applyTextEdits(text: string, edits: TextEdit[]): string {
  if (edits.length === 0) return text;
  const sorted = edits.length > 1 ? [...edits].sort((a, b) => a.start - b.start || a.end - b.end) : edits;
  const parts: string[] = [];
  let cursor = 0;
  for (let i = 0; i < sorted.length; i++) {
    const e = sorted[i];
    if (e.start > cursor) parts.push(text.slice(cursor, e.start));
    if (e.text) parts.push(e.text);
    cursor = e.end;
  }
  if (cursor < text.length) parts.push(text.slice(cursor));
  return parts.join("");
}

/**
 * Coalesces a batch of edits into a single contiguous range replacement `[first.start .. last.end]`
 * in O(spanLength) so Monaco performs 1 piece-tree edit instead of thousands on large selections.
 */
export function coalesceEditsForRange(text: string, edits: TextEdit[]): TextEdit {
  if (edits.length === 0) return { start: 0, end: 0, text: "" };
  const sorted = edits.length > 1 ? [...edits].sort((a, b) => a.start - b.start || a.end - b.end) : edits;
  const first = sorted[0];
  const last = sorted[sorted.length - 1];
  const parts: string[] = [];
  let cursor = first.start;
  for (let i = 0; i < sorted.length; i++) {
    const e = sorted[i];
    if (e.start > cursor) parts.push(text.slice(cursor, e.start));
    if (e.text) parts.push(e.text);
    cursor = e.end;
  }
  return { start: first.start, end: last.end, text: parts.join("") };
}

/**
 * Computes token-aware comment edits for `text` under `dialect` and `selections`.
 * Pure function: touches neither DOM nor Monaco, so every engine and edge case is unit-tested directly.
 */
export function computeCommentEdits(
  text: string,
  selections: SelectionSpan[],
  dialect: Dialect,
  mode: CommentMode,
  options: Partial<CommentOptions> = {},
): CommentResult {
  const opts: CommentOptions = { ...DEFAULT_COMMENT_OPTIONS, ...options };
  const d = knownDialect(dialect);
  const n = text.length;
  const sels: SelectionSpan[] =
    selections.length > 0
      ? selections.map((s) => {
          const a = Math.max(0, Math.min(Number.isFinite(s.start) ? s.start : 0, n));
          const b = Math.max(0, Math.min(Number.isFinite(s.end) ? s.end : 0, n));
          return a <= b ? { start: a, end: b } : { start: b, end: a };
        })
      : [{ start: 0, end: 0 }];

  // Fast paths that never need document tokenization.
  if (mode === "section") {
    return insertSectionHeader(text, sels[0], d, opts);
  }
  if (mode === "docHeader") {
    return insertDocHeader(text, sels[0], opts);
  }

  const stream = getStream(text, d);

  if (mode === "strip") {
    return stripComments(text, sels, stream, d);
  }

  if (mode === "convert") {
    return convertCommentStyle(text, sels[0], stream, d, opts);
  }

  if (mode === "statement") {
    const scope = resolveStatementCommentScope(text, sels[0].start, d, stream);
    if (!scope) return { edits: [], notice: "No SQL statement at cursor." };
    if (scope.kind === "block") {
      const tok = blockCommentCovering(stream.comments, scope.start, scope.end);
      return tok ? { edits: unwrapBlockCommentEdits(text, tok) } : { edits: [] };
    }
    const spans = linesForSelection(text, { start: scope.start, end: scope.end });
    const res = commentLineSpans(text, spans, scope.action === "uncomment" ? "remove" : "add", stream, d, opts);
    return { edits: mergeEdits(res.edits), notice: res.notice };
  }

  if (mode === "block") {
    const allEdits: TextEdit[] = [];
    let notice: string | undefined;
    let nextSelections: SelectionSpan[] | undefined;
    const ordered = sels.length > 1 ? [...sels].sort((a, b) => a.start - b.start || a.end - b.end) : sels;
    const handledBlockLines = sels.length > 1 ? new Set<number>() : null;
    let lastRangeEnd = -1;
    for (const sel of ordered) {
      if (handledBlockLines) {
        if (sel.start === sel.end) {
          const li = lineSpanAt(text, sel.start);
          if (handledBlockLines.has(li.start)) continue;
          handledBlockLines.add(li.start);
          lastRangeEnd = Math.max(lastRangeEnd, li.end);
        } else {
          if (sel.start < lastRangeEnd) continue;
          lastRangeEnd = sel.end;
          for (const li of linesForSelection(text, sel)) handledBlockLines.add(li.start);
        }
      }
      const res = blockCommentSelection(text, sel, stream, d, opts);
      allEdits.push(...res.edits);
      if (res.notice && !notice) notice = res.notice;
      if (res.nextSelections && sels.length === 1) nextSelections = res.nextSelections;
    }
    return { edits: mergeEdits(allEdits), nextSelections, notice };
  }

  // Line modes: "toggle", "add", "remove".
  const allEdits: TextEdit[] = [];
  let notice: string | undefined;
  const handledLineStarts = sels.length > 1 ? new Set<number>() : null;

  for (const sel of sels) {
    // 1. Toggle or remove when the whole trimmed selection is a single block comment → unwrap it.
    if ((mode === "toggle" || mode === "remove") && sel.start < sel.end) {
      const trimmed = trimSpan(text, sel);
      if (trimmed.start < trimmed.end) {
        const block = blockCommentCovering(stream.comments, trimmed.start, trimmed.end);
        if (block && block.start === trimmed.start && block.end === trimmed.end && !isHintOrVersionedComment(text, block, d)) {
          allEdits.push(...unwrapBlockCommentEdits(text, block));
          continue;
        }
      }
    }

    // 2. Smart inline block comment on Ctrl+/ when a partial single-line span is selected.
    if (mode === "toggle" && opts.inlineBlock && isInlinePartialSelection(text, sel)) {
      const res = blockCommentSelection(text, sel, stream, d, opts);
      allEdits.push(...res.edits);
      if (res.notice && !notice) notice = res.notice;
      continue;
    }

    // 3. Full-line / multi-line / cursor-line comment operation.
    let spans = linesForSelection(text, sel);
    if (handledLineStarts) {
      spans = spans.filter((li) => !handledLineStarts.has(li.start));
      if (spans.length === 0) continue;
      for (let i = 0; i < spans.length; i++) handledLineStarts.add(spans[i].start);
    }

    const res = commentLineSpans(text, spans, mode, stream, d, opts);
    allEdits.push(...res.edits);
    if (res.notice && !notice) notice = res.notice;
  }

  const edits = mergeEdits(allEdits);

  // Advance cursor to the next line in O(1) without copying or re-splitting the document.
  let nextSelections: SelectionSpan[] | undefined;
  if (opts.advanceCursor && sels.length === 1 && sels[0].start === sels[0].end && edits.length > 0) {
    const curLine = lineSpanAt(text, sels[0].start);
    const nextLine = nextLineSpan(text, curLine);
    if (nextLine) {
      const colInLine = sels[0].start - curLine.start;
      let delta = 0;
      for (let i = 0; i < edits.length; i++) {
        const e = edits[i];
        if (e.end <= nextLine.start) delta += e.text.length - (e.end - e.start);
      }
      const nextPos = nextLine.start + delta + Math.min(colInLine, nextLine.end - nextLine.start);
      nextSelections = [{ start: nextPos, end: nextPos }];
    }
  }

  return { edits, nextSelections, notice };
}

// Hand-written tolerant SQL tokenizer for the tier-1 engine (§9), with an incremental per-text token cache.
// Never throws: unterminated strings/comments/identifiers become tokens flagged `open`.
import type { Dialect } from "../../ipc/contract";
import { reservedWords } from "./keywords";

export type TokenKind =
  | "keyword" | "ident" | "qident" | "variable" | "param" | "string" | "number" | "comment" | "operator" | "punct" | "go";

export interface Token {
  kind: TokenKind;
  start: number;
  end: number;
  /** Uppercased text for keyword/ident tokens ('' otherwise). */
  upper: string;
  /** Identifier value without brackets/quotes (ident, qident, variable, param); operator/punct text; GO text. */
  value: string;
  /** The closing delimiter is missing (string, comment, qident). */
  open?: boolean;
  /** Dollar-quote tag length, e.g. 2 for `$$` (pg strings only). */
  tag?: number;
  /** Line comment (`--`). */
  line?: boolean;
  /** GO repeat count. */
  count?: number;
}

export interface TokenStream {
  text: string;
  dialect: Dialect;
  /** Code tokens (no comments), sorted by start. */
  code: Token[];
  /** Comment tokens, sorted by start. */
  comments: Token[];
  /** Code tokens that are DECLARE / CREATE / ALTER words or GO separators, sorted (fast batch/declaration lookup). */
  marks: Token[];
}

function isMark(t: Token): boolean {
  if (t.kind === "go") return true;
  if (t.kind !== "keyword" && t.kind !== "ident") return false;
  const u = t.upper;
  return u === "DECLARE" || u === "CREATE" || u === "ALTER";
}

const C_NL = 10, C_CR = 13, C_SP = 32, C_TAB = 9;

function isSpace(c: number): boolean {
  return c === C_SP || c === C_TAB || c === C_NL || c === C_CR || c === 11 || c === 12 || c === 0xa0 || c === 0xfeff ||
    c === 0x2028 || c === 0x2029;
}
function isDigit(c: number): boolean { return c >= 48 && c <= 57; }
function isAlpha(c: number): boolean { return (c >= 65 && c <= 90) || (c >= 97 && c <= 122); }
/** `tsql`: names may start with `#` and hold `@`/`#` (temp tables, `@vars` stay variables). `dollarStart`: MySQL `$`. */
function isIdentStart(c: number, tsql: boolean, dollarStart = false): boolean {
  return isAlpha(c) || c === 95 || (tsql && c === 35) || (dollarStart && c === 36) || (c > 127 && !isSpace(c));
}
function isIdentPart(c: number, tsql: boolean): boolean {
  return isAlpha(c) || isDigit(c) || c === 95 || c === 36 || (tsql && (c === 64 || c === 35)) || (c > 127 && !isSpace(c));
}

const OPS_T = ["<=", ">=", "<>", "!=", "!<", "!>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "||"];
const OPS_P = ["->>", "#>>", "<=", ">=", "<>", "!=", "||", "::", "->", "#>", "@>", "<@", "=>", "~~", "!~", "~*", "&&", "<<", ">>", ":="];
const OPS_S = ["->>", "->", "||", "==", "!=", "<>", "<=", ">=", "<<", ">>"];
const OPS_M = ["<=>", "->>", "->", ":=", "||", "&&", "<=", ">=", "<>", "!=", "<<", ">>"];
const OPS_D = ["->>", "->", "::", "//", "**", "||", "==", "!=", "<>", "<=", ">=", "<<", ">>", ":=", "=>"];

/** The lexical rules of a dialect. Each flag is one rule; the T-SQL and PostgreSQL rows reproduce the older two modes. */
interface LexRules {
  /** T-SQL names: `#temp` starts a name and `@`/`#` continue one. */
  tsql: boolean;
  ops: string[];
  /** Letters that prefix a '…' string (N'..', E'..', X'..'), and those whose string takes backslash escapes. */
  prefixes: string;
  backslashPrefixes: string;
  /** Backslash escapes in every string (MySQL), and "…" is a string rather than a name (MySQL's default sql_mode). */
  backslashStrings: boolean;
  doubleQuoteStrings: boolean;
  /** [bracketed] and `backtick` names. */
  brackets: boolean;
  backticks: boolean;
  /** `$n` parameters and `$tag$` bodies (PostgreSQL), `$name` parameters (SQLite), or all three (DuckDB). */
  dollar: "numberedAndBodies" | "named" | "all" | null;
  /** `@x` / `@@x`: a variable (T-SQL, MySQL) or a parameter (SQLite). */
  at: "variable" | "param" | null;
  /** `:name` parameters, and `?` / `?NNN` parameters. */
  colonParams: boolean;
  questionParams: boolean;
  /** `#` starts a line comment, and `--` needs whitespace after it to be one (MySQL). */
  hashComments: boolean;
  dashNeedsSpace: boolean;
  /** Block comments nest (SQL Server, PostgreSQL); in SQLite and MySQL the first `*\/` ends one. */
  nestedComments: boolean;
  /** `0x…` binary literals. */
  hexLiterals: boolean;
  /** `GO` batch separators. */
  go: boolean;
  /** Names may start with `$` (MySQL). */
  dollarStart: boolean;
}

const LEXING: Record<Dialect, LexRules> = {
  tsql: {
    tsql: true, ops: OPS_T, prefixes: "Nn", backslashPrefixes: "", backslashStrings: false, doubleQuoteStrings: false, brackets: true,
    backticks: false, dollar: null, at: "variable", colonParams: false, questionParams: false, hashComments: false,
    dashNeedsSpace: false, nestedComments: true, hexLiterals: true, go: true, dollarStart: false,
  },
  pg: {
    tsql: false, ops: OPS_P, prefixes: "NnEeBbXx", backslashPrefixes: "Ee", backslashStrings: false, doubleQuoteStrings: false,
    brackets: false, backticks: false, dollar: "numberedAndBodies", at: null, colonParams: true, questionParams: false, hashComments: false,
    dashNeedsSpace: false, nestedComments: true, hexLiterals: false, go: false, dollarStart: false,
  },
  sqlite: {
    tsql: false, ops: OPS_S, prefixes: "Xx", backslashPrefixes: "", backslashStrings: false, doubleQuoteStrings: false, brackets: true,
    backticks: true, dollar: "named", at: "param", colonParams: true, questionParams: true, hashComments: false,
    dashNeedsSpace: false, nestedComments: false, hexLiterals: true, go: false, dollarStart: false,
  },
  mySql: {
    tsql: false, ops: OPS_M, prefixes: "NnXxBb", backslashPrefixes: "", backslashStrings: true, doubleQuoteStrings: true,
    brackets: false, backticks: true, dollar: null, at: "variable", colonParams: false, questionParams: false, hashComments: true,
    dashNeedsSpace: true, nestedComments: false, hexLiterals: true, go: false, dollarStart: true,
  },
  // DuckDB's scanner is PostgreSQL's (nested comments, E'' strings, $tag$ bodies) plus $name parameters, ?, //, ** and ==;
  // [ ] are lists and { } structs, never names.
  duckDb: {
    tsql: false, ops: OPS_D, prefixes: "EeXx", backslashPrefixes: "Ee", backslashStrings: false, doubleQuoteStrings: false,
    brackets: false, backticks: false, dollar: "all", at: null, colonParams: false, questionParams: true, hashComments: false,
    dashNeedsSpace: false, nestedComments: true, hexLiterals: true, go: false, dollarStart: false,
  },
};

interface SyncState {
  min: number;
  delta: number;
  oldCode: Token[];
  oldCom: Token[];
  ci: number;
  mi: number;
  /** Set when synced: the new-text position where old tokens resume. */
  at: number;
}

/**
 * Lex `text` from `pos` appending to `code`/`comments`. With `sync`, stops at the first token start
 * (>= sync.min) that coincides with an old token start shifted by `delta` (lexer state is stateless between tokens).
 */
function lexFrom(text: string, pos: number, dialect: Dialect, code: Token[], comments: Token[], sync?: SyncState): void {
  const n = text.length;
  const rules = LEXING[dialect];
  const { tsql, ops } = rules;
  const reserved = reservedWords(dialect);
  while (pos < n) {
    const c = text.charCodeAt(pos);
    if (isSpace(c)) { pos++; continue; }
    if (sync && pos >= sync.min) {
      const target = pos - sync.delta;
      const oc = sync.oldCode, om = sync.oldCom;
      while (sync.ci < oc.length && oc[sync.ci].start < target) sync.ci++;
      while (sync.mi < om.length && om[sync.mi].start < target) sync.mi++;
      if ((sync.ci < oc.length && oc[sync.ci].start === target) || (sync.mi < om.length && om[sync.mi].start === target)) {
        sync.at = pos;
        return;
      }
    }
    const start = pos;
    const c1 = pos + 1 < n ? text.charCodeAt(pos + 1) : -1;
    // -- line comment (MySQL: only when whitespace or the end follows), # line comment (MySQL)
    const dash = c === 45 && c1 === 45 && (!rules.dashNeedsSpace || pos + 2 >= n || isSpace(text.charCodeAt(pos + 2)));
    if (dash || (c === 35 && rules.hashComments)) {
      let e = text.indexOf("\n", pos + 1);
      if (e < 0) e = n;
      else if (e > pos && text.charCodeAt(e - 1) === C_CR) e--;
      comments.push({ kind: "comment", start, end: e, upper: "", value: "", line: true });
      pos = e;
      continue;
    }
    // /* block comment */ (SQL Server and PostgreSQL nest them). MySQL's /*! … */ code is read as a comment for now.
    if (c === 47 && c1 === 42) {
      let depth = 1;
      pos += 2;
      while (pos < n && depth > 0) {
        const d = text.charCodeAt(pos);
        if (d === 42 && text.charCodeAt(pos + 1) === 47) { depth--; pos += 2; }
        else if (rules.nestedComments && d === 47 && text.charCodeAt(pos + 1) === 42) { depth++; pos += 2; }
        else pos++;
      }
      const tok: Token = { kind: "comment", start, end: Math.min(pos, n), upper: "", value: "" };
      if (depth > 0) tok.open = true;
      comments.push(tok);
      continue;
    }
    // strings: '..', prefixed N'..' / E'..' (pg, backslash escapes) / B'..' / X'..', MySQL "..." and backslash escapes
    let strStart = -1;
    let backslash = rules.backslashStrings;
    let quote = 39;
    if (c === 39 || (c === 34 && rules.doubleQuoteStrings)) { strStart = pos; quote = c; }
    else if (c1 === 39 && c < 128 && rules.prefixes.includes(text[pos])) {
      strStart = pos + 1;
      backslash ||= rules.backslashPrefixes.includes(text[pos]);
    }
    if (strStart >= 0) {
      pos = strStart + 1;
      let closed = false;
      while (pos < n) {
        const d = text.charCodeAt(pos);
        if (backslash && d === 92) { pos += 2; continue; }
        if (d === quote) {
          if (text.charCodeAt(pos + 1) === quote) { pos += 2; continue; }
          pos++;
          closed = true;
          break;
        }
        pos++;
      }
      if (pos > n) pos = n;
      const tok: Token = { kind: "string", start, end: pos, upper: "", value: "" };
      if (!closed) tok.open = true;
      code.push(tok);
      continue;
    }
    // "quoted identifier", [bracketed], `backtick`
    if (c === 34 || (rules.brackets && c === 91) || (rules.backticks && c === 96)) {
      const close = c === 91 ? 93 : c;
      pos++;
      let val = "";
      let segStart = pos;
      let closed = false;
      while (pos < n) {
        const d = text.charCodeAt(pos);
        if (d === close) {
          if (text.charCodeAt(pos + 1) === close) { val += text.slice(segStart, pos + 1); pos += 2; segStart = pos; continue; }
          val += text.slice(segStart, pos);
          pos++;
          closed = true;
          break;
        }
        if (d === C_NL || d === C_CR) break; // unterminated: stop at end of line
        pos++;
      }
      if (!closed) val += text.slice(segStart, pos);
      const tok: Token = { kind: "qident", start, end: pos, upper: val.toUpperCase(), value: val };
      if (!closed) tok.open = true;
      code.push(tok);
      continue;
    }
    // SQLite: $name parameters
    if (c === 36 && rules.dollar === "named" && isIdentStart(c1, false)) {
      pos += 2;
      while (pos < n && isIdentPart(text.charCodeAt(pos), false)) pos++;
      code.push({ kind: "param", start, end: pos, upper: "", value: text.slice(start, pos) });
      continue;
    }
    // pg: $n params and $tag$ dollar-quoted strings
    if (c === 36 && (rules.dollar === "numberedAndBodies" || rules.dollar === "all")) {
      if (isDigit(c1)) {
        pos += 2;
        while (pos < n && isDigit(text.charCodeAt(pos))) pos++;
        code.push({ kind: "param", start, end: pos, upper: "", value: text.slice(start, pos) });
        continue;
      }
      let j = pos + 1;
      if (c1 !== 36) {
        if (isAlpha(c1) || c1 === 95) {
          j++;
          while (j < n && (isAlpha(text.charCodeAt(j)) || isDigit(text.charCodeAt(j)) || text.charCodeAt(j) === 95)) j++;
        }
      }
      if (j < n && text.charCodeAt(j) === 36) {
        const tag = text.slice(pos, j + 1);
        const close = text.indexOf(tag, j + 1);
        const tok: Token = { kind: "string", start, end: close < 0 ? n : close + tag.length, upper: "", value: "", tag: tag.length };
        if (close < 0) tok.open = true;
        code.push(tok);
        pos = tok.end;
        continue;
      }
      // DuckDB: $name without a closing $ is a named parameter.
      if (rules.dollar === "all" && j > pos + 1) {
        code.push({ kind: "param", start, end: j, upper: "", value: text.slice(start, j) });
        pos = j;
        continue;
      }
    }
    // @variables / @@globals (T-SQL, MySQL); SQLite @name parameters
    if (c === 64 && rules.at === "variable") {
      pos++;
      if (pos < n && text.charCodeAt(pos) === 64) pos++;
      while (pos < n && isIdentPart(text.charCodeAt(pos), true)) pos++;
      const v = text.slice(start, pos);
      code.push({ kind: "variable", start, end: pos, upper: v.toUpperCase(), value: v });
      continue;
    }
    if (c === 64 && rules.at === "param" && isIdentStart(c1, false)) {
      pos += 2;
      while (pos < n && isIdentPart(text.charCodeAt(pos), false)) pos++;
      code.push({ kind: "param", start, end: pos, upper: "", value: text.slice(start, pos) });
      continue;
    }
    // SQLite ? and ?NNN parameters
    if (c === 63 && rules.questionParams) {
      pos++;
      while (pos < n && isDigit(text.charCodeAt(pos))) pos++;
      code.push({ kind: "param", start, end: pos, upper: "", value: text.slice(start, pos) });
      continue;
    }
    // :name parameters (not :: casts)
    if (c === 58 && rules.colonParams && c1 !== 58 && isIdentStart(c1, false) && (start === 0 || text.charCodeAt(start - 1) !== 58)) {
      pos += 2;
      while (pos < n && isIdentPart(text.charCodeAt(pos), false)) pos++;
      code.push({ kind: "param", start, end: pos, upper: "", value: text.slice(start, pos) });
      continue;
    }
    // numbers
    if (isDigit(c) || (c === 46 && isDigit(c1))) {
      if (rules.hexLiterals && c === 48 && (c1 === 120 || c1 === 88)) {
        pos += 2;
        while (pos < n && (isDigit(text.charCodeAt(pos)) || isAlpha(text.charCodeAt(pos)))) pos++;
      } else {
        while (pos < n && isDigit(text.charCodeAt(pos))) pos++;
        if (pos < n && text.charCodeAt(pos) === 46) { pos++; while (pos < n && isDigit(text.charCodeAt(pos))) pos++; }
        const e = pos < n ? text.charCodeAt(pos) : -1;
        if (e === 101 || e === 69) {
          let q = pos + 1;
          const s = q < n ? text.charCodeAt(q) : -1;
          if (s === 43 || s === 45) q++;
          if (q < n && isDigit(text.charCodeAt(q))) { pos = q; while (pos < n && isDigit(text.charCodeAt(pos))) pos++; }
        }
      }
      code.push({ kind: "number", start, end: pos, upper: "", value: text.slice(start, pos) });
      continue;
    }
    // words
    if (isIdentStart(c, tsql, rules.dollarStart)) {
      pos++;
      while (pos < n && isIdentPart(text.charCodeAt(pos), tsql)) pos++;
      const word = text.slice(start, pos);
      const upper = word.toUpperCase();
      if (rules.go && upper === "GO" && isGoLine(text, start, pos)) {
        let q = pos;
        while (q < n && (text.charCodeAt(q) === C_SP || text.charCodeAt(q) === C_TAB)) q++;
        let count: number | undefined;
        if (q < n && isDigit(text.charCodeAt(q))) {
          const ds = q;
          while (q < n && isDigit(text.charCodeAt(q))) q++;
          count = Number(text.slice(ds, q));
          pos = q;
        }
        const tok: Token = { kind: "go", start, end: pos, upper: "GO", value: text.slice(start, pos) };
        if (count !== undefined) tok.count = count;
        code.push(tok);
        continue;
      }
      code.push({ kind: reserved.has(upper) ? "keyword" : "ident", start, end: pos, upper, value: word });
      continue;
    }
    // punctuation
    if (c === 40 || c === 41 || c === 44 || c === 59 || c === 46) {
      pos++;
      code.push({ kind: "punct", start, end: pos, upper: "", value: text[start] });
      continue;
    }
    // operators
    let op = "";
    for (const o of ops) {
      if (text.startsWith(o, pos)) { op = o; break; }
    }
    if (!op) op = text[pos];
    pos += op.length;
    code.push({ kind: "operator", start, end: pos, upper: "", value: op });
  }
}

/** `GO` must be alone on its line, optionally followed by a count and/or a line comment. */
function isGoLine(text: string, start: number, end: number): boolean {
  for (let i = start - 1; i >= 0; i--) {
    const c = text.charCodeAt(i);
    if (c === C_NL) break;
    if (c !== C_SP && c !== C_TAB && c !== C_CR && c !== 0xfeff) return false;
  }
  const n = text.length;
  let q = end;
  while (q < n && (text.charCodeAt(q) === C_SP || text.charCodeAt(q) === C_TAB)) q++;
  while (q < n && isDigit(text.charCodeAt(q))) q++;
  while (q < n && (text.charCodeAt(q) === C_SP || text.charCodeAt(q) === C_TAB)) q++;
  if (q >= n) return true;
  const c = text.charCodeAt(q);
  if (c === C_NL || c === C_CR) return true;
  return c === 45 && text.charCodeAt(q + 1) === 45;
}

/** Tokenize the whole text (fresh arrays; comments included, in source order). */
export function tokenize(text: string, dialect: Dialect): Token[] {
  const code: Token[] = [];
  const comments: Token[] = [];
  try {
    lexFrom(text ?? "", 0, dialect, code, comments);
  } catch {
    /* tolerant: return what we have */
  }
  if (comments.length === 0) return code;
  const all: Token[] = new Array(code.length + comments.length);
  let i = 0, j = 0, k = 0;
  while (i < code.length || j < comments.length) {
    if (j >= comments.length || (i < code.length && code[i].start < comments[j].start)) all[k++] = code[i++];
    else all[k++] = comments[j++];
  }
  return all;
}

// ---------------------------------------------------------------------------------------------
// Incremental token cache. Typing in a 12k-line script re-lexes only the edited line(s): the common
// prefix/suffix with the previous text is found with chunked native compares, old tokens before the edit
// are kept, and old tokens after the edit are reused (offset-shifted in place) once the lexer re-syncs.
// ---------------------------------------------------------------------------------------------

const CACHE_SIZE = 4;
const cache: TokenStream[] = [];

function commonPrefix(a: string, b: string): number {
  const n = Math.min(a.length, b.length);
  const C = 2048;
  let i = 0;
  while (i + C <= n && a.slice(i, i + C) === b.slice(i, i + C)) i += C;
  while (i < n && a.charCodeAt(i) === b.charCodeAt(i)) i++;
  return i;
}

function commonSuffix(a: string, b: string, max: number): number {
  const la = a.length, lb = b.length;
  const C = 2048;
  let i = 0;
  while (i + C <= max && a.slice(la - i - C, la - i) === b.slice(lb - i - C, lb - i)) i += C;
  while (i < max && a.charCodeAt(la - 1 - i) === b.charCodeAt(lb - 1 - i)) i++;
  return i;
}

function fullLex(text: string, dialect: Dialect): TokenStream {
  const code: Token[] = [];
  const comments: Token[] = [];
  try { lexFrom(text, 0, dialect, code, comments); } catch { /* tolerant */ }
  return { text, dialect, code, comments, marks: code.filter(isMark) };
}

/** First index in `arr` whose token ends after `pos` (i.e. contains or follows pos). */
function firstEndingAfter(arr: Token[], pos: number): number {
  let lo = 0, hi = arr.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (arr[mid].end <= pos) lo = mid + 1; else hi = mid;
  }
  return lo;
}

function firstStartingAtOrAfter(arr: Token[], pos: number): number {
  let lo = 0, hi = arr.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (arr[mid].start < pos) lo = mid + 1; else hi = mid;
  }
  return lo;
}

function incremental(old: TokenStream, text: string, p: number, s: number): TokenStream {
  const ot = old.text;
  const delta = text.length - ot.length;
  // Restart at the start of the line containing the first change (GO detection looks at the whole line),
  // or earlier if a multi-line token (string/comment) spans that line start.
  let lineStart = p;
  while (lineStart > 0 && ot.charCodeAt(lineStart - 1) !== C_NL) lineStart--;
  let r = lineStart;
  const ic = firstEndingAfter(old.code, lineStart);
  const im = firstEndingAfter(old.comments, lineStart);
  if (ic < old.code.length && old.code[ic].start < r) r = old.code[ic].start;
  if (im < old.comments.length && old.comments[im].start < r) r = old.comments[im].start;
  const keepC = firstStartingAtOrAfter(old.code, r);
  const keepM = firstStartingAtOrAfter(old.comments, r);
  // Sync only after the end of the line holding the end of the edit (in new-text coordinates).
  const editEndNew = text.length - s;
  let min = text.indexOf("\n", editEndNew);
  min = min < 0 ? text.length : min + 1;
  const newC: Token[] = [];
  const newM: Token[] = [];
  const sync: SyncState = { min, delta, oldCode: old.code, oldCom: old.comments, ci: keepC, mi: keepM, at: -1 };
  lexFrom(text, r, old.dialect, newC, newM, sync);
  let code: Token[];
  let comments: Token[];
  // marks: computed before the tail is shifted (old coordinates)
  const oldTarget = sync.at >= 0 ? sync.at - delta : Infinity;
  const marks: Token[] = [];
  for (const m of old.marks) { if (m.start < r) marks.push(m); else break; }
  for (const t of newC) if (isMark(t)) marks.push(t);
  if (sync.at >= 0) {
    let i = firstStartingAtOrAfter(old.marks, oldTarget);
    for (; i < old.marks.length; i++) marks.push(old.marks[i]);
  }
  if (sync.at >= 0) {
    // Splice in place: the old stream is evicted from the cache, so its arrays can be reused.
    code = spliceShift(old.code, keepC, sync.ci, newC, delta);
    comments = spliceShift(old.comments, keepM, sync.mi, newM, delta);
  } else {
    code = old.code.slice(0, keepC).concat(newC);
    comments = old.comments.slice(0, keepM).concat(newM);
  }
  return { text, dialect: old.dialect, code, comments, marks };
}

function spliceShift(arr: Token[], from: number, to: number, ins: Token[], delta: number): Token[] {
  const removed = to - from;
  if (ins.length <= 4096) arr.splice(from, removed, ...ins);
  else arr = arr.slice(0, from).concat(ins, arr.slice(to));
  if (delta !== 0) {
    for (let i = from + ins.length; i < arr.length; i++) {
      const t = arr[i];
      t.start += delta;
      t.end += delta;
    }
  }
  return arr;
}

/** Cached token stream for `text` (shared, do not mutate). */
export function getStream(text: string, dialect: Dialect): TokenStream {
  for (let i = 0; i < cache.length; i++) {
    const e = cache[i];
    if (e.dialect === dialect && e.text === text) {
      if (i > 0) { cache.splice(i, 1); cache.unshift(e); }
      return e;
    }
  }
  let best = -1, bestP = 0, bestS = 0, bestScore = -1;
  for (let i = 0; i < cache.length; i++) {
    const e = cache[i];
    if (e.dialect !== dialect) continue;
    const p = commonPrefix(e.text, text);
    const s = commonSuffix(e.text, text, Math.min(e.text.length, text.length) - p);
    if (p + s > bestScore) { bestScore = p + s; best = i; bestP = p; bestS = s; }
  }
  let stream: TokenStream;
  if (best >= 0 && bestScore >= Math.min(text.length, cache[best].text.length) * 0.5 && text.length > 2000) {
    const old = cache[best];
    cache.splice(best, 1); // its tokens are mutated (shifted) below; the old text's entry is no longer valid
    try {
      stream = incremental(old, text, bestP, bestS);
    } catch {
      stream = fullLex(text, dialect);
    }
  } else {
    stream = fullLex(text, dialect);
  }
  cache.unshift(stream);
  if (cache.length > CACHE_SIZE) cache.length = CACHE_SIZE;
  return stream;
}

/** Test hook: drop all cached token streams. */
export function clearTokenCache(): void {
  cache.length = 0;
}

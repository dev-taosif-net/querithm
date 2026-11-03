// Keyword case as you type (§9): when a word boundary is typed, recase the reserved keyword just before it.
// Token-based: words inside strings, comments, quoted identifiers, variables or after a `.` are never touched.
import type { Dialect, KeywordCase } from "../../ipc/contract";
import { getStream } from "./lexer";

export interface CaseEdit {
  start: number;
  end: number;
  text: string;
}

export function applyKeywordCase(word: string, mode: KeywordCase): string {
  return mode === "upper" ? word.toUpperCase() : mode === "lower" ? word.toLowerCase() : word;
}

/** The edit recasing the keyword token ending exactly at `offset` in `text`, or null when nothing should change. */
export function keywordCaseEdit(text: string, dialect: Dialect, offset: number, mode: KeywordCase): CaseEdit | null {
  if (mode === "preserve" || offset <= 0) return null;
  const code = getStream(text, dialect).code;
  let lo = 0, hi = code.length - 1, i = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (code[mid].end <= offset) { i = mid; lo = mid + 1; } else hi = mid - 1;
  }
  if (i < 0) return null;
  const tok = code[i];
  if (tok.kind !== "keyword" || tok.end !== offset) return null;
  const prev = code[i - 1];
  if (prev && prev.kind === "punct" && prev.value === ".") return null;
  const word = text.slice(tok.start, tok.end);
  const want = applyKeywordCase(word, mode);
  return want === word ? null : { start: tok.start, end: tok.end, text: want };
}

// The formatted text of a JSON or XML value as lines with depth and fold ranges (Phase C2). Typed arrays, so a worker
// can hand the result over without copying and a 10 MB value stays compact. Pure: no DOM.

export interface StructuredView {
  /** Every line, joined with "\n". */
  text: string;
  /** Offset of each line's first character in `text`. */
  starts: Uint32Array;
  /** Nesting depth of each line (its indent is two spaces per level, already in the text). */
  depth: Uint16Array;
  /** For a line that opens a block: the index of the line that closes it; otherwise -1. */
  foldEnd: Int32Array;
}

export const lineCount = (v: StructuredView) => v.starts.length;

export function lineText(v: StructuredView, i: number): string {
  const end = i + 1 < v.starts.length ? v.starts[i + 1] - 1 : v.text.length;
  return v.text.slice(v.starts[i], end);
}

/** Collects formatted lines; `fold` links an opening line to its closing line. */
export class LineBuilder {
  private lines: string[] = [];
  private depths: number[] = [];
  private folds = new Map<number, number>();

  get count() {
    return this.lines.length;
  }

  push(depth: number, text: string): number {
    this.lines.push("  ".repeat(depth) + text);
    this.depths.push(depth);
    return this.lines.length - 1;
  }

  /** Appends to the last line (a separating comma). */
  appendToLast(text: string) {
    if (this.lines.length) this.lines[this.lines.length - 1] += text;
  }

  fold(open: number, close: number) {
    if (close > open) this.folds.set(open, close);
  }

  build(): StructuredView {
    const n = this.lines.length;
    const starts = new Uint32Array(n);
    const depth = new Uint16Array(n);
    const foldEnd = new Int32Array(n).fill(-1);
    let off = 0;
    for (let i = 0; i < n; i++) {
      starts[i] = off;
      off += this.lines[i].length + 1;
      depth[i] = Math.min(this.depths[i], 0xffff);
    }
    for (const [open, close] of this.folds) foldEnd[open] = close;
    return { text: this.lines.join("\n"), starts, depth, foldEnd };
  }
}

/** Lines left on screen with the blocks in `collapsed` (opening-line indexes) folded: the opening line stays, the rest of the block goes. */
export function visibleLines(v: StructuredView, collapsed: ReadonlySet<number>): Uint32Array {
  const n = v.starts.length;
  if (!collapsed.size) {
    const all = new Uint32Array(n);
    for (let i = 0; i < n; i++) all[i] = i;
    return all;
  }
  const out: number[] = [];
  for (let i = 0; i < n; i++) {
    out.push(i);
    if (collapsed.has(i) && v.foldEnd[i] > i) i = v.foldEnd[i];
  }
  return Uint32Array.from(out);
}

/** Case-insensitive search: the lines that contain `needle` (in order) and the total number of occurrences. */
export function findMatches(v: StructuredView, needle: string): { lines: Uint32Array; total: number } {
  const q = needle.toLowerCase();
  if (!q) return { lines: new Uint32Array(0), total: 0 };
  const hay = v.text.toLowerCase();
  const lines: number[] = [];
  let total = 0;
  let line = 0;
  for (let at = hay.indexOf(q); at >= 0; at = hay.indexOf(q, at + q.length)) {
    while (line + 1 < v.starts.length && v.starts[line + 1] <= at) line++;
    total++;
    if (lines[lines.length - 1] !== line) lines.push(line);
  }
  return { lines: Uint32Array.from(lines), total };
}

/** `collapsed` without the folds that hide `line`, so a search hit can be shown. Same set when nothing hides it. */
export function reveal(v: StructuredView, collapsed: ReadonlySet<number>, line: number): ReadonlySet<number> {
  let next: Set<number> | null = null;
  for (const open of collapsed) {
    if (open < line && v.foldEnd[open] >= line) (next ??= new Set(collapsed)).delete(open);
  }
  return next ?? collapsed;
}

/** "{ … }" / "<a> … </a>" text for a folded opening line: the opening text and the closing line, trimmed. */
export function foldedText(v: StructuredView, line: number): string {
  const end = v.foldEnd[line];
  return end > line ? `${lineText(v, line)} … ${lineText(v, end).trim()}` : lineText(v, line);
}

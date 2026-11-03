import type { GridSelection } from "@glideapps/glide-data-grid";

export interface SelectionStats {
  count: number;
  sampled?: boolean;
  /** NULL cells among those read; a NULL is not the text 'NULL' and not an empty string. */
  nullCount?: number;
  /** Distinct values (NULL counts as one) among those read. */
  distinct?: number;
  numericCount: number;
  sum: number;
  avg: number;
  min: number;
  max: number;
}
export function formatStatNumber(n: number): string {
  if (Number.isInteger(n)) return n.toLocaleString();
  return Number(n.toFixed(4)).toLocaleString(undefined, { maximumFractionDigits: 4 });
}
function* range(start: number, end: number) { for (let n = start; n < end; n++) yield n; }

/** Iterates at most 50k cells; selecting a million-row column never allocates a million indices. */
export function selectionStats(sel: GridSelection, totalRows: number, kinds: readonly string[], textAt: (row: number, col: number) => string | null): SelectionStats | null {
  let rows: Iterable<number>, cols: number[], rowCount: number;
  if (sel.columns.length) {
    rows = range(0, totalRows); rowCount = totalRows;
    cols = [...sel.columns].filter((c) => c >= 0 && c < kinds.length);
  } else if (sel.rows.length) {
    const clipped = sel.rows.remove([totalRows, Number.MAX_SAFE_INTEGER]);
    rows = clipped; rowCount = clipped.length;
    cols = [...range(0, kinds.length)];
  } else if (sel.current) {
    const r = sel.current.range;
    const start = Math.max(0, r.y), end = Math.min(totalRows, r.y + r.height);
    rows = range(start, end); rowCount = Math.max(0, end - start);
    cols = [...range(Math.max(0, r.x), Math.min(kinds.length, r.x + r.width))];
  } else return null;
  const count = rowCount * cols.length;
  if (count <= 1) return null;
  let checked = 0, numericCount = 0, sum = 0, min = Infinity, max = -Infinity, nullCount = 0;
  const seen = new Set<string | null>();
  outer: for (const r of rows) for (const c of cols) {
    if (checked++ === 50_000) break outer;
    const text = textAt(r, c);
    if (text == null) nullCount++;
    seen.add(text);
    if (kinds[c] !== "number") continue;
    if (text == null || text.trim() === "" || text.length > 128) continue;
    const n = Number(text);
    if (!Number.isFinite(n)) continue;
    numericCount++; sum += n; min = Math.min(min, n); max = Math.max(max, n);
  }
  return { count, sampled: count > 50_000, nullCount, distinct: seen.size, numericCount, sum, avg: numericCount ? sum / numericCount : 0, min: numericCount ? min : 0, max: numericCount ? max : 0 };
}

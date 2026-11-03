// Jupyter import (Phase C4): SQL cells come from `%%sql` code cells, markdown cells stay markdown, everything else
// is skipped and counted. Strictly validated (nbformat 4, the same size limits as a .sqlbook); outputs are never read,
// and the `%%sql` line itself is dropped — it may carry a connection string with a password.
import { LIMITS, newCellId, utf8Bytes, type NotebookCellFile } from "./format";

export type IpynbResult = { ok: true; cells: NotebookCellFile[]; skipped: number } | { ok: false; reason: string };

const isObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);

function sourceOf(v: unknown): string | null {
  if (typeof v === "string") return v;
  if (Array.isArray(v) && v.every((x) => typeof x === "string")) return v.join("");
  return null;
}

export function parseIpynb(text: string): IpynbResult {
  if (utf8Bytes(text, LIMITS.fileBytes) > LIMITS.fileBytes) return { ok: false, reason: "The file is larger than 20 MB." };
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (e) {
    return { ok: false, reason: `The file is not valid JSON: ${e instanceof Error ? e.message : String(e)}` };
  }
  if (!isObject(raw) || !Array.isArray(raw.cells)) return { ok: false, reason: "This is not a Jupyter notebook (no cell list)." };
  if (raw.nbformat !== 4) return { ok: false, reason: `Jupyter notebook format ${JSON.stringify(raw.nbformat)?.slice(0, 10)} is not supported; version 4 is.` };
  if (raw.cells.length > LIMITS.cells) return { ok: false, reason: `The notebook has ${raw.cells.length.toLocaleString()} cells; up to ${LIMITS.cells.toLocaleString()} can be imported.` };
  const cells: NotebookCellFile[] = [];
  let skipped = 0;
  for (let i = 0; i < raw.cells.length; i++) {
    const c = raw.cells[i];
    if (!isObject(c) || typeof c.cell_type !== "string") return { ok: false, reason: `Cell ${i + 1} is not a Jupyter cell.` };
    const source = sourceOf(c.source);
    if (source == null) return { ok: false, reason: `Cell ${i + 1} has no readable source.` };
    if (utf8Bytes(source, LIMITS.sourceBytes) > LIMITS.sourceBytes) return { ok: false, reason: `Cell ${i + 1} is larger than 1 MB.` };
    if (c.cell_type === "markdown") {
      cells.push({ id: newCellId(), kind: "markdown", source });
      continue;
    }
    const magic = /^\s*%%sql\b[^\n]*(\n|$)/.exec(source);
    if (c.cell_type === "code" && magic) {
      cells.push({ id: newCellId(), kind: "sql", source: source.slice(magic[0].length).replace(/\s+$/, "") });
      continue;
    }
    skipped++;
  }
  return { ok: true, cells, skipped };
}

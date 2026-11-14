import { Table, tableToIPC } from "apache-arrow";
import type { ArrowResultSet } from "./model";
import ExportWorker from "./export.worker?worker";
import type { ExportFormat, ExportRequest } from "./export.worker";
import { isTauri } from "../ipc/transport";
import { toast } from "../store/ui";
import { saveBytes } from "../app/saveBytes";

export const exportFormats: { format: ExportFormat; label: string; filter: string; mime: string }[] = [
  { format: "xlsx", label: "Excel (.xlsx)", filter: "Excel workbook", mime: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
  { format: "csv", label: "CSV", filter: "CSV", mime: "text/csv" },
  { format: "tsv", label: "TSV", filter: "TSV", mime: "text/tab-separated-values" },
  { format: "json", label: "JSON", filter: "JSON", mime: "application/json" },
];

const toBuffer = (ipc: Uint8Array) => ipc.buffer.slice(ipc.byteOffset, ipc.byteOffset + ipc.byteLength) as ArrayBuffer;

/** A set's rows as Arrow IPC streams; a set without rows sends its schema, so its export still has the header. */
function setBuffers(rs: ArrowResultSet): ArrayBuffer[] {
  if (rs.batches.length) return rs.batches.map((b) => toBuffer(tableToIPC(new Table([b]), "stream")));
  return rs.schema ? [toBuffer(tableToIPC(new Table(rs.schema), "stream"))] : [];
}

function inWorker(request: ExportRequest, transfer: ArrayBuffer[]): Promise<Uint8Array> {
  const worker = new ExportWorker();
  return new Promise<Uint8Array>((resolve, reject) => {
    worker.onmessage = (e) => (e.data instanceof Uint8Array ? resolve(e.data) : reject(new Error((e.data as { error: string }).error)));
    worker.onerror = (e) => reject(new Error(e.message));
    worker.postMessage(request, transfer);
  }).finally(() => worker.terminate());
}

/** Exports a result set to Excel/CSV/TSV/JSON in a web worker, then saves it through the shell's dialog + fs plugins. */
export async function exportResultSet(rs: ArrowResultSet, format: ExportFormat, baseName: string) {
  const info = exportFormats.find((f) => f.format === format)!;
  if (!rs.batches.length) return;
  const batches = setBuffers(rs);
  const bytes = await inWorker({ format, batches, sheetName: baseName }, batches);
  const path = await saveBytes(`${baseName}.${format}`, bytes, { name: info.filter, extension: format, mime: info.mime });
  if (path && isTauri) toast("success", `Exported ${rs.rowCount.toLocaleString()} rows to ${path}`);
}

/** The formats "Export all result sets" offers: Excel holds a sheet per set; the others are one file per set in a .zip. */
export const exportAllFormats: { format: ExportFormat; label: string }[] = [
  { format: "xlsx", label: "Excel (.xlsx), a sheet per result set" },
  { format: "csv", label: "CSV files (.zip), one per result set" },
  { format: "tsv", label: "TSV files (.zip), one per result set" },
  { format: "json", label: "JSON files (.zip), one per result set" },
];

/**
 * Every result set of a run in one saved file (one save dialog, so the shell's picked-path rule holds): a workbook with
 * a sheet per set, or a .zip with a file per set. Sets the row limit cut hold their fetched rows, and the toast says so.
 */
export async function exportAllResultSets(sets: readonly ArrowResultSet[], format: ExportFormat, baseName: string) {
  if (!sets.length) return;
  const all = sets.map((rs) => ({ name: `Result set ${rs.index + 1}`, batches: setBuffers(rs) }));
  const bytes = await inWorker({ format, batches: [], all }, all.flatMap((a) => a.batches));
  const ext = format === "xlsx" ? "xlsx" : "zip";
  const filter = format === "xlsx" ? exportFormats.find((f) => f.format === "xlsx")! : { filter: `${format.toUpperCase()} files (zip)`, mime: "application/zip" };
  const path = await saveBytes(`${baseName}-all-results.${ext}`, bytes, { name: filter.filter, extension: ext, mime: filter.mime });
  const rows = sets.reduce((n, rs) => n + rs.rowCount, 0);
  const cut = sets.filter((rs) => rs.truncated).length;
  if (path && isTauri) toast(cut ? "warn" : "success", `Exported ${sets.length} result sets (${rows.toLocaleString()} rows) to ${path}${cut ? `. The row limit cut ${cut === 1 ? "one of them" : `${cut} of them`}: they hold the fetched rows only.` : ""}`);
  else if (path && cut) toast("warn", `The row limit cut ${cut === 1 ? "one result set" : `${cut} result sets`}: the export holds the fetched rows only.`);
}

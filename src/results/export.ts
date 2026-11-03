import { Table, tableToIPC } from "apache-arrow";
import type { ArrowResultSet } from "./model";
import ExportWorker from "./export.worker?worker";
import type { ExportFormat } from "./export.worker";
import { isTauri } from "../ipc/transport";
import { toast } from "../store/ui";
import { saveBytes } from "../app/saveBytes";

export const exportFormats: { format: ExportFormat; label: string; filter: string; mime: string }[] = [
  { format: "xlsx", label: "Excel (.xlsx)", filter: "Excel workbook", mime: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
  { format: "csv", label: "CSV", filter: "CSV", mime: "text/csv" },
  { format: "tsv", label: "TSV", filter: "TSV", mime: "text/tab-separated-values" },
  { format: "json", label: "JSON", filter: "JSON", mime: "application/json" },
];

/** Exports a result set to Excel/CSV/TSV/JSON in a web worker, then saves it through the shell's dialog + fs plugins. */
export async function exportResultSet(rs: ArrowResultSet, format: ExportFormat, baseName: string) {
  const info = exportFormats.find((f) => f.format === format)!;
  if (!rs.batches.length) return;
  const batches = rs.batches.map((b) => {
    const ipc = tableToIPC(new Table([b]), "stream");
    return ipc.buffer.slice(ipc.byteOffset, ipc.byteOffset + ipc.byteLength) as ArrayBuffer;
  });
  const worker = new ExportWorker();
  const bytes = await new Promise<Uint8Array>((resolve, reject) => {
    worker.onmessage = (e) => (e.data instanceof Uint8Array ? resolve(e.data) : reject(new Error((e.data as { error: string }).error)));
    worker.onerror = (e) => reject(new Error(e.message));
    worker.postMessage({ format, batches, sheetName: baseName }, batches);
  }).finally(() => worker.terminate());
  const path = await saveBytes(`${baseName}.${format}`, bytes, { name: info.filter, extension: format, mime: info.mime });
  if (path && isTauri) toast("success", `Exported ${rs.rowCount.toLocaleString()} rows to ${path}`);
}

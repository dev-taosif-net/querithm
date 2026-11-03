// Export runs off the main thread over the same Arrow data (§7 Grid wiring): CSV, TSV, JSON or Excel.
import { tableFromIPC, Type, type Vector } from "apache-arrow";
import { buildColumns, exactNumber, formatValue, StorageClass, storageClassAt, type ColumnModel } from "./model";
import { XlsxWriter } from "./xlsx";

export type ExportFormat = "csv" | "json" | "tsv" | "xlsx";

export interface ExportRequest {
  format: ExportFormat;
  sheetName?: string;
  batches: ArrayBuffer[];
}

function csvCell(v: string | null, sep: string): string {
  if (v == null) return "";
  return /["\n\r]/.test(v) || v.includes(sep) ? `"${v.replace(/"/g, '""')}"` : v;
}

/** A dynamically typed cell's kind (Phase D0f, SQLite): integers and reals are numbers, everything else stays text. */
function dynamicKind(c: ColumnModel, vectors: Vector[], i: number): ColumnModel["kind"] | undefined {
  if (c.classField == null) return undefined;
  const cls = storageClassAt(c, vectors, i);
  return cls === StorageClass.Integer || cls === StorageClass.Real ? "number" : "text";
}

async function xlsx({ batches, sheetName }: ExportRequest): Promise<Uint8Array> {
  let writer: XlsxWriter | undefined;
  for (const buf of batches) {
    const table = tableFromIPC(new Uint8Array(buf));
    const cols = buildColumns(table.schema);
    const dynamic = cols.some((c) => c.classField != null);
    writer ??= new XlsxWriter(cols, { sheetName });
    for (const batch of table.batches) {
      const vectors = batch.schema.fields.map((_, i) => batch.getChildAt(i)!);
      for (let i = 0; i < batch.numRows; i++) {
        const values = cols.map((c) => (vectors[c.field].isValid(i) ? formatValue(c, vectors[c.field], i, vectors) : null));
        // A number Excel cannot hold exactly (beyond 15 digits, NaN) stays text: the writer checks each one.
        writer.addRow(values, dynamic ? cols.map((c) => dynamicKind(c, vectors, i)) : undefined);
      }
    }
  }
  return (writer ?? new XlsxWriter([], { sheetName })).finish();
}

self.onmessage = async (e: MessageEvent<ExportRequest>) => {
  const { format, batches } = e.data;
  if (format === "xlsx") {
    try {
      const out = await xlsx(e.data);
      (self as unknown as Worker).postMessage(out, [out.buffer]);
    } catch (err) {
      (self as unknown as Worker).postMessage({ error: err instanceof Error ? err.message : String(err) });
    }
    return;
  }
  const encoder = new TextEncoder();
  const parts: Uint8Array[] = [];
  let header = false;
  const sep = format === "tsv" ? "\t" : ",";
  if (format === "json") parts.push(encoder.encode("[\n"));
  let firstRow = true;
  for (const buf of batches) {
    const table = tableFromIPC(new Uint8Array(buf));
    const cols = buildColumns(table.schema);
    for (const batch of table.batches) {
      const vectors = batch.schema.fields.map((_, i) => batch.getChildAt(i)!);
      if (!header && format !== "json") {
        parts.push(encoder.encode(cols.map((c) => csvCell(c.name, sep)).join(sep) + "\r\n"));
        header = true;
      }
      const lines: string[] = [];
      for (let i = 0; i < batch.numRows; i++) {
        const values = cols.map((c) => (vectors[c.field].isValid(i) ? formatValue(c, vectors[c.field], i, vectors) : null));
        if (format === "json") {
          const obj: Record<string, string | number | null> = {};
          cols.forEach((c, k) => {
            const v = values[k];
            // A dynamically typed cell (SQLite) is a number when its class says so and its text converts exactly.
            if (v != null && c.classField != null) obj[c.name] = exactNumber(storageClassAt(c, vectors, i), v) ?? v;
            // A number sent as exact text (M11: wide decimals, unsigned BIGINT) stays the exact string it is.
            else obj[c.name] = v != null && c.kind === "number" && c.arrowTypeId !== Type.Utf8 && c.scale === 0 && /^-?\d{1,15}$/.test(v) ? Number(v) : v;
          });
          lines.push((firstRow ? "  " : ",\n  ") + JSON.stringify(obj));
          firstRow = false;
        } else lines.push(values.map((v) => csvCell(v, sep)).join(sep));
      }
      parts.push(encoder.encode(format === "json" ? lines.join("") : lines.join("\r\n") + (lines.length ? "\r\n" : "")));
    }
  }
  if (format === "json") parts.push(encoder.encode("\n]\n"));
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let off = 0;
  for (const p of parts) {
    out.set(p, off);
    off += p.length;
  }
  (self as unknown as Worker).postMessage(out, [out.buffer]);
};

// Export runs off the main thread over the same Arrow data (§7 Grid wiring): CSV, TSV, JSON or Excel.
import { tableFromIPC, Type, type Vector } from "apache-arrow";
import { buildColumns, exactNumber, formatValue, StorageClass, storageClassAt, type ColumnModel } from "./model";
import { XlsxWriter, zip } from "./xlsx";

export type ExportFormat = "csv" | "json" | "tsv" | "xlsx";

export interface ExportRequest {
  format: ExportFormat;
  sheetName?: string;
  batches: ArrayBuffer[];
  /**
   * "Export all result sets": every set of the run, each as Arrow IPC streams (a set without rows sends its schema).
   * Excel gets a sheet per set; CSV, TSV and JSON a file per set in one .zip. `batches` is then unused.
   */
  all?: { name: string; batches: ArrayBuffer[] }[];
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

/** Every result set in one workbook, a sheet each (more when a set passes Excel's row limit). */
async function xlsxAll(all: NonNullable<ExportRequest["all"]>): Promise<Uint8Array> {
  const writer = new XlsxWriter([]);
  for (const set of all) {
    let started = false;
    for (const buf of set.batches) {
      const table = tableFromIPC(new Uint8Array(buf));
      const cols = buildColumns(table.schema);
      const dynamic = cols.some((c) => c.classField != null);
      if (!started) writer.nextResult(cols, set.name);
      started = true;
      for (const batch of table.batches) {
        const vectors = batch.schema.fields.map((_, i) => batch.getChildAt(i)!);
        for (let i = 0; i < batch.numRows; i++) {
          const values = cols.map((c) => (vectors[c.field].isValid(i) ? formatValue(c, vectors[c.field], i, vectors) : null));
          writer.addRow(values, dynamic ? cols.map((c) => dynamicKind(c, vectors, i)) : undefined);
        }
      }
    }
    if (!started) writer.nextResult([], set.name);
  }
  return writer.finish();
}

/** File names inside the .zip: the set's name, made safe for every file system, and unique. */
export function zipEntryNames(names: readonly string[], ext: string): string[] {
  const seen = new Set<string>();
  return names.map((n) => {
    const base = n.replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").replace(/[. ]+$/, "").slice(0, 100) || "result";
    let name = `${base}.${ext}`;
    for (let i = 2; seen.has(name.toLowerCase()); i++) name = `${base} (${i}).${ext}`;
    seen.add(name.toLowerCase());
    return name;
  });
}

self.onmessage = async (e: MessageEvent<ExportRequest>) => {
  const { format, batches, all } = e.data;
  try {
    let out: Uint8Array;
    if (all) {
      if (format === "xlsx") out = await xlsxAll(all);
      else {
        const names = zipEntryNames(all.map((a) => a.name), format);
        out = await zip(all.map((a, i) => [names[i], [textFile(format, a.batches)]]));
      }
    } else out = format === "xlsx" ? await xlsx(e.data) : textFile(format, batches);
    (self as unknown as Worker).postMessage(out, [out.buffer]);
  } catch (err) {
    (self as unknown as Worker).postMessage({ error: err instanceof Error ? err.message : String(err) });
  }
};

/** CSV, TSV or JSON of one result set. The header comes from the schema, so a set without rows still has one. */
export function textFile(format: Exclude<ExportFormat, "xlsx">, batches: readonly ArrayBuffer[]): Uint8Array {
  const encoder = new TextEncoder();
  const parts: Uint8Array[] = [];
  let header = false;
  const sep = format === "tsv" ? "\t" : ",";
  if (format === "json") parts.push(encoder.encode("[\n"));
  let firstRow = true;
  for (const buf of batches) {
    const table = tableFromIPC(new Uint8Array(buf));
    const cols = buildColumns(table.schema);
    if (!header && format !== "json") {
      parts.push(encoder.encode(cols.map((c) => csvCell(c.name, sep)).join(sep) + "\r\n"));
      header = true;
    }
    for (const batch of table.batches) {
      const vectors = batch.schema.fields.map((_, i) => batch.getChildAt(i)!);
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
  return out;
}

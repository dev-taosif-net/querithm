// Excel (.xlsx) export without a dependency: SpreadsheetML parts in a zip, deflated with the platform's
// CompressionStream when it has one (stored otherwise). Runs in the export worker over formatted cell text.
import { Type } from "apache-arrow";
import type { ColumnModel } from "./model";

export type XlsxColumn = Pick<ColumnModel, "name" | "kind" | "arrowTypeId">;

/** Excel's grid: 1,048,576 rows (one is the header) and 32,767 characters per cell. */
export const MAX_SHEET_ROWS = 1_048_575;
const MAX_CELL_CHARS = 32_767;

// Style ids in styles.xml's cellXfs.
const S_HEADER = 1, S_DATETIME = 2, S_DATETIME_MS = 3, S_DATE = 4, S_TIME = 5, S_TIME_MS = 6;

const enc = new TextEncoder();

function colLetters(i: number): string {
  let s = "";
  for (let n = i + 1; n > 0; n = Math.floor((n - 1) / 26)) s = String.fromCharCode(65 + ((n - 1) % 26)) + s;
  return s;
}

/** XML-escapes text and drops characters XML 1.0 cannot carry (control chars, lone surrogates). */
export function xmlText(s: string): string {
  return s
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F￾￿]|[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g, "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

/** A number Excel stores exactly (≤ 15 significant digits), else null so the text is kept. */
function excelNumber(v: string): string | null {
  const m = /^-?(\d*)\.?(\d*)(?:[eE][+-]?\d+)?$/.exec(v);
  if (!m || (!m[1] && !m[2])) return null;
  const digits = (m[1] + m[2]).replace(/^0+/, "").replace(/0+$/, "");
  if (digits.length > 15 || !Number.isFinite(Number(v))) return null;
  return v;
}

const DAY_MS = 86_400_000;
const EXCEL_EPOCH_DAYS = 25_569; // 1970-01-01 as an Excel serial
const FIRST_SAFE_SERIAL = 61; // 1900-03-01: earlier serials hit Excel's 1900 leap-year bug

/** Excel date serial + style for a formatted temporal value; null keeps it as text (offsets, pre-1900). */
export function excelDate(v: string, typeId: Type): { serial: number; style: number } | null {
  if (typeId === Type.Time) {
    const t = /^(\d{2}):(\d{2}):(\d{2})(\.\d+)?$/.exec(v);
    if (!t || Number(t[1]) > 23) return null;
    const secs = Number(t[1]) * 3600 + Number(t[2]) * 60 + Number(t[3]) + Number(t[4] ?? 0);
    return { serial: secs / 86_400, style: t[4] ? S_TIME_MS : S_TIME };
  }
  const d = /^(\d{4})-(\d{2})-(\d{2})(?: (\d{2}):(\d{2}):(\d{2})(\.\d+)?)?$/.exec(v); // a trailing offset fails here
  if (!d) return null;
  const days = Date.UTC(Number(d[1]), Number(d[2]) - 1, Number(d[3])) / DAY_MS + EXCEL_EPOCH_DAYS;
  if (days < FIRST_SAFE_SERIAL) return null;
  if (d[4] == null) return { serial: days, style: typeId === Type.Date ? S_DATE : S_DATETIME };
  const secs = Number(d[4]) * 3600 + Number(d[5]) * 60 + Number(d[6]) + Number(d[7] ?? 0);
  return { serial: days + secs / 86_400, style: d[7] ? S_DATETIME_MS : S_DATETIME };
}

function cell(ref: string, col: XlsxColumn, v: string, kind = col.kind): string {
  if (kind === "number") {
    const n = excelNumber(v);
    if (n != null) return `<c r="${ref}"><v>${n}</v></c>`;
  } else if (kind === "bool" && (v === "0" || v === "1")) {
    return `<c r="${ref}" t="b"><v>${v}</v></c>`;
  } else if (kind === "temporal") {
    const d = excelDate(v, col.arrowTypeId);
    if (d) return `<c r="${ref}" s="${d.style}"><v>${d.serial}</v></c>`;
  }
  const text = v.length > MAX_CELL_CHARS ? v.slice(0, MAX_CELL_CHARS) : v;
  return `<c r="${ref}" t="inlineStr"><is><t xml:space="preserve">${xmlText(text)}</t></is></c>`;
}

/** Sheet names: ≤ 31 chars, none of []:*?/\ (Excel rejects the workbook otherwise). */
function sheetName(base: string, n: number): string {
  const clean = base.replace(/[[\]:*?/\\]/g, "_").trim() || "Result";
  const suffix = n > 1 ? ` (${n})` : "";
  return clean.slice(0, 31 - suffix.length) + suffix;
}

interface Sheet {
  name: string;
  chunks: Uint8Array[];
  rows: number;
  /** The last column's letters and the column count: a workbook of several result sets has a shape per sheet. */
  last: string;
  cols: number;
}

/** Streams rows into worksheets (a new one every MAX_SHEET_ROWS rows), then zips the workbook. */
export class XlsxWriter {
  private sheets: Sheet[] = [];
  private buf = "";
  private letters: string[];
  /** Sheets the current result set has filled so far (a set over Excel's row limit continues on the next one). */
  private part = 0;
  /** The last sheet still takes rows (its closing tags are not written yet). */
  private open = false;

  constructor(
    private cols: XlsxColumn[],
    private readonly opts: { sheetName?: string; maxRowsPerSheet?: number } = {},
  ) {
    this.letters = cols.map((_, i) => colLetters(i));
  }

  /**
   * Starts the next result set of a workbook with one per sheet ("Export all result sets"): its own columns and sheet
   * name. The rows added after this go to it.
   */
  nextResult(cols: XlsxColumn[], name: string) {
    this.endSheet();
    this.cols = cols;
    this.letters = cols.map((_, i) => colLetters(i));
    this.opts.sheetName = name;
    this.part = 0;
    this.startSheet();
  }

  private get max() {
    return this.opts.maxRowsPerSheet ?? MAX_SHEET_ROWS;
  }

  private startSheet() {
    this.flush();
    this.part++;
    const sheet: Sheet = { name: sheetName(this.opts.sheetName ?? "Result", this.part), chunks: [], rows: 0, last: this.letters[this.letters.length - 1] ?? "A", cols: this.cols.length };
    this.sheets.push(sheet);
    this.open = true;
    const widths = this.cols
      .map((c, i) => `<col min="${i + 1}" max="${i + 1}" width="${Math.min(60, Math.max(c.kind === "temporal" ? 20 : 10, c.name.length + 3))}" customWidth="1"/>`)
      .join("");
    const header = this.cols.map((c, i) => `<c r="${this.letters[i]}1" t="inlineStr" s="${S_HEADER}"><is><t xml:space="preserve">${xmlText(c.name)}</t></is></c>`).join("");
    this.buf =
      `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">` +
      `<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>` +
      (widths ? `<cols>${widths}</cols>` : "") +
      `<sheetData><row r="1">${header}</row>`;
  }

  private flush() {
    const sheet = this.sheets[this.sheets.length - 1];
    if (sheet && this.buf) sheet.chunks.push(enc.encode(this.buf));
    this.buf = "";
  }

  private endSheet() {
    const sheet = this.sheets[this.sheets.length - 1];
    if (!sheet || !this.open) return;
    this.open = false;
    this.buf += `</sheetData>${sheet.cols ? `<autoFilter ref="A1:${sheet.last}${sheet.rows + 1}"/>` : ""}</worksheet>`;
    this.flush();
  }

  /**
   * `kinds`: a kind per cell where it is not the column's — a dynamically typed (SQLite) cell is a number by its own
   * storage class (Phase D0f), while its column is text.
   */
  addRow(values: readonly (string | null)[], kinds?: readonly (XlsxColumn["kind"] | undefined)[]) {
    let sheet = this.sheets[this.sheets.length - 1];
    if (!sheet || sheet.rows >= this.max) {
      this.endSheet();
      this.startSheet();
      sheet = this.sheets[this.sheets.length - 1];
    }
    const r = ++sheet.rows + 1;
    let xml = `<row r="${r}">`;
    for (let i = 0; i < this.cols.length; i++) {
      const v = values[i];
      if (v != null) xml += cell(`${this.letters[i]}${r}`, this.cols[i], v, kinds?.[i] ?? this.cols[i].kind); // NULL = empty cell
    }
    this.buf += xml + "</row>";
    if (this.buf.length > 1 << 20) this.flush();
  }

  /** The hidden name Excel pairs with each sheet's header autofilter. */
  private filterName(s: Sheet, i: number) {
    const ref = `'${xmlText(s.name).replace(/'/g, "''")}'!$A$1:$${s.last}$${s.rows + 1}`;
    return `<definedName name="_xlnm._FilterDatabase" localSheetId="${i}" hidden="1">${ref}</definedName>`;
  }

  /** Number of worksheets written (more than one when the rows exceed Excel's limit). */
  get sheetCount() {
    return Math.max(1, this.sheets.length);
  }

  async finish(): Promise<Uint8Array> {
    if (!this.sheets.length) this.startSheet();
    this.endSheet();
    const n = this.sheets.length;
    const range = Array.from({ length: n }, (_, i) => i + 1);
    const xml = (s: string) => [enc.encode(`<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n${s}`)];
    const files: [string, Uint8Array[]][] = [
      ["[Content_Types].xml", xml(
        `<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">` +
        `<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>` +
        `<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>` +
        `<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>` +
        range.map((i) => `<Override PartName="/xl/worksheets/sheet${i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join("") +
        `</Types>`)],
      ["_rels/.rels", xml(
        `<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">` +
        `<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>`)],
      ["xl/workbook.xml", xml(
        `<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>` +
        this.sheets.map((s, i) => `<sheet name="${xmlText(s.name)}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`).join("") +
        `</sheets>${this.sheets.some((s) => s.cols) ? `<definedNames>${this.sheets.map((s, i) => (s.cols ? this.filterName(s, i) : "")).join("")}</definedNames>` : ""}</workbook>`)],
      ["xl/_rels/workbook.xml.rels", xml(
        `<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">` +
        range.map((i) => `<Relationship Id="rId${i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet${i}.xml"/>`).join("") +
        `<Relationship Id="rId${n + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>`)],
      ["xl/styles.xml", xml(STYLES)],
      ...this.sheets.map((s, i): [string, Uint8Array[]] => [`xl/worksheets/sheet${i + 1}.xml`, s.chunks]),
    ];
    return zip(files);
  }
}

const STYLES =
  `<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">` +
  `<numFmts count="5"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm:ss"/><numFmt numFmtId="165" formatCode="yyyy-mm-dd hh:mm:ss.000"/>` +
  `<numFmt numFmtId="166" formatCode="yyyy-mm-dd"/><numFmt numFmtId="167" formatCode="hh:mm:ss"/><numFmt numFmtId="168" formatCode="hh:mm:ss.000"/></numFmts>` +
  `<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>` +
  `<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>` +
  `<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>` +
  `<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>` +
  `<cellXfs count="7"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>` +
  [164, 165, 166, 167, 168].map((id) => `<xf numFmtId="${id}" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>`).join("") +
  `</cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>`;

// ---------------------------------------------------------------------------------------------------- zip

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

export function crc32(chunks: readonly Uint8Array[]): number {
  let c = 0xffffffff;
  for (const b of chunks) for (let i = 0; i < b.length; i++) c = CRC_TABLE[(c ^ b[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function concat(chunks: readonly Uint8Array[]): Uint8Array {
  const out = new Uint8Array(chunks.reduce((n, c) => n + c.length, 0));
  let off = 0;
  for (const c of chunks) {
    out.set(c, off);
    off += c.length;
  }
  return out;
}

/** Raw deflate through CompressionStream; null when the platform lacks it (the entry is then stored). */
async function deflateRaw(chunks: readonly Uint8Array[]): Promise<Uint8Array | null> {
  if (typeof CompressionStream === "undefined") return null;
  try {
    const cs = new CompressionStream("deflate-raw");
    const writer = cs.writable.getWriter();
    void (async () => {
      for (const c of chunks) await writer.write(c as Uint8Array<ArrayBuffer>);
      await writer.close();
    })().catch(() => {});
    return new Uint8Array(await new Response(cs.readable).arrayBuffer());
  } catch {
    return null;
  }
}

/** A plain (non-zip64) archive: up to 4 GB per entry, which a 1M-row sheet stays well under once deflated. */
export async function zip(files: [string, Uint8Array[]][]): Promise<Uint8Array> {
  const parts: Uint8Array[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;
  for (const [name, chunks] of files) {
    const nameBytes = enc.encode(name);
    const size = chunks.reduce((n, c) => n + c.length, 0);
    const crc = crc32(chunks);
    const deflated = await deflateRaw(chunks);
    const data = deflated ?? concat(chunks);
    if (size > 0xffffffff || data.length > 0xffffffff || offset > 0xffffffff) throw new Error("The export is larger than 4 GB: export it as CSV instead.");
    const method = deflated ? 8 : 0;
    const local = new DataView(new ArrayBuffer(30));
    local.setUint32(0, 0x04034b50, true);
    local.setUint16(4, 20, true);
    local.setUint16(6, 0x0800, true); // UTF-8 names
    local.setUint16(8, method, true);
    local.setUint16(12, 0x21, true); // 1980-01-01, no time
    local.setUint32(14, crc, true);
    local.setUint32(18, data.length, true);
    local.setUint32(22, size, true);
    local.setUint16(26, nameBytes.length, true);
    const dir = new DataView(new ArrayBuffer(46));
    dir.setUint32(0, 0x02014b50, true);
    dir.setUint16(4, 20, true);
    dir.setUint16(6, 20, true);
    dir.setUint16(8, 0x0800, true);
    dir.setUint16(10, method, true);
    dir.setUint16(14, 0x21, true);
    dir.setUint32(16, crc, true);
    dir.setUint32(20, data.length, true);
    dir.setUint32(24, size, true);
    dir.setUint16(28, nameBytes.length, true);
    dir.setUint32(42, offset, true);
    parts.push(new Uint8Array(local.buffer), nameBytes, data);
    central.push(new Uint8Array(dir.buffer), nameBytes);
    offset += 30 + nameBytes.length + data.length;
  }
  const dirSize = central.reduce((n, c) => n + c.length, 0);
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true);
  end.setUint16(8, files.length, true);
  end.setUint16(10, files.length, true);
  end.setUint32(12, dirSize, true);
  end.setUint32(16, offset, true);
  return concat([...parts, ...central, new Uint8Array(end.buffer)]);
}

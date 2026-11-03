// Result data held as Arrow batches (§7 Grid wiring): no row objects are materialized. Sorting and filtering
// build an index array over the batches. Instances live outside React state; components subscribe to `version`.
import { Precision, Type, type DataType, type RecordBatch, type Schema, type Vector } from "apache-arrow";

export type CellKind = "number" | "text" | "bool" | "temporal" | "binary";

/**
 * Storage class of one cell of a dynamically typed column (Phase D0f, SQLite): the value of the column's hidden
 * `<col>__class` sibling. The cell's text is lossless: integers in decimal, reals in round-trip form, blobs in hex.
 */
export const StorageClass = { Null: 0, Integer: 1, Real: 2, Text: 3, Blob: 4 } as const;
export type StorageClass = (typeof StorageClass)[keyof typeof StorageClass];

/** Bit of a storage class in the masks `classesSeen` returns. */
export const classBit = (cls: StorageClass) => 1 << cls;

export interface ColumnModel {
  name: string;
  typeName: string;
  /** Child index in each batch. */
  field: number;
  kind: CellKind;
  arrowTypeId: Type;
  /** Child index of the <col>__truncated flag, if any. */
  flagField?: number;
  /** Child index of the <col>__offset (datetimeoffset minutes), if any. */
  offsetField?: number;
  /** Child index of the <col>__class storage-class sibling (UInt8) of a dynamically typed column; `field` holds its text. */
  classField?: number;
  /** Server ordinal (for query.cell). */
  ordinal: number;
  /** null when neither the driver nor the source table says. */
  nullable: boolean | null;
  /** Table column this column reads ("schema.table.column"), when the sidecar could tell exactly. */
  source: string | null;
  pk: boolean;
  /** "schema.table.column" referenced through a foreign key. */
  fk: string | null;
  scale: number;
  tz: string | null;
  /** A Float column of single precision (Float32): shown as the shortest decimal that reads back as the same float (L12). */
  single?: boolean;
}

const NULL = null;

function decimalToString(words: Uint32Array, scale: number): string {
  let v = 0n;
  for (let k = words.length - 1; k >= 0; k--) v = (v << 32n) | BigInt(words[k]);
  const bits = BigInt(words.length * 32);
  if (words[words.length - 1] & 0x80000000) v -= 1n << bits;
  const neg = v < 0n;
  let s = (neg ? -v : v).toString();
  if (scale > 0) {
    s = s.padStart(scale + 1, "0");
    s = `${s.slice(0, -scale)}.${s.slice(-scale)}`;
  }
  return neg ? `-${s}` : s;
}

/** A Decimal128's unscaled value: exact, and within one column (one scale) it orders like the decimals (M11). */
function decimalToBigInt(words: Uint32Array): bigint {
  let v = 0n;
  for (let k = words.length - 1; k >= 0; k--) v = (v << 32n) | BigInt(words[k]);
  if (words[words.length - 1] & 0x80000000) v -= 1n << BigInt(words.length * 32);
  return v;
}

// M11: a sort key for a number sent as exact text (q:numeric): a string that compares (code unit order) like the numbers,
// with no float conversion, so 9 < 10, -0.5 < -0.05 and 40-digit values keep their order. Layout: a class letter
// ("0" -Infinity, "1" negative, "2" zero, "3" positive, "4" NaN, as PostgreSQL orders them; "5" text that is not a number),
// then for nonzero values the decimal exponent as 7 digits and the significant digits; a negative value stores both
// complemented (9 - digit) and ends in ":" (above every digit), so a longer magnitude sorts first.
const EXP_BIAS = 5_000_000;
const NUMERIC_TEXT = /^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/;

function complement(digits: string): string {
  let out = "";
  for (let i = 0; i < digits.length; i++) out += String.fromCharCode(105 - digits.charCodeAt(i)); // "0"↔"9" ("0" is 48)
  return out;
}

export function numericTextKey(text: string): string {
  const t = text.trim();
  const lower = t.toLowerCase();
  if (lower === "nan") return "4";
  if (lower === "infinity" || lower === "+infinity" || lower === "inf") return "3~";
  if (lower === "-infinity" || lower === "-inf") return "0";
  const m = NUMERIC_TEXT.exec(t);
  if (!m || (m[2] === "" && (m[3] ?? "") === "")) return `5${text}`;
  const intPart = m[2];
  const frac = m[3] ?? "";
  const all = intPart + frac;
  const first = all.search(/[1-9]/);
  if (first < 0) return "2";
  // Significant digits without the trailing zeros, and the exponent of the first one (123.4 → "1234", 3; 0.05 → "5", -1).
  const digits = all.slice(first).replace(/0+$/, "");
  const exp = intPart.length - first + (m[4] ? Number(m[4]) : 0);
  const e = String(Math.min(Math.max(exp + EXP_BIAS, 0), 9_999_999)).padStart(7, "0");
  if (m[1] !== "-") return `3${e}${digits}`;
  return `1${complement(e)}${complement(digits)}:`;
}

const pad = (n: number, w = 2) => String(n).padStart(w, "0");

function formatMicros(us: bigint, offsetMinutes: number | null, tz: string | null): string {
  const shifted = offsetMinutes != null ? us + BigInt(offsetMinutes) * 60_000_000n : us;
  let ms = Number(shifted / 1000n);
  let micro = Number(shifted % 1000n);
  if (micro < 0) {
    micro += 1000;
    ms -= 1;
  }
  const d = new Date(ms);
  const base = `${d.getUTCFullYear().toString().padStart(4, "0")}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
  let frac = `${pad(d.getUTCMilliseconds(), 3)}${micro ? pad(micro, 3) : ""}`;
  frac = frac.replace(/0+$/, "");
  const fraction = frac ? `.${frac}` : "";
  if (offsetMinutes != null) {
    const sign = offsetMinutes < 0 ? "-" : "+";
    const a = Math.abs(offsetMinutes);
    return `${base}${fraction} ${sign}${pad(Math.floor(a / 60))}:${pad(a % 60)}`;
  }
  return tz ? `${base}${fraction} +00:00` : `${base}${fraction}`;
}

/**
 * A time of day, or a MySQL / MariaDB TIME duration (L11): the sign goes in front once, hours may pass 24 (up to 838), and
 * the fraction keeps its microseconds — -02:30:00, 838:59:59.999999.
 */
function formatTimeMicros(us: bigint): string {
  const neg = us < 0n;
  const abs = neg ? -us : us;
  const s = Number(abs / 1_000_000n);
  const frac = Number(abs % 1_000_000n);
  const base = `${neg ? "-" : ""}${pad(Math.floor(s / 3600))}:${pad(Math.floor((s % 3600) / 60))}:${pad(s % 60)}`;
  return frac ? `${base}.${String(frac).padStart(6, "0").replace(/0+$/, "")}` : base;
}

/**
 * The shortest decimal that reads back as the same Float32 (L12): a REAL holding 0.1 shows 0.1, not the double
 * 0.10000000149011612 it widens to. At most 9 significant digits always round-trip.
 *
 * W9: the search costs up to 9 `toPrecision` calls. Measured (Node 24.22 on the Ryzen 7 4700U dev laptop, 1 M values
 * each): 1,174 ns per call for random float32 bit patterns, 877 ns for uniform values in [0, 1000), 560 ns for 2-decimal
 * prices — about 1 ms for 1,000 visible REAL cells each time the grid draws them all (a scroll or hover frame). So answers
 * are kept in a direct-mapped cache of F32_CACHE slots (two Float64Arrays, 256 KB): a redraw of 1,000 visible cells
 * whose window moves by 20 new cells per frame costs 98 ns per call (12× less), and a value never seen costs the same
 * as before (1,184 vs 1,179 ns). The output is the uncached function's, value for value.
 */
export function shortestFloat32(v: number): number {
  if (!Number.isFinite(v) || v === 0) return v;
  f32[0] = v;
  const bits = f32Bits[0];
  const slot = (Math.imul(bits ^ (bits >>> 15), 0x2c1b3c6d) >>> 18) & (F32_CACHE - 1);
  if (f32Keys[slot] === v) return f32Values[slot];
  const s = shortestFloat32Uncached(v);
  f32Keys[slot] = v;
  f32Values[slot] = s;
  return s;
}

/** Slots of shortestFloat32's cache (a power of two; the hash keeps 14 bits). */
const F32_CACHE = 1 << 14;
/** NaN never equals itself, so an empty slot always misses. */
const f32Keys = new Float64Array(F32_CACHE).fill(Number.NaN);
const f32Values = new Float64Array(F32_CACHE);
const f32 = new Float32Array(1);
const f32Bits = new Uint32Array(f32.buffer);

/** The search itself (exported for the test that keeps the cached answers equal to it). */
export function shortestFloat32Uncached(v: number): number {
  if (!Number.isFinite(v) || v === 0) return v;
  for (let p = 1; p < 9; p++) {
    const s = Number(v.toPrecision(p));
    if (Math.fround(s) === v) return s;
  }
  return Number(v.toPrecision(9));
}

function hex(bytes: Uint8Array, max = 64): string {
  let s = "0x";
  const n = Math.min(bytes.length, max);
  for (let i = 0; i < n; i++) s += bytes[i].toString(16).padStart(2, "0").toUpperCase();
  return bytes.length > max ? `${s}…` : s;
}

function kindOf(t: DataType): CellKind {
  switch (t.typeId) {
    case Type.Int:
    case Type.Float:
    case Type.Decimal:
      return "number";
    case Type.Bool:
      return "bool";
    case Type.Timestamp:
    case Type.Date:
    case Type.Time:
      return "temporal";
    case Type.Binary:
    case Type.LargeBinary:
      return "binary";
    default:
      return "text";
  }
}

function rawBigInt(vec: Vector, i: number): bigint {
  const data = vec.data[0];
  return (data.values as BigInt64Array)[data.offset + i];
}

export function buildColumns(schema: Schema): ColumnModel[] {
  const fields = schema.fields;
  const byName = new Map(fields.map((f, i) => [f.name, i]));
  // The sidecar names the class sibling "<col>__class" but makes the name unique on a clash; "q:of" always names its column.
  const classOf = new Map<string, number>();
  fields.forEach((f, i) => {
    const of = f.metadata.get("q:of");
    if (f.metadata.get("q:hidden") === "class" && of != null) classOf.set(of, i);
  });
  const cols: ColumnModel[] = [];
  fields.forEach((f, i) => {
    if (f.metadata.get("q:hidden")) return;
    const t = f.type as DataType & { scale?: number; timezone?: string | null; precision?: Precision };
    const dynamic = f.metadata.get("q:dynamic") === "1";
    // M11: a number the sidecar sent as exact text (beyond Decimal128 / Int64) is still a number.
    const numericText = f.metadata.get("q:numeric") === "1" && !dynamic;
    cols.push({
      name: f.name,
      typeName: f.metadata.get("q:typeName") ?? String(f.type),
      field: i,
      kind: numericText ? "number" : kindOf(f.type),
      arrowTypeId: f.type.typeId,
      flagField: byName.get(`${f.name}__truncated`),
      offsetField: byName.get(`${f.name}__offset`),
      classField: dynamic ? classOf.get(f.name) ?? byName.get(`${f.name}__class`) : undefined,
      ordinal: Number(f.metadata.get("q:ordinal") ?? cols.length),
      nullable: f.metadata.has("q:nullable") ? f.metadata.get("q:nullable") === "1" : null,
      source: f.metadata.get("q:source") ?? null,
      pk: f.metadata.get("q:pk") === "1",
      fk: f.metadata.get("q:fk") ?? null,
      scale: t.scale ?? 0,
      tz: t.timezone ?? null,
      ...(f.type.typeId === Type.Float && t.precision === Precision.SINGLE ? { single: true } : {}),
    });
  });
  return cols;
}

/** One result set: Arrow batches + an optional view (sorted/filtered row indices). */
export class ArrowResultSet {
  schema: Schema | null = null;
  columns: ColumnModel[] = [];
  batches: RecordBatch[] = [];
  /** Physical row index where each batch starts. */
  starts: number[] = [];
  rowCount = 0;
  truncated = false;
  complete = false;
  interrupted = false;
  version = 0;
  sort: { col: number; dir: "asc" | "desc" } | null = null;
  filter = "";
  /** Columns the filter searches (indices into `columns`); null = every column. */
  filterColumns: number[] | null = null;
  /** Bumped whenever view rows are re-ordered or re-filtered: a grid selection made before refers to other rows. */
  viewVersion = 0;
  private view: Uint32Array | null = null;
  private lastBatch = 0;
  private vectors: Vector[][] = [];
  private classScan = new Map<number, { batches: number; mask: number }>();
  private listeners = new Set<() => void>();

  constructor(readonly index: number) {}

  subscribe = (cb: () => void) => {
    this.listeners.add(cb);
    return () => this.listeners.delete(cb);
  };

  getVersion = () => this.version;

  private changed() {
    this.version++;
    this.listeners.forEach((l) => l());
  }

  setSchema(schema: Schema) {
    if (this.schema) return;
    this.schema = schema;
    this.columns = buildColumns(schema);
    this.changed();
  }

  append(batch: RecordBatch) {
    if (!this.schema) this.setSchema(batch.schema);
    this.starts.push(this.rowCount);
    this.batches.push(batch);
    this.vectors.push(batch.schema.fields.map((_, i) => batch.getChildAt(i)!));
    this.rowCount += batch.numRows;
    if (this.view && !this.filter && this.sort == null) this.view = null;
    this.changed();
  }

  finish(truncated: boolean) {
    this.truncated = truncated;
    this.complete = true;
    if (this.sort || this.filter) this.rebuildView();
    this.changed();
  }

  get visibleRows(): number {
    return this.view ? this.view.length : this.rowCount;
  }

  physicalRow(viewRow: number): number {
    return this.view ? this.view[viewRow] : viewRow;
  }

  private locate(row: number): [number, number] {
    const starts = this.starts;
    let b = this.lastBatch;
    if (!(b < starts.length && starts[b] <= row && (b + 1 >= starts.length || starts[b + 1] > row))) {
      let lo = 0;
      let hi = starts.length - 1;
      while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (starts[mid] <= row) lo = mid;
        else hi = mid - 1;
      }
      b = lo;
      this.lastBatch = b;
    }
    return [b, row - starts[b]];
  }

  /** Display text of a cell; null = SQL NULL. */
  text(viewRow: number, colIndex: number): string | null {
    return this.textPhysical(this.physicalRow(viewRow), colIndex);
  }

  textPhysical(row: number, colIndex: number): string | null {
    const col = this.columns[colIndex];
    const [b, i] = this.locate(row);
    const vec = this.vectors[b][col.field];
    if (!vec.isValid(i)) return NULL;
    return formatValue(col, vec, i, this.vectors[b]);
  }

  /** The raw Arrow value of any field (hidden ones included) at a view row; null for SQL NULL. */
  raw(viewRow: number, field: number): unknown {
    const [b, i] = this.locate(this.physicalRow(viewRow));
    const vec = this.vectors[b][field];
    return vec && vec.isValid(i) ? vec.get(i) : null;
  }

  isTruncated(viewRow: number, colIndex: number): boolean {
    return this.isTruncatedPhysical(this.physicalRow(viewRow), colIndex);
  }

  /** Storage class of a cell of a dynamically typed column (Phase D0f); null for every other column. */
  storageClass(viewRow: number, colIndex: number): StorageClass | null {
    return this.storageClassPhysical(this.physicalRow(viewRow), colIndex);
  }

  storageClassPhysical(row: number, colIndex: number): StorageClass | null {
    const col = this.columns[colIndex];
    if (col?.classField == null) return null;
    const [b, i] = this.locate(row);
    return storageClassAt(col, this.vectors[b], i);
  }

  /**
   * The storage classes found so far in a dynamically typed column, as a mask of `classBit`s (0 for other columns).
   * Each batch is scanned once; later calls only read the batches that arrived since.
   */
  classesSeen(colIndex: number): number {
    const col = this.columns[colIndex];
    if (col?.classField == null) return 0;
    let seen = this.classScan.get(colIndex);
    if (!seen) this.classScan.set(colIndex, (seen = { batches: 0, mask: 0 }));
    for (; seen.batches < this.vectors.length; seen.batches++) {
      for (const data of this.vectors[seen.batches][col.classField].data) {
        const values = data.values as Uint8Array;
        for (let i = 0; i < data.length; i++) seen.mask |= classBit(classOrText(values[data.offset + i]));
      }
    }
    return seen.mask;
  }

  isTruncatedPhysical(row: number, colIndex: number): boolean {
    const col = this.columns[colIndex];
    if (col.flagField == null) return false;
    const [b, i] = this.locate(row);
    return this.vectors[b][col.flagField].get(i) === true;
  }

  /** Sort key: number | bigint | string | null. */
  private key(row: number, col: ColumnModel): number | bigint | string | null {
    const [b, i] = this.locate(row);
    const vec = this.vectors[b][col.field];
    if (!vec.isValid(i)) return null;
    switch (col.arrowTypeId) {
      case Type.Decimal:
        return decimalToBigInt(vec.get(i) as Uint32Array);
      case Type.Utf8:
      case Type.LargeUtf8:
        return col.kind === "number" ? numericTextKey(vec.get(i) as string) : (vec.get(i) as string);
      case Type.Timestamp:
      case Type.Time:
        return rawBigInt(vec, i);
      case Type.Bool:
        return vec.get(i) ? 1 : 0;
      case Type.Binary:
        return hex(vec.get(i) as Uint8Array, 16);
      default: {
        const v = vec.get(i);
        return typeof v === "number" || typeof v === "bigint" || typeof v === "string" ? v : String(v);
      }
    }
  }

  setSort(col: number | null, dir: "asc" | "desc" = "asc") {
    this.sort = col == null ? null : { col, dir };
    this.rebuildView();
    this.changed();
  }

  /** Filter text and, optionally, the columns it searches (omitted = keep the current choice; null or [] = all). */
  setFilter(text: string, columns?: number[] | null) {
    this.filter = text;
    if (columns !== undefined) this.filterColumns = columns?.length ? [...columns].sort((a, b) => a - b) : null;
    this.rebuildView();
    this.changed();
  }

  private rebuildView() {
    this.viewVersion++;
    if (!this.sort && !this.filter) {
      this.view = null;
      return;
    }
    let rows: number[];
    if (this.filter) {
      const needle = this.filter.toLowerCase();
      rows = [];
      const cols = this.filterColumns?.filter((c) => c < this.columns.length) ?? this.columns.map((_, i) => i);
      for (let r = 0; r < this.rowCount; r++) {
        for (const c of cols) {
          const t = this.textPhysical(r, c);
          if (t != null && t.toLowerCase().includes(needle)) {
            rows.push(r);
            break;
          }
        }
      }
    } else {
      rows = Array.from({ length: this.rowCount }, (_, i) => i);
    }
    if (this.sort && this.columns[this.sort.col]?.classField != null) {
      this.sortDynamic(rows, this.columns[this.sort.col], this.sort.dir === "asc" ? 1 : -1);
    } else if (this.sort) {
      const col = this.columns[this.sort.col];
      const keys = new Map<number, number | bigint | string | null>();
      for (const r of rows) keys.set(r, this.key(r, col));
      const dir = this.sort.dir === "asc" ? 1 : -1;
      rows.sort((a, b) => {
        const ka = keys.get(a)!;
        const kb = keys.get(b)!;
        if (ka === kb) return a - b;
        if (ka === null) return 1;
        if (kb === null) return -1;
        return (ka < kb ? -1 : 1) * dir;
      });
    }
    this.view = Uint32Array.from(rows);
  }

  /**
   * SQLite's order for a dynamically typed column: NULL < numbers (integers and reals compared by value) < text < blob.
   * Text compares as the grid compares any text; blobs compare by their hex, which orders like their bytes. Descending
   * reverses all of it; equal keys keep row order.
   */
  private sortDynamic(rows: number[], col: ColumnModel, dir: 1 | -1) {
    const rank = new Uint8Array(this.rowCount);
    const value: (number | bigint | string | null)[] = new Array(this.rowCount);
    for (const r of rows) {
      const [b, i] = this.locate(r);
      const cls = storageClassAt(col, this.vectors[b], i);
      const text = cls === StorageClass.Null ? null : (this.vectors[b][col.field].get(i) as string);
      if (text == null) continue; // rank 0, the lowest
      if (cls === StorageClass.Integer || cls === StorageClass.Real) {
        rank[r] = 1;
        value[r] = numberKey(cls, text);
      } else {
        rank[r] = cls === StorageClass.Blob ? 3 : 2;
        value[r] = text;
      }
    }
    rows.sort((a, b) => {
      const va = value[a];
      const vb = value[b];
      const c = rank[a] - rank[b] || (va == null || vb == null ? 0 : va < vb ? -1 : va > vb ? 1 : 0);
      return c ? c * dir : a - b;
    });
  }

  /** Tab-separated text of a rectangular selection (for copy). */
  copyText(rows: number[], cols: number[], withHeaders: boolean): string {
    const lines: string[] = [];
    if (withHeaders) lines.push(cols.map((c) => this.columns[c].name).join("\t"));
    for (const r of rows) lines.push(cols.map((c) => this.text(r, c) ?? "NULL").join("\t"));
    return lines.join("\n");
  }
}

export function formatValue(col: ColumnModel, vec: Vector, i: number, row: Vector[]): string {
  switch (col.arrowTypeId) {
    case Type.Decimal:
      return decimalToString(vec.get(i) as Uint32Array, col.scale);
    case Type.Timestamp: {
      const offset = col.offsetField != null && row[col.offsetField].isValid(i) ? (row[col.offsetField].get(i) as number) : null;
      return formatMicros(rawBigInt(vec, i), offset, col.tz);
    }
    case Type.Date: {
      const d = new Date(vec.get(i) as number);
      return `${d.getUTCFullYear().toString().padStart(4, "0")}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
    }
    case Type.Time:
      return formatTimeMicros(vec.get(i) as bigint);
    case Type.Bool:
      return vec.get(i) ? "1" : "0";
    case Type.Binary:
    case Type.LargeBinary:
      return hex(vec.get(i) as Uint8Array);
    case Type.Float: {
      const v = col.single ? shortestFloat32(vec.get(i) as number) : (vec.get(i) as number);
      return Number.isInteger(v) ? v.toFixed(1).replace(/\.0$/, "") : String(v);
    }
    default: {
      const v = vec.get(i);
      return typeof v === "string" ? v : String(v);
    }
  }
}

/** A class byte the sidecar does not define (a newer sidecar) reads as text: shown and sorted as the text it carries. */
function classOrText(v: number): StorageClass {
  return v <= StorageClass.Blob ? (v as StorageClass) : StorageClass.Text;
}

/**
 * Storage class of cell `i` of a dynamically typed column, from its sibling in the same batch (`row` = the batch's
 * vectors); null when the column is not dynamic. A cell whose class is missing reads as NULL or text by its validity.
 */
export function storageClassAt(col: ColumnModel, row: Vector[], i: number): StorageClass | null {
  if (col.classField == null) return null;
  const cls = row[col.classField];
  if (cls?.isValid(i)) return classOrText(cls.get(i) as number);
  return row[col.field].isValid(i) ? StorageClass.Text : StorageClass.Null;
}

/** Sort key of an integer or real cell: integers beyond 2^53 as bigint (exact; JS compares bigint with number by value). */
function numberKey(cls: StorageClass, text: string): number | bigint {
  const n = Number(text);
  if (cls === StorageClass.Integer && !Number.isSafeInteger(n)) {
    try {
      return BigInt(text);
    } catch {
      // not an integer literal: fall through to the double
    }
  }
  return Number.isNaN(n) ? -Infinity : n; // NaN would make the comparator inconsistent; SQLite keeps none anyway
}

/** A decimal literal (sign, digits, optional fraction and exponent): no hex, no blanks, no Infinity or NaN. */
const DECIMAL_LITERAL = /^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/;

/**
 * The number a dynamically typed cell's text stands for, or null when the text must stay text: integers beyond 2^53
 * (a double would round them), NaN and infinities (JSON cannot write them), and every text or blob cell. A real's text
 * is the round-trip form of a double, so parsing it gives back that very double.
 */
export function exactNumber(cls: StorageClass | null, text: string): number | null {
  if (cls === StorageClass.Integer) {
    const n = Number(text);
    return Number.isSafeInteger(n) && String(n) === text ? n : null;
  }
  if (cls === StorageClass.Real && DECIMAL_LITERAL.test(text)) {
    const n = Number(text);
    return Number.isFinite(n) ? n : null;
  }
  return null;
}

/** Per-execution data outside React: result sets in order of arrival. */
export class ExecutionData {
  resultSets: ArrowResultSet[] = [];
  streamEnded = false;
  version = 0;
  private listeners = new Set<() => void>();

  constructor(readonly executionId: string) {}

  subscribe = (cb: () => void) => {
    this.listeners.add(cb);
    return () => this.listeners.delete(cb);
  };

  getVersion = () => this.version;

  notify() {
    this.version++;
    this.listeners.forEach((l) => l());
  }

  get(resultIndex: number): ArrowResultSet {
    let rs = this.resultSets.find((r) => r.index === resultIndex);
    if (!rs) {
      rs = new ArrowResultSet(resultIndex);
      this.resultSets.push(rs);
      this.resultSets.sort((a, b) => a.index - b.index);
      this.notify();
    }
    return rs;
  }

  finishStream() {
    this.streamEnded = true;
    // A schema without rows or an end marker is an aborted result. Keep completed
    // empty sets and any partial rows; never prune while frames can still arrive.
    this.resultSets = this.resultSets.filter((r) => r.complete || r.rowCount > 0);
    for (const result of this.resultSets) if (!result.complete) result.interrupted = true;
    this.notify();
  }

  get totalRows(): number {
    return this.resultSets.reduce((n, r) => n + r.rowCount, 0);
  }
}

export const executionData = new Map<string, ExecutionData>();

export const __test = { decimalToString, decimalToBigInt, formatMicros, formatTimeMicros };

// Column extraction for charts (Phase C1): an Arrow vector as doubles (NaN = NULL; dates and timestamps as epoch ms)
// or as display labels. Runs in the chart worker over the batches the main thread forwards.
import { Type, type Vector } from "apache-arrow";
import { formatValue, StorageClass, type ColumnModel } from "../results/model";

const TWO_32 = 4294967296;
const SAFE = 2 ** 53;

export interface NumChunk {
  values: Float64Array;
  /** A value lost precision converting to a double. */
  lossy: boolean;
  temporal: boolean;
}

function validAt(bitmap: Uint8Array | null | undefined, nullCount: number, at: number): boolean {
  if (!bitmap || nullCount === 0) return true;
  return ((bitmap[at >> 3] >> (at & 7)) & 1) === 1;
}

/** A Decimal128 at word offset `k` as a double; `lossy` when the unscaled integer is beyond 2^53. */
function decimalAt(words: Uint32Array, k: number, scale: number): { v: number; lossy: boolean } {
  const w0 = words[k];
  const w1 = words[k + 1];
  const w2 = words[k + 2];
  const w3 = words[k + 3];
  const div = 10 ** scale;
  if ((w2 === 0 && w3 === 0 && w1 < 0x200000) || (w2 === 0xffffffff && w3 === 0xffffffff && w1 >= 0xffe00000)) {
    const signed = w1 >= 0x80000000 ? (w1 - TWO_32) * TWO_32 + w0 : w1 * TWO_32 + w0;
    return { v: signed / div, lossy: false };
  }
  let big = (BigInt(w3) << 96n) | (BigInt(w2) << 64n) | (BigInt(w1) << 32n) | BigInt(w0);
  if (w3 & 0x80000000) big -= 1n << 128n;
  return { v: Number(big) / div, lossy: true };
}

const unitDiv = (unit: number) => (unit === 0 ? 1e-3 : unit === 1 ? 1 : unit === 2 ? 1e3 : 1e6);

/**
 * A dynamically typed column's text as doubles (Phase D0f, SQLite): integer and real cells parse; NULL and any other
 * class are NaN. An integer beyond 2^53 is drawn approximately and says so.
 */
function dynamicChunk(vec: Vector, cls: Vector): NumChunk {
  const out = new Float64Array(vec.length);
  let lossy = false;
  for (let i = 0; i < vec.length; i++) {
    const c = cls.isValid(i) ? (cls.get(i) as number) : StorageClass.Null;
    const text = c === StorageClass.Integer || c === StorageClass.Real ? (vec.get(i) as string | null) : null;
    const v = text == null ? NaN : Number(text);
    if (c === StorageClass.Integer && (v > SAFE || v < -SAFE)) lossy = true;
    out[i] = v;
  }
  return { values: out, lossy, temporal: false };
}

/** A numeric, boolean or temporal vector as doubles; `cls` is the storage-class sibling of a dynamically typed column. */
export function numericChunk(vec: Vector, col: Pick<ColumnModel, "arrowTypeId" | "scale">, cls?: Vector): NumChunk {
  if (cls) return dynamicChunk(vec, cls);
  const n = vec.length;
  const out = new Float64Array(n);
  let lossy = false;
  let temporal = false;
  let pos = 0;
  for (const data of vec.data) {
    const len = data.length;
    const bitmap = data.nullBitmap;
    const nulls = data.nullCount;
    const off = data.offset;
    const type = data.type as { typeId: Type; bitWidth?: number; unit?: number; scale?: number };
    switch (col.arrowTypeId) {
      case Type.Int:
      case Type.Float: {
        const values = data.values as ArrayLike<number | bigint>;
        const big = type.bitWidth === 64 && col.arrowTypeId === Type.Int;
        for (let i = 0; i < len; i++) {
          if (!validAt(bitmap, nulls, off + i)) {
            out[pos + i] = NaN;
            continue;
          }
          const raw = values[off + i];
          const v = big ? Number(raw as bigint) : (raw as number);
          if (big && (v > SAFE || v < -SAFE)) lossy = true;
          out[pos + i] = v;
        }
        break;
      }
      case Type.Decimal: {
        const words = data.values as Uint32Array;
        const scale = type.scale ?? col.scale;
        const stride = (type.bitWidth ?? 128) / 32;
        for (let i = 0; i < len; i++) {
          if (!validAt(bitmap, nulls, off + i)) {
            out[pos + i] = NaN;
            continue;
          }
          const d = decimalAt(words, (off + i) * stride, scale);
          if (d.lossy) lossy = true;
          out[pos + i] = d.v;
        }
        break;
      }
      case Type.Timestamp: {
        temporal = true;
        const values = data.values as BigInt64Array;
        const div = unitDiv(type.unit ?? 2);
        for (let i = 0; i < len; i++) out[pos + i] = validAt(bitmap, nulls, off + i) ? Number(values[off + i]) / div : NaN;
        break;
      }
      case Type.Bool: {
        const bits = data.values as Uint8Array;
        for (let i = 0; i < len; i++) {
          const at = off + i;
          out[pos + i] = validAt(bitmap, nulls, at) ? (bits[at >> 3] >> (at & 7)) & 1 : NaN;
        }
        break;
      }
      default: {
        // Dates (epoch ms from get), times of day (ms since midnight) and anything rarer: one value at a time.
        if (col.arrowTypeId === Type.Date || col.arrowTypeId === Type.Time) temporal = true;
        for (let i = 0; i < len; i++) {
          const v = validAt(bitmap, nulls, off + i) ? vec.get(pos + i) : null;
          out[pos + i] = v == null ? NaN : col.arrowTypeId === Type.Time ? Number(v) / unitDiv(type.unit ?? 2) : Number(v);
          // A number sent as exact text (M11) with more digits than a double holds is drawn approximately and says so.
          if (typeof v === "string" && v.replace(/^[+-]?0*|[.]|0*$/g, "").length > 15) lossy = true;
        }
      }
    }
    pos += len;
  }
  return { values: out, lossy, temporal };
}

/** Any vector as display labels, exactly as the grid shows them (null = NULL). */
export function labelChunk(vec: Vector, col: ColumnModel, row: Vector[]): (string | null)[] {
  const out: (string | null)[] = new Array(vec.length);
  if (col.arrowTypeId === Type.Utf8 || col.arrowTypeId === Type.LargeUtf8) {
    for (let i = 0; i < vec.length; i++) out[i] = vec.isValid(i) ? (vec.get(i) as string) : null;
    return out;
  }
  for (let i = 0; i < vec.length; i++) out[i] = vec.isValid(i) ? formatValue(col, vec, i, row) : null;
  return out;
}

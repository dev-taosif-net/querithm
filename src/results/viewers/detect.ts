// Which viewer a result cell gets (Phase C2) and the byte helpers the viewers share. Pure: no DOM, no React.
import type { ColumnModel } from "../model";

export type ViewerKind = "json" | "xml" | "binary" | "text";

/** Largest JSON / XML value the structured viewers parse (characters); bigger values are shown as raw text. */
export const STRUCTURED_MAX_CHARS = 10 * 1024 * 1024;

/** Longest text the raw / long-text view draws; Copy still copies all of it. */
export const RAW_DISPLAY_CHARS = 2 * 1024 * 1024;

/** First non-whitespace character code at or after `from` (-1 when none). */
function firstNonSpace(s: string, from = 0): number {
  for (let i = from; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c !== 0x20 && c !== 0x09 && c !== 0x0a && c !== 0x0d && c !== 0xfeff) return c;
  }
  return -1;
}

function lastNonSpace(s: string): number {
  for (let i = s.length - 1; i >= 0; i--) {
    const c = s.charCodeAt(i);
    if (c !== 0x20 && c !== 0x09 && c !== 0x0a && c !== 0x0d) return c;
  }
  return -1;
}

/**
 * The viewer for a cell: JSON for json/jsonb columns or text shaped like an object/array, XML for xml columns or text
 * that starts with "<", the hex/image viewer for binary columns, else the long-text viewer. Shape is checked on the
 * first and last characters only; the JSON viewer's parse decides (a malformed value is shown raw with the error).
 */
export function viewerKind(col: Pick<ColumnModel, "kind" | "typeName">, value: string | null): ViewerKind {
  if (col.kind === "binary") return "binary";
  const type = col.typeName.toLowerCase();
  if (type === "json" || type === "jsonb") return "json";
  if (type === "xml") return "xml";
  if (col.kind !== "text" || value == null) return "text";
  const first = firstNonSpace(value);
  const last = lastNonSpace(value);
  if ((first === 0x7b && last === 0x7d) || (first === 0x5b && last === 0x5d)) return "json"; // {…} or […]
  if (first === 0x3c && last === 0x3e) return "xml"; // <…>
  return "text";
}

export type ImageMime = "image/png" | "image/jpeg" | "image/gif";

/** PNG, JPEG or GIF by their magic bytes; anything else (an HTML document, SVG, unknown) is null. */
export function sniffImage(b: Uint8Array): ImageMime | null {
  if (b.length >= 8 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47 && b[4] === 0x0d && b[5] === 0x0a && b[6] === 0x1a && b[7] === 0x0a) return "image/png";
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return "image/jpeg";
  if (b.length >= 6 && b[0] === 0x47 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x38 && (b[4] === 0x37 || b[4] === 0x39) && b[5] === 0x61) return "image/gif";
  return null;
}

/** Bytes shown by the hex view; the length line always gives the full size. */
export const HEX_MAX_BYTES = 256 * 1024;

/** "00000010  48 65 6C 6C 6F …  Hello…" lines, 16 bytes each, at most `maxBytes` (then a final "… N more bytes" line). */
export function hexDump(b: Uint8Array, maxBytes = HEX_MAX_BYTES): string[] {
  const n = Math.min(b.length, maxBytes);
  const lines: string[] = [];
  for (let off = 0; off < n; off += 16) {
    let hex = "";
    let ascii = "";
    for (let i = 0; i < 16; i++) {
      if (off + i < n) {
        const v = b[off + i];
        hex += v.toString(16).padStart(2, "0").toUpperCase() + (i === 7 ? "  " : " ");
        ascii += v >= 0x20 && v < 0x7f ? String.fromCharCode(v) : ".";
      } else hex += i === 7 ? "    " : "   ";
    }
    lines.push(`${off.toString(16).padStart(8, "0").toUpperCase()}  ${hex} ${ascii}`);
  }
  if (b.length > n) lines.push(`… ${(b.length - n).toLocaleString("en-US")} more bytes`);
  return lines;
}

/** Bytes of a base64 string (query.cell returns binary values this way). */
export function base64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function nibble(c: number): number {
  if (c >= 0x30 && c <= 0x39) return c - 0x30; // 0-9
  if (c >= 0x61 && c <= 0x66) return c - 0x57; // a-f
  if (c >= 0x41 && c <= 0x46) return c - 0x37; // A-F
  return -1;
}

/**
 * Bytes of a hex string: a dynamically typed blob cell (SQLite) arrives as lowercase hex; an optional "0x" and either
 * case are accepted too. Null when the text is not whole bytes of hex.
 */
export function hexToBytes(hex: string): Uint8Array | null {
  const start = hex.length >= 2 && hex.charCodeAt(0) === 0x30 && (hex.charCodeAt(1) | 0x20) === 0x78 ? 2 : 0; // "0x" / "0X"
  if ((hex.length - start) % 2) return null;
  const out = new Uint8Array((hex.length - start) / 2);
  for (let i = 0; i < out.length; i++) {
    const hi = nibble(hex.charCodeAt(start + 2 * i));
    const lo = nibble(hex.charCodeAt(start + 2 * i + 1));
    if (hi < 0 || lo < 0) return null;
    out[i] = (hi << 4) | lo;
  }
  return out;
}

export function bytesToBase64(b: Uint8Array): string {
  let s = "";
  const CHUNK = 0x8000;
  for (let i = 0; i < b.length; i += CHUNK) s += String.fromCharCode(...b.subarray(i, i + CHUNK));
  return btoa(s);
}

/** "0x48656C…" for Copy: the whole value as one hex literal. */
export function hexLiteral(b: Uint8Array): string {
  let s = "0x";
  for (let i = 0; i < b.length; i++) s += b[i].toString(16).padStart(2, "0").toUpperCase();
  return s;
}

/** "1,234 bytes (1.2 KB)". */
export function byteSize(n: number): string {
  const base = `${n.toLocaleString("en-US")} byte${n === 1 ? "" : "s"}`;
  if (n < 1024) return base;
  const units = ["KB", "MB", "GB"];
  let v = n / 1024;
  let u = 0;
  while (v >= 1024 && u < units.length - 1) {
    v /= 1024;
    u++;
  }
  return `${base} (${v.toFixed(1)} ${units[u]})`;
}

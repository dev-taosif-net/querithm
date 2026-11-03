// Reads a workbook back in tests (xlsx.test, export.engines.test): every zip entry through its central directory,
// each entry's CRC checked.
import { inflateRawSync } from "node:zlib";
import { expect } from "vitest";
import { crc32 } from "../xlsx";

/** Reads every entry of a zip through its central directory, checking each entry's CRC. */
export function unzip(bytes: Uint8Array): Map<string, string> {
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const end = bytes.length - 22;
  expect(v.getUint32(end, true)).toBe(0x06054b50);
  const count = v.getUint16(end + 10, true);
  let p = v.getUint32(end + 16, true);
  const out = new Map<string, string>();
  for (let n = 0; n < count; n++) {
    expect(v.getUint32(p, true)).toBe(0x02014b50);
    const method = v.getUint16(p + 10, true);
    const crc = v.getUint32(p + 16, true);
    const csize = v.getUint32(p + 20, true);
    const nameLen = v.getUint16(p + 28, true);
    const local = v.getUint32(p + 42, true);
    const name = new TextDecoder().decode(bytes.subarray(p + 46, p + 46 + nameLen));
    const dataAt = local + 30 + v.getUint16(local + 26, true) + v.getUint16(local + 28, true);
    const raw = bytes.subarray(dataAt, dataAt + csize);
    const data = method === 8 ? new Uint8Array(inflateRawSync(raw)) : raw;
    expect(crc32([data])).toBe(crc);
    out.set(name, new TextDecoder().decode(data));
    p += 46 + nameLen + v.getUint16(p + 30, true) + v.getUint16(p + 32, true);
  }
  return out;
}

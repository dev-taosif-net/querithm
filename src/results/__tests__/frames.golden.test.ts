// The data plane across languages (§5): the sidecar encoder's frames (a golden file the .NET CrossLanguageFrameTests owns)
// decoded by the webview's own path — decodeStream (apache-arrow JS) into ArrowResultSet — and shown as the grid shows them.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { decodeStream, FRAME_BATCH, FRAME_SCHEMA } from "../../ipc/frames";
import { ArrowResultSet } from "../model";

function frames() {
  const buf = readFileSync(fileURLToPath(new URL("./fixtures/sidecar-frames.bin", import.meta.url)));
  const out: { kind: number; payload: Uint8Array }[] = [];
  for (let at = 0; at < buf.length; ) {
    const kind = buf[at];
    const len = buf.readInt32LE(at + 1);
    out.push({ kind, payload: new Uint8Array(buf.buffer, buf.byteOffset + at + 5, len) });
    at += 5 + len;
  }
  return out;
}

function resultSet() {
  const f = frames();
  expect(f.map((x) => x.kind)).toEqual([FRAME_SCHEMA, FRAME_BATCH]);
  const rs = new ArrowResultSet(0);
  for (const b of decodeStream(f[1].payload).batches) rs.append(b);
  rs.finish(false);
  return { rs, schema: decodeStream(f[0].payload).schema };
}

describe("sidecar frames through the webview reader", () => {
  it("keeps the Arrow types and the sidecar's field metadata", () => {
    const { schema } = resultSet();
    const types = Object.fromEntries(schema.fields.map((f) => [f.name, String(f.type)]));
    expect(types).toMatchObject({
      id: "Int64", i8: "Int8", i16: "Int16", i32: "Int32", u64: "Utf8", f32: "Float32", f64: "Float64", dec: "Decimal[18e+4]", txt: "Utf8", bin: "Binary",
      b: "Bool", dt: "Timestamp<MICROSECOND>", dto: "Timestamp<MICROSECOND, UTC>", dto__offset: "Int16", tm: "Utf8", g: "Utf8", dyn: "Utf8", dyn__class: "Uint8",
    });
    const meta = (name: string) => Object.fromEntries(schema.fields.find((f) => f.name === name)!.metadata);
    expect(meta("dec")).toMatchObject({ "q:typeName": "Decimal", "q:ordinal": "7" });
    // M11: an unsigned 64-bit integer travels as exact text marked as a number; real text is not marked.
    expect(meta("u64")).toMatchObject({ "q:numeric": "1" });
    expect(meta("txt")["q:numeric"]).toBeUndefined();
    expect(meta("dyn")).toMatchObject({ "q:dynamic": "1", "q:ordinal": "15" });
    expect(meta("dyn__class")).toMatchObject({ "q:hidden": "class", "q:of": "dyn" });
    expect(meta("dto__offset")).toMatchObject({ "q:hidden": "offset", "q:of": "dto" });
  });

  it("shows every value as the grid does, hidden siblings out of sight", () => {
    const { rs } = resultSet();
    expect(rs.columns.map((c) => c.name)).toEqual(["id", "i8", "i16", "i32", "u64", "f32", "f64", "dec", "txt", "bin", "b", "dt", "dto", "tm", "g", "dyn"]);
    expect(rs.columns.find((c) => c.name === "u64")?.kind).toBe("number");
    const row = (r: number) => rs.columns.map((_, c) => rs.text(r, c));
    expect(row(0)).toEqual(["1", "-128", "-32768", "-2147483648", "0", "1.5", "0.1", "12345.6789", "héllo ✓", "0x00FF", "1",
      "2026-10-31 10:20:30.123456", "2026-10-31 10:20:30.123 +06:00", "10:20:30.1230000", "6f9619ff-8b86-d011-b42d-00c04fc964ff", "1"]);
    expect(row(1)).toEqual(["2", ...Array(15).fill(null)]);
    expect(row(2)).toEqual(["3", "127", "32767", "2147483647", "18446744073709551615", "-0.25", "1e+300", "-0.0001", "", "0x", "0",
      "0001-01-01 00:00:00", "1999-12-31 23:59:59 -02:30", "23:59:59.9990000", "00000000-0000-0000-0000-000000000000", "x"]);
    // The dynamic column's storage class per cell: integer, null, text.
    const dyn = rs.columns.findIndex((c) => c.name === "dyn");
    expect([0, 1, 2].map((r) => rs.storageClass(r, dyn))).toEqual([1, 0, 3]);
  });
});

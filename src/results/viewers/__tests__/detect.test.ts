import { describe, expect, it } from "vitest";
import { base64ToBytes, byteSize, bytesToBase64, hexDump, hexLiteral, hexToBytes, sniffImage, viewerKind } from "../detect";

const text = { kind: "text" as const, typeName: "nvarchar" };

describe("viewerKind", () => {
  it("follows the column type first", () => {
    expect(viewerKind({ kind: "text", typeName: "json" }, "not json")).toBe("json");
    expect(viewerKind({ kind: "text", typeName: "jsonb" }, null)).toBe("json");
    expect(viewerKind({ kind: "text", typeName: "xml" }, "plain")).toBe("xml");
    expect(viewerKind({ kind: "binary", typeName: "varbinary" }, null)).toBe("binary");
  });
  it("sniffs objects, arrays and markup in text columns by their first and last characters", () => {
    expect(viewerKind(text, '  {"a": 1}\n')).toBe("json");
    expect(viewerKind(text, "[1, 2]")).toBe("json");
    expect(viewerKind(text, "﻿<root/>")).toBe("xml");
    expect(viewerKind(text, "{ unbalanced")).toBe("text");
    expect(viewerKind(text, "a < b > c")).toBe("text");
    expect(viewerKind(text, "plain words")).toBe("text");
    expect(viewerKind(text, null)).toBe("text");
  });
  it("numbers, dates and flags use the text viewer", () => {
    expect(viewerKind({ kind: "number", typeName: "int" }, "[1]")).toBe("text");
    expect(viewerKind({ kind: "temporal", typeName: "datetime2" }, "2026-01-01")).toBe("text");
  });
});

describe("sniffImage", () => {
  const png = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13]);
  it("recognises PNG, JPEG and GIF by their signatures", () => {
    expect(sniffImage(png)).toBe("image/png");
    expect(sniffImage(Uint8Array.from([0xff, 0xd8, 0xff, 0xe0, 0, 0x10]))).toBe("image/jpeg");
    expect(sniffImage(new TextEncoder().encode("GIF89a\x01\x00"))).toBe("image/gif");
    expect(sniffImage(new TextEncoder().encode("GIF87a\x01\x00"))).toBe("image/gif");
  });
  it("never takes markup or other bytes for an image", () => {
    expect(sniffImage(new TextEncoder().encode("<html><img src=x onerror=alert(1)></html>"))).toBeNull();
    expect(sniffImage(new TextEncoder().encode('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>'))).toBeNull();
    expect(sniffImage(png.subarray(0, 7))).toBeNull();
    expect(sniffImage(new Uint8Array(0))).toBeNull();
  });
});

describe("hex helpers", () => {
  it("dumps 16 bytes a line with offset, hex and printable ASCII", () => {
    const b = new TextEncoder().encode("Hello, world!\n\x00ABCDEFG");
    const lines = hexDump(b);
    expect(lines).toHaveLength(2);
    expect(lines[0]).toBe("00000000  48 65 6C 6C 6F 2C 20 77  6F 72 6C 64 21 0A 00 41  Hello, world!..A");
    expect(lines[1].startsWith("00000010  42 43 44 45 46 47")).toBe(true);
    expect(lines[1].endsWith(" BCDEFG")).toBe(true);
  });
  it("stops at the byte limit and says how many bytes were left out", () => {
    const lines = hexDump(new Uint8Array(100), 32);
    expect(lines).toHaveLength(3);
    expect(lines[2]).toBe("… 68 more bytes");
  });
  it("round-trips base64 and writes a hex literal", () => {
    const b = Uint8Array.from({ length: 70_000 }, (_, i) => i % 256);
    expect(base64ToBytes(bytesToBase64(b))).toEqual(b);
    expect(hexLiteral(Uint8Array.from([0, 0xab, 0x10]))).toBe("0x00AB10");
  });
  it("decodes hex text (a dynamically typed blob cell): lowercase as sent, with 0x or upper case too, else null", () => {
    expect(hexToBytes("00ab10ff")).toEqual(Uint8Array.from([0, 0xab, 0x10, 0xff]));
    expect(hexToBytes("0x00AB10")).toEqual(Uint8Array.from([0, 0xab, 0x10]));
    expect(hexToBytes("")).toEqual(new Uint8Array(0));
    expect(hexToBytes(hexLiteral(Uint8Array.from([1, 2, 254])))).toEqual(Uint8Array.from([1, 2, 254]));
    for (const bad of ["abc", "zz", "0x0", "12 34", "0aG0"]) expect(hexToBytes(bad)).toBeNull();
  });
  it("sizes bytes", () => {
    expect(byteSize(1)).toBe("1 byte");
    expect(byteSize(512)).toBe("512 bytes");
    expect(byteSize(2048)).toBe("2,048 bytes (2.0 KB)");
    expect(byteSize(3 * 1024 * 1024)).toBe("3,145,728 bytes (3.0 MB)");
  });
});

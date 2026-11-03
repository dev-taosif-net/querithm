// Export per Phase D engine: each provider's typical result shapes (fixtures.ts, as the sidecar's encoder sends them)
// through the export worker in every format it writes — CSV, TSV, JSON and Excel. Exact values (big integers, wide
// decimals, DuckDB's literal text) must leave exactly as the grid shows them.
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { Float32, Table, tableToIPC, type RecordBatch } from "apache-arrow";
import { duckDbShapes, makeBatch, mySqlShapes, sqliteShapes } from "./fixtures";
import { unzip } from "./zip";

type Handler = (e: { data: unknown }) => Promise<void>;
const posted: unknown[] = [];
const fakeSelf: { onmessage?: Handler; postMessage: (m: unknown) => void } = { postMessage: (m) => void posted.push(m) };

beforeAll(async () => {
  vi.stubGlobal("self", fakeSelf);
  await import("../export.worker");
});
afterEach(() => (posted.length = 0));

const ipc = (b: RecordBatch) => {
  const bytes = tableToIPC(new Table([b]), "stream");
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
};
async function run(format: string, b: RecordBatch) {
  posted.length = 0;
  await fakeSelf.onmessage!({ data: { format, batches: [ipc(b)], sheetName: "Result" } });
  return posted[0] as Uint8Array;
}
const text = async (format: string, b: RecordBatch) => new TextDecoder().decode(await run(format, b));
const json = async (b: RecordBatch) => JSON.parse(await text("json", b)) as Record<string, unknown>[];
const sheet = async (b: RecordBatch) => unzip(await run("xlsx", b)).get("xl/worksheets/sheet1.xml")!;
const inline = (ref: string, v: string) => `<c r="${ref}" t="inlineStr"><is><t xml:space="preserve">${v}</t></is></c>`;

describe("SQLite (every column dynamically typed)", () => {
  it("csv and tsv write each cell's text, never the hidden class and flag siblings", async () => {
    expect(await text("csv", sqliteShapes())).toBe(
      'id,name,price,note,data\r\n1,apple,3,7,00ff\r\n2,"say ""hi"", ok",2.5,n/a,\r\n3,,0.30000000000000004,9223372036854775807,\r\n',
    );
    expect((await text("tsv", sqliteShapes())).split("\r\n")[1]).toBe("1\tapple\t3\t7\t00ff");
  });

  it("json: integers and reals by their class, exactly; big integers, text and blobs as strings", async () => {
    expect(await json(sqliteShapes())).toEqual([
      { id: 1, name: "apple", price: 3, note: 7, data: "00ff" },
      { id: 2, name: 'say "hi", ok', price: 2.5, note: "n/a", data: null },
      { id: 3, name: null, price: 0.30000000000000004, note: "9223372036854775807", data: "" },
    ]);
  });

  it("xlsx: integer and real cells are numbers by their class; text, blobs and numbers Excel cannot hold stay text", async () => {
    const s = await sheet(sqliteShapes());
    expect(s).toContain('<c r="A2"><v>1</v></c>');
    expect(s).toContain('<c r="C3"><v>2.5</v></c>');
    expect(s).toContain('<c r="D2"><v>7</v></c>');
    expect(s).toContain(inline("D3", "n/a")); // a text cell in the same column stays text
    expect(s).toContain(inline("D4", "9223372036854775807")); // beyond 15 digits: exact text
    expect(s).toContain(inline("C4", "0.30000000000000004"));
    expect(s).toContain(inline("E2", "00ff")); // a blob, never a number
    expect(s).toContain(inline("B2", "apple"));
    expect(s).not.toContain("__class");
  });
});

describe("MySQL / MariaDB", () => {
  it("csv: signed TINYINT, exact decimals, the server's date text (zero date kept), JSON quoted, BIT and unsigned BIGINT", async () => {
    expect((await text("csv", mySqlShapes())).split("\r\n")).toEqual([
      "id,tiny,amount,wide,dt,ts,doc,flags,big",
      '1,-128,12.34,12345678901234567890123456789012.123,2026-10-31 12:34:56,2026-10-31 12:34:56.5,"{""a"": [1, 2], ""b"": ""x,y""}",5,18446744073709551615',
      "2,0,-9.95,0.000,0000-00-00 00:00:00,,null,0,0",
      "3,127,,,2026-11-01 00:00:00,1970-01-01 00:00:01,,255,",
      "",
    ]);
  });

  it("json: whole numbers as numbers; decimals, JSON documents, BIT and unsigned BIGINT as exact strings", async () => {
    const [first, second] = await json(mySqlShapes());
    expect(first).toEqual({
      id: 1, tiny: -128, amount: "12.34", wide: "12345678901234567890123456789012.123", dt: "2026-10-31 12:34:56", ts: "2026-10-31 12:34:56.5",
      doc: '{"a": [1, 2], "b": "x,y"}', flags: "5", big: "18446744073709551615",
    });
    expect(second).toMatchObject({ tiny: 0, amount: "-9.95", dt: "0000-00-00 00:00:00", doc: "null", ts: null });
  });

  it("xlsx: integers and DECIMAL(10,2) are numbers; wide decimals, dates sent as text and unsigned BIGINT stay text", async () => {
    const s = await sheet(mySqlShapes());
    expect(s).toContain('<c r="B2"><v>-128</v></c>');
    expect(s).toContain('<c r="C2"><v>12.34</v></c>');
    expect(s).toContain(inline("D2", "12345678901234567890123456789012.123"));
    expect(s).toContain(inline("E3", "0000-00-00 00:00:00"));
    expect(s).toContain(inline("G2", "{&quot;a&quot;: [1, 2], &quot;b&quot;: &quot;x,y&quot;}"));
    expect(s).toContain(inline("I2", "18446744073709551615"));
  });
});

describe("DuckDB", () => {
  it("csv: exact decimals and HUGEINT, intervals, nested literals (quoted where they hold a comma), timestamps, blobs", async () => {
    expect((await text("csv", duckDbShapes())).split("\r\n")).toEqual([
      "id,d,d38,h,iv,l,st,mp,ts,tz,bl,b",
      `1,1.50,12345678901234567890.123456789012345678,170141183460469231731687303715884105727,1 year 2 months 3 days 04:05:06.789,"[1, NULL, 3]","{'a': 1, 'b': 'x'}",{'k'=1},2026-10-31 10:20:30,2026-10-31 04:20:30.123456 +00:00,0xAABB,1`,
      `2,-0.25,0.000000000000000000,-1,00:00:00,[],"{'a': NULL, 'b': NULL}",{},2026-11-01 10:20:30.123456,,,0`,
      "3,,,,,,,,,1970-01-01 00:00:00 +00:00,0x,",
      "",
    ]);
  });

  it("json: decimals keep their digits (trailing zeros too) as strings; nested values are DuckDB's text, not JSON", async () => {
    const [first] = await json(duckDbShapes());
    expect(first).toEqual({
      id: 1, d: "1.50", d38: "12345678901234567890.123456789012345678", h: "170141183460469231731687303715884105727", iv: "1 year 2 months 3 days 04:05:06.789",
      l: "[1, NULL, 3]", st: "{'a': 1, 'b': 'x'}", mp: "{'k'=1}", ts: "2026-10-31 10:20:30", tz: "2026-10-31 04:20:30.123456 +00:00", bl: "0xAABB", b: "1",
    });
  });

  it("xlsx: TIMESTAMP is an Excel date, BOOLEAN a boolean; decimals a number Excel holds; wider decimals, HUGEINT, nested text and instants stay text", async () => {
    const s = await sheet(duckDbShapes());
    expect(s).toMatch(/<c r="I2" s="2"><v>46326\.43090277\d*<\/v><\/c>/); // 2026-10-31 10:20:30
    expect(s).toContain('<c r="L2" t="b"><v>1</v></c>');
    // M11: DECIMAL travels as exact numeric text; within Excel's 15 digits it is a number, beyond it stays text.
    expect(s).toContain('<c r="B2"><v>1.50</v></c>');
    expect(s).toContain(inline("C2", "12345678901234567890.123456789012345678"));
    expect(s).toContain(inline("D2", "170141183460469231731687303715884105727"));
    expect(s).toContain(inline("F2", "[1, NULL, 3]"));
    expect(s).toContain(inline("G2", "{'a': 1, 'b': 'x'}"));
    expect(s).toContain(inline("J2", "2026-10-31 04:20:30.123456 +00:00"));
    expect(s).toContain(inline("K2", "0xAABB"));
  });
});

describe("Float32 (REAL / FLOAT4 on every engine, L12)", () => {
  const reals = () => makeBatch([{ name: "r", type: new Float32(), values: [0.1, 2.5, null], meta: { "q:typeName": "real", "q:ordinal": "0" } }]);
  it("csv, json and xlsx write the shortest decimal that reads back as the same float, as the grid shows it", async () => {
    expect(await text("csv", reals())).toBe("r\r\n0.1\r\n2.5\r\n\r\n");
    expect(await json(reals())).toEqual([{ r: "0.1" }, { r: "2.5" }, { r: null }]);
    expect(await sheet(reals())).toContain('<c r="A2"><v>0.1</v></c>');
  });
});

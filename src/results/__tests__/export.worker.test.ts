import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { Table, tableToIPC } from "apache-arrow";
import { Bool, Float64, Int32, Utf8 } from "apache-arrow";
import { dynamicColumn, makeBatch, ordersBatch, type DynamicCell } from "./fixtures";

type Handler = (e: { data: unknown }) => Promise<void>;
const posted: unknown[] = [];
const fakeSelf: { onmessage?: Handler; postMessage: (m: unknown) => void } = { postMessage: (m) => void posted.push(m) };

beforeAll(async () => {
  vi.stubGlobal("self", fakeSelf);
  await import("../export.worker");
});
afterEach(() => (posted.length = 0));

const ipc = (b = ordersBatch()) => {
  const bytes = tableToIPC(new Table([b]), "stream");
  return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
};
async function run(format: string, batches: ArrayBuffer[], sheetName = "S") {
  await fakeSelf.onmessage!({ data: { format, batches, sheetName } });
  return posted[0];
}
const text = (u: unknown) => new TextDecoder().decode(u as Uint8Array);

describe("export worker", () => {
  it("csv: header then rows, NULL as empty, CRLF line ends", async () => {
    expect(text(await run("csv", [ipc()]))).toBe("id,name,qty\r\n1,one,10\r\n2,two,\r\n3,three,30\r\n");
  });

  it("tsv uses tabs; csv quotes separators, quotes and newlines", async () => {
    const b = makeBatch([{ name: "a,b", type: new Utf8(), values: ['x"y', "line\nbreak", "plain"] }]);
    expect(text(await run("csv", [ipc(b)]))).toBe('"a,b"\r\n"x""y"\r\n"line\nbreak"\r\nplain\r\n');
    posted.length = 0;
    const t = makeBatch([{ name: "a", type: new Utf8(), values: ["x\ty", "no,comma"] }, { name: "n", type: new Int32(), values: [1, 2] }]);
    expect(text(await run("tsv", [ipc(t)]))).toBe('a\tn\r\n"x\ty"\t1\r\nno,comma\t2\r\n');
  });

  it("header is written once across batches", async () => {
    expect(text(await run("csv", [ipc(), ipc()])).split("\r\n").filter((l) => l === "id,name,qty")).toHaveLength(1);
  });

  it("json: integers become numbers, other values stay strings, NULL is null", async () => {
    const out = JSON.parse(text(await run("json", [ipc(), ipc()]))) as Record<string, unknown>[];
    expect(out).toHaveLength(6);
    expect(out[0]).toEqual({ id: 1, name: "one", qty: 10 });
    expect(out[1].qty).toBeNull();
  });

  it("json with no batches is an empty array", async () => {
    expect(JSON.parse(text(await run("json", [])))).toEqual([]);
  });

  it("csv with no batches is empty", async () => {
    expect((await run("csv", [])) as Uint8Array).toHaveLength(0);
  });

  it("xlsx produces a zip workbook, also with no rows", async () => {
    const out = (await run("xlsx", [ipc()])) as Uint8Array;
    expect(Array.from(out.slice(0, 2))).toEqual([0x50, 0x4b]);
    posted.length = 0;
    const empty = (await run("xlsx", [])) as Uint8Array;
    expect(Array.from(empty.slice(0, 2))).toEqual([0x50, 0x4b]);
  });

  it("xlsx failures are posted as an error message", async () => {
    await run("xlsx", undefined as never);
    expect(posted[0]).toEqual({ error: expect.any(String) });
  });

  it("handles bool columns", async () => {
    const b = makeBatch([{ name: "ok", type: new Bool(), values: [true, false] }]);
    expect(text(await run("csv", [ipc(b)]))).toBe("ok\r\n1\r\n0\r\n");
  });

  describe("dynamically typed columns (Phase D0f, SQLite)", () => {
    const cells: DynamicCell[] = [
      [1, "42"], [2, "1.5"], [1, "9223372036854775807"], [3, "42"], [4, "00ff"], [0, null], [2, "NaN"], [3, 'say "hi", ok'],
    ];
    const dyn = () => makeBatch([{ name: "id", type: new Int32(), values: cells.map((_, i) => i + 1), meta: { "q:typeName": "INTEGER" } }, ...dynamicColumn("v", cells, 1)]);

    it("csv writes each cell's text and never the hidden siblings", async () => {
      expect(text(await run("csv", [ipc(dyn())]))).toBe(
        'id,v\r\n1,42\r\n2,1.5\r\n3,9223372036854775807\r\n4,42\r\n5,00ff\r\n6,\r\n7,NaN\r\n8,"say ""hi"", ok"\r\n',
      );
    });

    it("json writes integers and reals as numbers when exact; big integers, NaN, text and blobs as strings; NULL as null", async () => {
      const raw = text(await run("json", [ipc(dyn())]));
      const out = JSON.parse(raw) as Record<string, unknown>[];
      expect(out.map((r) => r.v)).toEqual([42, 1.5, "9223372036854775807", "42", "00ff", null, "NaN", 'say "hi", ok']);
      expect(Object.keys(out[0])).toEqual(["id", "v"]);
      expect(raw).toContain('"v":"9223372036854775807"'); // the digits survive: no rounding to 9223372036854775808
    });

    it("a typed float column is unaffected: its values stay strings as before", async () => {
      const b = makeBatch([{ name: "f", type: new Float64(), values: [1.5, 2], meta: { "q:typeName": "float" } }]);
      expect((JSON.parse(text(await run("json", [ipc(b)]))) as Record<string, unknown>[]).map((r) => r.f)).toEqual(["1.5", 2]);
    });
  });
});

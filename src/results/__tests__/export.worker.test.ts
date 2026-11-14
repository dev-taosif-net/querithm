import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { Table, tableToIPC } from "apache-arrow";
import { Bool, Float64, Int32, Utf8 } from "apache-arrow";
import { dynamicColumn, makeBatch, ordersBatch, type DynamicCell } from "./fixtures";
import { unzip } from "./zip";

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

describe("export worker: all result sets of a run (one save)", () => {
  const schemaOnly = () => {
    const t = new Table([makeBatch([{ name: "x", type: new Int32(), values: [] }])]);
    const bytes = tableToIPC(new Table(t.schema), "stream");
    return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer;
  };
  const other = () => ipc(makeBatch([{ name: "code", type: new Utf8(), values: ["A", "B"] }]));
  async function runAll(format: string, all: { name: string; batches: ArrayBuffer[] }[]) {
    await fakeSelf.onmessage!({ data: { format, batches: [], all } });
    return posted[0] as Uint8Array;
  }

  it("Excel: a sheet per result set, each with its own columns, header filter and name; a set without rows keeps its header", async () => {
    const files = unzip(await runAll("xlsx", [{ name: "Result set 1", batches: [ipc()] }, { name: "Result set 2", batches: [other(), other()] }, { name: "Result set 3", batches: [schemaOnly()] }]));
    const book = files.get("xl/workbook.xml")!;
    expect([...book.matchAll(/<sheet name="([^"]+)"/g)].map((m) => m[1])).toEqual(["Result set 1", "Result set 2", "Result set 3"]);
    expect(book).toContain("'Result set 1'!$A$1:$C$4");
    expect(book).toContain("'Result set 2'!$A$1:$A$5");
    expect(book).toContain("'Result set 3'!$A$1:$A$1");
    expect(files.get("xl/worksheets/sheet1.xml")).toContain('<autoFilter ref="A1:C4"/>');
    expect(files.get("xl/worksheets/sheet2.xml")).toContain(">code<");
    expect(files.get("xl/worksheets/sheet2.xml")).toContain('<autoFilter ref="A1:A5"/>');
    expect(files.get("xl/worksheets/sheet3.xml")).toMatch(/<row r="1">.*>x<.*<\/row><\/sheetData>/);
  });

  it("CSV / TSV / JSON: a .zip with a file per result set, named safely and uniquely", async () => {
    const files = unzip(await runAll("csv", [{ name: "Result set 1", batches: [ipc()] }, { name: "Result set 2", batches: [other()] }, { name: "Result set 3", batches: [schemaOnly()] }]));
    expect([...files.keys()]).toEqual(["Result set 1.csv", "Result set 2.csv", "Result set 3.csv"]);
    expect(files.get("Result set 1.csv")).toBe("id,name,qty\r\n1,one,10\r\n2,two,\r\n3,three,30\r\n");
    expect(files.get("Result set 2.csv")).toBe("code\r\nA\r\nB\r\n");
    expect(files.get("Result set 3.csv")).toBe("x\r\n");
    posted.length = 0;
    const json = unzip(await runAll("json", [{ name: "a", batches: [other()] }]));
    expect(JSON.parse(json.get("a.json")!)).toEqual([{ code: "A" }, { code: "B" }]);
  });

  it("zip entry names: no path separators or reserved characters, no trailing dots, unique ignoring case", async () => {
    const { zipEntryNames } = await import("../export.worker");
    expect(zipEntryNames(["../etc/passwd", "a:b*c?", "Same", "same", "dots. ", ""], "csv")).toEqual([
      ".._etc_passwd.csv", "a_b_c_.csv", "Same.csv", "same (2).csv", "dots.csv", "result.csv",
    ]);
  });
});

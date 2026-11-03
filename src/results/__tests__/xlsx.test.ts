import { describe, expect, it, vi } from "vitest";
import { Type } from "apache-arrow";
import { excelDate, xmlText, XlsxWriter, type XlsxColumn } from "../xlsx";
import { unzip } from "./zip";

const col = (name: string, kind: XlsxColumn["kind"], arrowTypeId = Type.Utf8): XlsxColumn => ({ name, kind, arrowTypeId });

describe("xlsx export", () => {
  it("writes a workbook Excel can open: content types, workbook, styles and a sheet with a frozen header", async () => {
    const w = new XlsxWriter([col("id", "number", Type.Int), col("name", "text")], { sheetName: "SQLQuery1-result1" });
    w.addRow(["1", "Abdul"]);
    const files = unzip(await w.finish());
    expect([...files.keys()]).toEqual(["[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml"]);
    expect(files.get("xl/workbook.xml")).toContain('<sheet name="SQLQuery1-result1" sheetId="1" r:id="rId1"/>');
    const sheet = files.get("xl/worksheets/sheet1.xml")!;
    expect(sheet).toContain('state="frozen"');
    expect(sheet).toContain('<row r="1"><c r="A1" t="inlineStr" s="1"><is><t xml:space="preserve">id</t></is></c>');
    expect(sheet).toContain('<row r="2"><c r="A2"><v>1</v></c><c r="B2" t="inlineStr"><is><t xml:space="preserve">Abdul</t></is></c></row>');
    expect(sheet).toContain('<autoFilter ref="A1:B2"/>');
  });

  it("types cells: exact numbers, booleans and dates are native; NULL is an empty cell; other values stay text", async () => {
    const w = new XlsxWriter([
      col("n", "number", Type.Int), col("big", "number", Type.Decimal), col("ok", "bool", Type.Bool),
      col("d", "temporal", Type.Date), col("ts", "temporal", Type.Timestamp), col("off", "temporal", Type.Timestamp), col("bin", "binary", Type.Binary),
    ]);
    w.addRow(["-42.5", "12345678901234567890", "1", "2024-02-29", "2024-02-29 13:30:00.250", "2024-02-29 13:30:00 +06:00", "0x0A"]);
    w.addRow([null, null, null, null, null, null, null]);
    const sheet = unzip(await w.finish()).get("xl/worksheets/sheet1.xml")!;
    expect(sheet).toContain('<c r="A2"><v>-42.5</v></c>');
    expect(sheet).toContain('<c r="B2" t="inlineStr"><is><t xml:space="preserve">12345678901234567890</t>'); // > 15 digits: kept exact
    expect(sheet).toContain('<c r="C2" t="b"><v>1</v></c>');
    expect(sheet).toContain('<c r="D2" s="4"><v>45351</v></c>');
    expect(sheet).toMatch(/<c r="E2" s="3"><v>45351\.56250289\d*<\/v><\/c>/);
    expect(sheet).toContain('<c r="F2" t="inlineStr"><is><t xml:space="preserve">2024-02-29 13:30:00 +06:00</t>');
    expect(sheet).toContain('<c r="G2" t="inlineStr"><is><t xml:space="preserve">0x0A</t>');
    expect(sheet).toContain('<row r="3"></row>');
  });

  it("escapes XML, drops characters XML cannot hold, and caps cells at Excel's 32,767 characters", async () => {
    expect(xmlText(`a<b>&"c"\u0001\uD800`)).toBe("a&lt;b&gt;&amp;&quot;c&quot;");
    expect(xmlText("😀")).toBe("😀");
    const w = new XlsxWriter([col("t", "text")]);
    w.addRow(["x".repeat(40_000)]);
    const sheet = unzip(await w.finish()).get("xl/worksheets/sheet1.xml")!;
    expect(sheet).toContain(`>${"x".repeat(32_767)}</t>`);
    expect(sheet).not.toContain("x".repeat(32_768));
  });

  it("dates Excel cannot represent (offsets, before 1900-03-01, bad times) stay text", () => {
    expect(excelDate("1899-12-31", Type.Date)).toBeNull();
    expect(excelDate("1900-03-01", Type.Date)).toEqual({ serial: 61, style: 4 });
    expect(excelDate("2024-01-01 00:00:00 +00:00", Type.Timestamp)).toBeNull();
    expect(excelDate("12:00:00", Type.Time)).toEqual({ serial: 0.5, style: 5 });
    expect(excelDate("25:00:00", Type.Time)).toBeNull();
  });

  it("rows beyond a sheet's limit continue on a new sheet, each with its header", async () => {
    const w = new XlsxWriter([col("id", "number", Type.Int)], { sheetName: "Data", maxRowsPerSheet: 2 });
    for (const v of ["1", "2", "3"]) w.addRow([v]);
    const files = unzip(await w.finish());
    expect(w.sheetCount).toBe(2);
    expect(files.get("xl/workbook.xml")).toContain('<sheet name="Data (2)" sheetId="2" r:id="rId2"/>');
    expect(files.get("xl/workbook.xml")).toContain(`localSheetId="1" hidden="1">'Data (2)'!$A$1:$A$2<`);
    const second = files.get("xl/worksheets/sheet2.xml")!;
    expect(second).toContain('<row r="1"><c r="A1"');
    expect(second).toContain('<row r="2"><c r="A2"><v>3</v></c></row>');
    expect(files.get("[Content_Types].xml")).toContain("/xl/worksheets/sheet2.xml");
  });

  it("sheet names are made legal for Excel (no []:*?/\\, at most 31 characters)", async () => {
    const w = new XlsxWriter([col("a", "text")], { sheetName: "a/b:c*d?e[f]g\\h-and-a-very-long-tail" });
    const wb = unzip(await w.finish()).get("xl/workbook.xml")!;
    const name = /<sheet name="([^"]+)"/.exec(wb)![1];
    expect(name).toBe("a_b_c_d_e_f_g_h-and-a-very-long");
    expect(name.length).toBeLessThanOrEqual(31);
  });

  it("still writes a valid (stored) zip where CompressionStream is unavailable", async () => {
    vi.stubGlobal("CompressionStream", undefined);
    try {
      const w = new XlsxWriter([col("a", "text")]);
      w.addRow(["hello"]);
      const files = unzip(await w.finish());
      expect(files.get("xl/worksheets/sheet1.xml")).toContain(">hello</t>");
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("columns letters run past Z (AA, AB…)", async () => {
    const cols = Array.from({ length: 28 }, (_, i) => col(`c${i}`, "text"));
    const w = new XlsxWriter(cols);
    w.addRow(cols.map(() => "v"));
    const sheet = unzip(await w.finish()).get("xl/worksheets/sheet1.xml")!;
    expect(sheet).toContain('<c r="AB2" t="inlineStr">');
    expect(sheet).toContain('<autoFilter ref="A1:AB2"/>');
  });
});

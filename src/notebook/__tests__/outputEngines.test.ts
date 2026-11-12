// @vitest-environment jsdom
// Notebook cell output per Phase D engine: a SQL cell's live result (each provider's typical shapes, as the sidecar's
// encoder sends them) saved into the .sqlbook file and exported to Markdown / HTML — the grid's exact text, NULL kept
// apart from text, nothing of a dynamic column's hidden class sibling.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { RecordBatch } from "apache-arrow";

vi.mock("../../editor/monaco", async () => {
  const { fake } = await import("../../editor/__tests__/fakeMonaco");
  return { initMonaco: vi.fn(), monaco: fake.monaco };
});
vi.mock("../../app/actions", () => ({
  executeOnTab: vi.fn(), trackExecution: vi.fn(), disposeExecution: vi.fn(),
  newQueryTabBinding: (p?: string, d?: string) => (p ? { profileId: p, database: d } : {}),
  workspaceHooks: {},
}));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => ({})), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../charts/chartData.worker?worker", () => ({ default: class {} }));
vi.mock("../../charts/render", () => ({ renderChartImage: () => null }));

import { newNotebookTab, serializeTab, setSaveOutputs } from "../runtime";
import { notebookOf, useNotebooks } from "../store";
import { parseNotebook } from "../format";
import { collectCells, toHtml, toMarkdown } from "../exportNotebook";
import { ArrowResultSet, ExecutionData, executionData } from "../../results/model";
import { duckDbShapes, mySqlShapes, oracleShapes, sqliteShapes } from "../../results/__tests__/fixtures";
import { useProfiles } from "../../store/profiles";
import { useTabs } from "../../store/tabs";
import type { Engine } from "../../ipc/contract";

let n = 0;
/** A notebook on `engine` whose one SQL cell shows `batch` as its live result. */
function notebookWith(engine: Engine, batch: RecordBatch): string {
  useProfiles.setState({ profiles: [{ id: "p", engine, environment: "dev" } as never] });
  const id = newNotebookTab({ id: `nb${++n}`, profileId: "p", database: "db", cells: [{ id: "c0", kind: "sql", source: "SELECT …" }] });
  const rs = new ArrowResultSet(0);
  rs.append(batch);
  rs.finish(false);
  const data = new ExecutionData(`ex${n}`);
  data.resultSets.push(rs);
  executionData.set(`ex${n}`, data);
  useNotebooks.getState().patchCell(id, "c0", { executionId: `ex${n}`, status: "done" });
  setSaveOutputs(id, true);
  return id;
}
function savedOutput(id: string) {
  const r = parseNotebook(serializeTab(id)!);
  if (!r.ok) throw new Error(r.reason);
  return r.notebook.cells[0].output!;
}
async function exported(id: string) {
  const cells = await collectCells(id);
  return { md: toMarkdown("nb", cells), html: toHtml("nb", cells) };
}

beforeEach(() => executionData.clear());
afterEach(() => {
  useTabs.setState({ tabs: {}, order: [], activeId: undefined });
  useNotebooks.setState({ docs: {} });
});

describe("SQLite", () => {
  it("saves each cell's text (numbers by class are still their exact text), NULL as null, no class column", async () => {
    const id = notebookWith("sqlite", sqliteShapes());
    expect(notebookOf(id)!.saveOutputs).toBe(true);
    expect(savedOutput(id)).toEqual({
      columns: ["id", "name", "price", "note", "data"],
      rows: [["1", "apple", "3", "7", "00ff"], ["2", 'say "hi", ok', "2.5", "n/a", null], ["3", null, "0.30000000000000004", "9223372036854775807", ""]],
      rowCount: 3,
    });
    const { md, html } = await exported(id);
    expect(md).toContain("| id | name | price | note | data |");
    expect(md).toContain('| 2 | say "hi", ok | 2.5 | n/a | NULL |');
    expect(html).toContain("<td>say &quot;hi&quot;, ok</td>");
    expect(md).not.toContain("__class");
  });
});

describe("MySQL / MariaDB", () => {
  it("saves exact decimals, the server's date text, JSON documents, BIT and unsigned BIGINT as the grid shows them", async () => {
    const id = notebookWith("mySql", mySqlShapes());
    const out = savedOutput(id);
    expect(out.columns).toEqual(["id", "tiny", "amount", "wide", "dt", "ts", "doc", "flags", "big"]);
    expect(out.rows[0]).toEqual(["1", "-128", "12.34", "12345678901234567890123456789012.123", "2026-10-31 12:34:56", "2026-10-31 12:34:56.5", '{"a": [1, 2], "b": "x,y"}', "5", "18446744073709551615"]);
    expect(out.rows[1]).toEqual(["2", "0", "-9.95", "0.000", "0000-00-00 00:00:00", null, "null", "0", "0"]);
    const { md, html } = await exported(id);
    expect(md).toContain('| 1 | -128 | 12.34 | 12345678901234567890123456789012.123 | 2026-10-31 12:34:56 | 2026-10-31 12:34:56.5 | {"a": [1, 2], "b": "x,y"} | 5 | 18446744073709551615 |');
    expect(html).toContain("<td>{&quot;a&quot;: [1, 2], &quot;b&quot;: &quot;x,y&quot;}</td>");
    expect(html).toContain("<td>null</td>"); // the JSON literal null is text, not a NULL cell
  });
});

describe("DuckDB", () => {
  it("saves exact decimals, HUGEINT, intervals, nested literals, timestamps, instants and blobs as the grid shows them", async () => {
    const id = notebookWith("duckDb", duckDbShapes());
    const out = savedOutput(id);
    expect(out.rows[0]).toEqual([
      "1", "1.50", "12345678901234567890.123456789012345678", "170141183460469231731687303715884105727", "1 year 2 months 3 days 04:05:06.789",
      "[1, NULL, 3]", "{'a': 1, 'b': 'x'}", "{'k'=1}", "2026-10-31 10:20:30", "2026-10-31 04:20:30.123456 +00:00", "0xAABB", "1",
    ]);
    expect(out.rows[2]).toEqual(["3", null, null, null, null, null, null, null, null, "1970-01-01 00:00:00 +00:00", "0x", null]);
    const { md, html } = await exported(id);
    // A nested NULL inside a list is DuckDB's text; a NULL cell is the export's NULL.
    expect(md).toContain("| 1 | 1.50 | 12345678901234567890.123456789012345678 | 170141183460469231731687303715884105727 | 1 year 2 months 3 days 04:05:06.789 | [1, NULL, 3] |");
    expect(md).toContain("| 3 | NULL | NULL |");
    expect(html).toContain("<td>{&#39;a&#39;: 1, &#39;b&#39;: &#39;x&#39;}</td>");
    expect(html).toContain("<td>[1, NULL, 3]</td>");
  });
});

describe("Oracle (D4 S12)", () => {
  it("saves exact NUMBER, BINARY_DOUBLE NaN / -Infinity, a BC DATE, zone, TIMESTAMP(9) and INTERVAL text as the grid shows them; the file stays valid", async () => {
    const id = notebookWith("oracle", oracleShapes());
    const out = savedOutput(id); // parseNotebook read it back: NaN and the infinities are text, not invalid JSON numbers
    expect(out.columns).toEqual(["ID", "AMT", "BIG", "N", "BD", "D", "TZ", "TS9", "IDS", "R", "B", "C3"]);
    expect(out.rows[0]).toEqual([
      "1", "1.250", "99999999999999999999999999999999999999", "0.3333333333333333333333333333333333333333", "1.5", "2024-02-29 13:14:15",
      "2024-07-01 02:30:00.500000 America/New_York", "2024-01-02 03:04:05.123456789", "+01 02:03:04.123456", "0xABCD", "1", "a  ",
    ]);
    expect(out.rows[1]).toEqual(["2", "-0.005", "0", "-12.5", "NaN", "-3999-03-04 05:06:07", "2024-07-01 02:30:00.000000 +05:30", null, "-00 00:00:01.000000", null, "0", null]);
    expect(out.rows[2][4]).toBe("-Infinity");
    const { md, html } = await exported(id);
    expect(md).toContain("| 2 | -0.005 | 0 | -12.5 | NaN | -3999-03-04 05:06:07 | 2024-07-01 02:30:00.000000 +05:30 | NULL |");
    expect(html).toContain("<td>2024-07-01 02:30:00.500000 America/New_York</td>");
  });
});

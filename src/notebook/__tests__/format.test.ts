import { describe, expect, it } from "vitest";
import { capOutput, hasOutputs, LIMITS, newCellId, NOTEBOOK_FORMAT, parseNotebook, serializeNotebook } from "../format";
import { deleteCell, insertCell, moveCell, neighbourAfterDelete, newCell } from "../store";

const doc = (over: Record<string, unknown> = {}) => JSON.stringify({ format: NOTEBOOK_FORMAT, version: 1, connection: null, cells: [], ...over });
const sql = (id: string, source = "select 1", over: Record<string, unknown> = {}) => ({ id, kind: "sql", source, ...over });

describe(".sqlbook validation (untrusted file)", () => {
  it("reads a good notebook and ignores unknown fields", () => {
    const r = parseNotebook(doc({ author: "x", connection: { profileId: "p1", database: "Sales", extra: 1 }, cells: [sql("a", "select 1", { color: "red" }), { id: "b", kind: "markdown", source: "# Hi" }] }));
    expect(r).toEqual({
      ok: true, warnings: [],
      notebook: { format: NOTEBOOK_FORMAT, version: 1, connection: { profileId: "p1", database: "Sales" }, cells: [{ id: "a", kind: "sql", source: "select 1" }, { id: "b", kind: "markdown", source: "# Hi" }] },
    });
  });

  it("refuses an unknown version with the reason (newer and nonsense alike)", () => {
    expect(parseNotebook(doc({ version: 2 }))).toEqual({ ok: false, reason: expect.stringMatching(/newer Querithm \(format version 2\).*version 1/) });
    expect(parseNotebook(doc({ version: "1" }))).toEqual({ ok: false, reason: expect.stringMatching(/Unknown notebook format version/) });
  });

  it("refuses what is not a notebook", () => {
    expect(parseNotebook("{")).toMatchObject({ ok: false, reason: expect.stringMatching(/not valid JSON/) });
    expect(parseNotebook("[]")).toMatchObject({ ok: false, reason: expect.stringMatching(/not a Querithm notebook/) });
    expect(parseNotebook(JSON.stringify({ format: "jupyter", version: 1, cells: [] }))).toMatchObject({ ok: false });
    expect(parseNotebook(doc({ cells: "x" }))).toMatchObject({ ok: false, reason: expect.stringMatching(/no cell list/) });
  });

  it("refuses bad cells, naming the cell", () => {
    expect(parseNotebook(doc({ cells: [sql("a"), 5] }))).toMatchObject({ ok: false, reason: "Cell 2 is not an object." });
    expect(parseNotebook(doc({ cells: [{ id: "a", kind: "python", source: "" }] }))).toMatchObject({ ok: false, reason: expect.stringMatching(/Cell 1 has an unknown kind/) });
    expect(parseNotebook(doc({ cells: [{ id: "a", kind: "sql" }] }))).toMatchObject({ ok: false, reason: "Cell 1 has no source text." });
  });

  it("enforces the size limits: file, cell count, cell source", () => {
    expect(parseNotebook(doc(), LIMITS.fileBytes + 1)).toMatchObject({ ok: false, reason: expect.stringMatching(/up to 20 MB/) });
    expect(parseNotebook(doc({ cells: Array.from({ length: LIMITS.cells + 1 }, (_, i) => sql(`c${i}`)) }))).toMatchObject({ ok: false, reason: expect.stringMatching(/2,001 cells/) });
    expect(parseNotebook(doc({ cells: [sql("a", "x".repeat(LIMITS.sourceBytes + 1))] }))).toMatchObject({ ok: false, reason: "Cell 1 is larger than 1 MB." });
    // Multi-byte text is measured in UTF-8 bytes, not characters.
    expect(parseNotebook(doc({ cells: [sql("a", "é".repeat(LIMITS.sourceBytes / 2 + 1))] }))).toMatchObject({ ok: false });
    expect(parseNotebook(doc({ cells: [sql("a", "é".repeat(LIMITS.sourceBytes / 2 - 10))] }))).toMatchObject({ ok: true });
  });

  it("missing, malformed or repeated ids get new ones", () => {
    const r = parseNotebook(doc({ cells: [sql("same"), sql("same"), { kind: "sql", source: "" }, sql("bad id!")] }));
    if (!r.ok) throw new Error(r.reason);
    const ids = r.notebook.cells.map((c) => c.id);
    expect(ids[0]).toBe("same");
    expect(new Set(ids).size).toBe(4);
    for (const id of ids) expect(id).toMatch(/^[A-Za-z0-9_-]{1,64}$/);
  });

  it("drops unreadable charts and outputs with a warning; a chart on markdown is dropped", () => {
    const out = { columns: ["a"], rows: [["1"], [null]], rowCount: 2 };
    const r = parseNotebook(doc({
      cells: [
        sql("a", "s", { chart: { kind: "bar", x: "k", y: ["v"], agg: "sum" }, view: "chart", output: out }),
        sql("b", "s", { chart: { kind: "radar" }, output: { columns: ["a"], rows: [["1", "2"]] } }),
        { id: "m", kind: "markdown", source: "", chart: { kind: "bar", x: "k", y: [], agg: "count" } },
      ],
    }));
    if (!r.ok) throw new Error(r.reason);
    expect(r.notebook.cells[0]).toMatchObject({ chart: { kind: "bar", x: "k" }, view: "chart", output: out });
    expect(r.notebook.cells[1].chart).toBeUndefined();
    expect(r.notebook.cells[1].output).toBeUndefined();
    expect(r.warnings).toEqual(["2 chart settings were not readable and were left out.", "1 saved output was not readable and was left out."]);
    expect(hasOutputs(r.notebook)).toBe(true);
  });

  it("oversized outputs are refused at open (more than 1,000 rows or 50 columns)", () => {
    const rows = Array.from({ length: LIMITS.outputRows + 1 }, () => ["x"]);
    const r = parseNotebook(doc({ cells: [sql("a", "s", { output: { columns: ["a"], rows } })] }));
    expect(r.ok && r.notebook.cells[0].output).toBeFalsy();
  });

  it("an unreadable connection opens without one, with a warning", () => {
    const r = parseNotebook(doc({ connection: { profileId: 5 } }));
    expect(r).toMatchObject({ ok: true, notebook: { connection: null }, warnings: [expect.stringMatching(/connection/)] });
  });
});

describe("writing .sqlbook", () => {
  const nb = {
    connection: { profileId: "p1", database: null },
    cells: [
      { id: "a", kind: "sql" as const, source: "select 1", chart: { kind: "bar" as const, x: "k", y: ["v"], series: null, agg: "sum" as const }, view: "chart" as const, output: { columns: ["v"], rows: [["1"]], rowCount: 1 } },
      { id: "b", kind: "markdown" as const, source: "notes" },
    ],
  };
  it("round-trips; outputs only when opted in", () => {
    const withOut = serializeNotebook(nb, true);
    const back = parseNotebook(withOut);
    expect(back.ok && back.notebook.cells).toEqual(nb.cells);
    const without = parseNotebook(serializeNotebook(nb, false));
    expect(without.ok && without.notebook.cells[0].output).toBeUndefined();
    expect(without.ok && without.notebook.cells[0].chart).toEqual(nb.cells[0].chart);
    expect(JSON.parse(withOut)).toMatchObject({ format: "querithm.sqlbook", version: 1, connection: { profileId: "p1", database: null } });
  });
  it("caps outputs to 1,000 rows × 50 columns and long values", () => {
    const cols = Array.from({ length: 60 }, (_, i) => `c${i}`);
    const rows = Array.from({ length: 1_200 }, () => cols.map(() => "v"));
    rows[0][0] = "x".repeat(5_000);
    const out = capOutput(cols, rows, 1_200);
    expect(out.columns).toHaveLength(50);
    expect(out.rows).toHaveLength(1_000);
    expect(out.rows[0]).toHaveLength(50);
    expect(out.rows[0][0]).toHaveLength(LIMITS.outputValue + 1);
    expect(out.rowCount).toBe(1_200);
  });
  it("ids are ULIDs: 26 characters, sortable by time", () => {
    const a = newCellId(1_000);
    const b = newCellId(2_000);
    expect(a).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
    expect(a.slice(0, 10) < b.slice(0, 10)).toBe(true);
  });
});

describe("cell list operations", () => {
  const cells = ["a", "b", "c"].map((id) => ({ ...newCell("sql"), id }));
  it("inserts after a cell, at the end, or at the start", () => {
    const x = { ...newCell("markdown"), id: "x" };
    expect(insertCell(cells, x, "a").map((c) => c.id)).toEqual(["a", "x", "b", "c"]);
    expect(insertCell(cells, x, "missing").map((c) => c.id)).toEqual(["a", "b", "c", "x"]);
    expect(insertCell(cells, x, undefined).map((c) => c.id)).toEqual(["a", "b", "c", "x"]);
    expect(insertCell(cells, x, null).map((c) => c.id)).toEqual(["x", "a", "b", "c"]);
  });
  it("moves within bounds only", () => {
    expect(moveCell(cells, "b", -1).map((c) => c.id)).toEqual(["b", "a", "c"]);
    expect(moveCell(cells, "b", 1).map((c) => c.id)).toEqual(["a", "c", "b"]);
    expect(moveCell(cells, "a", -1)).toBe(cells);
    expect(moveCell(cells, "c", 1)).toBe(cells);
  });
  it("deletes, and focus moves to the next cell, else the previous one", () => {
    expect(deleteCell(cells, "b").map((c) => c.id)).toEqual(["a", "c"]);
    expect(neighbourAfterDelete(cells, "b")).toBe("c");
    expect(neighbourAfterDelete(cells, "c")).toBe("b");
    expect(neighbourAfterDelete([cells[0]], "a")).toBeUndefined();
  });
  it("a new empty markdown cell opens for editing", () => {
    expect(newCell("markdown")).toMatchObject({ kind: "markdown", editing: true, status: "idle" });
    expect(newCell("markdown", "x").editing).toBe(false);
  });
});

import { describe, expect, it, vi } from "vitest";

vi.mock("../monaco", () => ({ monaco: { languages: { CompletionItemKind: {} } } }));
vi.mock("../editors", () => ({ tabIdOfModel: () => "t1", modelOwner: (k?: string) => k, onEditorCreated: () => {}, onModelContent: () => {} }));
const rpc = vi.fn();
vi.mock("../../ipc/client", () => ({ rpc: (...a: unknown[]) => rpc(...a), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));

const { formatText } = await import("../providers");

// The same terminator / blank-line guarantees the T-SQL formatter has (TSqlFormattingTests), for every engine.
describe("formatText per engine", () => {
  it("pg keeps every statement terminator", async () => {
    const r = await formatText("select 1;\nselect 2;", "pg");
    expect(r.error).toBeFalsy();
    expect(r.text.split(";").length - 1).toBe(2);
  });
  it("pg keeps MERGE terminated so the next statement still parses", async () => {
    const r = await formatText("merge into t using s on t.id = s.id when matched then update set a = s.a;\nselect 1", "pg");
    expect(r.text).toMatch(/s\.a;\n/);
  });
  it("pg collapses runs of blank lines between statements to one", async () => {
    const r = await formatText("select 1;\n\n\n\n\nselect 2;", "pg");
    expect(r.text).toContain("1;\n\nSELECT");
    expect(r.text).not.toContain("\n\n\n");
  });
  it("pg returns the text unchanged with a visible error when it cannot format", async () => {
    const r = await formatText("select 'unterminated", "pg");
    expect(r.text).toBe("select 'unterminated");
    expect(r.error).toBeTruthy();
  });
  it("tsql formats in the sidecar (ScriptDom)", async () => {
    rpc.mockResolvedValueOnce({ text: "SELECT 1;" });
    expect(await formatText("select 1;", "tsql")).toEqual({ text: "SELECT 1;" });
    expect(rpc).toHaveBeenCalledWith("lang.format", expect.objectContaining({ text: "select 1;", dialect: "tsql" }));
  });
  it.each(["sqlite", "mySql"] as const)("%s formats locally (sql-formatter in its own dialect), keeps terminators, never asks the sidecar", async (dialect) => {
    rpc.mockClear();
    const r = await formatText("select 1;\nselect 2;", dialect);
    expect(r.error).toBeFalsy();
    expect(r.text.split(";").length - 1).toBe(2);
    expect(rpc).not.toHaveBeenCalled();
  });
  it("sqlite keeps its own syntax: [bracketed] and \"quoted\" names, ?NNN and :name parameters", async () => {
    const r = await formatText("select [a b], \"c\" from t where x = ?1 and y = :y;", "sqlite");
    expect(r.error).toBeFalsy();
    for (const s of ["[a b]", '"c"', "?1", ":y"]) expect(r.text).toContain(s);
  });
  it("mySql keeps backticks and # comments", async () => {
    const r = await formatText("select `order` from t; # note", "mySql");
    expect(r.error).toBeFalsy();
    expect(r.text).toContain("`order`");
    expect(r.text).toContain("# note");
  });

  // D4 S06: Oracle formats statement by statement (oracleFormat.ts).
  it("oracle keeps q'' literals, binds and every / line alone on its line; formats only plain SQL, never asks the sidecar", async () => {
    rpc.mockClear();
    const src = "select q'[it's; /]' a, :b from dual where x = 1;\ncreate or replace procedure p as begin dbms_output.put_line('x'); end;\n/\nselect 2 from dual\n/\n";
    const r = await formatText(src, "oracle");
    expect(r.error).toBeFalsy();
    expect(rpc).not.toHaveBeenCalled();
    expect(r.text).toContain("q'[it's; /]'");
    expect(r.text).toContain(":b");
    // The PL/SQL unit is kept as written, its / on its own line; the last statement's / too.
    expect(r.text).toContain("create or replace procedure p as begin dbms_output.put_line('x'); end;\n/\n");
    const lines = r.text.split("\n");
    expect(lines.filter((l) => l.includes("/") && !l.includes("q'"))).toEqual(["/", "/"]);
    expect(r.text).toMatch(/^SELECT\n/);
  });
  it("oracle keeps comments between statements and SQL*Plus lines as written", async () => {
    const r = await formatText("SET SERVEROUTPUT ON\n-- totals\nselect 1 from dual;\nselect 2 from dual;", "oracle");
    expect(r.text.startsWith("SET SERVEROUTPUT ON\n-- totals\nSELECT")).toBe(true);
    expect(r.text.split(";").length - 1).toBe(2);
  });
  it("oracle: a selection holding only part of a block is left as it is (no SQL statement in it)", async () => {
    expect(await formatText("BEGIN\n  x := 1;", "oracle")).toEqual({ text: "BEGIN\n  x := 1;" });
  });
});

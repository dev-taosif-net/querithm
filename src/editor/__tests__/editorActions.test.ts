import { beforeEach, describe, expect, it, vi } from "vitest";
import { FakeEditor, FakeModel } from "./fakeMonaco";
import { IDX, PG_IDX } from "../completion/__tests__/fixture";
import { useTabs } from "../../store/tabs";

const state = vi.hoisted(() => ({ editor: undefined as unknown, model: undefined as unknown, ctx: null as unknown }));
const toast = vi.hoisted(() => vi.fn());
const openDefinition = vi.hoisted(() => vi.fn());
const scriptObject = vi.hoisted(() => vi.fn());

vi.mock("../editors", () => ({ editorForTab: () => state.editor, getModel: () => state.model }));
vi.mock("../providers", () => ({ docContext: () => state.ctx }));
vi.mock("../../app/actions", () => ({ openDefinition, scriptObject }));
vi.mock("../../store/ui", () => ({ toast }));

const { commentLines, goToDefinition, insertPrintForVariable } = await import("../editorActions");

function setup(text: string, cursor: number, lang: "tsql" | "pgsql", ctx: unknown) {
  const model = new FakeModel(text, lang, { toString: () => "u" });
  const ed = new FakeEditor(null, {});
  ed.position = model.getPositionAt(cursor);
  state.editor = ed; state.model = model; state.ctx = ctx;
  return { model, ed };
}

beforeEach(() => {
  vi.clearAllMocks();
  state.editor = state.model = undefined; state.ctx = null;
});

describe("goToDefinition", () => {
  it("does nothing without an editor", async () => {
    await goToDefinition("t");
    expect(toast).not.toHaveBeenCalled();
  });

  it("asks to connect when the tab has no database (offline / disconnected)", async () => {
    setup("select 1", 3, "tsql", { dialect: "tsql", schema: null });
    await goToDefinition("t");
    expect(toast).toHaveBeenCalledWith("info", "Connect this tab to a database first.");
    expect(openDefinition).not.toHaveBeenCalled();
  });

  it("does nothing when the document has no context at all", async () => {
    setup("select 1", 3, "tsql", null);
    await goToDefinition("t");
    expect(toast).toHaveBeenCalledWith("info", "Connect this tab to a database first.");
  });

  it("T-SQL: opens a procedure's definition", async () => {
    const text = "exec dbo.usp_GetOrderDetails 1";
    setup(text, text.indexOf("usp_") + 2, "tsql", { dialect: "tsql", profileId: "p1", database: "SalesDB", schema: IDX });
    await goToDefinition("t");
    expect(openDefinition).toHaveBeenCalledWith("p1", "SalesDB", { schema: "dbo", name: "usp_GetOrderDetails", kind: "proc", objectId: 7 }, false);
  });

  it("T-SQL: a table is scripted as CREATE rather than opened", async () => {
    const text = "select * from dbo.Orders";
    setup(text, text.indexOf("Orders") + 2, "tsql", { dialect: "tsql", profileId: "p1", database: "SalesDB", schema: IDX });
    await goToDefinition("t");
    expect(scriptObject).toHaveBeenCalledWith("p1", "SalesDB", { schema: "dbo", name: "Orders", kind: "table", objectId: 1 }, "create");
    expect(openDefinition).not.toHaveBeenCalled();
  });

  it("PostgreSQL: a function opens its definition, a table is scripted", async () => {
    const f = "select public.order_total(1)";
    setup(f, f.indexOf("order_total") + 2, "pgsql", { dialect: "pg", profileId: "p2", database: "shop", schema: PG_IDX });
    await goToDefinition("t");
    expect(openDefinition).toHaveBeenCalledWith("p2", "shop", { schema: "public", name: "order_total", kind: "func", objectId: 105 }, false);
    const t = "select * from public.orders";
    setup(t, t.indexOf("orders") + 2, "pgsql", { dialect: "pg", profileId: "p2", database: "shop", schema: PG_IDX });
    await goToDefinition("t");
    expect(scriptObject).toHaveBeenCalledWith("p2", "shop", expect.objectContaining({ name: "orders", kind: "table" }), "create");
  });

  it("a column resolves to its table (both engines)", async () => {
    const text = "select o.OrderId from dbo.Orders o";
    setup(text, text.indexOf("OrderId") + 2, "tsql", { dialect: "tsql", profileId: "p1", database: "SalesDB", schema: IDX });
    await goToDefinition("t");
    expect(scriptObject).toHaveBeenCalledWith("p1", "SalesDB", expect.objectContaining({ name: "Orders" }), "create");
    const pg = "select o.order_id from public.orders o";
    setup(pg, pg.indexOf("order_id") + 2, "pgsql", { dialect: "pg", profileId: "p2", database: "shop", schema: PG_IDX });
    await goToDefinition("t");
    expect(scriptObject).toHaveBeenLastCalledWith("p2", "shop", expect.objectContaining({ name: "orders" }), "create");
  });

  it("says so when nothing under the cursor is a database object", async () => {
    setup("select 1", 3, "tsql", { dialect: "tsql", profileId: "p1", database: "SalesDB", schema: IDX });
    await goToDefinition("t");
    expect(toast).toHaveBeenCalledWith("info", "No database object under the cursor.");
  });
});

describe("insertPrintForVariable", () => {
  beforeEach(() => {
    useTabs.setState({ tabs: { t: { id: "t", title: "q" } as never } });
  });

  it("does nothing without an editor", () => {
    insertPrintForVariable("t");
    expect(toast).not.toHaveBeenCalled();
  });

  it("inserts an indented PRINT below the line, tagged for removal", () => {
    const text = "begin\n    set @total = 1\nend";
    const { ed } = setup(text, text.indexOf("@total") + 2, "tsql", null);
    insertPrintForVariable("t");
    expect(ed.edits).toHaveLength(1);
    const e = ed.edits[0].edits[0];
    expect(e.text).toBe("    PRINT CONCAT(N'@total = ', CONVERT(nvarchar(max), @total)); -- querithm:trace\n");
    expect(e.range).toMatchObject({ startLineNumber: 3, startColumn: 1 });
    expect(toast).not.toHaveBeenCalled();
  });

  it("explains how to read the value when the tab is bound to a stored object", () => {
    useTabs.setState({ tabs: { t: { id: "t", title: "q", boundObject: { name: "p" } } as never } });
    setup("select @x", 8, "tsql", null);
    insertPrintForVariable("t");
    expect(toast).toHaveBeenCalledWith("info", expect.stringContaining("querithm:trace"));
  });

  it("PostgreSQL has no PRINT: a quoted name that starts with @ gets the hint, never T-SQL text", () => {
    const { ed } = setup('select "@x" from t', 9, "pgsql", null);
    insertPrintForVariable("t");
    expect(toast).toHaveBeenCalledWith("info", "Place the cursor on a @variable.");
    expect(ed.edits).toHaveLength(0);
  });

  it("asks for a @variable when the cursor is on something else (T-SQL and PostgreSQL)", () => {
    const { ed } = setup("select name from t", 9, "tsql", null);
    insertPrintForVariable("t");
    expect(toast).toHaveBeenCalledWith("info", "Place the cursor on a @variable.");
    expect(ed.edits).toHaveLength(0);
    const pg = setup("select name from t", 9, "pgsql", null);
    insertPrintForVariable("t");
    expect(toast).toHaveBeenCalledTimes(2);
    expect(pg.ed.edits).toHaveLength(0);
  });
});

describe("commentLines", () => {
  it("does nothing without an editor", () => expect(() => commentLines("t", "toggle")).not.toThrow());

  // Both dialects share the `--` / `/* */` language configuration that Monaco's comment actions read.
  it.each(["tsql", "pgsql"] as const)("%s: refocuses the editor (toolbar buttons take focus) and runs Monaco's comment actions", (lang) => {
    const { ed } = setup("select 1", 0, lang, null);
    const focus = vi.fn();
    Object.assign(ed, { focus });
    commentLines("t", "toggle");
    commentLines("t", "add");
    commentLines("t", "remove");
    commentLines("t", "block");
    expect(focus).toHaveBeenCalledTimes(4);
    expect(ed.triggered).toEqual([
      "querithm:editor.action.commentLine",
      "querithm:editor.action.addCommentLine",
      "querithm:editor.action.removeCommentLine",
      "querithm:editor.action.blockComment",
    ]);
  });
});

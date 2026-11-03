// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../../editor/monaco", async () => {
  const { fake } = await import("../../editor/__tests__/fakeMonaco");
  return { initMonaco: vi.fn(), monaco: fake.monaco };
});
const act = vi.hoisted(() => ({
  executeOnTab: vi.fn(),
  trackExecution: vi.fn(),
  disposeExecution: vi.fn(),
  onTabAdded: vi.fn(),
  sql: [] as string[],
}));
vi.mock("../../app/actions", () => ({
  executeOnTab: act.executeOnTab,
  trackExecution: act.trackExecution,
  disposeExecution: act.disposeExecution,
  newQueryTabBinding: (p?: string, d?: string) => (p ? { profileId: p, database: d } : {}),
  workspaceHooks: { onTabAdded: act.onTabAdded },
}));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => ({})), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
const toast = vi.hoisted(() => vi.fn());
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast }));

const { fake } = await import("../../editor/__tests__/fakeMonaco");
import { attachEditor, getModel, modelOwner } from "../../editor/editors";
import { useExecutions } from "../../store/executions";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useTabs } from "../../store/tabs";
import { notebookHooks } from "../hooks";
import {
  addCell, cancelNotebook, cellFailed, cellKey, cellModel, cellSource, newNotebookTab, removeCell, runAll, runCell, serializeTab, setCellKind, setSaveOutputs, shiftCell,
} from "../runtime";
import { notebookOf, useNotebooks } from "../store";
import { parseNotebook } from "../format";
import { ArrowResultSet, ExecutionData, executionData } from "../../results/model";
import { ordersBatch } from "../../results/__tests__/fixtures";

let n = 0;
/** executeOnTab accepts every cell; trackExecution registers a running view the test then ends. */
function acceptRuns() {
  act.executeOnTab.mockImplementation(async (_tab: string, sql: string, _lo: number, _x: unknown, before?: () => void) => {
    before?.();
    act.sql.push(sql);
    return { sessionId: "s1", executionId: `e${++n}`, options: {} };
  });
  act.trackExecution.mockImplementation((tabId: string, sessionId: string, executionId: string, sql: string, _lo: number, register?: () => void) => {
    useExecutions.getState().put({ id: executionId, tabId, sessionId, state: "running", startedAt: 0, resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql, lineOffset: 1 });
    register?.();
  });
}
const end = (id: string, state: "done" | "failed" | "cancelled", error = false) =>
  useExecutions.getState().patch(id, { state, messages: error ? [{ executionId: id, severity: "error", message: "boom" } as never] : [] });
const flush = () => new Promise((r) => setTimeout(r, 0));
async function until(cond: () => boolean) {
  for (let i = 0; i < 50 && !cond(); i++) await flush();
  expect(cond()).toBe(true);
}

function notebook(sources: (string | [string, "markdown"])[] = ["select 1", "select 2", "select 3"]) {
  const cells = sources.map((s, i) => (Array.isArray(s) ? { id: `c${i}`, kind: s[1], source: s[0] } : { id: `c${i}`, kind: "sql" as const, source: s }));
  return newNotebookTab({ id: `nb${++n}`, profileId: "p1", database: "Sales", cells });
}

beforeEach(() => {
  act.sql = [];
  vi.clearAllMocks();
  useExecutions.setState({ views: {} });
  useProfiles.setState({ profiles: [{ id: "p1", engine: "pg", environment: "dev" } as never] });
  useSettings.setState({ notebookStopOnError: true });
  acceptRuns();
});
afterEach(() => {
  useTabs.setState({ tabs: {}, order: [], activeId: undefined });
  useNotebooks.setState({ docs: {} });
});

describe("notebook tabs", () => {
  it("a new notebook is a tab with one empty SQL cell, focused, on the active connection", () => {
    const id = newNotebookTab({ profileId: "p1", database: "Sales" });
    expect(useTabs.getState().tabs[id]).toMatchObject({ notebook: true, title: expect.stringMatching(/^Notebook\d+\.sqlbook$/), profileId: "p1", database: "Sales" });
    const doc = notebookOf(id)!;
    expect(doc.cells).toHaveLength(1);
    expect(doc.cells[0]).toMatchObject({ kind: "sql", source: "" });
    expect(doc.focused).toBe(doc.cells[0].id);
    expect(act.onTabAdded).toHaveBeenCalledWith(id, true, false);
  });

  it("each SQL cell gets its own model, owned by the notebook (completion reads its connection), in its engine's language", () => {
    const id = notebook();
    const cell = notebookOf(id)!.cells[0];
    const m = cellModel(id, cell);
    expect(m.getValue()).toBe("select 1");
    expect(m.getLanguageId()).toBe("pgsql");
    expect(getModel(cellKey(id, cell.id))).toBe(m);
    expect(modelOwner(cellKey(id, cell.id))).toBe(id);
    (m as unknown as { setValue(t: string): void }).setValue("select 42");
    expect(cellSource(id, cell)).toBe("select 42");
    // Moving the notebook to SQL Server switches its cells' language.
    useProfiles.setState({ profiles: [{ id: "p1", engine: "pg" } as never, { id: "p2", engine: "mssql" } as never] });
    useTabs.getState().update(id, { profileId: "p2" });
    expect(m.getLanguageId()).toBe("tsql");
  });

  it("one shared editor for every cell: focusing another cell swaps the model, never a second editor", () => {
    const id = notebook();
    const [a, b] = notebookOf(id)!.cells;
    cellModel(id, a);
    cellModel(id, b);
    const before = fake.created.length;
    const e1 = attachEditor("g-nb", document.createElement("div"), cellKey(id, a.id), false, true) as unknown as { getModel(): unknown; options: Record<string, unknown> };
    const e2 = attachEditor("g-nb", document.createElement("div"), cellKey(id, b.id), false, true) as unknown as { getModel(): unknown; options: Record<string, unknown> };
    expect(e2).toBe(e1);
    expect(fake.created.length).toBeLessThanOrEqual(before + 1);
    expect(e2.getModel()).toBe(getModel(cellKey(id, b.id)));
    // Compact in a cell; the full editor again when a query tab takes it back.
    expect(e2.options.minimap).toEqual({ enabled: false });
    expect(e2.options.lineNumbers).toBe("off");
    attachEditor("g-nb", document.createElement("div"), cellKey(id, a.id), false, false);
    expect((e2.options.minimap as { enabled: boolean }).enabled).toBe(useSettings.getState().minimap);
  });

  it("adds, moves, retypes and deletes cells; each change marks the notebook modified", () => {
    const id = notebook();
    useTabs.getState().update(id, { dirty: false });
    const added = addCell(id, "markdown", "c0")!;
    expect(notebookOf(id)!.cells.map((c) => c.id)).toEqual(["c0", added, "c1", "c2"]);
    expect(notebookOf(id)!.focused).toBe(added);
    expect(useTabs.getState().tabs[id].dirty).toBe(true);
    shiftCell(id, "c2", -1);
    expect(notebookOf(id)!.cells.map((c) => c.id)).toEqual(["c0", added, "c2", "c1"]);
    cellModel(id, notebookOf(id)!.cells[0]);
    setCellKind(id, "c0", "markdown");
    expect(notebookOf(id)!.cells[0]).toMatchObject({ kind: "markdown", source: "select 1", editing: true });
    expect(getModel(cellKey(id, "c0"))).toBeUndefined();
    removeCell(id, added);
    expect(notebookOf(id)!.cells.map((c) => c.id)).toEqual(["c0", "c2", "c1"]);
    expect(notebookOf(id)!.focused).toBe("c2");
  });
});

describe("running cells", () => {
  it("run all: SQL cells in order, one execution each, markdown and empty cells skipped", async () => {
    const id = notebook(["select 1", ["# notes", "markdown"], "   ", "select 3"]);
    const done = runAll(id);
    await until(() => act.sql.length === 1);
    expect(notebookOf(id)!.cells[3].status).toBe("queued");
    expect(notebookOf(id)!.running).toBe("c0");
    expect(useTabs.getState().tabs[id]).toMatchObject({ activeExecutionId: "e" + n, busy: true });
    end(`e${n}`, "done");
    await until(() => act.sql.length === 2);
    end(`e${n}`, "done");
    await done;
    expect(act.sql).toEqual(["select 1", "select 3"]);
    expect(notebookOf(id)!.cells.map((c) => c.status)).toEqual(["done", "idle", "idle", "done"]);
    expect(notebookOf(id)!.cells[3].runNumber).toBe(2);
    expect(notebookOf(id)!.running).toBeUndefined();
  });

  it("a failing middle cell stops the queue; the cells after it say why", async () => {
    const id = notebook();
    const done = runAll(id);
    await until(() => act.sql.length === 1);
    end(`e${n}`, "done");
    await until(() => act.sql.length === 2);
    end(`e${n}`, "done", true); // SQL Server: the batch ended with an error message
    await done;
    expect(act.sql).toEqual(["select 1", "select 2"]);
    const cells = notebookOf(id)!.cells;
    expect(cells.map((c) => c.status)).toEqual(["done", "failed", "idle"]);
    expect(cells[2].note).toBe("Not run: cell 2 failed.");
  });

  it("with stop-on-error off the queue goes on after a failure", async () => {
    useSettings.setState({ notebookStopOnError: false });
    const id = notebook();
    const done = runAll(id);
    for (let i = 1; i <= 3; i++) {
      await until(() => act.sql.length === i);
      end(`e${n}`, i === 2 ? "failed" : "done");
    }
    await done;
    expect(notebookOf(id)!.cells.map((c) => c.status)).toEqual(["done", "failed", "done"]);
  });

  it("Cancel stops the running cell's queue: the cancelled cell and the rest are marked, outputs kept", async () => {
    const id = notebook();
    const done = runAll(id);
    await until(() => act.sql.length === 1);
    end(`e${n}`, "done");
    await until(() => act.sql.length === 2);
    cancelNotebook(id);
    end(`e${n}`, "cancelled");
    await done;
    const cells = notebookOf(id)!.cells;
    expect(cells.map((c) => c.status)).toEqual(["done", "cancelled", "idle"]);
    expect(cells[2].note).toBe("Not run: cancelled.");
    expect(cells[0].executionId).toBeDefined(); // the first cell keeps its output
  });

  it("a guard the user declined (or a failed connect) runs nothing more", async () => {
    const id = notebook();
    act.executeOnTab.mockResolvedValueOnce(undefined);
    await runAll(id);
    const cells = notebookOf(id)!.cells;
    expect(cells.map((c) => c.status)).toEqual(["idle", "idle", "idle"]);
    expect(cells[0].note).toBe("Not run.");
    expect(cells[1].note).toBe("Not run: cell 1 did not run.");
  });

  it("a statement the sidecar refuses (ATTACH on SQLite) fails its cell with the reason; the rest do not run", async () => {
    const id = notebook();
    act.executeOnTab.mockImplementationOnce(async (_t: string, _s: string, _l: number, _x: unknown, _b: unknown, onRefused?: (m: string) => void) => {
      onRefused?.("ATTACH is not allowed: Querithm opens only the file you picked.");
      return undefined;
    });
    await runAll(id);
    const cells = notebookOf(id)!.cells;
    expect(cells[0]).toMatchObject({ status: "failed", note: "Refused: ATTACH is not allowed: Querithm opens only the file you picked." });
    expect(cells[1].note).toBe("Not run: cell 1 did not run.");
  });

  it("the run's notes (MySQL user variables, a new in-memory database) reach the cell's execution view", async () => {
    const id = notebook(["select @v"]);
    act.executeOnTab.mockImplementationOnce(async () => ({ sessionId: "s1", executionId: "en", options: {}, notes: ["MySQL: @v is a user variable here — set it with SET @v = … in the script."] }));
    const run = runAll(id);
    await until(() => act.trackExecution.mock.calls.length === 1);
    expect(act.trackExecution.mock.calls[0][6]).toEqual(["MySQL: @v is a user variable here — set it with SET @v = … in the script."]);
    end("en", "done");
    await run;
  });

  it("the in-memory database the cells ran on is gone: their results go and each run cell says why", () => {
    const id = notebook(["select 1", ["notes", "markdown"], "select 2"]);
    useNotebooks.getState().patchCell(id, "c0", { executionId: "x1", status: "done" });
    notebookHooks.outputsLost!(id, "The in-memory database was lost.");
    const cells = notebookOf(id)!.cells;
    expect(act.disposeExecution).toHaveBeenCalledWith("x1");
    expect(cells[0]).toMatchObject({ executionId: undefined, status: "idle", note: "The in-memory database was lost." });
    expect(cells[1].note).toBeUndefined(); // markdown
    expect(cells[2].note).toBeUndefined(); // never ran
  });

  it("re-running a cell disposes its previous execution; a second run while running is refused", async () => {
    const id = notebook();
    const first = runCell(id, "c0");
    await until(() => act.sql.length === 1);
    await runAll(id);
    expect(toast).toHaveBeenCalledWith("info", expect.stringMatching(/already running/));
    end(`e${n}`, "done");
    await first;
    const prev = notebookOf(id)!.cells[0].executionId!;
    const again = runCell(id, "c0");
    await until(() => act.sql.length === 2);
    expect(act.disposeExecution).toHaveBeenCalledWith(prev);
    end(`e${n}`, "done");
    await again;
  });

  it("without a connection nothing runs and the connection picker opens", async () => {
    const id = newNotebookTab({ profileId: "", cells: [{ id: "c0", kind: "sql", source: "select 1" }] });
    await runAll(id);
    expect(act.executeOnTab).not.toHaveBeenCalled();
    expect(toast).toHaveBeenCalledWith("info", expect.stringMatching(/Pick a connection/));
  });

  it("Run (F5) runs the focused cell; Shift+Enter moves focus on; a markdown cell is rendered", async () => {
    const id = notebook(["select 1", ["# x", "markdown"]]);
    useNotebooks.getState().patch(id, { focused: "c1" });
    useNotebooks.getState().patchCell(id, "c1", { editing: true });
    await notebookHooks.run!(id);
    expect(notebookOf(id)!.cells[1].editing).toBe(false);
    expect(act.executeOnTab).not.toHaveBeenCalled();
    useNotebooks.getState().patch(id, { focused: "c0" });
    const p = runCell(id, "c0", true);
    expect(notebookOf(id)!.focused).toBe("c1");
    await until(() => act.sql.length === 1);
    end(`e${n}`, "done");
    await p;
  });

  it("what counts as a failure", () => {
    expect(cellFailed(undefined)).toBe(true);
    expect(cellFailed({ state: "done", error: null, messages: [] })).toBe(false);
    expect(cellFailed({ state: "done", error: null, messages: [{ severity: "info" } as never] })).toBe(false);
    expect(cellFailed({ state: "done", error: null, messages: [{ severity: "error" } as never] })).toBe(true);
    expect(cellFailed({ state: "failed", error: null, messages: [] })).toBe(true);
    expect(cellFailed({ state: "cancelled", error: null, messages: [] })).toBe(true);
  });
});

describe("the notebook as a document", () => {
  it("serializes the live sources and connection; results only when Save results is on", async () => {
    const id = notebook(["select 1", ["notes", "markdown"]]);
    (cellModel(id, notebookOf(id)!.cells[0]) as unknown as { setValue(t: string): void }).setValue("select 99");
    // A live result for cell 0.
    const data = new ExecutionData("ex1");
    const rs = new ArrowResultSet(0);
    rs.append(ordersBatch());
    rs.finish(false);
    data.resultSets.push(rs);
    executionData.set("ex1", data);
    useNotebooks.getState().patchCell(id, "c0", { executionId: "ex1" });
    let r = parseNotebook(serializeTab(id)!);
    if (!r.ok) throw new Error(r.reason);
    expect(r.notebook.connection).toEqual({ profileId: "p1", database: "Sales" });
    expect(r.notebook.cells.map((c) => [c.kind, c.source])).toEqual([["sql", "select 99"], ["markdown", "notes"]]);
    expect(r.notebook.cells[0].output).toBeUndefined();
    setSaveOutputs(id, true);
    r = parseNotebook(serializeTab(id)!);
    if (!r.ok) throw new Error(r.reason);
    expect(r.notebook.cells[0].output).toEqual({ columns: ["id", "name", "qty"], rows: [["1", "one", "10"], ["2", "two", null], ["3", "three", "30"]], rowCount: 3 });
    expect(notebookHooks.text!(id)).toBe("select 99\n\nnotes");
  });

  it("closing disposes the cells' models and executions", () => {
    const id = notebook();
    cellModel(id, notebookOf(id)!.cells[0]);
    useNotebooks.getState().patchCell(id, "c1", { executionId: "x9" });
    notebookHooks.dispose!(id);
    expect(getModel(cellKey(id, "c0"))).toBeUndefined();
    expect(act.disposeExecution).toHaveBeenCalledWith("x9");
    expect(notebookOf(id)).toBeUndefined();
  });
});

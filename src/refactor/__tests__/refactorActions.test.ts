import { beforeEach, describe, expect, it, vi } from "vitest";
import { FakeModel } from "../../editor/__tests__/fakeMonaco";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useUsages } from "../../store/usages";
import { useUi } from "../../store/ui";
import { useConnections } from "../../store/connections";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), toast: vi.fn(), activateTab: vi.fn(), newQueryTab: vi.fn(), setBottomTab: vi.fn(), openReadOnlyDefinition: vi.fn(),
  models: new Map<string, unknown>(), editors: new Map<string, unknown>(),
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../app/actions", () => ({ activateTab: m.activateTab, newQueryTab: m.newQueryTab, setBottomTab: m.setBottomTab, openReadOnlyDefinition: m.openReadOnlyDefinition }));
vi.mock("../../editor/editors", () => ({ getModel: (id: string) => m.models.get(id), editorForTab: (id: string) => m.editors.get(id) }));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast: m.toast }));

const A = await import("../refactorActions");

class EditModel extends FakeModel {
  ops: Array<{ range: any; text: string }> = [];
  stack: string[] = [];
  pushStackElement() { this.stack.push("stack"); }
  pushEditOperations(_s: unknown, ops: Array<{ range: any; text: string }>) { this.ops.push(...ops); this.stack.push("edit"); return null; }
}
const model = (text: string, lang = "tsql") => new EditModel(text, lang, { toString: () => "u" });
const tab = (id: string, over: Record<string, unknown> = {}) => ({ id, title: `${id}.sql`, profileId: "p", database: "db", ...over }) as never;

function editorAt(model: EditModel, offset: number) {
  return {
    getPosition: () => model.getPositionAt(offset),
    setSelection: vi.fn(), revealLineInCenter: vi.fn(), focus: vi.fn(),
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.useRealTimers();
  m.models.clear(); m.editors.clear();
  useTabs.setState({ tabs: {}, order: [], activeId: undefined } as never);
  useProfiles.setState({ profiles: [{ id: "p", engine: "mssql" } as never, { id: "pg", engine: "pg" } as never] });
  useUsages.setState({ byTab: {} });
  useUi.setState({ dialog: undefined, dialogs: [] } as never);
  useConnections.setState({ sessionFor: vi.fn().mockResolvedValue("sess") } as never);
});

describe("openTabsFor", () => {
  it("lists the text of editable tabs on that database that have a model", () => {
    useTabs.setState({
      tabs: { a: tab("a"), b: tab("b", { readOnly: true }), c: tab("c", { database: "other" }), d: tab("d"), e: tab("e", { profileId: "x" }) },
      order: ["a", "b", "c", "d", "e"],
    } as never);
    m.models.set("a", model("select 1"));
    m.models.set("b", model("ro"));
    expect(A.openTabsFor("p", "db")).toEqual([{ tabId: "a", title: "a.sql", text: "select 1" }]);
  });
});

describe("findUsagesAtCursor", () => {
  function setup(text: string, offset: number, tabOver: Record<string, unknown> = {}) {
    const mod = model(text);
    useTabs.setState({ tabs: { t: tab("t", tabOver) }, order: ["t"] } as never);
    m.models.set("t", mod);
    m.editors.set("t", editorAt(mod, offset));
    return mod;
  }

  it("does nothing when the tab has no editor", async () => {
    await A.findUsagesAtCursor("t");
    expect(m.rpc).not.toHaveBeenCalled();
  });

  it("lists a local symbol's occurrences without asking the server again", async () => {
    setup("declare @x int;\nset @x = 1;", 10);
    m.rpc.mockResolvedValueOnce({ local: [{ offset: 8, length: 2 }, { offset: 20, length: 2 }], target: null });
    await A.findUsagesAtCursor("t");
    expect(m.rpc).toHaveBeenCalledWith("refactor.symbolAt", { sessionId: "sess", database: "db", dialect: "tsql", text: "declare @x int;\nset @x = 1;", offset: 10 });
    const v = useUsages.getState().byTab.t;
    expect(v.running).toBe(false);
    expect(v.local!.name).toBe("@x");
    expect(v.local!.spans[1]).toMatchObject({ line: 2, column: 5, snippet: "set @x = 1;" });
    expect(m.setBottomTab).toHaveBeenCalledWith("t", "usages");
  });

  it("sends the pg dialect for PostgreSQL tabs", async () => {
    setup("select 1", 3, { profileId: "pg" });
    m.rpc.mockResolvedValueOnce({ local: null, target: null });
    await A.findUsagesAtCursor("t");
    expect(m.rpc.mock.calls[0][1].dialect).toBe("pg");
  });

  it("explains when nothing is under the cursor", async () => {
    setup("select 1", 3);
    m.rpc.mockResolvedValueOnce({ local: null, target: null });
    await A.findUsagesAtCursor("t");
    expect(m.toast).toHaveBeenCalledWith("info", expect.stringContaining("No database object"));
  });

  it("searches the database for an object, passing the open tabs", async () => {
    setup("select * from dbo.Orders", 20);
    const target = { kind: "table", schema: "dbo", name: "Orders", column: null };
    m.rpc.mockResolvedValueOnce({ local: null, target });
    const result = { target, hits: [], unchecked: [], serverUpdated: [], objectsScanned: 4, elapsedMs: 1 };
    m.rpc.mockResolvedValueOnce(result);
    await A.findUsagesAtCursor("t");
    expect(m.rpc).toHaveBeenLastCalledWith("refactor.findUsages", { sessionId: "sess", database: "db", target, tabs: [{ tabId: "t", title: "t.sql", text: "select * from dbo.Orders" }] });
    expect(useUsages.getState().byTab.t).toEqual({ target, result, running: false });
  });

  it("works without a connection (no session) and reports server errors", async () => {
    setup("select 1", 3, { profileId: undefined, database: undefined });
    m.rpc.mockRejectedValueOnce(new Error("sidecar down"));
    await A.findUsagesAtCursor("t");
    expect(m.rpc.mock.calls[0][1]).toMatchObject({ sessionId: null, database: null });
    expect(m.toast).toHaveBeenCalledWith("error", "sidecar down");
  });

  it("falls back to a null session when connecting fails", async () => {
    setup("select 1", 3);
    useConnections.setState({ sessionFor: vi.fn().mockRejectedValue(new Error("no")) } as never);
    m.rpc.mockResolvedValueOnce({ local: null, target: null });
    await A.findUsagesAtCursor("t");
    expect(m.rpc.mock.calls[0][1].sessionId).toBeNull();
  });
});

describe("runFindUsages", () => {
  const target = { kind: "column", schema: "dbo", name: "Orders", column: "Id" } as never;
  it("records the error on the view when the search fails", async () => {
    m.rpc.mockRejectedValueOnce(new Error("timeout"));
    await A.runFindUsages("t", "p", "db", target);
    expect(useUsages.getState().byTab.t).toEqual({ target, running: false, error: "timeout" });
    expect(m.setBottomTab).toHaveBeenCalledWith("t", "usages");
  });
});

describe("renameAtCursor", () => {
  function setup(text: string, offset: number, tabOver: Record<string, unknown> = {}) {
    const mod = model(text);
    useTabs.setState({ tabs: { t: tab("t", tabOver) }, order: ["t"] } as never);
    m.models.set("t", mod);
    m.editors.set("t", editorAt(mod, offset));
  }
  const dialog = () => (useUi.getState() as any).dialog ?? (useUi.getState() as any).dialogs?.at(-1);

  it("opens the local rename for a variable", async () => {
    setup("declare @x int; select @x", 22);
    m.rpc.mockResolvedValueOnce({ local: [{ offset: 8, length: 2 }, { offset: 23, length: 2 }], target: null });
    await A.renameAtCursor("t");
    expect(dialog()).toMatchObject({ kind: "rename", local: { tabId: "t", original: "@x", dialect: "tsql" } });
  });

  it("opens the previewed rename for a schema object (PostgreSQL too)", async () => {
    setup("select * from public.orders", 22, { profileId: "pg" });
    const target = { kind: "table", schema: "public", name: "orders", column: null };
    m.rpc.mockResolvedValueOnce({ local: null, target });
    await A.renameAtCursor("t");
    expect(dialog()).toMatchObject({ kind: "rename", profileId: "pg", database: "db", target, fromTabId: "t" });
  });

  it("says so when there is nothing to rename, and reports errors", async () => {
    setup("select 1", 3);
    m.rpc.mockResolvedValueOnce({ local: null, target: null });
    await A.renameAtCursor("t");
    expect(m.toast).toHaveBeenCalledWith("info", expect.stringContaining("No database object"));
    m.rpc.mockRejectedValueOnce(new Error("bad"));
    await A.renameAtCursor("t");
    expect(m.toast).toHaveBeenCalledWith("error", "bad");
    m.models.clear();
    await A.renameAtCursor("t");
    expect(m.rpc).toHaveBeenCalledTimes(2);
  });
});

describe("findUsagesOf / renameOf", () => {
  beforeEach(() => m.rpc.mockResolvedValue({ target: {}, hits: [], unchecked: [], serverUpdated: [], objectsScanned: 0, elapsedMs: 0 }));

  it("reuses the active tab when it is on the same database, else opens a new one", async () => {
    useTabs.setState({ tabs: { a: tab("a") }, order: ["a"], activeId: "a" } as never);
    A.findUsagesOf("p", "db", { schema: "dbo", name: "usp_x", kind: "proc" });
    await vi.waitFor(() => expect(m.rpc).toHaveBeenCalled());
    expect(m.rpc.mock.calls[0][1].target).toEqual({ kind: "procedure", schema: "dbo", name: "usp_x", column: null });
    expect(m.newQueryTab).not.toHaveBeenCalled();
    m.newQueryTab.mockReturnValue("new1");
    A.findUsagesOf("p", "other", { schema: "dbo", name: "Orders", kind: "table", column: "Id" });
    expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "p", database: "other" });
    await vi.waitFor(() => expect(m.rpc).toHaveBeenCalledTimes(2));
    expect(m.rpc.mock.calls[1][1].target).toEqual({ kind: "column", schema: "dbo", name: "Orders", column: "Id" });
  });

  it("renameOf opens the rename dialog for objects and columns", () => {
    A.renameOf("p", "db", { schema: "public", name: "v", kind: "view" });
    const d = (useUi.getState() as any).dialog ?? (useUi.getState() as any).dialogs?.at(-1);
    expect(d).toMatchObject({ kind: "rename", profileId: "p", database: "db", target: { kind: "view", name: "v", column: null } });
    A.renameOf("p", "db", { schema: "dbo", name: "Orders", kind: "table", column: "Id" });
    const d2 = (useUi.getState() as any).dialog ?? (useUi.getState() as any).dialogs?.at(-1);
    expect(d2.target).toEqual({ kind: "column", schema: "dbo", name: "Orders", column: "Id" });
  });
});

describe("revealHit", () => {
  const hit = (over: Record<string, unknown>) => ({ id: "h", line: 1, column: 1, offset: 7, length: 3, snippet: "", confidence: "exact", definition: false, ...over }) as never;

  it("selects the hit in an open tab", async () => {
    const mod = model("select abc");
    const ed = editorAt(mod, 0);
    m.models.set("t", mod); m.editors.set("t", ed);
    await A.revealHit(hit({ tabId: "t" }));
    expect(m.activateTab).toHaveBeenCalledWith("t");
    expect(ed.setSelection).toHaveBeenCalledWith({ startLineNumber: 1, startColumn: 8, endLineNumber: 1, endColumn: 11 });
    expect(ed.revealLineInCenter).toHaveBeenCalledWith(1);
    expect(ed.focus).toHaveBeenCalled();
  });

  it("retries until the editor is attached, then gives up quietly", async () => {
    vi.useFakeTimers();
    const mod = model("select abc");
    m.models.set("t", mod);
    await A.revealHit(hit({ tabId: "t" }));
    const ed = editorAt(mod, 0);
    m.editors.set("t", ed);
    await vi.advanceTimersByTimeAsync(60);
    expect(ed.setSelection).toHaveBeenCalledTimes(1);
    m.editors.clear();
    await A.revealHit(hit({ tabId: "t" }));
    await vi.advanceTimersByTimeAsync(2000);
    expect(ed.setSelection).toHaveBeenCalledTimes(1);
  });

  it("opens an object's definition read-only, through the preview tab (D25)", async () => {
    m.rpc.mockResolvedValueOnce({ sql: "create proc p as select 1" });
    m.openReadOnlyDefinition.mockResolvedValue("n1");
    await A.revealHit(hit({ object: { schema: "dbo", name: "p", kind: "proc", objectId: 1 } }), "p", "db");
    expect(m.rpc).toHaveBeenCalledWith("meta.definition", { sessionId: "sess", object: { schema: "dbo", name: "p", kind: "proc", objectId: 1, profileId: "p", database: "db" } });
    expect(m.openReadOnlyDefinition).toHaveBeenCalledWith("p", "db", { schema: "dbo", name: "p", kind: "proc", objectId: 1 }, "create proc p as select 1");
    expect(m.activateTab).toHaveBeenCalledWith("n1");
  });

  it("ignores a hit with no target, and toasts definition failures", async () => {
    await A.revealHit(hit({}));
    await A.revealHit(hit({ object: { schema: "dbo", name: "p", kind: "proc" } }));
    expect(m.rpc).not.toHaveBeenCalled();
    m.rpc.mockRejectedValueOnce(new Error("denied"));
    await A.revealHit(hit({ object: { schema: "dbo", name: "p", kind: "proc" } }), "p", "db");
    expect(m.toast).toHaveBeenCalledWith("error", "denied");
  });
});

describe("applyTabEdits", () => {
  it("applies each tab's edits as one undoable step and skips closed tabs", () => {
    const mod = model("select a\nfrom a");
    m.models.set("t", mod);
    A.applyTabEdits([
      { tabId: "gone", edits: [{ offset: 0, length: 1, text: "x" }] },
      { tabId: "t", edits: [{ offset: 7, length: 1, text: "b" }, { offset: 14, length: 1, text: "b" }] },
    ]);
    expect(mod.stack).toEqual(["stack", "edit", "stack"]);
    expect(mod.ops).toEqual([
      { range: { startLineNumber: 1, startColumn: 8, endLineNumber: 1, endColumn: 9 }, text: "b" },
      { range: { startLineNumber: 2, startColumn: 6, endLineNumber: 2, endColumn: 7 }, text: "b" },
    ]);
  });
});

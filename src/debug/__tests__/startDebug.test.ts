import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SnapObject } from "../../ipc/contract";
import { useDiag } from "../../store/diag";
import { useTabs } from "../../store/tabs";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), ask: vi.fn(), toast: vi.fn(), newQueryTab: vi.fn(), ensureSession: vi.fn(), editors: new Map<string, any>(), models: new Map<string, any>(),
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../store/ui", () => ({ ask: m.ask, toast: m.toast }));
vi.mock("../../app/actions", () => ({ ensureSession: m.ensureSession, newQueryTab: m.newQueryTab }));
vi.mock("../../editor/editors", () => ({ editorForTab: (id: string) => m.editors.get(id), getModel: (id: string) => m.models.get(id) }));
vi.mock("../../editor/monaco", () => ({ monaco: { editor: { MouseTargetType: { GUTTER_GLYPH_MARGIN: 2, GUTTER_LINE_NUMBERS: 3, CONTENT_TEXT: 6 } } } }));

const { startDebug } = await import("../startDebug");

const fn = (o: Partial<SnapObject> = {}): SnapObject => ({
  id: 777, schema: "public", name: "order_total", kind: "func", columns: null, returnType: "numeric",
  params: [{ name: "p_order_id", type: "bigint", ordinal: 1, output: false, hasDefault: false }, { name: "", type: "text", ordinal: 2, output: false, hasDefault: false }],
  ...o,
} as never);

/** What the debug-call dialog was asked to show. */
const dialogSql = () => m.ask.mock.calls[0][0](() => {}) as { kind: string; title: string; sql: string };

function editorWith(model: unknown) {
  const handlers: Array<(e: any) => void> = [];
  const sub = { dispose: vi.fn() };
  const ed = { getModel: () => model, onMouseDown: vi.fn((h: (e: any) => void) => { handlers.push(h); return sub; }) };
  return { ed, handlers, sub };
}

beforeEach(() => {
  vi.clearAllMocks();
  m.rpc.mockReset();
  m.editors.clear(); m.models.clear();
  m.newQueryTab.mockReturnValue("tab1");
  m.ensureSession.mockResolvedValue("sess");
  useDiag.setState({ debugs: {}, traces: {} });
  useTabs.setState({ tabs: { tab1: { id: "tab1", title: "d", txState: "none", busy: false } as never }, order: ["tab1"] } as never);
});

describe("call template", () => {
  it("a function is called with SELECT and named NULL arguments carrying their types", async () => {
    m.ask.mockResolvedValue(null);
    await startDebug("p", "shop", fn());
    expect(dialogSql()).toMatchObject({ kind: "debugCall", title: "Debug public.order_total", sql: "SELECT * FROM public.order_total(p_order_id => NULL /* bigint */, NULL /* text */);" });
  });
  it("a procedure is called with CALL; mixed-case and odd identifiers are quoted", async () => {
    m.ask.mockResolvedValue(null);
    await startDebug("p", "shop", fn({ kind: "proc", schema: "Sales", name: 'Do "it"', params: [] }));
    expect(dialogSql().sql).toBe('CALL "Sales"."Do ""it"""();');
  });
  it("output-only parameters without a type are not passed", async () => {
    m.ask.mockResolvedValue(null);
    await startDebug("p", "shop", fn({ params: [{ name: "o", type: "", ordinal: 1, output: true, hasDefault: false }, { name: "i", type: "int", ordinal: 2, output: true, hasDefault: false }] as never }));
    expect(dialogSql().sql).toBe("SELECT * FROM public.order_total(i => NULL /* int */);");
    m.ask.mockClear();
    await startDebug("p", "shop", fn({ params: null }));
    expect(dialogSql().sql).toBe("SELECT * FROM public.order_total();");
  });
});

describe("startDebug", () => {
  it("does nothing when the dialog is cancelled", async () => {
    m.ask.mockResolvedValue(null);
    await startDebug("p", "shop", fn());
    expect(m.newQueryTab).not.toHaveBeenCalled();
    expect(m.rpc).not.toHaveBeenCalled();
  });

  it("opens a read-only tab, attaches, loads the function source and registers the session", async () => {
    m.ask.mockResolvedValue("SELECT * FROM public.order_total(1)");
    const model = { setValue: vi.fn() };
    m.models.set("tab1", model);
    m.rpc.mockImplementation(async (method: string) => (method === "debug.attach" ? { debugId: "dbg1" } : method === "debug.source" ? { source: "CREATE FUNCTION ..." } : undefined));
    await startDebug("p", "shop", fn());
    expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "p", database: "shop", title: "🐞 order_total", text: "-- waiting for the target to reach the function…", readOnly: true });
    expect(m.rpc).toHaveBeenCalledWith("debug.attach", {
      sessionId: "sess", functionOid: 777, callSql: "SELECT * FROM public.order_total(1)", breakpoints: null, confirmedProd: false, confirmedDestructive: false,
    });
    expect(useDiag.getState().debugs.dbg1).toMatchObject({ debugId: "dbg1", tabId: "tab1", functionOid: 777, state: "attaching", breakpoints: [] });
    expect(useTabs.getState().tabs.tab1).toMatchObject({ debugId: "dbg1", bottomTab: "runtime", runtimeTool: "debug", bottomOpen: true });
    expect(model.setValue).toHaveBeenCalledWith("CREATE FUNCTION ...");
  });

  // §13 rule 7: the sidecar guards the call like a query; the webview asks and attaches again with the confirmation.
  it("a blocked attach goes through the guard dialog and is sent again with each confirmation", async () => {
    const block = (reason: string) => ({ reason, profileName: "Billing", server: "prod", database: "shop", environment: "prod", findings: [] });
    const dialogs: string[] = [];
    m.ask.mockImplementation(async (make: (r: unknown) => { kind: string }) => {
      const d = make(() => {});
      dialogs.push(d.kind);
      return d.kind === "debugCall" ? "DELETE FROM orders; SELECT public.order_total(1)" : true;
    });
    m.models.set("tab1", { setValue: vi.fn() });
    const attach: any[] = [];
    m.rpc.mockImplementation(async (method: string, p: any) => {
      if (method !== "debug.attach") return { source: "s" };
      attach.push({ ...p });
      return !p.confirmedProd ? { blocked: block("prodFirstRun") } : !p.confirmedDestructive ? { blocked: block("destructive") } : { debugId: "dbg1" };
    });
    await startDebug("p", "shop", fn());
    expect(dialogs).toEqual(["debugCall", "guard", "guard"]);
    expect(attach.map((p) => [p.confirmedProd, p.confirmedDestructive])).toEqual([[false, false], [true, false], [true, true]]);
    expect(useDiag.getState().debugs.dbg1).toBeTruthy();
  });

  it("a declined guard attaches nothing and says the call did not run", async () => {
    const model = { setValue: vi.fn() };
    m.models.set("tab1", model);
    m.ask.mockImplementation(async (make: (r: unknown) => { kind: string }) => (make(() => {}).kind === "debugCall" ? "SELECT 1" : false));
    m.rpc.mockResolvedValue({ blocked: { reason: "prodFirstRun", profileName: "B", server: "s", findings: [] } });
    await startDebug("p", "shop", fn());
    expect(m.rpc).toHaveBeenCalledTimes(1);
    expect(Object.keys(useDiag.getState().debugs)).toEqual([]);
    expect(model.setValue).toHaveBeenCalledWith(expect.stringContaining("was not run"));
  });

  it("reports attach failures (pldebugger missing) and leaves no session behind", async () => {
    m.ask.mockResolvedValue("SELECT 1");
    m.rpc.mockRejectedValue(new Error("pldbgapi not installed"));
    await startDebug("p", "shop", fn());
    expect(m.toast).toHaveBeenCalledWith("error", "Debugger: pldbgapi not installed");
    expect(Object.keys(useDiag.getState().debugs)).toEqual([]);
  });

  describe("breakpoint gutter", () => {
    async function attached() {
      m.ask.mockResolvedValue("SELECT 1");
      const model = { setValue: vi.fn() };
      const { ed, handlers, sub } = editorWith(model);
      m.models.set("tab1", model); m.editors.set("tab1", ed);
      m.rpc.mockImplementation(async (method: string) => (method === "debug.attach" ? { debugId: "dbg1" } : method === "debug.source" ? { source: "s" } : undefined));
      await startDebug("p", "shop", fn());
      m.rpc.mockClear();
      m.rpc.mockResolvedValue(undefined);
      return { handlers, sub, model, ed };
    }
    const click = (line: number | undefined, type = 2) => ({ target: { type, position: line ? { lineNumber: line } : null } });

    it("toggles a breakpoint on a gutter click: set, then drop", async () => {
      const { handlers } = await attached();
      handlers[0](click(7));
      expect(useDiag.getState().debugs.dbg1.breakpoints).toEqual([7]);
      expect(m.rpc).toHaveBeenLastCalledWith("debug.setBreakpoint", { debugId: "dbg1", functionOid: 777, line: 7 });
      handlers[0](click(9, 3));
      expect(useDiag.getState().debugs.dbg1.breakpoints).toEqual([7, 9]);
      handlers[0](click(7));
      expect(useDiag.getState().debugs.dbg1.breakpoints).toEqual([9]);
      expect(m.rpc).toHaveBeenLastCalledWith("debug.dropBreakpoint", { debugId: "dbg1", functionOid: 777, line: 7 });
    });

    it("ignores clicks in the text area, without a line, after the session finished, or when another model is shown", async () => {
      const { handlers, ed } = await attached();
      handlers[0](click(7, 6));
      handlers[0](click(undefined));
      useDiag.getState().patchDebug("dbg1", { state: "finished" });
      handlers[0](click(7));
      useDiag.getState().patchDebug("dbg1", { state: "paused" });
      (ed as any).getModel = () => ({ other: true });
      handlers[0](click(7));
      expect(m.rpc).not.toHaveBeenCalled();
      expect(useDiag.getState().debugs.dbg1.breakpoints).toEqual([]);
    });

    it("reports a failing breakpoint call, and stops listening when the session is cleared from the tab", async () => {
      const { handlers, sub } = await attached();
      m.rpc.mockRejectedValueOnce(new Error("bad line"));
      handlers[0](click(4));
      await vi.waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "bad line"));
      useTabs.getState().update("tab1", { debugId: undefined });
      expect(sub.dispose).toHaveBeenCalled();
    });

    it("does not wire the gutter when the editor is not mounted", async () => {
      m.ask.mockResolvedValue("SELECT 1");
      m.models.set("tab1", { setValue: vi.fn() });
      m.rpc.mockImplementation(async (method: string) => (method === "debug.attach" ? { debugId: "dbg1" } : { source: "s" }));
      await startDebug("p", "shop", fn());
      expect(m.toast).not.toHaveBeenCalled();
    });
  });
});

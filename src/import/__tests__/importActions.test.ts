// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useConnections } from "../../store/connections";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), invoke: vi.fn(), ask: vi.fn(), tauri: { v: false }, handlers: [] as Array<(e: any) => void>, status: [] as Array<(s: any) => void>,
  discard: vi.fn(async (_id: string) => {}),
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, shell: { resultsDiscard: m.discard }, RpcError: class extends Error {},
  onNotify: (_n: string, h: (e: any) => void) => { m.handlers.push(h); return () => { m.handlers = m.handlers.filter((x) => x !== h); }; },
  onSidecarStatus: (h: (s: any) => void) => { m.status.push(h); return () => { m.status = m.status.filter((x) => x !== h); }; },
}));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return m.tauri.v; }, transport: () => ({ invoke: m.invoke }) }));
vi.mock("../../store/ui", () => ({ ask: m.ask }));

const A = await import("../importActions");

beforeEach(() => {
  vi.clearAllMocks();
  m.tauri.v = false;
  m.handlers = [];
  m.status = [];
  m.rpc.mockReset();
  useConnections.setState({ sessionFor: vi.fn().mockResolvedValue("explorer") } as never);
});
afterEach(() => vi.restoreAllMocks());

function stubInput(file: File | null, event: "change" | "cancel") {
  const input: any = { type: "", accept: "", files: file ? [file] : [], click: vi.fn(() => queueMicrotask(() => (event === "change" ? input.onchange() : input.oncancel()))) };
  const orig = document.createElement.bind(document);
  vi.spyOn(document, "createElement").mockImplementation((tag: string) => (tag === "input" ? input : orig(tag)));
  return input;
}
const file = (bytes: Uint8Array, name: string) => { const f = new File([bytes as BlobPart], name); (f as any).arrayBuffer = async () => bytes.buffer; return f; };

describe("pickImportFile", () => {
  it("uses the shell's native dialog in the app", async () => {
    m.tauri.v = true;
    m.invoke.mockResolvedValue({ token: "t", name: "a.csv", size: 1 });
    expect(await A.pickImportFile()).toEqual({ token: "t", name: "a.csv", size: 1 });
    expect(m.invoke).toHaveBeenCalledWith("import_pick_file");
  });
  it("uploads the chosen file to the dev bridge in the browser, only for supported types", async () => {
    const input = stubInput(file(new Uint8Array([97, 44, 98]), "a.csv"), "change");
    m.invoke.mockResolvedValue({ token: "tok", name: "a.csv", size: 3 });
    expect(await A.pickImportFile()).toEqual({ token: "tok", name: "a.csv", size: 3 });
    expect(input.accept).toBe(".csv,.tsv,.txt,.json,.ndjson,.jsonl,.xlsx");
    expect(m.invoke).toHaveBeenCalledWith("import_stage_file", { name: "a.csv", data: btoa("a,b") });
  });
  it("encodes files larger than one chunk without overflowing the call stack", async () => {
    const big = new Uint8Array(0x8000 * 2 + 5).fill(65);
    stubInput(file(big, "big.csv"), "change");
    m.invoke.mockResolvedValue({ token: "t", name: "big.csv", size: big.length });
    await A.pickImportFile();
    expect(atob(m.invoke.mock.calls[0][1].data)).toHaveLength(big.length);
  });
  it("returns null when cancelled or nothing was selected", async () => {
    stubInput(null, "cancel");
    expect(await A.pickImportFile()).toBeNull();
    stubInput(null, "change");
    expect(await A.pickImportFile()).toBeNull();
    expect(m.invoke).not.toHaveBeenCalled();
  });
});

describe("import sessions", () => {
  it("opens a session of the wizard's own after connecting the profile", async () => {
    m.rpc.mockResolvedValue({ sessionId: "imp1" });
    expect(await A.openImportSession("p", "db")).toBe("imp1");
    expect(useConnections.getState().sessionFor).toHaveBeenCalledWith("p");
    expect(m.rpc).toHaveBeenCalledWith("session.open", { profileId: "p", database: "db", secret: null });
  });
  it("closes the session forcibly, ignoring errors and missing ids", async () => {
    m.rpc.mockRejectedValue(new Error("gone"));
    await A.closeImportSession("imp1");
    expect(m.rpc).toHaveBeenCalledWith("session.close", { sessionId: "imp1", force: true });
    await A.closeImportSession(undefined);
    expect(m.rpc).toHaveBeenCalledTimes(1);
  });
});

describe("executeGuarded", () => {
  const finish = (state: string, error?: string) => queueMicrotask(() => queueMicrotask(() => setTimeout(() => {
    m.handlers.forEach((h) => h({ executionId: "other", state: "done" }));
    m.handlers.forEach((h) => h({ executionId: "e1", state, error: error ? { message: error } : null }));
  }, 0)));

  it("runs the statement and returns null on success, closing the execution", async () => {
    m.rpc.mockImplementation(async (method: string) => (method === "query.execute" ? { executionId: "e1" } : undefined));
    const p = A.executeGuarded("s", "CREATE TABLE t(a int)");
    finish("done");
    expect(await p).toBeNull();
    const [, params] = m.rpc.mock.calls[0];
    expect(params).toMatchObject({ sessionId: "s", sql: "CREATE TABLE t(a int)", options: { confirmedProd: false, confirmedDestructive: false, maxRows: 0 } });
    expect(m.rpc).toHaveBeenCalledWith("query.close", { executionId: "e1" });
  });

  it("does not miss a done event that arrives before query.execute's reply (a fast statement on an open session)", async () => {
    m.rpc.mockImplementation(async (method: string) => {
      if (method !== "query.execute") return undefined;
      m.handlers.forEach((h) => h({ executionId: "e1", state: "done", error: null }));
      return { executionId: "e1" };
    });
    expect(await A.executeGuarded("s", "CREATE TABLE t(a int)")).toBeNull();
    expect(m.handlers).toHaveLength(0);
  });

  it("returns the error message, or a default one, when the statement fails", async () => {
    m.rpc.mockImplementation(async () => ({ executionId: "e1" }));
    let p = A.executeGuarded("s", "x");
    finish("error", "syntax error");
    expect(await p).toBe("syntax error");
    p = A.executeGuarded("s", "x");
    finish("error");
    expect(await p).toBe("The statement failed.");
  });

  it("asks before a Prod first run, then reruns with that confirmation", async () => {
    m.ask.mockResolvedValue(true);
    m.rpc.mockResolvedValueOnce({ blocked: { reason: "prodFirstRun" } }).mockResolvedValueOnce({ executionId: "e1" }).mockResolvedValue(undefined);
    const p = A.executeGuarded("s", "x");
    await vi.waitFor(() => expect(m.rpc).toHaveBeenCalledTimes(2));
    finish("done");
    expect(await p).toBeNull();
    expect(m.rpc.mock.calls[1][1].options).toMatchObject({ confirmedProd: true, confirmedDestructive: false });
  });

  it("asks before a destructive statement and confirms that instead", async () => {
    m.ask.mockResolvedValue(true);
    m.rpc.mockResolvedValueOnce({ blocked: { reason: "destructive" } }).mockResolvedValueOnce({ executionId: "e1" }).mockResolvedValue(undefined);
    const p = A.executeGuarded("s", "DROP TABLE t");
    await vi.waitFor(() => expect(m.rpc).toHaveBeenCalledTimes(2));
    finish("done");
    await p;
    expect(m.rpc.mock.calls[1][1].options).toMatchObject({ confirmedProd: false, confirmedDestructive: true });
    // the dialog was built as a guard dialog for that block
    const dialog = m.ask.mock.calls[0][0](() => {});
    expect(dialog).toMatchObject({ kind: "guard", block: { reason: "destructive" } });
  });

  it("drops the statement's frames in the shell: nothing reads them, and a full queue would stall every tab (L14)", async () => {
    m.rpc.mockImplementation(async (method: string) => (method === "query.execute" ? { executionId: "e1" } : undefined));
    const p = A.executeGuarded("s", "CREATE TABLE t(a int)");
    finish("done");
    expect(await p).toBeNull();
    expect(m.discard).toHaveBeenCalledWith("e1");
    expect([m.handlers.length, m.status.length]).toEqual([0, 0]);
  });

  it("ends the wait when the engine stops while the statement runs: its outcome is unknown", async () => {
    m.rpc.mockImplementation(async (method: string) => (method === "query.execute" ? { executionId: "e1" } : undefined));
    const p = A.executeGuarded("s", "ALTER TABLE t ADD c int");
    await vi.waitFor(() => expect(m.status).toHaveLength(1));
    m.status.forEach((h) => h({ state: "running" })); // not a stop
    m.status.forEach((h) => h({ state: "dead", detail: "exited" }));
    expect(await p).toBe(A.DDL_ENGINE_STOPPED);
    expect(A.DDL_ENGINE_STOPPED).toMatch(/whether it took effect is unknown/);
    expect([m.handlers.length, m.status.length]).toEqual([0, 0]);
  });

  it("cancels a statement that has not finished within the limit, and says so", async () => {
    vi.useFakeTimers();
    try {
      m.rpc.mockImplementation(async (method: string) => (method === "query.execute" ? { executionId: "e1" } : undefined));
      const p = A.executeGuarded("s", "ALTER TABLE big ALTER COLUMN c nvarchar(400)");
      await vi.waitFor(() => expect(m.status).toHaveLength(1));
      await vi.advanceTimersByTimeAsync(A.DDL_WAIT_MS);
      expect(await p).toBe(A.DDL_TIMED_OUT);
      expect(m.rpc).toHaveBeenCalledWith("query.cancel", { executionId: "e1" });
      expect(m.rpc).toHaveBeenCalledWith("query.close", { executionId: "e1" });
      expect([m.handlers.length, m.status.length]).toEqual([0, 0]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("reports Cancelled when the user declines, or when the guards keep blocking", async () => {
    m.ask.mockResolvedValue(false);
    m.rpc.mockResolvedValue({ blocked: { reason: "destructive" } });
    expect(await A.executeGuarded("s", "x")).toBe("Cancelled.");
    expect(m.rpc).toHaveBeenCalledTimes(1);
    m.rpc.mockClear();
    m.ask.mockResolvedValue(true);
    expect(await A.executeGuarded("s", "x")).toBe("Cancelled.");
    expect(m.rpc).toHaveBeenCalledTimes(3);
  });
});

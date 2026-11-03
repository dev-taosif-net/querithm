// Phase D webview leftovers in the guarded execution path: a refused statement shows its reason in Messages, MySQL says
// why it did not prompt for `@name`, an in-memory database lost in an engine restart (or with its session) is reported
// once per tab and never passed off as the old one, and a file grant refusal marks the server as needing a file.
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  rpc: vi.fn(),
  shell: { secretEndConnection: vi.fn(async () => {}), resultsDiscard: vi.fn(async () => {}), secretExists: vi.fn(async () => true) },
  stream: vi.fn(),
  models: new Map<string, any>(),
}));
vi.mock("../../ipc/client", async (orig) => ({ ...(await orig<typeof import("../../ipc/client")>()), rpc: h.rpc, shell: h.shell, onNotify: () => () => {} }));
vi.mock("../../ipc/frames", () => ({
  streamExecution: (...a: any[]) => h.stream(...a), stopStream: () => false, isStreaming: () => false, stopAllStreams: () => [],
  ResultsDroppedError: class ResultsDroppedError extends Error {},
}));
vi.mock("../../editor/monaco", () => ({ monaco: { editor: { setModelMarkers: vi.fn() }, MarkerSeverity: { Error: 8 } } }));
vi.mock("../../editor/completion", () => ({
  undeclaredParameters: () => [],
  // The real token-based reader has its own tests (sqlText.userVariables.test.ts).
  unsetUserVariables: (sql: string, d: string) => (d === "mySql" && sql.includes("@v") ? ["@v"] : []),
  statementBoundsAt: () => ({ start: 0, end: 8 }),
  statementTitle: () => undefined,
}));
vi.mock("../../results/gridEdit", () => ({ resolvePendingEdits: async () => true }));
vi.mock("../../editor/editors", () => ({
  languageFor: () => "sqlite",
  createModel: (id: string, text: string) => {
    const m = { text, getValue: () => m.text, setValue: (v: string) => void (m.text = v), getLanguageId: () => "sqlite" };
    h.models.set(id, m);
    return m;
  },
  getModel: (id: string) => h.models.get(id),
  setModelLanguage: () => {},
  disposeModel: (id: string) => h.models.delete(id),
  editorForTab: () => undefined,
  executableText: (id: string) => ({ sql: h.models.get(id)?.text ?? "", lineOffset: 1, isSelection: false }),
}));

import { RpcError } from "../../ipc/client";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";
import { useExecutions } from "../../store/executions";
import { executionData, ExecutionData } from "../../results/model";
import { resetAnnouncer, useAnnounce } from "../../store/announce";
import { notebookHooks } from "../../notebook/hooks";
import * as A from "../actions";

const mem: any = { id: "mem", name: "Scratch", engine: "sqlite", location: "memory", auth: "none", environment: "dev", databaseDefault: "main" };
const file: any = { id: "file", name: "App", engine: "sqlite", location: "file", file: { grantId: "g1", displayName: "app.db" }, auth: "none", environment: "dev", databaseDefault: "main" };
const my: any = { id: "my", name: "Shop", engine: "mySql", auth: "password", environment: "dev", databaseDefault: "shop" };
const ms: any = { id: "ms", name: "Sales", engine: "mssql", auth: "windows", environment: "dev", databaseDefault: "Sales" };

const tick = () => new Promise((r) => setTimeout(r, 0));
const tabOf = (id: string) => useTabs.getState().tabs[id];
const toasts = () => useUi.getState().toasts.map((t) => t.text);
const viewOf = (id: string) => useExecutions.getState().views[tabOf(id).activeExecutionId!];
const calls = (method: string) => h.rpc.mock.calls.filter((c) => c[0] === method);

function mkTab(profileId: string, text = "select 1", sessionId: string | null = "s1") {
  const id = A.newQueryTab({ profileId, text });
  if (sessionId) useTabs.getState().update(id, { sessionId });
  return id;
}

beforeEach(() => {
  vi.clearAllMocks();
  h.models.clear();
  executionData.clear();
  h.rpc.mockReset();
  h.rpc.mockResolvedValue(undefined);
  h.stream.mockReset();
  h.stream.mockReturnValue({ done: Promise.resolve(), cancel() {} });
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useUi.setState({ dialogs: [], palette: null, toasts: [], banner: null });
  useProfiles.setState({ profiles: [mem, file, my, ms], groups: [] });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useExecutions.setState({ views: {} });
  resetAnnouncer();
});

describe("a statement the sidecar refuses", () => {
  it.each([
    ["refused", "ATTACH is not allowed: Querithm opens only the file you picked."],
    ["refused", "LOAD DATA LOCAL is not allowed: the sidecar reads no client files."],
    ["memoryLost", "The in-memory database is gone: its connection closed, and its data went with it."],
  ])("%s: the reason is an error in the tab's Messages pane, not only a toast", async (appCode, message) => {
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "query.execute") throw new RpcError(message, -32001, appCode);
      return undefined;
    });
    const id = mkTab("file", "ATTACH 'x.db' AS x;");
    expect(await A.runQuery(id)).toBeUndefined();
    expect(tabOf(id)).toMatchObject({ bottomTab: "messages", bottomOpen: true, busy: false });
    expect(viewOf(id)).toMatchObject({ state: "failed", notRun: true, sql: "ATTACH 'x.db' AS x;" });
    expect(viewOf(id).messages).toEqual([expect.objectContaining({ severity: "error", message })]);
    expect(toasts()).toContain(message);
    if (appCode === "memoryLost") expect(tabOf(id).memoryLost).toBe(true);
  });

  it("other RPC errors stay a toast (no Messages entry)", async () => {
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "query.execute") throw new RpcError("socket closed", -32000, "internal");
      return undefined;
    });
    const id = mkTab("ms");
    await A.runQuery(id);
    expect(tabOf(id).activeExecutionId).toBeUndefined();
    expect(toasts()).toContain("socket closed");
  });

  it("a caller with its own place for the reason (a notebook cell) gets it instead of the tab's Messages", async () => {
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "query.execute") throw new RpcError("ATTACH is not allowed.", -32001, "refused");
      return undefined;
    });
    const id = mkTab("file");
    const onRefused = vi.fn();
    expect(await A.executeOnTab(id, "ATTACH 'x' AS y", 1, undefined, undefined, onRefused)).toBeUndefined();
    expect(onRefused).toHaveBeenCalledWith("ATTACH is not allowed.");
    expect(tabOf(id).activeExecutionId).toBeUndefined();
  });

  it("the Messages entry is local: disposing it sends nothing to the sidecar or the shell", async () => {
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "query.execute") throw new RpcError("ATTACH is not allowed.", -32001, "refused");
      return undefined;
    });
    const id = mkTab("file");
    await A.runQuery(id);
    const local = tabOf(id).activeExecutionId!;
    expect(local.startsWith("local-")).toBe(true);
    A.disposeExecution(local);
    expect(calls("query.close")).toEqual([]);
    expect(h.shell.resultsDiscard).not.toHaveBeenCalled();
    expect(useExecutions.getState().views[local]).toBeUndefined();
  });
});

describe("MySQL user variables", () => {
  beforeEach(() => h.rpc.mockImplementation(async (m: string) => (m === "query.execute" ? { executionId: "e1" } : undefined)));

  it("a @name the script never sets runs without a prompt, and the run says why in a toast and in Messages", async () => {
    const id = mkTab("my", "SELECT * FROM orders WHERE id = @v;");
    expect(await A.runQuery(id)).toBe("e1");
    expect(useUi.getState().dialogs.some((d) => d.kind === "params")).toBe(false);
    const note = A.userVariableNote("@v");
    expect(note).toBe("MySQL: @v is a user variable here — set it with SET @v = … in the script.");
    expect(toasts()).toContain(note);
    expect(useExecutions.getState().views.e1.messages).toEqual([expect.objectContaining({ severity: "info", message: note, executionId: "e1" })]);
  });

  it("other engines get no such note", async () => {
    const id = mkTab("file", "SELECT @v;");
    await A.runQuery(id);
    expect(useExecutions.getState().views.e1.messages).toEqual([]);
    expect(toasts()).toEqual([]);
  });
});

describe("an in-memory database lost in an engine restart", () => {
  it("each tab that had one is told once, its results and session go, and the banner names it", () => {
    const a = mkTab("mem", "select 1", "s1");
    const b = mkTab("mem", "select 2", null); // never ran: it had no database to lose
    const f = mkTab("file", "select 3", "s3");
    const s = mkTab("ms", "select 4", "s4");
    useExecutions.getState().put({ id: "e1", tabId: a, sessionId: "s1", state: "done", startedAt: 0, resultSetCount: 1, truncated: false, messages: [], statistics: [], plans: [], sql: "x", lineOffset: 1 });
    executionData.set("e1", new ExecutionData("e1"));
    useTabs.getState().update(a, { executions: ["e1"], activeExecutionId: "e1" });

    A.onSidecarRestarted();

    expect(useExecutions.getState().views.e1).toBeUndefined();
    expect(executionData.get("e1")).toBeUndefined();
    expect(tabOf(a)).toMatchObject({ memoryLost: true, sessionId: undefined, bottomTab: "messages" });
    expect(viewOf(a).messages).toEqual([expect.objectContaining({ severity: "warning", message: A.MEMORY_LOST_IN_RESTART })]);
    for (const other of [b, f, s]) expect(tabOf(other).memoryLost).toBeUndefined();
    expect(useAnnounce.getState().text).toMatch(/in-memory database lost/);
    const banner = useUi.getState().banner!.text;
    expect(banner).toContain("In-memory databases in");
    expect(banner).toContain(tabOf(a).title);
    expect(banner).not.toContain(tabOf(b).title);
    expect(banner).not.toContain("no work was lost");
    // The file and server tabs had sessions: what lived in them is gone too (L32).
    expect(banner).toContain("The 2 connected tabs get a new session at the next run: temporary tables and session settings from before are gone.");
  });

  it("a second restart before the tab ran again does not tell it twice", () => {
    const a = mkTab("mem", "select 1", "s1");
    A.onSidecarRestarted();
    const shown = tabOf(a).executions.length;
    A.onSidecarRestarted();
    expect(tabOf(a).executions.length).toBe(shown);
    expect(useUi.getState().banner!.text).toBe("The Querithm engine restarted. No tab was connected or running a query, so no work was lost; servers reconnect now.");
  });

  it("the next run opens a new session, clears the mark and says the database is a new, empty one", async () => {
    const a = mkTab("mem", "select * from t", "s1");
    A.onSidecarRestarted();
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") return { sessionId: "s2", database: "main" };
      if (m === "query.execute") return { executionId: "e2" };
      return undefined;
    });
    expect(await A.runQuery(a)).toBe("e2");
    expect(tabOf(a)).toMatchObject({ sessionId: "s2", memoryLost: undefined });
    expect(useExecutions.getState().views.e2.messages).toEqual([expect.objectContaining({ severity: "info", message: A.NEW_MEMORY_DATABASE_NOTE })]);
  });

  it("a notebook on an in-memory database clears its cell outputs through its hook", () => {
    const outputsLost = vi.fn();
    notebookHooks.outputsLost = outputsLost;
    const id = mkTab("mem", "", "s1");
    useTabs.getState().update(id, { notebook: true });
    A.onSidecarRestarted();
    expect(outputsLost).toHaveBeenCalledWith(id, A.MEMORY_LOST_IN_RESTART);
    expect(tabOf(id).memoryLost).toBe(true);
    notebookHooks.outputsLost = undefined;
  });
});

describe("an in-memory session that vanished", () => {
  it("is never reopened behind the user's back: the tab is marked and nothing runs", async () => {
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "query.execute") throw new RpcError("Session not found", -32004, "notFound");
      if (m === "session.open") return { sessionId: "s2" };
      return undefined;
    });
    const id = mkTab("mem", "select * from t", "s1");
    expect(await A.runQuery(id)).toBeUndefined();
    expect(calls("session.open")).toEqual([]);
    expect(calls("query.execute")).toHaveLength(1);
    expect(tabOf(id)).toMatchObject({ memoryLost: true, sessionId: undefined });
    expect(viewOf(id).messages[0]).toMatchObject({ message: A.MEMORY_GONE });
  });

  it("a server or file session that vanished still reopens once (unchanged)", async () => {
    let first = true;
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "query.execute") {
        if (first) {
          first = false;
          throw new RpcError("Session not found", -32004, "notFound");
        }
        return { executionId: "e9" };
      }
      if (m === "session.open") return { sessionId: "s2", database: "main" };
      if (m === "session.state") return { database: "main" };
      if (m === "meta.databases") return [];
      return undefined;
    });
    const id = mkTab("file", "select 1", "s1");
    expect(await A.runQuery(id)).toBe("e9");
    expect(tabOf(id).memoryLost).toBeUndefined();
  });
});

describe("a database file that must be picked again", () => {
  it("ensureSession marks the server as needing a file (the Explorer and the status bar offer Re-pick file…)", async () => {
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") throw new RpcError("The database file is gone.", -32001, "fileMissing");
      return undefined;
    });
    useConnections.setState({ servers: { file: { profileId: "file", status: "connected", sessionId: "ex1", databases: [{ name: "main" } as never] } }, order: ["file"] });
    const id = mkTab("file", "select 1", null);
    await expect(A.ensureSession(id)).rejects.toThrow("The database file is gone.");
    expect(useConnections.getState().servers.file).toMatchObject({ status: "error", repick: true, sessionId: undefined, databases: [] });
    // The Explorer's own session on the old file is closed, not left behind.
    expect(calls("session.close")).toEqual([["session.close", { sessionId: "ex1", force: true }]]);
    await tick();
  });
});

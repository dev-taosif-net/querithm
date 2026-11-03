import { tableFromArrays } from "apache-arrow";
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => {
  const notify = new Map<string, ((p: any) => void)[]>();
  const models = new Map<string, any>();
  return {
    notify,
    models,
    rpc: vi.fn(),
    shell: { secretEndConnection: vi.fn(async () => {}), resultsDiscard: vi.fn(async () => {}), secretExists: vi.fn(async () => true), restart: vi.fn(async () => {}) },
    editor: undefined as any,
    stream: vi.fn(),
    stopStream: vi.fn((_id: string) => false),
    isStreaming: vi.fn((_id: string) => false),
    stopAll: vi.fn((): string[] => []),
    resolvePending: vi.fn(async (_id: string) => true),
    markers: vi.fn(),
    exec: { sql: "select 1", lineOffset: 1, isSelection: false },
  };
});

vi.mock("../../ipc/client", async (orig) => {
  const real = await orig<typeof import("../../ipc/client")>();
  return {
    ...real,
    rpc: h.rpc,
    shell: h.shell,
    onNotify: (m: string, cb: (p: any) => void) => {
      h.notify.set(m, [...(h.notify.get(m) ?? []), cb]);
      return () => h.notify.set(m, (h.notify.get(m) ?? []).filter((x) => x !== cb));
    },
  };
});
vi.mock("../../ipc/frames", () => ({
  streamExecution: (...a: any[]) => h.stream(...a),
  stopStream: (id: string) => h.stopStream(id),
  isStreaming: (id: string) => h.isStreaming(id),
  stopAllStreams: () => h.stopAll(),
  ResultsDroppedError: class ResultsDroppedError extends Error {},
}));
vi.mock("../../editor/monaco", () => ({ monaco: { editor: { setModelMarkers: (...a: any[]) => h.markers(...a) }, MarkerSeverity: { Error: 8 } } }));
vi.mock("../../editor/completion", () => ({
  undeclaredParameters: (sql: string) => (sql.includes("@p") ? ["@p"] : []),
  unsetUserVariables: () => [],
  statementBoundsAt: () => ({ start: 0, end: 8 }),
  // The real token-based title has its own tests (statementTitle.test.ts); here a stand-in that names UPDATEs.
  statementTitle: (sql: string) => (sql.startsWith("UPDATE ") ? `UPDATE ${sql.split(" ")[1]}` : undefined),
}));
vi.mock("../../results/gridEdit", () => ({ resolvePendingEdits: (id: string) => h.resolvePending(id) }));
vi.mock("../../editor/editors", () => ({
  languageFor: (e?: string) => (e === "pg" ? "pgsql" : "tsql"),
  createModel: (id: string, text: string, lang: string) => {
    const m = {
      text, lang,
      getValue: () => m.text,
      setValue: (v: string) => void (m.text = v),
      getLanguageId: () => m.lang,
      getLineCount: () => 3,
      getLineMaxColumn: () => 20,
      getOffsetAt: () => 0,
      getPositionAt: () => ({ lineNumber: 1 }),
    };
    h.models.set(id, m);
    return m;
  },
  getModel: (id: string) => h.models.get(id),
  setModelLanguage: (id: string, lang: string) => { const m = h.models.get(id); if (m) m.lang = lang; },
  disposeModel: (id: string) => h.models.delete(id),
  editorForTab: () => h.editor,
  executableText: () => h.exec,
}));

import { RpcError } from "../../ipc/client";
import { useTabs } from "../../store/tabs";
import { useToolTabs } from "../../store/toolTabs";
import { useUi } from "../../store/ui";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";
import { useExecutions } from "../../store/executions";
import { executionData, ExecutionData } from "../../results/model";
import { useSettings } from "../../store/settings";
import { useClosedTabs } from "../../store/closedTabs";
import * as A from "../actions";
import { resetAnnouncer, useAnnounce } from "../../store/announce";

const ms: any = { id: "ms1", name: "Sales", engine: "mssql", environment: "dev", auth: "windows", databaseDefault: "master" };
const pg: any = { id: "pg1", name: "Pg", engine: "pg", environment: "dev", auth: "trust", databaseDefault: "postgres" };

/** Answers the next dialog of `kind` by calling its resolve with `value`. */
function answer(kind: string, value: unknown) {
  const d = useUi.getState().dialogs.find((x) => x.kind === kind) as any;
  expect(d, `dialog ${kind} is open`).toBeTruthy();
  d.resolve(value);
}
const tick = () => new Promise((r) => setTimeout(r, 0));
const dialogOf = (kind: string) => useUi.getState().dialogs.find((x) => x.kind === kind) as any;
const toasts = () => useUi.getState().toasts.map((t) => t.text);
const tabOf = (id: string) => useTabs.getState().tabs[id];

function reset() {
  vi.clearAllMocks();
  h.notify.clear();
  executionData.clear();
  h.models.clear();
  h.editor = undefined;
  h.exec = { sql: "select 1", lineOffset: 1, isSelection: false };
  h.resolvePending.mockResolvedValue(true);
  h.rpc.mockReset();
  h.stream.mockReset();
  h.stream.mockReturnValue({ done: Promise.resolve(), cancel() {} });
  A.workspaceHooks.onTabAdded = undefined;
  A.workspaceHooks.onTabRemoved = undefined;
  A.workspaceHooks.activate = undefined;
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useUi.setState({ dialogs: [], palette: null, toasts: [], banner: null });
  useProfiles.setState({ profiles: [ms, pg], groups: [] });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useExecutions.setState({ views: {} });
}

beforeEach(reset);

describe("newQueryTab", () => {
  it("creates a model and tab bound to the requested profile, using the engine language and default database", () => {
    const hook = vi.fn();
    A.workspaceHooks.onTabAdded = hook;
    h.rpc.mockResolvedValue({});
    const id = A.newQueryTab({ profileId: "pg1", text: "select 1" });
    expect(useTabs.getState().tabs[id]).toMatchObject({ profileId: "pg1", database: "postgres" });
    expect(h.models.get(id).lang).toBe("pgsql");
    expect(hook).toHaveBeenCalledWith(id, true, false);
    const id2 = A.newQueryTab({ profileId: "ms1", activate: false, atEnd: true, title: "x.sql" });
    expect(h.models.get(id2).lang).toBe("tsql");
    expect(hook).toHaveBeenLastCalledWith(id2, false, true);
    expect(useTabs.getState().tabs[id2].title).toBe("x.sql");
  });

  it("inherits the active tab's connection and gives sequential untitled names", () => {
    const a = A.newQueryTab({ profileId: "ms1", database: "db1" });
    const b = A.newQueryTab();
    expect(useTabs.getState().tabs[b]).toMatchObject({ profileId: "ms1", database: "db1" });
    expect(useTabs.getState().tabs[a].title).toMatch(/^SQLQuery\d+\.sql$/);
    expect(useTabs.getState().tabs[b].title).not.toBe(useTabs.getState().tabs[a].title);
    expect(A.activeTabState()?.id).toBe(b);
  });

  it.each([["ms1", "pg1"], ["pg1", "ms1"]])(
    "a new query on server %s while the active tab is on %s starts in that server's database in use, else its default",
    (target, other) => {
      const p = useProfiles.getState().byId(target)!;
      // No tab on the target server: the profile default.
      A.newQueryTab({ profileId: other, database: "OtherDb" });
      expect(tabOf(A.newQueryTab({ profileId: target }))).toMatchObject({ profileId: target, database: p.databaseDefault });
      // Tabs on the target server: the database of the most recently used one, even when another server's tab is active.
      const t1 = A.newQueryTab({ profileId: target, database: "Sales" });
      A.newQueryTab({ profileId: target, database: "Hr" });
      useTabs.getState().setActive(t1);
      A.newQueryTab({ profileId: other, database: "OtherDb" });
      expect(tabOf(A.newQueryTab({ profileId: target }))).toMatchObject({ profileId: target, database: "Sales" });
    },
  );

  it("a server with no tabs and no profile default starts in the database its explorer session landed in", () => {
    useProfiles.setState({ profiles: [{ ...ms, databaseDefault: null }, pg] });
    expect(tabOf(A.newQueryTab({ profileId: "ms1" })).database).toBeUndefined();
    useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
    useConnections.setState({ servers: { ms1: { profileId: "ms1", status: "connected", database: "Warehouse", databases: [] } } });
    A.newQueryTab({ profileId: "pg1" });
    expect(tabOf(A.newQueryTab({ profileId: "ms1" }))).toMatchObject({ profileId: "ms1", database: "Warehouse" });
  });

  it("does not inherit from a disconnected server", () => {
    A.newQueryTab({ profileId: "ms1" });
    useConnections.setState({ disconnected: { ms1: true } });
    const id = A.newQueryTab();
    expect(useTabs.getState().tabs[id].profileId).toBeUndefined();
  });
});

describe("closeTab", () => {
  it("returns true for an unknown tab and false when pending edits are cancelled", async () => {
    expect(await A.closeTab("nope")).toBe(true);
    const id = A.newQueryTab();
    h.resolvePending.mockResolvedValue(false);
    expect(await A.closeTab(id)).toBe(false);
    expect(useTabs.getState().tabs[id]).toBeTruthy();
  });

  it("closes the session, executions, trace and debugger of a plain tab", async () => {
    h.rpc.mockResolvedValue(undefined);
    const removed = vi.fn();
    A.workspaceHooks.onTabRemoved = removed;
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", traceId: "tr", debugId: "dbg", executions: ["e1"] });
    expect(await A.closeTab(id)).toBe(true);
    const calls = h.rpc.mock.calls.map((c) => c[0]);
    expect(calls).toEqual(expect.arrayContaining(["trace.stop", "debug.detach", "session.close", "query.close"]));
    expect(h.stopStream).toHaveBeenCalledWith("e1"); // its poll loop stops and the shell drops its frames
    expect(removed).toHaveBeenCalledWith(id);
    // Its model is disposed, which makes the language providers send lang.forget for the docId (M12, providers.test).
    expect(h.models.has(id)).toBe(false);
    expect(useTabs.getState().tabs[id]).toBeUndefined();
    expect(h.models.has(id)).toBe(false);
  });

  it("tolerates failing cleanup calls", async () => {
    h.rpc.mockRejectedValue(new Error("down"));
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", traceId: "tr", debugId: "d", executions: ["e1"] });
    expect(await A.closeTab(id)).toBe(true);
  });

  it("with an open transaction: cancel keeps the tab", async () => {
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", txState: "open" });
    const p = A.closeTab(id);
    await tick();
    answer("txClose", "cancel");
    expect(await p).toBe(false);
    expect(useTabs.getState().tabs[id]).toBeTruthy();
  });

  it.each(["commit", "rollback"] as const)("with an open transaction: %s runs the tx action then closes", async (choice) => {
    h.rpc.mockImplementation(async (m: string) => (m.startsWith("tx.") ? { txState: "none" } : undefined));
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", txState: "open" });
    const p = A.closeTab(id);
    await tick();
    answer("txClose", choice);
    expect(await p).toBe(true);
    expect(h.rpc.mock.calls.map((c) => c[0])).toContain(`tx.${choice}`);
    expect(useTabs.getState().tabs[id]).toBeUndefined();
  });

  it("with a transaction state that cannot be read (unknown): asks like an open one, and cancel keeps the tab (M9)", async () => {
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", txState: "unknown" });
    const p = A.closeTab(id);
    await tick();
    answer("txClose", "cancel");
    expect(await p).toBe(false);
    expect(tabOf(id)).toBeTruthy();
    expect(h.rpc.mock.calls.map((c) => c[0])).not.toContain("session.close");
  });

  it("with an unknown state: a rollback that still leaves it unknown keeps the tab (M9)", async () => {
    h.rpc.mockImplementation(async (m: string) => (m.startsWith("tx.") ? { txState: "unknown" } : undefined));
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", txState: "unknown" });
    const p = A.closeTab(id);
    await tick();
    answer("txClose", "rollback");
    expect(await p).toBe(false);
    expect(tabOf(id)).toBeTruthy();
    expect(h.rpc.mock.calls.map((c) => c[0])).not.toContain("session.close");
  });

  it("settleTab asks the quit wording and leaves the tab in place (H4)", async () => {
    h.rpc.mockImplementation(async (m: string) => (m.startsWith("tx.") ? { txState: "none" } : undefined));
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", txState: "open" });
    const p = A.settleTab(id, { quit: true });
    await tick();
    expect(dialogOf("txClose")).toMatchObject({ quit: true });
    answer("txClose", "rollback");
    expect(await p).toBe(true);
    expect(tabOf(id)).toMatchObject({ txState: "none" });
    expect(h.rpc.mock.calls.map((c) => c[0])).not.toContain("session.close");
  });

  it("with an open transaction: a commit that fails keeps the tab, so the transaction can still be ended", async () => {
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "tx.commit") throw new Error("deadlock victim");
    });
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", txState: "open" });
    const p = A.closeTab(id);
    await tick();
    answer("txClose", "commit");
    expect(await p).toBe(false);
    expect(tabOf(id)).toBeTruthy();
    expect(h.rpc.mock.calls.map((c) => c[0])).not.toContain("session.close");
    expect(toasts().join()).toMatch(/deadlock victim/);
  });
});

describe("closeTab on a running tab (D20)", () => {
  function running(profileId = "ms1") {
    const id = A.newQueryTab({ profileId });
    useTabs.getState().update(id, { sessionId: "s1", busy: true, activeExecutionId: "e1" });
    useExecutions.setState({ views: { e1: { id: "e1", tabId: id, state: "running" } as any } });
    return id;
  }

  it("asks first; Keep tab cancels nothing and keeps the tab", async () => {
    const id = running();
    const p = A.closeTab(id);
    await tick();
    expect(dialogOf("busyClose")).toBeTruthy();
    answer("busyClose", false);
    expect(await p).toBe(false);
    expect(h.rpc).not.toHaveBeenCalledWith("query.cancel", expect.anything());
    expect(tabOf(id)).toBeTruthy();
  });

  it.each(["ms1", "pg1"])("on %s: cancels the query, waits for it to stop, then closes", async (profileId) => {
    h.rpc.mockImplementation(async (m: string) => (m === "session.state" ? { txState: "none" } : m === "query.cancel" ? { cancelled: true } : undefined));
    const id = running(profileId);
    const p = A.closeTab(id);
    await tick();
    answer("busyClose", true);
    await tick();
    expect(h.rpc).toHaveBeenCalledWith("query.cancel", { executionId: "e1" });
    expect(tabOf(id)).toBeTruthy(); // still running: not closed yet
    useTabs.getState().update(id, { busy: false });
    expect(await p).toBe(true);
    expect(tabOf(id)).toBeUndefined();
    expect(h.rpc.mock.calls.map((c) => c[0])).toContain("session.close");
  });

  it("asks about a transaction the cancelled batch left open, even when the store had not heard of it yet", async () => {
    h.rpc.mockImplementation(async (m: string) => (m === "session.state" ? { txState: "open", txStartedAt: 1, spid: 53 } : undefined));
    const id = running();
    const p = A.closeTab(id);
    await tick();
    answer("busyClose", true);
    await tick();
    useTabs.getState().update(id, { busy: false });
    await tick();
    await tick();
    expect(dialogOf("txClose")).toBeTruthy();
    answer("txClose", "cancel");
    expect(await p).toBe(false);
    expect(tabOf(id)).toMatchObject({ txState: "open", spid: 53 });
    expect(h.rpc.mock.calls.map((c) => c[0])).not.toContain("session.close");
  });

  it("keeps the tab and says so when the query does not stop in time", async () => {
    vi.useFakeTimers();
    try {
      h.rpc.mockResolvedValue(undefined);
      const id = running();
      const p = A.closeTab(id);
      await vi.advanceTimersByTimeAsync(0);
      answer("busyClose", true);
      await vi.advanceTimersByTimeAsync(A.CLOSE_CANCEL_WAIT_MS);
      expect(await p).toBe(false);
      expect(tabOf(id)).toBeTruthy();
      expect(toasts().join()).toMatch(/has not stopped yet; the tab stays open/);
      expect(h.rpc.mock.calls.map((c) => c[0])).not.toContain("session.close");
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("closeTab remembers the tab for Ctrl+Shift+T", () => {
  beforeEach(() => useClosedTabs.setState({ list: [] }));

  it.each([
    ["ms1", "SELECT TOP 1 * FROM [dbo].[t]"],
    ["pg1", 'SELECT * FROM "public"."t" LIMIT 1'],
  ])("keeps the text and binding of a tab on %s", async (profileId, sql) => {
    h.rpc.mockResolvedValue(undefined);
    const id = A.newQueryTab({ profileId, database: "app", text: sql, title: "orders.sql", filePath: "/w/orders.sql" });
    expect(await A.closeTab(id)).toBe(true);
    expect(useClosedTabs.getState().list).toEqual([
      expect.objectContaining({ title: "orders.sql", profileId, database: "app", sql, filePath: "/w/orders.sql" }),
    ]);
  });

  it("forgets an empty scratch tab", async () => {
    const id = A.newQueryTab({ text: "" });
    expect(await A.closeTab(id)).toBe(true);
    expect(useClosedTabs.getState().list).toEqual([]);
  });

  it("remembers nothing when the close is cancelled", async () => {
    const id = A.newQueryTab({ profileId: "ms1", text: "select 1" });
    useTabs.getState().update(id, { sessionId: "s1", txState: "open" });
    const p = A.closeTab(id);
    await tick();
    answer("txClose", "cancel");
    expect(await p).toBe(false);
    expect(useClosedTabs.getState().list).toEqual([]);
  });
});

describe("disconnectServer", () => {
  function setup() {
    const a = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(a, { sessionId: "s1" });
    useConnections.setState({ servers: { ms1: { profileId: "ms1", sessionId: "ex", status: "connected", databases: [] } }, order: ["ms1"] });
    return a;
  }
  it("refuses while a query runs", async () => {
    const a = setup();
    useTabs.getState().update(a, { busy: true });
    await A.disconnectServer("ms1");
    expect(toasts().join()).toMatch(/Cancel it/);
    expect(h.rpc).not.toHaveBeenCalled();
  });
  it("closes tab sessions and marks the server disconnected", async () => {
    h.rpc.mockResolvedValue(undefined);
    const a = setup();
    await A.disconnectServer("ms1");
    expect(useTabs.getState().tabs[a].sessionId).toBeUndefined();
    expect(useConnections.getState().disconnected.ms1).toBe(true);
  });
  it("asks before rolling back open transactions (singular and plural)", async () => {
    h.rpc.mockResolvedValue(undefined);
    const a = setup();
    useTabs.getState().update(a, { txState: "open" });
    let p = A.disconnectServer("ms1");
    await tick();
    expect(dialogOf("confirm").body).toMatch(/1 tab has an open transaction/);
    answer("confirm", false);
    await p;
    expect(useConnections.getState().disconnected.ms1).toBeUndefined();
    const b = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(b, { sessionId: "s2", txState: "open" });
    p = A.disconnectServer("ms1");
    await tick();
    expect(dialogOf("confirm").body).toMatch(/2 tabs have open transactions/);
    answer("confirm", true);
    await p;
    expect(useConnections.getState().disconnected.ms1).toBe(true);
  });
  it("asks about a tab whose transaction state is unknown too (M9)", async () => {
    h.rpc.mockResolvedValue(undefined);
    const a = setup();
    useTabs.getState().update(a, { txState: "unknown" });
    const p = A.disconnectServer("ms1");
    await tick();
    expect(dialogOf("confirm").body).toMatch(/1 tab may have an open transaction on this server/);
    answer("confirm", false);
    await p;
    expect(useConnections.getState().disconnected.ms1).toBeUndefined();
  });
});

describe("ensureSession", () => {
  it("throws for a closed tab or missing profile", async () => {
    await expect(A.ensureSession("zz")).rejects.toThrow("tab closed");
    const id = A.newQueryTab();
    await expect(A.ensureSession(id)).rejects.toThrow(/Choose a connection/);
  });
  it("returns the existing session without an rpc", async () => {
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "have" });
    expect(await A.ensureSession(id)).toBe("have");
    expect(h.rpc).not.toHaveBeenCalled();
  });
  it.each([["ms1", "master"], ["pg1", "postgres"]])("opens a session for %s using the keyring (no secret asked)", async (pid, db) => {
    h.rpc.mockImplementation(async (m: string) => (m === "session.open" ? { sessionId: "s9", database: db } : undefined));
    useConnections.setState({ servers: { [pid]: { profileId: pid, sessionId: "ex", status: "connected", databases: [] } } });
    const id = A.newQueryTab({ profileId: pid });
    expect(await A.ensureSession(id)).toBe("s9");
    expect(h.rpc).toHaveBeenCalledWith("session.open", { profileId: pid, database: db, secret: null });
    expect(useTabs.getState().tabs[id].sessionId).toBe("s9");
  });
  it("a database file whose grant is gone: the error says how to pick it again; other errors pass through", async () => {
    const id = A.newQueryTab({ profileId: "pg1" });
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") throw new RpcError("The database file was replaced.", -32000, "fileReplaced");
      return undefined;
    });
    await expect(A.ensureSession(id)).rejects.toThrow("The database file was replaced. Pick the file again: Edit profile ▸ Re-pick file…");
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") throw new RpcError("busy", -32000, "busy");
      return undefined;
    });
    await expect(A.ensureSession(id)).rejects.toThrow(/^busy$/);
  });
  it("asks for a password for SQL auth when no explorer session and keyring empty; cancel throws", async () => {
    useProfiles.setState({ profiles: [{ ...ms, auth: "sql" }, pg] });
    h.shell.secretExists.mockResolvedValueOnce(false);
    const id = A.newQueryTab({ profileId: "ms1" });
    const p = A.ensureSession(id);
    p.catch(() => {});
    await tick();
    answer("password", null);
    await expect(p).rejects.toThrow("Connection cancelled.");
  });
  it("asks again after expiry even with a connected explorer", async () => {
    useProfiles.setState({ profiles: [{ ...ms, auth: "sql", passwordRetention: "duration", passwordRetentionMinutes: 15 }, pg] });
    useConnections.setState({ servers: { ms1: { profileId: "ms1", sessionId: "explorer", status: "connected", databases: [] } } });
    h.shell.secretExists.mockResolvedValueOnce(false);
    const id = A.newQueryTab({ profileId: "ms1" });
    const pending = A.ensureSession(id);
    pending.catch(() => {});
    await tick();
    answer("password", null);
    await expect(pending).rejects.toThrow("Connection cancelled.");
    expect(useConnections.getState().servers.ms1.sessionId).toBe("explorer");
    expect(h.rpc).not.toHaveBeenCalledWith("session.open", expect.anything());
  });
  it("passes a typed secret and starts the explorer connection", async () => {
    useProfiles.setState({ profiles: [{ ...ms, auth: "sql" }, pg] });
    h.shell.secretExists.mockResolvedValueOnce(false);
    h.rpc.mockImplementation(async (m: string) => (m === "session.open" ? { sessionId: "s1", database: null } : m === "meta.databases" ? [] : { serverVersion: "v" }));
    const id = A.newQueryTab({ profileId: "ms1" });
    const p = A.ensureSession(id);
    await tick();
    answer("password", "hunter2");
    expect(await p).toBe("s1");
    expect(h.rpc).toHaveBeenCalledWith("session.open", expect.objectContaining({ secret: "hunter2" }));
  });
  it("asks to reconnect a disconnected server; declining throws ReconnectDeclined, accepting clears the flag", async () => {
    useConnections.setState({ disconnected: { ms1: true } });
    const id = A.newQueryTab({ profileId: "ms1" });
    let p = A.ensureSession(id);
    p.catch(() => {});
    await tick();
    answer("confirm", false);
    await expect(p).rejects.toBeInstanceOf(A.ReconnectDeclined);
    h.rpc.mockImplementation(async (m: string) => (m === "session.open" ? { sessionId: "s2" } : m === "meta.databases" ? [] : {}));
    p = A.ensureSession(id);
    await tick();
    answer("confirm", true);
    expect(await p).toBe("s2");
    expect(useConnections.getState().disconnected.ms1).toBeUndefined();
  });
});

describe("changeConnection / changeDatabase", () => {
  it("ignores unknown tabs, warns when busy, and prompts when a transaction is open", async () => {
    await A.changeConnection("zz", "ms1");
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { busy: true });
    await A.changeConnection(id, "pg1");
    expect(toasts().join()).toMatch(/Cancel the running query before switching connection/);
    useTabs.getState().update(id, { busy: false, txState: "open" });
    let p = A.changeConnection(id, "pg1");
    await tick();
    answer("txSwitch", false);
    await p;
    expect(useTabs.getState().tabs[id].profileId).toBe("ms1");
    p = A.changeConnection(id, "pg1");
    await tick();
    h.rpc.mockResolvedValue(undefined);
    answer("txSwitch", true);
    await p;
    expect(useTabs.getState().tabs[id].profileId).toBe("pg1");
  });
  it.each([["pg1", "pgsql", "postgres"], ["ms1", "tsql", "master"]])("switches to %s: language %s, default db %s, old session closed", async (pid, lang, db) => {
    h.rpc.mockResolvedValue(undefined);
    const id = A.newQueryTab({ profileId: pid === "pg1" ? "ms1" : "pg1" });
    useTabs.getState().update(id, { sessionId: "old" });
    await A.changeConnection(id, pid);
    expect(h.rpc).toHaveBeenCalledWith("session.close", { sessionId: "old", force: true });
    expect(h.models.get(id).lang).toBe(lang);
    expect(useTabs.getState().tabs[id]).toMatchObject({ profileId: pid, database: db, sessionId: undefined });
    await A.changeConnection(id, pid, "custom");
    expect(useTabs.getState().tabs[id].database).toBe("custom");
  });
  it("changeDatabase: guards, with and without a session, and error reporting", async () => {
    await A.changeDatabase("zz", "x");
    const id = A.newQueryTab({ profileId: "ms1", database: "a" });
    h.rpc.mockClear();
    await A.changeDatabase(id, "a");
    expect(h.rpc).not.toHaveBeenCalled();
    useTabs.getState().update(id, { busy: true });
    await A.changeDatabase(id, "b");
    expect(toasts().join()).toMatch(/before switching database/);
    useTabs.getState().update(id, { busy: false, txState: "open" });
    await A.changeDatabase(id, "b");
    expect(toasts().join()).toMatch(/Commit or roll back/);
    useTabs.getState().update(id, { txState: "none" });
    await A.changeDatabase(id, "b");
    expect(useTabs.getState().tabs[id].database).toBe("b");
    useTabs.getState().update(id, { sessionId: "s1" });
    h.rpc.mockResolvedValueOnce({ database: "c" });
    await A.changeDatabase(id, "c");
    expect(useTabs.getState().tabs[id].database).toBe("c");
    expect(A.benchMarks.dbSwitch.length).toBeGreaterThan(0);
    h.rpc.mockRejectedValueOnce(new Error("denied"));
    await A.changeDatabase(id, "d");
    expect(useTabs.getState().tabs[id].database).toBe("c");
    expect(toasts().join()).toMatch(/Could not switch to d: denied/);
  });
});

describe("runQuery", () => {
  function mkTab(pid = "ms1") {
    const id = A.newQueryTab({ profileId: pid, text: "select 1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    return id;
  }
  const executed = () => h.rpc.mock.calls.filter((c) => c[0] === "query.execute");

  it("without a connection opens the connection palette", async () => {
    const id = A.newQueryTab();
    expect(await A.runQuery(id)).toBeUndefined();
    expect(useUi.getState().palette?.mode).toBe("connections");
    expect(await A.runQuery("zz")).toBeUndefined();
  });
  it("skips empty sql, busy tabs and cancelled pending edits", async () => {
    const id = mkTab();
    h.exec = { sql: "  ", lineOffset: 1, isSelection: false };
    expect(await A.runQuery(id)).toBeUndefined();
    h.exec = { sql: "select 1", lineOffset: 1, isSelection: false };
    useTabs.getState().update(id, { busy: true });
    expect(await A.runQuery(id)).toBeUndefined();
    expect(toasts().join()).toMatch(/already running/);
    useTabs.getState().update(id, { busy: false });
    h.resolvePending.mockResolvedValue(false);
    expect(await A.runQuery(id)).toBeUndefined();
    expect(executed()).toHaveLength(0);
  });
  it.each([
    ["ms1", true],
    ["pg1", false],
  ])("executes on %s with engine-specific options (mssql statistics only)", async (pid, isMs) => {
    useSettings.setState({ statisticsIo: true, statisticsTime: true, maxRows: 500 } as any);
    h.rpc.mockResolvedValue({ executionId: "e1" });
    const id = mkTab(pid);
    useTabs.getState().update(id, { actualPlan: true });
    expect(await A.runQuery(id)).toBe("e1");
    const opts = executed()[0][1].options;
    expect(opts.statisticsIo).toBe(isMs);
    expect(opts.statisticsTime).toBe(isMs);
    expect(opts.actualPlan).toBe(isMs);
    expect(opts.maxRows).toBe(500);
    expect(useExecutions.getState().views.e1.state).toBe("queued");
    expect(useTabs.getState().tabs[id]).toMatchObject({ busy: true, activeExecutionId: "e1", bottomTab: isMs ? "plan" : "results" });
    expect(executionData.get("e1")).toBeTruthy();
    expect(h.markers).toHaveBeenCalled();
    expect(h.stream).toHaveBeenCalled();
  });
  it("uses the statement under the cursor in statement mode and a selection as-is", async () => {
    h.rpc.mockResolvedValue({ executionId: "e1" });
    const id = mkTab();
    h.editor = { getPosition: () => ({ lineNumber: 1, column: 1 }) };
    h.models.get(id).text = "select 1; select 2";
    await A.runQuery(id, { mode: "statement" });
    expect(executed()[0][1].sql).toBe("select 1");
    h.rpc.mockClear();
    useTabs.getState().update(id, { busy: false });
    h.exec = { sql: "select 2", lineOffset: 5, isSelection: true };
    await A.runQuery(id, { mode: "statement" });
    expect(executed()[0][1]).toMatchObject({ sql: "select 2" });
    expect(executed()[0][1].options.lineOffset).toBe(5);
  });
  it("collects undeclared parameters; cancelling the dialog aborts, answering sends them", async () => {
    h.rpc.mockResolvedValue({ executionId: "e1" });
    const id = mkTab();
    h.exec = { sql: "select @p", lineOffset: 1, isSelection: false };
    let p = A.runQuery(id);
    await tick();
    answer("params", null);
    expect(await p).toBeUndefined();
    p = A.runQuery(id);
    await tick();
    answer("params", [{ name: "@p", value: "1", isNull: false, type: "int" }, { name: "@q", value: "x", isNull: true }]);
    expect(await p).toBe("e1");
    expect(executed()[0][1].options.parameters).toEqual([
      { name: "@p", value: "1", isNull: false, type: "int" },
      { name: "@q", value: null, isNull: true, type: null },
    ]);
  });
  it("reports ensureSession failures as session errors, silently for a declined reconnect", async () => {
    const id = A.newQueryTab({ profileId: "ms1", text: "x" });
    expect(await A.runQuery(id)).toBeUndefined();
  });
  it("shows a session error toast when opening fails", async () => {
    h.rpc.mockRejectedValue(new Error("boom"));
    const id = A.newQueryTab({ profileId: "ms1", text: "x" });
    useConnections.setState({ servers: { ms1: { profileId: "ms1", sessionId: "ex", status: "connected", databases: [] } } });
    expect(await A.runQuery(id)).toBeUndefined();
    expect(useTabs.getState().tabs[id].sessionError).toBe("boom");
    expect(toasts()).toContain("boom");
  });
  it("silently returns when the user declines to reconnect", async () => {
    useConnections.setState({ disconnected: { ms1: true } });
    const id = A.newQueryTab({ profileId: "ms1", text: "x" });
    const p = A.runQuery(id);
    await tick();
    answer("confirm", false);
    expect(await p).toBeUndefined();
    expect(toasts()).toEqual([]);
  });
  it("reopens the session once when the sidecar lost it (notFound)", async () => {
    let n = 0;
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "query.execute") {
        if (n++ === 0) throw new RpcError("gone", -32000, "notFound");
        return { executionId: "e2" };
      }
      if (m === "session.open") return { sessionId: "s2" };
      return {};
    });
    useConnections.setState({ servers: { ms1: { profileId: "ms1", sessionId: "ex", status: "connected", databases: [] } } });
    const id = mkTab();
    expect(await A.runQuery(id)).toBe("e2");
    expect(useTabs.getState().tabs[id].sessionId).toBe("s2");
  });
  it("toasts execution errors", async () => {
    h.rpc.mockRejectedValue(new Error("syntax"));
    const id = mkTab();
    expect(await A.runQuery(id)).toBeUndefined();
    expect(toasts()).toContain("syntax");
  });
  it.each([
    ["destructive", "confirmedDestructive"],
    ["prodFirstRun", "confirmedProd"],
  ])("guard block %s: confirming re-runs with %s", async (reason, flag) => {
    h.rpc.mockResolvedValueOnce({ blocked: { reason } }).mockResolvedValueOnce({ executionId: "e3" });
    const id = mkTab();
    const p = A.runQuery(id);
    await tick();
    answer("guard", true);
    expect(await p).toBe("e3");
    expect(executed()[1][1].options[flag]).toBe(true);
  });
  it("guard block declined aborts", async () => {
    h.rpc.mockResolvedValue({ blocked: { reason: "destructive" } });
    const id = mkTab();
    const p = A.runQuery(id);
    await tick();
    answer("guard", false);
    expect(await p).toBeUndefined();
    expect(executed()).toHaveLength(1);
  });
  it("keeps only the last three executions", async () => {
    const id = mkTab();
    for (const e of ["e1", "e2", "e3", "e4"]) {
      h.rpc.mockResolvedValue({ executionId: e });
      useTabs.getState().update(id, { busy: false });
      await A.runQuery(id);
    }
    expect(useTabs.getState().tabs[id].executions).toEqual(["e2", "e3", "e4"]);
    expect(useExecutions.getState().views.e1).toBeUndefined();
    expect(executionData.get("e1")).toBeUndefined();
  });
  it("a new run resets the shown error; a comparison with a run that falls out of the kept three ends with a message", async () => {
    const id = mkTab();
    for (const e of ["e1", "e2", "e3"]) {
      h.rpc.mockResolvedValue({ executionId: e });
      useTabs.getState().update(id, { busy: false });
      await A.runQuery(id);
    }
    useTabs.getState().update(id, { busy: false, ledgerError: 1, compare: { executionId: "e2", resultIndex: 0 } });
    h.rpc.mockResolvedValue({ executionId: "e4" });
    await A.runQuery(id);
    expect(useTabs.getState().tabs[id]).toMatchObject({ ledgerError: undefined, compare: { executionId: "e2", resultIndex: 0 } });
    useTabs.getState().update(id, { busy: false });
    h.rpc.mockResolvedValue({ executionId: "e5" });
    await A.runQuery(id);
    expect(useTabs.getState().tabs[id].compare).toBeUndefined();
    expect(toasts()).toContain("The compared run was discarded: a tab keeps its last 3 runs.");
  });
  it("feeds frame callbacks into the execution data", async () => {
    h.rpc.mockResolvedValue({ executionId: "e1" });
    const sink: any = {};
    h.stream.mockImplementation((_id: string, s: any) => { Object.assign(sink, s); return { done: Promise.reject(new Error("end")), cancel() {} }; });
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const id = mkTab();
    await A.runQuery(id);
    const data = executionData.get("e1")!;
    const get = vi.spyOn(data, "get").mockReturnValue({ setSchema: vi.fn(), append: vi.fn(), finish: vi.fn() } as any);
    sink.onSchema(0, {});
    sink.onBatch(0, {});
    sink.onBatch(0, {});
    sink.onEndResultSet(0, true);
    sink.onEnd();
    expect(get).toHaveBeenCalledTimes(4);
    expect(A.benchMarks.firstBatch.length).toBeGreaterThan(0);
    await tick();
    expect(warn).toHaveBeenCalled();
    warn.mockRestore();
  });
});

describe("run lifecycle: streams stop with their runs (M6, M7)", () => {
  const view = (id: string, tabId: string, state: string) =>
    useExecutions.getState().put({ id, tabId, sessionId: "s1", state, startedAt: 0, resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql: "", lineOffset: 1 } as any);

  it("disposeExecution stops the run's poll loop (the shell drops its frames) and closes it on the sidecar", () => {
    h.rpc.mockResolvedValue(undefined);
    const id = A.newQueryTab({ profileId: "ms1" });
    view("e9", id, "streaming");
    executionData.set("e9", new ExecutionData("e9"));
    A.disposeExecution("e9");
    expect(h.stopStream).toHaveBeenCalledWith("e9");
    expect(h.rpc).toHaveBeenCalledWith("query.close", { executionId: "e9" });
    expect(h.shell.resultsDiscard).not.toHaveBeenCalled(); // never for a run whose end already arrived: stopStream decides
    expect(executionData.has("e9")).toBe(false);
    expect(useExecutions.getState().views.e9).toBeUndefined();
  });

  it("Explain disposes the run it pushes out of the tab's last three, and its plan run is closed like any run", async () => {
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1", executions: ["r1", "r2", "r3"], activeExecutionId: "r3", compare: { executionId: "r1" } as any });
    for (const r of ["r1", "r2", "r3"]) {
      view(r, id, "done");
      executionData.set(r, new ExecutionData(r));
    }
    h.rpc.mockResolvedValue({ executionId: "p9" });
    await A.showPlan(id, "estimated");
    const t = tabOf(id);
    expect(t.executions.slice(0, 2)).toEqual(["r2", "r3"]);
    expect(t.executions[2]).toBe(t.activeExecutionId);
    // Before, r1 was only sliced off the list: its view, rows, sidecar execution and shell queue stayed for good.
    expect(useExecutions.getState().views.r1).toBeUndefined();
    expect(executionData.has("r1")).toBe(false);
    expect(h.stopStream).toHaveBeenCalledWith("r1");
    expect(h.rpc).toHaveBeenCalledWith("query.close", { executionId: "r1" });
    expect(t.compare).toBeUndefined();
    expect(toasts()).toContain("The compared run was discarded: a tab keeps its last 3 runs.");

    // The plan is an execution of the sidecar's (§11: plan.get runs as one): when it falls out in turn it is closed.
    const plan = t.activeExecutionId!;
    expect(plan).toBe("p9");
    h.rpc.mockClear();
    A.disposeExecution(plan);
    expect(h.rpc).toHaveBeenCalledWith("query.close", { executionId: "p9" });
  });

  it("an engine restart stops every stream and marks the runs still going as interrupted, outcome unknown", () => {
    const a = A.newQueryTab({ profileId: "ms1", title: "a.sql" });
    const b = A.newQueryTab({ profileId: "pg1", title: "b.sql" });
    useTabs.getState().update(a, { sessionId: "s1", busy: true, activeExecutionId: "e1" });
    useTabs.getState().update(b, { sessionId: "s2", busy: true, activeExecutionId: "e2" });
    view("e1", a, "streaming");
    view("e2", b, "queued");
    view("e3", a, "done");
    const data = new ExecutionData("e1");
    executionData.set("e1", data);
    h.stopAll.mockReturnValueOnce(["e1", "e2"]);
    A.onSidecarRestarted();
    expect(h.stopAll).toHaveBeenCalledTimes(1);
    const v = useExecutions.getState().views;
    for (const id of ["e1", "e2"]) {
      expect(v[id]).toMatchObject({ state: "failed", error: { message: A.RUN_INTERRUPTED } });
      expect(v[id].messages.at(-1)).toMatchObject({ severity: "error", message: A.RUN_INTERRUPTED });
    }
    expect(A.RUN_INTERRUPTED).toMatch(/outcome is unknown/);
    expect(v.e3.state).toBe("done");
    expect(v.e3.messages).toEqual([]);
    expect(data.streamEnded).toBe(true); // the rows that arrived stay, marked interrupted
    expect(useUi.getState().banner?.text).toMatch(/runs in progress in a\.sql, b\.sql were interrupted/);
    expect(useUi.getState().banner?.text).not.toMatch(/nothing was lost|no work was lost/);
    expect(tabOf(a).busy).toBe(false);
  });

  it("the restart banner never says nothing was lost when a tab had a session, a trace, a debugger or a running tool (L32)", () => {
    const a = A.newQueryTab({ profileId: "ms1", title: "a.sql" });
    const b = A.newQueryTab({ profileId: "pg1", title: "b.sql" });
    useTabs.getState().update(a, { sessionId: "s1", traceId: "tr1" });
    useTabs.getState().update(b, { sessionId: "s2", debugId: "d1" });
    useToolTabs.setState({ tools: { "tool:1": { id: "tool:1", kind: "dataCompare", title: "Data compare", params: {} } } });
    view("c1", "tool:1", "streaming");
    A.onSidecarRestarted();
    const text = useUi.getState().banner!.text;
    expect(text).toContain("Data compare stopped while running; run it again.");
    expect(text).toContain("The traces in a.sql stopped.");
    expect(text).toContain("The debugging sessions in b.sql ended.");
    expect(text).toContain("The 2 connected tabs get a new session at the next run: temporary tables and session settings from before are gone.");
    expect(text).not.toMatch(/no work was lost/);
    // A second restart: no tab has a session any more, nothing ran, so nothing was taken.
    A.onSidecarRestarted();
    expect(useUi.getState().banner!.text).toBe("The Querithm engine restarted. No tab was connected or running a query, so no work was lost; servers reconnect now.");
    useToolTabs.setState({ tools: {} });
  });

  it("a stream that breaks off keeps what arrived and says why in the run's Messages", async () => {
    h.rpc.mockResolvedValue({ executionId: "e7" });
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    h.stream.mockReturnValue({ done: Promise.reject(new Error("Expected to read 128 metadata bytes")), cancel() {} });
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.runQuery(id);
    await tick();
    expect(executionData.get("e7")!.streamEnded).toBe(true);
    expect(useExecutions.getState().views.e7.messages.at(-1)).toMatchObject({ severity: "error", message: "The results could not be read: Expected to read 128 metadata bytes" });
    warn.mockRestore();
  });

  it("a query.done that arrived before the view still arms the linger timer: the stream starts first (W3)", () => {
    vi.useFakeTimers();
    try {
      const off = A.installExecutionListeners();
      const id = A.newQueryTab({ profileId: "ms1" });
      // query.done came before query.execute's reply was processed: buffered until the view exists.
      (h.notify.get("query.done") ?? []).forEach((f) => f({ executionId: "e11", state: "done", resultSetCount: 0 }));
      h.isStreaming.mockImplementation((x: string) => x === "e11" && h.stream.mock.calls.some((c) => c[0] === "e11"));
      A.trackExecution(id, "s1", "e11", "select 1", 1);
      expect(useExecutions.getState().views.e11.state).toBe("done");
      vi.advanceTimersByTime(A.STREAM_LINGER_MS);
      // Before, the early query.done found no stream yet, armed nothing, and a lost end frame left the poll loop for good.
      expect(h.stopStream).toHaveBeenCalledWith("e11");
      off();
    } finally {
      h.isStreaming.mockImplementation(() => false);
      vi.useRealTimers();
    }
  });

  it("an engine that stopped and was not restarted takes the tabs' sessions along and names what was lost (W1)", () => {
    const a = A.newQueryTab({ profileId: "ms1", title: "a.sql" });
    const b = A.newQueryTab({ profileId: "pg1", title: "b.sql" });
    useTabs.getState().update(a, { sessionId: "s1", txState: "open", busy: true, activeExecutionId: "e1" });
    useTabs.getState().update(b, { sessionId: "s2", txState: "unknown" });
    view("e1", a, "running");
    const lost = A.onSidecarFailed();
    expect(lost).toContain("Open transactions in a.sql were lost (rolled back by the server).");
    expect(lost.join(" ")).toMatch(/may have been open in b\.sql/);
    expect(lost.join(" ")).toMatch(/runs in progress in a\.sql were interrupted/);
    for (const id of [a, b]) expect(tabOf(id)).toMatchObject({ sessionId: undefined, txState: "none", busy: false });
    expect(useExecutions.getState().views.e1).toMatchObject({ state: "failed", error: { message: A.RUN_STOPPED } });
    // The failure banner is engineStatus's; this one is left alone.
    expect(useUi.getState().banner).toBeNull();
  });

  it("a stream still open 30 s after its run's query.done (its end frame was lost) is stopped", async () => {
    vi.useFakeTimers();
    try {
      const off = A.installExecutionListeners();
      const id = A.newQueryTab({ profileId: "ms1" });
      view("e8", id, "streaming");
      executionData.set("e8", new ExecutionData("e8"));
      h.isStreaming.mockReturnValue(true);
      (h.notify.get("query.done") ?? []).forEach((f) => f({ executionId: "e8", state: "done", resultSetCount: 0 }));
      vi.advanceTimersByTime(A.STREAM_LINGER_MS - 1);
      expect(h.stopStream).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(h.stopStream).toHaveBeenCalledWith("e8");
      expect(executionData.get("e8")!.streamEnded).toBe(true);
      off();
    } finally {
      h.isStreaming.mockReturnValue(false);
      vi.useRealTimers();
    }
  });
});

describe("cancelQuery", () => {
  it("cancels only running executions", async () => {
    h.rpc.mockResolvedValue(undefined);
    const id = A.newQueryTab({ profileId: "ms1" });
    await A.cancelQuery(id);
    useTabs.getState().update(id, { activeExecutionId: "e1" });
    await A.cancelQuery(id);
    const base = { tabId: id, sessionId: "s", startedAt: 0, resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql: "", lineOffset: 1 };
    useExecutions.getState().put({ id: "e1", state: "done", ...base } as any);
    await A.cancelQuery(id);
    expect(h.rpc).not.toHaveBeenCalled();
    useExecutions.getState().patch("e1", { state: "running" });
    await A.cancelQuery(id);
    expect(h.rpc).toHaveBeenCalledWith("query.cancel", { executionId: "e1" });
    h.rpc.mockRejectedValueOnce(new Error("nope"));
    await A.cancelQuery(id);
    await tick();
    expect(toasts()).toContain("nope");
    A.disposeExecution("e1"); // no pending "has not stopped" check outlives the test
  });

  it("offers Restart engine when the run has not stopped some time after Cancel, and not when it has", async () => {
    vi.useFakeTimers();
    try {
      h.rpc.mockResolvedValue(undefined);
      useUi.setState({ banner: null });
      const id = A.newQueryTab({ profileId: "ms1" });
      const base = { tabId: id, sessionId: "s", startedAt: 0, resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql: "", lineOffset: 1 };
      useTabs.getState().update(id, { activeExecutionId: "e1" });
      useExecutions.getState().put({ id: "e1", state: "running", ...base } as any);

      // Stops in time: no banner.
      await A.cancelQuery(id);
      useExecutions.getState().patch("e1", { state: "cancelled" });
      await vi.advanceTimersByTimeAsync(A.CANCEL_STUCK_MS + 1);
      expect(useUi.getState().banner).toBeNull();

      // Still running after the wait: the banner names the tab and restarts the engine.
      useExecutions.getState().patch("e1", { state: "running" });
      await A.cancelQuery(id);
      await vi.advanceTimersByTimeAsync(A.CANCEL_STUCK_MS - 1);
      expect(useUi.getState().banner).toBeNull();
      await vi.advanceTimersByTimeAsync(1);
      const b = useUi.getState().banner!;
      expect(b).toMatchObject({ kind: "warn" });
      expect(b.text).toContain("has not stopped");
      expect(b.text).toContain("may still end by itself");
      b.action!.run();
      expect(h.shell.restart).toHaveBeenCalled();

      // A disposed view (tab closed, run discarded) never raises it later.
      useUi.setState({ banner: null });
      await A.cancelQuery(id);
      A.disposeExecution("e1");
      await vi.advanceTimersByTimeAsync(A.CANCEL_STUCK_MS + 1);
      expect(useUi.getState().banner).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("execution notifications", () => {
  const fire = (m: string, p: any) => (h.notify.get(m) ?? []).forEach((f) => f(p));
  let off: () => void;
  let id: string;
  beforeEach(() => {
    off = A.installExecutionListeners();
    id = A.newQueryTab({ profileId: "ms1", text: "a\nb\nc" });
    useTabs.getState().update(id, { sessionId: "s1", activeExecutionId: "e1", database: "master" });
    useExecutions.getState().put({ id: "e1", tabId: id, sessionId: "s1", state: "queued", startedAt: 0, resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql: "", lineOffset: 1 });
  });

  it("tracks queued/started/resultSet/plan and edit info", () => {
    fire("query.queued", { executionId: "e1" });
    expect(useExecutions.getState().views.e1.queued).toBe(true);
    fire("query.started", { executionId: "e1" });
    expect(useExecutions.getState().views.e1).toMatchObject({ state: "running", queued: false });
    fire("query.resultSet", { executionId: "e1", resultIndex: 1, edit: { table: "t" } });
    expect(useExecutions.getState().views.e1).toMatchObject({ state: "streaming", resultSetCount: 2, edits: { 1: { table: "t" } } });
    fire("query.resultSet", { executionId: "e1", resultIndex: 0 });
    expect(useExecutions.getState().views.e1.resultSetCount).toBe(2);
    fire("query.plan", { executionId: "e1", batchIndex: 0, plan: { root: 1 }, raw: "<x/>" });
    expect(useExecutions.getState().views.e1.plans).toHaveLength(1);
  });
  it("buffers events that arrive before the view exists", () => {
    fire("query.started", { executionId: "late" });
    expect(useExecutions.getState().views.late).toBeUndefined();
    useExecutions.getState().put({ id: "late", tabId: id, sessionId: "s1", state: "queued", startedAt: 0, resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql: "", lineOffset: 1 });
    h.rpc.mockResolvedValue({ executionId: "late" });
    // runQuery's startExecutionView flushes the buffer
    return A.runQuery(id).then(() => {
      expect(useExecutions.getState().views.late.state).toBe("running");
    });
  });
  it("marks error lines from error messages only; an error never takes the view away (§12 bottom panel)", () => {
    fire("query.message", { executionId: "e1", severity: "info", message: "hi" });
    expect(h.markers).not.toHaveBeenCalledWith(expect.anything(), "execution", expect.arrayContaining([expect.anything()]));
    expect(useTabs.getState().tabs[id].bottomTab).toBe("results");
    fire("query.message", { executionId: "e1", severity: "error", message: "bad", editorLine: 2 });
    fire("query.message", { executionId: "e1", severity: "error", message: "off", editorLine: 99 });
    expect(useTabs.getState().tabs[id].errorLines).toEqual([2]);
    expect(useExecutions.getState().views.e1.messages).toHaveLength(3);
    expect(useTabs.getState().tabs[id].bottomTab).toBe("results");
    useTabs.getState().update(id, { bottomTab: "plan" });
    fire("query.message", { executionId: "e1", severity: "error", message: "again", editorLine: 2 });
    expect(useTabs.getState().tabs[id].bottomTab).toBe("plan");
  });
  it("query.messages (M13): a batch is appended in place, O(batch), and the editor markers are set once per batch with an error", () => {
    const msg = (i: number, severity = "info", editorLine?: number) => ({ executionId: "e1", severity, message: `m${i}`, batchIndex: 0, ts: i, editorLine });
    h.markers.mockClear();
    fire("query.messages", { executionId: "e1", messages: Array.from({ length: 1000 }, (_, i) => msg(i, i % 400 === 7 ? "error" : "info", i % 400 === 7 ? 2 : undefined)) });
    const first = useExecutions.getState().views.e1.messages;
    expect(first).toHaveLength(1000);
    // Three errors in the batch: one marker update, not three (and no rescan per error).
    expect(h.markers.mock.calls.filter((c) => c[1] === "execution")).toHaveLength(1);
    expect(useTabs.getState().tabs[id].errorLines).toEqual([2, 2, 2]);
    h.markers.mockClear();
    fire("query.messages", { executionId: "e1", messages: [msg(1000), msg(1001)] });
    const second = useExecutions.getState().views.e1.messages;
    // The same array grew: no copy of the 1,000 earlier messages; a batch without errors leaves the markers alone.
    expect(second).toBe(first);
    expect(second).toHaveLength(1002);
    expect(second.at(-1)!.message).toBe("m1001");
    expect(h.markers).not.toHaveBeenCalled();
    // The single-message form still works during the transition.
    fire("query.message", msg(1002));
    expect(useExecutions.getState().views.e1.messages).toHaveLength(1003);
  });
  it("query.messages: a run keeps at most MESSAGE_LIMIT messages", () => {
    const batch = (from: number, n: number) => ({ executionId: "e1", messages: Array.from({ length: n }, (_, i) => ({ executionId: "e1", severity: "info", message: `m${from + i}`, batchIndex: 0, ts: 0 })) });
    for (let k = 0; k < 50; k++) fire("query.messages", batch(k * 500, 500));
    const kept = useExecutions.getState().views.e1.messages;
    expect(kept).toHaveLength(A.MESSAGE_LIMIT);
    expect(kept.at(-1)!.message).toBe(`m${A.MESSAGE_LIMIT - 1}`);
  });
  it("query.messages: batches and result sets keep the ledger's order", () => {
    fire("query.messages", { executionId: "e1", messages: [{ executionId: "e1", severity: "info", message: "a" }, { executionId: "e1", severity: "info", message: "b" }] });
    fire("query.resultSet", { executionId: "e1", resultIndex: 0 });
    fire("query.messages", { executionId: "e1", messages: [{ executionId: "e1", severity: "error", message: "bad", editorLine: 4 }] });
    fire("query.resultSet", { executionId: "e1", resultIndex: 1 });
    expect(useExecutions.getState().views.e1.setAfter).toEqual({ 0: 2, 1: 3 });
  });
  it("records how many messages had arrived when each result set began (the ledger's order)", () => {
    fire("query.message", { executionId: "e1", severity: "info", message: "(3 rows affected)" });
    fire("query.resultSet", { executionId: "e1", resultIndex: 0 });
    fire("query.message", { executionId: "e1", severity: "error", message: "bad", editorLine: 4 });
    fire("query.resultSet", { executionId: "e1", resultIndex: 1 });
    expect(useExecutions.getState().views.e1.setAfter).toEqual({ 0: 1, 1: 2 });
  });
  it("done: finishes the tab and follows a database change; a run without result sets stays on Results (its summary shows there)", () => {
    h.rpc.mockResolvedValue({});
    useTabs.getState().update(id, { busy: true });
    fire("query.done", { executionId: "e1", state: "done", elapsedMs: 5, resultSetCount: 0, database: "other", statistics: undefined });
    expect(useTabs.getState().tabs[id]).toMatchObject({ busy: false, bottomTab: "results", database: "other" });
    expect(useExecutions.getState().views.e1).toMatchObject({ state: "done", elapsedMs: 5, statistics: [] });
  });
  it("done with failed state keeps the view and does not discard pending Arrow frames", () => {
    const data = new ExecutionData("e1");
    const incompleteRs = { index: 0, complete: false, rowCount: 0 } as any;
    data.resultSets = [incompleteRs];
    executionData.set("e1", data);
    useTabs.getState().update(id, { bottomTab: "results" });
    fire("query.done", { executionId: "e1", state: "failed", resultSetCount: 1 });
    expect(useTabs.getState().tabs[id].bottomTab).toBe("results");
    expect(data.resultSets).toHaveLength(1);
    expect(useExecutions.getState().views.e1.resultSetCount).toBe(1);
    fire("query.done", { executionId: "e1", state: "failed", resultSetCount: 0 });
    expect(useTabs.getState().tabs[id].bottomTab).toBe("results");
  });
  it.each([
    ["ms1", true], ["ms1", false], ["pg1", true], ["pg1", false],
  ] as const)("keeps delayed frames and completed empty results (%s, control first=%s)", async (profileId, controlFirst) => {
    useTabs.getState().update(id, { profileId });
    h.rpc.mockResolvedValue({ executionId: "ordered" });
    await A.runQuery(id);
    const sink = h.stream.mock.calls.at(-1)![1];
    const batch = tableFromArrays({ value: [1, 2] }).batches[0];
    sink.onSchema(0, batch.schema);
    sink.onEndResultSet(0, false); // a legitimate zero-row SELECT
    sink.onSchema(1, batch.schema);
    fire("query.message", { executionId: "ordered", severity: "error", message: "bad" });
    const done = () => fire("query.done", { executionId: "ordered", state: "failed", resultSetCount: 3 });
    if (controlFirst) done();
    sink.onBatch(1, batch); // buffered Arrow data arrives after the error notification
    sink.onEndResultSet(1, false);
    sink.onSchema(2, batch.schema); // failed before the first row
    sink.onEnd();
    if (!controlFirst) done();
    const data = executionData.get("ordered")!;
    expect(data.resultSets.map((r) => [r.index, r.rowCount])).toEqual([[0, 0], [1, 2]]);
    expect(useExecutions.getState().views.ordered.resultSetCount).toBe(2);
    // The two result sets that did arrive stay on screen next to the error.
    expect(useTabs.getState().tabs[id].bottomTab).toBe("results");
    off();
  });
  it("session.stateChanged updates the owning tab; unknown sessions are ignored", () => {
    fire("session.stateChanged", { sessionId: "nope", txState: "open", busy: false });
    fire("session.stateChanged", { sessionId: "s1", txState: "open", txStartedAt: 10, spid: 55, busy: true, database: "newdb" });
    expect(useTabs.getState().tabs[id]).toMatchObject({ txState: "open", txStartedAt: 10, spid: 55, busy: true, database: "newdb" });
    fire("session.stateChanged", { sessionId: "s1", txState: "none", busy: false });
    expect(useTabs.getState().tabs[id]).toMatchObject({ txState: "none", spid: undefined, database: "newdb" });
  });
  it("auth.prompt opens the auth dialog, and the disposer unsubscribes", () => {
    fire("auth.prompt", { message: "go", verificationUrl: "http://x", userCode: "ABC" });
    expect(dialogOf("authPrompt")).toMatchObject({ message: "go", url: "http://x", code: "ABC" });
    off();
    fire("query.started", { executionId: "e1" });
    expect(useExecutions.getState().views.e1.state).toBe("queued");
  });
});

describe("transactions", () => {
  it.each([["begin", "tx.begin"], ["commit", "tx.commit"], ["rollback", "tx.rollback"]] as const)("%s calls %s and stores the state", async (op, method) => {
    h.rpc.mockResolvedValue({ txState: op === "begin" ? "open" : "none", txStartedAt: 1, spid: 9 });
    const id = A.newQueryTab({ profileId: "pg1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.txAction(id, op);
    expect(h.rpc).toHaveBeenCalledWith(method, { sessionId: "s1" });
    expect(useTabs.getState().tabs[id].txState).toBe(op === "begin" ? "open" : "none");
  });
  it("toasts a failure", async () => {
    const id = A.newQueryTab();
    await A.txAction(id, "begin");
    expect(toasts().join()).toMatch(/Choose a connection/);
  });
  it("shows the sidecar's outcome-unknown text for a COMMIT that was stopped, never a retry hint", async () => {
    const text = "COMMIT was stopped before it finished (the tab closed) and the transaction has ended, so whether the transaction committed is unknown: the server may have rolled it back. Check the data before running the work again.";
    h.rpc.mockRejectedValue(new RpcError(text, -32000, "txOutcomeUnknown"));
    const id = A.newQueryTab({ profileId: "pg1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.txAction(id, "commit");
    expect(toasts()).toEqual([text]);
  });
  it("says the outcome is unknown when the engine stops during COMMIT", async () => {
    h.rpc.mockRejectedValue(new RpcError("dev bridge closed", -32003, "sidecarDown"));
    const id = A.newQueryTab({ profileId: "pg1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.txAction(id, "commit");
    expect(toasts()).toEqual([A.COMMIT_ENGINE_STOPPED]);
    expect(A.COMMIT_ENGINE_STOPPED).not.toMatch(/try again/i);
    await A.txAction(id, "rollback");
    expect(toasts()[1]).toBe("dev bridge closed");
  });
});

describe("objects", () => {
  const obj: any = { schema: "dbo", name: "p", kind: "proc", objectId: 7 };
  const connect = () => useConnections.setState({ servers: { ms1: { profileId: "ms1", sessionId: "ex", status: "connected", databases: [] } }, order: ["ms1"] });

  it("openDefinition opens an editable bound tab or a read-only one", async () => {
    connect();
    h.rpc.mockResolvedValue({ sql: "create proc p", definitionHash: "h1" });
    await A.openDefinition("ms1", "db", obj, true);
    const t = Object.values(useTabs.getState().tabs).at(-1)!;
    expect(t).toMatchObject({ title: "p.sql", readOnly: false, boundObject: { objectId: 7, definitionHash: "h1", name: "p" } });
    // Go to definition of the object already open for edit activates that tab (D25).
    const activate = vi.fn();
    A.workspaceHooks.activate = activate;
    const count = Object.keys(useTabs.getState().tabs).length;
    await A.openDefinition("ms1", "db", { ...obj, schema: "DBO", objectId: undefined }, false);
    expect(Object.keys(useTabs.getState().tabs)).toHaveLength(count);
    expect(activate).toHaveBeenCalledWith(t.id);
    await A.openDefinition("ms1", "db", { ...obj, name: "q", objectId: undefined }, false);
    const r = Object.values(useTabs.getState().tabs).at(-1)!;
    expect(r).toMatchObject({ title: "q.sql", readOnly: true, preview: true, definitionOf: { schema: "dbo", name: "q" } });
    expect(r.boundObject).toBeUndefined();
    h.rpc.mockRejectedValueOnce(new Error("nodef"));
    await A.openDefinition("ms1", "db", obj, true);
    expect(toasts()).toContain("nodef");
  });
  it("one preview tab per strip: the next definition replaces it in place and says so; a kept preview stays (D25)", async () => {
    connect();
    resetAnnouncer();
    h.rpc.mockResolvedValue({ sql: "create proc a", definitionHash: "h" });
    const a = await A.openReadOnlyDefinition("ms1", "db", { schema: "dbo", name: "a" }, "create proc a");
    const b = await A.openReadOnlyDefinition("ms1", "db", { schema: "dbo", name: "b" }, "create proc b");
    expect(b).toBe(a);
    expect(tabOf(a!)).toMatchObject({ title: "b.sql", preview: true, definitionOf: { schema: "dbo", name: "b" } });
    expect(h.models.get(a!).text).toBe("create proc b");
    expect(useAnnounce.getState().text).toBe("Preview a replaced by b.");
    A.keepPreview(a!);
    const c = await A.openReadOnlyDefinition("ms1", "db", { schema: "dbo", name: "c" }, "create proc c");
    expect(c).not.toBe(a);
    expect(tabOf(a!).preview).toBe(false);
    // Another database: the preview is closed and a new one opens there.
    const d = await A.openReadOnlyDefinition("ms1", "other", { schema: "dbo", name: "d" }, "create proc d");
    expect(tabOf(c!)).toBeUndefined();
    expect(tabOf(d!)).toMatchObject({ database: "other", preview: true });
  });

  it.each([
    ["SQLQuery9.sql", "UPDATE Invoice SET x = 1", true, "UPDATE Invoice.sql"],
    ["SQLQuery9.sql", "UPDATE Invoice SET x = 1", false, "SQLQuery9.sql"],
    ["mine.sql", "UPDATE Invoice SET x = 1", true, "mine.sql"],
    ["SQLQuery9.sql", "select 1", true, "SQLQuery9.sql"],
  ])("auto title: %s running %s (setting %s) is titled %s, once, never over a name the user gave (D24)", async (title, text, on, expected) => {
    useSettings.setState({ autoTitle: on });
    h.rpc.mockImplementation(async (m: string) => (m === "query.execute" ? { executionId: "e1" } : { sessionId: "s1" }));
    const id = A.newQueryTab({ profileId: "ms1", title, text });
    h.exec = { sql: text, lineOffset: 1, isSelection: false };
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.runQuery(id);
    expect(tabOf(id).title).toBe(expected);
    useSettings.setState({ autoTitle: true });
  });

  it("scriptObject opens a script tab, optionally running it, and reports errors", async () => {
    vi.useFakeTimers();
    connect();
    h.rpc.mockImplementation(async (m: string) => (m === "meta.script" ? { sql: "select top 100 *" } : { executionId: "e" }));
    await A.scriptObject("ms1", "db", obj, "selectTop", true);
    const t = Object.values(useTabs.getState().tabs).at(-1)!;
    expect(h.models.get(t.id).text).toBe("select top 100 *");
    useTabs.getState().update(t.id, { sessionId: "s1" });
    await vi.advanceTimersByTimeAsync(60);
    expect(h.rpc.mock.calls.some((c) => c[0] === "query.execute")).toBe(true);
    vi.useRealTimers();
    h.rpc.mockRejectedValueOnce(new Error("fail"));
    await A.scriptObject("ms1", "db", obj, "create");
    expect(toasts()).toContain("fail");
  });
  it("script tabs are titled by the verb, so a DROP tab never looks like a CREATE one (B19)", async () => {
    connect();
    h.rpc.mockImplementation(async () => ({ sql: "-- script" }));
    // The default schema (dbo here) is left out of the title; the tooltip and the editor keep the full name.
    for (const [kind, title] of [["drop", `DROP ${obj.name}.sql`], ["dropCreate", `DROP and CREATE ${obj.name}.sql`], ["create", `CREATE ${obj.name}.sql`], ["execute", `EXEC ${obj.name}.sql`], ["selectTop", `${obj.name}.sql`]] as const) {
      await A.scriptObject("ms1", "db", obj, kind);
      expect(Object.values(useTabs.getState().tabs).at(-1)!.title, kind).toBe(title);
    }
  });
  it.each([
    ["ms1", "dbo", "DROP p.sql"],
    ["ms1", "sales", "DROP sales.p.sql"],
    ["pg1", "public", "DROP p.sql"],
    ["pg1", "dbo", "DROP dbo.p.sql"],
  ])("on %s, schema %s: %s (only the engine's default schema is left out)", async (profileId, schema, title) => {
    connect();
    h.rpc.mockImplementation(async () => ({ sql: "-- script" }));
    await A.scriptObject(profileId, "db", { ...obj, schema }, "drop");
    expect(Object.values(useTabs.getState().tabs).at(-1)!.title).toBe(title);
  });

  describe("applyProcedure / compareWithServer", () => {
    let id: string;
    beforeEach(() => {
      connect();
      id = A.newQueryTab({
        profileId: "ms1", database: "db", text: "alter proc p",
        boundObject: { profileId: "ms1", database: "db", objectId: 7, schema: "dbo", name: "p", kind: "proc", definitionHash: "h1" },
      });
      useTabs.getState().update(id, { sessionId: "s1" });
    });
    it("informs when the tab is not bound", async () => {
      const u = A.newQueryTab();
      await A.applyProcedure(u);
      expect(toasts().join()).toMatch(/not bound/);
      await A.compareWithServer(u);
    });
    it("reports server errors other than noDefinition", async () => {
      h.rpc.mockRejectedValue(new Error("oops"));
      await A.applyProcedure(id);
      expect(toasts()).toContain("oops");
    });
    it("applies directly when the hash is unchanged and records the new hash once done", async () => {
      h.rpc.mockImplementation(async (m: string) => {
        if (m === "meta.definition") return { sql: "x", definitionHash: "h1", objectId: 7 };
        if (m === "query.execute") return { executionId: "ex1" };
        return {};
      });
      await A.applyProcedure(id);
      h.rpc.mockImplementation(async (m: string) => (m === "meta.definition" ? { sql: "x", definitionHash: "h2", objectId: 8 } : {}));
      (h.notify.get("query.done") ?? []).forEach((f) => f({ executionId: "other", state: "done" }));
      (h.notify.get("query.done") ?? []).forEach((f) => f({ executionId: "ex1", state: "done" }));
      await tick();
      await tick();
      expect(useTabs.getState().tabs[id].boundObject).toMatchObject({ definitionHash: "h2", objectId: 8 });
      expect(toasts().join()).toMatch(/dbo\.p applied/);
    });
    it("records the new hash when the apply finished before runQuery returned (done event during query.execute)", async () => {
      const off = A.installExecutionListeners(); // the app's own listeners keep early events for the view
      let reads = 0;
      h.rpc.mockImplementation(async (m: string) => {
        if (m === "meta.definition") return reads++ === 0 ? { sql: "x", definitionHash: "h1", objectId: 7 } : { sql: "x", definitionHash: "h2", objectId: 8 };
        if (m === "query.execute") {
          (h.notify.get("query.done") ?? []).forEach((f) => f({ executionId: "ex1", state: "done" }));
          return { executionId: "ex1" };
        }
        return {};
      });
      await A.applyProcedure(id);
      await tick();
      await tick();
      off();
      expect(useTabs.getState().tabs[id].boundObject).toMatchObject({ definitionHash: "h2", objectId: 8 });
      expect(toasts().join()).toMatch(/dbo\.p applied/);
    });
    it("does not update the hash when the execution failed", async () => {
      h.rpc.mockImplementation(async (m: string) => (m === "meta.definition" ? { sql: "x", definitionHash: "h1" } : m === "query.execute" ? { executionId: "ex1" } : {}));
      await A.applyProcedure(id);
      (h.notify.get("query.done") ?? []).forEach((f) => f({ executionId: "ex1", state: "failed" }));
      await tick();
      expect(useTabs.getState().tabs[id].boundObject!.definitionHash).toBe("h1");
    });
    it("shows a diff on conflict and requires Overwrite", async () => {
      h.rpc.mockImplementation(async (m: string) => (m === "meta.definition" ? { sql: "server", definitionHash: "zzz" } : { executionId: "ex1" }));
      let p = A.applyProcedure(id);
      await tick();
      await tick();
      expect(dialogOf("diff")).toMatchObject({ left: "server", right: "alter proc p", okLabel: "Overwrite Server Version" });
      answer("diff", false);
      await p;
      expect(h.rpc.mock.calls.some((c) => c[0] === "query.execute")).toBe(false);
      p = A.applyProcedure(id);
      await tick();
      await tick();
      answer("diff", true);
      await p;
      expect(h.rpc.mock.calls.some((c) => c[0] === "query.execute")).toBe(true);
    });
    it("proceeds when the object has no definition on the server (noDefinition)", async () => {
      h.rpc.mockImplementation(async (m: string) => {
        if (m === "meta.definition") throw new RpcError("none", -1, "noDefinition");
        return { executionId: "ex1" };
      });
      await A.applyProcedure(id);
      expect(h.rpc.mock.calls.some((c) => c[0] === "query.execute")).toBe(true);
    });
    it("compareWithServer opens a diff of server vs tab", async () => {
      h.rpc.mockResolvedValue({ sql: "server" });
      const p = A.compareWithServer(id);
      await tick();
      expect(dialogOf("diff")).toMatchObject({ left: "server", right: "alter proc p" });
      answer("diff", true);
      await p;
    });
  });
});

describe("showPlan", () => {
  it("ignores tabs without a profile", async () => {
    await A.showPlan("zz", "estimated");
    const id = A.newQueryTab();
    await A.showPlan(id, "estimated");
    expect(h.rpc).not.toHaveBeenCalled();
  });
  it("SQL Server actual plan executes the query with actualPlan", async () => {
    h.rpc.mockResolvedValue({ executionId: "e1" });
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.showPlan(id, "actual");
    expect(h.rpc.mock.calls.find((c) => c[0] === "query.execute")![1].options.actualPlan).toBe(true);
    expect(useTabs.getState().tabs[id].bottomTab).toBe("plan");
  });
  const planParams = (mode: string, extra: object = {}) => ({ sessionId: "s1", sql: "select 1", mode, confirmedRun: mode === "actual", confirmedProd: false, confirmedDestructive: false, ...extra });
  it.each([["ms1", "estimated"], ["pg1", "estimated"]] as const)("estimated plan on %s runs as an execution of the tab via plan.get", async (pid, mode) => {
    h.rpc.mockResolvedValue({ executionId: "p1" });
    const off = A.installExecutionListeners();
    const id = A.newQueryTab({ profileId: pid });
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.showPlan(id, mode);
    expect(h.rpc).toHaveBeenCalledWith("plan.get", planParams(mode));
    const t = useTabs.getState().tabs[id];
    expect(t.bottomTab).toBe("plan");
    expect(t.activeExecutionId).toBe("p1");
    expect(t.busy).toBe(true); // Cancel reaches it like any run (query.cancel)
    expect(h.stream).toHaveBeenCalledWith("p1", expect.anything());
    // The plan arrives as query.plan on that execution.
    (h.notify.get("query.plan") ?? []).forEach((f) => f({ executionId: "p1", batchIndex: 0, plan: { statements: [] }, raw: "raw" }));
    expect(useExecutions.getState().views.p1.plans[0].raw).toBe("raw");
    off();
  });
  it("a plan blocked by the sidecar's guard asks, then is sent again with each confirmation", async () => {
    const block = (reason: string) => ({ reason, profileName: "Pg", server: "prod", database: "postgres", environment: "prod", findings: [] });
    const sent: any[] = [];
    h.rpc.mockImplementation(async (_m: string, p: any) => {
      sent.push({ ...p });
      return !p.confirmedProd ? { blocked: block("prodFirstRun") } : !p.confirmedDestructive ? { blocked: block("destructive") } : { executionId: "p2" };
    });
    const id = A.newQueryTab({ profileId: "pg1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    const p = A.showPlan(id, "estimated");
    await vi.waitFor(() => expect(dialogOf("guard")?.block.reason).toBe("prodFirstRun"));
    answer("guard", true);
    await vi.waitFor(() => expect(dialogOf("guard")?.block.reason).toBe("destructive"));
    answer("guard", true);
    await p;
    expect(sent.map((x) => [x.confirmedProd, x.confirmedDestructive])).toEqual([[false, false], [true, false], [true, true]]);
    expect(tabOf(id).activeExecutionId).toBe("p2");
  });
  it("a declined guard sends nothing more and starts no run", async () => {
    h.rpc.mockResolvedValue({ blocked: { reason: "prodFirstRun", profileName: "Pg", server: "prod", findings: [] } });
    const id = A.newQueryTab({ profileId: "pg1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    const p = A.showPlan(id, "estimated");
    await vi.waitFor(() => expect(dialogOf("guard")).toBeTruthy());
    answer("guard", false);
    await p;
    expect(h.rpc).toHaveBeenCalledTimes(1);
    expect(tabOf(id).activeExecutionId).toBeUndefined();
  });
  it("a refused plan (an open DuckDB transaction) shows the reason in Messages", async () => {
    useProfiles.setState({ profiles: [ms, pg, { id: "dk1", name: "Duck", engine: "duckDb", environment: "dev", auth: "none" } as never], groups: [] });
    h.rpc.mockRejectedValue(new RpcError("DuckDB has no savepoints, so an actual plan inside your open transaction…", -32000, "unsupported"));
    const id = A.newQueryTab({ profileId: "dk1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    const p = A.showPlan(id, "actual");
    await tick();
    answer("confirm", true);
    await p;
    const view = useExecutions.getState().views[tabOf(id).activeExecutionId!];
    expect(view.messages[0].message).toContain("no savepoints");
    expect(toasts()).toContain("DuckDB has no savepoints, so an actual plan inside your open transaction…");
  });
  it.each([
    ["pg1", "savepoint"],
    ["dk1", "no savepoints"],
  ] as const)("%s actual plan asks first, with what the sidecar does on that engine", async (pid, says) => {
    useProfiles.setState({ profiles: [ms, pg, { id: "dk1", name: "Duck", engine: "duckDb", environment: "dev", auth: "none" } as never], groups: [] });
    h.rpc.mockResolvedValue({ executionId: "p3" });
    const id = A.newQueryTab({ profileId: pid });
    useTabs.getState().update(id, { sessionId: "s1" });
    let p = A.showPlan(id, "actual");
    await tick();
    expect(dialogOf("confirm").ok).toBe("Run EXPLAIN ANALYZE");
    expect(dialogOf("confirm").body).toContain("rolls it back");
    expect(dialogOf("confirm").body).toContain(says);
    answer("confirm", false);
    await p;
    expect(h.rpc).not.toHaveBeenCalled();
    p = A.showPlan(id, "actual");
    await tick();
    answer("confirm", true);
    await p;
    expect(h.rpc).toHaveBeenCalledWith("plan.get", planParams("actual"));
  });
  it.each([["sqlite", "SQLite"], ["mySql", "MySQL"]] as const)("%s: no actual plans; says so and sends nothing, the estimated plan still works", async (engine, name) => {
    useProfiles.setState({ profiles: [ms, pg, { id: "x1", name: "X", engine, environment: "dev", auth: "none" } as never], groups: [] });
    h.rpc.mockResolvedValue({ executionId: "p4" });
    const id = A.newQueryTab({ profileId: "x1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.showPlan(id, "actual");
    expect(h.rpc).not.toHaveBeenCalled();
    expect(toasts()).toContain(`${name} shows the estimated plan only. Use Explain.`);
    await A.showPlan(id, "estimated");
    expect(h.rpc).toHaveBeenCalledWith("plan.get", planParams("estimated"));
  });
  it("does nothing for empty sql or a busy tab, and toasts errors", async () => {
    const id = A.newQueryTab({ profileId: "pg1" });
    h.exec = { sql: " ", lineOffset: 1, isSelection: false };
    await A.showPlan(id, "estimated");
    expect(h.rpc).not.toHaveBeenCalled();
    h.exec = { sql: "select 1", lineOffset: 1, isSelection: false };
    useTabs.getState().update(id, { sessionId: "s1", busy: true });
    await A.showPlan(id, "estimated");
    expect(h.rpc).not.toHaveBeenCalled();
    expect(toasts()).toContain("A query is already running in this tab.");
    useTabs.getState().update(id, { busy: false });
    h.rpc.mockRejectedValue(new Error("planfail"));
    await A.showPlan(id, "estimated");
    expect(toasts()).toContain("planfail");
  });
});

describe("misc tab actions", () => {
  it("formatTab runs the format action when an editor exists", async () => {
    const run = vi.fn(async () => {});
    h.editor = { getAction: (n: string) => (n === "editor.action.formatDocument" ? { run } : null) };
    await A.formatTab("t");
    expect(run).toHaveBeenCalled();
    h.editor = undefined;
    await A.formatTab("t");
    h.editor = { getAction: () => null };
    await A.formatTab("t");
  });
  it("toggleTrace starts and stops a trace", async () => {
    await A.toggleTrace("zz");
    h.rpc.mockResolvedValue({ traceId: "tr1" });
    const id = A.newQueryTab({ profileId: "ms1" });
    useTabs.getState().update(id, { sessionId: "s1" });
    await A.toggleTrace(id);
    expect(useTabs.getState().tabs[id]).toMatchObject({ traceId: "tr1", bottomTab: "runtime", runtimeTool: "trace", bottomOpen: true });
    await A.toggleTrace(id);
    expect(h.rpc).toHaveBeenCalledWith("trace.stop", { traceId: "tr1" });
    expect(useTabs.getState().tabs[id].traceId).toBeUndefined();
    h.rpc.mockRejectedValue(new Error("tracefail"));
    await A.toggleTrace(id);
    expect(toasts()).toContain("tracefail");
  });
  it("setBottomTab / toggleBottom / activateTab", () => {
    const id = A.newQueryTab();
    A.setBottomTab(id, "messages");
    expect(useTabs.getState().tabs[id]).toMatchObject({ bottomTab: "messages", bottomOpen: true });
    A.toggleBottom(id);
    expect(useTabs.getState().tabs[id].bottomOpen).toBe(false);
    A.toggleBottom("zz");
    const act = vi.fn();
    A.workspaceHooks.activate = act;
    A.activateTab(id);
    expect(act).toHaveBeenCalledWith(id);
  });
  it("toggleMaximize fills the tab with the panel and restores it; hiding a maximized panel restores the editor", () => {
    const id = A.newQueryTab();
    A.toggleBottom(id);
    A.toggleMaximize(id);
    expect(useTabs.getState().tabs[id]).toMatchObject({ bottomMax: true, bottomOpen: true });
    A.toggleMaximize(id);
    expect(useTabs.getState().tabs[id].bottomMax).toBe(false);
    A.toggleMaximize(id);
    A.toggleBottom(id);
    expect(useTabs.getState().tabs[id]).toMatchObject({ bottomMax: false, bottomOpen: false });
    A.toggleMaximize("zz");
  });
  it("showRun / stepRun move between the kept runs and reset the ledger selection and comparison", () => {
    const id = A.newQueryTab();
    useTabs.getState().update(id, { executions: ["r1", "r2", "r3"], activeExecutionId: "r3", resultIndex: 2, ledgerError: 0, compare: { executionId: "r3", resultIndex: 1 } });
    A.showRun(id, "r1");
    expect(useTabs.getState().tabs[id]).toMatchObject({ activeExecutionId: "r1", resultIndex: 0, ledgerError: undefined, compare: undefined });
    A.stepRun(id, 1);
    expect(useTabs.getState().tabs[id].activeExecutionId).toBe("r2");
    A.stepRun(id, 1);
    A.stepRun(id, 1);
    expect(useTabs.getState().tabs[id].activeExecutionId).toBe("r3");
    A.showRun(id, "gone");
    expect(useTabs.getState().tabs[id].activeExecutionId).toBe("r3");
    A.stepRun("zz", -1);
  });
  it("onSidecarRestarted names tabs whose transaction state was unknown as possibly lost (M9)", () => {
    const a = A.newQueryTab({ profileId: "ms1", title: "a.sql" });
    useTabs.getState().update(a, { sessionId: "s1", txState: "unknown" });
    h.rpc.mockResolvedValue({});
    A.onSidecarRestarted();
    expect(useUi.getState().banner?.text).toMatch(/may have been open in a\.sql were lost too/);
  });

  it("onSidecarRestarted resets sessions and shows a banner naming lost transactions", () => {
    const a = A.newQueryTab({ profileId: "ms1", title: "a.sql" });
    const b = A.newQueryTab({ profileId: "pg1", title: "b.sql" });
    useTabs.getState().update(a, { sessionId: "s1", txState: "open", busy: true, traceId: "t" });
    useTabs.getState().update(b, { sessionId: "s2" });
    h.rpc.mockResolvedValue({});
    A.onSidecarRestarted();
    expect(useTabs.getState().tabs[a]).toMatchObject({ sessionId: undefined, txState: "none", busy: false, traceId: undefined });
    expect(useUi.getState().banner?.text).toMatch(/Open transactions in a\.sql were lost/);
    useTabs.getState().update(a, { txState: "none" });
    A.onSidecarRestarted();
    expect(useUi.getState().banner?.text).toMatch(/no work was lost/);
  });

  it("duplicateTab clones a tab with its text and connection", () => {
    const a = A.newQueryTab({ profileId: "ms1", database: "db1", title: "query.sql", text: "select 42" });
    const b = A.duplicateTab(a);
    expect(b).toBeDefined();
    expect(useTabs.getState().tabs[b!]).toMatchObject({ profileId: "ms1", database: "db1", title: "query (copy).sql" });
    expect(h.models.get(b!).getValue()).toBe("select 42");
    expect(A.duplicateTab("unknown")).toBeUndefined();
  });
});

it("duplicates an unbound tab without inheriting another active tab's connection", () => {
  const source = A.newQueryTab({ profileId: "", text: "SELECT 1", title: "scratch.sql" });
  A.newQueryTab({ profileId: "pg1" });
  const copy = A.duplicateTab(source)!;
  expect(useTabs.getState().tabs[copy].profileId).toBeUndefined();
  expect(h.models.get(copy).getValue()).toBe("SELECT 1");
  expect(useTabs.getState().tabs[copy].title).toBe("scratch (copy).sql");
});

describe("notebook tabs in the shell (Phase C3)", () => {
  const hooks = async () => (await import("../../notebook/hooks")).notebookHooks;
  beforeEach(() => {
    useTabs.setState({ tabs: {}, order: [], activeId: undefined });
    useProfiles.setState({ profiles: [ms] });
    useClosedTabs.getState().clear();
  });

  it("Run on a notebook runs its cells, not an editor's text; Cancel stops its queue too", async () => {
    const nb = await hooks();
    nb.run = vi.fn(async () => {});
    nb.cancel = vi.fn();
    h.rpc.mockResolvedValue({});
    useTabs.getState().add({ id: "n1", title: "a.sqlbook", notebook: true, profileId: "ms1", activeExecutionId: "e1" });
    useExecutions.getState().put({ id: "e1", tabId: "n1", sessionId: "s", state: "running", startedAt: 0, resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql: "", lineOffset: 1 });
    expect(await A.runQuery("n1")).toBeUndefined();
    expect(nb.run).toHaveBeenCalledWith("n1");
    expect(h.rpc).not.toHaveBeenCalledWith("query.execute", expect.anything());
    await A.cancelQuery("n1");
    expect(nb.cancel).toHaveBeenCalledWith("n1");
    expect(h.rpc).toHaveBeenCalledWith("query.cancel", { executionId: "e1" });
  });

  it("closing a notebook remembers its document for Ctrl+Shift+T and disposes its cells", async () => {
    const nb = await hooks();
    nb.text = () => "select 1";
    nb.serialize = () => '{"format":"querithm.sqlbook"}';
    nb.dispose = vi.fn();
    useTabs.getState().add({ id: "n1", title: "a.sqlbook", notebook: true, profileId: "ms1" });
    expect(await A.closeTab("n1")).toBe(true);
    expect(nb.dispose).toHaveBeenCalledWith("n1");
    expect(useClosedTabs.getState().list[0]).toMatchObject({ title: "a.sqlbook", notebook: true, sql: '{"format":"querithm.sqlbook"}' });
  });

  it("executeOnTab is the one guarded path: a Prod first run asks, then sends the confirmation", async () => {
    useProfiles.setState({ profiles: [{ ...ms, environment: "prod" }] });
    useTabs.getState().add({ id: "t1", title: "q.sql", profileId: "ms1", sessionId: "s1" });
    h.rpc.mockImplementation(async (m: string, p: any) =>
      m === "query.execute" ? (p.options.confirmedProd ? { executionId: "e9" } : { blocked: { reason: "prodFirstRun", statements: [] } }) : undefined);
    const run = A.executeOnTab("t1", "select 1", 1);
    await vi.waitFor(() => expect(useUi.getState().dialogs.at(-1)?.kind).toBe("guard"));
    answer("guard", true);
    expect(await run).toMatchObject({ sessionId: "s1", executionId: "e9", options: { confirmedProd: true } });
  });
});

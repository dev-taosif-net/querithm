// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  rpc: vi.fn(),
  closeAck: vi.fn(async () => {}),
  closeAnswer: vi.fn(async (_close: boolean) => {}),
  listeners: new Map<string, (p: unknown) => void>(),
  flush: vi.fn(async () => {}),
  resolvePending: vi.fn(async (_id: string) => true),
  pending: vi.fn((_ids: readonly string[]) => [] as unknown[]),
  texts: new Map<string, string>(),
  order: [] as string[],
}));

vi.mock("../../ipc/client", async (orig) => {
  const real = await orig<typeof import("../../ipc/client")>();
  return {
    ...real,
    rpc: h.rpc,
    onNotify: () => () => {},
    shell: { closeAck: h.closeAck, closeAnswer: h.closeAnswer, restart: vi.fn(), resultsDiscard: vi.fn(async () => {}), secretEndConnection: vi.fn(async () => {}) },
  };
});
vi.mock("../../ipc/transport", () => ({
  isTauri: false,
  transport: () => ({
    listen: async (event: string, cb: (p: unknown) => void) => {
      h.listeners.set(event, cb);
      return () => h.listeners.delete(event);
    },
  }),
}));
vi.mock("../../ipc/frames", () => ({
  streamExecution: vi.fn(), stopStream: () => false, isStreaming: () => false, stopAllStreams: () => [], ResultsDroppedError: class extends Error {},
}));
vi.mock("../../editor/monaco", () => ({ monaco: { editor: { setModelMarkers: vi.fn() }, MarkerSeverity: { Error: 8 } } }));
vi.mock("../../editor/completion", () => ({ undeclaredParameters: () => [], unsetUserVariables: () => [], statementBoundsAt: () => ({ start: 0, end: 0 }), statementTitle: () => undefined }));
vi.mock("../../editor/editors", () => ({
  languageFor: () => "tsql", createModel: vi.fn(), getModel: () => undefined, setModelLanguage: vi.fn(), disposeModel: vi.fn(), editorForTab: () => undefined,
  executableText: () => ({ sql: "", lineOffset: 1, isSelection: false }),
}));
vi.mock("../../results/gridEdit", () => ({ resolvePendingEdits: (id: string) => h.resolvePending(id) }));
vi.mock("../../results/staging", () => ({ pendingIn: (ids: readonly string[]) => h.pending(ids), dropStagings: vi.fn() }));
vi.mock("../persistence", () => ({ flushAutosave: () => h.flush() }));
vi.mock("../tabActions", () => ({ allTabsOrder: () => h.order, textOf: (id: string) => h.texts.get(id) ?? "" }));

import { useTabs, type TabState } from "../../store/tabs";
import { useUi, type Dialog } from "../../store/ui";
import { useSettings } from "../../store/settings";
import { useEngineStatus } from "../../store/engine";
import { useUpdates } from "../../store/updates";
import { CLOSE_REQUESTED, INSTALLING_BODY, installQuitGuard, onCloseRequested } from "../quit";
import { CLOSE_CANCEL_WAIT_MS } from "../actions";

function addTab(t: Partial<TabState> & { id: string }) {
  const { id, title, ...rest } = t;
  useTabs.getState().add({ id, title: title ?? `${id}.sql` });
  useTabs.getState().update(id, rest);
  h.order.push(id);
}

/** The dialog the quit is waiting on (resolved by the test). */
async function dialog<K extends Dialog["kind"]>(kind: K): Promise<Extract<Dialog, { kind: K }>> {
  await vi.waitFor(() => expect(useUi.getState().dialogs.some((d) => d.kind === kind)).toBe(true));
  return useUi.getState().dialogs.find((d) => d.kind === kind) as Extract<Dialog, { kind: K }>;
}

function answer<K extends Dialog["kind"]>(d: Extract<Dialog, { kind: K }>, value: unknown) {
  useUi.getState().closeDialog(d);
  (d as unknown as { resolve: (v: unknown) => void }).resolve(value);
}

beforeEach(() => {
  for (const f of [h.closeAck, h.closeAnswer, h.flush, h.resolvePending, h.pending]) f.mockClear();
  h.rpc.mockReset();
  h.rpc.mockImplementation(async () => undefined);
  h.resolvePending.mockImplementation(async () => true);
  h.pending.mockImplementation(() => []);
  h.texts.clear();
  h.order.length = 0;
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useUi.setState({ dialogs: [], toasts: [] });
  useSettings.setState({ autosave: true } as never);
  useEngineStatus.setState({ state: "running" });
  useUpdates.setState({ phase: "idle" });
});

describe("closing the window (H4)", () => {
  it("listens for the shell's close request and acknowledges it at once", async () => {
    const off = installQuitGuard();
    await vi.waitFor(() => expect(h.listeners.has(CLOSE_REQUESTED)).toBe(true));
    h.listeners.get(CLOSE_REQUESTED)!(null);
    await vi.waitFor(() => expect(h.closeAnswer).toHaveBeenCalledWith(true));
    expect(h.closeAck).toHaveBeenCalled();
    off();
  });

  it("with nothing pending it writes what is waiting and closes without asking", async () => {
    addTab({ id: "a" });
    h.texts.set("a", "select 1"); // autosaved: the text comes back next launch
    await onCloseRequested();
    expect(useUi.getState().dialogs).toHaveLength(0);
    expect(h.flush).toHaveBeenCalledTimes(1);
    expect(h.closeAnswer).toHaveBeenCalledWith(true);
    // The pending autosaves are written before the window is told to close.
    expect(h.flush.mock.invocationCallOrder[0]).toBeLessThan(h.closeAnswer.mock.invocationCallOrder[0]);
  });

  it("an open transaction asks Commit / Roll back / Cancel; Cancel keeps Querithm open and writes nothing", async () => {
    addTab({ id: "a", sessionId: "s1", txState: "open" });
    const quit = onCloseRequested();
    const d = await dialog("txClose");
    expect(d).toMatchObject({ tabId: "a", quit: true });
    answer(d, "cancel");
    await quit;
    expect(h.closeAnswer).toHaveBeenCalledWith(false);
    expect(h.flush).not.toHaveBeenCalled();
    expect(h.rpc).not.toHaveBeenCalledWith("tx.rollback", expect.anything());
  });

  it("rolling back the open transaction lets the window close", async () => {
    addTab({ id: "a", sessionId: "s1", txState: "open" });
    h.rpc.mockImplementation(async (m: string) => (m === "tx.rollback" ? { txState: "none" } : undefined));
    const quit = onCloseRequested();
    answer(await dialog("txClose"), "rollback");
    await quit;
    expect(h.rpc).toHaveBeenCalledWith("tx.rollback", { sessionId: "s1" });
    expect(h.closeAnswer).toHaveBeenCalledWith(true);
  });

  it("a possibly open (unknown) transaction is asked about like an open one", async () => {
    addTab({ id: "a", sessionId: "s1", txState: "unknown" });
    const quit = onCloseRequested();
    answer(await dialog("txClose"), "cancel");
    await quit;
    expect(h.closeAnswer).toHaveBeenCalledWith(false);
  });

  it("a rollback that leaves the state unknown offers Quit anyway instead of a dead end (W1)", async () => {
    addTab({ id: "a", sessionId: "s1", txState: "open" });
    h.rpc.mockImplementation(async (m: string) => (m === "tx.rollback" ? { txState: "unknown" } : undefined));
    const quit = onCloseRequested();
    answer(await dialog("txClose"), "rollback");
    const d = await dialog("confirm");
    expect(d).toMatchObject({ title: "Quit anyway?", ok: "Quit anyway (the server rolls back)", danger: true });
    expect(d.body).toBe("ROLLBACK did not end the transaction. Quitting closes the connection, and the server rolls back whatever was not committed.");
    answer(d, true);
    await quit;
    expect(h.closeAnswer).toHaveBeenCalledWith(true);
  });

  it("a rollback that fails says why and offers Quit anyway; declining keeps Querithm open, accepting quits (W1)", async () => {
    addTab({ id: "a", sessionId: "s1", txState: "open" });
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "tx.rollback") throw new Error("The connection was lost during ROLLBACK.");
    });
    let quit = onCloseRequested();
    answer(await dialog("txClose"), "rollback");
    let d = await dialog("confirm");
    expect(d.body).toContain("ROLLBACK did not end the transaction: The connection was lost during ROLLBACK.");
    answer(d, false);
    await quit;
    expect(h.closeAnswer).toHaveBeenLastCalledWith(false);
    expect(h.flush).not.toHaveBeenCalled();
    // The next X asks again, and Quit anyway gets out (before W1 only killing the process did).
    quit = onCloseRequested();
    answer(await dialog("txClose"), "commit");
    d = await dialog("confirm");
    expect(d.body).toMatch(/^COMMIT did not end the transaction/);
    answer(d, true);
    await quit;
    expect(h.closeAnswer).toHaveBeenLastCalledWith(true);
  });

  it.each(["failed", "dead"] as const)("with the engine %s, a transaction it took along is not asked about (W1)", async (state) => {
    useEngineStatus.setState({ state });
    addTab({ id: "a", sessionId: "s1", txState: "open" });
    addTab({ id: "b", sessionId: "s2", txState: "unknown" });
    await onCloseRequested();
    expect(useUi.getState().dialogs).toHaveLength(0);
    expect(h.rpc).not.toHaveBeenCalledWith("tx.rollback", expect.anything());
    expect(h.closeAnswer).toHaveBeenCalledWith(true);
  });

  it("a query that does not stop names the staged grid edits quitting would discard (W4)", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      addTab({ id: "a", sessionId: "s1", busy: true, executions: ["e1"] });
      h.pending.mockImplementation((ids) => (ids.includes("e1") ? [{ counts: { updates: 2, inserts: 1, deletes: 0 } }] : []));
      let quit = onCloseRequested();
      answer(await dialog("busyClose"), true);
      await vi.advanceTimersByTimeAsync(CLOSE_CANCEL_WAIT_MS);
      let d = await dialog("confirm");
      expect(d.title).toBe("Quit and discard the staged grid edits?");
      expect(d.body).toContain("(2 updated, 1 new and 0 deleted row(s)) cannot be applied now");
      answer(d, false);
      await quit;
      expect(h.closeAnswer).toHaveBeenLastCalledWith(false);
      quit = onCloseRequested();
      answer(await dialog("busyClose"), true);
      await vi.advanceTimersByTimeAsync(CLOSE_CANCEL_WAIT_MS);
      d = await dialog("confirm");
      answer(d, true);
      await quit;
      expect(h.closeAnswer).toHaveBeenLastCalledWith(true);
      expect(h.resolvePending).not.toHaveBeenCalled(); // Apply cannot run while the query holds the session
    } finally {
      vi.useRealTimers();
    }
  });

  it("closing while an update installs asks first: Keep waiting keeps Querithm open, Quit anyway goes on (R1-8)", async () => {
    useUpdates.setState({ phase: "installing" });
    let quit = onCloseRequested();
    let d = await dialog("confirm");
    expect(d).toMatchObject({ title: "An update is being installed", ok: "Quit anyway", body: INSTALLING_BODY });
    answer(d, false);
    await quit;
    expect(h.closeAnswer).toHaveBeenLastCalledWith(false);
    expect(h.flush).not.toHaveBeenCalled();
    quit = onCloseRequested();
    d = await dialog("confirm");
    answer(d, true);
    await quit;
    expect(h.closeAnswer).toHaveBeenLastCalledWith(true);
  });

  it("staged grid edits go through the tab-close prompt (Apply / Discard / Cancel)", async () => {
    addTab({ id: "a", executions: ["e1"] });
    h.pending.mockImplementation((ids) => (ids.includes("e1") ? [{}] : []));
    h.resolvePending.mockImplementation(async () => false);
    await onCloseRequested();
    expect(h.resolvePending).toHaveBeenCalledWith("a");
    expect(h.closeAnswer).toHaveBeenCalledWith(false);
  });

  it("asked again while no dialog is showing (a query being cancelled), it closes at once", async () => {
    addTab({ id: "a", sessionId: "s1", busy: true });
    const first = onCloseRequested();
    answer(await dialog("busyClose"), true); // cancel the query and quit: now waiting for it to stop
    await vi.waitFor(() => expect(useUi.getState().dialogs).toHaveLength(0));
    await onCloseRequested();
    expect(h.closeAnswer).toHaveBeenCalledWith(true);
    useTabs.getState().update("a", { busy: false });
    await first;
  });

  it("asked again while its dialog is showing, the dialog stays and nothing closes", async () => {
    addTab({ id: "a", sessionId: "s1", txState: "open" });
    const first = onCloseRequested();
    const d = await dialog("txClose");
    await onCloseRequested();
    expect(h.closeAck).toHaveBeenCalledTimes(2);
    expect(h.closeAnswer).not.toHaveBeenCalled();
    answer(d, "cancel");
    await first;
    expect(h.closeAnswer).toHaveBeenCalledWith(false);
  });

  it("a close check that throws still closes the window", async () => {
    addTab({ id: "a", executions: ["e1"] });
    h.pending.mockImplementation(() => [{}]);
    h.resolvePending.mockImplementation(async () => {
      throw new Error("boom");
    });
    vi.spyOn(console, "error").mockImplementation(() => {});
    await onCloseRequested();
    expect(h.closeAnswer).toHaveBeenCalledWith(true);
  });

  it("with autosave off, SQL that exists only in a tab is confirmed before it is lost", async () => {
    useSettings.setState({ autosave: false } as never);
    addTab({ id: "a" });
    h.texts.set("a", "select 1");
    const quit = onCloseRequested();
    const d = await dialog("confirm");
    expect(d.body).toContain("autosave is off");
    answer(d, false);
    await quit;
    expect(h.closeAnswer).toHaveBeenCalledWith(false);
  });

  it("a file changed since it was saved is confirmed even with autosave on", async () => {
    addTab({ id: "a", filePath: "/x.sql" });
    useTabs.getState().update("a", { dirty: true });
    h.texts.set("a", "select 2");
    const quit = onCloseRequested();
    const d = await dialog("confirm");
    expect(d.body).toContain("not saved to disk");
    answer(d, true);
    await quit;
    expect(h.closeAnswer).toHaveBeenCalledWith(true);
  });
});

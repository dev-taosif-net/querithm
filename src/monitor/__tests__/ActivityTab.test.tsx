// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { forwardRef, useImperativeHandle } from "react";
import type { ActivityRow, MonitorSnapshot, KillPreview } from "../../ipc/contract";

const { rpc, handlers, gridProps, updateCells } = vi.hoisted(() => ({
  rpc: vi.fn(), handlers: new Map<string, (p: unknown) => void>(), gridProps: { current: null as any }, updateCells: vi.fn(),
}));

vi.mock("@glideapps/glide-data-grid", async (orig) => {
  const actual = await orig<typeof import("@glideapps/glide-data-grid")>();
  return {
    ...actual,
    DataEditor: forwardRef(function FakeEditor(props: unknown, ref) {
      gridProps.current = props;
      useImperativeHandle(ref, () => ({ updateCells }) as never);
      return <canvas data-testid="glide" />;
    }),
  };
});
vi.mock("@glideapps/glide-data-grid/dist/index.css", () => ({}));
vi.mock("../../ipc/client", () => ({
  rpc,
  onNotify: (m: string, h: (p: unknown) => void) => { handlers.set(m, h); return () => handlers.delete(m); },
  onSidecarStatus: () => () => {},
}));
vi.mock("../../plans/PlanView", () => ({ PlanDiagram: () => <div data-testid="plan" /> }));
vi.mock("../../app/actions", () => ({ newQueryTab: vi.fn(), txAction: vi.fn(), workspaceHooks: { activate: vi.fn() }, activeTabState: () => undefined }));
vi.mock("../../app/toolActions", () => ({ openTool: vi.fn() }));

import { ActivityTab, activityTabRenders } from "../ActivityTab";
import { KillDialog } from "../KillDialog";
import { BlockingTree } from "../BlockingTree";
import { BlockingBanner } from "../BlockingBanner";
import { SidePanel } from "../SidePanel";
import { DeadlockGraph, DeadlocksPane } from "../DeadlocksPane";
import { MonitorModel } from "../model";
import { useUi } from "../../store/ui";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";
import { useBlocking, MUTE_MS } from "../monitorActions";
import { useTabs } from "../../store/tabs";
import { txAction } from "../../app/actions";

const row = (id: number, o: Partial<ActivityRow> = {}): ActivityRow => ({ key: { id, instance: `i${id}` }, status: "running", blockedBy: [], isUserSession: true, rollingBack: false, ...o });
let seq = 0;
const snap = (sessions: ActivityRow[] | null, o: Partial<MonitorSnapshot> = {}): MonitorSnapshot => ({
  monitorId: "mon1", at: Date.now(), seq: ++seq, sessions, status: [{ pane: "sessions", state: "ok" }], lagMs: 0, connectionCount: 1, throttled: false, paused: false, connection: "connected", ...o,
});
const push = (s: MonitorSnapshot) => act(() => handlers.get("monitor.snapshot")?.(s));
// The tab is on screen and subscribed: a push before monitor.start resolved (slow runners) would be lost.
const ready = async () => {
  await screen.findByTestId("activity-tab");
  await vi.waitFor(() => expect(handlers.has("monitor.snapshot")).toBe(true));
};

beforeEach(() => {
  seq = 0;
  rpc.mockReset();
  updateCells.mockReset();
  rpc.mockImplementation(async (method: string) => {
    if (method === "monitor.start") return { monitorId: "mon1", intervalMs: 2000, panes: [], engine: "mssql" };
    if (method === "monitor.texts") return { texts: {}, missing: [] };
    return null;
  });
  useProfiles.setState({ profiles: [{ id: "p1", name: "Prod Billing", engine: "mssql", environment: "prod" } as never] });
  useConnections.setState({ servers: { p1: { status: "connected" } as never } } as never);
});
afterEach(cleanup);

const tool = { id: "t1", kind: "activity" as const, title: "Activity", profileId: "p1", params: {} };

describe("ActivityTab", () => {
  it("U1: snapshots never re-render the tab; the grid gets damaged cells only", async () => {
    render(<ActivityTab tool={tool} />);
    await ready();
    push(snap([row(1, { cpuMs: 1 }), row(2, { cpuMs: 2 })]));
    await screen.findByTestId("glide");
    const renders = activityTabRenders.count;
    for (let i = 0; i < 20; i++) push(snap([row(1, { cpuMs: 1 }), row(2, { cpuMs: 2 + i })]));
    expect(activityTabRenders.count).toBe(renders);
    // Row count unchanged: damage only, and only the cpu cell of row 2.
    await vi.waitFor(() => expect(updateCells).toHaveBeenCalled());
    const last = updateCells.mock.calls.at(-1)![0] as { cell: [number, number] }[];
    expect(last).toEqual([{ cell: [7, 1] }]);
  });

  it("U9: each pane state renders its text from PaneStatus", async () => {
    render(<ActivityTab tool={tool} />);
    await ready();
    expect(screen.getByText("collecting…")).toBeTruthy(); // loading: no snapshot yet
    push(snap(null, { connection: "reconnecting", connectionError: "connection lost", retryInMs: 4000, status: [{ pane: "sessions", state: "error", reason: "connection lost" }] }));
    expect(document.querySelector('[data-pane-state="disconnected"]')?.textContent).toContain("connection lost");
    expect(document.querySelector('[data-pane-state="disconnected"]')?.textContent).toContain("reconnecting in 4 s");
    push(snap([row(1)], { status: [{ pane: "sessions", state: "truncated", total: 1500 }], paused: true }));
    expect(document.querySelector("[data-pane-notice]")?.textContent).toContain("Showing the first 1,000 of 1,500, by priority");
    expect(document.querySelector("[data-pane-notice]")?.textContent).toContain("Paused at");
    fireEvent.click(screen.getByRole("tab", { name: "Locks" }));
    push(snap([row(1)], { status: [{ pane: "locks", state: "degraded", reason: "requires VIEW SERVER STATE" }] }));
    expect(document.querySelector('[data-pane-state="degraded"]')?.textContent).toContain("requires VIEW SERVER STATE");
    fireEvent.click(screen.getByRole("tab", { name: "Waits" }));
    push(snap([row(1)], { status: [{ pane: "waits", state: "error", reason: "The monitor query timed out (5 s)", retryInMs: 2000 }] }));
    expect(document.querySelector('[data-pane-state="error"]')?.textContent).toContain("retrying in 2 s");
    fireEvent.click(screen.getByRole("tab", { name: "Blocking" }));
    push(snap([row(1)], { blocking: { headBlockers: [], cycles: [], placeholders: [], blockedCount: 0 } }));
    expect(screen.getByText("No blocking right now")).toBeTruthy();
    act(() => handlers.get("monitor.stopped")?.({ monitorId: "mon1", reason: "The server was disconnected." }));
    expect(document.querySelector('[data-pane-state="stopped"]')?.textContent).toContain("The server was disconnected.");
  });

  it("U8: a Querithm connection row offers Go to tab, never kill or cancel", async () => {
    useTabs.setState({ tabs: { q1: { id: "q1", title: "Orders.sql", sessionId: "s1" } as never } } as never);
    render(<ActivityTab tool={tool} />);
    await ready();
    push(snap([row(60, { querithmKind: "pool", ownSessionId: "s1" })]));
    act(() => gridProps.current.onGridSelectionChange({ rows: { first: () => 0 }, current: { cell: [0, 0] } }));
    const go = await screen.findByText("Go to tab");
    expect(screen.queryByTestId("btn-kill")).toBeNull();
    expect(go.getAttribute("title")).toContain("Querithm connection");
  });
});

describe("KillDialog", () => {
  const model = () => {
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    m.texts.put("t".repeat(32), "UPDATE orders SET paid = 1");
    return m;
  };
  const preview = (o: Partial<KillPreview> = {}): KillPreview => ({ key: { id: 77, instance: "i77" }, risk: "high", logBytesUsed: 200 * 1024 * 1024, txElapsedMs: 15 * 60_000, blockedCount: 3, rollingBack: false, sessionEnded: false, ...o });

  it("U3, P9: shows who and what, needs the second confirm, typed confirmation on Prod and the rollback checkbox; never a time estimate", async () => {
    rpc.mockImplementation(async (m: string) => (m === "monitor.killPreview" ? preview() : { outcome: "done", message: "ok" }));
    const r = row(77, { login: "app", host: "web01", program: "billing", textId: "t".repeat(32), txElapsedMs: 900_000 });
    render(<KillDialog model={model()} row={r} mode="terminateSession" prod onClose={() => {}} />);
    await screen.findByText(/high — the rollback may take long/);
    for (const v of ["app", "web01", "billing", "15 min 0 s"]) expect(screen.getAllByText(v).length).toBeGreaterThan(0);
    expect(screen.getByTestId("kill-text").textContent).toBe("UPDATE orders SET paid = 1");
    const submit = screen.getByTestId("kill-submit") as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    fireEvent.click(screen.getByTestId("kill-confirm"));
    expect(submit.disabled).toBe(true); // rollback checkbox and typed id still missing
    fireEvent.click(screen.getByTestId("kill-rollback"));
    expect(submit.disabled).toBe(true);
    fireEvent.change(screen.getByTestId("kill-typed"), { target: { value: "78" } });
    expect(submit.disabled).toBe(true);
    fireEvent.change(screen.getByTestId("kill-typed"), { target: { value: "77" } });
    expect(submit.disabled).toBe(false);
    expect(document.body.textContent).not.toMatch(/remaining|estimated time|seconds left/i); // P9
    await act(async () => fireEvent.click(submit));
    expect(rpc).toHaveBeenCalledWith("monitor.kill", expect.objectContaining({ confirmedKill: true, confirmedProd: true, confirmedLongRollback: true, mode: "terminateSession" }));
  });

  it("Enter never sends a kill, wherever the focus is (Close stays Close)", async () => {
    rpc.mockImplementation(async (m: string) => (m === "monitor.killPreview" ? preview({ risk: "none", txElapsedMs: null, logBytesUsed: null }) : { outcome: "done", message: "ok" }));
    const onClose = vi.fn();
    render(<KillDialog model={model()} row={row(77)} mode="terminateSession" prod={false} onClose={onClose} />);
    await screen.findByText(/none — no open transaction/);
    const confirm = screen.getByTestId("kill-confirm");
    fireEvent.click(confirm);
    expect((screen.getByTestId("kill-submit") as HTMLButtonElement).disabled).toBe(false); // ready: only Enter is under test
    fireEvent.keyDown(confirm, { key: "Enter" });
    const close = screen.getByText("Close");
    close.focus();
    fireEvent.keyDown(close, { key: "Enter" });
    await act(async () => {});
    expect(rpc.mock.calls.filter(([m]) => m === "monitor.kill")).toHaveLength(0);
  });

  it("S4: the statement text is shown escaped (bidi override, control character), line breaks kept", async () => {
    rpc.mockImplementation(async (m: string) => (m === "monitor.killPreview" ? preview({ risk: "none" }) : null));
    const m = model();
    m.texts.put("e".repeat(32), "UPDATE t SET v = 1 ‮--x\u0007\r\nWHERE id = 2");
    render(<KillDialog model={m} row={row(77, { textId: "e".repeat(32) })} mode="terminateSession" prod={false} onClose={() => {}} />);
    expect(screen.getByTestId("kill-text").textContent).toBe("UPDATE t SET v = 1 \\u202e--x\\x07\nWHERE id = 2");
    await screen.findByText(/none — no open transaction/);
  });

  it("S4: status, refusal and preview error are shown escaped at the decision point", async () => {
    const bs = String.fromCharCode(92);
    rpc.mockImplementation(async (m: string) => (m === "monitor.killPreview" ? preview({ risk: "none", txElapsedMs: null, logBytesUsed: null }) : { outcome: "refused", code: "serverRefused", message: "permission denied for \u202Etxt.exe\u200B" }));
    render(<KillDialog model={model()} row={row(77, { status: "running\u202E" })} mode="terminateSession" prod={false} onClose={() => {}} />);
    await screen.findByText(/none — no open transaction/);
    expect(screen.getByTestId("kill-fact-Session").textContent).toBe(`77 · running${bs}u202e`);
    fireEvent.click(screen.getByTestId("kill-confirm"));
    await act(async () => fireEvent.click(screen.getByTestId("kill-submit")));
    expect(screen.getByTestId("kill-refusal").textContent).toBe(`permission denied for ${bs}u202etxt.exe${bs}u200b`);
    cleanup();
    rpc.mockImplementation(async (m: string) => { if (m === "monitor.killPreview") throw new Error("preview failed \u202Eevil"); return null; });
    render(<KillDialog model={model()} row={row(77)} mode="terminateSession" prod={false} onClose={() => {}} />);
    await screen.findByText(/preview failed/);
    expect(screen.getByTestId("kill-fact-Rollback risk").textContent).toBe(`unknown (preview failed ${bs}u202eevil)`);
  });

  it("shows the sidecar's refusal and stays open", async () => {
    rpc.mockImplementation(async (m: string) => (m === "monitor.killPreview" ? preview({ risk: "none", txElapsedMs: null, logBytesUsed: null }) : { outcome: "refused", code: "notInSnapshot", message: "The session ended or its ID was reused; nothing was sent." }));
    render(<KillDialog model={model()} row={row(77)} mode="terminateSession" prod={false} onClose={() => {}} />);
    await screen.findByText(/none — no open transaction/);
    fireEvent.click(screen.getByTestId("kill-confirm"));
    await act(async () => fireEvent.click(screen.getByTestId("kill-submit")));
    expect(screen.getByTestId("kill-refusal").textContent).toContain("ID was reused");
  });
});

describe("BlockingTree keyboard (U7)", () => {
  it("navigates, expands, opens detail with Enter and kill with Delete", () => {
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    m.caps = { serverVersion: "x", panes: [], allSessionsVisible: true, canTerminate: true, canCancelRequest: false, livePlans: false };
    m.connection = "connected";
    m.apply(snap([row(1, { status: "sleeping", openTx: 1 }), row(2, { blockedBy: [1], waitMs: 40_000 })], { blocking: { headBlockers: [1], cycles: [], placeholders: [], blockedCount: 1 } }));
    const detail = vi.fn(), kill = vi.fn();
    render(<BlockingTree model={m} actions={{ detail, diagnose: vi.fn(), kill }} />);
    const tree = screen.getByTestId("blocking-tree");
    expect(screen.getAllByRole("treeitem")).toHaveLength(2);
    fireEvent.keyDown(tree, { key: "ArrowLeft" });
    expect(screen.getAllByRole("treeitem")).toHaveLength(1); // collapsed
    fireEvent.keyDown(tree, { key: "ArrowRight" });
    fireEvent.keyDown(tree, { key: "ArrowDown" });
    fireEvent.keyDown(tree, { key: "Enter" });
    expect(detail).toHaveBeenCalledWith(expect.objectContaining({ key: { id: 2, instance: "i2" } }));
    fireEvent.keyDown(tree, { key: "Delete" });
    expect(kill).toHaveBeenCalledWith(expect.objectContaining({ key: { id: 2, instance: "i2" } }), "terminateSession");
  });

  it("a repeated occurrence says where its blocked sessions are shown and cannot be expanded there", () => {
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    // PostgreSQL hot row: 2 waits on 1; 3 and 4 wait on 2; 4 also waits on 3 (soft block), and 5 on 2, 3 and 4.
    m.apply(snap([row(1, { status: "idle in transaction" }), row(2, { blockedBy: [1], waitMs: 9 }), row(3, { blockedBy: [2], waitMs: 8 }),
      row(4, { blockedBy: [2, 3], waitMs: 7 }), row(5, { blockedBy: [2, 3, 4], waitMs: 6 })], { blocking: { headBlockers: [1], cycles: [], placeholders: [], blockedCount: 4 } }));
    render(<BlockingTree model={m} actions={{ detail: vi.fn(), diagnose: vi.fn(), kill: vi.fn() }} />);
    const marker = screen.getAllByTestId("tree-elsewhere")[0];
    expect(marker.textContent).toBe("blocked sessions shown under session 2");
    expect(marker.closest("[role=treeitem]")?.querySelector('button[aria-label="Expand"], button[aria-label="Collapse"]')).toBeNull();
  });
});

describe("Diagnosis rendering (W7)", () => {
  it("renders identifiers with markup and control characters as text", async () => {
    rpc.mockImplementation(async (m: string) => (m === "monitor.diagnose"
      ? { key: { id: 1, instance: "i1" }, findings: [{ rule: "blocked", severity: "high", message: "Blocked by session 5 <img src=x onerror=alert(1)>", evidence: ["blocker application <script>x</script>"] }], evidence: [] }
      : { key: { id: 1, instance: "i1" }, planReason: "none" }));
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    render(<SidePanel model={m} view={{ kind: "diagnose", row: row(1) }} engine="mssql" profileId="p1" onClose={() => {}} />);
    const d = await screen.findByTestId("diagnosis");
    expect(d.querySelector("img")).toBeNull();
    expect(d.querySelector("script")).toBeNull();
    expect(d.textContent).toContain("<img src=x onerror=alert(1)>");
  });
});

describe("SidePanel (U2)", () => {
  it("says the session has ended once its exact key leaves the snapshot", async () => {
    rpc.mockImplementation(async () => ({ key: { id: 1, instance: "i1" }, findings: [], evidence: [], planReason: "none" }));
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    act(() => { m.apply(snap([row(1), row(2)])); });
    render(<SidePanel model={m} view={{ kind: "diagnose", row: row(1) }} engine="mssql" profileId="p1" onClose={() => {}} />);
    expect(screen.queryByTestId("side-ended")).toBeNull();
    act(() => { m.apply(snap([row(1, { key: { id: 1, instance: "i1-reused" } }), row(2)])); }); // same id, new session
    expect(screen.getByTestId("side-ended").textContent).toContain("Session 1 has ended");
  });
});

describe("BlockingBanner (O8)", () => {
  it("Commit and Rollback run the tab’s own transaction commands, the same ones as the toolbar", () => {
    const tab = { id: "q1", sessionId: "s1", txState: "open", profileId: "p1" } as never;
    useBlocking.setState({ bySession: {}, muted: {} });
    render(<BlockingBanner tab={tab} />);
    act(() => useBlocking.getState().set({ sessionId: "s1", blockedCount: 2, maxWaitMs: 9_000, source: "check" }));
    vi.mocked(txAction).mockClear();
    fireEvent.click(screen.getByText("Rollback"));
    fireEvent.click(screen.getByText("Commit"));
    expect(vi.mocked(txAction).mock.calls).toEqual([["q1", "rollback"], ["q1", "commit"]]);
  });
});

const deadlock = (id: string, at: number, text = "UPDATE dbo.t SET v = 1") => ({
  id, at, database: "SalesDB", partial: false, source: "system_health", victims: [{ processId: "p0" }],
  processes: [{ id: "p0", spid: 51, victim: true, login: "app", text }, { id: "p1", spid: 52, victim: false, login: "app", text: "SELECT 1" }],
  resources: [{ id: "r0", kind: "keylock", object: "SalesDB.dbo.t", owners: [{ processId: "p0", mode: "X", role: "owner" }, { processId: "p1", mode: "S", role: "waiter" }] }],
});
const withDeadlocks = (o: Partial<MonitorSnapshot> = {}) =>
  snap([row(1)], { status: [{ pane: "sessions", state: "ok" }, { pane: "deadlocks", state: "ok" }], ...o });

describe("Deadlocks pane (B7)", () => {
  it("reads the full history once per monitor: switching panes away and back sends no second read; Refresh does", async () => {
    rpc.mockImplementation(async (method: string) => {
      if (method === "monitor.start") return { monitorId: "mon1", intervalMs: 2000, panes: [], engine: "mssql" };
      if (method === "monitor.deadlocks") return { events: [deadlock("d1", 1_000)], status: { pane: "deadlocks", state: "ok" }, truncated: false };
      return { texts: {}, missing: [] };
    });
    render(<ActivityTab tool={tool} />);
    await ready();
    push(withDeadlocks());
    const reads = () => rpc.mock.calls.filter(([m]) => m === "monitor.deadlocks").length;
    fireEvent.click(screen.getByRole("tab", { name: "Deadlocks" }));
    await screen.findByTestId("deadlock-graph");
    fireEvent.click(screen.getByRole("tab", { name: "Sessions" }));
    fireEvent.click(screen.getByRole("tab", { name: "Deadlocks" }));
    await screen.findByTestId("deadlock-graph");
    expect(reads()).toBe(1);
    // Deadlocks that arrive with snapshots meanwhile are merged into the kept list.
    push(withDeadlocks({ newDeadlocks: [deadlock("d2", 2_000)] }));
    expect(screen.getByTestId("deadlock-list").querySelectorAll("button")).toHaveLength(2);
    await act(async () => fireEvent.click(screen.getByText("Refresh")));
    expect(reads()).toBe(2);
  });

  it("X13: more than 200 deadlocks says the newest 200 are shown, not the sessions pane's wording", async () => {
    rpc.mockImplementation(async (method: string) =>
      method === "monitor.deadlocks" ? { events: [deadlock("d1", 1_000)], status: { pane: "deadlocks", state: "truncated", total: 213 }, truncated: true } : null);
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    act(() => { m.apply(withDeadlocks({ status: [{ pane: "sessions", state: "ok" }, { pane: "deadlocks", state: "truncated", total: 213 }] })); });
    render(<DeadlocksPane model={m} profileId="p1" />);
    await screen.findByTestId("deadlock-graph");
    const notice = document.querySelector("[data-pane-notice]")?.textContent;
    expect(notice).toContain("Showing the newest 200 of 213 deadlocks.");
    expect(notice).not.toContain("1,000");
  });

  it("S4: a process's statement text is shown escaped in its card", async () => {
    rpc.mockImplementation(async (method: string) =>
      method === "monitor.deadlocks" ? { events: [deadlock("d1", 1_000, "UPDATE t ‮evil\u0007\nSET v = 1")], status: { pane: "deadlocks", state: "ok" }, truncated: false } : null);
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    act(() => { m.apply(withDeadlocks()); });
    render(<DeadlocksPane model={m} profileId="p1" />);
    await screen.findByTestId("deadlock-graph");
    const pre = [...document.querySelectorAll("pre")].find((p) => p.textContent?.startsWith("UPDATE t"));
    expect(pre?.textContent).toBe("UPDATE t \\u202eevil\\x07\nSET v = 1");
  });

  it("X16: a toast for deadlocks after the first read only, whatever the server clock says (10 min ahead or behind)", async () => {
    useUi.setState({ toasts: [] });
    render(<ActivityTab tool={tool} />);
    await ready();
    const toasts = () => useUi.getState().toasts.filter((t) => t.text.startsWith("Deadlock in")).length;
    push(withDeadlocks({ newDeadlocks: [deadlock("old", Date.now() + 10 * 60_000)] })); // the first read: the last 24 h
    expect(toasts()).toBe(0);
    push(withDeadlocks({ newDeadlocks: [deadlock("new", Date.now() - 10 * 60_000)] })); // a later poll: news
    expect(toasts()).toBe(1);
    push(withDeadlocks({ newDeadlocks: [{ ...deadlock("named", 0), database: "Sales\u202EBD" }] }));
    expect(useUi.getState().toasts.at(-1)?.text).toBe(`Deadlock in Sales${String.fromCharCode(92)}u202eBD — see the Deadlocks pane.`);
  });

  it("a history read that failed shows its reason, not an empty list", async () => {
    rpc.mockImplementation(async (method: string) =>
      method === "monitor.deadlocks" ? { events: [], status: { pane: "deadlocks", state: "error", reason: "The query did not finish within its time limit" }, truncated: false } : null);
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    act(() => { m.apply(withDeadlocks()); });
    render(<DeadlocksPane model={m} profileId="p1" />);
    await screen.findByText("The query did not finish within its time limit");
    expect(screen.queryByText("No deadlocks in the last 24 hours")).toBeNull();
  });
});

describe("SidePanel detail (S4)", () => {
  it("shows the request text escaped, the current statement still marked", async () => {
    const text = "SELECT 1;\n‮evil\u0007 SELECT 2;";
    rpc.mockImplementation(async () => ({ key: { id: 1, instance: "i1" }, text, statementStart: 10, statementEnd: text.length, planReason: "none" }));
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    render(<SidePanel model={m} view={{ kind: "detail", row: row(1) }} engine="mssql" profileId="p1" onClose={() => {}} />);
    const pre = await screen.findByTestId("detail-text");
    expect(pre.textContent).toBe("SELECT 1;\n\\u202eevil\\x07 SELECT 2;");
    expect(pre.querySelector("mark")?.textContent).toBe("\\u202eevil\\x07 SELECT 2;");
  });
});

describe("ActivityTab lifecycle (L3)", () => {
  it("closing the tab stops its monitor", async () => {
    const { unmount } = render(<ActivityTab tool={tool} />);
    await ready();
    unmount();
    expect(rpc).toHaveBeenCalledWith("monitor.stop", { monitorId: "mon1" });
  });
});

describe("BlockingBanner (O9)", () => {
  it("dismiss mutes it for 5 minutes, then it shows again while the blocking goes on", () => {
    vi.useFakeTimers();
    try {
      const tab = { id: "q1", sessionId: "s1", txState: "open", profileId: "p1" } as never;
      useBlocking.setState({ bySession: {}, muted: {} });
      render(<BlockingBanner tab={tab} />);
      act(() => useBlocking.getState().set({ sessionId: "s1", blockedCount: 3, maxWaitMs: 45_000, source: "check" }));
      expect(screen.getByTestId("blocking-banner").textContent).toContain("blocking 3 sessions for 45 s");
      fireEvent.click(screen.getByLabelText("Dismiss for 5 minutes"));
      expect(screen.queryByTestId("blocking-banner")).toBeNull();
      act(() => { vi.advanceTimersByTime(MUTE_MS - 1_000); });
      expect(screen.queryByTestId("blocking-banner")).toBeNull();
      act(() => { vi.advanceTimersByTime(2_000); });
      expect(screen.getByTestId("blocking-banner")).toBeTruthy();
      act(() => useBlocking.getState().set({ sessionId: "s1", blockedCount: 0, maxWaitMs: 0, source: "check" }));
      expect(screen.queryByTestId("blocking-banner")).toBeNull(); // blocking stopped (O5)
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("Kill / Cancel availability (U4): never armed while unavailable", () => {
  it("Kill session is focusable and says why while unavailable, does nothing on click, and is the danger button once a session can be killed", async () => {
    render(<ActivityTab tool={tool} />);
    await ready();
    push(snap([row(70, { login: "app" })]));
    const kill = (await screen.findByTestId("btn-kill")) as HTMLButtonElement;
    // aria-disabled, not disabled: tokens.css paints .q-btn-danger[aria-disabled] as a plain button (the red fill read as armed).
    expect(kill.getAttribute("aria-disabled")).toBe("true");
    expect(kill.disabled).toBe(false);
    expect(kill.title).toBe("Select a session.");
    expect(screen.getByTestId("btn-cancel").getAttribute("aria-disabled")).toBe("true");
    fireEvent.click(kill);
    expect(screen.queryByTestId("kill-submit")).toBeNull();
    act(() => gridProps.current.onGridSelectionChange({ rows: { first: () => 0 }, current: { cell: [0, 0] } }));
    expect(screen.getByTestId("btn-kill").getAttribute("aria-disabled")).toBeNull();
    fireEvent.click(screen.getByTestId("btn-kill"));
    expect(await screen.findByTestId("kill-submit")).toBeTruthy();
  });

  it("the blocking tree's Kill is red only when it can be used", () => {
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    m.caps = { serverVersion: "x", panes: [], allSessionsVisible: true, canTerminate: false, terminateReason: "No permission.", canCancelRequest: false, livePlans: false };
    m.connection = "connected";
    m.apply(snap([row(1, { status: "sleeping", openTx: 1 }), row(2, { blockedBy: [1], waitMs: 40_000 })], { blocking: { headBlockers: [1], cycles: [], placeholders: [], blockedCount: 1 } }));
    const kill = vi.fn();
    render(<BlockingTree model={m} actions={{ detail: vi.fn(), diagnose: vi.fn(), kill }} />);
    const btn = screen.getAllByText("Kill")[0];
    expect(btn.className).not.toContain("text-err");
    expect(btn.getAttribute("aria-disabled")).toBe("true");
    expect(btn.getAttribute("title")).toBe("No permission.");
    fireEvent.click(btn);
    expect(kill).not.toHaveBeenCalled();
  });
});

describe("paused monitor (U9): said in words and dimmed to a readable tier", () => {
  it("the header says Paused, the grid repaints with muted (not faint) text, the overview and the blocking pane follow; resume clears it", async () => {
    render(<ActivityTab tool={tool} />);
    await ready();
    push(snap([row(1, { login: "app" })]));
    const live = gridProps.current.getCellContent;
    expect(live([1, 0]).themeOverride).toBeUndefined();
    expect(screen.queryByTestId("monitor-paused")).toBeNull();
    push(snap([row(1, { login: "app" })], { paused: true }));
    expect(screen.getByTestId("monitor-paused").textContent).toBe("Paused");
    // Glide repaints only damaged cells: without a new callback the paused grid stayed undimmed.
    const paused = gridProps.current.getCellContent;
    expect(paused).not.toBe(live);
    expect(paused([1, 0]).themeOverride).toEqual({ textDark: "var(--q-muted)" });
    expect(screen.getByTestId("overview").dataset.paused).toBe("true");
    fireEvent.click(screen.getByRole("tab", { name: "Blocking" }));
    expect(document.querySelector("[data-pane-notice]")?.textContent).toContain("Paused at");
    fireEvent.click(screen.getByRole("tab", { name: "Locks" }));
    push(snap([row(1)], { paused: true, status: [{ pane: "sessions", state: "ok" }, { pane: "locks", state: "ok" }], locks: [{ key: { id: 1, instance: "i1" }, resourceType: "OBJECT", object: "dbo.t", objectNote: "IX", mode: "IX", status: "granted", count: 1 }] }));
    const frame = screen.getByTestId("locks-table").closest("[data-paused]") as HTMLElement;
    expect(frame.className).toContain("text-muted");
    expect(frame.className).not.toContain("opacity"); // an opacity took muted text below 4.5:1
    push(snap([row(1, { login: "app" })], { paused: false }));
    expect(screen.queryByTestId("monitor-paused")).toBeNull();
    expect(screen.getByTestId("overview").dataset.paused).toBeUndefined();
  });

  it("the deadlocks notice does not claim the history is stale", async () => {
    rpc.mockImplementation(async (method: string) =>
      method === "monitor.deadlocks" ? { events: [deadlock("d1", 1_000)], status: { pane: "deadlocks", state: "ok" }, truncated: false } : null);
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    act(() => { m.apply(withDeadlocks({ paused: true })); });
    render(<DeadlocksPane model={m} profileId="p1" />);
    await screen.findByTestId("deadlock-graph");
    expect(document.querySelector("[data-pane-notice]")?.textContent).toMatch(/Paused at .* new deadlocks are not read until you resume\./);
  });
});

describe("readable monitor text (no --q-faint for information)", () => {
  it("blocking tree: transaction age, statement text and placeholders use the muted tier", () => {
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    m.texts.put("t".repeat(32), "UPDATE t SET v = 1");
    m.apply(snap([row(1, { status: "sleeping", openTx: 1, txElapsedMs: 6_300, textId: "t".repeat(32) }), row(2, { blockedBy: [1], waitMs: 40_000 })], { blocking: { headBlockers: [1], cycles: [], placeholders: [], blockedCount: 1 } }));
    const { container } = render(<BlockingTree model={m} actions={{ detail: vi.fn(), diagnose: vi.fn(), kill: vi.fn() }} />);
    expect(screen.getByText("tx 6.3 s").className).toContain("text-muted");
    expect(screen.getByText("UPDATE t SET v = 1").className).toContain("text-muted");
    expect(container.querySelector(".text-faint")).toBeNull();
  });
});

describe("DeadlockGraph (B7): labels stay at their drawn size", () => {
  it("draws at 1 unit = 1 px (it was scaled to the pane: 11 px labels became 6 px in a 960 px window)", () => {
    const ev = deadlock("d1", 1_000) as never;
    render(<DeadlockGraph event={ev} />);
    const svg = screen.getByTestId("deadlock-graph");
    const [, , vw, vh] = svg.getAttribute("viewBox")!.split(" ").map(Number);
    expect(svg.getAttribute("width")).toBe(String(vw));
    expect(svg.getAttribute("height")).toBe(String(vh));
    for (const t of svg.querySelectorAll("text")) expect(Number(t.getAttribute("font-size"))).toBeGreaterThanOrEqual(11);
    expect(svg.parentElement!.className).toContain("overflow-x-auto");
  });

  it("draws every box edge at 3:1 (--q-line-strong; --q-line was ~1.4:1), the victim's in the error color", () => {
    const ev = deadlock("d1", 1_000) as never;
    render(<DeadlockGraph event={ev} />);
    const strokes = [...screen.getByTestId("deadlock-graph").querySelectorAll("rect")].map((r) => r.getAttribute("stroke"));
    expect(strokes.length).toBeGreaterThan(0);
    expect(strokes).not.toContain("var(--q-line)");
    for (const s of strokes) expect(["var(--q-line-strong)", "var(--q-err)"]).toContain(s);
  });
});

describe("KillDialog server chip", () => {
  it("names the environment and the server it acts on (the same session id exists on every server)", async () => {
    rpc.mockImplementation(async (m: string) => (m === "monitor.killPreview" ? { key: { id: 77, instance: "i77" }, risk: "none", blockedCount: 0, rollingBack: false, sessionEnded: false } : null));
    const m = new MonitorModel("mon1", async () => ({ texts: {}, missing: [] }));
    render(<KillDialog model={m} row={row(77)} mode="terminateSession" prod profileId="p1" onClose={() => {}} />);
    await screen.findByText(/none — no open transaction/);
    const chip = screen.getByTestId("kill-fact-Server");
    expect(chip.querySelector("[data-env]")?.textContent).toBe("PROD");
    expect(chip.textContent).toContain("Prod Billing");
  });

  it("the activity tab passes its server to the dialog", async () => {
    rpc.mockImplementation(async (method: string) => {
      if (method === "monitor.start") return { monitorId: "mon1", intervalMs: 2000, panes: [], engine: "mssql" };
      if (method === "monitor.killPreview") return { key: { id: 70, instance: "i70" }, risk: "none", blockedCount: 0, rollingBack: false, sessionEnded: false };
      return { texts: {}, missing: [] };
    });
    render(<ActivityTab tool={tool} />);
    await ready();
    push(snap([row(70, { login: "app" })]));
    act(() => gridProps.current.onGridSelectionChange({ rows: { first: () => 0 }, current: { cell: [0, 0] } }));
    fireEvent.click(await screen.findByTestId("btn-kill"));
    expect((await screen.findByTestId("kill-fact-Server")).textContent).toContain("Prod Billing");
  });
});

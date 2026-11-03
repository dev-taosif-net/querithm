// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useDiag, type DebugState } from "../../store/diag";
import { useTabs } from "../../store/tabs";

const m = vi.hoisted(() => ({ rpc: vi.fn(), toast: vi.fn(), editors: new Map<string, any>() }));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../store/ui", () => ({ toast: m.toast }));
vi.mock("../../editor/editors", () => ({ editorForTab: (id: string) => m.editors.get(id) }));
vi.mock("../../editor/monaco", () => ({ monaco: { Range: class { constructor(public a: number, public b: number, public c: number, public d: number) {} } } }));
vi.mock("../../results/ResultsPanel", () => ({ Empty: ({ text }: { text: string }) => <div data-testid="empty">{text}</div> }));

const { DebugPanel, stepDebug, stopDebug } = await import("../DebugPanel");

const dbg = (o: Partial<DebugState> = {}): DebugState => ({
  debugId: "d1", tabId: "t", functionOid: 5, state: "paused", line: 12, currentOid: 5, breakpoints: [3, 8], log: [],
  stack: [{ level: 0, function: "order_total", functionOid: 5, line: 12, args: "p_order_id=42" }, { level: 1, function: "caller", functionOid: 6, line: 2 }],
  variables: [{ name: "v_total", type: "numeric", value: "10.5", isNull: false, varClass: "L" }, { name: "p_order_id", type: "bigint", value: "42", isNull: false, varClass: "A" }, { name: "v_note", type: "text", value: null, isNull: true, varClass: "L" }] as never,
  ...o,
});
const tab = { id: "t", debugId: "d1" } as never;
const setDbg = (o: Partial<DebugState> = {}) => useDiag.setState({ debugs: { d1: dbg(o) } });

function fakeEditor() {
  const e = { cleared: 0, created: [] as any[], reveal: vi.fn(), revealLineInCenterIfOutsideViewport: (l: number) => e.reveal(l), createDecorationsCollection: (items: any[]) => { e.created.push(items); return { clear: () => { e.cleared++; } }; } };
  return e;
}

beforeEach(() => {
  vi.clearAllMocks();
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(undefined);
  m.editors.clear();
  useDiag.setState({ debugs: {}, traces: {} });
  useTabs.setState({ tabs: { t: { id: "t", title: "d", debugId: "d1" } as never }, order: ["t"] } as never);
});
afterEach(cleanup);

describe("DebugPanel", () => {
  it("explains how to start debugging (PostgreSQL only) when there is no session", () => {
    render(<DebugPanel tab={{ id: "t" } as never} />);
    expect(screen.getByTestId("empty").textContent).toMatch(/PostgreSQL step debugging.*pldebugger/);
  });

  it("when paused shows variables (NULL, arg/local), call stack with args, and the line", () => {
    setDbg();
    render(<DebugPanel tab={tab} />);
    expect(screen.getByText("Paused at line 12")).toBeTruthy();
    expect(screen.getByText("v_total")).toBeTruthy();
    expect(screen.getByText("10.5")).toBeTruthy();
    expect(screen.getByText("NULL")).toBeTruthy();
    expect(screen.getByText("arg")).toBeTruthy();
    expect(screen.getAllByText("local")).toHaveLength(2);
    expect(screen.getByText("order_total")).toBeTruthy();
    expect(screen.getByText("line 12")).toBeTruthy();
    expect(screen.getByText("p_order_id=42")).toBeTruthy();
    expect((screen.getByText("▶ Continue") as HTMLButtonElement).disabled).toBe(false);
  });

  it("drives the debugger with continue, step over, step into and stop", () => {
    setDbg();
    render(<DebugPanel tab={tab} />);
    fireEvent.click(screen.getByText("▶ Continue"));
    expect(m.rpc).toHaveBeenLastCalledWith("debug.step", { debugId: "d1", kind: "continue" });
    fireEvent.click(screen.getByText("↷ Step over"));
    expect(m.rpc).toHaveBeenLastCalledWith("debug.step", { debugId: "d1", kind: "over" });
    fireEvent.click(screen.getByText("↓ Step into"));
    expect(m.rpc).toHaveBeenLastCalledWith("debug.step", { debugId: "d1", kind: "into" });
    fireEvent.click(screen.getByText("■ Stop"));
    expect(m.rpc).toHaveBeenLastCalledWith("debug.detach", { debugId: "d1" });
  });

  it.each([
    ["running", "Running…", true, false],
    ["attaching", "Waiting for the target to reach the function…", true, false],
    ["finished", "Finished", true, true],
    ["error", "Error", true, true],
  ] as const)("state %s: label, step buttons disabled while busy/not live", (state, label, _a, stopDisabled) => {
    setDbg({ state });
    render(<DebugPanel tab={tab} />);
    expect(screen.getByText(label)).toBeTruthy();
    expect((screen.getByText("▶ Continue") as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByText("■ Stop") as HTMLButtonElement).disabled).toBe(stopDisabled && (state as string) !== "running" && (state as string) !== "attaching");
  });

  it("shows the output log", () => {
    setDbg({ log: ["NOTICE: hello", "Error: boom"] });
    render(<DebugPanel tab={tab} />);
    expect(screen.getByText("Output")).toBeTruthy();
    expect(screen.getByText("NOTICE: hello")).toBeTruthy();
  });

  it("decorates breakpoints and the current line in the editor, and refreshes them when the line moves", () => {
    const ed = fakeEditor();
    m.editors.set("t", ed);
    setDbg();
    render(<DebugPanel tab={tab} />);
    expect(ed.created[0].map((d: any) => d.options.glyphMarginClassName ?? d.options.className)).toEqual(["q-breakpoint", "q-breakpoint", "q-debug-line"]);
    expect(ed.reveal).toHaveBeenCalledWith(12);
    // a pause inside another function does not highlight a line of this source
    act(() => useDiag.getState().patchDebug("d1", { line: 4, currentOid: 99 }));
    expect(ed.cleared).toBeGreaterThan(0);
    expect(ed.created.at(-1).map((d: any) => d.options.className).filter(Boolean)).toEqual([]);
  });
});

describe("stepDebug / stopDebug", () => {
  it("steps only when the tab has a session, and reports errors", async () => {
    await stepDebug("missing", "over");
    expect(m.rpc).not.toHaveBeenCalled();
    m.rpc.mockRejectedValueOnce(new Error("not paused"));
    await stepDebug("t", "over");
    expect(m.toast).toHaveBeenCalledWith("error", "not paused");
  });
  it("stop detaches, marks the session finished and clears it from the tab (even if detach fails)", async () => {
    setDbg();
    m.rpc.mockRejectedValueOnce(new Error("already gone"));
    await stopDebug("t");
    expect(useDiag.getState().debugs.d1).toMatchObject({ state: "finished", line: null });
    expect(useTabs.getState().tabs.t.debugId).toBeUndefined();
    await stopDebug("t");
    expect(m.rpc).toHaveBeenCalledTimes(1);
  });
});

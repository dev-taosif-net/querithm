// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { TraceEvent } from "../../ipc/contract";
import { useDiag } from "../../store/diag";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";

const m = vi.hoisted(() => ({ toggleTrace: vi.fn(), editors: new Map<string, any>() }));
vi.mock("../../app/actions", () => ({ toggleTrace: m.toggleTrace }));
vi.mock("../../editor/editors", () => ({ editorForTab: (id: string) => m.editors.get(id) }));
vi.mock("../../editor/monaco", () => ({ monaco: { Range: class { constructor(public a: number, public b: number, public c: number, public d: number) {} } } }));
vi.mock("../../results/ResultsPanel", () => ({ Empty: ({ text }: { text: string }) => <div data-testid="empty">{text}</div> }));

const { TracePanel } = await import("../TracePanel");

const ev = (seq: number, o: Partial<TraceEvent> = {}): TraceEvent => ({ seq, ts: seq, eventName: "sp_statement_completed", procName: "dbo.usp_a", line: seq * 10, durationUs: 100 * seq, rowCount: 5, statement: `stmt ${seq}`, ...o });
const baseTab = { id: "t", profileId: "p", traceId: "tr1", spid: 55 };
const editor = () => { const e = { clear: vi.fn(), created: [] as any[], revealLineInCenter: vi.fn(), createDecorationsCollection: vi.fn((d: any[]) => { e.created.push(d); return { clear: e.clear }; }) }; return e; };

function setup(tab: Record<string, unknown> = {}, engine: "mssql" | "pg" = "mssql", caps?: unknown) {
  useProfiles.setState({ profiles: [{ id: "p", engine } as never] });
  useConnections.setState({ servers: { p: { profileId: "p", status: "connected", databases: [], caps } } as never });
  const t = { ...baseTab, ...tab } as never;
  useTabs.setState({ tabs: { t, ...(tab.__others as object ?? {}) } } as never);
  return render(<TracePanel tab={t} />);
}

beforeEach(() => {
  vi.clearAllMocks();
  m.editors.clear();
  useDiag.setState({ traces: {}, debugs: {} });
});
afterEach(cleanup);

describe("TracePanel", () => {
  it("is SQL Server only: PostgreSQL points to the debugger instead", () => {
    setup({}, "pg");
    expect(screen.getByTestId("empty").textContent).toMatch(/PostgreSQL has a step debugger/);
    expect(screen.queryByText("● Start trace")).toBeNull();
  });

  it("starts a trace when none is running and explains what it captures", () => {
    setup({ traceId: undefined });
    expect(screen.getByTestId("empty").textContent).toMatch(/Start a trace to capture statement-level timing/);
    fireEvent.click(screen.getByText("● Start trace"));
    expect(m.toggleTrace).toHaveBeenCalledWith("t");
  });

  it("is disabled with the server's reason when traces are unavailable", () => {
    setup({ traceId: undefined }, "mssql", { trace: { available: false, reason: "VIEW SERVER STATE not granted" } });
    expect((screen.getByText("● Start trace") as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("VIEW SERVER STATE not granted")).toBeTruthy();
  });

  it("shows a live trace waiting for events, with the session id, and stops it", () => {
    setup();
    expect(screen.getByText(/Live ring-buffer trace on SPID 55/)).toBeTruthy();
    expect(screen.getByTestId("empty").textContent).toBe("Waiting for events…");
    fireEvent.click(screen.getByText("■ Stop trace"));
    expect(m.toggleTrace).toHaveBeenCalledWith("t");
  });

  it("falls back to ? when the session id is not known yet", () => {
    setup({ spid: undefined });
    expect(screen.getByText(/SPID \?/)).toBeTruthy();
  });

  it("lists events with duration bars, marks slow ones, errors and batches; clear empties the list", () => {
    useDiag.setState({ traces: { tr1: { traceId: "tr1", sessionId: "s", stopped: false, error: "xe session lost", events: [ev(1), ev(5, { procName: null, statement: null, error: "divide by zero", durationUs: null, rowCount: null, line: null }), ev(2, { durationUs: 450 })] } } });
    setup();
    expect(screen.getByText("xe session lost")).toBeTruthy();
    expect(screen.getAllByText("sp_statement_completed")).toHaveLength(3);
    expect(screen.getByText("(batch)")).toBeTruthy();
    expect(screen.getByText("divide by zero").closest("tr")!.className).toContain("text-err");
    expect(screen.getByText("450").className).toContain("text-warn");
    expect(screen.getByText("100").className).not.toContain("text-warn");
    expect(document.querySelectorAll("tbody .bg-accent\\/70")).toHaveLength(2);
    fireEvent.click(screen.getByText("Clear"));
    expect(useDiag.getState().traces.tr1.events).toEqual([]);
  });

  it("clicking an event highlights its line in the tab of the bound procedure (by qualified or bare name)", () => {
    useDiag.setState({ traces: { tr1: { traceId: "tr1", sessionId: "s", stopped: false, events: [ev(1), ev(2, { procName: "USP_B" })] } } });
    const procEd = editor();
    m.editors.set("proc", procEd);
    setup({ __others: { proc: { id: "proc", boundObject: { schema: "dbo", name: "usp_a" } }, other: { id: "other" } } });
    fireEvent.click(screen.getByText("stmt 1"));
    expect(procEd.created[0][0].range).toMatchObject({ a: 10, c: 10 });
    expect(procEd.created[0][0].options.className).toBe("q-trace-line");
    expect(procEd.revealLineInCenter).toHaveBeenCalledWith(10);
    fireEvent.click(screen.getByText("stmt 1"));
    expect(procEd.clear).toHaveBeenCalledTimes(1);
  });

  it("falls back to this tab for ad-hoc batches or unknown procedures, and ignores events without a line", () => {
    useDiag.setState({ traces: { tr1: { traceId: "tr1", sessionId: "s", stopped: false, events: [ev(1, { procName: null }), ev(2, { line: null }), ev(3, { procName: "dbo.nope" })] } } });
    const own = editor();
    m.editors.set("t", own);
    setup({ __others: { proc: { id: "proc", boundObject: { schema: "dbo", name: "usp_a" } } } });
    fireEvent.click(screen.getByText("stmt 1"));
    expect(own.revealLineInCenter).toHaveBeenCalledWith(10);
    fireEvent.click(screen.getByText("stmt 2"));
    expect(own.revealLineInCenter).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByText("stmt 3"));
    expect(own.revealLineInCenter).toHaveBeenCalledWith(30);
    m.editors.clear();
    fireEvent.click(screen.getByText("stmt 1"));
    expect(own.revealLineInCenter).toHaveBeenCalledTimes(2);
  });
});

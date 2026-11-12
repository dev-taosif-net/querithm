// @vitest-environment jsdom
// Phase D4 S12: the activity monitor's per-engine tables for Oracle (overview metrics, V$SQLSTATS top-query columns, the
// session as SID and SERIAL#, the kill buttons' reasons from the sidecar's capabilities).
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";

const { rpc } = vi.hoisted(() => ({ rpc: vi.fn(async () => ({})) }));
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, onSidecarStatus: () => () => {} }));
vi.mock("../../app/actions", () => ({ newQueryTab: vi.fn(), txAction: vi.fn(), workspaceHooks: { activate: vi.fn() }, activeTabState: () => undefined }));

import { MonitorModel } from "../model";
import { TopQueriesPane } from "../Tables";
import { OverviewStrip } from "../OverviewStrip";
import { KillDialog, sessionLabel } from "../KillDialog";
import { killAvailability } from "../killRules";
import { ENGINES } from "../../engines";
import type { ActivityRow, MonitorCapabilities } from "../../ipc/contract";

afterEach(() => {
  cleanup();
  rpc.mockClear();
});

const model = () => {
  const m = new MonitorModel("m", async () => ({ texts: {}, missing: [] }) as never);
  m.apply({
    monitorId: "m", at: Date.now(), seq: 1, status: [{ pane: "topQueries", state: "ok" }], lagMs: 0, connectionCount: 1, throttled: false, paused: false,
    connection: "connected", topQueries: [{ queryHash: "f2tts8m7sb5cv", database: null, execDelta: 3, cpuMsDelta: 5, durationMsDelta: 12, logicalReadsDelta: 40, rowsDelta: 3, ioTimeMsDelta: 2 }],
  } as never);
  return m;
};

/** A session row as OracleActivity sends it: SID, "SERIAL#:LOGON_TIME", lower-case status, SQL_ID:SQL_EXEC_ID. */
const row = (over: Partial<ActivityRow> = {}): ActivityRow => ({
  key: { id: 66, instance: "441:20261112061739" }, status: "active", blockedBy: [], isUserSession: true, login: "QUERITHM_APP",
  statementStart: "f2tts8m7sb5cv:16777216", ...over,
}) as ActivityRow;

const caps = (over: Partial<MonitorCapabilities> = {}): MonitorCapabilities => ({
  serverVersion: "Oracle AI Database 26ai Free 23.26.0.0.0", panes: [], allSessionsVisible: true, canTerminate: true, canCancelRequest: true,
  livePlans: false, login: "SYSTEM", ...over,
}) as MonitorCapabilities;

describe("Oracle activity monitor (D4 S12)", () => {
  it("is offered for Oracle (the sidecar degrades what the account may not read)", () => {
    expect(ENGINES.oracle.monitor).toBe(true);
  });

  it("top queries from V$SQLSTATS: CPU order, buffer gets, user I/O and the SQL_ID", () => {
    render(<TopQueriesPane model={model()} engine="oracle" onOpenText={() => {}} />);
    expect(screen.getByRole("option", { name: "CPU" })).toBeTruthy();
    expect(screen.getByRole("option", { name: "buffer gets" })).toBeTruthy();
    expect(screen.getByText("SQL_ID")).toBeTruthy();
    expect(screen.getByText("Buffer gets")).toBeTruthy();
    expect(screen.getByText("User I/O")).toBeTruthy();
    expect(screen.getByText(/V\$SQLSTATS/)).toBeTruthy();
    expect(rpc).toHaveBeenCalledWith("monitor.setTopQueries", expect.objectContaining({ orderBy: "cpu" }));
  });

  it("the overview shows Oracle's own metrics", () => {
    const { container } = render(<OverviewStrip model={model()} engine="oracle" />);
    for (const label of ["CPU", "Queries/s", "TPS", "Cache hit", "Active", "Idle in tx"]) expect(container.textContent).toContain(label);
    expect(container.textContent).not.toContain("Page life");
  });

  it("names a session by SID and SERIAL#, as ALTER SYSTEM KILL SESSION does; other engines by the id", () => {
    expect(sessionLabel("oracle", { id: 66, instance: "441:20261112061739" })).toBe("66, serial# 441");
    expect(sessionLabel("oracle", { id: 66, instance: "" })).toBe("66");
    expect(sessionLabel("pg", { id: 66, instance: "441:20261112061739" })).toBe("66");
    render(<KillDialog model={model()} row={row()} mode="cancelRequest" prod={false} engine="oracle" onClose={() => {}} />);
    expect(screen.getByText("Cancel the running query of session 66, serial# 441?")).toBeTruthy();
  });

  it("cancel needs a running statement; without ALTER SYSTEM the buttons say which privilege is missing", () => {
    expect(killAvailability(row(), caps(), "cancelRequest", true).enabled).toBe(true);
    expect(killAvailability(row({ status: "idle in transaction", statementStart: null }), caps(), "cancelRequest", true)).toEqual({ enabled: false, reason: "No running query." });
    expect(killAvailability(row({ rollingBack: true, status: "killed" }), caps(), "terminateSession", true).reason).toBe("Already rolling back.");
    const why = "Cancelling or terminating a session needs the ALTER SYSTEM privilege, which this account does not have.";
    const denied = caps({ canTerminate: false, canCancelRequest: false, terminateReason: why, cancelReason: why });
    expect(killAvailability(row(), denied, "terminateSession", true)).toEqual({ enabled: false, reason: why });
    expect(killAvailability(row(), denied, "cancelRequest", true)).toEqual({ enabled: false, reason: why });
  });
});

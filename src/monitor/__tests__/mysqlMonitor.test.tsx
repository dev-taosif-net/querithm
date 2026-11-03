// @vitest-environment jsdom
// Phase D2: the activity monitor's per-engine tables for MySQL / MariaDB (overview metrics, top-query columns, EXPLAIN text).
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";

const { rpc } = vi.hoisted(() => ({ rpc: vi.fn(async () => ({})) }));
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, onSidecarStatus: () => () => {} }));
vi.mock("../../app/actions", () => ({ newQueryTab: vi.fn(), txAction: vi.fn(), workspaceHooks: { activate: vi.fn() }, activeTabState: () => undefined }));

import { MonitorModel } from "../model";
import { TopQueriesPane } from "../Tables";
import { OverviewStrip } from "../OverviewStrip";

afterEach(() => {
  cleanup();
  rpc.mockClear();
});

/** A model whose top-queries pane is open (one connected snapshot with the pane ok). */
const model = () => {
  const m = new MonitorModel("m", async () => ({ texts: {}, missing: [] }) as never);
  m.apply({
    monitorId: "m", at: Date.now(), seq: 1, status: [{ pane: "topQueries", state: "ok" }], lagMs: 0, connectionCount: 1, throttled: false, paused: false,
    connection: "connected", topQueries: [{ queryHash: "abc", database: "shop", execDelta: 3, durationMsDelta: 12, logicalReadsDelta: 40, rowsDelta: 3 }],
  } as never);
  return m;
};

describe("MySQL activity monitor tables", () => {
  it("top queries from statement digests: no CPU column or order, rows examined, digest", () => {
    render(<TopQueriesPane model={model()} engine="mySql" onOpenText={() => {}} />);
    expect(screen.queryByRole("option", { name: "CPU" })).toBeNull();
    expect(screen.getByRole("option", { name: "rows examined" })).toBeTruthy();
    expect(screen.getByText("Statement digests report no CPU time.")).toBeTruthy();
    // The pane asks the sidecar for duration order (MySQL has no CPU time to sort by).
    expect(rpc).toHaveBeenCalledWith("monitor.setTopQueries", expect.objectContaining({ orderBy: "duration" }));
  });

  it("SQL Server keeps its CPU order and PostgreSQL its shared blocks", () => {
    render(<TopQueriesPane model={model()} engine="mssql" onOpenText={() => {}} />);
    expect(screen.getByRole("option", { name: "CPU" })).toBeTruthy();
    expect(rpc).toHaveBeenCalledWith("monitor.setTopQueries", expect.objectContaining({ orderBy: "cpu" }));
    cleanup();
    render(<TopQueriesPane model={model()} engine="pg" onOpenText={() => {}} />);
    expect(screen.getByRole("option", { name: "shared blocks" })).toBeTruthy();
    expect(screen.queryByText("Statement digests report no CPU time.")).toBeNull();
  });

  it("the overview shows MySQL's own metrics", () => {
    const { container } = render(<OverviewStrip model={model()} engine="mySql" />);
    for (const label of ["Queries/s", "Cache hit", "Active", "Idle in tx", "Connections", "Deadlocks"]) expect(container.textContent).toContain(label);
    expect(container.textContent).not.toContain("TPS");
  });
});

// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";

vi.mock("../../ipc/client", () => ({ rpc: vi.fn(), ipcTimings: () => ({ "x": [1] }), shell: {} }));
vi.mock("../../ipc/frames", () => ({ streamExecution: vi.fn() }));
vi.mock("../../editor/completion", () => ({ complete: vi.fn() }));
vi.mock("../../editor/editors", () => ({ editorForTab: vi.fn(), getModel: vi.fn() }));
vi.mock("../../editor/providers", () => ({ completionStats: { n: 3 } }));
vi.mock("../actions", () => ({ activateTab: vi.fn(), changeConnection: vi.fn(), newQueryTab: vi.fn(), runQuery: vi.fn(), benchMarks: { dbSwitch: [12], firstBatch: [34] } }));

import { bigProcedure, installBench } from "../bench";

describe("bigProcedure", () => {
  it("generates a T-SQL procedure of about the requested size, ending with END;", () => {
    const sql = bigProcedure(200);
    const lines = sql.split("\n");
    expect(lines[0]).toMatch(/^CREATE OR ALTER PROCEDURE dbo\.usp_BenchBig/);
    expect(lines.length).toBeGreaterThanOrEqual(198);
    expect(lines.length).toBeLessThan(215);
    expect(lines.at(-1)).toBe("END;");
  });
  it("includes a MERGE every seventh CTE", () => {
    expect(bigProcedure(400)).toContain("MERGE dbo.OrderTotals");
    expect(bigProcedure(12)).not.toContain("MERGE");
  });
});

describe("installBench", () => {
  it("exposes the harness on window.__bench with the recorded samples", () => {
    installBench();
    const b = (window as any).__bench;
    expect(b.ready).toBe(true);
    expect(typeof b.typing).toBe("function");
    expect(b.dbSwitch()).toEqual([12]);
    expect(b.firstBatch()).toEqual([34]);
    expect(b.completionStats()).toEqual({ n: 3 });
    expect(b.ipcTimings()).toEqual({ x: [1] });
    expect(b.marks().navigationToNow).toBeGreaterThanOrEqual(0);
  });
});

import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({ toast: vi.fn(), reveal: vi.fn(), setBottomTab: vi.fn(), pos: { lineNumber: 5 } as { lineNumber: number } | null }));
vi.mock("../../store/ui", () => ({ toast: h.toast }));
vi.mock("../reveal", () => ({ revealEditorLine: h.reveal }));
vi.mock("../../app/actions", () => ({ setBottomTab: h.setBottomTab }));
vi.mock("../../editor/editors", () => ({ editorForTab: () => ({ getPosition: () => h.pos }) }));

import type { ServerMessage } from "../../ipc/contract";
import { useTabs } from "../../store/tabs";
import { useExecutions } from "../../store/executions";
import { executionData } from "../model";
import { compareNext, stepError, stepOutput } from "../panelActions";
import { ordersSet, registerExecution, view } from "./fixtures";

const err = (line: number | null) => ({ executionId: "e2", severity: "error", message: `at ${line}`, batchIndex: 0, ts: 1, editorLine: line }) as ServerMessage;
function tab(over = {}) {
  useTabs.setState({ tabs: { t: { id: "t", title: "q", executions: ["e1", "e2"], activeExecutionId: "e2", ...over } as never } });
}

beforeEach(() => {
  vi.clearAllMocks();
  executionData.clear();
  h.pos = { lineNumber: 5 };
  useExecutions.setState({ views: { e1: view("e1"), e2: view("e2", { messages: [err(3), err(9), err(null)], setAfter: { 0: 0, 1: 0 } }) } });
  registerExecution("e1", ordersSet(0));
  registerExecution("e2", ordersSet(0), ordersSet(1));
  tab();
});

describe("stepOutput", () => {
  it("moves along the ledger: sets, then errors", () => {
    stepOutput("t", 1);
    expect(useTabs.getState().tabs.t.resultIndex).toBe(1);
    stepOutput("t", 1);
    expect(useTabs.getState().tabs.t.ledgerError).toBe(0);
    stepOutput("t", -1);
    expect(useTabs.getState().tabs.t).toMatchObject({ resultIndex: 1, ledgerError: undefined });
    stepOutput("nope", 1);
  });
});

describe("stepError", () => {
  it("F4 goes to the next error line after the cursor, wrapping; Shift+F4 to the previous; the ledger shows it", () => {
    stepError("t", 1);
    expect(h.reveal).toHaveBeenLastCalledWith("t", 9);
    expect(useTabs.getState().tabs.t.ledgerError).toBe(1);
    h.pos = { lineNumber: 20 };
    stepError("t", 1);
    expect(h.reveal).toHaveBeenLastCalledWith("t", 3);
    h.pos = { lineNumber: 5 };
    stepError("t", -1);
    expect(h.reveal).toHaveBeenLastCalledWith("t", 3);
    h.pos = { lineNumber: 1 };
    stepError("t", -1);
    expect(h.reveal).toHaveBeenLastCalledWith("t", 9);
  });

  it("a run without errors that have a line says so", () => {
    useExecutions.setState({ views: { e2: view("e2", { messages: [err(null)] }) } });
    stepError("t", 1);
    expect(h.toast).toHaveBeenCalledWith("info", "The shown run has no errors with an editor line.");
    expect(h.reveal).not.toHaveBeenCalled();
  });
});

describe("compareNext", () => {
  it("compares with the run's next result set, else with the same set of the previous run, else says why", () => {
    compareNext("t");
    expect(useTabs.getState().tabs.t.compare).toEqual({ executionId: "e2", resultIndex: 1 });
    expect(h.setBottomTab).toHaveBeenCalledWith("t", "results");
    registerExecution("e2", ordersSet(0));
    compareNext("t");
    expect(useTabs.getState().tabs.t.compare).toEqual({ executionId: "e1", resultIndex: 0 });
    tab({ executions: ["e2"] });
    compareNext("t");
    expect(h.toast).toHaveBeenCalledWith("info", expect.stringMatching(/^Nothing to compare with/));
    tab({ activeExecutionId: undefined });
    compareNext("t");
    expect(h.toast).toHaveBeenCalledWith("info", "Run a query first.");
  });
});

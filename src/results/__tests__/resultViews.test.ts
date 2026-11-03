import { beforeEach, describe, expect, it } from "vitest";
import { useTabs } from "../../store/tabs";
import { executionData } from "../model";
import { cleanCharts, setChartSpec, showResultAs, shownResultIndex } from "../resultViews";
import { ordersSet, registerExecution } from "./fixtures";

beforeEach(() => {
  executionData.clear();
  useTabs.setState({ tabs: { t1: { id: "t1", title: "q.sql", activeExecutionId: "e1", executions: ["e1"], bottomTab: "messages", bottomOpen: false } as never } });
});

describe("grid or chart per result set (Ctrl+1 / Ctrl+2)", () => {
  it("acts on the shown result set and opens the Results panel", () => {
    registerExecution("e1", ordersSet(0), ordersSet(3));
    useTabs.getState().update("t1", { resultIndex: 1 });
    expect(shownResultIndex(useTabs.getState().tabs.t1)).toBe(3);
    showResultAs("t1", "chart");
    expect(useTabs.getState().tabs.t1).toMatchObject({ charts: { 3: { view: "chart", spec: null } }, bottomTab: "results", bottomOpen: true });
    setChartSpec("t1", 3, { kind: "bar", x: "name", y: ["qty"], series: null, agg: "sum" });
    showResultAs("t1", "grid");
    expect(useTabs.getState().tabs.t1.charts?.[3]).toEqual({ view: "grid", spec: { kind: "bar", x: "name", y: ["qty"], series: null, agg: "sum" } });
  });
  it("does nothing without results", () => {
    showResultAs("t1", "chart");
    expect(useTabs.getState().tabs.t1.charts).toBeUndefined();
    showResultAs("missing", "chart");
  });
  it("cleans charts read back from the workspace file", () => {
    expect(cleanCharts(null)).toBeUndefined();
    expect(cleanCharts([1])).toBeUndefined();
    expect(cleanCharts({ 0: { view: "chart", spec: { kind: "bar", x: "a", y: ["b"], agg: "sum" } }, 1: { view: "pie?", spec: { kind: "nope" } }, x: {}, "-1": {}, 2: 5 })).toEqual({
      0: { view: "chart", spec: { kind: "bar", x: "a", y: ["b"], series: null, agg: "sum" } },
      1: { view: "grid", spec: null },
    });
  });
});

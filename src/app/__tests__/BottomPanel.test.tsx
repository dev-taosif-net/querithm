// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";

const h = vi.hoisted(() => ({
  actions: { setBottomTab: vi.fn(), showRun: vi.fn(), toggleBottom: vi.fn(), toggleMaximize: vi.fn(), showPlan: vi.fn() },
}));
vi.mock("../actions", () => h.actions);
vi.mock("../commands", () => ({ commands: [], keysFor: () => [], displayKeys: (k: string) => k }));
vi.mock("../../results/ResultsPanel", () => ({ ResultsPanel: () => <div>results-view</div> }));
vi.mock("../../results/MessagesPanel", () => ({ MessagesPanel: () => <div>messages-view</div>, fmtMs: (ms: number) => `${ms} ms` }));
vi.mock("../../refactor/UsagesPanel", () => ({ UsagesPanel: () => <div>usages-view</div> }));
vi.mock("../RuntimePanel", () => ({ RuntimePanel: () => <div>runtime-view</div> }));
vi.mock("../../plans/PlanView", () => ({ PlanView: () => <div>plan-view</div> }));

import { BottomPanel, PANEL_VIEWS } from "../BottomPanel";
import { useTabs, type TabState } from "../../store/tabs";
import { useExecutions } from "../../store/executions";
import { useUsages } from "../../store/usages";
import { useSettings } from "../../store/settings";
import { useDiag } from "../../store/diag";
import { useProfiles } from "../../store/profiles";
import { executionData } from "../../results/model";
import { ordersSet, registerExecution, view } from "../../results/__tests__/fixtures";

const base = { id: "t1", title: "q.sql", txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.35, actualPlan: false };
function tab(over: Partial<TabState> = {}): TabState {
  const t = { ...base, ...over } as TabState;
  useTabs.setState({ tabs: { t1: t }, order: ["t1"], activeId: "t1" });
  return t;
}
const show = (over: Partial<TabState> = {}) => render(<BottomPanel tab={tab(over)} />);
const tabsOf = () => within(screen.getByRole("tablist", { name: "Results panel views" })).getAllByRole("tab");

beforeEach(() => {
  vi.clearAllMocks();
  executionData.clear();
  useExecutions.setState({ views: {} });
  useUsages.setState({ byTab: {} } as never);
  useDiag.setState({ debugs: {} } as never);
  useSettings.setState({ panelDock: "bottom" });
  useProfiles.setState({ profiles: [{ id: "ms", engine: "mssql", environment: "dev" } as never, { id: "pg", engine: "pg", environment: "dev" } as never] });
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("BottomPanel views", () => {
  it.each([["mssql", "ms"], ["pg", "pg"], ["no connection", undefined]])("five fixed tabs in one order on %s: nothing appears, moves or hides per engine", (_name, profileId) => {
    show({ profileId });
    expect(tabsOf().map((t) => t.textContent)).toEqual(["Results", "Messages", "Plan", "Runtime", "Usages"]);
    expect(PANEL_VIEWS.map((v) => v.id)).toEqual(["results", "messages", "plan", "runtime", "usages"]);
    // Run views and tab views are told apart by a divider after Plan.
    expect(PANEL_VIEWS.map((v) => v.scope)).toEqual(["run", "run", "run", "tab", "tab"]);
  });

  it.each([
    ["results", "results-view"], ["messages", "messages-view"], ["runtime", "runtime-view"], ["usages", "usages-view"],
  ] as const)("shows the %s view; only the chosen tab is selected and in the Tab order", (bt, text) => {
    show({ bottomTab: bt });
    expect(screen.getByText(text)).toBeInTheDocument();
    const on = tabsOf().filter((t) => t.getAttribute("aria-selected") === "true");
    expect(on).toHaveLength(1);
    expect(on[0]).toHaveAttribute("tabindex", "0");
    expect(tabsOf().filter((t) => t.getAttribute("tabindex") === "0")).toHaveLength(1);
  });

  it("lazy-loads the plan view", async () => {
    show({ bottomTab: "plan" });
    expect(await screen.findByText("plan-view")).toBeInTheDocument();
  });

  it("arrow keys, Home and End move between the views and select them (wrapping)", () => {
    show({ bottomTab: "results" });
    const [results] = tabsOf();
    fireEvent.keyDown(results, { key: "ArrowRight" });
    fireEvent.keyDown(results, { key: "ArrowLeft" });
    fireEvent.keyDown(results, { key: "End" });
    fireEvent.keyDown(results, { key: "Home" });
    expect(h.actions.setBottomTab.mock.calls.map((c) => c[1])).toEqual(["messages", "usages", "usages", "results"]);
  });

  it("badges are text: result sets, errors with an icon and a spoken count, plans, a live trace, usages", () => {
    registerExecution("e1", ordersSet(0), ordersSet(1));
    useExecutions.setState({ views: { e1: view("e1", { messages: [{ severity: "error" }, { severity: "info" }, { severity: "error" }] as never, plans: [{}] as never }) } });
    useUsages.setState({ byTab: { t1: { result: { hits: [{}, {}, {}] } } } } as never);
    show({ activeExecutionId: "e1", executions: ["e1"], traceId: "tr" });
    expect(screen.getByRole("tab", { name: "Results, 2 result sets" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Messages, 2 errors" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Plan, 1 plan" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Runtime, trace live" })).toHaveTextContent("● Live");
    expect(screen.getByRole("tab", { name: "Usages, 3 usages" })).toBeInTheDocument();
  });

  it("a paused debugger shows on the Runtime tab", () => {
    useDiag.setState({ debugs: { d1: { state: "paused" } } } as never);
    show({ debugId: "d1" });
    expect(screen.getByRole("tab", { name: "Runtime, debugger paused" })).toBeInTheDocument();
  });

  it("header buttons: maximize / restore, dock right / under, hide", async () => {
    const user = userEvent.setup();
    show();
    await user.click(screen.getByRole("button", { name: /^Maximize the panel/ }));
    expect(h.actions.toggleMaximize).toHaveBeenCalledWith("t1");
    await user.click(screen.getByRole("button", { name: "Dock the panel to the right of the editor" }));
    expect(useSettings.getState().panelDock).toBe("right");
    await user.click(screen.getByRole("button", { name: /^Hide the panel/ }));
    expect(h.actions.toggleBottom).toHaveBeenCalledWith("t1");
    cleanup();
    show({ bottomMax: true });
    expect(screen.getByRole("button", { name: /^Restore the panel/ })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Dock the panel under the editor" })).toBeInTheDocument();
  });

  it("narrow (< 760 px): inactive tabs keep only their icon and badge; the name stays spoken and in the tooltip", () => {
    vi.spyOn(Element.prototype, "getBoundingClientRect").mockReturnValue({ width: 650, height: 200 } as DOMRect);
    show({ bottomTab: "results" });
    const messages = screen.getByRole("tab", { name: "Messages" });
    expect(messages).toHaveAttribute("title", "Messages");
    expect(messages.textContent).toBe("");
    expect(screen.getByRole("tab", { name: "Results" })).toHaveTextContent("Results");
    // Dock right needs width: it is not offered here.
    expect(screen.queryByRole("button", { name: /Dock the panel/ })).toBeNull();
  });
});

describe("Run picker", () => {
  function runs() {
    useExecutions.setState({
      views: {
        e1: view("e1", { startedAt: Date.UTC(2026, 10, 1, 8, 0, 0), elapsedMs: 1300, messages: [{ severity: "error", message: "x" }] as never }),
        e2: view("e2", { startedAt: Date.UTC(2026, 10, 1, 8, 1, 0), elapsedMs: 900 }),
        e3: view("e3", { startedAt: Date.UTC(2026, 10, 1, 8, 2, 0), elapsedMs: 1210, messages: [{ severity: "error", message: "y" }] as never }),
      },
    });
    registerExecution("e3", ordersSet(0), ordersSet(1));
  }

  it("is absent before the first run", () => {
    show();
    expect(screen.queryByRole("button", { name: /Shown run/ })).toBeNull();
  });

  it("names the shown run and lists the kept runs newest first; picking one shows it", async () => {
    runs();
    show({ executions: ["e1", "e2", "e3"], activeExecutionId: "e3" });
    const btn = screen.getByRole("button", { name: /^Shown run: Run 3 of 3/ });
    expect(btn).toHaveTextContent("Run 3 of 3");
    expect(btn.getAttribute("aria-label")).toContain("1 error");
    await userEvent.setup().click(btn);
    const items = screen.getAllByRole("menuitemcheckbox");
    expect(items.map((i) => i.textContent)).toEqual([expect.stringContaining("Run 3"), expect.stringContaining("Run 2"), expect.stringContaining("Run 1")]);
    expect(items[0]).toHaveTextContent("2 sets · 1 error · 1210 ms");
    expect(items[0]).toHaveAttribute("aria-checked", "true");
    await userEvent.setup().click(items[2]);
    expect(h.actions.showRun).toHaveBeenCalledWith("t1", "e1");
  });

  it("an older run is marked older and read-only", () => {
    runs();
    show({ executions: ["e1", "e2", "e3"], activeExecutionId: "e1" });
    const btn = screen.getByRole("button", { name: /^Shown run: Run 1 of 3/ });
    expect(btn).toHaveTextContent("older");
    expect(btn.getAttribute("aria-label")).toContain("older run, read-only");
  });

  it("is unavailable on Runtime and Usages, which belong to the tab, and says why", async () => {
    runs();
    show({ executions: ["e3"], activeExecutionId: "e3", bottomTab: "runtime" });
    const btn = screen.getByRole("button", { name: /^Shown run/ });
    expect(btn).toHaveAttribute("aria-disabled", "true");
    expect(btn).toHaveAttribute("title", "Runtime and Usages belong to the tab, not to a run");
    await userEvent.setup().click(btn);
    expect(screen.queryByRole("menu")).toBeNull();
  });
});

describe("chrome during a stream", () => {
  it("the header follows a streaming result at most every 250 ms, not per batch", async () => {
    vi.useFakeTimers();
    try {
      const data = registerExecution("e1", ordersSet(0, false));
      useExecutions.setState({ views: { e1: view("e1", { state: "streaming" }) } });
      show({ executions: ["e1"], activeExecutionId: "e1" });
      expect(screen.getByRole("tab", { name: "Results, 1 result set" })).toBeInTheDocument();
      act(() => {
        data.resultSets.push(ordersSet(1, false));
        for (let i = 0; i < 100; i++) data.notify();
      });
      // Nothing yet: the 100 notifications are folded into one update.
      expect(screen.getByRole("tab", { name: "Results, 1 result set" })).toBeInTheDocument();
      await act(async () => void vi.advanceTimersByTime(260));
      expect(screen.getByRole("tab", { name: "Results, 2 result sets" })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("BottomPanel boundary (M17)", () => {
  it("a view that throws fails inside the panel; the header stays and another view opens", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const results = await import("../../results/ResultsPanel");
    vi.spyOn(results, "ResultsPanel").mockImplementation(() => { throw new Error("grid broke"); });
    const { rerender } = show({ bottomTab: "results" });
    expect(screen.getByText("This panel failed")).toBeInTheDocument();
    expect(screen.getByText(/Results · Error: grid broke/)).toBeInTheDocument();
    expect(tabsOf()).toHaveLength(5);
    rerender(<BottomPanel tab={tab({ bottomTab: "messages" })} />);
    expect(screen.getByText("messages-view")).toBeInTheDocument();
    expect(screen.queryByText("This panel failed")).toBeNull();
  });

  it("a Results view that failed on one run recovers on another run, without switching views (W7)", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const results = await import("../../results/ResultsPanel");
    const spy = vi.spyOn(results, "ResultsPanel").mockImplementation(() => { throw new Error("grid broke"); });
    const { rerender } = show({ bottomTab: "results", executions: ["e1"], activeExecutionId: "e1" });
    expect(screen.getByText("This panel failed")).toBeInTheDocument();
    spy.mockImplementation(() => <div>results-view</div>);
    rerender(<BottomPanel tab={tab({ bottomTab: "results", executions: ["e1", "e2"], activeExecutionId: "e2" })} />);
    expect(screen.getByText("results-view")).toBeInTheDocument();
    expect(screen.queryByText("This panel failed")).toBeNull();
  });
});

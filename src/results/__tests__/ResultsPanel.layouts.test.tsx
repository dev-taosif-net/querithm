// @vitest-environment jsdom
// The Results panel's layouts for several result sets (§12 bottom panel): Tabs, Stacked and Side by side, with pins,
// maximize, Differences and Export all. The grid, chart and export are stubs; the layout logic is real.
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";

const { grid, exportAll, toast } = vi.hoisted(() => ({
  grid: { all: [] as Record<string, unknown>[] },
  exportAll: vi.fn(async (..._a: unknown[]) => {}),
  toast: vi.fn(),
}));

vi.mock("../ResultGrid", async (orig) => {
  const actual = await orig<typeof import("../ResultGrid")>();
  return {
    ...actual,
    ResultGrid: (props: { handleRef?: { current: unknown }; rs: { index: number } }) => {
      grid.all.push(props as never);
      if (props.handleRef) props.handleRef.current = { copySelection: () => "a", selectedRefs: () => [] };
      return <div data-testid="grid" data-set={props.rs.index} data-handle={props.handleRef ? "" : undefined} />;
    },
  };
});
vi.mock("../../charts/ChartView", () => ({ ChartView: () => <div data-testid="chart" /> }));
vi.mock("../export", () => ({
  exportFormats: [{ format: "csv", label: "CSV" }],
  exportAllFormats: [{ format: "xlsx", label: "Excel (.xlsx), a sheet per result set" }],
  exportResultSet: vi.fn(async () => {}),
  exportAllResultSets: exportAll,
}));
vi.mock("../../app/actions", () => ({ runQuery: vi.fn(), setBottomTab: vi.fn(), showRun: vi.fn() }));
vi.mock("../reveal", () => ({ revealEditorLine: vi.fn() }));
vi.mock("../gridEdit", async (orig) => ({ ...(await orig<typeof import("../gridEdit")>()), applyChanges: vi.fn(), reviewChanges: vi.fn(), scheduleTokens: vi.fn() }));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast }));

import type { Engine, ServerMessage } from "../../ipc/contract";
import { ENGINES } from "../../engines";
import { PanelWidthContext } from "../../app/panelKit";
import { useExecutions } from "../../store/executions";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";
import { executionData } from "../model";
import { ResultsPanel } from "../ResultsPanel";
import { stagings } from "../staging";
import { stagingFor } from "../gridEdit";
import { editInfo, ordersSet, registerExecution, view } from "./fixtures";
import { MAX_MOUNTED, SIDE_TOO_NARROW } from "../resultLayout";

beforeAll(async () => {
  await import("../ResultGrid");
});

const tab = (over = {}) => ({ id: "tab1", title: "orders.sql", profileId: "p1", sessionId: "sess1", activeExecutionId: "e1", executions: ["e1"], ...over }) as never;
function LivePanel({ width = "wide" as const }: { width?: "wide" | "mid" | "narrow" }) {
  const t = useTabs((s) => s.tabs.tab1);
  return <PanelWidthContext.Provider value={width}><ResultsPanel tab={t} /></PanelWidthContext.Provider>;
}
const sets = (n: number) => Array.from({ length: n }, (_, i) => ordersSet(i));
function setup(n: number, layout?: "tabs" | "stacked" | "side", viewOver = {}) {
  useExecutions.setState({ views: { e1: view("e1", { resultSetCount: n, ...viewOver }) } });
  registerExecution("e1", ...sets(n));
  if (layout) useTabs.setState({ tabs: { tab1: tab({ resultLayout: layout }) } });
}
const sections = () => [...document.querySelectorAll<HTMLElement>("[data-q-pane-key]")];
const layoutAttr = () => document.querySelector("[data-q-results]")?.getAttribute("data-q-result-layout");
const switchGroup = () => screen.queryByRole("group", { name: "Lay out the result sets" });
const handleGrid = () => document.querySelector<HTMLElement>("[data-testid=grid][data-handle]");

beforeEach(() => {
  stagings.clear();
  useUi.setState({ dialogs: [] });
  executionData.clear();
  useExecutions.setState({ views: {} });
  useSettings.setState({ resultLayout: "tabs" } as never);
  useProfiles.setState({ profiles: [{ id: "p1", engine: "mssql", environment: "dev" } as never] });
  useTabs.setState({ tabs: { tab1: tab() } });
  vi.clearAllMocks();
  grid.all = [];
});
afterEach(cleanup);

describe("choosing the layout", () => {
  it("one result set shows alone whatever the layout, with no layout switch", async () => {
    setup(1, "stacked");
    render(<LivePanel />);
    await screen.findByTestId("grid");
    expect(layoutAttr()).toBe("tabs");
    expect(switchGroup()).toBeNull();
  });

  it("Tabs is the default; the switch stores the tab's own choice and the panel lays the sets out at once", async () => {
    setup(2);
    render(<LivePanel />);
    await screen.findByTestId("grid");
    expect(layoutAttr()).toBe("tabs");
    expect(screen.getAllByTestId("grid")).toHaveLength(1);
    fireEvent.click(within(switchGroup()!).getByRole("button", { name: "Stacked: one under another" }));
    expect(useTabs.getState().tabs.tab1.resultLayout).toBe("stacked");
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    expect(layoutAttr()).toBe("stacked");
    expect(within(switchGroup()!).getByRole("button", { name: "Stacked: one under another" }).getAttribute("aria-pressed")).toBe("true");
  });

  it("the Settings default applies to a tab without its own choice", async () => {
    useSettings.setState({ resultLayout: "side" } as never);
    setup(2);
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    expect(layoutAttr()).toBe("side");
  });
});

describe("Stacked", () => {
  it("every set one under another; the selected one has the tools' grid handle; clicking another selects it", async () => {
    setup(3, "stacked");
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(3));
    expect(sections().map((s) => s.dataset.qPaneKey)).toEqual(["e1:0", "e1:1", "e1:2"]);
    expect(handleGrid()?.dataset.set).toBe("0");
    expect(document.querySelectorAll("[data-testid=grid][data-handle]")).toHaveLength(1);
    fireEvent.pointerDown(sections()[2]);
    expect(useTabs.getState().tabs.tab1.resultIndex).toBe(2);
    await waitFor(() => expect(handleGrid()?.dataset.set).toBe("2"));
    expect(sections()[2].hasAttribute("data-q-pane-selected")).toBe(true);
    // The footer and tools describe the selected set.
    expect(document.querySelector("[data-q-panel-footer]")?.textContent).toMatch(/Result set 3/);
  });

  it("errors sit in place between the sets around them", async () => {
    const e: ServerMessage = { executionId: "e1", severity: "error", message: "Invalid column name 'x'.\nmore", batchIndex: 0, ts: 1, editorLine: 4 } as ServerMessage;
    setup(2, "stacked", { messages: [e], setAfter: { 0: 0, 1: 1 } });
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    const order = [...document.querySelectorAll<HTMLElement>("[data-q-stack-key]")].map((x) => x.dataset.qStackKey);
    expect(order).toEqual(["e1:0", "err:0", "e1:1"]);
    const card = document.querySelector("[data-q-stack-error]")!;
    expect(card.textContent).toContain("Error · line 4");
    expect(card.textContent).toContain("Invalid column name 'x'.");
    expect(card.textContent).not.toContain("more");
    fireEvent.click(within(card as HTMLElement).getByRole("button", { name: "Show" }));
    expect(useTabs.getState().tabs.tab1.ledgerError).toBe(0);
  });

  it("many sets: at most 8 live grids (the rest are placeholders of the same height); sets after the 20th start collapsed", async () => {
    setup(25, "stacked");
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(MAX_MOUNTED));
    expect(sections()).toHaveLength(25);
    const collapsed = sections().filter((s) => s.querySelector("[aria-label^=\"Expand result set\"]"));
    expect(collapsed.map((s) => s.dataset.qPaneKey)).toEqual(["e1:20", "e1:21", "e1:22", "e1:23", "e1:24"]);
    expect(document.querySelectorAll("[data-q-pane-placeholder]")).toHaveLength(25 - 5 - MAX_MOUNTED);
    // Selecting a far set (a chip, Alt+PageDown) mounts and expands it.
    act(() => useTabs.getState().update("tab1", { resultIndex: 23 }));
    await waitFor(() => expect(handleGrid()?.dataset.set).toBe("23"));
    // Collapse and expand by the caption's button.
    const first = within(sections()[0]).getByRole("button", { name: "Collapse result set 1" });
    fireEvent.click(first);
    expect(within(sections()[0]).getByRole("button", { name: "Expand result set 1" })).toBeTruthy();
  });

  it("a pane's splitter resizes it by keyboard (a row per arrow, Home/End to the limits, Enter fits the rows)", async () => {
    setup(2, "stacked");
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    const sep = screen.getByRole("separator", { name: "Resize result set 1" });
    const body = sections()[0].querySelector<HTMLElement>("[data-q-pane-body]")!;
    const fit = Number(sep.getAttribute("aria-valuenow"));
    fireEvent.keyDown(sep, { key: "ArrowDown" });
    expect(parseInt(body.style.height)).toBeGreaterThan(fit);
    fireEvent.keyDown(sep, { key: "Home" });
    expect(sep.getAttribute("aria-valuenow")).toBe(sep.getAttribute("aria-valuemin"));
    fireEvent.keyDown(sep, { key: "Enter" });
    expect(Number(sep.getAttribute("aria-valuenow"))).toBe(fit);
  });

  it("staged changes in the selected set lock the others: they are dimmed and a click says why instead of switching", async () => {
    setup(2, "stacked", { edits: { 0: editInfo() } });
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    act(() => void stagingFor("e1", executionData.get("e1")!.resultSets[0], "mssql")!.addRow());
    await waitFor(() => expect(document.querySelector("[data-q-ledger-locked]")).toBeTruthy());
    fireEvent.pointerDown(sections()[1]);
    expect(useTabs.getState().tabs.tab1.resultIndex ?? 0).toBe(0);
    expect(toast).toHaveBeenCalledWith("info", "Apply or discard the staged changes to switch result sets");
    expect(sections()[1].querySelector(".cursor-not-allowed")).toBeTruthy();
    // The layout switch is gone while editing: the edit row replaces the tools.
    expect(switchGroup()).toBeNull();
  });

  it("Ctrl+click on a chip compares two sets beside each other, as in Tabs; stopping returns to the stack", async () => {
    setup(2, "stacked");
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    fireEvent.click(document.querySelector("[data-q-chip=s1]")!, { ctrlKey: true });
    await waitFor(() => expect(document.querySelector("[data-q-compare-pane=other]")).toBeTruthy());
    expect(layoutAttr()).toBe("tabs");
    fireEvent.click(screen.getByRole("button", { name: "Stop comparing" }));
    await waitFor(() => expect(layoutAttr()).toBe("stacked"));
  });
});

describe("Side by side", () => {
  it("shows up to three columns; a column's menu picks its set, swapping with the column that had it", async () => {
    setup(4, "side");
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(3));
    expect(sections().map((s) => s.dataset.qPaneKey)).toEqual(["e1:0", "e1:1", "e1:2"]);
    fireEvent.click(screen.getByRole("button", { name: /^Result set 1 · / }));
    fireEvent.click(screen.getByRole("menuitemcheckbox", { name: /Result set 4/ }));
    expect(useTabs.getState().tabs.tab1).toMatchObject({ sideSets: [3, 1, 2], resultIndex: 3 });
    await waitFor(() => expect(sections().map((s) => s.dataset.qPaneKey)).toEqual(["e1:3", "e1:1", "e1:2"]));
    fireEvent.click(screen.getByRole("button", { name: /^Result set 2 · / }));
    fireEvent.click(screen.getByRole("menuitemcheckbox", { name: /Result set 4/ }));
    expect(useTabs.getState().tabs.tab1.sideSets).toEqual([1, 3, 2]);
  });

  it("a set selected by a chip that no column shows takes the last column, and the columns are kept", async () => {
    setup(4, "side");
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(3));
    fireEvent.click(document.querySelector("[data-q-chip=s3]")!);
    await waitFor(() => expect(useTabs.getState().tabs.tab1.sideSets).toEqual([0, 1, 3]));
    fireEvent.pointerDown(sections()[0]);
    await waitFor(() => expect(handleGrid()?.dataset.set).toBe("0"));
    expect(sections().map((s) => s.dataset.qPaneKey)).toEqual(["e1:0", "e1:1", "e1:3"]);
  });

  it("Differences (two columns) tints the cells that differ and the footer counts them", async () => {
    setup(2, "side");
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    expect(grid.all.at(-1)!.tone).toBeUndefined();
    fireEvent.click(screen.getByRole("button", { name: /Differences/ }));
    await waitFor(() => expect(typeof grid.all.at(-1)!.tone).toBe("function"));
    expect(document.querySelector("[data-q-panel-footer]")?.textContent).toMatch(/No differences|differ/);
  });

  it("a narrow panel shows Stacked instead, and the switch says why", async () => {
    setup(2, "side");
    render(<LivePanel width="narrow" />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    expect(layoutAttr()).toBe("stacked");
    const side = within(switchGroup()!).getByRole("button", { name: "Side by side" });
    expect(side.getAttribute("title")).toBe(SIDE_TOO_NARROW);
    expect(side.getAttribute("aria-pressed")).toBe("true");
  });
});

describe("pins, maximize and Export all", () => {
  it("a pinned set stays on screen after the next run, first and read-only; unpinning removes it", async () => {
    setup(2, "stacked");
    const { rerender } = render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    fireEvent.click(screen.getByRole("button", { name: /^Pin result set 2/ }));
    expect(useTabs.getState().tabs.tab1.pins).toEqual([{ executionId: "e1", resultIndex: 1 }]);
    // The next run of the tab.
    useExecutions.setState({ views: { ...useExecutions.getState().views, e2: view("e2", { resultSetCount: 2 }) } });
    registerExecution("e2", ...sets(2));
    act(() => useTabs.getState().update("tab1", { executions: ["e1", "e2"], activeExecutionId: "e2", resultIndex: 0 }));
    rerender(<LivePanel />);
    await waitFor(() => expect(sections().map((s) => s.dataset.qPaneKey)).toEqual(["e1:1", "e2:0", "e2:1"]));
    expect(sections()[0].textContent).toContain("Pinned · Run 1 · Result set 2");
    expect(sections()[0].textContent).toContain("read-only");
    // A pinned set of another run is never the selected one: clicking it changes nothing.
    fireEvent.pointerDown(sections()[0]);
    expect(useTabs.getState().tabs.tab1.resultIndex).toBe(0);
    expect(handleGrid()?.closest("[data-q-pane-key]")?.getAttribute("data-q-pane-key")).toBe("e2:0");
    fireEvent.click(within(sections()[0]).getByRole("button", { name: /^Unpin result set 2/ }));
    await waitFor(() => expect(sections()).toHaveLength(2));
  });

  it("in Tabs, Pin is in the chip menu but unavailable, with the reason", async () => {
    setup(2);
    render(<LivePanel />);
    await screen.findByTestId("grid");
    fireEvent.contextMenu(document.querySelector("[data-q-chip=s1]")!);
    const pin = screen.getByRole("menuitem", { name: /Pin/ });
    expect(pin.getAttribute("aria-disabled")).toBe("true");
  });

  it("a set shown alone (maximize) gets Back to Stacked; Esc returns to the stack", async () => {
    setup(3, "stacked");
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(3));
    fireEvent.click(within(sections()[1]).getByRole("button", { name: "Show result set 2 alone" }));
    await waitFor(() => expect(layoutAttr()).toBe("tabs"));
    expect(useTabs.getState().tabs.tab1).toMatchObject({ resultIndex: 1, resultMaximized: true });
    expect(screen.getAllByTestId("grid")).toHaveLength(1);
    expect(screen.getByRole("button", { name: /Back to Stacked/ })).toBeTruthy();
    // Esc in a field (the filter) belongs to the field.
    fireEvent.keyDown(screen.getByPlaceholderText(/Filter/), { key: "Escape" });
    expect(useTabs.getState().tabs.tab1.resultMaximized).toBe(true);
    fireEvent.keyDown(document.querySelector("[data-q-results]")!, { key: "Escape" });
    await waitFor(() => expect(layoutAttr()).toBe("stacked"));
  });

  it("Export offers every result set in one file; one set offers no such entry", async () => {
    setup(3);
    render(<LivePanel />);
    await screen.findByTestId("grid");
    fireEvent.click(screen.getByRole("button", { name: /^Export/ }));
    fireEvent.click(screen.getByRole("menuitem", { name: "All 3 result sets: Excel (.xlsx), a sheet per result set" }));
    expect(exportAll).toHaveBeenCalledWith(executionData.get("e1")!.resultSets, "xlsx", "orders");
    cleanup();
    executionData.clear();
    setup(1);
    render(<LivePanel />);
    await screen.findByTestId("grid");
    fireEvent.click(screen.getByRole("button", { name: /^Export/ }));
    expect(screen.queryByRole("menuitem", { name: /All \d+ result sets/ })).toBeNull();
  });
});

describe("every engine: the layouts never depend on the engine", () => {
  it.each(Object.keys(ENGINES) as Engine[])("%s: Stacked and Side by side lay out the sets; edit stays on the selected set", async (engine) => {
    useProfiles.setState({ profiles: [{ id: "p1", engine, environment: "dev" } as never] });
    setup(2, "stacked", { edits: { 0: editInfo() } });
    render(<LivePanel />);
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    expect(layoutAttr()).toBe("stacked");
    expect(handleGrid()?.dataset.set).toBe("0");
    act(() => useTabs.getState().update("tab1", { resultLayout: "side" }));
    await waitFor(() => expect(layoutAttr()).toBe("side"));
    expect(screen.getAllByTestId("grid")).toHaveLength(2);
  });
});

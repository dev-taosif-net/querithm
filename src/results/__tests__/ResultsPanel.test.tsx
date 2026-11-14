// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";

const { grid, chart, exportResultSet, actions, edit, toast, reveal } = vi.hoisted(() => ({
  grid: { props: null as Record<string, unknown> | null, all: [] as Record<string, unknown>[], copy: vi.fn((_h: unknown): string | null => "a\tb"), selected: vi.fn(() => [{ kind: "existing", row: 1 }]) },
  chart: { props: null as Record<string, unknown> | null },
  exportResultSet: vi.fn(async (..._a: unknown[]) => {}),
  actions: { runQuery: vi.fn(), setBottomTab: vi.fn(), showRun: vi.fn() },
  edit: { applyChanges: vi.fn(async (..._a: unknown[]) => true), reviewChanges: vi.fn(async (..._a: unknown[]) => true), scheduleTokens: vi.fn() },
  toast: vi.fn(),
  reveal: vi.fn(),
}));

vi.mock("../ResultGrid", async (orig) => {
  const actual = await orig<typeof import("../ResultGrid")>();
  return {
    ...actual,
    ResultGrid: (props: { handleRef?: { current: unknown } }) => {
      grid.all.push(props as never);
      if (props.handleRef) {
        grid.props = props as never;
        props.handleRef.current = { copySelection: grid.copy, selectedRefs: grid.selected };
      }
      return <div data-testid="grid" />;
    },
  };
});
vi.mock("../viewers/CellViewerPanel", () => ({
  CellViewerPanel: (props: { row: number; col: number; onClose: () => void }) => (
    <aside data-testid="viewer">
      row {props.row} col {props.col}
      <button onClick={props.onClose}>close viewer</button>
    </aside>
  ),
}));
vi.mock("../../charts/ChartView", () => ({
  ChartView: (props: Record<string, unknown>) => {
    chart.props = props;
    return <div data-testid="chart" />;
  },
}));
vi.mock("../export", () => ({
  exportFormats: [{ format: "csv", label: "CSV" }, { format: "json", label: "JSON" }],
  exportResultSet,
}));
vi.mock("../../app/actions", () => actions);
vi.mock("../reveal", () => ({ revealEditorLine: reveal }));
vi.mock("../gridEdit", async (orig) => ({ ...(await orig<typeof import("../gridEdit")>()), ...edit }));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast }));

import type { ServerMessage } from "../../ipc/contract";
import { useExecutions } from "../../store/executions";
import { useProfiles } from "../../store/profiles";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";
import { executionData } from "../model";
import { Empty, ResultsPanel, useExecutionData } from "../ResultsPanel";
import { stagings } from "../staging";
import { stagingFor } from "../gridEdit";
import { editInfo, makeBatch, ordersSet, registerExecution, view } from "./fixtures";
import { openRowPopups } from "../../app/__tests__/rowPopups";
import { Int32, Utf8 } from "apache-arrow";
import { renderHook } from "@testing-library/react";

// ResultsPanel lazy-loads ResultGrid; the mock pulls in the real module (Glide), which under full-suite load can take
// longer than findByTestId's 1s default on the first render. Warm the module cache once so each lazy() resolves at once.
beforeAll(async () => {
  await import("../ResultGrid");
});

const tab = (over = {}) => ({ id: "tab1", title: "orders.sql", profileId: "p1", sessionId: "sess1", activeExecutionId: "e1", executions: ["e1"], ...over }) as never;
/** The panel as the app renders it: re-rendered with the tab from the store. */
function LivePanel() {
  const t = useTabs((s) => s.tabs.tab1);
  return <ResultsPanel tab={t} />;
}
const err = (message: string, over: Partial<ServerMessage> = {}) => ({ executionId: "e1", severity: "error", message, batchIndex: 0, ts: 1, ...over }) as ServerMessage;
const info = (message: string) => ({ executionId: "e1", severity: "info", message, batchIndex: 0, ts: 1 }) as ServerMessage;
const menuItem = (name: string | RegExp) => screen.getByRole("menuitem", { name });

beforeEach(() => {
  stagings.clear();
  useUi.setState({ dialogs: [] });
  executionData.clear();
  useExecutions.setState({ views: {} });
  useProfiles.setState({ profiles: [{ id: "p1", engine: "mssql", environment: "dev" } as never] });
  useTabs.setState({ tabs: { tab1: tab() } });
  Object.assign(navigator, { clipboard: { writeText: vi.fn(async () => {}) } });
  vi.clearAllMocks();
  grid.all = [];
  grid.copy.mockReturnValue("a\tb");
});
afterEach(cleanup);

describe("Empty / useExecutionData", () => {
  it("Empty renders its text", () => {
    render(<Empty text="nothing" />);
    expect(screen.getByText("nothing")).toBeTruthy();
  });
  it("useExecutionData returns the registered data or undefined", () => {
    registerExecution("e1", ordersSet());
    expect(renderHook(() => useExecutionData("e1")).result.current).toBe(executionData.get("e1"));
    expect(renderHook(() => useExecutionData("zz")).result.current).toBeUndefined();
    expect(renderHook(() => useExecutionData(undefined)).result.current).toBeUndefined();
  });
});

describe("ResultsPanel: runs without result sets show their summary in place (the view never switches)", () => {
  it("prompts to run a query when there is no execution", () => {
    render(<ResultsPanel tab={tab({ activeExecutionId: undefined })} />);
    expect(screen.getByText(/Run a query/)).toBeTruthy();
  });

  it.each([
    ["queued", {}, /Queued/],
    ["running", {}, /Executing/],
    ["streaming", {}, /Executing/],
    ["done", { affectedRows: 5000 }, /5,000 rows affected/],
    ["done", {}, /returned no result set/],
    ["cancelled", {}, /Cancelled before any result set/],
  ])("state %s", (state, extra, re) => {
    useExecutions.setState({ views: { e1: view("e1", { state: state as never, ...extra }) } });
    registerExecution("e1");
    render(<ResultsPanel tab={tab()} />);
    expect(screen.getByText(re)).toBeTruthy();
  });

  it("PRINT output and row counts show in the summary, with a way to Messages", () => {
    useExecutions.setState({ views: { e1: view("e1", { messages: [info("(3 rows affected)"), info("done printing")] }) } });
    registerExecution("e1");
    render(<ResultsPanel tab={tab()} />);
    expect(screen.getByText("done printing")).toBeTruthy();
    fireEvent.click(screen.getByText("Show in Messages"));
    expect(actions.setBottomTab).toHaveBeenCalledWith("tab1", "messages");
    expect(screen.getByText("No result sets")).toBeTruthy();
  });

  it("a run that failed before any result set shows its error card: details, go to line, copy", async () => {
    useExecutions.setState({ views: { e1: view("e1", { state: "failed", messages: [err("Invalid column name 'IssuedOn'.", { number: 207, class: 16, state: 1, line: 3, editorLine: 3 })] }) } });
    registerExecution("e1");
    render(<ResultsPanel tab={tab()} />);
    const card = document.querySelector("[data-q-error-card]") as HTMLElement;
    expect(within(card).getByText("Error at line 3")).toBeTruthy();
    expect(within(card).getByText("Msg 207, Level 16, State 1, Line 3")).toBeTruthy();
    fireEvent.click(within(card).getByText("Go to line 3"));
    expect(reveal).toHaveBeenCalledWith("tab1", 3);
    fireEvent.click(within(card).getByText("Copy error"));
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith("Invalid column name 'IssuedOn'."));
  });
});

describe("ResultsPanel with data", () => {
  function setup(sets = [ordersSet()], viewOver = {}) {
    useExecutions.setState({ views: { e1: view("e1", viewOver) } });
    registerExecution("e1", ...sets);
  }
  function second(truncated = true) {
    const rs = new (ordersSet(1).constructor as typeof import("../model").ArrowResultSet)(1);
    rs.append(makeBatch([{ name: "x", type: new Int32(), values: [1] }]));
    rs.finish(truncated);
    return rs;
  }

  it("one 28 px row holds the ledger and the tools; the footer names the shown set, the Arrow details are a tooltip", async () => {
    setup();
    const { container } = render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    const row = container.querySelector("[data-q-panel-row]") as HTMLElement;
    expect(row.className.split(/\s+/)).toContain("h-[28px]");
    expect(row.className).not.toMatch(/\bflex-wrap\b/); // never wraps; overflow goes into menus
    expect(screen.getByRole("tab", { name: "Result set 1, 3 rows" })).toBeTruthy();
    const foot = screen.getByText(/Result set 1 · 3 rows · 3 columns/);
    expect(foot.getAttribute("title")).toBe("Arrow stream · 1 batch · 3 columns");
    expect(screen.getByPlaceholderText("Filter 3 rows…")).toBeTruthy();
    // Copy is one split button; Export is a menu. No "Compare with…" button (owner, 2026-11-14): compare starts from a
    // chip (Ctrl+click or its menu).
    expect(screen.getByRole("button", { name: "Copy" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Copy as" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Export" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Compare with…" })).toBeNull();
    expect(container.querySelector(".text-faint")).toBeNull();
  });

  it("streaming: the chip and the footer say so", async () => {
    setup([ordersSet(0, false)], { state: "streaming" });
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    expect(screen.getByText(/3 rows · 3 columns · streaming…/)).toBeTruthy();
    expect(screen.getByRole("tab", { name: "Result set 1, 3 rows, still loading" })).toBeTruthy();
  });

  it("the ledger lists result sets and errors in the order they happened, and switches between them", async () => {
    setup([ordersSet(0), second()], { messages: [info("a"), err("Invalid object name 'dbo.Invoce'.", { editorLine: 12, number: 208, class: 16 })], setAfter: { 0: 0, 1: 1 } });
    render(<LivePanel />);
    await screen.findByTestId("grid");
    const chips = within(screen.getByRole("tablist", { name: "Result sets and errors" })).getAllByRole("tab");
    expect(chips.map((c) => c.getAttribute("aria-label"))).toEqual([
      "Result set 1, 3 rows",
      "Result set 2, 1 row, row limit reached",
      "Error 1, line 12: Invalid object name 'dbo.Invoce'.",
    ]);
    fireEvent.click(chips[1]);
    await waitFor(() => expect((grid.props as { rs: { index: number } }).rs.index).toBe(1));
    expect(useTabs.getState().tabs.tab1.resultIndex).toBe(1);
    // ←/→ move along the ledger and select.
    fireEvent.keyDown(screen.getByRole("tab", { name: /Result set 2/ }), { key: "ArrowRight" });
    expect(useTabs.getState().tabs.tab1.ledgerError).toBe(0);
    await waitFor(() => expect(document.querySelector("[data-q-error-card]")?.textContent).toContain("Error at line 12"));
    expect(screen.queryByTestId("grid")).toBeNull();
  });

  it("an error after result sets: the sets stay, a notice line appears once the run has ended, and it can be dismissed", async () => {
    setup([ordersSet(0)], { state: "failed", messages: [err("Invalid object name 'dbo.Invoce'.", { editorLine: 12, number: 208, class: 16 })], setAfter: { 0: 0 } });
    render(<LivePanel />);
    await screen.findByTestId("grid");
    const notice = document.querySelector("[data-q-notice='error']") as HTMLElement;
    expect(within(notice).getByText("Error at line 12")).toBeTruthy();
    expect(within(notice).getByText("Msg 208, Level 16 · Invalid object name 'dbo.Invoce'.")).toBeTruthy();
    fireEvent.click(within(notice).getByText("Go to line 12"));
    expect(reveal).toHaveBeenCalledWith("tab1", 12);
    fireEvent.click(within(notice).getByText("Show in Messages"));
    expect(actions.setBottomTab).toHaveBeenCalledWith("tab1", "messages");
    fireEvent.click(screen.getByRole("button", { name: "Dismiss notice" }));
    expect(useTabs.getState().tabs.tab1.noticeDismissed).toBe("e1");
    await waitFor(() => expect(document.querySelector("[data-q-notice]")).toBeNull());
  });

  it("several notices share the one line: the most important first, \"1 of 2\" steps to the next", async () => {
    const cut = ordersSet(0, false);
    cut.finish(true);
    setup([cut], { messages: [err("boom", { editorLine: 4 })], setAfter: { 0: 0 } });
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    expect(document.querySelector("[data-q-notice]")?.getAttribute("data-q-notice")).toBe("error");
    fireEvent.click(screen.getByText("1 of 2"));
    expect(document.querySelector("[data-q-notice]")?.getAttribute("data-q-notice")).toBe("limit");
    expect(screen.getByText("2 of 2")).toBeTruthy();
  });

  it("no notice while the run is still going (it would move the grid mid-stream)", async () => {
    setup([ordersSet(0, false)], { state: "streaming", messages: [err("boom")], setAfter: { 0: 0 } });
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    expect(document.querySelector("[data-q-notice]")).toBeNull();
  });

  it("the row-limit notice fetches all rows; copy and export of a cut-off set warn that they hold the fetched rows only", async () => {
    const rs = ordersSet(0, false);
    rs.finish(true);
    setup([rs]);
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    expect(screen.getByText(/row limit reached/)).toBeTruthy();
    fireEvent.click(screen.getByText("Fetch all rows"));
    expect(useTabs.getState().tabs.tab1.maxRows).toBe(0);
    expect(actions.runQuery).toHaveBeenCalledWith("tab1", { options: { maxRows: 0 } });
    fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    await waitFor(() => expect(toast).toHaveBeenCalledWith("warn", expect.stringContaining("the copy holds the fetched rows only")));
    fireEvent.click(screen.getByRole("button", { name: "Export" }));
    fireEvent.click(menuItem("CSV"));
    await waitFor(() => expect(toast).toHaveBeenCalledWith("warn", expect.stringContaining("Exported the first 3 rows")));
  });

  it("a chip shows the change in rows since the previous run of the same SQL", async () => {
    useExecutions.setState({ views: { e0: view("e0", { sql: "select * from orders" }), e1: view("e1", { sql: "select * from orders" }) } });
    const before = new (ordersSet().constructor as typeof import("../model").ArrowResultSet)(0);
    before.append(makeBatch([{ name: "id", type: new Int32(), values: [1] }]));
    before.finish(false);
    registerExecution("e0", before);
    registerExecution("e1", ordersSet());
    render(<ResultsPanel tab={tab({ executions: ["e0", "e1"] })} />);
    await screen.findByTestId("grid");
    expect(screen.getByRole("tab", { name: "Result set 1, 3 rows, +2 since the previous run" }).textContent).toContain("+2");
  });

  it("an older run is read-only, says so in the notice line, and offers the latest", async () => {
    setup([ordersSet()], { edits: { 0: editInfo() } });
    useExecutions.setState({ views: { ...useExecutions.getState().views, e2: view("e2") } });
    render(<ResultsPanel tab={tab({ executions: ["e1", "e2"] })} />);
    await screen.findByTestId("grid");
    expect(screen.getByRole("button", { name: "Read-only" }).getAttribute("title")).toBe("Read-only: this is an older run — edit the latest result");
    const notice = document.querySelector("[data-q-notice='older']") as HTMLElement;
    expect(notice.textContent).toContain("Run 1 of 2");
    fireEvent.click(within(notice).getByText("Back to latest"));
    expect(actions.showRun).toHaveBeenCalledWith("tab1", "e2");
  });

  it("compare: Ctrl+click a second chip shows it beside the shown one, marks differing cells and counts them", async () => {
    setup([ordersSet(0), second(false)]);
    render(<LivePanel />);
    await screen.findByTestId("grid");
    fireEvent.click(screen.getByRole("tab", { name: /Result set 2/ }), { ctrlKey: true });
    expect(useTabs.getState().tabs.tab1.compare).toEqual({ executionId: "e1", resultIndex: 1 });
    await waitFor(() => expect(screen.getAllByTestId("grid")).toHaveLength(2));
    expect(screen.getByText(/This run · result set 2 · 1 row · read-only/)).toBeTruthy();
    expect(screen.getByText(/cells differ · rows compared by position/)).toBeTruthy();
    const tone = (grid.props as { tone: (r: number, c: number) => string | null }).tone;
    expect(tone(2, 0)).toBe("changed"); // the other side has one row only
    expect(screen.getByRole("tab", { name: /Result set 2, 1 row, compared/ })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Stop comparing" }));
    expect(useTabs.getState().tabs.tab1.compare).toBeUndefined();
  });

  it("compare starts from a chip's menu (Shift+F10)", async () => {
    useExecutions.setState({ views: { e0: view("e0"), e1: view("e1") } });
    registerExecution("e0", ordersSet(0));
    registerExecution("e1", ordersSet(0), second(false));
    render(<LivePanel />);
    useTabs.setState({ tabs: { tab1: tab({ executions: ["e0", "e1"] }) } });
    await screen.findByTestId("grid");
    fireEvent.keyDown(screen.getByRole("tab", { name: /Result set 2/ }), { key: "F10", shiftKey: true });
    fireEvent.click(menuItem(/Compare with the shown result set/));
    expect(useTabs.getState().tabs.tab1.compare).toEqual({ executionId: "e1", resultIndex: 1 });
  });

  it("Grid | Chart: the chart replaces the grid for that result set and the choice is kept in the tab", async () => {
    setup();
    render(<LivePanel />);
    await screen.findByTestId("grid");
    const group = screen.getByRole("group", { name: "Show the result as" });
    expect(within(group).getByRole("button", { name: /Grid/ }).getAttribute("aria-pressed")).toBe("true");
    fireEvent.click(within(group).getByRole("button", { name: /Chart/ }));
    await screen.findByTestId("chart");
    expect(screen.queryByTestId("grid")).toBeNull();
    expect(useTabs.getState().tabs.tab1.charts?.[0]).toEqual({ view: "chart", spec: null });
    // Filter and copy act on the grid only; the rows can still be exported.
    expect(screen.queryByPlaceholderText(/Filter/)).toBeNull();
    expect(screen.queryByRole("button", { name: "Copy" })).toBeNull();
    expect(screen.getByRole("button", { name: "Export rows" })).toBeTruthy();
    (chart.props!.onSpecChange as (s: unknown) => void)({ kind: "bar", x: "name", y: ["qty"], series: null, agg: "sum" });
    expect(useTabs.getState().tabs.tab1.charts?.[0].spec).toMatchObject({ kind: "bar", x: "name" });
    fireEvent.click(within(screen.getByRole("group", { name: "Show the result as" })).getByRole("button", { name: /Grid/ }));
    await screen.findByTestId("grid");
    expect(useTabs.getState().tabs.tab1.charts?.[0]).toMatchObject({ view: "grid", spec: { kind: "bar" } });
  });

  it("activating a cell opens the side viewer for it; re-sorting or closing removes it", async () => {
    setup();
    render(<LivePanel />);
    await screen.findByTestId("grid");
    act(() => (grid.props!.onViewCell as (r: number, c: number) => void)(2, 1));
    expect((await screen.findByTestId("viewer")).textContent).toContain("row 2 col 1");
    fireEvent.click(screen.getByText("close viewer"));
    expect(screen.queryByTestId("viewer")).toBeNull();
    act(() => (grid.props!.onViewCell as (r: number, c: number) => void)(0, 0));
    await screen.findByTestId("viewer");
    // The view row refers to the old order after a sort: the panel goes rather than show another row.
    act(() => executionData.get("e1")!.resultSets[0].setSort(0, "desc"));
    await waitFor(() => expect(screen.queryByTestId("viewer")).toBeNull());
  });

  it("staged grid edits keep the grid: Chart is unavailable until they are applied or discarded", async () => {
    setup([ordersSet()], { edits: { 0: editInfo() } });
    useTabs.setState({ tabs: { tab1: tab({ charts: { 0: { view: "chart", spec: null } } }) } });
    render(<LivePanel />);
    await screen.findByTestId("chart");
    const rs = executionData.get("e1")!.resultSets[0];
    act(() => void stagingFor("e1", rs, "mssql")!.addRow());
    await screen.findByTestId("grid");
    // Edit mode replaces the tool row: no Grid | Chart switch until the changes are applied or discarded.
    expect(screen.queryByRole("group", { name: "Show the result as" })).toBeNull();
    expect(useTabs.getState().tabs.tab1.charts?.[0].view).toBe("chart"); // the choice is kept for afterwards
    // The ledger is locked while changes are staged, and says why.
    expect(document.querySelector("[data-q-ledger-locked]")?.getAttribute("title")).toMatch(/Apply or discard the staged changes/);
  });

  it("filters rows after a debounce; the footer counts what is shown", async () => {
    setup();
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    const rs = executionData.get("e1")!.resultSets[0];
    fireEvent.change(screen.getByPlaceholderText(/Filter/), { target: { value: "two" } });
    await waitFor(() => expect(rs.filter).toBe("two"));
    expect(rs.visibleRows).toBe(1);
    await waitFor(() => expect(screen.getByText(/1 of 3 rows/)).toBeTruthy());
  });

  it("filter column picker, the filter box's prefix: choose, toggle, search and reset", async () => {
    const wide = new (ordersSet().constructor as typeof import("../model").ArrowResultSet)(0);
    wide.append(makeBatch(Array.from({ length: 10 }, (_, i) => ({ name: `col${i}`, type: new Utf8(), values: ["a"] }))));
    wide.finish(false);
    setup([wide]);
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    fireEvent.click(screen.getByRole("button", { name: "Filter columns: All columns" }));
    const list = screen.getByRole("listbox");
    fireEvent.change(within(list).getByPlaceholderText("Find column…"), { target: { value: "col3" } });
    expect(within(list).queryByText("col4")).toBeNull();
    fireEvent.click(within(list).getByText("col3"));
    expect(wide.filterColumns).toEqual([3]);
    fireEvent.change(within(list).getByPlaceholderText("Find column…"), { target: { value: "col" } });
    fireEvent.click(within(list).getByText("col5"));
    expect(wide.filterColumns).toEqual([3, 5]);
    expect(screen.getByRole("button", { name: "Filter columns: 2 columns" })).toBeTruthy();
    fireEvent.click(within(list).getByText("col3"));
    fireEvent.click(within(list).getByText("col5"));
    expect(wide.filterColumns).toBeNull();
    fireEvent.click(within(list).getByText("col1"));
    fireEvent.click(within(list).getAllByRole("checkbox")[0]); // All columns
    expect(wide.filterColumns).toBeNull();
    fireEvent.change(within(list).getByPlaceholderText("Find column…"), { target: { value: "zzz" } });
    expect(within(list).getByText("No matching column")).toBeTruthy();
    fireEvent.keyDown(document.body, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("listbox")).toBeNull());
  });

  it("filter column list opens outside the 28 px tools row, which clips what overflows it; a press inside keeps it open", async () => {
    setup();
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    const rs = executionData.get("e1")!.resultSets[0];
    const button = screen.getByRole("button", { name: "Filter columns: All columns" });
    expect(button.closest("[data-q-panel-row]")).toBeTruthy();
    fireEvent.click(button);
    const list = screen.getByRole("listbox");
    expect(list.closest("[data-q-panel-row]")).toBeNull();
    expect(list.className).toContain("fixed");
    const first = within(list).getAllByRole("checkbox")[1];
    fireEvent.pointerDown(first);
    fireEvent.click(first);
    expect(rs.filterColumns).toEqual([0]);
    expect(screen.getByRole("listbox")).toBeTruthy();
    fireEvent.pointerDown(document.body);
    await waitFor(() => expect(screen.queryByRole("listbox")).toBeNull());
  });

  it("every popup of the tool row opens outside the row", async () => {
    setup();
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    expect(openRowPopups()).toEqual(expect.arrayContaining(["Filter columns: All columns", "Copy as", "Export"]));
  });

  it("Copy copies the grid selection and asks for one when there is none; Copy as offers headers, IN and Markdown", async () => {
    setup();
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith("a\tb"));
    expect(grid.copy).toHaveBeenLastCalledWith(false);
    expect(toast).toHaveBeenCalledWith("success", "Copied to clipboard.");
    for (const [label, format, said] of [["Copy with headers", true, "Copied with headers."], ["Copy as IN (…) clause", "in", "Copied as IN clause."], ["Copy as Markdown table", "markdown", "Copied as Markdown table."]] as const) {
      fireEvent.click(screen.getByRole("button", { name: "Copy as" }));
      fireEvent.click(menuItem(label));
      expect(grid.copy).toHaveBeenLastCalledWith(format);
      await waitFor(() => expect(toast).toHaveBeenCalledWith("success", said));
    }
    grid.copy.mockReturnValue(null);
    fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    await waitFor(() => expect(toast).toHaveBeenCalledWith("info", "Select cells, rows or columns first."));
  });

  it("filter input has a clear button and handles Escape", async () => {
    setup();
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    const input = screen.getByPlaceholderText(/Filter \d+ rows…/);
    fireEvent.change(input, { target: { value: "test" } });
    fireEvent.click(screen.getByLabelText("Clear filter"));
    expect((input as HTMLInputElement).value).toBe("");
    fireEvent.change(input, { target: { value: "another" } });
    fireEvent.keyDown(input, { key: "Escape" });
    expect((input as HTMLInputElement).value).toBe("");
  });

  it("selection statistics sit in the footer next to the data: Count, NULL, Distinct, Sum, Avg, Min, Max", async () => {
    setup();
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    act(() => (grid.props as { onStatsChange: (s: unknown) => void }).onStatsChange({ count: 5, nullCount: 1, distinct: 4, numericCount: 4, sum: 150, avg: 37.5, min: 10, max: 50 }));
    const line = document.querySelector("[data-q-selection-stats]") as HTMLElement;
    expect(line.textContent).toBe("Count 5·NULL 1·Distinct 4·Sum ≈ 150·Avg ≈ 37.5·Min 10·Max 50");
    act(() => (grid.props as { onStatsChange: (s: unknown) => void }).onStatsChange({ count: 60_000, sampled: true, nullCount: 2, distinct: 3, numericCount: 0, sum: 0, avg: 0, min: 0, max: 0 }));
    expect((document.querySelector("[data-q-selection-stats]") as HTMLElement).textContent).toContain("NULL ≈ 2");
    expect(screen.getByText("first 50,000 cells")).toBeTruthy();
  });

  it("Export runs the chosen format with a file name from the tab title; failures toast", async () => {
    setup();
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    fireEvent.click(screen.getByRole("button", { name: "Export" }));
    fireEvent.click(menuItem("JSON"));
    expect(exportResultSet).toHaveBeenCalledWith(expect.anything(), "json", "orders-result1");
    exportResultSet.mockRejectedValueOnce(new Error("disk full"));
    fireEvent.click(screen.getByRole("button", { name: "Export" }));
    fireEvent.click(menuItem("CSV"));
    await waitFor(() => expect(toast).toHaveBeenCalledWith("error", "Error: disk full"));
  });
});

describe("ResultsPanel edit mode", () => {
  const ex = (row: number) => ({ kind: "existing" as const, row });
  function setup(edits: Record<number, unknown>, engine: "mssql" | "pg" = "mssql", complete = true, environment = "dev") {
    useProfiles.setState({ profiles: [{ id: "p1", engine, environment } as never] });
    useExecutions.setState({ views: { e1: view("e1", { edits: edits as never }) } });
    registerExecution("e1", ordersSet(0, complete));
  }
  async function openEditing() {
    render(<ResultsPanel tab={tab()} />);
    await screen.findByTestId("grid");
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
  }

  it("the edit tools replace the tool row (no extra height) and keep the filter; the environment shows as a word and a 3 px rule", async () => {
    setup({ 0: editInfo() });
    await openEditing();
    const row = document.querySelector("[data-q-panel-row]") as HTMLElement;
    expect(row.className.split(/\s+/)).toContain("h-[28px]");
    expect(document.querySelectorAll("[data-q-panel-row]")).toHaveLength(1);
    expect(within(row).getByText("DEV")).toBeTruthy();
    expect(within(row).getByText("dbo.orders")).toBeTruthy();
    expect(within(row).getByText("No changes")).toBeTruthy();
    expect(within(row).getByPlaceholderText("Filter 3 rows…")).toBeTruthy();
    expect(document.querySelector("[data-q-env-rule]")).toBeTruthy();
    expect(screen.getByText(/Type or double-click to edit/).closest("[data-q-panel-footer]")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByText("No changes")).toBeNull();
    expect((grid.props as { staging?: unknown }).staging).toBeUndefined();
  });

  it("every popup of the edit tool row opens outside the row", async () => {
    setup({ 0: editInfo() });
    await openEditing();
    expect(openRowPopups()).toEqual(expect.arrayContaining(["Filter columns: All columns", "More edit actions"]));
  });

  for (const engine of ["mssql", "pg"] as const) {
    it(`read-only results say why instead of offering Edit (${engine})`, async () => {
      setup({ 0: editInfo({ editable: false, reason: "joins several tables" }) }, engine);
      render(<ResultsPanel tab={tab()} />);
      await screen.findByTestId("grid");
      expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
      expect(screen.getByRole("button", { name: "Read-only" }).getAttribute("title")).toBe("Read-only: joins several tables");
      expect((grid.props as { staging?: unknown }).staging).toBeUndefined();
    });

    it(`editable results open read-only until Edit is clicked (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      render(<ResultsPanel tab={tab()} />);
      await screen.findByTestId("grid");
      expect(screen.queryByRole("button", { name: "Apply" })).toBeNull();
      expect((grid.props as { staging?: unknown }).staging).toBeUndefined();
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      expect(screen.getByRole("button", { name: "Apply" })).toBeTruthy();
      expect((grid.props as { staging?: unknown }).staging).toBe(stagings.get("e1:0"));
    });

    it(`an interrupted result set stays read-only (${engine})`, async () => {
      setup({ 0: editInfo() }, engine, false);
      act(() => executionData.get("e1")!.finishStream());
      render(<ResultsPanel tab={tab()} />);
      await screen.findByTestId("grid");
      expect(screen.getByText(/incomplete — execution stopped/)).toBeTruthy();
      expect(screen.getByRole("button", { name: "Read-only" }).getAttribute("title")).toMatch(/stopped before every row arrived/);
    });

    it(`Cancel is the one way out, available with or without staged changes; there is no Done (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      await openEditing();
      expect(screen.queryByRole("button", { name: "Done" })).toBeNull();
      const cancel = () => screen.getByRole("button", { name: "Cancel" });
      expect(cancel().getAttribute("aria-disabled")).toBeNull();
      expect(cancel().getAttribute("title")).toBe("Leave edit mode (back to the read-only grid)");
      act(() => void stagings.get("e1:0")!.setCell(ex(0), 1, { value: "uno" }));
      expect(cancel().getAttribute("aria-disabled")).toBeNull();
      expect(cancel().getAttribute("title")).toBe("Discard the 1 staged change and leave edit mode…");
      expect(screen.queryByRole("button", { name: "Done" })).toBeNull();
    });

    it(`the edit buttons keep one order in every state (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      await openEditing();
      const names = () => [...document.querySelectorAll('[data-q-panel-row] [role="toolbar"] button')].map((b) => b.getAttribute("aria-label") ?? b.textContent).filter((n) => n && !/Filter|Clear filter/.test(n));
      const clean = names();
      expect(clean).toEqual(["Row", "Duplicate", "Delete rows", "Undo (Ctrl+Z)", "Redo (Ctrl+Y)", "More edit actions", "Apply", "Cancel"]);
      act(() => void stagings.get("e1:0")!.setCell(ex(0), 1, { value: "waytoolong" })); // staged and invalid
      expect(names()).toEqual(clean);
      expect(screen.getByRole("button", { name: "Apply" }).getAttribute("title")).toBe("Fix 1 invalid value first (marked red)");
    });

    it(`Cancel leaves at once with nothing staged (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      await openEditing();
      fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
      await waitFor(() => expect(screen.queryByRole("button", { name: "Apply" })).toBeNull());
      expect(useUi.getState().dialogs).toHaveLength(0);
    });

    it(`Cancel with staged changes asks first: Keep editing keeps them, Discard and leave drops them (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      await openEditing();
      const staging = stagings.get("e1:0")!;
      act(() => void staging.setCell(ex(0), 1, { value: "uno" }));
      fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
      let d = useUi.getState().dialogs.at(-1) as { kind: string; title: string; ok: string; cancel: string; danger: boolean; resolve: (ok: boolean) => void };
      expect(d).toMatchObject({ kind: "confirm", title: "Discard 1 staged change to dbo.orders?", ok: "Discard and leave", cancel: "Keep editing", danger: true });
      await act(async () => d.resolve(false));
      expect(staging.hasChanges).toBe(true);
      expect(screen.getByRole("button", { name: "Apply" })).toBeTruthy();
      fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
      d = useUi.getState().dialogs.at(-1) as typeof d;
      await act(async () => d.resolve(true));
      expect(staging.hasChanges).toBe(false);
      await waitFor(() => expect(screen.queryByRole("button", { name: "Apply" })).toBeNull());
    });

    it(`an Apply started anywhere (Ctrl+S) shows Applying… and locks the row (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      await openEditing();
      const staging = stagings.get("e1:0")!;
      act(() => {
        staging.setCell(ex(0), 1, { value: "uno" });
        staging.setBusy(true);
      });
      expect(screen.getByRole("button", { name: "Applying…" }).getAttribute("aria-disabled")).toBe("true");
      for (const name of ["Cancel", "Row", "Delete rows"]) expect(screen.getByRole("button", { name }).getAttribute("title")).toBe("Wait for Apply to finish");
      act(() => staging.setBusy(false));
      expect(screen.getByRole("button", { name: "Apply" }).getAttribute("aria-disabled")).toBeNull();
    });

    it(`edit mode ends when another result set is shown: coming back is read-only again (${engine})`, async () => {
      useProfiles.setState({ profiles: [{ id: "p1", engine, environment: "dev" } as never] });
      useExecutions.setState({ views: { e1: view("e1", { resultSetCount: 2, edits: { 0: editInfo(), 1: editInfo({ table: "lines" }) } as never }) } });
      registerExecution("e1", ordersSet(0), ordersSet(1));
      useTabs.setState({ tabs: { tab1: tab() } });
      render(<LivePanel />);
      await screen.findByTestId("grid");
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      expect(screen.getByRole("button", { name: "Apply" })).toBeTruthy();
      act(() => useTabs.getState().update("tab1", { resultIndex: 1 }));
      expect(screen.queryByRole("button", { name: "Apply" })).toBeNull();
      expect(screen.getByRole("button", { name: "Edit" })).toBeTruthy();
      act(() => useTabs.getState().update("tab1", { resultIndex: 0 }));
      expect(screen.queryByRole("button", { name: "Apply" })).toBeNull();
      expect((grid.props as { staging?: unknown }).staging).toBeUndefined();
    });

    it(`edit mode ends when the chart is shown: back on the grid it is read-only (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      useTabs.setState({ tabs: { tab1: tab() } });
      render(<LivePanel />);
      await screen.findByTestId("grid");
      fireEvent.click(screen.getByRole("button", { name: "Edit" }));
      act(() => useTabs.getState().update("tab1", { charts: { 0: { view: "chart", spec: null } } } as never));
      act(() => useTabs.getState().update("tab1", { charts: { 0: { view: "grid", spec: null } } } as never));
      await screen.findByTestId("grid");
      expect(screen.queryByRole("button", { name: "Apply" })).toBeNull();
      expect(screen.getByRole("button", { name: "Edit" })).toBeTruthy();
    });

    it(`one set editable, the other read-only: each shows its own rule (${engine})`, async () => {
      useProfiles.setState({ profiles: [{ id: "p1", engine, environment: "dev" } as never] });
      useExecutions.setState({ views: { e1: view("e1", { resultSetCount: 2, edits: { 0: editInfo(), 1: editInfo({ editable: false, reason: "Read-only: the result joins several tables" }) } as never }) } });
      registerExecution("e1", ordersSet(0), ordersSet(1));
      useTabs.setState({ tabs: { tab1: tab() } });
      render(<LivePanel />);
      await screen.findByTestId("grid");
      expect(screen.getByRole("button", { name: "Edit" })).toBeTruthy();
      act(() => useTabs.getState().update("tab1", { resultIndex: 1 }));
      // The sidecar's reason already starts with "Read-only:": never "Read-only: Read-only: …".
      expect(screen.getByRole("button", { name: "Read-only" }).getAttribute("title")).toBe("Read-only: the result joins several tables");
      act(() => useTabs.getState().update("tab1", { resultIndex: 0 }));
      expect(screen.getByRole("button", { name: "Edit" })).toBeTruthy();
    });

    it(`a set is found by its index, not its position, after an aborted set was dropped (${engine})`, async () => {
      useProfiles.setState({ profiles: [{ id: "p1", engine, environment: "dev" } as never] });
      // Sets 0 and 2 arrived; 1 was aborted and pruned, so set 2 sits at position 1.
      useExecutions.setState({ views: { e1: view("e1", { resultSetCount: 3, edits: { 0: editInfo(), 2: editInfo({ editable: false, reason: "joins several tables" }) } as never }) } });
      registerExecution("e1", ordersSet(0), ordersSet(2));
      useTabs.setState({ tabs: { tab1: tab({ resultIndex: 2 }) } });
      render(<LivePanel />);
      await screen.findByTestId("grid");
      expect((grid.props as { rs: { index: number } }).rs.index).toBe(2);
      expect(screen.getByRole("button", { name: "Read-only" }).getAttribute("title")).toBe("Read-only: joins several tables");
    });

    it(`a set whose session has closed is read-only and says why (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      render(<ResultsPanel tab={tab({ sessionId: undefined })} />);
      await screen.findByTestId("grid");
      expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
      expect(screen.getByRole("button", { name: "Read-only" }).getAttribute("title")).toMatch(/connection was closed since this ran/);
    });

    it(`changes staged before the session closed stay visible so they can be discarded (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      const rs = executionData.get("e1")!.byIndex(0)!;
      stagingFor("e1", rs, engine)!.setCell(ex(0), 1, { value: "uno" });
      render(<ResultsPanel tab={tab({ sessionId: "sess2" })} />);
      await screen.findByTestId("grid");
      expect((grid.props as { staging?: unknown }).staging).toBe(stagings.get("e1:0"));
    });

    it(`staged counts and the edit actions (${engine})`, async () => {
      setup({ 0: editInfo() }, engine);
      await openEditing();
      const staging = stagings.get("e1:0")!;
      expect(staging.engine).toBe(engine);
      expect(screen.getByRole("button", { name: "Apply" }).getAttribute("aria-disabled")).toBe("true");
      act(() => {
        staging.setCell(ex(0), 1, { value: "uno" });
        staging.addRow();
        staging.toggleDelete([ex(2)]);
      });
      const counts = document.querySelector("[data-q-edit-counts]") as HTMLElement;
      expect(counts.textContent).toMatch(/1 changed 1 new 1 deleted/);
      expect(screen.getByText(/stays local until applied as one transaction/)).toBeTruthy();
      fireEvent.click(screen.getByRole("button", { name: "Undo (Ctrl+Z)" }));
      fireEvent.click(screen.getByRole("button", { name: "Redo (Ctrl+Y)" }));
      fireEvent.click(screen.getByRole("button", { name: "Row" }));
      expect(staging.counts.inserts).toBe(2);
      fireEvent.click(screen.getByRole("button", { name: "Delete rows" }));
      expect(grid.selected).toHaveBeenCalled();
      fireEvent.click(screen.getByRole("button", { name: "More edit actions" }));
      fireEvent.click(menuItem("Revert selected rows"));
      expect(screen.getAllByText(/invalid/).length).toBeGreaterThan(0); // the new rows still miss the required name
      act(() => {
        for (let i = 0; i < staging.inserted.length; i++) staging.setCell({ kind: "inserted", index: i }, 1, { value: "n" });
      });
      fireEvent.click(screen.getByRole("button", { name: "Apply" }));
      expect(edit.applyChanges).toHaveBeenCalledWith("tab1", "e1", staging);
      await waitFor(() => expect(screen.getByRole("button", { name: "Apply" }).getAttribute("aria-disabled")).toBeNull());
      fireEvent.click(screen.getByRole("button", { name: "More edit actions" }));
      fireEvent.click(menuItem("Review SQL"));
      await waitFor(() => expect(edit.applyChanges).toHaveBeenLastCalledWith("tab1", "e1", staging, false, true));
    });
  }

  it.each([["prod", "PROD"], ["staging", "STAGE"]])("%s: one button reviews then applies, and names the environment", async (environment, word) => {
    setup({ 0: editInfo() }, "mssql", true, environment);
    await openEditing();
    const staging = stagings.get("e1:0")!;
    act(() => void staging.setCell(ex(0), 1, { value: "uno" }));
    expect(screen.queryByRole("button", { name: "Apply" })).toBeNull();
    const apply = screen.getByRole("button", { name: `Review & apply to ${word}…` });
    fireEvent.click(apply);
    expect(edit.applyChanges).toHaveBeenCalledWith("tab1", "e1", staging);
    fireEvent.click(screen.getByRole("button", { name: "More edit actions" }));
    // Kept in its place, unavailable with the reason: Apply itself reviews here.
    expect(menuItem(`Review SQL, Apply always shows the SQL on ${word}`).getAttribute("aria-disabled")).toBe("true");
  });

  it("Review SQL that is cancelled does not apply", async () => {
    setup({ 0: editInfo() });
    await openEditing();
    act(() => void stagings.get("e1:0")!.setCell(ex(0), 1, { value: "uno" }));
    edit.reviewChanges.mockResolvedValueOnce(false);
    fireEvent.click(screen.getByRole("button", { name: "More edit actions" }));
    fireEvent.click(menuItem("Review SQL"));
    await waitFor(() => expect(edit.reviewChanges).toHaveBeenCalled());
    expect(edit.applyChanges).not.toHaveBeenCalled();
  });

  it("flags invalid values and blocks Apply", async () => {
    setup({ 0: editInfo() });
    await openEditing();
    act(() => void stagings.get("e1:0")!.setCell(ex(0), 1, { value: "waytoolong" }));
    expect(screen.getAllByText(/1 invalid/).length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Apply" }).getAttribute("aria-disabled")).toBe("true");
  });

  it("waits for all rows while the result is still streaming", async () => {
    setup({ 0: editInfo() }, "mssql", false);
    await openEditing();
    expect(screen.getByText(/Editing starts when all rows have arrived/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Row" }).getAttribute("aria-disabled")).toBe("true");
  });

  it("passes staging callbacks to the grid", async () => {
    setup({ 0: editInfo() });
    await openEditing();
    const p = grid.props as { onStaged: () => void; onApply: () => void; staging: unknown };
    p.onStaged();
    expect(edit.scheduleTokens).toHaveBeenCalledWith("e1", p.staging);
    p.onApply();
    expect(edit.applyChanges).toHaveBeenCalledWith("tab1", "e1", p.staging);
  });
});

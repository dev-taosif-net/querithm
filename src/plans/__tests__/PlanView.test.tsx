// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import type { PlanNode, PlanStatement } from "../../ipc/contract";
import { useExecutions } from "../../store/executions";
import { useUi } from "../../store/ui";
import { openRowPopups } from "../../app/__tests__/rowPopups";

const m = vi.hoisted(() => ({ toast: vi.fn() }));
vi.mock("@xyflow/react", async () => {
  const { createElement: h } = await import("react");
  return {
    ReactFlow: (p: any) =>
      h("div", { "data-testid": "flow", "data-color-mode": p.colorMode },
        p.nodes.map((n: any) => {
          const C = p.nodeTypes[n.type];
          return h("div", { key: n.id, "data-testid": `node-${n.id}`, "data-edge-width": "", onClick: (e: any) => p.onNodeClick(e, n) }, h(C, { data: n.data }));
        }),
        p.edges.map((e: any) => h("i", { key: e.id, "data-testid": `edge-${e.id}`, "data-label": e.label, "data-stroke": e.style.stroke, "data-width": e.style.strokeWidth })),
        h("button", { "data-testid": "pane", onClick: () => p.onPaneClick() }),
        p.children),
    Background: () => null,
    Controls: () => null,
    MiniMap: (p: any) => h("i", { "data-testid": "minimap", "data-hot": p.nodeColor({ data: { rank: 0 } }), "data-cold": p.nodeColor({ data: { rank: null } }) }),
    Handle: () => null,
    Position: { Left: "left", Right: "right" },
  };
});
vi.mock("../../results/ResultsPanel", () => ({ Empty: ({ text }: { text: string }) => <div data-testid="empty">{text}</div> }));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast: m.toast }));

const { PlanView, misestimate } = await import("../PlanView");

const node = (id: string, o: Partial<PlanNode> = {}): PlanNode => ({
  id, operator: `Op ${id}`, physicalOp: `Phys${id}`, logicalOp: null, objectName: null, estRows: 100, actualRows: null, actualExecutions: null, estCost: 0.5, subtreeCost: 1,
  costPercent: 0, elapsedMs: null, logicalReads: null, warnings: [], properties: {}, children: [], ...o,
});
const stmt = (root: PlanNode, o: Partial<PlanStatement> = {}): PlanStatement => ({ text: "select 1", subtreeCost: 1.23456, root, missingIndexes: [], warnings: [], elapsedMs: null, cpuMs: null, ...o });
const tab = { id: "t", activeExecutionId: "e1" } as never;

function load(statements: PlanStatement[], raw = "<ShowPlanXML/>", state = "done") {
  useExecutions.setState({ views: { e1: { state, plans: [{ batchIndex: 0, plan: { statements }, raw }] } } } as never);
}

beforeEach(() => {
  vi.clearAllMocks();
  useExecutions.setState({ views: {} } as never);
  useUi.setState({ dialogs: [] });
  document.documentElement.dataset.theme = "dark";
});
afterEach(cleanup);

describe("PlanView", () => {
  it("explains how to get a plan when there is none", () => {
    render(<PlanView tab={tab} />);
    expect(screen.getByTestId("empty").textContent).toMatch(/Estimated plan: Ctrl\+L/);
  });

  it("says it is waiting while the query is still running", () => {
    useExecutions.setState({ views: { e1: { state: "running", plans: [] } } } as never);
    render(<PlanView tab={tab} />);
    expect(screen.getByTestId("empty").textContent).toBe("Waiting for the plan…");
  });

  it("renders the operator tree with cost badges for the top three expensive operators", () => {
    const root = node("0", { operator: "Select", children: [
      node("1", { operator: "Hash Match", logicalOp: "Inner Join", costPercent: 60, estRows: 1000, children: [
        node("2", { operator: "Index Seek", objectName: "dbo.Orders.IX", costPercent: 25, estRows: 20000, warnings: [{ kind: "implicitConversion", message: "implicit conversion" }] }),
        node("3", { operator: "Scan", costPercent: 10, estRows: 3_000_000 }),
        node("4", { operator: "Tiny", costPercent: 0.5 }),
      ] }),
      node("5", { operator: "Sort", costPercent: 5 }),
    ] });
    load([stmt(root)]);
    render(<PlanView tab={tab} />);
    // Ranks are text badges (#1 replaces the flame emoji), so they never rely on color.
    expect(screen.getByText("#1 · Cost 60.0%")).toBeTruthy();
    expect(screen.queryByText(/🔥/)).toBeNull();
    expect(screen.getByText("#2 · Cost 25.0%")).toBeTruthy();
    expect(screen.getByText("#3 · Cost 10.0%")).toBeTruthy();
    expect(screen.queryByText("Cost 5.0%")).toBeTruthy(); // un-ranked nodes show a muted cost
    expect(screen.getByText("Inner Join")).toBeTruthy();
    expect(screen.getByText("dbo.Orders.IX")).toBeTruthy();
    expect(screen.getByText("⚠ 1")).toBeTruthy();
    expect(screen.getByText("20.0K")).toBeTruthy();
    expect(screen.getByText("3.0M")).toBeTruthy();
    expect(screen.getByText("Cost").parentElement!.textContent).toContain("1.2346");
    expect(screen.getByTestId("minimap").dataset).toMatchObject({ hot: "#ef4444", cold: "#3b82f6" });
    expect(screen.getByTestId("flow").dataset.colorMode).toBe("dark");
  });

  it("uses the light colour mode in the light theme", () => {
    document.documentElement.dataset.theme = "light";
    load([stmt(node("0"))]);
    render(<PlanView tab={tab} />);
    expect(screen.getByTestId("flow").dataset.colorMode).toBe("light");
  });

  it("shows actual vs estimated rows, flags a cardinality skew on the edge, and scales edge width by rows", () => {
    const root = node("0", { children: [
      node("1", { actualRows: 5000, estRows: 10, children: [] }),
      node("2", { actualRows: 10, estRows: 10_000, children: [] }),
      node("3", { actualRows: 100, estRows: 100, children: [] }),
    ] });
    load([stmt(root, { elapsedMs: 120, cpuMs: 80 })]);
    render(<PlanView tab={tab} />);
    expect(screen.getAllByText(/^Actual/).length).toBe(3);
    expect(screen.getByText(/elapsed/).textContent).toContain("120 ms");
    expect(screen.getByTestId("edge-0-1").dataset.stroke).toBe("var(--q-err)");
    expect(screen.getByTestId("edge-0-2").dataset.stroke).toBe("var(--q-err)");
    expect(screen.getByTestId("edge-0-3").dataset.stroke).toBe("var(--q-accent)");
    expect(screen.getByTestId("edge-0-1").dataset.label).toBe("5,000 rows · 500×");
    expect(screen.getByTestId("edge-0-3").dataset.label).toBe("100 rows");
    expect(screen.getByText("4 operators · 2 misestimates")).toBeTruthy();
    expect(Number(screen.getByTestId("edge-0-1").dataset.width)).toBeGreaterThan(Number(screen.getByTestId("edge-0-3").dataset.width));
  });

  it("handles a statement with no operator tree", () => {
    load([stmt(undefined as never, { root: null })]);
    render(<PlanView tab={tab} />);
    expect(screen.queryAllByTestId(/^node-/)).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: /Raw plan/ }));
    expect(screen.getByRole("menuitem", { name: "View raw plan" })).toBeTruthy();
  });

  it("every popup of the tool row opens outside the row", () => {
    load([stmt(node("0"))]);
    render(<PlanView tab={tab} />);
    expect(openRowPopups()).toEqual(expect.arrayContaining(["Raw plan"]));
  });

  it("selects an operator to show its properties, and clears the selection from the pane", () => {
    const root = node("0", { children: [node("1", {
      operator: "Index Scan", objectName: "dbo.T", estCost: 0.123456, subtreeCost: 0.5, costPercent: 40, actualRows: 12, actualExecutions: 3, logicalReads: 1500, elapsedMs: 7,
      warnings: [{ kind: "spill", message: "spilled to tempdb" }], properties: { NodeId: "1", Ordered: "true", EstimateRows: "5", Parallel: "0" },
    })] });
    load([stmt(root)]);
    render(<PlanView tab={tab} />);
    fireEvent.click(screen.getByTestId("node-1"));
    expect(screen.getByText("Selected operator")).toBeTruthy();
    expect(screen.getByText("0.1235 (40%)").className).toContain("text-err");
    expect(screen.getByText("1,500 pages")).toBeTruthy();
    expect(screen.getByText("7 ms")).toBeTruthy();
    expect(screen.getByText("Executions")).toBeTruthy();
    expect(screen.getByText("⚠ spilled to tempdb")).toBeTruthy();
    expect(screen.getByText("Ordered")).toBeTruthy();
    expect(screen.queryByText("NodeId")).toBeNull();
    expect(screen.queryByText("EstimateRows")).toBeNull();
    fireEvent.click(screen.getByTestId("pane"));
    expect(screen.queryByText("Selected operator")).toBeNull();
  });

  it("inspector omits actual-only rows for an estimated plan", () => {
    load([stmt(node("0", { costPercent: 10 }))]);
    render(<PlanView tab={tab} />);
    fireEvent.click(screen.getByTestId("node-0"));
    expect(screen.queryByText("Actual rows")).toBeNull();
    expect(screen.getByText("Estimated rows")).toBeTruthy();
    expect(screen.getByText("0.5000 (10%)").className).not.toContain("text-err");
  });

  it("offers the missing index with a copy button", async () => {
    const write = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText: write }, configurable: true });
    load([stmt(node("0"), { missingIndexes: [{ impact: 87.65, table: "dbo.Orders", equalityColumns: ["CustomerId"], inequalityColumns: ["OrderDate"], includeColumns: [], createStatement: "CREATE INDEX ix ON dbo.Orders(CustomerId)" }] })]);
    render(<PlanView tab={tab} />);
    expect(screen.getByText(/Missing index on/).textContent).toContain("dbo.Orders (CustomerId, OrderDate) · 87.7%");
    fireEvent.click(screen.getByText("Copy CREATE INDEX"));
    await vi.waitFor(() => expect(m.toast).toHaveBeenCalledWith("success", "CREATE INDEX copied."));
    expect(write).toHaveBeenCalledWith("CREATE INDEX ix ON dbo.Orders(CustomerId)");
  });

  it("opens the raw plan in the viewer: XML for SQL Server plans, JSON for PostgreSQL plans", () => {
    load([stmt(node("0"))], "  <ShowPlanXML/>");
    const { unmount } = render(<PlanView tab={tab} />);
    fireEvent.click(screen.getByRole("button", { name: /Raw plan/ }));
    expect(screen.getByRole("menuitem", { name: "Copy raw plan (XML)" })).toBeTruthy();
    fireEvent.click(screen.getByRole("menuitem", { name: "View raw plan" }));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "cellViewer", title: "Raw plan", language: "xml", value: "  <ShowPlanXML/>" });
    unmount();
    load([stmt(node("0"))], '[{"Plan": {}}]');
    render(<PlanView tab={tab} />);
    fireEvent.click(screen.getByRole("button", { name: /Raw plan/ }));
    fireEvent.click(screen.getByRole("menuitem", { name: "View raw plan" }));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ language: "json" });
  });

  it("switches between the statements of a batch and clears the selection", () => {
    load([stmt(node("0", { operator: "First" }), { text: "select 1" }), stmt(node("0", { operator: "Second" }), { text: undefined as never })]);
    render(<PlanView tab={tab} />);
    expect(screen.getByText("First")).toBeTruthy();
    fireEvent.click(screen.getByTestId("node-0"));
    expect(screen.getByText("Selected operator")).toBeTruthy();
    // The plan's statements are the ledger's chips.
    const chips = within(screen.getByRole("tablist", { name: "Plan statements" })).getAllByRole("tab");
    expect(chips.map((c) => c.textContent)).toEqual(["1select 1", "2Statement 2"]);
    fireEvent.click(chips[1]);
    expect(screen.getByText("Second")).toBeTruthy();
    expect(screen.queryByText("Selected operator")).toBeNull();
  });

  it("Table shows the operators as a treegrid: depth, #1–#3 ranks, misestimates in words; Enter opens the inspector", () => {
    const root = node("0", { operator: "Select", children: [
      node("1", { operator: "Hash Match", costPercent: 60, actualRows: 4100, estRows: 120, children: [
        node("2", { operator: "Index Seek", objectName: "dbo.Customer.IX", costPercent: 25, warnings: [{ kind: "spill", message: "spilled to tempdb" }] }),
      ] }),
    ] });
    load([stmt(root)]);
    render(<PlanView tab={tab} />);
    fireEvent.click(within(screen.getByRole("group", { name: "Plan view" })).getByRole("button", { name: /Table/ }));
    const table = screen.getByRole("treegrid", { name: "Plan operators, statement 1" });
    const rows = within(table).getAllByRole("row").slice(1);
    expect(rows.map((r) => r.getAttribute("aria-level"))).toEqual(["1", "2", "3"]);
    expect(rows[1].textContent).toContain("#1");
    expect(rows[1].textContent).toContain("34× more rows than estimated");
    expect(rows[2].textContent).toContain("#2");
    expect(rows[2].textContent).toContain("spilled to tempdb");
    expect(screen.queryByTestId("flow")).toBeNull();
    expect(rows[0].getAttribute("tabindex")).toBe("0");
    fireEvent.keyDown(rows[0], { key: "ArrowDown" });
    fireEvent.keyDown(rows[1], { key: "Enter" });
    expect(screen.getByText("Selected operator")).toBeTruthy();
    expect(rows[1].getAttribute("aria-selected")).toBe("true");
  });

  it("misestimate: a tenfold gap either way is labelled; estimates alone are not", () => {
    expect(misestimate(node("a", { actualRows: 5000, estRows: 10 }))).toBe("500×");
    expect(misestimate(node("a", { actualRows: 10, estRows: 10_000 }))).toBe("1000×");
    expect(misestimate(node("a", { actualRows: 90, estRows: 10 }))).toBeUndefined();
    expect(misestimate(node("a", { actualRows: null }))).toBeUndefined();
  });
});

// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { erdSnapshot } from "./fixture";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useProfiles } from "../../store/profiles";
import { useToolTabs, type ToolTab } from "../../store/toolTabs";

const m = vi.hoisted(() => ({
  toast: vi.fn(), newQueryTab: vi.fn(), saveBytes: vi.fn(), rpc: vi.fn(), sessionFor: vi.fn(), svgToPng: vi.fn(),
}));
vi.mock("@xyflow/react", async () => (await import("./fakeFlow")).flowMock);
vi.mock("@xyflow/react/dist/style.css", () => ({}));
vi.mock("../erdLayout.worker?worker", async () => {
  const { layoutGraph } = await import("../erdLayout");
  return {
    default: class {
      onmessage: ((e: { data: unknown }) => void) | null = null;
      postMessage(req: Parameters<typeof layoutGraph>[0]) { queueMicrotask(() => this.onmessage?.({ data: { positions: layoutGraph(req) } })); }
      terminate() {}
    },
  };
});
vi.mock("../../store/ui", () => ({ toast: m.toast }));
vi.mock("../../app/actions", () => ({ newQueryTab: m.newQueryTab }));
vi.mock("../../app/saveBytes", () => ({ saveBytes: m.saveBytes }));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../erdExport", async (orig) => ({ ...(await orig<typeof import("../erdExport")>()), svgToPng: m.svgToPng }));
vi.mock("../../store/connections", () => ({ useConnections: { getState: () => ({ sessionFor: m.sessionFor }) }, isDisconnected: () => false }));

const { ErdTab, erdColors, erdBench } = await import("../ErdTab");

function setup(params: Record<string, unknown>, engine: "mssql" | "pg" = "mssql", snapshot = erdSnapshot()) {
  useProfiles.setState({ profiles: [{ id: "p", name: "Prod", engine } as never] });
  useSnapshots.setState({ entries: { [snapKey("p", "db")]: { key: snapKey("p", "db"), profileId: "p", database: "db", status: "fresh", snapshot, version: 1 } as never } });
  const tool: ToolTab = { id: "tool1", kind: "erd", title: "ERD", profileId: "p", database: "db", params };
  useToolTabs.setState({ tools: { tool1: tool } });
  return render(<ErdTab tool={tool} />);
}
const nodes = () => screen.queryAllByTestId(/^node-/);

beforeEach(() => {
  vi.clearAllMocks();
  m.sessionFor.mockResolvedValue("sess");
});
afterEach(cleanup);

describe("ErdTab", () => {
  it("shows a loading note while the snapshot is missing, and the error if loading failed", () => {
    useSnapshots.setState({ entries: { [snapKey("p", "db")]: { key: snapKey("p", "db"), profileId: "p", database: "db", status: "loading", version: 1 } as never } });
    const tool: ToolTab = { id: "t", kind: "erd", title: "ERD", profileId: "p", database: "db", params: { mode: "schema" } };
    const { unmount } = render(<ErdTab tool={tool} />);
    expect(screen.getByText("Loading the cached schema…")).toBeTruthy();
    unmount();
    useSnapshots.setState({ entries: { [snapKey("p", "db")]: { key: snapKey("p", "db"), profileId: "p", database: "db", status: "error", error: "boom", version: 1 } as never } });
    render(<ErdTab tool={tool} />);
    expect(screen.getByText("Metadata could not be loaded: boom")).toBeTruthy();
  });

  it("neighbourhood mode lays out the root with its direct neighbours and names the tab after the root", async () => {
    setup({ mode: "neighbourhood", rootId: 2 });
    await waitFor(() => expect(nodes()).toHaveLength(3));
    expect(screen.getByText("dbo.Orders")).toBeTruthy();
    expect(screen.getByText(/3 tables · 2 FKs/).className).toContain("text-muted"); // read, so not faint
    expect(document.querySelector(".text-faint")).toBeNull();
    // The toolbar wraps (at 960 px its labels broke into one-word columns and SVG / PNG were cut off).
    const bar = document.querySelector("[data-q-erd-toolbar]") as HTMLElement;
    expect(bar.className).toMatch(/\bflex-wrap\b/);
    expect(bar.className).toContain("whitespace-nowrap");
    expect(screen.getByRole("group", { name: "Diagram actions" }).className).toContain("ml-auto");
    expect(erdBench.lastNodes).toBe(3);
    expect(useToolTabs.getState().tools.tool1.title).toBe("ERD · Orders");
    // FK edge handles go column to column
    for (const e of screen.getAllByTestId(/^edge-/)) {
      expect(e.dataset.sourceHandle).toMatch(/^out:/);
      expect(e.dataset.targetHandle).toMatch(/^in:/);
    }
  });

  it("widens the neighbourhood with the hop buttons", async () => {
    setup({ mode: "neighbourhood", rootId: 2 });
    await waitFor(() => expect(nodes()).toHaveLength(3));
    fireEvent.click(screen.getByText("2-hop"));
    await waitFor(() => expect(nodes()).toHaveLength(4));
    fireEvent.click(screen.getByText("All connected"));
    await waitFor(() => expect(nodes()).toHaveLength(4));
    fireEvent.click(screen.getByText("1-hop"));
    await waitFor(() => expect(nodes()).toHaveLength(3));
  });

  it("schema mode filters by schema and reports tables with no foreign keys", async () => {
    setup({ mode: "schema" });
    await waitFor(() => expect(nodes()).toHaveLength(7));
    fireEvent.change(screen.getByLabelText("Schema"), { target: { value: "sales" } });
    await waitFor(() => expect(nodes()).toHaveLength(1));
    expect(useToolTabs.getState().tools.tool1.title).toBe("ERD · sales");
    fireEvent.change(screen.getByLabelText("Schema"), { target: { value: "" } });
    await waitFor(() => expect(nodes()).toHaveLength(7));
  });

  it("shows 'No foreign keys' when tables exist but nothing links them", async () => {
    setup({ mode: "schema" }, "mssql", { ...erdSnapshot(), fks: [] });
    await waitFor(() => expect(nodes()).toHaveLength(7));
    expect(screen.getByText("No foreign keys in this schema")).toBeTruthy();
  });

  it("query mode shows only the statement's tables, or explains when there are none", async () => {
    const { unmount } = setup({ mode: "query", queryTables: [1, 2, 999] });
    await waitFor(() => expect(nodes()).toHaveLength(2));
    expect(useToolTabs.getState().tools.tool1.title).toBe("ERD · query");
    unmount();
    setup({ mode: "query", queryTables: [] });
    expect(screen.getByText(/references no tables of this database/)).toBeTruthy();
  });

  it("switching the mode select resets expansion", async () => {
    setup({ mode: "neighbourhood", rootId: 2, queryTables: [1] });
    await waitFor(() => expect(nodes()).toHaveLength(3));
    fireEvent.change(screen.getByLabelText("Diagram mode"), { target: { value: "schema" } });
    await waitFor(() => expect(nodes()).toHaveLength(7));
    fireEvent.change(screen.getByLabelText("Diagram mode"), { target: { value: "query" } });
    await waitFor(() => expect(nodes()).toHaveLength(1));
  });

  it("the +N badge on a card pulls its neighbours in", async () => {
    setup({ mode: "neighbourhood", rootId: 1 });
    await waitFor(() => expect(nodes()).toHaveLength(2));
    // OrderLines is two hops away: Orders carries a +1 badge
    fireEvent.click(screen.getByText("+1"));
    await waitFor(() => expect(nodes()).toHaveLength(3));
  });

  it("compact toggle hides non-key columns", async () => {
    setup({ mode: "schema" });
    await waitFor(() => expect(nodes()).toHaveLength(7));
    expect(screen.getAllByText("Name").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByLabelText("Compact"));
    await waitFor(() => expect(screen.queryByText("Name")).toBeNull());
  });

  it("search highlights matching tables and dims the rest", async () => {
    setup({ mode: "schema" });
    await waitFor(() => expect(nodes()).toHaveLength(7));
    fireEvent.change(screen.getByPlaceholderText("Highlight table or column…"), { target: { value: "orderid" } });
    await waitFor(() => expect(screen.getByTestId("node-1").querySelector(".opacity-40")).not.toBeNull());
    expect(screen.getByTestId("node-2").querySelector(".opacity-40")).toBeNull();
    expect(screen.getByTestId("node-2").querySelector(".ring-warn\\/50")).not.toBeNull();
  });

  it("hovering an edge labels it and lights the key columns", async () => {
    setup({ mode: "neighbourhood", rootId: 2 });
    await waitFor(() => expect(nodes()).toHaveLength(3));
    const edge = screen.getAllByTestId(/^edge-/).find((e) => e.dataset.sourceHandle === "out:customerid")!;
    fireEvent.mouseEnter(edge);
    expect(edge.textContent).toBe("FK_Orders_Customers (CustomerId → CustomerId)");
    expect(document.querySelectorAll(".bg-accent\\/15.q-hl").length).toBe(2);
    fireEvent.mouseLeave(edge);
    expect(edge.textContent).toBe("");
  });

  it("click selects one table, shift-click adds, pane click clears; two tables generate a T-SQL join", async () => {
    setup({ mode: "neighbourhood", rootId: 2 });
    await waitFor(() => expect(nodes()).toHaveLength(3));
    const gen = screen.getByText("Generate SELECT") as HTMLButtonElement;
    expect(gen.disabled).toBe(true);
    fireEvent.click(screen.getByTestId("node-2"));
    expect(gen.disabled).toBe(true);
    fireEvent.click(screen.getByTestId("node-1"), { shiftKey: true });
    expect(screen.getByText("Clear selection (2)")).toBeTruthy();
    expect(gen.disabled).toBe(false);
    fireEvent.click(gen);
    expect(m.newQueryTab).toHaveBeenCalledWith(expect.objectContaining({ profileId: "p", database: "db", title: "Join.sql", text: "SELECT o.*, c.*\nFROM Orders o\nJOIN Customers c ON o.CustomerId = c.CustomerId;" }));
    fireEvent.click(screen.getByTestId("node-1"), { shiftKey: true });
    expect(screen.getByText("Clear selection (1)")).toBeTruthy();
    fireEvent.click(screen.getByTestId("pane"));
    expect(screen.queryByText(/Clear selection/)).toBeNull();
  });

  it("generates a PostgreSQL join with double-quoted identifiers", async () => {
    setup({ mode: "neighbourhood", rootId: 2 }, "pg");
    await waitFor(() => expect(nodes()).toHaveLength(3));
    fireEvent.click(screen.getByTestId("node-2"));
    fireEvent.click(screen.getByTestId("node-1"), { ctrlKey: true });
    fireEvent.click(screen.getByText("Generate SELECT"));
    expect(m.newQueryTab.mock.calls[0][0].text).toContain('"Customers"');
  });

  it("warns when the selected tables cannot be joined", async () => {
    setup({ mode: "schema" });
    await waitFor(() => expect(nodes()).toHaveLength(7));
    fireEvent.click(screen.getByTestId("node-1"));
    fireEvent.click(screen.getByTestId("node-6"), { metaKey: true });
    fireEvent.click(screen.getByText("Generate SELECT"));
    expect(m.toast).toHaveBeenCalledWith("warn", expect.any(String));
    expect(m.newQueryTab).not.toHaveBeenCalled();
  });

  it("loads row counts into the cards (exact and estimated) and reports failures", async () => {
    setup({ mode: "neighbourhood", rootId: 2 });
    await waitFor(() => expect(nodes()).toHaveLength(3));
    m.rpc.mockResolvedValueOnce([{ objectId: 1, rows: 1234, estimated: false }, { objectId: 2, rows: 50, estimated: true }, { objectId: 3, rows: null, estimated: false }]);
    fireEvent.click(screen.getByText("Load row counts"));
    await waitFor(() => expect(screen.getByText("~50")).toBeTruthy());
    expect(screen.getByText((1234).toLocaleString())).toBeTruthy();
    expect(m.rpc).toHaveBeenCalledWith("meta.rowCounts", { sessionId: "sess", database: "db", objectIds: expect.arrayContaining([1, 2, 3]) });
    m.rpc.mockRejectedValueOnce(new Error("no perms"));
    fireEvent.click(screen.getByText("Load row counts"));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "no perms"));
  });

  it("exports SVG and PNG through the save dialog", async () => {
    setup({ mode: "neighbourhood", rootId: 2 });
    await waitFor(() => expect((screen.getByText("SVG") as HTMLButtonElement).disabled).toBe(false));
    m.saveBytes.mockResolvedValue("/tmp/erd.svg");
    fireEvent.click(screen.getByText("SVG"));
    await waitFor(() => expect(m.saveBytes).toHaveBeenCalled());
    const [name, bytes, filter] = m.saveBytes.mock.calls[0];
    expect(name).toBe("erd-db-Orders.svg");
    expect(new TextDecoder().decode(bytes)).toContain("<svg");
    expect(filter.mime).toBe("image/svg+xml");
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("success", "Saved /tmp/erd.svg"));
    m.svgToPng.mockResolvedValue(new Uint8Array([1, 2]));
    m.saveBytes.mockResolvedValue(null);
    fireEvent.click(screen.getByText("PNG"));
    await waitFor(() => expect(m.saveBytes).toHaveBeenCalledTimes(2));
    expect(m.saveBytes.mock.calls[1][0]).toBe("erd-db-Orders.png");
    expect(m.toast).toHaveBeenCalledTimes(1);
    m.svgToPng.mockRejectedValue(new Error("too large"));
    fireEvent.click(screen.getByText("PNG"));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "too large"));
  });

  it("shows a minimap only for larger diagrams, and compacts very large ones by default", async () => {
    const big = erdSnapshot();
    for (let i = 100; i < 260; i++) big.objects.push({ id: i, schema: "dbo", name: `T${i}`, kind: "table", columns: [{ name: "Id", type: "int", nullable: false, identity: false, computed: false, pk: true, default: null, ordinal: 0 }], params: null, returnType: null, modifyDate: null });
    setup({ mode: "schema" }, "mssql", big);
    await waitFor(() => expect(nodes().length).toBe(167));
    expect(screen.getByTestId("minimap")).toBeTruthy();
    expect((screen.getByLabelText("Compact") as HTMLInputElement).checked).toBe(true);
  });

  it("reports a failed layout", async () => {
    const bad = erdSnapshot();
    const layout = await import("../layoutAsync");
    const spy = vi.spyOn(layout, "layoutAsync").mockRejectedValueOnce(new Error("dagre"));
    setup({ mode: "schema" }, "mssql", bad);
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "Layout failed: dagre"));
    spy.mockRestore();
  });

  it("erdColors reads the theme variables", () => {
    document.documentElement.style.setProperty("--q-bg", " #123456 ");
    expect(erdColors().bg).toBe("#123456");
  });
});

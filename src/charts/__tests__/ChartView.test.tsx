// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { Int32, Utf8 } from "apache-arrow";

const { feeds, drawn } = vi.hoisted(() => ({
  feeds: [] as { onResult: (d: unknown, ms: number) => void; onError: (m: string) => void; updates: unknown[]; disposed: boolean }[],
  drawn: [] as unknown[],
}));

vi.mock("../chartFeed", async (orig) => ({
  ...(await orig<typeof import("../chartFeed")>()),
  ChartFeed: class {
    updates: unknown[] = [];
    disposed = false;
    constructor(_rs: unknown, _w: unknown, public onResult: (d: unknown, ms: number) => void, public onError: (m: string) => void) {
      feeds.push(this);
    }
    update(spec: unknown) {
      this.updates.push(spec);
      return Promise.resolve();
    }
    dispose() {
      this.disposed = true;
    }
  },
}));
vi.mock("../render", () => ({
  drawChart: (_host: unknown, data: unknown) => {
    drawn.push(data);
    return { canvas: () => document.createElement("canvas"), destroy: () => {} };
  },
  renderChartImage: () => null,
  canvasPng: async () => new Uint8Array(),
}));

import { ArrowResultSet } from "../../results/model";
import { dynamicColumn, makeBatch, type DynamicCell } from "../../results/__tests__/fixtures";
import { ChartView } from "../ChartView";
import { computeChart } from "../chartData";
import type { ChartSpec } from "../chartSpec";

function result(complete = true) {
  const rs = new ArrowResultSet(0);
  rs.append(makeBatch([
    { name: "region", type: new Utf8(), values: ["n", "s", null] },
    { name: "sales", type: new Int32(), values: [1, 2, 3] },
  ]));
  if (complete) rs.finish(false);
  return rs;
}

beforeEach(() => {
  feeds.length = 0;
  drawn.length = 0;
  // jsdom has no layout: give the plot box a size.
  Object.defineProperty(HTMLElement.prototype, "clientWidth", { configurable: true, get: () => 600 });
  Object.defineProperty(HTMLElement.prototype, "clientHeight", { configurable: true, get: () => 300 });
});
afterEach(cleanup);

describe("ChartView", () => {
  it("infers a spec, keeps it, asks the worker and draws what comes back", async () => {
    const rs = result();
    const onSpec = vi.fn();
    render(<ChartView rs={rs} spec={null} onSpecChange={onSpec} title="q" />);
    const spec: ChartSpec = { kind: "bar", x: "region", y: ["sales"], series: null, agg: "sum" };
    expect(onSpec).toHaveBeenCalledWith(spec);
    expect(feeds).toHaveLength(1);
    expect(feeds[0].updates[0]).toEqual(spec);
    expect(screen.getByText("Computing the chart…")).toBeTruthy();
    const data = computeChart(spec, { rows: 3, x: { type: "cat", values: ["n", "s", null] }, ys: [{ name: "sales", values: Float64Array.from([1, 2, 3]) }], lossy: true });
    act(() => feeds[0].onResult(data, 5));
    expect(drawn).toEqual([data]);
    expect(screen.queryByText("Computing the chart…")).toBeNull();
    expect(screen.getByText("1 row skipped (NULL)")).toBeTruthy();
    expect(screen.getByText(/beyond 2\^53/)).toBeTruthy();
    expect(screen.getByRole("img").getAttribute("aria-label")).toBe("Bar chart of sales by region, 2 rows plotted. The Grid view has every value.");
  });

  it("Export chart ▾ is the app's menu: arrows move between its items, Escape returns focus to the button (L32)", () => {
    const rs = result();
    render(<ChartView rs={rs} spec={null} onSpecChange={() => {}} title="q" />);
    const spec: ChartSpec = { kind: "bar", x: "region", y: ["sales"], series: null, agg: "sum" };
    act(() => feeds[0].onResult(computeChart(spec, { rows: 3, x: { type: "cat", values: ["n", "s", null] }, ys: [{ name: "sales", values: Float64Array.from([1, 2, 3]) }], lossy: false }), 5));
    const button = screen.getByRole("button", { name: "Export chart ▾" });
    fireEvent.click(button);
    const menu = screen.getByRole("menu", { name: "Export chart" });
    const items = within(menu).getAllByRole("menuitem");
    expect(items.map((i) => i.textContent)).toEqual(["PNG image", "Chart data (CSV)"]);
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(menu, { key: "ArrowUp" });
    expect(document.activeElement).toBe(items[1]);
    fireEvent.keyDown(menu, { key: "Home" });
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(document.activeElement!, { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
    expect(document.activeElement).toBe(button);
  });

  it("partial results say so; a streaming result says it is still arriving", () => {
    const rs = result(false);
    const spec: ChartSpec = { kind: "bar", x: "region", y: ["sales"], series: null, agg: "sum" };
    const { rerender } = render(<ChartView rs={rs} spec={spec} onSpecChange={() => {}} title="q" />);
    expect(screen.getByText("Streaming… 3 rows so far")).toBeTruthy();
    rs.interrupted = true;
    rerender(<ChartView rs={rs} spec={{ ...spec }} onSpecChange={() => {}} title="q" />);
    expect(screen.getByText("Partial data (incomplete — execution stopped)")).toBeTruthy();
  });

  it("controls change the spec: kind, aggregation, value columns", () => {
    const onSpec = vi.fn();
    const spec: ChartSpec = { kind: "bar", x: "region", y: ["sales"], series: null, agg: "sum" };
    render(<ChartView rs={result()} spec={spec} onSpecChange={onSpec} title="q" />);
    onSpec.mockClear();
    fireEvent.click(within(screen.getByRole("radiogroup", { name: "Chart type" })).getByRole("radio", { name: "Donut" }));
    expect(onSpec).toHaveBeenLastCalledWith({ kind: "pie", x: "region", y: ["sales"], series: null, agg: "sum", bins: undefined });
    fireEvent.change(screen.getByDisplayValue("Sum"), { target: { value: "avg" } });
    expect(onSpec).toHaveBeenLastCalledWith({ ...spec, agg: "avg" });
    fireEvent.click(screen.getByRole("button", { name: /sales/ }));
    fireEvent.click(screen.getByLabelText("Row count"));
    expect(onSpec).toHaveBeenLastCalledWith({ ...spec, y: [], agg: "count" });
  });

  it("a result with nothing to chart says why; a worker failure is shown", () => {
    const rs = new ArrowResultSet(0);
    rs.append(makeBatch([{ name: "b", type: new Utf8(), values: ["x"] }]));
    rs.columns[0].kind = "binary";
    render(<ChartView rs={rs} spec={null} onSpecChange={() => {}} title="q" />);
    expect(screen.getByText(/Nothing in this result can be charted/)).toBeTruthy();
    cleanup();
    render(<ChartView rs={result()} spec={{ kind: "bar", x: "region", y: ["sales"], agg: "sum" }} onSpecChange={() => {}} title="q" />);
    act(() => feeds.at(-1)!.onError("out of memory"));
    expect(screen.getByText("The chart could not be computed: out of memory")).toBeTruthy();
  });

  it("disposes its worker feed when it goes away", () => {
    const { unmount } = render(<ChartView rs={result()} spec={{ kind: "bar", x: "region", y: ["sales"], agg: "sum" }} onSpecChange={() => {}} title="q" />);
    unmount();
    expect(feeds[0].disposed).toBe(true);
  });

  describe("dynamically typed columns (Phase D0f, SQLite)", () => {
    const batchOf = (cells: DynamicCell[]) =>
      makeBatch([{ name: "region", type: new Utf8(), values: cells.map((_, i) => `r${i}`), meta: { "q:typeName": "TEXT" } }, ...dynamicColumn("v", cells, 1)]);
    const MIXED = "Mixed value types: v is not numeric in every row";

    it("a column of integers and reals charts as numbers, with no note", () => {
      const rs = new ArrowResultSet(0);
      rs.append(batchOf([[1, "1"], [2, "2.5"], [0, null]]));
      rs.finish(false);
      const onSpec = vi.fn();
      render(<ChartView rs={rs} spec={null} onSpecChange={onSpec} title="q" />);
      expect(onSpec).toHaveBeenCalledWith({ kind: "bar", x: "region", y: ["v"], series: null, agg: "sum" });
      expect(screen.queryByText(MIXED)).toBeNull();
    });

    it("a column with numbers and text is not a value column: the chart counts rows and says why", () => {
      const rs = new ArrowResultSet(0);
      rs.append(batchOf([[1, "1"], [3, "n/a"]]));
      rs.finish(false);
      const onSpec = vi.fn();
      render(<ChartView rs={rs} spec={{ kind: "bar", x: "region", y: ["v"], series: null, agg: "sum" }} onSpecChange={onSpec} title="q" />);
      expect(onSpec).toHaveBeenLastCalledWith({ kind: "bar", x: "region", y: [], series: null, agg: "count" });
      expect(screen.getByText(MIXED)).toBeTruthy();
    });

    it("a text row arriving while streaming turns a number column mixed: the chart degrades and shows the note", () => {
      const rs = new ArrowResultSet(0);
      rs.append(batchOf([[1, "1"], [1, "2"]]));
      const onSpec = vi.fn();
      render(<ChartView rs={rs} spec={null} onSpecChange={onSpec} title="q" />);
      expect(onSpec).toHaveBeenLastCalledWith({ kind: "bar", x: "region", y: ["v"], series: null, agg: "sum" });
      expect(screen.queryByText(MIXED)).toBeNull();
      act(() => rs.append(batchOf([[3, "three"]])));
      expect(onSpec).toHaveBeenLastCalledWith({ kind: "bar", x: "region", y: [], series: null, agg: "count" });
      expect(screen.getByText(MIXED)).toBeTruthy();
    });
  });
});

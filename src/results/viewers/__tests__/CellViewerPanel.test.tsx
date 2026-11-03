// @vitest-environment jsdom
import "@testing-library/jest-dom/vitest";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { Binary, Bool, Field, makeData, RecordBatch, Schema, Struct, Utf8, vectorFromArray, type DataType } from "apache-arrow";

const { rpc } = vi.hoisted(() => ({ rpc: vi.fn() }));
vi.mock("../../../ipc/client", () => ({ rpc }));

import { ArrowResultSet } from "../../model";
import { useExecutions } from "../../../store/executions";
import { useTabs } from "../../../store/tabs";
import { CellViewerPanel, UNAVAILABLE_NOTE } from "../CellViewerPanel";
import { OPENED_PLAN_BATCH, PLAN_FORMATS, planEngine } from "../openAsPlan";
import { dynamicColumn, makeBatch, view, type DynamicCell } from "../../__tests__/fixtures";

type Col = { name: string; type: DataType; values: unknown[]; meta?: Record<string, string> };

function resultSet(cols: Col[]): ArrowResultSet {
  const fields = cols.map((c) => new Field(c.name, c.type, true, new Map(Object.entries(c.meta ?? {}))));
  const children = cols.map((c) => vectorFromArray(c.values, c.type).data[0]);
  const rs = new ArrowResultSet(0);
  rs.append(new RecordBatch(new Schema(fields), makeData({ type: new Struct(fields), length: children[0].length, nullCount: 0, children })));
  rs.finish(false);
  return rs;
}

/** One column of `type` (typeName `typeName`), rows as given, optionally with a __truncated flag column. */
function single(typeName: string, type: DataType, values: unknown[], truncated?: boolean[]) {
  const cols: Col[] = [{ name: "v", type, values, meta: { "q:typeName": typeName, "q:ordinal": "0" } }];
  if (truncated) cols.push({ name: "v__truncated", type: new Bool(), values: truncated, meta: { "q:hidden": "1" } });
  return resultSet(cols);
}

const text = (values: (string | null)[], typeName = "nvarchar", truncated?: boolean[]) => single(typeName, new Utf8(), values, truncated);
const bin = (values: (Uint8Array | null)[], truncated?: boolean[]) => single("varbinary", new Binary(), values, truncated);

const PNG = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]);
const SHOWPLAN = '<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence/></ShowPlanXML>';

function show(rs: ArrowResultSet, row = 0, onClose = vi.fn()) {
  const r = render(<CellViewerPanel tabId="tab1" executionId="e1" rs={rs} row={row} col={0} engine="mssql" onClose={onClose} />);
  return { ...r, onClose };
}

beforeEach(() => {
  rpc.mockReset();
  useExecutions.setState({ views: {} });
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
});
afterEach(cleanup);

describe("CellViewerPanel: values are never HTML", () => {
  it("markup in a text value is shown as text, not elements", async () => {
    const value = '<img src=x onerror="window.__pwned=1">';
    const { container } = show(text([value]));
    expect(await screen.findByRole("alert")).toHaveTextContent("Not valid XML");
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector("pre")?.textContent).toBe(value);
  });

  it("a well-formed <script> value is formatted as text and never becomes a script element", () => {
    const { container } = show(text(["<script>alert(document.cookie)</script>"]));
    expect(container.querySelector("script")).toBeNull();
    expect(container.textContent).toContain("<script>alert(document.cookie)</script>");
  });

  it("PNG bytes become an <img> with a data: URL of the sniffed type", () => {
    const { container } = show(bin([PNG]));
    const img = container.querySelector("img")!;
    expect(img.getAttribute("src")).toBe(`data:image/png;base64,${btoa(String.fromCharCode(...PNG))}`);
    expect(img.getAttribute("alt")).toContain("PNG");
    fireEvent.click(screen.getByRole("button", { name: "Hex" }));
    expect(container.querySelector("img")).toBeNull();
    expect(screen.getByLabelText("Hex dump").textContent).toContain("89 50 4E 47");
  });

  it("bytes of an HTML document are shown as hex, never as an image or markup", () => {
    const html = new TextEncoder().encode("<html><body><img src=x onerror=alert(1)></body></html>");
    const { container } = show(bin([html]));
    expect(container.querySelector("img")).toBeNull();
    expect(screen.queryByRole("button", { name: "Image" })).toBeNull();
    expect(screen.getByLabelText("Hex dump").textContent).toContain("3C 68 74 6D 6C 3E");
    expect(screen.getByText(`${html.length} bytes`)).toBeInTheDocument();
  });
});

describe("CellViewerPanel: JSON and XML", () => {
  it("malformed JSON shows the raw text with the parse error", async () => {
    const raw = '{"a": 1,, "b": 2}';
    const { container } = show(text([raw], "jsonb"));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent('Not valid JSON: Unexpected ",", expected a property name in double quotes at line 1, column 9');
    expect(container.querySelector("pre")?.textContent).toBe(raw);
  });

  it("malformed XML shows the raw text with the parse error", async () => {
    const { container } = show(text(["<a><b></a>"], "xml"));
    expect(await screen.findByRole("alert")).toHaveTextContent(/^Not valid XML: .+/);
    expect(container.querySelector("pre")?.textContent).toBe("<a><b></a>");
  });

  it("formats JSON, folds a block and searches it", async () => {
    show(text(['{"a":{"b":1,"c":2},"d":"Needle"}'], "json"));
    const region = await screen.findByRole("region", { name: "Formatted JSON" });
    expect(region.textContent).toContain('"b": 1,');
    fireEvent.click(screen.getByRole("button", { name: "Fold line 2" }));
    expect(region.textContent).not.toContain('"b": 1,');
    expect(region.textContent).toContain('"a": { … },');
    fireEvent.change(screen.getByRole("textbox", { name: "Search JSON" }), { target: { value: "needle" } });
    expect(screen.getByText("1 of 1 lines · 1 hits")).toBeInTheDocument();
    expect(region.querySelector("mark")?.textContent).toBe("Needle");
    fireEvent.change(screen.getByRole("textbox", { name: "Search JSON" }), { target: { value: '"b"' } });
    // The hit is inside the folded block: the fold opens to show it.
    expect(region.textContent).toContain('"b": 1,');
    fireEvent.click(screen.getByRole("button", { name: "Raw" }));
    expect(screen.queryByRole("region", { name: "Formatted JSON" })).toBeNull();
  });

  it("Open as plan parses a showplan in the sidecar and shows it in the tab's Plan panel, results kept", async () => {
    useTabs.getState().add({ id: "tab1", title: "q.sql", activeExecutionId: "e1", bottomTab: "results" });
    useExecutions.getState().put(view("e1", { plans: [{ batchIndex: 0, plan: { statements: [] }, raw: "<own/>" }] }));
    const plan = { statements: [{ text: "SELECT 1", subtreeCost: 1, root: null, missingIndexes: [], warnings: [] }] };
    rpc.mockResolvedValue({ plan, format: "xml" });
    show(text([SHOWPLAN], "xml"));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Open as plan" })));
    expect(rpc).toHaveBeenCalledWith("plan.parse", { engine: "mssql", raw: SHOWPLAN });
    const v = useExecutions.getState().views.e1;
    expect(v.plans.map((p) => p.batchIndex)).toEqual([OPENED_PLAN_BATCH, 0]);
    expect(v.plans[0].plan).toBe(plan);
    expect(useTabs.getState().tabs.tab1).toMatchObject({ bottomTab: "plan", bottomOpen: true, activeExecutionId: "e1" });
  });

  it("a PostgreSQL EXPLAIN (FORMAT JSON) value opens with the PostgreSQL parser; a refusal is a toast, not a crash", async () => {
    useTabs.getState().add({ id: "tab1", title: "q.sql", activeExecutionId: "e1" });
    useExecutions.getState().put(view("e1"));
    rpc.mockRejectedValue(new Error("Not a PostgreSQL EXPLAIN (FORMAT JSON) plan that can be read"));
    const raw = '[{"Plan": {"Node Type": "Seq Scan"}}]';
    show(text([raw], "json"));
    const open = await screen.findByRole("button", { name: "Open as plan" });
    await act(async () => fireEvent.click(open));
    expect(rpc).toHaveBeenCalledWith("plan.parse", { engine: "pg", raw });
    expect(useExecutions.getState().views.e1.plans).toEqual([]);
    expect(useTabs.getState().tabs.tab1.bottomTab).toBe("results");
  });

  it("each plan format belongs to one engine's parser and names it on the button", () => {
    expect(planEngine("showplan")).toBe("mssql");
    expect(planEngine("explainJson")).toBe("pg");
    expect(PLAN_FORMATS.showplan.title).toBe("Show this SQL Server showplan in the Plan panel");
    expect(PLAN_FORMATS.explainJson.title).toBe("Show this PostgreSQL EXPLAIN plan in the Plan panel");
    expect(planEngine("sqliteEqp")).toBe("sqlite");
    expect(planEngine("mysqlJson")).toBe("mySql");
  });

  it.each([
    ["sqlite", '[{"text": "SELECT * FROM t", "rows": [[2, 0, "SCAN t"]]}]', "Show this SQLite query plan in the Plan panel"],
    ["mySql", '[{"text": "SELECT 1", "plan": "{}"}]', "Show this MySQL EXPLAIN plan in the Plan panel"],
  ])("a %s plan value opens with that engine's parser", async (engine, raw, title) => {
    useTabs.getState().add({ id: "tab1", title: "q.sql", activeExecutionId: "e1" });
    useExecutions.getState().put(view("e1"));
    rpc.mockResolvedValue({ plan: { statements: [] }, format: "x" });
    show(text([raw], "json"));
    const open = await screen.findByRole("button", { name: "Open as plan" });
    expect(open.getAttribute("title")).toBe(title);
    await act(async () => fireEvent.click(open));
    expect(rpc).toHaveBeenCalledWith("plan.parse", { engine, raw });
  });

  it("ordinary XML and JSON offer no Open as plan", async () => {
    show(text(['[{"plan": 1}]'], "json"));
    await screen.findByRole("region", { name: "Formatted JSON" });
    expect(screen.queryByRole("button", { name: "Open as plan" })).toBeNull();
  });
});

describe("CellViewerPanel: dynamically typed columns (Phase D0f, SQLite)", () => {
  const hex = (b: Uint8Array) => Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
  const dynamic = (cells: DynamicCell[], truncated?: boolean[]) => {
    const [v, flag, cls] = dynamicColumn("v", cells);
    const rs = new ArrowResultSet(0);
    rs.append(makeBatch([v, truncated ? { ...flag, values: truncated } : flag, cls]));
    rs.finish(false);
    return rs;
  };

  it("a blob cell's hex is decoded to bytes: the image preview and the hex dump work as for a binary column", () => {
    const { container } = show(dynamic([[4, hex(PNG)]]));
    expect(container.querySelector("img")!.getAttribute("src")).toBe(`data:image/png;base64,${btoa(String.fromCharCode(...PNG))}`);
    fireEvent.click(screen.getByRole("button", { name: "Hex" }));
    expect(screen.getByLabelText("Hex dump").textContent).toContain("89 50 4E 47");
    expect(container.textContent).toContain(`${PNG.length} bytes · PNG`);
  });

  it("text that looks like hex, and numbers, stay text", () => {
    const { container, unmount } = show(dynamic([[3, "0aff"]]));
    expect(screen.queryByLabelText("Hex dump")).toBeNull();
    expect(container.querySelector("pre")?.textContent).toBe("0aff");
    unmount();
    const r = show(dynamic([[1, "1234"]]));
    expect(r.container.querySelector("pre")?.textContent).toBe("1234");
  });

  it("a truncated blob's full value comes back as hex text and is decoded too", async () => {
    rpc.mockResolvedValue({ value: hex(PNG), isBinary: false, unavailable: false });
    const { container } = show(dynamic([[4, hex(PNG.subarray(0, 4))]], [true]));
    await screen.findByRole("img");
    expect(container.querySelector("img")!.getAttribute("src")).toContain("data:image/png;base64,");
  });
});

describe("CellViewerPanel: full values", () => {
  it("fetches a truncated cell's full value by its physical row", async () => {
    const rs = text(["a", "short…"], "nvarchar", [false, true]);
    rs.setSort(0, "desc"); // view row 0 is physical row 1
    rpc.mockResolvedValue({ value: "the whole long value", isBinary: false, unavailable: false });
    const { container } = show(rs, 0);
    expect(screen.getByText("Fetching the full value…")).toBeInTheDocument();
    expect(await screen.findByText("the whole long value")).toBeInTheDocument();
    expect(rpc).toHaveBeenCalledWith("query.cell", { executionId: "e1", resultIndex: 0, row: 1, col: 0 });
    expect(container.textContent).not.toContain(UNAVAILABLE_NOTE);
  });

  it("says when the full value is gone and shows what the grid has", async () => {
    rpc.mockResolvedValue({ value: null, isBinary: false, unavailable: true });
    show(text(["partial"], "nvarchar", [true]));
    expect(await screen.findByText(UNAVAILABLE_NOTE)).toBeInTheDocument();
    expect(screen.getByText("partial")).toBeInTheDocument();
  });

  it("reads a truncated binary value from base64", async () => {
    rpc.mockResolvedValue({ value: btoa(String.fromCharCode(...PNG)), isBinary: true, unavailable: false });
    const { container } = show(bin([PNG.subarray(0, 4)], [true]));
    await screen.findByRole("img");
    expect(container.querySelector("img")!.getAttribute("src")).toContain("data:image/png;base64,");
  });

  it("shows NULL, wraps long text on request and closes on Escape", () => {
    const { onClose, unmount } = show(text([null]));
    expect(screen.getByText("NULL")).toBeInTheDocument();
    unmount();
    const r = show(text(["line one\nline two"]));
    const pre = r.container.querySelector("pre")!;
    expect(pre.className).toContain("whitespace-pre-wrap");
    fireEvent.click(screen.getByRole("checkbox", { name: "Wrap" }));
    expect(pre.className).not.toContain("whitespace-pre-wrap");
    fireEvent.keyDown(pre, { key: "Escape" });
    expect(r.onClose).toHaveBeenCalledTimes(1);
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Close the value viewer" }));
    expect(r.onClose).toHaveBeenCalledTimes(2);
  });
});

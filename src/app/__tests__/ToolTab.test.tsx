// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import "./domPolyfills";

const h = vi.hoisted(() => ({ closeTool: vi.fn() }));
vi.mock("../toolActions", () => ({ closeTool: (id: string) => h.closeTool(id) }));
vi.mock("../actions", () => ({ closeTab: vi.fn(), duplicateTab: vi.fn(), workspaceHooks: {} }));
vi.mock("../tabActions", () => ({
  closeTabs: vi.fn(), moveTabBy: vi.fn(), reopenClosed: vi.fn(), togglePin: vi.fn(), stripOrder: () => ["x1"],
  planClose: () => ({ close: [], running: [], inTx: [] }),
}));
vi.mock("../../erd/ErdTab", () => ({ ErdTab: ({ tool }: any) => <div>erd:{tool.title}</div> }));
vi.mock("../../compare/SchemaCompareTab", () => ({ SchemaCompareTab: ({ tool }: any) => <div>schema:{tool.title}</div> }));
vi.mock("../../compare/DataCompareTab", () => ({ DataCompareTab: ({ tool }: any) => <div>data:{tool.title}</div> }));

import { ToolTabHeader, ToolTabPanel } from "../ToolTab";
import { useToolTabs } from "../../store/toolTabs";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";

const tool = (kind: string, extra: any = {}) => ({ id: "x1", kind, title: `T-${kind}`, params: {}, ...extra });
function fakeApi(active = false) {
  let cb: ((e: { isActive: boolean }) => void) | undefined;
  const dispose = vi.fn();
  return {
    api: { isActive: active, onDidActiveChange: (f: any) => { cb = f; return { dispose }; } } as any,
    emit: (isActive: boolean) => act(() => cb?.({ isActive })),
    dispose,
  };
}
const setTool = (t: any) => useToolTabs.setState({ tools: { x1: t } });

beforeEach(() => {
  vi.clearAllMocks();
  useToolTabs.setState({ tools: {} });
  useProfiles.setState({
    profiles: [{ id: "pg1", name: "Analytics", engine: "pg", environment: "dev", color: "#123456" } as any, { id: "ms1", name: "Sales", engine: "mssql", environment: "prod" } as any],
    groups: [],
  });
});
afterEach(cleanup);

describe("ToolTabPanel", () => {
  it("renders nothing for an unknown tool", () => {
    const { container } = render(<ToolTabPanel {...({ params: { toolId: "x1" } } as any)} />);
    expect(container).toBeEmptyDOMElement();
  });
  it.each([
    ["erd", "erd:T-erd"],
    ["schemaCompare", "schema:T-schemaCompare"],
    ["dataCompare", "data:T-dataCompare"],
  ])("loads the %s tool lazily", async (kind, text) => {
    setTool(tool(kind));
    render(<ToolTabPanel {...({ params: { toolId: "x1" } } as any)} />);
    expect(screen.getByText("Loading…")).toBeInTheDocument();
    expect(await screen.findByText(text)).toBeInTheDocument();
  });
});

describe("ToolTabHeader", () => {
  const header = (f = fakeApi()) => render(<div className="dv-tab"><ToolTabHeader {...({ api: f.api, params: { toolId: "x1" } } as any)} /></div>);

  it("renders nothing for an unknown tool", () => {
    const { container } = header();
    expect(container.querySelector(".dv-tab")).toBeEmptyDOMElement();
  });
  it.each([
    ["erd", "ERD · Orders", "Orders", "ER diagram"],
    ["schemaCompare", "Compare · a → b", "Schema compare", "schema compare"],
    ["dataCompare", "Data · a → b", "Data compare", "data compare"],
    ["activity", "Activity — Sales", "Activity monitor", "activity monitor"],
  ])("%s: the kind icon and a short name; the kind is spoken and the full title is in the tooltip (D17)", (kind, title, shown, spoken) => {
    setTool(tool(kind, { title }));
    const { container } = header();
    expect(container.querySelector(".q-tab-l1 svg")).not.toBeNull();
    expect(container.querySelector("[data-q-tab-title]")!.textContent).toBe(shown);
    expect(container.querySelector(".dv-tab")!.getAttribute("aria-label")).toBe(`${shown}, ${spoken}`);
    expect((container.querySelector(".q-tab") as HTMLElement).title).toBe(title);
  });
  it("line 2 is the database in its environment's colour and the server; only Prod gets the band", () => {
    setTool(tool("erd", { profileId: "ms1", database: "Billing" }));
    const a = header();
    expect(a.container.querySelector("[data-q-tab-subtitle]")!.textContent).toBe("Billing· Sales");
    expect((a.container.querySelector(".q-tab-band") as HTMLElement).style.background).not.toBe("");
    expect(a.container.querySelector(".dv-tab")!.getAttribute("aria-label")).toContain("Production, Sales, database Billing");
    a.unmount();
    setTool(tool("erd", { profileId: "pg1", database: "warehouse" }));
    const b = header();
    // A custom profile colour, fitted (no stylesheet in jsdom: the colour itself).
    expect((b.container.querySelector(".q-tab-db") as HTMLElement).style.color).toBe("rgb(18, 52, 86)");
    expect(b.container.querySelector(".q-tab-band")).toBeNull();
    b.unmount();
    setTool(tool("erd"));
    const c = header();
    expect(c.container.querySelector("[data-q-tab-subtitle]")!.textContent).toBe("");
  });
  it("a compare tab reads source → target; the band follows the target (Stage → Prod)", () => {
    setTool(tool("schemaCompare", { params: { source: { kind: "live", profileId: "pg1", database: "orders" }, target: { kind: "live", profileId: "ms1", database: "Billing" } } }));
    const { container } = header();
    expect(container.querySelector("[data-q-tab-subtitle]")!.textContent).toBe("orders→Billing");
    expect(container.querySelector(".q-tab-band")).not.toBeNull();
    expect(container.querySelector(".dv-tab")!.getAttribute("aria-label")).toBe("Schema compare, schema compare, Development to Production, Analytics, database orders, to database Billing");
  });
  it("a disconnected server is shown as on a query tab", () => {
    useConnections.setState({ disconnected: { ms1: true } });
    setTool(tool("activity", { profileId: "ms1" }));
    const { container } = header();
    expect(container.querySelector("[data-q-tab-subtitle]")!.textContent).toBe("disconnectedSales");
    expect((container.querySelector(".q-tab") as HTMLElement).dataset.disc).toBe("true");
    useConnections.setState({ disconnected: {} });
  });
  it("has the tab menu: Close first, no rename or pin for a tool (D17, D19)", () => {
    setTool(tool("erd", { profileId: "ms1", database: "Billing" }));
    const { container } = header();
    fireEvent.contextMenu(container.querySelector(".q-tab")!, { clientX: 10, clientY: 10 });
    const items = screen.getAllByRole("menuitem").map((b) => b.getAttribute("aria-label"));
    expect(items[0]).toBe("Close");
    expect(items).not.toContain("Rename");
    expect(items).not.toContain("Pin");
    fireEvent.click(screen.getByRole("menuitem", { name: "Close" }));
    expect(h.closeTool).toHaveBeenCalledWith("x1");
  });
  it("the close button and middle click close the tool; other buttons do not", () => {
    setTool(tool("erd"));
    const { container } = header();
    fireEvent.click(screen.getByLabelText("Close T-erd"));
    expect(h.closeTool).toHaveBeenCalledWith("x1");
    h.closeTool.mockClear();
    const tab = container.querySelector(".q-tab") as Element;
    fireEvent.mouseDown(tab, { button: 1 });
    expect(h.closeTool).toHaveBeenCalledWith("x1");
    h.closeTool.mockClear();
    fireEvent.mouseDown(tab, { button: 0 });
    expect(h.closeTool).not.toHaveBeenCalled();
  });
  it("the close slot is on line 1's row, as on a query tab; line 2 is outside it (D12, owner revision 3)", () => {
    setTool(tool("erd", { profileId: "ms1", database: "Billing" }));
    const { container } = header();
    const top = container.querySelector(".q-tab-main > .q-tab-top")!;
    expect(top.querySelector(".q-tab-l1")).not.toBeNull();
    expect(top.querySelector(".q-tab-slot [aria-label='Close T-erd']")).not.toBeNull();
    expect(top.querySelector("[data-q-tab-subtitle]")).toBeNull();
    expect(container.querySelector(".q-tab > .q-tab-slot")).toBeNull();
  });
});

// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";

const h = vi.hoisted(() => ({
  actions: { applyProcedure: vi.fn(), changeDatabase: vi.fn(), closeTab: vi.fn(async () => true), compareWithServer: vi.fn(), setBottomTab: vi.fn(), toggleBottom: vi.fn(), toggleMaximize: vi.fn(), duplicateTab: vi.fn(), keepPreview: vi.fn(), workspaceHooks: {} as any },
  tabActions: {
    closeTabs: vi.fn(async () => {}), reopenClosed: vi.fn(), stripOrder: vi.fn((): string[] => []), togglePin: vi.fn(), middleClickClose: vi.fn(), moveTabBy: vi.fn(),
    planClose: vi.fn((scope: string, _id: string) => ({ close: scope === "others" ? ["t2", "t3"] : scope === "connection" ? ["t1"] : [], running: [], inTx: scope === "left" ? ["t0"] : [] })),
  },
  attach: vi.fn(),
  focus: vi.fn(),
  models: {} as Record<string, string>,
  contentCbs: [] as ((id: string) => void)[],
  data: undefined as any,
}));

vi.mock("../actions", () => h.actions);
vi.mock("../tabActions", () => h.tabActions);
vi.mock("../../editor/editors", () => ({
  attachEditor: (...a: any[]) => { h.attach(...a); return { focus: h.focus }; },
  getModel: (id: string) => (id in h.models ? { getValue: () => h.models[id] } : undefined),
  onModelContent: (cb: any) => { h.contentCbs.push(cb); return () => h.contentCbs.splice(h.contentCbs.indexOf(cb), 1); },
}));
// The panel's views have their own tests (BottomPanel.test and each view's); here only the frame around them.
vi.mock("../BottomPanel", () => ({ BottomPanel: () => <div>bottom-panel</div> }));
// The banner subscribes to monitor notifications through the real transport; it has its own tests (ActivityTab O8/O9).
vi.mock("../../monitor/BlockingBanner", () => ({ BlockingBanner: () => null }));

import { headerRenders, QueryTabHeader, QueryTabPanel } from "../QueryTab";
import { txClockRunning } from "../txClock";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";
import { useExecutions } from "../../store/executions";
import { useUsages } from "../../store/usages";
import { useSettings } from "../../store/settings";
import { useClosedTabs } from "../../store/closedTabs";

const ms: any = { id: "ms1", name: "Sales", engine: "mssql", environment: "prod", auth: "windows" };
const pg: any = { id: "pg1", name: "Analytics", engine: "pg", environment: "dev", auth: "trust" };

function fakeApi(opts: { visible?: boolean; active?: boolean } = {}) {
  const l: Record<string, ((e: any) => void)[]> = {};
  const on = (n: string) => (cb: any) => { (l[n] ??= []).push(cb); return { dispose: () => l[n].splice(l[n].indexOf(cb), 1) }; };
  return {
    api: { isVisible: opts.visible ?? true, isActive: opts.active ?? false, group: { id: "g1" }, onDidVisibilityChange: on("vis"), onDidGroupChange: on("grp"), onDidActiveChange: on("act"), setTitle: vi.fn() } as any,
    emit: (n: string, e?: any) => act(() => (l[n] ?? []).forEach((f) => f(e))),
  };
}
const base = { txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.35, actualPlan: false };
function setTab(extra: any = {}) {
  useTabs.setState({ tabs: { t1: { ...base, id: "t1", title: "one.sql", profileId: "ms1", database: "Sales", ...extra } as any }, order: ["t1"], activeId: "t1", mru: ["t1"] });
}

beforeEach(() => {
  vi.clearAllMocks();
  h.models = { t1: "select 1" };
  h.contentCbs.length = 0;
  h.data = undefined;
  h.tabActions.stripOrder.mockReturnValue(["t1", "t2"]);
  useProfiles.setState({ profiles: [ms, pg], groups: [] });
  useConnections.setState({ disconnected: {} });
  useExecutions.setState({ views: {} });
  useUsages.setState({ byTab: {} } as any);
  useSettings.setState({ panelDock: "bottom" });
  setTab();
});
afterEach(cleanup);

const panel = (api: any) => render(<QueryTabPanel {...({ api, params: { tabId: "t1" }, containerApi: {} } as any)} />);
// Inside a Dockview tab element, which takes the accessible name.
const header = (api: any, tabId = "t1") => render(<div className="dv-tab"><QueryTabHeader {...({ api, params: { tabId }, containerApi: {} } as any)} /></div>);
const q = (c: HTMLElement) => c.querySelector(".q-tab") as HTMLElement;

describe("QueryTabPanel", () => {
  it("renders nothing for a closed tab", () => {
    useTabs.setState({ tabs: {}, order: [] });
    const { container } = panel(fakeApi().api);
    expect(container).toBeEmptyDOMElement();
  });

  it("attaches the editor to its group when visible and focuses the active tab", () => {
    panel(fakeApi().api);
    expect(h.attach).toHaveBeenCalledWith("g1", expect.any(HTMLElement), "t1", false);
    expect(h.focus).toHaveBeenCalled();
  });

  it("does not attach while hidden, and attaches when it becomes visible; read-only tabs attach read-only", () => {
    setTab({ readOnly: true });
    const f = fakeApi({ visible: false });
    panel(f.api);
    expect(h.attach).not.toHaveBeenCalled();
    f.emit("vis", { isVisible: true });
    expect(h.attach).toHaveBeenCalledWith("g1", expect.any(HTMLElement), "t1", true);
    f.api.group.id = "g2";
    f.emit("grp");
    expect(h.attach).toHaveBeenLastCalledWith("g2", expect.any(HTMLElement), "t1", true);
  });

  it("hides the bottom panel when closed, and the splitter double-click toggles it", () => {
    const { container } = panel(fakeApi().api);
    const splitter = container.querySelector(".cursor-row-resize")!;
    fireEvent.doubleClick(splitter);
    expect(h.actions.toggleBottom).toHaveBeenCalledWith("t1");
    cleanup();
    setTab({ bottomOpen: false });
    panel(fakeApi().api);
    expect(screen.queryByText("bottom-panel")).toBeNull();
  });

  it("the splitter is a focusable separator: arrows resize by 5 %, Home/End go to the limits, Enter hides the panel", () => {
    panel(fakeApi().api);
    const sep = screen.getByRole("separator", { name: "Resize the results panel" });
    expect(sep).toHaveAttribute("tabindex", "0");
    expect(sep).toHaveAttribute("aria-orientation", "horizontal");
    expect(sep).toHaveAttribute("aria-valuenow", "35");
    fireEvent.keyDown(sep, { key: "ArrowUp" });
    expect(useTabs.getState().tabs.t1.bottomSize).toBeCloseTo(0.4);
    fireEvent.keyDown(sep, { key: "ArrowDown" });
    fireEvent.keyDown(sep, { key: "ArrowDown" });
    expect(useTabs.getState().tabs.t1.bottomSize).toBeCloseTo(0.3);
    fireEvent.keyDown(sep, { key: "End" });
    expect(useTabs.getState().tabs.t1.bottomSize).toBe(0.85);
    fireEvent.keyDown(sep, { key: "Home" });
    expect(useTabs.getState().tabs.t1.bottomSize).toBe(0.12);
    fireEvent.keyDown(sep, { key: "Enter" });
    expect(h.actions.toggleBottom).toHaveBeenCalledWith("t1");
  });

  it("maximized: the panel fills the tab, the editor keeps a strip with its first line and Restore, and the splitter goes", async () => {
    h.models.t1 = "\n  SELECT * FROM dbo.Invoice;\nGO";
    setTab({ bottomMax: true });
    const { container } = panel(fakeApi().api);
    expect(screen.getByText("bottom-panel")).toBeInTheDocument();
    expect(screen.queryByRole("separator")).toBeNull();
    expect(screen.getByText("SELECT * FROM dbo.Invoice;")).toBeInTheDocument();
    expect((container.querySelector("[data-q-editor-column]") as HTMLElement).style.height).toBe("28px");
    await userEvent.setup().click(screen.getByText("Restore"));
    expect(h.actions.toggleMaximize).toHaveBeenCalledWith("t1");
  });

  it("docked right: the panel sits beside the editor, the separator is vertical and dragging follows x", () => {
    useSettings.setState({ panelDock: "right" });
    const { container } = panel(fakeApi().api);
    expect(container.querySelector("[data-q-dock='right']")).toBeTruthy();
    const sep = screen.getByRole("separator");
    expect(sep).toHaveAttribute("aria-orientation", "vertical");
    const root = container.firstChild as HTMLElement;
    root.getBoundingClientRect = () => ({ left: 0, width: 1000 }) as DOMRect;
    fireEvent.mouseDown(sep);
    fireEvent.mouseMove(window, { clientX: 600 });
    expect(useTabs.getState().tabs.t1.bottomSize).toBeCloseTo(0.4);
    fireEvent.mouseUp(window);
    fireEvent.keyDown(sep, { key: "ArrowLeft" });
    expect(useTabs.getState().tabs.t1.bottomSize).toBeCloseTo(0.45);
  });

  it("dragging the splitter resizes the bottom panel within bounds", () => {
    const { container } = panel(fakeApi().api);
    const root = container.firstChild as HTMLElement;
    root.getBoundingClientRect = () => ({ top: 0, height: 1000 }) as DOMRect;
    fireEvent.mouseDown(container.querySelector(".cursor-row-resize")!);
    fireEvent.mouseMove(window, { clientY: 400 });
    expect(useTabs.getState().tabs.t1.bottomSize).toBeCloseTo(0.6);
    fireEvent.mouseMove(window, { clientY: 990 });
    expect(useTabs.getState().tabs.t1.bottomSize).toBe(0.12);
    fireEvent.mouseMove(window, { clientY: 0 });
    expect(useTabs.getState().tabs.t1.bottomSize).toBe(0.85);
    fireEvent.mouseUp(window);
    fireEvent.mouseMove(window, { clientY: 500 });
    expect(useTabs.getState().tabs.t1.bottomSize).toBe(0.85);
  });

  describe("context bar", () => {
    it("is absent for a plain tab", () => {
      panel(fakeApi().api);
      expect(screen.queryByText(/BOUND/)).toBeNull();
      expect(screen.queryByText(/Read-only/)).toBeNull();
    });
    it.each([
      ["proc", "BOUND STORED PROCEDURE"],
      ["view", "BOUND VIEW"],
      ["function", "BOUND FUNCTION"],
    ])("bound %s shows its badge, hash, and the compare/apply buttons", async (kind, badge) => {
      setTab({ boundObject: { profileId: "ms1", database: "Sales", schema: "dbo", name: "p", kind, definitionHash: "abcdef0123456789" } });
      panel(fakeApi().api);
      expect(screen.getByText(badge)).toBeInTheDocument();
      expect(screen.getByText("Sales.dbo.p")).toBeInTheDocument();
      expect(screen.getByText("definition hash abcdef01")).toBeInTheDocument();
      const user = userEvent.setup();
      await user.click(screen.getByText(/Compare with server/));
      await user.click(screen.getByText(/Apply/));
      expect(h.actions.compareWithServer).toHaveBeenCalledWith("t1");
      expect(h.actions.applyProcedure).toHaveBeenCalledWith("t1");
    });
    it("read-only tabs explain how to edit", () => {
      setTab({ readOnly: true });
      panel(fakeApi().api);
      expect(screen.getByText(/Read-only definition/)).toBeInTheDocument();
    });
    it("SQL Server: a USE that differs from the tab database offers to switch (debounced on edits)", async () => {
      h.models.t1 = "USE Other;\nselect 1";
      panel(fakeApi().api);
      expect(screen.getByText(/differs from the tab's database/)).toBeInTheDocument();
      await userEvent.setup().click(screen.getByText("Switch tab to Other"));
      expect(h.actions.changeDatabase).toHaveBeenCalledWith("t1", "Other");
    });
    it("SQL Server: no hint when USE matches (case-insensitively), and edits are re-checked after a pause", async () => {
      h.models.t1 = "USE sales";
      panel(fakeApi().api);
      expect(screen.queryByText(/differs/)).toBeNull();
      h.models.t1 = "USE Else";
      act(() => h.contentCbs.forEach((c) => c("other-tab")));
      await act(async () => { await new Promise((r) => setTimeout(r, 450)); });
      expect(screen.queryByText(/differs/)).toBeNull();
      act(() => h.contentCbs.forEach((c) => c("t1")));
      act(() => h.contentCbs.forEach((c) => c("t1")));
      await waitFor(() => expect(screen.getByText(/differs/)).toBeInTheDocument(), { timeout: 1500 });
    });
    it("PostgreSQL: USE statements are not interpreted", () => {
      setTab({ profileId: "pg1", database: "app" });
      h.models.t1 = "USE Other;";
      panel(fakeApi().api);
      expect(screen.queryByText(/differs/)).toBeNull();
      expect(h.contentCbs).toHaveLength(0);
    });
  });

});

describe("QueryTabHeader", () => {
  it("renders nothing for a closed tab", () => {
    useTabs.setState({ tabs: {}, order: [] });
    const { container } = header(fakeApi().api);
    expect(container.querySelector(".dv-tab")).toBeEmptyDOMElement();
  });

  it.each([
    ["ms1", "Production", true],
    ["pg1", "Development", false],
  ])("on %s: the name without .sql; line 2 is the database in its environment's colour and the server, with no environment word (owner revision 2)", (pid, env, prod) => {
    setTab({ profileId: pid });
    const { container } = header(fakeApi().api);
    expect(screen.getByText("one")).toBeInTheDocument();
    expect(screen.queryByText("one.sql")).toBeNull();
    const sub = container.querySelector("[data-q-tab-subtitle]") as HTMLElement;
    const server = pid === "ms1" ? "Sales" : "Analytics";
    expect(sub.textContent).toBe(`Sales· ${server}`);
    expect(sub.textContent).not.toMatch(/PROD|DEV|Prod|Dev/);
    expect((sub.querySelector(".q-tab-db") as HTMLElement).style.color).not.toBe("");
    expect((sub.querySelector(".q-tab-server") as HTMLElement).style.color).toBe("");
    // The environment is still said: in the accessible name and the tooltip; Prod also keeps its band.
    expect(container.querySelector(".dv-tab")!.getAttribute("aria-label")).toBe(`one, ${env}, ${server}, database Sales`);
    expect(q(container).title).toContain(`${prod ? "PROD" : "DEV"} ${server} › Sales`);
    expect(!!container.querySelector(".q-tab-band")).toBe(prod);
  });

  it.each([
    ["connected", false],
    ["disconnected", true],
  ])("the database is never cut (%s): the server takes the …, and the tab widens to the whole name", (_s, disc) => {
    setTab({ profileId: "pg1", database: "AdventureWorks2022_Archive_Reporting" });
    if (disc) useConnections.setState({ disconnected: { pg1: true } });
    const rect = (left: number, right: number) => ({ left, right, width: right - left, top: 0, bottom: 14, height: 14, x: left, y: 0, toJSON() {} }) as DOMRect;
    const spy = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      if (this.matches("[data-q-tab-db-full]")) return rect(30, 260);
      if (this.matches(".dv-tab")) return rect(10, 130);
      return rect(0, 0);
    });
    try {
      const { container } = header(fakeApi().api);
      const db = container.querySelector("[data-q-tab-db-full]") as HTMLElement;
      expect(db.textContent).toBe("AdventureWorks2022_Archive_Reporting");
      expect(db.querySelector(".q-cut-head, .q-cut-tail")).toBeNull();
      expect(container.querySelector(".q-tab-server")!.textContent).toBe("· Analytics");
      // 260 − 10 + the header's right padding (none in jsdom): over the stylesheet minimum, so it is set inline.
      const tab = container.querySelector(".dv-tab") as HTMLElement;
      expect(tab.style.getPropertyValue("min-width")).toBe("250px");
      expect(tab.style.getPropertyPriority("min-width")).toBe("important");
    } finally {
      spy.mockRestore();
    }
  });

  it("a tab whose database fits keeps the stylesheet minimum, and the inline one goes when the database does", () => {
    setTab({ profileId: "pg1", database: "db" });
    const spy = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      const r = this.matches("[data-q-tab-db-full]") ? [30, 50] : [10, 130];
      return { left: r[0], right: r[1], width: r[1] - r[0], top: 0, bottom: 0, height: 0, x: 0, y: 0, toJSON() {} } as DOMRect;
    });
    const style = document.createElement("style");
    style.textContent = ".dv-tab { min-width: 120px; }";
    document.head.appendChild(style);
    try {
      const { container } = header(fakeApi().api);
      const tab = container.querySelector(".dv-tab") as HTMLElement;
      expect(tab.style.getPropertyValue("min-width")).toBe("");
      act(() => useTabs.setState((s) => ({ tabs: { ...s.tabs, t1: { ...s.tabs.t1, database: "a_much_longer_database_name" } } })));
      expect(container.querySelector("[data-q-tab-db-full]")!.textContent).toBe("a_much_longer_database_name");
      act(() => useTabs.setState((s) => ({ tabs: { ...s.tabs, t1: { ...s.tabs.t1, profileId: undefined } } })));
      expect(tab.style.getPropertyValue("min-width")).toBe("");
    } finally {
      spy.mockRestore();
      style.remove();
    }
  });

  it("without a connection says so, in words and in the spoken name", () => {
    setTab({ profileId: undefined });
    const { container } = header(fakeApi().api);
    expect(screen.getByText("No connection")).toBeInTheDocument();
    expect(container.querySelector(".dv-tab")!.getAttribute("aria-label")).toBe("one, no connection");
    expect(container.querySelector(".q-tab-band")).toBeNull();
  });

  it("a disconnected server: plug and the word first, the database keeps its colour, and a wider minimum (D8)", () => {
    setTab({ profileId: "pg1" });
    const a = header(fakeApi().api);
    const color = (a.container.querySelector(".q-tab-db") as HTMLElement).style.color;
    a.unmount();
    useConnections.setState({ disconnected: { pg1: true } });
    const { container } = header(fakeApi().api);
    const sub = container.querySelector("[data-q-tab-subtitle]") as HTMLElement;
    expect(sub.textContent).toBe("disconnectedSales· Analytics");
    expect((sub.querySelector(".q-tab-db") as HTMLElement).style.color).toBe(color);
    expect(q(container).dataset.disc).toBe("true");
    expect(container.querySelector(".dv-tab")!.getAttribute("aria-label")).toContain("Analytics, server disconnected");
    expect(q(container).title).toContain("(disconnected)");
  });

  it("shows running, the transaction pill with its age, read-only and modified; the tooltip and spoken name list them", () => {
    setTab({ busy: true, txState: "open", txStartedAt: Date.now() - 12 * 60_000 - 1000, spid: 53, readOnly: true, dirty: true, filePath: "/w/a.sql" });
    const { container } = header(fakeApi().api);
    expect(container.querySelector(".q-spin")).not.toBeNull();
    expect(container.querySelector(".q-tx-pill")!.textContent).toBe("TX 12m");
    expect(container.querySelector(".q-tab-dot")).not.toBeNull();
    expect(q(container).dataset.dirty).toBe("true");
    expect(q(container).title).toBe("one.sql · modified\n/w/a.sql\nPROD Sales › Sales\nTransaction open 12 min · SPID 53\nQuery running");
    expect(container.querySelector(".dv-tab")!.getAttribute("aria-label")).toBe("one, modified, read-only, Production, Sales, database Sales, query running, open transaction");
  });

  it("all transaction pills share one clock, which stops when none is left", () => {
    useTabs.setState({
      tabs: Object.fromEntries(["t1", "t2", "t3"].map((id) => [id, { ...base, id, title: `${id}.sql`, txState: "open", txStartedAt: Date.now() } as any])),
      order: ["t1", "t2", "t3"],
    });
    const views = ["t1", "t2", "t3"].map((id) => header(fakeApi().api, id));
    expect(document.querySelectorAll(".q-tx-pill")).toHaveLength(3);
    expect(txClockRunning()).toBe(true);
    for (const v of views) v.unmount();
    expect(txClockRunning()).toBe(false);
  });

  it.each([
    ["DROP Orders.sql", "DROP", true, "Orders"],
    ["DELETE sales.Invoice.sql", "DELETE", true, "sales.Invoice"],
    ["CREATE orders_daily.sql", "CREATE", false, "orders_daily"],
    ["EXEC usp_PostOrders.sql", "EXEC", false, "usp_PostOrders"],
  ])("%s: the verb stands on its own and is never cut; destructive verbs are marked", (title, verb, danger, name) => {
    setTab({ title });
    const { container } = header(fakeApi().api);
    const v = container.querySelector(".q-tab-verb") as HTMLElement;
    expect(v.textContent).toBe(verb);
    expect(v.hasAttribute("data-danger")).toBe(danger);
    expect(container.querySelector("[data-q-tab-title]")!.textContent).toBe(name);
  });

  it("a long name keeps its last 7 characters; a debugger tab shows a bug icon instead of the emoji", () => {
    setTab({ title: "reconcile_invoice_payments_2026_10.sql" });
    const a = header(fakeApi().api);
    expect(a.container.querySelector("[data-q-tab-title] .q-cut-tail")!.textContent).toBe("2026_10");
    a.unmount();
    setTab({ title: "🐞 usp_PostOrders", readOnly: true });
    const { container } = header(fakeApi().api);
    expect(container.querySelector("[data-q-tab-title]")!.textContent).toBe("usp_PostOrders");
    expect(container.querySelector(".dv-tab")!.getAttribute("aria-label")).toMatch(/^usp_PostOrders, debugger, read-only/);
  });

  it("two tabs with the same name on the same database get their folder", () => {
    useTabs.setState({
      tabs: {
        t1: { ...base, id: "t1", title: "report.sql", profileId: "ms1", database: "Sales", filePath: "/w/q3/report.sql" } as any,
        t2: { ...base, id: "t2", title: "report.sql", profileId: "ms1", database: "Sales", filePath: "/w/q4/report.sql" } as any,
        t3: { ...base, id: "t3", title: "report.sql", profileId: "ms1", database: "Other", filePath: "/w/q5/report.sql" } as any,
      },
      order: ["t1", "t2", "t3"],
    });
    expect(header(fakeApi().api, "t1").container.querySelector(".q-tab-suffix")!.textContent).toBe("q3/");
    expect(header(fakeApi().api, "t3").container.querySelector(".q-tab-suffix")).toBeNull();
  });

  it("the close slot: × closes; middle click goes through the guarded close; pinned tabs show the pin and no ×", () => {
    const { container } = header(fakeApi().api);
    const x = screen.getByLabelText("Close one");
    expect(x.getAttribute("tabindex")).toBe("-1");
    fireEvent.click(x);
    expect(h.actions.closeTab).toHaveBeenCalledWith("t1");
    fireEvent.mouseDown(q(container), { button: 1 });
    expect(h.tabActions.middleClickClose).toHaveBeenCalledWith("t1");
    h.tabActions.middleClickClose.mockClear();
    fireEvent.mouseDown(q(container), { button: 0 });
    expect(h.tabActions.middleClickClose).not.toHaveBeenCalled();
    cleanup();
    setTab({ pinned: true });
    const p = header(fakeApi().api);
    expect(q(p.container).dataset.pinned).toBe("true");
    expect(screen.getByLabelText("Pinned")).toBeInTheDocument();
    expect(screen.queryByLabelText("Close one")).toBeNull();
  });

  it("the close slot is on line 1's row, so line 2 runs the tab's full width (D12, owner revision 3)", () => {
    setTab({ dirty: true });
    const { container } = header(fakeApi().api);
    const top = container.querySelector(".q-tab-main > .q-tab-top")!;
    expect(top.querySelector("[data-q-tab-title]")).not.toBeNull();
    expect(top.querySelector(".q-tab-slot .q-tab-dot")).not.toBeNull();
    expect(top.querySelector(".q-tab-slot [aria-label='Close one']")).not.toBeNull();
    expect(top.querySelector("[data-q-tab-subtitle]")).toBeNull();
    expect(container.querySelector(".q-tab-main > [data-q-tab-subtitle]")).not.toBeNull();
    // Nothing beside the two lines takes a column of its own.
    expect(container.querySelector(".q-tab > .q-tab-slot")).toBeNull();
  });

  it("double-clicking the slot neither renames nor keeps a preview; a rename keeps the slot's room (data-editing)", () => {
    setTab({ preview: true });
    const { container } = header(fakeApi().api);
    fireEvent.doubleClick(container.querySelector(".q-tab-slot")!);
    expect(screen.queryByLabelText("Tab name")).toBeNull();
    expect(h.actions.keepPreview).not.toHaveBeenCalled();
    fireEvent.doubleClick(screen.getByText("one"));
    expect(h.actions.keepPreview).toHaveBeenCalledWith("t1");
    cleanup();
    setTab({});
    const b = header(fakeApi().api);
    expect(q(b.container).dataset.editing).toBeUndefined();
    fireEvent.doubleClick(screen.getByText("one"));
    expect(screen.getByLabelText("Tab name").closest(".q-tab-top")!.querySelector(".q-tab-slot")).not.toBeNull();
    expect(q(b.container).dataset.editing).toBe("true");
  });

  it("re-renders only for what it shows: not on activation, results or bottom-panel changes (D26)", () => {
    useTabs.setState({ tabs: { ...useTabs.getState().tabs, t2: { ...base, id: "t2", title: "two.sql" } as any }, order: ["t1", "t2"] });
    header(fakeApi().api);
    headerRenders.count = 0;
    act(() => {
      useTabs.getState().setActive("t2");
      useTabs.getState().setActive("t1");
      useTabs.getState().update("t1", { bottomSize: 0.5 });
      useTabs.getState().update("t1", { bottomTab: "messages", bottomOpen: false });
      useTabs.getState().update("t1", { executions: ["e1"], activeExecutionId: "e1" });
      useTabs.getState().update("t2", { busy: true });
    });
    expect(headerRenders.count).toBe(0);
    act(() => useTabs.getState().update("t1", { busy: true }));
    expect(headerRenders.count).toBe(1);
  });

  describe("rename", () => {
    it("double-click edits; Enter commits the new title and updates Dockview", async () => {
      const f = fakeApi();
      header(f.api);
      fireEvent.doubleClick(screen.getByText("one"));
      const input = screen.getByLabelText("Tab name") as HTMLInputElement;
      expect(input.selectionEnd).toBe(3); // name without extension
      const user = userEvent.setup();
      await user.clear(input);
      await user.type(input, "renamed.sql{Enter}");
      expect(useTabs.getState().tabs.t1.title).toBe("renamed.sql");
      expect(f.api.setTitle).toHaveBeenCalledWith("renamed.sql");
      expect(screen.queryByLabelText("Tab name")).toBeNull();
    });
    it("Escape cancels; blank names are rejected; blur commits once", async () => {
      const f = fakeApi();
      header(f.api);
      const user = userEvent.setup();
      fireEvent.doubleClick(screen.getByText("one"));
      await user.type(screen.getByLabelText("Tab name"), "zzz{Escape}");
      expect(useTabs.getState().tabs.t1.title).toBe("one.sql");
      fireEvent.doubleClick(screen.getByText("one"));
      await user.clear(screen.getByLabelText("Tab name"));
      await user.keyboard("{Enter}");
      expect(useTabs.getState().tabs.t1.title).toBe("one.sql");
      expect(f.api.setTitle).not.toHaveBeenCalled();
      fireEvent.doubleClick(screen.getByText("one"));
      await user.clear(screen.getByLabelText("Tab name"));
      await user.type(screen.getByLabelText("Tab name"), "blur.sql");
      fireEvent.blur(screen.getByLabelText("Tab name"));
      expect(useTabs.getState().tabs.t1.title).toBe("blur.sql");
    });
    it("works for pinned tabs", async () => {
      setTab({ pinned: true });
      header(fakeApi().api);
      fireEvent.doubleClick(screen.getByText("one"));
      const user = userEvent.setup();
      await user.type(screen.getByLabelText("Tab name"), "{Escape}");
      expect(screen.queryByLabelText("Tab name")).toBeNull();
    });
  });

  describe("context menu", () => {
    const openMenu = (c: HTMLElement) => fireEvent.contextMenu(q(c), { clientX: 50, clientY: 20 });
    const labels = () => screen.getAllByRole("menuitem").map((b) => b.getAttribute("aria-label"));

    it("names its target, puts Close first, counts bulk closes and names the connection (D19)", () => {
      const { container } = header(fakeApi().api);
      openMenu(container);
      const menu = screen.getByRole("menu");
      expect(menu.querySelector("[data-testid='server-chip']")!.textContent).toContain("Sales");
      const items = labels();
      expect(items[0]).toBe("Close");
      expect(items).toContain("Close others (2)");
      expect(items).toContain("Close 1 on PROD Sales");
      expect(items).toContain("Close to the right, None to the right");
      // Every tab to the left holds a transaction: the reason says so (D20).
      expect(items).toContain("Close to the left, Kept: 1 open transaction");
      expect(items).not.toContain("Close empty tabs");
      expect(items).toEqual(expect.arrayContaining(["Rename", "Duplicate", "Pin", "Move", "Split right"]));
    });

    it("runs its items and closes; bulk closes go through the guarded bulk close", async () => {
      const { container } = header(fakeApi().api);
      const user = userEvent.setup();
      const cases: [RegExp, () => void][] = [
        [/^Close$/, () => expect(h.actions.closeTab).toHaveBeenCalledWith("t1")],
        [/^Close others/, () => expect(h.tabActions.closeTabs).toHaveBeenCalledWith("others", "t1")],
        [/^Close 1 on/, () => expect(h.tabActions.closeTabs).toHaveBeenCalledWith("connection", "t1")],
        [/^Close all/, () => expect(h.tabActions.closeTabs).toHaveBeenCalledWith("all", "t1")],
        [/^Pin$/, () => expect(h.tabActions.togglePin).toHaveBeenCalledWith("t1")],
        [/^Duplicate$/, () => expect(h.actions.duplicateTab).toHaveBeenCalledWith("t1")],
      ];
      for (const [name, check] of cases) {
        openMenu(container);
        await user.click(screen.getByRole("menuitem", { name }));
        check();
        expect(screen.queryByRole("menu")).toBeNull();
      }
    });

    it("an unavailable item says why and does nothing", async () => {
      const { container } = header(fakeApi().api);
      openMenu(container);
      await userEvent.setup().click(screen.getByRole("menuitem", { name: /Close to the right/ }));
      expect(h.tabActions.closeTabs).not.toHaveBeenCalled();
    });

    it("Reopen closed tab is unavailable until a tab was closed, then reopens it", async () => {
      useClosedTabs.setState({ list: [] });
      const { container } = header(fakeApi().api);
      openMenu(container);
      expect(screen.getByRole("menuitem", { name: /Reopen closed tab/ }).getAttribute("aria-disabled")).toBe("true");
      cleanup();
      useClosedTabs.getState().push({ closedAt: 1, title: "q.sql", sql: "select 1" });
      const again = header(fakeApi().api);
      openMenu(again.container);
      await userEvent.setup().click(screen.getByRole("menuitem", { name: /Reopen closed tab/ }));
      expect(h.tabActions.reopenClosed).toHaveBeenCalled();
      useClosedTabs.setState({ list: [] });
    });

    it("Rename starts editing; pinned tabs offer Unpin; without a connection there is no header", async () => {
      const { container } = header(fakeApi().api);
      openMenu(container);
      await userEvent.setup().click(screen.getByRole("menuitem", { name: /^Rename/ }));
      expect(screen.getByLabelText("Tab name")).toBeInTheDocument();
      cleanup();
      setTab({ pinned: true, profileId: undefined });
      const p = header(fakeApi().api);
      openMenu(p.container);
      expect(screen.getByRole("menuitem", { name: "Unpin" })).toBeInTheDocument();
      expect(screen.getByRole("menu").querySelector("[data-testid='server-chip']")).toBeNull();
      expect(labels()).toContain("Close tabs on this connection, No connection");
    });

    it("opens from the keyboard at the tab (Shift+F10 / Menu key) and F2 renames (D22)", () => {
      const { container } = header(fakeApi().api);
      const tab = container.querySelector(".dv-tab")!;
      act(() => void tab.dispatchEvent(new CustomEvent("q-tab-menu")));
      expect(screen.getByRole("menu")).toBeInTheDocument();
      cleanup();
      const b = header(fakeApi().api);
      act(() => void b.container.querySelector(".dv-tab")!.dispatchEvent(new CustomEvent("q-tab-rename")));
      expect(screen.getByLabelText("Tab name")).toBeInTheDocument();
    });

    it("the transaction's age is the tab's description, read again on focus", () => {
      setTab({ txState: "open", txStartedAt: Date.now() - 5 * 60_000 });
      const { container } = header(fakeApi().api);
      const tab = container.querySelector(".dv-tab") as HTMLElement;
      expect(tab.getAttribute("aria-description")).toBe("Transaction open 5 min");
    });

    it("dismisses on Escape", async () => {
      const { container } = header(fakeApi().api);
      openMenu(container);
      await userEvent.setup().keyboard("{Escape}");
      expect(screen.queryByRole("menu")).toBeNull();
    });
  });
});

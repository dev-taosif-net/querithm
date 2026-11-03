// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import "./domPolyfills";

const h = vi.hoisted(() => ({
  props: {} as any,
  newQueryTab: vi.fn(),
  activateTab: vi.fn(),
  applyPinnedOrder: vi.fn(),
  disposeGroupEditor: vi.fn(),
  hooks: {} as any,
}));

vi.mock("dockview", () => ({
  DockviewReact: (p: any) => { h.props = p; return <div data-testid="dockview" />; },
}));
vi.mock("dockview/dist/styles/dockview.css", () => ({}));
vi.mock("../QueryTab", () => ({ QueryTabHeader: () => null, QueryTabPanel: () => null }));
vi.mock("../ToolTab", () => ({ ToolTabHeader: () => null, ToolTabPanel: () => null }));
vi.mock("../actions", () => ({ workspaceHooks: h.hooks, newQueryTab: (...a: any[]) => h.newQueryTab(...a), activateTab: (id: string) => h.activateTab(id) }));
vi.mock("../../editor/editors", () => ({ disposeGroupEditor: (id: string) => h.disposeGroupEditor(id) }));
vi.mock("../tabActions", () => ({ applyPinnedOrder: (...a: any[]) => h.applyPinnedOrder(...a) }));
vi.mock("../commands", () => ({
  commands: [{ id: "tab.list", title: "Tabs", keys: ["Ctrl+P"] }],
  keysFor: (c: any) => c.keys ?? [],
  displayKeys: (k: string) => k,
}));

import { Workspace } from "../Workspace";
import { useTabs } from "../../store/tabs";
import { useToolTabs } from "../../store/toolTabs";
import { useUi } from "../../store/ui";
import { useProfiles } from "../../store/profiles";

type Listener = (e: any) => void;
function fakeDock() {
  const panels = new Map<string, any>();
  const listeners: Record<string, Listener> = {};
  const group = { id: "g1", panels: [] as any[] };
  const api: any = {
    activeGroup: group,
    groups: [group],
    activePanel: undefined as any,
    getPanel: (id: string) => panels.get(id),
    addPanel: vi.fn((o: any) => {
      const p = {
        id: o.id, group, options: o,
        api: { setActive: vi.fn(() => { api.activePanel = p; }), moveTo: vi.fn(({ index }: any) => { group.panels.splice(group.panels.indexOf(p), 1); group.panels.splice(index, 0, p); }) },
      };
      panels.set(o.id, p);
      group.panels.push(p);
      if (!o.inactive) api.activePanel = p;
      return p;
    }),
    removePanel: vi.fn((p: any) => { panels.delete(p.id); group.panels.splice(group.panels.indexOf(p), 1); }),
    onDidActivePanelChange: (f: Listener) => { listeners.active = f; },
    onDidMovePanel: (f: Listener) => { listeners.move = f; },
    onDidRemoveGroup: (f: Listener) => { listeners.removeGroup = f; },
  };
  return { api, group, panels, listeners };
}

const base = { txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.3, actualPlan: false };
const addTab = (id: string, extra: any = {}) => useTabs.getState().add({ ...base, id, title: id, ...extra } as any);

beforeEach(() => {
  vi.clearAllMocks();
  for (const k of Object.keys(h.hooks)) delete h.hooks[k];
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useToolTabs.setState({ tools: {}, activeToolId: undefined });
  useUi.setState({ palette: null, dialogs: [] });
  useProfiles.setState({ profiles: [], groups: [] });
});
afterEach(cleanup);

function mount(dock = fakeDock()) {
  const r = render(<Workspace />);
  act(() => h.props.onReady({ api: dock.api }));
  return { ...r, dock };
}

describe("Workspace", () => {
  it("configures Dockview for query and tool panels", () => {
    mount();
    expect(Object.keys(h.props.components)).toEqual(["query", "tool", "notebook"]);
    expect(Object.keys(h.props.tabComponents)).toEqual(["query", "tool"]);
    expect(h.props.defaultRenderer).toBe("onlyWhenVisible");
    expect(h.props.disableFloatingGroups).toBe(true);
  });

  it("restores tabs opened before mount, activating the active one, and re-applies the pinned order", () => {
    addTab("a");
    addTab("b", { pinned: true });
    useTabs.setState({ activeId: "b" });
    const { dock } = mount();
    expect(dock.api.addPanel).toHaveBeenCalledTimes(2);
    const byId = Object.fromEntries(dock.api.addPanel.mock.calls.map((c: any) => [c[0].id, c[0]]));
    expect(byId.a).toMatchObject({ component: "query", params: { tabId: "a" }, inactive: true });
    expect(byId.b).toMatchObject({ inactive: false });
    expect(h.applyPinnedOrder).toHaveBeenCalledWith("b");
    expect(h.applyPinnedOrder).toHaveBeenLastCalledWith();
  });

  it("new tabs open right after the pinned ones; existing panels are not re-added", () => {
    const { dock } = mount();
    addTab("p1", { pinned: true });
    h.hooks.onTabAdded("p1", true);
    addTab("n1");
    h.hooks.onTabAdded("n1", true);
    addTab("n2");
    h.hooks.onTabAdded("n2", true); // goes after p1, before n1
    expect(dock.group.panels.map((p: any) => p.id)).toEqual(["p1", "n2", "n1"]);
    const calls = dock.api.addPanel.mock.calls.length;
    h.hooks.onTabAdded("n2", true);
    h.hooks.onTabAdded("unknown", true);
    expect(dock.api.addPanel).toHaveBeenCalledTimes(calls);
  });

  it("atEnd keeps the saved order", () => {
    const { dock } = mount();
    addTab("x");
    h.hooks.onTabAdded("x", false, true);
    addTab("y");
    h.hooks.onTabAdded("y", false, true);
    expect(dock.group.panels.map((p: any) => p.id)).toEqual(["x", "y"]);
    expect(dock.api.addPanel.mock.calls.at(-1)[0].inactive).toBe(true);
  });

  it("opens, de-duplicates and removes tool panels and query panels", () => {
    const { dock } = mount();
    h.hooks.onToolAdded("tool1", "ER diagram");
    h.hooks.onToolAdded("tool1", "ER diagram");
    expect(dock.api.addPanel).toHaveBeenCalledTimes(1);
    expect(dock.api.addPanel.mock.calls[0][0]).toMatchObject({ component: "tool", params: { toolId: "tool1" }, title: "ER diagram" });
    h.hooks.onTabRemoved("tool1");
    expect(dock.api.removePanel).toHaveBeenCalled();
    h.hooks.onTabRemoved("missing");
    expect(dock.api.removePanel).toHaveBeenCalledTimes(1);
  });

  it("activate and strip/all order hooks read from Dockview", () => {
    const { dock } = mount();
    addTab("a");
    h.hooks.onTabAdded("a", false, true);
    h.hooks.onToolAdded("tool1", "T");
    h.hooks.activate("a");
    expect(dock.panels.get("a").api.setActive).toHaveBeenCalled();
    h.hooks.activate("nope");
    // Tool tabs are in the strip order (D17), and open first after the pinned tabs like query tabs (D16).
    expect(h.hooks.stripOrder("a")).toEqual(["tool1", "a"]);
    expect(h.hooks.stripOrder()).toEqual(["tool1", "a"]);
    expect(h.hooks.allOrder()).toEqual(["tool1", "a"]);
    dock.api.activeGroup = undefined;
    expect(h.hooks.stripOrder()).toEqual(useTabs.getState().order);
  });

  it("moveTab keeps the active panel active", () => {
    const { dock } = mount();
    for (const id of ["a", "b"]) { addTab(id); h.hooks.onTabAdded(id, false, true); }
    dock.api.activePanel = dock.panels.get("a");
    dock.panels.get("b").api.moveTo.mockImplementation(() => { dock.api.activePanel = dock.panels.get("b"); });
    h.hooks.moveTab("b", 0);
    expect(dock.panels.get("a").api.setActive).toHaveBeenCalled();
    h.hooks.moveTab("ghost", 0);
  });

  it("active panel changes bind either the query tab or the tool tab", () => {
    const { dock } = mount();
    addTab("a");
    useToolTabs.setState({ tools: { tool1: { id: "tool1", kind: "erd", title: "T", params: {} } } });
    dock.listeners.active({ id: "tool1" });
    expect(useToolTabs.getState().activeToolId).toBe("tool1");
    expect(useTabs.getState().activeId).toBeUndefined();
    dock.listeners.active({ id: "a" });
    expect(useToolTabs.getState().activeToolId).toBeUndefined();
    expect(useTabs.getState().activeId).toBe("a");
    dock.listeners.active(undefined);
    expect(useTabs.getState().activeId).toBeUndefined();
  });

  it("a moved panel restores the pinned order and the store follows the on-screen order", () => {
    const { dock } = mount();
    for (const id of ["a", "b", "c"]) addTab(id);
    for (const id of ["a", "b", "c"]) h.hooks.onTabAdded(id, false, true);
    dock.group.panels.reverse();
    h.applyPinnedOrder.mockClear();
    dock.listeners.move({ panel: { id: "c" } });
    expect(h.applyPinnedOrder).toHaveBeenCalledWith("c");
    expect(useTabs.getState().order).toEqual(["c", "b", "a"]);
  });

  it("a re-entrant move (from applyPinnedOrder) is ignored", () => {
    const { dock } = mount();
    addTab("a");
    h.applyPinnedOrder.mockClear();
    h.applyPinnedOrder.mockImplementation(() => dock.listeners.move({ panel: { id: "a" } }));
    dock.listeners.move({ panel: { id: "a" } });
    expect(h.applyPinnedOrder).toHaveBeenCalledTimes(1);
    h.applyPinnedOrder.mockReset();
  });

  it("removing a group disposes its editor", () => {
    const { dock } = mount();
    dock.listeners.removeGroup({ id: "g9" });
    expect(h.disposeGroupEditor).toHaveBeenCalledWith("g9");
  });

  it("unmount clears the workspace hooks", () => {
    const { unmount } = mount();
    expect(h.hooks.onTabAdded).toBeTypeOf("function");
    unmount();
    expect(Object.values(h.hooks).every((v) => v === undefined)).toBe(true);
  });
});

describe("Watermark", () => {
  const watermark = () => {
    const W = h.props.watermarkComponent;
    return render(<W />);
  };
  it("offers New Query and, without profiles, creating a connection profile", () => {
    mount();
    watermark();
    fireEvent.click(screen.getByText(/\+ New Query/));
    expect(h.newQueryTab).toHaveBeenCalled();
    fireEvent.click(screen.getByText("+ New connection profile"));
    expect(useUi.getState().dialogs[0]).toEqual({ kind: "profile" });
    expect(screen.getByText(/SQL Server and PostgreSQL/)).toBeInTheDocument();
  });
  it("with profiles it offers Connect…", () => {
    useProfiles.setState({ profiles: [{ id: "ms1" } as any] });
    mount();
    watermark();
    fireEvent.click(screen.getByText("Connect…"));
    expect(useUi.getState().dialogs[0]).toEqual({ kind: "connect" });
  });
});

describe("hidden-tabs button", () => {
  const rect = (left: number, width: number) => ({ left, right: left + width, width, top: 0, bottom: 30, height: 30, x: left, y: 0, toJSON() {} }) as DOMRect;
  /** A group whose strip shows x = 0…300 and tabs laid out at the given left edges, 150 px wide. */
  function strip(tabs: [string, number][]) {
    const element = document.createElement("div");
    const list = document.createElement("div");
    list.className = "dv-tabs-container";
    list.getBoundingClientRect = () => rect(0, 300);
    element.appendChild(list);
    const panels = tabs.map(([id, left]) => {
      const tab = document.createElement("div");
      tab.className = "dv-tab";
      const content = document.createElement("div");
      tab.appendChild(content);
      list.appendChild(tab);
      tab.getBoundingClientRect = () => rect(left, 150);
      return { id, view: { tab: { element: content } }, place: (x: number) => { tab.getBoundingClientRect = () => rect(x, 150); } };
    });
    return { group: { element }, list, panels };
  }
  const button = (s: ReturnType<typeof strip>) => {
    const B = h.props.rightHeaderActionsComponent;
    return render(<B panels={s.panels} group={s.group} />);
  };
  beforeEach(() => {
    vi.stubGlobal("requestAnimationFrame", (f: FrameRequestCallback) => { f(0); return 1; });
    vi.stubGlobal("cancelAnimationFrame", () => {});
  });
  afterEach(() => vi.unstubAllGlobals());

  it("is always there: \"≡\" with no count while every tab fits, opening the list of all tabs (D14)", () => {
    for (const id of ["a", "b"]) addTab(id);
    mount();
    button(strip([["a", 0], ["b", 150]]));
    const b = screen.getByLabelText("All open tabs");
    expect(b.textContent).toBe("");
    fireEvent.click(b);
    expect(useUi.getState().palette).toEqual({ mode: "tabs" });
  });

  it("counts every tab outside the strip, tool tabs too, and opens a list of just those", () => {
    for (const id of ["a", "b", "c", "d"]) addTab(id);
    mount();
    // c is half visible, d is past the edge, tool1 is a tool tab.
    button(strip([["a", 0], ["b", 150], ["c", 225], ["d", 450], ["tool1", 600]]));
    const b = screen.getByLabelText("3 hidden tabs");
    expect(b).toHaveTextContent("3");
    expect(b.title).toContain("3 hidden tabs");
    expect(b.title).toContain("Ctrl+P");
    fireEvent.click(b);
    expect(useUi.getState().palette).toEqual({ mode: "tabs", only: ["c", "d", "tool1"] });
  });

  it("shows the status of hidden tabs: a TX pill that goes to the tab, and how many are running (D14)", () => {
    for (const id of ["a", "b", "c", "d"]) addTab(id);
    useTabs.getState().update("c", { txState: "open" });
    useTabs.getState().update("d", { busy: true });
    useTabs.getState().update("a", { busy: true }); // visible: not counted
    mount();
    button(strip([["a", 0], ["b", 150], ["c", 300], ["d", 450]]));
    fireEvent.click(screen.getByLabelText("Go to the hidden tab with an open transaction"));
    expect(h.activateTab).toHaveBeenCalledWith("c");
    expect(screen.getByLabelText("1 hidden tab running")).toHaveTextContent("1");
    act(() => useTabs.getState().update("c", { txState: "none" }));
    expect(screen.queryByLabelText("Go to the hidden tab with an open transaction")).toBeNull();
  });

  it("re-counts when the strip scrolls", () => {
    for (const id of ["a", "b", "c"]) addTab(id);
    mount();
    const s = strip([["a", 0], ["b", 150], ["c", 300]]);
    button(s);
    expect(screen.getByLabelText("1 hidden tab")).toHaveTextContent("1");
    // Scrolled right by 150 px: a leaves, c comes in.
    s.panels[0].place(-150);
    s.panels[1].place(0);
    s.panels[2].place(150);
    act(() => { s.list.dispatchEvent(new Event("scroll")); });
    fireEvent.click(screen.getByLabelText("1 hidden tab"));
    expect(useUi.getState().palette).toEqual({ mode: "tabs", only: ["a"] });
  });

  it("without a tab strip it counts nothing", () => {
    mount();
    const B = h.props.rightHeaderActionsComponent;
    render(<B panels={[]} group={undefined} />);
    expect(screen.getByLabelText("All open tabs")).toBeInTheDocument();
  });
});


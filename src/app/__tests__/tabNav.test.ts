import { describe, expect, it } from "vitest";
import type { TabState } from "../../store/tabs";
import { closePlan, closeReport, closeTargets, newTabIndex, nthTab, pinnedFirst, reusableTab, stepTab, switcherOrder, tabContent, unsavedAmong } from "../tabNav";

const tab = (id: string, over: Partial<TabState> = {}): TabState =>
  ({ id, title: `${id}.sql`, txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.35, actualPlan: false, ...over }) as TabState;
const record = (...ts: TabState[]) => Object.fromEntries(ts.map((t) => [t.id, t]));

describe("tab navigation", () => {
  const order = ["a", "b", "c"];
  it("steps forward and back, wrapping at both ends", () => {
    expect(stepTab(order, "a", 1)).toBe("b");
    expect(stepTab(order, "c", 1)).toBe("a");
    expect(stepTab(order, "a", -1)).toBe("c");
  });
  it("starts at an end when nothing is active, and handles no tabs", () => {
    expect(stepTab(order, undefined, 1)).toBe("a");
    expect(stepTab(order, "gone", -1)).toBe("c");
    expect(stepTab([], "a", 1)).toBeUndefined();
  });
  it("Alt+N picks position N; Alt+0 the last; past the end is nothing", () => {
    expect(nthTab(order, 1)).toBe("a");
    expect(nthTab(order, 3)).toBe("c");
    expect(nthTab(order, 0)).toBe("c");
    expect(nthTab(order, 5)).toBeUndefined();
  });
  it("Ctrl+Tab lists recent tabs first, drops closed ones, then the rest in strip order", () => {
    expect(switcherOrder(["c", "gone", "a"], ["a", "b", "c", "d"])).toEqual(["c", "a", "b", "d"]);
  });
  it("new tabs open first, right after the pinned tabs", () => {
    expect(newTabIndex(["a", "b"], () => false)).toBe(0);
    expect(newTabIndex(["p1", "p2", "a"], (id) => id.startsWith("p"))).toBe(2);
  });
  it("puts pinned tabs first without reordering either part", () => {
    expect(pinnedFirst(["a", "p1", "b", "p2"], (id) => id.startsWith("p"))).toEqual(["p1", "p2", "a", "b"]);
  });
});

describe("what closing a tab loses", () => {
  it("empty, saved to an unchanged file, read-only definition, or unsaved SQL", () => {
    expect(tabContent(tab("a"), "  \n")).toBe("empty");
    expect(tabContent(tab("a", { filePath: "/x.sql" }), "select 1")).toBe("saved");
    expect(tabContent(tab("a", { filePath: "/x.sql", dirty: true }), "select 1")).toBe("unsaved");
    expect(tabContent(tab("a", { readOnly: true }), "create view v")).toBe("saved");
    expect(tabContent(tab("a"), "select 1")).toBe("unsaved");
  });
});

describe("bulk close targets", () => {
  const tabs = record(
    tab("pin", { pinned: true }),
    tab("ms1", { profileId: "mssql-prof" }),
    tab("empty"),
    tab("pg1", { profileId: "pg-prof", filePath: "/q.sql" }),
    tab("busy", { busy: true }),
    tab("tx", { profileId: "pg-prof", txState: "open" }),
  );
  const text: Record<string, string> = { pin: "x", ms1: "select 1", empty: "", pg1: "select 1", busy: "", tx: "begin" };
  const ctx = (tabId: string) => ({ order: Object.keys(tabs), tabs, tabId, textOf: (id: string) => text[id] });

  it("others: everything but the tab, pinned, running and open-transaction tabs", () => expect(closeTargets("others", ctx("ms1"))).toEqual(["empty", "pg1"]));
  it("left: tabs before it in the strip", () => expect(closeTargets("left", ctx("pg1"))).toEqual(["ms1", "empty"]));
  it("right: tabs after it in the strip, never a running or open-transaction one", () => expect(closeTargets("right", ctx("pg1"))).toEqual([]));
  it("saved: empty or saved to file, idle only", () => expect(closeTargets("saved", ctx("ms1"))).toEqual(["empty", "pg1"]));
  it("empty: idle empty tabs only (a running tab with no text stays)", () => expect(closeTargets("empty", ctx("ms1"))).toEqual(["empty"]));
  it.each([
    ["ms1", ["ms1"]],
    ["pg1", ["pg1"]],
    ["empty", []],
  ])("connection: tabs on %s's connection (SQL Server and PostgreSQL alike; none when unconnected)", (id, expected) => {
    expect(closeTargets("connection", ctx(id))).toEqual(expected);
  });
  it("all: every unpinned idle tab", () => expect(closeTargets("all", ctx("ms1"))).toEqual(["ms1", "empty", "pg1"]));
  it.each([
    ["all", ["busy"], ["tx"]],
    ["right", ["busy"], ["tx"]],
    ["left", [], []],
    ["empty", ["busy"], []],
  ] as const)("%s keeps running and open-transaction tabs in its scope and lists them (D20)", (scope, running, inTx) => {
    const plan = closePlan(scope, ctx(scope === "all" ? "ms1" : "pg1"));
    expect(plan.running).toEqual(running);
    expect(plan.inTx).toEqual(inTx);
  });
  it("a tab whose transaction state is unknown is kept like an open one (M9)", () => {
    const withUnknown = { ...tabs, tx: { ...tabs.tx, txState: "unknown" as const } };
    expect(closePlan("all", { order: Object.keys(withUnknown), tabs: withUnknown, tabId: "ms1", textOf: (id: string) => text[id] }).inTx).toEqual(["tx"]);
  });
  it("connection keeps the open-transaction tab on that connection (PostgreSQL)", () => {
    expect(closePlan("connection", ctx("pg1"))).toEqual({ close: ["pg1"], running: [], inTx: ["tx"] });
  });
  it("names the tabs whose SQL would be lost", () => expect(unsavedAmong(["ms1", "empty", "pg1", "tx"], tabs, (id) => text[id])).toEqual(["ms1", "tx"]));
});

describe("reusing an unconnected tab for a new connection", () => {
  const text: Record<string, string> = { conn: "select 1", draft: "select 2", spare: "", file: "", pinned: "" };
  const textOf = (id: string) => text[id] ?? "";
  it("prefers the active tab when it is unconnected, even with text", () => {
    expect(reusableTab(["conn", "draft"], record(tab("conn", { profileId: "p" }), tab("draft")), "draft", textOf)).toBe("draft");
  });
  it("otherwise takes the first empty, idle, unpinned, fileless spare", () => {
    const tabs = record(tab("conn", { profileId: "p" }), tab("draft"), tab("pinned", { pinned: true }), tab("file", { filePath: "/f.sql" }), tab("spare"));
    expect(reusableTab(Object.keys(tabs), tabs, "conn", textOf)).toBe("spare");
  });
  it("never takes a running, read-only or object-bound tab", () => {
    const tabs = record(tab("conn", { profileId: "p" }), tab("a", { busy: true }), tab("b", { readOnly: true }), tab("c", { boundObject: {} as never }));
    expect(reusableTab(Object.keys(tabs), tabs, "a", textOf)).toBeUndefined();
  });
});

describe("bulk close report", () => {
  it("says how many closed, and why the rest stayed", () => {
    expect(closeReport(9, { running: [], inTx: [] })).toBe("Closed 9 tabs");
    expect(closeReport(1, { running: [], inTx: [] })).toBe("Closed 1 tab");
    expect(closeReport(9, { running: ["a"], inTx: ["b"] })).toBe("Closed 9; kept 2: 1 running, 1 open transaction");
    expect(closeReport(0, { running: [], inTx: ["a", "b"] })).toBe("Closed 0; kept 2: 2 open transactions");
    expect(closeReport(3, { running: ["a", "b"], inTx: [] })).toBe("Closed 3; kept 2: 2 running");
  });
});

describe("bulk close with tool tabs (D17)", () => {
  const tabs = record(tab("pin", { pinned: true }), tab("q1", { profileId: "ms" }), tab("q2", { profileId: "pg" }));
  const tools = { erd: { profileId: "ms" }, cmp: { profileId: "pg" } };
  const order = ["pin", "q1", "erd", "q2", "cmp"];
  const ctx = (tabId: string) => ({ order, tabs, tools, tabId, textOf: () => "x" });
  it("closes tool tabs with the rest; they lose nothing, so they count as saved but never as empty", () => {
    expect(closeTargets("others", ctx("q1"))).toEqual(["erd", "q2", "cmp"]);
    expect(closeTargets("right", ctx("erd"))).toEqual(["q2", "cmp"]);
    expect(closeTargets("saved", ctx("q1"))).toEqual(["erd", "cmp"]);
    expect(closeTargets("empty", ctx("q1"))).toEqual([]);
    expect(closeTargets("all", ctx("q1"))).toEqual(["q1", "erd", "q2", "cmp"]);
  });
  it("on a connection: query and tool tabs on it, from either kind of tab", () => {
    expect(closeTargets("connection", ctx("q1"))).toEqual(["q1", "erd"]);
    expect(closeTargets("connection", ctx("cmp"))).toEqual(["q2", "cmp"]);
  });
});

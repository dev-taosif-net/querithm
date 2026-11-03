import { describe, it, expect, beforeEach } from "vitest";
import { activeTab, cleanTabTitle, newTabId, touchMru, useTabs } from "../tabs";

const s = () => useTabs.getState();

describe("tabs store", () => {
  beforeEach(() => useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] }));

  it("tab ids are unique even within the same millisecond", () => {
    const ids = new Set(Array.from({ length: 100 }, () => newTabId()));
    expect(ids.size).toBe(100);
  });
  it("add fills defaults, appends to the order and activates the tab", () => {
    const t = s().add({ id: "a", title: "Query 1" });
    expect(t).toMatchObject({ txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.35, actualPlan: false });
    s().add({ id: "b", title: "Query 2", bottomOpen: false });
    expect(s().order).toEqual(["a", "b"]);
    expect(s().activeId).toBe("b");
    expect(s().tabs.b.bottomOpen).toBe(false);
    expect(activeTab()?.id).toBe("b");
  });
  it("update merges a patch and ignores unknown tabs", () => {
    s().add({ id: "a", title: "Q" });
    s().update("a", { dirty: true, title: "Q*" });
    expect(s().tabs.a).toMatchObject({ dirty: true, title: "Q*", bottomTab: "results" });
    const before = s();
    s().update("missing", { dirty: true });
    expect(s()).toBe(before);
    expect(s().tabs.missing).toBeUndefined();
  });
  it("closing the active tab activates its left neighbour, or the new first tab", () => {
    for (const id of ["a", "b", "c"]) s().add({ id, title: id });
    s().setActive("b");
    s().remove("b");
    expect(s().order).toEqual(["a", "c"]);
    expect(s().activeId).toBe("a");
    s().remove("a");
    expect(s().activeId).toBe("c");
    s().remove("c");
    expect(s().activeId).toBeUndefined();
    expect(activeTab()).toBeUndefined();
  });
  it("closing an inactive tab keeps the active one", () => {
    for (const id of ["a", "b", "c"]) s().add({ id, title: id });
    s().remove("a");
    expect(s().activeId).toBe("c");
    expect(Object.keys(s().tabs)).toEqual(["b", "c"]);
  });
  it("bySession finds the tab bound to a session", () => {
    s().add({ id: "a", title: "a", sessionId: "s1" });
    s().add({ id: "b", title: "b", sessionId: "s2" });
    expect(s().bySession("s2")?.id).toBe("b");
    expect(s().bySession("nope")).toBeUndefined();
  });
  it("rename sets a tidied title; an empty name or a missing tab changes nothing", () => {
    s().add({ id: "a", title: "SQLQuery3.sql" });
    expect(s().rename("a", "  Employee report.sql  ")).toBe(true);
    expect(s().tabs.a.title).toBe("Employee report.sql");
    expect(s().rename("a", "   ")).toBe(false);
    expect(s().tabs.a.title).toBe("Employee report.sql");
    expect(s().rename("missing", "x")).toBe(false);
    expect(s().tabs.missing).toBeUndefined();
  });
  it("tab titles are one line, at most 120 characters, and cannot pose as a debugger tab", () => {
    expect(cleanTabTitle("a\n\tb   c")).toBe("a b c");
    expect(cleanTabTitle("x".repeat(200))).toHaveLength(120);
    expect(cleanTabTitle("🐞 🐞 trace.sql")).toBe("trace.sql");
    expect(cleanTabTitle("🐞")).toBeNull();
    expect(cleanTabTitle("")).toBeNull();
    expect(cleanTabTitle("bug 🐞.sql")).toBe("bug 🐞.sql");
  });

  it("keeps the most recently used tab first as tabs open, activate and close", () => {
    s().add({ id: "a", title: "a" });
    s().add({ id: "b", title: "b" });
    s().add({ id: "c", title: "c" });
    expect(s().mru).toEqual(["c", "b", "a"]);
    s().setActive("a");
    expect(s().mru).toEqual(["a", "c", "b"]);
    s().remove("c");
    expect(s().mru).toEqual(["a", "b"]);
    s().setActive(undefined);
    expect(s().mru).toEqual(["a", "b"]);
  });

  it("touchMru returns the same list when the tab is already first", () => {
    const mru = ["a", "b"];
    expect(touchMru(mru, "a")).toBe(mru);
    expect(touchMru(mru, "b")).toEqual(["b", "a"]);
  });
});

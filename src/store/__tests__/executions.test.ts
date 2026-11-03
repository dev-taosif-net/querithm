import { describe, it, expect, beforeEach } from "vitest";
import { useExecutions, type ExecutionView } from "../executions";

const view = (id: string, o: Partial<ExecutionView> = {}): ExecutionView => ({
  id, tabId: "t1", sessionId: "s1", state: "running", startedAt: 0, resultSetCount: 0, truncated: false,
  messages: [], statistics: [], plans: [], sql: "SELECT 1", lineOffset: 0, ...o,
});
const s = () => useExecutions.getState();

describe("executions store", () => {
  beforeEach(() => useExecutions.setState({ views: {} }));

  it("put replaces a view with the same id", () => {
    s().put(view("e1"));
    s().put(view("e1", { sql: "SELECT 2" }));
    expect(Object.keys(s().views)).toEqual(["e1"]);
    expect(s().views.e1.sql).toBe("SELECT 2");
  });
  it("patch accepts an object or a function of the current view", () => {
    s().put(view("e1", { resultSetCount: 1 }));
    s().patch("e1", { state: "done" });
    s().patch("e1", (v) => ({ resultSetCount: v.resultSetCount + 1 }));
    expect(s().views.e1).toMatchObject({ state: "done", resultSetCount: 2, sql: "SELECT 1" });
  });
  it("patching an unknown execution is a no-op and never calls the updater", () => {
    const before = s();
    let called = false;
    s().patch("nope", () => { called = true; return {}; });
    expect(called).toBe(false);
    expect(s()).toBe(before);
  });
  it("drop removes only that view", () => {
    s().put(view("e1"));
    s().put(view("e2"));
    s().drop("e1");
    s().drop("missing");
    expect(Object.keys(s().views)).toEqual(["e2"]);
  });
});

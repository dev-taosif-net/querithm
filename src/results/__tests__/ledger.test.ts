import { describe, expect, it } from "vitest";
import { Int32 } from "apache-arrow";
import type { ServerMessage } from "../../ipc/contract";
import { ArrowResultSet, ExecutionData } from "../model";
import { isLatestRun, ledgerItems, lineOf, noticesFor, previousRun, runErrors, selectPatch, selectedItem, stepLedger } from "../ledger";
import { makeBatch, ordersSet, view } from "./fixtures";

const msg = (severity: ServerMessage["severity"], message: string, over: Partial<ServerMessage> = {}): ServerMessage =>
  ({ executionId: "e1", severity, message, batchIndex: 0, ts: 1, ...over }) as ServerMessage;

function data(id: string, ...sets: ArrowResultSet[]) {
  const d = new ExecutionData(id);
  d.resultSets.push(...sets);
  return d;
}

function setOf(index: number, rows: number, opts: { complete?: boolean; truncated?: boolean } = {}) {
  const rs = new ArrowResultSet(index);
  rs.append(makeBatch([{ name: "x", type: new Int32(), values: Array.from({ length: rows }, (_, i) => i) }]));
  if (opts.complete !== false) rs.finish(!!opts.truncated);
  return rs;
}

describe("ledgerItems", () => {
  it("puts result sets and errors in the order they arrived (3 statements, the last one fails)", () => {
    const v = view("e1", {
      messages: [msg("info", "(3 rows affected)"), msg("info", "(1 row affected)"), msg("error", "Invalid object name 'dbo.Invoce'.", { editorLine: 12, number: 208 })],
      setAfter: { 0: 0, 1: 1 },
    });
    const items = ledgerItems(v, data("e1", ordersSet(0), setOf(1, 1)));
    expect(items.map((i) => i.key)).toEqual(["s0", "s1", "e0"]);
    expect(items[0]).toMatchObject({ kind: "set", rows: 3, complete: true, truncated: false });
    expect(items[2]).toMatchObject({ kind: "error", errorIndex: 0 });
  });

  it("an error that happened before a result set comes first (SQL Server goes on past a statement error)", () => {
    const v = view("e1", { messages: [msg("error", "Divide by zero", { editorLine: 2 })], setAfter: { 0: 1 } });
    expect(ledgerItems(v, data("e1", ordersSet(0))).map((i) => i.key)).toEqual(["e0", "s0"]);
  });

  it("without arrival records (bench-injected runs) result sets come first, in index order", () => {
    const v = view("e1", { messages: [msg("error", "boom")] });
    expect(ledgerItems(v, data("e1", setOf(1, 2), setOf(0, 1))).map((i) => i.key)).toEqual(["s0", "s1", "e0"]);
  });

  it("no view, no items; info and warning messages are not chips", () => {
    expect(ledgerItems(undefined, undefined)).toEqual([]);
    expect(ledgerItems(view("e1", { messages: [msg("info", "hi"), msg("warning", "careful")] }), data("e1"))).toEqual([]);
  });

  it("delta compares the same result index of the previous run, only for the same SQL and complete, untruncated sets", () => {
    const now = view("e2", { sql: "select * from t\n" });
    const before = view("e1", { sql: "select * from t" });
    expect(ledgerItems(now, data("e2", setOf(0, 5)), { view: before, data: data("e1", setOf(0, 3)) })[0]).toMatchObject({ delta: 2 });
    expect(ledgerItems(now, data("e2", setOf(0, 5)), { view: before, data: data("e1", setOf(0, 5)) })[0]).toMatchObject({ delta: 0 });
    // Different SQL, a truncated set, a set still streaming, or no matching set: no delta.
    expect(ledgerItems(now, data("e2", setOf(0, 5)), { view: view("e1", { sql: "select 2" }), data: data("e1", setOf(0, 3)) })[0]).toMatchObject({ delta: undefined });
    expect(ledgerItems(now, data("e2", setOf(0, 5)), { view: before, data: data("e1", setOf(0, 3, { truncated: true })) })[0]).toMatchObject({ delta: undefined });
    expect(ledgerItems(now, data("e2", setOf(0, 5, { complete: false })), { view: before, data: data("e1", setOf(0, 3)) })[0]).toMatchObject({ delta: undefined });
    expect(ledgerItems(now, data("e2", setOf(1, 5)), { view: before, data: data("e1", setOf(0, 3)) })[0]).toMatchObject({ delta: undefined });
  });
});

describe("selection", () => {
  const v = view("e1", { messages: [msg("error", "a"), msg("error", "b")], setAfter: { 0: 0, 1: 0 } });
  const items = ledgerItems(v, data("e1", setOf(0, 1), setOf(1, 1)));

  it("shows the chosen error, else the chosen result set, else the first set", () => {
    expect(selectedItem(items, { ledgerError: 1 })?.key).toBe("e1");
    expect(selectedItem(items, { resultIndex: 1 })?.key).toBe("s1");
    expect(selectedItem(items, { resultIndex: 7 })?.key).toBe("s0");
    expect(selectedItem(items, { ledgerError: 9, resultIndex: 1 })?.key).toBe("s1");
    expect(selectedItem([], {})).toBeUndefined();
  });

  it("steps through the ledger and stops at its ends", () => {
    expect(stepLedger(items, { resultIndex: 0 }, 1)).toEqual({ resultIndex: 1, ledgerError: undefined });
    expect(stepLedger(items, { resultIndex: 1 }, 1)).toEqual({ ledgerError: 0 });
    expect(stepLedger(items, { ledgerError: 1 }, 1)).toBeUndefined();
    expect(stepLedger(items, { resultIndex: 0 }, -1)).toBeUndefined();
    expect(stepLedger([], {}, 1)).toBeUndefined();
    expect(selectPatch(items[2])).toEqual({ ledgerError: 0 });
  });
});

describe("noticesFor", () => {
  const tab = { executions: ["e1", "e2", "e3"], activeExecutionId: "e3" };

  it("lists the first error and a row-limit cut once the run has ended", () => {
    const v = view("e3", { messages: [msg("error", "first", { editorLine: 4 }), msg("error", "second")] });
    const n = noticesFor(tab, v, data("e3", setOf(0, 1000, { truncated: true })));
    expect(n.map((x) => x.kind)).toEqual(["error", "limit"]);
    expect(n[0]).toMatchObject({ count: 2, message: { message: "first" } });
    expect(n[1]).toMatchObject({ resultIndex: 0, rows: 1000 });
  });

  it("adds nothing while the run is still going: the line never moves the grid mid-stream", () => {
    expect(noticesFor(tab, view("e3", { state: "streaming", messages: [msg("error", "x")] }), data("e3", setOf(0, 1, { truncated: true })))).toEqual([]);
  });

  it("an older run comes first, even while the newest run streams", () => {
    const older = { ...tab, activeExecutionId: "e2" };
    const n = noticesFor(older, view("e2", { messages: [msg("error", "x")] }), data("e2"));
    expect(n.map((x) => x.kind)).toEqual(["older", "error"]);
    expect(n[0]).toMatchObject({ position: 2, count: 3 });
  });

  it("no view, no notices", () => expect(noticesFor(tab, undefined, undefined)).toEqual([]));
});

describe("helpers", () => {
  it("runErrors, lineOf, previousRun and isLatestRun", () => {
    expect(runErrors(view("e1", { messages: [msg("info", "a"), msg("error", "b")] })).map((m) => m.message)).toEqual(["b"]);
    expect(runErrors(undefined)).toEqual([]);
    expect(lineOf(msg("error", "x", { editorLine: 12, line: 3 }))).toBe(12);
    expect(lineOf(msg("error", "x", { line: 3 }))).toBe(3);
    expect(lineOf(msg("error", "x"))).toBeUndefined();
    expect(previousRun({ executions: ["a", "b"] }, "b")).toBe("a");
    expect(previousRun({ executions: ["a", "b"] }, "a")).toBeUndefined();
    expect(previousRun({ executions: ["a"] }, undefined)).toBeUndefined();
    expect(isLatestRun({ executions: ["a", "b"], activeExecutionId: "b" })).toBe(true);
    expect(isLatestRun({ executions: ["a", "b"], activeExecutionId: "a" })).toBe(false);
    expect(isLatestRun({ executions: [] })).toBe(true);
  });
});

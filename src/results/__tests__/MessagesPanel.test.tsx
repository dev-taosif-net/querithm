// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import type { ServerMessage } from "../../ipc/contract";

const { editor } = vi.hoisted(() => ({
  editor: { revealLineInCenter: vi.fn(), setPosition: vi.fn(), focus: vi.fn() },
}));
vi.mock("../../editor/editors", () => ({ editorForTab: (id: string) => (id === "tab1" ? editor : undefined) }));
vi.mock("../../app/actions", () => ({ runQuery: vi.fn(), setBottomTab: vi.fn(), showRun: vi.fn() }));
vi.mock("../export", () => ({ exportFormats: [], exportResultSet: vi.fn() }));
vi.mock("../gridEdit", () => ({ applyChanges: vi.fn(), reviewChanges: vi.fn(), scheduleTokens: vi.fn(), stagingFor: vi.fn() }));

import { useExecutions } from "../../store/executions";
import { useProfiles } from "../../store/profiles";
import { useTabs } from "../../store/tabs";
import { executionData } from "../model";
import { fmtMs, MessagesPanel, WINDOW_AFTER } from "../MessagesPanel";
import { ordersSet, registerExecution, view } from "./fixtures";
import { openRowPopups } from "../../app/__tests__/rowPopups";

const tab = (over = {}) => ({ id: "tab1", activeExecutionId: "e1", executions: ["e1"], ...over }) as never;
const msg = (over: Record<string, unknown> = {}) => ({ executionId: "e1", severity: "info", message: "hello", batchIndex: 0, ts: 1_700_000_000_000, ...over }) as never;

beforeEach(() => {
  Object.values(editor).forEach((f) => f.mockClear());
  executionData.clear();
  useProfiles.setState({ profiles: [{ id: "ms", engine: "mssql", environment: "dev" } as never, { id: "pg", engine: "pg", environment: "dev" } as never] });
});
afterEach(cleanup);

describe("fmtMs", () => {
  it("formats ms, seconds and minutes", () => {
    expect(fmtMs(null)).toBe("—");
    expect(fmtMs(undefined)).toBe("—");
    expect(fmtMs(12.4)).toBe("12 ms");
    expect(fmtMs(1500)).toBe("1.50 s");
    expect(fmtMs(125_000)).toBe("2:05 min");
  });
});

describe("MessagesPanel", () => {
  it("shows a hint without an execution", () => {
    useExecutions.setState({ views: {} });
    render(<MessagesPanel tab={tab({ activeExecutionId: undefined })} />);
    expect(screen.getByText(/Messages from the server/)).toBeTruthy();
  });

  it("lines the webview wrote itself (a refused statement) end with Not run, not a run summary", () => {
    useExecutions.setState({ views: { e1: view("e1", { state: "failed", notRun: true, elapsedMs: 0, messages: [msg({ severity: "error", message: "ATTACH and DETACH are not available." })] }) } });
    render(<MessagesPanel tab={tab()} />);
    expect(screen.getByText("ATTACH and DETACH are not available.").className).toContain("text-err");
    expect(screen.getByText("Not run")).toBeTruthy();
    expect(screen.queryByText(/Completed with errors/)).toBeNull();
  });

  it("renders messages with severity classes and SQL Server error details", () => {
    useExecutions.setState({
      views: {
        e1: view("e1", {
          state: "failed",
          messages: [
            msg({ message: "printed" }),
            msg({ severity: "warning", message: "careful" }),
            msg({ severity: "error", message: "Incorrect syntax", number: 102, class: 15, state: 1, procedure: "sp_x", line: 3, editorLine: 7 }),
            msg({ severity: "error", message: "plain error" }),
          ],
        }),
      },
    });
    render(<MessagesPanel tab={tab()} />);
    expect(screen.getByText("careful").className).toContain("text-warn");
    expect(screen.getByText("Incorrect syntax").className).toContain("text-err");
    expect(screen.getByText(/Msg 102, Level 15, State 1, Procedure sp_x, Line 3 \(editor line 7\)/)).toBeTruthy();
    expect(screen.getByText("plain error").textContent).not.toContain("Msg");
    expect(screen.getByText(/Completed with errors/)).toBeTruthy();
    // Timestamps are read (when did this print?): the muted tier, never the faint one.
    const stamps = [...document.querySelectorAll("span.mr-2")];
    expect(stamps.length).toBeGreaterThanOrEqual(5);
    for (const t of stamps) expect(t.className).toContain("text-muted");
    expect(document.querySelector(".text-faint")).toBeNull();
  });

  it("clicking a message with an editor line reveals it in that tab's editor", () => {
    useExecutions.setState({ views: { e1: view("e1", { messages: [msg({ message: "boom", severity: "error", editorLine: 9 }), msg({ message: "nolink" })] }) } });
    render(<MessagesPanel tab={tab()} />);
    fireEvent.click(screen.getByText("nolink"));
    expect(editor.revealLineInCenter).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText("boom"));
    expect(editor.revealLineInCenter).toHaveBeenCalledWith(9);
    expect(editor.setPosition).toHaveBeenCalledWith({ lineNumber: 9, column: 1 });
    expect(editor.focus).toHaveBeenCalled();
  });

  it("every popup of the tool row opens outside the row", () => {
    useExecutions.setState({ views: { e1: view("e1", { messages: [msg({ message: "boom", severity: "error" })] }) } });
    render(<MessagesPanel tab={tab()} />);
    expect(openRowPopups()).toEqual(["Copy options"]);
  });

  it("clicking in a tab without an editor does not throw", () => {
    useExecutions.setState({ views: { e1: view("e1", { messages: [msg({ message: "boom", editorLine: 2 })] }) } });
    render(<MessagesPanel tab={tab({ id: "other" })} />);
    fireEvent.click(screen.getByText("boom"));
    expect(editor.revealLineInCenter).not.toHaveBeenCalled();
  });

  it("summarizes completion: done, cancelled, row counts and server times", () => {
    useExecutions.setState({ views: { e1: view("e1", { state: "done", affectedRows: 1234, elapsedMs: 1500, serverElapsedMs: 900, serverCpuMs: 50 }) } });
    const { rerender } = render(<MessagesPanel tab={tab()} />);
    expect(screen.getByText(/Completed/).textContent).toContain("1,234 row(s) affected · client 1.50 s · server 900 ms (CPU 50 ms)");
    useExecutions.setState({ views: { e1: view("e1", { state: "cancelled", elapsedMs: 10 }) } });
    rerender(<MessagesPanel tab={tab()} />);
    expect(screen.getByText(/Cancelled by user/).className).toContain("text-warn");
    useExecutions.setState({ views: { e1: view("e1", { state: "running" }) } });
    rerender(<MessagesPanel tab={tab()} />);
    expect(screen.queryByText(/Completed/)).toBeNull();
  });

  it("renders STATISTICS IO and TIME tables (SQL Server)", () => {
    useExecutions.setState({
      views: {
        e1: view("e1", {
          statistics: [
            { batchIndex: 0, kind: "io", table: "Orders", scanCount: 1, logicalReads: 1200, physicalReads: 3, readAheadReads: 4, lobLogicalReads: 5 },
            { batchIndex: 0, kind: "compile", cpuMs: 2, elapsedMs: 3 },
            { batchIndex: 1, kind: "execution", cpuMs: 8, elapsedMs: 9 },
          ],
        }),
      },
    });
    render(<MessagesPanel tab={tab()} />);
    expect(screen.getByText("STATISTICS IO")).toBeTruthy();
    expect(screen.getByText("Orders")).toBeTruthy();
    expect(screen.getByText("1,200")).toBeTruthy();
    expect(screen.getByText("STATISTICS TIME")).toBeTruthy();
    expect(screen.getByText("parse & compile")).toBeTruthy();
    expect(screen.getByText("execution")).toBeTruthy();
  });

  it("PostgreSQL notices render as plain messages without statistics tables", () => {
    useExecutions.setState({ views: { e1: view("e1", { messages: [msg({ message: "NOTICE: table exists, skipping" })] }) } });
    render(<MessagesPanel tab={tab({ profileId: "pg" })} />);
    expect(screen.getByText("NOTICE: table exists, skipping")).toBeTruthy();
    expect(screen.queryByText("STATISTICS IO")).toBeNull();
    expect(screen.queryByText("STATISTICS TIME")).toBeNull();
    // The gap is said, with what PostgreSQL offers instead.
    expect(document.querySelector("[data-q-capability='statistics']")?.textContent).toMatch(/SQL Server only.*on PostgreSQL, Explain › Actual/);
  });

  it("STATISTICS IO / TIME and Diagnostics collapse to one summary line each", () => {
    useExecutions.setState({
      views: {
        e1: view("e1", {
          statistics: [
            { batchIndex: 0, kind: "io", table: "Orders", scanCount: 1, logicalReads: 1200, physicalReads: 3, readAheadReads: 4, lobLogicalReads: 5 },
            { batchIndex: 0, kind: "io", table: "Lines", scanCount: 1, logicalReads: 4, physicalReads: 0, readAheadReads: 0, lobLogicalReads: 0 },
            { batchIndex: 0, kind: "execution", cpuMs: 8, elapsedMs: 9 },
          ],
        }),
      },
    });
    registerExecution("e1", ordersSet(0));
    render(<MessagesPanel tab={tab({ profileId: "ms" })} />);
    const summaries = [...document.querySelectorAll("summary")].map((x) => x.textContent);
    expect(summaries).toEqual(["STATISTICS IO · 2 tables · 1,204 logical reads", "STATISTICS TIME · 1 phase · CPU 8 ms", "Diagnostics · Arrow stream · 1 batch · 1 result set"]);
    for (const d of document.querySelectorAll("details")) expect(d.open).toBe(false);
  });

  it("the log marks where each result set began, between the messages, and the ledger marks the picked entry", () => {
    useExecutions.setState({
      views: { e1: view("e1", { messages: [msg({ message: "(3 rows affected)" }), msg({ severity: "error", message: "Invalid object name", editorLine: 12 })], setAfter: { 0: 1 } }) },
    });
    registerExecution("e1", ordersSet(0));
    useTabs.setState({ tabs: { tab1: tab() } });
    const { rerender } = render(<MessagesPanel tab={tab()} />);
    const lines = [...document.querySelectorAll("[data-q-entry]")].map((e) => e.getAttribute("data-q-entry"));
    expect(lines).toEqual(["m0", "s0", "e0"]);
    expect(screen.getByText("Result set 1 began")).toBeTruthy();
    // Picking the error chip marks its line.
    fireEvent.click(screen.getByRole("tab", { name: /Error 1, line 12/ }));
    expect(useTabs.getState().tabs.tab1.ledgerError).toBe(0);
    rerender(<MessagesPanel tab={useTabs.getState().tabs.tab1} />);
    expect(document.querySelector("[data-q-entry='e0']")?.className).toContain("border-accent");
  });

  it("Errors shows only the errors; Copy errors only copies them", async () => {
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    useExecutions.setState({ views: { e1: view("e1", { messages: [msg({ message: "printed" }), msg({ severity: "error", message: "bad one", number: 1, line: 2 })] }) } });
    render(<MessagesPanel tab={tab()} />);
    const show = screen.getByRole("group", { name: "Show" });
    fireEvent.click(within(show).getByRole("button", { name: /Errors 1/ }));
    expect(screen.queryByText("printed")).toBeNull();
    expect(screen.getByText("bad one")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Copy options" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Copy errors only" }));
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith(expect.stringMatching(/Msg 1, Line 2: bad one$/));
    expect((navigator.clipboard.writeText as ReturnType<typeof vi.fn>).mock.calls[0][0]).not.toContain("printed");
  });

  it("a long log renders a window of entries, not every line", () => {
    const many = Array.from({ length: WINDOW_AFTER + 600 }, (_, i) => msg({ message: `line ${i}` }));
    useExecutions.setState({ views: { e1: view("e1", { messages: many }) } });
    render(<MessagesPanel tab={tab()} />);
    const shown = document.querySelectorAll("[data-q-entry]").length;
    expect(shown).toBeGreaterThan(0);
    expect(shown).toBeLessThanOrEqual(200);
    expect(screen.getByText("line 0")).toBeTruthy();
    expect(screen.queryByText(`line ${WINDOW_AFTER + 599}`)).toBeNull();
    expect(screen.getByText(`${(WINDOW_AFTER + 600).toLocaleString()} messages`)).toBeTruthy();
  });

  it("batches appended in place (as query.messages does) show up with their markers, counts and filter, then Clear and another run", () => {
    // The list grows in place and the view is patched, as actions.ts appendMessages does.
    const list: ServerMessage[] = [msg({ message: "first" })];
    const append = (...ms: ServerMessage[]) => act(() => {
      list.push(...ms);
      useExecutions.getState().patch("e1", { messages: list });
    });
    const keys = () => [...document.querySelectorAll("[data-q-entry]")].map((e) => e.getAttribute("data-q-entry"));
    useExecutions.setState({ views: { e1: view("e1", { state: "running", messages: list }), e2: view("e2", { messages: [msg({ message: "other run" })] }) } });
    const { rerender } = render(<MessagesPanel tab={tab({ executions: ["e1", "e2"] })} />);
    append(msg({ message: "second" }), msg({ severity: "error", message: "bad", number: 208, line: 1 }));
    expect(keys()).toEqual(["m0", "m1", "e0"]);
    // A result set begins after the 3 messages so far; its marker shows its rows as they stream.
    const data = registerExecution("e1", ordersSet(0));
    act(() => useExecutions.getState().patch("e1", { setAfter: { 0: list.length } }));
    expect(keys()).toEqual(["m0", "m1", "e0", "s0"]);
    expect(screen.getByText("· 3 rows")).toBeTruthy();
    append(msg({ message: "after the set\nwith two lines" }), msg({ severity: "error", message: "worse" }));
    expect(keys()).toEqual(["m0", "m1", "e0", "s0", "m3", "e1"]);
    expect(screen.getByText("5 messages · 2 errors")).toBeTruthy();
    expect(screen.getByRole("button", { name: /All 5/ })).toBeTruthy();
    expect(data.resultSets).toHaveLength(1);
    // Errors only: the errors keep their keys, and a later error joins them.
    fireEvent.click(within(screen.getByRole("group", { name: "Show" })).getByRole("button", { name: /Errors 2/ }));
    expect(keys()).toEqual(["e0", "e1"]);
    append(msg({ message: "quiet" }), msg({ severity: "error", message: "third" }));
    expect(keys()).toEqual(["e0", "e1", "e2"]);
    fireEvent.click(within(screen.getByRole("group", { name: "Show" })).getByRole("button", { name: /All 7/ }));
    expect(keys()).toEqual(["m0", "m1", "e0", "s0", "m3", "e1", "m5", "e2"]);
    // Clear empties the log; the next batch starts a new list.
    fireEvent.click(screen.getByText("Clear"));
    expect(keys()).toEqual([]);
    act(() => useExecutions.getState().patch("e1", { messages: [msg({ message: "fresh" })] }));
    expect(keys()).toEqual(["m0"]);
    expect(screen.getByText("fresh")).toBeTruthy();
    // Another run of the tab shows its own lines.
    rerender(<MessagesPanel tab={tab({ executions: ["e1", "e2"], activeExecutionId: "e2" })} />);
    expect(keys()).toEqual(["m0"]);
    expect(screen.getByText("other run")).toBeTruthy();
  });

  it("a long log keeps its height right as batches arrive: the window's offsets follow every line, multi-line ones too", () => {
    const list: ServerMessage[] = Array.from({ length: WINDOW_AFTER + 100 }, (_, i) => msg({ message: `line ${i}` }));
    useExecutions.setState({ views: { e1: view("e1", { state: "running", messages: list }) } });
    render(<MessagesPanel tab={tab()} />);
    const spacer = () => (document.querySelector("[data-q-messages] > div") as HTMLElement).style.height;
    const line = 19 + 4;
    expect(spacer()).toBe(`${(WINDOW_AFTER + 100) * line}px`);
    act(() => {
      list.push(msg({ message: "a\nb\nc" }), msg({ severity: "error", message: "boom", number: 1 }));
      useExecutions.getState().patch("e1", { messages: list });
    });
    expect(spacer()).toBe(`${(WINDOW_AFTER + 100) * line + (3 * 19 + 4) + (2 * 19 + 4)}px`);
    // The ledger marks the run's first error, and the window moves to it.
    expect(screen.getByText("boom")).toBeTruthy();
    expect(screen.queryByText("line 0")).toBeNull();
    expect(screen.getByText(`${(WINDOW_AFTER + 102).toLocaleString()} messages · 1 error`)).toBeTruthy();
  });

  it("copy all copies messages to clipboard, and clear empties the messages", async () => {
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    useExecutions.setState({ views: { e1: view("e1", { state: "done", messages: [msg({ message: "sample log" })] }) } });
    render(<MessagesPanel tab={tab()} />);
    fireEvent.click(screen.getByRole("button", { name: "Copy all" }));
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith(expect.stringContaining("sample log"));
    fireEvent.click(screen.getByText("Clear"));
    expect(useExecutions.getState().views.e1.messages).toHaveLength(0);
  });
});

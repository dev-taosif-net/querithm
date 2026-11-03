// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useTabs } from "../../store/tabs";
import { useUsages } from "../../store/usages";
import { useUi } from "../../store/ui";
import { kindLabel } from "../refactor";

const m = vi.hoisted(() => ({ revealHit: vi.fn(), runFindUsages: vi.fn(), editors: new Map<string, any>(), models: new Map<string, any>() }));
vi.mock("../refactorActions", () => ({ revealHit: m.revealHit, runFindUsages: m.runFindUsages }));
vi.mock("../../editor/editors", () => ({ editorForTab: (id: string) => m.editors.get(id), getModel: (id: string) => m.models.get(id) }));

const { UsagesPanel } = await import("../UsagesPanel");

const tab = { id: "t", title: "Query.sql", profileId: "p", database: "db" } as never;
const target = { kind: "table", schema: "dbo", name: "Orders", column: null } as const;
const hit = (o: Record<string, unknown>) => ({ id: "h", line: 1, column: 1, offset: 0, length: 1, snippet: "x", confidence: "exact", definition: false, ...o }) as never;
const result = (hits: unknown[], extra: Record<string, unknown> = {}) => ({ target, hits, unchecked: [], serverUpdated: [], objectsScanned: 5, elapsedMs: 12.4, ...extra }) as never;

beforeEach(() => {
  vi.clearAllMocks();
  m.editors.clear(); m.models.clear();
  useUsages.setState({ byTab: {} });
  useTabs.setState({ tabs: { t: tab, other: { id: "other", title: "Other.sql" } as never } } as never);
});
afterEach(cleanup);

describe("UsagesPanel", () => {
  it("hints at Shift+F12 before any search", () => {
    render(<UsagesPanel tab={tab} />);
    expect(screen.getByText(/Shift\+F12 on a table/)).toBeTruthy();
  });

  it("lists a local symbol's occurrences and selects one in the editor on click", () => {
    useUsages.getState().set("t", { local: { name: "@x", spans: [{ offset: 8, length: 2, line: 1, column: 9, snippet: "declare @x int" }] }, running: false });
    const sel = vi.fn(), reveal = vi.fn(), focus = vi.fn();
    m.editors.set("t", { setSelection: sel, revealLineInCenter: reveal, focus });
    m.models.set("t", { getPositionAt: (o: number) => ({ lineNumber: 1, column: o + 1 }) });
    render(<UsagesPanel tab={tab} />);
    expect(screen.getByText("@x")).toBeTruthy();
    fireEvent.click(screen.getByText("declare @x int"));
    expect(sel).toHaveBeenCalledWith({ startLineNumber: 1, startColumn: 9, endLineNumber: 1, endColumn: 11 });
    expect(reveal).toHaveBeenCalledWith(1);
    expect(focus).toHaveBeenCalled();
  });

  it("does nothing when a local occurrence is clicked without an editor", () => {
    useUsages.getState().set("t", { local: { name: "@x", spans: [{ offset: 0, length: 2, line: 1, column: 1, snippet: "@x here" }] }, running: false });
    render(<UsagesPanel tab={tab} />);
    fireEvent.click(screen.getByText("@x here"));
    expect(m.editors.size).toBe(0);
  });

  it("groups hits: tabs first, then objects; shows counts, definition and possible badges", () => {
    useUsages.getState().set("t", {
      target, running: false,
      result: result([
        hit({ id: "1", tabId: "other", line: 3, column: 5, snippet: "from Orders" }),
        hit({ id: "2", object: { schema: "dbo", name: "usp_a", kind: "proc" }, line: 2, snippet: "create proc", definition: true }),
        hit({ id: "3", object: { schema: "dbo", name: "usp_a", kind: "proc" }, line: 9, snippet: "join Orders", confidence: "possible", reason: "unqualified" }),
        hit({ id: "4", tabId: "gone", snippet: "stale" }),
      ], { serverUpdated: [{ schema: "dbo", name: "v1", kind: "view" }] }),
    });
    render(<UsagesPanel tab={tab} />);
    expect(screen.getByText(/3 exact · 1 possible · 3 place\(s\)/)).toBeTruthy();
    expect(screen.getByText(/5 object\(s\) checked in 12 ms/)).toBeTruthy();
    expect(screen.getAllByText("tab")).toHaveLength(2);
    expect(screen.getByText("proc")).toBeTruthy();
    expect(screen.getByText("Other.sql")).toBeTruthy();
    expect(screen.getByText("closed tab")).toBeTruthy();
    expect(screen.getByText("definition")).toBeTruthy();
    expect(screen.getByText("possible")).toBeTruthy();
    expect(screen.getByText(/dbo\.v1/)).toBeTruthy();
    const order = screen.getAllByText(/Other\.sql|closed tab|dbo\.usp_a/).map((e) => e.textContent);
    expect(order.findIndex((t) => t?.includes("usp_a"))).toBeGreaterThan(order.findIndex((t) => t?.includes("Other.sql")));
    // Kind, line:column, counts and the definition badge are read: muted, never faint.
    expect(screen.getAllByText("tab")[0].className).toContain("text-muted");
    expect(screen.getByText("3:5").className).toContain("text-muted");
    expect(screen.getByText("definition").className).toContain("text-muted");
    expect(document.querySelector("[data-q-usages] .text-faint")).toBeNull();
  });

  it("jumps to a hit and collapses a group", () => {
    useUsages.getState().set("t", { target, running: false, result: result([hit({ id: "1", tabId: "other", snippet: "from Orders" })]) });
    render(<UsagesPanel tab={tab} />);
    fireEvent.click(screen.getByText("from Orders"));
    expect(m.revealHit).toHaveBeenCalledWith(expect.objectContaining({ id: "1" }), "p", "db");
    fireEvent.click(screen.getByText("Other.sql"));
    expect(screen.queryByText("from Orders")).toBeNull();
    fireEvent.click(screen.getByText("Other.sql"));
    expect(screen.getByText("from Orders")).toBeTruthy();
  });

  it("says when nothing references the object, and lists unchecked objects with the reason", () => {
    useUsages.getState().set("t", { target, running: false, result: result([], { unchecked: [{ object: { schema: "dbo", name: "enc", kind: "proc" }, reason: "encrypted" }] }) });
    render(<UsagesPanel tab={tab} />);
    expect(screen.getByText("No references found.")).toBeTruthy();
    expect(screen.getByText(/May reference it, cannot be checked \(1\)/)).toBeTruthy();
    expect(screen.getByText(/encrypted/)).toBeTruthy();
  });

  it("shows progress and errors; search again re-runs, rename opens the dialog", () => {
    useUsages.getState().set("t", { target, running: true });
    const { unmount } = render(<UsagesPanel tab={tab} />);
    expect(screen.getByText("Searching…")).toBeTruthy();
    expect((screen.getByText("Search again") as HTMLButtonElement).disabled).toBe(true);
    unmount();
    useUsages.getState().set("t", { target, running: false, error: "timed out", result: result([]) });
    render(<UsagesPanel tab={tab} />);
    expect(screen.getByText("timed out")).toBeTruthy();
    fireEvent.click(screen.getByText("Search again"));
    expect(m.runFindUsages).toHaveBeenCalledWith("t", "p", "db", target);
    fireEvent.click(screen.getByText(/Rename…/));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "rename", profileId: "p", database: "db", target, fromTabId: "t" });
  });

  it("offers no search/rename buttons for a tab without a connection", () => {
    useUsages.getState().set("t", { target, running: false, result: result([]) });
    render(<UsagesPanel tab={{ id: "t", title: "x" } as never} />);
    expect(screen.queryByText("Search again")).toBeNull();
  });

  it("labels column targets", () => {
    useUsages.getState().set("t", { target: { kind: "column", schema: "dbo", name: "Orders", column: "Id" }, running: false });
    render(<UsagesPanel tab={tab} />);
    expect(screen.getByText("dbo.Orders.Id")).toBeTruthy();
    expect(kindLabel("procedure")).toBe("procedure");
    expect(kindLabel("view")).toBe("view");
  });
});

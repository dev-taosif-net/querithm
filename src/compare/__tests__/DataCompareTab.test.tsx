// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createElement } from "react";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useToolTabs, type ToolTab } from "../../store/toolTabs";
import { useExecutions } from "../../store/executions";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), toast: vi.fn(), newQueryTab: vi.fn(), notify: undefined as undefined | ((e: any) => void), stream: { rs: undefined as any }, grid: { props: undefined as any },
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, shell: {}, RpcError: class extends Error {},
  onNotify: (_m: string, h: (e: any) => void) => { m.notify = h; return () => { m.notify = undefined; }; },
}));
vi.mock("../../store/ui", () => ({ toast: m.toast }));
vi.mock("../../app/actions", () => ({ newQueryTab: m.newQueryTab }));
vi.mock("../../results/useExecutionStream", () => ({ useExecutionStream: () => (m.stream.rs ? { resultSets: [m.stream.rs] } : undefined) }));
vi.mock("../../results/ResultGrid", () => ({ ResultGrid: (p: any) => { m.grid.props = p; return createElement("div", { "data-testid": "grid" }); } }));

const { DataCompareTab, dataCompareBench, COMPARE_INTERRUPTED } = await import("../DataCompareTab");

const START = { compareId: "c1", executionId: "e1", key: [{ source: "id", target: "id" }], columns: [{ source: "id", target: "id" }, { source: "name", target: "name" }], notes: ["note one"] };
const progress = (o: Record<string, unknown> = {}) => ({ compareId: "c1", sourceRows: 1200, targetRows: 1100, identical: 1000, different: 5, sourceOnly: 3, targetOnly: 2, done: false, cancelled: false, truncated: false, elapsedMs: 321.6, ...o });

function fakeRs() {
  const meta = (o: Record<string, string>) => new Map(Object.entries(o));
  return {
    setFilter: vi.fn(), complete: false,
    schema: { fields: [{ name: "_diff_kind", metadata: meta({}) }, { name: "_diff_mask", metadata: meta({}) }, { name: "diff", metadata: meta({}) }, { name: "id", metadata: meta({ "q:diffBit": "0" }) }, { name: "name (source)", metadata: meta({ "q:diffBit": "1", "q:diffSide": "source" }) }, { name: "name (target)", metadata: meta({ "q:diffSide": "target" }) }] },
    columns: [{ name: "diff", field: 2 }, { name: "id", field: 3 }, { name: "name (source)", field: 4 }, { name: "name (target)", field: 5 }],
    raw: vi.fn(),
  };
}

function setup(params: Record<string, unknown> = {}, engine: "mssql" | "pg" = "mssql") {
  useProfiles.setState({ profiles: [{ id: "p", name: "Prod", engine } as never] });
  useConnections.setState({
    servers: { p: { profileId: "p", status: "connected", databases: [{ name: "Sales", isSystem: false }, { name: "sys", isSystem: true }] } } as never,
    connect: vi.fn(), sessionFor: vi.fn().mockResolvedValue("sess"),
  } as never);
  useSnapshots.setState({ entries: { [snapKey("p", "Sales")]: { key: "k", profileId: "p", database: "Sales", status: "fresh", version: 1, snapshot: { objects: [{ schema: "dbo", name: "Orders", kind: "table" }, { schema: "dbo", name: "vw", kind: "view" }, { schema: "dbo", name: "usp", kind: "proc" }] } } as never } });
  const side = { mode: "table", profileId: "p", database: "Sales", table: "dbo.Orders" };
  const tool: ToolTab = { id: "tool1", kind: "dataCompare", title: "Data compare", profileId: "p", database: "Sales", params: { source: side, target: { ...side, table: "dbo.Orders2" }, ...params } };
  useToolTabs.setState({ tools: { tool1: tool } });
  return render(<DataCompareTab tool={tool} />);
}
const start = async () => {
  m.rpc.mockResolvedValueOnce(START);
  fireEvent.click(screen.getByText("Compare"));
  await waitFor(() => expect(m.notify).toBeDefined());
  await screen.findByText("Starting…");
};

beforeEach(() => {
  vi.clearAllMocks();
  m.stream.rs = undefined; m.grid.props = undefined; m.notify = undefined;
  m.rpc.mockReset();
  m.rpc.mockResolvedValue(undefined);
  useExecutions.setState({ views: {} });
});
afterEach(cleanup);

describe("DataCompareTab", () => {
  it("shows guidance until a comparison starts, and Compare needs both sides ready", () => {
    setup({ source: { mode: "table" }, target: { mode: "table" } });
    expect(screen.getByText(/Pick a table or write a query on each side/)).toBeTruthy();
    expect((screen.getByText("Compare") as HTMLButtonElement).disabled).toBe(true);
  });

  it("starts a table-to-table compare with parsed key and options, retitles and rebinds the tab to the target", async () => {
    setup();
    fireEvent.change(screen.getByLabelText("Key columns"), { target: { value: "id, part" } });
    fireEvent.click(screen.getByLabelText("Ignore case"));
    fireEvent.click(screen.getByLabelText(/Ignore char padding/));
    fireEvent.change(screen.getByDisplayValue("1e-9"), { target: { value: "0.5" } });
    fireEvent.change(screen.getByDisplayValue("0"), { target: { value: "2.4" } });
    fireEvent.change(screen.getByDisplayValue("50000"), { target: { value: "10" } });
    await start();
    expect(m.rpc).toHaveBeenCalledWith("dataCompare.start", {
      source: { sessionId: "sess", database: "Sales", schema: "dbo", table: "Orders" },
      target: { sessionId: "sess", database: "Sales", schema: "dbo", table: "Orders2" },
      key: ["id", "part"], columns: null,
      options: { floatEpsilon: 0.5, timeToleranceMs: 2, ignoreCase: true, trimCharPadding: false, maxDiffRows: 10 },
    });
    expect(useToolTabs.getState().tools.tool1).toMatchObject({ title: "Data · dbo.Orders → dbo.Orders2", profileId: "p", database: "Sales" });
    expect(screen.getByText(/Key id · 2 columns · note one/)).toBeTruthy();
    expect(screen.getByText("Waiting for the first rows…")).toBeTruthy();
  });

  it("uses defaults for blank or junk numeric options, and compares a query with a table (PostgreSQL profile)", async () => {
    setup({ source: { mode: "query", profileId: "p", database: "Sales", query: " select 1 " } }, "pg");
    fireEvent.change(screen.getByDisplayValue("1e-9"), { target: { value: "abc" } });
    fireEvent.change(screen.getByDisplayValue("0"), { target: { value: "-5" } });
    fireEvent.change(screen.getByDisplayValue("50000"), { target: { value: "" } });
    await start();
    const p = m.rpc.mock.calls[0][1];
    expect(p.source).toEqual({ sessionId: "sess", database: "Sales", query: "select 1" });
    expect(p.options).toMatchObject({ floatEpsilon: 0, timeToleranceMs: 0, maxDiffRows: 50000 });
    expect(p.key).toBeNull();
    expect(useToolTabs.getState().tools.tool1.title).toBe("Data · query → dbo.Orders2");
  });

  it("shows live progress, then completion with timing; ignores events of other comparisons", async () => {
    setup();
    await start();
    act(() => m.notify!(progress({ compareId: "other", sourceRows: 9 })));
    expect(screen.getByText("Starting…")).toBeTruthy();
    act(() => m.notify!(progress()));
    expect(screen.getByText("Reading… 1,200 / 1,100 rows · 1,000 identical")).toBeTruthy();
    expect(screen.getByText("Different 5")).toBeTruthy();
    expect(screen.getByText("Source only 3")).toBeTruthy();
    expect(screen.getByText("Target only 2")).toBeTruthy();
    expect(screen.getByText("Cancel")).toBeTruthy();
    act(() => m.notify!(progress({ done: true })));
    expect(screen.getByText("1,200 / 1,100 rows · 1,000 identical · 322 ms")).toBeTruthy();
    // The key / columns note is read: muted, never faint.
    expect(screen.getByText(/^Key /).className).toContain("text-muted");
    expect(document.querySelector(".text-faint")).toBeNull();
    expect(dataCompareBench.lastMs).toBe(321.6);
    expect(screen.getByText("Compare again")).toBeTruthy();
  });

  it("its run is a view under the tool's id, ended by its last progress and dropped with the tab (W6)", async () => {
    const { unmount } = setup();
    await start();
    // What an engine restart marks interrupted, and the restart banner names by the tool's title (actions.ts engineLost).
    expect(useExecutions.getState().views.e1).toMatchObject({ id: "e1", tabId: "tool1", state: "running" });
    act(() => m.notify!(progress({ done: true })));
    expect(useExecutions.getState().views.e1.state).toBe("done");
    unmount();
    expect(useExecutions.getState().views.e1).toBeUndefined();
  });

  it("a compare the engine took with it says so instead of reading forever, and can run again (W6)", async () => {
    setup();
    await start();
    act(() => m.notify!(progress()));
    expect(screen.getByText("Reading… 1,200 / 1,100 rows · 1,000 identical")).toBeTruthy();
    // An engine restart marks every run still going failed (interruptRuns).
    act(() => useExecutions.getState().patch("e1", { state: "failed", error: { message: "The Querithm engine restarted while this ran." } as never }));
    expect(screen.getByRole("alert").textContent).toBe(COMPARE_INTERRUPTED);
    expect(screen.getByText("1,200 / 1,100 rows · 1,000 identical · interrupted")).toBeTruthy();
    expect(screen.queryByText(/Reading…/)).toBeNull();
    expect(screen.queryByText("Cancel")).toBeNull();
    expect(screen.getByText("Compare again")).toBeTruthy();
  });

  it("shows server-side failure, truncation and cancellation", async () => {
    setup();
    await start();
    act(() => m.notify!(progress({ done: true, error: "key not unique", truncated: true, cancelled: true })));
    expect(screen.getByRole("alert").textContent).toBe("key not unique");
    expect(screen.getByText(/showing the first 50,000 differences/)).toBeTruthy();
    expect(screen.getByText(/cancelled/)).toBeTruthy();
    expect((screen.getByText("Open sync script") as HTMLButtonElement).disabled).toBe(true);
  });

  it("names the limit that stopped keeping differences", async () => {
    setup();
    await start();
    const note = "Showing the first 1,234 differences: they fill the 64 MB one comparison may keep. Compare fewer or shorter columns, or fewer rows, to see the rest.";
    act(() => m.notify!(progress({ done: true, truncated: true, kept: 1234, truncatedNote: note })));
    const shown = screen.getByText(note);
    expect(shown.getAttribute("title")).toBe(note);
    expect(screen.queryByText(/showing the first 50,000 differences/)).toBeNull();
  });

  it("reports a start failure", async () => {
    setup();
    m.rpc.mockRejectedValueOnce(new Error("no primary key"));
    fireEvent.click(screen.getByText("Compare"));
    expect((await screen.findByRole("alert")).textContent).toBe("no primary key");
  });

  it("closing the tab while the compare runs cancels it (H3: its stream and two server reads stop), a finished one is left alone", async () => {
    const { unmount } = setup();
    await start();
    unmount();
    expect(m.rpc).toHaveBeenLastCalledWith("dataCompare.cancel", { compareId: "c1" });

    cleanup();
    m.rpc.mockClear();
    const again = setup();
    await start();
    act(() => m.notify!(progress({ done: true })));
    again.unmount();
    expect(m.rpc.mock.calls.map((c) => c[0])).not.toContain("dataCompare.cancel");
  });

  it("cancels a running compare, swallowing cancel errors", async () => {
    setup();
    await start();
    m.rpc.mockRejectedValueOnce(new Error("already done"));
    fireEvent.click(screen.getByText("Cancel"));
    expect(m.rpc).toHaveBeenLastCalledWith("dataCompare.cancel", { compareId: "c1" });
  });

  describe("results grid", () => {
    async function withGrid() {
      m.stream.rs = fakeRs();
      setup();
      await start();
      await screen.findByTestId("grid");
      return m.stream.rs;
    }

    it("passes the filter text and the diff-kind column to the grid for each filter", async () => {
      const rs = await withGrid();
      await waitFor(() => expect(rs.setFilter).toHaveBeenCalledWith("", null));
      fireEvent.click(screen.getByText("Different"));
      expect(rs.setFilter).toHaveBeenLastCalledWith("≠", [0]);
      fireEvent.click(screen.getByText("Source only"));
      expect(rs.setFilter).toHaveBeenLastCalledWith("+ source", [0]);
      fireEvent.click(screen.getByText("Target only"));
      expect(rs.setFilter).toHaveBeenLastCalledWith("− target", [0]);
      expect(m.grid.props.executionId).toBe("e1");
    });

    it("tones cells from the row's kind and changed-column mask", async () => {
      const rs = await withGrid();
      const tone = (row: number, col: number) => m.grid.props.tone(row, col);
      // different row: id (bit 0) changed, name (bit 1) not changed; diff column always changed
      rs.raw.mockImplementation((_r: number, f: number) => (f === 0 ? 1 : f === 1 ? new Uint8Array([0b01]) : null));
      expect(tone(0, 0)).toBe("changed");
      expect(tone(0, 1)).toBe("changed");
      expect(tone(0, 2)).toBeNull();
      // source-only row: source side green, target side dimmed
      rs.raw.mockImplementation((_r: number, f: number) => (f === 0 ? 2 : null));
      expect(tone(0, 2)).toBe("added");
      expect(tone(0, 3)).toBe("muted");
      // target-only row
      rs.raw.mockImplementation((_r: number, f: number) => (f === 0 ? 3 : null));
      expect(tone(0, 3)).toBe("removed");
      expect(tone(0, 2)).toBe("muted");
      // a missing kind reads as 0 and a missing mask as no change
      rs.raw.mockImplementation(() => null);
      expect(tone(0, 1)).toBeNull();
    });

    it("opens the sync script in a tab bound to the target once done, with the chosen operations", async () => {
      await withGrid();
      act(() => m.notify!(progress({ done: true })));
      m.rpc.mockResolvedValueOnce({ sql: "UPDATE ...", statements: 3, rows: 1234, notes: ["skipped identity"] });
      fireEvent.click(screen.getByLabelText(/Delete target-only rows/));
      fireEvent.click(screen.getByLabelText(/Insert source-only rows/));
      fireEvent.click(screen.getByText("Open sync script"));
      await waitFor(() => expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "p", database: "Sales", text: "UPDATE ...", title: "Sync.sql" }));
      expect(m.rpc).toHaveBeenLastCalledWith("dataCompare.syncSql", { compareId: "c1", insert: false, update: true, delete: true });
      expect(m.toast).toHaveBeenCalledWith("info", "3 statement(s) for 1,234 row(s) in a new tab on the target. Review it, then run it. skipped identity");
    });

    it("reports a sync script failure and disables it when no operation is chosen", async () => {
      await withGrid();
      act(() => m.notify!(progress({ done: true })));
      m.rpc.mockRejectedValueOnce(new Error("no key"));
      fireEvent.click(screen.getByText("Open sync script"));
      await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "no key"));
      fireEvent.click(screen.getByLabelText(/Insert source-only rows/));
      fireEvent.click(screen.getByLabelText(/Update different rows/));
      expect((screen.getByText("Open sync script") as HTMLButtonElement).disabled).toBe(true);
    });
  });

  it("cannot script a sync when the target is a query", async () => {
    setup({ target: { mode: "query", profileId: "p", database: "Sales", query: "select 1" } });
    await start();
    act(() => m.notify!(progress({ done: true })));
    const btn = screen.getByText("Open sync script") as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
    expect(btn.title).toContain("needs a table");
  });

  it("swaps source and target", () => {
    setup();
    const s = within(screen.getByRole("group", { name: "Source" }));
    expect((s.getByLabelText("Source table") as HTMLInputElement).value).toBe("dbo.Orders");
    fireEvent.click(screen.getByLabelText("Swap source and target"));
    expect((within(screen.getByRole("group", { name: "Source" })).getByLabelText("Source table") as HTMLInputElement).value).toBe("dbo.Orders2");
    expect(useToolTabs.getState().tools.tool1.params).toMatchObject({ target: { table: "dbo.Orders" } });
  });
});

describe("DataSidePicker", () => {
  it("lists tables and views of the cached snapshot (not procedures) in the datalist", () => {
    setup();
    const opts = Array.from(document.querySelectorAll("datalist#dc-tables-Source option")).map((o) => o.getAttribute("value"));
    expect(opts).toEqual(["dbo.Orders", "dbo.vw"]);
  });

  it("changing connection clears the rest, database clears the table, kind switches to a query editor", () => {
    setup();
    const src = () => within(screen.getByRole("group", { name: "Source" }));
    fireEvent.change(src().getByLabelText("Source database"), { target: { value: "" } });
    expect((src().getByLabelText("Source table") as HTMLInputElement).disabled).toBe(true);
    fireEvent.change(src().getByLabelText("Source kind"), { target: { value: "query" } });
    fireEvent.change(src().getByLabelText("Source query"), { target: { value: "select 2" } });
    expect((src().getByLabelText("Source query") as HTMLTextAreaElement).value).toBe("select 2");
    expect(src().queryByLabelText("Source table")).toBeNull();
    fireEvent.change(src().getByLabelText("Source connection"), { target: { value: "" } });
    expect((src().getByLabelText("Source database") as HTMLSelectElement).disabled).toBe(true);
    expect(src().queryByText("sys")).toBeNull();
  });

  it("types a table name and connects an unconnected profile", () => {
    setup({ source: { mode: "table", profileId: "z", database: "d" } });
    expect(useConnections.getState().connect).toHaveBeenCalledWith("z");
    cleanup();
    setup({ source: { mode: "table", profileId: "p", database: "Sales" } });
    const t = within(screen.getByRole("group", { name: "Source" })).getByLabelText("Source table") as HTMLInputElement;
    fireEvent.change(t, { target: { value: "dbo.vw" } });
    expect(t.value).toBe("dbo.vw");
    fireEvent.change(t, { target: { value: "" } });
    expect(t.value).toBe("");
  });
});

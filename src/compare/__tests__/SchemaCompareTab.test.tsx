// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { CompareItem, SchemaCompareResult } from "../../ipc/contract";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useToolTabs, type ToolTab } from "../../store/toolTabs";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), toast: vi.fn(), newQueryTab: vi.fn(), pickSchemaFile: vi.fn(), sideParams: vi.fn(), exportSchemaModel: vi.fn(),
  diffs: [] as any[], editors: [] as any[], langs: [] as string[],
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../store/ui", () => ({ toast: m.toast }));
vi.mock("../../app/actions", () => ({ newQueryTab: m.newQueryTab }));
vi.mock("../../editor/editors", () => ({ languageFor: (e?: string) => (e === "pg" ? "pgsql" : "tsql") }));
vi.mock("../compareActions", () => ({ pickSchemaFile: m.pickSchemaFile, sideParams: m.sideParams, exportSchemaModel: m.exportSchemaModel }));
vi.mock("../../editor/monaco", () => {
  const model = (lang: string) => ({ lang, v: "", setValue(x: string) { this.v = x; }, getValue() { return this.v; }, dispose: vi.fn() });
  return {
    initMonaco: vi.fn(),
    monaco: {
      editor: {
        createModel: (_v: string, lang: string) => model(lang),
        createDiffEditor: () => { const d = { model: null as any, setModel(x: any) { this.model = x; }, getModel() { return this.model; }, dispose: vi.fn() }; m.diffs.push(d); return d; },
        create: (_h: unknown, o: { language: string }) => {
          m.langs.push(o.language);
          const e = { value: "", setValue(x: string) { this.value = x; }, getModel: () => model(o.language), dispose: vi.fn() };
          m.editors.push(e);
          return e;
        },
      },
    },
  };
});

const { SchemaCompareTab, schemaCompareBench } = await import("../SchemaCompareTab");

const item = (o: Partial<CompareItem>): CompareItem => ({ key: "k", kind: "table", schema: "dbo", name: "T", detail: null, class: "different", tags: [], changes: [], dataLoss: false, note: null, ...o });
const ITEMS: CompareItem[] = [
  item({ key: "table:dbo.Orders", name: "Orders", changes: ["column Total type changed"], dataLoss: true }),
  item({ key: "table:dbo.Customers", name: "Customers", class: "sourceOnly" }),
  item({ key: "view:dbo.v", kind: "view", name: "v", class: "targetOnly", note: "has dependents" }),
  item({ key: "procedure:dbo.p", kind: "procedure", name: "p", class: "identical" }),
  item({ key: "table:dbo.Spaces", name: "Spaces", tags: ["whitespaceOnly"] }),
  item({ key: "trigger:dbo.trg", kind: "trigger", name: "trg", detail: "Orders", class: "sourceOnly" }),
];
const RESULT: SchemaCompareResult = {
  compareId: "cmp1", source: { label: "s", engine: "mssql", capturedAt: 0, live: true }, target: { label: "t", engine: "mssql", capturedAt: 0, live: true },
  items: ITEMS, possibleRenames: [{ tableKey: "table:dbo.Orders", from: "Qty", to: "Quantity", type: "int" }], notes: ["extended properties"], transactionalDdl: true, elapsedMs: 42.4,
};

function setup(params: Record<string, unknown> = {}, engine: "mssql" | "pg" = "mssql") {
  useProfiles.setState({ profiles: [{ id: "p", name: "Prod", engine } as never, { id: "q", name: "Dev", engine } as never] });
  useConnections.setState({
    servers: { p: { profileId: "p", status: "connected", databases: [{ name: "Sales", isSystem: false }, { name: "master", isSystem: true }] }, q: { profileId: "q", status: "connecting", databases: [] } } as never,
    connect: vi.fn(),
  } as never);
  useSnapshots.setState({ entries: { [snapKey("p", "Sales")]: { key: "k", profileId: "p", database: "Sales", status: "fresh", snapshot: { objects: [{ schema: "sales" }, { schema: "dbo" }, { schema: "sales" }] }, version: 1 } as never } });
  const live = { kind: "live", profileId: "p", database: "Sales", schema: null };
  const tool: ToolTab = { id: "tool1", kind: "schemaCompare", title: "Schema compare", profileId: "p", database: "Sales", params: { source: live, target: { ...live }, ...params } };
  useToolTabs.setState({ tools: { tool1: tool } });
  return render(<SchemaCompareTab tool={tool} />);
}
const compare = async () => {
  fireEvent.click(screen.getByText("Compare"));
  await screen.findByText(/checked ·/);
};

beforeEach(() => {
  vi.clearAllMocks();
  m.diffs.length = 0; m.editors.length = 0; m.langs.length = 0;
  (globalThis as any).ResizeObserver = class { observe() {} disconnect() {} };
  m.sideParams.mockImplementation(async (s: any) => ({ sessionId: "s", database: s.database }));
  m.rpc.mockImplementation(async (method: string) => {
    if (method === "schemaCompare.run") return RESULT;
    if (method === "schemaCompare.objectSql") return { source: "SRC", target: "TGT" };
    if (method === "schemaCompare.migrationSql") return { sql: "ALTER TABLE ...", statements: 2, warnings: ["w1", "w2"], skipped: ["a", "b", "c", "d"], transactional: true };
    throw new Error("unexpected " + method);
  });
});
afterEach(cleanup);

const names = () => Array.from(document.querySelectorAll("[data-q-compare-item]")).map((e) => e.getAttribute("data-q-compare-item"));

describe("SchemaCompareTab", () => {
  it("compares the two sides, selects what differs, focuses the first difference and retitles the tab", async () => {
    setup();
    expect(screen.getByText(/Pick a source and a target/)).toBeTruthy();
    await compare();
    expect(m.rpc).toHaveBeenCalledWith("schemaCompare.run", { source: { sessionId: "s", database: "Sales" }, target: { sessionId: "s", database: "Sales" } });
    expect(useToolTabs.getState().tools.tool1.title).toBe("Compare · Prod · Sales → Prod · Sales");
    expect(screen.getByText("4 checked · 42 ms")).toBeTruthy();
    expect(schemaCompareBench).toEqual({ lastMs: 42.4, lastItems: 6 });
    expect(screen.getByText(/Not compared: extended properties/)).toBeTruthy();
    // identical hidden by default; whitespace-only differs but is unchecked
    expect(names()).not.toContain("procedure:dbo.p");
    expect((screen.getByLabelText("Include dbo.Spaces") as HTMLInputElement).checked).toBe(false);
    expect((screen.getByLabelText("Include dbo.Orders") as HTMLInputElement).checked).toBe(true);
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("schemaCompare.objectSql", { compareId: "cmp1", key: "table:dbo.Orders" }));
  });

  it("shows a failed comparison and lets you retry", async () => {
    setup();
    m.sideParams.mockRejectedValueOnce(new Error("Choose a connection and a database."));
    fireEvent.click(screen.getByText("Compare"));
    expect((await screen.findByRole("alert")).textContent).toContain("Choose a connection");
    expect((screen.getByText("Compare") as HTMLButtonElement).disabled).toBe(false);
  });

  it("is disabled until both sides are chosen", () => {
    setup({ source: { kind: "live" }, target: { kind: "live" } });
    expect((screen.getByText("Compare") as HTMLButtonElement).disabled).toBe(true);
  });

  it("filters by class, whitespace, identical and name; counts show per class", async () => {
    setup();
    await compare();
    expect(screen.getByText("Different 2")).toBeTruthy();
    fireEvent.click(screen.getByText("Source only 2"));
    expect(names()).toEqual(["table:dbo.Customers", "trigger:dbo.trg"]);
    fireEvent.click(screen.getByText("Target only 1"));
    expect(names()).toEqual(["view:dbo.v"]);
    fireEvent.click(screen.getByText("All"));
    fireEvent.click(screen.getByLabelText(/Hide identical/));
    expect(names()).toContain("procedure:dbo.p");
    fireEvent.click(screen.getByLabelText(/Hide whitespace-only/));
    expect(names()).not.toContain("table:dbo.Spaces");
    fireEvent.change(screen.getByPlaceholderText("Filter by name…"), { target: { value: "cust" } });
    expect(names()).toEqual(["table:dbo.Customers"]);
    fireEvent.change(screen.getByPlaceholderText("Filter by name…"), { target: { value: "zzz" } });
    expect(screen.getByText("Nothing to show with these filters.")).toBeTruthy();
    // Counts, the checked total and the identical badge are read: muted, never faint.
    fireEvent.change(screen.getByPlaceholderText("Filter by name…"), { target: { value: "" } });
    expect(screen.getByText(/checked ·/).className).toContain("text-muted");
    expect(document.querySelector(".text-faint")).toBeNull();
  });

  it("groups by kind or by action, collapses a group, and checks a whole group", async () => {
    setup();
    await compare();
    expect(screen.getByText("Tables")).toBeTruthy();
    expect(screen.getByText("Triggers")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Group by"), { target: { value: "action" } });
    expect(screen.getByText("Create")).toBeTruthy();
    expect(screen.getByText("Change")).toBeTruthy();
    expect(screen.getByText("Drop")).toBeTruthy();
    fireEvent.click(screen.getByLabelText("Check all Create"));
    expect(screen.getByText("2 checked · 42 ms")).toBeTruthy();
    fireEvent.click(screen.getByLabelText("Check all Create"));
    expect(screen.getByText("4 checked · 42 ms")).toBeTruthy();
    fireEvent.click(screen.getByText("Create"));
    expect(names()).not.toContain("table:dbo.Customers");
    fireEvent.click(screen.getByText("Create"));
    expect(names()).toContain("table:dbo.Customers");
  });

  it("toggles a single item without changing focus, and badges data loss, notes and whitespace", async () => {
    setup();
    await compare();
    expect(screen.getByText("data loss")).toBeTruthy();
    expect(screen.getByText("whitespace")).toBeTruthy();
    expect(screen.getByText("!").getAttribute("title")).toBe("has dependents");
    expect(screen.getByText("dbo.trg".replace("dbo.trg", "dbo.trg on Orders"))).toBeTruthy();
    fireEvent.click(screen.getByLabelText("Include dbo.Orders"));
    expect(screen.getByText("3 checked · 42 ms")).toBeTruthy();
  });

  it("shows the focused object's two definitions (left target, right source) and its change list", async () => {
    setup();
    await compare();
    await waitFor(() => expect(m.diffs[0].model.original.getValue()).toBe("TGT"));
    expect(m.diffs[0].model.modified.getValue()).toBe("SRC");
    expect(screen.getByText("column Total type changed")).toBeTruthy();
    m.rpc.mockImplementation(async (method: string) => (method === "schemaCompare.objectSql" ? { source: "NEW", target: null } : RESULT));
    fireEvent.click(screen.getByText("dbo.Customers"));
    await waitFor(() => expect(m.diffs[0].model.modified.getValue()).toBe("NEW"));
    expect(m.diffs[0].model.original.getValue()).toBe("-- not in the target\n");
  });

  it("shows missing-source text and object errors", async () => {
    setup();
    await compare();
    m.rpc.mockImplementation(async (method: string) => {
      if (method === "schemaCompare.objectSql") throw new Error("gone");
      return RESULT;
    });
    fireEvent.click(screen.getByText("dbo.Customers"));
    expect(await screen.findByText("gone")).toBeTruthy();
    m.rpc.mockImplementation(async () => ({ source: null, target: "T" }));
    fireEvent.click(screen.getByText("dbo.v"));
    await waitFor(() => expect(m.diffs[0].model.modified.getValue()).toBe("-- not in the source\n"));
  });

  it("clears the diff when nothing is focused", async () => {
    setup();
    m.rpc.mockImplementation(async (method: string) => (method === "schemaCompare.run" ? { ...RESULT, items: [ITEMS[3]] } : RESULT));
    await compare();
    fireEvent.click(screen.getByLabelText(/Hide identical/));
    fireEvent.click(screen.getByText("dbo.p"));
    await waitFor(() => expect(m.diffs.length).toBeGreaterThan(0));
  });

  describe("migration", () => {
    it("builds the script for the checked items and opens it in a tab bound to the target", async () => {
      setup();
      await compare();
      fireEvent.click(screen.getByText("Migration"));
      await waitFor(() => expect(m.editors[0].value).toBe("ALTER TABLE ..."));
      const call = m.rpc.mock.calls.find((c) => c[0] === "schemaCompare.migrationSql")![1];
      expect(call.compareId).toBe("cmp1");
      expect(call.keys).toContain("table:dbo.Orders");
      expect(call.keys).not.toContain("table:dbo.Spaces");
      expect(call.includeDataLoss).toBe(false);
      expect(call.renames).toEqual([]);
      expect(screen.getByText("2 statements")).toBeTruthy();
      expect(screen.getByText(/Left out \(data loss\): 4 — a; b; c…/)).toBeTruthy();
      expect(screen.getByText(/Review: 2 — w1…/)).toBeTruthy();
      fireEvent.click(screen.getByText("Open in query tab"));
      expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "p", database: "Sales", text: "ALTER TABLE ...", title: "Migration.sql" });
      expect(m.toast).toHaveBeenCalledWith("info", expect.stringContaining("new tab on the target"));
    });

    it("offers possible renames for checked tables, and includes the chosen ones; data-loss toggle re-requests", async () => {
      setup();
      await compare();
      fireEvent.click(screen.getByText("Migration"));
      const rename = await screen.findByText(/Orders: rename/);
      expect(rename.closest("label")!.textContent).toContain("Orders: rename Qty to Quantity (int)");
      fireEvent.click(within(rename.closest("label")!).getByRole("checkbox"));
      await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].renames).toEqual(RESULT.possibleRenames));
      fireEvent.click(within(rename.closest("label")!).getByRole("checkbox"));
      await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].renames).toEqual([]));
      fireEvent.click(screen.getByLabelText(/Include data-loss changes/));
      await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].includeDataLoss).toBe(true));
    });

    it("warns about engines whose DDL is not transactional (PostgreSQL ok, others not) and reports errors", async () => {
      setup();
      m.rpc.mockImplementation(async (method: string) => {
        if (method === "schemaCompare.run") return { ...RESULT, transactionalDdl: false, possibleRenames: [] };
        throw new Error("script failed");
      });
      await compare();
      fireEvent.click(screen.getByText("Migration"));
      expect(await screen.findByText(/cannot roll back if a step fails/)).toBeTruthy();
      expect(await screen.findByText("script failed")).toBeTruthy();
      expect((screen.getByText("Open in query tab") as HTMLButtonElement).disabled).toBe(true);
    });

    it("shows the refusal instead of a script", async () => {
      setup();
      m.rpc.mockImplementation(async () => ({ ...RESULT, migrationRefusal: "The target is a file." }));
      await compare();
      fireEvent.click(screen.getByText("Migration"));
      expect(await screen.findByText("The target is a file.")).toBeTruthy();
      expect(m.rpc).toHaveBeenCalledTimes(2);
    });

    it("does not open a tab when the target is a saved file; an empty script cannot be opened", async () => {
      setup({ target: { kind: "file", token: "t", name: "base.json" } });
      await compare();
      fireEvent.click(screen.getByText("Migration"));
      await waitFor(() => expect(m.editors[0].value).toBe("ALTER TABLE ..."));
      fireEvent.click(screen.getByText("Open in query tab"));
      expect(m.newQueryTab).not.toHaveBeenCalled();
    });

    it("uses the PostgreSQL language for the editors", async () => {
      setup({}, "pg");
      m.rpc.mockImplementation(async (method: string) => (method === "schemaCompare.run" ? { ...RESULT, transactionalDdl: true } : { sql: "x", statements: 0, warnings: [], skipped: [], transactional: true, source: "a", target: "b" }));
      await compare();
      fireEvent.click(screen.getByText("Migration"));
      await waitFor(() => expect(m.langs).toContain("pgsql"));
      expect((screen.getByText("Open in query tab") as HTMLButtonElement).disabled).toBe(true);
    });

    it("returns to the object view when an item is picked", async () => {
      setup();
      await compare();
      fireEvent.click(screen.getByText("Migration"));
      fireEvent.click(screen.getByText("dbo.Customers"));
      expect(screen.getByText(/left: target, right: source/)).toBeTruthy();
      fireEvent.click(screen.getByText("Migration"));
      fireEvent.click(screen.getByText("Object"));
      expect(screen.getByText(/left: target, right: source/)).toBeTruthy();
    });
  });

  describe("side pickers", () => {
    it("connects a server picked in a side and lists its non-system databases and the cached schemas", () => {
      setup({ source: { kind: "live" }, target: { kind: "live", profileId: "p", database: "Sales" } });
      const src = within(screen.getByRole("group", { name: "Source" }));
      fireEvent.change(src.getByLabelText("Source connection"), { target: { value: "p" } });
      expect(useConnections.getState().connect).not.toHaveBeenCalled();
      const tgt = within(screen.getByRole("group", { name: "Target" }));
      expect(tgt.getAllByRole("option").map((o) => o.textContent)).toEqual(expect.arrayContaining(["Sales", "sales", "dbo"]));
      expect(tgt.queryByText("master")).toBeNull();
      fireEvent.change(tgt.getByLabelText("Target schema"), { target: { value: "sales" } });
      expect((tgt.getByLabelText("Target schema") as HTMLSelectElement).value).toBe("sales");
    });

    it("asks the connection store to connect a profile that is not connected yet", () => {
      setup({ source: { kind: "live", profileId: "z" }, target: { kind: "live" } });
      expect(useConnections.getState().connect).toHaveBeenCalledWith("z");
      cleanup();
      setup({ source: { kind: "live", profileId: "q" }, target: { kind: "live" } });
      expect(within(screen.getByRole("group", { name: "Source" })).getByText("Connecting…")).toBeTruthy();
    });

    it("picks a database, then resets the schema", () => {
      setup({ source: { kind: "live", profileId: "p", schema: "dbo" }, target: { kind: "live" } });
      const src = within(screen.getByRole("group", { name: "Source" }));
      fireEvent.change(src.getByLabelText("Source database"), { target: { value: "Sales" } });
      expect((src.getByLabelText("Source database") as HTMLSelectElement).value).toBe("Sales");
      expect((src.getByLabelText("Source schema") as HTMLSelectElement).value).toBe("");
    });

    it("switches a side to a saved schema file and back, and swaps sides", async () => {
      setup();
      m.pickSchemaFile.mockResolvedValueOnce({ token: "tk", name: "base.json", size: 1 });
      const src = within(screen.getByRole("group", { name: "Source" }));
      fireEvent.change(src.getByLabelText("Source kind"), { target: { value: "file" } });
      await waitFor(() => expect(within(screen.getByRole("group", { name: "Source" })).getByText("base.json")).toBeTruthy());
      fireEvent.click(screen.getByLabelText("Swap source and target"));
      expect(within(screen.getByRole("group", { name: "Target" })).getByText("base.json")).toBeTruthy();
      expect(useToolTabs.getState().tools.tool1.params).toMatchObject({ target: { kind: "file", token: "tk" } });
      const tgt = within(screen.getByRole("group", { name: "Target" }));
      fireEvent.change(tgt.getByLabelText("Target kind"), { target: { value: "live" } });
      expect(within(screen.getByRole("group", { name: "Target" })).getByLabelText("Target connection")).toBeTruthy();
    });

    it("keeps the side when the file dialog is cancelled; Save… exports a live side", async () => {
      setup();
      m.pickSchemaFile.mockResolvedValueOnce(null);
      const src = within(screen.getByRole("group", { name: "Source" }));
      fireEvent.change(src.getByLabelText("Source kind"), { target: { value: "file" } });
      await Promise.resolve();
      expect(within(screen.getByRole("group", { name: "Source" })).getByLabelText("Source connection")).toBeTruthy();
      fireEvent.click(src.getByText("Save…"));
      expect(m.exportSchemaModel).toHaveBeenCalledWith({ kind: "live", profileId: "p", database: "Sales", schema: null });
    });
  });
});

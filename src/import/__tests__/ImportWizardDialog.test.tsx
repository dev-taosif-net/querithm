// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createElement } from "react";
import { useProfiles } from "../../store/profiles";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useUi } from "../../store/ui";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), toast: vi.fn(), ask: vi.fn(), notify: [] as Array<(e: any) => void>,
  pick: vi.fn(), openSession: vi.fn(), closeSession: vi.fn(), exec: vi.fn(), stream: { v: undefined as any }, refresh: vi.fn(),
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, shell: {},
  RpcError: class RpcError extends Error { appCode?: string; },
  onNotify: (_n: string, h: (e: any) => void) => { m.notify.push(h); return () => { m.notify = m.notify.filter((x) => x !== h); }; },
}));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast: m.toast, ask: m.ask }));
vi.mock("../importActions", () => ({ pickImportFile: m.pick, openImportSession: m.openSession, closeImportSession: m.closeSession, executeGuarded: m.exec }));
vi.mock("../../results/useExecutionStream", () => ({ useExecutionStream: () => m.stream.v }));
vi.mock("../../results/ResultGrid", () => ({ ResultGrid: () => createElement("div", { "data-testid": "preview-grid" }) }));

const { RpcError } = await import("../../ipc/client");
const { ImportWizardDialog } = await import("../ImportWizardDialog");
const { resetUncertainImports } = await import("../uncertainImports");

const col = (name: string, o: Record<string, unknown> = {}) => ({ name, ordinal: 0, kind: "text", precision: 0, scale: 0, maxLength: 12, nullCount: 0, firstNullRows: [], distinctEstimate: 10, min: null, max: null, candidateKey: false, ...o });
const INS = (o: Record<string, unknown> = {}) => ({
  inspectionId: "i1", fileName: "2024 sales.v2.csv", fileBytes: 100, format: "csv", delimiter: ",", hasHeader: true, encoding: "utf-8", sheets: [], sheet: null, rowCount: 1234,
  columns: [col("id", { kind: "int32", candidateKey: true, min: "1", max: "1234", distinctEstimate: 1234 }), col("amount", { kind: "decimal", precision: 10, scale: 2, nullCount: 3 }), col("note")],
  previewExecutionId: "prev1", notes: ["2 rows had extra fields"], ...o,
});
const TARGET = (o: Record<string, unknown> = {}) => ({
  tableExists: false, mapping: [{ fileColumn: "id", targetColumn: "id" }, { fileColumn: "amount", targetColumn: "amount" }, { fileColumn: "note", targetColumn: null }],
  verdicts: [], createTableSql: "CREATE TABLE dbo.t (id int)", proposedTypes: { id: "int", amount: "decimal(10,2)" }, ...o,
});
const FILE = { token: "tok", name: "2024 sales.v2.csv", size: 2048 };

function open(d: Record<string, unknown> = {}, engine: "mssql" | "pg" = "mssql") {
  useProfiles.setState({ profiles: [{ id: "p", name: "Prod", engine, environment: "prod" } as never] });
  m.pick.mockResolvedValue(FILE);
  const dialog = { kind: "import", profileId: "p", database: "Sales", ...d } as never;
  useUi.getState().openDialog(dialog);
  return { dialog, ...render(<ImportWizardDialog d={dialog} />) };
}
const emit = (e: Record<string, unknown>) => act(() => m.notify.forEach((h) => h(e)));
const prog = (o: Record<string, unknown> = {}) => ({ jobId: "job1", phase: "loading", rowsProcessed: 10, rowsLoaded: 1000, bytesProcessed: 50, totalBytes: 100, rejected: 0, elapsedMs: 2500, done: false, cancelled: false, ...o });

async function toTarget(ins = INS(), target = TARGET(), d: Record<string, unknown> = {}, engine: "mssql" | "pg" = "mssql") {
  const r = open(d, engine);
  m.rpc.mockImplementation(async (method: string) => (method === "import.inspect" ? ins : method === "import.checkTarget" ? target : undefined));
  m.openSession.mockResolvedValue("sid");
  fireEvent.click(screen.getByText("Choose file…"));
  await screen.findByText(/1,234 rows/);
  fireEvent.click(screen.getByText("Next: target →"));
  await waitFor(() => expect(screen.queryByText("Next: target →")).toBeNull());
  return r;
}

beforeEach(() => {
  vi.clearAllMocks();
  m.notify = [];
  m.stream.v = undefined;
  m.rpc.mockReset();
  m.pick.mockReset();
  m.ask.mockReset();
  m.exec.mockResolvedValue(null);
  m.closeSession.mockResolvedValue(undefined);
  useSnapshots.setState({ entries: {} });
  useUi.setState({ dialogs: [] });
  resetUncertainImports();
});
afterEach(cleanup);

describe("file step", () => {
  it("starts without a file and cannot proceed", () => {
    open();
    expect(screen.getByText("Import data")).toBeTruthy();
    // Where the rows go, environment included (never the server's name alone).
    expect(screen.getByTestId("server-chip").title).toBe("PROD Prod › Sales");
    expect(screen.getByTestId("server-chip").querySelector("[data-env]")?.textContent).toBe("PROD");
    expect((screen.queryByText("Next: target →") as HTMLButtonElement).disabled).toBe(true);
  });

  it("profiles the chosen file: columns, notes, key candidate and a table name derived from the file name", async () => {
    open();
    m.rpc.mockResolvedValue(INS());
    fireEvent.click(screen.getByText("Choose file…"));
    await screen.findByText(/1,234 rows/);
    expect(m.rpc).toHaveBeenCalledWith("import.inspect", { fileToken: "tok", filePath: null, options: expect.objectContaining({ format: "auto" }) });
    expect(screen.getByText("2024 sales.v2.csv · 2 KB", { exact: false })).toBeTruthy();
    expect(screen.getByText("2 rows had extra fields")).toBeTruthy();
    expect(screen.getByText("whole number 1…1234")).toBeTruthy();
    expect(screen.getByText("decimal (10,2)")).toBeTruthy();
    expect(screen.getByText("1,234 · unique")).toBeTruthy();
    expect(screen.getByText("Choose another file…")).toBeTruthy();
    expect((screen.getByDisplayValue("t_2024_sales_v2") as HTMLInputElement).value).toBe("t_2024_sales_v2");
    const keyBoxes = screen.getAllByRole("checkbox") as HTMLInputElement[];
    expect(keyBoxes.map((b) => b.checked)).toEqual([true, false, false]);
    fireEvent.click(keyBoxes[1]);
    fireEvent.click(keyBoxes[0]);
    expect(keyBoxes.map((b) => b.checked)).toEqual([false, true, false]);
  });

  it("does nothing when the picker is cancelled, and reports pick or inspect errors", async () => {
    open();
    m.pick.mockResolvedValueOnce(null);
    fireEvent.click(screen.getByText("Choose file…"));
    await waitFor(() => expect(m.pick).toHaveBeenCalled());
    expect(m.rpc).not.toHaveBeenCalled();
    m.pick.mockResolvedValueOnce(FILE);
    m.rpc.mockRejectedValueOnce(new Error("cannot read"));
    fireEvent.click(screen.getByText("Choose file…"));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "cannot read"));
    m.pick.mockRejectedValueOnce(new Error("dialog failed"));
    fireEvent.click(screen.getByText(/Choose (another )?file…/));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "dialog failed"));
  });

  it("re-inspects when the format, delimiter, header, or encoding is changed", async () => {
    open();
    m.rpc.mockResolvedValue(INS());
    fireEvent.click(screen.getByText("Choose file…"));
    await screen.findByText(/1,234 rows/);
    const select = (label: string) => screen.getByText(label).closest("label")!.querySelector("select")!;
    fireEvent.change(select("Delimiter"), { target: { value: ";" } });
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].options).toMatchObject({ delimiter: ";" }));
    fireEvent.change(select("First row"), { target: { value: "false" } });
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].options).toMatchObject({ hasHeader: false }));
    fireEvent.change(select("Encoding"), { target: { value: "latin1" } });
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].options).toMatchObject({ encoding: "latin1" }));
    fireEvent.change(select("Format"), { target: { value: "tsv" } });
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].options).toMatchObject({ format: "tsv" }));
  });

  it("offers a sheet picker for Excel files, and no header/delimiter/encoding options for JSON", async () => {
    open();
    m.rpc.mockResolvedValue(INS({ format: "xlsx", delimiter: null, sheets: ["Q1", "Q2"], sheet: "Q1" }));
    fireEvent.click(screen.getByText("Choose file…"));
    await screen.findByText(/1,234 rows/);
    expect(screen.queryByText("Delimiter")).toBeNull();
    expect(screen.queryByText("Encoding")).toBeNull();
    fireEvent.change(screen.getByText("Sheet").closest("label")!.querySelector("select")!, { target: { value: "Q2" } });
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].options).toMatchObject({ sheet: "Q2" }));
    cleanup();
    open();
    m.rpc.mockResolvedValue(INS({ format: "ndjson", delimiter: null }));
    fireEvent.click(screen.getByText("Choose file…"));
    await screen.findByText(/1,234 rows/);
    expect(screen.queryByText("First row")).toBeNull();
    expect(screen.queryByText("Delimiter")).toBeNull();
    expect(screen.getByText("Encoding")).toBeTruthy();
  });

  it("shows profiling progress and the preview grid", async () => {
    m.stream.v = { resultSets: [{}] };
    open();
    m.rpc.mockImplementation(() => new Promise(() => {}));
    fireEvent.click(screen.getByText("Choose file…"));
    await waitFor(() => expect((screen.getByText(/Choose (another )?file…/) as HTMLButtonElement).disabled).toBe(true));
    emit({ jobId: "tok", phase: "profiling", rowsProcessed: 5000, bytesProcessed: 25, totalBytes: 100, rowsLoaded: 0, rejected: 0, elapsedMs: 1, done: false, cancelled: false });
    expect(await screen.findByText("Profiling every row… 5,000 rows (25 %)")).toBeTruthy();
  });

  it("keeps profiling progress that arrives before the dialog re-renders", async () => {
    open();
    m.rpc.mockImplementation(() => {
      // Sent while import.inspect is in flight, before React has committed the picked file.
      m.notify.forEach((h) => h({ jobId: "tok", phase: "profiling", rowsProcessed: 7000, bytesProcessed: 50, totalBytes: 100, rowsLoaded: 0, rejected: 0, elapsedMs: 1, done: false, cancelled: false }));
      return new Promise(() => {});
    });
    fireEvent.click(screen.getByText("Choose file…"));
    expect(await screen.findByText("Profiling every row… 7,000 rows (50 %)")).toBeTruthy();
  });

  it("shows the preview grid when the file is inspected", async () => {
    m.stream.v = { resultSets: [{}] };
    open();
    m.rpc.mockResolvedValue(INS());
    fireEvent.click(screen.getByText("Choose file…"));
    expect(await screen.findByTestId("preview-grid")).toBeTruthy();
  });

  it("existing-table mode: no key column, table names from the snapshot split into schema and name", async () => {
    useSnapshots.setState({ entries: { [snapKey("p", "Sales")]: { key: "k", profileId: "p", database: "Sales", status: "fresh", version: 1, snapshot: { objects: [{ schema: "sales", name: "Orders", kind: "table", columns: [{ name: "id" }] }, { schema: "dbo", name: "v", kind: "view" }] } } as never } });
    open({ schema: "dbo" });
    m.rpc.mockResolvedValue(INS());
    fireEvent.click(screen.getByText("Choose file…"));
    await screen.findByText(/1,234 rows/);
    const loadInto = screen.getByText("Load into").closest("label")!.querySelector("select")!;
    fireEvent.change(loadInto, { target: { value: "existing" } });
    expect(screen.queryByText("Key")).toBeNull();
    const table = screen.getByText("Table").closest("label")!.querySelector("input") as HTMLInputElement;
    expect(Array.from(document.querySelectorAll("#import-tables option")).map((o) => o.getAttribute("value"))).toEqual(["sales.Orders"]);
    fireEvent.change(table, { target: { value: "sales.Orders" } });
    expect(table.value).toBe("Orders");
    expect((screen.getByText("Schema").closest("label")!.querySelector("input") as HTMLInputElement).value).toBe("sales");
    fireEvent.change(table, { target: { value: "plain" } });
    expect(table.value).toBe("plain");
  });

  it("defaults the schema per engine", () => {
    open({}, "pg");
    cleanup();
    open({ table: "t" }, "pg");
    expect(screen.queryByText("Choose file…")).not.toBeNull();
  });
});

describe("target step", () => {
  it("creates the mapping for a new table with DDL and proposed types; regenerate sends edited types and key", async () => {
    await toTarget();
    expect(m.openSession).toHaveBeenCalledWith("p", "Sales");
    expect(m.rpc).toHaveBeenCalledWith("import.checkTarget", expect.objectContaining({ sessionId: "sid", inspectionId: "i1", schema: "dbo", table: "t_2024_sales_v2", newTable: true, primaryKey: ["id"] }));
    expect((screen.getByDisplayValue("CREATE TABLE dbo.t (id int)") as HTMLTextAreaElement)).toBeTruthy();
    fireEvent.change(screen.getByDisplayValue("decimal(10,2)"), { target: { value: "money" } });
    fireEvent.change(screen.getAllByPlaceholderText("(skip)")[2], { target: { value: "comment" } });
    fireEvent.change(screen.getAllByPlaceholderText("(skip)")[2], { target: { value: "" } });
    fireEvent.click(screen.getByText("Regenerate CREATE TABLE"));
    await waitFor(() => expect(m.rpc.mock.calls.filter((c) => c[0] === "import.checkTarget")).toHaveLength(2));
    const second = m.rpc.mock.calls.filter((c) => c[0] === "import.checkTarget")[1][1];
    expect(second.typeOverrides).toEqual({ amount: "money" });
    fireEvent.change(screen.getByDisplayValue("CREATE TABLE dbo.t (id int)"), { target: { value: "CREATE TABLE edited" } });
    expect(screen.getByDisplayValue("CREATE TABLE edited")).toBeTruthy();
  });

  it("goes back to the file step", async () => {
    await toTarget();
    fireEvent.click(screen.getByText("← File"));
    expect(screen.getByText("Choose another file…")).toBeTruthy();
  });

  it("an empty table name blocks Next; check failures are reported", async () => {
    open();
    m.rpc.mockResolvedValue(INS());
    fireEvent.click(screen.getByText("Choose file…"));
    await screen.findByText(/1,234 rows/);
    const table = screen.getByDisplayValue("t_2024_sales_v2");
    fireEvent.change(table, { target: { value: "  " } });
    expect((screen.getByText("Next: target →") as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(table, { target: { value: "x" } });
    m.openSession.mockResolvedValue("sid");
    m.rpc.mockRejectedValueOnce(new Error("no such schema"));
    fireEvent.click(screen.getByText("Next: target →"));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "no such schema"));
    expect(screen.getByText("Next: target →")).toBeTruthy();
  });

  it("shows verdicts; red ones block the import until the suggested fix is ticked", async () => {
    const target = TARGET({
      tableExists: true,
      verdicts: [
        { fileColumn: "amount", targetColumn: "amt", kind: "widen", message: "needs wider", suggestedSql: "ALTER TABLE t ALTER COLUMN amt decimal(18,2)" },
        { fileColumn: "id", targetColumn: "id", kind: "nullIntoNotNull", message: "3 NULLs", suggestedSql: "ALTER TABLE t ALTER COLUMN id int NULL" },
        { fileColumn: "", targetColumn: "req", kind: "requiredUnmapped", message: "req has no source" },
        { fileColumn: "note", targetColumn: null, kind: "missingInTarget", message: "no column" },
      ],
    });
    await toTarget(INS(), target, { table: "t" });
    expect(screen.getByText("widen")).toBeTruthy();
    expect(screen.getByText("req has no source")).toBeTruthy();
    const importBtn = screen.getByText(/^Import 1,234 rows/) as HTMLButtonElement;
    expect(importBtn.disabled).toBe(true);
    expect(importBtn.title).toBe("Resolve the red verdicts first");
    const fixBoxes = screen.getAllByText("run").map((e) => e.closest("label")!.querySelector("input") as HTMLInputElement);
    expect(fixBoxes.map((b) => b.checked)).toEqual([true, false]);
    fireEvent.click(fixBoxes[1]);
    expect(importBtn.disabled).toBe(true); // requiredUnmapped still blocks
    fireEvent.click(fixBoxes[1]);
    fireEvent.click(fixBoxes[0]);
    fireEvent.click(fixBoxes[0]);
    expect(fixBoxes[0].checked).toBe(true);
  });

  it("existing table: remaps a file column to a table column and re-checks", async () => {
    useSnapshots.setState({ entries: { [snapKey("p", "Sales")]: { key: "k", profileId: "p", database: "Sales", status: "fresh", version: 1, snapshot: { objects: [{ schema: "dbo", name: "t", kind: "table", columns: [{ name: "id" }, { name: "Amt" }] }] } } as never } });
    await toTarget(INS(), TARGET({ tableExists: true }), { table: "t", schema: "dbo" });
    const selects = screen.getAllByRole("combobox") as HTMLSelectElement[];
    const noteSelect = selects.find((s) => Array.from(s.options).some((o) => o.value === "Amt"))!;
    fireEvent.change(noteSelect, { target: { value: "Amt" } });
    await waitFor(() => expect(m.rpc.mock.calls.filter((c) => c[0] === "import.checkTarget")).toHaveLength(2));
    expect(m.rpc.mock.calls.filter((c) => c[0] === "import.checkTarget")[1][1]).toMatchObject({ newTable: false, mapping: expect.arrayContaining([{ fileColumn: "id", targetColumn: "Amt" }]) });
  });

  it("shows engine-specific load options: SQL Server has bulk-copy switches, PostgreSQL does not", async () => {
    await toTarget();
    expect(screen.getByText(/Fire triggers/)).toBeTruthy();
    expect(screen.getByText(/Table lock/)).toBeTruthy();
    cleanup();
    await toTarget(INS(), TARGET(), {}, "pg");
    expect(screen.queryByText(/Fire triggers/)).toBeNull();
    expect(screen.queryByText(/Check constraints/)).toBeNull();
    expect(screen.getByText(/Keep identity values/)).toBeTruthy();
  });

  it("edits transaction mode, commit size, bad-row tolerance and flags", async () => {
    await toTarget();
    const tx = screen.getByText("Transaction").closest("label")!.querySelector("select")!;
    fireEvent.change(tx, { target: { value: "batches" } });
    fireEvent.change(screen.getByDisplayValue("10000"), { target: { value: "500" } });
    fireEvent.change(screen.getByText("Bad rows allowed").closest("label")!.querySelector("input")!, { target: { value: "-4" } });
    for (const t of [/Keep identity/, /Fire triggers/, /Check constraints/, /Table lock/]) fireEvent.click(screen.getByText(t).closest("label")!.querySelector("input")!);
    m.rpc.mockImplementation(async (method: string) => (method === "import.run" ? { jobId: "job1" } : undefined));
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await screen.findByText("Cancel import");
    expect(m.rpc.mock.calls.find((c) => c[0] === "import.run")![1].options).toEqual({
      transactionMode: "batches", commitEvery: 500, maxRejects: 0, keepIdentity: true, fireTriggers: true, checkConstraints: false, tableLock: true,
    });
  });
});

describe("load step", () => {
  async function started(target = TARGET(), d: Record<string, unknown> = {}) {
    await toTarget(INS(), target, d);
    m.rpc.mockImplementation(async (method: string) => (method === "import.run" ? { jobId: "job1" } : undefined));
  }

  it("runs the DDL through the guarded path, then the import; shows progress and a success summary; refreshes the snapshot", async () => {
    useSnapshots.setState({ ...useSnapshots.getState(), refresh: m.refresh } as never);
    await started(TARGET({ verdicts: [{ fileColumn: "amount", targetColumn: "amount", kind: "widen", message: "w", suggestedSql: "ALTER x" }] }));
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await screen.findByText("Cancel import");
    expect(m.exec.mock.calls.map((c) => c[1])).toEqual(["CREATE TABLE dbo.t (id int)", "ALTER x"]);
    expect(m.rpc).toHaveBeenCalledWith("import.run", expect.objectContaining({ sessionId: "sid", inspectionId: "i1", fileToken: "tok", schema: "dbo", table: "t_2024_sales_v2", confirmedProd: false }));
    expect(screen.getByText("Starting…")).toBeTruthy();
    emit(prog({ jobId: "other" }));
    emit(prog());
    expect(screen.getByText("1,000 rows loaded · 0 rejected · 2.5 s")).toBeTruthy();
    emit(prog({ done: true, rowsLoaded: 1234 }));
    expect(screen.getByText("Imported 1,234 rows into dbo.t_2024_sales_v2.")).toBeTruthy();
    expect(m.refresh).toHaveBeenCalledWith("p", "Sales");
    fireEvent.click(screen.getByText("Close"));
    expect(useUi.getState().dialogs).toHaveLength(0);
  });

  it("a small import that finishes before import.run's reply still shows its result (CI race)", async () => {
    await started();
    // The sidecar's final event overtakes the reply that names the job.
    m.rpc.mockImplementation(async (method: string) => {
      if (method !== "import.run") return undefined;
      emit(prog({ jobId: "other", done: true, rowsLoaded: 9 }));
      emit(prog({ done: true, rowsLoaded: 3 }));
      return { jobId: "job1" };
    });
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    expect(await screen.findByText("Imported 3 rows into dbo.t_2024_sales_v2.")).toBeTruthy();
    expect(screen.queryByText("Cancel import")).toBeNull();
  });

  it("existing table: skips the CREATE TABLE and runs only the chosen fixes", async () => {
    await started(TARGET({ tableExists: true }), { table: "t" });
    fireEvent.click(screen.getByText("Import 1,234 rows"));
    await screen.findByText("Cancel import");
    expect(m.exec).not.toHaveBeenCalled();
  });

  it("stops and reports when the DDL fails", async () => {
    await started();
    m.exec.mockResolvedValueOnce("object exists");
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "DDL failed: object exists"));
    expect(m.rpc.mock.calls.find((c) => c[0] === "import.run")).toBeUndefined();
    expect(screen.getByText("Create table and import 1,234 rows")).toBeTruthy();
  });

  it("asks for Prod confirmation and retries; declining stops quietly", async () => {
    await started();
    m.ask.mockResolvedValueOnce(true);
    m.rpc.mockImplementation(async (method: string) => {
      if (method !== "import.run") return undefined;
      return m.rpc.mock.calls.filter((c) => c[0] === "import.run").length === 1 ? { blocked: { reason: "prodFirstRun" } } : { jobId: "job1" };
    });
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await screen.findByText("Cancel import");
    const runs = m.rpc.mock.calls.filter((c) => c[0] === "import.run");
    expect(runs.map((c) => c[1].confirmedProd)).toEqual([false, true]);
    cleanup();
    m.ask.mockResolvedValueOnce(false);
    await toTarget();
    m.rpc.mockImplementation(async (method: string) => (method === "import.run" ? { blocked: { reason: "prodFirstRun" } } : undefined));
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await waitFor(() => expect((screen.getByText("Create table and import 1,234 rows") as HTMLButtonElement).disabled).toBe(false));
  });

  it("explains a reused file token and other run errors", async () => {
    await started();
    const e = new RpcError("x", -1 as never) as any;
    e.appCode = "fileToken";
    m.rpc.mockRejectedValueOnce(e);
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", expect.stringContaining("once per pick")));
    m.rpc.mockRejectedValueOnce(new Error("disk full"));
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", "disk full"));
  });

  it("reports a failure with committed rows, the reject log path, and copies the path", async () => {
    const write = vi.fn();
    Object.defineProperty(navigator, "clipboard", { value: { writeText: write }, configurable: true });
    await started();
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await screen.findByText("Cancel import");
    emit(prog({ done: true, error: "conversion failed", committedRows: 5000, rejected: 7, rejectFile: "/tmp/rejects.csv" }));
    expect(screen.getByText("conversion failed")).toBeTruthy();
    expect(screen.getByText(/5,000 rows were committed before the failure: resume after file row 5,000/)).toBeTruthy();
    expect(screen.getByText("/tmp/rejects.csv")).toBeTruthy();
    fireEvent.click(screen.getByText("Copy path"));
    expect(write).toHaveBeenCalledWith("/tmp/rejects.csv");
  });

  it("a commit cut short marks the table: the next import into it waits until the user says they checked it", async () => {
    await started();
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await screen.findByText("Cancel import");
    const unknown = "The connection failed while the import was being committed, so whether its 1,234 row(s) were written to dbo.t_2024_sales_v2 is unknown. Check the table before you import this file again.";
    emit(prog({ done: true, error: unknown, errorCode: "outcomeUnknown", rowsLoaded: 0 }));
    expect(screen.getByText(unknown)).toBeTruthy();
    expect(screen.getByText(/confirm you checked the table before it imports into it again/)).toBeTruthy();
    cleanup();

    // A new wizard on the same table (the file picked again): the import waits for the check.
    await toTarget();
    m.rpc.mockImplementation(async (method: string) => (method === "import.run" ? { jobId: "job2" } : undefined));
    const button = screen.getByText("Create table and import 1,234 rows") as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(screen.getByRole("alert").textContent).toContain("may already have written its rows");
    expect(screen.getByRole("alert").textContent).toContain("1,234 rows from 2024 sales.v2.csv");
    fireEvent.click(screen.getByLabelText(/I checked dbo.t_2024_sales_v2/));
    expect(button.disabled).toBe(false);
    // Unticked again: blocked again.
    fireEvent.click(screen.getByLabelText(/I checked dbo.t_2024_sales_v2/));
    expect(button.disabled).toBe(true);
    fireEvent.click(screen.getByLabelText(/I checked dbo.t_2024_sales_v2/));
    fireEvent.click(button);
    await screen.findByText("Cancel import");
    emit(prog({ jobId: "job2", done: true, rowsLoaded: 1234 }));
    cleanup();
    // That import succeeded: the table is no longer in doubt.
    await toTarget();
    expect((screen.getByText("Create table and import 1,234 rows") as HTMLButtonElement).disabled).toBe(false);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("cancels a running import; a cancelled result says nothing was kept", async () => {
    await started();
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await screen.findByText("Cancel import");
    m.rpc.mockRejectedValueOnce(new Error("too late"));
    fireEvent.click(screen.getByText("Cancel import"));
    expect(m.rpc).toHaveBeenLastCalledWith("import.cancel", { jobId: "job1" });
    emit(prog({ done: true, error: "cancelled", cancelled: true }));
    expect(screen.getByText("Cancelled — nothing was kept.")).toBeTruthy();
  });

  it("refuses to close while an import is running (Escape), but closes when idle", async () => {
    const { dialog } = await toTarget();
    m.rpc.mockImplementation(async (method: string) => (method === "import.run" ? { jobId: "job1" } : undefined));
    fireEvent.keyDown(window, { key: "Escape" });
    expect(useUi.getState().dialogs).not.toContain(dialog); // idle at the target step: closes
    cleanup();
    useUi.setState({ dialogs: [] });
    await toTarget();
    m.rpc.mockImplementation(async (method: string) => (method === "import.run" ? { jobId: "job1" } : undefined));
    fireEvent.click(screen.getByText("Create table and import 1,234 rows"));
    await screen.findByText("Cancel import");
    fireEvent.keyDown(window, { key: "Escape" });
    expect(m.toast).toHaveBeenCalledWith("info", "Cancel the import first.");
  });

  it("closes the wizard's session when it goes away", async () => {
    const { unmount } = await toTarget();
    unmount();
    expect(m.closeSession).toHaveBeenCalledWith("sid");
  });
});

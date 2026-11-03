// @vitest-environment jsdom
// The import wizard on Phase D engines. An in-memory SQLite / DuckDB database exists only in its query tab's session, so
// the wizard lists that session's tables and imports through that session (never a new one: that would be a new, empty
// database), and never closes it. Server engines keep the shared snapshot and a session of the wizard's own.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createElement } from "react";
import type { Engine, Profile, SnapObject } from "../../ipc/contract";
import { useProfiles } from "../../store/profiles";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useTabs, type TabState } from "../../store/tabs";
import { useUi } from "../../store/ui";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), toast: vi.fn(), ask: vi.fn(), notify: [] as Array<(e: any) => void>,
  pick: vi.fn(), openSession: vi.fn(), closeSession: vi.fn(), exec: vi.fn(), refresh: vi.fn(),
}));
vi.mock("../../ipc/client", () => ({
  rpc: m.rpc, shell: {},
  RpcError: class RpcError extends Error { appCode?: string; },
  onNotify: (_n: string, h: (e: any) => void) => { m.notify.push(h); return () => { m.notify = m.notify.filter((x) => x !== h); }; },
}));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast: m.toast, ask: m.ask }));
vi.mock("../importActions", () => ({ pickImportFile: m.pick, openImportSession: m.openSession, closeImportSession: m.closeSession, executeGuarded: m.exec }));
vi.mock("../../results/useExecutionStream", () => ({ useExecutionStream: () => undefined }));
vi.mock("../../results/ResultGrid", () => ({ ResultGrid: () => createElement("div") }));

const { ImportWizardDialog } = await import("../ImportWizardDialog");

const INS = {
  inspectionId: "i1", fileName: "rows.csv", fileBytes: 10, format: "csv", delimiter: ",", hasHeader: true, encoding: "utf-8", sheets: [], sheet: null, rowCount: 3,
  columns: [{ name: "id", ordinal: 0, kind: "int32", precision: 0, scale: 0, maxLength: 1, nullCount: 0, firstNullRows: [], distinctEstimate: 3, min: "1", max: "3", candidateKey: true }],
  previewExecutionId: "prev1", notes: [],
};
const TARGET = { tableExists: true, mapping: [{ fileColumn: "id", targetColumn: "id" }], verdicts: [], createTableSql: null, proposedTypes: {} };
const profile = (engine: Engine, location: Profile["location"]) => ({ id: "p", name: "Local", engine, location, auth: "none", environment: "dev" }) as never;
const tab = (o: Partial<TabState>) => useTabs.getState().add({ id: o.id ?? "t1", title: "q.sql", profileId: "p", ...o });
const obj = (schema: string, name: string, kind: SnapObject["kind"] = "table"): SnapObject => ({ id: name.length, schema, name, kind, columns: [{ name: "id", type: "INTEGER", nullable: false, identity: false, computed: false, pk: true, ordinal: 1 }] });
const entry = (database: string, objects: SnapObject[], sessionId?: string) => ({
  [snapKey("p", database, sessionId)]: { key: snapKey("p", database, sessionId), profileId: "p", database, sessionId, status: "fresh", version: 1, snapshot: { profileId: "p", database, status: "fresh", objects, fks: [] } } as never,
});
const emit = (e: Record<string, unknown>) => act(() => m.notify.forEach((h) => h(e)));
const listed = () => Array.from(document.querySelectorAll("#import-tables option")).map((o) => (o as HTMLOptionElement).value);

async function toTarget(d: Record<string, unknown>) {
  const dialog = { kind: "import", profileId: "p", table: "orders", ...d } as never;
  useUi.getState().openDialog(dialog);
  const r = render(<ImportWizardDialog d={dialog} />);
  m.rpc.mockImplementation(async (method: string) => (method === "import.inspect" ? INS : method === "import.checkTarget" ? TARGET : method === "import.run" ? { jobId: "job1" } : undefined));
  fireEvent.click(screen.getByText("Choose file…"));
  await screen.findByText(/3 rows/);
  return r;
}

beforeEach(() => {
  vi.clearAllMocks();
  m.notify = [];
  m.pick.mockResolvedValue({ token: "tok", name: "rows.csv", size: 10 });
  m.openSession.mockResolvedValue("own");
  m.closeSession.mockResolvedValue(undefined);
  m.exec.mockResolvedValue(null);
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useSnapshots.setState({ entries: {}, refresh: m.refresh } as never);
  useUi.setState({ dialogs: [] });
});
afterEach(cleanup);

describe.each<[Engine, string]>([["sqlite", "main"], ["duckDb", "memory"]])("an in-memory %s database", (engine, database) => {
  it("lists the tab session's tables, checks and imports through that session, refreshes its view, never closes it", async () => {
    useProfiles.setState({ profiles: [profile(engine, "memory")] });
    tab({ sessionId: "s1", database });
    useSnapshots.setState({ entries: entry(database, [obj("main", "orders"), obj("temp", "scratch"), obj("main", "v", "view")], "s1") });
    const { unmount } = await toTarget({ database, schema: "main", sessionId: "s1" });
    expect(listed()).toEqual(["main.orders", "temp.scratch"]);
    fireEvent.click(screen.getByText("Next: target →"));
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("import.checkTarget", expect.objectContaining({ sessionId: "s1", schema: "main", table: "orders", newTable: false })));
    fireEvent.click(await screen.findByText("Import 3 rows"));
    await screen.findByText("Cancel import");
    expect(m.rpc).toHaveBeenCalledWith("import.run", expect.objectContaining({ sessionId: "s1", table: "orders" }));
    emit({ jobId: "job1", phase: "done", rowsProcessed: 3, rowsLoaded: 3, bytesProcessed: 10, totalBytes: 10, rejected: 0, elapsedMs: 5, done: true, cancelled: false });
    expect(m.refresh).toHaveBeenCalledWith("p", database, false, "s1");
    expect(m.openSession).not.toHaveBeenCalled();
    unmount();
    expect(m.closeSession).not.toHaveBeenCalledWith("s1");
  });

  it("once the tab's session is gone, the wizard asks for a session of its own (refused for memory) instead of the stale one", async () => {
    useProfiles.setState({ profiles: [profile(engine, "memory")] });
    tab({ sessionId: "s1", database });
    useSnapshots.setState({ entries: entry(database, [obj("main", "orders")], "s1") });
    m.openSession.mockRejectedValue(new Error("In-memory: each tab has its own database, so there is no shared one to read. Use a query tab."));
    await toTarget({ database, schema: "main", sessionId: "s1" });
    act(() => useTabs.getState().update("t1", { sessionId: undefined }));
    expect(listed()).toEqual([]); // nothing to list: the database went with its session
    fireEvent.click(screen.getByText("Next: target →"));
    await waitFor(() => expect(m.toast).toHaveBeenCalledWith("error", expect.stringMatching(/^In-memory: each tab has its own database/)));
    expect(m.rpc).not.toHaveBeenCalledWith("import.checkTarget", expect.objectContaining({ sessionId: "s1" }));
  });
});

describe.each<[Engine, string]>([["mssql", "Sales"], ["pg", "shop"], ["mySql", "shop"]])("regression: %s", (engine, database) => {
  it("lists the shared snapshot's tables and imports through a session of the wizard's own, closed at the end", async () => {
    useProfiles.setState({ profiles: [profile(engine, null)] });
    tab({ sessionId: "s1", database });
    useSnapshots.setState({ entries: { ...entry(database, [obj("dbo", "orders")]), ...entry(database, [obj("x", "own")], "s1") } });
    const { unmount } = await toTarget({ database, schema: "dbo", sessionId: "s1" });
    expect(listed()).toEqual(["dbo.orders"]);
    fireEvent.click(screen.getByText("Next: target →"));
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("import.checkTarget", expect.objectContaining({ sessionId: "own" })));
    expect(m.openSession).toHaveBeenCalledWith("p", database);
    unmount();
    expect(m.closeSession).toHaveBeenCalledWith("own");
  });
});

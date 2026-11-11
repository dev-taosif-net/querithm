// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

const m = vi.hoisted(() => ({
  rpc: vi.fn(), toast: vi.fn(), newQueryTab: vi.fn(), applyTabEdits: vi.fn(), openTabsFor: vi.fn(() => [{ tabId: "t", title: "q", text: "x" }]),
  getModel: vi.fn(), sessionFor: vi.fn(), diffs: [] as any[], models: [] as any[],
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast: m.toast }));
vi.mock("../../store/connections", () => ({ useConnections: { getState: () => ({ sessionFor: m.sessionFor }) }, isDisconnected: () => false }));
vi.mock("../../app/actions", () => ({ newQueryTab: m.newQueryTab }));
vi.mock("../../editor/editors", () => ({ getModel: m.getModel, languageFor: (e?: string) => (e === "pg" ? "pgsql" : "tsql") }));
vi.mock("../../editor/monaco", () => {
  const mk = (value = "") => ({ v: value, getValue() { return this.v; }, setValue(x: string) { this.v = x; }, dispose: vi.fn() });
  return {
    initMonaco: vi.fn(),
    monaco: {
      editor: {
        createModel: (value: string, lang: string) => { const x = { ...mk(value), lang }; m.models.push(x); return x; },
        createDiffEditor: () => { const d = { model: null as any, setModel(x: any) { this.model = x; }, getModel() { return this.model; }, dispose: vi.fn() }; m.diffs.push(d); return d; },
      },
    },
  };
});
vi.mock("../refactorActions", () => ({ applyTabEdits: m.applyTabEdits, openTabsFor: m.openTabsFor }));

const { RenameDialog } = await import("../RenameDialog");

const hit = (id: string, over: Record<string, unknown> = {}) => ({ id, line: 4, column: 1, offset: 0, length: 1, snippet: `ref ${id}`, confidence: "exact", definition: false, ...over });
const preview = (over: Record<string, unknown> = {}) => ({
  objects: [
    { key: "o1", object: { schema: "dbo", name: "usp_a", kind: "proc" }, before: "BEFORE1", after: "AFTER1", hits: [hit("e1"), hit("p1", { confidence: "possible", reason: "dynamic" })], serverUpdated: false },
    { key: "o2", object: { schema: "dbo", name: "v1", kind: "view" }, before: "B2", after: "A2", hits: [], serverUpdated: true },
  ],
  tabs: [{ tabId: "t", edits: [{ offset: 0, length: 1, text: "n" }] }], sql: "EXEC sp_rename ...", statements: 1, warnings: ["careful"], unchecked: [{ object: { schema: "dbo", name: "enc", kind: "proc" }, reason: "enc" }], transactional: true, ...over,
});

const objDialog = (target: any = { kind: "table", schema: "dbo", name: "Orders", column: null }) =>
  ({ kind: "rename", profileId: "p", database: "db", target }) as never;
const localDialog = (original: string, dialect: "tsql" | "pg" = "tsql") => ({ kind: "rename", local: { tabId: "t", spans: [{ offset: 0, length: original.length }, { offset: 12, length: original.length }], original, dialect } }) as never;

beforeEach(() => {
  vi.clearAllMocks();
  m.diffs.length = 0; m.models.length = 0;
  m.sessionFor.mockResolvedValue("sess");
  useProfiles.setState({ profiles: [{ id: "p", name: "Billing", engine: "mssql", environment: "dev" } as never] });
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

const nameInput = () => screen.getByLabelText("New name") as HTMLInputElement;

describe("local rename", () => {
  it("renames every occurrence in the tab as one edit, keeping the @ of a T-SQL variable", () => {
    m.getModel.mockReturnValue({ getValue: () => "@total int; @total" });
    const d = localDialog("@total");
    useUi.getState().openDialog(d);
    render(<RenameDialog d={d as never} />);
    expect(nameInput().value).toBe("total");
    fireEvent.change(nameInput(), { target: { value: "sum" } });
    fireEvent.click(screen.getByText("Rename 2 occurrence(s)"));
    expect(m.applyTabEdits).toHaveBeenCalledWith([{ tabId: "t", edits: [{ offset: 0, length: 6, text: "@sum" }, { offset: 12, length: 6, text: "@sum" }] }]);
    expect(useUi.getState().dialogs).not.toContain(d);
  });

  it("brackets or quotes names that are not plain identifiers, per engine", () => {
    m.getModel.mockReturnValue({ getValue: () => "Orders        Orders" });
    render(<RenameDialog d={localDialog("Orders") as never} />);
    fireEvent.change(nameInput(), { target: { value: "My Orders" } });
    fireEvent.click(screen.getByText("Rename 2 occurrence(s)"));
    expect(m.applyTabEdits.mock.calls[0][0][0].edits[0].text).toBe("[My Orders]");
    cleanup();
    render(<RenameDialog d={localDialog("orders", "pg") as never} />);
    m.getModel.mockReturnValue({ getValue: () => "orders       orders" });
    fireEvent.change(nameInput(), { target: { value: "My Orders" } });
    fireEvent.click(screen.getByText("Rename 2 occurrence(s)"));
    expect(m.applyTabEdits.mock.calls[1][0][0].edits[0].text).toBe('"My Orders"');
  });

  it("unchanged or empty names just close; a closed tab just closes; Enter applies", () => {
    const d = localDialog("[a]");
    useUi.getState().openDialog(d);
    m.getModel.mockReturnValue(undefined);
    render(<RenameDialog d={d as never} />);
    expect(nameInput().value).toBe("a");
    fireEvent.click(screen.getByText("Rename 2 occurrence(s)"));
    expect(m.applyTabEdits).not.toHaveBeenCalled();
    fireEvent.change(nameInput(), { target: { value: "b" } });
    fireEvent.click(screen.getByText("Rename 2 occurrence(s)"));
    expect(m.applyTabEdits).not.toHaveBeenCalled();
    m.getModel.mockReturnValue({ getValue: () => "[a]         [a]" });
    fireEvent.keyDown(window, { key: "Enter" });
    expect(m.applyTabEdits).toHaveBeenCalledTimes(1);
    expect(m.applyTabEdits.mock.calls[0][0][0].edits[0].text).toBe("[b]");
    fireEvent.click(screen.getByText("Cancel"));
  });
});

describe("object rename", () => {
  it("previews after a debounce and shows objects, hits, warnings and unchecked objects", async () => {
    vi.useFakeTimers();
    m.rpc.mockResolvedValue(preview());
    render(<RenameDialog d={objDialog()} />);
    expect(screen.getByText("Rename table dbo.Orders")).toBeTruthy();
    // The script opens on this server and database: the dialog says which (environment in words).
    expect(screen.getByTestId("server-chip").title).toBe("DEV Billing › db");
    expect(m.rpc).not.toHaveBeenCalled();
    fireEvent.change(nameInput(), { target: { value: "Orders2" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(350); });
    expect(m.rpc).toHaveBeenCalledWith("refactor.previewRename", {
      sessionId: "sess", database: "db", target: expect.objectContaining({ name: "Orders" }), newName: "Orders2", tabs: [{ tabId: "t", title: "q", text: "x" }], include: [], exclude: [],
    });
    vi.useRealTimers();
    expect(await screen.findByText("careful")).toBeTruthy();
    expect(screen.getByText("dbo.usp_a")).toBeTruthy();
    expect(screen.getByText("updated by the server")).toBeTruthy();
    expect(screen.getByText(/Cannot be checked: dbo\.enc/)).toBeTruthy();
    expect(screen.getByText("possible")).toBeTruthy();
    expect(screen.getByText(/Also update 1 reference\(s\) in open tabs/)).toBeTruthy();
    // focus defaults to the first object that is not server-updated; the diff shows its text
    await waitFor(() => expect(m.diffs[0].model.original.getValue()).toBe("BEFORE1"));
    expect(m.diffs[0].model.modified.getValue()).toBe("AFTER1");
    expect(m.models[0].lang).toBe("tsql");
    // The focused group is a highlighted row: its muted line numbers take the highlight's muted color (4.5:1, D4 S11 smoke-ui).
    expect(screen.getByText("dbo.usp_a").closest("div")!.className).toContain("q-hl");
    fireEvent.click(screen.getByText("dbo.v1"));
    await waitFor(() => expect(m.diffs[0].model.modified.getValue()).toBe("A2"));
    expect(screen.getByText("dbo.v1").closest("div")!.className).toContain("q-hl");
    expect(screen.getByText("dbo.usp_a").closest("div")!.className).not.toContain("q-hl");
  });

  it("possible hits start unchecked; toggling re-requests with include/exclude", async () => {
    m.rpc.mockResolvedValue(preview());
    render(<RenameDialog d={objDialog()} />);
    fireEvent.change(nameInput(), { target: { value: "N" } });
    const exact = await screen.findByText("ref e1");
    const boxes = () => screen.getAllByRole("checkbox") as HTMLInputElement[];
    const rowBox = (text: string) => (screen.getByText(text).closest("label")!.querySelector("input") as HTMLInputElement);
    expect(rowBox("ref e1").checked).toBe(true);
    expect(rowBox("ref p1").checked).toBe(false);
    void exact; void boxes;
    fireEvent.click(rowBox("ref p1"));
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].include).toEqual(["p1"]));
    fireEvent.click(rowBox("ref e1"));
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].exclude).toEqual(["e1"]));
    fireEvent.click(rowBox("ref e1"));
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].exclude).toEqual([]));
    fireEvent.click(rowBox("ref p1"));
    await waitFor(() => expect(m.rpc.mock.calls.at(-1)![1].include).toEqual([]));
  });

  it("opens the script in a new query tab, applies open-tab edits unless declined, then closes", async () => {
    const d = objDialog();
    useUi.getState().openDialog(d);
    m.rpc.mockResolvedValue(preview());
    render(<RenameDialog d={d} />);
    fireEvent.change(nameInput(), { target: { value: "N" } });
    await screen.findByText("careful");
    fireEvent.click(screen.getByText("Open script in a query tab"));
    await waitFor(() => expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "p", database: "db", text: "EXEC sp_rename ...", title: "Rename.sql" }));
    expect(m.applyTabEdits).toHaveBeenCalledWith(preview().tabs);
    expect(m.toast).toHaveBeenCalledWith("info", expect.stringContaining("rename script"));
    expect(useUi.getState().dialogs).not.toContain(d);
  });

  it("leaves open tabs alone when 'also update' is unchecked", async () => {
    m.rpc.mockResolvedValue(preview());
    render(<RenameDialog d={objDialog()} />);
    fireEvent.change(nameInput(), { target: { value: "N" } });
    await screen.findByText("careful");
    fireEvent.click(screen.getByLabelText(/Also update/));
    fireEvent.click(screen.getByText("Open script in a query tab"));
    await waitFor(() => expect(m.newQueryTab).toHaveBeenCalled());
    expect(m.applyTabEdits).not.toHaveBeenCalled();
  });

  it("warns that PostgreSQL-style engines cannot roll back DDL, and explains an empty preview", async () => {
    m.rpc.mockResolvedValue(preview({ objects: [], tabs: [], warnings: [], unchecked: [], transactional: false }));
    render(<RenameDialog d={objDialog({ kind: "column", schema: "public", name: "orders", column: "total" })} />);
    expect(screen.getByText("Rename column public.orders.total")).toBeTruthy();
    expect(nameInput().value).toBe("total");
    fireEvent.change(nameInput(), { target: { value: "amount" } });
    expect(await screen.findByText(/cannot roll back if a step fails/)).toBeTruthy();
    expect(screen.getByText(/No database object references it: only the column itself is renamed/)).toBeTruthy();
    expect(screen.queryByText(/Also update/)).toBeNull();
  });

  it("mentions open-tab references in the empty-preview note", async () => {
    m.rpc.mockResolvedValue(preview({ objects: [], warnings: [], unchecked: [] }));
    render(<RenameDialog d={objDialog()} />);
    fireEvent.change(nameInput(), { target: { value: "N" } });
    expect(await screen.findByText(/plus 1 reference\(s\) in open tabs/)).toBeTruthy();
  });

  it("shows a server error and keeps the button disabled; reverting the name clears the preview", async () => {
    m.rpc.mockRejectedValueOnce(new Error("name taken"));
    render(<RenameDialog d={objDialog()} />);
    const open = screen.getByText("Open script in a query tab") as HTMLButtonElement;
    expect(open.disabled).toBe(true);
    fireEvent.change(nameInput(), { target: { value: "Taken" } });
    expect((await screen.findByRole("alert")).textContent).toBe("name taken");
    expect(open.disabled).toBe(true);
    m.rpc.mockResolvedValue(preview());
    fireEvent.change(nameInput(), { target: { value: "Free" } });
    await screen.findByText("careful");
    fireEvent.change(nameInput(), { target: { value: "Orders" } });
    await waitFor(() => expect(screen.queryByText("careful")).toBeNull());
  });

  it("reports a failure while building the final script", async () => {
    m.rpc.mockResolvedValueOnce(preview());
    render(<RenameDialog d={objDialog()} />);
    fireEvent.change(nameInput(), { target: { value: "N" } });
    await screen.findByText("careful");
    m.rpc.mockRejectedValueOnce(new Error("stale"));
    fireEvent.click(screen.getByText("Open script in a query tab"));
    expect((await screen.findByRole("alert")).textContent).toBe("stale");
    expect(m.newQueryTab).not.toHaveBeenCalled();
  });

  it("uses the PostgreSQL language for the diff on PostgreSQL profiles", async () => {
    useProfiles.setState({ profiles: [{ id: "p", engine: "pg" } as never] });
    m.rpc.mockResolvedValue(preview());
    render(<RenameDialog d={objDialog({ kind: "table", schema: "public", name: "orders", column: null })} />);
    fireEvent.change(nameInput(), { target: { value: "orders2" } });
    await waitFor(() => expect(m.models.length).toBeGreaterThan(0));
    expect(m.models[0].lang).toBe("pgsql");
  });
});

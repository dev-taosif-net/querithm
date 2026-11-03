// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  tauri: true,
  files: new Map<string, string>(),
  open: vi.fn(),
  save: vi.fn(),
  write: vi.fn(),
  newNotebookTab: vi.fn((o: { id?: string }) => o.id ?? "nb-new"),
  serializeTab: vi.fn((): string | undefined => '{"doc":1}'),
  activateTab: vi.fn(),
  toast: vi.fn(),
}));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return h.tauri; } }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: (...a: unknown[]) => h.open(...a), save: (...a: unknown[]) => h.save(...a) }));
vi.mock("@tauri-apps/plugin-fs", () => ({
  readTextFile: async (p: string) => {
    if (!h.files.has(p)) throw new Error("ENOENT");
    return h.files.get(p)!;
  },
  writeTextFile: (p: string, t: string) => h.write(p, t),
}));
vi.mock("../runtime", () => ({ newNotebookTab: h.newNotebookTab, serializeTab: h.serializeTab, syncSources: () => {} }));
vi.mock("../../app/actions", () => ({ activateTab: h.activateTab }));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast: h.toast }));

import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { importIpynb, openNotebookFile, openNotebookText, saveNotebook } from "../files";
import { notebookHooks } from "../hooks";

const file = (connection: unknown = null) => JSON.stringify({ format: "querithm.sqlbook", version: 1, connection, cells: [{ id: "a", kind: "sql", source: "select 1" }] });

beforeEach(() => {
  vi.clearAllMocks();
  h.tauri = true;
  h.files.clear();
  h.write.mockResolvedValue(undefined);
  useTabs.setState({ tabs: {}, order: [], activeId: undefined });
  useProfiles.setState({ profiles: [{ id: "p1", engine: "mssql" } as never] });
});

describe("opening notebooks", () => {
  it("opens on its saved connection when that profile exists", async () => {
    h.files.set("/w/a.sqlbook", file({ profileId: "p1", database: "Sales" }));
    h.open.mockResolvedValueOnce("/w/a.sqlbook");
    await openNotebookFile();
    expect(h.newNotebookTab).toHaveBeenCalledWith(expect.objectContaining({ title: "a.sqlbook", filePath: "/w/a.sqlbook", profileId: "p1", database: "Sales", notices: [] }));
  });
  it("a connection that is not on this computer opens it disconnected with a notice", () => {
    openNotebookText(file({ profileId: "someone-elses", database: "X" }), { title: "shared.sqlbook" });
    expect(h.newNotebookTab).toHaveBeenCalledWith(expect.objectContaining({ profileId: "", notices: [expect.stringMatching(/not on this computer/)] }));
  });
  it("a file that cannot be opened says why and opens nothing", () => {
    expect(openNotebookText(JSON.stringify({ format: "querithm.sqlbook", version: 9, cells: [] }), { title: "new.sqlbook" })).toBeNull();
    expect(h.toast).toHaveBeenCalledWith("error", expect.stringMatching(/Could not open new.sqlbook: .*newer Querithm/));
    expect(h.newNotebookTab).not.toHaveBeenCalled();
  });
  it("a file already open is activated, not opened twice", async () => {
    useTabs.getState().add({ id: "t9", title: "a.sqlbook", notebook: true, filePath: "/w/a.sqlbook" });
    h.open.mockResolvedValueOnce("/w/a.sqlbook");
    await openNotebookFile();
    expect(h.activateTab).toHaveBeenCalledWith("t9");
    expect(h.newNotebookTab).not.toHaveBeenCalled();
  });
  it("a closed notebook reopens with the tab's own connection", () => {
    notebookHooks.reopen!({ text: file({ profileId: "p1" }), title: "x.sqlbook", profileId: "", activate: true, atEnd: false, dirty: true });
    expect(h.newNotebookTab).toHaveBeenCalledWith(expect.objectContaining({ profileId: "", dirty: true }));
  });
});

describe("saving notebooks", () => {
  it("Save writes the file it came from and clears the modified mark", async () => {
    useTabs.getState().add({ id: "t1", title: "a.sqlbook", notebook: true, filePath: "/w/a.sqlbook", dirty: true });
    expect(await saveNotebook("t1")).toBe(true);
    expect(h.write).toHaveBeenCalledWith("/w/a.sqlbook", '{"doc":1}');
    expect(useTabs.getState().tabs.t1.dirty).toBe(false);
  });
  it("an untitled notebook asks where; cancelling saves nothing", async () => {
    useTabs.getState().add({ id: "t1", title: "Notebook1.sqlbook", notebook: true, dirty: true });
    h.save.mockResolvedValueOnce(null);
    expect(await saveNotebook("t1")).toBe(false);
    h.save.mockResolvedValueOnce("/w/q3.sqlbook");
    expect(await saveNotebook("t1")).toBe(true);
    expect(useTabs.getState().tabs.t1).toMatchObject({ filePath: "/w/q3.sqlbook", title: "q3.sqlbook", dirty: false });
  });
  it("a failed write (disk full) keeps the notebook modified and says the recovery copy is intact", async () => {
    useTabs.getState().add({ id: "t1", title: "a.sqlbook", notebook: true, filePath: "/w/a.sqlbook", dirty: true });
    h.write.mockRejectedValueOnce(new Error("No space left on device"));
    expect(await saveNotebook("t1")).toBe(false);
    expect(useTabs.getState().tabs.t1.dirty).toBe(true);
    expect(h.toast).toHaveBeenCalledWith("error", expect.stringMatching(/No space left on device.*recovery copy is intact/));
  });
  it("is not for query tabs", async () => {
    useTabs.getState().add({ id: "q", title: "q.sql" });
    expect(await saveNotebook("q")).toBe(false);
  });
});

describe("Jupyter import", () => {
  it("opens the imported cells as a new, unsaved notebook and says what was skipped", async () => {
    h.files.set("/w/a.ipynb", JSON.stringify({ nbformat: 4, cells: [{ cell_type: "code", source: "%%sql\nselect 1" }, { cell_type: "code", source: "x = 1" }] }));
    h.open.mockResolvedValueOnce("/w/a.ipynb");
    await importIpynb();
    expect(h.newNotebookTab).toHaveBeenCalledWith(expect.objectContaining({
      title: "a.sqlbook", dirty: true, cells: [expect.objectContaining({ kind: "sql", source: "select 1" })], notices: [expect.stringMatching(/1 cell was skipped/)],
    }));
  });
  it("a bad file is refused with the reason", async () => {
    h.files.set("/w/b.ipynb", "{");
    h.open.mockResolvedValueOnce("/w/b.ipynb");
    await importIpynb();
    expect(h.toast).toHaveBeenCalledWith("error", expect.stringMatching(/Could not import b.ipynb/));
  });
});

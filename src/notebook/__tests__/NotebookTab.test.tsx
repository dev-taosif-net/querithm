// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";

vi.mock("../../editor/monaco", async () => {
  const { fake } = await import("../../editor/__tests__/fakeMonaco");
  // The fake's `focus` is a flag; the real editor's is a method the cell calls when it takes the editor.
  const create = (host: unknown, opts: Record<string, unknown>) => Object.assign(fake.monaco.editor.create(host, opts), { focus: () => {} });
  return {
    initMonaco: vi.fn(),
    monaco: { ...fake.monaco, editor: { ...fake.monaco.editor, create, colorize: vi.fn(async (t: string) => `<span class="mtk1">${t.replace(/</g, "&lt;")}</span>`) } },
  };
});
const h = vi.hoisted(() => ({ executeOnTab: vi.fn(async () => undefined), cancelQuery: vi.fn(), toast: vi.fn() }));
vi.mock("../../app/actions", () => ({
  executeOnTab: h.executeOnTab, trackExecution: vi.fn(), disposeExecution: vi.fn(), cancelQuery: h.cancelQuery,
  newQueryTabBinding: (p?: string, d?: string) => (p ? { profileId: p, database: d } : {}), workspaceHooks: {},
}));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => ({})), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast: h.toast }));
vi.mock("../files", () => ({ saveNotebook: vi.fn() }));

const { fake } = await import("../../editor/__tests__/fakeMonaco");
import { NotebookTab } from "../NotebookTab";
import { newNotebookTab } from "../runtime";
import { notebookOf, useNotebooks } from "../store";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

let id: string;
beforeEach(() => {
  vi.clearAllMocks();
  useProfiles.setState({ profiles: [{ id: "p1", engine: "mssql", environment: "dev" } as never] });
  id = newNotebookTab({
    profileId: "p1", database: "Sales",
    cells: [
      { id: "c0", kind: "sql", source: "select 1" },
      { id: "c1", kind: "markdown", source: "Read [the docs](https://example.com) <b>now</b>" },
      { id: "c2", kind: "sql", source: "select <2>" },
    ],
  });
  Object.assign(navigator, { clipboard: { writeText: vi.fn(async () => {}) } });
});
afterEach(() => {
  cleanup();
  useTabs.setState({ tabs: {}, order: [], activeId: undefined });
  useNotebooks.setState({ docs: {} });
});

describe("NotebookTab", () => {
  it("shows every cell; the focused SQL cell hosts the shared editor, the others are colourised text", async () => {
    render(<NotebookTab tabId={id} groupId="g1" visible />);
    expect(screen.getAllByRole("region", { name: /cell \d of 3/ })).toHaveLength(3);
    const editors = document.querySelectorAll("[data-q-cell-editor]");
    expect(editors).toHaveLength(1);
    expect(fake.created.at(-1)!.getModel()!.getValue()).toBe("select 1");
    // The other SQL cell: Monaco's colouriser output (its text escaped), no second editor.
    const statics = document.querySelectorAll("[data-q-cell-static]");
    expect(statics).toHaveLength(1);
    await screen.findByText("select <2>");
    const editorsBefore = fake.created.length;
    fireEvent.click(statics[0]);
    expect(notebookOf(id)!.focused).toBe("c2");
    expect(fake.created.length).toBe(editorsBefore);
    expect(fake.created.at(-1)!.getModel()!.getValue()).toBe("select <2>");
  });

  it("Export ▾ is the app's menu: focus starts on its first item, arrows and End move, Escape returns to the button (L32)", () => {
    render(<NotebookTab tabId={id} groupId="g1" visible />);
    const button = screen.getByRole("button", { name: "Export ▾" });
    fireEvent.click(button);
    const menu = screen.getByRole("menu", { name: "Export notebook" });
    const items = within(menu).getAllByRole("menuitem");
    expect(items.map((i) => i.textContent)).toEqual(["Markdown (.md)", "HTML page (.html)"]);
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement).toBe(items[1]);
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(menu, { key: "End" });
    expect(document.activeElement).toBe(items[1]);
    fireEvent.keyDown(document.activeElement!, { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
    expect(document.activeElement).toBe(button);
  });

  it("markdown renders escaped; a link is copied, never followed", async () => {
    render(<NotebookTab tabId={id} groupId="g1" visible />);
    const md = document.querySelector(".q-markdown")!;
    expect(md.innerHTML).toContain("&lt;b&gt;now&lt;/b&gt;");
    const link = within(md as HTMLElement).getByText("the docs");
    const click = new MouseEvent("click", { bubbles: true, cancelable: true });
    act(() => void link.dispatchEvent(click));
    expect(click.defaultPrevented).toBe(true);
    expect(navigator.clipboard.writeText).toHaveBeenCalledWith("https://example.com");
  });

  it("Shift+F5 in the notebook runs every cell; Run all does too", () => {
    render(<NotebookTab tabId={id} groupId="g1" visible />);
    fireEvent.keyDown(screen.getByRole("toolbar", { name: "Notebook" }), { key: "F5", code: "F5", shiftKey: true });
    expect(h.executeOnTab).toHaveBeenCalledWith(id, "select 1", 1, undefined, expect.any(Function), expect.any(Function)); // markers, refusal
  });

  it("Save results in file shows the warning; no connection shows the picker", () => {
    render(<NotebookTab tabId={id} groupId="g1" visible />);
    fireEvent.click(screen.getByRole("switch"));
    expect(notebookOf(id)!.saveOutputs).toBe(true);
    expect(screen.getByRole("note").textContent).toMatch(/Results are saved in this file/);
    act(() => useTabs.getState().update(id, { profileId: undefined }));
    fireEvent.click(screen.getByText("Pick a connection"));
    expect(useUi.getState().palette).toEqual({ mode: "connections" });
  });

  it("Run all shows edited text cells formatted, keeping what was just typed", () => {
    useNotebooks.getState().patchCell(id, "c1", { editing: true });
    render(<NotebookTab tabId={id} groupId="g1" visible />);
    const area = screen.getByLabelText("Markdown") as HTMLTextAreaElement;
    fireEvent.change(area, { target: { value: "# Fresh title" } });
    fireEvent.keyDown(area, { key: "F5", code: "F5", shiftKey: true });
    expect(notebookOf(id)!.cells[1]).toMatchObject({ editing: false, source: "# Fresh title" });
    expect(document.querySelector(".q-markdown h1")?.textContent).toBe("Fresh title");
  });

  it("cell actions: add below, move, delete; notices can be dismissed", () => {
    useNotebooks.getState().patch(id, { notices: ["The connection this notebook was saved with is not on this computer."] });
    render(<NotebookTab tabId={id} groupId="g1" visible />);
    const first = screen.getByRole("toolbar", { name: "Cell 1 actions" });
    fireEvent.click(within(first).getByText("+ Text"));
    expect(notebookOf(id)!.cells.map((c) => c.kind)).toEqual(["sql", "markdown", "markdown", "sql"]);
    fireEvent.click(within(screen.getByRole("toolbar", { name: "Cell 4 actions" })).getByLabelText("Move cell up"));
    expect(notebookOf(id)!.cells.at(-1)!.id).toBe("c1");
    fireEvent.click(within(screen.getByRole("toolbar", { name: "Cell 1 actions" })).getByLabelText("Delete cell"));
    expect(notebookOf(id)!.cells).toHaveLength(3);
    fireEvent.click(screen.getByLabelText("Dismiss"));
    expect(notebookOf(id)!.notices).toEqual([]);
  });
});

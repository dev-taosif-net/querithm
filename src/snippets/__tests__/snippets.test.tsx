// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { Snippet } from "../../ipc/contract";

const h = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  borrowed: { value: "", release: vi.fn(), container: null as HTMLElement | null, language: "" },
  editor: undefined as unknown,
}));

const { FakeRpcError } = vi.hoisted(() => ({
  FakeRpcError: class extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string, readonly data?: unknown) { super(message); }
  },
}));

vi.mock("../../ipc/client", () => ({ rpc: (...a: unknown[]) => h.rpc(...a), onNotify: () => () => {}, shell: {}, RpcError: FakeRpcError }));
vi.mock("../../editor/editors", () => ({
  languageFor: (e?: string) => (e === "pg" ? "pgsql" : "tsql"),
  editorForTab: () => h.editor,
  borrowEditor: (container: HTMLElement, text: string, language: string) => {
    Object.assign(h.borrowed, { container, value: text, language });
    return { editor: {}, getValue: () => h.borrowed.value, release: h.borrowed.release };
  },
}));

import { SNIPPET_RETRY_MS, blankSnippet, snippetsFor, useSnippets } from "../snippetStore";
import { SnippetEditor, SnippetsPanel } from "../SnippetsPanel";
import { escapeSnippetText, insertSnippet, saveSelectionAsSnippet } from "../snippetActions";
// @ts-expect-error -- Monaco ships no types for its internal snippet parser; the test uses it to check the real grammar.
import { SnippetParser } from "monaco-editor/esm/vs/editor/contrib/snippet/browser/snippetParser.js";
import { useUi } from "../../store/ui";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";

const sn = (over: Partial<Snippet>): Snippet => ({ id: "u1", title: "Mine", prefix: "mine", body: "SELECT 1;", dialect: null, tags: [], updatedAt: 1, builtIn: false, ...over });
const LIB: Snippet[] = [
  sn({ id: "builtin:tsql:selt", prefix: "selt", title: "SELECT TOP", dialect: "tsql", builtIn: true, body: "SELECT TOP (${1:100}) *" }),
  sn({ id: "builtin:pg:sell", prefix: "sell", title: "SELECT … LIMIT", dialect: "pg", builtIn: true }),
  sn({ id: "u1", prefix: "audit", title: "Audit columns", dialect: null, tags: ["ddl"] }),
  sn({ id: "u2", prefix: "pgonly", title: "PG only", dialect: "pg" }),
];

beforeEach(() => {
  h.rpc.mockReset();
  h.borrowed.release.mockClear();
  h.editor = undefined;
  useSnippets.setState({ snippets: [], loaded: false, error: undefined, failedAt: undefined });
  useUi.setState({ dialogs: [], toasts: [], palette: null });
  useProfiles.setState({ profiles: [{ id: "p-pg", engine: "pg" } as never, { id: "p-ms", engine: "mssql" } as never] });
  useTabs.setState({ activeId: "t1", tabs: { t1: { id: "t1", profileId: "p-ms" } as never }, order: ["t1"] } as never);
});
afterEach(() => cleanup());

describe("snippet store", () => {
  it("loads the library once, even when asked twice at the same time", async () => {
    h.rpc.mockResolvedValue(LIB);
    await Promise.all([useSnippets.getState().load(), useSnippets.getState().load()]);
    await useSnippets.getState().load();
    expect(h.rpc).toHaveBeenCalledTimes(1);
    expect(useSnippets.getState()).toMatchObject({ loaded: true, snippets: LIB });
    await useSnippets.getState().load(true);
    expect(h.rpc).toHaveBeenCalledTimes(2);
  });

  it("keeps a load failure as an error instead of throwing", async () => {
    h.rpc.mockRejectedValue(new Error("sidecar down"));
    await useSnippets.getState().load();
    expect(useSnippets.getState()).toMatchObject({ loaded: false, error: "sidecar down" });
  });

  it("after a failed load, unforced loads back off instead of asking again on every call", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    try {
      h.rpc.mockRejectedValue(new Error("sidecar down"));
      await useSnippets.getState().load();
      await useSnippets.getState().load();
      await useSnippets.getState().load();
      expect(h.rpc).toHaveBeenCalledTimes(1);
      vi.setSystemTime(Date.now() + SNIPPET_RETRY_MS);
      h.rpc.mockResolvedValue(LIB);
      await useSnippets.getState().load();
      expect(h.rpc).toHaveBeenCalledTimes(2);
      expect(useSnippets.getState()).toMatchObject({ loaded: true, error: undefined, failedAt: undefined });
    } finally {
      vi.useRealTimers();
    }
  });

  it("a forced load retries a failure at once", async () => {
    h.rpc.mockRejectedValueOnce(new Error("sidecar down")).mockResolvedValue(LIB);
    await useSnippets.getState().load();
    await useSnippets.getState().load(true);
    expect(h.rpc).toHaveBeenCalledTimes(2);
    expect(useSnippets.getState().loaded).toBe(true);
  });

  it("save replaces the snippet in place; a refusal reports the sidecar's field", async () => {
    useSnippets.setState({ snippets: LIB, loaded: true });
    h.rpc.mockResolvedValueOnce({ ...LIB[2], title: "Renamed" });
    expect(await useSnippets.getState().save(LIB[2])).toMatchObject({ ok: true });
    expect(useSnippets.getState().snippets.filter((s) => s.id === "u1")).toEqual([{ ...LIB[2], title: "Renamed" }]);
    h.rpc.mockRejectedValueOnce(new FakeRpcError("The prefix 'audit' is already used", -32001, "snippetPrefixTaken", { field: "prefix" }));
    expect(await useSnippets.getState().save(blankSnippet({ prefix: "audit" }))).toEqual({ ok: false, field: "prefix", message: "The prefix 'audit' is already used" });
  });

  it("remove drops the snippet, or says why it could not", async () => {
    useSnippets.setState({ snippets: LIB, loaded: true });
    expect(await useSnippets.getState().remove("u1")).toEqual({ ok: true });
    expect(h.rpc).toHaveBeenCalledWith("snippet.delete", { id: "u1" });
    expect(useSnippets.getState().snippets.some((s) => s.id === "u1")).toBe(false);
    h.rpc.mockRejectedValueOnce(new Error("Built-in snippets cannot be deleted."));
    expect(await useSnippets.getState().remove("builtin:pg:sell")).toEqual({ ok: false, message: "Built-in snippets cannot be deleted." });
  });

  it("a dialect sees its own snippets and the dialect-free ones", () => {
    expect(snippetsFor(LIB, "tsql").map((s) => s.prefix)).toEqual(["selt", "audit"]);
    expect(snippetsFor(LIB, "pg").map((s) => s.prefix)).toEqual(["sell", "audit", "pgonly"]);
    // Phase D: the local and MySQL dialects get the dialect-free ones, plus their own.
    expect(snippetsFor(LIB, "sqlite").map((s) => s.prefix)).toEqual(["audit"]);
    expect(snippetsFor([...LIB, blankSnippet({ id: "m1", prefix: "myonly", dialect: "mySql" })], "mySql").map((s) => s.prefix)).toEqual(["audit", "myonly"]);
    expect(snippetsFor([...LIB, blankSnippet({ id: "d1", prefix: "duckonly", dialect: "duckDb" })], "duckDb").map((s) => s.prefix)).toEqual(["audit", "duckonly"]);
    expect(snippetsFor([...LIB, blankSnippet({ id: "o1", prefix: "plblock", dialect: "oracle" })], "oracle").map((s) => s.prefix)).toEqual(["audit", "plblock"]);
  });

  it("the snippet editor offers every dialect, the Phase D ones included", () => {
    render(<SnippetEditor snippet={blankSnippet({ prefix: "x" })} close={() => {}} />);
    const options = [...document.querySelectorAll("option")].map((o) => o.getAttribute("value"));
    expect(options).toEqual(expect.arrayContaining(["tsql", "pg", "sqlite", "mySql", "duckDb", "oracle"]));
  });

  it("a hidden engine's dialect is not offered and its built-ins stay out of All dialects (D4 S12)", async () => {
    const engines = (oracle: boolean) => ["mssql", "pg", "sqlite", "mySql", "duckDb", "oracle"].map((engine) => ({ engine, exposed: engine !== "oracle" || oracle }));
    const ora = sn({ id: "builtin:oracle:plblock", prefix: "plblock", title: "Anonymous PL/SQL block", dialect: "oracle", builtIn: true });
    for (const exposed of [false, true]) {
      h.rpc.mockImplementation(async (m) => (m === "snippet.list" ? [...LIB, ora] : m === "system.engines" ? engines(exposed) : undefined));
      useSnippets.setState({ snippets: [], loaded: false, error: undefined, failedAt: undefined });
      render(<SnippetsPanel close={vi.fn()} />);
      fireEvent.change(screen.getByLabelText("Dialect"), { target: { value: "all" } });
      expect(await screen.findByText("audit")).toBeTruthy();
      await waitFor(() => expect(h.rpc).toHaveBeenCalledWith("system.engines"));
      const values = () => [...screen.getByLabelText("Dialect").querySelectorAll("option")].map((o) => o.getAttribute("value"));
      if (exposed) {
        expect(await screen.findByText("plblock")).toBeTruthy();
        expect(values()).toContain("oracle");
      } else {
        await waitFor(() => expect(values()).not.toContain("oracle"));
        expect(screen.queryByText("plblock")).toBeNull();
        expect(values()).toEqual(expect.arrayContaining(["all", "tsql", "pg", "sqlite", "mySql", "duckDb"]));
      }
      cleanup();
      h.rpc.mockReset();
    }
    // Editing a snippet of a hidden dialect still shows its dialect.
    h.rpc.mockImplementation(async (m) => (m === "system.engines" ? engines(false) : undefined));
    render(<SnippetEditor snippet={blankSnippet({ prefix: "x", dialect: "oracle" })} close={() => {}} />);
    await waitFor(() => expect(h.rpc).toHaveBeenCalledWith("system.engines"));
    expect([...screen.getByLabelText("Snippet dialect").querySelectorAll("option")].map((o) => o.getAttribute("value"))).toContain("oracle");
  });
});

describe("snippet editor", () => {
  it("edits the body in the borrowed shared editor, saves, and gives the editor back", async () => {
    h.rpc.mockImplementation(async (m, p: any) => (m === "snippet.save" ? { ...p.snippet, id: "new1" } : undefined));
    const close = vi.fn();
    const { unmount } = render(<SnippetEditor snippet={blankSnippet({ dialect: "pg", body: "SELECT \\$1;" })} close={close} />);
    expect(h.borrowed.language).toBe("pgsql");
    expect(h.borrowed.container).toBe(screen.getByTestId("snippet-body"));
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Count rows" } });
    fireEvent.change(screen.getByLabelText("Prefix"), { target: { value: " cnt " } });
    fireEvent.change(screen.getByLabelText("Tags"), { target: { value: "stats, , quick" } });
    h.borrowed.value = "SELECT count(*) FROM ${1:t};";
    fireEvent.click(screen.getByText("Save"));
    await waitFor(() => expect(close).toHaveBeenCalled());
    expect(h.rpc).toHaveBeenCalledWith("snippet.save", {
      snippet: expect.objectContaining({ id: "", title: "Count rows", prefix: "cnt", dialect: "pg", body: "SELECT count(*) FROM ${1:t};", tags: ["stats", "quick"] }),
    });
    expect(useUi.getState().toasts.at(-1)?.text).toContain("cnt");
    unmount();
    expect(h.borrowed.release).toHaveBeenCalledTimes(1);
  });

  it("shows saving feedback, prevents a duplicate save, and permits retry after failure", async () => {
    let reject!: (error: Error) => void;
    // The editor also asks which engines are offered (D4 S12); only the save is held open.
    h.rpc.mockImplementation((m) => (m === "system.engines" ? Promise.resolve(undefined) : new Promise((_, fail) => { reject = fail; })));
    render(<SnippetEditor snippet={blankSnippet({ title: "x", prefix: "x", body: "SELECT 1;" })} close={() => {}} />);
    const save = screen.getByRole("button", { name: "Save" });
    fireEvent.click(save);
    expect(save.getAttribute("aria-busy")).toBe("true");
    expect(save.textContent).toContain("Saving…");
    fireEvent.click(save);
    expect(h.rpc.mock.calls.filter((c) => c[0] === "snippet.save")).toHaveLength(1);
    reject(new Error("Could not save"));
    await screen.findByRole("alert");
    expect((save as HTMLButtonElement).disabled).toBe(false);
    expect(save.textContent).toBe("Save");
  });

  it("shows a duplicate prefix inline next to the prefix and keeps the dialog open", async () => {
    h.rpc.mockRejectedValue(new FakeRpcError("The prefix 'audit' is already used by \"Audit columns\".", -32001, "snippetPrefixTaken", { field: "prefix" }));
    const close = vi.fn();
    render(<SnippetEditor snippet={blankSnippet({ title: "x", prefix: "audit", body: "SELECT 1;" })} close={close} />);
    fireEvent.click(screen.getByText("Save"));
    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("already used");
    expect(screen.getByLabelText("Prefix").getAttribute("aria-invalid")).toBe("true");
    expect(screen.getByLabelText("Title").getAttribute("aria-invalid")).toBe("false");
    expect(close).not.toHaveBeenCalled();
  });

  it("shows an error without a field below the form", async () => {
    h.rpc.mockRejectedValue(new Error("The library holds at most 5,000 snippets"));
    render(<SnippetEditor snippet={blankSnippet({ title: "x", prefix: "p", body: "b" })} close={vi.fn()} />);
    fireEvent.click(screen.getByText("Save"));
    expect((await screen.findByRole("alert")).textContent).toContain("5,000");
  });
});

describe("snippets panel", () => {
  it("lists the tab dialect's snippets, searches, filters, and inserts with placeholders", async () => {
    h.rpc.mockResolvedValue(LIB);
    const insert = vi.fn();
    h.editor = { getContribution: () => ({ insert }), focus: vi.fn() };
    const close = vi.fn();
    render(<SnippetsPanel close={close} />);
    await screen.findByText("selt");
    // The active tab is SQL Server: PostgreSQL-only snippets are hidden until "All dialects".
    expect(screen.queryByText("pgonly")).toBeNull();
    fireEvent.change(screen.getByLabelText("Dialect"), { target: { value: "all" } });
    expect(screen.getByText("pgonly")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Search snippets"), { target: { value: "ddl" } });
    expect(within(screen.getByRole("listbox", { name: "Snippet list" })).getAllByRole("option").map((o) => o.textContent)).toEqual([expect.stringContaining("audit")]);
    fireEvent.click(screen.getByText("Insert"));
    expect(insert).toHaveBeenCalledWith("SELECT 1;");
    expect(close).toHaveBeenCalled();
  });

  it("built-ins can only be duplicated; user snippets can be edited and deleted", async () => {
    h.rpc.mockImplementation(async (m) => (m === "snippet.list" ? LIB : undefined));
    render(<SnippetsPanel close={vi.fn()} />);
    fireEvent.click(await screen.findByText("selt"));
    expect(screen.queryByText("Edit…")).toBeNull();
    fireEvent.click(screen.getByText("Duplicate…"));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "snippetEdit", snippet: { id: "", builtIn: false, prefix: "selt_copy" } });
    fireEvent.click(screen.getByText("audit"));
    fireEvent.click(screen.getByText("Edit…"));
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "snippetEdit", snippet: { id: "u1" } });
    // Delete asks first; cancelling keeps the snippet.
    fireEvent.click(screen.getByText("Delete"));
    let confirm = useUi.getState().dialogs.at(-1) as { kind: string; danger?: boolean; resolve: (ok: boolean) => void };
    expect(confirm).toMatchObject({ kind: "confirm", danger: true, title: "Delete snippet “audit”?" });
    confirm.resolve(false);
    await Promise.resolve();
    expect(h.rpc).not.toHaveBeenCalledWith("snippet.delete", expect.anything());
    fireEvent.click(screen.getByText("Delete"));
    confirm = useUi.getState().dialogs.at(-1) as typeof confirm;
    confirm.resolve(true);
    await waitFor(() => expect(h.rpc).toHaveBeenCalledWith("snippet.delete", { id: "u1" }));
  });

  it("reports a library that cannot be loaded, and retries at once when opened again", async () => {
    h.rpc.mockRejectedValue(new Error("sidecar down"));
    render(<SnippetsPanel close={vi.fn()} />);
    expect(await screen.findByText("sidecar down")).toBeTruthy();
    cleanup();
    h.rpc.mockResolvedValue(LIB);
    render(<SnippetsPanel close={vi.fn()} />);
    expect(await screen.findByText("audit")).toBeTruthy();
    expect(h.rpc.mock.calls.filter((c) => c[0] === "snippet.list")).toHaveLength(2); // the panel also asks which engines are offered
  });
});

describe("snippet actions", () => {
  it("save selection opens the editor with the selection, the tab's dialect, and literal $ escaped", () => {
    useTabs.setState({ activeId: "t1", tabs: { t1: { id: "t1", profileId: "p-pg" } as never } } as never);
    const sel = { isEmpty: () => false };
    h.editor = { getModel: () => ({ getValueInRange: () => "DO $$ BEGIN END $$;", getValue: () => "all" }), getSelection: () => sel };
    saveSelectionAsSnippet();
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "snippetEdit", snippet: { id: "", dialect: "pg", body: "DO \\$\\$ BEGIN END \\$\\$;" } });
  });

  it("escaped selections round-trip through Monaco's real snippet parser exactly", () => {
    // Regression: only $ was escaped, so backslashes vanished and \\$1 became a placeholder.
    const samples = [
      "SELECT 'C:\\temp\\x' AS p, '\\$1' AS q, $1, '}' AS b;",
      "DO $fn$ BEGIN RAISE NOTICE '%', '${x}'; END $fn$;",
      "SELECT E'a\\\\b\\}' , '\\' ;",
    ];
    for (const sql of samples) expect(new SnippetParser().parse(escapeSnippetText(sql)).toString()).toBe(sql);
  });

  it("save selection with nothing to save, or insert without a tab, says so", () => {
    h.editor = { getModel: () => ({ getValueInRange: () => "", getValue: () => "  " }), getSelection: () => null };
    saveSelectionAsSnippet();
    expect(useUi.getState().dialogs).toEqual([]);
    expect(useUi.getState().toasts.at(-1)?.text).toContain("Select the SQL");
    h.editor = undefined;
    expect(insertSnippet(LIB[0])).toBe(false);
    expect(useUi.getState().toasts.at(-1)?.text).toContain("Open a query tab");
  });

  it("insert falls back to a plain edit when the snippet controller is missing", () => {
    const executeEdits = vi.fn();
    h.editor = { getContribution: () => null, getSelection: () => ({ r: 1 }), executeEdits, focus: vi.fn() };
    expect(insertSnippet(LIB[2])).toBe(true);
    expect(executeEdits).toHaveBeenCalledWith("snippet", [{ range: { r: 1 }, text: "SELECT 1;", forceMoveMarkers: true }]);
  });
});

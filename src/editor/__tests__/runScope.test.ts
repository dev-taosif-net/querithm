import { beforeEach, describe, expect, it, vi } from "vitest";
import { FakeEditor, FakeModel, FakeRange } from "./fakeMonaco";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";

const h = vi.hoisted(() => ({ editor: undefined as unknown, model: undefined as unknown }));
vi.mock("../monaco", async () => ({ monaco: (await import("./fakeMonaco")).fake.monaco }));
vi.mock("../editors", () => ({ editorForTab: () => h.editor, getModel: () => h.model }));

import { clearRunScope, runScopeRange, showRunScope } from "../runScope";

const SCRIPT = "SELECT 1;\n\nDELETE FROM t\nWHERE id < 5;\n";

describe("runScopeRange: what a Run control executes", () => {
  it("a selection wins in either mode (as runQuery)", () => {
    for (const mode of ["auto", "statement"] as const) expect(runScopeRange(SCRIPT, { start: 0, end: 6 }, 0, mode, "tsql")).toEqual({ start: 0, end: 6 });
  });

  it("auto without a selection is the whole script, the DELETE included", () => {
    expect(runScopeRange(SCRIPT, null, 0, "auto", "tsql")).toEqual({ start: 0, end: SCRIPT.length });
  });

  it.each(["tsql", "pg"] as const)("statement mode is the statement at the cursor from the %s tokens", (dialect) => {
    const r = runScopeRange(SCRIPT, null, SCRIPT.indexOf("FROM"), "statement", dialect)!;
    expect(SCRIPT.slice(r.start, r.end)).toContain("DELETE FROM t");
    expect(SCRIPT.slice(r.start, r.end)).not.toContain("SELECT 1");
  });

  it("a quote or comment marker inside a string does not split the statement", () => {
    const text = "SELECT ';--' AS x;\nSELECT 2;";
    const r = runScopeRange(text, null, 3, "statement", "tsql")!;
    expect(text.slice(r.start, r.end)).toContain("';--' AS x");
    expect(text.slice(r.start, r.end)).not.toContain("SELECT 2");
  });

  it("nothing to run is null (empty or blank script)", () => {
    expect(runScopeRange("", null, 0, "auto", "tsql")).toBeNull();
    expect(runScopeRange("  \n ", null, 1, "statement", "pg")).toBeNull();
  });
});

describe("showRunScope / clearRunScope", () => {
  let ed: FakeEditor;
  let model: FakeModel;
  beforeEach(() => {
    model = new FakeModel(SCRIPT, "tsql", { toString: () => "m" });
    ed = new FakeEditor(null, {});
    ed.model = model;
    h.editor = ed;
    h.model = model;
    useProfiles.setState({ profiles: [{ id: "p", name: "crm", engine: "mssql", environment: "dev" } as never], groups: [] });
    useTabs.setState({ tabs: { t1: { id: "t1", profileId: "p" } as never }, order: ["t1"], activeId: "t1" });
    clearRunScope();
  });
  type Deco = { range: FakeRange; options: { className: string; isWholeLine: boolean } };
  const decorations = () => (ed.createDecorationsCollection.mock.calls.at(-1) as unknown as [Deco[]])[0];

  it("marks the whole script as whole lines, without the empty line its end touches", () => {
    showRunScope("t1", "auto");
    const [d] = decorations();
    expect([d.range.startLineNumber, d.range.endLineNumber, d.options.isWholeLine, d.options.className]).toEqual([1, 4, true, "q-run-scope"]);
  });

  it("marks a selection exactly", () => {
    ed.selection = new FakeRange(3, 1, 3, 7);
    showRunScope("t1", "statement");
    const [d] = decorations();
    expect([d.range.startLineNumber, d.range.startColumn, d.range.endLineNumber, d.range.endColumn, d.options.isWholeLine]).toEqual([3, 1, 3, 7, false]);
  });

  it("uses the environment color on a prod connection", () => {
    useProfiles.setState({ profiles: [{ id: "p", name: "crm", engine: "mssql", environment: "prod" } as never] });
    showRunScope("t1", "auto");
    expect(decorations()[0].options.className).toBe("q-run-scope-prod");
  });

  it("clearing removes the marks; a new preview replaces the previous one", () => {
    showRunScope("t1", "auto");
    const first = ed.createDecorationsCollection.mock.results[0].value;
    showRunScope("t1", "statement");
    expect(first.clear).toHaveBeenCalledTimes(1);
    const second = ed.createDecorationsCollection.mock.results[1].value;
    clearRunScope();
    expect(second.clear).toHaveBeenCalledTimes(1);
  });

  it("does nothing when the tab has no editor, or its editor shows another model", () => {
    h.editor = undefined;
    showRunScope("t1", "auto");
    h.editor = ed;
    ed.model = new FakeModel("x", "tsql", { toString: () => "other" });
    showRunScope("t1", "auto");
    expect(ed.createDecorationsCollection).not.toHaveBeenCalled();
  });
});

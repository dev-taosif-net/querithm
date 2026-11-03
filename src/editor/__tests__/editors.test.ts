// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useSettings } from "../../store/settings";

vi.mock("../monaco", async () => {
  const { fake } = await import("./fakeMonaco");
  return { initMonaco: vi.fn(), monaco: fake.monaco };
});

const { fake } = await import("./fakeMonaco");
const ed = await import("../editors");

const tid = (() => { let n = 0; return () => `tab${++n}`; })();

describe("editor model registry", () => {
  it("maps engines to Monaco languages", () => {
    expect(ed.languageFor("pg")).toBe("pgsql");
    expect(ed.languageFor("mssql")).toBe("tsql");
    expect(ed.languageFor(undefined)).toBe("tsql");
  });

  it("creates one model per tab and returns the existing one on a second call", () => {
    const t = tid();
    const m = ed.createModel(t, "select 1", "tsql");
    expect(ed.getModel(t)).toBe(m);
    expect(ed.createModel(t, "other", "tsql")).toBe(m);
    expect(m.getValue()).toBe("select 1");
    expect(ed.tabIdOfModel(m)).toBe(t);
    expect(ed.tabIdOfModel(null)).toBeUndefined();
  });

  it("notifies content listeners until they unsubscribe", () => {
    const t = tid();
    const m = ed.createModel(t, "a", "tsql") as any;
    const seen: string[] = [];
    const off = ed.onModelContent((id) => seen.push(id));
    m.setValue("b");
    off();
    m.setValue("c");
    expect(seen).toEqual([t]);
  });

  it("disposeModel tells dispose listeners the model's key once; an unknown key tells nobody (M12: lang.forget)", () => {
    const t = tid();
    ed.createModel(t, "a", "tsql");
    const cell = `${t}:c1`;
    ed.createModel(cell, "b", "tsql", t);
    const seen: string[] = [];
    const off = ed.onModelDisposed((key) => seen.push(key));
    ed.disposeModel(cell);
    ed.disposeModel(t);
    ed.disposeModel(t);
    ed.disposeModel("never-made");
    off();
    expect(seen).toEqual([cell, t]);
  });

  it("switches the language only when it differs (T-SQL vs PostgreSQL tabs)", () => {
    const t = tid();
    const m = ed.createModel(t, "x", "tsql");
    fake.monaco.editor.setModelLanguage.mockClear();
    ed.setModelLanguage(t, "tsql");
    expect(fake.monaco.editor.setModelLanguage).not.toHaveBeenCalled();
    ed.setModelLanguage(t, "pgsql");
    expect(m.getLanguageId()).toBe("pgsql");
    ed.setModelLanguage("missing", "pgsql");
    expect(fake.monaco.editor.setModelLanguage).toHaveBeenCalledTimes(1);
  });

  it("disposeModel detaches it from editors, forgets it and ignores unknown tabs", () => {
    const t = tid();
    const m = ed.createModel(t, "x", "tsql") as any;
    const e = ed.attachEditor("gd", document.createElement("div"), t, false) as any;
    expect(e.getModel()).toBe(m);
    ed.disposeModel(t);
    expect(m.disposed).toBe(true);
    expect(e.getModel()).toBeNull();
    expect(ed.getModel(t)).toBeUndefined();
    expect(ed.tabIdOfModel(m)).toBeUndefined();
    expect(() => ed.disposeModel(t)).not.toThrow();
    ed.disposeGroupEditor("gd");
  });
});

describe("shared group editors", () => {
  beforeEach(() => {
    useSettings.setState({ editorFontSize: 14, minimap: true, wordWrap: true, reducedDecorations: false });
  });

  it("creates one editor per group, reuses it, and switches model while remembering view state", () => {
    const a = tid(), b = tid();
    const ma = ed.createModel(a, "A", "tsql");
    const mb = ed.createModel(b, "B", "pgsql");
    const container = document.createElement("div");
    const created: unknown[] = [];
    const off = ed.onEditorCreated((e) => created.push(e));
    const e1 = ed.attachEditor("g1", container, a, false) as any;
    expect(created).toContain(e1);
    expect(e1.getModel()).toBe(ma);
    expect(container.contains(e1.host)).toBe(true);
    expect(e1.options.readOnly).toBe(false);
    expect(e1.layout).toHaveBeenCalledTimes(1);
    const e2 = ed.attachEditor("g1", container, b, true) as any;
    expect(e2).toBe(e1);
    expect(e1.getModel()).toBe(mb);
    expect(e1.options.readOnly).toBe(true);
    // back to A restores the saved view state
    ed.attachEditor("g1", container, a, false);
    expect(e1.restoreViewState).toHaveBeenCalledWith({ saved: ma.uri.toString() });
    expect(ed.editorForTab(a)).toBe(e1);
    expect(ed.editorForTab(b)).toBeUndefined();
    // re-attaching the same tab does not re-layout
    e1.layout.mockClear();
    ed.attachEditor("g1", container, a, false);
    expect(e1.layout).not.toHaveBeenCalled();
    // late subscribers hear about existing editors
    const late: unknown[] = [];
    const off2 = ed.onEditorCreated((x) => late.push(x));
    expect(late).toContain(e1);
    off(); off2();
    expect(ed.allEditors()).toContain(e1);
    ed.disposeGroupEditor("g1");
    expect(e1.disposed).toBe(true);
    expect(ed.allEditors()).not.toContain(e1);
    expect(() => ed.disposeGroupEditor("g1")).not.toThrow();
  });

  it("moves the host between containers and tolerates a tab without a model", () => {
    const c1 = document.createElement("div"), c2 = document.createElement("div");
    const e = ed.attachEditor("g2", c1, "nomodel", false) as any;
    expect(e.getModel()).toBeNull();
    ed.attachEditor("g2", c2, "nomodel", false);
    expect(c2.contains(e.host)).toBe(true);
    ed.disposeGroupEditor("g2");
  });

  it("swallows middle-click so a Linux primary-selection paste cannot insert stray text", () => {
    const c = document.createElement("div");
    const e = ed.attachEditor("g3", c, "x", false) as any;
    const mid = new MouseEvent("mouseup", { button: 1, cancelable: true, bubbles: true });
    e.host.dispatchEvent(mid);
    expect(mid.defaultPrevented).toBe(true);
    const left = new MouseEvent("mouseup", { button: 0, cancelable: true, bubbles: true });
    e.host.dispatchEvent(left);
    expect(left.defaultPrevented).toBe(false);
    ed.disposeGroupEditor("g3");
  });

  it("builds editor options from settings and applies them to all live editors", () => {
    const c = document.createElement("div");
    const e = ed.attachEditor("g4", c, "x", false) as any;
    expect(e.opts).toMatchObject({ fontSize: 14, wordWrap: "on", renderLineHighlight: "all", occurrencesHighlight: "singleFile" });
    expect(e.opts.minimap.enabled).toBe(true);
    expect(e.opts.find.addExtraSpaceOnTop).toBe(false);
    expect(e.opts.suggest.snippetsPreventQuickSuggestions).toBe(false);
    useSettings.setState({ editorFontSize: 20, minimap: true, wordWrap: false, reducedDecorations: true });
    ed.refreshEditorOptions();
    expect(e.options).toMatchObject({ fontSize: 20, lineHeight: 34, wordWrap: "off", renderLineHighlight: "none", occurrencesHighlight: "off" });
    expect(e.options.minimap.enabled).toBe(false);
    expect(e.options.stickyScroll.enabled).toBe(false);
    ed.disposeGroupEditor("g4");
  });

  it("focusedEditor returns the editor with text focus, if any", () => {
    const e = ed.attachEditor("g5", document.createElement("div"), "x", false) as any;
    expect(ed.focusedEditor()).toBeUndefined();
    e.focus = true;
    expect(ed.focusedEditor()).toBe(e);
    ed.disposeGroupEditor("g5");
  });
});

describe("executableText", () => {
  it("is empty for an unknown tab", () => {
    expect(ed.executableText("unknown")).toEqual({ sql: "", lineOffset: 1, isSelection: false });
  });
  it("runs the whole script without a selection and only the selection with one (line offset maps errors back)", () => {
    const t = tid();
    const m = ed.createModel(t, "select 1;\nselect 2;\nselect 3;", "tsql");
    expect(ed.executableText(t)).toEqual({ sql: "select 1;\nselect 2;\nselect 3;", lineOffset: 1, isSelection: false });
    const e = ed.attachEditor("g6", document.createElement("div"), t, false) as any;
    e.selection = new (fake.monaco.Range as any)(1, 1, 1, 1);
    expect(ed.executableText(t).isSelection).toBe(false);
    e.selection = new (fake.monaco.Range as any)(2, 1, 2, 10);
    expect(ed.executableText(t)).toEqual({ sql: "select 2;", lineOffset: 2, isSelection: true });
    void m;
    ed.disposeGroupEditor("g6");
  });
});

describe("borrowEditor (snippet dialog)", () => {
  it("lends the shared editor with a temporary model and gives it back as it was", () => {
    const t = tid();
    const tabModel = ed.createModel(t, "select 1", "tsql") as any;
    const tabSlot = document.createElement("div");
    const e = ed.attachEditor("gb", tabSlot, t, true) as any;
    const created = fake.created.length;
    const dialog = document.createElement("div");
    const b = ed.borrowEditor(dialog, "SELECT ${1:x};", "pgsql");
    // The same instance: no second Monaco editor.
    expect(fake.created.length).toBe(created);
    expect(b.editor).toBe(e);
    expect(dialog.contains(e.host)).toBe(true);
    const temp = e.getModel();
    expect(temp).not.toBe(tabModel);
    expect(temp.getValue()).toBe("SELECT ${1:x};");
    expect(temp.lang).toBe("pgsql");
    expect(e.options.readOnly).toBe(false); // the snippet body is editable even when the tab is read-only
    temp.setValue("SELECT 2;");
    expect(b.getValue()).toBe("SELECT 2;");
    b.release();
    expect(tabSlot.contains(e.host)).toBe(true);
    expect(e.getModel()).toBe(tabModel);
    expect(e.restoreViewState).toHaveBeenCalledWith({ saved: tabModel.uri.toString() });
    expect(e.options.readOnly).toBe(true);
    expect(temp.disposed).toBe(true);
    // The temporary model never became a tab.
    expect(ed.tabIdOfModel(temp)).toBeUndefined();
    b.release(); // idempotent
    expect(e.getModel()).toBe(tabModel);
    ed.disposeGroupEditor("gb");
  });

  it("creates the editor when no tab has one yet, and the first tab's group takes it over", () => {
    for (const id of ["gd", "g1", "gb", "g6"]) ed.disposeGroupEditor(id);
    const before = fake.created.length;
    const dialog = document.createElement("div");
    const b = ed.borrowEditor(dialog, "x", "tsql");
    expect(fake.created.length).toBe(before + 1);
    const e = b.editor as any;
    b.release();
    expect(e.host.parentElement).toBeNull();
    expect(e.getModel()).toBeNull();
    expect(ed.allEditors()).toEqual([]); // not a group's editor until a tab attaches
    // A second borrow reuses it rather than creating another.
    ed.borrowEditor(document.createElement("div"), "y", "tsql").release();
    expect(fake.created.length).toBe(before + 1);

    const t = tid();
    ed.createModel(t, "select 1", "tsql");
    expect(ed.attachEditor("gs", document.createElement("div"), t, false)).toBe(e);
    expect(fake.created.length).toBe(before + 1);
    expect(ed.allEditors()).toEqual([e]);
    ed.disposeGroupEditor("gs");
    expect(e.disposed).toBe(true);
  });

  it("disposes the lent editor on release when a tab created its own meanwhile", () => {
    const before = fake.created.length;
    const b = ed.borrowEditor(document.createElement("div"), "x", "tsql");
    const lent = b.editor as any;
    const t = tid();
    ed.createModel(t, "select 1", "tsql");
    const own = ed.attachEditor("gm", document.createElement("div"), t, false) as any;
    expect(own).not.toBe(lent);
    expect(fake.created.length).toBe(before + 2);
    b.release();
    expect(lent.disposed).toBe(true);
    expect(lent.host.parentElement).toBeNull();
    expect(ed.allEditors()).toEqual([own]);
    ed.disposeGroupEditor("gm");
  });
});

describe("models owned by another tab (notebook cells, Phase C3)", () => {
  it("a cell's model is keyed by the cell and owned by its notebook; a tab's own model owns itself", () => {
    const m = ed.createModel("nbX::c1", "select 1", "tsql", "nbX");
    expect(ed.tabIdOfModel(m)).toBe("nbX::c1");
    expect(ed.modelOwner("nbX::c1")).toBe("nbX");
    expect(ed.modelOwner("plainTab")).toBe("plainTab");
    expect(ed.modelOwner(undefined)).toBeUndefined();
    ed.disposeModel("nbX::c1");
    expect(ed.modelOwner("nbX::c1")).toBe("nbX::c1");
  });
  it("the compact (cell) form is swapped in and out on the one shared editor; settings refresh keeps it", () => {
    ed.createModel("q1", "x", "tsql");
    ed.createModel("nbY::c1", "y", "tsql", "nbY");
    const e = ed.attachEditor("gc", document.createElement("div"), "nbY::c1", false, true) as any;
    expect(e.options.minimap).toEqual({ enabled: false });
    expect(e.options.scrollbar).toMatchObject({ vertical: "hidden" });
    ed.refreshEditorOptions();
    expect(e.options.minimap).toEqual({ enabled: false });
    const back = ed.attachEditor("gc", document.createElement("div"), "q1", false) as any;
    expect(back).toBe(e);
    expect(e.options.minimap.enabled).toBe(useSettings.getState().minimap);
  });
});

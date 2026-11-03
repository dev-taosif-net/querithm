import { describe, expect, it, vi } from "vitest";

vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => undefined) }));

import { defaultSettings, type Settings } from "../../store/settings";
import { defaultsOf, defOf, isModified, notRows, pages, searchPages, searchSettings, settingDefs } from "../registry";

const s = (over: Partial<Settings> = {}): Settings => ({ ...defaultSettings, ...over });

describe("settings registry", () => {
  it("every stored setting is a row or deliberately not one, exactly once", () => {
    const shown = settingDefs.flatMap((d) => d.keys);
    expect(new Set(shown).size).toBe(shown.length);
    expect([...shown, ...notRows].sort()).toEqual(Object.keys(defaultSettings).sort());
  });

  it("rows have unique ids, known pages and their defaults inside their own range and choices", () => {
    expect(new Set(settingDefs.map((d) => d.id)).size).toBe(settingDefs.length);
    for (const d of settingDefs) {
      expect(pages.map((p) => p.id)).toContain(d.page);
      expect(d.description.length).toBeGreaterThan(10);
      const v = defaultSettings[d.keys[0]];
      if (d.control.kind === "number") expect(v).toBeGreaterThanOrEqual(d.control.min), expect(v).toBeLessThanOrEqual(d.control.max);
      if (d.control.kind === "segmented") expect(d.control.options.map((o) => o.value)).toContain(v);
      if (d.control.kind === "switch") expect(typeof v).toBe("boolean");
    }
  });

  it("settings that act on SQL Server only say so and name the PostgreSQL alternative", () => {
    const only = settingDefs.filter((d) => d.engine).map((d) => d.id).sort();
    expect(only).toEqual(["leadingCommas", "newlineBeforeJoin", "statisticsIo", "statisticsTime"]);
    for (const id of only) expect(defOf(id).engine!.other).toMatch(/PostgreSQL/);
  });

  it("dependent rows say why they are off", () => {
    expect(defOf("minimap").disabledWhy!(s({ reducedDecorations: true }))).toMatch(/Reduce visual effects/);
    expect(defOf("minimap").disabledWhy!(s())).toBeNull();
    expect(defOf("keywordCaseOnType").disabledWhy!(s({ keywordCase: "preserve" }))).toMatch(/Preserve/);
    expect(defOf("explorerHoverOpen").disabledWhy!(s())).toMatch(/auto hides/);
    expect(defOf("explorerHoverOpen").disabledWhy!(s({ explorerAutoHide: true }))).toBeNull();
  });

  it("risky values carry a warning; safe ones don't", () => {
    const w = defOf("maxRows").warning!;
    expect(w(s({ maxRows: 0 }))).toMatch(/No limit/);
    expect(w(s({ maxRows: 2_000_000 }))).toMatch(/1,000,000/);
    expect(w(s({ maxRows: 1_000_000 }))).toBeNull();
    expect(defOf("autosave").warning!(s({ autosave: false }))).toMatch(/lost/);
    expect(defOf("autoUpdateCheck").warning!(s({ autoUpdateCheck: false }))).toMatch(/fixes/);
    expect(defOf("autoUpdateCheck").warning!(s())).toBeNull();
  });

  it("SQL Server's Preserve keyword case is described honestly", () => {
    expect(defOf("keywordCase").note!(s({ keywordCase: "preserve" }))).toMatch(/Pascal case/);
    expect(defOf("keywordCase").note!(s())).toBeNull();
  });

  it("modified compares with the defaults; the Explorer mode compares as a mode", () => {
    expect(isModified(defOf("wordWrap"), s({ wordWrap: true }))).toBe(true);
    expect(isModified(defOf("wordWrap"), s())).toBe(false);
    expect(isModified(defOf("explorerMode"), s({ explorerVisible: false }))).toBe(true);
    // A closed window keeps its auto-hide flag (Visual Studio); closed reads as one mode either way.
    expect(isModified(defOf("explorerMode"), s({ explorerVisible: false, explorerAutoHide: true }))).toBe(true);
    expect(isModified(defOf("explorerMode"), s({ explorerAutoHide: true }))).toBe(true);
    expect(defaultsOf(defOf("explorerMode"))).toEqual({ explorerVisible: true, explorerAutoHide: false });
  });

  it("search matches every word in labels, descriptions and keywords", () => {
    // Larger text: the whole interface's scale (L26) and the editor's own font size.
    expect(searchSettings("font").map((d) => d.id)).toEqual(["uiScale", "editorFontSize"]);
    expect(searchSettings("zoom").map((d) => d.id)).toEqual(expect.arrayContaining(["uiScale", "editorFontSize"]));
    expect(searchSettings("statistics").map((d) => d.id)).toEqual(["statisticsTime", "statisticsIo"]);
    expect(searchSettings("sql server").map((d) => d.id)).toEqual(expect.arrayContaining(["leadingCommas", "statisticsIo"]));
    expect(searchSettings("dark THEME").map((d) => d.id)).toEqual(["theme", "syntaxTheme"]);
    expect(searchSettings("color blind").map((d) => d.id)).toEqual(["syntaxTheme"]);
    expect(searchSettings("highlighting").map((d) => d.id)).toEqual(["syntaxTheme"]);
    expect(searchSettings("zzz")).toEqual([]);
    expect(searchSettings("  ")).toEqual([]);
  });

  it("search finds pages by title and keywords", () => {
    expect(searchPages("version").map((p) => p.id)).toEqual(["about"]);
    expect(searchPages("Keyboard").map((p) => p.id)).toEqual(["keyboard"]);
    expect(searchPages("")).toEqual([]);
  });
});

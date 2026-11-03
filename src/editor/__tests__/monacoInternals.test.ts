import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { SUGGEST_VISIBLE_KEY, suggestWidgetVisible } from "../monacoInternals";

const editor = (svc: unknown) => ({ _contextKeyService: svc }) as never;

describe("the one Monaco internal Querithm reads (L23)", () => {
  it("reads the suggest widget's context key from the editor's scoped service", () => {
    const asked: string[] = [];
    const svc = (value: unknown) => ({ getContextKeyValue: (k: string) => (asked.push(k), value) });
    expect(suggestWidgetVisible(editor(svc(true)))).toBe(true);
    expect(suggestWidgetVisible(editor(svc(false)))).toBe(false);
    expect(suggestWidgetVisible(editor(svc("true")))).toBe(false);
    expect(asked).toEqual([SUGGEST_VISIBLE_KEY, SUGGEST_VISIBLE_KEY, SUGGEST_VISIBLE_KEY]);
  });

  it("answers false when Monaco's internals are not there or throw", () => {
    expect(suggestWidgetVisible({} as never)).toBe(false);
    expect(suggestWidgetVisible(editor({}))).toBe(false);
    expect(suggestWidgetVisible(editor({ getContextKeyValue: () => { throw new Error("disposed"); } }))).toBe(false);
  });

  it("the bundled Monaco still has the field and the key (a rename on upgrade fails here first)", () => {
    const root = new URL("../../../node_modules/monaco-editor/esm/vs/", import.meta.url);
    const widget = readFileSync(new URL("editor/browser/widget/codeEditor/codeEditorWidget.js", root), "utf8");
    expect(widget).toContain("this._contextKeyService = ");
    const keys = readFileSync(new URL("platform/history/browser/contextScopedHistoryWidget.js", root), "utf8");
    expect(keys).toContain(`new RawContextKey('${SUGGEST_VISIBLE_KEY}'`);
  });
});

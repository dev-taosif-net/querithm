// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { installTextDrop } from "../textDrop";

type Pos = { lineNumber: number; column: number };
type Range = { startLineNumber: number; startColumn: number; endLineNumber: number; endColumn: number };

/** One-model editor whose executeEdits really changes the text; every client point maps to `at`. */
function fakeEditor(text: string, at: Pos | null, readOnly = false) {
  const model = {
    text,
    getOffsetAt(p: Pos) {
      const lines = this.text.split("\n");
      let off = 0;
      for (let i = 0; i < p.lineNumber - 1; i++) off += lines[i].length + 1;
      return off + p.column - 1;
    },
    getPositionAt(offset: number): Pos {
      const before = this.text.slice(0, offset).split("\n");
      return { lineNumber: before.length, column: before[before.length - 1].length + 1 };
    },
  };
  const marker = { set: vi.fn(), clear: vi.fn() };
  const editor = {
    model,
    marker,
    position: null as Pos | null,
    focused: false,
    getModel: () => model,
    getRawOptions: () => ({ readOnly }),
    getTargetAtClientPoint: () => (at ? { position: at } : null),
    createDecorationsCollection: () => marker,
    pushUndoStop: vi.fn(),
    executeEdits: vi.fn((_source: string, edits: Array<{ range: Range; text: string }>) => {
      for (const e of edits) {
        const start = model.getOffsetAt({ lineNumber: e.range.startLineNumber, column: e.range.startColumn });
        const end = model.getOffsetAt({ lineNumber: e.range.endLineNumber, column: e.range.endColumn });
        model.text = model.text.slice(0, start) + e.text + model.text.slice(end);
      }
      return true;
    }),
    setPosition(p: Pos) { editor.position = p; },
    focus() { editor.focused = true; },
  };
  return editor;
}

function dragEvent(type: string, data: Record<string, string>) {
  const e = new Event(type, { bubbles: true, cancelable: true }) as DragEvent;
  Object.defineProperty(e, "dataTransfer", {
    value: { types: Object.keys(data), getData: (k: string) => data[k] ?? "", effectAllowed: "all", dropEffect: "none" },
  });
  return e;
}

describe("dropping text into the editor", () => {
  // Regression: Monaco's own drop went through a snippet the standalone editor never expands, so an Object Explorer
  // table arrived as `HrmService.EmployeeJd$0`.
  it("inserts the dropped name as it is, with no `$0` and no escaping, and puts the cursor after it", () => {
    const ed = fakeEditor("SELECT * FROM ", { lineNumber: 1, column: 15 });
    const host = document.createElement("div");
    installTextDrop(ed as never, host);
    const drop = dragEvent("drop", { "text/plain": "HrmService.EmployeeJd" });
    host.dispatchEvent(drop);
    expect(ed.model.text).toBe("SELECT * FROM HrmService.EmployeeJd");
    expect(drop.defaultPrevented).toBe(true);
    expect(ed.position).toEqual({ lineNumber: 1, column: 36 });
    expect(ed.focused).toBe(true);
  });

  it("keeps `$`, `}` and `\\` in a name (the snippet path turned them into `\\$`…)", () => {
    const ed = fakeEditor("", { lineNumber: 1, column: 1 });
    const host = document.createElement("div");
    installTextDrop(ed as never, host);
    host.dispatchEvent(dragEvent("drop", { "text/plain": "[a$b}\\c]" }));
    expect(ed.model.text).toBe("[a$b}\\c]");
  });

  it("drops a multi-line column list mid-line and ends the cursor after its last line", () => {
    const ed = fakeEditor("SELECT  FROM t", { lineNumber: 1, column: 8 });
    const host = document.createElement("div");
    installTextDrop(ed as never, host);
    host.dispatchEvent(dragEvent("drop", { "text/plain": "a,\n  b" }));
    expect(ed.model.text).toBe("SELECT a,\n  b FROM t");
    expect(ed.position).toEqual({ lineNumber: 2, column: 4 });
  });

  it("accepts the drag and marks the drop point while text is dragged over, and clears the mark on leave", () => {
    const ed = fakeEditor("x", { lineNumber: 1, column: 2 });
    const host = document.createElement("div");
    installTextDrop(ed as never, host);
    const over = dragEvent("dragover", { "text/plain": "t" });
    host.dispatchEvent(over);
    expect(over.defaultPrevented).toBe(true);
    expect(over.dataTransfer!.dropEffect).toBe("copy");
    expect(ed.marker.set).toHaveBeenCalledWith([expect.objectContaining({ range: { startLineNumber: 1, startColumn: 2, endLineNumber: 1, endColumn: 2 } })]);
    host.dispatchEvent(dragEvent("dragleave", {}));
    expect(ed.marker.clear).toHaveBeenCalled();
  });

  it("does nothing in a read-only editor (the drop is not accepted)", () => {
    const ed = fakeEditor("x", { lineNumber: 1, column: 1 }, true);
    const host = document.createElement("div");
    installTextDrop(ed as never, host);
    const over = dragEvent("dragover", { "text/plain": "t" });
    host.dispatchEvent(over);
    host.dispatchEvent(dragEvent("drop", { "text/plain": "t" }));
    expect(over.defaultPrevented).toBe(false);
    expect(ed.executeEdits).not.toHaveBeenCalled();
    expect(ed.model.text).toBe("x");
  });

  it("ignores drags that carry no text (files) and drops outside the text area", () => {
    const ed = fakeEditor("x", null);
    const host = document.createElement("div");
    installTextDrop(ed as never, host);
    const files = dragEvent("dragover", { Files: "" });
    host.dispatchEvent(files);
    expect(files.defaultPrevented).toBe(false);
    host.dispatchEvent(dragEvent("drop", { "text/plain": "t" }));
    expect(ed.executeEdits).not.toHaveBeenCalled();
  });

  it("stops listening once disposed", () => {
    const ed = fakeEditor("", { lineNumber: 1, column: 1 });
    const host = document.createElement("div");
    installTextDrop(ed as never, host)();
    host.dispatchEvent(dragEvent("drop", { "text/plain": "t" }));
    expect(ed.model.text).toBe("");
  });
});

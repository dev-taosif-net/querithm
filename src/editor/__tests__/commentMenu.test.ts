import { describe, expect, it } from "vitest";
import { FakeEditor, FakeModel, FakeRange } from "./fakeMonaco";
import { addCommentMenu, isApplyingCommentEdit, runCommentInEditor } from "../commentMenu";

describe("comment items in the editor's context menu", () => {
  it("adds all nine SQL comment commands to the edit group, in order, only for writable editors", () => {
    const ed = new FakeEditor(null, {});
    addCommentMenu(ed as never, { add: [101], remove: [102] });
    expect(ed.actions.map((a) => [a.id, a.label, a.contextMenuGroupId, a.precondition])).toEqual([
      ["querithm.toggleComment", "Toggle Line Comment", "1_modification", "!editorReadonly"],
      ["querithm.comment", "Comment Lines", "1_modification", "!editorReadonly"],
      ["querithm.uncomment", "Uncomment Lines", "1_modification", "!editorReadonly"],
      ["querithm.blockComment", "Toggle Block Comment", "1_modification", "!editorReadonly"],
      ["querithm.statementComment", "Toggle Statement Comment", "1_modification", "!editorReadonly"],
      ["querithm.convertComment", "Convert Comment Style (-- ↔ /* */)", "1_modification", "!editorReadonly"],
      ["querithm.sectionComment", "Insert Section Header", "1_modification", "!editorReadonly"],
      ["querithm.docHeaderComment", "Insert Doc Block Header", "1_modification", "!editorReadonly"],
      ["querithm.stripComments", "Remove All Comments", "1_modification", "!editorReadonly"],
    ]);
    expect((ed.actions.find((a) => a.id === "querithm.comment") as { keybindings?: number[] }).keybindings).toEqual([101]);
    expect((ed.actions.find((a) => a.id === "querithm.uncomment") as { keybindings?: number[] }).keybindings).toEqual([102]);
  });

  it("runs the smart comment engine directly on the clicked editor across engines", () => {
    const ed = new FakeEditor(null, {});
    ed.setModel(new FakeModel("SELECT 1 -- note", "mysql", { toString: () => "u" }));
    addCommentMenu(ed as never);
    ed.actions.find((a) => a.id === "querithm.toggleComment")!.run(ed);
    expect(ed.edits).toHaveLength(1);
    expect(ed.edits[0].source).toBe("querithm.comment.toggle");
    expect(ed.edits[0].edits[0].text).toBe("-- ");

    ed.actions.find((a) => a.id === "querithm.stripComments")!.run(ed);
    expect(ed.edits).toHaveLength(2);
    expect(ed.edits[1].source).toBe("querithm.comment.strip");
  });

  it("coalesces large multi-line edits into a single Monaco range edit and flags isApplyingCommentEdit during execution", () => {
    const sql = Array.from({ length: 60 }, (_, i) => `SELECT ${i + 1};`).join("\n");
    const ed = new FakeEditor(null, {});
    const model = new FakeModel(sql, "tsql", { toString: () => "bulk" });
    ed.setModel(model);
    ed.selection = new FakeRange(1, 1, 60, 11);

    let sawFlag = false;
    const origExec = ed.executeEdits.bind(ed);
    ed.executeEdits = (source, edits) => {
      sawFlag = isApplyingCommentEdit();
      return origExec(source, edits);
    };

    runCommentInEditor(ed as never, "toggle");
    expect(sawFlag).toBe(true);
    expect(isApplyingCommentEdit()).toBe(false);
    expect(ed.edits).toHaveLength(1);
    expect(ed.edits[0].edits).toHaveLength(1);
    expect(ed.edits[0].edits[0].text.split("\n")).toHaveLength(60);
    expect(ed.edits[0].edits[0].text.startsWith("-- SELECT 1;\n-- SELECT 2;")).toBe(true);
  });
});

import { describe, expect, it } from "vitest";
import { FakeEditor } from "./fakeMonaco";
import { addCommentMenu, COMMENT_ACTIONS } from "../commentMenu";

describe("comment items in the editor's context menu", () => {
  it("adds the four comment commands to the edit group, in order, only for writable editors", () => {
    const ed = new FakeEditor(null, {});
    addCommentMenu(ed as never);
    expect(ed.actions.map((a) => [a.label, a.contextMenuGroupId, a.precondition])).toEqual([
      ["Toggle Line Comment", "1_modification", "!editorReadonly"],
      ["Comment Lines", "1_modification", "!editorReadonly"],
      ["Uncomment Lines", "1_modification", "!editorReadonly"],
      ["Toggle Block Comment", "1_modification", "!editorReadonly"],
    ]);
  });

  it("each item runs Monaco's own action, so the language's comment tokens (-- and /* */) apply on every engine", () => {
    const ed = new FakeEditor(null, {});
    addCommentMenu(ed as never);
    ed.actions.forEach((a) => a.run(ed));
    expect(ed.triggered).toEqual(Object.values(COMMENT_ACTIONS).map((id) => `querithm:${id}`));
  });
});

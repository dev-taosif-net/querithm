// Comment commands in the editor's right-click menu. The top bar has no comment buttons: they edit text, so they live
// where the text is, next to their shortcuts (Ctrl+/, Ctrl+K Ctrl+C / Ctrl+K Ctrl+U, Shift+Alt+A) and the palette.
import type { monaco } from "./monaco";

// Monaco's built-in comment actions; they read `comments` from the SQL language configuration (`--`, `/* */`).
export const COMMENT_ACTIONS = {
  toggle: "editor.action.commentLine",
  add: "editor.action.addCommentLine",
  remove: "editor.action.removeCommentLine",
  block: "editor.action.blockComment",
} as const;

const MENU: { id: string; label: string; action: keyof typeof COMMENT_ACTIONS }[] = [
  { id: "querithm.toggleComment", label: "Toggle Line Comment", action: "toggle" },
  { id: "querithm.comment", label: "Comment Lines", action: "add" },
  { id: "querithm.uncomment", label: "Uncomment Lines", action: "remove" },
  { id: "querithm.blockComment", label: "Toggle Block Comment", action: "block" },
];

/** Adds the comment items to `editor`'s context menu (no keybindings: Monaco's own actions keep theirs). */
export function addCommentMenu(editor: monaco.editor.IStandaloneCodeEditor) {
  MENU.forEach((m, i) =>
    editor.addAction({
      id: m.id,
      label: m.label,
      precondition: "!editorReadonly",
      contextMenuGroupId: "1_modification",
      contextMenuOrder: 10 + i,
      run: (ed) => ed.trigger("querithm", COMMENT_ACTIONS[m.action], null),
    }),
  );
}

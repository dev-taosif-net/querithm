import { editorForTab } from "../editor/editors";

/** Moves the tab's editor to `line` (an error's editor line) and gives it the keyboard. */
export function revealEditorLine(tabId: string, line: number | null | undefined) {
  if (!line) return;
  const ed = editorForTab(tabId);
  if (!ed) return;
  ed.revealLineInCenter(line);
  ed.setPosition({ lineNumber: line, column: 1 });
  ed.focus();
}

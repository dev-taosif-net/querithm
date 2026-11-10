import { editorForTab } from "../editor/editors";

/**
 * Moves the tab's editor to `line` (an error's editor line) and, when the server named one, its `column` (Oracle parse
 * and compile errors), and gives it the keyboard. A column past the line's end stops at the end.
 */
export function revealEditorLine(tabId: string, line: number | null | undefined, column?: number | null) {
  if (!line) return;
  const ed = editorForTab(tabId);
  if (!ed) return;
  ed.revealLineInCenter(line);
  ed.setPosition({ lineNumber: line, column: column && column > 0 ? column : 1 });
  ed.focus();
}

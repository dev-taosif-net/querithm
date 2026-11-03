// Run-scope preview: while the pointer or focus is on a Run control, the editor marks exactly the text that control
// would execute (the selection, else the statement at the cursor or the whole script), in the environment color on a
// prod connection. One decoration collection and no React state, so hovering never re-renders anything.
import { monaco } from "./monaco";
import { editorForTab, getModel } from "./editors";
import { statementBoundsAt } from "./completion";
import { useTabs } from "../store/tabs";
import { useProfiles } from "../store/profiles";
import { dialectOf } from "../engines";
import type { Dialect } from "../ipc/contract";

/** `auto`: what F5 runs (selection, else everything); `statement`: what Ctrl+Shift+Enter runs. */
export type ScopeMode = "auto" | "statement";

/** Offsets `mode` executes, as runQuery picks them (token-based statement bounds, never regex); null when nothing runs. */
export function runScopeRange(
  text: string, selection: { start: number; end: number } | null, cursor: number, mode: ScopeMode, dialect: Dialect,
): { start: number; end: number } | null {
  if (selection && selection.end > selection.start) return selection;
  const r = mode === "statement" ? statementBoundsAt(text, cursor, dialect) : { start: 0, end: text.length };
  return text.slice(r.start, r.end).trim() ? r : null;
}

let shown: monaco.editor.IEditorDecorationsCollection | undefined;

export function showRunScope(tabId: string, mode: ScopeMode) {
  clearRunScope();
  const editor = editorForTab(tabId);
  const model = getModel(tabId);
  if (!editor || !model || editor.getModel() !== model) return;
  const profile = useProfiles.getState().byId(useTabs.getState().tabs[tabId]?.profileId);
  const sel = editor.getSelection();
  const selection =
    sel && !sel.isEmpty()
      ? { start: model.getOffsetAt({ lineNumber: sel.startLineNumber, column: sel.startColumn }), end: model.getOffsetAt({ lineNumber: sel.endLineNumber, column: sel.endColumn }) }
      : null;
  const pos = editor.getPosition();
  const r = runScopeRange(model.getValue(), selection, pos ? model.getOffsetAt(pos) : 0, mode, dialectOf(profile?.engine));
  if (!r) return;
  const a = model.getPositionAt(r.start);
  const b = model.getPositionAt(r.end);
  const className = profile?.environment === "prod" ? "q-run-scope-prod" : "q-run-scope";
  // A selection is marked exactly; a statement or the script as whole lines, without the line its end merely touches.
  const range = selection
    ? new monaco.Range(a.lineNumber, a.column, b.lineNumber, b.column)
    : new monaco.Range(a.lineNumber, 1, b.column === 1 && b.lineNumber > a.lineNumber ? b.lineNumber - 1 : b.lineNumber, 1);
  shown = editor.createDecorationsCollection([{ range, options: { className, isWholeLine: !selection } }]);
}

export function clearRunScope() {
  shown?.clear();
  shown = undefined;
}

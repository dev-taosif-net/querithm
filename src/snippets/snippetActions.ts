// Snippet actions used by commands and the palette; the panel itself loads lazily with its dialog.
import type { Dialect, Snippet } from "../ipc/contract";
import { useUi, toast } from "../store/ui";
import { useTabs } from "../store/tabs";
import { profileDialect } from "../store/profiles";
import { editorForTab } from "../editor/editors";
import { blankSnippet } from "./snippetStore";

/** The active tab's dialect (its profile's engine; T-SQL for an unbound tab). */
export function activeDialect(): Dialect {
  const s = useTabs.getState();
  const tab = s.activeId ? s.tabs[s.activeId] : undefined;
  return profileDialect(tab?.profileId);
}

/** Inserts the snippet at the active tab's cursor, placeholders and all. */
export function insertSnippet(s: Snippet): boolean {
  const id = useTabs.getState().activeId;
  const editor = id ? editorForTab(id) : undefined;
  if (!editor) {
    toast("info", "Open a query tab to insert a snippet.");
    return false;
  }
  const ctrl = editor.getContribution("snippetController2") as { insert(text: string): void } | null;
  if (ctrl) ctrl.insert(s.body);
  else {
    const sel = editor.getSelection();
    if (sel) editor.executeEdits("snippet", [{ range: sel, text: s.body, forceMoveMarkers: true }]);
  }
  editor.focus();
  return true;
}

/** "Save selection as snippet": the editor dialog with the selection (or the whole text) as the body. */
export function saveSelectionAsSnippet() {
  const id = useTabs.getState().activeId;
  const editor = id ? editorForTab(id) : undefined;
  const model = editor?.getModel();
  const sel = editor?.getSelection();
  const text = model ? (sel && !sel.isEmpty() ? model.getValueInRange(sel) : model.getValue()) : "";
  if (!text.trim()) {
    toast("info", "Select the SQL to save as a snippet first.");
    return;
  }
  useUi.getState().openDialog({ kind: "snippetEdit", snippet: blankSnippet({ body: escapeSnippetText(text), dialect: activeDialect() }) });
}

/**
 * Escapes text so Monaco's snippet grammar inserts it verbatim: `$` (PostgreSQL `$$` bodies, `$1` parameters) would
 * start a placeholder, `}` would close one, and `\` escapes the next character.
 */
export function escapeSnippetText(text: string): string {
  let out = "";
  for (const ch of text) out += ch === "\\" || ch === "$" || ch === "}" ? `\\${ch}` : ch;
  return out;
}

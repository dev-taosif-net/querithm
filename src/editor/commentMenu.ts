// Comment commands in the editor's right-click menu and the shared Monaco runner.
// Keeps zero runtime imports of `monaco.ts` / `editors.ts` so both editor contexts and unit tests stay lightweight.
import type { monaco } from "./monaco";
import { dialectForLanguage } from "../engines";
import { useSettings } from "../store/settings";
import { toast } from "../store/ui";
import { applyTextEdits, coalesceEditsForRange, computeCommentEdits, type CommentMode, type CommentOptions, type SelectionSpan } from "./smartComment";

// Monaco's built-in comment action ids (kept exported for compatibility; right-click and app commands run smartComment).
export const COMMENT_ACTIONS = {
  toggle: "editor.action.commentLine",
  add: "editor.action.addCommentLine",
  remove: "editor.action.removeCommentLine",
  block: "editor.action.blockComment",
} as const;

/** Bulk-edit threshold above which contiguous edits are coalesced into 1 Monaco range edit (2 getPositionAt calls). */
const COALESCE_EDIT_THRESHOLD = 32;

/** True while `runCommentInEditor` is applying edits, so `installKeywordCaseOnType` skips re-lexing on `-- `. */
let applyingCommentEdit = false;
export function isApplyingCommentEdit(): boolean {
  return applyingCommentEdit;
}

function commentOptions(): CommentOptions {
  const s = useSettings.getState();
  return {
    advanceCursor: s.commentAdvanceCursor,
    inlineBlock: s.commentInlineBlock,
    placement: s.commentPlacement,
    skipBlankLines: s.commentSkipBlankLines,
    mysqlLineStyle: s.mysqlLineCommentStyle,
  };
}

function offsetToPosition(text: string, offset: number): { lineNumber: number; column: number } {
  const clamped = Math.max(0, Math.min(offset, text.length));
  let lineNumber = 1;
  let lineStart = 0;
  for (let i = 0; i < clamped; i++) {
    if (text.charCodeAt(i) === 10) {
      lineNumber++;
      lineStart = i + 1;
    }
  }
  return { lineNumber, column: clamped - lineStart + 1 };
}

function editorSelections(ed: monaco.editor.IStandaloneCodeEditor, model: monaco.editor.ITextModel): SelectionSpan[] {
  const raw = (ed as { getSelections?: () => monaco.Selection[] | null }).getSelections?.();
  if (raw && raw.length > 0) {
    return raw.map((s) => ({
      start: model.getOffsetAt({ lineNumber: s.startLineNumber, column: s.startColumn }),
      end: model.getOffsetAt({ lineNumber: s.endLineNumber, column: s.endColumn }),
    }));
  }
  const sel = ed.getSelection();
  if (sel) {
    return [{
      start: model.getOffsetAt({ lineNumber: sel.startLineNumber, column: sel.startColumn }),
      end: model.getOffsetAt({ lineNumber: sel.endLineNumber, column: sel.endColumn }),
    }];
  }
  const pos = ed.getPosition();
  const off = pos ? model.getOffsetAt(pos) : 0;
  return [{ start: off, end: off }];
}

/** Runs a token-aware SQL comment operation directly on `ed` (works for query tabs, notebook cells, and borrowed editors). */
export function runCommentInEditor(ed: monaco.editor.IStandaloneCodeEditor, mode: CommentMode) {
  const model = ed.getModel();
  if (!model || model.isDisposed?.() || ed.getRawOptions?.().readOnly) return;
  const text = model.getValue();
  const dialect = dialectForLanguage(model.getLanguageId());
  const sels = editorSelections(ed, model);
  const res = computeCommentEdits(text, sels, dialect, mode, commentOptions());
  if (res.edits.length > 0) {
    // Coalesce large single-selection/full-document batches into one piece-tree range edit (keeps multi-cursor markers intact).
    const editsToApply =
      sels.length === 1 && res.edits.length > COALESCE_EDIT_THRESHOLD ? [coalesceEditsForRange(text, res.edits)] : res.edits;
    const monacoEdits = editsToApply.map((e) => {
      const a = model.getPositionAt(e.start);
      const b = model.getPositionAt(e.end);
      return {
        range: { startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column },
        text: e.text,
      };
    });
    const versionBefore = model.getVersionId?.();
    applyingCommentEdit = true;
    try {
      ed.pushUndoStop();
      ed.executeEdits(`querithm.comment.${mode}`, monacoEdits);
      ed.pushUndoStop();
    } finally {
      applyingCommentEdit = false;
    }
    if (res.nextSelections && res.nextSelections.length > 0) {
      // Real Monaco updates the model synchronously during executeEdits: use its O(log L) getPositionAt directly.
      const modelUpdated = versionBefore !== undefined && model.getVersionId?.() !== versionBefore;
      const fallbackText = modelUpdated ? "" : applyTextEdits(text, res.edits);
      const mapped = res.nextSelections.map((s) => {
        const a = modelUpdated ? model.getPositionAt(s.start) : offsetToPosition(fallbackText, s.start);
        const b = modelUpdated ? model.getPositionAt(s.end) : offsetToPosition(fallbackText, s.end);
        return {
          selectionStartLineNumber: a.lineNumber,
          selectionStartColumn: a.column,
          positionLineNumber: b.lineNumber,
          positionColumn: b.column,
        };
      });
      const withSetSels = ed as { setSelections?: (s: typeof mapped) => void };
      if (withSetSels.setSelections) withSetSels.setSelections(mapped);
      else ed.setPosition({ lineNumber: mapped[0].positionLineNumber, column: mapped[0].positionColumn });
    }
  }
  if (res.notice) toast("info", res.notice);
}

export interface CommentChords {
  add?: number[];
  remove?: number[];
}

const MENU: { id: string; label: string; action: CommentMode }[] = [
  { id: "querithm.toggleComment", label: "Toggle Line Comment", action: "toggle" },
  { id: "querithm.comment", label: "Comment Lines", action: "add" },
  { id: "querithm.uncomment", label: "Uncomment Lines", action: "remove" },
  { id: "querithm.blockComment", label: "Toggle Block Comment", action: "block" },
  { id: "querithm.statementComment", label: "Toggle Statement Comment", action: "statement" },
  { id: "querithm.convertComment", label: "Convert Comment Style (-- ↔ /* */)", action: "convert" },
  { id: "querithm.sectionComment", label: "Insert Section Header", action: "section" },
  { id: "querithm.docHeaderComment", label: "Insert Doc Block Header", action: "docHeader" },
  { id: "querithm.stripComments", label: "Remove All Comments", action: "strip" },
];

/** Adds the SQL comment items to `editor`'s context menu and optionally binds SSMS Ctrl+K chords to the smart engine. */
export function addCommentMenu(editor: monaco.editor.IStandaloneCodeEditor, chords?: CommentChords) {
  MENU.forEach((m, i) => {
    const keybindings = m.action === "add" ? chords?.add : m.action === "remove" ? chords?.remove : undefined;
    editor.addAction({
      id: m.id,
      label: m.label,
      ...(keybindings ? { keybindings } : {}),
      precondition: "!editorReadonly",
      contextMenuGroupId: "1_modification",
      contextMenuOrder: 10 + i,
      run: (ed) => runCommentInEditor(ed as monaco.editor.IStandaloneCodeEditor, m.action),
    });
  });
}

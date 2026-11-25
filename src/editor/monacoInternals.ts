// The one place Querithm reads Monaco internals (L23): the suggest widget's visibility, and clearing a model's semantic
// tokens (clearSemanticTokens, below). Monaco 0.52 has no public API for "is the suggest widget
// open": `editor.createContextKey` only makes keys, and no event reports the widget's visibility. Tier-2 completion
// (providers.ts) needs it to merge late sidecar items into an open list, so this reads the editor's scoped context key
// service (`CodeEditorWidget._contextKeyService`, key `suggestWidgetVisible`). A Monaco upgrade that renames either
// makes this answer false — completion still works, late tier-2 items then wait for the next trigger — and
// __tests__/monacoInternals.test.ts fails on the bundled sources first, so the upgrade sees it.
import type * as monaco from "monaco-editor";

/** The suggest widget's context key (`historyNavigationVisible` in Monaco's sources). */
export const SUGGEST_VISIBLE_KEY = "suggestWidgetVisible";

/** Whether the editor's suggest widget is showing; false when Monaco's internals are not what this build expects. */
export function suggestWidgetVisible(editor: monaco.editor.ICodeEditor): boolean {
  try {
    const svc = (editor as unknown as { _contextKeyService?: { getContextKeyValue?(k: string): unknown } })._contextKeyService;
    return typeof svc?.getContextKeyValue === "function" && svc.getContextKeyValue(SUGGEST_VISIBLE_KEY) === true;
  } catch {
    return false;
  }
}

/**
 * Drops a model's applied semantic tokens (App Themes V7): they hold colour ids of the colour map they were styled with,
 * so after a palette change they would show old colours until the next request. The same call Monaco makes when semantic
 * colouring is off (viewportSemanticTokens.js), through `TextModel.tokenization` (tokenizationTextModelPart.js
 * `setSemanticTokens(tokens, isComplete)`), which the public API does not expose. A rename on upgrade makes this do
 * nothing, and __tests__/monacoInternals.test.ts fails on the bundled sources first.
 */
export function clearSemanticTokens(model: monaco.editor.ITextModel): void {
  try {
    const part = (model as unknown as { tokenization?: { setSemanticTokens?(tokens: null, isComplete: boolean): void } }).tokenization;
    if (typeof part?.setSemanticTokens === "function") part.setSemanticTokens(null, false);
  } catch {
    /* a disposed model: nothing to clear */
  }
}

// Dropping text into the editor (an Object Explorer name, a column list, text from another app).
//
// Monaco's own drop handling (`dropIntoEditor`) turns the dropped text into a snippet — escaped text plus `$0` — and
// hands it to the bulk edit service with `insertAsSnippet`. The standalone editor's bulk edit service ignores that flag
// and writes the snippet as plain text, so a dropped `dbo.Orders` arrived as `dbo.Orders$0` (and a `$` in a name came
// out as `\$`). editorOptions() turns that option off and this inserts the dropped text as it is.
import type * as monaco from "monaco-editor";

type Editor = Pick<
  monaco.editor.IStandaloneCodeEditor,
  "getTargetAtClientPoint" | "getModel" | "getRawOptions" | "executeEdits" | "setPosition" | "focus" | "pushUndoStop" | "createDecorationsCollection"
>;

function droppedText(e: DragEvent): string {
  return e.dataTransfer?.getData("text/plain") ?? "";
}

function offersText(e: DragEvent): boolean {
  const types = e.dataTransfer?.types;
  return !!types && Array.from(types).includes("text/plain");
}

/** Lets `host` (the element the editor was created in) accept dropped text. Returns a disposer. */
export function installTextDrop(editor: Editor, host: HTMLElement): () => void {
  const marker = editor.createDecorationsCollection();
  const readOnly = () => editor.getRawOptions().readOnly === true;
  const positionAt = (e: DragEvent) => editor.getTargetAtClientPoint(e.clientX, e.clientY)?.position ?? null;

  const onDragOver = (e: DragEvent) => {
    if (readOnly() || !editor.getModel() || !offersText(e)) return;
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = e.dataTransfer.effectAllowed === "move" ? "move" : "copy";
    const p = positionAt(e);
    marker.set(p ? [{ range: { startLineNumber: p.lineNumber, startColumn: p.column, endLineNumber: p.lineNumber, endColumn: p.column }, options: { className: "dnd-target" } }] : []);
  };
  const clear = () => marker.clear();
  const onDragLeave = (e: DragEvent) => {
    // Leaving a child for another child of the editor is not leaving the editor.
    if (!(e.relatedTarget instanceof Node) || !host.contains(e.relatedTarget)) clear();
  };
  const onDrop = (e: DragEvent) => {
    clear();
    const model = editor.getModel();
    if (readOnly() || !model) return;
    const text = droppedText(e);
    const p = positionAt(e);
    if (!text || !p) return;
    e.preventDefault();
    const range = { startLineNumber: p.lineNumber, startColumn: p.column, endLineNumber: p.lineNumber, endColumn: p.column };
    editor.pushUndoStop();
    editor.executeEdits("querithm.drop", [{ range, text, forceMoveMarkers: true }]);
    editor.pushUndoStop();
    const end = model.getPositionAt(model.getOffsetAt(p) + text.length);
    editor.setPosition(end);
    editor.focus();
  };

  host.addEventListener("dragover", onDragOver);
  host.addEventListener("dragleave", onDragLeave);
  host.addEventListener("drop", onDrop);
  host.addEventListener("dragend", clear);
  return () => {
    host.removeEventListener("dragover", onDragOver);
    host.removeEventListener("dragleave", onDragLeave);
    host.removeEventListener("drop", onDrop);
    host.removeEventListener("dragend", clear);
    clear();
  };
}

// Monaco models are created once per tab and disposed on close (§12). Editor instances are shared: one per
// visible Dockview group (normally one), and switching tabs calls setModel() — never one Monaco per tab.
import { initMonaco, monaco } from "./monaco";
import { useSettings } from "../store/settings";
import { createOverflowHost } from "./overflowHost";
import { languageOf } from "../engines";
import type { Engine } from "../ipc/contract";

const models = new Map<string, monaco.editor.ITextModel>();
const tabOfModel = new Map<string, string>();
/** Model key → the tab it belongs to, for models that are not a tab's own (a notebook's cells, Phase C3). */
const ownerOfModel = new Map<string, string>();
const viewStates = new Map<string, monaco.editor.ICodeEditorViewState | null>();
type EditorEntry = { editor: monaco.editor.IStandaloneCodeEditor; host: HTMLDivElement; overflow: HTMLDivElement; tabId?: string; readOnly?: boolean; compact?: boolean };
const editors = new Map<string, EditorEntry>();
// The editor lent to a dialog before any query tab exists: the first group takes it over instead of creating another.
let spare: { entry: EditorEntry; borrowed: boolean } | undefined;
const contentListeners = new Set<(tabId: string, model: monaco.editor.ITextModel) => void>();
const disposeListeners = new Set<(key: string) => void>();
const editorCreated = new Set<(editor: monaco.editor.IStandaloneCodeEditor) => void>();

/** The Monaco language of an engine's dialect; a tab without a connection uses the default engine's. */
export function languageFor(engine: Engine | null | undefined) {
  return languageOf(engine);
}

export function getModel(tabId: string): monaco.editor.ITextModel | undefined {
  return models.get(tabId);
}

/**
 * A model per tab, created once. `owner`: the tab a model that is not the tab's own belongs to (a notebook cell's model
 * is keyed by the cell and owned by the notebook tab): completion and diagnostics then read that tab's connection.
 */
export function createModel(tabId: string, text: string, language: string, owner?: string): monaco.editor.ITextModel {
  initMonaco();
  const existing = models.get(tabId);
  if (existing) return existing;
  if (owner) ownerOfModel.set(tabId, owner);
  const uri = monaco.Uri.parse(`inmemory://querithm/${tabId}.sql`);
  const model = monaco.editor.createModel(text, language, uri);
  model.updateOptions({ tabSize: 4, insertSpaces: true });
  models.set(tabId, model);
  tabOfModel.set(uri.toString(), tabId);
  model.onDidChangeContent(() => contentListeners.forEach((l) => l(tabId, model)));
  return model;
}

export function setModelLanguage(tabId: string, language: string) {
  const m = models.get(tabId);
  if (m && m.getLanguageId() !== language) monaco.editor.setModelLanguage(m, language);
}

export function disposeModel(tabId: string) {
  const m = models.get(tabId);
  if (!m) return;
  for (const e of editors.values()) if (e.editor.getModel() === m) e.editor.setModel(null);
  tabOfModel.delete(m.uri.toString());
  ownerOfModel.delete(tabId);
  models.delete(tabId);
  viewStates.delete(tabId);
  m.dispose();
  disposeListeners.forEach((l) => l(tabId));
}

export function tabIdOfModel(model: monaco.editor.ITextModel | null): string | undefined {
  return model ? tabOfModel.get(model.uri.toString()) : undefined;
}

/** The tab a model key belongs to: itself for a tab's own model, the notebook for a cell's. */
export function modelOwner(key: string | undefined): string | undefined {
  return key ? (ownerOfModel.get(key) ?? key) : undefined;
}

export function onModelContent(l: (tabId: string, model: monaco.editor.ITextModel) => void) {
  contentListeners.add(l);
  return () => contentListeners.delete(l);
}

/** A model went away (a closed tab, a deleted notebook cell): `key` is its docId for the sidecar's caches (M12). */
export function onModelDisposed(l: (key: string) => void) {
  disposeListeners.add(l);
  return () => disposeListeners.delete(l);
}

export function onEditorCreated(l: (editor: monaco.editor.IStandaloneCodeEditor) => void) {
  editorCreated.add(l);
  for (const e of editors.values()) l(e.editor);
  return () => editorCreated.delete(l);
}

function editorOptions(): monaco.editor.IStandaloneEditorConstructionOptions {
  const s = useSettings.getState();
  return {
    automaticLayout: true,
    fontFamily: '"JetBrains Mono Variable", "JetBrains Mono", "Cascadia Code", Menlo, monospace',
    fontSize: s.editorFontSize,
    lineHeight: Math.round(s.editorFontSize * 1.7),
    fontLigatures: false,
    minimap: { enabled: s.minimap && !s.reducedDecorations, renderCharacters: false, scale: 1 },
    wordWrap: s.wordWrap ? "on" : "off",
    glyphMargin: true,
    folding: true,
    foldingStrategy: "auto",
    showFoldingControls: "mouseover",
    renderLineHighlight: s.reducedDecorations ? "none" : "all",
    scrollBeyondLastLine: false,
    smoothScrolling: false,
    cursorSmoothCaretAnimation: "off",
    occurrencesHighlight: s.reducedDecorations ? "off" : "singleFile",
    // No tint on other copies of the selected text: with equal lines it looked like they were selected too, while Run
    // (rightly) runs only the selection.
    selectionHighlight: false,
    bracketPairColorization: { enabled: !s.reducedDecorations },
    // snippetsPreventQuickSuggestions: typing over a snippet placeholder (e.g. `table_name`) must still suggest.
    suggest: { showStatusBar: true, preview: false, showWords: false, insertMode: "replace", filterGraceful: true, snippetsPreventQuickSuggestions: false },
    quickSuggestions: { other: true, comments: false, strings: false },
    suggestOnTriggerCharacters: true,
    acceptSuggestionOnEnter: "on",
    tabCompletion: "on",
    wordBasedSuggestions: "off",
    fixedOverflowWidgets: true,
    padding: { top: 8 },
    // Ctrl+F overlays the find widget; don't push the query down to make room for it.
    find: { addExtraSpaceOnTop: false },
    scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10, useShadows: false },
    stickyScroll: { enabled: !s.reducedDecorations },
    "semanticHighlighting.enabled": false,
    contextmenu: true,
    multiCursorModifier: "alt",
    unicodeHighlight: { ambiguousCharacters: false, invisibleCharacters: false },
  };
}

/** A notebook cell (Phase C3): the shared editor without minimap, sticky scroll or extra margins; it grows with the text. */
function compactOptions(): monaco.editor.IEditorOptions {
  return {
    ...editorOptions(),
    minimap: { enabled: false },
    stickyScroll: { enabled: false },
    glyphMargin: false,
    folding: false,
    // No line numbers: the cell's text then sits where its static form shows it (errors still mark their line).
    lineNumbers: "off",
    lineDecorationsWidth: 10,
    padding: { top: 6, bottom: 6 },
    scrollbar: { vertical: "hidden", horizontalScrollbarSize: 8, useShadows: false, alwaysConsumeMouseWheel: false },
    overviewRulerLanes: 0,
    renderLineHighlight: "none",
  };
}

/** Applies settings to every live editor. */
export function refreshEditorOptions() {
  for (const e of editors.values()) e.editor.updateOptions(e.compact ? compactOptions() : editorOptions());
  spare?.entry.editor.updateOptions(editorOptions());
}

/**
 * Puts the group's shared editor into `container` showing `tabId`'s model. `compact`: the notebook cell form, swapped
 * back to the full editor when a query tab takes the editor again.
 */
export function attachEditor(groupId: string, container: HTMLElement, tabId: string, readOnly: boolean, compact = false) {
  const entry = groupEditor(groupId);
  const { editor, host } = entry;
  if (!!entry.compact !== compact) {
    editor.updateOptions(compact ? compactOptions() : editorOptions());
    entry.compact = compact;
  }
  if (entry.tabId && entry.tabId !== tabId && models.has(entry.tabId)) viewStates.set(entry.tabId, editor.saveViewState());
  if (host.parentElement !== container) container.appendChild(host);
  const model = models.get(tabId);
  if (editor.getModel() !== model) {
    editor.setModel(model ?? null);
    const vs = viewStates.get(tabId);
    if (vs) editor.restoreViewState(vs);
  }
  editor.updateOptions({ readOnly });
  entry.readOnly = readOnly;
  const moved = entry.tabId !== tabId;
  entry.tabId = tabId;
  // automaticLayout observes size changes; an explicit layout is only needed after a re-parent.
  if (moved) editor.layout();
  return editor;
}

let borrowSeq = 0;

/**
 * Lends the shared editor to a dialog (the snippet editor, §12: never a second Monaco instance): it moves into
 * `container` showing a temporary model, and `release()` returns it to where it was with the tab's model, view state
 * and read-only flag, and disposes the temporary model. With no query tab open yet, the editor is created here and
 * kept aside for the first tab's group to take over (or disposed, if a group created its own meanwhile).
 */
export function borrowEditor(container: HTMLElement, text: string, language: string) {
  const first = editors.values().next().value;
  let ownSpare = false;
  if (!first && !spare) spare = { entry: createEntry(), borrowed: false };
  if (!first && spare) {
    spare.borrowed = true;
    ownSpare = true;
  }
  const entry = first ?? spare!.entry;
  const { editor, host } = entry;
  const parent = host.parentElement;
  const previous = editor.getModel();
  const view = previous ? editor.saveViewState() : null;
  const temp = monaco.editor.createModel(text, language, monaco.Uri.parse(`inmemory://querithm/__borrowed-${++borrowSeq}.sql`));
  container.appendChild(host);
  editor.setModel(temp);
  editor.updateOptions({ readOnly: false });
  editor.layout();
  let released = false;
  return {
    editor,
    getValue: () => temp.getValue(),
    release() {
      if (released) return;
      released = true;
      if (parent) parent.appendChild(host);
      else host.remove();
      if (editor.getModel() === temp) {
        editor.setModel(previous && !previous.isDisposed() ? previous : null);
        if (view && previous && !previous.isDisposed()) editor.restoreViewState(view);
      }
      editor.updateOptions({ readOnly: entry.readOnly ?? false });
      temp.dispose();
      if (ownSpare && spare?.entry === entry) {
        spare.borrowed = false;
        // A group created its own editor while this one was lent out: one is enough.
        if (editors.size > 0) disposeSpare();
        return;
      }
      editor.layout();
    },
  };
}

function disposeSpare() {
  if (!spare) return;
  spare.entry.editor.dispose();
  spare.entry.host.remove();
  spare.entry.overflow.remove();
  spare = undefined;
}

function groupEditor(groupId: string) {
  let entry = editors.get(groupId);
  if (!entry) {
    if (spare && !spare.borrowed) {
      entry = spare.entry;
      spare = undefined;
    } else entry = createEntry();
    editors.set(groupId, entry);
  }
  return entry;
}

function createEntry(): EditorEntry {
  initMonaco();
  const host = document.createElement("div");
  host.style.position = "absolute";
  host.style.inset = "0";
  // Linux webviews paste the primary selection (text last highlighted in any app) on middle-click; in a script
  // that is about to run that inserts stray text, so the release is swallowed before the webview acts on it.
  const noPrimaryPaste = (e: MouseEvent) => {
    if (e.button === 1) e.preventDefault();
  };
  host.addEventListener("mouseup", noPrimaryPaste, true);
  host.addEventListener("auxclick", noPrimaryPaste, true);
  const overflow = createOverflowHost(document);
  const editor = monaco.editor.create(host, { ...editorOptions(), overflowWidgetsDomNode: overflow });
  editorCreated.forEach((l) => l(editor));
  return { editor, host, overflow };
}

export function editorForTab(tabId: string): monaco.editor.IStandaloneCodeEditor | undefined {
  for (const e of editors.values()) if (e.tabId === tabId) return e.editor;
  return undefined;
}

export function focusedEditor(): monaco.editor.IStandaloneCodeEditor | undefined {
  for (const e of editors.values()) if (e.editor.hasTextFocus()) return e.editor;
  return undefined;
}

export function disposeGroupEditor(groupId: string) {
  const e = editors.get(groupId);
  if (!e) return;
  e.editor.dispose();
  e.host.remove();
  e.overflow.remove();
  editors.delete(groupId);
}

export function allEditors() {
  return [...editors.values()].map((e) => e.editor);
}

/** Text to run: the selection if any, else everything; lineOffset maps server line numbers back (§10 step 3). */
export function executableText(tabId: string): { sql: string; lineOffset: number; isSelection: boolean } {
  const model = models.get(tabId);
  if (!model) return { sql: "", lineOffset: 1, isSelection: false };
  const editor = editorForTab(tabId);
  const sel = editor?.getSelection();
  if (editor && sel && !sel.isEmpty() && editor.getModel() === model) {
    return { sql: model.getValueInRange(sel), lineOffset: sel.startLineNumber, isSelection: true };
  }
  return { sql: model.getValue(), lineOffset: 1, isSelection: false };
}

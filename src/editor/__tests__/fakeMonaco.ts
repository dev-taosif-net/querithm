// A minimal in-memory stand-in for the Monaco API surface that editors.ts / providers.ts / editorActions.ts use, so the
// glue code is tested without loading the real editor (which needs a browser layout engine).
import { vi } from "vitest";

export interface Pos { lineNumber: number; column: number }

export class FakeRange {
  constructor(public startLineNumber: number, public startColumn: number, public endLineNumber: number, public endColumn: number) {}
  isEmpty() { return this.startLineNumber === this.endLineNumber && this.startColumn === this.endColumn; }
}

export class FakeModel {
  disposed = false;
  version = 1;
  listeners: Array<() => void> = [];
  options: unknown;
  constructor(public text: string, public lang: string, public uri: { toString(): string }) {}
  getValue() { return this.text; }
  getLanguageId() { return this.lang; }
  getVersionId() { return this.version; }
  isDisposed() { return this.disposed; }
  updateOptions(o: unknown) { this.options = o; }
  dispose() { this.disposed = true; }
  getLineContent(n: number) { return this.text.split("\n")[n - 1] ?? ""; }
  getOffsetAt(p: Pos) {
    const lines = this.text.split("\n");
    let off = 0;
    for (let i = 0; i < p.lineNumber - 1; i++) off += lines[i].length + 1;
    return off + p.column - 1;
  }
  getPositionAt(offset: number): Pos {
    const before = this.text.slice(0, offset).split("\n");
    return { lineNumber: before.length, column: before[before.length - 1].length + 1 };
  }
  getValueInRange(r: FakeRange) {
    return this.text.slice(this.getOffsetAt({ lineNumber: r.startLineNumber, column: r.startColumn }), this.getOffsetAt({ lineNumber: r.endLineNumber, column: r.endColumn }));
  }
  getFullModelRange() {
    const lines = this.text.split("\n");
    return new FakeRange(1, 1, lines.length, lines[lines.length - 1].length + 1);
  }
  onDidChangeContent(cb: () => void) { this.listeners.push(cb); return { dispose() {} }; }
  setValue(text: string) { this.text = text; this.version++; this.listeners.forEach((l) => l()); }
}

export class FakeEditor {
  model: FakeModel | null = null;
  position: Pos | null = { lineNumber: 1, column: 1 };
  selection: FakeRange | null = null;
  focus = false;
  disposed = false;
  edits: Array<{ source: string; edits: Array<{ range: unknown; text: string }> }> = [];
  options: Record<string, unknown> = {};
  contentCbs: Array<(e: unknown) => void> = [];
  modelCbs: Array<() => void> = [];
  disposeCbs: Array<() => void> = [];
  onDidDispose(cb: () => void) { this.disposeCbs.push(cb); return { dispose() {} }; }
  triggered: string[] = [];
  actions: Array<{ id: string; label: string; precondition?: string; contextMenuGroupId?: string; run: (ed: FakeEditor) => void }> = [];
  addAction(a: FakeEditor["actions"][number]) { this.actions.push(a); return { dispose() {} }; }
  undoStops = 0;
  suggestVisible = false;
  _contextKeyService = { getContextKeyValue: (k: string) => (k === "suggestWidgetVisible" ? this.suggestVisible : undefined) };
  layout = vi.fn();
  createDecorationsCollection = vi.fn(() => ({ set: vi.fn(), clear: vi.fn() }));
  cursorPositionCbs: Array<(e: unknown) => void> = [];
  onDidChangeCursorPosition(cb: (e: unknown) => void) { this.cursorPositionCbs.push(cb); return { dispose() {} }; }
  constructor(public host: unknown, public opts: Record<string, unknown>) {}
  getModel() { return this.model; }
  setModel(m: FakeModel | null) { this.model = m; this.modelCbs.forEach((c) => c()); }
  saveViewState() { return { saved: this.model?.uri.toString() }; }
  restoreViewState = vi.fn();
  updateOptions(o: Record<string, unknown>) { Object.assign(this.options, o); }
  getRawOptions() { return { ...this.opts, ...this.options }; }
  getTargetAtClientPoint(): { position: Pos } | null { return null; }
  setPosition(p: Pos) { this.position = p; }
  getPosition() { return this.position; }
  getSelection() { return this.selection; }
  hasTextFocus() { return this.focus; }
  dispose() { this.disposed = true; this.disposeCbs.forEach((cb) => cb()); }
  executeEdits(source: string, edits: Array<{ range: unknown; text: string }>) { this.edits.push({ source, edits }); return true; }
  pushUndoStop() { this.undoStops++; }
  triggerArgs: unknown[] = [];
  trigger(source: string, id: string, args?: unknown) { this.triggered.push(`${source}:${id}`); this.triggerArgs.push(args); }
  keyDownCbs: Array<(e: { keyCode: number }) => void> = [];
  onKeyDown(cb: (e: { keyCode: number }) => void) { this.keyDownCbs.push(cb); return { dispose() {} }; }
  /** Simulates a key typed in the editor (Monaco's KeyCode numbers). */
  pressKey(keyCode: number) { this.keyDownCbs.forEach((cb) => cb({ keyCode })); }
  onDidChangeModelContent(cb: (e: unknown) => void) { this.contentCbs.push(cb); return { dispose() {} }; }
  onDidChangeModel(cb: () => void) { this.modelCbs.push(cb); return { dispose() {} }; }
}

export function makeFakeMonaco() {
  const created: FakeEditor[] = [];
  const models: FakeModel[] = [];
  const providers: Record<string, Record<string, any>> = {};
  const reg = (kind: string) => (lang: string, p: unknown) => { (providers[lang] ??= {})[kind] = p; return { dispose() {} }; };
  const commands: Record<string, (...a: any[]) => void> = {};
  const monaco = {
    Uri: { parse: (s: string) => ({ toString: () => s }) },
    Range: FakeRange,
    KeyCode: { Enter: 3, Escape: 9, PageUp: 11, PageDown: 12, UpArrow: 16, DownArrow: 18, KeyC: 33, KeyK: 41, KeyU: 51 },
    KeyMod: { CtrlCmd: 2048, chord: (a: number, b: number) => a | (b << 16) },
    MarkerSeverity: { Error: 8, Warning: 4, Info: 2 },
    editor: {
      createModel: vi.fn((text: string, lang: string, uri: { toString(): string }) => { const m = new FakeModel(text, lang, uri); models.push(m); return m; }),
      create: vi.fn((host: unknown, opts: Record<string, unknown>) => { const e = new FakeEditor(host, opts); created.push(e); return e; }),
      setModelLanguage: vi.fn((m: FakeModel, l: string) => { m.lang = l; }),
      getEditors: () => created,
      getModels: () => models,
      setModelMarkers: vi.fn(),
      registerCommand: vi.fn((id: string, fn: (...a: any[]) => void) => { commands[id] = fn; }),
      defineTheme: vi.fn(),
      setTheme: vi.fn(),
      remeasureFonts: vi.fn(),
    },
    languages: {
      CompletionItemKind: { Keyword: 1, Struct: 2, Interface: 3, Field: 4, Method: 5, Function: 6, Variable: 7, Property: 8, Module: 9, Folder: 10, Reference: 11, Class: 12, Snippet: 13, TypeParameter: 14, Event: 15, Value: 16, EnumMember: 17, Constructor: 18, Text: 0 },
      CompletionItemInsertTextRule: { InsertAsSnippet: 4 },
      CompletionTriggerKind: { Invoke: 0, TriggerCharacter: 1, TriggerForIncompleteCompletions: 2 },
      FoldingRangeKind: { Comment: "comment", Region: "region" },
      DocumentHighlightKind: { Text: 0, Read: 1, Write: 2 },
      CodeActionKind: { RefactorRewrite: "refactor.rewrite", QuickFix: "quickfix" },
      register: vi.fn(),
      setMonarchTokensProvider: vi.fn(),
      setLanguageConfiguration: vi.fn(),
      registerCompletionItemProvider: reg("completion"),
      registerHoverProvider: reg("hover"),
      registerFoldingRangeProvider: reg("folding"),
      registerDocumentFormattingEditProvider: reg("format"),
      registerDocumentRangeFormattingEditProvider: reg("rangeFormat"),
      registerSignatureHelpProvider: reg("signature"),
      registerDocumentHighlightProvider: reg("documentHighlight"),
      registerCodeActionProvider: reg("codeAction"),
      registerDocumentRangeSemanticTokensProvider: reg("semanticTokens"),
    },
  };
  return { monaco, created, models, providers, commands };
}

export const fake = makeFakeMonaco();

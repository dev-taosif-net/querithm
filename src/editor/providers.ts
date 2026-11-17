// Monaco providers (§9 Monaco wiring). Tier 1 answers synchronously; tier 2 (sidecar AST) refines in place.
import type { CompletionItem, CompletionKind, Dialect } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { applyKeywordCase, complete, keywordCaseEdit, resolveObjectAt } from "./completion";
import type { SchemaIndex, Tier1Item } from "./completion/types";
import { sqlRegionAtOffset } from "./completion/sqlRegion";
import { closeQuoteOf, monacoFilterText, openQuoteAt } from "./completion/complete";
import { getStream } from "./completion/lexer";
import { lastStartingBefore } from "./completion/scanner";
import { expandWildcardAtOffset } from "./expandWildcard";
import { findInsertMatchingHighlight, findSyntaxPairHighlight } from "./syntaxPairs";
import { tier1SignatureHelp } from "./signature";
import { addCommentMenu } from "./commentMenu";
import { suggestWidgetVisible } from "./monacoInternals";
import { monaco } from "./monaco";
import { modelOwner, onEditorCreated, onModelContent, onModelDisposed, tabIdOfModel } from "./editors";
import { useTabs } from "../store/tabs";
import { useProfiles } from "../store/profiles";
import { ensureTabSnapshot, loginSchemaOf, tabScope, tabSnapshot, useSnapshots } from "../store/snapshots";
import { isDisconnected } from "../store/connections";
import { useSettings } from "../store/settings";
import { snippetsFor, useSnippets } from "../snippets/snippetStore";
import { assertNever, DEFAULT_DIALECT, DIALECT_LIST, DIALECTS, dialectForLanguage, dialectOf, dialectOfLanguage, foldIdentifier } from "../engines";
import { formatOracleScript } from "./oracleFormat";
import { columnLines, columnsTable, mdCode, mdFence, objectHeader, oneLine } from "./hoverMarkdown";
import { fkReference } from "./completion/schemaIndex";

export interface DocContext {
  tabId: string;
  /** The model's own key when it differs from the tab (a notebook cell): the sidecar caches per document. */
  docId?: string;
  dialect: Dialect;
  profileId?: string;
  database?: string;
  /** A session-owned engine's tab (SQLite): its session, whose own view completion and signature help read (Phase D0d). */
  sessionId?: string;
  /** The schema snapshot's version: a refresh at the same text asks tier 2 again. */
  snapVersion?: number;
  schema: SchemaIndex | null;
}

export function docContext(model: monaco.editor.ITextModel): DocContext | null {
  // A notebook cell's model reads its notebook's connection (Phase C3).
  const key = tabIdOfModel(model);
  const tabId = modelOwner(key);
  if (!tabId) return null;
  const docId = key !== tabId ? key : undefined;
  const tab = useTabs.getState().tabs[tabId];
  const profile = useProfiles.getState().byId(tab?.profileId);
  // The model's language decides, except the default dialect's: a model created before a connection was picked has
  // that language, so the tab's profile decides then.
  const fromLanguage = dialectOfLanguage(model.getLanguageId());
  const dialect: Dialect = fromLanguage !== undefined && fromLanguage !== DEFAULT_DIALECT ? fromLanguage : dialectOf(profile?.engine);
  // A disconnected server contributes nothing: no cached schema (tier 1) and no profile for tier 2 / signature help.
  if (isDisconnected(tab?.profileId)) return { tabId, ...(docId ? { docId } : {}), dialect, schema: null };
  const snap = tabSnapshot(tab);
  const sessionId = tabScope(tab)?.sessionId;
  return { tabId, ...(docId ? { docId } : {}), dialect, profileId: tab?.profileId, database: tab?.database, ...(sessionId ? { sessionId } : {}), ...(snap ? { snapVersion: snap.version } : {}), schema: snap?.index ?? null };
}

const kindMap: Record<CompletionKind, monaco.languages.CompletionItemKind> = {
  keyword: monaco.languages.CompletionItemKind.Keyword,
  table: monaco.languages.CompletionItemKind.Struct,
  view: monaco.languages.CompletionItemKind.Interface,
  column: monaco.languages.CompletionItemKind.Field,
  proc: monaco.languages.CompletionItemKind.Method,
  func: monaco.languages.CompletionItemKind.Function,
  variable: monaco.languages.CompletionItemKind.Variable,
  parameter: monaco.languages.CompletionItemKind.Property,
  schema: monaco.languages.CompletionItemKind.Module,
  database: monaco.languages.CompletionItemKind.Folder,
  alias: monaco.languages.CompletionItemKind.Reference,
  cte: monaco.languages.CompletionItemKind.Class,
  snippet: monaco.languages.CompletionItemKind.Snippet,
  type: monaco.languages.CompletionItemKind.TypeParameter,
  tempTable: monaco.languages.CompletionItemKind.Struct,
  cursor: monaco.languages.CompletionItemKind.Event,
};

/** Usage counts (accepted completions), feeding the "then usage count" ranking rule of §9. */
const usage = new Map<string, number>();
export function noteUsage(label: string) {
  const k = label.toLowerCase();
  usage.set(k, (usage.get(k) ?? 0) + 1);
}

/**
 * D4 S09: the schema tier 2 resolves unqualified names in before any ALTER SESSION SET CURRENT_SCHEMA (Oracle: the login's),
 * sent only when the dialect has one, so the other engines' requests stay as they were.
 */
export function schemaParam(profileId: string | null | undefined): { schema?: string } {
  const schema = profileId ? loginSchemaOf(profileId) : null;
  return schema ? { schema } : {};
}

// ---- tier 2 cache: the word being typed → items ----
/**
 * Keyed by the word, not the keystroke: the model, the connection (profile, database, session), the schema snapshot's
 * version, the dialect, where the word starts and the text before it. Extending the word keeps tier 2's items (Monaco
 * filters them) instead of dropping them for ~120 ms on every key and re-opening the list; a database switch or a
 * schema refresh at the same text asks again.
 */
const tier2Cache = new Map<string, CompletionItem[]>();
const TIER2_CACHE_MAX = 8;
/** Per model: its debounce timer, the key in flight, and the newest request (an older answer is never cached over it). */
const tier2Timers = new Map<string, ReturnType<typeof setTimeout>>();
const tier2Pending = new Map<string, string>();
const tier2Latest = new Map<string, number>();
let tier2Seq = 0;
let lastTier1Ms = 0;
export const completionStats = { tier1: [] as number[], tier2: [] as number[] };

function pushStat(arr: number[], v: number) {
  if (arr.length > 200) arr.shift();
  arr.push(v);
}

/** FNV-1a over a string: identifies the text before the word without keeping it. */
function textHash(s: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193); }
  return (h >>> 0).toString(36) + ":" + s.length;
}

export function tier2Key(model: monaco.editor.ITextModel, ctx: DocContext, text: string, wordStart: number): string {
  return [String(model.uri), ctx.profileId ?? "", ctx.database ?? "", ctx.sessionId ?? "", ctx.snapVersion ?? 0, ctx.dialect, wordStart, textHash(text.slice(0, wordStart))].join("|");
}

function cacheTier2(key: string, items: CompletionItem[]) {
  tier2Cache.delete(key);
  tier2Cache.set(key, items);
  while (tier2Cache.size > TIER2_CACHE_MAX) tier2Cache.delete(tier2Cache.keys().next().value!);
}

/** Drops a model's pending tier-2 request (its model went away), or every model's without a key. */
export function cancelTier2(modelKey?: string) {
  for (const k of modelKey === undefined ? [...tier2Timers.keys()] : [modelKey]) {
    clearTimeout(tier2Timers.get(k));
    tier2Timers.delete(k);
    tier2Pending.delete(k);
    tier2Latest.delete(k);
  }
}

function findEditor(model: monaco.editor.ITextModel) {
  return monaco.editor.getEditors().find((e) => e.getModel() === model);
}

function scheduleTier2(model: monaco.editor.ITextModel, ctx: DocContext, offset: number, key: string, tier1Labels: Set<string>, wordStart = offset) {
  if (!useSettings.getState().tier2) return;
  const m = ctx.docId ?? ctx.tabId; // the model key (a notebook cell's own)
  if (tier2Pending.get(m) === key) return;
  tier2Pending.set(m, key);
  clearTimeout(tier2Timers.get(m));
  const before = textHash(model.getValue().slice(0, wordStart));
  tier2Timers.set(m, setTimeout(async () => {
    tier2Timers.delete(m);
    const seq = ++tier2Seq;
    tier2Latest.set(m, seq);
    const t0 = performance.now();
    try {
      const items = await rpc("lang.complete", {
        docId: ctx.docId ?? ctx.tabId, text: model.getValue(), offset, dialect: ctx.dialect, profileId: ctx.profileId ?? null, database: ctx.database ?? null,
        ...(ctx.sessionId ? { sessionId: ctx.sessionId } : {}), ...schemaParam(ctx.profileId),
      });
      pushStat(completionStats.tier2, performance.now() - t0);
      // A slower, older request finishing after a newer one never replaces it.
      if (tier2Latest.get(m) !== seq || model.isDisposed()) return;
      cacheTier2(key, items);
      const editor = findEditor(model);
      const changed = items.some((i) => !tier1Labels.has(mergeKey(i.kind, i.label)) || i.detail);
      // Replace the open popup's items in place (§9): re-trigger only while the user is still on the same word (the text
      // before it unchanged) — or, when tier 1 had nothing to show (so no popup opened), open it with tier 2's items.
      const sameWord = textHash(model.getValue().slice(0, wordStart)) === before;
      if (editor && changed && sameWord && (suggestWidgetVisible(editor) || (tier1Labels.size === 0 && items.length > 0 && editor.hasTextFocus()))) {
        editor.trigger("tier2", "editor.action.triggerSuggest", {});
      }
    } catch {
      /* tier 2 is best effort */
    } finally {
      if (tier2Pending.get(m) === key) tier2Pending.delete(m);
    }
  }, 120)); // debounced 120 ms (§9)
}

function keywordText(label: string): string {
  const s = useSettings.getState();
  return s.keywordCaseOnType ? applyKeywordCase(label, s.keywordCase) : label;
}

/** A typed change that ends a word: whitespace (incl. Enter + indent) or punctuation, nothing word-like. */
function isBoundaryInsert(text: string): boolean {
  return text.length > 0 && text.length <= 64 && !/[\p{L}\p{N}_@#$'"[\]`]/u.test(text);
}

/** Keyword case as you type (§9): recase the reserved word just before a typed boundary, as its own undo step. */
function installKeywordCaseOnType(editor: monaco.editor.ICodeEditor) {
  editor.onDidChangeModelContent((e) => {
    if (e.isUndoing || e.isRedoing || e.isFlush || e.changes.length !== 1) return;
    const s = useSettings.getState();
    if (!s.keywordCaseOnType || s.keywordCase === "preserve") return;
    const change = e.changes[0];
    if (!isBoundaryInsert(change.text)) return;
    const model = editor.getModel();
    const ctx = model && docContext(model);
    if (!model || !ctx) return;
    const edit = keywordCaseEdit(model.getValue(), ctx.dialect, change.rangeOffset, s.keywordCase);
    if (!edit) return;
    const version = model.getVersionId();
    // Deferred: editing inside the change event would run while the typing command is still updating the cursor.
    queueMicrotask(() => {
      if (editor.getModel() !== model || model.getVersionId() !== version) return;
      const start = model.getPositionAt(edit.start), end = model.getPositionAt(edit.end);
      editor.pushUndoStop();
      editor.executeEdits("querithm.keywordCase", [{ range: new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column), text: edit.text }]);
      editor.pushUndoStop();
    });
  });
}

function toMonaco(item: Tier1Item | CompletionItem, range: monaco.IRange | monaco.languages.CompletionItemRanges, tier: 1 | 2): monaco.languages.CompletionItem {
  return {
    label: { label: item.label, description: item.detail ?? undefined },
    kind: kindMap[item.kind] ?? monaco.languages.CompletionItemKind.Text,
    detail: item.detail ?? undefined,
    documentation: item.documentation ? { value: item.documentation } : undefined,
    insertText: item.insertText ?? (item.kind === "keyword" ? keywordText(item.label) : item.label),
    insertTextRules: item.isSnippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
    sortText: item.sortText ?? (tier === 2 ? `5${item.label}` : undefined),
    filterText: "filterText" in item ? (item.filterText ?? undefined) : undefined,
    range,
    command: { id: "querithm.noteUsage", title: "", arguments: [item.label] },
  };
}

/** Clauses whose next word is an object name: a list opened by typing a space there is wanted (`FROM |`). */
const SPACE_TRIGGER_CLAUSES = new Set(["from", "join", "update", "insertInto", "delete", "exec", "use"]);

/**
 * A list opened by a typed space or comma with nothing typed yet is shown only where an object name comes next.
 * Anywhere else (`FROM t o |`, `WHERE a = 1 AND |`, `SELECT a, |`) Enter must start a new line, not accept the first
 * keyword or column; Ctrl+Space and typing a letter still open the list there.
 */
function wantsList(trigger: monaco.languages.CompletionContext | undefined, ctx: { clause: string; slot?: string; prefix: string; qualifier?: string }): boolean {
  if (!trigger || trigger.triggerKind !== monaco.languages.CompletionTriggerKind.TriggerCharacter) return true;
  if (trigger.triggerCharacter !== " " && trigger.triggerCharacter !== ",") return true;
  if (ctx.prefix || ctx.qualifier !== undefined) return true;
  return trigger.triggerCharacter === " " && SPACE_TRIGGER_CLAUSES.has(ctx.clause) && ctx.slot === undefined;
}

/** Merge key: one entry per name and kind family, so a column `Count` and the function COUNT both stay (§9). */
function mergeKey(kind: CompletionKind, label: string): string {
  const family = kind === "table" || kind === "view" || kind === "cte" || kind === "tempTable" ? "rel" : kind === "proc" || kind === "func" ? "call" : kind;
  return `${family}|${label.toLowerCase()}`;
}

/**
 * End of the name the cursor is in, so accepting replaces all of it (`Cust|omerId` → `CustomerId`, not
 * `CustomerIdomerId`). Inside an opened quote (`open` = `[`, `"` or a backtick) the name runs to its closing quote on
 * the same line, which is replaced too; without one, to the end of the word.
 */
export function wordEndAt(text: string, offset: number, open = ""): number {
  if (open) {
    const close = closeQuoteOf(open);
    for (let e = offset; e < text.length && e - offset < 256; e++) {
      const c = text[e];
      if (c === "\n" || c === "\r") break;
      if (c === close) return e + 1;
    }
  }
  let end = offset;
  while (end < text.length && /[\p{L}\p{N}_$#@]/u.test(text[end])) end++;
  return end;
}

function provideCompletion(model: monaco.editor.ITextModel, position: monaco.Position, trigger?: monaco.languages.CompletionContext): monaco.languages.CompletionList {
  const ctx = docContext(model);
  if (!ctx) return { suggestions: [] };
  // No schema cached for a connected tab (never loaded, or the load failed): load it now, so the next request —
  // `incomplete: true` re-asks on every keystroke — has its tables. ensure() is a no-op while a load is in flight.
  if (!ctx.schema && ctx.profileId) ensureTabSnapshot(useTabs.getState().tabs[ctx.tabId]);
  const t0 = performance.now();
  const offset = model.getOffsetAt(position);
  const text = model.getValue();
  const result = complete({ text, offset, dialect: ctx.dialect, schema: ctx.schema, usage, autoAlias: useSettings.getState().autoAlias });
  lastTier1Ms = performance.now() - t0;
  pushStat(completionStats.tier1, lastTier1Ms);
  if (result.context.inStringOrComment) {
    return inDollarBody(text, offset, ctx.dialect) ? dollarBodyCompletion(model, ctx, text, offset, position) : { suggestions: [] };
  }
  if (!wantsList(trigger, result.context)) return { suggestions: [], incomplete: true };
  // In a routine body tier 2 answers `$1` and `"Quoted"`: the range must cover a typed `$` or opening `"` too.
  const bodyStart = dollarBodyStart(text, offset, ctx.dialect);
  // An opened quote (`[Ord|`, `"Ord|`) is replaced with the item's fully quoted name, its closing quote included.
  const open = bodyStart >= 0 ? "" : openQuoteAt(text, result.context.prefixStart, ctx.dialect);
  const startOffset = open ? result.context.prefixStart - 1
    : bodyStart >= 0 ? Math.min(result.context.prefixStart, dollarWordStart(text, offset, bodyStart)) : result.context.prefixStart;
  const start = model.getPositionAt(startOffset);
  const end = model.getPositionAt(wordEndAt(text, offset, open));
  const insert = new monaco.Range(start.lineNumber, start.column, position.lineNumber, position.column);
  const range = { insert, replace: new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column) };
  const key = tier2Key(model, ctx, text, startOffset);
  const merged = new Map<string, monaco.languages.CompletionItem>();
  // Tier 1 already matched (prefix, camel hump, substring) and ranked these items. Monaco sorts by its own fuzzy score
  // first and uses sortText only for ties, which put `ORDER BY` above `OrderId` for `ORD` and dropped `tblmenu` for
  // `menu`. A filter text that starts with what was typed gives every tier-1 item the same score, so tier 1's order
  // holds. (The text typed includes an opened quote: the range starts there.)
  const typed = text.slice(startOffset, offset);
  for (const i of result.items) {
    const item = toMonaco(i, range, 1);
    if (typed) item.filterText = monacoFilterText(typed, i);
    merged.set(mergeKey(i.kind, i.label), item);
  }
  const t2 = tier2Cache.get(key);
  if (t2) {
    // Merge by name and kind (§9). A name tier 1 already offers keeps tier 1's insert text, which follows the
    // dialect's quoting rules (a reserved word delimited, an alias as written); tier 2 refines the detail.
    for (const i of t2) {
      const k = mergeKey(i.kind, i.label);
      const prev = merged.get(k);
      const next = toMonaco(i, range, 2);
      if (open) next.filterText = open + (next.filterText ?? i.label);
      merged.set(k, prev ? { ...prev, detail: next.detail ?? prev.detail, label: { label: (prev.label as monaco.languages.CompletionItemLabel).label, description: i.detail ?? (prev.label as monaco.languages.CompletionItemLabel).description } } : next);
    }
  } else {
    scheduleTier2(model, ctx, offset, key, new Set(merged.keys()), startOffset);
  }
  for (const item of snippetSuggestions(ctx.dialect, text, result.context.prefixStart, offset, insert)) merged.set(`snippet:${item.filterText}`, item);
  const status = schemaStatusItem(ctx, result.context, text.slice(startOffset, offset), insert);
  if (status) merged.set("status:", status);
  return { suggestions: [...merged.values()], incomplete: true };
}

const NAME_CLAUSES = new Set(["from", "join", "update", "insertInto", "delete", "exec", "insertColumns", "select", "where", "on", "set", "groupBy", "orderBy", "having"]);

/**
 * Why no table or column is listed, as the list's first row: the schema is still loading, failed to load, or the
 * server is disconnected. Accepting it changes nothing (it re-inserts what was typed). Shown only where names belong.
 */
function schemaStatusItem(ctx: DocContext, c: { clause: string; slot?: string }, typed: string, range: monaco.IRange): monaco.languages.CompletionItem | null {
  if (ctx.schema || !NAME_CLAUSES.has(c.clause) || c.slot === "alias" || c.slot === "name") return null;
  const tab = useTabs.getState().tabs[ctx.tabId];
  if (!tab?.profileId) return null;
  let label: string;
  let detail: string;
  if (isDisconnected(tab.profileId)) { label = "Not connected"; detail = "connect to list tables and columns"; }
  else {
    const entry = tabSnapshot(tab);
    if (!tabScope(tab)) return null; // no database picked: nothing to load
    if (entry && entry.status !== "loading" && entry.failedAt) { label = "Schema not loaded"; detail = "the load failed; retrying"; }
    else { label = "Loading schema…"; detail = "tables and columns appear when it arrives"; }
  }
  return {
    label: { label, description: detail }, kind: monaco.languages.CompletionItemKind.Text, detail,
    insertText: typed, filterText: typed || label, sortText: "!", range,
  };
}

/** The cursor is inside a `$tag$ … $tag$` body: the only string a PL/pgSQL tier 2 can answer for ('…' and comments never). */
export function inDollarBody(text: string, offset: number, dialect: Dialect): boolean {
  return dollarBodyStart(text, offset, dialect) >= 0;
}

/** Offset just after the opening `$tag$` of the body holding the cursor, or -1 outside one (or without such bodies). */
function dollarBodyStart(text: string, offset: number, dialect: Dialect): number {
  if (!DIALECTS[dialect].dollarQuotedBodies) return -1;
  const code = getStream(text, dialect).code;
  const t = code[lastStartingBefore(code, offset)];
  if (!t || t.kind !== "string" || !t.tag) return -1;
  return offset >= t.start + t.tag && (t.open ? offset <= t.end : offset <= t.end - t.tag) ? t.start + t.tag : -1;
}

/**
 * Tier 1 reads a PostgreSQL dollar-quoted body as a string; whether it is code (DO / AS $$ … $$) the sidecar's tier 2
 * decides, and it answers nothing for a real string. So the cursor's word is completed from tier 2 alone (§9).
 */
function dollarBodyCompletion(model: monaco.editor.ITextModel, ctx: DocContext, text: string, offset: number, position: monaco.Position): monaco.languages.CompletionList {
  const start = dollarWordStart(text, offset, Math.max(0, dollarBodyStart(text, offset, ctx.dialect)));
  const from = model.getPositionAt(start);
  const range = new monaco.Range(from.lineNumber, from.column, position.lineNumber, position.column);
  const key = tier2Key(model, ctx, text, start);
  const t2 = tier2Cache.get(key);
  if (t2) return { suggestions: t2.map((i) => toMonaco(i, range, 2)), incomplete: true };
  scheduleTier2(model, ctx, offset, key, new Set(), start);
  return { suggestions: [], incomplete: true };
}

/**
 * Start of the PostgreSQL token being typed at `offset` inside a routine body, so the completion replaces it whole:
 * an identifier (Unicode letters included), a positional parameter (`$`, `$1`) or an unterminated quoted identifier (`"Li`).
 */
export function dollarWordStart(text: string, offset: number, bodyStart = 0): number {
  const lineStart = Math.max(bodyStart, text.lastIndexOf("\n", offset - 1) + 1);
  // An open quoted identifier on this line: an odd number of `"` before the cursor.
  const q = text.lastIndexOf('"', offset - 1);
  if (q >= lineStart) {
    let n = 0;
    for (let i = lineStart; i < offset; i++) if (text[i] === '"') n++;
    if (n % 2 === 1) return q;
  }
  const part = (ch: string) => /[\p{L}\p{N}_]/u.test(ch);
  let start = offset;
  while (start > lineStart && part(text[start - 1])) start--;
  // `$` or `$12`: a parameter, unless the `$` closes a dollar-quote tag (`$$`, `$fn$`).
  if (start > lineStart && text[start - 1] === "$" && /^[0-9]*$/.test(text.slice(start, offset)) ) start--;
  return start;
}

/**
 * Library snippets for the tab's dialect (Phase A3), matched by prefix while a word is typed — never after a qualifier
 * (`o.|`). Bodies use Monaco snippet syntax (`${1:name}`). The library loads on first use.
 */
export function snippetSuggestions(dialect: Dialect, text: string, wordStart: number, offset: number, range: monaco.IRange): monaco.languages.CompletionItem[] {
  const lib = useSnippets.getState();
  if (!lib.loaded) void lib.load();
  if (offset <= wordStart || text[wordStart - 1] === ".") return [];
  return snippetsFor(lib.snippets, dialect).map((s) => ({
    label: { label: s.prefix, description: s.title },
    kind: monaco.languages.CompletionItemKind.Snippet,
    detail: s.builtIn ? `${s.title} (built-in snippet)` : `${s.title} (snippet)`,
    documentation: { value: "```sql\n" + s.body + "\n```" },
    insertText: s.body,
    insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
    sortText: `9${s.prefix}`,
    filterText: s.prefix,
    range,
  }));
}

function provideHover(model: monaco.editor.ITextModel, position: monaco.Position): monaco.languages.Hover | null {
  const ctx = docContext(model);
  if (!ctx) return null;
  const offset = model.getOffsetAt(position);
  const r = resolveObjectAt(model.getValue(), offset, ctx.dialect, ctx.schema);
  if (!r) return null;
  const lines: string[] = [];
  if (r.column) {
    const col = r.column.table.columns?.find((c) => c.name.toLowerCase() === r.column!.name.toLowerCase());
    if (!col) return null;
    const t = r.column.table;
    lines.push(...columnLines(`${t.schema}.${t.name}.${col.name}`, col, fkReference(ctx.schema, t.id, col.name)));
  } else if (r.object) {
    const o = r.object;
    lines.push(objectHeader(o));
    if (o.params?.length) {
      lines.push(
        mdFence(
          "sql",
          o.params.map((p) => `${oneLine(p.name)} ${oneLine(p.type)}${p.output ? " OUTPUT" : ""}${p.hasDefault ? " = default" : ""}`).join(",\n") +
            (o.returnType ? `\nRETURNS ${oneLine(o.returnType)}` : ""),
        ),
      );
    } else if (o.returnType) lines.push(`returns ${mdCode(o.returnType)}`);
    if (o.columns?.length) lines.push(...columnsTable(o.columns));
  } else if (r.variable) {
    return null;
  }
  // Untrusted markdown: no command links, no raw HTML (L10); the names in it are escaped above.
  return { contents: lines.map((value) => ({ value, isTrusted: false, supportHtml: false })) };
}

// ---- diagnostics (debounced 300 ms) ----
const diagTimers = new Map<string, ReturnType<typeof setTimeout>>();

function scheduleDiagnostics(tabId: string, model: monaco.editor.ITextModel) {
  clearTimeout(diagTimers.get(tabId));
  diagTimers.set(
    tabId,
    setTimeout(async () => {
      if (model.isDisposed()) return;
      const dialect = dialectOfLanguage(model.getLanguageId());
      if (!dialect || !DIALECTS[dialect].serverDiagnostics) {
        monaco.editor.setModelMarkers(model, "querithm", []);
        return;
      }
      const version = model.getVersionId();
      try {
        const diags = await rpc("lang.diagnostics", { docId: tabId, text: model.getValue(), dialect });
        if (model.isDisposed() || model.getVersionId() !== version) return;
        monaco.editor.setModelMarkers(
          model,
          "querithm",
          diags.map((d) => ({
            startLineNumber: d.line, startColumn: d.column, endLineNumber: d.endLine, endColumn: d.endColumn, message: d.message,
            severity: d.severity === "error" ? monaco.MarkerSeverity.Error : d.severity === "warning" ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Info,
            source: "ScriptDom",
          })),
        );
      } catch {
        /* sidecar unavailable */
      }
    }, 300),
  );
}

/** Formats with the bundled sql-formatter in one of its languages (no network, no sidecar). */
async function formatLocally(text: string, language: "postgresql" | "sqlite" | "mysql" | "duckdb" | "plsql"): Promise<{ text: string; error?: string | null }> {
  const s = useSettings.getState();
  try {
    const { format } = await import("sql-formatter");
    const one = (sql: string) => format(sql, {
      language, keywordCase: s.keywordCase === "preserve" ? "preserve" : s.keywordCase, tabWidth: s.indentSize,
      logicalOperatorNewline: "before",
    });
    // Oracle: statement by statement, so / lines, PL/SQL units and SQL*Plus lines survive (oracleFormat.ts).
    if (language === "plsql") return formatOracleScript(text, one);
    return { text: one(text) };
  } catch (e) {
    return { text, error: e instanceof Error ? e.message : String(e) };
  }
}

/** Formats SQL: T-SQL in the sidecar (ScriptDom); PostgreSQL, SQLite, MySQL, DuckDB and Oracle with the bundled sql-formatter. */
export async function formatText(text: string, dialect: Dialect): Promise<{ text: string; error?: string | null }> {
  const s = useSettings.getState();
  switch (dialect) {
    case "pg": return formatLocally(text, "postgresql");
    case "sqlite": return formatLocally(text, "sqlite");
    case "mySql": return formatLocally(text, "mysql");
    case "duckDb": return formatLocally(text, "duckdb");
    // Oracle: sql-formatter's PL/SQL language on each SQL statement; PL/SQL units and / lines as written (D4 S06).
    case "oracle": return formatLocally(text, "plsql");
    case "tsql":
      return rpc("lang.format", {
        text, dialect, options: { keywordCase: s.keywordCase, indentSize: s.indentSize, leadingCommas: s.leadingCommas, newlineBeforeJoin: s.newlineBeforeJoin },
      });
    default:
      return assertNever(dialect);
  }
}

let installed = false;

export function installProviders() {
  if (installed) return;
  installed = true;
  monaco.editor.registerCommand("querithm.noteUsage", (_accessor, label: string) => noteUsage(label));
  // A schema that arrives (or fails) while the list is open replaces the "Loading schema…" row with the real names.
  useSnapshots.subscribe((s, prev) => {
    if (s.entries === prev.entries) return;
    for (const editor of monaco.editor.getEditors()) {
      if (suggestWidgetVisible(editor)) editor.trigger("schema", "editor.action.triggerSuggest", {});
    }
  });
  for (const lang of DIALECT_LIST.map((d) => DIALECTS[d].language)) {
    monaco.languages.registerCompletionItemProvider(lang, {
      triggerCharacters: [".", " ", "@", "(", ",", "["],
      provideCompletionItems: (model, position, context) => provideCompletion(model, position, context),
    });
    monaco.languages.registerHoverProvider(lang, { provideHover: (model, position) => provideHover(model, position) });
    monaco.languages.registerFoldingRangeProvider(lang, {
      async provideFoldingRanges(model) {
        const ctx = docContext(model);
        if (!ctx) return [];
        try {
          const ranges = await rpc("lang.folding", { docId: ctx.docId ?? ctx.tabId, text: model.getValue(), dialect: ctx.dialect });
          return ranges.map((r) => ({
            start: r.startLine, end: r.endLine,
            kind: r.kind === "comment" ? monaco.languages.FoldingRangeKind.Comment : r.kind === "region" ? monaco.languages.FoldingRangeKind.Region : undefined,
          }));
        } catch {
          return [];
        }
      },
    });
    monaco.languages.registerDocumentFormattingEditProvider(lang, {
      async provideDocumentFormattingEdits(model) {
        const ctx = docContext(model);
        if (!ctx) return [];
        const r = await formatText(model.getValue(), ctx.dialect);
        if (r.error) {
          const { toast } = await import("../store/ui");
          toast("warn", r.error);
          return [];
        }
        return [{ range: model.getFullModelRange(), text: r.text }];
      },
    });
    monaco.languages.registerDocumentRangeFormattingEditProvider(lang, {
      async provideDocumentRangeFormattingEdits(model, range) {
        const ctx = docContext(model);
        if (!ctx) return [];
        const r = await formatText(model.getValueInRange(range), ctx.dialect);
        return r.error ? [] : [{ range, text: r.text }];
      },
    });
    monaco.languages.registerDocumentHighlightProvider(lang, {
      provideDocumentHighlights(model, position) {
        const ctx = docContext(model);
        const dialect = ctx?.dialect ?? dialectForLanguage(lang);
        const text = model.getValue();
        const region = sqlRegionAtOffset(text, model.getOffsetAt(position), dialect);
        const offset = region.offset;
        const stream = getStream(region.text, dialect);
        const T = stream.code;
        const idx = lastStartingBefore(T, offset + 1);
        if (idx < 0) return [];
        const cur = T[idx];
        if (offset < cur.start || offset > cur.end) return [];
        if (cur.kind !== "ident" && cur.kind !== "qident" && cur.kind !== "variable" && cur.kind !== "param") {
          return [];
        }
        // Folding dialects: a name's key is its catalog spelling (a quoted name exact); otherwise names match case-insensitively.
        const folds = DIALECTS[dialect].identifierFold !== null;
        const nameKey = (t: typeof cur) => folds && (t.kind === "ident" || t.kind === "qident") ? foldIdentifier(t.value, t.kind === "qident", dialect) : t.value.toLowerCase();
        const target = nameKey(cur);
        const highlights: monaco.languages.DocumentHighlight[] = [];
        for (const t of T) {
          if (t.kind === "ident" || t.kind === "qident" || t.kind === "variable" || t.kind === "param") {
            if (nameKey(t) === target) {
              const start = model.getPositionAt(t.start + region.base);
              const end = model.getPositionAt(t.end + region.base);
              highlights.push({
                range: new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column),
                kind: monaco.languages.DocumentHighlightKind.Text,
              });
            }
          }
        }
        return highlights;
      },
    });
    monaco.languages.registerCodeActionProvider(lang, {
      provideCodeActions(model, range) {
        const ctx = docContext(model);
        if (!ctx) return { actions: [], dispose() {} };
        const text = model.getValue();
        const offset = model.getOffsetAt({ lineNumber: range.startLineNumber, column: range.startColumn });
        const exp = expandWildcardAtOffset(text, offset, ctx.dialect, ctx.schema);
        if (!exp) return { actions: [], dispose() {} };
        const start = model.getPositionAt(exp.start);
        const end = model.getPositionAt(exp.end);
        const editRange = new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column);
        return {
          actions: [
            {
              title: "Expand '*' to column list",
              kind: "refactor.rewrite",
              edit: {
                edits: [
                  {
                    resource: model.uri,
                    textEdit: {
                      range: editRange,
                      text: exp.text,
                    },
                    versionId: model.getVersionId(),
                  },
                ],
              },
            },
          ],
          dispose() {},
        };
      },
    });
    monaco.languages.registerSignatureHelpProvider(lang, {
      signatureHelpTriggerCharacters: ["(", ",", " "],
      signatureHelpRetriggerCharacters: [","],
      async provideSignatureHelp(model, position, token) {
        const ctx = docContext(model);
        if (!ctx) return null;
        if (token?.isCancellationRequested) return null;
        const version = model.getVersionId();
        const offset = model.getOffsetAt(position);
        const text = model.getValue();

        const t1 = tier1SignatureHelp(text, offset, ctx.dialect, ctx.schema);

        if (t1) {
          return {
            value: {
              signatures: [
                {
                  label: t1.label,
                  documentation: t1.documentation,
                  parameters: t1.parameters,
                },
              ],
              activeSignature: 0,
              activeParameter: t1.activeParameter,
            },
            dispose() {},
          };
        }

        try {
          const help = await rpc("lang.signature", {
            docId: ctx.docId ?? ctx.tabId, text, offset, dialect: ctx.dialect,
            profileId: ctx.profileId ?? null, database: ctx.database ?? null, ...(ctx.sessionId ? { sessionId: ctx.sessionId } : {}),
            ...schemaParam(ctx.profileId),
          });
          if (token?.isCancellationRequested || model.getVersionId() !== version) return null;
          if (help.signatures?.length) {
            return {
              value: {
                signatures: help.signatures.map((s) => ({
                  label: s.label, documentation: s.documentation ?? undefined,
                  parameters: s.parameters.map((p) => ({ label: p.label, documentation: p.documentation ?? undefined })),
                })),
                activeSignature: help.activeSignature,
                activeParameter: help.activeParameter,
              },
              dispose() {},
            };
          }
        } catch {
          /* sidecar unavailable */
        }
        return null;
      },
    });
  }
  onModelContent((tabId, model) => scheduleDiagnostics(tabId, model));
  // M12: the sidecar keeps each document's parse and tokens by docId (the model key); a model that goes away is
  // forgotten there at once instead of waiting for the cache bound. Best effort: a sidecar that is down has nothing cached.
  onModelDisposed((key) => {
    clearTimeout(diagTimers.get(key));
    diagTimers.delete(key);
    cancelTier2(key);
    rpc("lang.forget", { docId: key }).catch(() => {});
  });
  onEditorCreated((editor) => {
    installKeywordCaseOnType(editor);
    addCommentMenu(editor);

    const pairDecos = editor.createDecorationsCollection([]);
    let pairTimer: ReturnType<typeof setTimeout> | undefined;
    const updatePairs = (position: monaco.IPosition) => {
      const model = editor.getModel();
      if (!model) {
        pairDecos.clear();
        return;
      }
      const ctx = docContext(model);
      const dialect = ctx?.dialect ?? dialectForLanguage(model.getLanguageId());
      const offset = model.getOffsetAt(position);
      const text = model.getValue();

      const insertMatch = findInsertMatchingHighlight(text, offset, dialect);
      if (insertMatch) {
        const colStart = model.getPositionAt(insertMatch.col.start);
        const colEnd = model.getPositionAt(insertMatch.col.end);
        const valStart = model.getPositionAt(insertMatch.val.start);
        const valEnd = model.getPositionAt(insertMatch.val.end);
        pairDecos.set([
          {
            range: new monaco.Range(colStart.lineNumber, colStart.column, colEnd.lineNumber, colEnd.column),
            options: { className: "q-insert-match" },
          },
          {
            range: new monaco.Range(valStart.lineNumber, valStart.column, valEnd.lineNumber, valEnd.column),
            options: { className: "q-insert-match" },
          },
        ]);
        return;
      }

      const syntaxPairs = findSyntaxPairHighlight(text, offset, dialect);
      if (syntaxPairs && syntaxPairs.length) {
        const decos = syntaxPairs.map((p) => {
          const start = model.getPositionAt(p.start);
          const end = model.getPositionAt(p.end);
          return {
            range: new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column),
            options: { className: "q-pair-match" },
          };
        });
        pairDecos.set(decos);
        return;
      }

      pairDecos.clear();
    };
    const cancelPairs = () => { clearTimeout(pairTimer); pairDecos.clear(); };
    editor.onDidChangeCursorPosition((e) => {
      clearTimeout(pairTimer);
      pairTimer = setTimeout(() => updatePairs(e.position), 75);
    });
    editor.onDidDispose(cancelPairs);

    editor.onDidChangeModel(() => {
      cancelPairs();
      const m = editor.getModel();
      const tabId = tabIdOfModel(m);
      if (m && tabId) scheduleDiagnostics(tabId, m);
    });
  });
}

export function lastTier1Latency() {
  return lastTier1Ms;
}

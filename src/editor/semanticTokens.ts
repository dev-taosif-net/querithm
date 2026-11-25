// Semantic SQL colours (App Themes §5.3): CTE names and schema prefixes as Monaco semantic tokens, from the tier-1
// analyzer (`semanticSpans`, completion/scanner.ts) — tokens and statements, never a regex over the text, no RPC.
// Monaco asks only while `semanticHighlighting.enabled` is on, from its viewport pass: debounced 100–500 ms and cancelled
// by the next edit (viewportSemanticTokens.js), so never on the keystroke path, and no React state is involved.
import type * as Monaco from "monaco-editor";
import type { Dialect } from "../ipc/contract";
import { semanticSpans, type SemanticKind, type SemanticSpan } from "./completion";
import type { SchemaIndex } from "./completion/types";
import { monaco } from "./monaco";
import { clearSemanticTokens } from "./monacoInternals";
import { useSnapshots } from "../store/snapshots";

/**
 * One type and two modifiers. Monaco styles a token by `_match([type, ...modifiers].join("."))`
 * (standaloneThemeService.js), so the theme rules `identifier.cte` / `identifier.schema` apply, and `identifier` where a
 * palette has neither.
 */
export const SEMANTIC_LEGEND: Monaco.languages.SemanticTokensLegend = { tokenTypes: ["identifier"], tokenModifiers: ["cte", "schema"] };
const MODIFIER_BIT: Record<SemanticKind, number> = { cte: 1 << 0, schema: 1 << 1 };

let enabled = false;
const registrations = new Map<string, Monaco.IDisposable>();
let provider: SemanticTokensProvider | undefined;
let installed = false;

/** Whether the provider answers (Default syntax colours); off until the theme code switches it on. */
export function semanticEnabled(): boolean {
  return enabled;
}

/** Switches the provider on or off. Off drops the tokens already applied, so none outlives the palette that wanted them. */
export function setSemanticEnabled(on: boolean): void {
  const next = on === true;
  if (next === enabled) return;
  enabled = next;
  if (!next) {
    try {
      for (const m of monaco.editor.getModels()) clearSemanticTokens(m);
    } catch {
      /* no editor loaded yet: nothing applied */
    }
  }
  notifySemanticChange();
}

const listeners = new Set<() => void>();
const onDidChange: Monaco.IEvent<void> = (listener, thisArgs?) => {
  const l = () => listener.call(thisArgs, undefined);
  listeners.add(l);
  return { dispose: () => void listeners.delete(l) };
};

/** The provider's model of the document changed without an edit: the cached schema (catalog-confirmed parts) or the gate. */
export function notifySemanticChange(): void {
  for (const l of [...listeners]) {
    try { l(); } catch { /* a listener's failure stays its own */ }
  }
  // Monaco 0.52's viewport pass listens to the provider registry, not to a range provider's onDidChange
  // (viewportSemanticTokens.js), so the same provider is registered again: the public way to make it ask again.
  if (enabled && installed) for (const lang of [...registrations.keys()]) register(lang);
}

/** Monaco's 5-number tokens, `[Δline, Δchar, length, type, modifiers]` per span; a span across a line is dropped. */
export function encodeSemanticSpans(model: Pick<Monaco.editor.ITextModel, "getPositionAt">, spans: readonly SemanticSpan[]): Uint32Array {
  const data = new Uint32Array(spans.length * 5);
  let n = 0, line = 0, char = 0;
  for (const s of spans) {
    const a = model.getPositionAt(s.start);
    const b = model.getPositionAt(s.end);
    if (a.lineNumber !== b.lineNumber || b.column <= a.column) continue;
    const l = a.lineNumber - 1, ch = a.column - 1;
    if (l < line || (l === line && ch < char)) continue; // out of order: Monaco reads relative positions
    data[n++] = l - line;
    data[n++] = l === line ? ch - char : ch;
    data[n++] = b.column - a.column;
    data[n++] = 0;
    data[n++] = MODIFIER_BIT[s.kind];
    line = l;
    char = ch;
  }
  return n === data.length ? data : data.slice(0, n);
}

/** What the provider needs to know about a model: `docContext` (providers.ts), or null for a model of no tab. */
export type SemanticContextOf = (model: Monaco.editor.ITextModel) => { dialect: Dialect; schema: SchemaIndex | null } | null;

export type SemanticTokensProvider = Monaco.languages.DocumentRangeSemanticTokensProvider & { onDidChange: Monaco.IEvent<void> };

export function semanticTokensProvider(contextOf: SemanticContextOf): SemanticTokensProvider {
  return {
    onDidChange,
    getLegend: () => SEMANTIC_LEGEND,
    provideDocumentRangeSemanticTokens(model, range) {
      if (!enabled) return null;
      const ctx = contextOf(model);
      if (!ctx) return null;
      // Only the statements overlapping the range are analysed, at most SEMANTIC_MAX_STATEMENTS (scanner.ts).
      const from = model.getOffsetAt({ lineNumber: range.startLineNumber, column: range.startColumn });
      const to = model.getOffsetAt({ lineNumber: range.endLineNumber, column: range.endColumn });
      return { data: encodeSemanticSpans(model, semanticSpans(model.getValue(), ctx.dialect, from, to, ctx.schema)) };
    },
  };
}

function register(lang: string): void {
  if (!provider) return;
  registrations.get(lang)?.dispose();
  registrations.set(lang, monaco.languages.registerDocumentRangeSemanticTokensProvider(lang, provider));
}

/**
 * Whether a change of the cached schemas can change what the provider answers: a schema index replaced, added or
 * dropped. The store replaces an entry on every status move (loading, refreshing, error, notes) but keeps its index
 * object then (snapshots.ts), so those ask Monaco nothing again: each refresh re-registers every SQL language.
 */
export function indexesChanged(next: Readonly<Record<string, { index?: unknown }>>, prev: Readonly<Record<string, { index?: unknown }>>): boolean {
  if (next === prev) return false;
  for (const k in next) if (next[k]?.index !== prev[k]?.index) return true;
  for (const k in prev) if (!(k in next) && prev[k]?.index) return true;
  return false;
}

/** Registers the provider for each SQL language (once, from installProviders); a schema that arrives refreshes it. */
export function installSemanticTokens(languages: readonly string[], contextOf: SemanticContextOf): void {
  if (installed) return;
  installed = true;
  provider = semanticTokensProvider(contextOf);
  for (const lang of languages) register(lang);
  useSnapshots.subscribe((s, prev) => { if (indexesChanged(s.entries, prev.entries)) notifySemanticChange(); });
}

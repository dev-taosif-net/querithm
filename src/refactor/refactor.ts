// Find usages and rename: the pure parts — grouping hits, local renames, labels.
import type { Dialect, FindUsagesResult, ObjectKind, RefactorKind, RefactorTarget, TextEdit, UsageHit } from "../ipc/contract";
import { DELIMIT, DIALECTS } from "../engines";
import { reservedWords } from "../editor/completion/keywords";

export interface HitGroup {
  key: string;
  label: string;
  /** An open tab or a database object. */
  source: "tab" | "object";
  hits: UsageHit[];
}

/** Open tabs first, then database objects by name; hits by line within each. */
export function groupHits(r: FindUsagesResult, tabTitle: (id: string) => string): HitGroup[] {
  const groups = new Map<string, HitGroup>();
  for (const h of r.hits) {
    const key = h.tabId ? `t:${h.tabId}` : `o:${h.object?.objectId ?? `${h.object?.schema}.${h.object?.name}`}`;
    let g = groups.get(key);
    if (!g) {
      g = { key, source: h.tabId ? "tab" : "object", label: h.tabId ? tabTitle(h.tabId) : `${h.object?.schema}.${h.object?.name}`, hits: [] };
      groups.set(key, g);
    }
    g.hits.push(h);
  }
  const list = [...groups.values()];
  for (const g of list) g.hits.sort((a, b) => a.line - b.line || a.column - b.column);
  return list.sort((a, b) => (a.source === b.source ? a.label.localeCompare(b.label) : a.source === "tab" ? -1 : 1));
}

export function targetLabel(t: RefactorTarget): string {
  return t.kind === "column" ? `${t.schema}.${t.name}.${t.column}` : `${t.schema}.${t.name}`;
}

export function kindLabel(k: RefactorKind): string {
  return k === "procedure" ? "procedure" : k;
}

/** The explorer's object kind as a refactor target kind. */
export function targetKindOf(kind: ObjectKind): RefactorKind {
  switch (kind) {
    case "view": return "view";
    case "proc": return "procedure";
    case "func": case "tvf": return "function";
    case "type": return "type";
    case "sequence": return "sequence";
    default: return "table";
  }
}

/** A local symbol (alias, CTE, column alias) that reads the same without delimiters. */
const PLAIN_LOCAL: Record<Dialect, RegExp> = {
  tsql: /^[A-Za-z_][A-Za-z0-9_]*$/, pg: DIALECTS.pg.regularIdentifier, sqlite: DIALECTS.sqlite.regularIdentifier,
  mySql: DIALECTS.mySql.regularIdentifier, duckDb: DIALECTS.duckDb.regularIdentifier, oracle: DIALECTS.oracle.regularIdentifier,
};

/**
 * A local symbol's new text: a T-SQL variable keeps its @, a bracketed or quoted name stays so; anything that is not a
 * plain identifier, or is a reserved word of the dialect (the same table quoteIdentifier reads), is delimited the
 * dialect's way (engines.ts: bracketed in T-SQL, quoted in PostgreSQL, SQLite and DuckDB, backticked in MySQL).
 */
export function localRenameText(original: string, newName: string, dialect: Dialect): string {
  const name = newName.trim();
  if (original.startsWith("@")) return name.startsWith("@") ? name : `@${name}`;
  if (original.startsWith("[")) return DELIMIT.bracket(name);
  if (original.startsWith('"')) return DELIMIT.doubleQuote(name);
  if (original.startsWith("`")) return DELIMIT.backtick(name);
  if (PLAIN_LOCAL[dialect].test(name) && !reservedWords(dialect).has(name.toUpperCase())) return name;
  return DIALECTS[dialect].quote(name);
}

/** Applies edits from the end so earlier offsets stay valid (the editor applies the same edits as one undo step). */
export function applyEdits(text: string, edits: TextEdit[]): string {
  let out = text;
  for (const e of [...edits].sort((a, b) => b.offset - a.offset)) out = out.slice(0, e.offset) + e.text + out.slice(e.offset + e.length);
  return out;
}

export function counts(hits: UsageHit[]) {
  return { exact: hits.filter((h) => h.confidence === "exact").length, possible: hits.filter((h) => h.confidence === "possible").length };
}

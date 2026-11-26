// Tier-1 completion (§9 context table). Synchronous, from the cached SchemaIndex; never throws.
import type { CompletionKind, Dialect, ObjectKind, SnapColumn, SnapFk, SnapObject } from "../../ipc/contract";
import type { CompletionRequest, CompletionResult, CursorContext, RowInfo, SchemaIndex, TableInScope, Tier1Item } from "./types";
import { lastStartingBefore, neverAlias, scan } from "./scanner";
import { findObject, fkLookup, fkReference, isDefaultSchema, resolveTable, synonymTarget } from "./schemaIndex";
import { columnLines, columnsTable, mdCode, mdText, objectHeader } from "../hoverMarkdown";
import { bestMatch, MATCH_NONE, MATCH_PREFIX, matchRankLower, sortText } from "./rank";
import { suggestAlias } from "./alias";
import { firstEndingAfter, getStream, type Token } from "./lexer";
import {
  afterReferenceKeywords, argValueKeywords, builtinFunctions, clauseKeywords, dataTypes, declareKeywords,
  execArgsKeywords, niladicFunctions, reservedWords, statementStartKeywords, tableContextKeywords, valuesKeywords,
} from "./keywords";
import { DIALECTS, foldIdentifier, knownDialect } from "../../engines";

const MAX_ITEMS = 500;

/**
 * A name from the source text that may have been written without delimiters (letters, digits, _, $, #, and a T-SQL
 * `#temp` / `@tableVar` start). Tested on one identifier's value, never on SQL text.
 */
const SOURCE_NAME = /^[\p{L}_#@][\p{L}\p{N}_$#@]*$/u;

/** Keywords that can be the last word of a statement (`... IS NULL`, `ORDER BY x DESC`, `CASE ... END`). */
/** Qualifiers that name the row a DML statement writes (lower case). Oracle and MySQL have none outside triggers. */
const PSEUDO_TABLES: Record<Dialect, Set<string>> = {
  tsql: new Set(["inserted", "deleted"]),
  pg: new Set(["excluded"]),
  sqlite: new Set(["excluded"]),
  duckDb: new Set(["excluded"]),
  mySql: new Set(),
  oracle: new Set(),
};

const STATEMENT_END_WORDS =new Set(["NULL", "TRUE", "FALSE", "END", "ASC", "DESC", "DEFAULT", "ONLY", "CURRENT_TIMESTAMP", "CURRENT_DATE", "CURRENT_USER"]);

interface Cand {
  label: string;
  /** Shown instead of the label (Tier1Item.display). */
  display?: string;
  /** Built from a snapshot object (Tier1Item.schemaObject). */
  schemaObject?: boolean;
  kind: CompletionKind;
  detail?: string;
  insertText?: string;
  isSnippet?: boolean;
  filterText?: string;
  documentation?: string;
  /**
   * Documentation built only for the items the list keeps (MAX_ITEMS): an object's column table costs more than the
   * rest of its item, and FROM on a large schema has thousands of candidates.
   */
  doc?: () => string;
  group: number;
  order?: number;
  /** Strings the prefix is matched against (default: [label]). */
  match?: string[];
  /** Lowercase keys looked up in `usage` (default: [label]). */
  usageKeys?: string[];
  /** Alias appended to insertText (auto-alias). */
  alias?: string;
  /**
   * The name an auto-alias is generated from, once the item is kept (finalize): generating one for each of 5,000
   * FROM candidates cost about 3 ms a keystroke (2026-11-25 profile), for the 500 the list keeps.
   */
  aliasFor?: string;
  /**
   * The snapshot object an object item stands for (objectCand). Ranked on its label and names alone; its detail,
   * insert text and documentation are made in finalize for the items the list keeps (fillObject). Each object gives one
   * candidate per list, so it is never de-duplicated.
   */
  obj?: SnapObject;
  /** `obj` is named without its schema (a schema was typed before it). */
  bare?: boolean;
  /** The match rank, when the loop that made the candidate computed it already. */
  m?: number;
}

/** A ranked candidate: the sortText fields as numbers (k1, k2) and the lower-case label that breaks their ties. */
interface Scored { c: Cand; k1: number; k2: number; low: string; tier: number; m: number; u: number; rest: number }

/**
 * Lower-case forms of an object's name and `schema.name`, kept per snapshot object (an indexed snapshot never changes:
 * a refresh builds new objects). Ranking and usage look them up for every object on every keystroke.
 */
const OBJ_KEYS = new WeakMap<SnapObject, { name: string; qualified: string }>();
function objKeys(o: SnapObject): { name: string; qualified: string } {
  let k = OBJ_KEYS.get(o);
  if (!k) {
    k = { name: o.name.toLowerCase(), qualified: `${o.schema}.${o.name}`.toLowerCase() };
    OBJ_KEYS.set(o, k);
  }
  return k;
}

/**
 * Items whose documentation finalize builds: the visible rows and a margin. The rest carry it as a thunk that the
 * provider's resolveCompletionItem runs when Monaco shows that item's details (completion audit 2026-11-25, H9).
 */
const EAGER_DOCS = 50;

/**
 * Whether a name needs delimiters, per dialect: depends on the name and the dialect only, and is asked for every
 * candidate on every keystroke (about 1.3 ms of a `FROM |` on 5,000 tables, 2026-11-25 profile). Bounded: cleared at 50,000.
 */
const QUOTE_MEMO: Partial<Record<Dialect, Map<string, boolean>>> = {};

/** A column; `fromSource`: named in the statement text (a CTE or derived table), `quoted` there (TableInScope.quotedColumns). */
interface ColInfo { name: string; col?: SnapColumn; fromSource?: boolean; quoted?: boolean }

const KIND_SETS = {
  from: new Set<ObjectKind>(["table", "view", "tvf", "synonym"]),
  dml: new Set<ObjectKind>(["table", "view", "synonym"]),
  procs: new Set<ObjectKind>(["proc"]),
  callable: new Set<ObjectKind>(["proc", "func"]),
};

const OBJECT_CLAUSES = new Set(["from", "join", "update", "insertInto", "delete", "exec", "insertColumns", "use"]);

export function emptyResultContext(offset: number): CursorContext {
  return {
    clause: "unknown", statementStart: offset, statementEnd: offset, tablesInScope: [], ctes: [], variables: [],
    prefix: "", prefixStart: offset, inStringOrComment: false,
  };
}


interface CompletionRules {
  /** Names inserted without delimiters (a T-SQL `@parameter` too); a reserved word is always delimited. */
  plainName: RegExp;
  /** Scalar functions are inserted schema-qualified (T-SQL calls a scalar UDF only by its two-part name). */
  qualifyScalarFunctions: boolean;
  /** The procedure-call statement template; null without stored procedures (SQLite). */
  call: { label: string; insertText: string; filter: string; match: string[] } | null;
  /**
   * Named arguments: `name => value` (PostgreSQL), `@name = value [OUTPUT]` (T-SQL), or none, where arguments are
   * positional only (MySQL; SQLite has no procedures) and no parameter names are offered.
   */
  namedArguments: "arrow" | "assign" | null;
}

const RULES: Record<Dialect, CompletionRules> = {
  tsql: {
    plainName: /^[A-Za-z_#@][A-Za-z0-9_@#$]*$/,
    qualifyScalarFunctions: true,
    call: { label: "EXEC …", insertText: "EXEC ${1:procedure_name} $0;", filter: "EXEC", match: ["EXEC", "EXECUTE"] },
    namedArguments: "assign",
  },
  pg: {
    plainName: DIALECTS.pg.regularIdentifier,
    qualifyScalarFunctions: false,
    call: { label: "CALL …", insertText: "CALL ${1:procedure_name}($0);", filter: "CALL", match: ["CALL"] },
    namedArguments: "arrow",
  },
  sqlite: {
    plainName: DIALECTS.sqlite.regularIdentifier,
    qualifyScalarFunctions: false,
    call: null,
    namedArguments: null,
  },
  mySql: {
    // User variables (`@x`, `@@x`) are inserted as written.
    plainName: /^@{0,2}[A-Za-z_$][A-Za-z0-9_$]*$/,
    qualifyScalarFunctions: false,
    call: { label: "CALL …", insertText: "CALL ${1:procedure_name}($0);", filter: "CALL", match: ["CALL"] },
    namedArguments: null,
  },
  duckDb: {
    plainName: DIALECTS.duckDb.regularIdentifier,
    qualifyScalarFunctions: false,
    // No stored procedures (macros are functions); CALL runs table functions.
    call: null,
    namedArguments: null,
  },
  // Phase D4 placeholder (hidden engine): no procedure call snippet until S06; PL/SQL names arguments with =>.
  oracle: {
    plainName: DIALECTS.oracle.regularIdentifier,
    qualifyScalarFunctions: false,
    call: null,
    namedArguments: "arrow",
  },
};
/** Tier-1 completion items for the cursor in `req` (§9). Never throws. */
export function complete(req: CompletionRequest): CompletionResult {
  const dialect: Dialect = knownDialect(req?.dialect);
  const text = typeof req?.text === "string" ? req.text : "";
  const offset = Math.max(0, Math.min(Number.isFinite(req?.offset) ? req.offset : 0, text.length));
  let ctx: CursorContext;
  try {
    ctx = scan(text, offset, dialect);
  } catch {
    ctx = emptyResultContext(offset);
  }
  // Inside or right after a number (`> 1.|`) no list opens (audit H8), so none is built.
  if (ctx.inStringOrComment || ctx.inNumber) return { context: ctx, items: [] };
  let items: Tier1Item[] = [];
  try {
    // Inside a PostgreSQL AS / DO $$ body the scanner read the body's own tokens (scanBody); so does the builder, whose
    // checks (an opened quote, a free alias slot, a statement starting at the prefix) found the body as one string token
    // of the outer text (code audit 2026-11-26, M15).
    const region = ctx.body ? { text: ctx.body.text, offset: offset - ctx.body.base, base: ctx.body.base } : { text, offset, base: 0 };
    items = new Builder(ctx, req.schema ?? null, dialect, region, req.usage, req.autoAlias !== false, req.rowCounts).build();
  } catch {
    items = [];
  }
  return { context: ctx, items };
}

/** Characters that open a quoted identifier per dialect (the lexer reads what follows as one name). */
const OPEN_QUOTES: Record<Dialect, string> = { tsql: '["', sqlite: '["`', mySql: "`", pg: '"', duckDb: '"', oracle: '"' };
const CLOSE_QUOTE: Record<string, string> = { "[": "]", '"': '"', "`": "`" };

/**
 * The opening quote just before the prefix (`[Ord|`, `"Ord|`, a backtick), or "" when the prefix is not quoted. The
 * character alone does not tell: right after `"orders"|` the quote before the cursor closes a name (a `"` or backtick
 * opens and closes alike), so the delimited name's token must start there.
 */
export function openQuoteAt(text: string, prefixStart: number, dialect: Dialect): string {
  return openQuoteToken(text, prefixStart, dialect) ? text[prefixStart - 1] : "";
}

/**
 * The delimited name opened just before the prefix (openQuoteAt), as the lexer reads it: `[Ord|]]x]` is one name up to
 * its last `]`, a doubled closing quote inside it included. Undefined when the prefix is not quoted.
 */
export function openQuoteToken(text: string, prefixStart: number, dialect: Dialect): Token | undefined {
  const c = prefixStart > 0 ? text[prefixStart - 1] : "";
  if (!c || !OPEN_QUOTES[dialect].includes(c)) return undefined;
  const T = getStream(text, dialect).code;
  const t = T[lastStartingBefore(T, prefixStart)];
  return t !== undefined && t.kind === "qident" && t.start === prefixStart - 1 ? t : undefined;
}

/**
 * The filter text the editor gives a tier-1 item: what was typed, then the item's own filter text. Monaco ranks by its
 * fuzzy score before sortText; the same score for every item keeps tier 1's ranking (providers.ts).
 */
export function monacoFilterText(typed: string, item: Pick<Tier1Item, "label" | "filterText">): string {
  return typed + (item.filterText ?? item.label);
}

/**
 * What the list shows right of an object's name (§9): its schema, then its kind unless it is a table (the icon says
 * so), then its columns and rows or what it returns: `sales · 42 cols · ~12k rows`, `sales · view · 8 cols`,
 * `dbo · function → int`. `withSchema` false where the list holds one schema only (the user typed `sales.`).
 */
export function objectDescription(o: SnapObject, rowCounts?: Map<number, RowInfo>, withSchema = true): string {
  const k = o.kind === "table" ? "" : o.kind === "tvf" ? "table function" : o.kind === "func" ? "function" : o.kind === "proc" ? "procedure" : o.kind;
  let s = withSchema ? o.schema : "";
  if (k) s = s ? `${s} · ${k}` : k;
  if (o.returnType) return `${s} → ${o.returnType}`;
  const n = o.columns?.length && (o.kind === "table" || o.kind === "view" || o.kind === "synonym") ? o.columns.length : 0;
  if (n) {
    const cols = n === 1 ? "1 col" : `${n} cols`;
    s = s ? `${s} · ${cols}` : cols;
  }
  const rc = o.kind === "table" ? rowCounts?.get(o.id) : undefined;
  if (rc) {
    const rows = rc.rows == null ? "rows n/a" : `${rc.estimated ? "~" : ""}${formatRows(rc.rows)} ${rc.rows === 1 ? "row" : "rows"}`;
    s = s ? `${s} · ${rows}` : rows;
  }
  return s || "table";
}

/** The snapshot object kind each object completion kind stands for. */
const ITEM_OBJECT_KIND: Partial<Record<CompletionKind, ObjectKind>> = {
  table: "table", view: "view", synonym: "synonym", tableFunction: "tvf", proc: "proc", func: "func", type: "type",
};

/**
 * The snapshot object a tier-2 item names, found by its label as tier 2 writes it — `schema.name`, or the bare name of
 * a default-schema object — and its kind; undefined for anything else (a delimited name, a temp table). A bare function
 * name is never looked up: it may be a built-in of the same name.
 */
export function objectForItem(idx: SchemaIndex, kind: CompletionKind, label: string): SnapObject | undefined {
  const want = ITEM_OBJECT_KIND[kind];
  if (!want) return undefined;
  const lower = label.toLowerCase();
  const q = idx.byQualified.get(lower);
  if (q) return q.kind === want ? q : undefined;
  if (want === "func") return undefined;
  return idx.byName.get(lower)?.find((o) => o.kind === want && isDefaultSchema(idx, o.schema));
}

/** A row count for an item's description: `1234` → `1.2k`, `5600000` → `5.6M`. */
export function formatRows(n: number): string {
  for (const [v, u] of [[1e9, "B"], [1e6, "M"], [1e3, "k"]] as const) {
    if (n >= v) { const x = n / v; return `${x < 10 ? x.toFixed(1).replace(/\.0$/, "") : Math.round(x)}${u}`; }
  }
  return String(n);
}

/** The closing quote matching `open`. */
export function closeQuoteOf(open: string): string {
  return CLOSE_QUOTE[open] ?? "";
}

/** `name` delimited with the quote style the user opened (its closing character doubled inside). */
function quoteWith(open: string, name: string): string {
  const close = CLOSE_QUOTE[open];
  return `${open}${name.split(close).join(close + close)}${close}`;
}

const escSnippet = (s: string): string => s.replace(/[\\$}]/g, "\\$&");
const eq = (a: string | undefined, b: string | undefined): boolean => (a ?? "").toLowerCase() === (b ?? "").toLowerCase();

/** A column's one-line description: type, nullability, key and generation flags, and the table an FK column references. */
export function columnDetail(c: SnapColumn, references?: string): string {
  let d = `${c.type} ${c.nullable ? "NULL" : "NOT NULL"}`;
  if (c.pk) d += " PK";
  if (references) d += ` FK→${references}`;
  if (c.identity) d += " IDENTITY";
  if (c.computed) d += " COMPUTED";
  return d;
}

/** `schema.table.column` (or `schema.table`, no referenced column known) → `table`, short enough for the list. */
function refName(fk: string | undefined): string | undefined {
  if (!fk) return undefined;
  const parts = fk.split(".");
  return parts.length >= 3 ? parts[parts.length - 2] : parts.length === 2 ? parts[1] : fk;
}

/** Columns of the engine's built-in row sources (table functions the snapshot does not list). */
const BUILTIN_ROW_SOURCES: Partial<Record<Dialect, Record<string, string[] | "alias">>> = {
  sqlite: {
    pragma_table_info: ["cid", "name", "type", "notnull", "dflt_value", "pk"],
    pragma_table_xinfo: ["cid", "name", "type", "notnull", "dflt_value", "pk", "hidden"],
    pragma_index_list: ["seq", "name", "unique", "origin", "partial"],
    pragma_index_info: ["seqno", "cid", "name"],
    pragma_foreign_key_list: ["id", "seq", "table", "from", "to", "on_update", "on_delete", "match"],
    json_each: ["key", "value", "type", "atom", "id", "parent", "fullkey", "path"],
    json_tree: ["key", "value", "type", "atom", "id", "parent", "fullkey", "path"],
  },
  // A set-returning function with a table alias and no column list names its one column after the alias.
  pg: { generate_series: "alias", unnest: "alias", generate_subscripts: "alias", json_each: ["key", "value"], jsonb_each: ["key", "value"], json_each_text: ["key", "value"], jsonb_each_text: ["key", "value"] },
  duckDb: { generate_series: ["generate_series"], range: ["range"], unnest: "alias" },
  oracle: { dual: ["DUMMY"] },
};

function builtinRowSource(dialect: Dialect, t: TableInScope): string[] | undefined {
  // An own property only: a table named `constructor` or `__proto__` found Object.prototype's, and completion threw (H11).
  const map = BUILTIN_ROW_SOURCES[dialect], key = t.name.toLowerCase();
  const spec = map && Object.hasOwn(map, key) ? map[key] : undefined;
  if (spec === undefined) return undefined;
  if (spec === "alias") return [t.alias ?? t.name];
  return spec;
}

/**
 * A foreign key a JOIN condition can be written from: every column pair known on both sides. SQLite's
 * `REFERENCES customers` without a column list may arrive without the referenced column, which wrote
 * `o.customer_id = c.""` (completion audit 2026-11-25, M26).
 */
function usableFk(fk: SnapFk): boolean {
  return !!fk.columns?.length && fk.columns.every((p) => !!p.parent && !!p.referenced);
}

/** The one key column of a table with a single-column primary key. */
function singleKey(o: SnapObject): string | undefined {
  const pks = (o.columns ?? []).filter((c) => c.pk);
  return pks.length === 1 ? pks[0].name : undefined;
}

/** Lower-case forms a table can be named for an entity (`customer` → customer, customers, tbl_customers …). */
function entityNames(base: string): string[] {
  const b = base.toLowerCase();
  const plural = [b, `${b}s`, `${b}es`, b.endsWith("y") ? `${b.slice(0, -1)}ies` : ""].filter(Boolean);
  return plural.flatMap((x) => [x, `tbl${x}`, `tbl_${x}`]);
}

interface NameJoins {
  /** Tables whose key a column of this name refers to. */
  keyFor(column: string): { obj: SnapObject; key: string }[];
  /** Columns of other tables that refer to `o`'s key `pk`. */
  referencing(o: SnapObject, pk: string): { obj: SnapObject; column: string }[];
}

/**
 * Lookups for name-based JOIN items, built once per schema snapshot: tables by their single key column's name and by
 * their (lower-case) name, and non-key columns by name. Tested on identifier strings only, never on SQL text.
 */
const nameJoinCache = new WeakMap<SchemaIndex, NameJoins>();
function nameJoinIndex(idx: SchemaIndex): NameJoins {
  const hit = nameJoinCache.get(idx);
  if (hit) return hit;
  const byKey = new Map<string, { obj: SnapObject; key: string }[]>();
  const byName = new Map<string, SnapObject>();
  const nonKey = new Map<string, { obj: SnapObject; column: string }[]>();
  for (const o of idx.objects) {
    if (o.kind !== "table") continue;
    const key = singleKey(o);
    if (key) {
      const k = key.toLowerCase();
      if (!byKey.has(k)) byKey.set(k, []);
      byKey.get(k)!.push({ obj: o, key });
      if (!byName.has(o.name.toLowerCase())) byName.set(o.name.toLowerCase(), o);
    }
    for (const c of o.columns ?? []) {
      if (c.pk) continue;
      const k = c.name.toLowerCase();
      if (!nonKey.has(k)) nonKey.set(k, []);
      nonKey.get(k)!.push({ obj: o, column: c.name });
    }
  }
  const ID = /^(.+?)_?id$/i;
  const result: NameJoins = {
    keyFor(column) {
      const out = [...(byKey.get(column.toLowerCase()) ?? [])];
      const m = ID.exec(column);
      if (m && m[1]) {
        for (const n of entityNames(m[1])) {
          const o = byName.get(n);
          const key = o && singleKey(o);
          if (o && key && /^id$/i.test(key) && !out.some((x) => x.obj.id === o.id)) out.push({ obj: o, key });
        }
      }
      return out;
    },
    referencing(o, pk) {
      const out = [...(nonKey.get(pk.toLowerCase()) ?? [])];
      if (/^id$/i.test(pk)) {
        // Customers.Id ← Orders.CustomerId / orders.customer_id
        const singular = o.name.replace(/^tbl_?/i, "").replace(/ies$/i, "y").replace(/e?s$/i, "");
        for (const form of [`${singular}id`, `${singular}_id`]) for (const x of nonKey.get(form.toLowerCase()) ?? []) out.push(x);
      }
      return out;
    },
  };
  nameJoinCache.set(idx, result);
  return result;
}

class Builder {
  private readonly cands: Cand[] = [];
  /**
   * The user typed an opening quote before the prefix: names are inserted fully delimited in that style, and the
   * replace range (providers.ts) starts at the quote.
   */
  private readonly openQuote: string;
  private readonly typedOpenQuote: boolean;
  private readonly reserved: Set<string>;
  /** The typed word in lower case, for matching thousands of names without lower-casing it again (objectMatch). */
  private readonly prefixLower: string;
  /** Aliases to avoid for an auto-aliased FROM/JOIN reference; null when no alias is appended here. */
  private readonly aliasTaken: string[] | null;
  /** The statement may end before the prefix, so the prefix may start the next one (`FROM t emp\nsele`). */
  private readonly nextStatement: boolean;

  constructor(
    private readonly ctx: CursorContext,
    private readonly idx: SchemaIndex | null,
    private readonly dialect: Dialect,
    /** The text the cursor's code is in (a PostgreSQL body's own, at `base` in the document) and the cursor in it. */
    region: { text: string; offset: number; base: number },
    private readonly usage?: Map<string, number>,
    autoAlias = false,
    private readonly rowCounts?: Map<number, RowInfo>,
  ) {
    const { text, base } = region;
    this.openQuote = openQuoteAt(text, ctx.prefixStart - base, dialect);
    this.typedOpenQuote = this.openQuote !== "";
    this.reserved = reservedWords(dialect);
    this.prefixLower = ctx.prefix.toLowerCase();
    this.aliasTaken = autoAlias && this.aliasSlotFree(text, region.offset)
      ? ctx.tablesInScope.flatMap((t) => (t.alias ? [t.alias, t.name] : [t.name]))
      : null;
    this.nextStatement = this.mayStartStatement(text, base);
  }

  /**
   * True when a new statement can begin at the prefix without ';' (both dialects split there, §9): paren depth 0 in
   * the current statement, the previous token can end a statement, and the user typed a prefix or started a new line.
   */
  private mayStartStatement(text: string, base: number): boolean {
    const { clause, slot, prefix } = this.ctx;
    const prefixStart = this.ctx.prefixStart - base, statementStart = this.ctx.statementStart - base;
    if (slot !== undefined && slot !== "afterReference") return false;
    if (clause === "update" || clause === "insertInto" || clause === "insertColumns") return false;
    if (/^[@:$]/.test(prefix)) return false;
    const T = getStream(text, this.dialect).code;
    let depth = 0;
    let prev: Token | undefined;
    // The statement's tokens only (found by binary search): a walk from the script's first token cost a 12,000-line
    // script's every token per keystroke (completion audit M16).
    for (let i = lastStartingBefore(T, statementStart) + 1; i < T.length; i++) {
      const t = T[i];
      if (t.start >= prefixStart) break;
      if (t.start < statementStart) continue;
      if (t.kind === "punct" && t.value === "(") depth++;
      else if (t.kind === "punct" && t.value === ")") depth = Math.max(0, depth - 1);
      prev = t;
    }
    if (!prev || depth > 0) return false;
    if (!prefix && !text.slice(prev.end, prefixStart).includes("\n")) return false;
    switch (prev.kind) {
      case "ident": case "qident": case "number": case "string": case "variable": case "param": return true;
      case "punct": return prev.value === ")";
      case "operator": return prev.value === "*";
      case "keyword": return STATEMENT_END_WORDS.has(prev.upper);
      default: return false;
    }
  }

  /** FROM/JOIN reference with nothing after the cursor that already continues it (an alias, AS, '.', '('). */
  private aliasSlotFree(text: string, offset: number): boolean {
    const { clause, slot } = this.ctx;
    if ((clause !== "from" && clause !== "join") || slot !== undefined || this.typedOpenQuote) return false;
    if (this.ctx.prefix.startsWith("@")) return false;
    const code = getStream(text, this.dialect).code;
    const next = code[firstEndingAfter(code, offset)];
    if (!next) return true;
    if (next.start < offset) return false; // cursor inside a word
    // A clause word the dialect does not reserve (Oracle's JOIN, LEFT, …) is lexed as a name, and is no alias (L10).
    if ((next.kind === "ident" && !neverAlias(next, this.dialect)) || next.kind === "qident") return false;
    if (next.kind === "keyword" && next.upper === "AS") return false;
    return !(next.kind === "punct" || next.kind === "operator") || (next.value !== "." && next.value !== "(");
  }

  /** Append a generated alias to a FROM/JOIN reference's insert text (generated in finalize, for the kept items only). */
  private withAlias(c: Cand, name: string): Cand {
    if (this.aliasTaken) c.aliasFor = name;
    return c;
  }

  build(): Tier1Item[] {
    const { clause, slot, qualifier } = this.ctx;
    if (slot === "alias" || slot === "name") return [];
    let objectContext = OBJECT_CLAUSES.has(clause) && slot !== "afterReference";
    if (qualifier !== undefined) {
      this.qualified(qualifier);
      objectContext = true;
    } else {
      switch (clause) {
        case "from": case "join": case "update": case "insertInto": case "delete":
          if (slot === "afterReference") this.keywords(afterReferenceKeywords(clause, this.dialect), 1);
          else {
            this.tableObjects();
            if (this.ctx.prefix.startsWith("@")) this.variables(4);
          }
          break;
        case "select": case "where": case "on": case "groupBy": case "having": case "orderBy": case "set":
          this.expression(clause);
          break;
        case "exec": this.procs(); break;
        case "execArgs": if (slot === "argValue") this.argValue(); else this.execArgs(); break;
        case "declare": this.types(); break;
        case "use": this.databases(); break;
        case "insertColumns": this.insertColumns(); break;
        case "values": if (slot === "argValue") this.argValue(); else this.values(); break;
        default:
          this.statementTemplates();
          this.keywords(statementStartKeywords(this.dialect), 8);
          if (/^[@:$]/.test(this.ctx.prefix)) this.variables(1);
          break;
      }
      if (this.nextStatement && clause !== "unknown") {
        this.statementTemplates();
        this.keywords(statementStartKeywords(this.dialect), 9);
      }
    }
    return this.finalize(objectContext);
  }

  // ---- naming ----

  private needsQuote(name: string): boolean {
    if (!name) return false;
    const memo = (QUOTE_MEMO[this.dialect] ??= new Map());
    let v = memo.get(name);
    if (v === undefined) {
      // SQLite's own schema names: TEMP is a keyword, but `temp.t` is how a temporary table is named.
      v = this.dialect === "sqlite" && (name === "temp" || name === "main") ? false
        : !RULES[this.dialect].plainName.test(name) || this.reserved.has(name.toUpperCase());
      if (memo.size >= 50_000) memo.clear();
      memo.set(name, v);
    }
    return v;
  }

  /** A catalog name as inserted: delimited when the dialect needs it, or in the quote style the user opened. */
  private q(name: string): string {
    if (this.typedOpenQuote) return quoteWith(this.openQuote, name);
    if (!this.needsQuote(name)) return name;
    return DIALECTS[this.dialect].quote(name);
  }

  /**
   * A column as inserted. One the statement itself names (a CTE's or derived table's) is written as it is there on
   * engines that fold unquoted names: by catalog rules Oracle's unquoted `id` became `"id"` and PostgreSQL's
   * `CustomerId` became `"CustomerId"`, names that do not exist (completion audit 2026-11-25, H7). A catalog column, or
   * any column on an engine that does not fold, follows the catalog rules (`q`).
   */
  private colText(c: ColInfo): string {
    const fold = DIALECTS[this.dialect].identifierFold;
    if (!c.fromSource || (fold !== "lower" && fold !== "upper")) return this.q(c.name);
    return this.src(c.name, c.quoted);
  }

  /**
   * A name taken from the source text (alias, CTE, table written in the statement): as the user wrote it, delimited
   * only when they delimited it. Re-quoting it by catalog rules would change it on engines that fold unquoted names
   * (Oracle `o` -> `"o"`, PostgreSQL `O` -> `"O"`).
   */
  private src(name: string, quoted?: boolean): string {
    // A name written bare is stored folded (Oracle `type` is TYPE, PostgreSQL `Order` is order): delimited, it is that
    // form that must be written, else the delimited name names nothing (code audit 2026-11-26, R1: `"type"`, `"Order"`).
    const stored = quoted || !SOURCE_NAME.test(name) ? name : foldIdentifier(name, false, this.dialect);
    if (this.typedOpenQuote) return quoteWith(this.openQuote, stored);
    // A reserved word written bare is a select-list label PostgreSQL allows (`SELECT 1 AS order`): referred to quoted.
    if (quoted || !SOURCE_NAME.test(name)) return DIALECTS[this.dialect].quote(name);
    return this.reserved.has(name.toUpperCase()) ? DIALECTS[this.dialect].quote(stored) : name;
  }

  /** How a table in scope is referred to: its alias, else its name, as written. */
  private refText(t: TableInScope): string {
    return t.alias !== undefined ? this.src(t.alias, t.aliasQuoted) : this.src(t.name, t.quoted);
  }

  private isDefault(schema: string): boolean {
    return this.idx ? isDefaultSchema(this.idx, schema) : schema.toLowerCase() === DIALECTS[this.dialect].defaultSchema;
  }

  /** Referred to without a schema: the default schema's objects, and Oracle's public synonyms (PUBLIC.x is invalid). */
  private unqualified(o: SnapObject): boolean {
    return this.isDefault(o.schema) || (this.dialect === "oracle" && o.kind === "synonym" && o.schema.toUpperCase() === "PUBLIC");
  }

  private labelFor(o: SnapObject): string {
    return this.unqualified(o) ? o.name : `${o.schema}.${o.name}`;
  }

  private insertFor(o: SnapObject, forceQualified = false): string {
    return this.unqualified(o) && !forceQualified ? this.q(o.name) : `${this.q(o.schema)}.${this.q(o.name)}`;
  }

  private objectDetail(o: SnapObject, withSchema = true): string {
    return objectDescription(o, this.rowCounts, withSchema);
  }

  private objectKind(o: SnapObject): CompletionKind {
    switch (o.kind) {
      case "table": return "table";
      case "synonym": return "synonym";
      case "tvf": return "tableFunction";
      case "view": return "view";
      case "proc": return "proc";
      case "type": return "type";
      default: return "func";
    }
  }

  private paramsSnippet(o: SnapObject): string {
    const ps = (o.params ?? []).filter((p) => p.name && !p.output).sort((a, b) => a.ordinal - b.ordinal);
    return ps.map((p, i) => `\${${i + 1}:${escSnippet(p.name)}}`).join(", ") || "$0";
  }

  // ---- item groups ----

  private keywords(list: string[], group: number): void {
    for (const label of list) {
      this.cands.push({ label, kind: "keyword", group, match: [label], usageKeys: [label.toLowerCase()] });
    }
  }

  private variables(group: number): void {
    for (const v of this.ctx.variables) {
      const c: Cand = { label: v.name, kind: v.kind === "parameter" ? "parameter" : "variable", group };
      if (v.type) c.detail = v.type;
      this.cands.push(c);
    }
  }

  /**
   * An object's candidate: its label, kind and group now, the rest when the list keeps it (fillObject). `unqualified`:
   * named bare because its schema was typed (`sales.|`). `m`: the match rank, when the caller filtered on it already.
   */
  private objectCand(o: SnapObject, unqualified: boolean, group: number, m?: number): Cand {
    const label = unqualified ? o.name : this.labelFor(o);
    // Every object candidate gets the same fields, set or undefined: one object shape for the engine.
    return {
      label, display: label !== o.name ? o.name : undefined, kind: this.objectKind(o), group, schemaObject: true,
      obj: o, bare: unqualified, m, detail: undefined, insertText: undefined, isSnippet: undefined, aliasFor: undefined,
    };
  }

  /** How the typed word matches an object's candidate (its label, or its bare name), without building a list for it. */
  private objectMatch(o: SnapObject, unqualified: boolean): number {
    const prefix = this.ctx.prefix;
    if (!prefix) return MATCH_PREFIX;
    const k = objKeys(o);
    const byName = matchRankLower(this.prefixLower, prefix, o.name, k.name);
    if (byName === MATCH_PREFIX || unqualified || this.unqualified(o)) return byName;
    const byLabel = matchRankLower(this.prefixLower, prefix, `${o.schema}.${o.name}`, k.qualified);
    return byName === MATCH_NONE ? byLabel : byLabel === MATCH_NONE ? byName : Math.min(byName, byLabel);
  }

  /** The detail, insert text and documentation of a kept object item (objectCand left them out). */
  private fillObject(c: Cand): void {
    const o = c.obj!;
    const unqualified = !!c.bare;
    let insertText = unqualified ? this.q(o.name) : this.insertFor(o);
    if (o.kind === "tvf") {
      insertText = `${escSnippet(insertText)}(${this.paramsSnippet(o)})`;
      c.isSnippet = true;
    }
    if (insertText !== c.label) c.insertText = insertText;
    c.detail = this.objectDetail(o, !unqualified);
    const cols = o.columns;
    if (cols?.length) c.doc = () => [objectHeader(o), ...columnsTable(cols, 15)].join("\n\n");
  }

  /** Tables, views, TVFs, CTEs, synonyms (+ schemas) for FROM/JOIN/UPDATE/INSERT INTO/DELETE. */
  private tableObjects(schema?: string): void {
    const clause = this.ctx.clause;
    const kinds = clause === "from" || clause === "join" ? KIND_SETS.from : KIND_SETS.dml;
    if (schema === undefined) {
      if (clause === "join") this.joinSnippets();
      for (const c of this.ctx.ctes) this.cands.push(this.withAlias({ label: c.name, kind: "cte", detail: "CTE", group: 1, insertText: this.src(c.name, c.quoted) }, c.name));
    }
    const idx = this.idx;
    if (!idx) return;
    const bare = schema !== undefined;
    for (const o of idx.objects) {
      if (!kinds.has(o.kind)) continue;
      if (bare && !eq(o.schema, schema)) continue;
      const m = this.objectMatch(o, bare);
      if (m === MATCH_NONE) continue;
      this.cands.push(this.withAlias(this.objectCand(o, bare, o.kind === "table" || o.kind === "view" ? 2 : 3, m), o.name));
    }
    if (schema === undefined) {
      for (const s of idx.schemas) this.cands.push({ label: s, kind: "schema", detail: "schema", group: 5, insertText: this.q(s) });
      // Oracle's one-row DUAL lives in SYS, which the snapshot does not load.
      if (this.dialect === "oracle" && !idx.byName.has("dual")) {
        this.cands.push({ label: "DUAL", kind: "table", detail: "SYS · one row (DUMMY)", group: 6 });
      }
      this.keywords(tableContextKeywords(this.dialect), 8);
    }
  }

  private procs(schema?: string): void {
    const idx = this.idx;
    if (!idx) return;
    for (const o of idx.objects) {
      if (o.kind !== "proc") continue;
      if (schema !== undefined && !eq(o.schema, schema)) continue;
      const m = this.objectMatch(o, schema !== undefined);
      if (m === MATCH_NONE) continue;
      const c = this.objectCand(o, schema !== undefined, 1, m);
      const ps = (o.params ?? []).filter((p) => p.name).sort((a, b) => a.ordinal - b.ordinal);
      // Inline code: the names and types come from the server, and the details pane renders markdown (M17).
      if (ps.length) c.documentation = mdCode(ps.map((p) => `${p.name} ${p.type}${p.output ? " OUTPUT" : ""}`).join(", "));
      this.cands.push(c);
    }
    if (schema === undefined) for (const s of idx.schemas) this.cands.push({ label: s, kind: "schema", detail: "schema", group: 5 });
  }

  private columnsOf(t: TableInScope): { cols: ColInfo[]; display: string; obj?: SnapObject } {
    if (t.isCte || t.isDerived) {
      if (!t.columns?.length && t.starSources?.length) {
        const seen = new Set<string>();
        const cols: ColInfo[] = [];
        for (const src of t.starSources) {
          for (const c of this.columnsOf(src).cols) {
            if (seen.has(c.name.toLowerCase())) continue;
            seen.add(c.name.toLowerCase());
            cols.push(c);
          }
        }
        return { cols, display: t.name };
      }
      const quoted = new Set(t.quotedColumns ?? []);
      return { cols: (t.columns ?? []).map((name) => ({ name, fromSource: true, quoted: quoted.has(name) })), display: t.name };
    }
    const ref = resolveTable(this.idx, t);
    // A synonym has its target's columns (SQL Server, Oracle).
    const o = ref && synonymTarget(this.idx, ref);
    if (!o) {
      // A column list the statement writes (`generate_series(1, 3) AS g(Num)`) is named as written there, like a CTE's
      // (M21: by catalog rules PostgreSQL's bare `Num` became `"Num"`, a name that does not exist); a built-in row
      // source's columns are the engine's own.
      if (t.columns?.length) return { cols: this.writtenColumns(t), display: t.schema ? `${t.schema}.${t.name}` : t.name };
      const builtin = builtinRowSource(this.dialect, t);
      return { cols: (builtin ?? []).map((name) => ({ name })), display: t.schema ? `${t.schema}.${t.name}` : t.name };
    }
    const cols: ColInfo[] = (o.columns ?? []).map((col) => ({ name: col.name, col }));
    if (t.columns && t.columns.length && cols.length === 0) return { cols: this.writtenColumns(t), display: `${o.schema}.${o.name}`, obj: o };
    return { cols, display: `${o.schema}.${o.name}`, obj: o };
  }

  /** A table's columns as the statement's text lists them, each quoted or not as written there. */
  private writtenColumns(t: TableInScope): ColInfo[] {
    const quoted = new Set(t.quotedColumns ?? []);
    return (t.columns ?? []).map((name) => ({ name, fromSource: true, quoted: quoted.has(name) }));
  }

  private nearestTable(): TableInScope | undefined {
    const pos = this.ctx.prefixStart;
    let before: TableInScope | undefined;
    let after: TableInScope | undefined;
    for (const t of this.ctx.tablesInScope) {
      const o = t.offset ?? 0;
      if (o < pos) { if (!before || o > (before.offset ?? 0)) before = t; }
      else if (!after || o < (after.offset ?? 0)) after = t;
    }
    return before ?? after;
  }

  private columnCands(t: TableInScope, group: number, qualify = false): void {
    const { cols, display, obj } = this.columnsOf(t);
    const kindNote = t.isCte ? "CTE " : t.isDerived ? "derived " : "";
    const prefixWith = qualify ? t.alias ?? t.name : undefined;
    const prefixText = qualify ? this.refText(t) : "";
    for (const c of cols) {
      const name = c.name;
      const fk = obj && c.col ? fkReference(this.idx, obj.id, name) : undefined;
      const label = prefixWith ? `${prefixWith}.${name}` : name;
      const ins = prefixWith ? `${prefixText}.${this.colText(c)}` : this.colText(c);
      const cand: Cand = {
        label, kind: "column", group, match: prefixWith ? [name, label] : [name],
        detail: c.col ? columnDetail(c.col, refName(fk)) : `${kindNote}column`,
        // Markdown, rendered untrusted: server names escaped (L10).
        documentation: c.col ? columnLines(`${display}.${name}`, c.col, fk).join("\n\n") : `**${mdText(`${display}.${name}`)}**`,
        usageKeys: [name.toLowerCase(), label.toLowerCase()],
      };
      if (prefixWith) cand.filterText = name;
      if (ins !== label) cand.insertText = ins;
      this.cands.push(cand);
    }
  }

  private scalarFunctions(group: number, schema?: string): void {
    const idx = this.idx;
    const prefix = this.ctx.prefix;
    if (idx) {
      for (const o of idx.objects) {
        if (o.kind !== "func") continue;
        if (schema !== undefined && !eq(o.schema, schema)) continue;
        if (prefix && bestMatch(prefix, [o.name, `${o.schema}.${o.name}`]) === MATCH_NONE) continue;
        // T-SQL calls a scalar function by its two-part name: the label is that name in every schema, dbo too, so the
        // sidecar's item for it (always `schema.name`) merges with this one instead of listing the function twice.
        const qualify = RULES[this.dialect].qualifyScalarFunctions;
        const label = schema !== undefined ? o.name : qualify ? `${o.schema}.${o.name}` : this.labelFor(o);
        const base = schema !== undefined ? this.q(o.name) : qualify ? this.insertFor(o, true) : this.insertFor(o);
        const usageKeys = [label.toLowerCase(), `${o.schema}.${o.name}`.toLowerCase()];
        // A T-SQL dbo function used to be listed by its bare name: uses counted under that name still count.
        if (qualify && schema === undefined && this.unqualified(o)) usageKeys.push(o.name.toLowerCase());
        this.cands.push({
          label, display: label !== o.name ? o.name : undefined, kind: "func", detail: this.objectDetail(o, schema === undefined), group,
          isSnippet: true, insertText: `${escSnippet(base)}(${this.paramsSnippet(o)})`, match: [label, o.name], usageKeys, schemaObject: true,
        });
      }
    }
    if (schema === undefined) {
      for (const f of builtinFunctions(this.dialect)) {
        const bare = niladicFunctions(this.dialect).has(f);
        this.cands.push({ label: f, kind: "func", detail: "built-in function", group: group + 1, insertText: bare ? f : `${f}($0)`, isSnippet: !bare });
      }
    }
  }

  private fkSnippets(): void {
    const idx = this.idx;
    if (!idx || !idx.fks.length) return;
    const nearest = this.nearestTable();
    if (!nearest || (nearest.offset ?? 0) >= this.ctx.prefixStart) return;
    const nObj = resolveTable(idx, nearest);
    if (!nObj) return;
    const nq = this.refText(nearest);
    const seen = new Set<string>();
    const fks = fkLookup(idx);
    const touching = [...(fks.byParent.get(nObj.id) ?? []), ...(fks.byReferenced.get(nObj.id) ?? [])];
    for (const t of this.ctx.tablesInScope) {
      if (t === nearest) continue;
      const tObj = resolveTable(idx, t);
      if (!tObj) continue;
      const tq = this.refText(t);
      if (tq.toLowerCase() === nq.toLowerCase()) continue;
      for (const fk of touching) {
        if (!usableFk(fk)) continue;
        let cond = "";
        if (fk.parent === nObj.id && fk.referenced === tObj.id) {
          cond = fk.columns.map((p) => `${nq}.${this.q(p.parent)} = ${tq}.${this.q(p.referenced)}`).join(" AND ");
        } else if (fk.referenced === nObj.id && fk.parent === tObj.id) {
          cond = fk.columns.map((p) => `${nq}.${this.q(p.referenced)} = ${tq}.${this.q(p.parent)}`).join(" AND ");
        }
        if (!cond || seen.has(cond)) continue;
        seen.add(cond);
        this.cands.push({ label: cond, kind: "snippet", detail: `FK ${fk.name}`, group: 0, match: [cond, `${tq}.${cond}`] });
      }
    }
  }

  /**
   * Complete `JOIN <table> <alias> ON <condition>` items for the tables in scope: from declared foreign keys first, then
   * (where no FK says so) from column names (`orders.customer_id` ↔ the `customers` key `customer_id`, or `CustomerId` ↔
   * `Customers.Id`) — the only help on schemas without declared keys (most SQLite, MySQL and DuckDB files). Each is a
   * snippet: the alias is one placeholder used in both places, so renaming it renames the ON condition too.
   */
  private joinSnippets(): void {
    const idx = this.idx;
    if (!idx || !this.ctx.tablesInScope.length) return;
    const takenAliases = new Set(this.ctx.tablesInScope.map((t) => (t.alias ?? t.name).toLowerCase()));
    const seen = new Set<string>();
    const inScope = new Set<number>();
    for (const x of this.ctx.tablesInScope) { const o = resolveTable(idx, x); if (o) inScope.add(o.id); }

    const push = (tq: string, other: SnapObject, pairs: { mine: string; theirs: string }[], why: string, group: number) => {
      const key = `${other.id}|${pairs.map((p) => `${tq}.${p.mine}=${p.theirs}`.toLowerCase()).join("&")}`;
      if (seen.has(key)) return;
      seen.add(key);
      // A generated alias is a new unquoted name (suggestAlias never returns a keyword): never delimited.
      const alias = suggestAlias(other.name, takenAliases);
      const target = this.isDefault(other.schema) ? this.q(other.name) : `${this.q(other.schema)}.${this.q(other.name)}`;
      const cond = pairs.map((p) => `${tq}.${this.q(p.mine)} = ${alias}.${this.q(p.theirs)}`).join(" AND ");
      const ph = `\${1:${escSnippet(alias)}}`;
      const snippetCond = pairs.map((p) => `${escSnippet(`${tq}.${this.q(p.mine)}`)} = ${ph}.${escSnippet(this.q(p.theirs))}`).join(" AND ");
      const label = `${other.name} ON ${cond}`;
      // A table already in the statement is still offered (a self-join, a second role) but below new ones.
      const joined = inScope.has(other.id);
      this.cands.push({
        label, kind: "snippet", detail: joined ? `JOIN · ${why} · already joined` : `JOIN · ${why}`,
        insertText: `${escSnippet(target)} ${ph} ON ${snippetCond}`, isSnippet: true,
        group: joined ? 4 : group, match: [other.name, `${other.schema}.${other.name}`, label],
        // Accepting it counts for the joined table, which ranks it (and the table itself) higher next time.
        usageKeys: [other.name.toLowerCase(), `${other.schema}.${other.name}`.toLowerCase()],
      });
    };

    const fks = fkLookup(idx);
    for (const t of this.ctx.tablesInScope) {
      const tObj = resolveTable(idx, t);
      if (!tObj) continue;
      const tq = this.refText(t);
      for (const fk of fks.byParent.get(tObj.id) ?? []) {
        if (!usableFk(fk)) continue;
        const other = idx.byId.get(fk.referenced);
        if (other) push(tq, other, fk.columns.map((p) => ({ mine: p.parent, theirs: p.referenced })), `FK ${fk.name}`, 0);
      }
      for (const fk of fks.byReferenced.get(tObj.id) ?? []) {
        if (!usableFk(fk) || fk.parent === tObj.id) continue; // a self-reference: listed once, from its holder above
        const other = idx.byId.get(fk.parent);
        if (other) push(tq, other, fk.columns.map((p) => ({ mine: p.referenced, theirs: p.parent })), `FK ${fk.name}`, 0);
      }
    }
    // By column name, where no FK covers the pair.
    const names = nameJoinIndex(idx);
    let n = 0;
    for (const t of this.ctx.tablesInScope) {
      const tObj = resolveTable(idx, t);
      if (!tObj?.columns) continue;
      const tq = this.refText(t);
      // A key whose columns are not known (no snippet from it) leaves the pair to the name match.
      const fkTargets = new Set<number>();
      for (const fk of fks.byParent.get(tObj.id) ?? []) if (usableFk(fk)) fkTargets.add(fk.referenced);
      for (const fk of fks.byReferenced.get(tObj.id) ?? []) if (usableFk(fk) && fk.parent !== tObj.id) fkTargets.add(fk.parent);
      // This table's column names another table's key: t.customer_id → customers.customer_id, t.CustomerId → Customers.Id.
      for (const c of tObj.columns) {
        if (c.pk) continue;
        for (const hit of names.keyFor(c.name)) {
          if (hit.obj.id === tObj.id || fkTargets.has(hit.obj.id) || n >= 40) continue;
          push(tq, hit.obj, [{ mine: c.name, theirs: hit.key }], "by column name", 2);
          n++;
        }
      }
      // Another table's column names this table's key.
      const pk = singleKey(tObj);
      if (pk) {
        for (const hit of names.referencing(tObj, pk)) {
          if (hit.obj.id === tObj.id || fkTargets.has(hit.obj.id) || n >= 40) continue;
          push(tq, hit.obj, [{ mine: pk, theirs: hit.column }], "by column name", 2);
          n++;
        }
      }
    }
  }

  /**
   * Statement templates, offered only while the prefix starts one of their words (`ins` → INSERT…, never `t` or `u`
   * matching inside EXECUTE). The filter text is that word, not the prefix itself: a filter equal to the prefix gave
   * Monaco a perfect score and put the templates above a column the user was typing mid-statement.
   */
  private statementTemplates(): void {
    const prefix = this.ctx.prefix.toLowerCase();
    const add = (label: string, insertText: string, match: string[]) => {
      const word = prefix ? match.find((m) => m.toLowerCase().startsWith(prefix)) : match[0];
      if (word === undefined) return;
      this.cands.push({ label, kind: "snippet", detail: "statement template", insertText, isSnippet: true, filterText: word, group: 0, match });
    };
    add("INSERT INTO … VALUES …", "INSERT INTO ${1:table} (${2:columns})\nVALUES ($0);", ["INSERT", "INSERT INTO", "ins"]);
    add("UPDATE … SET … WHERE …", "UPDATE ${1:table}\nSET ${2:column} = ${3:value}\nWHERE $0;", ["UPDATE", "upd"]);
    const call = RULES[this.dialect].call;
    if (call) add(call.label, call.insertText, call.match);
    add("ALTER TABLE …", "ALTER TABLE ${1:table_name} $0;", ["ALTER", "ALTER TABLE", "alt"]);
    add("SELECT … FROM …", "SELECT * FROM $0;", ["SELECT", "ssf"]);
    add("DELETE FROM … WHERE …", "DELETE FROM ${1:table_name} WHERE $0;", ["DELETE", "del"]);
  }

  private expression(clause: string): void {
    const tables = this.ctx.tablesInScope;
    // A SET target (the scanner kept only the written table): its columns, bare (audit M9). T-SQL may assign a
    // variable there too (`UPDATE t SET @x = col`).
    if (clause === "set" && this.ctx.setTarget) {
      for (const t of tables) this.columnCands(t, 1, false);
      if (this.dialect === "tsql") this.variables(4);
      return;
    }
    const multi = tables.length > 1;
    // A table the user gave an alias is referred to through it (`FROM emp e WHERE |` → `e.id`), also when it is the only
    // one. Not in SET: an UPDATE / ON CONFLICT / ON DUPLICATE KEY target column cannot be qualified on PostgreSQL,
    // SQLite and DuckDB.
    const qualify = (t: TableInScope) => multi || (t.alias !== undefined && clause !== "set");
    const nearest = this.nearestTable();
    if (clause === "on") this.fkSnippets();
    // GROUP BY: the select list's non-aggregated columns first. Each is one item — the column itself, ranked first —
    // not a second copy beside it (a column the tables in scope do not list still gets an item of its own).
    let grouped: Map<string, string> | undefined;
    if (clause === "groupBy" && this.ctx.nonAggregatedColumns?.length) {
      const nonAgg = this.ctx.nonAggregatedColumns;
      this.cands.push({
        label: "(all non-aggregated columns)",
        kind: "snippet",
        detail: `${nonAgg.length} non-aggregated column${nonAgg.length > 1 ? "s" : ""}`,
        insertText: nonAgg.join(", "),
        group: 0,
        order: 0,
        match: ["(all non-aggregated columns)", nonAgg[0]],
      });
      grouped = new Map(nonAgg.map((c) => [c.toLowerCase(), c]));
    }
    for (const a of this.ctx.selectAliases ?? []) {
      this.cands.push({ label: a.name, kind: "column", detail: "select-list alias", group: 0, order: 2, insertText: this.src(a.name, a.quoted) });
    }
    const first = this.cands.length;
    for (const t of tables) this.columnCands(t, t === nearest ? 1 : 2, qualify(t));
    if (grouped) {
      const promote = (c: Cand) => { c.group = 0; c.order = 1; };
      // A bare select-list column one table in scope has (`SELECT status … FROM orders o GROUP BY |`) is that table's
      // column item (`o.status`), not a second item beside it (M22).
      const byBare = new Map<string, Cand[]>();
      for (let i = first; i < this.cands.length; i++) {
        const c = this.cands[i];
        if (c.kind !== "column") continue;
        const key = c.label.toLowerCase();
        if (grouped.has(key)) { grouped.delete(key); promote(c); continue; }
        const bare = key.slice(key.lastIndexOf(".") + 1);
        let list = byBare.get(bare);
        if (!list) byBare.set(bare, (list = []));
        list.push(c);
      }
      for (const key of [...grouped.keys()]) {
        const only = !key.includes(".") ? byBare.get(key) : undefined;
        if (only?.length === 1) { grouped.delete(key); promote(only[0]); }
      }
      for (const col of grouped.values()) {
        this.cands.push({ label: col, kind: "column", detail: "non-aggregated column", insertText: col, group: 0, order: 1 });
      }
    }
    if (clause === "select") for (const t of tables) this.allColumnsCand(t, qualify(t));
    for (const t of tables) {
      if (t.alias) {
        const target = t.isDerived ? "derived table" : t.schema ? `${t.schema}.${t.name}` : t.name;
        this.cands.push({ label: t.alias, kind: "alias", detail: `alias · ${target}`, group: 3, insertText: this.refText(t) });
      } else {
        this.cands.push({ label: t.name, kind: t.isCte ? "cte" : "table", detail: t.isCte ? "CTE" : "table in scope", group: 3, insertText: this.refText(t) });
      }
    }
    this.variables(4);
    this.scalarFunctions(5);
    this.keywords(clauseKeywords(clause, this.dialect), 8);
  }

  /**
   * One item that inserts every column of `t` (`o.a, o.b, …`, qualified when several tables are in scope, or after a
   * typed `o.` its first column bare): an explicit list instead of `*`.
   */
  private allColumnsCand(t: TableInScope, qualify: boolean, afterQualifier = false): void {
    const { cols } = this.columnsOf(t);
    if (cols.length < 2) return;
    const ref = this.refText(t);
    const names = cols.map((c) => this.colText(c));
    const text = afterQualifier ? [names[0], ...names.slice(1).map((n) => `${ref}.${n}`)].join(", ")
      : qualify ? names.map((n) => `${ref}.${n}`).join(", ") : names.join(", ");
    const who = t.alias ?? t.name;
    this.cands.push({
      label: afterQualifier ? "(all columns)" : `(all columns of ${who})`, kind: "snippet", detail: `${cols.length} columns`,
      insertText: text, group: 6, match: ["(all columns)", `(all columns of ${who})`, "all", "*"],
      documentation: mdCode(text.length > 300 ? `${text.slice(0, 300)}…` : text),
    });
  }

  private argValue(): void {
    this.variables(1);
    this.scalarFunctions(5);
    this.keywords(argValueKeywords(this.ctx.clause, this.dialect), 8);
  }

  private execArgs(): void {
    // Positional arguments only (MySQL `CALL p(|`): a value goes here, as in a function call (audit M19).
    if (RULES[this.dialect].namedArguments === null) {
      this.argValue();
      return;
    }
    const idx = this.idx;
    const target = this.ctx.execTarget ?? "";
    const parts = target.split(".");
    const name = parts[parts.length - 1];
    const schema = parts.length >= 2 ? parts[parts.length - 2] : undefined;
    const db = parts.length >= 3 ? parts[parts.length - 3] : undefined;
    const obj = idx && (!db || eq(db, idx.database)) ? findObject(idx, name, schema, KIND_SETS.callable) : undefined;
    if (obj) {
      const used = new Set((this.ctx.execArgsUsed ?? []).map((u) => u.toLowerCase()));
      const style = RULES[this.dialect].namedArguments;
      const arrow = style === "arrow";
      // Positional-only arguments: no parameter names to offer.
      const params = style === null ? [] : (obj.params ?? []).filter((p) => p.name && !used.has(p.name.toLowerCase())).sort((a, b) => a.ordinal - b.ordinal);
      for (const p of params) {
        const pname = !arrow && !p.name.startsWith("@") ? `@${p.name}` : p.name;
        const detail = `${p.type}${p.hasDefault ? " = default" : ""}${p.output ? " OUTPUT" : ""}`;
        const c: Cand = { label: pname, kind: "parameter", detail, group: 1, order: p.ordinal };
        if (arrow) c.insertText = `${pname} => `;
        else if (p.output) { c.insertText = `${escSnippet(pname)} = \${1:${escSnippet(pname)}} OUTPUT`; c.isSnippet = true; }
        else c.insertText = `${pname} = `;
        this.cands.push(c);
      }
      if (used.size === 0 && params.length > 1) {
        let n = 0;
        const body = params.map((p) => {
          const pname = !arrow && !p.name.startsWith("@") ? `@${p.name}` : p.name;
          n++;
          if (arrow) return `${escSnippet(pname)} => \${${n}:${escSnippet(p.type)}}`;
          return p.output ? `${escSnippet(pname)} = \${${n}:${escSnippet(pname)}} OUTPUT` : `${escSnippet(pname)} = \${${n}:${escSnippet(p.type)}}`;
        }).join(", ");
        const first = params[0].name;
        this.cands.push({
          label: "(all parameters)", kind: "snippet", detail: obj.name, group: 2, insertText: body, isSnippet: true,
          filterText: first, match: [first, "(all parameters)"],
        });
      }
    }
    this.variables(4);
    this.keywords(execArgsKeywords(this.dialect), 8);
  }

  private types(schema?: string): void {
    if (schema === undefined) {
      for (const t of dataTypes(this.dialect)) {
        const c: Cand = { label: t.name, kind: "type", detail: "data type", group: 1 };
        if (t.snippet) { c.insertText = t.snippet; c.isSnippet = true; }
        this.cands.push(c);
      }
    }
    const idx = this.idx;
    if (idx) {
      for (const o of idx.objects) {
        if (o.kind !== "type") continue;
        if (schema !== undefined && !eq(o.schema, schema)) continue;
        this.cands.push(this.objectCand(o, schema !== undefined, 2));
      }
    }
    if (schema === undefined) this.keywords(declareKeywords(this.dialect), 8);
  }

  private databases(): void {
    const idx = this.idx;
    if (!idx) return;
    const seen = new Set<string>();
    for (const d of [idx.database, ...idx.databases]) {
      if (!d || seen.has(d.toLowerCase())) continue;
      seen.add(d.toLowerCase());
      this.cands.push({ label: d, kind: "database", detail: eq(d, idx.database) ? "database (current)" : "database", group: 1, insertText: this.q(d) });
    }
  }

  /** The table a DML statement writes: the INSERT target, else the first reference (UPDATE t, DELETE FROM t, MERGE INTO t). */
  private dmlTarget(): TableInScope | undefined {
    const t = this.ctx.insertTarget;
    if (t) {
      return this.ctx.tablesInScope.find((x) => eq(x.name, t.name) && (t.schema === undefined || eq(x.schema, t.schema)))
        ?? { name: t.name, ...(t.schema !== undefined ? { schema: t.schema } : {}), isCte: false, isDerived: false };
    }
    return this.ctx.tablesInScope.find((x) => !x.isDerived);
  }

  private insertTargetObject(): SnapObject | undefined {
    const t = this.ctx.insertTarget;
    if (!t || !this.idx) return undefined;
    const cte = t.schema === undefined ? this.ctx.ctes.find((c) => eq(c.name, t.name)) : undefined;
    if (cte) return { id: -1, schema: "", name: cte.name, kind: "view", columns: (cte.columns ?? []).map((name, i) => ({ name, type: "", nullable: true, identity: false, computed: false, pk: false, ordinal: i + 1 })) };
    return findObject(this.idx, t.name, t.schema, KIND_SETS.dml);
  }

  private insertColumns(): void {
    const obj = this.insertTargetObject();
    if (!obj) return;
    const used = new Set((this.ctx.insertColumnList ?? []).map((c) => c.toLowerCase()));
    const rest: string[] = [];
    for (const col of obj.columns ?? []) {
      if (col.computed || used.has(col.name.toLowerCase())) continue;
      const c: Cand = { label: col.name, kind: "column", detail: col.type ? columnDetail(col) : "column", group: col.identity ? 3 : 1 };
      const ins = this.q(col.name);
      if (ins !== col.name) c.insertText = ins;
      this.cands.push(c);
      if (!col.identity) rest.push(ins);
    }
    if (rest.length > 1) {
      this.cands.push({ label: "(all columns)", kind: "snippet", detail: `${rest.length} columns`, group: 2, insertText: rest.join(", "), filterText: rest[0], match: [rest[0], "(all columns)"] });
    }
  }

  private values(): void {
    const obj = this.insertTargetObject();
    if (obj) {
      const explicit = this.ctx.insertColumnList;
      const names = explicit && explicit.length
        ? explicit
        : (obj.columns ?? []).filter((c) => !c.identity && !c.computed).map((c) => c.name);
      if (names.length) {
        const ph = names.map((n, i) => `\${${i + 1}:${escSnippet(n)}}`).join(", ");
        this.cands.push({ label: "VALUES (…)", kind: "snippet", detail: `${names.length} values for ${obj.name}`, group: 0, insertText: `VALUES (${ph})`, isSnippet: true, filterText: "VALUES", match: ["VALUES"] });
        if (!explicit) {
          const cols = names.map((n) => escSnippet(this.q(n))).join(", ");
          this.cands.push({ label: "(columns) VALUES (…)", kind: "snippet", detail: `column list + values for ${obj.name}`, group: 0, order: 1, insertText: `(${cols}) VALUES (${ph})`, isSnippet: true, filterText: "VALUES", match: ["VALUES", "("] });
        }
      }
    }
    this.keywords(valuesKeywords(this.dialect), 8);
  }

  /** Qualifier resolution order (§9): aliases, then CTEs, then schemas, then databases. */
  private qualified(q: string): void {
    const clause = this.ctx.clause;
    const idx = this.idx;
    const parts = q.split(".");
    const isTableCtx = clause === "from" || clause === "join" || clause === "update" || clause === "insertInto" || clause === "delete";
    if (!isTableCtx && clause !== "exec" && clause !== "declare" && clause !== "use") {
      // 1. alias / in-scope table name
      // Innermost query level first: an alias in a subquery hides the same alias outside it.
      const tables = [...this.ctx.tablesInScope].sort((a, b) => (b.depth ?? 0) - (a.depth ?? 0));
      const lowerQ = q.toLowerCase();
      let t = tables.find((x) => x.alias && x.alias.toLowerCase() === lowerQ);
      t ??= tables.find((x) => !x.alias && (x.name.toLowerCase() === lowerQ || (x.schema && `${x.schema}.${x.name}`.toLowerCase() === lowerQ)));
      t ??= tables.find((x) => x.name.toLowerCase() === lowerQ || (x.schema !== undefined && `${x.schema}.${x.name}`.toLowerCase() === lowerQ));
      if (t) {
        this.columnCands(t, 1);
        if (clause === "select") this.allColumnsCand(t, false, true);
        return;
      }
      // 1b. a DML pseudo-table: T-SQL OUTPUT inserted.* / deleted.*, an upsert's excluded.* (PostgreSQL, SQLite, DuckDB)
      if (parts.length === 1 && PSEUDO_TABLES[this.dialect].has(lowerQ)) {
        const target = this.dmlTarget();
        if (target) { this.columnCands(target, 1); return; }
      }
      // 2. CTE
      const cte = this.ctx.ctes.find((c) => c.name.toLowerCase() === lowerQ);
      if (cte) {
        // Its quoted columns too: they are inserted as written (H7; this path, before FROM is typed, dropped them).
        this.columnCands({
          name: cte.name, isCte: true, isDerived: false, columns: cte.columns,
          ...(cte.quotedColumns ? { quotedColumns: cte.quotedColumns } : {}), ...(cte.quoted ? { quoted: true } : {}),
        }, 1);
        return;
      }
    }
    if (!idx) return;
    // 3. schema / 4. database / database.schema
    let schema: string | undefined;
    if (parts.length === 1 && idx.schemas.some((s) => eq(s, parts[0]))) schema = idx.schemas.find((s) => eq(s, parts[0]));
    else if (parts.length === 2 && eq(parts[0], idx.database)) schema = parts[1] === "" ? idx.defaultSchema : parts[1];
    else if (parts.length === 2 && parts[1] === "" && idx.databases.some((d) => eq(d, parts[0]))) return;
    if (schema !== undefined) {
      if (isTableCtx) this.tableObjects(schema);
      else if (clause === "exec") this.procs(schema);
      else if (clause === "declare") this.types(schema);
      else {
        this.scalarFunctions(1, schema);
        for (const o of idx.objects) {
          if (!KIND_SETS.from.has(o.kind) || !eq(o.schema, schema)) continue;
          const m = this.objectMatch(o, true);
          if (m === MATCH_NONE) continue;
          this.cands.push(this.objectCand(o, true, 2, m));
        }
      }
      return;
    }
    if (parts.length === 1 && eq(parts[0], idx.database)) {
      for (const s of idx.schemas) this.cands.push({ label: s, kind: "schema", detail: `schema · ${idx.database}`, group: 1, insertText: this.q(s) });
      // DuckDB resolves catalog.name in the catalog's default schema: `memory.orders` is memory.main.orders.
      if (this.dialect === "duckDb" && isTableCtx) this.tableObjects(idx.defaultSchema);
      return;
    }
    if (parts.length === 1 && idx.databases.some((d) => eq(d, parts[0]))) return; // other database: not in this snapshot
    if (!isTableCtx && clause !== "exec") {
      // 5. a table referenced by name without being in scope (e.g. SELECT dbo.Orders.| before FROM)
      const name = parts[parts.length - 1];
      const sch = parts.length >= 2 ? parts[parts.length - 2] : undefined;
      const o = findObject(idx, name, sch);
      if (o) this.columnCands({ name: o.name, schema: o.schema, isCte: false, isDerived: false }, 1);
    }
  }

  // ---- ranking ----

  /**
   * Ranks every candidate and builds the MAX_ITEMS kept. Ranking reads numbers only — the sortText fields (rank.ts) as two
   * numbers, then the lower-case label —, and an object candidate's detail, insert text, alias and documentation are made
   * for the kept items alone: on a 5,000-table `FROM |`, building all of them and comparing their sort strings was most
   * of the keystroke (2026-11-25 profile: finalize 28 %, garbage collection 20 %, objectCand 12 %; completion audit H9).
   */
  private finalize(objectContext: boolean): Tier1Item[] {
    const prefix = this.ctx.prefix;
    const usage = this.usage && this.usage.size ? this.usage : undefined;
    const scored: Scored[] = [];
    const seen = new Set<string>();
    for (const c of this.cands) {
      const o = c.obj;
      // Inside an opened quote only a name can follow: no keyword, template or call (a table function is one).
      if (this.typedOpenQuote && (c.kind === "keyword" || c.kind === "snippet" || c.isSnippet || o?.kind === "tvf")) continue;
      let m: number;
      let low: string;
      let u = 0;
      let shortest: number;
      if (o) {
        m = c.m ?? this.objectMatch(o, !!c.bare);
        if (m === MATCH_NONE) continue;
        // The label is the object's name or `schema.name` (objectCand); it was matched on both.
        const k = objKeys(o);
        low = c.label === o.name ? k.name : k.qualified;
        if (usage) u = Math.max(usage.get(low) ?? 0, usage.get(k.qualified) ?? 0, usage.get(k.name) ?? 0);
        shortest = o.name.length;
      } else {
        const match = c.match ?? [c.label];
        m = c.m ?? bestMatch(prefix, match);
        if (m === MATCH_NONE) continue;
        low = c.label.toLowerCase();
        const dedupe = `${c.kind}|${low}|${c.detail ?? ""}`;
        if (seen.has(dedupe)) continue;
        seen.add(dedupe);
        if (usage) for (const key of c.usageKeys ?? [low]) u = Math.max(u, usage.get(key) ?? 0);
        shortest = Infinity;
        for (const x of match) if (x.length < shortest) shortest = x.length;
      }
      const tier = c.kind === "keyword" && objectContext ? 1 : 0;
      const rest = prefix ? Math.max(0, shortest - prefix.length) : 0;
      // sortText's fields in its order, widths and caps: tier, match, group, usage (most first), order, rest.
      const uc = Math.max(0, Math.min(999999, Math.floor(u)));
      const k1 = ((tier * 10 + m) * 100 + Math.min(c.group, 99)) * 1e6 + (999999 - uc);
      const k2 = Math.min(c.order ?? 0, 9999) * 1000 + Math.min(rest, 999);
      scored.push({ c, k1, k2, low, tier, m, u, rest });
    }
    scored.sort((a, b) => a.k1 - b.k1 || a.k2 - b.k2 || (a.low < b.low ? -1 : a.low > b.low ? 1 : 0));
    const out: Tier1Item[] = new Array(Math.min(MAX_ITEMS, scored.length));
    for (let i = 0; i < out.length; i++) {
      const s = scored[i];
      const c = s.c;
      if (c.obj) this.fillObject(c);
      const key = sortText({ tier: s.tier, match: s.m, group: c.group, usage: s.u, order: c.order ?? 0, rest: s.rest, label: c.label });
      out[i] = this.item(c, key, i < EAGER_DOCS);
    }
    return out;
  }

  private item(c: Cand, sortKey: string, eagerDoc: boolean): Tier1Item {
    const item: Tier1Item = { label: c.label, kind: c.kind, sortText: sortKey };
    if (c.display !== undefined && c.display !== c.label) item.display = c.display;
    if (c.schemaObject) item.schemaObject = true;
    if (c.detail !== undefined) item.detail = c.detail;
    if (c.insertText !== undefined) item.insertText = c.insertText;
    if (c.isSnippet) item.isSnippet = true;
    if (c.aliasFor !== undefined && this.aliasTaken) {
      const alias = suggestAlias(c.aliasFor, this.aliasTaken);
      item.insertText = `${c.insertText ?? c.label} ${alias}`;
      item.alias = alias;
    } else if (c.alias !== undefined) item.alias = c.alias;
    const uk = c.usageKeys?.[0];
    if (uk !== undefined && uk !== c.label.toLowerCase()) item.usageKey = uk;
    if (c.filterText !== undefined) item.filterText = c.filterText;
    if (c.documentation !== undefined) item.documentation = c.documentation;
    else if (c.doc) {
      if (eagerDoc) item.documentation = c.doc();
      else item.resolveDocumentation = c.doc;
    }
    return item;
  }
}

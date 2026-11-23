// Tier-1 completion (§9 context table). Synchronous, from the cached SchemaIndex; never throws.
import type { CompletionKind, Dialect, ObjectKind, SnapColumn, SnapObject } from "../../ipc/contract";
import type { CompletionRequest, CompletionResult, CursorContext, RowInfo, SchemaIndex, TableInScope, Tier1Item } from "./types";
import { scan } from "./scanner";
import { findObject, fkReference, isDefaultSchema, resolveTable, synonymTarget } from "./schemaIndex";
import { columnLines, columnsTable, mdCode, mdText, objectHeader } from "../hoverMarkdown";
import { bestMatch, MATCH_NONE, sortText } from "./rank";
import { suggestAlias } from "./alias";
import { getStream, type Token } from "./lexer";
import {
  afterReferenceKeywords, argValueKeywords, builtinFunctions, clauseKeywords, dataTypes, declareKeywords,
  execArgsKeywords, reservedWords, statementStartKeywords, tableContextKeywords, valuesKeywords,
} from "./keywords";
import { DIALECTS, knownDialect } from "../../engines";

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
  kind: CompletionKind;
  detail?: string;
  insertText?: string;
  isSnippet?: boolean;
  filterText?: string;
  documentation?: string;
  group: number;
  order?: number;
  /** Strings the prefix is matched against (default: [label]). */
  match?: string[];
  /** Lowercase keys looked up in `usage` (default: [label]). */
  usageKeys?: string[];
  /** Alias appended to insertText (auto-alias). */
  alias?: string;
}

interface ColInfo { name: string; col?: SnapColumn }

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
  if (ctx.inStringOrComment) return { context: ctx, items: [] };
  let items: Tier1Item[] = [];
  try {
    items = new Builder(ctx, req.schema ?? null, dialect, text, req.usage, req.autoAlias !== false ? offset : -1, req.rowCounts).build();
  } catch {
    items = [];
  }
  return { context: ctx, items };
}

/** Characters that open a quoted identifier per dialect (the lexer reads what follows as one name). */
const OPEN_QUOTES: Record<Dialect, string> = { tsql: '["', sqlite: '["`', mySql: "`", pg: '"', duckDb: '"', oracle: '"' };
const CLOSE_QUOTE: Record<string, string> = { "[": "]", '"': '"', "`": "`" };

/** The opening quote just before the prefix (`[Ord|`, `"Ord|`, a backtick), or "" when the prefix is not quoted. */
export function openQuoteAt(text: string, prefixStart: number, dialect: Dialect): string {
  const c = prefixStart > 0 ? text[prefixStart - 1] : "";
  return c && OPEN_QUOTES[dialect].includes(c) ? c : "";
}

/**
 * The filter text the editor gives a tier-1 item: what was typed, then the item's own filter text. Monaco ranks by its
 * fuzzy score before sortText; the same score for every item keeps tier 1's ranking (providers.ts).
 */
export function monacoFilterText(typed: string, item: Pick<Tier1Item, "label" | "filterText">): string {
  return typed + (item.filterText ?? item.label);
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

/** `schema.table.column` → `table` (the referenced table's name, short enough for the list). */
function refName(fk: string | undefined): string | undefined {
  if (!fk) return undefined;
  const parts = fk.split(".");
  return parts.length >= 2 ? parts[parts.length - 2] : fk;
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
  const spec = BUILTIN_ROW_SOURCES[dialect]?.[t.name.toLowerCase()];
  if (spec === undefined) return undefined;
  if (spec === "alias") return [t.alias ?? t.name];
  return spec;
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
  /** Aliases to avoid for an auto-aliased FROM/JOIN reference; null when no alias is appended here. */
  private readonly aliasTaken: string[] | null;
  /** The statement may end before the prefix, so the prefix may start the next one (`FROM t emp\nsele`). */
  private readonly nextStatement: boolean;

  constructor(
    private readonly ctx: CursorContext,
    private readonly idx: SchemaIndex | null,
    private readonly dialect: Dialect,
    text: string,
    private readonly usage?: Map<string, number>,
    aliasOffset = -1,
    private readonly rowCounts?: Map<number, RowInfo>,
  ) {
    this.openQuote = openQuoteAt(text, ctx.prefixStart, dialect);
    this.typedOpenQuote = this.openQuote !== "";
    this.reserved = reservedWords(dialect);
    this.aliasTaken = aliasOffset >= 0 && this.aliasSlotFree(text, aliasOffset)
      ? ctx.tablesInScope.flatMap((t) => (t.alias ? [t.alias, t.name] : [t.name]))
      : null;
    this.nextStatement = this.mayStartStatement(text);
  }

  /**
   * True when a new statement can begin at the prefix without ';' (both dialects split there, §9): paren depth 0 in
   * the current statement, the previous token can end a statement, and the user typed a prefix or started a new line.
   */
  private mayStartStatement(text: string): boolean {
    const { clause, slot, prefix, prefixStart, statementStart } = this.ctx;
    if (slot !== undefined && slot !== "afterReference") return false;
    if (clause === "update" || clause === "insertInto" || clause === "insertColumns") return false;
    if (/^[@:$]/.test(prefix)) return false;
    const T = getStream(text, this.dialect).code;
    let depth = 0;
    let prev: Token | undefined;
    for (const t of T) {
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
    const next = getStream(text, this.dialect).code.find((t) => t.end > offset);
    if (!next) return true;
    if (next.start < offset) return false; // cursor inside a word
    if (next.kind === "ident" || next.kind === "qident") return false;
    if (next.kind === "keyword" && next.upper === "AS") return false;
    return !(next.kind === "punct" || next.kind === "operator") || (next.value !== "." && next.value !== "(");
  }

  /** Append a generated alias to a FROM/JOIN reference's insert text. */
  private withAlias(c: Cand, name: string): Cand {
    if (!this.aliasTaken) return c;
    const alias = suggestAlias(name, this.aliasTaken);
    c.insertText = `${c.insertText ?? c.label} ${alias}`;
    c.alias = alias;
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
    // SQLite's own schema names: TEMP is a keyword, but `temp.t` is how a temporary table is named.
    if (this.dialect === "sqlite" && (name === "temp" || name === "main")) return false;
    return !RULES[this.dialect].plainName.test(name) || this.reserved.has(name.toUpperCase());
  }

  /** A catalog name as inserted: delimited when the dialect needs it, or in the quote style the user opened. */
  private q(name: string): string {
    if (this.typedOpenQuote) return quoteWith(this.openQuote, name);
    if (!this.needsQuote(name)) return name;
    return DIALECTS[this.dialect].quote(name);
  }

  /**
   * A name taken from the source text (alias, CTE, table written in the statement): as the user wrote it, delimited
   * only when they delimited it. Re-quoting it by catalog rules would change it on engines that fold unquoted names
   * (Oracle `o` -> `"o"`, PostgreSQL `O` -> `"O"`).
   */
  private src(name: string, quoted?: boolean): string {
    if (this.typedOpenQuote) return quoteWith(this.openQuote, name);
    return quoted || !SOURCE_NAME.test(name) ? DIALECTS[this.dialect].quote(name) : name;
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

  private objectDetail(o: SnapObject): string {
    const k = o.kind === "tvf" ? "table function" : o.kind === "func" ? "function" : o.kind === "proc" ? "procedure" : o.kind;
    const n = o.columns?.length && (o.kind === "table" || o.kind === "view" || o.kind === "synonym") ? ` · ${o.columns.length} cols` : "";
    const rc = o.kind === "table" ? this.rowCounts?.get(o.id) : undefined;
    const rows = !rc ? "" : rc.rows == null ? " · rows n/a" : ` · ${rc.estimated ? "~" : ""}${formatRows(rc.rows)} rows`;
    return o.returnType ? `${k} · ${o.schema} → ${o.returnType}` : `${k} · ${o.schema}${n}${rows}`;
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

  private objectCand(o: SnapObject, unqualified: boolean, group: number): Cand {
    const label = unqualified ? o.name : this.labelFor(o);
    let insertText = unqualified ? this.q(o.name) : this.insertFor(o);
    let isSnippet = false;
    if (o.kind === "tvf") {
      insertText = `${escSnippet(insertText)}(${this.paramsSnippet(o)})`;
      isSnippet = true;
    }
    const c: Cand = {
      label, kind: this.objectKind(o), detail: this.objectDetail(o), group, match: [label, o.name],
      usageKeys: [label.toLowerCase(), `${o.schema}.${o.name}`.toLowerCase(), o.name.toLowerCase()],
    };
    if (o.columns?.length) c.documentation = [objectHeader(o), ...columnsTable(o.columns, 15)].join("\n\n");
    if (insertText !== label) c.insertText = insertText;
    if (isSnippet) c.isSnippet = true;
    return c;
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
    const prefix = this.ctx.prefix;
    for (const o of idx.objects) {
      if (!kinds.has(o.kind)) continue;
      if (schema !== undefined && !eq(o.schema, schema)) continue;
      if (prefix && bestMatch(prefix, [o.name, `${o.schema}.${o.name}`]) === MATCH_NONE) continue;
      this.cands.push(this.withAlias(this.objectCand(o, schema !== undefined, o.kind === "table" || o.kind === "view" ? 2 : 3), o.name));
    }
    if (schema === undefined) {
      for (const s of idx.schemas) this.cands.push({ label: s, kind: "schema", detail: "schema", group: 5, insertText: this.q(s) });
      // Oracle's one-row DUAL lives in SYS, which the snapshot does not load.
      if (this.dialect === "oracle" && !idx.byName.has("dual")) {
        this.cands.push({ label: "DUAL", kind: "table", detail: "table · SYS · one row (DUMMY)", group: 6 });
      }
      this.keywords(tableContextKeywords(this.dialect), 8);
    }
  }

  private procs(schema?: string): void {
    const idx = this.idx;
    if (!idx) return;
    const prefix = this.ctx.prefix;
    for (const o of idx.objects) {
      if (o.kind !== "proc") continue;
      if (schema !== undefined && !eq(o.schema, schema)) continue;
      if (prefix && bestMatch(prefix, [o.name, `${o.schema}.${o.name}`]) === MATCH_NONE) continue;
      const c = this.objectCand(o, schema !== undefined, 1);
      const ps = (o.params ?? []).filter((p) => p.name).sort((a, b) => a.ordinal - b.ordinal);
      if (ps.length) c.documentation = ps.map((p) => `${p.name} ${p.type}${p.output ? " OUTPUT" : ""}`).join(", ");
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
      return { cols: (t.columns ?? []).map((name) => ({ name })), display: t.name };
    }
    const ref = resolveTable(this.idx, t);
    // A synonym has its target's columns (SQL Server, Oracle).
    const o = ref && synonymTarget(this.idx, ref);
    if (!o) {
      const builtin = t.columns?.length ? undefined : builtinRowSource(this.dialect, t);
      return { cols: (t.columns ?? builtin ?? []).map((name) => ({ name })), display: t.schema ? `${t.schema}.${t.name}` : t.name };
    }
    const cols: ColInfo[] = (o.columns ?? []).map((col) => ({ name: col.name, col }));
    if (t.columns && t.columns.length && cols.length === 0) return { cols: t.columns.map((name) => ({ name })), display: `${o.schema}.${o.name}`, obj: o };
    return { cols, display: `${o.schema}.${o.name}`, obj: o };
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
      const ins = prefixWith ? `${prefixText}.${this.q(name)}` : this.q(name);
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
        const label = schema !== undefined ? o.name : this.labelFor(o);
        const base = schema !== undefined ? this.q(o.name) : RULES[this.dialect].qualifyScalarFunctions ? this.insertFor(o, true) : this.insertFor(o);
        this.cands.push({
          label, kind: "func", detail: this.objectDetail(o), group, isSnippet: true,
          insertText: `${escSnippet(base)}(${this.paramsSnippet(o)})`, match: [label, o.name],
          usageKeys: [label.toLowerCase(), `${o.schema}.${o.name}`.toLowerCase()],
        });
      }
    }
    if (schema === undefined) {
      for (const f of builtinFunctions(this.dialect)) {
        this.cands.push({ label: f, kind: "func", detail: "built-in function", group: group + 1, insertText: `${f}($0)`, isSnippet: true });
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
    for (const t of this.ctx.tablesInScope) {
      if (t === nearest) continue;
      const tObj = resolveTable(idx, t);
      if (!tObj) continue;
      const tq = this.refText(t);
      if (tq.toLowerCase() === nq.toLowerCase()) continue;
      for (const fk of idx.fks) {
        if (!fk.columns?.length) continue;
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

    for (const t of this.ctx.tablesInScope) {
      const tObj = resolveTable(idx, t);
      if (!tObj) continue;
      const tq = this.refText(t);
      for (const fk of idx.fks) {
        if (!fk.columns?.length) continue;
        if (fk.parent === tObj.id) {
          const other = idx.byId.get(fk.referenced);
          if (other) push(tq, other, fk.columns.map((p) => ({ mine: p.parent, theirs: p.referenced })), `FK ${fk.name}`, 0);
        } else if (fk.referenced === tObj.id) {
          const other = idx.byId.get(fk.parent);
          if (other) push(tq, other, fk.columns.map((p) => ({ mine: p.referenced, theirs: p.parent })), `FK ${fk.name}`, 0);
        }
      }
    }
    // By column name, where no FK covers the pair.
    const names = nameJoinIndex(idx);
    let n = 0;
    for (const t of this.ctx.tablesInScope) {
      const tObj = resolveTable(idx, t);
      if (!tObj?.columns) continue;
      const tq = this.refText(t);
      const fkTargets = new Set(idx.fks.flatMap((fk) => (fk.parent === tObj.id ? [fk.referenced] : fk.referenced === tObj.id ? [fk.parent] : [])));
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
    const multi = tables.length > 1;
    // A table the user gave an alias is referred to through it (`FROM emp e WHERE |` → `e.id`), also when it is the only
    // one. Not in SET: an UPDATE / ON CONFLICT / ON DUPLICATE KEY target column cannot be qualified on PostgreSQL,
    // SQLite and DuckDB.
    const qualify = (t: TableInScope) => multi || (t.alias !== undefined && clause !== "set");
    const nearest = this.nearestTable();
    if (clause === "on") this.fkSnippets();
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
      for (const col of nonAgg) {
        this.cands.push({
          label: col,
          kind: "column",
          detail: "non-aggregated column",
          insertText: col,
          group: 0,
          order: 1,
        });
      }
    }
    for (const a of this.ctx.selectAliases ?? []) {
      this.cands.push({ label: a, kind: "column", detail: "select-list alias", group: 0, order: 2, insertText: this.src(a) });
    }
    for (const t of tables) this.columnCands(t, t === nearest ? 1 : 2, qualify(t));
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
    const names = cols.map((c) => this.q(c.name));
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
      if (cte) { this.columnCands({ name: cte.name, isCte: true, isDerived: false, columns: cte.columns }, 1); return; }
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
        const prefix = this.ctx.prefix;
        for (const o of idx.objects) {
          if (!KIND_SETS.from.has(o.kind) || !eq(o.schema, schema)) continue;
          if (prefix && bestMatch(prefix, [o.name]) === MATCH_NONE) continue;
          this.cands.push(this.objectCand(o, true, 2));
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

  private finalize(objectContext: boolean): Tier1Item[] {
    const prefix = this.ctx.prefix;
    const usage = this.usage;
    const scored: { item: Tier1Item; key: string }[] = [];
    const seen = new Set<string>();
    for (const c of this.cands) {
      // Inside an opened quote only a name can follow: no keyword, template or call.
      if (this.typedOpenQuote && (c.kind === "keyword" || c.kind === "snippet" || c.isSnippet)) continue;
      const m = bestMatch(prefix, c.match ?? [c.label]);
      if (m === MATCH_NONE) continue;
      const dedupe = `${c.kind}|${c.label.toLowerCase()}|${c.detail ?? ""}`;
      if (seen.has(dedupe)) continue;
      seen.add(dedupe);
      let u = 0;
      if (usage && usage.size) {
        for (const key of c.usageKeys ?? [c.label.toLowerCase()]) u = Math.max(u, usage.get(key) ?? 0);
      }
      const tier = c.kind === "keyword" && objectContext ? 1 : 0;
      const rest = prefix ? Math.max(0, Math.min(...(c.match ?? [c.label]).map((x) => x.length)) - prefix.length) : 0;
      const st = sortText({ tier, match: m, group: c.group, usage: u, order: c.order ?? 0, rest, label: c.label });
      const item: Tier1Item = { label: c.label, kind: c.kind, sortText: st };
      if (c.detail !== undefined) item.detail = c.detail;
      if (c.insertText !== undefined) item.insertText = c.insertText;
      if (c.isSnippet) item.isSnippet = true;
      if (c.alias !== undefined) item.alias = c.alias;
      const uk = c.usageKeys?.[0];
      if (uk !== undefined && uk !== c.label.toLowerCase()) item.usageKey = uk;
      if (c.filterText !== undefined) item.filterText = c.filterText;
      if (c.documentation !== undefined) item.documentation = c.documentation;
      scored.push({ item, key: st });
    }
    scored.sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0));
    const out: Tier1Item[] = new Array(Math.min(MAX_ITEMS, scored.length));
    for (let i = 0; i < out.length; i++) out[i] = scored[i].item;
    return out;
  }
}

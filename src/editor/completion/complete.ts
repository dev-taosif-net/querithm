// Tier-1 completion (§9 context table). Synchronous, from the cached SchemaIndex; never throws.
import type { CompletionKind, Dialect, ObjectKind, SnapColumn, SnapObject } from "../../ipc/contract";
import type { CompletionRequest, CompletionResult, CursorContext, SchemaIndex, TableInScope, Tier1Item } from "./types";
import { scan } from "./scanner";
import { findObject, isDefaultSchema, resolveTable } from "./schemaIndex";
import { bestMatch, MATCH_NONE, sortText } from "./rank";
import { suggestAlias } from "./alias";
import { getStream, type Token } from "./lexer";
import {
  afterReferenceKeywords, argValueKeywords, builtinFunctions, clauseKeywords, dataTypes, declareKeywords,
  execArgsKeywords, reservedWords, statementStartKeywords, tableContextKeywords, valuesKeywords,
} from "./keywords";
import { DIALECTS, knownDialect } from "../../engines";

const MAX_ITEMS = 500;

/** Keywords that can be the last word of a statement (`... IS NULL`, `ORDER BY x DESC`, `CASE ... END`). */
const STATEMENT_END_WORDS = new Set(["NULL", "TRUE", "FALSE", "END", "ASC", "DESC", "DEFAULT", "ONLY", "CURRENT_TIMESTAMP", "CURRENT_DATE", "CURRENT_USER"]);

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
    call: { label: "EXEC …", insertText: "EXEC ${1:procedure_name} ${2:@params};", filter: "EXEC", match: ["EXEC", "EXECUTE"] },
    namedArguments: "assign",
  },
  pg: {
    plainName: DIALECTS.pg.regularIdentifier,
    qualifyScalarFunctions: false,
    call: { label: "CALL …", insertText: "CALL ${1:procedure_name}(${2:args});", filter: "CALL", match: ["CALL"] },
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
    call: { label: "CALL …", insertText: "CALL ${1:procedure_name}(${2:args});", filter: "CALL", match: ["CALL"] },
    namedArguments: null,
  },
  duckDb: {
    plainName: DIALECTS.duckDb.regularIdentifier,
    qualifyScalarFunctions: false,
    // No stored procedures (macros are functions); CALL runs table functions.
    call: null,
    namedArguments: null,
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
    items = new Builder(ctx, req.schema ?? null, dialect, text, req.usage, req.autoAlias !== false ? offset : -1).build();
  } catch {
    items = [];
  }
  return { context: ctx, items };
}

const escSnippet = (s: string): string => s.replace(/[\\$}]/g, "\\$&");
const eq = (a: string | undefined, b: string | undefined): boolean => (a ?? "").toLowerCase() === (b ?? "").toLowerCase();

export function columnDetail(c: SnapColumn): string {
  let d = `${c.type} ${c.nullable ? "NULL" : "NOT NULL"}`;
  if (c.pk) d += " PK";
  if (c.identity) d += " IDENTITY";
  if (c.computed) d += " COMPUTED";
  return d;
}

class Builder {
  private readonly cands: Cand[] = [];
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
  ) {
    const before = ctx.prefixStart > 0 ? text[ctx.prefixStart - 1] : "";
    this.typedOpenQuote = before === "[" || before === '"';
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
    return !RULES[this.dialect].plainName.test(name) || this.reserved.has(name.toUpperCase());
  }

  private q(name: string): string {
    if (this.typedOpenQuote || !this.needsQuote(name)) return name;
    return DIALECTS[this.dialect].quote(name);
  }

  private isDefault(schema: string): boolean {
    return this.idx ? isDefaultSchema(this.idx, schema) : schema.toLowerCase() === DIALECTS[this.dialect].defaultSchema;
  }

  private labelFor(o: SnapObject): string {
    return this.isDefault(o.schema) ? o.name : `${o.schema}.${o.name}`;
  }

  private insertFor(o: SnapObject, forceQualified = false): string {
    if (this.typedOpenQuote) return this.isDefault(o.schema) && !forceQualified ? o.name : `${o.schema}.${o.name}`;
    return this.isDefault(o.schema) && !forceQualified ? this.q(o.name) : `${this.q(o.schema)}.${this.q(o.name)}`;
  }

  private objectDetail(o: SnapObject): string {
    const k = o.kind === "tvf" ? "table function" : o.kind === "func" ? "function" : o.kind === "proc" ? "procedure" : o.kind;
    return o.returnType ? `${k} · ${o.schema} → ${o.returnType}` : `${k} · ${o.schema}`;
  }

  private objectKind(o: SnapObject): CompletionKind {
    switch (o.kind) {
      case "table": case "synonym": return "table";
      case "view": return "view";
      case "proc": return "proc";
      case "type": return "type";
      default: return "func";
    }
  }

  private paramsSnippet(o: SnapObject): string {
    const ps = (o.params ?? []).filter((p) => p.name && !p.output).sort((a, b) => a.ordinal - b.ordinal);
    return ps.map((p, i) => `\${${i + 1}:${escSnippet(p.name)}}`).join(", ") || "$1";
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
    let insertText = unqualified ? (this.typedOpenQuote ? o.name : this.q(o.name)) : this.insertFor(o);
    let isSnippet = false;
    if (o.kind === "tvf") {
      insertText = `${escSnippet(insertText)}(${this.paramsSnippet(o)})`;
      isSnippet = true;
    }
    const c: Cand = {
      label, kind: this.objectKind(o), detail: this.objectDetail(o), group, match: [label, o.name],
      usageKeys: [label.toLowerCase(), `${o.schema}.${o.name}`.toLowerCase(), o.name.toLowerCase()],
    };
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
      for (const c of this.ctx.ctes) this.cands.push(this.withAlias({ label: c.name, kind: "cte", detail: "CTE", group: 1, insertText: this.q(c.name) }, c.name));
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

  private columnsOf(t: TableInScope): { cols: ColInfo[]; display: string } {
    if (t.isCte || t.isDerived) {
      return { cols: (t.columns ?? []).map((name) => ({ name })), display: t.name };
    }
    const o = resolveTable(this.idx, t);
    if (!o) return { cols: (t.columns ?? []).map((name) => ({ name })), display: t.schema ? `${t.schema}.${t.name}` : t.name };
    const cols: ColInfo[] = (o.columns ?? []).map((col) => ({ name: col.name, col }));
    if (t.columns && t.columns.length && cols.length === 0) return { cols: t.columns.map((name) => ({ name })), display: `${o.schema}.${o.name}` };
    return { cols, display: `${o.schema}.${o.name}` };
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

  private columnCands(t: TableInScope, group: number, prefixWith?: string): void {
    const { cols, display } = this.columnsOf(t);
    const kindNote = t.isCte ? "CTE " : t.isDerived ? "derived " : "";
    for (const c of cols) {
      const name = c.name;
      const label = prefixWith ? `${prefixWith}.${name}` : name;
      const ins = prefixWith ? `${this.q(prefixWith)}.${this.q(name)}` : this.q(name);
      const cand: Cand = {
        label, kind: "column", group, match: prefixWith ? [name, label] : [name],
        detail: c.col ? columnDetail(c.col) : `${kindNote}column`,
        documentation: `${display}.${name}`,
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
        this.cands.push({ label: f, kind: "func", detail: "built-in function", group: group + 1, insertText: `${f}($1)`, isSnippet: true });
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
    const nq = this.q(nearest.alias ?? nearest.name);
    const seen = new Set<string>();
    for (const t of this.ctx.tablesInScope) {
      if (t === nearest) continue;
      const tObj = resolveTable(idx, t);
      if (!tObj) continue;
      const tq = this.q(t.alias ?? t.name);
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

  /** Auto-generate complete JOIN <table> <alias> ON <condition> candidates from foreign keys for tables in scope. */
  private joinSnippets(): void {
    const idx = this.idx;
    if (!idx || !idx.fks.length || !this.ctx.tablesInScope.length) return;
    const takenAliases = new Set(this.ctx.tablesInScope.map((t) => (t.alias ?? t.name).toLowerCase()));
    const seen = new Set<string>();

    for (const t of this.ctx.tablesInScope) {
      const tObj = resolveTable(idx, t);
      if (!tObj) continue;
      const tq = this.q(t.alias ?? t.name);

      for (const fk of idx.fks) {
        if (!fk.columns?.length) continue;
        let otherId: number | undefined;
        let isParent = false;
        if (fk.parent === tObj.id) {
          otherId = fk.referenced;
          isParent = true;
        } else if (fk.referenced === tObj.id) {
          otherId = fk.parent;
          isParent = false;
        }
        if (otherId == null) continue;
        const otherObj = idx.byId.get(otherId);
        if (!otherObj) continue;

        const otherAlias = suggestAlias(otherObj.name, takenAliases);
        const oq = this.q(otherAlias);
        const cond = isParent
          ? fk.columns.map((p) => `${tq}.${this.q(p.parent)} = ${oq}.${this.q(p.referenced)}`).join(" AND ")
          : fk.columns.map((p) => `${oq}.${this.q(p.parent)} = ${tq}.${this.q(p.referenced)}`).join(" AND ");

        const targetTable = this.isDefault(otherObj.schema)
          ? this.q(otherObj.name)
          : `${this.q(otherObj.schema)}.${this.q(otherObj.name)}`;
        const joinText = `${targetTable} ${oq} ON ${cond}`;
        if (seen.has(joinText)) continue;
        seen.add(joinText);

        const label = `${otherObj.name} ON ${cond}`;
        this.cands.push({
          label,
          kind: "snippet",
          detail: `JOIN · FK ${fk.name}`,
          insertText: joinText,
          group: 0,
          match: [otherObj.name, `${otherObj.schema}.${otherObj.name}`, label],
        });
      }
    }
  }

  private statementTemplates(): void {
    const call = RULES[this.dialect].call;
    const callTemplate: Cand[] = call
      ? [{ label: call.label, kind: "snippet", detail: "statement template", insertText: call.insertText, isSnippet: true, filterText: this.ctx.prefix || call.filter, group: 0, match: call.match }]
      : [];
    this.cands.push({
      label: "INSERT INTO … VALUES …",
      kind: "snippet",
      detail: "statement template",
      insertText: "INSERT INTO ${1:table} (${2:columns})\nVALUES (${3:values});",
      isSnippet: true,
      filterText: this.ctx.prefix || "INSERT",
      group: 0,
      match: ["INSERT", "INSERT INTO", "ins"],
    });
    this.cands.push({
      label: "UPDATE … SET … WHERE …",
      kind: "snippet",
      detail: "statement template",
      insertText: "UPDATE ${1:table}\nSET ${2:column} = ${3:value}\nWHERE ${4:condition};",
      isSnippet: true,
      filterText: this.ctx.prefix || "UPDATE",
      group: 0,
      match: ["UPDATE", "upd"],
    });
    this.cands.push(...callTemplate);
    this.cands.push({
      label: "ALTER TABLE …",
      kind: "snippet",
      detail: "statement template",
      insertText: "ALTER TABLE ${1:table_name} ${2:ADD column_name data_type};",
      isSnippet: true,
      filterText: this.ctx.prefix || "ALTER",
      group: 0,
      match: ["ALTER", "ALTER TABLE", "alt"],
    });
    this.cands.push({
      label: "SELECT … FROM …",
      kind: "snippet",
      detail: "statement template",
      insertText: "SELECT ${1:*} FROM ${2:table_name};",
      isSnippet: true,
      filterText: this.ctx.prefix || "SELECT",
      group: 0,
      match: ["SELECT", "ssf"],
    });
    this.cands.push({
      label: "DELETE FROM … WHERE …",
      kind: "snippet",
      detail: "statement template",
      insertText: "DELETE FROM ${1:table_name} WHERE ${2:condition};",
      isSnippet: true,
      filterText: this.ctx.prefix || "DELETE",
      group: 0,
      match: ["DELETE", "del"],
    });
  }

  private expression(clause: string): void {
    const tables = this.ctx.tablesInScope;
    const multi = tables.length > 1;
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
    for (const t of tables) this.columnCands(t, t === nearest ? 1 : 2, multi ? t.alias ?? t.name : undefined);
    for (const t of tables) {
      if (t.alias) {
        const target = t.isDerived ? "derived table" : t.schema ? `${t.schema}.${t.name}` : t.name;
        this.cands.push({ label: t.alias, kind: "alias", detail: `alias · ${target}`, group: 3, insertText: this.q(t.alias) });
      } else {
        this.cands.push({ label: t.name, kind: t.isCte ? "cte" : "table", detail: t.isCte ? "CTE" : "table in scope", group: 3, insertText: this.q(t.name) });
      }
    }
    this.variables(4);
    this.scalarFunctions(5);
    this.keywords(clauseKeywords(clause, this.dialect), 8);
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
      const tables = this.ctx.tablesInScope;
      const lowerQ = q.toLowerCase();
      let t = tables.find((x) => x.alias && x.alias.toLowerCase() === lowerQ);
      t ??= tables.find((x) => !x.alias && (x.name.toLowerCase() === lowerQ || (x.schema && `${x.schema}.${x.name}`.toLowerCase() === lowerQ)));
      t ??= tables.find((x) => x.name.toLowerCase() === lowerQ || (x.schema !== undefined && `${x.schema}.${x.name}`.toLowerCase() === lowerQ));
      if (t) { this.columnCands(t, 1); return; }
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
      const st = sortText({ tier, match: m, group: c.group, usage: u, order: c.order ?? 0, label: c.label });
      const item: Tier1Item = { label: c.label, kind: c.kind, sortText: st };
      if (c.detail !== undefined) item.detail = c.detail;
      if (c.insertText !== undefined) item.insertText = c.insertText;
      if (c.isSnippet) item.isSnippet = true;
      if (c.alias !== undefined) item.alias = c.alias;
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

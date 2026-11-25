// Tier-1 completion contract (§9). Synchronous, in the webview, from the cached SchemaSnapshot. Never throws.
import type { CompletionKind, Dialect, SchemaSnapshot, SnapFk, SnapObject } from "../../ipc/contract";

export type Clause =
  | "select" | "from" | "join" | "on" | "where" | "groupBy" | "having" | "orderBy" | "exec" | "execArgs"
  | "insertInto" | "insertColumns" | "values" | "update" | "set" | "delete" | "declare" | "use" | "unknown";

export interface TableInScope {
  name: string;
  schema?: string;
  database?: string;
  alias?: string;
  isCte: boolean;
  isDerived: boolean;
  /** Column names known from the source text (CTE column list, derived-table projection), when tier 1 can see them. */
  columns?: string[];
  /** Offset of the reference in the source text (used to rank columns of the nearest table first). */
  offset?: number;
  /** The name / schema was written quoted: exact, never folded (D4 S12, dialects that fold unquoted names). */
  quoted?: boolean;
  schemaQuoted?: boolean;
  /** The alias was written quoted. */
  aliasQuoted?: boolean;
  /** A derived table or CTE whose select list is `*`: the tables its query reads (their columns are its columns). */
  starSources?: TableInScope[];
  /** How deeply nested the query level that names it is (0: the statement's own); an inner name hides an outer one. */
  depth?: number;
}

export interface CursorContext {
  clause: Clause;
  /** Offset of the enclosing statement. */
  statementStart: number;
  statementEnd: number;
  tablesInScope: TableInScope[];
  ctes: { name: string; columns?: string[]; quoted?: boolean }[];
  /** DECLARE @x and proc params seen above the cursor (T-SQL), plpgsql DECLARE vars (pg). */
  variables: { name: string; type?: string; kind?: "variable" | "parameter" }[];
  /** Text before the last '.', e.g. 'o' in 'o.Ord'; 'dbo' in 'dbo.Ord'; 'SalesDB.dbo' in 'SalesDB.dbo.Ord'. */
  qualifier?: string;
  /** The partial word being typed (without brackets/quotes). */
  prefix: string;
  /** Offset where `prefix` starts (the replace range is [prefixStart, offset)). */
  prefixStart: number;
  /** Proc name after EXEC (T-SQL) or CALL (pg). */
  execTarget?: string;
  /** Parameter names already written in the EXEC argument list. */
  execArgsUsed?: string[];
  /** INSERT INTO target for insertColumns/values. */
  insertTarget?: { name: string; schema?: string };
  /** Cursor is inside a string or comment: offer nothing. */
  inStringOrComment: boolean;
  /**
   * Finer position inside the clause: 'afterReference' = right after a complete table reference (next is an alias
   * or a clause keyword); 'alias' = after AS (a new name is being typed); 'name' = a new variable/column name is
   * being typed (DECLARE @|, column definitions); 'argValue' = a value position (`@p = |`, inside VALUES (...)).
   */
  slot?: "afterReference" | "alias" | "name" | "argValue";
  /** Explicit INSERT column list, when present. */
  insertColumnList?: string[];
  /** Aliases the select list defines, for ORDER BY (and GROUP BY where the engine allows them). */
  selectAliases?: string[];
  /** Non-aggregated columns extracted from SELECT projection for GROUP BY. */
  nonAggregatedColumns?: string[];
}

/** O(1) lookups over one (profile, database) snapshot. Built once per snapshot version. */
export interface SchemaIndex {
  profileId: string;
  database: string;
  dialect: Dialect;
  objects: SnapObject[];
  /** lowercase name → objects with that name (any schema). */
  byName: Map<string, SnapObject[]>;
  /** lowercase "schema.name" → object. */
  byQualified: Map<string, SnapObject>;
  byId: Map<number, SnapObject>;
  /** Distinct schema names present in the snapshot. */
  schemas: string[];
  fks: SnapFk[];
  /** Other databases on the same server (for USE and db.schema.object qualifiers). */
  databases: string[];
  /** 'dbo' for T-SQL, 'public' for pg. */
  defaultSchema: string;
}

export interface Tier1Item {
  /** The item's identity: merged with tier 2, counted for usage and ranked by it (`sales.Orders`, `Orders` in dbo). */
  label: string;
  /**
   * What the list shows when it differs from `label`: an object's bare name, its schema being in `detail` (§9). The
   * label's qualified form stays the identity, so two tables of one name in two schemas remain two items.
   */
  display?: string;
  /** Built from a schema object of the snapshot: its detail (schema, columns, rows) is kept over tier 2's. */
  schemaObject?: boolean;
  kind: CompletionKind;
  detail?: string;
  /** Text inserted; defaults to label. Snippet syntax when isSnippet. */
  insertText?: string;
  isSnippet?: boolean;
  /** Alias appended to insertText for a FROM/JOIN reference (auto-alias). */
  alias?: string;
  /** What accepting it counts as for usage ranking (lower case): a JOIN item counts for the table it joins. */
  usageKey?: string;
  /** Lexicographic rank key; lower sorts first (§9 Ranking). */
  sortText: string;
  filterText?: string;
  documentation?: string;
}

/** A table's row count from catalog statistics (meta.rowCounts); null rows: the engine has none (SQLite before ANALYZE). */
export interface RowInfo { rows: number | null; estimated: boolean }

export interface CompletionRequest {
  text: string;
  offset: number;
  dialect: Dialect;
  schema: SchemaIndex | null;
  /** Usage counts by lowercase label (procs recently used, accepted items). */
  usage?: Map<string, number>;
  /** Append a generated alias to tables accepted in FROM/JOIN (default true). */
  autoAlias?: boolean;
  /** Row counts by object id, once loaded (table items show them; "rows n/a" where the engine has none). */
  rowCounts?: Map<number, RowInfo>;
}

export interface CompletionResult {
  context: CursorContext;
  items: Tier1Item[];
}

export type { SchemaSnapshot };

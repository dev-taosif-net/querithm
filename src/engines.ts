// Engine and dialect traits for the webview (Phase D0e). Every UI decision that depends on the engine or the SQL
// dialect reads one of these tables or switches exhaustively over `Engine` / `Dialect`, so appending a value to the
// generated union (src/ipc/contract.ts) fails to compile wherever a decision is missing, instead of silently treating
// the new engine as SQL Server. Facts the sidecar owns (capabilities, quoting of generated SQL) stay in the sidecar.
import type { AuthKind, Dialect, Engine, LocationKind } from "./ipc/contract";

/** Compile-time exhaustiveness for `switch` statements over a union: the `default` branch receives `never`. */
export function assertNever(x: never): never {
  throw new Error(`Unhandled value: ${String(x)}`);
}

export interface DialectTraits {
  /** The language's name in the UI ("T-SQL"). */
  label: string;
  /** Monaco language id (registered in editor/monaco.ts). */
  language: string;
  /**
   * The schema an unqualified name resolves to, and that titles and completion leave out. Null where the database is
   * the schema (MySQL): nothing is left out of titles, and completion treats the current database as the default.
   */
  defaultSchema: string | null;
  /** Delimits an identifier, doubling the closing delimiter inside it: `[a]]b]`, `"a""b"`, `` `a``b` ``. */
  quote(name: string): string;
  /** A name that needs no delimiters (reserved words aside, see quoteIdentifier). */
  regularIdentifier: RegExp;
  /**
   * How the server spells an unquoted identifier: folded to lower case (PostgreSQL) or upper case (Oracle), after which
   * names compare exactly and a quoted name is exact; null where names compare case-insensitively, quoted or not, and
   * keep the case they were written in (SQL Server's default collation, SQLite, MySQL on its default settings, DuckDB).
   */
  identifierFold: "lower" | "upper" | null;
  /** A string literal holding `text` for SQL the user copies (values sent to the server travel as parameters). */
  stringLiteral(text: string): string;
  /** `USE db` changes the session's database, and the editor shows the database the script switches to. */
  useStatement: boolean;
  /** The sidecar parses the text for syntax diagnostics (`lang.diagnostics`). */
  serverDiagnostics: boolean;
  /** Routine and DO bodies are dollar-quoted strings (`AS $$ … $$`) holding code the editor reads as SQL. */
  dollarQuotedBodies: boolean;
  /**
   * Unqualified names resolve to the signed-in user's own schema (Oracle: the user is the schema), so tier-1 completion
   * takes the profile's user name, folded, as the default schema; `defaultSchema` is then null.
   */
  loginSchema: boolean;
}

/**
 * The catalog spelling of a name as written: a quoted name is exact; an unquoted one is folded as the server folds it
 * (`identifierFold`), or kept as written where the dialect compares names case-insensitively instead.
 */
export function foldIdentifier(name: string, quoted: boolean, dialect: Dialect): string {
  if (quoted) return name;
  const fold = DIALECTS[dialect].identifierFold;
  return fold === "lower" ? name.toLowerCase() : fold === "upper" ? name.toUpperCase() : name;
}

const bracket = (name: string) => `[${name.replace(/]/g, "]]")}]`;
const doubleQuote = (name: string) => `"${name.replace(/"/g, '""')}"`;
const backtick = (name: string) => `\`${name.replace(/`/g, "``")}\``;
const singleQuoted = (text: string) => `'${text.replace(/'/g, "''")}'`;

/** Identifier delimiters by style, for keeping the style of text the user wrote (`[x]`, `"x"` or `` `x` ``). */
export const DELIMIT = { bracket, doubleQuote, backtick } as const;

export const DIALECTS: Record<Dialect, DialectTraits> = {
  tsql: {
    label: "T-SQL",
    language: "tsql",
    defaultSchema: "dbo",
    quote: bracket,
    regularIdentifier: /^[A-Za-z_#][A-Za-z0-9_@#$]*$/,
    identifierFold: null,
    // N'' keeps characters outside the code page of a varchar literal.
    stringLiteral: (text) => (/[^\x00-\x7f]/.test(text) ? `N${singleQuoted(text)}` : singleQuoted(text)),
    useStatement: true,
    serverDiagnostics: true,
    dollarQuotedBodies: false,
    loginSchema: false,
  },
  pg: {
    label: "PostgreSQL",
    language: "pgsql",
    defaultSchema: "public",
    quote: doubleQuote,
    regularIdentifier: /^[a-z_][a-z0-9_$]*$/,
    identifierFold: "lower",
    // E'' with doubled backslashes reads the same whatever standard_conforming_strings is.
    stringLiteral: (text) => (text.includes("\\") ? `E${singleQuoted(text).replace(/\\/g, "\\\\")}` : singleQuoted(text)),
    useStatement: false,
    serverDiagnostics: false,
    dollarQuotedBodies: true,
    loginSchema: false,
  },
  sqlite: {
    label: "SQLite",
    language: "sqlite",
    defaultSchema: "main",
    quote: doubleQuote,
    regularIdentifier: /^[A-Za-z_][A-Za-z0-9_]*$/,
    identifierFold: null,
    stringLiteral: singleQuoted,
    useStatement: false,
    serverDiagnostics: false,
    dollarQuotedBodies: false,
    loginSchema: false,
  },
  mySql: {
    label: "MySQL",
    language: "mysql",
    defaultSchema: null,
    quote: backtick,
    regularIdentifier: /^[A-Za-z_$][A-Za-z0-9_$]*$/,
    identifierFold: null,
    // A backslash escapes in MySQL strings (unless NO_BACKSLASH_ESCAPES): doubled, it reads as itself either way.
    stringLiteral: (text) => singleQuoted(text).replace(/\\/g, "\\\\"),
    useStatement: true,
    serverDiagnostics: false,
    dollarQuotedBodies: false,
    loginSchema: false,
  },
  duckDb: {
    label: "DuckDB",
    language: "duckdb",
    defaultSchema: "main",
    quote: doubleQuote,
    regularIdentifier: /^[A-Za-z_][A-Za-z0-9_$]*$/,
    // DuckDB keeps the case of names and compares them case-insensitively, quoted or not.
    identifierFold: null,
    stringLiteral: singleQuoted,
    // USE changes the default catalog / schema of the session, never the connection's database.
    useStatement: false,
    serverDiagnostics: false,
    dollarQuotedBodies: false,
    loginSchema: false,
  },
  // Phase D4 (hidden engine). Unquoted names fold to UPPER case; the schema is the signed-in user's (or the one ALTER
  // SESSION SET CURRENT_SCHEMA names), which the sidecar reports, so none is assumed here. Oracle names cannot hold '"' at
  // all (the sidecar's quoting refuses one), so the doubling below never reaches the server.
  oracle: {
    label: "Oracle SQL",
    language: "oracle",
    defaultSchema: null,
    quote: doubleQuote,
    regularIdentifier: /^[A-Z][A-Z0-9_$#]*$/,
    identifierFold: "upper",
    // No backslash escapes in Oracle strings: quotes doubled is the whole rule.
    stringLiteral: singleQuoted,
    // ALTER SESSION SET CURRENT_SCHEMA selects a schema; the service is the connection target, never switched.
    useStatement: false,
    serverDiagnostics: false,
    dollarQuotedBodies: false,
    loginSchema: true,
  },
};

export interface EngineTraits {
  /** Full product name ("Microsoft SQL Server"). */
  label: string;
  /** Short name for chips and screen-reader text ("SQL Server"). */
  shortLabel: string;
  /** Two-letter badge for compact lists. */
  badge: string;
  dialect: Dialect;
  /** Monaco language id of the engine's dialect. */
  language: string;
  /** The engine's default schema (dbo, public, main); null where the database is the schema (MySQL). */
  defaultSchema: string | null;
  /** The profile editor's new-profile defaults. Port and user are null for a database on this computer (SQLite). */
  defaultPort: number | null;
  defaultAuth: AuthKind;
  defaultUser: string | null;
  /** Null: the login's default database. */
  defaultDatabase: string | null;
  /** SET STATISTICS IO / TIME output in Messages (Settings ▸ Query). */
  statisticsIo: boolean;
  statisticsTime: boolean;
  /**
   * How an actual plan is collected. `withRun`: the server returns it while the query runs (SET STATISTICS XML), so it
   * can be included on every run. `explainAnalyze`: EXPLAIN ANALYZE executes the statement, so the user is asked first.
   * `none`: Querithm shows only the estimated plan (the sidecar refuses actual plans).
   */
  actualPlan: "withRun" | "explainAnalyze" | "none";
  /**
   * `explainAnalyze` engines: the confirm before an actual plan, true for what the sidecar does on this engine (§11) —
   * the statement runs inside a transaction that is always rolled back (a savepoint inside the user's open one where the
   * engine has savepoints; refused there where it has none). Null for the other modes.
   */
  actualPlanConfirm: string | null;
  /** Extended Events trace (Trace tab). */
  trace: boolean;
  /** Step debugger for routines (Debug tab). */
  debugger: boolean;
  /** Synonym objects in the Object Explorer. */
  synonyms: boolean;
  /** A trigger's body is a separate trigger function, which Modify opens (PostgreSQL); else the trigger itself. */
  triggerFunctions: boolean;
  /** Stored procedures and functions that a statement calls (Execute…, Script as ▸ EXECUTE). */
  procedures: boolean;
  /** The Activity monitor (sessions, waits, top queries). */
  monitor: boolean;
  /**
   * Server output a run collects when asked (Oracle DBMS_OUTPUT): the Messages pane shows a per-tab toggle, sent as
   * ExecuteOptions.serverOutput. Other engines' PRINT / NOTICE / warnings always arrive and have no toggle.
   */
  serverOutput: boolean;
  /**
   * Where the engine's databases live: on a server, or on this computer (a granted file, or memory). The sidecar's
   * `system.engines` says the same; this is the fallback when it cannot be asked.
   */
  locations: LocationKind[];
}

export const ENGINES: Record<Engine, EngineTraits> = {
  mssql: {
    label: "Microsoft SQL Server",
    shortLabel: "SQL Server",
    badge: "MS",
    dialect: "tsql",
    language: DIALECTS.tsql.language,
    defaultSchema: DIALECTS.tsql.defaultSchema,
    defaultPort: 1433,
    defaultAuth: "sql",
    defaultUser: "sa",
    defaultDatabase: null,
    statisticsIo: true,
    statisticsTime: true,
    actualPlan: "withRun",
    actualPlanConfirm: null,
    trace: true,
    debugger: false,
    synonyms: true,
    triggerFunctions: false,
    procedures: true,
    monitor: true,
    serverOutput: false,
    locations: ["server"],
  },
  pg: {
    label: "PostgreSQL",
    shortLabel: "PostgreSQL",
    badge: "PG",
    dialect: "pg",
    language: DIALECTS.pg.language,
    defaultSchema: DIALECTS.pg.defaultSchema,
    defaultPort: 5432,
    defaultAuth: "password",
    defaultUser: "postgres",
    defaultDatabase: "postgres",
    statisticsIo: false,
    statisticsTime: false,
    actualPlan: "explainAnalyze",
    actualPlanConfirm:
      "EXPLAIN ANALYZE executes the statement. Querithm runs it inside a transaction and rolls it back; inside your open transaction it goes back to a savepoint, so your own changes stay. Sequence values it draws and anything it does outside this database (dblink, extensions that call out) are not undone.",
    trace: false,
    debugger: true,
    synonyms: false,
    triggerFunctions: true,
    procedures: true,
    monitor: true,
    serverOutput: false,
    locations: ["server"],
  },
  sqlite: {
    label: "SQLite",
    shortLabel: "SQLite",
    badge: "SL",
    dialect: "sqlite",
    language: DIALECTS.sqlite.language,
    defaultSchema: DIALECTS.sqlite.defaultSchema,
    defaultPort: null,
    defaultAuth: "none",
    defaultUser: null,
    defaultDatabase: "main",
    statisticsIo: false,
    statisticsTime: false,
    actualPlan: "none",
    actualPlanConfirm: null,
    trace: false,
    debugger: false,
    synonyms: false,
    triggerFunctions: false,
    procedures: false,
    monitor: false,
    serverOutput: false,
    locations: ["file", "memory"],
  },
  mySql: {
    label: "MySQL / MariaDB",
    shortLabel: "MySQL",
    badge: "MY",
    dialect: "mySql",
    language: DIALECTS.mySql.language,
    defaultSchema: DIALECTS.mySql.defaultSchema,
    defaultPort: 3306,
    defaultAuth: "password",
    defaultUser: "root",
    defaultDatabase: null,
    statisticsIo: false,
    statisticsTime: false,
    actualPlan: "none",
    actualPlanConfirm: null,
    trace: false,
    debugger: false,
    synonyms: false,
    triggerFunctions: false,
    procedures: true,
    monitor: false,
    serverOutput: false,
    locations: ["server"],
  },
  duckDb: {
    label: "DuckDB",
    shortLabel: "DuckDB",
    badge: "DK",
    dialect: "duckDb",
    language: DIALECTS.duckDb.language,
    defaultSchema: DIALECTS.duckDb.defaultSchema,
    defaultPort: null,
    defaultAuth: "none",
    defaultUser: null,
    // The catalog is named after the file (memory for an in-memory database): the sidecar reports it.
    defaultDatabase: null,
    statisticsIo: false,
    statisticsTime: false,
    actualPlan: "explainAnalyze",
    actualPlanConfirm:
      "EXPLAIN ANALYZE executes the statement. Querithm runs it inside a transaction and rolls it back, so its changes to data and tables are undone. DuckDB has no savepoints: while a transaction of yours is open, the actual plan is refused instead.",
    trace: false,
    debugger: false,
    synonyms: false,
    triggerFunctions: false,
    procedures: false,
    monitor: false,
    serverOutput: false,
    locations: ["file", "memory"],
  },
  // Phase D4, hidden engine (the picker offers only what the sidecar exposes). Since S06 the editor, profile dialog and
  // Messages know it; plans, procedures, synonyms, the monitor and the rest stay off until their session (D4 S08–S12).
  oracle: {
    label: "Oracle Database",
    shortLabel: "Oracle",
    badge: "OR",
    dialect: "oracle",
    language: DIALECTS.oracle.language,
    defaultSchema: DIALECTS.oracle.defaultSchema,
    // TCPS is the default transport (§13.2), so the default port is the TCPS listener's; a profile that downgrades to
    // plain TCP defaults to 1521 (the sidecar's OracleConnectionStrings, the profile dialog's placeholder).
    defaultPort: 2484,
    defaultAuth: "password",
    defaultUser: "system",
    // The service name (Easy Connect host:port/service) is the connection target; the profile names it.
    defaultDatabase: null,
    statisticsIo: false,
    statisticsTime: false,
    actualPlan: "none",
    actualPlanConfirm: null,
    trace: false,
    debugger: false,
    synonyms: false,
    triggerFunctions: false,
    procedures: false,
    monitor: false,
    serverOutput: true,
    locations: ["server"],
  },
};

/** Engines in display order (the profile editor's picker, the empty explorer). */
export const ENGINE_LIST = Object.keys(ENGINES) as Engine[];
export const DIALECT_LIST = Object.keys(DIALECTS) as Dialect[];

/**
 * The engine assumed where none is known yet: a tab without a connection, a new profile. Its dialect is the editor's
 * language before a connection is picked.
 */
export const DEFAULT_ENGINE: Engine = "mssql";
export const DEFAULT_DIALECT: Dialect = ENGINES[DEFAULT_ENGINE].dialect;

/**
 * The traits of an engine; no engine (no connection) reads as the default engine. A value outside the union (text a
 * newer build stored that reached the webview without the sidecar's `unsupported` mapping) also reads as the default
 * rather than failing a render; the sidecar never opens such a profile.
 */
export function engineTraits(engine: Engine | null | undefined): EngineTraits {
  return ENGINES[knownEngine(engine)];
}

export function dialectOf(engine: Engine | null | undefined): Dialect {
  return engineTraits(engine).dialect;
}

/** A dialect from untyped input (a stored snippet, a request): unknown text is the default dialect. */
export function knownDialect(d: unknown): Dialect {
  return isDialect(d) ? d : DEFAULT_DIALECT;
}

export function isEngine(x: unknown): x is Engine {
  return typeof x === "string" && Object.prototype.hasOwnProperty.call(ENGINES, x);
}

/** An engine from untyped input; anything else (or none) is the default engine. */
export function knownEngine(e: unknown): Engine {
  return isEngine(e) ? e : DEFAULT_ENGINE;
}

export function isDialect(x: unknown): x is Dialect {
  return typeof x === "string" && Object.prototype.hasOwnProperty.call(DIALECTS, x);
}

/** The dialect a Monaco language id belongs to, if any. */
export function dialectOfLanguage(languageId: string): Dialect | undefined {
  return DIALECT_LIST.find((d) => DIALECTS[d].language === languageId);
}

/** The dialect of a model's language; another language (plain text) reads as the default dialect. */
export function dialectForLanguage(languageId: string): Dialect {
  return dialectOfLanguage(languageId) ?? DEFAULT_DIALECT;
}

/** The actual plan comes back with the query's own run (SET STATISTICS XML), so a run can include it (Explain ▾). */
export function actualPlanWithRun(engine: Engine | null | undefined): boolean {
  const mode = engineTraits(engine).actualPlan;
  switch (mode) {
    case "withRun": return true;
    case "explainAnalyze": return false;
    case "none": return false;
    default: return assertNever(mode);
  }
}

/** Monaco language id for an engine; no engine is the default engine's. */
export function languageOf(engine: Engine | null | undefined): string {
  return engineTraits(engine).language;
}

/**
 * A database on this computer (SQLite: a granted file or memory): the sidecar gives each tab one connection of its own
 * ("session-owned", Phase D0d), so a tab's schema view is its session's — an in-memory database's own objects, or the
 * shared file snapshot plus that session's temporary objects. Server engines share one snapshot per profile + database.
 */
export function isLocalEngine(engine: Engine | null | undefined): boolean {
  return !engineTraits(engine).locations.includes("server");
}

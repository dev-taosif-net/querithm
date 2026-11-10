// Phase D0e: the engine and dialect tables cover every value of the generated unions, every entry answers every trait,
// the helpers keep today's SQL Server / PostgreSQL decisions, and SQLite / MySQL get their own.
import { describe, expect, it } from "vitest";
import { DialectValues, EngineValues, type Dialect, type Engine } from "../ipc/contract";
import {
  actualPlanWithRun, assertNever, DEFAULT_DIALECT, DEFAULT_ENGINE, DELIMIT, DIALECT_LIST, DIALECTS, dialectForLanguage, dialectOf,
  dialectOfLanguage, ENGINE_LIST, ENGINES, engineTraits, isDialect, isEngine, isLocalEngine, knownDialect, knownEngine, languageOf,
  type DialectTraits, type EngineTraits,
} from "../engines";

// One name per trait: a trait added to the interfaces without a test fails `satisfies` below.
const ENGINE_TRAITS = {
  label: "string", shortLabel: "string", badge: "string", dialect: "string", language: "string", defaultSchema: "string|null",
  defaultPort: "number|null", defaultAuth: "string", defaultUser: "string|null", defaultDatabase: "string|null", statisticsIo: "boolean",
  statisticsTime: "boolean", actualPlan: "string", actualPlanConfirm: "string|null", trace: "boolean", debugger: "boolean", synonyms: "boolean", triggerFunctions: "boolean",
  procedures: "boolean", monitor: "boolean", locations: "object",
} satisfies Record<keyof EngineTraits, string>;

const DIALECT_TRAITS = {
  label: "string", language: "string", defaultSchema: "string|null", quote: "function", regularIdentifier: "object", foldsIdentifiers: "boolean",
  stringLiteral: "function", useStatement: "boolean", serverDiagnostics: "boolean", dollarQuotedBodies: "boolean",
} satisfies Record<keyof DialectTraits, string>;

const typeOf = (v: unknown) => (v === null ? "null" : typeof v);

describe("engine table", () => {
  it("has exactly the generated engines, in a stable list", () => {
    expect([...ENGINE_LIST].sort()).toEqual([...EngineValues].sort());
    expect(ENGINE_LIST.every(isEngine)).toBe(true);
  });

  it.each(ENGINE_LIST)("%s answers every trait with a value of the right type", (e) => {
    const t = ENGINES[e];
    expect(Object.keys(t).sort()).toEqual(Object.keys(ENGINE_TRAITS).sort());
    for (const [k, want] of Object.entries(ENGINE_TRAITS)) expect(want.split("|"), `${e}.${k}`).toContain(typeOf(t[k as keyof EngineTraits]));
    expect(t.label && t.shortLabel && t.badge).toBeTruthy();
    expect(["withRun", "explainAnalyze", "none"]).toContain(t.actualPlan);
    // Only an engine that asks before EXPLAIN ANALYZE has the confirm's text, and it says the run is rolled back.
    expect(t.actualPlanConfirm !== null).toBe(t.actualPlan === "explainAnalyze");
    if (t.actualPlanConfirm) expect(t.actualPlanConfirm).toContain("rolls it back");
    expect(t.locations.length).toBeGreaterThan(0);
    // A server engine has a port and a default user; a database on this computer has neither.
    const server = t.locations.includes("server");
    expect(t.defaultPort === null).toBe(!server);
    expect(t.defaultUser === null).toBe(!server);
    if (server) expect(t.defaultPort).toBeGreaterThan(0);
  });

  it.each(ENGINE_LIST)("%s: language and default schema come from its dialect", (e) => {
    const t = ENGINES[e];
    expect(isDialect(t.dialect)).toBe(true);
    expect(t.language).toBe(DIALECTS[t.dialect].language);
    expect(t.defaultSchema).toBe(DIALECTS[t.dialect].defaultSchema);
  });

  it("keeps today's SQL Server and PostgreSQL facts", () => {
    expect(ENGINES.mssql).toMatchObject({
      label: "Microsoft SQL Server", shortLabel: "SQL Server", badge: "MS", dialect: "tsql", language: "tsql", defaultSchema: "dbo",
      defaultPort: 1433, defaultAuth: "sql", defaultUser: "sa", defaultDatabase: null, statisticsIo: true, statisticsTime: true,
      actualPlan: "withRun", trace: true, debugger: false, synonyms: true, triggerFunctions: false, procedures: true, monitor: true,
      locations: ["server"],
    });
    expect(ENGINES.pg).toMatchObject({
      label: "PostgreSQL", shortLabel: "PostgreSQL", badge: "PG", dialect: "pg", language: "pgsql", defaultSchema: "public",
      defaultPort: 5432, defaultAuth: "password", defaultUser: "postgres", defaultDatabase: "postgres", statisticsIo: false,
      statisticsTime: false, actualPlan: "explainAnalyze", trace: false, debugger: true, synonyms: false, triggerFunctions: true,
      procedures: true, monitor: true, locations: ["server"],
    });
  });

  it("SQLite: a database file or memory, no sign-in, no actual plans, procedures, monitor, trace or debugger", () => {
    expect(ENGINES.sqlite).toEqual({
      label: "SQLite", shortLabel: "SQLite", badge: "SL", dialect: "sqlite", language: "sqlite", defaultSchema: "main", defaultPort: null,
      defaultAuth: "none", defaultUser: null, defaultDatabase: "main", statisticsIo: false, statisticsTime: false, actualPlan: "none",
      actualPlanConfirm: null, trace: false, debugger: false, synonyms: false, triggerFunctions: false, procedures: false, monitor: false, locations: ["file", "memory"],
    });
  });

  it("MySQL / MariaDB: a server engine with procedures; no default schema (the database is the schema), no actual plans yet", () => {
    expect(ENGINES.mySql).toEqual({
      label: "MySQL / MariaDB", shortLabel: "MySQL", badge: "MY", dialect: "mySql", language: "mysql", defaultSchema: null, defaultPort: 3306,
      defaultAuth: "password", defaultUser: "root", defaultDatabase: null, statisticsIo: false, statisticsTime: false, actualPlan: "none",
      actualPlanConfirm: null, trace: false, debugger: false, synonyms: false, triggerFunctions: false, procedures: true, monitor: false, locations: ["server"],
    });
  });
});

describe("Oracle (Phase D4, hidden engine placeholder)", () => {
  it("is a server engine with every feature off until the session that implements it", () => {
    expect(ENGINES.oracle).toEqual({
      label: "Oracle Database", shortLabel: "Oracle", badge: "OR", dialect: "oracle", language: "oracle", defaultSchema: null,
      defaultPort: 1521, defaultAuth: "password", defaultUser: "system", defaultDatabase: null, statisticsIo: false,
      statisticsTime: false, actualPlan: "none", actualPlanConfirm: null, trace: false, debugger: false, synonyms: false,
      triggerFunctions: false, procedures: false, monitor: false, locations: ["server"],
    });
    expect(isLocalEngine("oracle")).toBe(false);
    expect(actualPlanWithRun("oracle")).toBe(false);
  });

  it("quotes with \"\", has no USE statement, and only an upper-case name is a regular identifier", () => {
    expect(DIALECTS.oracle).toMatchObject({ label: "Oracle SQL", defaultSchema: null, useStatement: false, serverDiagnostics: false, dollarQuotedBodies: false });
    expect(DIALECTS.oracle.quote).toBe(DELIMIT.doubleQuote);
    expect(DIALECTS.oracle.regularIdentifier.test("ORDER_LINES$2")).toBe(true);
    expect(DIALECTS.oracle.regularIdentifier.test("T#1")).toBe(true);
    expect(DIALECTS.oracle.regularIdentifier.test("Orders")).toBe(false);
    expect(DIALECTS.oracle.regularIdentifier.test("_X")).toBe(false);
    // No backslash escapes: only quotes are doubled.
    expect(DIALECTS.oracle.stringLiteral("C:\\temp's")).toBe("'C:\\temp''s'");
    expect(languageOf("oracle")).toBe("oracle");
    expect(dialectOf("oracle")).toBe("oracle");
  });
});

describe("dialect table", () => {
  it("has exactly the generated dialects", () => {
    expect([...DIALECT_LIST].sort()).toEqual([...DialectValues].sort());
  });

  it.each(DIALECT_LIST)("%s answers every trait with a value of the right type", (d) => {
    const t = DIALECTS[d];
    expect(Object.keys(t).sort()).toEqual(Object.keys(DIALECT_TRAITS).sort());
    for (const [k, want] of Object.entries(DIALECT_TRAITS)) expect(want.split("|"), `${d}.${k}`).toContain(typeOf(t[k as keyof DialectTraits]));
    expect(t.regularIdentifier).toBeInstanceOf(RegExp);
  });

  it("gives every dialect its own Monaco language id", () => {
    const ids = DIALECT_LIST.map((d) => DIALECTS[d].language);
    expect(new Set(ids).size).toBe(ids.length);
    expect(DIALECTS.tsql.language).toBe("tsql");
    expect(DIALECTS.pg.language).toBe("pgsql");
    expect(DIALECTS.sqlite.language).toBe("sqlite");
    expect(DIALECTS.mySql.language).toBe("mysql");
  });

  it("keeps today's per-dialect facts", () => {
    expect(DIALECTS.tsql).toMatchObject({ label: "T-SQL", defaultSchema: "dbo", foldsIdentifiers: false, useStatement: true, serverDiagnostics: true, dollarQuotedBodies: false });
    expect(DIALECTS.pg).toMatchObject({ label: "PostgreSQL", defaultSchema: "public", foldsIdentifiers: true, useStatement: false, serverDiagnostics: false, dollarQuotedBodies: true });
    expect(DIALECTS.sqlite).toMatchObject({ label: "SQLite", defaultSchema: "main", foldsIdentifiers: false, useStatement: false, serverDiagnostics: false, dollarQuotedBodies: false });
    expect(DIALECTS.mySql).toMatchObject({ label: "MySQL", defaultSchema: null, foldsIdentifiers: false, useStatement: true, serverDiagnostics: false, dollarQuotedBodies: false });
    expect(DIALECTS.duckDb).toMatchObject({ label: "DuckDB", defaultSchema: "main", foldsIdentifiers: false, useStatement: false, serverDiagnostics: false, dollarQuotedBodies: false });
  });
});

describe("identifier quoting table", () => {
  const cases: Record<Dialect, [name: string, quoted: string][]> = {
    tsql: [["Orders", "[Orders]"], ["Odd]Name", "[Odd]]Name]"], ["a]]b", "[a]]]]b]"], ["]", "[]]]"], ['say "hi"', '[say "hi"]'], ["", "[]"]],
    pg: [["orders", '"orders"'], ['we"ird', '"we""ird"'], ['a""b', '"a""""b"'], ['"', '""""'], ["x]y", '"x]y"'], ["", '""']],
    sqlite: [["Orders", '"Orders"'], ['we"ird', '"we""ird"'], ["x]y", '"x]y"'], ["a`b", '"a`b"'], ["", '""']],
    mySql: [["Orders", "`Orders`"], ["we`ird", "`we``ird`"], ["``", "``````"], ['say "hi"', '`say "hi"`'], ["x]y", "`x]y`"], ["", "``"]],
    duckDb: [["Orders", '"Orders"'], ['we"ird', '"we""ird"'], ["x]y", '"x]y"'], ["{a}", '"{a}"'], ["", '""']],
    oracle: [["ORDERS", '"ORDERS"'], ['we"ird', '"we""ird"'], ["x]y", '"x]y"'], ["", '""']],
  };
  it.each(DIALECT_LIST)("%s delimits and doubles its closing delimiter", (d) => {
    for (const [name, quoted] of cases[d]) expect(DIALECTS[d].quote(name)).toBe(quoted);
  });

  it("keeps the style of text the user wrote whatever the dialect", () => {
    expect(DELIMIT.bracket("a]b")).toBe("[a]]b]");
    expect(DELIMIT.doubleQuote('a"b')).toBe('"a""b"');
    expect(DELIMIT.backtick("a`b")).toBe("`a``b`");
    expect(DIALECTS.tsql.quote).toBe(DELIMIT.bracket);
    expect(DIALECTS.pg.quote).toBe(DELIMIT.doubleQuote);
    expect(DIALECTS.sqlite.quote).toBe(DELIMIT.doubleQuote);
    expect(DIALECTS.mySql.quote).toBe(DELIMIT.backtick);
    expect(DIALECTS.duckDb.quote).toBe(DELIMIT.doubleQuote);
  });

  it("regular identifiers: T-SQL is case-insensitive and takes #temp names; PostgreSQL only folded lower case", () => {
    expect(DIALECTS.tsql.regularIdentifier.test("OrderLines")).toBe(true);
    expect(DIALECTS.tsql.regularIdentifier.test("#tmp")).toBe(true);
    expect(DIALECTS.tsql.regularIdentifier.test("@v")).toBe(false);
    expect(DIALECTS.tsql.regularIdentifier.test("my col")).toBe(false);
    expect(DIALECTS.pg.regularIdentifier.test("order_lines$1")).toBe(true);
    expect(DIALECTS.pg.regularIdentifier.test("Orders")).toBe(false);
    expect(DIALECTS.pg.regularIdentifier.test("1x")).toBe(false);
    expect(DIALECTS.sqlite.regularIdentifier.test("OrderLines")).toBe(true);
    expect(DIALECTS.sqlite.regularIdentifier.test("a$b")).toBe(false);
    expect(DIALECTS.sqlite.regularIdentifier.test("#t")).toBe(false);
    expect(DIALECTS.mySql.regularIdentifier.test("$total_2")).toBe(true);
    expect(DIALECTS.mySql.regularIdentifier.test("Order Lines")).toBe(false);
  });
});

describe("string literals for copied SQL", () => {
  it("T-SQL: N'' only for text outside ASCII, quotes doubled", () => {
    expect(DIALECTS.tsql.stringLiteral("it's")).toBe("'it''s'");
    expect(DIALECTS.tsql.stringLiteral("Größe")).toBe("N'Größe'");
    expect(DIALECTS.tsql.stringLiteral("C:\\temp")).toBe("'C:\\temp'");
  });
  it("PostgreSQL: E'' with doubled backslashes only when the text holds one", () => {
    expect(DIALECTS.pg.stringLiteral("it's")).toBe("'it''s'");
    expect(DIALECTS.pg.stringLiteral("Größe")).toBe("'Größe'");
    expect(DIALECTS.pg.stringLiteral("C:\\temp's")).toBe("E'C:\\\\temp''s'");
  });
  it("SQLite: plain '…' with quotes doubled; no prefix and backslashes as they are", () => {
    expect(DIALECTS.sqlite.stringLiteral("it's")).toBe("'it''s'");
    expect(DIALECTS.sqlite.stringLiteral("Größe")).toBe("'Größe'");
    expect(DIALECTS.sqlite.stringLiteral("C:\\temp")).toBe("'C:\\temp'");
  });
  it("MySQL: '…' with quotes and backslashes doubled", () => {
    expect(DIALECTS.mySql.stringLiteral("it's")).toBe("'it''s'");
    expect(DIALECTS.mySql.stringLiteral("C:\\temp's")).toBe("'C:\\\\temp''s'");
  });
});

describe("helpers", () => {
  it("isLocalEngine: a database on this computer (session-owned, Phase D0d), never a server engine; no engine is the default's", () => {
    expect(isLocalEngine("sqlite")).toBe(true);
    for (const e of ["mssql", "pg", "mySql"] as Engine[]) expect(isLocalEngine(e)).toBe(false);
    for (const e of ENGINE_LIST) expect(isLocalEngine(e)).toBe(!ENGINES[e].locations.includes("server"));
    expect(isLocalEngine(undefined)).toBe(false);
  });

  it("dialectOf maps every engine to its dialect; no engine is the default engine's", () => {
    expect(dialectOf("mssql")).toBe("tsql");
    expect(dialectOf("pg")).toBe("pg");
    expect(dialectOf("sqlite")).toBe("sqlite");
    expect(dialectOf("mySql")).toBe("mySql");
    for (const e of ENGINE_LIST) expect(dialectOf(e)).toBe(ENGINES[e].dialect);
    expect(DEFAULT_ENGINE).toBe("mssql");
    expect(DEFAULT_DIALECT).toBe("tsql");
    expect(dialectOf(undefined)).toBe(DEFAULT_DIALECT);
    expect(dialectOf(null)).toBe(DEFAULT_DIALECT);
  });

  it("engineTraits reads unknown text as the default engine instead of failing", () => {
    expect(engineTraits(undefined)).toBe(ENGINES[DEFAULT_ENGINE]);
    expect(engineTraits("duckdb" as Engine)).toBe(ENGINES[DEFAULT_ENGINE]);
    expect(engineTraits("constructor" as Engine)).toBe(ENGINES[DEFAULT_ENGINE]);
    expect(engineTraits("pg")).toBe(ENGINES.pg);
  });

  it("knownEngine / knownDialect / isEngine / isDialect accept only the unions' values", () => {
    expect(knownEngine("pg")).toBe("pg");
    expect(knownEngine(undefined)).toBe(DEFAULT_ENGINE);
    expect(knownEngine("constructor")).toBe(DEFAULT_ENGINE);
    expect(knownDialect("pg")).toBe("pg");
    expect(knownDialect(null)).toBe(DEFAULT_DIALECT);
    expect(knownDialect("toString")).toBe(DEFAULT_DIALECT);
    expect(isEngine("tsql")).toBe(false);
    expect(isDialect("mssql")).toBe(false);
    expect(isDialect("pg")).toBe(true);
  });

  it("maps Monaco language ids back to dialects", () => {
    for (const d of DIALECT_LIST) expect(dialectOfLanguage(DIALECTS[d].language)).toBe(d);
    expect(dialectOfLanguage("plaintext")).toBeUndefined();
    expect(dialectForLanguage("pgsql")).toBe("pg");
    expect(dialectForLanguage("plaintext")).toBe(DEFAULT_DIALECT);
  });

  it("languageOf gives the engine's Monaco language", () => {
    expect(languageOf("mssql")).toBe("tsql");
    expect(languageOf("pg")).toBe("pgsql");
    expect(languageOf("sqlite")).toBe("sqlite");
    expect(languageOf("mySql")).toBe("mysql");
    expect(languageOf(undefined)).toBe("tsql");
  });

  it("actualPlanWithRun: SQL Server includes the actual plan with a run; the other engines never do", () => {
    expect(actualPlanWithRun("mssql")).toBe(true);
    expect(actualPlanWithRun("pg")).toBe(false);
    expect(actualPlanWithRun("sqlite")).toBe(false);
    expect(actualPlanWithRun("mySql")).toBe(false);
    for (const e of ENGINE_LIST) expect(actualPlanWithRun(e)).toBe(ENGINES[e].actualPlan === "withRun");
  });

  it("assertNever throws for a value the switch did not handle", () => {
    expect(() => assertNever("duckdb" as never)).toThrow(/duckdb/);
  });
});

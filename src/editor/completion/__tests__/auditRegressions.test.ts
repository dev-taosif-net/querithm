// Tier-1 regressions from the completion audit of 2026-11-25 (docs/completion-audit-2026-11-25.md), every engine.
// Each case returned no columns (or the wrong statement) before its fix; the "still" cases pin what the fixes keep.
import { describe, expect, it } from "vitest";
import type { Dialect, SchemaSnapshot, SnapColumn, SnapObject } from "../../../ipc/contract";
import { buildSchemaIndex } from "../schemaIndex";
import { complete } from "../complete";
import { scan } from "../scanner";
import type { SchemaIndex, Tier1Item } from "../types";

const ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb", "oracle"] as const satisfies readonly Dialect[];
type Engine = (typeof ENGINES)[number];
const DEFAULT_SCHEMA: Record<Engine, string> = { tsql: "dbo", pg: "public", mySql: "shop", sqlite: "main", duckDb: "main", oracle: "APP" };
const N = (d: Engine, name: string) => (d === "oracle" ? name.toUpperCase() : name);
const col = (name: string, ordinal: number): SnapColumn => ({ name, type: "int", nullable: true, identity: false, computed: false, pk: ordinal === 1, ordinal });

function indexFor(d: Engine): SchemaIndex {
  const s = DEFAULT_SCHEMA[d];
  const objects: SnapObject[] = [
    // Columns named like statement verbs of some dialect: COMMENT (pg, DuckDB, Oracle), OPEN / CLOSE (pg, MySQL, T-SQL, Oracle).
    { id: 1, schema: s, name: N(d, "posts"), kind: "table", columns: ["id", "comment", "open", "title"].map((c, k) => col(N(d, c), k + 1)) },
    { id: 2, schema: s, name: N(d, "orders"), kind: "table", columns: ["order_id", "customer_id", "total", "status"].map((c, k) => col(N(d, c), k + 1)) },
  ];
  const snap: SchemaSnapshot = { profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", objects, fks: [] };
  return buildSchemaIndex(snap, d, d === "mySql" ? ["shop"] : ["app"]);
}
const IDX = Object.fromEntries(ENGINES.map((d) => [d, indexFor(d)])) as Record<Engine, SchemaIndex>;

function run(sql: string, d: Engine): Tier1Item[] {
  const i = sql.indexOf("|");
  return complete({ text: sql.slice(0, i) + sql.slice(i + 1), offset: i, dialect: d, schema: IDX[d] }).items;
}
const has = (items: Tier1Item[], label: string) => items.some((i) => i.label === label);
const labels = (items: Tier1Item[]) => items.map((i) => i.label).slice(0, 30).join(", ");

describe("H6: a name spelled like a statement verb never splits the statement", () => {
  for (const d of ENGINES) {
    const p = (c: string) => `p.${N(d, c)}`;
    it(`${d}: a qualified verb-named column keeps the WHERE`, () => {
      const items = run("SELECT * FROM posts p WHERE p.comment LIKE 'x' AND p.open = 1 AND |", d);
      expect(has(items, p("title")), labels(items)).toBe(true);
    });
    it(`${d}: a verb-named column in the select list keeps the FROM after it`, () => {
      const items = run("SELECT p.comment, | FROM posts p", d);
      expect(has(items, p("title")), labels(items)).toBe(true);
      const bare = run("SELECT comment, | FROM posts", d);
      expect(has(bare, N(d, "title")), labels(bare)).toBe(true);
    });
    it(`${d}: a verb-named column after SET or an operator is a column`, () => {
      const set = run("UPDATE posts SET comment = 'x', | WHERE id = 1", d);
      expect(has(set, N(d, "title")), labels(set)).toBe(true);
      const cmp = run("SELECT * FROM posts p WHERE p.id = open AND |", d);
      expect(has(cmp, p("title")), labels(cmp)).toBe(true);
    });
  }
  it("mySql: ORDER BY … DESC, | stays in the ORDER BY", () => {
    const items = run("SELECT * FROM orders o ORDER BY o.total DESC, |", "mySql");
    expect(has(items, "o.status"), labels(items)).toBe(true);
  });
  for (const [d, fn] of [["mySql", "REPLACE"], ["mySql", "IF"], ["mySql", "TRUNCATE"], ["mySql", "INSERT"], ["mySql", "REPEAT"], ["sqlite", "REPLACE"]] as const) {
    it(`${d}: ${fn}( is a function call, its arguments see the FROM`, () => {
      const items = run(`SELECT ${fn}(|, 1) FROM orders`, d);
      expect(has(items, "total"), labels(items)).toBe(true);
    });
  }
  it("pg: CALL refresh( is the routine's argument list, not a REFRESH statement", () => {
    const sql = "CALL refresh(";
    expect(scan(sql, sql.length, "pg").clause).toBe("execArgs");
  });
  it("still: a statement after an unfinished qualified name on the next line starts there", () => {
    const sql = "SELECT a.\nUPDATE ";
    expect(scan(sql, sql.length, "tsql").clause).toBe("update");
  });
  it("still: T-SQL SET NOCOUNT ON then SELECT on the next line are two statements", () => {
    const sql = "CREATE PROCEDURE p AS\nBEGIN\n  SET NOCOUNT ON\n  SELECT * FROM posts WHERE ";
    expect(scan(sql, sql.length, "tsql").clause).toBe("where");
  });
  it("still: MySQL IF ( … ) THEN inside a stored program starts a statement", () => {
    const sql = "CREATE PROCEDURE p() BEGIN IF (1 = 1) THEN UPDATE ";
    expect(scan(sql, sql.length, "mySql").clause).toBe("update");
  });
});

describe("H7: columns the statement itself names are inserted as written there", () => {
  const ins = (items: Tier1Item[], label: string) => { const i = items.find((x) => x.label === label); return i?.insertText ?? i?.label; };
  it("oracle: a CTE's unquoted columns are not re-quoted (ORA-00904)", () => {
    const items = run("WITH x AS (SELECT id, title FROM posts) SELECT | FROM x", "oracle");
    expect(ins(items, "id"), labels(items)).toBe("id");
    expect(ins(items, "title")).toBe("title");
  });
  it("pg: a derived table's mixed-case unquoted column is inserted bare (it folds like the column)", () => {
    const items = run("SELECT | FROM (SELECT CustomerId FROM orders) d", "pg");
    expect(ins(items, "d.CustomerId"), labels(items)).toBe("d.CustomerId");
  });
  it("pg / oracle: a column written quoted stays quoted, exactly", () => {
    const pg = run('SELECT | FROM (SELECT 1 AS "MixedCase") d', "pg");
    expect(ins(pg, "d.MixedCase"), labels(pg)).toBe('d."MixedCase"');
    const ora = run('WITH x AS (SELECT 1 AS "lower" FROM dual) SELECT | FROM x', "oracle");
    expect(ins(ora, "lower"), labels(ora)).toBe('"lower"');
    const list = run('WITH x ("Total", n) AS (SELECT 1, 2 FROM dual) SELECT | FROM x', "oracle");
    expect(ins(list, "Total")).toBe('"Total"');
    expect(ins(list, "n")).toBe("n");
  });
  it("pg: a reserved word used as a select-list label is quoted", () => {
    const items = run("WITH x AS (SELECT 1 AS order) SELECT | FROM x", "pg");
    expect(ins(items, "order"), labels(items)).toBe('"order"');
  });
  it("still: catalog columns follow the catalog rules on every engine", () => {
    for (const d of ENGINES) {
      const items = run("SELECT | FROM posts", d);
      expect(ins(items, N(d, "title")), `${d}: ${labels(items)}`).toBe(N(d, "title"));
    }
  });
});

describe("M18: niladic built-ins are inserted without parentheses", () => {
  const ins = (items: Tier1Item[], label: string) => { const i = items.find((x) => x.label === label); return i?.insertText ?? i?.label; };
  for (const [d, fn] of [["oracle", "SYSDATE"], ["oracle", "SYSTIMESTAMP"], ["oracle", "CURRENT_DATE"], ["pg", "current_date"], ["pg", "current_timestamp"], ["duckDb", "current_date"]] as const) {
    it(`${d}: ${fn} has no ()`, () => expect(ins(run("SELECT | FROM orders", d), fn)).toBe(fn));
  }
  it("still: functions that take arguments keep their () snippet, MySQL's SYSDATE() too", () => {
    expect(ins(run("SELECT | FROM orders", "pg"), "now")).toBe("now($0)");
    expect(ins(run("SELECT | FROM orders", "mySql"), "SYSDATE")).toBe("SYSDATE($0)");
    expect(ins(run("SELECT | FROM orders", "oracle"), "TO_CHAR")).toBe("TO_CHAR($0)");
  });
});

describe("H6 still: a select list's star is no operator", () => {
  it("SELECT * then a statement on the next line are two statements", () => {
    const sql = "SELECT *\nUPDATE ";
    expect(scan(sql, sql.length, "tsql").clause).toBe("update");
  });
  it("a multiplication's right side is an operand", () => {
    const items = run("SELECT p.id * open, | FROM posts p", "pg");
    expect(has(items, "p.title"), labels(items)).toBe(true);
  });
});

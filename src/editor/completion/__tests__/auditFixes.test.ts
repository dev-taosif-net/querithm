// Tier-1 fixes from the second review of the completion audit (2026-11-25): regressions the first round of fixes
// introduced, and the gaps it left. Every case failed before its fix; the "still" cases pin what the fixes keep.
import { describe, expect, it } from "vitest";
import type { Dialect, SchemaSnapshot, SnapColumn, SnapObject } from "../../../ipc/contract";
import { buildSchemaIndex } from "../schemaIndex";
import { complete } from "../complete";
import { scan } from "../scanner";
import { statementBoundsAt } from "../sqlText";
import type { SchemaIndex, Tier1Item } from "../types";

const ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb", "oracle"] as const satisfies readonly Dialect[];
type Engine = (typeof ENGINES)[number];
const DEFAULT_SCHEMA: Record<Engine, string> = { tsql: "dbo", pg: "public", mySql: "shop", sqlite: "main", duckDb: "main", oracle: "APP" };
const N = (d: Engine, name: string) => (d === "oracle" ? name.toUpperCase() : name);
const col = (name: string, ordinal: number): SnapColumn => ({ name, type: "int", nullable: true, identity: false, computed: false, pk: ordinal === 1, ordinal });

function indexFor(d: Engine): SchemaIndex {
  const s = DEFAULT_SCHEMA[d];
  const objects: SnapObject[] = [
    { id: 1, schema: s, name: N(d, "posts"), kind: "table", columns: ["id", "comment", "open", "title"].map((c, k) => col(N(d, c), k + 1)) },
    { id: 2, schema: s, name: N(d, "orders"), kind: "table", columns: ["order_id", "customer_id", "total", "status"].map((c, k) => col(N(d, c), k + 1)) },
    { id: 3, schema: s, name: N(d, "customers"), kind: "table", columns: ["customer_id", "email", "region"].map((c, k) => col(N(d, c), k + 1)) },
    { id: 4, schema: s, name: N(d, "staging"), kind: "table", columns: ["sid", "payload"].map((c, k) => col(N(d, c), k + 1)) },
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
const labels = (items: Tier1Item[]) => items.map((i) => i.label).slice(0, 40).join(", ");
const ins = (items: Tier1Item[], label: string) => { const i = items.find((x) => x.label === label); return i?.insertText ?? i?.label; };

describe("T1: a statement verb on the next line ends an unfinished clause", () => {
  for (const d of ENGINES) {
    const status = N(d, "status"), title = N(d, "title");
    for (const [what, sql] of [
      ["WHERE", "SELECT * FROM orders WHERE |\nUPDATE posts SET title = 'x' WHERE id = 1"],
      ["AND", "SELECT * FROM orders WHERE total > 1 AND |\nDELETE FROM posts WHERE id = 1"],
      ["ORDER BY", "SELECT * FROM orders ORDER BY |\nINSERT INTO posts (id) VALUES (1)"],
      ["an operator", "SELECT * FROM orders WHERE total > |\nDELETE FROM posts WHERE id = 1"],
      ["SELECT", "SELECT |\nSELECT * FROM posts"],
    ] as const) {
      it(`${d}: after ${what}, the next line's statement is not in scope`, () => {
        const items = run(sql, d);
        expect(has(items, title) || has(items, `posts.${title}`) || has(items, `${N(d, "posts")}.${title}`), labels(items)).toBe(false);
        if (what !== "SELECT") expect(has(items, status), labels(items)).toBe(true);
      });
    }
    it(`${d}: still, verb-named columns on their own lines stay names`, () => {
      const items = run("SELECT *\nFROM posts p\nWHERE\n  open = 1 AND\n  comment = 'x' AND\n  |", d);
      expect(has(items, `p.${title}`), labels(items)).toBe(true);
      const list = run("SELECT\n  comment,\n  |\nFROM posts", d);
      expect(has(list, title), labels(list)).toBe(true);
      const first = run("SELECT\n  comment\nFROM posts\nWHERE |", d);
      expect(has(first, title), labels(first)).toBe(true);
    });
  }
  it("tsql: a label's statement keeps its UPDATE (the `:` is no operand slot)", () => {
    const items = run("retry:\nUPDATE orders SET status = 1 WHERE |", "tsql");
    expect(has(items, "total"), labels(items)).toBe(true);
    const sql = "retry:\nUPDATE orders SET status = 1 WHERE ";
    expect(scan(sql, sql.length, "tsql").clause).toBe("where");
  });
  it("still: UNION DISTINCT then SELECT on the next line is one statement", () => {
    for (const d of ["pg", "mySql", "duckDb"] as const) {
      const text = "SELECT id FROM posts UNION DISTINCT\nSELECT order_id FROM orders";
      expect(statementBoundsAt(text, text.length - 3, d), d).toEqual({ start: 0, end: text.length });
    }
  });
  // Oracle's run-current-statement follows SQL*Plus (`;` and `/` only, oracleStatements.ts), not this splitter.
  for (const d of ["tsql", "pg", "mySql", "sqlite", "duckDb"] as const) {
    it(`${d}: run current statement stops before the next line's statement`, () => {
      const text = "SELECT * FROM orders WHERE \nUPDATE posts SET title = 'x' WHERE id = 1";
      const at = "SELECT * FROM orders WHERE ".length;
      expect(statementBoundsAt(text, at - 1, d)).toEqual({ start: 0, end: "SELECT * FROM orders WHERE".length });
      expect(statementBoundsAt(text, text.length - 1, d)).toEqual({ start: at + 1, end: text.length });
    });
  }
});

describe("T3: a derived table hides the FROM it sits in, not the queries around that", () => {
  for (const d of ENGINES) {
    const email = N(d, "email"), total = N(d, "total");
    it(`${d}: inside EXISTS (… FROM (derived) d), the outer query is still visible`, () => {
      const q = run("SELECT * FROM customers c WHERE EXISTS (SELECT 1 FROM (SELECT o.order_id FROM orders o WHERE o.customer_id = c.|) d)", d);
      expect(has(q, email), labels(q)).toBe(true);
      const bare = run("SELECT * FROM customers c WHERE EXISTS (SELECT 1 FROM (SELECT o.order_id FROM orders o WHERE o.customer_id = |) d)", d);
      expect(has(bare, `c.${email}`), labels(bare)).toBe(true);
      expect(has(bare, `o.${total}`), labels(bare)).toBe(true);
    });
    if (d !== "duckDb") {
      it(`${d}: still, the derived table's siblings in its own FROM are not visible`, () => {
        const q = run("SELECT * FROM customers c WHERE EXISTS (SELECT 1 FROM staging s JOIN (SELECT | FROM orders o) d ON 1 = 1)", d);
        expect(has(q, `o.${total}`) || has(q, total), labels(q)).toBe(true);
        expect(q.some((i) => i.label.startsWith("s.")), labels(q)).toBe(false);
        expect(has(q, `c.${email}`), labels(q)).toBe(true);
      });
    }
  }
  it("duckDb: a derived table in FROM is lateral without the keyword", () => {
    const q = run("SELECT * FROM orders o, (SELECT o.| ) x", "duckDb");
    expect(has(q, "total"), labels(q)).toBe(true);
    const j = run("SELECT * FROM customers c JOIN (SELECT | FROM orders o) d ON 1 = 1", "duckDb");
    expect(has(j, "c.email"), labels(j)).toBe(true);
  });
  it("still: a CTE body sees nothing of its statement, DuckDB too", () => {
    for (const d of ENGINES) {
      const q = run("WITH x AS (SELECT | FROM orders) SELECT * FROM customers c", d);
      expect(q.some((i) => i.label.startsWith("c.")), `${d}: ${labels(q)}`).toBe(false);
    }
  });
});

describe("T7: a CTE named as a qualifier before FROM keeps its columns' quoting", () => {
  it("pg: a quoted mixed-case CTE column stays quoted, a bare one bare", () => {
    const q = run('WITH x AS (SELECT "OrderId", total FROM orders) SELECT x.|', "pg");
    expect(ins(q, "OrderId"), labels(q)).toBe('"OrderId"');
    expect(ins(q, "total")).toBe("total");
  });
  it("oracle: a quoted lower-case CTE column stays quoted, a bare one bare", () => {
    const q = run('WITH x AS (SELECT "id", title FROM posts) SELECT x.|', "oracle");
    expect(ins(q, "id"), labels(q)).toBe('"id"');
    expect(ins(q, "title")).toBe("title");
  });
  it("oracle / pg: a CTE column list written quoted stays quoted", () => {
    const q = run('WITH x ("Total", n) AS (SELECT 1, 2 FROM dual) SELECT x.|', "oracle");
    expect(ins(q, "Total"), labels(q)).toBe('"Total"');
    const p = run('WITH x ("MixedCol") AS (SELECT 1) SELECT x.|', "pg");
    expect(ins(p, "MixedCol"), labels(p)).toBe('"MixedCol"');
  });
  it("still: the same with the FROM typed after the cursor, every engine's bare columns stay bare", () => {
    for (const d of ENGINES) {
      const q = run("WITH x AS (SELECT total FROM orders) SELECT x.| FROM x", d);
      expect(ins(q, "total"), `${d}: ${labels(q)}`).toBe("total");
    }
  });
});

describe("T15: the SET list's target is the statement's own table", () => {
  it("pg: an UPDATE inside a CTE body is not the target of the INSERT's ON CONFLICT … DO UPDATE SET", () => {
    const q = run("WITH u AS (UPDATE customers SET email = 'x' RETURNING customer_id) INSERT INTO orders (order_id) SELECT customer_id FROM u ON CONFLICT (order_id) DO UPDATE SET |", "pg");
    expect(has(q, "status"), labels(q)).toBe(true);
    expect(q.some((i) => i.label.includes(".")), labels(q)).toBe(false);
  });
  for (const d of ["pg", "sqlite", "duckDb"] as const) {
    it(`${d}: INSERT … SELECT without a column list, then ON CONFLICT … DO UPDATE SET, offers the target's columns, bare`, () => {
      const q = run("INSERT INTO orders SELECT * FROM staging ON CONFLICT (order_id) DO UPDATE SET |", d);
      expect(has(q, "status"), labels(q)).toBe(true);
      expect(has(q, "payload") || has(q, "staging.payload"), labels(q)).toBe(false);
    });
  }
  it("still: T-SQL UPDATE alias SET … FROM table alias writes that table", () => {
    const q = run("UPDATE o SET | FROM orders o JOIN customers c ON c.customer_id = o.customer_id", "tsql");
    expect(has(q, "status"), labels(q)).toBe(true);
    expect(has(q, "c.email") || has(q, "email"), labels(q)).toBe(false);
  });
});

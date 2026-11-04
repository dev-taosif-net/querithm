// Completion matrix (§9): positive cases (what must be offered) and negative cases (what must never be offered), run
// against every active engine with the same schema, plus engine-specific quoting and a sweep over every cursor offset
// of large scripts. Table suggestions are the feature's core: any table slot that offers no table is a failure.
import { describe, expect, it } from "vitest";
import type { Dialect, SchemaSnapshot, SnapColumn, SnapObject } from "../../../ipc/contract";
import { buildSchemaIndex } from "../schemaIndex";
import { complete } from "../complete";
import type { SchemaIndex, Tier1Item } from "../types";

const ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb"] as const satisfies readonly Dialect[];
type Engine = (typeof ENGINES)[number];
const DEFAULT_SCHEMA: Record<Engine, string> = { tsql: "dbo", pg: "public", mySql: "shop", sqlite: "main", duckDb: "main" };

const col = (name: string, ordinal: number, pk = false): SnapColumn => ({
  name, type: "int", nullable: !pk, identity: false, computed: false, pk, ordinal,
});

function indexFor(d: Engine): SchemaIndex {
  const s = DEFAULT_SCHEMA[d];
  const objects: SnapObject[] = [
    { id: 1, schema: s, name: "orders", kind: "table", columns: [col("order_id", 1, true), col("customer_id", 2), col("total", 3), col("status", 4)] },
    { id: 2, schema: s, name: "customers", kind: "table", columns: [col("customer_id", 1, true), col("full_name", 2), col("email", 3)] },
    { id: 3, schema: s, name: "order_items", kind: "table", columns: [col("order_id", 1), col("product_id", 2), col("qty", 3)] },
    { id: 4, schema: s, name: "v_sales", kind: "view", columns: [col("day", 1), col("amount", 2)] },
    { id: 5, schema: "sales", name: "products", kind: "table", columns: [col("product_id", 1, true), col("name", 2), col("price", 3)] },
  ];
  const snap: SchemaSnapshot = {
    profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", objects,
    fks: [
      { name: "fk_orders_customers", parent: 1, referenced: 2, columns: [{ parent: "customer_id", referenced: "customer_id" }] },
      { name: "fk_items_orders", parent: 3, referenced: 1, columns: [{ parent: "order_id", referenced: "order_id" }] },
    ],
  };
  return buildSchemaIndex(snap, d, d === "mySql" ? ["shop", "sales"] : ["app"]);
}
const IDX: Record<Engine, SchemaIndex> = Object.fromEntries(ENGINES.map((d) => [d, indexFor(d)])) as Record<Engine, SchemaIndex>;

function run(sql: string, d: Engine, schema: SchemaIndex | null = IDX[d]): Tier1Item[] {
  const i = sql.indexOf("|");
  if (i < 0) throw new Error(`no cursor in ${sql}`);
  return complete({ text: sql.slice(0, i) + sql.slice(i + 1), offset: i, dialect: d, schema }).items;
}
const ofKind = (items: Tier1Item[], ...k: string[]) => items.filter((i) => k.includes(i.kind)).map((i) => i.label);
const tablesOf = (items: Tier1Item[]) => ofKind(items, "table", "view");
const columnsOf = (items: Tier1Item[]) => ofKind(items, "column");

// A realistic large statement, used before, after and around the cursor.
const BIG = `SELECT o.order_id, o.customer_id, c.full_name,
       CASE WHEN o.status = 'open; still' THEN 1 ELSE 0 END AS is_open, -- a comment with FROM inside
       (SELECT COUNT(*) FROM order_items i WHERE i.order_id = o.order_id) AS n_items
FROM orders o
JOIN customers c ON c.customer_id = o.customer_id
LEFT JOIN order_items oi ON oi.order_id = o.order_id AND oi.qty > 0
WHERE o.total > 10 /* block ; comment */ AND EXISTS (SELECT 1 FROM order_items x WHERE x.order_id = o.order_id)
  AND o.status IN ('a', 'b') AND c.email LIKE '%@x.y'
GROUP BY o.order_id, o.customer_id, c.full_name, o.status
HAVING COUNT(*) > 0
ORDER BY o.order_id DESC`;

// ---------------------------------------------------------------------------------------------------------------
// Positive: a table slot always offers tables.
// ---------------------------------------------------------------------------------------------------------------
const TABLE_SLOTS: string[] = [
  "SELECT * FROM |",
  "SELECT * FROM ord|",
  "select * from |",
  "SELECT * FROM\n    |",
  "SELECT *\nFROM |\nWHERE 1 = 1",
  "SELECT * FROM | WHERE total > 1",
  "SELECT * FROM |;",
  "SELECT DISTINCT status FROM |",
  "SELECT o.* FROM orders o JOIN |",
  "SELECT o.* FROM orders o JOIN cus|",
  "SELECT o.* FROM orders o INNER JOIN |",
  "SELECT o.* FROM orders o LEFT JOIN |",
  "SELECT o.* FROM orders o LEFT OUTER JOIN |",
  "SELECT o.* FROM orders o RIGHT JOIN |",
  "SELECT o.* FROM orders o CROSS JOIN |",
  "SELECT o.* FROM orders o, |",
  "SELECT o.* FROM orders o, customers c, |",
  "SELECT o.* FROM orders o\nJOIN customers c ON c.customer_id = o.customer_id\nJOIN |",
  "SELECT o.* FROM orders o JOIN customers c ON c.customer_id = o.customer_id AND c.email IS NOT NULL JOIN |",
  "SELECT o.* FROM orders o JOIN customers c ON (c.customer_id = o.customer_id) JOIN |",
  "SELECT o.* FROM orders o JOIN customers c ON c.customer_id = o.customer_id AND c.email = 'a;b' JOIN |",
  "SELECT o.* FROM orders o JOIN customers c ON c.customer_id = o.customer_id JOIN | ON 1 = 1",
  "SELECT o.* FROM orders o JOIN | c ON c.customer_id = o.customer_id",
  "SELECT o.* FROM orders AS o JOIN |",
  "SELECT * FROM orders o WHERE o.customer_id IN (SELECT customer_id FROM |)",
  "SELECT * FROM orders o WHERE o.customer_id IN (SELECT customer_id FROM |",
  "SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM |)",
  "SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM |",
  "SELECT * FROM orders o WHERE NOT EXISTS (SELECT 1 FROM | WHERE 1 = 1)",
  "SELECT * FROM orders o WHERE EXISTS(SELECT 1 FROM |)",
  "SELECT * FROM orders o WHERE o.total > (SELECT AVG(total) FROM |)",
  "SELECT (SELECT COUNT(*) FROM |) AS n FROM orders",
  "SELECT d.* FROM (SELECT * FROM |) d",
  "SELECT d.* FROM (SELECT * FROM orders) d JOIN |",
  "WITH x AS (SELECT * FROM |) SELECT * FROM x",
  "WITH x AS (SELECT * FROM orders) SELECT * FROM x JOIN |",
  "WITH x AS (SELECT * FROM orders), y AS (SELECT * FROM |) SELECT 1",
  "SELECT * FROM orders UNION ALL SELECT * FROM |",
  "SELECT * FROM orders\nUNION\nSELECT * FROM |",
  "SELECT * FROM orders EXCEPT SELECT * FROM |",
  "SELECT CASE WHEN 1 = 1 THEN 'a' END AS x FROM |",
  "SELECT CAST(total AS int) FROM |",
  "SELECT COUNT(*) FROM |",
  "SELECT COALESCE(status, 'x') FROM |",
  "SELECT COUNT(*) OVER (PARTITION BY status ORDER BY total) FROM |",
  "SELECT * FROM orders WHERE status = 'x' AND total IN (1, 2, 3) AND email LIKE '%a' OR 1 = 1;\nSELECT * FROM |",
  "UPDATE |",
  "UPDATE ord|",
  "INSERT INTO |",
  "INSERT INTO ord|",
  "DELETE FROM |",
  "SELECT * FROM orders;\nSELECT * FROM |",
  "SELECT * FROM orders;\n\nSELECT * FROM customers;\n\nSELECT * FROM |",
  "-- header comment\n/* block */\nSELECT * FROM |",
  "SELECT 'FROM x; y' AS s;\nSELECT * FROM |",
  BIG + ";\nSELECT * FROM |",
  BIG + ";\n\nSELECT * FROM |\nWHERE 1 = 1",
  BIG + ";\nSELECT o.* FROM orders o JOIN |",
  BIG + ";\nSELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM |",
  "SELECT * FROM |;\n" + BIG,
  "SELECT * FROM |\n\n" + BIG,
  "SELECT o.* FROM orders o JOIN |;\n" + BIG,
  BIG + ";\nSELECT * FROM |;\n" + BIG,
  // the cursor inside the big statement itself
  BIG.replace("JOIN customers c", "JOIN |"),
  BIG.replace("LEFT JOIN order_items oi", "LEFT JOIN |"),
  BIG.replace("FROM order_items x", "FROM |"),
  BIG.replace("FROM order_items i", "FROM |"),
  BIG.replace("FROM orders o", "FROM |"),
];

// Positive: an expression slot offers the in-scope columns.
const COLUMN_SLOTS: Array<[string, string]> = [
  ["SELECT | FROM orders", "order_id"],
  ["SELECT tot| FROM orders", "total"],
  ["SELECT o.| FROM orders o", "order_id"],
  ["SELECT o.st| FROM orders o", "status"],
  ["SELECT c.| FROM orders o JOIN customers c ON c.customer_id = o.customer_id", "full_name"],
  ["SELECT * FROM orders o WHERE o.|", "total"],
  ["SELECT * FROM orders o WHERE |", "status"],
  ["SELECT * FROM orders o WHERE o.total > 1 AND o.|", "status"],
  ["SELECT * FROM orders o JOIN customers c ON c.|", "customer_id"],
  ["SELECT * FROM orders o JOIN customers c ON c.customer_id = o.|", "customer_id"],
  ["SELECT * FROM orders o ORDER BY o.|", "order_id"],
  ["SELECT * FROM orders o ORDER BY |", "order_id"],
  ["SELECT status, COUNT(*) FROM orders GROUP BY |", "status"],
  ["SELECT status FROM orders GROUP BY status HAVING MAX(|) > 1", "total"],
  ["UPDATE orders SET |", "status"],
  ["UPDATE orders SET status = 'x' WHERE |", "order_id"],
  ["DELETE FROM orders WHERE |", "order_id"],
  ["INSERT INTO orders (|", "status"],
  ["INSERT INTO orders (order_id, |", "status"],
  ["SELECT d.| FROM (SELECT order_id, total FROM orders) d", "total"],
  ["WITH x AS (SELECT order_id, total FROM orders) SELECT x.| FROM x", "total"],
  ["SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM customers c WHERE c.|)", "email"],
  ["SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM customers c WHERE c.customer_id = o.|)", "status"],
  ["SELECT p.| FROM sales.products p", "price"],
  [BIG + ";\nSELECT o.| FROM orders o", "order_id"],
  ["SELECT o.| FROM orders o;\n" + BIG, "order_id"],
  [BIG.replace("WHERE o.total > 10", "WHERE o.|"), "total"],
  [BIG.replace("c.full_name,\n", "c.|,\n"), "email"],
  [BIG.replace("ORDER BY o.order_id DESC", "ORDER BY c.|"), "full_name"],
];

// ---------------------------------------------------------------------------------------------------------------
// Negative: where nothing (or no tables / no columns) may be offered.
// ---------------------------------------------------------------------------------------------------------------
const NOTHING: string[] = [
  "SELECT 'FROM |' FROM orders",
  "SELECT * FROM orders WHERE status = 'abc|'",
  "SELECT * FROM orders -- FROM |",
  "SELECT * FROM orders /* JOIN | */",
  "/* SELECT * FROM |",
  "SELECT * FROM orders AS |",
  "SELECT total AS |",
  BIG + ";\nSELECT 'x|' FROM orders",
];
// Only the tables in scope may be offered (as qualifiers) in an expression; a qualifier offers no table at all.
const NO_TABLES: string[] = [
  "SELECT o.| FROM orders o",
  "SELECT * FROM orders o WHERE o.|",
  "SELECT * FROM orders o JOIN customers c ON c.|",
  "UPDATE orders SET |",
  "INSERT INTO orders (|",
  "SELECT EXTRACT(YEAR FROM |) FROM orders",
  "SELECT * FROM sales.|", // products only: the default schema's tables never answer another schema
];
const NO_COLUMNS: string[] = [
  "SELECT * FROM |",
  "SELECT * FROM orders o JOIN |",
  "UPDATE |",
  "INSERT INTO |",
  "DELETE FROM |",
  "SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM |",
];


describe.each(ENGINES)("completion matrix: %s", (d) => {
  it.each(TABLE_SLOTS.map((s) => [s]))("table slot offers tables: %s", (sql) => {
    const t = tablesOf(run(sql, d));
    expect(t, sql).toEqual(expect.arrayContaining(sql.match(/\b(ord|cus)\|/) ? [] : ["orders", "customers"]));
    expect(t.length, sql).toBeGreaterThan(0);
  });
  it.each(COLUMN_SLOTS)("column slot offers columns: %s → %s", (sql, want) => {
    expect(columnsOf(run(sql, d)).map((l) => l.split(".").pop()), sql).toContain(want);
  });
  it.each(NOTHING.map((s) => [s]))("offers nothing: %s", (sql) => {
    expect(run(sql, d), sql).toEqual([]);
  });
  it.each(NO_TABLES.map((s) => [s]))("offers no table out of scope: %s", (sql) => {
    const t = tablesOf(run(sql, d));
    const outOfScope = ["customers", "order_items", "v_sales", ...(/\borders\b/.test(sql) ? [] : ["orders"])];
    expect(t.filter((l) => outOfScope.includes(l)), sql).toEqual([]);
  });
  it.each(NO_COLUMNS.map((s) => [s]))("offers no columns in a table slot: %s", (sql) => {
    expect(columnsOf(run(sql, d)), sql).toEqual([]);
  });

  it("a prefix narrows the tables to the matching ones", () => {
    const t = tablesOf(run("SELECT * FROM cust|", d));
    expect(t).toContain("customers");
    expect(t).not.toContain("orders");
    expect(t).not.toContain("v_sales");
  });
  it("views are offered in FROM, and the other schema qualified", () => {
    const t = tablesOf(run("SELECT * FROM |", d));
    expect(t).toContain("v_sales");
    expect(t).toContain("sales.products");
  });
  it("a schema qualifier offers that schema's tables, unqualified", () => {
    expect(tablesOf(run("SELECT * FROM sales.|", d))).toEqual(["products"]);
    expect(tablesOf(run("SELECT * FROM sales.pro|", d))).toEqual(["products"]);
    expect(tablesOf(run(`SELECT * FROM ${DEFAULT_SCHEMA[d]}.|`, d))).toEqual(expect.arrayContaining(["orders", "customers"]));
  });
  it("an unknown alias qualifier offers no columns of another table", () => {
    expect(columnsOf(run("SELECT zz.| FROM orders o", d))).toEqual([]);
  });
  it("a prefix that matches nothing offers no tables", () => {
    expect(tablesOf(run("SELECT * FROM qqqzzz|", d))).toEqual([]);
  });
  it("without a schema (not connected / not loaded yet): keywords only, never throws", () => {
    const items = run("SELECT * FROM |", d, null);
    expect(tablesOf(items)).toEqual([]);
    expect(() => run(BIG + "|", d, null)).not.toThrow();
  });
  it("a table reference already complete is not followed by tables (the alias slot)", () => {
    expect(tablesOf(run("SELECT * FROM orders |", d))).toEqual([]);
  });

  // Sweep: every cursor offset of a large script answers without throwing, and every position right after a table
  // keyword (FROM / JOIN / UPDATE / INTO) offers tables, whatever comes before or after.
  it("sweep: every offset of a large script, and every table-keyword position offers tables", () => {
    const script = [BIG, "UPDATE orders SET status = 'x' WHERE order_id IN (SELECT order_id FROM order_items)",
      "INSERT INTO order_items (order_id, product_id, qty) SELECT order_id, 1, 1 FROM orders", BIG].join(";\n\n");
    for (let off = 0; off <= script.length; off++) {
      expect(() => complete({ text: script, offset: off, dialect: d, schema: IDX[d] }), `offset ${off}`).not.toThrow();
    }
    const misses: string[] = [];
    const kw = /\b(FROM|JOIN|UPDATE|INTO) /g;
    let m: RegExpExecArray | null;
    while ((m = kw.exec(script))) {
      const off = m.index + m[0].length;
      // Inside a string or comment the keyword is text, not SQL.
      const before = script.slice(0, off);
      if (/--[^\n]*$/.test(before) || (before.split("'").length - 1) % 2 === 1 || before.lastIndexOf("/*") > before.lastIndexOf("*/")) continue;
      // Cut the reference that follows, so the cursor is in an empty slot: `FROM | WHERE …`.
      const rest = script.slice(off).replace(/^[\w.]+(\s+(?!ON\b|WHERE\b|SET\b|JOIN\b|LEFT\b|GROUP\b|ORDER\b|SELECT\b)[a-z]+\b)?/, "");
      const items = complete({ text: script.slice(0, off) + " " + rest, offset: off, dialect: d, schema: IDX[d] }).items;
      if (!tablesOf(items).includes("orders")) misses.push(`${m[1]} @${off}: …${script.slice(Math.max(0, off - 40), off)}|`);
    }
    expect(misses).toEqual([]);
  });
  it("sweep: every prefix of a typed table name keeps offering it", () => {
    for (const lead of ["SELECT * FROM ", BIG + ";\nSELECT * FROM ", "SELECT * FROM orders o JOIN "]) {
      for (let n = 1; n <= "customers".length; n++) {
        const t = tablesOf(run(`${lead}${"customers".slice(0, n)}|`, d));
        expect(t, `${lead.slice(-30)}${"customers".slice(0, n)}`).toContain("customers");
      }
    }
  });
});

// ---------------------------------------------------------------------------------------------------------------
// Engine-specific identifier quoting: an opened quote still offers the tables, inserted without doubling it.
// ---------------------------------------------------------------------------------------------------------------
describe("completion matrix: quoted identifiers", () => {
  const OPEN: Record<Engine, string[]> = { tsql: ["["], pg: ['"'], mySql: ["`"], sqlite: ['"', "[", "`"], duckDb: ['"'] };
  for (const d of ENGINES) {
    for (const q of OPEN[d]) {
      it(`${d}: FROM ${q}| and FROM ${q}cus| offer tables`, () => {
        expect(tablesOf(run(`SELECT * FROM ${q}|`, d))).toContain("orders");
        const items = run(`SELECT * FROM ${q}cus|`, d);
        expect(tablesOf(items)).toContain("customers");
        const c = items.find((i) => i.label === "customers")!;
        expect((c.insertText ?? c.label).startsWith(q)).toBe(false);
      });
      it(`${d}: JOIN ${q}| offers tables in a large script`, () => {
        expect(tablesOf(run(`${BIG};\nSELECT * FROM orders o JOIN ${q}|`, d))).toContain("customers");
      });
    }
  }
});

// ---------------------------------------------------------------------------------------------------------------
// Engine-specific syntax around a table slot (positive) and the look-alikes that are not one (negative).
// ---------------------------------------------------------------------------------------------------------------
type Want = "tables" | "columns" | "noTables";
const SPECIFIC: Record<Engine, Array<[string, Want]>> = {
  tsql: [
    ["SELECT TOP 10 * FROM |", "tables"],
    ["SELECT TOP (10) PERCENT * FROM |", "tables"],
    ["SELECT * FROM orders WITH (NOLOCK) JOIN |", "tables"],
    ["SELECT * FROM orders o WITH (NOLOCK)\nJOIN |", "tables"],
    ["SELECT * FROM orders o CROSS APPLY |", "tables"],
    ["SELECT * FROM orders o OUTER APPLY |", "tables"],
    ["SELECT * FROM orders\nGO\nSELECT * FROM |", "tables"],
    ["DECLARE @x int = 1\nSELECT * FROM |", "tables"],
    ["DECLARE @x int = (SELECT MAX(total) FROM |)", "tables"],
    ["IF EXISTS (SELECT 1 FROM |)\n  PRINT 'x'", "tables"],
    ["IF 1 = 1\n  SELECT * FROM orders\nELSE\n  SELECT * FROM |", "tables"],
    ["IF 1 = 1 BEGIN\n  SELECT * FROM |\nEND", "tables"],
    ["BEGIN TRY\n  DELETE FROM |\nEND TRY\nBEGIN CATCH\nEND CATCH", "tables"],
    ["CREATE PROCEDURE dbo.p AS\nBEGIN\n  SET NOCOUNT ON;\n  SELECT * FROM |\nEND", "tables"],
    ["CREATE VIEW dbo.v AS SELECT * FROM |", "tables"],
    ["SELECT * INTO #t FROM |", "tables"],
    ["MERGE INTO orders AS t USING | AS s ON t.order_id = s.order_id", "tables"],
    ["MERGE orders AS t USING customers AS s ON t.customer_id = s.customer_id WHEN MATCHED THEN UPDATE SET |", "noTables"],
    ["MERGE orders AS t USING customers AS s ON t.customer_id = s.customer_id WHEN MATCHED THEN UPDATE SET t.|", "columns"],
    ["UPDATE o SET o.status = 'x' FROM orders o JOIN |", "tables"],
    ["UPDATE | SET status = 'x' WHERE order_id = 1", "tables"],
    ["DELETE o FROM orders o JOIN |", "tables"],
    ["SELECT CASE WHEN o.total > 1 THEN 'a' ELSE 'b' END AS k, o.| FROM orders o", "columns"],
    ["SELECT * FROM orders o WHERE CASE WHEN o.total > 1 THEN 1 ELSE 0 END = 1 AND o.|", "columns"],
    ["SELECT CASE WHEN o.total > 1 THEN CASE WHEN 1 = 1 THEN 1 ELSE 2 END ELSE 0 END, o.| FROM orders o", "columns"],
    ["SELECT CASE WHEN 1 = 1 THEN 1 ELSE 0 END AS a\nFROM orders o\nJOIN |", "tables"],
  ],
  pg: [
    ["SELECT * FROM orders LIMIT 10;\nSELECT * FROM |", "tables"],
    ["SELECT * FROM orders o, LATERAL (SELECT * FROM |) x", "tables"],
    ["SELECT * FROM orders o JOIN LATERAL (SELECT * FROM |) x ON true", "tables"],
    ["SELECT total::int FROM |", "tables"],
    ["SELECT * FROM ONLY |", "tables"],
    ["SELECT * FROM orders WHERE order_id = $1 AND customer_id IN (SELECT customer_id FROM |)", "tables"],
    ["INSERT INTO orders (order_id) VALUES (1) RETURNING *;\nSELECT * FROM |", "tables"],
    ["INSERT INTO orders (order_id) VALUES (1) ON CONFLICT (order_id) DO UPDATE SET |", "noTables"],
    ["UPDATE | SET status = 'x'", "tables"],
    ["DELETE FROM orders USING |", "tables"],
    ["WITH RECURSIVE r AS (SELECT 1 UNION ALL SELECT 1 FROM |) SELECT * FROM r", "tables"],
    ["DO $$ BEGIN PERFORM 1; END $$;\nSELECT * FROM |", "tables"],
    ["SELECT CASE WHEN o.total > 1 THEN 'a' ELSE 'b' END AS k, o.| FROM orders o", "columns"],
    ["SELECT ARRAY(SELECT order_id FROM |)", "tables"],
    ["SELECT * FROM orders o WHERE o.total > ANY (SELECT total FROM |)", "tables"],
  ],
  mySql: [
    ["SELECT * FROM orders LIMIT 10;\nSELECT * FROM |", "tables"],
    ["SELECT * FROM `orders` o JOIN |", "tables"],
    ["SELECT * FROM shop.|", "tables"],
    ["SELECT * FROM orders o STRAIGHT_JOIN |", "tables"],
    ["INSERT INTO orders (order_id) VALUES (1) ON DUPLICATE KEY UPDATE |", "noTables"],
    ["INSERT INTO orders (order_id) VALUES (1) ON DUPLICATE KEY UPDATE status = 1, |", "columns"],
    ["INSERT INTO orders (order_id) VALUES (1) ON DUPLICATE KEY UPDATE |", "columns"],
    ["REPLACE INTO |", "tables"],
    ["INSERT IGNORE INTO |", "tables"],
    ["UPDATE | SET status = 'x'", "tables"],
    ["DELETE o FROM orders o JOIN |", "tables"],
    ["SELECT CASE WHEN o.total > 1 THEN 'a' ELSE 'b' END AS k, o.| FROM orders o", "columns"],
    ["SELECT @x := 1;\nSELECT * FROM |", "tables"],
  ],
  sqlite: [
    ["INSERT OR REPLACE INTO |", "tables"],
    ["REPLACE INTO |", "tables"],
    ["SELECT * FROM orders LIMIT 10;\nSELECT * FROM |", "tables"],
    ["SELECT * FROM main.|", "tables"],
    ["UPDATE | SET status = 'x'", "tables"],
    ["UPDATE OR IGNORE |", "tables"],
    ["INSERT INTO orders (order_id) VALUES (1) ON CONFLICT (order_id) DO UPDATE SET |", "noTables"],
    ["SELECT * FROM orders o NATURAL JOIN |", "tables"],
    ["SELECT CASE WHEN o.total > 1 THEN 'a' ELSE 'b' END AS k, o.| FROM orders o", "columns"],
  ],
  duckDb: [
    ["FROM |", "tables"],
    ["FROM orders;\nFROM |", "tables"],
    ["SELECT * FROM orders o ASOF JOIN |", "tables"],
    ["SELECT * FROM orders o POSITIONAL JOIN |", "tables"],
    ["SELECT * FROM orders o ANTI JOIN |", "tables"],
    ["SELECT * FROM main.|", "tables"],
    ["UPDATE | SET status = 'x'", "tables"],
    ["INSERT OR REPLACE INTO |", "tables"],
    ["SELECT * FROM orders QUALIFY row_number() OVER () = 1;\nSELECT * FROM |", "tables"],
    ["SELECT CASE WHEN o.total > 1 THEN 'a' ELSE 'b' END AS k, o.| FROM orders o", "columns"],
  ],
};

describe.each(ENGINES)("completion matrix: %s-specific syntax", (d) => {
  it.each(SPECIFIC[d])("%s → %s", (sql, want) => {
    const items = run(sql, d);
    if (want === "tables") expect(tablesOf(items), sql).toEqual(expect.arrayContaining(sql.includes("main.|") || sql.includes("shop.|") ? ["orders"] : ["orders", "customers"]));
    else if (want === "columns") expect(columnsOf(items).length, sql).toBeGreaterThan(0);
    // noTables: only a table the statement itself names may be offered (as a qualifier).
    else expect(tablesOf(items).filter((l) => !sql.split(/[^\w]+/).includes(l)), sql).toEqual([]);
  });
});

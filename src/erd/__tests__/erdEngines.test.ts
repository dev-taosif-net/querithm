// Per-engine ER diagrams (Phase D): the diagram is built from the snapshot's tables and foreign keys, so each engine is
// checked on the snapshot its catalog sends — SQLite (schema "main", fk_<table>_<id> names, temporary tables in "temp"),
// MySQL / MariaDB (the schema is the database, backtick identifiers) and DuckDB (schemas of its own, exact types) — with
// the card label (schema.name) and the identifier quoting of the generated JOIN and the current-query scope.
import { describe, expect, it } from "vitest";
import type { Dialect, SchemaSnapshot } from "../../ipc/contract";
import { buildGraph, joinSql, layoutGraph, neighbourhood, schemaScope, statementTables } from "../erdLayout";
import { buildSvg } from "../erdExport";
import { buildSchemaIndex } from "../../editor/completion/schemaIndex";
import { duckDbSnapshot, mySqlSnapshot, sqliteSnapshot } from "./fixture";

const sorted = (s: Set<number>) => [...s].sort((a, b) => a - b);
const labels = (snap: SchemaSnapshot, ids: Set<number>) => buildGraph(snap, ids, false).nodes.map((n) => `${n.schema}.${n.name}`);
const COLORS = { bg: "#000", card: "#111", header: "#222", line: "#333", fg: "#fff", muted: "#999", pk: "#fa0", fk: "#0af", edge: "#888" };
const svgOf = (snap: SchemaSnapshot, ids: Set<number>) => {
  const g = buildGraph(snap, ids, false);
  return buildSvg(g, layoutGraph(g), COLORS);
};
const sqlOf = (snap: SchemaSnapshot, ids: number[], dialect: Dialect) => {
  const r = joinSql(snap, ids, dialect);
  if ("error" in r) throw new Error(r.error);
  return r.sql;
};

describe("SQLite", () => {
  const snap = sqliteSnapshot();

  it("builds nodes and FK edges from the catalog; views are not tables, temporary tables are in temp", () => {
    const g = buildGraph(snap, schemaScope(snap, null), false);
    expect(g.nodes.map((n) => `${n.schema}.${n.name}`)).toEqual(["main.customer", "main.order", "main.order_line", "main.product", "temp.scratch"]);
    expect(g.edges.map((e) => [e.name, e.from, e.to])).toEqual([["fk_order_0", 2, 1], ["fk_order_line_0", 3, 2], ["fk_order_line_1", 3, 4]]);
    expect(g.edges.find((e) => e.name === "fk_order_0")!.optional).toBe(true); // customer_id allows NULL
    expect(g.edges.find((e) => e.name === "fk_order_line_0")!.optional).toBe(false);
    const order = g.nodes.find((n) => n.name === "order")!;
    expect(order.columns.map((c) => [c.name, c.type, c.pk, c.fk])).toEqual([["id", "INTEGER", true, false], ["customer_id", "INTEGER", false, true], ["total", "REAL", false, false]]);
    // A column without a declared type (SQLite allows it) still gets a card row.
    expect(g.nodes.find((n) => n.name === "product")!.columns[1]).toMatchObject({ name: "label", type: "" });
  });

  it("schema main leaves the session's temporary tables out; temp has only them", () => {
    expect(labels(snap, schemaScope(snap, "main"))).toEqual(["main.customer", "main.order", "main.order_line", "main.product"]);
    expect(labels(snap, schemaScope(snap, "temp"))).toEqual(["temp.scratch"]);
    expect(sorted(neighbourhood(snap, [2], 1))).toEqual([1, 2, 3]);
  });

  it("the JOIN leaves main out and double-quotes the reserved word", () => {
    expect(sqlOf(snap, [1, 3], "sqlite")).toBe('SELECT c.*, ol.*\nFROM customer c\nJOIN "order" o ON o.customer_id = c.id\nJOIN order_line ol ON ol.order_id = o.id;');
  });

  it("query mode resolves the statement's tables, quoted or not", () => {
    const idx = buildSchemaIndex(snap, "sqlite", []);
    const text = 'SELECT * FROM "order" o JOIN main.customer c ON c.id = o.customer_id;\nSELECT * FROM product;';
    expect(statementTables(idx, text, 10, "sqlite").sort()).toEqual([1, 2]);
    expect(statementTables(idx, text, text.length - 3, "sqlite")).toEqual([4]);
  });

  it("cards and the SVG export are labelled main.<name>", () => {
    const svg = svgOf(snap, new Set([1, 2]));
    expect(svg).toContain(">main.order<");
    expect(svg).toContain(">main.customer<");
  });
});

describe("MySQL / MariaDB", () => {
  const snap = mySqlSnapshot();

  it("builds nodes and FK edges; the schema is the database name", () => {
    const g = buildGraph(snap, schemaScope(snap, "shop"), false);
    expect(g.nodes.map((n) => `${n.schema}.${n.name}`)).toEqual(["shop.m_child", "shop.m_parent", "shop.order"]);
    expect(g.edges.map((e) => [e.name, e.from, e.to, e.pairs])).toEqual([
      ["fk_child", 12, 11, [{ parent: "parent_id", referenced: "id" }]],
      ["order_ibfk_1", 13, 12, [{ parent: "key", referenced: "id" }]],
    ]);
    const order = g.nodes.find((n) => n.name === "order")!;
    expect(order.columns.map((c) => [c.name, c.type, c.fk])).toEqual([["id", "bigint unsigned", false], ["key", "int", true], ["flags", "bit(8)", false]]);
    expect(schemaScope(snap, "SHOP").size).toBe(3); // a database name matches as the schema filter does elsewhere
  });

  it("the JOIN qualifies with the database (no default schema) and backticks reserved words", () => {
    expect(sqlOf(snap, [13, 11], "mySql")).toBe("SELECT o.*, mp.*\nFROM shop.`order` o\nJOIN shop.m_child mc ON o.`key` = mc.id\nJOIN shop.m_parent mp ON mc.parent_id = mp.id;");
  });

  it("query mode resolves backticked and database-qualified names", () => {
    const idx = buildSchemaIndex(snap, "mySql", ["shop"]);
    const text = "SELECT * FROM `order` o JOIN shop.m_child mc ON mc.id = o.`key`;\nSELECT * FROM m_parent;";
    expect(statementTables(idx, text, 10, "mySql").sort()).toEqual([12, 13]);
    expect(statementTables(idx, text, text.length - 3, "mySql")).toEqual([11]);
  });

  it("cards and the SVG export are labelled <database>.<name>", () => {
    const svg = svgOf(snap, new Set([12, 13]));
    expect(svg).toContain(">shop.order<");
    expect(svg).toContain(">shop.m_child<");
  });
});

describe("DuckDB", () => {
  const snap = duckDbSnapshot();

  it("builds nodes and FK edges with the exact types DuckDB reports", () => {
    const g = buildGraph(snap, schemaScope(snap, null), false);
    expect(g.nodes.map((n) => `${n.schema}.${n.name}`)).toEqual(["crm.region", "main.customer", "main.order"]);
    expect(g.edges.map((e) => [e.name, e.from, e.to, e.optional])).toEqual([["order_customer_id_fkey", 22, 21, true]]);
    expect(g.nodes.find((n) => n.name === "customer")!.columns.map((c) => c.type)).toEqual(["INTEGER", "VARCHAR", "DECIMAL(10,2)"]);
    expect(g.nodes.find((n) => n.name === "order")!.columns.map((c) => c.type)).toEqual(["BIGINT", "INTEGER", "HUGEINT"]);
    expect(g.nodes.find((n) => n.name === "region")!.hidden).toBe(0);
  });

  it("schemas scope the diagram", () => {
    expect(labels(snap, schemaScope(snap, "crm"))).toEqual(["crm.region"]);
    expect(labels(snap, schemaScope(snap, "main"))).toEqual(["main.customer", "main.order"]);
  });

  it("the JOIN leaves main out and double-quotes the reserved word; unconnected schemas are refused, not cross-joined", () => {
    expect(sqlOf(snap, [22, 21], "duckDb")).toBe('SELECT o.*, c.*\nFROM "order" o\nJOIN customer c ON o.customer_id = c.id;');
    expect(joinSql(snap, [21, 23], "duckDb")).toEqual({ error: expect.stringContaining("main.customer and crm.region are not connected by foreign keys") });
  });

  it("query mode resolves quoted and schema-qualified names", () => {
    const idx = buildSchemaIndex(snap, "duckDb", []);
    const text = 'SELECT * FROM "order" o JOIN main.customer c ON c.id = o.customer_id;\nSELECT * FROM crm.region;';
    expect(statementTables(idx, text, 10, "duckDb").sort()).toEqual([21, 22]);
    expect(statementTables(idx, text, text.length - 3, "duckDb")).toEqual([23]);
  });

  it("cards and the SVG export are labelled <schema>.<name>", () => {
    const svg = svgOf(snap, new Set([21, 22, 23]));
    expect(svg).toContain(">main.order<");
    expect(svg).toContain(">crm.region<");
    expect(svg).toContain(">DECIMAL(10,2)<");
  });
});

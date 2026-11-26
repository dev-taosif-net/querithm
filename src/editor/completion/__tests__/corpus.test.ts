// The tier-1 completion corpus (fixtures/corpus.ts), run on every engine: each case's "must offer" and "must not offer"
// lists, exact insert texts, and two invariants for every item of every case — no name inserted half-written (`c.`,
// `c.""`: completion audit M26) and, where asked, no item listed twice.
import { describe, expect, it } from "vitest";
import type { SchemaSnapshot, SnapColumn, SnapObject, SnapParam } from "../../../ipc/contract";
import { buildSchemaIndex } from "../schemaIndex";
import { complete } from "../complete";
import type { SchemaIndex, Tier1Item } from "../types";
import { CORPUS_ENGINES, SHARED, SPECIFIC, type CorpusCase, type CorpusEngine } from "./fixtures/corpus";

const DEFAULT_SCHEMA: Record<CorpusEngine, string> = { tsql: "dbo", pg: "public", mySql: "shop", sqlite: "main", duckDb: "main", oracle: "APP" };
const KINDS = new Set(["keyword", "table", "view", "column", "proc", "func", "variable", "parameter", "schema", "database", "alias", "cte", "snippet", "type", "tempTable", "cursor", "synonym", "tableFunction"]);

function indexFor(d: CorpusEngine): SchemaIndex {
  const N = (s: string) => (d === "oracle" ? s.toUpperCase() : s);
  const col = (name: string, ordinal: number, pk = false): SnapColumn => ({ name: N(name), type: "int", nullable: !pk, identity: false, computed: false, pk, ordinal });
  const cols = (names: string[], pk = true) => names.map((n, i) => col(n, i + 1, pk && i === 0));
  const prm = (name: string, ordinal: number): SnapParam => ({ name: d === "tsql" ? `@${name}` : N(name), type: "int", ordinal, output: false, hasDefault: false });
  const s = DEFAULT_SCHEMA[d];
  const sales = d === "oracle" ? "SALES" : "sales";
  const objects: SnapObject[] = [
    { id: 1, schema: s, name: N("orders"), kind: "table", columns: cols(["order_id", "customer_id", "total", "status"]) },
    { id: 2, schema: s, name: N("customers"), kind: "table", columns: cols(["customer_id", "full_name", "email"]) },
    { id: 3, schema: s, name: N("order_items"), kind: "table", columns: cols(["order_id", "product_id", "qty"], false) },
    { id: 4, schema: s, name: N("v_sales"), kind: "view", columns: cols(["day", "amount"], false) },
    { id: 5, schema: sales, name: N("products"), kind: "table", columns: cols(["product_id", "name", "price"]) },
    // Columns named like statement verbs of some dialect (completion audit H6).
    { id: 6, schema: s, name: N("posts"), kind: "table", columns: cols(["id", "comment", "open", "title"]) },
    { id: 7, schema: s, name: N("refresh_totals"), kind: "proc", params: [prm("p_from", 1), prm("p_to", 2)] },
    { id: 8, schema: s, name: N("order_total"), kind: "func", returnType: "int", params: [prm("p_order_id", 1)] },
  ];
  const snap: SchemaSnapshot = {
    profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", objects,
    fks: [
      { name: "fk_orders_customers", parent: 1, referenced: 2, columns: [{ parent: N("customer_id"), referenced: N("customer_id") }] },
      { name: "fk_items_orders", parent: 3, referenced: 1, columns: [{ parent: N("order_id"), referenced: N("order_id") }] },
      // SQLite `product_id REFERENCES products` (no column list): the referenced column is not known (audit M26).
      { name: "fk_items_products", parent: 3, referenced: 5, columns: [{ parent: N("product_id"), referenced: "" }] },
    ],
  };
  return buildSchemaIndex(snap, d, d === "mySql" ? ["shop", "sales"] : ["app"], d === "oracle" ? "APP" : null);
}
const IDX = Object.fromEntries(CORPUS_ENGINES.map((d) => [d, indexFor(d)])) as Record<CorpusEngine, SchemaIndex>;

function run(sql: string, d: CorpusEngine): Tier1Item[] {
  const i = sql.indexOf("|");
  if (i < 0) throw new Error(`no cursor in ${sql}`);
  return complete({ text: sql.slice(0, i) + sql.slice(i + 1), offset: i, dialect: d, schema: IDX[d] }).items;
}

/** `label`, `kind:label`, a trailing `*` for a label prefix (`*` alone: any label); case-insensitive. */
function matches(item: Tier1Item, pattern: string): boolean {
  let label = pattern;
  const c = pattern.indexOf(":");
  if (c > 0 && KINDS.has(pattern.slice(0, c))) {
    if (item.kind !== pattern.slice(0, c)) return false;
    label = pattern.slice(c + 1);
  }
  const have = item.label.toLowerCase();
  const want = label.toLowerCase();
  return want.endsWith("*") ? have.startsWith(want.slice(0, -1)) : have === want;
}

const show = (items: Tier1Item[]) => items.slice(0, 40).map((i) => `${i.kind}:${i.label}`).join(", ");

/** An inserted name cut short: a qualifier with nothing (or an empty delimited name) after its dot. */
function halfWritten(item: Tier1Item): boolean {
  for (const text of [item.label, item.insertText ?? ""]) {
    for (let i = text.indexOf("."); i >= 0; i = text.indexOf(".", i + 1)) {
      const rest = text.slice(i + 1);
      if (rest.startsWith('""') || rest.startsWith("``") || rest.startsWith("[]")) return true;
      if (rest === "" || rest[0] === " " || rest[0] === ")" || rest[0] === ",") return true;
    }
  }
  return false;
}

function check(c: CorpusCase, d: CorpusEngine) {
  const items = run(c.sql, d);
  const where = `${d}: ${c.sql}${c.audit ? ` (${c.audit})` : ""}\n  got: ${show(items)}`;
  if (c.empty) expect(items, where).toEqual([]);
  for (const p of c.offer ?? []) expect(items.some((i) => matches(i, p)), `must offer ${p} — ${where}`).toBe(true);
  for (const p of c.notOffer ?? []) {
    const bad = items.filter((i) => matches(i, p)).map((i) => `${i.kind}:${i.label}`);
    expect(bad, `must not offer ${p} — ${where}`).toEqual([]);
  }
  for (const [label, text] of Object.entries(c.insert ?? {})) {
    const item = items.find((i) => i.label.toLowerCase() === label.toLowerCase());
    expect(item, `must offer ${label} — ${where}`).toBeDefined();
    expect(item!.insertText ?? item!.label, `insert text of ${label} — ${where}`).toBe(text);
  }
  if (c.unique) {
    const seen = new Set<string>();
    const dup = items.filter((i) => { const k = `${i.kind}|${i.label.toLowerCase()}`; if (seen.has(k)) return true; seen.add(k); return false; });
    expect(dup.map((i) => `${i.kind}:${i.label}`), `listed twice — ${where}`).toEqual([]);
  }
  expect(items.filter(halfWritten).map((i) => `${i.kind}:${i.label} → ${i.insertText ?? i.label}`), `half-written name — ${where}`).toEqual([]);
}

describe.each(CORPUS_ENGINES)("completion corpus: %s", (d) => {
  const cases = [...SHARED.filter((c) => !c.except?.includes(d)), ...SPECIFIC[d]];
  it("has at least 40 cases", () => expect(cases.length).toBeGreaterThanOrEqual(40));
  it.each(cases.map((c) => [c.sql, c] as const))("%s", (_sql, c) => check(c, d));
});

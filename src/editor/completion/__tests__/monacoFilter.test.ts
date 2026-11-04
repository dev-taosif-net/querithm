// Monaco filters a provider's items again by the typed word (its own fuzzyScore over filterText ?? label), so a table
// tier 1 offers can still vanish from the popup. This runs Monaco's real filter over tier 1's items, every engine.
import { describe, expect, it } from "vitest";
// @ts-expect-error -- Monaco's internal ESM module ships no type declarations.
import { fuzzyScore, FuzzyScoreOptions } from "monaco-editor/esm/vs/base/common/filters.js";
import type { Dialect, SchemaSnapshot, SnapObject } from "../../../ipc/contract";
import { buildSchemaIndex } from "../schemaIndex";
import { complete } from "../complete";
import type { Tier1Item } from "../types";

const ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb"] as const satisfies readonly Dialect[];
type Engine = (typeof ENGINES)[number];
const DEFAULT_SCHEMA: Record<Engine, string> = { tsql: "dbo", pg: "public", mySql: "shop", sqlite: "main", duckDb: "main" };
const NAMES = ["orders", "customers", "order_items", "OrderHistory", "tbl_CustomerAddress", "v_sales"];

function index(d: Engine) {
  const objects: SnapObject[] = NAMES.map((name, i) => ({ id: i + 1, schema: DEFAULT_SCHEMA[d], name, kind: name.startsWith("v_") ? "view" : "table", columns: [] }));
  objects.push({ id: 99, schema: "sales", name: "products", kind: "table", columns: [] });
  const snap: SchemaSnapshot = { profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", objects, fks: [] };
  return buildSchemaIndex(snap, d, []);
}

/** What the Monaco popup would show: tier 1's items that Monaco's own filter keeps for the typed word. */
function shown(text: string, d: Engine): Tier1Item[] {
  const offset = text.length;
  const r = complete({ text, offset, dialect: d, schema: index(d) });
  const word = text.slice(r.context.prefixStart, offset);
  if (!word) return r.items;
  return r.items.filter((i) => {
    const target = i.filterText ?? i.label;
    return !!fuzzyScore(word, word.toLowerCase(), 0, target, target.toLowerCase(), 0, FuzzyScoreOptions.default);
  });
}
const labels = (items: Tier1Item[]) => items.filter((i) => i.kind === "table" || i.kind === "view").map((i) => i.label);

const LEADS = ["SELECT * FROM ", "SELECT * FROM orders o JOIN ", "UPDATE ", "INSERT INTO ", "DELETE FROM ",
  "SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM "];

describe.each(ENGINES)("Monaco keeps tier 1's tables in the popup: %s", (d) => {
  it("every prefix of every table name keeps that table", () => {
    const misses: string[] = [];
    for (const lead of LEADS) {
      for (const name of NAMES) {
        for (let n = 1; n <= name.length; n++) {
          const typed = name.slice(0, n);
          if (!labels(shown(lead + typed, d)).includes(name)) misses.push(`${lead}${typed}`);
        }
      }
    }
    expect(misses).toEqual([]);
  });
  it("case-insensitive and camel-hump typing keeps the table", () => {
    const cases: Array<[string, string]> = [["ORD", "orders"], ["Cust", "customers"], ["oh", "OrderHistory"], ["OH", "OrderHistory"], ["tca", "tbl_CustomerAddress"], ["oi", "order_items"], ["VS", "v_sales"]];
    for (const [typed, want] of cases) expect(labels(shown(`SELECT * FROM ${typed}`, d)), typed).toContain(want);
  });
  it("another schema's table: by its name and by its qualifier", () => {
    expect(labels(shown("SELECT * FROM prod", d))).toContain("sales.products");
    expect(labels(shown("SELECT * FROM sales.prod", d))).toContain("products");
    expect(labels(shown("SELECT * FROM sales.", d))).toContain("products");
  });
  it("a word that matches no table shows none", () => {
    expect(labels(shown("SELECT * FROM xyzq", d))).toEqual([]);
  });
});

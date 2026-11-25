// Completion regressions found by the per-engine review (all six engines): clause words read as aliases, aliases and
// source names re-quoted by catalog rules, generated JOIN aliases delimited. Names follow each engine's folding:
// Oracle's catalog stores unquoted names upper case, PostgreSQL's lower case.
import { describe, expect, it } from "vitest";
import type { Dialect, SchemaSnapshot, SnapColumn, SnapObject } from "../../../ipc/contract";
import { buildSchemaIndex } from "../schemaIndex";
import { complete } from "../complete";
import type { SchemaIndex, Tier1Item } from "../types";

const ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb", "oracle"] as const satisfies readonly Dialect[];
type Engine = (typeof ENGINES)[number];
const DEFAULT_SCHEMA: Record<Engine, string> = { tsql: "dbo", pg: "public", mySql: "shop", sqlite: "main", duckDb: "main", oracle: "APP" };
/** How the engine's catalog spells an unquoted name. */
const N = (d: Engine, name: string) => (d === "oracle" ? name.toUpperCase() : name);

const col = (name: string, ordinal: number, pk = false): SnapColumn => ({ name, type: "int", nullable: !pk, identity: false, computed: false, pk, ordinal });

function indexFor(d: Engine): SchemaIndex {
  const s = DEFAULT_SCHEMA[d];
  const objects: SnapObject[] = [
    { id: 1, schema: s, name: N(d, "orders"), kind: "table", columns: [col(N(d, "order_id"), 1, true), col(N(d, "customer_id"), 2), col(N(d, "total"), 3)] },
    { id: 2, schema: s, name: N(d, "customers"), kind: "table", columns: [col(N(d, "customer_id"), 1, true), col(N(d, "full_name"), 2)] },
  ];
  const snap: SchemaSnapshot = {
    profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", objects,
    fks: [{ name: "fk_orders_customers", parent: 1, referenced: 2, columns: [{ parent: N(d, "customer_id"), referenced: N(d, "customer_id") }] }],
  };
  return buildSchemaIndex(snap, d, d === "mySql" ? ["shop"] : ["app"]);
}
const IDX = Object.fromEntries(ENGINES.map((d) => [d, indexFor(d)])) as Record<Engine, SchemaIndex>;

function run(sql: string, d: Engine): Tier1Item[] {
  const i = sql.indexOf("|");
  return complete({ text: sql.slice(0, i) + sql.slice(i + 1), offset: i, dialect: d, schema: IDX[d] }).items;
}
const ins = (i: Tier1Item | undefined) => i?.insertText ?? i?.label;

describe("completion: clause words are never aliases (every engine)", () => {
  for (const d of ENGINES) {
    it(`${d}: FROM t JOIN | offers tables (JOIN is not t's alias)`, () => {
      const items = run("SELECT * FROM orders JOIN |", d);
      expect(items.some((i) => i.label === N(d, "customers") && i.kind === "table")).toBe(true);
    });
    it(`${d}: LEFT / CROSS / USING after a table never become an alias`, () => {
      const left = run("SELECT * FROM orders LEFT JOIN customers c ON |", d);
      expect(left.some((i) => i.label.toUpperCase().startsWith("LEFT."))).toBe(false);
      expect(left.some((i) => i.label === `c.${N(d, "full_name")}`)).toBe(true);
      const cross = run("SELECT | FROM orders CROSS JOIN customers c", d);
      expect(cross.some((i) => i.kind === "alias" && i.label.toUpperCase() === "CROSS")).toBe(false);
    });
  }
  it("oracle: MERGE … USING s keeps s as the source alias, not USING", () => {
    const items = run("MERGE INTO orders t USING customers s ON (|", "oracle");
    expect(items.some((i) => i.label === "s.FULL_NAME")).toBe(true);
    expect(items.some((i) => i.kind === "alias" && i.label.toUpperCase() === "USING")).toBe(false);
  });
  it("oracle: a CTE followed by JOIN keeps the joined table in scope", () => {
    const items = run("WITH x AS (SELECT 1 AS n FROM dual) SELECT | FROM x JOIN orders o ON 1 = 1", "oracle");
    expect(items.some((i) => i.label === "o.TOTAL")).toBe(true);
  });
});

describe("completion: a single aliased table's columns go through its alias (every engine)", () => {
  const colItem = (items: Tier1Item[], label: string) => items.find((i) => i.kind === "column" && i.label === label);
  for (const d of ENGINES) {
    const total = N(d, "total");
    it(`${d}: WHERE / select list / ORDER BY insert alias.column`, () => {
      for (const sql of ["SELECT * FROM orders o WHERE |", "SELECT * FROM orders o WHERE tot|", "SELECT | FROM orders o", "SELECT * FROM orders AS o ORDER BY |"]) {
        const items = run(sql, d);
        expect(ins(colItem(items, `o.${total}`)), `${sql}`).toBe(`o.${total}`);
        expect(colItem(items, total), `${sql}: no bare duplicate`).toBeUndefined();
      }
      expect(colItem(run("SELECT * FROM orders o WHERE tot|", d), `o.${total}`)?.filterText).toBe(total);
    });
    it(`${d}: an unaliased table's columns stay bare`, () => {
      expect(ins(colItem(run("SELECT * FROM orders WHERE |", d), total))).toBe(total);
    });
  }
  // UPDATE t AS x SET col: a SET target cannot be qualified on PostgreSQL, SQLite and DuckDB, so the SET list stays bare.
  for (const d of ["pg", "mySql", "sqlite", "duckDb", "oracle"] as const) {
    it(`${d}: UPDATE t x SET | keeps target columns bare; its WHERE goes through the alias`, () => {
      const total = N(d, "total");
      expect(ins(colItem(run("UPDATE orders o SET |", d), total))).toBe(total);
      expect(colItem(run("UPDATE orders o SET |", d), `o.${total}`)).toBeUndefined();
      expect(ins(colItem(run("UPDATE orders o SET total = 1 WHERE |", d), `o.${total}`))).toBe(`o.${total}`);
    });
  }
  it("pg: INSERT INTO t AS x … ON CONFLICT DO UPDATE SET | keeps target columns bare", () => {
    const items = run("INSERT INTO orders AS o (total) VALUES (1) ON CONFLICT (order_id) DO UPDATE SET |", "pg");
    expect(ins(colItem(items, "total"))).toBe("total");
    expect(colItem(items, "o.total")).toBeUndefined();
  });
});

describe("completion: names from the statement are inserted as written (every engine)", () => {
  for (const d of ENGINES) {
    it(`${d}: alias-qualified columns and the alias itself are not re-quoted`, () => {
      const items = run("SELECT | FROM orders o JOIN customers c ON 1 = 1", d);
      const total = items.find((i) => i.label === `o.${N(d, "total")}`);
      expect(ins(total)).toBe(`o.${N(d, "total")}`);
      expect(ins(items.find((i) => i.kind === "alias" && i.label === "o"))).toBe("o");
    });
    it(`${d}: the generated alias of a FK JOIN item is never delimited`, () => {
      const join = run("SELECT * FROM orders o JOIN |", d).find((i) => i.kind === "snippet" && i.label.startsWith(N(d, "customers")));
      expect(join).toBeDefined();
      // A snippet: `<table> ${1:alias} ON o.x = ${1:alias}.y`, one bare alias placeholder in both places.
      expect(join!.isSnippet).toBe(true);
      expect(ins(join)).toMatch(/^\S+ \$\{1:([a-z]\w*)\} ON o\.\S+ = \$\{1:\1\}\.\S+$/);
      expect(join!.label).toMatch(/ ON o\.\S+ = [a-z]\w*\.\S+$/);
    });
    it(`${d}: without a declared FK, a JOIN is guessed from column names`, () => {
      const idx = buildSchemaIndex({
        profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", fks: [],
        objects: IDX[d].objects,
      }, d, d === "mySql" ? ["shop"] : ["app"]);
      const text = "SELECT * FROM orders o JOIN ";
      const join = complete({ text, offset: text.length, dialect: d, schema: idx }).items.find((i) => i.kind === "snippet" && i.label.startsWith(N(d, "customers")));
      expect(join?.detail).toBe("JOIN · by column name");
      expect(join?.label).toContain(`o.${N(d, "customer_id")} = `);
    });
    it(`${d}: CustomerId ↔ Customers.Id is guessed too`, () => {
      const objects: SnapObject[] = [
        { id: 1, schema: DEFAULT_SCHEMA[d], name: "Orders", kind: "table", columns: [col("Id", 1, true), col("CustomerId", 2)] },
        { id: 2, schema: DEFAULT_SCHEMA[d], name: "Customers", kind: "table", columns: [col("Id", 1, true), col("Name", 2)] },
      ];
      const idx = buildSchemaIndex({ profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", fks: [], objects }, d, d === "mySql" ? ["shop"] : ["app"]);
      const fromOrders = "SELECT * FROM Orders o JOIN ";
      // Mixed-case names are inserted quoted where the engine folds (PostgreSQL, Oracle): `o."CustomerId"`.
      expect(complete({ text: fromOrders, offset: fromOrders.length, dialect: d, schema: idx }).items.some((i) => /^Customers ON o\.\W?CustomerId\W? = \w+\.\W?Id\W?$/.test(i.label))).toBe(true);
      const fromCustomers = "SELECT * FROM Customers c JOIN ";
      expect(complete({ text: fromCustomers, offset: fromCustomers.length, dialect: d, schema: idx }).items.some((i) => /^Orders ON c\.\W?Id\W? = \w+\.\W?CustomerId\W?$/.test(i.label))).toBe(true);
    });
    it(`${d}: a table named in UPDATE is offered as written`, () => {
      const t = run("UPDATE orders SET total = 1 WHERE |", d).find((i) => i.kind === "table" && i.label === "orders");
      expect(ins(t)).toBe("orders");
    });
  }
  for (const d of ["pg", "oracle"] as const) {
    it(`${d}: an upper-case alias stays unquoted (it folds like the reference)`, () => {
      const items = run("SELECT | FROM orders O JOIN customers c ON 1 = 1", d);
      expect(ins(items.find((i) => i.label === `O.${N(d, "total")}`))).toBe(`O.${N(d, "total")}`);
    });
    it(`${d}: a quoted alias stays quoted`, () => {
      const items = run('SELECT | FROM orders "Ord" JOIN customers c ON 1 = 1', d);
      expect(ins(items.find((i) => i.label === `Ord.${N(d, "total")}`))).toBe(`"Ord".${N(d, "total")}`);
    });
  }
  it("oracle: catalog names still follow Oracle's folding (lower-case catalog names are quoted)", () => {
    const idx = buildSchemaIndex({
      profileId: "p", database: "app", status: "fresh", fks: [],
      objects: [{ id: 1, schema: "APP", name: "lower_t", kind: "table", columns: [col("id", 1)] }],
    }, "oracle", ["app"]);
    const items = complete({ text: "SELECT * FROM low", offset: 17, dialect: "oracle", schema: idx, autoAlias: false }).items;
    expect(ins(items.find((i) => i.label === "lower_t"))).toBe('"lower_t"');
  });
});

describe("completion: DML clauses (review regressions)", () => {
  const cols = (items: Tier1Item[]) => items.filter((i) => i.kind === "column").map((i) => i.label);
  for (const d of ENGINES) {
    it(`${d}: a comma after JOIN … ON lists tables again`, () => {
      const items = run("SELECT * FROM orders o JOIN customers c ON c.customer_id = o.customer_id, |", d);
      expect(items.some((i) => i.kind === "table" && i.label === N(d, "orders"))).toBe(true);
    });
  }
  for (const d of ["pg", "sqlite", "duckDb"] as const) {
    it(`${d}: ON CONFLICT (| offers the insert target's columns`, () => {
      expect(cols(run("INSERT INTO orders (customer_id) VALUES (1) ON CONFLICT (|", d))).toContain("order_id");
    });
    it(`${d}: DO UPDATE SET x = excluded.| offers the insert target's columns`, () => {
      const items = run("INSERT INTO orders (order_id, total) VALUES (1, 2) ON CONFLICT (order_id) DO UPDATE SET total = excluded.|", d);
      expect(cols(items)).toEqual(expect.arrayContaining(["order_id", "total"]));
    });
  }
  it("tsql: OUTPUT inserted.| and deleted.| offer the DML target's columns", () => {
    expect(cols(run("INSERT INTO orders (total) OUTPUT inserted.| VALUES (1)", "tsql"))).toContain("order_id");
    expect(cols(run("UPDATE orders SET total = 1 OUTPUT deleted.| WHERE order_id = 1", "tsql"))).toContain("total");
    expect(cols(run("DELETE FROM orders OUTPUT deleted.| WHERE order_id = 1", "tsql"))).toContain("customer_id");
  });
  for (const d of ["mySql", "sqlite"] as const) {
    it(`${d}: REPLACE INTO t (| offers t's columns; REPLACE(…) stays a function`, () => {
      const items = run("REPLACE INTO orders (|", d);
      expect(cols(items)).toContain("total");
      expect(items.some((i) => i.kind === "func")).toBe(false);
      expect(cols(run("SELECT REPLACE(o.|, 'a', 'b') FROM orders o", d))).toContain("total");
    });
  }
  it("sqlite: INSERT OR REPLACE INTO t (| and mySql: INSERT IGNORE INTO t (| offer t's columns", () => {
    expect(cols(run("INSERT OR REPLACE INTO orders (|", "sqlite"))).toContain("total");
    expect(cols(run("INSERT IGNORE INTO orders (|", "mySql"))).toContain("total");
  });
  for (const d of ENGINES) {
    it(`${d}: statement templates need a word prefix, not a letter inside one`, () => {
      const items = run("SELECT * FROM orders o WHERE o.total = 1 u|", d);
      expect(items.some((i) => i.kind === "snippet" && i.label.startsWith("EXEC"))).toBe(false);
      const tpl = run("ins|", d).find((i) => i.label.startsWith("INSERT INTO"));
      expect(tpl?.filterText).toBe("INSERT");
    });
    it(`${d}: statement templates exit snippet mode at $0 so highlights do not linger`, () => {
      const sel = run("sele|", d).find((i) => i.label === "SELECT … FROM …");
      expect(sel?.insertText).toBe("SELECT * FROM $0;");
      const del = run("del|", d).find((i) => i.label === "DELETE FROM … WHERE …");
      expect(del?.insertText).toBe("DELETE FROM ${1:table_name} WHERE $0;");
      const upd = run("upd|", d).find((i) => i.label === "UPDATE … SET … WHERE …");
      expect(upd?.insertText).toBe("UPDATE ${1:table}\nSET ${2:column} = ${3:value}\nWHERE $0;");
      const ins = run("ins|", d).find((i) => i.label === "INSERT INTO … VALUES …");
      expect(ins?.insertText).toBe("INSERT INTO ${1:table} (${2:columns})\nVALUES ($0);");
      const alt = run("alt|", d).find((i) => i.label === "ALTER TABLE …");
      expect(alt?.insertText).toBe("ALTER TABLE ${1:table_name} $0;");
    });
  }
});

describe("completion: descriptions and the details pane", () => {
  for (const d of ENGINES) {
    it(`${d}: an FK column names the table it references; the details pane has type, flags and the reference`, () => {
      const items = run("SELECT o.| FROM orders o", d);
      const fk = items.find((i) => i.label === N(d, "customer_id"))!;
      expect(fk.detail).toBe(`int NULL FK→${N(d, "customers")}`);
      // Markdown: `_` is escaped in names.
      expect(fk.documentation).toContain(`**${DEFAULT_SCHEMA[d]}.${N(d, "orders")}.${N(d, "customer_id")}**`.replace(/_/g, "\\_"));
      expect(fk.documentation).toContain("references `");
      const tbl = run("SELECT * FROM cust|", d).find((i) => i.label === N(d, "customers") && i.kind === "table")!;
      expect(tbl.detail).toContain("2 cols");
      expect(tbl.documentation).toContain("| Column | Type | Nullability |");
    });
  }
  it("server names in the details pane stay inert markdown (L10)", () => {
    const evil = "x](https://evil.example)<img src=x>|";
    const idx = buildSchemaIndex({
      profileId: "p", database: "app", status: "fresh", fks: [],
      objects: [{ id: 1, schema: "dbo", name: "t", kind: "table", columns: [col(evil, 1)] }],
    }, "tsql", ["app"]);
    const items = complete({ text: "SELECT t. FROM dbo.t", offset: 9, dialect: "tsql", schema: idx }).items;
    const doc = items.find((i) => i.kind === "column")!.documentation!;
    expect(doc).not.toMatch(/(^|[^\\])\]\(/); // no unescaped "](": no link
    expect(doc).not.toMatch(/(^|[^\\])</); // no unescaped "<": no HTML
  });
});

describe("completion: an object's name is shown once, its schema on the right (every engine)", () => {
  // Regression (2026-11-25): the list read `sales.orders  table sales.orders`, and default-schema rows had no schema.
  const OTHER: Record<Engine, string> = { tsql: "sales", pg: "sales", mySql: "sales", sqlite: "aux", duckDb: "sales", oracle: "SALES" };
  function twoSchemas(d: Engine): SchemaIndex {
    const objects: SnapObject[] = [
      ...IDX[d].objects,
      { id: 10, schema: OTHER[d], name: N(d, "orders"), kind: "table", columns: [col(N(d, "order_id"), 1, true)] },
      { id: 11, schema: OTHER[d], name: N(d, "order_totals"), kind: "view", columns: [col(N(d, "total"), 1), col(N(d, "n"), 2)] },
    ];
    return buildSchemaIndex({ profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", objects, fks: [] }, d, d === "mySql" ? ["shop"] : ["app"]);
  }
  const at = (sql: string, d: Engine, idx: SchemaIndex) => {
    const i = sql.indexOf("|");
    return complete({ text: sql.slice(0, i) + sql.slice(i + 1), offset: i, dialect: d, schema: idx, autoAlias: false }).items;
  };
  const shown = (i: Tier1Item) => i.display ?? i.label;
  for (const d of ENGINES) {
    const s = DEFAULT_SCHEMA[d], o = OTHER[d], orders = N(d, "orders");
    it(`${d}: two tables of one name stay two items, told apart by the schema on the right`, () => {
      const idx = twoSchemas(d);
      const items = at("SELECT * FROM ord|", d, idx).filter((i) => i.kind === "table" && shown(i) === orders);
      expect(items.map((i) => i.detail).sort()).toEqual([`${o} · 1 col`, `${s} · 3 cols`].sort());
      const other = items.find((i) => i.detail!.startsWith(o))!;
      expect(other.label).toBe(`${o}.${orders}`); // its identity and what Enter inserts keep the schema
      expect(other.insertText ?? other.label).toBe(`${o}.${orders}`);
      const view = at("SELECT * FROM ord|", d, idx).find((i) => i.kind === "view")!;
      expect([shown(view), view.detail]).toEqual([N(d, "order_totals"), `${o} · view · 2 cols`]);
    });
    it(`${d}: no row repeats its name on the right; a typed schema is not repeated either`, () => {
      const idx = twoSchemas(d);
      for (const i of at("SELECT * FROM |", d, idx).filter((x) => x.schemaObject)) {
        expect(i.detail, i.label).not.toContain(shown(i));
        expect(shown(i), i.label).not.toContain(".");
      }
      const typed = at(`SELECT * FROM ${o}.ord|`, d, idx).filter((i) => i.schemaObject);
      expect(typed.map((i) => [shown(i), i.detail])).toEqual(expect.arrayContaining([[orders, "1 col"], [N(d, "order_totals"), "view · 2 cols"]]));
    });
    it(`${d}: the details pane is still built for every listed object`, () => {
      const items = at("SELECT * FROM |", d, twoSchemas(d)).filter((i) => i.schemaObject);
      expect(items.length).toBeGreaterThan(0);
      for (const i of items) expect(i.documentation, i.label).toContain("| Column | Type | Nullability |");
    });
  }
  it("with more objects than the list keeps (500), each kept item still has its details pane (built after the cut)", () => {
    const objects: SnapObject[] = Array.from({ length: 700 }, (_, k) => ({ id: k + 1, schema: k % 2 ? "sales" : "dbo", name: `t${k}`, kind: "table" as const, columns: [col("id", 1, true)] }));
    const idx = buildSchemaIndex({ profileId: "p", database: "app", status: "fresh", objects, fks: [] }, "tsql", ["app"]);
    const items = complete({ text: "SELECT * FROM t", offset: 15, dialect: "tsql", schema: idx }).items.filter((i) => i.schemaObject);
    expect(items).toHaveLength(500);
    expect(items.every((i) => i.documentation?.includes("| Column |"))).toBe(true);
  });
});

describe("completion: aliases, synonyms and engine row sources (review regressions)", () => {
  const colLabels = (items: Tier1Item[]) => items.filter((i) => i.kind === "column").map((i) => i.label);
  for (const d of ENGINES) {
    it(`${d}: ORDER BY offers the select list's aliases`, () => {
      const items = run("SELECT o.total AS amount, o.order_id oid FROM orders o ORDER BY |", d);
      expect(colLabels(items)).toEqual(expect.arrayContaining(["amount", "oid"]));
      expect(items.find((i) => i.label === "amount")?.detail).toBe("select-list alias");
    });
  }
  it("GROUP BY offers select aliases only where the engine allows them", () => {
    const sql = "SELECT o.total AS amount, COUNT(*) FROM orders o GROUP BY |";
    for (const d of ["pg", "mySql", "sqlite", "duckDb"] as const) expect(colLabels(run(sql, d))).toContain("amount");
    for (const d of ["tsql", "oracle"] as const) expect(colLabels(run(sql, d))).not.toContain("amount");
  });

  function withSynonym(d: "tsql" | "oracle", schema: string, synName: string): SchemaIndex {
    const base = IDX[d];
    const objects: SnapObject[] = [...base.objects, { id: 50, schema, name: synName, kind: "synonym", targetObjectId: 1 }];
    return buildSchemaIndex({ profileId: "p", database: "app", status: "fresh", objects, fks: [] }, d, ["app"]);
  }
  for (const d of ["tsql", "oracle"] as const) {
    it(`${d}: a synonym has its target's columns`, () => {
      const idx = withSynonym(d, DEFAULT_SCHEMA[d], N(d, "syn_orders"));
      const text = `SELECT s. FROM ${N(d, "syn_orders")} s`;
      const items = complete({ text, offset: 9, dialect: d, schema: idx }).items;
      expect(colLabels(items)).toContain(N(d, "total"));
    });
  }
  it("oracle: a public synonym is inserted without PUBLIC.", () => {
    const idx = withSynonym("oracle", "PUBLIC", "PSYN");
    const item = complete({ text: "SELECT * FROM psy", offset: 17, dialect: "oracle", schema: idx, autoAlias: false }).items.find((i) => i.label === "PSYN");
    expect(item?.insertText ?? item?.label).toBe("PSYN");
  });
  it("oracle: DUAL is offered as a row source", () => {
    expect(run("SELECT 1 FROM du|", "oracle").some((i) => i.label === "DUAL")).toBe(true);
    expect(run("SELECT 1 FROM du|", "pg").some((i) => i.label === "DUAL")).toBe(false);
  });
  it("duckDb: catalog.| offers the catalog's default-schema tables too", () => {
    const items = run("SELECT * FROM app.ord|", "duckDb");
    expect(items.some((i) => i.label === "orders")).toBe(true);
  });
});

describe("completion: each query level sees its own tables and its enclosing ones (every engine)", () => {
  const inScope = (sql: string, d: Engine) => {
    const items = run(sql, d);
    return new Set(items.filter((i) => i.kind === "alias" || (i.kind === "table" && i.detail === "table in scope")).map((i) => i.label.toLowerCase()));
  };
  for (const d of ENGINES) {
    it(`${d}: the outer select list does not see a derived table's or a CTE body's tables`, () => {
      expect(inScope("SELECT | FROM (SELECT order_id FROM orders) dt", d)).toEqual(new Set(["dt"]));
      expect(inScope("WITH x AS (SELECT order_id FROM orders) SELECT | FROM x", d).has("orders")).toBe(false);
    });
    it(`${d}: an EXISTS subquery's table is not visible outside it, but the outer one is visible inside`, () => {
      expect(inScope("SELECT | FROM orders o WHERE EXISTS (SELECT 1 FROM customers c WHERE c.customer_id = o.customer_id)", d)).toEqual(new Set(["o"]));
      expect(inScope("SELECT * FROM orders o WHERE EXISTS (SELECT 1 FROM customers c WHERE |)", d)).toEqual(new Set(["o", "c"]));
    });
    it(`${d}: a UNION branch sees only its own FROM`, () => {
      expect(inScope("SELECT order_id FROM orders o UNION SELECT | FROM customers c", d)).toEqual(new Set(["c"]));
    });
    it(`${d}: an inner alias hides an outer one with the same name`, () => {
      const items = run("SELECT * FROM customers o WHERE 1 = (SELECT 1 FROM orders o WHERE o.|)", d);
      expect(items.some((i) => i.label === N(d, "total"))).toBe(true);
      expect(items.some((i) => i.label === N(d, "full_name"))).toBe(false);
    });
    it(`${d}: INSERT … SELECT reads its source, not the insert target`, () => {
      expect(inScope("INSERT INTO orders (customer_id) SELECT | FROM customers c", d)).toEqual(new Set(["c"]));
    });
  }
});

describe("completion: SELECT * in a derived table or CTE (every engine)", () => {
  for (const d of ENGINES) {
    it(`${d}: d.| and x.| list the columns the * reads`, () => {
      const dt = run("SELECT * FROM (SELECT * FROM orders) d WHERE d.|", d).map((i) => i.label);
      expect(dt).toEqual(expect.arrayContaining([N(d, "order_id"), N(d, "total")]));
      const cte = run("WITH x AS (SELECT * FROM orders o JOIN customers c ON 1 = 1) SELECT x.| FROM x", d).map((i) => i.label);
      expect(cte).toEqual(expect.arrayContaining([N(d, "total"), N(d, "full_name")]));
    });
  }
});

describe("completion: PostgreSQL MERGE stays one statement", () => {
  it("pg: WHEN MATCHED THEN UPDATE SET | and THEN INSERT (| see the target and source", () => {
    const head = "MERGE INTO orders t USING customers s ON t.customer_id = s.customer_id ";
    const set = run(`${head}WHEN MATCHED THEN UPDATE SET |`, "pg").map((i) => i.label);
    expect(set).toEqual(expect.arrayContaining(["t.total", "s.full_name"]));
    const src = run(`${head}WHEN MATCHED THEN UPDATE SET total = s.|`, "pg").map((i) => i.label);
    expect(src).toContain("full_name");
    const ins = run(`${head}WHEN NOT MATCHED THEN INSERT (|`, "pg");
    expect(ins.filter((i) => i.kind === "column").map((i) => i.label)).toContain("total");
    expect(ins.some((i) => i.kind === "table")).toBe(false);
  });
});

describe("completion: built-in row sources", () => {
  const cols = (items: Tier1Item[]) => items.filter((i) => i.kind === "column").map((i) => i.label);
  it("sqlite: pragma_table_info('t') p → p.| lists its fixed columns", () => {
    expect(cols(run("SELECT p.| FROM pragma_table_info('orders') p", "sqlite"))).toEqual(expect.arrayContaining(["name", "type", "notnull", "pk"]));
  });
  it("pg: generate_series(…) g → g.| offers g (the column is named after the alias)", () => {
    expect(cols(run("SELECT g.| FROM generate_series(1, 10) g", "pg"))).toEqual(["g"]);
  });
  it("oracle: DUAL has DUMMY", () => {
    expect(cols(run("SELECT d.| FROM dual d", "oracle"))).toEqual(["DUMMY"]);
  });
  it("sqlite: the temp schema is inserted bare", () => {
    const idx = buildSchemaIndex({
      profileId: "p", database: "app", status: "fresh", fks: [],
      objects: [{ id: 1, schema: "temp", name: "logs", kind: "table", columns: [col("id", 1)] }],
    }, "sqlite", ["app"]);
    const item = complete({ text: "SELECT * FROM log", offset: 17, dialect: "sqlite", schema: idx, autoAlias: false }).items.find((i) => i.label === "temp.logs");
    expect(item?.insertText ?? item?.label).toBe("temp.logs");
  });
});

describe("completion: usage ranking", () => {
  for (const d of ENGINES) {
    it(`${d}: a used table ranks first, and a JOIN item counts for the table it joins`, () => {
      const text = "SELECT * FROM ";
      const usage = new Map([[N(d, "customers").toLowerCase(), 50]]);
      const items = complete({ text, offset: text.length, dialect: d, schema: IDX[d], usage, autoAlias: false }).items.filter((i) => i.kind === "table");
      expect(items[0].label).toBe(N(d, "customers"));
      const join = run("SELECT * FROM orders o JOIN |", d).find((i) => i.kind === "snippet" && i.label.startsWith(N(d, "customers")));
      expect(join?.usageKey).toBe(N(d, "customers").toLowerCase());
    });
  }
});

describe("completion: T-SQL temp tables and table variables", () => {
  const cols = (items: Tier1Item[]) => items.filter((i) => i.kind === "column").map((i) => i.label);
  it("CREATE TABLE #t (…) gives #t its columns, constraints left out", () => {
    const sql = "CREATE TABLE #t (a int NOT NULL, b varchar(10), CONSTRAINT pk PRIMARY KEY (a));\nSELECT t.| FROM #t t";
    expect(cols(run(sql, "tsql"))).toEqual(expect.arrayContaining(["a", "b"]));
    expect(cols(run(sql, "tsql"))).not.toContain("CONSTRAINT");
  });
  it("DECLARE @t TABLE (…) gives @t its columns, also after another variable", () => {
    expect(cols(run("DECLARE @n int, @t TABLE (id int, name nvarchar(50));\nSELECT x.| FROM @t x", "tsql"))).toEqual(expect.arrayContaining(["id", "name"]));
  });
  it("SELECT … INTO #t gives #t the select list's columns", () => {
    expect(cols(run("SELECT order_id, total AS amount INTO #t FROM orders;\nSELECT x.| FROM #t x", "tsql"))).toEqual(expect.arrayContaining(["order_id", "amount"]));
  });
  it("a definition in an earlier batch (before GO) does not count", () => {
    expect(cols(run("CREATE TABLE #t (a int);\nGO\nSELECT t.| FROM #t t", "tsql"))).toEqual([]);
  });
});

describe("completion: T-SQL PIVOT and temporal tables", () => {
  const cols = (items: Tier1Item[]) => items.filter((i) => i.kind === "column").map((i) => i.label);
  it("p.| after PIVOT lists the kept and pivoted columns, not the pivoted-away ones", () => {
    const c = cols(run("SELECT p.| FROM (SELECT customer_id, status, total FROM orders) src PIVOT (SUM(total) FOR status IN ([Open], [Closed])) AS p", "tsql"));
    expect(c).toEqual(expect.arrayContaining(["customer_id", "Open", "Closed"]));
    expect(c).not.toContain("status");
    expect(c).not.toContain("total");
  });
  it("u.| after UNPIVOT lists the kept columns plus the value and name columns", () => {
    const c = cols(run("SELECT u.| FROM (SELECT order_id, 1 AS a, 2 AS b FROM orders) s UNPIVOT (val FOR col IN (a, b)) AS u", "tsql"));
    expect(c).toEqual(expect.arrayContaining(["order_id", "val", "col"]));
    expect(c).not.toContain("a");
  });
  it("FOR SYSTEM_TIME … keeps the alias after it", () => {
    for (const clause of ["AS OF '2024-01-01'", "ALL", "FROM @a TO @b", "BETWEEN '2024-01-01' AND SYSUTCDATETIME()", "CONTAINED IN (@a, @b)"]) {
      expect(cols(run(`SELECT o.| FROM orders FOR SYSTEM_TIME ${clause} o`, "tsql")), clause).toContain("total");
    }
  });
});

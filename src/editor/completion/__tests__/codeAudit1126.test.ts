// Tier-1 fixes from the code audit of 2026-11-26 (frontend): every case failed before its fix; the "still" cases pin
// what the fixes keep.
import { describe, expect, it } from "vitest";
import type { Dialect, SchemaSnapshot, SnapColumn, SnapObject } from "../../../ipc/contract";
import { buildSchemaIndex, fkReference } from "../schemaIndex";
import { complete } from "../complete";
import { statementBoundsAt } from "../sqlText";
import { clearTokenCache, getStream, tokenize } from "../lexer";
import { mdCode } from "../../hoverMarkdown";
import type { SchemaIndex, Tier1Item } from "../types";

const ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb", "oracle"] as const satisfies readonly Dialect[];
type Engine = (typeof ENGINES)[number];
const DEFAULT_SCHEMA: Record<Engine, string> = { tsql: "dbo", pg: "public", mySql: "shop", sqlite: "main", duckDb: "main", oracle: "APP" };
const N = (d: Engine, name: string) => (d === "oracle" ? name.toUpperCase() : name);
const col = (name: string, ordinal: number): SnapColumn => ({ name, type: "int", nullable: true, identity: false, computed: false, pk: ordinal === 1, ordinal });
const LINK = "[x](https://example.invalid/p)";

function indexFor(d: Engine): SchemaIndex {
  const s = DEFAULT_SCHEMA[d];
  const objects: SnapObject[] = [
    { id: 1, schema: s, name: N(d, "posts"), kind: "table", columns: ["id", "comment", "open", "title"].map((c, k) => col(N(d, c), k + 1)) },
    { id: 2, schema: s, name: N(d, "orders"), kind: "table", columns: ["order_id", "customer_id", "total", "status"].map((c, k) => col(N(d, c), k + 1)) },
    { id: 3, schema: s, name: N(d, "customers"), kind: "table", columns: ["customer_id", "email", "region"].map((c, k) => col(N(d, c), k + 1)) },
    { id: 4, schema: s, name: N(d, "staging"), kind: "table", columns: ["sid", "payload"].map((c, k) => col(N(d, c), k + 1)) },
    { id: 5, schema: s, name: N(d, "ship_order"), kind: "proc", params: [{ name: LINK, type: "int ![i](https://example.invalid/i.png)", ordinal: 1, output: false, hasDefault: false }] },
  ];
  const snap: SchemaSnapshot = { profileId: "p", database: d === "mySql" ? "shop" : "app", status: "fresh", objects, fks: [] };
  return buildSchemaIndex(snap, d, d === "mySql" ? ["shop"] : ["app"]);
}
const IDX = Object.fromEntries(ENGINES.map((d) => [d, indexFor(d)])) as Record<Engine, SchemaIndex>;

function result(sql: string, d: Engine) {
  const i = sql.indexOf("|");
  return complete({ text: sql.slice(0, i) + sql.slice(i + 1), offset: i, dialect: d, schema: IDX[d] });
}
const run = (sql: string, d: Engine): Tier1Item[] => result(sql, d).items;
const has = (items: Tier1Item[], label: string) => items.some((i) => i.label === label);
const labels = (items: Tier1Item[]) => items.map((i) => i.label).slice(0, 40).join(", ");
const ins = (items: Tier1Item[], label: string) => { const i = items.find((x) => x.label === label); return i?.insertText ?? i?.label; };
/** The statement Run current statement takes at the `|`. */
function bounds(sql: string, d: Engine): string {
  const i = sql.indexOf("|");
  const text = sql.slice(0, i) + sql.slice(i + 1);
  const b = statementBoundsAt(text, i, d);
  return text.slice(b.start, b.end);
}

describe("H11: a table named like an Object.prototype member does not break completion", () => {
  for (const d of ENGINES) {
    for (const name of ["constructor", "toString", "__proto__", "hasOwnProperty", "valueOf"]) {
      it(`${d}: JOIN ${name}`, () => {
        const items = run(`SELECT | FROM orders o JOIN ${name} c ON c.id = o.order_id`, d);
        expect(has(items, `o.${N(d, "total")}`) || has(items, N(d, "total")), labels(items)).toBe(true);
      });
    }
  }
  it("still: a built-in row source keeps its columns (SQLite pragma_table_info, Oracle DUAL)", () => {
    expect(has(run("SELECT p.| FROM pragma_table_info('orders') p", "sqlite"), "notnull")).toBe(true);
    expect(has(run("SELECT d.| FROM dual d", "oracle"), "DUMMY")).toBe(true);
  });
});

describe("M17: a procedure's parameter names and types from the server are inert in the details pane", () => {
  for (const [d, sql] of [["tsql", "EXEC ship|"], ["pg", "CALL ship|"], ["mySql", "CALL ship|"]] as const) {
    it(`${d}: the parameter list is inline code, not markdown`, () => {
      const p = run(sql, d).find((i) => i.label.endsWith("ship_order"));
      expect(p, labels(run(sql, d))).toBeDefined();
      expect(p!.documentation).toBe(mdCode(`${LINK} int ![i](https://example.invalid/i.png)`));
    });
  }
});

describe("M15: inside a PostgreSQL $$ body, tier 1 reads the body's own tokens", () => {
  const fn = (body: string) => `CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ ${body} $$`;
  it("no list right after a number (`> 1.|`), as outside a body", () => {
    expect(result("SELECT * FROM orders WHERE total > 1.|", "pg").context.inNumber).toBe(true);
    const r = result(fn("BEGIN IF 2 > 1.| THEN RETURN 1; END IF; END"), "pg");
    expect(r.context.inNumber).toBe(true);
    expect(r.items).toEqual([]);
  });
  it("a FROM reference gets its alias, as outside a body", () => {
    expect(ins(run("SELECT * FROM ord|", "pg"), "orders")).toBe("orders ord");
    expect(ins(run(fn("BEGIN PERFORM * FROM ord|; END"), "pg"), "orders")).toBe("orders ord");
    expect(ins(run("DO $$ BEGIN PERFORM * FROM ord|; END $$", "pg"), "orders")).toBe("orders ord");
  });
  it("still: no alias where one is already written in the body", () => {
    expect(ins(run(fn("BEGIN PERFORM * FROM ord| x; END"), "pg"), "orders")).toBe("orders");
  });
  it("an opened quote is completed as a quoted name, as outside a body", () => {
    expect(ins(run('SELECT * FROM "ord|', "pg"), "orders")).toBe('"orders"');
    expect(ins(run(fn('BEGIN PERFORM * FROM "ord|; END'), "pg"), "orders")).toBe('"orders"');
  });
  it("a statement may start on the next line after a FROM item, as outside a body", () => {
    expect(has(run("SELECT * FROM orders o\nsel|", "pg"), "SELECT")).toBe(true);
    expect(has(run(fn("BEGIN PERFORM * FROM orders o\nsel|; END"), "pg"), "SELECT")).toBe(true);
  });
});

describe("M16: statement edges in the tier-1 scanner", () => {
  it("mySql: CASE … THEN TRUNCATE( / THEN IF( is a function call: the FROM after it stays in scope", () => {
    expect(has(run("SELECT CASE WHEN o.total > 1 THEN TRUNCATE(o.|, 0) END FROM orders o", "mySql"), "total")).toBe(true);
    expect(has(run("SELECT CASE WHEN o.total > 1 THEN IF(o.|, 1, 0) ELSE 0 END FROM orders o", "mySql"), "total")).toBe(true);
    expect(has(run("SELECT CASE WHEN o.total > 1 THEN 0 ELSE IF(o.|, 1, 0) END FROM orders o", "mySql"), "total")).toBe(true);
  });
  it("still: a TRUNCATE statement after THEN in a stored program is a statement", () => {
    expect(bounds("IF 1 = 1 THEN\n  TRUNCATE TABLE orders|;\nEND IF;", "mySql")).toBe("TRUNCATE TABLE orders");
  });
  it("pg: a writable CTE's INSERT is not the target of the outer INSERT's ON CONFLICT … DO UPDATE SET", () => {
    const q = run("WITH s AS (INSERT INTO staging (sid) VALUES (1) RETURNING sid) INSERT INTO orders (order_id) SELECT sid FROM s ON CONFLICT (order_id) DO UPDATE SET |", "pg");
    expect(has(q, "status"), labels(q)).toBe(true);
    expect(has(q, "payload") || q.some((i) => i.label.includes(".")), labels(q)).toBe(false);
  });
  for (const verb of ["INSERT INTO", "REPLACE INTO", "INSERT"] as const) {
    it(`mySql: ${verb} … SET, then a SELECT on the next line is a statement of its own`, () => {
      const q = run(`${verb} orders SET total = 1\nSELECT | FROM customers`, "mySql");
      expect(has(q, "email"), labels(q)).toBe(true);
      expect(has(q, "status") || has(q, "orders.status"), labels(q)).toBe(false);
      expect(bounds(`${verb} orders SET total = 1\nSELECT * FROM cust|omers`, "mySql")).toBe("SELECT * FROM customers");
    });
  }
  it("still: mySql INSERT … SET … ON DUPLICATE KEY UPDATE stays one statement", () => {
    expect(bounds("INSERT INTO orders SET total = 1\nON DUPLICATE KEY UPDATE status = |1", "mySql")).toBe("INSERT INTO orders SET total = 1\nON DUPLICATE KEY UPDATE status = 1");
  });
  // A verb the dialect does not reserve is lexed as a name: MERGE, TRUNCATE and CALL in Oracle and PostgreSQL.
  for (const d of ["oracle", "pg"] as const) {
    for (const next of [
      "MERGE INTO posts p USING staging s ON (p.id = s.sid) WHEN MATCHED THEN UPDATE SET p.title = s.payload",
      "TRUNCATE TABLE posts",
      "CALL ship_order(1)",
    ]) {
      it(`${d}: an unreserved verb on the next line ends an unfinished WHERE (${next.split(" ")[0]})`, () => {
        const items = run(`SELECT * FROM orders WHERE |\n${next}`, d);
        expect(has(items, N(d, "status")), labels(items)).toBe(true);
        expect(items.some((i) => i.label.endsWith(N(d, "title"))), labels(items)).toBe(false);
      });
    }
    // PostgreSQL: Querithm reserves MERGE, TRUNCATE and CALL there (keywords.ts), so such a column is written quoted.
    if (d === "oracle") it(`${d}: still, a column named like an unreserved verb on its own line starts no statement`, () => {
      const items = run("SELECT\n  truncate\n, | FROM orders o", d);
      expect(has(items, `o.${N(d, "status")}`), labels(items)).toBe(true);
    });
  }
});

describe("L4: MySQL DESC on a line of its own starts a DESCRIBE", () => {
  it("mySql: after an unfinished statement, the DESC line is its own statement", () => {
    expect(bounds("SELECT * FROM orders WHERE total > 1\nDESC cust|omers", "mySql")).toBe("DESC customers");
    expect(bounds("SELECT * FROM orders WHERE total > 1\nDESC `cust|omers`", "mySql")).toBe("DESC `customers`");
    const items = run("SELECT * FROM orders WHERE |\nDESC posts", "mySql");
    expect(has(items, "status"), labels(items)).toBe(true);
    expect(has(items, "title") || has(items, "posts.title"), labels(items)).toBe(false);
  });
  it("still: DESC as ORDER BY's direction, on its own line too, stays in the statement", () => {
    for (const sql of [
      "SELECT * FROM orders ORDER BY total DESC|",
      "SELECT * FROM orders ORDER BY total\nDESC|",
      "SELECT * FROM orders ORDER BY total\nDESC\nLIMIT 5|",
      "SELECT * FROM orders ORDER BY total\nDESC, order_id|",
      "SELECT * FROM orders ORDER BY total\n  DESC LIMIT 5|",
    ]) expect(bounds(sql, "mySql"), sql).toBe(sql.replace("|", ""));
  });
});

describe("L9: the incremental lexer, now walking back to a line's start only where GO is", () => {
  for (const d of ENGINES) {
    it(`${d}: edits on a 400 kB one-line script lex as a full lex does`, () => {
      clearTokenCache();
      const line = "SELECT a, b, c FROM t WHERE x = 1 AND y = 'two'; ".repeat(8000);
      getStream(line, d);
      let text = line;
      for (const [at, ins] of [[text.length - 10, "z"], [200_000, " q"], [5, "\nGO\n"], [text.length - 3, "'"]] as const) {
        text = text.slice(0, at) + ins + text.slice(at);
        const inc = getStream(text, d).code.map((t) => `${t.kind}:${t.start}:${t.end}`);
        const full = tokenize(text, d).filter((t) => t.kind !== "comment").map((t) => `${t.kind}:${t.start}:${t.end}`);
        expect(inc.length, d).toBe(full.length);
        expect(inc.join(" ") === full.join(" "), d).toBe(true);
      }
    });
  }
});

describe("L10: a SQLite REFERENCES without a column list names the table without a trailing dot", () => {
  it("sqlite: fkReference", () => {
    const snap: SchemaSnapshot = {
      profileId: "p", database: "app", status: "fresh", fks: [{ name: "fk", parent: 2, referenced: 3, columns: [{ parent: "customer_id", referenced: "" }] }] as never,
      objects: [
        { id: 2, schema: "main", name: "orders", kind: "table", columns: [col("order_id", 1), col("customer_id", 2)] },
        { id: 3, schema: "main", name: "customers", kind: "table", columns: [col("customer_id", 1)] },
      ],
    };
    const idx = buildSchemaIndex(snap, "sqlite", ["app"]);
    expect(fkReference(idx, 2, "customer_id")).toBe("main.customers");
    const c = complete({ text: "SELECT o. FROM orders o", offset: 9, dialect: "sqlite", schema: idx }).items.find((i) => i.label === "customer_id");
    expect(c?.detail).toBe("int NULL FK→customers");
    expect(fkReference(buildSchemaIndex({ ...snap, fks: [{ name: "fk", parent: 2, referenced: 3, columns: [{ parent: "customer_id", referenced: "customer_id" }] }] as never }, "sqlite", ["app"]), 2, "customer_id"))
      .toBe("main.customers.customer_id");
  });
});

describe("R1: a reserved word written bare as a select-list label is quoted in the form the engine stored", () => {
  it("oracle: `status AS type` is the column TYPE, inserted \"TYPE\"", () => {
    expect(ins(run("WITH x AS (SELECT status AS type FROM orders) SELECT | FROM x", "oracle"), "type")).toBe('"TYPE"');
    expect(ins(run('WITH x AS (SELECT status AS type FROM orders) SELECT x."ty| FROM x', "oracle"), "type")).toBe('"TYPE"');
  });
  it("pg: `status AS Order` is the column order, inserted \"order\"", () => {
    expect(ins(run("WITH x AS (SELECT status AS Order FROM orders) SELECT | FROM x", "pg"), "Order")).toBe('"order"');
  });
  it("still: a label written quoted keeps its case, and a bare one that needs no quote stays bare", () => {
    expect(ins(run('WITH x AS (SELECT status AS "Type" FROM orders) SELECT | FROM x', "oracle"), "Type")).toBe('"Type"');
    expect(ins(run("WITH x AS (SELECT status AS st FROM orders) SELECT | FROM x", "oracle"), "st")).toBe("st");
    expect(ins(run("WITH x AS (SELECT status AS st FROM orders) SELECT | FROM x", "pg"), "st")).toBe("st");
  });
  for (const d of ["tsql", "mySql", "sqlite", "duckDb"] as const) {
    it(`${d}: still, an engine that does not fold quotes the label as written`, () => {
      const items = run("WITH x AS (SELECT status AS Order FROM orders) SELECT | FROM x", d);
      const got = ins(items, "Order");
      if (got !== undefined) expect(got.slice(1, -1), labels(items)).toBe("Order");
    });
  }
});

describe("R4: the $$ body tier 1 read is returned with the result, so the editor does not slice it again", () => {
  it("pg: a body, and the innermost of a body in a body", () => {
    const outer = "CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN PERFORM * FROM ord|; END $$";
    const t1 = outer.replace("|", "");
    const b1 = result(outer, "pg").context.body!;
    expect(b1.base).toBe(t1.indexOf("$$") + 2);
    expect(b1.text).toBe(t1.slice(b1.base, t1.lastIndexOf("$$")));
    const nested = "CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN DO $i$ BEGIN PERFORM * FROM ord|; END $i$; END $$";
    const t2 = nested.replace("|", "");
    const b2 = result(nested, "pg").context.body!;
    expect(b2.base).toBe(t2.indexOf("$i$") + 3);
    expect(b2.text).toBe(t2.slice(b2.base, t2.lastIndexOf("$i$")));
    expect(ins(run(nested, "pg"), "orders")).toBe("orders ord");
  });
  it("still: no body outside one, and none on another engine", () => {
    expect(result("SELECT * FROM ord|", "pg").context.body).toBeUndefined();
    for (const d of ENGINES) expect(result("SELECT $$x$$, * FROM ord|", d).context.body).toBeUndefined();
  });
});

describe("R3-1: a select-list alias written quoted is inserted quoted, as written", () => {
  for (const [d, alias, typed] of [["pg", "Total", "T"], ["oracle", "total", "t"], ["duckDb", "Total", "T"]] as const) {
    it(`${d}: ORDER BY a quoted alias, bare and after a typed quote`, () => {
      expect(ins(run(`SELECT sum(total) AS "${alias}" FROM orders ORDER BY ${typed}|`, d), alias)).toBe(`"${alias}"`);
      expect(ins(run(`SELECT sum(total) AS "${alias}" FROM orders ORDER BY "${typed}|`, d), alias)).toBe(`"${alias}"`);
    });
  }
  for (const [d, open, close] of [["tsql", "[", "]"], ["mySql", "`", "`"], ["sqlite", '"', '"']] as const) {
    it(`${d}: an engine that does not fold inserts the alias delimited as written`, () => {
      expect(ins(run(`SELECT sum(total) AS ${open}Total${close} FROM orders ORDER BY T|`, d), "Total")).toBe(`${open}Total${close}`);
    });
  }
  it("still: a bare alias on a folding engine stays bare, folded when a quote is typed", () => {
    expect(ins(run("SELECT sum(total) AS Total FROM orders ORDER BY T|", "pg"), "Total")).toBe("Total");
    expect(ins(run('SELECT sum(total) AS Total FROM orders ORDER BY "T|', "pg"), "Total")).toBe('"total"');
  });
});

describe("fourth round: H12 — OR / IN at a line's end, then a verb on the next line", () => {
  for (const d of ENGINES) {
    for (const [what, sql] of [
      ["OR", "SELECT * FROM orders WHERE total > 1 OR |\nUPDATE posts SET title = 'x' WHERE id = 1"],
      ["IN", "SELECT * FROM orders WHERE status IN |\nDELETE FROM posts WHERE id = 1"],
    ] as const) {
      it(`${d}: after ${what}, the next line's statement is not in scope`, () => {
        const items = run(sql, d);
        expect(items.some((i) => i.label.endsWith(N(d, "title"))), labels(items)).toBe(false);
      });
    }
  }
  it("still: CREATE OR on one line and ALTER on the next is one statement (T-SQL)", () => {
    expect(bounds("CREATE OR\nALTER VIEW v AS SELECT 1 AS x|", "tsql")).toBe("CREATE OR\nALTER VIEW v AS SELECT 1 AS x");
  });
  it("still: PL/pgSQL FOR … IN on one line and its query on the next", () => {
    const items = run("DO $$ DECLARE r record; BEGIN FOR r IN\nSELECT | FROM posts LOOP END LOOP; END $$", "pg");
    expect(has(items, "title"), labels(items)).toBe(true);
  });
});

describe("fourth round: M21", () => {
  it("mySql: DESC after an operand slot left open at a line's end starts a DESCRIBE", () => {
    expect(bounds("SELECT * FROM orders WHERE\nDESC cust|omers", "mySql")).toBe("DESC customers");
  });
  // PostgreSQL folds: a bare `Num` is num, a quoted one Num. (DuckDB matches names without case; Oracle has no such list.)
  it("pg: a table function's column list written in the statement is inserted as written", () => {
    expect(ins(run("SELECT g.| FROM generate_series(1, 3) AS g(Num)", "pg"), "Num")).toBe("Num");
    expect(ins(run('SELECT g.| FROM generate_series(1, 3) AS g("Num")', "pg"), "Num")).toBe('"Num"');
  });
  for (const d of ["pg", "duckDb"] as const) {
    it(`${d}: a select-list label that is a keyword does not end the select list`, () => {
      const order = run("SELECT total AS order, customer_id FROM orders ORDER BY |", d);
      expect(has(order, "order"), labels(order)).toBe(true);
      const group = run("SELECT status AS order, customer_id, count(*) FROM orders GROUP BY |", d);
      expect(ins(group, "(all non-aggregated columns)"), labels(group)).toBe("status, customer_id");
    });
  }
});

describe("fourth round: M22", () => {
  it("pg: a sealed CTE's UPDATE is not matched to an outer FROM alias of the same name", () => {
    const q = run("WITH u AS (UPDATE staging SET payload = 'x' RETURNING sid) INSERT INTO orders (order_id) SELECT 1 FROM customers staging ON CONFLICT (order_id) DO UPDATE SET |", "pg");
    expect(has(q, "status"), labels(q)).toBe(true);
    expect(has(q, "email"), labels(q)).toBe(false);
  });
  it("still: T-SQL UPDATE alias SET … FROM table alias writes that table", () => {
    const q = run("UPDATE o SET | FROM orders o JOIN customers c ON c.customer_id = o.customer_id", "tsql");
    expect(has(q, "status"), labels(q)).toBe(true);
  });
  for (const d of ENGINES) {
    it(`${d}: GROUP BY lists a bare non-aggregated column once, as the qualified column it is`, () => {
      const items = run("SELECT status, count(*) FROM orders o GROUP BY |", d);
      const status = items.filter((i) => i.label.toLowerCase().endsWith("status"));
      expect(status.map((i) => i.label), labels(items)).toEqual([`o.${N(d, "status")}`]);
    });
  }
  it("still: a bare name two tables have keeps its own item", () => {
    const items = run("SELECT customer_id, count(*) FROM orders o JOIN customers c ON 1 = 1 GROUP BY |", "pg");
    expect(has(items, "customer_id"), labels(items)).toBe(true);
  });
});

describe("fourth round: L10 — Oracle's unreserved JOIN after a FROM reference", () => {
  it("oracle: the reference still gets its alias, as on engines that reserve JOIN", () => {
    for (const d of ["oracle", "tsql"] as const) {
      const got = ins(run("SELECT * FROM ord| JOIN customers c ON 1 = 1", d), N(d, "orders"));
      expect(got?.includes(" "), `${d}: ${got}`).toBe(true);
    }
  });
});

describe("fourth round: H12 — Run current statement after OR at a line's end", () => {
  // Oracle's statements follow SQL*Plus (oracleStatements.ts), not this splitter.
  for (const d of ["tsql", "pg", "mySql", "sqlite", "duckDb"] as const) {
    it(`${d}: the next line's UPDATE is a statement of its own`, () => {
      expect(bounds("SELECT * FROM orders WHERE total > 1 OR\nUPDATE posts SET title = 'x' WHERE id = 1|", d)).toBe("UPDATE posts SET title = 'x' WHERE id = 1");
    });
  }
});

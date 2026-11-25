// Semantic spans (App Themes §5.3) on all six engines: CTE names and schema prefixes from the tier-1 analyzer. Each case
// marks the expected spans in the SQL (‹cte›, «schema»), and the result must be exactly those: anything unmarked —
// aliases, columns, strings, comments — must stay uncoloured.
import { describe, expect, it } from "vitest";
import type { Dialect, SchemaSnapshot } from "../../../ipc/contract";
import { buildSchemaIndex } from "../schemaIndex";
import { SEMANTIC_MAX_STATEMENTS, semanticSpans } from "../scanner";
import type { SchemaIndex } from "../types";

const ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb", "oracle"] as const satisfies readonly Dialect[];
type Engine = (typeof ENGINES)[number];

/** Each engine's schema as its catalog spells it, its database, and how it quotes a name. */
const S: Record<Engine, string> = { tsql: "dbo", pg: "sales", mySql: "shop", sqlite: "main", duckDb: "main", oracle: "HR" };
const DB: Record<Engine, string> = { tsql: "SalesDB", pg: "app", mySql: "shop", sqlite: "main", duckDb: "app", oracle: "FREEPDB1" };
const Q: Record<Engine, (n: string) => string> = {
  tsql: (n) => `[${n}]`, pg: (n) => `"${n}"`, mySql: (n) => `\`${n}\``, sqlite: (n) => `"${n}"`, duckDb: (n) => `"${n}"`, oracle: (n) => `"${n}"`,
};
const DUAL = (d: Engine) => (d === "oracle" ? " FROM dual" : "");

function catalog(d: Engine): SchemaIndex {
  const snap: SchemaSnapshot = {
    profileId: "p", database: DB[d], status: "fresh", fks: [],
    objects: [
      { id: 1, schema: S[d], name: d === "oracle" ? "ORDERS" : "orders", kind: "table", columns: [] },
      { id: 2, schema: S[d], name: d === "oracle" ? "F" : "f", kind: "func" },
    ],
  };
  return buildSchemaIndex(snap, d, [DB[d]]);
}

/** Strips the ‹cte› / «schema» marks: the text, and the spans they name as `text@offset:kind`. */
function parse(marked: string): { text: string; want: string[] } {
  let text = "";
  const want: string[] = [];
  for (let i = 0; i < marked.length; i++) {
    const ch = marked[i];
    if (ch === "‹" || ch === "«") {
      const close = marked.indexOf(ch === "‹" ? "›" : "»", i + 1);
      const name = marked.slice(i + 1, close);
      want.push(`${name}@${text.length}:${ch === "‹" ? "cte" : "schema"}`);
      text += name;
      i = close;
    } else text += ch;
  }
  return { text, want };
}

function check(marked: string, d: Engine, schema?: SchemaIndex | null) {
  const { text, want } = parse(marked);
  const got = semanticSpans(text, d, 0, text.length, schema);
  expect(got.map((s) => `${text.slice(s.start, s.end)}@${s.start}:${s.kind}`)).toEqual(want);
  for (const s of got) expect(text.slice(s.start, s.end)).not.toMatch(/[\r\n]/);
}

/** Cases every engine shares, written in its own spelling. */
function common(d: Engine): [string, string, boolean?][] {
  const s = S[d], q = Q[d], dual = DUAL(d);
  const rec = d === "tsql" || d === "oracle" ? "" : " RECURSIVE";
  return [
    ["CTE definition, references and an unaliased qualifier; aliases and columns stay uncoloured",
      `WITH ‹recent› AS (SELECT o.id, o.total FROM «${s}».orders o) SELECT ‹recent›.id, r.total FROM ‹recent› JOIN ‹recent› r ON r.id = ‹recent›.id`],
    ["a recursive CTE refers to itself",
      `WITH${rec} ‹n› (k) AS (SELECT 1 AS k${dual} UNION ALL SELECT ‹n›.k + 1 FROM ‹n› WHERE ‹n›.k < 5) SELECT k FROM ‹n›`],
    ["a schema-qualified name is never the CTE it shadows",
      `WITH ‹orders› AS (SELECT 1 AS id${dual}) SELECT o.id FROM «${s}».orders o`],
    ["an aliased CTE's qualifier is its alias, not the CTE",
      `WITH ‹c› AS (SELECT 1 AS id${dual}) SELECT c.id, x.id FROM ‹c› x, «${s}».c WHERE x.id = c.id`],
    ["FROM and JOIN",
      `SELECT a.id, b.v FROM «${s}».t a JOIN «${s}».u b ON b.id = a.id LEFT JOIN «${s}».w c ON c.id = a.id`],
    ["UPDATE, INSERT INTO and DELETE FROM",
      `UPDATE «${s}».t SET v = 1 WHERE id = 2;\nINSERT INTO «${s}».t (id, v) VALUES (1, 2);\nDELETE FROM «${s}».t WHERE id = 1;`],
    ["quoted names; nothing inside strings or comments",
      `SELECT '${s}.t' AS x, ${q("t")}.v FROM «${q(s)}».${q("t")} -- FROM ${s}.u\n/* JOIN ${s}.v ON 1 = 1 */ WHERE 1 = 1`],
    ["a name written across lines gives one span per line",
      `SELECT *\nFROM «${s}»\n  .t\nJOIN «${s}».\n  u ON 1 = 1`],
    ["`a.b(` calls only when the catalog has a: with no catalog",
      `SELECT ${s}.f(1), x.f(2)${dual}`, false],
    ["`a.b(` calls only when the catalog has a: with the catalog",
      `SELECT «${s}».f(1), x.f(2)${dual}`, true],
    ["a function called in FROM is not the CTE of its name",
      `WITH ‹f› AS (SELECT 1 AS x${dual}) SELECT f.x FROM f(1)`],
  ];
}

/** Each engine's own statements: its DDL, MERGE, TRUNCATE, EXEC / CALL and quirks. */
const OWN: Record<Engine, [string, string, boolean?][]> = {
  tsql: [
    ["MERGE INTO and USING", "MERGE INTO «dbo».t AS tg USING «dbo».s AS src ON tg.id = src.id WHEN MATCHED THEN UPDATE SET v = src.v;"],
    ["DDL headers, an index's and trigger's table, REFERENCES and TRUNCATE TABLE",
      "CREATE TABLE «dbo».t (id int REFERENCES «dbo».u (id));\nALTER TABLE «dbo».t ADD v int;\nDROP VIEW IF EXISTS «dbo».v;\n" +
      "CREATE INDEX ix ON «dbo».t (id);\nCREATE TRIGGER «dbo».trg ON «dbo».t AFTER INSERT AS SELECT 1;\nTRUNCATE TABLE «dbo».t;\n" +
      "CREATE OR ALTER PROCEDURE «dbo».p @a int AS SELECT @a;"],
    ["EXEC targets; EXECUTE AS and dynamic SQL have none",
      "EXEC «dbo».p @a = 1;\nEXEC @rc = «SalesDB».«dbo».p;\nEXECUTE AS USER = 'x';\nEXEC ('SELECT 1 FROM dbo.t');\nINSERT INTO #t EXEC «dbo».q;"],
    ["an EXEC after a statement left unfinished at a dot (no semicolon) keeps its schema", "SELECT a.\nEXEC «dbo».p"],
    ["database-qualified names", "SELECT * FROM «SalesDB»..t JOIN «Archive».«dbo».u ON 1 = 1"],
    ["APPLY: a schema's function only with the catalog, a column's method never (no catalog)",
      "SELECT * FROM «dbo».t a CROSS APPLY dbo.f(a.id) x OUTER APPLY a.Items.nodes('/x') n(c)", false],
    ["APPLY: a schema's function only with the catalog, a column's method never (with the catalog)",
      "SELECT * FROM «dbo».t a CROSS APPLY «dbo».f(a.id) x OUTER APPLY a.Items.nodes('/x') n(c)", true],
  ],
  pg: [
    ["MERGE, TRUNCATE (a list) and CALL",
      "MERGE INTO «sales».t AS tg USING «sales».s AS src ON tg.id = src.id WHEN MATCHED THEN UPDATE SET v = src.v;\n" +
      "TRUNCATE TABLE «sales».t, «sales».u;\nCALL «sales».p(1);"],
    ["DDL headers, an index's and trigger's table, REFERENCES, EXECUTE FUNCTION",
      "CREATE TABLE «sales».t (id int REFERENCES «sales».u (id));\nCREATE OR REPLACE VIEW «sales».v AS SELECT 1;\n" +
      "DROP TABLE IF EXISTS «sales».a, «sales».b;\nCREATE INDEX ix ON «sales».t (id);\n" +
      "CREATE TRIGGER trg AFTER INSERT ON «sales».t FOR EACH ROW EXECUTE FUNCTION «sales».f();"],
    ["$$ routine and DO bodies are analysed as scripts",
      "CREATE FUNCTION «sales».f() RETURNS int AS $$\n  WITH ‹c› AS (SELECT 1 AS n) SELECT n FROM ‹c›;\n$$ LANGUAGE sql;\n" +
      "DO $$ BEGIN PERFORM 1 FROM «sales».t; END $$;\nSELECT 'FROM sales.t' AS s, $$ FROM sales.u $$ AS d;"],
    ["IS DISTINCT FROM is an expression, not a FROM clause", "SELECT a IS DISTINCT FROM b.c FROM «sales».t b"],
    ["the catalog's folding: an unquoted schema folds, a quoted one is exact", "SELECT «Sales».f(1), \"Sales\".f(2)", true],
  ],
  mySql: [
    ["CALL, TRUNCATE TABLE and DDL",
      "CALL «shop».p();\nTRUNCATE TABLE «shop».t;\nCREATE TABLE «shop».t (id int REFERENCES «shop».u (id));\n" +
      "DROP TABLE IF EXISTS «shop».a, «shop».b;\nCREATE DEFINER = CURRENT_USER PROCEDURE «shop».q() SELECT 1;"],
    ["backtick names", "SELECT * FROM «`shop`».`t` JOIN «shop».`u` ON 1 = 1"],
    ["\"…\" is a string here", "SELECT \"shop.t\" FROM «shop».t"],
  ],
  sqlite: [
    ["DDL with a schema and an attached database",
      "CREATE TABLE «main».t (id INTEGER);\nCREATE INDEX «main».ix ON t (id);\nDROP TABLE IF EXISTS «aux».t;\n" +
      "CREATE TRIGGER «aux».trg AFTER INSERT ON t BEGIN SELECT 1; END;"],
    ["bracket and backtick names", "SELECT * FROM «[main]».[t] JOIN «`aux`».`u` ON 1 = 1"],
    ["INSERT OR REPLACE and REPLACE INTO", "INSERT OR REPLACE INTO «main».t (id) VALUES (1);\nREPLACE INTO «main».t (id) VALUES (2);"],
  ],
  duckDb: [
    ["FROM-first, three-part names, TRUNCATE, CALL and DDL",
      "FROM «app».«main».t SELECT *;\nTRUNCATE TABLE «main».t;\nCALL «main».f();\nCREATE OR REPLACE VIEW «main».v AS SELECT 1;\n" +
      "CREATE MACRO «main».m(a) AS a + 1;"],
    ["$$ stays a string", "SELECT $$ FROM main.t $$ AS s FROM «main».u"],
  ],
  oracle: [
    ["MERGE INTO, TRUNCATE TABLE and DDL",
      "MERGE INTO «HR».t tg USING «HR».s src ON (tg.id = src.id) WHEN MATCHED THEN UPDATE SET tg.v = src.v;\n" +
      "TRUNCATE TABLE «HR».t;\nCREATE TABLE «HR».t (id NUMBER REFERENCES «HR».u (id));\n" +
      "CREATE OR REPLACE VIEW «HR».v AS SELECT 1 AS x FROM dual;\nDROP TABLE «HR».t;"],
    ["CALL / EXEC: the schema of s.pkg.p; a two-part target (schema or package?) never without the catalog",
      "CALL «HR».pkg.p();\nCALL HR.p();\nEXEC «HR».pkg.p;\nBEGIN pkg.p(1); END;", false],
    ["CALL / EXEC: a two-part target only when the catalog has its first part",
      "CALL «HR».p();\nCALL pkg.p();\nEXEC «HR».p;\nBEGIN «HR».pkg.p(1); pkg.p(2); END;", true],
    ["name@link is never coloured", "SELECT e.id FROM HR.emp@remote e JOIN «HR».dept d ON d.id = e.dept_id"],
    ["a CTE named like the part before @link does not color it", "WITH ‹hr› AS (SELECT 1 AS x FROM dual) SELECT hr.pkg@remote(1) FROM ‹hr›"],
    ["SELECT … INTO a record field is not a schema", "SELECT x INTO r.f FROM «HR».t"],
  ],
};

describe("semantic spans (App Themes §5.3)", () => {
  for (const d of ENGINES) {
    describe(d, () => {
      for (const [name, sql, withCatalog] of [...common(d), ...OWN[d]]) {
        it(name, () => check(sql, d, withCatalog ? catalog(d) : null));
      }

      it("only the statements overlapping the range, and only the spans inside it", () => {
        const text = `SELECT * FROM ${S[d]}.a;\nSELECT * FROM ${S[d]}.b x;\nSELECT * FROM ${S[d]}.c;`;
        const from = text.indexOf("\n") + 1, to = text.lastIndexOf("\n");
        const got = semanticSpans(text, d, from, to);
        expect(got).toEqual([{ start: text.indexOf(`${S[d]}.b`), end: text.indexOf(`${S[d]}.b`) + S[d].length, kind: "schema" }]);
        expect(semanticSpans(text, d, to, from)).toEqual(got);
      });

      it(`analyses at most ${SEMANTIC_MAX_STATEMENTS} statements per call`, () => {
        const text = Array.from({ length: SEMANTIC_MAX_STATEMENTS + 50 }, (_, i) => `SELECT * FROM ${S[d]}.t${i};`).join("\n");
        const got = semanticSpans(text, d, 0, text.length);
        expect(got).toHaveLength(SEMANTIC_MAX_STATEMENTS);
        expect(text.slice(got[got.length - 1].start - 20, got[got.length - 1].end)).toContain(`\nSELECT * FROM ${S[d]}`);
        // A later window is its own call with its own budget.
        const late = text.indexOf(`.t${SEMANTIC_MAX_STATEMENTS + 10};`);
        expect(semanticSpans(text, d, late - 20, late + 5)).toHaveLength(1);
      });

      it("never throws on broken or partial SQL", () => {
        for (const sql of ["", "WITH", "WITH x AS (", "SELECT * FROM ", `SELECT * FROM ${S[d]}.`, "CREATE TABLE", "EXEC", "CALL x.", "'unterminated", "/* open"]) {
          expect(() => semanticSpans(sql, d, 0, sql.length)).not.toThrow();
        }
        expect(semanticSpans(undefined as never, d, 0, 10)).toEqual([]);
        expect(semanticSpans("SELECT 1", d, Number.NaN, Number.POSITIVE_INFINITY)).toEqual([]);
      });
    });
  }
});

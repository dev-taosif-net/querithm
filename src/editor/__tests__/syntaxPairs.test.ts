import { describe, it, expect } from "vitest";
import { findInsertMatchingHighlight, findSyntaxPairHighlight } from "../syntaxPairs";
import type { Dialect } from "../../ipc/contract";

const ALL_DIALECTS: readonly Dialect[] = ["tsql", "pg", "sqlite", "mySql", "duckDb", "oracle"];

function sliceMatch(sql: string, offset: number, dialect: Dialect) {
  const m = findInsertMatchingHighlight(sql, offset, dialect);
  if (!m) return null;
  return {
    col: sql.slice(m.col.start, m.col.end),
    val: sql.slice(m.val.start, m.val.end),
    vals: m.vals.map((v) => sql.slice(v.start, v.end)),
  };
}

describe("findInsertMatchingHighlight", () => {
  const sql = "INSERT INTO dbo.Orders (OrderId, CustomerId, TotalAmount) VALUES (101, 202, 59.99);";

  it("highlights corresponding value when cursor is on a column", () => {
    const custIdOffset = sql.indexOf("CustomerId");
    const match = findInsertMatchingHighlight(sql, custIdOffset, "tsql");
    expect(match).not.toBeNull();
    expect(sql.slice(match!.col.start, match!.col.end)).toBe("CustomerId");
    expect(sql.slice(match!.val.start, match!.val.end)).toBe("202");
    expect(match!.vals).toHaveLength(1);
  });

  it("highlights corresponding column when cursor is on a value", () => {
    const valOffset = sql.indexOf("59.99");
    const match = findInsertMatchingHighlight(sql, valOffset, "tsql");
    expect(match).not.toBeNull();
    expect(sql.slice(match!.col.start, match!.col.end)).toBe("TotalAmount");
    expect(sql.slice(match!.val.start, match!.val.end)).toBe("59.99");
  });

  it("handles complex value expressions with commas inside parentheses", () => {
    const complexSql = "INSERT INTO dbo.Orders (OrderId, TotalAmount) VALUES (101, COALESCE(NULL, 10.0));";
    const totalOffset = complexSql.indexOf("TotalAmount");
    const match = findInsertMatchingHighlight(complexSql, totalOffset, "tsql");
    expect(match).not.toBeNull();
    expect(complexSql.slice(match!.col.start, match!.col.end)).toBe("TotalAmount");
    expect(complexSql.slice(match!.val.start, match!.val.end)).toBe("COALESCE(NULL, 10.0)");
  });

  it("handles the user's exact VALUES template with inline comments and partial SELECT 'asd'", () => {
    const templateSql = `INSERT INTO auth.Menus ([strHashCode], [intMenuLabelId], [intParentMenuId], [strMenuName], [isHasApproval])
VALUES (/* strHashCode (nvarchar(500)) */ <value>, /* intMenuLabelId (bigint) */ <value>, /* intParentMenuId (bigint) */ <value>, /* strMenuName (nvarchar(250)) */ <value>, /* isHasApproval (bit) */ <value>);`;
    const m1 = sliceMatch(templateSql, templateSql.indexOf("[strHashCode]"), "tsql");
    expect(m1).toEqual({ col: "[strHashCode]", val: "<value>", vals: ["<value>"] });

    const secondPlaceholder = templateSql.indexOf("<value>", templateSql.indexOf("<value>") + 1);
    const m2 = sliceMatch(templateSql, secondPlaceholder, "tsql");
    expect(m2).toEqual({ col: "[intMenuLabelId]", val: "<value>", vals: ["<value>"] });

    const partialSelect = `INSERT INTO auth.Menus ([strHashCode], [intMenuLabelId], [intParentMenuId], [strMenuName], [strMenuNameWeb], [strMenuNameApps], [strTo], [strToForApps], [strIcon], [strIconForApps], [intMenuSerial], [intMenuSerialForApps], [isExpandable], [isForCommon], [isMenuForView], [isActive], [isForApps], [isForWeb], [isHasApproval])
SELECT 'asd'`;
    expect(sliceMatch(partialSelect, partialSelect.indexOf("[strHashCode]"), "tsql")).toEqual({
      col: "[strHashCode]",
      val: "'asd'",
      vals: ["'asd'"],
    });
    expect(sliceMatch(partialSelect, partialSelect.indexOf("'asd'"), "tsql")).toEqual({
      col: "[strHashCode]",
      val: "'asd'",
      vals: ["'asd'"],
    });
    // Column 2 has no value typed yet
    expect(findInsertMatchingHighlight(partialSelect, partialSelect.indexOf("[intMenuLabelId]"), "tsql")).toBeNull();
  });

  it.each(ALL_DIALECTS)("supports INSERT ... VALUES, multi-row VALUES, and INSERT ... SELECT across all dialects (%s)", (dialect) => {
    const multiVal = "INSERT INTO items (id, name, price) VALUES (1, 'a', 10), (2, 'b', 20), (3, 'c', 30);";
    expect(sliceMatch(multiVal, multiVal.indexOf("name"), dialect)).toEqual({
      col: "name",
      val: "'a'",
      vals: ["'a'", "'b'", "'c'"],
    });
    expect(sliceMatch(multiVal, multiVal.indexOf("'b'"), dialect)).toEqual({
      col: "name",
      val: "'b'",
      vals: ["'b'"],
    });
    expect(sliceMatch(multiVal, multiVal.indexOf("30"), dialect)).toEqual({
      col: "price",
      val: "30",
      vals: ["30"],
    });

    const insSel = "INSERT INTO items (id, name, price) SELECT s.id, UPPER(s.name), s.price * 2 AS p FROM staging s WHERE s.ok = 1;";
    expect(sliceMatch(insSel, insSel.indexOf("name"), dialect)).toEqual({
      col: "name",
      val: "UPPER(s.name)",
      vals: ["UPPER(s.name)"],
    });
    expect(sliceMatch(insSel, insSel.indexOf("s.price"), dialect)).toEqual({
      col: "price",
      val: "s.price * 2 AS p",
      vals: ["s.price * 2 AS p"],
    });
    // Cursor in FROM / WHERE must not highlight
    expect(findInsertMatchingHighlight(insSel, insSel.indexOf("staging"), dialect)).toBeNull();
    expect(findInsertMatchingHighlight(insSel, insSel.indexOf("s.ok"), dialect)).toBeNull();
  });

  it.each(ALL_DIALECTS)("supports partial/in-progress typing, empty slots, and unclosed VALUES parens (%s)", (dialect) => {
    const unclosed = "INSERT INTO t (a, b, c) VALUES (1, 'two'";
    expect(sliceMatch(unclosed, unclosed.indexOf("'two'"), dialect)).toEqual({
      col: "b",
      val: "'two'",
      vals: ["'two'"],
    });
    expect(findInsertMatchingHighlight(unclosed, unclosed.indexOf("c)"), dialect)).toBeNull();

    const emptySlot = "INSERT INTO t (a, b, c) VALUES (1, , 3);";
    expect(sliceMatch(emptySlot, emptySlot.indexOf("3"), dialect)).toEqual({
      col: "c",
      val: "3",
      vals: ["3"],
    });
    expect(findInsertMatchingHighlight(emptySlot, emptySlot.indexOf("b,"), dialect)).toBeNull();

    const partialSel = "INSERT INTO t (a, b, c) SELECT 1, ";
    expect(sliceMatch(partialSel, partialSel.indexOf("a,"), dialect)).toEqual({
      col: "a",
      val: "1",
      vals: ["1"],
    });
  });

  it.each(ALL_DIALECTS)("supports compound SELECT with UNION ALL / INTERSECT / EXCEPT and parenthesized queries (%s)", (dialect) => {
    const compound = `INSERT INTO t (a, b)
SELECT 1, 'one' FROM s1 WHERE x = 1
UNION ALL
SELECT 2, 'two' FROM s2
INTERSECT
SELECT 3, 'three' FROM s3;`;
    expect(sliceMatch(compound, compound.indexOf("b)"), dialect)).toEqual({
      col: "b",
      val: "'one'",
      vals: ["'one'", "'two'", "'three'"],
    });
    expect(sliceMatch(compound, compound.indexOf("'two'"), dialect)).toEqual({
      col: "b",
      val: "'two'",
      vals: ["'two'"],
    });
    expect(sliceMatch(compound, compound.indexOf("3"), dialect)).toEqual({
      col: "a",
      val: "3",
      vals: ["3"],
    });

    const parenthesized = "INSERT INTO t (a, b) (SELECT 10, 'x' FROM s1) UNION ALL (SELECT 20, 'y' FROM s2);";
    expect(sliceMatch(parenthesized, parenthesized.indexOf("a,"), dialect)).toEqual({
      col: "a",
      val: "10",
      vals: ["10", "20"],
    });
    expect(sliceMatch(parenthesized, parenthesized.indexOf("'y'"), dialect)).toEqual({
      col: "b",
      val: "'y'",
      vals: ["'y'"],
    });
  });

  it("supports T-SQL TOP, table hints, OUTPUT, optional INTO, @tableVar, #temp, MERGE, and semicolon-less scripts", () => {
    const tsqlComplex = `INSERT TOP (10) PERCENT dbo.Target WITH (TABLOCK) (id, label, amt)
OUTPUT inserted.id INTO @log (id)
SELECT DISTINCT TOP (5) PERCENT WITH TIES
  s.id,
  CASE WHEN s.flag = 1 THEN 'yes' ELSE 'no' END,
  (SELECT MAX(x) FROM dbo.Other)
FROM dbo.Src s WITH (NOLOCK)
WHERE s.active = 1
UNION ALL
SELECT 99, 'fallback', 0`;
    expect(sliceMatch(tsqlComplex, tsqlComplex.indexOf("label"), "tsql")).toEqual({
      col: "label",
      val: "CASE WHEN s.flag = 1 THEN 'yes' ELSE 'no' END",
      vals: ["CASE WHEN s.flag = 1 THEN 'yes' ELSE 'no' END", "'fallback'"],
    });
    expect(sliceMatch(tsqlComplex, tsqlComplex.indexOf("MAX(x)"), "tsql")).toEqual({
      col: "amt",
      val: "(SELECT MAX(x) FROM dbo.Other)",
      vals: ["(SELECT MAX(x) FROM dbo.Other)"],
    });
    // Cursor inside OUTPUT must not match
    expect(findInsertMatchingHighlight(tsqlComplex, tsqlComplex.indexOf("inserted.id"), "tsql")).toBeNull();
    expect(findInsertMatchingHighlight(tsqlComplex, tsqlComplex.indexOf("@log"), "tsql")).toBeNull();

    // Legacy T-SQL bare hint paren `(HOLDLOCK)` before column list
    const bareHint = "INSERT INTO dbo.T (HOLDLOCK) (a, b) VALUES (1, 2);";
    expect(sliceMatch(bareHint, bareHint.indexOf("b)"), "tsql")).toEqual({ col: "b", val: "2", vals: ["2"] });

    // Table variable & temp table & db..tbl
    const tv = "INSERT @buf (k, v) SELECT 1, 'v';";
    expect(sliceMatch(tv, tv.indexOf("'v'"), "tsql")).toEqual({ col: "v", val: "'v'", vals: ["'v'"] });
    const dbdot = "INSERT mydb..#tmp (k, v) VALUES (7, 8);";
    expect(sliceMatch(dbdot, dbdot.indexOf("8"), "tsql")).toEqual({ col: "v", val: "8", vals: ["8"] });

    // Semicolon-less SELECT after INSERT ... SELECT must not bleed
    const noSemi = "INSERT INTO dbo.T (a, b) SELECT 1, 2\nSELECT 3, 4 FROM dbo.Other";
    expect(sliceMatch(noSemi, noSemi.indexOf("2"), "tsql")).toEqual({ col: "b", val: "2", vals: ["2"] });
    expect(findInsertMatchingHighlight(noSemi, noSemi.indexOf("3"), "tsql")).toBeNull();

    // MERGE with multiple WHEN NOT MATCHED branches
    const mergeSql = `MERGE INTO dbo.Target AS t
USING dbo.Source AS s ON t.Id = s.Id
WHEN NOT MATCHED AND s.Kind = 1 THEN
  INSERT (Id, Name) VALUES (s.Id, s.Name)
WHEN NOT MATCHED THEN
  INSERT (Id, Name, Note) VALUES (s.Id, s.Name, 'default');`;
    expect(sliceMatch(mergeSql, mergeSql.indexOf("'default'"), "tsql")).toEqual({
      col: "Note",
      val: "'default'",
      vals: ["'default'"],
    });
    expect(sliceMatch(mergeSql, mergeSql.indexOf("s.Name)"), "tsql")).toEqual({
      col: "Name",
      val: "s.Name",
      vals: ["s.Name"],
    });
  });

  it("supports PostgreSQL AS alias, OVERRIDING, DISTINCT ON, ARRAY[...], ON CONFLICT, RETURNING, inline CTE, and writable CTE isolation", () => {
    const pgSql = `INSERT INTO public.events AS e (id, tags, payload)
OVERRIDING SYSTEM VALUE
SELECT DISTINCT ON (s.id) s.id, ARRAY[s.a, s.b]::text[], s.payload
FROM staging s
ON CONFLICT (id) DO UPDATE SET payload = EXCLUDED.payload
RETURNING id;`;
    expect(sliceMatch(pgSql, pgSql.indexOf("tags"), "pg")).toEqual({
      col: "tags",
      val: "ARRAY[s.a, s.b]::text[]",
      vals: ["ARRAY[s.a, s.b]::text[]"],
    });
    expect(sliceMatch(pgSql, pgSql.indexOf("s.b"), "pg")).toEqual({
      col: "tags",
      val: "ARRAY[s.a, s.b]::text[]",
      vals: ["ARRAY[s.a, s.b]::text[]"],
    });
    expect(findInsertMatchingHighlight(pgSql, pgSql.indexOf("EXCLUDED"), "pg")).toBeNull();
    expect(findInsertMatchingHighlight(pgSql, pgSql.lastIndexOf("id"), "pg")).toBeNull();

    // Inline WITH CTE between column list and SELECT
    const inlineCte = `INSERT INTO public.t (a, b)
WITH cte (x, y) AS MATERIALIZED (SELECT 1, 2)
SELECT x, y FROM cte;`;
    expect(sliceMatch(inlineCte, inlineCte.indexOf("y FROM"), "pg")).toEqual({
      col: "b",
      val: "y",
      vals: ["y"],
    });
    // Cursor inside CTE body should not match outer INSERT columns
    expect(findInsertMatchingHighlight(inlineCte, inlineCte.indexOf("1, 2"), "pg")).toBeNull();

    // Writable CTE: outer SELECT must NOT leak into the CTE's INSERT
    const writableCte = `WITH moved AS (
  INSERT INTO archive (a, b) VALUES (1, 2) RETURNING a, b
)
SELECT a, b FROM moved;`;
    expect(sliceMatch(writableCte, writableCte.indexOf("2)"), "pg")).toEqual({ col: "b", val: "2", vals: ["2"] });
    expect(findInsertMatchingHighlight(writableCte, writableCte.lastIndexOf("a, b"), "pg")).toBeNull();

    // Writable CTE feeding another INSERT
    const chainedCte = `WITH moved AS (
  INSERT INTO archive (a, b) VALUES (1, 2) RETURNING a, b
)
INSERT INTO audit (x, y) SELECT a, b FROM moved;`;
    expect(sliceMatch(chainedCte, chainedCte.indexOf("2)"), "pg")).toEqual({ col: "b", val: "2", vals: ["2"] });
    expect(sliceMatch(chainedCte, chainedCte.lastIndexOf("b FROM"), "pg")).toEqual({ col: "y", val: "b", vals: ["b"] });

    // Keyword-named table (`comment` is in PG_RESERVED)
    const kwTable = "INSERT INTO hr.comment (id, body) VALUES (1, 'hello');";
    expect(sliceMatch(kwTable, kwTable.indexOf("'hello'"), "pg")).toEqual({ col: "body", val: "'hello'", vals: ["'hello'"] });

    // PG MERGE with OVERRIDING SYSTEM VALUE
    const pgMerge = "MERGE INTO t USING s ON t.id = s.id WHEN NOT MATCHED THEN INSERT (id, v) OVERRIDING SYSTEM VALUE VALUES (s.id, s.v);";
    expect(sliceMatch(pgMerge, pgMerge.indexOf("s.v"), "pg")).toEqual({ col: "v", val: "s.v", vals: ["s.v"] });
  });

  it("supports MySQL / MariaDB VALUE, VALUES ROW(), IGNORE/modifiers, PARTITION, REPLACE, INSERT/REPLACE SET, and function guards", () => {
    const valSingular = "INSERT LOW_PRIORITY IGNORE INTO orders PARTITION (p0, p1) (`id`, `amt`) VALUE (1, 25), (2, 50);";
    expect(sliceMatch(valSingular, valSingular.indexOf("`amt`"), "mySql")).toEqual({
      col: "`amt`",
      val: "25",
      vals: ["25", "50"],
    });

    const rowCtor = "INSERT INTO t (a, b) VALUES ROW(1, 'x'), ROW(2, 'y');";
    expect(sliceMatch(rowCtor, rowCtor.indexOf("'y'"), "mySql")).toEqual({ col: "b", val: "'y'", vals: ["'y'"] });

    const replaceSel = "REPLACE DELAYED INTO t (a, b) SELECT DISTINCTROW HIGH_PRIORITY x, y FROM src ON DUPLICATE KEY UPDATE a = 1;";
    expect(sliceMatch(replaceSel, replaceSel.indexOf("y FROM"), "mySql")).toEqual({ col: "b", val: "y", vals: ["y"] });
    expect(findInsertMatchingHighlight(replaceSel, replaceSel.indexOf("a = 1"), "mySql")).toBeNull();

    const insertSet = "INSERT INTO users SET first_name = 'Alice', full_name = CONCAT('A', 'B'), role = CASE WHEN 1=1 THEN 'admin' ELSE 'user' END ON DUPLICATE KEY UPDATE first_name = 'X';";
    expect(sliceMatch(insertSet, insertSet.indexOf("first_name"), "mySql")).toEqual({
      col: "first_name",
      val: "'Alice'",
      vals: ["'Alice'"],
    });
    expect(sliceMatch(insertSet, insertSet.indexOf("CONCAT"), "mySql")).toEqual({
      col: "full_name",
      val: "CONCAT('A', 'B')",
      vals: ["CONCAT('A', 'B')"],
    });
    expect(sliceMatch(insertSet, insertSet.indexOf("'admin'"), "mySql")).toEqual({
      col: "role",
      val: "CASE WHEN 1=1 THEN 'admin' ELSE 'user' END",
      vals: ["CASE WHEN 1=1 THEN 'admin' ELSE 'user' END"],
    });
    expect(findInsertMatchingHighlight(insertSet, insertSet.lastIndexOf("first_name"), "mySql")).toBeNull();

    const replaceSet = "REPLACE INTO cfg SET k = 'theme', v = 'dark';";
    expect(sliceMatch(replaceSet, replaceSet.indexOf("'dark'"), "mySql")).toEqual({ col: "v", val: "'dark'", vals: ["'dark'"] });

    // String function guards
    const fnInsert = "SELECT INSERT('Quadratic', 3, 4, 'What');";
    expect(findInsertMatchingHighlight(fnInsert, fnInsert.indexOf("'What'"), "mySql")).toBeNull();
    const fnReplace = "SELECT REPLACE('abc', 'a', 'z');";
    expect(findInsertMatchingHighlight(fnReplace, fnReplace.indexOf("'z'"), "mySql")).toBeNull();
  });

  it("supports SQLite INSERT OR conflict-action, REPLACE INTO, inline CTE, and REPLACE() guard", () => {
    const orReplace = "INSERT OR REPLACE INTO kv (k, v) VALUES ('a', 1), ('b', 2);";
    expect(sliceMatch(orReplace, orReplace.indexOf("2"), "sqlite")).toEqual({ col: "v", val: "2", vals: ["2"] });

    const repSel = "REPLACE INTO kv (k, v) WITH c AS (SELECT 'x' a, 9 b) SELECT a, b FROM c;";
    expect(sliceMatch(repSel, repSel.indexOf("b FROM"), "sqlite")).toEqual({ col: "v", val: "b", vals: ["b"] });

    const fnRep = "SELECT REPLACE('hello', 'h', 'j');";
    expect(findInsertMatchingHighlight(fnRep, fnRep.indexOf("'j'"), "sqlite")).toBeNull();
  });

  it("supports DuckDB OR IGNORE, BY POSITION, BY NAME guard, FROM-first, list/struct literals, and DISTINCT ON", () => {
    const duckVals = "INSERT OR IGNORE INTO docs (id, tags, meta) VALUES (1, [10, 20], {'a': 1, 'b': 2});";
    expect(sliceMatch(duckVals, duckVals.indexOf("20"), "duckDb")).toEqual({
      col: "tags",
      val: "[10, 20]",
      vals: ["[10, 20]"],
    });
    expect(sliceMatch(duckVals, duckVals.indexOf("'b'"), "duckDb")).toEqual({
      col: "meta",
      val: "{'a': 1, 'b': 2}",
      vals: ["{'a': 1, 'b': 2}"],
    });

    const fromFirst = "INSERT INTO dst (a, b) BY POSITION FROM src WHERE active = true SELECT x, y QUALIFY ROW_NUMBER() OVER () = 1;";
    expect(sliceMatch(fromFirst, fromFirst.indexOf("y QUALIFY"), "duckDb")).toEqual({
      col: "b",
      val: "y",
      vals: ["y"],
    });
    expect(findInsertMatchingHighlight(fromFirst, fromFirst.indexOf("active"), "duckDb")).toBeNull();

    const byName = "INSERT INTO dst BY NAME SELECT 1 AS a, 2 AS b;";
    expect(findInsertMatchingHighlight(byName, byName.indexOf("1"), "duckDb")).toBeNull();

    // UNION BY NAME stops positional collection before the BY NAME branch
    const unionByName = "INSERT INTO dst (a, b) SELECT 1, 2 UNION BY NAME SELECT 3 AS b, 4 AS a;";
    expect(sliceMatch(unionByName, unionByName.indexOf("a,"), "duckDb")).toEqual({ col: "a", val: "1", vals: ["1"] });
    expect(findInsertMatchingHighlight(unionByName, unionByName.indexOf("3"), "duckDb")).toBeNull();
  });

  it("supports Oracle hints, PARTITION/SUBPARTITION, @dblink, UNIQUE, MINUS, INSERT ALL/FIRST, LOG ERRORS, inline view guard, and / terminator", () => {
    const oraSel = `INSERT /*+ APPEND */ INTO hr.emp@rem_db PARTITION (p_2026) e (id, score)
SELECT UNIQUE s.id, LOG(10, s.val) FROM stg s
MINUS
SELECT x.id, x.score FROM excl x
LOG ERRORS INTO err_log REJECT LIMIT UNLIMITED;`;
    expect(sliceMatch(oraSel, oraSel.indexOf("score"), "oracle")).toEqual({
      col: "score",
      val: "LOG(10, s.val)",
      vals: ["LOG(10, s.val)", "x.score"],
    });
    expect(sliceMatch(oraSel, oraSel.indexOf("x.score"), "oracle")).toEqual({
      col: "score",
      val: "x.score",
      vals: ["x.score"],
    });

    // Multi-table INSERT ALL
    const insAll = `INSERT ALL
  INTO t1 (a, b) VALUES (x, y)
  INTO t2 (c, d) VALUES (z, w)
SELECT x, y, z, w FROM dual;`;
    expect(sliceMatch(insAll, insAll.indexOf("b)"), "oracle")).toEqual({ col: "b", val: "y", vals: ["y"] });
    expect(sliceMatch(insAll, insAll.indexOf("z,"), "oracle")).toEqual({ col: "c", val: "z", vals: ["z"] });
    // Trailing SELECT does not falsely pair when all INTO branches have VALUES
    expect(findInsertMatchingHighlight(insAll, insAll.lastIndexOf("w FROM"), "oracle")).toBeNull();

    // Conditional INSERT FIRST with one VALUES branch and one trailing-SELECT branch
    const insFirst = `INSERT FIRST
  WHEN x > 10 AND EXISTS (SELECT 1 FROM ref) THEN
    INTO t1 (a, b) VALUES (x, y)
  ELSE
    INTO t2 (c, d)
SELECT x, y FROM src;`;
    expect(sliceMatch(insFirst, insFirst.indexOf("y)"), "oracle")).toEqual({ col: "b", val: "y", vals: ["y"] });
    expect(sliceMatch(insFirst, insFirst.indexOf("d)"), "oracle")).toEqual({ col: "d", val: "y", vals: ["y"] });
    expect(sliceMatch(insFirst, insFirst.lastIndexOf("x, y"), "oracle")).toEqual({ col: "c", val: "x", vals: ["x"] });

    // Inline view target must not mistake `(SELECT ...)` for a column list
    const inlineView = "INSERT INTO (SELECT a, b FROM t) VALUES (1, 2);";
    expect(findInsertMatchingHighlight(inlineView, inlineView.indexOf("1"), "oracle")).toBeNull();

    // Standalone `/` terminates statement
    const slashSep = "INSERT INTO t (a) VALUES (1)\n/\nSELECT 2 FROM dual;";
    expect(findInsertMatchingHighlight(slashSep, slashSep.indexOf("2"), "oracle")).toBeNull();
  });

  it("handles wildcards, dotted keyword columns, trigger/DDL false positives, and large multi-row performance", () => {
    // Wildcard cutoff: items before `*` map; `*` and after return null
    const star = "INSERT INTO t (a, b, c) SELECT 1, s.*, 3 FROM s;";
    expect(sliceMatch(star, star.indexOf("a,"), "pg")).toEqual({ col: "a", val: "1", vals: ["1"] });
    expect(findInsertMatchingHighlight(star, star.indexOf("b,"), "pg")).toBeNull();
    expect(findInsertMatchingHighlight(star, star.indexOf("c)"), "pg")).toBeNull();

    // Multiplication `a * b` is NOT a wildcard
    const mul = "INSERT INTO t (x, y) SELECT a * b, c FROM s;";
    expect(sliceMatch(mul, mul.indexOf("y)"), "pg")).toEqual({ col: "y", val: "c", vals: ["c"] });

    // Dotted column named after a SQL keyword (`s.end`, `s.from`, `s.set`)
    const dottedKw = "INSERT INTO t (a, b, c) SELECT s.end, s.from, s.set FROM s WHERE s.end > 0 UNION ALL SELECT 1, 2, 3;";
    expect(sliceMatch(dottedKw, dottedKw.indexOf("s.from"), "pg")).toEqual({ col: "b", val: "s.from", vals: ["s.from"] });
    expect(sliceMatch(dottedKw, dottedKw.indexOf("s.set"), "pg")).toEqual({ col: "c", val: "s.set", vals: ["s.set"] });
    expect(sliceMatch(dottedKw, dottedKw.indexOf("3"), "pg")).toEqual({ col: "c", val: "3", vals: ["3"] });

    // Trigger & GRANT false positives
    const trig = "CREATE TRIGGER tr ON tbl AFTER INSERT AS BEGIN SELECT 1, 2 END;";
    expect(findInsertMatchingHighlight(trig, trig.indexOf("1"), "tsql")).toBeNull();
    const grant = "GRANT INSERT ON tbl TO app_user; SELECT 1;";
    expect(findInsertMatchingHighlight(grant, grant.indexOf("1"), "tsql")).toBeNull();

    // Columnless INSERT / DEFAULT VALUES / EXEC / TABLE -> null
    expect(findInsertMatchingHighlight("INSERT INTO t VALUES (1, 2);", 22, "tsql")).toBeNull();
    expect(findInsertMatchingHighlight("INSERT INTO t DEFAULT VALUES;", 15, "tsql")).toBeNull();
    expect(findInsertMatchingHighlight("INSERT INTO t (a, b) EXEC dbo.P;", 16, "tsql")).toBeNull();
    expect(findInsertMatchingHighlight("INSERT INTO t (a, b) TABLE other;", 16, "pg")).toBeNull();

    // Large 2,000-row INSERT: clicking column caps vals, clicking row 1,500 matches row 1,500 accurately
    const bigRows = Array.from({ length: 2000 }, (_, i) => `(${i}, 'row_${i}')`).join(",\n");
    const bigSql = `INSERT INTO t (id, label) VALUES\n${bigRows};`;
    const colHit = findInsertMatchingHighlight(bigSql, bigSql.indexOf("label"), "tsql");
    expect(colHit).not.toBeNull();
    expect(colHit!.vals.length).toBe(500);
    expect(bigSql.slice(colHit!.val.start, colHit!.val.end)).toBe("'row_0'");

    const row1500 = bigSql.indexOf("'row_1500'");
    expect(sliceMatch(bigSql, row1500, "tsql")).toEqual({ col: "label", val: "'row_1500'", vals: ["'row_1500'"] });
  });

  it("handles exact cursor boundaries, trailing semicolons, window functions, strings with SQL keywords, comments, and parameters", () => {
    // Cursor right at the end of the last value before `;`
    const semiSel = "INSERT INTO t (a, b) SELECT 1, 'asd';";
    const endOfAsd = semiSel.indexOf("'asd'") + "'asd'".length;
    expect(sliceMatch(semiSel, endOfAsd, "tsql")).toEqual({ col: "b", val: "'asd'", vals: ["'asd'"] });
    // Cursor AFTER `;` must not match
    expect(findInsertMatchingHighlight(semiSel + " SELECT 9", endOfAsd + 1, "tsql")).toBeNull();

    // Window functions (COUNT(*), OVER (PARTITION BY ... ORDER BY ...), FILTER (WHERE ...)) & EXTRACT/SUBSTRING
    const winSql = `INSERT INTO report (id, rn, cnt, yr, sub)
SELECT
  id,
  ROW_NUMBER() OVER (PARTITION BY dept, team ORDER BY sal DESC) AS rn,
  COUNT(*) FILTER (WHERE active),
  EXTRACT(YEAR FROM created_at),
  SUBSTRING(name FROM 1 FOR 3)
FROM employees;`;
    expect(sliceMatch(winSql, winSql.indexOf("cnt"), "pg")).toEqual({
      col: "cnt",
      val: "COUNT(*) FILTER (WHERE active)",
      vals: ["COUNT(*) FILTER (WHERE active)"],
    });
    expect(sliceMatch(winSql, winSql.indexOf("SUBSTRING"), "pg")).toEqual({
      col: "sub",
      val: "SUBSTRING(name FROM 1 FOR 3)",
      vals: ["SUBSTRING(name FROM 1 FOR 3)"],
    });

    // Strings containing keywords/commas/parens/semicolons + Oracle q'[...]' + PG $$...$$ + MySQL backslash escapes
    const pgStr = "INSERT INTO t (a, b) VALUES ($$a, b (1); FROM x$$, 'ok');";
    expect(sliceMatch(pgStr, pgStr.indexOf("'ok'"), "pg")).toEqual({ col: "b", val: "'ok'", vals: ["'ok'"] });
    const oraStr = "INSERT INTO t (a, b) SELECT q'[a, b (1) FROM x;]', 2 FROM dual;";
    expect(sliceMatch(oraStr, oraStr.indexOf("2 FROM"), "oracle")).toEqual({ col: "b", val: "2", vals: ["2"] });
    const myStr = "INSERT INTO t (a, b) VALUES ('it\\'s, fine', \"two, \\\"quoted\\\"\");";
    expect(sliceMatch(myStr, myStr.indexOf("\"two"), "mySql")).toEqual({
      col: "b",
      val: "\"two, \\\"quoted\\\"\"",
      vals: ["\"two, \\\"quoted\\\"\""],
    });

    // Derived VALUES inside FROM of INSERT ... SELECT must not leak
    const fromVals = "INSERT INTO t (a, b) SELECT v.x, v.y FROM (VALUES (1, 2), (3, 4)) AS v(x, y);";
    expect(sliceMatch(fromVals, fromVals.indexOf("v.y"), "pg")).toEqual({ col: "b", val: "v.y", vals: ["v.y"] });
    expect(findInsertMatchingHighlight(fromVals, fromVals.indexOf("2)"), "pg")).toBeNull();

    // Scalar subquery with UNION inside SELECT list stays a single expression
    const subUnion = "INSERT INTO t (a, b) SELECT 1, (SELECT x FROM s1 UNION ALL SELECT y FROM s2) FROM outer_t;";
    expect(sliceMatch(subUnion, subUnion.indexOf("b)"), "tsql")).toEqual({
      col: "b",
      val: "(SELECT x FROM s1 UNION ALL SELECT y FROM s2)",
      vals: ["(SELECT x FROM s1 UNION ALL SELECT y FROM s2)"],
    });

    // Stored proc BEGIN ... INSERT ... SELECT ... END: END must NOT be eaten into SELECT
    const procSql = "CREATE PROCEDURE dbo.P AS\nBEGIN\n  INSERT INTO auth.Menus ([strHashCode], [intMenuLabelId])\n  SELECT 'asd', 1\nEND";
    expect(sliceMatch(procSql, procSql.indexOf("1\nEND"), "tsql")).toEqual({
      col: "[intMenuLabelId]",
      val: "1",
      vals: ["1"],
    });
    expect(findInsertMatchingHighlight(procSql, procSql.lastIndexOf("END"), "tsql")).toBeNull();
    // And BEGIN/END pair still works on that END
    const blockPair = findSyntaxPairHighlight(procSql, procSql.lastIndexOf("END"), "tsql");
    expect(blockPair).not.toBeNull();
    expect(procSql.slice(blockPair![0].start, blockPair![0].end)).toBe("BEGIN");

    // Quoted keyword column names & variables & bind params
    const qkw = "INSERT INTO t ([select], [from]) SELECT @a, @@IDENTITY;";
    expect(sliceMatch(qkw, qkw.indexOf("@@IDENTITY"), "tsql")).toEqual({
      col: "[from]",
      val: "@@IDENTITY",
      vals: ["@@IDENTITY"],
    });
    const pgParams = "INSERT INTO t (a, b.f1, arr[1]) VALUES ($1, $2, $3);";
    expect(sliceMatch(pgParams, pgParams.indexOf("arr[1]"), "pg")).toEqual({ col: "arr[1]", val: "$3", vals: ["$3"] });
    const myParams = "INSERT INTO t (a, b) SELECT ?, ? FROM s;";
    expect(sliceMatch(myParams, myParams.lastIndexOf("?"), "mySql")).toEqual({ col: "b", val: "?", vals: ["?"] });
  });
});

describe("findSyntaxPairHighlight", () => {
  it("matches BEGIN and END", () => {
    const sql = "BEGIN\n  SELECT 1;\nEND;";
    const beginOffset = sql.indexOf("BEGIN");
    const pairs = findSyntaxPairHighlight(sql, beginOffset, "tsql");
    expect(pairs).toHaveLength(2);
    expect(sql.slice(pairs![0].start, pairs![0].end)).toBe("BEGIN");
    expect(sql.slice(pairs![1].start, pairs![1].end)).toBe("END");

    const endOffset = sql.indexOf("END");
    const pairsFromEnd = findSyntaxPairHighlight(sql, endOffset, "tsql");
    expect(pairsFromEnd).toHaveLength(2);
    expect(sql.slice(pairsFromEnd![0].start, pairsFromEnd![0].end)).toBe("BEGIN");
    expect(sql.slice(pairsFromEnd![1].start, pairsFromEnd![1].end)).toBe("END");
  });

  it("matches CASE and END", () => {
    const sql = "SELECT CASE WHEN x = 1 THEN 'one' ELSE 'other' END FROM t;";
    const caseOffset = sql.indexOf("CASE");
    const pairs = findSyntaxPairHighlight(sql, caseOffset, "tsql");
    expect(pairs).toHaveLength(2);
    expect(sql.slice(pairs![0].start, pairs![0].end)).toBe("CASE");
    expect(sql.slice(pairs![1].start, pairs![1].end)).toBe("END");
  });

  it("matches BEGIN TRY and END TRY", () => {
    const sql = "BEGIN TRY\n  SELECT 1;\nEND TRY\nBEGIN CATCH\n  SELECT 2;\nEND CATCH";
    const tryOffset = sql.indexOf("BEGIN TRY");
    const pairs = findSyntaxPairHighlight(sql, tryOffset, "tsql");
    expect(pairs).toHaveLength(2);
    expect(sql.slice(pairs![0].start, pairs![0].end)).toBe("BEGIN TRY");
    expect(sql.slice(pairs![1].start, pairs![1].end)).toBe("END TRY");
  });

  it("matches IF and ELSE", () => {
    const sql = "IF @x = 1 SELECT 'a'; ELSE SELECT 'b';";
    const ifOffset = sql.indexOf("IF");
    const pairs = findSyntaxPairHighlight(sql, ifOffset, "tsql");
    expect(pairs).toHaveLength(2);
    expect(sql.slice(pairs![0].start, pairs![0].end)).toBe("IF");
    expect(sql.slice(pairs![1].start, pairs![1].end)).toBe("ELSE");
  });
});


it.each(["tsql", "pg"] as const)("pairs blocks around CASE expressions (%s)", (dialect) => {
  const sql = "BEGIN SELECT CASE WHEN x=1 THEN 2 ELSE 3 END; END;";
  const pair = findSyntaxPairHighlight(sql, 0, dialect)!;
  expect(pair[1].start).toBe(sql.lastIndexOf("END"));
});
it("pairs nested BEGIN inside TRY in both directions", () => {
  const sql = "BEGIN TRY BEGIN SELECT 1; END; END TRY";
  for (const offset of [0, sql.lastIndexOf("END")]) {
    const pair = findSyntaxPairHighlight(sql, offset, "tsql")!;
    expect(sql.slice(pair[0].start, pair[0].end)).toBe("BEGIN TRY");
    expect(pair[1].start).toBe(sql.lastIndexOf("END"));
  }
});
it("does not match a transaction BEGIN to a later procedure END", () => {
  const sql = "BEGIN TRANSACTION; BEGIN SELECT 1; END;";
  expect(findSyntaxPairHighlight(sql, 0, "tsql")).toBeNull();
});
it("does not pair IF with CASE's ELSE", () => {
  const sql = "IF @x=1 SELECT CASE WHEN @y=1 THEN 2 ELSE 3 END; ELSE SELECT 4;";
  expect(findSyntaxPairHighlight(sql, 0, "tsql")![1].start).toBe(sql.lastIndexOf("ELSE"));
});

it("handles PostgreSQL dollar-quoted routine bodies without treating literals as code", () => {
  const sql = "DO $$ BEGIN INSERT INTO t (id) VALUES (42); END $$";
  const match = findInsertMatchingHighlight(sql, sql.indexOf("42"), "pg")!;
  expect(sql.slice(match.col.start, match.col.end)).toBe("id");
  const pair = findSyntaxPairHighlight(sql, sql.indexOf("BEGIN"), "pg")!;
  expect(pair[1].start).toBe(sql.indexOf("END"));
  const literal = "SELECT $$ BEGIN SELECT 1; END $$";
  expect(findSyntaxPairHighlight(literal, literal.indexOf("BEGIN"), "pg")).toBeNull();
});

it("pairs PL/pgSQL IF/ELSE/END IF inside its enclosing BEGIN block", () => {
  const sql = "DO $$ BEGIN IF flag THEN SELECT 1; ELSE SELECT 2; END IF; END $$";
  const pair = findSyntaxPairHighlight(sql, sql.indexOf("IF"), "pg")!;
  expect(pair.map((p) => sql.slice(p.start, p.end))).toEqual(["IF", "ELSE", "END IF"]);
  expect(findSyntaxPairHighlight(sql, sql.indexOf("BEGIN"), "pg")![1].start).toBe(sql.lastIndexOf("END"));
});

it.each(["tsql", "pg"] as const)("matches the INSERT containing the cursor when statements have no semicolons (%s)", (dialect) => {
  const sql = "INSERT INTO a (x) VALUES (1)\nINSERT INTO b (y, z) VALUES (2, 3)";
  const match = findInsertMatchingHighlight(sql, sql.indexOf("z"), dialect);
  expect(match && sql.slice(match.val.start, match.val.end)).toBe("3");
});

import { describe, expect, it } from "vitest";
import type { Dialect } from "../../ipc/contract";
import { applyTextEdits, coalesceEditsForRange, computeCommentEdits, statementCommentScope, type CommentMode, type CommentOptions } from "../smartComment";

const ALL_DIALECTS: Dialect[] = ["tsql", "pg", "mySql", "oracle", "sqlite", "duckDb"];
const NESTED_DIALECTS: Dialect[] = ["tsql", "pg", "duckDb"];
const NON_NESTED_DIALECTS: Dialect[] = ["mySql", "sqlite", "oracle"];

/** Parses `|` (cursor) or `[ ... ]` (one or more selections) from a template string. */
function parse(tpl: string): { text: string; selections: { start: number; end: number }[] } {
  let text = "";
  const selections: { start: number; end: number }[] = [];
  let open = -1;
  for (let i = 0; i < tpl.length; i++) {
    const ch = tpl[i];
    if (ch === "|") {
      selections.push({ start: text.length, end: text.length });
    } else if (ch === "[") {
      open = text.length;
    } else if (ch === "]" && open >= 0) {
      selections.push({ start: open, end: text.length });
      open = -1;
    } else {
      text += ch;
    }
  }
  return { text, selections: selections.length ? selections : [{ start: 0, end: 0 }] };
}

function run(tpl: string, dialect: Dialect, mode: CommentMode, opts: Partial<CommentOptions> = {}) {
  const { text, selections } = parse(tpl);
  const res = computeCommentEdits(text, selections, dialect, mode, opts);
  return { ...res, out: applyTextEdits(text, res.edits) };
}

describe("smartComment — all 6 active engines", () => {
  it.each(ALL_DIALECTS)("%s: toggles single line comment and uncomments cleanly", (d) => {
    const r1 = run("  SELECT 1| FROM t", d, "toggle");
    expect(r1.out).toBe("  -- SELECT 1 FROM t");
    const r2 = run("  -- SELECT 1| FROM t", d, "toggle");
    expect(r2.out).toBe("  SELECT 1 FROM t");
  });

  it.each(ALL_DIALECTS)("%s: multi-line selection aligns -- to common indent and skips blank lines", (d) => {
    const sql = "[\n  SELECT a,\n    b\n\n  FROM t\n]";
    const r1 = run(sql, d, "toggle");
    expect(r1.out).toBe("\n  -- SELECT a,\n  --   b\n\n  -- FROM t\n");
    const r2 = run(`[${r1.out}]`, d, "toggle");
    expect(r2.out).toBe("\n  SELECT a,\n    b\n\n  FROM t\n");
  });

  it.each(ALL_DIALECTS)("%s: placement='column1' places -- at column 1 and skipBlankLines=false comments blanks", (d) => {
    const r = run("[  SELECT a\n\n    FROM t]", d, "add", { placement: "column1", skipBlankLines: false });
    expect(r.out).toBe("--   SELECT a\n-- \n--     FROM t");
  });

  it.each(ALL_DIALECTS)("%s: partial inline selection on Ctrl+/ toggles /* ... */ losslessly when inlineBlock is on", (d) => {
    const r1 = run("SELECT a, [b, ]c FROM t", d, "toggle", { inlineBlock: true });
    expect(r1.out).toBe("SELECT a, /* b, */ c FROM t");
    const r2 = run("SELECT a, [/* b, */ ]c FROM t", d, "toggle", { inlineBlock: true });
    expect(r2.out).toBe("SELECT a, b, c FROM t");
    // When inlineBlock is off, Ctrl+/ comments the whole line instead.
    const r3 = run("SELECT a, [b, ]c FROM t", d, "toggle", { inlineBlock: false });
    expect(r3.out).toBe("-- SELECT a, b, c FROM t");
  });

  it.each(ALL_DIALECTS)("%s: Ctrl+/ on a full-line block comment unwraps it", (d) => {
    const r = run("[/* SELECT 1 */]", d, "toggle");
    expect(r.out).toBe("SELECT 1");
  });

  it.each(ALL_DIALECTS)("%s: advances cursor to next line when advanceCursor is enabled", (d) => {
    const r = run("SELECT| 1\nFROM orders", d, "toggle", { advanceCursor: true });
    expect(r.out).toBe("-- SELECT 1\nFROM orders");
    expect(r.nextSelections).toBeDefined();
    const pos = r.nextSelections![0].start;
    expect(r.out.slice(pos)).toBe("rders");
  });

  it.each(ALL_DIALECTS)("%s: supports multi-cursor toggle without corrupting edits", (d) => {
    const r = run("SELECT 1|\nSELECT 2\nSELECT 3|", d, "toggle");
    expect(r.out).toBe("-- SELECT 1\nSELECT 2\n-- SELECT 3");
  });

  it.each(ALL_DIALECTS)("%s: guards against commenting a line starting strictly inside a multi-line string", (d) => {
    const r = run("SELECT 'line1\nline2|\nline3'", d, "toggle");
    expect(r.edits).toEqual([]);
    expect(r.notice).toMatch(/string literal/i);
  });

  it.each(ALL_DIALECTS)("%s: toggles current statement comment both ways", (d) => {
    const sql = "SELECT 1;\n\nSELECT a\nFROM| orders\nWHERE id = 5;\n\nSELECT 2;";
    const scope1 = statementCommentScope(sql.replace("|", ""), sql.indexOf("|"), d);
    expect(scope1).toMatchObject({ action: "comment", startLine: 2, endLine: 4 });

    const r1 = run(sql, d, "statement");
    expect(r1.out).toBe("SELECT 1;\n\n-- SELECT a\n-- FROM orders\n-- WHERE id = 5;\n\nSELECT 2;");

    const cursorInsideCommented = r1.out.indexOf("FROM");
    const scope2 = statementCommentScope(r1.out, cursorInsideCommented, d);
    expect(scope2).toMatchObject({ action: "uncomment", startLine: 2, endLine: 4 });

    const r2 = computeCommentEdits(r1.out, [{ start: cursorInsideCommented, end: cursorInsideCommented }], d, "statement");
    expect(applyTextEdits(r1.out, r2.edits)).toBe("SELECT 1;\n\nSELECT a\nFROM orders\nWHERE id = 5;\n\nSELECT 2;");
  });

  it.each(ALL_DIALECTS)("%s: inserts section header above code and in-place on blank lines", (d) => {
    const onBlank = run("  |", d, "section");
    expect(onBlank.out).toMatch(/^  -- ── Section ─+$/);
    expect(onBlank.nextSelections).toBeDefined();
    expect(onBlank.out.slice(onBlank.nextSelections![0].start, onBlank.nextSelections![0].end)).toBe("Section");

    const onCode = run("  SELECT 1|", d, "section");
    expect(onCode.out).toMatch(/^  -- ── Section ─+\n  SELECT 1$/);

    const withTitle = run("  [Orders Filter]", d, "section");
    expect(withTitle.out).toMatch(/^  -- ── Orders Filter ─+$/);
  });

  it.each(ALL_DIALECTS)("%s: inserts doc block header with date and selects summary", (d) => {
    const onBlank = run("  |", d, "docHeader", { date: "2026-11-27" });
    expect(onBlank.out).toBe("  /*\n   * Summary\n   * Date: 2026-11-27\n   */");
    expect(onBlank.out.slice(onBlank.nextSelections![0].start, onBlank.nextSelections![0].end)).toBe("Summary");

    const onCode = run("SELECT 1|", d, "docHeader", { date: "2026-11-27" });
    expect(onCode.out).toBe("/*\n * Summary\n * Date: 2026-11-27\n */\nSELECT 1");
  });

  it.each(ALL_DIALECTS)("%s: converts comments between -- and /* */ both ways", (d) => {
    const toBlock = run("  -- line 1|\n  -- line 2", d, "convert");
    expect(toBlock.out).toBe("  /*\n    line 1\n    line 2\n  */");

    const toLine = run("  /*\n   * line 1|\n   * line 2\n   */", d, "convert");
    expect(toLine.out).toBe("  -- line 1\n  -- line 2");

    const singleToBlock = run("-- single note|", d, "convert");
    expect(singleToBlock.out).toBe("/* single note */");

    const singleToLine = run("/* single| note */", d, "convert");
    expect(singleToLine.out).toBe("-- single note");
  });

  it.each(ALL_DIALECTS)("%s: refuses to convert an inline block comment with trailing code to a line comment", (d) => {
    const r = run("SELECT /* inline| */ 1 FROM t", d, "convert");
    expect(r.edits).toEqual([]);
    expect(r.notice).toMatch(/inline block comment/i);
  });

  it.each(ALL_DIALECTS)("%s: strips comments while keeping string literals intact and separating adjacent tokens", (d) => {
    const sql = "-- header\nSELECT/*c*/1, '-- keep' AS s -- trailing\nFROM /* mid */ t";
    const r = run(sql, d, "strip");
    expect(r.out).toBe("SELECT 1, '-- keep' AS s\nFROM t");
  });
});

describe("smartComment — dialect-specific rules", () => {
  it.each(NESTED_DIALECTS)("%s: allows nested block comments", (d) => {
    const r = run("[SELECT /* inner */ 1]", d, "block");
    expect(r.out).toBe("/* SELECT /* inner */ 1 */");
    expect(r.notice).toBeUndefined();
  });

  it.each(NON_NESTED_DIALECTS)("%s: falls back to line comments when block-commenting over an inner /* */", (d) => {
    const r = run("[SELECT /* inner */ 1\nFROM t]", d, "block");
    expect(r.out).toBe("-- SELECT /* inner */ 1\n-- FROM t");
    expect(r.notice).toMatch(/cannot nest/i);
  });

  it("MySQL: inserts single space for -- , supports # style, and uncomments both -- and #", () => {
    expect(run("SELECT 1|", "mySql", "toggle").out).toBe("-- SELECT 1");
    expect(run("SELECT 1|", "mySql", "toggle", { mysqlLineStyle: "# " }).out).toBe("# SELECT 1");
    expect(run("# SELECT 1|", "mySql", "toggle", { mysqlLineStyle: "-- " }).out).toBe("SELECT 1");
    // In MySQL `--foo` without space is NOT a comment, so toggling comments it cleanly.
    expect(run("--foo|", "mySql", "toggle").out).toBe("-- --foo");
  });

  it("MySQL: stripComments preserves /*!50000 ... */ executable comments and /*+ ... */ optimizer hints", () => {
    const sql = "# note\nSELECT /*+ MAX_EXECUTION_TIME(1000) */ /*!50000 SQL_NO_CACHE */ 1 -- end";
    expect(run(sql, "mySql", "strip").out).toBe("SELECT /*+ MAX_EXECUTION_TIME(1000) */ /*!50000 SQL_NO_CACHE */ 1");
    // Cursor inside a MySQL 8+ /*+ hint */ must not unwrap it on block comment.
    const hintBlock = run("SELECT /*+ NO_MERGE|(t) */ 1 FROM t", "mySql", "block");
    expect(hintBlock.out).toBe("-- SELECT /*+ NO_MERGE(t) */ 1 FROM t");
  });

  it("Oracle: stripComments preserves /*+ ... */ and --+ optimizer hints, and toggle does not corrupt --+ hints", () => {
    const sql = "-- normal\nSELECT /*+ FULL(e) */ 1 FROM emp e\n--+ PARALLEL(e 4)\nWHERE 1=1";
    expect(run(sql, "oracle", "strip").out).toBe("SELECT /*+ FULL(e) */ 1 FROM emp e\n--+ PARALLEL(e 4)\nWHERE 1=1");
    // Toggling an Oracle --+ hint line comments it out cleanly instead of stripping `--` and leaving `+`.
    expect(run("--+ FULL(e)|", "oracle", "toggle").out).toBe("-- --+ FULL(e)");
  });

  it("PostgreSQL: guards inside dollar-quoted $$...$$ function bodies", () => {
    const sql = "CREATE FUNCTION f() RETURNS text AS $$\nSELECT 1|\n$$ LANGUAGE sql;";
    const r = run(sql, "pg", "toggle");
    expect(r.edits).toEqual([]);
    expect(r.notice).toMatch(/string literal/i);
  });

  it("Oracle: guards inside q'[...]' alternative-quoted strings", () => {
    const sql = "SELECT q'[line1\nline2]' FROM dual;";
    const pos = sql.indexOf("line2") + 3;
    const r = computeCommentEdits(sql, [{ start: pos, end: pos }], "oracle", "toggle");
    expect(r.edits).toEqual([]);
    expect(r.notice).toMatch(/string literal/i);
  });

  it("block comment on blank line inserts /*  */ with cursor in the middle", () => {
    const r = run("  |", "tsql", "block");
    expect(r.out).toBe("  /*  */");
    expect(r.nextSelections).toEqual([{ start: 5, end: 5 }]);
  });

  it("block comment unwraps when cursor sits inside an existing block comment", () => {
    const r = run("SELECT /* a |+ b */ FROM t", "tsql", "block");
    expect(r.out).toBe("SELECT a + b FROM t");
  });

  it.each(NON_NESTED_DIALECTS)("%s: refuses converting line comment containing '*/' into a block comment", (d) => {
    const r = run("-- contains */ inside|", d, "convert");
    expect(r.edits).toEqual([]);
    expect(r.notice).toMatch(/cannot convert/i);
  });

  it("MySQL: converts block comment to # line comments when mysqlLineStyle is '# '", () => {
    const r = run("/* line 1|\n * line 2\n */", "mySql", "convert", { mysqlLineStyle: "# " });
    expect(r.out).toBe("# line 1\n# line 2");
  });

  it("audit regressions: adjacent stripped comments, reversed selections, string-crossing block wrap, unbalanced nested convert, docHeader */ sanitization, and blank-first-line indent", () => {
    // 1. Adjacent block comments between tokens and at line end (no dropped edit, no trailing space, single space).
    expect(run("SELECT/*a*//*b*/1", "tsql", "strip").out).toBe("SELECT 1");
    expect(run("SELECT 1 /* a */ /* b */", "tsql", "strip").out).toBe("SELECT 1");

    // 2. Reversed / out-of-bounds selections normalize safely without throwing.
    const rev = computeCommentEdits("SELECT a, b FROM t", [{ start: 11, end: 7 }], "tsql", "block");
    expect(applyTextEdits("SELECT a, b FROM t", rev.edits)).toBe("SELECT /* a, b */ FROM t");
    const oob = computeCommentEdits("SELECT 1", [{ start: -5, end: 999 }], "tsql", "toggle");
    expect(applyTextEdits("SELECT 1", oob.edits)).toBe("-- SELECT 1");

    // 3. Block-commenting a selection that ends inside a string literal is guarded.
    const strCross = run("[SELECT 'abc]def'", "tsql", "block");
    expect(strCross.edits).toEqual([]);
    expect(strCross.notice).toMatch(/string literal/i);

    // 4. Nested dialects reject unbalanced `*/` on convert and fall back cleanly when splitting an inner block comment.
    const unbal = run("-- unbalanced */ close|", "tsql", "convert");
    expect(unbal.edits).toEqual([]);
    expect(unbal.notice).toMatch(/unbalanced/i);

    const splitInner = run("[SELECT /* a] b */ 1", "tsql", "block");
    expect(splitInner.out).toBe("-- SELECT /* a b */ 1");
    expect(splitInner.notice).toMatch(/splits an existing block comment/i);

    // 5. docHeader sanitizes `*/` in selected text so it cannot break out of `/* ... */`.
    const docSan = run("  [foo */ bar]", "tsql", "docHeader", { date: "2026-11-27" });
    expect(docSan.out).toContain("* foo * / bar");

    // 6. convert with a leading blank line in selection preserves indent from first non-blank line.
    const blankFirst = run("[\n    -- a\n    -- b]", "tsql", "convert");
    expect(blankFirst.out).toBe("    /*\n\n      a\n      b\n    */");

    // 7. Multi-cursor block comment on the same line does not double-wrap; distinct inline selections both wrap.
    expect(run("SELECT a|, b| FROM t", "mySql", "block").out).toBe("/* SELECT a, b FROM t */");
    expect(run("SELECT [a], [b] FROM t", "tsql", "block").out).toBe("SELECT /* a */, /* b */ FROM t");

    // 8. Multi-cursor partial line overlap comments all non-overlapping lines instead of dropping the whole second selection.
    const partialOverlap = computeCommentEdits(
      "SELECT 1\nSELECT 2\nSELECT 3\nSELECT 4",
      [{ start: 0, end: 12 }, { start: 10, end: 35 }],
      "tsql",
      "toggle",
    );
    expect(applyTextEdits("SELECT 1\nSELECT 2\nSELECT 3\nSELECT 4", partialOverlap.edits)).toBe(
      "-- SELECT 1\n-- SELECT 2\n-- SELECT 3\n-- SELECT 4",
    );

    // 9. coalesceEditsForRange safely handles empty and unsorted edit lists.
    expect(coalesceEditsForRange("SELECT 1", [])).toEqual({ start: 0, end: 0, text: "" });
    const coal = coalesceEditsForRange("SELECT a, b", [
      { start: 10, end: 11, text: "B" },
      { start: 7, end: 8, text: "A" },
    ]);
    expect(coal).toEqual({ start: 7, end: 11, text: "A, B" });
  });

  it.each(ALL_DIALECTS)("%s: block comment handles trailing col-1 Shift+Down selections cleanly (LF and CRLF) and round-trips", (d) => {
    const lf = "SELECT *\n[FROM orders o\nWHERE o.status = 'open'\n]ORDER BY o.id";
    const r1 = run(lf, d, "block");
    expect(r1.out).toBe("SELECT *\n/* FROM orders o\nWHERE o.status = 'open' */\nORDER BY o.id");
    // Unwrapping with the same trailing-newline selection restores the original SQL losslessly.
    const r2 = run("SELECT *\n[/* FROM orders o\nWHERE o.status = 'open' */\n]ORDER BY o.id", d, "block");
    expect(r2.out).toBe("SELECT *\nFROM orders o\nWHERE o.status = 'open'\nORDER BY o.id");

    const crlf = "SELECT *\r\n[FROM orders o\r\nWHERE o.status = 'open'\r\n]ORDER BY o.id";
    const r3 = run(crlf, d, "block");
    expect(r3.out).toBe("SELECT *\r\n/* FROM orders o\r\nWHERE o.status = 'open' */\r\nORDER BY o.id");
  });

  it.each(NESTED_DIALECTS)("%s: block comment falls back to line toggle on unbalanced /* or */ inside strings/line comments", (d) => {
    const glob = run("[SELECT * FROM read_parquet('data/*.parquet');]", d, "block");
    expect(glob.out).toBe("-- SELECT * FROM read_parquet('data/*.parquet');");
    expect(glob.notice).toMatch(/unbalanced/i);
    // Repeated block toggle on the fallback line cleanly uncomments instead of stacking `-- --`.
    const glob2 = run(`[${glob.out}]`, d, "block");
    expect(glob2.out).toBe("SELECT * FROM read_parquet('data/*.parquet');");

    const cron = run("[SELECT '*/5 * * * *' AS cron;]", d, "block");
    expect(cron.out).toBe("-- SELECT '*/5 * * * *' AS cron;");
    expect(cron.notice).toMatch(/unbalanced/i);
  });

  it.each(NON_NESTED_DIALECTS)("%s: block comment guards */ inside strings and -- line comments and toggles cleanly on repeat", (d) => {
    const cron = run("[SELECT '*/5 * * * *' AS cron;]", d, "block");
    expect(cron.out).toBe("-- SELECT '*/5 * * * *' AS cron;");
    expect(cron.notice).toMatch(/contains '\*\/'/i);

    // Repeating block comment on a line with an inner block comment/hint must toggle `-- ` off instead of corrupting SQL.
    const first = run("SELECT /*+ FULL(t) */ * FROM t|", d, "block");
    expect(first.out).toBe("-- SELECT /*+ FULL(t) */ * FROM t");
    const second = run(`${first.out}|`, d, "block");
    expect(second.out).toBe("SELECT /*+ FULL(t) */ * FROM t");
  });

  it.each(ALL_DIALECTS)("%s: Ctrl+/ with no selection unwraps a whole-line block comment (including multi-cursor) without touching inline notes or hints", (d) => {
    expect(run("  /* SELECT| 1 */  ", d, "toggle").out).toBe("  SELECT 1  ");
    expect(run("/* a| b| */", d, "toggle").out).toBe("a b");
    // Inline block comment alongside code still comments the full line.
    expect(run("SELECT /* note| */ 1 FROM t", d, "toggle").out).toBe("-- SELECT /* note */ 1 FROM t");
    // Hints on Oracle/MySQL are never unwrapped.
    if (d === "oracle" || d === "mySql") {
      expect(run("/*+ FULL(t)| */", d, "toggle").out).toBe("-- /*+ FULL(t) */");
    }
  });
});

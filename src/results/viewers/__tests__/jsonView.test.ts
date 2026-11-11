import { describe, expect, it } from "vitest";
import { buildJsonView, JSON_MAX_DEPTH } from "../jsonView";
import { findMatches, foldedText, lineCount, lineText, reveal, visibleLines, type StructuredView } from "../lines";

const lines = (v: StructuredView) => Array.from({ length: lineCount(v) }, (_, i) => lineText(v, i));

function ok(text: string) {
  const r = buildJsonView(text);
  if (!r.ok) throw new Error(r.error);
  return r;
}

describe("buildJsonView", () => {
  it("formats with two-space indent and keeps numbers and strings exactly as written", () => {
    const r = ok('{"id":12345678901234567890,"price":1.50,"e":1E+2,"s":"a\\u00e9\\n\\"q\\"","list":[true,false,null],"empty":{},"none":[]}');
    expect(lines(r.view)).toEqual([
      "{",
      '  "id": 12345678901234567890,',
      '  "price": 1.50,',
      '  "e": 1E+2,',
      '  "s": "a\\u00e9\\n\\"q\\"",',
      '  "list": [',
      "    true,",
      "    false,",
      "    null",
      "  ],",
      '  "empty": {},',
      '  "none": []',
      "}",
    ]);
    expect(r.explainPlan).toBe(false);
  });

  it("links every opening line to its closing line", () => {
    const r = ok('{"a":{"b":[1,2]},"c":3}');
    const v = r.view;
    expect(v.foldEnd[0]).toBe(lineCount(v) - 1);
    expect(lineText(v, 1)).toBe('  "a": {');
    expect(lineText(v, v.foldEnd[1])).toBe("  },");
    expect(lineText(v, v.foldEnd[2])).toBe("    ]");
    expect(v.foldEnd[3]).toBe(-1);
    expect(Array.from(v.depth)).toEqual([0, 1, 2, 3, 3, 2, 1, 1, 0]);
  });

  it("formats scalars and a byte order mark", () => {
    expect(lines(ok("﻿ 42 ").view)).toEqual(["42"]);
    expect(lines(ok('"x"').view)).toEqual(['"x"']);
  });

  it.each([
    ['{"a":1,}', 'Unexpected "}", expected a property name in double quotes at line 1, column 8'],
    ['{"a" 1}', 'Unexpected "1", expected ":" at line 1, column 6'],
    ['[1 2]', 'Unexpected "2", expected "," or "]" at line 1, column 4'],
    ['{"a":"open', "Unterminated string at line 1, column 6"],
    ['{"a":tru}', 'Unexpected "t", expected a value at line 1, column 6'],
    ['{"a":01}', 'Unexpected "1", expected "," or "}" at line 1, column 7'],
    ['{"a":1}\n{"b":2}', 'Unexpected "{" after the end of the JSON value at line 2, column 1'],
    ['"bad \\x escape"', 'Invalid escape "\\x" in a string at line 1, column 6'],
    ['"tab\there"', "Unescaped control character U+0009 in a string at line 1, column 5"],
    ["", "Unexpected end of text, expected a value at line 1, column 1"],
    ["{'a': 1}", "Unexpected \"'\", expected a property name in double quotes at line 1, column 2"],
  ])("says where %j stops being JSON", (text, message) => {
    const r = buildJsonView(text);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error).toBe(message);
  });

  it("reports the line and column of an error on a later line", () => {
    const r = buildJsonView('{\n  "a": 1,\n  "b": ]\n}');
    expect(r).toMatchObject({ ok: false, line: 3, column: 8 });
  });

  it("refuses values nested deeper than the limit instead of recursing on", () => {
    const deep = "[".repeat(JSON_MAX_DEPTH + 5) + "]".repeat(JSON_MAX_DEPTH + 5);
    const r = buildJsonView(deep);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error).toContain(`Nested deeper than ${JSON_MAX_DEPTH} levels`);
    expect(buildJsonView("[".repeat(100) + "]".repeat(100)).ok).toBe(true);
  });

  it("shows values over the size cap as text with the reason", () => {
    const r = buildJsonView('{"a":"' + "x".repeat(100) + '"}', 50);
    expect(r).toMatchObject({ ok: false, tooLarge: true });
    if (!r.ok) expect(r.error).toContain("shown as text");
  });

  it("recognises a PostgreSQL EXPLAIN (FORMAT JSON) value", () => {
    expect(ok('[{"Plan": {"Node Type": "Seq Scan", "Total Cost": 1.5}}]').explainPlan).toBe(true);
    expect(ok('[{"plan": 1}]').explainPlan).toBe(false);
    expect(ok('{"Plan": {}}').explainPlan).toBe(false);
    expect(ok("[]").explainPlan).toBe(false);
  });

  it("names each JSON plan form the sidecar returns: PostgreSQL, SQLite (sqlite-eqp) and MySQL (mysql-json)", () => {
    expect(ok('[{"Plan": {"Node Type": "Seq Scan"}}]').jsonPlan).toBe("explainJson");
    expect(ok('[{"text": "SELECT * FROM t", "rows": [[2, 0, "SCAN t"], [5, 2, "USE TEMP B-TREE FOR ORDER BY"]]}]').jsonPlan).toBe("sqliteEqp");
    expect(ok('[{"text": "SELECT 1", "plan": "{\\"query_block\\": {}}"}]').jsonPlan).toBe("mysqlJson");
    expect(ok('[{"text": "SELECT 1", "actual": false, "plan": [{"name": "PROJECTION", "children": []}]}]').jsonPlan).toBe("duckdbJson");
    expect(ok('[{"text": "SELECT 1", "actual": true, "plan": {"latency": 0.1, "children": []}}]').jsonPlan).toBe("duckdbJson");
    // D4 S09: the sidecar's Oracle plan (format "oracle-json"), estimated or actual.
    expect(ok('[{"text": "SELECT 1 FROM dual", "actual": false, "operations": [{"id": 0, "operation": "SELECT STATEMENT", "cost": 2}]}]').jsonPlan).toBe("oracleJson");
    expect(ok('[{"text": "SELECT 1 FROM dual", "actual": true, "operations": []}]').jsonPlan).toBe("oracleJson");
    expect(ok('[{"text": "x", "operations": []}]').jsonPlan).toBeNull();
    expect(ok('[{"text": "x", "actual": true, "operations": {}}]').jsonPlan).toBeNull();
    expect(ok('[{"text": "x", "rows": [[1, 2]]}]').jsonPlan).toBeNull();
    expect(ok('[{"text": "x", "plan": {}}]').jsonPlan).toBeNull();
    expect(ok('[{"plan": "x"}]').jsonPlan).toBeNull();
    expect(ok('{"text": "x", "rows": []}').jsonPlan).toBeNull();
    expect(ok('[{"text": "x", "rows": [[1, 0, "SCAN t"]]}]').explainPlan).toBe(false);
  });
});

describe("folds and search over formatted lines", () => {
  const v = ok('{"a":{"b":1,"c":2},"d":[3,4],"e":"needle"}').view;

  it("folding a block keeps its opening line and hides the rest", () => {
    expect(visibleLines(v, new Set()).length).toBe(lineCount(v));
    const shown = Array.from(visibleLines(v, new Set([1])));
    expect(shown).toEqual([0, 1, 5, 6, 7, 8, 9, 10]);
    expect(foldedText(v, 1)).toBe('  "a": { … },');
    expect(foldedText(v, 6)).toBe("    3,");
  });

  it("finds hits case-insensitively and counts them", () => {
    const m = findMatches(v, "NEEDLE");
    expect(Array.from(m.lines)).toEqual([9]);
    expect(m.total).toBe(1);
    expect(findMatches(v, '"').total).toBe(12);
    expect(findMatches(v, "").total).toBe(0);
    expect(findMatches(v, "nothing").lines.length).toBe(0);
  });

  it("reveal opens only the folds that hide a line", () => {
    const folded = new Set([0, 1, 5]);
    const next = reveal(v, folded, 3);
    expect([...next].sort()).toEqual([5]);
    const same = new Set([5]);
    expect(reveal(v, same, 2)).toBe(same);
  });
});

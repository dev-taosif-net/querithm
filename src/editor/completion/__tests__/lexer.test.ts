import { describe, it, expect, beforeEach } from "vitest";
import type { Dialect } from "../../../ipc/contract";
import { clearTokenCache, getStream, tokenize, type Token } from "../lexer";

/** "kind:source text" per token. */
const kt = (text: string, dialect: Dialect = "tsql") => tokenize(text, dialect).map((t) => `${t.kind}:${text.slice(t.start, t.end)}`);
const one = (text: string, dialect: Dialect = "tsql"): Token => {
  const toks = tokenize(text, dialect);
  expect(toks).toHaveLength(1);
  return toks[0];
};
const shape = (toks: Token[]) => toks.map((t) => `${t.kind}:${t.start}:${t.end}:${t.open ? 1 : 0}`);

describe("lexer: comments", () => {
  it("line comment stops before CRLF", () => {
    expect(kt("-- hi\r\nSELECT")).toEqual(["comment:-- hi", "keyword:SELECT"]);
    expect(tokenize("-- hi", "tsql")[0].line).toBe(true);
  });
  it("line comment at end of text runs to the end", () => expect(kt("SELECT 1 --")).toEqual(["keyword:SELECT", "number:1", "comment:--"]));
  it("block comments nest", () => expect(kt("/* a /* b */ c */ SELECT", "pg")).toEqual(["comment:/* a /* b */ c */", "keyword:SELECT"]));
  it("unterminated block comment is open and runs to the end", () => {
    const t = one("/* never closed");
    expect(t.open).toBe(true);
    expect(t.end).toBe(15);
    expect(one("/*").open).toBe(true);
  });
  it("closed block comment is not open", () => expect(one("/* x */").open).toBeUndefined());
  it("comments are merged into source order with code", () => {
    expect(kt("SELECT /*c*/ 1 -- d")).toEqual(["keyword:SELECT", "comment:/*c*/", "number:1", "comment:-- d"]);
  });
});

describe("lexer: strings", () => {
  it("doubled quote is an escape, not a terminator", () => expect(kt("'it''s' x")).toEqual(["string:'it''s'", "ident:x"]));
  it("unterminated string is open", () => {
    const t = one("'abc");
    expect(t).toMatchObject({ kind: "string", start: 0, end: 4, open: true });
  });
  it("lone quote at end of text", () => expect(one("'")).toMatchObject({ kind: "string", end: 1, open: true }));
  it("N'' unicode literal includes the prefix (both cases)", () => {
    expect(kt("N'abc'")).toEqual(["string:N'abc'"]);
    expect(kt("n'abc'")).toEqual(["string:n'abc'"]);
  });
  it("T-SQL has no E''/B''/X'' literals: the letter is an identifier", () => {
    expect(kt("E'a'")).toEqual(["ident:E", "string:'a'"]);
    expect(kt("X'ff'")).toEqual(["ident:X", "string:'ff'"]);
  });
  it("pg E'' honours backslash escapes; plain '' does not", () => {
    expect(kt("E'a\\'b' x", "pg")).toEqual(["string:E'a\\'b'", "ident:x"]);
    expect(kt("e'\\\\' x", "pg")).toEqual(["string:e'\\\\'", "ident:x"]);
    expect(kt("'a\\' x", "pg")).toEqual(["string:'a\\'", "ident:x"]);
  });
  it("pg B'' and X'' bit/hex literals", () => expect(kt("B'101' X'ff'", "pg")).toEqual(["string:B'101'", "string:X'ff'"]));
  it("pg E'' with a trailing backslash stays open and within bounds", () => {
    const t = one("E'abc\\", "pg");
    expect(t.open).toBe(true);
    expect(t.end).toBe(6);
  });
});

describe("lexer: quoted identifiers", () => {
  it("brackets with spaces and escaped ]]", () => {
    expect(one("[Order Details]").value).toBe("Order Details");
    expect(one("[a]]b]").upper).toBe("A]B");
  });
  it('double quotes with escaped ""', () => expect(one('"a""b"', "pg").value).toBe('a"b'));
  it("unterminated quoted identifier stops at end of line", () => {
    const toks = tokenize("[open\nx", "tsql");
    expect(toks[0]).toMatchObject({ kind: "qident", value: "open", end: 5, open: true });
    expect(toks[1]).toMatchObject({ kind: "ident", value: "x" });
  });
  it("pg has no bracket identifiers", () => expect(kt("[1]", "pg")).toEqual(["operator:[", "number:1", "operator:]"]));
});

describe("lexer: pg dollar quotes and parameters", () => {
  it("$$ and $tag$ strings record the tag length", () => {
    expect(one("$$ body $$", "pg")).toMatchObject({ kind: "string", tag: 2 });
    expect(one("$fn$ a $$ b $fn$", "pg")).toMatchObject({ kind: "string", tag: 4, end: 16 });
  });
  it("unterminated dollar quote is open to the end", () => expect(one("$body$ BEGIN", "pg")).toMatchObject({ open: true, end: 12 }));
  it("$n positional parameters", () => {
    expect(one("$12", "pg")).toMatchObject({ kind: "param", value: "$12" });
  });
  it("a bare $ or $name without closing $ is not a string", () => {
    expect(kt("$", "pg")).toEqual(["operator:$"]);
    expect(kt("$a x", "pg")).toEqual(["operator:$", "ident:a", "ident:x"]);
  });
  it(":name parameter vs :: cast vs := assignment", () => {
    expect(kt(":name", "pg")).toEqual(["param::name"]);
    expect(kt("a::int", "pg")).toEqual(["ident:a", "operator:::", "ident:int"]);
    expect(kt("v := 1", "pg")).toEqual(["ident:v", "operator::=", "number:1"]);
  });
  it("T-SQL treats $ as an operator", () => expect(kt("$1")).toEqual(["operator:$", "number:1"]));
});

describe("lexer: variables, numbers, operators", () => {
  it("bare @ is a variable token in T-SQL and an operator in pg", () => {
    expect(one("@")).toMatchObject({ kind: "variable", value: "@" });
    expect(kt("a @> b", "pg")).toEqual(["ident:a", "operator:@>", "ident:b"]);
  });
  it("decimals, exponents and leading dots", () => {
    expect(kt("1.5e-3")).toEqual(["number:1.5e-3"]);
    expect(kt("2E10")).toEqual(["number:2E10"]);
    expect(kt(".5")).toEqual(["number:.5"]);
    expect(kt("1.")).toEqual(["number:1."]);
  });
  it("an incomplete exponent is not part of the number", () => expect(kt("1e+")).toEqual(["number:1", "ident:e", "operator:+"]));
  it("hex literals in T-SQL only", () => {
    expect(kt("0x1F")).toEqual(["number:0x1F"]);
    expect(kt("0x1F", "pg")).toEqual(["number:0", "ident:x1F"]);
  });
  it("dots between identifiers are punctuation, not numbers", () => expect(kt("a.b")).toEqual(["ident:a", "punct:.", "ident:b"]));
  it("T-SQL compound operators", () => {
    expect(kt("x+=1")).toEqual(["ident:x", "operator:+=", "number:1"]);
    expect(kt("a<>b")).toEqual(["ident:a", "operator:<>", "ident:b"]);
    expect(kt("a->b")).toEqual(["ident:a", "operator:-", "operator:>", "ident:b"]);
  });
  it("pg picks the longest JSON operator", () => {
    expect(kt("j->>'k'", "pg")).toEqual(["ident:j", "operator:->>", "string:'k'"]);
    expect(kt("j#>>'{a}'", "pg")).toEqual(["ident:j", "operator:#>>", "string:'{a}'"]);
  });
  it("pg # is not an identifier character", () => expect(kt("#tmp", "pg")).toEqual(["operator:#", "ident:tmp"]));
});

describe("lexer: words and whitespace", () => {
  it("keywords vs identifiers; upper is uppercased, value keeps the source", () => {
    const [s, x] = tokenize("select myCol", "tsql");
    expect(s).toMatchObject({ kind: "keyword", upper: "SELECT", value: "select" });
    expect(x).toMatchObject({ kind: "ident", upper: "MYCOL", value: "myCol" });
  });
  it("non-ASCII identifiers", () => expect(one("Ünïcode")).toMatchObject({ kind: "ident", value: "Ünïcode" }));
  it("$ inside an identifier", () => expect(one("a$b")).toMatchObject({ kind: "ident", value: "a$b" }));
  it("non-breaking space, BOM and line separators are whitespace", () => {
    expect(kt("SELECT x y﻿z")).toEqual(["keyword:SELECT", "ident:x", "ident:y", "ident:z"]);
  });
  it("null text yields no tokens", () => expect(tokenize(null as unknown as string, "tsql")).toEqual([]));
});

describe("lexer: GO separators", () => {
  it("GO with a count and a trailing comment", () => {
    const toks = tokenize("SELECT 1\n  GO 3 -- thrice", "tsql");
    const go = toks.find((t) => t.kind === "go")!;
    expect(go.count).toBe(3);
    expect(go.value).toBe("GO 3");
    expect(toks[toks.length - 1].kind).toBe("comment");
  });
  it("GO followed by other code on the line is not a separator", () => {
    expect(tokenize("GO;", "tsql").some((t) => t.kind === "go")).toBe(false);
    expect(tokenize("SELECT 1 GO", "tsql").some((t) => t.kind === "go")).toBe(false);
  });
  it("GO after a BOM and on CRLF lines", () => {
    expect(tokenize("﻿GO", "tsql")[0].kind).toBe("go");
    expect(tokenize("SELECT 1\r\ngo\r\nSELECT 2", "tsql").filter((t) => t.kind === "go")).toHaveLength(1);
  });
  it("GO without a count has no count property", () => expect(one("GO").count).toBeUndefined());
  it("pg has no GO separator", () => expect(one("GO", "pg").kind).not.toBe("go"));
});

describe("lexer: token stream cache", () => {
  beforeEach(() => clearTokenCache());

  it("splits code, comments and marks", () => {
    const s = getStream("DECLARE @x int; -- c\nGO\nCREATE TABLE t (a int)\nALTER TABLE t ADD b int", "tsql");
    expect(s.comments).toHaveLength(1);
    expect(s.code.every((t) => t.kind !== "comment")).toBe(true);
    expect(s.marks.map((t) => t.upper)).toEqual(["DECLARE", "GO", "CREATE", "ALTER"]);
  });
  it("returns the same stream for the same text and dialect", () => {
    const a = getStream("SELECT 1", "tsql");
    expect(getStream("SELECT 1", "tsql")).toBe(a);
    expect(getStream("SELECT 1", "pg")).not.toBe(a);
  });
  it("clearTokenCache forces a re-lex", () => {
    const a = getStream("SELECT 1", "tsql");
    clearTokenCache();
    expect(getStream("SELECT 1", "tsql")).not.toBe(a);
  });
  it("keeps the four most recently used streams", () => {
    const a = getStream("a", "tsql");
    const b = getStream("b", "tsql");
    getStream("c", "tsql");
    getStream("d", "tsql");
    expect(getStream("a", "tsql")).toBe(a); // hit: moves a to the front
    getStream("e", "tsql"); // evicts b, the least recently used
    expect(getStream("a", "tsql")).toBe(a);
    expect(getStream("b", "tsql")).not.toBe(b);
  });
  it("incremental re-lex of a large pg script matches a fresh tokenization", () => {
    let text = Array.from({ length: 200 }, (_, i) => `SELECT o.order_id, 'v${i}'::text FROM orders o -- row ${i}`).join("\n");
    getStream(text, "pg");
    const edits: [number, number, string][] = [
      [3000, 0, "$$"], [6000, 0, "$$"], [100, 0, "/*"], [9000, 0, "*/"], [4500, 3, ""], [7000, 0, "E'\\'"],
    ];
    for (const [pos, del, ins] of edits) {
      text = text.slice(0, pos) + ins + text.slice(pos + del);
      const s = getStream(text, "pg");
      const got = [...s.code, ...s.comments].sort((x, y) => x.start - y.start);
      expect(shape(got)).toEqual(shape(tokenize(text, "pg")));
    }
  });
  it("incremental re-lex with CRLF line endings matches a fresh tokenization", () => {
    let text = Array.from({ length: 150 }, (_, i) => `SELECT [c${i}], N'x' FROM dbo.T${i}\r\nGO`).join("\r\n");
    getStream(text, "tsql");
    for (const [pos, del, ins] of [[2500, 0, "["], [2600, 0, "'"], [4000, 2, ""], [100, 0, "GO\r\n"]] as [number, number, string][]) {
      text = text.slice(0, pos) + ins + text.slice(pos + del);
      const s = getStream(text, "tsql");
      expect(shape([...s.code, ...s.comments].sort((x, y) => x.start - y.start))).toEqual(shape(tokenize(text, "tsql")));
      expect(s.marks.map((t) => t.start)).toEqual(tokenize(text, "tsql").filter((t) => t.kind === "go").map((t) => t.start));
    }
  });
});

describe("lexer: SQLite rules", () => {
  it("quotes names with \"…\", […] and `…`; strings with '…'; X'…' is a blob literal", () => {
    expect(kt(`SELECT "a""b", [c d], \`e\`\`f\`, 'it''s', X'0A1b' FROM t`, "sqlite")).toEqual([
      "keyword:SELECT", 'qident:"a""b"', "punct:,", "qident:[c d]", "punct:,", "qident:`e``f`", "punct:,", "string:'it''s'", "punct:,",
      "string:X'0A1b'", "keyword:FROM", "ident:t",
    ]);
    expect(tokenize("`e``f`", "sqlite")[0].value).toBe("e`f");
  });
  it("reads ? ?NNN :name @name $name as parameters", () => {
    expect(kt("WHERE a = ? AND b = ?2 AND c = :c AND d = @d AND e = $e", "sqlite").filter((t) => t.startsWith("param:"))).toEqual([
      "param:?", "param:?2", "param::c", "param:@d", "param:$e",
    ]);
  });
  it("block comments do not nest; operators include == -> ->> and ||", () => {
    expect(kt("/* a /* b */ x */", "sqlite")).toEqual(["comment:/* a /* b */", "ident:x", "operator:*", "operator:/"]);
    expect(kt("a == b || c -> d ->> e", "sqlite").filter((t) => t.startsWith("operator:"))).toEqual(["operator:==", "operator:||", "operator:->", "operator:->>"]);
  });
  it("has no GO separator and no @variables", () => {
    expect(kt("GO", "sqlite")).toEqual(["ident:GO"]);
    expect(tokenize("@x", "sqlite")[0].kind).toBe("param");
  });
});

describe("lexer: DuckDB rules", () => {
  it("reads $name, $1 and ? as parameters and $tag$…$tag$ as one string", () => {
    expect(kt("WHERE a = $name AND b = $1 AND c = ? AND d = $t$x;y$t$", "duckDb")).toEqual([
      "keyword:WHERE", "ident:a", "operator:=", "param:$name", "keyword:AND", "ident:b", "operator:=", "param:$1", "keyword:AND", "ident:c",
      "operator:=", "param:?", "keyword:AND", "ident:d", "operator:=", "string:$t$x;y$t$",
    ]);
  });
  it("block comments nest; operators include // :: -> == and **; [ ] are not names", () => {
    expect(kt("/* a /* b */ c */ x", "duckDb")).toEqual(["comment:/* a /* b */ c */", "ident:x"]);
    expect(kt("a // b :: c -> d == e ** f", "duckDb").filter((t) => t.startsWith("operator:"))).toEqual([
      "operator://", "operator:::", "operator:->", "operator:==", "operator:**",
    ]);
    expect(kt("[1, 2]", "duckDb")[0]).not.toMatch(/^qident/);
  });
  it("quotes names with \"…\" only; E'' strings take backslash escapes", () => {
    expect(kt(`SELECT "Mixed""Case", E'it\\'s', 'x''y'`, "duckDb")).toEqual([
      "keyword:SELECT", 'qident:"Mixed""Case"', "punct:,", "string:E'it\\'s'", "punct:,", "string:'x''y'",
    ]);
  });
});

describe("lexer: MySQL rules", () => {
  it("backticks quote names; '…' and \"…\" are strings with backslash escapes", () => {
    expect(kt(`SELECT \`a\`\`b\`, 'it\\'s', "say \\"hi\\"", 'x''y'`, "mySql")).toEqual([
      "keyword:SELECT", "qident:`a``b`", "punct:,", "string:'it\\'s'", "punct:,", 'string:"say \\"hi\\""', "punct:,", "string:'x''y'",
    ]);
  });
  it("# and '-- ' start line comments; --x does not; block comments do not nest", () => {
    expect(kt("SELECT 1 # note\nSELECT 2 -- note", "mySql")).toEqual(["keyword:SELECT", "number:1", "comment:# note", "keyword:SELECT", "number:2", "comment:-- note"]);
    expect(kt("SELECT 1--1", "mySql")).toEqual(["keyword:SELECT", "number:1", "operator:-", "operator:-", "number:1"]);
    expect(kt("/* a /* b */ x", "mySql")).toEqual(["comment:/* a /* b */", "ident:x"]);
  });
  it("@user and @@system variables; $ starts a name; <=> and := are operators", () => {
    expect(kt("SET @x := @@session_x + $total", "mySql")).toEqual(["keyword:SET", "variable:@x", "operator::=", "variable:@@session_x", "operator:+", "ident:$total"]);
    expect(kt("a <=> b", "mySql")).toEqual(["ident:a", "operator:<=>", "ident:b"]);
  });
  it("keeps T-SQL and PostgreSQL lexing as it was (no backticks there)", () => {
    expect(kt("`x`", "tsql")[0]).toBe("operator:`");
    expect(kt("#x", "pg")[0]).toBe("operator:#");
  });
});

// D4 S06: the sidecar's OracleTokenizer forms (OracleIntelligenceTests), read the same way here.
describe("lexer: Oracle rules", () => {
  it("reads alternative quoting q'X…X' and nq'X…X' as one string, whatever the delimiter, a lone ' and ; inside", () => {
    for (const lit of ["q'[it's; /]'", "q'{a}'", "q'(a)'", "q'<a>'", "q'!it's!'", "Q'#a'#'", "nq'[x]'", "NQ'|y|'", "q'.a.'"]) {
      expect(kt(`SELECT ${lit} FROM dual`, "oracle"), lit).toEqual(["keyword:SELECT", `string:${lit}`, "keyword:FROM", "ident:dual"]);
    }
  });

  it("an unterminated q'…' runs to the end, flagged open; q followed by a space is a name", () => {
    const t = tokenize("SELECT q'[never closed", "oracle")[1];
    expect([t.kind, t.end, t.open]).toEqual(["string", 22, true]);
    expect(kt("SELECT q 'a'", "oracle")).toEqual(["keyword:SELECT", "ident:q", "string:'a'"]);
  });

  it("binds :name, :1 and :\"name\"; := and => are operators", () => {
    expect(kt(`x := :a + :1 + :"Mixed" + :b#2`, "oracle")).toEqual([
      "ident:x", "operator::=", "param::a", "operator:+", "param::1", "operator:+", 'param::"Mixed"', "operator:+", "param::b#2",
    ]);
    expect(kt("f(p => 1)", "oracle")).toContain("operator:=>");
  });

  it("names hold # and $; 1..10 is a range; << >> are label operators; hints are comments", () => {
    expect(kt("FOR i IN 1..10 LOOP", "oracle")).toEqual(["keyword:FOR", "ident:i", "keyword:IN", "number:1", "operator:..", "number:10", "keyword:LOOP"]);
    expect(kt("SELECT t#1.c$x FROM t#1", "oracle")).toEqual(["keyword:SELECT", "ident:t#1", "punct:.", "ident:c$x", "keyword:FROM", "ident:t#1"]);
    expect(kt("<<outer>> BEGIN", "oracle")).toEqual(["operator:<<", "ident:outer", "operator:>>", "keyword:BEGIN"]);
    expect(kt("SELECT /*+ FULL(t) */ 1 --+ hint\nFROM dual", "oracle")).toEqual([
      "keyword:SELECT", "comment:/*+ FULL(t) */", "number:1", "comment:--+ hint", "keyword:FROM", "ident:dual",
    ]);
  });

  it("other dialects keep their lexing: q'…' is a name and a string there, # ends a name", () => {
    expect(kt("q'[x]'", "pg")[0]).toBe("ident:q");
    expect(kt("t#1", "pg")[0]).toBe("ident:t");
  });
});

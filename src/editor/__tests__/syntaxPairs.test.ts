import { describe, it, expect } from "vitest";
import { findInsertMatchingHighlight, findSyntaxPairHighlight } from "../syntaxPairs";

describe("findInsertMatchingHighlight", () => {
  const sql = "INSERT INTO dbo.Orders (OrderId, CustomerId, TotalAmount) VALUES (101, 202, 59.99);";

  it("highlights corresponding value when cursor is on a column", () => {
    const custIdOffset = sql.indexOf("CustomerId");
    const match = findInsertMatchingHighlight(sql, custIdOffset, "tsql");
    expect(match).not.toBeNull();
    expect(sql.slice(match!.col.start, match!.col.end)).toBe("CustomerId");
    expect(sql.slice(match!.val.start, match!.val.end)).toBe("202");
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

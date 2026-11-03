// MySQL user variables a script reads but never sets (the run's note instead of a parameter prompt), read from tokens.
import { describe, expect, it } from "vitest";
import { undeclaredParameters, unsetUserVariables } from "../sqlText";

const unset = (sql: string) => unsetUserVariables(sql, "mySql");

describe("unsetUserVariables (MySQL)", () => {
  it("a variable the script only reads is reported once, in first-appearance order", () => {
    expect(unset("SELECT * FROM t WHERE id = @id AND kind = @kind OR id = @id")).toEqual(["@id", "@kind"]);
    // MySQL has no parameter prompt for it either.
    expect(undeclaredParameters("SELECT @id", "mySql")).toEqual([]);
  });
  it("SET @x = …, SET … , @y = …, @x := … and INTO @x, @y all set a variable", () => {
    expect(unset("SET @a = 1; SELECT @a")).toEqual([]);
    expect(unset("SET @a = 1, @b = 2; SELECT @a + @b")).toEqual([]);
    expect(unset("SELECT @n := COUNT(*) FROM t; SELECT @n")).toEqual([]);
    expect(unset("SELECT a, b INTO @x, @y FROM t LIMIT 1; SELECT @x, @y")).toEqual([]);
    expect(unset("SET @a = @a + 1")).toEqual([]);
  });
  it("names compare case-insensitively, as MySQL's do", () => {
    expect(unset("SET @Total = 0; SELECT @total")).toEqual([]);
    expect(unset("SELECT @Total, @TOTAL")).toEqual(["@Total"]);
  });
  it("a comparison is not an assignment outside SET, and the right side of a SET is read", () => {
    expect(unset("SELECT * FROM t WHERE @a = 1")).toEqual(["@a"]);
    expect(unset("SET @b = @a")).toEqual(["@a"]);
    expect(unset("UPDATE t SET c = 1 WHERE @a = 2")).toEqual(["@a"]);
  });
  it("@@system variables, comments and strings are not user variables", () => {
    expect(unset("SELECT @@version, @@session.sql_mode")).toEqual([]);
    expect(unset("SELECT '@v' -- @w\n /* @x */")).toEqual([]);
  });
  it("assignments inside a routine body count", () => {
    expect(unset("CREATE PROCEDURE p() BEGIN SET @a = 1; SELECT @a; END")).toEqual([]);
  });
  it("other dialects have none (their parameters are prompted for)", () => {
    expect(unsetUserVariables("SELECT @v", "tsql")).toEqual([]);
    expect(unsetUserVariables("SELECT @v", "sqlite")).toEqual([]);
    expect(unsetUserVariables("SELECT :v", "pg")).toEqual([]);
    expect(unsetUserVariables(undefined as unknown as string, "mySql")).toEqual([]);
  });
});

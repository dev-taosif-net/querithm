import { describe, expect, it } from "vitest";
import { parseType, validateCellInput } from "../cellInput";
import type { Engine } from "../../ipc/contract";

const ok = (type: string, text: string, engine: Engine = "mssql") => expect(validateCellInput(type, text, engine)).toBeNull();
const bad = (type: string, text: string, engine: Engine = "mssql") => expect(validateCellInput(type, text, engine)).not.toBeNull();

describe("parseType", () => {
  it("splits names, arguments and arrays", () => {
    expect(parseType("nvarchar(50)")).toEqual({ name: "nvarchar", args: [50], array: false });
    expect(parseType("decimal(18, 2)")).toEqual({ name: "decimal", args: [18, 2], array: false });
    expect(parseType("character varying(20)[]")).toEqual({ name: "character varying", args: [20], array: true });
    expect(parseType("timestamp(3) without time zone")).toEqual({ name: "timestamp without time zone", args: [3], array: false });
  });
});

describe("validateCellInput", () => {
  it("integers with ranges, per engine type names", () => {
    ok("int", "-42");
    ok("integer", "2147483647", "pg");
    bad("int", "2147483648");
    bad("tinyint", "-1");
    bad("smallint", "1.5");
    expect(validateCellInput("int", "abc", "mssql")).toBe("Not a whole number (int)");
    ok("bigint", "9223372036854775807");
  });

  it("decimals fit precision and scale", () => {
    ok("decimal(10,2)", "12345678.99");
    expect(validateCellInput("decimal(5,2)", "1234.5", "mssql")).toBe("Does not fit decimal(5,2)");
    bad("numeric(4,1)", "1.25", "pg");
    ok("numeric", "1e10", "pg");
    ok("numeric", "NaN", "pg");
    bad("decimal(10,2)", "1,5");
  });

  it("booleans per engine", () => {
    ok("bit", "1");
    ok("bit", "false");
    bad("bit", "yes");
    ok("boolean", "yes", "pg");
    ok("bool", "t", "pg");
    bad("boolean", "maybe", "pg");
  });

  it("dates and times", () => {
    ok("date", "2026-10-21");
    bad("date", "2026-02-30");
    bad("date", "21/10/2026");
    ok("datetime2", "2026-10-21 13:05:06.1234567");
    ok("timestamp without time zone", "2026-10-21T13:05", "pg");
    bad("datetime2", "2026-10-21 25:00:00");
    ok("datetimeoffset", "2026-10-21 13:05:06 +05:30");
    ok("timestamp with time zone", "2026-10-21 13:05:06Z", "pg");
    bad("datetime", "2026-10-21 13:05:06 +01:00");
    ok("time(3)", "10:00:00.250");
    // SQL Server timestamp is rowversion (locked in the grid), not a date.
    ok("timestamp", "anything", "mssql");
  });

  it("guids, binary and text length", () => {
    ok("uniqueidentifier", "6F9619FF-8B86-D011-B42D-00C04FC964FF");
    ok("uuid", "{6f9619ff8b86d011b42d00c04fc964ff}", "pg");
    bad("uuid", "not-a-guid", "pg");
    ok("varbinary(16)", "0xDEADBEEF");
    bad("bytea", "DEADBEEF", "pg");
    expect(validateCellInput("nvarchar(3)", "abcd", "mssql")).toBe("Longer than 3 characters");
    ok("nvarchar(max)", "x".repeat(10000));
    ok("text", "anything", "pg");
    ok("integer[]", "{1,2}", "pg");
    ok("bytea", "0xDEADBEEF", "pg");
    bad("bytea", "0xABC", "pg"); // odd number of hex digits
    // PostgreSQL's own \x form for bytea is accepted there only.
    ok("bytea", "\\xDEADBEEF", "pg");
    ok("bytea", "\\x", "pg");
    bad("bytea", "\\xABC", "pg");
    bad("varbinary(4)", "\\xDEADBEEF", "mssql");
    // binary(n) / varbinary(n) hold n bytes; max has no limit here.
    ok("varbinary(4)", "0xDEADBEEF", "mssql");
    bad("varbinary(4)", "0xDEADBEEF00", "mssql");
    bad("binary(2)", "0x010203", "mssql");
    ok("varbinary(max)", "0x" + "00".repeat(9000), "mssql");
    ok("money", "-1234.5678", "mssql");
    bad("smallmoney", "not-a-number", "mssql");
    ok("double precision", "Infinity", "pg");
    bad("float", "Infinity", "mssql");
  });
});

describe("DuckDB cell values", () => {
  it("signed TINYINT, unsigned and 128-bit integers, NaN in floats and decimals, BIT as a bit string", () => {
    ok("TINYINT", "-128", "duckDb");
    bad("TINYINT", "128", "duckDb");
    ok("UTINYINT", "255", "duckDb");
    bad("UTINYINT", "-1", "duckDb");
    ok("UBIGINT", "18446744073709551615", "duckDb");
    ok("HUGEINT", "-170141183460469231731687303715884105728", "duckDb");
    bad("HUGEINT", "170141183460469231731687303715884105728", "duckDb");
    ok("DECIMAL(10,2)", "12.34", "duckDb");
    bad("DECIMAL(4,2)", "123.4", "duckDb");
    ok("DOUBLE", "1e300", "duckDb");
    ok("FLOAT", "NaN", "duckDb");
    ok("BIT", "0101", "duckDb");
    bad("BIT", "2", "duckDb");
    ok("TIMESTAMP", "2026-10-31 10:20:30.123456", "duckDb");
    ok("TIMESTAMPTZ", "2026-10-31 10:20:30 +06:00", "duckDb");
    bad("DATE", "31/10/2026", "duckDb");
    ok("UUID", "6f9619ff-8b86-d011-b42d-00c04fc964ff", "duckDb");
    ok("INTEGER[]", "[1, 2]", "duckDb");
  });
});

describe("SQLite and MySQL cell values", () => {
  it("SQLite: a declared type is only an affinity, so any text is a value (a STRICT table is checked by the server)", () => {
    for (const [type, text] of [["INTEGER", "abc"], ["REAL", "1,5"], ["DATE", "yesterday"], ["BLOB", "zz"], ["", "x"]]) ok(type, text, "sqlite");
  });
  it("MySQL: integer ranges and dates are checked as on the other servers; TIMESTAMP is a date and time", () => {
    bad("int", "12.5", "mySql");
    bad("tinyint", "300", "mySql");
    // Regression: MySQL's TINYINT is signed (-128..127); SQL Server's 0..255 range rejected -5 and accepted 200.
    ok("tinyint", "-5", "mySql");
    bad("tinyint", "200", "mySql");
    ok("decimal(5,2)", "123.45", "mySql");
    bad("decimal(5,2)", "nan", "mySql");
    ok("timestamp", "2026-10-31 12:00:00", "mySql");
    bad("timestamp", "noon", "mySql");
    bad("date", "31/10/2026", "mySql");
  });
});

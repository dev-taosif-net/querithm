// Cell-level validation of typed grid input: a value the column type cannot hold is rejected at the
// cell, not on Apply. The sidecar (and the server) still check everything; this only gives the error early.
// The text checked here is a single typed value, never SQL.
import type { Engine } from "../ipc/contract";

/** Value forms that differ by engine for the same type name. */
interface ValueRules {
  /** numeric/decimal and floating-point types take NaN and ±Infinity (PostgreSQL). */
  nanInfinity: boolean;
  /** money/smallmoney must be a plain number (SQL Server); PostgreSQL's money also reads currency text. */
  moneyIsNumber: boolean;
  /** `timestamp` is a date and time (PostgreSQL); SQL Server's is rowversion, locked before it reaches here. */
  timestampIsDateTime: boolean;
  /** bytea also takes PostgreSQL's own `\x` hex form (as PostgresDataEdit does). */
  byteaHex: boolean;
  /**
   * A column's type name does not limit its values (SQLite: a declared type is only an affinity unless the table is
   * STRICT), so nothing is rejected here; the server still refuses what a STRICT table cannot hold.
   */
  anyValue: boolean;
  /** Integer ranges that differ from the shared table below (signed TINYINT, unsigned and 128-bit integers). */
  intRanges?: Record<string, [bigint, bigint]>;
  /** BIT is a string of bits ('0101', DuckDB), not one bit. */
  bitString?: boolean;
}

const SIGNED_TINYINT: Record<string, [bigint, bigint]> = { tinyint: [-128n, 127n] };
const I128: [bigint, bigint] = [-(1n << 127n), (1n << 127n) - 1n];

const VALUE_RULES: Record<Engine, ValueRules> = {
  mssql: { nanInfinity: false, moneyIsNumber: true, timestampIsDateTime: false, byteaHex: false, anyValue: false },
  pg: { nanInfinity: true, moneyIsNumber: false, timestampIsDateTime: true, byteaHex: true, anyValue: false },
  sqlite: { nanInfinity: false, moneyIsNumber: true, timestampIsDateTime: true, byteaHex: false, anyValue: true },
  // MySQL's TIMESTAMP is a date and time; it has no money type and no NaN / Infinity. Its TINYINT is signed.
  mySql: { nanInfinity: false, moneyIsNumber: true, timestampIsDateTime: true, byteaHex: false, anyValue: false, intRanges: SIGNED_TINYINT },
  // DuckDB: signed TINYINT, unsigned and 128-bit integers, NaN / Infinity in floats and decimals, BIT as a bit string.
  duckDb: {
    nanInfinity: true, moneyIsNumber: true, timestampIsDateTime: true, byteaHex: false, anyValue: false, bitString: true,
    intRanges: {
      ...SIGNED_TINYINT, int1: [-128n, 127n], hugeint: I128, int128: I128, utinyint: [0n, 255n], usmallint: [0n, 65535n],
      uinteger: [0n, 4294967295n], ubigint: [0n, (1n << 64n) - 1n], uhugeint: [0n, (1n << 128n) - 1n],
    },
  },
};

const INT_RANGES: Record<string, [bigint, bigint]> = {
  tinyint: [0n, 255n],
  smallint: [-32768n, 32767n],
  int2: [-32768n, 32767n],
  int: [-2147483648n, 2147483647n],
  integer: [-2147483648n, 2147483647n],
  int4: [-2147483648n, 2147483647n],
  serial: [1n, 2147483647n],
  bigint: [-9223372036854775808n, 9223372036854775807n],
  int8: [-9223372036854775808n, 9223372036854775807n],
  bigserial: [1n, 9223372036854775807n],
  smallserial: [1n, 32767n],
};

/** "character varying(20)[]" → { name: "character varying", args: [20], array: true } */
export function parseType(type: string): { name: string; args: number[]; array: boolean } {
  const t = type.trim().toLowerCase();
  const array = t.endsWith("[]");
  const body = array ? t.slice(0, -2) : t;
  const open = body.indexOf("(");
  if (open < 0) return { name: body.trim(), args: [], array };
  const close = body.indexOf(")", open);
  const args = body
    .slice(open + 1, close < 0 ? undefined : close)
    .split(",")
    .map((s) => Number(s.trim()))
    .filter((n) => Number.isFinite(n));
  const suffix = close < 0 ? "" : body.slice(close + 1).trim();
  return { name: `${body.slice(0, open).trim()}${suffix ? ` ${suffix}` : ""}`.trim(), args, array };
}

const isDigits = (s: string) => s.length > 0 && [...s].every((c) => c >= "0" && c <= "9");

function isInteger(s: string) {
  const t = s.trim();
  return isDigits(t[0] === "-" || t[0] === "+" ? t.slice(1) : t);
}

function isDecimal(s: string) {
  let t = s.trim();
  if (t[0] === "-" || t[0] === "+") t = t.slice(1);
  const e = t.search(/[eE]/);
  const mantissa = e < 0 ? t : t.slice(0, e);
  if (e >= 0 && !isInteger(t.slice(e + 1))) return false;
  const [a, b, ...rest] = mantissa.split(".");
  if (rest.length) return false;
  if (b === undefined) return isDigits(a);
  return (a === "" || isDigits(a)) && (b === "" || isDigits(b)) && (a + b).length > 0;
}

function decimalFits(s: string, precision: number, scale: number) {
  const t = s.trim().replace(/^[-+]/, "");
  if (/[eE]/.test(t)) return true; // exponent form: let the server decide
  const [a, b = ""] = t.split(".");
  const intDigits = a.replace(/^0+/, "").length;
  return intDigits <= precision - scale && b.replace(/0+$/, "").length <= scale;
}

const pad2 = (n: string) => n.length === 2 && isDigits(n);

function isDate(s: string) {
  const [y, m, d] = s.trim().split("-");
  if (!(y?.length === 4 && isDigits(y) && pad2(m ?? "") && pad2(d ?? ""))) return false;
  const dt = new Date(Date.UTC(Number(y), Number(m) - 1, Number(d)));
  return dt.getUTCFullYear() === Number(y) && dt.getUTCMonth() === Number(m) - 1 && dt.getUTCDate() === Number(d);
}

function isTime(s: string) {
  const [hms, frac] = s.trim().split(".");
  const [h, mi, se = "00"] = hms.split(":");
  if (!(pad2(h ?? "") && pad2(mi ?? "") && pad2(se))) return false;
  if (frac !== undefined && !isDigits(frac)) return false;
  return Number(h) < 24 && Number(mi) < 60 && Number(se) < 60;
}

/** "yyyy-mm-dd[ T]hh:mm[:ss[.fff…]]" with an optional " +hh:mm" / "Z" offset. */
function isTimestamp(s: string, offset: boolean) {
  let t = s.trim();
  const m = /(?:\s?[+-]\d{2}:?\d{2}|Z)$/.exec(t);
  if (m) {
    if (!offset) return false;
    t = t.slice(0, m.index).trim();
  }
  const sep = t.search(/[ T]/);
  if (sep < 0) return isDate(t);
  return isDate(t.slice(0, sep)) && isTime(t.slice(sep + 1));
}

const UUID = /^\{?[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}\}?$/i;

/** Null when `text` is a valid value of `type` (catalog type display), else a short message for the cell. */
export function validateCellInput(type: string, text: string, engine: Engine): string | null {
  const rules = VALUE_RULES[engine];
  if (rules.anyValue) return null;
  const { name, args, array } = parseType(type);
  if (array) return null; // array literals are parsed by the server
  const range = rules.intRanges?.[name] ?? INT_RANGES[name];
  if (range) {
    if (!isInteger(text)) return `Not a whole number (${name})`;
    const v = BigInt(text.trim());
    if (v < range[0] || v > range[1]) return `Out of range for ${name} (${range[0]} to ${range[1]})`;
    return null;
  }
  switch (name) {
    case "decimal":
    case "numeric": {
      if (rules.nanInfinity && /^(nan|[-+]?infinity)$/i.test(text.trim())) return null;
      if (!isDecimal(text)) return `Not a number (${name})`;
      const [p, sc = 0] = args;
      if (p && !decimalFits(text, p, sc)) return `Does not fit ${name}(${p},${sc})`;
      return null;
    }
    case "money":
    case "smallmoney":
      return rules.moneyIsNumber && !isDecimal(text) ? `Not a number (${name})` : null;
    case "float":
    case "real":
    case "double precision":
    case "float4":
    case "float8":
      if (rules.nanInfinity && /^(nan|[-+]?infinity)$/i.test(text.trim())) return null;
      return isDecimal(text) ? null : `Not a number (${name})`;
    case "bit":
      if (rules.bitString) return /^[01]+$/.test(text.trim()) ? null : "Use a string of 0s and 1s";
      return /^(0|1|true|false)$/i.test(text.trim()) ? null : "Use 1 or 0";
    case "boolean":
    case "bool":
      return /^(t|f|true|false|1|0|y|n|yes|no|on|off)$/i.test(text.trim()) ? null : "Use true or false";
    case "date":
      return isDate(text) ? null : "Use yyyy-mm-dd";
    case "time":
    case "time without time zone":
      return isTime(text) ? null : "Use hh:mm:ss[.fff]";
    case "datetime":
    case "datetime2":
    case "smalldatetime":
    case "timestamp without time zone":
      // SQL Server "timestamp" is rowversion (locked), so only PostgreSQL reaches the timestamp names here.
      return isTimestamp(text, false) ? null : "Use yyyy-mm-dd hh:mm:ss[.fff]";
    case "timestamp":
      return rules.timestampIsDateTime ? (isTimestamp(text, false) ? null : "Use yyyy-mm-dd hh:mm:ss[.fff]") : null;
    case "datetimeoffset":
    case "timestamptz":
    case "timestamp with time zone":
      return isTimestamp(text, true) ? null : "Use yyyy-mm-dd hh:mm:ss[.fff] +hh:mm";
    case "uniqueidentifier":
    case "uuid":
      return UUID.test(text.trim()) ? null : "Not a GUID";
    case "binary":
    case "varbinary":
    case "image":
    case "bytea": {
      const pgHex = rules.byteaHex && name === "bytea";
      const t = text.trim();
      const prefix = /^0x/i.test(t) || (pgHex && /^\\x/i.test(t)) ? 2 : -1;
      if (prefix < 0 || !/^([0-9a-f]{2})*$/i.test(t.slice(prefix))) return pgHex ? "Use 0x or \\x followed by hex digits" : "Use 0x followed by hex digits";
      const n = args[0];
      return (name === "binary" || name === "varbinary") && n && (t.length - prefix) / 2 > n ? `Longer than ${n} bytes` : null;
    }
    case "char":
    case "nchar":
    case "varchar":
    case "nvarchar":
    case "character":
    case "character varying": {
      const n = args[0];
      return n && text.length > n ? `Longer than ${n} characters` : null;
    }
    default:
      return null;
  }
}

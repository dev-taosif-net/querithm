// JSON value → formatted lines with fold ranges (Phase C2), run in json.worker.ts. A scanner, not JSON.parse +
// stringify: numbers and strings keep their exact text (no rounding of big integers, no re-escaping), and an error
// says where the text stops being JSON.
import { LineBuilder, type StructuredView } from "./lines";
import { STRUCTURED_MAX_CHARS } from "./detect";

/** Deepest nesting the scanner follows; deeper values are shown raw with the reason. */
export const JSON_MAX_DEPTH = 512;

export type JsonViewResult =
  | {
      ok: true;
      view: StructuredView;
      /** A PostgreSQL EXPLAIN (FORMAT JSON) value: `[{"Plan": …}]`. */
      explainPlan: boolean;
      /** Which JSON plan form the value is, if any (Open as plan; see PLAN_FORMATS). */
      jsonPlan: JsonPlanFormat | null;
    }
  | { ok: false; error: string; line?: number; column?: number; tooLarge?: boolean };

class JsonSyntaxError extends Error {
  constructor(message: string, readonly at: number) {
    super(message);
  }
}

const WS = (c: number) => c === 0x20 || c === 0x09 || c === 0x0a || c === 0x0d;

function describe(s: string, i: number): string {
  if (i >= s.length) return "end of text";
  const ch = s[i];
  return ch.charCodeAt(0) < 0x20 ? `control character U+${ch.charCodeAt(0).toString(16).padStart(4, "0").toUpperCase()}` : `"${ch}"`;
}

class Scanner {
  i = 0;
  constructor(readonly s: string, readonly out: LineBuilder) {}

  fail(what: string): never {
    throw new JsonSyntaxError(what, this.i);
  }

  unexpected(expected: string): never {
    this.fail(`Unexpected ${describe(this.s, this.i)}, expected ${expected}`);
  }

  ws() {
    const s = this.s;
    while (this.i < s.length && WS(s.charCodeAt(this.i))) this.i++;
  }

  string(): string {
    const s = this.s;
    const start = this.i;
    this.i++; // opening quote
    for (;;) {
      if (this.i >= s.length) {
        this.i = start;
        this.fail("Unterminated string");
      }
      const c = s.charCodeAt(this.i);
      if (c === 0x22) {
        this.i++;
        return s.slice(start, this.i);
      }
      if (c === 0x5c) {
        const e = s[this.i + 1];
        if (e === "u") {
          for (let k = 2; k < 6; k++) {
            if (!/[0-9a-fA-F]/.test(s[this.i + k] ?? "")) {
              this.i += k;
              this.fail("Invalid \\u escape in a string");
            }
          }
          this.i += 6;
          continue;
        }
        if (e === undefined || !'"\\/bfnrt'.includes(e)) {
          this.fail(`Invalid escape ${e === undefined ? "at the end of the text" : `"\\${e}"`} in a string`);
        }
        this.i += 2;
        continue;
      }
      if (c < 0x20) this.fail(`Unescaped ${describe(s, this.i)} in a string`);
      this.i++;
    }
  }

  digits(): number {
    const s = this.s;
    const start = this.i;
    while (this.i < s.length && s.charCodeAt(this.i) >= 0x30 && s.charCodeAt(this.i) <= 0x39) this.i++;
    return this.i - start;
  }

  number(): string {
    const s = this.s;
    const start = this.i;
    if (s[this.i] === "-") this.i++;
    if (s[this.i] === "0") this.i++;
    else if (!this.digits()) this.unexpected("a digit");
    if (s[this.i] === ".") {
      this.i++;
      if (!this.digits()) this.unexpected("a digit after the decimal point");
    }
    if (s[this.i] === "e" || s[this.i] === "E") {
      this.i++;
      if (s[this.i] === "+" || s[this.i] === "-") this.i++;
      if (!this.digits()) this.unexpected("a digit in the exponent");
    }
    return s.slice(start, this.i);
  }

  value(depth: number, prefix: string) {
    if (depth > JSON_MAX_DEPTH) this.fail(`Nested deeper than ${JSON_MAX_DEPTH} levels`);
    this.ws();
    const s = this.s;
    const c = s[this.i];
    if (c === "{" || c === "[") {
      const close = c === "{" ? "}" : "]";
      this.i++;
      this.ws();
      if (s[this.i] === close) {
        this.i++;
        this.out.push(depth, `${prefix}${c}${close}`);
        return;
      }
      const open = this.out.push(depth, `${prefix}${c}`);
      for (;;) {
        this.ws();
        if (c === "{") {
          if (s[this.i] !== '"') this.unexpected("a property name in double quotes");
          const key = this.string();
          this.ws();
          if (s[this.i] !== ":") this.unexpected('":"');
          this.i++;
          this.value(depth + 1, `${key}: `);
        } else this.value(depth + 1, "");
        this.ws();
        if (s[this.i] === ",") {
          this.i++;
          this.out.appendToLast(",");
          continue;
        }
        if (s[this.i] === close) {
          this.i++;
          break;
        }
        this.unexpected(`"," or "${close}"`);
      }
      this.out.fold(open, this.out.push(depth, close));
      return;
    }
    if (c === '"') return void this.out.push(depth, prefix + this.string());
    if (c === "-" || (c >= "0" && c <= "9")) return void this.out.push(depth, prefix + this.number());
    for (const lit of ["true", "false", "null"]) {
      if (s.startsWith(lit, this.i)) {
        this.i += lit.length;
        return void this.out.push(depth, prefix + lit);
      }
    }
    this.unexpected("a value");
  }
}

function lineAndColumn(s: string, at: number): { line: number; column: number } {
  let line = 1;
  let lineStart = 0;
  for (let i = 0; i < at && i < s.length; i++) {
    if (s.charCodeAt(i) === 0x0a) {
      line++;
      lineStart = i + 1;
    }
  }
  return { line, column: at - lineStart + 1 };
}

/**
 * The JSON plan forms Querithm opens as a plan: PostgreSQL's EXPLAIN (FORMAT JSON) `[{"Plan": …}]`; the sidecar's
 * SQLite query plan `[{"text", "rows": [[id, parent, detail], …]}]` (format "sqlite-eqp"); and its MySQL plan
 * `[{"text", "plan": "<EXPLAIN FORMAT=JSON text>"}]` (format "mysql-json"); and its DuckDB plan
 * `[{"text", "actual", "plan": <EXPLAIN (FORMAT JSON) value>}]` (format "duckdb-json").
 */
export type JsonPlanFormat = "explainJson" | "sqliteEqp" | "mysqlJson" | "duckdbJson";

export function jsonPlanOf(text: string): JsonPlanFormat | null {
  if (!text.trimStart().startsWith("[")) return null;
  try {
    const v: unknown = JSON.parse(text);
    if (!Array.isArray(v) || !v.length || !v[0] || typeof v[0] !== "object" || Array.isArray(v[0])) return null;
    const first = v[0] as Record<string, unknown>;
    if ("Plan" in first) return "explainJson";
    if (typeof first.text !== "string") return null;
    if (Array.isArray(first.rows) && first.rows.every((r) => Array.isArray(r) && r.length === 3)) return "sqliteEqp";
    if (typeof first.plan === "string") return "mysqlJson";
    if (typeof first.actual === "boolean" && first.plan !== null && typeof first.plan === "object") return "duckdbJson";
    return null;
  } catch {
    return null;
  }
}

/** Formats a JSON text (two-space indent, one value per line) or says why it is not JSON. */
export function buildJsonView(text: string, maxChars = STRUCTURED_MAX_CHARS): JsonViewResult {
  if (text.length > maxChars) {
    return { ok: false, tooLarge: true, error: `The value is larger than ${Math.round(maxChars / 1024 / 1024)} MB, so it is shown as text without formatting.` };
  }
  const out = new LineBuilder();
  const sc = new Scanner(text, out);
  try {
    if (text.charCodeAt(0) === 0xfeff) sc.i = 1;
    sc.value(0, "");
    sc.ws();
    if (sc.i < text.length) sc.fail(`Unexpected ${describe(text, sc.i)} after the end of the JSON value`);
  } catch (e) {
    if (!(e instanceof JsonSyntaxError)) throw e;
    const at = lineAndColumn(text, e.at);
    return { ok: false, error: `${e.message} at line ${at.line}, column ${at.column}`, ...at };
  }
  const jsonPlan = jsonPlanOf(text);
  return { ok: true, view: out.build(), explainPlan: jsonPlan === "explainJson", jsonPlan };
}

// What a tab's SQL is about, read from tokens (§15 rule 6: never regex over query text): the first statement as one
// line (the third line of a Ctrl+P row), and a verb-first title for an untitled tab ("UPDATE Invoice", D24). Uses the
// uncached tokenizer on the first few KB only, so it never displaces the editor's token stream.
import type { Dialect } from "../../ipc/contract";
import { tokenize, type Token } from "./lexer";
import { DIALECTS, knownDialect } from "../../engines";

/** Only the start of a tab's text is read: a title or a preview never needs more. */
export const STATEMENT_SCAN_CHARS = 8192;

const norm = knownDialect;
const isDot = (t: Token | undefined) => !!t && (t.kind === "punct" || t.kind === "operator") && t.value === ".";
const isPunctV = (t: Token | undefined, v: string) => !!t && t.kind === "punct" && t.value === v;
const isName = (t: Token | undefined) => !!t && (t.kind === "ident" || t.kind === "qident");
const word = (t: Token | undefined) => (t && (t.kind === "keyword" || t.kind === "ident") ? t.upper : "");

function codeTokens(text: string, dialect: Dialect): Token[] {
  return tokenize(text.slice(0, STATEMENT_SCAN_CHARS), norm(dialect)).filter((t) => t.kind !== "comment");
}

/** Statements as token ranges: split at top-level ";" and GO. */
function statements(tokens: Token[]): Token[][] {
  const out: Token[][] = [];
  let cur: Token[] = [];
  let depth = 0;
  for (const t of tokens) {
    if (t.kind === "go" || (depth === 0 && isPunctV(t, ";"))) {
      if (cur.length) out.push(cur);
      cur = [];
      continue;
    }
    if (isPunctV(t, "(")) depth++;
    else if (isPunctV(t, ")")) depth = Math.max(0, depth - 1);
    cur.push(t);
  }
  if (cur.length) out.push(cur);
  return out;
}

/** The first statement on one line: tokens that touch in the source still touch; any gap becomes one space. */
export function firstStatementLine(text: string, dialect: Dialect, max = 160): string {
  const first = statements(codeTokens(text, dialect))[0];
  if (!first) return "";
  let line = "";
  let prev: Token | undefined;
  for (const t of first) {
    const tight = !prev || t.start === prev.end;
    line += (tight ? "" : " ") + text.slice(t.start, t.end);
    prev = t;
    if (line.length > max) return `${line.slice(0, max - 1)}…`;
  }
  return line;
}

/**
 * A dotted name starting at `i`: its parts (unquoted) and the index after it. After a dot any word is a name part
 * ("stage.Load": LOAD is a keyword), spelled as written.
 */
function nameAt(ts: Token[], i: number, src: string): { parts: string[]; next: number } | undefined {
  if (!isName(ts[i])) return undefined;
  const parts = [ts[i].value];
  let j = i + 1;
  while (isDot(ts[j]) && (isName(ts[j + 1]) || ts[j + 1]?.kind === "keyword")) {
    const t = ts[j + 1];
    parts.push(t.kind === "keyword" ? src.slice(t.start, t.end) : t.value);
    j += 2;
  }
  return { parts, next: j };
}

/** "Orders" in the engine's default schema, "sales.Orders" elsewhere; a database prefix is dropped. */
function label(parts: string[], dialect: Dialect): string {
  const tail = parts.slice(-2);
  if (tail.length === 2 && tail[0].toLowerCase() === DIALECTS[norm(dialect)].defaultSchema) return tail[1];
  return tail.join(".");
}

const SKIP = new Set(["BEGIN", "SET", "USE", "DECLARE", "PRINT", "COMMIT", "ROLLBACK", "SAVE", "IF", "RAISERROR", "THROW", "START"]);
const KINDS = new Set(["TABLE", "VIEW", "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "INDEX", "SCHEMA", "SEQUENCE", "TYPE", "SYNONYM", "MATERIALIZED", "DATABASE"]);

/** The object a statement acts on, from its verb's grammar; undefined when it names none (SELECT 1). */
function target(ts: Token[], verb: string, src: string): string[] | undefined {
  let i = 1;
  const skipWords = (...ws: string[]) => {
    while (ws.includes(word(ts[i]))) i++;
  };
  switch (verb) {
    case "SELECT": {
      let depth = 0;
      for (let k = 1; k < ts.length; k++) {
        if (isPunctV(ts[k], "(")) depth++;
        else if (isPunctV(ts[k], ")")) depth--;
        else if (depth === 0 && word(ts[k]) === "FROM") return nameAt(ts, k + 1, src)?.parts;
      }
      return undefined;
    }
    case "INSERT":
    case "MERGE":
      skipWords("INTO");
      return nameAt(ts, i, src)?.parts;
    case "DELETE":
      skipWords("FROM");
      return nameAt(ts, i, src)?.parts;
    case "UPDATE":
      return nameAt(ts, i, src)?.parts;
    case "TRUNCATE":
      skipWords("TABLE");
      return nameAt(ts, i, src)?.parts;
    case "EXEC":
    case "EXECUTE":
      // EXEC @rc = dbo.usp_x
      if (ts[i]?.kind === "variable" && ts[i + 1]?.kind === "operator" && ts[i + 1].value === "=") i += 2;
      return nameAt(ts, i, src)?.parts;
    case "CREATE":
    case "ALTER":
    case "DROP":
      skipWords("OR", "ALTER", "REPLACE", "UNIQUE", "CLUSTERED", "NONCLUSTERED", "MATERIALIZED");
      if (!KINDS.has(word(ts[i]))) return undefined;
      i++;
      skipWords("IF", "NOT", "EXISTS");
      return nameAt(ts, i, src)?.parts;
  }
  return undefined;
}

/**
 * A verb-first title from the first statement that does something ("UPDATE Invoice", "SELECT sales.Orders",
 * "EXEC usp_PostOrders"), skipping BEGIN TRAN, SET, USE, DECLARE and the like; a CTE takes the verb of its main
 * statement. Undefined when no statement names an object.
 */
export function statementTitle(text: string, dialect: Dialect): string | undefined {
  const src = text.slice(0, STATEMENT_SCAN_CHARS);
  for (const ts of statements(codeTokens(text, dialect))) {
    let verb = word(ts[0]);
    if (SKIP.has(verb)) continue;
    let body = ts;
    if (verb === "WITH") {
      // The main statement follows the CTE definitions: the first top-level DML word.
      let depth = 0;
      const k = ts.findIndex((t, n) => {
        if (isPunctV(t, "(")) depth++;
        else if (isPunctV(t, ")")) depth--;
        return n > 0 && depth === 0 && ["SELECT", "INSERT", "UPDATE", "DELETE", "MERGE"].includes(word(t));
      });
      if (k < 0) return undefined;
      body = ts.slice(k);
      verb = word(body[0]);
    }
    const parts = target(body, verb, src);
    if (!parts) return undefined;
    return `${verb === "EXECUTE" ? "EXEC" : verb} ${label(parts, dialect)}`;
  }
  return undefined;
}

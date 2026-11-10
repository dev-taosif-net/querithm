// Oracle statement boundaries and bind variables in the webview (D4 S06), on tier-1 tokens: a port of the sidecar's
// OracleSplitter and OracleLanguageService.Binds, held to the same answers by one fixture both suites read
// (__tests__/fixtures/oracle-statements.json; OracleEditorSupportTests on the .NET side). The sidecar still decides what
// runs: this only places the cursor's statement (Execute current statement) and asks for the parameters a run needs.
import { getStream, type Token } from "./lexer";

export type OracleStatementKind = "sql" | "plsql" | "client";

export interface OracleStatement {
  kind: OracleStatementKind;
  /** The text the driver gets: a SQL statement without its `;`, a PL/SQL unit with its final `END;`, no `/` line. */
  start: number;
  end: number;
  /** Code tokens (no comments) of the statement, terminator excluded. */
  code: Token[];
}

/** SQL*Plus commands: the first word of a line, before any statement text, makes the line a client command. */
const CLIENT_WORDS = new Set(`ACCEPT ACC APPEND ARCHIVE ATTRIBUTE BREAK BRE BTITLE CLEAR CL COLUMN COL COMPUTE COMP CONNECT CONN COPY
DEFINE DEF DESCRIBE DESC DISCONNECT DISC EDIT ED EXECUTE EXEC EXIT QUIT GET HELP HOST HO INPUT LIST PASSWORD PAUSE PRINT PROMPT
PRO RECOVER REMARK REM REPFOOTER REPHEADER RUN SAVE SET SHOW SHO SHUTDOWN SPOOL SPO START STA STARTUP STORE TIMING TTITLE UNDEFINE
VARIABLE VAR WHENEVER XQUERY`.split(/\s+/));
/** `SET x` forms that are SQL, not SQL*Plus. */
const SQL_SET_WORDS = new Set(["TRANSACTION", "ROLE", "CONSTRAINT", "CONSTRAINTS"]);
const PLSQL_UNITS = new Set(["PROCEDURE", "FUNCTION", "PACKAGE", "TRIGGER", "LIBRARY", "JAVA"]);
const CREATE_MODIFIERS = new Set(["EDITIONABLE", "NONEDITIONABLE", "EDITIONING", "FORCE", "NOFORCE", "SHARING", "AND", "RESOLVE", "COMPILE", "NO", "OR"]);

const isWord = (t: Token | undefined, w?: string) => !!t && (t.kind === "keyword" || t.kind === "ident") && (w === undefined || t.upper === w);
const isPunct = (t: Token, v: string) => t.kind === "punct" && t.value === v;

/** A position with only spaces, tabs and carriage returns between it and the start of its line. */
function startsLine(text: string, offset: number): boolean {
  for (let i = offset - 1; i >= 0; i--) {
    const c = text[i];
    if (c === "\n") return true;
    if (c !== " " && c !== "\t" && c !== "\r") return false;
  }
  return true;
}

function endsLine(text: string, offset: number): boolean {
  for (let i = offset; i < text.length; i++) {
    const c = text[i];
    if (c === "\n") return true;
    if (c !== " " && c !== "\t" && c !== "\r") return false;
  }
  return true;
}

/** The unit kind from its first words: anonymous blocks, stored PL/SQL, `TYPE BODY`, `WITH FUNCTION` (OracleSplitter.IsPlSql). */
export function isPlSql(code: Token[]): boolean {
  if (!code.length) return false;
  const first = code[0];
  if (isWord(first, "DECLARE") || isWord(first, "BEGIN") || (first.kind === "operator" && first.value === "<<")) return true;
  if (isWord(first, "WITH")) return isWord(code[1], "FUNCTION") || isWord(code[1], "PROCEDURE");
  if (!isWord(first, "CREATE")) return false;
  let i = 1;
  if (isWord(code[i], "OR") && isWord(code[i + 1], "REPLACE")) i += 2;
  while (i < code.length && isWord(code[i]) && CREATE_MODIFIERS.has(code[i].upper)) i++;
  if (i >= code.length) return false;
  if (isWord(code[i], "TYPE")) return isWord(code[i + 1], "BODY");
  return isWord(code[i]) && PLSQL_UNITS.has(code[i].upper);
}

function isClientStart(T: Token[], i: number): boolean {
  const t = T[i];
  if (t.kind === "operator" && (t.value[0] === "@" || t.value[0] === "!")) return true;
  if (!isWord(t) || !CLIENT_WORDS.has(t.upper)) return false;
  if (t.upper === "SET") {
    const next = T[i + 1];
    return !(next && isWord(next) && SQL_SET_WORDS.has(next.upper));
  }
  return true;
}

/** The statements of an Oracle script, as SQL*Plus and the sidecar draw them. Never throws. */
export function oracleStatements(text: string): OracleStatement[] {
  const src = typeof text === "string" ? text : "";
  const T = getStream(src, "oracle").code;
  const out: OracleStatement[] = [];
  let code: Token[] = [];
  let plsql: boolean | null = null;
  const close = () => {
    if (code.length) {
      const kind = (plsql ??= isPlSql(code)) ? "plsql" : "sql";
      out.push({ kind, start: code[0].start, end: code[code.length - 1].end, code });
    }
    code = [];
    plsql = null;
  };
  for (let i = 0; i < T.length; i++) {
    const t = T[i];
    if (t.kind === "operator" && t.value === "/" && startsLine(src, t.start) && endsLine(src, t.end)) {
      close();
      continue;
    }
    if (!code.length && startsLine(src, t.start) && isClientStart(T, i)) {
      // To the end of its line; a trailing "-" continues it on the next one.
      let end = t.start;
      for (;;) {
        const nl = src.indexOf("\n", end);
        const lineEnd = nl < 0 ? src.length : nl;
        end = lineEnd;
        if (src.slice(t.start, lineEnd).trimEnd().endsWith("-") && nl >= 0) { end = nl + 1; continue; }
        break;
      }
      let raw = src.slice(t.start, end).trimEnd();
      if (raw.endsWith(";")) raw = raw.slice(0, -1).trimEnd();
      const own: Token[] = [];
      let k = i;
      while (k < T.length && T[k].start < end) own.push(T[k++]);
      out.push({ kind: "client", start: t.start, end: t.start + raw.length, code: own });
      i = k - 1;
      continue;
    }
    if (isPunct(t, ";")) {
      plsql ??= isPlSql(code);
      if (!plsql) { close(); continue; }
    }
    code.push(t);
  }
  close();
  return out;
}

/** A bind's dictionary spelling: `:emp` → `EMP`, `:"Emp"` → `Emp`, `:1` → `1` (OracleIdentifiers.Identity). */
export function bindIdentity(token: string): string {
  const name = token.startsWith(":") ? token.slice(1) : token;
  if (name.startsWith('"')) return name.slice(1, name.endsWith('"') && name.length > 1 ? -1 : undefined);
  return name.toUpperCase();
}

/**
 * The bind variables a script takes, each once in order of first use, as written the first time: SQL*Plus commands and
 * stored PL/SQL (`CREATE …`, where a trigger's `:NEW` / `:OLD` are row records) take none.
 */
export function oracleBinds(text: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const st of oracleStatements(text)) {
    if (st.kind === "client" || isWord(st.code[0], "CREATE")) continue;
    for (const t of st.code) {
      if (t.kind !== "param") continue;
      const id = bindIdentity(t.value);
      if (seen.has(id)) continue;
      seen.add(id);
      out.push(t.value);
    }
  }
  return out;
}

/**
 * The statement Execute current statement runs for the cursor at `offset`: the one holding it (or right after its `;`);
 * in the space between statements, one starting later on the cursor's line, else the one before.
 */
export function oracleStatementAt(text: string, offset: number): { start: number; end: number } | null {
  const list = oracleStatements(text);
  const src = typeof text === "string" ? text : "";
  let before: OracleStatement | null = null;
  for (const st of list) {
    // Right after its ; still belongs to the statement it ends (as on the other engines).
    if (offset >= st.start && (offset <= st.end || src.slice(st.end, offset) === ";")) return { start: st.start, end: st.end };
    if (st.start > offset) {
      if (!src.slice(offset, st.start).includes("\n")) return { start: st.start, end: st.end };
      break;
    }
    before = st;
  }
  return before ? { start: before.start, end: before.end } : null;
}

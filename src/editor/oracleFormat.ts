// Formatting Oracle scripts (D4 S06): sql-formatter's PL/SQL language, offline like the other local formatters, applied
// only to plain SQL statements. Statement boundaries come from oracleStatements (the sidecar's splitter, on tokens), so:
//  - a / alone on its line stays alone on its line (sql-formatter alone glued it to the statement: `dual /`, which is
//    division, and the next unit then ran together with this one);
//  - PL/SQL units (blocks, CREATE PROCEDURE / PACKAGE / TRIGGER …, WITH FUNCTION) and SQL*Plus lines are kept as written —
//    sql-formatter splits `CREATE OR REPLACE` and re-flows bodies, and a body is code the user laid out;
//  - comments and blank lines between statements are kept; a statement's own comments go through sql-formatter, which
//    keeps them. q'…' literals are one string token in both splitters and in sql-formatter's plsql language.
import { oracleStatements } from "./completion/oracleStatements";

export type FormatSql = (sql: string) => string;

/** The script with each SQL statement formatted by `format`; the text unchanged with the reason when one cannot be. */
export function formatOracleScript(text: string, format: FormatSql): { text: string; error?: string | null } {
  const src = typeof text === "string" ? text : "";
  const parts: string[] = [];
  let at = 0;
  try {
    for (const st of oracleStatements(src)) {
      parts.push(between(src.slice(at, st.start), at === 0));
      parts.push(st.kind === "sql" ? format(src.slice(st.start, st.end)).trim() : src.slice(st.start, st.end));
      at = st.end;
    }
  } catch (e) {
    return { text: src, error: e instanceof Error ? e.message : String(e) };
  }
  parts.push(src.slice(at));
  return { text: parts.join("") };
}

/**
 * The text between two statements: only whitespace, terminators (`;`, a `/` line) and comments. A bare `;` or `/`
 * between statements gets one blank line after it (as the other engines' formatters leave); anything else is kept.
 */
function between(gap: string, first: boolean): string {
  if (first) return gap;
  const t = gap.trim();
  if (t === ";") return ";\n\n";
  if (t === "/") return "\n/\n\n";
  return gap;
}

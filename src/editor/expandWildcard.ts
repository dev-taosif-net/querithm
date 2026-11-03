import { sqlRegionAtOffset } from "./completion/sqlRegion";
import type { Dialect } from "../ipc/contract";
import type { SchemaIndex } from "./completion/types";
import { getStream, type Token } from "./completion/lexer";
import { resolveTable } from "./completion/schemaIndex";
import { quoteIdentifier as q } from "./completion/quoteIdentifier";
import { lastStartingBefore, scan, W } from "./completion/scanner";
import { DIALECTS } from "../engines";

export interface WildcardExpansion {
  start: number;
  end: number;
  text: string;
}

function closeParen(T: Token[], open: number): number {
  let depth = 0;
  for (let i = open; i < T.length; i++) {
    if (T[i].value === "(") depth++;
    else if (T[i].value === ")" && --depth === 0) return i;
  }
  return -2;
}

/** First projection token after SELECT [ALL | DISTINCT [ON (…)]] [TOP n | TOP (n) [PERCENT] [WITH TIES]]. */
function projectionStart(T: Token[], select: number): number {
  let i = select + 1;
  if (W(T[i]) === "ALL") i++;
  else if (W(T[i]) === "DISTINCT") {
    i++;
    if (W(T[i]) === "ON" && T[i + 1]?.value === "(") i = closeParen(T, i + 1) + 1;
  }
  if (W(T[i]) === "TOP") {
    i++;
    i = T[i]?.value === "(" ? closeParen(T, i) + 1 : i + 1;
    if (W(T[i]) === "PERCENT") i++;
    if (W(T[i]) === "WITH" && W(T[i + 1]) === "TIES") i += 2;
  }
  return i;
}

/**
 * Expands an asterisk wildcard (`*` or `<alias>.*`) at or near `offset` in a SELECT clause
 * to the comma-separated list of column names for tables in scope.
 */
function expandWildcardAtOffsetInRegion(
  text: string,
  offset: number,
  dialect: Dialect,
  schema: SchemaIndex | null,
): WildcardExpansion | null {
  const stream = getStream(text, dialect);
  const T = stream.code;
  if (!T.length) return null;
  // PostgreSQL: unquoted names fold to lower case and then match exactly; T-SQL names match case-insensitively.
  const folds = DIALECTS[dialect].foldsIdentifiers;

  // Find token at or touching offset
  let idx = lastStartingBefore(T, offset + 1);
  if (idx < 0) return null;
  let tok = T[idx];

  // If offset is just before the asterisk
  if (tok.kind !== "operator" || tok.value !== "*") {
    if (idx + 1 < T.length && T[idx + 1].kind === "operator" && T[idx + 1].value === "*" && offset >= T[idx + 1].start && offset <= T[idx + 1].end) {
      idx = idx + 1;
      tok = T[idx];
    } else {
      return null;
    }
  }

  if (offset < tok.start || offset > tok.end) return null;

  // Check if qualified by an alias or table name (e.g., `o.*`)
  let qualifier: string | undefined;
  let spanStart = tok.start;
  const spanEnd = tok.end;

  if (idx >= 2 && T[idx - 1].kind === "punct" && T[idx - 1].value === ".") {
    const qTok = T[idx - 2];
    if (qTok.kind === "ident" || qTok.kind === "qident") {
      qualifier = folds && qTok.kind !== "qident" ? qTok.value.toLowerCase() : qTok.value;
      spanStart = qTok.start;
    }
  }

  const first = qualifier ? idx - 2 : idx;
  const after = T[idx + 1];
  // A star must be the entire projection item, never multiplication or a function argument.
  if (!T[first - 1]) return null;
  if (after && after.value !== "," && W(after) !== "FROM") return null;
  const ctx = scan(text, spanStart, dialect);
  if (ctx.clause !== "select") {
    return null;
  }

  // The completion scanner also collects references in CTEs and nested queries.
  // Expansion must use only the SELECT containing this star.
  let select = first - 1, backDepth = 0;
  for (; select >= 0; select--) {
    const t = T[select];
    if (t.value === ")") backDepth++;
    else if (t.value === "(") { if (!backDepth) return null; backDepth--; }
    else if (!backDepth && t.upper === "SELECT") break;
    else if (!backDepth && (t.value === ";" || t.kind === "go")) return null;
  }
  if (select < 0) return null;
  if (T[first - 1].value !== "," && first !== projectionStart(T, select)) return null;
  const visibleOffsets = new Set<number>();
  let depth = 0;
  let mergedJoin = false;
  for (let i = select + 1; i < T.length; i++) {
    const t = T[i];
    if (depth === 0 && (t.value === ")" || t.value === ";" || t.kind === "go" || ["UNION", "EXCEPT", "INTERSECT"].includes(t.upper))) break;
    if (depth === 0) {
      visibleOffsets.add(t.start);
      if (t.upper === "USING" || t.upper === "NATURAL") mergedJoin = true;
    }
    if (t.value === "(") depth++;
    if (t.value === ")") depth--;
  }
  const tables = ctx.tablesInScope.filter((t) => t.offset != null && visibleOffsets.has(t.offset)).map((t) => {
    if (!folds) return t;
    let i = lastStartingBefore(T, t.offset! + 1);
    const name = (index: number) => T[index].kind === "qident" ? T[index].value : T[index].value.toLowerCase();
    const parts: string[] = [];
    if (!t.isDerived) {
      parts.push(name(i++));
      while (T[i]?.value === "." && T[i + 1]) { parts.push(name(i + 1)); i += 2; }
    }
    if (T[i]?.value === "(") {
      let depth = 0;
      do { if (T[i].value === "(") depth++; if (T[i].value === ")") depth--; i++; } while (i < T.length && depth);
    }
    if (T[i]?.upper === "AS") i++;
    const alias = t.alias && T[i] ? name(i) : undefined;
    return { ...t, name: parts.at(-1) ?? t.name, schema: parts.length > 1 ? parts.at(-2) : t.schema, alias };
  });
  if (!tables.length) return null;
  let targetTables = tables;
  if (qualifier) {
    const qLower = qualifier.toLowerCase();
    targetTables = targetTables.filter(
      (t) => folds ? (t.alias ?? t.name) === qualifier : (t.alias ?? t.name).toLowerCase() === qLower,
    );
    if (!targetTables.length) return null;
  }

  // USING/NATURAL joins merge output columns; expanding both tables would change the result shape.
  if (!qualifier && mergedJoin) return null;
  const multipleTables = tables.length > 1 || !!qualifier;
  const formattedCols: string[] = [];

  for (const table of targetTables) {
    let colNames: string[] = [];

    if (table.columns?.length) {
      colNames = table.columns;
      // The tolerant scanner does not retain quote provenance for projected names.
      if (folds && colNames.some((name) => name !== name.toLowerCase())) return null;
    } else if (schema) {
      let match = resolveTable(schema, table);
      if (folds) {
        const candidates = (schema.byName.get(table.name.toLowerCase()) ?? []).filter((o) =>
          o.name === table.name && (table.schema === undefined || o.schema === table.schema));
        const defaults = candidates.filter((o) => o.schema === schema.defaultSchema);
        const matches = table.schema === undefined && defaults.length ? defaults : candidates;
        match = matches.length === 1 && (!table.database || table.database === schema.database) ? matches[0] : undefined;
      }
      if (match?.columns?.length) {
        colNames = match.columns.map((c) => c.name);
      }
    }

    if (!colNames.length || colNames.some((c) => c === "*")) return null;

    const prefix = multipleTables ? `${q(table.alias ?? table.name, dialect)}.` : "";
    for (const c of colNames) {
      formattedCols.push(`${prefix}${q(c, dialect)}`);
    }
  }

  if (!formattedCols.length) return null;

  return {
    start: spanStart,
    end: spanEnd,
    text: formattedCols.join(", "),
  };
}


export function expandWildcardAtOffset(text: string, offset: number, dialect: Dialect, schema: SchemaIndex | null): WildcardExpansion | null {
  const region = sqlRegionAtOffset(text, offset, dialect);
  const result = expandWildcardAtOffsetInRegion(region.text, region.offset, dialect, schema);
  return result ? { ...result, start: result.start + region.base, end: result.end + region.base } : null;
}

import { sqlRegionAtOffset } from "./completion/sqlRegion";
import type { Dialect } from "../ipc/contract";
import type { SchemaIndex } from "./completion/types";
import { getStream } from "./completion/lexer";
import { lastStartingBefore, scan, W } from "./completion/scanner";
import { DIALECTS, foldIdentifier } from "../engines";

export interface Tier1SignatureResult {
  label: string;
  documentation?: string;
  parameters: { label: string; documentation?: string }[];
  activeParameter: number;
}

function tier1SignatureHelpInRegion(
  text: string,
  offset: number,
  dialect: Dialect,
  schema: SchemaIndex | null,
): Tier1SignatureResult | null {
  if (!schema || !schema.objects.length) return null;
  const stream = getStream(text, dialect);
  const T = stream.code;
  if (!T.length) return null;
  // PostgreSQL / Oracle: unquoted names fold (lower / upper case) and then match exactly; T-SQL names match case-insensitively.
  const folds = DIALECTS[dialect].identifierFold !== null;

  const curIdx = lastStartingBefore(T, offset);
  if (curIdx < 0) return null;

  // Scenario 1: Inside parentheses (function call)
  let openParenIdx = -1;
  let parenDepth = 0;
  let commaCount = 0;
  let argumentStart = -1;

  for (let i = curIdx; i >= 0; i--) {
    const t = T[i];
    if (t.kind === "go" || (t.kind === "punct" && t.value === ";")) break;
    if (t.kind === "punct" && t.value === ")") {
      parenDepth++;
    } else if (t.kind === "punct" && t.value === "(") {
      if (parenDepth === 0) {
        openParenIdx = i;
        if (argumentStart < 0) argumentStart = i + 1;
        break;
      }
      parenDepth--;
    } else if (parenDepth === 0 && t.kind === "punct" && t.value === "," && t.start < offset) {
      commaCount++;
      if (argumentStart < 0) argumentStart = i + 1;
    }
  }

  let targetName: string | undefined;
  let targetSchema: string | undefined;

  if (openParenIdx > 0) {
    const prev = T[openParenIdx - 1];
    if (prev.kind === "ident" || prev.kind === "qident" || prev.kind === "keyword") {
      targetName = folds ? foldIdentifier(prev.value, prev.kind === "qident", dialect) : prev.value;
      if (openParenIdx >= 3 && T[openParenIdx - 2].kind === "punct" && T[openParenIdx - 2].value === ".") {
        const schemaTok = T[openParenIdx - 3];
        if (schemaTok.kind === "ident" || schemaTok.kind === "qident") {
          targetSchema = folds ? foldIdentifier(schemaTok.value, schemaTok.kind === "qident", dialect) : schemaTok.value;
          if (T[openParenIdx - 4]?.value === ".") return null;
        }
      }
    }
  }

  // Scenario 2: Procedure call after EXEC / EXECUTE / CALL
  if (!targetName) {
    const ctx = scan(text, offset, dialect);
    if (ctx.clause !== "exec" && ctx.clause !== "execArgs") return null;
    let execKwIdx = -1;
    for (let i = curIdx; i >= 0 && i >= curIdx - 50; i--) {
      const u = W(T[i]);
      if (u === "EXEC" || u === "EXECUTE" || u === "CALL") {
        execKwIdx = i;
        break;
      }
      if (T[i].kind === "punct" && T[i].value === ";") break;
    }

    if (execKwIdx >= 0) {
      let j = execKwIdx + 1;
      // Skip return variable `@res =`
      if (T[j]?.kind === "variable" && T[j + 1]?.kind === "operator" && T[j + 1]?.value === "=") {
        j += 2;
      }
      if (T[j] && (T[j].kind === "ident" || T[j].kind === "qident")) {
        let chainEnd = j;
        const value = (index: number) => folds ? foldIdentifier(T[index].value, T[index].kind === "qident", dialect) : T[index].value;
        const parts: string[] = [value(j)];
        while (T[chainEnd + 1]?.kind === "punct" && T[chainEnd + 1]?.value === "." && T[chainEnd + 2]) {
          parts.push(value(chainEnd + 2));
          chainEnd += 2;
        }
        if (parts.length > 2) return null;
        if (parts.length > 1) {
          targetSchema = parts[0];
          targetName = parts[1];
        } else {
          targetName = parts[0];
        }

        // Count commas between chainEnd and offset
        commaCount = 0;
        argumentStart = chainEnd + 1;
        let pDepth = 0;
        for (let x = chainEnd + 1; x <= curIdx; x++) {
          const t = T[x];
          if (t.start >= offset) break;
          if (t.kind === "punct" && t.value === "(") pDepth++;
          else if (t.kind === "punct" && t.value === ")") pDepth--;
          else if (pDepth === 0 && t.kind === "punct" && t.value === ",") { commaCount++; argumentStart = x + 1; }
        }
      }
    }
  }

  if (!targetName) return null;

  const tLower = targetName.toLowerCase();
  const sLower = targetSchema?.toLowerCase();

  const candidates = (schema.byName.get(tLower) ?? []).filter((o) =>
    (o.kind === "proc" || o.kind === "func" || o.kind === "tvf") &&
    (!folds || o.name === targetName) &&
    (sLower === undefined || (folds ? o.schema === targetSchema : o.schema.toLowerCase() === sLower)));
  const preferred = sLower === undefined ? candidates.filter((o) => o.schema === schema.defaultSchema) : candidates;
  const matches = preferred.length ? preferred : candidates;
  // Do not guess between overloaded PostgreSQL functions.
  if (matches.length !== 1) return null;
  const match = matches[0];
  if (!match.params?.length) return null;

  const sortedParams = [...match.params].sort((a, b) => a.ordinal - b.ordinal);
  const paramSignatures = sortedParams.map((p) => ({
    label: `${p.name} ${p.type}${p.output ? " OUTPUT" : ""}${p.hasDefault ? " = default" : ""}`,
    documentation: p.output ? "OUTPUT parameter" : undefined,
  }));

  const paramListStr = paramSignatures.map((p) => p.label).join(", ");
  const label = `${match.schema}.${match.name}(${paramListStr})${match.returnType ? `: ${match.returnType}` : ""}`;

  const arg = T[argumentStart];
  const named = arg && ["=", "=>", ":="].includes(T[argumentStart + 1]?.value ?? "")
    ? sortedParams.findIndex((p) => p.name.toLowerCase() === arg.value.toLowerCase()) : -1;
  return {
    label,
    parameters: paramSignatures,
    activeParameter: named >= 0 ? named : Math.min(Math.max(0, commaCount), sortedParams.length - 1),
  };
}


export function tier1SignatureHelp(text: string, offset: number, dialect: Dialect, schema: SchemaIndex | null): Tier1SignatureResult | null {
  const region = sqlRegionAtOffset(text, offset, dialect);
  return tier1SignatureHelpInRegion(region.text, region.offset, dialect, schema);
}

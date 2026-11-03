import type { Dialect } from "../../ipc/contract";
import { getStream } from "./lexer";
import { lastStartingBefore } from "./scanner";
import { DIALECTS } from "../../engines";

/** SQL-language and PL/pgSQL AS/DO bodies are code, unlike ordinary dollar-quoted literals. */
export function sqlRegionAtOffset(text: string, offset: number, dialect: Dialect): { text: string; offset: number; base: number } {
  let base = 0;
  if (DIALECTS[dialect].dollarQuotedBodies) for (let depth = 0; depth < 3; depth++) {
    const tokens = getStream(text, dialect).code;
    const index = lastStartingBefore(tokens, offset + 1);
    const t = tokens[index];
    if (!t || t.kind !== "string" || !t.tag || !["AS", "DO"].includes(tokens[index - 1]?.upper ?? "")) break;
    const start = t.start + t.tag, end = t.open ? t.end : t.end - t.tag;
    if (offset < start || offset > end) break;
    text = text.slice(start, end); offset -= start; base += start;
  }
  return { text, offset, base };
}

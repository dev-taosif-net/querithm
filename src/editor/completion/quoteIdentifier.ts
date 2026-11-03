import type { Dialect } from "../../ipc/contract";
import { DIALECTS } from "../../engines";
import { reservedWords } from "./keywords";

/** Quote a metadata identifier, including reserved words and embedded delimiters (the per-dialect table in engines.ts). */
export function quoteIdentifier(name: string, dialect: Dialect): string {
  const d = DIALECTS[dialect];
  if (d.regularIdentifier.test(name) && !reservedWords(dialect).has(name.toUpperCase())) return name;
  return d.quote(name);
}

// The current-statement scope of an ER diagram. Kept apart from erdLayout so the command that opens a diagram
// does not pull dagre into the startup bundle (§15 rule 9).
import type { Dialect } from "../ipc/contract";
import { scan } from "../editor/completion/scanner";
import { statementBoundsAt } from "../editor/completion/sqlText";
import { resolveTable } from "../editor/completion/schemaIndex";
import type { SchemaIndex } from "../editor/completion/types";

/**
 * Tables referenced by the statement at `offset` (the tier-1 scanner's tablesInScope), resolved against the schema.
 * Scanned from the statement's start: at the cursor the scanner treats the word being typed as a prefix, not a table.
 */
export function statementTables(idx: SchemaIndex, text: string, offset: number, dialect: Dialect): number[] {
  const ctx = scan(text, statementBoundsAt(text, offset, dialect).start, dialect);
  const ids = ctx.tablesInScope.map((t) => resolveTable(idx, t)).filter((o) => o?.kind === "table").map((o) => o!.id);
  return [...new Set(ids)];
}

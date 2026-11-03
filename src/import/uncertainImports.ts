// Imports whose own COMMIT was cut short (import.progress `errorCode: "outcomeUnknown"`, the code grid Apply uses):
// whether their rows were written is unknown, so the wizard will not import into that table again until the user says
// they checked it — a blind retry could load the same rows twice. Kept for the app's run; a later import into the
// table that succeeds forgets it.

export interface UncertainImport {
  rows: number;
  file: string;
}

const uncertain = new Map<string, UncertainImport>();

const key = (profileId: string, database: string, schema: string, table: string) =>
  [profileId, database, schema.trim(), table.trim()].map((s) => s.toLowerCase()).join("\u0000");

export function markUncertainImport(profileId: string, database: string, schema: string, table: string, info: UncertainImport) {
  uncertain.set(key(profileId, database, schema, table), info);
}

export function uncertainImport(profileId: string, database: string, schema: string, table: string): UncertainImport | undefined {
  return uncertain.get(key(profileId, database, schema, table));
}

export function forgetUncertainImport(profileId: string, database: string, schema: string, table: string) {
  uncertain.delete(key(profileId, database, schema, table));
}

/** Tests: start from no record. */
export function resetUncertainImports() {
  uncertain.clear();
}

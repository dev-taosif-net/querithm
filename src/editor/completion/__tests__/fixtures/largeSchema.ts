// The §14 #4b schema: 5,000 tables of 21 columns and 3,000 foreign keys, per engine (Oracle folds names to upper case).
// Shared by perf.test.ts and the bench's tier-1 metrics (scripts/bench/run.mjs --tier1), so both time the same thing.
import type { Dialect, SchemaSnapshot } from "../../../../ipc/contract";

export const LARGE_SCHEMA_ENGINES = ["tsql", "pg", "mySql", "sqlite", "duckDb", "oracle"] as const satisfies readonly Dialect[];

const DEFAULT_SCHEMA: Record<(typeof LARGE_SCHEMA_ENGINES)[number], string> = {
  tsql: "dbo", pg: "public", mySql: "db", sqlite: "main", duckDb: "main", oracle: "APP",
};

/** The snapshot, and the login schema to build its index with (Oracle). */
export function largeSchema(d: (typeof LARGE_SCHEMA_ENGINES)[number]): { snapshot: SchemaSnapshot; loginSchema: string | null } {
  const N = (s: string) => (d === "oracle" ? s.toUpperCase() : s);
  const schema = DEFAULT_SCHEMA[d];
  const name = (t: number) => N(`Table${String(t % 5000).padStart(4, "0")}`);
  const objects = Array.from({ length: 5000 }, (_, t) => ({
    id: t + 1, schema, name: name(t), kind: "table" as const,
    columns: Array.from({ length: 21 }, (_, c) => ({
      name: N(c === 0 ? "Id" : c === 1 ? `${name(t + 1)}Id` : `Col${c}`), type: "int", nullable: true, identity: false, computed: false, pk: c === 0, ordinal: c + 1,
    })),
  }));
  const fks = Array.from({ length: 3000 }, (_, i) => ({ name: `fk${i}`, parent: i + 1, referenced: ((i + 1) % 5000) + 1, columns: [{ parent: N(`${name(i + 1)}Id`), referenced: N("Id") }] }));
  return { snapshot: { profileId: "p", database: "db", status: "fresh", objects, fks }, loginSchema: d === "oracle" ? "APP" : null };
}

/** The statements #4b completes at their end: object names after FROM (with and without a prefix) and after JOIN. */
export const LARGE_SCHEMA_STATEMENTS = ["SELECT * FROM ", "SELECT * FROM Tab", "SELECT * FROM Table0001 a JOIN "] as const;

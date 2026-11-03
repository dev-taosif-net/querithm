// O(1) lookups over one (profile, database) SchemaSnapshot for tier-1 completion (§9).
import type { Dialect, ObjectKind, SchemaSnapshot, SnapObject } from "../../ipc/contract";
import type { SchemaIndex, TableInScope } from "./types";
import { DIALECTS, knownDialect } from "../../engines";

export function buildSchemaIndex(snapshot: SchemaSnapshot, dialect: Dialect, databases: string[]): SchemaIndex {
  // Triggers are never named in queries (and a PostgreSQL trigger may share its table's name), so completion skips them.
  const objects = (snapshot?.objects ?? []).filter((o): o is SnapObject => !!o && typeof o.name === "string" && o.kind !== "trigger");
  const byName = new Map<string, SnapObject[]>();
  const byQualified = new Map<string, SnapObject>();
  const byId = new Map<number, SnapObject>();
  const schemaSet = new Map<string, string>();
  for (const o of objects) {
    const n = o.name.toLowerCase();
    const list = byName.get(n);
    if (list) list.push(o); else byName.set(n, [o]);
    const schema = o.schema ?? "";
    byQualified.set(`${schema.toLowerCase()}.${n}`, o);
    byId.set(o.id, o);
    if (!schemaSet.has(schema.toLowerCase())) schemaSet.set(schema.toLowerCase(), schema);
  }
  // Where the database is the schema (MySQL), the snapshot's own database is what unqualified names resolve to.
  const defaultSchema = DIALECTS[knownDialect(dialect)].defaultSchema ?? snapshot?.database ?? "";
  const dbs: string[] = [];
  const seen = new Set<string>();
  for (const d of databases ?? []) {
    if (typeof d !== "string" || !d || seen.has(d.toLowerCase())) continue;
    seen.add(d.toLowerCase());
    dbs.push(d);
  }
  return {
    profileId: snapshot?.profileId ?? "",
    database: snapshot?.database ?? "",
    dialect,
    objects,
    byName,
    byQualified,
    byId,
    schemas: [...schemaSet.values()].filter((s) => s !== "").sort((a, b) => a.localeCompare(b)),
    fks: snapshot?.fks ?? [],
    databases: dbs,
    defaultSchema,
  };
}

const eq = (a: string | undefined, b: string | undefined): boolean => (a ?? "").toLowerCase() === (b ?? "").toLowerCase();

const RELATION_KINDS: ReadonlySet<ObjectKind> = new Set<ObjectKind>(["table", "view", "tvf", "synonym"]);

/** Look up an object by optional schema + name; without a schema prefer the default schema. */
export function findObject(
  idx: SchemaIndex, name: string, schema?: string, kinds: ReadonlySet<ObjectKind> = RELATION_KINDS,
): SnapObject | undefined {
  if (!name) return undefined;
  const n = name.toLowerCase();
  if (schema !== undefined && schema !== "") {
    const o = idx.byQualified.get(`${schema.toLowerCase()}.${n}`);
    return o && kinds.has(o.kind) ? o : undefined;
  }
  const list = idx.byName.get(n);
  if (!list) return undefined;
  let best: SnapObject | undefined;
  for (const o of list) {
    if (!kinds.has(o.kind)) continue;
    if (eq(o.schema, idx.defaultSchema)) return o;
    best ??= o;
  }
  return best;
}

/** Resolve a table reference from the scanner to a snapshot object (real tables/views/TVFs only). */
export function resolveTable(idx: SchemaIndex | null, t: TableInScope): SnapObject | undefined {
  if (!idx || t.isCte || t.isDerived) return undefined;
  if (t.database && !eq(t.database, idx.database)) return undefined;
  return findObject(idx, t.name, t.schema);
}

export function isDefaultSchema(idx: SchemaIndex, schema: string): boolean {
  return eq(schema, idx.defaultSchema);
}

export const ALL_KINDS: ReadonlySet<ObjectKind> = new Set<ObjectKind>(["table", "view", "proc", "func", "tvf", "type", "sequence", "synonym"]);
export { RELATION_KINDS };

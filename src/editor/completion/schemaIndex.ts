// O(1) lookups over one (profile, database) SchemaSnapshot for tier-1 completion (§9).
import type { Dialect, ObjectKind, SchemaSnapshot, SnapFk, SnapObject } from "../../ipc/contract";
import type { SchemaIndex, TableInScope } from "./types";
import { DIALECTS, foldIdentifier, knownDialect } from "../../engines";

/**
 * `loginSchema`: the signed-in user's schema, for a dialect where unqualified names resolve to it (Oracle,
 * DialectTraits.loginSchema); ignored elsewhere.
 */
export function buildSchemaIndex(snapshot: SchemaSnapshot, dialect: Dialect, databases: string[], loginSchema?: string | null): SchemaIndex {
  // Triggers, database links and foreign servers are not named as schema objects in queries, so completion skips them.
  const objects = (snapshot?.objects ?? []).filter(
    (o): o is SnapObject => !!o && typeof o.name === "string" && o.kind !== "trigger" && o.kind !== "dbLink" && o.kind !== "foreignServer",
  );
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
  // Where the database is the schema (MySQL), the snapshot's own database is what unqualified names resolve to; where the
  // user is the schema (Oracle), the signed-in user's.
  const traits = DIALECTS[knownDialect(dialect)];
  const defaultSchema = traits.defaultSchema ?? (traits.loginSchema && loginSchema ? loginSchema : null) ?? snapshot?.database ?? "";
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
  // D4 S12: where the server folds unquoted names (Oracle upper, PostgreSQL lower), the object stored under exactly the
  // name the server would look up wins over another that differs only in case, and a quoted name is that object or none.
  if (DIALECTS[knownDialect(idx.dialect)].identifierFold) {
    const name = foldIdentifier(t.name, !!t.quoted, idx.dialect);
    const schema = t.schema ? foldIdentifier(t.schema, !!t.schemaQuoted, idx.dialect) : undefined;
    const hits = (idx.byName.get(t.name.toLowerCase()) ?? []).filter((o) => RELATION_KINDS.has(o.kind) && o.name === name && (schema === undefined || o.schema === schema));
    if (hits.length) return hits.find((o) => schema !== undefined || eq(o.schema, idx.defaultSchema)) ?? hits[0];
    if (t.quoted || t.schemaQuoted) return undefined;
  }
  return findObject(idx, t.name, t.schema);
}

/** A synonym's target object when the snapshot has it (its columns are the synonym's), else the object itself. */
export function synonymTarget(idx: SchemaIndex | null, o: SnapObject): SnapObject {
  if (!idx || o.kind !== "synonym" || o.targetObjectId == null) return o;
  return idx.byId.get(o.targetObjectId) ?? o;
}

export function isDefaultSchema(idx: SchemaIndex, schema: string): boolean {
  return eq(schema, idx.defaultSchema);
}

export const ALL_KINDS: ReadonlySet<ObjectKind> = new Set<ObjectKind>(["table", "view", "proc", "func", "tvf", "type", "sequence", "synonym"]);
export { RELATION_KINDS };

/** An index's foreign keys by the table that holds them and by the table they reference. */
export interface FkLookup { byParent: Map<number, SnapFk[]>; byReferenced: Map<number, SnapFk[]> }

/**
 * The foreign keys of an index by table, built once per SchemaIndex (a schema change builds a new index; `fks` is never
 * changed in place): column details and JOIN items asked every FK of the schema for each column or table (completion audit H9).
 */
const FK_LOOKUP = new WeakMap<SchemaIndex, FkLookup>();
export function fkLookup(idx: SchemaIndex): FkLookup {
  let l = FK_LOOKUP.get(idx);
  if (!l) {
    l = { byParent: new Map(), byReferenced: new Map() };
    for (const fk of idx.fks ?? []) {
      if (!fk) continue;
      let p = l.byParent.get(fk.parent);
      if (!p) l.byParent.set(fk.parent, (p = []));
      p.push(fk);
      let r = l.byReferenced.get(fk.referenced);
      if (!r) l.byReferenced.set(fk.referenced, (r = []));
      r.push(fk);
    }
    FK_LOOKUP.set(idx, l);
  }
  return l;
}

/**
 * What a column references through a foreign key: `schema.table.column` (`schema.table` when the referenced column is not
 * known), or undefined when it is not an FK column.
 */
export function fkReference(idx: SchemaIndex | null, objectId: number, column: string): string | undefined {
  if (!idx) return undefined;
  const lower = column.toLowerCase();
  for (const fk of fkLookup(idx).byParent.get(objectId) ?? []) {
    if (fk.parent !== objectId) continue;
    const pair = fk.columns?.find((p) => p.parent.toLowerCase() === lower);
    if (!pair) continue;
    const ref = idx.byId.get(fk.referenced);
    // SQLite's `REFERENCES customers` without a column list may come without the referenced column: the table alone,
    // not `main.customers.` (code audit 2026-11-26, L10).
    if (!ref) return undefined;
    return pair.referenced ? `${ref.schema}.${ref.name}.${pair.referenced}` : `${ref.schema}.${ref.name}`;
  }
  return undefined;
}

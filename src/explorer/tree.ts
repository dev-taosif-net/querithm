// Object Explorer tree model (§10, §12): the visible rows as one flat list, built from the stores without React.
// Rows have stable keys made of names (never object ids), so an object dropped and created again — or a server that
// reconnects after an engine restart — keeps its place, its expansion and its selection. Per-snapshot buckets (kind,
// schema, lowercased names, triggers by table) are built once per snapshot object, so a rebuild costs the rows shown.
import type { Engine, EnvironmentKind, ObjectDetails, ObjectKind, Profile, SnapObject } from "../ipc/contract";
import type { ServerConn } from "../store/connections";
import type { SnapEntry } from "../store/snapshots";
import { snapKey } from "../store/snapshots";
import { schemaGroupKey } from "../store/settings";
import { MEMORY_NOTE } from "../store/connections";
import { engineTraits } from "../engines";

export type { Engine };
export type CatId = "tables" | "views" | "procs" | "funcs" | "types" | "sequences" | "synonyms";
export type KindFilter = "all" | CatId | "triggers";
export type Sub = "columns" | "keys" | "indexes" | "params" | "triggers";
export type ServerStatus = "connected" | "connecting" | "error" | "off";
export type DbState = "idle" | "ok" | "loading" | "refreshing" | "stale" | "error" | "offline";

export const CATEGORIES: { id: CatId; kinds: ObjectKind[]; label: string; icon: string }[] = [
  { id: "tables", kinds: ["table"], label: "Tables", icon: "table" },
  { id: "views", kinds: ["view"], label: "Views", icon: "view" },
  { id: "procs", kinds: ["proc"], label: "Procedures", icon: "proc" },
  { id: "funcs", kinds: ["func", "tvf"], label: "Functions", icon: "func" },
  { id: "types", kinds: ["type"], label: "Types", icon: "type" },
  { id: "sequences", kinds: ["sequence"], label: "Sequences", icon: "sequence" },
  { id: "synonyms", kinds: ["synonym"], label: "Synonyms", icon: "synonym" },
];
/** The categories a flat (not schema-grouped) database always lists, empty or not. */
const MAIN_CATS = new Set<CatId>(["tables", "views", "procs", "funcs"]);

/** Promotion order: servers are listed (and grouped) Local → Dev → Test → Staging → Prod. */
export const ENV_ORDER: EnvironmentKind[] = ["local", "dev", "test", "staging", "prod"];
/** Search results per kind before "Show all". */
export const SEARCH_CAP = 300;

/** The schema listed first and left out of names (dbo, public); no engine reads as the default engine's. */
export const defaultSchema = (engine: Engine | undefined) => engineTraits(engine).defaultSchema;

interface Base {
  key: string;
  /** aria-level (1-based). */
  level: number;
  /** aria-posinset / aria-setsize among the siblings (set once per build, see numberSiblings). */
  pos?: number;
  size?: number;
  /** Index of the parent row in the list, -1 at the top. */
  parent: number;
  expandable: boolean;
  expanded: boolean;
}
export type TreeRow = Base &
  (
    | { t: "env"; env: EnvironmentKind; count: number }
    | { t: "server"; profileId: string; status: ServerStatus; error?: string; pinned: boolean; memory?: boolean; refreshing?: boolean }
    | { t: "note"; text: string; tone: "muted" | "warn" | "err"; busy?: boolean }
    | { t: "action"; action: "reconnect" | "editProfile" | "repick" | "retryMeta" | "retryDetails"; label: string; profileId: string; db?: string; objKey?: string; obj?: SnapObject }
    | { t: "db"; profileId: string; db: string; active: boolean; state: DbState; refreshedAt?: number | null; error?: string | null; dbState?: string | null }
    | { t: "sysdbs"; profileId: string; count: number }
    | { t: "schema"; profileId: string; db: string; schema: string; count: number }
    | { t: "cat"; profileId: string; db: string; schema?: string; cat: CatId; count: number; shown?: number; filter?: string }
    | { t: "filter"; catKey: string; text: string; shown: number; total: number }
    | { t: "obj"; profileId: string; db: string; obj: SnapObject; bare: boolean; leaf: boolean }
    | { t: "sub"; profileId: string; db: string; obj: SnapObject; sub: Sub; objKey: string }
    | { t: "leaf"; text: string; detail?: string; icon: string; tone: string; drag?: string; obj?: SnapObject; profileId?: string; db?: string; column?: string }
    | { t: "header"; text: string; kind?: KindFilter; capped?: boolean }
  );
export type RowOf<T extends TreeRow["t"]> = Extract<TreeRow, { t: T }>;

/**
 * Details of one object (keys, indexes), tagged with the catalog record they were read for. A refresh that changed the
 * object replaces its record (a reconnect re-reads them all), so the details are read again; a status-only update
 * (refreshing, nothing changed) keeps the record and the details.
 */
export type DetailsCell = { o: SnapObject; d: ObjectDetails | "loading" | string };

export interface TreeInput {
  profiles: readonly Profile[];
  servers: Readonly<Record<string, ServerConn>>;
  /** Servers in the order they joined the Explorer. */
  order: readonly string[];
  pinned: readonly string[];
  showAll: boolean;
  groupByEnv: boolean;
  collapsedEnvs: ReadonlySet<EnvironmentKind>;
  expanded: ReadonlySet<string>;
  entries: Readonly<Record<string, SnapEntry>>;
  details: Readonly<Record<string, DetailsCell>>;
  schemaGroups: readonly string[];
  kind: KindFilter;
  /** Category key → text typed into it (type to filter a folder). */
  folderFilters: Readonly<Record<string, string>>;
  /** The active query tab's database. */
  active?: { profileId: string; db: string };
}

export interface TreeResult {
  rows: TreeRow[];
  /** Expanded Keys / Indexes folders whose details are missing or older than the snapshot. */
  needDetails: { key: string; profileId: string; db: string; obj: SnapObject }[];
  /** Expanded databases in view whose catalog was never read: only those, not every restored key (lazy, §10). */
  needCatalog: { profileId: string; db: string }[];
  /** Profile ids of the server rows, in order. */
  shown: string[];
}

// ------------------------------------------------------------------------------------------------- snapshot buckets

interface Buckets {
  byKind: Map<ObjectKind, SnapObject[]>;
  bySchema: Map<string, Map<ObjectKind, SnapObject[]>>;
  schemaCount: Map<string, number>;
  triggers: Map<number, SnapObject[]>;
  byId: Map<number, SnapObject>;
  lower: Map<SnapObject, string>;
}
const bucketCache = new WeakMap<object, Buckets>();

/** Groups a snapshot's objects once (O(n)); triggers sit under their table, not at the top level. */
export function bucketsOf(objects: readonly SnapObject[]): Buckets {
  let b = bucketCache.get(objects);
  if (b) return b;
  b = { byKind: new Map(), bySchema: new Map(), schemaCount: new Map(), triggers: new Map(), byId: new Map(), lower: new Map() };
  for (const o of objects) {
    if (!o) continue;
    b.byId.set(o.id, o);
    b.lower.set(o, `${o.schema}.${o.name}`.toLowerCase());
    if (o.kind === "trigger") {
      if (o.parentObjectId != null) {
        const list = b.triggers.get(o.parentObjectId);
        if (list) list.push(o);
        else b.triggers.set(o.parentObjectId, [o]);
      }
      // Search still lists triggers; they are not a tree category.
      push(b.byKind, o.kind, o);
      continue;
    }
    push(b.byKind, o.kind, o);
    let s = b.bySchema.get(o.schema);
    if (!s) b.bySchema.set(o.schema, (s = new Map()));
    push(s, o.kind, o);
    b.schemaCount.set(o.schema, (b.schemaCount.get(o.schema) ?? 0) + 1);
  }
  for (const list of b.triggers.values()) list.sort((x, y) => x.name.localeCompare(y.name));
  bucketCache.set(objects, b);
  return b;
}
function push<K, V>(m: Map<K, V[]>, k: K, v: V) {
  const list = m.get(k);
  if (list) list.push(v);
  else m.set(k, [v]);
}
const ofKinds = (m: Map<ObjectKind, SnapObject[]> | undefined, kinds: ObjectKind[]) => (kinds.length === 1 ? (m?.get(kinds[0]) ?? []) : kinds.flatMap((k) => m?.get(k) ?? []));

// ------------------------------------------------------------------------------------------------- keys

export const serverKey = (profileId: string) => `s:${profileId}`;
export const dbKey = (profileId: string, db: string) => `db:${profileId}:${db}`;
export const sysKey = (profileId: string) => `y:${profileId}`;
export const schemaKey = (profileId: string, db: string, schema: string) => `sc:${profileId}:${db}:${schema}`;
export const catKey = (profileId: string, db: string, cat: CatId, schema?: string) => (schema === undefined ? `c:${profileId}:${db}:${cat}` : `c:${profileId}:${db}:${schema}:${cat}`);

/**
 * An object's key by name: kind, schema and name, plus the argument types of a PostgreSQL routine (overloads share a
 * name) and the parent table of a trigger (PostgreSQL trigger names are unique per table only).
 */
export function objKey(profileId: string, db: string, o: SnapObject, parent?: SnapObject): string {
  const base = `o:${profileId}:${db}:${o.kind}:${o.schema}.${o.name}`;
  if (o.kind === "trigger") return `${base}@${parent ? `${parent.schema}.${parent.name}` : (o.parentObjectId ?? "")}`;
  if ((o.kind === "func" || o.kind === "tvf" || o.kind === "proc") && o.params?.length) return `${base}(${o.params.map((p) => p.type).join(",")})`;
  return base;
}

// ------------------------------------------------------------------------------------------------- servers

/** The servers the tree lists: those in use (connected, connecting, failed), pinned profiles, and with "Show all saved
 *  profiles" every profile — Local → Prod, pinned first within an environment, then in the order they joined. */
export function shownServers(i: Pick<TreeInput, "profiles" | "servers" | "order" | "pinned" | "showAll">): Profile[] {
  const byId = new Map(i.profiles.map((p) => [p.id, p]));
  const ids = new Set<string>();
  for (const id of i.order) if (i.servers[id] && byId.has(id)) ids.add(id);
  for (const id of i.pinned) if (byId.has(id)) ids.add(id);
  if (i.showAll) for (const p of i.profiles) ids.add(p.id);
  const pin = (id: string) => (i.pinned.includes(id) ? i.pinned.indexOf(id) : Infinity);
  const joined = (id: string) => (i.order.includes(id) ? i.order.indexOf(id) : Infinity);
  return [...ids]
    .map((id) => byId.get(id)!)
    .sort((a, b) =>
      ENV_ORDER.indexOf(a.environment) - ENV_ORDER.indexOf(b.environment) || pin(a.id) - pin(b.id) || joined(a.id) - joined(b.id) || a.name.localeCompare(b.name, undefined, { sensitivity: "base" }),
    );
}

export function statusOf(conn: ServerConn | undefined): ServerStatus {
  return !conn ? "off" : conn.status;
}

// ------------------------------------------------------------------------------------------------- the tree

export function buildTree(i: TreeInput): TreeResult {
  const rows: TreeRow[] = [];
  const need: Need = { details: [], catalog: [] };
  const add = (r: TreeRow) => rows.push(r) - 1;
  const servers = shownServers(i);
  let envParent = -1;
  let lastEnv: EnvironmentKind | undefined;
  for (const p of servers) {
    if (i.groupByEnv && p.environment !== lastEnv) {
      lastEnv = p.environment;
      const open = !i.collapsedEnvs.has(p.environment);
      envParent = add({ t: "env", key: `env:${p.environment}`, level: 1, parent: -1, expandable: true, expanded: open, env: p.environment, count: servers.filter((x) => x.environment === p.environment).length });
    }
    if (i.groupByEnv && i.collapsedEnvs.has(p.environment)) continue;
    const base = i.groupByEnv ? 1 : 0;
    const conn = i.servers[p.id];
    const status = statusOf(conn);
    const sKey = serverKey(p.id);
    const open = i.expanded.has(sKey);
    const sIdx = add({ t: "server", key: sKey, level: base + 1, parent: i.groupByEnv ? envParent : -1, expandable: true, expanded: open && status !== "off", profileId: p.id, status, error: conn?.error, ...(conn?.refreshing ? { refreshing: true } : {}), pinned: i.pinned.includes(p.id), ...(conn?.memory ? { memory: true } : {}) });
    if (!open || status === "off") continue;
    if (status === "error") {
      add({ t: "note", key: `${sKey}:why`, level: base + 2, parent: sIdx, expandable: false, expanded: false, text: firstSentence(conn?.error) ?? "Connection failed.", tone: "err" });
      add({ t: "action", key: `${sKey}:reconnect`, level: base + 2, parent: sIdx, expandable: false, expanded: false, action: "reconnect", label: "Reconnect", profileId: p.id });
      // A file whose grant is gone (revoked, moved, replaced, missing) is picked again here, straight through the dialog.
      if (conn?.repick) add({ t: "action", key: `${sKey}:repick`, level: base + 2, parent: sIdx, expandable: false, expanded: false, action: "repick", label: "Re-pick file…", profileId: p.id });
      else add({ t: "action", key: `${sKey}:edit`, level: base + 2, parent: sIdx, expandable: false, expanded: false, action: "editProfile", label: "Edit profile…", profileId: p.id });
      continue;
    }
    if (conn?.memory) {
      add({ t: "note", key: `${sKey}:memory`, level: base + 2, parent: sIdx, expandable: false, expanded: false, text: MEMORY_NOTE, tone: "muted" });
      continue;
    }
    // A reconnect after an engine restart keeps the databases it knew; a first connect has none yet.
    if (!conn!.databases.length) {
      if (status === "connecting") add({ t: "note", key: `${sKey}:wait`, level: base + 2, parent: sIdx, expandable: false, expanded: false, text: "Connecting…", tone: "muted", busy: true });
      continue;
    }
    const system = conn!.databases.filter((d) => d.isSystem && d.name.toLowerCase() !== (p.databaseDefault ?? "").toLowerCase());
    const user = conn!.databases.filter((d) => !system.includes(d));
    for (const d of user) addDb(i, rows, need, p, d.name, d.state, base + 2, sIdx);
    if (system.length) {
      const yKey = sysKey(p.id);
      const yOpen = i.expanded.has(yKey);
      const yIdx = add({ t: "sysdbs", key: yKey, level: base + 2, parent: sIdx, expandable: true, expanded: yOpen, profileId: p.id, count: system.length });
      if (yOpen) for (const d of system) addDb(i, rows, need, p, d.name, d.state, base + 3, yIdx);
    }
  }
  numberSiblings(rows);
  return { rows, needDetails: need.details, needCatalog: need.catalog, shown: servers.map((p) => p.id) };
}

interface Need { details: TreeResult["needDetails"]; catalog: TreeResult["needCatalog"] }

/** aria-posinset / aria-setsize for every row in two passes, so drawing a row never scans its siblings. */
export function numberSiblings(rows: TreeRow[]) {
  const size = new Map<number, number>();
  for (const r of rows) size.set(r.parent, (size.get(r.parent) ?? 0) + 1);
  const seen = new Map<number, number>();
  for (const r of rows) {
    const n = (seen.get(r.parent) ?? 0) + 1;
    seen.set(r.parent, n);
    r.pos = n;
    r.size = size.get(r.parent);
  }
}

function firstSentence(text: string | undefined): string | undefined {
  if (!text) return undefined;
  const line = text.split("\n")[0].trim();
  return line.length > 160 ? `${line.slice(0, 157)}…` : line;
}

function dbStateOf(entry: SnapEntry | undefined, serverState: string | null | undefined): DbState {
  if (serverState && serverState.toUpperCase() !== "ONLINE") return "offline";
  if (!entry) return "idle";
  if (entry.status === "loading" && !entry.snapshot) return "loading";
  const has = !!entry.snapshot?.objects.length;
  // A cache the sidecar has not read yet (empty) or is about to re-read (stale) is not an empty database.
  if (!has && (entry.status === "empty" || entry.status === "stale")) return "loading";
  if (entry.status === "error") return has ? "stale" : "error";
  if (entry.status === "refreshing" || entry.status === "loading") return has ? "refreshing" : "loading";
  return "ok";
}

function addDb(i: TreeInput, rows: TreeRow[], need: Need, p: Profile, db: string, serverState: string | null | undefined, level: number, parent: number) {
  const add = (r: TreeRow) => rows.push(r) - 1;
  const key = dbKey(p.id, db);
  const entry = i.entries[snapKey(p.id, db)];
  const state = dbStateOf(entry, serverState);
  const open = i.expanded.has(key) && state !== "offline";
  // Exact names (D0e identity): the tab's database is the one with this exact name, never a case variant of it.
  const active = !!i.active && i.active.profileId === p.id && i.active.db === db;
  const dIdx = add({
    t: "db", key, level, parent, expandable: state !== "offline", expanded: open, profileId: p.id, db, active, state,
    refreshedAt: entry?.snapshot?.refreshedAt, error: entry?.error, dbState: serverState,
  });
  if (!open) return;
  if (state === "idle") need.catalog.push({ profileId: p.id, db });
  const child = level + 1;
  if (state === "idle" || state === "loading") return void add({ t: "note", key: `${key}:loading`, level: child, parent: dIdx, expandable: false, expanded: false, text: "Loading metadata…", tone: "muted", busy: true });
  if (state === "error") {
    add({ t: "note", key: `${key}:error`, level: child, parent: dIdx, expandable: false, expanded: false, text: entry?.error || "Metadata could not be loaded.", tone: "err" });
    add({ t: "action", key: `${key}:retry`, level: child, parent: dIdx, expandable: false, expanded: false, action: "retryMeta", label: "Retry", profileId: p.id, db });
    return;
  }
  // What the last refresh could not read (Oracle: a dictionary view the login may not read): the snapshot is fresh, but
  // those objects or details are missing, and the reason is shown above them (D4 S05).
  (entry!.snapshot?.notes ?? []).forEach((text, n) =>
    add({ t: "note", key: `${key}:catalogNote:${n}`, level: child, parent: dIdx, expandable: false, expanded: false, text, tone: "warn" }));
  const objects = entry!.snapshot?.objects ?? [];
  if (!objects.length) return void add({ t: "note", key: `${key}:reading`, level: child, parent: dIdx, expandable: false, expanded: false, text: state === "ok" ? "No objects" : "Reading the catalog…", tone: "muted", busy: state !== "ok" });
  const b = bucketsOf(objects);
  const engine = p.engine;
  const ctx: DbCtx = { i, rows, need: need.details, p, db, b, engine, keys: new Set() };
  if (i.schemaGroups.includes(schemaGroupKey(p.id, db))) {
    const dflt = defaultSchema(engine);
    const schemas = [...b.bySchema.keys()].filter((s) => i.kind === "all" || hasKind(b.bySchema.get(s), i.kind)).sort((a, c) => (a === dflt ? -1 : c === dflt ? 1 : a.localeCompare(c, undefined, { sensitivity: "base" })));
    if (!schemas.length) return void add({ t: "note", key: `${key}:none`, level: child, parent: dIdx, expandable: false, expanded: false, text: i.kind === "all" ? "No objects" : noneOfKind(i.kind), tone: "muted" });
    for (const s of schemas) {
      const sk = schemaKey(p.id, db, s);
      const sOpen = i.expanded.has(sk);
      const sIdx = add({ t: "schema", key: sk, level: child, parent: dIdx, expandable: true, expanded: sOpen, profileId: p.id, db, schema: s, count: b.schemaCount.get(s) ?? 0 });
      if (sOpen) addCats(ctx, b.bySchema.get(s), s, child + 1, sIdx);
    }
  } else {
    const before = rows.length;
    addCats(ctx, b.byKind, undefined, child, dIdx);
    // A kind filter that leaves nothing here says so instead of an open database with no rows.
    if (rows.length === before) add({ t: "note", key: `${key}:none`, level: child, parent: dIdx, expandable: false, expanded: false, text: noneOfKind(i.kind), tone: "muted" });
  }
}

function noneOfKind(kind: KindFilter): string {
  if (kind === "triggers") return "Triggers are under their tables; search lists them";
  return `No ${CATEGORIES.find((c) => c.id === kind)?.label.toLowerCase() ?? "objects"}`;
}

const hasKind = (m: Map<ObjectKind, SnapObject[]> | undefined, kind: KindFilter) => CATEGORIES.find((c) => c.id === kind)?.kinds.some((k) => m?.get(k)?.length) ?? false;

interface DbCtx {
  i: TreeInput; rows: TreeRow[]; need: TreeResult["needDetails"]; p: Profile; db: string; b: Buckets; engine: Engine;
  /** Object keys used so far: a name-key collision (PostgreSQL `f(a int, OUT b int)` beside `f(a int, b int)`) gets the
   *  object id appended, so React keys and expansion stay one per object. */
  keys: Set<string>;
}

function addCats(c: DbCtx, m: Map<ObjectKind, SnapObject[]> | undefined, schema: string | undefined, level: number, parent: number) {
  const { i, rows, p, db } = c;
  const add = (r: TreeRow) => rows.push(r) - 1;
  for (const cat of CATEGORIES) {
    if (i.kind !== "all" && i.kind !== cat.id) continue;
    const list = ofKinds(m, cat.kinds);
    // Under a schema only non-empty categories are shown; the flat tree keeps the four main ones.
    if (!list.length && (schema !== undefined || !MAIN_CATS.has(cat.id))) continue;
    const key = catKey(p.id, db, cat.id, schema);
    const open = i.expanded.has(key) && list.length > 0;
    const filter = i.folderFilters[key]?.trim().toLowerCase() || undefined;
    const shown = filter ? list.filter((o) => o.name.toLowerCase().includes(filter)) : list;
    const cIdx = add({ t: "cat", key, level, parent, expandable: list.length > 0, expanded: open, profileId: p.id, db, schema, cat: cat.id, count: list.length, shown: filter ? shown.length : undefined, filter: i.folderFilters[key] || undefined });
    if (!open) continue;
    if (filter) add({ t: "filter", key: `${key}:filter`, level: level + 1, parent: cIdx, expandable: false, expanded: false, catKey: key, text: i.folderFilters[key], shown: shown.length, total: list.length });
    const dflt = defaultSchema(c.engine);
    for (const o of shown) addObj(c, o, level + 1, cIdx, schema !== undefined || o.schema === dflt, false);
    if (filter && !shown.length) add({ t: "note", key: `${key}:nomatch`, level: level + 1, parent: cIdx, expandable: false, expanded: false, text: `No ${cat.label.toLowerCase()} match “${i.folderFilters[key]}”`, tone: "muted" });
  }
}

function subsOf(o: SnapObject, b: Buckets): Sub[] {
  if (o.kind === "table") return ["columns", "keys", "indexes", "triggers"];
  if (o.kind === "view") return b.triggers.has(o.id) ? ["columns", "triggers"] : ["columns"];
  if (o.kind === "tvf") return ["params", "columns"];
  if (o.kind === "proc" || o.kind === "func") return ["params"];
  return [];
}

function addObj(c: DbCtx, o: SnapObject, level: number, parent: number, bare: boolean, forceLeaf: boolean, parentObj?: SnapObject) {
  const { i, rows, p, db, b } = c;
  const add = (r: TreeRow) => rows.push(r) - 1;
  let key = objKey(p.id, db, o, parentObj);
  if (c.keys.has(key)) key = `${key}#${o.id}`;
  c.keys.add(key);
  const subs = forceLeaf ? [] : subsOf(o, b);
  const open = i.expanded.has(key) && subs.length > 0;
  const oIdx = add({ t: "obj", key, level, parent, expandable: subs.length > 0, expanded: open, profileId: p.id, db, obj: o, bare, leaf: subs.length === 0 });
  if (!open) return;
  for (const sub of subs) {
    const sk = `${key}:${sub}`;
    const sOpen = i.expanded.has(sk);
    const sIdx = add({ t: "sub", key: sk, level: level + 1, parent: oIdx, expandable: true, expanded: sOpen, profileId: p.id, db, obj: o, sub, objKey: key });
    if (sOpen) addSub(c, o, sub, key, sk, level + 2, sIdx);
  }
}

function addSub(c: DbCtx, o: SnapObject, sub: Sub, oKey: string, sk: string, level: number, parent: number) {
  const { i, rows, p, db, b } = c;
  const add = (r: TreeRow) => rows.push(r) - 1;
  const leaf = (k: string, text: string, detail: string | undefined, icon: string, tone: string, extra: Partial<RowOf<"leaf">> = {}) =>
    add({ t: "leaf", key: `${sk}:${k}`, level, parent, expandable: false, expanded: false, text, detail, icon, tone, ...extra });
  const note = (text: string, tone: "muted" | "err" = "muted", busy = false) => add({ t: "note", key: `${sk}:note`, level, parent, expandable: false, expanded: false, text, tone, busy });
  if (sub === "triggers") {
    const list = b.triggers.get(o.id) ?? [];
    for (const tr of list) addObj(c, tr, level, parent, true, true, o);
    if (!list.length) note("No triggers");
  } else if (sub === "columns") {
    for (const col of o.columns ?? [])
      leaf(col.name, col.name, `${col.type}${col.nullable ? "" : " NOT NULL"}`, col.pk ? "key" : "column", col.pk ? "warn" : "muted", { drag: col.name, obj: o, profileId: p.id, db, column: col.name });
    if (!o.columns?.length) note("No columns");
  } else if (sub === "params") {
    for (const prm of o.params ?? []) leaf(prm.name || `$${prm.ordinal}`, prm.name || `$${prm.ordinal}`, `${prm.type}${prm.output ? " OUTPUT" : ""}${prm.hasDefault ? " = default" : ""}`, "param", "ident");
    if (o.returnType) leaf("ret", "RETURNS", o.returnType, "returns", "muted");
    if (!o.params?.length && !o.returnType) note("No parameters");
  } else {
    const cell = i.details[oKey];
    const fresh = cell && cell.o === o ? cell.d : undefined;
    if (!fresh || fresh === "loading") {
      if (!fresh) c.need.push({ key: oKey, profileId: p.id, db, obj: o });
      note("Loading…", "muted", true);
    } else if (typeof fresh === "string") {
      note(fresh, "err");
      add({ t: "action", key: `${sk}:retry`, level, parent, expandable: false, expanded: false, action: "retryDetails", label: "Retry", profileId: p.id, db, objKey: oKey, obj: o });
    } else if (sub === "indexes") {
      for (const ix of fresh.indexes) leaf(ix.name, ix.name, `${ix.isPrimary ? "PK " : ix.isUnique ? "UNIQUE " : ""}(${ix.columns.join(", ")})`, "index", "obj.index");
      if (!fresh.indexes.length) note("No indexes");
    } else {
      const pk = fresh.indexes.find((x) => x.isPrimary);
      // Short forms: at 280 px the detail gets about a third of the row, and "PRIMARY KEY" would be cut to "PR…".
      if (pk) leaf("pk", pk.name, `PK (${pk.columns.join(", ")})`, "key", "warn");
      for (const fk of fresh.foreignKeys) {
        const ref = b.byId.get(fk.referenced);
        leaf(`fk:${fk.name}`, fk.name.split("@")[0], `→ ${ref ? `${ref.schema}.${ref.name}` : fk.referenced} (${fk.columns.map((x) => `${x.parent}=${x.referenced}`).join(", ")})`, "fk", "obj.table");
      }
      for (const fk of fresh.referencedBy) {
        const src = b.byId.get(fk.parent);
        leaf(`r:${fk.name}`, fk.name.split("@")[0], `← ${src ? `${src.schema}.${src.name}` : fk.parent}`, "fk", "muted");
      }
      if (!pk && !fresh.foreignKeys.length && !fresh.referencedBy.length) note("No keys");
    }
  }
}

// ------------------------------------------------------------------------------------------------- search

export type SearchScope =
  | { state: "noTab" }
  | { state: "noConn" }
  | { state: "noDb"; profileId: string }
  | { state: "server"; profileId: string; db: string; sessionId?: string; status: Exclude<ServerStatus, "connected">; error?: string }
  | { state: "loading"; profileId: string; db: string; sessionId?: string }
  | { state: "error"; profileId: string; db: string; sessionId?: string; error: string }
  | { state: "ready"; profileId: string; db: string; sessionId?: string; objects: readonly SnapObject[]; stale: boolean };

/**
 * The active tab, as search reads it: a session-owned engine's tab (SQLite) names its session (its own view, Phase D0d);
 * `noDatabaseYet` is an in-memory tab whose session has not opened, so there is nothing to search.
 */
export interface SearchTab {
  profileId?: string;
  database?: string;
  sessionId?: string;
  noDatabaseYet?: boolean;
}

/** What the search box searches: the active query tab's database, never another server (B1). */
export function searchScope(active: SearchTab | undefined, servers: Readonly<Record<string, ServerConn>>, entries: Readonly<Record<string, SnapEntry>>): SearchScope {
  if (!active) return { state: "noTab" };
  if (!active.profileId) return { state: "noConn" };
  if (!active.database) return { state: "noDb", profileId: active.profileId };
  const conn = servers[active.profileId];
  const status = statusOf(conn);
  const at = { profileId: active.profileId, db: active.database, ...(active.sessionId ? { sessionId: active.sessionId } : {}) };
  const entry = entries[snapKey(active.profileId, active.database, active.sessionId)];
  const objects = entry?.snapshot?.objects;
  // Cached objects stay searchable while the server is away or a refresh failed; the scope line says so.
  if (objects?.length) return { state: "ready", ...at, objects, stale: entry!.status === "error" || status !== "connected" };
  if (status !== "connected") return { state: "server", ...at, status, error: conn?.error };
  if (active.noDatabaseYet) return { state: "ready", ...at, objects: [], stale: false };
  if (entry?.status === "error") return { state: "error", ...at, error: entry.error || "Metadata could not be loaded." };
  // Nothing read yet, or a first read still running: an empty list here would claim "no matches".
  if (!entry || entry.status !== "fresh") return { state: "loading", ...at };
  return { state: "ready", ...at, objects: [], stale: false };
}

export interface SearchResult {
  rows: TreeRow[];
  total: number;
}

/** Matches by substring of `schema.name`, name-prefix matches first, grouped by kind; each kind shows its first
 *  SEARCH_CAP matches unless it is in `uncapped`, and its header always gives the true count (B3). */
export function searchRows(profileId: string, db: string, engine: Engine, objects: readonly SnapObject[], query: string, kind: KindFilter, uncapped: ReadonlySet<string>): SearchResult {
  const needle = query.trim().toLowerCase();
  const b = bucketsOf(objects);
  const rows: TreeRow[] = [];
  let total = 0;
  const dflt = defaultSchema(engine);
  const groups: { id: KindFilter; label: string; kinds: ObjectKind[] }[] = [...CATEGORIES.map((c) => ({ id: c.id as KindFilter, label: c.label, kinds: c.kinds })), { id: "triggers", label: "Triggers", kinds: ["trigger"] }];
  for (const g of groups) {
    if (kind !== "all" && kind !== g.id) continue;
    const hits = ofKinds(b.byKind, g.kinds).filter((o) => b.lower.get(o)!.includes(needle));
    if (!hits.length) continue;
    total += hits.length;
    hits.sort((a, c) => {
      const ap = a.name.toLowerCase().startsWith(needle) ? 0 : 1;
      const cp = c.name.toLowerCase().startsWith(needle) ? 0 : 1;
      return ap - cp || a.name.length - c.name.length || a.name.localeCompare(c.name);
    });
    const capped = hits.length > SEARCH_CAP && !uncapped.has(g.id);
    const list = capped ? hits.slice(0, SEARCH_CAP) : hits;
    const h = rows.push({ t: "header", key: `h:${g.id}`, level: 1, parent: -1, expandable: false, expanded: true, text: capped ? `${g.label.toUpperCase()} · FIRST ${SEARCH_CAP} OF ${hits.length.toLocaleString("en-US")}` : `${g.label.toUpperCase()} · ${hits.length.toLocaleString("en-US")}`, kind: g.id, capped }) - 1;
    for (const o of list) {
      const parent = o.kind === "trigger" && o.parentObjectId != null ? b.byId.get(o.parentObjectId) : undefined;
      rows.push({ t: "obj", key: `r:${objKey(profileId, db, o, parent)}`, level: 2, parent: h, expandable: false, expanded: false, profileId, db, obj: o, bare: o.schema === dflt, leaf: true });
    }
  }
  numberSiblings(rows);
  return { rows, total };
}

/** Ancestor rows of `index` (nearest last), for the sticky path when the tree is scrolled deep. */
export function ancestorsOf(rows: readonly TreeRow[], index: number): number[] {
  const out: number[] = [];
  for (let p = rows[index]?.parent ?? -1; p >= 0; p = rows[p].parent) out.unshift(p);
  return out;
}

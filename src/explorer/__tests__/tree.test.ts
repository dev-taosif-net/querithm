import { describe, expect, it } from "vitest";
import type { DatabaseInfo, Engine, Profile, SnapObject } from "../../ipc/contract";
import type { ServerConn } from "../../store/connections";
import type { SnapEntry } from "../../store/snapshots";
import { snapKey } from "../../store/snapshots";
import { buildTree, bucketsOf, catKey, dbKey, objKey, PACKAGE_BODY_HIDDEN, PACKAGE_MEMBERS_NOTE, PACKAGE_NO_BODY, schemaKey, searchRows, searchScope, SEARCH_CAP, serverKey, shownServers, sysKey, ancestorsOf, type TreeInput, type TreeRow } from "../tree";

const profile = (id: string, engine: Engine, over: Partial<Profile> = {}): Profile => ({ id, name: `Srv-${id}`, engine, environment: "dev", databaseDefault: "AppDb", groupName: null, color: null, ...over }) as Profile;
const conn = (id: string, over: Partial<ServerConn> = {}): ServerConn => ({ profileId: id, status: "connected", sessionId: "s", databases: [{ name: "AppDb", isSystem: false }], ...over });
const col = (name: string, over: object = {}) => ({ name, type: "int", nullable: true, identity: false, computed: false, pk: false, ordinal: 1, ...over });
const obj = (id: number, schema: string, name: string, kind: SnapObject["kind"], over: Partial<SnapObject> = {}): SnapObject => ({ id, schema, name, kind, ...over });
const entry = (id: string, db: string, objects: SnapObject[], over: Partial<SnapEntry> = {}): SnapEntry => ({ key: snapKey(id, db), profileId: id, database: db, status: "fresh", snapshot: { profileId: id, database: db, status: "fresh", objects, fks: [] }, version: 1, ...over });

function input(over: Partial<TreeInput> = {}): TreeInput {
  return {
    profiles: [], servers: {}, order: [], pinned: [], showAll: false, groupByEnv: false, collapsedEnvs: new Set(), expanded: new Set(),
    entries: {}, details: {}, schemaGroups: [], kind: "all", folderFilters: {}, ...over,
  };
}
const labels = (rows: TreeRow[]) => rows.map((r) => `${"  ".repeat(r.level - 1)}${r.t}:${r.t === "server" ? r.profileId : r.t === "db" ? r.db : r.t === "cat" ? r.cat : r.t === "obj" ? `${r.bare ? "" : `${r.obj.schema}.`}${r.obj.name}` : r.t === "schema" ? r.schema : r.t === "sub" ? r.sub : r.t === "leaf" ? r.text : r.t === "note" ? r.text : r.t === "action" ? r.action : r.t === "sysdbs" ? r.count : r.t === "env" ? r.env : r.t === "header" ? r.text : ""}`);

describe("which servers the tree lists (owner decision, revised 2026-10-30)", () => {
  const ps = [profile("a", "mssql", { environment: "prod" }), profile("b", "pg"), profile("c", "pg", { environment: "local" }), profile("d", "mssql", { environment: "test" }), profile("e", "pg", { environment: "staging" })];

  it("in use (connected, connecting, failed) and pinned; the other saved profiles are not listed, not even as a count row", () => {
    const r = buildTree(input({ profiles: ps, servers: { a: conn("a"), b: conn("b", { status: "connecting", databases: [] }), e: conn("e", { status: "error", error: "x" }) }, order: ["a", "b", "e"], pinned: ["d"] }));
    expect(r.shown).toEqual(["b", "d", "e", "a"]); // Local → Prod: dev, test, staging, prod
    // The header's Connect… reaches the rest; a "N saved connections" row only repeated it (owner decision, 2026-11-02).
    expect(r.rows.filter((x) => x.level === 1).map((x) => x.t)).toEqual(["server", "server", "server", "server"]);
    expect(r.rows.at(-1)).toMatchObject({ t: "server", profileId: "a" });
    expect(r.rows.find((x) => x.t === "server" && x.profileId === "d")).toMatchObject({ status: "off", pinned: true, expanded: false });
  });

  it("Show all saved profiles lists every one", () => {
    const r = buildTree(input({ profiles: ps, showAll: true }));
    expect(r.shown).toEqual(["c", "b", "d", "e", "a"]);
    expect(r.rows.map((x) => x.t)).toEqual(["server", "server", "server", "server", "server"]);
  });

  it("pinned profiles come first within their environment, never above a lower one; then the joining order", () => {
    const more = [...ps, profile("f", "pg"), profile("g", "mssql")];
    const order = shownServers({ profiles: more, servers: { b: conn("b"), f: conn("f"), g: conn("g") }, order: ["b", "f", "g"], pinned: ["g", "a"], showAll: false }).map((p) => p.id);
    expect(order).toEqual(["g", "b", "f", "a"]);
  });

  it("a server whose profile was deleted is not listed", () => {
    expect(buildTree(input({ profiles: [], servers: { x: conn("x") }, order: ["x"] })).rows).toEqual([]);
  });

  it("groups by environment, collapsibly", () => {
    const base = { profiles: ps, servers: { a: conn("a"), b: conn("b") }, order: ["a", "b"], groupByEnv: true };
    expect(labels(buildTree(input(base)).rows)).toEqual(["env:dev", "  server:b", "env:prod", "  server:a"]);
    expect(labels(buildTree(input({ ...base, collapsedEnvs: new Set(["prod"]) })).rows)).toEqual(["env:dev", "  server:b", "env:prod"]);
  });
});

describe.each<Engine>(["mssql", "pg"])("tree levels (%s)", (engine) => {
  const dflt = engine === "pg" ? "public" : "dbo";
  const objects = [
    obj(1, dflt, "Orders", "table", { columns: [col("Id", { pk: true, nullable: false }), col("Total", { type: "money" })] }),
    obj(2, dflt, "orders_view", "view", { columns: [col("Id")] }),
    obj(3, "sales", "do_work", "proc", { params: [{ name: "@a", type: "int", ordinal: 1, output: true, hasDefault: true }] }),
    obj(4, dflt, "calc", "func", { returnType: "int", params: [] }),
    obj(5, dflt, "seq1", "sequence"),
    obj(6, dflt, "tr_orders", "trigger", { parentObjectId: 1 }),
  ];
  const p = profile("p", engine, { databaseDefault: engine === "pg" ? "postgres" : "AppDb" });
  const dbs = engine === "pg" ? [{ name: "postgres", isSystem: true }, { name: "AppDb", isSystem: false }, { name: "template_x", isSystem: true }] : [{ name: "AppDb", isSystem: false }, { name: "master", isSystem: true }, { name: "msdb", isSystem: true }];
  const base = (over: Partial<TreeInput> = {}) => input({ profiles: [p], servers: { p: conn("p", { databases: dbs }) }, order: ["p"], entries: { [snapKey("p", "AppDb")]: entry("p", "AppDb", objects) }, ...over });

  it("server → databases (no 'Databases' level), system databases folded, the profile's default kept out of the folder", () => {
    const rows = buildTree(base({ expanded: new Set([serverKey("p")]) })).rows;
    expect(labels(rows)).toEqual(engine === "pg" ? ["server:p", "  db:postgres", "  db:AppDb", "  sysdbs:1"] : ["server:p", "  db:AppDb", "  sysdbs:2"]);
    const open = buildTree(base({ expanded: new Set([serverKey("p"), sysKey("p")]) })).rows;
    expect(labels(open).filter((l) => l.startsWith("    "))).toEqual(engine === "pg" ? ["    db:template_x"] : ["    db:master", "    db:msdb"]);
  });

  it("a database lists the four main categories always and the others when they hold objects; default-schema names are bare", () => {
    const rows = buildTree(base({ expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), catKey("p", "AppDb", "tables"), catKey("p", "AppDb", "procs")]) })).rows;
    const l = labels(rows);
    expect(l).toEqual(expect.arrayContaining(["    cat:tables", "      obj:Orders", "    cat:views", "    cat:procs", "      obj:sales.do_work", "    cat:funcs", "    cat:sequences"]));
    expect(l.some((x) => x.includes("cat:types") || x.includes("cat:synonyms"))).toBe(false);
    expect(rows.find((r) => r.t === "cat" && r.cat === "tables")).toMatchObject({ count: 1, expandable: true, expanded: true });
  });

  it("a kind filter keeps only that category", () => {
    const rows = buildTree(base({ kind: "views", expanded: new Set([serverKey("p"), dbKey("p", "AppDb")]) })).rows;
    expect(rows.filter((r) => r.t === "cat").map((r) => (r as Extract<TreeRow, { t: "cat" }>).cat)).toEqual(["views"]);
  });

  it("an object expands to columns / keys / indexes / triggers; triggers sit under their table only", () => {
    const oKey = objKey("p", "AppDb", objects[0]);
    const rows = buildTree(base({ expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), catKey("p", "AppDb", "tables"), oKey, `${oKey}:columns`, `${oKey}:triggers`]) })).rows;
    const l = labels(rows);
    expect(l).toEqual(expect.arrayContaining(["        sub:columns", "          leaf:Id", "          leaf:Total", "        sub:keys", "        sub:indexes", "        sub:triggers", "          obj:tr_orders"]));
    expect(rows.find((r) => r.t === "leaf" && r.text === "Id")).toMatchObject({ icon: "key", tone: "warn", detail: "int NOT NULL", drag: "Id", column: "Id" });
    expect(l.filter((x) => x.includes("tr_orders"))).toHaveLength(1);
  });

  it("keys and indexes ask for details, show them for the object's current record and offer Retry after an error", () => {
    const oKey = objKey("p", "AppDb", objects[0]);
    const exp = new Set([serverKey("p"), dbKey("p", "AppDb"), catKey("p", "AppDb", "tables"), oKey, `${oKey}:keys`, `${oKey}:indexes`]);
    const missing = buildTree(base({ expanded: exp }));
    expect(missing.needDetails.map((n) => n.key)).toEqual([oKey, oKey]);
    const d = { columns: [], params: [], indexes: [{ name: "PK_Orders", isUnique: true, isPrimary: true, columns: ["Id"] }], foreignKeys: [{ name: "FK_x@1", parent: 1, referenced: 2, columns: [{ parent: "Id", referenced: "Id" }] }], referencedBy: [] };
    const ok = buildTree(base({ expanded: exp, details: { [oKey]: { o: objects[0], d } } }));
    expect(ok.needDetails).toEqual([]);
    expect(labels(ok.rows)).toEqual(expect.arrayContaining(["          leaf:PK_Orders", "          leaf:FK_x"]));
    // A refresh that changed the object replaced its record: the details are read again.
    const stale = buildTree(base({ expanded: exp, details: { [oKey]: { o: { ...objects[0] }, d } } }));
    expect(stale.needDetails).toHaveLength(2);
    const failed = buildTree(base({ expanded: exp, details: { [oKey]: { o: objects[0], d: "timeout" } } }));
    expect(failed.rows.filter((r) => r.t === "action").map((r) => (r as Extract<TreeRow, { t: "action" }>).action)).toEqual(["retryDetails", "retryDetails"]);
  });

  it("group by schema: the engine's default schema first, only non-empty categories, names bare", () => {
    const rows = buildTree(base({ schemaGroups: [`p|AppDb`], expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), schemaKey("p", "AppDb", dflt), schemaKey("p", "AppDb", "sales"), catKey("p", "AppDb", "procs", "sales")]) })).rows;
    const l = labels(rows).filter((x) => /schema:|cat:|obj:/.test(x));
    expect(l[0]).toBe(`    schema:${dflt}`);
    expect(l).toEqual(expect.arrayContaining(["    schema:sales", "      cat:procs", "        obj:do_work"]));
    expect(l.filter((x) => x.startsWith("      cat:")).length).toBeLessThan(8);
    expect(rows.find((r) => r.t === "schema" && r.schema === dflt)).toMatchObject({ count: 4 }); // table, view, function, sequence: the trigger is not counted
  });

  it("group by schema over 25,000 objects in one schema builds in linear time (the old grouping copied arrays per object)", () => {
    const many = Array.from({ length: 25_000 }, (_, i) => obj(i + 1, dflt, `t${i}`, "table"));
    const t0 = performance.now();
    const rows = buildTree(base({ entries: { [snapKey("p", "AppDb")]: entry("p", "AppDb", many) }, schemaGroups: ["p|AppDb"], expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), schemaKey("p", "AppDb", dflt), catKey("p", "AppDb", "tables", dflt)]) })).rows;
    expect(rows.filter((r) => r.t === "obj")).toHaveLength(25_000);
    expect(performance.now() - t0).toBeLessThan(1000);
  });

  it("type to filter a folder: the folder shows how many match, a filter row, and a note when none does", () => {
    const k = catKey("p", "AppDb", "tables");
    const rows = buildTree(base({ expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), k]), folderFilters: { [k]: "ORD" } })).rows;
    expect(rows.find((r) => r.key === k)).toMatchObject({ shown: 1, count: 1, filter: "ORD" });
    expect(rows.find((r) => r.t === "filter")).toMatchObject({ text: "ORD", shown: 1, total: 1 });
    const none = buildTree(base({ expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), k]), folderFilters: { [k]: "zzz" } })).rows;
    expect(none.some((r) => r.t === "note" && r.text.includes("zzz"))).toBe(true);
  });

  it("database states: loading, refreshing, stale (refresh failed over cached objects), error, offline", () => {
    const st = (e: Partial<SnapEntry> | null, d: DatabaseInfo[] = dbs) => buildTree(base({ servers: { p: conn("p", { databases: d }) }, entries: e ? { [snapKey("p", "AppDb")]: { ...entry("p", "AppDb", objects), ...e } } : {}, expanded: new Set([serverKey("p"), dbKey("p", "AppDb")]) })).rows.find((r) => r.t === "db" && r.db === "AppDb");
    expect(st(null)).toMatchObject({ state: "idle" });
    expect(st({ status: "loading", snapshot: undefined })).toMatchObject({ state: "loading" });
    expect(st({ status: "refreshing" })).toMatchObject({ state: "refreshing" });
    expect(st({ status: "error", error: "Timeout" })).toMatchObject({ state: "stale", error: "Timeout" });
    expect(st({ status: "error", error: "Timeout", snapshot: { objects: [] } as never })).toMatchObject({ state: "error" });
    expect(st(null, [{ name: "AppDb", isSystem: false, state: "OFFLINE" }])).toMatchObject({ state: "offline", expandable: false, dbState: "OFFLINE" });
  });

  it("a failed server expanded by the user shows the reason, Reconnect and Edit profile; collapsed it shows nothing", () => {
    const failed = { servers: { p: conn("p", { status: "error", error: "Connection timed out after 15 s.\nat Npgsql…" }) } };
    expect(labels(buildTree(base(failed)).rows)).toEqual(["server:p"]);
    expect(labels(buildTree(base({ ...failed, expanded: new Set([serverKey("p")]) })).rows)).toEqual(["server:p", "  note:Connection timed out after 15 s.", "  action:reconnect", "  action:editProfile"]);
  });

  it("an engine restart (connecting, databases known) keeps the subtree; a first connect says Connecting…", () => {
    const exp = new Set([serverKey("p"), dbKey("p", "AppDb")]);
    expect(labels(buildTree(base({ servers: { p: conn("p", { status: "connecting", databases: dbs }) }, expanded: exp })).rows)).toContain("    cat:tables");
    expect(labels(buildTree(base({ servers: { p: conn("p", { status: "connecting", databases: [] }) }, expanded: exp })).rows)).toEqual(["server:p", "  note:Connecting…"]);
  });

  it("keys are names: an object recreated with a new id keeps its key; PostgreSQL overloads get their argument types", () => {
    expect(objKey("p", "AppDb", objects[0])).toBe(objKey("p", "AppDb", { ...objects[0], id: 999 }));
    const f1 = obj(10, dflt, "f", "func", { params: [{ name: "a", type: "int", ordinal: 1, output: false, hasDefault: false }] });
    const f2 = obj(11, dflt, "f", "func", { params: [{ name: "a", type: "text", ordinal: 1, output: false, hasDefault: false }] });
    expect(objKey("p", "AppDb", f1)).not.toBe(objKey("p", "AppDb", f2));
    const t1 = obj(12, dflt, "audit", "trigger", { parentObjectId: 1 });
    expect(objKey("p", "AppDb", t1, objects[0])).toBe(`o:p:AppDb:trigger:${dflt}.audit@${dflt}.Orders`);
  });

  it("numbers siblings for aria-posinset / aria-setsize, and walks ancestors for the sticky path", () => {
    const rows = buildTree(base({ expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), catKey("p", "AppDb", "tables")]) })).rows;
    const tables = rows.findIndex((r) => r.t === "cat" && r.cat === "tables");
    const orders = rows.findIndex((r) => r.t === "obj");
    expect(rows[tables]).toMatchObject({ pos: 1, size: engine === "pg" ? 5 : 5 });
    expect(ancestorsOf(rows, orders).map((i) => rows[i].t)).toEqual(["server", "db", "cat"]);
  });

  // ---- 2026-10-30 review fixes

  it("asks for the catalog of an expanded database only while it is in view, not for every restored key", () => {
    const sys = engine === "pg" ? "template_x" : "master";
    const noEntries = { entries: {} };
    // Restored keys under a collapsed server and a collapsed System databases folder: nothing to read yet.
    expect(buildTree(base({ ...noEntries, expanded: new Set([dbKey("p", "AppDb"), dbKey("p", sys)]) })).needCatalog).toEqual([]);
    expect(buildTree(base({ ...noEntries, expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), dbKey("p", sys)]) })).needCatalog).toEqual([{ profileId: "p", db: "AppDb" }]);
    expect(buildTree(base({ ...noEntries, expanded: new Set([serverKey("p"), sysKey("p"), dbKey("p", sys)]) })).needCatalog).toEqual([{ profileId: "p", db: sys }]);
    // Read already: nothing to ask for.
    expect(buildTree(base({ expanded: new Set([serverKey("p"), dbKey("p", "AppDb")]) })).needCatalog).toEqual([]);
  });

  it("a catalog the sidecar has not read yet (empty, stale) reads as loading, not as an empty database", () => {
    const exp = new Set([serverKey("p"), dbKey("p", "AppDb")]);
    for (const status of ["empty", "stale"] as const) {
      const rows = buildTree(base({ expanded: exp, entries: { [snapKey("p", "AppDb")]: entry("p", "AppDb", [], { status }) } })).rows;
      expect(rows.find((r) => r.t === "note")).toMatchObject({ busy: true });
      expect(labels(rows)).not.toContain("    note:No objects");
    }
    const read = buildTree(base({ expanded: exp, entries: { [snapKey("p", "AppDb")]: entry("p", "AppDb", []) } })).rows;
    expect(labels(read)).toContain("    note:No objects");
  });

  it("a kind filter that leaves an open database empty says so (flat and grouped by schema)", () => {
    const exp = new Set([serverKey("p"), dbKey("p", "AppDb")]);
    const flat = (kind: TreeInput["kind"]) => labels(buildTree(base({ expanded: exp, kind })).rows).filter((x) => x.startsWith("    "));
    expect(flat("triggers")).toEqual(["    note:Triggers are under their tables; search lists them"]);
    expect(flat("synonyms")).toEqual(["    note:No synonyms"]);
    const grouped = labels(buildTree(base({ expanded: exp, kind: "synonyms", schemaGroups: ["p|AppDb"] })).rows).filter((x) => x.startsWith("    "));
    expect(grouped).toEqual(["    note:No synonyms"]);
  });

  it("two objects with the same name key (PostgreSQL f(a int, OUT b int) beside f(a int, b int)) get distinct keys", () => {
    const params = (outB: boolean) => [{ name: "a", type: "integer", ordinal: 1, output: false, hasDefault: false }, { name: "b", type: "integer", ordinal: 2, output: outB, hasDefault: false }];
    const fns = [obj(20, dflt, "f", "func", { params: params(true) }), obj(21, dflt, "f", "func", { params: params(false) })];
    const rows = buildTree(base({ entries: { [snapKey("p", "AppDb")]: entry("p", "AppDb", fns) }, expanded: new Set([serverKey("p"), dbKey("p", "AppDb"), catKey("p", "AppDb", "funcs")]) })).rows;
    const keys = rows.filter((r) => r.t === "obj").map((r) => r.key);
    expect(keys).toHaveLength(2);
    expect(new Set(keys).size).toBe(2);
    expect(keys[1]).toBe(`${keys[0]}#21`);
  });
});

describe("search on a session-owned engine (SQLite, Phase D0d)", () => {
  const servers = { f: conn("f", { databases: [{ name: "main", isSystem: false }] }), m: conn("m", { sessionId: undefined, databases: [], memory: true }) };
  it("a tab with a session searches its own view (temporary tables included), never the shared entry", () => {
    const shared = { [snapKey("f", "main")]: entry("f", "main", [obj(1, "main", "orders", "table")]) };
    const own = { [snapKey("f", "main", "s1")]: { ...entry("f", "main", [obj(1, "main", "orders", "table"), obj(9, "temp", "scratch", "table")]), key: snapKey("f", "main", "s1"), sessionId: "s1" } };
    const ready = searchScope({ profileId: "f", database: "main", sessionId: "s1" }, servers, { ...shared, ...own });
    expect(ready).toMatchObject({ state: "ready", sessionId: "s1" });
    expect(ready.state === "ready" && ready.objects.map((o) => o.name)).toEqual(["orders", "scratch"]);
    // Not loaded yet: loading, naming the session so the view (not the shared snapshot) is asked for.
    expect(searchScope({ profileId: "f", database: "main", sessionId: "s2" }, servers, shared)).toEqual({ state: "loading", profileId: "f", db: "main", sessionId: "s2" });
  });
  it("an in-memory tab whose session has not opened has nothing to search yet (not a spinner that never ends)", () => {
    expect(searchScope({ profileId: "m", database: "main", noDatabaseYet: true }, servers, {})).toEqual({ state: "ready", profileId: "m", db: "main", objects: [], stale: false });
  });
});

describe.each<Engine>(["mssql", "pg"])("search (%s)", (engine) => {
  const dflt = engine === "pg" ? "public" : "dbo";
  const servers = { p: conn("p") };

  it("scope is the active tab's database, never another server (B1); states for no tab, no database, server away, loading", () => {
    expect(searchScope(undefined, servers, {})).toEqual({ state: "noTab" });
    // A tab that exists but has no connection is its own state: "no query tab" would be wrong.
    expect(searchScope({}, servers, {})).toEqual({ state: "noConn" });
    expect(searchScope({ profileId: "p" }, servers, {})).toEqual({ state: "noDb", profileId: "p" });
    expect(searchScope({ profileId: "q", database: "AppDb" }, servers, {})).toMatchObject({ state: "server", status: "off" });
    expect(searchScope({ profileId: "p", database: "AppDb" }, { p: conn("p", { status: "error", error: "down" }) }, {})).toMatchObject({ state: "server", status: "error", error: "down" });
    expect(searchScope({ profileId: "p", database: "AppDb" }, servers, {})).toMatchObject({ state: "loading" });
    expect(searchScope({ profileId: "p", database: "AppDb" }, servers, { [snapKey("p", "AppDb")]: entry("p", "AppDb", [], { status: "error", error: "no access" }) })).toMatchObject({ state: "error", error: "no access" });
    const objects = [obj(1, dflt, "Orders", "table")];
    expect(searchScope({ profileId: "p", database: "AppDb" }, { p: conn("p", { status: "error" }) }, { [snapKey("p", "AppDb")]: entry("p", "AppDb", objects) })).toMatchObject({ state: "ready", stale: true });
  });

  it("groups by kind, prefix matches first, and gives true counts above the cap (B3)", () => {
    const objects = [obj(1, dflt, "OrderLines", "table"), obj(2, dflt, "Orders", "table"), obj(3, dflt, "BackOrders", "table"), obj(4, "sales", "orders_v", "view"), obj(5, dflt, "trg_orders", "trigger", { parentObjectId: 2 })];
    const r = searchRows("p", "AppDb", engine, objects, "order", "all", new Set());
    expect(r.total).toBe(5);
    expect(labels(r.rows)).toEqual(["header:TABLES · 3", "  obj:Orders", "  obj:OrderLines", "  obj:BackOrders", "header:VIEWS · 1", "  obj:sales.orders_v", "header:TRIGGERS · 1", "  obj:trg_orders"]);
    const many = Array.from({ length: SEARCH_CAP + 86 }, (_, i) => obj(i + 1, dflt, `t${i}`, "table"));
    const capped = searchRows("p", "AppDb", engine, many, "t", "all", new Set());
    expect(capped.rows[0]).toMatchObject({ t: "header", text: `TABLES · FIRST 300 OF ${SEARCH_CAP + 86}`, capped: true });
    expect(capped.rows.filter((x) => x.t === "obj")).toHaveLength(SEARCH_CAP);
    expect(searchRows("p", "AppDb", engine, many, "t", "all", new Set(["tables"])).rows.filter((x) => x.t === "obj")).toHaveLength(SEARCH_CAP + 86);
    expect(searchRows("p", "AppDb", engine, objects, "order", "views", new Set()).total).toBe(1);
  });
});

describe("snapshot buckets", () => {
  it("are built once per snapshot and hold triggers by table", () => {
    const objects = [obj(1, "dbo", "T", "table"), obj(2, "dbo", "b_tr", "trigger", { parentObjectId: 1 }), obj(3, "dbo", "a_tr", "trigger", { parentObjectId: 1 })];
    const b = bucketsOf(objects);
    expect(bucketsOf(objects)).toBe(b);
    expect(b.triggers.get(1)!.map((o) => o.name)).toEqual(["a_tr", "b_tr"]);
    expect(b.schemaCount.get("dbo")).toBe(1);
  });
});

describe("Oracle catalog notes (D4 S05)", () => {
  const p = profile("o", "oracle" as Engine, { databaseDefault: "FREEPDB1" });
  const objects = [obj(1, "APP", "T", "table"), obj(2, "APP", "MV", "view", { note: "materialized view" }), obj(3, "APP", "F", "func", { note: "invalid" })];
  const note = "Triggers are not listed: this login cannot read ALL_TRIGGERS (ORA-00942).";
  const e = entry("o", "FREEPDB1", objects);
  const tree = (notes: string[] | null) => buildTree(input({
    profiles: [p], servers: { o: conn("o", { databases: [{ name: "FREEPDB1", isSystem: false }] }) }, order: ["o"],
    entries: { [snapKey("o", "FREEPDB1")]: { ...e, snapshot: { ...e.snapshot!, notes } } },
    expanded: new Set([serverKey("o"), dbKey("o", "FREEPDB1")]),
  })).rows;

  it("a fresh snapshot that left something out says so under its database, as a warning, above the objects it could read", () => {
    const rows = tree([note]);
    const db = rows.findIndex((r) => r.t === "db");
    expect(rows[db + 1]).toMatchObject({ t: "note", text: note, tone: "warn" });
    expect(rows.some((r) => r.t === "cat")).toBe(true);
  });

  it("no notes, no rows for them", () => {
    expect(tree(null).some((r) => r.t === "note")).toBe(false);
    expect(tree([]).some((r) => r.t === "note")).toBe(false);
  });
});

describe("Oracle packages (D4 S08)", () => {
  const p = profile("o", "oracle", { databaseDefault: "FREEPDB1" });
  const objects = [
    obj(1, "APP", "PKG", "package", { members: ["P(A IN NUMBER)", "P(A IN VARCHAR2)", "F(X IN DATE) RETURN NUMBER"] }),
    obj(2, "APP", "PKG", "packageBody", { specObjectId: 1 }),
    obj(3, "APP", "LONE", "package", { note: "no body", members: [] }),
    obj(4, "OWN", "SHARED_PKG", "package", { note: "body not visible", members: ["P(A IN NUMBER)"] }),
    obj(5, "APP", "PKG", "proc"),
  ];
  const tree = (expanded: string[]) => buildTree(input({
    profiles: [p], servers: { o: conn("o", { databases: [{ name: "FREEPDB1", isSystem: false }] }) }, order: ["o"],
    entries: { [snapKey("o", "FREEPDB1")]: entry("o", "FREEPDB1", objects) },
    expanded: new Set([serverKey("o"), dbKey("o", "FREEPDB1"), ...expanded]),
  })).rows;
  const pkgKey = objKey("o", "FREEPDB1", objects[0]);

  it("a Packages category lists the specifications only: each body sits under its specification", () => {
    const rows = tree([catKey("o", "FREEPDB1", "packages")]);
    expect(rows.find((r) => r.t === "cat" && r.cat === "packages")).toMatchObject({ count: 3 });
    expect(rows.filter((r) => r.t === "obj").map((r) => r.t === "obj" && `${r.obj.schema}.${r.obj.name}:${r.obj.kind}`)).toEqual(["APP.PKG:package", "APP.LONE:package", "OWN.SHARED_PKG:package"]);
    // The procedure of the same name stays in Procedures: it is not part of the package.
    expect(rows.find((r) => r.t === "cat" && r.cat === "procs")).toMatchObject({ count: 1 });
  });

  it("an open specification shows its body as an object of its own, then its members with what they cannot do yet", () => {
    const rows = tree([catKey("o", "FREEPDB1", "packages"), pkgKey, `${pkgKey}:members`]);
    const at = rows.findIndex((r) => r.t === "obj" && r.obj.id === 1);
    expect(rows[at + 1]).toMatchObject({ t: "obj", obj: { id: 2, kind: "packageBody" }, leaf: true, parent: at });
    expect(rows[at + 2]).toMatchObject({ t: "sub", sub: "members" });
    expect(rows.slice(at + 3, at + 7).map((r) => (r.t === "leaf" ? `${r.icon}:${r.text}` : r.t === "note" ? r.text : r.t))).toEqual([
      "proc:P(A IN NUMBER)", "proc:P(A IN VARCHAR2)", "func:F(X IN DATE) RETURN NUMBER", PACKAGE_MEMBERS_NOTE,
    ]);
    expect(new Set(rows.map((r) => r.key)).size).toBe(rows.length); // the body's key is not its specification's
  });

  it("a specification without a body says why: none, or not visible to this login", () => {
    const lone = objKey("o", "FREEPDB1", objects[2]);
    const shared = objKey("o", "FREEPDB1", objects[3]);
    const rows = tree([catKey("o", "FREEPDB1", "packages"), lone, shared, `${lone}:members`]);
    expect(rows.find((r) => r.key === `${lone}:nobody`)).toMatchObject({ t: "note", text: PACKAGE_NO_BODY, tone: "muted" });
    expect(rows.find((r) => r.key === `${shared}:nobody`)).toMatchObject({ t: "note", text: PACKAGE_BODY_HIDDEN, tone: "warn" });
    expect(rows.find((r) => r.key === `${lone}:members:note`)).toMatchObject({ text: "No members" });
  });

  it("search lists specifications and bodies under Packages", () => {
    const r = searchRows("o", "FREEPDB1", "oracle", objects, "pkg", "all", new Set());
    expect(r.rows.filter((x) => x.t === "header").map((x) => x.t === "header" && x.text)).toEqual(["PROCEDURES · 1", "PACKAGES · 3"]);
    expect(r.rows.filter((x) => x.t === "obj").map((x) => x.t === "obj" && x.obj.kind)).toEqual(["proc", "package", "packageBody", "package"]);
  });

  it("no other engine's tree gains a Packages folder", () => {
    const rows = buildTree(input({
      profiles: [profile("g", "pg")], servers: { g: conn("g") }, order: ["g"], entries: { [snapKey("g", "AppDb")]: entry("g", "AppDb", [obj(1, "public", "t", "table")]) },
      expanded: new Set([serverKey("g"), dbKey("g", "AppDb")]),
    })).rows;
    expect(rows.some((r) => r.t === "cat" && r.cat === "packages")).toBe(false);
  });
});

// Context menus of the Explorer's rows (§10): every action of the old flat menus, grouped — open, Script as ▸,
// insert / copy, analyze, tools, Refresh — with the default action first and bold. An action the row cannot take is
// shown with the reason instead of disappearing (Debug on SQL Server, Rename on a synonym, server tools while not
// connected).
import type { MenuEntry } from "../app/ToolbarMenu";
import type { NamespaceModel, ObjectKind, ScriptKind, SnapObject } from "../ipc/contract";
import { engineTraits, ENGINE_LIST, ENGINES, knownEngine } from "../engines";
import { capabilityRefusal, MEMORY_NOTE, useConnections, type CapabilityFeature } from "../store/connections";
import { useProfiles } from "../store/profiles";
import { schemaGroupKey, useSettings } from "../store/settings";
import { ownSessionFor, sessionParam, useSnapshots } from "../store/snapshots";
import { useUi } from "../store/ui";
import { useTabs } from "../store/tabs";
import { disconnectServer, newQueryTab, openDefinition, scriptObject } from "../app/actions";
import { startDebug } from "../debug/startDebug";
import { openErdForDatabase, openErdForTable } from "../erd/erdActions";
import { openDataCompare, openSchemaCompare } from "../compare/compareActions";
import { openActivityMonitor } from "../monitor/monitorActions";
import { findUsagesOf, renameOf } from "../refactor/refactorActions";
import { isFileProfile, repickFile, revokeFileAccess } from "../connections/fileAccess";
import { rpc } from "../ipc/client";
import { toast } from "../store/ui";
import { bindActiveTab, columnName, connectFromExplorer, copyText, insertName, insertText, modifyTrigger, openObject, qualified, ref, scriptDml } from "./objectActions";
import { CATEGORIES, type Engine, type RowOf, type TreeRow } from "./tree";
import { useExplorer } from "./explorerState";

/** Script as ▸ the statement that runs a routine; null where the engine has no routines (SQLite). */
const EXECUTE_LABEL: Record<Engine, ((kind: ObjectKind) => string) | null> = {
  mssql: () => "EXECUTE",
  pg: (kind) => (kind === "proc" ? "CALL" : "SELECT"),
  sqlite: null,
  mySql: (kind) => (kind === "proc" ? "CALL" : "SELECT"),
  // Macros are called in SELECT; a table macro in FROM.
  duckDb: () => "SELECT",
  // Phase D4 placeholder (hidden engine): routine scripting arrives with S11.
  oracle: null,
};
/**
 * Why a trigger cannot be enabled or disabled from here: `always` where the engine has no such statement, `onView`
 * where it refuses a trigger on a view.
 */
const TRIGGER_TOGGLE: Record<Engine, { always?: string; onView?: string }> = {
  mssql: {},
  pg: { onView: "ALTER TABLE does not accept views" },
  sqlite: { always: "SQLite cannot disable a trigger; drop and create it instead" },
  mySql: { always: "MySQL cannot disable a trigger; drop and create it instead" },
  duckDb: { always: "DuckDB has no triggers" },
  oracle: { always: "Enabling and disabling Oracle triggers is not available yet" },
};
/**
 * "Group by …" on a database, named after the level its objects are grouped in (`Capabilities.namespaces`): schemas,
 * SQLite's attached databases (main, temp), or none below the database (MySQL, where the database is the schema).
 */
export const NAMESPACE_GROUP: Record<NamespaceModel, { label: string; unavailable?: string }> = {
  schemas: { label: "Group by schema" },
  attached: { label: "Group by attached database" },
  databases: { label: "Group by schema", unavailable: "The database is the schema here" },
};
/** "PostgreSQL only": the engines with a step debugger. */
const DEBUGGER_ONLY = `${ENGINE_LIST.filter((e) => ENGINES[e].debugger).map((e) => ENGINES[e].shortLabel).join(", ")} only`;

export interface MenuContext {
  /** The object a trigger belongs to (PostgreSQL cannot ENABLE / DISABLE a trigger on a view). */
  parent?: SnapObject;
  /** Reveal a search result in the tree. */
  reveal?: () => void;
}

type Item = Exclude<MenuEntry, "sep">;
const item = (label: string, run: () => void, more: Partial<Item> = {}): Item => ({ label, run, ...more });

/** Session-bound actions wait for a server that is reconnecting or failed instead of asking for a password from a
 *  double-click (the cached tree stays visible through an engine restart). */
export function sessionReason(profileId: string): string | undefined {
  const st = useConnections.getState().servers[profileId]?.status;
  if (st === "connected") return undefined;
  const name = useProfiles.getState().byId(profileId)?.name ?? "the server";
  return st === "connecting" ? `Waiting for ${name} to reconnect` : "Reconnect first";
}

function scriptItems(profileId: string, db: string, o: SnapObject, ctx: MenuContext): MenuEntry[] {
  const r = ref(o);
  const s = (label: string, kind: ScriptKind, unavailable?: string) => item(label, () => void scriptObject(profileId, db, r, kind), { unavailable });
  const engine = useProfiles.getState().byId(profileId)?.engine;
  const out: MenuEntry[] = [s("CREATE", "create")];
  if (o.kind !== "table") out.push(s("ALTER", "alter"));
  out.push(s("DROP and CREATE", "dropCreate"), s("DROP", "drop"));
  if (o.kind === "table") out.push("sep", item("INSERT", () => scriptDml(profileId, db, o, "insert")), item("UPDATE", () => scriptDml(profileId, db, o, "update")), item("DELETE", () => scriptDml(profileId, db, o, "delete")));
  const execute = EXECUTE_LABEL[knownEngine(engine)];
  if (execute && (o.kind === "proc" || o.kind === "func" || o.kind === "tvf")) out.push("sep", s(execute(o.kind), "execute"));
  if (o.kind === "trigger") {
    const toggle = TRIGGER_TOGGLE[knownEngine(engine)];
    const onView = toggle.always ?? (ctx.parent?.kind === "view" ? toggle.onView : undefined);
    out.push("sep", s("ENABLE", "enable", onView), s("DISABLE", "disable", onView));
  }
  return out;
}

/** The sidecar's reason a feature is off on this server's engine (SQLite and DuckDB have no schema or data compare). */
const refused = (profileId: string, feature: CapabilityFeature) => capabilityRefusal(useConnections.getState().servers, profileId, feature);

/** The import wizard on a database; a session-owned engine's goes through a query tab session on it (Phase D0d). */
const importInto = (profileId: string, database: string, target: { schema?: string; table?: string } = {}) =>
  useUi.getState().openDialog({ kind: "import", profileId, database, ...target, ...sessionParam(ownSessionFor(profileId, database)) });

const refreshDb = (profileId: string, db: string) => item(`Refresh “${db}”`, () => void useSnapshots.getState().refresh(profileId, db, false), { hint: "F5" });

export function objectMenu(row: RowOf<"obj">, ctx: MenuContext = {}): MenuEntry[] {
  const { profileId, db, obj: o } = row;
  const engine = useProfiles.getState().byId(profileId)?.engine;
  const waiting = sessionReason(profileId);
  const routine = o.kind === "proc" || o.kind === "func" || o.kind === "tvf";
  const out: MenuEntry[] = [];
  if (o.kind === "trigger") out.push(item(engineTraits(engine).triggerFunctions ? "Modify trigger function" : "Modify", () => void modifyTrigger(profileId, db, o), { bold: true, hint: "Double-click", unavailable: waiting }));
  else if (o.kind === "table" || o.kind === "view") out.push(item("Select top 1000", () => openObject(profileId, db, o), { bold: true, detail: "runs", hint: "Double-click", unavailable: waiting }));
  if (o.kind === "table") out.push(item("Count rows", () => void countRows(profileId, db, o), { unavailable: waiting }));
  if (o.kind === "view" || routine) out.push(item("Modify", () => void openDefinition(profileId, db, ref(o), true), { bold: o.kind !== "view", hint: o.kind !== "view" ? "Double-click" : undefined, unavailable: waiting }));
  if (routine) {
    const traits = engineTraits(engine);
    out.push(item("Execute…", () => useUi.getState().openDialog({ kind: "execProc", profileId, database: db, objectId: o.id }), { unavailable: traits.procedures ? waiting : `${traits.shortLabel} has no stored procedures` }));
    out.push(item("Debug…", () => void startDebug(profileId, db, o), { unavailable: engineTraits(engine).debugger ? waiting : DEBUGGER_ONLY }));
  }
  if (out.length) out.push("sep");
  out.push(item("Script as", () => {}, { detail: "new tab, nothing runs", sub: scriptItems(profileId, db, o, ctx), unavailable: waiting }));
  out.push("sep");
  out.push(item("Insert name", () => insertName(o, profileId), { hint: "Ctrl+Enter" }));
  if (o.columns?.length) out.push(item("Insert column list", () => insertName(o, profileId, true), { hint: "Ctrl+Alt+Enter" }));
  out.push(item("Copy name", () => void copyText(qualified(o, engine)), { hint: "Ctrl+C" }));
  out.push("sep");
  if (o.kind !== "trigger") {
    const noRefactor = o.kind === "synonym" ? "Not for synonyms" : waiting;
    out.push(item("Find usages", () => findUsagesOf(profileId, db, { schema: o.schema, name: o.name, kind: o.kind }), { hint: "Shift+F12", unavailable: noRefactor }));
    out.push(item("Rename…", () => renameOf(profileId, db, { schema: o.schema, name: o.name, kind: o.kind }), { hint: "F2", unavailable: noRefactor }));
  }
  out.push(item("Dependencies", () => useUi.getState().openDialog({ kind: "dependencies", profileId, database: db, schema: o.schema, name: o.name, objectId: o.id, kind2: o.kind }), { unavailable: waiting }));
  if (o.kind === "table" || o.kind === "view") {
    out.push("sep");
    if (o.kind === "table") out.push(item("View ER diagram", () => openErdForTable(profileId, db, o.id, o.name), { unavailable: waiting }));
    out.push(item("Compare data…", () => openDataCompare({ profileId, database: db, table: `${o.schema}.${o.name}` }), { unavailable: waiting ?? refused(profileId, "dataCompare") }));
    if (o.kind === "table") out.push(item("Import data into this table…", () => importInto(profileId, db, { schema: o.schema, table: o.name }), { unavailable: waiting }));
  }
  out.push("sep");
  if (ctx.reveal) out.push(item("Reveal in tree", ctx.reveal));
  out.push(refreshDb(profileId, db));
  return out;
}

async function countRows(profileId: string, db: string, o: SnapObject) {
  try {
    const sid = await useConnections.getState().sessionFor(profileId);
    const counts = await rpc("meta.rowCounts", { sessionId: sid, database: db, objectIds: [o.id] });
    const count = counts[0];
    if (count && count.rows != null) toast("info", `${o.schema}.${o.name}: ${count.rows.toLocaleString()} rows${count.estimated ? " (estimated)" : ""}`);
    else toast("warn", `Could not count rows for ${o.schema}.${o.name}`);
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

export function serverMenu(row: RowOf<"server">): MenuEntry[] {
  const id = row.profileId;
  const settings = useSettings.getState();
  const pinned = settings.pinnedProfiles.includes(id);
  const pin = item("Pin to top", () => settings.set({ pinnedProfiles: pinned ? settings.pinnedProfiles.filter((x) => x !== id) : [...settings.pinnedProfiles, id] }), { checked: pinned, detail: "stays in the Explorer" });
  const edit = item("Edit profile…", () => useUi.getState().openDialog({ kind: "profile", profileId: id }));
  const duplicate = item("Duplicate profile…", () => useUi.getState().openDialog({ kind: "profile", copyOf: id }));
  const off = row.status !== "connected" ? "Not connected" : undefined;
  const profile = useProfiles.getState().byId(id);
  const traits = engineTraits(profile?.engine);
  // A database file (Phase D0c): pick it again, or take Querithm's access to it away.
  const file: MenuEntry[] = isFileProfile(profile)
    ? [
        item("Re-pick file…", () => void repickFile(id), { detail: "read-only until writes are allowed" }),
        item("Revoke file access…", () => void revokeFileAccess(id), { unavailable: profile?.file ? undefined : "No file picked" }),
      ]
    : [];
  const tools: MenuEntry[] = [
    item("Refresh databases", () => void useConnections.getState().refreshDatabases(id).catch((e) => toast("error", `Could not refresh databases: ${e instanceof Error ? e.message : String(e)}. Choose Refresh databases to retry.`)), { hint: "F5", unavailable: off ?? (row.memory ? MEMORY_NOTE : undefined) }),
    item("Activity monitor", () => openActivityMonitor(id), { unavailable: off ?? (traits.monitor ? undefined : `${traits.shortLabel} has no activity monitor`) }),
  ];
  const sessions = Object.values(useTabs.getState().tabs).filter((t) => t.profileId === id && t.sessionId).length;
  const disconnect = item("Disconnect", () => void disconnectServer(id), { detail: row.status === "error" ? "stops retrying" : row.status === "connecting" ? "stops connecting" : sessions ? `closes ${sessions} tab session${sessions === 1 ? "" : "s"}` : undefined });
  if (row.status === "off")
    return [item("Connect", () => void connectFromExplorer(id), { bold: true, hint: "Enter" }), item("New query", () => void connectFromExplorer(id, { newQuery: true }), { detail: "connects first" }), edit, duplicate, ...file, "sep", ...tools, "sep", pin];
  if (row.status === "error")
    return [item("Reconnect", () => void connectFromExplorer(id), { bold: true, hint: "Enter" }), edit, duplicate, ...file, item("Copy error", () => void copyText(row.error ?? "")), "sep", item("New query", () => {}, { unavailable: "Not connected" }), ...tools, "sep", pin, "sep", disconnect];
  return [item("New query", () => newQueryTab({ profileId: id }), { unavailable: row.status === "connecting" ? "Connecting" : undefined }), ...tools, edit, duplicate, ...file, "sep", pin, "sep", disconnect];
}

export function dbMenu(row: RowOf<"db">): MenuEntry[] {
  const { profileId: id, db } = row;
  const settings = useSettings.getState();
  const key = schemaGroupKey(id, db);
  const grouped = settings.explorerSchemaGroups.includes(key);
  const off = row.state === "offline" ? `Database is ${(row.dbState ?? "offline").toLowerCase()}` : undefined;
  const group = NAMESPACE_GROUP[useConnections.getState().servers[id]?.caps?.namespaces ?? "schemas"] ?? NAMESPACE_GROUP.schemas;
  return [
    item("Use in active tab", () => bindActiveTab(id, db), { bold: true, hint: "Double-click", unavailable: off }),
    item("New query", () => newQueryTab({ profileId: id, database: db }), { unavailable: off }),
    "sep",
    item("Refresh metadata", () => void useSnapshots.getState().refresh(id, db, false), { hint: "F5", unavailable: off }),
    item("Full metadata reload", () => void useSnapshots.getState().refresh(id, db, true), { unavailable: off }),
    "sep",
    item("View ER diagram", () => openErdForDatabase(id, db), { unavailable: off }),
    item("Schema compare…", () => openSchemaCompare({ profileId: id, database: db }), { unavailable: off ?? refused(id, "schemaCompare") }),
    item("Data compare…", () => openDataCompare({ profileId: id, database: db }), { unavailable: off ?? refused(id, "dataCompare") }),
    item("Import data…", () => importInto(id, db), { unavailable: off }),
    "sep",
    item("Copy name", () => void copyText(db), { hint: "Ctrl+C" }),
    "sep",
    item(group.label, () => settings.set({ explorerSchemaGroups: grouped ? settings.explorerSchemaGroups.filter((k) => k !== key) : [...settings.explorerSchemaGroups, key] }), { checked: grouped, unavailable: grouped ? undefined : group.unavailable }),
  ];
}

export function schemaMenu(row: RowOf<"schema">): MenuEntry[] {
  return [
    item("View ER diagram", () => openErdForDatabase(row.profileId, row.db, row.schema)),
    item("Schema compare…", () => openSchemaCompare({ profileId: row.profileId, database: row.db, schema: row.schema }), { unavailable: refused(row.profileId, "schemaCompare") }),
    "sep",
    item("Copy name", () => void copyText(row.schema), { hint: "Ctrl+C" }),
    refreshDb(row.profileId, row.db),
  ];
}

export function catMenu(row: RowOf<"cat">): MenuEntry[] {
  const st = useExplorer.getState();
  const label = CATEGORIES.find((c) => c.id === row.cat)!.label;
  const filtered = st.kind === row.cat;
  const out: MenuEntry[] = [item(`Show only ${label.toLowerCase()}`, () => st.set({ kind: filtered ? "all" : row.cat }), { checked: filtered })];
  if (row.filter) out.push(item("Clear filter", () => st.set({ folderFilters: { ...st.folderFilters, [row.key]: "" } }), { hint: "Esc", detail: `“${row.filter}”` }));
  out.push("sep", refreshDb(row.profileId, row.db));
  return out;
}

export function leafMenu(row: RowOf<"leaf">): MenuEntry[] {
  if (!row.column || !row.obj || !row.profileId || !row.db) return [item("Copy name", () => void copyText(row.text), { hint: "Ctrl+C" })];
  const { obj: o, profileId, db, column } = row;
  const engine = useProfiles.getState().byId(profileId)?.engine;
  const name = columnName(column, engine);
  const waiting = sessionReason(profileId);
  return [
    item("Insert name", () => insertText(name), { hint: "Ctrl+Enter" }),
    item("Copy name", () => void copyText(column), { hint: "Ctrl+C" }),
    "sep",
    item("Find usages", () => findUsagesOf(profileId, db, { schema: o.schema, name: o.name, kind: o.kind, column }), { hint: "Shift+F12", unavailable: waiting }),
    item("Rename…", () => renameOf(profileId, db, { schema: o.schema, name: o.name, kind: o.kind, column }), { hint: "F2", unavailable: waiting }),
  ];
}

/** The menu of any row, or null (notes, headers). */
export function menuFor(row: TreeRow, ctx: MenuContext = {}): MenuEntry[] | null {
  switch (row.t) {
    case "server": return serverMenu(row);
    case "db": return dbMenu(row);
    case "schema": return schemaMenu(row);
    case "cat": return catMenu(row);
    case "obj": return objectMenu(row, ctx);
    case "leaf": return leafMenu(row);
    default: return null;
  }
}

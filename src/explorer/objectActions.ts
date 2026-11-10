// What Explorer rows do: open, script, insert names, connect. Scripts open as text in a new tab and never run from
// here (the sidecar's guards apply when the user runs them, §13 rule 7); Select top 1000 is the one item that runs.
import { quoteIdentifier } from "../editor/completion/quoteIdentifier";
import type { Engine, SnapObject } from "../ipc/contract";
import { assertNever, dialectOf, DIALECTS, engineTraits } from "../engines";
import { rpc } from "../ipc/client";
import { useConnections } from "../store/connections";
import { useProfiles } from "../store/profiles";
import { useTabs } from "../store/tabs";
import { toast } from "../store/ui";
import { activateTab, changeConnection, changeDatabase, newQueryTab, openDefinition, scriptObject } from "../app/actions";
import { editorForTab } from "../editor/editors";
import { tabForNewConnection } from "../app/tabActions";
import { objectLabel } from "../app/tabTitle";

export type { Engine };

export function ref(o: SnapObject) {
  return { schema: o.schema, name: o.name, kind: o.kind, objectId: o.id };
}

export function qualified(o: Pick<SnapObject, "schema" | "name">, engine: Engine | undefined) {
  const dialect = dialectOf(engine);
  return `${quoteIdentifier(o.schema, dialect)}.${quoteIdentifier(o.name, dialect)}`;
}

/** A column name as SQL text: bracketed on SQL Server; elsewhere delimited only where needed (on PostgreSQL that
 *  includes where folding would change the name: `"Id"`, not `Id`, which PostgreSQL reads as `id`). */
export function columnName(name: string, engine: Engine | undefined) {
  const dialect = dialectOf(engine);
  switch (dialect) {
    case "tsql": return DIALECTS.tsql.quote(name);
    case "pg":
    case "sqlite":
    case "mySql":
    case "duckDb":
    case "oracle":
      return quoteIdentifier(name, dialect);
    default: return assertNever(dialect);
  }
}

/** A column list for an INSERT or SELECT (Alt-drag, Ctrl+Alt+Enter). */
export function columnList(o: SnapObject, engine: Engine | undefined) {
  return (o.columns ?? []).map((c) => columnName(c.name, engine)).join(", ");
}

export function generateDmlTemplate(o: SnapObject, engine: Engine | undefined, kind: "insert" | "update" | "delete"): string {
  const q = (name: string) => columnName(name, engine);
  const qTable = qualified(o, engine);
  const comment = (value: string) => value.replace(/\*\//g, "* /").replace(/[\r\n]/g, " ");
  const cols = o.columns ?? [];
  const pks = cols.filter((c) => c.pk);
  const keyCols = pks.length ? pks : cols.length ? cols.slice(0, 1) : [];
  const where = keyCols.length ? "WHERE " + keyCols.map((c) => `${q(c.name)} = /* ${comment(c.type)} */ <value>`).join(" AND ") : "WHERE /* specify condition */";

  if (kind === "insert") {
    const targetCols = cols.filter((c) => !c.identity && !c.computed);
    if (cols.length && !targetCols.length) return `INSERT INTO ${qTable} DEFAULT VALUES;\n`;
    if (!targetCols.length) return `INSERT INTO ${qTable} (/* columns */)\nVALUES (/* values */);\n`;
    const colList = targetCols.map((c) => q(c.name)).join(", ");
    const valList = targetCols.map((c) => `/* ${comment(c.name)} (${comment(c.type)}) */ <value>`).join(", ");
    return `INSERT INTO ${qTable} (${colList})\nVALUES (${valList});\n`;
  }
  if (kind === "update") {
    const targetCols = cols.filter((c) => !c.pk && !c.identity && !c.computed);
    if (cols.length && !targetCols.length) return "-- No writable non-key columns are available for UPDATE.\n";
    if (!targetCols.length) return `UPDATE ${qTable}\nSET\n    /* column = <value> */\n${where};\n`;
    const setList = targetCols.map((c) => `    ${q(c.name)} = /* ${comment(c.type)} */ <value>`).join(",\n");
    return `UPDATE ${qTable}\nSET\n${setList}\n${where};\n`;
  }
  return `DELETE FROM ${qTable}\n${where};\n`;
}

/** INSERT / UPDATE / DELETE templates open in a tab titled by the verb, like every Script as tab. */
export function scriptDml(profileId: string, db: string, o: SnapObject, kind: "insert" | "update" | "delete") {
  const engine = useProfiles.getState().byId(profileId)?.engine;
  newQueryTab({ profileId, database: db, text: generateDmlTemplate(o, engine, kind), title: `${kind.toUpperCase()} ${objectLabel(o.schema, o.name, engine)}.sql` });
}

/**
 * Modify a trigger: SQL Server opens the trigger itself (CREATE OR ALTER TRIGGER); PostgreSQL opens its trigger
 * function, found through the trigger's dependencies, since the body lives there.
 */
export async function modifyTrigger(profileId: string, db: string, o: SnapObject) {
  if (!engineTraits(useProfiles.getState().byId(profileId)?.engine).triggerFunctions) return openDefinition(profileId, db, ref(o), true);
  try {
    const sessionId = await useConnections.getState().sessionFor(profileId);
    const deps = await rpc("meta.dependencies", { sessionId, object: { ...ref(o), profileId, database: db } });
    const fn = deps.find((d) => d.direction === "uses" && d.object.kind === "func");
    if (!fn) {
      toast("error", `The function of trigger ${o.name} was not found (it may not be visible to this login).`);
      return;
    }
    await openDefinition(profileId, db, { schema: fn.object.schema, name: fn.object.name, kind: "func", objectId: fn.object.objectId }, true);
  } catch (e) {
    toast("error", (e as Error).message);
  }
}

/** The double-click / Enter action of an object: Select top 1000 for tables and views, else Modify. */
export function openObject(profileId: string, db: string, o: SnapObject) {
  if (o.kind === "table" || o.kind === "view") void scriptObject(profileId, db, ref(o), "selectTop", true);
  else if (o.kind === "trigger") void modifyTrigger(profileId, db, o);
  else void openDefinition(profileId, db, ref(o), true);
}

/** Inserts text at the active editor's selection (Ctrl+Enter, Ctrl+Alt+Enter); false without an editor. */
export function insertText(text: string): boolean {
  const tab = useTabs.getState().activeId;
  const ed = tab ? editorForTab(tab) : undefined;
  if (!ed) return false;
  const sel = ed.getSelection();
  if (sel) ed.executeEdits("explorer", [{ range: sel, text, forceMoveMarkers: true }]);
  ed.focus();
  return true;
}

export function insertName(o: SnapObject, profileId: string, columns = false) {
  const engine = useProfiles.getState().byId(profileId)?.engine;
  return insertText(columns ? columnList(o, engine) : qualified(o, engine));
}

/** Double-click on a database: the active tab uses it (same server), takes the connection (no server), else a new tab. */
export function bindActiveTab(profileId: string, db: string) {
  const s = useTabs.getState();
  const t = s.activeId ? s.tabs[s.activeId] : undefined;
  if (t && t.profileId === profileId) void changeDatabase(t.id, db);
  else if (t && !t.profileId) void changeConnection(t.id, profileId, db);
  else newQueryTab({ profileId, database: db });
}

/**
 * A connect the user started in the Explorer (row, menu, Enter): it opens the server one level (§12), and an
 * unconnected tab takes the connection instead of a new tab piling up, as in the Connect dialog. With `newQuery`, a
 * query tab on the server is the goal: the reused tab is it, and only without one is a tab opened.
 */
export async function connectFromExplorer(profileId: string, opts?: { newQuery?: boolean }) {
  const ok = await useConnections.getState().connect(profileId);
  if (!ok) return false;
  const reuse = tabForNewConnection();
  if (reuse) {
    await changeConnection(reuse, profileId);
    activateTab(reuse);
  } else if (opts?.newQuery) newQueryTab({ profileId });
  return true;
}

export async function copyText(text: string) {
  await navigator.clipboard.writeText(text);
  toast("success", "Copied.");
}

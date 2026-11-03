// Opening ER diagrams: from a table, a database/schema, or the tables of the statement at the cursor.
import { activeTabState } from "../app/actions";
import { openTool } from "../app/toolActions";
import { profileDialect } from "../store/profiles";
import type { Dialect } from "../ipc/contract";
import { ownSessionFor, tabScope, tabSnapshot } from "../store/snapshots";
import { toast } from "../store/ui";
import { getModel, editorForTab } from "../editor/editors";
import { statementTables } from "./erdScope";
import type { ErdParams } from "./ErdTab";

const asParams = (p: ErdParams) => p as unknown as Record<string, unknown>;

/**
 * Where a diagram opens: the profile's database and, for a session-owned engine (SQLite, DuckDB), the query tab session
 * whose own view it draws (Phase D0d: an in-memory database exists only there). Server engines: no session.
 */
function binding(profileId: string, database: string, sessionId: string | undefined) {
  return sessionId ? { profileId, database, sessionId } : { profileId, database };
}

const schemaDiagram = (profileId: string, database: string, schema: string | null | undefined, sessionId: string | undefined) =>
  openTool("erd", `ERD · ${schema ?? database}`, { ...binding(profileId, database, sessionId), params: asParams({ mode: "schema", schema: schema ?? null }) });

/** From the explorer: a session-owned engine's diagram reads the view of a query tab on that database (ownSessionFor). */
export function openErdForTable(profileId: string, database: string, objectId: number, name: string) {
  openTool("erd", `ERD · ${name}`, { ...binding(profileId, database, ownSessionFor(profileId, database)), params: asParams({ mode: "neighbourhood", rootId: objectId }) });
}

export function openErdForDatabase(profileId: string, database: string, schema?: string | null) {
  schemaDiagram(profileId, database, schema, ownSessionFor(profileId, database));
}

/**
 * The tables of the statement at the cursor, resolved against the cached schema the tab reads (erd.fromQuery,
 * tabScope): a server engine's shared one, a session-owned engine's session view.
 */
export function queryTableIds(text: string, offset: number, dialect: Dialect, profileId: string, database: string, sessionId?: string): number[] {
  const idx = tabSnapshot({ profileId, database, sessionId })?.index;
  return idx ? statementTables(idx, text, offset, dialect) : [];
}

/** Ctrl+Shift+E: the statement at the cursor when it references tables, else the tab's whole database. */
export function openErdFromActiveTab() {
  const t = activeTabState();
  if (!t?.profileId || !t.database) {
    toast("info", "Pick a connection and database for this tab first.");
    return;
  }
  const model = getModel(t.id);
  const pos = editorForTab(t.id)?.getPosition();
  const dialect = profileDialect(t.profileId);
  // The tab's own view on a session-owned engine (its in-memory database, its temporary tables), as the editor reads it.
  const sessionId = tabScope(t)?.sessionId;
  const ids = model && pos ? queryTableIds(model.getValue(), model.getOffsetAt(pos), dialect, t.profileId, t.database, sessionId) : [];
  // Only this tab's own session: another tab's in-memory database is another database.
  if (ids.length) openTool("erd", "ERD · query", { ...binding(t.profileId, t.database, sessionId), params: asParams({ mode: "query", queryTables: ids }) });
  else schemaDiagram(t.profileId, t.database, null, sessionId);
}

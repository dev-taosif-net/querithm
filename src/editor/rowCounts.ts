// Table row counts for completion items (§9): catalog statistics through meta.rowCounts (each engine's own estimate, no
// table scan), loaded once per schema snapshot version in the background — never on the keystroke path. SQLite has none
// before ANALYZE: its items say "rows n/a".
import { rpc } from "../ipc/client";
import { useConnections } from "../store/connections";
import type { RowInfo, SchemaIndex } from "./completion/types";

export type { RowInfo };

/** Schemas with more tables than this are not counted: one catalog query, but the answer would be large. */
const MAX_TABLES = 5000;

/** After a failed load, the next keystroke past this delay tries again (not every keystroke: a failure may persist). */
const RETRY_MS = 30_000;

interface Entry { key: string; counts?: Map<number, RowInfo>; loading: boolean; retryAt?: number }
const entries = new Map<string, Entry>();
const listeners = new Set<() => void>();

/** Called when counts arrive (the editor re-opens an open list so its items show them). */
export function onRowCounts(fn: () => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/**
 * The counts for this schema version, or undefined while they load (the first call starts the load). `sessionId`: a
 * session-owned tab's own session (an in-memory database); otherwise the profile's explorer session — only one that is
 * already open: a keystroke never connects a server (a snapshot cached in store.db can outlive the connection), so
 * without one the counts wait for the next call after it connects.
 */
export function rowCountsFor(idx: SchemaIndex | null, version: number | undefined, profileId?: string, database?: string, sessionId?: string, now = Date.now()): Map<number, RowInfo> | undefined {
  if (!idx || !profileId || !database) return undefined;
  const scope = `${profileId}\u001f${database}\u001f${sessionId ?? ""}`;
  const key = `${scope}\u001f${version ?? 0}`;
  const e = entries.get(scope);
  if (e && e.key === key && (e.loading || e.retryAt === undefined || now < e.retryAt)) return e.counts;
  const tables = idx.objects.filter((o) => o.kind === "table").map((o) => o.id);
  if (!tables.length || tables.length > MAX_TABLES) return undefined;
  const sid = sessionId ?? useConnections.getState().servers[profileId]?.sessionId;
  if (!sid) return e?.counts;
  const entry: Entry = { key, loading: true, counts: e?.counts }; // the previous version's counts meanwhile
  entries.set(scope, entry);
  void (async () => {
    try {
      const list = await rpc("meta.rowCounts", { sessionId: sid, database, objectIds: tables });
      const m = new Map<number, RowInfo>();
      for (const id of tables) m.set(id, { rows: null, estimated: true });
      for (const c of list) m.set(c.objectId, { rows: c.rows ?? null, estimated: c.estimated });
      if (entries.get(scope) !== entry) return;
      entry.counts = m;
      for (const fn of listeners) fn();
    } catch {
      // Best effort: items keep what they had (none, or the previous version's) and a later call tries again.
      if (entries.get(scope) === entry) entry.retryAt = Date.now() + RETRY_MS;
    } finally {
      entry.loading = false;
    }
  })();
  return entry.counts;
}

/** Tests only. */
export function resetRowCounts() {
  entries.clear();
}

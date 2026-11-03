import { useEffect } from "react";
import { create } from "zustand";
import type { MetaUpdatedEvent, SchemaSnapshot, SnapshotStatus } from "../ipc/contract";
import { onNotify, rpc } from "../ipc/client";
import { buildSchemaIndex } from "../editor/completion/schemaIndex";
import type { SchemaIndex } from "../editor/completion/types";
import { isLocalEngine } from "../engines";
import { locationOf } from "../connections/auth";
import { profileDialect as dialectOf, useProfiles } from "./profiles";
import { isDisconnected, useConnections } from "./connections";
import { useTabs, type TabState } from "./tabs";

/**
 * Cached schema per (profile, database), used by tier-1 completion and the explorer (§9, §8 Lazy explorer). A tab on a
 * session-owned engine (SQLite, Phase D0d) has an entry of its own per session: its in-memory database, or the shared
 * file snapshot plus its temporary objects.
 */
export interface SnapEntry {
  key: string;
  profileId: string;
  database: string;
  /** The session whose own view this is (a session-owned engine's tab); absent for the shared snapshot. */
  sessionId?: string;
  status: SnapshotStatus | "loading";
  snapshot?: SchemaSnapshot;
  index?: SchemaIndex;
  version: number;
  error?: string | null;
}

/**
 * A database's cache key, by its exact name (Phase D0e identity): PostgreSQL's "Sales" and "sales" are two databases,
 * and the sidecar keeps names exact, so the webview never folds case and never merges two of them into one entry. A
 * session's own view (Phase D0d) adds its session id after a separator no database name holds.
 */
export const snapKey = (profileId: string, database: string, sessionId?: string) =>
  sessionId ? `${profileId}|${database}\u001f${sessionId}` : `${profileId}|${database}`;

/** Which snapshot to read: the shared one of a profile's database, or one session's own view of it. */
export interface SnapScope {
  profileId: string;
  database: string;
  sessionId?: string;
}

/**
 * The snapshot a tab reads. A server engine: the shared one of its profile and database. A session-owned engine
 * (SQLite): its session's own view once the session is open; before that, a file database's shared snapshot (the same
 * file, without temporary objects) and, for an in-memory database, none — it does not exist until the tab opens it.
 */
export function tabScope(t: Pick<TabState, "profileId" | "database" | "sessionId"> | undefined): SnapScope | undefined {
  if (!t?.profileId || !t.database) return undefined;
  const profile = useProfiles.getState().byId(t.profileId);
  if (!isLocalEngine(profile?.engine)) return { profileId: t.profileId, database: t.database };
  if (t.sessionId) return { profileId: t.profileId, database: t.database, sessionId: t.sessionId };
  if (profile && locationOf(profile) === "memory") return undefined;
  return { profileId: t.profileId, database: t.database };
}

/** The key of a tab's snapshot (tabScope), or undefined when the tab reads none. */
export function tabSnapKey(t: Pick<TabState, "profileId" | "database" | "sessionId"> | undefined): string | undefined {
  const s = tabScope(t);
  return s && snapKey(s.profileId, s.database, s.sessionId);
}

/** The tab's cached schema (tabScope). */
export function tabSnapshot(t: Pick<TabState, "profileId" | "database" | "sessionId"> | undefined): SnapEntry | undefined {
  const k = tabSnapKey(t);
  return k ? useSnapshots.getState().entries[k] : undefined;
}

/** Loads a tab's schema once (tabScope); nothing for a tab without a database or an in-memory one without its session. */
export function ensureTabSnapshot(t: Pick<TabState, "profileId" | "database" | "sessionId"> | undefined) {
  const s = tabScope(t);
  if (s) useSnapshots.getState().ensure(s.profileId, s.database, s.sessionId);
}

/**
 * What a tool tab or dialog (ER diagram, schema / data compare, import) was opened on: a profile's database and, for a
 * session-owned engine, the query tab session it was opened from (ownSessionFor).
 */
export interface ToolBinding {
  profileId?: string;
  database?: string;
  sessionId?: string;
}

/** A session-owned engine's binding whose session a query tab still holds on the same database. */
const holds = (tabs: Record<string, TabState>, b: ToolBinding | undefined) =>
  !!b?.sessionId &&
  isLocalEngine(useProfiles.getState().byId(b.profileId)?.engine) &&
  Object.values(tabs).some((t) => t.sessionId === b.sessionId && t.profileId === b.profileId && t.database === b.database);

/**
 * The session a tool opened on a profile's database reads through (Phase D0d): for a session-owned engine (SQLite,
 * DuckDB), the session of a query tab on that database — the active tab first, whose view the explorer shows, else
 * another one —; undefined for a server engine (its one shared snapshot) and when no tab holds a session there.
 */
export function ownSessionFor(profileId: string | undefined, database: string | undefined): string | undefined {
  if (!profileId || !database || !isLocalEngine(useProfiles.getState().byId(profileId)?.engine)) return undefined;
  const { tabs, activeId } = useTabs.getState();
  const on = (t: TabState | undefined): t is TabState => !!t?.sessionId && t.profileId === profileId && t.database === database;
  const active = activeId ? tabs[activeId] : undefined;
  return (on(active) ? active : Object.values(tabs).find(on))?.sessionId;
}

/**
 * The binding's session while a query tab still holds it on the same database (a closed tab's session is gone); never
 * for a server engine, whose tools go through the explorer's session.
 */
export function heldSession(b: ToolBinding | undefined): string | undefined {
  return holds(useTabs.getState().tabs, b) ? b!.sessionId : undefined;
}

/**
 * The session a tool's requests go through: its query tab session while a tab holds it (that session's own view; an
 * in-memory database exists only there), else the explorer's session on the profile (connecting first).
 */
export async function toolSession(b: ToolBinding): Promise<string> {
  return heldSession(b) ?? (await useConnections.getState().sessionFor(b.profileId!));
}

/**
 * The snapshot a tool reads: tabScope over its binding, with the session only while a tab holds it. A server engine:
 * the shared snapshot. A session-owned engine: that session's own view; once the session is gone, a file database's
 * shared snapshot and, for an in-memory database (it went with its session), none.
 */
export function toolScope(b: ToolBinding | undefined): SnapScope | undefined {
  return tabScope(b && { profileId: b.profileId, database: b.database, sessionId: heldSession(b) });
}

/**
 * A tool's snapshot entry (toolScope), re-read as tabs open and close sessions, and loaded once unless `load` is false
 * (the import wizard only reads what is cached). `scope` is undefined when there is nothing to read: no database
 * picked, or an in-memory database whose session is gone.
 */
export function useToolSnapshot(b: ToolBinding | undefined, load = true): { scope: SnapScope | undefined; entry: SnapEntry | undefined } {
  // Re-rendered as tabs open and close the binding's session, and as profiles change (engine, location).
  useTabs((s) => holds(s.tabs, b));
  useProfiles((s) => s.profiles);
  const scope = toolScope(b);
  const key = scope && snapKey(scope.profileId, scope.database, scope.sessionId);
  const entry = useSnapshots((s) => (key ? s.entries[key] : undefined));
  useEffect(() => {
    if (scope && load) useSnapshots.getState().ensure(scope.profileId, scope.database, scope.sessionId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, load]);
  return { scope, entry };
}

/** `sessionId` in a scoped request only when there is one (server engines send what they always sent). */
export const sessionParam = (sessionId: string | undefined) => (sessionId ? { sessionId } : {});

function databasesOf(profileId: string): string[] {
  return useConnections.getState().servers[profileId]?.databases.map((d) => d.name) ?? [];
}

function isMemoryProfile(profileId: string): boolean {
  const p = useProfiles.getState().byId(profileId);
  return !!p && locationOf(p) === "memory";
}

interface SnapStore {
  entries: Record<string, SnapEntry>;
  /** Loads a database's schema once; a server the user disconnected is skipped (nothing of it is shown). */
  ensure(profileId: string, database: string, sessionId?: string): void;
  load(profileId: string, database: string, sessionId?: string): Promise<void>;
  refresh(profileId: string, database: string, full?: boolean, sessionId?: string): Promise<void>;
  get(profileId?: string, database?: string, sessionId?: string): SnapEntry | undefined;
  /** Drop every cached database of a profile (on disconnect), session views included. */
  forget(profileId: string): void;
}

export const useSnapshots = create<SnapStore>((set, get) => {
  const put = (e: SnapEntry) => set((s) => ({ entries: { ...s.entries, [e.key]: e } }));
  const index = (snap: SchemaSnapshot) => buildSchemaIndex(snap, dialectOf(snap.profileId), databasesOf(snap.profileId));

  return {
    entries: {},
    ensure(profileId, database, sessionId) {
      const k = snapKey(profileId, database, sessionId);
      if (!get().entries[k] && !isDisconnected(profileId)) void get().load(profileId, database, sessionId);
    },
    async load(profileId, database, sessionId) {
      if (isDisconnected(profileId)) return;
      const k = snapKey(profileId, database, sessionId);
      const scope = { profileId, database, ...sessionParam(sessionId) };
      const prev = get().entries[k];
      put({ key: k, ...scope, status: prev?.snapshot ? prev.status : "loading", snapshot: prev?.snapshot, index: prev?.index, version: (prev?.version ?? 0) + 1 });
      try {
        const snapshot = await rpc("meta.snapshot", scope);
        if (!get().entries[k] || isDisconnected(profileId)) return; // forgotten (disconnected, session closed) while loading
        put({ key: k, ...scope, status: snapshot.status, snapshot, index: index(snapshot), version: (get().entries[k]?.version ?? 0) + 1, error: snapshot.error });
      } catch (e) {
        if (!get().entries[k] || isDisconnected(profileId)) return;
        put({ key: k, ...scope, status: "error", version: (get().entries[k]?.version ?? 0) + 1, error: e instanceof Error ? e.message : String(e) });
      }
    },
    async refresh(profileId, database, full = false, sessionId) {
      await rpc("meta.refresh", { profileId, database, full, ...sessionParam(sessionId) });
    },
    get(profileId, database, sessionId) {
      if (!profileId || !database) return undefined;
      return get().entries[snapKey(profileId, database, sessionId)];
    },
    forget(profileId) {
      set((s) => ({ entries: Object.fromEntries(Object.entries(s.entries).filter(([, e]) => e.profileId !== profileId)) }));
    },
  };
});

/** meta.updated patches one entry in place: only changed objects are re-fetched (§8 step 4, §12). */
async function applyUpdate(entry: SnapEntry, e: MetaUpdatedEvent) {
  const store = useSnapshots.getState();
  const { key: k, profileId, database, sessionId } = entry;
  if (e.status === "refreshing" || e.status === "error") {
    useSnapshots.setState((s) => (s.entries[k] ? { entries: { ...s.entries, [k]: { ...s.entries[k], status: e.status, error: e.error, version: s.entries[k].version + 1 } } } : s));
    return;
  }
  if (e.full || !entry.snapshot || !e.changedIds || e.changedIds.length > 300) {
    await store.load(profileId, database, sessionId);
    return;
  }
  if (e.changed === 0) {
    useSnapshots.setState((s) => (s.entries[k] ? { entries: { ...s.entries, [k]: { ...s.entries[k], status: "fresh", version: s.entries[k].version + 1 } } } : s));
    return;
  }
  const changed = e.changedIds.length ? await rpc("meta.objects", { profileId, database, ids: e.changedIds, ...sessionParam(sessionId) }) : [];
  const now = useSnapshots.getState().entries[k];
  if (!now || isDisconnected(profileId)) return; // disconnected (or the session closed) meanwhile
  const base = now.snapshot ?? entry.snapshot;
  const drop = new Set<number>([...(e.removedIds ?? []), ...e.changedIds]);
  const objects = base.objects.filter((o) => !drop.has(o.id)).concat(changed);
  objects.sort((a, b) => (a.schema + "." + a.name).localeCompare(b.schema + "." + b.name));
  const snapshot: SchemaSnapshot = { ...base, objects, status: "fresh" };
  const idx = buildSchemaIndex(snapshot, dialectOf(profileId), databasesOf(profileId));
  useSnapshots.setState((s) => (s.entries[k] ? { entries: { ...s.entries, [k]: { ...s.entries[k], snapshot, index: idx, status: "fresh", version: s.entries[k].version + 1 } } } : s));
}

/**
 * Routes a meta.updated to the entries it concerns. A session's own view changed (an in-memory database, temporary
 * tables): only that session's entry, never the shared snapshot. A shared change: the shared entry, and every session
 * view of the same file database (those views hold the shared objects too); an in-memory database has no shared part.
 */
async function onMetaUpdated(e: MetaUpdatedEvent) {
  if (isDisconnected(e.profileId)) return;
  const entries = useSnapshots.getState().entries;
  if (e.sessionId) {
    const own = entries[snapKey(e.profileId, e.database, e.sessionId)];
    if (own) await applyUpdate(own, e);
    return;
  }
  const memory = isMemoryProfile(e.profileId);
  const targets = Object.values(entries).filter((x) => x.profileId === e.profileId && x.database === e.database && (!x.sessionId || !memory));
  await Promise.all(targets.map((x) => applyUpdate(x, e)));
}

/**
 * Keeps session views in step with the tabs (Phase D0d): a tab whose session opened gets its own view loaded, and a
 * view whose session no tab holds any more (closed, switched, lost in a restart) is dropped.
 */
function syncSessionViews(tabs: Record<string, TabState>, prev: Record<string, TabState>) {
  const live = new Set<string>();
  const opened: TabState[] = [];
  for (const t of Object.values(tabs)) {
    const sc = t.sessionId ? tabScope(t) : undefined;
    if (!sc?.sessionId) continue;
    live.add(snapKey(sc.profileId, sc.database, sc.sessionId));
    const p = prev[t.id];
    if (!p || p.sessionId !== t.sessionId || p.database !== t.database || p.profileId !== t.profileId) opened.push(t);
  }
  const stale = Object.values(useSnapshots.getState().entries).filter((x) => x.sessionId && !live.has(x.key));
  if (stale.length) {
    const drop = new Set(stale.map((x) => x.key));
    useSnapshots.setState((s) => ({ entries: Object.fromEntries(Object.entries(s.entries).filter(([k]) => !drop.has(k))) }));
  }
  for (const t of opened) ensureTabSnapshot(t);
}

export function installSnapshotListeners() {
  const offMeta = onNotify("meta.updated", (e) => void onMetaUpdated(e));
  // A disconnected server's schema is no longer shown or searched (explorer, completion).
  const offConn = useConnections.subscribe((s, prev) => {
    if (s.order !== prev.order) for (const id of prev.order) if (!s.order.includes(id)) useSnapshots.getState().forget(id);
    // Reconnected: the tabs still bound to the server get their schema back (it was dropped on disconnect).
    for (const id of s.order) {
      if (s.servers[id]?.status !== "connected" || prev.servers[id]?.status === "connected") continue;
      for (const t of Object.values(useTabs.getState().tabs)) if (t.profileId === id && t.database) ensureTabSnapshot(t);
    }
  });
  const offTabs = useTabs.subscribe((s, prev) => {
    if (s.tabs !== prev.tabs) syncSessionViews(s.tabs, prev.tabs);
  });
  return () => {
    offMeta();
    offConn();
    offTabs();
  };
}

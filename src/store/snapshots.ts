import { useEffect } from "react";
import { create } from "zustand";
import type { MetaUpdatedEvent, SchemaSnapshot, SnapFk, SnapObject, SnapshotStatus } from "../ipc/contract";
import { onNotify, rpc } from "../ipc/client";
import { withTimeLimit } from "../ipc/timeLimit";
import { buildSchemaIndex } from "../editor/completion/schemaIndex";
import type { SchemaIndex } from "../editor/completion/types";
import { DIALECTS, foldIdentifier, isLocalEngine } from "../engines";
import { locationOf } from "../connections/auth";
import { profileDialect as dialectOf, useProfiles } from "./profiles";
import { isDisconnected, useConnections } from "./connections";
import { useLinkedServers } from "./linkedServers";
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
  /**
   * When the last load failed, or answered "error" with no objects (Date.now()); ensure() retries an entry with nothing
   * cached after retryDelay(retries).
   */
  failedAt?: number;
  /** Failed loads in a row with nothing cached (completion audit 2026-11-25, M21: the wait between retries doubles). */
  retries?: number;
}

/** Least time between two retries of a schema load that failed with nothing cached. */
export const RETRY_MS = 5000;
/** The longest wait between two retries of a schema that still cannot be read. */
export const MAX_RETRY_MS = 60_000;

/** The wait before retry number `retries` + 1: RETRY_MS, doubling up to MAX_RETRY_MS. */
export const retryDelay = (retries = 0) => Math.min(MAX_RETRY_MS, RETRY_MS * 2 ** Math.min(retries, 8));

/** An entry with no object to show: completion and the explorer have nothing of this database yet. */
const holdsNothing = (e: SnapEntry) => !e.snapshot?.objects?.length;

/**
 * Retries of a load that failed with nothing cached, before ensure() stops asking (about five minutes of waits): a
 * schema that still cannot be read then waits for the user's refresh or a reconnect, so a password changed meanwhile
 * does not turn into a failed login every minute. A load the engine refused for a missing password (needsSecret) is
 * not retried at all.
 */
export const MAX_RETRIES = 8;

/** ensure() will retry this entry's load (after retryDelay(retries)): it failed, holds nothing, retries are left. */
export const willRetry = (e: SnapEntry) => holdsNothing(e) && e.status === "error" && e.failedAt !== undefined && (e.retries ?? 0) < MAX_RETRIES;

/**
 * M22: how often the schema of a database an open tab reads is checked against the server while the app runs (an
 * incremental meta.refresh: one catalog query, objects re-read only where they changed). DDL from outside the app was
 * never seen before a restart or a manual refresh.
 */
export const FRESHNESS_MS = 10 * 60_000;
/** The least time between two checks of one database when the window gets the focus back. */
export const FOCUS_CHECK_MS = 2 * 60_000;

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

/**
 * The schema a profile's sign-in owns, where unqualified names resolve to it (DialectTraits.loginSchema: Oracle): the
 * user name as the server spells it, a "quoted" one exact. ALTER SESSION SET CURRENT_SCHEMA in a script moves it only for
 * the sidecar's tier 2 (OracleLanguageService.CurrentSchemaAt).
 */
export function loginSchemaOf(profileId: string): string | null {
  const p = useProfiles.getState().byId(profileId);
  if (!p?.username || !DIALECTS[dialectOf(profileId)].loginSchema) return null;
  const user = p.username.trim();
  const quoted = user.length > 1 && user.startsWith('"') && user.endsWith('"');
  return foldIdentifier(quoted ? user.slice(1, -1) : user, quoted, dialectOf(profileId)) || null;
}

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
  /**
   * The engine restarted (or stopped): no refresh or load it was running will report back. An entry left "refreshing" or
   * "loading" goes back to "stale" (its cached schema kept), so a refresh can start again — before, it stayed
   * "refreshing" for good and every refresh asked for it was refused (M21).
   */
  engineRestarted(): void;
}

/** When each entry was last read or checked (Date.now()), for the freshness check (M22). */
const checkedAt = new Map<string, number>();

/** Tests only: how many entries the freshness check remembers. */
export const freshnessChecksHeld = () => checkedAt.size;

/** Bumped by engineRestarted(): a load or refresh the restart overtook does not write its answer over the newer state. */
let restarts = 0;

/**
 * Per entry, bumped by each load and by each patch a meta.updated applies: an answer that comes back after a newer load
 * started (or a patch landed) is older than what the entry holds, and is dropped (code audit 2026-11-26, H10).
 */
const loadSeq = new Map<string, number>();
/** Sequence numbers come from one counter, never reused: an entry's can be dropped (forgetSeqs) with a load in flight. */
let seqCounter = 0;
const nextSeq = (k: string) => {
  const seq = ++seqCounter;
  loadSeq.set(k, seq);
  return seq;
};

/**
 * Per entry, the refresh() whose answer it waits for: a refresh forgotten in flight (a disconnect, then a reconnect)
 * that fails later does not fail the one started after it (M19).
 */
const refreshSeq = new Map<string, number>();

/**
 * An entry is gone: what the freshness check, the load and refresh sequences and the patch queue held of it goes too
 * (R3: session views piled up; M19: a patch still hung held the next one up after a reconnect).
 */
const forgetKey = (k: string) => {
  checkedAt.delete(k);
  loadSeq.delete(k);
  refreshSeq.delete(k);
  updating.delete(k);
};

/** Tests only: how many entries the load sequence remembers. */
export const loadSeqsHeld = () => loadSeq.size;

/** An entry without the bookkeeping of a failure (a read that succeeded). */
const succeeded = (e: SnapEntry): SnapEntry => {
  const { failedAt: _f, retries: _r, ...rest } = e;
  return { ...rest, error: null };
};

export const useSnapshots = create<SnapStore>((set, get) => {
  const put = (e: SnapEntry) => set((s) => ({ entries: { ...s.entries, [e.key]: e } }));
  const index = (snap: SchemaSnapshot) => buildSchemaIndex(snap, dialectOf(snap.profileId), databasesOf(snap.profileId), loginSchemaOf(snap.profileId));

  return {
    entries: {},
    ensure(profileId, database, sessionId) {
      const k = snapKey(profileId, database, sessionId);
      if (isDisconnected(profileId)) return;
      const e = get().entries[k];
      // A load that failed with nothing cached is retried, at most once per RETRY_MS (completion calls this per request).
      const retry = !!e && willRetry(e) && Date.now() - e.failedAt! >= retryDelay(e.retries);
      if (!e || retry) void get().load(profileId, database, sessionId);
    },
    async load(profileId, database, sessionId) {
      if (isDisconnected(profileId)) return;
      const k = snapKey(profileId, database, sessionId);
      const scope = { profileId, database, ...sessionParam(sessionId) };
      const prev = get().entries[k];
      const generation = restarts;
      const seq = nextSeq(k);
      // An entry with nothing to show is "loading" while the load runs; an empty "error" snapshot being retried kept
      // "error" without its retry count, and a restart meanwhile left it never retried (code audit 2026-11-26, H9).
      put({ key: k, ...scope, status: prev && !holdsNothing(prev) ? prev.status : "loading", snapshot: prev?.snapshot, index: prev?.index, version: (prev?.version ?? 0) + 1 });
      // Overtaken: a newer load started or a patch landed (either answer is newer), or the engine restarted meanwhile.
      const overtaken = () => loadSeq.get(k) !== seq || restarts !== generation;
      try {
        const snapshot = await rpc("meta.snapshot", scope);
        const now = get().entries[k];
        if (!now || isDisconnected(profileId) || overtaken()) return; // forgotten (disconnected, session closed), or overtaken
        checkedAt.set(k, Date.now());
        // The engine's last refresh failed and nothing was ever read: retried like a failed load (M21), not left empty.
        const failed = snapshot.status === "error" && !snapshot.objects?.length;
        put({
          key: k, ...scope, status: snapshot.status, snapshot, index: index(snapshot), version: (now.version ?? 0) + 1, error: snapshot.error,
          ...(failed ? { failedAt: Date.now(), retries: (prev?.failedAt !== undefined ? (prev.retries ?? 0) + 1 : 0) } : {}),
        });
      } catch (e) {
        const now = get().entries[k];
        // The engine restarted meanwhile: this load asked the old one, and engineRestarted() already set the entry.
        if (!now || isDisconnected(profileId) || overtaken()) return;
        // A failed reload keeps the schema already cached: completion and the explorer go on with it (§9) instead of
        // losing every table until the next successful load. A missing password is not retried (MAX_RETRIES).
        const noRetry = (e as { appCode?: string } | null)?.appCode === "needsSecret";
        put({
          key: k, ...scope, status: "error", snapshot: now.snapshot, index: now.index, version: now.version + 1,
          error: e instanceof Error ? e.message : String(e), failedAt: Date.now(),
          retries: noRetry ? MAX_RETRIES : prev?.failedAt !== undefined ? (prev.retries ?? 0) + 1 : 0,
        });
      }
    },
    async refresh(profileId, database, full = false, sessionId) {
      if (isDisconnected(profileId)) return;
      const k = snapKey(profileId, database, sessionId);
      const prev = get().entries[k];
      if (prev?.status === "refreshing" || prev?.status === "loading") return;
      // The last failure's time goes (M18: the status row said "the load failed" while this refresh ran); its retry
      // count stays, so a refresh that fails too goes on counting.
      const { failedAt: _f, ...rest } = prev ?? ({} as Partial<SnapEntry>);
      const pending: SnapEntry = { ...rest, key: k, profileId, database, sessionId,
        status: "refreshing", error: null, version: (prev?.version ?? 0) + 1 };
      put(pending);
      checkedAt.set(k, Date.now());
      const generation = restarts;
      const seq = ++seqCounter;
      refreshSeq.set(k, seq);
      try {
        await rpc("meta.refresh", { profileId, database, full, ...sessionParam(sessionId) });
      } catch (e) {
        // Handle fire-and-forget refresh actions too, keeping their cached schema and Retry available. A refresh the old
        // engine fails after a restart says nothing of the one the new engine may be running now (H10).
        const now = get().entries[k];
        if (!now || now.status !== "refreshing" || isDisconnected(profileId) || restarts !== generation || refreshSeq.get(k) !== seq) return;
        // A missing password is not retried, as for a load (MAX_RETRIES).
        const noRetry = (e as { appCode?: string } | null)?.appCode === "needsSecret";
        put({ ...now, status: "error", error: e instanceof Error ? e.message : String(e), failedAt: Date.now(), version: now.version + 1, ...(noRetry ? { retries: MAX_RETRIES } : {}) });
      }
    },
    get(profileId, database, sessionId) {
      if (!profileId || !database) return undefined;
      return get().entries[snapKey(profileId, database, sessionId)];
    },
    forget(profileId) {
      for (const e of Object.values(get().entries)) if (e.profileId === profileId) forgetKey(e.key);
      set((s) => ({ entries: Object.fromEntries(Object.entries(s.entries).filter(([, e]) => e.profileId !== profileId)) }));
    },
    engineRestarted() {
      restarts++;
      // No patch of the old engine's is waited for any more: their answers are dropped (R3-3), and the queue would
      // hold the new engine's patches up to UPDATE_WAIT_MS (M19).
      updating.clear();
      // An entry that holds nothing yet (its first load) is dropped rather than left "stale" and empty: nothing ever
      // loaded it again (M4). ensure() loads a missing entry, and the explorer asks for one it does not have. So is one
      // the old engine had not read yet ("empty", or "stale" with nothing): no refresh of the old engine will fill it (L9).
      const running = (e: SnapEntry) => e.status === "refreshing" || e.status === "loading";
      const dropped = (e: SnapEntry) => (running(e) || e.status === "empty" || e.status === "stale") && holdsNothing(e);
      for (const e of Object.values(get().entries)) if (dropped(e)) forgetKey(e.key);
      set((s) => ({
        entries: Object.fromEntries(Object.entries(s.entries).flatMap(([k, e]) =>
          dropped(e) ? [] : [[k, running(e) ? { ...e, status: "stale" as const, version: e.version + 1 } : e]])),
      }));
    },
  };
});

/**
 * M22: an incremental refresh of each database an open tab reads whose schema was last read or checked `minAge` ago or
 * longer, on a connected server only — a check never connects one. Runs every FRESHNESS_MS and when the window gets the
 * focus back (at most every FOCUS_CHECK_MS per database).
 */
export function checkFreshness(minAge: number) {
  // Nobody is looking: a catalog query on every connected server every 10 minutes, all night, helps no one.
  if (typeof document !== "undefined" && document.hidden) return;
  const now = Date.now();
  const store = useSnapshots.getState();
  const tabs = Object.values(useTabs.getState().tabs);
  // A database a tab holds an open transaction on: its catalog read can wait on that transaction's DDL (SQL Server),
  // and the sidecar defers the read until the transaction ends anyway (H11).
  const busy = new Set<string>();
  for (const t of tabs) {
    const sc = t.txState && t.txState !== "none" ? tabScope(t) : undefined;
    if (sc) busy.add(snapKey(sc.profileId, sc.database, sc.sessionId));
  }
  const seen = new Set<string>();
  for (const t of tabs) {
    const sc = tabScope(t);
    const k = sc && snapKey(sc.profileId, sc.database, sc.sessionId);
    if (!sc || !k || seen.has(k) || busy.has(k)) continue;
    seen.add(k);
    const e = store.entries[k];
    if (!e?.snapshot || e.status === "loading" || e.status === "refreshing") continue;
    if (useConnections.getState().servers[sc.profileId]?.status !== "connected") continue;
    if (now - (checkedAt.get(k) ?? 0) < minAge) continue;
    checkedAt.set(k, now);
    void store.refresh(sc.profileId, sc.database, false, sc.sessionId);
  }
}

/**
 * A server's "Refresh databases": the database list again, then a metadata refresh (the same incremental one as a
 * database's "Refresh metadata") of every online database whose catalog is loaded. A database not loaded yet needs
 * none: its first load asks the sidecar, which refreshes a catalog not read this launch or gone stale.
 */
export async function refreshServer(profileId: string): Promise<void> {
  await useConnections.getState().refreshDatabases(profileId);
  const server = useConnections.getState().servers[profileId];
  if (!server || isDisconnected(profileId)) return;
  const online = new Set(server.databases.filter((d) => !d.state || d.state.toUpperCase() === "ONLINE").map((d) => d.name));
  const store = useSnapshots.getState();
  const loaded = Object.values(store.entries).filter((e) => e.profileId === profileId && !e.sessionId && online.has(e.database));
  const linked = useLinkedServers.getState();
  const reloadLinked = !!linked.servers[profileId];
  linked.forget(profileId);
  await Promise.all([
    ...loaded.map((e) => store.refresh(profileId, e.database, false)),
    ...(reloadLinked ? [linked.loadServers(profileId, true)] : []),
  ]);
}

/**
 * The patch each entry is applying: the meta.updated events of one entry apply one after another, in the order they came
 * (H10: with two patches in flight, the older one answering last wrote its objects over the newer ones).
 */
const updating = new Map<string, Promise<void>>();

/**
 * The longest a patch waits for the one before it (CLAUDE §4, no unbounded waits): an RPC the engine never answers
 * holds up the next patches of that database this long at most, not for good.
 */
export const UPDATE_WAIT_MS = 30_000;

function applyUpdate(entry: SnapEntry, e: MetaUpdatedEvent): Promise<void> {
  const k = entry.key;
  const before = updating.get(k);
  // true: the one before is still running after UPDATE_WAIT_MS (it may be a full reload this patch cannot build on).
  const gaveUp = before
    ? withTimeLimit(before.then(() => false), UPDATE_WAIT_MS, "the previous schema update is still running").catch(() => true)
    : Promise.resolve(false);
  const next = gaveUp.then((reload) => {
    // Read again: an earlier patch, a load or a forget may have changed the entry while this one waited.
    const cur = useSnapshots.getState().entries[k];
    return cur ? applyUpdateNow(cur, e, reload) : undefined;
  });
  const settled: Promise<void> = next.catch(() => {}).finally(() => { if (updating.get(k) === settled) updating.delete(k); });
  updating.set(k, settled);
  return next;
}

/** meta.updated patches one entry in place: only changed objects are re-fetched (§8 step 4, §12). */
async function applyUpdateNow(entry: SnapEntry, e: MetaUpdatedEvent, reload = false) {
  const store = useSnapshots.getState();
  const { key: k, profileId, database, sessionId } = entry;
  // `reload`: this event gave up waiting on the one before, which may be a full reload or a patch still running: the
  // schema is read again whatever this event says. A status event that only set its status let the next patch start
  // from the hung one's sequence, and one of the two was lost while the entry read fresh (R2, R3-2).
  if (reload) {
    await store.load(profileId, database, sessionId);
    return;
  }
  // Status only: a refresh started or failed, or DDL ran in an open pooled transaction (H11: the sidecar marks the
  // snapshot stale and reads it when the transaction ends, so nothing is read again now — before, a full reload).
  if (e.status === "refreshing" || e.status === "error" || (e.status === "stale" && !e.full && !e.changedIds)) {
    // A failed refresh of a database with nothing cached is retried by ensure() (M21), like a failed load; the retry
    // is counted by the load that ensure() starts, once per cycle.
    useSnapshots.setState((s) => {
      const cur = s.entries[k];
      if (!cur) return s;
      const retry = e.status === "error" && holdsNothing(cur) ? { failedAt: Date.now(), retries: cur.retries ?? 0 } : {};
      // A refresh that starts clears the last failure's message (L2: every "refreshing" event brought it back).
      const error = e.status === "refreshing" ? null : e.error ?? cur.error;
      return { entries: { ...s.entries, [k]: { ...cur, status: e.status, error, version: cur.version + 1, ...retry } } };
    });
    return;
  }
  // A refresh that ends stale (one ran during such a transaction) still patches what it read, and stays stale.
  const status = e.status === "stale" ? "stale" : "fresh";
  if (e.full || !entry.snapshot || !e.changedIds || e.changedIds.length > 300) {
    await store.load(profileId, database, sessionId);
    return;
  }
  if (e.changed === 0) {
    // A load in flight read the catalog before this refresh ended: its status is older than this one (H9), dropped.
    nextSeq(k);
    checkedAt.set(k, Date.now());
    // Nothing changed, but what the refresh could not read may have (D4 S05): the notes come with the event.
    useSnapshots.setState((s) => (s.entries[k] ? { entries: { ...s.entries, [k]: { ...succeeded(s.entries[k]), status, snapshot: s.entries[k].snapshot && { ...s.entries[k].snapshot!, notes: e.notes ?? null }, version: s.entries[k].version + 1 } } } : s));
    return;
  }
  const ids = e.changedIds;
  const scope = { profileId, database, ids, ...sessionParam(sessionId) };
  // A changed table's foreign keys are read again with it (H12): before, the patch kept the old FK list, so a new FK
  // never reached JOIN suggestions and a dropped table's FKs stayed.
  let changed: SnapObject[] = [], changedFks: SnapFk[] = [];
  // A load that starts while the objects are read answers with a newer catalog: it wins, and this patch is dropped.
  // So is an answer (or a failure) of the engine a restart replaced: the new one's state is not the old one's (R3-3).
  const seq = loadSeq.get(k) ?? 0, generation = restarts;
  const overtaken = () => (loadSeq.get(k) ?? 0) !== seq || restarts !== generation;
  try {
    if (ids.length) [changed, changedFks] = await Promise.all([rpc("meta.objects", scope), rpc("meta.foreignKeys", scope)]);
  } catch (err) {
    if (overtaken()) return;
    // The schema cached stays; stale (not refreshing), so a refresh or the freshness check asks again.
    useSnapshots.setState((s) => (s.entries[k] ? { entries: { ...s.entries, [k]: {
      ...s.entries[k], status: "stale", error: err instanceof Error ? err.message : String(err), version: s.entries[k].version + 1,
    } } } : s));
    return;
  }
  const now = useSnapshots.getState().entries[k];
  if (!now || isDisconnected(profileId) || overtaken()) return; // disconnected (or the session closed) meanwhile
  // A load still in flight read the catalog before the refresh this patch comes from ended: it is dropped.
  nextSeq(k);
  const base = now.snapshot ?? entry.snapshot;
  const removed = new Set<number>(e.removedIds ?? []);
  const drop = new Set<number>([...removed, ...ids]);
  const objects = base.objects.filter((o) => !drop.has(o.id)).concat(changed);
  objects.sort((a, b) => (a.schema + "." + a.name).localeCompare(b.schema + "." + b.name));
  // An FK belongs to the table it is declared on: a changed or removed table's FKs are replaced, and one that points at a
  // removed table goes too.
  const fks = base.fks.filter((f) => !drop.has(f.parent) && !removed.has(f.referenced)).concat(changedFks);
  const snapshot: SchemaSnapshot = { ...base, objects, fks, status, notes: e.notes ?? null };
  const idx = buildSchemaIndex(snapshot, dialectOf(profileId), databasesOf(profileId), loginSchemaOf(profileId));
  useSnapshots.setState((s) => (s.entries[k] ? { entries: { ...s.entries, [k]: { ...succeeded(s.entries[k]), snapshot, index: idx, status, version: s.entries[k].version + 1 } } } : s));
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
    for (const k of drop) forgetKey(k);
    useSnapshots.setState((s) => ({ entries: Object.fromEntries(Object.entries(s.entries).filter(([k]) => !drop.has(k))) }));
  }
  for (const t of opened) ensureTabSnapshot(t);
}

export function installSnapshotListeners() {
  // applyUpdate settles its own failures; anything else is logged, never an unhandled rejection.
  const offMeta = onNotify("meta.updated", (e) => { onMetaUpdated(e).catch((err) => console.warn("[snapshots] meta.updated", err)); });
  // A disconnected server's schema is no longer shown or searched (explorer, completion).
  const offConn = useConnections.subscribe((s, prev) => {
    if (s.order !== prev.order) {
      for (const id of prev.order) {
        if (!s.order.includes(id)) {
          useSnapshots.getState().forget(id);
          useLinkedServers.getState().forget(id);
        }
      }
    }
    // Reconnected: the tabs still bound to the server get their schema back (it was dropped on disconnect). A schema
    // loaded before the server was connected is asked for again: restored tabs load theirs at startup, often before the
    // sidecar holds the profile's credentials, and without those it starts no refresh — the cache then stayed as old as
    // the last launch left it for the whole session (tables created since: no completion, read-only in the grid).
    for (const id of s.order) {
      if (s.servers[id]?.status !== "connected" || prev.servers[id]?.status === "connected") continue;
      const asked = new Set<string>();
      for (const t of Object.values(useTabs.getState().tabs)) {
        const scope = t.profileId === id && t.database ? tabScope(t) : undefined;
        const k = scope && snapKey(scope.profileId, scope.database, scope.sessionId);
        if (!scope || !k || asked.has(k)) continue;
        asked.add(k);
        const store = useSnapshots.getState();
        const entry = store.entries[k];
        if (!entry) store.ensure(scope.profileId, scope.database, scope.sessionId);
        else if (entry.status !== "loading" && entry.status !== "refreshing") void store.load(scope.profileId, scope.database, scope.sessionId);
      }
    }
  });
  const offTabs = useTabs.subscribe((s, prev) => {
    if (s.tabs !== prev.tabs) syncSessionViews(s.tabs, prev.tabs);
  });
  // M22: DDL from outside the app (another tool, a migration, a colleague) reaches completion while the app runs.
  const timer = setInterval(() => checkFreshness(FRESHNESS_MS), FRESHNESS_MS);
  const onFocus = () => checkFreshness(FOCUS_CHECK_MS);
  if (typeof window !== "undefined") window.addEventListener("focus", onFocus);
  return () => {
    offMeta();
    offConn();
    offTabs();
    clearInterval(timer);
    if (typeof window !== "undefined") window.removeEventListener("focus", onFocus);
  };
}

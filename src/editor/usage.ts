// Completion usage ranking (§9 "then usage count"): accepted items per (profile, database), kept by the sidecar in store.db
// (lang.usage.*) so the ranking survives a restart. Ranked by uses, halved every two weeks since the last use, so what was
// used lately and often comes first. Names are user data: they stay on this device and are never logged.
import { rpc } from "../ipc/client";

interface Entry { uses: number; last: number }
interface Db { items: Map<string, Entry>; loaded: boolean; loading?: Promise<void>; scores?: Map<string, number> }

const HALF_LIFE_MS = 14 * 24 * 3600 * 1000;
const FLUSH_MS = 1500;
const MAX_PER_CALL = 64;

const dbs = new Map<string, Db>();
const pending = new Map<string, { profileId: string; database: string; items: Set<string> }>();
let flushTimer: ReturnType<typeof setTimeout> | undefined;
/** Without a connection (no profile or database): this session only. */
const session = new Map<string, number>();

const keyOf = (profileId: string, database: string) => `${profileId}\u001f${database}`;

function db(profileId: string, database: string): Db {
  const k = keyOf(profileId, database);
  let d = dbs.get(k);
  if (!d) {
    d = { items: new Map(), loaded: false };
    dbs.set(k, d);
  }
  if (!d.loaded && !d.loading) {
    const target = d;
    target.loading = Promise.resolve()
      .then(() => rpc("lang.usage.list", { profileId, database }))
      .then((list) => {
        for (const u of list) {
          const prev = target.items.get(u.item);
          // Uses recorded in this session before the list arrived are kept on top.
          target.items.set(u.item, { uses: u.uses + (prev?.uses ?? 0), last: Math.max(u.lastUsedAt, prev?.last ?? 0) });
        }
        target.scores = undefined;
      })
      .catch(() => { /* best effort: ranking without history */ })
      .finally(() => { target.loaded = true; target.loading = undefined; });
  }
  return d;
}

/** Usage scores (lower-case item → integer) for tier-1 ranking; loads the database's history on first use. */
export function usageScores(profileId: string | undefined, database: string | undefined, now = Date.now()): Map<string, number> {
  if (!profileId || !database) return session;
  const d = db(profileId, database);
  if (!d.scores) {
    const s = new Map<string, number>();
    for (const [item, e] of d.items) s.set(item, Math.max(1, Math.round(10 * e.uses * Math.pow(0.5, Math.max(0, now - e.last) / HALF_LIFE_MS))));
    d.scores = s;
  }
  return d.scores;
}

/** An accepted completion item: counted at once for ranking, written to store.db in a batch. */
export function recordUsage(item: string, profileId?: string, database?: string, now = Date.now()) {
  const k = item.toLowerCase();
  if (!k || k.length > 512) return;
  if (!profileId || !database) {
    session.set(k, (session.get(k) ?? 0) + 10);
    return;
  }
  const d = db(profileId, database);
  const e = d.items.get(k);
  d.items.set(k, { uses: (e?.uses ?? 0) + 1, last: now });
  d.scores = undefined;
  const pk = keyOf(profileId, database);
  let p = pending.get(pk);
  if (!p) { p = { profileId, database, items: new Set() }; pending.set(pk, p); }
  p.items.add(k);
  clearTimeout(flushTimer);
  flushTimer = setTimeout(() => void flushUsage(), FLUSH_MS);
}

/** Writes the batched uses now (also on quit). */
export async function flushUsage() {
  clearTimeout(flushTimer);
  flushTimer = undefined;
  const batches = [...pending.values()];
  pending.clear();
  for (const b of batches) {
    const items = [...b.items];
    for (let i = 0; i < items.length; i += MAX_PER_CALL) {
      try { await rpc("lang.usage.record", { profileId: b.profileId, database: b.database, items: items.slice(i, i + MAX_PER_CALL) }); } catch { /* best effort */ }
    }
  }
}

/** Tests only. */
export function resetUsage() {
  dbs.clear();
  pending.clear();
  session.clear();
  clearTimeout(flushTimer);
  flushTimer = undefined;
}

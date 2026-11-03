// Recently closed query tabs (Ctrl+Shift+T reopens the newest). Kept in memory only: the session, transaction and
// results of a closed tab are gone, so a reopened tab gets its text and binding back and connects on first use.
import { create } from "zustand";
import type { BoundObject, TabState } from "./tabs";

export interface ClosedTabSnapshot {
  closedAt: number;
  title: string;
  profileId?: string;
  database?: string;
  sql: string;
  filePath?: string;
  boundObject?: BoundObject;
  readOnly?: boolean;
  /** The file-backed tab had changes not saved to its file. */
  dirty?: boolean;
  /** Tabs closed together by one bulk close share this id: they are one "Reopen 29 tabs" entry (D28). */
  batch?: string;
  /** Where the tab was: its strip (Dockview group) and position, so a reopened tab goes back there (D28). */
  groupId?: string;
  index?: number;
  /** A notebook (Phase C3): `sql` holds its .sqlbook document. */
  notebook?: boolean;
}

/** Closed tabs are kept up to this much SQL (characters), oldest dropped first: a size cap, not a count (D28). */
export const MAX_CLOSED_CHARS = 8 * 1024 * 1024;

/** The newest entries whose SQL fits the cap; an entry larger than the cap alone is still kept. */
function capped(list: ClosedTabSnapshot[]): ClosedTabSnapshot[] {
  let size = 0;
  const out: ClosedTabSnapshot[] = [];
  for (const s of list) {
    size += s.sql.length;
    if (out.length && size > MAX_CLOSED_CHARS) break;
    out.push(s);
  }
  return out;
}

/** The entries of one bulk close (the newest first), or the single entry at `index`. */
export function entryAt(list: readonly ClosedTabSnapshot[], index: number): number[] {
  const s = list[index];
  if (!s) return [];
  if (!s.batch) return [index];
  return list.map((x, i) => (x.batch === s.batch ? i : -1)).filter((i) => i >= 0);
}

/** What to remember about a tab being closed; undefined for an empty scratch tab (nothing worth reopening). */
export function snapshotOf(t: TabState, sql: string, now = Date.now()): ClosedTabSnapshot | undefined {
  if (!sql.trim() && !t.filePath && !t.boundObject) return undefined;
  return {
    closedAt: now, title: t.title, profileId: t.profileId, database: t.database, sql,
    filePath: t.filePath, boundObject: t.boundObject, readOnly: t.readOnly, dirty: t.dirty || undefined,
  };
}

/** "just now", "5 min ago", "3 h ago", "2 d ago". */
export function closedAgo(closedAt: number, now = Date.now()): string {
  const min = Math.floor((now - closedAt) / 60_000);
  if (min < 1) return "just now";
  if (min < 60) return `${min} min ago`;
  const h = Math.floor(min / 60);
  return h < 24 ? `${h} h ago` : `${Math.floor(h / 24)} d ago`;
}

interface ClosedTabsStore {
  list: ClosedTabSnapshot[];
  push(s: ClosedTabSnapshot): void;
  remove(index: number): void;
  /** Removes several entries at once (a bulk close reopened together). */
  removeMany(indexes: number[]): void;
  clear(): void;
}

export const useClosedTabs = create<ClosedTabsStore>((set) => ({
  list: [],
  push(s) {
    set((st) => ({ list: capped([s, ...st.list]) }));
  },
  remove(index) {
    set((st) => ({ list: st.list.filter((_, i) => i !== index) }));
  },
  removeMany(indexes) {
    const drop = new Set(indexes);
    set((st) => ({ list: st.list.filter((_, i) => !drop.has(i)) }));
  },
  clear() {
    set({ list: [] });
  },
}));

// The blocking tree (B2) flattened for a virtualised list: head blockers as roots (with placeholders for pseudo and
// hidden blockers), blocked sessions as children (a multi-blocker victim appears under each of its blockers), cycles
// as a "deadlock in progress" group. A node shows at most 50 children, then "+N more". Expand state is kept by key.
//
// The graph is a DAG, not a tree: PostgreSQL reports soft blocks, so on a hot row waiter k is blocked by every waiter
// ahead of it. Expanding every path is exponential (30 waiters: ~2^28 items), so each session's own victims are expanded
// once per build, under its shallowest visible occurrence (breadth-first from the heads); its other occurrences are
// leaves that say where its victims are shown. The whole list is capped at MAX_TREE_ITEMS.
import type { ActivityRow, BlockingInfo } from "../ipc/contract";
import { keyOf } from "./format";

export const MAX_CHILDREN = 50;
export const MAX_TREE_ITEMS = 5_000;

export type TreeItem =
  | {
      kind: "session"; id: string; depth: number; row: ActivityRow; childCount: number; expanded: boolean; inCycle: boolean;
      /** Set on a repeated occurrence: its victims are shown under this blocker id (a session id, or a pseudo-blocker < 0). */
      elsewhere?: number;
    }
  | { kind: "placeholder"; id: string; depth: number; sessionId: number; reason: string; isSession: boolean; childCount: number; expanded: boolean }
  | { kind: "cycle"; id: string; depth: number; members: number[] }
  | { kind: "more"; id: string; depth: number; hidden: number }
  | { kind: "truncated"; id: string; depth: number; hidden: number };

/** Ids of the nodes shown expanded: everything is expanded unless the user collapsed it (by node path id). */
export function buildTree(rows: ActivityRow[], info: BlockingInfo | null | undefined, collapsed: ReadonlySet<string>, showAll: ReadonlySet<string> = new Set()): TreeItem[] {
  const byId = new Map<number, ActivityRow>();
  for (const r of rows) if (!byId.has(r.key.id)) byId.set(r.key.id, r);
  const victims = new Map<number, ActivityRow[]>();
  for (const r of rows) {
    for (const b of r.blockedBy) {
      let l = victims.get(b);
      if (!l) victims.set(b, (l = []));
      l.push(r);
    }
    if (r.blockedByNote) {
      const pseudo = noteId(r.blockedByNote); // the sidecar's placeholder id for this pseudo-blocker (-2 / -3 / -4)
      let l = victims.get(pseudo);
      if (!l) victims.set(pseudo, (l = []));
      l.push(r);
    }
  }
  for (const l of victims.values()) l.sort((a, b) => (b.waitMs ?? 0) - (a.waitMs ?? 0) || a.key.id - b.key.id);
  const shown = (id: number, path: string) => {
    const list = victims.get(id) ?? [];
    return showAll.has(path) ? list : list.slice(0, MAX_CHILDREN);
  };
  const heads = info?.headBlockers ?? [];
  const headSet = new Set(heads);

  // Where each session's own victims are expanded: its first visible occurrence breadth-first (the shallowest one).
  const home = new Map<number, { path: string; parent: number }>();
  const queue: { id: number; path: string }[] = [];
  for (const head of heads) if (!collapsed.has(`h${head}`)) queue.push({ id: head, path: `h${head}` });
  for (let q = 0; q < queue.length; q++) {
    const { id, path } = queue[q];
    for (const r of shown(id, path)) {
      if (home.has(r.key.id) || headSet.has(r.key.id) || !victims.get(r.key.id)?.length) continue;
      const nid = `${path}/${keyOf(r.key)}`;
      home.set(r.key.id, { path: nid, parent: id });
      if (!collapsed.has(nid)) queue.push({ id: r.key.id, path: nid });
    }
  }

  const inCycle = new Set<number>(info?.cycles.flat() ?? []);
  const out: TreeItem[] = [];
  let cut = 0;
  const push = (item: TreeItem) => {
    if (out.length < MAX_TREE_ITEMS) out.push(item);
    else cut++;
  };

  const children = (id: number, depth: number, path: string) => {
    const all = victims.get(id) ?? [];
    const list = shown(id, path);
    for (const r of list) {
      const nid = `${path}/${keyOf(r.key)}`;
      const count = victims.get(r.key.id)?.length ?? 0;
      const at = home.get(r.key.id);
      if (count > 0 && at?.path === nid) {
        const expanded = !collapsed.has(nid);
        push({ kind: "session", id: nid, depth, row: r, childCount: count, expanded, inCycle: inCycle.has(r.key.id) });
        if (expanded) children(r.key.id, depth + 1, nid);
      } else if (count > 0 && at) {
        push({ kind: "session", id: nid, depth, row: r, childCount: count, expanded: false, inCycle: inCycle.has(r.key.id), elsewhere: at.parent });
      } else {
        // A leaf, or a cycle member below its own ancestor (shown once, never descended into).
        push({ kind: "session", id: nid, depth, row: r, childCount: 0, expanded: false, inCycle: inCycle.has(r.key.id) });
      }
    }
    if (all.length > list.length) push({ kind: "more", id: `${path}/+more`, depth, hidden: all.length - list.length });
  };

  for (const head of heads) {
    const row = byId.get(head);
    const path = `h${head}`;
    const count = victims.get(head)?.length ?? 0;
    const expanded = !collapsed.has(path);
    if (row) push({ kind: "session", id: path, depth: 0, row, childCount: count, expanded, inCycle: false });
    else {
      const p = info?.placeholders.find((x) => x.id === head);
      push({ kind: "placeholder", id: path, depth: 0, sessionId: head, reason: p?.reason ?? "session hidden", isSession: p?.isSession ?? head > 0, childCount: count, expanded });
    }
    if (expanded) children(head, 1, path);
  }
  for (const [i, cycle] of (info?.cycles ?? []).entries()) {
    push({ kind: "cycle", id: `c${i}`, depth: 0, members: cycle });
    for (const id of cycle) {
      const row = byId.get(id);
      if (row) push({ kind: "session", id: `c${i}/${keyOf(row.key)}`, depth: 1, row, childCount: 0, expanded: false, inCycle: true });
    }
  }
  if (cut > 0) out.push({ kind: "truncated", id: "+truncated", depth: 0, hidden: cut });
  return out;
}

function noteId(note: string): number {
  return note === "orphanedDtc" ? -2 : note === "deferredRecovery" ? -3 : note === "latchOwnerUnknown" ? -4 : -1;
}

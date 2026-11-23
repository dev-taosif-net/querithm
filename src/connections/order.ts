// Custom ordering helpers for connection groups and profiles (stored in ui.settings as groupOrder / profileOrder).
const compareNames = (a: string, b: string) => a.localeCompare(b, undefined, { sensitivity: "base", numeric: true });

/** Map each key to its first index in `order` (missing keys are absent). */
export function orderMap(order: readonly string[] | undefined, caseInsensitive = false): Map<string, number> {
  const m = new Map<string, number>();
  if (!order?.length) return m;
  for (let i = 0; i < order.length; i++) {
    const k = caseInsensitive ? order[i].toLocaleLowerCase() : order[i];
    if (!m.has(k)) m.set(k, i);
  }
  return m;
}

/** Rank of `key` in `map`, or Infinity when unlisted. */
export function orderRank(map: ReadonlyMap<string, number>, key: string, caseInsensitive = false): number {
  const r = map.get(caseInsensitive ? key.toLocaleLowerCase() : key);
  return r === undefined ? Infinity : r;
}

/** Sort group names by custom `groupOrder` first, then A–Z. */
export function sortGroupNames(names: readonly string[], groupOrder?: readonly string[]): string[] {
  const rank = orderMap(groupOrder, true);
  return [...names].sort((a, b) => orderRank(rank, a, true) - orderRank(rank, b, true) || compareNames(a, b));
}

/**
 * Reorder `draggedId` before or after `targetId`, preserving any currently hidden/disconnected ids already in
 * `currentOrder` while snapshotting `visibleIds` so every visible sibling gets a stable position.
 */
export function reorderList(
  currentOrder: readonly string[],
  visibleIds: readonly string[],
  draggedId: string,
  targetId: string,
  place: "before" | "after",
): string[] {
  if (draggedId === targetId) return [...currentOrder];
  const merged: string[] = [];
  const seen = new Set<string>();
  for (const id of currentOrder) {
    if (!seen.has(id)) {
      seen.add(id);
      merged.push(id);
    }
  }
  for (const id of visibleIds) {
    if (!seen.has(id)) {
      seen.add(id);
      merged.push(id);
    }
  }
  if (!seen.has(draggedId)) merged.push(draggedId);
  if (!seen.has(targetId)) merged.push(targetId);
  const without = merged.filter((id) => id !== draggedId);
  const at = without.indexOf(targetId);
  if (at < 0) return without;
  without.splice(place === "before" ? at : at + 1, 0, draggedId);
  return without;
}

/** Replace a renamed group name in `order` while keeping its slot. */
export function renameInOrder(order: readonly string[], from: string, to: string): string[] {
  const fromLow = from.toLocaleLowerCase();
  const out: string[] = [];
  const seen = new Set<string>();
  for (const name of order) {
    const next = name.toLocaleLowerCase() === fromLow ? to : name;
    const key = next.toLocaleLowerCase();
    if (!seen.has(key)) {
      seen.add(key);
      out.push(next);
    }
  }
  return out;
}

/** Remove an id or group name from `order`. */
export function removeFromOrder(order: readonly string[], target: string, caseInsensitive = false): string[] {
  const needle = caseInsensitive ? target.toLocaleLowerCase() : target;
  return order.filter((x) => (caseInsensitive ? x.toLocaleLowerCase() : x) !== needle);
}

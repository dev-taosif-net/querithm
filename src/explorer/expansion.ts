// Tree expansion keys are "<kind>:<profileId>[:…]" (s:, y:, db:, sc:, c:, o:, by name, tree.ts); a server's keys go when it
// leaves the Explorer, so it opens one level again when the user reconnects it.

/** Drops the expansion of servers that left the Explorer, so a reconnect the user starts opens one level again. */
export function pruneExpanded(expanded: ReadonlySet<string>, shown: readonly string[]): ReadonlySet<string> {
  const live = new Set(shown);
  const next = new Set<string>();
  for (const k of expanded) {
    const parts = k.split(":");
    if (parts.length > 1 && ["s", "y", "d", "db", "sc", "c", "o"].includes(parts[0]) && !live.has(parts[1])) continue;
    next.add(k);
  }
  return next.size === expanded.size ? expanded : next;
}

/**
 * Servers that just connected for the first time since joining the tree. `seen` is updated in place (ids that left the
 * tree are dropped), so a server already seen keeps whatever the user collapsed or opened when another connects.
 */
export function takeNewlyConnected(seen: Set<string>, shown: readonly string[], isConnected: (id: string) => boolean): string[] {
  const live = new Set(shown);
  for (const id of seen) if (!live.has(id)) seen.delete(id);
  const fresh = shown.filter((id) => !seen.has(id) && isConnected(id));
  for (const id of fresh) seen.add(id);
  return fresh;
}

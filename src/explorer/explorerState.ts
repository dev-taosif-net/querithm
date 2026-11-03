// Object Explorer view state outside React, so the workspace file can save the expansion (by name, §10) and the
// bench can drive the tree. Transient: selection, search, details; saved: the expanded keys.
import { create } from "zustand";
import type { EnvironmentKind } from "../ipc/contract";
import type { DetailsCell, KindFilter } from "./tree";

/** Keys saved with the workspace, at most (a whole expanded 20,000-table folder is one key, not 20,000). */
export const MAX_SAVED_KEYS = 2000;

interface ExplorerState {
  expanded: ReadonlySet<string>;
  collapsedEnvs: ReadonlySet<EnvironmentKind>;
  details: Readonly<Record<string, DetailsCell>>;
  kind: KindFilter;
  folderFilters: Readonly<Record<string, string>>;
  query: string;
  /** Search kinds whose results are shown past the cap ("Show all"). */
  uncapped: ReadonlySet<string>;
  selected?: string;
  setExpanded(f: (prev: ReadonlySet<string>) => ReadonlySet<string>): void;
  toggle(key: string, open?: boolean): boolean;
  set(p: Partial<Pick<ExplorerState, "collapsedEnvs" | "details" | "kind" | "folderFilters" | "query" | "uncapped" | "selected">>): void;
}

export const useExplorer = create<ExplorerState>((set, get) => ({
  expanded: new Set(),
  collapsedEnvs: new Set(),
  details: {},
  kind: "all",
  folderFilters: {},
  query: "",
  uncapped: new Set(),
  selected: undefined,
  setExpanded(f) {
    const next = f(get().expanded);
    if (next !== get().expanded) set({ expanded: next });
  },
  /** Opens (or closes) a key; returns whether it is now open. */
  toggle(key, open) {
    const was = get().expanded.has(key);
    const now = open ?? !was;
    if (now === was) return now;
    const next = new Set(get().expanded);
    if (now) next.add(key);
    else next.delete(key);
    set({ expanded: next });
    return now;
  },
  set(p) {
    set(p);
  },
}));

/** The expansion as the workspace file stores it: the most recently opened keys when there are too many (a set keeps
 *  insertion order, and reopening a key moves it to the end). */
export function savedExpansion(): string[] {
  return [...useExplorer.getState().expanded].slice(-MAX_SAVED_KEYS);
}

/** Restores a saved expansion (untrusted file content: strings only, bounded). */
export function restoreExpansion(keys: unknown) {
  if (!Array.isArray(keys)) return;
  const valid = keys.filter((k): k is string => typeof k === "string" && k.length <= 2048 && /^(env|s|y|db|sc|c|o):/.test(k)).slice(-MAX_SAVED_KEYS);
  useExplorer.getState().setExpanded((prev) => new Set([...prev, ...valid]));
}

import { create } from "zustand";
import type { FindUsagesResult, RefactorTarget } from "../ipc/contract";

/** The last usages search shown in a query tab's bottom panel. */
export interface UsagesView {
  target?: RefactorTarget;
  /** A local symbol: its occurrences in the tab. */
  local?: { name: string; spans: { offset: number; length: number; line: number; column: number; snippet: string }[] };
  result?: FindUsagesResult;
  running: boolean;
  error?: string;
}

interface UsagesStore {
  byTab: Record<string, UsagesView>;
  set(tabId: string, v: UsagesView): void;
  clear(tabId: string): void;
}

export const useUsages = create<UsagesStore>((set) => ({
  byTab: {},
  set(tabId, v) {
    set((s) => ({ byTab: { ...s.byTab, [tabId]: v } }));
  },
  clear(tabId) {
    set((s) => {
      const { [tabId]: _, ...rest } = s.byTab;
      return { byTab: rest };
    });
  },
}));

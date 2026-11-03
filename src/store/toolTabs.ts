import { create } from "zustand";

/** Workspace tabs that are not query editors: ER diagrams (M2), schema compare (M5), data compare (M6), activity monitor (Phase B). Not persisted. */
export type ToolKind = "erd" | "schemaCompare" | "dataCompare" | "activity";

export interface ToolTab {
  id: string;
  kind: ToolKind;
  title: string;
  profileId?: string;
  database?: string;
  /**
   * A session-owned engine's query tab session the tool was opened from (Phase D0d, snapshots.ts ownSessionFor): the
   * ER diagram reads that session's own view (an in-memory database exists nowhere else). Absent for server engines.
   */
  sessionId?: string;
  /** Kind-specific start state (e.g. the ERD's root table and mode). */
  params: Record<string, unknown>;
}

interface ToolStore {
  tools: Record<string, ToolTab>;
  activeToolId?: string;
  add(t: ToolTab): void;
  update(id: string, patch: Partial<ToolTab>): void;
  remove(id: string): void;
  setActive(id: string | undefined): void;
}

let n = 0;
export const newToolId = () => `tool${Date.now().toString(36)}${(++n).toString(36)}`;

export const useToolTabs = create<ToolStore>((set) => ({
  tools: {},
  activeToolId: undefined,
  add(t) {
    set((s) => ({ tools: { ...s.tools, [t.id]: t }, activeToolId: t.id }));
  },
  update(id, patch) {
    set((s) => (s.tools[id] ? { tools: { ...s.tools, [id]: { ...s.tools[id], ...patch } } } : s));
  },
  remove(id) {
    set((s) => {
      const { [id]: _, ...rest } = s.tools;
      return { tools: rest, activeToolId: s.activeToolId === id ? undefined : s.activeToolId };
    });
  },
  setActive(id) {
    set({ activeToolId: id });
  },
}));

export const isToolId = (id: string | undefined) => !!id && !!useToolTabs.getState().tools[id];

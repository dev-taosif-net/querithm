import { create } from "zustand";
import type { LinkedCatalogInfo, LinkedServerInfo, LinkedTableInfo, LinkedTestResult } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { isDisconnected, useConnections } from "./connections";

export type LinkedLoadStatus = "loading" | "ok" | "error";

export interface LinkedServersEntry {
  status: LinkedLoadStatus;
  items: LinkedServerInfo[];
  note?: string | null;
  error?: string;
}

export interface LinkedCatalogsEntry {
  status: LinkedLoadStatus;
  items: LinkedCatalogInfo[];
  error?: string;
}

export interface LinkedTablesEntry {
  status: LinkedLoadStatus;
  items: LinkedTableInfo[];
  truncated?: boolean;
  note?: string | null;
  error?: string;
}

export const linkedCatalogKey = (profileId: string, serverName: string) => `${profileId}\u001f${serverName}`;
export const linkedTableKey = (profileId: string, serverName: string, catalog: string) => `${profileId}\u001f${serverName}\u001f${catalog}`;

/**
 * Lazy in-memory cache of server-level Linked / External Servers and their browsed catalogs and tables.
 * Nothing here is fetched on connect or startup — only when the user expands a folder or asks to refresh.
 */
interface LinkedServersStore {
  servers: Record<string, LinkedServersEntry>;
  catalogs: Record<string, LinkedCatalogsEntry>;
  tables: Record<string, LinkedTablesEntry>;
  loadServers(profileId: string, force?: boolean): Promise<void>;
  loadCatalogs(profileId: string, serverName: string, force?: boolean): Promise<void>;
  loadTables(profileId: string, serverName: string, catalog: string, force?: boolean): Promise<void>;
  testServer(profileId: string, serverName: string): Promise<LinkedTestResult>;
  forget(profileId: string): void;
}

let seqCounter = 0;
const reqSeq = new Map<string, number>();
const nextSeq = (k: string) => {
  const n = ++seqCounter;
  reqSeq.set(k, n);
  return n;
};
const isLatest = (k: string, seq: number, profileId: string, sessionId: string) =>
  reqSeq.get(k) === seq && useConnections.getState().servers[profileId]?.sessionId === sessionId && !isDisconnected(profileId);

export const useLinkedServers = create<LinkedServersStore>((set, get) => ({
  servers: {},
  catalogs: {},
  tables: {},

  async loadServers(profileId, force = false) {
    const cur = get().servers[profileId];
    if (!force && (cur?.status === "loading" || cur?.status === "ok")) return;
    const sessionId = useConnections.getState().servers[profileId]?.sessionId;
    if (!sessionId || isDisconnected(profileId)) return;
    const reqKey = `s:${profileId}`;
    const seq = nextSeq(reqKey);
    set((s) => ({ servers: { ...s.servers, [profileId]: { status: "loading", items: cur?.items ?? [], note: cur?.note } } }));
    try {
      const res = await rpc("meta.linkedServers", { sessionId });
      if (!isLatest(reqKey, seq, profileId, sessionId)) return;
      set((s) => ({
        servers: { ...s.servers, [profileId]: { status: "ok", items: res.servers, note: res.note } },
      }));
    } catch (e) {
      if (!isLatest(reqKey, seq, profileId, sessionId)) return;
      const error = e instanceof Error ? e.message : String(e);
      set((s) => ({ servers: { ...s.servers, [profileId]: { status: "error", items: [], error } } }));
    }
  },

  async loadCatalogs(profileId, serverName, force = false) {
    const key = linkedCatalogKey(profileId, serverName);
    const cur = get().catalogs[key];
    if (!force && (cur?.status === "loading" || cur?.status === "ok")) return;
    const sessionId = useConnections.getState().servers[profileId]?.sessionId;
    if (!sessionId || isDisconnected(profileId)) return;
    const reqKey = `c:${key}`;
    const seq = nextSeq(reqKey);
    set((s) => ({ catalogs: { ...s.catalogs, [key]: { status: "loading", items: cur?.items ?? [] } } }));
    try {
      const items = await rpc("meta.linkedCatalogs", { sessionId, serverName });
      if (!isLatest(reqKey, seq, profileId, sessionId)) return;
      set((s) => ({ catalogs: { ...s.catalogs, [key]: { status: "ok", items } } }));
    } catch (e) {
      if (!isLatest(reqKey, seq, profileId, sessionId)) return;
      const error = e instanceof Error ? e.message : String(e);
      set((s) => ({ catalogs: { ...s.catalogs, [key]: { status: "error", items: [], error } } }));
    }
  },

  async loadTables(profileId, serverName, catalog, force = false) {
    const key = linkedTableKey(profileId, serverName, catalog);
    const cur = get().tables[key];
    if (!force && (cur?.status === "loading" || cur?.status === "ok")) return;
    const sessionId = useConnections.getState().servers[profileId]?.sessionId;
    if (!sessionId || isDisconnected(profileId)) return;
    const reqKey = `t:${key}`;
    const seq = nextSeq(reqKey);
    set((s) => ({ tables: { ...s.tables, [key]: { status: "loading", items: cur?.items ?? [] } } }));
    try {
      const res = await rpc("meta.linkedTables", { sessionId, serverName, catalog });
      if (!isLatest(reqKey, seq, profileId, sessionId)) return;
      set((s) => ({
        tables: { ...s.tables, [key]: { status: "ok", items: res.tables, truncated: res.truncated, note: res.note } },
      }));
    } catch (e) {
      if (!isLatest(reqKey, seq, profileId, sessionId)) return;
      const error = e instanceof Error ? e.message : String(e);
      set((s) => ({ tables: { ...s.tables, [key]: { status: "error", items: [], error } } }));
    }
  },

  async testServer(profileId, serverName) {
    const sessionId = await useConnections.getState().sessionFor(profileId);
    return rpc("meta.testLinkedServer", { sessionId, serverName });
  },

  forget(profileId) {
    const prefix = `${profileId}\u001f`;
    reqSeq.delete(`s:${profileId}`);
    for (const k of [...reqSeq.keys()]) {
      if (k.startsWith(`c:${prefix}`) || k.startsWith(`t:${prefix}`)) reqSeq.delete(k);
    }
    set((s) => {
      const nextServers = { ...s.servers };
      delete nextServers[profileId];
      const nextCatalogs = Object.fromEntries(Object.entries(s.catalogs).filter(([k]) => !k.startsWith(prefix)));
      const nextTables = Object.fromEntries(Object.entries(s.tables).filter(([k]) => !k.startsWith(prefix)));
      return { servers: nextServers, catalogs: nextCatalogs, tables: nextTables };
    });
  },
}));

// Session-keyed snapshots for session-owned engines (Phase D0d): a SQLite tab reads its own session's view (an
// in-memory database, or the shared file snapshot plus its temporary objects); server engines keep one shared snapshot
// per profile + database and never send a sessionId.
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MetaUpdatedEvent, Profile, SchemaSnapshot, SnapObject } from "../../ipc/contract";

const { rpc, notify } = vi.hoisted(() => ({
  rpc: vi.fn(),
  notify: new Map<string, (p: unknown) => void>(),
}));
vi.mock("../../ipc/client", () => ({
  rpc,
  onNotify: (method: string, h: (p: unknown) => void) => {
    notify.set(method, h);
    return () => notify.delete(method);
  },
  shell: {},
  RpcError: class extends Error {},
}));

import { ensureTabSnapshot, installSnapshotListeners, snapKey, tabScope, tabSnapKey, tabSnapshot, useSnapshots } from "../snapshots";
import { useConnections } from "../connections";
import { useProfiles } from "../profiles";
import { useTabs, type TabState } from "../tabs";

const profile = (o: Partial<Profile>): Profile =>
  ({ id: "x", name: "x", engine: "sqlite", auth: "none", environment: "dev", tlsMode: "default", trustCert: false, createdAt: 0, updatedAt: 0, ...o }) as Profile;
const PROFILES = [
  profile({ id: "mem", location: "memory", databaseDefault: "main" }),
  profile({ id: "file", location: "file", file: { grantId: "g1", displayName: "app.db" }, databaseDefault: "main" }),
  profile({ id: "ms", engine: "mssql", auth: "sql", host: "h", databaseDefault: "Sales" }),
];
const obj = (id: number, name: string, schema = "main"): SnapObject => ({ id, schema, name, kind: "table" });
const snap = (o: Partial<SchemaSnapshot>): SchemaSnapshot => ({ profileId: "mem", database: "main", status: "fresh", objects: [], fks: [], ...o });
const event = (o: Partial<MetaUpdatedEvent>): MetaUpdatedEvent => ({ profileId: "mem", database: "main", status: "fresh", changed: 0, full: false, elapsedMs: 1, ...o });
const s = () => useSnapshots.getState();
const flush = () => new Promise((r) => setTimeout(r, 0));
const tab = (o: Partial<TabState>) => useTabs.getState().add({ id: o.id ?? "t1", title: "q.sql", ...o });
const snapshotCalls = () => rpc.mock.calls.filter((c) => c[0] === "meta.snapshot").map((c) => c[1]);

let uninstall: (() => void) | undefined;
beforeEach(() => {
  uninstall?.();
  uninstall = undefined;
  rpc.mockReset();
  notify.clear();
  useSnapshots.setState({ entries: {} });
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useProfiles.setState({ profiles: PROFILES, groups: [] });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
});

describe("which snapshot a tab reads", () => {
  it("a server engine reads the shared snapshot, with or without a session", () => {
    expect(tabScope({ profileId: "ms", database: "Sales", sessionId: "s1" })).toEqual({ profileId: "ms", database: "Sales" });
    expect(tabSnapKey({ profileId: "ms", database: "Sales", sessionId: "s1" })).toBe(snapKey("ms", "Sales"));
  });
  it("a SQLite tab with a session reads its session's own view; the key is not the shared one", () => {
    expect(tabScope({ profileId: "file", database: "main", sessionId: "s1" })).toEqual({ profileId: "file", database: "main", sessionId: "s1" });
    expect(snapKey("file", "main", "s1")).not.toBe(snapKey("file", "main"));
    expect(snapKey("mem", "main", "s1")).not.toBe(snapKey("mem", "main", "s2"));
  });
  it("before its session opens, a file database reads the shared file snapshot and an in-memory one reads none", () => {
    expect(tabScope({ profileId: "file", database: "main" })).toEqual({ profileId: "file", database: "main" });
    expect(tabScope({ profileId: "mem", database: "main" })).toBeUndefined();
    expect(tabScope({ profileId: "mem" })).toBeUndefined();
    expect(tabScope(undefined)).toBeUndefined();
  });
  it("ensureTabSnapshot asks for nothing for an in-memory tab without its session", () => {
    ensureTabSnapshot({ profileId: "mem", database: "main" });
    expect(rpc).not.toHaveBeenCalled();
  });
});

describe("session views", () => {
  it("load names the session in meta.snapshot and keys the entry by profile + database + session", async () => {
    rpc.mockResolvedValueOnce(snap({ objects: [obj(1, "t")], sessionId: "s1" }));
    await s().load("mem", "main", "s1");
    expect(rpc).toHaveBeenCalledWith("meta.snapshot", { profileId: "mem", database: "main", sessionId: "s1" });
    expect(s().get("mem", "main", "s1")).toMatchObject({ sessionId: "s1", status: "fresh" });
    expect(s().get("mem", "main")).toBeUndefined();
  });
  it("server engines send exactly what they always sent (no sessionId)", async () => {
    rpc.mockResolvedValue(snap({ profileId: "ms", database: "Sales" }));
    await s().load("ms", "Sales");
    await s().refresh("ms", "Sales", true);
    expect(rpc.mock.calls[0]).toEqual(["meta.snapshot", { profileId: "ms", database: "Sales" }]);
    expect(rpc.mock.calls[1]).toEqual(["meta.refresh", { profileId: "ms", database: "Sales", full: true }]);
  });
  it("two in-memory tabs with the same table name keep two isolated entries", async () => {
    rpc.mockImplementation(async (_m: string, a: { sessionId: string }) => snap({ objects: [obj(a.sessionId === "s1" ? 1 : 2, "t")], sessionId: a.sessionId }));
    await s().load("mem", "main", "s1");
    await s().load("mem", "main", "s2");
    expect(s().get("mem", "main", "s1")?.snapshot?.objects[0].id).toBe(1);
    expect(s().get("mem", "main", "s2")?.snapshot?.objects[0].id).toBe(2);
    expect(tabSnapshot({ profileId: "mem", database: "main", sessionId: "s2" })?.index?.byName.get("t")?.[0].id).toBe(2);
  });
  it("refresh of a session view names the session", async () => {
    rpc.mockResolvedValue({ jobId: "j" });
    await s().refresh("mem", "main", false, "s1");
    expect(rpc).toHaveBeenCalledWith("meta.refresh", { profileId: "mem", database: "main", full: false, sessionId: "s1" });
  });
});

describe("meta.updated", () => {
  beforeEach(() => {
    uninstall = installSnapshotListeners();
  });
  const send = async (e: MetaUpdatedEvent) => {
    notify.get("meta.updated")!(e);
    await flush();
    await flush();
  };
  const seed = (key: string, o: Partial<{ profileId: string; database: string; sessionId: string; objects: SnapObject[] }>) => {
    const entry = { key, profileId: o.profileId ?? "mem", database: o.database ?? "main", ...(o.sessionId ? { sessionId: o.sessionId } : {}), status: "fresh" as const, version: 1,
      snapshot: snap({ profileId: o.profileId ?? "mem", objects: o.objects ?? [] }) };
    useSnapshots.setState((st) => ({ entries: { ...st.entries, [key]: entry } }));
  };

  it("an event for a session patches only that session's entry, fetching its objects with the session", async () => {
    seed(snapKey("mem", "main", "s1"), { sessionId: "s1", objects: [obj(1, "a")] });
    seed(snapKey("mem", "main", "s2"), { sessionId: "s2", objects: [obj(1, "a")] });
    rpc.mockResolvedValueOnce([obj(5, "b")]);
    await send(event({ sessionId: "s1", changed: 1, changedIds: [5] }));
    expect(rpc).toHaveBeenCalledWith("meta.objects", { profileId: "mem", database: "main", ids: [5], sessionId: "s1" });
    expect(s().get("mem", "main", "s1")?.snapshot?.objects.map((o) => o.name)).toEqual(["a", "b"]);
    expect(s().get("mem", "main", "s2")?.snapshot?.objects.map((o) => o.name)).toEqual(["a"]);
  });
  it("a shared entry keeps ignoring an event that carries a sessionId (temporary tables stay out of the shared snapshot)", async () => {
    seed(snapKey("file", "main"), { profileId: "file", objects: [obj(1, "a")] });
    await send(event({ profileId: "file", sessionId: "s1", changed: 1, changedIds: [9] }));
    expect(rpc).not.toHaveBeenCalled();
    expect(s().get("file", "main")?.snapshot?.objects.map((o) => o.name)).toEqual(["a"]);
  });
  it("an event for a session nobody holds any more (tab closed) is dropped", async () => {
    await send(event({ sessionId: "gone", changed: 1, changedIds: [1], full: true }));
    expect(rpc).not.toHaveBeenCalled();
    expect(s().entries).toEqual({});
  });
  it("a shared change of a file database reaches the shared entry and every session view of that file", async () => {
    seed(snapKey("file", "main"), { profileId: "file", objects: [obj(1, "a")] });
    seed(snapKey("file", "main", "s1"), { profileId: "file", sessionId: "s1", objects: [obj(1, "a"), obj(100, "tmp", "temp")] });
    rpc.mockImplementation(async (_m: string, a: { sessionId?: string }) => [obj(2, a.sessionId ? "b(s1)" : "b")]);
    await send(event({ profileId: "file", changed: 1, changedIds: [2] }));
    expect(rpc).toHaveBeenCalledWith("meta.objects", { profileId: "file", database: "main", ids: [2] });
    expect(rpc).toHaveBeenCalledWith("meta.objects", { profileId: "file", database: "main", ids: [2], sessionId: "s1" });
    expect(s().get("file", "main")?.snapshot?.objects.map((o) => o.name)).toEqual(["a", "b"]);
    expect(s().get("file", "main", "s1")?.snapshot?.objects.map((o) => o.name)).toEqual(["a", "b(s1)", "tmp"]);
  });
  it("an in-memory session view has no shared part: an event without a sessionId leaves it alone", async () => {
    seed(snapKey("mem", "main", "s1"), { sessionId: "s1", objects: [obj(1, "a")] });
    await send(event({ changed: 1, changedIds: [2], full: true }));
    expect(rpc).not.toHaveBeenCalled();
  });
  it("a full or status event on a session view reloads or marks that view only", async () => {
    seed(snapKey("mem", "main", "s1"), { sessionId: "s1", objects: [obj(1, "a")] });
    await send(event({ sessionId: "s1", status: "refreshing" }));
    expect(s().get("mem", "main", "s1")?.status).toBe("refreshing");
    rpc.mockResolvedValueOnce(snap({ objects: [obj(3, "c")], sessionId: "s1" }));
    await send(event({ sessionId: "s1", full: true, changed: 1 }));
    expect(rpc).toHaveBeenCalledWith("meta.snapshot", { profileId: "mem", database: "main", sessionId: "s1" });
    expect(s().get("mem", "main", "s1")?.snapshot?.objects.map((o) => o.name)).toEqual(["c"]);
  });
});

describe("session views follow the tabs", () => {
  beforeEach(() => {
    uninstall = installSnapshotListeners();
    rpc.mockImplementation(async (_m: string, a: { profileId: string; database: string; sessionId?: string }) => snap({ ...a }));
  });

  it("a SQLite tab whose session opens gets its own view loaded", async () => {
    tab({ profileId: "mem", database: "main" });
    useTabs.getState().update("t1", { sessionId: "s1" });
    await flush();
    expect(snapshotCalls()).toEqual([{ profileId: "mem", database: "main", sessionId: "s1" }]);
    expect(s().get("mem", "main", "s1")).toBeTruthy();
  });
  it("closing the tab, or losing its session, drops the view; the shared file snapshot stays", async () => {
    tab({ id: "a", profileId: "file", database: "main", sessionId: "s1" });
    tab({ id: "b", profileId: "file", database: "main", sessionId: "s2" });
    await s().load("file", "main");
    await flush();
    expect(Object.keys(s().entries).sort()).toEqual([snapKey("file", "main"), snapKey("file", "main", "s1"), snapKey("file", "main", "s2")].sort());
    useTabs.getState().remove("a");
    expect(s().get("file", "main", "s1")).toBeUndefined();
    useTabs.getState().update("b", { sessionId: undefined });
    expect(s().get("file", "main", "s2")).toBeUndefined();
    expect(s().get("file", "main")).toBeTruthy();
  });
  it("a view that arrives after its tab closed is not kept", async () => {
    let resolve!: (v: SchemaSnapshot) => void;
    rpc.mockReturnValueOnce(new Promise<SchemaSnapshot>((r) => (resolve = r)));
    tab({ profileId: "mem", database: "main" });
    useTabs.getState().update("t1", { sessionId: "s1" });
    useTabs.getState().remove("t1");
    resolve(snap({ objects: [obj(1, "late")], sessionId: "s1" }));
    await flush();
    expect(s().entries).toEqual({});
  });
  it("server tabs never get session views", async () => {
    tab({ profileId: "ms", database: "Sales" });
    useTabs.getState().update("t1", { sessionId: "s1" });
    await flush();
    expect(snapshotCalls()).toEqual([]);
    expect(Object.values(s().entries).some((e) => e.sessionId)).toBe(false);
  });
  it("a reconnected server gives a SQLite tab its session view back, an in-memory tab without a session nothing", async () => {
    tab({ id: "a", profileId: "file", database: "main", sessionId: "s9" });
    tab({ id: "b", profileId: "mem", database: "main" });
    await flush();
    rpc.mockClear();
    useSnapshots.setState({ entries: {} });
    useConnections.setState({ servers: { file: { profileId: "file", status: "connected", databases: [] }, mem: { profileId: "mem", status: "connected", databases: [], memory: true } }, order: ["file", "mem"] });
    await flush();
    expect(snapshotCalls()).toEqual([{ profileId: "file", database: "main", sessionId: "s9" }]);
  });
});

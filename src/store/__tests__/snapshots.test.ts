import { describe, it, expect, vi, beforeEach } from "vitest";
import type { MetaUpdatedEvent, SchemaSnapshot, SnapObject } from "../../ipc/contract";
import { installSnapshotListeners, RETRY_MS, snapKey, useSnapshots } from "../snapshots";
import { useConnections } from "../connections";
import { useTabs } from "../tabs";

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

const obj = (id: number, schema: string, name: string): SnapObject => ({ id, schema, name, kind: "table" });
const snapshot = (objects: SnapObject[], o: Partial<SchemaSnapshot> = {}): SchemaSnapshot => ({
  profileId: "p1", database: "Sales", status: "fresh", objects, fks: [], ...o,
});
const event = (o: Partial<MetaUpdatedEvent>): MetaUpdatedEvent => ({
  profileId: "p1", database: "Sales", status: "fresh", changed: 0, full: false, elapsedMs: 1, ...o,
});
const s = () => useSnapshots.getState();
const flush = () => new Promise((r) => setTimeout(r, 0));

/** A promise the test resolves by hand. */
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

describe("snapshots store", () => {
  beforeEach(() => {
    useSnapshots.setState({ entries: {} });
    rpc.mockReset();
  });

  it("keys are per profile and the database's exact name (D0e identity: PostgreSQL's \"Sales\" and \"sales\" are two databases)", () => {
    expect(snapKey("P1", "SalesDB")).toBe("P1|SalesDB");
    expect(snapKey("p1", "SALESDB")).not.toBe(snapKey("p1", "salesdb"));
  });
  it("two databases whose names differ only in case keep two entries; neither is read for the other", async () => {
    rpc.mockImplementation(async (_m: string, a: { profileId: string; database: string }) => snapshot([obj(a.database === "Sales" ? 1 : 2, "public", "t")], { database: a.database }));
    await s().load("p1", "Sales");
    await s().load("p1", "sales");
    expect(s().get("p1", "Sales")?.snapshot?.objects[0].id).toBe(1);
    expect(s().get("p1", "sales")?.snapshot?.objects[0].id).toBe(2);
    expect(s().get("p1", "SALES")).toBeUndefined();
    expect(Object.keys(s().entries).sort()).toEqual(["p1|Sales", "p1|sales"]);
  });
  it("load caches the snapshot with a built index under the exact database name", async () => {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "Orders")]));
    await s().load("p1", "Sales");
    const e = s().get("p1", "Sales")!;
    expect(rpc).toHaveBeenCalledWith("meta.snapshot", { profileId: "p1", database: "Sales" });
    expect(e.status).toBe("fresh");
    expect(e.index?.byName.get("orders")?.[0].id).toBe(1);
    expect(e.version).toBe(2); // loading, then loaded
    expect(s().get(undefined, "Sales")).toBeUndefined();
    expect(s().get("p1", "")).toBeUndefined();
  });
  it("shows loading first, and keeps the previous snapshot visible while reloading", async () => {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "A")]));
    await s().load("p1", "Sales");
    const d = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(d.promise);
    const p = s().load("p1", "Sales");
    expect(s().get("p1", "Sales")).toMatchObject({ status: "fresh", version: 3 });
    expect(s().get("p1", "Sales")?.snapshot?.objects[0].name).toBe("A");
    d.resolve(snapshot([obj(2, "dbo", "B")]));
    await p;
    expect(s().get("p1", "Sales")?.snapshot?.objects[0].name).toBe("B");
  });
  it("a failed load records the error", async () => {
    rpc.mockRejectedValueOnce(new Error("login failed"));
    await s().load("p1", "Sales");
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", error: "login failed" });
    expect(s().get("p1", "Sales")?.snapshot).toBeUndefined();
  });
  it("a failed reload keeps the previously cached snapshot and index, so completion keeps its tables", async () => {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "A")]));
    await s().load("p1", "Sales");
    rpc.mockRejectedValueOnce(new Error("network"));
    await s().load("p1", "Sales");
    const e = s().get("p1", "Sales")!;
    expect(e).toMatchObject({ status: "error", error: "network" });
    expect(e.snapshot?.objects[0].name).toBe("A");
    expect(e.index?.byName.get("a")?.[0].id).toBe(1);
  });
  it("ensure retries a failed load that cached nothing, at most once per RETRY_MS", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    try {
      rpc.mockRejectedValueOnce(new Error("timeout"));
      await s().load("p1", "Sales");
      expect(rpc).toHaveBeenCalledTimes(1);
      s().ensure("p1", "Sales"); // too soon
      expect(rpc).toHaveBeenCalledTimes(1);
      vi.setSystemTime(Date.now() + RETRY_MS);
      rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "A")]));
      s().ensure("p1", "Sales");
      await flush();
      expect(rpc).toHaveBeenCalledTimes(2);
      expect(s().get("p1", "Sales")?.index?.byName.get("a")?.[0].id).toBe(1);
    } finally {
      vi.useRealTimers();
    }
  });
  it("ensure does not retry a failed reload that still has a cached schema", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    try {
      rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "A")]));
      await s().load("p1", "Sales");
      rpc.mockRejectedValueOnce(new Error("network"));
      await s().load("p1", "Sales");
      vi.setSystemTime(Date.now() + RETRY_MS);
      s().ensure("p1", "Sales");
      expect(rpc).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });
  it("a failed load bumps the version past the loading entry", async () => {
    rpc.mockRejectedValueOnce("boom");
    const p = s().load("p1", "Sales");
    const loadingVersion = s().get("p1", "Sales")!.version;
    await p;
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", error: "boom", version: loadingVersion + 1 });
  });
  it("a snapshot that arrives after forget (disconnect) is discarded", async () => {
    const d = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(d.promise);
    const p = s().load("p1", "Sales");
    s().forget("p1");
    d.resolve(snapshot([obj(1, "dbo", "A")]));
    await p;
    expect(s().entries).toEqual({});
  });
  it("ensure loads a database only once", async () => {
    rpc.mockResolvedValue(snapshot([]));
    s().ensure("p1", "Sales");
    s().ensure("p1", "Sales");
    await flush();
    expect(rpc).toHaveBeenCalledTimes(1);
  });
  it("forget drops only that profile's databases", async () => {
    rpc.mockImplementation(async (_m: string, a: { profileId: string; database: string }) => snapshot([], { profileId: a.profileId, database: a.database }));
    await s().load("p1", "A");
    await s().load("p1", "B");
    await s().load("p2", "A");
    s().forget("p1");
    expect(Object.keys(s().entries)).toEqual([snapKey("p2", "A")]);
  });
});

describe("snapshots: meta.updated", () => {
  let off: () => void;
  const emit = async (e: MetaUpdatedEvent) => {
    notify.get("meta.updated")!(e);
    await flush();
  };
  beforeEach(async () => {
    off?.();
    useSnapshots.setState({ entries: {} });
    rpc.mockReset();
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "Orders"), obj(2, "dbo", "Old"), obj(3, "audit", "Log")]));
    await s().load("p1", "Sales");
    rpc.mockReset();
  });

  it("events for databases that are not cached are ignored", async () => {
    await emit(event({ database: "Other", changed: 1, changedIds: [1] }));
    expect(rpc).not.toHaveBeenCalled();
  });
  it("refreshing/error only change the status", async () => {
    const v = s().get("p1", "Sales")!.version;
    await emit(event({ status: "error", error: "timeout" }));
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", error: "timeout", version: v + 1 });
    expect(s().get("p1", "Sales")?.snapshot?.objects).toHaveLength(3);
  });
  it("patches only changed and removed objects, re-sorted and re-indexed", async () => {
    rpc.mockResolvedValueOnce([obj(1, "dbo", "Orders2"), obj(4, "dbo", "Brand")]);
    await emit(event({ changed: 2, changedIds: [1, 4], removedIds: [2] }));
    expect(rpc).toHaveBeenCalledWith("meta.objects", { profileId: "p1", database: "Sales", ids: [1, 4] });
    const e = s().get("p1", "Sales")!;
    expect(e.snapshot?.objects.map((o) => `${o.schema}.${o.name}`)).toEqual(["audit.Log", "dbo.Brand", "dbo.Orders2"]);
    expect(e.index?.byName.has("orders")).toBe(false);
    expect(e.index?.byName.has("orders2")).toBe(true);
    expect(e.status).toBe("fresh");
  });
  it("nothing changed: marks fresh without fetching", async () => {
    await emit(event({ changed: 0, changedIds: [] }));
    expect(rpc).not.toHaveBeenCalled();
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
  });
  it("full refreshes, missing id lists and large change sets reload the whole snapshot", async () => {
    rpc.mockResolvedValue(snapshot([]));
    await emit(event({ full: true, changed: 1, changedIds: [1] }));
    await emit(event({ changed: 1, changedIds: null }));
    await emit(event({ changed: 301, changedIds: Array.from({ length: 301 }, (_, i) => i) }));
    expect(rpc.mock.calls.map((c) => c[0])).toEqual(["meta.snapshot", "meta.snapshot", "meta.snapshot"]);
  });
  it("a patch that lands after forget (disconnect) does not re-create the entry", async () => {
    const d = deferred<SnapObject[]>();
    rpc.mockReturnValueOnce(d.promise);
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [1] }));
    s().forget("p1");
    d.resolve([obj(1, "dbo", "Orders")]);
    await flush();
    expect(s().get("p1", "Sales")).toBeUndefined();
  });
});

describe("snapshots: disconnected servers", () => {
  let off: (() => void) | undefined;
  beforeEach(() => {
    off?.();
    off = undefined;
    useSnapshots.setState({ entries: {} });
    useConnections.setState({ servers: {}, order: [], disconnected: {} });
    useTabs.setState({ tabs: {} });
    rpc.mockReset();
  });

  it("ensure and load do nothing for a server the user disconnected (new query tab after disconnect)", async () => {
    useConnections.setState({ disconnected: { p1: true } });
    s().ensure("p1", "Sales");
    await s().load("p1", "Sales");
    expect(rpc).not.toHaveBeenCalled();
    expect(s().get("p1", "Sales")).toBeUndefined();
  });
  it("a snapshot that arrives after the server was disconnected is discarded", async () => {
    const d = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(d.promise);
    const p = s().load("p1", "Sales");
    useConnections.setState({ disconnected: { p1: true } });
    d.resolve(snapshot([obj(1, "dbo", "Orders")]));
    await p;
    expect(s().get("p1", "Sales")?.snapshot).toBeUndefined();
  });
  it("meta.updated events for a disconnected server are ignored", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "Orders")]));
    await s().load("p1", "Sales");
    rpc.mockReset();
    useConnections.setState({ disconnected: { p1: true } });
    notify.get("meta.updated")!(event({ full: true, changed: 1 }));
    await flush();
    expect(rpc).not.toHaveBeenCalled();
  });
  it("meta.updated for one session's own view leaves the shared snapshot alone (Phase D0d)", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "Orders")]));
    await s().load("p1", "Sales");
    rpc.mockReset();
    notify.get("meta.updated")!(event({ full: true, changed: 1, sessionId: "s9" }));
    await flush();
    expect(rpc).not.toHaveBeenCalled();
    expect(s().get("p1", "Sales")?.snapshot?.objects.map((o) => o.name)).toEqual(["Orders"]);
  });
  it("disconnect forgets the schema; reconnect reloads it for the tabs still bound to the server", async () => {
    off = installSnapshotListeners();
    const conn = { profileId: "p1", sessionId: "s1", status: "connected" as const, databases: [] };
    useConnections.setState({ servers: { p1: conn }, order: ["p1"] });
    rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "Orders")]));
    await s().load("p1", "Sales");
    useTabs.setState({ tabs: { t1: { id: "t1", title: "q", profileId: "p1", database: "Sales" } as never } });

    useConnections.setState({ servers: {}, order: [], disconnected: { p1: true } });
    expect(s().get("p1", "Sales")).toBeUndefined();

    rpc.mockReset();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "dbo", "Orders")]));
    useConnections.setState({ servers: { p1: { ...conn, status: "connecting" } }, order: ["p1"], disconnected: {} });
    expect(rpc).not.toHaveBeenCalled(); // not until it is connected
    useConnections.setState({ servers: { p1: conn } });
    await flush();
    expect(rpc).toHaveBeenCalledWith("meta.snapshot", { profileId: "p1", database: "Sales" });
    expect(s().get("p1", "Sales")?.snapshot?.objects[0].name).toBe("Orders");
  });
});

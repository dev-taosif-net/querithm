// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MetaUpdatedEvent, SchemaSnapshot, SnapObject } from "../../ipc/contract";
import { checkFreshness, freshnessChecksHeld, installSnapshotListeners, MAX_RETRIES, RETRY_MS, useSnapshots } from "../snapshots";
import { useConnections } from "../connections";
import { useTabs } from "../tabs";

// Second review of the completion audit (2026-11-25): how the snapshot store follows meta.updated and failed loads.

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

const obj = (id: number, name: string): SnapObject => ({ id, schema: "dbo", name, kind: "table" });
const snapshot = (objects: SnapObject[], o: Partial<SchemaSnapshot> = {}): SchemaSnapshot => ({
  profileId: "p1", database: "Sales", status: "fresh", objects, fks: [], ...o,
});
const event = (o: Partial<MetaUpdatedEvent>): MetaUpdatedEvent => ({
  profileId: "p1", database: "Sales", status: "fresh", changed: 0, full: false, elapsedMs: 1, ...o,
});
const s = () => useSnapshots.getState();
const flush = () => new Promise((r) => setTimeout(r, 0));
const emit = async (e: MetaUpdatedEvent) => {
  notify.get("meta.updated")!(e);
  await flush();
  await flush();
};
const connect = (id: string) =>
  useConnections.setState((st) => ({ servers: { ...st.servers, [id]: { profileId: id, sessionId: "e1", status: "connected" as const, databases: [] } }, order: [...st.order, id] }));

let off: (() => void) | undefined;
beforeEach(() => {
  off?.();
  off = undefined;
  useSnapshots.setState({ entries: {} });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useTabs.setState({ tabs: {} });
  rpc.mockReset();
});
afterEach(() => {
  off?.();
  off = undefined;
  vi.useRealTimers();
});

describe("a stale meta.updated (DDL in an open transaction, H11) changes the status only", () => {
  it("no full reload of the snapshot, the cached schema kept", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    rpc.mockReset().mockResolvedValue(snapshot([obj(1, "Orders")]));
    await emit(event({ status: "stale" }));
    expect(rpc).not.toHaveBeenCalled();
    expect(s().get("p1", "Sales")).toMatchObject({ status: "stale" });
    expect(s().get("p1", "Sales")?.snapshot?.objects).toHaveLength(1);
  });
  it("a refresh that ran meanwhile and ended stale keeps the entry stale, its changes patched in", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    rpc.mockReset().mockImplementation(async (m: string) => (m === "meta.objects" ? [obj(2, "Lines")] : []));
    await emit(event({ status: "stale", changed: 1, changedIds: [2] }));
    expect(s().get("p1", "Sales")).toMatchObject({ status: "stale" });
    expect(s().get("p1", "Sales")?.snapshot?.objects.map((o) => o.name)).toEqual(["Lines", "Orders"]);
    await emit(event({ status: "stale", changed: 0, changedIds: [] }));
    expect(s().get("p1", "Sales")?.status).toBe("stale");
  });
});

describe("a patch whose reads fail leaves the entry usable (no unhandled rejection, no stuck refreshing)", () => {
  it("the entry goes stale with the error, keeps its schema, and the next refresh runs", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    rpc.mockReset().mockResolvedValue({ jobId: "j" });
    await s().refresh("p1", "Sales");
    expect(s().get("p1", "Sales")?.status).toBe("refreshing");
    rpc.mockReset().mockRejectedValue(new Error("the engine stopped"));
    await emit(event({ changed: 1, changedIds: [1] }));
    expect(s().get("p1", "Sales")).toMatchObject({ status: "stale", error: "the engine stopped" });
    expect(s().get("p1", "Sales")?.snapshot?.objects).toHaveLength(1);
    rpc.mockReset().mockResolvedValue({ jobId: "j" });
    await s().refresh("p1", "Sales");
    expect(rpc).toHaveBeenCalledWith("meta.refresh", { profileId: "p1", database: "Sales", full: false });
  });
});

describe("retry bookkeeping", () => {
  it("a successful patch clears an earlier failure", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    useSnapshots.setState((st) => ({ entries: { ...st.entries, "p1|Sales": { ...st.entries["p1|Sales"], status: "error", error: "x", failedAt: 1, retries: 2 } } }));
    rpc.mockReset().mockImplementation(async (m: string) => (m === "meta.objects" ? [obj(1, "Orders")] : []));
    await emit(event({ changed: 1, changedIds: [1] }));
    expect(s().get("p1", "Sales")).toMatchObject({ status: "fresh", error: null });
    expect(s().get("p1", "Sales")?.failedAt).toBeUndefined();
    expect(s().get("p1", "Sales")?.retries).toBeUndefined();
    useSnapshots.setState((st) => ({ entries: { ...st.entries, "p1|Sales": { ...st.entries["p1|Sales"], status: "error", error: "x", failedAt: 1, retries: 2 } } }));
    await emit(event({ changed: 0, changedIds: [] }));
    expect(s().get("p1", "Sales")).toMatchObject({ status: "fresh", error: null });
    expect(s().get("p1", "Sales")?.failedAt).toBeUndefined();
  });
  it("a retry cycle counts once: the load's error answer, not also the refresh events it starts", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValue(snapshot([], { status: "error", error: "timeout" }));
    await s().load("p1", "Sales");
    expect(s().get("p1", "Sales")?.retries).toBe(0);
    await emit(event({ status: "refreshing" }));
    await emit(event({ status: "error", error: "timeout" }));
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", retries: 0 });
    expect(s().get("p1", "Sales")?.failedAt).toBeDefined();
  });
  it("a refresh that fails with nothing cached keeps the count the loads made", async () => {
    rpc.mockResolvedValueOnce(snapshot([], { status: "error", error: "timeout" }));
    await s().load("p1", "Sales");
    useSnapshots.setState((st) => ({ entries: { ...st.entries, "p1|Sales": { ...st.entries["p1|Sales"], status: "stale", retries: 3 } } }));
    rpc.mockReset().mockRejectedValue(new Error("down"));
    await s().refresh("p1", "Sales");
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", retries: 3 });
    expect(s().get("p1", "Sales")?.failedAt).toBeDefined();
  });
  it("retries stop after MAX_RETRIES, and a load refused for a missing password is not retried", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    rpc.mockRejectedValue(new Error("timeout"));
    await s().load("p1", "Sales");
    for (let i = 0; i < MAX_RETRIES + 3; i++) {
      vi.setSystemTime(Date.now() + 3_600_000);
      s().ensure("p1", "Sales");
      await flush();
    }
    expect(rpc).toHaveBeenCalledTimes(MAX_RETRIES + 1);
    useSnapshots.setState({ entries: {} });
    rpc.mockReset().mockRejectedValue(Object.assign(new Error("The password is not saved."), { appCode: "needsSecret" }));
    await s().load("p1", "Sales");
    vi.setSystemTime(Date.now() + 3_600_000);
    s().ensure("p1", "Sales");
    await flush();
    expect(rpc).toHaveBeenCalledTimes(1);
  });
  it("a load the engine restart overtook does not write its failure: the entry it had nothing in is dropped, to load again", async () => {
    let fail!: (e: Error) => void;
    rpc.mockReturnValue(new Promise((_, rej) => { fail = rej; }));
    const loading = s().load("p1", "Sales");
    expect(s().get("p1", "Sales")?.status).toBe("loading");
    s().engineRestarted();
    fail(new Error("the engine stopped"));
    await loading;
    // Before (code audit 2026-11-26, M4): left "stale" with nothing in it, and nothing ever loaded it again.
    expect(s().get("p1", "Sales")).toBeUndefined();
  });
});

describe("the freshness check (M22) waits while it cannot help", () => {
  async function loaded() {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
  }
  it("checks nothing while the window is hidden", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    connect("p1");
    await loaded();
    useTabs.setState({ tabs: { t1: { id: "t1", title: "a", profileId: "p1", database: "Sales", txState: "none" } as never } });
    rpc.mockReset().mockResolvedValue({ jobId: "j" });
    vi.setSystemTime(Date.now() + 3_600_000);
    const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(true);
    try {
      checkFreshness(0);
      expect(rpc).not.toHaveBeenCalled();
    } finally {
      hidden.mockRestore();
    }
    checkFreshness(0);
    expect(rpc).toHaveBeenCalledWith("meta.refresh", { profileId: "p1", database: "Sales", full: false });
  });
  it("skips a database a tab holds an open transaction on (its catalog read would wait on that DDL)", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    connect("p1");
    await loaded();
    useTabs.setState({ tabs: {
      t1: { id: "t1", title: "a", profileId: "p1", database: "Sales", txState: "open" } as never,
      t2: { id: "t2", title: "b", profileId: "p1", database: "Sales", txState: "none" } as never,
    } });
    rpc.mockReset().mockResolvedValue({ jobId: "j" });
    vi.setSystemTime(Date.now() + 3_600_000);
    checkFreshness(0);
    expect(rpc).not.toHaveBeenCalled();
  });
});

describe("what the freshness check remembers goes with its entries", () => {
  it("forget() and a dropped session view take their check times along", async () => {
    rpc.mockResolvedValue(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    await s().load("p1", "Hr");
    const held = freshnessChecksHeld();
    s().forget("p1");
    expect(freshnessChecksHeld()).toBe(held - 2);
  });
  it("RETRY_MS stays the first wait", () => expect(RETRY_MS).toBe(5000));
});

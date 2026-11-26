// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MetaUpdatedEvent, SchemaSnapshot, SnapFk, SnapObject } from "../../ipc/contract";
import {
  checkFreshness, FOCUS_CHECK_MS, FRESHNESS_MS, installSnapshotListeners, MAX_RETRY_MS, RETRY_MS, retryDelay, useSnapshots,
} from "../snapshots";
import { useConnections } from "../connections";
import { useTabs } from "../tabs";

// Completion audit 2026-11-25: FKs follow an incremental refresh (H12), a schema that cannot be read is retried with a
// growing wait and an engine restart never leaves one "refreshing" for good (M21), and open tabs' schemas are checked
// against the server while the app runs (M22).

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
const fk = (name: string, parent: number, referenced: number): SnapFk => ({ name, parent, referenced, columns: [{ parent: "a", referenced: "b" }] });
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
};

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

describe("an incremental refresh patches foreign keys too (H12)", () => {
  it("replaces a changed table's FKs, drops a removed table's and those pointing at it, keeps the rest", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders"), obj(2, "Old"), obj(3, "Lines"), obj(4, "Customers")], {
      fks: [fk("FK_Orders_Old", 1, 2), fk("FK_Lines_Orders", 3, 1), fk("FK_Old_Customers", 2, 4), fk("FK_Lines_Old", 3, 2)],
    }));
    await s().load("p1", "Sales");
    rpc.mockReset().mockImplementation(async (method: string) =>
      method === "meta.objects" ? [obj(1, "Orders")] : method === "meta.foreignKeys" ? [fk("FK_Orders_Customers", 1, 4)] : undefined);
    await emit(event({ changed: 2, changedIds: [1], removedIds: [2] }));
    expect(rpc).toHaveBeenCalledWith("meta.foreignKeys", { profileId: "p1", database: "Sales", ids: [1] });
    const fks = s().get("p1", "Sales")!.snapshot!.fks.map((f) => f.name).sort();
    expect(fks).toEqual(["FK_Lines_Orders", "FK_Orders_Customers"]);
    // The JOIN suggestions read the patched list: the new FK joins Orders to Customers.
    expect(s().get("p1", "Sales")!.index!.fks.map((f) => f.name).sort()).toEqual(["FK_Lines_Orders", "FK_Orders_Customers"]);
  });
});

describe("a schema that cannot be read is retried (M21)", () => {
  it("an engine answer of error with no objects is retried, waiting twice as long each time", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    rpc.mockResolvedValue(snapshot([], { status: "error", error: "login failed" }));
    await s().load("p1", "Sales");
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", retries: 0 });
    s().ensure("p1", "Sales");
    expect(rpc).toHaveBeenCalledTimes(1); // too soon
    vi.setSystemTime(Date.now() + RETRY_MS);
    s().ensure("p1", "Sales");
    await flush();
    expect(rpc).toHaveBeenCalledTimes(2);
    expect(s().get("p1", "Sales")?.retries).toBe(1);
    vi.setSystemTime(Date.now() + RETRY_MS); // the second wait is 2 × RETRY_MS
    s().ensure("p1", "Sales");
    expect(rpc).toHaveBeenCalledTimes(2);
    vi.setSystemTime(Date.now() + RETRY_MS);
    s().ensure("p1", "Sales");
    await flush();
    expect(rpc).toHaveBeenCalledTimes(3);
    // Read at last: no more retries.
    rpc.mockResolvedValue(snapshot([obj(1, "Orders")]));
    vi.setSystemTime(Date.now() + MAX_RETRY_MS);
    s().ensure("p1", "Sales");
    await flush();
    vi.setSystemTime(Date.now() + MAX_RETRY_MS);
    s().ensure("p1", "Sales");
    expect(rpc).toHaveBeenCalledTimes(4);
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
  });
  it("the wait doubles up to MAX_RETRY_MS", () => {
    expect([0, 1, 2, 3, 4, 20].map(retryDelay)).toEqual([RETRY_MS, 2 * RETRY_MS, 4 * RETRY_MS, 8 * RETRY_MS, Math.min(MAX_RETRY_MS, 16 * RETRY_MS), MAX_RETRY_MS]);
  });
  it("a failed refresh of a database with nothing cached is retried by ensure", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([], { status: "refreshing" }));
    await s().load("p1", "Sales");
    await emit(event({ status: "error", error: "timeout" }));
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", error: "timeout" });
    rpc.mockReset().mockResolvedValue(snapshot([obj(1, "Orders")]));
    vi.setSystemTime(Date.now() + RETRY_MS);
    s().ensure("p1", "Sales");
    await flush();
    expect(rpc).toHaveBeenCalledWith("meta.snapshot", { profileId: "p1", database: "Sales" });
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
  });
  it("an engine restart puts an entry left refreshing back to stale, so a refresh can start again", async () => {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    rpc.mockReset().mockReturnValue(new Promise(() => {})); // the old engine never answers
    void s().refresh("p1", "Sales");
    expect(s().get("p1", "Sales")?.status).toBe("refreshing");
    await s().refresh("p1", "Sales");
    expect(rpc).toHaveBeenCalledTimes(1); // refused while refreshing
    s().engineRestarted();
    expect(s().get("p1", "Sales")).toMatchObject({ status: "stale" });
    expect(s().get("p1", "Sales")?.snapshot?.objects).toHaveLength(1); // the cached schema stays
    rpc.mockReset().mockResolvedValue({ jobId: "j" });
    await s().refresh("p1", "Sales");
    expect(rpc).toHaveBeenCalledWith("meta.refresh", { profileId: "p1", database: "Sales", full: false });
  });
});

describe("open tabs' schemas are checked while the app runs (M22)", () => {
  const connect = (id: string) =>
    useConnections.setState((st) => ({ servers: { ...st.servers, [id]: { profileId: id, sessionId: "e1", status: "connected" as const, databases: [] } }, order: [...st.order, id] }));

  async function loaded(profileId: string, database: string) {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")], { profileId, database }));
    await s().load(profileId, database);
  }

  it("refreshes each database a tab reads once FRESHNESS_MS passed, on connected servers only, never twice", async () => {
    vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
    connect("p1");
    await loaded("p1", "Sales");
    await loaded("p2", "Hr"); // its server is not connected
    await loaded("p1", "Unused"); // no tab reads it
    useTabs.setState({ tabs: {
      t1: { id: "t1", title: "a", profileId: "p1", database: "Sales" } as never,
      t2: { id: "t2", title: "b", profileId: "p1", database: "Sales" } as never,
      t3: { id: "t3", title: "c", profileId: "p2", database: "Hr" } as never,
    } });
    off = installSnapshotListeners();
    rpc.mockReset().mockResolvedValue({ jobId: "j" });
    checkFreshness(FRESHNESS_MS);
    expect(rpc).not.toHaveBeenCalled(); // just read

    vi.advanceTimersByTime(FRESHNESS_MS);
    await flush();
    expect(rpc.mock.calls).toEqual([["meta.refresh", { profileId: "p1", database: "Sales", full: false }]]);
    // The refresh's answer arrives; a focus right after checks nothing again.
    await emit(event({ changed: 0, changedIds: [] }));
    rpc.mockClear();
    window.dispatchEvent(new Event("focus"));
    expect(rpc).not.toHaveBeenCalled();
  });
  it("the window getting the focus back checks a database last read FOCUS_CHECK_MS ago", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    connect("p1");
    await loaded("p1", "Sales");
    useTabs.setState({ tabs: { t1: { id: "t1", title: "a", profileId: "p1", database: "Sales" } as never } });
    off = installSnapshotListeners();
    rpc.mockReset().mockResolvedValue({ jobId: "j" });
    window.dispatchEvent(new Event("focus"));
    expect(rpc).not.toHaveBeenCalled();
    vi.setSystemTime(Date.now() + FOCUS_CHECK_MS);
    window.dispatchEvent(new Event("focus"));
    expect(rpc).toHaveBeenCalledWith("meta.refresh", { profileId: "p1", database: "Sales", full: false });
  });
});

// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MetaUpdatedEvent, SchemaSnapshot, SnapObject } from "../../ipc/contract";
import { installSnapshotListeners, loadSeqsHeld, MAX_RETRIES, RETRY_MS, UPDATE_WAIT_MS, useSnapshots } from "../snapshots";
import { useProfiles } from "../profiles";
import { useConnections } from "../connections";
import { useTabs } from "../tabs";

// Code audit 2026-11-26 (snapshots): an engine restart during a first load, a retried empty "error" snapshot, and answers
// that come back out of order (two loads, a load and a patch, two patches, a refresh across a restart).

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
const flush = async () => { for (let i = 0; i < 6; i++) await new Promise((r) => setTimeout(r, 0)); };
const names = () => s().get("p1", "Sales")?.snapshot?.objects.map((o) => o.name);

/** A promise and the functions that settle it. */
function deferred<T>() {
  let resolve!: (v: T) => void, reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

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

describe("an engine restart during a load that has nothing cached", () => {
  it("drops the entry, so the next ensure() loads it again (it was left stale and empty for good)", async () => {
    const first = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(first.promise);
    const loading = s().load("p1", "Sales");
    s().engineRestarted();
    first.reject(new Error("the engine stopped"));
    await loading;
    expect(s().get("p1", "Sales")).toBeUndefined();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    s().ensure("p1", "Sales");
    await flush();
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
    expect(names()).toEqual(["Orders"]);
  });
  it("a reload of a cached schema keeps it", async () => {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    const reload = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(reload.promise);
    const loading = s().load("p1", "Sales");
    s().engineRestarted();
    reload.reject(new Error("the engine stopped"));
    await loading;
    expect(s().get("p1", "Sales")).toMatchObject({ status: "fresh" });
    expect(names()).toEqual(["Orders"]);
  });
});

describe("retrying an empty \"error\" snapshot", () => {
  it("shows loading while the retry runs, and a restart meanwhile leaves it retriable", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    rpc.mockResolvedValueOnce(snapshot([], { status: "error", error: "timeout" }));
    await s().load("p1", "Sales");
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", retries: 0 });
    vi.setSystemTime(Date.now() + RETRY_MS);
    const retry = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(retry.promise);
    s().ensure("p1", "Sales");
    expect(s().get("p1", "Sales")?.status).toBe("loading");
    s().engineRestarted();
    retry.reject(new Error("the engine stopped"));
    await flush();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    s().ensure("p1", "Sales");
    await flush();
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
  });
  it("a retry that fails again counts on from the last count", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    rpc.mockResolvedValueOnce(snapshot([], { status: "error", error: "timeout" }));
    await s().load("p1", "Sales");
    vi.setSystemTime(Date.now() + RETRY_MS);
    rpc.mockResolvedValueOnce(snapshot([], { status: "error", error: "timeout" }));
    await s().load("p1", "Sales");
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", retries: 1 });
  });
});

describe("answers that come back out of order", () => {
  it("an older load that answers last does not overwrite a newer one", async () => {
    const older = deferred<SchemaSnapshot>(), newer = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise);
    const a = s().load("p1", "Sales");
    const b = s().load("p1", "Sales");
    newer.resolve(snapshot([obj(1, "Orders"), obj(2, "Lines")]));
    await b;
    older.resolve(snapshot([obj(1, "Orders")]));
    await a;
    expect(names()).toEqual(["Orders", "Lines"]);
  });
  it("an older load that fails last does not turn a newer one into an error", async () => {
    const older = deferred<SchemaSnapshot>(), newer = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise);
    const a = s().load("p1", "Sales");
    const b = s().load("p1", "Sales");
    newer.resolve(snapshot([obj(1, "Orders")]));
    await b;
    older.reject(new Error("timeout"));
    await a;
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
    expect(s().get("p1", "Sales")?.error).toBeFalsy();
  });
  it("a load answered after a patch landed does not undo the patch", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    const reload = deferred<SchemaSnapshot>();
    rpc.mockImplementation((method: string) =>
      method === "meta.snapshot" ? reload.promise : method === "meta.objects" ? Promise.resolve([obj(2, "Lines")]) : Promise.resolve([]));
    const loading = s().load("p1", "Sales");
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    await flush();
    expect(names()).toEqual(["Lines", "Orders"]);
    reload.resolve(snapshot([obj(1, "Orders")])); // read before the refresh the patch came from
    await loading;
    expect(names()).toEqual(["Lines", "Orders"]);
  });
  it("two patches of the same database apply in the order they came", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders"), obj(2, "Lines")]));
    await s().load("p1", "Sales");
    const slow = deferred<SnapObject[]>();
    let calls = 0;
    rpc.mockImplementation((method: string) => {
      if (method !== "meta.objects") return Promise.resolve([]);
      return ++calls === 1 ? slow.promise : Promise.resolve([obj(2, "LinesV2")]);
    });
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    await flush();
    slow.resolve([obj(2, "LinesV1")]);
    await flush();
    expect(names()).toEqual(["LinesV2", "Orders"]);
  });
  it("a patch whose RPC never answers holds up the next one UPDATE_WAIT_MS at most, which then reads it all again", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders"), obj(2, "Lines")]));
    await s().load("p1", "Sales");
    vi.useFakeTimers();
    let calls = 0;
    rpc.mockImplementation((method: string) => {
      if (method === "meta.snapshot") return Promise.resolve(snapshot([obj(1, "Orders"), obj(2, "LinesV2")]));
      if (method !== "meta.objects") return Promise.resolve([]);
      return ++calls === 1 ? new Promise(() => {}) : Promise.resolve([obj(2, "LinesV1")]);
    });
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    await vi.advanceTimersByTimeAsync(UPDATE_WAIT_MS - 1000);
    expect(names()).toEqual(["Orders", "Lines"]); // still waiting
    await vi.advanceTimersByTimeAsync(2000);
    expect(names()).toEqual(["Orders", "LinesV2"]); // read again (R2), not patched over a patch it could not see
    expect(calls).toBe(1);
  });
  it("a patch that fails after a newer load does not mark the new schema stale", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    const patch = deferred<SnapObject[]>();
    rpc.mockImplementation((method: string) =>
      method === "meta.objects" ? patch.promise : method === "meta.snapshot" ? Promise.resolve(snapshot([obj(1, "Orders"), obj(2, "Lines")])) : Promise.resolve([]));
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    await flush();
    await s().load("p1", "Sales");
    patch.reject(new Error("the engine stopped"));
    await flush();
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
    expect(s().get("p1", "Sales")?.error).toBeFalsy();
    expect(names()).toEqual(["Orders", "Lines"]);
  });
  it("a refresh the old engine fails after a restart does not fail the refresh the new one runs", async () => {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    const old = deferred<unknown>();
    rpc.mockReturnValueOnce(old.promise);
    void s().refresh("p1", "Sales");
    s().engineRestarted();
    rpc.mockReturnValueOnce(new Promise(() => {}));
    void s().refresh("p1", "Sales");
    old.reject(new Error("the engine stopped"));
    await flush();
    expect(s().get("p1", "Sales")?.status).toBe("refreshing");
    expect(s().get("p1", "Sales")?.error).toBeFalsy();
  });
});

describe("a \"refreshing\" meta.updated", () => {
  it("clears the last failure's message instead of bringing it back", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    notify.get("meta.updated")!(event({ status: "error", error: "timeout" }));
    await flush();
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", error: "timeout" });
    notify.get("meta.updated")!(event({ status: "refreshing", error: null }));
    await flush();
    expect(s().get("p1", "Sales")).toMatchObject({ status: "refreshing" });
    expect(s().get("p1", "Sales")?.error).toBeFalsy();
  });
});

describe("second round (R2, R3)", () => {
  it("R2: a patch that gave up waiting on a slow full reload reloads too, instead of patching the old schema", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    vi.useFakeTimers();
    const slow = deferred<SchemaSnapshot>();
    let snaps = 0;
    rpc.mockImplementation((method: string) => {
      if (method === "meta.snapshot") return ++snaps === 1 ? slow.promise : Promise.resolve(snapshot([obj(1, "Orders"), obj(2, "Lines"), obj(3, "Items")]));
      return Promise.resolve(method === "meta.objects" ? [obj(3, "Items")] : []);
    });
    notify.get("meta.updated")!(event({ full: true, changed: 1 }));
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [3] }));
    await vi.advanceTimersByTimeAsync(UPDATE_WAIT_MS + 1000);
    slow.resolve(snapshot([obj(1, "Orders"), obj(2, "Lines")])); // the full reload's late answer
    await vi.advanceTimersByTimeAsync(10);
    expect(rpc.mock.calls.filter((c) => c[0] === "meta.objects")).toEqual([]);
    expect(names()).toEqual(["Orders", "Lines", "Items"]);
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
  });
  it("R3: forget(), a restart and a dropped session view take the load sequence along", async () => {
    rpc.mockResolvedValue(snapshot([obj(1, "Orders"), obj(2, "Lines")]));
    await s().load("p1", "Sales");
    s().forget("p1"); // what earlier tests left
    const held = loadSeqsHeld();
    await s().load("p1", "Sales");
    await s().load("p1", "Hr");
    expect(loadSeqsHeld()).toBe(held + 2);
    s().forget("p1");
    expect(loadSeqsHeld()).toBe(held);
    rpc.mockReset().mockReturnValue(new Promise(() => {}));
    void s().load("p1", "Sales");
    s().engineRestarted();
    expect(loadSeqsHeld()).toBe(held);
    off = installSnapshotListeners();
    useProfiles.setState({ profiles: [{ id: "lite", name: "App", engine: "sqlite", location: "file", file: { grantId: "g" }, environment: "dev" } as never] });
    rpc.mockReset().mockResolvedValue(snapshot([obj(1, "Orders")], { profileId: "lite", database: "main" }));
    useTabs.setState({ tabs: { t1: { id: "t1", title: "q", profileId: "lite", database: "main", sessionId: "s1" } as never } });
    await flush();
    expect(s().get("lite", "main", "s1")).toBeDefined();
    expect(loadSeqsHeld()).toBe(held + 1);
    useTabs.setState({ tabs: {} });
    expect(s().get("lite", "main", "s1")).toBeUndefined();
    expect(loadSeqsHeld()).toBe(held);
    useProfiles.setState({ profiles: [] });
  });
  it("R3: still, a load forgotten while in flight never writes over a load started after", async () => {
    const old = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(old.promise).mockReturnValueOnce(new Promise(() => {}));
    void s().load("p1", "Sales");
    s().forget("p1");
    void s().load("p1", "Sales");
    old.resolve(snapshot([obj(1, "Orders")]));
    await flush();
    expect(s().get("p1", "Sales")?.status).toBe("loading");
  });
});

describe("third round (R3-2, R3-3)", () => {
  it("R3-2: a status event that gave up waiting reloads, so a later patch is not built on a patch still hung", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders"), obj(2, "Lines"), obj(3, "Items")]));
    await s().load("p1", "Sales");
    vi.useFakeTimers();
    let objects = 0;
    rpc.mockImplementation((method: string) => {
      if (method === "meta.snapshot") return Promise.resolve(snapshot([obj(1, "Orders"), obj(2, "LinesNew"), obj(3, "Items")]));
      if (method !== "meta.objects") return Promise.resolve([]);
      return ++objects === 1 ? new Promise(() => {}) : Promise.resolve([obj(3, "ItemsV2")]);
    });
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] })); // its objects never come
    notify.get("meta.updated")!(event({ status: "refreshing" }));
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [3] }));
    await vi.advanceTimersByTimeAsync(UPDATE_WAIT_MS + 1000);
    // Read again: what the hung patch would bring is there, and the patch after it is applied on that.
    expect(rpc.mock.calls.filter((c) => c[0] === "meta.snapshot").length).toBeGreaterThanOrEqual(1);
    expect(names()).toEqual(["ItemsV2", "LinesNew", "Orders"]);
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
  });
  it("R3-3: a patch the old engine fails after a restart does not mark the schema stale", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    const patch = deferred<SnapObject[]>();
    rpc.mockImplementation((method: string) => (method === "meta.objects" ? patch.promise : Promise.resolve([])));
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    await flush();
    s().engineRestarted();
    patch.reject(new Error("the engine stopped"));
    await flush();
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
    expect(s().get("p1", "Sales")?.error).toBeFalsy();
  });
  it("R3-3: a patch the old engine answers after a restart is not applied", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    const patch = deferred<SnapObject[]>();
    rpc.mockImplementation((method: string) => (method === "meta.objects" ? patch.promise : Promise.resolve([])));
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    await flush();
    s().engineRestarted();
    patch.resolve([obj(2, "Old")]);
    await flush();
    expect(names()).toEqual(["Orders"]);
  });
});

describe("fourth round (H9, M18, M19, L9)", () => {
  it("H9: a load in flight when a refresh reports nothing changed does not undo its fresh status", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")], { status: "stale" }));
    await s().load("p1", "Sales");
    const reload = deferred<SchemaSnapshot>();
    rpc.mockReturnValueOnce(reload.promise);
    const loading = s().load("p1", "Sales");
    notify.get("meta.updated")!(event({ status: "fresh", changed: 0, changedIds: [] }));
    await flush();
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
    reload.resolve(snapshot([obj(1, "Orders")], { status: "stale" })); // read before that refresh ended
    await loading;
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
  });
  it("M18: a refresh does not carry the last failure's time, and one refused for a missing password is not retried", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    rpc.mockRejectedValueOnce(new Error("timeout"));
    await s().load("p1", "Sales");
    expect(s().get("p1", "Sales")?.failedAt).toBeDefined();
    const refresh = deferred<unknown>();
    rpc.mockReturnValueOnce(refresh.promise);
    void s().refresh("p1", "Sales");
    expect(s().get("p1", "Sales")).toMatchObject({ status: "refreshing" });
    expect(s().get("p1", "Sales")?.failedAt).toBeUndefined();
    refresh.reject(Object.assign(new Error("The password is not saved."), { appCode: "needsSecret" }));
    await flush();
    expect(s().get("p1", "Sales")).toMatchObject({ status: "error", retries: MAX_RETRIES });
    vi.setSystemTime(Date.now() + 3_600_000);
    rpc.mockClear();
    s().ensure("p1", "Sales");
    expect(rpc).not.toHaveBeenCalled();
  });
  it("M19: forget() drops a patch still hung, so the next patch after a reconnect does not wait for it", async () => {
    off = installSnapshotListeners();
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    vi.useFakeTimers();
    let objects = 0;
    rpc.mockImplementation((method: string) => {
      if (method === "meta.snapshot") return Promise.resolve(snapshot([obj(1, "Orders")]));
      if (method !== "meta.objects") return Promise.resolve([]);
      return ++objects === 1 ? new Promise(() => {}) : Promise.resolve([obj(2, "Lines")]);
    });
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    await vi.advanceTimersByTimeAsync(10);
    s().forget("p1");
    await s().load("p1", "Sales");
    notify.get("meta.updated")!(event({ changed: 1, changedIds: [2] }));
    await vi.advanceTimersByTimeAsync(100);
    expect(names()).toEqual(["Lines", "Orders"]);
  });
  it("M19: a refresh forgotten while in flight does not fail the refresh started after it", async () => {
    rpc.mockResolvedValueOnce(snapshot([obj(1, "Orders")]));
    await s().load("p1", "Sales");
    const old = deferred<unknown>();
    rpc.mockReturnValueOnce(old.promise).mockReturnValueOnce(new Promise(() => {}));
    void s().refresh("p1", "Sales");
    s().forget("p1");
    void s().refresh("p1", "Sales");
    old.reject(new Error("the connection closed"));
    await flush();
    expect(s().get("p1", "Sales")?.status).toBe("refreshing");
    expect(s().get("p1", "Sales")?.error).toBeFalsy();
  });
  it("L9: an engine restart drops an entry whose engine had not read it yet (empty), so it is loaded again", async () => {
    rpc.mockResolvedValueOnce(snapshot([], { status: "empty" }));
    await s().load("p1", "Sales");
    expect(s().get("p1", "Sales")?.status).toBe("empty");
    s().engineRestarted();
    expect(s().get("p1", "Sales")).toBeUndefined();
  });
  it("L9: still, a database that is really empty, and a failed load waiting for its retry, stay", async () => {
    rpc.mockResolvedValueOnce(snapshot([])).mockRejectedValueOnce(new Error("timeout"));
    await s().load("p1", "Sales");
    await s().load("p1", "Hr");
    s().engineRestarted();
    expect(s().get("p1", "Sales")?.status).toBe("fresh");
    expect(s().get("p1", "Hr")?.status).toBe("error");
  });
});

// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { LinkedCatalogInfo, LinkedServerInfo, LinkedTablesResult, LinkedTestResult } from "../../ipc/contract";

const rpcMock = vi.hoisted(() => vi.fn());
vi.mock("../../ipc/client", () => ({ rpc: rpcMock, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));

import { useConnections } from "../connections";
import { linkedCatalogKey, linkedTableKey, useLinkedServers } from "../linkedServers";

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe("useLinkedServers store", () => {
  const sessionFor = vi.fn(async (profileId: string) => useConnections.getState().servers[profileId]?.sessionId ?? "sid-1");

  beforeEach(() => {
    rpcMock.mockReset();
    sessionFor.mockClear();
    useLinkedServers.setState({ servers: {}, catalogs: {}, tables: {} });
    useConnections.setState({
      servers: {
        p1: { profileId: "p1", status: "connected", sessionId: "sid-1", databases: [] },
        p2: { profileId: "p2", status: "connected", sessionId: "sid-2", databases: [] },
      },
      order: ["p1", "p2"],
      disconnected: {},
      sessionFor,
    } as never);
  });

  it("caches servers, catalogs and tables lazily and skips redundant calls unless forced", async () => {
    const srv: LinkedServerInfo = { name: "ERP", canBrowse: true, canTest: true };
    const cat: LinkedCatalogInfo = { name: "ErpDb" };
    const tbls: LinkedTablesResult = { tables: [{ schema: "dbo", name: "Orders", kind: "table" }], truncated: false };
    rpcMock.mockImplementation(async (method: string) => {
      if (method === "meta.linkedServers") return { servers: [srv], note: null };
      if (method === "meta.linkedCatalogs") return [cat];
      if (method === "meta.linkedTables") return tbls;
      throw new Error(`unexpected ${method}`);
    });

    await useLinkedServers.getState().loadServers("p1");
    await useLinkedServers.getState().loadServers("p1");
    expect(rpcMock).toHaveBeenCalledTimes(1);
    expect(useLinkedServers.getState().servers.p1).toMatchObject({ status: "ok", items: [srv] });

    await useLinkedServers.getState().loadCatalogs("p1", "ERP");
    await useLinkedServers.getState().loadCatalogs("p1", "ERP");
    expect(rpcMock).toHaveBeenCalledTimes(2);
    expect(useLinkedServers.getState().catalogs[linkedCatalogKey("p1", "ERP")]).toMatchObject({ status: "ok", items: [cat] });

    await useLinkedServers.getState().loadTables("p1", "ERP", "ErpDb");
    await useLinkedServers.getState().loadTables("p1", "ERP", "ErpDb");
    expect(rpcMock).toHaveBeenCalledTimes(3);
    expect(useLinkedServers.getState().tables[linkedTableKey("p1", "ERP", "ErpDb")]).toMatchObject({ status: "ok", items: tbls.tables });

    // Force refresh issues a new RPC.
    await useLinkedServers.getState().loadServers("p1", true);
    expect(rpcMock).toHaveBeenCalledTimes(4);
  });

  it("records errors and recovers cleanly when retried with force=true", async () => {
    rpcMock.mockRejectedValueOnce(new Error("Remote server offline."));
    await useLinkedServers.getState().loadCatalogs("p1", "ERP");
    expect(useLinkedServers.getState().catalogs[linkedCatalogKey("p1", "ERP")]).toEqual({
      status: "error", items: [], error: "Remote server offline.",
    });

    rpcMock.mockResolvedValueOnce([{ name: "Sales" }]);
    await useLinkedServers.getState().loadCatalogs("p1", "ERP", true);
    expect(useLinkedServers.getState().catalogs[linkedCatalogKey("p1", "ERP")]).toEqual({
      status: "ok", items: [{ name: "Sales" }],
    });
  });

  it("drops stale out-of-order responses even across forget() + immediate refresh on the same sessionId", async () => {
    const first = deferred<{ servers: LinkedServerInfo[]; note?: string | null }>();
    const second = deferred<{ servers: LinkedServerInfo[]; note?: string | null }>();
    rpcMock.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    // Request 1 starts on sid-1.
    const p1 = useLinkedServers.getState().loadServers("p1");
    // refreshServer calls forget() then immediately starts Request 2 on the SAME sid-1.
    useLinkedServers.getState().forget("p1");
    const p2 = useLinkedServers.getState().loadServers("p1", true);

    // Request 2 completes first with fresh data; stale Request 1 completes later.
    second.resolve({ servers: [{ name: "FRESH_SRV", canBrowse: true, canTest: true }] });
    await p2;
    first.resolve({ servers: [{ name: "STALE_SRV", canBrowse: true, canTest: true }] });
    await p1;

    expect(useLinkedServers.getState().servers.p1?.items.map((x) => x.name)).toEqual(["FRESH_SRV"]);
  });

  it("forget(profileId) cleans up only that profile and ignores in-flight updates after disconnect", async () => {
    const inflight = deferred<LinkedCatalogInfo[]>();
    rpcMock.mockImplementation((method: string) => {
      if (method === "meta.linkedServers") return Promise.resolve({ servers: [{ name: "S", canBrowse: true, canTest: true }] });
      if (method === "meta.linkedCatalogs") return inflight.promise;
      return Promise.resolve({ tables: [], truncated: false });
    });

    await useLinkedServers.getState().loadServers("p1");
    await useLinkedServers.getState().loadServers("p2");
    const catPromise = useLinkedServers.getState().loadCatalogs("p1", "S");

    // Disconnect p1 while catalogs are in flight.
    useLinkedServers.getState().forget("p1");
    inflight.resolve([{ name: "LateCatalog" }]);
    await catPromise;

    expect(useLinkedServers.getState().servers.p1).toBeUndefined();
    expect(useLinkedServers.getState().catalogs[linkedCatalogKey("p1", "S")]).toBeUndefined();
    // p2 remains untouched.
    expect(useLinkedServers.getState().servers.p2?.status).toBe("ok");
  });

  it("testServer delegates through sessionFor and meta.testLinkedServer", async () => {
    const res: LinkedTestResult = { ok: true, elapsedMs: 14.5 };
    rpcMock.mockResolvedValueOnce(res);
    await expect(useLinkedServers.getState().testServer("p1", "ERP")).resolves.toEqual(res);
    expect(sessionFor).toHaveBeenCalledWith("p1");
    expect(rpcMock).toHaveBeenCalledWith("meta.testLinkedServer", { sessionId: "sid-1", serverName: "ERP" });
  });
});

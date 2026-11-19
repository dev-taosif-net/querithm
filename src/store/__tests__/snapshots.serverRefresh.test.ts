// A server's "Refresh databases": the database list again, then the incremental metadata refresh a database's
// "Refresh metadata" runs, for every online database whose catalog is loaded — on every engine.
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DatabaseInfo, Engine, Profile } from "../../ipc/contract";

const { rpc } = vi.hoisted(() => ({ rpc: vi.fn() }));
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, shell: { secretEndConnection: async () => {} }, RpcError: class extends Error {} }));

import { refreshServer, snapKey, useSnapshots, type SnapEntry } from "../snapshots";
import { useConnections } from "../connections";
import { useProfiles } from "../profiles";

const ENGINES: Engine[] = ["mssql", "pg", "mySql", "oracle", "sqlite", "duckDb"];
const profile = (engine: Engine): Profile =>
  ({ id: "p", name: "p", engine, auth: "none", environment: "dev", tlsMode: "default", trustCert: false, createdAt: 0, updatedAt: 0,
     ...(engine === "sqlite" || engine === "duckDb" ? { location: "file", file: { grantId: "g", displayName: "a.db" } } : { host: "h" }) }) as Profile;
const entry = (database: string, sessionId?: string): SnapEntry =>
  ({ key: snapKey("p", database, sessionId), profileId: "p", database, sessionId, status: "fresh", version: 1 }) as SnapEntry;
const refreshCalls = () => rpc.mock.calls.filter((c) => c[0] === "meta.refresh").map((c) => c[1]);

function setup(engine: Engine, live: DatabaseInfo[]) {
  rpc.mockReset();
  rpc.mockImplementation(async (m: string) => (m === "meta.databases" ? live : m === "meta.refresh" ? { jobId: "j" } : undefined));
  useProfiles.setState({ profiles: [profile(engine)], groups: [] });
  useConnections.setState({
    servers: { p: { profileId: "p", status: "connected", sessionId: "s", databases: [{ name: "A" }, { name: "B" }, { name: "Gone" }] } as never },
    order: ["p"], disconnected: {},
  });
  useSnapshots.setState({ entries: {} });
}

describe.each(ENGINES)("refreshServer (%s)", (engine) => {
  beforeEach(() => setup(engine, [{ name: "A", isSystem: false, state: "ONLINE" }, { name: "B", isSystem: false }, { name: "C", isSystem: false }, { name: "Off", isSystem: false, state: "OFFLINE" }]));

  it("lists the databases, then refreshes the metadata of each loaded online one, incrementally", async () => {
    useSnapshots.setState({ entries: Object.fromEntries([entry("A"), entry("B"), entry("Off"), entry("Gone"), entry("A", "tab1")].map((e) => [e.key, e])) });
    await refreshServer("p");
    expect(useConnections.getState().servers.p.databases.map((d) => d.name)).toEqual(["A", "B", "C", "Off"]);
    // Not C (never loaded: its first load refreshes it), not Off (offline), not Gone (dropped), not a tab's own view.
    expect(refreshCalls()).toEqual([{ profileId: "p", database: "A", full: false }, { profileId: "p", database: "B", full: false }]);
    expect(rpc.mock.calls.findIndex((c) => c[0] === "meta.databases")).toBeLessThan(rpc.mock.calls.findIndex((c) => c[0] === "meta.refresh"));
    expect(useSnapshots.getState().entries[snapKey("p", "A")].status).toBe("refreshing");
  });

  it("a database list that cannot be read refreshes no metadata and reports the error", async () => {
    useSnapshots.setState({ entries: { [snapKey("p", "A")]: entry("A") } });
    rpc.mockImplementation(async (m: string) => { if (m === "meta.databases") throw new Error("Connection lost"); });
    await expect(refreshServer("p")).rejects.toThrow("Connection lost");
    expect(refreshCalls()).toEqual([]);
    expect(useSnapshots.getState().entries[snapKey("p", "A")].status).toBe("fresh");
  });

  it("one database's failed metadata refresh leaves the others refreshing and keeps its cached catalog", async () => {
    useSnapshots.setState({ entries: Object.fromEntries([entry("A"), entry("B")].map((e) => [e.key, e])) });
    rpc.mockImplementation(async (m: string, p: { database?: string }) => {
      if (m === "meta.databases") return [{ name: "A" }, { name: "B" }];
      if (m === "meta.refresh" && p.database === "A") throw new Error("permission denied");
      return { jobId: "j" };
    });
    await refreshServer("p");
    expect(useSnapshots.getState().entries[snapKey("p", "A")]).toMatchObject({ status: "error", error: "permission denied" });
    expect(useSnapshots.getState().entries[snapKey("p", "B")].status).toBe("refreshing");
  });

  it("a server disconnected while the list was read refreshes nothing", async () => {
    useSnapshots.setState({ entries: { [snapKey("p", "A")]: entry("A") } });
    rpc.mockImplementation(async (m: string) => {
      if (m === "meta.databases") { useConnections.setState({ disconnected: { p: true } }); return [{ name: "A" }]; }
      return { jobId: "j" };
    });
    await refreshServer("p");
    expect(refreshCalls()).toEqual([]);
  });
});

it("an in-memory server has no shared databases: nothing is read", async () => {
  setup("sqlite", []);
  useConnections.setState({ servers: { p: { profileId: "p", status: "connected", memory: true, databases: [] } as never } });
  useSnapshots.setState({ entries: { [snapKey("p", "main")]: entry("main") } });
  await refreshServer("p");
  expect(rpc).not.toHaveBeenCalled();
});

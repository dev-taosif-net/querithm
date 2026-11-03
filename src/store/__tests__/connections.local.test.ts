// Databases on this computer (Phase D0c/D1): an in-memory profile opens no Explorer session (each tab session is its own
// database), and a connect refused on the file grant asks the user to pick the file again.
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Profile } from "../../ipc/contract";

const { rpc, RpcError } = vi.hoisted(() => {
  class RpcError extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string) {
      super(message);
    }
  }
  return { rpc: vi.fn(), RpcError };
});
vi.mock("../../ipc/client", () => ({
  rpc,
  onNotify: () => () => {},
  shell: { secretEndConnection: vi.fn(async () => {}), secretExists: async () => true },
  RpcError,
}));

import { MEMORY_NOTE, REPICK_HINT, useConnections } from "../connections";
import { useProfiles } from "../profiles";
import { buildTree } from "../../explorer/tree";

const c = () => useConnections.getState();
const profile = (o: Partial<Profile>): Profile =>
  ({ id: "m1", name: "Scratch", engine: "sqlite", auth: "none", environment: "dev", tlsMode: "default", trustCert: false, createdAt: 0, updatedAt: 0, ...o }) as Profile;

const tree = (p: Profile) =>
  buildTree({
    profiles: [p], servers: c().servers, order: c().order, entries: {}, details: {}, expanded: new Set([`s:${p.id}`]), collapsedEnvs: new Set(),
    pinned: [], kind: "all", folderFilters: {}, schemaGroups: [], groupByEnv: false, showAll: true, active: undefined,
  } as never).rows;

beforeEach(() => {
  rpc.mockReset();
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
});

describe("in-memory profiles", () => {
  beforeEach(() => useProfiles.setState({ profiles: [profile({ location: "memory" })], byId: (id?: string) => (id === "m1" ? profile({ location: "memory" }) : undefined) } as never));

  it("connect marks the server connected without opening a session (one would be a new, empty database)", async () => {
    expect(await c().connect("m1")).toBe(true);
    expect(rpc).not.toHaveBeenCalled();
    expect(c().servers.m1).toMatchObject({ status: "connected", memory: true, databases: [] });
    expect(c().servers.m1.sessionId).toBeUndefined();
  });

  it("the Explorer shows the note instead of databases", async () => {
    await c().connect("m1");
    const rows = tree(profile({ location: "memory" }));
    expect(rows.map((r) => (r.t === "note" ? r.text : r.t))).toEqual(["server", MEMORY_NOTE]);
    expect(rows[0]).toMatchObject({ t: "server", memory: true });
  });

  it("asking for the Explorer's session says why there is none; refreshing databases does nothing", async () => {
    await c().connect("m1");
    await expect(c().sessionFor("m1")).rejects.toThrow(MEMORY_NOTE);
    await c().refreshDatabases("m1");
    expect(rpc).not.toHaveBeenCalled();
  });

  it("a restarted sidecar brings it back without dialing", async () => {
    await c().connect("m1");
    c().resetSessions();
    await new Promise((r) => setTimeout(r, 0));
    expect(c().servers.m1).toMatchObject({ status: "connected", memory: true });
    expect(rpc).not.toHaveBeenCalled();
  });

  it("server and file profiles still dial", async () => {
    useProfiles.setState({ byId: (id?: string) => (id === "f1" ? profile({ id: "f1", location: "file", file: { grantId: "g" } }) : undefined) } as never);
    rpc.mockImplementation(async (method: string) => {
      if (method === "session.open") return { sessionId: "s1" };
      if (method === "session.state") return { serverVersion: "3.46", database: "main" };
      if (method === "meta.databases") return [{ name: "main", isSystem: false }];
      return undefined;
    });
    expect(await c().connect("f1")).toBe(true);
    expect(c().servers.f1).toMatchObject({ status: "connected", sessionId: "s1" });
    expect(c().servers.f1.memory).toBeUndefined();
  });
});

describe("file grant refusals", () => {
  beforeEach(() => useProfiles.setState({ byId: (id?: string) => (id === "f1" ? profile({ id: "f1", location: "file", file: { grantId: "g" } }) : undefined) } as never));

  it.each(["fileMissing", "fileMoved", "fileReplaced", "fileGrantUnknown"])("%s: the error says to pick the file again and the Explorer offers it", async (code) => {
    rpc.mockImplementation(async (method: string) => {
      if (method === "session.open") throw new RpcError("The database file is no longer where it was picked.", -32000, code);
      return undefined;
    });
    expect(await c().connect("f1")).toBe(false);
    expect(c().servers.f1).toMatchObject({ status: "error", repick: true });
    expect(c().servers.f1.error).toContain(REPICK_HINT);
    const rows = tree(profile({ id: "f1", location: "file" }));
    // Picked straight through the shell's dialog (fileAccess.repickFile), not through the profile editor.
    expect(rows.find((r) => r.t === "action" && r.action === "repick")).toMatchObject({ label: "Re-pick file…" });
    expect(rows.some((r) => r.t === "action" && r.action === "editProfile")).toBe(false);
  });

  it("other failures keep the usual Edit profile… action", async () => {
    rpc.mockImplementation(async (method: string) => {
      if (method === "session.open") throw new RpcError("disk I/O error", -32000, "providerError");
      return undefined;
    });
    expect(await c().connect("f1")).toBe(false);
    expect(c().servers.f1.repick).toBeFalsy();
    const rows = tree(profile({ id: "f1", location: "file" }));
    expect(rows.find((r) => r.t === "action" && r.action === "editProfile")).toMatchObject({ label: "Edit profile…" });
  });
});

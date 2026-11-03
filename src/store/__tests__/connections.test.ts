import { describe, it, expect, vi, beforeEach } from "vitest";
import { isDisconnected, useConnections } from "../connections";
import { useProfiles } from "../profiles";

const { rpc } = vi.hoisted(() => ({ rpc: vi.fn() }));
vi.mock("../../ipc/client", () => ({
  rpc,
  onNotify: () => () => {},
  shell: { secretEndConnection: vi.fn(async () => {}), secretExists: async () => true },
  RpcError: class extends Error {},
}));

const c = () => useConnections.getState();
const flush = () => new Promise((r) => setTimeout(r, 0));

function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

/** session.open → s<n>, session.state, meta.databases → [Sales]; capabilities resolve when the test says. */
function server(caps = deferred<unknown>()) {
  let n = 0;
  rpc.mockImplementation(async (method: string) => {
    if (method === "session.open") return { sessionId: `s${++n}`, database: null };
    if (method === "session.state") return { serverVersion: "16", database: "master" };
    if (method === "meta.databases") return [{ name: "Sales", isSystem: false }];
    if (method === "session.capabilities") return caps.promise;
    return undefined;
  });
  return caps;
}

describe("connections: disconnect", () => {
  beforeEach(() => {
    rpc.mockReset();
    useConnections.setState({ servers: {}, order: [], disconnected: {} });
    useProfiles.setState({ byId: (id?: string) => (id === "p1" ? ({ id: "p1", name: "Local", engine: "mssql", auth: "integrated" } as never) : undefined) } as never);
  });

  it("removes the server and its databases, and marks it disconnected", async () => {
    server();
    await c().connect("p1");
    expect(c().servers.p1.databases).toHaveLength(1);
    expect(c().servers.p1.database).toBe("master"); // where the login landed: new tabs on this server start there
    await c().disconnect("p1");
    expect(c().servers.p1).toBeUndefined();
    expect(c().order).toEqual([]);
    expect(isDisconnected("p1")).toBe(true);
    expect(rpc).toHaveBeenCalledWith("session.close", { sessionId: "s1", force: true });
  });
  it("a capabilities reply that arrives after disconnect does not bring the server back", async () => {
    const caps = server();
    await c().connect("p1");
    await c().disconnect("p1");
    caps.resolve({ debugger: true });
    await flush();
    expect(c().servers.p1).toBeUndefined();
    expect(c().order).toEqual([]);
  });
  it("disconnecting while a connect is dialing closes the new session instead of showing the server", async () => {
    server();
    const dbs = deferred<unknown>();
    const base = rpc.getMockImplementation()!;
    rpc.mockImplementation((m: string, p: unknown) => (m === "meta.databases" ? dbs.promise : base(m, p)));
    const connecting = c().connect("p1");
    await flush();
    await c().disconnect("p1");
    dbs.resolve([{ name: "Sales", isSystem: false }]);
    expect(await connecting).toBe(false);
    expect(c().servers.p1).toBeUndefined();
    expect(rpc).toHaveBeenCalledWith("session.close", { sessionId: "s1", force: true });
  });
  it("sessionFor does not silently reconnect a disconnected server", async () => {
    server();
    await c().connect("p1");
    await c().disconnect("p1");
    rpc.mockClear();
    await expect(c().sessionFor("p1")).rejects.toThrow(/Disconnected/);
    await expect(c().refreshDatabases("p1")).rejects.toThrow(/Disconnected/);
    expect(rpc).not.toHaveBeenCalledWith("session.open", expect.anything());
    expect(c().servers.p1).toBeUndefined();
  });
  it("an explicit connect clears the disconnected mark and lists databases again", async () => {
    server();
    await c().connect("p1");
    await c().disconnect("p1");
    expect(await c().connect("p1")).toBe(true);
    expect(isDisconnected("p1")).toBe(false);
    expect(c().servers.p1).toMatchObject({ status: "connected", sessionId: "s2" });
    expect(c().servers.p1.databases.map((d) => d.name)).toEqual(["Sales"]);
  });
});

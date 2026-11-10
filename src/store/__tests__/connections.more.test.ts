import { describe, it, expect, vi, beforeEach } from "vitest";

const { rpc, ask, shell, RpcError } = vi.hoisted(() => {
  class RpcError extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string, readonly data?: unknown) {
      super(message);
    }
  }
  return { rpc: vi.fn(), ask: vi.fn(), shell: { secretEndConnection: vi.fn(async () => {}), secretExists: vi.fn(async () => true) }, RpcError };
});
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, shell, RpcError }));
vi.mock("../ui", () => ({ ask: (make: (r: (v: unknown) => void) => unknown) => new Promise((resolve) => ask(make(resolve), resolve)) }));

import { needsSecret, obtainSecret, useConnections } from "../connections";
import { useProfiles } from "../profiles";

const c = () => useConnections.getState();
const prof = (o: Record<string, unknown> = {}) => ({ id: "p1", name: "L", engine: "mssql", auth: "sql", ...o }) as never;

function setup(profile = prof()) {
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useProfiles.setState({ byId: (id?: string) => (id === "p1" ? profile : undefined) } as never);
  rpc.mockReset();
  ask.mockReset();
  shell.secretExists.mockReset();
  shell.secretExists.mockResolvedValue(true);
}

describe("secrets (both engines)", () => {
  beforeEach(() => setup());
  it("needsSecret by auth kind", () => {
    for (const auth of ["sql", "password", "entraToken"]) expect(needsSecret(prof({ auth }))).toBe(true);
    for (const auth of ["integrated", "none"]) expect(needsSecret(prof({ auth }))).toBe(false);
  });
  it("obtainSecret: not needed, in keyring, keyring lookup fails, retry after error", async () => {
    expect(await obtainSecret(prof({ auth: "integrated" }))).toBeUndefined();
    expect(await obtainSecret(prof())).toBeUndefined();
    expect(ask).not.toHaveBeenCalled();
    shell.secretExists.mockRejectedValueOnce(new Error("x"));
    ask.mockImplementationOnce((_d, resolve) => resolve("typed"));
    expect(await obtainSecret(prof())).toBe("typed");
    ask.mockImplementationOnce((_d, resolve) => resolve(null));
    expect(await obtainSecret(prof(), "login failed")).toBeNull();
    expect(ask.mock.calls.at(-1)![0]).toMatchObject({ kind: "password", error: "login failed" });
  });
});

describe("connect", () => {
  beforeEach(() => setup());
  it("returns false for an unknown profile", async () => {
    expect(await c().connect("nope")).toBe(false);
  });

  it("never dials a profile this build cannot read and shows why (Phase D0a)", async () => {
    setup(prof({ unsupported: "This profile is for an engine this version of Querithm does not know ('oracle')." }));
    expect(await c().connect("p1")).toBe(false);
    expect(rpc).not.toHaveBeenCalled();
    expect(ask).not.toHaveBeenCalled();
    expect(c().servers.p1).toMatchObject({ status: "error", error: expect.stringContaining("oracle") });
  });

  for (const [engine, msg] of [["mssql", "Login failed for user 'sa'. 18456"], ["pg", "password authentication failed for user x"]] as const) {
    it(`re-prompts on an auth error and succeeds (${engine})`, async () => {
      setup(prof({ engine }));
      let opens = 0;
      rpc.mockImplementation(async (m: string) => {
        if (m === "session.open") {
          if (++opens === 1) throw new RpcError(msg, -1);
          return { sessionId: "s2" };
        }
        if (m === "session.state") return { serverVersion: "1" };
        if (m === "meta.databases") return [];
        if (m === "session.capabilities") return {};
      });
      ask.mockImplementation((_d, resolve) => resolve("pw"));
      expect(await c().connect("p1")).toBe(true);
      expect(ask).toHaveBeenCalledTimes(1);
      expect(ask.mock.calls[0][0]).toMatchObject({ kind: "password", error: msg });
      expect(c().servers.p1.status).toBe("connected");
    });
  }

  it("gives up after repeated auth failures with the last error", async () => {
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") throw new RpcError("login failed", -1, "needsSecret");
    });
    ask.mockImplementation((_d, resolve) => resolve("bad"));
    expect(await c().connect("p1")).toBe(false);
    expect(c().servers.p1).toMatchObject({ status: "error", error: "login failed" });
  });

  it("cancelling the password prompt removes the pending server", async () => {
    shell.secretExists.mockResolvedValue(false);
    ask.mockImplementation((_d, resolve) => resolve(null));
    expect(await c().connect("p1")).toBe(false);
    expect(c().servers.p1).toBeUndefined();
    expect(c().order).toEqual([]);
  });

  it("a non-auth error is recorded and closes the half-open session", async () => {
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") return { sessionId: "s9" };
      if (m === "session.state") throw new Error("unreachable");
      return undefined;
    });
    expect(await c().connect("p1")).toBe(false);
    expect(c().servers.p1).toMatchObject({ status: "error", error: "unreachable" });
    expect(rpc).toHaveBeenCalledWith("session.close", { sessionId: "s9", force: true });
  });

  it("non-Error rejections are stringified", async () => {
    setup(prof({ auth: "integrated" }));
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") throw "plain";
    });
    await c().connect("p1");
    expect(c().servers.p1.error).toBe("plain");
  });
});

describe("sessions", () => {
  beforeEach(() => setup(prof({ auth: "integrated" })));
  const ok = () =>
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") return { sessionId: "sx" };
      if (m === "session.state") return { serverVersion: "1" };
      if (m === "meta.databases") return [{ name: "A" }];
      if (m === "session.capabilities") return { x: 1 };
    });

  it("sessionFor reuses, connects lazily, and refuses after an explicit disconnect", async () => {
    ok();
    expect(await c().sessionFor("p1")).toBe("sx");
    const opens = rpc.mock.calls.filter((x) => x[0] === "session.open").length;
    expect(await c().sessionFor("p1")).toBe("sx");
    expect(rpc.mock.calls.filter((x) => x[0] === "session.open").length).toBe(opens);
    await c().disconnect("p1");
    await expect(c().sessionFor("p1")).rejects.toThrow(/Disconnected/);
    await expect(c().sessionFor("unknown")).rejects.toThrow("Not connected");
  });

  it("database refresh keeps the current list, blocks duplicate refreshes, and clears busy after failure", async () => {
    ok();
    await c().connect("p1");
    const cached = c().servers.p1.databases;
    let reject!: (error: Error) => void;
    rpc.mockImplementation(() => new Promise((_, fail) => { reject = fail; }));
    const refresh = c().refreshDatabases("p1");
    expect(c().servers.p1.refreshing).toBe(true);
    expect(c().servers.p1.databases).toBe(cached);
    await c().refreshDatabases("p1");
    expect(rpc.mock.calls.filter((call) => call[0] === "meta.databases")).toHaveLength(2); // initial connect + refresh
    reject(new Error("Connection lost"));
    await expect(refresh).rejects.toThrow("Connection lost");
    expect(c().servers.p1.refreshing).toBe(false);
    expect(c().servers.p1.databases).toBe(cached);
  });

  it("refreshDatabases updates the live server", async () => {
    ok();
    await c().connect("p1");
    rpc.mockImplementation(async (m: string) => (m === "meta.databases" ? [{ name: "B" }] : undefined));
    await c().refreshDatabases("p1");
    expect(c().servers.p1.databases).toEqual([{ name: "B" }]);
  });

  it("resetSessions clears ids and reconnects every server", async () => {
    ok();
    await c().connect("p1");
    c().resetSessions();
    expect(c().servers.p1.sessionId).toBeUndefined();
    await vi.waitFor(() => expect(c().servers.p1.status).toBe("connected"));
    expect(c().servers.p1.sessionId).toBe("sx");
  });

  it("sessionFor during a reconnect waits for it instead of dialing a second session (engine restart)", async () => {
    ok();
    await c().connect("p1");
    rpc.mockClear();
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    let n = 0;
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") {
        await gate;
        return { sessionId: `s${++n}` };
      }
      if (m === "session.state") return { serverVersion: "1" };
      if (m === "meta.databases") return [{ name: "A" }];
    });
    c().resetSessions();
    const a = c().sessionFor("p1");
    const b = c().sessionFor("p1");
    release();
    expect(await a).toBe("s1");
    expect(await b).toBe("s1");
    expect(rpc.mock.calls.filter((x) => x[0] === "session.open")).toHaveLength(1);
    expect(rpc.mock.calls.filter((x) => x[0] === "session.close")).toHaveLength(0);
  });

  it("sessionFor after a reconnect that failed reports it instead of dialing again", async () => {
    ok();
    await c().connect("p1");
    rpc.mockClear();
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") throw new Error("server is starting up");
    });
    c().resetSessions();
    await expect(c().sessionFor("p1")).rejects.toThrow("Not connected");
    expect(rpc.mock.calls.filter((x) => x[0] === "session.open")).toHaveLength(1);
    // Once the failed attempt is over, an explicit action dials again.
    ok();
    expect(await c().sessionFor("p1")).toBe("sx");
  });

  it("reconnecting clears the disconnected flag", async () => {
    ok();
    await c().disconnect("p1");
    expect(c().disconnected.p1).toBe(true);
    await c().connect("p1");
    expect(c().disconnected.p1).toBeUndefined();
  });
});

describe("connect quietly (startup reconnect): never asks, leaves the server to Connect", () => {
  const ok = (m: string) => {
    if (m === "session.open") return { sessionId: "s1" };
    if (m === "session.state") return { serverVersion: "16", database: "master" };
    if (m === "meta.databases") return [];
    return undefined;
  };

  it.each(["mssql", "pg"])("%s: a saved password connects without a prompt", async (engine) => {
    setup(prof({ engine, auth: engine === "pg" ? "password" : "sql" }));
    rpc.mockImplementation(async (m: string) => ok(m));
    expect(await c().connect("p1", { quiet: true })).toBe(true);
    expect(c().servers.p1.status).toBe("connected");
    expect(ask).not.toHaveBeenCalled();
  });

  it.each(["mssql", "pg"])("%s: no saved password: no prompt, no dial, an error that says how to continue", async (engine) => {
    setup(prof({ engine, auth: engine === "pg" ? "password" : "sql" }));
    shell.secretExists.mockResolvedValue(false);
    expect(await c().connect("p1", { quiet: true })).toBe(false);
    expect(ask).not.toHaveBeenCalled();
    expect(rpc).not.toHaveBeenCalled();
    expect(c().servers.p1).toMatchObject({ status: "error", error: "The password is not saved. Reconnect to continue." });
    expect(c().order).toEqual(["p1"]); // still listed in the explorer
  });

  it("a rejected saved password is not retried with a prompt", async () => {
    setup();
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") throw new RpcError("login failed", -1, "needsSecret");
      return undefined;
    });
    expect(await c().connect("p1", { quiet: true })).toBe(false);
    expect(ask).not.toHaveBeenCalled();
    expect(rpc.mock.calls.filter((x) => x[0] === "session.open")).toHaveLength(1);
    expect(c().servers.p1).toMatchObject({ status: "error", error: "login failed Reconnect to continue." });
  });

  it.each(["sshHostKeyUnknown", "sshHostKeyChanged", "sshNeedsSecret"])("%s: no SSH dialog", async (appCode) => {
    setup(prof({ auth: "integrated" }));
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open") throw new RpcError("ssh", -1, appCode, { host: "h" });
      return undefined;
    });
    expect(await c().connect("p1", { quiet: true })).toBe(false);
    expect(ask).not.toHaveBeenCalled();
    expect(c().servers.p1.status).toBe("error");
  });

  it("a server down at launch shows the error like an interactive connect", async () => {
    setup(prof({ auth: "integrated" }));
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.state") throw new RpcError("network unreachable", -1);
      return ok(m);
    });
    expect(await c().connect("p1", { quiet: true })).toBe(false);
    expect(c().servers.p1).toMatchObject({ status: "error", error: "network unreachable" });
    expect(rpc).toHaveBeenCalledWith("session.close", { sessionId: "s1", force: true }); // no orphaned session
  });

  it("a sidecar restart does not prompt for a server that was still waiting for its password", async () => {
    setup();
    shell.secretExists.mockResolvedValue(false);
    await c().connect("p1", { quiet: true });
    expect(c().servers.p1.status).toBe("error");
    c().resetSessions();
    await vi.waitFor(() => expect(c().servers.p1.status).toBe("error"));
    expect(ask).not.toHaveBeenCalled();
    expect(c().order).toEqual(["p1"]);
  });

  it("disconnecting while the quiet check runs does not bring the server back", async () => {
    setup();
    let release!: (v: boolean) => void;
    shell.secretExists.mockReturnValue(new Promise<boolean>((r) => (release = r)));
    const p = c().connect("p1", { quiet: true });
    await c().disconnect("p1");
    release(false);
    expect(await p).toBe(false);
    expect(c().servers.p1).toBeUndefined();
  });
});

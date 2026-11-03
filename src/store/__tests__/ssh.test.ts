import { describe, it, expect, vi, beforeEach } from "vitest";
import { useConnections } from "../connections";
import { sshAccount, useProfiles } from "../profiles";

// SSH tunnels: the connect flow's host-key and SSH-secret prompts, and the keyring account of the SSH secret.
const { rpc, ask, shell, RpcError } = vi.hoisted(() => {
  class RpcError extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string, readonly data?: unknown) {
      super(message);
    }
  }
  return {
    rpc: vi.fn(),
    ask: vi.fn(),
    shell: { secretExists: vi.fn(async () => true), secretSet: vi.fn(async () => {}), secretDelete: vi.fn(async () => {}) },
    RpcError,
  };
});
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, shell, RpcError }));
vi.mock("../ui", () => ({ ask: (make: (resolve: (v: unknown) => void) => unknown) => new Promise((resolve) => ask(make(resolve), resolve)) }));

const c = () => useConnections.getState();
const hostKey = { host: "bastion", port: 22, algorithm: "ssh-ed25519", fingerprint: "AbC123", previousFingerprint: null };

/** session.open fails with `failures` in turn, then succeeds. */
function server(...failures: Error[]) {
  rpc.mockImplementation(async (method: string) => {
    if (method === "session.open") return { sessionId: `s${rpc.mock.calls.filter((x) => x[0] === "session.open").length}`, database: null };
    if (method === "session.state") {
      const f = failures.shift();
      if (f) throw f;
      return { serverVersion: "16" };
    }
    if (method === "meta.databases") return [];
    return undefined;
  });
}

describe("ssh: connect", () => {
  beforeEach(() => {
    rpc.mockReset();
    ask.mockReset();
    useConnections.setState({ servers: {}, order: [], disconnected: {} });
    const profile = { id: "p1", name: "Via bastion", engine: "pg", auth: "integrated", ssh: { host: "bastion", username: "me", auth: "password" } };
    useProfiles.setState({ byId: (id?: string) => (id === "p1" ? (profile as never) : undefined) } as never);
  });

  it.each(["sshHostKeyUnknown", "sshHostKeyChanged"])("%s: asks the user, records the trusted key, and retries", async (code) => {
    server(new RpcError("not trusted", 1, code, hostKey));
    ask.mockImplementation((dialog, resolve) => resolve(dialog.kind === "hostKey"));
    expect(await c().connect("p1")).toBe(true);
    expect(ask.mock.calls[0][0]).toMatchObject({ kind: "hostKey", hostKey });
    expect(rpc).toHaveBeenCalledWith("ssh.trustHostKey", hostKey);
    // The half-open session of the refused attempt is closed.
    expect(rpc).toHaveBeenCalledWith("session.close", { sessionId: "s1", force: true });
    expect(c().servers.p1.status).toBe("connected");
  });

  it("a declined host key is never trusted and leaves the server in error", async () => {
    server(new RpcError("The SSH server bastion:22 is not trusted yet.", 1, "sshHostKeyUnknown", hostKey));
    ask.mockImplementation((_dialog, resolve) => resolve(false));
    expect(await c().connect("p1")).toBe(false);
    expect(rpc).not.toHaveBeenCalledWith("ssh.trustHostKey", expect.anything());
    expect(c().servers.p1).toMatchObject({ status: "error", error: expect.stringContaining("not trusted") });
  });

  it("a missing SSH secret is asked for (not the database password) and sent with the retry", async () => {
    server(new RpcError("The SSH password is not loaded.", 1, "sshNeedsSecret"));
    ask.mockImplementation((_dialog, resolve) => resolve("typed-ssh"));
    expect(await c().connect("p1")).toBe(true);
    expect(ask.mock.calls[0][0]).toMatchObject({ kind: "password", profileId: "p1", ssh: true });
    const opens = rpc.mock.calls.filter((x) => x[0] === "session.open").map((x) => x[1].sshSecret);
    expect(opens).toEqual([null, "typed-ssh"]);
  });

  it("cancelling the SSH secret prompt stops connecting", async () => {
    server(new RpcError("The SSH password is not loaded.", 1, "sshNeedsSecret"));
    ask.mockImplementation((_dialog, resolve) => resolve(null));
    expect(await c().connect("p1")).toBe(false);
    expect(rpc.mock.calls.filter((x) => x[0] === "session.open")).toHaveLength(1);
    expect(c().servers.p1.status).toBe("error");
  });
});

describe("ssh: keyring", () => {
  beforeEach(() => {
    rpc.mockReset();
    shell.secretSet.mockClear();
    shell.secretDelete.mockClear();
    useProfiles.setState({ profiles: [] });
  });

  it("the SSH secret has its own account and never travels in profile.save", async () => {
    const p = { id: "p1", name: "B", ssh: { host: "bastion", username: "me", auth: "password" } };
    rpc.mockResolvedValue(p);
    await useProfiles.getState().save(p as never, "db-pass", "ssh-pass");
    expect(sshAccount("p1")).toBe("p1:ssh");
    expect(shell.secretSet).toHaveBeenCalledWith("p1", "db-pass");
    expect(shell.secretSet).toHaveBeenCalledWith("p1:ssh", "ssh-pass");
    expect(JSON.stringify(rpc.mock.calls)).not.toContain("ssh-pass");
  });

  it("null keeps the stored SSH secret; a profile without a tunnel drops it", async () => {
    const withSsh = { id: "p1", name: "B", ssh: { host: "bastion", username: "me", auth: "password" } };
    rpc.mockResolvedValue(withSsh);
    await useProfiles.getState().save(withSsh as never, null, null);
    expect(shell.secretSet).not.toHaveBeenCalled();
    expect(shell.secretDelete).not.toHaveBeenCalled();
    rpc.mockResolvedValue({ ...withSsh, ssh: null });
    await useProfiles.getState().save({ ...withSsh, ssh: null } as never, null, null);
    expect(shell.secretDelete).toHaveBeenCalledWith("p1:ssh");
  });

  it("deleting a profile leaves both secrets to the shell, which deletes them after the sidecar did (L2)", async () => {
    await useProfiles.getState().remove("p1");
    expect(shell.secretDelete).not.toHaveBeenCalled();
  });
});

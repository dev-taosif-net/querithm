// §13 rule 1, reading a secret back on connect: the webview never reads it. It only asks the shell whether the keyring
// has one (secret_exists); session.open goes out with secret: null and the shell injects "<id>" / "<id>:ssh". A missing
// secret prompts the user (or, quietly, leaves the server waiting for Connect); no empty password is ever sent.
import { describe, expect, it, vi } from "vitest";

const { rpc, ask, shell, RpcError } = vi.hoisted(() => {
  class RpcError extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string, readonly data?: unknown) {
      super(message);
    }
  }
  return {
    rpc: vi.fn(), ask: vi.fn(),
    shell: { secretEndConnection: vi.fn(async (_id: string) => {}), secretExists: vi.fn(async (_id: string) => true) },
    RpcError,
  };
});
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, shell, RpcError }));
vi.mock("../ui", () => ({ ask: (make: (r: (v: unknown) => void) => unknown) => new Promise((resolve) => ask(make(resolve), resolve)) }));

import { useConnections } from "../connections";
import { useProfiles } from "../profiles";

type P = Record<string, unknown>;
const c = () => useConnections.getState();
const prof = (o: P = {}): P => ({ id: "p1", name: "L", engine: "mssql", auth: "sql", host: "h", ...o });
const opens = () => rpc.mock.calls.filter((x) => x[0] === "session.open").map((x) => x[1] as { secret: unknown; sshSecret: unknown });

function setup(profile: P) {
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useProfiles.setState({ byId: (id?: string) => (id === "p1" ? profile : undefined) } as never);
  rpc.mockReset();
  ask.mockReset();
  shell.secretExists.mockReset();
  shell.secretExists.mockResolvedValue(true);
  shell.secretEndConnection.mockClear();
  rpc.mockImplementation(async (m: string) => {
    if (m === "session.open") return { sessionId: "s1" };
    if (m === "session.state") return { serverVersion: "v", database: "db" };
    if (m === "meta.databases") return [];
    return undefined;
  });
}

// Every server engine × each sign-in that takes a secret.
const SECRET_SIGN_INS: [string, string][] = [["mssql", "sql"], ["mssql", "entraToken"], ["pg", "password"], ["pg", "entraToken"], ["mySql", "password"]];
const NO_SECRET: [string, string][] = [["mssql", "windows"], ["mssql", "kerberos"], ["mssql", "entraInteractive"], ["mssql", "entraDeviceCode"], ["pg", "kerberos"]];

describe("connect reads the secret back through the shell only", () => {
  it.each(SECRET_SIGN_INS)("%s/%s with a saved secret: no prompt, session.open sends secret null (the shell injects it)", async (engine, auth) => {
    setup(prof({ engine, auth }));
    expect(await c().connect("p1")).toBe(true);
    expect(shell.secretExists).toHaveBeenCalledWith("p1");
    expect(ask).not.toHaveBeenCalled();
    expect(opens()).toEqual([expect.objectContaining({ secret: null, sshSecret: null })]);
  });

  it.each(SECRET_SIGN_INS)("%s/%s with no saved secret: the user is asked; the typed one goes to the keyring and session.open still sends null", async (engine, auth) => {
    setup(prof({ engine, auth }));
    shell.secretExists.mockResolvedValue(false);
    // The password dialog stores what was typed and resolves undefined (Dialogs.tsx PasswordDialog).
    ask.mockImplementation((d: P, resolve: (v: unknown) => void) => {
      expect(d).toMatchObject({ kind: "password", profileId: "p1" });
      resolve(undefined);
    });
    expect(await c().connect("p1")).toBe(true);
    expect(ask).toHaveBeenCalledTimes(1);
    expect(opens()).toEqual([expect.objectContaining({ secret: null })]);
  });

  it.each(SECRET_SIGN_INS)("%s/%s: cancelling the prompt sends nothing", async (engine, auth) => {
    setup(prof({ engine, auth }));
    shell.secretExists.mockResolvedValue(false);
    ask.mockImplementation((_d: P, resolve: (v: unknown) => void) => resolve(null));
    expect(await c().connect("p1")).toBe(false);
    expect(opens()).toEqual([]);
  });

  it("a keyring that cannot be read (locked) counts as no secret: the user is asked", async () => {
    setup(prof());
    shell.secretExists.mockRejectedValue(new Error("the keyring is locked"));
    ask.mockImplementation((_d: P, resolve: (v: unknown) => void) => resolve(null));
    expect(await c().connect("p1")).toBe(false);
    expect(ask).toHaveBeenCalledTimes(1);
    expect(opens()).toEqual([]);
  });

  it.each(SECRET_SIGN_INS)("%s/%s quiet reconnect with no saved secret: never asks, never dials, says why", async (engine, auth) => {
    setup(prof({ engine, auth }));
    shell.secretExists.mockResolvedValue(false);
    expect(await c().connect("p1", { quiet: true })).toBe(false);
    expect(ask).not.toHaveBeenCalled();
    expect(opens()).toEqual([]);
    expect(c().servers.p1).toMatchObject({ status: "error", error: "The password is not saved. Reconnect to continue." });
  });

  it.each(NO_SECRET)("%s/%s needs no secret: the keyring is not consulted and no secret is sent", async (engine, auth) => {
    setup(prof({ engine, auth }));
    expect(await c().connect("p1")).toBe(true);
    expect(shell.secretExists).not.toHaveBeenCalled();
    expect(opens()).toEqual([expect.objectContaining({ secret: null, sshSecret: null })]);
  });

  it.each(["sqlite", "duckDb"])("%s file profile signs in with nothing: no keyring lookup, no secret", async (engine) => {
    setup(prof({ engine, auth: "none", location: "file", host: null, file: { grantId: "g1", displayName: "a.db" } }));
    expect(await c().connect("p1")).toBe(true);
    expect(shell.secretExists).not.toHaveBeenCalled();
    expect(opens()).toEqual([expect.objectContaining({ secret: null })]);
  });

  it("a rejected password asks again, showing the server's reason", async () => {
    setup(prof({ engine: "pg", auth: "password" }));
    let n = 0;
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open" && n++ === 0) throw new RpcError("password authentication failed for user u", -1);
      if (m === "session.open") return { sessionId: "s1" };
      if (m === "meta.databases") return [];
      return { serverVersion: "v" };
    });
    ask.mockImplementation((_d: P, resolve: (v: unknown) => void) => resolve(undefined));
    expect(await c().connect("p1")).toBe(true);
    expect(ask).toHaveBeenCalledTimes(1);
    expect(ask.mock.calls[0][0]).toMatchObject({ kind: "password", error: "password authentication failed for user u" });
    expect(opens().every((o) => o.secret === null)).toBe(true);
  });

  it("a missing SSH secret prompts for <id>:ssh; an empty answer is never sent as an empty passphrase", async () => {
    setup(prof({ engine: "pg", auth: "password", ssh: { host: "b", port: 22, username: "j", auth: "password", keyPath: null } }));
    let n = 0;
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open" && n++ === 0) throw new RpcError("SSH needs a password", -1, "sshNeedsSecret");
      if (m === "session.open") return { sessionId: "s1" };
      if (m === "meta.databases") return [];
      return { serverVersion: "v" };
    });
    // "Save in the OS keyring" off and nothing typed: the dialog resolves "".
    ask.mockImplementation((d: P, resolve: (v: unknown) => void) => {
      expect(d).toMatchObject({ kind: "password", profileId: "p1", ssh: true });
      resolve("");
    });
    expect(await c().connect("p1")).toBe(true);
    expect(opens()).toHaveLength(2);
    expect(opens()[1].sshSecret).toBeNull();
  });

  it("an SSH passphrase typed without saving goes only into that session.open", async () => {
    setup(prof({ engine: "mySql", auth: "password", ssh: { host: "b", port: 22, username: "j", auth: "privateKey", keyPath: "/k" } }));
    let n = 0;
    rpc.mockImplementation(async (m: string) => {
      if (m === "session.open" && n++ === 0) throw new RpcError("SSH key is protected", -1, "sshNeedsSecret");
      if (m === "session.open") return { sessionId: "s1" };
      if (m === "meta.databases") return [];
      return { serverVersion: "v" };
    });
    ask.mockImplementation((_d: P, resolve: (v: unknown) => void) => resolve("typed-pp"));
    expect(await c().connect("p1")).toBe(true);
    expect(opens()[1]).toMatchObject({ secret: null, sshSecret: "typed-pp" });
  });

  it("disconnect tells the shell the connection ended (an 'until disconnected' secret is dropped there)", async () => {
    setup(prof());
    await c().connect("p1");
    await c().disconnect("p1");
    expect(shell.secretEndConnection).toHaveBeenCalledWith("p1");
  });
});

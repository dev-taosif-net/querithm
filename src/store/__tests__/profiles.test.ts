import { describe, it, expect, vi, beforeEach } from "vitest";

const { rpc, shell, RpcError } = vi.hoisted(() => ({
  rpc: vi.fn(),
  shell: { secretSet: vi.fn(async () => {}), secretDelete: vi.fn(async () => {}) },
  RpcError: class extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string) {
      super(message);
    }
  },
}));
vi.mock("../../ipc/client", () => ({ rpc, shell, RpcError }));

import { envColor, envLabel, engineBadge, profileDialect, profileTitle, sshAccount, useProfiles } from "../profiles";

const prof = (o: Record<string, unknown> = {}) => ({ id: "p1", name: "Zed", engine: "mssql", environment: "dev", ...o }) as never;
const s = () => useProfiles.getState();

describe("profile helpers (both engines)", () => {
  it("envColor prefers profile color, then group color, then css var", () => {
    expect(envColor(undefined)).toBe("var(--q-accent)");
    expect(envColor(prof({ color: "#f00" }))).toBe("#f00");
    expect(envColor(prof({ groupName: "g" }), [{ name: "g", color: "#0f0" } as never])).toBe("#0f0");
    expect(envColor(prof({ groupName: "g" }), [{ name: "g" } as never])).toBe("var(--q-env-dev)");
    expect(envColor(prof({ environment: "prod" }))).toBe("var(--q-env-prod)");
  });
  it("engineBadge and title", () => {
    expect(engineBadge(prof({ engine: "pg" }))).toBe("PG");
    expect(engineBadge(prof({ engine: "mssql" }))).toBe("MS");
    expect(engineBadge(undefined)).toBe("MS");
    expect(profileTitle(undefined)).toBe("Not connected");
    expect(profileTitle(prof({ environment: "staging" }))).toBe(`${envLabel.staging} - Zed`);
  });
  it("sshAccount", () => expect(sshAccount("p")).toBe("p:ssh"));
  it("profileDialect: the profile's engine dialect; no profile is the default engine's (T-SQL)", () => {
    useProfiles.setState({ profiles: [prof({ id: "m", engine: "mssql" }), prof({ id: "g", engine: "pg" })] });
    expect(profileDialect("m")).toBe("tsql");
    expect(profileDialect("g")).toBe("pg");
    expect(profileDialect("gone")).toBe("tsql");
    expect(profileDialect(undefined)).toBe("tsql");
    useProfiles.setState({ profiles: [] });
  });
});

describe("profiles store", () => {
  beforeEach(() => {
    rpc.mockReset();
    shell.secretSet.mockClear();
    shell.secretDelete.mockReset();
    shell.secretDelete.mockResolvedValue(undefined);
    useProfiles.setState({ profiles: [], groups: [], loaded: false });
  });

  it("loads profiles and groups", async () => {
    rpc.mockImplementation(async (m: string) => (m === "profile.list" ? [prof()] : [{ name: "g" }]));
    await s().load();
    expect(s().loaded).toBe(true);
    expect(s().byId("p1")?.name).toBe("Zed");
    expect(s().byId(undefined)).toBeUndefined();
    expect(s().groups).toHaveLength(1);
  });

  for (const engine of ["mssql", "pg"] as const) {
    it(`save stores secret in keyring, never in profile.save (${engine})`, async () => {
      rpc.mockImplementation(async (_m: string, p: unknown) => p);
      await s().save(prof({ engine }), "pw");
      expect(shell.secretSet).toHaveBeenCalledWith("p1", "pw");
      expect(rpc).toHaveBeenCalledWith("profile.save", expect.not.objectContaining({ secret: expect.anything() }));
      // no ssh -> ssh secret cleared
      expect(shell.secretDelete).toHaveBeenCalledWith("p1:ssh");
    });
  }

  it("save: empty secret deletes, null leaves, ssh secret stored", async () => {
    rpc.mockImplementation(async (_m: string, p: unknown) => p);
    await s().save(prof({ ssh: { host: "b" } }), "", "sshpw");
    expect(shell.secretDelete).toHaveBeenCalledWith("p1");
    expect(shell.secretSet).toHaveBeenCalledWith("p1:ssh", "sshpw");
    shell.secretDelete.mockClear();
    shell.secretSet.mockClear();
    await s().save(prof({ ssh: { host: "b" } }), null, null);
    expect(shell.secretSet).not.toHaveBeenCalled();
    expect(shell.secretDelete).not.toHaveBeenCalled();
  });

  it("save keeps profiles sorted and replaces by id while reporting SSH deletion failures", async () => {
    rpc.mockImplementation(async (_m: string, p: unknown) => p);
    shell.secretDelete.mockRejectedValue(new Error("none"));
    await expect(s().save(prof({ id: "b", name: "B" }))).rejects.toMatchObject({ savedProfile: expect.objectContaining({ id: "b" }) });
    await expect(s().save(prof({ id: "a", name: "A" }))).rejects.toMatchObject({ savedProfile: expect.objectContaining({ id: "a" }) });
    await expect(s().save(prof({ id: "b", name: "C" }))).rejects.toMatchObject({ savedProfile: expect.objectContaining({ id: "b" }) });
    expect(s().profiles.map((p) => p.name)).toEqual(["A", "C"]);
  });

  it("remove deletes the profile in the sidecar; its keyring entries are the shell's to delete (L2)", async () => {
    useProfiles.setState({ profiles: [prof(), prof({ id: "p2" })] });
    shell.secretDelete.mockClear();
    rpc.mockResolvedValue(undefined);
    await s().remove("p1");
    expect(rpc).toHaveBeenCalledWith("profile.delete", { profileId: "p1" });
    expect(s().profiles.map((p) => p.id)).toEqual(["p2"]);
    expect(shell.secretDelete).not.toHaveBeenCalled();
  });

  it("remove keeps the profile when the sidecar refuses, and drops it when only the keyring entries failed", async () => {
    useProfiles.setState({ profiles: [prof(), prof({ id: "p2" })] });
    rpc.mockRejectedValueOnce(new RpcError("busy", -32001, "profileBusy"));
    await expect(s().remove("p1")).rejects.toMatchObject({ appCode: "profileBusy" });
    expect(s().profiles.map((p) => p.id)).toEqual(["p1", "p2"]);
    rpc.mockRejectedValueOnce(new RpcError("deleted, keyring locked", -32001, "secretsNotDeleted"));
    await expect(s().remove("p1")).rejects.toMatchObject({ appCode: "secretsNotDeleted" });
    expect(s().profiles.map((p) => p.id)).toEqual(["p2"]);
  });

  it("group save/remove replace the group list from the reply", async () => {
    rpc.mockResolvedValueOnce([{ name: "g1" }]);
    await s().saveGroup({ name: "g1" } as never);
    expect(s().groups).toEqual([{ name: "g1" }]);
    rpc.mockResolvedValueOnce([]);
    await s().removeGroup("g1");
    expect(rpc).toHaveBeenLastCalledWith("group.delete", { name: "g1" });
    expect(s().groups).toEqual([]);
  });
});

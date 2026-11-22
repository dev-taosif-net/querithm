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
import { useUi } from "../ui";

const prof = (o: Record<string, unknown> = {}) => ({ id: "p1", name: "Zed", engine: "mssql", environment: "dev", ...o }) as never;
const s = () => useProfiles.getState();

describe("profile helpers (both engines)", () => {
  it("envColor prefers the profile's own color, then the environment's; a group never changes it", () => {
    expect(envColor(undefined)).toBe("var(--q-accent)");
    expect(envColor(prof({ color: "#f00" }))).toBe("#f00");
    expect(envColor(prof({ groupName: "g" }))).toBe("var(--q-env-dev)");
    expect(envColor(prof({ environment: "prod", groupName: "Payments" }))).toBe("var(--q-env-prod)");
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

  it("createGroup replaces the group list from the reply", async () => {
    rpc.mockResolvedValueOnce([{ name: "g1", members: 0 }]);
    await s().createGroup("g1");
    expect(rpc).toHaveBeenLastCalledWith("group.create", { name: "g1" });
    expect(s().groups).toEqual([{ name: "g1", members: 0 }]);
  });

  it("rename, delete and assign apply the groups and the moved profiles in one step", async () => {
    useProfiles.setState({ profiles: [prof({ groupName: "Old" }), prof({ id: "p2", groupName: "Other" })], groups: [{ name: "Old", members: 1 }] });
    const seen: string[][] = [];
    const stop = useProfiles.subscribe((st) => seen.push([st.groups.map((g) => g.name).join(), st.profiles.map((p) => p.groupName ?? "-").join()]));
    rpc.mockResolvedValueOnce({ groups: [{ name: "New", members: 1 }], profiles: [prof({ groupName: "New" })], renamed: { from: "Old", to: "New" } });
    const r = await s().renameGroup("Old", "New");
    expect(rpc).toHaveBeenLastCalledWith("group.rename", { from: "Old", to: "New" });
    expect(r.renamed).toEqual({ from: "Old", to: "New" });
    expect(seen).toEqual([["New", "New,Other"]]); // one set(): never the new group with the old profile
    rpc.mockResolvedValueOnce({ groups: [], profiles: [prof({ groupName: null })] });
    await s().deleteGroup("New", 1);
    expect(rpc).toHaveBeenLastCalledWith("group.delete", { name: "New", expectedMembers: 1 });
    expect(s().byId("p1")?.groupName).toBeNull();
    rpc.mockResolvedValueOnce({ groups: [{ name: "Core", members: 2 }], profiles: [prof({ groupName: "Core" }), prof({ id: "p2", groupName: "Core" })], missing: ["gone"] });
    expect((await s().assignGroup(["p1", "p2", "gone"], "Core")).missing).toEqual(["gone"]);
    expect(rpc).toHaveBeenLastCalledWith("group.assign", { profileIds: ["p1", "p2", "gone"], name: "Core" });
    expect(s().profiles.map((p) => p.groupName)).toEqual(["Core", "Core"]);
    stop();
  });

  it("a refused group write changes nothing in the store", async () => {
    useProfiles.setState({ profiles: [prof({ groupName: "Old" })], groups: [{ name: "Old", members: 1 }] });
    rpc.mockRejectedValueOnce(new RpcError("now 2", -32001, "groupChanged"));
    await expect(s().deleteGroup("Old", 1)).rejects.toMatchObject({ appCode: "groupChanged" });
    expect(s().groups).toEqual([{ name: "Old", members: 1 }]);
    expect(s().byId("p1")?.groupName).toBe("Old");
  });

  it("load still loads the profiles when group.list fails, and says so", async () => {
    useUi.setState({ toasts: [] });
    rpc.mockImplementation(async (m: string) => {
      if (m === "group.list") throw new Error("store busy");
      return [prof({ groupName: "Payments" })];
    });
    await s().load();
    expect(s().loaded).toBe(true);
    expect(s().byId("p1")?.groupName).toBe("Payments");
    expect(s().groups).toEqual([]);
    expect(useUi.getState().toasts.some((t) => t.kind === "warn" && /groups/.test(t.text))).toBe(true);
  });

  it("load still fails when profile.list fails (the start screen shows it)", async () => {
    rpc.mockImplementation(async (m: string) => {
      if (m === "profile.list") throw new Error("no store");
      return [];
    });
    await expect(s().load()).rejects.toThrow("no store");
    expect(s().loaded).toBe(false);
  });

  it("a failed group refresh after a save never fails the save, so a second save updates the same profile (G2)", async () => {
    let n = 0;
    rpc.mockImplementation(async (m: string, p: { id?: string }) => {
      if (m === "group.list") throw new Error("store busy");
      return { ...p, id: p.id || `new${++n}` };
    });
    const saved = await s().save(prof({ id: "", groupName: "Payments" }));
    expect(saved.id).toBe("new1");
    await s().save({ ...saved, name: "Renamed" });
    expect(s().profiles.map((p) => p.id)).toEqual(["new1"]);
  });

  it("a delete that happened is never reported as failed because the group refresh failed (G2)", async () => {
    useProfiles.setState({ profiles: [prof()], groups: [{ name: "g", members: 1 }] });
    rpc.mockImplementation(async (m: string) => {
      if (m === "group.list") throw new Error("store busy");
    });
    await s().remove("p1");
    expect(s().profiles).toEqual([]);
    expect(s().groups).toEqual([{ name: "g", members: 1 }]);
  });

  it("save and remove read the groups again (a typed name may have made one, or emptied one)", async () => {
    rpc.mockImplementation(async (m: string, p: unknown) => (m === "group.list" ? [{ name: "Payments", members: 1 }] : p));
    await s().save(prof({ groupName: "Payments" }));
    expect(s().groups).toEqual([{ name: "Payments", members: 1 }]);
    rpc.mockImplementation(async (m: string) => (m === "group.list" ? [] : undefined));
    await s().remove("p1");
    expect(s().groups).toEqual([]);
  });
});

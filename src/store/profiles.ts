import { create } from "zustand";
import type { DatabaseInfo, Dialect, EnvironmentKind, GroupChangeResult, Profile, ProfileGroup } from "../ipc/contract";
import { rpc, RpcError, shell } from "../ipc/client";
import { endpointKey } from "../connections/auth";
import { removeFromOrder, renameInOrder } from "../connections/order";
import { dialectOf, engineTraits } from "../engines";
import { useSettings } from "./settings";
import { toast } from "./ui";

export const envLabel: Record<EnvironmentKind, string> = { local: "Local", dev: "Dev", test: "Test", staging: "Stage", prod: "Prod" };

/** §6 Environment safety: Prod defaults to red; a profile's own color overrides. Groups have no color (they never change an environment cue). */
export function envColor(p: Pick<Profile, "environment" | "color"> | undefined): string {
  if (!p) return "var(--q-accent)";
  if (p.color) return p.color;
  return `var(--q-env-${p.environment})`;
}

export function engineBadge(p: Pick<Profile, "engine"> | undefined) {
  return engineTraits(p?.engine).badge;
}

export function profileTitle(p: Profile | undefined): string {
  return p ? `${envLabel[p.environment]} - ${p.name}` : "Not connected";
}

interface ProfilesState {
  profiles: Profile[];
  groups: ProfileGroup[];
  loaded: boolean;
  load(): Promise<void>;
  /** `secret` / `sshSecret`: null = keep the remembered secret, "" = remove, else store (§13 rule 1). */
  save(p: Profile, secret?: string | null, sshSecret?: string | null): Promise<Profile>;
  remove(id: string): Promise<void>;
  /** Reads the groups again; never rejects (a failure keeps the list shown and says so). */
  refreshGroups(): Promise<void>;
  /** One empty group (the group manager's New). */
  createGroup(name: string): Promise<void>;
  renameGroup(from: string, to: string): Promise<GroupChangeResult>;
  /** `expectedMembers`: the count the user was shown; a different count is refused (appCode groupChanged). */
  deleteGroup(name: string, expectedMembers?: number): Promise<GroupChangeResult>;
  /** Moves profiles into a group (a new name makes it) or, with null, out of their group. */
  assignGroup(profileIds: string[], name: string | null): Promise<GroupChangeResult>;
  byId(id: string | undefined): Profile | undefined;
}

export const useProfiles = create<ProfilesState>((set, get) => {
  /** A group write's reply, applied in one step: the groups and the profiles it moved. */
  const apply = (r: GroupChangeResult) => {
    const changed = new Map(r.profiles.map((p) => [p.id, p]));
    set({ groups: r.groups, profiles: get().profiles.map((p) => changed.get(p.id) ?? p) });
    return r;
  };
  return {
  profiles: [],
  groups: [],
  loaded: false,
  async load() {
    // Groups are an extra: profiles still load (grouped by their own names) when group.list fails.
    const [profiles, groups] = await Promise.allSettled([rpc("profile.list"), rpc("group.list")]);
    if (profiles.status === "rejected") throw profiles.reason;
    if (groups.status === "rejected") toast("warn", "Could not read the connection groups; connections still show under their group names.");
    set({ profiles: profiles.value, groups: groups.status === "fulfilled" ? groups.value : get().groups, loaded: true });
  },
  async refreshGroups() {
    try {
      const groups = await rpc("group.list");
      if (Array.isArray(groups)) set({ groups });
    } catch {
      toast("warn", "Could not read the connection groups again; the list may be out of date until the next change.");
    }
  },
  async save(p, secret, sshSecret) {
    // A group's default environment applies to new profiles that did not pick one explicitly.
    const before = get().profiles.find((profile) => profile.id === p.id);
    const moved = !!before && endpointKey(before) !== endpointKey(p);
    // Remove credentials before committing a new endpoint. If the keyring is locked, the stored profile must still
    // point at its old endpoint; a failed deletion must never leave the old secret attached to a new destination.
    if (moved) {
      await shell.secretDelete(p.id);
      await shell.secretDelete(sshAccount(p.id));
    }
    const saved = await rpc("profile.save", p);
    // The profile exists from here on, whatever the keyring does next: the list shows it, and a keyring failure carries
    // it (savedProfile) so the editor saves the same profile again instead of creating a second one.
    set({ profiles: [...get().profiles.filter((x) => x.id !== saved.id), saved].sort((a, b) => a.name.localeCompare(b.name)) });
    // A typed group name may have made a group (or emptied one): best effort, never failing the save (G2).
    await get().refreshGroups();
    try {
      if (secret != null) {
        // §13 rule 1: the secret goes to the shell under the profile’s retention policy, never through profile.save.
        if (secret === "") { if (!moved) await shell.secretDelete(saved.id); }
        else await shell.secretSet(saved.id, secret);
      }
      // The SSH tunnel's password or key passphrase: its own keyring entry, "<profileId>:ssh".
      if (sshSecret != null || !saved.ssh) {
        if (!sshSecret) { if (!moved) await shell.secretDelete(sshAccount(saved.id)); }
        else await shell.secretSet(sshAccount(saved.id), sshSecret);
      }
    } catch (e) {
      throw Object.assign(e instanceof Error ? e : new Error(String(e)), { savedProfile: saved });
    }
    return saved;
  },
  async remove(id) {
    // L2: the sidecar closes the profile's sessions (refused with appCode profileBusy while one has an open transaction or
    // a running query) and the shell then deletes every keyring entry of it; a keyring failure there comes back as
    // secretsNotDeleted, with the profile already gone.
    const drop = async () => {
      set({ profiles: get().profiles.filter((p) => p.id !== id) });
      // Its group may have lost its last member: best effort, never reporting a delete that happened as failed (G2).
      await get().refreshGroups();
    };
    try {
      await rpc("profile.delete", { profileId: id });
    } catch (e) {
      if (e instanceof RpcError && e.appCode === "secretsNotDeleted") await drop();
      throw e;
    }
    await drop();
  },
  async createGroup(name) {
    set({ groups: await rpc("group.create", { name }) });
  },
  async renameGroup(from, to) {
    const r = apply(await rpc("group.rename", { from, to }));
    const s = useSettings.getState();
    const next = renameInOrder(s.groupOrder, r.renamed?.from ?? from, r.renamed?.to ?? to);
    if (next.length !== s.groupOrder.length || next.some((x, i) => x !== s.groupOrder[i])) s.set({ groupOrder: next });
    return r;
  },
  async deleteGroup(name, expectedMembers) {
    const r = apply(await rpc("group.delete", { name, expectedMembers: expectedMembers ?? null }));
    const s = useSettings.getState();
    const next = removeFromOrder(s.groupOrder, name, true);
    if (next.length !== s.groupOrder.length) s.set({ groupOrder: next });
    return r;
  },
  async assignGroup(profileIds, name) {
    return apply(await rpc("group.assign", { profileIds, name }));
  },
  byId(id) {
    return id ? get().profiles.find((p) => p.id === id) : undefined;
  },
  };
});

export type { DatabaseInfo };

/** The keyring account of a profile's SSH password or key passphrase. */
export const sshAccount = (profileId: string) => `${profileId}:ssh`;

/** The SQL dialect of a profile's engine; no profile (no connection) is the default engine's (engines.ts). */
export function profileDialect(profileId: string | undefined): Dialect {
  return dialectOf(useProfiles.getState().byId(profileId)?.engine);
}

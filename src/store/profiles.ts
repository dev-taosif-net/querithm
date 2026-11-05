import { create } from "zustand";
import type { DatabaseInfo, Dialect, EnvironmentKind, Profile, ProfileGroup } from "../ipc/contract";
import { rpc, RpcError, shell } from "../ipc/client";
import { dialectOf, engineTraits } from "../engines";

export const envLabel: Record<EnvironmentKind, string> = { local: "Local", dev: "Dev", test: "Test", staging: "Stage", prod: "Prod" };

/** §6 Environment safety: Prod defaults to red; a profile color or its group's color overrides. */
export function envColor(p: Pick<Profile, "environment" | "color" | "groupName"> | undefined, groups: ProfileGroup[] = []): string {
  if (!p) return "var(--q-accent)";
  if (p.color) return p.color;
  const g = groups.find((x) => x.name === p.groupName);
  if (g?.color) return g.color;
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
  saveGroup(g: ProfileGroup): Promise<void>;
  removeGroup(name: string): Promise<void>;
  byId(id: string | undefined): Profile | undefined;
}

export const useProfiles = create<ProfilesState>((set, get) => ({
  profiles: [],
  groups: [],
  loaded: false,
  async load() {
    const [profiles, groups] = await Promise.all([rpc("profile.list"), rpc("group.list")]);
    set({ profiles, groups, loaded: true });
  },
  async save(p, secret, sshSecret) {
    // A group's default environment applies to new profiles that did not pick one explicitly.
    const saved = await rpc("profile.save", p);
    // The profile exists from here on, whatever the keyring does next: the list shows it, and a keyring failure carries
    // it (savedProfile) so the editor saves the same profile again instead of creating a second one.
    set({ profiles: [...get().profiles.filter((x) => x.id !== saved.id), saved].sort((a, b) => a.name.localeCompare(b.name)) });
    try {
      if (secret != null) {
        // §13 rule 1: the secret goes to the shell under the profile’s retention policy, never through profile.save.
        if (secret === "") await shell.secretDelete(saved.id);
        else await shell.secretSet(saved.id, secret);
      }
      // The SSH tunnel's password or key passphrase: its own keyring entry, "<profileId>:ssh".
      if (sshSecret != null || !saved.ssh) {
        if (!sshSecret) await shell.secretDelete(sshAccount(saved.id)).catch(() => {});
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
    const drop = () => set({ profiles: get().profiles.filter((p) => p.id !== id) });
    try {
      await rpc("profile.delete", { profileId: id });
    } catch (e) {
      if (e instanceof RpcError && e.appCode === "secretsNotDeleted") drop();
      throw e;
    }
    drop();
  },
  async saveGroup(g) {
    set({ groups: await rpc("group.save", g) });
  },
  async removeGroup(name) {
    set({ groups: await rpc("group.delete", { name }) });
  },
  byId(id) {
    return id ? get().profiles.find((p) => p.id === id) : undefined;
  },
}));

export type { DatabaseInfo };

/** The keyring account of a profile's SSH password or key passphrase. */
export const sshAccount = (profileId: string) => `${profileId}:ssh`;

/** The SQL dialect of a profile's engine; no profile (no connection) is the default engine's (engines.ts). */
export function profileDialect(profileId: string | undefined): Dialect {
  return dialectOf(useProfiles.getState().byId(profileId)?.engine);
}

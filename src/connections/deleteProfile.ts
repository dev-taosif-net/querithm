// Deleting a profile removes everything kept for it: sessions, keyring secrets, the sidecar's metadata cache and query
// history (profile.delete), in-memory snapshots, pins and per-database explorer settings; open tabs fall back to "Not connected".
// Each tab on the profile is settled first, as closing it would (a running query, staged edits, an open transaction ask);
// the sidecar then closes the profile's sessions itself and refuses (profileBusy) while one still has a transaction or a
// running query, and the shell deletes the keyring entries (L2).
import { settleTab } from "../app/actions";
import { RpcError } from "../ipc/client";
import { useConnections } from "../store/connections";
import { useProfiles } from "../store/profiles";
import { useSettings } from "../store/settings";
import { useSnapshots } from "../store/snapshots";
import { useTabs } from "../store/tabs";
import { ask, toast } from "../store/ui";
import type { Profile } from "../ipc/contract";

export const DELETE_PROFILE_BODY =
  "This removes the profile and all data kept for it: its saved password (OS keyring), cached schema metadata, query history and pin. Open tabs using it are disconnected; a tab with a running query or an open transaction asks first. This cannot be undone.";

/** Asks first (danger confirm), then deletes. True when the profile was deleted. */
export async function confirmDeleteProfile(p: Pick<Profile, "id" | "name">): Promise<boolean> {
  const ok = await ask<boolean>((resolve) => ({ kind: "confirm", title: `Delete profile “${p.name}”?`, body: DELETE_PROFILE_BODY, danger: true, ok: "Delete", resolve }));
  return ok && (await deleteProfile(p.id));
}

/** True when the profile is gone (its keyring entries may not be: then the reason is shown). False: the profile stays (a tab was kept, or the sidecar refused). */
export async function deleteProfile(profileId: string): Promise<boolean> {
  for (const id of [...useTabs.getState().order]) {
    const t = useTabs.getState().tabs[id];
    if (t?.profileId === profileId && t.sessionId && !(await settleTab(id))) return false;
  }
  let keyringError: string | undefined;
  try {
    await useProfiles.getState().remove(profileId);
  } catch (e) {
    if (e instanceof RpcError && e.appCode === "secretsNotDeleted") keyringError = e.message;
    else {
      toast(e instanceof RpcError && e.appCode === "profileBusy" ? "warn" : "error", (e as Error).message || "Could not delete the profile.");
      return false;
    }
  }
  const tabs = useTabs.getState();
  for (const id of tabs.order) {
    const t = tabs.tabs[id];
    if (t?.profileId !== profileId && t?.boundObject?.profileId !== profileId) continue;
    useTabs.getState().update(id, {
      ...(t.profileId === profileId ? { profileId: undefined, database: undefined, sessionId: undefined, spid: undefined, txState: "none" as const, txStartedAt: undefined, busy: false } : {}),
      ...(t.boundObject?.profileId === profileId ? { boundObject: undefined } : {}),
    });
  }
  await useConnections.getState().disconnect(profileId);
  useSnapshots.getState().forget(profileId);
  // A deleted id never reconnects, so it needs no "explicitly disconnected" marker either.
  useConnections.setState((s) => ({ disconnected: Object.fromEntries(Object.entries(s.disconnected).filter(([k]) => k !== profileId)) }));
  const settings = useSettings.getState();
  const prefix = `${profileId}|`;
  const pinnedProfiles = settings.pinnedProfiles.filter((id) => id !== profileId);
  const explorerSchemaGroups = settings.explorerSchemaGroups.filter((k) => !k.startsWith(prefix));
  if (pinnedProfiles.length !== settings.pinnedProfiles.length || explorerSchemaGroups.length !== settings.explorerSchemaGroups.length)
    settings.set({ pinnedProfiles, explorerSchemaGroups });
  if (keyringError) toast("error", keyringError);
  return true;
}

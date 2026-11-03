// Revoking and re-picking a database file (Phase D0c lifecycle). The sidecar's `grant.revoke` closes every session on the
// profile's file and forgets its grant — refused (appCode `grantBusy`) while a tab there has an open transaction or a
// running query — and the shell drops the grant, so the profile needs a new pick before it opens again. A re-pick goes
// through the shell's dialog (pickDatabaseFile) and is read-only until the user allows writes; the webview only ever
// holds the grant id and the file's name.
import type { Profile } from "../ipc/contract";
import { rpc, RpcError } from "../ipc/client";
import { useConnections } from "../store/connections";
import { useProfiles } from "../store/profiles";
import { useSnapshots } from "../store/snapshots";
import { useTabs, type TabState } from "../store/tabs";
import { ask, toast } from "../store/ui";
import { announce } from "../store/announce";
import { locationOf } from "./auth";
import { pickDatabaseFile } from "./fileGrants";

/** Shown on the server after a revoke: nothing opens the file until it is picked again. */
export const REVOKED_NOTE = "File access revoked. Pick the file again (Re-pick file…) to use this connection.";

/** A profile whose database is a granted file (not a server, not memory). */
export function isFileProfile(p: Pick<Profile, "location"> | undefined): boolean {
  return !!p && locationOf(p) === "file";
}

function fileName(p: Profile): string {
  return p.file?.displayName || "the database file";
}

/** Tabs on the profile that hold a session (each one is a connection to the file). */
function sessionTabs(profileId: string): TabState[] {
  return Object.values(useTabs.getState().tabs).filter((t) => t.profileId === profileId && t.sessionId);
}

/** The sessions on the file are closed: tabs keep their text and open a new session on their next run. */
function forgetSessions(profileId: string) {
  for (const t of sessionTabs(profileId)) useTabs.getState().update(t.id, { sessionId: undefined, txState: "none", txStartedAt: undefined, spid: undefined });
}

/** `grant.revoke`; false (and the reason shown) when the sidecar refused or failed. */
async function revoke(profileId: string): Promise<boolean> {
  try {
    await rpc("grant.revoke", { profileId });
    return true;
  } catch (e) {
    // A tab there has an open transaction or a running query: the sidecar's message says what to do first.
    if (e instanceof RpcError && e.appCode === "grantBusy") toast("warn", e.message);
    else toast("error", (e as Error).message || "Could not revoke the file access.");
    return false;
  }
}

/**
 * Explorer ▸ Revoke file access…: after a confirm, closes the file's sessions and forgets its grant. The server then
 * shows "needs a file" (Re-pick file…) instead of reconnecting by itself.
 */
export async function revokeFileAccess(profileId: string): Promise<boolean> {
  const profile = useProfiles.getState().byId(profileId);
  if (!isFileProfile(profile)) return false;
  const name = fileName(profile!);
  const n = sessionTabs(profileId).length;
  const ok = await ask<boolean>((resolve) => ({
    kind: "confirm", title: `Revoke access to “${name}”?`, danger: true, ok: "Revoke access",
    body: `${n ? `Closes ${n} tab session${n === 1 ? "" : "s"} on this file, and ` : ""}Querithm forgets the file: this connection cannot open it again until you pick it again (Re-pick file…). The file itself is not changed.`,
    resolve,
  }));
  if (!ok || !(await revoke(profileId))) return false;
  forgetSessions(profileId);
  useConnections.getState().markNeedsFile(profileId, REVOKED_NOTE);
  useSnapshots.getState().forget(profileId);
  toast("success", `Access to ${name} revoked.`);
  announce(`Access to ${name} revoked`);
  return true;
}

/**
 * Re-pick file… (Explorer server menu, the "needs a file" row, the status bar): picks the file through the shell's
 * dialog, closes the sessions still on the old one (and forgets its grant), saves the profile with the new grant —
 * read-only until the user allows writes in the profile editor — and reconnects.
 */
export async function repickFile(profileId: string): Promise<boolean> {
  const profile = useProfiles.getState().byId(profileId);
  if (!isFileProfile(profile)) return false;
  const open = sessionTabs(profileId);
  if (open.some((t) => t.busy)) {
    toast("warn", "A query is running on this file. Cancel it before picking another file.");
    return false;
  }
  if (open.some((t) => t.txState !== "none")) {
    toast("warn", "A tab on this file has an open transaction. Commit or roll back before picking another file.");
    return false;
  }
  let picked;
  try {
    picked = await pickDatabaseFile();
  } catch (e) {
    toast("error", (e as Error).message || "Could not open the file.");
    return false;
  }
  if (!picked) return false;
  // The old grant goes first: the new one is not bound to the profile until the save, so it stays.
  if (!(await revoke(profileId))) return false;
  forgetSessions(profileId);
  let saved: Profile;
  try {
    saved = await useProfiles.getState().save({ ...profile!, file: { grantId: picked.grantId, displayName: picked.name } });
  } catch (e) {
    useConnections.getState().markNeedsFile(profileId, `${(e as Error).message} ${REVOKED_NOTE}`);
    toast("error", (e as Error).message || "Could not save the profile.");
    return false;
  }
  // The schema shown was the old file's.
  useSnapshots.getState().forget(profileId);
  const connected = await useConnections.getState().connect(saved.id);
  if (connected) {
    // The sidecar's cache may still hold the old file's catalog under this profile: read the new file in full.
    for (const d of useConnections.getState().servers[saved.id]?.databases ?? []) void useSnapshots.getState().refresh(saved.id, d.name, true).catch(() => {});
  }
  toast(connected ? "success" : "warn", connected ? `${saved.name} now uses ${picked.name} (read-only; allow writes in Edit profile…).` : `${saved.name} now names ${picked.name}, but it did not connect.`);
  return connected;
}

// Group actions shared by the group manager and the Object Explorer's group row menu (§6).
import { RpcError } from "../ipc/client";
import { useProfiles } from "../store/profiles";
import { ask, toast } from "../store/ui";

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/**
 * Deletes a group after saying what happens to its connections (they leave the group; none is deleted). The count the
 * user saw goes along; when the group changed meanwhile the sidecar refuses (groupChanged) and this asks again with
 * the real count. An empty group goes without asking. True when it was deleted.
 */
export async function confirmDeleteGroup(name: string): Promise<boolean> {
  const start = useProfiles.getState();
  let expected = start.groups.find((g) => g.name === name)?.members ?? start.profiles.filter((p) => p.groupName === name).length;
  for (let attempt = 0; attempt < 3; attempt++) {
    const prod = useProfiles.getState().profiles.filter((p) => p.groupName === name && p.environment === "prod").length;
    const ok = expected === 0 || (await ask<boolean>((resolve) => ({
      kind: "confirm", danger: true, ok: "Delete group", title: `Delete group “${name}”?`,
      body: `${plural(expected, "connection")}${prod ? ` (${prod} Production)` : ""} leave the group and go back to their usual place in the list. Their environment, passwords and open tabs don’t change. No connection is deleted.`,
      resolve,
    })));
    if (!ok) return false;
    try {
      await useProfiles.getState().deleteGroup(name, expected);
      return true;
    } catch (e) {
      if (e instanceof RpcError && e.appCode === "groupChanged") {
        expected = (e.data as { members?: number } | undefined)?.members ?? expected;
        await useProfiles.getState().refreshGroups();
        continue;
      }
      toast("error", e instanceof Error ? e.message : String(e));
      return false;
    }
  }
  return false;
}

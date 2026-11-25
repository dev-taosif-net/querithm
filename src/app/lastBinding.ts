// The connection shown while no query tab is in front: the top bar names it, a new query opens on it, and Go to object
// (Ctrl+T) and the Explorer's search read its database, so all of them agree on one target.
import { useConnections } from "../store/connections";
import { useProfiles } from "../store/profiles";
import { useTabs, type TabBinding } from "../store/tabs";

/**
 * With no query tab in front (every tab closed, a tool tab active): the server and database the last tab was on, unless
 * the user disconnected that server since or its profile is gone (a new query does not inherit either). Undefined while
 * a query tab is in front (`tabInFront`): that tab is the target then.
 */
export function useLastBinding(tabInFront: boolean): TabBinding | undefined {
  const last = useTabs((s) => (tabInFront ? undefined : s.lastBinding));
  const id = last?.profileId;
  const offline = useConnections((s) => !!id && !!s.disconnected[id]);
  const exists = useProfiles((s) => !!id && s.profiles.some((p) => p.id === id));
  return id && exists && !offline ? last : undefined;
}

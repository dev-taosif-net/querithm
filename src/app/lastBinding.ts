// The connection shown while no query tab is in front: the top bar, status bar and Object Explorer name it, a new
// query opens on it, Go to object (Ctrl+T), Explorer search, ERD, Compare, Import, Activity monitor and Snippets read
// it, so all of them agree on one target.
import { useMemo } from "react";
import { useConnections } from "../store/connections";
import { useProfiles } from "../store/profiles";
import { useTabs, type TabBinding } from "../store/tabs";

/**
 * With no query tab in front (every tab closed, a tool tab active): the server and database the last tab was on
 * (falling back to the profile's or connected server's default database when the tab had none set), unless the user
 * disconnected that server since or its profile is gone (a new query does not inherit either). Undefined while a query
 * tab is in front (`tabInFront`): that tab is the target then.
 */
export function useLastBinding(tabInFront: boolean): TabBinding | undefined {
  const last = useTabs((s) => (tabInFront ? undefined : s.lastBinding));
  const id = last?.profileId;
  const offline = useConnections((s) => !!id && !!s.disconnected?.[id]);
  const serverDb = useConnections((s) => (id ? (s.servers?.[id]?.database ?? undefined) : undefined));
  const profile = useProfiles((s) => (id ? s.profiles.find((p) => p.id === id) : undefined));
  const database = last?.database ?? profile?.databaseDefault ?? serverDb;
  return useMemo(() => {
    if (!id || !profile || offline) return undefined;
    return database === last?.database ? last : { profileId: id, database };
  }, [id, profile, offline, database, last]);
}

/** Non-hook counterpart of `useLastBinding` for commands and actions outside React render. */
export function lastBindingTarget(): TabBinding | undefined {
  const s = useTabs.getState();
  if (s.activeId) return undefined;
  const last = s.lastBinding;
  const id = last?.profileId;
  const conn = useConnections.getState();
  if (!id || conn.disconnected?.[id]) return undefined;
  const profile = useProfiles.getState().byId(id);
  if (!profile) return undefined;
  const database = last.database ?? profile.databaseDefault ?? conn.servers?.[id]?.database ?? undefined;
  return database === last.database ? last : { profileId: id, database };
}

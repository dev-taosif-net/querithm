// Which server and database a new query tab starts on (§6): what the caller asked for, else the active tab's.

export interface Binding {
  profileId?: string;
  database?: string;
}

/**
 * `requested.profileId`: undefined = inherit from the active tab, otherwise that profile (or none when empty).
 * A server the user disconnected is never inherited: the new tab starts unbound instead of on a dead connection.
 * `defaultDb`: the database a tab on another server starts in (see `serverDatabase` in actions).
 */
export function newTabBinding(requested: Binding, active: Binding | undefined, disconnected: (profileId: string) => boolean, defaultDb: (profileId: string) => string | undefined): Binding {
  const inherit = requested.profileId === undefined && !!active?.profileId && !disconnected(active.profileId);
  const profileId = requested.profileId || (inherit ? active!.profileId : undefined);
  if (!profileId) return {};
  const database = requested.database ?? (profileId === active?.profileId ? active.database : undefined) ?? defaultDb(profileId);
  return { profileId, database };
}

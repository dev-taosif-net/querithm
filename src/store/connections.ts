import { create } from "zustand";
import type { Capabilities, CapabilityInfo, DatabaseInfo, Profile, SshHostKey } from "../ipc/contract";
import { rpc, RpcError, shell } from "../ipc/client";
import { ask } from "./ui";
import { useProfiles } from "./profiles";
import { locationOf, needsSecretFor } from "../connections/auth";
import { needsRepick } from "../connections/fileGrants";

/** Why an in-memory profile shows no databases: every tab session opens its own, so there is nothing shared to list. */
export const MEMORY_NOTE = "In-memory: each tab has its own database";

/** A connect refused because the file's grant is gone (moved, replaced, revoked): the user picks the file again. */
export const REPICK_HINT = "Pick the file again: Edit profile ▸ Re-pick file…";

/** A server in use: connecting, connected or failed. The Object Explorer lists these and the pinned profiles (§12). */
export interface ServerConn {
  profileId: string;
  /** Connected in the background (startup reconnect, engine restart): the Explorer does not open it a level. */
  quiet?: boolean;
  /** The explorer's own session (definitions, database list, scripting). Tabs have their own. */
  sessionId?: string;
  status: "connecting" | "connected" | "error";
  error?: string;
  serverVersion?: string | null;
  /** The database the explorer session is in: the profile default, else the login's default as the server reports it. */
  database?: string;
  databases: DatabaseInfo[];
  caps?: Capabilities;
  /** An in-memory profile: shown connected without a session of its own (one would be a new, empty database). */
  memory?: boolean;
  /** The connect failed on the file grant; the Explorer offers "Re-pick file…". */
  repick?: boolean;
}

/** The features a server's engine can lack (session.capabilities), each with the sidecar's reason. */
export type CapabilityFeature = { [K in keyof Capabilities]-?: Capabilities[K] extends CapabilityInfo ? K : never }[keyof Capabilities];

/**
 * Why a feature is off on a server's engine, as its explorer session's capabilities say (SQLite and DuckDB: no schema or
 * data compare), or undefined when it is available or not known yet (an in-memory profile has no explorer session: the
 * sidecar's refusal of the call says why). The sidecar refuses a crafted call with the same reason.
 */
export function capabilityRefusal(servers: Record<string, ServerConn>, profileId: string | undefined, feature: CapabilityFeature): string | undefined {
  const c = profileId ? servers[profileId]?.caps?.[feature] : undefined;
  return c && !c.available ? (c.reason ?? "Not available on this engine.") : undefined;
}

/** The user disconnected this server: nothing of it (databases, schema, completion) is shown until they reconnect. */
export function isDisconnected(profileId: string | undefined): boolean {
  return !!profileId && !!useConnections.getState().disconnected[profileId];
}

export function needsSecret(p: Profile) {
  return needsSecretFor(p.auth);
}

/**
 * Makes sure the sidecar can authenticate this profile: if the keyring has no secret, ask for one.
 * Returns a secret to pass explicitly (when the user chose not to save it), or undefined when the keyring has it.
 */
export async function obtainSecret(p: Profile, error?: string): Promise<string | undefined | null> {
  if (!needsSecret(p)) return undefined;
  if (!error && (await shell.secretExists(p.id).catch(() => false))) return undefined;
  const result = await ask<string | null>((resolve) => ({ kind: "password", profileId: p.id, resolve, error }));
  return result; // null = cancelled
}

interface ConnStore {
  servers: Record<string, ServerConn>;
  order: string[];
  /** Servers the user disconnected; running a query on one asks before reconnecting. Cleared on connect. */
  disconnected: Record<string, true>;
  /** `quiet`: never asks (password, SSH key/secret); a server needing input is left in the error state to Connect. */
  connect(profileId: string, opts?: { quiet?: boolean }): Promise<boolean>;
  disconnect(profileId: string): Promise<void>;
  refreshDatabases(profileId: string): Promise<void>;
  sessionFor(profileId: string): Promise<string>;
  resetSessions(): void;
  /**
   * The profile's file cannot be opened until it is picked again (its grant was revoked, or the file moved, was
   * replaced or is gone): no session, the error shown, and the Explorer offers "Re-pick file…".
   */
  markNeedsFile(profileId: string, error: string): void;
}

function isAuthError(e: unknown) {
  if (!(e instanceof RpcError)) return false;
  return e.appCode === "needsSecret" || /login failed|password authentication failed|18456/i.test(e.message);
}

export const useConnections = create<ConnStore>((set, get) => {
  const put = (id: string, patch: Partial<ServerConn>) =>
    set((s) => ({
      servers: { ...s.servers, [id]: { ...(s.servers[id] ?? { profileId: id, status: "connecting", databases: [] }), ...patch } },
      order: s.order.includes(id) ? s.order : [...s.order, id],
    }));

  /** Patches only a server still on the same explorer session: a late reply must not resurrect a disconnected one. */
  const patchLive = (id: string, sessionId: string, patch: Partial<ServerConn>) => {
    if (get().servers[id]?.sessionId === sessionId && !get().disconnected[id]) put(id, patch);
  };

  /** One connect attempt (prompts, retries, SSH host key). */
  const dial = async (profileId: string, opts?: Parameters<ConnStore["connect"]>[1]): Promise<boolean> => {
    const profile = useProfiles.getState().byId(profileId);
    if (!profile) return false;
    // A profile this build cannot read (newer build, unavailable engine) is never dialed; the sidecar refuses it as well.
    if (profile.unsupported) {
      put(profileId, { status: "error", error: profile.unsupported });
      return false;
    }
    const quiet = !!opts?.quiet;
    if (get().disconnected[profileId]) set((s) => ({ disconnected: omit(s.disconnected, profileId) }));
    // In memory, each tab session is its own database: the Explorer opens none (it would be another, empty one).
    if (locationOf(profile) === "memory") {
      put(profileId, { status: "connected", error: undefined, quiet, sessionId: undefined, databases: [], memory: true, repick: false });
      return true;
    }
    put(profileId, { status: "connecting", error: undefined, quiet, repick: false });
    const needsInput = (why: string) => {
      if (!get().disconnected[profileId] && get().servers[profileId]) put(profileId, { status: "error", error: `${why} Reconnect to continue.` });
      return false;
    };
    if (quiet && needsSecret(profile) && !(await shell.secretExists(profileId).catch(() => false))) return needsInput("The password is not saved.");
    let error: string | undefined;
    let sshSecret: string | undefined;
    for (let attempt = 0; attempt < 4; attempt++) {
      const secret = await obtainSecret(profile, error);
      if (secret === null) {
        if (!get().servers[profileId]?.sessionId) set((s) => ({ order: s.order.filter((x) => x !== profileId), servers: omit(s.servers, profileId) }));
        return false;
      }
      let openedId: string | undefined;
      try {
        const opened = await rpc("session.open", { profileId, database: profile.databaseDefault ?? null, secret: secret ?? null, sshSecret: sshSecret ?? null });
        openedId = opened.sessionId;
        const state = await rpc("session.state", { sessionId: opened.sessionId }); // dials (§6 rule 1)
        const databases = await rpc("meta.databases", { sessionId: opened.sessionId });
        if (get().disconnected[profileId] || !get().servers[profileId]) {
          // Disconnected while dialing: drop the session instead of bringing the server back.
          void rpc("session.close", { sessionId: opened.sessionId, force: true }).catch(() => {});
          return false;
        }
        put(profileId, { sessionId: opened.sessionId, status: "connected", serverVersion: state.serverVersion, database: state.database ?? undefined, databases });
        void rpc("session.capabilities", { sessionId: opened.sessionId }).then((caps) => patchLive(profileId, opened.sessionId, { caps })).catch(() => {});
        return true;
      } catch (e) {
        // A session opened but never connected (lazy) is closed before retrying.
        if (openedId) void rpc("session.close", { sessionId: openedId, force: true }).catch(() => {});
        if (get().disconnected[profileId] || !get().servers[profileId]) return false;
        const msg = e instanceof Error ? e.message : String(e);
        // SSH tunnel: an untrusted bastion key waits for the user; a missing SSH secret is asked for.
        if (quiet && e instanceof RpcError && (e.appCode === "sshHostKeyUnknown" || e.appCode === "sshHostKeyChanged" || e.appCode === "sshNeedsSecret")) return needsInput(msg);
        if (quiet && isAuthError(e) && needsSecret(profile)) return needsInput(msg);
        if (e instanceof RpcError && (e.appCode === "sshHostKeyUnknown" || e.appCode === "sshHostKeyChanged")) {
          const hostKey = e.data as SshHostKey;
          if (await ask<boolean>((resolve) => ({ kind: "hostKey", hostKey, resolve }))) {
            await rpc("ssh.trustHostKey", hostKey);
            continue;
          }
          put(profileId, { status: "error", error: msg });
          return false;
        }
        if (e instanceof RpcError && e.appCode === "sshNeedsSecret") {
          const typed = await ask<string | null>((resolve) => ({ kind: "password", profileId, resolve, error: msg, ssh: true }));
          if (typed === null) {
            put(profileId, { status: "error", error: msg });
            return false;
          }
          sshSecret = typed || undefined; // undefined: stored in the keyring, Rust injects it
          continue;
        }
        if (isAuthError(e) && needsSecret(profile)) {
          error = msg;
          continue;
        }
        if (e instanceof RpcError && needsRepick(e.appCode)) {
          put(profileId, { status: "error", error: `${msg} ${REPICK_HINT}`, repick: true });
          return false;
        }
        put(profileId, { status: "error", error: msg });
        return false;
      }
    }
    if (!get().disconnected[profileId] && get().servers[profileId]) put(profileId, { status: "error", error });
    return false;
  };

  // A connect in flight per profile: a session-bound action during a reconnect waits for it instead of dialing again
  // (a second dial races the first, orphans one of the two sessions and can ask for the password twice).
  const inflight = new Map<string, Promise<boolean>>();

  return {
    servers: {},
    order: [],
    disconnected: {},
    connect(profileId, opts) {
      const run = dial(profileId, opts);
      inflight.set(profileId, run);
      const clear = () => void (inflight.get(profileId) === run && inflight.delete(profileId));
      run.then(clear, clear);
      return run;
    },
    async disconnect(profileId) {
      await shell.secretEndConnection(profileId);
      const s = get().servers[profileId];
      if (s?.sessionId) await rpc("session.close", { sessionId: s.sessionId, force: true }).catch(() => {});
      set((st) => ({ servers: omit(st.servers, profileId), order: st.order.filter((x) => x !== profileId), disconnected: { ...st.disconnected, [profileId]: true } }));
    },
    async refreshDatabases(profileId) {
      if (get().servers[profileId]?.memory) return;
      const sid = await get().sessionFor(profileId);
      const databases = await rpc("meta.databases", { sessionId: sid });
      patchLive(profileId, sid, { databases });
    },
    async sessionFor(profileId) {
      const s = get().servers[profileId];
      if (s?.sessionId) return s.sessionId;
      const profile = useProfiles.getState().byId(profileId);
      if (profile && locationOf(profile) === "memory") throw new Error(`${MEMORY_NOTE}, so there is no shared one to read. Use a query tab.`);
      // An explicit disconnect is never undone behind the user's back (object scripting, definitions, dependencies).
      if (get().disconnected[profileId]) throw new Error("Disconnected from this server. Connect it again to continue.");
      const pending = inflight.get(profileId);
      if (pending) {
        if ((await pending) && get().servers[profileId]?.sessionId) return get().servers[profileId]!.sessionId!;
        throw new Error("Not connected");
      }
      if (await get().connect(profileId)) return get().servers[profileId]!.sessionId!;
      throw new Error("Not connected");
    },
    resetSessions() {
      // After a sidecar restart every session id is gone; reconnect lazily. A server that was not connected (e.g. a
      // startup reconnect waiting for its password) reconnects quietly: a crash must not open a dialog per server.
      const wasConnected = new Set(get().order.filter((id) => get().servers[id]?.status === "connected"));
      set((s) => ({
        servers: Object.fromEntries(Object.entries(s.servers).map(([k, v]) => [k, { ...v, sessionId: undefined, status: "connecting" as const }])),
      }));
      for (const id of get().order) void get().connect(id, { quiet: !wasConnected.has(id) });
    },
    markNeedsFile(profileId, error) {
      // The Explorer's own session on the old file (if still open) is closed rather than left behind.
      const old = get().servers[profileId]?.sessionId;
      if (old) void rpc("session.close", { sessionId: old, force: true }).catch(() => {});
      put(profileId, { status: "error", error, repick: true, sessionId: undefined, databases: [], caps: undefined, memory: undefined });
    },
  };
});

function omit<T>(o: Record<string, T>, k: string): Record<string, T> {
  const { [k]: _, ...rest } = o;
  return rest;
}

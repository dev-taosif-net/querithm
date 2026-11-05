// Profile sign-in rules shared by the connection store and the profile editor (Phase D0a). The sidecar's
// ProfileRules.NeedsSecret makes the same decision; both enumerate every AuthKind, so a new kind cannot compile
// here without a decision.
import type { AuthKind, LocationKind, Profile } from "../ipc/contract";

const NEEDS_SECRET: Record<AuthKind, boolean> = {
  sql: true,
  password: true,
  entraToken: true,
  windows: false,
  kerberos: false,
  entraInteractive: false,
  entraDeviceCode: false,
  none: false,
};

/** The sign-in sends a password or token the shell keeps (keyring or memory). Unknown text (a newer build) needs none. */
export function needsSecretFor(auth: AuthKind): boolean {
  return NEEDS_SECRET[auth] ?? false;
}

export function locationOf(p: Pick<Profile, "location">): LocationKind {
  return p.location ?? "server";
}

/** A database on this computer (a file or memory): no user, password, TLS or SSH fields; environment and Prod guards stay. */
export function isLocal(p: Pick<Profile, "location">): boolean {
  return locationOf(p) !== "server";
}

/**
 * A copy of a profile for "Duplicate profile…": a new id and name, and never the original's file grant (the shell
 * binds one grant to one profile and refuses a second; the copy picks its file again).
 */
export function duplicateProfile(p: Profile): Profile {
  return { ...p, id: "", name: `${p.name} (copy)`, file: null, unsupported: null, createdAt: 0, updatedAt: 0 };
}

/** Everything a saved secret is bound to: where it is sent, as whom, and how the connection is protected. */
export function endpointKey(p: Profile): string {
  const rawHost = (p.host ?? "").trim();
  // PostgreSQL also accepts filesystem and abstract Unix sockets. Their names are case sensitive.
  const socket = p.engine === "pg" && rawHost.split(",").some((h) => /^[\/@]/.test(h.trimStart()));
  const host = (socket ? rawHost : rawHost.toLowerCase()) || null;
  return JSON.stringify([
    p.engine, isLocal(p) ? null : host, p.port ?? null, p.username ?? null, p.auth, p.tlsMode, !!p.trustCert, p.caPath ?? null,
    p.options?.hostNameInCertificate ?? null,
    p.ssh ? [p.ssh.host.trim().toLowerCase(), p.ssh.port ?? 22, p.ssh.username, p.ssh.auth, p.ssh.keyPath ?? null] : null,
  ]);
}

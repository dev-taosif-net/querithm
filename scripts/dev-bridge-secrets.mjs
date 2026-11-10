// Which secrets scripts/dev-bridge.mjs injects into session.open / profile.test, mirroring
// src-tauri/src/keyring.rs `wants` (Phase D0a): a sign-in without a secret and a profile without an SSH tunnel never
// look one up, so they open with an empty or failing secret store.

/**
 * [database secret, SSH secret] a profile signs in with; a profile not listed yet is looked up as before.
 * A missing auth field is a legacy SQL login.
 * @param {{ auth?: string | null, ssh?: unknown } | undefined | null} profile
 * @returns {[boolean, boolean]}
 */
export function wantedSecrets(profile) {
  if (!profile) return [true, true];
  return [["sql", "password", "entraToken"].includes(profile.auth ?? "sql"), !!profile.ssh];
}


/** Connection identity and protection, mirroring src-tauri/src/credential_guard.rs. */
export function sameEndpoint(a, b) {
  const name = (p, k, d) => String(p[k] ?? d).toLowerCase();
  const host = (p) => String(p.host ?? "").trim().toLowerCase();
  const key = (p) => {
    const location = name(p, "location", "server");
    const rawHost = String(p.host ?? "").trim();
    const socket = name(p, "engine", "mssql") === "pg" && rawHost.split(",").some((h) => /^[\/@]/.test(h.trimStart()));
    return JSON.stringify([
      name(p, "engine", "mssql"), location, p.file?.grantId ?? null,
      location === "server" ? socket ? rawHost : host(p) : null, p.port ?? null, p.username ?? null,
      name(p, "auth", "sql"), name(p, "tlsMode", "default"), p.trustCert === true,
      p.caPath ?? null, p.options?.hostNameInCertificate ?? null,
      p.ssh ? [host(p.ssh), p.ssh.port ?? 22, p.ssh.username ?? null, name(p.ssh, "auth", "password"), p.ssh.keyPath ?? null] : null,
    ]);
  };
  return key(a) === key(b);
}

/** One profile's save/delete and credential injection must not race each other. */
export function createProfileLocks() {
  const gates = new Map();
  return async (method, params) => {
    const id = method === "profile.save" ? params?.id
      : ["profile.delete", "session.open", "profile.test"].includes(method) ? params?.profileId : null;
    if (!id) return () => {};
    const previous = gates.get(id);
    let release;
    const current = new Promise((resolve) => { release = resolve; });
    gates.set(id, current);
    await previous;
    return () => {
      if (gates.get(id) === current) gates.delete(id);
      release();
    };
  };
}


/** Only the shell can read or write the :timed envelope accounts. */
export function validCredentialAccount(id) {
  if (typeof id !== "string") return false;
  const base = id.endsWith(":ssh") ? id.slice(0, -4) : id;
  return base.length >= 1 && base.length <= 54 && !/[^A-Za-z0-9_-]/.test(base);
}

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

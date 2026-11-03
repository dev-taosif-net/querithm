// Database-file grants for scripts/dev-bridge.mjs, mirroring src-tauri/src/grants.rs + injection.rs (Phase D0c), so
// browser development and smoke-ui follow the same rules: the webview names a grant id, never a path; a fileGrant it
// sends is dropped; session.open / profile.test of a file profile get the grant injected after the file is checked;
// a grant belongs to the profile that saved it. The browser uploads the database it picked; the bridge keeps that copy
// in a private temp directory, so dev-mode writes never reach the user's original file.
import { randomBytes } from "node:crypto";
import { realpathSync, statSync } from "node:fs";

/** "u:<dev>:<ino>" as the shell and the sidecar compute it (BigInt: inode numbers exceed 2^53). @param {string} path */
export function identityOf(path) {
  const s = statSync(path, { bigint: true });
  if (!s.isFile()) throw new Error("not a file");
  return `u:${s.dev}:${s.ino}`;
}

/** @param {string} code @param {string} message */
const refusal = (code, message) => ({ code: -32001, message, data: { code } });

export function createGrants() {
  /** @type {Map<string, { path: string, identity: string, access: "readOnly" | "readWrite", profileId: string | null }>} */
  const grants = new Map();
  /** @type {Map<string, string | null>} profile id → grant id of a file profile */
  const fileProfiles = new Map();

  /** @param {any} profile */
  const grantOf = (profile) => (profile && profile.location === "file" ? profile.file?.grantId ?? null : null);

  return {
    /** A staged upload becomes a read-only grant. @param {string} path */
    register(path) {
      const canonical = realpathSync(path);
      const id = randomBytes(16).toString("hex");
      grants.set(id, { path: canonical, identity: identityOf(canonical), access: "readOnly", profileId: null });
      return id;
    },
    /** @param {string} id */
    allowWrites(id) {
      const g = grants.get(id);
      if (!g) throw new Error("Unknown file grant.");
      g.access = "readWrite";
    },
    /** @param {string} id */
    readOnly(id) {
      const g = grants.get(id);
      if (!g) throw new Error("Unknown file grant.");
      g.access = "readOnly";
    },
    /** @param {string} id */
    get(id) {
      return grants.get(id);
    },
    /** Every listed or saved profile. @param {any} profile */
    remember(profile) {
      if (profile?.id) fileProfiles.set(profile.id, grantOf(profile));
    },
    /** @param {string} id @param {string | null | undefined} profileId */
    resolve(id, profileId) {
      const g = grants.get(id);
      if (!g) return { error: refusal("fileGrantUnknown", "This connection has no access to its database file any more. Pick the file again (Re-pick file…).") };
      if (g.profileId && g.profileId !== profileId) return { error: refusal("fileGrantBound", "That file was picked for another connection. Pick it again for this one (Re-pick file…).") };
      let identity;
      try {
        if (realpathSync(g.path) !== g.path) return { error: refusal("fileMoved", "The database file is no longer at the path it was picked from. Pick it again (Re-pick file…).") };
        identity = identityOf(g.path);
      } catch {
        return { error: refusal("fileMissing", "The database file is gone. Pick it again (Re-pick file…).") };
      }
      if (identity !== g.identity) return { error: refusal("fileReplaced", "The file at that path is not the one that was picked (it was replaced). Pick it again (Re-pick file…).") };
      return { grant: { grantId: id, path: g.path, mode: g.access, identity } };
    },
    /**
     * Before the call: drops a webview fileGrant everywhere, refuses a profile.save that would take another profile's
     * grant, injects the grant into session.open / profile.test. Returns an error object or null.
     * @param {string} method @param {any} params
     */
    inject(method, params) {
      if (!params || typeof params !== "object") return null;
      delete params.fileGrant;
      if (method === "profile.save") {
        const id = grantOf(params);
        const g = id ? grants.get(id) : undefined;
        if (id && !g) return refusal("fileGrantUnknown", "Pick the database file again (Re-pick file…).");
        if (g?.profileId && g.profileId !== (params.id || null)) return refusal("fileGrantBound", "That file was picked for another connection. Pick it again for this one (Re-pick file…).");
        return null;
      }
      if (method !== "session.open" && method !== "profile.test") return null;
      // profile.test tests the edited profile it carries; session.open uses the stored one only (M1).
      const edited = method === "profile.test" && params.profile && typeof params.profile === "object" ? params.profile : null;
      const id = edited ? grantOf(edited) : fileProfiles.get(params.profileId) ?? null;
      if (!id) return null;
      const r = this.resolve(id, params.profileId ?? null);
      if (r.error) return r.error;
      params.fileGrant = r.grant;
      return null;
    },
    /** After a successful call. @param {string} method @param {any} params @param {any} result */
    settle(method, params, result) {
      if (method === "profile.save") {
        const id = grantOf(result);
        const g = id ? grants.get(id) : undefined;
        if (g && !g.profileId) g.profileId = result.id;
      }
      if (method === "profile.delete" || method === "grant.revoke") {
        for (const [id, g] of grants) if (g.profileId === params?.profileId) grants.delete(id);
        if (method === "profile.delete") fileProfiles.delete(params?.profileId);
      }
    },
  };
}

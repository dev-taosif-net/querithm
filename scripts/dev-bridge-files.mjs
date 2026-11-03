// Picked-file tokens for scripts/dev-bridge.mjs, mirroring src-tauri/src/files.rs so browser development
// follows the same rule: the webview never names a path. The browser uploads the chosen file once; the bridge keeps
// it in a private temp directory and hands back a token; import.* calls get the path injected, forged paths dropped.
import { randomBytes } from "node:crypto";

/** Names only the shell fills in; the webview never sends one (a null is dropped). */
const SHELL_OWNED = ["filePath", "fileGrant"];
/** Names the webview sends itself and the shell reads or fills. */
const SHARED = ["fileToken", "secret", "sshSecret"];
const fold = (/** @type {string} */ s) => s.toUpperCase();

/**
 * Mirrors injection.rs `refuse_shell_keys` (M1): the params, their `source` / `target` sides and their `profile` may
 * not name `filePath` / `fileGrant` (a null is dropped) nor any other spelling of those or of `fileToken`, `secret`,
 * `sshSecret`. Returns the refusal, or null.
 * @param {unknown} params
 */
export function refuseShellKeys(params) {
  if (!params || typeof params !== "object" || Array.isArray(params)) return null;
  /** @param {Record<string, unknown>} obj */
  const check = (obj) => {
    for (const key of Object.keys(obj)) {
      const name = [...SHELL_OWNED, ...SHARED].find((n) => fold(n) === fold(key));
      if (!name) continue;
      if (key !== name || (SHELL_OWNED.includes(name) && obj[key] != null)) {
        return { code: -32001, message: `The request names "${key}", which only the Querithm shell sets.`, data: { code: "shellOwned" } };
      }
      if (SHELL_OWNED.includes(name)) delete obj[key];
    }
    return null;
  };
  const top = /** @type {Record<string, unknown>} */ (params);
  for (const obj of [top, top.source, top.target, top.profile]) {
    if (obj && typeof obj === "object" && !Array.isArray(obj)) {
      const refused = check(/** @type {Record<string, unknown>} */ (obj));
      if (refused) return refused;
    }
  }
  return null;
}

const noToken = () => ({ code: -32001, message: "No file was picked for this call. Pick the file with the file dialog.", data: { code: "fileToken" } });

export function createFileTokens() {
  /** @type {Map<string, { path: string, used: boolean, running: boolean }>} */
  const map = new Map();
  return {
    /** @param {string} path */
    register(path) {
      const token = randomBytes(16).toString("hex");
      map.set(token, { path, used: false, running: false });
      return token;
    },
    /**
     * The path of a live token. `run` (import.run) holds the token until {@link settleRun}: a second import.run is
     * refused meanwhile, and the token is spent only once an import really started (a Production guard block or a
     * failed call leaves it usable for the retry).
     * @param {string} token @param {boolean} run
     */
    resolve(token, run) {
      const p = map.get(token);
      if (!p || p.used || (run && p.running)) return null;
      if (run) p.running = true;
      return p.path;
    },
    /** Ends the import.run holding the token: `started` (the reply had a jobId) spends it. @param {unknown} token @param {boolean} started */
    settleRun(token, started) {
      const p = typeof token === "string" ? map.get(token) : undefined;
      if (!p) return;
      p.running = false;
      if (started) p.used = true;
    },
    /**
     * Strips any filePath from import.inspect / import.run params and injects the path of their fileToken. Returns an
     * error object (as the Rust relay does) when there is no token, or it is unknown or already used by an import (M1:
     * never passed on without a path).
     * @param {string} method @param {Record<string, unknown> | null} params
     */
    inject(method, params) {
      if (method === "import.inspect" || method === "import.run") {
        if (!params || typeof params !== "object") return noToken();
        return this.injectOne(params, method === "import.run");
      }
      if (!params || typeof params !== "object") return null;
      if (method.startsWith("schemaCompare.")) {
        // Plan M5: each side of a compare may be a saved model file; a live side (a session) names none.
        for (const side of ["source", "target"]) {
          const v = /** @type {Record<string, unknown> | undefined} */ (params[side]);
          if (v && typeof v === "object") {
            delete v.filePath;
            if (v.fileToken == null) continue;
            const err = this.injectOne(v, false);
            if (err) return err;
          }
        }
      }
      return null;
    },
    /** @param {Record<string, unknown>} params @param {boolean} run */
    injectOne(params, run) {
      delete params.filePath;
      if (typeof params.fileToken !== "string") return noToken();
      const path = this.resolve(params.fileToken, run);
      if (path == null) return { code: -32001, message: "The picked file is no longer available. Pick it again.", data: { code: "fileToken" } };
      params.filePath = path;
      return null;
    },
  };
}

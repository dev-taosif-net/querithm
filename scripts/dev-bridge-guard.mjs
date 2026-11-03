// Request guards for scripts/dev-bridge.mjs, kept side-effect free so they can be unit tested.
//
// Browsers let any page open a WebSocket to 127.0.0.1, so binding to loopback alone is not enough: without an
// Origin check a site the developer happens to visit could drive the sidecar (and the bridge injects stored
// secrets into session.open). Only the Vite dev server's own origin is accepted.

/** @param {number} uiPort */
export function allowedOrigins(uiPort) {
  return new Set([`http://127.0.0.1:${uiPort}`, `http://localhost:${uiPort}`]);
}

/**
 * True when a WebSocket upgrade's Origin header names the dev UI. A missing Origin is rejected too: every browser
 * sends one, and the bridge has no non-browser clients.
 * @param {string | undefined} origin
 * @param {number} uiPort
 */
export function isAllowedOrigin(origin, uiPort) {
  return typeof origin === "string" && allowedOrigins(uiPort).has(origin);
}

/**
 * Parses one client message; returns null instead of throwing so a malformed frame cannot crash the bridge.
 * @param {unknown} raw
 * @returns {{ id: number, cmd: string, args: Record<string, unknown> } | null}
 */
export function parseMessage(raw) {
  let msg;
  try {
    msg = JSON.parse(String(raw));
  } catch {
    return null;
  }
  if (msg == null || typeof msg !== "object" || !Number.isInteger(msg.id) || msg.id < 0 || msg.id > 0xffffffff || typeof msg.cmd !== "string")
    return null;
  const args = msg.args != null && typeof msg.args === "object" ? msg.args : {};
  return { id: msg.id, cmd: msg.cmd, args };
}

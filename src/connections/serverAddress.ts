// The server a profile points at, as the user typed it (host or IP, never resolved: a lookup would put the webview on
// the network and a load balancer's address changes under it). Shown in the status bar and the tab tooltip, because a
// profile's name can say "billing-dev" while it points at production.
import type { Profile } from "../ipc/contract";
import { engineTraits } from "../engines";
import { isLocal } from "./auth";

type AddressFacts = Pick<Profile, "engine" | "location" | "host" | "port">;

/**
 * "10.0.4.12", "db01.local:5433", "[fd00::5]:5433": the port only when it is not the engine's default. Undefined for a
 * file or in-memory database (no server) and for a server profile with no host.
 */
export function serverAddress(p: AddressFacts | undefined): string | undefined {
  if (!p || isLocal(p)) return undefined;
  const host = p.host?.trim();
  if (!host) return undefined;
  const port = p.port != null && p.port !== engineTraits(p.engine).defaultPort ? p.port : undefined;
  if (port == null) return host;
  // An IPv6 literal takes brackets before a port, or "fd00::5:5433" would read as one address.
  return `${host.includes(":") && !host.startsWith("[") ? `[${host}]` : host}:${port}`;
}

/** "Server 10.0.4.12:1444 via SSH jump-01": the address line of a tooltip; undefined when there is no server. */
export function serverAddressLine(p: (AddressFacts & Pick<Profile, "ssh">) | undefined): string | undefined {
  const addr = serverAddress(p);
  if (!addr) return undefined;
  const ssh = p?.ssh?.host?.trim();
  return `Server ${addr}${ssh ? ` via SSH ${ssh}` : ""}`;
}

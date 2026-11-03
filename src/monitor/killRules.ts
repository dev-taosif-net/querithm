// Why a kill / cancel button is disabled (U4). The sidecar enforces every rule again (KillGuard); this only explains.
import type { ActivityRow, KillMode, MonitorCapabilities } from "../ipc/contract";

export const QUERITHM_CONNECTION = "This is a Querithm connection — cancel or disconnect it from its tab.";

export interface Availability {
  enabled: boolean;
  reason?: string;
  /** A Querithm connection leased by a tab: offer Go to tab instead. */
  goToSession?: string;
}

export function killAvailability(row: ActivityRow | undefined, caps: MonitorCapabilities | null, mode: KillMode, connected: boolean): Availability {
  if (!row) return { enabled: false, reason: "Select a session." };
  if (!connected) return { enabled: false, reason: "The monitor is disconnected." };
  if (row.key.id < 0) return { enabled: false, reason: "Not a session." };
  if (row.querithmKind) {
    if (row.querithmKind === "monitor" || row.querithmKind === "control") return { enabled: false, reason: "This is the monitor's own connection." };
    return { enabled: false, reason: QUERITHM_CONNECTION, goToSession: row.ownSessionId ?? undefined };
  }
  if (row.identityHidden || !row.key.instance) return { enabled: false, reason: `Its identity is hidden (${row.identityHidden ?? "not visible"}), so it cannot be re-checked on the server.` };
  if (!row.isUserSession) return { enabled: false, reason: "System session." };
  if (row.rollingBack) return { enabled: false, reason: "Already rolling back." };
  if (caps?.otherLoginsReason && caps.login && row.login && row.login !== caps.login) return { enabled: false, reason: caps.otherLoginsReason };
  if (mode === "cancelRequest") {
    if (caps && !caps.canCancelRequest) return { enabled: false, reason: caps.cancelReason ?? "Cancelling a single request is not supported." };
    const running = row.statementStart != null || ["running", "runnable", "suspended", "active"].includes(row.status);
    if (!running) return { enabled: false, reason: "No running query." };
    return { enabled: true };
  }
  if (caps && !caps.canTerminate) return { enabled: false, reason: caps.terminateReason ?? "Terminating sessions is not allowed for this login." };
  return { enabled: true };
}

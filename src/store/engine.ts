// The engine's state as the shell last reported it (§4 step 3: running, dead and restarting, restarted, failed). Its own
// module so actions.ts can read it without importing engineStatus.ts, which imports actions.ts.
import { create } from "zustand";
import type { SidecarStatus } from "../ipc/client";

export const useEngineStatus = create<SidecarStatus>(() => ({ state: "running" }));

/**
 * The engine is gone: killed as unresponsive or exited ("dead", a restart follows) or not running at all ("failed").
 * Its sessions went with it, and with them every open transaction (the server rolls back when a connection drops).
 */
export function engineGone(): boolean {
  const s = useEngineStatus.getState().state;
  return s === "dead" || s === "failed";
}

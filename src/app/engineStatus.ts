import { onNotify, onSidecarStatus, rpc, toRpcError } from "../ipc/client";
import { toast, useUi } from "../store/ui";
import type { SystemNotice } from "../ipc/contract";
import { onSidecarFailed, onSidecarRestarted } from "./actions";
import { shell } from "../ipc/client";
import { retrySettingsSave } from "../store/settings";
import { useEngineStatus } from "../store/engine";

export { useEngineStatus };

/** Shown while the engine has no reader for result rows (§5 data plane); cleared when one attaches. */
export const DATA_PLANE_LOST =
  "Results can't be shown: the window lost its connection to the engine's results channel. Restart the engine to reconnect it.";

/**
 * The sidecar says when frames find no reader (`dataPlane.state`): runs then fail with the reason, and the banner offers
 * the way out; it goes away by itself when the shell reconnects.
 */
function installDataPlaneStatus() {
  return onNotify("dataPlane.state", (e) => {
    const ui = useUi.getState();
    if (!e.connected) ui.setBanner({ kind: "error", text: DATA_PLANE_LOST, action: { label: "Restart engine", run: () => void shell.restart() } });
    else if (ui.banner?.text === DATA_PLANE_LOST) ui.setBanner(null);
  });
}

/** The engine banner of a stopped engine: the shell's reason, what the tabs lost with it (§4 step 3), and the way out. */
export function showEngineFailed(detail?: string | null, lost: readonly string[] = []) {
  const text = [detail ?? "The Querithm engine stopped.", ...lost].join(" ");
  useUi.getState().setBanner({ kind: "error", text, action: { label: "Restart engine", run: () => void shell.restart() } });
}

/** The engine never started (M18): the shell kept its reason, which the window shows with Restart engine. */
export class EngineDown extends Error {}

/**
 * Why `system.ping` failed at boot: when the engine is down and the shell says it failed to start (its spawn error, kept
 * by the supervisor because the status event went out before this window listened), an {@link EngineDown} with that
 * reason; any other failure as it was.
 */
export async function whyEngineDown(e: unknown): Promise<unknown> {
  if (toRpcError(e).appCode !== "sidecarDown") return e;
  const s = await Promise.resolve().then(() => shell.status()).catch(() => null);
  return s?.state === "failed" ? new EngineDown(s.detail ?? "The Querithm engine did not start.") : e;
}

/**
 * M22: what the engine met at start that the user must hear about (a damaged local store set aside), once; shown in the
 * banner with the file's name. Never blocks startup.
 */
export async function showStartupNotices() {
  const notices = await rpc("system.notices").catch(() => null);
  if (!Array.isArray(notices)) return;
  for (const n of notices) useUi.getState().setBanner({ kind: "warn", text: noticeText(n) });
}

const noticeText = (n: SystemNotice) => (n.file ? `${n.message} The damaged file was kept as ${n.file}.` : n.message);

/**
 * A notice the engine met after start (8a review: history retention deleting entries for the first time) is taken with
 * `system.notices` like the startup ones; it never replaces a banner already showing (an engine or results-channel
 * problem), and goes to a toast then.
 */
async function showLaterNotices() {
  const notices = await rpc("system.notices").catch(() => null);
  if (!Array.isArray(notices)) return;
  for (const n of notices) {
    if (useUi.getState().banner) toast("warn", noticeText(n));
    else useUi.getState().setBanner({ kind: "warn", text: noticeText(n) });
  }
}

/** §4 step 3: non-modal banner when the sidecar dies; on restart the webview replays sessions lazily. */
export function installEngineStatus() {
  const offData = installDataPlaneStatus();
  const offNotices = onNotify("system.noticeAdded", () => void showLaterNotices());
  const offStatus = onSidecarStatus((s) => {
    useEngineStatus.setState(s);
    const ui = useUi.getState();
    if (s.state === "dead") ui.setBanner({ kind: "warn", text: "The Querithm engine stopped responding and is restarting…" });
    else if (s.state === "restarted") {
      onSidecarRestarted();
      void retrySettingsSave();
    }
    // W1: an engine that stopped and was not restarted took every session along: the tabs drop theirs (open transactions
    // are said as lost), so closing a tab or quitting no longer asks to end a transaction nothing can reach.
    else if (s.state === "failed") showEngineFailed(s.detail, onSidecarFailed());
  });
  return () => {
    offData();
    offNotices();
    offStatus();
  };
}

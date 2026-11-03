// Quitting (H4, §4): closing the window (title-bar X, Alt+F4, the OS quitting the app) is a close request the shell holds
// until the webview answers. Before it says "close", every tab is settled the way closing it would be (a running query,
// staged grid edits, an open or possibly open transaction), unsaved work the bulk close asks about is confirmed, and the
// writes still waiting on their debounce go out. The shell closes anyway when the webview does not acknowledge the
// request in time (hung or crashed), and a second request while no dialog is showing closes at once.
import { shell } from "../ipc/client";
import { transport } from "../ipc/transport";
import { pendingIn } from "../results/staging";
import { useSettings } from "../store/settings";
import { useTabs } from "../store/tabs";
import { ask, useUi } from "../store/ui";
import { useUpdates } from "../store/updates";
import { activateTab, settleTab } from "./actions";
import { flushAutosave } from "./persistence";
import { allTabsOrder, textOf } from "./tabActions";
import { unsavedAmong } from "./tabNav";

/** The shell's event for a close request on the main window (src-tauri/src/close.rs). */
export const CLOSE_REQUESTED = "app://close-requested";
/** How long the final writes may take before the window closes without them (a hung file write must not hold it). */
export const FLUSH_LIMIT_MS = 5_000;

/** Tabs that closing would settle: a running query, staged grid edits, an open or possibly open transaction. */
export function tabsToSettle(order: readonly string[]): string[] {
  const { tabs } = useTabs.getState();
  return order.filter((id) => {
    const t = tabs[id];
    return !!t && (t.busy || (!!t.sessionId && t.txState !== "none") || pendingIn(t.executions).length > 0);
  });
}

/**
 * Tabs whose work quitting loses: with autosave on, only files changed since they were saved (the text comes back in its
 * tab, the file on disk does not change); with autosave off, every tab whose SQL exists only there (as a bulk close asks).
 */
export function quitLosses(order: readonly string[]): string[] {
  const { tabs } = useTabs.getState();
  const unsaved = unsavedAmong(order, tabs, textOf);
  return useSettings.getState().autosave ? unsaved.filter((id) => !!tabs[id]?.filePath) : unsaved;
}

/** Asked when the window is closed while an update installs (R1-8): quitting then can cut the new version's files. */
export const INSTALLING_BODY =
  "Querithm is installing an update and restarts by itself when it is done. Quitting now can stop the installer half-way and leave Querithm damaged (reinstall it then).";

/** Settles every tab and confirms what would be lost; false: the user chose to keep Querithm open. */
export async function prepareQuit(): Promise<boolean> {
  // R1-8: the shell installs the package (and on Linux replaces the AppImage) after the engine stopped; a close confirmed
  // meanwhile would end the app mid-write. Keep waiting is the default; Quit anyway stays possible (never a dead end).
  if (useUpdates.getState().phase === "installing") {
    const quit = await ask<boolean>((resolve) => ({ kind: "confirm", title: "An update is being installed", danger: true, ok: "Quit anyway", body: INSTALLING_BODY, resolve }));
    if (!quit) return false;
  }
  const order = [...new Set([...allTabsOrder(), ...useTabs.getState().order])];
  for (const id of tabsToSettle(order)) {
    if (!useTabs.getState().tabs[id]) continue;
    activateTab(id); // the prompt is about the tab in front of the user
    if (!(await settleTab(id, { quit: true }))) return false;
  }
  const lost = quitLosses(order);
  if (lost.length) {
    const { tabs } = useTabs.getState();
    const autosave = useSettings.getState().autosave;
    const names = lost.slice(0, 5).map((id) => tabs[id]?.title).join(", ") + (lost.length > 5 ? ` and ${lost.length - 5} more` : "");
    const ok = await ask<boolean>((resolve) => ({
      kind: "confirm", title: "Quit Querithm?", danger: !autosave, ok: "Quit",
      body: autosave
        ? `${lost.length === 1 ? "This file has" : `${lost.length} files have`} changes not saved to disk: ${names}. The text comes back in ${lost.length === 1 ? "its tab" : "their tabs"} next time, but the ${lost.length === 1 ? "file keeps" : "files keep"} the old text until you save.`
        : `${lost.length === 1 ? "This tab has" : `${lost.length} tabs have`} SQL that is not saved to a file, and autosave is off, so it will be lost: ${names}.`,
      resolve,
    }));
    if (!ok) return false;
  }
  return true;
}

/** The writes still pending, bounded: the close never waits on a file write that does not finish. */
async function flushBounded() {
  let timer: ReturnType<typeof setTimeout> | undefined;
  await Promise.race([
    flushAutosave().catch((e) => console.error("[quit] final writes failed", e)),
    new Promise<void>((resolve) => (timer = setTimeout(resolve, FLUSH_LIMIT_MS))),
  ]);
  clearTimeout(timer);
}

let handling: Promise<void> | null = null;

/** One close request from the shell. Exported for tests. */
export async function onCloseRequested(): Promise<void> {
  await shell.closeAck().catch(() => {});
  if (handling) {
    // Asked again while the first request is still being handled. A dialog showing is that request's question; without
    // one (a query being cancelled, files being written) the user means it: close now.
    if (!useUi.getState().dialogs.length) {
      await flushBounded();
      await shell.closeAnswer(true).catch(() => {});
    }
    return;
  }
  handling = (async () => {
    let close = true;
    try {
      close = await prepareQuit();
    } catch (e) {
      // The window must still close when the check itself fails.
      console.error("[quit] the close check failed; closing anyway", e);
    }
    if (close) await flushBounded();
    await shell.closeAnswer(close).catch(() => {});
  })().finally(() => {
    handling = null;
  });
  return handling;
}

/** Listens for the shell's close requests. Installed first at boot, so even an app that failed to start closes at once. */
export function installQuitGuard(): () => void {
  let un: (() => void) | undefined;
  let cancelled = false;
  void transport()
    .listen(CLOSE_REQUESTED, () => void onCloseRequested())
    .then((u) => (cancelled ? u() : (un = u)))
    .catch(() => {});
  return () => {
    cancelled = true;
    un?.();
  };
}

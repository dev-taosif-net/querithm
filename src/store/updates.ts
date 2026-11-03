// In-app updates (§13 rule 10, §16). The shell checks the signed release manifest; the top bar offers Update while a
// newer release exists. Automatic checks stay silent when offline; a manual check always reports its outcome.
import { create } from "zustand";
import { onUpdateProgress, shell, type UpdateFailure, type UpdateInfo } from "../ipc/client";
import { useSettings } from "./settings";
import { toast } from "./ui";

export type UpdatePhase = "idle" | "checking" | "available" | "downloading" | "installing" | "failed";

interface UpdateStore {
  phase: UpdatePhase;
  info: UpdateInfo | null;
  downloaded: number;
  total: number | null;
  error: UpdateFailure | null;
  /** When a check last got an answer (Settings → About: "Up to date · checked 14:02"). */
  checkedAt: number | null;
  /** Why the last check failed, until one succeeds; kind "disabled" marks a development build. */
  checkError: UpdateFailure | null;
}

export const useUpdates = create<UpdateStore>(() => ({ phase: "idle", info: null, downloaded: 0, total: null, error: null, checkedAt: null, checkError: null }));

export const FIRST_CHECK_MS = 10_000;
export const CHECK_EVERY_MS = 6 * 60 * 60 * 1000;
export const DOWNLOAD_PAGE = "https://dev-taosif-net.github.io/querithm-releases/";

const working = (p: UpdatePhase) => p === "checking" || p === "downloading" || p === "installing";

export function asUpdateFailure(e: unknown): UpdateFailure {
  if (e && typeof e === "object" && "message" in e) {
    const o = e as { kind?: unknown; message: unknown };
    return { kind: typeof o.kind === "string" ? o.kind : "install", message: String(o.message) };
  }
  return { kind: "install", message: String(e) };
}

export async function checkForUpdates(manual = false): Promise<void> {
  const before = useUpdates.getState();
  if (working(before.phase)) {
    if (manual) toast("info", "An update is already in progress.");
    return;
  }
  useUpdates.setState({ phase: "checking" });
  try {
    const info = await shell.updateCheck();
    const answered = { checkedAt: Date.now(), checkError: null };
    useUpdates.setState(info ? { phase: "available", info, error: null, ...answered } : { phase: "idle", info: null, error: null, ...answered });
    if (manual && !info) toast("success", "Querithm is up to date.");
  } catch (e) {
    // A failed check keeps whatever was offered before; only a manual check says why (Settings → About shows it too).
    const f = asUpdateFailure(e);
    useUpdates.setState({ phase: before.phase, checkError: f });
    if (manual) {
      toast(f.kind === "disabled" ? "info" : "error", f.message);
    }
  }
}

/**
 * Downloads and verifies the package while the app keeps working, then runs `beforeRestart` (saves the open tabs)
 * and installs. The shell relaunches the app; on failure the current version keeps running and the reason is kept.
 */
export async function installUpdate(beforeRestart: () => Promise<void>): Promise<void> {
  const s = useUpdates.getState();
  if (!s.info || working(s.phase)) return;
  useUpdates.setState({ phase: "downloading", downloaded: 0, total: null, error: null });
  const off = onUpdateProgress((p) => useUpdates.setState({ downloaded: p.downloaded, total: p.total ?? null }));
  try {
    await shell.updateDownload();
  } catch (e) {
    useUpdates.setState({ phase: "failed", error: asUpdateFailure(e) });
    return;
  } finally {
    off();
  }
  useUpdates.setState({ phase: "installing" });
  try {
    await beforeRestart();
  } catch (e) {
    useUpdates.setState({
      phase: "failed",
      error: { kind: "save", message: `The update was not installed because your open tabs could not be saved: ${asUpdateFailure(e).message}` },
    });
    return;
  }
  try {
    await shell.updateInstall();
  } catch (e) {
    useUpdates.setState({ phase: "failed", error: asUpdateFailure(e) });
  }
}

/** First check shortly after start (keeps cold start untouched, §14), then every 6 hours while enabled. */
export function installUpdateChecks(): () => void {
  let timer: ReturnType<typeof setTimeout>;
  const run = () => {
    if (useSettings.getState().autoUpdateCheck) void checkForUpdates();
    timer = setTimeout(run, CHECK_EVERY_MS);
  };
  timer = setTimeout(run, FIRST_CHECK_MS);
  return () => clearTimeout(timer);
}

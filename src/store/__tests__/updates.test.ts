import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({
  shell: { updateCheck: vi.fn(), updateDownload: vi.fn(), updateInstall: vi.fn() },
  progress: undefined as undefined | ((p: { downloaded: number; total?: number | null }) => void),
  unlisten: vi.fn(),
  calls: [] as string[],
}));

vi.mock("../../ipc/client", () => ({
  shell: h.shell,
  rpc: vi.fn(async () => []),
  onUpdateProgress: (cb: any) => {
    h.progress = cb;
    return () => {
      h.progress = undefined;
      h.unlisten();
    };
  },
}));

import {
  asUpdateFailure, CHECK_EVERY_MS, checkForUpdates, FIRST_CHECK_MS, installUpdate, installUpdateChecks, useUpdates,
} from "../updates";
import { useSettings } from "../settings";
import { useUi } from "../ui";

const info = { version: "0.2.0", currentVersion: "0.1.1", notes: "Faster grid", date: null };
const deferred = <T = void>() => {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((a, b) => { resolve = a; reject = b; });
  return { promise, resolve, reject };
};
const toasts = () => useUi.getState().toasts.map((t) => [t.kind, t.text]);
const saved = vi.fn(async () => { h.calls.push("save"); });

beforeEach(() => {
  vi.clearAllMocks();
  h.calls = [];
  h.progress = undefined;
  h.shell.updateDownload.mockImplementation(async () => { h.calls.push("download"); });
  h.shell.updateInstall.mockImplementation(async () => { h.calls.push("install"); });
  useUpdates.setState({ phase: "idle", info: null, downloaded: 0, total: null, error: null, checkedAt: null, checkError: null });
  useUi.setState({ toasts: [] });
  useSettings.setState({ autoUpdateCheck: true });
});
afterEach(() => vi.useRealTimers());

describe("checkForUpdates", () => {
  it("remembers when a check last got an answer and why the last one failed (Settings → About)", async () => {
    vi.useFakeTimers({ now: new Date(2026, 9, 30, 14, 2) });
    h.shell.updateCheck.mockRejectedValueOnce({ kind: "network", message: "offline" });
    await checkForUpdates();
    expect(useUpdates.getState()).toMatchObject({ checkedAt: null, checkError: { kind: "network", message: "offline" } });
    h.shell.updateCheck.mockResolvedValueOnce(null);
    await checkForUpdates();
    expect(useUpdates.getState()).toMatchObject({ checkedAt: new Date(2026, 9, 30, 14, 2).getTime(), checkError: null });
    h.shell.updateCheck.mockRejectedValueOnce({ kind: "disabled", message: "Updates are available in installed release builds only." });
    await checkForUpdates();
    expect(useUpdates.getState().checkError?.kind).toBe("disabled");
    expect(useUpdates.getState().checkedAt).not.toBeNull();
  });

  it("a newer release makes the update available", async () => {
    h.shell.updateCheck.mockResolvedValue(info);
    await checkForUpdates();
    expect(useUpdates.getState()).toMatchObject({ phase: "available", info, error: null });
    expect(toasts()).toEqual([]);
  });

  it("no release: idle, silent when automatic, reported when manual", async () => {
    h.shell.updateCheck.mockResolvedValue(null);
    await checkForUpdates();
    expect(useUpdates.getState()).toMatchObject({ phase: "idle", info: null });
    expect(toasts()).toEqual([]);
    await checkForUpdates(true);
    expect(toasts()).toEqual([["success", "Querithm is up to date."]]);
  });

  it("a release withdrawn since the last check hides the button", async () => {
    useUpdates.setState({ phase: "available", info });
    h.shell.updateCheck.mockResolvedValue(null);
    await checkForUpdates();
    expect(useUpdates.getState()).toMatchObject({ phase: "idle", info: null });
  });

  it("shows 'checking' while the request runs", async () => {
    const d = deferred<null>();
    h.shell.updateCheck.mockReturnValue(d.promise);
    const p = checkForUpdates();
    expect(useUpdates.getState().phase).toBe("checking");
    d.resolve(null);
    await p;
    expect(useUpdates.getState().phase).toBe("idle");
  });

  it("an automatic check that fails offline is silent and keeps the offer already shown", async () => {
    useUpdates.setState({ phase: "available", info });
    h.shell.updateCheck.mockRejectedValue({ kind: "network", message: "Could not reach the update server." });
    await checkForUpdates();
    expect(useUpdates.getState()).toMatchObject({ phase: "available", info });
    expect(toasts()).toEqual([]);
  });

  it("an automatic check that fails with nothing offered stays idle", async () => {
    h.shell.updateCheck.mockRejectedValue(new Error("boom"));
    await checkForUpdates();
    expect(useUpdates.getState().phase).toBe("idle");
    expect(toasts()).toEqual([]);
  });

  it("a manual check that fails says why; a dev build says updates are disabled", async () => {
    h.shell.updateCheck.mockRejectedValueOnce({ kind: "network", message: "Could not reach the update server." });
    await checkForUpdates(true);
    h.shell.updateCheck.mockRejectedValueOnce({ kind: "disabled", message: "Updates are available in installed release builds only." });
    await checkForUpdates(true);
    expect(toasts()).toEqual([
      ["error", "Could not reach the update server."],
      ["info", "Updates are available in installed release builds only."],
    ]);
  });

  it("does not check while a download or install is running", async () => {
    for (const phase of ["checking", "downloading", "installing"] as const) {
      useUpdates.setState({ phase, info });
      await checkForUpdates();
      expect(useUpdates.getState().phase).toBe(phase);
    }
    expect(h.shell.updateCheck).not.toHaveBeenCalled();
    await checkForUpdates(true);
    expect(toasts()).toEqual([["info", "An update is already in progress."]]);
  });

  it("after a failed install, a new check can offer the update again", async () => {
    useUpdates.setState({ phase: "failed", info, error: { kind: "network", message: "x" } });
    h.shell.updateCheck.mockResolvedValue(info);
    await checkForUpdates(true);
    expect(useUpdates.getState()).toMatchObject({ phase: "available", error: null });
  });
});

describe("installUpdate", () => {
  beforeEach(() => useUpdates.setState({ phase: "available", info }));

  it("downloads, saves the open tabs, then installs, in that order", async () => {
    await installUpdate(saved);
    expect(h.calls).toEqual(["download", "save", "install"]);
    // The shell relaunches the app; until then the button says Restarting.
    expect(useUpdates.getState().phase).toBe("installing");
  });

  it("tracks download progress and stops listening when the download ends", async () => {
    const d = deferred();
    h.shell.updateDownload.mockReturnValue(d.promise);
    const p = installUpdate(saved);
    expect(useUpdates.getState()).toMatchObject({ phase: "downloading", downloaded: 0, total: null });
    h.progress!({ downloaded: 512, total: 2048 });
    expect(useUpdates.getState()).toMatchObject({ downloaded: 512, total: 2048 });
    h.progress!({ downloaded: 1024 });
    expect(useUpdates.getState()).toMatchObject({ downloaded: 1024, total: null });
    d.resolve();
    await p;
    expect(h.unlisten).toHaveBeenCalledTimes(1);
    expect(h.progress).toBeUndefined();
  });

  it("a failed download keeps the current version running: nothing saved or installed", async () => {
    h.shell.updateDownload.mockRejectedValue({ kind: "network", message: "Could not reach the update server." });
    await installUpdate(saved);
    expect(saved).not.toHaveBeenCalled();
    expect(h.shell.updateInstall).not.toHaveBeenCalled();
    expect(h.unlisten).toHaveBeenCalledTimes(1);
    expect(useUpdates.getState()).toMatchObject({ phase: "failed", error: { kind: "network", message: "Could not reach the update server." } });
  });

  it("a package that fails its signature check is never installed", async () => {
    h.shell.updateDownload.mockRejectedValue({ kind: "signature", message: "The downloaded update failed its signature check and was not installed." });
    await installUpdate(saved);
    expect(h.shell.updateInstall).not.toHaveBeenCalled();
    expect(useUpdates.getState().error?.kind).toBe("signature");
  });

  it("if the tabs cannot be saved, the update is not installed", async () => {
    await installUpdate(async () => { throw new Error("disk full"); });
    expect(h.shell.updateInstall).not.toHaveBeenCalled();
    expect(useUpdates.getState()).toMatchObject({ phase: "failed", error: { kind: "save" } });
    expect(useUpdates.getState().error!.message).toContain("disk full");
  });

  it("an install that fails (cancelled admin prompt) is reported and can be retried", async () => {
    h.shell.updateInstall.mockRejectedValueOnce({ kind: "cancelled", message: "The administrator prompt was cancelled." });
    await installUpdate(saved);
    expect(useUpdates.getState()).toMatchObject({ phase: "failed", error: { kind: "cancelled" } });
    await installUpdate(saved);
    expect(h.calls).toEqual(["download", "save", "download", "save", "install"]);
    expect(useUpdates.getState().error).toBeNull();
  });

  it("a second click while working does nothing", async () => {
    const d = deferred();
    h.shell.updateDownload.mockReturnValue(d.promise);
    const first = installUpdate(saved);
    await installUpdate(saved);
    useUpdates.setState({ phase: "installing" });
    await installUpdate(saved);
    expect(h.shell.updateDownload).toHaveBeenCalledTimes(1);
    d.resolve();
    await first;
  });

  it("does nothing without a known release", async () => {
    useUpdates.setState({ phase: "idle", info: null });
    await installUpdate(saved);
    expect(h.shell.updateDownload).not.toHaveBeenCalled();
  });
});

describe("asUpdateFailure", () => {
  it("keeps the shell's kind and message, and copes with other errors", () => {
    expect(asUpdateFailure({ kind: "permission", message: "no" })).toEqual({ kind: "permission", message: "no" });
    expect(asUpdateFailure(new Error("plain"))).toEqual({ kind: "install", message: "plain" });
    expect(asUpdateFailure({ kind: 5, message: "odd" })).toEqual({ kind: "install", message: "odd" });
    expect(asUpdateFailure("text")).toEqual({ kind: "install", message: "text" });
  });
});

describe("installUpdateChecks", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    h.shell.updateCheck.mockResolvedValue(null);
  });

  it("first checks after the start-up delay, then every 6 hours", async () => {
    const stop = installUpdateChecks();
    await vi.advanceTimersByTimeAsync(FIRST_CHECK_MS - 1);
    expect(h.shell.updateCheck).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(h.shell.updateCheck).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(CHECK_EVERY_MS);
    expect(h.shell.updateCheck).toHaveBeenCalledTimes(2);
    stop();
    await vi.advanceTimersByTimeAsync(CHECK_EVERY_MS * 3);
    expect(h.shell.updateCheck).toHaveBeenCalledTimes(2);
  });

  it("checks nothing while turned off in Settings, and resumes when turned back on", async () => {
    useSettings.setState({ autoUpdateCheck: false });
    const stop = installUpdateChecks();
    await vi.advanceTimersByTimeAsync(FIRST_CHECK_MS + CHECK_EVERY_MS);
    expect(h.shell.updateCheck).not.toHaveBeenCalled();
    useSettings.setState({ autoUpdateCheck: true });
    await vi.advanceTimersByTimeAsync(CHECK_EVERY_MS);
    expect(h.shell.updateCheck).toHaveBeenCalledTimes(1);
    stop();
  });

  it("automatic checks are on by default", async () => {
    const { defaultSettings } = await import("../settings");
    expect(defaultSettings.autoUpdateCheck).toBe(true);
  });
});

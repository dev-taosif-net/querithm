// What the shell says about the app (version, platform, data folder), asked once and shared: boot reads it for the
// bench flag, Settings → About shows it. A failed ask is forgotten so the next caller asks again.
import { shell } from "../ipc/client";

export type AppInfo = Awaited<ReturnType<typeof shell.appInfo>>;

let pending: Promise<AppInfo> | null = null;

export function getAppInfo(): Promise<AppInfo> {
  // Through then(): a shell that throws instead of rejecting still yields a rejected promise.
  pending ??= Promise.resolve().then(() => shell.appInfo()).catch((e: unknown) => {
    pending = null;
    throw e;
  });
  return pending;
}

/** Test hook: forget the cached answer. */
export function resetAppInfo() {
  pending = null;
}

/** "linux" → "Linux"; the shell reports the Rust OS name. */
export function platformName(p: string | undefined): string {
  const names: Record<string, string> = { linux: "Linux", windows: "Windows", macos: "macOS" };
  return p ? (names[p] ?? p) : "Unknown platform";
}

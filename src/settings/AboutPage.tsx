// Settings → About: the version the shell reports, the update status, where the logs are with "Copy diagnostics" (M21),
// and the credit (owner request 2026-10-30: "Made by Taosif", on this page only — never in logs, exports, copies, the
// top bar or the status bar).
import { useEffect, useState } from "react";
import { CONTRACT_VERSION } from "../ipc/contract";
import { rpc, shell } from "../ipc/client";
import { isTauri } from "../ipc/transport";
import { getAppInfo, platformName, type AppInfo } from "../app/appInfo";
import { Logo } from "../app/Logo";
import { Icon } from "../app/icons";
import { checkForUpdates, useUpdates } from "../store/updates";
import { useUi } from "../store/ui";
import { defOf } from "./registry";
import { PageHeader, SettingRow } from "./SettingRow";

export const CREDIT = "Made by Taosif";
export const COPYRIGHT = "© 2026 Taosif · MIT License";

/** The version, once the shell answered; null while asking; "unavailable" when it could not answer. */
export function useAppInfo(): AppInfo | null | "unavailable" {
  const [info, setInfo] = useState<AppInfo | null | "unavailable">(null);
  useEffect(() => {
    let live = true;
    getAppInfo().then(
      (i) => live && setInfo(i),
      () => live && setInfo("unavailable"),
    );
    return () => {
      live = false;
    };
  }, []);
  return info;
}

/** The text "Copy version info" puts on the clipboard: version, platform and engine contract, nothing personal. */
export function versionText(info: AppInfo): string {
  return `Querithm ${info.version} · ${platformName(info.platform)} · engine contract ${CONTRACT_VERSION}`;
}

/** The logs folder the shell reports (older shells: `<data dir>/logs`). */
export function logsDir(info: AppInfo): string | undefined {
  if (info.logsDir) return info.logsDir;
  if (!info.dataDir) return undefined;
  return `${info.dataDir}${info.dataDir.includes("\\") ? "\\" : "/"}logs`;
}

/** How many of the newest log lines "Copy diagnostics" takes. */
export const DIAGNOSTIC_LINES = 200;

/**
 * "Copy diagnostics" (M21): version, system, the engine's state and the newest lines of Querithm's own logs, already
 * scrubbed of secrets by the engine; the data folder (it holds the user's name) is written as <data dir>. A part the
 * engine or the shell cannot give says so instead.
 */
export async function diagnosticsText(info: AppInfo | null): Promise<string> {
  const status = await Promise.resolve().then(() => shell.status()).catch(() => null);
  const tail = await rpc("system.logTail", { lines: DIAGNOSTIC_LINES }).catch(() => null);
  const engine = status
    ? `Engine: ${status.state}${status.pid ? ` (pid ${status.pid})` : ""}${status.restarts ? `, restarted ${status.restarts}×` : ""}${status.detail ? ` — ${status.detail}` : ""}`
    : "Engine: the shell did not answer";
  const lines = [
    info ? versionText(info) : `Querithm (version unavailable) · engine contract ${CONTRACT_VERSION}`,
    engine,
    `Window: ${navigator.userAgent}`,
    "",
    tail?.lines?.length ? `Newest log lines (${tail.lines.length}, secrets removed):` : "Log lines: the engine could not read them.",
    ...(tail?.lines ?? []),
  ];
  const text = lines.join("\n");
  return info?.dataDir ? text.split(info.dataDir).join("<data dir>") : text;
}

const time = (ms: number) => new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

function UpdateStatus() {
  const { phase, info, error, checkedAt, checkError } = useUpdates();
  if (phase === "checking") return <>Checking for updates…</>;
  if (phase === "available" && info)
    return (
      <span className="inline-flex items-center gap-2">
        <span className="w-1.5 h-1.5 rounded-full bg-accent" aria-hidden />
        Querithm {info.version} is available.
        <button type="button" className="q-btn q-btn-primary" onClick={() => useUi.getState().openDialog({ kind: "update" })}>
          Update to {info.version}…
        </button>
      </span>
    );
  if (phase === "downloading" || phase === "installing") return <>Updating…</>;
  if (phase === "failed") return <span className="text-err">The update failed: {error?.message ?? "unknown error"}. The current version keeps running.</span>;
  if (checkError?.kind === "disabled") return <>Development build: updates come with installed release builds.</>;
  if (checkError) return <span className="text-warn">The last check failed: {checkError.message}</span>;
  if (checkedAt)
    return (
      <span className="inline-flex items-center gap-1.5">
        <Icon name="check" size={12} className="text-ok" />
        Up to date · checked {time(checkedAt)}
      </span>
    );
  return <>Not checked yet.</>;
}

export function AboutPage({ announce }: { announce: (t: string) => void }) {
  const info = useAppInfo();
  const phase = useUpdates((s) => s.phase);
  const devBuild = useUpdates((s) => !isTauri || s.checkError?.kind === "disabled");
  const busy = phase === "checking" || phase === "downloading" || phase === "installing";
  const known = info && info !== "unavailable" ? info : null;
  const copy = () => {
    if (!known) return;
    navigator.clipboard.writeText(versionText(known)).then(
      () => announce("Version info copied."),
      () => announce("Couldn't copy. Select the text and press Ctrl+C."),
    );
  };
  const logs = known ? logsDir(known) : undefined;
  const copyText = (text: string, done: string) =>
    navigator.clipboard.writeText(text).then(
      () => announce(done),
      () => announce("Couldn't copy. Select the text and press Ctrl+C."),
    );
  const copyDiagnostics = async () => copyText(await diagnosticsText(known), "Diagnostics copied.");
  const check = async () => {
    if (busy) return;
    await checkForUpdates(true);
    const s = useUpdates.getState();
    announce(s.phase === "available" && s.info ? `Querithm ${s.info.version} is available.` : s.checkError ? s.checkError.message : "Querithm is up to date.");
  };
  return (
    <>
      <PageHeader title="About" />
      <div className="flex items-start gap-4 pb-5">
        <Logo tile className="w-10 h-10 shrink-0" />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="text-[18px] font-semibold text-fg leading-6">Querithm</span>
            {devBuild && <span className="h-[18px] px-1.5 inline-flex items-center border border-line rounded text-[10px] font-medium text-muted">Development build</span>}
          </div>
          <div className="mt-1 flex items-center gap-3 flex-wrap">
            <span id="about-version" className="text-[12px] text-fg select-text tabular-nums" data-about-version>
              {info === null ? "Version …" : known ? `Version ${known.version}` : "Version unavailable: the app shell did not answer."}
            </span>
            {known && (
              <button type="button" className="q-btn" aria-describedby="about-version" onClick={copy}>
                <Icon name="copy" size={12} />
                Copy version info
              </button>
            )}
          </div>
          <div className="mt-1 text-[11px] text-muted select-text">
            {known ? `${platformName(known.platform)} · ` : ""}Engine contract {CONTRACT_VERSION}
          </div>
        </div>
      </div>
      <section aria-labelledby="about-updates">
        <h3 id="about-updates" className="text-[11px] font-semibold uppercase tracking-wide text-muted pb-1.5 border-b border-line">
          Updates
        </h3>
        <div className="flex items-center gap-4 py-2.5 min-h-[44px] border-b border-line">
          <span className="text-[12px] text-fg flex-1 min-w-0" aria-live="off">
            <UpdateStatus />
          </span>
          <button id="setting-checkUpdates" type="button" className="q-btn" aria-disabled={busy || undefined} onClick={() => void check()}>
            {phase === "checking" ? "Checking…" : "Check for updates"}
          </button>
        </div>
      </section>
      <SettingRow def={defOf("autoUpdateCheck")} announce={announce} />
      <section aria-labelledby="about-diagnostics" className="mt-6">
        <h3 id="about-diagnostics" className="text-[11px] font-semibold uppercase tracking-wide text-muted pb-1.5 border-b border-line">
          Diagnostics
        </h3>
        <div className="flex items-center gap-4 py-2.5 min-h-[44px] border-b border-line">
          <span className="text-[12px] text-fg flex-1 min-w-0">
            <span className="block">Logs folder</span>
            <span id="about-logs" className="block text-[11px] text-muted select-text break-all" data-about-logs>
              {logs ?? (info === null ? "…" : "Unavailable: the app shell did not say where its data folder is.")}
            </span>
          </span>
          {logs && (
            <button type="button" className="q-btn" aria-describedby="about-logs" onClick={() => void copyText(logs, "Logs folder path copied.")}>
              <Icon name="copy" size={12} />
              Copy path
            </button>
          )}
        </div>
        <div className="flex items-center gap-4 py-2.5 min-h-[44px] border-b border-line">
          <span id="about-diagnostics-help" className="text-[12px] text-fg flex-1 min-w-0">
            Version, system, engine state and the newest {DIAGNOSTIC_LINES} lines of Querithm's logs, with passwords and keys removed, for a
            bug report. Nothing is sent anywhere.
          </span>
          <button type="button" className="q-btn" aria-describedby="about-diagnostics-help" onClick={() => void copyDiagnostics()}>
            <Icon name="copy" size={12} />
            Copy diagnostics
          </button>
        </div>
      </section>
      <div className="mt-8 pt-4 border-t border-line text-[11px] leading-[16px] text-muted">
        <p>{CREDIT}</p>
        <p>{COPYRIGHT}</p>
      </div>
    </>
  );
}

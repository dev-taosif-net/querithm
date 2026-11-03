import { useEffect, useLayoutEffect, useState } from "react";
import { CONTRACT_VERSION } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { isTauri } from "../ipc/transport";
import { initMonaco, applyMonacoTheme } from "../editor/monaco";
import { installProviders } from "../editor/providers";
import { refreshEditorOptions } from "../editor/editors";
import { useSettings } from "../store/settings";
import { useProfiles } from "../store/profiles";
import { installSnapshotListeners } from "../store/snapshots";
import { installDiagListeners } from "../store/diag";
import { ExplorerHost } from "../explorer/ExplorerHost";
import { TopBar } from "./TopBar";
import { StatusBar } from "./StatusBar";
import { Workspace } from "./Workspace";
import { Dialogs } from "./Dialogs";
import { Banner, Palette, Toasts } from "./Palette";
import { TabSwitcher } from "./TabList";
import { Announcer } from "./Announcer";
import { installAnnouncements } from "./announcements";
import { installExecutionListeners, newQueryTab } from "./actions";
import { installKeybindings } from "./commands";
import { installMiddleClickPasteGuard } from "./middleClickPaste";
import { installNavigation } from "./navigation";
import { EngineDown, installEngineStatus, showStartupNotices, whyEngineDown } from "./engineStatus";
import { ErrorBoundary } from "./ErrorBoundary";
import { shell } from "../ipc/client";
import { installAutosave, restoreWorkspace } from "./persistence";
import { installQuitGuard } from "./quit";
import { installBench, runAppBench } from "./bench";
import { installUpdateChecks } from "../store/updates";
import { getAppInfo } from "./appInfo";
import { applyUiScale } from "./uiScale";

let booted: Promise<void> | null = null;
let prepared = false;

function syncDocumentTheme(theme = useSettings.getState().theme, syntax = useSettings.getState().syntaxTheme) {
  const dark = theme === "dark" || (theme === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
  document.documentElement.dataset.syntax = syntax;
  applyMonacoTheme();
}

async function boot() {
  // Once, also when a boot whose engine had not started is tried again (M18).
  if (!prepared) {
    prepared = true;
    // First: closing the window works (and asks what it must) from the moment the shell can send the request (H4).
    installQuitGuard();
    initMonaco();
    installProviders();
  }
  let ping: Awaited<ReturnType<typeof rpc<"system.ping">>>;
  try {
    ping = await rpc("system.ping");
  } catch (e) {
    throw await whyEngineDown(e);
  }
  // §5 Versioning: refuse to run against a sidecar built from a different contract.
  if (ping.version !== CONTRACT_VERSION) throw new Error(`Engine contract ${ping.version} does not match the UI (${CONTRACT_VERSION}). Rebuild the sidecar.`);
  await Promise.all([useSettings.getState().load(), useProfiles.getState().load()]);
  syncDocumentTheme();
  installExecutionListeners();
  installSnapshotListeners();
  installDiagListeners();
  installEngineStatus();
  void showStartupNotices();
  installKeybindings();
  installMiddleClickPasteGuard();
  installNavigation();
  installAnnouncements();
  if (!(await restoreWorkspace())) newQueryTab();
  installAutosave();
  const info = await getAppInfo().catch(() => null);
  // window.__bench only for a harness (L10): the shell or the dev bridge reports bench when launched with QUERITHM_BENCH=1;
  // ?bench is honoured in the browser (dev bridge) only, never in the shell's webview.
  if (info?.bench || (!isTauri && new URLSearchParams(location.search).has("bench"))) installBench();
  if (info?.bench && isTauri) void runAppBench(performance.now()).catch((e) => console.error("app bench failed", e));
  // Only the installed app updates itself; bench runs stay offline.
  if (isTauri && !info?.bench) installUpdateChecks();
}

/**
 * Applies the theme and the editor's display settings. A leaf of its own: when the root App subscribed to them, every
 * font-size step re-rendered the whole shell (App.test "re-renders no part of the shell").
 */
function SettingsEffects() {
  const theme = useSettings((s) => s.theme);
  const syntax = useSettings((s) => s.syntaxTheme);
  useLayoutEffect(() => {
    const apply = () => syncDocumentTheme(theme, syntax);
    apply();
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    mq.addEventListener("change", apply);
    return () => mq.removeEventListener("change", apply);
  }, [theme, syntax]);
  const font = useSettings((s) => `${s.editorFontSize}|${s.minimap}|${s.wordWrap}|${s.reducedDecorations}`);
  useEffect(() => refreshEditorOptions(), [font]);
  // Only once the settings have loaded: before, uiScale is the default, and applying it would undo the scale the window
  // started at (applyCachedUiScale, W8) until the saved one arrived.
  const scale = useSettings((s) => (s.loaded ? s.uiScale : null));
  useEffect(() => void (scale != null && applyUiScale(scale)), [scale]);
  return null;
}

export function App() {
  const [state, setState] = useState<"booting" | "ready" | "error">("booting");
  const [error, setError] = useState("");
  const [down, setDown] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    booted ??= boot();
    booted.then(() => setState("ready")).catch((e) => {
      setError(e instanceof Error ? e.message : String(e));
      setDown(e instanceof EngineDown);
      setState("error");
    });
  }, [attempt]);

  /** M18: the engine did not start; Restart spawns it again, then the boot goes on from the ping. */
  const restart = async () => {
    setState("booting");
    await shell.restart().catch(() => {});
    booted = null;
    setAttempt((n) => n + 1);
  };

  if (state === "error") {
    return (
      <div className="h-full flex items-center justify-center bg-bg">
        <SettingsEffects />
        <div role="alert" className="max-w-lg p-6 rounded-xl border border-err/50 bg-panel space-y-3">
          <div className="text-sm font-bold text-err">Querithm could not start its engine</div>
          <div className="text-xs text-fg whitespace-pre-wrap select-text">{error}</div>
          {down ? (
            <button className="q-btn q-btn-primary" onClick={() => void restart()}>Restart engine</button>
          ) : (
            <button className="q-btn q-btn-primary" onClick={() => location.reload()}>Retry</button>
          )}
        </div>
      </div>
    );
  }
  if (state === "booting")
    return (
      <div className="h-full flex items-center justify-center text-muted text-xs bg-bg">
        <SettingsEffects />
        Starting Querithm…
      </div>
    );

  return (
    <div className="h-full flex flex-col bg-bg text-fg">
      <SettingsEffects />
      <TopBar />
      <Banner />
      <div className="flex-1 flex min-h-0 relative">
        <ErrorBoundary name="Explorer">
          <ExplorerHost />
        </ErrorBoundary>
        <div className="flex-1 min-w-0">
          <Workspace />
        </div>
      </div>
      <StatusBar />
      <Dialogs />
      <Palette />
      <TabSwitcher />
      <Toasts />
      <Announcer />
    </div>
  );
}

import { useEffect, useLayoutEffect, useState } from "react";
import { CONTRACT_VERSION } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { isTauri } from "../ipc/transport";
import { initMonaco, applyMonacoTheme } from "../editor/monaco";
import { installProviders } from "../editor/providers";
import { refreshEditorOptions } from "../editor/editors";
import { takeSyntaxMigrationNotice, useSettings, useSettingsStatus } from "../store/settings";
import { toast } from "../store/ui";
import { resolveAppTheme } from "../theme/resolve";
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
import { cachedTheme, cachedThemeChoice, isDark, keepTheme, keepThemeBoot, setBootColors } from "./theme";
import { hideSplash, showSplash, splashStage } from "./splash";

let booted: Promise<void> | null = null;
let prepared = false;

/**
 * The theme to show: the setting once it has loaded; before that the one the window last had (§16 start screen), which
 * public/boot-theme.js already named on the document. Applying the store's default here made a light window start dark.
 */
function themeToShow(s = useSettings.getState()) {
  return s.loaded ? s.theme : (cachedTheme() ?? s.theme);
}

/**
 * The theme choice to show, as themeToShow for the word: the settings once they have loaded; before that the App Theme
 * picks and syntax colors the window last had (the boot cache, which public/boot-theme.js already put on the document,
 * plans/app-themes §6.2). The store's defaults here switched a Cortado window to Querithm Dark and back while loading.
 */
function choiceToShow(s = useSettings.getState()) {
  const live = { theme: s.theme, darkTheme: s.darkTheme, lightTheme: s.lightTheme, syntaxColors: s.syntaxColors };
  return s.loaded ? live : { ...live, ...bootChoice() };
}

/** The boot cache, read once: it is written only once the settings have loaded (keepThemeBoot), and read only before. */
let bootCache: ReturnType<typeof cachedThemeChoice> | undefined;
const bootChoice = () => (bootCache ??= cachedThemeChoice());

/** One part of choiceToShow for a selector, which runs on every settings change: no object built, the cache read once. */
function shownPart<K extends "darkTheme" | "lightTheme" | "syntaxColors">(s: ReturnType<typeof useSettings.getState>, k: K) {
  return s.loaded ? s[k] : (bootChoice()[k] ?? s[k]);
}

/** Writes an attribute only when it changes: every write of data-theme flushes the tab-style caches (tabStyle.ts). */
function setIfChanged(root: HTMLElement, key: string, value: string) {
  if (root.dataset[key] !== value) root.dataset[key] = value;
}

/**
 * Names the shown App Theme on the document (plans/app-themes §4.4): data-theme = its mode (every existing reader keeps
 * working), data-app-theme = its id (selects its generated token block), data-syntax = Syntax colors, data-comments =
 * upright when italic comments are off, and the start screen's colours (shown again by Restart engine). Then the editor
 * theme follows; semantic coloring is switched with the palette.
 */
function syncDocumentTheme() {
  const s = useSettings.getState();
  const c = choiceToShow(s);
  const root = document.documentElement;
  const app = resolveAppTheme(c, isDark("system"));
  setIfChanged(root, "theme", app.mode);
  setIfChanged(root, "appTheme", app.id);
  setIfChanged(root, "syntax", c.syntaxColors);
  setIfChanged(root, "comments", s.italicComments ? "italic" : "upright");
  setBootColors(root, app);
  if (applyMonacoTheme()) refreshEditorOptions();
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
  splashStage(2);
  // §5 Versioning: refuse to run against a sidecar built from a different contract.
  if (ping.version !== CONTRACT_VERSION) throw new Error(`Engine contract ${ping.version} does not match the UI (${CONTRACT_VERSION}). Rebuild the sidecar.`);
  await Promise.all([useSettings.getState().load(), useProfiles.getState().load()]);
  syncDocumentTheme();
  splashStage(3);
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
  splashStage(4);
  // Once, on the start that moved a saved Standard to Default: say so, with the way back (plans/app-themes §3.6).
  if (takeSyntaxMigrationNotice())
    toast("info", "Syntax colors now follow your color theme (Default): NULL, AND/OR, CTE names and schema prefixes have their own colors.", {
      label: "Use Standard",
      run: () => useSettings.getState().set({ syntaxColors: "standard" }),
    });
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
  const theme = useSettings(themeToShow);
  // Primitives only: a selector returning a fresh object would re-render forever. The shown values (the boot cache's
  // before the settings load), so the load re-applies whenever what shows changes, also back to a default.
  const darkTheme = useSettings((s) => shownPart(s, "darkTheme"));
  const lightTheme = useSettings((s) => shownPart(s, "lightTheme"));
  const syntax = useSettings((s) => shownPart(s, "syntaxColors"));
  const italicComments = useSettings((s) => s.italicComments);
  // Kept for the next start only when it is the saved setting: not the default before the settings have loaded, and
  // not the default they fall back to when they could not be read.
  const loaded = useSettings((s) => s.loaded);
  const unread = useSettingsStatus((s) => s.loadFailed);
  // Both picks' start colours for the next start: the shell's window colour and the first frame (plans/app-themes §6);
  // an unknown pick keeps Querithm's. A syntax-only change tells the shell nothing new (keepTheme dedupes its line).
  useLayoutEffect(() => {
    if (!loaded || unread) return;
    const dark = resolveAppTheme({ theme: "dark", darkTheme, lightTheme }, true);
    const light = resolveAppTheme({ theme: "light", darkTheme, lightTheme }, false);
    keepTheme(theme, dark, light);
    keepThemeBoot(dark, light, syntax);
  }, [loaded, unread, theme, darkTheme, lightTheme, syntax]);
  useLayoutEffect(() => {
    const apply = () => syncDocumentTheme();
    apply();
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    mq.addEventListener("change", apply);
    return () => mq.removeEventListener("change", apply);
  }, [theme, darkTheme, lightTheme, syntax, italicComments]);
  // The tab layout sets the strip's height (--q-strip-h), which the Object Explorer's header follows.
  const tabLayout = useSettings((s) => s.tabLayout);
  useLayoutEffect(() => void (document.documentElement.dataset.tabLayout = tabLayout), [tabLayout]);
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

  // The start screen (index.html, §16) covers the window while the app boots, and again after Restart engine.
  useLayoutEffect(() => (state === "booting" ? showSplash() : hideSplash()), [state]);

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
      <div className="h-full bg-bg">
        <SettingsEffects />
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

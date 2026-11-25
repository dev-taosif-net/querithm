// @vitest-environment jsdom
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";
import { appThemeById } from "../../theme/appThemes";
import { keepThemeBoot } from "../theme";

const h = vi.hoisted(() => ({
  version: "1",
  rpc: vi.fn(),
  appInfo: vi.fn(),
  tauri: false,
  restored: false,
  calls: [] as string[],
  refresh: vi.fn(),
  applyTheme: vi.fn(),
  runAppBench: vi.fn(async () => {}),
  topBarRenders: 0,
  restart: vi.fn(async () => {}),
  whyEngineDown: vi.fn(async (e: unknown) => e),
  setZoom: vi.fn(async (_z: number) => {}),
  themeKeep: vi.fn(async (_t: string) => {}),
}));

vi.mock("../../ipc/contract", () => ({ CONTRACT_VERSION: "1" }));
vi.mock("../../ipc/client", () => ({ rpc: (...a: any[]) => h.rpc(...a), shell: { appInfo: () => h.appInfo(), restart: () => h.restart(), themeKeep: (t: string) => h.themeKeep(t) } }));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return h.tauri; } }));
vi.mock("../../editor/monaco", () => ({ initMonaco: () => h.calls.push("initMonaco"), applyMonacoTheme: () => h.applyTheme() }));
vi.mock("../../editor/providers", () => ({ installProviders: () => h.calls.push("providers") }));
vi.mock("../../editor/editors", () => ({ refreshEditorOptions: () => h.refresh() }));
vi.mock("../../store/snapshots", () => ({ installSnapshotListeners: () => h.calls.push("snapshots") }));
vi.mock("../../store/diag", () => ({ installDiagListeners: () => h.calls.push("diag") }));
vi.mock("../../explorer/ExplorerHost", () => ({ ExplorerHost: () => <div>explorer</div> }));
vi.mock("../TopBar", () => ({ TopBar: () => { h.topBarRenders++; return <div>topbar</div>; } }));
vi.mock("../StatusBar", () => ({ StatusBar: () => <div>statusbar</div> }));
vi.mock("../Workspace", () => ({ Workspace: () => <div>workspace</div> }));
vi.mock("../Dialogs", () => ({ Dialogs: () => <div>dialogs</div> }));
vi.mock("../Palette", () => ({ Banner: () => <div>banner</div>, Palette: () => <div>palette</div>, Toasts: () => <div>toasts</div> }));
vi.mock("../TabList", () => ({ TabSwitcher: () => <div>switcher</div> }));
vi.mock("../actions", () => ({ installExecutionListeners: () => h.calls.push("exec"), newQueryTab: () => h.calls.push("newQueryTab") }));
vi.mock("../commands", () => ({ installKeybindings: () => h.calls.push("keys") }));
vi.mock("../middleClickPaste", () => ({ installMiddleClickPasteGuard: () => h.calls.push("paste") }));
vi.mock("../navigation", () => ({ installNavigation: () => h.calls.push("navigation") }));
vi.mock("../engineStatus", () => {
  class EngineDown extends Error {}
  return {
    EngineDown,
    installEngineStatus: () => h.calls.push("engine"),
    showStartupNotices: async () => { h.calls.push("notices"); },
    whyEngineDown: (e: unknown) => h.whyEngineDown(e),
  };
});
vi.mock("../persistence", () => ({ restoreWorkspace: async () => h.restored, installAutosave: () => h.calls.push("autosave") }));
vi.mock("../quit", () => ({ installQuitGuard: () => h.calls.push("quit") }));
vi.mock("../bench", () => ({ installBench: () => h.calls.push("installBench"), runAppBench: (...a: any[]) => (h.runAppBench as any)(...a) }));
vi.mock("../../store/updates", () => ({ installUpdateChecks: () => h.calls.push("updateChecks"), useUpdates: { getState: () => ({ phase: "idle" }) } }));
vi.mock("@tauri-apps/api/webview", () => ({ getCurrentWebview: () => ({ setZoom: (z: number) => h.setZoom(z) }) }));

type Stores = {
  useSettings: typeof import("../../store/settings").useSettings;
  useProfiles: typeof import("../../store/profiles").useProfiles;
  flushSettings: typeof import("../../store/settings").flushSettings;
};
let stores: Stores | undefined;
/** Each test gets a fresh module graph: App boots once per module instance, so the stores must come from the same graph. */
async function loadApp() {
  vi.resetModules();
  const settings = await import("../../store/settings");
  stores = { useSettings: settings.useSettings, useProfiles: (await import("../../store/profiles")).useProfiles, flushSettings: settings.flushSettings };
  return (await import("../App")).App;
}
const useSettings = { setState: (s: any) => stores!.useSettings.setState(s), getState: () => stores!.useSettings.getState() };
const useProfiles = { getState: () => stores!.useProfiles.getState() };

function matchMedia(dark: boolean) {
  const listeners: (() => void)[] = [];
  window.matchMedia = ((q: string) => ({
    matches: dark, media: q, addEventListener: (_: string, f: () => void) => listeners.push(f), removeEventListener: (_: string, f: () => void) => listeners.splice(listeners.indexOf(f), 1),
  })) as any;
  return listeners;
}

beforeEach(() => {
  vi.clearAllMocks();
  h.calls.length = 0;
  h.restored = false;
  h.tauri = false;
  h.rpc.mockImplementation(async (m: string) => (m === "system.ping" ? { version: "1" } : m === "profile.list" || m === "group.list" ? [] : m === "settings.get" ? [] : undefined));
  h.appInfo.mockResolvedValue({ version: "1", platform: "linux", bench: false });
  matchMedia(false);
  history.replaceState(null, "", "/");
});
let restoreLocation = () => {};
afterEach(async () => {
  restoreLocation();
  restoreLocation = () => {};
  cleanup();
  // A save this test's settings scheduled (400 ms debounce) is written now, into a mock that answers at once: left
  // pending, it fired in the next test, whose settings.get it answered in place of that test's own load (W8 flake).
  h.rpc.mockImplementation(async () => undefined);
  await stores?.flushSettings();
  stores = undefined;
  for (const a of ["data-theme", "data-app-theme", "data-syntax", "data-comments", "style"]) document.documentElement.removeAttribute(a);
  document.getElementById("splash")?.remove();
  localStorage.removeItem("querithm.theme");
  localStorage.removeItem("querithm.themeBoot"); // read before the settings load: never from another test
});

/** index.html's start screen (§16), as the page holds it before the app renders. */
function startScreen() {
  const html = readFileSync(resolve(process.cwd(), "index.html"), "utf8");
  const el = document.importNode(new DOMParser().parseFromString(html, "text/html").getElementById("splash")!, true);
  document.body.appendChild(el);
  return el;
}

describe("App", () => {
  // Profile (React render count, this test): before the fix the root App subscribed to the editor settings, so every
  // font-size step re-rendered the whole shell (TopBar, Workspace, Explorer, StatusBar) once per change.
  it("changing editor or theme settings re-renders no part of the shell", async () => {
    const App = await loadApp();
    render(<App />);
    await screen.findByText("topbar");
    const before = h.topBarRenders;
    act(() => useSettings.getState().set({ editorFontSize: 15 }));
    act(() => useSettings.getState().set({ minimap: false, wordWrap: true, theme: "light" }));
    await waitFor(() => expect(h.refresh).toHaveBeenCalled());
    expect(document.documentElement.dataset.theme).toBe("light");
    expect(h.topBarRenders).toBe(before);
  });

  it("shows the start screen, boots, and renders every region", async () => {
    const splash = startScreen();
    const App = await loadApp();
    render(<App />);
    expect(splash.hidden).toBe(false);
    expect(screen.getByText("Starting the engine…")).toBeInTheDocument();
    for (const t of ["topbar", "banner", "explorer", "workspace", "statusbar", "dialogs", "palette", "switcher", "toasts"]) expect(await screen.findByText(t)).toBeInTheDocument();
    // Ready: the last step was named and the screen is leaving, then gone.
    expect(splash.dataset.stage).toBe("4");
    await waitFor(() => expect(splash.hidden).toBe(true));
  });

  it("the start screen follows the boot: engine, then settings, then workspace (§16)", async () => {
    const splash = startScreen();
    const gate: Record<string, (v: unknown) => void> = {};
    const held = (m: string) => new Promise((r) => (gate[m] = r));
    h.rpc.mockImplementation(async (m: string) => (m === "system.ping" || m === "settings.get" ? held(m) : m === "profile.list" || m === "group.list" ? [] : undefined));
    const App = await loadApp();
    render(<App />);
    await waitFor(() => expect(gate["system.ping"]).toBeDefined());
    expect(splash.dataset.stage).toBe("1");
    gate["system.ping"]({ version: "1" });
    await waitFor(() => expect(gate["settings.get"]).toBeDefined());
    expect(splash.dataset.stage).toBe("2");
    expect(screen.getByText("Loading your settings…")).toBeInTheDocument();
    gate["settings.get"]([]);
    await screen.findByText("workspace");
    expect(splash.dataset.stage).toBe("4");
  });

  it("an engine that did not start takes the start screen away for the reason, and Restart engine brings it back", async () => {
    const splash = startScreen();
    const App = await loadApp();
    const { EngineDown } = await import("../engineStatus");
    let ping: ((v: unknown) => void) | undefined;
    let started = false;
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "system.ping") {
        if (!started) throw { code: -32003, message: "down" };
        return new Promise((r) => (ping = r));
      }
      return m === "profile.list" || m === "group.list" || m === "settings.get" ? [] : undefined;
    });
    h.whyEngineDown.mockImplementation(async () => new EngineDown("The Querithm engine did not start"));
    h.restart.mockImplementation(async () => { started = true; });
    render(<App />);
    await screen.findByText("Querithm could not start its engine");
    await waitFor(() => expect(splash.hidden).toBe(true));
    await userEvent.setup().click(screen.getByRole("button", { name: "Restart engine" }));
    await waitFor(() => expect(ping).toBeDefined());
    expect(splash.hidden).toBe(false);
    expect(splash.dataset.stage).toBe("1");
    ping!({ version: "1" });
    await screen.findByText("workspace");
    await waitFor(() => expect(splash.hidden).toBe(true));
    h.whyEngineDown.mockImplementation(async (e: unknown) => e);
  });

  it("boots in order: Monaco, providers, ping, stores, listeners; opens a new tab when nothing is restored", async () => {
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    expect(h.calls).toEqual(["quit", "initMonaco", "providers", "exec", "snapshots", "diag", "engine", "notices", "keys", "paste", "navigation", "newQueryTab", "autosave"]);
    expect(h.rpc).toHaveBeenCalledWith("system.ping");
    expect(useProfiles.getState().loaded).toBe(true);
    expect(useSettings.getState().loaded).toBe(true);
  });

  it("does not open a blank tab when a workspace was restored", async () => {
    h.restored = true;
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    expect(h.calls).not.toContain("newQueryTab");
    expect(h.calls).toContain("autosave");
  });

  it("refuses to run against a sidecar with a different contract version, and Retry reloads", async () => {
    h.rpc.mockImplementation(async (m: string) => (m === "system.ping" ? { version: "999" } : undefined));
    const reload = vi.fn();
    const original = Object.getOwnPropertyDescriptor(window, "location")!;
    restoreLocation = () => Object.defineProperty(window, "location", original);
    Object.defineProperty(window, "location", { configurable: true, value: { ...window.location, reload, search: "" } });
    const App = await loadApp();
    render(<App />);
    expect(await screen.findByText("Querithm could not start its engine")).toBeInTheDocument();
    expect(screen.getByText(/Engine contract 999 does not match the UI \(1\)\. Rebuild the sidecar\./)).toBeInTheDocument();
    expect(h.calls).not.toContain("exec");
    await userEvent.setup().click(screen.getByText("Retry"));
    expect(reload).toHaveBeenCalled();
  });

  it("an engine the shell could not start shows the reason with Restart engine, which restarts it and boots on (M18)", async () => {
    const App = await loadApp();
    // After loadApp: the module graph App boots from (its EngineDown class).
    const { EngineDown } = await import("../engineStatus");
    let started = false;
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "system.ping") {
        if (!started) throw { code: -32003, message: "The Querithm engine is not running.", data: { code: "sidecarDown" } };
        return { version: "1" };
      }
      return m === "profile.list" || m === "group.list" || m === "settings.get" ? [] : undefined;
    });
    h.whyEngineDown.mockImplementation(async () => new EngineDown("The Querithm engine did not start: could not start /x/querithm-sidecar: denied"));
    h.restart.mockImplementation(async () => { started = true; });
    render(<App />);
    expect(await screen.findByText("Querithm could not start its engine")).toBeInTheDocument();
    expect(screen.getByText("The Querithm engine did not start: could not start /x/querithm-sidecar: denied")).toBeInTheDocument();
    expect(screen.queryByText("Retry")).toBeNull();
    await userEvent.setup().click(screen.getByRole("button", { name: "Restart engine" }));
    expect(h.restart).toHaveBeenCalledTimes(1);
    expect(await screen.findByText("workspace")).toBeInTheDocument();
    // Monaco, the providers and the quit guard were set up once, before the first ping.
    expect(h.calls.filter((c) => c === "initMonaco" || c === "quit")).toEqual(["quit", "initMonaco"]);
    h.whyEngineDown.mockImplementation(async (e: unknown) => e);
  });

  it("shows non-Error failures as text", async () => {
    h.rpc.mockRejectedValue("sidecar down");
    const App = await loadApp();
    render(<App />);
    expect(await screen.findByText("sidecar down")).toBeInTheDocument();
  });

  it("?bench installs the bench hook without running the in-app bench outside Tauri", async () => {
    history.replaceState(null, "", "/?bench");
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    await waitFor(() => expect(h.calls).toContain("installBench"));
    expect(h.runAppBench).not.toHaveBeenCalled();
  });

  it("?bench in the shell's webview without the shell's bench flag installs no bench hook (L10)", async () => {
    h.tauri = true;
    history.replaceState(null, "", "/?bench");
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    await waitFor(() => expect(h.appInfo).toHaveBeenCalled());
    await Promise.resolve();
    expect(h.calls).not.toContain("installBench");
    expect(h.runAppBench).not.toHaveBeenCalled();
  });

  it("the shell's bench flag under Tauri also runs the app bench", async () => {
    h.tauri = true;
    h.appInfo.mockResolvedValue({ version: "1", platform: "linux", bench: true });
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    await waitFor(() => expect(h.runAppBench).toHaveBeenCalledWith(expect.any(Number)));
    expect(h.calls).toContain("installBench");
  });

  it("a failing app bench is logged, not fatal", async () => {
    h.tauri = true;
    h.appInfo.mockResolvedValue({ version: "1", platform: "linux", bench: true });
    h.runAppBench.mockRejectedValueOnce(new Error("bench boom"));
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    await waitFor(() => expect(err).toHaveBeenCalledWith("app bench failed", expect.any(Error)));
    err.mockRestore();
  });

  it("without app info (browser dev) no bench is installed", async () => {
    h.appInfo.mockRejectedValue(new Error("no shell"));
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    expect(h.calls).not.toContain("installBench");
  });

  describe("theme", () => {
    it.each([
      ["dark", false, "dark"],
      ["light", true, "light"],
      ["system", true, "dark"],
      ["system", false, "light"],
    ])("theme %s with OS dark=%s resolves to %s", async (theme, osDark, expected) => {
      matchMedia(osDark);
      const App = await loadApp();
      useSettings.setState({ theme } as any);
      render(<App />);
      await waitFor(() => expect(document.documentElement.dataset.theme).toBe(expected));
      expect(h.applyTheme).toHaveBeenCalled();
    });

    it("follows OS changes and re-applies when the setting changes", async () => {
      const listeners = matchMedia(false);
      const App = await loadApp();
      useSettings.setState({ theme: "system" } as any);
      render(<App />);
      await waitFor(() => expect(document.documentElement.dataset.theme).toBe("light"));
      matchMedia(true);
      act(() => listeners.forEach((f) => f()));
      expect(document.documentElement.dataset.theme).toBe("dark");
      act(() => useSettings.setState({ theme: "light" } as any));
      await waitFor(() => expect(document.documentElement.dataset.theme).toBe("light"));
    });

    // Regression (§16 start screen): the store's default (dark) was applied while the settings loaded, so a light
    // window started dark and jumped to light once the engine had answered.
    it("keeps the theme the window last had while the settings load, then the setting", async () => {
      localStorage.setItem("querithm.theme", "light");
      document.documentElement.dataset.theme = "light"; // public/boot-theme.js, before the first paint
      let loaded: ((v: unknown) => void) | undefined;
      // Only the load waits for the test; the save that follows it (a migrated object is written back) reads at once,
      // as the sidecar answers it: a read left hanging is a write in flight that flushSettings waits for.
      h.rpc.mockImplementation(async (m: string) =>
        m === "system.ping" ? { version: "1" } : m === "settings.get" ? (loaded ? [] : new Promise((r) => (loaded = r))) : m === "profile.list" || m === "group.list" ? [] : undefined,
      );
      const seen: (string | undefined)[] = [];
      const mo = new MutationObserver(() => seen.push(document.documentElement.dataset.theme));
      mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
      const App = await loadApp();
      render(<App />);
      await waitFor(() => expect(loaded).toBeDefined());
      expect(document.documentElement.dataset.theme).toBe("light");
      loaded!([{ key: "ui.settings", valueJson: JSON.stringify({ theme: "light" }) }]);
      await screen.findByText("workspace");
      expect(document.documentElement.dataset.theme).toBe("light");
      mo.disconnect();
      expect(seen).not.toContain("dark");
    });

    // Regression (plans/app-themes §6.2, review): before the settings loaded, the store's default picks and syntax
    // colors replaced the App Theme boot-theme.js had named (Cortado → Querithm Dark → Cortado once the engine answered).
    it("keeps the App Theme and syntax colors the window last had while the settings load, then the setting", async () => {
      const root = document.documentElement;
      localStorage.setItem("querithm.theme", "dark");
      const cortado = appThemeById("cortado")!, vellum = appThemeById("vellum")!;
      keepThemeBoot(cortado, vellum, "neon");
      // public/boot-theme.js, before the first paint:
      root.dataset.theme = "dark";
      root.dataset.appTheme = "cortado";
      root.dataset.syntax = "neon";
      let loaded: ((v: unknown) => void) | undefined;
      h.rpc.mockImplementation(async (m: string) =>
        m === "system.ping" ? { version: "1" } : m === "settings.get" ? (loaded ? [] : new Promise((r) => (loaded = r))) : m === "profile.list" || m === "group.list" ? [] : undefined,
      );
      const seen: string[] = [];
      const mo = new MutationObserver(() => seen.push(`${root.dataset.appTheme}/${root.dataset.syntax}`));
      mo.observe(root, { attributes: true, attributeFilter: ["data-app-theme", "data-syntax"] });
      const App = await loadApp();
      render(<App />);
      await waitFor(() => expect(loaded).toBeDefined());
      await act(async () => {}); // let the mount's layout effects and observers run
      expect([root.dataset.appTheme, root.dataset.syntax]).toEqual(["cortado", "neon"]);
      expect(seen).toEqual([]); // nothing switched while the settings were unread
      // The saved setting is what shows once read, also when it is the default the cache did not hold.
      loaded!([{ key: "ui.settings", valueJson: JSON.stringify({ theme: "dark", darkTheme: "dark", syntaxColors: "default" }) }]);
      await screen.findByText("workspace");
      await waitFor(() => expect([root.dataset.appTheme, root.dataset.syntax]).toEqual(["dark", "default"]));
      mo.disconnect();
    });

    it("the start screen's colours follow the theme shown, for the splash Restart engine shows again", async () => {
      const root = document.documentElement;
      root.removeAttribute("style"); // no boot cache: boot-theme.js set no --boot-* colours
      const App = await loadApp();
      render(<App />);
      await screen.findByText("workspace");
      expect(root.style.getPropertyValue("--boot-bg")).toBe("#0f1117"); // Querithm Dark, the default
      act(() => useSettings.setState({ theme: "dark", darkTheme: "fathom" } as any));
      await waitFor(() => expect(root.style.getPropertyValue("--boot-bg")).toBe(appThemeById("fathom")!.ui.bg));
      expect(root.style.getPropertyValue("--boot-fg")).toBe(appThemeById("fathom")!.ui.fg);
      act(() => useSettings.setState({ theme: "light" } as any));
      await waitFor(() => expect(root.style.getPropertyValue("--boot-bg")).toBe("#ffffff"));
      expect(root.style.getPropertyValue("--boot-idle")).toBe("#e3e8ef"); // Querithm Light's own splash tier
    });

    it("keeps the loaded setting for the next start, in the webview and in the shell", async () => {
      h.tauri = true;
      h.rpc.mockImplementation(async (m: string) =>
        m === "system.ping" ? { version: "1" } : m === "settings.get" ? [{ key: "ui.settings", valueJson: JSON.stringify({ theme: "light" }) }] : m === "profile.list" || m === "group.list" ? [] : undefined,
      );
      const App = await loadApp();
      render(<App />);
      await screen.findByText("workspace");
      await waitFor(() => expect(localStorage.getItem("querithm.theme")).toBe("light"));
      await waitFor(() => expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"]]));
      act(() => useSettings.getState().set({ theme: "system" }));
      await waitFor(() => expect(localStorage.getItem("querithm.theme")).toBe("system"));
      await waitFor(() => expect(h.themeKeep.mock.calls).toEqual([["light #0f1117 #ffffff"], ["system #0f1117 #ffffff"]]));
      // A new pick is a new window colour for the shell (plans/app-themes §6.3); a syntax-only change is not.
      act(() => useSettings.getState().set({ darkTheme: "cortado" }));
      await waitFor(() => expect(h.themeKeep.mock.calls.at(-1)).toEqual(["system #221a13 #ffffff"]));
      act(() => useSettings.getState().set({ syntaxColors: "neon" }));
      await waitFor(() => expect(JSON.parse(localStorage.getItem("querithm.themeBoot") ?? "{}").syntax).toBe("neon"));
      expect(h.themeKeep).toHaveBeenCalledTimes(3);
    });

    it("settings that could not be read never replace the kept theme with the default", async () => {
      h.tauri = true;
      localStorage.setItem("querithm.theme", "light");
      h.rpc.mockImplementation(async (m: string) => {
        if (m === "settings.get") throw new Error("store busy");
        return m === "system.ping" ? { version: "1" } : m === "profile.list" || m === "group.list" ? [] : undefined;
      });
      const App = await loadApp();
      render(<App />);
      await screen.findByText("workspace");
      expect(useSettings.getState().loaded).toBe(true);
      expect(localStorage.getItem("querithm.theme")).toBe("light");
      expect(h.themeKeep).not.toHaveBeenCalled();
    });

    it("names the syntax colors on the document and re-applies the editor theme when they change", async () => {
      const App = await loadApp();
      render(<App />);
      await waitFor(() => expect(document.documentElement.dataset.syntax).toBe("default"));
      h.applyTheme.mockClear();
      act(() => useSettings.setState({ syntaxColors: "standard" }));
      await waitFor(() => expect(document.documentElement.dataset.syntax).toBe("standard"));
      expect(h.applyTheme).toHaveBeenCalled();
    });

    it("names the App Theme on the document by its mode and id; a pick of the other mode or an unknown id shows Querithm Dark/Light", async () => {
      const App = await loadApp();
      render(<App />);
      const root = document.documentElement;
      await waitFor(() => expect(root.dataset.appTheme).toBe("dark"));
      act(() => useSettings.setState({ theme: "dark", darkTheme: "cortado" }));
      await waitFor(() => expect(root.dataset.appTheme).toBe("cortado"));
      expect(root.dataset.theme).toBe("dark");
      act(() => useSettings.setState({ theme: "light", lightTheme: "vellum" }));
      await waitFor(() => expect(root.dataset.appTheme).toBe("vellum"));
      expect(root.dataset.theme).toBe("light");
      act(() => useSettings.setState({ lightTheme: "cortado" })); // a dark theme as the light pick (a hand-edited file)
      await waitFor(() => expect(root.dataset.appTheme).toBe("light"));
      act(() => useSettings.setState({ italicComments: false }));
      await waitFor(() => expect(root.dataset.comments).toBe("upright"));
    });

    it("applies the interface scale to the webview when it changes, re-rendering no part of the shell (L26)", async () => {
      h.tauri = true;
      const App = await loadApp();
      render(<App />);
      await screen.findByText("workspace");
      expect(h.setZoom).not.toHaveBeenCalled(); // 100 % at start: no call
      const before = h.topBarRenders;
      act(() => useSettings.getState().set({ uiScale: 125 }));
      await waitFor(() => expect(h.setZoom).toHaveBeenCalledWith(1.25));
      act(() => useSettings.getState().set({ uiScale: 400 }));
      await waitFor(() => expect(h.setZoom).toHaveBeenLastCalledWith(1.5)); // clamped by the store
      expect(h.topBarRenders).toBe(before);
    });

    it("starts at the scale the window last had and never drops it to 100 % while the settings load (W8)", async () => {
      h.tauri = true;
      localStorage.setItem("querithm.uiScale", "125");
      let loaded: ((v: unknown) => void) | undefined;
      h.rpc.mockImplementation(async (m: string) =>
        m === "system.ping" ? { version: "1" } : m === "settings.get" ? (loaded ? [] : new Promise((r) => (loaded = r))) : m === "profile.list" || m === "group.list" ? [] : undefined,
      );
      try {
        const App = await loadApp();
        // main.tsx, before the first render.
        await (await import("../uiScale")).applyCachedUiScale();
        expect(h.setZoom.mock.calls).toEqual([[1.25]]);
        render(<App />);
        await waitFor(() => expect(loaded).toBeDefined());
        // Before W8 the default (100 %) was applied here, then the saved scale once the engine had answered.
        expect(h.setZoom.mock.calls).toEqual([[1.25]]);
        loaded!([{ key: "ui.settings", valueJson: JSON.stringify({ uiScale: 125 }) }]);
        await screen.findByText("workspace");
        expect(useSettings.getState().uiScale).toBe(125);
        expect(h.setZoom.mock.calls).toEqual([[1.25]]);
      } finally {
        localStorage.removeItem("querithm.uiScale");
      }
    });

    it("refreshes editor options when font, minimap, wrap or decorations change", async () => {
      const App = await loadApp();
      render(<App />);
      await screen.findByText("workspace");
      h.refresh.mockClear();
      act(() => useSettings.setState({ editorFontSize: 20 } as any));
      expect(h.refresh).toHaveBeenCalled();
    });
  });
});

describe("update checks", () => {
  it("the installed app schedules update checks after boot", async () => {
    h.tauri = true;
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    await waitFor(() => expect(h.calls).toContain("updateChecks"));
    expect(h.calls.indexOf("updateChecks")).toBeGreaterThan(h.calls.indexOf("autosave"));
  });

  it("browser dev mode never checks", async () => {
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    await waitFor(() => expect(h.calls).toContain("autosave"));
    expect(h.calls).not.toContain("updateChecks");
  });

  it("bench runs stay offline", async () => {
    h.tauri = true;
    h.appInfo.mockResolvedValue({ version: "1", platform: "linux", bench: true });
    const App = await loadApp();
    render(<App />);
    await screen.findByText("workspace");
    await waitFor(() => expect(h.calls).toContain("installBench"));
    expect(h.calls).not.toContain("updateChecks");
  });
});

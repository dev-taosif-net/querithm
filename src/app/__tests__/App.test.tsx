// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";

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
}));

vi.mock("../../ipc/contract", () => ({ CONTRACT_VERSION: "1" }));
vi.mock("../../ipc/client", () => ({ rpc: (...a: any[]) => h.rpc(...a), shell: { appInfo: () => h.appInfo(), restart: () => h.restart() } }));
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

type Stores = { useSettings: typeof import("../../store/settings").useSettings; useProfiles: typeof import("../../store/profiles").useProfiles };
let stores: Stores;
/** Each test gets a fresh module graph: App boots once per module instance, so the stores must come from the same graph. */
async function loadApp() {
  vi.resetModules();
  stores = { useSettings: (await import("../../store/settings")).useSettings, useProfiles: (await import("../../store/profiles")).useProfiles };
  return (await import("../App")).App;
}
const useSettings = { setState: (s: any) => stores.useSettings.setState(s), getState: () => stores.useSettings.getState() };
const useProfiles = { getState: () => stores.useProfiles.getState() };

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
afterEach(() => { restoreLocation(); restoreLocation = () => {}; cleanup(); document.documentElement.removeAttribute("data-theme"); });

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

  it("shows the starting screen, boots, and renders every region", async () => {
    const App = await loadApp();
    render(<App />);
    expect(screen.getByText("Starting Querithm…")).toBeInTheDocument();
    for (const t of ["topbar", "banner", "explorer", "workspace", "statusbar", "dialogs", "palette", "switcher", "toasts"]) expect(await screen.findByText(t)).toBeInTheDocument();
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

    it("names the syntax theme on the document and re-applies the editor theme when it changes", async () => {
      const App = await loadApp();
      render(<App />);
      await waitFor(() => expect(document.documentElement.dataset.syntax).toBe("standard"));
      h.applyTheme.mockClear();
      act(() => useSettings.setState({ syntaxTheme: "unknown" } as any));
      await waitFor(() => expect(document.documentElement.dataset.syntax).toBe("unknown"));
      expect(h.applyTheme).toHaveBeenCalled();
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
        m === "system.ping" ? { version: "1" } : m === "settings.get" ? new Promise((r) => (loaded = r)) : m === "profile.list" || m === "group.list" ? [] : undefined,
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

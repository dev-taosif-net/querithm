// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "../../app/__tests__/domPolyfills";

const h = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: any[]) => undefined as any),
  appInfo: vi.fn(async () => ({ version: "0.1.6", platform: "linux", bench: false, dataDir: "/home/someone/.local/share/querithm" })),
  updateCheck: vi.fn(async () => null as any),
  status: vi.fn(async (): Promise<any> => ({ state: "running", pid: 4242, restarts: 0 })),
  explorerAction: vi.fn(),
  tauri: true,
}));

vi.mock("../../ipc/client", () => ({ rpc: (...a: any[]) => h.rpc(...a), shell: { appInfo: () => h.appInfo(), updateCheck: () => h.updateCheck(), status: () => h.status() }, onUpdateProgress: () => () => {} }));
vi.mock("../../ipc/transport", () => ({ get isTauri() { return h.tauri; } }));
vi.mock("../../explorer/explorerWindow", () => ({ explorerAction: (...a: any[]) => h.explorerAction(...a) }));
vi.mock("../../app/commands", async () => {
  const { splitBinding } = await import("../../app/shortcuts");
  const { useSettings } = await import("../../store/settings");
  const commands = [
    { id: "query.run", title: "Execute selection or all", keys: ["F5", "Ctrl+Enter"], category: "Query" },
    { id: "query.estimatedPlan", title: "Show estimated plan", keys: ["Ctrl+L"], category: "Query" },
    { id: "query.actualPlan", title: "Run with actual plan", category: "Query" },
    { id: "editor.comment", title: "Comment selected lines", keys: ["Ctrl+K Ctrl+C"], category: "Editor" },
    { id: "tab.switchRecent", title: "Switch to recent tab", keys: ["Ctrl+Tab", "MacCtrl+Tab"], category: "Tabs" },
  ];
  const keysFor = (c: any) => {
    const o = useSettings.getState().keybindings[c.id];
    return o ? splitBinding(o) : (c.keys ?? []);
  };
  return { commands, keysFor };
});

import { SettingsDialog } from "../SettingsDialog";
import { defaultSettings, useSettings, useSettingsStatus } from "../../store/settings";
import { useUpdates } from "../../store/updates";
import { useUi } from "../../store/ui";
import { resetAppInfo } from "../../app/appInfo";

const close = vi.fn();
const open = (props: Partial<Parameters<typeof SettingsDialog>[0]> = {}) => render(<SettingsDialog close={close} {...props} />);
const tab = (name: RegExp | string) => screen.getByRole("tab", { name });
const status = () => screen.getByRole("status").textContent;
const raf = () => act(async () => { await new Promise((r) => requestAnimationFrame(() => r(null))); });

beforeEach(() => {
  vi.clearAllMocks();
  resetAppInfo();
  h.tauri = true;
  useSettings.setState({ ...defaultSettings, pinnedProfiles: ["p1"], loaded: true });
  useSettingsStatus.setState({ save: "saved", error: undefined, loadFailed: false });
  useUpdates.setState({ phase: "idle", info: null, error: null, checkedAt: null, checkError: null });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("Settings: pages and navigation", () => {
  it("is a dialog with a vertical tablist of eight pages; focus starts in search", async () => {
    open();
    expect(screen.getByRole("dialog", { name: "Settings" })).toBeInTheDocument();
    const list = screen.getByRole("tablist", { name: "Settings pages" });
    expect(list).toHaveAttribute("aria-orientation", "vertical");
    expect(within(list).getAllByRole("tab").map((t) => t.textContent)).toEqual([
      "Appearance", "Editor", "Formatting", "Query execution", "Tabs & recovery", "Object Explorer", "Keyboard shortcuts", "About",
    ]);
    await raf();
    expect(document.activeElement).toBe(screen.getByRole("searchbox", { name: "Search settings" }));
  });

  it("arrow keys, Home and End move between pages and show them (selection follows focus)", async () => {
    open({ page: "appearance" });
    fireEvent.keyDown(tab("Appearance"), { key: "ArrowDown" });
    expect(tab("Editor")).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tabpanel", { name: "Editor" })).toBeInTheDocument();
    await raf();
    expect(document.activeElement).toBe(tab("Editor"));
    fireEvent.keyDown(tab("Editor"), { key: "End" });
    expect(tab("About")).toHaveAttribute("aria-selected", "true");
    fireEvent.keyDown(tab("About"), { key: "ArrowDown" });
    expect(tab("Appearance")).toHaveAttribute("aria-selected", "true");
    expect(tab("Appearance")).toHaveAttribute("tabindex", "0");
    expect(tab("Editor")).toHaveAttribute("tabindex", "-1");
  });

  it("reopens on the page used last in this session", async () => {
    const a = open();
    await userEvent.setup().click(tab("Formatting"));
    a.unmount();
    open();
    expect(tab("Formatting")).toHaveAttribute("aria-selected", "true");
  });

  it("opened at a page, focus goes to that page's tab; a focus target wins", async () => {
    const a = open({ page: "keyboard" });
    await raf();
    expect(document.activeElement).toBe(tab("Keyboard shortcuts"));
    a.unmount();
    open({ page: "about", focus: "setting-checkUpdates" });
    await raf();
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Check for updates" }));
  });
});

describe("Settings: rows", () => {
  it("a switch changes its setting at once", async () => {
    open({ page: "editor" });
    const sw = screen.getByRole("switch", { name: "Wrap long lines" });
    expect(sw).not.toBeChecked();
    await userEvent.setup().click(sw);
    expect(useSettings.getState().wordWrap).toBe(true);
    expect(sw).toBeChecked();
  });

  it("a segmented control is a radio group; choosing a theme applies it", async () => {
    open({ page: "appearance" });
    const group = screen.getByRole("radiogroup", { name: "Color theme" });
    await userEvent.setup().click(within(group).getByRole("radio", { name: "Light" }));
    expect(useSettings.getState().theme).toBe("light");
  });

  it("syntax colors are a radio group of theme cards with a preview; choosing one applies it and reset brings Standard back", async () => {
    open({ page: "appearance" });
    const group = screen.getByRole("radiogroup", { name: "Syntax colors" });
    const standard = within(group).getByRole("radio", { name: "Standard" });
    expect(standard).toBeChecked();
    expect(standard).toHaveAccessibleDescription(/everyday SQL/);
    expect(within(group).getAllByText("DECLARE").length).toBe(within(group).getAllByRole("radio").length);
    useSettings.setState({ syntaxTheme: "other" as never });
    expect(await screen.findByRole("button", { name: "Reset Syntax colors to the default, Standard" })).toBeInTheDocument();
    await userEvent.setup().click(standard);
    expect(useSettings.getState().syntaxTheme).toBe("standard");
  });

  it("a number applies when valid, is never stored empty or out of range, and reverts on blur", () => {
    open({ page: "editor" });
    const box = screen.getByRole("spinbutton", { name: "Font size" });
    fireEvent.focus(box);
    fireEvent.change(box, { target: { value: "1" } });
    expect(useSettings.getState().editorFontSize).toBe(13);
    expect(box).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByText("Allowed: 9–24")).toBeInTheDocument();
    fireEvent.change(box, { target: { value: "16" } });
    expect(useSettings.getState().editorFontSize).toBe(16);
    expect(box).not.toHaveAttribute("aria-invalid");
    fireEvent.change(box, { target: { value: "" } });
    fireEvent.blur(box);
    expect(useSettings.getState().editorFontSize).toBe(16);
    expect(box).toHaveValue(16);
  });

  it("row limit: No limit stores 0 with a memory warning; unticking brings the number back", async () => {
    open({ page: "execution" });
    const user = userEvent.setup();
    const box = screen.getByRole("spinbutton", { name: "Rows fetched per result set" });
    fireEvent.focus(box);
    fireEvent.change(box, { target: { value: "2000" } });
    fireEvent.blur(box);
    expect(useSettings.getState().maxRows).toBe(2000);
    await user.click(screen.getByRole("checkbox", { name: "No limit" }));
    expect(useSettings.getState().maxRows).toBe(0);
    expect(box).toBeDisabled();
    expect(screen.getByText(/No limit keeps every fetched row in memory/)).toBeInTheDocument();
    await user.click(screen.getByRole("checkbox", { name: "No limit" }));
    expect(useSettings.getState().maxRows).toBe(2000);
    expect(screen.getByText(/It is not TOP or LIMIT/)).toBeInTheDocument();
  });

  it("SQL Server-only settings carry the chip and say what to use on PostgreSQL", () => {
    open({ page: "execution" });
    const row = screen.getByRole("switch", { name: "Reads per table" }).closest("[data-setting]") as HTMLElement;
    expect(within(row).getByText("SQL Server")).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Reads per table" })).toHaveAccessibleDescription(/PostgreSQL: use Explain/);
  });

  it("a dependent row stays focusable, says why it is off, and ignores changes", async () => {
    useSettings.setState({ reducedDecorations: true, minimap: true });
    open({ page: "editor" });
    const sw = screen.getByRole("switch", { name: "Show minimap" });
    expect(sw).toHaveAttribute("aria-disabled", "true");
    expect(sw).toHaveAccessibleDescription(/Off while Reduce visual effects is on/);
    await userEvent.setup().click(sw);
    expect(useSettings.getState().minimap).toBe(true);
  });

  it("SQL Server's Preserve keyword case is described honestly, and typing case is then off", async () => {
    open({ page: "formatting" });
    await userEvent.setup().click(screen.getByRole("radio", { name: "Preserve" }));
    expect(screen.getByText(/Pascal case \(Select\)/)).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Change keyword case as I type" })).toHaveAttribute("aria-disabled", "true");
  });

  it("a changed row shows a reset that restores the default, says so, and keeps focus on the control", async () => {
    open({ page: "editor" });
    const user = userEvent.setup();
    expect(screen.queryByRole("button", { name: /Reset Wrap long lines/ })).toBeNull();
    await user.click(screen.getByRole("switch", { name: "Wrap long lines" }));
    expect(screen.getByRole("switch", { name: "Wrap long lines" })).toHaveAccessibleDescription(/Changed from the default/);
    await user.click(screen.getByRole("button", { name: "Reset Wrap long lines to the default, off" }));
    expect(useSettings.getState().wordWrap).toBe(false);
    await raf();
    await waitFor(() => expect(status()).toBe("Wrap long lines reset to off."));
    expect(document.activeElement).toBe(screen.getByRole("switch", { name: "Wrap long lines" }));
  });

  it("Restore defaults asks once, resets only that page, and never touches pins", async () => {
    useSettings.setState({ wordWrap: true, editorFontSize: 18, theme: "light" });
    open({ page: "editor" });
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Restore defaults" }));
    expect(screen.getByText("Reset 2 settings on this page?")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Reset" }));
    expect(useSettings.getState()).toMatchObject({ wordWrap: false, editorFontSize: 13, theme: "light", pinnedProfiles: ["p1"] });
    expect(screen.queryByRole("button", { name: "Restore defaults" })).toBeNull();
  });

  it("the Object Explorer window mode goes through the explorer layout action", async () => {
    open({ page: "explorer" });
    await userEvent.setup().click(screen.getByRole("radio", { name: "Auto hide" }));
    expect(h.explorerAction).toHaveBeenCalledWith({ mode: "autoHide" });
  });
});

describe("Settings: search", () => {
  it("finds rows across pages, counts them in the rail, and announces the count once typing pauses", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    open();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.type(screen.getByRole("searchbox", { name: "Search settings" }), "statistics");
    const results = screen.getByRole("region", { name: "Search results for statistics" });
    expect(within(results).getByRole("switch", { name: "Server CPU and elapsed time" })).toBeInTheDocument();
    expect(within(results).getByRole("switch", { name: "Reads per table" })).toBeInTheDocument();
    expect(within(tab(/Query execution/)).getByLabelText("2 results")).toBeInTheDocument();
    await act(async () => { vi.advanceTimersByTime(600); });
    expect(status()).toBe("2 results on 1 page.");
  });

  it("finds About by 'version' and says when nothing matches", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    open();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const box = screen.getByRole("searchbox", { name: "Search settings" });
    await user.type(box, "version");
    await user.click(screen.getByRole("button", { name: "Open About" }));
    expect(tab("About")).toHaveAttribute("aria-selected", "true");
    expect(box).toHaveValue("");
    await user.type(box, "qqqq");
    expect(screen.getByText("No settings match “qqqq”.")).toBeInTheDocument();
    await act(async () => { vi.advanceTimersByTime(600); });
    expect(status()).toBe("No settings match “qqqq”.");
  });

  it("Escape in a search box with text clears it; the next Escape closes the dialog", async () => {
    open();
    const user = userEvent.setup();
    const box = screen.getByRole("searchbox", { name: "Search settings" });
    await user.type(box, "font");
    await user.keyboard("{Escape}");
    expect(box).toHaveValue("");
    expect(close).not.toHaveBeenCalled();
    await user.keyboard("{Escape}");
    expect(close).toHaveBeenCalledTimes(1);
  });
});

describe("Settings: keyboard shortcuts", () => {
  const recorder = () => screen.getByRole("button", { name: /^Recording a shortcut/ });
  // Opened at a page, the dialog focuses that page's tab on its first frame. Wait for it: a frame that lands later takes
  // focus from the box being typed into, or blurs (and so stops) the recorder.
  const openKeyboard = async () => {
    open({ page: "keyboard" });
    await raf();
  };

  it("lists every command, unbound ones as Not set, chords as the editor's; macOS-only keys hidden elsewhere", () => {
    open({ page: "keyboard" });
    const row = (t: string) => screen.getByRole("rowheader", { name: t }).closest("tr") as HTMLElement;
    expect(within(row("Run with actual plan")).getByText("Not set")).toBeInTheDocument();
    expect(within(row("Execute selection or all")).getAllByRole("button", { name: /^Execute selection or all: / }).map((b) => b.getAttribute("aria-label"))).toEqual([
      "Execute selection or all: F5", "Execute selection or all: Control Enter",
    ]);
    expect(within(row("Comment selected lines")).queryByRole("button", { name: /^Comment selected lines:/ })).toBeNull();
    expect(within(row("Switch to recent tab")).getAllByRole("button", { name: /^Switch to recent tab:/ })).toHaveLength(1);
  });

  it("records a replacement for one key: modifiers alone wait, Escape cancels without closing, keys apply", async () => {
    await openKeyboard();
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Show estimated plan: Control L" }));
    await raf(); // the recorder takes focus on its first frame, and Escape goes to whatever has focus
    fireEvent.keyDown(recorder(), { key: "Control", code: "ControlLeft", ctrlKey: true });
    expect(recorder()).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(close).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: /^Recording a shortcut/ })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Show estimated plan: Control L" }));
    fireEvent.keyDown(recorder(), { key: "e", code: "KeyE", ctrlKey: true, shiftKey: true });
    expect(useSettings.getState().keybindings).toEqual({ "query.estimatedPlan": "Ctrl+Shift+E" });
    await raf();
    await waitFor(() => expect(status()).toBe("Show estimated plan: Control Shift E."));
  });

  it("refuses a bare letter or Tab-like keys with the reason, and keeps recording", async () => {
    await openKeyboard();
    await userEvent.setup().click(screen.getByRole("button", { name: "Add a shortcut for Run with actual plan" }));
    fireEvent.keyDown(recorder(), { key: "k", code: "KeyK" });
    expect(screen.getByText(/Add Ctrl or Alt/)).toBeInTheDocument();
    fireEvent.keyDown(recorder(), { key: "c", code: "KeyC", ctrlKey: true });
    expect(screen.getByText(/copying, pasting/)).toBeInTheDocument();
    expect(recorder()).toBeInTheDocument();
    expect(useSettings.getState().keybindings).toEqual({});
    fireEvent.keyDown(recorder(), { key: "Tab", code: "Tab" });
    expect(screen.queryByRole("button", { name: /^Recording a shortcut/ })).toBeNull();
    expect(useSettings.getState().keybindings).toEqual({});
  });

  it("a shortcut another command uses is offered for reassignment, never duplicated", async () => {
    await openKeyboard();
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Add a shortcut for Run with actual plan" }));
    fireEvent.keyDown(recorder(), { key: "l", code: "KeyL", ctrlKey: true });
    expect(screen.getByText("Ctrl+L is used by Show estimated plan.")).toBeInTheDocument();
    expect(useSettings.getState().keybindings).toEqual({});
    await user.click(screen.getByRole("button", { name: "Reassign" }));
    expect(useSettings.getState().keybindings).toEqual({ "query.estimatedPlan": ",", "query.actualPlan": "Ctrl+L" });
  });

  it("Delete removes one key; the row reset and Reset all restore the defaults", async () => {
    await openKeyboard();
    const user = userEvent.setup();
    fireEvent.keyDown(screen.getByRole("button", { name: "Execute selection or all: F5" }), { key: "Delete" });
    expect(useSettings.getState().keybindings).toEqual({ "query.run": "Ctrl+Enter" });
    await user.click(screen.getByRole("button", { name: "Reset Execute selection or all to its default shortcuts" }));
    expect(useSettings.getState().keybindings).toEqual({});
    fireEvent.keyDown(screen.getByRole("button", { name: "Show estimated plan: Control L" }), { key: "Delete" });
    await user.click(screen.getByRole("button", { name: "Reset all shortcuts" }));
    await user.click(screen.getByRole("button", { name: "Reset all" }));
    expect(useSettings.getState().keybindings).toEqual({});
  });

  it("search filters by command or key, and its Escape clears it first", async () => {
    await openKeyboard();
    const user = userEvent.setup();
    const box = screen.getByRole("searchbox", { name: "Search shortcuts by command or key" });
    await user.type(box, "ctrl+l");
    expect([...document.querySelectorAll("tr[data-command]")].map((r) => r.getAttribute("data-command"))).toEqual(["query.estimatedPlan"]);
    await user.keyboard("{Escape}");
    expect(box).toHaveValue("");
    expect(close).not.toHaveBeenCalled();
  });
});

describe("Settings: About", () => {
  it("shows the version the shell reports, the platform and the credit; Copy copies the version only", async () => {
    const writeText = vi.fn(async () => {});
    const user = userEvent.setup(); // user-event installs its own clipboard; ours goes over it
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    open({ page: "about" });
    expect(await screen.findByText("Version 0.1.6")).toBeInTheDocument();
    expect(screen.getByText(/Linux · Engine contract/)).toBeInTheDocument();
    expect(screen.getByText("Made by Taosif")).toBeInTheDocument();
    expect(screen.getByText("© 2026 Taosif · MIT License")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Copy version info" }));
    expect(writeText).toHaveBeenCalledWith(expect.stringMatching(/^Querithm 0\.1\.6 · Linux · engine contract \d/));
    const copied = (writeText.mock.calls[0] as unknown as [string])[0];
    expect(copied).not.toMatch(/Taosif|someone|querithm\//);
    await raf();
    await waitFor(() => expect(status()).toBe("Version info copied."));
  });

  it("shows the logs folder with Copy path, and Copy diagnostics copies version, engine state and the scrubbed log tail (M21)", async () => {
    const writeText = vi.fn(async () => {});
    const user = userEvent.setup();
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    h.rpc.mockImplementation(async (m: string, p?: any) => m === "system.logTail"
      ? { logDir: "/home/someone/.local/share/querithm/logs", lines: ["== sidecar-20261103-42.log ==", `2026-11-03T10:00:00Z [info] host: Querithm sidecar 0.2.0, data /home/someone/.local/share/querithm (asked ${p.lines})`, "connect Password=***"] }
      : undefined);
    open({ page: "about" });
    expect(await screen.findByText("/home/someone/.local/share/querithm/logs")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Copy path" }));
    expect(writeText).toHaveBeenLastCalledWith("/home/someone/.local/share/querithm/logs");
    await user.click(screen.getByRole("button", { name: "Copy diagnostics" }));
    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(2));
    const text = (writeText.mock.calls[1] as unknown as [string])[0];
    expect(text).toMatch(/^Querithm 0\.1\.6 · Linux · engine contract \d/);
    expect(text).toContain("Engine: running (pid 4242)");
    expect(text).toContain("Newest log lines (3, secrets removed):");
    expect(text).toContain("data <data dir> (asked 200)");
    expect(text).toContain("connect Password=***");
    // The user's name in the data folder path, and the credit, stay out of it.
    expect(text).not.toMatch(/someone|Taosif/);
    h.rpc.mockReset();
    h.rpc.mockImplementation(async () => undefined);
  });

  it("Copy diagnostics still copies what it has when the engine cannot read its logs", async () => {
    const writeText = vi.fn(async () => {});
    const user = userEvent.setup();
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    h.rpc.mockRejectedValue(new Error("engine down"));
    h.status.mockRejectedValueOnce(new Error("no shell"));
    open({ page: "about" });
    await screen.findByText("Version 0.1.6");
    await user.click(screen.getByRole("button", { name: "Copy diagnostics" }));
    await waitFor(() => expect(writeText).toHaveBeenCalled());
    const text = (writeText.mock.calls[0] as unknown as [string])[0];
    expect(text).toContain("Engine: the shell did not answer");
    expect(text).toContain("Log lines: the engine could not read them.");
    h.rpc.mockReset();
    h.rpc.mockImplementation(async () => undefined);
  });

  it("says when the version could not be read, and offers no copy", async () => {
    h.appInfo.mockRejectedValueOnce(new Error("no shell"));
    open({ page: "about" });
    expect(await screen.findByText("Version unavailable: the app shell did not answer.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Copy version info" })).toBeNull();
  });

  it("the rail footer shows the version and opens About", async () => {
    open({ page: "editor" });
    await userEvent.setup().click(await screen.findByRole("button", { name: "Querithm 0.1.6" }));
    expect(tab("About")).toHaveAttribute("aria-selected", "true");
  });

  it("Check for updates reports in place: up to date with the time, or the new version with an Update button", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true, now: new Date(2026, 9, 30, 14, 2) });
    h.updateCheck.mockResolvedValueOnce(null).mockResolvedValueOnce({ version: "0.2.0", currentVersion: "0.1.6" });
    open({ page: "about" });
    expect(screen.getByText("Not checked yet.")).toBeInTheDocument();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(screen.getByRole("button", { name: "Check for updates" }));
    expect(await screen.findByText(/Up to date · checked/)).toBeInTheDocument();
    await act(async () => { vi.advanceTimersByTime(50); });
    expect(status()).toBe("Querithm is up to date.");
    await user.click(screen.getByRole("button", { name: "Check for updates" }));
    await user.click(await screen.findByRole("button", { name: "Update to 0.2.0…" }));
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "update" });
  });

  it("Check for updates stays focusable but inactive while working; a development build says so", async () => {
    useUpdates.setState({ phase: "checking" });
    h.tauri = false;
    open({ page: "about" });
    const btn = screen.getByRole("button", { name: "Checking…" });
    expect(btn).toHaveAttribute("aria-disabled", "true");
    expect(btn).not.toBeDisabled();
    await userEvent.setup().click(btn);
    expect(h.updateCheck).not.toHaveBeenCalled();
    expect(screen.getByText("Development build")).toBeInTheDocument();
  });

  it("automatic update checks can be turned off, with a note about missed fixes", async () => {
    open({ page: "about" });
    await userEvent.setup().click(screen.getByRole("switch", { name: "Check for updates automatically" }));
    expect(useSettings.getState().autoUpdateCheck).toBe(false);
    expect(screen.getByText(/You won't hear about fixes/)).toBeInTheDocument();
  });
});

describe("Settings: saving", () => {
  it("the footer says changes are saved; a failed save says so and Retry tries again", async () => {
    open();
    expect(screen.getByText("Changes apply at once and are saved automatically.")).toBeInTheDocument();
    act(() => useSettingsStatus.setState({ save: "failed", error: "engine down" }));
    expect(screen.getByText("Not saved: engine down")).toBeInTheDocument();
    h.rpc.mockResolvedValue(undefined);
    await userEvent.setup().click(screen.getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(useSettingsStatus.getState().save).toBe("saved"));
  });

  it("Done closes", async () => {
    open();
    await userEvent.setup().click(screen.getByRole("button", { name: "Done" }));
    expect(close).toHaveBeenCalled();
  });
});

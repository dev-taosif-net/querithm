// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";

const h = vi.hoisted(() => ({
  a: { cancelQuery: vi.fn(), newQueryTab: vi.fn(), runQuery: vi.fn(), showPlan: vi.fn(), txAction: vi.fn() },
  cmd: {} as Record<string, ReturnType<typeof vi.fn>>,
  scope: { showRunScope: vi.fn(), clearRunScope: vi.fn() },
}));
vi.mock("../actions", () => h.a);
vi.mock("../commands", () => {
  const keys: Record<string, string[]> = {
    "query.run": ["F5", "Ctrl+Enter"], "query.runStatement": ["Ctrl+Shift+Enter"], "query.cancel": ["Alt+Pause"], "query.estimatedPlan": ["Ctrl+L"],
    "query.toggleActualPlan": ["Ctrl+M"], "tab.new": ["Ctrl+N"], "file.open": ["Ctrl+O"], "tab.reopenClosed": ["Ctrl+Shift+T"],
    "palette.connections": ["Ctrl+Shift+U"], "palette.databases": ["Ctrl+U"], "palette.objects": ["Ctrl+T"], "view.history": ["Ctrl+H"],
    "view.settings": ["Ctrl+,"], "nav.back": ["Alt+Left"], "nav.forward": ["Alt+Right"],
  };
  return {
    displayKeys: (k: string) => k,
    commands: Object.entries(keys).map(([id, k]) => ({ id, keys: k, run: (h.cmd[id] = vi.fn()) })),
    keysFor: (c: { keys?: string[] }) => c.keys ?? [],
  };
});
const nav = vi.hoisted(() => ({ goBack: vi.fn(), goForward: vi.fn() }));
vi.mock("../navigation", async () => {
  const { create } = await import("zustand");
  return { ...nav, useNav: create(() => ({ canBack: false, canForward: false })) };
});
vi.mock("../../editor/runScope", () => h.scope);
vi.mock("../../results/MessagesPanel", () => ({ fmtMs: (ms?: number | null) => (ms == null ? "—" : `${ms} ms`) }));
const files = vi.hoisted(() => ({ repickFile: vi.fn(async () => true) }));
vi.mock("../../connections/fileAccess", () => files);

import { LONG_TX_MS, SWAP_GUARD_MS, TopBar } from "../TopBar";
import { StatusBar } from "../StatusBar";
import { Logo } from "../Logo";
import { useEngineStatus } from "../engineStatus";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";
import { useProfiles } from "../../store/profiles";
import { useExecutions } from "../../store/executions";
import { useConnections } from "../../store/connections";
import { useDiag } from "../../store/diag";
import { executionData } from "../../results/model";

const ms: any = { id: "ms1", name: "Sales", engine: "mssql", environment: "dev", auth: "windows" };
const pg: any = { id: "pg1", name: "Analytics", engine: "pg", environment: "prod", auth: "trust", color: "#ff0000" };
const base = { txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.35, actualPlan: false };
function setTab(extra: any = {}) {
  useTabs.setState({ tabs: { t1: { ...base, id: "t1", title: "one.sql", profileId: "ms1", database: "Sales", ...extra } as any }, order: ["t1"], activeId: "t1", mru: ["t1"] });
}
const view = (state: string, extra: any = {}) =>
  useExecutions.setState({ views: { e1: { id: "e1", tabId: "t1", sessionId: "s", state, startedAt: Date.now(), resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql: "", lineOffset: 1, ...extra } as any } });
const connect = vi.fn(async () => true);

beforeEach(() => {
  vi.clearAllMocks();
  useUi.setState({ palette: null, dialogs: [] });
  useProfiles.setState({ profiles: [ms, pg], groups: [] });
  useConnections.setState({ disconnected: {}, servers: {}, connect } as any);
  useExecutions.setState({ views: {} });
  useDiag.setState({ debugs: {} } as any);
  executionData.clear();
  useEngineStatus.setState({ state: "running" } as any);
  setTab();
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); });

const btn = (name: string | RegExp) => screen.getByRole("button", { name });
const run = () => btn(/^(Run|Continue|Cancel)/);
const unavailable = (el: HTMLElement) => expect(el).toHaveAttribute("aria-disabled", "true");
const available = (el: HTMLElement) => expect(el).not.toHaveAttribute("aria-disabled");

describe("TopBar: brand and navigation", () => {
  it("keeps the logo and the Querithm wordmark", () => {
    render(<TopBar />);
    expect(screen.getByText("Querithm")).toBeInTheDocument();
  });

  it("go back / go forward are unavailable until there is history; clicks go back and forward; tooltips show the shortcut", async () => {
    const { useNav } = await import("../navigation");
    render(<TopBar />);
    unavailable(btn("Go back"));
    unavailable(btn("Go forward"));
    await userEvent.click(btn("Go back"));
    expect(nav.goBack).not.toHaveBeenCalled();
    expect(btn("Go back").getAttribute("title")).toBe("Go back (Alt+Left)");
    act(() => useNav.setState({ canBack: true, canForward: true }));
    await userEvent.click(btn("Go back"));
    await userEvent.click(btn("Go forward"));
    expect(nav.goBack).toHaveBeenCalledTimes(1);
    expect(nav.goForward).toHaveBeenCalledTimes(1);
  });

  it("New query opens a tab; its menu opens one on another connection, a file, or a closed tab", async () => {
    const user = userEvent.setup();
    render(<TopBar />);
    await user.click(btn("New query"));
    expect(h.a.newQueryTab).toHaveBeenCalledTimes(1);
    await user.click(btn("More ways to open a query"));
    await user.click(screen.getByRole("menuitem", { name: /New query on another connection/ }));
    expect(h.a.newQueryTab).toHaveBeenCalledTimes(2);
    expect(useUi.getState().palette).toEqual({ mode: "connections" });
    await user.click(btn("More ways to open a query"));
    await user.click(screen.getByRole("menuitem", { name: /Open SQL file/ }));
    await user.click(btn("More ways to open a query"));
    await user.click(screen.getByRole("menuitem", { name: /Reopen closed tab/ }));
    expect(h.cmd["file.open"]).toHaveBeenCalledTimes(1);
    expect(h.cmd["tab.reopenClosed"]).toHaveBeenCalledTimes(1);
  });

  it("has no comment buttons (they are in the editor's right-click menu, on their shortcuts and in the palette)", () => {
    render(<TopBar />);
    expect(screen.queryByText(/Comment/)).toBeNull();
  });
});

describe("TopBar: launch strip", () => {
  it.each([
    ["ms1", "DEV", "Run on DEV Sales › Sales (F5)"],
    ["pg1", "PROD", "Run on PROD Analytics › Sales (F5)"],
  ])("names the run target on %s: environment label, connection › database", (pid, env, title) => {
    setTab({ profileId: pid });
    render(<TopBar />);
    expect(screen.getByText(env)).toBeInTheDocument();
    expect(run()).toHaveAttribute("title", title);
    expect(screen.getByRole("img", { name: pid === "pg1" ? "PostgreSQL" : "Microsoft SQL Server" })).toBeInTheDocument();
  });

  it("only prod gets the solid badge (keyed on the environment, filled from its own color); others an outlined label", () => {
    setTab({ profileId: "pg1" });
    const { unmount } = render(<TopBar />);
    expect(screen.getByText("PROD").style.background).not.toBe("");
    unmount();
    setTab({ profileId: "ms1" });
    render(<TopBar />);
    const dev = screen.getByText("DEV");
    expect(dev.style.background).toBe("");
    expect(dev.style.borderColor).not.toBe("");
  });

  it("connection and database are two buttons: the connections palette and the databases palette", async () => {
    const user = userEvent.setup();
    render(<TopBar />);
    await user.click(btn("Change connection, now DEV Sales"));
    expect(useUi.getState().palette).toEqual({ mode: "connections" });
    await user.click(btn("Change database, now Sales"));
    expect(useUi.getState().palette).toEqual({ mode: "databases" });
    expect(btn("Change database, now Sales")).toHaveAttribute("title", "Change database (Ctrl+U)");
  });

  it("shows (default) for a tab without a database", () => {
    setTab({ database: undefined });
    render(<TopBar />);
    expect(btn("Change database, now (default)")).toHaveTextContent("(default)");
  });

  it("a tab without a connection offers Connect…; Run stays available and asks for one (query.run does)", async () => {
    const user = userEvent.setup();
    setTab({ profileId: undefined, database: undefined });
    render(<TopBar />);
    await user.click(btn("Connect…"));
    expect(useUi.getState().palette).toEqual({ mode: "connections" });
    available(run());
    await user.click(run());
    expect(h.cmd["query.run"]).toHaveBeenCalledTimes(1);
  });

  it("without any tab Run, Explain and Begin Tx are unavailable, stay focusable, and do nothing", async () => {
    const user = userEvent.setup();
    useTabs.setState({ tabs: {}, order: [], activeId: undefined });
    render(<TopBar />);
    for (const el of [run(), btn(/^Explain/), btn("Begin transaction")]) {
      unavailable(el);
      expect(el).not.toBeDisabled();
      await user.click(el);
    }
    expect(h.cmd["query.run"]).not.toHaveBeenCalled();
    expect(h.a.showPlan).not.toHaveBeenCalled();
    expect(h.a.txAction).not.toHaveBeenCalled();
  });

  it("a server the user disconnected keeps its names and says so; Begin Tx waits for a reconnect", () => {
    useConnections.setState({ disconnected: { ms1: true } });
    render(<TopBar />);
    expect(btn(/Change connection, now DEV Sales, disconnected/)).toBeInTheDocument();
    expect(btn("Change database, now Sales").getAttribute("title")).toMatch(/Disconnected: running asks before reconnecting/);
    unavailable(btn("Begin transaction"));
  });

  it("a lost connection offers Reconnect, which reconnects that server", async () => {
    useConnections.setState({ servers: { ms1: { profileId: "ms1", status: "error", databases: [] } } } as any);
    render(<TopBar />);
    expect(btn(/connection lost/)).toBeInTheDocument();
    await userEvent.setup().click(btn("Connection lost. Reconnect"));
    expect(connect).toHaveBeenCalledWith("ms1");
    unavailable(btn("Begin transaction"));
  });
});

describe("TopBar: Run", () => {
  it("runs through the query.run command, like F5 (so it also continues a paused debugger), never runQuery directly", async () => {
    render(<TopBar />);
    await userEvent.setup().click(run());
    expect(h.cmd["query.run"]).toHaveBeenCalledTimes(1);
    expect(h.a.runQuery).not.toHaveBeenCalled();
  });

  it("while the debugger is paused it reads Continue and still runs query.run", async () => {
    setTab({ debugId: "d1" });
    useDiag.setState({ debugs: { d1: { state: "paused" } } } as any);
    render(<TopBar />);
    expect(run()).toHaveAccessibleName("Continue debugging");
    expect(run()).toHaveTextContent(/^Continue/);
    await userEvent.setup().click(run());
    expect(h.cmd["query.run"]).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["a busy tab", () => setTab({ busy: true })],
    ["a running execution", () => { setTab({ activeExecutionId: "e1" }); view("running"); }],
    ["a streaming execution", () => { setTab({ activeExecutionId: "e1" }); view("streaming"); }],
  ])("%s turns Run into Cancel in the same slot; Cancel cancels; the run menu and Begin Tx are unavailable", async (_n, arrange) => {
    arrange();
    render(<TopBar />);
    expect(run()).toHaveTextContent(/^Cancel$/);
    unavailable(btn("More ways to run"));
    unavailable(btn("Begin transaction"));
    await userEvent.setup().click(run());
    expect(h.a.cancelQuery).toHaveBeenCalledWith("t1");
  });

  it("a finished execution shows Run again", () => {
    setTab({ activeExecutionId: "e1" });
    view("done");
    render(<TopBar />);
    expect(run()).toHaveTextContent(/^Run/);
  });

  it("a click right after Run became Cancel is ignored (a double-click must not cancel); a keyboard press and a later click are not", () => {
    let now = 1000;
    vi.spyOn(performance, "now").mockImplementation(() => now);
    setTab({ activeExecutionId: "e1" });
    view("done");
    render(<TopBar />);
    act(() => view("running"));
    fireEvent.click(run(), { detail: 1 });
    expect(h.a.cancelQuery).not.toHaveBeenCalled();
    fireEvent.click(run(), { detail: 0 }); // Enter / Space
    expect(h.a.cancelQuery).toHaveBeenCalledTimes(1);
    now += SWAP_GUARD_MS + 1;
    fireEvent.click(run(), { detail: 1 });
    expect(h.a.cancelQuery).toHaveBeenCalledTimes(2);
  });

  it("the same element stays across Run ↔ Cancel, so keyboard focus survives the swap", () => {
    setTab({ activeExecutionId: "e1" });
    view("done");
    render(<TopBar />);
    const el = run();
    el.focus();
    act(() => view("running"));
    expect(run()).toBe(el);
    expect(document.activeElement).toBe(el);
  });

  it("hovering or focusing Run previews what it runs; leaving clears it; nothing is previewed while running", () => {
    render(<TopBar />);
    fireEvent.pointerEnter(run());
    expect(h.scope.showRunScope).toHaveBeenLastCalledWith("t1", "auto");
    fireEvent.pointerLeave(run());
    expect(h.scope.clearRunScope).toHaveBeenCalled();
    cleanup();
    vi.clearAllMocks();
    setTab({ busy: true });
    render(<TopBar />);
    fireEvent.pointerEnter(run());
    expect(h.scope.showRunScope).not.toHaveBeenCalled();
  });

  it.each([
    ["ms1", "Run with actual plan", "runs the query"],
    ["pg1", "Explain analyze…", "runs, asks first"],
  ])("Run ▾ on %s: current statement (previewed), and the engine's actual plan", async (pid, actual, detail) => {
    const user = userEvent.setup();
    setTab({ profileId: pid });
    render(<TopBar />);
    await user.click(btn("More ways to run"));
    const menu = screen.getByRole("menu", { name: "Run" });
    const statement = within(menu).getByRole("menuitem", { name: /Run current statement/ });
    fireEvent.pointerEnter(statement);
    expect(h.scope.showRunScope).toHaveBeenLastCalledWith("t1", "statement");
    expect(within(menu).getByText(new RegExp(detail))).toBeInTheDocument();
    await user.click(statement);
    expect(h.cmd["query.runStatement"]).toHaveBeenCalledTimes(1);
    await user.click(btn("More ways to run"));
    await user.click(screen.getByRole("menuitem", { name: new RegExp(actual) }));
    expect(h.a.showPlan).toHaveBeenCalledWith("t1", "actual");
  });
});

describe("TopBar: Explain", () => {
  it("shows the estimated plan on both engines and needs a connection", async () => {
    const user = userEvent.setup();
    for (const pid of ["ms1", "pg1"]) {
      setTab({ profileId: pid });
      const { unmount } = render(<TopBar />);
      await user.click(btn(/^Explain/));
      expect(h.a.showPlan).toHaveBeenLastCalledWith("t1", "estimated");
      unmount();
    }
    h.a.showPlan.mockClear();
    setTab({ profileId: undefined });
    render(<TopBar />);
    unavailable(btn(/^Explain/));
    await user.click(btn(/^Explain/));
    expect(h.a.showPlan).not.toHaveBeenCalled();
  });

  it("SQL Server: Explain ▾ toggles Include actual plan; Run then reads +plan and its name says so", async () => {
    const user = userEvent.setup();
    render(<TopBar />);
    await user.click(btn("Plan options"));
    const toggle = screen.getByRole("menuitemcheckbox", { name: /Include actual plan on every run/ });
    expect(toggle).toHaveAttribute("aria-checked", "false");
    await user.click(toggle);
    expect(useTabs.getState().tabs.t1.actualPlan).toBe(true);
    expect(run()).toHaveTextContent("+plan");
    expect(run()).toHaveAccessibleName(/with actual plan/);
    expect(btn(/^Explain/)).toHaveAccessibleName(/Actual plan on/);
    await user.click(btn("Plan options"));
    await user.click(screen.getByRole("menuitem", { name: /Actual plan/ }));
    expect(h.a.showPlan).toHaveBeenCalledWith("t1", "actual");
  });

  it("PostgreSQL: the menu says Explain / Explain analyze…; the actual-plan toggle is unavailable and says why", async () => {
    const user = userEvent.setup();
    setTab({ profileId: "pg1", actualPlan: true });
    render(<TopBar />);
    expect(run()).not.toHaveTextContent("+plan"); // the flag only applies to SQL Server
    await user.click(btn("Plan options"));
    expect(screen.getByRole("menuitem", { name: "Explain, doesn't run" })).toBeInTheDocument();
    const toggle = screen.getByRole("menuitemcheckbox");
    unavailable(toggle);
    expect(toggle).toHaveTextContent("SQL Server only");
    await user.click(toggle);
    expect(useTabs.getState().tabs.t1.actualPlan).toBe(true);
  });
});

describe.each<[string, string]>([["sqlite", "SQLite"], ["mySql", "MySQL"]])("TopBar: Explain on %s (estimated plan only)", (engine, name) => {
  it("Explain shows the estimated plan; actual plans and the per-run toggle are unavailable with the reason", async () => {
    const user = userEvent.setup();
    useProfiles.setState({ profiles: [ms, pg, { id: "x1", name: "X", engine, environment: "dev", auth: "none" } as any], groups: [] });
    setTab({ profileId: "x1", actualPlan: true });
    render(<TopBar />);
    expect(run()).not.toHaveTextContent("+plan");
    await user.click(btn("Plan options"));
    expect(screen.getByRole("menuitem", { name: "Explain, doesn't run" })).toBeInTheDocument();
    const actual = screen.getByRole("menuitem", { name: /^Actual plan/ });
    unavailable(actual);
    expect(actual).toHaveAccessibleName(new RegExp(`${name} shows the estimated plan only`));
    await user.click(actual);
    expect(h.a.showPlan).not.toHaveBeenCalled();
    unavailable(screen.getByRole("menuitemcheckbox"));
    await user.keyboard("{Escape}");
    await user.click(btn("More ways to run"));
    unavailable(screen.getByRole("menuitem", { name: /Run with actual plan/ }));
  });
});

describe("TopBar: transaction", () => {
  it("Begin Tx when none is open", async () => {
    render(<TopBar />);
    await userEvent.setup().click(btn("Begin transaction"));
    expect(h.a.txAction).toHaveBeenCalledWith("t1", "begin");
  });

  it("open: the TX clock ticks on its own, Commit / Rollback act; after 5 minutes the border goes to full strength", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-01-01T00:10:00Z"));
    setTab({ txState: "open", txStartedAt: Date.now() - 65_000 });
    render(<TopBar />);
    const group = screen.getByRole("group", { name: "Open transaction" });
    expect(screen.getByRole("timer")).toHaveTextContent("01:05");
    act(() => { vi.advanceTimersByTime(3000); });
    expect(screen.getByRole("timer")).toHaveTextContent("01:08");
    expect(group.className).toContain("border-warn/45");
    expect(screen.queryByRole("button", { name: "Begin transaction" })).toBeNull();
    act(() => { vi.advanceTimersByTime(LONG_TX_MS); });
    expect(group.className).toMatch(/border-warn(\s|$)/);
    fireEvent.click(btn("Commit"));
    fireEvent.click(btn("Rollback"));
    expect(h.a.txAction).toHaveBeenCalledWith("t1", "commit");
    expect(h.a.txAction).toHaveBeenCalledWith("t1", "rollback");
  });

  it("unknown state shows TX ? with Commit / Rollback (never an available Begin Tx)", async () => {
    const user = userEvent.setup();
    setTab({ txState: "unknown" });
    render(<TopBar />);
    expect(screen.getByRole("group", { name: "Transaction state unknown" })).toBeInTheDocument();
    expect(screen.getByRole("timer")).toHaveTextContent("?");
    expect(screen.queryByRole("button", { name: "Begin transaction" })).toBeNull();
    await user.click(btn("Rollback"));
    expect(h.a.txAction).toHaveBeenCalledWith("t1", "rollback");
  });

  it("Commit / Rollback are unavailable while a query runs on the pinned connection", async () => {
    setTab({ txState: "open", txStartedAt: Date.now(), busy: true });
    render(<TopBar />);
    for (const name of ["Commit", "Rollback"]) {
      unavailable(btn(name));
      expect(btn(name)).toHaveAttribute("title", "Wait for the running query");
      await userEvent.setup().click(btn(name));
    }
    expect(h.a.txAction).not.toHaveBeenCalled();
  });
});

describe("TopBar: right side, keyboard and announcements", () => {
  it("search box, history and settings open palette / dialogs; the search hint says how to reach commands", async () => {
    const user = userEvent.setup();
    render(<TopBar />);
    expect(btn("Go to table, proc, or command")).toHaveTextContent("> for commands");
    await user.click(btn("Go to table, proc, or command"));
    expect(useUi.getState().palette).toEqual({ mode: "objects" });
    await user.click(btn("Query history"));
    await user.click(btn("Settings"));
    expect(useUi.getState().dialogs.map((d) => d.kind)).toEqual(["history", "settings"]);
  });

  it("each toolbar is one Tab stop; ←/→ move (wrapping), Home/End jump, and it comes back to the control used last", () => {
    render(<TopBar />);
    const bar = screen.getByRole("toolbar", { name: "Query" });
    const items = within(bar).getAllByRole("button");
    expect(items.filter((b) => b.tabIndex === 0)).toEqual([items[0]]);
    items[0].focus();
    fireEvent.keyDown(items[0], { key: "ArrowRight" });
    expect(document.activeElement).toBe(items[1]);
    expect(items[1].tabIndex).toBe(0);
    expect(items[0].tabIndex).toBe(-1);
    fireEvent.keyDown(items[1], { key: "End" });
    expect(document.activeElement).toBe(items.at(-1));
    fireEvent.keyDown(items.at(-1)!, { key: "ArrowRight" });
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(items[0], { key: "ArrowLeft" });
    expect(document.activeElement).toBe(items.at(-1));
    fireEvent.keyDown(items.at(-1)!, { key: "Home" });
    expect(document.activeElement).toBe(items[0]);
    expect(within(screen.getByRole("toolbar", { name: "App" })).getAllByRole("button").filter((b) => b.tabIndex === 0)).toHaveLength(1);
  });

  it("a control that appears later (Reconnect) joins the toolbar without becoming a second Tab stop", async () => {
    render(<TopBar />);
    act(() => useConnections.setState({ servers: { ms1: { profileId: "ms1", status: "error", databases: [] } } } as any));
    await waitFor(() => expect(btn("Connection lost. Reconnect").tabIndex).toBe(-1));
    expect(within(screen.getByRole("toolbar", { name: "Query" })).getAllByRole("button").filter((b) => b.tabIndex === 0)).toHaveLength(1);
  });

  it("ArrowDown on a ▾ opens its menu", () => {
    render(<TopBar />);
    fireEvent.keyDown(btn("More ways to run"), { key: "ArrowDown" });
    expect(screen.getByRole("menu", { name: "Run" })).toBeInTheDocument();
    expect(btn("More ways to run")).toHaveAttribute("aria-expanded", "true");
  });

  it("a status region announces a running query and an open transaction (never the ticking clock)", () => {
    setTab({ busy: true });
    const { unmount } = render(<TopBar />);
    expect(screen.getByRole("status")).toHaveTextContent("Query running on Sales");
    unmount();
    setTab({ txState: "open", txStartedAt: Date.now() });
    render(<TopBar />);
    expect(screen.getByRole("status")).toHaveTextContent("Transaction open, connection pinned");
  });
});

describe("StatusBar", () => {
  it("without a tab says No connection and reports the engine state", () => {
    useTabs.setState({ tabs: {}, order: [], activeId: undefined });
    render(<StatusBar />);
    expect(screen.getByText("No connection")).toBeInTheDocument();
    expect(screen.getByText(/Engine ready/)).toBeInTheDocument();
  });

  it.each([
    ["running", /Engine ready/], ["restarted", /Engine ready/], ["dead", /Engine restarting/], ["failed", /Engine stopped/],
  ])("engine state %s", (state, text) => {
    useEngineStatus.setState({ state } as any);
    render(<StatusBar />);
    expect(screen.getByText(text)).toBeInTheDocument();
  });

  it.each([
    ["ms1", "DEV - Sales"],
    ["pg1", "PROD - Analytics"],
  ])("shows the connection as plain text for %s, with SPID and database (the picker is in the top bar)", (pid, label) => {
    setTab({ profileId: pid, spid: 55 });
    render(<StatusBar />);
    const conn = screen.getByText(new RegExp(`${label} \\(SPID 55\\)`));
    expect(conn.style.background).toBe("");
    expect(screen.getByText("DB: Sales")).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it.each([
    ["ms1", "dev, environment colour", "4px solid var(--q-env-dev)"],
    ["pg1", "prod, the profile's own colour", "4px solid rgb(255, 0, 0)"],
  ])("a connection on %s (%s) draws a 4px top border", (pid, _why, border) => {
    setTab({ profileId: pid });
    const { container } = render(<StatusBar />);
    expect((container.firstChild as HTMLElement).style.borderTop).toBe(border);
  });

  it.each([
    ["mssql", "prod", 1444, "10.0.4.12:1444", "max-[1279px]:hidden"],
    ["pg", "staging", 5432, "10.0.4.12", "max-[1279px]:hidden"],
    ["mySql", "dev", 3307, "10.0.4.12:3307", "max-[1439px]:hidden"],
  ])("%s on %s shows the server address, which drops at its width and stays in the connection's tooltip", (engine, environment, port, text, narrow) => {
    useProfiles.setState({ profiles: [{ id: "x1", name: "Box", engine, environment, auth: "sql", host: "10.0.4.12", port } as any], groups: [] });
    setTab({ profileId: "x1" });
    const { container } = render(<StatusBar />);
    const addr = container.querySelector("[data-q-server-address]") as HTMLElement;
    expect(addr.textContent).toBe(text);
    expect(addr.className).toContain(narrow);
    expect(screen.getByText(/ - Box/)).toHaveAttribute("title", `Server ${text}`);
  });

  it("an SSH profile marks the address and names the bastion in the tooltip", () => {
    useProfiles.setState({ profiles: [{ id: "x1", name: "Box", engine: "pg", environment: "dev", auth: "sql", host: "10.0.4.12", ssh: { host: "jump-01" } } as any], groups: [] });
    setTab({ profileId: "x1" });
    const { container } = render(<StatusBar />);
    const addr = container.querySelector("[data-q-server-address]") as HTMLElement;
    expect(addr.textContent).toBe("10.0.4.12 · SSH");
    expect(addr).toHaveAttribute("title", "Server 10.0.4.12 via SSH jump-01");
  });

  it.each(["sqlite", "duckDb"])("%s: a local database shows no server address", (engine) => {
    useProfiles.setState({ profiles: [{ id: "x1", name: "Box", engine, environment: "local", auth: "none", location: "file", host: "localhost" } as any], groups: [] });
    setTab({ profileId: "x1" });
    const { container } = render(<StatusBar />);
    expect(container.querySelector("[data-q-server-address]")).toBeNull();
    expect(screen.getByText(/ - Box/)).not.toHaveAttribute("title");
  });

  it("a tab without a connection has no coloured border", () => {
    setTab({ profileId: undefined });
    const { container } = render(<StatusBar />);
    expect((container.firstChild as HTMLElement).style.borderTop).toBe("");
  });

  it("shows (default) and (disconnected) database markers", () => {
    setTab({ database: undefined });
    useConnections.setState({ disconnected: { ms1: true } });
    render(<StatusBar />);
    expect(screen.getByText("DB: (default) (disconnected)")).toBeInTheDocument();
  });

  it("transaction: open is a quiet note (the clock is in the top bar); unknown is warned; trace and session errors appear", () => {
    setTab({ txState: "open", txStartedAt: Date.now() - 65_000, traceId: "tr", sessionError: "dial failed" });
    render(<StatusBar />);
    expect(screen.getByText("TX · connection pinned")).toBeInTheDocument();
    expect(screen.queryByText(/01:05/)).toBeNull();
    expect(screen.getByText("● Trace")).toBeInTheDocument();
    expect(screen.getByText("dial failed")).toBeInTheDocument();
    cleanup();
    setTab({ txState: "unknown" });
    render(<StatusBar />);
    expect(screen.getByText("TX state unknown")).toBeInTheDocument();
  });

  it("a tab whose in-memory database was lost says so until its next session opens", () => {
    setTab({ memoryLost: true });
    render(<StatusBar />);
    expect(screen.getByText("In-memory database lost")).toHaveAttribute("title", expect.stringMatching(/next run starts a new, empty one/));
  });

  it("a session error on a database file that needs picking again offers Re-pick file… right there", async () => {
    setTab({ sessionError: "The database file is gone. Pick the file again." });
    const { unmount } = render(<StatusBar />);
    expect(screen.queryByRole("button", { name: "Re-pick file…" })).toBeNull(); // the server does not need a file
    unmount();
    useConnections.setState({ servers: { ms1: { profileId: "ms1", status: "error", repick: true, databases: [] } } } as any);
    render(<StatusBar />);
    await userEvent.click(btn("Re-pick file…"));
    expect(files.repickFile).toHaveBeenCalledWith("ms1");
  });

  it.each([
    ["queued", "Queued"], ["running", "Executing"], ["failed", "Error"], ["cancelled", "Cancelled"], ["done", "Done"],
  ])("execution state %s reads %s", (state, text) => {
    setTab({ activeExecutionId: "e1" });
    view(state, { elapsedMs: 120 });
    render(<StatusBar />);
    expect(screen.getByText(new RegExp(`^${text}`))).toBeInTheDocument();
  });

  it("finished execution shows row counts, sets, client and server timings", () => {
    setTab({ activeExecutionId: "e1" });
    view("done", { elapsedMs: 250, serverElapsedMs: 40 });
    executionData.set("e1", { totalRows: 12345, resultSets: [{}, {}], subscribe: () => () => {}, getVersion: () => 0 } as any);
    render(<StatusBar />);
    expect(screen.getByText(/Done · 12,345 rows \(2 sets\)/)).toBeInTheDocument();
    expect(screen.getByText("250 ms")).toBeInTheDocument();
    expect(screen.getByText("40 ms")).toBeInTheDocument();
  });

  it("without rows it reports affected rows; a running query counts elapsed time live", () => {
    vi.useFakeTimers();
    setTab({ activeExecutionId: "e1" });
    view("done", { affectedRows: 3000, elapsedMs: 5 });
    const { unmount } = render(<StatusBar />);
    expect(screen.getByText(/Done · 3,000 affected/)).toBeInTheDocument();
    unmount();
    view("running", { startedAt: Date.now() });
    render(<StatusBar />);
    expect(screen.getByText("0 ms")).toBeInTheDocument();
    act(() => { vi.advanceTimersByTime(2000); });
    expect(screen.getByText(/^2\d\d\d ms$/)).toBeInTheDocument();
  });
});

describe("Logo", () => {
  it("renders the cropped mark by default and the full tile on request, with unique gradient ids", () => {
    const { container } = render(<><Logo className="a" /><Logo tile className="b" /></>);
    const svgs = container.querySelectorAll("svg");
    expect(svgs[0].getAttribute("viewBox")).toBe("84 72 368 368");
    expect(svgs[1].getAttribute("viewBox")).toBe("0 0 512 512");
    const ids = [...container.querySelectorAll("linearGradient")].map((g) => g.id);
    expect(new Set(ids).size).toBe(ids.length);
  });
});

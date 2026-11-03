// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, act, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "./domPolyfills";

const h = vi.hoisted(() => ({
  actions: {
    activateTab: vi.fn(), changeConnection: vi.fn(), changeDatabase: vi.fn(), newQueryTab: vi.fn(),
    openDefinition: vi.fn(), scriptObject: vi.fn(), closeTab: vi.fn(async () => true),
  },
  cmdRun: vi.fn(),
  closeTabs: vi.fn(async () => {}),
  pick: vi.fn(),
  closeTool: vi.fn(),
  models: {} as Record<string, string>,
}));

vi.mock("../../editor/editors", () => ({
  getModel: (id: string) => (id in h.models ? { getValue: () => h.models[id], getVersionId: () => h.models[id].length } : undefined),
  editorForTab: () => undefined,
}));
vi.mock("../toolActions", () => ({ closeTool: (id: string) => h.closeTool(id) }));
vi.mock("../actions", () => ({ ...h.actions, workspaceHooks: {} }));
vi.mock("../commands", () => ({
  commands: [
    { id: "a", title: "Format document", category: "Edit", keys: ["Ctrl+K"], run: () => h.cmdRun("a") },
    { id: "b", title: "Run query", category: "Query", keys: ["F5"], run: () => { throw new Error("kaboom"); } },
  ],
  keysFor: (c: any) => c.keys,
  displayKeys: (k: string) => k,
}));
vi.mock("../tabActions", async (orig) => {
  const real = await orig<typeof import("../tabActions")>();
  return { ...real, closeTabs: (...a: any[]) => (h.closeTabs as any)(...a), pickFromSwitcher: (i: number) => (h.pick as any)(i), allTabsOrder: () => [...Object.keys(useTabs.getState().tabs), ...Object.keys(useToolTabs.getState().tools)], textOf: (id: string) => (id === "empty1" ? "" : "x") };
});

import { Banner, Palette, Toasts } from "../Palette";
import { TabList, TabSwitcher } from "../TabList";
import { TOAST_MS, TOAST_RESUME_MS, useUi } from "../../store/ui";
import { ANNOUNCE_GAP_MS, resetAnnouncer, useAnnounce } from "../../store/announce";
import { Announcer } from "../Announcer";
import { useTabs } from "../../store/tabs";
import { useToolTabs } from "../../store/toolTabs";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";
import { useSnapshots } from "../../store/snapshots";
import { useSettings } from "../../store/settings";
import { useClosedTabs } from "../../store/closedTabs";
import { useSnippets } from "../../snippets/snippetStore";

const ms: any = { id: "ms1", name: "Sales", engine: "mssql", environment: "prod", auth: "windows", host: "h1", port: 1433 };
const pg: any = { id: "pg1", name: "Analytics", engine: "pg", environment: "dev", auth: "trust", host: "h2" };

function setTab(extra: any = {}) {
  useTabs.setState({
    tabs: { t1: { id: "t1", title: "one.sql", txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.3, actualPlan: false, profileId: "ms1", database: "Sales", ...extra } as any },
    order: ["t1"], activeId: "t1", mru: ["t1"],
  });
}
const snapshot = (objects: any[]) =>
  useSnapshots.setState({ entries: { "ms1|Sales": { key: "ms1|Sales", profileId: "ms1", database: "Sales", status: "ready", snapshot: { objects } as any, version: 1 } as any } });

beforeEach(() => {
  vi.clearAllMocks();
  useUi.setState({ palette: null, dialogs: [], toasts: [], banner: null, switcher: null });
  useProfiles.setState({ profiles: [ms, pg], groups: [] });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useSnapshots.setState({ entries: {} });
  useClosedTabs.setState({ list: [] });
  setTab();
});
afterEach(cleanup);

const open = (p: any) => act(() => useUi.getState().setPalette(p));

describe("Palette", () => {
  it("renders nothing when closed", () => {
    const { container } = render(<Palette />);
    expect(container).toBeEmptyDOMElement();
  });

  it("command mode lists commands with shortcuts, filters by typing and runs on Enter", async () => {
    const user = userEvent.setup();
    render(<Palette />);
    open({ mode: "commands" });
    expect(screen.getByPlaceholderText("Type a command…")).toHaveValue(">");
    expect(screen.getByText("Format document")).toBeInTheDocument();
    expect(screen.getByText("Ctrl+K")).toBeInTheDocument();
    await user.type(screen.getByRole("textbox"), "qu");
    expect(screen.queryByText("Format document")).toBeNull();
    await user.clear(screen.getByRole("textbox"));
    await user.type(screen.getByRole("textbox"), ">fmt");
    expect(screen.getByText("Format document")).toBeInTheDocument(); // subsequence match
    await user.keyboard("{Enter}");
    expect(h.cmdRun).toHaveBeenCalledWith("a");
    expect(useUi.getState().palette).toBeNull();
  });

  it("navigates with arrows and reports a throwing command as an error toast", async () => {
    const user = userEvent.setup();
    render(<Palette />);
    open({ mode: "commands" });
    await user.keyboard("{ArrowDown}{ArrowDown}{ArrowUp}{ArrowDown}{Enter}");
    expect(useUi.getState().toasts[0]).toMatchObject({ kind: "error" });
  });

  it("Escape and clicking the backdrop close it; clicking a row runs it", async () => {
    const user = userEvent.setup();
    const { container } = render(<Palette />);
    open({ mode: "commands" });
    await user.keyboard("{Escape}");
    expect(useUi.getState().palette).toBeNull();
    open({ mode: "commands" });
    fireEvent.mouseDown(container.firstChild as Element);
    expect(useUi.getState().palette).toBeNull();
    open({ mode: "commands" });
    fireEvent.mouseEnter(screen.getByText("Run query"));
    fireEvent.mouseDown(screen.getByText("Format document"));
    expect(h.cmdRun).toHaveBeenCalled();
  });

  it("shows 'No matches.' for an unmatched query", async () => {
    const user = userEvent.setup();
    render(<Palette />);
    open({ mode: "commands" });
    await user.type(screen.getByRole("textbox"), "zzzz");
    expect(screen.getByText("No matches.")).toBeInTheDocument();
  });

  describe("objects", () => {
    it("explains why the list is empty (no connection / loading / disconnected)", () => {
      render(<Palette />);
      useTabs.setState({ tabs: { t1: { ...useTabs.getState().tabs.t1, profileId: undefined } } });
      open({ mode: "objects" });
      expect(screen.getByText(/Connect the tab to a database/)).toBeInTheDocument();
      act(() => useTabs.setState({ tabs: { t1: { ...useTabs.getState().tabs.t1, profileId: "ms1" } } }));
      open({ mode: "objects", initial: "" });
      expect(screen.getByText("Metadata is loading…")).toBeInTheDocument();
      act(() => useConnections.setState({ disconnected: { ms1: true } }));
      open({ mode: "objects", initial: " " });
      expect(screen.getByText(/Disconnected: connect the server/)).toBeInTheDocument();
    });

    it.each([
      ["table", "scriptObject", "Enter: select top 1000"],
      ["view", "scriptObject", "Enter: select top 1000"],
      ["proc", "openDefinition", "Enter: modify"],
    ] as const)("a %s runs %s", async (kind, fn, hint) => {
      snapshot([{ id: 5, schema: "dbo", name: "Thing", kind }]);
      const user = userEvent.setup();
      render(<Palette />);
      open({ mode: "objects" });
      expect(screen.getByText("dbo.Thing")).toBeInTheDocument();
      expect(screen.getByText(hint)).toBeInTheDocument();
      await user.type(screen.getByRole("textbox"), "thing{Enter}");
      const call = (h.actions as any)[fn];
      expect(call).toHaveBeenCalled();
      const args = call.mock.calls[0];
      expect(args.slice(0, 2)).toEqual(["ms1", "Sales"]);
      expect(args[2]).toMatchObject({ schema: "dbo", name: "Thing", kind, objectId: 5 });
      if (fn === "scriptObject") expect(args.slice(3)).toEqual(["selectTop", true]);
    });

    it("does nothing for an object when the tab has no database; open tabs are listed and activated", async () => {
      snapshot([{ id: 5, schema: "dbo", name: "Thing", kind: "table" }]);
      const user = userEvent.setup();
      render(<Palette />);
      open({ mode: "objects" });
      useTabs.setState({ tabs: { t1: { ...useTabs.getState().tabs.t1, database: undefined } } });
      await user.click(screen.getByText("one.sql"));
      expect(h.actions.activateTab).toHaveBeenCalledWith("t1");
    });
  });

  describe("snippets", () => {
    const snip = (id: string, prefix: string, dialect: any, over: any = {}) => ({ id, title: `T ${prefix}`, prefix, body: "SELECT 1;", dialect, tags: [], updatedAt: 0, builtIn: false, ...over });
    it("lists the active tab dialect's snippets and inserts the chosen one", async () => {
      useSnippets.setState({ loaded: true, snippets: [snip("1", "selt", "tsql", { builtIn: true }), snip("2", "sell", "pg"), snip("3", "any", null, { tags: ["x", "y"] })] as any });
      const user = userEvent.setup();
      render(<Palette />);
      open({ mode: "snippets" });
      expect(screen.getByPlaceholderText("Insert snippet…")).toBeInTheDocument();
      expect(screen.getByText("selt — T selt")).toBeInTheDocument();
      expect(screen.getByText("built-in")).toBeInTheDocument();
      expect(screen.getByText("x, y")).toBeInTheDocument();
      expect(screen.queryByText("sell — T sell")).toBeNull(); // the tab is SQL Server
      await user.click(screen.getByText("any — T any"));
      // No editor in this test: the insert path reports it instead of failing silently.
      await waitFor(() => expect(useUi.getState().toasts.at(-1)?.text).toContain("Open a query tab"));
    });
    it("says when the library is empty, still loading, or failed", () => {
      useSnippets.setState({ loaded: true, snippets: [], error: undefined });
      render(<Palette />);
      open({ mode: "snippets" });
      expect(screen.getByText(/No snippets yet/)).toBeInTheDocument();
      act(() => useSnippets.setState({ loaded: false, error: "sidecar down" }));
      expect(screen.getByText("sidecar down")).toBeInTheDocument();
    });
  });

  describe("databases", () => {
    it("connects a server never connected, shows Connecting…, then lists databases and switches", async () => {
      const connect = vi.fn(async () => true);
      useConnections.setState({ connect } as any);
      const user = userEvent.setup();
      render(<Palette />);
      open({ mode: "databases" });
      expect(connect).toHaveBeenCalledWith("ms1");
      expect(screen.getByText("Connecting…")).toBeInTheDocument();
      act(() => useConnections.setState({ servers: { ms1: { profileId: "ms1", status: "connected", databases: [{ name: "Sales" }, { name: "master", isSystem: true }, { name: "Old", state: "offline" }] } as any } }));
      expect(screen.getByText("current")).toBeInTheDocument();
      expect(screen.getByText("system")).toBeInTheDocument();
      expect(screen.getByText("offline")).toBeInTheDocument();
      await user.click(screen.getByText("master"));
      expect(h.actions.changeDatabase).toHaveBeenCalledWith("t1", "master");
    });
    it("shows a connection error", () => {
      useConnections.setState({ servers: { ms1: { profileId: "ms1", status: "error", error: "login failed", databases: [] } as any } });
      render(<Palette />);
      open({ mode: "databases" });
      expect(screen.getByText("login failed")).toBeInTheDocument();
    });
    it("for a disconnected server offers to connect and reopens the list on success; never auto-reconnects", async () => {
      const connect = vi.fn(async () => true);
      useConnections.setState({ connect, disconnected: { ms1: true } } as any);
      const user = userEvent.setup();
      render(<Palette />);
      open({ mode: "databases" });
      expect(connect).not.toHaveBeenCalled();
      await user.click(screen.getByText(/Disconnected. Connect to list databases/));
      expect(connect).toHaveBeenCalledWith("ms1");
      await act(async () => {});
      expect(useUi.getState().palette).toEqual({ mode: "databases" });
    });
  });

  describe("connections", () => {
    it("lists both engines, switches the active tab, and offers a new profile", async () => {
      useConnections.setState({ servers: { pg1: { profileId: "pg1", status: "connected", databases: [] } as any } });
      const user = userEvent.setup();
      render(<Palette />);
      open({ mode: "connections" });
      expect(screen.getByText("[MS] Prod - Sales")).toBeInTheDocument();
      expect(screen.getByText("[PG] Dev - Analytics")).toBeInTheDocument();
      expect(screen.getByText("connected")).toBeInTheDocument();
      expect(screen.getByText("h1:1433")).toBeInTheDocument();
      await user.click(screen.getByText("[PG] Dev - Analytics"));
      expect(h.actions.changeConnection).toHaveBeenCalledWith("t1", "pg1");
      open({ mode: "connections" });
      await user.click(screen.getByText("+ New connection profile…"));
      expect(useUi.getState().dialogs[0]).toMatchObject({ kind: "profile" });
    });
    it("without an active tab opens a new query tab on the profile", async () => {
      useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
      const user = userEvent.setup();
      render(<Palette />);
      open({ mode: "connections" });
      await user.click(screen.getByText("[MS] Prod - Sales"));
      expect(h.actions.newQueryTab).toHaveBeenCalledWith({ profileId: "ms1" });
    });
  });

  it("tabs mode renders the open-tabs list and closes through it", async () => {
    render(<Palette />);
    open({ mode: "tabs" });
    expect(screen.getByLabelText("Search open tabs")).toBeInTheDocument();
    await userEvent.setup().keyboard("{Escape}");
    expect(useUi.getState().palette).toBeNull();
  });
});

describe("Toasts and Banner", () => {
  const urgent = () => document.querySelector("[data-q-announcer-urgent]")!.textContent;
  const polite = () => document.querySelector("[data-q-announcer]")!.textContent;
  beforeEach(() => resetAnnouncer());

  it("renders each toast kind, says errors and warnings by more than color, and dismisses with its button or a click", async () => {
    render(<Toasts />);
    act(() => {
      for (const k of ["info", "error", "success", "warn"] as const) useUi.getState().toast(k, `msg-${k}`);
    });
    const region = screen.getByRole("region", { name: "Notifications" });
    const box = (text: string) => screen.getByText(text).closest("[data-q-toast]") as HTMLElement;
    expect(box("msg-error").className).toMatch(/text-err/);
    expect(box("msg-warn").className).toMatch(/text-warn/);
    expect(box("msg-success").className).toMatch(/text-ok/);
    expect(box("msg-error").textContent).toBe("Error: msg-error");
    expect(box("msg-warn").textContent).toBe("Warning: msg-warn");
    expect(within(region).getAllByRole("button", { name: "Dismiss" })).toHaveLength(4);
    const user = userEvent.setup();
    await user.click(within(box("msg-error")).getByRole("button", { name: "Dismiss" }));
    expect(screen.queryByText("msg-error")).toBeNull();
    await user.click(screen.getByText("msg-info"));
    expect(screen.queryByText("msg-info")).toBeNull();
    // From the keyboard: Enter on Dismiss closes it and focus moves to the next toast's button.
    within(box("msg-success")).getByRole("button", { name: "Dismiss" }).focus();
    await user.keyboard("{Enter}");
    expect(screen.queryByText("msg-success")).toBeNull();
    await waitFor(() => expect(document.activeElement).toBe(within(box("msg-warn")).getByRole("button", { name: "Dismiss" })));
  });

  it("reaches screen readers through the Announcer: errors and warnings assertive, the rest polite", () => {
    vi.useFakeTimers();
    try {
      render(<><Toasts /><Announcer /></>);
      act(() => useUi.getState().toast("error", "Could not save the file"));
      expect(urgent()).toBe("Error: Could not save the file.");
      act(() => vi.advanceTimersByTime(ANNOUNCE_GAP_MS));
      act(() => useUi.getState().toast("success", "Saved"));
      expect(polite()).toBe("Saved.");
      expect(urgent()).toBe("");
    } finally {
      vi.useRealTimers();
    }
  });

  it("joins an identical toast into the shown one (×2, its time starts over) and reads it once", () => {
    vi.useFakeTimers();
    try {
      render(<><Toasts /><Announcer /></>);
      act(() => useUi.getState().toast("warn", "Settings could not be saved"));
      act(() => vi.advanceTimersByTime(TOAST_MS.warn - 1000));
      for (let i = 0; i < 9; i++) act(() => useUi.getState().toast("warn", "Settings could not be saved"));
      expect(useUi.getState().toasts).toHaveLength(1);
      expect(screen.getByText("×10")).toBeTruthy();
      expect(useAnnounce.getState().n).toBe(1);
      act(() => vi.advanceTimersByTime(TOAST_MS.warn - 1));
      expect(useUi.getState().toasts).toHaveLength(1);
      act(() => vi.advanceTimersByTime(1));
      expect(useUi.getState().toasts).toHaveLength(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it("stops the time while the pointer is over the toasts or focus is inside them, and leaves at least 2 s after", () => {
    vi.useFakeTimers();
    try {
      render(<Toasts />);
      act(() => useUi.getState().toast("info", "Copied"));
      const region = screen.getByRole("region", { name: "Notifications" });
      act(() => vi.advanceTimersByTime(TOAST_MS.info - 500));
      fireEvent.pointerEnter(region);
      act(() => vi.advanceTimersByTime(60_000));
      expect(screen.getByText("Copied")).toBeTruthy();
      // Focus inside holds it too, also after the pointer left.
      act(() => screen.getByRole("button", { name: "Dismiss" }).focus());
      fireEvent.pointerLeave(region);
      act(() => vi.advanceTimersByTime(60_000));
      expect(screen.getByText("Copied")).toBeTruthy();
      act(() => (document.activeElement as HTMLElement).blur());
      act(() => vi.advanceTimersByTime(TOAST_RESUME_MS - 1));
      expect(screen.getByText("Copied")).toBeTruthy();
      act(() => vi.advanceTimersByTime(1));
      expect(screen.queryByText("Copied")).toBeNull();
      // The last toast gone under the pointer: the next one still times out.
      act(() => useUi.getState().toast("info", "Second"));
      fireEvent.pointerEnter(region);
      act(() => useUi.getState().dismissToast(useUi.getState().toasts[0].id));
      act(() => useUi.getState().toast("info", "Third"));
      act(() => vi.advanceTimersByTime(TOAST_MS.info));
      expect(screen.queryByText("Third")).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it("Banner is hidden without one, is read at once when it appears, runs its action, and dismisses", async () => {
    const { container } = render(<Banner />);
    expect(container).toBeEmptyDOMElement();
    render(<Announcer />);
    const run = vi.fn();
    act(() => useUi.getState().setBanner({ kind: "error", text: "engine down", action: { label: "Restart engine", run } }));
    expect(screen.getByRole("region", { name: "Error notice" }).textContent).toContain("engine down");
    expect(urgent()).toBe("engine down.");
    const user = userEvent.setup();
    await user.click(screen.getByText("Restart engine"));
    expect(run).toHaveBeenCalled();
    await user.click(screen.getByText("Dismiss"));
    expect(screen.queryByText("engine down")).toBeNull();
    act(() => useUi.getState().setBanner({ kind: "warn", text: "careful" }));
    expect(screen.queryByText("Restart engine")).toBeNull();
    expect(screen.getByRole("region", { name: "Notice" })).toBeTruthy();
  });
});

describe("TabList", () => {
  function many() {
    const base = { txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.3, actualPlan: false };
    useTabs.setState({
      tabs: {
        t1: { ...base, id: "t1", title: "one.sql", profileId: "ms1", database: "Sales", pinned: true, busy: true, txState: "open", dirty: true, filePath: "/w/one.sql" },
        t2: { ...base, id: "t2", title: "two.sql", profileId: "pg1", database: "app" },
        empty1: { ...base, id: "empty1", title: "blank.sql" },
      } as any,
      order: ["t1", "t2", "empty1"], activeId: "t1", mru: ["t2", "t1", "empty1"],
    });
  }
  beforeEach(() => { many(); useSettings.setState({ tabListGroupByConnection: false } as any); });

  const subtitles = () => [...document.querySelectorAll("[data-q-tab-subtitle]")].map((e) => e.textContent);

  it("each row is the tab's identity: markers, then the database in its colour and the server; no environment word (D18, owner revision 2)", async () => {
    const user = userEvent.setup();
    render(<TabList onClose={() => {}} />);
    expect(subtitles()).toEqual(["app· Analytics", "Sales· Sales", "No connection"]);
    expect(subtitles().join()).not.toMatch(/PROD|DEV/);
    expect(document.querySelector(".q-tx-pill")!.textContent).toBe("TX");
    expect(document.querySelector(".q-spin")).not.toBeNull();
    expect(screen.getByText("current")).toBeInTheDocument();
    expect(screen.getByText("previous")).toBeInTheDocument();
    // The environment and the states are in the option's name.
    expect(screen.getByRole("option", { name: /^one, modified, Production, Sales, database Sales, query running, open transaction, pinned, current$/ })).toBeInTheDocument();
    expect(document.querySelector("[data-q-tab-subtitle]")!.closest("[role=dialog]")).not.toBeNull();
    await user.type(screen.getByLabelText("Search open tabs"), "two");
    expect(screen.queryByText("one")).toBeNull();
    await user.clear(screen.getByLabelText("Search open tabs"));
    await user.type(screen.getByLabelText("Search open tabs"), "nomatch");
    expect(screen.getByText("No open tab matches.")).toBeInTheDocument();
  });

  it("a tab on a disconnected server says so first, as on the strip", () => {
    useConnections.setState({ disconnected: { pg1: true } });
    render(<TabList onClose={() => {}} />);
    expect(subtitles()[0]).toBe("disconnectedapp· Analytics");
    expect(screen.getByRole("option", { name: /Analytics, server disconnected/ })).toBeInTheDocument();
  });

  it("is a modal dialog: a combobox over a listbox, 88 px from the top; with nothing typed the previous tab is picked (D18)", () => {
    render(<TabList onClose={() => {}} />);
    const dialog = screen.getByRole("dialog", { name: "Go to tab" });
    expect(dialog).toHaveAttribute("aria-modal", "true");
    expect(dialog.className).toContain("top-[88px]");
    const box = screen.getByRole("combobox", { name: "Search open tabs" });
    expect(box).toHaveAttribute("aria-controls", "q-tablist");
    const selected = screen.getAllByRole("option").find((o) => o.getAttribute("aria-selected") === "true")!;
    expect(box.getAttribute("aria-activedescendant")).toBe(selected.id);
    expect(selected.getAttribute("aria-label")).toMatch(/^two,.*previous$/);
  });

  it("hover picks a row only after the pointer really moves", () => {
    render(<TabList onClose={() => {}} />);
    const [, second] = screen.getAllByRole("option");
    fireEvent.mouseMove(second, { clientX: 10, clientY: 10 }); // the list opened under a resting pointer
    expect(second).toHaveAttribute("aria-selected", "false");
    fireEvent.mouseMove(second, { clientX: 12, clientY: 10 });
    expect(second).toHaveAttribute("aria-selected", "true");
  });

  it("finds tabs by their SQL text and by SPID, and says how many match", async () => {
    h.models.t1 = "UPDATE dbo.Invoice SET Total = 1";
    useTabs.getState().update("t2", { spid: 77 });
    const user = userEvent.setup();
    render(<TabList onClose={() => {}} />);
    await user.type(screen.getByLabelText("Search open tabs"), "invoice");
    expect(screen.getAllByRole("option").map((o) => o.getAttribute("aria-label")!.split(",")[0])).toEqual(["one"]);
    expect(screen.getByText("1 tab match")).toBeInTheDocument();
    await user.clear(screen.getByLabelText("Search open tabs"));
    await user.type(screen.getByLabelText("Search open tabs"), "77");
    expect(screen.getAllByRole("option").map((o) => o.getAttribute("aria-label")!.split(",")[0])).toEqual(["two"]);
    delete h.models.t1;
  });

  it("shows the first statement of a tab's SQL as a third line", () => {
    h.models.t2 = "-- q3\nSELECT region,\n  SUM(amount) FROM sales GROUP BY region;";
    render(<TabList onClose={() => {}} />);
    expect(screen.getByText("SELECT region, SUM(amount) FROM sales GROUP BY region")).toBeInTheDocument();
    delete h.models.t2;
  });

  it("lists tool tabs with their kind and closes them as tools (D17)", () => {
    useToolTabs.setState({ tools: { erd1: { id: "erd1", kind: "erd", title: "ERD · Orders", profileId: "ms1", database: "Sales", params: {} } } });
    useTabs.setState({ mru: ["t2", "erd1", "t1", "empty1"] });
    render(<TabList onClose={() => {}} />);
    expect(screen.getByRole("option", { name: /^Orders, ER diagram, Production, Sales, database Sales/ })).toBeInTheDocument();
    expect(screen.getByText("ERD · Orders")).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByLabelText("Close Orders"));
    expect(h.closeTool).toHaveBeenCalledWith("erd1");
    useToolTabs.setState({ tools: {} });
  });

  it("opens the highlighted tab with Enter and arrows; Escape closes", async () => {
    const onClose = vi.fn();
    const user = userEvent.setup();
    render(<TabList onClose={onClose} />);
    await user.keyboard("{ArrowDown}{ArrowDown}{ArrowUp}");
    await user.type(screen.getByLabelText("Search open tabs"), "{ArrowDown}{Enter}");
    expect(h.actions.activateTab).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
    await user.type(screen.getByLabelText("Search open tabs"), "{Escape}");
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("closing from the row closes the tab without opening it; shows empty-tab cleanup", async () => {
    const user = userEvent.setup();
    render(<TabList onClose={() => {}} />);
    fireEvent.mouseDown(screen.getByLabelText("Close two"));
    expect(h.actions.closeTab).toHaveBeenCalledWith("t2");
    expect(h.actions.activateTab).not.toHaveBeenCalled();
    await user.click(screen.getByText("Close 1 empty tab"));
    expect(h.closeTabs).toHaveBeenCalledWith("empty", "t1");
  });

  it("group by connection toggles through the settings and shows section headers", async () => {
    const set = vi.fn((p: any) => useSettings.setState(p));
    useSettings.setState({ set } as any);
    const user = userEvent.setup();
    render(<TabList onClose={() => {}} />);
    await user.click(screen.getByLabelText("Group by connection"));
    expect(set).toHaveBeenCalledWith({ tabListGroupByConnection: true });
    // Each section names its connection like a dialog does: environment, server.
    const chips = screen.getAllByTestId("server-chip").map((c) => c.textContent);
    expect(chips).toEqual(["DEVAnalytics", "PRODSales"]);
    expect(screen.getAllByRole("group").map((g) => g.getAttribute("aria-label"))).toEqual(["Analytics", "Sales", "No connection"]);
  });

  it("with `only` lists just those tabs (the strip's hidden ones)", () => {
    render(<TabList onClose={() => {}} only={["t2"]} />);
    expect(screen.getByText("two")).toBeInTheDocument();
    expect(screen.queryByText("one")).toBeNull();
    expect(screen.queryByText("blank")).toBeNull();
    expect(screen.getByRole("dialog", { name: "Hidden tabs" })).toBeInTheDocument();
    expect(screen.getByLabelText("Search open tabs")).toHaveAttribute("placeholder", expect.stringContaining("1 hidden tabs"));
    cleanup();
    render(<TabList onClose={() => {}} only={["gone"]} />);
    expect(screen.getByText("No hidden tabs.")).toBeInTheDocument();
  });

  describe("recently closed", () => {
    beforeEach(() => {
      useClosedTabs.setState({ list: [] });
      useClosedTabs.getState().push({ closedAt: Date.now() - 5 * 60_000, title: "report.sql", profileId: "pg1", database: "dw", sql: 'select * from "t"' });
      useClosedTabs.getState().push({ closedAt: Date.now(), title: "fix.sql", profileId: "ms1", database: "Sales", sql: "select 1", filePath: "/w/fix.sql" });
    });

    it("lists closed tabs under the open ones with connection and age; search filters them too", async () => {
      const user = userEvent.setup();
      render(<TabList onClose={() => {}} />);
      expect(screen.getByText("Recently closed")).toBeInTheDocument();
      expect(screen.getByText("fix")).toBeInTheDocument();
      expect(subtitles()).toContain("dw· Analytics");
      expect(screen.getByText("5 min ago")).toBeInTheDocument();
      await user.type(screen.getByLabelText("Search open tabs"), "analytics");
      expect(screen.getByText("report")).toBeInTheDocument();
      expect(screen.queryByText("fix")).toBeNull();
    });

    it("arrows move past the open tabs into the closed ones; Enter reopens", async () => {
      const onClose = vi.fn();
      const user = userEvent.setup();
      render(<TabList onClose={onClose} />);
      // 3 open tabs, then fix.sql, report.sql: the 5th row is report.sql (store index 1).
      await user.type(screen.getByLabelText("Search open tabs"), "{ArrowDown}{ArrowDown}{ArrowDown}{ArrowDown}{ArrowDown}{Enter}");
      expect(onClose).toHaveBeenCalled();
      expect(h.actions.newQueryTab).toHaveBeenCalledWith(expect.objectContaining({ title: "report.sql", profileId: "pg1", database: "dw" }));
      expect(h.actions.activateTab).not.toHaveBeenCalled();
      expect(useClosedTabs.getState().list.map((s) => s.title)).toEqual(["fix.sql"]);
    });

    it("clicking a closed row reopens it; × forgets it; Clear forgets all", async () => {
      const user = userEvent.setup();
      render(<TabList onClose={() => {}} />);
      fireEvent.mouseDown(screen.getByLabelText("Forget report.sql"));
      expect(h.actions.newQueryTab).not.toHaveBeenCalled();
      expect(useClosedTabs.getState().list.map((s) => s.title)).toEqual(["fix.sql"]);
      fireEvent.mouseDown(screen.getByText("fix"));
      expect(h.actions.newQueryTab).toHaveBeenCalledWith(expect.objectContaining({ title: "fix.sql", filePath: "/w/fix.sql" }));
      cleanup();
      useClosedTabs.getState().push({ closedAt: Date.now(), title: "a.sql", sql: "a" });
      render(<TabList onClose={() => {}} />);
      await user.click(screen.getByText("Clear recently closed"));
      expect(useClosedTabs.getState().list).toEqual([]);
      expect(screen.queryByText("Recently closed")).toBeNull();
    });

    it("the hidden-tabs list does not show closed tabs", () => {
      render(<TabList onClose={() => {}} only={["t2"]} />);
      expect(screen.queryByText("Recently closed")).toBeNull();
      expect(screen.queryByText("Clear recently closed")).toBeNull();
    });
  });

  it("the palette passes the hidden-tab ids to the list", () => {
    act(() => useUi.getState().setPalette({ mode: "tabs", only: ["t1"] }));
    render(<Palette />);
    expect(screen.getByText("one")).toBeInTheDocument();
    expect(screen.queryByText("two")).toBeNull();
  });

  it("with no tabs says so", () => {
    useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
    render(<TabList onClose={() => {}} />);
    expect(screen.getByText("No open tabs.")).toBeInTheDocument();
  });
});

describe("TabSwitcher", () => {
  it("is hidden until Ctrl+Tab opens it, highlights the index, and picks on press", () => {
    useTabs.setState({
      tabs: { t1: { id: "t1", title: "one.sql", profileId: "ms1", database: "Sales", txState: "none", busy: false, dirty: false } as any, t2: { id: "t2", title: "two.sql" } as any },
      order: ["t1", "t2"],
    });
    const { container } = render(<TabSwitcher />);
    expect(container).toBeEmptyDOMElement();
    act(() => useUi.getState().setSwitcher({ ids: ["t1", "gone", "t2"], index: 2 }));
    const opts = screen.getAllByRole("option");
    expect(opts).toHaveLength(2);
    expect(opts[1]).toHaveAttribute("aria-selected", "true");
    fireEvent.mouseDown(opts[0]);
    expect(h.pick).toHaveBeenCalledWith(0);
  });
  it("scrolls the window of twelve rows with the highlight", () => {
    const ids = Array.from({ length: 20 }, (_, i) => `x${i}`);
    useTabs.setState({ tabs: Object.fromEntries(ids.map((id) => [id, { id, title: id, txState: "none" } as any])), order: ids });
    render(<TabSwitcher />);
    act(() => useUi.getState().setSwitcher({ ids, index: 19 }));
    expect(screen.getAllByRole("option")).toHaveLength(12);
    expect(screen.getByText("x19")).toBeInTheDocument();
    expect(screen.queryByText("x0")).toBeNull();
  });
  it("says where it is (\"20 of 20\") and reads the highlighted tab through a live region (D18)", () => {
    const ids = Array.from({ length: 20 }, (_, i) => `x${i}`);
    useTabs.setState({ tabs: Object.fromEntries(ids.map((id) => [id, { id, title: `${id}.sql`, txState: "none" } as any])), order: ids });
    render(<TabSwitcher />);
    act(() => useUi.getState().setSwitcher({ ids, index: 2 }));
    expect(screen.getByText(/Recent tabs · 3 of 20/)).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("x2, no connection, 3 of 20");
  });
});

// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, Profile } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  activateTab: vi.fn(),
  changeConnection: vi.fn(async (..._a: unknown[]) => {}),
  newQueryTab: vi.fn(),
  disconnectServer: vi.fn(async (..._a: unknown[]) => {}),
  tabForNewConnection: vi.fn((): string | undefined => undefined),
  confirmDeleteProfile: vi.fn(async (..._a: unknown[]) => true),
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../app/actions", () => ({ activateTab: m.activateTab, changeConnection: m.changeConnection, newQueryTab: m.newQueryTab, disconnectServer: m.disconnectServer }));
vi.mock("../../app/tabActions", () => ({ tabForNewConnection: m.tabForNewConnection }));
vi.mock("../deleteProfile", () => ({ confirmDeleteProfile: m.confirmDeleteProfile }));

import { ConnectDialog } from "../ConnectDialog";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";

const prof = (id: string, engine: Engine, over: Partial<Profile> = {}) =>
  ({ id, name: id, engine, environment: "dev", host: "h-" + id, port: null, ...over }) as Profile;

const row = (id: string) => document.querySelector(`[data-profile="${id}"]`) as HTMLElement;
const activeRow = () => document.querySelector("[data-active]")?.getAttribute("data-profile") ?? null;
const search = () => screen.getByLabelText("Search profiles") as HTMLInputElement;
const footer = () => document.querySelector(".q-profile-reveal") as HTMLElement;
const connected = (...ids: string[]) => Object.fromEntries(ids.map((id) => [id, { profileId: id, status: "connected", databases: [] }]));

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  (globalThis.CSS as unknown) = { ...(globalThis.CSS ?? {}), escape: (s: string) => s };
  Object.values(m).forEach((f) => f.mockClear());
  m.tabForNewConnection.mockReturnValue(undefined);
  useProfiles.setState({
    profiles: [prof("ms1", "mssql", { name: "Sales", port: 1433 }), prof("pg1", "pg", { name: "Billing", environment: "prod" })],
    groups: [],
    loaded: true,
  });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useSettings.setState({ groupOrder: [], profileOrder: [], recentProfiles: [], prodHintMuted: [], showGroups: true });
  useUi.setState({ dialogs: [] });
  useTabs.setState({ activeId: undefined } as never);
});
afterEach(cleanup);

describe("ConnectDialog: layout", () => {
  it("is titled 'Connect to a database' with a × and no subtitle, count or key hints", async () => {
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    expect(screen.getByText("Connect to a database")).toBeTruthy();
    expect(screen.queryByText(/appear in the Object Explorer/)).toBeNull();
    expect(document.body.textContent).not.toMatch(/↑↓|Enter connect|2 profiles/);
    expect(screen.queryByTestId("profile-count")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onClose).toHaveBeenCalled();
  });

  it("first run: two paths, no search; each opens the editor, which comes back to this dialog", async () => {
    useProfiles.setState({ profiles: [] });
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    expect(screen.getByText("No saved profiles yet")).toBeTruthy();
    expect(screen.queryByLabelText("Search profiles")).toBeNull();
    expect(screen.queryByText("+ New profile")).toBeNull();
    // Each path names its engines from the engine table.
    expect(screen.getByText("SQL Server · PostgreSQL · MySQL / MariaDB · Oracle")).toBeTruthy();
    expect(screen.getByText("SQLite · DuckDB")).toBeTruthy();
    await userEvent.click(screen.getByText("Open a database file…"));
    expect(onClose).toHaveBeenCalled();
    expect(useUi.getState().dialogs).toEqual([{ kind: "profile", preset: "sqlite", fromConnect: true }]);
    await userEvent.click(screen.getByText("Connect to a server…"));
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "profile", fromConnect: true });
  });

  it("profiles that could not load show Retry, never the first-run state", async () => {
    const load = vi.fn(async () => {});
    useProfiles.setState({ profiles: [], loaded: false, load } as never);
    render(<ConnectDialog onClose={() => {}} />);
    expect(screen.getByRole("alert").textContent).toContain("Couldn’t load saved profiles");
    expect(screen.queryByText("No saved profiles yet")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(load).toHaveBeenCalled();
  });

  it("each row names environment, profile, engine and address; only Production has the stripe and a filled tag", () => {
    render(<ConnectDialog onClose={() => {}} />);
    expect(screen.getByRole("option", { name: "Development, Sales, Microsoft SQL Server, h-ms1:1433" })).toBeTruthy();
    expect(screen.getByRole("option", { name: "Production, Billing, PostgreSQL, h-pg1" })).toBeTruthy();
    expect(row("pg1").querySelector("[data-prod]")).toBeTruthy();
    expect(row("ms1").querySelector("[data-prod]")).toBeNull();
    expect(row("pg1").querySelector('[data-env="prod"]')!.className).toContain("bg-env-prod-fill");
    expect(row("ms1").querySelector('[data-env="dev"]')!.className).toContain("text-env-dev-fg");
    expect(row("ms1").querySelector('[data-env="dev"]')!.textContent).toBe("DEV");
  });

  it.each<[Engine, Partial<Profile>, string]>([
    ["mssql", { host: "sql01", port: 1433 }, "sql01:1433"],
    ["pg", { host: "2001:db8::14", port: 5432 }, "[2001:db8::14]:5432"],
    ["mySql", { host: "my01", port: 3306 }, "my01:3306"],
    ["oracle", { host: "ora01", port: 1521 }, "ora01:1521"],
    ["sqlite", { location: "file", host: null, file: { grantId: "g", displayName: "scratch.sqlite" } }, "scratch.sqlite"],
    ["duckDb", { location: "memory", host: null }, "In memory"],
  ])("a %s profile shows its address", (engine, over, shown) => {
    useProfiles.setState({ profiles: [prof("x", engine, over)] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(row("x").textContent).toContain(shown);
  });
});

describe("ConnectDialog: search and filters", () => {
  it("search gives one ranked list with a count, and a clearable no-match state", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.type(search(), "billing");
    expect(screen.queryByText("Sales")).toBeNull();
    expect(screen.getByRole("group", { name: "1 match" })).toBeTruthy();
    expect(screen.getByTestId("profile-count").textContent).toBe("1 of 2");
    // The top match is the Enter target; the matched letters are marked.
    expect(activeRow()).toBe("pg1");
    expect(row("pg1").querySelector("mark")?.textContent).toBe("Billing");
    await userEvent.clear(search());
    await userEvent.type(search(), "zzz");
    expect(screen.getByText(/No profiles match “zzz”/)).toBeTruthy();
    expect(footer().textContent).not.toContain("↵");
    await userEvent.click(screen.getByText("Clear search"));
    expect(screen.getByText("Sales")).toBeTruthy();
  });

  it("environment chips are multi-select toggles; empty environments have no chip", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    const prod = screen.getByRole("button", { name: /^Production/ });
    const dev = screen.getByRole("button", { name: /^Development/ });
    expect(screen.queryByRole("button", { name: /^Staging/ })).toBeNull();
    await userEvent.click(prod);
    expect(prod.getAttribute("aria-pressed")).toBe("true");
    expect(screen.queryByText("Sales")).toBeNull();
    await userEvent.click(dev);
    expect(screen.getByText("Sales")).toBeTruthy();
    expect(screen.getByText("Billing")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: /^All environments/ }));
    expect(prod.getAttribute("aria-pressed")).toBe("false");
  });

  it("has no chips when every profile is in one environment", () => {
    useProfiles.setState({ profiles: [prof("a", "pg"), prof("b", "mssql")] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(screen.queryByRole("group", { name: "Filter by environment" })).toBeNull();
  });

  it("mentions the environment in the no-match message", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: /^Production/ }));
    await userEvent.type(search(), "sales");
    expect(screen.getByText(/in Prod/)).toBeTruthy();
  });

  it("Esc clears the search first (the earlier row comes back), then closes", async () => {
    useSettings.setState({ recentProfiles: ["ms1"] });
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    expect(activeRow()).toBe("ms1");
    await userEvent.type(search(), "bill");
    expect(activeRow()).toBe("pg1");
    fireEvent.keyDown(search(), { key: "Escape" });
    expect(search().value).toBe("");
    expect(activeRow()).toBe("ms1");
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.keyDown(search(), { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });
});

describe("ConnectDialog: the Enter target", () => {
  it("opens on the most recently connected profile and names it in the footer", () => {
    useSettings.setState({ recentProfiles: ["gone", "pg1"] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(activeRow()).toBe("pg1");
    expect(footer().textContent).toContain("Connect");
    expect(footer().textContent).toContain("PROD");
    expect(footer().textContent).toContain("Billing");
  });

  it("without a recent profile it opens on no row (never the Production one first): Enter does nothing", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    render(<ConnectDialog onClose={() => {}} />);
    expect(activeRow()).toBeNull();
    expect(footer().textContent).toContain("Type, or press ↓ to choose a profile");
    fireEvent.keyDown(search(), { key: "Enter" });
    expect(connect).not.toHaveBeenCalled();
  });

  it("`focus` (just saved) wins as the initial active row", () => {
    render(<ConnectDialog onClose={() => {}} focus="pg1" />);
    expect(activeRow()).toBe("pg1");
  });

  it("a click makes a row the target; hovering another never moves it", () => {
    render(<ConnectDialog onClose={() => {}} />);
    fireEvent.click(row("pg1"));
    expect(activeRow()).toBe("pg1");
    fireEvent.mouseEnter(row("ms1"));
    fireEvent.mouseMove(row("ms1"));
    expect(activeRow()).toBe("pg1");
    expect(search().getAttribute("aria-activedescendant")).toBe(row("pg1").id);
  });

  it("arrow keys move the target from the search box and Enter connects it", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    render(<ConnectDialog onClose={() => {}} />);
    const order = Array.from(document.querySelectorAll("[data-profile]")).map((e) => e.getAttribute("data-profile"));
    search().focus();
    await userEvent.keyboard("{ArrowDown}");
    expect(activeRow()).toBe(order[0]);
    await userEvent.keyboard("{ArrowDown}{ArrowDown}");
    expect(activeRow()).toBe(order[1]);
    expect(document.querySelector("[data-kbd]")).toBeTruthy();
    await userEvent.keyboard("{ArrowUp}{Enter}");
    expect(connect).toHaveBeenCalledWith(order[0]);
  });
});

describe("ConnectDialog: row actions", () => {
  it("Connect closes the dialog and opens a query tab when none is active", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    await userEvent.click(within(row("ms1")).getByText("Connect", { selector: "button" }));
    await waitFor(() => expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "ms1" }));
    expect(onClose).toHaveBeenCalled();
  });

  it("Connect reuses an unconnected tab instead of opening a new one", async () => {
    useConnections.setState({ connect: vi.fn(async () => true) } as never);
    m.tabForNewConnection.mockReturnValue("tab-7");
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(within(row("pg1")).getByText("Connect", { selector: "button" }));
    await waitFor(() => expect(m.activateTab).toHaveBeenCalledWith("tab-7"));
    expect(m.changeConnection).toHaveBeenCalledWith("tab-7", "pg1");
    expect(m.newQueryTab).not.toHaveBeenCalled();
  });

  it("does not open a tab when an active tab exists and nothing can be reused, nor when the connect fails", async () => {
    useConnections.setState({ connect: vi.fn(async () => true) } as never);
    useTabs.setState({ activeId: "t1" } as never);
    const { unmount } = render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(within(row("ms1")).getByText("Connect", { selector: "button" }));
    await waitFor(() => expect(useConnections.getState().connect).toHaveBeenCalled());
    await Promise.resolve();
    expect(m.newQueryTab).not.toHaveBeenCalled();
    unmount();
    useConnections.setState({ connect: vi.fn(async () => false) } as never);
    useTabs.setState({ activeId: undefined } as never);
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(within(row("ms1")).getByText("Connect", { selector: "button" }));
    await waitFor(() => expect(useConnections.getState().connect).toHaveBeenCalled());
    await Promise.resolve();
    expect(m.newQueryTab).not.toHaveBeenCalled();
    expect(m.activateTab).not.toHaveBeenCalled();
  });

  it("Ctrl+Enter connects and keeps the dialog open", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    useSettings.setState({ recentProfiles: ["ms1"] });
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    fireEvent.keyDown(search(), { key: "Enter", ctrlKey: true });
    await waitFor(() => expect(connect).toHaveBeenCalledWith("ms1"));
    expect(onClose).not.toHaveBeenCalled();
    expect(m.newQueryTab).not.toHaveBeenCalled();
  });

  it("a connected row says so; its action, Enter and double-click open a new query tab", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect, servers: connected("ms1") } as never);
    useSettings.setState({ recentProfiles: ["ms1"] });
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    expect(row("ms1").textContent).toContain("Connected");
    expect(footer().textContent).toContain("New query");
    fireEvent.keyDown(search(), { key: "Enter" });
    expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "ms1" });
    expect(onClose).toHaveBeenCalled();
    fireEvent.doubleClick(row("ms1"));
    expect(m.newQueryTab).toHaveBeenCalledTimes(2);
    expect(connect).not.toHaveBeenCalled();
  });

  it("a connecting row offers Cancel and ignores Enter (never a second dial)", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect, servers: { ms1: { profileId: "ms1", status: "connecting", databases: [] } } } as never);
    useSettings.setState({ recentProfiles: ["ms1"] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(row("ms1").textContent).toContain("Connecting…");
    fireEvent.keyDown(search(), { key: "Enter" });
    expect(connect).not.toHaveBeenCalled();
    await userEvent.click(within(row("ms1")).getByText("Cancel", { selector: "button" }));
    expect(m.disconnectServer).toHaveBeenCalledWith("ms1");
  });

  it("a failed row shows Failed with the error in the footer, and Retry connects again", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect, servers: { pg1: { profileId: "pg1", status: "error", error: "Login failed for user 'app'", databases: [] } } } as never);
    useSettings.setState({ recentProfiles: ["pg1"] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(row("pg1").textContent).toContain("Failed");
    expect(footer().textContent).toContain("Retry");
    expect(footer().textContent).toContain("Login failed for user 'app'");
    await userEvent.click(within(row("pg1")).getByText("Retry", { selector: "button" }));
    expect(connect).toHaveBeenCalledWith("pg1");
  });

  it("a profile this build cannot open is Unavailable, is never the default row, and ignores Enter", () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    useProfiles.setState({ profiles: [prof("old", "pg", { unsupported: "Saved by a newer version of Querithm." })] });
    useSettings.setState({ recentProfiles: ["old"] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(row("old").textContent).toContain("Unavailable");
    expect(activeRow()).toBeNull();
    fireEvent.click(row("old"));
    fireEvent.keyDown(search(), { key: "Enter" });
    expect(connect).not.toHaveBeenCalled();
    expect(footer().textContent).toContain("Saved by a newer version of Querithm.");
  });

  it("Edit and F2 open the editor for the row, which comes back to this dialog", async () => {
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    await userEvent.click(within(row("ms1")).getByRole("button", { name: "Edit Sales" }));
    expect(onClose).toHaveBeenCalled();
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "profile", profileId: "ms1", fromConnect: true });
    fireEvent.click(row("pg1"));
    fireEvent.keyDown(search(), { key: "F2" });
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "profile", profileId: "pg1", fromConnect: true });
  });

  it("the ⋯ menu duplicates and deletes (with the profile's identity) and has no Pin item; Delete is not a row icon", async () => {
    useProfiles.setState({ profiles: [prof("pg1", "pg", { name: "Billing", environment: "prod", port: 5432, groupName: "Finance" }), prof("ms1", "mssql", { name: "Sales" })] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(screen.queryByTitle(/Delete/)).toBeNull();
    await userEvent.click(within(row("pg1")).getByRole("button", { name: "More actions for Billing" }));
    expect(activeRow()).toBe("pg1");
    const menu = screen.getByRole("menu", { name: "Actions for Billing" });
    expect(within(menu).getAllByRole("menuitem").map((b) => b.textContent)).toEqual(expect.arrayContaining(["ConnectEnter", "Edit…F2", "Duplicate…", "Copy address"]));
    expect(within(menu).queryByRole("menuitem", { name: /^Pin/ })).toBeNull();
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete profile…" }));
    expect(m.confirmDeleteProfile).toHaveBeenCalledWith(expect.objectContaining({ id: "pg1" }), "Production · h-pg1:5432 · Finance");
  });

  it("right-click and Shift+F10 open the same menu for that row", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    fireEvent.contextMenu(row("ms1"), { clientX: 10, clientY: 10 });
    expect(screen.getByRole("menu", { name: "Actions for Sales" })).toBeTruthy();
    fireEvent.keyDown(search(), { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("menu")).toBeNull());
    fireEvent.click(row("pg1"));
    fireEvent.keyDown(search(), { key: "F10", shiftKey: true });
    expect(screen.getByRole("menu", { name: "Actions for Billing" })).toBeTruthy();
  });
});

describe("ConnectDialog: telling rows apart", () => {
  it("two rows with the same name, group and address add the user; outside a group section, the group is named", () => {
    useProfiles.setState({
      profiles: [
        prof("a", "pg", { name: "orders-db", environment: "prod", groupName: "Billing", host: "pg-prod", port: 5432, username: "app_rw" }),
        prof("b", "pg", { name: "orders-db", environment: "prod", groupName: "Billing", host: "pg-prod", port: 5432, username: "admin" }),
      ],
    });
    const { rerender } = render(<ConnectDialog onClose={() => {}} />);
    expect(row("a").textContent).toContain("· app_rw");
    expect(row("a").textContent).not.toContain("· Billing");
    expect(row("b").textContent).toContain("· admin");
    useSettings.setState({ showGroups: false });
    rerender(<ConnectDialog onClose={() => {}} />);
    expect(row("a").textContent).toContain("· Billing · app_rw");
  });

  it("marks a DEV profile whose name says prod, until the user turns the hint off for it", async () => {
    useProfiles.setState({ profiles: [prof("r", "mySql", { name: "orders-prod-replica" }), prof("p", "pg", { name: "products-db" })] });
    useSettings.setState({ recentProfiles: ["r"] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(screen.getByRole("option", { name: /orders-prod-replica.*name suggests Production/ })).toBeTruthy();
    expect(screen.getByRole("option", { name: /products-db/ }).getAttribute("aria-label")).not.toContain("name suggests Production");
    expect(footer().textContent).toContain("name says prod");
    await userEvent.click(within(row("r")).getByRole("button", { name: "More actions for orders-prod-replica" }));
    await userEvent.click(screen.getByRole("menuitem", { name: /Don’t warn for this profile/ }));
    expect(useSettings.getState().prodHintMuted).toEqual(["r"]);
    expect(footer().textContent).not.toContain("name says prod");
  });
});

// Bulk move (§6): Organize › Select profiles… ticks connections and moves them into a group.
describe("Connect dialog: groups and Select mode", () => {
  const box = (name: string) => screen.getByLabelText(`Select ${name}`) as HTMLInputElement;
  const startSelecting = async () => {
    await userEvent.click(screen.getByRole("button", { name: "Organize" }));
    await userEvent.click(screen.getByRole("menuitem", { name: /Select profiles…/ }));
  };
  beforeEach(() => {
    useProfiles.setState({
      profiles: [
        prof("orders", "mssql", { name: "Orders", environment: "prod", groupName: "Payments" }),
        prof("ledger", "mySql", { name: "Ledger", groupName: "Payments" }),
        prof("warehouse", "duckDb", { name: "Warehouse", environment: "prod" }),
        prof("scratch", "sqlite", { name: "Scratch", environment: "local" }),
      ],
      groups: [{ name: "Payments", members: 2 }],
    });
  });

  it("shows a grouped profile under its group with the counts, and the others in the environment sections", () => {
    render(<ConnectDialog onClose={() => {}} />);
    const group = screen.getByRole("group", { name: "Payments, 2 profiles, 1 Production" });
    expect(group.textContent).toContain("2 · 1 Production");
    expect(screen.getByRole("group", { name: "Production, 1 profile" }).textContent).toContain("Warehouse");
    expect(screen.queryByText(/no group/i)).toBeNull();
  });

  it("Show groups in Organize turns the group sections off", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Organize" }));
    await userEvent.click(screen.getByRole("menuitemcheckbox", { name: "Show groups" }));
    expect(useSettings.getState().showGroups).toBe(false);
    expect(screen.queryByRole("group", { name: /^Payments/ })).toBeNull();
    expect(row("orders").textContent).toContain("· Payments");
  });

  it("ticks rows instead of connecting, ranges with Shift, and moves the ticked ones", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await startSelecting();
    fireEvent.click(screen.getByText("Orders"));
    fireEvent.click(screen.getByText("Warehouse"), { shiftKey: true });
    expect(["Orders", "Ledger", "Warehouse", "Scratch"].map((n) => box(n).checked)).toEqual([true, true, true, false]);
    fireEvent.doubleClick(screen.getByText("Scratch"));
    expect(useConnections.getState().servers.scratch).toBeUndefined();
    const bar = screen.getByRole("toolbar", { name: "Selection" });
    expect(bar.textContent).toContain("3 selected");
    expect(bar.textContent).toContain("2 Production");
    await userEvent.click(within(bar).getByRole("button", { name: "Move to group…" }));
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "moveToGroup", profileIds: ["orders", "ledger", "warehouse"] });
  });

  it("a group heading ticks all its shown profiles; the selection survives a filter and says how many it hides", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await startSelecting();
    await userEvent.click(screen.getByLabelText("Select all in Payments"));
    expect([box("Orders").checked, box("Ledger").checked]).toEqual([true, true]);
    await userEvent.type(search(), "orders");
    expect(screen.getByRole("toolbar", { name: "Selection" }).textContent).toContain("1 hidden by filter");
  });

  it("Enter ticks the active row in Select mode; Esc leaves Select mode before it closes the dialog", async () => {
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    await startSelecting();
    fireEvent.keyDown(search(), { key: "ArrowDown" });
    fireEvent.keyDown(search(), { key: "Enter" });
    expect(box("Orders").checked).toBe(true);
    expect(useConnections.getState().servers.orders).toBeUndefined();
    fireEvent.keyDown(search(), { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("toolbar", { name: "Selection" })).toBeNull());
    expect(onClose).not.toHaveBeenCalled();
  });

  it("Remove from group asks first when a Production connection is among them, then ungroups only the grouped ones", async () => {
    m.rpc.mockImplementation(async (method: unknown) => (method === "group.assign" ? { groups: [], profiles: [] } : undefined));
    render(<ConnectDialog onClose={() => {}} />);
    await startSelecting();
    fireEvent.click(screen.getByText("Orders"));
    fireEvent.click(screen.getByText("Scratch"));
    await userEvent.click(screen.getByRole("button", { name: "Remove from group" }));
    let d: unknown;
    await waitFor(() => expect((d = useUi.getState().dialogs.at(-1)) && (d as { kind: string }).kind).toBe("confirm"));
    (d as { resolve: (ok: boolean) => void }).resolve(true);
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("group.assign", { profileIds: ["orders"], name: null }));
  });

  it("Manage groups… in Organize opens the group manager", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Organize" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Manage groups…" }));
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "groups" });
  });
});

// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, Profile } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]) => undefined),
  activateTab: vi.fn(),
  changeConnection: vi.fn(async (..._a: unknown[]) => {}),
  newQueryTab: vi.fn(),
  tabForNewConnection: vi.fn((): string | undefined => undefined),
  confirmDeleteProfile: vi.fn(async (..._a: unknown[]) => true),
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../app/actions", () => ({ activateTab: m.activateTab, changeConnection: m.changeConnection, newQueryTab: m.newQueryTab }));
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

beforeEach(() => {
  Element.prototype.scrollIntoView = vi.fn();
  (globalThis.CSS as unknown) = { ...(globalThis.CSS ?? {}), escape: (s: string) => s };
  Object.values(m).forEach((f) => f.mockClear());
  m.tabForNewConnection.mockReturnValue(undefined);
  useProfiles.setState({
    profiles: [prof("ms1", "mssql", { name: "Sales", port: 1433 }), prof("pg1", "pg", { name: "Billing", environment: "prod" })],
    groups: [],
  });
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useSettings.setState({ pinnedProfiles: [] });
  useUi.setState({ dialogs: [] });
  useTabs.setState({ activeId: undefined } as never);
});
afterEach(cleanup);

describe("ConnectDialog", () => {
  it("shows an empty state without profiles and opens the new-profile editor", async () => {
    useProfiles.setState({ profiles: [] });
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    expect(screen.getByText(/No saved profiles yet/)).toBeTruthy();
    expect(screen.queryByLabelText("Search profiles")).toBeNull();
    await userEvent.click(screen.getByText("+ New Profile"));
    expect(onClose).toHaveBeenCalled();
    expect(useUi.getState().dialogs).toEqual([{ kind: "profile" }]);
  });

  it("lists profiles of both engines with logos and host:port", () => {
    render(<ConnectDialog onClose={() => {}} />);
    expect(screen.getByText("Sales")).toBeTruthy();
    expect(screen.getByText("Billing")).toBeTruthy();
    expect(screen.getByText("h-ms1:1433")).toBeTruthy();
    expect(screen.getByLabelText("Microsoft SQL Server")).toBeTruthy();
    expect(screen.getByLabelText("PostgreSQL")).toBeTruthy();
    expect(screen.getByText(/2 profiles/)).toBeTruthy();
  });

  it("filters by search text and shows a clearable no-match state", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    const search = screen.getByLabelText("Search profiles");
    await userEvent.type(search, "billing");
    expect(screen.queryByText("Sales")).toBeNull();
    expect(screen.getByText(/1 of 2 profiles/)).toBeTruthy();
    await userEvent.clear(search);
    await userEvent.type(search, "zzz");
    expect(screen.getByText(/No profiles match/)).toBeTruthy();
    await userEvent.click(screen.getByText("Clear filters"));
    expect(screen.getByText("Sales")).toBeTruthy();
  });

  it("filters by environment chip, toggles it off and disables empty environments", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    const prod = screen.getByRole("radio", { name: /Prod/ });
    await userEvent.click(prod);
    expect(screen.queryByText("Sales")).toBeNull();
    expect(screen.getByText("Billing")).toBeTruthy();
    expect(screen.getByText(/No profiles match|Billing/)).toBeTruthy();
    await userEvent.click(prod);
    expect(screen.getByText("Sales")).toBeTruthy();
    expect((screen.getByRole("radio", { name: /Stage/ }) as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(screen.getByRole("radio", { name: /Prod/ }));
    await userEvent.click(screen.getByRole("radio", { name: /All/ }));
    expect(screen.getByText("Sales")).toBeTruthy();
  });

  it("mentions the environment in the no-match message", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getByRole("radio", { name: /Prod/ }));
    await userEvent.type(screen.getByLabelText("Search profiles"), "sales");
    expect(screen.getByText(/in Prod/)).toBeTruthy();
  });

  it("pins and unpins a profile into a Pinned section", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getAllByTitle("Pin to top")[1]);
    expect(useSettings.getState().pinnedProfiles).toHaveLength(1);
    const pinnedId = useSettings.getState().pinnedProfiles[0];
    expect(screen.getByText("Pinned")).toBeTruthy();
    await userEvent.click(screen.getByTitle("Unpin"));
    expect(useSettings.getState().pinnedProfiles).not.toContain(pinnedId);
  });

  it("edit closes the dialog and opens the profile editor for that profile", async () => {
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    await userEvent.click(screen.getAllByTitle("Edit profile")[0]);
    expect(onClose).toHaveBeenCalled();
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "profile" });
    expect((useUi.getState().dialogs.at(-1) as { profileId: string }).profileId).toMatch(/ms1|pg1/);
  });

  it("delete goes through the confirm-and-delete flow", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getAllByTitle("Delete profile and its data")[0]);
    expect(m.confirmDeleteProfile).toHaveBeenCalledTimes(1);
    expect(m.confirmDeleteProfile.mock.calls[0][0]).toMatchObject({ id: expect.stringMatching(/ms1|pg1/) });
  });

  it("Connect opens a new query tab when none is active and connection succeeded", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    await userEvent.click(screen.getAllByText("Connect", { selector: "button" })[0]);
    await waitFor(() => expect(m.newQueryTab).toHaveBeenCalled());
    expect(onClose).toHaveBeenCalled();
    expect(connect).toHaveBeenCalled();
  });

  it("Connect reuses an unconnected tab instead of opening a new one", async () => {
    useConnections.setState({ connect: vi.fn(async () => true) } as never);
    m.tabForNewConnection.mockReturnValue("tab-7");
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getAllByText("Connect", { selector: "button" })[0]);
    await waitFor(() => expect(m.activateTab).toHaveBeenCalledWith("tab-7"));
    expect(m.changeConnection).toHaveBeenCalledWith("tab-7", expect.stringMatching(/ms1|pg1/));
    expect(m.newQueryTab).not.toHaveBeenCalled();
  });

  it("does not open a tab when an active tab exists and nothing can be reused", async () => {
    useConnections.setState({ connect: vi.fn(async () => true) } as never);
    useTabs.setState({ activeId: "t1" } as never);
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getAllByText("Connect", { selector: "button" })[0]);
    await waitFor(() => expect(useConnections.getState().connect).toHaveBeenCalled());
    await Promise.resolve();
    expect(m.newQueryTab).not.toHaveBeenCalled();
    expect(m.changeConnection).not.toHaveBeenCalled();
  });

  it("does nothing further when the connection fails", async () => {
    useConnections.setState({ connect: vi.fn(async () => false) } as never);
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.click(screen.getAllByText("Connect", { selector: "button" })[0]);
    await waitFor(() => expect(useConnections.getState().connect).toHaveBeenCalled());
    await Promise.resolve();
    expect(m.newQueryTab).not.toHaveBeenCalled();
    expect(m.activateTab).not.toHaveBeenCalled();
  });

  it("shows connected and connecting states instead of the Connect button", () => {
    useConnections.setState({
      servers: { ms1: { profileId: "ms1", status: "connected", databases: [] }, pg1: { profileId: "pg1", status: "connecting", databases: [] } },
      order: ["ms1", "pg1"],
    } as never);
    render(<ConnectDialog onClose={() => {}} />);
    expect(screen.getByText("Connected")).toBeTruthy();
    expect(screen.getByText("Connecting…")).toBeTruthy();
    expect(screen.queryByText("Connect", { selector: "button" })).toBeNull();
  });

  it("double-click connects an unconnected profile but not a connected one", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect, servers: { ms1: { profileId: "ms1", status: "connected", databases: [] } }, order: ["ms1"] } as never);
    render(<ConnectDialog onClose={() => {}} />);
    await userEvent.dblClick(screen.getByText("Sales"));
    expect(connect).not.toHaveBeenCalled();
    await userEvent.dblClick(screen.getByText("Billing"));
    expect(connect).toHaveBeenCalledWith("pg1");
  });

  it("arrow keys move the selection and Enter connects the selected profile", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect } as never);
    render(<ConnectDialog onClose={() => {}} />);
    const search = screen.getByLabelText("Search profiles");
    search.focus();
    // flat order: dev (Sales) before prod? the order is whatever the list shows; read it from the DOM.
    const ids = () => Array.from(document.querySelectorAll("[data-profile]")).map((e) => e.getAttribute("data-profile"));
    const order = ids();
    await userEvent.keyboard("{ArrowDown}");
    expect(document.querySelector(".ring-1")?.getAttribute("data-profile")).toBe(order[1]);
    await userEvent.keyboard("{ArrowDown}{ArrowDown}");
    expect(document.querySelector(".ring-1")?.getAttribute("data-profile")).toBe(order[1]);
    await userEvent.keyboard("{ArrowUp}");
    expect(document.querySelector(".ring-1")?.getAttribute("data-profile")).toBe(order[0]);
    await userEvent.keyboard("{Enter}");
    expect(connect).toHaveBeenCalledWith(order[0]);
  });

  it("Enter does not reconnect an already connected profile", async () => {
    const connect = vi.fn(async () => true);
    useConnections.setState({ connect, servers: { ms1: { profileId: "ms1", status: "connected", databases: [] }, pg1: { profileId: "pg1", status: "connected", databases: [] } } } as never);
    render(<ConnectDialog onClose={() => {}} />);
    screen.getByLabelText("Search profiles").focus();
    await userEvent.keyboard("{Enter}");
    expect(connect).not.toHaveBeenCalled();
  });

  it("clicking a row selects it", async () => {
    render(<ConnectDialog onClose={() => {}} />);
    const row = document.querySelector('[data-profile="pg1"]') as HTMLElement;
    fireEvent.click(row);
    expect(row.className).toContain("ring-1");
  });

  it("Close calls onClose", async () => {
    const onClose = vi.fn();
    render(<ConnectDialog onClose={onClose} />);
    await userEvent.click(screen.getByText("Close"));
    expect(onClose).toHaveBeenCalled();
  });

  it("groups profiles under their group heading", () => {
    useProfiles.setState({ profiles: [prof("a", "pg", { groupName: "Analytics" }), prof("b", "mssql")] });
    render(<ConnectDialog onClose={() => {}} />);
    expect(screen.getByText("Analytics")).toBeTruthy();
  });
});

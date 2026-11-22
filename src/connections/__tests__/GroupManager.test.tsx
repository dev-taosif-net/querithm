// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, Profile, ProfileGroup } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  RpcError: class extends Error {
    constructor(message: string, readonly code = -1, readonly appCode?: string, readonly data?: unknown) {
      super(message);
    }
  },
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: m.RpcError }));

import { GroupManagerDialog } from "../GroupManager";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useUi, type Dialog } from "../../store/ui";

const prof = (id: string, engine: Engine, over: Partial<Profile> = {}) => ({ id, name: id, engine, environment: "dev", host: "h", ...over }) as Profile;
const groupList = () => within(screen.getByRole("list", { name: "Groups" }));
const nameBox = () => screen.getByLabelText("Name") as HTMLInputElement;

/** A fake sidecar: group.list answers the store's groups; other calls answer what the test queues. */
let groups: ProfileGroup[];
const replies: unknown[] = [];
beforeEach(() => {
  groups = [{ name: "Payments", members: 3 }, { name: "Analytics", members: 1 }, { name: "Empty", members: 0 }];
  replies.length = 0;
  m.rpc.mockReset();
  m.rpc.mockImplementation(async (method: unknown) => {
    if (method === "group.list") return groups;
    const r = replies.shift();
    if (r instanceof Error) throw r;
    return r;
  });
  useProfiles.setState({
    groups,
    profiles: [
      prof("orders", "mssql", { environment: "prod", groupName: "Payments" }),
      prof("billing", "oracle", { environment: "prod", groupName: "Payments" }),
      prof("ledger", "mySql", { groupName: "Payments" }),
      prof("warehouse", "duckDb", { environment: "prod", groupName: "Analytics" }),
      prof("scratch", "sqlite"),
      prof("reports", "pg"),
    ],
  });
  useSettings.setState({ showGroups: true });
  useUi.setState({ dialogs: [], toasts: [] });
});
afterEach(cleanup);

/** Answers the confirm dialog `ask` opened. */
async function answerConfirm(ok: boolean): Promise<Extract<Dialog, { kind: "confirm" }>> {
  let d: Dialog | undefined;
  await waitFor(() => expect((d = useUi.getState().dialogs.at(-1))?.kind).toBe("confirm"));
  const c = d as Extract<Dialog, { kind: "confirm" }>;
  c.resolve(ok);
  return c;
}

describe("Group manager", () => {
  it("lists the groups A–Z with their member and Production counts, and the first one's members", () => {
    render(<GroupManagerDialog onClose={() => {}} />);
    expect(groupList().getAllByRole("button").map((b) => b.textContent)).toEqual(["Analytics1 · 1 Prod", "Emptyempty", "Payments3 · 2 Prod"]);
    expect(nameBox().value).toBe("Analytics");
    expect(screen.getByText("Members (1)")).toBeTruthy();
  });

  it("New makes an empty group and selects it; an existing name is said at the field", async () => {
    render(<GroupManagerDialog focus="Payments" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "+ New" }));
    replies.push(new m.RpcError("A group with this name already exists.", -32001, "groupExists", { field: "name" }));
    await userEvent.type(screen.getByLabelText("New group name"), "payments{Enter}");
    await waitFor(() => expect(screen.getByText("A group with this name already exists.")).toBeTruthy());
    await userEvent.clear(screen.getByLabelText("New group name"));
    replies.push([...groups, { name: "Core Banking", members: 0 }]);
    await userEvent.type(screen.getByLabelText("New group name"), "Core   Banking");
    await userEvent.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() => expect(nameBox().value).toBe("Core Banking"));
    expect(m.rpc).toHaveBeenCalledWith("group.create", { name: "Core Banking" });
  });

  it("Rename moves the members and follows the new name; a case-only rename is allowed; a collision is said at the field", async () => {
    render(<GroupManagerDialog focus="Payments" onClose={() => {}} />);
    await userEvent.clear(nameBox());
    await userEvent.type(nameBox(), "Analytics");
    replies.push(new m.RpcError("A group with this name already exists.", -32001, "groupExists", { field: "name" }));
    await userEvent.click(screen.getByRole("button", { name: "Rename" }));
    await waitFor(() => expect(screen.getByText("A group with this name already exists.")).toBeTruthy());
    expect(useProfiles.getState().byId("orders")?.groupName).toBe("Payments");

    await userEvent.clear(nameBox());
    await userEvent.type(nameBox(), "PAYMENTS");
    groups = [{ name: "Analytics", members: 1 }, { name: "Empty", members: 0 }, { name: "PAYMENTS", members: 3 }];
    replies.push({ groups, renamed: { from: "Payments", to: "PAYMENTS" }, profiles: ["orders", "billing", "ledger"].map((id) => ({ ...useProfiles.getState().byId(id)!, groupName: "PAYMENTS" })) });
    await userEvent.click(screen.getByRole("button", { name: "Rename" }));
    await waitFor(() => expect(screen.getByText("Members (3)")).toBeTruthy());
    expect(m.rpc).toHaveBeenCalledWith("group.rename", { from: "Payments", to: "PAYMENTS" });
    expect(nameBox().value).toBe("PAYMENTS");
    expect(useProfiles.getState().byId("orders")?.groupName).toBe("PAYMENTS");
  });

  it("Remove from group ungroups only the ticked members", async () => {
    render(<GroupManagerDialog focus="Payments" onClose={() => {}} />);
    const members = within(screen.getByRole("list", { name: "Members of Payments" }));
    await userEvent.click(members.getByLabelText(/ledger/));
    replies.push({ groups, profiles: [{ ...useProfiles.getState().byId("ledger")!, groupName: null }] });
    await userEvent.click(screen.getByRole("button", { name: "Remove from group (1)" }));
    await waitFor(() => expect(screen.getByText("Members (2)")).toBeTruthy());
    expect(m.rpc).toHaveBeenCalledWith("group.assign", { profileIds: ["ledger"], name: null });
  });

  it("Add connections lists the others with their current group and moves the picked ones in", async () => {
    render(<GroupManagerDialog focus="Empty" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Add connections…" }));
    const pick = within(screen.getByRole("list", { name: "Connections to add" }));
    expect(pick.getAllByRole("checkbox")).toHaveLength(6);
    expect(pick.getAllByText("in Payments")).toHaveLength(3);
    await userEvent.type(screen.getByLabelText("Search connections"), "re");
    expect(pick.getAllByRole("checkbox")).toHaveLength(2); // reports, warehouse
    await userEvent.click(pick.getByLabelText(/reports/));
    await userEvent.click(pick.getByLabelText(/warehouse/));
    replies.push({ groups, profiles: ["reports", "warehouse"].map((id) => ({ ...useProfiles.getState().byId(id)!, groupName: "Empty" })), missing: null });
    await userEvent.click(screen.getByRole("button", { name: "Add 2 connections" }));
    await waitFor(() => expect(screen.getByText("Members (2)")).toBeTruthy());
    expect(m.rpc).toHaveBeenCalledWith("group.assign", { profileIds: ["reports", "warehouse"], name: "Empty" });
  });

  it("Delete says how many connections (and how many Production) leave the group, and sends the count it showed", async () => {
    render(<GroupManagerDialog focus="Payments" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Delete group…" }));
    replies.push({ groups: groups.filter((g) => g.name !== "Payments"), profiles: [] });
    const c = await answerConfirm(true);
    expect(c.title).toBe("Delete group “Payments”?");
    expect(c.body).toMatch(/^3 connections \(2 Production\) leave the group and go back to their usual place in the list\..*No connection is deleted\.$/);
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("group.delete", { name: "Payments", expectedMembers: 3 }));
  });

  it("a member count that changed meanwhile asks again with the real count", async () => {
    render(<GroupManagerDialog focus="Payments" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Delete group…" }));
    replies.push(new m.RpcError("The group now has 4 connections, not 3.", -32001, "groupChanged", { members: 4 }));
    await answerConfirm(true);
    replies.push({ groups: [], profiles: [] });
    const again = await answerConfirm(true);
    expect(again.body).toMatch(/^4 connections/);
    await waitFor(() => expect(m.rpc).toHaveBeenLastCalledWith("group.delete", { name: "Payments", expectedMembers: 4 }));
  });

  it("cancelling the delete changes nothing; an empty group goes without asking", async () => {
    render(<GroupManagerDialog focus="Payments" onClose={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Delete group…" }));
    await answerConfirm(false);
    expect(m.rpc).not.toHaveBeenCalledWith("group.delete", expect.anything());
    await userEvent.click(groupList().getByRole("button", { name: /^Empty/ }));
    replies.push({ groups: groups.filter((g) => g.name !== "Empty"), profiles: [] });
    await userEvent.click(screen.getByRole("button", { name: "Delete group…" }));
    await waitFor(() => expect(m.rpc).toHaveBeenCalledWith("group.delete", { name: "Empty", expectedMembers: 0 }));
  });

  it("a failed change keeps the members as they were and says why", async () => {
    render(<GroupManagerDialog focus="Payments" onClose={() => {}} />);
    await userEvent.click(within(screen.getByRole("list", { name: "Members of Payments" })).getByLabelText(/orders/));
    replies.push(new m.RpcError("Querithm's store is busy. Nothing was changed; try again.", -32001, "storeBusy"));
    await userEvent.click(screen.getByRole("button", { name: "Remove from group (1)" }));
    await waitFor(() => expect(useUi.getState().toasts.some((t) => t.kind === "error" && /store is busy/.test(t.text))).toBe(true));
    expect(screen.getByText("Members (3)")).toBeTruthy();
  });

  it("the Show groups switch is the same setting the Explorer and the Connect dialog use", async () => {
    render(<GroupManagerDialog onClose={() => {}} />);
    await userEvent.click(screen.getByLabelText("Show groups in the Object Explorer and the Connect dialog"));
    expect(useSettings.getState().showGroups).toBe(false);
  });
});

// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Engine, Profile } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  rpc: vi.fn(async (..._a: unknown[]): Promise<unknown> => undefined),
  RpcError: class extends Error {
    constructor(message: string, readonly code = -1, readonly appCode?: string, readonly data?: unknown) {
      super(message);
    }
  },
}));
vi.mock("../../ipc/client", () => ({ rpc: m.rpc, onNotify: () => () => {}, shell: {}, RpcError: m.RpcError }));

import { MoveToGroupDialog } from "../MoveToGroup";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";
import { ENGINE_LIST } from "../../engines";

const prof = (id: string, engine: Engine, over: Partial<Profile> = {}) => ({ id, name: id, engine, environment: "dev", host: "h", ...over }) as Profile;
const groupBox = () => screen.getByLabelText("Group") as HTMLInputElement;
const toasts = () => useUi.getState().toasts.map((t) => `${t.kind}:${t.text}`);

beforeEach(() => {
  m.rpc.mockReset();
  useProfiles.setState({ groups: [{ name: "Payments", members: 1 }] });
  useUi.setState({ toasts: [], dialogs: [] });
});
afterEach(cleanup);

describe("Move to group", () => {
  for (const engine of ENGINE_LIST) {
    it(`moves every chosen connection into an existing group in its stored spelling (${engine})`, async () => {
      useProfiles.setState({ profiles: [prof("a", engine, { environment: "prod", groupName: "Old" }), prof("b", engine), prof("c", engine, { groupName: "Payments" })] });
      m.rpc.mockImplementation(async (method: unknown, params: unknown) => {
        expect(method).toBe("group.assign");
        expect(params).toEqual({ profileIds: ["a", "b"], name: "Payments" });
        return { groups: [{ name: "Payments", members: 3 }], profiles: [prof("a", engine, { environment: "prod", groupName: "Payments" }), prof("b", engine, { groupName: "Payments" })] };
      });
      const onClose = vi.fn();
      render(<MoveToGroupDialog profileIds={["a", "b"]} onClose={onClose} />);
      expect(screen.getByText("from Old")).toBeTruthy();
      expect(screen.getByText("no group")).toBeTruthy();
      expect(screen.getByText(/1 Production connection stays Production/)).toBeTruthy();
      await userEvent.type(groupBox(), "  PAYMENTS ");
      expect(screen.getByText("Joins “Payments” · 1 connection")).toBeTruthy();
      await userEvent.click(screen.getByRole("button", { name: "Move 2 connections" }));
      await waitFor(() => expect(onClose).toHaveBeenCalled());
      expect(useProfiles.getState().profiles.map((p) => p.groupName)).toEqual(["Payments", "Payments", "Payments"]);
      expect(toasts()).toContain("info:Moved 2 connections to “Payments”.");
    });
  }

  it("a new name makes the group, and connections deleted meanwhile are skipped and said so", async () => {
    useProfiles.setState({ profiles: [prof("a", "pg"), prof("b", "pg")] });
    m.rpc.mockResolvedValueOnce({ groups: [{ name: "Core Banking", members: 1 }], profiles: [prof("a", "pg", { groupName: "Core Banking" })], missing: ["b"] });
    render(<MoveToGroupDialog profileIds={["a", "b"]} onClose={() => {}} />);
    await userEvent.type(groupBox(), "Core   Banking");
    expect(screen.getByText("Creates group “Core Banking”")).toBeTruthy();
    await userEvent.tab();
    expect(groupBox().value).toBe("Core Banking");
    await userEvent.click(screen.getByRole("button", { name: "Move 2 connections" }));
    await waitFor(() => expect(toasts()).toContain("warn:Moved 1 connection to “Core Banking”. 1 connection was deleted elsewhere and skipped."));
    expect(m.rpc).toHaveBeenCalledWith("group.assign", { profileIds: ["a", "b"], name: "Core Banking" });
  });

  it("a refused name is said at the field and Move stays off", async () => {
    useProfiles.setState({ profiles: [prof("a", "pg")] });
    render(<MoveToGroupDialog profileIds={["a"]} onClose={() => {}} />);
    await userEvent.type(groupBox(), "Pay​ments");
    expect(groupBox().getAttribute("aria-invalid")).toBe("true");
    expect((screen.getByRole("button", { name: "Move 1 connection" }) as HTMLButtonElement).disabled).toBe(true);
    expect(m.rpc).not.toHaveBeenCalled();
  });

  it("a failed move keeps the dialog open with the reason and changes nothing", async () => {
    useProfiles.setState({ profiles: [prof("a", "pg", { groupName: "Old" })] });
    m.rpc.mockRejectedValueOnce(new m.RpcError("Querithm's store is busy. Nothing was changed; try again.", -32001, "storeBusy"));
    const onClose = vi.fn();
    render(<MoveToGroupDialog profileIds={["a"]} onClose={onClose} />);
    await userEvent.type(groupBox(), "Payments");
    await userEvent.click(screen.getByRole("button", { name: "Move 1 connection" }));
    await waitFor(() => expect(screen.getByText(/store is busy/)).toBeTruthy());
    expect(onClose).not.toHaveBeenCalled();
    expect(useProfiles.getState().byId("a")?.groupName).toBe("Old");
  });

  it("connections deleted before the move leave the list", async () => {
    useProfiles.setState({ profiles: [] });
    render(<MoveToGroupDialog profileIds={["gone"]} onClose={() => {}} />);
    expect(screen.getByText("These connections no longer exist.")).toBeTruthy();
  });
});

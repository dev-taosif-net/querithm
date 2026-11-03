import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Engine } from "../../ipc/contract";

const { rpc } = vi.hoisted(() => ({ rpc: vi.fn(async (..._a: unknown[]) => undefined) }));
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, shell: { secretEndConnection: async () => {}, secretDelete: async () => {}, secretExists: async () => true }, RpcError: class extends Error {} }));
vi.mock("../../app/actions", () => ({ settleTab: async () => true }));

import { confirmDeleteProfile, DELETE_PROFILE_BODY } from "../deleteProfile";
import { useProfiles } from "../../store/profiles";
import { useUi } from "../../store/ui";

type Confirm = { kind: "confirm"; title: string; body: string; danger: boolean; ok: string; resolve: (v: boolean) => void };

describe.each<Engine>(["mssql", "pg"])("confirmDeleteProfile (%s)", (engine) => {
  beforeEach(() => {
    rpc.mockClear();
    useUi.setState({ dialogs: [] });
    useProfiles.setState({ profiles: [{ id: "p", name: "Prod DB", engine, environment: "prod" } as never] });
  });

  it("asks with a danger confirm naming the profile and deletes only when confirmed", async () => {
    const pending = confirmDeleteProfile({ id: "p", name: "Prod DB" });
    const dlg = useUi.getState().dialogs.at(-1) as Confirm;
    expect(dlg).toMatchObject({ kind: "confirm", danger: true, ok: "Delete", body: DELETE_PROFILE_BODY });
    expect(dlg.title).toContain("Prod DB");
    dlg.resolve(true);
    expect(await pending).toBe(true);
    expect(rpc).toHaveBeenCalledWith("profile.delete", { profileId: "p" });
    expect(useProfiles.getState().profiles).toEqual([]);
  });

  it("keeps the profile when the user cancels", async () => {
    const pending = confirmDeleteProfile({ id: "p", name: "Prod DB" });
    (useUi.getState().dialogs.at(-1) as Confirm).resolve(false);
    expect(await pending).toBe(false);
    expect(rpc).not.toHaveBeenCalled();
    expect(useProfiles.getState().profiles).toHaveLength(1);
  });
});

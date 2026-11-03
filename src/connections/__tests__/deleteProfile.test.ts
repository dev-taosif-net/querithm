import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Engine } from "../../ipc/contract";

const { rpc, secretDelete, settleTab, RpcError } = vi.hoisted(() => {
  class RpcError extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string) {
      super(message);
    }
  }
  return {
    rpc: vi.fn(async (..._args: unknown[]): Promise<unknown> => undefined),
    secretDelete: vi.fn(async () => {}),
    settleTab: vi.fn(async (_id: string) => true),
    RpcError,
  };
});
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, shell: { secretEndConnection: vi.fn(async () => {}), secretDelete, secretExists: async () => true }, RpcError }));
vi.mock("../../app/actions", () => ({ settleTab }));

import { deleteProfile } from "../deleteProfile";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useSnapshots } from "../../store/snapshots";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";

const profile = (id: string, engine: Engine) => ({ id, name: id, engine, environment: "dev" }) as never;

describe.each<Engine>(["mssql", "pg", "mySql", "sqlite", "duckDb"])("deleting a %s profile", (engine) => {
  beforeEach(() => {
    rpc.mockReset();
    rpc.mockImplementation(async () => undefined);
    secretDelete.mockClear();
    settleTab.mockReset();
    settleTab.mockImplementation(async () => true);
    useUi.setState({ toasts: [] });
    useProfiles.setState({ profiles: [profile("gone", engine), profile("kept", engine)] });
    useConnections.setState({
      servers: { gone: { profileId: "gone", sessionId: "sx", status: "connected", databases: [] } as never },
      order: ["gone"],
      disconnected: {},
    });
    useSnapshots.setState({
      entries: {
        "gone|db": { key: "gone|db", profileId: "gone", database: "db", status: "fresh", version: 1 },
        "kept|db": { key: "kept|db", profileId: "kept", database: "db", status: "fresh", version: 1 },
      },
    });
    useSettings.setState({ pinnedProfiles: ["kept", "gone"], explorerSchemaGroups: ["gone|db", "kept|db"] });
    useTabs.setState({
      order: ["t1", "t2", "t3", "t4"],
      tabs: {
        t1: { id: "t1", title: "q1", profileId: "gone", database: "db", sessionId: "s1", txState: "open", busy: false } as never,
        t2: { id: "t2", title: "q2", profileId: "kept", database: "db", sessionId: "s2", txState: "none", busy: false, boundObject: { profileId: "gone" } } as never,
        t3: { id: "t3", title: "q3", profileId: "kept", sessionId: "s3", txState: "none", busy: false } as never,
        t4: { id: "t4", title: "q4", profileId: "gone", txState: "none", busy: false } as never,
      },
    });
  });

  it("settles each of its tabs that holds a session first, as closing it would", async () => {
    expect(await deleteProfile("gone")).toBe(true);
    expect(settleTab.mock.calls.map(([id]) => id)).toEqual(["t1"]);
    expect(settleTab.mock.invocationCallOrder[0]).toBeLessThan(rpc.mock.invocationCallOrder[rpc.mock.calls.findIndex(([m]) => m === "profile.delete")]);
  });

  it("deletes it in the sidecar, which closes its sessions; the webview deletes no keyring entry itself", async () => {
    expect(await deleteProfile("gone")).toBe(true);
    expect(rpc).toHaveBeenCalledWith("profile.delete", { profileId: "gone" });
    // The shell deletes <id>, <id>:timed, <id>:ssh and <id>:ssh:timed after the delete (src-tauri keyring.rs).
    expect(secretDelete).not.toHaveBeenCalled();
    expect(rpc.mock.calls.filter(([m, p]) => m === "session.close" && (p as { sessionId: string }).sessionId === "s1")).toEqual([]);
    expect(useProfiles.getState().profiles.map((p) => p.id)).toEqual(["kept"]);
  });

  it("drops its snapshots, pin, explorer settings and disconnect marker", async () => {
    await deleteProfile("gone");
    expect(Object.keys(useConnections.getState().servers)).toEqual([]);
    expect(useConnections.getState().disconnected).toEqual({});
    expect(Object.keys(useSnapshots.getState().entries)).toEqual(["kept|db"]);
    expect(useSettings.getState().pinnedProfiles).toEqual(["kept"]);
    expect(useSettings.getState().explorerSchemaGroups).toEqual(["kept|db"]);
  });

  it("leaves its tabs open but not connected, and other profiles' tabs alone", async () => {
    await deleteProfile("gone");
    const { tabs } = useTabs.getState();
    expect(tabs.t1).toMatchObject({ profileId: undefined, database: undefined, sessionId: undefined, txState: "none" });
    expect(tabs.t2).toMatchObject({ profileId: "kept", sessionId: "s2", boundObject: undefined });
    expect(tabs.t3).toMatchObject({ profileId: "kept", sessionId: "s3" });
  });

  it("keeps everything when the user keeps a tab (its transaction or query is not ended)", async () => {
    settleTab.mockImplementation(async () => false);
    expect(await deleteProfile("gone")).toBe(false);
    expect(rpc).not.toHaveBeenCalledWith("profile.delete", expect.anything());
    expect(useProfiles.getState().profiles.map((p) => p.id)).toEqual(["gone", "kept"]);
    expect(useTabs.getState().tabs.t1).toMatchObject({ profileId: "gone", sessionId: "s1", txState: "open" });
  });

  it("keeps everything and says why when the sidecar refuses (profileBusy)", async () => {
    rpc.mockImplementation(async (m: unknown) => {
      if (m === "profile.delete") throw new RpcError("A tab on this connection has an open transaction or a running query.", -32001, "profileBusy");
      return undefined;
    });
    expect(await deleteProfile("gone")).toBe(false);
    expect(useProfiles.getState().profiles.map((p) => p.id)).toEqual(["gone", "kept"]);
    expect(useTabs.getState().tabs.t1).toMatchObject({ profileId: "gone", sessionId: "s1" });
    expect(useConnections.getState().servers.gone).toBeDefined();
    expect(useUi.getState().toasts.at(-1)).toMatchObject({ kind: "warn", text: expect.stringContaining("open transaction") });
  });

  it("finishes the delete but shows the reason when the keyring entries could not be removed", async () => {
    rpc.mockImplementation(async (m: unknown) => {
      if (m === "profile.delete") throw new RpcError("The profile was deleted, but its saved password could not be removed from the OS keyring (locked).", -32001, "secretsNotDeleted");
      return undefined;
    });
    expect(await deleteProfile("gone")).toBe(true);
    expect(useProfiles.getState().profiles.map((p) => p.id)).toEqual(["kept"]);
    expect(useTabs.getState().tabs.t1).toMatchObject({ profileId: undefined, sessionId: undefined });
    expect(useUi.getState().toasts.at(-1)).toMatchObject({ kind: "error", text: expect.stringContaining("OS keyring") });
  });
});

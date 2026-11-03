// Revoking and re-picking a database file (Phase D0c lifecycle): grant.revoke after a confirm, the sidecar's grantBusy
// refusal shown as it is, the server marked as needing a file; a re-pick through the shell's dialog that closes the old
// file's sessions, saves the new grant (read-only) and reconnects.
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Profile } from "../../ipc/contract";

const h = vi.hoisted(() => {
  class RpcError extends Error {
    constructor(message: string, readonly code: number, readonly appCode?: string) {
      super(message);
    }
  }
  return { rpc: vi.fn(), pick: vi.fn(), RpcError, shell: { secretDelete: vi.fn(async () => {}), secretSet: vi.fn(async () => {}), secretExists: vi.fn(async () => true), secretEndConnection: vi.fn(async () => {}) } };
});
vi.mock("../../ipc/client", () => ({ rpc: h.rpc, onNotify: () => () => {}, shell: h.shell, RpcError: h.RpcError }));
vi.mock("../fileGrants", async (orig) => ({ ...(await orig<typeof import("../fileGrants")>()), pickDatabaseFile: h.pick }));

import { isFileProfile, repickFile, REVOKED_NOTE, revokeFileAccess } from "../fileAccess";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { snapKey, useSnapshots } from "../../store/snapshots";
import { useTabs, type TabState } from "../../store/tabs";
import { useUi } from "../../store/ui";

const profile = (o: Partial<Profile>): Profile =>
  ({ id: "f1", name: "App", engine: "sqlite", location: "file", file: { grantId: "g-old", displayName: "app.db" }, auth: "none", environment: "dev", tlsMode: "default", trustCert: false, createdAt: 0, updatedAt: 0, databaseDefault: "main", ...o }) as Profile;

const toasts = () => useUi.getState().toasts.map((t) => `${t.kind}: ${t.text}`);
const calls = (method: string) => h.rpc.mock.calls.filter((c) => c[0] === method);
const tick = () => new Promise((r) => setTimeout(r, 0));
const tab = (o: Partial<TabState>) => useTabs.getState().add({ id: o.id ?? "t1", title: "q.sql", profileId: "f1", database: "main", ...o });
/** Answers the open confirm dialog. */
async function confirm(ok: boolean) {
  await tick();
  const d = useUi.getState().dialogs.find((x) => x.kind === "confirm") as { resolve(v: boolean): void; title: string; body: string } | undefined;
  expect(d, "confirm dialog is open").toBeTruthy();
  d!.resolve(ok);
  return d!;
}

beforeEach(() => {
  vi.clearAllMocks();
  h.rpc.mockReset();
  h.pick.mockReset();
  h.rpc.mockImplementation(async (m: string, p: any) => {
    if (m === "grant.revoke") return { closedSessions: 1 };
    if (m === "profile.save") return { ...p, updatedAt: 1 };
    if (m === "session.open") return { sessionId: "ex2" };
    if (m === "session.state") return { serverVersion: "3.46", database: "main" };
    if (m === "meta.databases") return [{ name: "main", isSystem: false }];
    if (m === "meta.refresh") return { jobId: "j" };
    return undefined;
  });
  useProfiles.setState({ profiles: [profile({}), profile({ id: "mem", location: "memory", file: null }), profile({ id: "ms", engine: "mssql", location: null, file: null })], groups: [] });
  useConnections.setState({ servers: { f1: { profileId: "f1", status: "connected", sessionId: "ex1", databases: [{ name: "main", isSystem: false } as never] } }, order: ["f1"], disconnected: {} });
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useUi.setState({ dialogs: [], toasts: [] });
  useSnapshots.setState({ entries: { [snapKey("f1", "main")]: { key: snapKey("f1", "main"), profileId: "f1", database: "main", status: "fresh", version: 1 } } });
});

describe("isFileProfile", () => {
  it("only a granted-file location", () => {
    expect(isFileProfile(profile({}))).toBe(true);
    expect(isFileProfile(profile({ location: "memory" }))).toBe(false);
    expect(isFileProfile(profile({ location: null }))).toBe(false);
    expect(isFileProfile(undefined)).toBe(false);
  });
});

describe("revokeFileAccess", () => {
  it("does nothing for a profile that is not a database file", async () => {
    expect(await revokeFileAccess("mem")).toBe(false);
    expect(await revokeFileAccess("ms")).toBe(false);
    expect(useUi.getState().dialogs).toEqual([]);
    expect(h.rpc).not.toHaveBeenCalled();
  });

  it("asks first; Cancel revokes nothing", async () => {
    const run = revokeFileAccess("f1");
    const d = await confirm(false);
    expect(await run).toBe(false);
    expect(d.title).toBe("Revoke access to “app.db”?");
    expect(calls("grant.revoke")).toEqual([]);
  });

  it("confirmed: grant.revoke, the tabs lose their sessions, the server needs a file and its schema goes", async () => {
    tab({ id: "a", sessionId: "s1" });
    tab({ id: "b" });
    const run = revokeFileAccess("f1");
    const d = await confirm(true);
    expect(await run).toBe(true);
    expect(d.body).toContain("Closes 1 tab session on this file");
    expect(calls("grant.revoke")).toEqual([["grant.revoke", { profileId: "f1" }]]);
    expect(useTabs.getState().tabs.a).toMatchObject({ sessionId: undefined, txState: "none" });
    expect(useConnections.getState().servers.f1).toMatchObject({ status: "error", repick: true, error: REVOKED_NOTE, sessionId: undefined, databases: [] });
    expect(useSnapshots.getState().entries).toEqual({});
    expect(toasts()).toContain("success: Access to app.db revoked.");
  });

  it("grantBusy: the sidecar's message is shown and nothing changes", async () => {
    const busy = "A tab on this file has an open transaction or a running query. Commit or roll back (or cancel the query) and close the tab first.";
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "grant.revoke") throw new h.RpcError(busy, -32001, "grantBusy");
      return undefined;
    });
    tab({ id: "a", sessionId: "s1", txState: "open" });
    const run = revokeFileAccess("f1");
    await confirm(true);
    expect(await run).toBe(false);
    expect(toasts()).toContain(`warn: ${busy}`);
    expect(useTabs.getState().tabs.a.sessionId).toBe("s1");
    expect(useConnections.getState().servers.f1).toMatchObject({ status: "connected", sessionId: "ex1" });
    expect(useSnapshots.getState().entries[snapKey("f1", "main")]).toBeTruthy();
  });
});

describe("repickFile", () => {
  it("a running query or an open transaction on the file stops it before the dialog", async () => {
    tab({ id: "a", sessionId: "s1", busy: true });
    expect(await repickFile("f1")).toBe(false);
    useTabs.getState().update("a", { busy: false, txState: "open" });
    expect(await repickFile("f1")).toBe(false);
    expect(h.pick).not.toHaveBeenCalled();
    expect(toasts().join("\n")).toMatch(/query is running[\s\S]*open transaction/);
  });

  it("a cancelled dialog changes nothing", async () => {
    h.pick.mockResolvedValue(null);
    expect(await repickFile("f1")).toBe(false);
    expect(h.rpc).not.toHaveBeenCalled();
  });

  it("picks, closes the old file's sessions, saves the new grant (read-only) and reconnects with a full read", async () => {
    h.pick.mockResolvedValue({ grantId: "g-new", name: "copy.db", size: 0 });
    tab({ id: "a", sessionId: "s1" });
    expect(await repickFile("f1")).toBe(true);
    const order = h.rpc.mock.calls.map((c) => c[0]);
    expect(order.indexOf("grant.revoke")).toBeLessThan(order.indexOf("profile.save"));
    expect(calls("profile.save")[0][1]).toMatchObject({ id: "f1", location: "file", file: { grantId: "g-new", displayName: "copy.db" } });
    expect(useProfiles.getState().byId("f1")?.file?.grantId).toBe("g-new");
    expect(useTabs.getState().tabs.a.sessionId).toBeUndefined();
    expect(useConnections.getState().servers.f1).toMatchObject({ status: "connected", sessionId: "ex2", repick: false });
    expect(calls("meta.refresh")).toEqual([["meta.refresh", { profileId: "f1", database: "main", full: true }]]);
    expect(toasts().join("\n")).toMatch(/now uses copy\.db \(read-only/);
  });

  it("a refused revoke (grantBusy) keeps the old file and saves nothing", async () => {
    h.pick.mockResolvedValue({ grantId: "g-new", name: "copy.db", size: 0 });
    h.rpc.mockImplementation(async (m: string) => {
      if (m === "grant.revoke") throw new h.RpcError("A tab on this file has an open transaction.", -32001, "grantBusy");
      return undefined;
    });
    expect(await repickFile("f1")).toBe(false);
    expect(calls("profile.save")).toEqual([]);
    expect(useProfiles.getState().byId("f1")?.file?.grantId).toBe("g-old");
    expect(toasts()).toContain("warn: A tab on this file has an open transaction.");
  });

  it("a server that needs a file (after a revoke) comes back once the file is picked again", async () => {
    useConnections.getState().markNeedsFile("f1", REVOKED_NOTE);
    h.pick.mockResolvedValue({ grantId: "g-new", name: "app.db", size: 0 });
    expect(await repickFile("f1")).toBe(true);
    expect(useConnections.getState().servers.f1).toMatchObject({ status: "connected", repick: false });
  });
});

// Tools on a session-owned engine's database (Phase D0d): the ER diagram, schema / data compare and the import wizard read
// the view of the query tab session they were opened from — an in-memory SQLite / DuckDB database exists only there —
// while server engines (SQL Server, PostgreSQL, MySQL) keep reading the one shared snapshot and the explorer's session.
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Profile } from "../../ipc/contract";

const { rpc } = vi.hoisted(() => ({ rpc: vi.fn() }));
vi.mock("../../ipc/client", () => ({ rpc, onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));

import { heldSession, ownSessionFor, snapKey, toolScope, toolSession, useSnapshots } from "../snapshots";
import { MEMORY_NOTE, useConnections } from "../connections";
import { useProfiles } from "../profiles";
import { useTabs, type TabState } from "../tabs";

const profile = (o: Partial<Profile>): Profile =>
  ({ id: "x", name: "x", engine: "sqlite", auth: "none", environment: "dev", tlsMode: "default", trustCert: false, createdAt: 0, updatedAt: 0, ...o }) as Profile;
const PROFILES = [
  profile({ id: "mem", location: "memory", databaseDefault: "main" }),
  profile({ id: "file", location: "file", file: { grantId: "g1", displayName: "app.db" }, databaseDefault: "main" }),
  profile({ id: "duck", engine: "duckDb", location: "memory", databaseDefault: "memory" }),
  profile({ id: "ms", engine: "mssql", auth: "sql", host: "h", databaseDefault: "Sales" }),
  profile({ id: "my", engine: "mySql", auth: "sql", host: "h", databaseDefault: "shop" }),
];
const tab = (o: Partial<TabState>) => useTabs.getState().add({ id: o.id ?? "t1", title: "q.sql", ...o });
const realSessionFor = useConnections.getState().sessionFor;

beforeEach(() => {
  rpc.mockReset();
  useSnapshots.setState({ entries: {} });
  useTabs.setState({ tabs: {}, order: [], activeId: undefined, mru: [] });
  useProfiles.setState({ profiles: PROFILES, groups: [] });
  useConnections.setState({ servers: {}, order: [], disconnected: {}, sessionFor: realSessionFor });
});

describe("which query tab session a tool opens on", () => {
  it("a session-owned engine's database: the active tab's session first, else another tab's on the same database", () => {
    tab({ id: "a", profileId: "mem", database: "main", sessionId: "sA" });
    tab({ id: "b", profileId: "mem", database: "main", sessionId: "sB" });
    useTabs.setState({ activeId: "b" });
    expect(ownSessionFor("mem", "main")).toBe("sB");
    useTabs.setState({ activeId: undefined }); // a tool tab is active
    expect(ownSessionFor("mem", "main")).toBe("sA");
    tab({ id: "d", profileId: "duck", database: "memory", sessionId: "sD" });
    expect(ownSessionFor("duck", "memory")).toBe("sD");
  });

  it("none for a tab without a session, another database or profile, and never for a server engine", () => {
    tab({ id: "a", profileId: "mem", database: "main" });
    tab({ id: "b", profileId: "file", database: "main", sessionId: "sF" });
    tab({ id: "m", profileId: "ms", database: "Sales", sessionId: "sM" });
    tab({ id: "y", profileId: "my", database: "shop", sessionId: "sY" });
    expect(ownSessionFor("mem", "main")).toBeUndefined();
    expect(ownSessionFor("file", "other")).toBeUndefined();
    expect(ownSessionFor("ms", "Sales")).toBeUndefined();
    expect(ownSessionFor("my", "shop")).toBeUndefined();
    expect(ownSessionFor(undefined, "main")).toBeUndefined();
  });
});

describe("what a tool reads", () => {
  it("an in-memory SQLite or DuckDB database: its tab session's view while the tab holds the session", () => {
    tab({ id: "a", profileId: "mem", database: "main", sessionId: "sA" });
    tab({ id: "d", profileId: "duck", database: "memory", sessionId: "sD" });
    expect(toolScope({ profileId: "mem", database: "main", sessionId: "sA" })).toEqual({ profileId: "mem", database: "main", sessionId: "sA" });
    expect(toolScope({ profileId: "duck", database: "memory", sessionId: "sD" })).toEqual({ profileId: "duck", database: "memory", sessionId: "sD" });
    expect(heldSession({ profileId: "mem", database: "main", sessionId: "sA" })).toBe("sA");
  });

  it("once the session is gone: nothing for an in-memory database, the shared snapshot for a file", () => {
    tab({ id: "a", profileId: "mem", database: "main", sessionId: "sA" });
    useTabs.getState().update("a", { sessionId: undefined }); // lost in an engine restart, or the tab switched away
    expect(toolScope({ profileId: "mem", database: "main", sessionId: "sA" })).toBeUndefined();
    expect(heldSession({ profileId: "mem", database: "main", sessionId: "sA" })).toBeUndefined();
    expect(toolScope({ profileId: "file", database: "main", sessionId: "gone" })).toEqual({ profileId: "file", database: "main" });
    // A session another tab now holds on a different database is not this binding's.
    tab({ id: "b", profileId: "mem", database: "other", sessionId: "sA" });
    expect(heldSession({ profileId: "mem", database: "main", sessionId: "sA" })).toBeUndefined();
  });

  it("regression: server engines read the shared snapshot even when a tab session is named", () => {
    tab({ id: "m", profileId: "ms", database: "Sales", sessionId: "sM" });
    tab({ id: "y", profileId: "my", database: "shop", sessionId: "sY" });
    expect(toolScope({ profileId: "ms", database: "Sales", sessionId: "sM" })).toEqual({ profileId: "ms", database: "Sales" });
    expect(toolScope({ profileId: "my", database: "shop" })).toEqual({ profileId: "my", database: "shop" });
    expect(snapKey("my", "shop")).toBe("my|shop");
  });
});

describe("the session a tool's requests go through", () => {
  it("the held tab session, without connecting the explorer", async () => {
    const sessionFor = vi.fn();
    useConnections.setState({ sessionFor } as never);
    tab({ id: "a", profileId: "mem", database: "main", sessionId: "sA" });
    await expect(toolSession({ profileId: "mem", database: "main", sessionId: "sA" })).resolves.toBe("sA");
    expect(sessionFor).not.toHaveBeenCalled();
  });

  it("regression: a server engine goes through the explorer's session", async () => {
    const sessionFor = vi.fn().mockResolvedValue("explorer");
    useConnections.setState({ sessionFor } as never);
    tab({ id: "m", profileId: "ms", database: "Sales", sessionId: "sM" });
    await expect(toolSession({ profileId: "ms", database: "Sales", sessionId: "sM" })).resolves.toBe("explorer");
    expect(sessionFor).toHaveBeenCalledWith("ms");
  });

  it("an in-memory database whose tab session is gone is refused with the reason, never a new empty database", async () => {
    // The real sessionFor: an in-memory profile has no explorer session to fall back to.
    await expect(toolSession({ profileId: "mem", database: "main", sessionId: "gone" })).rejects.toThrow(MEMORY_NOTE);
    expect(rpc).not.toHaveBeenCalledWith("session.open", expect.anything());
  });
});

// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Engine, SnapObject } from "../../ipc/contract";

const m = vi.hoisted(() => ({
  scriptObject: vi.fn(async (..._a: unknown[]) => {}),
  openDefinition: vi.fn(async (..._a: unknown[]) => {}),
  newQueryTab: vi.fn(),
  disconnectServer: vi.fn(async (..._a: unknown[]) => {}),
  changeConnection: vi.fn(async (..._a: unknown[]) => {}),
  changeDatabase: vi.fn(async (..._a: unknown[]) => {}),
  startDebug: vi.fn(),
  openErdForTable: vi.fn(),
  openErdForDatabase: vi.fn(),
  openDataCompare: vi.fn(),
  openSchemaCompare: vi.fn(),
  openActivityMonitor: vi.fn(),
  findUsagesOf: vi.fn(),
  renameOf: vi.fn(),
  connect: vi.fn(async () => true),
  repickFile: vi.fn(async (..._a: unknown[]) => true),
  revokeFileAccess: vi.fn(async (..._a: unknown[]) => true),
  reuse: undefined as string | undefined,
}));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => []), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../../app/actions", () => ({ scriptObject: m.scriptObject, openDefinition: m.openDefinition, newQueryTab: m.newQueryTab, disconnectServer: m.disconnectServer, changeConnection: m.changeConnection, changeDatabase: m.changeDatabase, activateTab: vi.fn() }));
vi.mock("../../app/tabActions", () => ({ tabForNewConnection: () => m.reuse }));
vi.mock("../../debug/startDebug", () => ({ startDebug: m.startDebug }));
vi.mock("../../erd/erdActions", () => ({ openErdForTable: m.openErdForTable, openErdForDatabase: m.openErdForDatabase }));
vi.mock("../../compare/compareActions", () => ({ openDataCompare: m.openDataCompare, openSchemaCompare: m.openSchemaCompare }));
vi.mock("../../monitor/monitorActions", () => ({ openActivityMonitor: m.openActivityMonitor }));
vi.mock("../../refactor/refactorActions", () => ({ findUsagesOf: m.findUsagesOf, renameOf: m.renameOf }));
vi.mock("../../editor/editors", () => ({ editorForTab: () => undefined }));
vi.mock("../../connections/fileAccess", () => ({
  isFileProfile: (p?: { location?: string | null }) => p?.location === "file",
  repickFile: m.repickFile,
  revokeFileAccess: m.revokeFileAccess,
}));

import type { MenuEntry } from "../../app/ToolbarMenu";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useUi } from "../../store/ui";
import { useTabs } from "../../store/tabs";
import { catMenu, dbMenu, leafMenu, menuFor, objectMenu, PACKAGE_REFACTOR, serverMenu } from "../menus";
import type { RowOf } from "../tree";

type Item = Exclude<MenuEntry, "sep">;
const items = (menu: MenuEntry[]) => menu.filter((x): x is Item => x !== "sep");
const labels = (menu: MenuEntry[]) => items(menu).map((x) => x.label);
const find = (menu: MenuEntry[], label: string) => items(menu).find((x) => x.label === label)!;
const sub = (menu: MenuEntry[], label: string) => labels(find(menu, label).sub!);
const base = { level: 1, parent: -1, expandable: false, expanded: false };
const obj = (kind: SnapObject["kind"], over: Partial<SnapObject> = {}): SnapObject => ({ id: 7, schema: "dbo", name: "Orders", kind, columns: kind === "table" || kind === "view" ? [{ name: "Id", type: "int", nullable: false, pk: true, identity: false, computed: false, ordinal: 1 }] : undefined, ...over });
const objRow = (o: SnapObject): RowOf<"obj"> => ({ ...base, t: "obj", key: "o:p:AppDb:x", profileId: "p", db: "AppDb", obj: o, bare: true, leaf: false });
const server = (status: RowOf<"server">["status"], error?: string): RowOf<"server"> => ({ ...base, t: "server", key: "s:p", profileId: "p", status, error, pinned: false });

beforeEach(() => {
  Object.values(m).forEach((f) => typeof f === "function" && f.mockClear());
  m.reuse = undefined;
  useConnections.setState({ servers: { p: { profileId: "p", status: "connected", sessionId: "s", databases: [] } }, order: ["p"], disconnected: {}, connect: m.connect } as never);
  useSettings.setState({ pinnedProfiles: [], explorerSchemaGroups: [], explorerShowAll: false });
  useUi.setState({ dialogs: [] });
  useTabs.setState({ tabs: {}, order: [], activeId: undefined } as never);
});

describe.each<Engine>(["mssql", "pg"])("object menus (%s)", (engine) => {
  beforeEach(() => useProfiles.setState({ profiles: [{ id: "p", name: "Srv", engine, environment: "prod" } as never] }));

  it("table: the default action first and bold, Script as ▸ with every variant, every old item still there", () => {
    const menu = objectMenu(objRow(obj("table")));
    expect(items(menu)[0]).toMatchObject({ label: "Select top 1000", bold: true, detail: "runs", hint: "Double-click" });
    expect(labels(menu)).toEqual(["Select top 1000", "Count rows", "Script as", "Insert name", "Insert column list", "Copy name", "Find usages", "Rename…", "Dependencies", "View ER diagram", "Compare data…", "Import data into this table…", "Refresh “AppDb”"]);
    expect(find(menu, "Script as")).toMatchObject({ detail: "new tab, nothing runs" });
    expect(sub(menu, "Script as")).toEqual(["CREATE", "DROP and CREATE", "DROP", "INSERT", "UPDATE", "DELETE"]); // no ALTER for a table
    expect(find(menu, "Find usages")).toMatchObject({ hint: "Shift+F12" });
  });

  it("view: Select top 1000, Modify, ALTER in Script as, column list, no ER diagram", () => {
    const menu = objectMenu(objRow(obj("view")));
    expect(labels(menu).slice(0, 2)).toEqual(["Select top 1000", "Modify"]);
    expect(sub(menu, "Script as")).toContain("ALTER");
    expect(labels(menu)).not.toContain("View ER diagram");
    expect(labels(menu)).toContain("Compare data…");
  });

  it("procedure: Modify is the default; Debug only on PostgreSQL, otherwise shown with the reason", () => {
    const menu = objectMenu(objRow(obj("proc", { params: [] })));
    expect(items(menu)[0]).toMatchObject({ label: "Modify", bold: true });
    expect(find(menu, "Execute…")).toBeTruthy();
    expect(find(menu, "Debug…").unavailable).toBe(engine === "pg" ? undefined : "PostgreSQL only");
    expect(sub(menu, "Script as").at(-1)).toBe(engine === "pg" ? "CALL" : "EXECUTE");
    find(menu, "Debug…").run();
    expect(m.startDebug).toHaveBeenCalledTimes(1);
  });

  it("synonyms keep Find usages and Rename, unavailable with the reason (they were hidden before)", () => {
    const menu = objectMenu(objRow(obj("synonym")));
    expect(find(menu, "Find usages").unavailable).toBe("Not for synonyms");
    expect(find(menu, "Rename…").unavailable).toBe("Not for synonyms");
  });

  it("trigger: Modify (the trigger function on PostgreSQL), ENABLE / DISABLE scripts, refused for a PostgreSQL trigger on a view", () => {
    const t = obj("trigger", { name: "tr", parentObjectId: 7 });
    const menu = objectMenu(objRow(t), { parent: obj("view") });
    expect(items(menu)[0].label).toBe(engine === "pg" ? "Modify trigger function" : "Modify");
    const s = find(menu, "Script as").sub!;
    expect(labels(s)).toEqual(["CREATE", "ALTER", "DROP and CREATE", "DROP", "ENABLE", "DISABLE"]);
    expect(items(s).find((x) => x.label === "ENABLE")!.unavailable).toBe(engine === "pg" ? "ALTER TABLE does not accept views" : undefined);
    expect(labels(objectMenu(objRow(t), { parent: obj("table") })).includes("Find usages")).toBe(false);
  });

  it("Script as items script into a new tab and never run (only Select top 1000 runs)", () => {
    const menu = objectMenu(objRow(obj("table")));
    items(find(menu, "Script as").sub!).find((x) => x.label === "DROP")!.run();
    expect(m.scriptObject).toHaveBeenLastCalledWith("p", "AppDb", { schema: "dbo", name: "Orders", kind: "table", objectId: 7 }, "drop");
    items(menu)[0].run();
    expect(m.scriptObject).toHaveBeenLastCalledWith("p", "AppDb", expect.anything(), "selectTop", true);
  });

  it("session actions wait for a reconnecting server instead of asking for a password; copy and insert still work", () => {
    useConnections.setState({ servers: { p: { profileId: "p", status: "connecting", databases: [] } } } as never);
    const menu = objectMenu(objRow(obj("table")));
    expect(find(menu, "Select top 1000").unavailable).toBe("Waiting for Srv to reconnect");
    expect(find(menu, "Script as").unavailable).toBeTruthy();
    expect(find(menu, "Copy name").unavailable).toBeUndefined();
    useConnections.setState({ servers: { p: { profileId: "p", status: "error", databases: [] } } } as never);
    expect(find(objectMenu(objRow(obj("table"))), "Select top 1000").unavailable).toBe("Reconnect first");
  });

  it("a search result can be revealed in the tree", () => {
    const reveal = vi.fn();
    const menu = objectMenu(objRow(obj("table")), { reveal });
    find(menu, "Reveal in tree").run();
    expect(reveal).toHaveBeenCalled();
  });

  it("column leaves: insert, copy, find usages and rename of the column", () => {
    const leaf: RowOf<"leaf"> = { ...base, t: "leaf", key: "l", text: "Id", icon: "key", tone: "warn", drag: "Id", obj: obj("table"), profileId: "p", db: "AppDb", column: "Id" };
    const menu = leafMenu(leaf);
    expect(labels(menu)).toEqual(["Insert name", "Copy name", "Find usages", "Rename…"]);
    find(menu, "Find usages").run();
    expect(m.findUsagesOf).toHaveBeenCalledWith("p", "AppDb", { schema: "dbo", name: "Orders", kind: "table", column: "Id" });
  });
});

describe.each<[Engine, string]>([["sqlite", "SQLite"], ["mySql", "MySQL"]])("object menus (%s)", (engine, name) => {
  beforeEach(() => useProfiles.setState({ profiles: [{ id: "p", name: "Srv", engine, environment: "dev" } as never] }));

  it("a trigger cannot be enabled or disabled: the engine has no such statement", () => {
    const menu = objectMenu(objRow(obj("trigger", { name: "tr", parentObjectId: 7 })), { parent: obj("table") });
    expect(items(menu)[0].label).toBe("Modify");
    const s = find(menu, "Script as").sub!;
    for (const l of ["ENABLE", "DISABLE"]) expect(items(s).find((x) => x.label === l)!.unavailable).toBe(`${name} cannot disable a trigger; drop and create it instead`);
  });

  it("a routine: Execute… only where procedures exist (MySQL calls it with CALL / SELECT); never a debugger", () => {
    const menu = objectMenu(objRow(obj("proc", { params: [] })));
    expect(find(menu, "Debug…").unavailable).toBe("PostgreSQL only");
    if (engine === "mySql") {
      expect(find(menu, "Execute…").unavailable).toBeUndefined();
      expect(sub(menu, "Script as").at(-1)).toBe("CALL");
      expect(sub(objectMenu(objRow(obj("func", { params: [] }))), "Script as").at(-1)).toBe("SELECT");
    } else {
      expect(find(menu, "Execute…").unavailable).toBe("SQLite has no stored procedures");
      expect(sub(menu, "Script as")).not.toContain("EXECUTE");
    }
  });

  it("the server has no Activity monitor, said with the reason", () => {
    expect(find(serverMenu(server("connected")), "Activity monitor").unavailable).toBe(`${name} has no activity monitor`);
  });
});

describe("Oracle package menus (D4 S08)", () => {
  beforeEach(() => useProfiles.setState({ profiles: [{ id: "p", name: "Srv", engine: "oracle", environment: "dev" } as never] }));
  const spec = obj("package", { schema: "APP", name: "PKG", members: ["P(A IN NUMBER)"] });
  const body = obj("packageBody", { id: 8, schema: "APP", name: "PKG", specObjectId: 7 });

  it("a specification: Modify first; Script as the specification, with its body, ALTER … COMPILE and the drops", () => {
    const menu = objectMenu(objRow(spec));
    expect(items(menu)[0]).toMatchObject({ label: "Modify", bold: true, hint: "Double-click" });
    expect(labels(menu)).not.toContain("Execute…");
    expect(labels(menu)).not.toContain("Debug…");
    expect(sub(menu, "Script as")).toEqual(["CREATE", "CREATE with body", "ALTER … COMPILE", "DROP and CREATE", "DROP"]);
    const s = find(menu, "Script as").sub!;
    find(s, "CREATE with body").run();
    expect(m.scriptObject).toHaveBeenLastCalledWith("p", "AppDb", { schema: "APP", name: "PKG", kind: "package", objectId: 7 }, "create", false, true);
    find(s, "CREATE").run();
    expect(m.scriptObject).toHaveBeenLastCalledWith("p", "AppDb", { schema: "APP", name: "PKG", kind: "package", objectId: 7 }, "create");
    find(menu, "Modify").run();
    expect(m.openDefinition).toHaveBeenLastCalledWith("p", "AppDb", { schema: "APP", name: "PKG", kind: "package", objectId: 7 }, true);
  });

  it("a body: Modify opens the body itself, and there is no CREATE with body", () => {
    const menu = objectMenu(objRow(body));
    expect(items(menu)[0]).toMatchObject({ label: "Modify", bold: true });
    expect(sub(menu, "Script as")).toEqual(["CREATE", "ALTER … COMPILE", "DROP and CREATE", "DROP"]);
    find(menu, "Modify").run();
    expect(m.openDefinition).toHaveBeenLastCalledWith("p", "AppDb", { schema: "APP", name: "PKG", kind: "packageBody", objectId: 8 }, true);
  });

  it("find usages and rename stay in the menu, unavailable with the reason, for both parts", () => {
    for (const o of [spec, body]) {
      const menu = objectMenu(objRow(o));
      expect(find(menu, "Find usages").unavailable).toBe(PACKAGE_REFACTOR);
      expect(find(menu, "Rename…").unavailable).toBe(PACKAGE_REFACTOR);
    }
  });
});

describe("server, database, folder and saved menus", () => {
  beforeEach(() => useProfiles.setState({ profiles: [{ id: "p", name: "Srv", engine: "pg", environment: "staging" } as never] }));

  it("connected: new query, refresh, activity monitor, edit, pin, and a neutral Disconnect that says what it closes", () => {
    useTabs.setState({ tabs: { t1: { id: "t1", profileId: "p", sessionId: "x" } }, order: ["t1"] } as never);
    const menu = serverMenu(server("connected"));
    expect(labels(menu)).toEqual(["New query", "Refresh databases", "Activity monitor", "Edit profile…", "Duplicate profile…", "Pin to top", "Disconnect"]);
    expect(find(menu, "Disconnect")).toMatchObject({ detail: "closes 1 tab session" });
    expect(find(menu, "Disconnect").unavailable).toBeUndefined();
    find(menu, "Disconnect").run();
    expect(m.disconnectServer).toHaveBeenCalledWith("p");
  });

  it("Duplicate profile… opens the profile editor on a copy (the copy never keeps a file grant, see duplicateProfile)", () => {
    find(serverMenu(server("connected")), "Duplicate profile…").run();
    expect(useUi.getState().dialogs.at(-1)).toMatchObject({ kind: "profile", copyOf: "p" });
  });

  it("an in-memory server lists no databases: Refresh databases says why", () => {
    const row = { ...server("connected"), memory: true };
    expect(find(serverMenu(row), "Refresh databases").unavailable).toBe("In-memory: each tab has its own database");
  });

  it("failed: Reconnect first and bold, Copy error, server tools unavailable with the reason", () => {
    const menu = serverMenu(server("error", "timed out"));
    expect(items(menu)[0]).toMatchObject({ label: "Reconnect", bold: true, hint: "Enter" });
    for (const l of ["New query", "Refresh databases", "Activity monitor"]) expect(find(menu, l).unavailable).toBe("Not connected");
    expect(find(menu, "Disconnect").detail).toBe("stops retrying");
  });

  it("not connected (pinned): Connect first; New query connects first; pinning is a checkbox shared with the Connect dialog", () => {
    useSettings.setState({ pinnedProfiles: ["p"] });
    const menu = serverMenu(server("off"));
    expect(items(menu)[0]).toMatchObject({ label: "Connect", bold: true });
    expect(find(menu, "New query").detail).toBe("connects first");
    expect(find(menu, "Pin to top").checked).toBe(true);
    find(menu, "Pin to top").run();
    expect(useSettings.getState().pinnedProfiles).toEqual([]);
    find(menu, "Connect").run();
    expect(m.connect).toHaveBeenCalledWith("p");
  });

  it("New query on a server that is not connected opens one query tab: the unconnected tab it reuses, or a new one", async () => {
    m.reuse = "t1";
    find(serverMenu(server("off")), "New query").run();
    await vi.waitFor(() => expect(m.changeConnection).toHaveBeenCalledWith("t1", "p"));
    expect(m.newQueryTab).not.toHaveBeenCalled();
    m.reuse = undefined;
    find(serverMenu(server("off")), "New query").run();
    await vi.waitFor(() => expect(m.newQueryTab).toHaveBeenCalledTimes(1));
    expect(m.newQueryTab).toHaveBeenCalledWith({ profileId: "p" });
  });

  it("while the server reconnects, every session-bound object and column action says it is waiting", () => {
    useConnections.setState({ servers: { p: { profileId: "p", status: "connecting", databases: [] } } } as never);
    const menu = objectMenu(objRow(obj("table")));
    for (const l of ["Select top 1000", "Count rows", "Script as", "Find usages", "Rename…", "Dependencies", "View ER diagram", "Compare data…", "Import data into this table…"])
      expect(find(menu, l).unavailable).toBe("Waiting for Srv to reconnect");
    for (const l of ["Insert name", "Copy name"]) expect(find(menu, l).unavailable).toBeUndefined();
    const leaf = leafMenu({ ...base, t: "leaf", key: "k", text: "Id", icon: "column", tone: "muted", obj: obj("table"), profileId: "p", db: "AppDb", column: "Id" } as never);
    expect(find(leaf, "Find usages").unavailable).toBe("Waiting for Srv to reconnect");
    expect(find(leaf, "Rename…").unavailable).toBe("Waiting for Srv to reconnect");
  });

  it("database: Use in active tab is the default; every old item; Group by schema is a checkbox; an offline database says why", () => {
    const row: RowOf<"db"> = { ...base, t: "db", key: "db:p:AppDb", profileId: "p", db: "AppDb", active: false, state: "ok" };
    const menu = dbMenu(row);
    expect(labels(menu)).toEqual(["Use in active tab", "New query", "Refresh metadata", "Full metadata reload", "View ER diagram", "Schema compare…", "Data compare…", "Import data…", "Copy name", "Group by schema"]);
    find(menu, "Group by schema").run();
    expect(useSettings.getState().explorerSchemaGroups).toEqual(["p|AppDb"]);
    const off = dbMenu({ ...row, state: "offline", dbState: "RESTORING" });
    expect(find(off, "Refresh metadata").unavailable).toBe("Database is restoring");
    expect(find(off, "Copy name").unavailable).toBeUndefined();
  });

  it("category: show only this kind, clear its filter, refresh the database", () => {
    const row: RowOf<"cat"> = { ...base, t: "cat", key: "c:p:AppDb:tables", profileId: "p", db: "AppDb", cat: "tables", count: 3, filter: "ord" };
    expect(labels(catMenu(row))).toEqual(["Show only tables", "Clear filter", "Refresh “AppDb”"]);
  });

  it("notes and headers have no menu", () => {
    expect(menuFor({ ...base, t: "note", key: "n", text: "x", tone: "muted" })).toBeNull();
    expect(menuFor({ ...base, t: "header", key: "h", text: "TABLES" })).toBeNull();
  });
});

describe("database files and namespace levels (Phase D)", () => {
  const fileProfile = { id: "p", name: "App", engine: "sqlite", environment: "dev", location: "file", file: { grantId: "g", displayName: "app.db" } };

  it.each(["connected", "error", "off"] as const)("a file server (%s) offers Re-pick file… and Revoke file access…", (status) => {
    useProfiles.setState({ profiles: [fileProfile as never] });
    const menu = serverMenu(server(status, status === "error" ? "The database file is gone." : undefined));
    expect(labels(menu)).toEqual(expect.arrayContaining(["Re-pick file…", "Revoke file access…"]));
    expect(labels(menu).indexOf("Re-pick file…")).toBe(labels(menu).indexOf("Duplicate profile…") + 1);
    find(menu, "Re-pick file…").run();
    expect(m.repickFile).toHaveBeenCalledWith("p");
    find(menu, "Revoke file access…").run();
    expect(m.revokeFileAccess).toHaveBeenCalledWith("p");
  });

  it("a file profile with no file picked cannot revoke; server and in-memory profiles have neither item", () => {
    useProfiles.setState({ profiles: [{ ...fileProfile, file: null } as never] });
    expect(find(serverMenu(server("connected")), "Revoke file access…").unavailable).toBe("No file picked");
    for (const p of [{ id: "p", name: "Srv", engine: "pg", environment: "dev" }, { ...fileProfile, location: "memory", file: null }]) {
      useProfiles.setState({ profiles: [p as never] });
      expect(labels(serverMenu(server("connected")))).not.toContain("Re-pick file…");
      expect(labels(serverMenu(server("connected")))).not.toContain("Revoke file access…");
    }
  });

  const dbRow = { ...base, t: "db" as const, key: "d:p:main", profileId: "p", db: "main", active: false, state: "ok" as const };

  it("schema and data compare say why the engine lacks them (the sidecar's capability reason); available engines say nothing", () => {
    const reason = (f: string) => `SQLite has no ${f} in Querithm.`;
    const caps = (available: boolean) => ({ schemaCompare: { available, reason: available ? null : reason("schema compare") }, dataCompare: { available, reason: available ? null : reason("data compare") } });
    useProfiles.setState({ profiles: [fileProfile as never] });
    useConnections.setState({ servers: { p: { profileId: "p", status: "connected", sessionId: "s", databases: [], caps: caps(false) as never } } } as never);
    expect(find(dbMenu(dbRow as never), "Schema compare…").unavailable).toBe(reason("schema compare"));
    expect(find(dbMenu(dbRow as never), "Data compare…").unavailable).toBe(reason("data compare"));
    expect(find(menuFor({ ...base, t: "schema", key: "sc", profileId: "p", db: "main", schema: "main" } as never)!, "Schema compare…").unavailable).toBe(reason("schema compare"));
    expect(find(objectMenu({ ...objRow(obj("table", { schema: "main" })), db: "main" }), "Compare data…").unavailable).toBe(reason("data compare"));
    // The ER diagram and import stay: SQLite has both.
    expect(find(dbMenu(dbRow as never), "View ER diagram").unavailable).toBeUndefined();
    expect(find(dbMenu(dbRow as never), "Import data…").unavailable).toBeUndefined();
    useConnections.setState({ servers: { p: { profileId: "p", status: "connected", sessionId: "s", databases: [], caps: caps(true) as never } } } as never);
    expect(find(dbMenu(dbRow as never), "Schema compare…").unavailable).toBeUndefined();
    expect(find(objectMenu(objRow(obj("table"))), "Compare data…").unavailable).toBeUndefined();
  });

  it("import from the explorer goes through a query tab session on a session-owned engine's database; none for a server", () => {
    useProfiles.setState({ profiles: [{ ...fileProfile, location: "memory", file: null } as never] });
    useTabs.setState({ tabs: { t: { id: "t", title: "q", profileId: "p", database: "main", sessionId: "s1" } }, order: ["t"], activeId: "t" } as never);
    find(dbMenu(dbRow as never), "Import data…").run();
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "import", profileId: "p", database: "main", sessionId: "s1" });
    useProfiles.setState({ profiles: [{ id: "p", name: "Srv", engine: "pg", environment: "dev" } as never] });
    find(objectMenu({ ...objRow(obj("table")), db: "main" }), "Import data into this table…").run();
    expect(useUi.getState().dialogs.at(-1)).toEqual({ kind: "import", profileId: "p", database: "main", schema: "dbo", table: "Orders" });
  });
  it.each([
    ["schemas", "Group by schema", undefined],
    ["attached", "Group by attached database", undefined],
    ["databases", "Group by schema", "The database is the schema here"],
  ] as const)("Capabilities.namespaces %s names the grouping level", (namespaces, label, unavailable) => {
    useConnections.setState({ servers: { p: { profileId: "p", status: "connected", sessionId: "s", databases: [], caps: { namespaces } as never } } } as never);
    const item = items(dbMenu(dbRow as never)).at(-1)!;
    expect(item).toMatchObject({ label, checked: false });
    expect(item.unavailable).toBe(unavailable);
  });
});

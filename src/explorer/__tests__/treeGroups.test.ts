import { describe, expect, it } from "vitest";
import type { Engine, Profile } from "../../ipc/contract";
import type { ServerConn } from "../../store/connections";
import { ENGINE_LIST } from "../../engines";
import { buildTree, groupEnvKey, groupRowKey, headsOf, orderedServers, type TreeInput, type TreeRow } from "../tree";

// Connection groups in the Object Explorer (§6, owner decisions 2026-11-22): Group › Environment › Connection for
// grouped connections; connections without a group keep exactly today's rows (no "No group" row).

const profile = (id: string, engine: Engine, over: Partial<Profile> = {}): Profile =>
  ({ id, name: `Srv-${id}`, engine, environment: "dev", databaseDefault: "AppDb", groupName: null, color: null, ...over }) as Profile;
const conn = (id: string, over: Partial<ServerConn> = {}): ServerConn => ({ profileId: id, status: "connected", sessionId: "s", databases: [{ name: "AppDb", isSystem: false }], ...over });
function input(over: Partial<TreeInput> = {}): TreeInput {
  return {
    profiles: [], servers: {}, order: [], showAll: true, groupByEnv: false, collapsedEnvs: new Set(), expanded: new Set(),
    entries: {}, details: {}, schemaGroups: [], kind: "all", folderFilters: {}, showGroups: true, collapsedGroups: new Set(), ...over,
  };
}
const labels = (rows: TreeRow[]) =>
  rows.map((r) => `${"  ".repeat(r.level - 1)}${r.t}:${r.t === "server" ? r.profileId : r.t === "env" ? r.env : r.t === "group" ? r.name : r.t === "db" ? r.db : r.t === "serverObjects" ? r.profileId : r.t === "note" ? r.text : r.t === "action" ? r.action : ""}`);
/** What a row is, without where it sits in the list (parents are indexes). */
const shape = (rows: TreeRow[]) => rows.map((r) => ({ t: r.t, key: r.key, level: r.level, expanded: r.expanded, pos: r.pos, size: r.size }));

// Five connections, as in the owner's example: none in a group at first.
const five = [
  profile("scratch", "sqlite", { environment: "local" }),
  profile("ledger", "mySql"),
  profile("reports", "pg"),
  profile("payStage", "pg", { environment: "staging" }),
  profile("orders", "mssql", { environment: "prod" }),
];

describe("connections without a group stay exactly as they are", () => {
  for (const groupByEnv of [false, true]) {
    it(`with no group at all the tree equals today's (group by environment ${groupByEnv ? "on" : "off"})`, () => {
      const on = buildTree(input({ profiles: five, groupByEnv, showGroups: true }));
      const off = buildTree(input({ profiles: five, groupByEnv, showGroups: false }));
      expect(on.rows).toEqual(off.rows);
      expect(on.rows.some((r) => r.t === "group")).toBe(false);
    });

    it(`one grouped connection gets a group row on top; the other four keep today's rows, keys and order (env ${groupByEnv ? "on" : "off"})`, () => {
      const grouped = five.map((p) => (p.id === "ledger" ? { ...p, groupName: "Payments" } : p));
      const rows = buildTree(input({ profiles: grouped, groupByEnv })).rows;
      const today = buildTree(input({ profiles: five.filter((p) => p.id !== "ledger"), groupByEnv, showGroups: false })).rows;
      const head = groupByEnv ? 3 : 2; // group (› env) › server
      expect(labels(rows.slice(0, head))).toEqual(groupByEnv ? ["group:Payments", "  env:dev", "    server:ledger"] : ["group:Payments", "  server:ledger"]);
      // The rest is today's tree without ledger, with one more sibling at the top level.
      expect(shape(rows.slice(head)).map(({ pos: _p, size: _s, ...r }) => r)).toEqual(shape(today).map(({ pos: _p, size: _s, ...r }) => r));
      expect(rows.some((r) => r.t === "group" && /no group/i.test(r.name))).toBe(false);
    });
  }

  it("Show groups off draws grouped connections where they always were", () => {
    const grouped = five.map((p) => (p.id === "ledger" ? { ...p, groupName: "Payments" } : p));
    expect(buildTree(input({ profiles: grouped, showGroups: false })).rows).toEqual(buildTree(input({ profiles: five, showGroups: false })).rows);
  });
});

describe("Group › Environment › Connection", () => {
  const ps = [
    profile("orders", "mssql", { environment: "prod", groupName: "Payments" }),
    profile("billing", "oracle", { environment: "prod", groupName: "Payments" }),
    profile("ledger", "mySql", { groupName: "Payments" }),
    profile("t10", "pg", { groupName: "Team 10" }),
    profile("t2", "pg", { groupName: "team 2", environment: "test" }),
    profile("scratch", "sqlite", { environment: "local" }),
  ];
  const servers = { orders: conn("orders"), billing: conn("billing", { status: "error", error: "ORA-12541" }) };

  it("groups A–Z (numbers by value), Local → Prod inside each, then the ungrouped servers", () => {
    const rows = buildTree(input({ profiles: ps, servers, groupByEnv: true })).rows;
    expect(labels(rows)).toEqual([
      "group:Payments", "  env:dev", "    server:ledger", "  env:prod", "    server:billing", "    server:orders",
      "group:team 2", "  env:test", "    server:t2",
      "group:Team 10", "  env:dev", "    server:t10",
      "env:local", "  server:scratch",
    ]);
    expect(rows[0]).toMatchObject({ t: "group", key: groupRowKey("Payments"), count: 3, prod: 2, failed: 1, expanded: true, pos: 1, size: 4 });
    expect(rows[3]).toMatchObject({ t: "env", key: groupEnvKey("prod", "Payments"), group: "Payments", count: 2, parent: 0 });
    expect(rows[4]).toMatchObject({ t: "server", profileId: "billing", parent: 3 });
    expect(rows.at(-2)).toMatchObject({ t: "env", key: "env:local", parent: -1 });
    expect(new Set(rows.map((r) => r.key)).size).toBe(rows.length);
  });

  it("without environment rows a group lists its servers directly, ordered by environment", () => {
    expect(labels(buildTree(input({ profiles: ps.slice(0, 3) })).rows)).toEqual(["group:Payments", "  server:ledger", "  server:billing", "  server:orders"]);
  });

  it("a collapsed group hides its servers; a collapsed environment inside a group hides only its own", () => {
    const collapsedGroups = new Set([groupRowKey("Payments")]);
    expect(labels(buildTree(input({ profiles: ps.slice(0, 3), groupByEnv: true, collapsedGroups })).rows)).toEqual(["group:Payments"]);
    const inner = new Set([groupEnvKey("prod", "Payments")]);
    expect(labels(buildTree(input({ profiles: ps.slice(0, 3), groupByEnv: true, collapsedGroups: inner })).rows)).toEqual(["group:Payments", "  env:dev", "    server:ledger", "  env:prod"]);
    // An ungrouped Prod env row is a different key: collapsing it leaves the group's Prod row open.
    const rows = buildTree(input({ profiles: [...ps.slice(0, 3), profile("x", "pg", { environment: "prod" })], groupByEnv: true, collapsedEnvs: new Set(["prod"]) })).rows;
    expect(labels(rows)).toEqual(["group:Payments", "  env:dev", "    server:ledger", "  env:prod", "    server:billing", "    server:orders", "env:prod"]);
  });

  it("an expanded server keeps its databases under the group, one level deeper", () => {
    const rows = buildTree(input({ profiles: ps.slice(0, 1), servers, groupByEnv: true, expanded: new Set(["s:orders"]) })).rows;
    expect(labels(rows)).toEqual(["group:Payments", "  env:prod", "    server:orders", "      db:AppDb", "      serverObjects:orders"]);
  });

  it("lists the servers in the order the tree draws them, respecting custom groupOrder and profileOrder", () => {
    expect(orderedServers({ profiles: ps, servers: {}, order: [], showAll: true, showGroups: true }).map((p) => p.id)).toEqual(["ledger", "billing", "orders", "t2", "t10", "scratch"]);
    expect(orderedServers({ profiles: ps, servers: {}, order: [], showAll: true, showGroups: true, groupOrder: ["Team 10", "Payments"], profileOrder: ["orders", "ledger"] }).map((p) => p.id)).toEqual(["t10", "orders", "ledger", "billing", "t2", "scratch"]);
  });
});

describe("headsOf: the collapsed rows that hide a server", () => {
  const p = profile("a", "pg", { groupName: "Payments", environment: "prod" });
  it("grouped: its group row, and the environment row inside it when grouping by environment", () => {
    expect(headsOf(p, { showGroups: true, groupByEnv: true })).toEqual({ groups: [groupRowKey("Payments"), groupEnvKey("prod", "Payments")] });
    expect(headsOf(p, { showGroups: true, groupByEnv: false })).toEqual({ groups: [groupRowKey("Payments")] });
  });
  it("ungrouped (or groups off): today's environment row", () => {
    expect(headsOf({ ...p, groupName: null }, { showGroups: true, groupByEnv: true })).toEqual({ groups: [], env: "prod" });
    expect(headsOf(p, { showGroups: false, groupByEnv: true })).toEqual({ groups: [], env: "prod" });
    expect(headsOf(p, { showGroups: false, groupByEnv: false })).toEqual({ groups: [], env: undefined });
  });
});

describe.each(ENGINE_LIST)("a grouped server keeps its states (%s)", (engine) => {
  it("failed, memory and connecting servers show their notes and actions under the group", () => {
    const ps = [profile("f", engine, { groupName: "G" }), profile("m", engine, { groupName: "G" }), profile("c", engine, { groupName: "G" })];
    const rows = buildTree(input({
      profiles: ps,
      servers: { f: conn("f", { status: "error", error: "refused" }), m: conn("m", { memory: true } as Partial<ServerConn>), c: conn("c", { status: "connecting", databases: [] }) },
      expanded: new Set(["s:f", "s:m", "s:c"]),
    })).rows;
    expect(rows[0]).toMatchObject({ t: "group", failed: 1, count: 3 });
    expect(labels(rows).filter((l) => l.startsWith("    "))).toEqual(expect.arrayContaining(["    action:reconnect", "    note:Connecting…"]));
    expect(rows.filter((r) => r.level === 3 && r.t === "note").length).toBeGreaterThanOrEqual(2);
  });
});

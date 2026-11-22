import { describe, expect, it } from "vitest";
import type { Engine, EnvironmentKind, Profile } from "../../ipc/contract";
import {
  buildSections, byEnvironment, defaultActiveId, ENGINE_TERMS, envCounts, filterProfiles, matchesProfile, matchParts, nameSuffix, prodNameHint,
  profileAddress, rankProfiles, ROW_ACTION, rowState, splitPinned, togglePin,
} from "../profileList";

const ENGINES: Engine[] = ["mssql", "pg"];
const ALL_ENGINES: Engine[] = ["mssql", "pg", "sqlite", "mySql", "duckDb", "oracle"];

function prof(id: string, over: Partial<Profile> = {}): Profile {
  return { id, name: id, engine: "mssql", environment: "dev", host: "localhost", port: null, ...over } as Profile;
}
const ids = (ps: readonly Profile[]) => ps.map((p) => p.id);

describe("connect dialog: search", () => {
  it("matches name, host, host:port, group, user and default database, ignoring case", () => {
    const p = prof("a", { name: "Bench Fixture", host: "db.internal", port: 14333, groupName: "Analytics", username: "reporter", databaseDefault: "Sales" });
    for (const q of ["bench", "FIXTURE", "db.int", "14333", "db.internal:14333", "analytics", "reporter", "sales"]) expect(matchesProfile(p, q)).toBe(true);
    expect(matchesProfile(p, "nope")).toBe(false);
  });
  it("needs every term to match, in any field", () => {
    const p = prof("a", { name: "Acme", host: "203.0.113.10", environment: "prod" });
    expect(matchesProfile(p, "acme prod")).toBe(true);
    expect(matchesProfile(p, "acme staging")).toBe(false);
    expect(matchesProfile(p, "   ")).toBe(true);
  });
  it("matches the environment by id, label and alias (prd, production, stg, qa)", () => {
    expect(matchesProfile(prof("a", { environment: "staging" }), "stage")).toBe(true);
    expect(matchesProfile(prof("a", { environment: "staging" }), "stg")).toBe(true);
    expect(matchesProfile(prof("a", { environment: "prod" }), "production")).toBe(true);
    expect(matchesProfile(prof("a", { environment: "test" }), "qa")).toBe(true);
  });
  it("finds a database file by its file name", () => {
    expect(matchesProfile(prof("f", { engine: "sqlite", location: "file", host: null, file: { grantId: "g", displayName: "scratch.sqlite" } }), "scratch")).toBe(true);
  });
  it.each(ALL_ENGINES)("finds %s profiles by every engine word, and only those", (engine) => {
    const list = ALL_ENGINES.map((e) => prof(e, { engine: e }));
    for (const term of ENGINE_TERMS[engine]) expect(ids(filterProfiles(list, term))).toContain(engine);
    // The full product name is specific to one engine.
    const full = ({ mssql: "sql server", pg: "postgresql", sqlite: "sqlite", mySql: "mariadb", duckDb: "duckdb", oracle: "oracle" } satisfies Record<Engine, string>)[engine];
    expect(ids(filterProfiles(list, full))).toEqual([engine]);
  });
});

describe("connect dialog: search ranking", () => {
  const list = [
    prof("archive", { name: "finance-archive", environment: "prod", databaseDefault: "orders_hist" }),
    prof("replica", { name: "orders-prod-replica", environment: "dev" }),
    prof("ordersDev", { name: "orders-db", environment: "dev" }),
    prof("ordersProd", { name: "orders-db", environment: "prod" }),
    prof("myorders", { name: "my-orders" }),
    prof("exact", { name: "orders" }),
  ];

  it("puts the exact name first, then prefix, word prefix, other fields; ties go to the least risky environment", () => {
    // Three prefix matches: the two DEV ones (A–Z) before the PROD one, whatever their names.
    expect(ids(rankProfiles(filterProfiles(list, "orders"), "orders"))).toEqual(["exact", "ordersDev", "replica", "ordersProd", "myorders", "archive"]);
  });

  it("breaks ties by the most recent connect before the environment", () => {
    expect(ids(rankProfiles(filterProfiles(list, "orders-db"), "orders-db", ["ordersProd"]))).toEqual(["ordersProd", "ordersDev"]);
  });

  it("an environment word ranks that environment first without hiding the rest", () => {
    // "dev" alone would pick devops-db by name; the environment word puts DEV rows above it.
    const l = [prof("devops", { name: "devops-db", environment: "prod" }), prof("d", { name: "billing", environment: "dev" })];
    expect(ids(rankProfiles(filterProfiles(l, "dev"), "dev"))).toEqual(["d", "devops"]);
    expect(ids(rankProfiles(filterProfiles(list, "orders prod"), "orders prod"))).toEqual(["ordersProd", "archive", "replica"]);
  });

  it("a profile named exactly like the query outranks the environment boost", () => {
    const l = [prof("t", { name: "test", environment: "prod" }), prof("q", { name: "qa-box", environment: "test" })];
    expect(ids(rankProfiles(filterProfiles(l, "test"), "test"))).toEqual(["t", "q"]);
  });

  it("searching turns the sections into one 'N matches' list, each profile once", () => {
    const { sections, flat } = buildSections(filterProfiles(list, "orders"), ["ordersProd"], { showGroups: true, query: "orders" });
    expect(sections.map((s) => [s.kind, s.title])).toEqual([["matches", "6 matches"]]);
    expect(new Set(ids(flat)).size).toBe(flat.length);
    expect(buildSections([], [], { query: "x" }).sections).toEqual([]);
  });

  it("cuts text into matched and unmatched runs for underlining", () => {
    expect(matchParts("orders-prod-replica", ["prod", "orders"])).toEqual([{ text: "orders", hit: true }, { text: "-", hit: false }, { text: "prod", hit: true }, { text: "-replica", hit: false }]);
    expect(matchParts("abc", [])).toEqual([{ text: "abc", hit: false }]);
  });

  it("underlines the match itself after a character whose lower case is longer", () => {
    // Regression: "İ" lower-cases to two code units, so the whole-text indices ran one place late ("rod-…").
    expect(matchParts("İstanbul-prod", ["prod"])).toEqual([{ text: "İstanbul-", hit: false }, { text: "prod", hit: true }]);
    expect(matchParts("İstanbul-prod", ["i̇stan"])).toEqual([{ text: "İstan", hit: true }, { text: "bul-prod", hit: false }]);
  });
});

describe("connect dialog: environment filter", () => {
  const envs: EnvironmentKind[] = ["local", "dev", "test", "staging", "prod"];
  const list = envs.flatMap((env) => ENGINES.map((engine) => prof(`${env}-${engine}`, { environment: env, engine })));
  it.each(envs)("keeps only %s profiles of every engine", (env) => {
    expect(ids(filterProfiles(list, "", [env]))).toEqual(ENGINES.map((e) => `${env}-${e}`));
  });
  it("combines several environments with OR, and the search with AND", () => {
    expect(ids(filterProfiles(list, "", ["prod", "dev"]))).toEqual(["dev-mssql", "dev-pg", "prod-mssql", "prod-pg"]);
    expect(ids(filterProfiles(list, "postgres", ["prod"]))).toEqual(["prod-pg"]);
    expect(filterProfiles(list, "")).toHaveLength(list.length);
  });
  it("counts profiles per environment for the chips", () => {
    expect(envCounts([...list, prof("x", { environment: "prod" })])).toEqual({ local: 2, dev: 2, test: 2, staging: 2, prod: 3 });
  });
});

describe("connect dialog: pins and sections", () => {
  it("toggles a pin on and off, appending new pins", () => {
    expect(togglePin(["a"], "b")).toEqual(["a", "b"]);
    expect(togglePin(["a", "b"], "a")).toEqual(["b"]);
  });
  it("puts pinned profiles first in pin order and ignores pins of missing or filtered-out profiles", () => {
    const r = splitPinned([prof("a"), prof("b"), prof("c")], ["c", "gone", "a"]);
    expect(ids(r.pinned)).toEqual(["c", "a"]);
    expect(ids(r.rest)).toEqual(["b"]);
  });
  it("builds Pinned, Production, Staging & test and Development & local; rows Production first, A–Z; flat follows the screen", () => {
    const list = [
      prof("dev1", { environment: "dev", groupName: "G" }),
      prof("loc", { environment: "local", engine: "pg" }),
      prof("prod1", { environment: "prod" }),
      prof("qa", { environment: "test", engine: "pg" }),
      prof("stage", { environment: "staging" }),
      prof("dev2", { environment: "dev", groupName: "G" }),
    ];
    const { sections, flat } = buildSections(list, ["dev2"]);
    expect(sections.map((s) => [s.kind, s.title])).toEqual([["pinned", "Pinned"], ["env", "Production"], ["env", "Staging & test"], ["env", "Development & local"]]);
    expect(ids(sections[2].profiles)).toEqual(["stage", "qa"]);
    expect(ids(flat)).toEqual(["dev2", "prod1", "stage", "qa", "dev1", "loc"]);
  });
  it("has no Pinned section when no pinned profile is shown, and counts Production rows per section", () => {
    expect(buildSections([prof("a")], ["b"]).sections.map((s) => s.key)).toEqual(["dev+local"]);
    expect(buildSections([prof("p", { environment: "prod" }), prof("d")], ["p", "d"]).sections[0].prod).toBe(1);
  });
  it("sorts by environment, Production first, then name", () => {
    expect(ids(byEnvironment([prof("b", { environment: "local" }), prof("z", { environment: "prod" }), prof("a", { environment: "prod" })]))).toEqual(["a", "z", "b"]);
  });
});

// Connection groups (§6, owner decisions 2026-11-22): Show groups puts grouped profiles first, one section per group,
// rows Production first (no bucket captions); profiles without a group keep the environment sections.
describe.each<Engine>(ALL_ENGINES)("connect dialog: groups (%s)", (engine) => {
  const list = [
    prof("orders", { engine, environment: "prod", groupName: "Payments" }),
    prof("ledger", { engine, groupName: "Payments" }),
    prof("payStage", { engine, environment: "staging", groupName: "Payments" }),
    prof("t10", { engine, groupName: "Team 10" }),
    prof("t2", { engine, groupName: "team 2" }),
    prof("scratch", { engine, environment: "local" }),
    prof("billing", { engine, environment: "prod" }),
  ];

  it("groups A–Z first with Production first inside, then the environment sections for the rest", () => {
    const { sections, flat } = buildSections(list, ["t10"], { showGroups: true });
    expect(sections.map((s) => s.title)).toEqual(["Pinned", "Payments", "team 2", "Production", "Development & local"]);
    expect(sections[1]).toMatchObject({ kind: "group", prod: 1 });
    expect(ids(sections[1].profiles)).toEqual(["orders", "payStage", "ledger"]);
    expect(sections.some((s) => /no group/i.test(s.title))).toBe(false);
    expect(ids(flat)).toEqual(["t10", "orders", "payStage", "ledger", "t2", "billing", "scratch"]);
  });

  it("with no grouped profile, or Show groups off, there are only environment sections", () => {
    const plain = list.map((p) => ({ ...p, groupName: null }));
    expect(buildSections(plain, [], { showGroups: true })).toEqual(buildSections(plain, [], { showGroups: false }));
    expect(buildSections(list, [], { showGroups: false }).sections.every((s) => s.kind === "env")).toBe(true);
  });
});

describe("connect dialog: the row it opens on", () => {
  const flat = [prof("prodPin", { environment: "prod" }), prof("devPin"), prof("other"), prof("old", { unsupported: "Saved by a newer version" })];
  it("prefers a just-saved profile, then the most recent listed one, then the first pinned non-Production one", () => {
    expect(defaultActiveId(flat, { focus: "other", recent: ["devPin"] })).toBe("other");
    expect(defaultActiveId(flat, { recent: ["gone", "old", "prodPin"], pinned: ["prodPin", "devPin"] })).toBe("prodPin");
    expect(defaultActiveId(flat, { recent: ["gone"], pinned: ["prodPin", "devPin"] })).toBe("devPin");
  });
  it("never falls back to the first row (Production first by construction); a single usable profile is the default", () => {
    expect(defaultActiveId(flat, { pinned: ["prodPin"] })).toBeUndefined();
    expect(defaultActiveId(flat)).toBeUndefined();
    expect(defaultActiveId([prof("only", { environment: "prod" })])).toBe("only");
    expect(defaultActiveId([prof("old", { unsupported: "x" })])).toBeUndefined();
  });
});

describe("connect dialog: rows", () => {
  it.each(ALL_ENGINES)("shows a %s profile's address: host:port for a server, the file's name, or In memory", (engine) => {
    expect(profileAddress(prof("s", { engine, host: "db-prod-01.example", port: 5432 })).full).toBe("db-prod-01.example:5432");
    expect(profileAddress(prof("s", { engine, host: "2001:db8::14", port: 5432 }))).toEqual({ host: "[2001:db8::14]", port: ":5432", full: "[2001:db8::14]:5432" });
    expect(profileAddress(prof("f", { engine, location: "file", host: null, file: { grantId: "g", displayName: "sales.duckdb" } })).full).toBe("sales.duckdb");
    expect(profileAddress(prof("f", { engine, location: "file", host: null, file: null })).full).toBe("No file picked");
    expect(profileAddress(prof("m", { engine, location: "memory", host: null })).full).toBe("In memory");
  });

  it("names the group outside the group's own section, and the differing user when name, group and address are shared", () => {
    const all = [
      prof("a", { name: "billing-reports", groupName: "Billing", host: "pg-prod-02", port: 5432, username: "report_ro" }),
      prof("b", { name: "billing-reports", groupName: "Billing", host: "pg-prod-02", port: 5432, username: "admin" }),
      prof("c", { name: "orders-db", groupName: "Billing", host: "10.0.0.12" }),
      prof("d", { name: "Orders-DB", host: "10.0.0.13" }),
    ];
    expect(nameSuffix(all[0], all, true)).toBe("report_ro");
    expect(nameSuffix(all[0], all, false)).toBe("Billing · report_ro");
    expect(nameSuffix(all[2], all, true)).toBe("");
    expect(nameSuffix(all[3], all, false)).toBe("No group");
    expect(nameSuffix(prof("solo"), [prof("solo")], false)).toBe("");
  });

  it("flags a non-Production profile whose name or host says prod, as a whole word", () => {
    for (const name of ["orders-prod-replica", "prod01", "ordersProd", "PRD copy", "production"]) expect(prodNameHint(prof("x", { name }))).toBe(true);
    for (const name of ["products-db", "preprod", "pre-prod", "non-prod", "reproduce"]) expect(prodNameHint(prof("x", { name }))).toBe(false);
    expect(prodNameHint(prof("x", { name: "orders", host: "db.prod.example" }))).toBe(true);
    expect(prodNameHint(prof("x", { name: "orders-prod", environment: "prod" }))).toBe(false);
    // A file profile's (absent) host never counts.
    expect(prodNameHint(prof("x", { name: "copy", location: "file", host: "prod" }))).toBe(false);
  });

  it("maps a server's status to the row's state and Enter's verb", () => {
    expect(rowState(prof("a"), undefined)).toBe("idle");
    expect(rowState(prof("a"), { status: "connecting" })).toBe("connecting");
    expect(rowState(prof("a"), { status: "connected" })).toBe("connected");
    expect(rowState(prof("a"), { status: "error" })).toBe("failed");
    expect(rowState(prof("a", { unsupported: "newer" }), { status: "error" })).toBe("unavailable");
    expect(ROW_ACTION).toEqual({ idle: "Connect", failed: "Retry", connected: "New query", connecting: null, unavailable: null });
  });
});

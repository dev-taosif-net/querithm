import { describe, expect, it } from "vitest";
import type { Engine, EnvironmentKind, Profile } from "../../ipc/contract";
import { buildSections, ENGINE_TERMS, envCounts, filterProfiles, matchesProfile, splitPinned, togglePin } from "../profileList";

const ENGINES: Engine[] = ["mssql", "pg"];
const ALL_ENGINES: Engine[] = ["mssql", "pg", "sqlite", "mySql"];

function prof(id: string, over: Partial<Profile> = {}): Profile {
  return { id, name: id, engine: "mssql", environment: "dev", host: "localhost", port: null, ...over } as Profile;
}

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
  it("matches the environment by id and by its label", () => {
    expect(matchesProfile(prof("a", { environment: "staging" }), "stage")).toBe(true);
    expect(matchesProfile(prof("a", { environment: "staging" }), "staging")).toBe(true);
  });
  it.each(ALL_ENGINES)("finds %s profiles by every engine word, and only those", (engine) => {
    const list = ALL_ENGINES.map((e) => prof(e, { engine: e }));
    for (const term of ENGINE_TERMS[engine]) expect(filterProfiles(list, term, "all").map((p) => p.id)).toContain(engine);
    // The full product name is specific to one engine.
    const full = ({ mssql: "sql server", pg: "postgresql", sqlite: "sqlite", mySql: "mariadb", duckDb: "duckdb" } satisfies Record<Engine, string>)[engine];
    expect(filterProfiles(list, full, "all").map((p) => p.id)).toEqual([engine]);
  });
});

describe("connect dialog: environment filter", () => {
  const envs: EnvironmentKind[] = ["local", "dev", "test", "staging", "prod"];
  const list = envs.flatMap((env) => ENGINES.map((engine) => prof(`${env}-${engine}`, { environment: env, engine })));
  it.each(envs)("keeps only %s profiles of every engine", (env) => {
    expect(filterProfiles(list, "", env).map((p) => p.id)).toEqual(ENGINES.map((e) => `${env}-${e}`));
  });
  it("combines with the search text", () => {
    expect(filterProfiles(list, "postgres", "prod").map((p) => p.id)).toEqual(["prod-pg"]);
  });
  it("counts profiles per environment for the chips", () => {
    expect(envCounts([...list, prof("x", { environment: "prod" })])).toEqual({ local: 2, dev: 2, test: 2, staging: 2, prod: 3 });
  });
});

describe("connect dialog: pins", () => {
  it("toggles a pin on and off, appending new pins", () => {
    expect(togglePin(["a"], "b")).toEqual(["a", "b"]);
    expect(togglePin(["a", "b"], "a")).toEqual(["b"]);
  });
  it("puts pinned profiles first in pin order and ignores pins of missing or filtered-out profiles", () => {
    const list = [prof("a"), prof("b"), prof("c")];
    const r = splitPinned(list, ["c", "gone", "a"]);
    expect(r.pinned.map((p) => p.id)).toEqual(["c", "a"]);
    expect(r.rest.map((p) => p.id)).toEqual(["b"]);
  });
  it("builds Pinned, then production, staging/QA and development sections with groups; flat follows the screen", () => {
    const list = [
      prof("dev1", { environment: "dev", groupName: "G" }),
      prof("loc", { environment: "local", engine: "pg" }),
      prof("prod1", { environment: "prod" }),
      prof("qa", { environment: "test", engine: "pg" }),
      prof("dev2", { environment: "dev", groupName: "G" }),
    ];
    const { sections, flat } = buildSections(list, ["dev2"]);
    expect(sections.map((s) => s.title)).toEqual(["Pinned", "Production profiles", "Staging & QA profiles", "Development profiles"]);
    expect(sections[3].groups.map((g) => [g.group, g.profiles.map((p) => p.id)])).toEqual([["G", ["dev1"]], ["", ["loc"]]]);
    expect(flat.map((p) => p.id)).toEqual(["dev2", "prod1", "qa", "dev1", "loc"]);
  });
  it("has no Pinned section when no pinned profile is shown", () => {
    expect(buildSections([prof("a")], ["b"]).sections.map((s) => s.key)).toEqual(["dev+local"]);
  });
});

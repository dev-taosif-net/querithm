// Connect dialog list logic: search, environment filter and pins (kept pure so it is testable without a DOM).
import type { Engine, EnvironmentKind, Profile } from "../ipc/contract";
import { envLabel } from "../store/profiles";

export type EnvFilter = "all" | EnvironmentKind;

/** Filter chips, in environment order (local to production). */
export const ENV_FILTERS: EnvironmentKind[] = ["local", "dev", "test", "staging", "prod"];

/** Words a search can use to find an engine's profiles. A Record, so a new engine must add its own. */
export const ENGINE_TERMS: Record<Engine, string[]> = {
  mssql: ["mssql", "sql server", "sqlserver", "t-sql", "tsql"],
  pg: ["pg", "postgres", "postgresql", "psql"],
  sqlite: ["sqlite", "sqlite3"],
  mySql: ["mysql", "mariadb", "my sql"],
  duckDb: ["duckdb", "duck db", "duck"],
  oracle: ["oracle", "ora", "plsql", "pl/sql"],
};

function haystack(p: Profile): string {
  return [
    p.name, p.host, p.port != null ? String(p.port) : "", p.host && p.port ? `${p.host}:${p.port}` : "",
    p.groupName, p.username, p.databaseDefault, p.environment, envLabel[p.environment], ...ENGINE_TERMS[p.engine],
  ].filter(Boolean).join("\n").toLowerCase();
}

/** Every whitespace-separated term must appear in the name, address, group, user, default database, environment or engine. */
export function matchesProfile(p: Profile, text: string): boolean {
  const terms = text.toLowerCase().split(/\s+/).filter(Boolean);
  if (!terms.length) return true;
  const h = haystack(p);
  return terms.every((t) => h.includes(t));
}

export function filterProfiles(profiles: readonly Profile[], text: string, env: EnvFilter): Profile[] {
  return profiles.filter((p) => (env === "all" || p.environment === env) && matchesProfile(p, text));
}

export function envCounts(profiles: readonly Profile[]): Record<EnvironmentKind, number> {
  const counts = { local: 0, dev: 0, test: 0, staging: 0, prod: 0 };
  for (const p of profiles) counts[p.environment]++;
  return counts;
}

/** Pinned profiles first, in pin order; the rest keep their order. Pins of unknown ids are ignored. */
export function splitPinned(list: readonly Profile[], pinned: readonly string[]): { pinned: Profile[]; rest: Profile[] } {
  const byId = new Map(list.map((p) => [p.id, p]));
  const top = pinned.map((id) => byId.get(id)).filter((p): p is Profile => !!p);
  const set = new Set(top.map((p) => p.id));
  return { pinned: top, rest: list.filter((p) => !set.has(p.id)) };
}

export function togglePin(pinned: readonly string[], id: string): string[] {
  return pinned.includes(id) ? pinned.filter((x) => x !== id) : [...pinned, id];
}

export interface ProfileSection {
  key: string;
  title: string;
  groups: { group: string; profiles: Profile[] }[];
}

const ENV_SECTIONS: { envs: EnvironmentKind[]; title: string }[] = [
  { envs: ["prod"], title: "Production profiles" },
  { envs: ["staging", "test"], title: "Staging & QA profiles" },
  { envs: ["dev", "local"], title: "Development profiles" },
];

/** Pinned first, then by environment (production on top) and group; `flat` is the on-screen order for keyboard moves. */
export function buildSections(list: readonly Profile[], pinned: readonly string[]): { sections: ProfileSection[]; flat: Profile[] } {
  const split = splitPinned(list, pinned);
  const sections: ProfileSection[] = [];
  if (split.pinned.length) sections.push({ key: "pinned", title: "Pinned", groups: [{ group: "", profiles: split.pinned }] });
  for (const { envs, title } of ENV_SECTIONS) {
    const ps = split.rest.filter((p) => envs.includes(p.environment));
    if (!ps.length) continue;
    const byGroup = new Map<string, Profile[]>();
    for (const p of ps) byGroup.set(p.groupName ?? "", [...(byGroup.get(p.groupName ?? "") ?? []), p]);
    sections.push({ key: envs.join("+"), title, groups: [...byGroup].map(([group, profiles]) => ({ group, profiles })) });
  }
  return { sections, flat: sections.flatMap((s) => s.groups.flatMap((g) => g.profiles)) };
}

// Connect dialog list logic (kept pure so it is testable without a DOM): search and its ranking, the environment filter,
// pins, sections, the row the dialog opens on, a row's state and address, and the words that tell look-alikes apart.
import type { Engine, EnvironmentKind, Profile } from "../ipc/contract";
import type { ServerConn } from "../store/connections";
import { envLabel } from "../store/profiles";
import { locationOf } from "./auth";
import { compareGroupNames } from "./groupNames";

/** The environments shown; empty shows every environment. Several combine with OR, and with the search by AND. */
export type EnvFilter = readonly EnvironmentKind[];

/** Production first: the chips, the sections and the order of rows inside a group. */
export const ENV_ORDER: readonly EnvironmentKind[] = ["prod", "staging", "test", "dev", "local"];
/** Filter chips, in environment order (Production first). */
export const ENV_FILTERS = ENV_ORDER;

/** An environment written out, for screen readers and the delete confirm. */
export const ENV_NAMES: Record<EnvironmentKind, string> = { prod: "Production", staging: "Staging", test: "Test", dev: "Development", local: "Local" };

/** Words a search can use for an environment. A query word equal to one ranks that environment first; it never hides rows. */
export const ENV_WORDS: Record<EnvironmentKind, readonly string[]> = {
  prod: ["prod", "production", "prd"],
  staging: ["stage", "staging", "stg"],
  test: ["test", "qa"],
  dev: ["dev", "development"],
  local: ["local"],
};

/** Words a search can use to find an engine's profiles. A Record, so a new engine must add its own. */
export const ENGINE_TERMS: Record<Engine, string[]> = {
  mssql: ["mssql", "sql server", "sqlserver", "t-sql", "tsql"],
  pg: ["pg", "postgres", "postgresql", "psql"],
  sqlite: ["sqlite", "sqlite3"],
  mySql: ["mysql", "mariadb", "my sql"],
  duckDb: ["duckdb", "duck db", "duck"],
  oracle: ["oracle", "ora", "plsql", "pl/sql"],
};

const isEnvWord = (t: string) => ENV_ORDER.some((e) => ENV_WORDS[e].includes(t));

/** The search box's words, lower case. */
export const searchTerms = (text: string): string[] => text.toLowerCase().split(/\s+/).filter(Boolean);

function haystack(p: Profile): string {
  return [
    p.name, p.host, p.port != null ? String(p.port) : "", p.host && p.port ? `${p.host}:${p.port}` : "", p.file?.displayName,
    p.groupName, p.username, p.databaseDefault, p.environment, envLabel[p.environment], ...(ENV_WORDS[p.environment] ?? []), ...ENGINE_TERMS[p.engine],
  ].filter(Boolean).join("\n").toLowerCase();
}

/** Every whitespace-separated term must appear in the name, address, file, group, user, default database, environment or engine. */
export function matchesProfile(p: Profile, text: string): boolean {
  const terms = searchTerms(text);
  if (!terms.length) return true;
  const h = haystack(p);
  return terms.every((t) => h.includes(t));
}

export function filterProfiles(profiles: readonly Profile[], text: string, envs: EnvFilter = []): Profile[] {
  return profiles.filter((p) => (!envs.length || envs.includes(p.environment)) && matchesProfile(p, text));
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

/** Production first, then Staging, Test, Development, Local; A–Z inside each. */
export function byEnvironment(list: readonly Profile[]): Profile[] {
  return [...list].sort((a, b) => ENV_ORDER.indexOf(a.environment) - ENV_ORDER.indexOf(b.environment) || a.name.localeCompare(b.name));
}

/** A row's address: host and port for a server (an IPv6 host in brackets), the file's name, or "In memory". */
export function profileAddress(p: Pick<Profile, "location" | "host" | "port" | "file">): { host: string; port: string; full: string } {
  const where = locationOf(p);
  if (where === "memory") return { host: "In memory", port: "", full: "In memory" };
  if (where === "file") {
    const name = p.file?.displayName || "No file picked";
    return { host: name, port: "", full: name };
  }
  const raw = p.host ?? "";
  const host = raw.includes(":") && !raw.startsWith("[") ? `[${raw}]` : raw;
  const port = p.port ? `:${p.port}` : "";
  return { host, port, full: host + port };
}

/** How well a profile's name (else its address) matches the search words: 0 exact … 4 address, 5 another field. */
function nameTier(p: Profile, terms: readonly string[]): number {
  const name = p.name.toLowerCase();
  const address = profileAddress(p).full.toLowerCase();
  let best = 5;
  for (const t of terms) {
    const tier = name === t ? 0 : name.startsWith(t) ? 1 : name.split(/[^a-z0-9]+/).some((w) => w.startsWith(t)) ? 2 : name.includes(t) ? 3 : address.includes(t) ? 4 : 5;
    best = Math.min(best, tier);
  }
  return best;
}

/**
 * The search's one ranked list: the whole query as the exact name first; then, when a word is an environment word
 * (prod, stage, qa…), that environment's profiles (never a hidden filter); then name exact › prefix › word prefix ›
 * contains › address › other fields; ties go to the most recently connected, then to the least risky environment (Local
 * before Production, so an exact tie never makes Production the Enter target), then A–Z.
 */
export function rankProfiles(list: readonly Profile[], query: string, recent: readonly string[] = []): Profile[] {
  const q = query.trim().toLowerCase();
  const terms = searchTerms(q);
  const envTerms = terms.filter(isEnvWord);
  const nameTerms = terms.filter((t) => !isEnvWord(t));
  const rank = new Map(recent.map((id, i) => [id, i]));
  const key = (p: Profile) => [
    p.name.toLowerCase() === q ? 0 : 1,
    envTerms.length && !envTerms.some((t) => ENV_WORDS[p.environment]?.includes(t)) ? 1 : 0,
    nameTerms.length ? nameTier(p, nameTerms) : 5,
    rank.get(p.id) ?? Number.MAX_SAFE_INTEGER,
    ENV_ORDER.length - 1 - ENV_ORDER.indexOf(p.environment),
  ];
  return list
    .map((p) => ({ p, k: key(p) }))
    .sort((a, b) => {
      for (let i = 0; i < a.k.length; i++) if (a.k[i] !== b.k[i]) return a.k[i] - b.k[i];
      return a.p.name.localeCompare(b.p.name);
    })
    .map((x) => x.p);
}

export type SectionKind = "pinned" | "group" | "env" | "matches";

export interface ProfileSection {
  key: string;
  kind: SectionKind;
  title: string;
  profiles: Profile[];
  /** How many of `profiles` are Production (Pinned and group headers say it). */
  prod: number;
}

const ENV_SECTIONS: { envs: EnvironmentKind[]; title: string }[] = [
  { envs: ["prod"], title: "Production" },
  { envs: ["staging", "test"], title: "Staging & test" },
  { envs: ["dev", "local"], title: "Development & local" },
];

export interface SectionOptions {
  /** Grouped profiles get a section per group (Settings › Show groups). */
  showGroups?: boolean;
  /** The search text: when it has a word, the sections give way to one ranked list. */
  query?: string;
  /** Profile ids by last connect, newest first: the search's tie-break. */
  recent?: readonly string[];
}

const section = (key: string, kind: SectionKind, title: string, profiles: Profile[]): ProfileSection => ({
  key, kind, title, profiles, prod: profiles.filter((p) => p.environment === "prod").length,
});

/**
 * The list as shown; `flat` is the on-screen order for keyboard moves, and every profile is in it once.
 * Searching: one ranked section, "N matches". Otherwise: Pinned (in pin order), then with `showGroups` one section per
 * connection group (A–Z, rows Production first), then Production, Staging & test and Development & local for the rest.
 * There is no "No group" section (§6).
 */
export function buildSections(list: readonly Profile[], pinned: readonly string[], opts: SectionOptions = {}): { sections: ProfileSection[]; flat: Profile[] } {
  if (searchTerms(opts.query ?? "").length) {
    const ranked = rankProfiles(list, opts.query ?? "", opts.recent);
    const sections = ranked.length ? [section("matches", "matches", `${ranked.length} ${ranked.length === 1 ? "match" : "matches"}`, ranked)] : [];
    return { sections, flat: ranked };
  }
  const split = splitPinned(list, pinned);
  const sections: ProfileSection[] = [];
  if (split.pinned.length) sections.push(section("pinned", "pinned", "Pinned", split.pinned));
  let rest = split.rest;
  if (opts.showGroups) {
    const names = [...new Set(rest.map((p) => p.groupName).filter((g): g is string => !!g))].sort(compareGroupNames);
    for (const name of names) sections.push(section(`g:${name}`, "group", name, byEnvironment(rest.filter((p) => p.groupName === name))));
    rest = rest.filter((p) => !p.groupName);
  }
  for (const { envs, title } of ENV_SECTIONS) {
    const ps = byEnvironment(rest.filter((p) => envs.includes(p.environment)));
    if (ps.length) sections.push(section(envs.join("+"), "env", title, ps));
  }
  return { sections, flat: sections.flatMap((s) => s.profiles) };
}

/**
 * The row the dialog opens on: `focus` (a profile just saved), else the most recently connected profile that is listed,
 * else the first pinned one that is not Production, else the only one, else none: Enter then does nothing until the
 * user picks a row. Never simply the first row, which is Production by construction. A profile this build cannot open
 * is never the default.
 */
export function defaultActiveId(flat: readonly Profile[], opts: { recent?: readonly string[]; pinned?: readonly string[]; focus?: string } = {}): string | undefined {
  const byId = new Map(flat.map((p) => [p.id, p]));
  const usable = (p: Profile | undefined): p is Profile => !!p && !p.unsupported;
  if (opts.focus && byId.has(opts.focus)) return opts.focus;
  for (const id of opts.recent ?? []) if (usable(byId.get(id))) return id;
  for (const id of opts.pinned ?? []) {
    const p = byId.get(id);
    if (usable(p) && p.environment !== "prod") return id;
  }
  return flat.length === 1 && usable(flat[0]) ? flat[0].id : undefined;
}

/**
 * The muted words after a row's name. Outside a group's own section a grouped profile names its group; a profile that
 * shares its name, group and address with another adds the user (else the database) that differs, so no two rows read
 * the same.
 */
export function nameSuffix(p: Profile, all: readonly Profile[], inGroupSection: boolean): string {
  const same = (a: string | null | undefined, b: string | null | undefined) => (a ?? "").trim().toLowerCase() === (b ?? "").trim().toLowerCase();
  const twins = all.filter((o) => o.id !== p.id && same(o.name, p.name));
  const parts: string[] = [];
  if (!inGroupSection && p.groupName) parts.push(p.groupName);
  else if (!inGroupSection && twins.some((o) => o.groupName)) parts.push("No group");
  const sameSpot = twins.filter((o) => same(o.groupName, p.groupName) && profileAddress(o).full === profileAddress(p).full);
  if (p.username && sameSpot.some((o) => !same(o.username, p.username))) parts.push(p.username);
  else if (p.databaseDefault && sameSpot.some((o) => !same(o.databaseDefault, p.databaseDefault))) parts.push(p.databaseDefault);
  return parts.join(" · ");
}

const PROD_WORDS = new Set(["prod", "prd", "production"]);

/**
 * A profile not tagged Production whose name or server host has the word prod, prd or production in it (whole words,
 * split at punctuation, digits and camelCase: "orders-prod", "prod01", "ordersProd"; never "products", "preprod" or
 * "non-prod"). The dialog marks it so a Production server saved with the default environment stands out.
 */
export function prodNameHint(p: Pick<Profile, "environment" | "name" | "host" | "location">): boolean {
  if (p.environment === "prod") return false;
  const text = `${p.name} ${locationOf(p) === "server" ? (p.host ?? "") : ""}`;
  const words = text
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/([a-zA-Z])([0-9])/g, "$1 $2")
    .replace(/([0-9])([a-zA-Z])/g, "$1 $2")
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter(Boolean);
  return words.some((w, i) => PROD_WORDS.has(w) && words[i - 1] !== "pre" && words[i - 1] !== "non");
}

/** What a row shows and what Enter does on it. */
export type RowState = "idle" | "connecting" | "connected" | "failed" | "unavailable";

export function rowState(p: Pick<Profile, "unsupported">, server: Pick<ServerConn, "status"> | undefined): RowState {
  if (p.unsupported) return "unavailable";
  if (!server) return "idle";
  return server.status === "connected" ? "connected" : server.status === "connecting" ? "connecting" : "failed";
}

/** Enter's verb (and the row's button) for a state; none where Enter does nothing. */
export const ROW_ACTION: Record<RowState, string | null> = { idle: "Connect", failed: "Retry", connected: "New query", connecting: null, unavailable: null };

/** `text` cut into the runs that match a search word and the runs that do not, for underlining the matches. */
export function matchParts(text: string, terms: readonly string[]): { text: string; hit: boolean }[] {
  // Lower-cased one character at a time with each position's source: a character whose lower case is longer ("İ" is two
  // code units) shifted every later highlight when the whole text was lower-cased at once.
  let low = "";
  const from: number[] = [];
  for (let i = 0; i < text.length; i++) {
    const l = text[i].toLowerCase();
    low += l;
    for (let k = 0; k < l.length; k++) from.push(i);
  }
  const hit = new Array<boolean>(text.length).fill(false);
  for (const t of terms) {
    if (!t) continue;
    for (let i = low.indexOf(t); i >= 0; i = low.indexOf(t, i + t.length)) hit.fill(true, from[i], from[i + t.length - 1] + 1);
  }
  const out: { text: string; hit: boolean }[] = [];
  for (let i = 0; i < text.length; i++) {
    const last = out[out.length - 1];
    if (last && last.hit === hit[i]) last.text += text[i];
    else out.push({ text: text[i], hit: hit[i] });
  }
  return out;
}

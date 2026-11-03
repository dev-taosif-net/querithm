// Open-tabs list: search and grouping by connection (pure; the list component renders the result).
import type { Profile } from "../ipc/contract";
import type { TabState } from "../store/tabs";
import type { ToolTab } from "../store/toolTabs";
import { envLabel } from "../store/profiles";
import { ENGINE_TERMS } from "../connections/profileList";

export interface TabRow {
  id: string;
  /** The query tab; for a tool tab, its title and database in the same shape. */
  tab: Pick<TabState, "title"> & Partial<TabState>;
  tool?: ToolTab;
  profile?: Profile;
}

/** What search looks at; a recently closed tab's snapshot has the same fields. */
type Searchable = { tab: Pick<TabState, "title"> & Partial<Pick<TabState, "filePath" | "database" | "spid">>; profile?: Profile };

function haystack({ tab, profile }: Searchable): string {
  return [
    tab.title, tab.filePath, tab.database, tab.spid ? `spid ${tab.spid}\n${tab.spid}` : undefined,
    ...(profile ? [profile.name, profile.host, profile.groupName, profile.environment, envLabel[profile.environment], ...ENGINE_TERMS[profile.engine]] : ["no connection"]),
  ].filter(Boolean).join("\n").toLowerCase();
}

/**
 * Every whitespace-separated term must appear in the title, file path, database, SPID, the connection's name,
 * address, environment or engine, or in `sql` (the start of the tab's text, lower case) (D18).
 */
export function matchesTab(row: Searchable, text: string, sql = ""): boolean {
  const terms = text.toLowerCase().split(/\s+/).filter(Boolean);
  if (!terms.length) return true;
  const h = sql ? `${haystack(row)}\n${sql}` : haystack(row);
  return terms.every((t) => h.includes(t));
}

export interface TabGroup {
  key: string;
  profile?: Profile;
  rows: TabRow[];
}

/** One group per connection in first-seen order (rows keep their order); unconnected tabs last. */
export function groupByConnection(rows: readonly TabRow[]): TabGroup[] {
  const groups = new Map<string, TabGroup>();
  for (const r of rows) {
    const key = r.profile?.id ?? "";
    if (!groups.has(key)) groups.set(key, { key, profile: r.profile, rows: [] });
    groups.get(key)!.rows.push(r);
  }
  const none = groups.get("");
  groups.delete("");
  return none ? [...groups.values(), none] : [...groups.values()];
}

// How a tab's stored title is shown (owner revision 1 of the strip redesign). The stored title never changes, so a
// saved workspace and the tooltip keep the full name: the strip, the accessible name and the dialogs drop ".sql" and
// the debugger's 🐞 marker, and a leading script verb is drawn on its own (never cut; destructive verbs in --q-err).
// Titles are labels the app wrote or the user typed, not SQL: nothing here reads query text.
import type { Engine } from "../ipc/contract";
import { ENGINES, isEngine } from "../engines";

/** Verbs the app puts first in script titles ("DROP Orders", "EXEC usp_Post"); longest first. */
const VERBS = ["DROP and CREATE", "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "EXEC", "TRUNCATE", "MERGE", "ENABLE", "DISABLE"];
/** Verbs whose script destroys data or objects: a DROP tab must never look like a CREATE tab. */
const DANGER = new Set(["DROP and CREATE", "DROP", "DELETE", "TRUNCATE"]);
const DEBUG_MARK = "🐞";

/** "SQLQuery5.sql": a tab the user has not named and that has no file (D24 names it once, at its first run). */
export function isUntitledTitle(title: string): boolean {
  return /^SQLQuery\d+\.sql$/.test(title);
}

/** A debugger tab (its title starts with the 🐞 marker the debugger gives it). */
export function isDebugTitle(title: string): boolean {
  return title.startsWith(DEBUG_MARK);
}

/** The title without the debugger marker and without a ".sql" extension. */
export function tabTitleText(title: string): string {
  let t = title;
  if (isDebugTitle(t)) t = t.slice(DEBUG_MARK.length).trimStart();
  if (t.length > 4 && t.slice(-4).toLowerCase() === ".sql") t = t.slice(0, -4);
  return t;
}

export interface TabName {
  /** The leading script verb, upper case as the app writes it; undefined for other titles. */
  verb?: string;
  /** The verb destroys data or objects (DROP, DROP and CREATE, DELETE, TRUNCATE). */
  danger: boolean;
  /** The rest of the title. */
  name: string;
}

/** Splits a title into its script verb and name ("DROP sales.Orders.sql" → DROP + "sales.Orders"). */
export function tabName(title: string): TabName {
  const text = tabTitleText(title);
  for (const v of VERBS) {
    if (text.length > v.length + 1 && text.startsWith(v) && text[v.length] === " ") {
      const name = text.slice(v.length + 1).trimStart();
      if (name) return { verb: v, danger: DANGER.has(v), name };
    }
  }
  return { danger: false, name: text };
}

/**
 * The two parts of a middle cut that keeps the last `keep` characters: the head takes the ellipsis (CSS
 * text-overflow), the tail is never cut, so "reconcile_invoice_payments_2026_10" ends "…2026_10" when narrow.
 */
export function middleCut(text: string, keep: number): [head: string, tail: string] {
  const chars = Array.from(text);
  if (keep <= 0 || chars.length <= keep) return [text, ""];
  return [chars.slice(0, -keep).join(""), chars.slice(-keep).join("")];
}

/** "Orders" in the engine's default schema (dbo, public), "sales.Orders" elsewhere: the title names the object briefly. */
export function objectLabel(schema: string | null | undefined, name: string, engine: Engine | undefined): string {
  if (!schema) return name;
  // No engine: no default schema to leave out.
  const home = isEngine(engine) ? ENGINES[engine].defaultSchema : undefined;
  return schema === home ? name : `${schema}.${name}`;
}

interface NamedTab {
  id: string;
  title: string;
  profileId?: string;
  database?: string;
  filePath?: string;
}

/**
 * "q3/": the folder of a tab's file, when another open tab shows the same name on the same connection and database,
 * so two "report" tabs can be told apart. Undefined otherwise, and for tabs without a file.
 */
export function folderSuffix(tabs: Readonly<Record<string, NamedTab>>, id: string): string | undefined {
  const t = tabs[id];
  if (!t?.filePath) return undefined;
  const name = tabTitleText(t.title);
  const twin = Object.values(tabs).some((o) => o.id !== id && o.profileId === t.profileId && o.database === t.database && tabTitleText(o.title) === name);
  if (!twin) return undefined;
  const parts = t.filePath.split(/[\\/]/).filter(Boolean);
  return parts.length > 1 ? `${parts[parts.length - 2]}/` : undefined;
}

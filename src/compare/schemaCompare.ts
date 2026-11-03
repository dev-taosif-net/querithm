// Schema compare: the pure parts of the tab — sides, filters, grouping, default selection.
import type { CompareClass, CompareItem, CompareObjectKind, PossibleRename } from "../ipc/contract";

/**
 * A side as the tab holds it: a live database (optionally one schema), or a saved model file (shell token). A
 * session-owned engine's live side keeps the query tab session it was opened from (Phase D0d, ownSessionFor).
 */
export type SideState =
  | { kind: "live"; profileId?: string; database?: string; schema?: string | null; sessionId?: string }
  | { kind: "file"; token: string; name: string };

export interface CompareTabParams {
  source?: SideState;
  target?: SideState;
}

export type ClassFilter = "all" | "different" | "sourceOnly" | "targetOnly";
export type GroupBy = "kind" | "action";

export const CLASS_FILTERS: { id: ClassFilter; label: string }[] = [
  { id: "all", label: "All" },
  { id: "different", label: "Different" },
  { id: "sourceOnly", label: "Source only" },
  { id: "targetOnly", label: "Target only" },
];

export const KIND_LABEL: Record<CompareObjectKind, string> = {
  schema: "Schemas", extension: "Extensions", type: "Types", sequence: "Sequences", table: "Tables", view: "Views",
  function: "Functions", procedure: "Procedures", trigger: "Triggers",
};

/** What applying the item does to the target. */
export const ACTION_LABEL: Record<Exclude<CompareClass, "identical">, string> = { sourceOnly: "Create", different: "Change", targetOnly: "Drop" };

export const CLASS_BADGE: Record<CompareClass, { text: string; cls: string }> = {
  sourceOnly: { text: "+", cls: "text-ok" },
  targetOnly: { text: "−", cls: "text-err" },
  different: { text: "≠", cls: "text-warn" },
  identical: { text: "=", cls: "text-muted" },
};

export function sideReady(s: SideState | undefined): boolean {
  return !!s && (s.kind === "file" ? !!s.token : !!s.profileId && !!s.database);
}

export function sideTitle(s: SideState | undefined, profileName: (id: string) => string | undefined): string {
  if (!s) return "?";
  if (s.kind === "file") return s.name;
  if (!s.profileId || !s.database) return "?";
  return `${profileName(s.profileId) ?? "?"} · ${s.database}${s.schema ? `.${s.schema}` : ""}`;
}

export function whitespaceOnly(i: CompareItem): boolean {
  return i.class === "different" && i.tags.length > 0 && i.tags.every((t) => t === "whitespaceOnly");
}

export function filterItems(items: CompareItem[], f: { cls: ClassFilter; hideWhitespace: boolean; hideIdentical: boolean; search: string }): CompareItem[] {
  const q = f.search.trim().toLowerCase();
  return items.filter((i) => {
    if (f.cls !== "all" && i.class !== f.cls) return false;
    if (f.cls === "all" && f.hideIdentical && i.class === "identical") return false;
    if (f.hideWhitespace && whitespaceOnly(i)) return false;
    if (q && !`${i.schema}.${i.name}`.toLowerCase().includes(q)) return false;
    return true;
  });
}

const KIND_ORDER: CompareObjectKind[] = ["schema", "extension", "type", "sequence", "table", "view", "function", "procedure", "trigger"];
const ACTION_ORDER: CompareClass[] = ["sourceOnly", "different", "targetOnly", "identical"];

export type Row = { t: "group"; key: string; label: string; count: number; keys: string[] } | { t: "item"; key: string; item: CompareItem };

/** Groups (by kind or by action) with their items; collapsed groups show only their header. */
export function buildRows(items: CompareItem[], by: GroupBy, collapsed: ReadonlySet<string>): Row[] {
  const groups = new Map<string, CompareItem[]>();
  for (const i of items) {
    const g = by === "kind" ? i.kind : i.class;
    (groups.get(g) ?? groups.set(g, []).get(g)!).push(i);
  }
  const order: string[] = by === "kind" ? KIND_ORDER : ACTION_ORDER;
  const rows: Row[] = [];
  for (const g of order) {
    const list = groups.get(g);
    if (!list?.length) continue;
    const label = by === "kind" ? KIND_LABEL[g as CompareObjectKind] : g === "identical" ? "Identical" : ACTION_LABEL[g as Exclude<CompareClass, "identical">];
    rows.push({ t: "group", key: `g:${g}`, label, count: list.length, keys: list.map((i) => i.key) });
    if (collapsed.has(`g:${g}`)) continue;
    for (const item of [...list].sort((a, b) => `${a.schema}.${a.name}`.localeCompare(`${b.schema}.${b.name}`))) rows.push({ t: "item", key: item.key, item });
  }
  return rows;
}

/** Checked by default: everything that differs except cosmetic (whitespace-only) changes. */
export function defaultSelection(items: CompareItem[]): Set<string> {
  return new Set(items.filter((i) => i.class !== "identical" && !whitespaceOnly(i)).map((i) => i.key));
}

export function counts(items: CompareItem[]): Record<CompareClass, number> {
  const c: Record<CompareClass, number> = { identical: 0, different: 0, sourceOnly: 0, targetOnly: 0 };
  for (const i of items) c[i.class]++;
  return c;
}

export const renameKey = (r: PossibleRename) => `${r.tableKey}|${r.from}|${r.to}`;

/** The swap button: source ⇄ target. */
export function swapSides(p: CompareTabParams): CompareTabParams {
  return { source: p.target, target: p.source };
}

export function itemName(i: CompareItem): string {
  if (i.kind === "extension") return i.name;
  if (i.kind === "schema") return i.name;
  return `${i.schema}.${i.name}${i.detail != null ? (i.kind === "trigger" ? ` on ${i.detail}` : `(${i.detail})`) : ""}`;
}

// TabFacts (what TabIdentity draws) from the stores, for the strip, Ctrl+P and Ctrl+Tab alike (D18).
import type { Profile } from "../ipc/contract";
import type { TabState } from "../store/tabs";
import type { ToolTab } from "../store/toolTabs";
import type { TabFacts, TabTarget } from "./TabIdentity";

type ById = (id: string | undefined) => Profile | undefined;

export function queryFacts(t: TabState, profile: Profile | undefined, disconnected: boolean, suffix?: string): TabFacts {
  return {
    kind: "query", title: t.title, profile, database: t.database, disconnected: !!profile && disconnected, busy: t.busy, txOpen: t.txState === "open",
    txStartedAt: t.txStartedAt, spid: t.spid, dirty: t.dirty, readOnly: !!t.readOnly, pinned: !!t.pinned, filePath: t.filePath, suffix,
    notebook: t.notebook || undefined,
  };
}

/** Where a compare tab reads from and writes to (its params hold both sides). */
export function compareSides(tool: ToolTab, byId: ById): { source?: TabTarget; target?: TabTarget } {
  type Side = { profileId?: string; database?: string; kind?: string; name?: string } | undefined;
  const p = tool.params as { source?: Side; target?: Side };
  const side = (s: Side): TabTarget | undefined =>
    !s ? undefined : s.kind === "file" ? { file: s.name } : s.profileId ? { profile: byId(s.profileId), database: s.database } : undefined;
  return { source: side(p.source), target: side(p.target) };
}

/** A tool tab: compare tabs read source → target; the others run on their profile and database (D17). */
export function toolFacts(tool: ToolTab, byId: ById, isDisconnected: (profileId: string) => boolean): TabFacts {
  const compare = tool.kind === "schemaCompare" || tool.kind === "dataCompare";
  const sides = compare ? compareSides(tool, byId) : {};
  const profile = compare ? sides.source?.profile : byId(tool.profileId);
  return {
    kind: tool.kind,
    title: tool.title,
    profile,
    database: compare ? sides.source?.database : tool.database,
    target: compare && (sides.target?.profile || sides.target?.file) ? sides.target : undefined,
    disconnected: !!profile && isDisconnected(profile.id),
  };
}

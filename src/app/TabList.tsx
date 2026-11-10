// Open-tabs list (Ctrl+P and the strip's "≡" button) and the Ctrl+Tab switcher. Every row is the tab's own identity,
// the same two lines as on the strip (D18): query and tool tabs alike, colours fitted to the list's surfaces, no
// coloured rail. Ctrl+P is a modal dialog with a combobox over a listbox; it searches the tabs' SQL and SPIDs too.
import { useEffect, useMemo, useRef, useState } from "react";
import type { Profile } from "../ipc/contract";
import { useTabs, type TabState } from "../store/tabs";
import { useToolTabs, type ToolTab } from "../store/toolTabs";
import { useProfiles } from "../store/profiles";
import { dialectOf } from "../engines";
import { useSettings } from "../store/settings";
import { useUi } from "../store/ui";
import { useConnections } from "../store/connections";
import { getModel } from "../editor/editors";
import { firstStatementLine } from "../editor/completion";
import { ServerChip } from "./EnvTag";
import { activateTab, closeTab } from "./actions";
import { closeTool } from "./toolActions";
import { activePanelId, allTabsOrder, closeTabs, pickFromSwitcher, reopenClosed, textOf } from "./tabActions";
import { closeTargets, switcherOrder } from "./tabNav";
import { groupByConnection, matchesTab, type TabRow } from "./tabFilter";
import { queryFacts, toolFacts } from "./tabFacts";
import { displayTitle, spokenName, TabLine1, TabLine2, type TabFacts } from "./TabIdentity";
import { OVERLAY_SURFACES } from "./tabStyle";
import { tabTitleText } from "./tabTitle";
import { closedAgo, entryAt, useClosedTabs, type ClosedTabSnapshot } from "../store/closedTabs";

/** Search reads the first 4 KB of a tab's SQL; read again only when the editor's model changes. */
const SEARCH_CHARS = 4096;
const sqlCache = new Map<string, { version: number; text: string; line?: string }>();

function modelEntry(id: string) {
  const model = getModel(id);
  if (!model) return undefined;
  const version = model.getVersionId?.() ?? 0;
  let e = sqlCache.get(id);
  if (!e || e.version !== version) {
    e = { version, text: model.getValue().slice(0, SEARCH_CHARS).toLowerCase() };
    sqlCache.set(id, e);
  }
  return e;
}

/** The tab's SQL for search (lower case). */
function sqlOf(id: string): string {
  return modelEntry(id)?.text ?? "";
}

/** The tab's first statement on one line: the third line of its row. */
function previewOf(id: string, engine: Profile["engine"] | undefined): string {
  const e = modelEntry(id);
  if (!e) return "";
  if (e.line === undefined) e.line = firstStatementLine(getModel(id)!.getValue(), dialectOf(engine), 120);
  return e.line;
}

interface ClosedRow {
  snap: ClosedTabSnapshot;
  /** Position in the closed-tabs store (search can skip entries). */
  index: number;
  profile?: Profile;
  /** The titles of a bulk close shown as one entry (D28); one title for a single tab. */
  titles: string[];
}

/** Recently closed tabs matching `q`, a bulk close as one entry; none in the hidden-tabs list. */
function useClosedRows(only: string[] | undefined, q: string): ClosedRow[] {
  const list = useClosedTabs((s) => s.list);
  const profiles = useProfiles((s) => s.profiles);
  return useMemo(() => {
    if (only) return [];
    const byId = new Map(profiles.map((p) => [p.id, p]));
    const rows: (ClosedRow & { match: boolean })[] = [];
    list.forEach((snap, index) => {
      const profile = snap.profileId ? byId.get(snap.profileId) : undefined;
      const match = matchesTab({ tab: snap, profile }, q);
      const last = rows[rows.length - 1];
      if (snap.batch && last && last.snap.batch === snap.batch) {
        last.titles.push(snap.title);
        last.match ||= match;
        return;
      }
      rows.push({ snap, index, profile, titles: [snap.title], match });
    });
    return rows.filter((r) => r.match);
  }, [list, profiles, only, q]);
}

function rowOf(id: string, tabs: Record<string, TabState>, tools: Record<string, ToolTab>, byId: Map<string, Profile>): TabRow | undefined {
  const t = tabs[id];
  if (t) return { id, tab: t, profile: t.profileId ? byId.get(t.profileId) : undefined };
  const tool = tools[id];
  if (tool) return { id, tool, tab: { title: tool.title, database: tool.database }, profile: tool.profileId ? byId.get(tool.profileId) : undefined };
  return undefined;
}

/** Every open tab, query and tool (D17), most recently used first; or just `only`. */
function useRows(only?: string[]): TabRow[] {
  const tabs = useTabs((s) => s.tabs);
  const mru = useTabs((s) => s.mru);
  const order = useTabs((s) => s.order);
  const tools = useToolTabs((s) => s.tools);
  const profiles = useProfiles((s) => s.profiles);
  return useMemo(() => {
    const byId = new Map(profiles.map((p) => [p.id, p]));
    const keep = only ? new Set(only) : undefined;
    return switcherOrder(mru, allTabsOrder())
      .filter((id) => !keep || keep.has(id))
      .map((id) => rowOf(id, tabs, tools, byId))
      .filter((r): r is TabRow => !!r);
    // `order` re-reads the strip after drags and closes.
  }, [tabs, tools, mru, order, profiles, only]);
}

function useFacts() {
  const profiles = useProfiles((s) => s.profiles);
  const disconnected = useConnections((s) => s.disconnected);
  return (r: TabRow): TabFacts => {
    const isOff = (id: string) => !!disconnected[id];
    if (r.tool) return toolFacts(r.tool, (id) => profiles.find((p) => p.id === id), isOff);
    return queryFacts(r.tab as TabState, r.profile, !!r.profile && isOff(r.profile.id));
  };
}

/** One row: the tab's identity on the list's surface, with the SQL's first statement under it. */
function Identity({ f, line }: { f: TabFacts; line?: string }) {
  return (
    <div className="q-list-identity min-w-0 flex-1 flex flex-col gap-[1px]">
      <TabLine1 f={f} />
      <TabLine2 f={f} surfaces={OVERLAY_SURFACES} />
      {line && <span className="q-mono text-[10.5px] leading-[15px] text-muted truncate">{line}</span>}
    </div>
  );
}

export function TabList({ onClose, only }: { onClose: () => void; only?: string[] }) {
  const rows = useRows(only);
  const factsOf = useFacts();
  const grouped = useSettings((s) => s.tabListGroupByConnection);
  const mru = useTabs((s) => s.mru);
  const [q, setQ] = useState("");
  const [sel, setSel] = useState<number | null>(null);
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => void setTimeout(() => input.current?.focus(), 0), []);

  const current = activePanelId();
  const previous = switcherOrder(mru, allTabsOrder()).find((id) => id !== current);
  const filtered = useMemo(() => rows.filter((r) => matchesTab(r, q, r.tool ? "" : sqlOf(r.id))), [rows, q]);
  const sections = useMemo(() => (grouped ? groupByConnection(filtered) : [{ key: "all", profile: undefined, rows: filtered }]), [filtered, grouped]);
  const flat = sections.flatMap((s) => s.rows);
  const closed = useClosedRows(only, q);
  // One highlight runs through the open tabs, then the recently closed ones. With nothing typed it starts on the
  // previous tab, so Ctrl+P, Enter goes back (D18).
  const total = flat.length + closed.length;
  const start = !q && previous ? Math.max(0, flat.findIndex((r) => r.id === previous)) : 0;
  const cur = Math.min(sel ?? start, Math.max(0, total - 1));
  const tabsState = useTabs.getState().tabs;
  const emptyCount = current && tabsState[current] ? closeTargets("empty", { order: rows.map((r) => r.id), tabs: tabsState, tabId: current, textOf }).length : 0;
  // Hover selects only after the pointer really moves: a list opening under a resting pointer keeps the keyboard's pick.
  const pointer = useRef<{ x: number; y: number } | null>(null);
  const hover = (k: number) => (e: { clientX: number; clientY: number }) => {
    const p = pointer.current;
    pointer.current = { x: e.clientX, y: e.clientY };
    if (p && (p.x !== e.clientX || p.y !== e.clientY) && k !== cur) setSel(k);
  };

  const open = (r: TabRow | undefined) => {
    if (!r) return;
    onClose();
    activateTab(r.id);
  };
  const reopen = (r: ClosedRow | undefined) => {
    if (!r) return;
    onClose();
    reopenClosed(r.index);
  };
  const pick = (k: number) => (k < flat.length ? open(flat[k]) : reopen(closed[k - flat.length]));
  const optId = (k: number) => `q-tablist-opt-${k}`;
  const label = only ? "Hidden tabs" : "Go to tab";

  return (
    <div className="q-modal-backdrop fixed inset-0 z-40" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      {/* 88 px from the top: below the tab strip, which stays in view (D18). */}
      <div role="dialog" aria-modal="true" aria-label={label} className="q-palette-in absolute left-1/2 top-[88px] -translate-x-1/2 w-[640px] max-w-[92vw] bg-raised border border-line rounded-lg shadow-2xl overflow-hidden" style={{ ["--q-row-bg" as string]: "var(--q-raised)" }}>
        <input
          ref={input}
          role="combobox"
          aria-expanded="true"
          aria-controls="q-tablist"
          aria-autocomplete="list"
          aria-activedescendant={total ? optId(cur) : undefined}
          className="w-full bg-panel px-3 py-2.5 text-[13px] text-fg outline-none border-b border-line"
          placeholder={`Go to one of ${rows.length} ${only ? "hidden" : "open"} tabs: title, connection, database, file, SQL, SPID…`}
          aria-label="Search open tabs"
          value={q}
          onChange={(e) => { setQ(e.target.value); setSel(null); }}
          onKeyDown={(e) => {
            if (e.key === "Escape") onClose();
            else if (e.key === "ArrowDown") { e.preventDefault(); setSel(Math.min(total - 1, cur + 1)); }
            else if (e.key === "ArrowUp") { e.preventDefault(); setSel(Math.max(0, cur - 1)); }
            else if (e.key === "Enter") { e.preventDefault(); pick(cur); }
          }}
        />
        <div id="q-tablist" role="listbox" aria-label={only ? "Hidden tabs" : "Open tabs"} className="max-h-[56vh] overflow-y-auto py-1">
          {sections.map((s) => (
            <div key={s.key} role={grouped ? "group" : "presentation"} aria-label={grouped ? (s.profile?.name ?? "No connection") : undefined}>
              {grouped && (
                <div className="px-3 pt-2 pb-1 text-[10px] font-bold text-muted flex items-center gap-1.5" aria-hidden>
                  {s.profile ? <ServerChip profileId={s.profile.id} surface="var(--q-raised)" /> : <span className="uppercase">No connection</span>}
                  <span className="text-muted font-normal">{s.rows.length}</span>
                </div>
              )}
              {s.rows.map((r) => {
                const k = flat.indexOf(r);
                const f = factsOf(r);
                const meta = r.id === current ? "current" : r.id === previous ? "previous" : "";
                return (
                  <div
                    key={r.id}
                    id={optId(k)}
                    role="option"
                    aria-selected={k === cur}
                    aria-label={`${spokenName(f)}${meta ? `, ${meta}` : ""}`}
                    ref={(el) => { if (k === cur && el) el.scrollIntoView?.({ block: "nearest" }); }}
                    className={`group/row mx-1 px-2 py-1 rounded flex items-start gap-2 cursor-default ${k === cur ? "bg-overlay-sel q-hl" : "hover:bg-hover"}`}
                    style={k === cur ? { ["--q-row-bg" as string]: "var(--q-overlay-sel)" } : undefined}
                    onMouseMove={hover(k)}
                    onMouseDown={(e) => { if (e.button === 0) { e.preventDefault(); open(r); } }}
                  >
                    <Identity f={f} line={r.tool ? (displayTitle(r.tool.kind, r.tool.title) !== r.tool.title ? r.tool.title : undefined) : previewOf(r.id, r.profile?.engine)} />
                    {meta && <span className="text-[10.5px] leading-[17px] text-muted shrink-0">{meta}</span>}
                    <button
                      className={`shrink-0 w-6 h-6 rounded text-muted hover:text-fg hover:bg-hover ${k === cur ? "" : "opacity-0 group-hover/row:opacity-100"}`}
                      title="Close tab"
                      tabIndex={-1}
                      aria-label={`Close ${tabTitleText(displayTitle(f.kind, f.title))}`}
                      onMouseDown={(e) => { e.preventDefault(); e.stopPropagation(); if (r.tool) closeTool(r.id); else void closeTab(r.id); }}
                    >
                      ×
                    </button>
                  </div>
                );
              })}
            </div>
          ))}
          {!flat.length && <div className="px-3 py-3 text-muted">{rows.length ? `No ${only ? "hidden" : "open"} tab matches.` : only ? "No hidden tabs." : "No open tabs."}</div>}
          {closed.length > 0 && (
            <div role="group" aria-label="Recently closed">
              <div className="px-3 pt-2 pb-1 text-[10px] font-bold uppercase text-muted flex items-center gap-1.5" aria-hidden>
                <span>Recently closed</span>
                <span className="text-muted font-normal">{closed.length}</span>
              </div>
              {closed.map((r, i) => {
                const k = flat.length + i;
                const f: TabFacts = { kind: "query", title: r.snap.title, profile: r.profile, database: r.snap.database, filePath: r.snap.filePath };
                const many = r.titles.length > 1;
                const names = r.titles.map(tabTitleText).join(", ");
                return (
                  <div
                    key={`${r.snap.closedAt}-${r.index}`}
                    id={optId(k)}
                    role="option"
                    aria-selected={k === cur}
                    aria-label={`${many ? `Reopen ${r.titles.length} tabs: ${names}` : spokenName(f)}, closed ${closedAgo(r.snap.closedAt)}`}
                    ref={(el) => { if (k === cur && el) el.scrollIntoView?.({ block: "nearest" }); }}
                    className={`group/row mx-1 px-2 py-1 rounded flex items-start gap-2 cursor-default ${k === cur ? "bg-overlay-sel q-hl" : "hover:bg-hover"}`}
                    onMouseMove={hover(k)}
                    onMouseDown={(e) => { if (e.button === 0) { e.preventDefault(); reopen(r); } }}
                  >
                    {many ? (
                      <div className="q-list-identity min-w-0 flex-1 flex flex-col gap-[1px]">
                        <span className="q-tab-l1"><span className="q-tab-name">Reopen {r.titles.length} tabs</span></span>
                        <span className="q-tab-l2 truncate">{names}</span>
                      </div>
                    ) : (
                      <Identity f={f} />
                    )}
                    <span className="text-[10.5px] leading-[17px] text-muted shrink-0">{closedAgo(r.snap.closedAt)}</span>
                    <button
                      className={`shrink-0 w-6 h-6 rounded text-muted hover:text-fg hover:bg-hover ${k === cur ? "" : "opacity-0 group-hover/row:opacity-100"}`}
                      title="Forget"
                      tabIndex={-1}
                      aria-label={`Forget ${many ? `${r.titles.length} tabs` : r.snap.title}`}
                      onMouseDown={(e) => { e.preventDefault(); e.stopPropagation(); const s = useClosedTabs.getState(); s.removeMany(entryAt(s.list, r.index)); }}
                    >
                      ×
                    </button>
                  </div>
                );
              })}
            </div>
          )}
        </div>
        <div role="status" className="sr-only">{q ? `${flat.length} tab${flat.length === 1 ? "" : "s"} match` : ""}</div>
        <div className="px-3 py-1.5 border-t border-line bg-panel flex items-center gap-3 text-[10.5px] text-muted">
          <label className="flex items-center gap-1.5 text-muted cursor-default">
            <input type="checkbox" checked={grouped} onChange={(e) => useSettings.getState().set({ tabListGroupByConnection: e.target.checked })} />
            Group by connection
          </label>
          {emptyCount > 0 && current && (
            <button className="text-muted hover:text-fg underline-offset-2 hover:underline" onClick={() => void closeTabs("empty", current)}>
              Close {emptyCount} empty tab{emptyCount === 1 ? "" : "s"}
            </button>
          )}
          {!only && closed.length > 0 && (
            <button className="text-muted hover:text-fg underline-offset-2 hover:underline" onClick={() => useClosedTabs.getState().clear()}>
              Clear recently closed
            </button>
          )}
          <span className="ml-auto">Enter open · Ctrl+Shift+T reopen · Ctrl+Tab recent</span>
        </div>
      </div>
    </div>
  );
}

/** Shown while Ctrl is held after Ctrl+Tab: recent tabs (tool tabs too), the highlighted one opens on release. */
export function TabSwitcher() {
  const switcher = useUi((s) => s.switcher);
  const tabs = useTabs((s) => s.tabs);
  const tools = useToolTabs((s) => s.tools);
  const profiles = useProfiles((s) => s.profiles);
  const factsOf = useFacts();
  if (!switcher) return null;
  const byId = new Map(profiles.map((p) => [p.id, p]));
  // At most 12 rows, scrolled to keep the highlight visible.
  const start = Math.max(0, Math.min(switcher.index - 5, switcher.ids.length - 12));
  const shown = switcher.ids.slice(start, start + 12);
  const n = switcher.ids.length;
  const hl = rowOf(switcher.ids[switcher.index], tabs, tools, byId);
  return (
    <div className="q-modal-backdrop fixed inset-0 z-50 pointer-events-none flex items-start justify-center pt-[90px]">
      <div className="q-palette-in pointer-events-auto w-[440px] max-w-[92vw] bg-raised border border-line rounded-lg shadow-2xl py-1" style={{ ["--q-row-bg" as string]: "var(--q-raised)" }}>
        <div className="px-3 pt-1 pb-1.5 text-[10px] font-bold uppercase text-muted">
          Recent tabs · {switcher.index + 1} of {n} · release Ctrl to open
        </div>
        <div role="listbox" aria-label="Recent tabs">
          {shown.map((id, k) => {
            const r = rowOf(id, tabs, tools, byId);
            if (!r) return null;
            const i = start + k;
            return (
              <div
                key={id}
                role="option"
                aria-selected={i === switcher.index}
                className={`mx-1 px-2 py-1 rounded flex items-center gap-2 ${i === switcher.index ? "bg-overlay-sel q-hl" : ""}`}
                style={i === switcher.index ? { ["--q-row-bg" as string]: "var(--q-overlay-sel)" } : undefined}
                onMouseDown={(e) => { e.preventDefault(); pickFromSwitcher(i); }}
              >
                <Identity f={factsOf(r)} />
              </div>
            );
          })}
        </div>
        {/* The highlighted tab and its place, read as Ctrl+Tab moves (D18). */}
        <div role="status" aria-live="polite" className="sr-only">{hl ? `${spokenName(factsOf(hl))}, ${switcher.index + 1} of ${n}` : ""}</div>
      </div>
    </div>
  );
}

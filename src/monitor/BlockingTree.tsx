// Blocking tree (B2): head blockers as roots, blocked sessions as children, cycles as "deadlock in progress".
// A virtualised list (fixed row height). Keyboard: ↑/↓ move, →/← expand/collapse, Enter detail, Delete kill (U7).
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import type { ActivityRow } from "../ipc/contract";
import type { MonitorModel } from "./model";
import { buildTree, MAX_TREE_ITEMS, type TreeItem } from "./treeModel";
import { escapeForDisplay, ms, waitColorVar } from "./format";
import { useTopics } from "./useModel";
import { killAvailability } from "./killRules";
import { goToSession, tabTitleForSession } from "./monitorActions";

const ROW = 24;

export interface TreeActions {
  detail: (row: ActivityRow) => void;
  diagnose: (row: ActivityRow) => void;
  kill: (row: ActivityRow, mode: "cancelRequest" | "terminateSession") => void;
}

export function BlockingTree({ model, actions }: { model: MonitorModel; actions: TreeActions }) {
  useTopics(model, ["rows", "texts", "connection"]);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [showAll, setShowAll] = useState<Set<string>>(new Set());
  const [focus, setFocus] = useState(0);
  const [scroll, setScroll] = useState(0);
  const [height, setHeight] = useState(400);
  const box = useRef<HTMLDivElement>(null);
  const items = useMemo(() => buildTree([...model.byKey.values()], model.blocking, collapsed, showAll), [model.byKey, model.blocking, collapsed, showAll]);
  useEffect(() => {
    if (!box.current || typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(() => setHeight(box.current?.clientHeight ?? 400));
    ro.observe(box.current);
    return () => ro.disconnect();
  }, []);
  const first = Math.max(0, Math.floor(scroll / ROW) - 5);
  const last = Math.min(items.length, Math.ceil((scroll + height) / ROW) + 5);
  const toggle = (id: string) => setCollapsed((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const connected = model.connection === "connected" && !model.stopped;

  const onKey = (e: KeyboardEvent) => {
    const it = items[focus];
    if (e.key === "ArrowDown") { e.preventDefault(); setFocus((f) => Math.min(items.length - 1, f + 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setFocus((f) => Math.max(0, f - 1)); }
    else if (!it) return;
    else if (e.key === "ArrowRight" && "expanded" in it && !it.expanded && it.childCount > 0 && !(it.kind === "session" && it.elsewhere != null)) toggle(it.id);
    else if (e.key === "ArrowLeft" && "expanded" in it && it.expanded) toggle(it.id);
    else if (e.key === "Enter" && it.kind === "session") actions.detail(it.row);
    else if (e.key === "Delete" && it.kind === "session" && killAvailability(it.row, model.caps, "terminateSession", connected).enabled) actions.kill(it.row, "terminateSession");
    else if (e.key === "Enter" && it.kind === "more") setShowAll((s) => new Set(s).add(it.id.replace(/\/\+more$/, "")));
  };

  if (items.length === 0) return <div className="flex-1 flex items-center justify-center text-muted text-xs" data-pane-state="empty">No blocking right now</div>;
  return (
    <div
      ref={box}
      className="flex-1 min-h-0 overflow-auto outline-none text-[11.5px] q-mono"
      tabIndex={0}
      role="tree"
      aria-label="Blocking tree"
      onKeyDown={onKey}
      onScroll={(e) => setScroll((e.target as HTMLDivElement).scrollTop)}
      data-testid="blocking-tree"
    >
      <div style={{ height: items.length * ROW, position: "relative" }}>
        {items.slice(first, last).map((it, i) => (
          <Node key={it.id} item={it} top={(first + i) * ROW} focused={first + i === focus} model={model} actions={actions} connected={connected}
            onToggle={() => toggle(it.id)} onMore={() => setShowAll((s) => new Set(s).add(it.id.replace(/\/\+more$/, "")))} onFocus={() => setFocus(first + i)} />
        ))}
      </div>
    </div>
  );
}

function Node({ item, top, focused, model, actions, connected, onToggle, onMore, onFocus }: {
  item: TreeItem; top: number; focused: boolean; model: MonitorModel; actions: TreeActions; connected: boolean;
  onToggle: () => void; onMore: () => void; onFocus: () => void;
}) {
  const style = { position: "absolute" as const, top, left: 0, right: 0, height: ROW, paddingLeft: 8 + item.depth * 18 };
  const cls = `flex items-center gap-2 pr-2 whitespace-nowrap ${focused ? "bg-accent/15 q-hl" : "hover:bg-hover"}`;
  if (item.kind === "more")
    return <div style={style} className={`${cls} text-accent cursor-pointer`} role="treeitem" onClick={onMore}>+{item.hidden.toLocaleString("en-US")} more</div>;
  if (item.kind === "truncated")
    return (
      <div style={style} className={`${cls} text-warn`} role="treeitem" data-testid="tree-truncated">
        Showing the first {MAX_TREE_ITEMS.toLocaleString("en-US")} rows of the blocking tree ({item.hidden.toLocaleString("en-US")} more): collapse nodes to see the rest.
      </div>
    );
  if (item.kind === "cycle")
    return <div style={style} className={`${cls} text-err font-semibold`} role="treeitem">⟲ deadlock in progress: {item.members.join(" → ")}</div>;
  const elsewhere = item.kind === "session" ? item.elsewhere : undefined;
  const caret = "childCount" in item && item.childCount > 0 && elsewhere == null ? (
    <button className="w-3 text-muted" aria-label={item.expanded ? "Collapse" : "Expand"} onClick={onToggle}>{item.expanded ? "▾" : "▸"}</button>
  ) : <span className="w-3" />;
  if (item.kind === "placeholder")
    return (
      <div style={style} className={`${cls} text-muted`} role="treeitem" aria-expanded={item.expanded} onClick={onFocus}>
        {caret}
        <span className="text-muted">{item.isSession ? `session ${item.sessionId}` : `blocker ${item.sessionId}`}</span>
        <span className="italic">{item.reason}</span>
        <span className="text-muted">· {item.childCount} waiting</span>
      </div>
    );
  const r = item.row;
  const text = r.textId ? model.texts.get(r.textId) : undefined;
  const own = r.ownSessionId ? tabTitleForSession(r.ownSessionId) : undefined;
  const term = killAvailability(r, model.caps, "terminateSession", connected);
  const cancel = killAvailability(r, model.caps, "cancelRequest", connected);
  return (
    <div style={style} className={cls} role="treeitem" aria-expanded={"expanded" in item ? item.expanded : undefined} onClick={onFocus} data-session={r.key.id}>
      {caret}
      <span className={`font-semibold ${model.paused ? "text-muted" : "text-fg"}`}>{r.key.id}</span>
      <span className="text-muted">{escapeForDisplay(r.login ?? "—")}</span>
      <span className={r.status === "sleeping" || r.status.startsWith("idle") ? "text-warn" : "text-muted"}>{r.status}</span>
      {r.txElapsedMs != null && <span className="text-muted">tx {ms(r.txElapsedMs)}</span>}
      {r.waitType && <span style={{ color: waitColorVar(r.waitMs) }}>{r.waitType} {ms(r.waitMs)}</span>}
      {"childCount" in item && item.childCount > 0 && <span className="text-err">blocks {item.childCount}</span>}
      {elsewhere != null && (
        <span className="text-muted" data-testid="tree-elsewhere">blocked sessions shown under {elsewhere > 0 ? `session ${elsewhere}` : `blocker ${elsewhere}`}</span>
      )}
      {item.inCycle && <span className="text-err">in cycle</span>}
      {r.querithmKind && <span className="text-accent">{own ? `your tab: ${own}` : r.querithmKind === "pool" ? "Querithm pool" : `Querithm (${r.querithmKind})`}</span>}
      <span className="text-muted truncate flex-1">{text ? escapeForDisplay(text) : ""}</span>
      <span className="flex gap-1 shrink-0">
        <button className="q-btn !h-[18px] !px-1.5 text-[10px]" onClick={() => actions.detail(r)}>Detail</button>
        <button className="q-btn !h-[18px] !px-1.5 text-[10px]" onClick={() => actions.diagnose(r)}>Why?</button>
        {term.goToSession ? (
          <button className="q-btn !h-[18px] !px-1.5 text-[10px]" title={term.reason} onClick={() => goToSession(term.goToSession!)}>Go to tab</button>
        ) : (
          <>
            {/* Unavailable: focusable with the reason in the tooltip; Kill is red only when it can be used. */}
            <button className="q-btn !h-[18px] !px-1.5 text-[10px]" aria-disabled={!cancel.enabled || undefined} title={cancel.reason ?? "Cancel the running query"} onClick={() => cancel.enabled && actions.kill(r, "cancelRequest")}>Cancel</button>
            <button className={`q-btn !h-[18px] !px-1.5 text-[10px] ${term.enabled ? "text-err" : ""}`} aria-disabled={!term.enabled || undefined} title={term.reason ?? "Terminate the session"} onClick={() => term.enabled && actions.kill(r, "terminateSession")}>Kill</button>
          </>
        )}
        <button className="q-btn !h-[18px] !px-1.5 text-[10px]" disabled={!text} onClick={() => text && void navigator.clipboard.writeText(text)}>Copy SQL</button>
      </span>
    </div>
  );
}

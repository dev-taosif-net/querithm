import { BlockingBanner } from "../monitor/BlockingBanner";
import { useEffect, useRef, useState } from "react";
import { useShallow } from "zustand/react/shallow";
import type { IDockviewPanelHeaderProps, IDockviewPanelProps } from "dockview";
import { useTabs, type TabState } from "../store/tabs";
import { envColor, useProfiles } from "../store/profiles";
import { useSettings } from "../store/settings";
import { DIALECTS, dialectOf } from "../engines";
import { useConnections } from "../store/connections";
import { attachEditor, getModel, onModelContent } from "../editor/editors";
import { detectUse } from "../editor/completion";
import { applyProcedure, changeDatabase, closeTab, compareWithServer, keepPreview, toggleBottom, toggleMaximize } from "./actions";
import { BottomPanel } from "./BottomPanel";
import { bandColor, useThemeKey } from "./tabStyle";
import { Icon } from "./icons";
import { TabLine1, TabLine2, tooltipText, useFullDatabase, useTabSemantics, type TabFacts } from "./TabIdentity";
import { TabContextMenu } from "./TabMenu";
import { folderSuffix, tabTitleText } from "./tabTitle";
import { middleClickClose } from "./tabActions";


type Params = { tabId: string };

/** Keyboard steps of the panel separator (fraction of the tab). */
export const PANEL_MIN = 0.12;
export const PANEL_MAX = 0.85;
const PANEL_STEP = 0.05;

export function QueryTabPanel(props: IDockviewPanelProps<Params>) {
  const tabId = props.params.tabId;
  const tab = useTabs((s) => s.tabs[tabId]);
  const dock = useSettings((s) => s.panelDock);
  const slot = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(props.api.isVisible);
  const [groupId, setGroupId] = useState(props.api.group.id);

  useEffect(() => {
    const a = props.api.onDidVisibilityChange((e) => setVisible(e.isVisible));
    const b = props.api.onDidGroupChange(() => setGroupId(props.api.group.id));
    return () => {
      a.dispose();
      b.dispose();
    };
  }, [props.api]);

  useEffect(() => {
    if (!visible || !slot.current || !tab) return;
    const editor = attachEditor(groupId, slot.current, tabId, !!tab.readOnly);
    // The active tab's editor takes the keyboard (new tab, tab switch), unless a dialog or input owns it.
    if (useTabs.getState().activeId === tabId && !(document.activeElement instanceof HTMLInputElement)) editor.focus();
  }, [visible, groupId, tabId, tab?.readOnly, !!tab]);

  if (!tab) return null;
  const right = dock === "right";
  const max = tab.bottomOpen && !!tab.bottomMax;
  const setSize = (frac: number) => useTabs.getState().update(tabId, { bottomSize: Math.min(PANEL_MAX, Math.max(PANEL_MIN, frac)), bottomOpen: true });

  const startDrag = (e: React.MouseEvent) => {
    e.preventDefault();
    const rect = root.current!.getBoundingClientRect();
    const move = (ev: MouseEvent) => setSize(right ? 1 - (ev.clientX - rect.left) / rect.width : 1 - (ev.clientY - rect.top) / rect.height);
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  };
  // The separator is a focusable WAI-ARIA separator: arrows grow or shrink the panel by 5 %, Home/End go to the
  // limits, Enter hides it (Ctrl+R brings it back).
  const separatorKey = (e: React.KeyboardEvent) => {
    const grow = right ? "ArrowLeft" : "ArrowUp";
    const shrink = right ? "ArrowRight" : "ArrowDown";
    let to: number | null = null;
    if (e.key === grow) to = tab.bottomSize + PANEL_STEP;
    else if (e.key === shrink) to = tab.bottomSize - PANEL_STEP;
    else if (e.key === "Home") to = PANEL_MIN;
    else if (e.key === "End") to = PANEL_MAX;
    else if (e.key === "Enter") {
      e.preventDefault();
      toggleBottom(tabId);
      return;
    }
    if (to == null) return;
    e.preventDefault();
    setSize(to);
  };
  const size = `${tab.bottomSize * 100}%`;

  return (
    <div ref={root} className={`q-query-page h-full flex ${right ? "flex-row" : "flex-col"} bg-bg overflow-hidden`} data-q-dock={right ? "right" : "bottom"}>
      <div
        className={`relative flex flex-col min-h-0 min-w-0 overflow-hidden ${max ? "shrink-0" : "flex-1"}`}
        style={max ? (right ? { width: 32 } : { height: 28 }) : undefined}
        data-q-editor-column
      >
        <ContextBar tab={tab} />
        <BlockingBanner tab={tab} />
        <div className="flex-1 relative min-h-0" ref={slot} />
        {max && <EditorStrip tabId={tabId} vertical={right} />}
      </div>
      {tab.bottomOpen && (
        <>
          {!max && (
            <div
              role="separator"
              aria-orientation={right ? "vertical" : "horizontal"}
              aria-label="Resize the results panel"
              aria-valuemin={Math.round(PANEL_MIN * 100)}
              aria-valuemax={Math.round(PANEL_MAX * 100)}
              aria-valuenow={Math.round(tab.bottomSize * 100)}
              tabIndex={0}
              className={right
                ? "w-[5px] -ml-[2px] -mr-[3px] z-10 cursor-col-resize hover:bg-accent/60 focus-visible:bg-accent outline-none"
                : "h-[5px] -mt-[2px] -mb-[3px] z-10 cursor-row-resize hover:bg-accent/60 focus-visible:bg-accent outline-none"}
              onMouseDown={startDrag}
              onDoubleClick={() => toggleBottom(tabId)}
              onKeyDown={separatorKey}
            />
          )}
          <div
            data-focus-region="results"
            className={`bg-panel flex flex-col min-h-0 min-w-0 ${max ? "flex-1" : "shrink-0"} ${right ? "border-l border-line" : "border-t border-line"}`}
            style={max ? undefined : right ? { width: size, minWidth: 420 } : { height: size }}
          >
            <BottomPanel tab={tab} />
          </div>
        </>
      )}
    </div>
  );
}

/** The editor while the panel is maximized: its first line and Restore, in one 28 px strip. */
function EditorStrip({ tabId, vertical }: { tabId: string; vertical: boolean }) {
  const model = getModel(tabId);
  const first = model ? (model.getValue().split("\n").find((l) => l.trim()) ?? "").trim() : "";
  if (vertical) {
    return (
      <button type="button" className="absolute inset-0 z-10 bg-panel border-r border-line flex items-center justify-center text-muted hover:text-fg" title="Restore the editor (Ctrl+Shift+R)" aria-label="Restore the editor" onClick={() => toggleMaximize(tabId)}>
        <Icon name="restore" size={14} strokeWidth={2} />
      </button>
    );
  }
  return (
    <div className="absolute inset-0 z-10 bg-panel border-b border-line px-3 flex items-center gap-3 text-[11.5px]" data-q-editor-strip>
      <span className="text-muted shrink-0">Editor</span>
      <span className="q-mono text-fg truncate min-w-0">{first || "(empty)"}</span>
      <button type="button" className="q-btn h-[20px] ml-auto shrink-0" onClick={() => toggleMaximize(tabId)} title="Restore the editor (Ctrl+Shift+R)">
        <Icon name="restore" size={12} strokeWidth={2} />Restore
      </button>
    </div>
  );
}

function ContextBar({ tab }: { tab: TabState }) {
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === tab.profileId));
  const [useDb, setUseDb] = useState<string | undefined>();

  useEffect(() => {
    // Only a dialect whose USE switches the database shows the database the script moves to.
    const dialect = profile?.engine ? dialectOf(profile.engine) : undefined;
    if (!dialect || !DIALECTS[dialect].useStatement) return;
    let timer: ReturnType<typeof setTimeout>;
    const check = () => {
      const m = getModel(tab.id);
      setUseDb(m ? detectUse(m.getValue(), dialect) : undefined);
    };
    check();
    const off = onModelContent((id) => {
      if (id !== tab.id) return;
      clearTimeout(timer);
      timer = setTimeout(check, 400);
    });
    return () => {
      off();
      clearTimeout(timer);
    };
  }, [tab.id, profile?.engine]);

  const b = tab.boundObject;
  const prod = profile?.environment === "prod";
  const showUse = useDb && tab.database && useDb.toLowerCase() !== tab.database.toLowerCase();
  if (!b && !tab.readOnly && !showUse) return null;
  return (
    <div className={`q-banner-in h-[30px] border-b border-line px-3.5 flex items-center justify-between text-xs shrink-0 ${prod ? "bg-err/10 q-hl" : "bg-panel-2"}`}>
      <div className="flex items-center gap-2 min-w-0">
        {b && (
          <>
            <span className={`px-1.5 py-0.5 rounded text-[10px] font-bold tracking-wide border ${prod ? "bg-err/15 border-err/40 text-err" : "bg-accent/15 border-accent/40 text-accent"}`}>
              BOUND {b.kind === "proc" ? "STORED PROCEDURE" : b.kind === "view" ? "VIEW" : "FUNCTION"}
            </span>
            <span className="q-mono text-fg font-medium truncate">
              {b.database}.{b.schema}.{b.name}
            </span>
            <span className="text-[11px] text-muted truncate">definition hash {b.definitionHash.slice(0, 8)}</span>
          </>
        )}
        {tab.readOnly && <span className="text-muted">Read-only definition — use Modify in the explorer to edit.</span>}
        {showUse && (
          <span className="text-warn">
            USE <b>{useDb}</b> in this script differs from the tab's database <b>{tab.database}</b>.
            <button className="ml-2 q-btn h-[20px]" onClick={() => void changeDatabase(tab.id, useDb!)}>
              Switch tab to {useDb}
            </button>
          </span>
        )}
      </div>
      {b && (
        <div className="flex items-center gap-2 shrink-0">
          <button className="q-btn h-[22px]" onClick={() => void compareWithServer(tab.id)}>⇄ Compare with server</button>
          <button className="q-btn q-btn-primary h-[22px]" onClick={() => void applyProcedure(tab.id)}>✓ Apply (Ctrl+Shift+S)</button>
        </div>
      )}
    </div>
  );
}

/** Fields of a tab its header shows. Results, executions and the bottom panel change often and never re-render it (D26). */
function headerFields(t: TabState | undefined) {
  if (!t) return undefined;
  return {
    title: t.title, profileId: t.profileId, database: t.database, busy: t.busy, txOpen: t.txState === "open", txStartedAt: t.txStartedAt,
    spid: t.spid, dirty: t.dirty, readOnly: !!t.readOnly, pinned: !!t.pinned, filePath: t.filePath, preview: !!t.preview, notebook: !!t.notebook,
  };
}

/** Renders of tab headers (tests: switching tabs and run bookkeeping must not re-render them). */
export const headerRenders = { count: 0 };

/**
 * Tab header (§12, strip redesign): two lines from TabIdentity, a Prod-only band, and the close slot. Active, hover and
 * split states are CSS on Dockview's classes, so activation re-renders nothing here. The accessible name goes on
 * Dockview's tab element, which is what takes focus.
 */
export function QueryTabHeader(props: IDockviewPanelHeaderProps<Params>) {
  headerRenders.count++;
  const tabId = props.params.tabId;
  const v = useTabs(useShallow((s) => headerFields(s.tabs[tabId])));
  const suffix = useTabs((s) => folderSuffix(s.tabs, tabId));
  const profile = useProfiles((s) => (v?.profileId ? s.profiles.find((p) => p.id === v.profileId) : undefined));
  const groups = useProfiles((s) => s.groups);
  const disconnected = useConnections((s) => !!v?.profileId && !!s.disconnected[v.profileId]);
  useThemeKey();
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const [editing, setEditing] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const tabEl = useRef<HTMLElement | null>(null);
  const facts: TabFacts | undefined = v && { kind: "query", ...v, profile, disconnected: !!profile && disconnected, suffix };
  useTabSemantics(root, tabEl, facts, setMenu, () => setEditing(true));
  useFullDatabase(root, tabEl, `${profile?.id ?? ""}|${facts?.database ?? ""}|${facts?.disconnected ?? false}`);
  if (!v || !facts) return null;
  const finishRename = (value: string | null) => {
    setEditing(false);
    if (value != null && useTabs.getState().rename(tabId, value)) props.api.setTitle(useTabs.getState().tabs[tabId].title);
    // Focus goes back to the tab (D22), whichever way the rename started.
    tabEl.current?.focus();
  };
  const band = profile?.environment === "prod" ? bandColor(envColor(profile, groups)) : undefined;
  return (
    <div
      ref={root}
      className="q-tab"
      data-pinned={v.pinned || undefined}
      data-disc={facts.disconnected || undefined}
      data-dirty={v.dirty || undefined}
      data-editing={editing || undefined}
      title={tooltipText(facts)}
      // The transaction's age in the tooltip is read when the pointer arrives, not when the header last rendered.
      onPointerEnter={(e) => void (e.currentTarget.title = tooltipText(facts))}
      onContextMenu={(e) => {
        e.preventDefault();
        e.stopPropagation();
        setMenu({ x: e.clientX, y: e.clientY });
      }}
      onMouseDown={(e) => {
        if (e.button === 1) {
          e.preventDefault();
          middleClickClose(tabId);
        }
      }}
    >
      {band && <span className="q-tab-band" style={{ background: band }} aria-hidden />}
      <span className="q-tab-sep" aria-hidden />
      {/* Double-click renames; on a preview tab it keeps the tab instead (D25), as editors do. */}
      <span className="q-tab-main" onDoubleClick={(e) => { e.stopPropagation(); if (v.preview) keepPreview(tabId); else setEditing(true); }}>
        {/* The close slot sits at the end of line 1 (D12), so line 2 runs the tab's full width. */}
        <span className="q-tab-top">
          {editing ? <TitleEditor initial={v.title} onDone={finishRename} /> : <TabLine1 f={facts} />}
          <span className="q-tab-slot" onDoubleClick={(e) => e.stopPropagation()}>
            {v.pinned ? (
              <span aria-label="Pinned" className="flex"><Icon name="pin" size={13} strokeWidth={1.9} /></span>
            ) : (
              <>
                {v.dirty && <span className="q-tab-dot" aria-hidden />}
                <button
                  type="button"
                  // Not a Tab stop: Delete closes the focused tab from the keyboard (D12).
                  tabIndex={-1}
                  className="q-tab-x"
                  onClick={(e) => {
                    e.stopPropagation();
                    void closeTab(tabId);
                  }}
                  title="Close (Ctrl+W)"
                  aria-label={`Close ${tabTitleText(v.title)}`}
                >
                  <Icon name="close" size={12} strokeWidth={2.2} />
                </button>
              </>
            )}
          </span>
        </span>
        <TabLine2 f={facts} fullDatabase />
      </span>
      {menu && <TabContextMenu tabId={tabId} at={menu} anchor={tabEl} onClose={() => setMenu(null)} onRename={() => setEditing(true)} />}
    </div>
  );
}

/** Inline rename in the tab header: Enter or leaving the field keeps the name, Escape cancels. */
function TitleEditor({ initial, onDone }: { initial: string; onDone: (value: string | null) => void }) {
  const input = useRef<HTMLInputElement>(null);
  const done = useRef(false);
  const finish = (v: string | null) => {
    if (done.current) return;
    done.current = true;
    onDone(v);
  };
  useEffect(() => {
    const el = input.current;
    if (!el) return;
    el.focus();
    // Select the name without its extension, as file renames do.
    const dot = initial.lastIndexOf(".");
    el.setSelectionRange(0, dot > 0 ? dot : initial.length);
    // Dockview tabs are HTML5-draggable: while typing, a drag must select text, not move the tab.
    const tabEl = el.closest<HTMLElement>(".dv-tab");
    const draggable = tabEl?.draggable;
    if (tabEl) tabEl.draggable = false;
    return () => void (tabEl && (tabEl.draggable = !!draggable));
  }, [initial]);
  return (
    <input
      ref={input}
      aria-label="Tab name"
      className="q-input h-[18px] w-full min-w-0 text-xs px-1"
      defaultValue={initial}
      maxLength={120}
      // The header is a drag handle and closes on middle click: keep pointer and key events in the field.
      onMouseDown={(e) => e.stopPropagation()}
      onPointerDown={(e) => e.stopPropagation()}
      onDoubleClick={(e) => e.stopPropagation()}
      onKeyDown={(e) => {
        e.stopPropagation();
        if (e.key === "Enter") finish(e.currentTarget.value);
        if (e.key === "Escape") finish(null);
      }}
      onBlur={(e) => finish(e.currentTarget.value)}
    />
  );
}


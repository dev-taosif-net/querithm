// Center region (§12): Dockview hosts query tabs; each tab carries its own bottom panel.
import { useEffect, useRef } from "react";
import { DockviewReact, type DockviewApi, type DockviewReadyEvent, type IDockviewHeaderActionsProps, type IWatermarkPanelProps } from "dockview";
import "dockview/dist/styles/dockview.css";
import { QueryTabHeader, QueryTabPanel } from "./QueryTab";
import { ToolTabHeader, ToolTabPanel } from "./ToolTab";
import { NotebookTabPanel } from "./NotebookTabPanel";
import { useToolTabs } from "../store/toolTabs";
import { useShallow } from "zustand/react/shallow";
import { useTabs, type TabState } from "../store/tabs";
import { activateTab, closeTab, workspaceHooks, newQueryTab } from "./actions";
import { closeTool } from "./toolActions";
import { focusedTabId, handleStripKey, syncStripAria, tabElement, type StripActions } from "./stripKeyboard";
import { announceTxLeftBehind } from "./announcements";
import { disposeGroupEditor, editorForTab } from "../editor/editors";
import { useUi } from "../store/ui";
import { useProfiles } from "../store/profiles";
import { Logo } from "./Logo";
import { applyPinnedOrder, moveTabBy } from "./tabActions";
import { newTabIndex } from "./tabNav";
import { useStripLayout } from "./hiddenTabs";
import { Icon } from "./icons";
import { displayKeys, keysFor, commands } from "./commands";
import { withBoundary } from "./ErrorBoundary";

// M17: one panel's render error shows "This panel failed" in that panel; the shell and the other tabs keep working.
const components = { query: withBoundary(QueryTabPanel, "Query tab"), tool: withBoundary(ToolTabPanel, "Tool tab"), notebook: withBoundary(NotebookTabPanel, "Notebook") };
const tabComponents = { query: withBoundary(QueryTabHeader, "Tab header", "tab"), tool: withBoundary(ToolTabHeader, "Tab header", "tab") };
/** What the strip's keys do (D22). */
const stripActions: StripActions = {
  activate: (id) => activateTab(id),
  close: async (id) => {
    if (useToolTabs.getState().tools[id]) {
      closeTool(id);
      return true;
    }
    return closeTab(id);
  },
  move: (id, delta) => moveTabBy(id, delta),
  focusEditor: () => {
    const id = useTabs.getState().activeId;
    if (id) editorForTab(id)?.focus();
  },
};
const theme = { name: "querithm", className: "dockview-theme-querithm", gap: 0 };

function Watermark(_: IWatermarkPanelProps) {
  const profiles = useProfiles((s) => s.profiles);
  return (
    <div className="h-full flex items-center justify-center bg-bg">
      <div className="text-center space-y-4 max-w-md">
        <Logo tile className="w-16 h-16 mx-auto" />
        <div className="text-2xl font-bold text-fg tracking-tight" data-contrast="logotype">
          Que<span className="text-[#38BDF8]">rithm</span>
        </div>
        <div className="text-muted text-xs">A SQL IDE for SQL Server and PostgreSQL.</div>
        <div className="flex gap-2 justify-center">
          <button className="q-btn q-btn-primary" onClick={() => newQueryTab()}>+ New Query <span className="q-kbd text-accent-fg">Ctrl+N</span></button>
          <button className="q-btn" onClick={() => useUi.getState().openDialog({ kind: profiles.length ? "connect" : "profile" })}>
            {profiles.length ? "Connect…" : "+ New connection profile"}
          </button>
        </div>
        <div className="text-[11px] text-muted space-y-0.5 pt-2">
          <div>Command palette <span className="q-kbd">Ctrl+Shift+P</span> · Go to object <span className="q-kbd">Ctrl+T</span></div>
          <div>Run <span className="q-kbd">F5</span> · Run statement <span className="q-kbd">Ctrl+Shift+Enter</span> · Cancel <span className="q-kbd">Ctrl+Shift+C</span></div>
        </div>
      </div>
    </div>
  );
}

const isPinnedId = (id: string) => !!useTabs.getState().tabs[id]?.pinned;

/** Of the hidden tabs: the first holding an open transaction, and how many are running. */
function hiddenStatus(tabs: Record<string, TabState>, hidden: string[]): { tx?: string; busy: number } {
  let tx: string | undefined;
  let busy = 0;
  for (const id of hidden) {
    const t = tabs[id];
    if (!t) continue;
    if (!tx && t.txState === "open") tx = id;
    if (t.busy) busy++;
  }
  return { tx, busy };
}

/**
 * Right end of each tab strip, always there and 48 px wide (D14): "≡ N" counts the tabs out of view (query and tool
 * tabs) and lists them; under it, the status of hidden tabs, so an open transaction or a running query is never out
 * of sight: a TX pill that goes to that tab, and a count of running ones. It also lays the strip out (sticky pins,
 * fades, the active tab kept in view).
 */
function StripEnd({ panels, group, activePanel }: IDockviewHeaderActionsProps) {
  const hidden = useStripLayout(group, panels, isPinnedId, activePanel?.id);
  const status = useTabs(useShallow((s) => hiddenStatus(s.tabs, hidden)));
  const list = commands.find((c) => c.id === "tab.list");
  const key = list ? keysFor(list)[0] : undefined;
  const n = hidden.length;
  const label = n ? `${n} hidden tab${n === 1 ? "" : "s"}` : "All open tabs";
  return (
    <div className="q-strip-end" data-q-strip-end>
      <button
        type="button"
        className="q-strip-menu"
        data-hidden={n ? "" : undefined}
        aria-haspopup="dialog"
        aria-label={label}
        title={`${label}${key ? ` · all open tabs: ${displayKeys(key)}` : ""} · Ctrl+Tab switches to recent tabs`}
        onClick={() => useUi.getState().setPalette(n ? { mode: "tabs", only: hidden } : { mode: "tabs" })}
      >
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" aria-hidden="true"><path d="M4 6h16M4 12h16M4 18h10" /></svg>
        {n > 0 && <span>{n}</span>}
      </button>
      {(status.tx || status.busy > 0) && (
        <span className="flex items-center gap-[3px]">
          {status.tx && (
            <button
              type="button"
              className="q-tx-pill q-strip-tx"
              aria-label="Go to the hidden tab with an open transaction"
              title="Go to the hidden tab with an open transaction"
              onClick={() => activateTab(status.tx!)}
            >
              TX
            </button>
          )}
          {status.busy > 0 && (
            <span className="q-strip-busy" title={`${status.busy} hidden tab${status.busy === 1 ? "" : "s"} running`} aria-label={`${status.busy} hidden tab${status.busy === 1 ? "" : "s"} running`}>
              <Icon name="spinner" size={10} strokeWidth={2.6} className="q-spin" />
              {status.busy}
            </span>
          )}
        </span>
      )}
    </div>
  );
}

export function Workspace() {
  const api = useRef<DockviewApi | null>(null);

  const onReady = (e: DockviewReadyEvent) => {
    api.current = e.api;
    let ready = false;
    let justOpenedId: string | undefined;
    const canAnimate = () =>
      typeof Element !== "undefined" &&
      typeof Element.prototype.animate === "function" &&
      !window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches;
    const snapshotTabPositions = (group: { panels: { view?: { tab?: { element: HTMLElement } } }[] } | undefined) => {
      const map = new Map<HTMLElement, number>();
      if (!ready || !group || !canAnimate()) return map;
      for (const x of group.panels) {
        if (x.view?.tab) {
          const el = tabElement(x as any);
          map.set(el, el.getBoundingClientRect().left);
        }
      }
      return map;
    };
    const animateOpenedPanel = (id: string, activate: boolean, before: Map<HTMLElement, number>) => {
      if (!ready || !canAnimate()) return;
      const p = e.api.getPanel(id);
      if (!p) return;
      for (const [el, oldLeft] of before) {
        const dx = oldLeft - el.getBoundingClientRect().left;
        if (Math.abs(dx) > 2) {
          el.animate([{ transform: `translateX(${dx}px)` }, { transform: "none" }], {
            duration: 220,
            easing: "cubic-bezier(0.16, 1, 0.3, 1)",
          });
        }
      }
      if (p.view?.tab) {
        tabElement(p).animate(
          [
            { opacity: 0, transform: "translateY(-8px) scale(0.95)" },
            { opacity: 1, transform: "none" },
          ],
          { duration: 220, easing: "cubic-bezier(0.16, 1, 0.3, 1)" },
        );
      }
      if (activate && p.view?.content?.element) {
        justOpenedId = id;
        (p.view.content.element as HTMLElement).animate(
          [
            { opacity: 0, transform: "translateY(-10px) scale(0.992)" },
            { opacity: 1, transform: "none" },
          ],
          { duration: 220, easing: "cubic-bezier(0.16, 1, 0.3, 1)" },
        );
      }
    };
    const add = (id: string, activate: boolean, atEnd = false) => {
      const t = useTabs.getState().tabs[id];
      if (!t || e.api.getPanel(id)) return;
      // Without a position and with no active group (startup, before the restored active tab), Dockview adds a new
      // group at the left edge, and an inactive panel then sits alone in a split showing the watermark. Always open in
      // an existing group; the first panel of an empty workspace is shown, since a group needs a visible panel.
      const group = e.api.activeGroup ?? e.api.groups[0];
      const before = snapshotTabPositions(group);
      e.api.addPanel({
        id, component: t.notebook ? "notebook" : "query", tabComponent: "query", params: { tabId: id }, title: t.title,
        inactive: !activate && !!group, position: group ? { referenceGroup: group } : undefined,
      });
      if (t.pinned) applyPinnedOrder(id);
      else if (!atEnd) openFirst(id);
      if (!atEnd) animateOpenedPanel(id, activate, before);
    };
    /** New tabs open on the left, right after the pinned ones, where they are easy to find (D16: tool tabs too). */
    const openFirst = (id: string) => {
      const tabs = useTabs.getState().tabs;
      const order = workspaceHooks.stripOrder!(id);
      const index = newTabIndex(order.filter((x) => x !== id), (x) => !!tabs[x]?.pinned);
      if (order.indexOf(id) !== index) workspaceHooks.moveTab!(id, index);
    };
    workspaceHooks.onTabAdded = add;
    workspaceHooks.onToolAdded = (id, title) => {
      if (e.api.getPanel(id)) return;
      const group = e.api.activeGroup ?? e.api.groups[0];
      const before = snapshotTabPositions(group);
      e.api.addPanel({ id, component: "tool", tabComponent: "tool", params: { toolId: id }, title, position: group ? { referenceGroup: group } : undefined });
      openFirst(id);
      animateOpenedPanel(id, true, before);
    };
    workspaceHooks.onTabRemoved = (id) => {
      const p = e.api.getPanel(id);
      if (p) e.api.removePanel(p);
    };
    workspaceHooks.activate = (id) => e.api.getPanel(id)?.api.setActive();
    // Strip order holds query and tool tabs alike (D17): positions are Dockview's, so moves land where they should.
    workspaceHooks.stripOrder = (tabId) => {
      const g = (tabId ? e.api.getPanel(tabId)?.group : undefined) ?? e.api.activeGroup;
      return g ? g.panels.map((p) => p.id) : useTabs.getState().order;
    };
    workspaceHooks.allOrder = () => e.api.groups.flatMap((g) => g.panels.map((p) => p.id));
    workspaceHooks.focusedTab = () => focusedTabId(e.api);
    workspaceHooks.placeOf = (id) => {
      const p = e.api.getPanel(id);
      return p ? { groupId: p.group.id, index: p.group.panels.indexOf(p) } : undefined;
    };
    workspaceHooks.place = (id, groupId, index) => {
      const p = e.api.getPanel(id);
      if (!p) return;
      const group = e.api.groups.find((g) => g.id === groupId) ?? p.group;
      const tabs = useTabs.getState().tabs;
      // Never in front of a pinned tab: past the pins at least.
      const pins = group.panels.filter((x) => x !== p && tabs[x.id]?.pinned).length;
      const at = Math.min(Math.max(index, tabs[id]?.pinned ? 0 : pins), group.panels.filter((x) => x !== p).length);
      if (group === p.group && group.panels.indexOf(p) === at) return;
      p.api.moveTo({ group, index: at, skipSetActive: true });
    };
    workspaceHooks.splitRight = (id) => {
      const p = e.api.getPanel(id);
      if (p) p.api.moveTo({ group: p.group, position: "right" });
    };
    workspaceHooks.focusTab = (id) => {
      const p = e.api.getPanel(id);
      if (!p) return;
      const el = tabElement(p);
      for (const x of p.group.panels) tabElement(x).tabIndex = x === p ? 0 : -1;
      el.focus();
    };
    // Tabs semantics (D22): roles and one Tab stop per strip, kept in step with Dockview after every change.
    let ariaFrame = 0;
    const sync = () => {
      if (ariaFrame) return;
      ariaFrame = requestAnimationFrame(() => {
        ariaFrame = 0;
        syncStripAria(e.api);
      });
    };
    for (const on of [e.api.onDidAddPanel, e.api.onDidRemovePanel, e.api.onDidMovePanel, e.api.onDidActiveGroupChange, e.api.onDidAddGroup, e.api.onDidLayoutChange]) on?.call(e.api, sync);
    sync();
    stripApi.current = e.api;
    workspaceHooks.moveTab = (id, index) => {
      const active = e.api.activePanel?.id;
      e.api.getPanel(id)?.api.moveTo({ index, skipSetActive: true });
      if (active && e.api.activePanel?.id !== active) e.api.getPanel(active)?.api.setActive();
    };
    // A tool tab (diagram, compare) is not a query tab: query commands then have no active tab.
    let lastActiveIndex = -1;
    e.api.onDidActivePanelChange((p) => {
      const tool = !!p && !!useToolTabs.getState().tools[p.id];
      const before = useTabs.getState().activeId;
      useToolTabs.getState().setActive(tool ? p!.id : undefined);
      useTabs.getState().setActive(tool ? undefined : p?.id);
      // Ctrl+Tab lists tool tabs too (D17): they join the recent-tabs list without becoming the active query tab.
      if (tool) useTabs.getState().touch(p!.id);
      if (tool && before) announceTxLeftBehind(before);
      const nextIndex = p?.group ? p.group.panels.indexOf(p) : -1;
      if (ready && p && p.id !== justOpenedId && p.view?.content?.element && canAnimate()) {
        const dir = lastActiveIndex >= 0 && nextIndex >= 0 && nextIndex !== lastActiveIndex ? (nextIndex > lastActiveIndex ? 14 : -14) : 0;
        (p.view.content.element as HTMLElement).animate(
          [
            { opacity: 0.35, transform: dir ? `translateX(${dir}px)` : "translateY(-6px)" },
            { opacity: 1, transform: "none" },
          ],
          { duration: 180, easing: "cubic-bezier(0.22, 1, 0.36, 1)" },
        );
      }
      lastActiveIndex = nextIndex;
      justOpenedId = undefined;
      sync();
    });
    // A drag can drop a tab in front of pinned ones: put the pinned tabs back at the front.
    let reordering = false;
    e.api.onDidMovePanel(({ panel }) => {
      if (reordering) return;
      reordering = true;
      try {
        applyPinnedOrder(panel.id);
        // Keep the store in on-screen order, so the saved workspace restores tabs where the user put them.
        const shown = e.api.groups.flatMap((g) => g.panels.map((p) => p.id));
        useTabs.setState((st) => ({ order: [...shown.filter((id) => st.tabs[id]), ...st.order.filter((id) => !shown.includes(id))] }));
      } finally {
        reordering = false;
      }
    });
    e.api.onDidRemoveGroup((g) => disposeGroupEditor(g.id));
    // Tabs restored before the workspace mounted.
    const s = useTabs.getState();
    for (const id of s.order) add(id, id === s.activeId, true);
    applyPinnedOrder();
    ready = true;
  };

  // Keys on a focused strip tab (D22): one listener for every strip.
  const stripApi = useRef<DockviewApi | null>(null);
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = root.current;
    if (!el) return;
    const onKey = (ev: KeyboardEvent) => {
      if (stripApi.current) handleStripKey(ev, stripApi.current, stripActions);
    };
    el.addEventListener("keydown", onKey);
    return () => el.removeEventListener("keydown", onKey);
  }, []);

  useEffect(() => () => {
    workspaceHooks.focusedTab = undefined;
    workspaceHooks.focusTab = undefined;
    workspaceHooks.splitRight = undefined;
    workspaceHooks.placeOf = undefined;
    workspaceHooks.place = undefined;
    workspaceHooks.onTabAdded = undefined;
    workspaceHooks.onToolAdded = undefined;
    workspaceHooks.onTabRemoved = undefined;
    workspaceHooks.activate = undefined;
    workspaceHooks.stripOrder = undefined;
    workspaceHooks.allOrder = undefined;
    workspaceHooks.moveTab = undefined;
  }, []);

  return (
    <div ref={root} className="h-full w-full">
    <DockviewReact
      className="h-full w-full"
      components={components}
      tabComponents={tabComponents}
      watermarkComponent={Watermark}
      rightHeaderActionsComponent={StripEnd}
      disableTabsOverflowList
      onReady={onReady}
      theme={theme}
      disableFloatingGroups
      singleTabMode="default"
      defaultRenderer="onlyWhenVisible"
    />
    </div>
  );
}

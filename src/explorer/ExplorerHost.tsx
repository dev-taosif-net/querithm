// Hosts the Object Explorer as a Visual Studio-style tool window (§12): docked, auto-hide (a side tab
// whose panel flies out over the workspace without resizing it) or closed. The Explorer stays mounted
// in every state so its expanded tree, search and scroll survive.
import { useEffect, useRef } from "react";
import { useConnections } from "../store/connections";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { useSettings } from "../store/settings";
import { Icon } from "../app/icons";
import { menuTriggerOf } from "../app/ToolbarMenu";
import { shownServers } from "./tree";
import { useTabs } from "../store/tabs";
import { useUi } from "../store/ui";
import { editorForTab } from "../editor/editors";
import { Explorer } from "./Explorer";
import { EXPLORER_MAX, EXPLORER_MIN, explorerAction, explorerHooks, shownExplorerWidth, useExplorerWidth } from "./explorerWindow";
import type { ExplorerAction } from "./layoutState";

const RAIL = 22;
const HOVER_OPEN_MS = 300;
const HOVER_CLOSE_MS = 500;
const SLIDE_MS = 120;

function focusEditor() {
  const id = useTabs.getState().activeId;
  if (id) editorForTab(id)?.focus();
}

export function ExplorerHost() {
  const visible = useSettings((s) => s.explorerVisible);
  const autoHide = useSettings((s) => s.explorerAutoHide);
  const hoverOpen = useSettings((s) => s.explorerHoverOpen);
  const reduced = useSettings((s) => s.reducedDecorations);
  const flyout = useUi((s) => s.explorerFlyout);
  const width = useExplorerWidth();
  const panel = useRef<HTMLDivElement>(null);
  const rail = useRef<HTMLDivElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);

  const pinned = visible && !autoHide;
  const docked = visible && autoHide;
  const out = docked && flyout;
  const schedule = (a: ExplorerAction | null, ms = 0) => {
    clearTimeout(timer.current);
    if (a) timer.current = setTimeout(() => explorerAction(a), ms);
  };
  useEffect(() => () => clearTimeout(timer.current), []);

  // Inside the flyout: the panel, its side tab, and a menu opened from the panel (portaled to the body).
  const inside = (n: Node | null) => !!n && (!!panel.current?.contains(n) || !!rail.current?.contains(n) || !!panel.current?.contains(menuTriggerOf(n)));

  // A click anywhere outside the flyout and its tab slides it back, like a Visual Studio auto-hide window.
  useEffect(() => {
    if (!out) return;
    const down = (e: PointerEvent) => {
      if (!inside(e.target as Node)) explorerAction("dismiss");
    };
    window.addEventListener("pointerdown", down, true);
    return () => window.removeEventListener("pointerdown", down, true);
  }, [out]);

  // Pointer over the tab or the flyout keeps it out; leaving both closes a hover-opened (not activated) flyout.
  const enter = (fromTab: boolean) => {
    if (out) schedule(null);
    else if (fromTab && hoverOpen) schedule("flyOut", HOVER_OPEN_MS);
  };
  const leave = () => {
    if (!out) schedule(null);
    else if (!inside(document.activeElement)) schedule("dismiss", HOVER_CLOSE_MS);
  };

  const setWidth = (px: number) => useUi.getState().setExplorer({ explorerWidth: Math.min(EXPLORER_MAX, Math.max(EXPLORER_MIN, Math.round(px))) });
  const dragWidth = (e: React.MouseEvent) => {
    e.preventDefault();
    const left = panel.current?.getBoundingClientRect().left ?? 0;
    const move = (ev: MouseEvent) => setWidth(ev.clientX - left);
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  };

  const floating: React.CSSProperties = {
    position: "absolute",
    top: 0,
    bottom: 0,
    // Slide with `left`, not `transform`: a transformed ancestor would re-anchor the Explorer's fixed context menu.
    left: out ? RAIL : -width - 8,
    visibility: out ? "visible" : "hidden",
    transition: reduced ? undefined : out ? `left ${SLIDE_MS}ms ease-out` : `left ${SLIDE_MS}ms ease-in, visibility 0s linear ${SLIDE_MS}ms`,
    zIndex: 30,
  };

  return (
    <>
      {docked && (
        <SideTab
          railRef={rail}
          active={out}
          onEnter={() => enter(true)}
          onLeave={leave}
          onClick={() => {
            schedule(null);
            // Clicking activates the window: open it and move focus in so it stays until focus leaves.
            if (out) explorerHooks.focusSearch?.();
            else explorerAction("show");
          }}
        />
      )}
      <div
        ref={panel}
        tabIndex={-1}
        aria-hidden={!pinned && !out}
        className={`outline-none ${pinned ? "relative shrink-0 min-w-0" : "shadow-2xl motion-reduce:transition-none"}`}
        style={pinned ? { width } : { ...floating, width }}
        onMouseEnter={() => enter(false)}
        onMouseLeave={leave}
        onBlur={() => {
          if (!out) return;
          // Deferred: focus has not moved yet. Window blur leaves activeElement in place, so it does not close.
          setTimeout(() => {
            const a = document.activeElement;
            if (a && a !== document.body && !inside(a)) explorerAction("dismiss");
          }, 0);
        }}
        onKeyDown={(e) => {
          if (!out || e.key !== "Escape") return;
          if (e.target instanceof HTMLInputElement && e.target.value) return; // Esc clears the search first
          explorerAction("dismiss");
          focusEditor();
        }}
        onDragStart={() => {
          // Let the drop reach the editor under the flyout; fold it once the drag ends.
          if (out && panel.current) panel.current.style.pointerEvents = "none";
        }}
        onDragEnd={() => {
          if (!panel.current) return;
          panel.current.style.pointerEvents = "";
          if (useUi.getState().explorerFlyout) explorerAction("dismiss");
        }}
      >
        <Explorer />
        {(pinned || out) && (
          // The resize edge is a focusable separator: ←/→ move it 16 px, Home/End go to the narrowest / widest dock.
          <div
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize the Object Explorer"
            aria-valuemin={EXPLORER_MIN}
            aria-valuemax={shownExplorerWidth(EXPLORER_MAX, window.innerWidth)}
            aria-valuenow={width}
            tabIndex={0}
            className="absolute top-0 bottom-0 -right-[2px] w-[4px] z-10 cursor-col-resize hover:bg-accent/60 focus-visible:bg-accent outline-none"
            onMouseDown={dragWidth}
            onKeyDown={(e) => {
              // From the width shown, so a key press never seems to do nothing when the window caps the dock.
              const to = e.key === "ArrowLeft" ? width - 16 : e.key === "ArrowRight" ? width + 16 : e.key === "Home" ? EXPLORER_MIN : e.key === "End" ? EXPLORER_MAX : null;
              if (to === null) return;
              e.preventDefault();
              setWidth(to);
            }}
          />
        )}
      </div>
    </>
  );
}

interface SideTabProps {
  railRef: React.RefObject<HTMLDivElement | null>;
  active: boolean;
  onEnter(): void;
  onLeave(): void;
  onClick(): void;
}

function SideTab({ railRef, active, onEnter, onLeave, onClick }: SideTabProps) {
  const order = useConnections((s) => s.order);
  const servers = useConnections((s) => s.servers);
  const profiles = useProfiles((s) => s.profiles);
  const groups = useProfiles((s) => s.groups);
  const pinned = useSettings((s) => s.pinnedProfiles);
  // One mark per server in use, in the tree's order, shaped by state (never color alone): a dot when connected, a
  // ring while connecting, a warning sign when it failed. Pinned profiles that are not connected have none.
  const marks = shownServers({ profiles, servers, order, pinned, showAll: false }).filter((p) => servers[p.id]);
  const count = (st: string) => marks.filter((p) => servers[p.id]?.status === st).length;
  const label = ["Object Explorer", count("connected") && `${count("connected")} connected`, count("connecting") && `${count("connecting")} connecting`, count("error") && `${count("error")} failed`]
    .filter(Boolean)
    .join(", ");
  return (
    <div ref={railRef} className="shrink-0 bg-panel border-r border-line flex flex-col items-center pt-1 gap-2" style={{ width: RAIL }}>
      {/* WebKit (Linux WebKitGTK, older macOS WKWebView) ignores writing-mode on <button>: the vertical text goes on a span. */}
      <button
        className={`w-full py-2 flex justify-center text-[11px] border-l-2 ${active ? "border-accent text-fg bg-raised" : "border-transparent text-muted hover:text-fg hover:bg-raised"}`}
        title="Object Explorer (Ctrl+B)"
        aria-label={label}
        aria-expanded={active}
        onMouseEnter={onEnter}
        onMouseLeave={onLeave}
        onClick={onClick}
      >
        <span className="block [writing-mode:vertical-rl]">Object Explorer</span>
      </button>
      {marks.map((p) => {
        const st = servers[p.id]!.status;
        const title = `${envLabel[p.environment]} ${p.name}, ${st === "error" ? "connection failed" : st}`;
        if (st === "error") return <span key={p.id} title={title} data-mark="error" className="text-err"><Icon name="alert" size={11} strokeWidth={2.25} /></span>;
        if (st === "connecting") return <span key={p.id} title={title} data-mark="connecting" className="w-[7px] h-[7px] rounded-full border-[1.5px]" style={{ borderColor: envColor(p, groups) }} />;
        // Prod is a shield, not only a red dot (never color alone, and red is also the failed server's color).
        if (p.environment === "prod") return <span key={p.id} title={title} data-mark="connected" style={{ color: envColor(p, groups) }}><Icon name="shield" size={11} strokeWidth={2.25} /></span>;
        return <span key={p.id} title={title} data-mark="connected" className="w-[7px] h-[7px] rounded-full ring-1 ring-line-strong" style={{ background: envColor(p, groups) }} />;
      })}
    </div>
  );
}

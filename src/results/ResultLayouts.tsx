// The Stacked and Side by side layouts of the Results panel (§12 bottom panel): the panes of several result sets,
// their captions, splitters and mount window. What a pane shows (grid or chart) and its tools come from the panel; the
// selected pane is the one the tool row, the footer, Edit and the cell viewer act on.
import { memo, useEffect, useRef, useState, type ReactNode } from "react";
import type { ArrowResultSet } from "./model";
import { Icon } from "../app/icons";
import { toast } from "../store/ui";
import { COLLAPSE_AFTER, fitHeight, HEIGHT_STEP, heightRange, mountedPanes } from "./resultLayout";

/** One result set on screen: of the shown run, or pinned from another kept run (read-only). */
export interface PaneModel {
  /** `${executionId}:${resultIndex}`. */
  key: string;
  executionId: string;
  rs: ArrowResultSet;
  /** A set of the shown run: it can be selected (and edited when it is the selected one). */
  current: boolean;
  pinned: boolean;
  /** "Run 2": the kept run a pinned set of another run comes from. */
  run?: string;
}

export type StackItem = { kind: "pane"; pane: PaneModel } | { kind: "error"; key: string; node: ReactNode };

// One formatter (toLocaleString() builds one per call: the top cost of 500 captions in the §14 metric 37 profile).
const NUMBER = new Intl.NumberFormat();
const fmt = (n: number) => NUMBER.format(n);
const rowsText = (n: number) => `${fmt(n)} ${n === 1 ? "row" : "rows"}`;

/** A pane's caption text: which set, its rows and its state; a pinned set of another run says where it is from. */
export function paneTitle(p: PaneModel): string {
  const rs = p.rs;
  const state = rs.interrupted ? " · stopped" : rs.truncated ? " · row limit" : rs.complete ? "" : " · streaming…";
  const from = p.current ? "" : `Pinned · ${p.run ?? "Older run"} · `;
  return `${from}Result set ${rs.index + 1} · ${rowsText(rs.rowCount)}${state}${p.current ? "" : " · read-only"}`;
}

interface PaneCallbacks {
  selectedKey?: string;
  /** Staged changes in the selected set: no other set can be selected until they are applied or discarded. */
  locked?: string;
  renderBody: (pane: PaneModel, selected: boolean) => ReactNode;
  onSelect: (pane: PaneModel) => void;
  /** The caption's buttons (pin, maximize, export). */
  tools: (pane: PaneModel) => ReactNode;
}

/**
 * Selecting a pane by pointer or keyboard focus. A locked panel keeps the selection: the other panes are dimmed, and
 * a click says why instead of switching.
 */
function useSelectHandlers(p: PaneModel, selected: boolean, cb: PaneCallbacks) {
  const pick = (e: { preventDefault(): void; stopPropagation(): void }) => {
    if (!p.current || selected) return;
    if (cb.locked) {
      e.preventDefault();
      e.stopPropagation();
      toast("info", cb.locked);
      return;
    }
    cb.onSelect(p);
  };
  return { onPointerDownCapture: pick, onFocusCapture: pick };
}

function Caption({ pane, selected, children, lead, title }: { pane: PaneModel; selected: boolean; children?: ReactNode; lead?: ReactNode; title?: ReactNode }) {
  return (
    <div
      className={`h-[24px] px-1.5 flex items-center gap-1.5 text-[11px] bg-panel border-b border-line shrink-0 min-w-0 ${selected ? "text-fg shadow-[inset_0_2px_0_var(--q-accent)]" : "text-muted"}`}
      data-q-pane-caption
    >
      {lead}
      {pane.pinned && <Icon name="pin" size={11} strokeWidth={2} className={pane.current ? "text-muted" : "text-accent"} />}
      {title ?? <span className={`truncate min-w-0 ${selected ? "font-semibold" : ""}`}>{paneTitle(pane)}</span>}
      <span className="flex-1" />
      {children}
    </div>
  );
}

/** Dims an unselectable pane while the panel is locked (the click itself is refused by the capture handler). */
function LockVeil({ why }: { why: string }) {
  return <div aria-hidden className="absolute inset-0 z-10 bg-bg/50 cursor-not-allowed" title={why} />;
}

/**
 * The splitter under a stacked pane: drag (the height follows the pointer on the element itself, so the grids do not
 * re-render per move; the new height is kept on release), double-click to fit the rows, arrows ±1 row, Home/End to
 * the limits, Enter to fit.
 */
function Splitter({ label, height, range, target, onChange, onFit }: {
  label: string; height: number; range: { min: number; max: number }; target: React.RefObject<HTMLDivElement | null>; onChange: (h: number) => void; onFit: () => void;
}) {
  const drag = useRef<{ y: number; h: number; now: number } | null>(null);
  const clamp = (h: number) => Math.round(Math.min(range.max, Math.max(range.min, h)));
  return (
    <div
      role="separator"
      aria-orientation="horizontal"
      aria-label={label}
      aria-valuemin={range.min}
      aria-valuemax={range.max}
      aria-valuenow={height}
      tabIndex={0}
      title="Drag to resize · double-click to fit the rows"
      className="h-[5px] shrink-0 cursor-row-resize border-b border-line hover:bg-accent/40 focus-visible:bg-accent/60 outline-none"
      data-q-pane-splitter
      onPointerDown={(e) => {
        e.currentTarget.setPointerCapture?.(e.pointerId);
        drag.current = { y: e.clientY, h: height, now: height };
      }}
      onPointerMove={(e) => {
        const d = drag.current;
        if (!d) return;
        d.now = clamp(d.h + e.clientY - d.y);
        if (target.current) target.current.style.height = `${d.now}px`;
      }}
      onPointerUp={() => {
        const d = drag.current;
        drag.current = null;
        if (d && d.now !== d.h) onChange(d.now);
      }}
      onDoubleClick={onFit}
      onKeyDown={(e) => {
        const to = e.key === "ArrowUp" ? height - HEIGHT_STEP : e.key === "ArrowDown" ? height + HEIGHT_STEP : e.key === "Home" ? range.min : e.key === "End" ? range.max : null;
        if (e.key === "Enter") {
          e.preventDefault();
          onFit();
        } else if (to != null) {
          e.preventDefault();
          onChange(clamp(to));
        }
      }}
    />
  );
}

interface StackPaneProps {
  pane: PaneModel; index: number; selected: boolean; mounted: boolean; avail: number; height?: number; collapsed: boolean;
  setHeight: (h: number | undefined) => void; setCollapsed: (c: boolean) => void; observe: (el: HTMLElement | null) => void; chart: boolean; cb: PaneCallbacks;
  /** What the caption shows of the set (rows and state): a pane re-renders when it changes. */
  stamp: string;
}

/**
 * A pane that is not selected re-renders only when what it shows changes (its set, rows, size, mount, lock): a pane
 * entering the view must not re-render the other 499. Its grid follows its rows by itself (it subscribes to the set).
 */
const samePane = (a: StackPaneProps, b: StackPaneProps) =>
  !a.selected && !b.selected && a.pane.key === b.pane.key && a.pane.rs === b.pane.rs && a.pane.pinned === b.pane.pinned && a.stamp === b.stamp &&
  a.index === b.index && a.mounted === b.mounted && a.avail === b.avail && a.height === b.height && a.collapsed === b.collapsed && a.chart === b.chart &&
  a.cb.locked === b.cb.locked;

const StackPane = memo(function StackPane({ pane, index, selected, mounted, avail, height, collapsed, setHeight, setCollapsed, observe, chart, cb }: StackPaneProps) {
  const body = useRef<HTMLDivElement>(null);
  const handlers = useSelectHandlers(pane, selected, cb);
  const fit = Math.max(fitHeight(pane.rs.rowCount, avail), chart ? 240 : 0);
  const h = height ?? fit;
  const n = pane.rs.index + 1;
  return (
    <section
      ref={observe}
      role="group"
      aria-label={paneTitle(pane)}
      aria-current={selected || undefined}
      className={`flex flex-col min-w-0 ${index ? "border-t border-line" : ""}`}
      // Panes far from the view skip layout and paint (their size is reserved), so 500 sets lay out like the few in
      // view: laying out every pane was ~150 ms of a 500-set switch (CPU profile, §14 metric 37, 2026-11-14).
      style={{ contentVisibility: "auto", containIntrinsicSize: `auto ${24 + (collapsed ? 0 : h + 5)}px` }}
      data-q-pane-key={pane.key}
      data-q-stack-key={pane.key}
      data-q-pane-selected={selected ? "" : undefined}
      {...handlers}
    >
      <Caption
        pane={pane}
        selected={selected}
        lead={
          <button
            type="button"
            className="w-4 h-4 rounded flex items-center justify-center hover:bg-hover hover:text-fg shrink-0"
            aria-expanded={!collapsed}
            aria-label={`${collapsed ? "Expand" : "Collapse"} result set ${n}`}
            title={collapsed ? "Expand" : "Collapse"}
            onClick={() => setCollapsed(!collapsed)}
          >
            <Icon name="chevron" size={11} strokeWidth={2} className={collapsed ? "-rotate-90" : ""} />
          </button>
        }
      >
        {/* A collapsed pane is one line: its tools come with it when it opens (500 rows of buttons cost the switch). */}
        {!collapsed && cb.tools(pane)}
      </Caption>
      {!collapsed && (
        <>
          <div ref={body} className="relative flex min-h-0 shrink-0" style={{ height: h }} data-q-pane-body>
            {mounted ? cb.renderBody(pane, selected) : (
              <div className="flex-1 flex items-center justify-center text-muted text-[11px]" data-q-pane-placeholder>
                Result set {n} · {rowsText(pane.rs.rowCount)}
              </div>
            )}
            {cb.locked && !selected && pane.current && <LockVeil why={cb.locked} />}
          </div>
          <Splitter label={`Resize result set ${n}`} height={h} range={heightRange(avail)} target={body} onChange={setHeight} onFit={() => setHeight(undefined)} />
        </>
      )}
    </section>
  );
}, samePane);

/**
 * Stacked: every set of the run one under another in one scroll, its errors in place, pinned sets first. A grid's
 * height fits its rows (3 rows up to 60 % of the panel; its splitter changes it); sets after the 20th start collapsed;
 * only the panes near the view (and the selected one) hold a live grid. Selecting a set elsewhere (a chip,
 * Alt+PageDown) brings it into view; a run never scrolls the stack by itself.
 */
export function StackedSets({ items, chartKeys, ...cb }: PaneCallbacks & { items: StackItem[]; chartKeys: ReadonlySet<string> }) {
  const box = useRef<HTMLDivElement>(null);
  const [avail, setAvail] = useState(400);
  const [heights, setHeights] = useState<Record<string, number>>({});
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});
  const [visible, setVisible] = useState<Set<string> | null>(null);
  const io = useRef<IntersectionObserver | null>(null);
  const [live, setLive] = useState<string[]>([]);
  const liveRef = useRef<string[]>([]);

  // The panel's height from the ResizeObserver's first report (after the browser's own layout): measuring it here
  // forced a synchronous layout of every pane, 150 ms of a 500-set switch (CPU profile, §14 metric 37, 2026-11-14).
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const measure = (h: number) => h > 0 && setAvail((a) => (Math.abs(a - h) > 1 ? Math.round(h) : a));
    if (typeof ResizeObserver === "undefined") {
      measure(el.clientHeight);
      return;
    }
    const ro = new ResizeObserver((entries) => measure(entries[0].contentRect.height));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Which panes are near the view: only entering and leaving re-render, never a scroll frame by itself.
  useEffect(() => {
    const root = box.current;
    if (!root || typeof IntersectionObserver === "undefined") return;
    const seen = new Set<string>();
    const obs = new IntersectionObserver(
      (entries) => {
        let changed = false;
        for (const e of entries) {
          const k = (e.target as HTMLElement).dataset.qPaneKey;
          if (!k || e.isIntersecting === seen.has(k)) continue;
          if (e.isIntersecting) seen.add(k);
          else seen.delete(k);
          changed = true;
        }
        if (changed) setVisible(new Set(seen));
      },
      { root, rootMargin: "240px 0px" },
    );
    io.current = obs;
    root.querySelectorAll<HTMLElement>("[data-q-pane-key]").forEach((el) => obs.observe(el));
    return () => {
      obs.disconnect();
      io.current = null;
    };
  }, []);

  // The selected item comes into view when it changes (a chip, Alt+PageDown); nothing else scrolls the stack.
  useEffect(() => {
    if (!cb.selectedKey) return;
    const el = [...(box.current?.querySelectorAll<HTMLElement>("[data-q-stack-key]") ?? [])].find((x) => x.dataset.qStackKey === cb.selectedKey);
    el?.scrollIntoView?.({ block: "nearest" });
  }, [cb.selectedKey]);

  const panes = items.filter((i): i is Extract<StackItem, { kind: "pane" }> => i.kind === "pane").map((i) => i.pane);
  const order = panes.map((p) => p.key);
  const keep = new Set(cb.selectedKey && order.includes(cb.selectedKey) ? [cb.selectedKey] : []);
  // Grids coming into view mount one per frame (placeholders until then), so no frame builds several at once; the
  // selected one mounts at once. Grids leaving the window unmount at once.
  const target = [...mountedPanes(order, visible, keep)];
  const targetKey = target.join("|");
  useEffect(() => {
    const want = targetKey ? targetKey.split("|") : [];
    let id = 0;
    const step = () => {
      const cur = liveRef.current;
      const kept = cur.filter((k) => want.includes(k));
      const missing = want.filter((k) => !kept.includes(k));
      const next = missing.length ? [...kept, missing[0]] : kept;
      if (next.length !== cur.length || missing.length) {
        liveRef.current = next;
        setLive(next);
      }
      if (missing.length > 1) id = requestAnimationFrame(step);
    };
    step();
    return () => cancelAnimationFrame(id);
  }, [targetKey]);
  const mounted = new Set([...live.filter((k) => target.includes(k)), ...keep]);
  const observe = (el: HTMLElement | null) => el && io.current?.observe(el);
  let n = 0;

  return (
    <div ref={box} className="flex-1 min-h-0 min-w-0 overflow-y-auto overflow-x-hidden" role="group" aria-label={`Result sets, ${panes.length}, stacked`} data-q-layout="stacked">
      {items.map((it) => {
        if (it.kind === "error") return <div key={it.key} data-q-stack-key={it.key}>{it.node}</div>;
        const p = it.pane;
        const i = n++;
        const selected = p.key === cb.selectedKey;
        return (
          <StackPane
            key={p.key}
            pane={p}
            index={i}
            selected={selected}
            mounted={mounted.has(p.key)}
            avail={avail}
            height={heights[p.key]}
            collapsed={!selected && (collapsed[p.key] ?? i >= COLLAPSE_AFTER)}
            setHeight={(h) => setHeights((s) => {
              const next = { ...s };
              if (h === undefined) delete next[p.key];
              else next[p.key] = h;
              return next;
            })}
            setCollapsed={(c) => setCollapsed((s) => ({ ...s, [p.key]: c }))}
            observe={observe}
            chart={chartKeys.has(p.key)}
            cb={cb}
            stamp={`${p.rs.rowCount}|${p.rs.complete}|${p.rs.truncated}|${p.rs.interrupted}`}
          />
        );
      })}
    </div>
  );
}

function SidePane({ pane, first, selected, picker, cb }: { pane: PaneModel; first: boolean; selected: boolean; picker?: ReactNode; cb: PaneCallbacks }) {
  const handlers = useSelectHandlers(pane, selected, cb);
  return (
    <section
      role="group"
      aria-label={paneTitle(pane)}
      aria-current={selected || undefined}
      className={`flex-1 basis-0 min-w-[280px] flex flex-col min-h-0 ${first ? "" : "border-l-2 border-line"}`}
      data-q-pane-key={pane.key}
      data-q-pane-selected={selected ? "" : undefined}
      {...handlers}
    >
      <Caption pane={pane} selected={selected} title={picker}>{cb.tools(pane)}</Caption>
      <div className="flex-1 min-h-0 relative flex" data-q-pane-body>
        {cb.renderBody(pane, selected)}
        {cb.locked && !selected && pane.current && <LockVeil why={cb.locked} />}
      </div>
    </section>
  );
}

/**
 * Side by side: up to three sets in equal columns (pinned sets first); each column of the shown run picks its set from
 * the caption's menu. Columns keep 280 px at least: a narrower panel scrolls them sideways (and below 760 px the panel
 * shows Stacked instead).
 */
export function SideSets({ columns, picker, ...cb }: PaneCallbacks & { columns: PaneModel[]; picker: (pane: PaneModel, slot: number) => ReactNode }) {
  let slot = 0;
  return (
    <div className="flex-1 min-h-0 min-w-0 flex overflow-x-auto" role="group" aria-label={`Result sets side by side, ${columns.length}`} data-q-layout="side">
      {columns.map((p, i) => (
        <SidePane key={p.key} pane={p} first={i === 0} selected={p.key === cb.selectedKey} picker={p.current ? picker(p, slot++) : undefined} cb={cb} />
      ))}
    </div>
  );
}

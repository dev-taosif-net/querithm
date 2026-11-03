// The bottom panel of a query tab (§12 bottom panel): a 30 px header with five fixed views — Results, Messages, Plan
// (about the shown run) ┃ Runtime, Usages (about the tab) — the run picker and the layout buttons, then the view.
// Views never appear, move or switch on their own; a view an engine cannot use keeps its slot and says why.
import { lazy, Suspense, useRef, useState, type KeyboardEvent } from "react";
import type { BottomTab, TabState } from "../store/tabs";
import { useExecutions } from "../store/executions";
import { useUsages } from "../store/usages";
import { useDiag } from "../store/diag";
import { useSettings } from "../store/settings";
import { setBottomTab, showRun, toggleBottom, toggleMaximize } from "./actions";
import { ResultsPanel } from "../results/ResultsPanel";
import { MessagesPanel, fmtMs } from "../results/MessagesPanel";
import { UsagesPanel } from "../refactor/UsagesPanel";
import { RuntimePanel } from "./RuntimePanel";
import { executionData } from "../results/model";
import { isLatestRun, runErrors } from "../results/ledger";
import { timeOfDay } from "../results/messageLog";
import { useThrottledVersion } from "../results/useThrottledVersion";
import { Icon, type IconName } from "./icons";
import { DropMenu } from "./ToolbarMenu";
import { PanelIconButton, PanelWidthContext, useWidthBucket, type PanelWidth } from "./panelKit";
import { displayKeys, keysFor, commands } from "./commands";
import { ErrorBoundary } from "./ErrorBoundary";

// Loaded on first use: keeps React Flow + dagre out of startup memory.
const PlanView = lazy(() => import("../plans/PlanView").then((m) => ({ default: m.PlanView })));

export const PANEL_VIEWS: { id: BottomTab; label: string; icon: IconName; scope: "run" | "tab" }[] = [
  { id: "results", label: "Results", icon: "results", scope: "run" },
  { id: "messages", label: "Messages", icon: "messages", scope: "run" },
  { id: "plan", label: "Plan", icon: "plan", scope: "run" },
  { id: "runtime", label: "Runtime", icon: "runtime", scope: "tab" },
  { id: "usages", label: "Usages", icon: "search", scope: "tab" },
];

const keyOf = (id: string) => {
  const c = commands.find((x) => x.id === id);
  const k = c ? keysFor(c)[0] : undefined;
  return k ? displayKeys(k) : "";
};

export function BottomPanel({ tab }: { tab: TabState }) {
  const root = useRef<HTMLDivElement>(null);
  const width = useWidthBucket(root);
  return (
    <PanelWidthContext.Provider value={width}>
      <div ref={root} className="flex-1 flex flex-col min-h-0 min-w-0" data-q-bottom-panel data-width={width}>
        <PanelHeader tab={tab} width={width} />
        <div className="flex-1 flex flex-col min-h-0 bg-bg">
          {/* M17: a view that fails shows so in its place; the header (and so the other views) keeps working. A run view
              that failed tries again on another run (W7); the tab's own views (Runtime, Usages) keep their boundary. */}
          <ErrorBoundary
            key={tab.bottomTab} name={PANEL_VIEWS.find((v) => v.id === tab.bottomTab)?.label ?? "Panel"}
            resetKey={PANEL_VIEWS.find((v) => v.id === tab.bottomTab)?.scope === "run" ? (tab.activeExecutionId ?? "") : undefined}
          >
            {tab.bottomTab === "results" && <ResultsPanel tab={tab} />}
            {tab.bottomTab === "messages" && <MessagesPanel tab={tab} />}
            {tab.bottomTab === "plan" && (
              <Suspense fallback={<div className="flex-1" />}>
                <PlanView tab={tab} />
              </Suspense>
            )}
            {tab.bottomTab === "runtime" && <RuntimePanel tab={tab} />}
            {tab.bottomTab === "usages" && <UsagesPanel tab={tab} />}
          </ErrorBoundary>
        </div>
      </div>
    </PanelWidthContext.Provider>
  );
}

function PanelHeader({ tab, width }: { tab: TabState; width: PanelWidth }) {
  const view = useExecutions((s) => (tab.activeExecutionId ? s.views[tab.activeExecutionId] : undefined));
  const data = tab.activeExecutionId ? executionData.get(tab.activeExecutionId) : undefined;
  useThrottledVersion(data);
  const hits = useUsages((s) => s.byTab[tab.id]?.result?.hits.length);
  const debugState = useDiag((s) => (tab.debugId ? s.debugs[tab.debugId]?.state : undefined));
  const dock = useSettings((s) => s.panelDock);
  const refs = useRef<Record<string, HTMLButtonElement | null>>({});
  const errors = runErrors(view).length;
  const sets = data?.resultSets.length ?? 0;

  const badge = (id: BottomTab): { text: string; tone?: "err" | "ok"; spoken: string } | undefined => {
    if (id === "results" && view) return { text: String(sets), spoken: `${sets} result set${sets === 1 ? "" : "s"}` };
    if (id === "messages" && errors) return { text: String(errors), tone: "err", spoken: `${errors} error${errors === 1 ? "" : "s"}` };
    if (id === "plan" && view?.plans.length) return { text: String(view.plans.length), spoken: `${view.plans.length} plan${view.plans.length === 1 ? "" : "s"}` };
    if (id === "runtime" && tab.traceId) return { text: "● Live", tone: "ok", spoken: "trace live" };
    if (id === "runtime" && debugState === "paused") return { text: "Paused", tone: "ok", spoken: "debugger paused" };
    if (id === "usages" && hits != null) return { text: String(hits), spoken: `${hits} usage${hits === 1 ? "" : "s"}` };
    return undefined;
  };

  const move = (e: KeyboardEvent, i: number) => {
    const n = PANEL_VIEWS.length;
    const to = e.key === "ArrowRight" ? (i + 1) % n : e.key === "ArrowLeft" ? (i - 1 + n) % n : e.key === "Home" ? 0 : e.key === "End" ? n - 1 : null;
    if (to == null) return;
    e.preventDefault();
    const v = PANEL_VIEWS[to];
    refs.current[v.id]?.focus();
    setBottomTab(tab.id, v.id);
  };

  return (
    <div className="h-[30px] pl-1 pr-1.5 flex items-stretch gap-px border-b border-line bg-panel shrink-0 min-w-0" data-q-panel-header>
      <div role="tablist" aria-label="Results panel views" className="flex items-stretch gap-px min-w-0">
        {PANEL_VIEWS.map((v, i) => {
          const on = tab.bottomTab === v.id;
          const b = badge(v.id);
          const iconOnly = width === "narrow" && !on;
          return (
            <span key={v.id} className="flex items-stretch">
              {i === 3 && <span aria-hidden className="w-px bg-line my-2 mx-1.5" />}
              <button
                ref={(el) => void (refs.current[v.id] = el)}
                type="button"
                role="tab"
                aria-selected={on}
                tabIndex={on ? 0 : -1}
                aria-label={iconOnly || b ? `${v.label}${b ? `, ${b.spoken}` : ""}` : undefined}
                title={iconOnly ? v.label : undefined}
                data-q-panel-tab={v.id}
                className={`relative px-2.5 flex items-center gap-1.5 text-[12px] whitespace-nowrap ${on ? "text-fg font-semibold" : "text-muted hover:text-fg"}`}
                onClick={() => setBottomTab(tab.id, v.id)}
                onKeyDown={(e) => move(e, i)}
              >
                <Icon name={v.icon} size={14} strokeWidth={2} />
                {!iconOnly && <span>{v.label}</span>}
                {b && (
                  <span className={`px-[5px] rounded-full q-mono text-[10.5px] leading-4 font-semibold flex items-center gap-0.5 ${b.tone === "err" ? "bg-err/15 text-err" : b.tone === "ok" ? "bg-ok/15 text-ok" : "bg-raised text-muted"}`}>
                    {b.tone === "err" && <Icon name="errorCircle" size={10} strokeWidth={2.25} />}
                    {b.text}
                  </span>
                )}
                {on && <span aria-hidden className="absolute left-1.5 right-1.5 -bottom-px h-[2px] rounded bg-accent" />}
              </button>
            </span>
          );
        })}
      </div>
      <span className="flex-1 min-w-2" />
      <RunPicker tab={tab} compact={width === "narrow"} />
      {width !== "narrow" && (
        <PanelIconButton
          icon={dock === "right" ? "dockBottom" : "dock"}
          label={dock === "right" ? "Dock the panel under the editor" : "Dock the panel to the right of the editor"}
          onClick={() => useSettings.getState().set({ panelDock: dock === "right" ? "bottom" : "right" })}
        />
      )}
      <PanelIconButton icon={tab.bottomMax ? "restore" : "maximize"} label={`${tab.bottomMax ? "Restore the panel" : "Maximize the panel"} (${keyOf("panel.maximize")})`} pressed={!!tab.bottomMax} onClick={() => toggleMaximize(tab.id)} />
      <PanelIconButton icon="hidePanel" label={`Hide the panel (${keyOf("results.toggle")})`} onClick={() => toggleBottom(tab.id)} />
    </div>
  );
}

const clock = timeOfDay;

/** The 3 runs the tab keeps, newest first; an older run opens read-only. Disabled on the tab-scoped views. */
function RunPicker({ tab, compact }: { tab: TabState; compact: boolean }) {
  const [open, setOpen] = useState(false);
  const btn = useRef<HTMLButtonElement>(null);
  const views = useExecutions((s) => s.views);
  if (!tab.executions.length || !tab.activeExecutionId) return null;
  const pos = tab.executions.indexOf(tab.activeExecutionId) + 1;
  const total = tab.executions.length;
  const cur = views[tab.activeExecutionId];
  const older = !isLatestRun(tab);
  const errs = runErrors(cur).length;
  const toolView = tab.bottomTab === "runtime" || tab.bottomTab === "usages";
  const why = toolView ? "Runtime and Usages belong to the tab, not to a run" : undefined;
  const label = `Run ${pos} of ${total}${cur ? `, ${clock(cur.startedAt)}` : ""}${older ? ", older run, read-only" : ""}${errs ? `, ${errs} error${errs === 1 ? "" : "s"}` : ""}`;
  return (
    <>
      <button
        ref={btn}
        type="button"
        className={`self-center h-[22px] px-1.5 mr-1 flex items-center gap-1.5 rounded border text-[11.5px] shrink-0 ${older ? "border-warn/60" : "border-line"} ${toolView ? "text-muted" : "text-fg hover:bg-hover"}`}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`Shown run: ${label}`}
        aria-disabled={toolView || undefined}
        title={why ?? `Runs kept for this tab (${keyOf("panel.previousRun")} / ${keyOf("panel.nextRun")})`}
        data-q-run-picker
        onClick={() => !toolView && setOpen((o) => !o)}
      >
        <Icon name="history" size={13} strokeWidth={2} />
        <span>{compact ? `${pos}/${total}` : `Run ${pos} of ${total}`}</span>
        {!compact && cur && <span className="q-mono text-[10.5px] text-muted">{clock(cur.startedAt)}</span>}
        {older && <span className="px-1 rounded bg-warn/15 text-warn text-[10px] font-semibold">older</span>}
        {errs > 0 && (
          <span className="flex items-center gap-0.5 text-err text-[10.5px] font-semibold">
            <Icon name="errorCircle" size={10} strokeWidth={2.25} />
            {errs}
          </span>
        )}
        <Icon name="chevron" size={11} strokeWidth={2} />
      </button>
      {open && (
        <DropMenu
          anchor={btn}
          label="Runs kept for this tab"
          header={<span className="text-[11px] text-muted">The last 3 runs stay in memory; older runs open read-only.</span>}
          items={[...tab.executions].reverse().map((id) => {
            const v = views[id];
            const n = tab.executions.indexOf(id) + 1;
            const sets = executionData.get(id)?.resultSets.length ?? v?.resultSetCount ?? 0;
            const e = runErrors(v).length;
            const parts = [`${sets} set${sets === 1 ? "" : "s"}`, e ? `${e} error${e === 1 ? "" : "s"}` : v?.state === "cancelled" ? "cancelled" : null, v?.elapsedMs != null ? fmtMs(v.elapsedMs) : null].filter(Boolean);
            return { label: `Run ${n}`, detail: parts.join(" · "), hint: v ? clock(v.startedAt) : undefined, checked: id === tab.activeExecutionId, run: () => showRun(tab.id, id) };
          })}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}


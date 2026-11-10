// Top bar (§12). Left: the launch strip (environment · connection › database joined to Run ▾), Explain ▾ and the
// transaction; right: search, history, settings, update. Each state is loud in one place and that place is here:
// where a query will run, what Run will do (actual plan, debugger), and how long a transaction has been open. The
// status bar keeps the quiet detail. Below 1440 px labels drop, never functions; state (environment, Run/Cancel,
// transaction) never collapses.
import { BusySpinner } from "./LoadingStatus";
import { useEffect, useRef, useState, type FocusEvent, type KeyboardEvent, type MouseEvent, type ReactNode, type RefObject } from "react";
import { useActiveTab, useTabs, type TabState } from "../store/tabs";
import { useUi } from "../store/ui";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { useExecutions } from "../store/executions";
import { useConnections } from "../store/connections";
import { useDiag } from "../store/diag";
import { cancelQuery, newQueryTab, showPlan, txAction } from "./actions";
import { commands, displayKeys, keysFor } from "./commands";
import { goBack, goForward, useNav } from "./navigation";
import { Logo } from "./Logo";
import { UpdateButton } from "./UpdateButton";
import { EngineLogo } from "../connections/EngineLogo";
import { fillForWhite, readableOnSurface, useThemeKey } from "./tabStyle";
import { Icon } from "./icons";
import { DropMenu, type MenuEntry } from "./ToolbarMenu";
import { clearRunScope, showRunScope, type ScopeMode } from "../editor/runScope";
import { actualPlanWithRun, assertNever, engineTraits, ENGINE_LIST, ENGINES } from "../engines";
import type { Engine } from "../ipc/contract";

/** A transaction open this long is a lock risk: its group's border goes to full strength (never blinks). */
export const LONG_TX_MS = 5 * 60_000;
/** Clicks on Run / Cancel are ignored this long after the slot swapped, so a double-click cannot run then cancel. */
export const SWAP_GUARD_MS = 400;

function keyOf(id: string) {
  const c = commands.find((x) => x.id === id);
  const k = c ? keysFor(c)[0] : undefined;
  return k ? displayKeys(k) : "";
}

const tip = (label: string, id: string) => (keyOf(id) ? `${label} (${keyOf(id)})` : label);

function runCommand(id: string) {
  void commands.find((x) => x.id === id)?.run();
}

/** Click handler for a control that keeps focus when unavailable (aria-disabled): it says why, it does nothing. */
const guarded = (unavailable: boolean, run: () => void) => (e: MouseEvent) => {
  if (unavailable) e.preventDefault();
  else run();
};

function useRunning(tab: TabState | undefined) {
  const view = useExecutions((s) => (tab?.activeExecutionId ? s.views[tab.activeExecutionId] : undefined));
  return !!tab?.busy || (!!view && ["queued", "running", "streaming"].includes(view.state));
}

/**
 * WAI-ARIA toolbar: one Tab stop, ←/→/Home/End move between its controls, and it returns to the control used last.
 * Tab stops live in the DOM (no React state), so focus moves never re-render the bar; a control that a child
 * component adds later (Reconnect, Update) is folded in by a MutationObserver.
 */
function useRovingToolbar(ref: RefObject<HTMLDivElement | null>) {
  const last = useRef<HTMLElement | null>(null);
  const controls = () => [...(ref.current?.querySelectorAll<HTMLElement>("button") ?? [])];
  useEffect(() => {
    const root = ref.current;
    if (!root) return;
    const normalize = () => {
      const all = controls();
      const stop = last.current && all.includes(last.current) ? last.current : all[0];
      for (const el of all) el.tabIndex = el === stop ? 0 : -1;
    };
    normalize();
    const observer = new MutationObserver(normalize);
    observer.observe(root, { childList: true, subtree: true });
    return () => observer.disconnect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const onFocus = (e: FocusEvent) => {
    const all = controls();
    if (!all.includes(e.target as HTMLElement)) return;
    last.current = e.target as HTMLElement;
    for (const el of all) el.tabIndex = el === e.target ? 0 : -1;
  };
  const onKeyDown = (e: KeyboardEvent) => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(e.key)) return;
    const all = controls();
    const i = all.indexOf(document.activeElement as HTMLElement);
    if (i < 0) return;
    e.preventDefault();
    const n = e.key === "Home" ? 0 : e.key === "End" ? all.length - 1 : (i + (e.key === "ArrowRight" ? 1 : -1) + all.length) % all.length;
    all[n].focus();
  };
  return { onFocus, onKeyDown };
}

/** A face button and its ▾ menu, as two real buttons (each with its own name, focus ring and target). */
function SplitButton({ face, caretLabel, menuLabel, items, caretClass = "", disabled = false, className = "" }: {
  face: ReactNode; caretLabel: string; menuLabel: string; items: () => MenuEntry[]; caretClass?: string; disabled?: boolean; className?: string;
}) {
  const [open, setOpen] = useState(false);
  const caret = useRef<HTMLButtonElement>(null);
  const toggle = () => setOpen((o) => !o);
  return (
    <div className={`flex items-stretch ${className}`}>
      {face}
      <button
        ref={caret}
        type="button"
        className={`q-tb !min-w-0 w-[18px] !px-0 rounded-l-none ${caretClass}`}
        aria-label={caretLabel}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-disabled={disabled || undefined}
        onClick={guarded(disabled, toggle)}
        onKeyDown={(e) => {
          if (!disabled && e.key === "ArrowDown") {
            e.preventDefault();
            setOpen(true);
          }
        }}
      >
        <Icon name="chevron" size={12} strokeWidth={2.25} />
      </button>
      {open && <DropMenu anchor={caret} label={menuLabel} items={items()} onClose={() => setOpen(false)} />}
    </div>
  );
}

function NavButtons() {
  const { canBack, canForward } = useNav();
  const cls = "q-tb w-7 !px-0 text-muted hover:text-fg";
  return (
    <div className="flex items-center gap-0.5">
      <button type="button" className={cls} aria-disabled={!canBack || undefined} onClick={guarded(!canBack, goBack)} title={tip("Go back", "nav.back")} aria-label="Go back">
        <Icon name="back" />
      </button>
      <button type="button" className={cls} aria-disabled={!canForward || undefined} onClick={guarded(!canForward, goForward)} title={tip("Go forward", "nav.forward")} aria-label="Go forward">
        <Icon name="forward" />
      </button>
    </div>
  );
}

function NewButton() {
  return (
    <SplitButton
      caretLabel="More ways to open a query"
      menuLabel="Open a query"
      items={() => [
        { label: "New query", detail: "same connection", hint: keyOf("tab.new"), run: () => void newQueryTab() },
        { label: "New query on another connection…", run: () => { newQueryTab(); useUi.getState().setPalette({ mode: "connections" }); } },
        { label: "New notebook", detail: "SQL and notes in cells", run: () => runCommand("notebook.new") },
        "sep",
        { label: "Open SQL file…", hint: keyOf("file.open"), run: () => runCommand("file.open") },
        { label: "Open notebook…", run: () => runCommand("notebook.open") },
        { label: "Import Jupyter notebook…", run: () => runCommand("notebook.importIpynb") },
        { label: "Reopen closed tab", hint: keyOf("tab.reopenClosed"), run: () => runCommand("tab.reopenClosed") },
      ]}
      face={
        <button type="button" className="q-tb rounded-r-none" onClick={() => void newQueryTab()} title={tip("New query on the same connection", "tab.new")} aria-label="New query">
          <Icon name="plus" strokeWidth={2} className="text-accent" />
          <span className="hidden min-[1440px]:inline">New query</span>
        </button>
      }
    />
  );
}

/** Environment · engine · connection › database, joined to Run ▾: the eye crosses the target before it reaches Run. */
function LaunchStrip({ tab }: { tab: TabState | undefined }) {
  useThemeKey(); // the badge and label colors are computed from the theme's colors
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === tab?.profileId));
  const groups = useProfiles((s) => s.groups);
  const disconnected = useConnections((s) => !!tab?.profileId && !!s.disconnected[tab.profileId]);
  const lost = useConnections((s) => !!tab?.profileId && !s.disconnected[tab.profileId] && s.servers[tab.profileId]?.status === "error");
  const debugging = useDiag((s) => {
    const d = tab?.debugId ? s.debugs[tab.debugId] : undefined;
    return !!d && d.state !== "finished" && d.state !== "error";
  });
  const running = useRunning(tab);
  const swappedAt = useRef(0);
  const was = useRef(running);
  useEffect(() => {
    if (was.current !== running) swappedAt.current = performance.now();
    was.current = running;
  }, [running]);

  const color = envColor(profile, groups);
  const env = profile ? envLabel[profile.environment].toUpperCase() : "";
  const prod = profile?.environment === "prod";
  const target = profile ? `${env} ${profile.name} › ${tab?.database ?? "(default)"}` : "";
  const scope = (mode: ScopeMode) => (on: boolean) => (on && tab && profile && !running ? showRunScope(tab.id, mode) : clearRunScope());
  const plan = !!profile && actualPlanWithRun(profile.engine) && !!tab?.actualPlan;

  const onRun = (e: MouseEvent) => {
    // A pointer click right after the slot swapped is the second half of a double-click; keyboard activation is not.
    if (!tab || (e.detail > 0 && performance.now() - swappedAt.current < SWAP_GUARD_MS)) return;
    clearRunScope();
    if (running) void cancelQuery(tab.id);
    else runCommand("query.run");
  };

  const runLabel = running ? "Cancel the running query" : debugging ? "Continue debugging" : profile ? `Run on ${target}${plan ? ", with actual plan" : ""}` : "Run (choose a connection first)";
  const runKeys = running ? keyOf("query.cancel") : keyOf("query.run");
  const tone = running ? "text-err hover:bg-err/15" : "text-ok hover:bg-ok/15";

  return (
    <div className={`flex items-stretch h-[28px] rounded-md border bg-bg ${!tab?.profileId ? "border-dashed border-line-strong" : lost ? "border-err" : "border-line-strong"}`}>
      <div className="w-[180px] min-[1280px]:w-[240px] min-[1440px]:w-[280px] max-[1100px]:w-[160px] flex items-center gap-1 pl-1 pr-1 min-w-0">
        {!profile ? (
          <button
            type="button"
            className="q-tb flex-1 justify-between !h-[22px] !px-1.5 text-accent font-semibold"
            aria-disabled={!tab || undefined}
            onClick={guarded(!tab, () => useUi.getState().setPalette({ mode: "connections" }))}
            title={tip("Choose a connection", "palette.connections")}
          >
            <span className="flex items-center gap-1.5">
              <Icon name="plug" size={14} strokeWidth={2} />
              Connect…
            </span>
            <Icon name="chevron" size={12} strokeWidth={2} />
          </button>
        ) : (
          <>
            <button
              type="button"
              className="q-tb !h-[22px] !px-1 !min-w-0 shrink min-w-0 text-muted"
              onClick={() => useUi.getState().setPalette({ mode: "connections" })}
              title={tip("Change connection", "palette.connections")}
              aria-label={`Change connection, now ${env} ${profile.name}${disconnected ? ", disconnected" : lost ? ", connection lost" : ""}`}
            >
              {prod ? (
                <span className="h-[18px] px-1.5 rounded flex items-center gap-1 text-[10px] font-bold tracking-wide text-white shrink-0" style={{ background: fillForWhite(color) }}>
                  <Icon name="shield" size={10} strokeWidth={2.5} />
                  PROD
                </span>
              ) : (
                <EnvPill color={color} label={env} />
              )}
              <EngineLogo engine={profile.engine} size={14} />
              {/* The database keeps its width: it changes more often than the server, and the full name is in the tooltip. */}
              <span className="hidden xl:inline truncate max-w-[110px]">{profile.name}</span>
            </button>
            <span aria-hidden className="hidden xl:inline text-muted">›</span>
            <button
              type="button"
              className="q-tb !h-[22px] !px-1 !min-w-0 flex-1 min-w-0 justify-between font-semibold"
              onClick={() => useUi.getState().setPalette({ mode: "databases" })}
              title={tip(disconnected ? "Change database. Disconnected: running asks before reconnecting" : "Change database", "palette.databases")}
              aria-label={`Change database, now ${tab?.database ?? "(default)"}`}
            >
              <span className="flex items-center gap-1.5 min-w-0">
                {disconnected && <span aria-hidden className="w-2 h-2 rounded-full border border-fg shrink-0" />}
                <span className="truncate">{tab?.database ?? "(default)"}</span>
              </span>
              <Icon name="chevron" size={12} strokeWidth={2} className="text-muted" />
            </button>
            {lost && (
              <button
                type="button"
                className="q-tb !h-[22px] !px-1 text-err font-semibold"
                onClick={() => void useConnections.getState().connect(profile.id)}
                title="Connection lost. Reconnect"
                aria-label="Connection lost. Reconnect"
              >
                <Icon name="reconnect" size={12} strokeWidth={2.25} />
                <span className="hidden xl:inline">Reconnect</span>
              </button>
            )}
          </>
        )}
      </div>
      <SplitButton
        className={`-my-px -mr-px rounded-r-md border ${running ? "border-err bg-err/10" : "border-ok bg-ok/10"}`}
        caretClass={`!h-auto ${tone}`}
        caretLabel="More ways to run"
        menuLabel="Run"
        disabled={running || !tab}
        items={() => runMenu(tab, profile?.engine, scope)}
        face={
          <button
            type="button"
            className={`q-tb !h-auto rounded-none w-[96px] xl:w-[104px] max-[1100px]:w-[88px] font-semibold ${running ? "!gap-1 !px-1" : ""} ${tone}`}
            aria-disabled={!tab || undefined}
            aria-label={runLabel}
            aria-keyshortcuts={runKeys}
            title={`${runLabel} (${runKeys})`}
            onClick={onRun}
            onPointerEnter={() => scope("auto")(true)}
            onPointerLeave={() => scope("auto")(false)}
            onFocus={() => scope("auto")(true)}
            onBlur={() => scope("auto")(false)}
          >
            {running ? (
              <>
                <Icon name="stop" size={14} strokeWidth={2.5} />
                <BusySpinner />
                Cancel
              </>
            ) : debugging ? (
              <>
                <Icon name="continue" size={12} strokeWidth={2.5} />
                Continue
                <span className="q-kbd !text-ok hidden min-[1440px]:inline">{keyOf("query.run")}</span>
              </>
            ) : (
              <>
                <Icon name="play" size={12} />
                Run
                {plan ? (
                  <span className="px-1 py-px rounded-[3px] bg-accent/15 text-accent text-[10px] font-bold">+plan</span>
                ) : (
                  <span className="q-kbd !text-ok hidden min-[1440px]:inline">{keyOf("query.run")}</span>
                )}
              </>
            )}
          </button>
        }
      />
    </div>
  );
}

/** A non-prod environment: an outlined text label (the color alone never carries it), readable on the strip. */
function EnvPill({ color, label }: { color: string; label: string }) {
  const c = readableOnSurface(color, "var(--q-bg)") ?? color;
  return (
    <span className="h-[18px] px-1.5 rounded border flex items-center text-[10px] font-bold tracking-wide shrink-0" style={{ color: c, borderColor: c }}>
      {label}
    </span>
  );
}

/**
 * How Run ▾ and Explain ▾ name an engine's plans; no engine (no connection) reads as the default engine. `unavailable`
 * says why an engine without actual plans greys those items out.
 */
function planLabels(engine: Engine | undefined): {
  estimated: string;
  actual: { label: string; detail: string; unavailable?: string };
  run: { label: string; detail: string; unavailable?: string };
} {
  const traits = engineTraits(engine);
  const mode = traits.actualPlan;
  switch (mode) {
    case "withRun":
      return { estimated: "Estimated plan", actual: { label: "Actual plan", detail: "runs the query" }, run: { label: "Run with actual plan", detail: "runs the query" } };
    case "explainAnalyze":
      return { estimated: "Explain", actual: { label: "Explain analyze…", detail: "runs, asks first" }, run: { label: "Explain analyze…", detail: "runs, asks first" } };
    case "none": {
      const unavailable = `${traits.shortLabel} shows the estimated plan only`;
      return { estimated: "Explain", actual: { label: "Actual plan", detail: "runs the query", unavailable }, run: { label: "Run with actual plan", detail: "runs the query", unavailable } };
    }
    default:
      return assertNever(mode);
  }
}

/** The engines whose runs can include the actual plan ("SQL Server only"). */
const PER_RUN_PLAN_ONLY = `${ENGINE_LIST.filter(actualPlanWithRun).map((e) => ENGINES[e].shortLabel).join(", ")} only`;

function runMenu(tab: TabState | undefined, engine: Engine | undefined, scope: (m: ScopeMode) => (on: boolean) => void): MenuEntry[] {
  const unavailable = tab?.profileId ? undefined : "choose a connection first";
  const id = tab?.id ?? "";
  return [
    { label: "Run selection or whole script", hint: keyOf("query.run"), run: () => runCommand("query.run"), onHighlight: scope("auto"), unavailable },
    { label: "Run current statement", hint: keyOf("query.runStatement"), run: () => runCommand("query.runStatement"), onHighlight: scope("statement"), unavailable },
    "sep",
    { ...planLabels(engine).run, run: () => void showPlan(id, "actual"), onHighlight: scope("auto"), unavailable: unavailable ?? planLabels(engine).run.unavailable },
  ];
}

/** Explain ▾: the face shows the estimated plan (never runs); the menu holds the plans that run and the SQL Server toggle. */
function PlanButton({ tab }: { tab: TabState | undefined }) {
  const engine = useProfiles((s) => s.profiles.find((p) => p.id === tab?.profileId)?.engine);
  const running = useRunning(tab);
  const off = !tab?.profileId || running;
  const why = !tab?.profileId ? "Connect to a server first" : "Wait for the running query";
  const perRun = actualPlanWithRun(engine);
  const on = !!engine && perRun && !!tab?.actualPlan;
  const labels = planLabels(engine);
  const id = tab?.id ?? "";
  return (
    <SplitButton
      caretLabel="Plan options"
      menuLabel="Plans"
      disabled={off}
      caretClass="text-muted"
      items={() => [
        { label: labels.estimated, detail: "doesn't run", hint: keyOf("query.estimatedPlan"), run: () => void showPlan(id, "estimated") },
        { ...labels.actual, run: () => void showPlan(id, "actual") },
        "sep",
        perRun
          ? { label: "Include actual plan on every run", checked: on, hint: keyOf("query.toggleActualPlan"), run: () => useTabs.getState().update(id, { actualPlan: !on }) }
          : { label: "Include actual plan on every run", checked: false, unavailable: PER_RUN_PLAN_ONLY, run: () => {} },
      ]}
      face={
        <button
          type="button"
          className="q-tb rounded-r-none"
          aria-disabled={off || undefined}
          onClick={guarded(off, () => void showPlan(id, "estimated"))}
          title={off ? why : tip(`${labels.estimated}, does not run the query${on ? ". Actual plan is included on every run" : ""}`, "query.estimatedPlan")}
          aria-label={`Explain: estimated plan, does not run the query${on ? ". Actual plan on" : ""}`}
        >
          <span className="relative flex text-muted">
            <Icon name="plan" />
            {on && <span aria-hidden className="absolute -right-0.5 -top-0.5 w-[7px] h-[7px] rounded-full bg-accent ring-[1.5px] ring-panel" />}
          </span>
          <span className="hidden min-[1440px]:inline">Explain</span>
        </button>
      }
    />
  );
}

function fmtClock(ms: number) {
  const s = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const ss = s % 60;
  return h ? `${h}:${String(m).padStart(2, "0")}:${String(ss).padStart(2, "0")}` : `${String(m).padStart(2, "0")}:${String(ss).padStart(2, "0")}`;
}

/** Begin Tx while none is open; while one is (or may be) open: its age and Commit / Rollback, which never collapse. */
function TxSlot({ tab }: { tab: TabState | undefined }) {
  const running = useRunning(tab);
  const offline = useConnections((s) => !!tab?.profileId && (!!s.disconnected[tab.profileId] || s.servers[tab.profileId]?.status === "error"));
  if (tab && (tab.txState === "open" || tab.txState === "unknown")) return <TxGroup tab={tab} running={running} />;
  const off = !tab?.profileId || running || offline;
  return (
    <button
      type="button"
      className="q-tb"
      aria-disabled={off || undefined}
      onClick={guarded(off, () => tab && void txAction(tab.id, "begin"))}
      title={!tab?.profileId ? "Connect to a server first" : running ? "Wait for the running query" : offline ? "Reconnect first" : "Begin transaction: pins a connection to this tab"}
      aria-label="Begin transaction"
    >
      <Icon name="transaction" className="text-muted" />
      <span className="hidden min-[1440px]:inline">Begin Tx</span>
    </button>
  );
}

/** Its own 1 Hz clock, so the rest of the bar never re-renders for it. */
function TxGroup({ tab, running }: { tab: TabState; running: boolean }) {
  const open = tab.txState === "open";
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!open) return;
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [open]);
  const age = now - (tab.txStartedAt ?? now);
  const long = open && age >= LONG_TX_MS;
  const why = running ? "Wait for the running query" : undefined;
  return (
    <div role="group" aria-label={open ? "Open transaction" : "Transaction state unknown"} className={`flex items-stretch h-[28px] rounded-md border bg-warn/10 ${long ? "border-warn" : "border-warn/45"}`}>
      <span
        className={`flex items-center gap-1.5 px-2 max-[1100px]:px-1.5 text-warn text-[11px] tracking-wide ${long ? "font-bold" : "font-semibold"}`}
        title={open ? "Transaction open: a connection is pinned to this tab" : "Transaction state unknown: commit or roll back to be sure"}
      >
        <span aria-hidden className="w-[7px] h-[7px] rounded-full bg-warn" />
        TX
        <span role="timer" className="q-mono tabular-nums">{open ? fmtClock(age) : "?"}</span>
      </span>
      <span aria-hidden className="w-px bg-warn/40" />
      <button type="button" className="q-tb !h-auto rounded-none max-[1100px]:!px-1.5" aria-disabled={running || undefined} title={why ?? "Commit"} onClick={guarded(running, () => void txAction(tab.id, "commit"))}>
        <Icon name="check" size={14} strokeWidth={2.25} className="text-ok" />
        Commit
      </button>
      <span aria-hidden className="w-px bg-warn/40" />
      <button type="button" className="q-tb !h-auto rounded-l-none max-[1100px]:!px-1.5" aria-disabled={running || undefined} title={why ?? "Roll back"} onClick={guarded(running, () => void txAction(tab.id, "rollback"))}>
        <Icon name="undo" size={14} strokeWidth={2.25} className="text-err" />
        Rollback
      </button>
    </div>
  );
}

/** Screen-reader announcement of the bar's state changes (never the ticking clock). */
function LiveStatus({ tab }: { tab: TabState | undefined }) {
  const running = useRunning(tab);
  const name = useProfiles((s) => s.profiles.find((p) => p.id === tab?.profileId)?.name);
  const lost = useConnections((s) => !!tab?.profileId && s.servers[tab.profileId]?.status === "error");
  const text = running ? `Query running on ${name ?? "the server"}` : lost ? `${name} connection lost` : tab?.txState === "open" ? "Transaction open, connection pinned" : "";
  return (
    <span role="status" className="sr-only">
      {text}
    </span>
  );
}

export function TopBar() {
  const tab = useActiveTab();
  const query = useRef<HTMLDivElement>(null);
  const app = useRef<HTMLDivElement>(null);
  const queryNav = useRovingToolbar(query);
  const appNav = useRovingToolbar(app);
  return (
    // The left toolbar never shrinks; the search box gives up its width down to icon + key, and below 1024 px to its
    // icon. Below 1100 px the gaps, the launch strip and the transaction group tighten so the bar fits the 960 px
    // minimum window with a transaction open and the Update button showing.
    <div data-focus-region="toolbar" className="q-topbar h-[40px] bg-panel border-b border-line flex items-center justify-between px-3 shrink-0 gap-3">
      <div ref={query} role="toolbar" aria-label="Query" className="flex items-center gap-3 max-[1100px]:gap-2 shrink-0 whitespace-nowrap" {...queryNav}>
        <div className="flex items-center gap-1.5 font-bold text-fg text-xs tracking-tight mr-1">
          <Logo className="w-[18px] h-[18px]" />
          <span className="hidden xl:inline">Querithm</span>
        </div>
        <NavButtons />
        <NewButton />
        <LaunchStrip tab={tab} />
        <PlanButton tab={tab} />
        <TxSlot tab={tab} />
      </div>
      <div ref={app} role="toolbar" aria-label="App" className="flex items-center justify-end gap-1 min-w-0" {...appNav}>
        <button
          type="button"
          className="mr-2 w-[200px] min-[1440px]:w-80 max-[1100px]:w-auto min-w-0 shrink bg-bg border border-line-strong rounded-md pl-2 pr-1 h-[28px] text-[12px] text-muted flex items-center justify-between gap-2 hover:border-accent"
          onClick={() => useUi.getState().setPalette({ mode: "objects" })}
          title={tip("Go to table, proc, or command", "palette.objects")}
          aria-label="Go to table, proc, or command"
          aria-haspopup="dialog"
        >
          <span className="flex items-center gap-1.5 min-w-0">
            <Icon name="search" size={14} strokeWidth={2} />
            <span className="truncate max-[1100px]:hidden">Go to table, proc… (&gt; for commands)</span>
          </span>
          <kbd className="px-1 bg-panel border border-line rounded text-[10px] text-fg q-mono shrink-0 max-[1024px]:hidden">{keyOf("palette.objects")}</kbd>
        </button>
        <button type="button" className="q-tb w-7 !px-0 text-muted hover:text-fg" title={tip("Query history", "view.history")} aria-label="Query history" onClick={() => useUi.getState().openDialog({ kind: "history" })}>
          <Icon name="history" />
        </button>
        <button type="button" className="q-tb w-7 !px-0 text-muted hover:text-fg" title={tip("Settings", "view.settings")} aria-label="Settings" onClick={() => useUi.getState().openDialog({ kind: "settings" })}>
          <Icon name="settings" />
        </button>
        <UpdateButton />
      </div>
      <LiveStatus tab={tab} />
    </div>
  );
}

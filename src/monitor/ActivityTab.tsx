// The Activity tab (Phase B): header (profile, connections, interval, pause/resume, lag / throttled / disconnected),
// overview strip, tabbed panes, a side panel for detail and diagnosis, and the kill dialog. Snapshots never re-render
// this component: panes subscribe to the model's topics (U1).
import { useCallback, useEffect, useState } from "react";
import type { ActivityRow, Engine, KillMode } from "../ipc/contract";
import { knownEngine } from "../engines";
import type { ToolTab } from "../store/toolTabs";
import { rpc } from "../ipc/client";
import { useProfiles } from "../store/profiles";
import { useConnections } from "../store/connections";
import { toast } from "../store/ui";
import { newQueryTab } from "../app/actions";
import { MonitorModel, COLUMNS, DEFAULT_COLUMNS } from "./model";
import { useTopics } from "./useModel";
import { registerModel, goToSession } from "./monitorActions";
import { SessionsPane } from "./SessionsPane";
import { BlockingTree } from "./BlockingTree";
import { LocksPane, WaitsPane, IoPane, TopQueriesPane } from "./Tables";
import { DeadlocksPane } from "./DeadlocksPane";
import { HistoryPane } from "./HistoryPane";
import { OverviewStrip } from "./OverviewStrip";
import { KillDialog } from "./KillDialog";
import { SidePanel, type SideView } from "./SidePanel";
import { PaneNotice, PaneStateBody, paneView } from "./PaneStateView";
import { killAvailability } from "./killRules";
import { escapeForDisplay, ms } from "./format";

/** Render counter for the "no tab-level re-render per snapshot" test (U1). */
export const activityTabRenders = { count: 0 };

type PaneId = "sessions" | "blocking" | "locks" | "waits" | "io" | "topQueries" | "deadlocks" | "history";
const PANES: { id: PaneId; title: string }[] = [
  { id: "sessions", title: "Sessions" }, { id: "blocking", title: "Blocking" }, { id: "locks", title: "Locks" }, { id: "waits", title: "Waits" },
  { id: "io", title: "I/O" }, { id: "topQueries", title: "Top queries" }, { id: "deadlocks", title: "Deadlocks" }, { id: "history", title: "History" },
];

export function ActivityTab({ tool }: { tool: ToolTab }) {
  activityTabRenders.count++;
  const profileId = tool.profileId!;
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === profileId));
  const engine: Engine = knownEngine(profile?.engine);
  const [model, setModel] = useState<MonitorModel | null>(null);
  const [startError, setStartError] = useState<string | null>(null);
  const [generation, setGeneration] = useState(0);
  const [pane, setPane] = useState<PaneId>("sessions");
  const [side, setSide] = useState<SideView | null>(null);
  const [kill, setKill] = useState<{ row: ActivityRow; mode: KillMode } | null>(null);
  const [columns, setColumns] = useState(DEFAULT_COLUMNS);

  useEffect(() => {
    let cancelled = false;
    let monitorId: string | null = null;
    let unregister = () => {};
    setStartError(null);
    (async () => {
      try {
        // The monitor uses the connection's remembered credentials: connect the server first if needed.
        if (!useConnections.getState().servers[profileId]) await useConnections.getState().connect(profileId);
        const started = await rpc("monitor.start", { profileId, intervalMs: 2_000 });
        if (cancelled) { void rpc("monitor.stop", { monitorId: started.monitorId }).catch(() => {}); return; }
        monitorId = started.monitorId;
        const m = new MonitorModel(started.monitorId, (ids) => rpc("monitor.texts", { monitorId: started.monitorId, ids }),
          // A live notification for deadlocks that happened while this monitor ran (X16), once each: the model leaves out
          // the first read's history without comparing the server's clock with this machine's.
          (fresh) => toast("warn", `Deadlock in ${fresh[0].database != null ? escapeForDisplay(fresh[0].database) : "the server"} — see the Deadlocks pane.`));
        unregister = registerModel(m, profileId);
        setModel(m);
      } catch (e) {
        if (!cancelled) setStartError(String((e as Error)?.message ?? e));
      }
    })();
    return () => {
      cancelled = true;
      unregister();
      // Tab closed: the loop stops and the connection closes (L3).
      if (monitorId) void rpc("monitor.stop", { monitorId }).catch(() => {});
    };
  }, [profileId, generation]);

  const restart = useCallback(() => { setModel(null); setGeneration((g) => g + 1); }, []);
  const actions = {
    detail: (row: ActivityRow) => setSide({ kind: "detail", row }),
    diagnose: (row: ActivityRow) => setSide({ kind: "diagnose", row }),
    kill: (row: ActivityRow, mode: KillMode) => setKill({ row, mode }),
  };

  if (startError) return <PaneStateBody view={{ kind: "stopped", reason: startError }} onRestart={restart} />;
  if (!model) return <PaneStateBody view={{ kind: "loading" }} />;
  return (
    <div className="flex-1 min-h-0 flex flex-col" data-testid="activity-tab">
      <Header model={model} profileName={profile?.name ?? "server"} onRestart={restart} />
      <OverviewStrip model={model} engine={engine} />
      <div className="h-[30px] px-2 border-b border-line flex items-center gap-0.5 text-[11.5px] shrink-0" role="tablist">
        {PANES.map((p) => (
          <button key={p.id} role="tab" aria-selected={pane === p.id}
            className={`px-2.5 h-full ${pane === p.id ? "text-fg font-semibold border-b-2 border-accent" : "text-muted hover:text-fg"}`} onClick={() => setPane(p.id)}>
            {p.title}
          </button>
        ))}
      </div>
      <div className="flex-1 min-h-0 flex">
        <div className="flex-1 min-w-0 flex flex-col">
          {pane === "sessions" && <SessionsView model={model} columns={columns} setColumns={setColumns} actions={actions} onRestart={restart} />}
          {pane === "blocking" && <PaneGate model={model} pane="sessions" onRestart={restart}><BlockingTree model={model} actions={actions} /></PaneGate>}
          {pane === "locks" && <LocksPane model={model} />}
          {pane === "waits" && <WaitsPane model={model} engine={engine} />}
          {pane === "io" && <IoPane model={model} />}
          {pane === "topQueries" && <TopQueriesPane model={model} engine={engine} onOpenText={(t) => newQueryTab({ profileId, text: t })} />}
          {pane === "deadlocks" && <DeadlocksPane model={model} profileId={profileId} />}
          {pane === "history" && <HistoryPane model={model} />}
        </div>
        {side && <SidePanel model={model} view={side} engine={engine} profileId={profileId} onClose={() => setSide(null)} />}
      </div>
      {kill && <KillDialog model={model} row={kill.row} mode={kill.mode} prod={profile?.environment === "prod"} profileId={profileId} engine={engine} onClose={() => setKill(null)} />}
    </div>
  );
}

function PaneGate({ model, pane, children, onRestart }: { model: MonitorModel; pane: "sessions"; children: React.ReactNode; onRestart: () => void }) {
  useTopics(model, ["rows", "status", "connection"]);
  const view = paneView(model, pane, model.received, "No other sessions");
  if (view.kind !== "content" && view.kind !== "empty") return <PaneStateBody view={view} onRestart={onRestart} />;
  // The tree is built from the sessions snapshot: its notices (paused, truncated, a stale error) apply here too.
  return (
    <div className="flex-1 min-h-0 flex flex-col">
      <PaneNotice model={model} pane={pane} />
      {children}
    </div>
  );
}

function Header({ model, profileName, onRestart }: { model: MonitorModel; profileName: string; onRestart: () => void }) {
  useTopics(model, ["connection", "counts"]);
  const c = model.counts();
  const [interval, setIntervalMs] = useState(2_000);
  const conn = model.connection;
  return (
    <div className="h-[34px] px-3 border-b border-line flex items-center gap-3 text-[11.5px] shrink-0 whitespace-nowrap [&>*]:shrink-0" data-testid="monitor-header">
      <span className="font-semibold text-fg max-w-[200px] truncate">{profileName}</span>
      <span className="text-muted !shrink min-w-0 truncate" title="Connections this monitor holds: 1 while running, +1 while a kill runs on its own connection.">
        monitor: {model.connectionCount} connection{model.connectionCount === 1 ? "" : "s"}, 2 during a kill
      </span>
      <span className="text-muted !shrink min-w-0 truncate">{c.total} sessions · {c.running} running · <span className={c.blocked ? "text-err" : ""}>{c.blocked} blocked</span> · {c.sleepingWithTx} idle with open tx · {c.headBlockers} head blockers</span>
      <span className="flex-1 !shrink" />
      {model.stopped ? (
        <span className="text-err !shrink min-w-0 flex items-center gap-1"><span className="truncate" title={model.stopped}>Monitor stopped: {model.stopped}</span><button className="q-btn ml-1 shrink-0" onClick={onRestart}>Restart</button></span>
      ) : conn === "reconnecting" ? (
        <span className="text-err">disconnected — reconnecting{model.retryInMs != null ? ` in ${Math.ceil(model.retryInMs / 1000)} s` : ""}</span>
      ) : conn === "connecting" ? <span className="text-muted">connecting…</span> : null}
      {model.lagMs > 500 && <span className="text-warn" title="The last tick started this late (a slow server or a long secondary job).">lag {ms(model.lagMs)}</span>}
      {model.throttled && <span className="text-warn">monitor throttled to protect the server</span>}
      {model.paused && !model.stopped && (
        <span className="text-warn font-semibold" data-testid="monitor-paused" title="Nothing is read from the server until you resume; the panes keep the last data, dimmed.">
          Paused
        </span>
      )}
      <select className="q-input h-[22px] py-0" value={interval} aria-label="Refresh interval"
        onChange={(e) => { const v = Number(e.target.value); setIntervalMs(v); void rpc("monitor.setInterval", { monitorId: model.monitorId, intervalMs: v }).catch((err) => toast("error", String(err?.message ?? err))); }}>
        {[1_000, 2_000, 5_000, 10_000, 30_000].map((v) => <option key={v} value={v}>every {v / 1000} s</option>)}
      </select>
      <button className="q-btn" disabled={!!model.stopped} onClick={() => void rpc(model.paused ? "monitor.resume" : "monitor.pause", { monitorId: model.monitorId })}>
        {model.paused ? "Resume" : "Pause"}
      </button>
    </div>
  );
}

function SessionsView({ model, columns, setColumns, actions, onRestart }: {
  model: MonitorModel; columns: string[]; setColumns: (c: string[]) => void;
  actions: { detail: (r: ActivityRow) => void; diagnose: (r: ActivityRow) => void; kill: (r: ActivityRow, m: KillMode) => void }; onRestart: () => void;
}) {
  useTopics(model, ["status", "connection", "selection"]);
  const [chooser, setChooser] = useState(false);
  const view = paneView(model, "sessions", model.received, "No other sessions");
  if (view.kind !== "content" && view.kind !== "empty") return <PaneStateBody view={view} onRestart={onRestart} />;
  const row = model.selected;
  const connected = model.connection === "connected" && !model.stopped;
  const term = killAvailability(row, model.caps, "terminateSession", connected);
  const cancel = killAvailability(row, model.caps, "cancelRequest", connected);
  const f = model.filters;
  return (
    <div className="flex-1 min-h-0 flex flex-col">
      <PaneNotice model={model} pane="sessions" />
      {model.caps?.sessionsReason && <div className="px-3 py-1 text-[11px] text-warn border-b border-line">{model.caps.sessionsReason}</div>}
      <div className="px-2 py-1 flex items-center gap-2 border-b border-line text-[11px] flex-wrap">
        <label className="flex items-center gap-1"><input type="checkbox" checked={f.hideIdle} onChange={(e) => model.setFilters({ hideIdle: e.target.checked })} /> hide idle</label>
        <label className="flex items-center gap-1"><input type="checkbox" checked={f.hideSystem} onChange={(e) => model.setFilters({ hideSystem: e.target.checked })} /> hide system</label>
        <input className="q-input h-[22px] w-28" placeholder="database" value={f.database ?? ""} onChange={(e) => model.setFilters({ database: e.target.value || undefined })} />
        <input className="q-input h-[22px] w-28" placeholder="login" value={f.login ?? ""} onChange={(e) => model.setFilters({ login: e.target.value || undefined })} />
        <input className="q-input h-[22px] w-28" placeholder="program" value={f.program ?? ""} onChange={(e) => model.setFilters({ program: e.target.value || undefined })} />
        <div className="relative">
          <button className="q-btn" onClick={() => setChooser((v) => !v)} aria-expanded={chooser}>Columns</button>
          {chooser && (
            <div className="absolute z-30 top-6 left-0 bg-raised border border-line rounded p-2 w-48 max-h-80 overflow-auto shadow-xl">
              {COLUMNS.map((c) => (
                <label key={c.id} className="flex items-center gap-1.5 py-0.5">
                  <input type="checkbox" checked={columns.includes(c.id)} onChange={(e) => setColumns(e.target.checked ? COLUMNS.filter((x) => columns.includes(x.id) || x.id === c.id).map((x) => x.id) : columns.filter((x) => x !== c.id))} />
                  {c.title}
                </label>
              ))}
            </div>
          )}
        </div>
        <span className="flex-1" />
        <span className="text-muted">{row ? `session ${row.key.id}` : "select a session"}</span>
        <button className="q-btn" disabled={!row} onClick={() => row && actions.detail(row)}>Detail</button>
        <button className="q-btn" disabled={!row} onClick={() => row && actions.diagnose(row)}>Why is it slow?</button>
        {term.goToSession ? (
          <button className="q-btn" title={term.reason} onClick={() => goToSession(term.goToSession!)}>Go to tab</button>
        ) : (
          <>
            {/* Unavailable: focusable, the reason in the tooltip; the danger button turns plain (tokens.css) so it never looks armed. */}
            <button className="q-btn" aria-disabled={!cancel.enabled || undefined} title={cancel.reason ?? "Cancel the running query"} onClick={() => cancel.enabled && row && actions.kill(row, "cancelRequest")} data-testid="btn-cancel">Cancel query</button>
            <button className="q-btn q-btn-danger" aria-disabled={!term.enabled || undefined} title={term.reason ?? "Terminate the session"} onClick={() => term.enabled && row && actions.kill(row, "terminateSession")} data-testid="btn-kill">Kill session</button>
          </>
        )}
      </div>
      {view.kind === "empty" ? <PaneStateBody view={view} /> : <SessionsPane model={model} columns={columns} />}
    </div>
  );
}

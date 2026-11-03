// The same pane states in every pane (U9): loading, empty, paused, degraded, error with retry countdown, truncated,
// and monitor disconnected. Each renders its text from PaneStatus; missing data is never shown as an empty table.
import { useEffect, useState } from "react";
import type { MonitorPane, PaneStatus } from "../ipc/contract";
import type { MonitorModel } from "./model";
import { useTopics } from "./useModel";

export type PaneView =
  | { kind: "content" }
  | { kind: "loading" }
  | { kind: "empty"; text: string }
  | { kind: "degraded"; reason: string }
  | { kind: "error"; message: string; retryInMs?: number | null }
  | { kind: "disconnected"; reason: string; retryInMs?: number | null }
  | { kind: "stopped"; reason: string };

/** What a pane shows instead of (or above) its rows. */
export function paneView(m: MonitorModel, pane: MonitorPane, hasRows: boolean, emptyText: string): PaneView {
  if (m.stopped) return { kind: "stopped", reason: m.stopped };
  if (m.connection === "reconnecting") return { kind: "disconnected", reason: m.connectionError ?? "connection lost", retryInMs: m.retryInMs };
  const st: PaneStatus | undefined = m.status.get(pane);
  if (st?.state === "degraded") return { kind: "degraded", reason: st.reason ?? "not available" };
  if (st?.state === "error" && !hasRows) return { kind: "error", message: st.reason ?? "error", retryInMs: st.retryInMs };
  if (!hasRows && (!st || st.state === "collecting" || m.connection === "connecting")) return { kind: "loading" };
  if (!hasRows) return { kind: "empty", text: emptyText };
  return { kind: "content" };
}

function Countdown({ ms }: { ms: number }) {
  const [left, setLeft] = useState(ms);
  useEffect(() => {
    setLeft(ms);
    const start = Date.now();
    const t = setInterval(() => setLeft(Math.max(0, ms - (Date.now() - start))), 500);
    return () => clearInterval(t);
  }, [ms]);
  return <>{Math.ceil(left / 1000)} s</>;
}

export function PaneStateBody({ view, onRestart }: { view: PaneView; onRestart?: () => void }) {
  if (view.kind === "content") return null;
  const box = "flex-1 flex flex-col items-center justify-center gap-2 text-xs p-6 text-center";
  switch (view.kind) {
    case "loading":
      return <div className={`${box} text-muted`} data-pane-state="loading">collecting…</div>;
    case "empty":
      return <div className={`${box} text-muted`} data-pane-state="empty">{view.text}</div>;
    case "degraded":
      return (
        <div className={`${box} text-muted`} data-pane-state="degraded">
          <div className="text-warn font-semibold">Not available</div>
          <div className="max-w-[560px] select-text">{view.reason}</div>
        </div>
      );
    case "error":
      return (
        <div className={`${box}`} data-pane-state="error">
          <div className="text-err font-semibold select-text">{view.message}</div>
          {view.retryInMs != null && <div className="text-muted">retrying in <Countdown ms={view.retryInMs} /></div>}
        </div>
      );
    case "disconnected":
      return (
        <div className={`${box}`} data-pane-state="disconnected">
          <div className="text-err font-semibold select-text">Monitor disconnected: {view.reason}</div>
          {view.retryInMs != null && <div className="text-muted">reconnecting in <Countdown ms={view.retryInMs} /></div>}
        </div>
      );
    case "stopped":
      return (
        <div className={`${box}`} data-pane-state="stopped">
          <div className="text-err font-semibold">Monitor stopped</div>
          <div className="text-muted select-text">{view.reason}</div>
          {onRestart && <button className="q-btn mt-1" onClick={onRestart}>Restart monitor</button>}
        </div>
      );
  }
}

/** A strip above a pane's rows for states that keep the rows visible (paused, truncated, notes, stale error). */
export function PaneNotice({ model, pane }: { model: MonitorModel; pane: MonitorPane }) {
  useTopics(model, ["status", "connection"]);
  const st = model.status.get(pane);
  const items: { cls: string; text: string; key: string }[] = [];
  // Deadlocks are history read on demand: pausing stops new ones arriving, it does not make the list stale.
  const at = model.pausedAt ? new Date(model.pausedAt).toLocaleTimeString() : "now";
  if (model.paused) items.push({ key: "p", cls: "text-warn", text: pane === "deadlocks" ? `Paused at ${at} — new deadlocks are not read until you resume.` : `Paused at ${at} — data is dimmed and not refreshed.` });
  if (st?.state === "truncated") items.push({ key: "t", cls: "text-warn", text: truncatedText(pane, st.total ?? 0) });
  if (st?.state === "resetDetected") items.push({ key: "r", cls: "text-warn", text: "Counter reset detected (server restart or statistics cleared): this interval is not shown." });
  if (st?.state === "error") items.push({ key: "e", cls: "text-err", text: `${st.reason ?? "error"}${st.retryInMs != null ? ` — retrying in ${Math.ceil(st.retryInMs / 1000)} s` : ""}` });
  if ((st?.state === "ok" || st?.state === "truncated") && st.reason) items.push({ key: "n", cls: "text-muted", text: st.reason });
  if (items.length === 0) return null;
  return (
    <div className="px-3 py-1 border-b border-line text-[11px] space-y-0.5 bg-panel-2 select-text" data-pane-notice>
      {items.map((i) => <div key={i.key} className={i.cls}>{i.text}</div>)}
    </div>
  );
}

/** The sidecar's per-pane caps (ActivityMonitor.MaxSessions / MaxLocks / MaxTopQueries / MaxDeadlocks). */
const capOf = (pane: MonitorPane) => (pane === "locks" ? 2_000 : pane === "topQueries" ? 100 : pane === "deadlocks" ? 200 : 1_000);

/** Deadlocks keep the newest events (B7, X13); the other panes keep their rows in priority order. */
const truncatedText = (pane: MonitorPane, total: number) => {
  const [cap, of] = [capOf(pane).toLocaleString("en-US"), total.toLocaleString("en-US")];
  return pane === "deadlocks" ? `Showing the newest ${cap} of ${of} deadlocks.` : `Showing the first ${cap} of ${of}, by priority.`;
};

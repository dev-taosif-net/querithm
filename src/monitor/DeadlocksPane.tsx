// Deadlock history (B7): list + graph (processes ↔ resources, victim marked; SVG with theme tokens, no library).
// Strictly read-only on the server. Exporting the original XML (.xdl) contains query text, so it asks first. The full
// history read is kept in the monitor model (once per monitor, Refresh to read again); server text is shown escaped (S4).
import { useEffect, useMemo, useState } from "react";
import type { DeadlockEvent, DeadlockProcess } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { ask, toast } from "../store/ui";
import { newQueryTab } from "../app/actions";
import type { MonitorModel } from "./model";
import { escapeBlock, escapeForDisplay, ms } from "./format";
import { useTopics } from "./useModel";
import { PaneNotice, PaneStateBody, paneView } from "./PaneStateView";
import { saveExport } from "./exportFile";

export function DeadlocksPane({ model, profileId }: { model: MonitorModel; profileId: string }) {
  useTopics(model, ["deadlocks", "status", "connection"]);
  const [selected, setSelected] = useState<string | null>(null);
  // The full read runs once per monitor (and on Refresh), never on every visit to this pane (B7, metric 30).
  const read = (force = false) => model.readDeadlocks(() => rpc("monitor.deadlocks", { monitorId: model.monitorId }), force);
  useEffect(() => { void read(); }, [model]); // eslint-disable-line react-hooks/exhaustive-deps
  const events = model.deadlockRead?.events;
  const error = model.deadlockRead?.error ?? null;
  const all = useMemo(() => {
    const byId = new Map<string, DeadlockEvent>();
    for (const e of [...model.deadlocks, ...(events ?? [])]) if (!byId.has(e.id)) byId.set(e.id, e);
    return [...byId.values()].sort((a, b) => b.at - a.at).slice(0, 200);
  }, [model.deadlocks, events]);
  const view = paneView(model, "deadlocks", all.length > 0, "No deadlocks in the last 24 hours");
  const ev = all.find((e) => e.id === selected) ?? all[0];
  const refresh = (
    <button className="q-btn" disabled={model.readingDeadlocks} onClick={() => void read(true)} title="Read the server's deadlock history again">
      {model.readingDeadlocks ? "Reading…" : "Refresh"}
    </button>
  );
  if (all.length === 0 && model.readingDeadlocks && view.kind === "empty") return <PaneStateBody view={{ kind: "loading" }} />;
  if (error && all.length === 0) return <><PaneStateBody view={{ kind: "degraded", reason: error }} /><div className="p-2 flex justify-center">{refresh}</div></>;
  if (view.kind === "empty") return <><PaneStateBody view={view} /><div className="p-2 flex justify-center">{refresh}</div></>;
  if (view.kind !== "content") return <PaneStateBody view={view} />;
  return (
    <div className="flex-1 min-h-0 flex flex-col">
      <PaneNotice model={model} pane="deadlocks" />
      <div className="px-3 py-1 border-b border-line flex items-center gap-2 text-[11px]">
        <span className="text-muted">{all.length} deadlock{all.length === 1 ? "" : "s"}{error ? ` · last read failed: ${error}` : ""}</span>
        <span className="flex-1" />
        {refresh}
      </div>
      <div className="flex-1 min-h-0 flex">
        <div className="w-[300px] shrink-0 border-r border-line overflow-auto text-[11.5px]" data-testid="deadlock-list">
          {all.map((e) => (
            <button key={e.id} className={`block w-full text-left px-3 py-1.5 border-b border-line/50 ${ev?.id === e.id ? "bg-accent/15 q-hl" : "hover:bg-hover"}`} onClick={() => setSelected(e.id)}>
              <div className="text-fg">{new Date(e.at).toLocaleString()}</div>
              <div className="text-muted">{escapeForDisplay(e.database ?? "—")} · {e.processes.length} processes{e.partial ? " · partial" : ""}</div>
            </button>
          ))}
        </div>
        {ev && <DeadlockDetail event={ev} model={model} profileId={profileId} />}
      </div>
    </div>
  );
}

function DeadlockDetail({ event, model, profileId }: { event: DeadlockEvent; model: MonitorModel; profileId: string }) {
  const exportXdl = async () => {
    const ok = await ask<boolean>((resolve) => ({ kind: "confirm", title: "Export the deadlock graph?", body: "The .xdl file contains the query text of every process in the deadlock (user data).", ok: "Export", resolve }));
    if (!ok) return;
    try {
      const f = await rpc("monitor.deadlockXml", { monitorId: model.monitorId, eventId: event.id, confirmedText: true });
      const saved = await saveExport(f.path, f.fileName, { name: "Deadlock graph", extension: "xdl", mime: "application/xml" });
      if (saved) toast("success", `Saved ${saved}`);
    } catch (e) {
      toast("error", String((e as Error)?.message ?? e));
    }
  };
  return (
    <div className="flex-1 min-w-0 flex flex-col">
      <div className="px-3 py-1.5 border-b border-line flex items-center gap-2 text-[11px]">
        <span className="text-muted">{event.source}{event.partial ? " · the report was cut off; what could be read is shown" : ""}</span>
        <span className="flex-1" />
        <button className="q-btn" onClick={() => void exportXdl()}>Export .xdl…</button>
      </div>
      <div className="flex-1 min-h-0 overflow-auto">
        <DeadlockGraph event={event} />
        <div className="p-3 space-y-2 text-[11.5px]">
          {event.processes.map((p) => <ProcessCard key={p.id} p={p} profileId={profileId} />)}
        </div>
      </div>
    </div>
  );
}

function ProcessCard({ p, profileId }: { p: DeadlockProcess; profileId: string }) {
  return (
    <div className={`border rounded p-2 ${p.victim ? "border-err" : "border-line"}`}>
      <div className="flex gap-2 items-baseline">
        <b className={p.victim ? "text-err" : "text-fg"}>{p.victim ? "Victim · " : ""}session {p.spid ?? "?"}</b>
        <span className="text-muted">{escapeForDisplay([p.login, p.host, p.program].filter(Boolean).join(" · "))}</span>
        <span className="flex-1" />
        {p.text && <button className="q-btn !h-[18px] text-[10px]" onClick={() => void navigator.clipboard.writeText(p.text!)}>Copy text</button>}
        {p.text && <button className="q-btn !h-[18px] text-[10px]" onClick={() => newQueryTab({ profileId, text: p.text!, title: `Deadlock ${p.spid}.sql` })}>Open in new tab</button>}
      </div>
      <div className="text-muted">{escapeForDisplay([p.isolationLevel, p.lockMode && `mode ${p.lockMode}`, p.waitResource && `waits on ${p.waitResource}`, p.waitMs != null && `for ${ms(p.waitMs)}`].filter(Boolean).join(" · "))}</div>
      {p.text && <pre className="q-mono text-[11px] mt-1 whitespace-pre-wrap max-h-[120px] overflow-auto select-text">{escapeBlock(p.text)}</pre>}
    </div>
  );
}

/** Processes on the left, resources on the right; owner edges solid, waiter edges dashed; the victim in the error colour.
 *  Drawn at its own size (1 unit = 1 px): scaled to the pane (380 px in a 960 px window) its 11 px labels shrank to 6 px.
 *  A narrower pane scrolls sideways. */
export function DeadlockGraph({ event }: { event: DeadlockEvent }) {
  const W = 760, rowH = 46;
  const procs = event.processes;
  const res = event.resources;
  const h = Math.max(procs.length, res.length) * rowH + 24;
  const py = (i: number) => 20 + i * rowH;
  const ry = py;
  const pIndex = new Map(procs.map((p, i) => [p.id, i]));
  return (
    <div className="border-b border-line overflow-x-auto">
      <svg width={W} height={h} viewBox={`0 0 ${W} ${h}`} className="block" role="img" aria-label="Deadlock graph" data-testid="deadlock-graph">
        {res.flatMap((r, ri) =>
          r.owners.filter((o) => pIndex.has(o.processId)).map((o, oi) => {
            const pi = pIndex.get(o.processId)!;
            return (
              <line key={`${r.id}-${o.processId}-${oi}`} x1={230} y1={py(pi) + 14} x2={500} y2={ry(ri) + 14}
                stroke={o.role === "owner" ? "var(--q-accent)" : "var(--q-err)"} strokeWidth={1.5} strokeDasharray={o.role === "waiter" ? "5 4" : undefined} />
            );
          }),
        )}
        {procs.map((p, i) => (
          <g key={p.id}>
            <rect x={10} y={py(i)} width={220} height={28} rx={14} fill="var(--q-panel)" stroke={p.victim ? "var(--q-err)" : "var(--q-line-strong)"} strokeWidth={p.victim ? 2.5 : 1} />
            <text x={24} y={py(i) + 18} fill="var(--q-fg)" fontSize={11}>{`${p.victim ? "✕ " : ""}session ${p.spid ?? "?"}${p.lockMode ? ` (${escapeForDisplay(p.lockMode)})` : ""}`}</text>
          </g>
        ))}
        {res.map((r, i) => (
          <g key={r.id}>
            <rect x={500} y={ry(i)} width={250} height={28} fill="var(--q-raised)" stroke="var(--q-line-strong)" />
            <text x={510} y={ry(i) + 18} fill="var(--q-fg)" fontSize={11}>{`${escapeForDisplay(r.kind)}: ${escapeForDisplay(r.object ?? r.id).slice(0, 34)}`}</text>
          </g>
        ))}
      </svg>
    </div>
  );
}

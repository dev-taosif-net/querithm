// The resizable side panel: request detail and live plan (B5), "Why is it slow?" (B3b). Server-returned text and
// identifiers are rendered as text, never as markup (W7).
import { useEffect, useState } from "react";
import type { ActivityRow, Diagnosis, Engine, RequestDetail } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { PlanDiagram } from "../plans/PlanView";
import { newQueryTab } from "../app/actions";
import type { MonitorModel } from "./model";
import { goToSession } from "./monitorActions";
import { escapeBlock, escapeForDisplay, keyOf, ms } from "./format";
import { useTopics } from "./useModel";

export type SideView = { kind: "detail" | "diagnose"; row: ActivityRow };

export function SidePanel({ model, view, engine, profileId, onClose }: { model: MonitorModel; view: SideView; engine: Engine; profileId: string; onClose: () => void }) {
  const [width, setWidth] = useState(460);
  const [tab, setTab] = useState(view.kind);
  useEffect(() => setTab(view.kind), [view]);
  // Tied to the exact session: once it leaves the snapshot, what the panel shows is history and says so.
  useTopics(model, ["rows"]);
  const ended = model.byKey.size > 0 && !model.byKey.has(keyOf(view.row.key));
  return (
    <div className="shrink-0 border-l border-line bg-panel flex flex-col min-h-0 relative" style={{ width }} data-testid="side-panel">
      <div
        className="absolute left-0 top-0 bottom-0 w-1 cursor-col-resize hover:bg-accent/40"
        onMouseDown={(e) => {
          const x0 = e.clientX, w0 = width;
          const move = (ev: MouseEvent) => setWidth(Math.min(1000, Math.max(300, w0 - (ev.clientX - x0))));
          const up = () => { window.removeEventListener("mousemove", move); window.removeEventListener("mouseup", up); };
          window.addEventListener("mousemove", move);
          window.addEventListener("mouseup", up);
        }}
      />
      <div className="h-[30px] px-2 border-b border-line flex items-center gap-1 text-[11px]">
        <button className={`px-2 h-full ${tab === "detail" ? "text-fg font-semibold border-b-2 border-accent" : "text-muted"}`} onClick={() => setTab("detail")}>Detail</button>
        <button className={`px-2 h-full ${tab === "diagnose" ? "text-fg font-semibold border-b-2 border-accent" : "text-muted"}`} onClick={() => setTab("diagnose")}>Why is it slow?</button>
        <span className="flex-1 text-right text-muted truncate">session {view.row.key.id}</span>
        <button className="w-5 h-5 text-muted hover:text-fg" onClick={onClose} aria-label="Close panel">×</button>
      </div>
      {ended && (
        <div className="px-3 py-1.5 text-[11px] text-warn bg-warn/10 border-b border-line" data-testid="side-ended">
          Session {view.row.key.id} has ended; this is what it showed when you opened it.
        </div>
      )}
      {tab === "detail" ? <Detail model={model} row={view.row} engine={engine} profileId={profileId} /> : <DiagnosisView model={model} row={view.row} />}
    </div>
  );
}

/** The session's text can be opened in a new tab behind EXPLAIN (PostgreSQL); SQL Server's detail carries its live or cached plan. */
const EXPLAIN_TEXT: Record<Engine, boolean> = { mssql: false, pg: true, sqlite: false, mySql: true, duckDb: false };

function Detail({ model, row, engine, profileId }: { model: MonitorModel; row: ActivityRow; engine: Engine; profileId: string }) {
  const [d, setD] = useState<RequestDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setD(null);
    setError(null);
    rpc("monitor.requestDetail", { monitorId: model.monitorId, key: row.key }).then(setD, (e) => setError(String(e?.message ?? e)));
  }, [model.monitorId, row.key.id, row.key.instance]);
  if (error) return <div className="p-3 text-err text-xs select-text">{error}</div>;
  if (!d) return <div className="p-3 text-muted text-xs">Loading…</div>;
  const t = d.text ?? "";
  const s = d.statementStart ?? 0, e = d.statementEnd ?? t.length;
  return (
    <div className="flex-1 min-h-0 flex flex-col text-xs">
      <div className="p-2 space-y-1 border-b border-line">
        <div className="flex gap-2 flex-wrap">
          {d.txElapsedMs != null && <span className="text-muted">transaction {ms(d.txElapsedMs)}</span>}
          {d.openTx != null && <span className="text-muted">open tx {d.openTx}</span>}
          {d.isolationLevel && <span className="text-muted">{escapeForDisplay(d.isolationLevel)}</span>}
          {d.textNote && <span className="text-warn">{escapeForDisplay(d.textNote)}</span>}
        </div>
        <div className="flex gap-1.5">
          <button className="q-btn" disabled={!t} onClick={() => newQueryTab({ profileId, text: t, title: `Session ${row.key.id}.sql` })}>Open in new tab</button>
          {EXPLAIN_TEXT[engine] && t && (
            <button className="q-btn" title="Opens a new tab with EXPLAIN in front of the text; nothing runs until you run it." onClick={() => newQueryTab({ profileId, text: `EXPLAIN\n${t}`, title: `Explain ${row.key.id}.sql` })}>
              EXPLAIN this text in a new tab
            </button>
          )}
        </div>
      </div>
      <pre className="q-mono text-[11px] p-2 max-h-[30%] overflow-auto whitespace-pre-wrap select-text border-b border-line" data-testid="detail-text">
        {/* Sliced at the server's offsets first, then each part escaped (S4): escaping changes lengths. */}
        {t ? <>{escapeBlock(t.slice(0, s))}<mark className="bg-warn/25 text-fg">{escapeBlock(t.slice(s, e))}</mark>{escapeBlock(t.slice(e))}</> : "No text."}
      </pre>
      <div className="flex-1 min-h-[200px] flex flex-col">
        {d.plan ? (
          <>
            <div className="px-2 py-1 text-[11px] text-muted border-b border-line">{d.planSource === "live" ? "Live plan (in-flight statistics)" : "Cached plan"}</div>
            <PlanDiagram plans={[{ plan: d.plan.plan, raw: d.plan.raw }]} />
          </>
        ) : (
          <div className="p-3 text-muted select-text" data-testid="plan-reason">{d.planReason ?? "No plan."}</div>
        )}
      </div>
    </div>
  );
}

const SEV: Record<string, string> = { high: "text-err", medium: "text-warn", low: "text-fg", info: "text-muted" };

function DiagnosisView({ model, row }: { model: MonitorModel; row: ActivityRow }) {
  const [d, setD] = useState<Diagnosis | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setD(null);
    rpc("monitor.diagnose", { monitorId: model.monitorId, key: row.key }).then(setD, (e) => setError(String(e?.message ?? e)));
  }, [model.monitorId, row.key.id, row.key.instance]);
  if (error) return <div className="p-3 text-err text-xs select-text">{error}</div>;
  if (!d) return <div className="p-3 text-muted text-xs">Diagnosing…</div>;
  return (
    <div className="flex-1 min-h-0 overflow-auto p-2 space-y-2 text-xs select-text" data-testid="diagnosis">
      {d.findings.map((f, i) => (
        <div key={i} className="border border-line rounded p-2 bg-bg">
          <div className={`font-semibold ${SEV[f.severity] ?? ""}`}>{f.message}</div>
          {f.nextStep && <div className="mt-1 text-fg">→ {f.nextStep}</div>}
          {f.evidence.length > 0 && <ul className="mt-1 text-muted list-disc pl-4">{f.evidence.map((e, j) => <li key={j}>{e}</li>)}</ul>}
          {f.ownSessionId && <button className="q-btn mt-1.5" onClick={() => goToSession(f.ownSessionId!)}>Go to tab</button>}
        </div>
      ))}
      {d.notes?.map((n, i) => <div key={i} className="text-muted">{n}</div>)}
    </div>
  );
}

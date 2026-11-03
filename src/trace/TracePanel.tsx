// SQL Server Trace (Extended Events, §11). This is a trace timeline, not a debugger, and is labeled as such.
import { useMemo, useState, type ReactNode } from "react";
import type { TabState } from "../store/tabs";
import { useTabs } from "../store/tabs";
import { useDiag } from "../store/diag";
import { useProfiles } from "../store/profiles";
import { useConnections } from "../store/connections";
import { toggleTrace } from "../app/actions";
import { editorForTab } from "../editor/editors";
import { monaco } from "../editor/monaco";
import { Empty } from "../results/ResultsPanel";
import { engineTraits, ENGINE_LIST, ENGINES, type EngineTraits } from "../engines";

const engineNames = (has: (t: EngineTraits) => boolean) => ENGINE_LIST.filter((e) => has(ENGINES[e])).map((e) => ENGINES[e].shortLabel).join(", ");
const TRACE_ELSEWHERE = `Trace (Extended Events) is available for ${engineNames((t) => t.trace)}. ${engineNames((t) => t.debugger)} has a step debugger instead (Debug tab).`;

const decorations = new Map<string, monaco.editor.IEditorDecorationsCollection>();

/** Highlights the selected event's line in the procedure's definition tab, or in this tab for ad-hoc batches. */
function highlight(tabs: Record<string, TabState>, current: TabState, procName: string | null | undefined, line: number | null | undefined) {
  if (!line) return;
  const target =
    (procName &&
      Object.values(tabs).find((t) => {
        const b = t.boundObject;
        const p = procName.toLowerCase();
        return b && (p === `${b.schema}.${b.name}`.toLowerCase() || p === b.name.toLowerCase());
      })) ||
    current;
  const ed = editorForTab(target.id);
  if (!ed) return;
  decorations.get(target.id)?.clear();
  decorations.set(
    target.id,
    ed.createDecorationsCollection([{ range: new monaco.Range(line, 1, line, 1), options: { isWholeLine: true, className: "q-trace-line", glyphMarginClassName: "q-breakpoint" } }]),
  );
  ed.revealLineInCenter(line);
}

/** `lead`: the Runtime view's Trace | Debug switch, at the start of the tool row. */
export function TracePanel({ tab, lead }: { tab: TabState; lead?: ReactNode }) {
  const trace = useDiag((s) => (tab.traceId ? s.traces[tab.traceId] : undefined));
  const tabs = useTabs((s) => s.tabs);
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === tab.profileId));
  const caps = useConnections((s) => (tab.profileId ? s.servers[tab.profileId]?.caps : undefined));
  const [sel, setSel] = useState<number | null>(null);
  const events = trace?.events ?? [];
  const maxDur = useMemo(() => Math.max(1, ...events.map((e) => e.durationUs ?? 0)), [events]);

  if (!profile || !engineTraits(profile.engine).trace) return <Empty text={TRACE_ELSEWHERE} />;

  const available = caps?.trace.available !== false;
  return (
    <div className="flex-1 flex flex-col min-h-0">
      <div className="h-[28px] px-2 border-b border-line flex items-center gap-2 text-[11px] shrink-0 whitespace-nowrap overflow-hidden">
        {lead}
        <button className={`q-btn ${tab.traceId ? "q-btn-danger" : "q-btn-primary"}`} disabled={!available} title={caps?.trace.reason ?? undefined} onClick={() => void toggleTrace(tab.id)}>
          {tab.traceId ? "■ Stop trace" : "● Start trace"}
        </button>
        {!available && <span className="text-warn">{caps?.trace.reason}</span>}
        {tab.traceId && (
          <span className="text-ok">
            ● Live ring-buffer trace on SPID {tab.spid ?? "?"} — run the procedure in this tab to capture statements
          </span>
        )}
        <div className="flex-1" />
        {tab.traceId && <button className="q-btn" onClick={() => useDiag.getState().clearTrace(tab.traceId!)}>Clear</button>}
        <span className="text-muted">Variable values are not available in a trace; use “Insert PRINT for @var” from the editor menu.</span>
      </div>
      {trace?.error && <div className="px-3 py-1 text-err text-[11px] border-b border-line">{trace.error}</div>}
      {!events.length ? (
        <Empty text={tab.traceId ? "Waiting for events…" : "Start a trace to capture statement-level timing (sp_statement_completed, module_end, errors) for this session."} />
      ) : (
        <div className="flex-1 overflow-auto q-mono text-[12px]">
          <table className="w-full">
            <thead className="sticky top-0 bg-panel-2 text-muted text-[11px]">
              <tr>
                {["#", "Event", "Procedure", "Line", "Duration (µs)", "Rows", "Timeline", "Statement"].map((h) => (
                  <th key={h} className="px-3 py-1.5 text-left font-semibold border-b border-line whitespace-nowrap">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {events.map((e, i) => (
                <tr
                  key={`${e.seq}-${i}`}
                  onClick={() => {
                    setSel(i);
                    highlight(tabs, tab, e.procName, e.line);
                  }}
                  className={`border-b border-line/50 cursor-default ${sel === i ? "bg-accent/15 q-hl" : "hover:bg-panel"} ${e.error ? "text-err" : ""}`}
                >
                  <td className="px-3 py-1 text-muted">{i + 1}</td>
                  <td className="px-3 py-1 whitespace-nowrap">{e.eventName}</td>
                  <td className="px-3 py-1 whitespace-nowrap">{e.procName ?? "(batch)"}</td>
                  <td className="px-3 py-1 text-accent">{e.line ?? ""}</td>
                  <td className={`px-3 py-1 text-right ${(e.durationUs ?? 0) > maxDur * 0.5 ? "text-warn font-bold" : ""}`}>{e.durationUs?.toLocaleString() ?? ""}</td>
                  <td className="px-3 py-1 text-right text-ok">{e.rowCount?.toLocaleString() ?? ""}</td>
                  <td className="px-3 py-1 w-40">
                    {e.durationUs != null && <div className="h-2 rounded bg-accent/70" style={{ width: `${Math.max(2, (e.durationUs / maxDur) * 100)}%` }} />}
                  </td>
                  <td className="px-3 py-1 text-muted truncate max-w-[480px]" title={e.statement ?? e.error ?? ""}>{e.error ?? e.statement ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

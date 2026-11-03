// History (B6): scrub to any retained tick and see sessions and blocking as they were; "Live" returns to now.
// In memory only; export writes JSON (query text, login and host only when ticked — §13 user data).
import { useEffect, useState } from "react";
import type { HistoryResult, MonitorSnapshot } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { toast } from "../store/ui";
import type { MonitorModel } from "./model";
import { bytes, escapeForDisplay, ms } from "./format";
import { saveExport } from "./exportFile";

export function HistoryPane({ model }: { model: MonitorModel }) {
  const [hist, setHist] = useState<HistoryResult | null>(null);
  const [index, setIndex] = useState<number | null>(null);
  const [snap, setSnap] = useState<MonitorSnapshot | null>(null);
  const [includeText, setIncludeText] = useState(false);
  const refresh = () => rpc("monitor.history", { monitorId: model.monitorId }).then(setHist, () => {});
  useEffect(() => {
    void refresh();
    const t = setInterval(() => { if (index == null) void refresh(); }, 5_000);
    return () => clearInterval(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [model.monitorId, index]);
  useEffect(() => {
    if (index == null || !hist?.ticks[index]) { setSnap(null); return; }
    rpc("monitor.history", { monitorId: model.monitorId, seq: hist.ticks[index].seq }).then((r) => setSnap(r.snapshot ?? null), () => setSnap(null));
  }, [index, hist, model.monitorId]);
  const ticks = hist?.ticks ?? [];
  const exportJson = async () => {
    try {
      const f = await rpc("monitor.export", { monitorId: model.monitorId, includeText });
      const saved = await saveExport(f.path, f.fileName, { name: "Monitor history", extension: "json", mime: "application/json" });
      if (saved) toast("success", `Saved ${saved}`);
    } catch (e) {
      toast("error", String((e as Error)?.message ?? e));
    }
  };
  const tick = index != null ? ticks[index] : undefined;
  return (
    <div className="flex-1 min-h-0 flex flex-col text-[11.5px]">
      <div className="px-3 py-2 border-b border-line flex items-center gap-3 flex-wrap">
        <span className="text-muted">History: {ticks.length} ticks{hist ? `, ${bytes(hist.bytes)}` : ""}{hist?.capHit ? " (memory cap reached: oldest dropped)" : ""}</span>
        <select className="q-input h-[22px] py-0" value={hist?.retentionMinutes ?? 10}
          onChange={(e) => void rpc("monitor.historySettings", { monitorId: model.monitorId, retentionMinutes: Number(e.target.value) }).then(refresh)}>
          {[1, 5, 10, 15, 30, 60].map((m) => <option key={m} value={m}>keep {m} min</option>)}
        </select>
        <button className={`q-btn ${index == null ? "q-btn-primary" : ""}`} onClick={() => setIndex(null)}>Live</button>
        <span className="flex-1" />
        <label className="flex items-center gap-1"><input type="checkbox" checked={includeText} onChange={(e) => setIncludeText(e.target.checked)} /> include query text, logins and hosts</label>
        <button className="q-btn" disabled={ticks.length === 0} onClick={() => void exportJson()}>Export…</button>
      </div>
      <div className="px-3 py-2 border-b border-line">
        <input type="range" className="w-full" min={0} max={Math.max(0, ticks.length - 1)} value={index ?? ticks.length - 1} disabled={ticks.length === 0}
          onChange={(e) => setIndex(Number(e.target.value))} aria-label="History position" data-testid="history-scrubber" />
        <div className="flex justify-between text-muted">
          <span>{ticks[0] ? new Date(ticks[0].at).toLocaleTimeString() : ""}</span>
          <span>{tick ? `${new Date(tick.at).toLocaleTimeString()} · ${tick.sessions} sessions · ${tick.blocked} blocked${tick.gapBefore ? " · after a reconnect gap (no data before)" : ""}` : "live"}</span>
          <span>{ticks.at(-1) ? new Date(ticks.at(-1)!.at).toLocaleTimeString() : ""}</span>
        </div>
      </div>
      {index == null ? (
        <div className="p-3 text-muted">Pick a moment on the timeline to see the sessions and blocking as they were.</div>
      ) : !snap ? (
        <div className="p-3 text-muted">Loading…</div>
      ) : (
        <div className="flex-1 min-h-0 overflow-auto" data-testid="history-snapshot">
          {snap.blocking && snap.blocking.headBlockers.length > 0 && (
            <div className="px-3 py-1.5 border-b border-line text-err">Head blockers: {snap.blocking.headBlockers.join(", ")} · {snap.blocking.blockedCount} blocked</div>
          )}
          <table className="w-full q-mono">
            <thead className="text-muted text-left whitespace-nowrap"><tr><th className="px-2">Session</th><th className="pr-3">Login</th><th className="pr-3">Status</th><th className="pr-3">Wait</th><th className="pr-3">Blocked by</th><th className="pr-3">Elapsed</th><th>Text</th></tr></thead>
            <tbody>
              {(snap.sessions ?? []).filter((r) => r.isUserSession || r.blockedBy.length).sort((a, b) => a.key.id - b.key.id).map((r) => (
                <tr key={`${r.key.id}|${r.key.instance}`} className="border-t border-line/40 whitespace-nowrap">
                  <td className="px-2">{r.key.id}</td>
                  <td className="pr-3">{escapeForDisplay(r.login ?? "—")}</td>
                  <td className="pr-3">{r.status}</td>
                  <td className="pr-3">{r.waitType ?? "—"}</td>
                  <td className={`pr-3 ${r.blockedBy.length ? "text-err" : ""}`}>{r.blockedBy.join(", ") || "—"}</td>
                  <td className="pr-3">{ms(r.elapsedMs)}</td>
                  <td className="truncate max-w-[480px]">{escapeForDisplay((r.textId && snap.texts?.[r.textId]) || "—")}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

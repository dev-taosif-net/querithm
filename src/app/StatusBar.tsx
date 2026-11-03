import { useEffect, useState, useSyncExternalStore } from "react";
import { useActiveTab } from "../store/tabs";
import { envColor, envLabel, useProfiles } from "../store/profiles";
import { EngineLogo } from "../connections/EngineLogo";
import { useExecutions } from "../store/executions";
import { useConnections } from "../store/connections";
import { repickFile } from "../connections/fileAccess";
import { executionData } from "../results/model";
import { fmtMs } from "../results/MessagesPanel";
import { useEngineStatus } from "./engineStatus";

function useNow(active: boolean) {
  const [, set] = useState(0);
  useEffect(() => {
    if (!active) return;
    const t = setInterval(() => set((x) => x + 1), 1000);
    return () => clearInterval(t);
  }, [active]);
  return Date.now();
}

const EMPTY = { subscribe: () => () => {}, getVersion: () => 0 };

/**
 * server · database · TX state · execution timer · row count · sidecar health (§12). The quiet half of "loud in one
 * place": the top bar carries the environment badge, the database picker and the transaction clock; here the
 * environment is the 4 px edge and plain text.
 */
export function StatusBar() {
  const tab = useActiveTab();
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === tab?.profileId));
  const groups = useProfiles((s) => s.groups);
  const disconnected = useConnections((s) => !!tab?.profileId && !!s.disconnected[tab.profileId]);
  // The tab's database file needs picking again (its grant was revoked, or the file moved or was replaced).
  const needsFile = useConnections((s) => !!tab?.profileId && !!s.servers[tab.profileId]?.repick);
  const view = useExecutions((s) => (tab?.activeExecutionId ? s.views[tab.activeExecutionId] : undefined));
  const data = tab?.activeExecutionId ? executionData.get(tab.activeExecutionId) : undefined;
  useSyncExternalStore((data ?? EMPTY).subscribe, (data ?? EMPTY).getVersion);
  const running = !!view && ["queued", "running", "streaming"].includes(view.state);
  const now = useNow(running);
  const engine = useEngineStatus();
  const color = envColor(profile, groups);
  const elapsed = running ? now - view!.startedAt : view?.elapsedMs;
  const rows = data?.totalRows ?? 0;

  return (
    <div className="h-[24px] bg-panel border-t border-line px-3 flex items-center justify-between text-[11px] shrink-0 gap-3" style={profile ? { borderTop: `4px solid ${color}` } : undefined}>
      <div className="flex items-center gap-3 min-w-0">
        {profile ? (
          <span className="font-semibold flex items-center gap-1 text-fg whitespace-nowrap">
            <EngineLogo engine={profile.engine} size={12} /> {envLabel[profile.environment].toUpperCase()} - {profile.name}
            {tab?.spid ? ` (SPID ${tab.spid})` : ""}
          </span>
        ) : (
          <span className="text-muted">No connection</span>
        )}
        {tab?.profileId && (
          <span className="text-fg whitespace-nowrap">
            DB: {tab.database ?? "(default)"}{disconnected ? " (disconnected)" : ""}
          </span>
        )}
        {tab?.txState === "open" && <span className="text-warn font-semibold whitespace-nowrap">TX · connection pinned</span>}
        {tab?.txState === "unknown" && <span className="text-warn">TX state unknown</span>}
        {tab?.traceId && <span className="text-ok">● Trace</span>}
        {tab?.memoryLost && (
          <span className="text-warn truncate" data-q-memory-lost title="The tab's in-memory database was lost; the next run starts a new, empty one.">
            In-memory database lost
          </span>
        )}
        {tab?.sessionError && <span className="text-err truncate">{tab.sessionError}</span>}
        {tab?.sessionError && needsFile && (
          <button type="button" className="q-btn h-[18px] px-1.5 text-[10.5px] shrink-0" onClick={() => void repickFile(tab.profileId!)}>Re-pick file…</button>
        )}
      </div>
      <div className="flex items-center gap-4 text-muted whitespace-nowrap">
        {view && (
          <>
            <span className="text-fg font-medium">
              {view.state === "queued" ? "Queued" : running ? "Executing" : view.state === "failed" ? "Error" : view.state === "cancelled" ? "Cancelled" : "Done"}
              {rows > 0 ? ` · ${rows.toLocaleString()} rows${data && data.resultSets.length > 1 ? ` (${data.resultSets.length} sets)` : ""}` : ""}
              {view.affectedRows != null && rows === 0 ? ` · ${view.affectedRows.toLocaleString()} affected` : ""}
            </span>
            <span>
              Client: <b className="text-fg">{fmtMs(elapsed)}</b>
              {view.serverElapsedMs != null && (
                <>
                  {" "}│ Server: <b className="text-ok">{fmtMs(view.serverElapsedMs)}</b>
                </>
              )}
            </span>
          </>
        )}
        <span className={`font-medium flex items-center gap-1 ${engine.state === "running" || engine.state === "restarted" ? "text-ok" : engine.state === "dead" ? "text-warn" : "text-err"}`}>
          ● {engine.state === "running" || engine.state === "restarted" ? "Engine ready" : engine.state === "dead" ? "Engine restarting…" : "Engine stopped"}
        </span>
      </div>
    </div>
  );
}

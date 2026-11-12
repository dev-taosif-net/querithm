// Overview (B3): 4–6 sparklines of the last 5 minutes, drawn on canvas (no chart library). A metric the server does not
// report shows "—" with the reason, never 0 (plan step 7).
import { useEffect, useRef } from "react";
import type { Engine, MetricValue } from "../ipc/contract";
import type { MonitorModel } from "./model";
import { bytes, DASH } from "./format";
import { useTopics } from "./useModel";

const LABELS: Record<string, string> = {
  cpu: "CPU", batchRequests: "Batch req/s", compilations: "Compilations/s", userConnections: "Connections", ple: "Page life",
  tps: "TPS", queries: "Queries/s", cacheHit: "Cache hit", activeConnections: "Active", idleInTx: "Idle in tx", tempBytes: "Temp bytes/s", deadlocks: "Deadlocks", checkpoints: "Checkpoints/min",
};

/** Overview metrics per engine; none where the engine has no Activity monitor (SQLite). */
const ORDER: Record<Engine, string[]> = {
  mssql: ["cpu", "batchRequests", "compilations", "userConnections", "ple"],
  pg: ["tps", "cacheHit", "activeConnections", "idleInTx", "tempBytes", "deadlocks"],
  sqlite: [],
  mySql: ["queries", "cacheHit", "activeConnections", "idleInTx", "userConnections", "deadlocks"],
  duckDb: [],
  // D4 S12: V$SYSSTAT rates (execute count, commits + rollbacks, logical vs physical reads), V$SESSION counts, V$SYSMETRIC CPU.
  oracle: ["cpu", "queries", "tps", "cacheHit", "activeConnections", "idleInTx"],
};

export function formatMetric(v: MetricValue | undefined): string {
  if (!v || v.value == null) return DASH;
  switch (v.unit) {
    case "percent": return `${v.value.toFixed(v.value < 10 ? 1 : 0)}%`;
    case "perSec": return `${v.value.toFixed(v.value < 10 ? 1 : 0)}/s`;
    case "perMin": return `${v.value.toFixed(1)}/min`;
    case "bytesPerSec": return `${bytes(v.value)}/s`;
    case "seconds": return `${Math.round(v.value).toLocaleString("en-US")} s`;
    default: return Math.round(v.value).toLocaleString("en-US");
  }
}

function Spark({ points, color }: { points: (number | null)[]; color: string }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const c = ref.current;
    const ctx = c?.getContext?.("2d");
    if (!c || !ctx) return;
    const w = (c.width = c.clientWidth * devicePixelRatio || 120);
    const h = (c.height = c.clientHeight * devicePixelRatio || 28);
    ctx.clearRect(0, 0, w, h);
    const vals = points.filter((p): p is number => p != null);
    if (vals.length < 2) return;
    const max = Math.max(...vals, 1e-9), min = Math.min(...vals, 0);
    ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue(color).trim() || "#3b82f6";
    ctx.lineWidth = 1.5 * devicePixelRatio;
    ctx.beginPath();
    let started = false;
    points.forEach((p, i) => {
      if (p == null) { started = false; return; } // a gap stays a gap: never interpolated
      const x = (i / Math.max(1, points.length - 1)) * w;
      const y = h - ((p - min) / (max - min || 1)) * (h - 2) - 1;
      if (!started) { ctx.moveTo(x, y); started = true; } else ctx.lineTo(x, y);
    });
    ctx.stroke();
  }, [points, color]);
  return <canvas ref={ref} className="w-full h-[28px] block" />;
}

export function OverviewStrip({ model, engine }: { model: MonitorModel; engine: Engine }) {
  useTopics(model, ["metrics", "status", "connection"]); // connection: paused
  const paused = model.paused;
  const status = model.status.get("metrics");
  if (status?.state === "degraded") return <div className="px-3 py-1.5 border-b border-line text-[11px] text-muted">Overview not available: {status.reason}</div>;
  const latest = model.metrics.at(-1);
  return (
    // Paused: values and lines fall back to the muted tier, like the panes (the header says Paused).
    <div className="grid border-b border-line" style={{ gridTemplateColumns: `repeat(${ORDER[engine].length}, minmax(0, 1fr))` }} data-testid="overview" data-paused={paused || undefined}>
      {ORDER[engine].map((key) => {
        const v = latest?.values.find((x) => x.key === key);
        const series = model.metrics.map((s) => s.values.find((x) => x.key === key)?.value ?? null);
        return (
          <div key={key} className="px-3 py-1.5 border-r border-line last:border-r-0 min-w-0" title={v?.note ?? undefined}>
            <div className="flex items-baseline justify-between gap-2">
              <span className="text-[10px] uppercase tracking-wider text-muted truncate">{LABELS[key] ?? key}</span>
              <span className={`text-[13px] font-semibold q-mono ${paused ? "text-muted" : "text-fg"}`}>{formatMetric(v)}</span>
            </div>
            <Spark points={series} color={paused ? "--q-muted" : "--q-accent"} />
            <div className="text-[9.5px] text-muted truncate h-3">{v?.value == null ? (v?.note ?? "") : v.note ?? ""}</div>
          </div>
        );
      })}
    </div>
  );
}

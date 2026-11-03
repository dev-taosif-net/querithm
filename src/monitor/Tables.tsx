// Locks (B2), waits (B3), I/O (B3) and top queries (B4): sortable, virtualised tables. Missing values are "—".
import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { Engine, IoDelta, LockRow, TopQueryRow, WaitDelta, WaitInfo } from "../ipc/contract";
import { rpc } from "../ipc/client";
import type { MonitorModel } from "./model";
import { bytes, DASH, escapeForDisplay, ms, num, text } from "./format";
import { useTopics } from "./useModel";
import { PaneNotice, PaneStateBody, paneView } from "./PaneStateView";

const ROW = 22;

export interface TableColumn<T> {
  id: string;
  title: string;
  width: number;
  value: (r: T) => number | string | null | undefined;
  render?: (r: T) => ReactNode;
  align?: "right";
}

export function VirtualTable<T>({ rows, columns, rowKey, initialSort, onRowClick, testId }: {
  rows: T[]; columns: TableColumn<T>[]; rowKey: (r: T, i: number) => string; initialSort?: { id: string; desc: boolean }; onRowClick?: (r: T) => void; testId?: string;
}) {
  const [sort, setSort] = useState(initialSort ?? null);
  const [scroll, setScroll] = useState(0);
  const [height, setHeight] = useState(400);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!box.current || typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(() => setHeight(box.current?.clientHeight ?? 400));
    ro.observe(box.current);
    return () => ro.disconnect();
  }, []);
  const sorted = useMemo(() => {
    const col = sort ? columns.find((c) => c.id === sort.id) : undefined;
    if (!col) return rows;
    const dir = sort!.desc ? -1 : 1;
    return rows.map((r, i) => ({ r, i })).sort((a, b) => {
      const va = col.value(a.r), vb = col.value(b.r);
      if (va == null && vb == null) return a.i - b.i;
      if (va == null) return 1;
      if (vb == null) return -1;
      const c = typeof va === "number" && typeof vb === "number" ? va - vb : String(va).localeCompare(String(vb));
      return c !== 0 ? c * dir : a.i - b.i;
    }).map((x) => x.r);
  }, [rows, columns, sort]);
  const first = Math.max(0, Math.floor(scroll / ROW) - 5);
  const last = Math.min(sorted.length, Math.ceil((scroll + height) / ROW) + 5);
  const total = columns.reduce((s, c) => s + c.width, 0);
  return (
    <div ref={box} className="flex-1 min-h-0 overflow-auto text-[11.5px]" onScroll={(e) => setScroll((e.target as HTMLDivElement).scrollTop)} data-testid={testId}>
      <div style={{ minWidth: total }}>
        <div className="flex sticky top-0 z-10 bg-panel border-b border-line font-semibold text-muted" style={{ height: ROW }}>
          {columns.map((c) => (
            <button key={c.id} className={`px-2 text-left truncate hover:text-fg ${c.align === "right" ? "text-right" : ""}`} style={{ width: c.width }}
              onClick={() => setSort((s) => (s?.id === c.id ? (s.desc ? null : { id: c.id, desc: true }) : { id: c.id, desc: false }))}>
              {c.title}{sort?.id === c.id ? (sort.desc ? " ▼" : " ▲") : ""}
            </button>
          ))}
        </div>
        <div style={{ height: sorted.length * ROW, position: "relative" }}>
          {sorted.slice(first, last).map((r, i) => (
            <div key={rowKey(r, first + i)} className={`flex absolute left-0 right-0 border-b border-line/40 hover:bg-hover ${onRowClick ? "cursor-pointer" : ""}`}
              style={{ top: (first + i) * ROW, height: ROW }} onClick={() => onRowClick?.(r)}>
              {columns.map((c) => (
                <div key={c.id} className={`px-2 truncate leading-[22px] q-mono ${c.align === "right" ? "text-right" : ""}`} style={{ width: c.width }}>
                  {c.render ? c.render(r) : textCell(c.value(r))}
                </div>
              ))}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

const textCell = (v: number | string | null | undefined) => (v == null || v === "" ? DASH : typeof v === "number" ? num(v) : escapeForDisplay(v));

function PaneFrame({ model, pane, has, empty, children }: { model: MonitorModel; pane: "locks" | "waits" | "io" | "topQueries"; has: boolean; empty: string; children: ReactNode }) {
  const view = paneView(model, pane, has, empty);
  return (
    // Paused: plain values fall back to the muted tier (still 4.5:1, unlike an opacity) while status colors stay.
    <div className={`flex-1 min-h-0 flex flex-col ${model.paused ? "text-muted" : ""}`} data-paused={model.paused || undefined}>
      <PaneNotice model={model} pane={pane} />
      {view.kind === "content" ? children : <PaneStateBody view={view} />}
    </div>
  );
}

// ---- locks ----

export function LocksPane({ model }: { model: MonitorModel }) {
  useTopics(model, ["locks", "status", "connection"]);
  const rows = model.locks ?? [];
  const cols: TableColumn<LockRow>[] = [
    { id: "spid", title: "Session", width: 70, value: (l) => l.key.id },
    { id: "type", title: "Resource", width: 110, value: (l) => l.resourceType },
    { id: "db", title: "Database", width: 120, value: (l) => l.database },
    { id: "obj", title: "Object", width: 260, value: (l) => l.object, render: (l) => <>{text(l.object && escapeForDisplay(l.object))}{l.objectNote && <span className="text-muted"> ({l.objectNote})</span>}</> },
    { id: "mode", title: "Mode", width: 90, value: (l) => l.mode },
    { id: "status", title: "Status", width: 80, value: (l) => l.status, render: (l) => <span className={l.status === "waiting" ? "text-err" : ""}>{l.status}</span> },
    { id: "count", title: "Count", width: 70, value: (l) => l.count, align: "right" },
  ];
  return (
    <PaneFrame model={model} pane="locks" has={model.locks != null && rows.length > 0} empty={model.locks ? "No locks besides database locks" : ""}>
      <VirtualTable rows={rows} columns={cols} rowKey={(l, i) => `${l.key.id}|${l.resourceType}|${l.object}|${l.mode}|${l.status}|${i}`} initialSort={{ id: "status", desc: true }} testId="locks-table" />
    </PaneFrame>
  );
}

// ---- waits (with dictionary hover) ----

const waitInfoCache = new Map<string, Promise<WaitInfo>>();
export function waitInfo(engine: Engine, waitType: string): Promise<WaitInfo> {
  const k = `${engine}|${waitType}`;
  let p = waitInfoCache.get(k);
  if (!p) waitInfoCache.set(k, (p = rpc("monitor.waitInfo", { engine, waitType }).catch(() => ({ waitType, known: false, meaning: "no description yet" }) as WaitInfo)));
  return p;
}

export function WaitName({ engine, waitType }: { engine: Engine; waitType: string }) {
  const [info, setInfo] = useState<WaitInfo | null>(null);
  const [open, setOpen] = useState(false);
  return (
    <span className="relative" onMouseEnter={() => { setOpen(true); void waitInfo(engine, waitType).then(setInfo); }} onMouseLeave={() => setOpen(false)}>
      <span className="underline decoration-dotted">{waitType}</span>
      {open && info && (
        <span className="absolute left-0 top-5 z-30 w-[360px] whitespace-normal bg-raised border border-line rounded p-2 shadow-xl font-sans text-[11px] text-fg" data-testid="wait-card">
          <span className="block font-semibold">{info.waitType}{info.category ? ` · ${info.category}` : ""}</span>
          <span className="block text-muted mt-0.5">{info.meaning}</span>
          {info.causes?.length ? <span className="block mt-1"><b>Common causes:</b> {info.causes.join("; ")}</span> : null}
          {info.check?.length ? <span className="block mt-1"><b>Check next:</b> {info.check.join("; ")}</span> : null}
        </span>
      )}
    </span>
  );
}

export function WaitsPane({ model, engine }: { model: MonitorModel; engine: Engine }) {
  useTopics(model, ["waits", "status", "connection"]);
  const rows = model.waits ?? [];
  const cov = model.coverage;
  const cols: TableColumn<WaitDelta>[] = [
    { id: "cat", title: "Category", width: 110, value: (w) => w.category },
    { id: "type", title: "Wait", width: 260, value: (w) => w.waitType, render: (w) => <WaitName engine={engine} waitType={w.waitType} /> },
    { id: "ms", title: cov?.source === "tick-sampled" ? "Wait ms (estimate)" : "Wait ms", width: 130, value: (w) => w.waitMsDelta, align: "right" },
    { id: "tasks", title: cov ? "Samples" : "Tasks", width: 90, value: (w) => w.tasksDelta, align: "right" },
    { id: "sig", title: "Signal ms", width: 90, value: (w) => w.signalMsDelta, align: "right" },
  ];
  return (
    <PaneFrame model={model} pane="waits" has={rows.length > 0} empty="No waits in the last interval">
      {cov && (
        <div className="px-3 py-1 text-[11px] border-b border-line bg-panel-2" data-testid="wait-coverage">
          {cov.source === "tick-sampled"
            ? <span className="text-warn">Tick-sampled, low resolution: one sample per {ms(cov.periodMs)}; waits shorter than that are missed. Not exact.{cov.partial ? " Partial: the session cap truncated active sessions." : ""}</span>
            : <span className="text-muted">In-server profile (pg_wait_sampling, {cov.periodMs} ms sampling): sampled, not exact.</span>}
        </div>
      )}
      <VirtualTable rows={rows} columns={cols} rowKey={(w) => w.waitType} initialSort={{ id: "ms", desc: true }} testId="waits-table" />
    </PaneFrame>
  );
}

// ---- I/O ----

export function IoPane({ model }: { model: MonitorModel }) {
  useTopics(model, ["io", "status", "connection"]);
  const rows = model.io ?? [];
  const cluster = rows[0]?.scope === "cluster";
  const cols: TableColumn<IoDelta>[] = cluster
    ? [
        { id: "bt", title: "Backend type", width: 160, value: (r) => r.backendType },
        { id: "obj", title: "Object", width: 110, value: (r) => r.object },
        { id: "ctx", title: "Context", width: 100, value: (r) => r.context },
      ]
    : [
        { id: "db", title: "Database", width: 140, value: (r) => r.database },
        { id: "file", title: "File", width: 180, value: (r) => r.file },
      ];
  cols.push(
    { id: "rb", title: "Read", width: 100, value: (r) => r.readBytes, render: (r) => bytes(r.readBytes), align: "right" },
    { id: "wb", title: "Written", width: 100, value: (r) => r.writeBytes, render: (r) => bytes(r.writeBytes), align: "right" },
    { id: "rs", title: "Read stall", width: 100, value: (r) => r.readStallMs, render: (r) => (r.readStallMs == null ? DASH : ms(r.readStallMs)), align: "right" },
    { id: "ws", title: "Write stall", width: 100, value: (r) => r.writeStallMs, render: (r) => (r.writeStallMs == null ? DASH : ms(r.writeStallMs)), align: "right" },
  );
  return (
    <PaneFrame model={model} pane="io" has={rows.length > 0} empty="No I/O in the last interval">
      {cluster && <div className="px-3 py-1 text-[11px] border-b border-line bg-panel-2 text-muted">Cluster-wide (pg_stat_io): no database or file; the database filter does not apply.</div>}
      <VirtualTable rows={rows} columns={cols} rowKey={(r) => `${r.scope}|${r.database}|${r.file}|${r.backendType}|${r.object}|${r.context}`} initialSort={{ id: "rb", desc: true }} testId="io-table" />
    </PaneFrame>
  );
}

// ---- top queries ----

/**
 * Where top queries come from: pg_stat_statements (shared blocks and I/O time, no CPU time) on PostgreSQL; SQL Server's
 * statistics report CPU and logical reads; MySQL's statement digests report rows examined (in the logical-reads field) and
 * no CPU time.
 */
type TopQuerySource = "statistics" | "pgStatStatements" | "digests";
const TOP_QUERY_SOURCE: Record<Engine, TopQuerySource> = {
  mssql: "statistics", pg: "pgStatStatements", sqlite: "statistics", mySql: "digests", duckDb: "statistics",
};

export function TopQueriesPane({ model, engine, onOpenText }: { model: MonitorModel; engine: Engine; onOpenText: (text: string) => void }) {
  useTopics(model, ["top", "texts", "status", "connection"]);
  const source = TOP_QUERY_SOURCE[engine];
  const pg = source === "pgStatStatements";
  const cpu = source === "statistics";
  const [order, setOrder] = useState(cpu ? "cpu" : "duration");
  const [since, setSince] = useState(false);
  useEffect(() => {
    void rpc("monitor.setTopQueries", { monitorId: model.monitorId, orderBy: order, sinceStart: since }).catch(() => {});
  }, [model.monitorId, order, since]);
  const rows = model.top ?? [];
  const cols: TableColumn<TopQueryRow>[] = [
    { id: "text", title: "Query", width: 420, value: (q) => (q.textId ? model.texts.get(q.textId) : null) ?? null, render: (q) => {
      const t = q.textId ? model.texts.get(q.textId) : undefined;
      return t ? escapeForDisplay(t) : q.textId && model.texts.isMissing(q.textId) ? <span className="text-muted">text no longer available</span> : DASH;
    } },
    { id: "exec", title: "Executions", width: 90, value: (q) => q.execDelta, align: "right" },
    ...(!cpu ? [] : [{ id: "cpu", title: "CPU", width: 90, value: (q: TopQueryRow) => q.cpuMsDelta, render: (q: TopQueryRow) => (q.cpuMsDelta == null ? DASH : ms(q.cpuMsDelta)), align: "right" as const }]),
    { id: "dur", title: "Duration", width: 100, value: (q) => q.durationMsDelta, render: (q) => ms(q.durationMsDelta), align: "right" },
    pg ? { id: "blks", title: "Shared blocks", width: 110, value: (q) => q.sharedBlocksDelta, align: "right" } : { id: "reads", title: source === "digests" ? "Rows examined" : "Logical reads", width: 110, value: (q) => q.logicalReadsDelta, align: "right" },
    { id: "rows", title: "Rows", width: 90, value: (q) => q.rowsDelta, align: "right" },
    ...(pg ? [{ id: "io", title: "I/O time", width: 90, value: (q: TopQueryRow) => q.ioTimeMsDelta, render: (q: TopQueryRow) => (q.ioTimeMsDelta == null ? DASH : ms(q.ioTimeMsDelta)), align: "right" as const }] : []),
    { id: "db", title: "Database", width: 110, value: (q) => q.database },
    { id: "hash", title: pg ? "Query id" : source === "digests" ? "Digest" : "Query hash", width: 160, value: (q) => q.queryHash },
  ];
  return (
    <PaneFrame model={model} pane="topQueries" has={rows.length > 0} empty={since ? "No statements since the monitor started" : "No statements completed in the last interval"}>
      <div className="px-3 py-1 flex items-center gap-2 text-[11px] border-b border-line">
        <span className="text-muted">Top 100 by</span>
        <select className="q-input h-[22px] py-0" value={order} onChange={(e) => setOrder(e.target.value)}>
          {cpu && <option value="cpu">CPU</option>}
          <option value="duration">duration</option>
          <option value="reads">{pg ? "shared blocks" : source === "digests" ? "rows examined" : "logical reads"}</option>
          <option value="executions">executions</option>
        </select>
        <label className="flex items-center gap-1"><input type="checkbox" checked={since} onChange={(e) => setSince(e.target.checked)} /> since the monitor started</label>
        {pg && <span className="text-muted">pg_stat_statements reports no CPU time.</span>}
        {source === "digests" && <span className="text-muted">Statement digests report no CPU time.</span>}
      </div>
      <VirtualTable rows={rows} columns={cols} rowKey={(q) => `${q.queryHash}|${q.database}`} onRowClick={(q) => { const t = q.textId ? model.texts.get(q.textId) : undefined; if (t) onOpenText(t); }} testId="top-table" />
    </PaneFrame>
  );
}

// Data compare tab: two tables or queries merged on a key while both stream; the differences fill a grid
// as they are found (changed cells lit), and the sync script opens in a query tab bound to the target.
import { lazy, memo, Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { DataCompareProgressEvent, DataCompareStart } from "../ipc/contract";
import { onNotify, rpc } from "../ipc/client";
import type { ToolTab } from "../store/toolTabs";
import { useToolTabs } from "../store/toolTabs";
import { useProfiles } from "../store/profiles";
import { capabilityRefusal, useConnections } from "../store/connections";
import { ownSessionFor, sessionParam, toolSession, useToolSnapshot } from "../store/snapshots";
import { toast } from "../store/ui";
import { newQueryTab } from "../app/actions";
import { useExecutionStream } from "../results/useExecutionStream";
import { useExecutions } from "../store/executions";
import { count, dataSideReady, DIFF_FILTERS, diffTone, parseKey, sideToParams, type DataCompareTabParams, type DataSideState, type DiffFilter } from "./dataCompare";

const ResultGrid = lazy(() => import("../results/ResultGrid").then((m) => ({ default: m.ResultGrid })));

export const DataCompareTab = memo(function DataCompareTab({ tool }: { tool: ToolTab }) {
  const params = tool.params as unknown as DataCompareTabParams;
  const [source, setSource] = useState<DataSideState>(params.source ?? { mode: "table" });
  const [target, setTarget] = useState<DataSideState>(params.target ?? { mode: "table" });
  const [keyText, setKeyText] = useState("");
  const [ignoreCase, setIgnoreCase] = useState(false);
  const [trimPadding, setTrimPadding] = useState(true);
  const [epsilon, setEpsilon] = useState("1e-9");
  const [timeMs, setTimeMs] = useState("0");
  const [maxDiffs, setMaxDiffs] = useState("50000");
  const [start, setStart] = useState<DataCompareStart | null>(null);
  const [progress, setProgress] = useState<DataCompareProgressEvent | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<DiffFilter>("all");
  const [sync, setSync] = useState({ insert: true, update: true, delete: false });
  const data = useExecutionStream(start?.executionId);
  const rs = data?.resultSets[0];
  // W6: the compare's run is a view under the tool's id, so an engine restart (or a stop) marks it failed like any run
  // still going — the banner names this tab, and the tab stops saying "Reading…" for a compare nothing will finish.
  const runState = useExecutions((s) => (start ? s.views[start.executionId]?.state : undefined));
  const interrupted = !!start && !progress?.done && runState === "failed";
  const running = !!start && !progress?.done && !interrupted;
  const profiles = useProfiles((s) => s.profiles);
  const name = (id?: string) => profiles.find((p) => p.id === id)?.name ?? "?";
  // SQLite and DuckDB have no data compare: the sidecar's reason, before anything runs (it refuses a call as well).
  const refusal = useConnections((s) => capabilityRefusal(s.servers, source.profileId, "dataCompare") ?? capabilityRefusal(s.servers, target.profileId, "dataCompare"));

  useEffect(() => {
    useToolTabs.getState().update(tool.id, { params: { source, target } as unknown as Record<string, unknown> });
  }, [source, target, tool.id]);

  useEffect(() => {
    const id = start?.executionId;
    if (!id) return;
    useExecutions.getState().put({
      id, tabId: tool.id, sessionId: "", state: "running", startedAt: Date.now(), resultSetCount: 0, truncated: false, messages: [], statistics: [], plans: [], sql: "", lineOffset: 1,
    });
    return () => useExecutions.getState().drop(id);
  }, [start?.executionId, tool.id]);

  // The compare that finished (its last progress said done): nothing is left to cancel for it.
  const finished = useRef<string | undefined>(undefined);
  useEffect(
    () =>
      onNotify("dataCompare.progress", (e) => {
        if (e.compareId !== start?.compareId) return;
        setProgress(e);
        if (e.done) {
          finished.current = e.compareId;
          dataCompareBench.lastMs = e.elapsedMs;
          if (e.error) setError(e.error);
          useExecutions.getState().patch(start.executionId, { state: e.cancelled ? "cancelled" : e.error ? "failed" : "done" });
        }
      }),
    [start?.compareId, start?.executionId],
  );

  // Closing the tab, or starting another compare, ends one still running: its two server reads and its diff stream
  // stop (the stream's query.close stops it as well, H3).
  useEffect(() => {
    const compareId = start?.compareId;
    if (!compareId) return;
    return () => {
      if (finished.current !== compareId) void rpc("dataCompare.cancel", { compareId }).catch(() => {});
    };
  }, [start?.compareId]);

  useEffect(() => {
    if (!rs) return;
    rs.setFilter(DIFF_FILTERS.find((f) => f.id === filter)!.text, filter === "all" ? null : [0]);
  }, [rs, filter, rs?.complete]);

  const run = async () => {
    setError(null);
    setProgress(null);
    setStart(null);
    try {
      const [s, t] = await Promise.all([toolSession(source), toolSession(target)]);
      const r = await rpc("dataCompare.start", {
        source: sideToParams(source, s), target: sideToParams(target, t), key: parseKey(keyText), columns: null,
        options: {
          floatEpsilon: Number(epsilon) || 0, timeToleranceMs: Math.max(0, Math.round(Number(timeMs) || 0)), ignoreCase, trimCharPadding: trimPadding,
          maxDiffRows: Math.max(1, Math.round(Number(maxDiffs) || 50_000)),
        },
      });
      setStart(r);
      const label = (x: DataSideState) => (x.mode === "table" ? x.table : "query");
      useToolTabs.getState().update(tool.id, { title: `Data · ${label(source)} → ${label(target)}`, profileId: target.profileId, database: target.database });
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const cancel = () => start && void rpc("dataCompare.cancel", { compareId: start.compareId }).catch(() => {});

  const openSync = async () => {
    if (!start) return;
    try {
      const r = await rpc("dataCompare.syncSql", { compareId: start.compareId, ...sync });
      newQueryTab({ profileId: target.profileId, database: target.database, text: r.sql, title: "Sync.sql" });
      toast("info", `${r.statements} statement(s) for ${count(r.rows)} row(s) in a new tab on the target. Review it, then run it.${r.notes.length ? ` ${r.notes.join(" ")}` : ""}`);
    } catch (e) {
      toast("error", (e as Error).message);
    }
  };

  // Field indices and per-column metadata of the diff schema, read once per result set.
  const layout = useMemo(() => {
    if (!rs?.schema) return null;
    const fields = rs.schema.fields;
    const kindField = fields.findIndex((f) => f.name === "_diff_kind");
    const maskField = fields.findIndex((f) => f.name === "_diff_mask");
    const meta = rs.columns.map((c) => {
      const m = fields[c.field].metadata;
      const bit = m.get("q:diffBit");
      return { bit: bit != null ? Number(bit) : null, side: (m.get("q:diffSide") as "source" | "target" | undefined) ?? null, isKind: c.name === "diff" };
    });
    return { kindField, maskField, meta };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rs, rs?.schema]);
  const tone = useCallback(
    (row: number, col: number) => {
      if (!rs || !layout) return null;
      const kind = Number(rs.raw(row, layout.kindField) ?? 0);
      return diffTone(kind, rs.raw(row, layout.maskField) as Uint8Array | null, layout.meta[col]);
    },
    [rs, layout],
  );

  const p = progress;
  return (
    <div className="flex-1 flex flex-col min-h-0 text-[11px]">
      <div className="px-3 py-1.5 border-b border-line bg-panel flex flex-col gap-1 shrink-0">
        <div className="flex items-start gap-2 flex-wrap">
          <DataSidePicker label="Source" side={source} onChange={setSource} />
          <button className="q-btn h-[22px]" title="Swap source and target" aria-label="Swap source and target" onClick={() => { setSource(target); setTarget(source); }}>⇄</button>
          <DataSidePicker label="Target" side={target} onChange={setTarget} />
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          <label className="flex items-center gap-1">Key <input className="q-input h-[22px] w-40" placeholder="primary key" value={keyText} onChange={(e) => setKeyText(e.target.value)} aria-label="Key columns" /></label>
          <label className="flex items-center gap-1 cursor-pointer"><input type="checkbox" checked={ignoreCase} onChange={(e) => setIgnoreCase(e.target.checked)} /> Ignore case</label>
          <label className="flex items-center gap-1 cursor-pointer" title="Trailing spaces in char/nchar values are padding">
            <input type="checkbox" checked={trimPadding} onChange={(e) => setTrimPadding(e.target.checked)} /> Ignore char padding
          </label>
          <label className="flex items-center gap-1" title="Relative difference under which floating-point values are equal">Float ε <input className="q-input h-[22px] w-16" value={epsilon} onChange={(e) => setEpsilon(e.target.value)} /></label>
          <label className="flex items-center gap-1" title="Date/time values this close are equal (datetime vs timestamp precision)">Time ± <input className="q-input h-[22px] w-12" value={timeMs} onChange={(e) => setTimeMs(e.target.value)} /> ms</label>
          <label className="flex items-center gap-1" title="Differences kept for the grid and the sync script; the rest are counted">Keep <input className="q-input h-[22px] w-16" value={maxDiffs} onChange={(e) => setMaxDiffs(e.target.value)} /></label>
          <div className="flex-1" />
          {running ? (
            <button className="q-btn h-[22px]" onClick={cancel}>Cancel</button>
          ) : (
            <button className="q-btn q-btn-primary h-[22px]" disabled={!!refusal || !dataSideReady(source) || !dataSideReady(target)} title={refusal} onClick={() => void run()}>
              {start ? "Compare again" : "Compare"}
            </button>
          )}
        </div>
      </div>
      {refusal && <div className="px-3 py-1.5 text-warn border-b border-line bg-panel" role="note">{refusal}</div>}
      {error && <div className="px-3 py-1.5 text-err border-b border-line bg-panel" role="alert">{error}</div>}
      {interrupted && !error && <div className="px-3 py-1.5 text-err border-b border-line bg-panel" role="alert">{COMPARE_INTERRUPTED}</div>}
      {!start ? (
        <div className="flex-1 flex items-center justify-center text-muted text-xs p-4 text-center">
          Pick a table or write a query on each side, then Compare. Rows are matched on the key (the primary key unless you name columns).
        </div>
      ) : (
        <>
          <div className="min-h-[30px] py-1 px-3 border-b border-line bg-panel flex flex-wrap items-center gap-x-2 gap-y-1 shrink-0 whitespace-nowrap">
            <div className="flex items-center gap-1" role="group" aria-label="Show">
              {DIFF_FILTERS.map((f) => (
                <button key={f.id} onClick={() => setFilter(f.id)}
                  className={`px-2 py-0.5 rounded ${filter === f.id ? "bg-accent/15 text-accent border border-accent/40 font-medium" : "bg-raised text-muted border border-transparent"}`}>
                  {f.label}{p && f.id !== "all" ? ` ${count(f.id === "different" ? p.different : f.id === "sourceOnly" ? p.sourceOnly : p.targetOnly)}` : ""}
                </button>
              ))}
            </div>
            <span className="text-muted" data-q-data-progress>
              {p ? `${p.done || interrupted ? "" : "Reading… "}${count(p.sourceRows)} / ${count(p.targetRows)} rows · ${count(p.identical)} identical${p.done ? ` · ${Math.round(p.elapsedMs)} ms` : ""}${p.cancelled ? " · cancelled" : ""}${interrupted ? " · interrupted" : ""}` : interrupted ? "Interrupted" : "Starting…"}
            </span>
            {p?.truncated && (
              // L21: the sidecar says which limit stopped keeping differences (Keep, or the bytes one comparison may hold).
              <span className="text-warn truncate max-w-[45%]" title={p.truncatedNote ?? "Raise Keep to see more"}>
                {p.truncatedNote ?? `showing the first ${count(start ? Number(maxDiffs) : 0)} differences`}
              </span>
            )}
            <div className="flex-1" />
            <span className="text-muted truncate max-w-[40%]" title={start.notes.join("\n")}>
              Key {start.key.map((k) => k.source).join(", ")} · {start.columns.length} columns{start.notes.length ? ` · ${start.notes[0]}` : ""}
            </span>
          </div>
          <div className="flex-1 min-h-0 relative">
            {rs ? (
              <Suspense fallback={null}><ResultGrid rs={rs} executionId={start.executionId} tone={tone} /></Suspense>
            ) : (
              <div className="h-full flex items-center justify-center text-muted">{interrupted ? "No rows arrived before the engine stopped." : "Waiting for the first rows…"}</div>
            )}
          </div>
          <div className="h-[32px] px-3 border-t border-line bg-panel flex items-center gap-3 shrink-0">
            <span className="text-muted">Sync the target:</span>
            <label className="flex items-center gap-1 cursor-pointer"><input type="checkbox" checked={sync.insert} onChange={(e) => setSync({ ...sync, insert: e.target.checked })} /> Insert source-only rows</label>
            <label className="flex items-center gap-1 cursor-pointer"><input type="checkbox" checked={sync.update} onChange={(e) => setSync({ ...sync, update: e.target.checked })} /> Update different rows</label>
            <label className={`flex items-center gap-1 cursor-pointer ${sync.delete ? "text-err" : ""}`}><input type="checkbox" checked={sync.delete} onChange={(e) => setSync({ ...sync, delete: e.target.checked })} /> Delete target-only rows</label>
            <div className="flex-1" />
            <button className="q-btn q-btn-primary h-[22px]" disabled={!p?.done || !!p.error || target.mode !== "table" || !(sync.insert || sync.update || sync.delete)}
              title={target.mode !== "table" ? "The target is a query: a sync script needs a table" : `Script for ${name(target.profileId)} · ${target.database}`}
              onClick={() => void openSync()}>
              Open sync script
            </button>
          </div>
        </>
      )}
    </div>
  );
});

function DataSidePicker({ label, side, onChange }: { label: string; side: DataSideState; onChange: (s: DataSideState) => void }) {
  const profiles = useProfiles((s) => s.profiles);
  const server = useConnections((s) => (side.profileId ? s.servers[side.profileId] : undefined));
  // A session-owned engine's side reads its tab session's view (Phase D0d); a server engine, the shared snapshot.
  const snap = useToolSnapshot(side).entry?.snapshot;
  const databases = (server?.databases ?? []).filter((d) => !d.isSystem).map((d) => d.name);
  // An in-memory database is listed nowhere (each tab has its own): the one this side was opened on stays selectable.
  if (server?.memory && side.database && !databases.includes(side.database)) databases.push(side.database);
  const tables = useMemo(
    () => (snap ? snap.objects.filter((o) => o.kind === "table" || o.kind === "view").map((o) => `${o.schema}.${o.name}`).sort((a, b) => a.localeCompare(b)) : []),
    [snap],
  );
  useEffect(() => {
    if (side.profileId && !server) void useConnections.getState().connect(side.profileId);
  }, [side.profileId, server]);
  const listId = `dc-tables-${label}`;
  return (
    <div className="flex flex-col gap-1" role="group" aria-label={label}>
      <div className="flex items-center gap-1">
        <span className="text-muted font-medium w-11">{label}</span>
        <select className="q-input h-[22px] py-0 max-w-[160px]" value={side.profileId ?? ""} aria-label={`${label} connection`}
          onChange={(e) => onChange({ mode: side.mode, profileId: e.target.value || undefined })}>
          <option value="">Connection…</option>
          {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        <select className="q-input h-[22px] py-0 max-w-[150px]" value={side.database ?? ""} disabled={!side.profileId} aria-label={`${label} database`}
          onChange={(e) => {
            const { sessionId: _, ...rest } = side;
            onChange({ ...rest, database: e.target.value || undefined, table: undefined, ...sessionParam(ownSessionFor(side.profileId, e.target.value)) });
          }}>
          <option value="">{server?.status === "connecting" ? "Connecting…" : "Database…"}</option>
          {databases.map((d) => <option key={d} value={d}>{d}</option>)}
        </select>
        <select className="q-input h-[22px] py-0" value={side.mode} aria-label={`${label} kind`} onChange={(e) => onChange({ ...side, mode: e.target.value as "table" | "query" })}>
          <option value="table">Table</option>
          <option value="query">Query</option>
        </select>
        {side.mode === "table" && (
          <>
            <input className="q-input h-[22px] w-48" list={listId} placeholder="schema.table" value={side.table ?? ""} disabled={!side.database} aria-label={`${label} table`}
              onChange={(e) => onChange({ ...side, table: e.target.value || undefined })} />
            <datalist id={listId}>{tables.map((t) => <option key={t} value={t} />)}</datalist>
          </>
        )}
      </div>
      {side.mode === "query" && (
        <textarea className="q-input font-mono text-[11px] w-[520px] h-[52px] resize-y" placeholder="SELECT … (one read-only SELECT)" value={side.query ?? ""}
          aria-label={`${label} query`} spellCheck={false} onChange={(e) => onChange({ ...side, query: e.target.value })} />
      )}
    </div>
  );
}

/** Shown in a compare whose run the engine took with it (a restart or a stop): nothing reports its end any more. */
export const COMPARE_INTERRUPTED = "The Querithm engine stopped while this compare ran, so it did not finish. Compare again.";

/** Bench hook (§14 metric 22): the last compare's duration. */
export const dataCompareBench = { lastMs: 0 };

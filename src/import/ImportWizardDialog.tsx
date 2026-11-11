// Import wizard: 1. pick and profile a file (whole file, preview from the data plane), 2. map it onto a new or
// existing table (DDL and per-column verdicts from the sidecar), 3. load it with the native bulk path and a reject log.
import { LoadingStatus, ProgressBar } from "../app/LoadingStatus";
import { lazy, Suspense, useEffect, useMemo, useRef, useState } from "react";
import type {
  Engine, ImportColumnVerdict, ImportFormat, ImportInspectResult, ImportMapping, ImportProgressEvent, ImportReadOptions, ImportRunOptions, ImportTargetResult,
} from "../ipc/contract";
import { onNotify, rpc, RpcError } from "../ipc/client";
import { useExecutionStream } from "../results/useExecutionStream";
import { Modal, Field } from "../app/Modal";
import { ServerChip } from "../app/EnvTag";
import { ask, toast, useUi, type Dialog } from "../store/ui";
import { useProfiles } from "../store/profiles";
import { engineTraits, knownEngine } from "../engines";
import { heldSession, loginSchemaOf, useSnapshots, useToolSnapshot } from "../store/snapshots";
import { closeImportSession, executeGuarded, openImportSession, pickImportFile, type PickedFile } from "./importActions";
import { forgetUncertainImport, markUncertainImport, uncertainImport } from "./uncertainImports";

const ResultGrid = lazy(() => import("../results/ResultGrid").then((m) => ({ default: m.ResultGrid })));

type Step = "file" | "target" | "load";

const KIND_LABEL: Record<string, string> = {
  empty: "all NULL", bool: "true/false", int32: "whole number", int64: "big whole number", decimal: "decimal", double: "number", date: "date",
  dateTime: "date + time", dateTimeOffset: "date + time + offset", time: "time", uuid: "GUID", text: "text",
};

const VERDICT_STYLE: Record<string, string> = {
  fits: "text-ok", widen: "text-warn", mismatch: "text-err", nullIntoNotNull: "text-err", missingInTarget: "text-muted", requiredUnmapped: "text-err",
};

function bytes(n: number) {
  return n >= 1 << 20 ? `${(n / (1 << 20)).toFixed(1)} MB` : n >= 1024 ? `${(n / 1024).toFixed(0)} KB` : `${n} B`;
}

/** The bulk loader takes trigger, constraint-check and table-lock options (SQL Server's SqlBulkCopy only). */
const BULK_COPY_OPTIONS: Record<Engine, boolean> = { mssql: true, pg: false, sqlite: false, mySql: false, duckDb: false, oracle: false };

export function ImportWizardDialog({ d }: { d: Extract<Dialog, { kind: "import" }> }) {
  const close = () => useUi.getState().closeDialog(d);
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === d.profileId));
  const engine = knownEngine(profile?.engine);
  // A server engine's shared snapshot; a session-owned engine's view of the query tab session it was opened from (D0d).
  const snap = useToolSnapshot(d, false).entry;
  const [step, setStep] = useState<Step>("file");
  const [file, setFile] = useState<PickedFile | null>(null);
  const [opts, setOpts] = useState<ImportReadOptions>({ format: "auto", delimiter: null, hasHeader: null, encoding: null, sheet: null, nullValues: null });
  const [ins, setIns] = useState<ImportInspectResult | null>(null);
  const [progress, setProgress] = useState<ImportProgressEvent | null>(null);
  const [busy, setBusy] = useState(false);
  const [sessionId, setSessionId] = useState<string>();
  const [newTable, setNewTable] = useState(!d.table);
  // Where the sign-in owns a schema (Oracle, D4 S10) it is the default target; where the database is the schema (MySQL),
  // the database itself.
  const [schema, setSchema] = useState(d.schema ?? engineTraits(engine).defaultSchema ?? loginSchemaOf(d.profileId) ?? d.database);
  const [table, setTable] = useState(d.table ?? "");
  const [mapping, setMapping] = useState<ImportMapping[] | null>(null);
  const [types, setTypes] = useState<Record<string, string>>({});
  const [pk, setPk] = useState<string[]>([]);
  const [target, setTarget] = useState<ImportTargetResult | null>(null);
  const [ddl, setDdl] = useState("");
  const [fixes, setFixes] = useState<Set<number>>(new Set());
  const [runOpts, setRunOpts] = useState<ImportRunOptions>({ transactionMode: "single", commitEvery: 10000, maxRejects: 0, keepIdentity: false, fireTriggers: false, checkConstraints: true, tableLock: false });
  const [job, setJob] = useState<string | null>(null);
  const [result, setResult] = useState<ImportProgressEvent | null>(null);
  const sessionRef = useRef<string | undefined>(undefined);
  sessionRef.current = sessionId;
  const preview = useExecutionStream(ins?.previewExecutionId);
  const rs = preview?.resultSets[0];

  useEffect(() => () => void closeImportSession(sessionRef.current), []);
  // Set when the request goes out, not after the next commit: progress can arrive before the listener would re-subscribe.
  const watch = useRef<{ token?: string; job?: string; target?: { schema: string; table: string; rows: number; file: string } }>({});
  // A small import can finish before import.run's reply names its job: keep the latest event of unknown jobs to replay.
  const early = useRef(new Map<string, ImportProgressEvent>());
  const onProgress = (e: ImportProgressEvent) => {
    if (e.jobId === watch.current.token || e.jobId === watch.current.job) setProgress(e);
    if (e.jobId === watch.current.job && e.done) {
      setResult(e);
      setBusy(false);
      const t = watch.current.target;
      if (t && e.errorCode === "outcomeUnknown") markUncertainImport(d.profileId, d.database, t.schema, t.table, { rows: t.rows, file: t.file });
      else if (t && !e.error) forgetUncertainImport(d.profileId, d.database, t.schema, t.table);
      const own = heldSession(d);
      void (own ? useSnapshots.getState().refresh(d.profileId, d.database, false, own) : useSnapshots.getState().refresh(d.profileId, d.database));
    }
  };
  useEffect(
    () =>
      onNotify("import.progress", (e) => {
        if (e.jobId === watch.current.token || e.jobId === watch.current.job) return onProgress(e);
        early.current.delete(e.jobId);
        early.current.set(e.jobId, e);
        if (early.current.size > 16) early.current.delete(early.current.keys().next().value!);
      }),
    [d.profileId, d.database],
  );

  const tables = useMemo(() => (snap?.snapshot?.objects ?? []).filter((o) => o.kind === "table").map((o) => `${o.schema}.${o.name}`).sort(), [snap]);

  const choose = async () => {
    try {
      const f = await pickImportFile();
      if (!f) return;
      watch.current.token = f.token;
      setFile(f);
      setIns(null);
      await inspect(f, opts);
    } catch (e) {
      toast("error", (e as Error).message);
    }
  };

  const inspect = async (f: PickedFile, o: ImportReadOptions) => {
    setBusy(true);
    setProgress(null);
    try {
      const r = await rpc("import.inspect", { fileToken: f.token, filePath: null, options: o });
      setIns(r);
      setOpts({ ...o, format: r.format, delimiter: r.delimiter ?? null, hasHeader: r.hasHeader, encoding: r.encoding, sheet: r.sheet ?? null });
      setMapping(null);
      setTarget(null);
      if (!d.table && !table) setTable(r.fileName.replace(/\.[^.]+$/, "").replace(/[^A-Za-z0-9_]+/g, "_").replace(/^(\d)/, "t_$1"));
      const key = r.columns.find((c) => c.candidateKey && (c.kind === "int32" || c.kind === "int64" || c.kind === "uuid"));
      setPk(key ? [key.name] : []);
    } catch (e) {
      toast("error", (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const ensureSession = async () => {
    // A session-owned engine's database open in a query tab goes through that tab's session: an in-memory database
    // exists only there, and the sidecar reads that session's own view (temporary tables included) and runs the import
    // under its execution lock, after the tab's own query. The wizard never closes it. Once the tab is gone, an
    // in-memory database is refused by openImportSession (with the reason) rather than loaded into a new, empty one.
    const tabSession = heldSession(d);
    if (tabSession) return tabSession;
    if (sessionId) return sessionId;
    const s = await openImportSession(d.profileId, d.database);
    setSessionId(s);
    return s;
  };

  const check = async (m: ImportMapping[] | null = mapping) => {
    if (!ins || !table.trim()) return;
    setBusy(true);
    try {
      const sid = await ensureSession();
      const r = await rpc("import.checkTarget", {
        sessionId: sid, inspectionId: ins.inspectionId, schema: schema.trim(), table: table.trim(), newTable, mapping: m, typeOverrides: types, primaryKey: pk,
      });
      setTarget(r);
      setMapping(r.mapping);
      setDdl(r.createTableSql ?? "");
      setFixes(new Set(r.verdicts.map((v, i) => (v.kind === "widen" && v.suggestedSql ? i : -1)).filter((i) => i >= 0)));
      setStep("target");
    } catch (e) {
      toast("error", (e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const blocking = (target?.verdicts ?? []).filter((v, i) => (v.kind === "requiredUnmapped" || v.kind === "nullIntoNotNull") && !fixes.has(i));
  // The last import into this table may or may not have been committed: no blind retry until the user checked it.
  const doubt = uncertainImport(d.profileId, d.database, schema, table);
  const [checkedDoubt, setCheckedDoubt] = useState("");
  const doubtKey = `${schema.trim().toLowerCase()}.${table.trim().toLowerCase()}`;
  const doubtBlocks = !!doubt && checkedDoubt !== doubtKey;

  const run = async () => {
    if (!ins || !file || !target) return;
    setBusy(true);
    setResult(null);
    try {
      const sid = await ensureSession();
      // The wizard's DDL runs through the normal guarded execution first (CREATE TABLE, chosen ALTERs).
      const statements = [...(newTable ? [ddl] : []), ...[...fixes].map((i) => target.verdicts[i]?.suggestedSql).filter((x): x is string => !!x)];
      for (const sql of statements) {
        const err = await executeGuarded(sid, sql);
        if (err) {
          toast("error", `DDL failed: ${err}`);
          setBusy(false);
          return;
        }
      }
      let confirmedProd = false;
      watch.current.target = { schema: schema.trim(), table: table.trim(), rows: ins.rowCount, file: file.name };
      for (let round = 0; round < 2; round++) {
        const r = await rpc("import.run", {
          sessionId: sid, inspectionId: ins.inspectionId, fileToken: file.token, filePath: null, schema: schema.trim(), table: table.trim(),
          mapping, options: runOpts, confirmedProd,
        });
        if (r.blocked) {
          if (!(await ask<boolean>((resolve) => ({ kind: "guard", block: r.blocked!, resolve })))) return setBusy(false);
          confirmedProd = true;
          continue;
        }
        watch.current.job = r.jobId!;
        setJob(r.jobId!);
        setStep("load");
        const seen = early.current.get(r.jobId!);
        if (seen) onProgress(seen);
        return;
      }
    } catch (e) {
      const code = e instanceof RpcError ? e.appCode : undefined;
      toast("error", code === "fileToken" ? "The file can be imported once per pick. Pick it again to import it again." : (e as Error).message);
      setBusy(false);
    }
  };

  const cancel = () => job && void rpc("import.cancel", { jobId: job }).catch(() => {});

  const setMap = (fileColumn: string, targetColumn: string | null) => {
    const next = (mapping ?? []).map((m) => (m.fileColumn === fileColumn ? { ...m, targetColumn } : m));
    setMapping(next);
    void check(next);
  };

  const targetColumns = useMemo(() => {
    const o = (snap?.snapshot?.objects ?? []).find((x) => x.kind === "table" && x.schema.toLowerCase() === schema.toLowerCase() && x.name.toLowerCase() === table.toLowerCase());
    return o?.columns?.map((c) => c.name) ?? [];
  }, [snap, schema, table]);

  const pct = progress && progress.totalBytes > 0 ? Math.min(100, Math.round((progress.bytesProcessed / progress.totalBytes) * 100)) : null;

  return (
    <Modal
      title="Import data"
      subtitle={<>{profile ? <ServerChip profileId={d.profileId} database={d.database} /> : d.database}{file ? ` · ${file.name}` : ""}</>}
      onClose={() => (busy && job && !result ? toast("info", "Cancel the import first.") : close())}
      width={1000}
      bodyClassName="p-4 overflow-auto max-h-[75vh]"
      footer={
        <>
          {step !== "file" && step !== "load" && <button className="q-btn" onClick={() => setStep("file")}>← File</button>}
          <div className="flex-1" />
          {step === "file" && <button className="q-btn q-btn-primary" disabled={!ins || busy || !table.trim()} onClick={() => void check()}>Next: target →</button>}
          {step === "target" && (
            <button className="q-btn q-btn-primary" disabled={busy || blocking.length > 0 || doubtBlocks}
              title={blocking.length ? "Resolve the red verdicts first" : doubtBlocks ? "Check the table first: the last import into it may already have written these rows" : undefined} onClick={() => void run()}>
              {newTable ? "Create table and import" : "Import"} {ins ? `${ins.rowCount.toLocaleString()} rows` : ""}
            </button>
          )}
          {step === "load" && !result && <button className="q-btn q-btn-danger" onClick={cancel}>Cancel import</button>}
          {step === "load" && result && <button className="q-btn q-btn-primary" onClick={close}>Close</button>}
        </>
      }
    >
      {step === "file" && (
        <div className="space-y-3">
          <div className="flex items-center gap-2">
            <button className="q-btn q-btn-primary" onClick={() => void choose()} disabled={busy}>{file ? "Choose another file…" : "Choose file…"}</button>
            {file && <span className="text-muted">{file.name} · {bytes(file.size)}</span>}
            {busy && progress && progress.phase === "profiling" && (
              <LoadingStatus label={`Profiling every row… ${progress.rowsProcessed.toLocaleString()} rows${pct != null ? ` (${pct} %)` : ""}`} />
            )}
          </div>
          {busy && (!progress || progress.phase !== "profiling") && <LoadingStatus label={ins ? "Checking target…" : "Reading file…"} />}
          {ins && file && (
            <>
              <div className="grid grid-cols-5 gap-2">
                <Field label="Format">
                  <select className="q-input w-full" value={opts.format} onChange={(e) => void inspect(file, { ...opts, format: e.target.value as ImportFormat })}>
                    {["csv", "tsv", "json", "ndjson", "xlsx"].map((f) => <option key={f} value={f}>{f.toUpperCase()}</option>)}
                  </select>
                </Field>
                {(ins.format === "csv" || ins.format === "tsv") && (
                  <Field label="Delimiter">
                    <select className="q-input w-full" value={opts.delimiter ?? ","} onChange={(e) => void inspect(file, { ...opts, delimiter: e.target.value })}>
                      {[",", ";", "\\t", "|"].map((x) => <option key={x} value={x}>{x === "\\t" ? "Tab" : x}</option>)}
                    </select>
                  </Field>
                )}
                {ins.format === "xlsx" && (
                  <Field label="Sheet">
                    <select className="q-input w-full" value={opts.sheet ?? ""} onChange={(e) => void inspect(file, { ...opts, sheet: e.target.value })}>
                      {ins.sheets.map((s) => <option key={s} value={s}>{s}</option>)}
                    </select>
                  </Field>
                )}
                {ins.format !== "json" && ins.format !== "ndjson" && (
                  <Field label="First row">
                    <select className="q-input w-full" value={String(opts.hasHeader)} onChange={(e) => void inspect(file, { ...opts, hasHeader: e.target.value === "true" })}>
                      <option value="true">Column names</option>
                      <option value="false">Data</option>
                    </select>
                  </Field>
                )}
                {ins.format !== "xlsx" && (
                  <Field label="Encoding">
                    <select className="q-input w-full" value={opts.encoding ?? "utf-8"} onChange={(e) => void inspect(file, { ...opts, encoding: e.target.value })}>
                      {["utf-8", "utf-16le", "utf-16be", "latin1"].map((x) => <option key={x}>{x}</option>)}
                    </select>
                  </Field>
                )}
              </div>
              <div className="text-[11px] text-muted">
                {ins.rowCount.toLocaleString()} rows · {ins.columns.length} columns, every row profiled.
                {ins.notes.map((n) => <div key={n} className="text-warn">{n}</div>)}
              </div>
              <table className="w-full text-[11px]">
                <thead className="text-muted text-left">
                  <tr><th className="font-normal">Column</th><th className="font-normal">Inferred</th><th className="font-normal">NULLs</th><th className="font-normal">Max length</th><th className="font-normal">Distinct ≈</th>{newTable && <th className="font-normal">Key</th>}</tr>
                </thead>
                <tbody>
                  {ins.columns.map((c) => (
                    <tr key={c.name} className="border-t border-line/50">
                      <td className="q-mono text-fg">{c.name}</td>
                      <td>{KIND_LABEL[c.kind] ?? c.kind}{c.kind === "decimal" ? ` (${c.precision},${c.scale})` : ""}{c.min != null ? ` ${c.min}…${c.max}` : ""}</td>
                      <td>{c.nullCount.toLocaleString()}</td>
                      <td>{c.maxLength.toLocaleString()}</td>
                      <td>{c.distinctEstimate.toLocaleString()}{c.candidateKey ? " · unique" : ""}</td>
                      {newTable && <td><input type="checkbox" checked={pk.includes(c.name)} onChange={(e) => setPk((s) => (e.target.checked ? [...s, c.name] : s.filter((x) => x !== c.name)))} /></td>}
                    </tr>
                  ))}
                </tbody>
              </table>
              {rs && (
                <div className="h-[220px] border border-line rounded relative">
                  <Suspense fallback={null}><ResultGrid rs={rs} executionId={ins.previewExecutionId} /></Suspense>
                </div>
              )}
              <div className="grid grid-cols-3 gap-2 items-end">
                <Field label="Load into">
                  <select className="q-input w-full" value={newTable ? "new" : "existing"} onChange={(e) => setNewTable(e.target.value === "new")}>
                    <option value="new">A new table</option>
                    <option value="existing">An existing table</option>
                  </select>
                </Field>
                <Field label="Schema"><input className="q-input w-full" value={schema} onChange={(e) => setSchema(e.target.value)} /></Field>
                <Field label="Table">
                  <input className="q-input w-full" list="import-tables" value={table} onChange={(e) => {
                    const v = e.target.value;
                    const dot = v.indexOf(".");
                    if (!newTable && dot > 0 && tables.includes(v)) { setSchema(v.slice(0, dot)); setTable(v.slice(dot + 1)); } else setTable(v);
                  }} />
                  {!newTable && <datalist id="import-tables">{tables.map((t) => <option key={t} value={t} />)}</datalist>}
                </Field>
              </div>
            </>
          )}
        </div>
      )}

      {step === "target" && target && ins && (
        <div className="space-y-3">
          {doubt && (
            <div className="p-2 rounded border border-warn/50 bg-warn/10 text-fg" role="alert">
              <div className="font-semibold text-warn">The last import into {schema}.{table} may already have written its rows</div>
              <div>
                Its connection failed while {doubt.rows.toLocaleString()} rows from {doubt.file} were being committed, so whether they were
                written is unknown. Check the table (for example its row count) before you import again: importing the same rows twice adds
                them twice.
              </div>
              <label className="flex items-center gap-1 mt-1">
                <input type="checkbox" checked={checkedDoubt === doubtKey} onChange={(e) => setCheckedDoubt(e.target.checked ? doubtKey : "")} />
                I checked {schema}.{table}: import into it
              </label>
            </div>
          )}
          {newTable ? (
            <>
              <div className="text-[11px] text-muted">The table is created first, through the normal (guarded) execution. Edit the statement or the column types if needed.</div>
              <table className="w-full text-[11px]">
                <thead className="text-muted text-left"><tr><th className="font-normal">File column</th><th className="font-normal">Column</th><th className="font-normal">Type</th></tr></thead>
                <tbody>
                  {(mapping ?? []).map((m) => (
                    <tr key={m.fileColumn} className="border-t border-line/50">
                      <td className="q-mono">{m.fileColumn}</td>
                      <td><input className="q-input h-[20px]" value={m.targetColumn ?? ""} placeholder="(skip)" onChange={(e) => setMapping((mapping ?? []).map((x) => (x.fileColumn === m.fileColumn ? { ...x, targetColumn: e.target.value || null } : x)))} /></td>
                      <td><input className="q-input h-[20px] q-mono" value={types[m.fileColumn] ?? target.proposedTypes?.[m.fileColumn] ?? ""} onChange={(e) => setTypes({ ...types, [m.fileColumn]: e.target.value })} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <button className="q-btn" onClick={() => void check()}>Regenerate CREATE TABLE</button>
              <textarea className="q-input w-full h-40 q-mono text-[11px]" value={ddl} onChange={(e) => setDdl(e.target.value)} spellCheck={false} />
            </>
          ) : (
            <table className="w-full text-[11px]">
              <thead className="text-muted text-left"><tr><th className="font-normal">File column</th><th className="font-normal">Table column</th></tr></thead>
              <tbody>
                {(mapping ?? []).map((m) => (
                  <tr key={m.fileColumn} className="border-t border-line/50">
                    <td className="q-mono">{m.fileColumn}</td>
                    <td>
                      <select className="q-input h-[20px] py-0" value={m.targetColumn ?? ""} onChange={(e) => setMap(m.fileColumn, e.target.value || null)}>
                        <option value="">(skip)</option>
                        {targetColumns.map((c) => <option key={c} value={c}>{c}</option>)}
                      </select>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {target.verdicts.length > 0 && (
            <div className="space-y-1">
              <div className="text-[11px] font-semibold text-muted">Checks</div>
              {target.verdicts.map((v: ImportColumnVerdict, i) => (
                <div key={i} className="flex items-start gap-2 text-[11px]">
                  <span className={`w-28 shrink-0 font-semibold ${VERDICT_STYLE[v.kind] ?? ""}`}>{v.kind}</span>
                  <span className="q-mono w-40 shrink-0 truncate">{v.fileColumn || "—"} → {v.targetColumn ?? "—"}</span>
                  <span className="flex-1">{v.message}</span>
                  {v.suggestedSql && (
                    <label className="flex items-center gap-1 shrink-0" title={v.suggestedSql}>
                      <input type="checkbox" checked={fixes.has(i)} onChange={(e) => setFixes((s) => { const n = new Set(s); if (e.target.checked) n.add(i); else n.delete(i); return n; })} />
                      run <span className="q-mono text-muted truncate max-w-[220px]">{v.suggestedSql}</span>
                    </label>
                  )}
                </div>
              ))}
            </div>
          )}
          <div className="grid grid-cols-4 gap-2 items-end border-t border-line pt-3">
            <Field label="Transaction">
              <select className="q-input w-full" value={runOpts.transactionMode} onChange={(e) => setRunOpts({ ...runOpts, transactionMode: e.target.value as ImportRunOptions["transactionMode"] })}>
                <option value="single">One transaction (all or nothing)</option>
                <option value="batches">Commit every N rows</option>
              </select>
            </Field>
            {runOpts.transactionMode === "batches" && (
              <Field label="Rows per commit" hint="A failure keeps the committed batches; the summary says where to resume.">
                <input className="q-input w-full" type="number" min={100} value={runOpts.commitEvery ?? 10000} onChange={(e) => setRunOpts({ ...runOpts, commitEvery: Number(e.target.value) })} />
              </Field>
            )}
            <Field label="Bad rows allowed" hint="Rows that cannot be converted go to a reject log; 0 stops at the first.">
              <input className="q-input w-full" type="number" min={0} value={runOpts.maxRejects} onChange={(e) => setRunOpts({ ...runOpts, maxRejects: Math.max(0, Number(e.target.value)) })} />
            </Field>
            <div className="space-y-0.5 text-[11px]">
              <label className="flex items-center gap-1"><input type="checkbox" checked={runOpts.keepIdentity} onChange={(e) => setRunOpts({ ...runOpts, keepIdentity: e.target.checked })} /> Keep identity values</label>
              {BULK_COPY_OPTIONS[engine] && (
                <>
                  <label className="flex items-center gap-1"><input type="checkbox" checked={runOpts.fireTriggers} onChange={(e) => setRunOpts({ ...runOpts, fireTriggers: e.target.checked })} /> Fire triggers</label>
                  <label className="flex items-center gap-1"><input type="checkbox" checked={runOpts.checkConstraints} onChange={(e) => setRunOpts({ ...runOpts, checkConstraints: e.target.checked })} /> Check constraints</label>
                  <label className="flex items-center gap-1"><input type="checkbox" checked={runOpts.tableLock} onChange={(e) => setRunOpts({ ...runOpts, tableLock: e.target.checked })} /> Table lock (faster)</label>
                </>
              )}
            </div>
          </div>
        </div>
      )}

      {step === "load" && (
        <div className="space-y-3">
          <ProgressBar label="Import progress" value={result && !result.error ? 100 : pct} active={!result} failed={!!result?.error} />
          <div className="text-fg">
            {progress ? `${progress.rowsLoaded.toLocaleString()} rows loaded · ${progress.rejected.toLocaleString()} rejected · ${(progress.elapsedMs / 1000).toFixed(1)} s` : "Starting…"}
          </div>
          {result && (
            <div className={`p-3 rounded border ${result.error ? "border-err/50 bg-err/10" : "border-ok/50 bg-ok/10"}`}>
              {result.error ? <div className="text-err font-semibold">{result.cancelled ? "Cancelled — nothing was kept." : result.error}</div> : <div className="text-ok font-semibold">Imported {result.rowsLoaded.toLocaleString()} rows into {schema}.{table}.</div>}
              {result.committedRows != null && result.error && result.errorCode !== "outcomeUnknown" && <div className="text-fg">{result.committedRows.toLocaleString()} rows were committed before the failure: resume after file row {result.committedRows.toLocaleString()}.</div>}
              {result.errorCode === "outcomeUnknown" && <div className="text-fg">Querithm asks you to confirm you checked the table before it imports into it again.</div>}
              {result.rejectFile && (
                <div className="mt-1 text-fg">
                  {result.rejected.toLocaleString()} rejected row(s) with the reason per row: <span className="q-mono select-text">{result.rejectFile}</span>
                  <button className="q-btn h-[20px] ml-2" onClick={() => void navigator.clipboard.writeText(result.rejectFile!)}>Copy path</button>
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </Modal>
  );
}

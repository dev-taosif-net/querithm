// Schema compare tab: source ⇄ target pickers, the object list with filters and checkboxes, a diff per
// object, and the migration script, which opens in a query tab bound to the target so the usual guards apply.
import { memo, useEffect, useMemo, useRef, useState } from "react";
import type { CompareItem, SchemaCompareResult, SchemaMigrationResult } from "../ipc/contract";
import { rpc } from "../ipc/client";
import type { ToolTab } from "../store/toolTabs";
import { useToolTabs } from "../store/toolTabs";
import { useProfiles } from "../store/profiles";
import { capabilityRefusal, useConnections } from "../store/connections";
import { ownSessionFor, sessionParam, useToolSnapshot } from "../store/snapshots";
import { toast } from "../store/ui";
import { newQueryTab } from "../app/actions";
import { initMonaco, monaco } from "../editor/monaco";
import { languageFor } from "../editor/editors";
import { knownEngine } from "../engines";
import {
  buildRows, CLASS_BADGE, CLASS_FILTERS, counts, defaultSelection, filterItems, itemName, renameKey, sideReady, sideTitle, swapSides, whitespaceOnly,
  type ClassFilter, type CompareTabParams, type GroupBy, type SideState,
} from "./schemaCompare";
import { exportSchemaModel, pickSchemaFile, sideParams } from "./compareActions";

const ROW = 24;
const FONT = '"JetBrains Mono Variable", "JetBrains Mono", monospace';

export const SchemaCompareTab = memo(function SchemaCompareTab({ tool }: { tool: ToolTab }) {
  const params = tool.params as unknown as CompareTabParams;
  const [source, setSource] = useState<SideState>(params.source ?? { kind: "live" });
  const [target, setTarget] = useState<SideState>(params.target ?? { kind: "live" });
  const [result, setResult] = useState<SchemaCompareResult | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [cls, setCls] = useState<ClassFilter>("all");
  const [hideWhitespace, setHideWhitespace] = useState(false);
  const [hideIdentical, setHideIdentical] = useState(true);
  const [search, setSearch] = useState("");
  const [groupBy, setGroupBy] = useState<GroupBy>("kind");
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [focus, setFocus] = useState<string | null>(null);
  const [view, setView] = useState<"diff" | "migration">("diff");
  const [renames, setRenames] = useState<Set<string>>(new Set());
  const [dataLoss, setDataLoss] = useState(false);
  const profiles = useProfiles((s) => s.profiles);
  const profileName = (id: string) => profiles.find((p) => p.id === id)?.name;
  const engine = useMemo(() => {
    const live = [source, target].find((s) => s.kind === "live" && s.profileId) as Extract<SideState, { kind: "live" }> | undefined;
    return profiles.find((p) => p.id === live?.profileId)?.engine ?? knownEngine(result?.source.engine);
  }, [source, target, profiles, result]);

  // SQLite and DuckDB have no schema compare: the sidecar's reason, before anything runs (it refuses a call as well).
  const refusal = useConnections((s) => [source, target].map((x) => (x.kind === "live" ? capabilityRefusal(s.servers, x.profileId, "schemaCompare") : undefined)).find(Boolean));

  useEffect(() => {
    useToolTabs.getState().update(tool.id, { params: { source, target } as unknown as Record<string, unknown> });
  }, [source, target, tool.id]);

  const run = async () => {
    setRunning(true);
    setError(null);
    try {
      const r = await rpc("schemaCompare.run", { source: await sideParams(source), target: await sideParams(target) });
      setResult(r);
      setChecked(defaultSelection(r.items));
      setRenames(new Set());
      setFocus(r.items.find((i) => i.class !== "identical")?.key ?? null);
      const title = `Compare · ${sideTitle(source, profileName)} → ${sideTitle(target, profileName)}`;
      useToolTabs.getState().update(tool.id, { title, profileId: target.kind === "live" ? target.profileId : undefined, database: target.kind === "live" ? target.database : undefined });
      schemaCompareBench.lastMs = r.elapsedMs;
      schemaCompareBench.lastItems = r.items.length;
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setRunning(false);
    }
  };

  const items = result?.items ?? [];
  const shown = useMemo(() => filterItems(items, { cls, hideWhitespace, hideIdentical, search }), [items, cls, hideWhitespace, hideIdentical, search]);
  const rows = useMemo(() => buildRows(shown, groupBy, collapsed), [shown, groupBy, collapsed]);
  const focused = items.find((i) => i.key === focus);
  const c = counts(items);

  return (
    <div className="flex-1 flex flex-col min-h-0 text-[11px]">
      <div className="px-3 py-1.5 border-b border-line bg-panel flex items-center gap-2 shrink-0 flex-wrap">
        <SidePicker label="Source" side={source} onChange={setSource} />
        <button className="q-btn h-[22px]" title="Swap source and target" aria-label="Swap source and target"
          onClick={() => { const s = swapSides({ source, target }); setSource(s.source!); setTarget(s.target!); }}>⇄</button>
        <SidePicker label="Target" side={target} onChange={setTarget} />
        <button className="q-btn q-btn-primary h-[22px]" disabled={running || !!refusal || !sideReady(source) || !sideReady(target)} title={refusal}
          onClick={() => void run()}>
          {running ? "Comparing…" : result ? "Compare again" : "Compare"}
        </button>
      </div>
      {refusal && <div className="px-3 py-1.5 text-warn border-b border-line bg-panel" role="note">{refusal}</div>}
      {error && <div className="px-3 py-1.5 text-err border-b border-line bg-panel" role="alert">{error}</div>}
      {!result ? (
        <div className="flex-1 flex items-center justify-center text-muted text-xs p-4 text-center">
          {running ? "Reading both schemas…" : "Pick a source and a target (a database, one schema of it, or a saved schema file), then Compare."}
        </div>
      ) : (
        <>
          <div className="min-h-[30px] py-1 px-3 border-b border-line bg-panel flex flex-wrap items-center gap-x-2 gap-y-1 shrink-0 whitespace-nowrap">
            <div className="flex items-center gap-1" role="group" aria-label="Show">
              {CLASS_FILTERS.map((f) => (
                <button key={f.id} onClick={() => setCls(f.id)}
                  className={`px-2 py-0.5 rounded ${cls === f.id ? "bg-accent/15 text-accent border border-accent/40 font-medium" : "bg-raised text-muted border border-transparent"}`}>
                  {f.label}{f.id !== "all" ? ` ${c[f.id]}` : ""}
                </button>
              ))}
            </div>
            <label className="flex items-center gap-1 cursor-pointer"><input type="checkbox" checked={hideWhitespace} onChange={(e) => setHideWhitespace(e.target.checked)} /> Hide whitespace-only</label>
            <label className="flex items-center gap-1 cursor-pointer"><input type="checkbox" checked={hideIdentical} onChange={(e) => setHideIdentical(e.target.checked)} /> Hide identical ({c.identical})</label>
            <select className="q-input h-[22px] py-0" value={groupBy} onChange={(e) => setGroupBy(e.target.value as GroupBy)} aria-label="Group by">
              <option value="kind">By kind</option>
              <option value="action">By action</option>
            </select>
            <input className="q-input h-[22px] w-40" placeholder="Filter by name…" value={search} onChange={(e) => setSearch(e.target.value)} />
            <div className="flex-1" />
            <span className="text-muted">{checked.size} checked · {Math.round(result.elapsedMs)} ms</span>
          </div>
          <div className="flex-1 min-h-0 flex">
            <ItemList rows={rows} checked={checked} focus={focus} onFocus={(k) => { setFocus(k); setView("diff"); }} onCheck={setChecked}
              onToggleGroup={(k) => setCollapsed((s) => { const n = new Set(s); if (n.has(k)) n.delete(k); else n.add(k); return n; })} />
            <div className="flex-1 min-w-0 flex flex-col border-l border-line">
              <div className="h-[28px] px-2 border-b border-line bg-panel flex items-center gap-1 shrink-0">
                <button className={`q-btn h-[22px] ${view === "diff" ? "border-accent text-accent" : ""}`} onClick={() => setView("diff")}>Object</button>
                <button className={`q-btn h-[22px] ${view === "migration" ? "border-accent text-accent" : ""}`} onClick={() => setView("migration")}>Migration</button>
                {view === "diff" && focused && <span className="ml-2 truncate text-muted">{itemName(focused)} — left: target, right: source</span>}
              </div>
              {view === "diff" ? (
                <ObjectDiff compareId={result.compareId} item={focused} language={languageFor(engine)} />
              ) : (
                <Migration result={result} checked={checked} renames={renames} setRenames={setRenames} dataLoss={dataLoss} setDataLoss={setDataLoss}
                  target={target} language={languageFor(engine)} />
              )}
            </div>
          </div>
          {result.notes.length > 0 && (
            <div className="px-3 py-1 border-t border-line bg-panel text-muted truncate" title={result.notes.join("\n")}>Not compared: {result.notes.join(" · ")}</div>
          )}
        </>
      )}
    </div>
  );
});

function SidePicker({ label, side, onChange }: { label: string; side: SideState; onChange: (s: SideState) => void }) {
  const profiles = useProfiles((s) => s.profiles);
  const live = side.kind === "live" ? side : null;
  const server = useConnections((s) => (live?.profileId ? s.servers[live.profileId] : undefined));
  // A session-owned engine's side reads its tab session's view (Phase D0d); a server engine, the shared snapshot.
  const snap = useToolSnapshot(live ?? undefined).entry?.snapshot;
  const schemas = useMemo(() => (snap ? [...new Set(snap.objects.map((o) => o.schema))].sort() : []), [snap]);
  const databases = (server?.databases ?? []).filter((d) => !d.isSystem).map((d) => d.name);
  // An in-memory database is listed nowhere (each tab has its own): the one this side was opened on stays selectable.
  if (server?.memory && live?.database && !databases.includes(live.database)) databases.push(live.database);
  useEffect(() => {
    if (live?.profileId && !server) void useConnections.getState().connect(live.profileId);
  }, [live?.profileId, server]);

  return (
    <div className="flex items-center gap-1" role="group" aria-label={label}>
      <span className="text-muted font-medium">{label}</span>
      <select className="q-input h-[22px] py-0" value={side.kind} aria-label={`${label} kind`}
        onChange={(e) => {
          if (e.target.value === "live") onChange({ kind: "live" });
          else void pickSchemaFile().then((f) => f && onChange({ kind: "file", token: f.token, name: f.name }));
        }}>
        <option value="live">Database</option>
        <option value="file">{side.kind === "file" ? side.name : "Schema file…"}</option>
      </select>
      {live && (
        <>
          <select className="q-input h-[22px] py-0 max-w-[160px]" value={live.profileId ?? ""} aria-label={`${label} connection`}
            onChange={(e) => onChange({ kind: "live", profileId: e.target.value || undefined })}>
            <option value="">Connection…</option>
            {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          <select className="q-input h-[22px] py-0 max-w-[150px]" value={live.database ?? ""} disabled={!live.profileId} aria-label={`${label} database`}
            onChange={(e) => onChange({ kind: "live", profileId: live.profileId, database: e.target.value || undefined, schema: null, ...sessionParam(ownSessionFor(live.profileId, e.target.value)) })}>
            <option value="">{server?.status === "connecting" ? "Connecting…" : "Database…"}</option>
            {databases.map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
          <select className="q-input h-[22px] py-0 max-w-[130px]" value={live.schema ?? ""} disabled={!live.database} aria-label={`${label} schema`}
            onChange={(e) => onChange({ ...live, schema: e.target.value || null })}>
            <option value="">All schemas</option>
            {schemas.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
          <button className="q-btn h-[22px]" disabled={!sideReady(side)} title="Save this schema to a file, to compare against later"
            onClick={() => void exportSchemaModel(side)}>Save…</button>
        </>
      )}
    </div>
  );
}

function ItemList({ rows, checked, focus, onFocus, onCheck, onToggleGroup }: {
  rows: ReturnType<typeof buildRows>; checked: Set<string>; focus: string | null;
  onFocus: (key: string) => void; onCheck: (s: Set<string>) => void; onToggleGroup: (key: string) => void;
}) {
  const box = useRef<HTMLDivElement>(null);
  const [scroll, setScroll] = useState(0);
  const [height, setHeight] = useState(600);
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setHeight(el.clientHeight));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const start = Math.max(0, Math.floor(scroll / ROW) - 10);
  const end = Math.min(rows.length, Math.ceil((scroll + height) / ROW) + 10);
  const toggle = (keys: string[], on: boolean) => {
    const n = new Set(checked);
    for (const k of keys) if (on) n.add(k); else n.delete(k);
    onCheck(n);
  };
  return (
    <div ref={box} className="w-[380px] shrink-0 overflow-y-auto relative" onScroll={(e) => setScroll(e.currentTarget.scrollTop)} data-q-compare-list>
      {rows.length === 0 && <div className="p-4 text-muted text-center">Nothing to show with these filters.</div>}
      <div style={{ height: rows.length * ROW, position: "relative" }}>
        {rows.slice(start, end).map((row, i) => {
          const top = (start + i) * ROW;
          if (row.t === "group") {
            const all = row.keys.every((k) => checked.has(k));
            return (
              <div key={row.key} className="absolute left-0 right-0 flex items-center gap-1.5 px-2 bg-panel-2 font-semibold text-muted select-none" style={{ top, height: ROW }}>
                <input type="checkbox" checked={all} aria-label={`Check all ${row.label}`} onChange={(e) => toggle(row.keys, e.target.checked)} />
                <button className="flex-1 text-left" onClick={() => onToggleGroup(row.key)}>{row.label} <span className="text-muted font-normal">{row.count}</span></button>
              </div>
            );
          }
          return <ItemRow key={row.key} item={row.item} top={top} checked={checked.has(row.key)} focused={focus === row.key} onFocus={onFocus} onCheck={(on) => toggle([row.key], on)} />;
        })}
      </div>
    </div>
  );
}

function ItemRow({ item, top, checked, focused, onFocus, onCheck }: { item: CompareItem; top: number; checked: boolean; focused: boolean; onFocus: (k: string) => void; onCheck: (on: boolean) => void }) {
  const badge = CLASS_BADGE[item.class];
  return (
    <div className={`absolute left-0 right-0 flex items-center gap-1.5 pl-5 pr-2 cursor-pointer ${focused ? "bg-accent/15 q-hl" : "hover:bg-hover"}`} style={{ top, height: ROW }}
      onClick={() => onFocus(item.key)} title={[...item.changes, item.note ?? ""].filter(Boolean).join("\n")} data-q-compare-item={item.key}>
      <input type="checkbox" checked={checked} disabled={item.class === "identical"} aria-label={`Include ${itemName(item)}`}
        onClick={(e) => e.stopPropagation()} onChange={(e) => onCheck(e.target.checked)} />
      <span className={`w-3 text-center font-bold ${badge.cls}`} aria-label={item.class}>{badge.text}</span>
      <span className="truncate flex-1">{itemName(item)}</span>
      {item.dataLoss && <span className="text-err text-[10px]" title="Applying this loses data">data loss</span>}
      {whitespaceOnly(item) && <span className="text-muted text-[10px]">whitespace</span>}
      {item.note && <span className="text-warn" title={item.note}>!</span>}
    </div>
  );
}

function ObjectDiff({ compareId, item, language }: { compareId: string; item: CompareItem | undefined; language: string }) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    initMonaco();
    const ed = monaco.editor.createDiffEditor(host.current!, {
      readOnly: true, automaticLayout: true, ignoreTrimWhitespace: false, renderSideBySide: true, minimap: { enabled: false }, fontSize: 12,
      fontFamily: FONT, originalEditable: false, scrollBeyondLastLine: false,
      // A read-only viewer: no current-line fill under the diff tints (their contrast is checked on --q-bg).
      renderLineHighlight: "none",
    });
    // One pair of models for the editor's life: swapping models under a running diff makes Monaco reject it ("Canceled").
    const original = monaco.editor.createModel("", language);
    const modified = monaco.editor.createModel("", language);
    ed.setModel({ original, modified });
    editor.current = ed;
    return () => {
      ed.dispose();
      original.dispose();
      modified.dispose();
    };
  }, [language]);
  useEffect(() => {
    let live = true;
    setError(null);
    const set = (left: string, right: string) => {
      const m = editor.current?.getModel();
      if (!m || !live) return;
      m.original.setValue(left);
      m.modified.setValue(right);
    };
    if (!item) set("", "");
    else
      rpc("schemaCompare.objectSql", { compareId, key: item.key })
        .then((r) => set(r.target ?? "-- not in the target\n", r.source ?? "-- not in the source\n"))
        .catch((e) => live && setError((e as Error).message));
    return () => {
      live = false;
    };
  }, [compareId, item, language]);
  return (
    <div className="flex-1 min-h-0 flex flex-col">
      {item && item.changes.length > 0 && (
        <div className="px-3 py-1 border-b border-line text-muted max-h-[84px] overflow-y-auto">
          {item.changes.map((ch, i) => <div key={i}>{ch}</div>)}
          {item.note && <div className="text-warn">{item.note}</div>}
        </div>
      )}
      {error && <div className="px-3 py-1 text-err">{error}</div>}
      <div ref={host} className="flex-1 min-h-0" />
    </div>
  );
}

function Migration({ result, checked, renames, setRenames, dataLoss, setDataLoss, target, language }: {
  result: SchemaCompareResult; checked: Set<string>; renames: Set<string>; setRenames: (s: Set<string>) => void;
  dataLoss: boolean; setDataLoss: (v: boolean) => void; target: SideState; language: string;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const [mig, setMig] = useState<SchemaMigrationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const offered = result.possibleRenames.filter((r) => checked.has(r.tableKey));
  useEffect(() => {
    initMonaco();
    const ed = monaco.editor.create(host.current!, {
      value: "", language, readOnly: true, automaticLayout: true, minimap: { enabled: false }, fontSize: 12, fontFamily: FONT, scrollBeyondLastLine: false,
    });
    editor.current = ed;
    return () => {
      ed.getModel()?.dispose();
      ed.dispose();
    };
  }, [language]);
  useEffect(() => {
    if (result.migrationRefusal) return;
    let live = true;
    rpc("schemaCompare.migrationSql", {
      compareId: result.compareId, keys: [...checked], includeDataLoss: dataLoss,
      renames: result.possibleRenames.filter((r) => checked.has(r.tableKey) && renames.has(renameKey(r))),
    })
      .then((m) => {
        if (!live) return;
        setMig(m);
        setError(null);
        editor.current?.setValue(m.sql);
      })
      .catch((e) => live && setError((e as Error).message));
    return () => {
      live = false;
    };
  }, [result, checked, renames, dataLoss]);

  const open = () => {
    if (!mig || target.kind !== "live" || !target.profileId || !target.database) return;
    newQueryTab({ profileId: target.profileId, database: target.database, text: mig.sql, title: "Migration.sql" });
    toast("info", "The migration is in a new tab on the target. Review it, then run it.");
  };

  if (result.migrationRefusal) return <div className="flex-1 flex items-center justify-center text-muted p-4 text-center">{result.migrationRefusal}</div>;
  return (
    <div className="flex-1 min-h-0 flex flex-col">
      <div className="px-3 py-1.5 border-b border-line flex flex-col gap-1">
        <div className="flex items-center gap-3">
          <label className={`flex items-center gap-1 cursor-pointer ${dataLoss ? "text-err" : ""}`}>
            <input type="checkbox" checked={dataLoss} onChange={(e) => setDataLoss(e.target.checked)} /> Include data-loss changes (drops, type changes)
          </label>
          <div className="flex-1" />
          {mig && <span className="text-muted">{mig.statements} statements</span>}
          <button className="q-btn q-btn-primary h-[22px]" disabled={!mig || mig.statements === 0} onClick={open}>Open in query tab</button>
        </div>
        {!result.transactionalDdl && <div className="text-warn">This engine commits DDL as it goes: the script cannot roll back if a step fails.</div>}
        {offered.length > 0 && (
          <div className="flex flex-col gap-0.5">
            <span className="text-muted">Possible renames (unchecked: the column is dropped and a new one added):</span>
            {offered.map((r) => (
              <label key={renameKey(r)} className="flex items-center gap-1 cursor-pointer pl-2">
                <input type="checkbox" checked={renames.has(renameKey(r))}
                  onChange={(e) => { const n = new Set(renames); if (e.target.checked) n.add(renameKey(r)); else n.delete(renameKey(r)); setRenames(n); }} />
                {r.tableKey.split(":")[1]}: rename <b>{r.from}</b> to <b>{r.to}</b> ({r.type})
              </label>
            ))}
          </div>
        )}
        {mig && mig.skipped.length > 0 && <div className="text-muted" title={mig.skipped.join("\n")}>Left out (data loss): {mig.skipped.length} — {mig.skipped.slice(0, 3).join("; ")}{mig.skipped.length > 3 ? "…" : ""}</div>}
        {mig && mig.warnings.length > 0 && <div className="text-warn" title={mig.warnings.join("\n")}>Review: {mig.warnings.length} — {mig.warnings[0]}{mig.warnings.length > 1 ? "…" : ""}</div>}
        {error && <div className="text-err">{error}</div>}
      </div>
      <div ref={host} className="flex-1 min-h-0" />
    </div>
  );
}

/** Bench hook (§14 metric 21): the last compare's server-side duration and size. */
export const schemaCompareBench = { lastMs: 0, lastItems: 0 };

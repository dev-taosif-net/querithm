// ER diagram tab: neighbourhood, schema and current-query modes over the cached snapshot (no server roundtrip),
// column-to-column FK edges, search highlight, Shift+Click selection → generated JOIN, and SVG/PNG export.
import { memo, useCallback, useEffect, useMemo, useState } from "react";
import { Background, Controls, MiniMap, ReactFlow, type Edge, type Node } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import type { ToolTab } from "../store/toolTabs";
import { useToolTabs } from "../store/toolTabs";
import { toolSession, useToolSnapshot } from "../store/snapshots";
import { useProfiles } from "../store/profiles";
import { dialectOf } from "../engines";
import { toast } from "../store/ui";
import { rpc } from "../ipc/client";
import { saveBytes } from "../app/saveBytes";
import { newQueryTab } from "../app/actions";
import { buildGraph, joinSql, matching, neighbourhood, schemaScope, tablesOf, type Positioned } from "./erdLayout";
import { layoutAsync } from "./layoutAsync";
import { TableNodeCard, handleId, HEADER_HANDLE, type TableNodeData } from "./TableNodeCard";
import { buildSvg, svgToPng, type ErdColors } from "./erdExport";

export type ErdMode = "neighbourhood" | "schema" | "query";
export type Hops = 1 | 2 | "all";

export interface ErdParams {
  mode: ErdMode;
  rootId?: number;
  schema?: string | null;
  /** Table ids referenced by the statement (query mode). */
  queryTables?: number[];
}

const nodeTypes = { table: TableNodeCard };

/** An in-memory database (SQLite, DuckDB) lives in one query tab's session; with that session gone there is nothing to draw. */
export const MEMORY_GONE =
  "Nothing to draw: an in-memory database exists only in the session of the query tab that opened it, and no open tab holds that session. Open the diagram from a query tab on the database (Ctrl+Shift+E).";
const COMPACT_ABOVE = 150;

function cssVar(name: string) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

export function erdColors(): ErdColors {
  return {
    bg: cssVar("--q-bg"), card: cssVar("--q-panel"), header: cssVar("--q-panel-2"), line: cssVar("--q-line"), fg: cssVar("--q-fg"),
    muted: cssVar("--q-muted"), pk: cssVar("--q-warn"), fk: cssVar("--q-accent"), edge: cssVar("--q-muted"),
  };
}

export const ErdTab = memo(function ErdTab({ tool }: { tool: ToolTab }) {
  const params = tool.params as unknown as ErdParams;
  const profileId = tool.profileId!;
  const database = tool.database!;
  // A server engine's shared snapshot; a session-owned engine's view of the tab session it was opened from (Phase D0d).
  const { scope, entry } = useToolSnapshot(tool);
  const profile = useProfiles((s) => s.profiles.find((p) => p.id === profileId));
  const snap = entry?.snapshot;
  const [mode, setMode] = useState<ErdMode>(params.mode);
  const [hops, setHops] = useState<Hops>(1);
  const [schema, setSchema] = useState<string | null>(params.schema ?? null);
  const [extra, setExtra] = useState<Set<number>>(new Set());
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<number[]>([]);
  const [hoverEdge, setHoverEdge] = useState<string | null>(null);
  const [positions, setPositions] = useState<Positioned[] | null>(null);
  const [rowCounts, setRowCounts] = useState<Map<number, { rows: number; estimated: boolean }>>(new Map());
  const [layoutMs, setLayoutMs] = useState<number | null>(null);

  const ids = useMemo(() => {
    if (!snap) return new Set<number>();
    let base: Set<number>;
    if (mode === "neighbourhood" && params.rootId != null) base = neighbourhood(snap, [params.rootId], hops);
    else if (mode === "query") base = new Set((params.queryTables ?? []).filter((id) => tablesOf(snap).has(id)));
    else base = schemaScope(snap, schema);
    for (const id of extra) base.add(id);
    return base;
  }, [snap, mode, hops, schema, params.rootId, params.queryTables, extra]);

  const [compactChoice, setCompactChoice] = useState<boolean | null>(null);
  const compact = compactChoice ?? ids.size > COMPACT_ABOVE;
  const graph = useMemo(() => (snap ? buildGraph(snap, ids, compact) : { nodes: [], edges: [] }), [snap, ids, compact]);

  useEffect(() => {
    let live = true;
    const t0 = performance.now();
    setPositions(null);
    void layoutAsync({ nodes: graph.nodes.map((n) => ({ id: n.id, width: n.width, height: n.height })), edges: graph.edges })
      .then((p) => {
        if (!live) return;
        setPositions(p);
        setLayoutMs(performance.now() - t0);
        erdBench.lastLayoutMs = performance.now() - t0;
        erdBench.lastNodes = graph.nodes.length;
      })
      .catch((e) => toast("error", `Layout failed: ${(e as Error).message}`));
    return () => {
      live = false;
    };
  }, [graph]);

  const matched = useMemo(() => (snap ? matching(snap, ids, search) : new Set<number>()), [snap, ids, search]);
  const hovered = graph.edges.find((e) => e.id === hoverEdge);

  const expand = useCallback(
    (id: number) => {
      if (!snap) return;
      const more = neighbourhood(snap, [id], 1);
      setExtra((s) => new Set([...s, ...more]));
    },
    [snap],
  );

  const nodes: Node<TableNodeData>[] = useMemo(() => {
    if (!positions) return [];
    const pos = new Map(positions.map((p) => [p.id, p]));
    return graph.nodes.map((t) => {
      const highlight = new Set<string>();
      if (hovered?.from === t.id) hovered.pairs.forEach((p) => highlight.add(p.parent.toLowerCase()));
      if (hovered?.to === t.id) hovered.pairs.forEach((p) => highlight.add(p.referenced.toLowerCase()));
      return {
        id: String(t.id), type: "table", position: { x: pos.get(t.id)?.x ?? 0, y: pos.get(t.id)?.y ?? 0 },
        data: {
          table: t, highlight, selected: selected.includes(t.id), matched: matched.has(t.id), dimmed: !!search.trim() && matched.size > 0 && !matched.has(t.id),
          rowCount: rowCounts.get(t.id), onExpand: expand,
        },
      };
    });
  }, [graph, positions, hovered, selected, matched, search, rowCounts, expand]);

  const edges: Edge[] = useMemo(
    () =>
      graph.edges.map((e) => {
        const from = graph.nodes.find((n) => n.id === e.from);
        const to = graph.nodes.find((n) => n.id === e.to);
        const fromCol = e.pairs[0]?.parent;
        const toCol = e.pairs[0]?.referenced;
        const lit = e.id === hoverEdge;
        return {
          id: e.id, source: String(e.from), target: String(e.to),
          sourceHandle: fromCol && from?.columns.some((c) => c.name === fromCol) ? handleId("out", fromCol) : `${HEADER_HANDLE}-out`,
          targetHandle: toCol && to?.columns.some((c) => c.name === toCol) ? handleId("in", toCol) : HEADER_HANDLE,
          type: e.self ? "smoothstep" : "default",
          label: lit ? `${e.name} (${e.pairs.map((p) => `${p.parent} → ${p.referenced}`).join(", ")})` : undefined,
          style: { stroke: lit ? "var(--q-accent)" : "var(--q-muted)", strokeWidth: lit ? 2.2 : 1.3, strokeDasharray: e.optional ? "5 3" : undefined },
          labelStyle: { fill: "var(--q-fg)", fontSize: 11 },
          labelBgStyle: { fill: "var(--q-panel)" },
        };
      }),
    [graph, hoverEdge],
  );

  const schemas = useMemo(() => (snap ? [...new Set([...tablesOf(snap).values()].map((o) => o.schema))].sort() : []), [snap]);

  const generateJoin = () => {
    if (!snap) return;
    const r = joinSql(snap, selected, dialectOf(profile?.engine));
    if ("error" in r) return toast("warn", r.error);
    newQueryTab({ profileId, database, text: r.sql, title: "Join.sql" });
  };

  const loadRowCounts = async () => {
    try {
      const sessionId = await toolSession(tool);
      const counts = await rpc("meta.rowCounts", { sessionId, database, objectIds: graph.nodes.map((n) => n.id) });
      setRowCounts(new Map(counts.filter((c) => c.rows != null).map((c) => [c.objectId, { rows: c.rows!, estimated: c.estimated }])));
    } catch (e) {
      toast("error", (e as Error).message);
    }
  };

  const exportAs = async (format: "svg" | "png") => {
    if (!positions) return;
    const svg = buildSvg(graph, positions, erdColors());
    try {
      const bytes = format === "svg" ? new TextEncoder().encode(svg) : await svgToPng(svg);
      const name = `erd-${database}${mode === "neighbourhood" && params.rootId != null ? `-${graph.nodes.find((n) => n.id === params.rootId)?.name ?? ""}` : ""}.${format}`;
      const saved = await saveBytes(name, bytes, { name: format.toUpperCase(), extension: format, mime: format === "svg" ? "image/svg+xml" : "image/png" });
      if (saved) toast("success", `Saved ${saved}`);
    } catch (e) {
      toast("error", (e as Error).message);
    }
  };

  useEffect(() => {
    const root = params.rootId != null && snap ? tablesOf(snap).get(params.rootId) : undefined;
    const title = mode === "neighbourhood" && root ? `ERD · ${root.name}` : mode === "query" ? "ERD · query" : `ERD · ${schema ?? database}`;
    if (title !== tool.title) useToolTabs.getState().update(tool.id, { title });
  }, [mode, schema, params.rootId, snap, database, tool.id, tool.title]);

  if (!snap) {
    return (
      <div className="flex-1 flex items-center justify-center text-muted text-xs p-4 text-center">
        {!scope ? MEMORY_GONE : entry?.status === "error" ? `Metadata could not be loaded: ${entry.error}` : "Loading the cached schema…"}
      </div>
    );
  }

  return (
    <div className="flex-1 flex flex-col min-h-0">
      {/* Wraps instead of squeezing: at 960 px the labels broke into one-word columns and the export buttons were cut off. */}
      <div className="min-h-[34px] py-1 px-3 border-b border-line bg-panel flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px] shrink-0 whitespace-nowrap" data-q-erd-toolbar>
        <span className="text-muted">{profile?.name} · {database}</span>
        <select className="q-input h-[22px] py-0" value={mode} onChange={(e) => { setMode(e.target.value as ErdMode); setExtra(new Set()); }} aria-label="Diagram mode">
          {params.rootId != null && <option value="neighbourhood">Around table</option>}
          <option value="schema">Schema</option>
          {params.queryTables && <option value="query">Current query</option>}
        </select>
        {mode === "neighbourhood" && (
          <div className="flex items-center" role="group" aria-label="Neighbourhood depth">
            {([1, 2, "all"] as Hops[]).map((h) => (
              <button key={String(h)} className={`q-btn h-[22px] ${hops === h ? "border-accent text-accent" : ""}`} onClick={() => { setHops(h); setExtra(new Set()); }}>
                {h === "all" ? "All connected" : `${h}-hop`}
              </button>
            ))}
          </div>
        )}
        {mode === "schema" && (
          <select className="q-input h-[22px] py-0" value={schema ?? ""} onChange={(e) => setSchema(e.target.value || null)} aria-label="Schema">
            <option value="">All schemas</option>
            {schemas.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        )}
        <label className="flex items-center gap-1 cursor-pointer" title="Show only key columns">
          <input type="checkbox" checked={compact} onChange={(e) => setCompactChoice(e.target.checked)} /> Compact
        </label>
        <input className="q-input h-[22px] w-44" placeholder="Highlight table or column…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <div className="ml-auto flex flex-wrap items-center justify-end gap-2" role="group" aria-label="Diagram actions">
          <span className="text-muted">
            {graph.nodes.length} tables · {graph.edges.length} FKs{layoutMs != null ? ` · ${Math.round(layoutMs)} ms` : ""}
          </span>
          {selected.length > 0 && (
            <button className="q-btn h-[22px]" onClick={() => setSelected([])}>Clear selection ({selected.length})</button>
          )}
          <button className="q-btn h-[22px]" disabled={selected.length < 2} title="Shift+Click tables to select them" onClick={generateJoin}>Generate SELECT</button>
          <button className="q-btn h-[22px]" onClick={() => void loadRowCounts()}>Load row counts</button>
          <button className="q-btn h-[22px]" disabled={!positions} onClick={() => void exportAs("svg")}>SVG</button>
          <button className="q-btn h-[22px]" disabled={!positions} onClick={() => void exportAs("png")}>PNG</button>
        </div>
      </div>
      <div className="flex-1 min-h-0 relative">
        {graph.nodes.length === 0 ? (
          <div className="h-full flex items-center justify-center text-muted text-xs">{mode === "query" ? "The statement at the cursor references no tables of this database." : "No tables here."}</div>
        ) : (
          <>
            {graph.edges.length === 0 && graph.nodes.length > 1 && (
              <div className="absolute z-10 top-2 left-1/2 -translate-x-1/2 px-3 py-1 rounded bg-panel border border-line text-muted text-[11px]">No foreign keys in this schema</div>
            )}
            {!positions && <div className="absolute z-10 top-2 left-2 text-muted text-[11px]">Laying out {graph.nodes.length} tables…</div>}
            <ReactFlow
              nodes={nodes}
              edges={edges}
              nodeTypes={nodeTypes}
              fitView
              fitViewOptions={{ maxZoom: 1, padding: 0.15 }}
              minZoom={0.05}
              maxZoom={2}
              onlyRenderVisibleElements
              nodesConnectable={false}
              proOptions={{ hideAttribution: true }}
              onEdgeMouseEnter={(_, e) => setHoverEdge(e.id)}
              onEdgeMouseLeave={() => setHoverEdge(null)}
              onNodeClick={(ev, n) => {
                const id = Number(n.id);
                if (ev.shiftKey || ev.ctrlKey || ev.metaKey) setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));
                else setSelected([id]);
              }}
              onPaneClick={() => setSelected([])}
            >
              <Background gap={24} size={1} />
              <Controls showInteractive={false} />
              {graph.nodes.length > 20 && <MiniMap pannable zoomable nodeColor={() => "var(--q-line)"} maskColor="rgba(0,0,0,0.35)" />}
            </ReactFlow>
          </>
        )}
      </div>
    </div>
  );
});

/** Bench hook (§14 metrics 18–19): the last layout's duration and size. */
export const erdBench = { lastLayoutMs: 0, lastNodes: 0 };

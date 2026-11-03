// Execution plan (§11): a React Flow DAG (left-to-right dagre layout, edge thickness by rows, #1–#3 badges for the top
// costs, warnings, a properties inspector) or the same operators as a treegrid table (§12 bottom panel: screen
// readers can walk it, and it fits a short panel). The plan's statements are the ledger's chips.
import { memo, useMemo, useState, type KeyboardEvent } from "react";
import { Background, Controls, Handle, MiniMap, Position, ReactFlow, type Edge, type Node, type NodeProps } from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { Graph, layout } from "@dagrejs/dagre";
import type { PlanNode, PlanStatement, PlanTree } from "../ipc/contract";
import type { TabState } from "../store/tabs";
import { useExecutions } from "../store/executions";
import { useUi, toast } from "../store/ui";
import { Empty } from "../results/ResultsPanel";
import { useThemeKey } from "../app/tabStyle";
import { Chips, MenuButton, PanelFooter, PanelRow, Seg, usePanelWidth } from "../app/panelKit";
import { Icon } from "../app/icons";

const NODE_W = 236;
const NODE_H = 104;

type PlanNodeData = { node: PlanNode; rank: number | null; selected: boolean };

const fmt = (n?: number | null) => (n == null ? "—" : n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e4 ? `${(n / 1e3).toFixed(1)}K` : Math.round(n).toLocaleString());

const PlanNodeCard = memo(function PlanNodeCard({ data }: NodeProps<Node<PlanNodeData>>) {
  const n = data.node;
  const hot = data.rank === 0;
  const warn = n.warnings.length > 0;
  const est = n.estRows * (n.actualExecutions && n.actualExecutions > 0 ? 1 : 1);
  return (
    <div
      className={`rounded-lg p-2.5 text-[11px] shadow-lg border ${
        hot ? "bg-err/10 q-hl border-2 border-err" : warn ? "bg-panel border-warn/60" : "bg-panel border-line"
      } ${data.selected ? "ring-2 ring-accent" : ""}`}
      style={{ width: NODE_W }}
    >
      <Handle type="target" position={Position.Right} style={{ opacity: 0 }} />
      <Handle type="source" position={Position.Left} style={{ opacity: 0 }} />
      <div className="flex items-center justify-between gap-1">
        <span className={`text-[10px] font-bold uppercase truncate ${hot ? "text-err" : warn ? "text-warn" : "text-muted"}`}>{n.logicalOp ?? n.physicalOp}</span>
        {data.rank != null ? (
          <span className={`px-1.5 rounded text-[10px] font-bold ${hot ? "bg-err-solid text-white" : "bg-warn/15 text-warn"}`}>
            #{data.rank + 1} · Cost {n.costPercent?.toFixed(1)}%
          </span>
        ) : (
          <span className="text-[10px] text-muted">Cost {n.costPercent?.toFixed(1) ?? 0}%</span>
        )}
      </div>
      <div className="text-[13px] font-bold text-fg mt-0.5 truncate" title={n.operator}>{n.operator}</div>
      {n.objectName && <div className={`q-mono text-[10.5px] truncate ${hot ? "text-fg" : "text-ident"}`} title={n.objectName}>{n.objectName}</div>}
      <div className="mt-1 flex justify-between text-muted">
        <span>
          {n.actualRows != null ? (
            <>
              Actual <b className="text-fg">{fmt(n.actualRows)}</b> │ Est <b className="text-fg">{fmt(est)}</b>
            </>
          ) : (
            <>Est rows <b className="text-fg">{fmt(n.estRows)}</b></>
          )}
        </span>
        {warn && <span className="text-warn font-bold" title={n.warnings.map((w) => w.message).join("\n")}>⚠ {n.warnings.length}</span>}
      </div>
    </div>
  );
});

const nodeTypes = { plan: PlanNodeCard };

function flatten(root: PlanNode): { nodes: PlanNode[]; edges: [PlanNode, PlanNode][] } {
  const nodes: PlanNode[] = [];
  const edges: [PlanNode, PlanNode][] = [];
  const walk = (n: PlanNode) => {
    nodes.push(n);
    for (const c of n.children) {
      edges.push([n, c]);
      walk(c);
    }
  };
  walk(root);
  return { nodes, edges };
}

function buildGraph(stmt: PlanStatement, selectedId: string | null) {
  if (!stmt.root) return { nodes: [], edges: [] };
  const { nodes, edges } = flatten(stmt.root);
  const ranked = [...nodes].filter((n) => (n.costPercent ?? 0) > 1).sort((a, b) => (b.costPercent ?? 0) - (a.costPercent ?? 0)).slice(0, 3);
  const g = new Graph();
  g.setGraph({ rankdir: "LR", nodesep: 28, ranksep: 80, marginx: 20, marginy: 20 });
  g.setDefaultEdgeLabel(() => ({}));
  nodes.forEach((n) => g.setNode(n.id, { width: NODE_W, height: NODE_H }));
  edges.forEach(([p, c]) => g.setEdge(p.id, c.id));
  layout(g);
  const maxRows = Math.max(1, ...nodes.map((n) => n.actualRows ?? n.estRows));
  const rfNodes: Node<PlanNodeData>[] = nodes.map((n) => {
    const p = g.node(n.id) as { x: number; y: number };
    const rank = ranked.indexOf(n);
    return { id: n.id, type: "plan", position: { x: p.x - NODE_W / 2, y: p.y - NODE_H / 2 }, data: { node: n, rank: rank >= 0 ? rank : null, selected: n.id === selectedId } };
  });
  const rfEdges: Edge[] = edges.map(([p, c]) => {
    const rows = c.actualRows ?? c.estRows;
    const width = 1.5 + 9 * (Math.log10(rows + 1) / Math.log10(maxRows + 1));
    const skew = misestimate(c);
    return {
      id: `${p.id}-${c.id}`, source: c.id, target: p.id, label: `${fmt(rows)} rows${skew ? ` · ${skew}` : ""}`,
      style: { strokeWidth: width, stroke: skew ? "var(--q-err)" : "var(--q-accent)" },
      labelStyle: { fill: "var(--q-ok)", fontSize: 10, fontFamily: "var(--font-mono)" },
      labelBgStyle: { fill: "var(--q-bg)" },
    };
  });
  return { nodes: rfNodes, edges: rfEdges };
}

/** "34×" when actual and estimated rows differ more than tenfold, else undefined (the label carries it, not color alone). */
export function misestimate(n: PlanNode): string | undefined {
  if (n.actualRows == null || n.estRows <= 0) return undefined;
  const ratio = n.actualRows >= n.estRows ? n.actualRows / n.estRows : n.estRows / Math.max(1, n.actualRows);
  return ratio > 10 ? `${Math.round(ratio)}×` : undefined;
}

export function PlanView({ tab }: { tab: TabState }) {
  const view = useExecutions((s) => (tab.activeExecutionId ? s.views[tab.activeExecutionId] : undefined));
  if (!view || view.plans.length === 0) {
    return <Empty text={view && ["running", "streaming", "queued"].includes(view.state) ? "Waiting for the plan…" : "No plan yet. Estimated plan: Ctrl+L. Actual plan: Explain ▾ › Actual plan (Ctrl+M includes it on every run)."} />;
  }
  return <PlanDiagram plans={view.plans} />;
}

/** Renders plans that are already loaded (a query tab's plans, or a running request's plan from the activity monitor). */
export function PlanDiagram({ plans }: { plans: { plan: PlanTree; raw: string }[] }) {
  const themeKey = useThemeKey();
  const width = usePanelWidth();
  const statements = useMemo(() => plans.flatMap((p) => p.plan.statements.map((s) => ({ s, raw: p.raw }))), [plans]);
  const [index, setIndex] = useState(0);
  const [selected, setSelected] = useState<string | null>(null);
  const [mode, setMode] = useState<"graph" | "table">("graph");
  const cur = statements[Math.min(index, statements.length - 1)];
  const graph = useMemo(() => (cur && mode === "graph" ? buildGraph(cur.s, selected) : { nodes: [], edges: [] }), [cur, selected, mode]);
  const nodes = useMemo(() => (cur?.s.root ? flatten(cur.s.root).nodes : []), [cur]);
  const selectedNode = useMemo(() => nodes.find((n) => n.id === selected), [nodes, selected]);

  if (!cur) return <Empty text="The plan has no statements." />;
  const missing = cur.s.missingIndexes[0];
  const warnings = nodes.reduce((n, x) => n + x.warnings.length, 0);
  const skews = nodes.filter((x) => misestimate(x)).length;
  const chips = statements.map((x, i) => ({
    key: String(i), n: String(i + 1), icon: "plan" as const, text: (x.s.text ?? "").replace(/\s+/g, " ").trim().slice(0, 40) || `Statement ${i + 1}`,
    aria: `Plan statement ${i + 1}: ${(x.s.text ?? "").slice(0, 120)}`, title: x.s.text ?? undefined,
  }));
  const openRaw = () => useUi.getState().openDialog({ kind: "cellViewer", title: "Raw plan", value: cur.raw, language: cur.raw.trimStart().startsWith("<") ? "xml" : "json" });
  return (
    <div className="flex-1 flex flex-col min-h-0" data-q-plan>
      <PanelRow
        label="Plan tools"
        ledger={<Chips label="Plan statements" chips={chips} selected={String(index)} onSelect={(k) => { setIndex(Number(k)); setSelected(null); }} />}
        tools={
          <>
            <Seg label="Plan view" value={mode} compact={width === "narrow"} options={[{ value: "graph", label: "Graph", icon: "plan" }, { value: "table", label: "Table", icon: "table" }]} onChange={setMode} />
            {width === "wide" && (
              <span className="text-[11px] text-muted shrink-0">
                Cost <b className="text-fg">{cur.s.subtreeCost?.toFixed(4)}</b>
                {cur.s.elapsedMs != null && <> · elapsed <b className="text-ok">{cur.s.elapsedMs} ms</b> · CPU {cur.s.cpuMs} ms</>}
              </span>
            )}
            {missing && (
              <span className="h-[22px] flex items-center gap-1.5 pl-1.5 pr-0.5 rounded border border-warn/40 bg-warn/15 text-warn text-[11px] shrink-0 min-w-0" title={`Missing index on ${missing.table} — est. impact ${missing.impact.toFixed(1)}%`}>
                <Icon name="alert" size={12} strokeWidth={2} />
                <span className="truncate">Missing index on <b>{missing.table} ({[...missing.equalityColumns, ...missing.inequalityColumns].join(", ")})</b> · {missing.impact.toFixed(1)}%</span>
                <button
                  type="button"
                  className="h-[16px] px-1.5 bg-warn text-bg font-bold rounded text-[10px] shrink-0"
                  onClick={() => void navigator.clipboard.writeText(missing.createStatement).then(() => toast("success", "CREATE INDEX copied."))}
                >
                  Copy CREATE INDEX
                </button>
              </span>
            )}
            <MenuButton
              label="Plan output"
              icon="copy"
              items={() => [
                { label: "View raw plan", run: openRaw },
                { label: `Copy raw plan (${cur.raw.trimStart().startsWith("<") ? "XML" : "JSON"})`, run: () => void navigator.clipboard.writeText(cur.raw).then(() => toast("success", "Raw plan copied.")) },
              ]}
            >
              {width === "narrow" ? undefined : "Raw plan"}
            </MenuButton>
          </>
        }
      />
      <div className="flex-1 flex min-h-0">
        {mode === "table" ? (
          <PlanTable nodes={cur.s.root ? flattenDepth(cur.s.root) : []} selected={selected} onSelect={setSelected} label={`Plan operators, statement ${index + 1}`} />
        ) : (
        <div className="flex-1 relative bg-[color-mix(in_srgb,var(--q-bg)_85%,black)]">
          <ReactFlow
            nodes={graph.nodes}
            edges={graph.edges}
            nodeTypes={nodeTypes}
            fitView
            minZoom={0.1}
            maxZoom={2}
            nodesDraggable={false}
            nodesConnectable={false}
            onNodeClick={(_, n) => setSelected(n.id)}
            onPaneClick={() => setSelected(null)}
            proOptions={{ hideAttribution: true }}
            colorMode={themeKey === "light" ? "light" : "dark"}
            // The canvas is an app surface: the hot node's error tint is read on it (React Flow's own grey is not a token).
            style={{ background: "var(--q-bg)" }}
          >
            <Background gap={24} size={1} color="var(--q-line)" />
            <Controls showInteractive={false} />
            <MiniMap pannable zoomable style={{ background: "var(--q-panel)" }} nodeColor={(n) => ((n.data as PlanNodeData).rank === 0 ? "#ef4444" : "#3b82f6")} />
          </ReactFlow>
        </div>
        )}
        {selectedNode && <Inspector node={selectedNode} />}
      </div>
      <PanelFooter
        left={`${nodes.length} ${nodes.length === 1 ? "operator" : "operators"}${warnings ? ` · ${warnings} ${warnings === 1 ? "warning" : "warnings"}` : ""}${skews ? ` · ${skews} ${skews === 1 ? "misestimate" : "misestimates"}` : ""}`}
        right={selectedNode ? `Selected: ${selectedNode.operator}` : undefined}
      />
    </div>
  );
}

/** Operators with their depth, in tree order, and the #1–#3 cost ranks (the table view). */
function flattenDepth(root: PlanNode): { node: PlanNode; depth: number; rank: number | null }[] {
  const out: { node: PlanNode; depth: number; rank: number | null }[] = [];
  const walk = (n: PlanNode, depth: number) => {
    out.push({ node: n, depth, rank: null });
    n.children.forEach((c) => walk(c, depth + 1));
  };
  walk(root, 0);
  out.filter((x) => (x.node.costPercent ?? 0) > 1).sort((a, b) => (b.node.costPercent ?? 0) - (a.node.costPercent ?? 0)).slice(0, 3).forEach((x, i) => (x.rank = i));
  return out;
}

/** The plan as a WAI-ARIA treegrid: one row per operator, ↑/↓/Home/End move, Enter (or a click) opens it in the inspector. */
function PlanTable({ nodes, selected, onSelect, label }: { nodes: { node: PlanNode; depth: number; rank: number | null }[]; selected: string | null; onSelect: (id: string) => void; label: string }) {
  const [focus, setFocus] = useState(0);
  const key = (e: KeyboardEvent<HTMLTableRowElement>, i: number) => {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      onSelect(nodes[i].node.id);
      return;
    }
    const to = e.key === "ArrowDown" ? i + 1 : e.key === "ArrowUp" ? i - 1 : e.key === "Home" ? 0 : e.key === "End" ? nodes.length - 1 : null;
    if (to == null) return;
    e.preventDefault();
    const next = Math.max(0, Math.min(nodes.length - 1, to));
    setFocus(next);
    (e.currentTarget.parentElement?.children[next] as HTMLElement | undefined)?.focus();
  };
  return (
    <div className="flex-1 overflow-auto min-w-0">
      <table role="treegrid" aria-label={label} className="w-full text-[12px] q-mono" data-q-plan-table>
        <thead className="sticky top-0 bg-panel-2 text-muted text-[11px] font-sans">
          <tr>
            {["Operator", "Object", "Cost", "Est. rows", "Actual rows", "Warnings"].map((h, i) => (
              <th key={h} scope="col" className={`px-3 py-1.5 font-semibold border-b border-line whitespace-nowrap ${i >= 2 && i <= 4 ? "text-right" : "text-left"}`}>{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {nodes.map(({ node: n, depth, rank }, i) => {
            const skew = misestimate(n);
            return (
              <tr
                key={n.id}
                role="row"
                aria-level={depth + 1}
                aria-selected={selected === n.id}
                tabIndex={i === focus ? 0 : -1}
                onClick={() => { setFocus(i); onSelect(n.id); }}
                onKeyDown={(e) => key(e, i)}
                className={`border-b border-line/50 cursor-default outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-inset ${selected === n.id ? "bg-row-sel q-hl" : "hover:bg-panel"}`}
              >
                <td role="gridcell" className="px-3 py-1 font-sans whitespace-nowrap">
                  <span style={{ paddingLeft: depth * 16 }} className="text-fg">{depth ? <span aria-hidden className="text-muted">└ </span> : null}{n.operator}</span>
                </td>
                <td role="gridcell" className="px-3 py-1 text-ident truncate max-w-[280px]" title={n.objectName ?? undefined}>{n.objectName ?? ""}</td>
                <td role="gridcell" className="px-3 py-1 text-right whitespace-nowrap">
                  {rank != null && <span className={`mr-1.5 px-1 rounded-[3px] text-[10px] font-bold ${rank === 0 ? "bg-err-solid text-white" : "bg-warn/15 text-warn"}`}>#{rank + 1}</span>}
                  {(n.costPercent ?? 0).toFixed(1)}%
                </td>
                <td role="gridcell" className="px-3 py-1 text-right">{fmt(n.estRows)}</td>
                <td role="gridcell" className="px-3 py-1 text-right">{n.actualRows != null ? fmt(n.actualRows) : "—"}</td>
                <td role="gridcell" className="px-3 py-1 font-sans text-[11.5px]">
                  {skew && <span className="text-err font-semibold mr-2">{skew} {(n.actualRows ?? 0) > n.estRows ? "more" : "fewer"} rows than estimated</span>}
                  {n.warnings.length > 0 && <span className="text-warn">{n.warnings.map((w) => w.message).join("; ")}</span>}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function Inspector({ node }: { node: PlanNode }) {
  const rows: [string, string, string?][] = [
    ["Operator cost", `${node.estCost.toFixed(4)} (${node.costPercent ?? 0}%)`, (node.costPercent ?? 0) > 30 ? "text-err" : undefined],
    ["Subtree cost", node.subtreeCost.toFixed(4)],
    ["Estimated rows", fmt(node.estRows)],
  ];
  if (node.actualRows != null) rows.push(["Actual rows", fmt(node.actualRows), "text-fg"]);
  if (node.actualExecutions != null) rows.push(["Executions", fmt(node.actualExecutions)]);
  if (node.logicalReads != null) rows.push(["Logical reads", `${node.logicalReads.toLocaleString()} pages`]);
  if (node.elapsedMs != null) rows.push(["Actual elapsed", `${node.elapsedMs} ms`, "text-ok"]);
  return (
    <div className="w-[330px] shrink-0 bg-panel border-l border-line p-3 overflow-y-auto text-[11px] select-text">
      <div className="border-b border-line pb-2 mb-2">
        <div className="text-[10px] text-muted font-bold uppercase">Selected operator</div>
        <div className="text-sm font-bold text-fg">{node.operator}</div>
        {node.objectName && <div className="q-mono text-muted">{node.objectName}</div>}
      </div>
      <div className="space-y-1.5">
        {rows.map(([k, v, cls]) => (
          <div key={k} className="flex justify-between gap-3">
            <span className="text-muted">{k}</span>
            <span className={`q-mono ${cls ?? "text-fg"}`}>{v}</span>
          </div>
        ))}
      </div>
      {node.warnings.length > 0 && (
        <div className="mt-3 p-2 rounded bg-warn/10 border border-warn/40 text-warn space-y-1">
          {node.warnings.map((w, i) => (
            <div key={i}>⚠ {w.message}</div>
          ))}
        </div>
      )}
      <div className="mt-3 p-2 bg-bg border border-line rounded space-y-1">
        {Object.entries(node.properties)
          .filter(([k]) => !["NodeId", "EstimateRows", "EstimatedTotalSubtreeCost"].includes(k))
          .map(([k, v]) => (
            <div key={k}>
              <div className="text-muted font-semibold">{k}</div>
              <div className="q-mono text-ident break-all">{v}</div>
            </div>
          ))}
      </div>
    </div>
  );
}

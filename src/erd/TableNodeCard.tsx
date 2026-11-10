// One table in the ER diagram: schema.name, key markers, NOT NULL, types; one handle per key column so
// FK edges run column to column, and both ends light up while an edge is hovered.
import { memo } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import type { ErdNode } from "./erdLayout";
import { HEADER_H, ROW_H } from "./erdLayout";

export type TableNodeData = {
  table: ErdNode;
  /** Lowercase column names to highlight (hovered edge). */
  highlight: Set<string>;
  selected: boolean;
  matched: boolean;
  dimmed: boolean;
  rowCount?: { rows: number; estimated: boolean; analyzedAt?: number | null };
  onExpand?: (id: number) => void;
};

export const handleId = (side: "in" | "out", column: string) => `${side}:${column.toLowerCase()}`;
export const HEADER_HANDLE = "hdr";

/** Estimated counts say where they come from, and when the statistics were gathered if the engine says (Oracle LAST_ANALYZED). */
export function rowCountTitle(c: { estimated: boolean; analyzedAt?: number | null }): string {
  if (!c.estimated) return "Row count";
  const base = "Estimated row count (from the engine's statistics";
  return c.analyzedAt != null ? `${base}, gathered ${new Date(c.analyzedAt).toLocaleString()})` : `${base})`;
}

export const TableNodeCard = memo(function TableNodeCard({ data }: NodeProps<Node<TableNodeData>>) {
  const t = data.table;
  const qualified = `${t.schema}.${t.name}`;
  return (
    <div
      className={`rounded-md border shadow-lg bg-panel text-[11px] ${data.selected ? "border-accent ring-2 ring-accent/60" : data.matched ? "border-warn ring-2 ring-warn/50" : "border-line"} ${data.dimmed ? "opacity-40" : ""}`}
      style={{ width: t.width }}
    >
      <Handle id={HEADER_HANDLE} type="target" position={Position.Left} style={{ top: HEADER_H / 2, opacity: 0 }} />
      <Handle id={`${HEADER_HANDLE}-out`} type="source" position={Position.Right} style={{ top: HEADER_H / 2, opacity: 0 }} />
      <div className="flex items-center gap-1.5 px-2.5 rounded-t-md bg-panel-2 border-b border-line" style={{ height: HEADER_H }} title={qualified}>
        <span className="text-obj-table">⊞</span>
        <span className="font-bold text-fg truncate flex-1">{qualified}</span>
        {data.rowCount && (
          <span className="text-[10px] text-muted shrink-0" title={rowCountTitle(data.rowCount)}>
            {data.rowCount.estimated ? "~" : ""}{data.rowCount.rows.toLocaleString()}
          </span>
        )}
        <span className="text-[10px] text-muted shrink-0" title={`${t.columnCount} columns`}>{t.columnCount}c</span>
        {t.hidden > 0 && data.onExpand && (
          <button
            className="nodrag shrink-0 px-1 rounded bg-accent/10 text-accent hover:bg-accent/15 font-bold"
            title={`Show ${t.hidden} more related table${t.hidden === 1 ? "" : "s"}`}
            onClick={(e) => {
              e.stopPropagation();
              data.onExpand!(t.id);
            }}
          >
            +{t.hidden}
          </button>
        )}
      </div>
      {t.columns.map((c) => {
        const lit = data.highlight.has(c.name.toLowerCase());
        return (
          <div key={c.name} className={`relative flex items-center gap-1.5 px-2.5 ${lit ? "bg-accent/15 q-hl" : ""}`} style={{ height: ROW_H }}>
            {(c.pk || c.fk) && <Handle id={handleId("in", c.name)} type="target" position={Position.Left} style={{ opacity: 0 }} />}
            {c.fk && <Handle id={handleId("out", c.name)} type="source" position={Position.Right} style={{ opacity: 0 }} />}
            <span className={`w-4 text-[9px] font-bold ${c.pk ? "text-warn" : "text-accent"}`}>{c.pk ? "PK" : c.fk ? "FK" : ""}</span>
            <span className={`q-mono truncate flex-1 ${c.notNull ? "text-fg font-semibold" : "text-fg/90"}`} title={c.notNull ? "NOT NULL" : "NULL allowed"}>{c.name}</span>
            <span className="q-mono text-[10px] text-muted truncate max-w-[45%]">{c.type}</span>
          </div>
        );
      })}
      {t.columns.length === 0 && <div className="px-2.5 text-muted italic" style={{ height: ROW_H, lineHeight: `${ROW_H}px` }}>no key columns</div>}
    </div>
  );
});

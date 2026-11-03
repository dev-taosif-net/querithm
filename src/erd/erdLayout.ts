// ER diagrams: graphs built from the in-memory schema snapshot (no server roundtrip), the subgraph each
// mode shows, the JOIN generator, and the dagre layout. Pure functions; ErdTab renders, erdExport draws SVG.
import { Graph, layout } from "@dagrejs/dagre";
import type { Dialect, FkColumnPair, SchemaSnapshot, SnapFk, SnapObject } from "../ipc/contract";
import { reservedWords } from "../editor/completion/keywords";
import { quoteIdentifier } from "../editor/completion/quoteIdentifier";
import { DIALECTS } from "../engines";
export { statementTables } from "./erdScope";

export interface ErdColumn {
  name: string;
  type: string;
  pk: boolean;
  fk: boolean;
  notNull: boolean;
}

export interface ErdNode {
  id: number;
  schema: string;
  name: string;
  columns: ErdColumn[];
  /** All columns of the table (compact mode shows only keys). */
  columnCount: number;
  /** FK neighbours not in the diagram (the "+" badge expands them). */
  hidden: number;
  width: number;
  height: number;
}

export interface ErdEdge {
  id: string;
  /** The table holding the foreign key. */
  from: number;
  /** The referenced table. */
  to: number;
  name: string;
  pairs: FkColumnPair[];
  self: boolean;
  /** Any FK column allows NULL (an optional relationship). */
  optional: boolean;
}

export interface ErdGraph {
  nodes: ErdNode[];
  edges: ErdEdge[];
}

export const HEADER_H = 30;
export const ROW_H = 20;
const CHAR_W = 7;
const MIN_W = 170;
const MAX_W = 360;

/** Tables only: foreign keys connect tables. */
export function tablesOf(snap: SchemaSnapshot): Map<number, SnapObject> {
  const m = new Map<number, SnapObject>();
  for (const o of snap.objects) if (o.kind === "table") m.set(o.id, o);
  return m;
}

/** Undirected FK adjacency between tables. */
export function adjacency(snap: SchemaSnapshot): Map<number, Set<number>> {
  const adj = new Map<number, Set<number>>();
  const add = (a: number, b: number) => {
    if (!adj.has(a)) adj.set(a, new Set());
    adj.get(a)!.add(b);
  };
  for (const fk of snap.fks) {
    add(fk.parent, fk.referenced);
    add(fk.referenced, fk.parent);
  }
  return adj;
}

/** Tables within `hops` FK steps of the roots ("all" = everything connected to them). */
export function neighbourhood(snap: SchemaSnapshot, roots: number[], hops: number | "all"): Set<number> {
  const adj = adjacency(snap);
  const tables = tablesOf(snap);
  const seen = new Set(roots.filter((r) => tables.has(r)));
  let frontier = [...seen];
  for (let d = 0; frontier.length && (hops === "all" || d < hops); d++) {
    const next: number[] = [];
    for (const id of frontier)
      for (const n of adj.get(id) ?? [])
        if (!seen.has(n) && tables.has(n)) {
          seen.add(n);
          next.push(n);
        }
    frontier = next;
  }
  return seen;
}

/** Every table of a schema (null = all schemas). */
export function schemaScope(snap: SchemaSnapshot, schema: string | null): Set<number> {
  const out = new Set<number>();
  for (const [id, o] of tablesOf(snap)) if (schema == null || o.schema.toLowerCase() === schema.toLowerCase()) out.add(id);
  return out;
}

/** Tables whose name or a column name contains `text` (case-insensitive), for highlighting. */
export function matching(snap: SchemaSnapshot, ids: Iterable<number>, text: string): Set<number> {
  const needle = text.trim().toLowerCase();
  const out = new Set<number>();
  if (!needle) return out;
  const tables = tablesOf(snap);
  for (const id of ids) {
    const o = tables.get(id);
    if (!o) continue;
    if (`${o.schema}.${o.name}`.toLowerCase().includes(needle) || o.columns?.some((c) => c.name.toLowerCase().includes(needle))) out.add(id);
  }
  return out;
}

function fkColumns(snap: SchemaSnapshot): Map<number, Set<string>> {
  const m = new Map<number, Set<string>>();
  for (const fk of snap.fks) {
    if (!m.has(fk.parent)) m.set(fk.parent, new Set());
    for (const p of fk.columns) m.get(fk.parent)!.add(p.parent.toLowerCase());
  }
  return m;
}

export function nodeSize(name: string, columns: ErdColumn[]): { width: number; height: number } {
  const longest = Math.max(name.length + 4, ...columns.map((c) => c.name.length + c.type.length + 6));
  return { width: Math.min(MAX_W, Math.max(MIN_W, longest * CHAR_W + 24)), height: HEADER_H + Math.max(1, columns.length) * ROW_H + 6 };
}

/** The diagram for a set of tables: cards (all columns, or only key columns when compact) and FK edges between them. */
export function buildGraph(snap: SchemaSnapshot, ids: Set<number>, compact: boolean): ErdGraph {
  const tables = tablesOf(snap);
  const adj = adjacency(snap);
  const fkCols = fkColumns(snap);
  const nodes: ErdNode[] = [];
  for (const id of ids) {
    const o = tables.get(id);
    if (!o) continue;
    const fks = fkCols.get(id) ?? new Set<string>();
    const all: ErdColumn[] = (o.columns ?? []).map((c) => ({ name: c.name, type: c.type, pk: c.pk, fk: fks.has(c.name.toLowerCase()), notNull: !c.nullable }));
    const columns = compact ? all.filter((c) => c.pk || c.fk) : all;
    const hidden = [...(adj.get(id) ?? [])].filter((n) => !ids.has(n) && tables.has(n)).length;
    nodes.push({ id, schema: o.schema, name: o.name, columns, columnCount: all.length, hidden, ...nodeSize(`${o.schema}.${o.name}`, columns) });
  }
  nodes.sort((a, b) => `${a.schema}.${a.name}`.localeCompare(`${b.schema}.${b.name}`));
  const nullable = new Map<string, boolean>();
  for (const o of tables.values()) for (const c of o.columns ?? []) nullable.set(`${o.id}|${c.name.toLowerCase()}`, c.nullable);
  const edges: ErdEdge[] = snap.fks
    .filter((fk) => ids.has(fk.parent) && ids.has(fk.referenced))
    .map((fk) => ({
      id: `${fk.parent}:${fk.name}`, from: fk.parent, to: fk.referenced, name: fk.name, pairs: fk.columns, self: fk.parent === fk.referenced,
      optional: fk.columns.some((p) => nullable.get(`${fk.parent}|${p.parent.toLowerCase()}`) === true),
    }));
  return { nodes, edges };
}

export interface Positioned {
  id: number;
  x: number;
  y: number;
}

/** Left-to-right dagre layout: referenced tables to the right of the tables that point at them. */
export function layoutGraph(graph: { nodes: { id: number; width: number; height: number }[]; edges: { from: number; to: number; self?: boolean }[] }): Positioned[] {
  const g = new Graph({ multigraph: true });
  g.setGraph({ rankdir: "LR", nodesep: 30, ranksep: 90, marginx: 20, marginy: 20 });
  g.setDefaultEdgeLabel(() => ({}));
  for (const n of graph.nodes) g.setNode(String(n.id), { width: n.width, height: n.height });
  graph.edges.forEach((e, i) => {
    if (!e.self) g.setEdge(String(e.from), String(e.to), {}, `e${i}`);
  });
  layout(g);
  return graph.nodes.map((n) => {
    const p = g.node(String(n.id));
    return { id: n.id, x: (p?.x ?? 0) - n.width / 2, y: (p?.y ?? 0) - n.height / 2 };
  });
}

// ---------------------------------------------------------------------------------------------- JOIN generator

export type JoinResult = { sql: string; tables: number[] } | { error: string };

interface Step {
  table: number;
  via?: { fk: SnapFk; existing: number };
}

/**
 * Connects the selected tables through foreign keys with the shortest paths (fewest joins, then fewest nullable FK
 * columns), growing one tree from the first table. Tables on a path but not selected are joined too.
 */
export function joinPlan(snap: SchemaSnapshot, selected: number[]): { steps: Step[] } | { error: string } {
  const tables = tablesOf(snap);
  const picked = [...new Set(selected)].filter((id) => tables.has(id));
  if (picked.length < 2) return { error: "Select at least two tables." };
  const nullable = new Map<string, boolean>();
  for (const o of tables.values()) for (const c of o.columns ?? []) nullable.set(`${o.id}|${c.name.toLowerCase()}`, c.nullable);
  const edgesOf = new Map<number, { to: number; fk: SnapFk; w: number }[]>();
  for (const fk of snap.fks) {
    if (fk.parent === fk.referenced || !tables.has(fk.parent) || !tables.has(fk.referenced)) continue;
    const w = 1 + 0.001 * fk.columns.filter((p) => nullable.get(`${fk.parent}|${p.parent.toLowerCase()}`)).length;
    (edgesOf.get(fk.parent) ?? edgesOf.set(fk.parent, []).get(fk.parent)!).push({ to: fk.referenced, fk, w });
    (edgesOf.get(fk.referenced) ?? edgesOf.set(fk.referenced, []).get(fk.referenced)!).push({ to: fk.parent, fk, w });
  }
  const inTree = new Set([picked[0]]);
  const steps: Step[] = [{ table: picked[0] }];
  for (const target of picked.slice(1)) {
    if (inTree.has(target)) continue;
    // Dijkstra from the whole tree to the target.
    const dist = new Map<number, number>();
    const prev = new Map<number, { from: number; fk: SnapFk }>();
    const queue: [number, number][] = [];
    for (const t of inTree) {
      dist.set(t, 0);
      queue.push([0, t]);
    }
    while (queue.length) {
      queue.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
      const [d, u] = queue.shift()!;
      if (d > (dist.get(u) ?? Infinity)) continue;
      if (u === target) break;
      for (const e of edgesOf.get(u) ?? []) {
        const nd = d + e.w;
        if (nd < (dist.get(e.to) ?? Infinity)) {
          dist.set(e.to, nd);
          prev.set(e.to, { from: u, fk: e.fk });
          queue.push([nd, e.to]);
        }
      }
    }
    if (!dist.has(target)) {
      const a = tables.get(picked[0])!;
      const b = tables.get(target)!;
      return { error: `${a.schema}.${a.name} and ${b.schema}.${b.name} are not connected by foreign keys, so no JOIN can be generated (a cross join is never guessed).` };
    }
    const path: Step[] = [];
    for (let v = target; !inTree.has(v); ) {
      const p = prev.get(v)!;
      path.unshift({ table: v, via: { fk: p.fk, existing: p.from } });
      v = p.from;
    }
    for (const s of path) {
      inTree.add(s.table);
      steps.push(s);
    }
  }
  return { steps };
}

/** Quotes an identifier only when the dialect needs it (the one per-dialect quoting table, engines.ts). */
export function quoteIdent(name: string, dialect: Dialect): string {
  return quoteIdentifier(name, dialect);
}

/** Short aliases from the table name (Orders → o, OrderLines → ol, order_lines → ol), unique and never a keyword. */
export function aliasFor(name: string, taken: Set<string>, dialect: Dialect): string {
  const words = name
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean);
  let base = words.map((w) => w[0]).join("").toLowerCase() || "t";
  if (!/^[a-z]/.test(base)) base = `t${base}`;
  const reserved = reservedWords(dialect);
  let alias = base;
  for (let i = 2; taken.has(alias) || reserved.has(alias.toUpperCase()); i++) alias = `${base}${i}`;
  taken.add(alias);
  return alias;
}

export function joinSql(snap: SchemaSnapshot, selected: number[], dialect: Dialect): JoinResult {
  const plan = joinPlan(snap, selected);
  if ("error" in plan) return plan;
  const tables = tablesOf(snap);
  const defaultSchema = DIALECTS[dialect].defaultSchema;
  const taken = new Set<string>();
  const alias = new Map<number, string>();
  const name = (o: SnapObject) =>
    o.schema.toLowerCase() === defaultSchema ? quoteIdent(o.name, dialect) : `${quoteIdent(o.schema, dialect)}.${quoteIdent(o.name, dialect)}`;
  for (const s of plan.steps) alias.set(s.table, aliasFor(tables.get(s.table)!.name, taken, dialect));
  const picked = new Set(selected);
  const select = plan.steps.filter((s) => picked.has(s.table)).map((s) => `${alias.get(s.table)}.*`);
  const lines = [`SELECT ${select.join(", ")}`, `FROM ${name(tables.get(plan.steps[0].table)!)} ${alias.get(plan.steps[0].table)}`];
  for (const s of plan.steps.slice(1)) {
    const fk = s.via!.fk;
    const child = alias.get(fk.parent)!;
    const parent = alias.get(fk.referenced)!;
    const on = fk.columns.map((p) => `${child}.${quoteIdent(p.parent, dialect)} = ${parent}.${quoteIdent(p.referenced, dialect)}`).join(" AND ");
    lines.push(`JOIN ${name(tables.get(s.table)!)} ${alias.get(s.table)} ON ${on}`);
  }
  return { sql: lines.join("\n") + ";", tables: plan.steps.map((s) => s.table) };
}

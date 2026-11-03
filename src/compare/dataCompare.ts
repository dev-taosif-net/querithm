// Data compare: the pure parts of the tab — sides, the diff grid's cell tones, the kind filter.
import type { DataCompareSide } from "../ipc/contract";
import type { CellTone } from "../results/ResultGrid";

export interface DataSideState {
  profileId?: string;
  database?: string;
  /** A session-owned engine's query tab session the side reads through (Phase D0d, ownSessionFor). */
  sessionId?: string;
  mode: "table" | "query";
  /** "schema.name" from the cached snapshot. */
  table?: string;
  query?: string;
}

export interface DataCompareTabParams {
  source?: DataSideState;
  target?: DataSideState;
}

export type DiffFilter = "all" | "different" | "sourceOnly" | "targetOnly";

export const DIFF_FILTERS: { id: DiffFilter; label: string; text: string }[] = [
  { id: "all", label: "All", text: "" },
  { id: "different", label: "Different", text: "≠" },
  { id: "sourceOnly", label: "Source only", text: "+ source" },
  { id: "targetOnly", label: "Target only", text: "− target" },
];

export function dataSideReady(s: DataSideState | undefined): boolean {
  if (!s?.profileId || !s.database) return false;
  return s.mode === "table" ? !!s.table : !!s.query?.trim();
}

/** The side as dataCompare.start takes it; the table is split at its first dot (schema.name as the snapshot lists it). */
export function sideToParams(s: DataSideState, sessionId: string): DataCompareSide {
  if (s.mode === "query") return { sessionId, database: s.database ?? null, query: s.query?.trim() ?? "" };
  const t = s.table ?? "";
  const dot = t.indexOf(".");
  return { sessionId, database: s.database ?? null, schema: dot > 0 ? t.slice(0, dot) : null, table: dot > 0 ? t.slice(dot + 1) : t };
}

/** "id, part" → ["id", "part"]; empty → null (the sidecar uses the primary key). */
export function parseKey(text: string): string[] | null {
  const k = text.split(",").map((x) => x.trim()).filter(Boolean);
  return k.length ? k : null;
}

/** Diff kinds as the sidecar writes them in _diff_kind. */
export const DIFF_DIFFERENT = 1;
export const DIFF_SOURCE_ONLY = 2;
export const DIFF_TARGET_ONLY = 3;

/**
 * A cell's tone from its row's kind and mask and its column's metadata: changed pairs light up on a different row; on
 * a row only one side has, that side is green (source) or red (target) and the other side's empty cells are dimmed.
 */
export function diffTone(kind: number, mask: Uint8Array | null, meta: { bit: number | null; side: "source" | "target" | null; isKind: boolean }): CellTone {
  if (kind === DIFF_SOURCE_ONLY) return meta.side === "target" ? "muted" : "added";
  if (kind === DIFF_TARGET_ONLY) return meta.side === "source" ? "muted" : "removed";
  if (meta.isKind) return "changed";
  if (meta.bit == null || !mask) return null;
  return (mask[meta.bit >> 3] & (1 << (meta.bit & 7))) !== 0 ? "changed" : null;
}

export function count(n: number) {
  return n.toLocaleString("en-US");
}

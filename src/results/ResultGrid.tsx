// Glide Data Grid over Arrow vectors (§7 Grid wiring). The grid owns its canvas; React does not re-render on scroll.
// When the result set is editable the same grid stages edits: values come from the staging overlay first.
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type MutableRefObject } from "react";
import { createPortal } from "react-dom";
import {
  CompactSelection, DataEditor, GridCellKind, drawTextCell, type DataEditorRef, type DrawCellCallback, type DrawHeaderCallback, type EditableGridCell, type GridCell, type GridColumn, type GridKeyEventArgs,
  type GridMouseEventArgs, type GridSelection, type Item, type SpriteMap, type Theme,
} from "@glideapps/glide-data-grid";
import "@glideapps/glide-data-grid/dist/index.css";
import { StorageClass, type ArrowResultSet, type ColumnModel } from "./model";
import type { Cell, GridStaging, RowRef } from "./staging";
import { rpc } from "../ipc/client";
import { useSettings } from "../store/settings";
import { useUi } from "../store/ui";
import { selectionStats, type SelectionStats } from "./selectionStats";
export { formatStatNumber, type SelectionStats } from "./selectionStats";
import { clampLeft } from "./tipPosition";
import { DEFAULT_ENGINE, dialectOf, DIALECTS } from "../engines";
import type { Engine } from "../ipc/contract";

function cssVar(name: string) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** A theme color at `alpha` (canvas cannot resolve CSS variables or color-mix). */
export function tint(color: string, alpha: number): string {
  const c = color.trim();
  let r = 0, g = 0, b = 0;
  if (/^#[0-9a-f]{3,4}$/i.test(c)) [r, g, b] = [1, 2, 3].map((i) => parseInt(c[i] + c[i], 16));
  else if (/^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(c)) [r, g, b] = [1, 3, 5].map((i) => parseInt(c.slice(i, i + 2), 16));
  else {
    const m = /rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)/i.exec(c);
    if (!m) return c;
    [r, g, b] = [m[1], m[2], m[3]].map(Number);
  }
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

export function gridTheme(): Partial<Theme> {
  return {
    accentColor: cssVar("--q-accent"),
    accentFg: "#ffffff",
    accentLight: cssVar("--q-sel"),
    textDark: cssVar("--q-fg"),
    textMedium: cssVar("--q-muted"),
    textLight: cssVar("--q-faint"),
    textHeader: cssVar("--q-muted"),
    textHeaderSelected: "#ffffff",
    textBubble: cssVar("--q-fg"),
    bgIconHeader: cssVar("--q-muted"),
    fgIconHeader: cssVar("--q-panel"),
    bgCell: cssVar("--q-bg"),
    bgCellMedium: cssVar("--q-panel-2"),
    bgHeader: cssVar("--q-panel"),
    bgHeaderHasFocus: cssVar("--q-raised"),
    bgHeaderHovered: cssVar("--q-hover"),
    bgBubble: cssVar("--q-raised"),
    bgBubbleSelected: cssVar("--q-hover"),
    bgSearchResult: "rgba(245, 158, 11, 0.25)",
    borderColor: cssVar("--q-line"),
    horizontalBorderColor: tint(cssVar("--q-line"), 0.55),
    drilldownBorder: cssVar("--q-line"),
    linkColor: cssVar("--q-accent"),
    headerFontStyle: `600 ${11 * GRID_SCALE}px`,
    baseFontStyle: `${12 * GRID_SCALE}px`,
    markerFontStyle: `${11 * GRID_SCALE}px`,
    fontFamily: '"JetBrains Mono Variable", "JetBrains Mono", "Cascadia Code", Menlo, monospace',
    editorFontSize: `${12 * GRID_SCALE}px`,
    cellHorizontalPadding: 8 * GRID_SCALE,
    cellVerticalPadding: 3,
    lineHeight: 1.4,
  };
}

// Header markers, drawn in fixed theme colors (the grid's icon fg/bg would make them all look alike).
function headerIcons(): SpriteMap {
  const pk = cssVar("--q-warn");
  const fk = cssVar("--q-accent");
  const nul = cssVar("--q-null");
  const svg = (body: string) => `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;
  return {
    qPk: () => svg(`<g stroke="${pk}"><circle cx="7.5" cy="15.5" r="4.5"/><path d="m21 2-9.6 9.6"/><path d="m15.5 7.5 3 3L22 7l-3-3"/></g>`),
    qFk: () => svg(`<g stroke="${fk}"><path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/></g>`),
    qNull: () => svg(`<g stroke="${nul}"><circle cx="12" cy="12" r="7.5"/><path d="M6.5 17.5 17.5 6.5"/></g>`),
    qNullDot: () => svg(`<circle cx="18" cy="18" r="4.5" fill="${nul}" stroke="none"/>`),
    qLinkBadge: () => svg(`<circle cx="18" cy="18" r="5" fill="${fk}" stroke="none"/>`),
    qEmpty: () => `<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"></svg>`,
  };
}

/** The fixed advance of the grid's monospace font, per canvas font string; measured again once the web font loads. */
const advance = new Map<string, number>();
if (typeof document !== "undefined") document.fonts?.addEventListener?.("loadingdone", () => advance.clear());
/** Latin keeps the monospace advance; other text (CJK, emoji, ⏎) is measured. A code-unit scan, not a pattern: cells hold SQL. */
function latin(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (c < 0x20 || c > 0x24f) return false;
  }
  return true;
}

/**
 * `text` cut to `width` with a trailing "…", or `text` itself when it fits: Glide 6 clips overflowing text at the cell
 * edge with no mark and has no ellipsis option. Latin text is cut by the font's fixed advance, so the scroll path
 * measures nothing; other text is measured (a binary search, only for cells that may overflow).
 */
export function fitText(ctx: Pick<CanvasRenderingContext2D, "font" | "measureText">, text: string, width: number, font = ctx.font): string {
  // `font` is the theme's font string, which Glide has set on ctx: reading ctx.font back costs more than the whole fit.
  let w = advance.get(font);
  if (w === undefined) advance.set(font, (w = ctx.measureText("0").width || 7));
  if (text.length * w * 2 <= width) return text; // fits even if every glyph were double width
  if (latin(text)) {
    if (text.length * w <= width) return text;
    const n = Math.floor(width / w) - 1;
    return n > 0 ? `${text.slice(0, n)}…` : width >= w ? "…" : "";
  }
  if (ctx.measureText(text).width <= width) return text;
  let lo = 0, hi = text.length;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (ctx.measureText(`${text.slice(0, mid)}…`).width <= width) lo = mid;
    else hi = mid - 1;
  }
  const unit = text.charCodeAt(lo - 1);
  if (unit >= 0xd800 && unit <= 0xdbff) lo--; // never split a surrogate pair
  return lo > 0 ? `${text.slice(0, lo)}…` : width >= w ? "…" : "";
}

/** Text cells that overflow are drawn cut with "…" (Glide's public drawTextCell, the cell's own theme and alignment). */
export const drawFitted: DrawCellCallback = (args, drawContent) => {
  const { cell, ctx, rect, theme } = args;
  if (cell?.kind !== GridCellKind.Text || cell.allowWrapping) return drawContent();
  // Glide passes its realized theme, whose baseFontFull is the font it set on ctx (the public Theme type omits it).
  const font = (theme as Theme & { baseFontFull?: string }).baseFontFull;
  const fitted = fitText(ctx, cell.displayData, rect.width - 2 * theme.cellHorizontalPadding, font);
  if (fitted === cell.displayData) drawContent();
  // Glide passes its full draw args (cellFillColor, spriteManager…); DrawCellCallback's type lists fewer. drawTextCell reads ctx, rect, theme.
  else drawTextCell(args as Parameters<typeof drawTextCell>[0], fitted, cell.contentAlign);
};

function markers(c: ColumnModel): { icon?: string; overlayIcon?: string } {
  if (c.pk) return { icon: "qPk", overlayIcon: c.fk ? "qLinkBadge" : undefined };
  if (c.fk) return { icon: "qFk", overlayIcon: c.nullable ? "qNullDot" : undefined };
  return c.nullable ? { icon: "qNull" } : {};
}

function describe(c: ColumnModel, staging?: GridStaging): string {
  const parts = [c.typeName];
  if (c.pk) parts.push("Primary key");
  if (c.fk) parts.push(`Foreign key → ${c.fk}`);
  parts.push(c.nullable === true ? "NULL allowed" : c.nullable === false ? "NOT NULL" : "Nullability unknown");
  if (c.source) parts.push(`From ${c.source}`);
  const ec = staging?.column(c.ordinal);
  if (staging) parts.push(ec && !ec.locked ? "Editable" : `Read-only: ${ec?.lockReason ?? "not a table column"}`);
  return parts.join(" · ");
}

/** Compact starting width: header text plus enough room for a typical value of the type; the user can widen any column. */
/** Uniform size factor for the grid (fonts, row/header heights, padding, default widths). */
export const GRID_SCALE = 0.93;

function defaultWidth(name: string, typeName: string): number {
  const base = Math.max(name.length * 7 + 12, 40);
  const t = typeName.toLowerCase();
  if (t === "date") return Math.max(base, 84);
  if (t.includes("offset") || t.includes("tz")) return Math.max(base, 200);
  if (t.includes("date") || t.includes("timestamp")) return Math.max(base, 160);
  if (t.includes("time")) return Math.max(base, 90);
  if (t.includes("unique") || t === "uuid") return Math.max(base, 260);
  if (t.includes("char") || t.includes("text") || t === "xml") return Math.min(Math.max(base, 110), 240);
  return Math.min(Math.max(base, 56), 200);
}

/** An integer or real cell of a dynamically typed column (SQLite): right-aligned and copied unquoted, like a number column's. */
const isNumberClass = (cls: StorageClass | null) => cls === StorageClass.Integer || cls === StorageClass.Real;

/** Values edited in the cell viewer instead of the inline editor. */
function needsViewer(c: ColumnModel, text: string | null) {
  return c.typeName === "xml" || /json/.test(c.typeName) || (text != null && (text.includes("\n") || text.length > 2000));
}

export type CopyFormat = "plain" | "headers" | "in" | "markdown";

export interface ResultGridHandle {
  copySelection(format?: CopyFormat | boolean): string | null;
  /** Grid rows (with their row refs) covered by the selection. */
  selectedRefs(): RowRef[];
}

/** A cell's background in a read-only grid (data compare): changed / only in the source / only in the target / dimmed. */
export type CellTone = "changed" | "added" | "removed" | "muted" | null;

export function ResultGrid({
  rs, executionId, handleRef, staging, onStaged, onApply, tone, onStatsChange, onViewCell, engine = DEFAULT_ENGINE,
}: {
  engine?: Engine;
  rs: ArrowResultSet; executionId: string; handleRef?: MutableRefObject<ResultGridHandle | null>; staging?: GridStaging;
  /** Read-only grids only: a background per cell (view row, column index). */
  tone?: (row: number, col: number) => CellTone;
  /** Called after edits to existing rows (the panel reads their version tokens). */
  onStaged?: () => void;
  onApply?: () => void;
  onStatsChange?: (stats: SelectionStats | null) => void;
  /** Read-only grids: activating a cell (double-click, Enter) shows it in the cell viewer panel instead of the dialog. */
  onViewCell?: (viewRow: number, col: number) => void;
}) {
  const version = useSyncExternalStore(rs.subscribe, rs.getVersion);
  const stagingVersion = useSyncExternalStore(staging?.subscribe ?? noSubscribe, staging?.getVersion ?? zero);
  const [widths, setWidths] = useState<Record<string, number>>({});
  const [selection, setSelection] = useState<GridSelection>({ columns: CompactSelection.empty(), rows: CompactSelection.empty() });
  const ref = useRef<DataEditorRef>(null);
  const settingTheme = useSettings((s) => s.theme);
  const themeKey = `${settingTheme}|${document.documentElement.dataset.theme}`;
  const theme = useMemo(gridTheme, [themeKey]);
  // "(default)", rows marked for deletion and unchanged compare cells are still read: the muted tier, not the faint one.
  const muted = theme.textMedium;
  // The row-number gutter wears the header background so it reads as a heading, not as data.
  const markerTheme = useMemo(() => ({ bgCell: theme.bgHeader, bgCellMedium: theme.bgHeader, textLight: theme.textHeader }), [theme]);
  // NULL stands out from an empty string or the text "NULL" (SSMS shows it highlighted too).
  const nullColor = useMemo(() => cssVar("--q-null"), [themeKey]);
  const colors = useMemo(() => {
    const warn = cssVar("--q-warn"), ok = cssVar("--q-ok"), err = cssVar("--q-err");
    return { warn, warnBg: tint(warn, 0.16), okBg: tint(ok, 0.14), errBg: tint(err, 0.14), err, errStrong: tint(err, 0.3) };
  }, [themeKey]);
  const icons = useMemo(headerIcons, [themeKey]);
  const [tip, setTip] = useState<{ text: string; x: number; y: number } | null>(null);
  // Sorting or filtering moves rows under the selection: drop it rather than copy rows the user did not pick.
  useEffect(() => setSelection({ columns: CompactSelection.empty(), rows: CompactSelection.empty() }), [rs.viewVersion]);

  const committed = staging?.committed.length ?? 0;
  const insertedCount = staging?.inserted.length ?? 0;
  const totalRows = rs.visibleRows + committed + insertedCount;

  const refAt = useCallback(
    (row: number): RowRef => {
      if (row < rs.visibleRows) return { kind: "existing", row: rs.physicalRow(row) };
      if (row < rs.visibleRows + committed) return { kind: "existing", row: rs.rowCount + (row - rs.visibleRows) };
      return { kind: "inserted", index: row - rs.visibleRows - committed };
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [rs, version, committed],
  );

  const columns: GridColumn[] = useMemo(
    () =>
      rs.columns.map((c, i) => ({
        id: String(i),
        title: c.name,
        width: widths[String(i)] ?? Math.round((defaultWidth(c.name, c.typeName) + (markers(c).icon ? 20 : 0) + 8) * GRID_SCALE),
        hasMenu: true,
        menuIcon: "qEmpty",
        ...markers(c),
      })),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [rs.columns, widths, rs.sort?.col, rs.sort?.dir],
  );

  /** One cell of an editable result set. */
  const stagedCell = useCallback(
    (col: number, row: number): GridCell => {
      const c = rs.columns[col];
      const ref = refAt(row);
      const cell = staging!.cell(ref, c.ordinal);
      const lock = rs.complete ? staging!.lockReason(ref, c.ordinal) : "Rows are still loading";
      const err = staging!.errorFor(ref, c.ordinal);
      const deleted = ref.kind === "existing" && (staging!.isDeleted(ref.row) || staging!.appliedDeleted.has(ref.row));
      const text = cell.isDefault ? null : cell.value;
      const display = cell.isDefault ? "(default)" : cell.unknown ? "…" : text == null ? "NULL" : text.length > 300 ? `${text.slice(0, 300)}…` : text.replace(/\r?\n/g, " ⏎ ");
      const truncated = ref.kind === "existing" && ref.row < rs.rowCount && !cell.staged && c.flagField != null && rs.isTruncatedPhysical(ref.row, col);
      const numberClass = c.classField != null && ref.kind === "existing" && ref.row < rs.rowCount && !cell.staged && isNumberClass(rs.storageClassPhysical(ref.row, col));
      const override: Partial<Theme> = {};
      if (err) override.bgCell = colors.errStrong;
      else if (deleted) override.bgCell = colors.errBg;
      else if (ref.kind === "inserted") override.bgCell = colors.okBg;
      else if (cell.staged) override.bgCell = colors.warnBg;
      if (text == null || cell.unknown) override.textDark = cell.isDefault || cell.unknown ? muted : nullColor;
      else if (deleted) override.textDark = muted;
      return {
        kind: GridCellKind.Text,
        data: text ?? "",
        displayData: truncated ? `${display} ⋯` : display,
        allowOverlay: lock != null ? text != null : !needsViewer(c, text) && !truncated,
        readonly: lock != null,
        contentAlign: c.kind === "number" || numberClass ? "right" : undefined,
        copyData: text ?? "NULL",
        themeOverride: Object.keys(override).length ? override : undefined,
      };
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [rs, staging, refAt, colors, muted, nullColor, stagingVersion],
  );

  const getCellContent = useCallback(
    ([col, row]: Item): GridCell => {
      if (staging) return stagedCell(col, row);
      const c = rs.columns[col];
      const loaded = row < rs.visibleRows;
      const text = loaded ? rs.text(row, col) : null;
      const t = loaded && tone ? tone(row, col) : null;
      const bg = t === "changed" ? colors.warnBg : t === "added" ? colors.okBg : t === "removed" ? colors.errBg : undefined;
      if (text === null) {
        return {
          kind: GridCellKind.Text, data: "", displayData: t === "muted" ? "" : "NULL", allowOverlay: false, readonly: true,
          themeOverride: { textDark: loaded ? nullColor : muted, ...(bg ? { bgCell: bg } : {}) }, copyData: "NULL",
        };
      }
      const truncated = c.flagField != null && rs.isTruncated(row, col);
      const display = text.length > 300 ? `${text.slice(0, 300)}…` : text;
      return {
        kind: GridCellKind.Text,
        data: text,
        displayData: truncated ? `${display} ⋯` : display.replace(/\r?\n/g, " ⏎ "),
        allowOverlay: true,
        readonly: true,
        contentAlign: c.kind === "number" || (c.classField != null && isNumberClass(rs.storageClass(row, col))) ? "right" : undefined,
        copyData: text,
        themeOverride: bg ? { bgCell: bg } : t === "muted" ? { textDark: muted } : undefined,
      };
    },
    // version re-binds the callback so Glide re-reads cells after batches arrive.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [rs, version, muted, nullColor, staging, stagedCell, tone, colors],
  );

  /** Left accent bar on staged cells and a strike line through deleted rows (Glide themes have neither). */
  const drawCell: DrawCellCallback = useCallback(
    (args, drawContent) => {
      drawFitted(args, drawContent);
      if (!staging) return;
      const { ctx, rect, col, row } = args;
      const ref = refAt(row);
      const ordinal = rs.columns[col]?.ordinal;
      if (ordinal == null) return;
      if (ref.kind === "existing" && (staging.isDeleted(ref.row) || staging.appliedDeleted.has(ref.row))) {
        ctx.strokeStyle = colors.err;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(rect.x + 4, rect.y + rect.height / 2);
        ctx.lineTo(rect.x + rect.width - 4, rect.y + rect.height / 2);
        ctx.stroke();
      } else if (ref.kind === "existing" && staging.edits.get(ref.row)?.has(ordinal)) {
        ctx.fillStyle = colors.warn;
        ctx.fillRect(rect.x, rect.y + 1, 2, rect.height - 2);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [staging, refAt, rs, colors, stagingVersion],
  );

  const drawHeader: DrawHeaderCallback = useCallback(
    (args, drawContent) => {
      drawContent();
      const { ctx, columnIndex, menuBounds, isHovered, isSelected, theme: colTheme } = args;
      const isSorted = rs.sort?.col === columnIndex;
      if (isSorted) {
        const cx = menuBounds.x + menuBounds.width / 2;
        const cy = menuBounds.y + menuBounds.height / 2;
        ctx.save();
        ctx.beginPath();
        const dir = rs.sort!.dir;
        ctx.fillStyle = isSelected ? colTheme.textHeaderSelected : colTheme.accentColor;
        if (dir === "asc") {
          ctx.moveTo(cx, cy - 4.5);
          ctx.lineTo(cx + 4.5, cy + 3.5);
          ctx.lineTo(cx - 4.5, cy + 3.5);
        } else {
          ctx.moveTo(cx, cy + 4.5);
          ctx.lineTo(cx + 4.5, cy - 3.5);
          ctx.lineTo(cx - 4.5, cy - 3.5);
        }
        ctx.closePath();
        ctx.fill();
        ctx.restore();
      } else if (isHovered) {
        const cx = menuBounds.x + menuBounds.width / 2;
        const cy = menuBounds.y + menuBounds.height / 2;
        ctx.save();
        ctx.fillStyle = isSelected ? colTheme.textHeaderSelected : colTheme.textHeader;
        ctx.globalAlpha = 0.4;
        ctx.beginPath();
        ctx.moveTo(cx, cy - 5.5);
        ctx.lineTo(cx + 3.5, cy - 1.5);
        ctx.lineTo(cx - 3.5, cy - 1.5);
        ctx.closePath();
        ctx.fill();
        ctx.beginPath();
        ctx.moveTo(cx, cy + 5.5);
        ctx.lineTo(cx + 3.5, cy + 1.5);
        ctx.lineTo(cx - 3.5, cy + 1.5);
        ctx.closePath();
        ctx.fill();
        ctx.restore();
      }
    },
    [rs.sort],
  );

  const onHeaderMenuClick = useCallback(
    (col: number) => {
      const cur = rs.sort;
      if (!cur || cur.col !== col) rs.setSort(col, "asc");
      else if (cur.dir === "asc") rs.setSort(col, "desc");
      else rs.setSort(null);
    },
    [rs],
  );

  // Glide already selects the column on mousedown, so decide from the selection as it was before the press.
  const downCols = useRef<CompactSelection | undefined>(undefined);
  const onHeaderClicked = useCallback(
    (col: number, e?: { shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean }) => {
      const before = downCols.current ?? selection.columns;
      downCols.current = undefined;
      // A plain click on the only selected column deselects it; the next click selects it again.
      let cols = before.length === 1 && before.hasIndex(col) ? CompactSelection.empty() : CompactSelection.fromSingleSelection(col);
      if (e?.ctrlKey || e?.metaKey) {
        cols = before.hasIndex(col) ? before.remove(col) : before.add(col);
      } else if (e?.shiftKey && before.length > 0) {
        const first = before.first() ?? col;
        const start = Math.min(first, col);
        const end = Math.max(first, col) + 1;
        cols = CompactSelection.empty().add([start, end]);
      }
      setSelection({
        columns: cols,
        rows: CompactSelection.empty(),
      });
    },
    [selection.columns],
  );

  const stage = useCallback(
    (changes: { ref: RowRef; ordinal: number; cell: Cell }[], fullValue = false) => {
      if (!staging || !changes.length) return;
      staging.setCells(changes, fullValue);
      if (changes.some((c) => c.ref.kind === "existing")) onStaged?.();
    },
    [staging, onStaged],
  );

  const openCell = useCallback(
    async ([col, row]: Item) => {
      const c = rs.columns[col];
      if (staging) {
        const ref = refAt(row);
        const cell = staging.cell(ref, c.ordinal);
        const editable = rs.complete && staging.lockReason(ref, c.ordinal) == null;
        const truncated = ref.kind === "existing" && ref.row < rs.rowCount && !cell.staged && c.flagField != null && rs.isTruncatedPhysical(ref.row, col);
        if (editable && !truncated && !needsViewer(c, cell.value)) return; // the inline editor handles it
        let value = cell.isDefault ? "" : cell.value ?? "";
        if (truncated) {
          const full = await rpc("query.cell", { executionId, resultIndex: rs.index, row: ref.row, col: c.ordinal }).catch(() => null);
          if (!full || full.unavailable || full.value == null || full.isBinary) {
            useUi.getState().openDialog({ kind: "cellViewer", title: `${c.name} (${c.typeName})`, value: `${value}\n\n— The full value is no longer available. Re-run the query to edit it.` });
            return;
          }
          value = full.value;
        }
        const lang = c.typeName === "xml" ? "xml" : /json/.test(c.typeName) ? "json" : undefined;
        useUi.getState().openDialog({
          kind: "cellViewer", title: `${c.name} (${c.typeName})`, value, language: lang,
          // The viewer holds the whole value, so a shortened display must not block the save (staging locks inline edits of it).
          onSave: editable || truncated ? (v) => stage([{ ref, ordinal: c.ordinal, cell: { value: v } }], truncated) : undefined,
        });
        return;
      }
      if (onViewCell) {
        onViewCell(row, col);
        return;
      }
      let value = rs.text(row, col);
      if (value == null) return;
      if (c.flagField != null && rs.isTruncated(row, col)) {
        const full = await rpc("query.cell", { executionId, resultIndex: rs.index, row: rs.physicalRow(row), col: c.ordinal }).catch(() => null);
        if (full && !full.unavailable && full.value != null) value = full.isBinary ? `0x${base64ToHex(full.value)}` : full.value;
        else value += "\n\n— The full value is no longer available (the execution was closed). Re-run the query to fetch it.";
      }
      const lang = c.typeName === "xml" ? "xml" : /json/.test(c.typeName) ? "json" : undefined;
      useUi.getState().openDialog({ kind: "cellViewer", title: `${c.name} (${c.typeName})`, value, language: lang });
    },
    [rs, executionId, staging, refAt, stage, onViewCell],
  );

  const onCellEdited = useCallback(
    ([col, row]: Item, nv: EditableGridCell) => {
      if (!staging || nv.kind !== GridCellKind.Text) return;
      stage([{ ref: refAt(row), ordinal: rs.columns[col].ordinal, cell: { value: nv.data } }]);
    },
    [staging, refAt, rs, stage],
  );

  /** Grid rows covered by the selection (range rows plus whole selected rows). */
  const selectedRows = useCallback((): number[] => {
    const rows = new Set<number>();
    if (selection.current) {
      const r = selection.current.range;
      for (let y = r.y; y < r.y + r.height; y++) rows.add(y);
    }
    for (const y of selection.rows) rows.add(y);
    return [...rows].filter((y) => y < totalRows).sort((a, b) => a - b);
  }, [selection, totalRows]);

  const selectedCells = useCallback((): Item[] => {
    const out: Item[] = [];
    if (selection.current) {
      const r = selection.current.range;
      for (let y = r.y; y < r.y + r.height; y++) for (let x = r.x; x < r.x + r.width; x++) out.push([x, y]);
    } else for (const y of selection.rows) rs.columns.forEach((_, x) => out.push([x, y]));
    return out;
  }, [selection, rs]);

  const addRow = useCallback(() => {
    if (!staging?.canInsert) return;
    staging.addRow();
    const row = rs.visibleRows + staging.committed.length + staging.inserted.length - 1;
    requestAnimationFrame(() => ref.current?.scrollTo(0, row));
  }, [staging, rs]);

  const onKeyDown = useCallback(
    (e: GridKeyEventArgs) => {
      if (!staging) return;
      const mod = e.ctrlKey || e.metaKey;
      const key = e.key.toLowerCase();
      let handled = true;
      if (mod && key === "0") stage(selectedCells().map(([x, y]) => ({ ref: refAt(y), ordinal: rs.columns[x].ordinal, cell: { value: null } })));
      else if (e.altKey && e.key === "Insert") addRow();
      else if (mod && e.key === "Delete") staging.toggleDelete(selectedRows().map(refAt));
      else if (mod && key === "z" && !e.shiftKey) staging.undo();
      else if (mod && (key === "y" || (key === "z" && e.shiftKey))) staging.redo();
      else if (mod && key === "s") onApply?.();
      else handled = false;
      if (handled) {
        e.preventDefault();
        e.cancel();
      }
    },
    [staging, stage, selectedCells, selectedRows, refAt, rs, addRow, onApply],
  );

  /** Pasting a TSV/Excel range stages edits; rows pasted past the end become new rows. */
  const onPaste = useCallback(
    (target: Item, values: readonly (readonly string[])[]): boolean => {
      if (!staging) return false;
      const [x0, y0] = target;
      const changes: { ref: RowRef; ordinal: number; cell: Cell }[] = [];
      const fresh: Map<number, Cell>[] = [];
      values.forEach((line, i) => {
        const y = y0 + i;
        const cells = new Map<number, Cell>();
        line.forEach((v, j) => {
          const c = rs.columns[x0 + j];
          if (!c) return;
          const cell: Cell = { value: v === "NULL" ? null : v };
          if (y < totalRows) changes.push({ ref: refAt(y), ordinal: c.ordinal, cell });
          else cells.set(c.ordinal, cell);
        });
        if (y >= totalRows) fresh.push(cells);
      });
      stage(changes);
      if (fresh.length && staging.canInsert) for (const cells of fresh) staging.addRow(cells);
      return false;
    },
    [staging, rs, totalRows, refAt, stage],
  );

  const textAt = useCallback(
    (row: number, col: number): string | null => {
      if (!staging) return rs.text(row, col);
      const cell = staging.cell(refAt(row), rs.columns[col].ordinal);
      return cell.isDefault ? "DEFAULT" : cell.value;
    },
    [staging, rs, refAt],
  );

  /** Storage class of a dynamically typed cell as the grid holds it; null when not dynamic or the value is staged / new. */
  const classAt = (row: number, col: number): StorageClass | null => {
    if (rs.columns[col].classField == null || row >= rs.visibleRows) return null;
    if (staging?.cell(refAt(row), rs.columns[col].ordinal).staged) return null;
    return rs.storageClass(row, col);
  };

  if (handleRef) handleRef.current = {
    copySelection(formatOrHeaders = false) {
      const mode: CopyFormat =
        typeof formatOrHeaders === "boolean"
          ? (formatOrHeaders ? "headers" : "plain")
          : formatOrHeaders;
      const sel = selection;
      const cellCount = sel.columns.length ? totalRows * sel.columns.length
        : sel.rows.length ? sel.rows.length * rs.columns.length
        : sel.current ? sel.current.range.width * sel.current.range.height : 0;
      if ((mode === "in" || mode === "markdown") && cellCount > 50_000)
        throw new Error("Copy as IN/Markdown supports up to 50,000 cells. Select fewer cells or export the result.");
      const rows: number[] = [];
      const cols: number[] = [];
      if (sel.columns.length) {
        for (let y = 0; y < totalRows; y++) rows.push(y);
        for (const x of sel.columns) if (x < rs.columns.length) cols.push(x);
      } else if (sel.rows.length) {
        for (const y of sel.rows) if (y < totalRows) rows.push(y);
        rs.columns.forEach((_, i) => cols.push(i));
      } else if (sel.current) {
        const r = sel.current.range;
        for (let y = r.y; y < r.y + r.height; y++) if (y < totalRows) rows.push(y);
        for (let x = r.x; x < r.x + r.width; x++) if (x < rs.columns.length) cols.push(x);
      } else return null;

      if (!rows.length || !cols.length) return null;

      if (mode === "in") {
        const vals: string[] = [];
        for (const r of rows) {
          for (const c of cols) {
            // A dynamically typed cell follows its own class: without column affinity SQLite holds 1 and '1' unequal.
            const cls = classAt(r, c);
            if (rs.columns[c].kind === "binary" || cls === StorageClass.Blob || (!staging && rs.isTruncated(r, c)))
              throw new Error("IN clauses cannot copy binary or truncated cells. Export or fetch the full value first.");
            const val = textAt(r, c);
            if (val === null) {
              vals.push("NULL");
            } else if ((rs.columns[c].kind === "number" || isNumberClass(cls)) && Number.isFinite(Number(val)) && val.trim() !== "") {
              vals.push(val);
            } else {
              vals.push(DIALECTS[dialectOf(engine)].stringLiteral(val));
            }
          }
        }
        return `IN (${vals.join(", ")})`;
      }

      if (mode === "markdown") {
        const header = `| ${cols.map((c) => rs.columns[c].name.replace(/\|/g, "\\|")).join(" | ")} |`;
        const div = `| ${cols.map(() => "---").join(" | ")} |`;
        const body = rows.map((r) => {
          const cells = cols.map((c) => {
            const v = textAt(r, c) ?? "NULL";
            return v.replace(/\|/g, "\\|").replace(/\r?\n/g, " ");
          });
          return `| ${cells.join(" | ")} |`;
        });
        return [header, div, ...body].join("\n");
      }

      if (!staging && mode === "plain") return rs.copyText(rows, cols, false);
      if (!staging && mode === "headers") return rs.copyText(rows, cols, true);

      const lines: string[] = [];
      if (mode === "headers") lines.push(cols.map((c) => rs.columns[c].name).join("\t"));
      for (const r of rows) lines.push(cols.map((c) => textAt(r, c) ?? "NULL").join("\t"));
      return lines.join("\n");
    },
    selectedRefs: () => selectedRows().map(refAt),
  };

  useEffect(() => {
    if (!onStatsChange) return;
    onStatsChange(selectionStats(selection, totalRows, rs.columns.map((c) => c.kind), textAt));
  }, [selection, totalRows, rs.columns.length, textAt, onStatsChange]);

  const onItemHovered = (e: GridMouseEventArgs) => {
    if (e.kind === "header") {
      const c = rs.columns[e.location[0]];
      setTip(c ? { text: describe(c, staging), x: e.bounds.x + 4, y: e.bounds.y + e.bounds.height + 4 } : null);
      return;
    }
    if (staging && e.kind === "cell") {
      const [col, row] = e.location;
      const c = rs.columns[col];
      const ref = row < totalRows ? refAt(row) : undefined;
      if (c && ref) {
        const err = staging.errorFor(ref, c.ordinal);
        const cell = staging.cell(ref, c.ordinal);
        const was = ref.kind === "existing" && cell.staged ? staging.original(ref.row, c.ordinal) : undefined;
        const text = err ?? (was !== undefined ? `Was: ${was ?? "NULL"}` : undefined);
        setTip(text ? { text, x: e.bounds.x + 4, y: e.bounds.y + e.bounds.height + 4 } : null);
        return;
      }
    }
    setTip(null);
  };

  // The grid's name for screen readers (L32): which result set and how many rows. It changes with a batch or a filter,
  // which re-render this grid anyway, never with a scroll.
  const rowsText = `${rs.visibleRows.toLocaleString("en-US")}${rs.visibleRows !== rs.rowCount ? ` of ${rs.rowCount.toLocaleString("en-US")}` : ""} ${rs.rowCount === 1 ? "row" : "rows"}${rs.complete ? "" : " so far"}`;
  return (
    <div
      role="group"
      aria-label={`Results: result set ${rs.index + 1}, ${rowsText}`}
      className="h-full w-full"
      data-q-grid-edit={staging ? "" : undefined}
      onMouseDownCapture={() => (downCols.current = selection.columns)}
      onTouchStartCapture={() => (downCols.current = selection.columns)}
    >
    <DataEditor
      ref={ref}
      columns={columns}
      rows={totalRows}
      getCellContent={getCellContent}
      getCellsForSelection={true}
      onColumnResize={(_c, w, i) => setWidths((s) => ({ ...s, [String(i)]: w }))}
      onHeaderClicked={onHeaderClicked}
      onHeaderMenuClick={onHeaderMenuClick}
      onCellActivated={openCell}
      onCellEdited={staging ? onCellEdited : undefined}
      onPaste={staging ? onPaste : undefined}
      onDelete={staging ? () => false : undefined}
      onKeyDown={staging ? onKeyDown : undefined}
      drawCell={staging ? drawCell : drawFitted}
      drawHeader={drawHeader}
      gridSelection={selection}
      onGridSelectionChange={setSelection}
      // Clicking a row number selects the whole row; Ctrl/Shift+click adds rows or a range.
      rowMarkers={{ kind: "clickable-number", width: Math.max(44, String(totalRows).length * 8 + 16), theme: markerTheme }}
      smoothScrollX={false}
      smoothScrollY={false}
      overscrollX={0}
      rowHeight={Math.round(24 * GRID_SCALE)}
      headerHeight={Math.round(28 * GRID_SCALE)}
      width="100%"
      height="100%"
      theme={theme}
      keybindings={{ search: true, copy: true, selectAll: true, paste: !!staging }}
      verticalBorder={(c) => c > 0}
      fixedShadowX={false}
      headerIcons={icons}
      onItemHovered={onItemHovered}
      onMouseMove={(e) => e.kind !== "header" && e.kind !== "cell" && tip && setTip(null)}
    />
    {tip && <HeaderTip {...tip} />}
    </div>
  );
}

const noSubscribe = () => () => {};
const zero = () => 0;

/** Portaled: dockview's `contain: layout` re-anchors fixed children to the dock (offset by the explorer and tab strip) and clips them. */
function HeaderTip({ text, x, y }: { text: string; x: number; y: number }) {
  const box = useRef<HTMLDivElement>(null);
  const [left, setLeft] = useState(x);
  useLayoutEffect(() => setLeft(clampLeft(x, box.current?.offsetWidth ?? 0, window.innerWidth)), [x, text]);
  return createPortal(
    <div ref={box} role="tooltip" className="fixed z-50 pointer-events-none px-2 py-1 rounded border border-line bg-panel text-[11px] text-fg shadow-lg q-mono whitespace-nowrap" style={{ left, top: y }}>
      {text}
    </div>,
    document.body,
  );
}

function base64ToHex(b64: string) {
  const bin = atob(b64);
  let s = "";
  for (let i = 0; i < bin.length; i++) s += bin.charCodeAt(i).toString(16).padStart(2, "0").toUpperCase();
  return s;
}

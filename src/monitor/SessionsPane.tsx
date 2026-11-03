// Sessions (B1a): a Glide grid over the model's rows. A snapshot repaints only the cells that changed (damage), and
// re-renders this grid only when the row count changes — never the tab (U1). Selection follows the session key (U2).
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { DataEditor, GridCellKind, CompactSelection, type DataEditorRef, type GridCell, type GridColumn, type GridSelection, type Item } from "@glideapps/glide-data-grid";
import "@glideapps/glide-data-grid/dist/index.css";
import type { ActivityRow } from "../ipc/contract";
import { drawFitted, gridTheme } from "../results/ResultGrid";
import { COLUMNS, DEFAULT_COLUMNS, changedCells, querithmLabel, type Column, type MonitorModel } from "./model";
import { keyOf, waitColorVar } from "./format";
import { useTopics } from "./useModel";
import { tabTitleForSession } from "./monitorActions";

const resolve = (v: string) => {
  const name = /var\((--[^)]+)\)/.exec(v)?.[1];
  return name && typeof document !== "undefined" ? getComputedStyle(document.documentElement).getPropertyValue(name).trim() || v : v;
};

export interface SessionsPaneProps {
  model: MonitorModel;
  columns?: string[];
  onOpenDetail?: (row: ActivityRow) => void;
}

export function SessionsPane({ model, columns = DEFAULT_COLUMNS }: SessionsPaneProps) {
  const ref = useRef<DataEditorRef>(null);
  const cols = useMemo<Column[]>(() => columns.map((id) => COLUMNS.find((c) => c.id === id)!).filter(Boolean), [columns]);
  const [rowCount, setRowCount] = useState(model.rows.length);
  const [selection, setSelection] = useState<GridSelection>({ columns: CompactSelection.empty(), rows: CompactSelection.empty() });
  const prev = useRef<ActivityRow[]>(model.rows);
  const [sortTag, setSortTag] = useState("");
  const theme = useMemo(gridTheme, []);
  useTopics(model, ["connection"]); // dim when paused
  const paused = model.paused;

  const syncSelection = useCallback(() => {
    const i = model.selectedKey ? model.rows.findIndex((r) => keyOf(r.key) === model.selectedKey) : -1;
    setSelection(i >= 0 ? { columns: CompactSelection.empty(), rows: CompactSelection.fromSingleSelection(i), current: { cell: [0, i], range: { x: 0, y: i, width: 1, height: 1 }, rangeStack: [] } } : { columns: CompactSelection.empty(), rows: CompactSelection.empty() });
  }, [model]);

  useEffect(() => {
    const offRows = model.subscribe("rows", () => {
      const before = prev.current;
      const after = model.rows;
      prev.current = after;
      setSortTag(model.sort ? `${model.sort.column}:${model.sort.desc}` : "");
      if (before.length !== after.length) setRowCount(after.length);
      else ref.current?.updateCells(changedCells(before, after, cols, model));
      syncSelection();
    });
    const offTexts = model.subscribe("texts", () => {
      const t = cols.findIndex((c) => c.id === "text");
      if (t >= 0) ref.current?.updateCells(model.rows.map((_, row) => ({ cell: [t, row] as Item })));
    });
    const offSel = model.subscribe("selection", syncSelection);
    return () => { offRows(); offTexts(); offSel(); };
  }, [model, cols, syncSelection]);

  const gridColumns = useMemo<GridColumn[]>(
    () => cols.map((c) => ({ id: c.id, title: c.title + (model.sort?.column === c.id ? (model.sort.desc ? " ▼" : " ▲") : ""), width: c.width })),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [cols, sortTag],
  );

  const getCellContent = useCallback(
    ([col, row]: Item): GridCell => {
      const r = model.rows[row];
      const c = cols[col];
      if (!r || !c) return { kind: GridCellKind.Text, data: "", displayData: "", allowOverlay: false };
      const display = c.id === "querithm" ? querithmLabel(r, tabTitleForSession) : c.display(r, model);
      const themeOverride =
        c.id === "wait" && r.waitType ? { textDark: resolve(waitColorVar(r.waitMs)) }
        : c.id === "blockedBy" && (r.blockedBy.length || r.blockedByNote) ? { textDark: resolve("var(--q-err)") }
        : r.querithmKind ? { textDark: resolve("var(--q-accent)") }
        : paused ? { textDark: resolve("var(--q-muted)") } : undefined;
      return { kind: GridCellKind.Text, data: display, displayData: display, allowOverlay: c.id === "text", readonly: true, themeOverride };
    },
    // `paused` re-binds the callback: Glide repaints only damaged cells, so pausing alone would leave the grid undimmed.
    [model, cols, paused],
  );

  return (
    <div className="flex-1 min-h-0 relative" data-testid="sessions-grid">
      <DataEditor
        ref={ref}
        columns={gridColumns}
        rows={rowCount}
        getCellContent={getCellContent}
        drawCell={drawFitted}
        theme={theme}
        rowMarkers="none"
        smoothScrollX
        smoothScrollY
        width="100%"
        height="100%"
        gridSelection={selection}
        onGridSelectionChange={(s) => {
          const row = s.current?.cell[1] ?? s.rows.first();
          const r = row != null ? model.rows[row] : undefined;
          model.select(r ? keyOf(r.key) : null);
          setSelection(s);
        }}
        onHeaderClicked={(col) => cols[col] && model.setSort(cols[col].id)}
        getCellsForSelection
      />
    </div>
  );
}

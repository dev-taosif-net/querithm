# Phase C — Result Charts, SQL Notebooks, Rich Cell Viewers

Goal: turn result sets into charts without leaving the app, keep query + notes + charts together in a
notebook file, and view JSON/XML/binary cells properly. Engine-neutral (works off Arrow results).

Slices: **C1** chart view → **C2** rich cell viewers → **C3** `.sqlbook` notebooks → **C4** notebook
charts and export.

---

## Library decision (§2 row needed)

| Package | Version | License | Why |
|---|---|---|---|
| `uplot` | 1.6.x | MIT | ~50 KB, canvas, line/area/scatter at 100k+ points; no runtime deps |

Bar, pie/donut and histogram are drawn by a small in-house canvas renderer (`src/charts/draw/`), so no
second library. Rejected: ECharts (~1 MB), Recharts/D3 (SVG, slow at size). Bundle offline (no CDN).

## C1 — Chart view for a result set

### UI (`src/charts/`, new)
- `ChartView.tsx` — toggle in `ResultsPanel.tsx`: `Grid (Ctrl+1) | Chart (Ctrl+2)` per result set.
- `chartSpec.ts` — `{ kind: 'bar'|'line'|'area'|'scatter'|'pie'|'histogram', x, y[], series?, agg:
  'none'|'sum'|'count'|'avg'|'min'|'max', bins? }`. Default spec guessed from column types (time/number
  → line; text + number → bar; two numbers → scatter).
- `chartData.worker.ts` — reads Arrow vectors (transfer the record batches, same pattern as
  `src/results/export.worker.ts`), aggregates/bins, down-samples (LTTB for lines, max 10k points; bars
  top-N 200 + "other").
- `draw/bar.ts`, `draw/pie.ts`, `draw/histogram.ts`; `uplotAdapter.ts` for line/area/scatter.
- Export: PNG (canvas) and the aggregated data as CSV through the existing save path (`src/app/saveBytes.ts`).
- Theme: colours from `src/theme/tokens.css`, light/dark.
- Spec saved per tab in the workspace (`src/app/persistence.ts`), never the data.

### Rules
- No React re-render per incoming batch: the worker recomputes on a debounce (250 ms) while streaming,
  final pass on `query.done`.
- Charting a partial (cancelled/aborted) result shows "partial data" (reuse the incomplete marker).

### Tests (Vitest)
- Default spec inference per type; aggregation and binning correctness; LTTB keeps first/last/extremes;
  NULL handling (skipped and counted); decimal/bigint as numbers with precision warning.

### Performance
- §14 row **34** (29–33 are taken; `scripts/bench/run.mjs` records `34`/`34frames`): "Chart 1M-row result, line with 2 series — Chart toggle → first paint ≤ 500 ms;
  re-aggregation off the main thread, 0 frames > 32 ms".

## C2 — Rich cell viewers
- `src/results/viewers/` — side panel opened from a cell: JSON (pretty, fold, search; parse in a worker,
  cap 10 MB), XML (pretty; SQL Server showplan XML → "Open as plan"), binary (hex + length; image preview
  for PNG/JPEG/GIF magic bytes, rendered via blob URL, never as HTML), long text (wrap, copy).
- Cell data comes through `query.cell` (already exists) for values truncated in the grid.
- Tests: malformed JSON/XML shows the raw text with the parse error; image bytes never injected as HTML.

## C3 — `.sqlbook` notebooks

### File format (versioned JSON, validated on open)
```
{ "format": "querithm.sqlbook", "version": 1,
  "connection": { "profileId": "...", "database": "..." } | null,
  "cells": [ { "id": "ulid", "kind": "sql"|"markdown", "source": "...", "chart"?: ChartSpec } ] }
```
- **Outputs are not saved by default** (results are user data). Opt-in "Save outputs" stores at most
  1,000 rows × 50 columns per cell as text; the file then shows a warning badge.
- Size limits on open: file ≤ 20 MB, ≤ 2,000 cells, cell source ≤ 1 MB. Unknown `version` → refuse with reason.
- Profile ids that do not exist locally → notebook opens disconnected with "pick a connection".

### Where files are read/written
- Open/Save use the Tauri dialog + `fs` plugin on the user-picked path (same path script files use);
  the sidecar never writes user folders (§13). Autosave copy goes to `<appDataDir>/querithm/notebooks/`.
- Validation of the file shape happens in the webview loader (`src/notebook/format.ts`), and the
  sidecar validates every RPC parameter as usual (cells execute through the normal `query.execute`).

### UI (`src/notebook/`, new)
- `NotebookTab.tsx` (dockview panel), `Cell.tsx`, `MarkdownCell.tsx` (render with `marked`, HTML
  disabled / escaped, links open via Tauri only on click), `SqlCell.tsx`.
- **One Monaco instance:** the focused SQL cell hosts the shared editor (`editor.setModel()` with the
  cell's model); other cells render static colourised text (`monaco.editor.colorize`). Never one editor per cell.
- One session per notebook (lazy, like tabs); Run cell `Ctrl+Enter`, Run all `Shift+F5`, stop on first
  error (setting), Cancel stops the running cell and the queue.
- Each cell output: result grid (compact, max height) or chart (C4), messages.
- Guards: destructive / Prod first-run guards apply per cell, enforced by the sidecar as today.

### Tests
- Vitest: format validation (oversize, wrong version, bad shapes, unknown fields ignored), cell
  reorder/add/delete, single Monaco model switching, run-all queue with cancel and error stop.
- Smoke (`scripts/bench/smoke-ui.mjs`): new notebook → 2 SQL cells + 1 markdown → Run all → results visible.
- Integration: run-all on both engines incl. a failing middle cell (PG stops; SQL Server batch semantics
  per cell still stop the notebook queue).

### Failure paths
- Connection drops mid-run → running cell shows the error, queue stops, other outputs kept.
- Save fails (disk full / permission) → error toast, unsaved marker stays, autosave copy intact.
- Crash → autosave copy restored on next launch like tabs.

## C4 — Notebook charts and export
- SQL cell can show its result as a chart (`chart` spec saved in the cell).
- Export notebook to Markdown or HTML (static, inline PNG charts, no script) via the save dialog.
- `.ipynb` import (SQL cells only, from `%%sql` cells) — optional, validate strictly.

## Spec
- §2 rows (`uplot`), §12 (chart view, notebook panel), §14 row 34, §16 if any deviation; THIRD-PARTY-NOTICES.

## Phase C done when
- Chart view + viewers + notebooks shipped on both engines, smoke covers a notebook run, metric 34 passes.

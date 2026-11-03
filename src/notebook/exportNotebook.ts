// Notebook export (Phase C4): a static Markdown or HTML document — the cells' text, each SQL cell's result as a table
// (first 100 rows) and its chart as an inline PNG. The HTML page carries no script and loads nothing (its own CSP
// allows inline styles and data: images only); every piece of cell text and data is escaped.
import { reconcileSpec, type ChartSpec } from "../charts/chartSpec";
import { chartColumns, computeChartOnce } from "../charts/chartFeed";
import { renderChartImage } from "../charts/render";
import { chartTheme } from "../charts/theme";
import { saveBytes } from "../app/saveBytes";
import { executionData, type ArrowResultSet } from "../results/model";
import { useTabs } from "../store/tabs";
import { toast } from "../store/ui";
import { escapeHtml, renderMarkdown } from "./markdown";
import { cellSource, syncSources } from "./runtime";
import { notebookOf, type CellState } from "./store";
import type { SavedOutput } from "./format";

export const EXPORT_ROWS = 100;
const EXPORT_COLUMNS = 50;

/** A cell's result as plain rows: the live result set, else the one saved in the file. */
export interface ExportTable {
  columns: string[];
  rows: (string | null)[][];
  rowCount: number;
}

function liveTable(rs: ArrowResultSet): ExportTable {
  const cols = rs.columns.slice(0, EXPORT_COLUMNS);
  const rows: (string | null)[][] = [];
  for (let r = 0; r < Math.min(rs.rowCount, EXPORT_ROWS); r++) rows.push(cols.map((_, c) => rs.textPhysical(r, c)));
  return { columns: cols.map((c) => c.name), rows, rowCount: rs.rowCount };
}

const savedTable = (s: SavedOutput): ExportTable => ({ columns: s.columns, rows: s.rows.slice(0, EXPORT_ROWS), rowCount: s.rowCount });

export interface ExportCell {
  kind: "sql" | "markdown";
  source: string;
  table?: ExportTable;
  /** data:image/png;base64,… */
  chart?: string;
  error?: string;
}

const mdCell = (v: string | null) => (v == null ? "NULL" : v.replace(/\\/g, "\\\\").replace(/\|/g, "\\|").replace(/\r?\n/g, " "));

/** The document as Markdown. */
export function toMarkdown(title: string, cells: ExportCell[]): string {
  const out: string[] = [`<!-- Exported from Querithm: ${title.replace(/--/g, "- -")} -->`, ""];
  for (const c of cells) {
    if (c.kind === "markdown") {
      out.push(c.source.trimEnd(), "");
      continue;
    }
    // A fence longer than any backtick run in the SQL, so the code block cannot end early.
    const longest = Math.max(2, ...[...c.source.matchAll(/`+/g)].map((m) => m[0].length));
    const fence = "`".repeat(longest + 1);
    out.push(`${fence}sql`, c.source.trimEnd(), fence, "");
    if (c.error) out.push(`> Error: ${c.error.replace(/\r?\n/g, " ")}`, "");
    if (c.chart) out.push(`![Chart](${c.chart})`, "");
    if (c.table) {
      out.push(`| ${c.table.columns.map(mdCell).join(" | ")} |`, `| ${c.table.columns.map(() => "---").join(" | ")} |`);
      for (const r of c.table.rows) out.push(`| ${r.map(mdCell).join(" | ")} |`);
      if (c.table.rowCount > c.table.rows.length) out.push("", `_First ${c.table.rows.length.toLocaleString()} of ${c.table.rowCount.toLocaleString()} rows._`);
      out.push("");
    }
  }
  return out.join("\n");
}

const STYLE = `body{font:14px/1.55 system-ui,-apple-system,"Segoe UI",sans-serif;max-width:1100px;margin:24px auto;padding:0 16px;color:#1f2328;background:#fff}
pre{background:#f6f8fa;border:1px solid #d0d7de;border-radius:6px;padding:10px 12px;overflow:auto;font:12.5px/1.5 ui-monospace,Menlo,Consolas,monospace}
table{border-collapse:collapse;font:12px ui-monospace,Menlo,Consolas,monospace;margin:6px 0 4px;display:block;overflow:auto;max-width:100%}
th,td{border:1px solid #d0d7de;padding:3px 8px;text-align:left;white-space:nowrap}th{background:#f6f8fa}
.null{color:#59636e;font-style:italic}.note{color:#59636e;font-size:12px}.error{color:#b11c1c;font-family:ui-monospace,Menlo,monospace;white-space:pre-wrap}
img{max-width:100%;border:1px solid #d0d7de;border-radius:6px}section{margin:18px 0}`;

/** The document as a static HTML page: no script, nothing loaded from anywhere. */
export function toHtml(title: string, cells: ExportCell[]): string {
  const body = cells.map((c) => {
    if (c.kind === "markdown") return `<section>${renderMarkdown(c.source)}</section>`;
    const parts = [`<pre><code>${escapeHtml(c.source)}</code></pre>`];
    if (c.error) parts.push(`<div class="error">${escapeHtml(c.error)}</div>`);
    if (c.chart) parts.push(`<img alt="Chart" src="${escapeHtml(c.chart)}">`);
    if (c.table) {
      const head = `<tr>${c.table.columns.map((h) => `<th>${escapeHtml(h)}</th>`).join("")}</tr>`;
      const rows = c.table.rows.map((r) => `<tr>${r.map((v) => (v == null ? '<td class="null">NULL</td>' : `<td>${escapeHtml(v)}</td>`)).join("")}</tr>`).join("\n");
      parts.push(`<table><thead>${head}</thead><tbody>${rows}</tbody></table>`);
      if (c.table.rowCount > c.table.rows.length) parts.push(`<div class="note">First ${c.table.rows.length.toLocaleString()} of ${c.table.rowCount.toLocaleString()} rows.</div>`);
    }
    return `<section>${parts.join("\n")}</section>`;
  });
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; form-action 'none'; base-uri 'none'; object-src 'none'; frame-src 'none'">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(title)}</title>
<style>${STYLE}</style>
</head>
<body>
${body.join("\n")}
</body>
</html>
`;
}

/** A cell's chart as a PNG data URL, computed from its live result (null when it has none). */
async function chartImage(cell: CellState, rs: ArrowResultSet | undefined): Promise<string | null> {
  if (cell.view !== "chart" || !rs?.columns.length) return null;
  // chartColumns: a dynamically typed (SQLite) column counts as numeric only when every value is a number (Phase D0f).
  const { spec } = reconcileSpec(cell.chart as ChartSpec | null | undefined, chartColumns(rs));
  if (!spec) return null;
  const data = await computeChartOnce(rs, spec);
  // Exported pages are read on white: draw in the light palette whatever the app's theme.
  const root = document.documentElement;
  const was = root.dataset.theme;
  root.dataset.theme = "light";
  try {
    const canvas = renderChartImage(data, `${spec.kind === "histogram" ? `Histogram of ${spec.x}` : `${spec.y.join(", ") || "Rows"} by ${spec.x}`}`, 900, 360, chartTheme());
    return canvas ? canvas.toDataURL("image/png") : null;
  } finally {
    if (was === undefined) delete root.dataset.theme;
    else root.dataset.theme = was;
  }
}

export async function collectCells(tabId: string): Promise<ExportCell[]> {
  syncSources(tabId);
  const doc = notebookOf(tabId);
  if (!doc) return [];
  const out: ExportCell[] = [];
  for (const c of doc.cells) {
    const source = cellSource(tabId, c);
    if (c.kind === "markdown") {
      out.push({ kind: "markdown", source });
      continue;
    }
    const rs = c.executionId ? executionData.get(c.executionId)?.resultSets[0] : undefined;
    const cell: ExportCell = { kind: "sql", source };
    if (rs?.columns.length) cell.table = liveTable(rs);
    else if (c.saved) cell.table = savedTable(c.saved);
    if (c.status === "failed") cell.error = "The cell failed when it last ran.";
    try {
      cell.chart = (await chartImage(c, rs)) ?? undefined;
    } catch {
      /* the table still carries the result */
    }
    out.push(cell);
  }
  return out;
}

export async function exportNotebook(tabId: string, format: "md" | "html") {
  const t = useTabs.getState().tabs[tabId];
  if (!t) return;
  const title = t.title.replace(/\.sqlbook$/i, "");
  const cells = await collectCells(tabId);
  const text = format === "md" ? toMarkdown(title, cells) : toHtml(title, cells);
  const path = await saveBytes(`${title}.${format}`, new TextEncoder().encode(text), format === "md"
    ? { name: "Markdown", extension: "md", mime: "text/markdown" }
    : { name: "HTML page", extension: "html", mime: "text/html" });
  if (path) toast("success", `Exported ${path}`);
}

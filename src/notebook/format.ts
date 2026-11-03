// The .sqlbook file (Phase C3): versioned JSON, validated on open. A notebook file is untrusted input — it may come
// from anyone — so every field is checked for shape and size, unknown fields are ignored and an unknown version is
// refused with the reason. Outputs (query results are user data) are written only when the user opts in.
import { validateSpec, type ChartSpec } from "../charts/chartSpec";

export const NOTEBOOK_FORMAT = "querithm.sqlbook";
export const NOTEBOOK_VERSION = 1;
export const NOTEBOOK_EXTENSION = "sqlbook";

export const LIMITS = {
  /** File size on open (UTF-8 bytes). */
  fileBytes: 20 * 1024 * 1024,
  cells: 2_000,
  /** One cell's source (UTF-8 bytes). */
  sourceBytes: 1024 * 1024,
  /** Saved outputs: per cell. */
  outputRows: 1_000,
  outputColumns: 50,
  /** One saved output value (characters); longer text is cut with an ellipsis when saving. */
  outputValue: 4_096,
  id: 64,
  name: 256,
} as const;

export type CellKind = "sql" | "markdown";

/** A result kept in the file as text (opt-in "Save outputs"). */
export interface SavedOutput {
  columns: string[];
  rows: (string | null)[][];
  /** Rows the result had when it was saved (more than `rows` when cut at the limit). */
  rowCount: number;
}

export interface NotebookCellFile {
  id: string;
  kind: CellKind;
  source: string;
  chart?: ChartSpec;
  /** The cell showed its chart rather than its grid. */
  view?: "grid" | "chart";
  output?: SavedOutput;
}

export interface NotebookFile {
  format: typeof NOTEBOOK_FORMAT;
  version: typeof NOTEBOOK_VERSION;
  connection: { profileId: string; database: string | null } | null;
  cells: NotebookCellFile[];
}

export type ParseResult = { ok: true; notebook: NotebookFile; warnings: string[] } | { ok: false; reason: string };

const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/** A ULID: 48-bit time + 80 random bits, sortable by creation. */
export function newCellId(now = Date.now()): string {
  let t = now;
  let time = "";
  for (let i = 0; i < 10; i++) {
    time = CROCKFORD[t % 32] + time;
    t = Math.floor(t / 32);
  }
  const rnd = new Uint8Array(16);
  crypto.getRandomValues(rnd);
  return time + Array.from(rnd, (b) => CROCKFORD[b % 32]).join("");
}

const encoder = new TextEncoder();
/** UTF-8 byte length, without encoding when the string is short enough either way. */
export function utf8Bytes(s: string, limit = Infinity): number {
  if (s.length * 3 <= limit) return s.length; // cannot be over the limit: an estimate is enough
  return encoder.encode(s).length;
}

const isObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === "object" && !Array.isArray(v);
const ID = /^[A-Za-z0-9_-]{1,64}$/;

function readOutput(raw: unknown): SavedOutput | null {
  if (!isObject(raw)) return null;
  const { columns, rows, rowCount } = raw;
  if (!Array.isArray(columns) || columns.length > LIMITS.outputColumns || !columns.every((c) => typeof c === "string" && c.length <= LIMITS.name)) return null;
  if (!Array.isArray(rows) || rows.length > LIMITS.outputRows) return null;
  for (const r of rows) {
    if (!Array.isArray(r) || r.length !== columns.length) return null;
    if (!r.every((v) => v === null || (typeof v === "string" && v.length <= LIMITS.outputValue + 1))) return null;
  }
  const count = typeof rowCount === "number" && Number.isInteger(rowCount) && rowCount >= rows.length ? rowCount : rows.length;
  return { columns: columns as string[], rows: rows as (string | null)[][], rowCount: count };
}

/** Reads a .sqlbook document. `byteLength`: the file's size when known (checked before parsing). */
export function parseNotebook(text: string, byteLength?: number): ParseResult {
  const size = byteLength ?? utf8Bytes(text, LIMITS.fileBytes);
  if (size > LIMITS.fileBytes) return { ok: false, reason: `The file is ${(size / 1024 / 1024).toFixed(1)} MB; notebooks up to 20 MB can be opened.` };
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch (e) {
    return { ok: false, reason: `The file is not valid JSON: ${e instanceof Error ? e.message : String(e)}` };
  }
  if (!isObject(raw) || raw.format !== NOTEBOOK_FORMAT) return { ok: false, reason: "This is not a Querithm notebook (the format field is not \"querithm.sqlbook\")." };
  if (raw.version !== NOTEBOOK_VERSION) {
    return {
      ok: false,
      reason: typeof raw.version === "number" && raw.version > NOTEBOOK_VERSION
        ? `This notebook was written by a newer Querithm (format version ${raw.version}); this version reads version ${NOTEBOOK_VERSION}. Update Querithm to open it.`
        : `Unknown notebook format version (${JSON.stringify(raw.version)?.slice(0, 20)}); this version reads version ${NOTEBOOK_VERSION}.`,
    };
  }
  const warnings: string[] = [];
  let connection: NotebookFile["connection"] = null;
  if (raw.connection != null) {
    const c = raw.connection;
    if (isObject(c) && typeof c.profileId === "string" && c.profileId.length > 0 && c.profileId.length <= LIMITS.id && (c.database == null || (typeof c.database === "string" && c.database.length <= LIMITS.name))) {
      connection = { profileId: c.profileId, database: (c.database as string | null | undefined) ?? null };
    } else warnings.push("The saved connection was not readable; pick a connection.");
  }
  if (!Array.isArray(raw.cells)) return { ok: false, reason: "The notebook has no cell list." };
  if (raw.cells.length > LIMITS.cells) return { ok: false, reason: `The notebook has ${raw.cells.length.toLocaleString()} cells; up to ${LIMITS.cells.toLocaleString()} can be opened.` };
  const seen = new Set<string>();
  const cells: NotebookCellFile[] = [];
  let droppedCharts = 0;
  let droppedOutputs = 0;
  for (let i = 0; i < raw.cells.length; i++) {
    const c = raw.cells[i];
    if (!isObject(c)) return { ok: false, reason: `Cell ${i + 1} is not an object.` };
    if (c.kind !== "sql" && c.kind !== "markdown") return { ok: false, reason: `Cell ${i + 1} has an unknown kind (${JSON.stringify(c.kind)?.slice(0, 20)}).` };
    if (typeof c.source !== "string") return { ok: false, reason: `Cell ${i + 1} has no source text.` };
    if (utf8Bytes(c.source, LIMITS.sourceBytes) > LIMITS.sourceBytes) return { ok: false, reason: `Cell ${i + 1} is larger than 1 MB.` };
    // A missing, malformed or repeated id gets a new one: ids only tell cells apart.
    let id = typeof c.id === "string" && ID.test(c.id) && !seen.has(c.id) ? c.id : newCellId();
    while (seen.has(id)) id = newCellId();
    seen.add(id);
    const cell: NotebookCellFile = { id, kind: c.kind, source: c.source };
    if (c.chart != null) {
      const spec = c.kind === "sql" ? validateSpec(c.chart) : null;
      if (spec) cell.chart = spec;
      else droppedCharts++;
    }
    if (c.view === "chart" && cell.chart) cell.view = "chart";
    if (c.output != null) {
      const out = c.kind === "sql" ? readOutput(c.output) : null;
      if (out) cell.output = out;
      else droppedOutputs++;
    }
    cells.push(cell);
  }
  if (droppedCharts) warnings.push(`${droppedCharts} chart setting${droppedCharts === 1 ? " was" : "s were"} not readable and ${droppedCharts === 1 ? "was" : "were"} left out.`);
  if (droppedOutputs) warnings.push(`${droppedOutputs} saved output${droppedOutputs === 1 ? " was" : "s were"} not readable and ${droppedOutputs === 1 ? "was" : "were"} left out.`);
  return { ok: true, notebook: { format: NOTEBOOK_FORMAT, version: NOTEBOOK_VERSION, connection, cells }, warnings };
}

/** Cuts a result to what a file keeps: 1,000 rows × 50 columns, each value at most 4,096 characters. */
export function capOutput(columns: string[], rows: (string | null)[][], rowCount: number): SavedOutput {
  const cols = columns.slice(0, LIMITS.outputColumns).map((c) => c.slice(0, LIMITS.name));
  const cut = (v: string | null) => (v != null && v.length > LIMITS.outputValue ? `${v.slice(0, LIMITS.outputValue)}…` : v);
  return { columns: cols, rows: rows.slice(0, LIMITS.outputRows).map((r) => r.slice(0, cols.length).map(cut)), rowCount };
}

/** The document as text; `outputs` false leaves every saved output out. */
export function serializeNotebook(nb: Omit<NotebookFile, "format" | "version">, outputs: boolean): string {
  const file: NotebookFile = {
    format: NOTEBOOK_FORMAT,
    version: NOTEBOOK_VERSION,
    connection: nb.connection,
    cells: nb.cells.map((c) => ({
      id: c.id,
      kind: c.kind,
      source: c.source,
      ...(c.chart ? { chart: c.chart } : {}),
      ...(c.view === "chart" && c.chart ? { view: "chart" as const } : {}),
      ...(outputs && c.output ? { output: c.output } : {}),
    })),
  };
  return `${JSON.stringify(file, null, 2)}\n`;
}

/** True when a document holds saved results (the warning badge). */
export const hasOutputs = (nb: NotebookFile) => nb.cells.some((c) => !!c.output);

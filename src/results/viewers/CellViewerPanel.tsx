// Cell value viewer (Phase C2): a side panel next to the result grid for the activated cell. JSON (formatted in a
// worker, folding, search), XML (formatted, showplan → Open as plan), binary (hex, PNG/JPEG/GIF preview) and long text
// (wrap, copy). A truncated cell's full value comes from query.cell. Values are never inserted as HTML: text is React
// text, and an image is an <img> with a data: URL whose type comes from the bytes' signature.
import { useEffect, useMemo, useState } from "react";
import type { Engine } from "../../ipc/contract";
import { rpc } from "../../ipc/client";
import { toast } from "../../store/ui";
import { StorageClass, type ArrowResultSet } from "../model";
import { RAW_DISPLAY_CHARS, base64ToBytes, byteSize, bytesToBase64, hexDump, hexLiteral, hexToBytes, sniffImage, viewerKind, type ViewerKind } from "./detect";
import { parseJson } from "./parseJson";
import type { JsonViewResult } from "./jsonView";
import { buildXmlView } from "./xmlView";
import { StructuredLines } from "./StructuredLines";
import { openAsPlan, PLAN_FORMATS, type PlanFormat } from "./openAsPlan";

export interface CellViewerPanelProps {
  tabId: string;
  executionId: string;
  rs: ArrowResultSet;
  /** View row (after sort / filter). */
  row: number;
  /** Index into rs.columns. */
  col: number;
  engine: Engine;
  onClose(): void;
}

export const UNAVAILABLE_NOTE = "The full value is no longer available (the execution was closed). Re-run the query to fetch it.";

type Loaded =
  | { state: "loading" }
  | { state: "null" }
  | { state: "text"; text: string; note?: string }
  | { state: "bytes"; bytes: Uint8Array; note?: string };
type Value = Extract<Loaded, { state: "text" | "bytes" }>;

/** The cell's value: what the grid holds, or the full value from query.cell when the grid's copy was truncated. */
function useCellValue(executionId: string, rs: ArrowResultSet, row: number, col: number): Loaded {
  const [loaded, setLoaded] = useState<Loaded>({ state: "loading" });
  useEffect(() => {
    let live = true;
    const c = rs.columns[col];
    if (!c || row >= rs.visibleRows) {
      setLoaded({ state: "null" });
      return;
    }
    const binary = c.kind === "binary";
    // A blob in a dynamically typed column (SQLite) travels as hex text: the viewer shows its bytes, as for a binary column.
    const blob = rs.storageClass(row, col) === StorageClass.Blob;
    const fromText = (text: string): Value => {
      const bytes = blob ? hexToBytes(text) : null;
      return bytes ? { state: "bytes", bytes } : { state: "text", text };
    };
    const local = binary ? (rs.raw(row, c.field) as Uint8Array | null) : rs.text(row, col);
    if (local == null) {
      setLoaded({ state: "null" });
      return;
    }
    const have: Value = typeof local === "string" ? fromText(local) : { state: "bytes", bytes: local };
    if (c.flagField == null || !rs.isTruncated(row, col)) {
      setLoaded(have);
      return;
    }
    setLoaded({ state: "loading" });
    void rpc("query.cell", { executionId, resultIndex: rs.index, row: rs.physicalRow(row), col: c.ordinal })
      .catch(() => null)
      .then((full) => {
        if (!live) return;
        if (!full || full.unavailable || full.value == null) setLoaded({ ...have, note: UNAVAILABLE_NOTE });
        else if (full.isBinary) setLoaded({ state: "bytes", bytes: base64ToBytes(full.value) });
        else setLoaded(fromText(full.value));
      });
    return () => {
      live = false;
    };
  }, [executionId, rs, row, col]);
  return loaded;
}

function Segments<T extends string>({ value, options, onChange, label }: { value: T; options: { value: T; label: string }[]; onChange: (v: T) => void; label: string }) {
  return (
    <div className="flex rounded border border-line-strong overflow-hidden" role="group" aria-label={label}>
      {options.map((o) => (
        <button
          key={o.value}
          className={`h-[20px] px-2 text-[11px] ${value === o.value ? "bg-accent-solid text-white font-semibold" : "text-muted hover:text-fg hover:bg-hover"}`}
          aria-pressed={value === o.value}
          onClick={() => onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

function Note({ children, error }: { children: React.ReactNode; error?: boolean }) {
  return (
    <div className={`px-2 py-1 text-[11px] border-b border-line shrink-0 break-words ${error ? "bg-err/10 q-hl text-err" : "bg-panel-2 text-muted"}`} role={error ? "alert" : "note"}>
      {children}
    </div>
  );
}

function RawText({ text, wrap }: { text: string; wrap: boolean }) {
  const cut = text.length > RAW_DISPLAY_CHARS;
  return (
    <>
      {cut && <Note>Showing the first {RAW_DISPLAY_CHARS.toLocaleString("en-US")} of {text.length.toLocaleString("en-US")} characters; Copy copies all of them.</Note>}
      <pre
        className={`flex-1 min-h-0 overflow-auto m-0 p-2 q-mono text-[11.5px] leading-[18px] text-fg ${wrap ? "whitespace-pre-wrap break-words" : "whitespace-pre"} focus:outline-none focus-visible:ring-2 focus-visible:ring-accent`}
        tabIndex={0}
        aria-label="Value"
      >
        {cut ? text.slice(0, RAW_DISPLAY_CHARS) : text}
      </pre>
    </>
  );
}

async function copy(text: string, what: string) {
  try {
    await navigator.clipboard.writeText(text);
    toast("success", `Copied ${what}.`);
  } catch (e) {
    toast("error", e instanceof Error ? e.message : "Could not copy the value.");
  }
}

/** JSON formatted off the main thread; null while the worker runs. */
function useJsonView(text: string | null): JsonViewResult | null {
  const [r, setR] = useState<JsonViewResult | null>(null);
  useEffect(() => {
    setR(null);
    if (text == null) return;
    const ac = new AbortController();
    parseJson(text, ac.signal).then(setR, (e) => {
      if (!ac.signal.aborted) setR({ ok: false, error: e instanceof Error ? e.message : String(e) });
    });
    return () => ac.abort();
  }, [text]);
  return r;
}

function TextValue({ text, kind, tabId, executionId }: { text: string; kind: ViewerKind; tabId: string; executionId: string }) {
  const [mode, setMode] = useState<"pretty" | "raw">("pretty");
  const [wrap, setWrap] = useState(kind === "text");
  const json = useJsonView(kind === "json" ? text : null);
  const xml = useMemo(() => (kind === "xml" ? buildXmlView(text) : null), [kind, text]);
  const structured = kind === "json" ? json : xml;
  const view = structured?.ok ? structured.view : null;
  const planFormat: PlanFormat | null = xml?.ok && xml.showplan ? "showplan" : json?.ok ? (json.jsonPlan ?? null) : null;
  const plan = planFormat ? PLAN_FORMATS[planFormat] : null;
  const [opening, setOpening] = useState(false);
  const label = kind === "json" ? "JSON" : kind === "xml" ? "XML" : "text";
  const showPretty = !!view && mode === "pretty";
  return (
    <>
      <div className="flex flex-wrap items-center gap-1.5 px-2 py-1 border-b border-line shrink-0">
        {(kind === "json" || kind === "xml") && view && (
          <Segments label="Display" value={mode} onChange={setMode} options={[{ value: "pretty", label: "Formatted" }, { value: "raw", label: "Raw" }]} />
        )}
        {!showPretty && (
          <label className="flex items-center gap-1 text-[11px] text-muted cursor-pointer">
            <input type="checkbox" checked={wrap} onChange={(e) => setWrap(e.target.checked)} /> Wrap
          </label>
        )}
        <span className="text-[11px] text-muted">{text.length.toLocaleString("en-US")} characters</span>
        <div className="ml-auto flex items-center gap-1.5">
          {plan && (
            <button
              className="q-btn h-[22px]"
              disabled={opening}
              title={plan.title}
              onClick={() => {
                setOpening(true);
                void openAsPlan({ tabId, executionId, engine: plan.engine, raw: text }).finally(() => setOpening(false));
              }}
            >
              {opening ? "Opening…" : "Open as plan"}
            </button>
          )}
          <button className="q-btn h-[22px]" onClick={() => void copy(text, "the value")}>Copy</button>
        </div>
      </div>
      {kind === "json" && !json && <Note>Formatting JSON…</Note>}
      {structured && !structured.ok && (
        <Note error={!("tooLarge" in structured && structured.tooLarge)}>
          {"tooLarge" in structured && structured.tooLarge ? structured.error : `Not valid ${label}: ${structured.error}`}
        </Note>
      )}
      {showPretty ? <StructuredLines view={view!} label={label} /> : <RawText text={text} wrap={wrap} />}
    </>
  );
}

function BytesValue({ bytes }: { bytes: Uint8Array }) {
  const mime = useMemo(() => sniffImage(bytes), [bytes]);
  const [mode, setMode] = useState<"image" | "hex">(mime ? "image" : "hex");
  useEffect(() => setMode(mime ? "image" : "hex"), [mime]);
  const lines = useMemo(() => (mode === "hex" ? hexDump(bytes) : []), [bytes, mode]);
  const src = useMemo(() => (mime && mode === "image" ? `data:${mime};base64,${bytesToBase64(bytes)}` : null), [bytes, mime, mode]);
  return (
    <>
      <div className="flex flex-wrap items-center gap-1.5 px-2 py-1 border-b border-line shrink-0">
        {mime && <Segments label="Display" value={mode} onChange={setMode} options={[{ value: "image", label: "Image" }, { value: "hex", label: "Hex" }]} />}
        <span className="text-[11px] text-muted">{byteSize(bytes.length)}{mime ? ` · ${mime.slice(6).toUpperCase()}` : ""}</span>
        <button className="q-btn h-[22px] ml-auto" onClick={() => void copy(hexLiteral(bytes), "the value as hex")}>Copy hex</button>
      </div>
      {src ? (
        <div className="flex-1 min-h-0 overflow-auto p-2 bg-panel-2">
          <img src={src} alt={`Image preview (${mime!.slice(6).toUpperCase()}, ${byteSize(bytes.length)})`} className="max-w-full h-auto block" draggable={false} />
        </div>
      ) : (
        <pre className="flex-1 min-h-0 overflow-auto m-0 p-2 q-mono text-[11.5px] leading-[18px] text-fg whitespace-pre focus:outline-none focus-visible:ring-2 focus-visible:ring-accent" tabIndex={0} aria-label="Hex dump">
          {lines.join("\n")}
        </pre>
      )}
    </>
  );
}

/** The side panel for one cell. Esc (focus inside the panel) or ✕ closes it. */
export function CellViewerPanel({ tabId, executionId, rs, row, col, onClose }: CellViewerPanelProps) {
  const c = rs.columns[col];
  const loaded = useCellValue(executionId, rs, row, col);
  const kind: ViewerKind | null = c ? viewerKind(c, loaded.state === "text" ? loaded.text : null) : null;
  return (
    <aside
      className="w-[400px] max-w-[50%] min-w-[260px] shrink-0 border-l border-line bg-panel flex flex-col min-h-0"
      aria-label="Cell value"
      data-q-cell-viewer
      onKeyDown={(e) => {
        if (e.key === "Escape" && !e.defaultPrevented) {
          e.stopPropagation();
          onClose();
        }
      }}
    >
      <div className="h-[30px] px-2 border-b border-line flex items-center gap-2 shrink-0">
        <span className="q-mono text-[12px] font-semibold text-fg truncate" title={c?.name}>{c?.name ?? "—"}</span>
        <span className="text-[11px] text-muted truncate shrink min-w-0">{c?.typeName}</span>
        <span className="text-[11px] text-muted whitespace-nowrap">row {(row + 1).toLocaleString("en-US")}</span>
        <button className="ml-auto q-btn h-[22px] w-[22px] px-0 justify-center shrink-0" onClick={onClose} title="Close (Esc)" aria-label="Close the value viewer">
          ✕
        </button>
      </div>
      {loaded.state === "loading" && <Note>Fetching the full value…</Note>}
      {loaded.state === "null" && <div className="flex-1 flex items-center justify-center text-muted text-xs">NULL</div>}
      {"note" in loaded && loaded.note && <Note>{loaded.note}</Note>}
      {loaded.state === "text" && kind && <TextValue key={`${row}:${col}`} text={loaded.text} kind={kind} tabId={tabId} executionId={executionId} />}
      {loaded.state === "bytes" && <BytesValue bytes={loaded.bytes} />}
    </aside>
  );
}

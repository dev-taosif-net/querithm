// One notebook cell (Phase C3). The focused SQL cell hosts the group's shared Monaco editor (attachEditor with the
// cell's model); every other SQL cell is static, colourised text. Never one editor per cell (§12).
import { memo, useEffect, useLayoutEffect, useRef, useState } from "react";
import { attachEditor, languageFor } from "../editor/editors";
import type { Engine } from "../ipc/contract";
import { knownEngine } from "../engines";
import { monaco } from "../editor/monaco";
import { useSettings } from "../store/settings";
import { toast } from "../store/ui";
import { Icon } from "../app/icons";
import { CellOutput } from "./CellOutput";
import { renderMarkdown } from "./markdown";
import { addCell, cellKey, cellModel, cellSource, focusCell, removeCell, runCell, setCellKind, setCellSource, shiftCell } from "./runtime";
import { useNotebooks, type CellState } from "./store";

export interface CellProps {
  tabId: string;
  cell: CellState;
  index: number;
  count: number;
  focused: boolean;
  /** The Dockview group whose shared editor the focused cell borrows. */
  groupId: string;
  visible: boolean;
  engine: Engine | undefined;
  running: boolean;
}

const MIN_LINES = 3;

/** Lines → pixels, as the editor draws them (its line height is 1.7 × the font size, plus 6 px padding each side). */
function heightFor(lines: number, fontSize: number): number {
  return Math.max(MIN_LINES, lines) * Math.round(fontSize * 1.7) + 12;
}

export const Cell = memo(function Cell(p: CellProps) {
  const { tabId, cell, index, count, focused } = p;
  const label = `${cell.kind === "sql" ? "SQL" : "Markdown"} cell ${index + 1} of ${count}`;
  const gutter =
    cell.status === "running" ? <Icon name="spinner" size={12} strokeWidth={2.6} className="q-spin text-accent" />
    : cell.status === "queued" ? <span title="Queued">…</span>
    : cell.kind === "sql" ? <span title={cell.runNumber ? `Run ${cell.runNumber}` : "Not run yet"}>[{cell.runNumber ?? " "}]</span>
    : null;
  return (
    <section
      aria-label={label}
      data-q-cell={cell.id}
      data-focused={focused || undefined}
      className={`group relative flex rounded border ${focused ? "border-accent" : "border-line"} bg-panel`}
    >
      {focused && <span className="absolute left-0 top-0 bottom-0 w-[2px] bg-accent rounded-l" aria-hidden />}
      <div className={`w-12 shrink-0 pt-2 pr-2 text-right q-mono text-[11px] ${cell.status === "failed" ? "text-err" : "text-muted"}`} aria-hidden>{gutter}</div>
      <div className="flex-1 min-w-0 py-1 pr-2">
        <CellBar {...p} />
        {cell.kind === "sql" ? <SqlBody {...p} /> : <MarkdownBody {...p} />}
        {cell.note && <div className="text-[11px] text-muted mt-1">{cell.note}</div>}
        {cell.kind === "sql" && <CellOutput tabId={tabId} cell={cell} engine={knownEngine(p.engine)} />}
      </div>
    </section>
  );
});

/** The cell's actions: on hover, on keyboard focus inside the cell, and always on the focused cell. */
function CellBar({ tabId, cell, index, count, focused, running }: CellProps) {
  const busy = cell.status === "running" || cell.status === "queued";
  return (
    <div className="flex items-center gap-1 h-[22px] text-[11px]">
      <span className="text-muted uppercase tracking-wide text-[10px] font-semibold">{cell.kind === "sql" ? "SQL" : "Markdown"}</span>
      <div className={`ml-auto flex items-center gap-0.5 ${focused ? "" : "opacity-0 group-hover:opacity-100 focus-within:opacity-100"}`} role="toolbar" aria-label={`Cell ${index + 1} actions`}>
        <button className="q-btn h-[20px] px-1.5" disabled={busy || (running && cell.kind === "sql")} title={cell.kind === "sql" ? "Run cell (Ctrl+Enter)" : "Show the formatted text (Ctrl+Enter)"} onClick={() => void runCell(tabId, cell.id)}>
          <Icon name="play" size={10} className="text-ok" /> {cell.kind === "sql" ? "Run" : "Done"}
        </button>
        {cell.kind === "markdown" && !cell.editing && (
          <button className="q-btn h-[20px] px-1.5" onClick={() => { focusCell(tabId, cell.id); useNotebooks.getState().patchCell(tabId, cell.id, { editing: true }); }}>Edit</button>
        )}
        <button className="q-btn h-[20px] px-1.5" disabled={busy} title={cell.kind === "sql" ? "Change to a markdown cell" : "Change to a SQL cell"} onClick={() => setCellKind(tabId, cell.id, cell.kind === "sql" ? "markdown" : "sql")}>
          {cell.kind === "sql" ? "To markdown" : "To SQL"}
        </button>
        <button className="q-btn h-[20px] px-1.5" disabled={index === 0} title="Move up" aria-label="Move cell up" onClick={() => shiftCell(tabId, cell.id, -1)}>↑</button>
        <button className="q-btn h-[20px] px-1.5" disabled={index === count - 1} title="Move down" aria-label="Move cell down" onClick={() => shiftCell(tabId, cell.id, 1)}>↓</button>
        <button className="q-btn h-[20px] px-1.5" title="Add a SQL cell below" onClick={() => addCell(tabId, "sql", cell.id)}>+ SQL</button>
        <button className="q-btn h-[20px] px-1.5" title="Add a markdown cell below" onClick={() => addCell(tabId, "markdown", cell.id)}>+ Text</button>
        <button className="q-btn h-[20px] px-1.5" disabled={busy} title="Delete cell" aria-label="Delete cell" onClick={() => removeCell(tabId, cell.id)}>
          <Icon name="close" size={11} strokeWidth={2.2} />
        </button>
      </div>
    </div>
  );
}

function SqlBody(p: CellProps) {
  return p.focused && p.visible ? <SqlEditor {...p} /> : <SqlStatic {...p} />;
}

/** The shared editor, moved into this cell and sized to its text. */
function SqlEditor({ tabId, cell, groupId }: CellProps) {
  const box = useRef<HTMLDivElement>(null);
  const fontSize = useSettings((s) => s.editorFontSize);
  const [height, setHeight] = useState(() => heightFor(cellSource(tabId, cell).split("\n").length, fontSize));
  useLayoutEffect(() => {
    if (!box.current) return;
    cellModel(tabId, cell);
    const editor = attachEditor(groupId, box.current, cellKey(tabId, cell.id), false, true);
    if (!(document.activeElement instanceof HTMLInputElement) && !(document.activeElement instanceof HTMLTextAreaElement && !document.activeElement.closest(".monaco-editor"))) editor.focus();
    const fit = () => {
      const h = editor.getContentHeight?.();
      if (h) setHeight(Math.max(heightFor(MIN_LINES, fontSize), Math.min(h, Math.round(window.innerHeight * 0.6))));
    };
    fit();
    const sub = editor.onDidContentSizeChange?.(fit);
    return () => sub?.dispose();
    // The cell keeps its id; re-attach when the notebook is shown again or moves to another group.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, cell.id, groupId, fontSize]);
  return <div ref={box} className="relative" style={{ height }} data-q-cell-editor />;
}

/** A SQL cell without the editor: its text in the editor's colours (Monaco's colouriser escapes the text). */
function SqlStatic({ tabId, cell, engine }: CellProps) {
  const source = cellSource(tabId, cell);
  const fontSize = useSettings((s) => s.editorFontSize);
  const [html, setHtml] = useState<{ for: string; html: string } | null>(null);
  const el = useRef<HTMLDivElement>(null);
  const inView = useInView(el);
  const lang = languageFor(engine);
  useEffect(() => {
    if (!inView || !source || html?.for === `${lang}\n${source}`) return;
    let stale = false;
    void monaco.editor.colorize(source, lang, { tabSize: 4 }).then((h) => !stale && setHtml({ for: `${lang}\n${source}`, html: h }), () => {});
    return () => {
      stale = true;
    };
  }, [inView, source, lang, html?.for]);
  const style = { fontSize, lineHeight: `${Math.round(fontSize * 1.7)}px`, minHeight: heightFor(MIN_LINES, fontSize) };
  return (
    // Clicking the text (or Enter on it) moves the editor here; clicking the output does not.
    <div
      ref={el}
      role="button"
      tabIndex={0}
      aria-label={`Edit the SQL of this cell${source ? `: ${source.slice(0, 60)}` : ""}`}
      // Monaco's class and colors: the colouriser's token classes read correctly on the editor's own background, the
      // same pairing the syntax themes are checked for (4.5:1), whatever the app theme.
      className="monaco-editor px-2.5 py-1.5 cursor-text whitespace-pre overflow-x-auto rounded text-left outline-none focus-visible:ring-2 focus-visible:ring-accent"
      style={{ ...style, fontFamily: "var(--font-mono)", background: "var(--vscode-editor-background, var(--q-bg))", color: "var(--vscode-editor-foreground, var(--q-fg))" }}
      onClick={() => focusCell(tabId, cell.id)}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          focusCell(tabId, cell.id);
        }
      }}
      data-q-cell-static
    >
      {!source ? (
        <span className="text-muted">Empty SQL cell — click to write a query</span>
      ) : html?.for === `${lang}\n${source}` ? (
        <div dangerouslySetInnerHTML={{ __html: html.html }} />
      ) : (
        source
      )}
    </div>
  );
}

function MarkdownBody({ tabId, cell, focused }: CellProps) {
  const area = useRef<HTMLTextAreaElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  /** What was typed and not yet in the store (the textarea can go before it blurs: Run all, Shift+Enter). */
  const typed = useRef<string | undefined>(undefined);
  const commit = () => {
    clearTimeout(timer.current);
    if (typed.current !== undefined) setCellSource(tabId, cell.id, typed.current);
    typed.current = undefined;
  };
  const commitRef = useRef(commit);
  commitRef.current = commit;
  // The textarea goes when editing ends (Run, Run all, Shift+Enter) or the cell goes: what was typed lands first.
  useEffect(() => () => commitRef.current(), [cell.editing]);
  useEffect(() => {
    if (cell.editing && focused && area.current && document.activeElement !== area.current) area.current.focus();
  }, [cell.editing, focused]);
  if (cell.editing) {
    const lines = Math.max(3, cell.source.split("\n").length + 1);
    return (
      <textarea
        ref={area}
        aria-label="Markdown"
        className="q-input w-full q-mono text-[12px] leading-[1.6] resize-y py-1.5"
        rows={Math.min(lines, 30)}
        defaultValue={cell.source}
        placeholder="Write notes in Markdown: # heading, **bold**, - lists, `code`, [links](https://…)"
        // Typing does not re-render the notebook: the text reaches the store when typing pauses and on blur.
        onChange={(e) => {
          typed.current = e.target.value;
          clearTimeout(timer.current);
          timer.current = setTimeout(commit, 500);
        }}
        onBlur={commit}
        onFocus={() => focusCell(tabId, cell.id)}
      />
    );
  }
  return (
    <MarkdownView
      source={cell.source}
      onEdit={() => {
        focusCell(tabId, cell.id);
        useNotebooks.getState().patchCell(tabId, cell.id, { editing: true });
      }}
    />
  );
}

function MarkdownView({ source, onEdit }: { source: string; onEdit: () => void }) {
  if (!source.trim()) {
    return <div className="text-muted text-xs px-1 py-1.5 cursor-text" onDoubleClick={onEdit}>Empty text cell — double-click or Edit to write.</div>;
  }
  return (
    <div
      className="q-markdown px-1 py-1 text-[13px] leading-relaxed"
      onDoubleClick={onEdit}
      onClick={(e) => {
        // Links are never followed inside the app: the address is copied to open in a browser.
        const a = (e.target as HTMLElement).closest?.("a[data-q-link]");
        if (!a) return;
        e.preventDefault();
        const href = a.getAttribute("data-q-link") ?? "";
        void navigator.clipboard?.writeText(href).then(() => toast("info", `Link copied: ${href}`), () => toast("info", href));
      }}
      dangerouslySetInnerHTML={{ __html: renderMarkdown(source) }}
    />
  );
}

/** True once the element has been on screen (IntersectionObserver; always true where there is none). */
function useInView(el: React.RefObject<HTMLElement | null>): boolean {
  const [seen, setSeen] = useState(typeof IntersectionObserver === "undefined");
  useEffect(() => {
    if (seen || !el.current) return;
    const io = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        setSeen(true);
        io.disconnect();
      }
    }, { rootMargin: "400px" });
    io.observe(el.current);
    return () => io.disconnect();
  }, [seen, el]);
  return seen;
}

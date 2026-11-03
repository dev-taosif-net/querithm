// The Messages view of the bottom panel (§12 bottom panel, §11 Query statistics): the run's log in the order things
// happened — server messages, errors with their editor line, and a marker where each result set began — then the run
// summary, STATISTICS IO / TIME and Diagnostics as collapsible sections. The ledger is the same as Results': picking a
// chip scrolls to and marks its place in the log.
import { memo, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTabs, type TabState } from "../store/tabs";
import { useExecutions, type ExecutionView } from "../store/executions";
import { useProfiles } from "../store/profiles";
import { engineTraits } from "../engines";
import { toast } from "../store/ui";
import { Icon } from "../app/icons";
import { Chips, PanelFooter, PanelRow, Seg, SplitButton, usePanelWidth } from "../app/panelKit";
import { executionData, type ExecutionData } from "./model";
import { isFinal, ledgerItems, previousRun, selectPatch, selectedItem, type LedgerItem } from "./ledger";
import { RunNotice } from "./RunNotice";
import { revealEditorLine } from "./reveal";
import { useThrottledVersion } from "./useThrottledVersion";
import { chipOf, Empty } from "./ResultsPanel";
import { errorsIn, header, MessageLogIndex, setMarks, timeOfDay as time, type Entry } from "./messageLog";

/** Logs longer than this render a window of entries (the 20,000-message cap would otherwise be 20,000 DOM nodes). */
export const WINDOW_AFTER = 400;
const BLOCK = 50;

export function MessagesPanel({ tab }: { tab: TabState }) {
  const executionId = tab.activeExecutionId;
  const view = useExecutions((s) => (executionId ? s.views[executionId] : undefined));
  const data = executionId ? executionData.get(executionId) : undefined;
  useThrottledVersion(data);
  const prevId = previousRun(tab, executionId);
  const prevView = useExecutions((s) => (prevId ? s.views[prevId] : undefined));
  const width = usePanelWidth();
  const [errorsOnly, setErrorsOnly] = useState(false);
  if (!view) return <Empty text="Messages from the server (PRINT, RAISERROR, NOTICE, errors, row counts) appear here." />;
  const items = ledgerItems(view, data, prevId ? { view: prevView, data: executionData.get(prevId) } : undefined);
  const item = selectedItem(items, tab);
  const errors = errorsIn(view.messages).list.length;

  const copyAll = async (onlyErrors: boolean) => {
    const lines: string[] = [];
    for (const m of view.messages) {
      if (onlyErrors && m.severity !== "error") continue;
      const h = header(m);
      lines.push(`[${time(m.ts)}] ${h ? `${h.replace(/ \(editor line \d+\)$/, "")}: ` : ""}${m.message}`);
    }
    if (!onlyErrors && isFinal(view)) lines.push(`[${time(view.startedAt + (view.elapsedMs ?? 0))}] ${summaryText(view)}`);
    if (!lines.length) return toast("info", onlyErrors ? "No errors to copy." : "No messages to copy.");
    try {
      await navigator.clipboard.writeText(lines.join("\n"));
      toast("success", onlyErrors ? "Copied the errors." : "Copied messages to clipboard.");
    } catch (error) {
      toast("error", error instanceof Error ? error.message : "Could not copy messages.");
    }
  };
  const clear = () => tab.activeExecutionId && useExecutions.getState().patch(tab.activeExecutionId, { messages: [], statistics: [] });
  const select = (k: string) => {
    const it = items.find((i) => i.key === k);
    if (it) useTabs.getState().update(tab.id, selectPatch(it));
  };

  return (
    <div className="flex-1 flex flex-col min-h-0" data-q-messages-view>
      <PanelRow
        label="Message tools"
        ledger={<Chips label="Result sets and errors" chips={items.map(chipOf)} selected={item?.key} onSelect={select} />}
        tools={
          <>
            <Seg
              label="Show"
              value={errorsOnly ? "errors" : "all"}
              compact={false}
              options={[
                { value: "all", label: `All ${view.messages.length.toLocaleString()}` },
                { value: "errors", label: `Errors ${errors.toLocaleString()}`, icon: "errorCircle" },
              ]}
              onChange={(v) => setErrorsOnly(v === "errors")}
            />
            <SplitButton face="Copy all" icon="copy" compact={width === "narrow"} onFace={() => void copyAll(false)} title="Copy all messages" menuLabel="Copy options" items={() => [{ label: "Copy errors only", run: () => void copyAll(true) }]} />
            <button type="button" className="q-btn h-[22px] px-2 shrink-0" onClick={clear} title="Clear messages">Clear</button>
          </>
        }
      />
      <RunNotice tab={tab} view={view} data={data} skip={["error"]} />
      <MessageLog tab={tab} view={view} data={data} errorsOnly={errorsOnly} selected={item} />
      <PanelFooter
        left={`${view.messages.length.toLocaleString()} ${view.messages.length === 1 ? "message" : "messages"}${errors ? ` · ${errors} ${errors === 1 ? "error" : "errors"}` : ""}`}
        right={item ? `Marked: ${item.kind === "set" ? `result set ${item.resultIndex + 1}` : `error ${item.errorIndex + 1}`}` : undefined}
      />
    </div>
  );
}

function summaryText(view: ExecutionView): string {
  if (view.notRun) return "Not run";
  const verb = view.state === "done" ? "Completed" : view.state === "failed" ? "Completed with errors" : "Cancelled by user";
  return `${verb}${view.affectedRows != null ? ` · ${view.affectedRows.toLocaleString()} row(s) affected` : ""} · client ${fmtMs(view.elapsedMs)}${view.serverElapsedMs != null ? ` · server ${fmtMs(view.serverElapsedMs)}` : ""}`;
}

function MessageLog({ tab, view, data, errorsOnly, selected }: { tab: TabState; view: ExecutionView; data: ExecutionData | undefined; errorsOnly: boolean; selected?: LedgerItem }) {
  // Lines and offsets are extended per batch (§14 #36frames): a 20,000-line log is not listed again per notification.
  const index = useRef<MessageLogIndex>(null);
  index.current ??= new MessageLogIndex();
  const { entries, offsets } = index.current.sync(view.messages, setMarks(view, data), errorsOnly);
  const rowsOf = new Map(data?.resultSets.map((rs) => [rs.index, rs.rowCount]));
  const scroller = useRef<HTMLDivElement>(null);
  const windowed = entries.length > WINDOW_AFTER;
  // The window moves in blocks of 50 entries: scrolling inside a block renders nothing new.
  const [block, setBlock] = useState(0);
  const onScroll = () => {
    if (!windowed || !scroller.current) return;
    const top = scroller.current.scrollTop;
    let lo = 0, hi = offsets.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (offsets[mid] <= top) lo = mid;
      else hi = mid - 1;
    }
    const b = Math.floor(lo / BLOCK);
    if (b !== block) setBlock(b);
  };
  const markedKey = selected?.key;
  // The marked entry is scrolled into view when the ledger selection changes.
  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el || !markedKey) return;
    const i = entries.findIndex((e) => e.key === markedKey);
    if (i < 0) return;
    if (windowed) {
      el.scrollTop = Math.max(0, offsets[i] - 24);
      setBlock(Math.floor(i / BLOCK));
    } else el.querySelector(`[data-q-entry="${markedKey}"]`)?.scrollIntoView?.({ block: "nearest" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [markedKey]);
  useEffect(() => setBlock(0), [view.id]);

  const from = windowed ? Math.max(0, (block - 1) * BLOCK) : 0;
  const to = windowed ? Math.min(entries.length, (block + 3) * BLOCK) : entries.length;
  const done = isFinal(view);
  const rows = entries.slice(from, to).map((e) => <EntryLine key={e.key} tabId={tab.id} entry={e} rows={e.kind === "set" ? rowsOf.get(e.index) : undefined} marked={e.key === markedKey} />);

  return (
    <div ref={scroller} onScroll={onScroll} className="flex-1 overflow-auto q-mono text-[12px] py-2 select-text min-h-0" role="log" aria-label="Messages" data-q-messages>
      {entries.length === 0 && !done && <div className="px-3 text-muted italic">No messages to display.</div>}
      {windowed ? (
        <div style={{ height: offsets[offsets.length - 1], position: "relative" }}>
          <div style={{ position: "absolute", top: offsets[from], left: 0, right: 0 }}>{rows}</div>
        </div>
      ) : (
        rows
      )}
      {done && !errorsOnly && (
        <div className={`px-3 py-0.5 ${view.notRun ? "text-muted" : view.state === "failed" ? "text-err" : view.state === "cancelled" ? "text-warn" : "text-ok"}`}>
          <span className="text-muted mr-2">{time(view.startedAt + (view.elapsedMs ?? 0))}</span>
          {view.notRun ? "Not run" : (
            <>
              {view.state === "done" ? "Completed" : view.state === "failed" ? "Completed with errors" : "Cancelled by user"}
              {view.affectedRows != null ? ` · ${view.affectedRows.toLocaleString()} row(s) affected` : ""} · client {fmtMs(view.elapsedMs)}
              {view.serverElapsedMs != null ? ` · server ${fmtMs(view.serverElapsedMs)} (CPU ${fmtMs(view.serverCpuMs)})` : ""}
            </>
          )}
        </div>
      )}
      {done && !errorsOnly && <RunSections tab={tab} view={view} data={data} />}
    </div>
  );
}

/** One line; memoized, so a batch renders only the lines it added to the shown window. */
const EntryLine = memo(function EntryLine({ tabId, entry: e, rows = 0, marked }: { tabId: string; entry: Entry; rows?: number; marked: boolean }) {
  const base = `px-3 py-0.5 border-l-2 ${marked ? "border-accent bg-accent/10 q-hl" : "border-transparent"}`;
  if (e.kind === "set") {
    return (
      <div className={`${base} flex items-center gap-2 text-muted font-sans text-[11.5px]`} data-q-entry={e.key}>
        <Icon name="results" size={12} strokeWidth={2} />
        <span>Result set {e.index + 1} began</span>
        <span className="q-mono">· {rows.toLocaleString()} {rows === 1 ? "row" : "rows"}</span>
      </div>
    );
  }
  const m = e.m;
  const h = header(m);
  return (
    <div
      data-q-entry={e.key}
      onClick={() => revealEditorLine(tabId, m.editorLine)}
      className={`${base} whitespace-pre-wrap ${m.editorLine ? "cursor-pointer hover:bg-raised" : ""}`}
    >
      <span className="text-muted mr-2">{time(m.ts)}</span>
      {h && (
        <span className="text-err">
          {h}
          {"\n"}
        </span>
      )}
      <span className={m.severity === "error" ? "text-err" : m.severity === "warning" ? "text-warn" : "text-fg"}>{m.message}</span>
    </div>
  );
});

/** STATISTICS IO / TIME (SQL Server) and Diagnostics, collapsed to one summary line each. */
function RunSections({ tab, view, data }: { tab: TabState; view: ExecutionView; data: ExecutionData | undefined }) {
  const engine = useProfiles((s) => s.profiles.find((p) => p.id === tab.profileId)?.engine);
  const traits = engineTraits(engine);
  const io = view.statistics.filter((s) => s.kind === "io");
  const times = view.statistics.filter((s) => s.kind !== "io");
  const reads = io.reduce((n, s) => n + (s.logicalReads ?? 0), 0);
  const cpu = times.reduce((n, s) => n + (s.cpuMs ?? 0), 0);
  const sets = data?.resultSets ?? [];
  const batches = sets.reduce((n, s) => n + s.batches.length, 0);
  const summary = "px-3 py-0.5 font-sans text-[11.5px] text-muted cursor-pointer select-none hover:text-fg";
  return (
    <div className="pt-2 space-y-0.5">
      {io.length > 0 && (
        <details>
          <summary className={summary}><b className="text-fg font-semibold">STATISTICS IO</b> · {io.length} {io.length === 1 ? "table" : "tables"} · {reads.toLocaleString()} logical reads</summary>
          <table className="ml-3 my-1 text-[11px] border border-line">
            <thead className="bg-panel text-muted">
              <tr>
                {["Batch", "Table", "Scans", "Logical reads", "Physical reads", "Read-ahead", "LOB logical"].map((h) => (
                  <th key={h} scope="col" className="px-2 py-1 text-left font-semibold border-b border-line">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {io.map((s, i) => (
                <tr key={i} className="border-b border-line/50">
                  <td className="px-2 py-0.5">{s.batchIndex + 1}</td>
                  <td className="px-2 py-0.5 text-fg">{s.table}</td>
                  <td className="px-2 py-0.5 text-right">{s.scanCount?.toLocaleString()}</td>
                  <td className="px-2 py-0.5 text-right text-warn">{s.logicalReads?.toLocaleString()}</td>
                  <td className="px-2 py-0.5 text-right">{s.physicalReads?.toLocaleString()}</td>
                  <td className="px-2 py-0.5 text-right">{s.readAheadReads?.toLocaleString()}</td>
                  <td className="px-2 py-0.5 text-right">{s.lobLogicalReads?.toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
      {times.length > 0 && (
        <details>
          <summary className={summary}><b className="text-fg font-semibold">STATISTICS TIME</b> · {times.length} {times.length === 1 ? "phase" : "phases"} · CPU {cpu.toLocaleString()} ms</summary>
          <table className="ml-3 my-1 text-[11px] border border-line">
            <thead className="bg-panel text-muted">
              <tr>
                {["Batch", "Phase", "CPU ms", "Elapsed ms"].map((h) => (
                  <th key={h} scope="col" className="px-2 py-1 text-left font-semibold border-b border-line">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {times.map((s, i) => (
                <tr key={i} className="border-b border-line/50">
                  <td className="px-2 py-0.5">{s.batchIndex + 1}</td>
                  <td className="px-2 py-0.5">{s.kind === "compile" ? "parse & compile" : "execution"}</td>
                  <td className="px-2 py-0.5 text-right">{s.cpuMs}</td>
                  <td className="px-2 py-0.5 text-right text-ok">{s.elapsedMs}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </details>
      )}
      {!traits.statisticsIo && !view.notRun && (
        <div className="px-3 py-0.5 font-sans text-[11.5px] text-muted" data-q-capability="statistics">
          Read statistics are SQL Server only (STATISTICS IO / TIME); on {traits.shortLabel}, Explain › Actual shows the work per operator.
        </div>
      )}
      {sets.length > 0 && (
        <details>
          <summary className={summary}><b className="text-fg font-semibold">Diagnostics</b> · Arrow stream · {batches} {batches === 1 ? "batch" : "batches"} · {sets.length} {sets.length === 1 ? "result set" : "result sets"}</summary>
          <div className="px-6 py-1 text-[11px] text-muted space-y-0.5">
            {sets.map((s) => (
              <div key={s.index}>Result set {s.index + 1}: {s.rowCount.toLocaleString()} rows · {s.columns.length} columns · {s.batches.length} {s.batches.length === 1 ? "batch" : "batches"}{s.truncated ? " · row limit" : ""}{s.interrupted ? " · stopped" : ""}</div>
            ))}
          </div>
        </details>
      )}
    </div>
  );
}

export function fmtMs(ms?: number | null) {
  if (ms == null) return "—";
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(2)} s`;
  const m = Math.floor(ms / 60_000);
  return `${m}:${String(Math.floor((ms % 60_000) / 1000)).padStart(2, "0")} min`;
}

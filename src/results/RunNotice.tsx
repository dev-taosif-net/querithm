// The notice line (§12 bottom panel): 24 px under the panel's row, only when there is something to know about the
// shown run. One entry at a time, most important first ("1 of 2 ▾" steps through the rest); the error and row-limit
// entries are added when the run ends, so the line never moves the grid while rows stream or the user scrolls.
import { useState } from "react";
import { useTabs, type TabState } from "../store/tabs";
import type { ExecutionView } from "../store/executions";
import { Icon } from "../app/icons";
import { showRun, runQuery, setBottomTab } from "../app/actions";
import { lineOf, noticesFor, type Notice } from "./ledger";
import { revealEditorLine } from "./reveal";
import type { ExecutionData } from "./model";

const clock = (ts: number) => new Date(ts).toLocaleTimeString(undefined, { hour12: false });

/** `skip`: entries a view already shows itself (Messages lists the errors in its log). */
export function RunNotice({ tab, view, data, skip = [] }: { tab: TabState; view: ExecutionView | undefined; data: ExecutionData | undefined; skip?: Notice["kind"][] }) {
  const [at, setAt] = useState(0);
  const all = noticesFor(tab, view, data).filter((n) => !skip.includes(n.kind));
  // A dismissed line returns on the next run; an older run's entry stays (everything on screen is from then).
  const list = tab.noticeDismissed && tab.noticeDismissed === view?.id ? all.filter((n) => n.kind === "older") : all;
  if (!list.length || !view) return null;
  const n = list[Math.min(at, list.length - 1)];
  const tone = n.kind === "error" ? "err" : "warn";
  return (
    <div
      role="status"
      data-q-notice={n.kind}
      className={`h-[24px] pl-2 pr-1 flex items-center gap-2 text-[11.5px] shrink-0 whitespace-nowrap overflow-hidden border-b q-hl ${tone === "err" ? "bg-err/10 border-err/35 text-fg" : "bg-warn/10 border-warn/35 text-fg"}`}
    >
      <NoticeBody tab={tab} notice={n} />
      <span className="flex-1" />
      {list.length > 1 && (
        <button type="button" className="q-mono text-[10.5px] text-muted hover:text-fg flex items-center gap-0.5 shrink-0" title="Next notice" onClick={() => setAt((at + 1) % list.length)}>
          {(at % list.length) + 1} of {list.length}
          <Icon name="chevron" size={11} strokeWidth={2} />
        </button>
      )}
      {n.kind !== "older" && (
        <button
          type="button"
          className="w-5 h-5 rounded flex items-center justify-center text-muted hover:text-fg hover:bg-hover shrink-0"
          aria-label="Dismiss notice"
          title="Dismiss (it returns on the next run that needs it)"
          onClick={() => useTabs.getState().update(tab.id, { noticeDismissed: view.id })}
        >
          <Icon name="close" size={12} strokeWidth={2} />
        </button>
      )}
    </div>
  );
}

function NoticeBody({ tab, notice: n }: { tab: TabState; notice: Notice }) {
  const link = "text-accent font-semibold hover:underline shrink-0 flex items-center gap-1";
  if (n.kind === "older") {
    const latest = tab.executions[tab.executions.length - 1];
    return (
      <>
        <span className="font-semibold text-warn flex items-center gap-1 shrink-0"><Icon name="history" size={12} strokeWidth={2} />Older run</span>
        <span className="truncate min-w-0">Run {n.position} of {n.count}, from {clock(n.startedAt)}: read-only, and not what the database holds now.</span>
        {latest && <button type="button" className={link} onClick={() => showRun(tab.id, latest)}>Back to latest</button>}
      </>
    );
  }
  if (n.kind === "error") {
    const m = n.message;
    const line = lineOf(m);
    const code = m.number != null ? `Msg ${m.number}${m.class != null ? `, Level ${m.class}` : ""} · ` : "";
    return (
      <>
        <span className="font-semibold text-err flex items-center gap-1 shrink-0">
          <Icon name="errorCircle" size={12} strokeWidth={2} />
          {n.count > 1 ? `${n.count} errors` : "Error"}{line ? ` at line ${line}` : ""}
        </span>
        <span className="truncate min-w-0" title={m.message}>{code}{m.message.split("\n")[0]}</span>
        {m.editorLine && (
          <button type="button" className={link} onClick={() => revealEditorLine(tab.id, m.editorLine)}>
            <Icon name="goToLine" size={12} strokeWidth={2} />Go to line {m.editorLine}
          </button>
        )}
        {tab.bottomTab !== "messages" && <button type="button" className={link} onClick={() => setBottomTab(tab.id, "messages")}>Show in Messages</button>}
      </>
    );
  }
  return (
    <>
      <span className="font-semibold text-warn flex items-center gap-1 shrink-0"><Icon name="alert" size={12} strokeWidth={2} />Row limit</span>
      <span className="truncate min-w-0">Result set {n.resultIndex + 1} shows its first {n.rows.toLocaleString()} rows; Copy and Export cover only these.</span>
      <button
        type="button"
        className={link}
        onClick={() => {
          useTabs.getState().update(tab.id, { maxRows: 0 });
          void runQuery(tab.id, { options: { maxRows: 0 } });
        }}
      >
        Fetch all rows
      </button>
    </>
  );
}

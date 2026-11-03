// Engine-neutral debugger UI (§11): toolbar, Variables, Call Stack, output. Only the PostgreSQL pldebugger
// adapter exists in v1; SQL Server shows Trace instead and never a debugger.
import { useEffect, useRef, type ReactNode } from "react";
import type { TabState } from "../store/tabs";
import { useTabs } from "../store/tabs";
import { useDiag } from "../store/diag";
import { rpc } from "../ipc/client";
import { editorForTab } from "../editor/editors";
import { monaco } from "../editor/monaco";
import { toast } from "../store/ui";
import { Empty } from "../results/ResultsPanel";

export async function stepDebug(tabId: string, kind: "over" | "into" | "continue") {
  const t = useTabs.getState().tabs[tabId];
  if (!t?.debugId) return;
  await rpc("debug.step", { debugId: t.debugId, kind }).catch((e) => toast("error", (e as Error).message));
}

export async function stopDebug(tabId: string) {
  const t = useTabs.getState().tabs[tabId];
  if (!t?.debugId) return;
  await rpc("debug.detach", { debugId: t.debugId }).catch(() => {});
  useDiag.getState().patchDebug(t.debugId, { state: "finished", line: null });
  useTabs.getState().update(tabId, { debugId: undefined });
}

/** Keeps the current-line and breakpoint decorations of the debugged tab in sync. */
function useDebugDecorations(tab: TabState) {
  const dbg = useDiag((s) => (tab.debugId ? s.debugs[tab.debugId] : undefined));
  const deco = useRef<monaco.editor.IEditorDecorationsCollection | null>(null);
  useEffect(() => {
    const ed = editorForTab(tab.id);
    if (!ed) return;
    deco.current?.clear();
    if (!dbg) return;
    const items: monaco.editor.IModelDeltaDecoration[] = dbg.breakpoints.map((l) => ({
      range: new monaco.Range(l, 1, l, 1), options: { glyphMarginClassName: "q-breakpoint", glyphMarginHoverMessage: { value: "Breakpoint" } },
    }));
    if (dbg.line && dbg.currentOid === dbg.functionOid) {
      items.push({ range: new monaco.Range(dbg.line, 1, dbg.line, 1), options: { isWholeLine: true, className: "q-debug-line" } });
      ed.revealLineInCenterIfOutsideViewport(dbg.line);
    }
    deco.current = ed.createDecorationsCollection(items);
  }, [tab.id, dbg?.line, dbg?.breakpoints, dbg?.currentOid, dbg?.functionOid, dbg]);
}

/** `lead`: the Runtime view's Trace | Debug switch, at the start of the tool row. */
export function DebugPanel({ tab, lead }: { tab: TabState; lead?: ReactNode }) {
  const dbg = useDiag((s) => (tab.debugId ? s.debugs[tab.debugId] : undefined));
  useDebugDecorations(tab);
  if (!dbg) {
    return (
      <Empty text="PostgreSQL step debugging: right-click a function or procedure in the Object Explorer → Debug. Requires the pldebugger extension (pldbgapi) on the server." />
    );
  }
  const busy = dbg.state === "running" || dbg.state === "attaching";
  const live = dbg.state === "paused" || busy;
  return (
    <div className="flex-1 flex flex-col min-h-0">
      <div className="h-[28px] px-2 border-b border-line flex items-center gap-2 text-[11px] shrink-0 whitespace-nowrap overflow-hidden">
        {lead}
        <button className="q-btn q-btn-run" disabled={!live || busy} onClick={() => void stepDebug(tab.id, "continue")} title="Continue (F5)">▶ Continue</button>
        <button className="q-btn" disabled={!live || busy} onClick={() => void stepDebug(tab.id, "over")} title="Step over (F10)">↷ Step over</button>
        <button className="q-btn" disabled={!live || busy} onClick={() => void stepDebug(tab.id, "into")} title="Step into (F11)">↓ Step into</button>
        <button className="q-btn q-btn-danger" disabled={!live} onClick={() => void stopDebug(tab.id)} title="Stop (Shift+F5)">■ Stop</button>
        <span className={dbg.state === "error" ? "text-err" : dbg.state === "finished" ? "text-muted" : "text-ok"}>
          {dbg.state === "paused" ? `Paused at line ${dbg.line}` : dbg.state === "running" ? "Running…" : dbg.state === "attaching" ? "Waiting for the target to reach the function…" : dbg.state === "finished" ? "Finished" : "Error"}
        </span>
        <div className="flex-1" />
        <span className="text-muted">Click the editor gutter to toggle breakpoints · pldebugger</span>
      </div>
      <div className="flex-1 flex min-h-0 text-[12px]">
        <div className="flex-1 overflow-auto border-r border-line">
          <div className="px-3 py-1 text-[10px] font-bold uppercase text-muted bg-panel-2 sticky top-0">Variables</div>
          <table className="w-full q-mono">
            <tbody>
              {dbg.variables.map((v) => (
                <tr key={v.name} className="border-b border-line/50">
                  <td className="px-3 py-0.5 text-ident">{v.name}</td>
                  <td className="px-3 py-0.5 text-muted">{v.type}</td>
                  <td className="px-3 py-0.5">{v.isNull ? <span className="text-muted italic">NULL</span> : v.value}</td>
                  <td className="px-3 py-0.5 text-muted">{v.varClass === "A" ? "arg" : "local"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="w-[38%] overflow-auto">
          <div className="px-3 py-1 text-[10px] font-bold uppercase text-muted bg-panel-2 sticky top-0">Call stack</div>
          {dbg.stack.map((f) => (
            <div key={f.level} className="px-3 py-0.5 q-mono border-b border-line/50">
              <span className="text-fg">{f.function}</span> <span className="text-accent">line {f.line}</span>
              {f.args && <div className="text-muted text-[11px]">{f.args}</div>}
            </div>
          ))}
          {dbg.log.length > 0 && (
            <>
              <div className="px-3 py-1 text-[10px] font-bold uppercase text-muted bg-panel-2">Output</div>
              {dbg.log.map((l, i) => (
                <div key={i} className="px-3 py-0.5 q-mono whitespace-pre-wrap">{l}</div>
              ))}
            </>
          )}
        </div>
      </div>
    </div>
  );
}

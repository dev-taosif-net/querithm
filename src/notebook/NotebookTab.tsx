// A notebook tab (Phase C3): a toolbar, the notices (no connection, file warnings, results saved in the file) and
// the cells. One session per notebook, opened on the first run, like a query tab's; the top bar's connection and
// database pickers, Run and Cancel act on it.
import { useRef, useState } from "react";
import { useTabs } from "../store/tabs";
import { useProfiles } from "../store/profiles";
import { useSettings } from "../store/settings";
import { useUi, toast } from "../store/ui";
import { DropMenu } from "../app/ToolbarMenu";
import { eventToShortcut } from "../app/shortcuts";
import { Icon } from "../app/icons";
import { cancelQuery } from "../app/actions";
import { Cell } from "./Cell";
import { addCell, runAll, runCell, setSaveOutputs } from "./runtime";
import { saveNotebook } from "./files";
import { useNotebooks } from "./store";

export function NotebookTab({ tabId, groupId, visible }: { tabId: string; groupId: string; visible: boolean }) {
  const doc = useNotebooks((s) => s.docs[tabId]);
  const tab = useTabs((s) => s.tabs[tabId]);
  const engine = useProfiles((s) => s.profiles.find((p) => p.id === tab?.profileId)?.engine);
  const root = useRef<HTMLDivElement>(null);
  if (!doc || !tab) return null;

  // Shift+F5 runs every cell, Shift+Enter runs the focused cell and moves on; F5 / Ctrl+Enter reach the app's Run,
  // which runs the focused cell (commands.ts NOTEBOOK_KEYS). Captured before the cell's editor sees them.
  const onKeyDownCapture = (e: React.KeyboardEvent) => {
    const sc = eventToShortcut(e.nativeEvent);
    if (sc === "Shift+F5") {
      e.preventDefault();
      e.stopPropagation();
      void runAll(tabId);
    } else if (sc === "Shift+Enter" && doc.focused) {
      e.preventDefault();
      e.stopPropagation();
      void runCell(tabId, doc.focused, true);
    }
  };

  const runningAt = doc.running ? doc.cells.findIndex((c) => c.id === doc.running) + 1 : 0;
  return (
    <div ref={root} className="h-full flex flex-col bg-bg overflow-hidden" data-q-notebook onKeyDownCapture={onKeyDownCapture}>
      <Toolbar tabId={tabId} running={!!doc.running} runningAt={runningAt} saveOutputs={doc.saveOutputs} />
      <Notices tabId={tabId} notices={doc.notices} connected={!!tab.profileId} saveOutputs={doc.saveOutputs} />
      <div className="flex-1 min-h-0 overflow-y-auto px-4 py-3" data-focus-region="editor">
        <div className="max-w-[1400px] mx-auto space-y-2">
          {doc.cells.map((c, i) => (
            <Cell
              key={c.id} tabId={tabId} cell={c} index={i} count={doc.cells.length} focused={doc.focused === c.id} groupId={groupId} visible={visible}
              engine={engine} running={!!doc.running}
            />
          ))}
          <div className="flex items-center justify-center gap-2 py-2">
            <button className="q-btn" onClick={() => addCell(tabId, "sql", doc.cells.at(-1)?.id)}>+ SQL cell</button>
            <button className="q-btn" onClick={() => addCell(tabId, "markdown", doc.cells.at(-1)?.id)}>+ Text cell</button>
          </div>
        </div>
      </div>
    </div>
  );
}

function Toolbar({ tabId, running, runningAt, saveOutputs }: { tabId: string; running: boolean; runningAt: number; saveOutputs: boolean }) {
  const stop = useSettings((s) => s.notebookStopOnError);
  return (
    <div className="min-h-[34px] py-1 px-3 border-b border-line bg-panel flex flex-wrap items-center gap-x-2 gap-y-1 shrink-0 text-[11px]" role="toolbar" aria-label="Notebook" data-q-notebook-toolbar>
      {running ? (
        <button className="q-btn h-[24px] text-err" onClick={() => void cancelQuery(tabId)} title="Cancel the running cell and the cells after it (Ctrl+Shift+C)">
          <Icon name="stop" size={12} /> Cancel
        </button>
      ) : (
        <button className="q-btn h-[24px]" onClick={() => void runAll(tabId)} title={`Run every SQL cell, top to bottom (Shift+F5)${stop ? "; stops at the first failing cell" : ""}`}>
          <Icon name="play" size={11} className="text-ok" /> Run all
        </button>
      )}
      {running && runningAt > 0 && <span className="text-muted" role="status">Running cell {runningAt}…</span>}
      <span className="w-px h-4 bg-line mx-1" aria-hidden />
      <button className="q-btn h-[24px]" onClick={() => addCell(tabId, "sql")} title="Add a SQL cell below the focused cell">+ SQL</button>
      <button className="q-btn h-[24px]" onClick={() => addCell(tabId, "markdown")} title="Add a text (markdown) cell below the focused cell">+ Text</button>
      <div className="ml-auto flex items-center gap-2">
        <label className="flex items-center gap-1.5 cursor-pointer" title="Write each SQL cell's result (first 1,000 rows × 50 columns, as text) into the file. Results are your data: share such a file with care.">
          <input type="checkbox" role="switch" checked={saveOutputs} onChange={(e) => setSaveOutputs(tabId, e.target.checked)} />
          <span>Save results in file</span>
        </label>
        <ExportMenu tabId={tabId} />
        <button className="q-btn h-[24px]" onClick={() => void saveNotebook(tabId)} title="Save the notebook to a .sqlbook file (Ctrl+S)">Save</button>
      </div>
    </div>
  );
}

/** The app's menu (ToolbarMenu: ↑/↓/Home/End, Esc back to the button), not a list of its own (L32). */
function ExportMenu({ tabId }: { tabId: string }) {
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const run = (format: "md" | "html") => {
    void import("./exportNotebook").then((m) => m.exportNotebook(tabId, format)).catch((e) => toast("error", `Could not export the notebook: ${e instanceof Error ? e.message : String(e)}`));
  };
  return (
    <>
      <button ref={trigger} className="q-btn h-[24px]" onClick={() => setOpen(!open)} aria-haspopup="menu" aria-expanded={open}>Export ▾</button>
      {open && (
        <DropMenu
          anchor={trigger}
          label="Export notebook"
          items={[{ label: "Markdown (.md)", run: () => run("md") }, { label: "HTML page (.html)", run: () => run("html") }]}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}

function Notices({ tabId, notices, connected, saveOutputs }: { tabId: string; notices: string[]; connected: boolean; saveOutputs: boolean }) {
  const dismiss = (i: number) => useNotebooks.getState().patch(tabId, (d) => ({ notices: d.notices.filter((_, k) => k !== i) }));
  if (!notices.length && connected && !saveOutputs) return null;
  return (
    <div className="px-3 py-1.5 border-b border-line bg-panel-2 space-y-1 text-[11px] shrink-0" data-q-notebook-notices>
      {!connected && (
        <div className="flex items-center gap-2 text-fg">
          <Icon name="plug" size={12} className="text-muted" />
          <span>This notebook has no connection. Pick one to run its cells.</span>
          <button className="q-btn h-[20px]" onClick={() => useUi.getState().setPalette({ mode: "connections" })}>Pick a connection</button>
        </div>
      )}
      {saveOutputs && (
        <div className="flex items-center gap-2 text-warn" role="note">
          <Icon name="alert" size={12} />
          <span>Results are saved in this file (up to 1,000 rows × 50 columns per cell). They are your data: share the file with care.</span>
        </div>
      )}
      {notices.map((n, i) => (
        <div key={i} className="flex items-center gap-2 text-warn">
          <Icon name="alert" size={12} />
          <span className="flex-1">{n}</span>
          <button className="text-muted hover:text-fg" aria-label="Dismiss" title="Dismiss" onClick={() => dismiss(i)}>
            <Icon name="close" size={11} strokeWidth={2.2} />
          </button>
        </div>
      ))}
    </div>
  );
}

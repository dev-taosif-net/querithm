// Usages tab of the bottom panel: hits grouped by open tab and database object, with line, snippet and
// confidence; objects that cannot be checked are listed apart. A click jumps to the hit.
import { useMemo, useState } from "react";
import type { TabState } from "../store/tabs";
import { useTabs } from "../store/tabs";
import { useUsages } from "../store/usages";
import { useUi } from "../store/ui";
import { groupHits, kindLabel, targetLabel, counts } from "./refactor";
import { revealHit, runFindUsages } from "./refactorActions";
import { editorForTab, getModel } from "../editor/editors";

export function UsagesPanel({ tab }: { tab: TabState }) {
  const view = useUsages((s) => s.byTab[tab.id]);
  const titles = useTabs((s) => s.tabs);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const groups = useMemo(() => (view?.result ? groupHits(view.result, (id) => titles[id]?.title ?? "closed tab") : []), [view?.result, titles]);

  if (!view) return <div className="flex-1 flex items-center justify-center text-muted text-xs">Shift+F12 on a table, column, procedure or function lists its usages here.</div>;
  if (view.local) {
    return (
      <div className="flex-1 overflow-auto text-[12px] p-2" data-q-usages>
        <div className="text-muted mb-1">Local name <b className="text-fg">{view.local.name}</b>: {view.local.spans.length} occurrence(s) in this tab (F2 renames them here).</div>
        {view.local.spans.map((s) => (
          <button key={s.offset} className="w-full text-left flex gap-2 px-2 py-0.5 rounded hover:bg-hover" onClick={() => {
            const ed = editorForTab(tab.id);
            const m = getModel(tab.id);
            if (!ed || !m) return;
            const a = m.getPositionAt(s.offset);
            const b = m.getPositionAt(s.offset + s.length);
            ed.setSelection({ startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column });
            ed.revealLineInCenter(a.lineNumber);
            ed.focus();
          }}>
            <span className="text-muted w-16 shrink-0">{s.line}:{s.column}</span>
            <span className="font-mono truncate">{s.snippet}</span>
          </button>
        ))}
      </div>
    );
  }
  const r = view.result;
  const c = r ? counts(r.hits) : { exact: 0, possible: 0 };
  return (
    <div className="flex-1 flex flex-col min-h-0 text-[12px]" data-q-usages>
      <div className="h-[28px] px-3 border-b border-line flex items-center gap-3 shrink-0">
        {view.target && <span>Usages of {kindLabel(view.target.kind)} <b>{targetLabel(view.target)}</b></span>}
        {view.running && <span className="text-muted">Searching…</span>}
        {r && (
          <span className="text-muted">
            {c.exact} exact{c.possible ? ` · ${c.possible} possible` : ""} · {groups.length} place(s) · {r.objectsScanned} object(s) checked in {Math.round(r.elapsedMs)} ms
          </span>
        )}
        <div className="flex-1" />
        {view.target && tab.profileId && tab.database && (
          <>
            <button className="q-btn h-[22px]" disabled={view.running} onClick={() => void runFindUsages(tab.id, tab.profileId!, tab.database!, view.target!)}>Search again</button>
            <button className="q-btn h-[22px]" disabled={view.running || !r} onClick={() =>
              useUi.getState().openDialog({ kind: "rename", profileId: tab.profileId!, database: tab.database!, target: view.target!, fromTabId: tab.id })}>
              Rename… (F2)
            </button>
          </>
        )}
      </div>
      {view.error && <div className="px-3 py-1.5 text-err">{view.error}</div>}
      <div className="flex-1 overflow-auto p-1">
        {r && r.hits.length === 0 && <div className="p-3 text-muted">No references found.</div>}
        {r && r.serverUpdated.length > 0 && (
          <div className="px-2 py-1 text-muted">Stored parsed by the server (they follow a rename by themselves): {r.serverUpdated.map((o) => `${o.schema}.${o.name}`).join(", ")}</div>
        )}
        {groups.map((g) => (
          <div key={g.key}>
            <button className="w-full text-left px-2 py-0.5 font-semibold flex items-center gap-2 hover:bg-hover rounded"
              onClick={() => setCollapsed((s) => { const n = new Set(s); if (n.has(g.key)) n.delete(g.key); else n.add(g.key); return n; })}>
              <span className="text-muted w-3">{collapsed.has(g.key) ? "▸" : "▾"}</span>
              <span className="text-muted text-[10px] uppercase">{g.source === "tab" ? "tab" : g.hits[0].object?.kind}</span>
              {g.label}
              <span className="text-muted font-normal">{g.hits.length}</span>
            </button>
            {!collapsed.has(g.key) && g.hits.map((h) => (
              <button key={h.id} className="w-full text-left flex gap-2 pl-8 pr-2 py-0.5 rounded hover:bg-hover" title={h.reason ?? undefined}
                onClick={() => void revealHit(h, tab.profileId, tab.database)} data-q-usage-hit={h.id}>
                <span className="text-muted w-16 shrink-0">{h.line}:{h.column}</span>
                <span className="font-mono truncate flex-1">{h.snippet}</span>
                {h.definition && <span className="text-muted text-[10px] shrink-0">definition</span>}
                {h.confidence === "possible" && <span className="text-warn text-[10px] shrink-0">possible</span>}
              </button>
            ))}
          </div>
        ))}
        {r && r.unchecked.length > 0 && (
          <div className="mt-2 px-2">
            <div className="text-warn font-semibold">May reference it, cannot be checked ({r.unchecked.length})</div>
            {r.unchecked.map((u) => (
              <div key={`${u.object.schema}.${u.object.name}`} className="pl-6 text-muted"><span className="text-fg">{u.object.schema}.{u.object.name}</span> — {u.reason}</div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

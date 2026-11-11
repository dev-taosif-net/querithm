// Rename (F2). A local symbol (variable, parameter, CTE, alias) is renamed in the editor; a schema object or
// column gets a preview — every affected object with its hits (possible ones unchecked), a diff per object — and a
// guarded script that opens in a query tab on the database, so the usual Prod and destructive guards apply.
import { useEffect, useRef, useState } from "react";
import type { RenamePreviewResult } from "../ipc/contract";
import { rpc } from "../ipc/client";
import type { Dialog } from "../store/ui";
import { toast, useUi } from "../store/ui";
import { useProfiles } from "../store/profiles";
import { useConnections } from "../store/connections";
import { Modal } from "../app/Modal";
import { ServerChip } from "../app/EnvTag";
import { newQueryTab } from "../app/actions";
import { getModel } from "../editor/editors";
import { initMonaco, monaco } from "../editor/monaco";
import { languageFor } from "../editor/editors";
import { kindLabel, localRenameText, targetLabel } from "./refactor";
import { applyTabEdits, openTabsFor } from "./refactorActions";

type RenameDialogProps = Extract<Dialog, { kind: "rename" }>;

export function RenameDialog({ d }: { d: RenameDialogProps }) {
  return d.local ? <LocalRename d={d} /> : <ObjectRename d={d} />;
}

function LocalRename({ d }: { d: RenameDialogProps }) {
  const l = d.local!;
  const close = () => useUi.getState().closeDialog(d);
  // The name without its @ or brackets/quotes; they come back from localRenameText.
  const initial = l.original.replace(/^[@["]|[\]"]$/g, "");
  const [name, setName] = useState(initial);
  const apply = () => {
    if (!name.trim() || name.trim() === initial) return close();
    const m = getModel(l.tabId);
    if (!m) return close();
    const text = m.getValue();
    applyTabEdits([{ tabId: l.tabId, edits: l.spans.map((s) => ({ offset: s.offset, length: s.length, text: localRenameText(text.slice(s.offset, s.offset + s.length), name, l.dialect) })) }]);
    close();
  };
  return (
    <Modal title={`Rename ${l.original}`} onClose={close} onEnter={apply}
      footer={<><button className="q-btn" onClick={close}>Cancel</button><button className="q-btn q-btn-primary" onClick={apply}>Rename {l.spans.length} occurrence(s)</button></>}>
      <p className="text-muted mb-2">A local name: it is renamed in this tab only (undo with Ctrl+Z).</p>
      <input className="q-input w-full" autoFocus onFocus={(e) => e.target.select()} value={name} onChange={(e) => setName(e.target.value)} aria-label="New name" />
    </Modal>
  );
}

function ObjectRename({ d }: { d: RenameDialogProps }) {
  const target = d.target!;
  const close = () => useUi.getState().closeDialog(d);
  const old = target.column ?? target.name;
  const [name, setName] = useState(old);
  const [preview, setPreview] = useState<RenamePreviewResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [include, setInclude] = useState<Set<string>>(new Set());
  const [exclude, setExclude] = useState<Set<string>>(new Set());
  const [focus, setFocus] = useState<string | null>(null);
  const [updateTabs, setUpdateTabs] = useState(true);
  const engine = useProfiles((s) => s.byId(d.profileId)?.engine);

  const request = async () => {
    const sessionId = await useConnections.getState().sessionFor(d.profileId!);
    return rpc("refactor.previewRename", {
      sessionId, database: d.database!, target, newName: name, tabs: openTabsFor(d.profileId!, d.database!), include: [...include], exclude: [...exclude],
    });
  };

  useEffect(() => {
    if (!name.trim() || name.trim() === old) { setPreview(null); return; }
    let live = true;
    const t = setTimeout(() => {
      setBusy(true);
      request()
        .then((r) => { if (!live) return; setPreview(r); setError(null); setFocus((f) => f ?? r.objects.find((o) => !o.serverUpdated)?.key ?? r.objects[0]?.key ?? null); })
        .catch((e) => live && setError((e as Error).message))
        .finally(() => live && setBusy(false));
    }, 300);
    return () => { live = false; clearTimeout(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [name, include, exclude]);

  const toggle = (id: string, possible: boolean, on: boolean) => {
    if (possible) setInclude((s) => { const n = new Set(s); if (on) n.add(id); else n.delete(id); return n; });
    else setExclude((s) => { const n = new Set(s); if (on) n.delete(id); else n.add(id); return n; });
  };

  const open = async () => {
    try {
      setBusy(true);
      const fresh = await request(); // tab offsets as the tabs are now
      newQueryTab({ profileId: d.profileId, database: d.database, text: fresh.sql, title: "Rename.sql" });
      if (updateTabs) applyTabEdits(fresh.tabs);
      toast("info", "The rename script is in a new tab on the database. Review it, then run it.");
      close();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const focused = preview?.objects.find((o) => o.key === focus);
  const tabEdits = preview?.tabs.reduce((n, t) => n + t.edits.length, 0) ?? 0;
  return (
    <Modal title={`Rename ${kindLabel(target.kind)} ${targetLabel(target)}`} subtitle={<ServerChip profileId={d.profileId} database={d.database} />} onClose={close} width={1100}
      footer={
        <>
          {preview && tabEdits > 0 && (
            <label className="flex items-center gap-1.5 mr-auto text-muted cursor-pointer">
              <input type="checkbox" checked={updateTabs} onChange={(e) => setUpdateTabs(e.target.checked)} /> Also update {tabEdits} reference(s) in open tabs
            </label>
          )}
          <button className="q-btn" onClick={close}>Cancel</button>
          <button className="q-btn q-btn-primary" disabled={!preview || busy || !!error} onClick={() => void open()}>Open script in a query tab</button>
        </>
      }>
      <div className="flex flex-col gap-2 text-[12px]">
        <label className="flex items-center gap-2">New name <input className="q-input flex-1" autoFocus onFocus={(e) => e.target.select()} value={name} onChange={(e) => setName(e.target.value)} aria-label="New name" /></label>
        {busy && <div className="text-muted">Finding every reference…</div>}
        {error && <div className="text-err" role="alert">{error}</div>}
        {preview && (
          <>
            {!preview.transactional && <div className="text-warn">This engine commits DDL as it goes: the script cannot roll back if a step fails.</div>}
            {preview.warnings.map((w, i) => <div key={i} className="text-warn">{w}</div>)}
            <div className="flex gap-2 h-[420px]">
              <div className="w-[340px] shrink-0 overflow-auto border border-line rounded" data-q-rename-objects>
                {preview.objects.length === 0 && (
                  <div className="p-3 text-muted">No database object references it: only the {kindLabel(target.kind)} itself is renamed{tabEdits > 0 ? `, plus ${tabEdits} reference(s) in open tabs` : ""}.</div>
                )}
                {preview.objects.map((o) => (
                  // q-hl: muted text in the selected group keeps 4.5:1 on its highlight (found by smoke-ui on an Oracle rename).
                  <div key={o.key} className={`${focus === o.key ? "bg-active q-hl" : ""}`}>
                    <button className="w-full text-left px-2 py-1 font-semibold flex items-center gap-2 hover:bg-hover" onClick={() => setFocus(o.key)}>
                      {o.object.schema}.{o.object.name}
                      {o.serverUpdated && <span className="text-muted text-[10px] font-normal">updated by the server</span>}
                    </button>
                    {!o.serverUpdated && o.hits.map((h) => {
                      const possible = h.confidence === "possible";
                      const on = possible ? include.has(h.id) : !exclude.has(h.id);
                      return (
                        <label key={h.id} className="flex items-center gap-2 pl-5 pr-2 py-0.5 cursor-pointer" title={h.reason ?? undefined}>
                          <input type="checkbox" checked={on} onChange={(e) => toggle(h.id, possible, e.target.checked)} />
                          <span className="text-muted w-10 shrink-0">{h.line}</span>
                          <span className="font-mono truncate flex-1">{h.snippet}</span>
                          {possible && <span className="text-warn text-[10px]">possible</span>}
                        </label>
                      );
                    })}
                  </div>
                ))}
                {preview.unchecked.length > 0 && (
                  <div className="p-2 text-warn">
                    Cannot be checked: {preview.unchecked.map((u) => `${u.object.schema}.${u.object.name}`).join(", ")}
                  </div>
                )}
              </div>
              <RenameDiff before={focused?.before ?? ""} after={focused?.after ?? ""} language={languageFor(engine)} />
            </div>
          </>
        )}
      </div>
    </Modal>
  );
}

function RenameDiff({ before, after, language }: { before: string; after: string; language: string }) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);
  useEffect(() => {
    initMonaco();
    const ed = monaco.editor.createDiffEditor(host.current!, {
      readOnly: true, automaticLayout: true, renderSideBySide: true, minimap: { enabled: false }, fontSize: 12, originalEditable: false, scrollBeyondLastLine: false,
      fontFamily: '"JetBrains Mono Variable", "JetBrains Mono", monospace',
      // A read-only viewer: no current-line fill under the diff tints (their contrast is checked on --q-bg).
      renderLineHighlight: "none",
    });
    // One pair of models for the editor's life (swapping models under a running diff makes Monaco reject it).
    const original = monaco.editor.createModel("", language);
    const modified = monaco.editor.createModel("", language);
    ed.setModel({ original, modified });
    editor.current = ed;
    return () => { ed.dispose(); original.dispose(); modified.dispose(); };
  }, [language]);
  useEffect(() => {
    const m = editor.current?.getModel();
    if (!m) return;
    if (m.original.getValue() !== before) m.original.setValue(before);
    if (m.modified.getValue() !== after) m.modified.setValue(after);
  }, [before, after]);
  return <div ref={host} className="flex-1 min-w-0 border border-line rounded" />;
}

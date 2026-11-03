// Dialogs of grid editing: the DML preview, the conflict review and the unapplied-changes prompt.
import { useEffect, useRef } from "react";
import type { Dialog } from "../store/ui";
import { useTabs } from "../store/tabs";
import { Modal } from "../app/Modal";
import { initMonaco, monaco } from "../editor/monaco";
import { languageOf } from "../engines";

/** Read-only Monaco view of the generated statements (values shown as literals; they are sent as parameters). */
export function GridPreviewDialog({ d }: { d: Extract<Dialog, { kind: "gridPreview" }> }) {
  const host = useRef<HTMLDivElement>(null);
  useEffect(() => {
    initMonaco();
    const ed = monaco.editor.create(host.current!, {
      value: d.sql, language: languageOf(d.engine), readOnly: true, automaticLayout: true, minimap: { enabled: false }, fontSize: 12,
      fontFamily: '"JetBrains Mono Variable", "JetBrains Mono", monospace', scrollBeyondLastLine: false, wordWrap: "on",
    });
    return () => {
      ed.getModel()?.dispose();
      ed.dispose();
    };
  }, [d]);
  return (
    <Modal
      title="Review changes"
      subtitle={d.title}
      onClose={() => d.resolve(false)}
      width={900}
      footer={
        <>
          <button className="q-btn" onClick={() => d.resolve(false)}>Cancel</button>
          <button className="q-btn q-btn-primary" onClick={() => d.resolve(true)} autoFocus>Apply changes</button>
        </>
      }
    >
      {d.notes.length > 0 && (
        <ul className="mb-2 space-y-0.5 text-[11px] text-muted list-disc pl-4">
          {d.notes.map((n) => <li key={n}>{n}</li>)}
        </ul>
      )}
      <div ref={host} className="h-[50vh] border border-line rounded" />
    </Modal>
  );
}

/** Rows that changed on the server since the query: shows the server's current values next to the staged ones. */
export function GridConflictsDialog({ d }: { d: Extract<Dialog, { kind: "gridConflicts" }> }) {
  const s = d.staging;
  const deleted = d.conflicts.filter((c) => c.reason === "deletedOnServer").length;
  const changed = d.conflicts.length - deleted;
  const shown = d.conflicts.slice(0, 50);
  return (
    <Modal
      title="Rows changed on the server"
      subtitle={`${changed} changed, ${deleted} deleted since the query ran — nothing was written`}
      onClose={() => d.resolve("cancel")}
      width={760}
      footer={
        <>
          <button className="q-btn" onClick={() => d.resolve("cancel")}>Keep editing</button>
          <button className="q-btn" onClick={() => d.resolve("discard")}>Drop my edits of these rows</button>
          <button className="q-btn q-btn-danger" onClick={() => d.resolve("overwrite")} disabled={changed === 0}>Overwrite with my values</button>
        </>
      }
    >
      <p className="text-fg leading-relaxed mb-3">
        Someone else changed or deleted these rows after you read them. Overwrite replaces their changes with yours (rows they deleted are skipped); dropping
        your edits keeps theirs.
      </p>
      <div className="max-h-[50vh] overflow-auto space-y-2">
        {shown.map((c) => {
          const staged = s.edits.get(c.rowIndex);
          return (
            <div key={c.rowIndex} className="border border-line rounded p-2">
              <div className="text-[11px] font-semibold text-fg mb-1">
                Row {c.rowIndex + 1} · key {s.keyOf(c.rowIndex).map((k) => k.text ?? "NULL").join(", ")} ·{" "}
                <span className={c.reason === "deletedOnServer" ? "text-err" : "text-warn"}>{c.reason === "deletedOnServer" ? "deleted on the server" : "changed on the server"}</span>
              </div>
              {c.current && staged && (
                <table className="w-full text-[11px] q-mono">
                  <thead>
                    <tr className="text-muted text-left"><th className="pr-3 font-normal">Column</th><th className="pr-3 font-normal">You read</th><th className="pr-3 font-normal">Server now</th><th className="font-normal">Yours</th></tr>
                  </thead>
                  <tbody>
                    {[...staged].map(([ordinal, cell]) => {
                      const col = s.column(ordinal)?.column ?? String(ordinal);
                      const server = c.current?.[col];
                      return (
                        <tr key={ordinal}>
                          <td className="pr-3 text-muted">{col}</td>
                          <td className="pr-3">{s.original(c.rowIndex, ordinal) ?? "NULL"}</td>
                          <td className="pr-3 text-warn">{server ? (server.isNull ? "NULL" : server.text) : "?"}</td>
                          <td className="text-accent">{cell.isDefault ? "DEFAULT" : cell.value ?? "NULL"}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              )}
            </div>
          );
        })}
        {d.conflicts.length > shown.length && <div className="text-muted">…and {d.conflicts.length - shown.length} more.</div>}
      </div>
    </Modal>
  );
}

export function GridPendingDialog({ d }: { d: Extract<Dialog, { kind: "gridPending" }> }) {
  const tab = useTabs((st) => st.tabs[d.tabId]);
  return (
    <Modal
      title="Unapplied grid changes"
      subtitle={tab?.title}
      onClose={() => d.resolve("cancel")}
      footer={
        <>
          <button className="q-btn" onClick={() => d.resolve("cancel")}>Cancel</button>
          <button className="q-btn text-err" onClick={() => d.resolve("discard")}>Discard changes</button>
          <button className="q-btn q-btn-primary" onClick={() => d.resolve("apply")}>Apply changes</button>
        </>
      }
    >
      <p className="text-fg leading-relaxed">{d.summary} They are never dropped silently — choose what happens to them.</p>
    </Modal>
  );
}

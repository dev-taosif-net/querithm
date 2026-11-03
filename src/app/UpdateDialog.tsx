// The Update dialog (§16 updater): what's new, what the restart affects, download progress and failures.
import { useTabs } from "../store/tabs";
import { useExecutions } from "../store/executions";
import { DOWNLOAD_PAGE, installUpdate, useUpdates } from "../store/updates";
import { saveWorkspaceNow } from "./persistence";
import { Modal } from "./Modal";
import { percent } from "./UpdateButton";

const mb = (n: number) => `${(n / 1_048_576).toFixed(1)} MB`;
const RUNNING = new Set(["queued", "running", "streaming"]);
const WEBSITE_KINDS = new Set(["signature", "unsupported", "permission"]);

export function UpdateDialog({ close }: { close: () => void }) {
  const { phase, info, downloaded, total, error } = useUpdates();
  const tabs = useTabs((s) => s.tabs);
  const views = useExecutions((s) => s.views);
  if (!info) {
    return (
      <Modal title="Update" onClose={close} footer={<button className="q-btn q-btn-primary" onClick={close}>Close</button>}>
        <p className="text-fg">Querithm is up to date.</p>
      </Modal>
    );
  }
  const openTx = Object.values(tabs).filter((t) => t.txState === "open").length;
  const maybeTx = Object.values(tabs).filter((t) => t.txState === "unknown").length;
  const running = Object.values(views).filter((v) => RUNNING.has(v.state)).length;
  const unsavedFiles = Object.values(tabs).filter((t) => t.filePath && t.dirty).length;
  const busy = phase === "downloading" || phase === "installing";
  const pct = percent(downloaded, total);
  const start = () => void installUpdate(saveWorkspaceNow);
  const warnings = [
    openTx > 0 && `${openTx} open transaction${openTx > 1 ? "s" : ""} will be rolled back.`,
    maybeTx > 0 && `${maybeTx} tab${maybeTx > 1 ? "s" : ""} may have an open transaction; ${maybeTx > 1 ? "they" : "it"} will be rolled back.`,
    running > 0 && `${running} running quer${running > 1 ? "ies" : "y"} will be cancelled.`,
    unsavedFiles > 0 && `${unsavedFiles} file${unsavedFiles > 1 ? "s have" : " has"} unsaved changes. Your text is kept in the tab, but save the file to keep it on disk.`,
  ].filter(Boolean) as string[];

  return (
    <Modal
      title={`Querithm ${info.version} is available`}
      subtitle={`You have version ${info.currentVersion}`}
      onClose={close}
      // An alert that opens on Later (M19): installing rolls back open transactions and cancels running queries, so it
      // takes the button itself, never Enter on the dialog.
      alert
      footer={
        busy ? (
          <button className="q-btn" data-modal-cancel onClick={close} disabled={phase === "installing"}>Hide</button>
        ) : (
          <>
            <button className="q-btn" data-modal-cancel onClick={close}>Later</button>
            <button className="q-btn q-btn-primary" onClick={start}>{phase === "failed" ? "Try again" : "Update & Restart"}</button>
          </>
        )
      }
    >
      {info.notes && (
        <div className="mb-3">
          <div className="text-[11px] font-semibold text-muted mb-1">What's new</div>
          <div className="max-h-48 overflow-y-auto whitespace-pre-wrap border border-line rounded p-2 text-fg select-text">{info.notes}</div>
        </div>
      )}
      {phase === "downloading" && (
        <div className="space-y-1">
          <div className="text-fg">Downloading{pct === null ? "…" : ` ${pct}%`} <span className="text-muted">{downloaded > 0 && `(${mb(downloaded)}${total ? ` of ${mb(total)}` : ""})`}</span></div>
          <div className="h-1.5 bg-line rounded overflow-hidden" role="progressbar" aria-label="Download progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct ?? undefined}>
            <div className="h-full bg-accent transition-[width]" style={{ width: `${pct ?? 0}%` }} />
          </div>
          <p className="text-muted">You can keep working. Querithm restarts when the download is finished.</p>
        </div>
      )}
      {phase === "installing" && <p className="text-fg">Installing the update. Querithm will restart in a moment…</p>}
      {phase === "failed" && error && (
        <div role="alert" className="border border-err/50 bg-err/10 rounded p-2 text-fg">
          <p>{error.message}</p>
          {WEBSITE_KINDS.has(error.kind) && <p className="mt-1 q-mono text-fg select-text">{DOWNLOAD_PAGE}</p>}
        </div>
      )}
      {!busy && (
        <div className="mt-3 space-y-1">
          <p className="text-fg">Querithm downloads the update, then restarts. Your open tabs are saved and come back after the restart.</p>
          {warnings.map((w) => (
            <p key={w} className="text-warn">⚠ {w}</p>
          ))}
        </div>
      )}
    </Modal>
  );
}

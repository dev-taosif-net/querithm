// Kill / cancel dialog (B1c). Shows who and what before anything is sent; a second confirm is always required, a Prod
// server needs the session id typed, and a high (or unknown) rollback risk adds the rollback checkbox. The sidecar
// enforces all of it again. Rollback time is never estimated (P9). Enter never submits.
import { useEffect, useState } from "react";
import type { ActivityRow, KillMode, KillPreview, KillResult } from "../ipc/contract";
import { rpc } from "../ipc/client";
import { Modal } from "../app/Modal";
import { ServerChip } from "../app/EnvTag";
import { toast } from "../store/ui";
import { bytes, escapeBlock, escapeForDisplay, ms, text } from "./format";
import type { MonitorModel } from "./model";

export function riskLabel(p: KillPreview | null): string {
  if (!p) return "checking…";
  switch (p.risk) {
    case "none": return "none — no open transaction";
    case "low": return "low";
    case "high": return "high — the rollback may take long";
    default: return `unknown${p.reason ? ` (${escapeForDisplay(p.reason)})` : ""}`;
  }
}

export function KillDialog({ model, row, mode, prod, profileId, onClose, onDone }: {
  model: MonitorModel; row: ActivityRow; mode: KillMode; prod: boolean; profileId?: string; onClose: () => void; onDone?: (r: KillResult) => void;
}) {
  const [preview, setPreview] = useState<KillPreview | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [typed, setTyped] = useState("");
  const [rollbackOk, setRollbackOk] = useState(false);
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const terminate = mode === "terminateSession";
  useEffect(() => {
    if (!terminate) return;
    rpc("monitor.killPreview", { monitorId: model.monitorId, key: row.key }).then(setPreview, (e) => setPreviewError(String(e?.message ?? e)));
  }, [model.monitorId, row.key, terminate]);
  const sqlText = row.textId ? model.texts.get(row.textId) : undefined;
  const risky = terminate && (preview?.risk === "high" || preview?.risk === "unknown" || previewError != null);
  const ready = confirmed && (!prod || typed.trim() === String(row.key.id)) && (!risky || rollbackOk) && (!terminate || preview != null || previewError != null) && !busy;

  const submit = async () => {
    if (!ready) return;
    setBusy(true);
    setRefusal(null);
    try {
      const r = await rpc("monitor.kill", {
        monitorId: model.monitorId, target: row.key, mode, confirmedKill: true, confirmedProd: prod, confirmedLongRollback: rollbackOk,
        expectedStatementStart: mode === "cancelRequest" ? row.statementStart ?? null : null,
      });
      onDone?.(r);
      if (r.outcome === "done") {
        toast("success", escapeForDisplay(r.message));
        onClose();
      } else setRefusal(r.message);
    } catch (e) {
      setRefusal(String((e as Error)?.message ?? e));
    } finally {
      setBusy(false);
    }
  };

  const facts: [string, string][] = [
    ["Session", `${row.key.id}${row.status ? ` · ${escapeForDisplay(row.status)}` : ""}`],
    ["Login", escapeForDisplay(text(row.login))],
    ["Host", escapeForDisplay(text(row.host))],
    ["Program", escapeForDisplay(text(row.program))],
    ["Database", escapeForDisplay(text(row.database))],
    ["Transaction age", ms(preview?.txElapsedMs ?? row.txElapsedMs)],
  ];
  if (terminate) {
    facts.push(["Rollback risk", previewError ? `unknown (${escapeForDisplay(previewError)})` : riskLabel(preview)]);
    if (preview?.logBytesUsed != null) facts.push(["Log used by the transaction", bytes(preview.logBytesUsed)]);
    if (preview?.hasWrites != null) facts.push(["Transaction wrote", preview.hasWrites ? "yes" : "no (read-only)"]);
    if (preview && preview.blockedCount > 0) facts.push(["Sessions waiting on it", String(preview.blockedCount)]);
    if (preview?.rollingBack) facts.push(["State", "already rolling back"]);
    if (preview?.sessionEnded) facts.push(["State", "the session has ended"]);
  }
  return (
    <Modal
      title={terminate ? `Terminate session ${row.key.id}?` : `Cancel the running query of session ${row.key.id}?`}
      subtitle={terminate ? "The session ends; the server rolls back its open transaction." : "Only the running statement is cancelled; the session stays connected."}
      danger
      width={620}
      onClose={onClose}
      // No Enter shortcut: a destructive dialog is confirmed only by its button (like the host-key and conflict dialogs);
      // Enter on a focused Close or checkbox must never send a kill.
      footer={
        <>
          <button className="q-btn" data-modal-cancel onClick={onClose}>Close</button>
          <button className="q-btn q-btn-danger" disabled={!ready} onClick={() => void submit()} data-testid="kill-submit">
            {busy ? "Sending…" : terminate ? "Terminate session" : "Cancel query"}
          </button>
        </>
      }
    >
      <div className="space-y-3">
        <div className="grid grid-cols-[170px_1fr] gap-x-3 gap-y-1 text-[11.5px]">
          {/* Which server first: the same session id can exist on every server the user monitors. */}
          {profileId && (
            <div className="contents">
              <span className="text-muted">Server</span>
              <span className="min-w-0" data-testid="kill-fact-Server"><ServerChip profileId={profileId} /></span>
            </div>
          )}
          {facts.map(([k, v]) => (
            <div key={k} className="contents">
              <span className="text-muted">{k}</span>
              <span className="q-mono text-fg select-text break-all" data-testid={`kill-fact-${k}`}>{v}</span>
            </div>
          ))}
        </div>
        <pre className="q-mono text-[11px] bg-bg border border-line rounded p-2 max-h-[160px] overflow-auto whitespace-pre-wrap select-text" data-testid="kill-text">
          {sqlText != null ? escapeBlock(sqlText) : row.textNote != null ? escapeBlock(row.textNote) : "No query text."}
        </pre>
        <label className="flex items-start gap-2">
          <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} data-testid="kill-confirm" />
          <span>I checked that this is the right session{terminate ? " and want to end it" : ""}.</span>
        </label>
        {risky && (
          <label className="flex items-start gap-2 text-warn">
            <input type="checkbox" checked={rollbackOk} onChange={(e) => setRollbackOk(e.target.checked)} data-testid="kill-rollback" />
            <span>I understand the rollback may take a long time and hold locks.</span>
          </label>
        )}
        {prod && (
          <label className="block space-y-1">
            <span className="text-err font-semibold">Production server: type the session id ({row.key.id}) to confirm.</span>
            <input className="q-input w-40" value={typed} onChange={(e) => setTyped(e.target.value)} data-testid="kill-typed" />
          </label>
        )}
        {/* A refusal can carry a server message (e.g. PostgreSQL's MessageText): escaped like any server text (S4). */}
        {refusal && <div className="text-err select-text whitespace-pre-wrap" data-testid="kill-refusal">{escapeBlock(refusal)}</div>}
      </div>
    </Modal>
  );
}

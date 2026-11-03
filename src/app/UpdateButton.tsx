// Top-bar Update button (§16 updater): shown only while a newer release is known, next to Settings.
import { useUi } from "../store/ui";
import { useUpdates } from "../store/updates";

function openUpdateDialog() {
  const ui = useUi.getState();
  if (!ui.dialogs.some((d) => d.kind === "update")) ui.openDialog({ kind: "update" });
}

export function percent(downloaded: number, total: number | null) {
  return total ? Math.min(100, Math.floor((downloaded * 100) / total)) : null;
}

export function UpdateButton() {
  const phase = useUpdates((s) => s.phase);
  const version = useUpdates((s) => s.info?.version);
  const pct = useUpdates((s) => percent(s.downloaded, s.total));
  if (!version || phase === "idle") return null;
  const label =
    phase === "downloading" ? `Updating${pct === null ? "…" : ` ${pct}%`}` : phase === "installing" ? "Restarting…" : "Update";
  // A ghost control with an accent dot: Run stays the strongest control on the bar.
  return (
    <button
      type="button"
      className={`q-tb shrink-0 font-semibold ${phase === "failed" ? "text-warn" : "text-accent"}`}
      title={phase === "failed" ? "The update did not finish. Click for details." : `Querithm ${version} is available`}
      aria-label={`Update to Querithm ${version}`}
      onClick={openUpdateDialog}
    >
      <span aria-hidden className={`w-1.5 h-1.5 rounded-full ${phase === "failed" ? "bg-warn" : "bg-accent"}`} />
      {label}
    </button>
  );
}

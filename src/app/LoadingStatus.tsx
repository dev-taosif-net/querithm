import { useEffect, useState } from "react";
import { Icon } from "./icons";

/** Reserves its space immediately; short operations finish before the spinner appears. */
export function BusySpinner() {
  return <span className="q-busy-spinner" aria-hidden="true"><Icon name="spinner" size={13} className="q-spin" /></span>;
}

function Elapsed({ startedAt }: { startedAt: number }) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  return <span aria-live="off" className="tabular-nums">{Math.max(0, Math.floor((now - startedAt) / 1000))} s</span>;
}

export function LoadingStatus({ label, startedAt }: { label: string; startedAt?: number }) {
  return (
    <div className="flex items-center justify-center gap-2 text-muted text-xs" role="status">
      <BusySpinner />
      <span>{label}</span>
      {startedAt != null && <Elapsed startedAt={startedAt} />}
    </div>
  );
}

/** Both labels contribute to width, so an in-flight action cannot shift its neighbours. */
export function BusyButtonContent({ busy, label, busyLabel }: { busy: boolean; label: string; busyLabel: string }) {
  return (
    <span className="inline-flex items-center justify-center gap-1.5">
      <span className="inline-flex w-[13px] shrink-0">{busy && <BusySpinner />}</span>
      <span className="q-busy-button-label" data-label={label} data-busy-label={busyLabel}>
        <span>{busy ? busyLabel : label}</span>
      </span>
    </span>
  );
}

export function ProgressBar({ label, value, active, failed = false }: { label: string; value: number | null; active: boolean; failed?: boolean }) {
  const percent = value == null ? undefined : Math.min(100, Math.max(0, value));
  return (
    <div role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}
      aria-valuetext={failed ? "Import stopped" : percent == null ? active ? "In progress" : "Stopped" : `${percent}%`}
      className="h-2 rounded bg-panel-2 overflow-hidden">
      <div className={`q-progress-fill h-full ${failed ? "bg-err" : "bg-accent"} ${percent == null && active ? "q-progress-indeterminate" : ""}`}
        style={{ width: percent == null ? active ? "35%" : "0%" : `${percent}%` }} />
    </div>
  );
}

/** A stable first-load placeholder; decorative blocks are hidden from assistive technology. */
export function LoadingSkeleton({ label }: { label: string }) {
  return <div className="space-y-4 py-4" aria-busy="true">
    <LoadingStatus label={label} />
    <div aria-hidden="true" className="q-skeleton space-y-3">
      <div className="h-4 w-2/3 rounded bg-panel-2" />
      <div className="h-8 rounded bg-panel-2" />
      <div className="h-8 rounded bg-panel-2" />
      <div className="h-4 w-1/2 rounded bg-panel-2" />
    </div>
  </div>;
}

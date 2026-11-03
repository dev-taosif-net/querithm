// M17: a render error stays inside what threw. Each Dockview panel (query, tool and notebook tabs and their headers), each
// bottom-panel view and the Explorer has a boundary that shows "This panel failed" with Reload panel / Copy details in
// place of the panel; the root boundary (main.tsx) catches the rest with "Querithm hit an error". Every caught error is
// written to the sidecar log (system.reportError): where it hit, the error's type and message, its script stack and
// React's component stack — quoted text in the message is masked, so a cell value or SQL text a parser echoed never
// leaves the webview. "Copy details" copies the same text.
import { Component, Fragment, type ComponentType, type ErrorInfo, type FunctionComponent, type ReactNode } from "react";
import type { ErrorReport } from "../ipc/contract";
import { rpc } from "../ipc/client";

/** Quoted runs in an error message ('x', "x", `x`) may be data (JSON.parse echoes its input): masked. */
export function maskMessage(message: string): string {
  return message.replace(/"[^"]*"|'[^']*'|`[^`]*`/g, '"…"').slice(0, 500);
}

export function errorReport(where: string, error: unknown, componentStack?: string | null): ErrorReport {
  const e = error instanceof Error ? error : undefined;
  return {
    where,
    name: e?.name ?? typeof error,
    message: maskMessage(e ? e.message : String(error)),
    // Only the frames (function and bundle file:line; V8 "    at f (url:1:2)", WebKit "f@url:1:2"): V8's first line
    // repeats the unmasked message.
    stack: e?.stack?.split("\n").filter((l) => /^\s+at\s/.test(l) || /@\S*:\d+:\d+$/.test(l)).filter((l) => !e.message || !e.message.includes(l.trim())).slice(0, 30).join("\n").slice(0, 4000) || undefined,
    componentStack: componentStack?.slice(0, 4000) || undefined,
  };
}

export function detailsText(r: ErrorReport): string {
  return [`Querithm render error in ${r.where}`, `${r.name}: ${r.message}`, r.stack ?? "", r.componentStack ? `Component stack:${r.componentStack}` : ""].filter(Boolean).join("\n");
}

/** At most this many reports within REPORT_WINDOW_MS (a rolling window): a render loop cannot flood the log. */
export const MAX_REPORTS = 20;
export const REPORT_WINDOW_MS = 10 * 60_000;
/** When the reports of the window were sent, oldest first (never more than MAX_REPORTS). */
let sentAt: number[] = [];

/** Best effort: the engine may be down (that may be why a panel failed); a render loop sends at most 20 per 10 minutes. */
export function reportRenderError(r: ErrorReport, now = Date.now()) {
  sentAt = sentAt.filter((t) => now - t < REPORT_WINDOW_MS);
  if (sentAt.length >= MAX_REPORTS) return;
  sentAt.push(now);
  void Promise.resolve()
    .then(() => rpc("system.reportError", r))
    .catch(() => {});
}

/** Test hook. */
export function resetErrorReports() {
  sentAt = [];
}

function copyDetails(r: ErrorReport) {
  void navigator.clipboard?.writeText(detailsText(r)).catch(() => {});
}

/**
 * `resetKey` (W7): a failed panel tries again by itself when it changes (the bottom panel passes the shown run), while a
 * panel that works is never remounted by it (its own state, a filter or a scroll position, stays).
 */
type Props = { name: string; children: ReactNode; variant?: "panel" | "root" | "tab"; resetKey?: string };
type State = { failed: boolean; report?: ErrorReport; generation: number; resetKey?: string };

export class ErrorBoundary extends Component<Props, State> {
  state: State = { failed: false, generation: 0, resetKey: this.props.resetKey };

  static getDerivedStateFromError(): Partial<State> {
    return { failed: true };
  }

  static getDerivedStateFromProps(props: Props, state: State): Partial<State> | null {
    if (props.resetKey === state.resetKey) return null;
    return state.failed ? { resetKey: props.resetKey, failed: false, report: undefined, generation: state.generation + 1 } : { resetKey: props.resetKey };
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    const report = errorReport(this.props.name, error, info.componentStack);
    this.setState({ report });
    reportRenderError(report);
  }

  /** Mounts the panel again from scratch (its own state is lost; the tab's store state is kept). */
  reload = () => this.setState((s) => ({ failed: false, report: undefined, generation: s.generation + 1 }));

  render() {
    if (!this.state.failed) return <Fragment key={this.state.generation}>{this.props.children}</Fragment>;
    const r = this.state.report ?? errorReport(this.props.name, new Error("unknown error"));
    const variant = this.props.variant ?? "panel";
    if (variant === "tab")
      return (
        <button type="button" className="q-tab text-err text-xs px-2" title={`This tab's header failed: ${r.message}. Click to reload it.`} onClick={this.reload}>
          Tab failed · reload
        </button>
      );
    if (variant === "root")
      return (
        <div role="alert" className="h-full flex items-center justify-center bg-bg">
          <div className="max-w-lg p-6 rounded-xl border border-err/50 bg-panel space-y-3">
            <div className="text-sm font-bold text-err">Querithm hit an error</div>
            <div className="text-xs text-fg whitespace-pre-wrap select-text">
              {r.name}: {r.message}
            </div>
            <div className="text-xs text-muted">Your tabs were autosaved; reloading the window restores them.</div>
            <div className="flex gap-2">
              <button type="button" className="q-btn q-btn-primary" onClick={() => location.reload()}>
                Reload window
              </button>
              <button type="button" className="q-btn" onClick={() => copyDetails(r)}>
                Copy details
              </button>
            </div>
          </div>
        </div>
      );
    return (
      <div role="alert" className="h-full min-h-0 flex-1 flex items-center justify-center p-4 bg-bg" data-panel-failed={this.props.name}>
        <div className="max-w-md space-y-2 text-center">
          <div className="text-sm font-semibold text-err">This panel failed</div>
          <div className="text-xs text-muted select-text">
            {this.props.name} · {r.name}: {r.message}
          </div>
          <div className="flex gap-2 justify-center">
            <button type="button" className="q-btn q-btn-primary" onClick={this.reload}>
              Reload panel
            </button>
            <button type="button" className="q-btn" onClick={() => copyDetails(r)}>
              Copy details
            </button>
          </div>
        </div>
      </div>
    );
  }
}

/** A Dockview panel or tab component inside its own boundary. */
export function withBoundary<P extends object>(C: ComponentType<P>, name: string, variant: Props["variant"] = "panel"): FunctionComponent<P> {
  const Wrapped: FunctionComponent<P> = (props: P) => (
    <ErrorBoundary name={name} variant={variant}>
      <C {...props} />
    </ErrorBoundary>
  );
  Wrapped.displayName = `Boundary(${C.displayName ?? C.name ?? name})`;
  return Wrapped;
}

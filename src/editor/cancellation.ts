// Monaco rejects the internal delays it cancels without catching them (the word highlighter's, when a tab switch
// restores a view state); those "Canceled" rejections are expected. VS Code drops them in onUnexpectedError the same way.

/** Monaco's CancellationError: name and message are both "Canceled". */
export function isMonacoCancellation(reason: unknown): boolean {
  const r = reason as { name?: unknown; message?: unknown } | null | undefined;
  return !!r && typeof r === "object" && r.name === "Canceled" && r.message === "Canceled";
}

let installed = false;

/** Stops expected cancellations from surfacing as unhandled errors; every other rejection is left alone. */
export function ignoreMonacoCancellations(target: Pick<Window, "addEventListener"> = window) {
  if (installed) return;
  installed = true;
  target.addEventListener("unhandledrejection", (e: PromiseRejectionEvent) => {
    if (isMonacoCancellation(e.reason)) e.preventDefault();
  });
}

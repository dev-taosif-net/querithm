import { useEffect, useReducer } from "react";

export interface VersionSource {
  subscribe(cb: () => void): () => void;
  getVersion(): number;
}

/** How often the panel's chrome (ledger counts, footer, badges) follows a streaming result. */
export const CHROME_UPDATE_MS = 250;

/**
 * Re-renders the caller at most once per `ms` while `src` notifies (an Arrow batch each time), and once more after the
 * last notification. The grid follows batches itself; the chrome around it (row counts in chips, the footer) only needs
 * to keep up with the eye, so a 1,000-batch stream costs the chrome about 4 renders a second instead of 1,000.
 */
export function useThrottledVersion(src: VersionSource | undefined, ms = CHROME_UPDATE_MS): number {
  const [, bump] = useReducer((x: number) => x + 1, 0);
  useEffect(() => {
    if (!src) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let last = -Infinity;
    const off = src.subscribe(() => {
      if (timer) return;
      timer = setTimeout(() => {
        timer = undefined;
        last = Date.now();
        bump();
      }, Math.max(0, last + ms - Date.now()));
    });
    return () => {
      off();
      if (timer) clearTimeout(timer);
    };
  }, [src, ms]);
  return src?.getVersion() ?? 0;
}

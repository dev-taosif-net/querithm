// One clock for every open-transaction pill (D10): it ticks every 30 s and only while a pill is on screen, and only
// the pills subscribe, so a tab header never re-renders for the age of its transaction.
import { useSyncExternalStore } from "react";

export const TX_CLOCK_MS = 30_000;

let now = Date.now();
const listeners = new Set<() => void>();
let timer: ReturnType<typeof setInterval> | undefined;

function subscribe(cb: () => void): () => void {
  listeners.add(cb);
  if (!timer) {
    now = Date.now();
    timer = setInterval(() => {
      now = Date.now();
      for (const l of [...listeners]) l();
    }, TX_CLOCK_MS);
  }
  return () => {
    listeners.delete(cb);
    if (!listeners.size && timer) {
      clearInterval(timer);
      timer = undefined;
    }
  };
}

/** The time of the last tick (ms since the epoch). */
export function useTxNow(): number {
  return useSyncExternalStore(subscribe, () => now);
}

/** Clocks running (tests: one for any number of pills, none without pills). */
export function txClockRunning(): boolean {
  return !!timer;
}

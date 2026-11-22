// The start screen (§16): index.html draws it (public/boot-splash.css), so it is on screen from the first frame, before
// this bundle has run. Its three tiers follow the three steps of the boot (App.tsx): the engine answered, the settings
// and profiles loaded, the workspace is back. This module only names the step and takes the screen away; it changes
// attributes and text on that one element, so no React component renders for it.
// Without the element (a test, a page that is not index.html) every call does nothing.

/** 1: starting the engine · 2: loading the settings · 3: restoring the workspace · 4: ready. */
export type SplashStage = 1 | 2 | 3 | 4;

const LABELS: Record<SplashStage, string> = {
  1: "Starting the engine…",
  2: "Loading your settings…",
  3: "Restoring your workspace…",
  4: "Ready",
};

/** The seconds waited are shown from here on: a start that takes this long is worth saying so. */
export const SPLASH_ELAPSED_AFTER_S = 3;
/** The fade (boot-splash.css, 160 ms) has ended by then; the element then stops drawing (its animations with it). */
const LEAVE_MS = 200;

const root = () => document.getElementById("splash");

/** performance.now() counts from the page's start, which is when the screen appeared. */
let since = 0;
let ticker: ReturnType<typeof setInterval> | undefined;
let leaving: ReturnType<typeof setTimeout> | undefined;

function showElapsed() {
  const el = document.getElementById("splash-elapsed");
  if (!el) return;
  const secs = Math.floor((performance.now() - since) / 1000);
  el.textContent = secs >= SPLASH_ELAPSED_AFTER_S ? ` · ${secs} s` : "";
}

function count() {
  clearInterval(ticker);
  ticker = setInterval(showElapsed, 1000);
}

/** Names the step of the start that is running; nothing once the screen is gone. */
export function splashStage(stage: SplashStage) {
  const el = root();
  if (!el || el.hidden) return;
  el.dataset.stage = String(stage);
  const text = document.getElementById("splash-text");
  if (text) text.textContent = LABELS[stage];
}

/** The app is starting: the screen is there. Brings it back, from the first step, when it had gone (Restart engine). */
export function showSplash() {
  const el = root();
  if (!el) return;
  const gone = el.hidden || leaving !== undefined;
  clearTimeout(leaving);
  leaving = undefined;
  if (gone) {
    el.hidden = false;
    delete el.dataset.leaving;
    since = performance.now();
    splashStage(1);
    showElapsed();
  }
  if (gone || ticker === undefined) count();
}

/** The app is ready, or says why it is not: the screen fades out and is gone. */
export function hideSplash() {
  const el = root();
  clearInterval(ticker);
  ticker = undefined;
  if (!el || el.hidden || leaving !== undefined) return;
  el.dataset.leaving = "";
  leaving = setTimeout(() => {
    leaving = undefined;
    el.hidden = true;
    delete el.dataset.leaving;
  }, LEAVE_MS);
}

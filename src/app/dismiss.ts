// Closing popups (context menus, dropdowns) on any press outside them, anywhere in the window.
import { useEffect, useRef, type RefObject } from "react";

type Listen = Pick<Window, "addEventListener" | "removeEventListener">;

/**
 * Calls `close` on a press outside every `inside` element, a right-click elsewhere, Escape, scrolling outside,
 * resizing or the window losing focus. Listens in the capture phase on `pointerdown`: Monaco and the grid canvas
 * stop propagation and cancel the compatibility `mousedown`, so a bubbling listener would never hear them.
 * Scrolls are ignored until the next frame: the press that opened the popup can itself scroll (right-clicking a
 * half-hidden tab scrolls the strip to it), and that scroll event arrives after the popup is already listening.
 */
export function onDismiss(
  inside: () => (Node | null | undefined)[], close: () => void, win: Listen = window,
  nextFrame: (f: () => void) => unknown = (f) => (typeof requestAnimationFrame === "function" ? requestAnimationFrame(f) : f()),
  /** Escape goes here first; returning true keeps the popup open (a submenu closes only itself). */
  escape?: () => boolean,
): () => void {
  const outside = (e: Event) => !inside().some((n) => n?.contains(e.target as Node | null));
  const press = (e: Event) => outside(e) && close();
  let armed = false;
  nextFrame(() => (armed = true));
  const scroll = (e: Event) => armed && press(e);
  const key = (e: Event) => (e as KeyboardEvent).key === "Escape" && !escape?.() && close();
  const always = () => close();
  const capture: [string, (e: Event) => void][] = [["pointerdown", press], ["contextmenu", press], ["scroll", scroll], ["keydown", key]];
  for (const [t, f] of capture) win.addEventListener(t, f, true);
  win.addEventListener("resize", always);
  win.addEventListener("blur", always);
  return () => {
    for (const [t, f] of capture) win.removeEventListener(t, f, true);
    win.removeEventListener("resize", always);
    win.removeEventListener("blur", always);
  };
}

/** onDismiss while `open`, for the elements behind `refs`. */
export function useDismiss(open: boolean, refs: RefObject<HTMLElement | null>[], close: () => void, escape?: () => boolean) {
  const latest = useRef(close);
  latest.current = close;
  const latestEscape = useRef(escape);
  latestEscape.current = escape;
  useEffect(() => {
    if (!open) return;
    return onDismiss(() => refs.map((r) => r.current), () => latest.current(), window, undefined, () => !!latestEscape.current?.());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
}

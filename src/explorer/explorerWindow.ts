// Applies Object Explorer window actions (layoutState.ts) to the stores: the mode persists in settings,
// the auto-hide flyout is transient UI state.
import { useEffect, useState } from "react";
import { useSettings } from "../store/settings";
import { useUi } from "../store/ui";
import { nextExplorer, type ExplorerAction } from "./layoutState";

/** Set while the Explorer is mounted; used by "Object Explorer: show" (F8) and the auto-hide side tab. */
export const explorerHooks: { focusSearch?: () => void } = {};

export function explorerAction(a: ExplorerAction) {
  const st = useSettings.getState();
  const cur = { visible: st.explorerVisible, autoHide: st.explorerAutoHide, flyout: useUi.getState().explorerFlyout };
  const next = nextExplorer(cur, a);
  if (next.visible !== cur.visible || next.autoHide !== cur.autoHide) st.set({ explorerVisible: next.visible, explorerAutoHide: next.autoHide });
  if (next.flyout !== cur.flyout) useUi.getState().setExplorer({ explorerFlyout: next.flyout });
  if (a === "show") requestAnimationFrame(() => explorerHooks.focusSearch?.());
}

/** The workspace keeps at least this width: a wide dock gives way in a small window (the minimum is 960 px). */
export const WORKSPACE_MIN = 480;
export const EXPLORER_MIN = 200;
export const EXPLORER_MAX = 600;

/** The dock width as shown: the user's width (kept in the workspace file), capped so the workspace keeps WORKSPACE_MIN. */
export function shownExplorerWidth(stored: number, windowWidth: number): number {
  return Math.max(EXPLORER_MIN, Math.min(stored, EXPLORER_MAX, windowWidth - WORKSPACE_MIN));
}

export function useExplorerWidth(): number {
  const stored = useUi((s) => s.explorerWidth);
  const [win, setWin] = useState(() => (typeof window === "undefined" ? 1440 : window.innerWidth));
  useEffect(() => {
    const f = () => setWin(window.innerWidth);
    window.addEventListener("resize", f);
    return () => window.removeEventListener("resize", f);
  }, []);
  return shownExplorerWidth(stored, win);
}

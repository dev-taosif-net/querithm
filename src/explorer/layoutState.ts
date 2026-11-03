// Object Explorer window states, modelled on Visual Studio tool windows (§12 Left dock):
// pinned (docked), auto-hide (a side tab that flies out over the workspace) or closed.
// `visible` and `autoHide` persist in settings; `flyout` is transient.

export interface ExplorerLayout {
  visible: boolean;
  autoHide: boolean;
  /** The auto-hide panel is currently slid out. */
  flyout: boolean;
}

export type ExplorerMode = "pinned" | "autoHide" | "hidden";

export type ExplorerAction =
  | "toggle" // Ctrl+B
  | "show" // F8: show and focus
  | "togglePin" // the title-bar pushpin
  | "close" // the title-bar ×
  | "flyOut" // hover or click on the side tab
  | "dismiss" // focus or pointer left the flyout
  | { mode: ExplorerMode };

export function modeOf(s: Pick<ExplorerLayout, "visible" | "autoHide">): ExplorerMode {
  return !s.visible ? "hidden" : s.autoHide ? "autoHide" : "pinned";
}

export function nextExplorer(s: ExplorerLayout, a: ExplorerAction): ExplorerLayout {
  if (typeof a === "object") {
    // A closed window keeps its auto-hide flag, like Visual Studio, so re-opening restores it.
    if (a.mode === "hidden") return { ...s, visible: false, flyout: false };
    return { visible: true, autoHide: a.mode === "autoHide", flyout: false };
  }
  switch (a) {
    case "toggle":
      if (!s.visible) return { ...s, visible: true, flyout: s.autoHide };
      if (s.autoHide) return { ...s, flyout: !s.flyout };
      return { ...s, visible: false, flyout: false };
    case "show":
      return { ...s, visible: true, flyout: s.autoHide };
    case "togglePin":
      return { ...s, visible: true, autoHide: !s.autoHide, flyout: false };
    case "close":
      return { ...s, visible: false, flyout: false };
    case "flyOut":
      return s.visible && s.autoHide ? { ...s, flyout: true } : s;
    case "dismiss":
      return s.flyout ? { ...s, flyout: false } : s;
  }
}

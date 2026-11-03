// Linux browsers and webviews (Chromium/Brave, WebKitGTK) paste the primary selection on middle-click into whatever
// editable element has focus, even when the click lands elsewhere: middle-clicking a tab to close it pasted the
// last highlighted text into the editor behind it. Cancelling the mouse release is not enough (Chromium pastes
// "even if handled"), so the paste itself is cancelled while a middle-button press is in progress.
// Ctrl+V and menu pastes are untouched.

type Listen = Pick<Window, "addEventListener" | "removeEventListener">;

export function installMiddleClickPasteGuard(win: Listen = window, later: (f: () => void) => unknown = (f) => setTimeout(f, 0)): () => void {
  let middle = false;
  const down = (e: Event) => {
    if ((e as MouseEvent).button === 1) middle = true;
  };
  // The paste runs while the release is handled, so the flag is cleared only after that task.
  const up = (e: Event) => {
    if ((e as MouseEvent).button === 1) later(() => (middle = false));
  };
  const block = (e: Event) => {
    if (!middle) return;
    if (e.type === "beforeinput" && (e as InputEvent).inputType !== "insertFromPaste") return;
    e.preventDefault();
    e.stopImmediatePropagation();
  };
  const listeners: [string, (e: Event) => void][] = [["pointerdown", down], ["mousedown", down], ["pointerup", up], ["mouseup", up], ["paste", block], ["beforeinput", block]];
  for (const [t, f] of listeners) win.addEventListener(t, f, true);
  return () => {
    for (const [t, f] of listeners) win.removeEventListener(t, f, true);
  };
}

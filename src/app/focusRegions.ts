// F6 / Shift+F6 (§12 Keyboard model): move focus between the top bar, the Object Explorer, the tab strip, the active
// tab's editor and its results panel, as SSMS and VS Code do. Tab indents in Monaco, so this is the keyboard way out of
// the editor.
import { useTabs } from "../store/tabs";
import { editorForTab } from "../editor/editors";

const ORDER = ["toolbar", "explorer", "tabs", "editor", "results"] as const;
type Region = (typeof ORDER)[number];

/** On screen: laid out, and not inside a part hidden from the user (a closed or slid-in auto-hide Explorer). */
const shown = (el: Element) => el.getClientRects().length > 0 && !el.closest('[aria-hidden="true"]');

function regionOf(el: Element | null): Region | undefined {
  if (el?.closest(".monaco-editor")) return "editor";
  if (el?.closest('.dv-tabs-container[role="tablist"]')) return "tabs";
  const r = el?.closest<HTMLElement>("[data-focus-region]")?.dataset.focusRegion;
  return ORDER.find((x) => x === r);
}

/** Focuses `region`: the editor of the active tab, the strip's Tab stop, else the region's toolbar stop or first control. */
function focusRegion(region: Region): boolean {
  if (region === "tabs") {
    // The strip of the focused group first (D22): its one Tab stop is the tab it shows.
    const strips = [...document.querySelectorAll<HTMLElement>('.dv-active-group .dv-tabs-container[role="tablist"], .dv-tabs-container[role="tablist"]')];
    for (const strip of strips) {
      const tab = strip.querySelector<HTMLElement>('[role="tab"][tabindex="0"]') ?? strip.querySelector<HTMLElement>('[role="tab"]');
      if (tab && shown(tab)) {
        tab.focus();
        return true;
      }
    }
    return false;
  }
  if (region === "editor") {
    const id = useTabs.getState().activeId;
    const editor = id ? editorForTab(id) : undefined;
    if (!editor) return false;
    editor.focus();
    return true;
  }
  for (const root of document.querySelectorAll<HTMLElement>(`[data-focus-region="${region}"]`)) {
    if (!shown(root)) continue;
    // The Explorer takes focus on its tree (one Tab stop, the selected row), else its search box.
    const target =
      (region === "explorer" ? (root.querySelector<HTMLElement>('[role="tree"]') ?? root.querySelector<HTMLElement>("input")) : null) ??
      root.querySelector<HTMLElement>('[role="toolbar"] [tabindex="0"]') ??
      root.querySelector<HTMLElement>("button:not([disabled]), [tabindex='0']");
    if (target) {
      target.focus();
      return true;
    }
  }
  return false;
}

export function cycleFocus(dir: 1 | -1) {
  const current = regionOf(document.activeElement);
  let i = current ? ORDER.indexOf(current) : dir === 1 ? -1 : 0;
  for (let n = 0; n < ORDER.length; n++) {
    i = (i + dir + ORDER.length) % ORDER.length;
    if (focusRegion(ORDER[i])) return;
  }
}

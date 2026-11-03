// Tabs semantics and the keyboard for the editor tab strips (D22). Dockview draws the strips and gives every tab a Tab
// stop; this makes each strip a tablist with one Tab stop (roving tabindex on the shown tab), and handles the keys
// with manual activation: arrows, Home and End move focus, Enter or Space activates, so walking the strip neither
// switches editors nor fills the recent-tabs list. Everything is DOM work: no tab header re-renders for it.
import type { DockviewApi, IDockviewPanel } from "dockview";

/** What the keys do; the workspace wires these to the tab actions. */
export interface StripActions {
  activate(id: string): void;
  /** Closes a tab through the guarded close (D20); resolves false when the tab stayed. */
  close(id: string): Promise<boolean>;
  /** Moves a tab within its strip by `delta` places (pinned tabs stay first). */
  move(id: string, delta: -1 | 1): void;
  /** Focus the editor of the active tab (Escape). */
  focusEditor(): void;
}

/** The element of a panel's tab: Dockview's .dv-tab around the header, which takes focus. */
export function tabElement(p: IDockviewPanel): HTMLElement {
  return (p.view.tab.element.closest(".dv-tab") ?? p.view.tab.element) as HTMLElement;
}

/** Sent to a tab's header: start renaming it (F2). */
export const RENAME_EVENT = "q-tab-rename";
/** Sent to a tab's header: open its menu at the tab (Shift+F10, the Menu key). */
export const MENU_EVENT = "q-tab-menu";

/**
 * Roles and Tab stops for every strip: tablist / tab, aria-selected on the tab each group shows, and that tab as the
 * strip's only Tab stop. The panel's content becomes the tabpanel the tab controls.
 */
export function syncStripAria(api: DockviewApi) {
  for (const g of api.groups) {
    const strip = g.element?.querySelector(".dv-tabs-container");
    if (strip) {
      strip.setAttribute("role", "tablist");
      strip.setAttribute("aria-label", "Editor tabs");
      // Dockview scrolls the strip by script behind overflow:hidden: the UI smoke's layout check treats it as a scroller.
      strip.setAttribute("data-layout-scroller", "");
    }
    const shown = g.activePanel;
    for (const p of g.panels) {
      if (!p.view?.tab) continue;
      const el = tabElement(p);
      el.setAttribute("role", "tab");
      el.setAttribute("aria-selected", String(p === shown));
      el.tabIndex = p === shown ? 0 : -1;
      const content = p.view.content?.element as HTMLElement | undefined;
      if (content) {
        if (!content.id) content.id = `q-tabpanel-${p.id}`;
        content.setAttribute("role", "tabpanel");
        el.setAttribute("aria-controls", content.id);
      }
    }
  }
}

/** The panel whose tab is `el`, and the group's panels in strip order. */
function locate(api: DockviewApi, el: Element): { panel: IDockviewPanel; panels: IDockviewPanel[] } | undefined {
  for (const g of api.groups) {
    const panel = g.panels.find((p) => tabElement(p) === el);
    if (panel) return { panel, panels: g.panels };
  }
  return undefined;
}

/** Moves the strip's Tab stop to `p` and focuses it (focus scrolls it into view, clear of the pinned tabs). */
function focusTab(panels: IDockviewPanel[], p: IDockviewPanel) {
  for (const x of panels) tabElement(x).tabIndex = x === p ? 0 : -1;
  tabElement(p).focus();
}

/** The id of the strip tab that has keyboard focus, if any. */
export function focusedTabId(api: DockviewApi): string | undefined {
  const el = (document.activeElement as HTMLElement | null)?.closest?.(".dv-tab");
  return el ? locate(api, el)?.panel.id : undefined;
}

/**
 * Handles a key pressed on a strip tab; true when it was the strip's. Ctrl+Shift+PageUp/PageDown (move) are app
 * commands and act on the focused tab through `focusedTabId`.
 */
export function handleStripKey(e: KeyboardEvent, api: DockviewApi, actions: StripActions): boolean {
  const target = (e.target as HTMLElement | null)?.closest?.(".dv-tab");
  if (!target || (e.target as HTMLElement).closest("input, textarea, [role='menu']")) return false;
  const at = locate(api, target);
  if (!at) return false;
  const { panel, panels } = at;
  const i = panels.indexOf(panel);
  const plain = !e.ctrlKey && !e.altKey && !e.metaKey;
  const go = (p: IDockviewPanel | undefined) => p && focusTab(panels, p);
  if (plain && !e.shiftKey && e.key === "ArrowRight") go(panels[(i + 1) % panels.length]);
  else if (plain && !e.shiftKey && e.key === "ArrowLeft") go(panels[(i - 1 + panels.length) % panels.length]);
  else if (plain && !e.shiftKey && e.key === "Home") go(panels[0]);
  else if (plain && !e.shiftKey && e.key === "End") go(panels[panels.length - 1]);
  else if (plain && !e.shiftKey && (e.key === "Enter" || e.key === " ")) actions.activate(panel.id);
  else if (plain && !e.shiftKey && e.key === "Delete") {
    // Focus stays in the strip: on the tab that takes the closed one's place, else the one before it.
    void actions.close(panel.id).then((closed) => {
      const g = api.groups.find((x) => x.panels.some((p) => panels.includes(p)));
      const rest = g?.panels ?? [];
      if (!closed) return go(panel);
      const next = rest[Math.min(i, rest.length - 1)];
      if (next) focusTab(rest, next);
    });
  } else if (plain && !e.shiftKey && e.key === "F2") target.dispatchEvent(new CustomEvent(RENAME_EVENT));
  else if ((e.key === "F10" && e.shiftKey && plain) || (e.key === "ContextMenu" && plain)) target.dispatchEvent(new CustomEvent(MENU_EVENT));
  else if (plain && !e.shiftKey && e.key === "Escape") actions.focusEditor();
  else return false;
  e.preventDefault();
  e.stopPropagation();
  return true;
}

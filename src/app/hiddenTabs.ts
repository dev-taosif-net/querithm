// The layout of one tab strip that Dockview does not do (strip redesign D13–D15): pinned tabs stay at the left while
// the strip scrolls, edge fades show which side hides tabs, the active tab is kept in view after a resize, and the
// tabs out of view are counted for the strip's end button. All of it is DOM work in one observer per strip: a
// scroll or resize re-renders nothing until the set of hidden tabs (or their status) changes.
import { useEffect, useState } from "react";
import type { DockviewGroupPanel, IDockviewPanel } from "dockview";

/** Pinned tabs stay at the left up to this share of the strip; past it they scroll like other tabs (D13). */
export const STICKY_SHARE = 0.4;

export interface StripTab {
  id: string;
  el: Element;
  /** Held at the left edge (a pinned tab within the sticky share): never hidden. */
  sticky?: boolean;
}

/**
 * Ids whose tab is not entirely inside the strip's visible width, right of the pinned tabs held at its left
 * (`leftInset` px). 1 px of slack: fractional tab widths round differently from the strip's edge.
 */
export function hiddenIds(strip: Element, tabs: StripTab[], leftInset = 0): string[] {
  const s = strip.getBoundingClientRect();
  return tabs
    .filter(({ el, sticky }) => {
      if (sticky) return false;
      const r = el.getBoundingClientRect();
      return r.left < s.left + leftInset - 1 || r.right > s.right + 1;
    })
    .map((t) => t.id);
}

/**
 * Where each pinned tab sits while the strip scrolls: offsets add up from the left, and only while they stay within
 * `share` of the strip (`widths` in strip order, pinned tabs first). Returns the offsets of the held tabs and the
 * width they cover.
 */
export function stickyOffsets(widths: readonly number[], stripWidth: number, share = STICKY_SHARE): { offsets: number[]; covered: number } {
  const offsets: number[] = [];
  let x = 0;
  for (const w of widths) {
    if (x + w > stripWidth * share) break;
    offsets.push(x);
    x += w;
  }
  return { offsets, covered: x };
}

/** A tab cut by an edge (partly visible): the edge fade goes over it, never over a fully visible tab (D14). */
function cutAt(s: DOMRect, r: DOMRect, left: number): "left" | "right" | undefined {
  if (r.left < left - 1 && r.right > left + 1) return "left";
  if (r.left < s.right - 1 && r.right > s.right + 1) return "right";
  return undefined;
}

/**
 * Lays out one group's strip and returns the ids of its tabs out of view (query and tool tabs alike), kept up to date
 * when the strip resizes, scrolls or its tabs change. `isPinned` says which tabs are held at the left;
 * `activeId` is scrolled fully into view, right of the pins, after it changes and after every resize (D15).
 */
export function useStripLayout(group: DockviewGroupPanel | undefined, panels: IDockviewPanel[], isPinned: (id: string) => boolean, activeId?: string): string[] {
  const [hidden, setHidden] = useState<string[]>([]);
  useEffect(() => {
    const strip = group?.element.querySelector<HTMLElement>(".dv-tabs-container");
    if (!strip) {
      // Keep the same empty array: a new one re-renders, and with a fresh `panels` array re-runs this effect forever.
      setHidden((cur) => (cur.length ? [] : cur));
      return;
    }
    const frameBox = strip.parentElement;
    let frame = 0;
    let revealPending = false;
    const tabEl = (p: IDockviewPanel) => (p.view.tab.element.closest(".dv-tab") ?? p.view.tab.element) as HTMLElement;
    const measure = (reveal: boolean) => {
      frame = 0;
      const els = panels.map((p) => ({ id: p.id, el: tabEl(p) }));
      // Pinned tabs come first in the strip (applyPinnedOrder); hold as many as fit in the sticky share.
      const pinned = els.filter((t) => isPinned(t.id));
      const { offsets, covered } = stickyOffsets(pinned.map((t) => t.el.getBoundingClientRect().width), strip.clientWidth);
      pinned.forEach((t, i) => {
        const held = i < offsets.length;
        t.el.style.left = held ? `${offsets[i]}px` : "";
        t.el.toggleAttribute("data-q-sticky", held);
        // An overlay by design: tabs scroll under it (the UI smoke's layout check skips such layers).
        t.el.toggleAttribute("data-layout-layer", held);
        t.el.toggleAttribute("data-q-sticky-edge", held && i === offsets.length - 1);
      });
      strip.style.scrollPaddingLeft = `${covered}px`;
      strip.toggleAttribute("data-q-scrolled", strip.scrollLeft > 0);
      const tabs: StripTab[] = els.map((t, i) => ({ ...t, sticky: i < offsets.length && isPinned(t.id) }));
      if (frameBox) {
        const s = strip.getBoundingClientRect();
        const left = s.left + covered;
        let fadeL = false;
        let fadeR = false;
        for (const t of tabs) {
          if (t.sticky) continue;
          const at = cutAt(s, t.el.getBoundingClientRect(), left);
          if (at === "left") fadeL = true;
          if (at === "right") fadeR = true;
        }
        frameBox.style.setProperty("--q-pin-w", `${covered}px`);
        frameBox.toggleAttribute("data-q-fade-l", fadeL);
        frameBox.toggleAttribute("data-q-fade-r", fadeR);
      }
      if (reveal && activeId) {
        const a = els.find((t) => t.id === activeId);
        if (a && !tabs.find((t) => t.id === activeId)?.sticky) a.el.scrollIntoView?.({ block: "nearest", inline: "nearest" });
      }
      const next = hiddenIds(strip, tabs, covered);
      setHidden((cur) => (cur.length === next.length && cur.every((x, i) => x === next[i]) ? cur : next));
    };
    const schedule = (reveal = false) => {
      revealPending ||= reveal;
      if (!frame) frame = requestAnimationFrame(() => {
        const shouldReveal = revealPending;
        revealPending = false;
        measure(shouldReveal);
      });
    };
    const onScroll = () => schedule();
    const onResize = () => schedule(true);
    measure(true);
    const resize = new ResizeObserver(onResize);
    resize.observe(strip);
    // Adding, closing, renaming or moving a tab can change what fits without resizing the strip itself.
    const mutations = new MutationObserver(() => schedule(true));
    mutations.observe(strip, { childList: true, subtree: true, characterData: true });
    strip.addEventListener("scroll", onScroll, { passive: true });
    return () => {
      if (frame) cancelAnimationFrame(frame);
      resize.disconnect();
      mutations.disconnect();
      strip.removeEventListener("scroll", onScroll);
    };
  }, [group, panels, isPinned, activeId]);
  return hidden;
}

// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { hiddenIds, stickyOffsets, useStripLayout } from "../hiddenTabs";

/** An element whose box spans [left, right) horizontally. */
function box(left: number, right: number, el: HTMLElement = document.createElement("div")) {
  el.getBoundingClientRect = () => ({ left, right, top: 0, bottom: 30, width: right - left, height: 30, x: left, y: 0, toJSON: () => ({}) });
  return el;
}

describe("hiddenIds", () => {
  const strip = box(100, 500);

  it("lists tabs cut by either edge or outside the strip, in strip order", () => {
    const tabs = [
      { id: "left-out", el: box(-50, 60) },
      { id: "left-cut", el: box(80, 180) },
      { id: "inside", el: box(180, 300) },
      { id: "right-cut", el: box(420, 520) },
      { id: "right-out", el: box(520, 640) },
    ];
    expect(hiddenIds(strip, tabs)).toEqual(["left-out", "left-cut", "right-cut", "right-out"]);
  });

  it("allows 1 px of rounding at the edges", () => {
    expect(hiddenIds(strip, [{ id: "a", el: box(99.5, 200) }, { id: "b", el: box(400, 500.8) }])).toEqual([]);
    expect(hiddenIds(strip, [{ id: "c", el: box(400, 501.5) }])).toEqual(["c"]);
  });

  it("tabs under the pinned ones count as hidden; the pinned ones held at the edge never do", () => {
    const tabs = [
      { id: "pin", el: box(100, 228), sticky: true },
      { id: "under", el: box(200, 330) },
      { id: "clear", el: box(330, 460) },
    ];
    expect(hiddenIds(strip, tabs, 128)).toEqual(["under"]);
  });

  it("nothing hidden when every tab fits", () => {
    expect(hiddenIds(strip, [])).toEqual([]);
    expect(hiddenIds(strip, [{ id: "a", el: box(100, 500) }])).toEqual([]);
  });
});

describe("useHiddenTabs", () => {
  let resize: (() => void) | undefined;
  let disconnected = 0;
  let frames: FrameRequestCallback[] = [];
  const flushFrames = () => act(() => { const f = frames; frames = []; f.forEach((cb) => cb(0)); });

  beforeEach(() => {
    disconnected = 0;
    frames = [];
    vi.stubGlobal("ResizeObserver", class {
      constructor(cb: () => void) { resize = cb; }
      observe() {}
      disconnect() { disconnected++; }
    });
    vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback) => frames.push(cb));
    vi.stubGlobal("cancelAnimationFrame", vi.fn());
  });
  afterEach(() => vi.unstubAllGlobals());

  /** A Dockview group with a strip 100..500 and one tab per [id, left, right]. */
  function group(tabs: [string, number, number][]) {
    const element = document.createElement("div");
    const strip = box(100, 500);
    strip.className = "dv-tabs-container";
    element.appendChild(strip);
    const panels = tabs.map(([id, l, r]) => {
      const tab = box(l, r);
      tab.className = "dv-tab";
      const inner = document.createElement("span");
      tab.appendChild(inner);
      strip.appendChild(tab);
      return { id, view: { tab: { element: inner } }, tab };
    });
    return { group: { element } as any, panels: panels as any[], strip, byId: Object.fromEntries(panels.map((p) => [p.id, p.tab])) };
  }

  const none = () => false;
  const pinnedIds = (...ids: string[]) => (id: string) => ids.includes(id);

  it("measures the .dv-tab around each header; tool tabs count like query tabs", () => {
    const g = group([["a", 100, 300], ["b", 300, 520], ["tool", 520, 600]]);
    const { result } = renderHook(() => useStripLayout(g.group, g.panels, none));
    expect(result.current).toEqual(["b", "tool"]);
  });

  it("updates on resize and scroll, once per frame, and keeps the same array while the set is unchanged", () => {
    const g = group([["a", 100, 300], ["b", 300, 480]]);
    const { result } = renderHook(() => useStripLayout(g.group, g.panels, none));
    expect(result.current).toEqual([]);
    const first = result.current;

    g.strip.dispatchEvent(new Event("scroll"));
    g.strip.dispatchEvent(new Event("scroll"));
    expect(frames).toHaveLength(1); // coalesced
    flushFrames();
    expect(result.current).toBe(first); // nothing crossed an edge: no new array, no re-render

    box(100, 400, g.strip); // the strip got narrower
    act(() => resize!());
    flushFrames();
    expect(result.current).toEqual(["b"]);
    box(0, 200, g.byId.a); // scrolled: a now sticks out on the left
    g.strip.dispatchEvent(new Event("scroll"));
    flushFrames();
    expect(result.current).toEqual(["a", "b"]);
  });

  it("holds pinned tabs at the left within 40 % of the strip, pads scrolling past them and marks the last one (D13)", () => {
    const g = group([["p1", 100, 228], ["p2", 228, 356], ["c", 200, 330], ["d", 330, 460]]);
    Object.defineProperty(g.strip, "clientWidth", { value: 400 });
    g.strip.scrollLeft = 40;
    const { result } = renderHook(() => useStripLayout(g.group, g.panels, pinnedIds("p1", "p2")));
    const p1 = g.byId.p1 as HTMLElement;
    const p2 = g.byId.p2 as HTMLElement;
    expect(p1.hasAttribute("data-q-sticky")).toBe(true);
    expect(p1.style.left).toBe("0px");
    expect(p1.hasAttribute("data-q-sticky-edge")).toBe(true);
    expect(p1.hasAttribute("data-layout-layer")).toBe(true);
    // 2 × 128 px is more than 40 % of 400 px: the second pin scrolls like any tab.
    expect(p2.hasAttribute("data-q-sticky")).toBe(false);
    expect(g.strip.style.scrollPaddingLeft).toBe("128px");
    expect(g.strip.hasAttribute("data-q-scrolled")).toBe(true);
    expect(result.current).toEqual(["c"]); // c passes under the held pin; p2 starts right after it
  });

  it("fades the edge that cuts a tab, and only that one; the left fade starts after the pins (D14)", () => {
    const g = group([["a", 100, 300], ["b", 300, 520]]);
    renderHook(() => useStripLayout(g.group, g.panels, none));
    const frame = g.group.element as HTMLElement;
    expect(frame.hasAttribute("data-q-fade-r")).toBe(true);
    expect(frame.hasAttribute("data-q-fade-l")).toBe(false);
    expect(frame.style.getPropertyValue("--q-pin-w")).toBe("0px");
    box(80, 300, g.byId.a);
    box(300, 500, g.byId.b);
    g.strip.dispatchEvent(new Event("scroll"));
    flushFrames();
    expect(frame.hasAttribute("data-q-fade-l")).toBe(true);
    expect(frame.hasAttribute("data-q-fade-r")).toBe(false); // b ends exactly at the edge: fully visible
  });

  it("keeps the active tab in view on activation and after a resize, not on a plain scroll (D15)", () => {
    const g = group([["a", 100, 300], ["b", 300, 520]]);
    const into = vi.fn();
    (g.byId.b as HTMLElement).scrollIntoView = into;
    renderHook(() => useStripLayout(g.group, g.panels, none, "b"));
    expect(into).toHaveBeenCalledTimes(1);
    expect(into).toHaveBeenCalledWith({ block: "nearest", inline: "nearest" });
    g.strip.dispatchEvent(new Event("scroll"));
    flushFrames();
    expect(into).toHaveBeenCalledTimes(1);
    act(() => resize!());
    flushFrames();
    expect(into).toHaveBeenCalledTimes(2);
  });

  it("a queued scroll cannot suppress a resize's active-tab reveal", () => {
    const g = group([["a", 100, 300], ["b", 300, 520]]);
    const into = vi.fn();
    g.byId.b.scrollIntoView = into;
    renderHook(() => useStripLayout(g.group, g.panels, none, "b"));
    g.strip.dispatchEvent(new Event("scroll"));
    act(() => resize!());
    flushFrames();
    expect(into).toHaveBeenCalledTimes(2);
  });

  it("re-measures when tabs are added to the strip", async () => {
    const g = group([["a", 100, 300]]);
    renderHook(() => useStripLayout(g.group, g.panels, none));
    g.strip.appendChild(document.createElement("div")); // Dockview adding a tab
    await act(async () => {});
    expect(frames.length).toBeGreaterThan(0);
  });

  it("no group or no strip: nothing hidden; unmounting removes every listener", () => {
    expect(renderHook(() => useStripLayout(undefined, [], none)).result.current).toEqual([]);
    const g = group([["a", 100, 300]]);
    const remove = vi.spyOn(g.strip, "removeEventListener");
    const { unmount } = renderHook(() => useStripLayout(g.group, g.panels, none));
    g.strip.dispatchEvent(new Event("scroll"));
    unmount();
    expect(disconnected).toBe(1);
    expect(remove).toHaveBeenCalledWith("scroll", expect.any(Function));
    expect(cancelAnimationFrame).toHaveBeenCalled(); // the pending frame is dropped
  });
});

describe("stickyOffsets", () => {
  it("adds widths from the left while they stay within the share", () => {
    expect(stickyOffsets([128, 128, 128], 1000)).toEqual({ offsets: [0, 128, 256], covered: 384 });
    expect(stickyOffsets([128, 128, 128], 700)).toEqual({ offsets: [0, 128], covered: 256 });
    expect(stickyOffsets([128], 300)).toEqual({ offsets: [], covered: 0 });
    expect(stickyOffsets([], 1000)).toEqual({ offsets: [], covered: 0 });
  });
});

import { describe, expect, it, vi } from "vitest";
import { onDismiss } from "../dismiss";

/** A window stand-in that records listeners with their capture flag. */
function fakeWindow() {
  const listeners = new Map<string, { f: (e: Event) => void; capture: boolean }[]>();
  return {
    addEventListener: (t: string, f: (e: Event) => void, capture?: boolean) => listeners.set(t, [...(listeners.get(t) ?? []), { f, capture: !!capture }]),
    removeEventListener: (t: string, f: (e: Event) => void) => listeners.set(t, (listeners.get(t) ?? []).filter((l) => l.f !== f)),
    fire: (t: string, e: Partial<Event & KeyboardEvent> = {}) => (listeners.get(t) ?? []).forEach((l) => l.f(e as Event)),
    capture: (t: string) => (listeners.get(t) ?? []).every((l) => l.capture),
    count: () => [...listeners.values()].reduce((n, l) => n + l.length, 0),
  };
}

const menu = { contains: (n: unknown) => n === menu || n === item } as unknown as Node;
const item = {} as Node;
const editor = {} as Node;

describe("dismissing popups", () => {
  it("a press anywhere outside the menu closes it, including in the editor or grid (capture phase)", () => {
    const win = fakeWindow();
    const close = vi.fn();
    onDismiss(() => [menu], close, win as never);
    expect(win.capture("pointerdown")).toBe(true);
    win.fire("pointerdown", { target: editor });
    expect(close).toHaveBeenCalledTimes(1);
  });
  it("Escape asks the popup first: a submenu that handles it keeps the menu open", () => {
    const win = fakeWindow();
    const close = vi.fn();
    let handled = true;
    onDismiss(() => [menu], close, win as never, (f) => f(), () => handled);
    win.fire("keydown", { key: "Escape" });
    expect(close).not.toHaveBeenCalled();
    handled = false;
    win.fire("keydown", { key: "Escape" });
    expect(close).toHaveBeenCalledTimes(1);
  });
  it("a press inside the menu (its items) keeps it open", () => {
    const win = fakeWindow();
    const close = vi.fn();
    onDismiss(() => [menu], close, win as never);
    win.fire("pointerdown", { target: item });
    win.fire("contextmenu", { target: menu });
    win.fire("scroll", { target: item });
    expect(close).not.toHaveBeenCalled();
  });
  it("Escape, a right-click elsewhere, scrolling elsewhere, resizing and leaving the window close it", () => {
    for (const [type, e] of [["keydown", { key: "Escape" }], ["contextmenu", { target: editor }], ["scroll", { target: editor }], ["resize", {}], ["blur", {}]] as const) {
      const win = fakeWindow();
      const close = vi.fn();
      onDismiss(() => [menu], close, win as never);
      win.fire(type, e);
      expect(close, type).toHaveBeenCalledTimes(1);
    }
  });
  it("a scroll in the frame the popup opened in (caused by the opening press) does not close it; later scrolls do", () => {
    const win = fakeWindow();
    const close = vi.fn();
    let frame: () => void = () => {};
    onDismiss(() => [menu], close, win as never, (f) => (frame = f));
    win.fire("scroll", { target: editor });
    expect(close).not.toHaveBeenCalled();
    win.fire("pointerdown", { target: editor });
    expect(close).toHaveBeenCalledTimes(1);
    frame();
    win.fire("scroll", { target: editor });
    expect(close).toHaveBeenCalledTimes(2);
  });
  it("other keys do not close it", () => {
    const win = fakeWindow();
    const close = vi.fn();
    onDismiss(() => [menu], close, win as never);
    win.fire("keydown", { key: "ArrowDown" });
    expect(close).not.toHaveBeenCalled();
  });
  it("several elements count as inside (button + dropdown), and a missing one is ignored", () => {
    const win = fakeWindow();
    const close = vi.fn();
    const button = { contains: (n: unknown) => n === button } as unknown as Node;
    onDismiss(() => [button, null, menu], close, win as never);
    win.fire("pointerdown", { target: button });
    expect(close).not.toHaveBeenCalled();
  });
  it("cleanup removes every listener", () => {
    const win = fakeWindow();
    const off = onDismiss(() => [menu], vi.fn(), win as never);
    expect(win.count()).toBe(6);
    off();
    expect(win.count()).toBe(0);
  });
});

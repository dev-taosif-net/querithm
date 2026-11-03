import { describe, expect, it, vi } from "vitest";
import { installMiddleClickPasteGuard } from "../middleClickPaste";

function fakeWindow() {
  const listeners = new Map<string, { f: (e: Event) => void; capture: boolean }[]>();
  return {
    addEventListener: (t: string, f: (e: Event) => void, capture?: boolean) => listeners.set(t, [...(listeners.get(t) ?? []), { f, capture: !!capture }]),
    removeEventListener: (t: string, f: (e: Event) => void) => listeners.set(t, (listeners.get(t) ?? []).filter((l) => l.f !== f)),
    fire(t: string, init: Record<string, unknown> = {}) {
      const e = { type: t, preventDefault: vi.fn(), stopImmediatePropagation: vi.fn(), ...init };
      (listeners.get(t) ?? []).forEach((l) => l.f(e as unknown as Event));
      return e;
    },
    capture: () => [...listeners.values()].flat().every((l) => l.capture),
    count: () => [...listeners.values()].flat().length,
  };
}

function setup() {
  const win = fakeWindow();
  const pending: (() => void)[] = [];
  const off = installMiddleClickPasteGuard(win as never, (f) => pending.push(f));
  const flush = () => pending.splice(0).forEach((f) => f());
  return { win, off, flush };
}

describe("middle-click paste guard", () => {
  it("cancels the primary-selection paste of a middle click (e.g. closing a tab), in the capture phase", () => {
    const { win } = setup();
    expect(win.capture()).toBe(true);
    win.fire("mousedown", { button: 1 });
    win.fire("mouseup", { button: 1 });
    const paste = win.fire("paste");
    expect(paste.preventDefault).toHaveBeenCalled();
    expect(paste.stopImmediatePropagation).toHaveBeenCalled();
    const input = win.fire("beforeinput", { inputType: "insertFromPaste" });
    expect(input.preventDefault).toHaveBeenCalled();
  });
  it("lets typing through during a middle press, and every paste after it (Ctrl+V)", () => {
    const { win, flush } = setup();
    win.fire("pointerdown", { button: 1 });
    expect(win.fire("beforeinput", { inputType: "insertText" }).preventDefault).not.toHaveBeenCalled();
    win.fire("pointerup", { button: 1 });
    flush();
    expect(win.fire("paste").preventDefault).not.toHaveBeenCalled();
  });
  it("left and right clicks never block pastes", () => {
    const { win } = setup();
    win.fire("mousedown", { button: 0 });
    expect(win.fire("paste").preventDefault).not.toHaveBeenCalled();
    win.fire("mousedown", { button: 2 });
    expect(win.fire("paste").preventDefault).not.toHaveBeenCalled();
  });
  it("uninstalls every listener", () => {
    const { win, off } = setup();
    off();
    expect(win.count()).toBe(0);
  });
});

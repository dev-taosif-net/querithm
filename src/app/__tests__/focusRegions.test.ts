// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useTabs } from "../../store/tabs";

const h = vi.hoisted(() => ({ editor: { focus: vi.fn() } as { focus: () => void } | undefined }));
vi.mock("../../editor/editors", () => ({ editorForTab: () => h.editor }));

import { cycleFocus } from "../focusRegions";

/** jsdom has no layout: mark which regions are on screen. */
function shown(el: HTMLElement, on = true) {
  el.getClientRects = () => (on ? [{}] : []) as unknown as DOMRectList;
}

let editorArea: HTMLTextAreaElement;
beforeEach(() => {
  document.body.innerHTML = `
    <div data-focus-region="toolbar"><div role="toolbar"><button tabindex="-1">Back</button><button tabindex="0">Run</button></div></div>
    <div class="monaco-editor"><textarea></textarea></div>
    <div data-focus-region="results" id="hidden"><button>Results (other tab)</button></div>
    <div data-focus-region="results" id="results"><button disabled>Off</button><button>Results</button><button>Messages</button></div>`;
  shown(document.querySelector('[data-focus-region="toolbar"]')!);
  shown(document.getElementById("results")!);
  shown(document.getElementById("hidden")!, false);
  editorArea = document.querySelector("textarea")!;
  h.editor = { focus: vi.fn(() => editorArea.focus()) };
  useTabs.setState({ activeId: "t1" });
});
afterEach(() => (document.body.innerHTML = ""));

const focused = () => document.activeElement?.textContent || document.activeElement?.tagName;

describe("F6 focus areas", () => {
  it("F6 goes top bar → editor → results → top bar; the bar takes focus at its current tab stop", () => {
    cycleFocus(1);
    expect(focused()).toBe("Run");
    cycleFocus(1);
    expect(h.editor!.focus).toHaveBeenCalled();
    expect(focused()).toBe("TEXTAREA");
    cycleFocus(1);
    expect(focused()).toBe("Results"); // the visible tab's panel, its first enabled control
    cycleFocus(1);
    expect(focused()).toBe("Run");
  });

  it("Shift+F6 goes the other way, starting from the last area", () => {
    cycleFocus(-1);
    expect(focused()).toBe("Results");
    cycleFocus(-1);
    expect(focused()).toBe("TEXTAREA");
    cycleFocus(-1);
    expect(focused()).toBe("Run");
  });

  it("skips an area that is not there: no editor (no tab) and a closed results panel", () => {
    h.editor = undefined;
    document.getElementById("results")!.remove();
    cycleFocus(1);
    expect(focused()).toBe("Run");
    cycleFocus(1);
    expect(focused()).toBe("Run");
  });
});

describe("F6 and the Object Explorer (B15)", () => {
  beforeEach(() => {
    const ex = document.createElement("div");
    ex.dataset.focusRegion = "explorer";
    ex.innerHTML = `<div role="toolbar"><button>Connect…</button></div><input aria-label="Search objects"><div role="tree" tabindex="0" aria-label="Object Explorer"></div>`;
    document.querySelector('[data-focus-region="toolbar"]')!.after(ex);
    shown(ex);
  });

  it("F6 goes top bar → Explorer tree → editor; Shift+F6 comes back to the tree", () => {
    cycleFocus(1);
    expect(focused()).toBe("Run");
    cycleFocus(1);
    expect(document.activeElement?.getAttribute("role")).toBe("tree");
    cycleFocus(1);
    expect(focused()).toBe("TEXTAREA");
    cycleFocus(-1);
    expect(document.activeElement?.getAttribute("role")).toBe("tree");
  });

  it("skips an auto-hidden Explorer that is slid in (aria-hidden), even though it is laid out", () => {
    const ex = document.querySelector<HTMLElement>('[data-focus-region="explorer"]')!;
    const wrap = document.createElement("div");
    wrap.setAttribute("aria-hidden", "true");
    ex.replaceWith(wrap);
    wrap.append(ex);
    cycleFocus(1);
    cycleFocus(1);
    expect(focused()).toBe("TEXTAREA");
  });
});

describe("F6 and the tab strip (D22)", () => {
  beforeEach(() => {
    const strip = document.createElement("div");
    strip.className = "dv-tabs-container";
    strip.setAttribute("role", "tablist");
    strip.innerHTML = `<div role="tab" tabindex="-1">one</div><div role="tab" tabindex="0">two</div>`;
    document.querySelector('[data-focus-region="toolbar"]')!.after(strip);
    for (const t of strip.querySelectorAll<HTMLElement>('[role="tab"]')) shown(t);
  });

  it("F6 goes top bar → the strip's Tab stop (the shown tab) → editor; Shift+F6 from the editor comes back to it", () => {
    cycleFocus(1);
    expect(focused()).toBe("Run");
    cycleFocus(1);
    expect(focused()).toBe("two");
    cycleFocus(1);
    expect(focused()).toBe("TEXTAREA");
    cycleFocus(-1);
    expect(focused()).toBe("two");
  });
});

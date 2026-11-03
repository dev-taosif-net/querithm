// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { focusedTabId, handleStripKey, MENU_EVENT, RENAME_EVENT, syncStripAria, type StripActions } from "../stripKeyboard";

/** A Dockview-like api: groups with a strip element and panels whose header sits inside a .dv-tab. */
function dock(groups: string[][], shown: string[]) {
  const made = groups.map((ids, gi) => {
    const element = document.createElement("div");
    const strip = document.createElement("div");
    strip.className = "dv-tabs-container";
    element.appendChild(strip);
    document.body.appendChild(element);
    const panels = ids.map((id) => {
      const tab = document.createElement("div");
      tab.className = "dv-tab";
      tab.tabIndex = 0;
      const header = document.createElement("div");
      tab.appendChild(header);
      strip.appendChild(tab);
      const content = document.createElement("div");
      return { id, view: { tab: { element: header }, content: { element: content } } };
    });
    return { element, panels, activePanel: panels.find((p) => p.id === shown[gi]) };
  });
  return { groups: made } as any;
}
const tab = (api: any, id: string) => api.groups.flatMap((g: any) => g.panels).find((p: any) => p.id === id).view.tab.element.parentElement as HTMLElement;
const key = (_el: HTMLElement, k: string, o: Partial<KeyboardEventInit> = {}) => new KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true, ...o });

let actions: StripActions & { [k: string]: any };
beforeEach(() => {
  document.body.innerHTML = "";
  actions = { activate: vi.fn(), close: vi.fn(async () => true), move: vi.fn(), focusEditor: vi.fn() };
});

describe("syncStripAria", () => {
  it("makes each strip a tablist with one Tab stop: the tab it shows (D22)", () => {
    const api = dock([["a", "b", "c"], ["d"]], ["b", "d"]);
    syncStripAria(api);
    const strip = api.groups[0].element.querySelector(".dv-tabs-container");
    expect(strip.getAttribute("role")).toBe("tablist");
    expect(strip.getAttribute("aria-label")).toBe("Editor tabs");
    expect(strip.hasAttribute("data-layout-scroller")).toBe(true);
    expect(["a", "b", "c"].map((id) => [tab(api, id).getAttribute("role"), tab(api, id).getAttribute("aria-selected"), tab(api, id).tabIndex])).toEqual([
      ["tab", "false", -1],
      ["tab", "true", 0],
      ["tab", "false", -1],
    ]);
    expect(tab(api, "d").tabIndex).toBe(0);
    const panel = api.groups[0].panels[1].view.content.element as HTMLElement;
    expect(panel.getAttribute("role")).toBe("tabpanel");
    expect(tab(api, "b").getAttribute("aria-controls")).toBe(panel.id);
  });
});

describe("keys on a focused tab (manual activation)", () => {
  it("arrows, Home and End move focus and the Tab stop without activating; they wrap", () => {
    const api = dock([["a", "b", "c"]], ["a"]);
    syncStripAria(api);
    tab(api, "a").focus();
    const pressOn = (k: string) => {
      const e = key(document.activeElement as HTMLElement, k);
      Object.defineProperty(e, "target", { value: document.activeElement });
      return handleStripKey(e, api, actions);
    };
    expect(pressOn("ArrowRight")).toBe(true);
    expect(focusedTabId(api)).toBe("b");
    expect(tab(api, "b").tabIndex).toBe(0);
    expect(tab(api, "a").tabIndex).toBe(-1);
    pressOn("End");
    expect(focusedTabId(api)).toBe("c");
    pressOn("ArrowRight");
    expect(focusedTabId(api)).toBe("a");
    pressOn("ArrowLeft");
    expect(focusedTabId(api)).toBe("c");
    pressOn("Home");
    expect(focusedTabId(api)).toBe("a");
    expect(actions.activate).not.toHaveBeenCalled();
  });

  const on = (api: any, id: string, k: string, o?: Partial<KeyboardEventInit>) => {
    const el = tab(api, id);
    el.focus();
    const e = key(el, k, o);
    Object.defineProperty(e, "target", { value: el });
    return { handled: handleStripKey(e, api, actions), e };
  };

  it("Enter and Space activate; Escape goes to the editor", () => {
    const api = dock([["a", "b"]], ["a"]);
    expect(on(api, "b", "Enter").handled).toBe(true);
    expect(actions.activate).toHaveBeenCalledWith("b");
    on(api, "a", " ");
    expect(actions.activate).toHaveBeenLastCalledWith("a");
    on(api, "a", "Escape");
    expect(actions.focusEditor).toHaveBeenCalled();
  });

  it("Delete closes through the guarded close and keeps focus in the strip", async () => {
    const api = dock([["a", "b", "c"]], ["a"]);
    actions.close = vi.fn(async (id: string) => {
      api.groups[0].panels = api.groups[0].panels.filter((p: any) => p.id !== id);
      return true;
    }) as any;
    on(api, "b", "Delete");
    await Promise.resolve();
    await Promise.resolve();
    expect(actions.close).toHaveBeenCalledWith("b");
    expect(focusedTabId(api)).toBe("c");
  });

  it("Delete on a tab that stays (a cancelled close) keeps focus on it", async () => {
    const api = dock([["a", "b"]], ["a"]);
    actions.close = vi.fn(async () => false) as any;
    on(api, "b", "Delete");
    await Promise.resolve();
    await Promise.resolve();
    expect(focusedTabId(api)).toBe("b");
  });

  it("F2 asks the header to rename; Shift+F10 and the Menu key open its menu", () => {
    const api = dock([["a"]], ["a"]);
    const el = tab(api, "a");
    const rename = vi.fn();
    const menu = vi.fn();
    el.addEventListener(RENAME_EVENT, rename);
    el.addEventListener(MENU_EVENT, menu);
    on(api, "a", "F2");
    on(api, "a", "F10", { shiftKey: true });
    on(api, "a", "ContextMenu");
    expect(rename).toHaveBeenCalledTimes(1);
    expect(menu).toHaveBeenCalledTimes(2);
  });

  it("leaves other keys, keys with modifiers, and keys typed in the rename field alone", () => {
    const api = dock([["a", "b"]], ["a"]);
    expect(on(api, "a", "x").handled).toBe(false);
    expect(on(api, "a", "ArrowRight", { ctrlKey: true }).handled).toBe(false);
    const input = document.createElement("input");
    tab(api, "a").appendChild(input);
    const e = key(input, "Delete");
    Object.defineProperty(e, "target", { value: input });
    expect(handleStripKey(e, api, actions)).toBe(false);
    const outside = document.createElement("button");
    const e2 = key(outside, "Enter");
    Object.defineProperty(e2, "target", { value: outside });
    expect(handleStripKey(e2, api, actions)).toBe(false);
  });
});

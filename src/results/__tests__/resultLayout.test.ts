import { beforeEach, describe, expect, it, vi } from "vitest";

const { toast } = vi.hoisted(() => ({ toast: vi.fn() }));
vi.mock("../../store/ui", async (orig) => ({ ...(await orig<typeof import("../../store/ui")>()), toast }));

import { useSettings } from "../../store/settings";
import { useTabs } from "../../store/tabs";
import { GRID_HEADER_PX, GRID_ROW_PX } from "../gridScale";
import {
  chosenLayout, cleanLayout, COLLAPSE_AFTER, fitHeight, heightRange, keptPins, MAX_MOUNTED, MAX_PINS, mountedPanes, pinShown, pinUnavailable, setResultLayout,
  shownLayout, sideColumns, SIDE_TOO_NARROW, toggleResultMaximized, togglePin,
} from "../resultLayout";

const tab = (over = {}) => ({ id: "t1", title: "q.sql", executions: ["e1", "e2"], activeExecutionId: "e2", ...over }) as never;

beforeEach(() => {
  toast.mockClear();
  useSettings.setState({ resultLayout: "tabs" } as never);
  useTabs.setState({ tabs: { t1: tab() } });
});

describe("layout choice", () => {
  it("cleanLayout keeps the three layouts and drops anything else (the workspace file is untrusted)", () => {
    expect(["tabs", "stacked", "side"].map(cleanLayout)).toEqual(["tabs", "stacked", "side"]);
    for (const bad of ["Stacked", "", 1, null, undefined, {}, ["side"]]) expect(cleanLayout(bad)).toBeUndefined();
  });

  it("a tab's own choice wins; without one the Settings default applies", () => {
    expect(chosenLayout({}, "stacked")).toBe("stacked");
    expect(chosenLayout({ resultLayout: "side" }, "stacked")).toBe("side");
    useSettings.setState({ resultLayout: "side" } as never);
    expect(chosenLayout({})).toBe("side");
  });

  it("side by side falls back to Stacked on a narrow panel and says why; the others draw as chosen", () => {
    expect(shownLayout("side", "narrow")).toEqual({ layout: "stacked", why: SIDE_TOO_NARROW });
    expect(shownLayout("side", "mid")).toEqual({ layout: "side" });
    expect(shownLayout("stacked", "narrow")).toEqual({ layout: "stacked" });
    expect(shownLayout("tabs", "narrow")).toEqual({ layout: "tabs" });
  });

  it("setResultLayout stores the tab's choice, ends a maximized set and shows the Results view", () => {
    useTabs.getState().update("t1", { resultMaximized: true, bottomTab: "messages", bottomOpen: false });
    setResultLayout("t1", "stacked");
    expect(useTabs.getState().tabs.t1).toMatchObject({ resultLayout: "stacked", resultMaximized: undefined, bottomTab: "results", bottomOpen: true });
    setResultLayout("gone", "side"); // a closed tab: nothing happens
  });

  it("maximize toggles in Stacked / Side by side; in Tabs it says one set already shows at a time", () => {
    toggleResultMaximized("t1");
    expect(useTabs.getState().tabs.t1.resultMaximized).toBeUndefined();
    expect(toast).toHaveBeenCalledWith("info", "Tabs already shows one result set at a time.");
    useTabs.getState().update("t1", { resultLayout: "stacked" });
    toggleResultMaximized("t1");
    expect(useTabs.getState().tabs.t1.resultMaximized).toBe(true);
    toggleResultMaximized("t1");
    expect(useTabs.getState().tabs.t1.resultMaximized).toBeUndefined();
  });
});

describe("stacked sizes and the mount window", () => {
  const min = GRID_HEADER_PX + 3 * GRID_ROW_PX + 14;
  it("a pane fits its rows, between 3 rows and 60 % of the panel", () => {
    expect(fitHeight(0, 1000)).toBe(min);
    expect(fitHeight(1, 1000)).toBe(min);
    expect(fitHeight(10, 1000)).toBe(GRID_HEADER_PX + 10 * GRID_ROW_PX + 14);
    expect(fitHeight(1_000_000, 1000)).toBe(600);
    // A panel too low for 60 % to hold 3 rows still gets 3 rows.
    expect(fitHeight(1_000_000, 50)).toBe(min);
  });

  it("the splitter's range: 3 rows up to 90 % of the panel", () => {
    expect(heightRange(1000)).toEqual({ min, max: 900 });
    expect(heightRange(10)).toEqual({ min, max: min });
  });

  it("mounts the kept panes and the visible ones, up to the cap", () => {
    const order = Array.from({ length: 30 }, (_, i) => `k${i}`);
    expect([...mountedPanes(order, null, new Set())]).toEqual(order.slice(0, MAX_MOUNTED));
    const visible = new Set(["k3", "k4", "k5"]);
    expect([...mountedPanes(order, visible, new Set(["k20"]))].sort()).toEqual(["k20", "k3", "k4", "k5"]);
    // Many tiny visible panes: the cap holds; the kept pane is always in.
    const all = new Set(order);
    const m = mountedPanes(order, all, new Set(["k29"]));
    expect(m.size).toBe(MAX_MOUNTED);
    expect(m.has("k29")).toBe(true);
    // A kept key that is not a pane (an error is selected) mounts nothing extra.
    expect(mountedPanes(order, visible, new Set(["err:0"])).has("err:0")).toBe(false);
    expect(COLLAPSE_AFTER).toBe(20);
  });
});

describe("side by side columns", () => {
  it("fills the columns in order, keeps the tab's choice and always shows the selected set", () => {
    expect(sideColumns([0, 1, 2, 3, 4], undefined, 0, 3)).toEqual([0, 1, 2]);
    expect(sideColumns([0, 1, 2, 3, 4], [4, 2], 2, 3)).toEqual([4, 2, 0]);
    expect(sideColumns([0, 1, 2, 3, 4], [0, 1, 2], 4, 3)).toEqual([0, 1, 4]);
    // Sets that no longer exist and repeats in the saved choice are skipped.
    expect(sideColumns([0, 1], [7, 1, 1], undefined, 3)).toEqual([1, 0]);
    // Fewer slots (pinned sets take the others) and fewer sets than slots.
    expect(sideColumns([0, 1, 2], undefined, 2, 1)).toEqual([2]);
    expect(sideColumns([], undefined, undefined, 3)).toEqual([]);
  });
});

describe("pins", () => {
  it("pins and unpins a set of a kept run, at most two, and says why it cannot", () => {
    expect(togglePin("t1", { executionId: "e1", resultIndex: 0 })).toBeNull();
    expect(togglePin("t1", { executionId: "e2", resultIndex: 1 })).toBeNull();
    expect(useTabs.getState().tabs.t1.pins).toHaveLength(MAX_PINS);
    expect(togglePin("t1", { executionId: "e2", resultIndex: 2 })).toBe("Unpin a result set first: up to 2 stay pinned");
    expect(togglePin("t1", { executionId: "e1", resultIndex: 0 })).toBeNull();
    expect(useTabs.getState().tabs.t1.pins).toEqual([{ executionId: "e2", resultIndex: 1 }]);
    expect(togglePin("t1", { executionId: "e9", resultIndex: 0 })).toBe("That run is no longer kept");
    expect(togglePin("gone", { executionId: "e1", resultIndex: 0 })).toBeNull();
  });

  it("pinning is refused in Tabs (pins show in Stacked and Side by side), with the reason", () => {
    expect(pinUnavailable("tabs")).toMatch(/Stacked and Side by side/);
    expect(pinUnavailable("stacked")).toBeNull();
    pinShown("t1");
    expect(toast).toHaveBeenCalledWith("info", "Pinned result sets show in the Stacked and Side by side layouts.");
    expect(useTabs.getState().tabs.t1.pins).toBeUndefined();
    useTabs.getState().update("t1", { resultLayout: "side", resultIndex: 1 });
    pinShown("t1");
    expect(useTabs.getState().tabs.t1.pins).toEqual([{ executionId: "e2", resultIndex: 1 }]);
    useTabs.getState().update("t1", { activeExecutionId: undefined });
    pinShown("t1");
    expect(toast).toHaveBeenLastCalledWith("info", "Run a query first.");
  });

  it("keptPins drops the pins of runs the tab no longer keeps", () => {
    const pins = [{ executionId: "e1", resultIndex: 0 }, { executionId: "e2", resultIndex: 0 }];
    expect(keptPins(pins, ["e1", "e2"])).toBe(pins);
    expect(keptPins(pins, ["e2", "e3"])).toEqual([{ executionId: "e2", resultIndex: 0 }]);
    expect(keptPins(pins, ["e3"])).toBeUndefined();
    expect(keptPins(undefined, ["e3"])).toBeUndefined();
  });
});

import { describe, expect, it } from "vitest";
import { modeOf, nextExplorer, type ExplorerLayout } from "../layoutState";

const pinned: ExplorerLayout = { visible: true, autoHide: false, flyout: false };
const autoHide: ExplorerLayout = { visible: true, autoHide: true, flyout: false };

describe("explorer layout", () => {
  it("Ctrl+B closes and re-opens a pinned explorer", () => {
    const closed = nextExplorer(pinned, "toggle");
    expect(modeOf(closed)).toBe("hidden");
    expect(nextExplorer(closed, "toggle")).toEqual(pinned);
  });

  it("Ctrl+B slides an auto-hide explorer in and out", () => {
    const out = nextExplorer(autoHide, "toggle");
    expect(out.flyout).toBe(true);
    expect(nextExplorer(out, "toggle")).toEqual(autoHide);
  });

  it("closing remembers auto-hide; re-opening flies it out", () => {
    const closed = nextExplorer({ ...autoHide, flyout: true }, "close");
    expect(closed).toEqual({ visible: false, autoHide: true, flyout: false });
    expect(nextExplorer(closed, "toggle")).toEqual({ ...autoHide, flyout: true });
    expect(nextExplorer(closed, "show")).toEqual({ ...autoHide, flyout: true });
  });

  it("the pushpin switches between docked and auto-hide and folds the flyout", () => {
    expect(nextExplorer(pinned, "togglePin")).toEqual(autoHide);
    expect(nextExplorer({ ...autoHide, flyout: true }, "togglePin")).toEqual(pinned);
  });

  it("hover fly-out only applies in auto-hide", () => {
    expect(nextExplorer(pinned, "flyOut")).toBe(pinned);
    expect(nextExplorer({ ...autoHide, visible: false }, "flyOut").flyout).toBe(false);
    expect(nextExplorer(autoHide, "flyOut").flyout).toBe(true);
    expect(nextExplorer({ ...autoHide, flyout: true }, "dismiss")).toEqual(autoHide);
  });

  it("explicit modes", () => {
    expect(nextExplorer(pinned, { mode: "autoHide" })).toEqual(autoHide);
    expect(nextExplorer(autoHide, { mode: "hidden" })).toEqual({ ...autoHide, visible: false });
    expect(nextExplorer({ ...autoHide, visible: false }, { mode: "pinned" })).toEqual(pinned);
  });
});

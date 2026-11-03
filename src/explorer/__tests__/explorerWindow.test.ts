import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => undefined), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));

import { explorerAction, explorerHooks } from "../explorerWindow";
import { useSettings } from "../../store/settings";
import { useUi } from "../../store/ui";

beforeEach(() => {
  useSettings.setState({ explorerVisible: true, explorerAutoHide: false });
  useUi.setState({ explorerFlyout: false });
  vi.stubGlobal("requestAnimationFrame", (f: () => void) => (f(), 1));
});
afterEach(() => {
  vi.unstubAllGlobals();
  explorerHooks.focusSearch = undefined;
});

describe("explorerAction", () => {
  it("close hides the window and persists the setting", () => {
    explorerAction("close");
    expect(useSettings.getState().explorerVisible).toBe(false);
  });
  it("togglePin switches to auto-hide without a flyout", () => {
    explorerAction("togglePin");
    expect(useSettings.getState().explorerAutoHide).toBe(true);
    expect(useUi.getState().explorerFlyout).toBe(false);
  });
  it("flyOut then dismiss only touches the transient flyout flag", () => {
    useSettings.setState({ explorerAutoHide: true });
    explorerAction("flyOut");
    expect(useUi.getState().explorerFlyout).toBe(true);
    expect(useSettings.getState().explorerVisible).toBe(true);
    explorerAction("dismiss");
    expect(useUi.getState().explorerFlyout).toBe(false);
  });
  it("show makes the window visible and focuses the search box", () => {
    const focus = vi.fn();
    explorerHooks.focusSearch = focus;
    useSettings.setState({ explorerVisible: false, explorerAutoHide: true });
    explorerAction("show");
    expect(useSettings.getState().explorerVisible).toBe(true);
    expect(useUi.getState().explorerFlyout).toBe(true);
    expect(focus).toHaveBeenCalled();
  });
  it("show without a registered search box does not throw", () => {
    expect(() => explorerAction("show")).not.toThrow();
  });
  it("an action with no effect leaves the stores alone", () => {
    const before = useSettings.getState();
    explorerAction("dismiss");
    expect(useSettings.getState().explorerVisible).toBe(before.explorerVisible);
  });
});

describe("dock width in a small window", () => {
  it("gives way so the workspace keeps 480 px, never below 200 or above 600; the stored width is untouched", async () => {
    const { shownExplorerWidth } = await import("../explorerWindow");
    expect(shownExplorerWidth(600, 960)).toBe(480);
    expect(shownExplorerWidth(300, 960)).toBe(300);
    expect(shownExplorerWidth(600, 1440)).toBe(600);
    expect(shownExplorerWidth(900, 2560)).toBe(600);
    expect(shownExplorerWidth(400, 600)).toBe(200);
  });
});

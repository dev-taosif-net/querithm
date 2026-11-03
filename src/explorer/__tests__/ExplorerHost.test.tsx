// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";

const m = vi.hoisted(() => ({ focus: vi.fn(), focusSearch: vi.fn() }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => undefined), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));
vi.mock("../Explorer", () => ({ Explorer: () => <input aria-label="inner search" /> }));
vi.mock("../../editor/editors", () => ({ editorForTab: () => ({ focus: m.focus }) }));

import { ExplorerHost } from "../ExplorerHost";
import { DropMenu } from "../../app/ToolbarMenu";
import { explorerHooks } from "../explorerWindow";
import { useConnections } from "../../store/connections";
import { useProfiles } from "../../store/profiles";
import { useSettings } from "../../store/settings";
import { useTabs } from "../../store/tabs";
import { useUi } from "../../store/ui";

const setMode = (visible: boolean, autoHide: boolean, extra: object = {}) => useSettings.setState({ explorerVisible: visible, explorerAutoHide: autoHide, explorerHoverOpen: true, reducedDecorations: false, ...extra } as never);
const panel = () => screen.getByLabelText("inner search").closest("[tabindex='-1']") as HTMLElement;

beforeEach(() => {
  vi.useFakeTimers();
  m.focus.mockClear();
  useUi.setState({ explorerFlyout: false, explorerWidth: 300 });
  useTabs.setState({ activeId: "t1" } as never);
  useConnections.setState({ servers: {}, order: [], disconnected: {} });
  useProfiles.setState({ profiles: [], groups: [] });
  useSettings.setState({ pinnedProfiles: [] });
  setMode(true, false);
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("ExplorerHost", () => {
  it("docked: shows the panel at the stored width with a resize handle and no side tab", () => {
    render(<ExplorerHost />);
    expect(panel().style.width).toBe("300px");
    expect(panel().getAttribute("aria-hidden")).toBe("false");
    expect(screen.queryByTitle("Object Explorer (Ctrl+B)")).toBeNull();
  });

  it("closed: keeps the Explorer mounted but hidden, with no side tab", () => {
    setMode(false, false);
    render(<ExplorerHost />);
    expect(screen.getByLabelText("inner search")).toBeTruthy();
    expect(panel().getAttribute("aria-hidden")).toBe("true");
    expect(screen.queryByTitle("Object Explorer (Ctrl+B)")).toBeNull();
  });

  it("auto-hide: shows a side tab and a hidden panel; clicking the tab flies it out and focuses search", () => {
    setMode(true, true);
    render(<ExplorerHost />);
    expect(panel().style.visibility).toBe("hidden");
    fireEvent.click(screen.getByTitle("Object Explorer (Ctrl+B)"));
    expect(useUi.getState().explorerFlyout).toBe(true);
    expect(useSettings.getState().explorerVisible).toBe(true);
  });

  it("side tab: the vertical text sits on a span, not the button (WebKit ignores writing-mode on buttons)", () => {
    setMode(true, true);
    render(<ExplorerHost />);
    const tab = screen.getByTitle("Object Explorer (Ctrl+B)");
    expect(tab.className).not.toContain("writing-mode");
    expect(tab.querySelector("span")?.className).toContain("[writing-mode:vertical-rl]");
  });

  it("clicking the tab while out focuses the search box", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    explorerHooks.focusSearch = m.focusSearch;
    render(<ExplorerHost />);
    fireEvent.click(screen.getByTitle("Object Explorer (Ctrl+B)"));
    expect(m.focusSearch).toHaveBeenCalled();
    explorerHooks.focusSearch = undefined;
  });

  it("hovering the tab opens the flyout after a delay, and leaving closes it", () => {
    setMode(true, true);
    render(<ExplorerHost />);
    const tab = screen.getByTitle("Object Explorer (Ctrl+B)");
    fireEvent.mouseEnter(tab);
    expect(useUi.getState().explorerFlyout).toBe(false);
    act(() => void vi.advanceTimersByTime(350));
    expect(useUi.getState().explorerFlyout).toBe(true);
    fireEvent.mouseLeave(tab);
    act(() => void vi.advanceTimersByTime(600));
    expect(useUi.getState().explorerFlyout).toBe(false);
  });

  it("does not open on hover when hover-open is off", () => {
    setMode(true, true, { explorerHoverOpen: false });
    render(<ExplorerHost />);
    fireEvent.mouseEnter(screen.getByTitle("Object Explorer (Ctrl+B)"));
    act(() => void vi.advanceTimersByTime(1000));
    expect(useUi.getState().explorerFlyout).toBe(false);
  });

  it("re-entering the flyout cancels the pending close", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    render(<ExplorerHost />);
    fireEvent.mouseLeave(panel());
    act(() => void vi.advanceTimersByTime(200));
    fireEvent.mouseEnter(panel());
    act(() => void vi.advanceTimersByTime(1000));
    expect(useUi.getState().explorerFlyout).toBe(true);
  });

  it("leaving the panel while focus is inside it keeps the flyout", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    render(<ExplorerHost />);
    screen.getByLabelText("inner search").focus();
    fireEvent.mouseLeave(panel());
    act(() => void vi.advanceTimersByTime(1000));
    expect(useUi.getState().explorerFlyout).toBe(true);
  });

  it("a press outside a flyout dismisses it, a press inside does not", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    render(<ExplorerHost />);
    fireEvent.pointerDown(screen.getByLabelText("inner search"));
    expect(useUi.getState().explorerFlyout).toBe(true);
    fireEvent.pointerDown(document.body);
    expect(useUi.getState().explorerFlyout).toBe(false);
  });

  it("Escape in the flyout dismisses it and returns focus to the editor; a non-empty search keeps it", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    render(<ExplorerHost />);
    const input = screen.getByLabelText("inner search") as HTMLInputElement;
    input.value = "abc";
    fireEvent.keyDown(input, { key: "Escape" });
    expect(useUi.getState().explorerFlyout).toBe(true);
    input.value = "";
    fireEvent.keyDown(input, { key: "a" });
    expect(useUi.getState().explorerFlyout).toBe(true);
    fireEvent.keyDown(input, { key: "Escape" });
    expect(useUi.getState().explorerFlyout).toBe(false);
    expect(m.focus).toHaveBeenCalled();
  });

  it("Escape does nothing when the panel is docked", () => {
    render(<ExplorerHost />);
    fireEvent.keyDown(screen.getByLabelText("inner search"), { key: "Escape" });
    expect(m.focus).not.toHaveBeenCalled();
  });

  it("blur to an element outside the flyout dismisses it", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    render(
      <>
        <button>elsewhere</button>
        <ExplorerHost />
      </>,
    );
    const other = screen.getByText("elsewhere");
    other.focus();
    fireEvent.blur(panel());
    act(() => void vi.advanceTimersByTime(5));
    expect(useUi.getState().explorerFlyout).toBe(false);
  });

  it("a menu opened from the flyout (portaled to the body) counts as inside: focus, a press and the pointer on it keep the flyout", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    render(<ExplorerHost />);
    const anchor = { current: screen.getByLabelText("inner search") as HTMLElement };
    render(<DropMenu anchor={anchor} at={{ x: 10, y: 10 }} label="Actions" items={[{ label: "Copy name", run: () => {} }]} onClose={() => {}} />);
    const entry = screen.getByRole("menuitem", { name: "Copy name" });
    expect(document.activeElement).toBe(entry);
    fireEvent.blur(panel());
    fireEvent.pointerDown(entry);
    fireEvent.mouseLeave(panel());
    act(() => void vi.advanceTimersByTime(1000));
    expect(useUi.getState().explorerFlyout).toBe(true);
    // A menu that does not belong to the flyout is outside.
    const other = document.createElement("button");
    document.body.append(other);
    render(<DropMenu anchor={{ current: other }} label="Other" items={[{ label: "Elsewhere", run: () => {} }]} onClose={() => {}} />);
    fireEvent.pointerDown(screen.getByRole("menuitem", { name: "Elsewhere" }));
    expect(useUi.getState().explorerFlyout).toBe(false);
    other.remove();
  });

  it("blur to the document body (window blur) keeps the flyout", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    render(<ExplorerHost />);
    fireEvent.blur(panel());
    act(() => void vi.advanceTimersByTime(5));
    expect(useUi.getState().explorerFlyout).toBe(true);
  });

  it("dragging an item lets the drop through and folds the flyout at drag end", () => {
    setMode(true, true);
    useUi.setState({ explorerFlyout: true });
    render(<ExplorerHost />);
    fireEvent.dragStart(panel());
    expect(panel().style.pointerEvents).toBe("none");
    fireEvent.dragEnd(panel());
    expect(panel().style.pointerEvents).toBe("");
    expect(useUi.getState().explorerFlyout).toBe(false);
  });

  it("dragging while docked leaves pointer events alone", () => {
    render(<ExplorerHost />);
    fireEvent.dragStart(panel());
    expect(panel().style.pointerEvents).toBe("");
    fireEvent.dragEnd(panel());
  });

  it("drags the right edge to resize, clamped to 200..600", () => {
    render(<ExplorerHost />);
    const handle = panel().querySelector(".cursor-col-resize") as HTMLElement;
    fireEvent.mouseDown(handle);
    fireEvent.mouseMove(window, { clientX: 450 });
    expect(useUi.getState().explorerWidth).toBe(450);
    fireEvent.mouseMove(window, { clientX: 5000 });
    expect(useUi.getState().explorerWidth).toBe(600);
    fireEvent.mouseMove(window, { clientX: 1 });
    expect(useUi.getState().explorerWidth).toBe(200);
    fireEvent.mouseUp(window);
    fireEvent.mouseMove(window, { clientX: 400 });
    expect(useUi.getState().explorerWidth).toBe(200);
  });

  it("the side tab shows one mark per server in use, shaped by state, failed ones included (B16), for both engines", () => {
    setMode(true, true);
    useProfiles.setState({
      profiles: [
        { id: "a", name: "Sales", engine: "mssql", environment: "prod" },
        { id: "b", name: "Billing", engine: "pg", environment: "dev" },
        { id: "d", name: "Replica", engine: "pg", environment: "staging" },
        { id: "e", name: "Archive", engine: "mssql", environment: "test" },
      ] as never,
    });
    useSettings.setState({ pinnedProfiles: ["e"] }); // pinned, not connected: no mark
    useConnections.setState({
      order: ["a", "b", "c", "d"],
      servers: {
        a: { profileId: "a", status: "connected", databases: [] },
        b: { profileId: "b", status: "connecting", databases: [] },
        c: { profileId: "c", status: "connected", databases: [] }, // its profile was deleted: not in the tree either
        d: { profileId: "d", status: "error", error: "timeout", databases: [] },
      },
    } as never);
    render(<ExplorerHost />);
    expect(screen.getByTitle("Prod Sales, connected").dataset.mark).toBe("connected");
    expect(screen.getByTitle("Dev Billing, connecting").dataset.mark).toBe("connecting");
    expect(screen.getByTitle("Stage Replica, connection failed").dataset.mark).toBe("error");
    expect(screen.queryByTitle(/Archive/)).toBeNull();
    expect(document.querySelectorAll("[data-mark]")).toHaveLength(3);
    // Local → Prod, as in the tree.
    expect([...document.querySelectorAll<HTMLElement>("[data-mark]")].map((m) => m.title.split(",")[0])).toEqual(["Dev Billing", "Stage Replica", "Prod Sales"]);
    expect(screen.getByRole("button", { name: "Object Explorer, 1 connected, 1 connecting, 1 failed" }).getAttribute("aria-expanded")).toBe("false");
  });

  it("a wide dock gives way in the 960 px minimum window and comes back when it widens; the stored width is kept", () => {
    useUi.setState({ explorerWidth: 600 });
    Object.defineProperty(window, "innerWidth", { value: 960, configurable: true });
    render(<ExplorerHost />);
    expect(panel().style.width).toBe("480px");
    act(() => {
      Object.defineProperty(window, "innerWidth", { value: 1440, configurable: true });
      window.dispatchEvent(new Event("resize"));
    });
    expect(panel().style.width).toBe("600px");
    expect(useUi.getState().explorerWidth).toBe(600);
    Object.defineProperty(window, "innerWidth", { value: 1024, configurable: true }); // jsdom's default
  });

  it("the resize edge is a keyboard separator: ←/→ move 16 px, Home/End go to 200 / 600", () => {
    render(<ExplorerHost />);
    const sep = screen.getByRole("separator", { name: "Resize the Object Explorer" });
    expect(sep.getAttribute("aria-valuenow")).toBe("300");
    fireEvent.keyDown(sep, { key: "ArrowRight" });
    expect(useUi.getState().explorerWidth).toBe(316);
    fireEvent.keyDown(sep, { key: "ArrowLeft" });
    fireEvent.keyDown(sep, { key: "ArrowLeft" });
    expect(useUi.getState().explorerWidth).toBe(284);
    fireEvent.keyDown(sep, { key: "End" });
    expect(useUi.getState().explorerWidth).toBe(600);
    fireEvent.keyDown(sep, { key: "Home" });
    expect(useUi.getState().explorerWidth).toBe(200);
  });

  it("reduced decorations drop the slide transition", () => {
    setMode(true, true, { reducedDecorations: true });
    render(<ExplorerHost />);
    expect(panel().style.transition).toBe("");
  });

  it("unmounting clears a pending timer", () => {
    setMode(true, true);
    const { unmount } = render(<ExplorerHost />);
    fireEvent.mouseEnter(screen.getByTitle("Object Explorer (Ctrl+B)"));
    unmount();
    act(() => void vi.advanceTimersByTime(1000));
    expect(useUi.getState().explorerFlyout).toBe(false);
  });
});

import { describe, it, expect, vi } from "vitest";

const panel = vi.hoisted(() => ({ stepOutput: vi.fn(), stepError: vi.fn(), compareNext: vi.fn() }));
import { displayKeys, eventToShortcut } from "../commands";

// isMac is read once at import; pin macOS so these run the same on every host.
vi.hoisted(() => vi.stubGlobal("navigator", { platform: "MacIntel" }));

const { tabActions } = vi.hoisted(() => ({
  tabActions: { closeTabs: vi.fn(), gotoTab: vi.fn(), nextTab: vi.fn(), switchTab: vi.fn(), togglePin: vi.fn() },
}));
vi.mock("../actions", () => ({
  activeTabState: vi.fn(), runQuery: vi.fn(), cancelQuery: vi.fn(), applyProcedure: vi.fn(), closeTab: vi.fn(), duplicateTab: vi.fn(), formatTab: vi.fn(),
  newQueryTab: vi.fn(), showPlan: vi.fn(), toggleBottom: vi.fn(), toggleMaximize: vi.fn(), stepRun: vi.fn(), toggleTrace: vi.fn(), txAction: vi.fn(),
}));
vi.mock("../persistence", () => ({ openFile: vi.fn(), saveAs: vi.fn(), saveFile: vi.fn() }));
vi.mock("../tabActions", () => tabActions);
vi.mock("../../debug/DebugPanel", () => ({ stepDebug: vi.fn(), stopDebug: vi.fn() }));
vi.mock("../../editor/editorActions", () => ({ goToDefinition: vi.fn(), insertPrintForVariable: vi.fn(), expandWildcardAtCursor: vi.fn(), commentLines: vi.fn() }));
vi.mock("../../erd/erdActions", () => ({ openErdFromActiveTab: vi.fn() }));
vi.mock("../../compare/compareActions", () => ({ openSchemaCompare: vi.fn(), openDataCompare: vi.fn() }));
vi.mock("../../refactor/refactorActions", () => ({ findUsagesAtCursor: vi.fn(), renameAtCursor: vi.fn() }));
vi.mock("../../explorer/explorerWindow", () => ({ explorerAction: vi.fn() }));
vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => undefined), onNotify: () => () => {} }));
vi.mock("../navigation", () => ({ goBack: vi.fn(), goForward: vi.fn() }));
vi.mock("../../results/panelActions", () => panel);
vi.mock("../focusRegions", () => ({ cycleFocus: vi.fn() }));

const key = (o: Partial<KeyboardEvent>) =>
  ({ key: "", code: "", ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...o }) as KeyboardEvent;

describe("commands: shortcuts from key events (mac)", () => {
  it("Cmd is Ctrl", () => expect(eventToShortcut(key({ key: "s", code: "KeyS", metaKey: true }))).toBe("Ctrl+S"));
  it("the Control key is MacCtrl", () => {
    expect(eventToShortcut(key({ key: "a", code: "KeyA", ctrlKey: true }))).toBe("MacCtrl+A");
    expect(eventToShortcut(key({ key: "Enter", code: "Enter", metaKey: true, ctrlKey: true, shiftKey: true }))).toBe("Ctrl+MacCtrl+Shift+Enter");
  });
  it("the tab switcher uses Control+Tab (Cmd+Tab belongs to macOS)", async () => {
    const { commands } = await import("../commands");
    expect(commands.find((c) => c.id === "tab.switchRecent")!.keys).toContain(eventToShortcut(key({ key: "Tab", code: "Tab", ctrlKey: true })));
  });
  it("go back/forward use Control+- / Control+Shift+- (Option+Left moves by word in the editor)", async () => {
    const { commands, keysFor } = await import("../commands");
    const keysOf = (id: string) => keysFor(commands.find((c) => c.id === id)!);
    expect(keysOf("nav.back")).toEqual([eventToShortcut(key({ key: "-", code: "Minus", ctrlKey: true }))]);
    expect(keysOf("nav.forward")).toEqual([eventToShortcut(key({ key: "_", code: "Minus", ctrlKey: true, shiftKey: true }))]);
    expect(keysOf("nav.back")).not.toContain("Alt+Left");
  });
  it("shortcuts display with mac symbols", () => expect(displayKeys("Ctrl+Shift+Alt+F")).toBe("⌘⇧⌥F"));
  it("the Control key shows as ⌃, never as Cmd", () => expect(displayKeys("MacCtrl+Tab")).toBe("⌃Tab"));
});

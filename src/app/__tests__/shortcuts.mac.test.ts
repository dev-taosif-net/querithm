import { describe, it, expect, vi } from "vitest";
import { shortcutProblem, spokenKeys } from "../shortcuts";

// isMac is read once at import; pin macOS so these run the same on every host.
vi.hoisted(() => vi.stubGlobal("navigator", { platform: "MacIntel" }));

describe("shortcuts on macOS", () => {
  it("speaks Ctrl as Command, Alt as Option and MacCtrl as Control", () => {
    expect(spokenKeys("Ctrl+Shift+F")).toBe("Command Shift F");
    expect(spokenKeys("MacCtrl+Alt+/")).toBe("Control Option Slash");
  });

  it("a key without Command, Option or Control is refused in Mac words and glyphs", () => {
    expect(shortcutProblem("A")).toBe("Add Command, Option or Control: on its own A types or moves in the editor.");
    expect(shortcutProblem("Shift+A")).toContain("on its own ⇧A");
  });

  it("Control alone is enough, and Cmd+Q belongs to the system", () => {
    expect(shortcutProblem("MacCtrl+A")).toBeNull();
    expect(shortcutProblem("Ctrl+Q")).toBe("⌘Q belongs to the system.");
  });
});

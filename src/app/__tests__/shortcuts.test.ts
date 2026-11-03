import { describe, it, expect, vi } from "vitest";
import { canonical, joinBinding, shortcutProblem, splitBinding, splitShortcut, spokenKeys, UNBOUND, usableShortcut } from "../shortcuts";

// isMac is read once at import, and Node's navigator.platform reports the host OS (e.g. "MacIntel" on macOS CI),
// so pin a non-mac platform before shortcuts.ts loads. shortcuts.mac.test.ts covers the macOS wording.
vi.hoisted(() => vi.stubGlobal("navigator", { platform: "Linux x86_64" }));

describe("shortcuts: parsing", () => {
  it("splits modifiers from the key, including a plus or comma key", () => {
    expect(splitShortcut("Ctrl+Shift+F")).toEqual({ mods: ["Ctrl", "Shift"], key: "F" });
    expect(splitShortcut("F5")).toEqual({ mods: [], key: "F5" });
    expect(splitShortcut("Ctrl++")).toEqual({ mods: ["Ctrl"], key: "+" });
    expect(splitShortcut("Ctrl+Shift+,")).toEqual({ mods: ["Ctrl", "Shift"], key: "," });
  });

  it("canonical orders modifiers and upper-cases one-character keys", () => {
    expect(canonical("shift+ctrl+f")).toBe("Ctrl+Shift+F");
    expect(canonical("Ctrl+comma")).toBe("Ctrl+,");
  });

  it("an empty key list is stored as an explicit unbind, which reads back as no keys", () => {
    expect(joinBinding([])).toBe(UNBOUND);
    expect(splitBinding(UNBOUND)).toEqual([]);
    expect(splitBinding(joinBinding(["Ctrl+,", "F9"]))).toEqual(["Ctrl+,", "F9"]);
  });

  it("speaks keys as words for screen readers", () => {
    expect(spokenKeys("Ctrl+Shift+F")).toBe("Control Shift F");
    expect(spokenKeys("Ctrl+/")).toBe("Control Slash");
  });
});

describe("shortcuts: what a user may bind", () => {
  it.each(["Ctrl+Shift+K", "Alt+Shift+A", "F5", "Shift+F12", "Ctrl+Tab", "Alt+Pause", "MacCtrl+-"])("%s is allowed", (sc) =>
    expect(shortcutProblem(sc)).toBeNull(),
  );

  it.each([
    ["A", "Add Ctrl or Alt"],
    ["Shift+A", "Add Ctrl or Alt"],
    ["Enter", "Add Ctrl or Alt"],
    ["Tab", "Tab moves focus"],
    ["Shift+Tab", "Tab moves focus"],
    ["Escape", "Escape cancels"],
    ["Ctrl+Escape", "Escape cancels"],
    ["Alt+F4", "belongs to the system"],
    ["Ctrl+Control", "Press a key"],
    ["Hyper+K", "Unknown modifier"],
    ["Ctrl+C", "copying, pasting"],
    ["Ctrl+Shift+Z", "copying, pasting"],
    ["Ctrl+K", "two-key shortcuts"],
  ])("%s is refused (%s)", (sc, reason) => expect(shortcutProblem(sc)).toContain(reason));

  it("a saved chord is never a user shortcut (the editor owns Ctrl+K chords)", () => {
    expect(usableShortcut("Ctrl+K Ctrl+C")).toBe(false);
    expect(usableShortcut("Ctrl+Shift+L")).toBe(true);
  });
});

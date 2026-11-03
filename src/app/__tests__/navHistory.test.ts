import { describe, it, expect } from "vitest";
import { canGoBack, canGoForward, emptyNav, forgetNav, JUMP_LINES, MAX_ENTRIES, recordNav, type NavEntry, type NavStack } from "../navHistory";

const run = (...es: NavEntry[]) => es.reduce<NavStack>((s, e) => recordNav(s, e), emptyNav());

describe("navHistory: recording", () => {
  it("starts empty with nowhere to go", () => {
    const s = emptyNav();
    expect(canGoBack(s)).toBe(false);
    expect(canGoForward(s)).toBe(false);
  });

  it("a tab switch adds an entry", () => {
    const s = run({ tabId: "a" }, { tabId: "b" });
    expect(s.entries.map((e) => e.tabId)).toEqual(["a", "b"]);
    expect(s.index).toBe(1);
    expect(canGoBack(s)).toBe(true);
  });

  it("the first cursor position in a tab fills its entry", () => {
    const s = run({ tabId: "a" }, { tabId: "a", line: 40, column: 3 });
    expect(s.entries).toEqual([{ tabId: "a", line: 40, column: 3 }]);
  });

  it("small moves update the current entry; a jump of more than JUMP_LINES adds one", () => {
    let s = run({ tabId: "a", line: 1, column: 1 }, { tabId: "a", line: 1 + JUMP_LINES, column: 1 });
    expect(s.entries).toHaveLength(1);
    // Line by line, the entry follows the cursor (it is where the user was last).
    for (let l = 2 + JUMP_LINES; l <= 30; l++) s = recordNav(s, { tabId: "a", line: l, column: 1 });
    expect(s.entries).toEqual([{ tabId: "a", line: 30, column: 1 }]);
    s = recordNav(s, { tabId: "a", line: 31 + JUMP_LINES, column: 5 });
    expect(s.entries.map((e) => e.line)).toEqual([30, 31 + JUMP_LINES]);
  });

  it("re-activating the current tab (no position) changes nothing", () => {
    const s = run({ tabId: "a", line: 5, column: 1 });
    expect(recordNav(s, { tabId: "a" })).toBe(s);
    expect(recordNav(s, { tabId: "a", line: 5, column: 1 })).toBe(s);
  });

  it("recording after going back drops the forward entries", () => {
    const s = { ...run({ tabId: "a" }, { tabId: "b" }, { tabId: "c" }), index: 0 };
    expect(canGoForward(s)).toBe(true);
    const n = recordNav(s, { tabId: "d" });
    expect(n.entries.map((e) => e.tabId)).toEqual(["a", "d"]);
    expect(canGoForward(n)).toBe(false);
  });

  it("keeps at most MAX_ENTRIES, dropping the oldest", () => {
    const s = run(...Array.from({ length: MAX_ENTRIES + 5 }, (_, i) => ({ tabId: `t${i}` })));
    expect(s.entries).toHaveLength(MAX_ENTRIES);
    expect(s.entries[0].tabId).toBe("t5");
    expect(s.index).toBe(MAX_ENTRIES - 1);
  });
});

describe("navHistory: closed tabs", () => {
  it("drops the tab's entries and keeps the index on the same place", () => {
    const s = { ...run({ tabId: "a" }, { tabId: "b" }, { tabId: "c" }, { tabId: "d" }), index: 2 };
    const n = forgetNav(s, "b");
    expect(n.entries.map((e) => e.tabId)).toEqual(["a", "c", "d"]);
    expect(n.entries[n.index].tabId).toBe("c");
  });

  it("closing the current entry's tab moves to the entry before it", () => {
    const s = { ...run({ tabId: "a" }, { tabId: "b" }, { tabId: "c" }), index: 1 };
    const n = forgetNav(s, "b");
    expect(n.entries[n.index].tabId).toBe("a");
  });

  it("merges neighbours left at the same place", () => {
    const n = forgetNav(run({ tabId: "a", line: 1 }, { tabId: "b" }, { tabId: "a", line: 1 }), "b");
    expect(n.entries).toEqual([{ tabId: "a", line: 1 }]);
    expect(n.index).toBe(0);
  });

  it("keeps different lines of the same tab", () => {
    const n = forgetNav(run({ tabId: "a", line: 1 }, { tabId: "b" }, { tabId: "a", line: 80 }), "b");
    expect(n.entries.map((e) => e.line)).toEqual([1, 80]);
  });

  it("an unknown tab is a no-op; forgetting everything empties the stack", () => {
    const s = run({ tabId: "a" });
    expect(forgetNav(s, "zz")).toBe(s);
    expect(forgetNav(s, "a")).toEqual(emptyNav());
  });
});

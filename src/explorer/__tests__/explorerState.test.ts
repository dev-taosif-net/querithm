import { beforeEach, describe, expect, it } from "vitest";
import { restoreExpansion, savedExpansion, useExplorer } from "../explorerState";

beforeEach(() => useExplorer.setState({ expanded: new Set() }));

describe("saved expansion", () => {
  it("keeps the most recently opened 2000 keys, not the oldest; reopening a key makes it recent", () => {
    const keys = Array.from({ length: 2100 }, (_, n) => `s:p${n}`);
    useExplorer.setState({ expanded: new Set(keys) });
    useExplorer.getState().toggle("s:p0", false);
    useExplorer.getState().toggle("s:p0", true);
    const saved = savedExpansion();
    expect(saved).toHaveLength(2000);
    expect(saved.at(-1)).toBe("s:p0");
    expect(saved).not.toContain("s:p1");
    expect(saved).toContain("s:p2099");
  });

  it("restores only well-formed keys, at most 2000, the most recent ones", () => {
    restoreExpansion(["s:a", 7, "bogus", "db:a:AppDb", "x".repeat(3000)]);
    expect([...useExplorer.getState().expanded]).toEqual(["s:a", "db:a:AppDb"]);
    useExplorer.setState({ expanded: new Set() });
    restoreExpansion(Array.from({ length: 2005 }, (_, n) => `s:q${n}`));
    expect(useExplorer.getState().expanded.size).toBe(2000);
    expect(useExplorer.getState().expanded.has("s:q2004")).toBe(true);
    restoreExpansion("not a list");
    expect(useExplorer.getState().expanded.size).toBe(2000);
  });
});

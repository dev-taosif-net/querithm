import { describe, expect, it } from "vitest";
import fixture from "./fixtures/group-names.json";
import { cleanGroupName, compareGroupNames, findGroup, groupKey } from "../groupNames";

// The same cases the sidecar's GroupNamesParityTests read: the editor's hint never disagrees with what is stored.
describe("group names match the sidecar's rule (shared fixture)", () => {
  for (const c of fixture as { raw: string; name: string | null; key: string | null; error: boolean }[]) {
    it(`${JSON.stringify(c.raw.length > 40 ? `${c.raw.slice(0, 12)}…(${c.raw.length})` : c.raw)}`, () => {
      const r = cleanGroupName(c.raw);
      expect(!!r.error).toBe(c.error);
      expect(r.name).toBe(c.name);
      if (r.name) expect(groupKey(r.name)).toBe(c.key);
    });
  }
});

describe("group name helpers", () => {
  const groups = [{ name: "Payments", members: 3 }, { name: "Core Banking", members: 0 }];

  it("findGroup matches ignoring case, spacing and Unicode form, and never matches an empty or refused name", () => {
    expect(findGroup(groups, "  payments ")?.name).toBe("Payments");
    expect(findGroup(groups, "core   BANKING")?.name).toBe("Core Banking");
    expect(findGroup(groups, "Billing")).toBeUndefined();
    expect(findGroup(groups, "")).toBeUndefined();
    expect(findGroup(groups, "Pay​ments")).toBeUndefined();
  });

  it("a lone surrogate is refused, never normalized", () => {
    expect(cleanGroupName("\uD800x").error).toBeTruthy();
    expect(cleanGroupName("a\uDC00").error).toBeTruthy();
    expect(cleanGroupName("x".repeat(2000)).error).toBeTruthy();
  });

  it("sorts ignoring case, numbers by value", () => {
    expect(["Team 10", "team 2", "Alpha", "beta"].sort(compareGroupNames)).toEqual(["Alpha", "beta", "team 2", "Team 10"]);
  });
});

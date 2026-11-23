import { describe, expect, it } from "vitest";
import { removeFromOrder, renameInOrder, reorderList, sortGroupNames } from "../order";

describe("custom group and profile ordering", () => {
  it("sortGroupNames puts ordered groups first (case-insensitive), then unlisted groups A–Z", () => {
    expect(sortGroupNames(["Alpha", "Team 10", "team 2", "Billing"], ["team 10", "Billing"])).toEqual(["Team 10", "Billing", "Alpha", "team 2"]);
  });

  it("reorderList moves an item before or after a target while preserving disconnected/hidden items", () => {
    // A > B > C, then B is disconnected (visible = [A, C]): dragging C before A keeps B right after A.
    expect(reorderList(["a", "b", "c"], ["a", "c"], "c", "a", "before")).toEqual(["c", "a", "b"]);
    expect(reorderList(["a", "b", "c"], ["a", "c"], "a", "c", "after")).toEqual(["b", "c", "a"]);
    // First drag when order is empty snapshots all visible siblings.
    expect(reorderList([], ["a", "b", "c"], "c", "a", "before")).toEqual(["c", "a", "b"]);
  });

  it("renameInOrder updates a group's name in place and removeFromOrder drops it", () => {
    expect(renameInOrder(["Payments", "Analytics"], "payments", "Billing")).toEqual(["Billing", "Analytics"]);
    expect(removeFromOrder(["Billing", "Analytics"], "billing", true)).toEqual(["Analytics"]);
  });
});

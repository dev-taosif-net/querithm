import { describe, expect, it } from "vitest";
import { rowCountTitle } from "../TableNodeCard";

describe("row count tooltip", () => {
  it("an exact count says so; an estimate names its source and, when the engine says, when it was gathered (Oracle LAST_ANALYZED)", () => {
    expect(rowCountTitle({ estimated: false })).toBe("Row count");
    expect(rowCountTitle({ estimated: true })).toBe("Estimated row count (from the engine's statistics)");
    const at = Date.UTC(2026, 10, 10, 9, 30);
    expect(rowCountTitle({ estimated: true, analyzedAt: at })).toBe(`Estimated row count (from the engine's statistics, gathered ${new Date(at).toLocaleString()})`);
  });
});

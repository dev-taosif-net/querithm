import { describe, it, expect, vi } from "vitest";
import { CompactSelection } from "@glideapps/glide-data-grid";
import { selectionStats } from "../selectionStats";
describe("selection statistics", () => {
  it("bounds work for a million-row selection and reports sampling", () => {
    const read = vi.fn(() => "2");
    const result = selectionStats({ columns: CompactSelection.fromSingleSelection(0), rows: CompactSelection.empty() }, 1_000_000, ["number"], read);
    expect(read).toHaveBeenCalledTimes(50_000);
    expect(result).toMatchObject({ count: 1_000_000, sampled: true, sum: 100_000 });
  });
  it("does not interpret text identifiers as numeric values", () => {
    const read = vi.fn(() => "00123");
    const result = selectionStats({ columns: CompactSelection.fromSingleSelection(0), rows: CompactSelection.empty() }, 2, ["text"], read);
    expect(result?.numericCount).toBe(0);
    expect(result?.distinct).toBe(1);
  });
  it("counts NULLs and distinct values: a NULL is neither the text 'NULL' nor an empty string", () => {
    const cells = [null, "NULL", "", null, "x"];
    const result = selectionStats({ columns: CompactSelection.fromSingleSelection(0), rows: CompactSelection.empty() }, cells.length, ["text"], (r) => cells[r]);
    expect(result).toMatchObject({ count: 5, nullCount: 2, distinct: 4, numericCount: 0 });
  });
});

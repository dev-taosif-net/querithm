import { describe, expect, it } from "vitest";
import { clampLeft } from "../tipPosition";

describe("header tooltip position", () => {
  it("keeps the anchor when the tooltip fits", () => {
    expect(clampLeft(300, 400, 1440)).toBe(300);
  });

  it("shifts left so a tooltip on the last column is not cut off", () => {
    // Regression: the last header's tooltip ran past the right edge and lost half its text.
    expect(clampLeft(1300, 400, 1440)).toBe(1440 - 400 - 4);
  });

  it("never goes past the left edge when the tooltip is wider than the viewport", () => {
    expect(clampLeft(100, 2000, 1440)).toBe(4);
  });
});

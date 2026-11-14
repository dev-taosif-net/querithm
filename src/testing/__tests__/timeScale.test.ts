// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { getConfig } from "@testing-library/react";
import { timeScale } from "../timeScale.setup";

describe("test time scale", () => {
  it("stretches Testing Library's async waits by QUERITHM_TEST_TIME_SCALE", () => {
    expect(getConfig().asyncUtilTimeout).toBe(1000 * timeScale);
  });
});

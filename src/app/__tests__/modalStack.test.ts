import { describe, expect, it } from "vitest";
import { isTopModal, pushModal } from "../modalStack";

describe("modal stack", () => {
  it("only the topmost modal handles keys; closing it hands them back", () => {
    const connect = {}, confirm = {};
    const popConnect = pushModal(connect);
    expect(isTopModal(connect)).toBe(true);
    const popConfirm = pushModal(confirm);
    expect(isTopModal(connect)).toBe(false);
    expect(isTopModal(confirm)).toBe(true);
    popConfirm();
    expect(isTopModal(connect)).toBe(true);
    popConnect();
    expect(isTopModal(connect)).toBe(false);
  });
  it("a lower modal closing does not disturb the top one", () => {
    const a = {}, b = {};
    const popA = pushModal(a);
    const popB = pushModal(b);
    popA();
    expect(isTopModal(b)).toBe(true);
    popB();
  });
});

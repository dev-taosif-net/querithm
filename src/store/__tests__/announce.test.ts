import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ANNOUNCE_GAP_MS, ANNOUNCE_MAX, announce, resetAnnouncer, useAnnounce } from "../announce";

beforeEach(() => {
  vi.useFakeTimers();
  resetAnnouncer();
});
afterEach(() => vi.useRealTimers());

describe("announcer", () => {
  it("says a message at once, ending it with a full stop", () => {
    announce("Closed 3 tabs");
    expect(useAnnounce.getState()).toMatchObject({ text: "Closed 3 tabs.", n: 1 });
  });
  it("reads the same text again as a new message", () => {
    announce("Closed 1 tab.");
    vi.advanceTimersByTime(ANNOUNCE_GAP_MS);
    announce("Closed 1 tab.");
    expect(useAnnounce.getState()).toMatchObject({ text: "Closed 1 tab.", n: 2 });
  });
  it("speaks at most once a second and joins what arrived in between", () => {
    announce("Query finished in Orders");
    announce("Server QA disconnected");
    announce("Closed 2 tabs!");
    expect(useAnnounce.getState().text).toBe("Query finished in Orders.");
    vi.advanceTimersByTime(ANNOUNCE_GAP_MS - 1);
    expect(useAnnounce.getState().n).toBe(1);
    vi.advanceTimersByTime(1);
    expect(useAnnounce.getState()).toMatchObject({ text: "Server QA disconnected. Closed 2 tabs!", n: 2 });
  });
  it("does not say a message again that is already waiting or was read under a second ago (M20)", () => {
    announce("Saved");
    announce("Saved.");
    announce("Copied");
    announce("Copied");
    expect(useAnnounce.getState()).toMatchObject({ text: "Saved.", n: 1 });
    vi.advanceTimersByTime(ANNOUNCE_GAP_MS);
    expect(useAnnounce.getState()).toMatchObject({ text: "Copied.", n: 2 });
  });
  it("a flood of different messages reads the first few and how many more, not all of them (W9)", () => {
    announce("First");
    for (let i = 1; i <= 40; i++) announce(`Toast number ${i}`);
    announce("Toast number 40"); // the same one again is still one
    vi.advanceTimersByTime(ANNOUNCE_GAP_MS);
    const text = useAnnounce.getState().text;
    expect(text).toBe(`${Array.from({ length: ANNOUNCE_MAX }, (_, i) => `Toast number ${i + 1}.`).join(" ")} … and ${40 - ANNOUNCE_MAX} more.`);
    // The next burst starts afresh.
    vi.advanceTimersByTime(ANNOUNCE_GAP_MS);
    announce("Alone");
    expect(useAnnounce.getState().text).toBe("Alone.");
  });
  it("ignores empty messages", () => {
    announce("   ");
    expect(useAnnounce.getState().n).toBe(0);
  });
});

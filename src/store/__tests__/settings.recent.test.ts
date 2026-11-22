import { describe, expect, it, vi } from "vitest";

vi.mock("../../ipc/client", () => ({ rpc: vi.fn(async () => undefined), onNotify: () => () => {}, shell: {}, RpcError: class extends Error {} }));

import { defaultSettings, RECENT_PROFILES_MAX, sanitizeSettings, touchRecent } from "../settings";

// The Connect dialog's memory (§16 Connect dialog): profile ids by last connect, and the muted "name says prod" hints.
describe("settings: recent profiles and muted hints", () => {
  it("start empty", () => {
    expect(defaultSettings.recentProfiles).toEqual([]);
    expect(defaultSettings.prodHintMuted).toEqual([]);
  });

  it("touchRecent moves a profile to the front, once, and keeps at most RECENT_PROFILES_MAX", () => {
    expect(touchRecent(["a", "b", "c"], "c")).toEqual(["c", "a", "b"]);
    expect(touchRecent([], "a")).toEqual(["a"]);
    const many = Array.from({ length: RECENT_PROFILES_MAX }, (_, i) => `p${i}`);
    const next = touchRecent(many, "new");
    expect(next).toHaveLength(RECENT_PROFILES_MAX);
    expect(next[0]).toBe("new");
    expect(next).not.toContain(`p${RECENT_PROFILES_MAX - 1}`);
  });

  it("saved values are untrusted: wrong types are dropped, duplicates removed and the list capped", () => {
    expect(sanitizeSettings({ recentProfiles: "a", prodHintMuted: [1] }).values).toEqual({});
    const long = [...Array.from({ length: 50 }, (_, i) => `p${i}`), "p0"];
    const { values } = sanitizeSettings({ recentProfiles: long, prodHintMuted: ["x"] });
    expect(values.recentProfiles).toHaveLength(RECENT_PROFILES_MAX);
    expect(new Set(values.recentProfiles).size).toBe(RECENT_PROFILES_MAX);
    expect(values.prodHintMuted).toEqual(["x"]);
  });
});

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
    // (syntaxColors is added to any object saved before it existed: the one-time migration, settings.themes.test.)
    const { syntaxColors: _migrated, ...rest } = sanitizeSettings({ recentProfiles: "a", prodHintMuted: [1] }).values;
    expect(rest).toEqual({});
    const long = [...Array.from({ length: 50 }, (_, i) => `p${i}`), "p0"];
    const { values } = sanitizeSettings({ recentProfiles: long, prodHintMuted: ["x"] });
    expect(values.recentProfiles).toHaveLength(RECENT_PROFILES_MAX);
    expect(new Set(values.recentProfiles).size).toBe(RECENT_PROFILES_MAX);
    expect(values.prodHintMuted).toEqual(["x"]);
  });
});

describe("settings: keys that name Object.prototype", () => {
  it("are neither settings nor kept keys, and change no prototype", () => {
    // JSON.parse keeps "__proto__" as an ordinary own key, as store.db would hand it over.
    const raw = JSON.parse('{"__proto__": {"polluted": 1}, "constructor": 5, "prototype": 1, "toString": 7, "hasOwnProperty": 3, "minimap": false, "futureThing": 1}');
    const { values, extra } = sanitizeSettings(raw);
    expect(Object.getPrototypeOf(values)).toBe(Object.prototype);
    expect(Object.getPrototypeOf(extra)).toBe(Object.prototype);
    expect(({} as Record<string, unknown>).polluted).toBeUndefined();
    // Inherited names are no settings: they are kept as a newer version's keys (unsafe ones dropped), never cleaned.
    expect(Object.keys(values).sort()).toEqual(["minimap", "syntaxColors"]);
    expect(values.minimap).toBe(false);
    expect(extra).toEqual({ toString: 7, hasOwnProperty: 3, futureThing: 1 });
  });
});

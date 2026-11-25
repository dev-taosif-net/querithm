// Theme settings across versions and windows (plans/app-themes §3): the one-time move of a saved Standard to Default,
// the mirror older builds read, a choice made in an older build adopted on return, and saves that send only what this
// session changed, merged by the sidecar (another window's or a newer version's keys are kept).
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { rpc } = vi.hoisted(() => ({ rpc: vi.fn() }));
vi.mock("../../ipc/client", () => ({ rpc, onNotify: vi.fn(() => () => {}), shell: {}, RpcError: class extends Error {} }));

import { defaultSettings, legacySyntaxMirror, migrateSyntaxTheme, takeSyntaxMigrationNotice, useSettings, useSettingsStatus } from "../settings";
import { useUi } from "../ui";
import { fakeSettingsDb, patchesOf } from "./fakeSettingsDb";

/** A fake store.db holding ui.settings, answering settings.get / settings.set / settings.patch as the sidecar does. */
function fakeStore(initial: Record<string, unknown> | null) {
  const db = fakeSettingsDb(initial);
  rpc.mockImplementation(db.handle);
  return db;
}
const sets = () => patchesOf(rpc.mock.calls).length;

describe("theme settings: migration, mirror and adoption", () => {
  beforeEach(() => {
    rpc.mockReset();
    useSettings.setState({ ...defaultSettings, loaded: false });
    useSettingsStatus.setState({ save: "saved", error: undefined, loadFailed: false });
    useUi.setState({ toasts: [] });
    takeSyntaxMigrationNotice(); // clear a flag another test left
    vi.useFakeTimers();
  });
  afterEach(() => vi.useRealTimers());

  it("the mirror and the migration rule", () => {
    expect(legacySyntaxMirror("default")).toBe("standard");
    expect(legacySyntaxMirror("neon")).toBe("neon");
    expect(legacySyntaxMirror("a-theme-from-2027")).toBe("standard");
    expect(migrateSyntaxTheme("standard")).toBe("default");
    expect(migrateSyntaxTheme(undefined)).toBe("default");
    expect(migrateSyntaxTheme("classic")).toBe("default");
    expect(migrateSyntaxTheme("ocean")).toBe("ocean");
  });

  it("a saved Standard moves to Default once: written at load, the notice is due once, and the next load changes nothing", async () => {
    const db = fakeStore({ theme: "dark", syntaxTheme: "standard", wordWrap: true });
    await useSettings.getState().load();
    await vi.advanceTimersByTimeAsync(0);
    expect(useSettings.getState().syntaxColors).toBe("default");
    expect(db.read()).toMatchObject({ syntaxColors: "default", syntaxTheme: "standard", wordWrap: true });
    expect(takeSyntaxMigrationNotice()).toBe(true);
    expect(takeSyntaxMigrationNotice()).toBe(false);
    rpc.mockClear();
    await useSettings.getState().load();
    await vi.advanceTimersByTimeAsync(500);
    expect(sets()).toBe(0);
    expect(takeSyntaxMigrationNotice()).toBe(false);
  });

  it("a saved non-Standard theme is kept with no notice; a fresh install writes nothing and shows no notice", async () => {
    let db = fakeStore({ syntaxTheme: "forest" });
    await useSettings.getState().load();
    await vi.advanceTimersByTimeAsync(0);
    expect(useSettings.getState().syntaxColors).toBe("forest");
    expect(db.read()!.syntaxColors).toBe("forest");
    expect(takeSyntaxMigrationNotice()).toBe(false);
    rpc.mockReset();
    db = fakeStore(null);
    await useSettings.getState().load();
    await vi.advanceTimersByTimeAsync(500);
    expect(useSettings.getState().syntaxColors).toBe("default");
    expect(sets()).toBe(0);
    expect(takeSyntaxMigrationNotice()).toBe(false);
  });

  it("a fresh install's first save names its syntax colors: the next start is not read as an old build's settings (no notice)", async () => {
    const db = fakeStore(null);
    await useSettings.getState().load();
    useSettings.getState().set({ editorFontSize: 15 }); // any first change: a connect touches recentProfiles too
    await vi.advanceTimersByTimeAsync(400);
    expect(db.read()).toMatchObject({ editorFontSize: 15, syntaxColors: "default", syntaxTheme: "standard" });
    // The next start.
    useSettings.setState({ ...defaultSettings, loaded: false });
    rpc.mockClear();
    await useSettings.getState().load();
    await vi.advanceTimersByTimeAsync(500);
    expect(takeSyntaxMigrationNotice()).toBe(false);
    expect(sets()).toBe(0);
    expect(useSettings.getState()).toMatchObject({ editorFontSize: 15, syntaxColors: "default" });
  });

  it("after a failed load, a save that recovers an old object leaves its migration (and its notice) to the next load", async () => {
    // Validated for a review: the recovery does not write the move to Default, so the next load does it and shows the
    // notice together with it; writing it here would show the new colors with no notice, ever.
    const db = fakeStore({ syntaxTheme: "standard" });
    rpc.mockImplementationOnce(async () => { throw new Error("store busy"); });
    await useSettings.getState().load();
    expect(useSettingsStatus.getState().loadFailed).toBe(true);
    useSettings.getState().set({ editorFontSize: 15 });
    await vi.advanceTimersByTimeAsync(400);
    expect(useSettings.getState().syntaxColors).toBe("default");
    expect(db.read()).toEqual({ syntaxTheme: "standard", editorFontSize: 15 });
    useSettings.setState({ ...defaultSettings, loaded: false });
    await useSettings.getState().load();
    await vi.advanceTimersByTimeAsync(0);
    expect(db.read()).toMatchObject({ syntaxColors: "default", editorFontSize: 15 });
    expect(takeSyntaxMigrationNotice()).toBe(true);
  });

  it("Standard chosen after the upgrade is never turned back into Default", async () => {
    const db = fakeStore({ syntaxColors: "default", syntaxTheme: "standard" });
    await useSettings.getState().load();
    useSettings.getState().set({ syntaxColors: "standard" });
    await vi.advanceTimersByTimeAsync(400);
    expect(db.read()).toMatchObject({ syntaxColors: "standard", syntaxTheme: "standard" });
    await useSettings.getState().load();
    expect(useSettings.getState().syntaxColors).toBe("standard");
  });

  it("a choice made in an older build (it writes only syntaxTheme) is adopted on return; its fallback Standard never flips a newer choice", async () => {
    let db = fakeStore({ syntaxColors: "default", syntaxTheme: "ocean" }); // an older build changed it
    await useSettings.getState().load();
    await vi.advanceTimersByTimeAsync(0);
    expect(useSettings.getState().syntaxColors).toBe("ocean");
    expect(db.read()!.syntaxColors).toBe("ocean");
    rpc.mockReset();
    db = fakeStore({ syntaxColors: "neon", syntaxTheme: "standard" }); // an older build without neon fell back to Standard
    await useSettings.getState().load();
    expect(useSettings.getState().syntaxColors).toBe("neon");
  });
});

describe("saves send only this session's changes, merged by the sidecar", () => {
  beforeEach(() => {
    rpc.mockReset();
    useSettings.setState({ ...defaultSettings, loaded: false });
    useSettingsStatus.setState({ save: "saved", error: undefined, loadFailed: false });
    vi.useFakeTimers();
  });
  afterEach(() => vi.useRealTimers());

  it("another window's change to another key survives this window's save (V11)", async () => {
    const db = fakeStore({ syntaxColors: "default", theme: "dark", darkTheme: "dark", editorFontSize: 13 });
    await useSettings.getState().load();
    // Another window picks Cortado and saves.
    db.write({ ...db.read(), darkTheme: "cortado" });
    // This window changes the font size only.
    useSettings.getState().set({ editorFontSize: 15 });
    await vi.advanceTimersByTimeAsync(400);
    expect(db.read()).toMatchObject({ darkTheme: "cortado", editorFontSize: 15 });
  });

  it("another window's save that lands while this window's save is on its way is kept too (audit 2026-11-25)", async () => {
    // Before, a save read ui.settings in one call and wrote it in the next; another window's write between the two was
    // put back to what the read saw. Now the save is one patch the sidecar merges under its write lock: nothing in
    // this window holds a copy of what is stored.
    const db = fakeStore({ syntaxColors: "default", theme: "dark", darkTheme: "dark", editorFontSize: 13 });
    await useSettings.getState().load();
    const base = rpc.getMockImplementation()!;
    rpc.mockImplementation(async (m: string, a: never) => {
      if (m === "settings.patch") db.write({ ...db.read(), darkTheme: "cortado" }); // the other window's save lands first
      return base(m, a);
    });
    useSettings.getState().set({ editorFontSize: 15 });
    await vi.advanceTimersByTimeAsync(400);
    expect(rpc.mock.calls.filter(([m]) => m === "settings.get")).toHaveLength(1); // the load's only
    expect(patchesOf(rpc.mock.calls)).toEqual([expect.objectContaining({ set: { editorFontSize: "15" } })]);
    expect(db.read()).toMatchObject({ darkTheme: "cortado", editorFontSize: 15 });
  });

  it("an App Theme id this build does not know (a newer build's) is kept on disk while showing the fallback (V12)", async () => {
    const db = fakeStore({ syntaxColors: "default", theme: "dark", darkTheme: "aurora-2027" });
    await useSettings.getState().load();
    expect(useSettings.getState().darkTheme).toBe("dark");
    useSettings.getState().set({ wordWrap: true });
    await vi.advanceTimersByTimeAsync(400);
    expect(db.read()).toMatchObject({ darkTheme: "aurora-2027", wordWrap: true });
  });

  it("a pick of the wrong mode is refused (a light theme as the dark pick)", () => {
    useSettings.getState().set({ darkTheme: "vellum", lightTheme: "cortado" });
    expect(useSettings.getState()).toMatchObject({ darkTheme: "dark", lightTheme: "light" });
    useSettings.getState().set({ darkTheme: "fathom", lightTheme: "floe" });
    expect(useSettings.getState()).toMatchObject({ darkTheme: "fathom", lightTheme: "floe" });
  });

  it("a set() during a save keeps its key for the next save", async () => {
    const db = fakeStore({ syntaxColors: "default" });
    await useSettings.getState().load();
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const base = rpc.getMockImplementation()!;
    rpc.mockImplementation(async (m: string, a: never) => {
      if (m === "settings.patch") await gate;
      return base(m, a);
    });
    useSettings.getState().set({ minimap: false });
    await vi.advanceTimersByTimeAsync(400); // the save of minimap is in flight
    useSettings.getState().set({ minimap: true });
    release();
    await vi.advanceTimersByTimeAsync(400);
    await vi.advanceTimersByTimeAsync(0);
    expect(db.read()!.minimap).toBe(true);
  });
});

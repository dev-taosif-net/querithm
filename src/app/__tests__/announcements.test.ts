import { beforeEach, describe, expect, it } from "vitest";
import { useTabs } from "../../store/tabs";
import { useProfiles } from "../../store/profiles";
import { useConnections } from "../../store/connections";
import { useSettings } from "../../store/settings";
import { resetAnnouncer, useAnnounce } from "../../store/announce";
import { announceQueryDone, announceTxLeftBehind, installAnnouncements } from "../announcements";

const tab = (id: string, o: any = {}) => ({ id, title: `${id}.sql`, txState: "none", busy: false, dirty: false, executions: [], bottomTab: "results", bottomOpen: true, bottomSize: 0.35, actualPlan: false, ...o });

beforeEach(() => {
  resetAnnouncer();
  useSettings.setState({ announceBackground: true });
  useProfiles.setState({ profiles: [{ id: "ms1", name: "Billing", engine: "mssql", environment: "prod" } as any, { id: "pg1", name: "QA Inventory", engine: "pg", environment: "test" } as any], groups: [] });
  useTabs.setState({ tabs: { a: tab("a", { profileId: "ms1" }) as any, b: tab("b", { profileId: "pg1" }) as any, c: tab("c", { profileId: "pg1" }) as any }, order: ["a", "b", "c"], activeId: "a" });
  useConnections.setState({ disconnected: {} });
});
const said = () => useAnnounce.getState().text;

describe("background announcements (D23)", () => {
  it.each([
    ["done", "Query finished in b (Test, 2.4 s)."],
    ["failed", "Query failed in b (Test, 2.4 s)."],
    ["cancelled", "Query was cancelled in b (Test, 2.4 s)."],
  ])("a %s run in a background tab is announced with its environment and time", (state, text) => {
    announceQueryDone("b", state, 2400);
    expect(said()).toBe(text);
  });

  it("the shown tab's own run, and everything when the setting is off, stay quiet", () => {
    announceQueryDone("a", "done", 10);
    expect(said()).toBe("");
    useSettings.setState({ announceBackground: false });
    announceQueryDone("b", "done", 10);
    announceTxLeftBehind("a");
    expect(said()).toBe("");
  });

  it("the shown tab's run says once what came back: polite on success, assertive when a statement failed (§12 bottom panel)", () => {
    announceQueryDone("a", "done", 10, { resultSets: 2, errors: [] });
    expect(said()).toBe("Run finished: 2 result sets.");
    expect(useAnnounce.getState().level).toBe("polite");
    resetAnnouncer();
    announceQueryDone("a", "failed", 10, { resultSets: 1, errors: [{ message: "Invalid object name 'dbo.Invoce'.\nmore", editorLine: 12 }, { message: "second" }] });
    expect(said()).toBe("Run failed: 1 result set, 2 errors. Line 12: Invalid object name 'dbo.Invoce'.");
    expect(useAnnounce.getState().level).toBe("assertive");
    // It is about the shown tab, so the background setting does not silence it.
    resetAnnouncer();
    useSettings.setState({ announceBackground: false });
    announceQueryDone("a", "done", 10, { resultSets: 0, errors: [] });
    expect(said()).toBe("Run finished: 0 result sets.");
  });

  it("a burst joined into one message takes its most urgent level", () => {
    announceQueryDone("a", "done", 10, { resultSets: 1, errors: [] });
    resetAnnouncer();
    announceTxLeftBehind("a"); // polite, nothing to say here (no transaction) …
    announceQueryDone("a", "failed", 10, { resultSets: 0, errors: [{ message: "boom" }] });
    expect(useAnnounce.getState().level).toBe("assertive");
  });

  it("an open transaction left behind by a tool tab is announced", () => {
    announceTxLeftBehind("a");
    expect(said()).toBe("");
    useTabs.getState().update("a", { txState: "open" });
    announceTxLeftBehind("a");
    expect(said()).toBe("Open transaction remains on a (Production).");
  });

  it("a server that drops is announced once with the tabs it affects", () => {
    const off = installAnnouncements();
    useConnections.setState({ disconnected: { pg1: true } });
    expect(said()).toBe("Server QA Inventory disconnected, 2 tabs affected.");
    const n = useAnnounce.getState().n;
    useConnections.setState({ disconnected: { pg1: true } }); // a new object, the same drop
    expect(useAnnounce.getState().n).toBe(n);
    off();
  });
});

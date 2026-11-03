import { describe, it, expect, beforeEach } from "vitest";
import { closedAgo, entryAt, MAX_CLOSED_CHARS, snapshotOf, useClosedTabs, type ClosedTabSnapshot } from "../closedTabs";
import type { BoundObject, TabState } from "../tabs";

const s = () => useClosedTabs.getState();

const snap = (title: string, sql = ""): ClosedTabSnapshot => ({ closedAt: Date.now(), title, sql });

describe("useClosedTabs store", () => {
  beforeEach(() => useClosedTabs.setState({ list: [] }));

  it("push prepends, newest first", () => {
    s().push(snap("a.sql"));
    s().push(snap("b.sql"));
    expect(s().list[0].title).toBe("b.sql");
    expect(s().list[1].title).toBe("a.sql");
  });

  it("caps by size, not by count: 8 MB of SQL, oldest dropped first (D28)", () => {
    for (let i = 0; i < 25; i++) s().push(snap(`q${i}.sql`));
    expect(s().list).toHaveLength(25);
    const big = (title: string) => ({ ...snap(title), sql: "x".repeat(3 * 1024 * 1024) });
    s().push(big("b1.sql"));
    s().push(big("b2.sql"));
    expect(s().list).toHaveLength(27);
    s().push(big("b3.sql")); // 9 MB: b1 and everything older go
    expect(s().list.map((x) => x.title)).toEqual(["b3.sql", "b2.sql"]);
    s().push({ ...snap("huge.sql"), sql: "x".repeat(MAX_CLOSED_CHARS + 1) });
    expect(s().list.map((x) => x.title)).toEqual(["huge.sql"]); // a single tab larger than the cap is still kept
  });

  it("a bulk close is one entry: entryAt finds its tabs; removeMany drops them together", () => {
    s().push(snap("solo.sql"));
    for (const t of ["a.sql", "b.sql", "c.sql"]) s().push({ ...snap(t), batch: "b1" });
    expect(entryAt(s().list, 1)).toEqual([0, 1, 2]);
    expect(entryAt(s().list, 3)).toEqual([3]);
    expect(entryAt(s().list, 9)).toEqual([]);
    s().removeMany(entryAt(s().list, 0));
    expect(s().list.map((x) => x.title)).toEqual(["solo.sql"]);
  });

  it("remove drops by index", () => {
    s().push(snap("a.sql"));
    s().push(snap("b.sql"));
    s().push(snap("c.sql"));
    s().remove(1); // remove "b.sql" (index 1 after push order: c, b, a)
    const titles = s().list.map((x) => x.title);
    expect(titles).toEqual(["c.sql", "a.sql"]);
  });

  it("clear empties the list", () => {
    s().push(snap("a.sql"));
    s().push(snap("b.sql"));
    s().clear();
    expect(s().list).toHaveLength(0);
  });

  it("preserves all snapshot fields", () => {
    const full: ClosedTabSnapshot = {
      closedAt: 12345,
      title: "proc.sql",
      profileId: "p1",
      database: "MyDb",
      sql: "SELECT 1",
      filePath: "/tmp/proc.sql",
      readOnly: false,
    };
    s().push(full);
    expect(s().list[0]).toEqual(full);
  });
});

describe("snapshotOf", () => {
  const tab = (extra: Partial<TabState> = {}) => ({ id: "t1", title: "q.sql", txState: "none", busy: false, dirty: false, ...extra }) as TabState;
  const bound = { profileId: "pg1", database: "app", schema: "public", name: "f", kind: "func", definitionHash: "h" } as BoundObject;

  it("skips an empty scratch tab", () => {
    expect(snapshotOf(tab(), "  \n ")).toBeUndefined();
  });

  it("keeps an empty tab backed by a file or a server object", () => {
    expect(snapshotOf(tab({ filePath: "/w/q.sql" }), "")?.filePath).toBe("/w/q.sql");
    expect(snapshotOf(tab({ boundObject: bound }), "")?.boundObject).toEqual(bound);
  });

  it.each([
    ["SqlServer", "ms1", "Sales", "SELECT TOP 1 * FROM [dbo].[t]"],
    ["Postgres", "pg1", "app", 'SELECT * FROM "public"."t" LIMIT 1'],
  ])("copies the text, binding and flags of a %s tab", (_engine, profileId, database, sql) => {
    const s = snapshotOf(tab({ profileId, database, readOnly: true, dirty: true, sessionId: "s1", txState: "open" }), sql, 42);
    expect(s).toEqual({ closedAt: 42, title: "q.sql", profileId, database, sql, filePath: undefined, boundObject: undefined, readOnly: true, dirty: true });
  });
});

describe("closedAgo", () => {
  const now = 10 * 24 * 3_600_000;
  it.each([
    [now - 30_000, "just now"],
    [now - 5 * 60_000, "5 min ago"],
    [now - 3 * 3_600_000, "3 h ago"],
    [now - 2 * 24 * 3_600_000, "2 d ago"],
  ])("%d → %s", (at, text) => expect(closedAgo(at, now)).toBe(text));
});

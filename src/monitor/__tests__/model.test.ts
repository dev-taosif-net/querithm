import { describe, expect, it, vi } from "vitest";
import type { ActivityRow, MonitorSnapshot } from "../../ipc/contract";
import { MonitorModel, COLUMNS, changedCells, type Column } from "../model";
import { TextCache, MAX_TEXTS } from "../textCache";
import { buildTree, MAX_CHILDREN, MAX_TREE_ITEMS } from "../treeModel";
import { killAvailability, QUERITHM_CONNECTION } from "../killRules";
import { keyOf, escapeBlock, escapeForDisplay, waitColorVar } from "../format";

const row = (id: number, o: Partial<ActivityRow> = {}): ActivityRow => ({
  key: { id, instance: `i${id}` }, status: "running", blockedBy: [], isUserSession: true, rollingBack: false, ...o,
});

const snap = (seq: number, sessions: ActivityRow[] | null, o: Partial<MonitorSnapshot> = {}): MonitorSnapshot => ({
  monitorId: "m", at: seq * 2000, seq, sessions, status: [], lagMs: 0, connectionCount: 1, throttled: false, paused: false, connection: "connected", ...o,
});

const noFetch = vi.fn(async () => ({ texts: {}, missing: [] as string[] }));

describe("snapshot model", () => {
  it("M6: a snapshot with an older or repeated Seq is ignored", () => {
    const m = new MonitorModel("m", noFetch);
    expect(m.apply(snap(5, [row(1)]))).toBe(true);
    expect(m.apply(snap(4, [row(1), row(2)]))).toBe(false);
    expect(m.apply(snap(5, [row(3)]))).toBe(false);
    expect([...m.byKey.values()].map((r) => r.key.id)).toEqual([1]);
  });

  it("M6: a text whose notification was lost is fetched when a row names it", async () => {
    const fetch = vi.fn(async (ids: string[]) => ({ texts: Object.fromEntries(ids.map((i) => [i, `text ${i}`])), missing: [] as string[] }));
    const m = new MonitorModel("m", fetch);
    m.apply(snap(1, [row(1, { textId: "a".repeat(32) })])); // the snapshot carrying Texts was lost
    await vi.waitFor(() => expect(m.texts.get("a".repeat(32))).toBe(`text ${"a".repeat(32)}`));
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("U2: the selection follows the session key across ticks and is cleared when the session ends", () => {
    const m = new MonitorModel("m", noFetch);
    m.apply(snap(1, [row(1), row(2), row(3)]));
    m.select(keyOf(row(2).key));
    m.apply(snap(2, [row(3), row(2), row(1)]));
    expect(m.selected?.key.id).toBe(2);
    m.apply(snap(3, [row(1), row(3), row(2, { key: { id: 2, instance: "reused" } })])); // spid reused: a different session
    expect(m.selected).toBeUndefined();
    expect(m.selectedKey).toBeNull();
  });

  it("U5: sorting is stable across ticks (ties broken by session key) and filters persist", () => {
    const m = new MonitorModel("m", noFetch);
    m.setSort("status");
    const tick = (seq: number) => m.apply(snap(seq, [row(5), row(3), row(9, { status: "sleeping", isUserSession: true }), row(1), row(7, { isUserSession: false })]));
    tick(1);
    const first = m.rows.map((r) => r.key.id);
    tick(2);
    expect(m.rows.map((r) => r.key.id)).toEqual(first);
    expect(first).toEqual([1, 3, 5, 9]); // system session 7 hidden by default
    m.setFilters({ hideIdle: true });
    tick(3);
    expect(m.rows.map((r) => r.key.id)).toEqual([1, 3, 5]);
  });

  it("S10: system and background sessions are hidden by \"hide system\", shown when it is off, and never killable", () => {
    const m = new MonitorModel("m", noFetch);
    const sys = row(7, { isUserSession: false, backendType: "checkpointer" });
    m.apply(snap(1, [row(1), sys]));
    expect(m.rows.map((r) => r.key.id)).toEqual([1]);
    m.setFilters({ hideSystem: false });
    m.apply(snap(2, [row(1), sys]));
    expect(m.rows.map((r) => r.key.id)).toEqual([1, 7]);
    const caps = { serverVersion: "x", panes: [], allSessionsVisible: true, canTerminate: true, canCancelRequest: true, livePlans: true };
    expect(killAvailability(sys, caps, "terminateSession", true).enabled).toBe(false);
    expect(killAvailability(sys, caps, "cancelRequest", true).enabled).toBe(false);
  });

  it("U1: only cells whose display changed are damaged", () => {
    const m = new MonitorModel("m", noFetch);
    m.apply(snap(1, [row(1, { cpuMs: 10 }), row(2, { cpuMs: 20 })]));
    const before = m.rows;
    m.apply(snap(2, [row(1, { cpuMs: 10 }), row(2, { cpuMs: 25 })]));
    const cols = COLUMNS.filter((c) => ["spid", "cpu", "status"].includes(c.id));
    expect(changedCells(before, m.rows, cols, m)).toEqual([{ cell: [2, 1] }]); // columns spid, status, cpu: only row 2 cpu
  });

  it("U1: a tick formats only its own rows; the previous rows are compared as they were shown (metric 28ui)", () => {
    const m = new MonitorModel("m", noFetch);
    let calls = 0;
    const cpu: Column = { id: "cpu", title: "CPU", width: 80, value: (r) => r.cpuMs, display: (r) => { calls++; return String(r.cpuMs); } };
    const cols = [cpu]; // one list for the grid's lifetime, as SessionsPane memoises it
    m.apply(snap(1, [row(1, { cpuMs: 10 }), row(2, { cpuMs: 20 })]));
    let before = m.rows;
    m.apply(snap(2, [row(1, { cpuMs: 10 }), row(2, { cpuMs: 25 })]));
    expect(changedCells(before, m.rows, cols, m)).toEqual([{ cell: [0, 1] }]);
    expect(calls).toBe(4); // nothing shown yet: both sides once
    calls = 0;
    before = m.rows;
    m.apply(snap(3, [row(1, { cpuMs: 11 }), row(2, { cpuMs: 25 })]));
    expect(changedCells(before, m.rows, cols, m)).toEqual([{ cell: [0, 0] }]);
    expect(calls).toBe(2); // only the new rows
    // Same row objects in a new order: every moved row is damaged.
    before = m.rows;
    m.setSort("cpu");
    m.setSort("cpu"); // descending: 25 before 11
    expect(m.rows.map((r) => r.key.id)).toEqual([2, 1]);
    expect(changedCells(before, m.rows, cols, m)).toEqual([{ cell: [0, 0] }, { cell: [0, 1] }]);
  });

  it("S4: text with nothing to escape comes back unchanged, astral characters included; escapes after clean text are found", () => {
    const clean = `SELECT 1 /* ünïcode 😀 ${"x".repeat(400)} */`;
    expect(escapeForDisplay(clean)).toBe(clean);
    const bs = String.fromCharCode(92); // the expected escapes begin with a literal backslash
    expect(escapeForDisplay("abc 😀" + String.fromCharCode(0x202e))).toBe("abc 😀" + bs + "u202e");
    expect(escapeForDisplay("a😀" + String.fromCharCode(7))).toBe("a😀" + bs + "x07");
  });

  it("S4: zero-width and invisible characters, line/paragraph separators, BOM, soft hyphen and tag characters are escaped; other astral characters stay whole", () => {
    const bs = String.fromCharCode(92);
    const invisible = "a\u200Bb\u200Cc\u200Dd\u2060e\u2064f\uFEFFg\u00ADh\u2028i\u2029j";
    const escaped = `a${bs}u200bb${bs}u200cc${bs}u200dd${bs}u2060e${bs}u2064f${bs}ufeffg${bs}u00adh${bs}u2028i${bs}u2029j`;
    expect(escapeForDisplay(invisible)).toBe(escaped);
    expect(escapeBlock(invisible)).toBe(escaped);
    // Tag characters are astral (U+E0000–E007F): only a code-point scan sees them; an emoji around them stays one pair.
    const tags = "x\u{E0041}\u{E007F}😀y";
    expect(escapeForDisplay(tags)).toBe(`x${bs}u{e0041}${bs}u{e007f}😀y`);
    expect(escapeBlock(`${tags}\nz`)).toBe(`x${bs}u{e0041}${bs}u{e007f}😀y\nz`);
    expect(escapeBlock("😀 \u{1F600}\n")).toBe("😀 \u{1F600}\n"); // nothing to escape: the same string back
  });

  it("S3, S4: nulls show as a dash, control characters and bidi overrides are escaped", () => {
    expect(escapeForDisplay("a‮b\u0007c\nd")).toBe("a\\u202eb\\x07c d");
    const m = new MonitorModel("m", noFetch);
    m.apply(snap(1, [row(1, { login: null, program: "app‮txt.exe" })]));
    const login = COLUMNS.find((c) => c.id === "login")!;
    const program = COLUMNS.find((c) => c.id === "program")!;
    expect(login.display(m.rows[0], m)).toBe("—");
    expect(program.display(m.rows[0], m)).toBe("app\\u202etxt.exe");
  });

  it("counts: running, blocked, idle with an open transaction, head blockers", () => {
    const m = new MonitorModel("m", noFetch);
    m.apply(snap(1, [row(1, { status: "sleeping", openTx: 1 }), row(2, { blockedBy: [1] }), row(3)], {
      blocking: { headBlockers: [1], cycles: [], placeholders: [], blockedCount: 1 },
    }));
    expect(m.counts()).toEqual({ total: 3, running: 2, blocked: 1, sleepingWithTx: 1, headBlockers: 1 });
  });

  it("U6: wait colours are theme tokens defined for both themes", async () => {
    expect(waitColorVar(1_000)).toBe("var(--q-wait-short)");
    expect(waitColorVar(10_000)).toBe("var(--q-wait-mid)");
    expect(waitColorVar(60_000)).toBe("var(--q-wait-long)");
    const { readFileSync } = await import("node:fs");
    const css = readFileSync(new URL("../../theme/tokens.css", import.meta.url), "utf8");
    const dark = css.slice(css.indexOf(':root[data-theme="dark"]'), css.indexOf(':root[data-theme="light"]'));
    const light = css.slice(css.indexOf(':root[data-theme="light"]'));
    for (const t of ["--q-wait-short", "--q-wait-mid", "--q-wait-long"]) {
      expect(dark).toContain(`${t}:`);
      expect(light).toContain(`${t}:`);
    }
  });
});

describe("text cache", () => {
  it("M7: a text evicted from the UI cache is fetched again", async () => {
    const fetch = vi.fn(async (ids: string[]) => ({ texts: Object.fromEntries(ids.map((i) => [i, "t"])), missing: [] as string[] }));
    const c = new TextCache(fetch, () => {}, 3);
    for (const id of ["a", "b", "c", "d"]) c.put(id, "x");
    expect(c.has("a")).toBe(false);
    await c.ensure(["a"]);
    expect(fetch).toHaveBeenCalledWith(["a"]);
    expect(c.get("a")).toBe("t");
  });

  it("M8: sustained churn keeps the cache within 5,000 texts and 8 MB", () => {
    const c = new TextCache(noFetch);
    for (let tick = 0; tick < 1_800; tick++) for (let s = 0; s < 50; s++) c.put(`${tick}-${s}`, "x".repeat(400));
    expect(c.size).toBeLessThanOrEqual(MAX_TEXTS);
    const big = new TextCache(noFetch);
    for (let i = 0; i < 3_000; i++) big.put(`b${i}`, "é".repeat(2_000)); // 4 KB each
    expect(big.byteSize).toBeLessThanOrEqual(8 * 1024 * 1024);
  });

  it("M9: ids the sidecar no longer has are shown as unavailable and never asked again", async () => {
    const fetch = vi.fn(async (ids: string[]) => ({ texts: {}, missing: ids }));
    const c = new TextCache(fetch);
    await c.ensure(["gone"]);
    await c.ensure(["gone"]);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(c.isMissing("gone")).toBe(true);
    const m = new MonitorModel("m", fetch);
    m.apply(snap(1, [row(1, { textId: "gone" })]));
    await vi.waitFor(() => expect(m.texts.isMissing("gone")).toBe(true));
    expect(COLUMNS.find((x) => x.id === "text")!.display(m.rows[0], m)).toBe("text no longer available (session ended)");
  });

  it("asks in batches of at most 500 ids", async () => {
    const fetch = vi.fn(async (ids: string[]) => ({ texts: Object.fromEntries(ids.map((i) => [i, "t"])), missing: [] as string[] }));
    const c = new TextCache(fetch);
    await c.ensure(Array.from({ length: 1_200 }, (_, i) => `id${i}`));
    expect(fetch.mock.calls.map((a) => a[0].length)).toEqual([500, 500, 200]);
  });
});

describe("blocking tree", () => {
  it("U10: a node with 300 children shows 50 and +250 more; collapse state is kept by key across ticks", () => {
    const rows = [row(1, { status: "sleeping" }), ...Array.from({ length: 300 }, (_, i) => row(100 + i, { blockedBy: [1], waitMs: i }))];
    const info = { headBlockers: [1], cycles: [], placeholders: [], blockedCount: 300 };
    const items = buildTree(rows, info, new Set());
    expect(items.filter((i) => i.kind === "session" && i.depth === 1)).toHaveLength(MAX_CHILDREN);
    const more = items.find((i) => i.kind === "more");
    expect(more && "hidden" in more && more.hidden).toBe(250);
    const collapsed = new Set(["h1"]);
    const next = buildTree(rows.map((r) => ({ ...r })), info, collapsed);
    expect(next).toHaveLength(1);
    expect(buildTree(rows, info, new Set(), new Set(["h1"])).filter((i) => i.depth === 1)).toHaveLength(300);
  });

  it("B4, B6, B11: multi-blocker under both, cycles as a group, pseudo-blockers as placeholders", () => {
    const rows = [row(1), row(3), row(2, { blockedBy: [1, 3] }), row(5, { blockedBy: [6] }), row(6, { blockedBy: [5] }), row(8, { blockedByNote: "orphanedDtc" })];
    const info = {
      headBlockers: [1, 3, -2], cycles: [[5, 6]],
      placeholders: [{ id: -2, isSession: false, reason: "orphaned distributed transaction (-2): not a session" }], blockedCount: 4,
    };
    const items = buildTree(rows, info, new Set());
    expect(items.filter((i) => i.kind === "session" && i.row.key.id === 2)).toHaveLength(2);
    expect(items.some((i) => i.kind === "cycle")).toBe(true);
    const ph = items.find((i) => i.kind === "placeholder");
    expect(ph && ph.kind === "placeholder" && !ph.isSession && ph.reason).toContain("not a session");
    expect(items.some((i) => i.kind === "session" && i.row.key.id === 8 && i.depth === 1)).toBe(true);
  });

  // PostgreSQL reports soft blocks: on a hot row, waiter k is blocked by every waiter ahead of it (pg_blocking_pids), so
  // the graph is a DAG. Expanding every path made 2^(n-2) items (30 waiters: ~268 million) and froze the webview.
  const WAITERS = 30;
  const hotRow = (n: number) => [
    row(1, { status: "idle in transaction", openTx: 1 }),
    ...Array.from({ length: n }, (_, i) => row(i + 2, { status: "active", waitMs: 1_000 * (n - i), blockedBy: i === 0 ? [1] : Array.from({ length: i }, (_, j) => j + 2) })),
  ];

  it("B4 at scale: 30 hot-row waiters each blocked by every earlier one expand once each, the rest point to it", () => {
    const rows = hotRow(WAITERS);
    const info = { headBlockers: [1], cycles: [], placeholders: [], blockedCount: WAITERS };
    const t0 = performance.now();
    const items = buildTree(rows, info, new Set());
    expect(performance.now() - t0).toBeLessThan(1_000); // generous: the old per-path build never finished at 30 waiters
    // One item per (blocker, victim) edge under an expanded blocker: linear in the edges, never per path.
    expect(items.length).toBeLessThanOrEqual(2 + (WAITERS * (WAITERS + 1)) / 2);
    const sessions = items.filter((i) => i.kind === "session");
    for (let id = 1; id <= WAITERS + 1; id++) expect(sessions.filter((i) => i.row.key.id === id && i.expanded).length).toBeLessThanOrEqual(1);
    // Every waiter is shown expanded under its shallowest blocker; later occurrences say where its victims are.
    const marker = sessions.find((i) => i.row.key.id === 5 && !i.expanded && i.childCount > 0);
    expect(marker && marker.elsewhere).toBe(2);
    expect(Math.max(...items.map((i) => i.depth))).toBeLessThanOrEqual(3);
    // B4 still holds: the last waiter is listed under each of its blockers whose victims are shown.
    expect(sessions.filter((i) => i.row.key.id === WAITERS + 1)).toHaveLength(WAITERS - 1);
  });

  it("a forest larger than the cap is cut with a visible truncation item", () => {
    const rows: ActivityRow[] = [];
    for (let h = 0; h < 200; h++) {
      rows.push(row(10_000 + h, { status: "sleeping", openTx: 1 }));
      for (let v = 0; v < 50; v++) rows.push(row(h * 100 + v + 1, { blockedBy: [10_000 + h], waitMs: v }));
    }
    const info = { headBlockers: Array.from({ length: 200 }, (_, h) => 10_000 + h), cycles: [], placeholders: [], blockedCount: 10_000 };
    const items = buildTree(rows, info, new Set());
    expect(items.length).toBe(MAX_TREE_ITEMS + 1);
    const last = items.at(-1)!;
    expect(last.kind).toBe("truncated");
    expect(last.kind === "truncated" && last.hidden).toBe(200 * 51 - MAX_TREE_ITEMS);
  });
});

describe("hide idle (B1a)", () => {
  it("keeps blockers using one pass over the rows per view, not one pass per row", () => {
    const m = new MonitorModel("m", noFetch);
    const rows = [row(1, { status: "sleeping" }), row(2, { blockedBy: [1] }), ...Array.from({ length: 2_000 }, (_, i) => row(100 + i, { status: "sleeping" }))];
    m.apply(snap(1, rows));
    const builds = m.blockerSetBuilds;
    m.setFilters({ hideIdle: true });
    expect(m.blockerSetBuilds - builds).toBe(1); // one blocker set for 2,002 rows
    m.setSort("cpu");
    expect(m.blockerSetBuilds - builds).toBe(1); // same snapshot: reused
    expect(m.rows.map((r) => r.key.id)).toEqual([1, 2]); // the idle blocker stays, idle non-blockers go
    m.setSort("cpu");
    m.setSort("cpu"); // back to the default order
    m.apply(snap(2, [row(1, { status: "sleeping" }), row(2)])); // the blocking ended: a new snapshot, a new set
    expect(m.blockerSetBuilds - builds).toBe(2);
    expect(m.rows.map((r) => r.key.id)).toEqual([2]);
  });
});

describe("deadlock history read (B7)", () => {
  const failed = { events: [], status: { pane: "deadlocks" as const, state: "error" as const, reason: "The query did not finish within its time limit" }, truncated: false };

  it("a read whose status is error keeps its reason (never an empty 'no deadlocks' list)", async () => {
    const m = new MonitorModel("m", noFetch);
    await m.readDeadlocks(async () => failed);
    expect(m.deadlockRead?.error).toBe("The query did not finish within its time limit");
  });

  it("a failed read is tried again once each time the monitor reconnects, never in a loop", async () => {
    const m = new MonitorModel("m", noFetch);
    const read = vi.fn(async () => failed);
    m.apply(snap(1, [row(1)]));
    await m.readDeadlocks(read);
    expect(read).toHaveBeenCalledTimes(1);
    m.apply(snap(2, [row(1)])); // still connected: nothing
    m.apply(snap(3, null, { connection: "reconnecting" }));
    m.apply(snap(4, [row(1)])); // back: one new read
    await vi.waitFor(() => expect(read).toHaveBeenCalledTimes(2));
    m.apply(snap(5, [row(1)]));
    m.apply(snap(6, [row(1)]));
    await Promise.resolve();
    expect(read).toHaveBeenCalledTimes(2); // it failed again: no retry until the next reconnect
    m.apply(snap(7, null, { connection: "reconnecting" }));
    m.apply(snap(8, [row(1)]));
    await vi.waitFor(() => expect(read).toHaveBeenCalledTimes(3));
    // A read that succeeded is kept across reconnects.
    const ok = vi.fn(async () => ({ events: [], status: { pane: "deadlocks" as const, state: "ok" as const }, truncated: false }));
    await m.readDeadlocks(ok, true);
    m.apply(snap(9, null, { connection: "reconnecting" }));
    m.apply(snap(10, [row(1)]));
    await Promise.resolve();
    expect(ok).toHaveBeenCalledTimes(1);
    expect(read).toHaveBeenCalledTimes(3);
  });
});

describe("deadlock notifications (X16)", () => {
  const dl = (id: string, at: number) => ({ id, at, victims: [], processes: [], resources: [], partial: false });
  const status = (state: "collecting" | "ok" | "error") => [{ pane: "sessions" as const, state: "ok" as const }, { pane: "deadlocks" as const, state }];

  it("the first successful read is history and the rest is news, whatever the server's clock says", () => {
    for (const skew of [10 * 60_000, -10 * 60_000]) {
      const news = vi.fn();
      const m = new MonitorModel("m", noFetch, news);
      m.apply(snap(1, [row(1)], { status: status("collecting") }));
      m.apply(snap(2, [row(1)], { status: status("error") })); // the first read failed: nothing is history yet
      m.apply(snap(3, [row(1)], { status: status("ok"), newDeadlocks: [dl("old1", Date.now() + skew - 3_600_000), dl("old2", Date.now() + skew)] }));
      expect(news).not.toHaveBeenCalled();
      m.apply(snap(4, [row(1)], { status: status("ok"), newDeadlocks: [dl("new1", Date.now() + skew)] }));
      expect(news).toHaveBeenCalledTimes(1);
      expect(news.mock.calls[0][0].map((d: { id: string }) => d.id)).toEqual(["new1"]);
      m.apply(snap(5, [row(1)], { status: status("ok"), newDeadlocks: [dl("new1", Date.now() + skew)] })); // repeated: not again
      expect(news).toHaveBeenCalledTimes(1);
      expect(m.deadlocks.map((d) => d.id)).toEqual(["new1", "old2", "old1"]);
    }
  });

  it("a first read with no history still lets the next deadlock through", () => {
    const news = vi.fn();
    const m = new MonitorModel("m", noFetch, news);
    m.apply(snap(1, [row(1)], { status: status("ok") })); // read done, nothing in the last 24 h
    m.apply(snap(2, [row(1)], { status: status("ok"), newDeadlocks: [dl("n", 0)] }));
    expect(news).toHaveBeenCalledTimes(1);
  });

  it("a status-only snapshot never closes the baseline (its events come with the next full one)", () => {
    const news = vi.fn();
    const m = new MonitorModel("m", noFetch, news);
    m.apply(snap(1, null, { status: status("ok"), paused: true })); // e.g. Pause right after the first read
    m.apply(snap(2, [row(1)], { status: status("ok"), newDeadlocks: [dl("old", 0)] }));
    expect(news).not.toHaveBeenCalled();
  });
});

describe("kill availability (U4)", () => {
  const caps = { serverVersion: "x", panes: [], allSessionsVisible: true, canTerminate: true, canCancelRequest: false, cancelReason: "SQL Server can only terminate the whole session", livePlans: true };
  it("each degradation disables with its reason", () => {
    expect(killAvailability(row(1, { querithmKind: "pool", ownSessionId: "s1" }), caps, "terminateSession", true)).toEqual({ enabled: false, reason: QUERITHM_CONNECTION, goToSession: "s1" });
    expect(killAvailability(row(1, { isUserSession: false }), caps, "terminateSession", true).reason).toBe("System session.");
    expect(killAvailability(row(1, { identityHidden: "requires pg_read_all_stats", key: { id: 1, instance: "" } }), caps, "terminateSession", true).reason).toContain("pg_read_all_stats");
    expect(killAvailability(row(1), caps, "cancelRequest", true).reason).toContain("terminate the whole session");
    expect(killAvailability(row(1), { ...caps, canTerminate: false, terminateReason: "Terminating sessions requires ALTER ANY CONNECTION." }, "terminateSession", true).reason).toContain("ALTER ANY CONNECTION");
    expect(killAvailability(row(1), caps, "terminateSession", false).reason).toContain("disconnected");
    expect(killAvailability(row(1, { rollingBack: true }), caps, "terminateSession", true).reason).toContain("rolling back");
    expect(killAvailability(row(1, { status: "idle" }), { ...caps, canCancelRequest: true }, "cancelRequest", true).reason).toBe("No running query.");
    expect(killAvailability(row(1, { login: "other" }), { ...caps, login: "me", otherLoginsReason: "requires pg_signal_backend" }, "terminateSession", true).reason).toContain("pg_signal_backend");
    expect(killAvailability(row(1), caps, "terminateSession", true).enabled).toBe(true);
  });
});

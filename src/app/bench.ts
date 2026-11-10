// Bench hook (§14): QUERITHM_BENCH=1 (or ?bench in the dev bridge) exposes window.__bench so scripts/bench/run.mjs
// can drive the real UI. Each function returns raw samples; the harness computes median/p95 and compares runs.
import { rpc, ipcTimings } from "../ipc/client";
import { streamExecution } from "../ipc/frames";
import { ExecutionData, executionData } from "../results/model";
import { useExecutions } from "../store/executions";
import { useTabs } from "../store/tabs";
import { useUi } from "../store/ui";
import { complete } from "../editor/completion";
import { editorForTab, getModel } from "../editor/editors";
import { activateTab, changeConnection, newQueryTab, benchMarks, runQuery, workspaceHooks } from "./actions";
import { shell } from "../ipc/client";
import { useProfiles } from "../store/profiles";
import { useConnections } from "../store/connections";
import { snapKey, useSnapshots } from "../store/snapshots";
import { completionStats } from "../editor/providers";

const raf = () => new Promise<number>((r) => requestAnimationFrame(r));
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

export function bigProcedure(lines: number): string {
  const out: string[] = ["CREATE OR ALTER PROCEDURE dbo.usp_BenchBig @CustomerId int, @StartDate datetime2 = NULL AS", "BEGIN", "SET NOCOUNT ON;"];
  let cte = 0;
  while (out.length < lines - 2) {
    cte++;
    out.push(
      `WITH c${cte} AS (`,
      `    SELECT o.OrderId, o.CustomerId, o.OrderDate, o.TotalAmount FROM dbo.Orders o WHERE o.CustomerId = @CustomerId AND o.OrderDate >= @StartDate`,
      `)`,
      `SELECT c.OrderId, cu.FullName, c.TotalAmount FROM c${cte} c INNER JOIN dbo.Customers cu ON cu.CustomerId = c.CustomerId WHERE c.TotalAmount > ${cte};`,
    );
    if (cte % 7 === 0)
      out.push(
        `MERGE dbo.OrderTotals AS t USING (SELECT CustomerId, SUM(TotalAmount) s FROM dbo.Orders GROUP BY CustomerId) AS s ON t.CustomerId = s.CustomerId`,
        `WHEN MATCHED THEN UPDATE SET t.Total = s.s WHEN NOT MATCHED THEN INSERT (CustomerId, Total) VALUES (s.CustomerId, s.s);`,
      );
  }
  out.push("END;");
  return out.join("\n");
}

/**
 * Metrics 3 / 4 on Oracle (D4 S06): a PL/SQL package body of about `lines` lines — cursor loops over joins, SELECT INTO,
 * MERGE, q'…' literals, DBMS_OUTPUT and exception handlers — ending with its / line.
 */
export function bigPlsqlPackage(lines: number): string {
  const out: string[] = ["CREATE OR REPLACE PACKAGE BODY bench_big AS"];
  let n = 0;
  while (out.length < lines - 2) {
    n++;
    out.push(
      `  PROCEDURE p${n}(p_customer IN NUMBER, p_from IN DATE DEFAULT SYSDATE - 30) IS`,
      `    v_total NUMBER(12, 2) := 0;`,
      `    v_name customers.full_name%TYPE;`,
      `  BEGIN`,
      `    SELECT c.full_name INTO v_name FROM customers c WHERE c.customer_id = p_customer;`,
      `    FOR r IN (SELECT o.order_id, o.total_amount FROM orders o JOIN customers c ON c.customer_id = o.customer_id`,
      `              WHERE o.customer_id = p_customer AND o.order_date >= p_from AND o.note <> q'[it's; done]') LOOP`,
      `      v_total := v_total + r.total_amount;`,
      `      DBMS_OUTPUT.PUT_LINE('order ' || r.order_id || ' for ' || v_name || ': ' || TO_CHAR(r.total_amount, 'FM999G990D00'));`,
      `    END LOOP;`,
      `    MERGE INTO order_totals t USING (SELECT p_customer AS customer_id, v_total AS total FROM dual) s ON (t.customer_id = s.customer_id)`,
      `    WHEN MATCHED THEN UPDATE SET t.total = s.total WHEN NOT MATCHED THEN INSERT (customer_id, total) VALUES (s.customer_id, s.total);`,
      `  EXCEPTION`,
      `    WHEN NO_DATA_FOUND THEN NULL;`,
      `  END p${n};`,
    );
  }
  out.push("END bench_big;", "/");
  return out.join("\n");
}

async function typing(samples = 200, lines = 12_000, dialect: "tsql" | "oracle" = "tsql") {
  // Oracle: the tab is on the active tab's (Oracle) connection, so its model is the oracle language.
  const profileId = dialect === "oracle" ? useTabs.getState().tabs[useTabs.getState().activeId ?? ""]?.profileId : undefined;
  const id = dialect === "oracle"
    ? newQueryTab({ text: bigPlsqlPackage(lines), title: "bench-typing-oracle.sql", profileId })
    : newQueryTab({ text: bigProcedure(lines), title: "bench-typing.sql" });
  await sleep(500);
  const ed = editorForTab(id);
  const model = getModel(id)!;
  ed?.focus();
  const work: number[] = [];
  const toPaint: number[] = [];
  for (let i = 0; i < samples; i++) {
    const line = 4 + ((i * 37) % (lines - 10));
    // Work latency = synchronous edit handling + Monaco's render inside the next frame (vsync wait excluded).
    const t0 = performance.now();
    model.pushEditOperations([], [{ range: { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: 1 }, text: "x" }], () => null);
    ed?.setPosition({ lineNumber: line, column: 2 });
    const tSync = performance.now();
    const frameStart = await raf();
    const tEnd = performance.now();
    work.push(tSync - t0 + (tEnd - Math.max(frameStart, tSync)));
    toPaint.push(tEnd - t0);
  }
  return { work, toPaint };
}

async function tier1(samples = 300, lines = 12_000, dialect: "tsql" | "oracle" = "tsql") {
  const text = dialect === "oracle" ? bigPlsqlPackage(lines) : bigProcedure(lines);
  const tab = useTabs.getState().activeId ? useTabs.getState().tabs[useTabs.getState().activeId!] : undefined;
  const { tabSnapshot } = await import("../store/snapshots");
  const schema = tabSnapshot(tab)?.index ?? null;
  // Recorded with the metric: a tab not bound to the fixture would time completion without a schema.
  (window as unknown as { __tier1Objects?: number | null }).__tier1Objects = schema ? schema.objects.length : null;
  const times: number[] = [];
  for (let i = 0; i < samples; i++) {
    const offset = Math.floor((text.length * (i + 1)) / (samples + 2));
    const t0 = performance.now();
    complete({ text, offset, dialect, schema });
    times.push(performance.now() - t0);
  }
  return times;
}

async function tabSwitch(samples = 50) {
  const s = useTabs.getState();
  const ids = s.order.slice(0, 2);
  while (ids.length < 2) ids.push(newQueryTab({ text: "SELECT 1;" }));
  await sleep(300);
  const times: number[] = [];
  for (let i = 0; i < samples; i++) {
    // Click → the frame in which editor and results are rendered (same definition as the typing metric).
    const t0 = performance.now();
    activateTab(ids[i % 2]);
    await raf();
    times.push(performance.now() - t0);
    await sleep(30);
  }
  return times;
}

/**
 * Metric 7b: with 30 tabs open, switch between the first and the last tab of the strip (click → the frame in which
 * editor and results are rendered). Opens the missing tabs on the active tab's connection and leaves them open; the
 * tab that was active is active again afterwards.
 */
async function tabSwitch30(samples = 50) {
  const start = useTabs.getState().activeId;
  const base = start ? useTabs.getState().tabs[start] : undefined;
  while (useTabs.getState().order.length < 30) {
    newQueryTab({ profileId: base?.profileId ?? "", database: base?.database, text: `SELECT ${useTabs.getState().order.length};`, activate: false, atEnd: true });
  }
  await sleep(500);
  const order = workspaceHooks.stripOrder?.() ?? useTabs.getState().order;
  const ends = [order[0], order[order.length - 1]];
  const times: number[] = [];
  for (let i = 0; i < samples; i++) {
    const t0 = performance.now();
    activateTab(ends[i % 2]);
    await raf();
    times.push(performance.now() - t0);
    await sleep(30);
  }
  if (start) activateTab(start);
  return times;
}

/** Metrics 11–13: synthetic 1M × 40 result → first batch painted, scroll frame time, memory delta. */
async function bigResult(rows = 1_000_000, columns = 40, scrollFrames = 240) {
  const perf = performance as Performance & { memory?: { usedJSHeapSize: number } };
  const before = perf.memory?.usedJSHeapSize ?? null;
  const tabId = useTabs.getState().activeId ?? newQueryTab();
  // Warm the sidecar's encoder (JIT) the way any first real query would, then measure.
  const warm = await rpc("bench.synthetic", { rows: 20_000, columns });
  await streamExecution(warm.executionId!, { onSchema() {}, onBatch() {}, onEndResultSet() {}, onEnd() {} }).done;
  const t0 = performance.now();
  const r = await rpc("bench.synthetic", { rows, columns });
  const exId = r.executionId!;
  const data = new ExecutionData(exId);
  executionData.set(exId, data);
  useExecutions.getState().put({ id: exId, tabId, sessionId: "", state: "streaming", startedAt: Date.now(), resultSetCount: 1, truncated: false, messages: [], statistics: [], plans: [], sql: "-- synthetic", lineOffset: 1 });
  useTabs.getState().update(tabId, { activeExecutionId: exId, executions: [...useTabs.getState().tabs[tabId].executions, exId], bottomOpen: true, bottomTab: "results" });
  let firstPainted = -1;
  const done = streamExecution(exId, {
    onSchema: (ri, s) => data.get(ri).setSchema(s),
    onBatch: (ri, b) => {
      data.get(ri).append(b);
      data.notify();
      if (firstPainted < 0) {
        firstPainted = 0;
        void raf().then(() => raf()).then(() => (firstPainted = performance.now() - t0));
      }
    },
    onEndResultSet: (ri, tr) => data.get(ri).finish(tr),
    onEnd: () => data.notify(),
  }).done;
  await done;
  const fullMs = performance.now() - t0;
  while (firstPainted <= 0) await raf();
  await sleep(300);
  const scroller = document.querySelector(".dvn-scroller") as HTMLElement | null;
  const frames: number[] = [];
  if (scroller) {
    let last = await raf();
    for (let i = 0; i < scrollFrames; i++) {
      scroller.scrollTop += 2400;
      const now = await raf();
      frames.push(now - last);
      last = now;
    }
  }
  const after = perf.memory?.usedJSHeapSize ?? null;
  return { firstBatchMs: firstPainted, fullFetchMs: fullMs, scrollFrames: frames, memoryDeltaBytes: before != null && after != null ? after - before : null, rows: data.totalRows };
}

/**
 * §14 metric 34 (Phase C1): a 1M-row synthetic result (40 columns) fully in a tab, then Grid → Chart as a line with two
 * series (CreatedAt5 × Qty1, Amount3): toggle → the computed chart painted, and every frame interval meanwhile (the
 * batches are forwarded to the worker in ≤ 8 ms slices; aggregation and down-sampling run in the worker). One warm-up
 * toggle loads the chart's code; `samples` measured toggles follow. The tab shows its grid again afterwards.
 */
async function chartToggle(rows = 1_000_000, samples = 3) {
  const { setResultChart } = await import("../results/resultViews");
  const { chartTimings } = await import("../charts/ChartView");
  const tabId = useTabs.getState().activeId ?? newQueryTab();
  const r = await rpc("bench.synthetic", { rows, columns: 40 });
  const exId = r.executionId!;
  const data = new ExecutionData(exId);
  executionData.set(exId, data);
  useExecutions.getState().put({ id: exId, tabId, sessionId: "", state: "streaming", startedAt: Date.now(), resultSetCount: 1, truncated: false, messages: [], statistics: [], plans: [], sql: "-- synthetic", lineOffset: 1 });
  await streamExecution(exId, {
    onSchema: (ri, sc) => data.get(ri).setSchema(sc),
    onBatch: (ri, b) => data.get(ri).append(b),
    onEndResultSet: (ri, tr) => data.get(ri).finish(tr),
    onEnd: () => data.finishStream(),
  }).done;
  data.notify();
  const spec = { kind: "line" as const, x: "CreatedAt5", y: ["Qty1", "Amount3"], series: null, agg: "none" as const };
  useTabs.getState().update(tabId, {
    activeExecutionId: exId, executions: [...useTabs.getState().tabs[tabId].executions, exId], resultIndex: 0, bottomOpen: true, bottomTab: "results",
    charts: { 0: { view: "grid", spec } },
  });
  await sleep(500);
  const painted = (after: number) =>
    new Promise<number>((resolve) => {
      const check = () => {
        const t = chartTimings.painted.find((x) => x > after);
        if (t === undefined) return;
        chartTimings.listeners.delete(check);
        resolve(t);
      };
      chartTimings.listeners.add(check);
      check();
    });
  const toggle = async () => {
    const frames: number[] = [];
    let watching = true;
    let last = performance.now();
    const watch = () => {
      if (!watching) return;
      const now = performance.now();
      frames.push(now - last);
      last = now;
      requestAnimationFrame(watch);
    };
    requestAnimationFrame(watch);
    const t0 = performance.now();
    setResultChart(tabId, 0, { view: "chart" });
    const at = await painted(t0);
    const ms = at - t0;
    await sleep(200);
    watching = false;
    const long = frames.filter((f) => f > 32).map(Math.round);
    setResultChart(tabId, 0, { view: "grid" });
    await sleep(400);
    return { ms, frames, long };
  };
  await toggle(); // warm-up: the chart's code and the worker's first JIT
  const firstPaint: number[] = [];
  const frames: number[] = [];
  const long: number[][] = [];
  for (let i = 0; i < samples; i++) {
    const t = await toggle();
    firstPaint.push(t.ms);
    frames.push(...t.frames);
    long.push(t.long);
  }
  return { firstPaint,  frames, long, longFrames: frames.filter((f) => f > 32).length, worstFrameMs: Math.max(...frames), rows: data.totalRows };
}

/** Metric 14: frame times while typing and scrolling during concurrent long queries (caller starts the queries). */
async function frameDrops(durationMs = 10_000) {
  const frames: number[] = [];
  const end = performance.now() + durationMs;
  let last = await raf();
  const id = useTabs.getState().activeId;
  const model = id ? getModel(id) : undefined;
  let n = 0;
  while (performance.now() < end) {
    if (model && n++ % 3 === 0) model.applyEdits([{ range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 }, text: " " }]);
    const now = await raf();
    frames.push(now - last);
    last = now;
  }
  return frames;
}

/**
 * Metric 36frames (M13): frame times in the fixture tab while one run sends `count` PRINT messages, with the Messages
 * view open, until the run ends. Before M13 each message was its own notification and a copy of the whole log.
 */
async function messageFlood(count = 100_000) {
  // Its own fixture tab: the active one can be one of metric 14's 60 s queries (a tab added inactive may still be
  // activated by Dockview), and a busy tab runs nothing.
  if (!fixtureProfile) throw new Error("fixture profile not set up");
  const tabId = newQueryTab({ profileId: fixtureProfile, database: fixtureDb, text: "" });
  await sleep(300);
  const sql = `DECLARE @i int = 0; WHILE @i < ${count} BEGIN PRINT CONCAT('line ', @i); SET @i += 1; END;`;
  const t0 = performance.now();
  const execId = await runQuery(tabId, { sql, lineOffset: 1, options: { noHistory: true }, bottomTab: "messages" });
  if (!execId) {
    const t = useTabs.getState().tabs[tabId];
    throw new Error(`the run did not start (tab busy ${t?.busy}; last toast: ${useUi.getState().toasts.at(-1)?.text ?? "none"})`);
  }
  const frames: number[] = [];
  let last = await raf();
  const done = () => ["done", "failed", "cancelled"].includes(useExecutions.getState().views[execId]?.state ?? "");
  while (!done() && performance.now() - t0 < 120_000) {
    const now = await raf();
    frames.push(now - last);
    last = now;
  }
  const v = useExecutions.getState().views[execId];
  return { ms: performance.now() - t0, state: v?.state, messages: v?.messages.length ?? 0, frames: frames.length, longFrames: frames.filter((f) => f > 32).length, worstFrameMs: frames.length ? Math.max(...frames) : 0 };
}

let fixtureProfile: string | undefined;

/** Creates (or reuses) the fixture profile, connects it and binds the active tab, waiting for the snapshot. */
async function setupFixture(m: { host: string; port: number; user: string; password: string; db: string }) {
  let p = useProfiles.getState().profiles.find((x) => x.name === "Bench fixture");
  if (!p) {
    p = await useProfiles.getState().save({
      id: "", name: "Bench fixture", engine: "mssql", host: m.host, port: m.port, auth: "sql", username: m.user, databaseDefault: m.db, environment: "dev",
      color: null, groupName: null, tlsMode: "mandatory", trustCert: true, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null, createdAt: 0, updatedAt: 0,
    });
  }
  await shell.secretSet(p.id, m.password);
  fixtureProfile = p.id;
  fixtureDb = m.db;
  await useConnections.getState().connect(p.id);
  const tabId = useTabs.getState().activeId ?? newQueryTab();
  await changeConnection(tabId, p.id, m.db);
  for (let i = 0; i < 300 && !useSnapshots.getState().get(p.id, m.db)?.index?.objects.length; i++) await sleep(100);
  return { profileId: p.id, objects: useSnapshots.getState().get(p.id, m.db)?.index?.objects.length ?? 0 };
}

/**
 * Metrics 3.oracle / 4.oracle (D4 S06): a profile on the Oracle fixture (plain TCP, the app user), connected, with the
 * active tab on it once its snapshot has objects (tier 1 then completes against the user's schema).
 */
async function setupOracle(m: { host: string; port: number; user: string; password: string; service: string }) {
  let p = useProfiles.getState().profiles.find((x) => x.name === "Bench Oracle UI");
  if (!p) {
    p = await useProfiles.getState().save({
      id: "", name: "Bench Oracle UI", engine: "oracle", host: m.host, port: m.port, auth: "password", username: m.user, databaseDefault: m.service,
      environment: "dev", color: null, groupName: null, tlsMode: "disable", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null,
      createdAt: 0, updatedAt: 0,
    });
  }
  await shell.secretSet(p.id, m.password);
  await useConnections.getState().connect(p.id);
  const tabId = useTabs.getState().activeId ?? newQueryTab();
  await changeConnection(tabId, p.id, m.service);
  for (let i = 0; i < 600 && !useSnapshots.getState().get(p.id, m.service)?.index?.objects.length; i++) await sleep(100);
  return { profileId: p.id, objects: useSnapshots.getState().get(p.id, m.service)?.index?.objects.length ?? 0 };
}

/** UI smoke (D4 S06): replaces the active tab's text in one edit (typing it would meet auto-close and auto-indent). */
function setEditorText(text: string): boolean {
  const id = useTabs.getState().activeId;
  const model = id ? getModel(id) : undefined;
  if (!model) return false;
  model.setValue(text);
  return true;
}

/** UI smoke: the active tab's cursor (line and column), or null without an editor. */
function editorPosition(): { lineNumber: number; column: number } | null {
  const id = useTabs.getState().activeId;
  const p = id ? editorForTab(id)?.getPosition() : null;
  return p ? { lineNumber: p.lineNumber, column: p.column } : null;
}

/** Connects a profile (secret into the dev keyring) and opens a new tab on it. */
async function useProd(profileId: string, secret: string) {
  await useProfiles.getState().load();
  await shell.secretSet(profileId, secret);
  await useConnections.getState().connect(profileId);
  newQueryTab({ profileId });
  await sleep(300);
}

/** Starts n long queries on separate tabs/sessions (metric 14). */
async function startConcurrent(n = 5, seconds = 60) {
  const active = useTabs.getState().activeId;
  for (let i = 0; i < n; i++) {
    const id = newQueryTab({ profileId: fixtureProfile, text: `WAITFOR DELAY '00:${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}';`, activate: false });
    void runQuery(id);
  }
  if (active) activateTab(active);
  await sleep(500);
}

let fixtureDb = "";

/** ER diagram cost on the fixture schema (§14 metrics 18–19): 2-hop subgraph + layout, and whole-schema compact layout (worker). */
async function erd(runs = 5) {
  const { buildGraph, layoutGraph, neighbourhood, schemaScope, adjacency } = await import("../erd/erdLayout");
  const { layoutAsync } = await import("../erd/layoutAsync");
  const snap = useSnapshots.getState().get(fixtureProfile, fixtureDb)?.snapshot;
  if (!snap) throw new Error("fixture snapshot not loaded");
  if (snap.objects.length === 0) throw new Error("fixture snapshot is empty: the metadata load found no objects (fixture host unreachable?)");
  const adj = adjacency(snap);
  const root = [...adj.entries()].sort((a, b) => b[1].size - a[1].size)[0]?.[0] ?? snap.objects[0].id;
  const hop: number[] = [];
  let hopNodes = 0;
  for (let i = 0; i < runs + 1; i++) {
    const t0 = performance.now();
    const g = buildGraph(snap, neighbourhood(snap, [root], 2), false);
    layoutGraph(g);
    hop.push(performance.now() - t0);
    hopNodes = g.nodes.length;
  }
  const whole: number[] = [];
  let wholeNodes = 0;
  let wholeEdges = 0;
  for (let i = 0; i < 3; i++) {
    const t0 = performance.now();
    const g = buildGraph(snap, schemaScope(snap, null), true);
    await layoutAsync({ nodes: g.nodes.map((n) => ({ id: n.id, width: n.width, height: n.height })), edges: g.edges });
    whole.push(performance.now() - t0);
    wholeNodes = g.nodes.length;
    wholeEdges = g.edges.length;
  }
  return { hop: hop.slice(1), hopNodes, whole, wholeNodes, wholeEdges };
}

/**
 * §14 metric 28, webview half: JSON.parse + model apply + changed-cell damage for a 500-session snapshot (p95 of
 * steady ticks without new texts), and the first snapshot with every text. Returns payload sizes too.
 */
async function monitorApply(sessions = 500, ticks = 60) {
  const { MonitorModel, COLUMNS, DEFAULT_COLUMNS, changedCells } = await import("../monitor/model");
  const cols = COLUMNS.filter((c) => DEFAULT_COLUMNS.includes(c.id));
  const text = (i: number) => `SELECT o.OrderId, o.CustomerId, o.TotalAmount FROM dbo.Orders o WHERE o.CustomerId = ${i} AND o.OrderDate >= '2026-01-01' /* ${"x".repeat(300)} */`;
  const id = (i: number) => i.toString(16).padStart(32, "0");
  const row = (i: number, t: number) => ({
    key: { id: i, instance: `46321:${3076143 + i}` }, login: `user${i % 20}`, host: `host${i % 7}`, program: "Bench app", database: "SalesDB",
    status: (i + t) % 4 === 0 ? "running" : "sleeping", command: (i + t) % 4 === 0 ? "SELECT" : null, isUserSession: true,
    waitType: (i + t) % 9 === 0 ? "LCK_M_S" : null, waitMs: (i + t) % 9 === 0 ? (t * 100) % 40_000 : null, blockedBy: i % 50 === 0 ? [1] : [],
    textId: id(i), queryHash: "0x00000000DEADBEEF", elapsedMs: (i * 37 + t * 2000) % 900_000, cpuMs: i * 10 + t, reads: i * 100 + t, writes: 0, logicalReads: i * 1000 + t * 3,
    rowCount: 1, openTx: i % 11 === 0 ? 1 : 0, rollingBack: false,
  });
  const snap = (seq: number, withTexts: boolean) => ({
    monitorId: "bench", at: Date.now(), seq, sessions: Array.from({ length: sessions }, (_, i) => row(i + 1, seq)),
    texts: withTexts ? Object.fromEntries(Array.from({ length: sessions }, (_, i) => [id(i + 1), text(i + 1)])) : null,
    blocking: { headBlockers: [1], cycles: [], placeholders: [], blockedCount: Math.floor(sessions / 50) },
    status: [{ pane: "sessions", state: "ok" }], lagMs: 0, connectionCount: 1, throttled: false, paused: false, connection: "connected",
  });
  const m = new MonitorModel("bench", async () => ({ texts: {}, missing: [] }));
  const firstJson = JSON.stringify(snap(1, true));
  const t0 = performance.now();
  m.apply(JSON.parse(firstJson));
  changedCells([], m.rows, cols, m);
  const first = performance.now() - t0;
  const steady: number[] = [];
  let tickBytes = 0;
  for (let t = 2; t < ticks + 2; t++) {
    const json = JSON.stringify(snap(t, false));
    tickBytes = Math.max(tickBytes, json.length);
    const before = m.rows;
    const s = performance.now();
    m.apply(JSON.parse(json));
    changedCells(before, m.rows, cols, m);
    steady.push(performance.now() - s);
  }
  return { first, steady, firstBytes: firstJson.length, tickBytes, sessions };
}

/**
 * §14 metric 30, webview half: first paint of the 200-event deadlock list (with the newest event’s detail) after the
 * events arrive, and the render of a 10-process deadlock graph (median of 10). Timed to the next animation frame.
 */
async function deadlockRender(events = 200) {
  const { createElement } = await import("react");
  const { createRoot } = await import("react-dom/client");
  const { flushSync } = await import("react-dom");
  const { DeadlocksPane, DeadlockGraph } = await import("../monitor/DeadlocksPane");
  const { MonitorModel } = await import("../monitor/model");
  const frame = () => new Promise<void>((r) => requestAnimationFrame(() => r()));
  const ev = (n: number, procs: number) => ({
    id: n.toString(16).padStart(32, "0"), at: Date.now() - n * 60_000, database: "SalesDB", partial: false, source: "ring buffer",
    victims: [{ processId: "p0" }],
    processes: Array.from({ length: procs }, (_, i) => ({ id: `p${i}`, spid: 50 + i, victim: i === 0, login: "app", host: "web01", program: "Bench", database: "SalesDB",
      isolationLevel: "read committed (2)", lockMode: "X", waitResource: `KEY: 9:72057594085376000 (${i})`, waitMs: 1_000 + i, text: `UPDATE dbo.t SET v = v WHERE id = ${i}` })),
    resources: Array.from({ length: procs }, (_, i) => ({ id: `r${i}`, kind: "keylock", database: "SalesDB", object: "SalesDB.dbo.t", index: "PK_t",
      owners: [{ processId: `p${i}`, mode: "X", role: "owner" }, { processId: `p${(i + 1) % procs}`, mode: "X", role: "waiter" }] })),
  });
  const list = Array.from({ length: events }, (_, i) => ev(i + 1, i === 0 ? 10 : 2));
  const host = document.createElement("div");
  host.style.cssText = "position:fixed;left:0;top:0;width:1200px;height:800px;display:flex";
  document.body.appendChild(host);
  try {
    const m = new MonitorModel("bench-deadlocks", async () => ({ texts: {}, missing: [] }));
    const t0 = performance.now();
    m.apply({ monitorId: "bench-deadlocks", at: Date.now(), seq: 1, sessions: null, status: [{ pane: "deadlocks", state: "ok" }], lagMs: 0, connectionCount: 1,
      throttled: false, paused: false, connection: "connected", newDeadlocks: list } as never);
    const root = createRoot(host);
    flushSync(() => root.render(createElement(DeadlocksPane, { model: m, profileId: "bench" })));
    await frame();
    const listMs = performance.now() - t0;
    root.unmount();
    const graph: number[] = [];
    for (let i = 0; i < 10; i++) {
      const r = createRoot(host);
      const s = performance.now();
      flushSync(() => r.render(createElement(DeadlockGraph, { event: list[0] as never })));
      await frame();
      graph.push(performance.now() - s);
      r.unmount();
    }
    return { listMs, graph, events, processes: list[0].processes.length };
  } finally {
    host.remove();
  }
}

/**
 * §14 metric 33: the Object Explorer on a synthetic database of 25,000 objects (20,000 tables, 2,000 views, 3,000
 * procedures, all in one schema — the old schema grouping was quadratic there), driven through the real, mounted tree.
 * The synthetic server lives only in the stores and is removed afterwards.
 */
async function explorerTree(runs = 5) {
  const { useExplorer } = await import("../explorer/explorerState");
  const { treeStats } = await import("../explorer/Explorer");
  const { catKey, dbKey, searchRows } = await import("../explorer/tree");
  const { useSettings } = await import("../store/settings");
  const PID = "BENCH-EXPLORER";
  const settings = useSettings.getState();
  const before = { visible: settings.explorerVisible, autoHide: settings.explorerAutoHide, groups: settings.explorerSchemaGroups };
  settings.set({ explorerVisible: true, explorerAutoHide: false });
  const cols = [{ name: "id", type: "int", nullable: false, pk: true, identity: false, computed: false, ordinal: 1 }, { name: "a", type: "nvarchar(50)", nullable: true, pk: false, identity: false, computed: false, ordinal: 2 }];
  const objects: import("../ipc/contract").SnapObject[] = [];
  let id = 1;
  for (let i = 0; i < 20_000; i++) objects.push({ id: id++, schema: "dbo", name: `t${String(i).padStart(5, "0")}`, kind: "table", columns: cols });
  for (let i = 0; i < 2_000; i++) objects.push({ id: id++, schema: "dbo", name: `v${i}`, kind: "view", columns: cols });
  for (let i = 0; i < 3_000; i++) objects.push({ id: id++, schema: "dbo", name: `p${i}`, kind: "proc", params: [] });
  const other = objects.slice(0, 100);
  const entry = (db: string, objs: typeof objects, version: number) => ({ key: snapKey(PID, db), profileId: PID, database: db, status: "fresh" as const, snapshot: { profileId: PID, database: db, status: "fresh" as const, objects: objs, fks: [] }, version });
  useProfiles.setState((s) => ({ profiles: [...s.profiles, { id: PID, name: "Bench catalog", engine: "mssql", host: "bench", port: 1, auth: "sql", username: "x", databaseDefault: "Big", environment: "dev", color: null, groupName: null, tlsMode: "mandatory", trustCert: false, caPath: null, timeoutS: 15, commandTimeoutS: 0, options: null, createdAt: 0, updatedAt: 0 } as never] }));
  useConnections.setState((s) => ({ order: [...s.order, PID], servers: { ...s.servers, [PID]: { profileId: PID, sessionId: "bench", status: "connected", databases: [{ name: "Big", isSystem: false }, { name: "Other", isSystem: false }] } } }));
  useSnapshots.setState((s) => ({ entries: { ...s.entries, [snapKey(PID, "Big")]: entry("Big", objects, 1), [snapKey(PID, "Other")]: entry("Other", other, 1) } }));
  const ex = useExplorer.getState();
  ex.setExpanded((prev) => new Set([...prev, `s:${PID}`, dbKey(PID, "Big"), dbKey(PID, "Other")]));
  for (let i = 0; i < 5; i++) await raf();
  const tables = catKey(PID, "Big", "tables");
  const tree = document.querySelector<HTMLElement>("[data-q-explorer] [role='tree']");
  try {
    if (!tree) throw new Error("Explorer tree not mounted");
    const expand: number[] = [];
    for (let i = 0; i < runs + 1; i++) {
      const t0 = performance.now();
      ex.toggle(tables, true);
      await raf();
      expand.push(performance.now() - t0);
      ex.toggle(tables, false);
      await raf();
      await raf();
    }
    expand.shift(); // the first build also groups the snapshot (once per snapshot)
    ex.toggle(tables, true);
    for (let i = 0; i < 3; i++) await raf();
    // Scroll into the 20,000 tables, one step per frame; count tree renders.
    const tablesRow = [...tree.querySelectorAll<HTMLElement>("[role='treeitem']")].find((r) => r.getAttribute("aria-label")?.startsWith("Tables, 20000"));
    tree.scrollTop = (Number(tablesRow?.style.top.replace("px", "")) || 0) + 240;
    for (let i = 0; i < 3; i++) await raf();
    const renders0 = treeStats.renders;
    const scroll: number[] = [];
    /** Frame times of the steps that rendered the tree (a new block of rows), apart from the others. */
    const renderFrames: number[] = [];
    const steps = 240;
    let last = await raf();
    for (let i = 0; i < steps; i++) {
      const r0 = treeStats.renders;
      tree.scrollTop += 37;
      const now = await raf();
      scroll.push(now - last);
      if (treeStats.renders !== r0) renderFrames.push(now - last);
      last = now;
    }
    const renders = treeStats.renders - renders0;
    // A metadata patch to another (collapsed) database while 20,000 rows are expanded.
    const patch: number[] = [];
    for (let i = 0; i < runs; i++) {
      const t0 = performance.now();
      useSnapshots.setState((s) => ({ entries: { ...s.entries, [`${PID}|other`]: entry("Other", [...other], 2 + i) } }));
      await raf();
      patch.push(performance.now() - t0);
      await raf();
    }
    // Search keystrokes over 25,000 objects (the pure match + group + sort the search box runs); the first, cold call
    // (JIT) is reported apart, as for metric 25.
    const cold0 = performance.now();
    searchRows(PID, "Big", "mssql", objects, "t", "all", new Set());
    const searchCold = performance.now() - cold0;
    const search: number[] = [];
    for (const q of ["t", "t1", "t12", "t123", "v1", "p2", "x", "t0001", "t", "v", "p", "t9"]) {
      const t0 = performance.now();
      searchRows(PID, "Big", "mssql", objects, q, "all", new Set());
      search.push(performance.now() - t0);
    }
    // Group by schema on the 25,000-object database.
    const group: number[] = [];
    for (let i = 0; i < runs; i++) {
      const t0 = performance.now();
      useSettings.getState().set({ explorerSchemaGroups: [...before.groups, `${PID}|Big`] });
      await raf();
      group.push(performance.now() - t0);
      useSettings.getState().set({ explorerSchemaGroups: before.groups });
      await raf();
      await raf();
    }
    return { expand, scroll, renderFrames, renders, steps, patch, search, searchCold, group, objects: objects.length };
  } finally {
    useExplorer.getState().setExpanded((prev) => new Set([...prev].filter((k) => !k.includes(PID))));
    useSnapshots.setState((s) => {
      const { [snapKey(PID, "Big")]: _a, [snapKey(PID, "Other")]: _b, ...rest } = s.entries;
      return { entries: rest };
    });
    useConnections.setState((s) => {
      const { [PID]: _c, ...rest } = s.servers;
      return { servers: rest, order: s.order.filter((x) => x !== PID) };
    });
    useProfiles.setState((s) => ({ profiles: s.profiles.filter((p) => p.id !== PID) }));
    useSettings.getState().set({ explorerVisible: before.visible, explorerAutoHide: before.autoHide, explorerSchemaGroups: before.groups });
  }
}

/** Smoke test: the Explorer's dock width (the layout audit shoots the narrowest and the widest). */
async function explorerWidth(px: number) {
  const { useUi } = await import("../store/ui");
  useUi.getState().setExplorer({ explorerWidth: px });
}

/** Smoke test: the activity monitor for a profile (as Explorer → Activity monitor). */
async function openMonitor(profileId: string) {
  const { openActivityMonitor } = await import("../monitor/monitorActions");
  openActivityMonitor(profileId);
}

/** Smoke test: Explorer → Disconnect for a profile. */
async function disconnect(profileId: string) {
  const { disconnectServer } = await import("./actions");
  await disconnectServer(profileId);
}

export function installBench() {
  /** Smoke diagnostics: each grid staging's table, whether its rows finished loading, and why each cell of row 0 is locked. */
  const stagingInfo = async () => {
    const { stagings } = await import("../results/staging");
    return [...stagings.entries()].map(([key, s]) => ({
      key, table: s.edit.table, complete: (s.src as { complete?: boolean }).complete ?? null,
      locks: s.src.columns.map((c) => ({ name: c.name, ordinal: c.ordinal, lock: s.lockReason({ kind: "existing", row: 0 }, c.ordinal) })),
    }));
  };
  (window as unknown as { __bench: unknown }).__bench = {
    stagingInfo,
    ready: true,
    timeOrigin: performance.timeOrigin,
    marks: () => ({ navigationToNow: performance.now() }),
    setupFixture,
    setupOracle,
    erd,
    monitorApply,
    deadlockRender,
    explorerTree,
    explorerWidth,
    openMonitor,
    disconnect,
    useProd,
    setEditorText,
    editorPosition,
    startConcurrent,
    rpc,
    typing,
    tier1,
    tabSwitch,
    tabSwitch30,
    bigResult,
    chartToggle,
    frameDrops,
    messageFlood,
    ipcTimings,
    completionStats: () => completionStats,
    dbSwitch: () => benchMarks.dbSwitch,
    firstBatch: () => benchMarks.firstBatch,
  };
}

const p95 = (a: number[]) => {
  const s = [...a].sort((x, y) => x - y);
  return s.length ? s[Math.min(s.length - 1, Math.ceil(s.length * 0.95) - 1)] : NaN;
};

/**
 * Tauri bench mode (QUERITHM_BENCH=1): runs the UI metrics inside the real webview (WebKitGTK on Linux, §14 gate)
 * and writes $DATA/querithm/bench/app-<time>.json for scripts/bench/run.mjs --app.
 */
export async function runAppBench(bootMs: number) {
  await sleep(2000);
  // OS keyring roundtrip through the shell (Secret Service / Credential Manager / Keychain).
  let keyring = "ok";
  try {
    await shell.secretSet("bench-probe", "probe-value");
    if (!(await shell.secretExists("bench-probe"))) keyring = "set but not found";
    await shell.secretDelete("bench-probe");
    if (await shell.secretExists("bench-probe")) keyring = "delete failed";
  } catch (e) {
    keyring = `error: ${(e as Error).message ?? e}`;
  }
  const typingR = await typing(150, 12_000);
  const t1 = await tier1(200, 12_000);
  const tabs = await tabSwitch(40);
  const big = await bigResult(1_000_000, 40, 180);
  const refresh = [...big.scrollFrames].sort((a, b) => a - b)[Math.floor(big.scrollFrames.length / 2)];
  const result = {
    date: new Date().toISOString(),
    userAgent: navigator.userAgent,
    bootMs,
    keyring,
    typingWorkP95: p95(typingR.work),
    typingToPaintP95: p95(typingR.toPaint),
    tier1P95: p95(t1),
    tabSwitchP95: p95(tabs),
    firstBatchMs: big.firstBatchMs,
    fullFetchMs: big.fullFetchMs,
    scrollP95: p95(big.scrollFrames),
    scrollRefreshMs: refresh,
    longScrollFrames: big.scrollFrames.filter((f) => f > refresh + 1.5).length,
  };
  const fs = await import("@tauri-apps/plugin-fs");
  await fs.mkdir("querithm/bench", { baseDir: fs.BaseDirectory.Data, recursive: true }).catch(() => {});
  await fs.writeTextFile(`querithm/bench/app-${Date.now()}.json`, JSON.stringify(result, null, 2), { baseDir: fs.BaseDirectory.Data });
  return result;
}

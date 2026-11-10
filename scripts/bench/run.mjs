#!/usr/bin/env node
// Benchmark harness (§14). Engine metrics drive the sidecar over its RPC; UI metrics drive the webview through
// window.__bench (dev bridge + headless Chrome here; QUERITHM_BENCH=1 exposes the same hook in the Tauri app).
// Results: bench/results/<date>-<os>.json, compared with the previous run; a regression > 20 % fails.
//
//   pnpm bench                 # engine + ui (needs the docker fixture: scripts/bench/gen-schema.mjs --apply)
//   pnpm bench -- --engine     # engine metrics only
//   pnpm bench -- --ui         # ui metrics only
//   pnpm bench -- --sqlite     # SQLite per-engine metrics only (temp-dir files, no Docker)
//   pnpm bench -- --mysql      # MySQL per-engine metrics only (the mysql84 fixture)
//   pnpm bench -- --duckdb     # DuckDB per-engine metrics only (temp-dir files, no Docker)
//   pnpm bench -- --oracle     # Oracle per-engine metrics only (the oracle fixture, --profile oracle)
//   pnpm bench -- --app <bin>  # also measure the bundled app (cold start, RSS, orphan check) — Linux/macOS
import { spawn, spawnSync, execFileSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync, mkdtempSync } from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import readline from "node:readline";
import { fileURLToPath } from "node:url";
import { bigPlpgsql, bigProc, bigResultQuery } from "./gen-schema.mjs";
import { fixturePassword } from "../fixture/fixture-secrets.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const args = process.argv.slice(2);
const only = args.includes("--engine") ? "engine" : args.includes("--ui") ? "ui" : args.includes("--sqlite") ? "sqlite" : args.includes("--mysql") ? "mysql" : args.includes("--duckdb") ? "duckdb" : args.includes("--oracle") ? "oracle" : "all";
const appBin = args.includes("--app") ? args[args.indexOf("--app") + 1] : null;
const RUNS = 5;

const MSSQL = { host: process.env.QUERITHM_IT_MSSQL_HOST || "localhost", port: 14333, user: "sa", password: fixturePassword("mssql"), db: "SalesDB" };
const PG = { host: process.env.QUERITHM_IT_PG_HOST || "localhost", port: 54329, user: "postgres", password: fixturePassword("pg"), db: "postgres" };
const MYSQL = { host: "127.0.0.1", port: 33084, user: "root", password: fixturePassword("mysql"), db: "q_bench" };
// Phase D4: Oracle Free's least-privileged app user over plain TCP (the fixture's TCPS certificate is self-signed).
const ORACLE = { host: "127.0.0.1", port: 15210, user: "querithm_app", password: fixturePassword("oracle"), service: "FREEPDB1" };

// metric id → [name, unit, target, compare] ; compare: "le" (value must be ≤ target)
const TARGETS = {
  1: ["Cold start to interactive editor", "ms", 1500],
  2: ["Idle RSS after start, no connections", "MB", 180],
  3: ["Typing latency in the 12k-line proc (p95)", "ms", 16],
  4: ["Tier-1 completion latency (p95)", "ms", 5],
  5: ["Tier-2 completion latency, 12k-line proc (p95)", "ms", 150],
  6: ["Full parse of 12k-line proc", "ms", 100],
  7: ["Tab switch between connected sessions (p95)", "ms", 50],
  "7b": ["Tab switch 1 ↔ 30 with 30 tabs open (p95)", "ms", 50],
  8: ["DB switch, SQL Server, existing connection (overhead over one roundtrip)", "ms", 20],
  9: ["Metadata snapshot load from SQLite", "ms", 80],
  10: ["Incremental refresh with 10 changed objects", "ms", 2000],
  11: ["1M-row result: time to first batch painted (minus server time)", "ms", 300],
  "11cold": ["Execute → first batch on a cold pool connection (SELECT 1, median, slower engine)", "ms", 300],
  12: ["1M-row result: scroll frame time (p95)", "ms", 16],
  13: ["1M-row result: peak webview memory delta", "MB", 600],
  14: ["Frames > 32 ms with 5 concurrent 60 s queries", "frames", 0],
  15: ["Cancel latency", "ms", 500],
  16: ["Sidecar processes alive 5 s after killing the app", "count", 0],
  17: ["Installer size (largest package but the AppImage)", "MB", 120],
  // Owner decision 2026-10-31 (§16): the AppImage carries WebKitGTK, JavaScriptCore and ICU, which the other packages take from the OS.
  "17.appimage": ["Installer size, Linux AppImage", "MB", 150],
  18: ["ERD 2-hop neighbourhood on the fixture: subgraph + layout", "ms", 30],
  19: ["ERD whole-schema compact layout (web worker)", "ms", 1000],
  20: ["Grid edits: DML preview for 100 staged cells (roundtrip)", "ms", 15],
  21: ["Schema compare of the fixture database with itself, slowest engine", "ms", 2000],
  22: ["Data compare 100k × 10, 1k differences, slowest engine (incl. server time)", "ms", 1500],
  23: ["Import 100k × 10 CSV, slowest engine (SQL Server / PostgreSQL)", "ms", 2000],
  24: ["Find usages of a column all ~550 cached modules mention + 5 open tabs (p50, SQL Server)", "ms", 120],
  27: ["Tier-2 completion latency, PostgreSQL, 10k-line PL/pgSQL function (p95)", "ms", 150],
  // Activity monitor (Phase B): the webview half of metric 28; the server/sidecar halves and 29–32 are measured by
  // QUERITHM_BENCH_MONITOR=1 dotnet test sidecar --filter MonitorBench (bench/results/<date>-monitor-<os>.json).
  "28ui": ["Activity snapshot, 500 sessions: parse + apply + cell damage in the webview (p95)", "ms", 16],
  "28first": ["Activity first snapshot, 500 sessions with texts: parse + apply in the webview", "ms", 50],
  "30list": ["Deadlock history: first paint of the 200-event list after the events arrive", "ms", 100],
  "30graph": ["Deadlock history: graph render of a 10-process deadlock (median of 10)", "ms", 50],
  // Object Explorer on a synthetic database of 25,000 objects (20,000 tables in one schema), through the mounted tree.
  "33expand": ["Explorer: expand a 20,000-table folder → painted (median)", "ms", 50],
  "33scroll": ["Explorer: scroll frame time through 20,000 tables (p95; renders once per 16 rows)", "ms", 16],
  "33patch": ["Explorer: metadata patch to another database while 20,000 rows are expanded → painted (median)", "ms", 33],
  "33search": ["Explorer: search keystroke over 25,000 objects (p95, warm)", "ms", 16],
  "33group": ["Explorer: group a 25,000-object database by schema → painted (median)", "ms", 50],
  // Result charts (Phase C1): a 1M-row result as a line chart with two series, aggregated in the chart worker.
  34: ["Chart 1M-row result, line with 2 series: Chart toggle → first paint (median)", "ms", 500],
  "34frames": ["Chart toggle on a 1M-row result: frames longer than 32 ms", "frames", 0],
  // Server-message floods (M13): one run's 100,000 PRINT (SQL Server) / RAISE NOTICE (PostgreSQL) messages, which the
  // sidecar coalesces into query.messages batches (the first 20,000 forwarded, then one "not shown" line).
  36: ["Message flood: 100k PRINT / RAISE NOTICE in one run → query.done (slower engine, median of 3, server time included)", "ms", 5000],
  "36notify": ["Message flood: notifications the sidecar sends for one 100k-message run (most of either engine)", "notifications", 300],
  "36frames": ["Message flood in a tab (100k PRINT, Messages open): frames longer than 32 ms until the run ends", "frames", 0],
  // Phase D per-engine metric ids (§14 "Cross-cutting per engine"): SQLite on a temp-dir file, no Docker.
  "11cold.sqlite": ["SQLite: execute → first batch on a new tab's own connection (open + hardening + SELECT 1, median)", "ms", 300],
  "15.sqlite": ["SQLite: cancel latency (sqlite3_interrupt)", "ms", 500],
  "9.sqlite": ["SQLite: metadata snapshot load from SQLite (600 tables, 150 views, 200 triggers)", "ms", 80],
  "10.sqlite": ["SQLite: incremental refresh with 10 changed objects", "ms", 2000],
  "5.sqlite": ["SQLite: tier-2 completion latency, 10k-line script on the 600-table fixture (p95)", "ms", 150],
  "20.sqlite": ["SQLite: grid edits, DML preview for 100 staged cells", "ms", 15],
  "23.sqlite": ["SQLite: import 100k × 10 CSV into a new table (median of 3)", "ms", 2000],
  // Phase D2: MySQL 8.4 (the fixture's mysql84, TLS required), hidden behind the dev setting like SQLite.
  // Phase D4: Oracle (hidden engine), the fixture's Oracle Free.
  "11b.oracle": ["Oracle: execute → first batch on a cold pool connection (TCP, login, product + identity read, SELECT 1 FROM dual, median)", "ms", 300],
  "9.oracle": ["Oracle: metadata snapshot load from SQLite (600 tables, 150 views, 200 triggers)", "ms", 80],
  "10.oracle": ["Oracle: incremental refresh with 10 changed objects (least-privileged user, ALL_* views)", "ms", 2000],
  "11cold.mysql": ["MySQL: execute → first batch on a cold pool connection (TCP, TLS, login, identity read, SELECT 1, median)", "ms", 300],
  "8.mysql": ["MySQL: database switch on an existing connection (COM_INIT_DB, median)", "ms", 20],
  "15.mysql": ["MySQL: cancel latency (KILL QUERY on a side connection)", "ms", 500],
  "9.mysql": ["MySQL: metadata snapshot load from SQLite (600 tables, 150 views, 200 triggers)", "ms", 80],
  "10.mysql": ["MySQL: incremental refresh with 10 changed objects", "ms", 2000],
  "5.mysql": ["MySQL: tier-2 completion latency, 10k-line script on the 600-table fixture (p95)", "ms", 150],
  "20.mysql": ["MySQL: grid edits, DML preview for 100 staged cells", "ms", 15],
  "21.mysql": ["MySQL: schema compare of the fixture database with itself (600 tables, 150 views, 200 triggers)", "ms", 2000],
  "22.mysql": ["MySQL: data compare 100k × 10 against a copy with 1,000 changed rows", "ms", 1500],
  "23.mysql": ["MySQL: import 100k × 10 CSV into a new table (median of 3)", "ms", 2000],
  "24.mysql": ["MySQL: find usages of a column every fixture module mentions + 5 open tabs", "ms", 120],
  // Phase D3: DuckDB on temp-dir files (no Docker).
  "11cold.duckdb": ["DuckDB: execute → first batch on a new tab's own connection (new file, open + policy + SELECT 1, median)", "ms", 300],
  "15.duckdb": ["DuckDB: cancel latency (duckdb_interrupt)", "ms", 500],
  "9.duckdb": ["DuckDB: metadata snapshot load from SQLite (600 tables, 150 views)", "ms", 80],
  "10.duckdb": ["DuckDB: incremental refresh with 10 changed objects", "ms", 2000],
  "23.duckdb": ["DuckDB: import 100k × 10 CSV into a new table (appender, median of 3)", "ms", 2000],
  "5.duckdb": ["DuckDB: tier-2 completion latency, 10k-line script on the 600-table fixture (p95)", "ms", 150],
  "20.duckdb": ["DuckDB: grid edits, DML preview for 100 staged cells", "ms", 15],
};

const median = (a) => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.floor((s.length - 1) / 2)] : NaN; };
const p95 = (a) => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.min(s.length - 1, Math.ceil(s.length * 0.95) - 1)] : NaN; };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const results = {};
const record = (id, value, extra = {}) => {
  results[id] = { metric: TARGETS[id][0], unit: TARGETS[id][1], target: TARGETS[id][2], value: Number.isFinite(value) ? Math.round(value * 100) / 100 : value, pass: value <= TARGETS[id][2], ...extra };
  console.log(`  #${id} ${TARGETS[id][0]}: ${results[id].value} ${TARGETS[id][1]} (target ≤ ${TARGETS[id][2]}) ${results[id].pass ? "PASS" : "FAIL"}`);
};

let builtSidecar = null;
function sidecarBin() {
  if (process.env.QUERITHM_SIDECAR) return process.env.QUERITHM_SIDECAR;
  // Always (incrementally) built: an existing binary can be older than the code under test, and the run would measure it.
  if (!builtSidecar) {
    const r = spawnSync("dotnet", ["build", path.join(root, "sidecar", "Querithm.Host"), "-c", "Release", "-v", "q", "--nologo"], { stdio: "inherit" });
    if (r.status !== 0) throw new Error(`sidecar Release build failed (${r.error?.message ?? "exit " + r.status})`);
    builtSidecar = path.join(root, "sidecar", "Querithm.Host", "bin", "Release", "net10.0", process.platform === "win32" ? "querithm-sidecar.exe" : "querithm-sidecar");
  }
  return builtSidecar;
}

/** Minimal sidecar client: JSON-RPC over stdio + frame counting on the data socket. */
async function startSidecar(extraEnv = {}) {
  const token = randomBytes(16).toString("hex");
  // Windows: the sidecar takes a pipe name and listens on \\.\pipe\<name> (as in dev-bridge.mjs); elsewhere a socket path.
  const tag = `qbench-${process.pid}-${Math.random().toString(36).slice(2, 6)}`;
  const sockName = process.platform === "win32" ? tag : path.join(os.tmpdir(), `${tag}.sock`);
  const sock = process.platform === "win32" ? `\\\\.\\pipe\\${tag}` : sockName;
  const dataDir = mkdtempSync(path.join(os.tmpdir(), "qbench-data-"));
  // QUERITHM_BENCH=1: the engine registers bench.* (bench.synthetic) only for a harness that asks (L10).
  const child = spawn(sidecarBin(), [], { env: { ...process.env, ...extraEnv, QUERITHM_BENCH: "1", QUERITHM_TOKEN: token, QUERITHM_SOCKET: sockName, QUERITHM_PARENT_PID: String(process.pid), QUERITHM_DATA_DIR: dataDir, QUERITHM_LOG: "warn" }, stdio: ["pipe", "pipe", "ignore"] });
  rssMb(child.pid); // Windows: starts the background working-set sampler now, so the idle reading below has samples
  let id = 1;
  const pending = new Map();
  const listeners = new Set();
  // Work started by a request runs in the background, so its final notification can beat the reply that names it.
  const recent = [];
  readline.createInterface({ input: child.stdout }).on("line", (l) => {
    const m = JSON.parse(l);
    if (m.id != null && pending.has(m.id)) {
      const p = pending.get(m.id);
      pending.delete(m.id);
      m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result);
    } else if (m.method) {
      recent.push([m.method, m.params]);
      if (recent.length > 5000) recent.shift();
      listeners.forEach((f) => f(m.method, m.params));
    }
  });
  const call = (method, params) =>
    new Promise((resolve, reject) => {
      const i = id++;
      pending.set(i, { resolve, reject });
      child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id: i, method, ...(params ? { params } : {}) }) + "\n");
    });
  /** `replay`: also match notifications already received; only for predicates on an id the reply returned. */
  const until = (pred, timeout = 120_000, label = pred.toString(), replay = false) =>
    new Promise((resolve, reject) => {
      const hit = replay ? recent.find(([m, p]) => pred(m, p)) : undefined;
      if (hit) return resolve(hit[1]);
      const t = setTimeout(() => { listeners.delete(f); reject(new Error(`timeout after ${timeout} ms waiting for ${label}`)); }, timeout);
      const f = (m, p) => { if (pred(m, p)) { clearTimeout(t); listeners.delete(f); resolve(p); } };
      listeners.add(f);
    });
  const frames = new Map();
  let ds;
  for (let i = 0; ; i++) {
    try {
      ds = await new Promise((resolve, reject) => {
        const c = net.createConnection(sock);
        c.once("connect", () => resolve(c));
        c.once("error", reject);
      });
      break;
    } catch (e) {
      if (i > 200) throw e;
      await sleep(50);
    }
  }
  ds.write(token + "\n");
  let buf = Buffer.alloc(0);
  ds.on("data", (c) => {
    buf = Buffer.concat([buf, c]);
    while (buf.length >= 4 && buf.length >= 4 + buf.readUInt32LE(0)) {
      const len = buf.readUInt32LE(0);
      const kind = buf[4];
      const exec = buf.subarray(5, 21).toString("hex");
      buf = buf.subarray(4 + len);
      const e = frames.get(exec) ?? { batches: 0, firstAt: 0, endAt: 0 };
      if (kind === 2) { e.batches++; if (!e.firstAt) e.firstAt = performance.now(); }
      if (kind === 4) e.endAt = performance.now();
      frames.set(exec, e);
    }
  });
  return { child, call, until, frames, dataDir, kill: () => { ds.destroy(); child.kill(); } };
}

function rssMb(pid) {
  try {
    const s = readFileSync(`/proc/${pid}/status`, "utf8");
    return Number(/VmRSS:\s+(\d+)/.exec(s)[1]) / 1024;
  } catch {
    if (process.platform === "win32") return windowsWorkingSetMb(pid);
    const out = spawnSync("ps", ["-o", "rss=", "-p", String(pid)]).stdout?.toString().trim();
    return out ? Number(out) / 1024 : NaN;
  }
}

/**
 * Windows has no /proc: one background PowerShell per process reports its working set (the counterpart of RSS) every
 * 20 ms and this returns the latest sample. Never a synchronous spawn: #22 samples memory every 20 ms inside its
 * timed section, and a blocking call there would be measured as data-compare time.
 */
const workingSets = new Map();
function windowsWorkingSetMb(pid) {
  let s = workingSets.get(pid);
  if (!s) {
    s = { mb: NaN };
    const ps = spawn("powershell", ["-NoProfile", "-Command", `while ($true) { try { (Get-Process -Id ${Number(pid)} -ErrorAction Stop).WorkingSet64 } catch { exit }; Start-Sleep -Milliseconds 20 }`], { stdio: ["ignore", "pipe", "ignore"] });
    readline.createInterface({ input: ps.stdout }).on("line", (l) => { const v = Number(l); if (Number.isFinite(v) && v > 0) s.mb = v / 1024 / 1024; });
    ps.unref();
    process.once("exit", () => ps.kill());
    workingSets.set(pid, s);
  }
  return s.mb;
}

async function engineMetrics() {
  console.log("engine metrics");
  const sc = await startSidecar();
  const { call, until } = sc;
  try {
    await sleep(1500);
    results.sidecarIdleRssMb = Math.round(rssMb(sc.child.pid));
    console.log(`  sidecar idle RSS ${results.sidecarIdleRssMb} MB`);
    const profile = await call("profile.save", { id: "", name: "Bench", engine: "mssql", host: MSSQL.host, port: MSSQL.port, auth: "sql", username: MSSQL.user, environment: "dev", tlsMode: "mandatory", trustCert: true, databaseDefault: MSSQL.db });
    const { sessionId } = await call("session.open", { profileId: profile.id, database: MSSQL.db, secret: MSSQL.password });
    await call("session.state", { sessionId });

    // #9 snapshot load (after a full refresh populated the cache)
    const refreshed = until((m, p) => m === "meta.updated" && p.status === "fresh");
    await call("meta.refresh", { profileId: profile.id, database: MSSQL.db, full: true });
    const full = await refreshed;
    results.fullRefreshMs = Math.round(full.elapsedMs);
    const snapTimes = [];
    let objects = 0;
    for (let i = 0; i < RUNS; i++) {
      const t0 = performance.now();
      const s = await call("meta.snapshot", { profileId: profile.id, database: MSSQL.db });
      snapTimes.push(performance.now() - t0);
      objects = s.objects.length;
    }
    record(9, median(snapTimes), { objects, samples: snapTimes });

    // #10 incremental refresh with 10 changed objects
    const incr = [];
    for (let r = 0; r < RUNS; r++) {
      const sql = Array.from({ length: 10 }, (_, k) => `CREATE OR ALTER PROCEDURE dbo.usp_p${k + 1} @Id int, @From datetime2 = NULL, @Count int OUTPUT AS BEGIN /* rev ${Date.now()}-${r} */ SET NOCOUNT ON; SELECT @Count = ${r}; END;`).join("\nGO\n");
      const ex = await call("query.execute", { sessionId, sql, options: { noHistory: true } });
      await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 120_000, undefined, true);
      await sleep(1100); // modify_date has millisecond precision but let the DDL-triggered refresh settle first
      const done = until((m, p) => m === "meta.updated" && p.status === "fresh");
      const t0 = performance.now();
      await call("meta.refresh", { profileId: profile.id, database: MSSQL.db, full: false });
      const ev = await done;
      incr.push(performance.now() - t0);
      results[`incrChanged${r}`] = ev.changed;
    }
    record(10, median(incr), { samples: incr });

    // #5, #6 tier-2 latency and full parse on the 12k-line proc
    const text = bigProc("mssql");
    const parse = [];
    for (let i = 0; i < RUNS; i++) {
      const t0 = performance.now();
      await call("lang.diagnostics", { docId: `bench-parse-${i}-${Date.now()}`, text: text + `\n-- ${i}`, dialect: "tsql" });
      parse.push(performance.now() - t0);
    }
    record(6, median(parse), { lines: text.split("\n").length, samples: parse });
    const comp = [];
    for (let i = 0; i < 60; i++) {
      const offset = Math.floor((text.length * (i + 1)) / 62);
      const t0 = performance.now();
      await call("lang.complete", { docId: "bench-complete", text, offset, dialect: "tsql", profileId: profile.id, database: MSSQL.db });
      comp.push(performance.now() - t0);
    }
    record(5, p95(comp.slice(5)), { samples: comp.length });

    // #27 tier-2 completion inside the 10,000-line PL/pgSQL function (PostgreSQL), against the fixture's snapshot
    {
      const pgP = await call("profile.save", { id: "", name: "Bench PG tier 2", engine: "pg", host: PG.host, port: PG.port, auth: "password", username: PG.user, environment: "dev", tlsMode: "prefer", trustCert: false, databaseDefault: PG.db });
      const pgS = await call("session.open", { profileId: pgP.id, database: PG.db, secret: PG.password });
      await call("session.state", { sessionId: pgS.sessionId });
      const pgFresh = until((m, p) => m === "meta.updated" && p.profileId === pgP.id && p.status !== "refreshing", 120_000, "#27 meta.updated");
      await call("meta.refresh", { profileId: pgP.id, database: PG.db, full: true });
      await pgFresh;
      const pl = bigPlpgsql();
      const marks = ["COALESCE(rec.", "max(x.", "v_total := "];
      const at = [];
      for (let i = 0; i < 60; i++) {
        let o = -1;
        for (let k = 0, n = 20 + i * 19; k <= n; k++) o = pl.indexOf(marks[i % 3], o + 1);
        at.push(o + marks[i % 3].length);
      }
      const pgComp = [];
      let items = 0;
      for (const offset of at) {
        const t0 = performance.now();
        const r = await call("lang.complete", { docId: "bench-complete-pg", text: pl, offset, dialect: "pg", profileId: pgP.id, database: PG.db });
        pgComp.push(performance.now() - t0);
        items += r.length;
      }
      record(27, p95(pgComp.slice(5)), { lines: pl.split("\n").length, samples: pgComp.length, itemsPerCall: Math.round(items / at.length) });
    }

    // #8 database switch overhead vs one roundtrip
    const rt = [];
    const sw = [];
    for (let i = 0; i < 10; i++) {
      let t0 = performance.now();
      const ex = await call("query.execute", { sessionId, sql: "SELECT 1", options: { noHistory: true } });
      await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 120_000, undefined, true);
      rt.push(performance.now() - t0);
      t0 = performance.now();
      await call("session.changeDatabase", { sessionId, database: i % 2 ? MSSQL.db : "master" });
      sw.push(performance.now() - t0);
    }
    record(8, Math.max(0, median(sw) - median(rt) / 2), { switchMs: median(sw), executeRoundtripMs: median(rt) });

    // #15 cancel latency
    const cancels = [];
    for (let i = 0; i < RUNS; i++) {
      const ex = await call("query.execute", { sessionId, sql: "WAITFOR DELAY '00:01:00'", options: { noHistory: true } });
      await sleep(300);
      const done = until((m, p) => m === "query.done" && p.executionId === ex.executionId, 120_000, undefined, true);
      const t0 = performance.now();
      await call("query.cancel", { executionId: ex.executionId });
      await done;
      cancels.push(performance.now() - t0);
    }
    record(15, median(cancels), { samples: cancels });

    // #36 message flood (M13): time to query.done and the notifications one 100k-message run costs, per engine.
    {
      const pgProfile = await call("profile.save", { id: "", name: "Bench PG flood", engine: "pg", host: PG.host, port: PG.port, auth: "password", username: PG.user, environment: "dev", tlsMode: "prefer", trustCert: false, databaseDefault: PG.db });
      const pg = await call("session.open", { profileId: pgProfile.id, database: PG.db, secret: PG.password });
      const flood = {};
      for (const [engine, sid, sql] of [
        ["mssql", sessionId, "DECLARE @i int = 0; WHILE @i < 100000 BEGIN PRINT CONCAT('line ', @i); SET @i += 1; END;"],
        ["pg", pg.sessionId, "DO $$ BEGIN FOR i IN 1..100000 LOOP RAISE NOTICE 'line %', i; END LOOP; END $$;"],
      ]) {
        const runs = [];
        for (let r = 0; r < 3; r++) {
          // Counted from before the execute: the first batch may arrive before its reply.
          const counts = new Map();
          const target = { id: null };
          const done = until((m, p) => {
            if (m === "query.messages" || m === "query.message") {
              const c = counts.get(p.executionId) ?? { notifications: 0, messages: 0 };
              c.notifications++;
              c.messages += m === "query.messages" ? p.messages.length : 1;
              counts.set(p.executionId, c);
            }
            return m === "query.done" && p.executionId === target.id;
          }, 120_000, `#36.${engine} done`);
          const t0 = performance.now();
          const ex = await call("query.execute", { sessionId: sid, sql, options: { noHistory: true } });
          target.id = ex.executionId;
          await done;
          runs.push({ ms: performance.now() - t0, ...(counts.get(ex.executionId) ?? { notifications: 0, messages: 0 }) });
        }
        flood[engine] = { ms: Math.round(median(runs.map((x) => x.ms))), notifications: Math.max(...runs.map((x) => x.notifications)), messages: runs[0].messages };
      }
      record(36, Math.max(flood.mssql.ms, flood.pg.ms), flood);
      record("36notify", Math.max(flood.mssql.notifications, flood.pg.notifications), flood);
    }

    // #20 grid edits: 100 staged cells → data.previewPatch (statement build + revalidation)
    {
      const setup = await call("query.execute", { sessionId, options: { noHistory: true }, sql: `
        DROP TABLE IF EXISTS dbo.q_bench_edit; CREATE TABLE dbo.q_bench_edit (id int PRIMARY KEY, v nvarchar(20) NULL, n int NULL);
        INSERT dbo.q_bench_edit (id, v, n) SELECT TOP 100 ROW_NUMBER() OVER (ORDER BY (SELECT 1)), N'x', 1 FROM sys.all_objects;` });
      const setupDone = await until((m, p) => m === "query.done" && p.executionId === setup.executionId, 120_000, undefined, true);
      if (setupDone.state !== "done") throw new Error(`#20 setup ${setupDone.state}: ${setupDone.error?.message}`);
      // Any terminal status: a refresh that ends in "error" never sends "fresh".
      const refreshed2 = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.database === MSSQL.db && p.status !== "refreshing", 120_000, "#20 meta.updated");
      await call("meta.refresh", { profileId: profile.id, database: MSSQL.db, full: false });
      const meta2 = await refreshed2;
      if (meta2.status === "error") throw new Error(`#20 metadata refresh failed: ${meta2.error}`);
      const editInfo = until((m) => m === "query.resultSet", 120_000, "#20 resultSet");
      editInfo.catch(() => {}); // a failed SELECT sends done without a result set; reported below instead
      const sel = await call("query.execute", { sessionId, sql: "SELECT id, v, n FROM dbo.q_bench_edit ORDER BY id", options: { noHistory: true } });
      const selDone = await until((m, p) => m === "query.done" && p.executionId === sel.executionId, 120_000, "#20 select done", true);
      // Notifications arrive in order, so a result set for this SELECT has already been delivered by now.
      const rsEvent = await Promise.race([editInfo, Promise.resolve(null)]);
      if (selDone.state !== "done" || !rsEvent) throw new Error(`#20 select ${selDone.state}: ${selDone.error?.message ?? "no result set"}`);
      const v = (t) => ({ isNull: false, useDefault: false, text: t });
      const updates = Array.from({ length: 100 }, (_, i) => ({ rowIndex: i, key: [v(String(i + 1))], changes: { v: v(`y${i}`) }, original: { v: v("x") }, token: null }));
      const prev = [];
      for (let i = 0; i < RUNS + 2; i++) {
        const t0 = performance.now();
        await call("data.previewPatch", { sessionId, executionId: sel.executionId, resultIndex: 0, updates, inserts: [], deletes: [] });
        prev.push(performance.now() - t0);
      }
      record(20, median(prev.slice(2)), { editable: rsEvent.edit?.editable, samples: prev });
      const drop = await call("query.execute", { sessionId, sql: "DROP TABLE dbo.q_bench_edit", options: { noHistory: true } });
      await until((m, p) => m === "query.done" && p.executionId === drop.executionId, 120_000, undefined, true);
    }

    // #23 import: 100k × 10 CSV into a new table, per engine (the bench injects the path as the shell would)
    {
      const csv = path.join(mkdtempSync(path.join(os.tmpdir(), "qbench-import-")), "bench.csv");
      const lines = ["id,name,qty,price,ok,born,at,code,note,ratio"];
      for (let i = 1; i <= 100_000; i++) lines.push(`${i},name ${i},${i % 100},${i % 1000}.25,${i % 2 ? "true" : "false"},2026-01-${String(1 + (i % 28)).padStart(2, "0")},2026-10-21 10:${String(i % 60).padStart(2, "0")}:00,C${i},"note, ${i}",0.${i % 10}`);
      writeFileSync(csv, lines.join("\n") + "\n");
      const pgProfile = await call("profile.save", { id: "", name: "Bench PG", engine: "pg", host: PG.host, port: PG.port, auth: "password", username: PG.user, environment: "dev", tlsMode: "prefer", trustCert: false, databaseDefault: PG.db });
      const pg = await call("session.open", { profileId: pgProfile.id, database: PG.db, secret: PG.password });
      const perEngine = {};
      for (const [engine, sid, schema] of [["mssql", sessionId, "dbo"], ["pg", pg.sessionId, "public"]]) {
        const run = async (sql) => {
          const ex = await call("query.execute", { sessionId: sid, sql, options: { noHistory: true } });
          await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 120_000, undefined, true);
        };
        await run(`DROP TABLE IF EXISTS ${schema}.q_bench_import`);
        const ins = await call("import.inspect", { fileToken: `bench-${engine}`, filePath: csv });
        const target = await call("import.checkTarget", { sessionId: sid, inspectionId: ins.inspectionId, schema, table: "q_bench_import", newTable: true, primaryKey: ["id"] });
        await run(target.createTableSql);
        const t0 = performance.now();
        const job = await call("import.run", { sessionId: sid, inspectionId: ins.inspectionId, fileToken: `bench-${engine}`, filePath: csv, schema, table: "q_bench_import" });
        const done = await until((m, p) => m === "import.progress" && p.jobId === job.jobId && p.done, 120_000, undefined, true);
        perEngine[engine] = { ms: Math.round(performance.now() - t0), rows: done.rowsLoaded, error: done.error ?? null };
        await run(`DROP TABLE ${schema}.q_bench_import`);
      }
      record(23, Math.max(perEngine.mssql.ms, perEngine.pg.ms), perEngine);

      // #21 schema compare: both sides read in parallel on two sessions, compared, kept for the script.
      const sides = {};
      for (const [engine, pid, db, secret] of [["mssql", profile.id, MSSQL.db, MSSQL.password], ["pg", pgProfile.id, PG.db, PG.password]]) {
        const a = await call("session.open", { profileId: pid, database: db, secret });
        const b = await call("session.open", { profileId: pid, database: db, secret });
        await call("schemaCompare.run", { source: { sessionId: a.sessionId, database: db }, target: { sessionId: b.sessionId, database: db } }); // warm-up
        const runs = [];
        let items = 0;
        for (let i = 0; i < 3; i++) {
          const t0 = performance.now();
          const r = await call("schemaCompare.run", { source: { sessionId: a.sessionId, database: db }, target: { sessionId: b.sessionId, database: db } });
          runs.push(performance.now() - t0);
          items = r.items.length;
        }
        sides[engine] = { ms: Math.round(median(runs)), objects: items };
      }
      record(21, Math.max(sides.mssql.ms, sides.pg.ms), sides);

      // #22 data compare: 100k × 10 against a copy with 1,000 changed rows; sidecar memory delta ≤ 64 MB.
      const dc = {};
      const SQL22 = {
        mssql: `DROP TABLE IF EXISTS dbo.q_bench_dc_a; DROP TABLE IF EXISTS dbo.q_bench_dc_b;
          CREATE TABLE dbo.q_bench_dc_a (id int PRIMARY KEY, name nvarchar(40), qty int, price decimal(10,2), ok bit, born date, at datetime2(0), code varchar(12), note nvarchar(60), ratio float);
          WITH n AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
          INSERT dbo.q_bench_dc_a SELECT i, CONCAT(N'name ', i), i % 100, (i % 1000) + 0.25, i % 2, DATEADD(day, i % 365, '2026-01-01'), DATEADD(minute, i, '2026-01-01'), CONCAT('C', i), CONCAT(N'note ', i), (i % 10) / 10.0 FROM n;
          SELECT * INTO dbo.q_bench_dc_b FROM dbo.q_bench_dc_a; ALTER TABLE dbo.q_bench_dc_b ADD PRIMARY KEY (id);
          UPDATE dbo.q_bench_dc_b SET name = name + N'!' WHERE id % 100 = 0;`,
        pg: `DROP TABLE IF EXISTS q_bench_dc_a, q_bench_dc_b;
          CREATE TABLE q_bench_dc_a (id int PRIMARY KEY, name text, qty int, price numeric(10,2), ok boolean, born date, at timestamp(0), code varchar(12), note text, ratio float8);
          INSERT INTO q_bench_dc_a SELECT i, 'name ' || i, i % 100, (i % 1000) + 0.25, i % 2 = 1, date '2026-01-01' + (i % 365), timestamp '2026-01-01' + i * interval '1 minute', 'C' || i, 'note ' || i, (i % 10) / 10.0 FROM generate_series(1, 100000) i;
          CREATE TABLE q_bench_dc_b (LIKE q_bench_dc_a INCLUDING ALL); INSERT INTO q_bench_dc_b SELECT * FROM q_bench_dc_a;
          UPDATE q_bench_dc_b SET name = name || '!' WHERE id % 100 = 0;`,
      };
      for (const [engine, pid, db, secret, schema] of [["mssql", profile.id, MSSQL.db, MSSQL.password, "dbo"], ["pg", pgProfile.id, PG.db, PG.password, "public"]]) {
        const sess = await call("session.open", { profileId: pid, database: db, secret });
        const ex = await call("query.execute", { sessionId: sess.sessionId, sql: SQL22[engine], options: { noHistory: true } });
        await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 300_000, undefined, true);
        const refreshed = until((m, p) => m === "meta.updated" && p.profileId === pid && p.database === db && p.status !== "refreshing", 120_000);
        await call("meta.refresh", { profileId: pid, database: db, full: false });
        await refreshed;
        const side = (table) => ({ sessionId: sess.sessionId, database: db, schema, table });
        const runs = [];
        let peak = 0;
        let rows = 0;
        for (let i = 0; i < 3; i++) {
          const before = rssMb(sc.child.pid);
          const timer = setInterval(() => { peak = Math.max(peak, rssMb(sc.child.pid) - before); }, 20);
          const t0 = performance.now();
          const st = await call("dataCompare.start", { source: side("q_bench_dc_a"), target: side("q_bench_dc_b") });
          const done = await until((m, p) => m === "dataCompare.progress" && p.compareId === st.compareId && p.done, 120_000, undefined, true);
          runs.push(performance.now() - t0);
          clearInterval(timer);
          rows = done.different;
          await call("query.close", { executionId: st.executionId }).catch(() => {});
        }
        dc[engine] = { ms: Math.round(median(runs)), differences: rows, memoryDeltaMb: Math.round(peak) };
        const drop = await call("query.execute", { sessionId: sess.sessionId, sql: engine === "mssql" ? "DROP TABLE dbo.q_bench_dc_a; DROP TABLE dbo.q_bench_dc_b;" : "DROP TABLE q_bench_dc_a, q_bench_dc_b;", options: { noHistory: true } });
        await until((m, p) => m === "query.done" && p.executionId === drop.executionId, 120_000, undefined, true);
      }
      record(22, Math.max(dc.mssql.ms, dc.pg.ms), { ...dc, memoryGatePass: Math.max(dc.mssql.memoryDeltaMb, dc.pg.memoryDeltaMb) <= 64 });

      // #24 find usages: a column every module mentions (id), so all ~550 cached modules are parsed, + 5 open tabs.
      // Definitions are cached by a first search; each timed search is for a new target, so no scan is reused.
      {
        const refreshed = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.database === MSSQL.db && p.status !== "refreshing", 120_000);
        await call("meta.refresh", { profileId: profile.id, database: MSSQL.db, full: false });
        await refreshed;
        // Every tab names every target table, so all five are parsed on every search.
        const tab = Array.from({ length: 200 }, (_, i) => `SELECT o.*, ${i} AS n FROM dbo.t${(i % 8) + 1} o WHERE o.id > ${i};`).join("\n");
        const tabs = Array.from({ length: 5 }, (_, i) => ({ tabId: `bench${i}`, title: `Bench${i}.sql`, text: tab }));
        const args = (n) => ({ sessionId, database: MSSQL.db, target: { kind: "column", schema: "dbo", name: `t${n}`, column: "id" }, tabs });
        await call("refactor.findUsages", args(1)); // fills definition_cache
        const cold = [];
        let last;
        for (let i = 2; i <= 8; i++) {
          const t0 = performance.now();
          last = await call("refactor.findUsages", args(i));
          cold.push(performance.now() - t0);
        }
        const warm = [];
        for (let i = 0; i < 5; i++) {
          const t0 = performance.now();
          await call("refactor.findUsages", args(8));
          warm.push(performance.now() - t0);
        }
        record(24, median(cold), { objectsScanned: last.objectsScanned, hits: last.hits.length, repeatedSearchMs: Math.round(median(warm) * 10) / 10 });
      }
    }

    // data plane throughput (sidecar side of #11): 1M × 40 synthetic
    const t0 = performance.now();
    const syn = await call("bench.synthetic", { rows: 1_000_000, columns: 40 });
    await until((m, p) => m === "query.done" && p.executionId === syn.executionId, 120_000, undefined, true);
    await sleep(200);
    const f = [...sc.frames.values()].find((x) => x.batches >= 100);
    results.dataPlane1MRowsMs = Math.round((f?.endAt ?? performance.now()) - t0);
    results.dataPlaneFirstBatchMs = Math.round((f?.firstAt ?? 0) - t0);
    console.log(`  data plane: 1M×40 streamed in ${results.dataPlane1MRowsMs} ms (first batch ${results.dataPlaneFirstBatchMs} ms)`);

    // real 1M-row 40-column query from the fixture (server + encode)
    const t1 = performance.now();
    const big = await call("query.execute", { sessionId, sql: bigResultQuery("mssql"), options: { maxRows: 0, noHistory: true } });
    const bigDone = await until((m, p) => m === "query.done" && p.executionId === big.executionId, 300_000, undefined, true);
    results.real1MRowsMs = Math.round(performance.now() - t1);
    console.log(`  real 1M×40 query: ${results.real1MRowsMs} ms, state ${bigDone.state}`);

    // #11cold: #11 runs a synthetic execution and never opens a connection; this is the first execute of a session.
    const cold = { mssql: await coldFirstBatch(sc, "mssql", 15), pg: await coldFirstBatch(sc, "pg", 15) };
    record("11cold", Math.max(median(cold.mssql), median(cold.pg)), {
      mssqlMedian: Math.round(median(cold.mssql) * 10) / 10, mssqlP95: Math.round(p95(cold.mssql) * 10) / 10,
      pgMedian: Math.round(median(cold.pg) * 10) / 10, pgP95: Math.round(p95(cold.pg) * 10) / 10, samplesPerEngine: 15,
    });
  } finally {
    sc.kill();
  }
  await orphanCheck();
}

/**
 * #11cold: execute → first batch on a cold pool connection. Each sample is the first execute of a session on a new profile,
 * so it opens a physical connection (the drivers do not pool; Core does, §6): TCP, TLS, login, and the identity read every
 * physical open has done since Phase B (B1b) — then SELECT 1. Returns one time per sample, in ms.
 */
async function coldFirstBatch({ call, until, frames }, engine, n) {
  const cfg = engine === "mssql" ? MSSQL : PG;
  const times = [];
  for (let i = 0; i < n; i++) {
    const profile = await call("profile.save", engine === "mssql"
      ? { id: "", name: `Bench cold ${i}`, engine, host: cfg.host, port: cfg.port, auth: "sql", username: cfg.user, environment: "dev", tlsMode: "mandatory", trustCert: true, databaseDefault: cfg.db }
      : { id: "", name: `Bench cold ${i}`, engine, host: cfg.host, port: cfg.port, auth: "password", username: cfg.user, environment: "dev", tlsMode: "prefer", trustCert: false, databaseDefault: cfg.db });
    const { sessionId } = await call("session.open", { profileId: profile.id, database: cfg.db, secret: cfg.password });
    const before = new Set(frames.keys());
    const t0 = performance.now();
    const ex = await call("query.execute", { sessionId, sql: "SELECT 1", options: { noHistory: true } });
    await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 30_000, "cold SELECT 1", true);
    // The batch frame (data socket) and query.done (stdout) travel separately: wait briefly for the frame.
    let f;
    for (let w = 0; w < 100 && !(f = [...frames.entries()].find(([k, v]) => !before.has(k) && v.firstAt)?.[1]); w++) await sleep(10);
    if (!f) throw new Error(`#11cold: no batch frame for the ${engine} SELECT 1`);
    times.push(f.firstAt - t0);
    await call("session.close", { sessionId, force: true });
    await call("profile.delete", { profileId: profile.id });
  }
  return times;
}

/**
 * Phase D1 per-engine metrics for SQLite (exposed with QUERITHM_DEV_ENGINES; the bench plays the shell and injects the
 * file grant itself). An empty file is a valid empty SQLite database; the schema is created through the sidecar.
 */
/** The #23 file: 100k × 10 CSV, the same rows for every engine. */
function writeImportCsv(dir) {
  const csv = path.join(dir, "bench.csv");
  const lines = ["id,name,qty,price,ok,born,at,code,note,ratio"];
  for (let i = 1; i <= 100_000; i++) lines.push(`${i},name ${i},${i % 100},${i % 1000}.25,${i % 2 ? "true" : "false"},2026-01-${String(1 + (i % 28)).padStart(2, "0")},2026-10-21 10:${String(i % 60).padStart(2, "0")}:00,C${i},"note, ${i}",0.${i % 10}`);
  writeFileSync(csv, lines.join("\n") + "\n");
  return csv;
}

/**
 * #23 per engine: the CSV into a new table q_bench_import (median of 3; the table is dropped before each run). `dropped`
 * waits until the DROP reached the snapshot import.checkTarget reads (untimed), for engines whose DDL refresh can land late.
 */
async function importMetric({ call, until }, run, sessionId, { engine, schema, csv, dropped }) {
  const samples = [];
  let rows = 0;
  for (let r = 0; r < 3; r++) {
    await run(sessionId, `DROP TABLE IF EXISTS ${schema}.q_bench_import`);
    await dropped?.("q_bench_import");
    const ins = await call("import.inspect", { fileToken: `bench-${engine}-${r}`, filePath: csv });
    const target = await call("import.checkTarget", { sessionId, inspectionId: ins.inspectionId, schema, table: "q_bench_import", newTable: true, primaryKey: ["id"] });
    await run(sessionId, target.createTableSql);
    const t0 = performance.now();
    const job = await call("import.run", { sessionId, inspectionId: ins.inspectionId, fileToken: `bench-${engine}-${r}`, filePath: csv, schema, table: "q_bench_import" });
    const done = await until((m, p) => m === "import.progress" && p.jobId === job.jobId && p.done, 300_000, `${engine} import`, true);
    if (done.error) throw new Error(`#23.${engine}: ${done.error}`);
    samples.push(performance.now() - t0);
    rows = done.rowsLoaded;
  }
  await run(sessionId, `DROP TABLE IF EXISTS ${schema}.q_bench_import`);
  return { ms: median(samples), rows, samples: samples.map((x) => Math.round(x)) };
}

/**
 * #20 per engine: 100 staged cell edits of a 100-row keyed table (q_bench_edit: id key, v = 'x') → data.previewPatch
 * (statement build + revalidation), median after two warm-ups. `refresh` brings the new table into the snapshot.
 */
async function previewPatchMetric({ call, until }, run, sessionId, { engine, setup, select, refresh }) {
  for (const sql of setup) {
    const d = await run(sessionId, sql);
    if (d.error) throw new Error(`#20.${engine} setup: ${d.error.message}`);
  }
  await refresh();
  const editInfo = until((m) => m === "query.resultSet", 120_000, `#20.${engine} resultSet`);
  editInfo.catch(() => {}); // a failed SELECT sends done without a result set; reported below instead
  const sel = await call("query.execute", { sessionId, sql: select, options: { noHistory: true } });
  const selDone = await until((m, p) => m === "query.done" && p.executionId === sel.executionId, 120_000, `#20.${engine} select`, true);
  const rs = await Promise.race([editInfo, Promise.resolve(null)]);
  if (selDone.state !== "done" || !rs) throw new Error(`#20.${engine} select ${selDone.state}: ${selDone.error?.message ?? "no result set"}`);
  if (!rs.edit?.editable) throw new Error(`#20.${engine}: the result is read-only: ${JSON.stringify(rs.edit)}`);
  const v = (t) => ({ isNull: false, useDefault: false, text: t });
  const updates = Array.from({ length: 100 }, (_, i) => ({ rowIndex: i, key: [v(String(i + 1))], changes: { v: v(`y${i}`) }, original: { v: v("x") }, token: null }));
  const samples = [];
  for (let i = 0; i < RUNS + 2; i++) {
    const t0 = performance.now();
    await call("data.previewPatch", { sessionId, executionId: sel.executionId, resultIndex: 0, updates, inserts: [], deletes: [] });
    samples.push(performance.now() - t0);
  }
  await call("query.close", { executionId: sel.executionId }).catch(() => {});
  return { ms: median(samples.slice(2)), samples };
}

/**
 * #5 per engine: tier-2 completion in a 10,000-line script of joins over the 600-table fixture — `lang.complete` roundtrip
 * after `a.` at 60 places spread over the script (first 5 dropped), against the session's snapshot.
 */
async function tier2Metric({ call }, { dialect, profileId, database, sessionId }) {
  const lines = Array.from({ length: 10_000 }, (_, i) => `SELECT a.id, a.c0, b.c1 FROM t${(i % 599) + 1} a JOIN t${(i % 599) + 2} b ON b.id = a.id WHERE a.c2 = 'x${i}';`);
  const text = lines.join("\n");
  const at = Array.from({ length: 60 }, (_, i) => text.indexOf("SELECT a.", Math.floor((text.length * i) / 60)) + "SELECT a.".length);
  const samples = [];
  let items = 0;
  for (const offset of at) {
    const t0 = performance.now();
    const r = await call("lang.complete", { docId: `bench-tier2-${dialect}`, text, offset, dialect, profileId, database, sessionId });
    samples.push(performance.now() - t0);
    items += r.length;
  }
  if (items === 0) throw new Error(`#5.${dialect}: tier 2 offered no columns`);
  return { p95: p95(samples.slice(5)), lines: lines.length, itemsPerCall: Math.round(items / at.length), samples: samples.length };
}

async function sqliteMetrics() {
  console.log("sqlite metrics");
  const { identityOf } = await import("../dev-bridge-grants.mjs");
  const sc = await startSidecar({ QUERITHM_DEV_ENGINES: "sqlite" });
  const { call, until, frames } = sc;
  const dir = mkdtempSync(path.join(os.tmpdir(), "qbench-sqlite-"));
  const newFile = (name) => {
    const p = path.join(dir, name);
    writeFileSync(p, "");
    return p;
  };
  const open = async (file, grantId) => {
    const profile = await call("profile.save", { id: "", name: path.basename(file), engine: "sqlite", auth: "none", location: "file", file: { grantId, displayName: path.basename(file) }, environment: "dev" });
    const { sessionId } = await call("session.open", { profileId: profile.id, fileGrant: { grantId, path: file, mode: "readWrite", identity: identityOf(file) } });
    return { profile, sessionId };
  };
  const run = async (sessionId, sql) => {
    const ex = await call("query.execute", { sessionId, sql, options: { noHistory: true } });
    return until((m, p) => m === "query.done" && p.executionId === ex.executionId, 120_000, "sqlite run", true);
  };
  try {
    // #11cold.sqlite: each sample a new tab on a new file: open, hardening, SELECT 1 → first batch frame.
    const cold = [];
    for (let i = 0; i < 15; i++) {
      const { profile, sessionId } = await open(newFile(`cold${i}.db`), `cold${i}`);
      const before = new Set(frames.keys());
      const t0 = performance.now();
      const ex = await call("query.execute", { sessionId, sql: "SELECT 1", options: { noHistory: true } });
      await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 30_000, "sqlite cold", true);
      let f;
      for (let w = 0; w < 100 && !(f = [...frames.entries()].find(([k, v]) => !before.has(k) && v.firstAt)?.[1]); w++) await sleep(10);
      if (!f) throw new Error("#11cold.sqlite: no batch frame");
      cold.push(f.firstAt - t0);
      await call("session.close", { sessionId, force: true });
      await call("profile.delete", { profileId: profile.id });
    }
    record("11cold.sqlite", median(cold), { samples: cold.map((x) => Math.round(x * 10) / 10) });

    // The schema for #9 / #10: 600 tables × 15 columns, 150 views, 200 triggers.
    const { profile, sessionId } = await open(newFile("fixture.db"), "fixture");
    const ddl = ["BEGIN;"];
    for (let t = 1; t <= 600; t++) ddl.push(`CREATE TABLE t${t} (id INTEGER PRIMARY KEY, ${Array.from({ length: 14 }, (_, c) => `c${c} TEXT`).join(", ")});`);
    for (let v = 1; v <= 150; v++) ddl.push(`CREATE VIEW v${v} AS SELECT id, c0 FROM t${v} WHERE id > ${v};`);
    for (let g = 1; g <= 200; g++) ddl.push(`CREATE TRIGGER tr${g} AFTER INSERT ON t${g} BEGIN UPDATE t${g} SET c1 = 'x' WHERE id = NEW.id; END;`);
    ddl.push("COMMIT;");
    const made = await run(sessionId, ddl.join("\n"));
    if (made.error) throw new Error(`sqlite fixture: ${made.error.message}`);
    const refreshed = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status === "fresh" && p.full, 120_000, "sqlite full refresh");
    await call("meta.refresh", { profileId: profile.id, database: "main", full: true, sessionId });
    await refreshed;
    const snaps = [];
    let objects = 0;
    for (let i = 0; i < RUNS; i++) {
      const t0 = performance.now();
      const s = await call("meta.snapshot", { profileId: profile.id, database: "main", sessionId });
      snaps.push(performance.now() - t0);
      objects = s.objects.length;
    }
    record("9.sqlite", median(snaps), { objects, samples: snaps });

    const incr = [];
    for (let r = 0; r < RUNS; r++) {
      const changes = Array.from({ length: 10 }, (_, k) => `DROP VIEW v${k + 1}; CREATE VIEW v${k + 1} AS SELECT id, c0, c1 FROM t${k + 1} /* rev ${r} */;`).join("\n");
      await run(sessionId, changes);
      await sleep(300); // let the DDL-triggered refresh settle first
      const done = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status === "fresh", 60_000, "sqlite incremental");
      const t0 = performance.now();
      await call("meta.refresh", { profileId: profile.id, database: "main", sessionId });
      await done;
      incr.push(performance.now() - t0);
    }
    record("10.sqlite", median(incr), { samples: incr });

    // #15.sqlite: cancel a CPU-bound statement after 200 ms; query.cancel → query.done(cancelled).
    const cancels = [];
    for (let i = 0; i < RUNS; i++) {
      const ex = await call("query.execute", { sessionId, sql: "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT count(*) FROM n", options: { noHistory: true } });
      await sleep(200);
      const t0 = performance.now();
      await call("query.cancel", { executionId: ex.executionId });
      const d = await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 30_000, "sqlite cancel", true);
      if (d.state !== "cancelled") throw new Error(`#15.sqlite: state ${d.state}`);
      cancels.push(performance.now() - t0);
    }
    record("15.sqlite", median(cancels), { samples: cancels });

    const t2 = await tier2Metric(sc, { dialect: "sqlite", profileId: profile.id, database: "main", sessionId });
    record("5.sqlite", t2.p95, t2);
    const refresh = async () => {
      const done = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status !== "refreshing", 60_000, "sqlite #20 refresh");
      await call("meta.refresh", { profileId: profile.id, database: "main", sessionId });
      await done;
    };
    const edit = await previewPatchMetric(sc, run, sessionId, {
      engine: "sqlite", refresh, select: "SELECT id, v, n FROM q_bench_edit ORDER BY id",
      setup: ["DROP TABLE IF EXISTS q_bench_edit", "CREATE TABLE q_bench_edit (id INTEGER PRIMARY KEY, v TEXT, n INTEGER); WITH RECURSIVE k(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM k WHERE i < 100) INSERT INTO q_bench_edit SELECT i, 'x', 1 FROM k;"],
    });
    record("20.sqlite", edit.ms, { samples: edit.samples });
    const imp = await importMetric(sc, run, sessionId, { engine: "sqlite", schema: "main", csv: writeImportCsv(dir) });
    record("23.sqlite", imp.ms, { rows: imp.rows, samples: imp.samples });
  } finally {
    sc.kill();
  }
}

/**
 * Phase D3 per-engine metrics for DuckDB (exposed with QUERITHM_DEV_ENGINES; the bench plays the shell and injects the
 * file grants itself). An empty file with writes allowed becomes a new database (DuckDbInstances), so files are made empty.
 */
async function duckdbMetrics() {
  console.log("duckdb metrics");
  const { identityOf } = await import("../dev-bridge-grants.mjs");
  const sc = await startSidecar({ QUERITHM_DEV_ENGINES: "duckDb" });
  const { call, until, frames } = sc;
  const dir = mkdtempSync(path.join(os.tmpdir(), "qbench-duckdb-"));
  const newFile = (name) => {
    const p = path.join(dir, name);
    writeFileSync(p, "");
    return p;
  };
  const open = async (file, grantId) => {
    const profile = await call("profile.save", { id: "", name: path.basename(file), engine: "duckDb", auth: "none", location: "file", file: { grantId, displayName: path.basename(file) }, environment: "dev" });
    const { sessionId } = await call("session.open", { profileId: profile.id, fileGrant: { grantId, path: file, mode: "readWrite", identity: identityOf(file) } });
    return { profile, sessionId };
  };
  const run = async (sessionId, sql) => {
    const ex = await call("query.execute", { sessionId, sql, options: { noHistory: true } });
    return until((m, p) => m === "query.done" && p.executionId === ex.executionId, 120_000, "duckdb run", true);
  };
  try {
    // #11cold.duckdb: each sample a new tab on a new (empty) file: create, open, policy, SELECT 1 → first batch frame.
    const cold = [];
    for (let i = 0; i < 15; i++) {
      const { profile, sessionId } = await open(newFile(`cold${i}.duckdb`), `cold${i}`);
      const before = new Set(frames.keys());
      const t0 = performance.now();
      const ex = await call("query.execute", { sessionId, sql: "SELECT 1", options: { noHistory: true } });
      const d = await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 30_000, "duckdb cold", true);
      if (d.error) throw new Error(`#11cold.duckdb: ${d.error.message}`);
      let f;
      for (let w = 0; w < 100 && !(f = [...frames.entries()].find(([k, v]) => !before.has(k) && v.firstAt)?.[1]); w++) await sleep(10);
      if (!f) throw new Error("#11cold.duckdb: no batch frame");
      cold.push(f.firstAt - t0);
      await call("session.close", { sessionId, force: true });
      await call("profile.delete", { profileId: profile.id });
    }
    record("11cold.duckdb", median(cold), { samples: cold.map((x) => Math.round(x * 10) / 10) });

    // The schema for #9 / #10: 600 tables × 15 columns, 150 views (DuckDB has no triggers). The catalog is the file's name.
    const { profile, sessionId } = await open(newFile("fixture.duckdb"), "fixture");
    const ddl = ["BEGIN TRANSACTION;"];
    for (let t = 1; t <= 600; t++) ddl.push(`CREATE TABLE t${t} (id INTEGER PRIMARY KEY, ${Array.from({ length: 14 }, (_, c) => `c${c} VARCHAR`).join(", ")});`);
    for (let v = 1; v <= 150; v++) ddl.push(`CREATE VIEW v${v} AS SELECT id, c0 FROM t${v} WHERE id > ${v};`);
    ddl.push("COMMIT;");
    const made = await run(sessionId, ddl.join("\n"));
    if (made.error) throw new Error(`duckdb fixture: ${made.error.message}`);
    const refreshed = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status === "fresh" && p.full, 120_000, "duckdb full refresh");
    await call("meta.refresh", { profileId: profile.id, database: "fixture", full: true, sessionId });
    await refreshed;
    const snaps = [];
    let objects = 0;
    for (let i = 0; i < RUNS; i++) {
      const t0 = performance.now();
      const s = await call("meta.snapshot", { profileId: profile.id, database: "fixture", sessionId });
      snaps.push(performance.now() - t0);
      objects = s.objects.length;
    }
    record("9.duckdb", median(snaps), { objects, samples: snaps });

    const incr = [];
    for (let r = 0; r < RUNS; r++) {
      const changes = Array.from({ length: 10 }, (_, k) => `CREATE OR REPLACE VIEW v${k + 1} AS SELECT id, c0, c1 FROM t${k + 1} /* rev ${r} */;`).join("\n");
      await run(sessionId, changes);
      await sleep(300); // let the DDL-triggered refresh settle first
      const done = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status === "fresh", 60_000, "duckdb incremental");
      const t0 = performance.now();
      await call("meta.refresh", { profileId: profile.id, database: "fixture", sessionId });
      await done;
      incr.push(performance.now() - t0);
    }
    record("10.duckdb", median(incr), { samples: incr });

    // #15.duckdb: cancel a CPU-bound statement after 200 ms; query.cancel → query.done(cancelled).
    const cancels = [];
    for (let i = 0; i < RUNS; i++) {
      const ex = await call("query.execute", { sessionId, sql: "SELECT count(*) FROM range(100000000000)", options: { noHistory: true } });
      await sleep(200);
      const t0 = performance.now();
      await call("query.cancel", { executionId: ex.executionId });
      const d = await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 30_000, "duckdb cancel", true);
      if (d.state !== "cancelled") throw new Error(`#15.duckdb: state ${d.state}`);
      cancels.push(performance.now() - t0);
    }
    record("15.duckdb", median(cancels), { samples: cancels });

    // #23.duckdb: 100k × 10 CSV into a new table through the appender (batched INSERTs took 24 s here: bench/results 2026-10-31-13-07).
    const imp = await importMetric(sc, run, sessionId, { engine: "duckdb", schema: "main", csv: writeImportCsv(dir) });
    record("23.duckdb", imp.ms, { rows: imp.rows, samples: imp.samples });

    const t2 = await tier2Metric(sc, { dialect: "duckDb", profileId: profile.id, database: "fixture", sessionId });
    record("5.duckdb", t2.p95, t2);
    const refresh = async () => {
      const done = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status !== "refreshing", 60_000, "duckdb #20 refresh");
      await call("meta.refresh", { profileId: profile.id, database: "fixture", sessionId });
      await done;
    };
    const edit = await previewPatchMetric(sc, run, sessionId, {
      engine: "duckdb", refresh, select: "SELECT id, v, n FROM q_bench_edit ORDER BY id",
      setup: ["DROP TABLE IF EXISTS q_bench_edit", "CREATE TABLE q_bench_edit (id INTEGER PRIMARY KEY, v VARCHAR, n INTEGER); INSERT INTO q_bench_edit SELECT i, 'x', 1 FROM range(1, 101) t(i);"],
    });
    record("20.duckdb", edit.ms, { samples: edit.samples });
  } finally {
    sc.kill();
  }
}

/**
 * Phase D4 per-engine metrics for Oracle (exposed with QUERITHM_DEV_ENGINES), on the Docker fixture's Oracle Free
 * (`docker compose -p querithm-fixture -f scripts/bench/docker/compose.yml --profile oracle up -d --wait oracle`).
 * #11b.oracle: each sample a new profile, so a new physical connection (TCP, login, the product and identity reads every
 * open does), then `SELECT 1 FROM dual` to its first batch frame.
 */
async function oracleMetrics() {
  console.log("oracle metrics");
  const sc = await startSidecar({ QUERITHM_DEV_ENGINES: "oracle" });
  const { call, until, frames } = sc;
  const profileOf = (name) => call("profile.save", { id: "", name, engine: "oracle", host: ORACLE.host, port: ORACLE.port, auth: "password", username: ORACLE.user, environment: "dev", tlsMode: "disable", databaseDefault: ORACLE.service });
  try {
    const cold = [];
    for (let i = 0; i < 15; i++) {
      const profile = await profileOf(`Bench Oracle cold ${i}`);
      const { sessionId } = await call("session.open", { profileId: profile.id, secret: ORACLE.password });
      const before = new Set(frames.keys());
      const t0 = performance.now();
      const ex = await call("query.execute", { sessionId, sql: "SELECT 1 FROM dual", options: { noHistory: true } });
      const done = await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 60_000, "oracle cold", true);
      if (done.error) throw new Error(`#11b.oracle: ${done.error.message}`);
      let f;
      for (let w = 0; w < 100 && !(f = [...frames.entries()].find(([k, v]) => !before.has(k) && v.firstAt)?.[1]); w++) await sleep(10);
      if (!f) throw new Error("#11b.oracle: no batch frame");
      cold.push(f.firstAt - t0);
      await call("session.close", { sessionId, force: true });
      await call("profile.delete", { profileId: profile.id });
    }
    record("11b.oracle", median(cold), { samples: cold.map((x) => Math.round(x * 10) / 10) });

    // #9.oracle / #10.oracle: the MySQL schema shape in the app user's own schema (600 tables × 15 columns, 150 views, 200
    // triggers), made in one PL/SQL block (DDL commits on its own) and dropped again at the end, so the fixture keeps no bench
    // objects. The refresh runs as the least-privileged user: every read goes through the ALL_* views.
    const profile = await profileOf("Bench Oracle");
    const { sessionId } = await call("session.open", { profileId: profile.id, secret: ORACLE.password });
    const run = async (sql) => {
      const ex = await call("query.execute", { sessionId, sql, options: { noHistory: true } });
      const done = await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 900_000, "oracle run", true);
      if (done.error) throw new Error(`oracle bench schema: ${done.error.message}`);
      return done;
    };
    const dropAll = `BEGIN
  FOR o IN (SELECT object_name, object_type FROM user_objects WHERE SUBSTR(object_name, 1, 3) = 'QB_' AND object_type IN ('VIEW', 'TABLE') ORDER BY CASE object_type WHEN 'VIEW' THEN 0 ELSE 1 END) LOOP
    EXECUTE IMMEDIATE 'DROP ' || o.object_type || ' ' || o.object_name || CASE o.object_type WHEN 'TABLE' THEN ' CASCADE CONSTRAINTS PURGE' END;
  END LOOP;
END;
/`;
    await run(dropAll);
    const cols = Array.from({ length: 14 }, (_, c) => `c${c} VARCHAR2(40)`).join(", ");
    await run(`BEGIN
  FOR t IN 1 .. 600 LOOP EXECUTE IMMEDIATE 'CREATE TABLE qb_t' || t || ' (id NUMBER PRIMARY KEY, ${cols})'; END LOOP;
  FOR v IN 1 .. 150 LOOP EXECUTE IMMEDIATE 'CREATE VIEW qb_v' || v || ' AS SELECT id, c0 FROM qb_t' || v || ' WHERE id > ' || v; END LOOP;
  FOR g IN 1 .. 200 LOOP EXECUTE IMMEDIATE 'CREATE TRIGGER qb_tr' || g || ' BEFORE INSERT ON qb_t' || g || ' FOR EACH ROW BEGIN :NEW.c0 := :NEW.c1; END;'; END LOOP;
END;
/`);
    try {
      const refreshed = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status === "fresh" && p.full, 900_000, "oracle full refresh");
      await call("meta.refresh", { profileId: profile.id, database: ORACLE.service, full: true });
      await refreshed;
      const snaps = [];
      const statuses = [];
      let objects = 0;
      for (let i = 0; i < RUNS; i++) {
        const t0 = performance.now();
        const s = await call("meta.snapshot", { profileId: profile.id, database: ORACLE.service });
        snaps.push(performance.now() - t0);
        statuses.push(s.status);
        objects = s.objects.length;
      }
      record("9.oracle", median(snaps), { objects, samples: snaps, statuses });

      const incr = [];
      for (let r = 0; r < RUNS; r++) {
        const changes = Array.from({ length: 10 }, (_, k) => `CREATE OR REPLACE VIEW qb_v${k + 1} AS SELECT id, c0, c1 FROM qb_t${k + 1} WHERE id > ${r};`).join("\n");
        await run(changes);
        await sleep(300); // let the DDL-triggered refresh settle first
        const done = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status === "fresh", 120_000, "oracle incremental");
        const t0 = performance.now();
        await call("meta.refresh", { profileId: profile.id, database: ORACLE.service });
        const e = await done;
        incr.push(performance.now() - t0);
        if (e.changed < 10) throw new Error(`#10.oracle: the refresh saw ${e.changed} changed objects, not 10`);
      }
      record("10.oracle", median(incr), { samples: incr.map((x) => Math.round(x)) });
    } finally {
      await run(dropAll);
    }
  } catch (e) {
    console.log(`  oracle metrics failed: ${e.message}`);
    throw e;
  } finally {
    sc.kill();
  }
}

/**
 * Phase D2 per-engine metrics for MySQL 8.4 (exposed with QUERITHM_DEV_ENGINES), on the Docker fixture. The fixture's
 * certificate is self-signed, so the profiles take the explicit Required downgrade, as the integration tests do.
 */
async function mysqlMetrics() {
  console.log("mysql metrics");
  const sc = await startSidecar({ QUERITHM_DEV_ENGINES: "mySql" });
  const { call, until, frames } = sc;
  const profileOf = (name, db) => call("profile.save", { id: "", name, engine: "mySql", host: MYSQL.host, port: MYSQL.port, auth: "password", username: MYSQL.user, environment: "dev", tlsMode: "require", databaseDefault: db });
  const run = async (sessionId, sql) => {
    const ex = await call("query.execute", { sessionId, sql, options: { noHistory: true } });
    return until((m, p) => m === "query.done" && p.executionId === ex.executionId, 300_000, "mysql run", true);
  };
  try {
    const admin = await profileOf("Bench MySQL admin", "mysql");
    const { sessionId: adminSid } = await call("session.open", { profileId: admin.id, database: "mysql", secret: MYSQL.password });
    const reset = await run(adminSid, `DROP DATABASE IF EXISTS ${MYSQL.db}; CREATE DATABASE ${MYSQL.db}; DROP DATABASE IF EXISTS q_bench_other; CREATE DATABASE q_bench_other;`);
    if (reset.error) throw new Error(`mysql fixture: ${reset.error.message}`);

    // #11cold.mysql: each sample a new profile, so a new physical connection: TCP, TLS, login, identity read, SELECT 1.
    const cold = [];
    for (let i = 0; i < 15; i++) {
      const profile = await profileOf(`Bench MySQL cold ${i}`, MYSQL.db);
      const { sessionId } = await call("session.open", { profileId: profile.id, database: MYSQL.db, secret: MYSQL.password });
      const before = new Set(frames.keys());
      const t0 = performance.now();
      const ex = await call("query.execute", { sessionId, sql: "SELECT 1", options: { noHistory: true } });
      await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 30_000, "mysql cold", true);
      let f;
      for (let w = 0; w < 100 && !(f = [...frames.entries()].find(([k, v]) => !before.has(k) && v.firstAt)?.[1]); w++) await sleep(10);
      if (!f) throw new Error("#11cold.mysql: no batch frame");
      cold.push(f.firstAt - t0);
      await call("session.close", { sessionId, force: true });
      await call("profile.delete", { profileId: profile.id });
    }
    record("11cold.mysql", median(cold), { samples: cold.map((x) => Math.round(x * 10) / 10) });

    const profile = await profileOf("Bench MySQL", MYSQL.db);
    const { sessionId } = await call("session.open", { profileId: profile.id, database: MYSQL.db, secret: MYSQL.password });
    await run(sessionId, "SELECT 1"); // the session's connection exists before the switch is timed

    // #8.mysql: USE-equivalent switch on the same connection (COM_INIT_DB), there and back.
    const switches = [];
    for (let i = 0; i < RUNS * 2; i++) {
      const t0 = performance.now();
      await call("session.changeDatabase", { sessionId, database: i % 2 === 0 ? "q_bench_other" : MYSQL.db });
      switches.push(performance.now() - t0);
    }
    if ((await call("session.state", { sessionId })).database !== MYSQL.db) await call("session.changeDatabase", { sessionId, database: MYSQL.db });
    record("8.mysql", median(switches), { samples: switches });

    // The schema for #9 / #10: 600 tables × 15 columns, 150 views, 200 triggers (DDL commits on its own; no transaction).
    const ddl = [];
    for (let t = 1; t <= 600; t++) ddl.push(`CREATE TABLE t${t} (id INT PRIMARY KEY, ${Array.from({ length: 14 }, (_, c) => `c${c} VARCHAR(40)`).join(", ")});`);
    for (let v = 1; v <= 150; v++) ddl.push(`CREATE VIEW v${v} AS SELECT id, c0 FROM t${v} WHERE id > ${v};`);
    for (let g = 1; g <= 200; g++) ddl.push(`CREATE TRIGGER tr${g} AFTER INSERT ON t${g} FOR EACH ROW SET @x = NEW.id;`);
    for (let k = 0; k < ddl.length; k += 100) {
      const made = await run(sessionId, ddl.slice(k, k + 100).join("\n"));
      if (made.error) throw new Error(`mysql fixture: ${made.error.message}`);
    }
    const refreshed = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status === "fresh" && p.full, 300_000, "mysql full refresh");
    await call("meta.refresh", { profileId: profile.id, database: MYSQL.db, full: true });
    await refreshed;
    const snaps = [];
    let objects = 0;
    for (let i = 0; i < RUNS; i++) {
      const t0 = performance.now();
      const s = await call("meta.snapshot", { profileId: profile.id, database: MYSQL.db });
      snaps.push(performance.now() - t0);
      objects = s.objects.length;
    }
    record("9.mysql", median(snaps), { objects, samples: snaps });

    const incr = [];
    for (let r = 0; r < RUNS; r++) {
      const changes = Array.from({ length: 10 }, (_, k) => `CREATE OR REPLACE VIEW v${k + 1} AS SELECT id, c0, c1 FROM t${k + 1} WHERE id > ${r};`).join("\n");
      await run(sessionId, changes);
      await sleep(300); // let the DDL-triggered refresh settle first
      const done = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.status === "fresh", 60_000, "mysql incremental");
      const t0 = performance.now();
      await call("meta.refresh", { profileId: profile.id, database: MYSQL.db });
      await done;
      incr.push(performance.now() - t0);
    }
    record("10.mysql", median(incr), { samples: incr });

    // #15.mysql: cancel SELECT SLEEP(30) after 200 ms; query.cancel → query.done(cancelled).
    const cancels = [];
    for (let i = 0; i < RUNS; i++) {
      const ex = await call("query.execute", { sessionId, sql: "SELECT SLEEP(30)", options: { noHistory: true } });
      await sleep(200);
      const t0 = performance.now();
      await call("query.cancel", { executionId: ex.executionId });
      const d = await until((m, p) => m === "query.done" && p.executionId === ex.executionId, 30_000, "mysql cancel", true);
      if (d.state !== "cancelled") throw new Error(`#15.mysql: state ${d.state}`);
      cancels.push(performance.now() - t0);
    }
    record("15.mysql", median(cancels), { samples: cancels });

    const t2 = await tier2Metric(sc, { dialect: "mySql", profileId: profile.id, database: MYSQL.db, sessionId });
    record("5.mysql", t2.p95, t2);
    const refresh = async () => {
      const done = until((m, p) => m === "meta.updated" && p.profileId === profile.id && p.database === MYSQL.db && p.status !== "refreshing", 120_000, "mysql refresh");
      await call("meta.refresh", { profileId: profile.id, database: MYSQL.db });
      await done;
    };
    // A DDL-triggered refresh can report "fresh" before the new table is read: wait until the snapshot has it.
    const inSnapshot = async (table) => {
      await refresh();
      for (let i = 0; i < 100; i++) {
        const snap = await call("meta.snapshot", { profileId: profile.id, database: MYSQL.db });
        if (snap.objects.some((o) => o.name === table && o.columns?.length)) return;
        await sleep(100);
      }
      throw new Error(`${table} never reached the MySQL snapshot`);
    };
    // The same race after a DROP: import.checkTarget refused "already exists" when it read the snapshot before the
    // DROP's refresh landed (CI, 2026-11-01: import.inspect of the 100k-row CSV finished first).
    const notInSnapshot = async (table) => {
      await refresh();
      for (let i = 0; i < 100; i++) {
        const snap = await call("meta.snapshot", { profileId: profile.id, database: MYSQL.db });
        if (!snap.objects.some((o) => o.name === table)) return;
        await sleep(100);
      }
      throw new Error(`${table} never left the MySQL snapshot`);
    };
    const edit = await previewPatchMetric(sc, run, sessionId, {
      engine: "mysql", refresh: () => inSnapshot("q_bench_edit"), select: "SELECT id, v, n FROM q_bench_edit ORDER BY id",
      setup: ["DROP TABLE IF EXISTS q_bench_edit", "CREATE TABLE q_bench_edit (id INT PRIMARY KEY, v VARCHAR(20), n INT)",
        "INSERT INTO q_bench_edit WITH RECURSIVE k(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM k WHERE i < 100) SELECT i, 'x', 1 FROM k"],
    });
    record("20.mysql", edit.ms, { samples: edit.samples });
    await run(sessionId, "DROP TABLE q_bench_edit");

    const imp = await importMetric(sc, run, sessionId, { engine: "mysql", schema: MYSQL.db, csv: writeImportCsv(mkdtempSync(path.join(os.tmpdir(), "qbench-mysql-"))), dropped: notInSnapshot });
    record("23.mysql", imp.ms, { rows: imp.rows, samples: imp.samples });

    // #21.mysql: the fixture database compared with itself, both sides read in parallel on two sessions.
    {
      const a = await call("session.open", { profileId: profile.id, database: MYSQL.db, secret: MYSQL.password });
      const b = await call("session.open", { profileId: profile.id, database: MYSQL.db, secret: MYSQL.password });
      const sides = { source: { sessionId: a.sessionId, database: MYSQL.db }, target: { sessionId: b.sessionId, database: MYSQL.db } };
      await call("schemaCompare.run", sides); // warm-up
      const runs = [];
      let items = 0;
      for (let i = 0; i < 3; i++) {
        const t0 = performance.now();
        items = (await call("schemaCompare.run", sides)).items.length;
        runs.push(performance.now() - t0);
      }
      record("21.mysql", median(runs), { objects: items, samples: runs.map((x) => Math.round(x)) });
      await call("session.close", { sessionId: a.sessionId, force: true });
      await call("session.close", { sessionId: b.sessionId, force: true });
    }

    // #22.mysql: 100k × 10 against a copy with 1,000 changed rows; sidecar memory delta ≤ 64 MB, as #22.
    {
      for (const sql of [
        "DROP TABLE IF EXISTS q_bench_dc_a, q_bench_dc_b",
        "CREATE TABLE q_bench_dc_a (id INT PRIMARY KEY, name VARCHAR(40), qty INT, price DECIMAL(10,2), ok BOOLEAN, born DATE, at DATETIME, code VARCHAR(12), note VARCHAR(60), ratio DOUBLE)",
        // 100,000 ids from five digits (a recursive count to 100,000 needs a session setting, and each execute may take another pooled connection).
        `INSERT INTO q_bench_dc_a WITH RECURSIVE d(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM d WHERE n < 9),
          k(i) AS (SELECT 1 + a.n + 10 * b.n + 100 * c.n + 1000 * e.n + 10000 * f.n FROM d a, d b, d c, d e, d f)
          SELECT i, CONCAT('name ', i), i % 100, (i % 1000) + 0.25, i % 2, DATE_ADD('2026-01-01', INTERVAL i % 365 DAY), DATE_ADD('2026-01-01', INTERVAL i MINUTE), CONCAT('C', i), CONCAT('note ', i), (i % 10) / 10.0 FROM k`,
        "CREATE TABLE q_bench_dc_b LIKE q_bench_dc_a",
        "INSERT INTO q_bench_dc_b SELECT * FROM q_bench_dc_a",
        "UPDATE q_bench_dc_b SET name = CONCAT(name, '!') WHERE id % 100 = 0",
      ]) {
        const d = await run(sessionId, sql);
        if (d.error) throw new Error(`#22.mysql setup: ${d.error.message}`);
      }
      await inSnapshot("q_bench_dc_b");
      const side = (table) => ({ sessionId, database: MYSQL.db, schema: MYSQL.db, table });
      const runs = [];
      let peak = 0;
      let rows = 0;
      for (let i = 0; i < 3; i++) {
        const before = rssMb(sc.child.pid);
        const timer = setInterval(() => { peak = Math.max(peak, rssMb(sc.child.pid) - before); }, 20);
        const t0 = performance.now();
        const st = await call("dataCompare.start", { source: side("q_bench_dc_a"), target: side("q_bench_dc_b") });
        const done = await until((m, p) => m === "dataCompare.progress" && p.compareId === st.compareId && p.done, 120_000, "mysql data compare", true);
        runs.push(performance.now() - t0);
        clearInterval(timer);
        if (done.error) throw new Error(`#22.mysql: ${done.error}`);
        rows = done.different;
        await call("query.close", { executionId: st.executionId }).catch(() => {});
      }
      record("22.mysql", median(runs), { differences: rows, memoryDeltaMb: Math.round(peak), memoryGatePass: peak <= 64, samples: runs.map((x) => Math.round(x)) });
      await run(sessionId, "DROP TABLE q_bench_dc_a, q_bench_dc_b");
    }

    // #24.mysql: find usages of id in t2…t8 — every view and trigger of the fixture mentions id — plus 5 open tabs.
    {
      await refresh();
      const tab = Array.from({ length: 200 }, (_, i) => `SELECT o.*, ${i} AS n FROM t${(i % 8) + 1} o WHERE o.id > ${i};`).join("\n");
      const tabs = Array.from({ length: 5 }, (_, i) => ({ tabId: `bench${i}`, title: `Bench${i}.sql`, text: tab }));
      const args = (n) => ({ sessionId, database: MYSQL.db, target: { kind: "column", schema: MYSQL.db, name: `t${n}`, column: "id" }, tabs });
      await call("refactor.findUsages", args(1)); // fills the definition cache
      const cold = [];
      let last;
      for (let i = 2; i <= 8; i++) {
        const t0 = performance.now();
        last = await call("refactor.findUsages", args(i));
        cold.push(performance.now() - t0);
      }
      record("24.mysql", median(cold), { objectsScanned: last.objectsScanned, hits: last.hits.length, samples: cold.map((x) => Math.round(x * 10) / 10) });
    }
    await run(adminSid, `DROP DATABASE IF EXISTS ${MYSQL.db}; DROP DATABASE IF EXISTS q_bench_other;`);
  } finally {
    sc.kill();
  }
}

/** #16: kill the parent with SIGKILL; the sidecar must be gone within 5 s (PDEATHSIG / getppid watch). */
async function orphanCheck() {
  if (process.platform === "win32") return;
  const token = randomBytes(8).toString("hex");
  const sock = path.join(os.tmpdir(), `qorph-${process.pid}.sock`);
  const parent = spawn(process.execPath, ["-e", `
    const { spawn } = require("node:child_process");
    const c = spawn(${JSON.stringify(sidecarBin())}, [], { env: { ...process.env, QUERITHM_TOKEN: "${token}", QUERITHM_SOCKET: ${JSON.stringify(sock)}, QUERITHM_PARENT_PID: String(process.pid), QUERITHM_DATA_DIR: ${JSON.stringify(mkdtempSync(path.join(os.tmpdir(), "qorph-")))} }, stdio: ["pipe", "ignore", "ignore"] });
    console.log(c.pid);
    setInterval(() => {}, 1000);
  `], { stdio: ["ignore", "pipe", "ignore"] });
  const childPid = Number(await new Promise((r) => parent.stdout.once("data", (d) => r(String(d).trim()))));
  await sleep(1500);
  process.kill(parent.pid, "SIGKILL");
  await sleep(5000);
  let alive = 0;
  try {
    process.kill(childPid, 0);
    alive = 1;
    process.kill(childPid, "SIGKILL");
  } catch { /* gone */ }
  record(16, alive);
}

async function uiMetrics() {
  console.log("ui metrics (dev bridge + headless Chrome)");
  const chrome = ["/usr/bin/google-chrome", "/usr/bin/chromium", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "C:/Program Files/Google/Chrome/Application/chrome.exe"].find(existsSync);
  if (!chrome) return console.log("  skipped: no Chrome found");
  const { default: puppeteer } = await import("puppeteer-core");
  // build:web (tsc -b && vite build) without pnpm on PATH; a failed build must not measure an older dist/.
  // Ports as for the UI smoke (QUERITHM_UI_PORT / QUERITHM_BRIDGE_PORT), so a dev server already on 1420/1421 is not
  // what gets measured; the webview is built to dial the bridge's port.
  const uiPort = Number(process.env.QUERITHM_UI_PORT ?? 1420);
  const bridgePort = Number(process.env.QUERITHM_BRIDGE_PORT ?? 1421);
  for (const step of [[path.join(root, "node_modules", "typescript", "bin", "tsc"), "-b"], [path.join(path.dirname(fileURLToPath(import.meta.resolve("vite/package.json"))), "bin", "vite.js"), "build"]]) {
    const r = spawnSync(process.execPath, step, { cwd: root, stdio: "inherit", env: { ...process.env, VITE_BRIDGE_URL: `ws://127.0.0.1:${bridgePort}` } });
    if (r.status !== 0) throw new Error(`web build failed: ${path.basename(step[0])} ${step[1]} (${r.error?.message ?? "exit " + r.status})`);
  }
  const bridge = spawn(process.execPath, [path.join(root, "scripts", "dev-bridge.mjs")], { env: { ...process.env, QUERITHM_BENCH: "1", QUERITHM_SIDECAR: sidecarBin(), QUERITHM_BRIDGE_PORT: String(bridgePort), QUERITHM_UI_PORT: String(uiPort), QUERITHM_DATA_DIR: mkdtempSync(path.join(os.tmpdir(), "qbench-ui-")) }, stdio: "ignore" });
  const preview = spawn(process.execPath, [path.join(path.dirname(fileURLToPath(import.meta.resolve("vite/package.json"))), "bin", "vite.js"), "preview", "--port", String(uiPort), "--strictPort", "--host", "127.0.0.1"], { cwd: root, stdio: "ignore" });
  await sleep(2500);
  const browser = await puppeteer.launch({ executablePath: chrome, headless: true, args: ["--enable-precise-memory-info", "--js-flags=--expose-gc", "--window-size=1440,900"], defaultViewport: { width: 1440, height: 900 } });
  try {
    const page = await browser.newPage();
    const t0 = Date.now();
    await page.goto(`http://127.0.0.1:${uiPort}/?bench`, { waitUntil: "domcontentloaded" });
    await page.waitForSelector(".monaco-editor textarea", { timeout: 30_000 });
    await page.click(".monaco-editor");
    await page.keyboard.type("x");
    await page.waitForFunction(() => document.querySelector(".view-lines")?.textContent?.includes("x"), { timeout: 10_000 });
    record(1, Date.now() - t0, { note: "browser navigation → first keystroke rendered (app cold start: use --app)" });
    await page.waitForFunction(() => window.__bench?.ready === true, { timeout: 30_000 });
    // Bind the active tab to the fixture so tier 1 builds real items.
    await page.evaluate(async (m) => {
      const p = await window.__bench.setupFixture(m);
      return p;
    }, MSSQL);
    const typing = await page.evaluate(() => window.__bench.typing(200, 12000));
    record(3, p95(typing.work), { samples: typing.work.length, toPaintP95IncludingVsync: Math.round(p95(typing.toPaint) * 10) / 10 });
    const t1 = await page.evaluate(() => window.__bench.tier1(300, 12000));
    record(4, p95(t1), { samples: t1.length, schemaObjects: await page.evaluate(() => window.__tier1Objects ?? null) });
    const tabs = await page.evaluate(() => window.__bench.tabSwitch(50));
    record(7, p95(tabs), { samples: tabs.length });
    const tabs30 = await page.evaluate(() => window.__bench.tabSwitch30(50));
    record("7b", p95(tabs30), { samples: tabs30.length });
    await page.evaluate(() => window.gc?.());
    const big = await page.evaluate(() => window.__bench.bigResult(1_000_000, 40, 240));
    record(11, big.firstBatchMs, { fullFetchMs: big.fullFetchMs, rows: big.rows });
    // Frame intervals cannot go below one refresh (16.7 ms at 60 Hz): the gate is "no frame longer than one refresh".
    const refresh = median(big.scrollFrames);
    const longFrames = big.scrollFrames.filter((f) => f > refresh + 1.5).length;
    results[12] = { metric: TARGETS[12][0], unit: "ms", target: TARGETS[12][2], value: Math.round(p95(big.scrollFrames) * 10) / 10, refreshMs: Math.round(refresh * 10) / 10, longFrames, samples: big.scrollFrames.length, pass: p95(big.scrollFrames) <= refresh + 1.5 };
    console.log(`  #12 ${TARGETS[12][0]}: ${results[12].value} ms at a ${results[12].refreshMs} ms refresh, ${longFrames} long frame(s) ${results[12].pass ? "PASS" : "FAIL"}`);
    record(13, big.memoryDeltaBytes == null ? NaN : big.memoryDeltaBytes / 1024 / 1024);
    await page.evaluate(() => window.gc?.());
    const chart = await page.evaluate(() => window.__bench.chartToggle(1_000_000, 3));
    record(34, median(chart.firstPaint), { rows: chart.rows, samples: chart.firstPaint });
    record("34frames", chart.longFrames, { frames: chart.frames.length, worstMs: Math.round(chart.worstFrameMs * 10) / 10 });
    const frames = await page.evaluate(async () => {
      await window.__bench.startConcurrent(5, 60);
      return window.__bench.frameDrops(15_000);
    });
    record(14, frames.filter((f) => f > 32).length, { frames: frames.length, worstMs: Math.max(...frames) });
    const flood = await page.evaluate(() => window.__bench.messageFlood(100_000));
    record("36frames", flood.longFrames, { ms: Math.round(flood.ms), state: flood.state, messages: flood.messages, frames: flood.frames, worstMs: Math.round(flood.worstFrameMs * 10) / 10 });
    const mon = await page.evaluate(() => window.__bench.monitorApply(500, 60));
    record("28ui", p95(mon.steady), { sessions: mon.sessions, tickBytes: mon.tickBytes, samples: mon.steady.length });
    record("28first", mon.first, { firstSnapshotBytes: mon.firstBytes });
    const dl = await page.evaluate(() => window.__bench.deadlockRender(200));
    record("30list", dl.listMs, { events: dl.events });
    record("30graph", median(dl.graph), { processes: dl.processes, samples: dl.graph });
    // The Explorer's synthetic 25k-object tree (§14 metric 33). A failure here is recorded and does not skip the ERD
    // metrics below.
    try {
      const ex = await page.evaluate(() => window.__bench.explorerTree(5));
      record("33expand", median(ex.expand), { objects: ex.objects, samples: ex.expand });
      // As for #12: p95 frame time against the measured refresh; renders counted to prove no render per frame.
      const refresh = median(ex.scroll);
      const longFrames = ex.scroll.filter((f) => f > refresh + 1.5).length;
      const pass = p95(ex.scroll) <= refresh + 1.5 && ex.renders <= Math.ceil((ex.steps * 37) / 24 / 16) + 2;
      results["33scroll"] = { metric: TARGETS["33scroll"][0], unit: "ms", target: TARGETS["33scroll"][2], value: Math.round(p95(ex.scroll) * 10) / 10, refreshMs: Math.round(refresh * 10) / 10, longFrames, renders: ex.renders, steps: ex.steps, pass };
      console.log(`  #33scroll ${TARGETS["33scroll"][0]}: ${results["33scroll"].value} ms at a ${results["33scroll"].refreshMs} ms refresh, ${longFrames} long frame(s), ${ex.renders} renders in ${ex.steps} steps ${pass ? "PASS" : "FAIL"}`);
      record("33patch", median(ex.patch), { samples: ex.patch });
      record("33search", p95(ex.search), { coldMs: Math.round(ex.searchCold * 10) / 10, samples: ex.search });
      record("33group", median(ex.group), { samples: ex.group });
    } catch (e) {
      for (const k of ["33expand", "33scroll", "33patch", "33search", "33group"]) if (!results[k]) results[k] = { metric: TARGETS[k][0], unit: TARGETS[k][1], target: TARGETS[k][2], value: null, error: String(e?.message ?? e), pass: false };
      console.log(`  #33 Explorer tree bench failed: ${e?.message ?? e}`);
    }
    // Last: the diagram builds allocate a lot, which would otherwise show up as GC pauses in the samples above.
    const erd = await page.evaluate(() => window.__bench.erd(5));
    record(18, median(erd.hop), { tables: erd.hopNodes, samples: erd.hop });
    record(19, median(erd.whole), { tables: erd.wholeNodes, fks: erd.wholeEdges, samples: erd.whole });
  } finally {
    await browser.close();
    preview.kill();
    bridge.kill();
  }
}

/** Bundled app: cold start (#1), total RSS (#2), orphan check against the real shell (#16), installer size (#17). */
async function appMetrics() {
  if (!appBin) {
    const bundle = path.join(root, "src-tauri", "target", "release", "bundle");
    if (existsSync(bundle)) {
      let max = 0;
      let appImage = 0;
      const walk = (d) => readdirSync(d).forEach((f) => {
        const p = path.join(d, f);
        const st = statSync(p);
        if (st.isDirectory()) walk(p);
        else if (f.endsWith(".AppImage")) appImage = Math.max(appImage, st.size);
        else if (/\.(deb|msi|exe|dmg|rpm)$/.test(f)) max = Math.max(max, st.size);
      });
      walk(bundle);
      if (max) record(17, max / 1024 / 1024);
      if (appImage) record("17.appimage", appImage / 1024 / 1024);
    }
    return;
  }
  console.log("app metrics");
  const outDir = mkdtempSync(path.join(os.tmpdir(), "qapp-"));
  const t0 = Date.now();
  const app = spawn(appBin, [], { env: { ...process.env, QUERITHM_BENCH: "1", QUERITHM_BENCH_OUT: outDir }, stdio: "ignore" });
  await sleep(8000);
  const tree = execFileSync("ps", ["-e", "-o", "pid=,ppid=,rss=,comm="]).toString().trim().split("\n").map((l) => l.trim().split(/\s+/));
  const kids = (pid) => tree.filter((r) => r[1] === String(pid));
  const all = [String(app.pid)];
  for (let i = 0; i < all.length; i++) for (const k of kids(all[i])) all.push(k[0]);
  const rss = tree.filter((r) => all.includes(r[0])).reduce((n, r) => n + Number(r[2]), 0) / 1024;
  record(2, rss, { processes: all.length });
  results.appLaunchToSampleMs = Date.now() - t0;
  const sidecar = tree.find((r) => all.includes(r[0]) && r[3].startsWith("querithm-sideca"));
  process.kill(app.pid, "SIGKILL");
  await sleep(5000);
  let alive = 0;
  if (sidecar) try { process.kill(Number(sidecar[0]), 0); alive = 1; } catch { /* gone */ }
  record(16, alive, { via: "bundled app" });
}

function compare(file) {
  const dir = path.dirname(file);
  const cur = JSON.parse(readFileSync(file, "utf8"));
  // Only runs from the same hardware are comparable (a committed laptop baseline vs a CI runner is not).
  const earlier = readdirSync(dir).filter((f) => f.endsWith(".json") && path.join(dir, f) !== file).sort()
    .map((f) => JSON.parse(readFileSync(path.join(dir, f), "utf8")))
    .filter((j) => j.cpu === cur.cpu && j.cores === cur.cores);
  if (earlier.length === 0) {
    console.log(`  no earlier run on this machine (${cur.cpu}, ${cur.cores} cores) — regression check skipped`);
    return 0;
  }
  // Each metric against the latest earlier run that measured it (a partial run, e.g. --sqlite, hides nothing).
  const old = {};
  for (const j of earlier) for (const [id, r] of Object.entries(j.results ?? {})) if (r?.value != null) old[id] = r;
  let failures = 0;
  for (const [id, r] of Object.entries(results)) {
    if (!r?.metric || !old[id]?.value || !Number.isFinite(r.value)) continue;
    // Noise floor: changes on values still under half their target are not regressions.
    if (r.value > old[id].value * 1.2 && r.value > r.target * 0.5) {
      console.log(`  REGRESSION #${id} ${r.metric}: ${old[id].value} → ${r.value} ${r.unit} (> 20 %)`);
      failures++;
    }
  }
  return failures;
}

if (only === "engine" || only === "all") await engineMetrics();
if (only === "all" || only === "engine" || only === "sqlite") await sqliteMetrics();
if (only === "all" || only === "engine" || only === "mysql") await mysqlMetrics();
if (only === "all" || only === "engine" || only === "duckdb") await duckdbMetrics();
if (only === "all" || only === "engine" || only === "oracle") await oracleMetrics();
// A ui run that broke off measured nothing: it fails the run instead of reading as "0 outside target".
let uiError = null;
if (only === "ui" || only === "all") await uiMetrics().catch((e) => { uiError = e; console.log(`  ui metrics failed: ${e.message}`); });
await appMetrics();

const outDir = path.join(root, "bench", "results");
mkdirSync(outDir, { recursive: true });
const file = path.join(outDir, `${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}-${process.platform === "win32" ? "windows" : process.platform}.json`);
writeFileSync(file, JSON.stringify({ date: new Date().toISOString(), os: `${os.type()} ${os.release()}`, cpu: os.cpus()[0]?.model, cores: os.cpus().length, memGb: Math.round(os.totalmem() / 2 ** 30), results }, null, 2));
const failed = Object.values(results).filter((r) => r?.metric && !r.pass).length;
const regressions = compare(file);
console.log(`\nwrote ${path.relative(root, file)} — ${failed} metric(s) outside target, ${regressions} regression(s)${uiError ? ", ui metrics FAILED" : ""}`);
process.exit(regressions > 0 || uiError ? 1 : 0);

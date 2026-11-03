# Phase B — Server Activity Monitor

Goal: a live, engine-neutral view of what a server is doing — sessions, blocking, locks, waits, top
queries, overview metrics, short history — with guarded cancel/kill. Feature parity target: SSMS Activity
Monitor + `sp_WhoIsActive` column set, for both active engines (`SqlServer`, `Postgres`). No new
dependencies.

Slices, in two tiers. Sizes use the S/M/L scale of `plans/README.md` and are rough, not a commitment.
- **B0 — risk spike** (S), before anything else: see below. Its numbers go into this plan before B1a starts.
- **B-core** (Phase B is done when these ship), in this order:
  **B1a** sessions, read-only (M) → **B1b** own-tab awareness + blocking banner (M) → **B1c** guarded
  kill + kill preview (L) → **B2** blocking tree + locks (M) → **B3** waits + overview metrics + I/O (M)
  → **B3b** "Why is it slow?" + wait dictionary (M) → **B4** top queries (M) → **B7** deadlock history (M).
  - B1b ships before B1c because `KillGuard` refuses Querithm's own connections using B1b's connection
    registry; the kill must never ship without it.
  - B3b ships after B3 because its rules read the blocking graph (B2) and the waits (B3).
- **B-next** (same phase, after B-core): **B5** request detail / live plan (S) → **B6** history ring
  buffer + export (M).
- **Deferred, not in Phase B:** **B8** Query Store + server health and **B9** local alerts. They stay
  specified below so the contract leaves room for them, but nothing in them is built, tested or
  benchmarked in Phase B. Reason: they cost a lot (1 GB Query Store fixture, PG primary + standby, a
  Tauri notification plugin that grows `src-tauri`, background connections to Prod) and add little
  over what IDE users need — 24/7 alerting is what dedicated monitors do (Redgate SQL Monitor,
  pganalyze, Datadog DBM), and alerts that only fire while Querithm is open can't compete with them.
  Revisit after Phase D's initial milestone (D1–D3); if picked up, B8/B9 get their own plan and §14 rows.

Each slice is a vertical slice (Contract → Sidecar + xUnit → UI + Vitest) and ships on its own.

## B0 — Risk spike (throwaway branch, both fixture engines)
Proves the assumptions the rest of the plan stands on. If one fails, this plan is revised before B1a.
1. **Identity round trip (K13):** read `SessionKey` on SQL Server (`login_time` as `SqlDateTime`) and
   PG (`backend_start` in µs via Npgsql), send it through JSON → UI → RPC, bind it back as a typed
   parameter, and match exactly; the same key with the last digit changed must not match.
2. **Kill batches:** the SQL Server `IF EXISTS … KILL <literal>` batch with a typed `@start`
   parameter, and the PG `pg_terminate_backend` / `pg_cancel_backend` predicates (B1c), each against
   a real blocked session.
3. **Kill control connection:** `SessionManager.OpenDedicatedAsync` through an SSH profile within the
   5 s connect timeout while the profile's pool is exhausted.
4. **Identity cost:** the extra round trip in `SessionManager.OpenConnectionAsync` (B1b), measured on
   the §14 "execute → first batch" path for a cold pool connection.
5. **Payload:** full-row snapshot size for 500 sessions and the webview cost of `JSON.parse` + Glide
   update. This decides whether full rows meet metric 28 or the diff escalation is needed (see
   "Payload").

### B0 findings (2026-10-28)
Environment: Windows 11 dev machine, Docker Desktop fixture (`scripts/bench/docker/compose.yml`): SQL Server
2022 Developer RTM-CU27-GDR 16.0.4305.5 on Linux (`EngineEdition = 3`); PostgreSQL 16.15 with
`plugin_debugger,pg_stat_statements,pg_wait_sampling` preloaded, `max_connections = 600`, `track_io_timing = on`;
PostgreSQL 17.11 with only `pg_stat_statements` preloaded; a bare PostgreSQL 16.15 (nothing preloaded).
Drivers Microsoft.Data.SqlClient 6.1.7, Npgsql 10.0.3. Probe program: a throwaway console app (not in the repo);
the behaviours that matter are now integration tests (`MonitorIntegrationTests`, `ConnectionLifecycleIntegrationTests`).

| # | SQL / action | Observed | Consequence |
|---|---|---|---|
| 1 | SQL Server: `SELECT session_id, login_time FROM sys.dm_exec_sessions WHERE session_id = @@SPID`, read with `GetSqlDateTime`; back as `SqlDbType.DateTime` from `new SqlDateTime(DayTicks, TimeTicks)` in `… WHERE session_id = @id AND login_time = @start` | Exact match (1 row); `TimeTicks + 1` → 0 rows. 20 sessions opened back to back: no two share a `login_time` | `Instance` = `"<DayTicks>:<TimeTicks>"` (invariant integers), parsed strictly, bound as `SqlDbType.DateTime`. A `DateTime2` parameter is **not** safe: it compares the converted `datetime2` exactly, so a millisecond-truncated value misses |
| 1 | PG: `SELECT pid, backend_start FROM pg_stat_activity WHERE pid = pg_backend_pid()`; µs since 2000-01-01 from `DateTime.Ticks`; back as `NpgsqlDbType.TimestampTz` | Exact match; +1 µs → 0; ms-truncated → 0. Npgsql returns `Kind = Utc` with no sub-µs ticks | `Instance` = µs since the PG epoch as invariant integer text |
| 2 | SQL Server guarded batch `IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE session_id = @id AND login_time = @start AND is_user_process = 1) KILL <n>; ELSE SELECT 'gone';` sent parameterised (SqlClient wraps it in `sp_executesql`) against an idle-in-tx blocker | `KILL` works inside `sp_executesql`; wrong `login_time` → `'gone'`, target untouched; exact → no result set, target's next command fails (connection broken), its update rolled back, the blocked reader completes. Errors: 6104 own process, 6107 "Only user processes can be killed", 6106 not an active process, 6115 inside a user transaction, 6102 no permission, 6120 STATUSONLY when not rolling back | "No result set" = sent; `'gone'` = identity mismatch. Error numbers mapped to messages |
| 2 | SQL Server system sessions | System spids up to 72 while the lowest user spid was 70 | Confirms K11: `is_user_process`, never `spid > 50` |
| 2 | **SQL Server rollback after `KILL`** of a 141 MB write tx: (a) target sleeping (idle in tx) (b) target running | (a) **no `sys.dm_exec_requests` row at all** during the rollback; the session row stays until done. (b) request row `status = 'rollback'`, `command = 'AWAITING COMMAND'` (not `KILLED/ROLLBACK`), `percent_complete` rising. A repeated `KILL` and `KILL … WITH STATUSONLY` both return info message **6109** ("transaction rollback in progress … N%") and no error | **Plan revised (B1c, K19):** "rolling back" = request `status = 'rollback'` **or** the target is one this monitor already terminated and its session row still exists (6109 confirms on STATUSONLY). Progress: `percent_complete` when there is a request row, else STATUSONLY's 6109. The guard refuses a repeated kill itself: the server would accept it silently. The server's "estimated time remaining" in 6109 is never shown (P9) |
| 2 | PG `SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE pid = $1 AND backend_start = $2 AND state = 'active' AND query_start = $3 AND backend_type = 'client backend'` | Stale `query_start` → no row, nothing sent; exact → `true`, target's statement fails 57014, same backend stays. Idle backend → no row. (A raw `pg_cancel_backend` on an idle backend returns `true` and does nothing) | As planned (K26, K27) |
| 2 | PG `pg_terminate_backend` with the same identity predicate | `true` in 2 ms; the `pg_stat_activity` row is gone immediately; target's next command 57P01, connection broken, its update rolled back | K28 as planned |
| 3 | SQL Server client cancel (attention) of `WAITFOR` inside `BEGIN TRAN` | `XACT_ABORT OFF`: `@@TRANCOUNT` stays 1, connection open. **`XACT_ABORT ON`: the server rolls the transaction back** (`@@TRANCOUNT` 0), connection open | Querithm never rolls back on cancel; a server rollback is detected by the post-execution `@@TRANCOUNT` read, which unpins the connection and warns "the transaction that was open before this run has ended on the server" (`ConnectionLifecycleIntegrationTests`, both engines) |
| 3 | PG client cancel (Npgsql) / `pg_cancel_backend` inside a tx block | Statement 57014, connection open, transaction **aborted** (`idle in transaction (aborted)`); next statement and `SAVEPOINT` → 25P02 | Existing `GetTxStateAsync` maps 25P02 → Open: the tab keeps the pinned connection until the user rolls back. Never auto-rolled back |
| 4 | SQL Server login without `VIEW SERVER STATE` / `VIEW SERVER PERFORMANCE STATE` (2022) | `dm_exec_sessions`, `dm_exec_requests`: own row only, no error. **`dm_exec_sql_text`, `dm_exec_input_buffer`, `dm_exec_connections`: error 300 even for the login's own session** (found in the second pass, once errors stopped being reported as messages — B0 row 10): own rows have no text, with the reason. `dm_os_waiting_tasks`, `dm_os_wait_stats`, `dm_tran_locks`, `dm_exec_query_stats`, `dm_tran_database_transactions`: error 300 "VIEW SERVER PERFORMANCE STATE permission was denied". `dm_xe_session_targets`: 297. With only `VIEW SERVER PERFORMANCE STATE` granted: everything above readable | Probe both permissions with `HAS_PERMS_BY_NAME` (`VIEW SERVER PERFORMANCE STATE` is unknown before 2022 → NULL → treated as 0); errors 300/297 at run time degrade the pane, never crash |
| 4 | **PG login without `pg_read_all_stats`** reading other roles' rows | `backend_start`, `backend_type`, `state`, `xact_start`, `query_start`, `wait_event*`, `query_id`, `backend_xid` are **NULL**, `query = '<insufficient privilege>'` (only pid, user, application, client, `backend_xmin` visible). With `pg_signal_backend` granted, `pg_terminate_backend` of such a backend still succeeds | **Plan revised (new degradation row + K29):** such rows have no exact identity (`Instance` empty, `IdentityHidden` reason). They are shown but never killable, even with `pg_signal_backend`: the identity re-check cannot run. Superuser backends: 42501 for a non-superuser even with `pg_signal_backend` (K18) |
| 5 | Counters: SQL Server `DBCC SQLPERF('sys.dm_os_wait_stats', CLEAR)`; PG `pg_stat_reset()`, `pg_stat_statements_reset()` | Wait sum → 0; `pg_stat_database` counters → 0 with a new `stats_reset`; `pg_stat_statements_info.stats_reset` changes | Reset = any per-key decrease, or a changed `stats_reset` / `sqlserver_start_time` |
| 5 | SQL Server CPU ring buffer on Linux | `ProcessUtilization` meaningful, **`SystemIdle` always 0** | Only "SQL Server process CPU %" is shown; "other processes" (100 − idle − process) is never derived |
| 5 | `dm_os_performance_counters` | `Batch Requests/sec`, `SQL Compilations/sec` are cumulative (`cntr_type 272696576`); `Page life expectancy`, `User Connections` are point values (65792) | Delta by `cntr_type`, never by name |
| 6 | PG 16 vs 17 catalogs | 16: `pg_stat_bgwriter.checkpoints_timed/req`, `pg_stat_statements.blk_read_time`. 17: `pg_stat_checkpointer.num_timed/num_requested`, `shared_blk_read_time`. `pg_stat_io.op_bytes` on both (18 replaces it with `read_bytes`) | Probed by `to_regclass` / `pg_attribute`, never by version |
| 6 | `pg_wait_sampling` | Profile keyed by (pid, event_type, event, queryid), `profile_period = 10` ms, `sample_cpu = on` (NULL event = on CPU); dominated by background `Activity` waits | Aggregate `GROUP BY event_type, event` server-side; `Activity` class and `Client:ClientRead` are benign |
| 6 | Bare PG with `CREATE EXTENSION pg_stat_statements` | `SELECT … FROM pg_stat_statements` and `pg_stat_statements(false)` → 55000 "pg_stat_statements must be loaded via shared_preload_libraries"; `compute_query_id = auto` → `query_id` NULL | T4 / D19 as planned |
| 7 | SQL Server `sys.dm_os_wait_stats` while a lock wait is in progress | The wait is counted only when it **completes**: a victim blocked for 30 s adds nothing until it is released | The waits pane says "waits completed since the previous tick; waits still in progress are shown in the sessions pane" (the sessions pane has `wait_time` of running waits) |
| 7 | SQL Server statement text by offsets; plain `SELECT … /* tag */` | The statement ends at its last token (a trailing comment is outside the offsets); a plain parameterless `SELECT` is auto-parameterised and cached under the normalised text | Tests tag statements with a column alias inside a batch that declares a variable |
| 7 | PG `SELECT 1 FROM pg_stat_statements(false) LIMIT 0` on a server without the preload | **Succeeds**: with `LIMIT 0` the executor never calls the function, so 55000 is never raised | Probes fetch a row (`LIMIT 1`) for `pg_stat_statements` and `pg_wait_sampling_profile`; T4 is the regression test |
| 7 | PG `pg_stat_database.deadlocks` after a real deadlock between two explicit transactions | Unchanged while the victim's (aborted) transaction is open; +1 once it rolls back: a backend flushes its statistics only when idle outside a transaction block | The overview's deadlock counter can lag a deadlock until the victim's transaction ends; X11 rolls both back before asserting |
| 7 | PG `pg_stat_statements` `queryid` | Ignores column aliases and constants and keeps the first text it saw; the table's OID is part of it | Top-query text for PG is the first-seen text of that `queryid` (one sample text, T5); tests give each run its own temp table |
| 7 | SQL Server 2022 lock pane under a Sch-M lock (M3) | The name resolver's catalog read (READ UNCOMMITTED, `LOCK_TIMEOUT` 2 s) returned `dbo.monitor_it2` while the `ALTER TABLE` transaction held Sch-M (2 runs) | M3 accepts the resolved name or "name locked" (when the catalog read times out); the requirement is "no hang", which holds either way |
| 8 | SQL Server 2022 deadlock involving `MonitorIT`, read by a login with VIEW SERVER STATE and no user in that database (X7) | The `system_health` report carries `objectname="MonitorIT.dbo.monitor_it2"` itself: the graph shows the name | **Plan revised (X7):** the name comes from the report, so database access does not change it; "shown as id" applies only when the report has no `objectname` |
| 9 | SQL Server: `KILL` of an **idle** session holding a 240–940 MB write transaction, then any read of `sys.dm_tran_session_transactions` (any session filter) | The read waits on `MSQL_XACT_MGR_MUTEX` until the rollback ends (3–15 s here); a client timeout (attention) is not processed meanwhile. `dm_tran_active_transactions` shows the transaction with `transaction_state = 7` and does not block; `dm_exec_sessions`, `dm_exec_requests`, `dm_os_waiting_tasks`, `dm_tran_locks` and the space-usage DMVs do not block | Every SQL Server read of `dm_tran_session_transactions` is guarded: `DECLARE @rb bit = CASE WHEN EXISTS (… transaction_state = 7) …` and `WHERE @rb = 0 AND …` (a startup filter: the DMV is not touched). While it runs, transaction ages are shown as unavailable with the reason and the kill preview is "unknown" (long-rollback confirm). Before the guard, each tick and the kill preview froze for the rollback's length |
| 10 | SQL Server connections opened by the provider (`FireInfoMessageEventOnUserErrors = true`, for query-tab message streaming) running the monitor's queries without VIEW SERVER STATE, or a `KILL` without ALTER ANY CONNECTION | Errors below severity 17 arrive as info messages: error 300 → an empty, "successful" result; `KILL` error 6102 → "no result set" = reported as terminated | `IActivityProvider.PrepareMonitorConnection` turns it off on monitor and control connections; the banner check turns it off around its own query. S14 and K17 (revoked after the probe) are the regression tests |
| 11 | SQL Server `SELECT COUNT_BIG(*)` over a cross join of `sys.all_objects` (test load) | Finishes in seconds (18 billion rows "counted"); with a per-row predicate it runs, but serially (DOP 1): catalog views keep a plan serial | Long and parallel test queries use a user numbers table (`dbo.monitor_nums`, DOP 4 observed) |
| 12 | SQL Server: `KILL` of a tab's idle pooled connection, then the tab runs again (SqlClient connection resiliency, `ConnectRetryCount` 1) | The same `SqlConnection` object reconnects inside the run with a new spid/`login_time`; Querithm re-read the identity only at the next borrow, so meanwhile the tab's server session looked foreign and passed the kill guard | Identity re-read after every run (token check, no I/O unless it changed); kills refused while any registered connection's token changed and it is not identified again (O2) |
| 13 | SQL Server `DBCC FREEPROCCACHE` with the top-queries pane on | Noticed only at the next full read (every 30th run: 150 s at a 1 s interval, 300 s at 2 s) | Each incremental read also reads the plan-cache size (`dm_os_memory_cache_counters`, O(1)); a drop below 10 % of the last full read triggers a full read at once (T1) |
| 14 | SQL Server: 20 independent two-session deadlocks formed at the same moment, 11 times (bench seeding) | `Number of Deadlocks/sec` counted 240, `system_health` held 37 `xml_deadlock_report` events (no dropped events): deadlocks resolved in one lock-monitor pass are reported together, one multi-victim report | The deadlock pane shows reports, not deadlocks: a multi-victim report is one event (X6 renders it). Metric 30 seeds one deadlock at a time |
| 15 | SQL Server `sys.fn_xe_file_target_read_file(N'system_health*.xel'…)` filtered to `xml_deadlock_report`, 200 deadlocks retained | 1.4 s with ~5 MB of `system_health` files, 5.7 s with 30 MB (one 25 MB current file after repeated seeding): the function parses every event of every file; SQL Server 2022 keeps up to 10 × 100 MB | A ring-buffer-only poll was tried and failed the smoke test: the ring buffer's XML is capped at 4 MB (`truncated=1`) and then leaves out the newest events (newest in it 09:03 while a 09:27 deadlock was in the files). The poll now reads the files once per connection and afterwards only what came after its last position (`fn_xe_file_target_read_file(@path, NULL, @file, @offset)`); **revised (soak):** that first read scans only the file being written (the `event_file` target names it: 25 ms for 138 events, against 0.9 s idle / 2.8–3.4 s under churn for all ten rollover files), and two failed or cut first reads switch the poll to the ring buffer with the reason — a cut read used to reset the cursor, so every poll repeated it and the pane failed for a whole soak; the pane does a full read; every file read is bounded by an 8 s command timeout (then ring buffer only, with the reason). Metric 30's 2 s full read holds only while the files are small |
| 16 | PostgreSQL `pg_wait_sampling` (`profile_pid = on`, the default) after the bench's connection churn (`pgbench -C`) | `pg_wait_sampling_profile` held 121,301 entries for 56,801 pids, most long gone; the monitor's `GROUP BY event_type, event` over it took 241 ms per tick | The profile read is timed on its own; after three reads over 50 ms the monitor samples waits from the sessions (tier 2) for the rest of that connection and the waits pane says why and how to make the profile cheap again; it never resets the profile itself (X15) |
| 17 | Bounded soak (11 min, four monitors at 1 s, churn with new literals on both engines): heap growth after warm-up | +40.5 MB (22 → 70 MB). Heap dumps 3 min apart (`dotnet-gcdump`): 72,197 `TopQueryCounter` objects for a plan cache of ~30,000 statements — each SQL Server monitor's tracker holds every cached statement with its plan-handle, sql-handle and query-hash strings (~800 B each), and the cache was refilling after a restart; plus 1,052 task continuations left on the scheduler's wake signal in 3 min | Tracker entries are a 128-bit key and seven counters (151 B measured, dictionary slack included); the sidecar digests the raw plan handle + offsets (a server-side `HASHBYTES` made the full read 1.7× slower: 366 vs 210 ms over 28,144 entries, 159 ms with the raw handle) and SQL Server sends hash and text key only for entries that ran since the cutoff; at most 200,000 tracked entries (beyond: the pane stops with the reason and restarts from a full read that fits) and 10,000 since-start groups (least recently active dropped, with a note). The scheduler waits with `Task.WhenAny`, which removes its continuation from the task that did not finish |
| 18 | Text store cap (4 MB of unreferenced texts) against the heap | The cap counted UTF-8 text bytes: 20,000 short churn statements counted 78 B each while the heap held 451 B (UTF-16 text, id string, entry, LRU node, dictionary and sent-set slots) — the 4 MB cap allowed ~23 MB per monitor | A text counts its UTF-16 size plus a measured 360 B overhead; the 4 MB cap and B6's 50 MB cap (which includes history texts) now bound heap bytes |
| 19 | Deadlock poll on SQL Server with the `system_health` ring buffer at its ~4 MB cap (2.1 M characters, 163 events), throwaway probe in the test host, 12 polls each | Every poll read the whole ring buffer although the event-file cursor had nothing new: **~65 MB allocated and ~0.7 s per poll** (the ring read alone ~43 MB: SqlClient's async read rented ~2,000 `char[]` buffers of 128 K–4 M elements from `ArrayPool` as the packets arrived, and the pool kept the largest). A heap dump after the reads held 16.5 MB reachable while `GC.GetTotalMemory(true)` read 92 MB: the rest is free space between large objects | **Revised (soak):** the ring buffer is read only when the event files cannot be (failed, cut, given up, no `event_file` target); a poll with nothing new then took 12–31 ms and ~18 KB, and the probe's heap stayed at 15.7 MB. The soak records `FragmentedBytes` beside the heap so the two can be told apart |
| — | `MonitorPanes` as a `[Flags]` enum | The contract generator emits enums as string unions and the wire format writes enums as camelCase strings (`allowIntegerValues: false`); a flags value would travel as `"sessions, locks"` | **Plan revised:** `MonitorPane[] Panes` (one name per pane); unknown names fail JSON binding, reserved `queryStore`/`health` are refused by `monitor.start` (L14). §16 row |
| — | `ActivityRow.OwnTabId` | The sidecar knows session ids, not tab ids | **Plan revised:** `OwnSessionId`; the webview maps it to its tab |

Items 3 (control connection through SSH under an exhausted pool), 4 (identity read cost) and 5 (payload) are
measured with the real `SessionManager` / monitor: see `plans/PHASE-B-IMPLEMENTATION-STATUS.md` (C3, bench 28).

---

## Design decisions

- **Own connection, not the tab's session.** The monitor opens a dedicated connection from the profile
  (like data compare), so a blocked tab session never freezes the monitor. On Prod profiles this holds
  one connection for the monitor's lifetime — accepted.
- **Connection budget per profile, stated honestly.** Steady state: 1 per running monitor. Transient:
  +1 control connection while a preempting kill runs (closed right after), +1 for the shared own-tab
  blocking check (below) for the duration of one query every 10 s. The tab header shows the live count
  ("monitor: 1 connection, 2 during kill").
  - The **kill control connection is never borrowed from Core's pool.** The pool caps at 8 per profile
    and queues borrowers when full, and the sessions a user wants to kill are often the ones blocking
    the tabs that pin it, so a kill that waits on the pool can deadlock against its own target.
    Instead it is opened with `SessionManager.OpenDedicatedAsync` (same SSH tunnel, same remembered
    secrets, outside the pool cap) with a 5 s connect timeout. At most one control connection per
    profile: kills on a profile are serialised, and a second kill waits at most 5 s for the first,
    then fails "another kill is in progress". If the control connection can't be opened (server
    connection limit, timeout), the fallback cancels the running monitor job (monitor jobs are
    read-only, so cancelling one is safe) and runs the kill on the monitor connection. Each path is
    bounded; no path waits on the tab pool.
  - The banner check borrows from Core's pool (same SSH tunnel) with a 1 s bounded wait. If the pool is
    full, the cycle is skipped and its waiter removed, never left queued.
- **Monitor connection settings (applied on open and after every reconnect, asserted in tests).** The
  monitor must never block, be blocked for long, or hold back cleanup:
  - SQL Server: `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000;
    SET DEADLOCK_PRIORITY LOW;` and `Application Name=Querithm Monitor` in the connection string.
  - PostgreSQL: autocommit always (no explicit transaction ever, so the monitor never holds back
    vacuum's `xmin`); `SET statement_timeout = '5s'; SET lock_timeout = '2s';
    SET application_name = 'Querithm Monitor';`. A job with a longer timeout sets `statement_timeout`
    before its query and restores it after (in a `finally`); the scheduler serialises jobs, so no
    other job sees the raised value. `SET LOCAL` is not used — it needs a transaction block.
  - The monitor's own session is recognised by its identity (`ConnectionRegistry`, B1b), not by
    `application_name`, which any client can spoof.
- **One poll loop per monitor, sidecar-driven.** Interval default 2 s, validated 1–30 s. A single
  round-trip per tick collects every enabled pane (sessions, waits, metrics, I/O); slower panes run as
  secondary jobs on their own period or on demand. The loop stops on `monitor.stop`, tab close,
  profile disconnect, or sidecar shutdown.
- **One connection, one scheduler.** All work on the monitor connection goes through a single
  `MonitorScheduler` (`Querithm.Core/Monitoring/`) — ADO.NET connections are not concurrent. It is the
  only place that decides when a job runs. Job classes, highest first:
  1. **Kill / cancel** — preempts: when the monitor connection is idle it runs there; if any job is
     running it runs on the dedicated control connection (see "Connection budget"), never on a pool
     connection and never queued behind a job.
  2. **Interactive** — `requestDetail`, `killPreview`, `diagnose` when it needs fresh data, on-demand
     locks/deadlocks refresh. Starts as soon as the connection is free, ahead of the next tick (the
     user is waiting); a tick it delays reports `lagMs`. One pending instance per kind: a new request
     (say, detail for a different row) replaces the pending one, and the old one fails "superseded".
     If it hasn't started after 5 s of queue time (e.g. behind a long deadlock read), it fails
     "monitor busy, try again" and is removed. It is never left pending.
  3. **Main tick** — every interval (sessions, waits, metrics, I/O). Never queued twice: a late tick
     runs once, never as catch-up, and its delay is reported as `lagMs`.
  4. **Secondary periodic** — locks and top queries (×5 interval), deadlock poll (every 30 s while the
     pane is open), lock-name resolution (B2). **After every main tick, at most one due secondary job
     runs before the next tick, the most overdue first** — even if the next tick is already due. That
     one rule is the whole starvation guarantee: secondary jobs are due less than once per tick in
     total (2/5 + 1/15 at defaults), so each runs within its period plus a few ticks, and on a slow
     server each still runs at least once per 2 × its period (C7, C8). A secondary job that runs past
     the next tick delays it (`lagMs`); at defaults the longest (deadlock read, ~2 s) fits after a
     ~100 ms tick within metric 28's lag budget.
  - Running jobs are never interrupted except by stop, their own timeout, or the kill fallback.
  - Per-job timeouts: tick 5 s, locks/top queries 5 s, deadlocks 10 s, detail 5 s. Timeouts only bound
    execution, start when the job starts, and never decide when it runs. A timeout cancels only that job.
  - Budget: if scheduled work exceeds 50 % of wall time over 10 s, the lowest-priority periodic job is
    halved in frequency and the header shows "monitor throttled to protect the server".
- **Payload: JSON notifications, decided.** Activity data is bounded server status, not a user query
  result set, so the Arrow-only rule (which covers query rows) does not apply. Caps: 1,000 sessions,
  2,000 locks, 100 top queries, 64 wait categories per snapshot; truncation flagged. Add a §16 row
  stating this and the caps.
- **Rows in full, texts once — measure before diffing.** Query texts dominate the payload (at the caps,
  4 KB previews are up to ~4.4 MB), so texts are sent once; rows are small and are sent in full.
  - Every snapshot carries the full `Sessions` list (≤ cap) and `Seq`. The UI replaces its row set and
    ignores a snapshot whose `Seq` isn't newer than the last one applied. There are no keyframes and
    no resync protocol: a lost notification costs one tick and leaves nothing to repair.
  - Rows carry `TextId`, which identifies the **exact** text shown. On both engines it is a content
    hash (SHA-256 from the BCL, first 16 bytes, hex) of the preview text exactly as the server
    returned it this tick. It is never a server-side key:
    - PG `query_id` is normalised, so queries that differ only in literals share an id. Keying text
      by it would show one session's SQL on another session's row and in the kill dialog.
    - SQL Server `sql_handle` + offsets is not immutable either. For stored procedures, triggers and
      functions the handle derives from database and object identity, not from the text, so after
      `ALTER PROCEDURE` the handle and even the statement offsets can stay the same while the text
      changes.
    Server keys travel separately for grouping only (`QueryHash` = PG `query_id` / SQL Server
    `query_hash`). Hashing cost: per session the sidecar compares the text with the previous tick's
    (ordinal) and rehashes only when it changed. That cost must fit metric 28's 10 ms sidecar budget
    and is measured there. Any sidecar text cache is keyed by `TextId` (content), never by `sql_handle` or
    `query_id`, so it cannot serve stale text.
  - **Text store, bounded on both sides.** Each monitor has one sidecar `TextStore` (`TextId` →
    preview, ≤ 4 KB UTF-8 each), reference-counted:
    - **Live references** are texts named by a row in the latest snapshot (sessions, top queries).
      They are never evicted, and the snapshot caps bound them: (1,000 + 100) × 4 KB ≈ 4.4 MB.
    - **History references** (B6) are texts named by a retained history tick. They are kept while
      that tick is retained, and their bytes count against B6's 50 MB cap, so history eviction
      frees them.
    - **Unreferenced texts** (e.g. ended sessions) stay in an LRU capped at 4 MB, oldest evicted
      first. With literals changing every tick, the store stays at live + history + 4 MB. It never
      grows with uptime. **Revised (measured, B0 row 18):** sizes are heap bytes (UTF-16 characters
      plus a measured 360 B per text), not UTF-8 length, which understated a short text ~6×.
    - "Sent once" means once while the id stays in the store. A text that was evicted and appears
      again is new to the store and is sent in `Texts` again.
    - The UI keeps an LRU of 5,000 texts or 8 MB, whichever it hits first, and asks
      `monitor.texts(ids)` (≤ 500 ids, each 32 hex chars, validated at the RPC boundary) for any
      text it lacks — after its own eviction, or when a notification carrying the text was lost. The
      reply is `MonitorTextsResult { Dictionary<string, string> Texts; string[] Missing; }`. A live
      row's text is always present, because live references are never evicted. For an id in
      `Missing`, the UI shows "text no longer available (session ended)" and never asks again for
      that id.
  - Budgets (metric 28): ≤ 512 KB per tick for 500 sessions when no new texts arrive; first snapshot
    (all texts) ≤ 2.5 MB. The 16 ms UI and 10 ms sidecar budgets decide, not the byte count.
  - **Escalation, only on a measured miss.** If B0 or bench 28 shows full rows missing the UI or
    payload budget, switch sessions to diffs: `SessionsUpserted` (new or changed rows, keyed by
    `SessionKey`), `SessionsEnded` (keys), a keyframe every 30 ticks, and `monitor.resync` on a `Seq`
    gap. That contract change ships in the same change as the failing bench result, citing it
    (CLAUDE.md §4 "Profile before optimizing"); it is not built speculatively.
- **Query text is user data.** Snapshots carry a 4 KB preview; full text only on demand (B5). Never
  logged, never in crash dumps. History (B6) is in-memory only unless the user exports it.
- **Session identity = provider-defined key, exact to the server's own precision.** Spids are
  reused, so every action targets a `SessionKey { long Id; string Instance; }`:
  - `Id` is what users see (spid / pid / processlist id). `Instance` is an opaque string the
    provider builds and parses; Core and the UI only compare it for equality and never interpret it.
  - SQL Server: `Instance` = `login_time` serialised losslessly from `SqlDateTime` (its ~3.33 ms
    `datetime` ticks), and bound back as a `datetime` parameter — never via `DateTime` milliseconds,
    epoch ms or a JS number.
  - PostgreSQL: `Instance` = `backend_start` as its exact microsecond value (`timestamptz` → `long`
    microseconds since the PG epoch, as text), bound back as `timestamptz`.
  - Phase D note: MySQL has no session start time in the processlist; its provider will use
    `performance_schema.threads.THREAD_ID` (never reused) as `Instance`. The opaque shape exists so
    that engine fits without a contract change.
  - The provider validates `Instance` (max 64 chars, its own format) at the RPC boundary; a value
    that does not parse is rejected before any SQL runs. Identity is re-validated on the server
    immediately before acting.
- **Deltas are computed in the sidecar.** Cumulative counters (wait stats, I/O, batch req/s, TPS,
  query stats) are diffed against the previous tick; a counter reset (server restart, `DBCC SQLPERF
  CLEAR`, `pg_stat_reset`) yields "reset detected" for one tick instead of negative values.

## Shared contract (`sidecar/Querithm.Contract/Monitor.cs`, new)
```
record MonitorStartParams { string ProfileId; int IntervalMs; MonitorPane[] Panes; string? DatabaseFilter; }
enum MonitorPane { Sessions, Locks, Waits, Metrics, Io, TopQueries, Deadlocks,
  QueryStore, Health }   // last two reserved for deferred B8; rejected by monitor.start in Phase B
// B0: an array of names, not [Flags] — enums travel as camelCase strings and the generator emits string unions.
record MonitorRef { Ulid MonitorId; }
record MonitorSnapshot { Ulid MonitorId; long At; long Seq;
  ActivityRow[]? Sessions;            // full list every tick
  Dictionary<string, string>? Texts;  // TextId → preview, only ids not already sent while in the TextStore
  LockRow[]? Locks; WaitDelta[]? Waits; WaitCoverage? WaitCoverage; MetricSample? Metrics;
  IoDelta[]? Io; TopQueryRow[]? TopQueries;
  DeadlockEvent[]? NewDeadlocks;   // only events not sent before; full list via monitor.deadlocks
  PaneStatus[] Status;  // per pane: ok | truncated | degraded(reason) | error(message) | resetDetected
  long LagMs; int ConnectionCount; bool Throttled; }
record PaneStatus { MonitorPanes Pane; string State; string? Reason; }
record WaitCoverage { string Source; int PeriodMs; bool Partial; }  // PG tier 2 (tick-sampled) only; null otherwise
```
The test "every `MonitorPane` name is distinct and `queryStore`/`health` are refused" guards the list (U). B8, if picked up,
adds `HealthCard[]? Health` (Query Store stays on demand via `monitor.queryStore`, never in snapshots).
`DatabaseFilter` filters rows; it never changes the monitor connection's database (matters on Azure SQL,
where the connection's database scopes the DMVs — see degradation).

RPC: `monitor.start` → `MonitorRef`, `monitor.stop`, `monitor.setPanes`, `monitor.setInterval`,
`monitor.pause`/`resume`, `monitor.texts`, `monitor.kill`, `monitor.killPreview`, `monitor.diagnose`,
`monitor.waitInfo`, `monitor.deadlocks` (B7), `monitor.requestDetail` (B5), `monitor.history`,
`monitor.export` (B6); notification `monitor.snapshot`.
Deferred with B8: `monitor.queryStore`.

## Provider interface
`sidecar/Querithm.Contract/Providers/IActivityProvider.cs` (new), exposed as `IDbProvider.Activity`
(nullable → whole monitor degrades with reason). Each pane method may return `Degraded(reason)`.
```
Task<MonitorCapabilities> ProbeAsync(DbConnection c, CancellationToken ct);  // perms, version, Azure, extensions
Task<SessionKey> IdentityAsync(DbConnection c, CancellationToken ct);        // own session's key; needs no permission
Task<PaneResult<ActivityRow[]>> SessionsAsync(DbConnection c, int max, CancellationToken ct);
Task<PaneResult<LockRow[]>> LocksAsync(...); Task<PaneResult<WaitCounter[]>> WaitsAsync(...);
Task<PaneResult<MetricCounters>> MetricsAsync(...); Task<PaneResult<IoCounter[]>> IoAsync(...);
Task<PaneResult<TopQueryCounter[]>> TopQueriesAsync(...);
Task<KillResult> CancelRequestAsync(DbConnection c, SessionKey key, string? statementStart, CancellationToken ct);
Task<KillResult> TerminateSessionAsync(DbConnection c, SessionKey key, CancellationToken ct);
Task<RequestDetail> RequestDetailAsync(DbConnection c, SessionKey key, CancellationToken ct);
```
**As implemented** (refinement, same responsibilities): `IActivityProvider` keeps the stateless parts (`IdentityAsync`,
`TryParseInstance`, `MonitorSessionSetupSql`, wait categories, `BlockedByAsync` for the banner) and `ProbeAsync` returns an
`IActivitySession` bound to one monitor connection: it holds the probe result (`Capabilities`, `StartRefusal`, `Self`) and
the per-connection caches (PG statement texts), and exposes one `TickAsync` (sessions + waits + metrics + I/O in one
round trip) plus the secondary jobs, kill and detail calls. A reconnect probes again and gets a new session object, so no
capability outlives the connection it was probed on.

`ProbeAsync` runs once at start and on reconnect; it decides which panes are available and why not.
It probes the actual roles, views, columns and extensions — never a version string or a cloud
platform's name. Each provider also owns `SessionKey.Instance` formatting/parsing (`TryParseInstance`)
and the connection-settings batch above (`MonitorSessionSetupSql`). `IdentityAsync` reads the
connection's own row (SQL Server `sys.dm_exec_sessions WHERE session_id = @@SPID`; PG `pg_stat_activity
WHERE pid = pg_backend_pid()`), which every login can see.

## Core (`sidecar/Querithm.Core/Monitoring/`, new)
- `MonitorService.cs` — owns monitors (start/stop, cap 8 per sidecar), reconnect with back-off,
  `ProbeAsync` re-run; `IAsyncDisposable`; `CancellationToken` through every call. Holds no timing
  logic of its own: when jobs run is `MonitorScheduler`'s business.
- `MonitorScheduler.cs` — the per-connection job queue described in "One connection, one scheduler".
- `ConnectionRegistry.cs` (B1b) — `SessionKey` → Querithm connection, filled by
  `SessionManager.OpenConnectionAsync`.
- `TextStore.cs` — the reference-counted text store described in "Payload".
- `DeltaTracker.cs` — per-counter previous values, reset detection.
- `BlockingGraph.cs` — builds head-blocker forest from `BlockedBy[]`; drops self-edges (SQL Server
  parallel exchange waits), detects cycles (deadlock in progress) and marks them instead of recursing.
  Non-session blockers (`BlockedByNote`, see B1a) become synthetic root nodes with their reason.
- `HistoryBuffer.cs` (B6) — bounded ring buffer.
- `KillGuard.cs` — see B1c.

## Degradation matrix (each row gets a test)
| Condition | Effect / user-visible reason |
|---|---|
| SQL Server without `VIEW SERVER STATE` (2022: `VIEW SERVER PERFORMANCE STATE`) | Sessions: own only; waits/metrics/file I/O/top queries disabled — "requires VIEW SERVER STATE" |
| Azure SQL Database (`SERVERPROPERTY('EngineEdition') = 5`) | DMVs scoped to the connected database (`VIEW DATABASE STATE`); server-wide panes disabled with reason; `DatabaseFilter` fixed to the connected DB |
| SQL Server: cancel a single request | **Not possible in T-SQL** — `CancelRequest` disabled with reason "SQL Server can only terminate the whole session"; only own-tab requests can be cancelled, from the tab |
| SQL Server: no `ALTER ANY CONNECTION` | Terminate disabled, reason shown |
| Azure SQL Database: no `KILL DATABASE CONNECTION` (it replaces `ALTER ANY CONNECTION` there) | Terminate disabled, reason names the Azure permission |
| Azure SQL Database: CPU | `sys.dm_os_ring_buffers` unavailable → CPU % read from `sys.dm_db_resource_stats` (15 s granularity, labelled "database CPU, 15 s"), not disabled |
| Managed SQL Server (Azure SQL Managed Instance — `EngineEdition = 8` — and RDS for SQL Server) | Treated as SQL Server; permissions probed as on-premises; the edition is used only to word reasons. An unreadable `system_health` event file → ring buffer only (X9) |
| SQL Server CPU from `sys.dm_os_ring_buffers` | Undocumented DMV: parsed defensively; unexpected shape → CPU "n/a (ring buffer format)", other metrics continue |
| SQL Server `blocking_session_id` < 0 (-2 orphaned distributed tx, -3 deferred recovery tx, -4 latch owner not determinable) | Not a session: shown as a synthetic blocker node with that reason; never killable |
| PG 14+ `query_id` null because `compute_query_id` is `off` (or `auto` without `pg_stat_statements` loaded) | `QueryHash` null, so sessions are not grouped by query; "query id unavailable (compute_query_id)". `TextId` is unaffected (it is always a content hash) |
| PG `pg_stat_io` (16+) | Cluster-wide only, by backend type / object / context, with no database or file. I/O rows have `Scope = "cluster"`, `DatabaseFilter` does not apply to them (pane says so). Stall times null with "requires track_io_timing" when it is off. PG < 16 → I/O pane degraded with reason |
| PG 17+ `pg_stat_statements` renames (`blk_read_time` → `shared_blk_read_time`, `blk_write_time` → `shared_blk_write_time`) | Mapped by probed columns, not by version string |
| PG without `pg_read_all_stats` / superuser | Other users' query text and waits hidden — "requires pg_read_all_stats" |
| PG without `pg_read_all_stats`: other roles' rows have NULL `backend_start` / `backend_type` / `state` (B0 row 4) | Row shown with pid, user, application and client only; `SessionKey.Instance` empty and `IdentityHidden` = "requires pg_read_all_stats"; never killable, even with `pg_signal_backend` (the identity re-check cannot run) (K29) |
| PG without `pg_signal_backend` / same role | Cancel/terminate of other roles' backends disabled; superuser backends never signalable by non-superusers |
| Managed PostgreSQL (RDS/Aurora, Cloud SQL, Azure Flexible Server) | No superuser. Kill rights come from the platform's admin role where it grants `pg_signal_backend`; platform-owned backends (e.g. `rdsadmin`) run as superuser and are refused by the server (K18). Extension availability (`pg_stat_statements`, `pg_wait_sampling`) varies by platform. All of it is decided by `ProbeAsync` from actual roles and extensions, never from the platform's name. No managed fixture: tested with fake probe results (D18) |
| PG without `pg_stat_statements` | Top queries disabled — "extension pg_stat_statements not installed" |
| PG `pg_stat_statements` installed but not in `shared_preload_libraries` | Querying the view errors → degraded "pg_stat_statements is not loaded (shared_preload_libraries)" |
| PG without `pg_wait_sampling` | Waits estimated from the main tick's session sample, one sample per interval, labelled "tick-sampled, low resolution" with install hint |
| PG `pg_wait_sampling` installed but not loaded / `profile_queries` off | Falls back to tick sampling, reason shown |
| Read-only replica / hot standby | Monitoring works; `pg_stat_statements` and kill still allowed; panes that need the primary say so |
| Server version below minimum (SQL Server < 2016, PG < 12) | Monitor refuses to start with reason |
| SQL Server `system_health` session stopped/dropped, or ring buffer and files unreadable | Deadlock history degraded "system_health session not running" |
| SQL Server deadlock XE files on a path the server account can't read / Azure SQL DB | Use ring buffer only (Azure: `sys.fn_xe_telemetry_blob_target_read_file` unavailable → degraded with reason) |
| PG deadlock history | No server-side history in core → degraded "PostgreSQL keeps no deadlock history; enable log_lock_waits and read the server log"; only the `pg_stat_database.deadlocks` counter is shown |
| Query Store off, `READ_ONLY` due to size, or < 2016 | Query Store pane degraded with exact state (`actual_state_desc`, `readonly_reason`) |
| No `VIEW DATABASE STATE` on target DB (Query Store) | Degraded per database |
| PG Query Store equivalent | None in core → pane hidden with reason; top queries (B4) remain |
| No AlwaysOn / replication configured | Health card shows "not configured", not an error |
| PG `track_activity_query_size` truncation | Text marked "truncated by server setting (N bytes)" |
| PG < 14 (no `query_id`), SQL Server live plan off | Corresponding columns/features hidden with reason |
| Target is one of Querithm's own connections | Kill/cancel replaced by **Go to tab** (or nothing, for an idle pool connection), reason "this is a Querithm connection — cancel or disconnect it from its tab" |

---

## B1a — Sessions (read-only)

### Columns (`ActivityRow`) — `sp_WhoIsActive`-level
```
record SessionKey { long Id; string Instance; }                 // see "Session identity" above
record ActivityRow { SessionKey Key; string? Login; string? Host; string? Program; string? Database;
  string Status; string? Command; string? BackendType;            // PG: client backend, autovacuum, …
  bool IsUserSession;                                            // SQL Server is_user_process; PG backend_type = 'client backend'
  string? WaitType; string? WaitResource; long? WaitMs;
  long[] BlockedBy;                                              // both engines: 0..n session ids
  string? BlockedByNote;                                         // SQL Server negative blocker: orphanedDtc | deferredRecovery | latchOwnerUnknown
  string? TextId; string? QueryHash;                             // TextId = content hash of the exact text, sent once in MonitorSnapshot.Texts; QueryHash = grouping fingerprint (query_hash / query_id)
  long? ElapsedMs; long? TxElapsedMs; long? StateChangeMs;       // request start / xact start / last state change
  string? StatementStart;                                        // opaque, exact server value (PG query_start µs, like SessionKey.Instance); identifies the running statement for cancel
  long? CpuMs; long? Reads; long? Writes; long? LogicalReads; long? RowCount;
  int? OpenTx; double? PercentComplete;                         // BACKUP/DBCC/index rebuild
  string? QuerithmKind;                                          // B1b: null = not Querithm's | pool | dedicated | monitor | control
  string? OwnSessionId;                                          // B1b: session currently leasing this pool connection (display only; the webview maps it to its tab)
  string? IdentityHidden;                                        // B0: why Key.Instance is empty (PG without pg_read_all_stats); never killable
  long? TempdbKb; long? GrantedMemoryKb; int? Dop; }             // SQL Server only
```
- **SQL Server** (`SqlServerActivity.cs`): `sys.dm_exec_sessions` ⟕ `sys.dm_exec_requests` ⟕
  `sys.dm_exec_sql_text` (statement offsets) ⟕ `sys.dm_tran_session_transactions`/`dm_tran_active_transactions`
  ⟕ `sys.dm_db_session_space_usage`/`dm_db_task_space_usage` ⟕ `sys.dm_exec_query_memory_grants`.
  Blockers come from `sys.dm_os_waiting_tasks` (one row per waiting task), not only
  `dm_exec_requests.blocking_session_id`, so a parallel request blocked by several sessions lists all of
  them (distinct). Self-blocking (`blocking_session_id = session_id`) normalised to none; negative
  values map to `BlockedByNote` (degradation matrix), never into `BlockedBy`.
- **PostgreSQL** (`PostgresActivity.cs`): `pg_stat_activity` (`backend_start`, `xact_start`,
  `query_start`, `state_change`, `wait_event_type`, `wait_event`, `backend_type`, `query_id` on 14+);
  `pg_blocking_pids(pid)` **only for rows with `wait_event_type = 'Lock'`** (it is expensive; PG docs
  warn against frequent calls).

### UI (`src/monitor/`, new)
- `ActivityTab.tsx` — tool tab (`src/store/toolTabs.ts`), opened from explorer server node →
  "Activity monitor" and a palette command.
- **Layout:** header (profile, `ConnectionCount`, interval picker, pause/resume, lag and "throttled"
  notices) → Overview strip (B3) → tabbed panes (Sessions | Blocking | Locks | Waits | I/O | Top queries
  | Deadlocks; each tab appears with its slice) → a resizable side panel for detail and diagnosis,
  closed by default.
- **Pane states**, driven by `PaneStatus` and the same in every pane:
  - loading — first tick not back yet: "collecting…", no empty-state message;
  - empty — "No other sessions" / "No blocking right now";
  - paused — last data dimmed, "Paused at <time>";
  - degraded — the reason and the permission or setting that would enable it;
  - error — the message and "retrying in N s";
  - truncated — "showing the first 1,000 of N, by priority";
  - monitor disconnected — every pane dimmed, reconnect countdown.
- `SessionsPane.tsx` — Glide grid; rows kept in a ref; each snapshot is compared per `SessionKey`
  with the previous row and only changed cells are passed to Glide as damage regions, so there is no
  tab-level re-render. Column chooser (defaults: spid, login, db, status, wait, blocked by, elapsed,
  cpu, reads, text). Filters: hide idle / hide system / database / login / program. Selection kept by
  `SessionKey` across ticks. Header counts: running, blocked, sleeping with open tx, head blockers.

Tests: S1–S18, M1–M14, L1–L15, C1, C6–C8, C12–C14, U1, U2, U5, U9.

## B1b — Own-tab awareness + blocking banner

### Querithm's own connections are shown, never killed from the monitor
Killing a tab's connection from the monitor would race the tab's lease (borrow, return, pin), and
making that safe needs a hold state machine inside `Pool`. The tab already has Cancel and Disconnect
with the right transaction semantics. So a Querithm connection's row offers **Go to tab** instead of
cancel/kill (an idle pool connection offers nothing: the pool's idle sweep closes it), and `KillGuard`
refuses it (B1c step 5). This keeps `Pool` and its lock unchanged.

- **Identity registry.** `SessionManager.OpenConnectionAsync` is the single open path for pooled and
  dedicated connections. Right after open, before the connection is returned to anyone, it calls
  `IActivityProvider.IdentityAsync` and records the result in `ConnectionRegistry` (`SessionKey` →
  weak reference to the connection, plus its kind):
  - `pool` for pool opens, `dedicated` for `OpenDedicatedAsync` (debugger and similar). The monitor
    relabels its own connections `monitor` and `control`.
  - One round trip per physical open, never per execution; `session.open` still dials nothing. B0
    measures the cost on the "execute → first batch" path.
  - A driver reopen goes through the same path, so the new identity is recorded and the old key maps
    to nothing.
  - The own-row query needs no permission on either engine, so a failed identity read normally means the
    connection failed: it is treated as an open failure. Engines without `IActivityProvider` skip it.
    **Revised (decision, 2026-10-29):** when the server refuses the read itself on a working connection (an explicit
    `DENY`, `pg_stat_activity` revoked, a compatible server without the view or column), a tab or dedicated connection is
    kept unidentified and serves queries; the monitor refuses every kill while one is open (`querithmUnidentified`) and
    the banner tells that tab it cannot check it. The monitor's own connections must identify themselves (K6): a monitor
    whose read is refused stops with the reason. ARCHITECTURE §6 rule 7, §16.
  - Entries for closed or collected connections are pruned on lookup and on the pool's idle sweep.
  - **Registration window.** Between server login and the identity read, a new connection is visible
    on the server but not yet registered. If a user kills it in that window, the identity read fails
    and the open fails like any connection failure: the slot is released and the borrower's execution
    reports the error, with nothing orphaned (K9).
- **Owner, for display only.** `PhysicalConnection.LeasedBy` (session id, null when idle) is set in
  `SessionManager.AcquireAsync`, kept while pinned, and cleared when `Release` or unpin returns the
  connection to the pool. Snapshot rows get `QuerithmKind` from the registry and `OwnSessionId` from
  `LeasedBy` at snapshot time. It is read without a lock: a badge one tick stale is harmless because
  no action depends on it. `Session.Spid` stays display-only and is never used for ownership.
- UI: own-tab rows and tree nodes show a "your tab: <tab title>" badge and **Go to tab**; idle pool
  connections show "Querithm pool (idle)"; other kinds show "Querithm (<kind>)". Kill/cancel is
  disabled on all of them, with the reason as tooltip.

### Blocking banner in the editor tab
- When a monitor is running for that profile, or — without a monitor — via a cheap check the tab
  session itself runs every 10 s **only while it has an open transaction** (SQL Server
  `@@TRANCOUNT > 0`, PG transaction state from the driver), the sidecar checks whether that session
  blocks others (`sys.dm_exec_requests.blocking_session_id = @@SPID`; PG `pg_blocking_pids` of waiters
  containing our pid). **One shared check per profile**, not per tab: a single query covers every tab
  session of that profile with an open transaction (their ids as a parameter array), on a connection
  borrowed from Core's pool for that profile (bounded 1 s wait; pool full → cycle skipped) and
  returned right after — at most one extra connection per profile, for one query every 10 s. Never
  runs on a tab session; tabs that are executing are left out of that cycle. When a monitor is
  running for the profile, its snapshot answers the question and no extra check runs.
- If blocking ≥ 5 s: non-modal banner in that tab — "This tab's open transaction is blocking N
  sessions for 45 s — **Commit** / **Rollback** / Open monitor". Commit/Rollback use the tab's existing
  transaction commands (same guards). Banner disappears when blocking stops; dismiss mutes it for 5 min.
- Engines/permission without visibility of other sessions → no banner, setting shows the reason.

Tests: O1–O19, K7–K9, U8.

## B1c — Guarded kill + kill preview

### Kill (`monitor.kill`)
```
record MonitorKillParams { Ulid MonitorId; SessionKey Target; KillMode Mode;
  bool ConfirmedKill; bool ConfirmedProd; bool ConfirmedLongRollback;
  string? ExpectedStatementStart; } // CancelRequest only: the row's StatementStart as the dialog showed it; required for PG cancel
enum KillMode { CancelRequest, TerminateSession }
```
`KillGuard` in the sidecar, in order:
1. `ConfirmedKill` required, always.
2. Prod environment → `ConfirmedProd` required (`GuardReason.ProdWrite`).
3. Target must be in the latest snapshot (exact `SessionKey` equality) **and** re-checked on the
   server in the same statement/batch as the kill, with `Instance` bound as a typed parameter in the
   server's native precision:
   - PG: `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid = $1 AND backend_start = $2
     AND backend_type = 'client backend'` (`$2` `timestamptz`).
   - SQL Server: see the `KILL` batch below.
   Mismatch → refused, "session ended or ID reused".
4. Monitor's own session → always refused.
4a. Not a user session (SQL Server `is_user_process = 0`, PG `backend_type` ≠ `client backend`) →
   refused "system session". The old `spid > 50` heuristic is **not** used: on large servers system
   sessions exceed 50.
5. Querithm's own connection — the target is in `ConnectionRegistry` **at kill time**, any kind →
   refused "this is a Querithm connection — cancel or disconnect it from its tab", nothing sent. The
   registry is read at kill time, never taken from the snapshot, so a connection registered after the
   dialog opened is still refused (K8).
6. Mode unsupported by engine (SQL Server `CancelRequest`) → refused with the degradation reason.
7. `TerminateSession` with rollback risk **high** (recomputed server-side at kill time, not taken from
   the client) → requires `ConfirmedLongRollback`.
- SQL Server `KILL` is not parameterisable: the spid comes from `SessionKey.Id` (validated
  1–32767 at the RPC boundary, present in the snapshot after step 3) and is formatted with
  `CultureInfo.InvariantCulture` as an integer literal; everything else is a parameter:
  ```
  IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions
             WHERE session_id = @id AND login_time = @start AND is_user_process = 1)
      KILL <id literal>;
  ELSE SELECT 'gone';
  ```
  `@start` is a `SqlDbType.DateTime` parameter built from `Instance`. Document in §16.
- After terminate, report rollback progress on following ticks (SQL Server) — long rollbacks are the common
  surprise. B0 row 2: a killed *running* request shows `status = 'rollback'` with `percent_complete`; a killed
  *sleeping* session has no request row while it rolls back, so the monitor remembers the keys it terminated and,
  while such a session still exists, reads progress with `KILL <spid> WITH STATUSONLY` (info message 6109), in a
  batch guarded by the same identity predicate. A repeated kill of a session that is rolling back is refused by
  `KillGuard` (the server would accept it silently).
- **PG cancel keeps the session and targets the statement the dialog showed.** `pg_cancel_backend`
  stops the current query; `pg_terminate_backend` ends the session. The cancel runs
  `pg_cancel_backend(pid)` only `WHERE pid = $1 AND backend_start = $2 AND state = 'active' AND
  query_start = $3`, with `$3` bound as `timestamptz` from `ExpectedStatementStart` (validated and
  parsed by the provider like `Instance`). A later statement on the same backend is never cancelled by
  an old request: a mismatch returns "statement already finished", nothing sent. PG delivers the
  signal asynchronously, so the window can't be closed entirely, only narrowed to the gap between the
  check and signal delivery — the same race PG's own cancel protocol has. A cancel against an idle or
  idle-in-transaction backend returns "no running query", nothing sent.

### Kill safety preview (in the kill dialog)
- Before `TerminateSession`, `monitor.killPreview(MonitorRef, SessionKey)` returns the rollback cost:
  SQL Server — `database_transaction_log_bytes_used` / `_reserved` and `database_transaction_begin_time`
  from `sys.dm_tran_database_transactions` (summed per session), tx count, and whether the session is
  already rolling back; PG — `xact_start` age, `backend_xid`/`backend_xmin` present (writes vs read-only),
  and whether the session holds locks others wait on.
- Shown as a risk level: **none** (no open tx) / **low** (< 100 MB log and < 10 min tx; revised: the earlier "< 1 min"
  left 1–10 min undefined) / **high** (≥ 100 MB or ≥ 10 min) / **unknown** (no permission → reason). Thresholds are fixed constants with tests.
  Rollback time is shown only as "may take long" — never a fabricated time estimate.
- High risk requires an extra checkbox ("I understand the rollback may take a long time and hold
  locks") — enforced in the sidecar as `ConfirmedLongRollback` on `MonitorKillParams`.
- Preview is advisory; the identity re-check (`KillGuard` step 3) still runs at kill time, and the
  preview result is never trusted from the client.

### UI
- Kill dialog: login, host, program, text, tx age, rollback risk; second confirm required; Prod needs
  typed confirmation; high risk adds the rollback checkbox. Kill/cancel buttons disabled with the
  degradation reason as tooltip.

Tests: K1–K6, K10–K28, P1–P9, C2–C5, C9–C11 (the kill preview is the first interactive job), U3, U4.

## B2 — Blocking tree + locks
- `BlockingTree.tsx` — head blockers as roots (including sleeping sessions with open tx), blocked
  sessions as children; PG multi-blocker → shown under each blocker; cycles shown as "deadlock in
  progress" group. Colour by wait: < 5 s, 5–30 s, > 30 s (theme tokens). Per-node actions: cancel, kill
  (B1c dialog; Go to tab for Querithm connections), copy SQL, open detail (B5). Rendered as a
  virtualised list; a node with more than 50 children shows the first 50 and "+N more"; expand state
  is kept by `SessionKey` across ticks.
- Locks pane (`LockRow { SessionKey; string ResourceType; string? Database; string? Object; string Mode;
  string Status /* granted|waiting */; long Count; }`):
  SQL Server `sys.dm_tran_locks` (aggregated by session/resource/mode); PG `pg_locks`.
  On-demand or ×5 interval.
  **PG name resolution is guarded by database.** `pg_locks` is cluster-wide, but `pg_class` only
  describes the connected database (plus shared catalogs), and relation OIDs from other databases
  can collide with unrelated local objects. So `pg_locks` joins `pg_class` only where
  `l.database = (SELECT oid FROM pg_database WHERE datname = current_database())` or
  `l.database = 0` (shared relations). Other rows show `<datname>.<oid>`, with `datname` from
  `pg_database`, which is cluster-wide. Their names come only from the existing metadata cache (§8),
  which is keyed by (profile, database, object id), when that database is already cached ("name from
  metadata cache"). The monitor never opens a connection to another database to resolve a name
  (connection budget). SQL Server object names are **not** resolved with `OBJECT_NAME(id, db_id)`
  in the lock query — it takes metadata locks and can hang behind a schema-modification lock, i.e.
  exactly when the monitor is needed. Names come from the existing metadata cache (§8) for that
  database; ids missing from the cache are shown as `db.object_id` and resolved lazily in a secondary
  job under the monitor's `LOCK_TIMEOUT` (timeout → id stays, "name locked").
- Tests: B1–B12, LK1–LK4, U7, U10.

## B3 — Waits + overview metrics + I/O
- **Waits** (`WaitDelta { string Category; string WaitType; long WaitMsDelta; long TasksDelta; long SignalMsDelta; }`):
  SQL Server `sys.dm_os_wait_stats` deltas, benign waits excluded (fixed list in code, with test),
  grouped into categories (CPU, Lock, Latch, Buffer IO, Log IO, Network, Memory, Parallelism, Other).
  PG core has no cumulative wait counters, so PG waits use a tiered source, picked by `ProbeAsync`
  and shown in the pane header (`WaitCoverage.Source`):
  1. **`pg_wait_sampling` installed** (preferred): read `pg_wait_sampling_profile` (in-server sampler,
     default 10 ms period, cumulative per `event_type`/`event`) and diff it like SQL Server's
     `dm_os_wait_stats`. `Source = "server-profile"`. Accuracy is set by the extension's
     `pg_wait_sampling.profile_period` (read and shown); profile resets
     (`pg_wait_sampling_reset_profile()`) go through `DeltaTracker` reset detection. Never call the
     reset function ourselves — it is server-wide and would wipe other tools' data.
  2. **Extension missing — tick-sampled, no extra query.** The main tick's sessions query already
     returns `wait_event_type`/`wait_event` for every active backend, so the sidecar derives waits
     from it: each active, waiting backend counts as `min(time since the previous tick, 2 × interval)`
     of its wait. Gaps beyond that are never extrapolated. `Source = "tick-sampled"`,
     `PeriodMs` = the interval. The pane says this is a low-resolution estimate that misses waits
     shorter than the interval, and offers "install pg_wait_sampling for in-server 10 ms sampling". On
     PG the sessions query therefore runs whenever Waits is enabled, even with the Sessions pane
     hidden. If the sessions cap truncated the active rows, `Partial` is set and shown.
     - A dedicated sub-second client sampler is **not** in Phase B: it would need its own slots in the
       scheduler and its own load and latency guards. Add it only if users report tier 2 is too
       coarse.
  3. **No `pg_read_all_stats`**: only own sessions are visible → waits pane degraded with reason.
  Exact totals are impossible on PG without an extension; the UI never presents tier 2 as exact.
- **Metrics** (`MetricSample`): SQL Server — CPU % (`sys.dm_os_ring_buffers` scheduler monitor),
  batch requests/s, compilations/s, user connections, page life expectancy (`dm_os_performance_counters`);
  PG — TPS (commits + rollbacks delta), blocks read/hit ratio, active/idle-in-tx connections, temp
  bytes, deadlocks (`pg_stat_database`), checkpoints (`pg_stat_bgwriter` / `pg_stat_checkpointer` 17+).
- **I/O** — the two engines report different shapes, so the row says which one it is and never
  fills an identity the server didn't give:
  ```
  record IoDelta { string Scope;              // "file" (SQL Server) | "cluster" (PG)
    string? Database; string? File;            // Scope = file only
    string? BackendType; string? Object; string? Context;   // Scope = cluster only (pg_stat_io grouping)
    long ReadBytes; long WriteBytes; long? ReadStallMs; long? WriteStallMs; }
  ```
  - SQL Server: `sys.dm_io_virtual_file_stats` ⟕ `sys.master_files`, so `Scope = "file"`, one row per
    database file.
  - PG: `pg_stat_io` (16+) gives cluster-wide aggregates by `backend_type` × `object` × `context`, with
    no database or file, so `Scope = "cluster"`, `Database`/`File` null. Bytes: `reads`/`writes` ×
    `op_bytes` where present, else the `read_bytes`/`write_bytes` columns (probed, not by version).
    Stalls come from `read_time`/`write_time`, which are null when `track_io_timing` is off ("requires
    track_io_timing"). `DatabaseFilter` doesn't apply to cluster rows, and the pane says so. PG < 16
    → degraded with reason. The per-database view PG does have (`pg_stat_database` `blks_read`,
    `blk_read_time`) stays in Metrics and is not passed off as file I/O.
- UI: `OverviewStrip.tsx` — 4–6 sparklines (last 5 min from B6 buffer, drawn on canvas, no chart
  library); `WaitsPane.tsx`, `IoPane.tsx` sortable grids. `IoPane` picks its columns from `Scope`
  (database/file for SQL Server, backend type/object/context for PG), with a "cluster-wide" label for PG.
- Tests: D1–D20, U6.

## B3b — "Why is it slow?" + wait dictionary

### Diagnosis
- `monitor.diagnose(MonitorRef, SessionKey)` → `Diagnosis { Finding[] Findings; string[] Evidence; }`,
  computed in the sidecar by a **fixed rule set** (no LLM, deterministic, offline), using only data the
  monitor already has (snapshot, blocking graph, waits, request detail, kill preview). Rules, first
  match ordered by severity, each yields a sentence + suggested next step:
  - Blocked → by whom, blocker's status/app/tx age; if blocker is idle in tx → "commit or terminate
    the blocker"; if blocker is an own tab → "your tab <title>" with Go to tab.
  - Lock wait with no blocker visible → "blocker hidden (permission)".
  - Long single wait type → explanation from the wait dictionary (below).
  - Waiting for memory grant (`RESOURCE_SEMAPHORE`, SQL Server) → grant size requested vs granted.
  - Parallel skew (`CXPACKET`/`CXCONSUMER` dominating) → DOP shown.
  - High logical reads vs rows → "likely scan; open plan".
  - Plan warnings from the live/cached plan (missing index, implicit conversion, spill) when B5 is
    available.
  - PG: `idle in transaction` with locks, `IO`/`LWLock`/`Lock` event classes, long `xact_start`.
  - Nothing matched → "no obvious cause; running on CPU" with CPU and reads numbers.
- Output shows the evidence values each finding used, so the user can verify it. Text uses
  identifiers as data, never as markup.
- UI: "Why is it slow?" button on a session row and tree node → side panel.

### Wait type dictionary
- `sidecar/Querithm.Core/Monitoring/WaitDictionary.json` (embedded resource, offline): ~150 SQL Server
  wait types and all PG `wait_event_type`/`wait_event` pairs for supported versions — category,
  one-line meaning, common causes, what to check next. Written in-house (no copied third-party text;
  license note in `THIRD-PARTY-NOTICES.md` not needed).
- Exposed via `monitor.waitInfo(engine, waitType)` and bundled into the snapshot only by key. Unknown
  wait → "no description yet" (never fails).
- UI: hover card in sessions grid, waits pane, blocking tree and diagnosis panel.
- Trimming: loaded with `System.Text.Json` source generation (no reflection).

Tests: W1–W10.

## B4 — Top queries
- `TopQueryRow { string QueryHash; string TextId /* text via MonitorSnapshot.Texts */; long ExecDelta; long CpuMsDelta;
  long DurationMsDelta; long LogicalReadsDelta; long RowsDelta; string? Database; }`, top 100 by
  selected metric over the last interval or since monitor start.
- SQL Server `sys.dm_exec_query_stats` grouped by `query_hash` (Query Store not required); PG
  `pg_stat_statements` (columns differ 13+ — `total_exec_time` vs `total_time`; 17+ renames
  `blk_*_time` → `shared_blk_*_time`; probe columns and map, never branch on version strings).
- **This is the heaviest recurring query in the monitor, so it is bounded:**
  - SQL Server: incremental reads, plus a full read on start and every 30th run to catch evictions.
    Text is fetched only for the final top 100 via `sql_handle`, never for the whole cache. It is
    fetched fresh on each run and hashed into `TextId`, never cached under `sql_handle` (an
    `ALTER PROCEDURE` keeps the handle, see "Payload").
    The cutoff is **not** "`last_execution_time >` previous run". `last_execution_time` is when an
    execution *started*, but the counters change when it *completes*, so a query that spans a poll
    boundary would be missed until the next full read (or lost to eviction first). Instead, each run
    records, in the same batch and in server time, `cutoff = MIN(SYSDATETIME(), MIN(start_time) of
    sys.dm_exec_requests)`, i.e. the earliest start of anything still running. The next incremental
    read takes entries with `last_execution_time >= previous cutoff`. Anything that completes between
    two runs either started after the previous run or was running during it, so its
    `last_execution_time` is at or after the previous cutoff. A very long-running request moves
    the cutoff back and makes incremental reads larger; that is correct, not an error, and bench 31
    measures it. Delta semantics: a long query's whole cost lands in the interval where it completed.
  - PG: `pg_stat_statements(showtext := false)` on every run (a full read, so no cutoff problem);
    text fetched once per (`queryid`, `dbid`, `userid`) into a sidecar cache, so the query file is not
    read every 10 s. The `TextId` it sends is the content hash of that normalised text, the same rule
    as sessions, never the `queryid` itself.
  - Budget in metric 31; measured on a fixture with a 50,000-entry plan cache and
    `pg_stat_statements.max = 5000` full.
  - **Revised (measured, B0 row 17): the tracker's memory is bounded too.** Deltas need the previous
    counters of every cached statement, so the tracker holds one entry per statement: a 128-bit key
    (SQL Server: SHA-256 of the raw plan handle and offsets, computed in the sidecar — `HASHBYTES` in
    the query made the full read 1.7× slower) and seven counters,
    no strings. Query hash and text key are read only for entries that ran since the cutoff. Above
    200,000 entries the pane stops with the reason (and restarts from a full read that fits);
    since-start totals keep the 10,000 most recently active groups and say how many were dropped.
- UI: `TopQueriesPane.tsx` — sort by CPU/duration/reads/executions; "open in new tab" and (B5) plan.
- Tests: T1–T11.

## B5 — Request detail / live plan
- `monitor.requestDetail(MonitorRef, SessionKey)` → full text (cap 1 MB; PG reports
  `track_activity_query_size` truncation), current statement offsets, transaction info, and plan:
  SQL Server live plan via `sys.dm_exec_query_statistics_xml` (lightweight profiling on by default 2019+),
  else cached plan via `sys.dm_exec_query_plan`, else reason → existing `PlanView.tsx`. PG: text only,
  "plan unavailable for running queries"; offer "EXPLAIN this text" into a new tab (never auto-run).
- Tests: plan fallback chain with fakes; identity mismatch → "session ended"; integration: long-running
  query on SQL Server returns a plan; PG returns text with reason.

## B6 — History + export
- `HistoryBuffer` keeps the last 10 minutes of snapshots (configurable 1–60 min, memory-capped 50 MB;
  oldest dropped first, cap hit is shown). Sessions stored in memory as diffs between ticks (an
  internal storage choice, independent of the wire format). **Revised (measured, metric 29):** each tick
  part is held as Deflate-compressed JSON with a keyframe every 10 ticks. Object graphs needed 56 MB for
  only 187 of the 300 ticks (500 sessions changing every tick) and the cap ran on an estimate; compressed,
  300 ticks take 14 MB of heap, the cap counts the bytes held, and scrubbing rebuilds from at most 9 diffs.
- UI: timeline scrubber on the Activity tab — scrub to any past tick and see sessions/blocking/waits as
  they were; "live" button returns to now.
- `monitor.export(range)` → JSON file written to `<appDataDir>/querithm/exports/` and handed to the user
  via the existing save dialog flow (Rust); includes query text only if the user ticks "include query
  text" (user data, §13).
- Tests: H1–H8.

## B7 — Deadlock history
- `DeadlockEvent { string Id /* hash of graph */; long At; DeadlockVictim[] Victims; DeadlockProcess[] Processes; DeadlockResource[] Resources; string? Database; bool Partial; }`
  — processes carry spid, login, host, app, isolation level, lock mode, wait resource and a 4 KB text
  preview; never the raw XML in snapshots.
- **SQL Server:** read `xml_deadlock_report` from the built-in `system_health` session — ring buffer
  (`sys.dm_xe_session_targets`) plus `event_file` via `sys.fn_xe_file_target_read_file` (path from
  `sys.dm_xe_session_object_columns`). **Revised (B0 row 19):** the ring buffer only when the files cannot be read. Read on demand and every 30 s while the pane is open; time
  window filter (default last 24 h, max 7 days); cap 200 events, newest first. XML parsed in the
  sidecar with `XmlReader` (`DtdProcessing.Prohibit`, `XmlResolver = null`, max 5 MB per event).
  Never create, alter or start XE sessions — read-only by design.
- **PostgreSQL:** core keeps no deadlock history → pane degraded with reason; shows
  `pg_stat_database.deadlocks` delta per database from B3 so users still see *that* deadlocks happen.
- UI: `DeadlocksPane.tsx` list + `DeadlockGraph.tsx` (processes ↔ resources, victim marked, SVG, theme
  tokens, no library). Actions: copy text, "open in new tab", export the original XML as `.xdl`
  (SSMS-compatible, written via the existing save flow, includes query text → explicit confirm).
- Live notification: when a new deadlock appears while the monitor runs, a non-modal toast "Deadlock
  in <db> — view".
- Tests: X1–X16.

## B8 — Query Store + server health — *deferred, not in Phase B*
Kept as a design sketch so the contract reserves room for it (`MonitorPanes.QueryStore`/`Health`);
not built, tested or benchmarked in Phase B. See "Slices" for why.

### Query Store (SQL Server 2016+, per database)
- **Scope: one database at a time**, chosen in a picker listing only databases where Query Store is
  `READ_WRITE` or `READ_ONLY` (discovered from `sys.databases.is_query_store_on`, then
  `sys.database_query_store_options` per DB, lazily, max 50 DBs probed per call, each probe 2 s
  timeout; more than 50 → "type to search" instead of listing). Defaults to the profile's database.
  An "all databases" summary (top 10 across DBs) is an explicit action, capped at 20 DBs, run
  sequentially through the scheduler, cancellable, showing per-DB progress and per-DB failures.
- Queries the chosen DB with three-part names after quoting the database name through the SQL Server
  quoting service (never concatenated raw); the database name must be in the discovered list.
- `QueryStoreRow { long QueryId; string QueryHash; string TextPreview; long PlanCount; double CpuMs;
  double DurationMs; double LogicalReads; long Execs; double? RegressionPct; }`.
- Views: **Top resource consumers** and **Regressed queries** (compare recent window vs history window,
  default last 1 h vs last 7 d, validated ≤ 30 d) from `sys.query_store_runtime_stats` ⟕
  `query_store_plan` ⟕ `query_store_query` ⟕ `query_store_query_text`; **Forced plans** list
  (read-only — forcing/unforcing plans is out of scope for Phase B).
- Plan per `plan_id` → existing `PlanView.tsx`. Query runs with a 10 s timeout and
  `OPTION (MAXDOP 1)`; large stores (> 1 GB) default to the last 24 h.
- Shows store state (`actual_state_desc`, size vs max, `readonly_reason`).
- PG: no equivalent in core → hidden with reason.

### Server health cards
| Card | SQL Server | PostgreSQL |
|---|---|---|
| Replication / HA lag | `sys.dm_hadr_database_replica_states` (log send/redo queue, lag) | `pg_stat_replication` (write/flush/replay lag), `pg_stat_wal_receiver` on standby |
| Replication slots | — | `pg_replication_slots` (inactive slots, retained WAL bytes) |
| Transaction-ID wraparound | — | `age(datfrozenxid)` per DB, warning at 1.5 billion, critical at 1.9 billion |
| tempdb / temp usage | `sys.dm_db_file_space_usage` on tempdb, version store size | `pg_stat_database.temp_bytes` delta |
| Memory | `sys.dm_os_memory_clerks` top 10, `dm_os_sys_memory` state | `shared_buffers` hit ratio (from B3) |
| Long maintenance ops | `percent_complete` (B1a) | `pg_stat_progress_vacuum/analyze/create_index/cluster/basebackup/copy` |
| Log space | `sys.dm_db_log_space_usage`, `log_reuse_wait_desc` | WAL generated per sec (`pg_stat_wal`, 14+) |
| Database states | offline / suspect / recovering / restoring | `datallowconn = false`, in recovery |
- Thresholds are fixed constants in code (documented, tested); a card is green / amber / red / "not
  configured" / degraded(reason). Polled at ×15 the main interval (default 30 s) as a secondary job.
- UI: `HealthPane.tsx` card grid; clicking a card opens the detail rows.

## B9 — Local alerts — *deferred, not in Phase B*
Design sketch only; not built in Phase B (see "Slices"). If picked up, re-decide the OS-notification
plugin against CLAUDE.md rule 1 (keep `src-tauri` smaller) — the in-app toast may be enough.

Alerts fire only while Querithm is running and a monitor (or the background alert watcher below) is
active for the profile. In-app only: no email, webhooks, or network — offline-first.

- **Rules** (`AlertRule { Ulid Id; string ProfileId; AlertKind Kind; double Threshold; int ForSeconds; Severity Severity; bool Enabled; }`),
  stored in `store.db` per profile (settings only — no query text, no secrets). Built-in defaults,
  editable, resettable:
  | Kind | Default | Engines |
  |---|---|---|
  | Session blocked longer than | 30 s | both |
  | Head blocker idle in transaction longer than | 60 s | both |
  | CPU above | 90 % for 60 s | SQL Server (PG: degraded, no CPU from SQL) |
  | New deadlock | any | SQL Server (PG: `deadlocks` counter delta > 0) |
  | Replica lag above | 60 s | both, only where configured |
  | Inactive replication slot retaining WAL above | 10 GB | PG |
  | Wraparound age | amber 1.5 b / red 1.9 b | PG |
  | Log space used above | 90 % | SQL Server |
  | Query Store went `READ_ONLY` | any | SQL Server |
  | Monitor connection lost longer than | 30 s | both |
- **Evaluation** in the sidecar (`AlertEvaluator.cs`) on snapshot data — no extra server queries
  beyond what the enabled panes already run; a rule whose pane is disabled is evaluated on a minimal
  query scheduled through `MonitorScheduler` at ×15 interval. `ForSeconds` uses server-side
  timestamps; hysteresis: clears only after the value stays below threshold × 0.9 for `ForSeconds`.
- **Background alert watcher:** optional per profile ("watch this server while Querithm is open"),
  runs the minimal alert queries without an open Activity tab; off by default; never on Prod without
  explicit opt-in per profile; max 5 watched profiles.
- **Delivery:** notification `monitor.alert` → in-app toast + alerts list (bell icon, last 200,
  in-memory) + optional OS notification via Tauri notification plugin (**needs a §2 row and a §16
  entry for the capability; opt-in setting, off by default**). One alert per rule per episode — no
  repeats while it stays firing; "resolved" message when it clears. Snooze per rule 15 min / 1 h / until
  restart. Clicking an alert opens the Activity tab focused on the relevant session/card.
- Alert text never includes query text; it names spid, login, database and the measured value.
- Failure: evaluator error → that rule shows "error (reason)" in the rules list, others continue;
  monitor connection loss fires the "connection lost" rule instead of every other rule going quiet.

---

## Failure paths (each tested)
- Monitor connection drops → every pane `error("connection lost, retrying")`, reconnect with
  exponential back-off (1 s → 30 s), `ProbeAsync` re-run, no orphaned loop (L4).
- Server restart → counter reset detected, deltas suppressed for one tick (L5, D2).
- Slow server (tick > interval) → a late tick runs once, never as catch-up; `lagMs` shown; secondary
  jobs still progress (C8).
- Sidecar restart → UI shows "monitor stopped" with restart button; history is lost (stated) (L8).
- Kill fails (permission, session gone, rollback in progress) → specific message, no retry (K17, K19).
- Kill control connection can't be opened → bounded fallback onto the monitor connection (C4).
- A pool connection killed during its registration window → ordinary open failure, slot freed (K9).

## Corner cases and full test matrix

Every row is a required test, except the tables marked deferred (B8/B9). **U** = unit (xUnit/Vitest with fakes), **S** = Security suite
(`sidecar/Querithm.Tests/Security/`, written first), **I** = integration on both engines
(`QUERITHM_IT=1`) unless an engine is named, **E** = smoke UI (`scripts/bench/smoke-ui.mjs`).
Test names go in `MonitorTests.cs`, `MonitorSchedulerTests.cs`, `KillGuardTests.cs`, `BlockingGraphTests.cs`,
`DeltaTrackerTests.cs`, `ConnectionRegistryTests.cs`, `HistoryBufferTests.cs`, `MonitorSecurityTests.cs`,
`MonitorIntegrationTests.cs`, and `src/monitor/__tests__/*.test.ts`.

### Kill / cancel (highest risk)
| # | Case | Expected | Type |
|---|---|---|---|
| K1 | `ConfirmedKill = false` | Refused, nothing sent to server | S |
| K2 | Prod profile, `ConfirmedProd = false` | Refused, `GuardReason.ProdWrite` | S |
| K3 | Spid not in latest snapshot | Refused | S |
| K4 | Spid reused (same spid, new `login_time`/`backend_start`) | Refused "session ended or ID reused", target untouched | S, I |
| K5 | Session ends between check and kill | Kill is a no-op, reported "session already ended" | I |
| K6 | Target = monitor's own session | Always refused | S |
| K7 | Target = a Querithm connection: leased by a tab, idle in the pool, or dedicated (debugger, the monitor's control connection) | Refused "this is a Querithm connection — cancel or disconnect it from its tab", nothing sent | S |
| K8 | Connection registered after the snapshot that showed it (it looked foreign in the dialog) | Refused at kill time: the registry is read at kill time, not from the snapshot | U, S |
| K9 | A new pool connection killed between server login and its identity read | That open fails like any connection failure; pool slot released; borrower's execution reports the error; next borrow opens a fresh connection; nothing orphaned | U |
| K10 | SQL Server `CancelRequest` | Refused with engine reason, nothing sent | U, S |
| K11 | SQL Server `is_user_process = 0` (incl. a system session with spid > 50) / PG `backend_type` ≠ client backend | Refused "system session"; a user session with spid ≤ 50 is **not** refused for that reason | S, I |
| K12 | `Id` overflow / negative / zero / non-numeric in JSON; `Instance` or `ExpectedStatementStart` empty, > 64 chars or unparseable by the provider; PG `CancelRequest` without `ExpectedStatementStart` | Rejected at RPC boundary, no SQL sent | S |
| K13 | `Instance` round trip: snapshot → JSON → UI → `monitor.kill` → typed parameter matches the server value exactly (SQL Server `datetime` ⅓-ms ticks, PG microseconds) | Kill of the right session succeeds; the same key with the last digit changed is refused | U (per engine), I |
| K14 | Kill against a negative-blocker synthetic node (-2/-3/-4) | Refused "not a session", nothing sent | S |
| K15 | Azure SQL DB without `KILL DATABASE CONNECTION` | Terminate disabled, Azure permission named | U |
| K16 | `MonitorId` unknown or belongs to a stopped monitor | Refused | S |
| K17 | Missing `ALTER ANY CONNECTION` / `pg_signal_backend` | Server error mapped to clear message, no retry | I |
| K18 | PG non-superuser kills a superuser backend (incl. a managed platform's own backends, faked) | Refused by server, message shown | I (PG), U |
| K19 | Terminate a session with a long rollback | Returns ok; following ticks show rollback progress (`WITH STATUSONLY`); repeated kill refused while rolling back | I (SQL Server) |
| K20 | Double click / two kill requests for same target | Second is a no-op, single server call | U |
| K21 | Kill while monitor connection is down | Refused "monitor disconnected", not queued | U |
| K22 | Kill SQL text built only from validated int; no string from client reaches `KILL` | Assert generated batch | S |
| K23 | Kill target in a different database than `DatabaseFilter` | Allowed only if in snapshot; filter is not a security boundary | U |
| K24 | Audit: every kill attempt (allowed or refused) logged without query text, login or host | Log scrub test | S |
| K25 | PG cancel of a foreign session's running query | Its query ends with `57014`; the session stays connected (same `SessionKey`) | I (PG) |
| K26 | PG cancel after the dialog's statement finished and a new one started on the same backend | `query_start` mismatch → "statement already finished"; nothing sent; new statement untouched | U, I (PG) |
| K27 | `CancelRequest` on an idle / idle-in-transaction backend | "no running query", nothing sent | U |
| K28 | Terminate a foreign session A holding a lock that blocks B | A's connection dead; B proceeds | I |
| K29 | PG login without `pg_read_all_stats` (with or without `pg_signal_backend`): another role's backend | Row has empty `Instance` + `IdentityHidden`; kill refused at the RPC boundary, nothing sent | U, I (PG) |

### Session snapshot
| # | Case | Expected | Type |
|---|---|---|---|
| S1 | 0 other sessions | Empty grid, no error | U |
| S2 | > 1,000 sessions | First 1,000 by priority (blocked, blockers, active, then idle), `truncated` | U, I |
| S3 | NULL login/host/program/database/text | Shown as "—", no crash | U |
| S4 | Non-ASCII, emoji, RTL, control chars in text/program name | Rendered escaped; no grid corruption | U |
| S5 | Text > 4 KB | Preview cut on a character boundary (no split surrogate) | U |
| S6 | Statement offsets invalid / text NULL (SQL Server, encrypted modules) | "text unavailable (encrypted)" | U, I (SQL Server) |
| S7 | Parallel query rows (multiple tasks) | One row per session, max wait chosen | I (SQL Server) |
| S8 | Session starts or ends mid-query | Row present or absent, never duplicated | U |
| S9 | Idle in transaction (PG) / sleeping with open tx (SQL Server) | Flagged and counted | I |
| S10 | Background workers, autovacuum, replication senders (PG); system spids (SQL Server) | Hidden by "hide system", visible otherwise, never killable from UI | I |
| S11 | Timezone / clock skew between client and server | Durations computed on the server (`now() - x`, `DATEDIFF_BIG`), never client clock | U, I |
| S12 | Durations > 24 days (int overflow) | `long` milliseconds, `DATEDIFF_BIG` | U |
| S13 | Server-returned names with quotes/brackets/newlines | Treated as data only, never re-used in SQL | S |
| S14 | Permission downgrade during monitoring (role revoked) | Next tick degrades with reason, no crash | I |
| S15 | Two PG sessions run the same statement with different literals (same `query_id`) | Distinct `TextId`s, so each row and its kill dialog shows its own text; same `QueryHash` | U, I (PG) |
| S16 | SQL Server `ALTER PROCEDURE` that changes a literal but keeps the statement offsets (same `sql_handle`, same offsets), then run again | New `TextId` and the new text on the next tick; old text never shown for the new run | U, I (SQL Server) |
| S17 | Unchanged text across ticks | Hash not recomputed (ordinal compare hit); same `TextId` | U |
| S18 | Login without `VIEW SERVER STATE` / `pg_read_all_stats` | Only own sessions, reason shown | I |

### Blocking graph
| # | Case | Expected | Type |
|---|---|---|---|
| B1 | Simple A → B | A root, B child | U, I |
| B2 | Chain A → B → C | 3 levels | U, I |
| B3 | One head, many victims | Fan-out, counts correct | U |
| B4 | PG multi-blocker (B waits on A and C) | B under both A and C, counted once in header | U, I (PG) |
| B5 | Self-edge (parallel exchange) | Ignored | U, I (SQL Server) |
| B6 | Cycle A → B → A (deadlock before detector fires) | Marked "deadlock in progress", no infinite loop | U |
| B7 | Blocker not in snapshot (truncated, permission hidden) | Placeholder node "session hidden (reason)" | U |
| B8 | Head blocker is idle with open tx | Shown as root with tx age | I |
| B9 | Deadlock detector kills a victim between ticks | Tree updates, no stale node | I |
| B10 | 1,000-node tree | Built ≤ 5 ms, rendered with virtualization | U, bench |
| B11 | SQL Server `blocking_session_id` -2 / -3 / -4 | Synthetic root node with reason, not a session; never in `BlockedBy` | U |
| B12 | SQL Server parallel request whose tasks wait on two different sessions | `BlockedBy` lists both (distinct), node under both | U, I (SQL Server) |

### Locks
| # | Case | Expected | Type |
|---|---|---|---|
| LK1 | PG, two databases where a lock in `db2` has a relation OID that also exists as a different relation in the connected `db1` | `db2` row shown as `db2.<oid>` (or its name from `db2`'s metadata cache), never as `db1`'s relation | U, I (PG) |
| LK2 | PG lock on a shared catalog (`l.database = 0`, e.g. `pg_database`) | Resolved by name | U, I (PG) |
| LK3 | PG lock in another database with no metadata cache for it | `datname.oid`, no connection opened to that database | U |
| LK4 | Blocking pair on the test table | Locks pane shows A's granted and B's waiting lock | I |

### Deltas, waits, metrics
| # | Case | Expected | Type |
|---|---|---|---|
| D1 | First tick | No deltas, "collecting…" | U |
| D2 | Counter reset (server restart, `DBCC SQLPERF(...CLEAR)`, `pg_stat_reset`, `pg_wait_sampling_reset_profile`) | `resetDetected`, no negative values | U, I |
| D3 | Counter wraps / bigint near max | Handled as reset | U |
| D4 | Tick skipped (slow) | Delta divided by real elapsed time, not nominal interval | U |
| D5 | New wait type appears mid-session | Delta from 0 | U |
| D6 | Benign wait list | Fixed list asserted; benign waits never shown | U |
| D7 | PG with `pg_wait_sampling` | Source "server-profile", deltas match a generated lock wait within ±10 % | I (PG, fixture image with extension) |
| D8 | PG without extension, lock wait held for 3 intervals | Source "tick-sampled", Lock category grows, labelled low resolution | I (PG) |
| D9 | PG tier 2 issues no query of its own | Waits derived from the tick's sessions rows; assert SQL sent (sessions query runs even with the Sessions pane hidden) | U |
| D10 | PG tier 2 with ticks late by 3 intervals (fake clock) | Each sample weighted by real elapsed time capped at 2 × interval; no extrapolation | U |
| D11 | PG tier 2 with active rows truncated by the sessions cap | `WaitCoverage.Partial` set, shown | U |
| D12 | CPU ring buffer empty (fresh SQL Server) | Metric shows "n/a" | U |
| D13 | `pg_stat_checkpointer` (17+) vs `pg_stat_bgwriter` (< 17), `pg_stat_io` (16+) | Correct view by probed catalog | U, I |
| D14 | PG I/O rows | `Scope = "cluster"`, `Database`/`File` null, `BackendType`/`Object`/`Context` set; `DatabaseFilter` not applied, pane labelled; `track_io_timing` off → stalls null with reason; bytes from `op_bytes` or `read_bytes`/`write_bytes` by probed columns | U, I (PG) |
| D15 | SQL Server I/O rows | `Scope = "file"`, `Database`/`File` set, grouping fields null | U, I (SQL Server) |
| D16 | Azure SQL server-wide DMVs | Degraded per matrix; CPU from `sys.dm_db_resource_stats` | U |
| D17 | Ring buffer XML in an unexpected shape | CPU "n/a (ring buffer format)", other metrics fine | U |
| D18 | Managed PG / managed SQL Server probe results (no superuser, signal role from the platform's admin role, extension missing, unreadable XE file) | Each pane degrades per matrix from probed capabilities; no code path tests a platform name | U |
| D19 | PG `compute_query_id = off` | `QueryHash` null, no grouping, reason shown; `TextId` still a content hash | U, I (PG) |
| D20 | Generated load / a blocking pair | Batch req/s / TPS > 0; Lock wait category grows | I |

### Top queries
| # | Case | Expected | Type |
|---|---|---|---|
| T1 | Plan cache flushed / `pg_stat_statements_reset()` | Reset detected | U, I |
| T2 | Query evicted from cache between ticks | Dropped, no negative delta | U |
| T3 | `pg_stat_statements` 1.8+ vs older column names | Mapped | U |
| T4 | Extension missing / not preloaded | Degraded with exact reason | I (PG) |
| T5 | Same hash, different text (literals) | Grouped by hash, one sample text | U |
| T6 | PG 17 column names (`shared_blk_read_time`) | Mapped by probed columns | U, I (PG 17) |
| T7 | SQL Server incremental read: only entries with `last_execution_time >=` previous cutoff fetched; full read every 30th run catches evictions | Assert SQL / fake rows | U |
| T8 | PG uses `showtext := false`; text fetched once per (`queryid`, `dbid`, `userid`); `TextId` is the text's content hash | Assert SQL sent | U |
| T9 | Cross-poll completion: execution starts before run N, completes between run N and N+1 | Appears in run N+1's incremental read with exec delta 1, not deferred to the full read | U (fake rows), I (SQL Server, `WAITFOR` query spanning a poll) |
| T10 | Cutoff with a request running for 10 min | Cutoff = that request's start (server time); incremental read still correct; cost within metric 31 | U, bench |
| T11 | Run a query N times | Appears with exec delta N | I |

### Monitor connection and payload
| # | Case | Expected | Type |
|---|---|---|---|
| M1 | Session settings applied on open and after reconnect (SQL Server isolation / `LOCK_TIMEOUT` / `DEADLOCK_PRIORITY`; PG timeouts / `application_name`) | Read back from the server and asserted | I |
| M2 | PG monitor never holds a transaction | `pg_stat_activity.backend_xmin` of the monitor is null between jobs | I (PG) |
| M3 | Monitor against a table under a schema-modification lock (SQL Server) | Lock pane returns within `LOCK_TIMEOUT`; object shown by name when the catalog read succeeds (observed on 2022, B0 row 7) or by id "name locked" when it times out; no hang | I (SQL Server) |
| M4 | Job with raised `statement_timeout` (PG) | Revised: no job raises it (the 5 s session value is set once at connect; jobs are bounded by the scheduler's cancel), so nothing has to be restored; a static test keeps it that way | S |
| M5 | Every tick | Full `Sessions` list; `Texts` holds only ids not already sent while in the store | U |
| M6 | Lost or out-of-order notification | Snapshot with older `Seq` ignored; texts it carried are fetched via `monitor.texts` when a row names them; no resync RPC exists | U, Vitest |
| M7 | UI LRU eviction | `monitor.texts` refetch | U, Vitest |
| M8 | Sustained text churn: 500 sessions with new unique literals every tick, 1 h on a fake clock | Sidecar store ≤ live + 4 MB at every tick; no growth with uptime; UI cache ≤ 5,000 texts / 8 MB | U, Vitest |
| M9 | Refetch after sidecar eviction (ended session's text evicted) | `monitor.texts` returns it in `Missing`; UI shows "text no longer available"; no repeated request | U, Vitest |
| M10 | Live row whose text the UI evicted | `monitor.texts` always returns it (live refs never evicted) | U |
| M11 | Evicted text reappears in a new session | Re-sent in `Texts` | U |
| M12 | `monitor.texts` with > 500 ids or a malformed id | Rejected at RPC boundary | S |
| M13 | Payload budget: 500 sessions ≤ 512 KB per tick without new texts, first snapshot ≤ 2.5 MB | Measured in bench 28 | bench |
| M14 | Connection count per profile never exceeds the stated budget (monitor + kill control + shared banner check) | Count from `sys.dm_exec_sessions` / `pg_stat_activity` during a kill and a banner cycle | I |

### Lifecycle and failure recovery
| # | Case | Expected | Type |
|---|---|---|---|
| L1 | Start twice with same profile | Two independent monitors, two connections | U |
| L2 | Stop while a tick is running | Tick cancelled via token, connection disposed, no late notification | U |
| L3 | Tab closed / profile disconnected | Loop stopped, connection closed | U, E |
| L4 | Monitor connection killed externally | Error state, reconnect with back-off 1 s → 30 s, probe re-run | I |
| L5 | Server restart | Reconnect, counters reset handled | I |
| L6 | SSH tunnel drop | Same as L4; tunnel reopened by existing tunnel logic | I |
| L7 | Query timeout on a DMV (server overloaded) | Per-query timeout (5 s), pane error, other panes continue | U |
| L8 | Sidecar crash/restart | UI "monitor stopped", restart button, no orphan connection on server | E |
| L9 | Sidecar shutdown with N monitors | All disposed within 2 s | U |
| L10 | Interval / pane changes while running | Applied next tick, no restart | U |
| L11 | Pause | No queries sent while paused (assert with fake) | U |
| L12 | 50 monitors started (abuse) | Capped (8 per sidecar), refused with reason | S |
| L13 | Leak check: start/stop 1,000 times | No growth in connections, tasks or memory | U |
| L14 | Interval outside 1–30 s, unknown pane bits, reserved B8 bits | Rejected at RPC boundary | S |
| L15 | Faulted fake provider on one pane | That pane `error`, loop continues with back-off, other panes fine | U |

### History and export
| # | Case | Expected | Type |
|---|---|---|---|
| H1 | Buffer full by time | Oldest evicted | U |
| H2 | Buffer full by 50 MB cap | Oldest evicted, cap message | U |
| H3 | Diff replay | Replayed snapshot equals original | U |
| H4 | Scrub during reconnect gap | Gap shown, no interpolation | U |
| H5 | Export without "include query text" | No text, login or host in file | S |
| H6 | Export path traversal (`../`) | Rejected; only `<appDataDir>/querithm/exports/` | S |
| H7 | Disk full during export | Partial file deleted, error shown | U |
| H8 | Texts referenced only by retained history | Kept while referenced, counted in the 50 MB cap, freed when those ticks are evicted | U |

### UI
| # | Case | Expected | Type |
|---|---|---|---|
| U1 | Snapshot update | No tab-level re-render (render count assert); only changed cells damaged | Vitest |
| U2 | Selection kept across ticks by `SessionKey`, cleared if session ends | — | Vitest |
| U3 | Kill dialog shows login/host/program/text/tx age; second confirm required; Prod needs typed confirmation | — | Vitest, E |
| U4 | Kill button disabled with tooltip reason for each degradation | — | Vitest |
| U5 | Sort/filter stable across ticks | — | Vitest |
| U6 | Theme tokens for wait colours in light and dark | — | Vitest |
| U7 | Keyboard: navigate tree, open detail, kill (with confirm) | — | Vitest |
| U8 | Querithm-connection rows: badge, **Go to tab** focuses the tab, no kill/cancel | — | Vitest, E |
| U9 | Pane states: loading, empty, paused, degraded, error with countdown, truncated, disconnected | Each renders its text from `PaneStatus` | Vitest |
| U10 | Blocking node with 300 children | First 50 + "+250 more"; expand state kept across ticks | Vitest |

### Deadlock history (B7)
| # | Case | Expected | Type |
|---|---|---|---|
| X1 | Real deadlock created by two sessions | Appears with both processes, victim marked, resources correct | I (SQL Server) |
| X2 | Same deadlock in ring buffer and event file | Shown once (dedupe by graph hash) | U |
| X3 | Malformed / truncated XML (ring buffer cut-off) | Event marked `Partial`, others still parsed | U |
| X4 | XML with DTD / external entity | Rejected, no resolution attempted (XXE) | S |
| X5 | Event > 5 MB | Skipped with "event too large" | U, S |
| X6 | Multi-victim and parallel-query deadlocks (exchange events) | Rendered without crash | U |
| X7 | Deadlock involving objects in a DB the login can't access | Revised (B0 row 8): the report itself names the object, so it is shown by name; by id only when the report has no `objectname` | I (SQL Server) |
| X8 | `system_health` stopped | Degraded per matrix | U, I (SQL Server) |
| X9 | Event file path unreadable | Ring buffer only, reason | U |
| X10 | Azure SQL DB | Degraded per matrix | U |
| X11 | PG | Degraded with reason, `deadlocks` counter delta shown after a real PG deadlock (once the victim's transaction has ended, B0 row 7) | I (PG) |
| X12 | 0 events / time window empty | Empty state, no error | U |
| X13 | > 200 events | Newest 200, `truncated` | U |
| X14 | `.xdl` export includes query text only after confirm; path inside app data | — | S |
| X15 | Monitor code paths never issue `CREATE/ALTER/DROP EVENT SESSION` (scoped to `Querithm.Core/Monitoring` and the providers' activity classes — §11 Trace legitimately creates `querithm_trace_*`) | Assert SQL sent by fake connection | S |
| X16 | New deadlock during live monitoring | One toast, not repeated on next poll | U, E |

### Query Store (B8) — *deferred with B8; not run in Phase B*
| # | Case | Expected | Type |
|---|---|---|---|
| Q1 | Query Store `READ_WRITE` with data | Top consumers and regressed lists populated | I (SQL Server) |
| Q2 | Off / `READ_ONLY` (size) / `ERROR` | Degraded with exact state and reason | U, I (SQL Server) |
| Q3 | Regression calc: query with no history | Excluded from "regressed", not divide-by-zero | U |
| Q4 | Multiple plans per query | Plan count, plan picker | U |
| Q5 | Window validation (> 30 d, end < start, negative) | Rejected at RPC boundary | S |
| Q6 | 10 s timeout on huge store | Pane error, suggest smaller window, monitor continues | U |
| Q7 | No `VIEW DATABASE STATE` | Degraded per database | I (SQL Server) |
| Q8 | Database switched / dropped while pane open | Pane resets with reason | U |
| Q9 | Never runs `sp_query_store_force_plan` / `unforce` / `ALTER DATABASE … QUERY_STORE` | Assert SQL sent | S |
| Q10 | PG | Pane hidden with reason | U |
| Q11 | Server with > 50 databases | Picker switches to search; probe stops at 50 | U |
| Q12 | "All databases" summary with one DB offline / no permission / timing out | That DB listed as failed with reason, others shown | U, I (SQL Server) |
| Q13 | Cancel "all databases" midway | Stops after current DB, partial result marked | U |
| Q14 | Database name with `]`, spaces, Unicode | Quoted via quoting service; name not in discovered list → refused | S |

### Scheduler
| # | Case | Expected | Type |
|---|---|---|---|
| C1 | Two jobs never run concurrently on the monitor connection | Assert with fake connection that throws on overlap | U |
| C2 | Kill while a long job runs (deadlock read, top queries) | Kill on the dedicated control connection (not the pool) within 500 ms; closed after | U, I |
| C3 | Kill a foreign blocker while it blocks 8 tab connections (pool exhausted) plus a queued waiter | Kill completes on the control connection; the kill itself changes no pool state (waiter count, borrowed set); the blocked tabs proceed once the blocker is gone | U, I |
| C4 | Control connection can't be opened (fake connect failure / server connection limit) | Running monitor job cancelled, kill runs on the monitor connection, path reported; total time bounded (≤ connect timeout + 1 s) | U |
| C5 | Two kills on the same profile at once | Serialised on one control connection; the second waits ≤ 5 s, then "another kill is in progress" | U |
| C6 | Secondary jobs due during a main tick | After the tick, exactly one runs (most overdue first); the rest stay pending; none queued twice | U |
| C7 | Default intervals, every Phase B pane enabled, idle server, typical durations (tick 100 ms, locks 200 ms, top queries 300 ms / full 1.5 s, deadlocks 2 s), fake clock, 10 min | Every secondary job runs within its period + 3 ticks; tick lag p95 ≤ 200 ms | U |
| C8 | Slow server: every job 3× those durations, tick longer than the interval | Every secondary job still runs at least once per 2 × its **effective** period (revised: the period after any C13 throttle — the two rows contradicted, since C13 doubles a period on purpose); late ticks run once (no catch-up); `lagMs` reported | U |
| C9 | Interactive request while a job runs | Starts as soon as that job ends, before the next tick; the delayed tick reports `lagMs` | U |
| C10 | Interactive job still not started after 5 s queue time (fake long job running) | Fails "monitor busy, try again"; no pending entry left; the next request is admitted normally | U |
| C11 | Second detail request (other row) while the first is pending | First fails "superseded"; only the second runs | U |
| C12 | Job timeout | Only that job cancelled; next tick normal | U |
| C13 | Budget > 50 % for 10 s | Lowest-priority job halved, header message; restored when load drops | U |
| C14 | Stop during any job | All jobs cancelled, both connections closed | U |

### Server health (B8) — *deferred with B8; not run in Phase B*
| # | Case | Expected | Type |
|---|---|---|---|
| R1 | No HA / replication configured | "not configured", not error | U, I |
| R2 | PG streaming replica (fixture primary + standby) | Lag values shown; standby shows receiver card | I (PG) |
| R3 | Inactive replication slot retaining WAL | Amber/red by retained bytes | U, I (PG) |
| R4 | Wraparound thresholds (1.5 b / 1.9 b) | Boundary values exactly at, below, above | U |
| R5 | tempdb version store growing (long snapshot tx) | Card amber, links to the session in B1a | I (SQL Server) |
| R6 | `log_reuse_wait_desc` = `ACTIVE_TRANSACTION` / `LOG_BACKUP` | Shown with explanation | U |
| R7 | Progress views by PG version (copy 14+, basebackup 13+) | Correct views per version | U |
| R8 | Database offline / suspect / restoring | Red card, no query against that DB | U, I (SQL Server) |
| R9 | AlwaysOn DMVs on Express / non-HA edition | "not available on this edition" | U |
| R10 | Each card query fails independently | Other cards still render | U |
| R11 | Threshold constants | Asserted by tests so changes are deliberate | U |

### Kill safety preview
| # | Case | Expected | Type |
|---|---|---|---|
| P1 | No open tx | Risk none, no extra checkbox | U, I |
| P2 | Large write tx (≥ 100 MB log on SQL Server; long write tx on PG) | Risk high | I |
| P3 | High risk, `ConfirmedLongRollback = false` | Refused in sidecar | S |
| P4 | Client sends low risk but server-side recompute is high | Sidecar uses its own value, refused | S |
| P5 | Session already rolling back | Preview says so; kill refused (K19) | I (SQL Server) |
| P6 | No permission to read tx DMVs | Risk unknown with reason; treated as high for confirmation | U, S |
| P7 | Threshold boundary values | Exactly at / below / above | U |
| P8 | Read-only PG tx (no `backend_xid`) | Risk low regardless of age | U, I (PG) |
| P9 | No time estimate ever shown | Assert UI text | Vitest |

### Own-tab awareness and blocking banner
| # | Case | Expected | Type |
|---|---|---|---|
| O1 | Tab session appears in snapshot | `QuerithmKind = pool`, `OwnSessionId` set, badge + Go to tab | U, I, E |
| O2 | Tab reconnects (new spid) | New identity registered on the open; old key maps to nothing | U, I |
| O3 | Spid reused by another app after tab closes | Not marked Querithm (identity incl. start time) | U |
| O4 | Own tab open tx blocks another session ≥ 5 s | Banner appears with correct count | I, E |
| O5 | Blocking stops | Banner disappears within one check | I |
| O6 | Tab executing | Background check skipped; no use of tab session | U |
| O7 | No open tx | No checks at all (assert zero queries) | U |
| O8 | Banner Commit / Rollback | Uses existing tab tx commands and guards; Prod guard applies | U, E |
| O9 | Dismiss | Muted 5 min, then shows again if still blocking | Vitest |
| O10 | No permission to see others | No banner, reason in settings | I |
| O11 | Check connection fails | Silent retry next cycle, no banner flicker, no orphan connection | U |
| O12 | Five tabs on one profile with open tx | One check query per 10 s for all five, one borrowed connection | U, I |
| O13 | Monitor running for the profile | Banner fed from the snapshot; zero extra check queries | U |
| O14 | Profile pool full when the check is due | Waits ≤ 1 s **in the queue** (revised: the bound covers only the wait for a full pool, never opening a connection — a slower open was cancelled every cycle and the banner never checked), cycle skipped, waiter removed; next cycle retries | U |
| O15 | Tabs A and B borrow the same physical connection one after the other (pool of 1) | During A's run `OwnSessionId` = A; between runs null ("Querithm pool (idle)"); during B's run = B | U, I |
| O16 | Pin / unpin: `BEGIN TRAN` pins, idle between statements, `COMMIT` unpins and returns it | `OwnSessionId` kept while pinned (incl. idle), cleared on return | U, I |
| O17 | Driver reopens a broken pooled connection | New identity recorded on the reopen; old `SessionKey` no longer maps to any connection | U |
| O18 | Dedicated connections (debugger target, monitor, kill control) | Labelled by kind; never killable (K7) | U |
| O19 | Registry growth: 1,000 physical opens and closes | Entries for closed connections pruned; registry size bounded by open connections | U |

### "Why is it slow?" and wait dictionary
| # | Case | Expected | Type |
|---|---|---|---|
| W1 | Each rule fires on its fixture input (blocked, idle blocker, hidden blocker, memory grant, parallel skew, scan, plan warnings, PG idle-in-tx, PG IO/LWLock) | Correct finding + evidence | U (one test per rule) |
| W2 | Multiple rules match | Ordered by severity, deterministic | U |
| W3 | Nothing matches | "no obvious cause" with numbers | U |
| W4 | Session ended | "session ended" | U |
| W5 | Plan unavailable | Plan rules skipped, noted | U |
| W6 | Real blocking pair | Diagnosis names the blocker | I |
| W7 | Identifiers with markup/control chars in evidence | Rendered as text | S, Vitest |
| W8 | Dictionary JSON valid, covers every wait in the benign/category lists and all PG events for supported versions | Schema + coverage test | U |
| W9 | Unknown wait type | "no description yet" | U |
| W10 | Dictionary loads under trimming | Published trimmed build test | U |

### Local alerts (B9) — *deferred with B9; not run in Phase B*
| # | Case | Expected | Type |
|---|---|---|---|
| A1 | Each default rule fires on fixture data and resolves | One fire, one resolve | U (one per rule) |
| A2 | Value flaps around threshold | Hysteresis: no repeat fires | U |
| A3 | `ForSeconds` uses server time, survives skipped ticks | — | U |
| A4 | Rule's pane disabled | Minimal query scheduled at ×15, via scheduler | U |
| A5 | Connection lost | Only "connection lost" fires, others suspended | U |
| A6 | Rule validation (negative, NaN, absurd thresholds, unknown kind) | Rejected at RPC boundary | S |
| A7 | Alert text has no query text | Assert | S |
| A8 | Snooze / resolve / reset to defaults | — | U, Vitest |
| A9 | Watcher limits: > 5 profiles; Prod without opt-in | Refused with reason | S |
| A10 | OS notification off by default; on → plugin called; plugin unavailable → in-app only | — | Vitest |
| A11 | Rules stored without secrets; survive restart | Store test | U, S |
| A12 | Real blocking > 30 s on both engines | Alert fires, click focuses session | I, E |
| A13 | Real deadlock (SQL Server) / counter delta (PG) | Alert fires | I |
| A14 | PG CPU rule | Degraded with reason, never fires | U |
| A15 | Evaluator exception in one rule | Rule shows error, others continue | U |

### Integration fixture additions
- PG image with `pg_stat_statements` and `pg_wait_sampling` in `shared_preload_libraries`, plus a
  second PG container without them (degradation tests).
- `max_connections` ≥ 600; least-privilege logins on both engines (no `VIEW SERVER STATE` /
  no `pg_read_all_stats`, no kill rights) for permission tests.
- Helper that creates a deterministic blocking pair, a three-level chain, and a PG multi-blocker.
- Helper that produces a real deadlock on both engines (two sessions, opposite update order).
- Helper that fills the SQL Server plan cache to ~50,000 entries and `pg_stat_statements` to its
  `max` (5,000) for the top-queries bench (metric 31).
- No managed-cloud fixture (RDS, Cloud SQL, Azure): those rows are covered by fake probe results (D18).
- **Not added in Phase B** (deferred with B8): the Query Store regression DB and the PG primary +
  standby pair. That removes the two most expensive fixture pieces.

## Performance (§14 rows)
- **28** "Activity snapshot, 500 sessions": server query ≤ 100 ms, sidecar processing ≤ 10 ms, UI
  update (parse + Glide update of the full row set) ≤ 16 ms, sidecar CPU ≤ 2 % at 2 s interval, tick
  lag (`LagMs`) p95 ≤ 200 ms; payload ≤ 512 KB per tick without new texts, first snapshot ≤ 2.5 MB,
  first-snapshot apply in the webview ≤ 50 ms. A miss on the UI or payload budget triggers the diff
  escalation ("Payload").
- **29** "Monitor history, 10 min at 2 s with 500 sessions" (B-next): memory ≤ 50 MB, scrub to any
  tick ≤ 50 ms.
- **30** "Deadlock history, 200 events (SQL Server)": server read ≤ 2 s, sidecar XML parse ≤ 200 ms
  total (≤ 5 ms median per event), first paint of list ≤ 100 ms after response, graph render of a
  10-process deadlock ≤ 50 ms. Bench: seed 200 deadlocks with the fixture helper.
- **31** "Top queries refresh, 50,000-entry plan cache (SQL Server) / full `pg_stat_statements`
  (5,000, PG)": incremental server query ≤ 300 ms SQL Server, ≤ 100 ms PG; full read (every 30th run)
  ≤ 1.5 s SQL Server; sidecar delta + top-100 ≤ 20 ms; slower engine reported.
- **32** "Scheduler overhead": ≤ 1 % sidecar CPU at default intervals with every Phase B pane enabled.
- The identity read added to `OpenConnectionAsync` (B1b) touches the existing "execute → first batch"
  row: `pnpm bench` must stay within the 20 % regression rule for a cold pool connection.
- Query Store and health-card targets are set when B8 is picked up (not numbered now).
- Bench: open 500 idle connections from the bench script. The PG Docker fixture needs
  `max_connections` ≥ 600 (default 100) — update `scripts/bench/docker/compose.yml`.

## Phase B done when
**B-core:**
- B0 findings recorded in this plan.
- Both engines: sessions, blocking tree, locks, waits, metrics, I/O, top queries, deadlock
  history — or a visible degradation reason, each with a test.
- Kill/cancel guarded in the sidecar incl. exact session-identity re-check (K13), the
  `is_user_process` / `backend_type` rule (K11) and the Querithm-connection refusal (K7–K9); Security
  suite green.
- Monitor connection settings and payload tests (M1–M14) green.
- Smoke test extended: open monitor, see the blocking pair in the tree; trigger a deadlock and open
  its graph; own-tab row → Go to tab; own-tab blocking banner → Rollback clears it; "Why is it slow?"
  names the blocker; kill dialog on a foreign session shows high rollback risk and requires the extra
  confirm.
- Deadlock history strictly read-only (X15 green).
- Metrics 28, 30, 31, 32 within target and "execute → first batch" within the regression rule; §16
  rows added (JSON payload with full rows and texts sent once, `KILL` literal, monitor connection on
  Prod, the per-profile connection budget, the kill control connection opened outside the pool cap,
  the identity read on every physical open, and Querithm's own connections not killable from the
  monitor).

**B-next:** request detail / live plan and history + export, each with its tests; smoke test scrubs
history back to the blocking pair; metric 29 within target.

No new Tauri capability and no new dependency in Phase B.

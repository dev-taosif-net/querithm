# Phase B — implementation status (B-core + B-next)

Requirement-to-evidence checklist for [PHASE-B-SERVER-MONITOR.md](PHASE-B-SERVER-MONITOR.md). B8 (Query Store, health
cards) and B9 (local alerts) are deferred and not listed. Last updated 2026-10-29 (completion pass, Linux). **Status: B-core and
B-next implemented and verified on Linux (see "Completion pass"): every gate check passes, including the 20-min soak; the
hardening pass's coverage gaps are closed. Still open: S2 on PostgreSQL and S11 (blocked by the fixture), and no run on Windows
or macOS for this build.**

**Statuses** — *not started*: nothing exists. *implemented*: code and tests exist, but at least one required kind of test
(U, S, I per engine, E, bench) is missing or did not pass in the latest run. *verified*: every required kind exists and
passed in the latest run recorded below (an integration row on each engine it names). *blocked*: cannot be verified in this
environment; the reason is given. A written test is not a passed test; an integration test that returned early is not a
pass (every integration fact below ran with `QUERITHM_IT=1` against the Docker fixture).

**Environment** — Windows 11 dev box (i5-1135G7, 8 logical cores), Docker Desktop fixture
(`scripts/bench/docker/compose.yml`): SQL Server 2022 RTM-CU27-GDR 16.0.4305.5 (Linux container), PostgreSQL 16.15 with
`pg_stat_statements` + `pg_wait_sampling` preloaded, PostgreSQL 17.11 (`pg_stat_statements` only), a bare PostgreSQL 16.15,
the SSH bastion. Drivers: Microsoft.Data.SqlClient 6.1.7, Npgsql 10.0.3. Integration runs set
`QUERITHM_IT_MSSQL_HOST=127.0.0.1 QUERITHM_IT_PG_HOST=127.0.0.1` (a localhost connect is slow on this machine, §16).


## Summary

Matrix rows in scope (deferred B8/B9 tables excluded): **209** — verified **207**, blocked **2**.
Rows not verified: S2(blocked: I PostgreSQL), S11(blocked: I SQL Server/I PostgreSQL) (reasons in the matrix and in "Not verified here").


## Completion pass (2026-10-29, Linux) — the hardening pass's open items

Same machine and fixture as the hardening pass below. What was open there, and what happened to it:

| Open item | Outcome |
|---|---|
| 20-min soak fails its heap check on Linux | **Fixed and passing.** Measured first (throwaway probe in the test host, `system_health` ring buffer at its ~4 MB cap): every 30 s deadlock poll read the whole ring buffer although the event-file cursor had nothing new — ~65 MB allocated and ~0.7 s per poll, ~2,000 pooled `char[]` buffers (128 K–4 M elements) rented per read by SqlClient's async path. A heap dump held 16.5 MB reachable where `GC.GetTotalMemory(true)` read 92 MB (free space between large objects). Fix: the ring buffer is read only when the event files cannot be (plan B0 row 19, ARCHITECTURE §16); a poll with nothing new then took 12–31 ms and ~18 KB. Soak: heap growth **13.8 MB** (limit 32 MB; 65.6 MB before), no swings (26 → 46 MB, flat 42–46 MB from minute 5 while the bounded text store fills), lag p95 2 ms, one monitor connection per monitor in every sample, nothing left on either server, P2 high. The soak now also records `GcFragmentedBytes`; its check is unchanged |
| Coverage: M14, O15, B5, X12, D19, P2 on PostgreSQL | Tests added (matrix rows below): M14 forces the kill's control path (a job holds the monitor connection) and real banner cycles (timer-driven, the monitor filtered so it does not answer) and counts the profile's connections on the server, both engines; O15 on both engines; B5's plan chain with fakes (live → cached → reason, profiling off, unparseable plan, idle, ended/identity mismatch, no VIEW SERVER STATE); X12 (no events, window excludes all); D19 at the reader and capability level; P2/P3/P8 on PostgreSQL as an opt-in 10-min test (`QUERITHM_IT_LONG=1`): a write transaction is high risk at 10 min, a read-only one stays low, the kill needs the long-rollback confirm |
| "Execute → first batch" never compared with a pre-Phase-B run | Metric #11 runs a synthetic execution and never opens a connection, so it cannot show the identity read. New engine metric **`11cold`** (§14 row 11b): `SELECT 1` as the first execute of a session on a new profile. A/B against the pre-Phase-B sidecar (b0e01da), 10 alternating rounds, 100 samples per engine each: SQL Server 15.5 → 17.4 ms (+1.9 ms, +12 %), PostgreSQL 6.8 → 8.7 ms (+1.9 ms, **+28 %**): one round trip per physical open, as designed (B1b). Over 20 % relative on PostgreSQL; `run.mjs` does not compare values under half their target. Accepted and recorded in §16 — it cannot be folded into another round trip (opens make none) or deferred past the hand-out (K8/K9) |
| Identity read the server refuses: querying fails | **Changed by decision (2026-10-29):** the connection serves queries, kills are refused while it is open (`querithmUnidentified`), the banner tells that tab it cannot check it; a monitor whose own read is refused stops with the reason. ARCHITECTURE §6 rule 7, §16; unit, Security and integration tests (both engines: SQL Server `DENY SELECT` on `sys.dm_exec_sessions`, PostgreSQL `pg_stat_activity` revoked inside one database) |
| Found on the way | The Deadlocks pane said "Showing the first 1,000 of 213, by priority" (the sessions pane's text): now "Showing the newest 200 of 213 deadlocks" (X13; Vitest failed before the fix) |

### Gate (completion pass, final build unless noted)

| Check | Command | Outcome |
|---|---|---|
| .NET build | `dotnet build` (sidecar) | 0 errors, 0 warnings |
| .NET suite, integration and long tests on | `QUERITHM_IT=1 QUERITHM_IT_LONG=1 dotnet test` | **1,807 / 1,807 passed** (65 Security tests; P2 on PostgreSQL 10 min 5 s) |
| Vitest / TypeScript | `vitest run` / `tsc -b --noEmit` | **116 files, 1,970 passed** / exit 0 |
| Contract | `pnpm gen:contract` | `src/ipc/contract.ts` unchanged (no RPC DTO changed) |
| Soak, 20 min | `QUERITHM_IT=1 QUERITHM_SOAK_MINUTES=20 dotnet test --filter MonitorSoak` | **passed** (`2026-10-29-10-48-16-monitor-soak-linux.json`) |
| Monitor bench | `QUERITHM_IT=1 QUERITHM_BENCH_MONITOR=1 dotnet test --filter MonitorBench` | 8 / 8, every measure within target (`2026-10-29-11-21-34-monitor-linux.json`). Metric 30 read 1,881 ms (≤ 2,000): the fixture's `system_health` files grew by the ~2,600 deadlocks seeded for the probe, and the full read scans every file (B0 row 15; 1,127 ms in the hardening pass) |
| Trimmed release + W10 | `node scripts/run.mjs build-sidecar --release`, `--filter WaitDictionaryTests` | built (3 IL2026 warnings, all in files unchanged by this pass: `HistoryBuffer.cs:200/207`, `MonitorService.cs:256`); 5 / 5 |
| Engine + webview bench | `pnpm bench` | 0 metrics outside target; `11cold` 16.25 ms; **1 flagged regression: #24 find usages 75.93 → 92.43 ms (+21.7 %)** (`2026-10-29-11-25-30-linux.json`). Not this change: two alternating engine runs of the previous commit (d421d19) and this build read 88.9 / 94.1 ms and 85.5 / 94.9 ms, and the pre-Phase-B code read 89–92 ms on this machine in the hardening pass — 75.93 ms was a low run |
| UI smoke | `node scripts/bench/smoke-ui.mjs <out>` | passed (exit 0, 41 screens), on the build before the X13 wording fix (a text change, covered by Vitest) |
| Vulnerable packages | `dotnet list sidecar package --vulnerable --include-transitive` | none; no dependency changed (`cargo audit` not applicable) |
| Not rerun | L5 server restarts (`QUERITHM_IT_DISRUPTIVE=1`) | no reconnect or probe code changed; last passed in the hardening pass |

## Hardening pass (2026-10-29, second machine, Linux) — audit, fixes and re-verification

The hand-off build was audited by five independent reviewers (requirements traceability, Core logic, tests, security and
failure modes, regressions and UI), the findings were cross-reviewed, fixed with a regression test each, and the changes
were reviewed again (two rounds) by the reviewers who had not written them. Everything below ran on the **final build** of
this pass.

**Environment** — Ubuntu 26.04.1 LTS, AMD Ryzen 7 4700U (8 logical cores), 14 GB; Docker 29.1.3 with the same fixture
(`scripts/bench/docker/compose.yml`: SQL Server 2022, PostgreSQL 16 with `pg_stat_statements` + `pg_wait_sampling`,
PostgreSQL 17, bare PostgreSQL 16, SSH bastion); .NET SDK 10.0.403, Node 24.22.2, pnpm 12.14.0. Fixture hosts default
(`localhost`), no overrides needed on Linux.

### Gate on the final build

| Check | Command | Outcome |
|---|---|---|
| .NET build | `dotnet build --no-incremental` (sidecar) | 0 errors, 0 warnings |
| Trimmed release | `node scripts/run.mjs build-sidecar --release` | built; 2 IL2026 warnings, both in code unchanged since 6ddf0e5 (`HistoryBuffer.cs:207`, `MonitorService.cs:256`), covered by W10's `--self-test` |
| .NET suite, integration on | `QUERITHM_IT=1 dotnet test` | **1,792 / 1,792 passed** (284 monitor / lifecycle / wait-dictionary results, 87 of them against the fixture; W10 on the trimmed binary) |
| .NET suite before this pass, same machine | same, on 6ddf0e5 | 1,758 / 1,765: the 2 new scheduler tests (defects 47, 48) and 5 PostgreSQL tests failing on a fresh fixture (defect 72) |
| Vitest | `vitest run` | **116 files, 1,969 tests passed** |
| TypeScript | `tsc -b --noEmit` | exit 0 |
| Contract | `pnpm gen:contract` | `src/ipc/contract.ts` unchanged (no RPC DTO changed) |
| Vulnerable packages | `dotnet list sidecar package --vulnerable --include-transitive` | none; no dependency file changed (`cargo audit` not applicable: `src-tauri` untouched) |
| Monitor bench | `QUERITHM_IT=1 QUERITHM_BENCH_MONITOR=1 dotnet test --filter MonitorBench` | 8 / 8 passed; every measure within target (`2026-10-29-09-09-54-monitor-linux.json`) — see below |
| Engine + webview bench | `pnpm bench` | 0 metrics outside target, 0 regressions vs this machine's previous run (`2026-10-29-09-52-18-linux.json`); see "Regression attribution" |
| Soak, 20 min | `QUERITHM_IT=1 QUERITHM_SOAK_MINUTES=20 dotnet test --filter MonitorSoak` | **failed: "managed heap grew 65,637 KB"** (limit 32 MB) — pre-existing on Linux, see "Soak"; fixed in the completion pass |
| UI smoke | `node scripts/bench/smoke-ui.mjs <out>` | passed (exit 0, no console or page errors, 41 screens: sessions, blocking tree, "Why?", kill dialog with the high-risk confirm, own-tab banner and row, deadlock graph, pause, history, disconnected, sidecar killed — L8) |
| Server restarts (L5) | `QUERITHM_IT=1 QUERITHM_IT_DISRUPTIVE=1 dotnet test --filter L5_a_` | 2 / 2 passed (SQL Server 2022, PostgreSQL 17 restarted under a running monitor) |

### Monitor bench (final build)

| Bench | Measure | Measured | Target |
|---|---|---|---|
| 28 PG idle / active | server query p95 | 11.4 / 34.4 ms | ≤ 100 |
| 28 SQL Server idle / active | server query p95 | 57.6 / 88.4 ms | ≤ 100 |
| 28 all four | tick lag p95 | 1–2 ms | ≤ 200 |
| 28 / 32 all four | sidecar processing p95 / CPU | ≤ 2.4 ms / ≤ 0.61 % | ≤ 10 ms / ≤ 2 % |
| 28 | payload per tick without new texts / first snapshot | ≤ 202 KB / ≤ 202 KB | ≤ 512 KB / ≤ 2.5 MB |
| 29 | scrub p95 | 6.0 ms | ≤ 50 |
| 30 | server read + parse, 200 events / parse total | 1,127 ms / 6.8 ms | ≤ 2,000 / ≤ 200 |
| 31 SQL Server | incremental p95 / full read / T10 | 118 / 122 / 120 ms | ≤ 300 / ≤ 1,500 / ≤ 300 |
| 31 PG | full read / delta + top 100 | 31 / 12 ms | ≤ 100 / ≤ 20 |

SQL Server server query under active churn varies with how much the churn (inside the same container) takes: 144 ms in
this pass's first run, 88 ms on the final build. Webview: 28ui 8.6 ms (≤ 16), 28first 9.3 ms (≤ 50), 30list 31.3 ms
(≤ 100), 30graph 16 ms (≤ 50).

### Regression attribution (CLAUDE.md 20 % rule)

`run.mjs` compares with the newest run on the same hardware. Against this laptop's last run before Phase B
(`2026-10-24-19-34-35-linux.json`), data compare (#22) read +20–23 %. The pre-Phase-B code (b0e01da), run today on the
same machine and fixture, measured the same or slower, so the difference is the environment, not Phase B:

| Metric | b0e01da today (2 runs) | final build today (2 runs) | 2026-10-24 |
|---|---|---|---|
| #22 data compare | 1,405 / 1,228 ms | 1,272 / 1,302 ms | 1,060 ms |
| #24 find usages | 91.9 / 89.4 ms | 81.2 / 75.9 ms | 69.2 ms |
| #23 import | 1,690 / 1,759 ms | 1,874 / 1,565 ms | 1,626 ms |

### Soak

The 20-minute soak fails its heap check on Linux, **on the hand-off code too**: an 8-minute soak of 6ddf0e5 on this
machine failed the same check (heap growth 62.8 MB). On both builds the measured heap (`GC.GetTotalMemory(true)`) makes
one step to ~120–150 MB within the first 2–4 minutes and then stays flat (Windows settled near 44 MB), so the check
passes or fails by whether the step lands before or after the warm window starts (an 8-minute soak of the final build
passed with 1.7 MB). Everything else held in the 20-minute run: one monitor connection per monitor in every sample,
nothing left on either server, no pane errors, tick lag p95 2 ms, threads flat, P2 read as high risk.

Two heap dumps of the final build (`dotnet-gcdump`, minute 3.5 and 7.3) hold 58.9 and 64.5 MB of reachable objects; the
GC reports 126–134 MB, the rest being free space and fragmentation of the large-object heap. Live: `System.Char[]` 30.6 MB
(+6.5 MB between the dumps; mostly 1 MiB arrays — the size class `ArrayPool<char>` keeps, i.e. pooled buffers of large
string reads), the top-queries tracker 16.1 MB (flat; within its 200,000-entry bound), the text store small and bounded
(8.5 MB counted after 20 min, under its per-monitor cap). Likely source of the large strings, not yet measured: each SQL
Server monitor's 30 s deadlock poll reads the `system_health` ring-buffer XML (up to ~4 MB) as one string. **Open:**
read the ring buffer only when the event-file cursor is not in use, then re-check the soak; and make the soak's heap
check robust to the step (e.g. compare the warm window's minimum with its last samples) — a decision for the next pass,
not made here. *Resolved in the completion pass: the ring-buffer read was confirmed as the source and removed from the poll;
the check passes unchanged.*

### Defects found and fixed in this pass (numbering continues defects 1–46, listed further down)

"Failed before" = run against 6ddf0e5 (the hand-off build), or, for a fix to this pass's own first version, a mutation
that restores it. Tests are in `MonitorHardeningTests.cs` unless named otherwise.

| # | Defect | Found by | Fix | Regression test (evidence) |
|---|---|---|---|---|
| 47 | Scheduler: the tick branch came before the secondary one, and after a tick longer than the interval the next tick is due at once: locks, top queries and deadlocks never ran until the throttle had slowed everything (~130 s). C8 measured gaps between runs, never the first one | Core review | a secondary job already due when a tick ends goes before the next tick; one that becomes due later waits (a plain reorder made C7's lag p95 1.7 s) | `MonitorSchedulerTests.C8_a_tick_longer_than_the_interval…` (failed: locks never ran in 60 s); C7 green |
| 48 | Throttle halved disabled jobs and counted them in "throttled" | Core review | enabled jobs only | `MonitorSchedulerTests.C13_the_throttle_slows_the_lowest_ranked_job_that_runs…` (failed: multiplier 2 on the disabled job) |
| 49 | Stop during the probe: the connect installed a fresh lost-signal and started a scheduler nobody stopped (monitor gone from the list, still polling; off the test scheduler `monitor.stop` hung) | Core review | cancellation checked under the state lock before installing | `C14_a_stop_that_arrives_during_the_probe…` (failed) |
| 50 | A pause made while reconnecting was lost: the new scheduler polled while snapshots said "paused" (L11) | Core review | pause applied to the new scheduler; Pause/Resume applied in order under one gate (a crossing Pause and Resume could leave the scheduler paused — code review, no deterministic test) | `L11_a_pause_made_while_the_monitor_reconnects…` (failed) |
| 51 | Open, identity, session settings and probe had no time bound: a server that accepted the login and stalled kept the monitor "connecting" for ever, every kill refused | Security review | bounded by 2 × the profile's connect timeout + 15 s | `L4_a_server_that_accepts_the_login_and_then_stalls…` (failed) |
| 52 | A rejected sign-in was retried every ≤ 30 s with the same secret (~120 failed logins an hour: account lockout) | Security review | rejected sign-ins (SQL Server 18456/18488/18487/18486/18470/18452, PG 28P01/28000, SSH auth, changed/unknown host key) stop the monitor with the reason; unreachable or failing-over servers still retried; stopped monitors pruned | `L4_a_rejected_sign_in_stops_the_monitor…` (failed: retried), `L4_an_unreachable_server_is_still_retried…` (guard) |
| 53 | The reconnect back-off restarted at 1 s after every successful connect: a server that drops the monitor gets a probe every second | Core + security review | restarts only after a tick succeeded | `L4_a_server_that_drops_the_monitor_before_its_first_tick…` (failed: gaps 1 s) |
| 54 | A fault after the provider returned (malformed row) or a rollback-status timeout was swallowed by the scheduler: the previous tick's data stayed on screen as "ok" | Test review | the tick's panes go to error with the reason; a rollback-status timeout keeps the rows | `L15_a_fault_while_processing_a_tick…` (failed) |
| 55 | Any non-stopped monitor silenced the own-tab banner, though a paused, reconnecting or timing-out one sends no rows; a filtered snapshot cleared banners of tabs it could not see | Core + regression review | only a monitor whose latest read covered every session, recently, answers; only such reads raise the snapshot event | `O13_only_a_monitor_reading_every_session…` |
| 56 | The banner check had no time bound (pool connections have no command timeout): one hung query stopped the banner on every profile and kept a pool slot | Core, test and security review | the query bounded (5 s token + 5 s driver timeout), the connection discarded after a cut or failed-after-cut check, the open bounded at 45 s | `O11_a_blocking_check_that_hangs…` |
| 57 | The banner check retried a rejected sign-in every 10 s (~360 failed logins an hour) | Security re-review | paused per profile until its connections are rebuilt (`SessionManager.CredentialEpoch`; the epoch is read before the open and the secret stored before the pools are rebuilt) | `O11_a_rejected_sign_in_pauses_the_blocking_check…` (mutation of the epoch read: failed) |
| 58 | After a run, a failed re-identification skipped the transaction-state refresh: the batch's `BEGIN TRAN` was not pinned and the connection, transaction open, went back to the pool | Core + regression review | each step on its own; the refresh always runs | `ConnectionLifecycleTests.A_failed_identity_read_after_a_run_still_pins…` (failed) |
| 59 | (This pass's first fix for 58 refreshed the state first; the refresh can hand the connection to a queued tab, and the identity read then ran on a connection another borrower owned) | Core re-review | identity read first, refresh after | `ConnectionLifecycleTests.The_identity_read_after_a_run_never_overlaps…` (mutation: "overlapping commands on spid 102: FAKE identity") |
| 60 | The terminated set was per monitor: a second or restarted monitor sent `KILL` again to a session still rolling back (accepted silently by the server); once shared, the monitor that did not prune it never ended its rollback progress | Security review, Core re-review | shared per profile, pruned only by a read of every session; each monitor ends the progress it showed | `K19_a_second_monitor_on_the_profile_refuses…` (failed), `K19_every_monitor_ends_the_rollback_progress…` (mutation: failed) |
| 61 | The Querithm-connection check ran before the kill lock, the control connection and the preview (up to ~10 s): a connection registered meanwhile was not refused | Security review | checked again right before the statement | `K8_a_querithm_connection_registered_while_the_kill_waits…` (failed) |
| 62 | RPC boundary: preview, detail and diagnose accepted ids above SQL Server's range (narrowed to int: 2^32 + 57 read session 57 under a bogus id); the deadlock window check overflowed; kills refused at the boundary were not audited (K24) | Security review | id range as for kills; `from ≥ 0`; ids-only audit lines | `K12_K24_out_of_range_ids…` (failed) |
| 63 | Monitor commands had no driver timeout: a cancel the server never acknowledges (SqlClient sends only an attention) held the job, and stop, reconnect and kill preemption with it | Test + security review | 15 s command timeout on monitor and control connections (above every job's limit) | `Monitor_connections_carry_a_driver_command_timeout…` |
| 64 | Stop did not wait for a running kill: it closed the connection the `KILL` was running on (or left the control connection up to 15 s); a kill queued behind it then reported "may still take effect" for a statement never sent | Core review, Core re-review | kill limits linked to stop; stop pauses the scheduler and waits ≤ 16 s for kills; a kill that starts after stop sends nothing and says so | `C14_stop_waits_for_a_running_kill…` (mutations: failed) |
| 65 | Editing a profile left its monitors on the old host and options while kills read the environment fresh (a Prod → Dev edit removed the typed confirmation for what was still the old server) | Core + regression review | monitors stop when the connection would be built differently (not for a rename or a retention change) | `Saving_a_profile_stops_its_monitors…` (failed) |
| 66 | Staged exports were never deleted: every `.xdl` (query text, logins, hosts) and history export stayed in app data, also after a cancelled save | Security review | sweep of staged files older than 10 min at start, every 5 min and before each export; never through a link or a linked folder | `H7_staged_exports_older_than_ten_minutes…` |
| 67 | Lock names: each of up to 2,000 lock rows read the metadata cache from the local store and scanned it, under the monitor's state lock (ticks, publishes and kills waited) | Core review | one database list and one snapshot per database per job, outside the lock, names built only for locked ids | `B2_lock_names_come_from_one_metadata_cache_read…` (mutation: failed) |
| 68 | Top-query texts were "live" only in the snapshot that carried them: `monitor.texts` could answer "missing" for a top query still on screen (M10) | Core review | the latest top-query rows stay live | code review only |
| 69 | A status-only publish (pause, failed tick) could overtake a full snapshot; the UI then dropped the full one as older (lost locks, top queries, new deadlocks) | Core review | sequence number and notification under one publish lock | code review only |
| 70 | Fixture: `pg_stat_statements` and `pg_wait_sampling` were preloaded but never created in the monitor's database; 5 integration tests failed on a fresh fixture (as on any CI runner) | baseline run here | `gen-schema.mjs --apply` creates both | the 5 tests pass after the fix (D2, D7, D14_D15_D20, T1, T11) |
| 71 | CI ran the integration tests (W10 needs a trimmed publish) before building the release sidecar; W10 threw instead of failing with a reason | Regression review | release build first; W10 asserts the folder with a message | `ci.yml`; W10 |
| 72 | Blocking tree on PostgreSQL: soft blocks make a hot-row pile-up a DAG; every path was expanded (30 waiters ≈ 2^28 rows, rebuilt every tick: the webview froze) | Regression review | each session's victims expanded once (shallowest occurrence), other occurrences marked; 5,000-item cap | Vitest "B4 at scale" (18 waiters: 131,073 items before) |
| 73 | Enter in the kill dialog sent the kill wherever focus was | Regression review | no Enter shortcut (as the other destructive dialogs) | Vitest "Enter never sends a kill" (failed) |
| 74 | Server text shown raw at decision points (kill dialog, deadlock cards, request detail, toasts): bidi overrides, control, zero-width and tag characters | Regression + security review | `escapeBlock` / `escapeForDisplay` over code points, at every decision point | Vitest S4 tests (failed) |
| 75 | New-deadlock toast compared server event time with the webview's clock | Requirements review | the first completed read is history | Vitest X16 ±10 min skew (failed) |
| 76 | Deadlocks pane re-read the whole history on every visit (seconds on the server, monitor busy); a failed read stuck until Refresh; an `error` result showed "No deadlocks" | Regression + requirements review | read once per monitor and on Refresh, re-read once after a reconnect, errors shown | Vitest (failed) |
| 77 | "Hide idle" looked for blockers once per row (O(N²)) | Regression review | one set per snapshot | Vitest (failed) |
| 78 | Tests: S2 flake (the victim's UPDATE hit the driver's 30 s default timeout); the soak asserted nothing on pane errors or lag; H4/H5/K24 could pass without exercising their claim; no unit test of the PostgreSQL sessions reader; D9/D11 untested; M2 could exit without asserting; M1 never read PG timeouts back | Test review | fixed and extended (mutations of gap marks, `Partial`, export text: the tests failed) | `MonitorCoverageTests`, `MonitorServiceTests`, `MonitorTests`, `MonitorSecurityTests`, `Integration/*` |
| 79 | Compiler warnings: CS9124 (a stale captured copy of the PG capabilities), CS8620, CS8604, CS4014, xUnit2030 | build | fixed (no suppression) | build: 0 warnings |

### Not changed, by decision (with the reason)

| Item | Why |
|---|---|
| `ConnectionRegistry.HasReconnected()` refuses kills on every server while one of Querithm's connections is unidentified | Scoping it to the profile is unsafe: two profiles can point at the same server. The refusal message now says why and what to do |
| A failed identity read on a physical open fails the open (ARCHITECTURE §6 rule 7) | On a server where the own-row read itself fails (an explicit `DENY` on `sys.dm_exec_sessions`, a PostgreSQL-compatible server without `pid`/`backend_start`) querying fails too. Degrading (register the connection as unidentified, refuse kills) changes a documented safety rule — left for a decision. **Decided and changed in the completion pass** (degrade, refuse kills) |
| Closing the last query tab under a temporary password retention stops the profile's monitors | The sidecar keeps credentials only while sessions exist; holding them for a monitor would change the retention contract. The monitor says why it stopped |
| A tick cancelled by a kill's fallback reads "timed out (5 s)"; disposal of a few CTS/timers is left to the GC; `lockNames` takes a secondary slot when it has nothing to do; diagnosis ignores wait deltas | Low impact (reviewers' F11, F18–F20) |
| Hand-rolled bracket quoting of the lock-name database | Fixed: through the engine's quoting service |

### Coverage still missing (matrix rows)

*Closed in the completion pass:* M14, O15, B5, X12, D19, P2 on PostgreSQL. Still: S2 on PostgreSQL and
S11 (blocked by the fixture, as before). `MonitorBench` asserts sizes, not its timing targets, and the monitor results
are not in `run.mjs`'s 20 % comparison. Integration tests report "passed" when `QUERITHM_IT` is not set (repo
convention). Not run in this pass: Windows, macOS, a packaged app.

## State at hand-off (2026-10-29) — not complete

*Superseded: every "still to run" step below ran on the final build of the hardening pass (above), on Linux.*

The work moved to another machine mid-verification. The final verification chain on the **final build** (after the
deadlock-poll fix, defect 45) was stopped at its monitor-bench step. On the final build only the build, the
18 deadlock unit tests and part of that bench ran (`bench/results/2026-10-29-07-16-26…07-19-58-monitor-windows.json`, incomplete). Everything in "Gate and checks" below ran on the product build just before that fix (it has
every other fix). For X1, X14 and X16, the matrix's "verified" therefore means verified on that earlier build. Nothing
was committed before this hand-off; this commit contains all the Phase B work.

**Still to run on the final build**, in this order, with the Docker fixture up (`scripts/bench/docker/compose.yml`) and
`QUERITHM_IT=1 QUERITHM_IT_MSSQL_HOST=127.0.0.1 QUERITHM_IT_PG_HOST=127.0.0.1` set:

1. `cd sidecar && dotnet build`
2. `QUERITHM_BENCH_MONITOR=1 dotnet test Querithm.Tests --filter FullyQualifiedName~MonitorBench`. This covers benches 28–32
   and M13; metric 30's first poll now reads only the current file.
3. The deadlock integration tests: `dotnet test Querithm.Tests --filter "FullyQualifiedName~Integration&(FullyQualifiedName~X1_|FullyQualifiedName~X14|FullyQualifiedName~X16|FullyQualifiedName~Deadlock)"`
4. `QUERITHM_SOAK_MINUTES=20 dotnet test Querithm.Tests --filter FullyQualifiedName~MonitorSoak`. Then check that the
   newest `bench/results/*-monitor-soak-*.json` shows no `Deadlocks` entry in `ErrorsByPane` (it was in error in every
   sample before defect 45).
5. `node scripts/run.mjs build-sidecar --release`, then `dotnet test Querithm.Tests --filter FullyQualifiedName~WaitDictionaryTests` (W10)
6. Full gate: `dotnet test` (all, integration on), `vitest run`, `tsc -b`
7. `VITE_BRIDGE_URL=ws://127.0.0.1:1421 vite build`, then `node scripts/bench/smoke-ui.mjs out/ --no-build`
8. `node scripts/bench/run.mjs --ui`, twice, on an otherwise idle machine (28ui on the final build)
9. Last, because it restarts both server containers: `QUERITHM_IT_DISRUPTIVE=1 dotnet test Querithm.Tests --filter FullyQualifiedName~L5_a_`

Notes for that machine:
- `pnpm` was not installed here, so `pnpm test` was run as `vitest run` plus `dotnet test sidecar`.
- On this Windows box `localhost` connections to the fixture were slow or failed, hence the `127.0.0.1` overrides (§16).
- Webview timings follow host load: a loaded run shows unrelated metrics slower too (full parse 18 → 48 ms).
- The bench stops its in-container load through a stop file. Never stop it with `pkill`: the postmaster runs as PID 1 and
  restarts on any orphan that dies by a signal (defect 22).

**Open items**:
- 28ui on the final build (quiet runs passed at 7.7 and 7.8 ms on the previous build).
- Metric 28, SQL Server under active churn: server query p95 above 100 ms, and lag p95 416 ms even on the quiet host. The
  load shares the server container's CPU; this needs a run on a machine where the load does not compete with the server.
- Three intermittent failures, not root-caused: refactor dependency order (1 of 8 full runs), S2 SQL Server blocker list
  (1 run), and one `run.mjs` pipe ENOENT.
- The first soak's unexplained pane errors are probably defect 45 (the soak now records the pane and the reason). Confirm
  with step 4.


## Slices

| Slice | Contract | Provider (SQL Server / PostgreSQL) | Core / RPC | UI | Status |
|---|---|---|---|---|---|
| B0 risk spike | — | probes recorded in the plan's "B0 findings" (rows 1–18; rows 7–16 found by the integration runs and benches, 17–18 by the soak) | — | — | verified (findings are reproduced by integration tests) |
| B1a sessions | `Querithm.Contract/Monitor.cs`, `Providers/IActivityProvider.cs` | `SqlServerActivity.cs` (`SessionsFull`/`SessionsOwn`, `ReadSessionsAsync`), `PostgresActivity.cs` (`SessionsSql`) | `Monitoring/ActivityMonitor.cs`, `TextStore.cs`, `MonitorService.cs`, `RpcServer.cs` (`monitor.*`) | `src/monitor/SessionsPane.tsx`, `model.ts`, `textCache.ts`, `ActivityTab.tsx` | see matrix S*, M* |
| B1b own-tab awareness + banner | `OwnSessionId`, `QuerithmKind`, `SessionBlockingEvent` | `IdentityAsync`, `ConnectionToken`, `BlockedByAsync` | `ConnectionRegistry.cs`, identity read in `SessionManager.OpenConnectionAsync`, re-identify on reuse and after every run, `BlockingBannerService.cs` | `BlockingBanner.tsx`, `monitorActions.ts` (Go to tab) | see matrix O* |
| B1c guarded kill + preview | `MonitorKillParams`, `KillResult`, `KillPreview` (`SessionEnded`) | `TerminateSql` (validated literal), PG identity predicates, `KillPreviewAsync`, `RollbackStatusAsync` | `KillGuard.cs`, `ActivityMonitor.KillAsync` (control connection, preempt fallback, reconnect check, audit) | `KillDialog.tsx`, `killRules.ts` | see matrix K*, P* |
| B2 blocking tree + locks | `BlockingInfo`, `LockRow` | blockers result set, `LocksAsync`, `ResolveObjectNamesAsync` | `BlockingGraph.cs`, lock-name resolution | `BlockingTree.tsx`, `treeModel.ts`, `Tables.tsx` (locks) | see matrix B*, LK* |
| B3 waits, metrics, I/O | `WaitDelta`, `MetricSample`, `IoDelta` | `WaitsSql`/`MetricsSql`/`IoSql` per engine, `SqlServerWaits.cs`, `PostgresWaits.cs` | `DeltaTracker.cs`, `Aggregates.cs` (tick-sampled waits, metrics) | `OverviewStrip.tsx`, `Tables.tsx` | see matrix D* |
| B3b "Why is it slow?" + dictionary | `Diagnosis`, `WaitInfo` | — | `Diagnoser.cs`, `WaitDictionary.cs` + `.json` (embedded, trimmed build) | `SidePanel.tsx` | see matrix W* |
| B4 top queries | `TopQueryRow`, `TopQueryKey` | incremental read + flush check, server-side entry digest, hash/text key only for entries that ran since the cutoff (SQL Server); `pg_stat_statements(false)` + text cache (PG) | `TopQueriesTracker` (128-bit keys + counters, 200,000-entry and 10,000-group bounds) | `Tables.tsx` (top queries) | see matrix T* |
| B5 request detail / live plan (B-next) | `RequestDetail` | live → cached plan (SQL Server), EXPLAIN in a new tab (PG) | `monitor.requestDetail` | `SidePanel.tsx` + `PlanDiagram` | verified (RD1 on both engines; W5; plan chain with fakes: `MonitorCoverageTests.B5_request_detail_*`) |
| B6 history + export (B-next) | `HistoryParams/Result`, `MonitorExportParams` | — | `HistoryBuffer.cs`, `MonitorExporter.cs` | `HistoryPane.tsx`, `exportFile.ts` | see matrix H*, bench 29 |
| B7 deadlock history | `DeadlockEvent`, `DeadlocksResult` | `SqlServerDeadlocks.cs` (ring buffer + event file, XXE-safe), PG counter only | `DeadlocksAsync`, poll, `.xdl` export | `DeadlocksPane.tsx` (+ graph) | see matrix X* |
| Scheduler | — | — | `MonitorScheduler.cs`, `MonitorClock.cs` | — | see matrix C* |
| Connection lifecycle | — | — | `Sessions/Pool.cs` (state machine), `SessionManager.cs`, `ExecutionService.cs` | — | see "Lifecycle invariants" |

## Process requirements (the request's steps)

| Step | Evidence | Status |
|---|---|---|
| 1 checklist | this file; matrix generated from test names / test-body ID comments and the latest TRX + Vitest JSON | verified |
| 2 vertical slices, contract regenerated | `node scripts/run.mjs gen-contract` after every DTO change (last: `KillPreview.SessionEnded`); `src/ipc/contract.ts` never hand-edited | verified |
| 3 high-risk probes first | plan "B0 findings" rows 1–6 before the UI; rows 7–18 from the integration pass, benches and soak, each with its regression test | verified |
| 4 explicit lifecycle | ARCHITECTURE §6 "Physical connection lifecycle" table + invariants; `ConnectionLifecycleTests` (17), `ConnectionLifecycleIntegrationTests` (5, both engines) | verified |
| 5 deterministic interleavings | gates/barriers/fake clock in `ConnectionLifecycleTests`, `MonitorSchedulerTests`, `MonitorServiceTests`; real-engine counterparts in `Monitor*IntegrationTests`; every bug found has a regression test that failed before its fix (list below) | verified |
| 6 snapshot protocol | M5–M11, H1–H4, H8, U1, U2 (incl. the side panel's ended-session state), M6 Seq ordering | verified |
| 7 explicit unavailable data | `PaneState` Degraded/Error/Truncated/ResetDetected/Collecting; nullable metrics never 0 (`Metrics_rates…`, D12, D14); sampled waits labelled (D8, D10); text notes (S6, S18); timed-out panes (L7); rollback-hidden tx ages (B0 row 9) | verified |
| 8 realistic load | bench 28–32 idle + active churn, slow-link (SSH) control connection, reconnects (L4–L6), multiple monitors (soak) — results below | see "Benchmarks" |
| 9 UI smoke | `node scripts/bench/smoke-ui.mjs out/` monitor workflow + L8 — results below | see "Smoke" |
| 10 evidence | commands and outcomes below | see "Gate" |


## Lifecycle invariants (request step 4; ARCHITECTURE §6 "Physical connection lifecycle")

| Invariant | Unit evidence (gates, barriers, fakes that detect overlap / I/O under the lock) | Real-engine evidence | Status |
|---|---|---|---|
| One command at a time per physical connection | `One_command_at_a_time_per_connection_and_no_io_under_the_pool_lock…`; the fake records overlaps in every test (`InvariantSink`); scheduler `C1` | every integration run (SqlClient/Npgsql throw on overlap) | verified |
| No database I/O under the pool lock | same test; `LockHeldByCurrentThread` checked by every fake command | — (a lock-time property) | verified |
| A probing or leased connection is never handed to another borrower | `A_connection_being_validated_is_never_handed…`, `Returning_a_connection_twice…` | `C2_C3_…` (9 tabs, queued borrower) | verified |
| No double dispose / double slot release | `Discarding_twice_disposes_once…`, `A_broken_connection_is_discarded_on_return…`, `A_failed_open_frees_its_slot…` | L4/L5/L6 reconnects | verified |
| Cancelled or bounded queued borrowers pass the slot / connection on | `A_queued_borrower_cancelled_after…` (×2), `A_queued_borrower_whose_queue_limit_fires…`, `A_queue_wait_limit_bounds_only…` | O4 banner on both engines | verified |
| No automatic rollback of a user's transaction; server-side rollback reported | `A_cancelled_statement_inside_a_transaction_keeps_the_session_pinned…` | `ConnectionLifecycleIntegrationTests` (both engines; XACT_ABORT ON on SQL Server) | verified |
| A failed termination never becomes a local termination | kill path never uses a tab connection (`RunKillAsync` comment + K7/K8/O18 refusals); a server-refused kill is `failed` | `K17_a_kill_the_server_refuses_after_the_probe…`, K18 (PG) | verified |
| Cancellation keeps the session usable unless the connection failed | `A_cancelled_statement…`, `A_connection_lost_during_a_pinned_transaction…` | `A_cancel_outside_a_transaction…`, `A_cancel_inside_a_transaction…` (both engines) | verified |
| Querithm's connections are always known (identity on open, on reuse, after every run) | `O17_O19…`, `O2_O3…`, `A_driver_reconnect_during_a_run…`, `K7_a_kill_while_one_of_querithms_connections_reconnected…` | `O1_O16_K6_K7…`, `O2_…` (both engines) | verified |
| Coordination with execution cleanup, pinned connections, queued borrowers | `Closing_a_session_while_its_batch_opens_a_transaction…`, `A_failed_BEGIN_TRANSACTION…`, `Closing_a_pool_fails_its_queued_borrowers…` | C3 | verified |


## Gate and checks (2026-10-29, on the build just before the deadlock-poll fix)

All runs set `QUERITHM_IT=1 QUERITHM_IT_MSSQL_HOST=127.0.0.1 QUERITHM_IT_PG_HOST=127.0.0.1` against the Docker fixture.
`pnpm` is not installed on this machine: `pnpm test` was run as its two halves.

| Check | Command | Outcome |
|---|---|---|
| .NET build | `dotnet build` (in `sidecar`) | succeeded, 0 errors |
| .NET suite, integration on | `dotnet test --logger trx;LogFileName=full-final.trx` | see "Final .NET run" below |
| Vitest | `vitest run` (`pnpm test`'s first half) | 116 files, 1,951 tests passed, 0 errors, exit 0 |
| TypeScript | `tsc -b` | exit 0 |
| Monitor bench | `QUERITHM_BENCH_MONITOR=1 dotnet test --filter MonitorBench` | see "Monitor bench" below |
| Soak | `QUERITHM_SOAK_MINUTES=11 dotnet test --filter MonitorSoak` | passed — see "Soak" |
| Trimmed release self-test (W10) | `node scripts/run.mjs build-sidecar --release`, then `--filter WaitDictionaryTests` | 5/5 passed (`waitDictionary`, `monitorJson ok`, `monitorHistory ok`) |
| Server restarts (L5, disruptive, alone) | `QUERITHM_IT_DISRUPTIVE=1 dotnet test --filter L5_a_` | 2/2 passed (SQL Server 2022, PostgreSQL 17) |
| UI smoke | `node scripts/bench/smoke-ui.mjs out/` | passed (exit 0, no errors), incl. monitor workflow, kill dialog, deadlock toast once, history scrub to the blocking pair, L8 sidecar kill |
| PostgreSQL crash restarts during any step | `docker logs` scan for "terminated by signal" | 0 in every step |
| Security suite | `dotnet test --filter Security` | 65/65 passed (also inside the full run) |
| Vulnerable packages | `dotnet list sidecar package --vulnerable --include-transitive` | none (no package added or changed; `cargo audit` not applicable: no Rust dependency changed) |
| Engine + UI benchmarks | `node scripts/bench/run.mjs` | see "Benchmarks (`run.mjs`)" |

### Final .NET run, Vitest, TypeScript (2026-10-29 13:03–13:10, host CPU 67 %)

| Suite | Result |
|---|---|
| `dotnet test` (all, `QUERITHM_IT=1`) | **1,762 / 1,762 passed** (`full-final.trx`) |
| `vitest run` | **116 files, 1,951 tests passed, 0 errors, exit 0** (`vitest-final.json`) |
| `tsc -b` | exit 0 |

Earlier full runs on the same day failed intermittently on one test each — each was investigated; the fixes are defects
36–46 below. Two did not recur and are not root-caused (see "Not verified here").

### Monitor bench (`QUERITHM_BENCH_MONITOR=1 dotnet test --filter MonitorBench`)

Host load decides the active-churn and webview figures on this laptop (the same build measured 2–3× apart within an hour),
so two runs are shown: the quietest (host CPU 10 %, `2026-10-29-04-52-06-monitor-windows.json`) and the latest
(host CPU 44 %, the bench took 16 min instead of 8; `2026-10-29-06-15-19-monitor-windows.json`). Both passed 8/8.

**Quiet host:**

| Bench | Measure | Measured | Target | Result |
|---|---|---|---|---|
| 29 | scrubMs | p95 5.34 (median 1.89) | ≤ 50 | pass |
| 30 | serverReadAndParseMs | value 1295.97 | ≤ 2000 | pass |
| 30 | parseTotalMs | value 5.63 | ≤ 200 | pass |
| 30 | parsePerEventMs | median 0.03 | ≤ 5 | pass |
| 28-pg-idle | serverQueryMs | p95 14.21 (median 9.11) | ≤ 100 | pass |
| 28-pg-idle | sidecarProcessingMs | p95 0.45 (median 0.29) | ≤ 10 | pass |
| 28-pg-idle | payloadBytesPerTickWithoutNewTexts | median 198649 | ≤ 524288 | pass |
| 28-pg-idle | firstSnapshotBytes | value 195316 | ≤ 2560000 | pass |
| 28-pg-idle | tickLagMs | p95 14 | ≤ 200 | pass |
| 28-pg-idle | sidecarCpuPercent | value 0.21 | ≤ 2 | pass |
| 28-mssql-idle | serverQueryMs | p95 62.03 (median 58.15) | ≤ 100 | pass |
| 28-mssql-idle | sidecarProcessingMs | p95 1.54 (median 0.26) | ≤ 10 | pass |
| 28-mssql-idle | payloadBytesPerTickWithoutNewTexts | median 190215 | ≤ 524288 | pass |
| 28-mssql-idle | firstSnapshotBytes | value 189840 | ≤ 2560000 | pass |
| 28-mssql-idle | tickLagMs | p95 17 | ≤ 200 | pass |
| 28-mssql-idle | sidecarCpuPercent | value 0.33 | ≤ 2 | pass |
| 28-pg-active | serverQueryMs | p95 60.43 (median 42.41) | ≤ 100 | pass |
| 28-pg-active | sidecarProcessingMs | p95 0.84 (median 0.57) | ≤ 10 | pass |
| 28-pg-active | payloadBytesPerTickWithoutNewTexts | no sample | ≤ 524288 | n/a |
| 28-pg-active | firstSnapshotBytes | value 199263 | ≤ 2560000 | pass |
| 28-pg-active | tickLagMs | p95 14 | ≤ 200 | pass |
| 28-pg-active | sidecarCpuPercent | value 0.1 | ≤ 2 | pass |
| 28-mssql-active | serverQueryMs | p95 211.67 (median 174.1) | ≤ 100 | **over** |
| 28-mssql-active | sidecarProcessingMs | p95 2.56 (median 0.7) | ≤ 10 | pass |
| 28-mssql-active | payloadBytesPerTickWithoutNewTexts | median 198952 | ≤ 524288 | pass |
| 28-mssql-active | firstSnapshotBytes | value 199070 | ≤ 2560000 | pass |
| 28-mssql-active | tickLagMs | p95 416 | ≤ 200 | **over** |
| 28-mssql-active | sidecarCpuPercent | value 0.55 | ≤ 2 | pass |
| 31-pg | fullReadMs | median 20.93 | ≤ 100 | pass |
| 31-pg | incrementalReadMs | no sample | ≤ 100 | n/a |
| 31-pg | deltaAndTop100Ms | p95 4.71 (median 1.5) | ≤ 20 | pass |
| 31-pg | fullReadTrackerUpdateMs | median 3.32 | ≤ 20 | pass |
| 31-mssql | fullReadMs | median 86.14 | ≤ 1500 | pass |
| 31-mssql | incrementalReadMs | p95 64.92 (median 63.31) | ≤ 300 | pass |
| 31-mssql | deltaAndTop100Ms | p95 0.07 (median 0.03) | ≤ 20 | pass |
| 31-mssql | fullReadTrackerUpdateMs | median 2.29 | ≤ 20 | pass |
| 31-mssql-T10-long-running-request | incrementalReadMs | p95 84.02 (median 77.74) | ≤ 300 | pass |

Over target on the quiet host: SQL Server **under active churn** only — server query p95 212 ms and tick lag p95 416 ms
(the churn runs inside the server's container and shares its CPU: a trivial `COUNT(*)` slows by the same factor, see the
profile below; earlier runs on quieter moments measured lag p95 15–22 ms). PostgreSQL active, both idle cases, history (29),
deadlocks (30), top queries (31), payload (M13, now asserted) are within target.

**Loaded host (latest):** 8 measures over target — the four "active" SQL Server / PostgreSQL server-query and lag
measures, SQL Server idle server query (251 ms) and lag (301 ms), deadlock server read (4.7 s), and top-queries T10 (433 ms).
Not a code regression: no monitor product code changed between the two runs except the SPID refresh after runs (fix 42).

### Soak (20 min, four monitors at 1 s, churn on both engines; `2026-10-29-06-36-02-monitor-soak-windows.json`)

Passed. Managed heap +10.7 MB over the warm window (one step at ≈ 9 min, then flat at 41–43 MB: the bounded text cache
filling); **working set −1.3 MB** (its spikes to 290–312 MB at 14.7 and 18.8 min follow the GC's committed bytes and return:
GC retention, not a native leak — this answers the working-set step seen in an 11-min soak); threads −2; one monitor
connection per monitor on each server in every sample; registry 4–5 entries; nothing left on either server afterwards;
P2 (a PostgreSQL write transaction open 11 min) read as high risk. **But:** the SQL Server deadlocks pane was in error in
every sample ("did not finish within its time limit") — defect 45, fixed afterwards; **the soak has not been rerun since
that fix** (see "Hand-off").

Earlier soaks: before the memory fixes, heap +40.5 MB in 11 min (defects 24–26); after them, +2.5 to +2.7 MB in 11 min.

### UI smoke (`node scripts/bench/smoke-ui.mjs out/`)

Passed on the build before the deadlock-poll fix (12:51): every Phase B step — sessions, blocking tree, "Why?", guarded
kill with the high-risk confirm, own-tab banner, Prod guard, deadlock list + graph, the new-deadlock toast exactly once,
pause/resume, history scrub to the blocking pair (keyboard, as a user scrubs), tab close / disconnect leave 0 monitor
sessions, the sidecar kill (L8) shows the stopped state with 0 orphan connections.

### Webview metrics (`node scripts/bench/run.mjs --ui`)

| Metric | Target | Quiet runs (host CPU 6–11 %) | Loaded runs (13–67 %) |
|---|---|---|---|
| 28ui snapshot apply, 500 sessions (p95) | ≤ 16 ms | **7.7, 7.8 ms** (build before the last cell-cache change) | 13.3–25.9 ms |
| 28first first snapshot with texts | ≤ 50 ms | 2.5, 2.8 ms | 5.1–10.2 ms |
| 30list 200-event list first paint | ≤ 100 ms | 24.1, 26.4 ms | 41.7–130.8 ms |
| 30graph 10-process graph render (median) | ≤ 50 ms | 16.3 ms | 15.6–17.1 ms |

28ui was 21.1 ms before the fix (defect 30). It is **not verified on the final build**: the final build was only measured
on a loaded host (13.3 ms and 19.0 ms). Engine metrics unrelated to Phase B moved the same way in those runs (full parse
18 → 48 ms), which is how a loaded run is recognised.

### Profiles and measurements behind the design changes

- **Top-queries full read, SQL Server, A/B over 28,144 entries, 9 alternating rounds (PowerShell + SqlClient):** before
  the compact tracker 210 ms; with a server-side `HASHBYTES` key 366 ms; with the raw plan handle hashed in the sidecar
  159 ms (shipped). Bench 31 full read on the quiet host: 119 ms.
- **Tracker memory per entry:** ~800 B before (heap dumps: 72,197 `TopQueryCounter` objects for a ~30,000-statement
  plan cache), 151 B after (`The_top_queries_tracker_keeps_no_strings_per_tracked_entry`).
- **Text store:** 78 B counted vs 451 B held per short text before; counted ≥ held after.
- **Scheduler:** 14,957 KB retained by 50,000 idle waits before, < 1 MB after.
- **28ui, Vitest/Node, median per steady tick:** parse 0.64 ms, apply 0.18 ms, changed cells 8.63 ms (text column 6.28 ms)
  before; total 5.1 ms (changed cells 3.4 ms) after.
- **Deadlock poll, inside the SQL Server container:** priming over all ten rollover files (10,688 events) 1.0 s idle /
  3.3–3.4 s under churn; the current file alone 25 ms.
- **Wait statistics cleared on an idle server:** 26 non-zero wait types → 1 right after → 9 a second later (D2).

### Measurements that changed the design (kept as evidence; the files were superseded by the final runs)

| What | Before | After | Change |
|---|---|---|---|
| Metric 29, 10 min at 2 s with 500 sessions | 187 of 300 ticks kept at the cap, heap +56.2 MB (estimate 52.4 MB) | 300 ticks, heap +14.3 MB, scrub p95 9.1 ms | compressed history ticks (§16) |
| Soak heap, 4 monitors, default 10-min history | +26.8 → 154 MB over 10 min, then flat (the history filling) | soak run with 1-min retention so growth after it would be a leak | soak design |
| Metric 28 active, sidecar CPU | 10.4 % (PG) / 2.0 % (SQL Server) — the load generator ran in the measured process | load generated inside the containers | bench design |
| Metric 28 active, server query p95 | 278 ms (PG) / 479 ms (SQL Server) with the in-process load | see the final table | — |
| Kill via control connection while a job runs (C2), PostgreSQL | 2,077–2,286 ms (`localhost`: 2.27 s of it opening the connection) | 41 ms via 127.0.0.1 | fixture host override (§16) |
| Deadlock history read (SQL Server, 200 events) | 1.4 s with ~5 MB of `system_health` files; 5.7 s with 30 MB — and the 30 s poll did this every time | poll: ring buffer only (final table); pane: same file read, 8 s bound | defect 18 |
| PostgreSQL wait profile read per tick | 241 ms with 121,301 entries / 56,801 past pids | timed; tick-sampled fallback after 3 reads over 50 ms | defect 19 |
| Banner check on PostgreSQL | skipped every cycle (the 1 s bound cut the 2 s localhost open) | checks run | queue-only bound (defect 2) |

**Profile of the active-load server query (SQL Server, timed inside the container with `sqlcmd`, 10 runs each):**
a trivial `COUNT(*) FROM sys.dm_exec_sessions` took 18 ms idle and 74 ms under 8 churning clients; the monitor's
sessions query took 28 ms idle and 121 ms under churn (each figure includes sqlcmd start-up and connect). The query
slows by the same factor as a trivial one: the server's CPU is shared with the load, not a cost the query adds.


## Defects found by verification (each fixed; the last column names the test or run that shows the fix — a unit test that failed before it where one could be written)

| # | Defect | Found by | Fix | Regression test |
|---|---|---|---|---|
| 1 | Pool: a borrow cancelled during the idle-validation probe leaked its slot; cancelled granted waiters lost wake-ups / connections; closed sessions pinned connections; failed BEGIN leaked the borrow | lifecycle state table (step 4) | `Pool.cs` state machine with CAS transitions and hand-off grants | `ConnectionLifecycleTests` (first 7 facts) |
| 2 | Banner check: the 1 s bound also covered opening the check connection; on PostgreSQL (a ~2 s localhost connect here) every cycle was skipped and the banner never checked | O4 on PostgreSQL | the bound covers only the queue (`maxQueueWait`, `poolBusy`) | `O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool`, `A_queue_wait_limit_bounds_only_the_wait…` |
| 3 | PG probes used `LIMIT 0`, which never calls `pg_stat_statements()` / `pg_wait_sampling_profile`: a server without the preload was reported as having top queries | T4 on the bare server | `LIMIT 1` | `T4_D19_on_a_server_without_preloaded_extensions…` (integration) |
| 4 | SQL Server connections inherit `FireInfoMessageEventOnUserErrors = true`: on monitor connections a denied DMV read returned an empty "ok" pane and a `KILL` refused by the server (6102) was reported as terminated | S14 (revoke mid-run) | `IActivityProvider.PrepareMonitorConnection`; banner query toggles it | `S14_…`, `K17_a_kill_the_server_refuses_after_the_probe…` (integration), `Monitor_connections_on_sql_server_raise_user_errors…` |
| 5 | The own-sessions query (no VIEW SERVER STATE) applied `dm_exec_sql_text`, denied even for the login's own session: low-privilege monitors showed an empty session list as "ok" (hidden by defect 4) | S18 after fix 4 | no text in own mode, rows say why; request detail returns the reason | `S18_K29_…` (integration, now asserts the own row), `S18_without_server_state_the_sessions_query_reads_no_denied_dmv…` |
| 6 | `sys.dm_tran_session_transactions` blocks (MSQL_XACT_MGR_MUTEX) while a terminated idle session rolls back, and a client cancel is not processed meanwhile: every tick and the kill preview froze for the rollback's length | P5 (preview timed out) | `@rb` startup-filter guard on all three reads; ages shown as unavailable with the reason | `P5_…` (integration), `While_a_rollback_runs_the_session_transaction_dmv_is_not_read…` |
| 7 | A cancelled/timed-out secondary job (locks, top queries, deadlocks) left its pane as it was: stale data looked current | L7 | pane → error "did not finish within its time limit … retrying" | `L7_a_dmv_query_that_never_returns…` |
| 8 | A kill of a session that ended after the snapshot asked for the long-rollback confirm ("risk unknown") | K5 | `KillPreview.SessionEnded` → `gone` | `K5_a_session_that_ended_after_the_snapshot…`, `K4_K5_…` (integration) |
| 9 | SQL Server: a MARS session (several request rows) appeared twice | S8 | first row per session id | `S8_a_session_with_two_request_rows…` |
| 10 | Event-file read errors other than `SqlException` escaped; untestable fallback | X9 | catch `DbException` | `X9_an_unreadable_event_file_falls_back…` |
| 11 | XACT_ABORT ON: a cancel/error made the server roll the user's transaction back silently | B0 row 3 re-check | post-execution warning "has ended on the server" | `With_xact_abort_on_a_cancel_rolls_the_transaction_back…` (integration) |
| 12 | A pooled connection that SqlClient reconnected during a run was re-identified only at its next borrow: meanwhile the tab's server session looked foreign to the monitor and passed the kill guard | O2 on SQL Server | re-identify after every run; kills refused while any registered connection reconnected unidentified | `A_driver_reconnect_during_a_run_is_identified_again…`, `K7_a_kill_while_one_of_querithms_connections_reconnected…`, `O2_…` (integration) |
| 13 | SQL Server top queries noticed a plan-cache flush only at the next 30th run (up to 150–300 s) | T1 | cache size from `dm_os_memory_cache_counters` on each incremental read; a 90 % drop triggers a full read | `T1_a_plan_cache_flush_seen_on_an_incremental_read…`, `T1_clearing_the_statement_statistics…` (integration) |
| 14 | The side panel kept showing a diagnosis/detail for a session that had ended | smoke screenshot 20 | "Session N has ended" notice tied to the exact key | `SidePanel (U2)` Vitest |
| 15 | Smoke: tab activation used synthetic events (dockview needs pointer events); an edit collapsed `page.$$` | smoke run 1–2 | real clicks | the smoke run itself |
| 16 | Explorer test still expected the pre-Phase-B server menu; a Vitest fixture lacked the new `sessionEnded` field (tsc) | full Vitest + `tsc -b` | test updated | `Explorer.test.tsx`, `tsc -b` |
| 17 | History held object graphs: 10 min of 500 sessions did not fit the 50 MB cap (187 of 300 ticks, 56 MB of heap, the cap ran on an estimate) | metric 29 | ticks stored as Deflate-compressed JSON, keyframe every 10 ticks, cap counts the bytes held | `M29_…` bench (300 ticks, 14 MB), `H2_…`, trimmed `--self-test` (W10) |
| 18 | The 30 s deadlock poll re-read all `system_health` event files every time (5.7 s for 30 MB). A first fix (ring buffer only) was wrong: the ring buffer's XML is capped at 4 MB and then omits the newest events — the smoke test's deadlock toast never came | metric 30, then smoke X16 | the poll resumes in the files after its last position (file + offset); full reads only for the pane; 8 s bound | `X16_the_poll_reads_the_event_files_once_then_only…`, `X1_X14_…` (integration: the poll delivers a new deadlock), smoke X16 |
| 19 | PostgreSQL `pg_wait_sampling` with `profile_pid = on` keeps every past backend: the per-tick read took 241 ms after churn and kept growing | metric 28 (PG idle p95 crept 34 → 102 ms over runs) | profile read timed separately; 3 reads over 50 ms → tick-sampled waits with the reason | `A_pg_wait_sampling_profile_that_got_costly_to_read…` |
| 20 | Tool runs (grid edits, imports, syncs) did not re-identify a reconnected connection (defect 12's other path) | review | same re-identification after `RunToolAsync` | `A_driver_reconnect_during_a_tool_run…` |
| 21 | Bench harness: concurrent deadlock seeding produced multi-victim reports (26 events for 220 deadlocks); a `NaN` result broke every later save; the active load ran inside the measured process | bench runs | seed one at a time; `NaN`-safe save; load generated inside the containers | the bench itself |
| 22 | Bench/soak harness: stopping the in-container load with `pkill` crash-restarted PostgreSQL (soak ended in `57P03`). Not a pattern hitting a backend: the parent `bash -c` died first, its loop subshell and `psql` children were re-parented to the container's PID 1 — the postmaster — which treats any child that dies by a signal as a crashed backend ("server process (PID n) was terminated by signal 15", restart) | soak end; reproduced on demand (`pgstop.sh`: crash at 10:06:51 UTC, the dying "server process" was the load's own subshell) | the load stops through a stop file: loops end after their statement, the script kills only `pgbench`, its own child, and reaps everything; a load that does not stop within 20 s fails the run | prototype: stop in 92 ms, rc 0, no crash line; the soak / bench runs after the fix |
| 23 | `run.mjs` measured a stale sidecar: it built the Release sidecar only when the binary was missing, and `bin/Release/net10.0` dated from 2026-10-27, before Phase B. The current web UI against it never showed the editor; that failure was printed but the run ended "0 metric(s) outside target" with exit 0; its web build went through `pnpm`, absent here, and failed silently | `run.mjs` ui metrics (`.monaco-editor textarea` never appeared) | incremental Release build on every run; web build via node (`tsc -b`, `vite build`), a failed build stops the run; a ui-metrics failure fails the run; the two engine runs of the day (09:31, 10:02) were discarded as evidence | the rerun after the fix |
| 24 | Scheduler: each wait for the next due job attached a `ContinueWith` to the wake signal, which completes only when something signals the loop (an interactive request, a kill). An idle monitor kept ~300 B per wait for its lifetime (1,052 continuations in 3 min across four monitors; ~39 MB a day per monitor at 1 s) | soak heap dumps (`dotnet-gcdump`, 3 min apart) | `Task.WhenAny`, which takes its continuation off the task that did not finish | `An_idle_monitor_holds_no_more_memory_after_50000_waits_between_jobs` (14,957 KB retained before, passes after) |
| 25 | Top queries: the tracker held every cached statement as the provider's record with plan-handle, sql-handle and query-hash strings (~800 B each): 72,197 records in two SQL Server monitors for a ~30,000-statement plan cache; on a large server (160k+ statements) ~130 MB per monitor. Since-start totals grew with every new query shape | soak (heap +40.5 MB in 11 min, after a restart emptied the plan cache) + heap dumps | 128-bit key + seven counters per entry (151 B measured); SQL Server digests plan handle + offsets on the server and sends hash / text key only for entries that ran since the cutoff; 200,000-entry cap (pane stops with the reason, restarts from a full read that fits); 10,000 since-start groups (oldest dropped, with a note) | `The_top_queries_tracker_keeps_no_strings_per_tracked_entry`, `Over_the_tracking_cap_…`, `Since_start_totals_keep_…`, `SQL_Server_top_queries_carry_hash_and_text_key_only_…`; T1/T9/T11/T4 integration rerun |
| 26 | Text store: the 4 MB cap counted UTF-8 text bytes; a short statement holds ~6× that on the heap (78 B counted, 451 B held), so the cap allowed ~23 MB per monitor — and B6's 50 MB history cap counted history texts the same way | soak + heap dumps | sizes are UTF-16 characters + a measured 360 B per text | `The_text_store_counts_the_memory_its_texts_hold…` (78 vs 451 B before) |
| 27 | Metric 31 (PostgreSQL) never measured a full `pg_stat_statements`: its fill varied only constants, comments and aliases, which the view folds into one entry — 5,500 statements made ~100 entries (63 and 83 read in two runs; one of them also after a crash restart, defect 22, which clears the view) | reading the bench results | the fill varies structure (100 target-list lengths × 55 AND chains) after a reset: 4,764 entries | the rerun: full read 26 ms, tracker update p95 7.2 ms |
| 28 | Unit tests: L13 (start/stop 1,000×, process heap < 8 MB) and B10 (1,000-node tree ≤ 5 ms, the evidence for that row) measure the whole process but ran beside other classes: L13 failed in 3 of 3 full runs (+30 MB of other tests' live data; 1.5 MB alone) and already once in an earlier run today; B10 failed once. L13's message also printed a different figure from the one it asserted | full-suite reruns | both classes run in the serial collection; the message prints the asserted growth | 4 of 4 full runs green afterwards |
| 29 | Smoke: the history step depended on run speed. On a lightly loaded run the kill came within ~1 s of the monitor's first tick, so no retained tick held the blocking pair ("history scrub: no tick shows the blocking pair") | final smoke run | wait two ticks (2 s interval) after the pair is shown, before the kill | the smoke rerun |
| 30 | Webview metric 28ui (500-session steady tick ≤ 16 ms) failed at 21.1 ms p95 — the first time the metric ran (earlier UI runs never reached it, defect 23). Profile (Vitest/Node, median per tick): parse 0.64 ms, apply 0.18 ms, changed-cell damage 8.63 ms, of which the text column 6.28 ms: both snapshots' rows were formatted again each tick, and every 400-character text was escaped character by character | `run.mjs` ui metrics | the comparison uses what each row displayed when it was new (formats only the new rows); `escapeForDisplay` returns the string itself when nothing needs escaping | `U1: a tick formats only its own rows…` (formats 2, not 4, per changed tick), `S4: text with nothing to escape…`; metric 28ui rerun |
| 31 | Vitest: one unhandled rejection ("dev bridge not reachable") — the query tab now renders the blocking banner, which subscribes to monitor notifications through the real transport, and the QueryTab tests did not stub it | final gate (all 116 files passed, exit 1) | the QueryTab tests stub the banner like the tab's other panels (the banner has its own O8/O9 tests) | full Vitest run: 1,951 passed, 0 errors, exit 0 |
| 32 | Smoke: the history step set the slider value from script with a synthetic `input` event; on the final builds that never reached React's change handler (every position read "live"; one scrubber, enabled, visible). Why it stopped working was not established | final smoke runs, with a diagnostic message added | the step scrubs as a user does: focus, Home, arrow keys | smoke rerun: the tick at 6:11:27 PM shows "1 blocked", head blocker 61 |
| 33 | History pane: the snapshot table had no cell padding; status and wait ran together ("suspendedLCK_M_IX61") | smoke screenshot | padded, unwrapped cells | smoke screenshot |
| 34 | Bench 28: "sidecar processing p95" came from 14 samples, i.e. the slowest one (0.8–14.8 ms across runs, median steady at 0.3–0.9 ms) | over-target PG active run (11.7 ms) | 60 samples; p95, max and the number of samples with a GC reported | rerun: p95 0.9–3.5 ms, max 8.1 ms on all four cases |
| 35 | Fix 25 regressed the SQL Server full top-queries read: `HASHBYTES` in the query cost the server more than it saved (A/B, 28,144 entries, 9 alternating rounds: 210 ms before, 366 ms with `HASHBYTES`, 159 ms with the raw handle) | bench 31 (450 ms vs 226 ms before; one sample each, so an A/B was run) | the sidecar hashes the raw plan handle + offsets (SHA-256, 128 bits) | `SQL_Server_top_queries_carry_…` (keys from the raw handle); T1/T9/T10/T11 rerun; bench 31 rerun |
| 36 | W8 (every PostgreSQL 17 wait event described) failed once: L5 on PostgreSQL restarts the PG 17 container, which W8 also uses, and was not opt-in like the SQL Server L5, so it ran beside the suite | final gate (connection cut mid-handshake at the restart) | the PG 17 restart is opt-in (`QUERITHM_IT_DISRUPTIVE=1`) and runs alone with the SQL Server one | full suite 1,760/1,760; both L5 alone |
| 37 | S7/B5 integration test assumed the first snapshot of a parallel query shows a wait; a tick can catch every task running, and no wait is then correct | final gate | waits for a tick with the longest task wait; asserts one row per session in every tick | 3/3 reruns |
| 38 | T11 (PostgreSQL) counted the test's own `CREATE TEMP TABLE` (it carries the tag; `pg_stat_statements` tracks utility statements) when the baseline read came before it: "exec deltas 1,7" | top-queries rerun after fix 35 | counts only the SELECT under test | 3/3 reruns on both engines |
| 39 | D2: after the wait statistics are cleared, SQL Server's read (non-zero counters only) loses nearly every wait type — 26 non-zero types became 1 right after a clear, 9 a second later, nearly all benign — so no counter went down and no reset was reported (D2 failed 2 of 3 runs alone on an idle server; it passed in the gates only because other tests' waits came back lower) | final gates, then alone | a read that lost ≥ 90 % of ≥ 10 previous keys is a reset (`DeltaTracker`) | `D2_a_read_that_lost_nearly_every_counter_was_cleared_and_is_a_reset` (failed before); D2 integration 4/4 |
| 40 | Integration helper `WaitSnapshot` looked only at the newest snapshot on each 100 ms poll: a state that lasts one tick (ResetDetected) was missed when the poll resumed late | D2 analysis | checks every snapshot from the call on, in order | D2 / full suite |
| 41 | Bench harness: the monitor and deadlock webview metrics ran after the ERD step, which the harness notes allocates heavily (GC pauses then land in their samples); the changed-cell cache allocated a `Map` per row per tick | 28ui variance (7.7–25.9 ms across runs) | ERD last again; one small array per row | 28ui reruns |
| 42 | After a transparent reconnect during a run (SqlClient connection resiliency on a killed idle connection), the tab kept reporting the dead session's SPID: the run read `ServerProcessId` (no round trip) before its command, i.e. before the reconnect; the registry was re-identified (fix 12) but the session's SPID was not | O2 integration test, intermittent (only when the reconnect is transparent) | the SPID is read again right after every run and tool run, with the identity | `A_driver_reconnect_during_a_run_is_identified_again…` now asserts the SPID (101 vs 102 before); O2 3/3 on both engines |
| 43 | Fix 40's helper returned the first matching snapshot; the first tick showing a blocking pair can lack detail later ticks have (B1 failed once: the victim's row had no text yet) | full suite | newest matching snapshot since the call, newest first (one-tick states are still found) | B1, D2, O1 3/3 |
| 44 | Soak diagnostic (added in this session) enumerated the monitor's text store from the test thread while a tick changed it: "Collection was modified" ended a 20-min soak at 7 min 49 s | soak | read under the monitor's state lock (`ActivityMonitor.TextStats`) | the 20-min soak |
| 45 | SQL Server deadlock poll failed on every run for a whole 20-min soak ("did not finish within its time limit"): its priming read scanned every `system_health` rollover file (10,688 events: 0.9 s idle, 2.8–3.4 s under churn, inside the container; more with the host loaded), and a read cut by the poll job's 10 s limit reset the cursor, so each poll repeated it | 20-min soak with pane errors recorded (1,595 error states, all this pane); timings with `sqlcmd` in the container | priming reads only the file being written (from the `event_file` target: 25 ms for its 138 events); two failed or cut primings and the poll gives up on the files, ring buffer only, with the reason | `X16_the_poll_reads_the_event_files_once…` (asserts the current file), `X16_a_priming_read_cut_twice…`; deadlock integration tests; soak and smoke reruns |
| 46 | S16 integration test matched its session by spid only: a snapshot older than the new connection held the spid's previous owner (it read another test's "SELECT DB_NAME(), …") | final gate | matched by program name too | 3/3 reruns |


## Not verified here, and why

| Item | Why | What does cover it |
|---|---|---|
| S2 on PostgreSQL (> 1,000 sessions) | needs more than 1,000 backends; the fixture is sized `max_connections = 600` (plan spec) and ~1,000 backends need several GB in Docker Desktop | S2 unit test (truncation, priority order); S2 on SQL Server with 1,000 extra connections |
| S11 integration (client/server clock skew) | the fixture's containers share the host clock: no skew can be produced | S11/S12 unit test asserts server-side expressions only (`DATEDIFF_BIG(…, SYSDATETIME())`, `now()`/`clock_timestamp()`) |
| Managed platforms (Azure SQL Database, RDS, Cloud SQL, managed PostgreSQL roles) | no such fixture (plan: "covered by fake probe results") | D16, D18, K15, X10 unit tests from probe results |
| Older engine versions (SQL Server 2016–2019; PostgreSQL 12–15; `pg_stat_statements` < 1.8) | fixture has SQL Server 2022, PostgreSQL 16 and 17 only | probe-driven code paths unit-tested with fake probes / column sets (T3, D13, D14, the `HAS_PERMS_BY_NAME` 2022 fallback) |
| Sidecar restart under a monitor (L8) in the real app | the dev bridge reports the sidecar dead but does not restart it (the Rust shell does) | smoke: "monitor stopped" shown, no orphan connection on the server |
| Native WebView2 / packaged app | smoke and UI benches run the built webview in headless Chrome through the dev bridge | — |
| Linux and macOS | not run in this session (Windows only) | — |
| Banner Commit/Rollback on Prod (O8) | the tab's transaction commands have no Prod prompt of their own; the banner reuses them unchanged | O8 Vitest + smoke |
| Intermittent: `RefactorIntegrationTests.Dependent_modules_are_dropped_and_recreated_in_dependency_order` (SQL Server, rename feature from an earlier phase) | failed once in eight full runs today (the view → function dependency was missing from the drop order); passes 5/5 alone and in the other seven full runs; not root-caused | — |
| Intermittent: S2 on SQL Server (1,000+ sessions, the blocked session's blocker list) | failed once in the full suite (the victim was listed but its blocker list did not name the holder); passes 3/3 alone and in later full runs; the assertion now prints the actual blocker list; not root-caused | — |
| Intermittent: one `run.mjs` run could not reach the sidecar's pipe (ENOENT after 10 s) | not reproduced in the next five runs; sidecar stderr is discarded by the harness, so the cause is unknown | — |
| Webview timings on this laptop | the host shares its CPU with the user's applications: the same build measured cold start 991–4,239 ms and 28ui 7.7–25.9 ms across runs within an hour; only runs with the host near idle are evidence | — |
| Metric 28, SQL Server under active churn: server query p95 > 100 ms (232–371 ms) | the server's CPU is shared with the churn inside the same container: a trivial `COUNT(*)` slows by the same factor (18 → 74 ms) as the monitor's query (28 → 121 ms) | idle p95 83–114 ms (at the target's edge on this host); PostgreSQL within target idle and active |
| Metric 30: `system_health` server read > 2 s (2.6–4.0 s for 200 events) | the event-file function parses every event of every file; the fixture's files grow with each bench's 200 seeded deadlocks (≈ 30 MB). The 30 s poll reads only what is new (0.6–0.8 s), the pane's full read is bounded at 8 s | parse within target (18 ms); plan B0 row 15 |
| Metric 31 on a 50,000-entry plan cache | the fixture's SQL Server keeps 17,000–30,000 statements (each ad hoc plan ≈ 67 KB of its 5 GB target): the bench reports the entries it read | full read 159–450 ms at 27–28k entries, target 1.5 s |
| Top queries on a server with more than 200,000 cached statements | by design the pane stops with the reason (memory bound, plan B0 row 17); not exercised on a real server that large | `Over_the_tracking_cap_…` unit test |
| First soak's pane errors (≈ 30 per 30 s in its first 6.5 min, right after a SQL Server restart) | not reproduced in four later soaks; that run did not record which pane or why (the soak now does) | later soaks: 0 pane errors |
| Metrics outside target before Phase B (yesterday's baseline): Tier-1 completion (#4), data compare (#22), import (#23), tab switch (#7) | not in Phase B's scope; equal or better than the pre-Phase B baseline on quiet runs | baseline file 2026-10-27-05-45-28-windows.json |


## Matrix

| # | Case | Type | Tests (file, name) | Latest result | Status | Limitation / blocker |
|---|---|---|---|---|---|---|
| K1 | `ConfirmedKill = false` | S | `MonitorTests.cs` K1_K2_K3_K4_confirmations_and_snapshot_membership_are_che…<br>`MonitorSecurityTests.cs` K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_s… | passed | **verified** | — |
| K2 | Prod profile, `ConfirmedProd = false` | S | `MonitorTests.cs` K1_K2_K3_K4_confirmations_and_snapshot_membership_are_che…<br>`MonitorSecurityTests.cs` K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_s… | passed | **verified** | — |
| K3 | Spid not in latest snapshot | S | `MonitorTests.cs` K1_K2_K3_K4_confirmations_and_snapshot_membership_are_che…<br>`MonitorSecurityTests.cs` K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_s… | passed | **verified** | — |
| K4 | Spid reused (same spid, new `login_time`/`backend_start`) | S, I | `MonitorMatrixTests.cs` K4_K5_a_session_that_ends_after_the_snapshot_is_reported_…<br>`MonitorTests.cs` K1_K2_K3_K4_confirmations_and_snapshot_membership_are_che…<br>`MonitorSecurityTests.cs` K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_s… | passed | **verified** | — |
| K5 | Session ends between check and kill | I | `MonitorMatrixTests.cs` K4_K5_a_session_that_ends_after_the_snapshot_is_reported_…<br>`MonitorServiceTests.cs` K5_a_session_that_ended_after_the_snapshot_is_gone_not_a_… | passed | **verified** | — |
| K6 | Target = monitor's own session | S | `MonitorIntegrationTests.cs` O1_O16_K6_K7_querithm_connections_are_labelled_and_never_…<br>`MonitorTests.cs` K6_K11_K14_K21_K29_own_monitor_system_sessions_pseudo_blo…<br>`MonitorSecurityTests.cs` K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_s… | passed | **verified** | — |
| K7 | Target = a Querithm connection: leased by a tab, idle in the pool, or dedicated (debugger, the monitor's control connection) | S | `MonitorIntegrationTests.cs` O1_O16_K6_K7_querithm_connections_are_labelled_and_never_…<br>`MonitorServiceTests.cs` K7_a_kill_while_one_of_querithms_connections_reconnected_…<br>`MonitorTests.cs` K7_K8_a_connection_registered_after_the_snapshot_is_refus…<br>`MonitorSecurityTests.cs` K7_a_querithm_connection_is_refused_with_its_reason_and_n…<br>`MonitorSecurityTests.cs` K7_while_a_querithm_connection_the_server_would_not_identi…<br>`MonitorServiceTests.cs` An_identity_read_the_server_refuses_keeps_the_connection_f…<br>`MonitorScenarioTests.cs` A_login_whose_identity_read_is_refused_still_queries_and_k… (both engines) | passed | **verified** | — |
| K8 | Connection registered after the snapshot that showed it (it looked foreign in the dialog) | U, S | `MonitorTests.cs` K7_K8_a_connection_registered_after_the_snapshot_is_refus…<br>`MonitorSecurityTests.cs` K8_a_connection_registered_after_the_snapshot_is_refused_… | passed | **verified** | — |
| K9 | A new pool connection killed between server login and its identity read | U | `MonitorServiceTests.cs` K9_a_pool_connection_killed_in_its_registration_window_is…<br>`MonitorServiceTests.cs` A_refused_identity_read_still_fails_the_open_when_the_con…<br>`MonitorCoverageTests.cs` An_identity_read_the_server_refuses_is_reported_as_unavail… | passed | **verified** | — |
| K10 | SQL Server `CancelRequest` | U, S | `MonitorIntegrationTests.cs` K10_cancel_request_is_refused_with_the_engine_reason_and_…<br>`MonitorTests.cs` K10_K26_K27_cancel_rules_engine_reason_statement_identity…<br>`MonitorSecurityTests.cs` K10_a_cancel_the_engine_cannot_do_is_refused_by_the_guard… | passed | **verified** | — |
| K11 | SQL Server `is_user_process = 0` (incl. a system session with spid > 50) / PG `backend_type` ≠ client backend | S, I | `MonitorMatrixTests.cs` S10_K11_background_and_system_sessions_are_marked_and_nev…<br>`MonitorScenarioTests.cs` K11_K17_system_sessions_and_logins_without_alter_any_conn…<br>`MonitorTests.cs` K6_K11_K14_K21_K29_own_monitor_system_sessions_pseudo_blo…<br>`MonitorSecurityTests.cs` K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_s… | passed | **verified** | — |
| K12 | `Id` overflow / negative / zero / non-numeric in JSON; `Instance` or `ExpectedStatementStart` empty, > 64 chars or unparseable by the provider; PG `CancelRequest` without `ExpectedStatementStart` | S | `MonitorTests.cs` K12_shapes_are_rejected_at_the_rpc_boundary<br>`MonitorSecurityTests.cs` K12_L14_M12_malformed_input_is_rejected_at_the_rpc_boundary | passed | **verified** | — |
| K13 | `Instance` round trip: snapshot → JSON → UI → `monitor.kill` → typed parameter matches the server value exactly (SQL Server `datetime` ⅓-ms ticks, PG microseconds) | U (per engine), I | `MonitorIntegrationTests.cs` K13_K28_terminating_the_foreign_blocker_ends_its_connecti…<br>`MonitorTests.cs` K13_instance_round_trips_exactly_per_engine | passed | **verified** | — |
| K14 | Kill against a negative-blocker synthetic node (-2/-3/-4) | S | `MonitorTests.cs` K6_K11_K14_K21_K29_own_monitor_system_sessions_pseudo_blo…<br>`MonitorSecurityTests.cs` K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_s… | passed | **verified** | — |
| K15 | Azure SQL DB without `KILL DATABASE CONNECTION` | U | `MonitorTests.cs` X3_X4_X5_cut_off_and_dtd_xml_and_oversized_events<br>`MonitorTests.cs` D16_K15_azure_sql_database_scopes_panes_and_names_its_own… | passed | **verified** | Azure SQL Database exists only as fake probe results, per the plan |
| K16 | `MonitorId` unknown or belongs to a stopped monitor | S | `MonitorServiceTests.cs` L2_C14_stop_while_a_tick_runs_cancels_it_closes_the_conne…<br>`MonitorSecurityTests.cs` K1_K2_K3_K4_K6_K11_K14_K16_refusals_send_nothing_to_the_s… | passed | **verified** | — |
| K17 | Missing `ALTER ANY CONNECTION` / `pg_signal_backend` | I | `MonitorMatrixTests.cs` K17_a_kill_the_server_refuses_after_the_probe_is_reported…<br>`MonitorMatrixTests.cs` K17_a_role_without_pg_signal_backend_is_told_what_it_need…<br>`MonitorScenarioTests.cs` K11_K17_system_sessions_and_logins_without_alter_any_conn… | passed | **verified** | — |
| K18 | PG non-superuser kills a superuser backend (incl. a managed platform's own backends, faked) | I (PG), U | `MonitorScenarioTests.cs` K18_a_role_with_pg_signal_backend_cannot_terminate_a_supe…<br>`MonitorCoverageTests.cs` K18_a_postgresql_permission_refusal_is_mapped_to_a_clear_… | passed | **verified** | the U part fakes the server's 42501; a managed platform's own backends are not available here |
| K19 | Terminate a session with a long rollback | I (SQL Server) | `MonitorIntegrationTests.cs` P2_P3_K19_terminating_a_long_write_transaction_needs_the_…<br>`MonitorTests.cs` K19_a_session_rolling_back_is_refused | passed | **verified** | — |
| K20 | Double click / two kill requests for same target | U | `MonitorServiceTests.cs` K20_K21_two_kills_of_one_target_send_one_command_and_a_di… | passed | **verified** | — |
| K21 | Kill while monitor connection is down | U | `MonitorServiceTests.cs` K20_K21_two_kills_of_one_target_send_one_command_and_a_di…<br>`MonitorTests.cs` K6_K11_K14_K21_K29_own_monitor_system_sessions_pseudo_blo… | passed | **verified** | — |
| K22 | Kill SQL text built only from validated int; no string from client reaches `KILL` | S | `MonitorTests.cs` K22_the_kill_batch_formats_only_a_validated_integer<br>`MonitorSecurityTests.cs` K22_the_kill_batch_takes_the_spid_only_as_a_validated_int… | passed | **verified** | — |
| K23 | Kill target in a different database than `DatabaseFilter` | U | `MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>`MonitorServiceTests.cs` K23_the_database_filter_is_not_a_security_boundary_only_s… | passed | **verified** | — |
| K24 | Audit: every kill attempt (allowed or refused) logged without query text, login or host | S | `MonitorServiceTests.cs` K24_every_kill_attempt_is_logged_without_query_text_login…<br>`MonitorSecurityTests.cs` K24_every_kill_attempt_allowed_or_refused_is_audited_with… | passed | **verified** | — |
| K25 | PG cancel of a foreign session's running query | I (PG) | `MonitorIntegrationTests.cs` K25_K26_cancelling_a_foreign_running_query_keeps_its_sess… | passed | **verified** | — |
| K26 | PG cancel after the dialog's statement finished and a new one started on the same backend | U, I (PG) | `MonitorIntegrationTests.cs` K25_K26_cancelling_a_foreign_running_query_keeps_its_sess…<br>`MonitorTests.cs` K10_K26_K27_cancel_rules_engine_reason_statement_identity… | passed | **verified** | — |
| K27 | `CancelRequest` on an idle / idle-in-transaction backend | U | `MonitorTests.cs` K10_K26_K27_cancel_rules_engine_reason_statement_identity… | passed | **verified** | — |
| K28 | Terminate a foreign session A holding a lock that blocks B | I | `MonitorIntegrationTests.cs` K13_K28_terminating_the_foreign_blocker_ends_its_connecti… | passed | **verified** | — |
| K29 | PG login without `pg_read_all_stats` (with or without `pg_signal_backend`): another role's backend | U, I (PG) | `MonitorIntegrationTests.cs` S18_K29_a_login_without_server_state_sees_only_its_own_se…<br>`MonitorMatrixTests.cs` S14_revoking_the_monitor_logins_server_state_permission_d…<br>`MonitorTests.cs` K6_K11_K14_K21_K29_own_monitor_system_sessions_pseudo_blo… | passed | **verified** | — |
| S1 | 0 other sessions | U | `MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>`MonitorServiceTests.cs` S1_no_other_sessions_is_an_empty_list_without_error | passed | **verified** | — |
| S2 | > 1,000 sessions | U, I | `MonitorMatrixTests.cs` S2_more_than_1000_sessions_show_the_first_1000_by_priorit…<br>`MonitorCoverageTests.cs` S2_more_than_1000_sessions_keep_the_first_1000_in_server_… | passed | **blocked** | missing: I PostgreSQL. PostgreSQL I part blocked: more than 1,000 backends are needed; the fixture has max_connections = 600 (the plan's fixture spec) and that many backends need several GB inside Docker Desktop. SQL Server I part passed (1,000 extra connections) |
| S3 | NULL login/host/program/database/text | U | `model.test.ts` snapshot model › S3, S4: nulls show as a dash, control ch… | passed | **verified** | — |
| S4 | Non-ASCII, emoji, RTL, control chars in text/program name | U | `model.test.ts` snapshot model › S4: text with nothing to escape comes ba…<br>`model.test.ts` snapshot model › S3, S4: nulls show as a dash, control ch… | passed | **verified** | — |
| S5 | Text > 4 KB | U | `MonitorTests.cs` S5_a_text_over_4KB_is_cut_on_a_character_boundary_without… | passed | **verified** | — |
| S6 | Statement offsets invalid / text NULL (SQL Server, encrypted modules) | U, I (SQL Server) | `MonitorScenarioTests.cs` S6_an_encrypted_module_shows_text_unavailable<br>`MonitorCoverageTests.cs` S6_encrypted_module_text_is_marked_unavailable_not_shown_… | passed | **verified** | — |
| S7 | Parallel query rows (multiple tasks) | I (SQL Server) | `MonitorMatrixTests.cs` S7_B5_a_parallel_query_is_one_row_with_its_longest_task_w… | passed | **verified** | — |
| S8 | Session starts or ends mid-query | U | `MonitorCoverageTests.cs` S8_a_session_with_two_request_rows_is_one_row_never_dupli… | passed | **verified** | — |
| S9 | Idle in transaction (PG) / sleeping with open tx (SQL Server) | I | `MonitorScenarioTests.cs` B1_B2_B8_S9_a_three_level_chain_has_the_idle_holder_as_it… | passed | **verified** | — |
| S10 | Background workers, autovacuum, replication senders (PG); system spids (SQL Server) | I | `MonitorMatrixTests.cs` S10_K11_background_and_system_sessions_are_marked_and_nev…<br>`model.test.ts` snapshot model › S10: system and background sessions are … | passed | **verified** | — |
| S11 | Timezone / clock skew between client and server | U, I | `MonitorCoverageTests.cs` S11_S12_durations_come_from_the_server_clock_as_long_mill… | passed | **blocked** | missing: I SQL Server, I PostgreSQL. I part blocked: the fixture's servers share the host clock, so client/server skew cannot be produced; durations are server-side expressions (asserted in the U test) |
| S12 | Durations > 24 days (int overflow) | U | `MonitorCoverageTests.cs` S11_S12_durations_come_from_the_server_clock_as_long_mill… | passed | **verified** | — |
| S13 | Server-returned names with quotes/brackets/newlines | S | `MonitorSecurityTests.cs` S13_server_returned_names_are_quoted_as_identifiers_never… | passed | **verified** | — |
| S14 | Permission downgrade during monitoring (role revoked) | I | `MonitorMatrixTests.cs` S14_revoking_the_monitor_logins_server_state_permission_d… | passed | **verified** | — |
| S15 | Two PG sessions run the same statement with different literals (same `query_id`) | U, I (PG) | `MonitorScenarioTests.cs` S15_two_backends_with_the_same_query_id_and_different_lit…<br>`MonitorTests.cs` S15_S16_the_text_id_is_the_content_hash_so_changed_text_g… | passed | **verified** | — |
| S16 | SQL Server `ALTER PROCEDURE` that changes a literal but keeps the statement offsets (same `sql_handle`, same offsets), then run again | U, I (SQL Server) | `MonitorScenarioTests.cs` S16_altering_a_procedure_that_keeps_its_offsets_gets_a_ne…<br>`MonitorTests.cs` S15_S16_the_text_id_is_the_content_hash_so_changed_text_g… | passed | **verified** | — |
| S17 | Unchanged text across ticks | U | `MonitorTests.cs` M5_S17_texts_are_sent_once_and_an_unchanged_session_text_… | passed | **verified** | — |
| S18 | Login without `VIEW SERVER STATE` / `pg_read_all_stats` | I | `MonitorIntegrationTests.cs` S18_K29_a_login_without_server_state_sees_only_its_own_se…<br>`MonitorCoverageTests.cs` S18_without_server_state_the_sessions_query_reads_no_deni… | passed | **verified** | — |
| B1 | Simple A → B | U, I | `MonitorIntegrationTests.cs` B1_LK4_M5_sessions_a_blocking_pair_its_locks_and_its_text…<br>`MonitorScenarioTests.cs` B1_B2_B8_S9_a_three_level_chain_has_the_idle_holder_as_it…<br>`MonitorTests.cs` B1_B2_B3_chains_and_fan_out | passed | **verified** | — |
| B2 | Chain A → B → C | U, I | `MonitorScenarioTests.cs` B1_B2_B8_S9_a_three_level_chain_has_the_idle_holder_as_it…<br>`MonitorTests.cs` B1_B2_B3_chains_and_fan_out | passed | **verified** | — |
| B3 | One head, many victims | U | `MonitorTests.cs` B1_B2_B3_chains_and_fan_out | passed | **verified** | — |
| B4 | PG multi-blocker (B waits on A and C) | U, I (PG) | `MonitorScenarioTests.cs` B4_a_backend_blocked_by_two_others_lists_both<br>`MonitorTests.cs` B4_multi_blocker_is_counted_once<br>`model.test.ts` blocking tree › B4, B6, B11: multi-blocker under both, cy… | passed | **verified** | — |
| B5 | Self-edge (parallel exchange) | U, I (SQL Server) | `MonitorMatrixTests.cs` S7_B5_a_parallel_query_is_one_row_with_its_longest_task_w…<br>`MonitorCoverageTests.cs` B5_B12_a_self_edge_is_ignored_and_a_parallel_request_bloc… | passed | **verified** | — |
| B6 | Cycle A → B → A (deadlock before detector fires) | U | `MonitorTests.cs` B6_a_cycle_is_marked_as_deadlock_in_progress_without_recu…<br>`model.test.ts` blocking tree › B4, B6, B11: multi-blocker under both, cy… | passed | **verified** | — |
| B7 | Blocker not in snapshot (truncated, permission hidden) | U | `MonitorTests.cs` B7_B11_hidden_blockers_and_negative_pseudo_blockers_becom… | passed | **verified** | — |
| B8 | Head blocker is idle with open tx | I | `MonitorScenarioTests.cs` B1_B2_B8_S9_a_three_level_chain_has_the_idle_holder_as_it… | passed | **verified** | — |
| B9 | Deadlock detector kills a victim between ticks | I | `MonitorMatrixTests.cs` B9_a_deadlock_resolved_between_ticks_leaves_no_stale_node… | passed | **verified** | — |
| B10 | 1,000-node tree | U, bench | `MonitorTests.cs` B10_a_1000_node_tree_builds_within_5ms | passed | **verified** | bench: the 5 ms bound is timed in the unit test; there is no separate harness metric |
| B11 | SQL Server `blocking_session_id` -2 / -3 / -4 | U | `MonitorTests.cs` B7_B11_hidden_blockers_and_negative_pseudo_blockers_becom…<br>`model.test.ts` blocking tree › B4, B6, B11: multi-blocker under both, cy… | passed | **verified** | — |
| B12 | SQL Server parallel request whose tasks wait on two different sessions | U, I (SQL Server) | `MonitorMatrixTests.cs` B12_a_parallel_scan_blocked_by_two_sessions_lists_both_bl…<br>`MonitorCoverageTests.cs` B5_B12_a_self_edge_is_ignored_and_a_parallel_request_bloc… | passed | **verified** | — |
| LK1 | PG, two databases where a lock in `db2` has a relation OID that also exists as a different relation in the connected `db1` | U, I (PG) | `MonitorScenarioTests.cs` LK1_LK2_locks_in_another_database_are_never_named_after_a…<br>`MonitorCoverageTests.cs` LK1_LK2_LK3_postgresql_locks_in_other_databases_keep_thei… | passed | **verified** | — |
| LK2 | PG lock on a shared catalog (`l.database = 0`, e.g. `pg_database`) | U, I (PG) | `MonitorScenarioTests.cs` LK1_LK2_locks_in_another_database_are_never_named_after_a…<br>`MonitorCoverageTests.cs` LK1_LK2_LK3_postgresql_locks_in_other_databases_keep_thei… | passed | **verified** | — |
| LK3 | PG lock in another database with no metadata cache for it | U | `MonitorScenarioTests.cs` LK1_LK2_locks_in_another_database_are_never_named_after_a…<br>`MonitorCoverageTests.cs` LK1_LK2_LK3_postgresql_locks_in_other_databases_keep_thei… | passed | **verified** | — |
| LK4 | Blocking pair on the test table | I | `MonitorIntegrationTests.cs` B1_LK4_M5_sessions_a_blocking_pair_its_locks_and_its_text… | passed | **verified** | — |
| D1 | First tick | U | `MonitorTests.cs` D1_to_D5_first_sample_collects_resets_and_wraps_suppress_… | passed | **verified** | — |
| D2 | Counter reset (server restart, `DBCC SQLPERF(...CLEAR)`, `pg_stat_reset`, `pg_wait_sampling_reset_profile`) | U, I | `MonitorMatrixTests.cs` D2_clearing_postgresql_statistics_and_the_wait_profile_is…<br>`MonitorScenarioTests.cs` D2_clearing_wait_statistics_is_reported_as_a_reset_not_ne…<br>`MonitorTests.cs` D1_to_D5_first_sample_collects_resets_and_wraps_suppress_…<br>`MonitorTests.cs` D2_a_read_that_lost_nearly_every_counter_was_cleared_and_… | passed | **verified** | — |
| D3 | Counter wraps / bigint near max | U | `MonitorTests.cs` D1_to_D5_first_sample_collects_resets_and_wraps_suppress_… | passed | **verified** | — |
| D4 | Tick skipped (slow) | U | `MonitorTests.cs` D1_to_D5_first_sample_collects_resets_and_wraps_suppress_… | passed | **verified** | — |
| D5 | New wait type appears mid-session | U | `MonitorTests.cs` D1_to_D5_first_sample_collects_resets_and_wraps_suppress_… | passed | **verified** | — |
| D6 | Benign wait list | U | `MonitorTests.cs` D6_the_benign_wait_list_is_fixed_and_benign_waits_are_nev… | passed | **verified** | — |
| D7 | PG with `pg_wait_sampling` | I (PG, fixture image with extension) | `MonitorMatrixTests.cs` D7_pg_wait_sampling_lock_wait_deltas_match_a_generated_lo… | passed | **verified** | — |
| D8 | PG without extension, lock wait held for 3 intervals | I (PG) | `MonitorScenarioTests.cs` D8_D13_T6_postgresql_17_uses_the_checkpointer_view_rename… | passed | **verified** | — |
| D9 | PG tier 2 issues no query of its own | U | `MonitorTests.cs` D9_D10_D11_tick_sampled_waits_weight_by_real_elapsed_time… | passed | **verified** | — |
| D10 | PG tier 2 with ticks late by 3 intervals (fake clock) | U | `MonitorTests.cs` D9_D10_D11_tick_sampled_waits_weight_by_real_elapsed_time… | passed | **verified** | — |
| D11 | PG tier 2 with active rows truncated by the sessions cap | U | `MonitorTests.cs` D9_D10_D11_tick_sampled_waits_weight_by_real_elapsed_time… | passed | **verified** | — |
| D12 | CPU ring buffer empty (fresh SQL Server) | U | `MonitorTests.cs` D12_D17_cpu_from_the_ring_buffer_is_read_defensively | passed | **verified** | — |
| D13 | `pg_stat_checkpointer` (17+) vs `pg_stat_bgwriter` (< 17), `pg_stat_io` (16+) | U, I | `MonitorScenarioTests.cs` D8_D13_T6_postgresql_17_uses_the_checkpointer_view_rename…<br>`MonitorCoverageTests.cs` D13_postgresql_reads_checkpoints_from_the_view_its_catalo… | passed | **verified** | — |
| D14 | PG I/O rows | U, I (PG) | `MonitorIntegrationTests.cs` D14_D15_D20_waits_metrics_and_io_report_values_with_their…<br>`MonitorScenarioTests.cs` D8_D13_T6_postgresql_17_uses_the_checkpointer_view_rename…<br>`MonitorCoverageTests.cs` D14_postgresql_io_is_cluster_scoped_bytes_follow_the_prob… | passed | **verified** | — |
| D15 | SQL Server I/O rows | U, I (SQL Server) | `MonitorIntegrationTests.cs` D14_D15_D20_waits_metrics_and_io_report_values_with_their…<br>`MonitorCoverageTests.cs` D15_sql_server_io_rows_are_per_file_with_database_and_fil… | passed | **verified** | — |
| D16 | Azure SQL server-wide DMVs | U | `MonitorTests.cs` X3_X4_X5_cut_off_and_dtd_xml_and_oversized_events<br>`MonitorTests.cs` D16_K15_azure_sql_database_scopes_panes_and_names_its_own… | passed | **verified** | Azure SQL Database exists only as fake probe results, per the plan |
| D17 | Ring buffer XML in an unexpected shape | U | `MonitorTests.cs` Metrics_rates_use_elapsed_time_and_a_missing_value_is_nev…<br>`MonitorTests.cs` D12_D17_cpu_from_the_ring_buffer_is_read_defensively | passed | **verified** | — |
| D18 | Managed PG / managed SQL Server probe results (no superuser, signal role from the platform's admin role, extension missing, unreadable XE file) | U | `MonitorTests.cs` X3_X4_X5_cut_off_and_dtd_xml_and_oversized_events<br>`MonitorTests.cs` D18_managed_servers_degrade_from_probed_capabilities_not_… | passed | **verified** | managed platforms (RDS, Cloud SQL, Azure) exist only as fake probe results, per the plan |
| D19 | PG `compute_query_id = off` | U, I (PG) | `MonitorScenarioTests.cs` T4_D19_on_a_server_without_preloaded_extensions_top_queri…<br>`MonitorTests.cs` D18_managed_servers_degrade_from_probed_capabilities_not_…<br>`MonitorCoverageTests.cs` D19_without_query_ids_postgresql_rows_have_no_query_hash_… | passed | **verified** | — |
| D20 | Generated load / a blocking pair | I | `MonitorIntegrationTests.cs` D14_D15_D20_waits_metrics_and_io_report_values_with_their… | passed | **verified** | — |
| T1 | Plan cache flushed / `pg_stat_statements_reset()` | U, I | `MonitorMatrixTests.cs` T1_clearing_the_statement_statistics_is_reported_as_a_res…<br>`MonitorCoverageTests.cs` T1_a_plan_cache_flush_seen_on_an_incremental_read_trigger…<br>`MonitorTests.cs` T1_T2_T5_T7_deltas_per_entry_grouped_by_hash_evictions_dr… | passed | **verified** | — |
| T2 | Query evicted from cache between ticks | U | `MonitorCoverageTests.cs` T1_a_plan_cache_flush_seen_on_an_incremental_read_trigger…<br>`MonitorTests.cs` T1_T2_T5_T7_deltas_per_entry_grouped_by_hash_evictions_dr… | passed | **verified** | — |
| T3 | `pg_stat_statements` 1.8+ vs older column names | U | `MonitorCoverageTests.cs` T3_T6_pg_stat_statements_columns_are_mapped_from_the_prob… | passed | **verified** | — |
| T4 | Extension missing / not preloaded | I (PG) | `MonitorScenarioTests.cs` T4_D19_on_a_server_without_preloaded_extensions_top_queri… | passed | **verified** | — |
| T5 | Same hash, different text (literals) | U | `MonitorTests.cs` T1_T2_T5_T7_deltas_per_entry_grouped_by_hash_evictions_dr… | passed | **verified** | — |
| T6 | PG 17 column names (`shared_blk_read_time`) | U, I (PG 17) | `MonitorScenarioTests.cs` D8_D13_T6_postgresql_17_uses_the_checkpointer_view_rename…<br>`MonitorCoverageTests.cs` T3_T6_pg_stat_statements_columns_are_mapped_from_the_prob… | passed | **verified** | — |
| T7 | SQL Server incremental read: only entries with `last_execution_time >=` previous cutoff fetched; full read every 30th run catches evictions | U | `MonitorTests.cs` T1_T2_T5_T7_deltas_per_entry_grouped_by_hash_evictions_dr… | passed | **verified** | — |
| T8 | PG uses `showtext := false`; text fetched once per (`queryid`, `dbid`, `userid`); `TextId` is the text's content hash | U | `MonitorCoverageTests.cs` T8_postgresql_polls_without_text_and_fetches_each_text_on… | passed | **verified** | — |
| T9 | Cross-poll completion: execution starts before run N, completes between run N and N+1 | U (fake rows), I (SQL Server, `WAITFOR` query spanning a poll) | `MonitorMatrixTests.cs` T9_a_statement_running_across_a_top_queries_poll_appears_…<br>`MonitorTests.cs` T9_a_query_that_completes_between_polls_appears_in_the_ne… | passed | **verified** | — |
| T10 | Cutoff with a request running for 10 min | U, bench | `MonitorBench.cs` M31_top_queries_refresh_with_a_full_plan_cache<br>`MonitorCoverageTests.cs` T10_the_incremental_cutoff_is_the_earliest_running_reques… | passed | **verified** | — |
| T11 | Run a query N times | I | `MonitorIntegrationTests.cs` T11_top_queries_show_a_query_run_n_times_with_an_executio… | passed | **verified** | — |
| M1 | Session settings applied on open and after reconnect (SQL Server isolation / `LOCK_TIMEOUT` / `DEADLOCK_PRIORITY`; PG timeouts / `application_name`) | I | `MonitorIntegrationTests.cs` M1_M2_L4_monitor_settings_are_applied_and_the_connection_… | passed | **verified** | — |
| M2 | PG monitor never holds a transaction | I (PG) | `MonitorIntegrationTests.cs` M1_M2_L4_monitor_settings_are_applied_and_the_connection_… | passed | **verified** | — |
| M3 | Monitor against a table under a schema-modification lock (SQL Server) | I (SQL Server) | `MonitorScenarioTests.cs` M3_the_lock_pane_returns_under_a_schema_modification_lock… | passed | **verified** | — |
| M4 | Job with raised `statement_timeout` (PG) | S | `MonitorSecurityTests.cs` M4_no_monitor_job_changes_statement_timeout_so_none_has_t… | passed | **verified** | — |
| M5 | Every tick | U | `MonitorIntegrationTests.cs` B1_LK4_M5_sessions_a_blocking_pair_its_locks_and_its_text…<br>`MonitorTests.cs` M5_S17_texts_are_sent_once_and_an_unchanged_session_text_… | passed | **verified** | — |
| M6 | Lost or out-of-order notification | U, Vitest | `model.test.ts` snapshot model › M6: a snapshot with an older or repeated…<br>`model.test.ts` snapshot model › M6: a text whose notification was lost i… | passed | **verified** | — |
| M7 | UI LRU eviction | U, Vitest | `model.test.ts` text cache › M7: a text evicted from the UI cache is fetc… | passed | **verified** | — |
| M8 | Sustained text churn: 500 sessions with new unique literals every tick, 1 h on a fake clock | U, Vitest | `MonitorTests.cs` M8_sustained_text_churn_keeps_the_store_at_live_plus_4MB<br>`model.test.ts` text cache › M8: sustained churn keeps the cache within 5… | passed | **verified** | — |
| M9 | Refetch after sidecar eviction (ended session's text evicted) | U, Vitest | `MonitorTests.cs` M9_M10_M11_live_texts_are_never_evicted_evicted_ones_are_…<br>`model.test.ts` text cache › M9: ids the sidecar no longer has are shown … | passed | **verified** | — |
| M10 | Live row whose text the UI evicted | U | `MonitorTests.cs` M9_M10_M11_live_texts_are_never_evicted_evicted_ones_are_… | passed | **verified** | — |
| M11 | Evicted text reappears in a new session | U | `MonitorTests.cs` M9_M10_M11_live_texts_are_never_evicted_evicted_ones_are_… | passed | **verified** | — |
| M12 | `monitor.texts` with > 500 ids or a malformed id | S | `MonitorServiceTests.cs` M12_monitor_texts_rejects_too_many_or_malformed_ids<br>`MonitorSecurityTests.cs` K12_L14_M12_malformed_input_is_rejected_at_the_rpc_boundary | passed | **verified** | — |
| M13 | Payload budget: 500 sessions ≤ 512 KB per tick without new texts, first snapshot ≤ 2.5 MB | bench | `MonitorBench.cs` M28_M32_snapshot_of_500_sessions_payload_lag_and_cpu | passed | **verified** | — |
| M14 | Connection count per profile never exceeds the stated budget (monitor + kill control + shared banner check) | I | `MonitorServiceTests.cs` M14_the_header_reports_one_connection_and_two_while_a_kil…<br>`MonitorScenarioTests.cs` M14_a_kill_on_the_control_connection_and_banner_checks_k… (both engines; control path and timer-driven banner checks forced, counted on the server) | passed | **verified** | — |
| L1 | Start twice with same profile | U | `MonitorServiceTests.cs` L1_L12_L14_two_monitors_on_one_profile_are_independent_th… | passed | **verified** | — |
| L2 | Stop while a tick is running | U | `MonitorServiceTests.cs` L2_C14_stop_while_a_tick_runs_cancels_it_closes_the_conne… | passed | **verified** | — |
| L3 | Tab closed / profile disconnected | U, E | `MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>`MonitorServiceTests.cs` L3_disconnecting_the_profile_stops_its_monitors_and_close…<br>`ActivityTab.test.tsx` ActivityTab lifecycle (L3) › closing the tab stops its mo… | passed | **verified** | E: smoke closes the Activity tab and disconnects; 0 `Querithm Monitor` sessions left on the server after each |
| L4 | Monitor connection killed externally | I | `MonitorIntegrationTests.cs` M1_M2_L4_monitor_settings_are_applied_and_the_connection_…<br>`MonitorServiceTests.cs` L4_a_lost_connection_reconnects_with_back_off_and_probes_… | passed | **verified** | — |
| L5 | Server restart | I | `MonitorMatrixTests.cs` L5_a_sql_server_restart_reconnects_and_reports_the_counte…<br>`MonitorScenarioTests.cs` L5_a_server_restart_reconnects_and_reports_the_counter_reset | passed | **verified** | — |
| L6 | SSH tunnel drop | I | `MonitorMatrixTests.cs` L6_through_an_ssh_tunnel_the_sql_server_monitor_reconnect…<br>`MonitorScenarioTests.cs` L6_B0_3_through_an_ssh_tunnel_the_monitor_works_reconnect… | passed | **verified** | — |
| L7 | Query timeout on a DMV (server overloaded) | U | `MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>`MonitorServiceTests.cs` L7_a_dmv_query_that_never_returns_is_cut_at_its_time_limi… | passed | **verified** | — |
| L8 | Sidecar crash/restart | E | — | passed (see notes) | **verified** | E: smoke kills the sidecar under a PostgreSQL monitor; "The sidecar restarted…" shown, 0 orphan monitor connections after 5 s. The dev bridge does not restart the sidecar (the Rust shell does), so the restart itself is not exercised |
| L9 | Sidecar shutdown with N monitors | U | `MonitorServiceTests.cs` L9_sidecar_shutdown_disposes_every_monitor_within_two_sec… | passed | **verified** | — |
| L10 | Interval / pane changes while running | U | `MonitorSchedulerTests.cs` L10_an_interval_change_applies_from_the_next_tick_without… | passed | **verified** | — |
| L11 | Pause | U | `MonitorSchedulerTests.cs` L11_paused_no_job_runs_and_resume_ticks_at_once<br>`MonitorServiceTests.cs` L11_paused_monitor_sends_no_queries | passed | **verified** | — |
| L12 | 50 monitors started (abuse) | S | `MonitorServiceTests.cs` L1_L12_L14_two_monitors_on_one_profile_are_independent_th…<br>`MonitorSecurityTests.cs` L12_more_than_eight_monitors_are_refused | passed | **verified** | — |
| L13 | Leak check: start/stop 1,000 times | U | `MonitorServiceTests.cs` L13_starting_and_stopping_a_thousand_times_leaks_no_conne… | passed | **verified** | — |
| L14 | Interval outside 1–30 s, unknown pane bits, reserved B8 bits | S | `MonitorServiceTests.cs` L1_L12_L14_two_monitors_on_one_profile_are_independent_th…<br>`MonitorSecurityTests.cs` K12_L14_M12_malformed_input_is_rejected_at_the_rpc_boundary | passed | **verified** | — |
| L15 | Faulted fake provider on one pane | U | `MonitorServiceTests.cs` L15_a_faulted_pane_errors_with_back_off_while_the_other_p… | passed | **verified** | — |
| H1 | Buffer full by time | U | `MonitorTests.cs` H1_H3_H4_diffs_replay_to_the_original_oldest_go_first_and… | passed | **verified** | — |
| H2 | Buffer full by 50 MB cap | U | `MonitorTests.cs` H2_the_memory_cap_evicts_oldest_and_says_so | passed | **verified** | — |
| H3 | Diff replay | U | `MonitorIntegrationTests.cs` H3_on_a_real_server_history_keeps_the_ticks_of_a_blocking…<br>`MonitorTests.cs` H1_H3_H4_diffs_replay_to_the_original_oldest_go_first_and… | passed | **verified** | — |
| H4 | Scrub during reconnect gap | U | `MonitorTests.cs` H1_H3_H4_diffs_replay_to_the_original_oldest_go_first_and… | passed | **verified** | — |
| H5 | Export without "include query text" | S | `MonitorServiceTests.cs` H5_H6_H7_exports_hold_no_text_login_or_host_unless_asked_…<br>`MonitorSecurityTests.cs` H5_H6_an_export_has_no_text_login_or_host_unless_asked_an… | passed | **verified** | — |
| H6 | Export path traversal (`../`) | S | `MonitorServiceTests.cs` H5_H6_H7_exports_hold_no_text_login_or_host_unless_asked_…<br>`MonitorSecurityTests.cs` H5_H6_an_export_has_no_text_login_or_host_unless_asked_an… | passed | **verified** | — |
| H7 | Disk full during export | U | `MonitorServiceTests.cs` H5_H6_H7_exports_hold_no_text_login_or_host_unless_asked_… | passed | **verified** | — |
| H8 | Texts referenced only by retained history | U | `MonitorTests.cs` H8_texts_named_only_by_history_are_kept_counted_and_freed… | passed | **verified** | — |
| U1 | Snapshot update | Vitest | `ActivityTab.test.tsx` ActivityTab › U1: snapshots never re-render the tab; the …<br>`model.test.ts` snapshot model › U1: only cells whose display changed are…<br>`model.test.ts` snapshot model › U1: a tick formats only its own rows; th… | passed | **verified** | — |
| U2 | Selection kept across ticks by `SessionKey`, cleared if session ends | Vitest | `ActivityTab.test.tsx` SidePanel (U2) › says the session has ended once its exac…<br>`model.test.ts` snapshot model › U2: the selection follows the session ke… | passed | **verified** | — |
| U3 | Kill dialog shows login/host/program/text/tx age; second confirm required; Prod needs typed confirmation | Vitest, E | `ActivityTab.test.tsx` KillDialog › U3, P9: shows who and what, needs the second… | passed | **verified** | E: smoke kill dialog shows high risk (135 MB of log) and requires both confirms |
| U4 | Kill button disabled with tooltip reason for each degradation | Vitest | `model.test.ts` kill availability (U4) › each degradation disables with i… | passed | **verified** | — |
| U5 | Sort/filter stable across ticks | Vitest | `model.test.ts` snapshot model › U5: sorting is stable across ticks (ties… | passed | **verified** | — |
| U6 | Theme tokens for wait colours in light and dark | Vitest | `model.test.ts` snapshot model › U6: wait colours are theme tokens define… | passed | **verified** | — |
| U7 | Keyboard: navigate tree, open detail, kill (with confirm) | Vitest | `ActivityTab.test.tsx` BlockingTree keyboard (U7) › navigates, expands, opens de… | passed | **verified** | — |
| U8 | Querithm-connection rows: badge, **Go to tab** focuses the tab, no kill/cancel | Vitest, E | `ActivityTab.test.tsx` ActivityTab › U8: a Querithm connection row offers Go to … | passed | **verified** | E: smoke own-tab row offers Go to tab and it focuses the query tab |
| U9 | Pane states: loading, empty, paused, degraded, error with countdown, truncated, disconnected | Vitest | `ActivityTab.test.tsx` ActivityTab › U9: each pane state renders its text from P… | passed | **verified** | — |
| U10 | Blocking node with 300 children | Vitest | `model.test.ts` blocking tree › U10: a node with 300 children shows 50 an… | passed | **verified** | — |
| X1 | Real deadlock created by two sessions | I (SQL Server) | `MonitorIntegrationTests.cs` X1_X14_a_real_deadlock_appears_in_the_deadlock_history_wi… | passed | **verified** | — |
| X2 | Same deadlock in ring buffer and event file | U | `MonitorTests.cs` X2_X6_deadlock_graphs_parse_with_victims_resources_and_ex… | passed | **verified** | — |
| X3 | Malformed / truncated XML (ring buffer cut-off) | U | `MonitorTests.cs` X3_X4_X5_cut_off_and_dtd_xml_and_oversized_events | passed | **verified** | — |
| X4 | XML with DTD / external entity | S | `MonitorTests.cs` X3_X4_X5_cut_off_and_dtd_xml_and_oversized_events<br>`MonitorSecurityTests.cs` X4_X5_deadlock_xml_with_a_dtd_or_over_5MB_is_refused_with… | passed | **verified** | — |
| X5 | Event > 5 MB | U, S | `MonitorTests.cs` X3_X4_X5_cut_off_and_dtd_xml_and_oversized_events<br>`MonitorSecurityTests.cs` X4_X5_deadlock_xml_with_a_dtd_or_over_5MB_is_refused_with… | passed | **verified** | — |
| X6 | Multi-victim and parallel-query deadlocks (exchange events) | U | `MonitorTests.cs` X2_X6_deadlock_graphs_parse_with_victims_resources_and_ex… | passed | **verified** | — |
| X7 | Deadlock involving objects in a DB the login can't access | I (SQL Server) | `MonitorMatrixTests.cs` X7_a_deadlock_in_a_database_the_monitor_login_cannot_open… | passed | **verified** | — |
| X8 | `system_health` stopped | U, I (SQL Server) | `MonitorScenarioTests.cs` X8_with_system_health_stopped_the_deadlock_pane_says_so<br>`MonitorCoverageTests.cs` X8_with_system_health_stopped_the_deadlock_pane_is_degrad… | passed | **verified** | — |
| X9 | Event file path unreadable | U | `MonitorCoverageTests.cs` X9_an_unreadable_event_file_falls_back_to_the_ring_buffer…<br>`MonitorCoverageTests.cs` X9_the_ring_buffer_is_read_only_when_the_event_files_can… | passed | **verified** | — |
| X10 | Azure SQL DB | U | `MonitorCoverageTests.cs` X10_azure_sql_database_has_no_server_deadlock_history_and… | passed | **verified** | Azure SQL Database exists only as fake probe results, per the plan |
| X11 | PG | I (PG) | `MonitorIntegrationTests.cs` X11_deadlock_history_degrades_with_the_reason<br>`MonitorScenarioTests.cs` X11_a_real_postgresql_deadlock_shows_in_the_deadlocks_cou… | passed | **verified** | — |
| X12 | 0 events / time window empty | U | `MonitorTests.cs` X3_X4_X5_cut_off_and_dtd_xml_and_oversized_events<br>`MonitorCoverageTests.cs` X12_no_deadlocks_or_none_in_the_time_window_is_an_empty_o… | passed | **verified** | — |
| X13 | > 200 events | U | `MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>`MonitorServiceTests.cs` X13_more_than_200_deadlocks_return_the_newest_200_and_say…<br>`ActivityTab.test.tsx` X13: more than 200 deadlocks says the newest 200 are shown… | passed | **verified** | — |
| X14 | `.xdl` export includes query text only after confirm; path inside app data | S | `MonitorIntegrationTests.cs` X1_X14_a_real_deadlock_appears_in_the_deadlock_history_wi…<br>`MonitorSecurityTests.cs` X14_the_xdl_export_needs_the_text_confirm | passed | **verified** | — |
| X15 | Monitor code paths never issue `CREATE/ALTER/DROP EVENT SESSION` (scoped to `Querithm.Core/Monitoring` and the providers' activity classes — §11 Trace legitimately creates `querithm_trace_*`) | S | `MonitorMatrixTests.cs` T1_clearing_the_statement_statistics_is_reported_as_a_res…<br>`MonitorSecurityTests.cs` X15_monitor_code_never_creates_alters_starts_or_drops_an_… | passed | **verified** | — |
| X16 | New deadlock during live monitoring | U, E | `MonitorIntegrationTests.cs` X1_X14_a_real_deadlock_appears_in_the_deadlock_history_wi…<br>`MonitorCoverageTests.cs` X16_the_poll_reads_the_event_files_once_then_only_what_ca…<br>`MonitorCoverageTests.cs` X16_a_priming_read_cut_twice_by_the_poll_limit_stops_read…<br>`MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>+1 more | passed | **verified** | E: smoke counts toasts with a DOM observer: one for the new deadlock, still one after the next 30 s poll |
| C1 | Two jobs never run concurrently on the monitor connection | U | `MonitorSchedulerTests.cs` C7_default_intervals_every_secondary_job_runs_within_its_… | passed | **verified** | — |
| C2 | Kill while a long job runs (deadlock read, top queries) | U, I | `MonitorMatrixTests.cs` C2_C3_a_kill_while_a_job_holds_the_monitor_connection_and…<br>`MonitorSchedulerTests.cs` C2_C4_a_kill_claims_the_idle_connection_or_preempts_the_r…<br>`MonitorServiceTests.cs` C2_C3_a_kill_during_a_long_job_uses_a_control_connection_… | passed | **verified** | — |
| C3 | Kill a foreign blocker while it blocks 8 tab connections (pool exhausted) plus a queued waiter | U, I | `MonitorMatrixTests.cs` C2_C3_a_kill_while_a_job_holds_the_monitor_connection_and…<br>`MonitorServiceTests.cs` C2_C3_a_kill_during_a_long_job_uses_a_control_connection_… | passed | **verified** | — |
| C4 | Control connection can't be opened (fake connect failure / server connection limit) | U | `MonitorSchedulerTests.cs` C2_C4_a_kill_claims_the_idle_connection_or_preempts_the_r…<br>`MonitorServiceTests.cs` C4_when_no_control_connection_opens_the_running_job_is_pr… | passed | **verified** | — |
| C5 | Two kills on the same profile at once | U | `MonitorServiceTests.cs` C5_two_kills_on_one_profile_are_serialised_and_the_second… | passed | **verified** | — |
| C6 | Secondary jobs due during a main tick | U | `MonitorSchedulerTests.cs` C6_secondary_jobs_due_during_a_tick_run_one_at_a_time_aft… | passed | **verified** | — |
| C7 | Default intervals, every Phase B pane enabled, idle server, typical durations (tick 100 ms, locks 200 ms, top queries 300 ms / full 1.5 s, deadlocks 2 s), fake clock, 10 min | U | `MonitorSchedulerTests.cs` C7_default_intervals_every_secondary_job_runs_within_its_… | passed | **verified** | — |
| C8 | Slow server: every job 3× those durations, tick longer than the interval | U | `MonitorSchedulerTests.cs` C8_slow_server_secondary_jobs_still_run_once_per_two_peri… | passed | **verified** | — |
| C9 | Interactive request while a job runs | U | `MonitorSchedulerTests.cs` C9_an_interactive_request_starts_as_soon_as_the_running_j… | passed | **verified** | — |
| C10 | Interactive job still not started after 5 s queue time (fake long job running) | U | `MonitorSchedulerTests.cs` C10_an_interactive_request_not_started_within_five_second… | passed | **verified** | — |
| C11 | Second detail request (other row) while the first is pending | U | `MonitorSchedulerTests.cs` C11_a_second_request_of_the_same_kind_supersedes_the_pend… | passed | **verified** | — |
| C12 | Job timeout | U | `MonitorSchedulerTests.cs` C12_a_job_timeout_cancels_only_that_job_and_the_next_tick… | passed | **verified** | — |
| C13 | Budget > 50 % for 10 s | U | `MonitorSchedulerTests.cs` C8_slow_server_secondary_jobs_still_run_once_per_two_peri…<br>`MonitorSchedulerTests.cs` C13_load_over_half_of_wall_time_throttles_the_lowest_prio… | passed | **verified** | — |
| C14 | Stop during any job | U | `MonitorSchedulerTests.cs` C14_stop_during_a_job_cancels_it_and_fails_pending_requests<br>`MonitorServiceTests.cs` L2_C14_stop_while_a_tick_runs_cancels_it_closes_the_conne… | passed | **verified** | — |
| P1 | No open tx | U, I | `MonitorScenarioTests.cs` B1_B2_B8_S9_a_three_level_chain_has_the_idle_holder_as_it…<br>`MonitorTests.cs` P1_P6_P7_P8_rollback_risk_thresholds | passed | **verified** | — |
| P2 | Large write tx (≥ 100 MB log on SQL Server; long write tx on PG) | I | `MonitorIntegrationTests.cs` P2_P3_K19_terminating_a_long_write_transaction_needs_the_…<br>`MonitorSoak.cs` Four_monitors_under_churn_keep_connections_threads_and_me…<br>`MonitorScenarioTests.cs` P2_P3_P8_a_write_transaction_open_ten_minutes_is_a_high_… (PostgreSQL, `QUERITHM_IT_LONG=1`) | passed | **verified** | — |
| P3 | High risk, `ConfirmedLongRollback = false` | S | `MonitorIntegrationTests.cs` P2_P3_K19_terminating_a_long_write_transaction_needs_the_…<br>`MonitorServiceTests.cs` P3_P4_a_high_rollback_risk_recomputed_on_the_server_needs…<br>`MonitorSecurityTests.cs` P3_P4_the_rollback_risk_is_recomputed_on_the_server_at_ki… | passed | **verified** | — |
| P4 | Client sends low risk but server-side recompute is high | S | `MonitorServiceTests.cs` P3_P4_a_high_rollback_risk_recomputed_on_the_server_needs…<br>`MonitorSecurityTests.cs` P3_P4_the_rollback_risk_is_recomputed_on_the_server_at_ki… | passed | **verified** | — |
| P5 | Session already rolling back | I (SQL Server) | `MonitorMatrixTests.cs` P5_a_terminated_session_rolling_back_says_so_in_the_previ… | passed | **verified** | — |
| P6 | No permission to read tx DMVs | U, S | `MonitorTests.cs` P1_P6_P7_P8_rollback_risk_thresholds<br>`MonitorSecurityTests.cs` P6_an_unreadable_rollback_cost_needs_the_long_rollback_co… | passed | **verified** | — |
| P7 | Threshold boundary values | U | `MonitorTests.cs` P1_P6_P7_P8_rollback_risk_thresholds | passed | **verified** | — |
| P8 | Read-only PG tx (no `backend_xid`) | U, I (PG) | `MonitorScenarioTests.cs` P8_a_read_only_transaction_is_low_risk_however_old<br>`MonitorTests.cs` P1_P6_P7_P8_rollback_risk_thresholds | passed | **verified** | — |
| P9 | No time estimate ever shown | Vitest | `ActivityTab.test.tsx` KillDialog › U3, P9: shows who and what, needs the second… | passed | **verified** | — |
| O1 | Tab session appears in snapshot | U, I, E | `MonitorIntegrationTests.cs` O1_O16_K6_K7_querithm_connections_are_labelled_and_never_…<br>`MonitorServiceTests.cs` O1_a_tab_connection_appears_as_a_querithm_pool_connection… | passed | **verified** | — |
| O2 | Tab reconnects (new spid) | U, I | `ConnectionLifecycleTests.cs` A_driver_reconnect_during_a_run_is_identified_again_right…<br>`MonitorMatrixTests.cs` O2_a_tab_whose_connection_was_killed_is_known_by_its_new_…<br>`MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>`MonitorServiceTests.cs` O2_O3_a_reconnected_tab_is_known_by_its_new_identity_and_… | passed | **verified** | — |
| O3 | Spid reused by another app after tab closes | U | `MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>`MonitorServiceTests.cs` O2_O3_a_reconnected_tab_is_known_by_its_new_identity_and_… | passed | **verified** | — |
| O4 | Own tab open tx blocks another session ≥ 5 s | I, E | `MonitorScenarioTests.cs` O4_O5_O12_the_own_tab_banner_check_counts_the_sessions_a_…<br>`MonitorServiceTests.cs` O4_O5_O11_O13_O14_banner_shows_after_five_seconds_clears_…<br>`MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool | passed | **verified** | E: smoke banner "blocking 1 session for 37 s" appears and Rollback clears it |
| O5 | Blocking stops | I | `MonitorScenarioTests.cs` O4_O5_O12_the_own_tab_banner_check_counts_the_sessions_a_…<br>`MonitorServiceTests.cs` O4_O5_O11_O13_O14_banner_shows_after_five_seconds_clears_… | passed | **verified** | — |
| O6 | Tab executing | U | `MonitorServiceTests.cs` O6_O7_O12_one_shared_check_per_profile_only_for_idle_tabs… | passed | **verified** | — |
| O7 | No open tx | U | `MonitorServiceTests.cs` O6_O7_O12_one_shared_check_per_profile_only_for_idle_tabs… | passed | **verified** | — |
| O8 | Banner Commit / Rollback | U, E | `ActivityTab.test.tsx` BlockingBanner (O8) › Commit and Rollback run the tab’s o… | passed | **verified** | Commit/Rollback are the tab's own commands (`txAction`, same as the toolbar). Those commands have no Prod prompt of their own in Querithm (Prod guards apply when statements execute), so the banner adds none |
| O9 | Dismiss | Vitest | `ActivityTab.test.tsx` BlockingBanner (O9) › dismiss mutes it for 5 minutes, the… | passed | **verified** | — |
| O10 | No permission to see others | I | `MonitorMatrixTests.cs` O10_a_login_that_cannot_see_other_sessions_gets_no_banner…<br>`MonitorServiceTests.cs` O10_a_tab_whose_server_refused_its_identity_read_is_told… | passed | **verified** | — |
| O11 | Check connection fails | U | `MonitorServiceTests.cs` O4_O5_O11_O13_O14_banner_shows_after_five_seconds_clears_… | passed | **verified** | — |
| O12 | Five tabs on one profile with open tx | U, I | `MonitorScenarioTests.cs` O4_O5_O12_the_own_tab_banner_check_counts_the_sessions_a_…<br>`MonitorServiceTests.cs` O6_O7_O12_one_shared_check_per_profile_only_for_idle_tabs… | passed | **verified** | — |
| O13 | Monitor running for the profile | U | `MonitorServiceTests.cs` O4_O5_O11_O13_O14_banner_shows_after_five_seconds_clears_… | passed | **verified** | — |
| O14 | Profile pool full when the check is due | U | `MonitorServiceTests.cs` O4_O5_O11_O13_O14_banner_shows_after_five_seconds_clears_…<br>`MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool | passed | **verified** | — |
| O15 | Tabs A and B borrow the same physical connection one after the other (pool of 1) | U, I | `MonitorIntegrationTests.cs` O1_O16_K6_K7_querithm_connections_are_labelled_and_never_…<br>`MonitorServiceTests.cs` O15_O16_the_owner_badge_follows_the_lease_and_the_pin<br>`MonitorScenarioTests.cs` O15_two_tabs_that_borrow_the_same_pool_connection_in_tur… (both engines) | passed | **verified** | — |
| O16 | Pin / unpin: `BEGIN TRAN` pins, idle between statements, `COMMIT` unpins and returns it | U, I | `MonitorIntegrationTests.cs` O1_O16_K6_K7_querithm_connections_are_labelled_and_never_…<br>`MonitorServiceTests.cs` O15_O16_the_owner_badge_follows_the_lease_and_the_pin | passed | **verified** | — |
| O17 | Driver reopens a broken pooled connection | U | `MonitorServiceTests.cs` O17_O19_a_driver_reopen_records_the_new_identity_and_the_… | passed | **verified** | — |
| O18 | Dedicated connections (debugger target, monitor, kill control) | U | `MonitorServiceTests.cs` O14_a_slow_connection_open_is_not_mistaken_for_a_full_pool<br>`MonitorServiceTests.cs` O18_dedicated_connections_are_labelled_by_kind_and_never_… | passed | **verified** | — |
| O19 | Registry growth: 1,000 physical opens and closes | U | `MonitorServiceTests.cs` O17_O19_a_driver_reopen_records_the_new_identity_and_the_… | passed | **verified** | — |
| W1 | Each rule fires on its fixture input (blocked, idle blocker, hidden blocker, memory grant, parallel skew, scan, plan warnings, PG idle-in-tx, PG IO/LWLock) | U (one test per rule) | `MonitorCoverageTests.cs` W1_each_rule_fires_on_its_fixture_input_with_its_evidence | passed | **verified** | — |
| W2 | Multiple rules match | U | `MonitorCoverageTests.cs` W2_several_matching_rules_are_ordered_by_severity_and_det… | passed | **verified** | — |
| W3 | Nothing matches | U | `MonitorCoverageTests.cs` W3_nothing_matching_says_no_obvious_cause_with_the_numbers | passed | **verified** | — |
| W4 | Session ended | U | `MonitorCoverageTests.cs` W4_a_session_that_ended_says_so | passed | **verified** | — |
| W5 | Plan unavailable | U | `MonitorCoverageTests.cs` W5_without_a_plan_the_plan_rules_are_skipped_and_noted | passed | **verified** | — |
| W6 | Real blocking pair | I | `MonitorScenarioTests.cs` B1_B2_B8_S9_a_three_level_chain_has_the_idle_holder_as_it… | passed | **verified** | — |
| W7 | Identifiers with markup/control chars in evidence | S, Vitest | `MonitorCoverageTests.cs` W1_each_rule_fires_on_its_fixture_input_with_its_evidence<br>`MonitorTests.cs` X2_X6_deadlock_graphs_parse_with_victims_resources_and_ex…<br>`MonitorSecurityTests.cs` W7_diagnosis_evidence_carries_server_names_as_plain_data_…<br>`ActivityTab.test.tsx` Diagnosis rendering (W7) › renders identifiers with marku… | passed | **verified** | — |
| W8 | Dictionary JSON valid, covers every wait in the benign/category lists and all PG events for supported versions | U | `MonitorScenarioTests.cs` L5_a_server_restart_reconnects_and_reports_the_counter_reset<br>`WaitDictionaryTests.cs` W8_entries_are_complete_and_cover_every_benign_and_notabl…<br>`WaitDictionaryTests.cs` W8_categories_match_the_providers_category_rules<br>`WaitDictionaryTests.cs` W8_every_wait_event_postgresql_17_reports_is_described | passed | **verified** | — |
| W9 | Unknown wait type | U | `WaitDictionaryTests.cs` W9_an_unknown_wait_has_no_description_yet_and_never_fails | passed | **verified** | — |
| W10 | Dictionary loads under trimming | U | `WaitDictionaryTests.cs` W10_the_dictionary_loads_in_the_published_trimmed_sidecar | passed | **verified** | — |

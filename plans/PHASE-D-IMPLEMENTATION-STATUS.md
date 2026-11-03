# Phase D — implementation status

Requirement-to-evidence checklist for [PHASE-D-ENGINE-EXPANSION.md](PHASE-D-ENGINE-EXPANSION.md). Last updated
2026-10-31 (Linux, fourth pass).

**Status in one line:** D0a–D0g are implemented; D1 (SQLite), D2 (MySQL / MariaDB) and D3 (DuckDB) are implemented and their
feature matrices have no **gap** left (fourth pass, below). **2026-11-01: they left the hidden-engine flag** — the CI
`release-smoke` job passed on win-x64, linux-x64, osx-x64 and osx-arm64 (main, run 41322336312) and the owner decided to ship
them in 0.2.0; they are active engines now (CLAUDE.md §2). The paragraph below is the state before that decision. Owner decisions (2026-10-31): the Linux AppImage gets its own ≤ 150 MB budget (143.4 MB measured; every other package
stays ≤ 120 MB), and #23.mysql's 3.94 s is accepted as the measured baseline (LOAD DATA LOCAL stays off). Before un-hiding
them: the CI `release-smoke` run on win-x64 / osx-x64 / osx-arm64 (needs a push) and the release decision. D4 (Oracle) is deferred by the owner; D5–D7 are not started.

**Statuses** — *done*: code and tests exist and passed in this pass. *partial*: the part named is done, the rest is listed.
*not started*.

**Environment** — Linux (Ryzen 7 4700U), Docker fixture `scripts/bench/docker/compose.yml`: SQL Server 2022, PostgreSQL 16 /
17 / bare 16, the SSH bastion, and (new) MySQL 8.4.13 and MariaDB 11.8.9 pinned by digest. .NET SDK 10.0.403, Node 24.22.2.
No Tauri build: this machine has no GTK/WebKit development libraries, so the `src-tauri` crate does not compile here (see
"Rust").

## Slices

| Slice | Status | Evidence | Open |
|---|---|---|---|
| D0a contract, profile model, store | done | `ProfileModelTests` (43); `auth.test.ts`; `connections.more.test.ts` (unsupported profile); `dev-bridge-secrets.test.ts`; Rust `injection.rs` tests | Keyring skip is tested in the real Rust crate now (`cargo test`, 37) and the dev bridge; not end to end in a running Tauri app |
| D0b provider-owned transactions | done | `TransactionControlTests` (9); `CoverageProviderDialectTests`; full integration suite on SQL Server / PostgreSQL; engine bench (see below) | — |
| D0c file grants | done | `Security/FileGrantSecurityTests` (20); Rust `grants.rs` + `injection.rs` (18, run in a std-only scratch crate); `dev-bridge-grants.test.ts`; `capabilities.test.ts`; trimmed self-test (`FileIdentity` in a single-file build) | — (the Rust commands and relay compile and their tests run in the real crate since the fourth pass). Revoke / re-pick UI done (`fileAccess.test.ts`, 10) |
| D0d connection ownership, session-scoped metadata | done (sidecar) | `SessionOwnedTests` (12); `FileEngines/SqliteIntegrationTests` (temp objects, transactions) | — (UI snapshots keyed by session: `snapshots.session.test.ts`, 19; memory-loss notice after an engine restart: `actions.localEngines.test.ts`, 15) |
| D0e two-engine audit, identifier identity | done | `IdentifierIdentityTests` (5); `engines.test.ts` (24), `quoteIdentifier.test.ts` (10) and the TS exhaustiveness (adding an engine fails the build at each decision) | `localRenameText` quotes reserved words in every dialect (fourth pass, `refactor.test.ts`); SQL Server/PG integration suites not merged into shared theories (a test refactor, no behaviour) |
| D0f stable Arrow schemas | done (sidecar) | `DynamicColumnTests` (8) | see "Webview" |
| D0g UI, languages, tests, bench | partial | `system.engines` + hidden engines (`EngineRegistryTests`, 8); `TestEngines` shared source; bench `--sqlite` metrics; file-engine tests in `FileEngines/` without Docker | see "Webview" (smoke-ui now runs a SQLite file workflow: create the profile, write, refused ATTACH in Messages) |
| D1 SQLite | partial (hidden) | `SqliteIntelligenceTests`, `FileEngines/SqliteIntegrationTests`, `Security/SqliteSecurityTests` (13); `FileEngines/SqliteEditImportTests` (7: rowid key editable, BLOB locked, affinity typing, original-value conflict rolls the whole patch back, nullable declared PK read-only, WITHOUT ROWID editable, read-only grant refuses, CSV import + PK failure leaves nothing); trimmed single-file self-test; bench 11cold/15/9/10 `.sqlite` | Feature matrix complete (no **gap** left; see below); release smoke of the other RIDs runs in CI (not run yet); un-hiding still needs the release decision |
| D2 MySQL / MariaDB | partial (hidden) | `MySqlIntelligenceTests` (15); `MySqlDialectTests` + `MySqlPlanTests` (44, no server); `Integration/MySqlIntegrationTests`, `MySqlEditImportTests`, `MySqlCaseTests`, `MySqlSshAuthTests`, `MySqlMonitorTests`, `MySqlCompareTests`, `MySqlSchemaCompareTests`, `MySqlRefactorTests` (128 with the theories over MySQL 8.4.13, MySQL 9.7.4, MariaDB 11.8.9 and the `lower_case_table_names=1` container); grid edit, import, actual plans, SSH tunnel (+ loss), `caching_sha2_password` full auth against the container address, MariaDB ed25519 refusal, activity monitor + identity read, data compare, schema compare (round trip: compare → migrate → compare again identical, on all three products), find usages / rename, per-engine bench (11cold 11.6 ms, 8 2.4 ms, 15 14.3 ms, 9 33.2 ms, 10 119 ms — `pnpm bench -- --mysql`) done (`MySqlSchemaCompareRulesTests` 20 unit, `MySqlReferenceFinderTests` 11; `MySqlDataCompareTests` 22 unit; `MySqlActivityRulesTests` 20 unit; monitor UI `mysqlMonitor.test.tsx` 3); MySQL 9.7 found MD5() gone (markers now SHA-256) and the JSON v2 plan format (now read); trimmed self-test | other RIDs in CI (not run yet); 23.mysql over its target, accepted by the owner as the measured baseline (§16). Done in the fourth pass: tier 2 / signature help, 5 / 20–24 / 28 / 31 / 32 bench ids, the webview compare / rename / monitor screens against a MySQL session (smoke-ui) |
| D3 DuckDB | partial (hidden) | Data-plane slice (Apache.Arrow 18.1.0 → 23.0.0, byte-identical frames: `CrossLanguageFrameTests` + `frames.golden.test.ts`, commit 386c26e). Provider slice (third pass): `Querithm.Providers.DuckDb` + `Querithm.Intelligence.DuckDb` on DuckDB.NET.Data.Full 1.5.6.1 — **security first**: `Security/DuckDbSecurityTests` (locked policy verified on every connection; SET / RESET cannot unlock; extensions, URLs, outside files, ATTACH, COPY, EXPORT, secrets, profiler paths refused by the engine; path aliases and case variants of an open file; two modes on one file; a file whose view / macro calls `enable_logging` / `query` is not opened; spills land under app data with a 256 MB cap; the temp sweep; 35 statements the classifier refuses and 10 look-alikes that still run), `Security/StdoutGuardTests` (native writes to fd 1 reach stderr, not JSON-RPC — fails without the guard); `DuckDbLanguageTests` (tokenizer parity, classifier, origin prediction, exact decimals, intervals, nested literals); `FileEngines/DuckDbIntegrationTests` (types, read-only / writable / empty / corrupt / locked-by-another-process files, cancel then reuse, cancel during a file read and a cancelled write leave the file whole, transactions, catalog, plans, parameters, Prod guards, grid edits + conflicts, appender import with rejects / defaults / cast fallback / batches); smoke-ui "DuckDB file database"; bench `--duckdb` | other RIDs' release smoke in CI (not run yet); the Linux AppImage (143.4 MB) has its own ≤ 150 MB budget by owner decision (§16). Done in the fourth pass: tier-2 statement cache, macOS dylib thinning, the Rust shell compiled and tested |
| D4 Oracle, D5 ClickHouse, D6 CockroachDB, D7 TiDB | not started | — | — |

## Feature matrices (release gate: every row implemented, or degraded with a visible reason and a test)

Status: **done** (implemented, test named) · **degraded** (refused or reduced with a visible reason, test named) · **gap** (not
done, or degraded without a visible reason or test yet). An engine leaves the dev flag only when it has no **gap**.

| Feature | SQLite (D1) | MySQL / MariaDB (D2) | DuckDB (D3) |
|---|---|---|---|
| Editor highlighting, folding | done (webview languages; `engines.test.ts`; folding from the splitter, `SqliteIntelligenceTests`) | done (same; `MySqlIntelligenceTests`) | done (`duckdbLanguage`, `monaco.test.ts`; folding from the splitter) |
| Diagnostics | as PostgreSQL: no server-side parse diagnostics (only T-SQL has them, through ScriptDom) | as PostgreSQL | as PostgreSQL |
| Signature help | done: the bundled library's built-in functions — SQLite stores no routines (`Signature_help_knows_the_built_in_functions_with_or_without_a_snapshot`; every name checked against the library: `Every_built_in_function_signature_help_offers_exists_in_the_bundled_library`) | done: stored routines from the snapshot (`Signature_help_shows_a_stored_procedures_parameters_and_no_named_arguments_are_offered`; live on three products: `Tier_two_completion_and_signature_help_read_the_live_snapshot`) | done (shared tier-2 engine under DuckDB rules: `Signature_help_shows_a_macros_parameters`) |
| Formatting | done (webview sql-formatter dialect) | done (same) | done (sql-formatter `duckdb`) |
| Completion tier 1 / tier 2 | tier 1 done (webview); tier 2 done: PostgreSQL's engine under SQLite's rules (`SqliteIntelligenceTests` tier-2 cases: quoting styles, trigger bodies, INSERT / REPLACE lists; over RPC on the session's snapshot: `Tier_two_completion_and_signature_help_run_over_rpc_on_the_sessions_own_snapshot`) | tier 1 done; tier 2 done under MySQL's rules (backticks / ANSI_QUOTES, USE moves to that database's snapshot, DELIMITER bodies: `MySqlIntelligenceTests`; live: `Tier_two_completion_and_signature_help_read_the_live_snapshot`) | done: tier 1 (DuckDB 1.5.6 keywords, lexer rules: `lexer.test.ts`); tier 2 = PostgreSQL's token-scoped engine under `PgCompletionRules` (case-insensitive names, no code in dollar strings, `;` statements, USE / SET schema context: `DuckDbLanguageTests`) |
| Execute / cancel / transactions | done (`FileEngines/SqliteIntegrationTests`, `TransactionControlTests`) | done (`MySqlIntegrationTests`: cancel + reuse, DDL implicit commit) | done (`DuckDbIntegrationTests`: cancel + reuse, aborted transaction needs ROLLBACK, no savepoints with reason) |
| Ad-hoc parameters | done (`:a`/`@a`/`$a`/`?` theory) | degraded with reason (`@x` is a user variable; webview note, `sqlText.userVariables.test.ts`) | done (`$a` / `$1` / `?` / `?1` theory; `sqlText.test.ts`) |
| Grid + grid editing | done (`SqliteEditImportTests`) | done (`MySqlEditImportTests`, `MySqlDialectTests`) | done (`Grid_edits_write_typed_values_check_conflicts_and_tables_without_a_key_stay_read_only`); origins predicted for single-table SELECTs only (others read-only with reason) |
| Export, history, charts, notebooks | done: SQLite result shapes (dynamic columns) through CSV / TSV / JSON / XLSX (`export.engines.test.ts`; XLSX now types each cell by its storage class — it wrote every SQLite value as text), charts (`chartEngines.test.ts`), notebook outputs and exports (`outputEngines.test.ts`), history badge and re-open (`Dialogs.test.tsx`) | done: MySQL shapes (signed TINYINT, exact DECIMAL text, DATETIME, JSON, BIT, unsigned BIGINT) through the same tests; exact decimals and DATETIME stay text in charts / XLSX (pinned; a typed-text flag would need sidecar field metadata) | generic paths over the Arrow types DuckDB sends (Int*, Utf8 incl. exact decimals and nested text, Binary, Timestamp, Bool — the golden frames decode each in the webview; `Duckdb_types_arrive_typed_or_as_exact_text`) |
| Explorer, `meta.rowCounts` | done (snapshot tests; `Row_counts_come_from_analyze_and_modify_apply_runs_again`: unknown before ANALYZE, then sqlite_stat1) | done (catalog test; `Row_counts_and_modify_apply_…`: TABLE_ROWS, estimated) | done (catalog test; user types: `Dependencies_row_counts_scripts_types_and_modify_apply_of_a_view_and_a_macro`; row counts from estimated_size) |
| Scripting, Modify / Apply / Execute | done (Modify → Apply of a view, same test) | done — Modify → Apply was broken (plain SHOW CREATE re-ran as "already exists"), now OR REPLACE / DROP IF EXISTS + DELIMITER (`Row_counts_and_modify_apply_…`, three products) | done (Modify → Apply of a view and a macro, ENUM / STRUCT type scripts: same test) |
| Dependencies | done (`Dependencies_come_from_the_stored_sql_of_views_and_triggers`) | done (`Dependencies_are_the_real_references_in_definitions`, three products) | done on tokens (views, macros; case-insensitive; same test) |
| Plans | estimated done, actual degraded with reason (`SqliteIntegrationTests`) | done, actual SELECT/TABLE only with reason (`MySqlIntegrationTests`, `MySqlPlanTests`) | done, estimated and actual (`Estimated_and_actual_plans_become_plan_trees_and_parse_back`); DuckDB 1.5.6's missing JSON profile reported with reason |
| Import | done: prepared-row loader, all three transaction modes (`SqliteEditImportTests`: `A_csv_imports_…`, `Batches_mode_commits_every_batch_…`, `Inside_an_open_transaction_the_import_joins_it_…`); in-memory databases import through the tab session (`importLocalEngines.test.tsx`) | done: multi-row loader, all three transaction modes and rejects (`MySqlEditImportTests`: `A_csv_imports_with_multi_row_inserts_…`, `Batches_commit_as_they_go_and_inside_a_transaction_the_import_joins_it`) | done (appender; three tests) |
| ERD | done (`erdEngines.test.ts`: nodes, FKs, quoting, labels; an in-memory database reads the tab session's snapshot: `erdLocalEngines.test.tsx`) | done (`erdEngines.test.ts`, database-qualified backtick labels) | done on the snapshot's FKs (`The_catalog_lists_tables_keys_indexes_views_macros_and_sequences…` asserts them); layout is engine-agnostic |
| Schema / data compare | degraded with reason (`Features_SQLite_lacks_say_why_and_the_sidecar_refuses_them`; the compare tabs and Explorer menu show it with Compare disabled: `compareLocalEngines.test.tsx`, `menus.test.ts`) | done (`MySqlSchemaCompareTests`, `MySqlCompareTests`) | degraded with reason (`Features_DuckDB_lacks_say_why_and_the_sidecar_refuses_them`) |
| Find usages / rename | degraded with reason (same test) | done (`MySqlRefactorTests`, `MySqlReferenceFinderTests`) | degraded with reason (same test) |
| SSH | n/a (local file; profile model refuses) | done (`MySqlSshAuthTests`) | n/a (local file; profile model refuses) |
| Safety guards (destructive, Prod) | done (`Security/SqliteSecurityTests`, classifier) | done (classifier incl. executable comments; `MySqlIntelligenceTests`) | done (`DuckDbSecurityTests`, `DuckDbLanguageTests`, `Prod_guards_apply_to_file_profiles`) |
| Monitor | degraded with reason (a file on this computer) | done (`MySqlMonitorTests`, `MySqlActivityRulesTests`, `mysqlMonitor.test.tsx`) | degraded with reason (runs inside Querithm) |
| Snippets dialect filter | done (`snippets.test.tsx`: filter + editor offers every dialect) | done (same) | done (`snippets.test.tsx`) |
| UI smoke | done: profile through the editor, connect → execute → results, grid edit applied and read back through Querithm, import wizard (3 rows), refused ATTACH in Messages (smoke-ui "SQLite file database") | done: connect → execute → results, grid edit applied and read back on the server, import wizard read back on the server, find usages + rename preview, schema compare + migration script, data compare with differences, activity monitor sessions (smoke-ui "MySQL") | done: the same workflow on an empty picked file that becomes a database, refused COPY TO in Messages (smoke-ui "DuckDB file database") |
| Bench ids | 11cold, 15, 9, 10, 5, 20, 23 (`.sqlite`), all within target | 11cold, 8, 15, 9, 10, 5, 20, 21, 22, 24 (`.mysql`) within target; 23.mysql 3.94 s, the measured baseline the owner accepted (§16: LOAD DATA LOCAL stays off); 28 / 31 / 32 within target on MySQL 8.4 (`MonitorBench`, §14 row; 29 synthetic covers it, 30 n/a: deadlock history degraded) | 11cold, 9, 10, 15, 23, 5, 20 (`.duckdb`), all within target |
| Trimmed release smoke per RID | linux-x64 self-test here; win-x64, osx-x64 and osx-arm64 self-test in CI's new `release-smoke` job (ci.yml) — **not run yet** (needs a push) | same | same; macOS publishes keep only their slice of libduckdb.dylib (osx-arm64 50.5 MB, osx-x64 53.6 MB; `MachOThinTests`) |
| Installer size (metric 17) | see DuckDB | see DuckDB | decided: the Linux AppImage measures 143.4 MB with this sidecar (rebuilt from the v0.1.5 image) under its own ≤ 150 MB budget (owner decision, §16 "Installer size with DuckDB"; release.yml and `17.appimage` gate it); the other packages project well under 120 MB |

## Fourth pass — D0–D3 gap closure (2026-10-31, after commit 879c2a3)

What changed: SQLite and MySQL tier 2 and signature help (PostgreSQL's engine under their rules; SQLite signature help from
the bundled library's built-in functions); profiled fixes found by the new per-engine bench ids — tier-2 statements are
split once per document (#5.sqlite 179 → 23.6 ms), DuckDB / MySQL USE context scanned once per document (#5.duckdb 264 →
57.7 ms), `MySqlSplitter` made linear (quadratic before: a 10,000-statement script never finished), a prepared-row SQLite
loader (#23.sqlite 7,792 → 563 ms) and a convert-once MySQL loader (#23.mysql 4,965 → 3,940 ms, still over: §16);
macOS libduckdb.dylib thinned per RID in the publish; the Rust shell compiled and tested here; `gen-notices.mjs` keeps the
DuckDB native row (a release build would have dropped it); CI `release-smoke` job for every shipped RID; webview: local
engines' tools read the tab session's snapshot, compare refusals shown, per-engine ERD / export / chart / notebook /
history tests, reserved-word rename quoting, and two webview bugs (DuckDB's current statement split at every FROM; XLSX
wrote every SQLite value as text).

| Gate | Result |
|---|---|
| `dotnet test sidecar` (unit) | 2415 passed (2383 before) |
| `QUERITHM_IT=1 dotnet test sidecar` | 2411 passed with every fixture up (before the last import-service change, whose suites — 154 import tests — and the MySQL monitor suites, 18 + 3 `MonitorBench`, were re-run after it) (SQL Server, PostgreSQL 16 / 17 / bare, SSH, MySQL 8.4 / 9.7 / lctn, MariaDB 11.8), 3 m 45 s — new: SQLite / MySQL loaders in every transaction mode, live MySQL tier 2 on three products |
| `npx tsc -b --noEmit` / `npx vitest run --maxWorkers=2` | clean / 177 files, 3057 tests passed |
| Rust (`src-tauri`) | `cargo check` clean (two dead-code warnings fixed); `cargo test` 37 passed — first time the whole crate compiled here (GTK / WebKit dev packages extracted without root) |
| `cargo audit` (0.22.2) | 533 crates, no vulnerability; 5 allowed warnings (unmaintained: proc-macro-error, zbus_names, zvariant, zvariant_derive; unsound: glib 0.18 VariantStrIter) |
| `dotnet list sidecar package --vulnerable --include-transitive` | no vulnerable package in any of the 15 projects (no package was added in this pass) |
| Trimmed single-file publish + `--self-test` | linux-x64 ok (65 MB file, all eight checks); osx-arm64 50,538,161 and osx-x64 53,646,113 bytes after thinning (osx-arm64 was 69,399,041); win-x64 / macOS self-tests are the new CI job (not run yet) |
| Per-engine bench | `--sqlite` all within target (`2026-10-31-15-19-41-linux.json`); `--duckdb` all within target (`2026-10-31-15-33-37-linux.json`); `--mysql` all within target but 23.mysql 3.94 s (`2026-10-31-15-50-45-linux.json`) |
| smoke-ui | exit 0 on 1420 / 1421 with every step, now also MySQL find usages / rename / schema compare / data compare / activity monitor (first run: the SQLite import wizard hung — `executeGuarded` subscribed to `query.done` after `query.execute` returned, and the tab's warm SQLite session finished the CREATE TABLE first; Modify → Apply had the same race; both fixed with tests that fail without the fix) |
| `pnpm bench` (full, quiet machine after a reboot) | full run `2026-10-31-16-17-53-linux.json` on a quiet machine (after a reboot): every metric within target except 23.mysql (4.09 s, the §16 baseline). The second pass's under-load misses now pass: #4 4.2 ms, #34 470.8 ms, #14 0 frames; #5 25.6 ms, #27 67.5 ms (the shared tier-2 engine changed). It flagged #11 183 → 263.6 ms, #24 76.4 → 97.7 ms and #9.sqlite 31.9 → 49.9 ms; re-runs of the same code read #11 183.6 ms, #24 85.3 ms, #9.sqlite 52.1 ms (its range here is 44–52; 31.9 was a low outlier) and flagged #9 37.6 → 57.5 ms instead (its range here is 34–60 ms) — run-to-run noise on this laptop, none on a path this pass changed |
| Installer size | Linux AppImage 143.4 MB measured by rebuilding the v0.1.5 image with this sidecar; the owner gave the AppImage its own ≤ 150 MB budget (§16) |

## Third pass — D3 DuckDB provider (2026-10-31, after commit 386c26e)

| Gate | Result |
|---|---|
| `dotnet test` (unit) | 2383 passed (2237 before; +146: DuckDB security, language, file-engine integration, stdout guard) |
| `QUERITHM_IT=1 dotnet test`, MySQL suites excluded | 2163 passed (SQL Server, PostgreSQL 16 / 17 / bare, SSH); the MySQL / MariaDB containers were stopped to keep memory free — the change touches them only through the shared encoder's Stream path |
| `npx tsc -b --noEmit` / `npx vitest run --maxWorkers=2` | clean / 169 files, 2977 tests passed |
| smoke-ui (ports 1530/1531) | exit 0 with the new "DuckDB file database" step (SQLite and DuckDB now share one workflow function; the cell edit waits for the grid's focus) |
| Trimmed single-file publish + `--self-test` | linux-x64 ok (`duckdb ok`: locked policy, refused COPY TO, exact DECIMAL(38,18), a list holding NULL through the generic read): 68,027,104 bytes (base 44,732,430, +23.3 MB); win-x64 45,393,934 and osx-arm64 69,399,041 bytes published, not run |
| Startup (spawn → first `system.ping`, linux-x64 trimmed, base vs DuckDB build, A/B) | warm medians 825 / 826 ms; first run (native extraction) 920 / 1,105 ms — once per install |
| `pnpm bench -- --duckdb` | 11cold.duckdb 81.3 ms, 9.duckdb 54.5 ms, 10.duckdb 71.2 ms, 15.duckdb 1.8 ms, 23.duckdb 788 ms — all within target (`2026-10-31-13-12-21-linux.json`); batched INSERTs had taken 24,090 ms for #23 (`2026-10-31-13-07-01-linux.json`), hence the appender |
| `dotnet list sidecar package --vulnerable --include-transitive` | no vulnerable package in any of the 15 projects |
| `cargo audit` | not run: no crate changed (`sidecar.rs` sets one environment variable; not compiled here) |

## Second pass (2026-10-31, after commit a7d881a)

| Gate | Result |
|---|---|
| `dotnet test` (unit) | 2157 passed |
| `QUERITHM_IT=1 dotnet test` | full run 2136 passed before the monitor slice (every fixture, incl. MySQL 9.7.4 and the lctn container); after it, the MySQL + monitor suites 413 passed and `MySqlMonitorTests` 5/5 twice |
| `npx tsc -b --noEmit` / `npx vitest run` | clean / 167 files, 2958 tests passed (one earlier run at load average 19 had 17 timeouts; the next two runs were fully green) |
| smoke-ui (ports 1530/1531) | exit 0 with the new SQLite step (before the monitor UI tables changed) |
| Trimmed single-file publish (linux-x64) + `--self-test` | ok, now also checking the MySQL plan raw form through System.Text.Json and the TREE parser, and the DEFINER removal; 44,778,606 bytes (+75,869 over the first pass) |
| `pnpm bench -- --mysql` | first run, all within target: 11cold.mysql 11.6 ms, 8.mysql 2.4 ms, 15.mysql 14.3 ms, 9.mysql 33.2 ms, 10.mysql 119 ms (`2026-10-31-08-55-37-linux.json`) |
| Dependency audits | not run: no package or crate was added in this pass |
| `pnpm bench` (full) then `--ui` | **outside target**: #4 completion p95 (6.0 / 6.8 / 5.5 ms vs ≤ 5), #34 chart toggle (≈ 690 ms vs ≤ 500), #14 one long frame. A/B against HEAD (without this pass's webview changes) in a scratch worktree, alternating runs on the same machine: HEAD gave #4 4.3 / 4.3 / 4.3 / 6.5 ms, #34 682–1040 ms, #14 0–1 — the same failures; both builds timed completion against the same 1157-object schema (now recorded with #4 as `schemaObjects`). The machine ran at load average 2–4 throughout (the 03:47 baseline was quieter), so these are not attributed to the code, but the gate is not green: re-run `pnpm bench` on a quiet machine before calling it done |

## Gates run in this pass

| Gate | Result |
|---|---|
| `dotnet test sidecar` | 2041 passed (unit; 1831 at the start of Phase D) |
| `QUERITHM_IT=1 dotnet test sidecar` | 2030 passed on every fixture (SQL Server, PostgreSQL 16/17/bare, SSH, MySQL 8.4, MariaDB 11.8); the tests added after that run (MySQL warnings, SQLite parameter forms) then passed on their own: MySQL 22/22, SQLite parameters 9/9 |
| `node scripts/bench/smoke-ui.mjs` | passed (exit 0) on ports 1530/1531 (1420/1421 were in use by a dev server already running) |
| Trimmed single-file publish (linux-x64) + `--self-test` | ok (wait dictionary, monitor JSON, history, SQLite hardening + file identity, MySqlConnector options); 44,260,473 → 44,702,737 bytes with the MySQL provider (+442 KB) |
| `pnpm bench -- --engine` | final run `2026-10-31-06-34-10-linux.json`: every metric within target, 0 regressions (the SQLite ids included). Earlier runs flagged #9, then #22 and #24, between two runs of the same code (run-to-run noise on this laptop) |
| `pnpm bench -- --sqlite` | 11cold 1.9 ms, 15 1.1 ms, 9 47.2 ms, 10 23.3 ms (first run, all within target) |
| Rust | `grants.rs` and `injection.rs` tests pass under rustc 1.99 in a scratch crate; `files.rs` / `ipc.rs` / `main.rs` edits are checked against the tauri-plugin-dialog 2.8.3 API but not compiled |
| `dotnet list sidecar package --vulnerable --include-transitive` | no vulnerable packages in any project (MySqlConnector 2.6.2 and the explicit SQLitePCLRaw.core 2.1.13 pin included) |
| `cargo audit` | not run: no new crate was added (only the `Win32_Storage_FileSystem` feature of the existing windows-sys) |

## Webview

Done in two TypeScript passes (delegated, reviewed through tsc, vitest and the smoke run): `src/engines.ts` makes every engine
and dialect decision exhaustive; SQLite and MySQL / MariaDB have their entries (Monaco languages, quoting, lexer rules, keywords,
formatting through sql-formatter, run-dialog parameters — none for MySQL, where `@x` is a user variable —, Execute… builders,
plan formats `sqlite-eqp` / `mysql-json`, own engine marks); the profile editor offers exposed engines and the local-file flow;
in-memory profiles are shown without an Explorer session; the D0f consumers (sort, export, charts, cell viewer) and exact
database-name identity are in place.

| Check | Result |
|---|---|
| `npx tsc -b --noEmit` | no errors |
| `npx vitest run` | 166 files, 2955 tests passed (2704 at the start of Phase D) |

Third pass (delegated, then re-run here): refusals (`refused` / `memoryLost`) appear in the tab's Messages ending "Not run";
MySQL's unset `@name` gets its reason (token-based, `sqlText.userVariables.test.ts`); `NamespaceModel` drives the database
menu's grouping label; per-session snapshots for local engines; the in-memory loss notice; revoke / re-pick; the smoke SQLite
step. Checked here: `npx tsc -b --noEmit` clean, `npx vitest run` 166 files / 2955 tests passed, smoke-ui exit 0 on ports
1530/1531. Still open: the ER diagram, compare and import read the shared snapshot (an in-memory database shows nothing there).

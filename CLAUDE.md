# Querithm — Golden Rules for Claude (Solo-Dev Mode)

Read `docs/ARCHITECTURE.md` first (§2, §4, §5, §12, §13, §15 before writing code; §16 lists deviations). Product name is **Querithm** everywhere (`Querithm.*`, `querithm`).

## 1. Non-Negotiable Architecture Boundaries
- **Three Processes, Strict Ownership (§4):**
  - **Rust (`src-tauri`):** Window, menus, file dialogs, OS keyring, sidecar spawn/kill, raw binary IPC relay. **Zero** SQL, parser, or driver code in Rust. When two designs compete, pick the one that keeps `src-tauri` smaller.
  - **.NET 10 (`sidecar`):** Sessions, pools, `IDbProvider` drivers, Arrow serialization, AST/token intelligence, SSH tunnels, and `store.db`. **Zero** window, file-dialog, or keyring code in .NET.
  - **React 19 (`src`):** UI shell, shared single Monaco instance (`editor.setModel()` on tab switch — never mount one Monaco per tab), Glide Data Grid, Zustand per-tab state.
- **Contract-First IPC (§5):** Every RPC method gets a C# DTO in `sidecar/Querithm.Contract`. Run `pnpm gen:contract` to regenerate `src/ipc/contract.ts` — **never edit `contract.ts` by hand**.
- **Arrow-Only Data Plane:** Query rows cross IPC **only** as self-contained Apache Arrow frames (`0x01` schema, `0x02` batch) over the local socket. Never serialize row sets as JSON.
- **Offline-First & Trimming Safe:** Bundle every asset (fonts, icons, Monaco workers). No CDNs, no network-capable plugins in Tauri. Every new library needs a row in the §2 table; every new .NET dependency must be pinned in `sidecar/Directory.Packages.props`, logged in `THIRD-PARTY-NOTICES.md`, and work under the Host's partial trimming (`TrimMode=partial`; add roots for reflection-heavy types).

## 2. Multi-Engine Discipline
- **Every Engine Counts:** Every feature/fix must support all active engines: today `SqlServer`, `Postgres`, `MySql` (MySQL / MariaDB), `Sqlite` and `DuckDb`. A later engine (Phase D4+) joins this list once its provider lands — don't stub engines that don't exist yet. Check each engine's provider (`sidecar/Querithm.Providers.*`), intelligence layer (`sidecar/Querithm.Intelligence.*`), and UI dialect handling. When a new engine is added, extend the existing per-engine test suites to cover it.
- **Provider Isolation:** `Querithm.Core` must never reference concrete ADO.NET driver types directly. Route all engine-specific behavior through `IDbProvider`, `IReferenceFinder`, and dialect services (`ISchemaCompareDialect`, `IDataCompareDialect`, …).
- **Token/AST Only — Never Regex Over SQL:** Statement splitting, identifier quoting (`[x]`, `"x"`, `` `x` ``), destructive classification, completion, and refactoring must use the engine's lexer/tokens or AST (`ScriptDom` for T-SQL, the dialect scanner for PG, and a dialect scanner for each new engine). Never use regex on raw SQL text.
- **Graceful Capability Degradation:** If an engine or server version lacks a feature (e.g., file-based DBs lacking `ChangeDatabaseAsync`, engines without step debuggers or `rowversion`), degrade visibly with a clear user-facing reason and add a test for that degradation path.

## 3. Security & Production Safety (§13)
- **Keyring Exclusivity:** Passwords, SSH keys/passphrases, encryption keys, and API keys live **only** in the OS keyring (`querithm` service). Never write secrets or raw connection strings to `store.db`, logs, crash dumps, or exported files. Keep the log scrubber updated for any new secret parameter names.
- **Sidecar Enforces Guards:** Production write guards (`GuardReason.ProdWrite`) and destructive statement checks (`confirmedDestructive: true`) must be enforced in the .NET sidecar, never solely in the React UI.
- **Filesystem Sandbox:** The sidecar never writes into user directories; picked files are read-only via shell-authorized tokens, and sidecar outputs go to `<appDataDir>/querithm`. The one exception (Phase D0c): the sidecar writes a user file only through a ReadWrite database grant given in a dialog Rust shows, limited to that database and its engine-managed journal/WAL/SHM files (§13 rule 6).
- **Validate Untrusted Input:** Treat every RPC parameter, imported file (CSV/JSON/XLSX, `.qschema.json`, `.sqlbook`), server-returned name, and LLM output as untrusted. Validate shape/size/range in the sidecar at the RPC boundary; generated SQL quotes identifiers through the engine's quoting service and passes values as parameters — never string-concatenated.
- **Least Privilege:** Keep Tauri capabilities at the §13.6 minimum (`dialog`, `fs` scoped to `$APPDATA/querithm` + picked paths, no `http`); the sidecar binds no TCP port except the §13.4 SSH forward and authenticates its socket; TLS stays on by default (§13.2). Any new permission needs a §16 entry with the reason.
- **Review Dependencies:** Before adding a package, check its license, maintenance, transitive deps and network/reflection behavior; `cargo audit` and `dotnet list package --vulnerable` must show no high-severity findings (§13.10).
- **Never Commit Real Credentials:** Only the documented dev fixtures (`scripts/bench/docker/compose.yml`, `scripts/bench/docker/ssh/`) may hold credentials, and they must never work outside the local Docker fixture. No real connection strings, hostnames, tokens or keys in code, tests, plans, docs or commit messages.
- **User Data is Sensitive:** Query results, history, autosaved scripts and schema contents are user data: keep them in `<appDataDir>/querithm`, never in logs or crash dumps beyond what the user explicitly exports, and never send them off-device (Phase 2 cloud LLMs go through the PII scrubber and require explicit opt-in).
- **Security Suite stays Green:** Every rule in §13 has tests in `sidecar/Querithm.Tests/Security/` that must remain green; write the test first for any new §13 rule.

## 4. Performance, Reliability & UX Polish (§12, §14)
- **Zero Frame Drops on Hot Paths:** Never trigger React re-renders on editor keystrokes or grid scroll frames. Append incoming Arrow batches to a ref and update Glide row counts incrementally.
- **Lazy Startup:** Keep cold start ≤ 1.5s. Defer heavy initialization to first use (e.g. ScriptDom/completion JIT warm-up on the first `session.open`, §16); `session.open` itself dials nothing (§6).
- **Measured Targets, Not Adjectives:** Every performance-sensitive feature gets a numeric target and a measurement method in the `docs/ARCHITECTURE.md` §14 table (new row if none fits), measured on the §14 fixture.
- **Profile Before Optimizing:** Find bottlenecks with a profiler (`dotnet-trace`/`dotnet-counters` for the sidecar, browser Performance panel for the webview) before changing code for speed; cite the profile in the change.
- **Benchmark Critical Paths:** Run `pnpm bench` when a change touches a §14 critical path (startup, typing/completion, execute → first batch, grid scroll, cancel, metadata, compare, import, refactor). A regression over 20 % vs the previous `bench/results/` run fails the change.
- **No Unsupported Claims:** Never call code or results "fast", "lightweight", "optimized", "efficient", or similar without the measured number that backs it (metric, before/after, fixture). Same for comments, commit messages and PR descriptions.
- **Resource Hygiene:** Every `Session` and `Execution` is `IAsyncDisposable`. Always wire `CancellationToken` through every async DB and IPC call.
- **No Unbounded Waits:** Every await on something outside the process (database, data plane, webview, SSH) carries the caller's token or a time limit, so Cancel and shutdown always end it. `CancellationToken.None` / `default(CancellationToken)` only where something else bounds the wait, listed with that bound in `ArchitectureRulesTests` (the test fails on any new use). Never hold a lock across a driver call (`Close()` included: Npgsql's blocks while it drains the command). Cancel must end every run (§7 step 4): a new long-running path joins the escalation and gets a `CancelRecoveryTests` case.
- **Design and Test Failure Recovery:** Every feature defines what happens when its dependencies fail — DB connection drop, query timeout/cancel (including a server that never acknowledges the cancel), sidecar crash or restart (§4: restart once, replay profiles, tell the user lost transactions), data plane with no reader or not draining (§5), SSH tunnel loss, malformed or partial import, concurrent edit (row-version conflict). Recovery must leave no orphaned process, pinned connection, half-written file or silent data loss, and must surface a clear user-facing message. Each failure path a change introduces gets a test (unit with a faulted fake, or integration by killing the connection/fixture).

## 5. Solo-Dev Execution & Verification Workflow
1. **Work in Vertical Slices:** Implement Contract → Sidecar + xUnit tests → UI + Vitest tests. Keep diffs focused and reviewable.
2. **Mandatory Tests — testing is non-negotiable:**
   - **Unit tests for business logic:** Vitest (`__tests__/*.test.ts` beside code) for UI/TS, xUnit in `sidecar/Querithm.Tests` for .NET.
   - **Integration tests for important boundaries:** in `sidecar/Querithm.Tests/Integration/` (gated by `QUERITHM_IT=1`) for every boundary a change touches — provider ↔ real engine, session/pool, RPC ↔ sidecar, Arrow data plane. File engines (SQLite, later DuckDB) need no Docker: their integration tests live in `sidecar/Querithm.Tests/FileEngines/`, use temp-dir files, and run in plain `pnpm test` (never behind `QUERITHM_IT`).
   - **End-to-end tests for critical workflows:** via `node scripts/bench/smoke-ui.mjs out/` for connect → execute → results, grid edit, import, and similar; extend it when such a workflow changes.
   - New behavior gets cases for **each** active engine; bug fixes require a regression test that fails before the fix.
3. **Run the Gate Before Claiming Done:**
   - Run `pnpm test` (runs both `vitest run` and `dotnet test sidecar`).
   - Run `QUERITHM_IT=1 dotnet test sidecar` if the Docker fixture is up and providers/sessions were touched.
   - Run `node scripts/bench/smoke-ui.mjs out/` if a critical workflow was touched.
   - Run `pnpm bench` if a §14 critical path was touched.
   - Run `cargo audit` (in `src-tauri`) and `dotnet list sidecar package --vulnerable --include-transitive` if dependencies changed.
   - Before calling the task done, verify each relevant area: performance targets (§4, bench results), dependency risks (audit output), security controls (Security suite), and failure recovery (failure-path tests). State in the summary which were checked and which didn't apply.
   - Every test must pass before calling the task finished; never claim completion on a red run — fix failures or report the exact output.
   - **Never claim tests passed without running them in this session.** Report which suites ran; name any that were skipped (e.g. Docker fixture not up) and why, instead of implying they passed.
4. **Keep Spec in Sync:** If implementation requires deviating from `docs/ARCHITECTURE.md`, document the deviation and rationale in `docs/ARCHITECTURE.md §16` in the same change.

## Useful Commands
- Test all: `pnpm test`
- Integration tests (Docker): `QUERITHM_IT=1 dotnet test sidecar`
- Regenerate IPC contract: `pnpm gen:contract`
- Browser dev mode: `pnpm dev:bridge` + `pnpm dev:web`
- Benchmarks & UI smoke: `pnpm bench` | `node scripts/bench/smoke-ui.mjs out/`
- A tab that hangs or a Cancel that does nothing: `docs/diagnosing-hangs.md` (logs, `dotnet-dump` + `dumpasync`)

# Querithm Roadmap — Phase Plans

Baseline (done): §15 milestones 1–7 and the Phase 8 depth items M1–M7 (grid editing, ERD, import, SSH,
schema compare, data compare, find usages/rename), plus history FTS, Excel export, ad-hoc query
parameters (`collectParameters` in `src/app/actions.ts`), in-app updates and workspace restore.
Active engines: **SQL Server** (`Engine.Mssql`) and **PostgreSQL** (`Engine.Pg`).

Implement the phases **in order**, with one exception: Phase D's initial milestone (D1–D3) comes before E;
D4–D7 are later milestones that may land after E and F (see "Why this order"). Each phase is a set of
vertical slices: contract → sidecar + xUnit → UI + Vitest → integration/smoke. A slice is mergeable alone;
a phase is done when all its slices are.

| Phase | File | Focus | New deps | Size |
|---|---|---|---|---|
| A | [PHASE-A-PARITY-POLISH.md](PHASE-A-PARITY-POLISH.md) | Close known gaps between engines; snippets; Windows/Linux gates | none | S–M |
| B | [PHASE-B-SERVER-MONITOR.md](PHASE-B-SERVER-MONITOR.md) | Activity monitor, blocking tree, guarded kill, deadlocks, top queries (Query Store / alerts deferred) | none | L |
| C | [PHASE-C-CHARTS-NOTEBOOKS.md](PHASE-C-CHARTS-NOTEBOOKS.md) | Result charts, `.sqlbook` notebooks, rich cell viewers | `uplot` | M–L |
| D | [PHASE-D-ENGINE-EXPANSION.md](PHASE-D-ENGINE-EXPANSION.md) | D0 groundwork; D1–D3 SQLite → MySQL/MariaDB → DuckDB (initial milestone); D4–D7 Oracle → ClickHouse → CockroachDB → TiDB (later milestones) | `MySqlConnector`, `DuckDB.NET.Data.Full` (needs Apache.Arrow ≥ 23); gated: `Oracle.ManagedDataAccess.Core`, `ClickHouse.Driver`. CockroachDB/TiDB reuse Npgsql/MySqlConnector | XL |
| E | [PHASE-E-AI-COPILOT.md](PHASE-E-AI-COPILOT.md) | Local-first AI: NL→SQL, plan explain, scrubber | none (HttpClient) | L |
| F | [PHASE-F-DEVOPS-CLI.md](PHASE-F-DEVOPS-CLI.md) | `querithm-cli` drift checks, team snippet folders | none | M |

## Why this order

- **Engine-neutral features before new engines.** CLAUDE.md §2 ("Every Engine Counts") means every
  feature built after an engine lands must be built for that engine too. Phases A–C ship on two engines;
  doing them after D would mean building them for up to nine engine families (ten products, counting
  MariaDB separately).
- **A first:** small, closes documented gaps (§9 PL/pgSQL tier 1, §16 trigger gap, §15 Definition of
  done for Windows benches).
- **E after D1–D3:** the AI context builder reads the snapshot of every active engine, so it is written
  for the initial Phase D engine set. Each later engine (D4–D7) extends the context builder and scrubber
  as part of its own cross-cutting checklist.
- **F last:** the CLI reuses compare code that must already work for all engines; D4–D7 engines that
  land after F add their CLI support (or a capability refusal) in their own sub-phase.

## Naming vs CLAUDE.md

CLAUDE.md calls the engine work "Phase 1 engines" and the cloud LLM work "Phase 2". In this roadmap
they are **Phase D** and **Phase E**. Update CLAUDE.md wording when Phase D starts: its "Phase 1 engines"
list (`Sqlite`, `DuckDb`, `MySql`) becomes the seven Phase D families, and an engine joins the active list
when it is exposed in the profile picker (Phase D "Merge model").

## Per-slice checklist (copy into each PR description)

- [ ] DTOs in `sidecar/Querithm.Contract`, methods on `IQuerithmRpc` (`Rpc.cs`), `pnpm gen:contract` run
- [ ] Sidecar validates every new RPC parameter (shape/size/range) at the boundary
- [ ] No regex over SQL; tokens/AST only
- [ ] Works on every active engine, or degrades with a visible reason + a test for the degradation
- [ ] xUnit unit tests; integration tests in `Integration/` (`QUERITHM_IT=1`) per engine
- [ ] Vitest tests beside UI code (`__tests__/`)
- [ ] Failure paths defined and tested (connection drop, cancel, sidecar restart, malformed input)
- [ ] New §13 rule → test in `sidecar/Querithm.Tests/Security/` written first
- [ ] Perf-sensitive → §14 row (next free number is **35**; check `scripts/bench/run.mjs` too), `pnpm bench` run, ≤ 20 % regression
- [ ] New library → §2 row, `Directory.Packages.props` pin, `THIRD-PARTY-NOTICES.md`, trimming verified,
      `dotnet list sidecar package --vulnerable --include-transitive` / `cargo audit` clean
- [ ] Deviation from the spec → §16 row in the same change
- [ ] `store.db` change → bump `PRAGMA user_version` with a forward migration in `LocalStore.cs` + test
- [ ] Release note line in `docs/release-notes/vX.Y.Z.md`
- [ ] Gate: `pnpm test`; `QUERITHM_IT=1 dotnet test sidecar` if providers/sessions touched;
      `node scripts/bench/smoke-ui.mjs out/` if a critical workflow touched

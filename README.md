# Querithm

<p align="center"><img src="docs/brand/querithm-wordmark.svg" alt="Querithm — Database &amp; SQL Development IDE" width="480"></p>

A cross-platform SQL IDE for **SQL Server**, **PostgreSQL**, **MySQL / MariaDB**, **SQLite** and **DuckDB**: Tauri v2 shell,
.NET 10 engine (ScriptDom, Microsoft.Data.SqlClient, Npgsql, MySqlConnector, Microsoft.Data.Sqlite, DuckDB.NET),
React + Monaco + Glide Data Grid, Apache Arrow over IPC.
The architecture and every decision live in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

It runs fully offline: fonts, editor, grid and plan renderer are bundled; the only network traffic is to your
databases (and Microsoft Entra ID when you choose that sign-in).

## What it does

- Engines: SQL Server, PostgreSQL and MySQL / MariaDB servers; SQLite and DuckDB files (read-only until you allow writes)
  or in-memory databases. A feature an engine lacks stays in place and says why.
- Multi-tab SQL editor (Monaco): T-SQL and PostgreSQL highlighting, folding from the AST, diagnostics, formatting.
- Two-tier autocomplete: a tolerant tokenizer answers in ~1–3 ms from the cached schema; ScriptDom refines
  (CTE/derived-table columns, temp tables, table variables, OUTPUT, MERGE, EXEC parameters, signatures).
- Many simultaneous connections; per-tab sessions; SQL Server database switch in one roundtrip.
- Streaming results (Arrow, 10k-row batches) in a canvas grid: 1M rows, sort, filter, copy, CSV/TSV/JSON export.
- Stored procedures: Modify (CREATE OR ALTER), Apply with server-side change detection and a diff, Compare,
  Execute with a parameter form, Script as CREATE/ALTER/DROP/EXEC, dependencies.
- Plans: estimated/actual (SQL Server ShowPlanXML, PostgreSQL EXPLAIN [ANALYZE]) as a DAG with cost badges,
  warnings and missing-index suggestions; STATISTICS IO/TIME tables.
- SQL Server **Trace** (Extended Events timeline). PostgreSQL **step debugger** (pldebugger: breakpoints,
  step over/into, variables, call stack).
- Safety: environments (Dev/Staging/Prod) with colors; first-run-on-Prod and destructive-statement (AST) guards
  enforced by the engine; transactions never silently rolled back; secrets only in the OS keyring.

## Develop

Prerequisites: Node 24 + pnpm, .NET SDK 10, Rust (stable). Linux also needs
`libwebkit2gtk-4.1-dev libgtk-3-dev libsoup-3.0-dev librsvg2-dev libayatana-appindicator3-dev libxdo-dev libssl-dev pkg-config`.

```bash
pnpm install
pnpm dev                 # builds the sidecar (debug) and starts the app with hot reload
pnpm test                # vitest (webview) + dotnet test (sidecar)
pnpm build               # release sidecar (trimmed, self-contained) + THIRD-PARTY-NOTICES + installers
```

Without the native toolchain you can still run the real engine and UI in a browser:

```bash
pnpm dev:bridge          # terminal 1: sidecar behind a local WebSocket (dev only)
pnpm dev:web             # terminal 2: http://127.0.0.1:1420
```

### Test databases and benchmarks

```bash
docker compose -f scripts/bench/docker/compose.yml up -d --build   # SQL Server 2022 + PostgreSQL 16 (pldebugger)
node scripts/bench/gen-schema.mjs --apply                           # 600 tables, 400 procs, 12k-line proc, 1M rows
node scripts/fixture/load-testdb.mjs                                # hand-written PostgreSQL test DB (querithm_test)
QUERITHM_IT=1 dotnet test sidecar                                   # integration tests against the fixture
pnpm bench                                                          # §14 metrics → bench/results/<date>-<os>.json
node scripts/bench/smoke-ui.mjs out/                                # headless-Chrome UI smoke test with screenshots
QUERITHM_BENCH=1 src-tauri/target/release/querithm                  # in-app bench inside the native webview
```

### Offline builds

Run `pnpm offline:prepare` once while online (pnpm store, NuGet packages for every RID, Rust crates, docker
images). Afterwards `QUERITHM_OFFLINE=1 pnpm build` builds with no network. Tauri's AppImage bundler downloads
`linuxdeploy` on first use; build `--bundles deb,rpm` offline or do one online build to cache it.

## Layout

```
src-tauri/   Rust shell: window, keyring, sidecar supervision, IPC relay (no SQL)
src/         React webview: editor, completion (tier 1), results, explorer, plans, trace, debugger
sidecar/     .NET 10 engine: contract, host, core, providers (SQL Server, PostgreSQL, MySQL, SQLite, DuckDB), intelligence
scripts/     build, contract generation, dev bridge, bench, notices
docs/        ARCHITECTURE.md (the spec)
```

# S12 — Monitor, remaining features, feature matrix

**Goal:** every cross-cutting feature has an Oracle answer — implemented or degraded with a visible reason and a test.
STATUS.md feature matrix has no **gap**.

**Prerequisites:** S07–S11. Owner answer to O4 or take the default.

## Scope

1. **Activity monitor (`OracleActivity : IActivityProvider`)** where `V$SESSION` / `V$LOCK` / `V$SQL` are granted:
   sessions, blocking tree (`BLOCKING_SESSION`), top queries; guarded kill via `ALTER SYSTEM KILL SESSION 'sid,serial#'`
   (needs `ALTER SYSTEM` → otherwise reason). Without grants: monitor degraded with reason. Bench 28–32 if implemented.
2. **Trace / debugger:** degraded with reason ("Querithm does not implement tracing/debugging for Oracle").
3. **Walk the cross-cutting list** (PHASE-D-ENGINE-EXPANSION.md "Cross-cutting per engine") and fix or degrade each:
   export (CSV/TSV/JSON/XLSX of Oracle types — exact decimals, TZ timestamps), history badge + re-open, charts,
   notebooks, ERD (FKs, quoted labels), snippets dialect filter (Oracle snippets: anonymous block, cursor loop,
   package skeleton), diagnostics (none server-side → "as PostgreSQL"), AI context / scrubber and CLI only if those
   phases exist on `dev` by now.
4. **Capability refusals enforced at the RPC boundary** for every degraded feature (not only hidden in the UI).

## Tests
`Integration/OracleMonitorTests.cs`, `OracleActivityRulesTests`; `Features_Oracle_lacks_say_why_and_the_sidecar_refuses_them`;
Vitest `export.engines`, `chartEngines`, `outputEngines`, `erdEngines`, menus with Oracle cases.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar` · `node scripts/bench/smoke-ui.mjs out/`.

## Done when
STATUS.md feature matrix: every row done or degraded, each with a test name.

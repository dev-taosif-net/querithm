# D4 Oracle — status

Update at the end of every session. Statuses: *not started* · *in progress* · *done* (code + tests passed in that
session) · *partial* (what is left is in "Open").

Last updated: 2026-11-10 — S01 partial (no Docker on the session's machine).

## Sessions

| # | Status | Evidence (test names / commands run) | Open / stopping point |
|---|---|---|---|
| S01 | partial | Driver gate passed (license, vulnerable scan clean, transitive review, trimmed DLL byte-identical to the package, trimmed `--self-test` → `oracle ok`). `OracleSkeletonTests`, `EngineRegistryTests` (Oracle hidden, no paste detection), `engines.test.ts` / quoting / rename / profile-search Oracle rows, `fixture-secrets.test.ts`. `pnpm test`: sidecar 3584 passed / 269 skipped; `DuckDbSecurityTests.Spill_files_land_under_app_data` is flaky on `HEAD` too (fails at its 256 MB cap). Vitest: 50 `localStorage` failures in App / persistence / uiScale tests, identical on clean `HEAD` (Node 26). Trim baseline 29 → 30: ODP.NET's IL2104, unavoidable (rooted, unmodifiable) | Not run (no Docker): fixture start (`docker compose -p querithm-fixture -f scripts/bench/docker/compose.yml --profile oracle up -d --wait oracle`) and `QUERITHM_IT=1 dotnet test sidecar --filter Oracle`. Unverified until then: `ENABLE_TCPS` on 2484 and its client-wallet path (S03 needs the CA), setup script, healthcheck. Size measured on the sidecar only, not a full installer |
| S02 | not started | — | — |
| S03 | not started | — | — |
| S04 | not started | — | — |
| S05 | not started | — | — |
| S06 | not started | — | — |
| S07 | not started | — | — |
| S08 | not started | — | — |
| S09 | not started | — | — |
| S10 | not started | — | — |
| S11 | not started | — | — |
| S12 | not started | — | — |
| S13 | not started | — | — |

## Decisions taken (owner answers to README "Open decisions")

| # | Answer | Date |
|---|---|---|
| O2 | Default taken: TCPS with the image's self-signed certificate generated at first start (`ENABLE_TCPS`), TLS tests required | 2026-11-10 |

## Pinned versions

| What | Version / digest | Session |
|---|---|---|
| `Oracle.ManagedDataAccess.Core` | 23.26.400 (published 2026-10-20; assembly 23.1.0.0) | S01 |
| Oracle Free container | `container-registry.oracle.com/database/free:23.26.3.0@sha256:f988b0c04c4c386cd306a2a914c0d7a9702d83acc31b064a28ad8eb6278a8fba` (multi-arch index) | S01 |

## Driver gate (S01)

- Transitive: new to the Host only `System.DirectoryServices.Protocols` 8.0.2 (MIT, Microsoft; LDAP client, used only for LDAP naming, which Querithm does not configure). PerformanceCounter, ConfigurationManager, EventLog, ProtectedData and Pkcs (pinned 10.0.13) were already shipped; System.Text.Json / Immutable / Memory / Asn1 come from the framework.
- Size (metric 17, win-x64 trimmed single-file sidecar): 108.6 → 115.5 MB (+6.9 MB); gzip -9 39.9 → 43.1 MB (+3.2 MB).

## Feature matrix (filled from S06 on; copied into PHASE-D-IMPLEMENTATION-STATUS.md in S13)

Status per row: **done** / **degraded** (visible reason + test) / **gap**.

| Feature | Oracle | Test |
|---|---|---|
| Editor highlighting, folding | gap | — |
| Diagnostics | gap | — |
| Signature help | gap | — |
| Formatting | gap | — |
| Completion tier 1 / tier 2 | gap | — |
| Execute / cancel / transactions | gap | — |
| Ad-hoc parameters | gap | — |
| Grid + grid editing | gap | — |
| Export, history, charts, notebooks | gap | — |
| Explorer, `meta.rowCounts` | gap | — |
| Scripting, Modify / Apply / Execute | gap | — |
| Dependencies | gap | — |
| Plans (estimated / actual) | gap | — |
| Import | gap | — |
| ERD | gap | — |
| Schema / data compare | gap | — |
| Find usages / rename | gap | — |
| SSH | gap | — |
| Safety guards (destructive, Prod) | gap | — |
| Monitor (Phase B) | gap | — |
| Snippets dialect filter | gap | — |
| Packages (browse / script) | gap | — |
| Trace / debugger | gap | — |

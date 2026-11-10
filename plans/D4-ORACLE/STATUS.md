# D4 Oracle — status

Update at the end of every session. Statuses: *not started* · *in progress* · *done* (code + tests passed in that
session) · *partial* (what is left is in "Open").

Last updated: 2026-11-10 — S01 done (fixture verified in S03, after fixing its start scripts); S02 partial (server-side splitter checks move to S04); S03 done.

## Sessions

| # | Status | Evidence (test names / commands run) | Open / stopping point |
|---|---|---|---|
| S01 | done | Driver gate passed (license, vulnerable scan clean, transitive review, trimmed DLL byte-identical to the package, trimmed `--self-test` → `oracle ok`). `OracleSkeletonTests`, `EngineRegistryTests` (Oracle hidden, no paste detection), `engines.test.ts` / quoting / rename / profile-search Oracle rows, `fixture-secrets.test.ts`. `pnpm test`: sidecar 3584 passed / 269 skipped; `DuckDbSecurityTests.Spill_files_land_under_app_data` is flaky on `HEAD` too (fails at its 256 MB cap). Vitest: 50 `localStorage` failures in App / persistence / uiScale tests, identical on clean `HEAD` (Node 26). Trim baseline 29 → 30: ODP.NET's IL2104, unavoidable (rooted, unmodifiable) | Fixture verified in S03: `docker compose … --profile oracle up -d --wait oracle` healthy, TCPS on 2484 (CN=localhost), both users; `QUERITHM_IT=1` Oracle suite green. The S01 fixture had never run: the Free image ships a created database, so `ENABLE_TCPS` and `scripts/setup` never ran — moved to idempotent `oracle/startup/00-tcps.sh` + `01-users.sh` (§16). Size measured on the sidecar only, not a full installer |
| S02 | partial | `OracleIntelligenceTests` (tokenizer: every `q'` delimiter, `nq'`, hints, binds vs `:=` / `:NEW`, db links, `1..10`; splitter: mixed SQL + PL/SQL, package spec + body, compound trigger, `WITH FUNCTION`, type spec vs body, `/` in string/comment, missing final `/`, `END;` kept, `;` stripped, client commands, folding; classifier: each destructive form, MERGE, dynamic SQL in blocks, DDL, transactions, `CURRENT_SCHEMA`; keywords; quoting), `Security/OracleSecurityTests` (written first, 20 red on the stub, then green: Prod destructive block without `confirmedDestructive`, client-command refusals with every flag, `IDENTIFIED BY` redaction), `OracleSkeletonTests` updated. `dotnet test sidecar`: 3734 passed / 269 skipped / 1 failed (`DuckDbSecurityTests.Spill_files_land_under_app_data`, the known flaky test). Vitest: the same 50 `localStorage` failures in App / persistence / uiScale (Node 26 baseline) | (1) `OracleKeywords` not regenerated from `V$RESERVED_WORDS` on the fixture (no Docker) — list taken from the SQL reference. (2) Done-when "splitter output executes unchanged" is checked in S03/S04 on the server, notably `CREATE TYPE … AS OBJECT (…)` sent without its `;`. (3) Deviation from the plan: Oracle has no `""` escape in quoted names, so `OracleIdentifiers.Quote` refuses `"` / NUL instead of doubling. (4) `SET SERVEROUTPUT` toggles are reported (`ServerOutputToggles`); applying them is S04. `SIZE` / `FORMAT` options are accepted and ignored |
| S03 | done | `OracleProviderTests` (builder: TCPS + DN match default on 2484, TCP downgrade on 1521, Verify CA, refused modes and trust-cert, SID option, TNS_ADMIN pinned to an empty app-data folder whatever `TNS_ADMIN` says, a dropped-in `tnsnames.ora` refused, profile extras ignored, descriptor-injecting host / service / user refused, CA file → PEM trust wallet; parse: Easy Connect forms, descriptors, aliases / lists / malformed refused, extras named not applied; ORA position → line/column, help line dropped, innermost listener code; identity format), `Security/OracleSecurityTests` (+ TLS default, secret keywords masked in logs, built string scrubbed, pasted proxy / wallet passwords dropped, SQL secret names redacted), `SecretAtRestMatrixTests` Oracle rows, `CancelRecoveryTests.Oracle_whose_close_waits_…`, `OracleSkeletonTests` updated. `QUERITHM_IT=1` (`--filter Oracle`, 270 passed): `OracleProviderIntegrationTests` (TCPS with fixture CA → rows, plain TCP, wrong password / untrusted CA / unknown service readable, DN mismatch vs Verify CA, ORA-00904 line 2 column 3, begin→insert→rollback and commit checked from an independent connection, begun-but-unchanged stays open, DDL ends the transaction and commits, typed COMMIT / ROLLBACK end it, savepoints + read-only, cancel `DBMS_SESSION.SLEEP(30)` then same SID reused, command timeout, identity with / without `V$SESSION`, single service), `CancelEscalationIntegrationTests.Oracle_is_never_closed_mid_command_…`. Measured: `Close()` under a running block waits for it (28.5 s, 3/3) → `CloseAbortsCommand` false; break latency 4 ms (busy loop) / 4.5–5.9 s (`DBMS_SESSION.SLEEP`). Gate: `dotnet test sidecar` 3818 passed / 288 skipped / 0 failed; Vitest 4951 passed / 50 failed — the known Node 26 `localStorage` baseline (App / persistence / uiScale), no TS changed. Not run: smoke-ui (no UI change), `pnpm bench` (Oracle not in the bench, no Core path changed), audits (no dependency change) | Not in scope / later: SSH (`TunnelTarget` refuses until S07); types beyond text, `AddParameter`, DBMS_OUTPUT (S04); monitor panes and the blocking banner (identity only, S12); wallet / OS / token sign-in deferred. S02 open item (2) — splitter output executed on the server — moves to S04 |
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
| O5 | Default taken: `SET SERVEROUTPUT ON/OFF` toggles DBMS_OUTPUT; every other SQL\*Plus command (and `&substitution`) refused with a reason | 2026-11-10 |
| O1 | Default taken: host `TNS_ADMIN` / `sqlnet.ora` / `tnsnames.ora` ignored; documented in ARCHITECTURE §16 ("Oracle network configuration") | 2026-11-10 |
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

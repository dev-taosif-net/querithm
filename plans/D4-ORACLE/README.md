# D4 — Oracle: session-by-session implementation plan

Source of truth for *what* must be built: [PHASE-D-ENGINE-EXPANSION.md § D4](../PHASE-D-ENGINE-EXPANSION.md#d4--oracle),
the per-engine checklist and the cross-cutting list in the same file. This folder only says *in which order*
and *in which session*. Progress goes in [STATUS.md](STATUS.md).

Branch: `feature/d4-oracle` (from `dev`). Template engine: **D2 MySQL** — mirror its file layout
(`sidecar/Querithm.Providers.MySql/*`, `sidecar/Querithm.Intelligence.MySql/*`, `sidecar/Querithm.Tests/MySql*`,
`sidecar/Querithm.Tests/Integration/MySql*`) unless Oracle forces a difference.

## How to run a session

1. Start a fresh Claude session and say: `Read plans/D4-ORACLE/README.md, STATUS.md and sessions/Sxx-*.md, then implement Sxx.`
2. The session reads CLAUDE.md + `docs/ARCHITECTURE.md` (§2, §4, §5, §12, §13, §15, §16) first.
3. One session = one vertical slice = one reviewable commit (or a few). Do not start the next session's scope.
4. Finish with the gate listed in the session file, update [STATUS.md](STATUS.md) (status, evidence = test names,
   open items, decisions), and list which gates ran and which were skipped and why.
5. If a session runs out of room, record the exact stopping point in STATUS.md so the next session resumes there.

## Merge model

Oracle stays a **hidden engine** (dev-only setting, not in the picker, not in connection-string auto-detection,
not in the README) until S13 passes the release gates. While hidden it is *not* an active engine, so other work on
`dev` does not have to cover it yet — but every Oracle slice must keep the existing engines green.

## Sessions

| # | Session | Milestone | Needs Oracle container |
|---|---|---|---|
| S01 | [Driver gate, fixture, hidden engine skeleton](sessions/S01-driver-fixture-skeleton.md) | D4a | yes |
| S02 | [Intelligence: tokenizer, PL/SQL splitter, classifier](sessions/S02-intelligence-core.md) | D4a | no |
| S03 | [Provider core: connect, TLS, auth, transactions, cancel](sessions/S03-provider-core.md) | D4a | yes |
| S04 | [Execute path: Arrow types, LOBs, DBMS_OUTPUT, parameters](sessions/S04-execute-types-messages.md) | D4a | yes |
| S05 | [Metadata, markers, explorer, row counts](sessions/S05-metadata.md) | D4a | yes |
| S06 | [Webview: language, profile dialog, tier-1 completion, smoke](sessions/S06-webview.md) | D4a | for smoke only |
| S07 | [SSH and failure recovery](sessions/S07-ssh-failure-recovery.md) | D4b | yes |
| S08 | [Package metadata contract](sessions/S08-package-contract.md) | D4b | yes |
| S09 | [Plans, tier-2 completion, signature help](sessions/S09-plans-tier2.md) | D4b | yes |
| S10 | [Grid editing and import](sessions/S10-edit-import.md) | D4b | yes |
| S11 | [Scripting, dependencies, compare, find usages / rename](sessions/S11-script-compare-refactor.md) | D4b | yes |
| S12 | [Monitor, remaining features, feature matrix](sessions/S12-monitor-feature-matrix.md) | D4b | yes |
| S13 | [Release gates, bench, spec updates, un-hide decision](sessions/S13-release-gates.md) | D4b | yes |

**D4a (S01–S06):** connect → execute → results, metadata and cancel work behind the hidden flag.
**D4b (S07–S13):** everything the feature matrix needs before Oracle can enter the picker.

Rough size: 13 sessions. S08 can be dropped (≈ 1 session saved) if the owner decides packages are marked
unsupported in the feature matrix for the first release — see "Open decisions".

## Open decisions (owner) — answer before the session that needs them

| # | Decision | Default if unanswered | Needed by |
|---|---|---|---|
| O1 | Honour host `TNS_ADMIN` (`sqlnet.ora`, `tnsnames.ora`)? | Ignored, documented in §16 | S03 |
| O2 | TCPS in the fixture: self-signed CA generated at fixture start, or plain TCP only with TLS tests skipped? | Generated CA at fixture start; TLS tests required | S01 |
| O3 | Package browsing in the first release (S08), or marked unsupported? | Implement S08 | S08 |
| O4 | Activity monitor for Oracle (needs `V$SESSION` grants), or degraded with reason? | Implement where permitted, degrade otherwise | S12 |
| O5 | `SET SERVEROUTPUT ON` and other SQL\*Plus client commands: emulate, or refuse with a reason? | `SET SERVEROUTPUT ON/OFF` toggles DBMS_OUTPUT; every other client command refused with a reason | S02 / S04 |
| O6 | Intel macOS (unsupported by the driver): hide the engine there, or show it disabled with the reason? | Shown disabled with the reason | S06 |

## Oracle facts every session must respect

- Empty string `''` is `NULL` in `VARCHAR2`/`CHAR`. Equality-based conflict checks and compares must use
  `IS NULL`-aware predicates.
- DDL commits **before and after** the statement, also when it fails after parsing. Transaction state must reset.
- A plain SQL statement sent through the driver must **not** end with `;` (ORA-00933/ORA-00911); a PL/SQL block must
  keep its final `END;`. The splitter owns this, not string trimming.
- `/` alone on a line ends a PL/SQL unit (SQL\*Plus convention); inside a PL/SQL body `;` does not end the statement.
- `:name` is a bind variable, but `:NEW`/`:OLD` inside trigger bodies are not ad-hoc parameters.
- Unquoted identifiers fold to **upper** case; quoted ones are exact (D0e identifier identity).
- Schema selection (`ALTER SESSION SET CURRENT_SCHEMA`) is not database switching; the service is the connection target.
- `SYS_CONTEXT('USERENV', 'SID')` needs no extra grant; `SERIAL#`/`LOGON_TIME` need `V$SESSION` access.
- Oracle Free container: slow first start (minutes) and ~2 GB RAM. Integration runs are expensive — batch them.

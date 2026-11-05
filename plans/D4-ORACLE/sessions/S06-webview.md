# S06 — Webview: language, profile dialog, tier-1 completion, smoke (end of D4a)

**Goal:** a developer can enable the hidden engine, create an Oracle profile, write PL/SQL with highlighting, run it and
see results — the D4a milestone.

**Prerequisites:** S02–S05. Owner answer to O6 or take the default.

## Scope

1. **Monaco language** (`src/editor/languages.ts`): Oracle tokens matching `OracleTokenizer` (q-quotes, hints, binds,
   `/` lines); folding from the sidecar splitter. Shared single Monaco instance — no new editor instances.
2. **Formatting:** sql-formatter `plsql` dialect; test that `q'` literals and `/` lines survive formatting.
3. **Tier-1 completion:** keywords from `OracleKeywords`, snapshot objects, upper-case-fold quoting via the D0e
   `quoteIdentifier` (`quoteIdentifier.test.ts`).
4. **Engine decisions** in `src/engines.ts`: every exhaustive switch has an explicit Oracle answer (no default branch).
5. **Profile dialog:** host, port, service name, optional SID (explicit), TCPS default + warned downgrade, password
   only via keyring; wallet / OS auth shown unavailable with reason. Intel macOS: engine shown disabled with the
   driver's platform reason (O6).
6. **Messages panel:** DBMS_OUTPUT toggle per tab; compile errors with line/column jump.
7. **Ad-hoc parameter UI** for `:name`.
8. **UI smoke** (`scripts/bench/smoke-ui.mjs`): hidden-engine flag on → create Oracle profile → run a PL/SQL block with
   DBMS_OUTPUT → see result + message → cancel a sleep.

## Tests
Vitest: `engines.test.ts`, `monaco.test.ts` / `lexer.test.ts` Oracle cases, profile dialog validation, DBMS_OUTPUT
toggle, parameter detection parity with the sidecar. Smoke-ui Oracle workflow (fixture up).

## Gate
`pnpm test` · `node scripts/bench/smoke-ui.mjs out/` · `pnpm bench` metrics 4 and 3 with an Oracle dialect fixture.

## Done when (D4a milestone)
Connect → execute → results, PL/SQL editing, messages, cancel and explorer work for a hidden Oracle profile. Update
STATUS.md and pause for the owner to try it before D4b.

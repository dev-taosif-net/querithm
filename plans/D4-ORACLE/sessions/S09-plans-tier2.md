# S09 — Plans, tier-2 completion, signature help

**Goal:** estimated and actual plans as `PlanTree`; token-scoped tier-2 completion and signature help on the session
snapshot.

**Prerequisites:** S05 (S08 if package members should show in signature help — otherwise standalone routines only).

## Scope

1. **Estimated plan** (`OraclePlans : IPlanProvider`): `EXPLAIN PLAN SET STATEMENT_ID = '<guid>' FOR …` then read
   `PLAN_TABLE` (id / parent_id → `PlanNode.Children`, operation, options, object, cost, cardinality, bytes,
   predicates). Verify on the fixture whether `PLAN_TABLE` rows are session-private (global temporary) and whether the
   statement affects the user's open transaction; always delete our rows; if `PLAN_TABLE` is missing or not writable →
   degrade with reason.
2. **Actual plan:** run the statement with `/*+ GATHER_PLAN_STATISTICS */` then `DBMS_XPLAN.DISPLAY_CURSOR(NULL, NULL,
   'ALLSTATS LAST')` on the same session; needs `V$SQL_PLAN*` access → otherwise degrade with reason. Executes SQL →
   guarded exactly like `query.execute` (destructive + ProdWrite), enforced at the RPC boundary.
3. **Plan format:** new `Format` value routed in `plan.parse` (`PlanParsing.cs`) and the UI (`openAsPlan.ts`,
   `CellViewerPanel.tsx`).
4. **Tier-2 completion:** token-scoped engine under Oracle rules — upper-case folding, quoted names, schema-qualified
   names, synonyms, `CURRENT_SCHEMA` context from S02, no completion inside strings/`q'`/comments, PL/SQL block scope
   (declared variables optional; record if skipped).
5. **Signature help:** standalone procedures/functions from `ALL_ARGUMENTS` (overloads), named-argument `=>` syntax;
   package members only if S08 landed with member data, otherwise reason.

## Tests
`OraclePlanTests` (canned PLAN_TABLE rows → tree), integration: estimated plan without leaving rows, actual plan with
and without grants, actual plan of a DELETE refused without confirmation, plan inside an open transaction leaves it
intact. Completion/signature cases in `OracleIntelligenceTests` + live snapshot test. Bench metric 5 / 27 for tier 2.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar --filter Oracle` · `pnpm bench` 5 / 27.

## Done when
Plans and tier-2 rows of the feature matrix are done or degraded with tests.

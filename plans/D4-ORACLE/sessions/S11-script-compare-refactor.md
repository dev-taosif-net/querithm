# S11 — Scripting, dependencies, compare, find usages / rename

**Goal:** scripting and Modify/Apply/Execute, dependencies, schema/data compare and token-based references for the
object kinds Oracle supports; everything else degrades with a reason.

**Prerequisites:** S05 (S08 for package scripting).

## Scope

1. **`OracleScripter : IObjectScripter`:** CREATE / CREATE OR REPLACE / DROP / SELECT / INSERT templates; Modify →
   Apply for views, procedures, functions, triggers, types (and packages if S08); output ends PL/SQL units with `/`.
   Execute procedure dialog with typed parameters (IN / OUT / IN OUT, ref cursors as result sets or reason).
2. **Dependencies:** `ALL_DEPENDENCIES` (real references), not text search.
3. **Schema compare (`ISchemaCompareDialect`):** tables (columns, defaults, identity, constraints, indexes), views,
   sequences, standalone routines and triggers by normalized definition; migration script respects implicit DDL
   commits (one statement per step, no wrapping transaction, ordered for FK dependencies). Round trip: compare →
   migrate → compare again identical. Unsupported kinds (packages members, object types with dependents, partitions,
   materialized views) → listed as "not compared" with reason.
4. **Data compare (`IDataCompareDialect`):** keys with proven uniqueness; Oracle and .NET equality / ordering matched
   (NLS_SORT = BINARY on the compare session; `''` = NULL; NUMBER exact); unsupported keys refused before a diff.
5. **`OracleReferenceFinder : IReferenceFinder`** over `ALL_SOURCE` / view text tokens; rename of tables, columns,
   views, standalone routines; quoted vs unquoted names; package members → reason.

## Tests
`Integration/OracleSchemaCompareTests.cs` (round trip), `OracleCompareTests.cs`, `OracleRefactorTests.cs`; unit
`OracleSchemaCompareRulesTests`, `OracleReferenceFinderTests`, `OracleDataCompareTests`. Smoke-ui compare / rename on an
Oracle session. Bench 21 / 22 / 24 for Oracle.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar --filter Oracle` · `node scripts/bench/smoke-ui.mjs out/` · `pnpm bench`.

## Done when
Scripting, Dependencies, Schema / data compare and Find usages / rename rows are done or degraded with tests.

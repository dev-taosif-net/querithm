# S10 — Grid editing and import

**Goal:** safe grid edits with optimistic conflict checks, and CSV/JSON/XLSX import through parameterized batches.

**Prerequisites:** S04, S05.

## Scope

1. **Result origins** (`ResultOrigins`, `ResultEditRefusal`): single-table SELECTs only, from tokens + snapshot;
   joins, views without a key, synonyms to remote objects (`@dblink`) → read-only with reason.
2. **`OracleDataEdit : IDataEditDialect`:**
   - Key: non-null PK or unique constraint on non-null columns. `ROWID` fallback only when the result contains it from
     the same table and it is not an IOT/partition-move risk → otherwise read-only with reason.
   - Conflict check: original-values compare with `IS NULL`-aware predicates (`''` = NULL); `ORA_ROWSCN` is block-level
     unless `ROWDEPENDENCIES` — do not rely on it. Exactly one row affected or the whole patch rolls back.
   - LOB, `LONG`, object types, virtual and identity-always columns: read-only per column with reason.
   - Values as parameters, identifiers via the quoting service.
3. **`OracleImport : IImportDialect` + loader:** parameterized batched INSERT (array binding as the first candidate —
   measure vs. multi-row `INSERT ALL`, metric 23); the three transaction modes (all-or-nothing, batches, join open
   transaction); rejects; type fit for NUMBER/DATE/TIMESTAMP; cancel mid-import leaves nothing in all-or-nothing mode.
   Native bulk copy (`OracleBulkCopy`) **not** used until transaction + cancellation behaviour is verified — record why.
4. **Prod guards:** edits and imports on Prod profiles go through `GuardReason.ProdWrite` in the sidecar.

## Tests
`Integration/OracleEditImportTests.cs`: PK edit, unique-key edit, nullable unique key read-only, `''`/NULL conflict,
concurrent edit conflict rolls back the patch, LOB column read-only, ROWID fallback, CSV import in each mode, PK
violation leaves nothing, cancel mid-import, Prod guard. Unit: `OracleDialectTests` SQL generation.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar --filter Oracle` · smoke-ui grid edit + import on Oracle · `pnpm bench`
20 / 23 for Oracle.

## Done when
Grid + grid editing and Import rows are done with tests.

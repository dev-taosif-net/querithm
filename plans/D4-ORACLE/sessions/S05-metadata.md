# S05 — Metadata, markers, explorer, row counts

**Goal:** `OracleMetadata : IMetadataProvider` builds the schema snapshot from permission-scoped dictionary views,
with one marker query per refresh (§8) and visible degradations when privileges are missing.

**Prerequisites:** S03. Packages are **not** in scope here (S08) — list them nowhere yet; record "packages: S08" in
the feature matrix.

## Scope

1. **Namespaces:** schemas (`ALL_USERS` that own objects, plus the current schema) are namespaces under the single
   service "database". Decide and document in §6 / §16: schema list = schemas the user can see objects in.
2. **Catalog** from `ALL_*` views (never `DBA_*` as a requirement): tables (`ALL_TABLES`, excluding recycle bin,
   `SECONDARY`, IOT overflow, nested and external tables marked), columns (`ALL_TAB_COLS` incl. identity, virtual,
   invisible, default), views, materialized views (as views with a flag or a refusal reason), indexes, constraints
   (PK/UK/FK/check, deferrable), sequences, synonyms (`ALL_SYNONYMS`, public + private), triggers (parent = table),
   standalone procedures/functions with arguments (`ALL_ARGUMENTS`), types.
3. **Markers:** one dictionary query per refresh computing a deterministic definition hash server-side
   (`STANDARD_HASH`) over columns/indexes/constraints/triggers per object; `LAST_DDL_TIME` alone is not trusted. Bench
   metric 10 (`10.oracle`).
4. **Definitions on demand** (never per refresh): `DBMS_METADATA.GET_DDL` with transform params set per session
   (no storage clauses, SQL terminator), fallback to `ALL_SOURCE` for PL/SQL when `GET_DDL` is not permitted.
5. **Row counts** (`meta.rowCounts`): `ALL_TABLES.NUM_ROWS` (statistics-based, estimated, with last-analyzed date
   in the label) — never `COUNT(*)`.
6. **Permission failures** (ORA-00942, ORA-01031) become visible degradations on that object kind, not a failed refresh.
7. **Invalid objects:** `STATUS = 'INVALID'` shown in the explorer.

## Tests
Integration with the least-privileged user and the catalog-role user: snapshot lists every object kind created by the
fixture script; case-distinct quoted names (`"Sales"` vs `SALES`); synonym to another schema's table; marker changes
after `ALTER TABLE ADD`, after `CREATE INDEX`, not after DML; definition retrieval with and without `GET_DDL`
permission; row counts before and after `DBMS_STATS.GATHER_TABLE_STATS`; missing privilege → degradation message.
Unit: snapshot assembly from canned rows.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar --filter Oracle` · `pnpm bench` metric 10 for Oracle.

## Done when
Explorer data for Oracle is complete except packages; refresh is one marker query; degradations are visible.

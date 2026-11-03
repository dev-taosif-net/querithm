# Phase A — Parity Gaps and Polish

Goal: remove the known differences between SQL Server and PostgreSQL support, add a local snippet
library, and close the §15 Definition-of-done gaps. No new dependencies.

Slices: **A1** triggers in the snapshot → **A2** PL/pgSQL tier-2 intelligence → **A3** snippet library →
**A4** Windows bench + Linux WebKitGTK gate.

---

## A1 — Triggers in the metadata snapshot (both engines)

**Why:** §16 "Find usages scope" lists SQL Server triggers as a known gap; the explorer shows no
triggers on either engine.

### Contract
- `sidecar/Querithm.Contract/Core.cs`: add `Trigger` to `ObjectKind`.
- `ObjectStamp` / snapshot object DTO (`Metadata.cs`): add `long? ParentObjectId` (table/view the trigger
  belongs to; null for SQL Server database DDL triggers, which stay out of scope).
- Run `pnpm gen:contract`.

### Store (`sidecar/Querithm.Core/Store/`)
- `Schema.sql`: `obj.kind` comment gains `'trigger'`; add column `parent_object_id INTEGER`.
- `LocalStore.cs`: migration `user_version 1 → 2` (`ALTER TABLE obj ADD COLUMN parent_object_id INTEGER`),
  then mark every `schema_snapshot` row `stale` so the next refresh fills triggers.
- `MetadataStore.cs`: read/write the new column.

### Providers
- `Querithm.Providers.SqlServer/SqlServerMetadata.cs` — `GetStampsAsync`: include `sys.triggers`
  (`parent_class = 1`), marker = `modify_date`; `GetDefinitionsAsync` already reads `sys.sql_modules`.
- `Querithm.Providers.Postgres/PostgresMetadata.cs` — include `pg_trigger` rows where `NOT tgisinternal`,
  object id = trigger oid, parent = `tgrelid`, marker = trigger function's `xmin`; definition =
  `pg_get_triggerdef(oid)`. The trigger *function* stays a `Func`.
- Scripting (`SqlServerScripter.cs`, Postgres scripter): Script as CREATE/DROP for `Trigger`.

### Intelligence / refactor
- `TSqlReferenceFinder.cs`: trigger bodies become searchable modules (they are in the definition cache now).
- `PgReferenceFinder.cs`: nothing new for bodies (trigger functions are already funcs), but a column
  rename must list triggers whose `UPDATE OF col` list names it (token scan of `pg_get_triggerdef`).

### UI
- `src/explorer/Explorer.tsx`: "Triggers" folder under each table/view (lazy, from snapshot).
- Context menu: Modify (MSSQL `CREATE OR ALTER TRIGGER`; PG opens the trigger function), Script as, Enable/Disable
  (generates SQL into a new tab — never executes directly).

### Tests
- xUnit: store migration v1→v2 keeps data and marks snapshots stale (`StoreTests.cs`).
- xUnit: `ReferenceFinderTests.cs` — a column referenced only inside a trigger is found (T-SQL).
- Integration (`SqlServerIntegrationTests.cs`, `PostgresIntegrationTests.cs`): create table + trigger,
  refresh, assert snapshot has the trigger with the right parent; incremental refresh picks up an
  `ALTER TRIGGER` / `CREATE OR REPLACE TRIGGER` (PG 14+; older PG: drop/create).
- `RefactorIntegrationTests.cs`: rename a column used in a trigger → preview includes the trigger.
- Vitest: explorer renders the Triggers folder from a snapshot fixture.

### Failure paths
- Login lacks `VIEW DEFINITION` on a trigger → definition null → explorer shows "definition not visible"
  (same as encrypted modules). Test it.
- Snapshot refresh interrupted mid-migration → migration is one transaction; test a reopened store.

### Spec
- §16 "Find usages scope": remove the trigger gap sentence. §8: document `parent_object_id`.
- Metric 9 (snapshot load ≤ 80 ms) and 10 (incremental refresh ≤ 2 s) must still pass: run `pnpm bench`.
  Add ~200 triggers to `scripts/bench/gen-schema.mjs`.

---

## A2 — PL/pgSQL tier-2 intelligence

**Why:** §9 says PostgreSQL completion is tier 1 until "Phase 8"; T-SQL has CTE/derived-table columns,
temp tables, signatures. This is the biggest UX gap between the engines.

### Sidecar (`sidecar/Querithm.Intelligence.PgSql/`)
New folder `Completion/` mirroring `Querithm.Intelligence.TSql/Completion/`:
- `PgScopeBuilder.cs` — from `PgTokenizer` tokens (no regex), build statement scopes: `FROM`/`JOIN`
  sources with aliases, CTEs (`WITH x(a,b) AS (...)` and inferred projection columns), derived tables,
  `LATERAL`, `RETURNING`.
- `PlpgsqlBlockScanner.cs` — inside `$$…$$` bodies: `DECLARE` variables with types, function parameters
  (from snapshot params or the `CREATE FUNCTION` header), `RETURNS TABLE (...)` columns, `FOR rec IN`
  record variables, `CREATE TEMP TABLE` within the script.
- `PgTier2Completion.cs` — column completion for alias.`|`, variables, temp tables, `INSERT INTO t (|`
  column lists, `CALL proc(|` named args.
- `PgLanguageService.cs`: wire `Complete`/`Signature` to tier 2 with tier-1 fallback; diagnostics stay
  as now (no PG parser — document that tier 2 = scope-aware completion, not full parse).
- `PostgresProvider.GetCapabilitiesAsync`: `CompletionTier = "tier1+tier2"`.

### UI
- `src/editor/providers.ts`: nothing engine-specific should change; verify the PG tier-2 path merges
  with tier 1 like T-SQL does. Status bar already reads `CompletionTier`.

### Tests
- `PgSqlTests.cs`: add a snippet corpus like the T-SQL 60-snippet suite (target ≥ 40 PG snippets:
  CTE columns, LATERAL, DECLARE vars, RETURNS TABLE, temp tables, `$tag$` quoting, `::` casts,
  quoted identifiers with mixed case).
- `LargeDocumentTests.cs`: 10k-line PL/pgSQL function — completion within budget.
- Vitest: provider merge for PG tier 2.

### Performance
- New §14 row **27**: "Tier-2 completion latency, PostgreSQL, 10k-line PL/pgSQL function —
  `lang.complete` roundtrip, p95 ≤ 150 ms". Add the function to `gen-schema.mjs` (PG side) and a case to
  `scripts/bench/run.mjs`.

### Failure paths
- Unterminated `$$` while typing → scanner treats rest of document as body, completion still answers
  (tier-1 fallback); test it.

### Spec
- §9: replace "Tier 2 for PL/pgSQL is Phase 8" with what tier 2 covers for PG.

---

## A3 — Local snippet library

**Why:** the `script` table only binds tabs to files; there is no reusable snippet store.

### Contract (`sidecar/Querithm.Contract/Snippets.cs`, new)
```
record Snippet { Ulid Id; string Title; string Prefix; string Body; Dialect? Dialect; string[] Tags; long UpdatedAt; }
record SnippetSaveParams { Snippet Snippet; }
record SnippetRef { Ulid Id; }
```
RPC: `snippet.list` → `Snippet[]`, `snippet.save`, `snippet.delete`.
Limits validated in the sidecar: title ≤ 200 chars, prefix `[A-Za-z0-9_]{1,40}` (validated on the
prefix string, not SQL), body ≤ 256 KB, ≤ 20 tags, ≤ 5,000 snippets.

### Store
- `snippet` table (id, title, prefix UNIQUE, body, dialect, tags_json, created_at, updated_at);
  migration v2 → v3. User data: stays in `store.db`, never logged.
- `Core/Store/Stores.cs`: `SnippetStore`.
- Ship a small built-in set per dialect (read-only, in code, not in the DB).

### UI (`src/snippets/`, new)
- `snippetStore.ts` (Zustand), `SnippetsPanel.tsx` (list, search, edit in a modal; body edited in the
  shared Monaco via a temporary model — no second Monaco instance).
- `src/editor/providers.ts`: snippet completion items (Monaco snippet syntax `${1:name}`), filtered by
  the tab's dialect.
- Commands (`src/app/commands.ts`): "Save selection as snippet", "Insert snippet…" (Palette).

### Tests
- xUnit: CRUD, validation rejects oversize/invalid prefix, migration v2→v3.
- Security: snippet bodies never appear in logs (extend the scrubber test).
- Vitest: completion items per dialect; panel edit flow.

### Failure paths
- Duplicate prefix → sidecar returns a validation error; UI shows it inline.

---

## A4 — Windows bench and Linux WebKitGTK gate

**Why:** §15 Definition of done needs all metrics on Windows **and** Linux; `bench/results/` has only Linux runs.

- Run `pnpm bench` on Windows against the Docker fixture; commit `bench/results/<date>-windows.json`.
- Linux: run metrics 3, 4, 12 with `WEBKIT_DISABLE_COMPOSITING_MODE` set and unset; record both.
- Any metric over target: profile first (`dotnet-trace` / Performance panel), fix, cite the profile.
- If WebKitGTK fails metric 3, apply the §1 fallback (reduced minimap/decorations on Linux) and add a §16 row.
- Update §14 with a "last verified" note per OS.

---

## Phase A done when
- A1–A4 merged, `pnpm test` + `QUERITHM_IT=1 dotnet test sidecar` green, smoke UI green.
- Metrics 9, 10, 27 within target; Windows + Linux bench files committed.
- Release notes written.

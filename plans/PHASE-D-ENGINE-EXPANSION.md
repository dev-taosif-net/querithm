# Phase D — Engine Expansion: SQLite → MySQL/MariaDB → DuckDB → Oracle → ClickHouse → CockroachDB → TiDB

Goal: add seven engine families behind the existing `IDbProvider` seams. Each engine is its own sub-phase.

Order: **D1 SQLite** (driver already a dependency) → **D2 MySQL/MariaDB** (pure managed driver) →
**D3 DuckDB** (native binary and installer-size gate) → **D4 Oracle** (enterprise SQL) →
**D5 ClickHouse** (analytics) → **D6 CockroachDB** (PostgreSQL-compatible transport) →
**D7 TiDB** (MySQL-compatible transport).

Milestones: D1–D3 are the initial milestone; D4–D7 are later expansion milestones, not prerequisites
for releasing D1–D3. Ship each sub-phase independently. MySQL and MariaDB share a provider family but
are separate products: separate product detection, test coverage, and entries in the engine list.

**Merge model.** Provider slices merge behind a hidden flag: the engine is absent from the profile
picker, from connection-string auto-detection (`RpcServer` tries every registered provider), and from
the README. A dev-only setting exposes it for testing. The engine becomes an **active engine**
(CLAUDE.md §2) the day it is exposed in the picker, which requires its feature matrix and release
gates (end of this plan) to pass. From then on every feature must cover it or degrade with a visible
reason. If a D4–D7 driver gate fails, the engine does not ship and the feature matrix says why.

**Spec updates (same change as the code, per sub-phase).** §2 rows; §3 layout and §4 diagram (grant
registry, new projects); §5 injection list and type mapping; §6 rule 2 and the database-switching
table; §7 transactions; §8 refresh step 2 (per-engine marker table) and the `LocalStore`
`user_version` bump; §11 plans table; §13 rules 1–3 (scrubber keys, TLS default, auth kinds) and
rule 6 (D0c); §14 rows; §15 milestone 8 ("more providers (MySQL first)" → this order); §16 rows;
README engine list; CLAUDE.md active-engine line and its "Phase 1 engines" wording (plans/README.md
"Naming vs CLAUDE.md").

---

## D0 — Shared groundwork

D0 lands as slices **D0a–D0g before D1**, each mergeable alone with its own "done when". Items that
only make sense once a given engine exists (MySQL case rules, Oracle packages) live in that engine's
sub-phase. The **per-engine checklist** at the end of D0 is repeated in every Dn.

### D0a — Contract, profile model and store

**Engine and dialect identity**
- Extend `Engine` (sidecar/Querithm.Contract/Core.cs) incrementally as providers land: `Sqlite`, `MySql`,
  `DuckDb`, `Oracle`, `ClickHouse`, `CockroachDb`, `TiDb`. Preserve the members `Mssql`/`Pg` and their
  serialized values `"mssql"`/`"pg"` (camelCase `JsonStringEnumConverter` and `EnumText.Of`), and
  `Dialect` `Tsql`/`Pg` (`"tsql"`/`"pg"`). Write a round-trip test for each new value's wire and store
  spelling (`MySql` serializes as `"mySql"`).
- Give every engine its own `Dialect` value (`Sqlite`, `MySql`, `DuckDb`, `Oracle`, `ClickHouse`,
  `CockroachDb`, `TiDb`); shared scanner code sits behind them. Reason: `ProviderRegistry.ForDialect`
  is `FirstOrDefault` (Core/Sessions/SessionManager.cs:18), and it routes every `lang.*` call
  (Core/Metadata/ObjectService.cs:127–128) and refactor (Core/Refactor/RefactorService.cs:71), with
  `CompleteParams` carrying only the dialect (Contract/Language.cs:15). Two engines sharing a dialect would
  reach an arbitrary provider. MySQL and MariaDB share `MySql` and differ by `Product`.
- Add `Product` (product name and server version as detected) to `SessionState`/`Capabilities`, so
  MySQL vs MariaDB (and other product-specific behaviour) is data, not inference.
- Bump `ContractInfo.Version` (Core.cs:6) with every contract change, then run `pnpm gen:contract`;
  the webview refuses to run on a mismatch.

**Location**
- `Profiles.cs`: discriminated `Profile.Location` — `Server` (host/port plus provider-specific
  service/database settings), `File` (grant ID from D0c), or `Memory` (session-scoped).
- Migration to `Location.Server` preserves every encoding `Profile.Host` carries today: PG Unix-socket
  directories (`/…`, `@…`) and comma failover lists (Providers.Postgres/PostgresProvider.cs:77–84),
  and SQL Server `tcp:`, `np:`, `lpc:`, `host,port`, `host\instance`
  (Providers.SqlServer/SqlServerProvider.cs:56–69). New engines with socket support model it explicitly.

**Auth and connection readiness**
- Add an explicit no-credentials `AuthKind` for file/memory engines. Do not reuse the SQL default:
  `AuthKind.Sql` is the enum default and the fallback in `ProfileStore.Read` (Core/Store/Stores.cs:94),
  connection-string rebasing (Host/RpcServer.cs:232) and the editor (src/connections/ProfileEditor.tsx:50).
  Validate engine/location/auth combinations in the sidecar. Hide username/password/TLS/SSH fields for
  local profiles; the environment field and Prod guards stay.
- Readiness: `MetadataService.Snapshot` gates background refresh on `_sessions.HasCredentials`
  (Core/Metadata/MetadataService.cs:75), a pure `_secrets.ContainsKey` (SessionManager.cs:185). Replace
  it with the auth-aware `CanOpen` (SessionManager.cs:62), extended with file-grant validity and
  memory-session availability. The same gap already hits Windows/Kerberos/Entra-interactive profiles
  before the first open, after a sidecar restart and after `ForgetProfile`.
- Extend `NeedsSecret` (SessionManager.cs:263) for the new auth kind and replace its UI copies
  (src/store/connections.ts:28, src/connections/ProfileEditor.tsx:55) with one shared rule.
- Rust reads the keyring on every `session.open`/`profile.test` regardless of auth kind, and a keyring
  error fails the call (src-tauri/src/ipc.rs:157–176). A profile that needs no secret must open with a
  locked or unavailable keyring: skip the lookup for it (Rust already sees every profile in the
  `profile.list`/`profile.save` replies, ipc.rs:178–183).
- Tests: fresh local connect/query/explorer with an empty keyring and with a failing keyring, restart,
  no password prompt or synthetic secret, invalid auth/location rejection, unchanged server
  credential-retention behaviour, and background refresh for a Windows-auth profile before first open.

**Capabilities**
- Extend the existing `Capabilities` record (Contract/Sessions.cs:65–77; today `Trace`, `Debugger`,
  `EstimatedPlan`, `ActualPlan` as `CapabilityInfo{Available, Reason}`) with ChangeDatabase,
  Transactions, Namespaces, DataEdit, Import, SchemaCompare/DataCompare (read vs apply), References and
  Monitor. Decide whether the mandatory provider members (`Metadata`, `Plans`, `Scripter`, `DataEdit`,
  `Import`; Contract/Providers/IDbProvider.cs:27–34) become nullable or get capability flags.
- Represent namespace semantics separately: SQLite has `main`/`temp`, MySQL uses databases as schemas,
  Oracle uses user-owned schemas. Do not disable explorer namespaces because an engine lacks `CREATE SCHEMA`.
- Capabilities depend on product, server version, permissions, profile access mode and session state.
  Today they are cached per `profileId|database` (ObjectService.cs:108–118) and per server in the UI
  (src/store/connections.ts:105): re-key or invalidate both. Enforce unsupported operations in sidecar
  RPCs as well as disabling UI actions with a reason.

**Store**
- `profile.engine` is TEXT without a CHECK constraint (Core/Store/Schema.sql:4), so new values need no
  column change. The hazard is the silent fallback in `ProfileStore.Read`: unknown engine text becomes
  `Engine.Mssql` (Stores.cs:91), unknown auth `AuthKind.Sql` (:94), unknown snippet dialect `Tsql`
  (:225). Replace them with a visible "unsupported profile" state that is never opened as SQL Server
  (a profile written by a newer build or for a hidden engine).
- Location fields become columns or `StoredOptions` members (Stores.cs:82) through a
  `LocalStore.Migrations` step with a `user_version` bump (today 3) and a migration test.

**Done when:** SQL Server/PG profiles round-trip unchanged (Host encodings, SSH, CA path, retention,
timeouts), unknown engine values are refused, readiness tests pass, contract version bumped; no new
engine is exposed.

### D0b — Provider-owned transactions

Today transaction control is split three ways:
- `tx.*` uses `IDbProvider.BeginTransactionSql/CommitSql/RollbackSql` (Contract/Providers/IDbProvider.cs:78–80;
  SessionManager.cs:352, 372).
- Grid edits, import savepoints and `BatchInsertLoader` use a second SQL set,
  `IDataEditDialect.*Sql` and `Savepoint*Sql` (Contract/Providers/IDataEditDialect.cs:60–66), worded differently
  (SqlServerProvider.cs:286 `IF @@TRANCOUNT > 0 COMMIT TRANSACTION` vs SqlServerDataEdit.cs:241).
- Native bulk loaders (SqlServerImport.cs:240, PostgresImport.cs:199) and data-compare reads
  (DataCompareService.cs:94, 280) use ADO.NET `BeginTransactionAsync`; PG actual plans issue literal
  `BEGIN`/`ROLLBACK` inside the provider (PostgresPlans.cs:26, 38). Compares only emit
  `SyncTransaction` text into generated scripts (DataCompareService.cs:454).

State detection already exists: `GetTxStateAsync` (IDbProvider.cs:77) and `RefreshTxStateAsync`, which
pins on Open/Unknown (SessionManager.cs:396–436) after executions and tool runs (ExecutionService.cs:270, 574).

Plan:
- Unify these behind one provider-owned transaction API: begin, commit, rollback, savepoints and state.
  Providers may use SQL or driver APIs (Oracle needs `OracleConnection.BeginTransaction`); Core owns
  serialization and connection lifetime without referencing concrete drivers. Keep and extend the
  existing state detection.
- `Pool.Return` checks only `IsOpen` (Core/Sessions/Pool.cs:228), so helper borrows (compare reads,
  metadata refresh) can return a connection in an open or unknown state. Every return checks state;
  such a connection is discarded, never pooled.
- Specify per engine: autocommit default, user-issued transaction SQL, DDL implicit commits, savepoint
  support, and unknown/ambiguous states.
- Preserve SQL Server/PG behaviour. This touches §14 critical paths: gate on the existing SQL
  Server/PG integration suites (including `ConnectionLifecycleIntegrationTests`) and `pnpm bench`
  (metrics 11b, 15, 20, 23 within 20 %).

**Done when:** one API serves `tx.*`, edits, imports, compare reads and PG actual plans; the existing
suites and bench pass.

### D0c — File grants and file-access policy (required before D1)

**Owner decision (2026-10-31): writable database files are approved**, read-only by default and only
through the consent flow below. This relaxes §13 rule 6 and CLAUDE.md §3 ("picked files are
read-only"), and the §16 rows that rest on "the sidecar never writes into the user's folders" (§7
import, saved snapshots, monitor exports); those rows keep their meaning for every other kind of file.
Amended wording: "the sidecar writes a user file only through a ReadWrite database grant given in a
dialog Rust shows, limited to that database and its engine-managed journal/WAL/SHM files." Amend §13,
§16 and CLAUDE.md in the same change as enforcement, not before (the rules describe enforced
behaviour). Security tests in `sidecar/Querithm.Tests/Security/` are written first. No Tauri capability
changes: the webview gets no fs scope on database files. This plan does not itself enable anything.

**Picking and consent**
- A Rust command (`db_pick_file`, like `import_pick_file` in src-tauri/src/files.rs, "returns a token,
  never the path") shows the native dialog, registers the grant and returns only an opaque ID and a
  display name. Not the webview `@tauri-apps/plugin-dialog` `open()` used for the SSH key path today
  (ProfileEditor.tsx:460–463).
- Read-only by default. Rust shows the "Allow writes to this file" consent dialog itself; the answer
  never passes through the webview, and the webview's `dialog:allow-ask` is never the consent path.

**Grant registry (Rust)**
- Rust persists `{ID → canonical path, file identity (dev/inode or volume + file index), access mode,
  profile ID, consent}` outside every webview fs scope. Today the webview may write anywhere under
  `$DATA/querithm/**` (src-tauri/capabilities/default.json), so either narrow those scopes to the
  folders the webview needs (scripts, notebooks, exports, the workspace file) or authenticate each
  entry with an HMAC whose key lives in the OS keyring. Security test first: an entry written or
  altered by the webview is rejected.
- Rust injects `{path, mode, fileIdentity}` into `session.open`/`profile.test` the same way it injects
  secrets (extend the §5 injection list). Rust checks only existence, identity and canonical path;
  format and corruption checks are driver work in the sidecar. The sidecar re-checks the opened file's
  identity against the injected one (closing the check-then-open gap) and keeps the path in memory only.
- The existing `PickedFiles` tokens (files.rs:20–23, used by `import.*` and `schemaCompare.*`) are
  in-memory, one-time per `import.run`, and do not survive an app restart; the grant registry is a
  separate, durable mechanism.
- Rust standard library only, or a §2 row and `cargo audit` for any new crate. Update §3/§4 and add a
  §16 row explaining why this is the smallest design that keeps paths out of the webview.

**Lifecycle**
- A missing, moved, replaced (identity mismatch) or revoked file gives a visible error with a
  "Re-pick file…" action; never silently create or open another file. During background workspace
  restore no dialog opens (§16 "Connections across restarts").
- Revocation is a contract RPC (DTO in `Querithm.Contract`). It is refused while an affected session
  has an open/unknown transaction or a running query, or it goes through the tab-close Commit /
  Roll back / Cancel prompt (§6 rule 5). Then it closes the sessions and invalidates pooled
  connections; loss of a memory database is announced.
- `profile.delete` removes the profile's grants. Duplication and export never transfer a writable
  grant; the grant registry is never exported.
- Other raw paths stay as today in Phase D: the SSH key path (picked in the webview, read by
  Core/Ssh/SshTunnelManager.cs:188) and the free-text CA path (ProfileEditor.tsx:386). Note this in §16.
- Paths are user data: `LogScrubber` (Core/Logging/LogScrubber.cs) scrubs only secrets today; add path
  scrubbing for driver errors and logs.
- The dev bridge (scripts/dev-bridge-files.mjs) mirrors grant picking and injection, so `smoke-ui` can
  cover file engines.

**Enforcement inside the engines**
- A writable grant covers that database and its necessary journal/WAL sidecars, not arbitrary files in
  its directory. Configure temporary/spill outputs under app data wherever supported. Document required
  adjacent sidecars and test read-only opens that would otherwise create files.
- Read-only connection mode is not a filesystem sandbox. D1 refuses user SQL `ATTACH`, `DETACH`,
  `VACUUM INTO`, extension loading, and filesystem-changing pragmas/functions until authorized
  implementations exist.
- File-policy refusals are hard errors, not a confirmable `GuardReason`: `confirmedDestructive`
  (Contract/Queries.cs:20, 38) never bypasses file authorization.
- Use native authorization hooks/engine settings where available, backed by token/AST checks; cover
  computed filenames, URI paths, parameters, and multiple statements. If enforcement cannot be
  demonstrated for the pinned driver/native build, refuse the operation with a visible reason.
- Security tests: forged ID/path, webview-written registry entry, read-to-write escalation, profile
  binding, identity mismatch, revocation with an open transaction, app and sidecar restart,
  missing/replaced file, migration of existing server profiles, unpicked reads/writes in both access
  modes, traversal/symlinks, computed destinations, forbidden extension loading, and attempts to alter
  security settings. Future attachment support requires its own grant lifecycle and session-scoped
  namespace/cache design before enabling it.

**Done when:** the Security tests above pass and §13/§16/CLAUDE.md are amended in the same change.

### D0d — Connection ownership and session-scoped metadata (required before D1)

- Add a provider connection-lifetime policy: existing server pooling versus a session-owned connection,
  built on the existing `Session.Pinned` connection (Core/Sessions/Session.cs:16; used at SessionManager.cs:254). SQLite uses one lazily opened
  connection per tab until tab close; `Pooling=False` alone does not disable Core's own pool
  (Pool.cs:46–50, per-execution borrow at SessionManager.cs:252–260).
- Route every connection user through the policy. Besides execution, today's pool borrows that bypass
  the session are: MetadataService.cs:136, DataCompareService.cs:87 and 273, ObjectService.cs:17–23
  (cross-database), TraceService.cs:48, BlockingBannerService.cs:82, and `OpenDedicatedAsync`
  (SessionManager.cs:163–166) for monitor/debugger. The Explorer holds its own per-server session
  (src/store/connections.ts:96–105); for a memory database it must use the owning tab's session.
- Serialize work on a session connection; avoid deadlocks when metadata refresh is requested during
  execution. No helper may silently open a fresh `:memory:` database.
- Scope memory databases and their snapshot identities to the session, not just profile/database.
  SQLite `temp` objects are also session-local. Keep persistent file metadata separate from these.
- Carry this scope through the contract and consumers. Snapshot, refresh, `meta.updated` and completion
  DTOs are profile/database-only today (Contract/Metadata.cs:10–14, 86–91, 98–106; Language.cs:9–18),
  as is `ISchemaSource.Get(profileId, database)` (Contract/Providers/Services.cs:6–10); also explorer subscriptions
  and result-origin lookup. Use an explicit session/snapshot identity, validate that it belongs to the
  requested profile, and remove ephemeral snapshots/subscriptions on close.
- Metadata read inside a transaction is session-local even for file-backed databases. Do not publish
  uncommitted schema into shared/persisted snapshots or other tabs. D1 defers shared refresh while the
  owner has an open/unknown transaction and marks its shared snapshot stale; if transaction-local
  completion/explorer refresh is offered, use a separate non-persisted session overlay. On
  commit/rollback, discard the overlay and refresh committed metadata. Do the same after implicit
  commit, cancellation-induced rollback, or connection loss once the resulting state is established.
- Closing a memory session destroys its data. Reconnect/sidecar recovery (§4: restart once, replay
  profiles) must visibly report that loss and must not present an empty replacement as a recovered database.
- Tests: two memory tabs with identical object names stay isolated; delayed events after tab close;
  mismatched profile/session request; create → refresh sees the same database; temporary objects;
  commit/rollback does not release session ownership; close/cancel cleanup; reconnect; metadata
  refresh while execution is active.
- Test file-backed `BEGIN → CREATE/ALTER/DROP → refresh → ROLLBACK` and the commit equivalent across
  two tabs. A second tab must not see uncommitted objects; rollback must leave no phantom objects in the
  cache or after restart. A read transaction's old snapshot must not replace newer committed metadata.

**Done when:** the tests above pass on a test provider with session-owned lifetime, and SQL
Server/PG behaviour is unchanged.

### D0e — Two-engine audit and identifier identity

**Audit.** Replace engine branches with capability checks (feature availability) and provider/dialect
registries (syntax, quoting, metadata, language). Known places:
- Sidecar: Stores.cs:91/94/225 fallbacks (D0a); default database `"postgres"` for PG else null
  (SessionManager.cs:224, ActivityMonitor.cs:228); `ToDictionary(p => p.Engine)` (SessionManager.cs:13);
  warm-up hard-coding Tsql/Pg (RpcServer.cs:59); connection-string auto-detection across all providers,
  where new providers change detection of ambiguous strings and `Rebase` sets non-PG engines to
  `AuthKind.Sql` (RpcServer.cs:207–233); provider list, project refs and `TrimmerRootAssembly`
  (Host/Program.cs:71, Querithm.Host.csproj:28–31, 49–53); `plan.parse` Mssql-else-PG
  (Core/Metadata/PlanParsing.cs:29); Mssql→`OrdinalIgnoreCase` else `Ordinal` (RefactorService.cs:31,
  DataCompareService.cs:52); `dbo`/`public` (RefactorService.cs:52); SchemaCompareService.cs:151;
  monitor branches (MonitorService.cs:172, 241; KillGuard.cs:98, 104; Diagnoser.cs:110–224;
  WaitDictionary.cs:49).
- UI: src/app/actions.ts `profile.engine === "pg" ? "pg" : "tsql"` (379, 425), `engine === "pg"` (655),
  `statisticsIo`/`statisticsTime`/`actualPlan` gated on `"mssql"` (431–433, 801). Many types use the
  literal `"mssql" | "pg"` instead of `Engine`, so a new engine compiles and silently becomes
  mssql/tsql; only `Record<Engine, …>` maps catch it (ProfileEditor.tsx:17, 33; profileList.ts:11).
  Use `Engine` with exhaustive maps/switches everywhere: editors.ts `languageFor`, snapshots.ts,
  refactorActions.ts, erdActions.ts, ErdTab.tsx, ResultsPanel.tsx, gridEdit.ts, ResultGrid.tsx
  (`N`/`E` literal prefixes), staging.ts, cellInput.ts, GridDialogs.tsx, notebook Cell.tsx/CellOutput.tsx,
  monitor ActivityTab.tsx, store/ui.ts, profiles.ts `engineBadge`, EngineLogo.tsx, ProfileEditor.tsx
  defaults (ports 1433/5432).
- Identifier quoting done in TS outside the engine quoting service (src/refactor/refactor.ts:58–60, where
  non-PG means `[x]`; src/erd/erdLayout.ts:251, 274) moves to the engine quoting service or a per-dialect table
  tested against it.

**Identifier identity.** Audit unconditional lowercasing and case-insensitive dictionaries. Introduce
provider-aware identifier identity for database/schema/object names, distinct from display names and
SQL quoting, and apply it to pool keys (SessionManager.cs:238; Pool.cs:206), metadata caches and
refresh-job keys (MetadataService.cs:16, 59), the store (`obj_name … COLLATE NOCASE`, Schema.sql:25),
the UI snapshot key (snapshots.ts:22), DataEditorService.cs:50, SchemaCompareService.cs:102,
ObjectService.cs:17, completion and compare matching. Preserve exact names until server comparison
rules are known; do not infer identifier case rules from the client OS or data-column collation.
Migrate or invalidate old normalized cache entries; never merge distinct object identities.
(MySQL/MariaDB `lower_case_table_names` tests belong to D2.)

**Done when:** every remaining `"mssql"`/`"pg"`/`Engine.Mssql`/`Engine.Pg` reference is reviewed and
either removed or justified, TS uses `Engine` with exhaustive checks, and identity tests pass on SQL Server/PG.

### D0f — Stable Arrow schemas (required before D1)

- Today `ColumnEncoder.Create` fixes one Arrow type per column from `DbColumn.DataType` before the first
  batch and never switches (Core/Execution/ArrowEncoder.cs:208–251); unknown CLR types fall back to
  Utf8 (:250). Batches flush at `ResultSetEncoder.MaxBatchRows` (10,000, :19) or a byte limit (:145).
  The new-engine risk is the reported type: SQLite's declared type/affinity is not a guarantee, and
  typed reader calls (`GetInt64`/`GetDouble`) on mixed storage classes throw or coerce.
- Each provider declares which reported column types it trusts. D1 uses a stable Utf8 representation
  for dynamically typed columns, with a visible type note and documented lossless encodings for
  integers, reals, text, blobs, and nulls. Use native typed columns only where the provider can prove
  the result's type is stable.
- Per-value storage classes travel only in Arrow — a sibling column (like the existing
  `<col>__truncated`, ArrowEncoder.cs:67) or a union — never in JSON DTOs; DTOs and schema metadata
  carry per-column facts only.
- Specify sort/filter/export behaviour for textual numeric values; refuse editing when original typed
  values cannot be reconstructed safely. Do not silently coerce mixed values into numeric builders.
- Phase C consumers infer chart types from column types and offer a binary viewer: define their
  behaviour for textual numbers and blobs (parse with a visible note, or degrade with a reason) and test it.
- Tests: null-first results, integer/text/blob mixtures, empty results, large values, a type change after
  more than `MaxBatchRows` rows; grid, export, charts and editing consume one stable schema.

### D0g — UI, languages, tests and bench harness

- src/connections/ProfileEditor.tsx: the engine picker (`ENGINES`/`EngineSelect`) gains file/server
  variants; file picking goes through D0c's Rust command; per-engine defaults (ports, auth) become data.
- src/connections/logos/: engine logos (own drawings or permitted marks — check trademark terms).
- Languages, registered incrementally per engine: grammars in src/editor/languages.ts, registration in
  src/editor/monaco.ts, the engine→language mapping (`languageFor`, src/editor/editors.ts) and the
  dialect derivation now spread over providers.ts, editorActions.ts, runScope.ts and actions.ts become one
  mapping with completion/snippet routing. Test quoting, comments, strings, statement boundaries, and
  procedural bodies for each dialect; shared scanners require parity tests.
- Tests: replace the 11 duplicated `TheoryData<string> Engines => new() { "mssql", "pg" }` lists
  (DataEditTests, SchemaCompareTests, ReferenceFinderTests, TriggerReferenceTests and seven
  `Integration/` suites) with one shared `Engine`-typed source, and turn the split
  `SqlServerIntegrationTests`/`PostgresIntegrationTests` into shared theories. File-engine integration
  tests need no Docker and must run in plain `pnpm test`: they must not use the `QUERITHM_IT` gate
  (`TestSupport.IntegrationEnabled`, Tests/TestSupport.cs:73). Record where they live in CLAUDE.md §5.
- Bench harness: scripts/bench/run.mjs and the §14 fixture are SQL Server/PG only (metric 4 on the
  T-SQL fixture, 15 via `WAITFOR DELAY`, 21–23 record the slower of SQL Server/PG, 24 SQL Server).
  Add per-engine fixtures (`gen-schema.mjs` variants), cancel workloads and per-engine metric ids.

### Per-engine checklist (repeat in D1–D7)

Every engine table covers these rows (a table may combine them); "n/a" needs a reason.

| Row | Required content |
|---|---|
| Driver | §2 row; `Directory.Packages.props` pin; `THIRD-PARTY-NOTICES.md` including native-bundled licenses (not covered by NuGet metadata in `scripts/gen-notices.mjs`); trimming root in `Querithm.Host.csproj` and trimmed release smoke; clean `dotnet list sidecar package --vulnerable --include-transitive`; measured installer-size delta (metric 17) |
| Connection / TLS / SSH | TLS on by default (§13 rule 2 updated); SSH via `RouteThrough`/`TunnelTarget` or n/a with reason |
| Auth | supported kinds added to §13 rule 3; new secret parameter names added to the log scrubber (§13 rule 1) |
| Identity read | `SessionKey` per §6 rule 7, or the §16 "identity read the server refuses" degradation; file engines: none, monitor n/a |
| Classify / guards | token-based classifier with engine-specific destructive forms; database-switch and transaction-statement detection; DDL detection driving §8 refresh; sidecar re-classification with `confirmedDestructive` / `GuardReason.ProdWrite`; actual-plan runs that execute SQL guarded like `query.execute`; Security tests written first |
| Metadata markers | one catalog query per refresh, computed server-side, never per-object DDL fetches; measured by metric 10; per-engine row in §8 |
| Arrow types | mapping per §5 (decimals → Decimal128, unsigned/large integers, temporal types, LOBs, nested types); lossless fallback where the grid cannot represent a type |
| Data edit | optimistic conflict check (row version or original-values compare) or the explicit no-rowversion degradation (§7); tables without a proven unique key read-only with reason |
| Failure recovery | connection drop mid-result, cancel then reuse, sidecar restart (replay, lost transactions reported), SSH tunnel loss for server engines, partial import, concurrent edit conflict |
| Fixture | exact server version (patch tag or digest, never a floating tag); credentials only in `scripts/bench/docker/compose.yml` or generated at fixture start, otherwise extend the CLAUDE.md allow-list in the same change |

---

## D1 — SQLite

| Area | Plan |
|---|---|
| Projects | `Querithm.Providers.Sqlite`, `Querithm.Intelligence.Sqlite` (tokenizer + splitter + classifier) |
| Driver | `Microsoft.Data.Sqlite` 10.0.13, already pinned and used by `Querithm.Core` for `store.db` (bundled SQLite 3.53.3 via SQLitePCLRaw 2.1.13). Native calls (interrupt, authorizer, limits, db config) go through `SQLitePCLRaw`: pin it explicitly once called directly. Update the §2 row |
| Connection | granted file: explicit `Mode=ReadOnly` or `Mode=ReadWrite` (never the default `ReadWriteCreate`), `Cache=Private`, `Pooling=False`; `:memory:` via `Mode=Memory`, which turns on URI filenames (`SQLITE_OPEN_URI`), so refusal tests cover URI targets; per-connection initialization below |
| Auth / identity | no-credentials auth (D0a); no server identity, monitor n/a |
| Sessions | session-owned connection per D0d; `ChangeDatabase` off; expose `main`/`temp`; user `ATTACH`/`DETACH` refused in D1 |
| Metadata | `sqlite_schema` + `pragma_table_xinfo`, `pragma_index_list/info`, `pragma_foreign_key_list`; `PRAGMA schema_version` as the cheap whole-database change check; deterministic markers from the `sqlite_schema.sql` text of tables plus dependent indexes/triggers (FKs live in the table SQL); stable IDs scoped to namespace; invalidate after DDL |
| Arrow types | stable schema policy from D0f; no per-cell Arrow type changes |
| Cancel | `SqliteCommand.Cancel()` is a no-op through Microsoft.Data.Sqlite 11.x; cancel calls `sqlite3_interrupt` on the session connection's handle (thread-safe, connection-wide). An interrupted DML statement inside an explicit transaction rolls back the whole transaction: report it. The driver's busy-retry loop runs with no statement active, so an interrupt during a lock wait is lost: set an explicit busy budget (`DefaultTimeout`/`CommandTimeout`). Prove long-query cancellation and connection reuse |
| Messages | none (no PRINT) |
| Plans | `EXPLAIN QUERY PLAN` (`id, parent, notused, detail`) → `PlanTree`: build `PlanNode.Children` from id/parent, keep `detail` as opaque operator text (SQLite says the format may change between releases); add a plan `Format` value and route it in `plan.parse` (PlanParsing.cs:29) and the UI (openAsPlan.ts, CellViewerPanel.tsx); actual plans unavailable with reason |
| Classify / guards | token-based DROP and DELETE/UPDATE without WHERE; Prod guards apply to file profiles; filesystem operations follow D0c refusal policy independently of confirmation |
| Data edit | only proven unique, non-null identities; nullable declared PKs are not sufficient; verified `rowid` fallback or read-only with reason (see policy below) |
| Import | `BatchInsertLoader` with explicit transaction ownership, rollback-on-failure tests, and measured throughput (metric 23) |
| Schema compare | table rebuild pattern (SQLite `ALTER TABLE` is limited) — or degrade with reason in D1 and add in a follow-up |
| Data compare | only keys with proven uniqueness and matching SQLite/.NET equality and order; mixed storage-class keys degrade in D1 (see policy below) |
| References | `IReferenceFinder` over views/triggers definitions |
| Trace / debugger | degrade with reason "Querithm does not implement tracing/debugging for SQLite" |
| SSH | not applicable (local file) |
| Fixture | files created in a temp dir under the test's app-data root; no Docker |

**Connection initialization and hardening (every physical connection, before user SQL):**
- Set `PRAGMA foreign_keys=ON` (or `Foreign Keys=True`) before starting a transaction, and verify it reads
  back as enabled; the pragma is a silent no-op inside a transaction. Do not depend on native-build
  defaults. Reapply initialization after reconnect, including helper connections if any.
- Treat every database file as untrusted (SQLite security guidance): `PRAGMA trusted_schema=OFF`,
  `SQLITE_DBCONFIG_DEFENSIVE`, `SQLITE_LIMIT_ATTACHED=0`, and an authorizer that denies
  `SQLITE_ATTACH`/`SQLITE_DETACH`, `load_extension` and file-changing pragmas. Never call
  `EnableExtensions(true)`.
- `VACUUM INTO` clears the read-only flag internally and can create a file from a `Mode=ReadOnly`
  connection: verify that the authorizer and attach limit refuse it on the pinned build. Plain `VACUUM`
  also attaches internally; if the limit blocks it, offer it as an app-owned operation on ReadWrite
  grants or degrade with a reason.
- A read-only open of a WAL database creates `-wal`/`-shm` next to the file when the directory is
  writable: cover them in the grant or refuse with a reason.
- If users explicitly disable FK enforcement in SQL, expose that session state. App-owned edit/import
  operations require enforcement enabled; refuse with a reason if it cannot be enabled outside the
  current transaction. Never silently commit the user's transaction to change a pragma.
- Test invalid FK inserts through grid/import, cascade/restrict behavior, deferred constraints at
  commit, reconnect, and attempts to change enforcement during an open transaction.

**Row identity and compare eligibility:**
- Ordinary SQLite rowid tables may have nullable declared primary keys. Require non-null unique
  identity for the affected row; a PK flag alone does not authorize an edit. For fallback `rowid`,
  verify table/result origin and an unshadowed alias, and include optimistic conflict checks.
  Roll back app-owned patches unless exactly the intended row was affected.
- D1 compare supports only non-null unique keys whose storage types, collation, and SQL ordering
  can be matched by the .NET comparer. Validate both inputs in the same consistent read snapshots
  used for comparison; refuse unsupported keys before returning a successful diff or sync script.
  Do not treat an Arrow Utf8 fallback as evidence that keys are text or use lossy casts to force order.
- General mixed-type key support is deferred until the compare contract carries per-value storage
  classes (in Arrow, per D0f) and defines matching numeric/text/blob equality and ordering.
  `KindOf(columnType)` (IDataCompareDialect.cs:29) cannot describe arbitrary SQLite keys. Define
  cross-engine compatibility explicitly.
- Tests: nullable/composite PKs with multiple NULL rows, shadowed rowid aliases, optimistic conflicts,
  integer `2`/integer `10`/text `'2'` in one key column, blob keys, collation differences, empty inputs,
  and mixed values beyond the first batch. Unsupported cases must give a reason, not a partial diff.

**Tests:** unit (tokenizer, splitter, classifier, type mapping); integration against temp-dir fixture
files, run in plain `pnpm test` (D0g); read-only enforcement and hardening security tests (`VACUUM INTO`
on a ReadOnly connection, URI-form `ATTACH`/`VACUUM INTO` targets on memory sessions, read-only WAL
open); `SQLITE_BUSY` surfaces only after the configured busy budget with a message naming the lock and a
retry hint, and a user transaction that failed on a read→write upgrade is never auto-retried; cancel
while blocked on a lock; interrupted DML reports the rollback; corrupt file → clear error; no
half-written file after cancel; memory-database loss on sidecar restart is reported.

## D2 — MySQL / MariaDB

| Area | Plan |
|---|---|
| Projects | `Querithm.Providers.MySql`, `Querithm.Intelligence.MySql`: tokenizer with backticks, `#` comments, `-- ` comments (trailing whitespace required), `ANSI_QUOTES`-dependent `"`; executable comments `/*! … */`, `/*!80000 … */` and MariaDB `/*M! … */` are code, not comments, so the classifier sees inside them; optimizer hints `/*+ … */`; client-side `DELIMITER` handling in the splitter |
| Driver | `MySqlConnector` 2.x (2.6.2 today; MIT, managed, async, trimmable) — per-engine driver checklist |
| Connection | TCP / Unix socket; TLS default `SslMode=VerifyFull`; `Required` (encrypts without validating CA or hostname) is an explicit, warned per-profile downgrade; SSH tunnel via `RouteThrough`/`TunnelTarget`. Editor/session connections set `AllowUserVariables=true` (otherwise any `@var` in user SQL fails with "Parameter must be defined"); app SQL still binds parameters |
| Auth | password from keyring. `caching_sha2_password` full authentication needs a validated certificate, a Unix socket or loopback; RSA key exchange (`ServerRsaPublicKeyFile`/`AllowPublicKeyRetrieval`) applies only to explicitly non-TLS profiles. Surface the reason; failures can look intermittent because fast-auth cache hits succeed. `mysql_native_password` only for MariaDB and for MySQL 8.4 with it explicitly enabled (removed in 9.0); MariaDB ed25519/parsec degrade with a reason unless the plugin package is shipped |
| Identity read | `performance_schema.threads.THREAD_ID` as the `Instance` (PHASE-B-SERVER-MONITOR "Phase D note"), or the §16 unidentified degradation |
| Sessions | `ChangeDatabase` sends COM_INIT_DB (one round trip, metric 8); databases = schemas; pooled connections with `ConnectionReset=true` revert to the connection-string database, so the session database is reapplied per borrow |
| Metadata | `information_schema` + canonical definition hashes (including indexes/FKs/triggers) from one catalog query per refresh; `UPDATE_TIME` is not persisted across restarts or cache eviction and `CREATE_TIME` is not a reliable marker either; routine bodies via `SHOW CREATE` only on demand (definition retrieval), never per refresh |
| Arrow types | DECIMAL → Decimal128 (lossless string beyond precision 38); `BIGINT UNSIGNED` → UInt64; zero dates (`0000-00-00`) via an explicit lossless fallback; BIT, JSON and spatial types mapped or shown as text with a note |
| Cancel | `KILL QUERY <id>` on a side connection (MySqlConnector `Cancel`, `CancellationTimeout` then hard close); proxies/load balancers can break it — verify and report |
| Messages | `SHOW WARNINGS` after each statement when warning count > 0 |
| Plans | estimated: `EXPLAIN FORMAT=JSON` (parse JSON v1 and v2). Actual: `EXPLAIN ANALYZE` `FORMAT=TREE` text on 8.0.18–8.4; JSON only on 9.x with `explain_json_format_version=2` (default from 9.5); MariaDB `ANALYZE FORMAT=JSON`. Actual plans execute the statement, DML included, so destructive/ProdWrite guards apply |
| Classify / guards | per-engine checklist row, including executable comments and multi-statement scripts |
| Data edit / import | PK-based optimistic patches; batched INSERT; bulk load disabled (see boundary below) |
| Schema/data compare, references | implement; routines diff via `SHOW CREATE` text |
| Trace / debugger | none → reasons |
| Fixture | `scripts/bench/docker/compose.yml`: `mysql:8.4.13` (LTS floor) and `mysql:9.7.4` (current LTS), `mariadb:11.8.9` (LTS), each pinned by digest, fixture-only credentials; a separately initialized MySQL container with `lower_case_table_names=1`; `gen-schema.mjs` MySQL variant |

**Bulk-import file boundary:**
- MySqlConnector 2.x restricts local-infile requests to app-registered streams only on connections that
  do **not** verify TLS. With `AllowLoadLocalInfile=true` on a `VerifyCA`/`VerifyFull` connection it opens
  any file the server names (`File.OpenRead` of the server-supplied path). `MySqlBulkCopy` requires
  `AllowLoadLocalInfile=true`.
- Therefore D2 keeps `AllowLoadLocalInfile=false` on every connection, and connection strings/profile
  extras must not override it. Import uses batched INSERT. User SQL `LOAD DATA LOCAL INFILE` fails
  with a visible reason. Never lower `SslMode` to obtain the stream restriction.
- Bulk load may be enabled only when a pinned driver offers a stream-only mode independent of
  `SslMode`, on a dedicated, non-reusable import connection serving an app-owned stream of authorized
  input, disposed on completion, cancellation or failure. If an import must participate in an existing
  user transaction, it uses batched INSERT on that session connection.
- Gate any bulk enablement on tests for forged server file requests, unpicked paths, option injection,
  editor-issued local-infile SQL, cancellation/disconnect cleanup, and transaction rollback.

**Identifier case rules:** `lower_case_table_names` can only be set when the server is initialized
(defaults: Unix 0, Windows 1, macOS 2). Test distinct `Sales`/`sales` databases and case-distinct tables
on the case-sensitive fixture, plus the separately initialized case-insensitive container. Uses the
D0e identifier identity; never merge distinct object identities.

**Tests:** same matrix as SQL Server/PG integration suites; MariaDB vs MySQL differences as `[Theory]`
over products; connection drop mid-result; SSH tunnel loss; unsupported authentication paths → readable
error; `caching_sha2_password` full authentication against a non-loopback address (the container's
network address) with an empty auth cache; DDL implicit commits; SQL modes affecting quotes/escapes;
executable-comment classification; `@var` user variables; metadata changes without timestamp changes;
cancel then reuse; `LOAD DATA LOCAL` refused; MySQL/MariaDB plan-format differences (TREE text vs JSON
v1/v2). Pin exact tested server versions and dependency versions.

## D3 — DuckDB

| Area | Plan |
|---|---|
| Projects | `Querithm.Providers.DuckDb`; intelligence: DuckDB 2.0 parses with a PEG grammar (libpg_query is gone) over a PG-like dialect. Build the DuckDB keyword sets from the pinned version (replacing `PgSplitter.Reserved`/completion keywords) and reuse the keyword-free `PgTokenizer` only after parity tests on DuckDB lexical differences (`//`, `==`, case-preserving identifiers, PEG-only syntax); else `Querithm.Intelligence.DuckDb` |
| Driver | `DuckDB.NET.Data.Full` (MIT). Pin the DuckDB 2.0 LTS binding when published; until then 1.5.6.1 (DuckDB 1.5.6, EOL 2027-03-01), verified against matching-version docs. Prerequisites: it requires Apache.Arrow ≥ 23.0.0 while the sidecar pins 18.1.0, so upgrading the data-plane Arrow is its own slice (bench metrics 11, 11b, 12, 13; frame compatibility with the webview reader). No `IsTrimmable` metadata → root it. Native size per RID (uncompressed/deflated): win-x64 35.0/12.6 MB, win-arm64 40.9/13.5 MB, linux-x64 67.3/22.8 MB, linux-arm64 60.0/20.6 MB, macOS one universal dylib 111.6/35.1 MB (thin per arch with `lipo -thin`); no linux-musl asset. Measure metric 17 per RID **before** committing |
| Native extraction | `IncludeNativeLibrariesForSelfExtract=true` (Querithm.Host.csproj:23) extracts to .NET's default folder, outside app data: set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` under `<appDataDir>/querithm` at sidecar spawn or ship the library beside the Host; measure metric 1 on first run |
| Connection | session-owned `:memory:` or granted file; read-only by default. DuckDB.NET caches one native database per Data Source string process-wide (case-insensitive) and applies configuration only when that instance is created; later connections silently inherit its access mode and allowlist. So: open files only by canonical absolute path, one provider-owned instance per (file, mode, allowlist), refuse conflicting opens, and close every connection to dispose the instance when a grant changes. Plain `:memory:` is a fresh database per connection; never `?cache=shared` |
| Auth / identity | no-credentials auth; no server identity, monitor n/a |
| Sessions | catalogs (the opened database, `memory`, `temp`, `system`) and schemas as namespaces; `USE` changes the session's default catalog/schema and is detected for completion context; external `ATTACH` refused |
| Files | only shell-granted paths; configuration order is forced (see policy below) |
| Metadata | `duckdb_tables()`, `duckdb_columns()`, `duckdb_views()`, `duckdb_indexes()`, `duckdb_constraints()`; definition markers from one query over their `sql` text |
| Arrow | read via `DuckDBDataReader` into our Arrow builders (keep one encoder path); the binding's `ExecuteArrowStream`/`ExecuteArrowBatchesAsync` may be evaluated later (measure first). HUGEINT/UHUGEINT, DECIMAL, INTERVAL, TIMESTAMPTZ, LIST/STRUCT/MAP/UNION mapped or given a lossless fallback |
| Cancel | `DuckDBCommand.Cancel()` maps to the native interrupt in the pinned binding — verify; prove cancel then reuse, and cancellation during file reads |
| Messages | none |
| Plans | `EXPLAIN (FORMAT JSON)` → `PlanTree`; `EXPLAIN ANALYZE` executes the statement → guarded like `query.execute` |
| Classify / guards | per-engine checklist row; additionally refuse `INSTALL`/`LOAD`, `COPY … TO`, `EXPORT DATABASE`, external `ATTACH`, `CREATE [PERSISTENT] SECRET`, profiler output paths |
| Data edit / compare | PK-based with optimistic conflict check; tables without PK read-only with reason |
| Import | DuckDB.NET appender vs batched INSERT, measured (metric 23); explicit transaction ownership and rollback-on-failure tests |
| Schema compare / references | implement over `duckdb_*` definitions, or degrade with reason |
| Trace / debugger / SSH | none / none / not applicable → reasons |
| Fixture | files created in a temp dir under the test's app-data root; no Docker |

**File and configuration policy:**
- Pin a DuckDB/native-driver version and verify every setting against it. The required order is: open
  → `SET allowed_paths`/`allowed_directories` → `SET enable_external_access=false` →
  `SET lock_configuration=true`, all before accepting SQL. Allowlists cannot be set at startup and
  cannot change once external access is off, so putting `enable_external_access=false` in the
  connection string makes an allowlist impossible.
- Defaults to override before locking: `autoinstall_known_extensions`, `autoload_known_extensions` and
  `allow_community_extensions` default to true → set false; `temp_directory` defaults to `<db>.tmp`
  beside the file (or `.tmp` in the working directory for memory databases) → app data;
  `max_temp_directory_size` defaults to 90 % of free disk → cap it; point the extension and secret
  directories at app data or disable them. Refuse `CREATE [PERSISTENT] SECRET` with a reason (secrets
  would sit in SQL text, history and on disk, outside the keyring).
- `enable_external_access=false` blocks file `ATTACH`, `COPY` read/write, external `read_*` functions
  and extension install/load; external access cannot be re-enabled at runtime, and `SET`/`RESET` of
  locked settings fail (only `schema`/`search_path` and explicitly allowed configs stay settable).
- Allowlisted paths are readable **and writable**, and allowlisted directories allow file creation
  (no read/write distinction in the engine). Disabling external access also auto-allows the attached
  database and its `.wal`/`.wal.checkpoint`/`.wal.recovery` files, so a read-only profile relies on the
  read-only open mode plus the refusal of `COPY TO`/export. Picked inputs are therefore staged into app
  data (a copy) or direct external-file queries stay disabled; D3 never allowlists a user's original file
  for reading.
- Newly picked paths do not unlock an active connection. Require an explicit reconnect that rebuilds
  the allowlist (disposing the instance), refuses while a transaction is active, and explains
  memory-database data loss. Do not silently recreate an in-memory database to apply a grant.

**Security tests first:** extension install/load/autoload refused, `httpfs` URLs refused, unpicked paths
refused, read-only profile rejects database writes, picked inputs cannot be overwritten, `COPY TO` and
export refused, `CREATE SECRET` refused, profiler output paths refused, spill files land under app data,
and SQL `SET`/`RESET` cannot re-enable external access, widen paths, or unlock configuration. Cover
revoked grants, path aliases and case variants (instance cache), two profiles on one file with
different modes, and cancellation during file reads.

**Tests:** unit (keyword/tokenizer parity, classifier, type mapping); integration against temp-dir files
in plain `pnpm test`; cancel then reuse; a file locked by another process; corrupt file → clear error; no
half-written file after cancel; memory-database loss on sidecar restart reported; nested types through
grid/export; first-run native extraction location.

---

## D4 — Oracle

Session-by-session plan and progress: [D4-ORACLE/](D4-ORACLE/README.md).

| Area | Plan |
|---|---|
| Projects | `Querithm.Providers.Oracle`, `Querithm.Intelligence.Oracle` |
| Driver gate | `Oracle.ManagedDataAccess.Core` 23.26.x (.NET 10 supported from 23.26.0; 64-bit only; one managed DLL of about 5.6 MB plus transitive deps). License: "Oracle Free Distribution, Hosting, and Use Terms and Conditions" — redistribute unmodified only, include the license, keep notices (copy the MIT-Kerberos and other notices from its info.txt into THIRD-PARTY-NOTICES). Trimming would modify the binary: root it untrimmed and prove trimmed-Host smoke. Platforms: Windows, Oracle Linux/RHEL 8.6+/9.2+, Ubuntu 22.04.1+, Debian 11+, Alpine 3.20.3+, macOS ARM64 14.7+ only — Intel macOS is unsupported and marked visibly. Database 19c or later. Decide whether host `TNS_ADMIN` files (`sqlnet.ora`, `tnsnames.ora`) are honoured; default: ignored, documented |
| Connection/auth | host/port + service name (Easy Connect); SID only via an explicit `CONNECT_DATA=(SID=…)` option if offered; password from keyring; TLS (TCPS) on by default with certificate validation, plain TCP an explicit warned downgrade (§13 rule 2 updated); wallet/external auth deferred with a reason until secure storage and file-grant handling are designed |
| SSH | tunnel to the listener host/port; listener redirects (RAC SCAN, shared server) to other hosts break a tunnel → detect and degrade with a reason |
| Identity read | SID and serial#/logon time from `v$session` where permitted; otherwise the §16 unidentified degradation |
| Language | quoted identifiers, Oracle alternative quoting (`q'[…]'`), PL/SQL blocks, packages, and client `/` terminators; never split procedural bodies at every semicolon |
| Sessions | service identifies connection target; schema selection is not database switching; provider-owned driver transaction/autocommit control per D0b (`BeginTransaction` disables autocommit and adopts an implicitly started transaction), including DDL implicit commits |
| Metadata | permission-scoped dictionary views for tables/views/indexes/constraints/sequences/routines/packages; deterministic definition markers from one dictionary query per refresh; permission failures become visible degradations |
| Arrow | precision-safe NUMBER, timestamps/time zones, intervals, RAW and LOBs; no silent decimal overflow or large-value truncation |
| Messages | `DBMS_OUTPUT` only when enabled for the session and read back after each execution; otherwise none |
| Plans/cancel | estimated/actual plans only where supported and permitted; clean up any plan artifacts; actual plans execute the statement → guarded; verify command cancellation, timeout, and connection reuse |
| Classify / guards | per-engine checklist row; PL/SQL-aware (anonymous blocks, `TRUNCATE`, `DROP … PURGE`, `MERGE`); dynamic SQL classified conservatively |
| Data edit/import | PK-based optimistic patches; parameterized batched inserts first; native bulk path only after transaction and cancellation verification |
| Compare/references | table/view compare and token-based references where covered; package bodies, procedural refactors, unsupported object kinds degrade explicitly |
| Monitor/trace/debug | defer unsupported features with capability reasons and tests |
| Fixture | Oracle AI Database 26ai Free container (`container-registry.oracle.com/database/free`, Oracle Free Use Terms: 2 cores, 2 GB RAM, 12 GB user data, no security patches) pinned by exact tag/digest; least-privileged test account; admin credentials confined to fixture setup in `compose.yml` or generated at start |

**Package metadata contract (before advertising package browsing):**
- Current `ObjectKind` has no package/specification/body kinds, and `ParentObjectId` is documented
  only for triggers. Add explicit package/specification and body representation, with stable object
  identities and a relationship between them, rather than mapping packages to standalone procedures.
  Extend generated DTOs, metadata persistence, explorer categories, definition retrieval, and scripting
  dispatch together. Keep trigger-parent behavior unchanged; invalidate incompatible cached snapshots.
- D4 initially browses/scripts package specifications and bodies as whole objects. Package-member
  completion, overload-aware navigation, rename, and compare remain visibly unavailable until the
  contract supports member identity/signatures and implementations have tests.
- Test a specification and body with the same name, a package without a body, overloaded members
  in definitions, restricted body visibility, cache round trips, and whole-object script routing.
  If this contract slice is deferred, explicitly mark package browsing/scripting unsupported in the
  feature matrix instead of silently omitting packages or misclassifying them.

**Tests:** connect by service, TLS/auth failures, quoted names, PL/SQL splitting, empty-string/NULL
semantics, high-precision NUMBER, LOB streaming/cancel, DDL implicit commits, missing catalog/plan
privileges, optimistic conflicts, disconnect mid-result, and SSH tunnel loss. Unsupported OS/RIDs must
not fail app startup. Prove begin → DML → rollback leaves no writes using an independent connection;
verify commit, autocommit outside explicit transactions, SQL-issued commit/rollback, DDL-induced
transaction ends, and failed/ambiguous commit handling. Do not substitute a PL/SQL `BEGIN` block for
transaction begin.

## D5 — ClickHouse

| Area | Plan |
|---|---|
| Projects | `Querithm.Providers.ClickHouse`, `Querithm.Intelligence.ClickHouse` |
| Driver gate | official `ClickHouse.Driver` (ClickHouse/clickhouse-cs, MIT; successor of the unofficial `ClickHouse.Client`). Stable transport is HTTP(S) with ADO.NET (`ClickHouseConnection`/`Command`/`DataReader`); the native-TCP client is experimental without ADO.NET and is not used. Prove streaming, `{name:Type}` parameter binding, TLS, per-query `QueryId`, cancellation, and trimming (no `IsTrimmable` metadata) before pinning; bulk insert via `InsertBinaryAsync` (`ClickHouseBulkCopy` is deprecated); adapter remains provider-owned |
| Connection | HTTP(S) endpoint (8123/8443), database, TLS validation, keyring credentials; SSH as a plain TCP forward to the HTTP(S) port, keeping the server hostname for SNI and certificate checks through the tunnel |
| Identity read | HTTP requests carry no persistent server session; tag every query with a `QueryId`, and use the §16 unidentified degradation for monitor kills |
| Initial scope | query execution, metadata/explorer, scripting, export, charts/notebooks, and supported plan representations |
| Capabilities | ordinary relational transactions (experimental in ClickHouse) and PK grid editing off initially; do not interpret a MergeTree sorting/primary key as a unique row identity; disable unsupported mutations and compare-apply flows |
| Metadata/language | product-specific catalogs, table engines, sorting/partition keys, dialect tokens, and deterministic definition markers |
| Arrow | signed/unsigned integers, Decimal precision, DateTime64/time zones, Nullable, arrays/tuples/maps and other nested types; explicit lossless fallback where grid types cannot represent them |
| Plans/cancel | distinguish pipeline/estimated plans from execution. `ClickHouseCommand.Cancel()` only aborts the HTTP request, and self-managed servers keep running it (`cancel_http_readonly_queries_on_client_close` defaults to 0): cancel issues `KILL QUERY WHERE query_id = …` on a separate request (and may set that setting per query); verify in `system.processes` that server work stopped |
| Classify / guards | per-engine checklist row; mutations (`ALTER … DELETE/UPDATE`), lightweight `DELETE`, `DROP`/`TRUNCATE`/`DETACH` are destructive |
| Import/compare | optional measured append import with explicit partial-write semantics; no promise of transaction rollback; defer data/schema compare apply until semantics are supported |
| Monitor/trace/debug | implement only measured, permission-tested capabilities; other features degrade visibly |
| Fixture | ClickHouse 26.8 LTS (or 26.3 LTS) pinned by exact tag/digest, deterministic analytics data, restricted account |

**Tests:** nested/unsigned/large decimal values, read-only credentials, cancellation (server work stops)
and connection loss, SSH tunnel loss, partial insert reporting if import is enabled, non-unique primary
keys remaining uneditable, and transaction RPC refusal. Imported data must not be automatically
replayed after an ambiguous failure.

## D6 — CockroachDB

| Area | Plan |
|---|---|
| Projects | `Querithm.Providers.CockroachDb`; reuse Npgsql transport and shared PG scanner components behind the `CockroachDb` dialect with explicit overrides |
| Driver | Npgsql (already pinned, 10.0.3). Cockroach Labs lists Npgsql as fully supported but last tested 7.0.2, so the Npgsql 10 + pinned server combination is ours to prove in the integration suite |
| Compatibility | distinct engine/product detection and capability matrix; PostgreSQL wire compatibility does not authorize PostgreSQL catalog, plan, monitor, or debugger implementations unchanged |
| Connection | TLS verification, keyring credentials, database selection and SSH routing tested against the chosen driver/server versions |
| Identity read | own session via `SHOW session_id`/`crdb_internal` session views where permitted; otherwise the §16 unidentified degradation |
| Metadata/plans | CockroachDB-supported catalogs; `EXPLAIN` has text forms only (VERBOSE/TYPES/OPT/VEC/DISTSQL, no JSON) → a text plan representation; `EXPLAIN ANALYZE` executes → guarded; deterministic object identity and definition markers; test schema changes and permission restrictions |
| Arrow types | arbitrary-precision DECIMAL (Decimal128 where it fits, lossless string otherwise), `INT` = INT8 by default, UUID, ARRAY, JSONB, INTERVAL |
| Transactions | surface serialization/retry errors (40001) clearly; 40003 "result is ambiguous" is never auto-replayed; never auto-replay arbitrary editor scripts or committed writes; automatic retry limited to explicitly safe idempotent app-owned operations with dedicated tests |
| Classify / guards | per-engine checklist row |
| Edit/import | PK-based optimistic patches and bounded inserts with explicit transaction ownership; schema changes follow server semantics rather than PostgreSQL assumptions |
| Compare/references | supported objects only; dialect-tested quoting and generated SQL; unsupported operations degrade with reasons |
| Monitor/trace/debug | separate implementation or visible degradation; no automatic reuse of PG extensions or backend-control SQL |
| Fixture | pin v26.2 LTS (or v25.4 LTS) by exact tag/digest. License: CockroachDB Software License since 24.3 (not OSI). A single-node cluster (`start-single-node`) needs no key and is never throttled: use it for the basic smoke. A multi-node cluster without a key is throttled to 5 concurrent transactions after a 7-day grace, and free/trial keys require telemetry. **Owner decision (2026-10-31): no license or telemetry key.** The multi-node retry/node-loss fixture is ephemeral: created at the start of each gated run and removed at the end (`docker compose down -v`), so it never outlives the grace period; the setup refuses to reuse a cluster older than 6 days. Verify these license terms against the pinned version at D6 |

**Tests:** product detection, unsupported PG features, transaction conflicts/retry errors, ambiguous
commit without automatic replay, node loss, metadata invalidation, permissions, cancel/reuse, SSH tunnel
loss, and TLS.

## D7 — TiDB

| Area | Plan |
|---|---|
| Projects | `Querithm.Providers.TiDb`; share MySqlConnector transport and MySQL scanner components behind the `TiDb` dialect with explicit capability overrides |
| Compatibility | distinct product detection; never inherit MySQL/MariaDB support claims solely because connection and simple SELECT succeed. Unsupported in v8.5 LTS: stored procedures/functions, triggers, events, UDFs, FULLTEXT (parsed only), SPATIAL, XA → those explorer categories and features visibly unavailable |
| Connection | TLS verification (D2 `SslMode` policy), keyring credentials, schema/database selection, and verified SSH routing |
| Identity read | `CONNECTION_ID()` with `information_schema.cluster_processlist`, or the §16 unidentified degradation |
| Metadata/plans | TiDB-supported catalogs, indexes/constraints (foreign keys GA since 8.5.0); `EXPLAIN` formats `row`/`brief`/`dot`/`tidb_json`/`verbose` — no MySQL `FORMAT=JSON`/`TREE`, so parse `tidb_json`; `EXPLAIN ANALYZE` executes → guarded; definition-based markers; TiDB-specific diagnostics only when permitted |
| Arrow types | as D2 (DECIMAL, unsigned integers, temporal types), verified against the pinned TiDB |
| Transactions | pessimistic by default; optimistic mode checks unique constraints at COMMIT; DDL auto-commits; `txn-total-size-limit` (default 100 MB) and the 6 MB row limit surface as clear errors; no automatic replay of arbitrary user SQL |
| Cancel | `KILL QUERY` through a load balancer relies on Global Kill (on by default since 6.1) — verify for the pinned version |
| Classify / guards | per-engine checklist row |
| Edit/import | PK-based optimistic editing; parameterized batched INSERT; `LOAD DATA LOCAL` stays disabled for the D2 driver reason (it would also run as one size-limited transaction) |
| Compare/references | own compatibility tests for generated DDL and reference lookup; unsupported object kinds visibly unavailable according to tested version |
| Monitor/trace/debug | explicit TiDB adapters or reasons; no untested MySQL administrative/control statements |
| Fixture | `pingcap/tidb:v8.5.8` pinned by digest for connection smoke; a PD/TiKV topology for distributed failure cases, kept separate; document dependencies/resources |

**Tests:** product detection, MySQL-incompatible syntax/features, transaction conflicts, size-limit
errors, partial/ambiguous failures, schema changes, type precision, permissions, cancellation, SSH
tunnel loss, and connection recovery.

---

## Cross-cutting per engine (repeat for D1–D7)
- Every feature from the root README "What it does", plus features added since: editor highlighting,
  folding, diagnostics and formatting; completion tier 1 and tier 2; execution/cancel/tx; ad-hoc query
  parameters (§7 parameter syntax); grid and grid editing; export; history; explorer and
  `meta.rowCounts`; scripting; procedure Modify/Apply/Compare/Execute and dependencies; plans; import;
  ERD; schema/data compare; find usages/rename; SSH; safety guards (destructive and Prod); monitor
  (Phase B); charts/notebooks (Phase C); snippets dialect filter (A3); AI context and scrubber
  (Phase E) and CLI (Phase F) once those phases exist. For each: implemented, or degraded with a
  visible reason **and** a test.
- Bench (per-engine metric ids such as `21.sqlite`, which must meet the §14 target; a miss needs a §16
  measured-baseline row; the 20 % regression rule applies from the second run): 11b, 15, 9, 10 per
  engine; 20 and 21–24 where the feature is implemented; 8 where database switching is supported;
  28–32 where the monitor is implemented; 4 and 3 with a per-dialect fixture; 5/27 for any tier 2.
  Rerun 1, 2 and 17 after each provider. Extend `run.mjs`, the §14 fixture and the "How measured" text.
- Release note + README engine list + CLAUDE.md "active engines" line (MySQL and MariaDB listed
  separately).

## Release gates and Phase D completion
- Before an engine is exposed in the picker: complete a feature matrix with implemented/degraded
  status, concrete reason, and test reference for every cross-cutting feature. Basic query connectivity
  alone is not support. Enforce capability refusals at RPC boundaries, including actual plans that
  execute SQL.
- Each engine passes unit/security tests, its real-engine integration matrix, and UI smoke for
  connect → execute → results plus every newly supported critical workflow (edit/import/etc.).
  File-engine integration tests run without Docker; server fixtures are explicitly gated and pinned.
- Run required dependency/license/security audits and trimmed release smoke on every supported RID.
  Verify cancellation, connection loss, transaction state, resource cleanup, and sidecar restart.
- Record benchmark rows and cumulative installer size with all shipped providers (metric 17 ≤ 120 MB
  per platform; DuckDB alone adds 12–35 MB deflated depending on RID). If adding an engine exceeds the
  budget, revise packaging explicitly before shipping it; do not silently raise the target or download
  drivers at runtime.
- Apply the "Spec updates" list (top of this plan) with each implemented sub-phase. D1–D3 completion
  is the initial milestone; full Phase D requires D1–D7 gates to pass.

## Reference material for implementation spikes
- [SQLite VACUUM / computed output filenames](https://www.sqlite.org/lang_vacuum.html)
- [SQLite foreign-key initialization](https://www.sqlite.org/foreignkeys.html)
- [SQLite nullable primary keys](https://www.sqlite.org/rowidtable.html)
- [SQLite storage classes and ordering](https://www.sqlite.org/datatype3.html)
- [SQLite security for untrusted database files](https://www.sqlite.org/security.html)
- [SQLite interrupt](https://www.sqlite.org/c3ref/interrupt.html) and
  [EXPLAIN QUERY PLAN format caveat](https://www.sqlite.org/eqp.html)
- [Microsoft.Data.Sqlite connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)
  and [database errors / busy handling](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)
- [MySqlConnector local-infile security](https://mysqlconnector.net/troubleshooting/load-data-local-infile/)
  and [connection options](https://mysqlconnector.net/connection-options/)
- [MySQL identifier case rules (8.4 LTS)](https://dev.mysql.com/doc/refman/8.4/en/identifier-case-sensitivity.html)
- [MySQL `INFORMATION_SCHEMA.TABLES` timestamp limitations (8.4 LTS)](https://dev.mysql.com/doc/refman/8.4/en/information-schema-tables-table.html)
- [MySQL EXPLAIN (8.4 LTS)](https://dev.mysql.com/doc/refman/8.4/en/explain.html)
- [DuckDB security and configuration locking](https://duckdb.org/docs/stable/operations_manual/securing_duckdb/overview)
  (use the docs version that matches the pinned binding)
- [DuckDB.NET connection strings and instance caching](https://duckdb.net/docs/connection-string.html)
- [Oracle .NET drivers](https://www.oracle.com/database/technologies/net-downloads.html) and
  [Oracle.ManagedDataAccess.Core on NuGet](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core)
- [Oracle explicit transaction API](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/ConnectionBeginTransaction1.html)
- [ClickHouse C# client](https://clickhouse.com/docs/integrations/language-clients/csharp/overview)
- [CockroachDB PostgreSQL compatibility](https://docs.cockroachlabs.com/docs/stable/postgresql-compatibility),
  [transaction retry errors](https://docs.cockroachlabs.com/docs/stable/transaction-retry-error-reference)
  and [licensing FAQ](https://docs.cockroachlabs.com/docs/stable/licensing-faqs)
- [TiDB MySQL compatibility](https://docs.pingcap.com/tidb/stable/mysql-compatibility/)

Verify documentation against the exact pinned driver/server versions at implementation time. Version
facts in this plan were checked on 2026-10-31.

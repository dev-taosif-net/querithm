# SQL completion audit and roadmap — 2026-11-25

Scope: tier 1 (webview), tier 2 for all six active engines (SQL Server, PostgreSQL, MySQL/MariaDB, Oracle, SQLite,
DuckDB), and the schema metadata that feeds both. Benchmark: the feature set commonly offered by SQL editors and
completion add-ins, from basic to advanced tiers.

Method: five parallel reviews (feature survey, tier 1, T-SQL tier 2, the other engines' tier 2, metadata). The
highest-impact sidecar findings were then reproduced with a temporary xUnit probe; tier 1 findings were reproduced by
a temporary Vitest probe. Both probes were deleted. This is a bounded, mostly read-only audit, not a claim that every
defect was found. See [Verification and limits](#verification-and-limits).

Status labels used below:
- **Test** — reproduced by a probe test in this audit (sidecar: xUnit; webview: Vitest).
- **Read** — the code path was traced end to end and the defect follows from the code.
- **Suspected** — traced, but the outcome depends on engine behaviour not run here (needs an integration test).

## Status (updated 2026-11-25: Phase 0 done except the items marked open)

| Finding | State | Verified by |
|---|---|---|
| H1 exponential CTE / derived resolution | Fixed: memo per scope | `CompletionBudgetTests` (5 engines fail without the memo, 1.3–1.5 s each) |
| H2 no cancellation / deadline | Fixed: token through `IDialectService`, newest request per document wins, 3 s deadline; a superseded request stops in a median 0.23 ms (max 0.84 ms) | `CompletionBudgetTests`, `CompletionBudgetTimingTests.A_superseded_request_stops_within_50_ms` |
| H3 parse cache growth | Fixed: repair attempts uncached, declaration cache bounded | `CompletionBudgetTests` |
| M8 (found worse: a sidecar crash) | Fixed: ScriptDom on a 256 MiB stack, nesting / operator limits | `DeepStackTests` (every engine; crashed the test host before) |
| M13 per-schema rules caches | Fixed for Oracle, DuckDB and MySQL / MariaDB (which had the same growth): at most 16 per dialect, least recently used out | `Tier2AuditTests` (failed before: typing a 52-letter schema name kept one per letter) |
| H4 `SET … ON` without `;` | Fixed | `TSqlCompletionAuditTests` |
| H5 `WITH … INSERT … SELECT` | Fixed | `TSqlCompletionAuditTests` |
| H6 verb-named names split statements | Fixed (tier 1) | `auditRegressions.test.ts`, `corpus.test.ts` |
| M19 MySQL `INSERT … SET`, `CALL p(`, `CONVERT` order | Fixed (tier 1) | `corpus.test.ts`, `auditRegressions.test.ts` |
| H7 source-text columns re-quoted | Fixed (tier 1) | `auditRegressions.test.ts` |
| H8 decimal point / `[` trigger | Fixed | `providers.test.ts` |
| M9 SET targets qualified / other sources | Fixed in both tiers, every engine (T-SQL had it too); MySQL multi-table UPDATE keeps every table, qualified | `Tier2AuditTests`, `Tier2CorpusTests`, `corpus.test.ts` |
| M18 niladic functions | Fixed (tier 1) | `auditRegressions.test.ts` |
| M1 derived-table scope, M2 open CASE, `OVER (ORDER BY)` aliases | Fixed (T-SQL); M1 also fixed in tier 2 of the PostgreSQL family and in tier 1 of every engine (CTE bodies too), where the corpora found it | `TSqlCompletionAuditTests`, `Tier2CorpusTests`, `corpus.test.ts` |
| Low tier-1 items (`$$` word start, snippets in name slots / quotes, duplicate GROUP BY item, `openQuoteAt`, late tier-2 answer resetting the selection) | Fixed; the selection reset reproduced (Monaco re-focused the first row) | `providers.test.ts`, `corpus.test.ts` |
| H11 DDL in a pooled transaction | Fixed: read when the transaction ends, snapshot stale meanwhile | `MetadataFreshnessTests`, `MetadataFreshnessIntegrationTests` (PostgreSQL fixture; failed before through the toolbar's Begin / Commit) |
| H12 incremental update keeps old FKs | Fixed: `meta.foreignKeys`, FKs patched with the objects | `snapshots.freshness.test.ts` |
| H13 PostgreSQL marker misses index / key changes | Fixed: marker over `pg_index`, `pg_constraint`, `pg_attrdef` | `MetadataFreshnessIntegrationTests` (reproduced on the fixture: `CREATE UNIQUE INDEX` marked nothing changed) |
| H14 SQLite view over a dropped table | Fixed: views read one at a time | `FileEngines/SqliteMetadataTests` (reproduced: "no such table: main.t") |
| M20 FK names colliding across schemas | Fixed: store v13 keys FKs by table; SQL Server groups by constraint object | `StoreMigrationTests` (the SQL Server half is not run here: its fixture port is taken on this machine) |
| M21 loading row, retries, stuck "refreshing" | Fixed: row until the index holds objects; retry with a doubling wait (5 s → 60 s); engine restart resets the entry | `providers.test.ts`, `snapshots.freshness.test.ts` |
| M22 no freshness check while running | Fixed: incremental check every 10 min and on window focus; DDL after `USE` reads both databases | `snapshots.freshness.test.ts`, `MetadataFreshnessTests` |
| M26 SQLite FK without a column list | Fixed in both tiers: the parent's primary key; tier 1 never builds `c.""` | `FileEngines/SqliteMetadataTests`, `corpus.test.ts` |
| H9 tier-1 latency on 5,000 tables | Fixed: p95 1.71–3.45 ms on all six engines (target 5 ms) | `perf.test.ts` (`QUERITHM_PERF_STRICT=1` for p95; §14 #4b) |
| M3 500-item cap by sort text only | Fixed: prefix > camel hump > substring, then sort text | `Tier2AuditTests` |
| M4 T-SQL items built in full before filtering | Fixed: docs built for kept items only; 10,000 tables, before the filter: median 108–154 → 16–19 ms | `Tier2AuditTests`, §14 #5b |
| M16 tier-1 long text | Fixed: 458 KB line 11.6 → 0.77 ms median; incremental cache-key hash 2.6 → 0.41–0.45 ms | `perf.test.ts`, `prefixHash.test.ts`, `lexer.test.ts` |
| M23 SQLite / DuckDB refresh cost | Fixed: one grouping per read, only the asked tables read; SQLite 4,000 tables quadratic before (16 → 593 ms for 8×), DuckDB 3,200 tables 350 → 143 ms | `FileEngines/SqliteMetadataTests`, `FileEngines/DuckDbMetadataTests` (DuckDB's ratio test passed before too: a guard) |
| M15 tier-2 cache key | Fixed | `providers.test.ts` |
| M17 tier-2-only items ranked last | Fixed | `providers.test.ts` |
| S1 quoting-mode bypass of the destructive guard | Fixed for MySQL / MariaDB and PostgreSQL | `Security/MySqlSqlModeTests`, `Security/PgStringModeTests`, integration against the fixture (the server ran the hidden DROP once confirmed) |
| S2 lang.* / meta.* input bounds | Fixed: lang.* documents and names; `meta.objects`, `meta.foreignKeys`, `meta.rowCounts` at most 50,000 ids | `CompletionBudgetTests`, `Security/MetadataRpcBoundsTests` |
| S3 stored snapshot error unscrubbed | Fixed: scrubbed when stored and when sent | `Security/MetadataRpcBoundsTests` |
| S4 MySQL classifier shared state | Fixed (stateless) | `Security/MySqlSqlModeTests` |
| Test infrastructure (Phase 0 item 7) | Done: a tier-1 corpus (56–68 cases per engine) and a tier-2 corpus (44–51 per engine), Oracle in the matrix and Monaco filter tests, p95 assertions on 5,000 tables | `corpus.test.ts`, `Tier2CorpusTests`, `matrix.test.ts`, `perf.test.ts` |
| M25 SQL Server catalog read blocked up to 5 min | Open: not in a Phase 0 item, and the SQL Server fixture cannot start on this machine to reproduce it | — |
| H10 dialect clause words as aliases | Fixed (before Phase 1, as a stopgap for the release): each dialect says which words end a FROM item (`PgCompletionRules.EndsFromItem`: Oracle / MySQL / DuckDB reserved words, Oracle `SAMPLE (` / `PARTITION (` / `VERSIONS BETWEEN`, DuckDB `USING SAMPLE`, SQLite `INDEXED`); Oracle `MINUS` splits operands like UNION. Tier 1 had the reverse half: its one list for every dialect dropped PostgreSQL aliases such as `start` and `qualify`, and Oracle `AS OF` / `SAMPLE (10)` became aliases; it now has per-dialect lists too | `Tier2CorpusTests` (18 cases failed before: `START.STATUS`, `USE.status`, `MINUS.*` beside `CUSTOMERS.*`, `USING SAMPLE` as a source), `corpus.test.ts` (4 failed before) |
| M5, M6, M10–M12, M14, the T-SQL / other-engine / metadata low items | Open: Phase 1 (grammar tables, DML slot model, routine bodies, script awareness) | — |

### Review of the Phase 0 changes (2026-11-25, second pass)

A read-only review of the uncommitted Phase 0 work found gaps in some fixes above; each is fixed now with a test that
failed before it (unless noted). Details are in `docs/ARCHITECTURE.md` (§8, §9, §13 rule 13, §14 #4b, #5d, #5e, #5f,
#10d, §16).

| Finding | Was | Now |
|---|---|---|
| S1 incomplete | A SET in the script that changes how quotes are read (`sql_mode`, `standard_conforming_strings`) desynchronised every uniform reading: a short prefix hid the DROP, and the fixture's server ran it | `readingChanged` finding; PG DO bodies / EXECUTE read with the reading's escapes; transaction flags and actual-plan refusal across readings |
| M1 over-reach | A derived table hid every outer query level, so `EXISTS (SELECT 1 FROM (SELECT … c.\|) d)` lost `c` (both tiers, every engine); DuckDB is implicitly lateral | Only the derived table's own FROM level is hidden |
| H6 over-reach (tier 1) | After `WHERE` / `AND` / an operator / `label:`, the next line's statement was swallowed (completion and Run current statement) | A reserved verb on a new line starts a statement |
| M21 not live | An `error` answer built an empty index, so typing never retried, while the row said "retrying" | Retried from typing, at most 8 times, never after `needsSecret` |
| M8 gaps | Nested BEGINs, NOT runs and IFs past 187,500 were not counted; many short statements were refused for their total | Counted; chains per statement; the guard shows the reason |
| H3 follow-on | Declarations past the cache budget cleared it mid-keystroke: 645 KB of them cost 888 ms per keystroke | Entries in use are kept (§14 #5e) |
| H2 claim | "0.23 ms" was measured on a fake engine; on a 1 MB document PostgreSQL and SQLite never stopped | Token checked every 1,024 tokens, also while MySQL, Oracle and SQLite map their tokens; real engines median 0.06–8.6 ms, worst single run 21.5 ms (§14 #5d) |
| H11 / M22 follow-ons | Deferral races, a stale event reloading the whole snapshot, a database left by `USE` marked fresh, a failing patch stuck "refreshing" | Fixed in both tiers |
| H13 follow-on | `md5()` fails on FIPS servers; index renames unseen | `hashtextextended`, index `pg_class.xmin` |

Not fixed, by decision: a pool of deep-stack threads (measured gain 0.09 ms per call; a pooled thread keeps committed
stack), and a case-insensitive MySQL completion cache key (with `lower_case_table_names=0`, `Sales` and `sales` differ).
Open: §14 #5f (a cold tier-2 request on a 1 MB PostgreSQL-family document, 232–421 ms against 150 ms).

## 1. Where Querithm stands

Many completion tools cover a single engine. Querithm already does things that are not common:

- one completion model across six engines, with per-engine quoting and case folding;
- usage-learned ranking (`completion_usage`, decayed);
- PL/pgSQL body scopes (parameters, `%TYPE`, `FOR rec IN`, trigger `NEW`/`OLD`/`TG_*` variables), Oracle synonyms and
  package signatures, OPENJSON / PIVOT / VALUES projections in T-SQL;
- row counts in the list for every engine, FK- and name-based `JOIN … ON` snippets, `(all non-aggregated columns)`.

It falls behind the common feature set in breadth of context (DDL, hints, options, system catalog), in smart insertion (column picker,
INSERT/UPDATE expansion), in metadata depth (comments, indexes, synonyms' columns), and — most urgently — in
**trustworthiness**: several common patterns give no list or a wrong insertion, one PostgreSQL-family path grows
exponentially, and nothing in tier 2 can be cancelled. Being "the best" starts with fixing those.

### Feature comparison

| Feature | How common elsewhere | Querithm today | Gap |
|---|---|---|---|
| Context-aware objects / columns | Basic tier | Yes, 6 engines | Ahead on engines; bugs below |
| Prefix > CamelCase > substring filter | Basic tier | Yes; tier 2 camel hump greedy (`oid` ≠ `OrderItemId`) | Small |
| Usage-learned ranking | Rare | Yes; decay math wrong (§2 L) | Ahead |
| Typo-tolerant / FK- or type-aware ranking | Rare | No | Opportunity |
| CTE / temp tables / table variables | Basic tier | Yes (T-SQL, PG); MySQL partial; tier 1 temp-table columns T-SQL only | Medium |
| Objects created earlier in the script (`CREATE TABLE`, `ALTER … ADD`) | Mid tier | Temp tables only | Gap |
| DDL / security statement prompts | Mid tier | Types after DECLARE / CAST only; `CREATE TABLE dbo.t (Id |` offers functions | Gap |
| Hints, SET options, datepart, COLLATE, index/constraint names | Uncommon | No | Gap |
| System catalog (`sys.*`, DMVs, `information_schema`, `pg_catalog`, `V$`) | Uncommon | No (snapshot excludes system objects) | Gap |
| Cross-database / linked server | Mid tier | Database names only; `OtherDb.dbo.` lists the current database | Gap |
| Metadata auto-refresh with change detection | Mid tier | After DDL in the app only; external DDL never seen while running | Gap |
| `JOIN … ON` from FKs and matching names | Mid tier | Tier 1 yes (up to 40); FK data goes stale (§2 H12, M20) | Parity, with bugs |
| Virtual FKs | Rare | No | Opportunity |
| Wildcard expansion | Mid tier | "Expand *" code action | Small |
| Column picker (multi-select, types) | Mid tier | `(all columns of t)` item only | Gap |
| INSERT / UPDATE expansion with type comments, VALUES column hint | Mid tier | INSERT column list + VALUES placeholder; no UPDATE SET fill, no VALUES position hint | Gap |
| Snippets with placeholders | Mid tier | Yes (library, tab stops) | Parity |
| Surround-with snippets | Rare | No | Opportunity |
| Parameter info with overloads | Basic tier | T-SQL, PG, Oracle, SQLite built-ins; PG built-ins and MySQL / Oracle positional hints missing | Medium |
| Quick info with keys, indexes, description | Basic tier | Hover from snapshot; no comments on any engine; indexes not in snapshot DTO | Gap |
| Automatic aliases | Mid tier | Yes; no custom masks | Small |
| Keyword case on typing | Mid tier | Yes (option) | Parity |
| Live diagnostics with quick fixes | Advanced tier | T-SQL ScriptDom diagnostics; none for other engines | Opportunity |
| AI ghost text | Rare | No | Opportunity (offline / local model) |

## 2. Findings

### High

| ID | Engines | Problem | Repro (`|` = cursor) | Where | Status |
|---|---|---|---|---|---|
| H1 | PG, MySQL, Oracle, SQLite, DuckDB | Column resolution through chained CTEs / derived tables grows exponentially; nothing cancels it, so one request pegs a CPU and later `lang.*` calls queue | `WITH c1 AS (SELECT 1 AS a0, …a9), c2 AS (SELECT a0,…a9 FROM c1), … SELECT | FROM ck` — measured k=2: 0.7 ms, 3: 10.2 ms, 4: 36.7 ms, 5: 277.6 ms, **6: 1,515 ms** (10 columns; 20 columns is far worse) | `PgResolver.cs:31-42, 67-73, 197-222, 273-287` | Test |
| H2 | All | `lang.complete` / `lang.signature` take no `CancellationToken`, have no deadline and no size cap; superseded requests keep running. T-SQL can run up to 11 ScriptDom parses per request. Breaks the CLAUDE.md "no unbounded waits" rule | Type in a 1 MB generated script | `ObjectService.cs:186-199`, `Services.cs:16,19`, `CursorAnalysis.cs:131-164` | Read |
| H3 | SQL Server | The statement parse cache keeps every repair attempt's AST (keys never hit again), bounded only by count (4,096); `lang.forget` doesn't clear it; the eventual `Clear()` causes a latency spike | Edit a 2,000-line statement for a few hundred keystrokes | `StatementParser.cs:19,36` | Read |
| H4 | SQL Server | `SET NOCOUNT ON` without `;` swallows the next DML statement — the most common procedure preamble | `CREATE PROCEDURE dbo.p AS` / `SET NOCOUNT ON` / `UPDATE #w SET Amount = 0 WHERE |` → no columns | `DocModel.cs:264-272`, `TSqlCompletion.cs:176` | Test |
| H5 | SQL Server | `WITH … INSERT … SELECT` is split at the SELECT, so the CTE is lost (40 statements of this shape are in the §14 BigProc fixture, none tested) | `WITH c AS (SELECT OrderId, TotalAmount AS Amt FROM dbo.Orders) INSERT INTO #w (Id, Amount) SELECT c.| FROM c` → `[]` | `DocModel.cs:254-260` | Test |
| H6 | Tier 1: PG, MySQL, SQLite, DuckDB, Oracle | A column or function named like a statement verb starts a new statement (no check for a preceding `.` or a following `(`) | pg `… WHERE p.comment LIKE 'x' AND |`; MySQL `ORDER BY o.total DESC, |`; `SELECT REPLACE(|, …)`; pg `CALL refresh(|` | `scanner.ts:219-249` | Test |
| H7 | Tier 1: Oracle, PG (folding engines) | CTE / derived / PIVOT columns are re-quoted by catalog rules, inserting names that don't exist | Oracle `WITH x AS (SELECT id FROM posts) SELECT | FROM x` → inserts `"id"` (ORA-00904); pg derived `CustomerId` → `d."CustomerId"` | `complete.ts:628, 854` | Test |
| H8 | Tier 1: all | A decimal point opens the list; `[` opens it on engines without bracket identifiers | `WHERE TotalAmount > 1.|` → 106 items; at `1.5|` Enter would give `1.5CustomerId` (acceptance inferred) | `providers.ts:323-328, 638` | Test |
| H9 | Tier 1: all | Large-schema latency about 10× the §14 #4 budget (p95 ≤ 5 ms): every candidate gets detail and docs before the 500 cut; FK lookup scans all FKs per column | 5,000 tables × 21 columns, 3,000 FKs: `FROM |` median 50.6 / p95 58.6 ms. Measured before the in-progress lazy-doc change; re-measure | `complete.ts:530-552`, `schemaIndex.ts:107` | Test |
| H10 | Oracle (high); DuckDB, MySQL, SQLite | Dialect clause words become table aliases (one PostgreSQL stop-word list for every engine) | Oracle `… FROM emp START WITH … CONNECT BY …` → `"START".ENAME`; `MINUS` scopes; DuckDB `QUALIFY`, `ASOF`, `USING SAMPLE`; MySQL `USE INDEX`, `PARTITION (p0)`, `STRAIGHT_JOIN`; SQLite `INDEXED BY` | `PgScopeBuilder.cs:55-59, 101, 131` | Read |
| H11 | PG (pooled engines) | DDL inside an open transaction refreshes at once on another connection, which can't see it, and marks the snapshot fresh; the commit never refreshes | Begin; `CREATE TABLE t …`; Commit → `t` never completes until manual refresh | `RpcServer.cs:62-66`, `MetadataService.cs:309-336` | Read |
| H12 | All (tier 1) | Incremental snapshot updates keep the old FK list: new FKs never reach JOIN suggestions, dropped tables' FKs stay | `ALTER TABLE … ADD FOREIGN KEY …` then `JOIN |` | `src/store/snapshots.ts:292` | Read |
| H13 | PG | Incremental refresh misses index / PK / FK changes (marker hashes `pg_class.xmin` + `pg_attribute` only) | `CREATE UNIQUE INDEX …` or a second FK → "0 changed" | `PostgresMetadata.cs:31` | Suspected |
| H14 | SQLite | One view over a dropped table fails every refresh of that file | `CREATE VIEW v AS SELECT * FROM t; DROP TABLE t;` then any DDL | `SqliteMetadata.cs:91-97` | Suspected |

### Security (outside completion, found on the way)

| ID | Problem | Where | Status |
|---|---|---|---|
| S1 | The MySQL language service is always built with the default lexer mode, so the destructive classifier assumes backslash escapes on every session. Under `NO_BACKSLASH_ESCAPES`, `SELECT 'a\'; DROP TABLE t; -- '` classifies as one SELECT but the server reads a DROP — a possible bypass of `confirmedDestructive` / the production guard. Whether the server actually runs the DROP depends on how the batch is sent | `MySqlProvider.cs:26` (`new MySqlLanguageService()`); lexer `MySqlTokenizer.cs:91` | Lexer part Read; execution Suspected — needs a `QUERITHM_IT` MySQL test |
| S2 | `lang.signature` validates nothing; `lang.diagnostics` / `lang.folding` accept any docId length as a cache key; `lang.complete` doesn't bound ProfileId / Database / SessionId / Schema; `meta.objects` / `meta.rowCounts` id arrays have no size check (§13 rule 7) | `ObjectService.cs`, `RpcServer.cs` | Read |
| S3 | `schema_snapshot.error` is stored and returned unscrubbed (the log line and event text are scrubbed) | `MetadataService.cs:208, 439` | Read |
| S4 | `MySqlLanguageService.Classify` keeps the text in an instance field of a singleton: two concurrent classifications can report findings against the wrong script | `MySqlLanguageService.cs:65, 225` | Read |

### Medium

| ID | Engines | Problem | Where | Status |
|---|---|---|---|---|
| M1 | SQL Server | A derived table sees outer sources and the parser placeholder: `JOIN (SELECT | FROM dbo.Orders) d` offers `c.CustomerId`, `c.FullName`, `d.__q__` | `TSqlCompletion.cs:320-376, 451-453`, `Scope.cs:155-156, 284` | Test |
| M2 | SQL Server | Inside an unclosed `CASE` / `(` the repair ladder loses FROM: `SELECT CASE WHEN o.| FROM dbo.Orders o` → `[]` | `CursorAnalysis.cs:105, 137-163` | Test |
| M3 | SQL Server | The 500-item cap orders by SortText only, so a prefix match (`Orders`) can be cut in favour of substring matches on a large schema | `CompletionFilter.cs:22-23` | Read |
| M4 | SQL Server | Every item is built in full before filtering (column previews for 10k tables); §14 5b measured p95 128–172 ms against 150 ms | `TSqlCompletion.cs:538-562`, `Scope.cs:233-245` | Read |
| M5 | SQL Server | Synonyms, table-type variables and TVP parameters have no columns (`sys.synonyms.base_object_name` never read) | `SqlServerMetadata.cs`, `Declarations.cs:176-179`, `Scope.cs:149-153, 223-227` | Read |
| M6 | SQL Server, PG | Name resolution: `USE` in the script ignored; unqualified names go dbo-first then any schema; default schema hard-coded (`dbo` / `public`), so a login's default schema or `search_path` is wrong | `SchemaIndex.cs:34-46, 91-98`, `engines.ts:69, 83` | Read |
| M7 | SQL Server | Batch splitting is quadratic in the number of `GO`s (runs on every classify and diagnostics) | `TSqlParsing.cs:115-126` | Read |
| M8 | SQL Server | Recursive AST containment walk on a 5k-term `OR` chain could overflow the stack and kill the sidecar | `CursorAnalysis.cs:212-227` | Suspected |
| M9 | PG, SQLite, DuckDB (tier 1 and 2) | SET targets in `UPDATE … FROM` / MERGE come qualified and include other sources (PG rejects `SET o.status`): `UPDATE orders o SET | FROM customers c` → `o.id, …, c.full_name` | `complete.ts:803`, `PgTier2Completion.cs:160-165, 236` | Test |
| M10 | PG, Oracle, DuckDB | `MERGE … USING (subquery) s` never registers `s` | `PgScopeBuilder.cs:131` | Read |
| M11 | PG, MySQL, SQLite, DuckDB | Upserts get nothing: `ON CONFLICT … DO UPDATE SET x = excluded.|` → `[]`; `ON DUPLICATE KEY UPDATE |` → `[]`. A test asserts the gap as correct (`PgTier2CompletionTests.cs:119`) | `PgTier2Completion.cs:155`, `PgScopeBuilder.cs:129` | Test (PG) |
| M12 | MySQL, SQLite, Oracle | Routine bodies: MySQL stored programs and SQLite triggers are one statement; no parameters / variables (MySQL, SQLite, Oracle); `NEW.` / `OLD.` columns missing on every engine | `PgCompletionContext.cs:84-97`, `MySqlCompletion.cs:39-50` | Read |
| M13 | Oracle, DuckDB | Rules caches grow per keystroke while a schema name is typed (`HR`, `HR_`, …), each with a full schema index | `OracleCompletion.cs:193-221`, `DuckDbLanguageService.cs:346-365` | Read |
| M14 | MySQL, SQLite, DuckDB | `INSERT IGNORE INTO t (|`, `INSERT OR IGNORE INTO t (|` → `[]` | `PgTier2Completion.cs:236, 288`, `PgScopeBuilder.cs:130` | Read |
| M15 | Tier 1 | Tier 2's cache key ignores the text after the cursor: change `FROM dbo.Orders x` to `FROM dbo.Customers x` and `x.|` still shows Orders items | `providers.ts:140-167` | Test |
| M16 | Tier 1 | Long text: one 458 KB line max 22.6 ms; 12k-line script `FROM |` p95 8.0 ms; `tier2Key` hashes the whole prefix twice per keystroke (1.0 / 2.6 ms) | `lexer.ts:537-549`, `complete.ts:350, 373`, `providers.ts` | Test |
| M17 | Tier 1 | Tier-2-only items always rank below tier-1 items (different filterText / sortText schemes) | `providers.ts:392-411` | Test |
| M18 | Tier 1 | Functions without parentheses are inserted with them: Oracle `SYSDATE()`, pg `current_date()` | `complete.ts:661` | Test |
| M19 | Tier 1: MySQL, Oracle | MySQL `INSERT INTO t SET |` → no columns; `CALL p(|` offers procedures; MySQL `CONVERT(expr, type)` argument order reversed | `scanner.ts:239, 1295, 1298, 1546` | Test |
| M20 | SQL Server, Oracle | FKs with the same name in two schemas collide (store key is `(profile, database, name)`); on SQL Server the survivor can carry both column lists | `Schema.sql:56-60`, `MetadataStore.cs:146`, `SqlServerMetadata.cs:119-126` | Read |
| M21 | All | "Loading schema…" disappears as soon as an empty index exists; a failed first load is never retried; a sidecar restart during a refresh leaves the entry "refreshing" forever | `snapshots.ts:203, 215, 231, 358`, `providers.ts:426`, `actions.ts:1341` | Read |
| M22 | All | No freshness check while the app runs (external DDL never seen); DDL on another database refreshes only the current one | `snapshots.ts`, `RpcServer.cs:65` | Read |
| M23 | SQLite, DuckDB | Refresh is O(tables × columns) and holds the tab's exec lock | `SqliteMetadata.cs:70, 113, 148`, `DuckDbMetadata.cs:113` | Read |
| M24 | All | The whole snapshot crosses IPC as JSON (estimate ~30 MB at 10k × 30), with full reloads on > 300 changes; no §14 row above 600 tables | `snapshots.ts:277, 312` | Read |
| M25 | SQL Server | A catalog read blocked by another session's open DDL transaction waits up to 5 minutes (no `LOCK_TIMEOUT`) | `MetadataService.cs:352` | Suspected |
| M26 | SQLite | An FK written without a column list (`REFERENCES customers`) yields the snippet `o.customer_id = c.""` | `SqliteMetadata.cs:173`, `complete.ts:684-688` | Read |

### Low (grouped)

- **T-SQL:** select-list aliases offered inside `OVER (ORDER BY |)` (Test); no types in `CREATE TABLE dbo.t (Id |`;
  `EXEC OtherDb.dbo.|` and `Srv.Db.dbo.|` resolve locally; items de-duplicated by label across kinds (a `Year` column
  hides `YEAR()`); alias / temp-table / cursor labels not quoted; greedy camel hump; usage score = lifetime count ×
  decay since last use (one new use restores a year of weight — store a decayed score instead); `lang.forget` misses
  text-keyed caches; temp tables created inside a proc body leak to later batches.
- **Other engines:** `emp@remote` resolves to the local table; DuckDB `* EXCLUDE / REPLACE` and `COLUMNS()` give no
  columns; SQLite `json_each` gets one fake column; `JOIN b USING (|` offers qualified names; PG folds non-ASCII
  (PG lowers ASCII only); MySQL `lower_case_table_names` and `sql_mode` (ANSI_QUOTES) ignored, contradicting §16;
  swallowed exceptions in `PgTier2Completion.cs:16-29` are not logged.
- **Tier 1:** pg `$$` word start can land inside a string literal (accepting deletes text); library snippets in
  alias slots and inside an opened bracket; duplicate GROUP BY item; `openQuoteAt` misreads a closing quote; a late
  tier-2 answer may reset the selected row (Suspected).
- **Metadata:** PG extension functions (PostGIS, pg_trgm) excluded; PG enums / domains / composites missing; SQL
  Server parameter defaults always false; same-named function and table collide in `schemaIndex.ts:22`; SQL Server
  index columns joined and split on `,`.
- **Tests:** Oracle missing from `matrix.test.ts` and `monacoFilter.test.ts`; `perf.test.ts` asserts the median
  (§14 says p95) on a 14-object schema, T-SQL only; no dialect-syntax corpus for MySQL, SQLite, DuckDB, Oracle; no
  cancellation or memory-bound tests for tier 2.

## 3. Roadmap

Every phase follows the CLAUDE.md slice order (contract → sidecar + xUnit → UI + Vitest) and covers all six engines.
Each item lists the finding IDs it closes. Phase exit criteria are measured, not adjectives.

### Phase 0 — Trust (correctness, safety, budgets)

Goal: no empty list or wrong insertion on common SQL, no runaway request, no stale schema.

1. **Cancellation and work budgets** (H1, H2, H3, M8, M13). Add a `CancellationToken` to `IDialectService.Complete` /
   `Signature`; newest request per docId cancels the previous one; a statement-size cap above which T-SQL uses
   `TokenScopes` only; a deadline wrapper. Memoize CTE / derived columns per `PgResolver`. Stop caching placeholder
   parses; keep a byte-budgeted declaration cache that `lang.forget` clears. Bound the Oracle / DuckDB rules caches.
2. **Statement segmentation** (H4, H5, H6, M19). T-SQL: `SET <option> ON|OFF` is a complete statement; `mainKw ==
   "INSERT"` keeps the SELECT. Tier 1: never split after `.`, inside parentheses, or on a verb followed by `(`; no
   `DESC` boundary on MySQL; `INSERT … SET`.
3. **Insertion correctness** (H7, H8, M9, M18, low tier-1 items). Carry a quoted flag for projected columns; no list
   after a number's `.`; `[` trigger only for T-SQL and SQLite; SET-target slot offers only target columns, bare;
   no-parentheses flag in the function catalog.
4. **Metadata freshness** (H11–H14, M20–M22, M26). Patch FKs with `meta.objects`; FK key `(parent_object_id, name)`
   (store migration); PG marker over `pg_constraint` / `pg_index` / `pg_attrdef`; deferred refresh on
   `TransactionEnded` for pooled sessions; read SQLite views one by one; status-driven loading row with retry and
   backoff; reset on sidecar restart; cheap `meta.snapshot` freshness check on focus and every 10 minutes.
5. **Latency at scale** (H9, M3, M4, M16, M23). Rank names first, then build detail / docs for the kept 500 (tier 1
   and T-SQL tier 2); FK index by parent; binary search in `mayStartStatement` / `aliasSlotFree`; hash the statement,
   not the prefix; order the 500 cap by match tier; dictionaries in SQLite / DuckDB refresh.
6. **Security** (S1–S4). Read the session's `sql_mode` and pass it to the MySQL classifier and tier 2 (with a
   `QUERITHM_IT` test proving the guard holds under `NO_BACKSLASH_ESCAPES`); size / shape checks on every `lang.*` and
   `meta.*` parameter with Security-suite tests; scrub the stored snapshot error; make MySQL `Classify` stateless.
7. **Test infrastructure.** A completion corpus per engine — at least 40 snippets each with "must offer" and "must not
   offer" lists, including every repro above as a regression case; Oracle in the matrix tests; p95 assertions on a
   5,000-table fixture.

**Exit criteria:**

- Every H, M and S finding has a regression test. The test fails before the fix and passes after it.
- §14 #4 (tier 1) is at or under 5 ms p95 on 5,000 tables × 21 columns. Today it is 58.6 ms.
- §14 #5b (T-SQL tier 2) is at or under 150 ms p95. Today it is 128–172 ms.
- A new §14 row covers PostgreSQL-family tier 2 on 8 chained CTEs × 30 columns, with a target of at most 50 ms p95. Today 6 × 10 takes 1,515 ms.
- A superseded request stops within 50 ms, and a test proves it.

### Phase 1 — Parity with the common feature set, on six engines

1. **Per-dialect grammar tables** in `PgCompletionRules` shared with the reference finders (H10, M14): stop-words,
   set operators (`MINUS`), join words (`ASOF`, `SEMI`, `ANTI`, `STRAIGHT_JOIN`), table hints, INSERT modifiers.
2. **DML slot model** (M9–M11, low items): SET targets, MERGE source / INSERT list, `excluded.` / MySQL row alias /
   `VALUES()`, `USING` / NATURAL merged columns.
3. **Routine bodies everywhere** (M12): Oracle's body cutter as a rules hook; dialect block scanners for MySQL / SQLite
   / Oracle variables, parameters, cursors; `NEW` / `OLD` columns from `CREATE TRIGGER … ON t` on every engine;
   T-SQL table types / TVPs and synonym targets (M5).
4. **Script awareness** (M6): objects created or altered earlier in the script join completion (marked as "from
   script"); `USE` tracking in T-SQL; the session's default schema / `search_path`; other databases' snapshots loaded
   on their qualifier; linked-server names not resolved locally.
5. **Context breadth**: DDL (column types in `CREATE TABLE`, `ALTER COLUMN` targets, index / constraint / statistics
   names, object lists for `DROP` / `ALTER` / `GRANT`), table and query hints, SET options, datepart words, COLLATE
   names, CONVERT styles, sequences (`NEXT VALUE FOR`, `.NEXTVAL`).
6. **Bundled offline catalogs** per engine and version: system objects (`sys.*`, DMVs, `information_schema`,
   `pg_catalog`, `V$`, `duckdb_*()`) and built-in functions with signatures, arity, overloads and the no-parentheses
   flag. This feeds signature help for PG built-ins and MySQL / Oracle positional hints.
7. **Metadata depth**: comments / descriptions on all engines (MS_Description, `pg_description`, MySQL `COMMENT`,
   Oracle `ALL_*_COMMENTS`, DuckDB `comment`), indexes and unique keys in the snapshot DTO, PG enums / domains, SQL
   Server parameter defaults (from ScriptDom over cached definitions), extension functions marked as such.
8. **Smart insertion**: column picker (multi-select with types, in SELECT / GROUP BY / INSERT); INSERT and UPDATE
   expansion with per-column type / NOT NULL / default comments; the VALUES position hint (current column in bold);
   inline and whole-document `*` expansion; `CASE … END`, `OVER (PARTITION BY … ORDER BY …)` snippets; custom alias
   masks.

**Exit:**

- Every row marked "Gap" in the §1 table is closed, on all six engines or with a visible, tested degradation where an engine lacks the feature.
- The corpus has at least 100 cases per engine and passes.

### Phase 2 — Beyond parity

1. **One error-tolerant structural parser for the non-T-SQL dialects** (C#, dialect-parameterized: query blocks,
   FROM items with aliases and column lists, CTEs, DML targets and clauses, routine blocks, shallow expressions).
   Completion, the reference finders, result-source tracing and lightweight diagnostics share it, which ends today's
   three separate FROM-item parsers and their drift (the root of H10). ScriptDom stays for T-SQL. libpg_query is not
   recommended: native code, PostgreSQL only, and it fails on incomplete SQL.
2. **Ranking that understands the query**: after `a.customer_id = |` the FK partner key first; type compatibility;
   nearest table; correctly decayed usage; typo tolerance (edit distance 1, snake_case humps). All local, no network.
3. **Join intelligence**: virtual FKs (user-defined, and inferred from naming rules), a two-way FK graph, two-hop join
   paths, joins that know CTEs and temp tables.
4. **Column-first completion**: type a column whose table is not in FROM; accepting it adds the table (and join) to
   FROM.
5. **Live diagnostics with quick fixes on every engine**, using the shared parser:
   - Diagnostics: unresolved name, ambiguous column, a qualified SET target, a column missing from GROUP BY.
   - Quick fixes: qualify, add to GROUP BY, add the JOIN.
6. **Surround-with snippets** (`$SELECTEDTEXT$`) and postfix completion.
7. **Large-schema mode**: table names first, columns per object on demand (or columnar arrays), and per-session file-database views sending only their own objects. Target: 100k objects with an unchanged keystroke budget.

### Phase 3 — Verified AI completion (optional, opt-in)

Schema-grounded ghost text from a **local** model by default. A cloud model is possible only through the PII scrubber, with explicit opt-in (CLAUDE.md §3).

The differentiator is that every suggestion passes through tier 2 before it is shown:
- Each identifier must resolve.
- Unknown names are marked.
- Destructive statements are never auto-accepted.

Accepting can be done word by word or line by line. This needs:
- a §2 library row;
- a §13 / §16 entry for any new capability;
- a §14 latency row.

## 4. How to prove "best"

Claims of "fast" or "best" need measurements (CLAUDE.md §4). Add a completion benchmark next to `pnpm bench`:

- **The corpus:** real-world queries per engine, replayed one keystroke at a time.
- **Accuracy:** the share of positions where the intended item is first, and in the top 5.
- **Keystrokes saved:** the share of characters the user did not have to type.
- **Wrong insertions:** the number of accepted items that would make the statement invalid. This must be 0.
- **Empty lists:** the number of positions that show no list where one was expected.
- **Latency:** tier 1 and tier 2 p95, measured on the §14 fixture plus the 5,000-table and chained-CTE fixtures.

Record each run in `bench/results/`. A regression of more than 20 % fails the change, like the other critical paths.

## Verification and limits

**Run in this audit:**
- A temporary xUnit probe with 8 cases.
  - T-SQL H4, H5, M1 and M2 reproduced, plus the low `OVER (ORDER BY)` item.
  - `SET NOCOUNT ON` followed by a SELECT works.
  - PG H1 timings, M9 and M11 reproduced.
- Temporary Vitest probes by the tier 1 reviewer (H6–H9, M15–M19).
- Both probes were deleted.

**Not run:**
- The full `pnpm test`.
- `QUERITHM_IT=1` integration tests (Docker fixture not started), so S1 execution, H13, H14 and M25 stay Suspected.
- `pnpm bench` and the UI smoke.

**Other limits:**
- The latency numbers are single runs on this development machine, not on the §14 fixture. They swing with background load.
- Another change to `complete.ts`, `types.ts`, `providers.ts`, `TSqlCompletion.cs` and their tests (lazy item docs, schema in the item description) was in progress in the working tree during the audit. H9 was measured before it and must be re-measured after it lands.

**Phase 0 verification (2026-11-25, this machine):**
- `pnpm test`: Vitest 254 files, 6,850 tests passed; xUnit 5,251 passed, 396 skipped (the `QUERITHM_IT` integration
  tests), 0 failed.
- `QUERITHM_IT=1 dotnet test sidecar` with the PostgreSQL, MySQL / MariaDB and Oracle fixtures up: 5,630 passed, 130
  failed. Every failure is a SQL Server case: another project's container holds the port the SQL Server fixture binds
  (14333), so those tests reach that server and fail with "Login failed for user 'sa'". The SQL Server half of M20 (FKs
  grouped by constraint object) is therefore not run against a server.
- `pnpm bench` per engine (`bench/results/2026-11-25-08-30-52`, `08-33-20`, `08-34-11`, `08-34-35-windows.json`): MySQL,
  Oracle, SQLite and DuckDB, 0 regressions; every metric within target except 23.mysql (import, 2,925 ms against 2,000 ms;
  the §16 measured baseline, a path this change does not touch). #5 tier-2 p95: mysql 16.2, oracle 23.6, sqlite 13.0,
  duckdb 16.2 ms; #4.oracle 0.2 ms. The engine and UI metrics (SQL Server, PostgreSQL #27) and the UI smoke need the SQL
  Server fixture and were not run.
- Each regression test was run before its fix and failed, except the DuckDB stamps ratio (a guard: the catalog query
  dominated at the size tested) and the new APIs whose absence was the bug (`meta.foreignKeys`, `checkFreshness`,
  `engineRestarted`).
- Exit criteria: §14 #4 p95 1.71–3.45 ms on 5,000 tables (≤ 5 ms); #5b p95 55.5–62.4 ms (≤ 150 ms); #5c p95 at most
  11.3 ms on a quiet machine (≤ 50 ms; a busy one read 61 ms once, so it runs in the `timing` collection); a superseded
  request stops in a median 0.08–0.23 ms (≤ 50 ms). M25 has no regression test: it stays open.


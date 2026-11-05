# S01 — Driver gate, fixture, hidden engine skeleton

**Goal:** prove the driver can ship (license, trimming, size, platforms), stand up a pinned Oracle fixture, and add
`Engine.Oracle` as a hidden engine that compiles end to end with every feature refused with a reason.

**Prerequisites:** owner answer to O2 (TCPS fixture) or take the default.

## Scope

1. **Driver gate (stop the session and report if any check fails — the plan says the engine then does not ship):**
   - Latest `Oracle.ManagedDataAccess.Core` 23.26.x: pin in `sidecar/Directory.Packages.props`; record the version in STATUS.md.
   - License: copy the "Oracle Free Distribution, Hosting, and Use Terms" and the third-party notices from the
     package's `info.txt` (MIT-Kerberos etc.) into `THIRD-PARTY-NOTICES.md`; `scripts/gen-notices.mjs` does not cover them.
   - Transitive deps: list them; license + maintenance + network/reflection behaviour of each (CLAUDE.md §3 "Review Dependencies").
   - `dotnet list sidecar package --vulnerable --include-transitive` clean.
   - Trimming: root the assembly untrimmed in `Querithm.Host.csproj` (trimming would modify a redistribute-unmodified binary).
   - Measure installer-size delta (metric 17) on the local RID; record before/after.
2. **Fixture** in `scripts/bench/docker/compose.yml`: `container-registry.oracle.com/database/free` pinned by exact tag
   **and** digest; admin password generated at start (`scripts/fixture/fixture-secrets.mjs`) or fixture-only in
   compose; init script creating a least-privileged test user (CREATE SESSION + own-schema objects only) and a second
   user with `SELECT_CATALOG_ROLE` / `V$SESSION` grants for monitor/plan tests. TCPS listener with a CA generated at
   fixture start (O2). Healthcheck that waits for the PDB to open.
3. **Skeleton:**
   - `Engine.Oracle` in `sidecar/Querithm.Contract/Core.cs`; `pnpm gen:contract`.
   - Projects `sidecar/Querithm.Providers.Oracle` and `sidecar/Querithm.Intelligence.Oracle` added to the solution.
   - `OracleProvider : IDbProvider` registered as **hidden** (see `EngineRegistryTests`); `FeatureRefusal` returns a
     reason for every `Feature` until later sessions enable it.
   - `ProfileRules` (Core/Sessions), `Program.cs` self-test entry (as MySQL/DuckDB have), `ArchitectureRulesTests` still green.
   - Webview exhaustiveness: `src/engines.ts`, `src/editor/languages.ts`, `src/editor/providers.ts`,
     `src/connections/profileList.ts` compile with an Oracle branch (placeholder: generic SQL, hidden).
4. **Driver smoke:** one integration test (`Integration/OracleIntegrationTests.cs`, `QUERITHM_IT=1`) that opens a
   connection to the fixture and runs `SELECT 1 FROM DUAL`.

## Tests
- `EngineRegistryTests`: Oracle hidden, absent from auto-detection.
- TS exhaustiveness (`engines.test.ts`).
- Trimmed single-file self-test passes with the Oracle assembly loaded.
- Integration: driver smoke above.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar --filter Oracle` · `dotnet list sidecar package --vulnerable --include-transitive` · trimmed Host self-test.

## Done when
Driver pinned and documented, fixture starts from a clean Docker state with one command, Oracle hidden engine
compiles in sidecar + webview, every Oracle feature refuses with a reason, STATUS.md has the versions and size delta.

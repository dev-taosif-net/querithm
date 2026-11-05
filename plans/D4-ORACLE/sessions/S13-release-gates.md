# S13 — Release gates, bench, spec updates, un-hide decision

**Goal:** pass the Phase D release gates for Oracle and hand the un-hide decision to the owner.

**Prerequisites:** S12 (no **gap** in the feature matrix).

## Scope

1. **Bench** (`scripts/bench/run.mjs` `--oracle`, §14 rows + "How measured"): 11b, 15, 9, 10; 8 n/a (no database
   switching, reason); 20–24 and 28–32 where implemented; 4 and 3 with the Oracle dialect fixture; 5 / 27 tier 2.
   Rerun 1, 2 and 17. A target miss needs a §16 measured-baseline row.
2. **Installer size** (metric 17) per platform with all shipped providers: ≤ 120 MB (Linux AppImage ≤ 150 MB).
3. **Trimmed release smoke** on every supported RID (CI `release-smoke`); Intel macOS: app starts, Oracle shows its
   platform reason (no startup failure). Unsupported RIDs must not fail app startup.
4. **Audits:** `dotnet list sidecar package --vulnerable --include-transitive`, `cargo audit` (if Rust changed),
   license re-check of the pinned driver version.
5. **Spec updates** (the "Spec updates" list at the top of PHASE-D-ENGINE-EXPANSION.md): `docs/ARCHITECTURE.md` §2
   row, §3/§4 projects, §5 type mapping, §6 rule 2 + database-switching table (schema ≠ database), §7 transactions
   (DDL implicit commit), §8 marker row, §11 plans, §13 rules 1–3 (scrubber keys, TCPS default, auth kinds), §14 rows,
   §16 rows (TNS_ADMIN ignored, wallet deferred, packages scope, bulk copy not used, any baseline); release note in
   `docs/release-notes/`.
6. **Status docs:** copy the feature matrix into `plans/PHASE-D-IMPLEMENTATION-STATUS.md` (D4 column), mark D4 there.
7. **Un-hide (only after the owner decides):** picker, connection-string auto-detection, README engine list,
   CLAUDE.md active-engine line (`Oracle` joins the list).

## Gate
Full: `pnpm test` · `QUERITHM_IT=1 dotnet test sidecar` · `node scripts/bench/smoke-ui.mjs out/` · `pnpm bench` ·
audits · CI release-smoke.

## Done when
All gates green and recorded in STATUS.md; owner decision on un-hiding recorded.

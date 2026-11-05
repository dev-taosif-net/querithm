# S08 — Package metadata contract

**Goal:** packages are first-class objects (spec + body) in the contract, store, explorer and scripting — see
"Package metadata contract" in PHASE-D-ENGINE-EXPANSION.md § D4.

**Prerequisites:** S05. Owner answer to O3. If O3 = "unsupported for now", replace this session with: feature-matrix
row "Packages: degraded" + a test that the explorer shows a "packages not supported yet" node with the reason, and
skip to S09.

## Scope

1. **Contract** (`sidecar/Querithm.Contract/Core.cs`): `ObjectKind.Package` and `ObjectKind.PackageBody` (or one kind
   with a part flag — decide and record), stable object identity, an explicit spec ↔ body relationship (not
   `ParentObjectId`, which stays trigger-only). `pnpm gen:contract`.
2. **Other engines:** the new kinds never appear for them; every exhaustive switch in sidecar + webview handles them
   (Oracle-only), keeping trigger-parent behaviour unchanged.
3. **Metadata:** packages from `ALL_OBJECTS` / `ALL_PROCEDURES` (members listed for display only); restricted body
   visibility (other schema without `EXECUTE`/`DEBUG`) → spec only + reason.
4. **Persistence:** metadata cache / `store.db` — bump `PRAGMA user_version` with a forward migration in
   `LocalStore.cs`; invalidate incompatible cached snapshots.
5. **Explorer:** "Packages" category, spec and body nodes; open definition; script whole object (spec, body, both);
   Modify / Apply as whole objects.
6. **Out of scope (visibly unavailable with reason):** package-member completion, overload-aware navigation, rename,
   compare of package members.

## Tests
Spec + body with the same name; package without body; overloaded members in definitions; restricted body visibility;
cache round trip after migration; whole-object script routing; other engines' snapshots unchanged
(existing tests green). Vitest: explorer category, menus.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar` (all engines — contract change) · migration test.

## Done when
Packages browse and script as whole objects; member-level features show their reason.

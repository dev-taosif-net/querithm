# Phase F — DevOps: Headless CLI and Team Snippet Folders

Goal: run schema compare / drift checks in CI without the UI, and share snippets/scripts through a
folder the team keeps in git.

Slices: **F1** `querithm-cli` → **F2** CI examples and packaging → **F3** team snippet folders.

---

## F1 — `querithm-cli`

### Project
- `sidecar/Querithm.Cli/` (new console app) referencing `Querithm.Core` and the provider projects.
  No StreamJsonRpc, no data-plane server. Trimmed self-contained single file per RID, like the Host.
  Per-RID builds carry the native libraries of file engines (DuckDB) and mark RIDs a driver does not
  support (Oracle driver gate, Phase D4) as unsupported for that engine.
- Argument parsing by hand or `System.CommandLine` (if added: §2 row, pin, notices, trimming check).

### Commands
```
querithm-cli compare-schema  --source <conn.json> --target <conn.json> [--schemas a,b] [--out script.sql] [--fail-on-diff]
querithm-cli compare-data    --source ... --target ... --table s.t [--out sync.sql] [--max-diff-rows N]
querithm-cli snapshot        --conn <conn.json> --out schema.qschema.json
querithm-cli drift           --conn <conn.json> --baseline schema.qschema.json [--fail-on-diff]
querithm-cli run             --conn <conn.json> --file script.sql [--allow-destructive]
```
- Connection files contain **no secrets**: password from env var named in the file
  (`"passwordEnv": "DB_PASSWORD"`) or from the OS keyring if run on a desktop. Never accepted as a CLI
  argument (shell history). Log scrubber active.
- File engines (SQLite, DuckDB): the connection file names a `file` path, opened read-only unless
  `--allow-write` is passed; the CLI has no grant registry (user-invoked tool, see Filesystem). Memory
  databases are not applicable to the CLI.
- Output JSON (`--format json`) or human text; exit codes: 0 no diff, 1 diff found, 2 usage error,
  3 connection error, 4 guard refused, 5 not supported for this engine (capability refusal with reason).
- `run` keeps the sidecar guards: destructive statements need `--allow-destructive`; Prod environment
  needs `--allow-prod`.
- `.qschema.json`: versioned, validated on read (size ≤ 100 MB, version check) — untrusted input.

### Filesystem
The CLI is a user-invoked tool, not the sandboxed sidecar: it writes only to `--out` paths the user
passes. Add a §16 row stating this boundary.

### Tests
- xUnit: argument validation, exit codes, secrets refused as arguments (Security test), env-var lookup,
  `.qschema.json` malformed/oversize refused.
- Integration (`QUERITHM_IT=1`; file engines without Docker), every active engine: compare fixture db
  with a modified copy → exit 1 and a script that, applied, makes the next run exit 0. Engines whose
  compare apply is refused by capability (e.g. ClickHouse in D5, SQLite schema compare if D1 degrades it)
  exit 5 with the reason instead of emitting a script.

## F2 — CI packaging
- Release workflow builds `querithm-cli` for win-x64, linux-x64, osx-arm64 alongside installers;
  signed/checksummed like the app.
- `docs/cli.md` with a GitHub Actions example (Docker service DB, `drift --fail-on-diff`).

## F3 — Team snippet folders
- Extends A3: Settings → "Snippet folders" (user picks folders via Tauri dialog; read via the existing
  picked-path mechanism). Files: `*.sql` with a front-matter comment header (`-- title:`, `-- prefix:`,
  `-- dialect:`), parsed by the dialect tokenizer (comment tokens), not regex.
- Read-only in the app by default; "Edit" opens the file as a normal script tab; saving uses the Tauri fs
  path the user picked. Git is used by the user outside Querithm (no bundled git, no network).
- Folder watch (Rust `notify` would add a crate — instead re-scan on window focus / manual refresh).
- Conflicts: same prefix in local and team snippets → team entry shown with folder name, both available.
- Tests: header parsing, invalid files skipped with a visible list of errors, ≥ 2,000 files scan time.

## Performance
- §14 row **36** (next free number after Phase E's 35): "CLI compare of the fixture database with itself — process start to exit ≤ 4 s"
  (metric 21 + startup).

## Phase F done when
- CLI binaries in releases, CI example verified against the Docker fixture, team folders working on all OSes.

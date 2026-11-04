# Querithm Architecture Spec

Source: "SQL IDE Architecture Spec", Oct 19 2026, @dev-taosif-net. The original uses the working
name *SqlForge*; this copy uses the product name **Querithm** everywhere (namespaces
`Querithm.*`, identifiers `querithm`). Deviations from the original are listed in §16 and
nowhere else.

## 1. Purpose and decisions

This spec is the single source of truth for building the SQL IDE. Run the milestones in §15
in order. Every decision below is final unless a Phase 1 benchmark in §14 fails its gate.

| Decision | Choice | Why |
|---|---|---|
| Desktop shell | Tauri v2 (Rust, thin) | Native webview, small binary, no SQL logic in Rust |
| Backend engine | .NET 10 sidecar process | ScriptDom is the only full T-SQL parser; Microsoft.Data.SqlClient is the only driver with Entra/Kerberos/Always Encrypted |
| Editor | Monaco | Multi-cursor, folding, minimap, diff editor out of the box |
| Results grid | Glide Data Grid | Canvas rendering; DOM tables cannot hold 1M rows |
| Rows over IPC | Apache Arrow IPC stream, never JSON | Bounded memory, zero-copy columnar slices |
| Local store | One SQLite file | Metadata cache, saved scripts, history, connection profiles (no secrets) |
| Secrets | OS keyring for timed and indefinite retention; memory for connection/app sessions | Windows Credential Manager, macOS Keychain, Linux Secret Service |
| SQL Server "debugging" | Extended Events trace, labeled **Trace** | The engine has no step debugger since SSMS 18 |
| PostgreSQL debugging | pldebugger extension, shown only when detected | Only true step debugger available |
| Pure-Rust backend | Not in v1 | sqlparser-rs cannot parse procedural T-SQL |
| Avalonia / single C# stack | Rejected | Loses Monaco |

**Not decided, measured in Phase 1:** whether Monaco + Glide in WebKitGTK (Linux) meets the
typing-latency gate. If it fails, the Linux build ships with reduced minimap/decorations before
any stack change is considered.

## 2. Technology stack

Pin the versions below; upgrade only at milestone boundaries.

| Layer | Package | Version | License | Owns |
|---|---|---|---|---|
| Shell | tauri (Rust crate) | 2.x | MIT/Apache-2 | Window, menus, file dialogs, sidecar spawn/supervise, binary IPC relay |
| Shell | tauri-plugin-dialog, tauri-plugin-fs | 2.x | MIT/Apache-2 | File pickers, script files |
| Shell | tauri-plugin-updater (+ reqwest/rustls, minisign-verify, transitive) | 2.14.x | MIT/Apache-2 | Signed in-app updates (§13 rule 10, §16); the only network-capable code in the app |
| Shell | keyring (Rust crate) | 3.x | MIT/Apache-2 | OS credential vault |
| Shell | tokio (Rust crate) | 1.x | MIT | Sidecar stdio/socket I/O |
| Shell | windows-sys (Rust crate, Windows only) | 0.59 | MIT/Apache-2 | Job Object for orphan prevention (§4) |
| UI | react, react-dom | 19.x | MIT | Component tree |
| UI | typescript | 5.x | Apache-2 | Types shared with IPC contract |
| UI | vite | 6.x | MIT | Dev server, bundling |
| UI | tailwindcss | 4.x | MIT | Styling, theme tokens |
| UI | zustand | 5.x | MIT | Per-tab and global state |
| UI | dockview | 4.x | MIT | Panes, tabs, bottom panel |
| Editor | monaco-editor | 0.52.x | MIT | SQL editing, diff view |
| Grid | @glideapps/glide-data-grid | 6.x | MIT | Results rendering |
| Plans | @xyflow/react | 12.x | MIT | Execution plan DAG |
| Plans | @dagrejs/dagre | 3.x | MIT | Left-to-right plan layout (§11) |
| UI | @fontsource-variable/jetbrains-mono | 5.x | OFL-1.1 | Bundled editor font (no CDN, works offline) |
| Grid | lodash, marked, react-responsive-carousel | pinned | MIT | Peer dependencies required by Glide Data Grid; marked also renders notebook text cells (raw HTML escaped, §12) |
| Dev | ws, puppeteer-core | 8.x / 25.x | MIT / Apache-2 | Dev bridge and headless-Chrome bench/smoke (never shipped) |
| Dev | gitleaks (CI binary, checksum-pinned in `ci.yml`) | 8.30.x | MIT | Secret scan of every commit and the tree; `.gitleaks.toml` allowlists only the SSH fixture keys and two fake test values (never shipped) |
| Data | apache-arrow (npm) | 18.x | Apache-2 | Decode Arrow IPC batches in webview |
| UI | sql-formatter | 15.x | MIT | PostgreSQL formatting (§10) |
| UI | uplot | 1.6.x | MIT | Result charts: line, area and scatter on canvas (Phase C1); no dependencies, no network code. Bars, donuts and histograms are drawn by `src/charts/draw/` |
| Test | vitest | 4.x | MIT | Webview unit tests (4.1.12+: GHSA-82fw-gwwq-j7x9, GHSA-qxg6-89mg-rqv8; drops tinypool) |
| Sidecar | .NET SDK | 10.0 LTS | MIT | Runtime; publish self-contained, trimmed, single-file |
| Sidecar | Microsoft.Data.SqlClient | 6.1.x (spec: 5.2+) | MIT | SQL Server driver |
| Sidecar | Npgsql | 10.x | PostgreSQL | PostgreSQL driver |
| Sidecar | MySqlConnector | 2.6.2 | MIT | MySQL / MariaDB driver (Phase D2, shipped in 0.2.0); managed, no native code; local-infile always off |
| Sidecar | DuckDB.NET.Data.Full (+ DuckDB.NET.Bindings.Full and native DuckDB 1.5.6, MIT; EOL 2027-03-01) | 1.5.6.1 | MIT | DuckDB driver (Phase D3, shipped in 0.2.0); no IsTrimmable metadata, rooted whole; native library per RID (sidecar single file +23.3 MB on linux-x64) |
| Sidecar | SSH.NET (+ BouncyCastle.Cryptography, transitive) | 2026.0.x / 2.x | MIT | SSH tunnels to servers behind a bastion |
| Sidecar | Microsoft.SqlServer.TransactSql.ScriptDom | 180.x (spec: 161+) | MIT | T-SQL parse, AST, formatting |
| Sidecar | Microsoft.SqlServer.SqlManagementObjects | 181.x (spec: 170+) | MIT (since 171) | Table scripting (modules and dependencies use catalog views) |
| Sidecar | System.Security.Cryptography.Pkcs | 10.0.x (transitive pin) | MIT | Overrides the vulnerable 9.0.11 pulled in by SqlClient/SMO (§13 rule 10) |
| Sidecar | Apache.Arrow (+ Apache.Arrow.Scalars, a dependency since 23) | 23.0.0 | Apache-2 | Encode result batches (18.1 → 23.0 in Phase D3: DuckDB.NET needs ≥ 23; the frames are byte-identical, `CrossLanguageFrameTests` + `frames.golden.test.ts`) |
| Sidecar | Microsoft.Data.Sqlite | 10.x | MIT | Local store; the SQLite provider (Phase D1, shipped in 0.2.0) |
| Sidecar | SQLitePCLRaw.core (+ bundled SQLite 3.53.3, public domain) | 2.1.13 | Apache-2 | Called directly by the SQLite provider: authorizer, defensive config, attach limit, interrupt, autocommit flag (pinned since Phase D1) |
| Sidecar | StreamJsonRpc | 2.x | MIT | Control plane RPC |
| Sidecar | Microsoft.Extensions.Logging | 10.x | MIT | Logging with secret scrubbing |
| Test | xunit, Microsoft.NET.Test.Sdk | 2.9 / 18.x | Apache-2 / MIT | Sidecar tests |

**Rust stays thin.** The Rust crate contains no SQL, no parsing, no driver code. Adding
tiberius, sqlparser-rs or tree-sitter to Cargo.toml is out of scope.

**SMO fallback.** If SMO licensing or trimming blocks release, replace its two uses (object
scripting, dependency walk) with catalog views: `sys.sql_modules` for definitions,
`sys.sql_expression_dependencies` for dependencies. The `IObjectScripter` interface is designed
so this swap touches one class.

## 3. Repository layout

```
querithm/
  package.json               # pnpm workspace root; scripts: dev, build, test, bench
  src-tauri/                 # Rust shell
    Cargo.toml
    tauri.conf.json          # bundles sidecar under externalBin
    binaries/                # sidecar-<target-triple>[.exe] (built by scripts/build-sidecar)
    src/
      main.rs                # setup, spawn sidecar, register commands
      sidecar.rs             # spawn, health check, restart, kill-on-exit
      ipc.rs                 # relay: webview <-> sidecar socket, raw binary responses
      keyring.rs             # get/set/delete secret by profile id
      grants.rs              # database-file grant registry: canonical path, identity, mode, profile, consent (Phase D0c)
      injection.rs           # what the relay adds to session.open / profile.test: secrets needed, file grant
  src/                       # React webview
    main.tsx
    app/                     # App shell, Dockview layout, command palette
    ipc/                     # typed client: control plane + Arrow stream reader
      contract.ts            # generated from sidecar/Querithm.Contract (see §5)
    editor/                  # Monaco setup, SQL language, completion providers
      completion/            # tier-1 scanner (see §9)
    results/                 # Glide grid, Arrow data source, export; viewers/ = the cell value side panel (Phase C2)
    charts/                  # result charts: spec, chart worker (aggregation, binning, LTTB), canvas renderers, uPlot adapter (Phase C1)
    notebook/                # .sqlbook notebooks: format, runtime (cells, run queue), panel, export, Jupyter import (Phase C3/C4)
    explorer/                # object tree, search
    plans/                   # React Flow plan renderer
    trace/                   # XEvents trace timeline
    debug/                   # engine-neutral debugger UI
    connections/             # profile dialog, groups, env badges
    snippets/                # snippet library: store, panel, editor dialog, actions (Phase A3)
    settings/                # Settings dialog: registry of every preference, pages, keyboard shortcuts, About
    store/                   # zustand slices: tabs, sessions, settings
    theme/                   # tokens, dark/light
  sidecar/                   # .NET 10
    Querithm.Sidecar.sln
    Querithm.Contract/       # DTOs + interfaces shared by all projects
    Querithm.ContractGen/    # C# DTOs -> src/ipc/contract.ts
    Querithm.Host/           # Program.cs, JSON-RPC server, Arrow socket server
    Querithm.Core/           # sessions, pools, execution, cache, history
    Querithm.Providers.SqlServer/
    Querithm.Providers.Postgres/
    Querithm.Intelligence.TSql/   # ScriptDom: completion, diagnostics, format
    Querithm.Intelligence.PgSql/  # tokenizer, splitter, classifier, references; Completion/ = tier 2 (§9)
    Querithm.Providers.Sqlite/    # Phase D1: granted files and in-memory databases, hardened connections
    Querithm.Intelligence.Sqlite/ # Phase D1: tokenizer, splitter, classifier (file-policy refusals)
    Querithm.Providers.MySql/     # Phase D2: MySQL and MariaDB (one family, two products)
    Querithm.Intelligence.MySql/  # Phase D2: tokenizer (executable comments), DELIMITER splitter, classifier
    Querithm.Tests/
  scripts/
    build-sidecar.(ps1|sh)   # dotnet publish per RID -> src-tauri/binaries
    gen-contract.(ps1|sh)    # C# DTOs -> contract.ts
    bench/                   # benchmark harness (§14)
  docs/
    ARCHITECTURE.md          # this document
```

| Task | Command |
|---|---|
| Dev (hot reload UI, debug sidecar) | `pnpm dev` = `scripts/build-sidecar --debug && tauri dev` |
| Release build | `pnpm build` = `build-sidecar --release && tauri build` |
| Sidecar only | `dotnet publish sidecar/Querithm.Host -c Release -r <rid> --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true` |
| Tests | `pnpm test` = `vitest run && dotnet test sidecar` |
| Benchmarks | `pnpm bench` (§14) |

RIDs to publish: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`. SMO and ScriptDom are
reflection-heavy: keep `TrimMode=partial` and add `TrimmerRootAssembly` entries for both until a
trimmed build passes the full test suite.

## 4. Process model

Three processes, each with one job. The webview renders and edits; the Rust shell owns the
window, the secrets and the sidecar's life; the .NET sidecar owns every database byte. **The
webview never touches a database; the sidecar never touches a window.**

```
Webview (React 19 + TS): app shell · Monaco · Glide grid · Zustand tab state
        │  Tauri invoke + raw binary responses
Tauri v2 shell (Rust, thin): window/dialogs · sidecar monitor · IPC relay · keyring vault · file-grant registry
        │  JSON-RPC over stdio (control)        │  Arrow over local socket (rows)
.NET 10 sidecar (one process): JSON-RPC host · sessions and pools · providers (MSSQL, PG, SQLite, MySQL / MariaDB, DuckDB)
                               ScriptDom intelligence · Arrow encoder · SQLite local store
        │                                       │
   SQL Server (TDS)                        PostgreSQL (wire)
```

Control traffic (connect, execute, cancel, metadata, completion) goes webview → Rust → sidecar
as JSON-RPC. Result rows go sidecar → Rust → webview as Arrow IPC batches and are never
re-encoded as JSON.

**Sidecar lifecycle** (`src-tauri/src/sidecar.rs`)

1. On app start, Rust spawns the sidecar with stdin/stdout piped and a random session token in
   env var `QUERITHM_TOKEN`, plus `QUERITHM_SOCKET` (socket path) and `QUERITHM_PARENT_PID`.
2. The sidecar listens on a Unix domain socket or a Windows named pipe (`\\.\pipe\querithm-<pid>-<gen>-<random>`);
   the first message on that socket must be the token or the connection is dropped. Unix (L3): the socket is
   `<dir>/dp-<gen>.sock` in a folder `querithm-<random>` the shell makes 0700 for itself, under `$XDG_RUNTIME_DIR` when that
   is the user's private folder (Linux), else under the temp folder (`$TMPDIR`, else `/tmp`, which must be the user's or
   sticky); a folder that is a link, another user's, or open to group/other is never used, and the folder goes at exit.
   Before it sends the token the shell checks the server: Unix `peer_cred()` — this user, and the spawned child's pid;
   Windows `GetNamedPipeServerProcessId` — the child's pid.
3. Health: Rust calls `system.ping` every 5 s; three misses → mark sidecar dead, show a
   non-modal banner, restart once, replay open connection profiles (not transactions: those are
   lost and the user is told). An engine that stops and does not come back (`failed`: twice in a minute, or a restart that
   failed) takes the tabs' sessions along the same way (W1): runs going are marked stopped, open transactions are named as
   lost in the failure banner, and no prompt asks to end a transaction nothing can reach. M8 (2026-11-03): one thread of its own writes the sidecar's stdin; a call only queues its
   line (a bounded queue of 1,024, never waiting), so a sidecar that stops draining stdin (a `lang.*` call sends whole
   documents) blocks neither a tokio worker nor other calls. A line still queued 10 s after it was queued is failed
   unsent, and while one write has been stuck for 10 s every new call fails at once with the reason — both only once the
   engine has answered a first request (R1-1): a first start can take well over 15 s (native extraction, an antivirus),
   and requests queued meanwhile (restored tabs' `lang.*` calls carry whole documents) wait for it; the health check still
   ends an engine that never comes up. The ping goes
   through the same queue, so a stuck writer makes it miss; killing the dead sidecar closes the pipe, which fails the
   write in progress and every queued call ("The Querithm engine stopped."). `system.shutdown` is queued the same way.
4. Orphan prevention is mandatory, per OS:
   - Windows: Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, child assigned.
   - Linux: sidecar calls `prctl(PR_SET_PDEATHSIG, SIGKILL)` right after start (Program.cs via
     P/Invoke) and also watches `getppid()`. Rust spawns the sidecar from the main thread,
     because PDEATHSIG fires when the *spawning thread* exits.
   - macOS: sidecar polls `getppid()` every 2 s and exits when it changes.
5. On app exit, Rust sends `system.shutdown`, waits 2 s, then kills.
6. A spawn that fails at launch (M18: the binary missing — often an antivirus quarantine — or refused) never aborts the
   app: the window opens, the supervisor's state is `failed` with the reason (`sidecar_status` answers it too, since the
   status event went out before the webview listened), and the boot screen shows it with **Restart engine**, which
   spawns again and boots on. A shell panic appends a crash record (location, message, version) to the shell log before
   `panic = "abort"`.

**Logs (M21).** `<data dir>/querithm/logs/`: the sidecar writes `sidecar-YYYYMMDD-<pid>.log` (one file per process: .NET
writes at offsets it tracks itself, so two instances must not share one), the shell appends to `shell-YYYYMMDD.log`
(O_APPEND / FILE_APPEND_DATA, one write per line, safe to share). Both: a file stops at 5 MB and the next part starts
(`-2`, `-3`, …); files last written within 7 days are kept, at most 14 per kind, checked at start and at every new file
(`LogFiles`, `shell_log.rs`). The shell's log holds what only it sees — relayed sidecar stderr (a release sidecar runs
with `QUERITHM_LOG_STDERR=0`, so that is what it printed outside its logger, such as a runtime crash), data-plane
failures, spawn and restart failures, panics — scrubbed of the launch token, every secret it handed the sidecar, and
any `password` / `pwd` / `passphrase` / `secret` / `token` / `authorization` value. R1-10: a secret is matched written
as it is, JSON-escaped (`\"`, `\\`, `\uXXXX`) or URL-encoded (`%XX`, `+` for a space), in any mix; the newest 4 launch
tokens are kept apart, and of the other secrets the 256 handed most recently (one handed again moves to the newest
place), so a secret still in use is never dropped for a stale one; a secret shorter than 3 characters is not matched as
such (it would mask ordinary text everywhere) — the key=value rules still cover it. The sidecar logs unhandled exceptions
(`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) through its scrubbing logger, whose file
flushes every line. `QUERITHM_LOG=trace` stays honoured in release: no Querithm code logs at Trace and no library gets
the logger factory, so it adds only Querithm's own debug lines (no SQL text, row values or secrets; `DiagnosticsTests`).
A server error around a grid write, an import or a row read is logged by type and code (`LogScrubber.Describe`): its
message may quote a row value (SQL Server and MySQL name the duplicate key).
Settings → About shows the logs folder (Copy path) and **Copy diagnostics**: version, OS, engine state and the newest
200 lines of these logs (`system.logTail`, which reads nothing else and scrubs every line again), the data folder written
as `<data dir>`.

**Closing the window (H4)** (`src-tauri/src/close.rs`, `src/app/quit.ts`). A close request on the main window (title-bar X,
Alt+F4, the OS quitting the app while it is open) is held (`prevent_close` / `prevent_exit`) and sent to the webview
(`app://close-requested`). The webview acknowledges it at once (`app_close_ack`), then settles every tab the way closing
it would (`settleTab`, shared with tab close): a running query asks "Cancel the running query?" (one that has not stopped
10 s after the cancel no longer holds the quit), staged grid edits ask Apply / Discard / Cancel (`resolvePendingEdits`),
an open or possibly open (unknown, §6) transaction asks Commit / Roll back / Cancel; then the unsaved work the bulk close
asks about (with autosave on: files changed since saved; with autosave off: SQL that exists only in a tab), then every
write still waiting on its debounce (scripts, notebook copies, workspace, settings — at most 5 s). It answers
`app_close_answer {close}`; Cancel anywhere keeps the app open. Quitting never dead-ends (W1, W4, R1-8): with the engine
gone (`dead` or `failed`) a transaction is not asked about (it went with the engine's connections); a COMMIT or ROLLBACK
that fails asks **Quit anyway (the server rolls back)**; a query that has not stopped 10 s after its cancel names the
staged grid edits it holds (Discard and quit) instead of dropping them unasked; and a close while an update installs asks
first (**Keep waiting** is the default; quitting then can stop the installer half-way). The window closes anyway when the webview cannot answer:
no acknowledgement within 3 s (hung, crashed, still loading), or a second close request while the webview shows no dialog
(it is waiting for a query to stop or a file write). An update install marks the close as allowed before it restarts the
app, and an engine restart closes no window, so neither waits. No window permission is added: the shell holds and closes
the window, the webview only answers through two app commands (§16). Several app instances stay allowed. The first one
owns the saved workspace (M23): it holds an exclusive lock on `<data dir>/querithm/instance.lock` while it runs
(`instance.rs`, dropped by the OS with the process; a lock held is tried 3 more times 200 ms apart first, R1-7: Windows
releases a crashed process's lock asynchronously and a restart starts the new process before the old one is gone); a later
window starts with a new tab and neither restores nor writes `workspace.json`, so two windows never share a tab's script
file. Its tabs still get recovery copies, which the next first window offers back once that window is gone (W5, §8).

One sidecar serves all connections and tabs. Never spawn a sidecar per connection.

## 5. IPC contract

The contract lives once, in C# (`Querithm.Contract`), and `scripts/gen-contract` emits
`src/ipc/contract.ts`. The webview never hand-writes a DTO.

**Control plane: JSON-RPC 2.0 over the sidecar's stdio**, newline-delimited. Rust exposes one
Tauri command `rpc(method, params) -> result` that forwards verbatim (the only exception: for
`session.open` and `profile.test` Rust adds the unexpired memory or keyring secret — and, for a profile with an SSH tunnel, the
`<profileId>:ssh` secret as `sshSecret`, §8; a sign-in that needs no secret, and a profile without a tunnel, skip the vault, so
they open with a locked or unavailable keyring, Phase D0a; a file profile also gets its grant as `fileGrant` — `{grantId, path,
mode, identity}` from the shell's registry after the shell checked the file is there, at the same canonical path, with the
same identity, and bound to that profile; a `fileGrant` sent by the webview is dropped on every method, Phase D0c). The
shell owns `filePath` and `fileGrant`: before anything is injected, a call whose params, `source` / `target` side or
`profile` names one of them with a value, or spells one of them or `fileToken` / `secret` / `sshSecret` any other way
than exactly (`FilePath`, `SECRET`), is refused (`shellOwned`, M1); session.open decides what it needs from the stored
profile only. The sidecar binds member names exactly as the contract spells them (`Json.CreateRpc`: camelCase,
case-sensitive), so a case variant could not reach a service even past the shell. Every method takes and
returns a DTO; ids are opaque strings (ULIDs).

| Method | Params | Returns | Notes |
|---|---|---|---|
| `system.ping` | – | `{version}` | Health |
| `system.shutdown` | – | – | Graceful stop |
| `system.engines` | – | `EngineInfo[]` | Phase D0g: the engines this build knows (name, dialect, default port / database / schema / sign-in, sign-ins, locations), exposed ones first; a hidden one only behind the dev setting |
| `system.notices` | – | `SystemNotice[]` | M22: what the sidecar met at start that the user must hear (a damaged store set aside, a store it must not open, §8; history retention deleting entries for the first time, L17); each is answered once |
| `system.reportError` | `ErrorReport` (`{where, name?, message?, stack?, componentStack?}`) | – | M17: a webview render error for the sidecar log (§12), capped and scrubbed there |
| `system.logTail` | `{lines?}` (1–500, default 200) | `{logDir, lines}` | M21: the newest lines of Querithm's own logs, scrubbed again on read; reads no other file (§4) |
| `profile.list` | – | `Profile[]` | From SQLite, no secrets. A stored engine, sign-in or location this build does not know, or an engine without a provider, is listed with `unsupported` (the reason) and is never opened, tested or overwritten (Phase D0a) |
| `profile.save` | `Profile` | `Profile` | Secret goes to keyring via Rust, not through this call. Validated in the sidecar (`invalidProfile`): `location` (server — the default, every legacy profile — file with a shell grant id, or memory), sign-in and engine must fit together (`ProfileRules` + the provider's `ValidateProfile`) |
| `profile.delete` | `profileId` | – | Closes the profile's sessions first, refused (`profileBusy`, nothing deleted) while one has an open or unknown transaction or a running query (the UI settles each tab first, as tab close does); then stops its monitors and deletes its secrets, pools, driver state, grant, cached metadata and history. On success the shell deletes its keyring entries (`<id>`, `<id>:timed`, `<id>:ssh`, `<id>:ssh:timed`) and memory copies; a keyring failure then is reported as `secretsNotDeleted`, with the profile already gone (L2) |
| `profile.test` | `profileId` or unsaved `Profile` | `{ok, serverVersion, error?, notes[], untrustedHostKey?}` | Opens and closes one connection (through a temporary SSH tunnel when the profile has one) |
| `group.list` / `group.save` / `group.delete` | – / `ProfileGroup` / `ProfileGroup` | `ProfileGroup[]` | Connection groups (`{name, environment?, color?}`, names unique ignoring case), kept in the `setting` table; save and delete answer the whole list |
| `ssh.trustHostKey` | `SshHostKey` | – | Records the bastion's key in `<data dir>/known_hosts` after the user compared the fingerprint; never automatic. Only a key a bastion presented to this sidecar (profile.test, session.open, a tunnel reopen) in the last 15 minutes, matched exactly on host, port, algorithm and fingerprint, is accepted, once; any other is refused (`sshHostKeyNotPresented`, M2) |
| `profile.parseConnectionString` | `{profile, connectionString}` | `{profile, secret?, unknown[], notApplied[], notes[]}` | Each driver's builder reads the string (or a `postgres://` URI); the engine recognising the most keywords wins. The string is never stored; the secret goes to the keyring on Save. A TLS keyword the string does not name keeps the editor's mode (M3: MySqlConnector reads an absent `SslMode` as Preferred); one that is weaker than the engine's default (§13 rule 2) is applied and explained in `notes`, which the editor shows as warnings |
| `session.open` | `{profileId, database?}` | `{sessionId, database, serverVersion}` | Lazy: borrows a physical connection on first use. A file profile needs its injected grant (`fileGrant`); the sidecar checks the path's shape and the file's identity before and after every open (`fileMissing` / `fileReplaced` / `fileMoved`, each asking for a re-pick) and keeps the path in memory only |
| `grant.revoke` | `{profileId}` | `{closedSessions}` | Phase D0c: closes the profile's sessions and forgets its file grant; refused (`grantBusy`) while one has an open or unknown transaction or a running query. On success the shell deletes the grant; `profile.delete` deletes a profile's grants as well |
| `session.close` | `sessionId` | – | Refuses if `txState == open` unless `force`; then cancels the session's runs and helpers and waits (bounded) for them before closing its connection (§6) |
| `session.changeDatabase` | `{sessionId, database}` | `{database}` | SQL Server: `ChangeDatabase`; PostgreSQL: reconnects (§6) |
| `session.state` | `sessionId` | `SessionState` | `{txState, busy, database, spid}` |
| `session.capabilities` | `sessionId` | `Capabilities` | Trace/debugger/CREATE OR ALTER availability with reasons; Phase D0a adds the detected `product` (name, version), the engine's `namespaces` model, and ChangeDatabase, Transactions, DataEdit, Import, SchemaCompare/Apply, DataCompare/Apply, References and Monitor, each with its reason. Cached per (profile, database, credential epoch); every RPC that needs a refused feature fails with `unsupported` and the same reason |
| `query.execute` | `{sessionId, sql, options}` | `{executionId}` or `{blocked}` | Returns immediately; results arrive on the data plane |
| `query.cancel` | `executionId` | `{cancelled}` | Driver-level cancel |
| `query.cell` | `{executionId, resultIndex, row, col}` | `{value}` or `unavailable` | Full value of a truncated cell |
| `query.close` | `executionId` | – | Release the execution's buffers |
| `tx.begin` / `tx.commit` / `tx.rollback` | `sessionId` | `SessionState` | Explicit only |
| `import.inspect` | `{fileToken, options}` | `ImportInspectResult` | Detects format/encoding/delimiter/header, profiles every row; preview rows as Arrow frames under `previewExecutionId`; progress as `import.progress` |
| `import.checkTarget` | `{sessionId, inspectionId, schema, table, newTable, mapping?}` | `{mapping, verdicts, createTableSql?}` | New table: CREATE TABLE from the inferred types; existing: per-column verdicts with suggested ALTER/ADD |
| `import.run` / `import.cancel` | `ImportRunParams` / `{jobId}` | `{jobId}` or `{blocked}` / `{cancelled}` | One guarded execution; native bulk load; reject log; `import.progress` every 250 ms |
| `data.rowTokens` | `{sessionId, executionId, resultIndex, keys, expect?}` | `{tokens}` | Grid edits (§7): row-version tokens of edited rows; null = row changed since the query |
| `data.previewPatch` | `GridPatchParams` | `{sql, statementCount, notes}` | The generated DML with literals, never executed |
| `data.applyPatch` | `GridPatchParams` | `GridApplyResult` (`blocked`, `conflicts`, `insertedKeys`) | One execution in one transaction (savepoint inside an open one); sidecar-enforced guards |
| `schemaCompare.run` | `{source, target}` (each `{sessionId, database, schema?}` or `{fileToken}`) | `SchemaCompareResult` (`compareId`, items with class/tags/changes, `possibleRenames`, `notes`, `migrationRefusal?`) | Reads both models in parallel; the last 4 comparisons are kept in the sidecar |
| `schemaCompare.objectSql` | `{compareId, key}` | `{source?, target?}` | One object scripted from each side's model for the diff view |
| `schemaCompare.migrationSql` | `{compareId, keys, renames?, includeDataLoss}` | `{sql, statements, warnings, skipped, transactional}` | Text only, never executed; keys and renames must belong to the comparison |
| `schemaCompare.exportModel` | `{sessionId, database?, schema?}` | `{fileName, json}` | A `.qschema.json` model the webview saves through the shell |
| `dataCompare.start` | `{source, target, key?, columns?, options?}` (each side `{sessionId, database, schema+table \| query}`) | `{compareId, executionId, key, columns, notes}` | Diff rows stream as Arrow frames under `executionId`; `dataCompare.progress` every 250 ms |
| `dataCompare.cancel` | `{compareId}` | `{cancelled}` | |
| `dataCompare.syncSql` | `{compareId, insert, update, delete}` | `{sql, statements, rows, notes}` | Literal INSERT / UPDATE / DELETE for the kept differences in one transaction; text only |
| `refactor.symbolAt` | `{sessionId?, database?, dialect, text, offset}` | `{target?, local?}` | The schema object / column at the cursor (resolved against the sidecar's snapshot), or a local symbol's spans |
| `refactor.findUsages` | `{sessionId, database?, target, tabs?}` | `{hits, unchecked, serverUpdated, objectsScanned, elapsedMs}` | Cached definitions (filled first) + the open tabs' text; hits are `exact` or `possible` |
| `refactor.previewRename` | `{sessionId, database?, target, newName, tabs?, include?, exclude?}` | `{objects, tabs, sql, statements, warnings, unchecked}` | Before/after per object, edits for open tabs, the guarded script (text only) |
| `meta.databases` | `sessionId` | `DatabaseInfo[]` | |
| `meta.snapshot` | `{profileId, database}` | `SchemaSnapshot` | From cache; triggers refresh if stale |
| `meta.objects` | `{profileId, database, ids, sessionId?}` | `SnapObject[]` | Cached objects by id with their columns (the Explorer's lazy levels, §8); `sessionId` for a session's own view (Phase D0d) |
| `meta.refresh` | `{profileId, database}` | `{jobId}` | Background; emits `meta.updated` |
| `meta.objectDetails` | `{profileId, database, objectId}` | `ObjectDetails` | Columns, keys, indexes, params from SQLite |
| `meta.definition` | `{sessionId, objectRef}` | `{sql}` | `sys.sql_modules` / `pg_get_functiondef` |
| `meta.dependencies` | `{sessionId, objectRef}` | `Dependency[]` | |
| `meta.search` | `{profileId, database, text}` | `ObjectRef[]` | Prefix + fuzzy over cache |
| `meta.script` | `{sessionId, objectRef, kind}` | `{sql}` | Script as CREATE/ALTER/DROP+CREATE/EXEC via `IObjectScripter`; triggers also ENABLE / DISABLE (text for a new tab, never executed) |
| `meta.rowCounts` | `{sessionId, database?, objectIds}` | `RowCount[]` | ER diagram "Load row counts": `sys.dm_db_partition_stats` (else `sys.partitions`, estimated) / `pg_class.reltuples` (estimated; unknown before ANALYZE) |
| `lang.complete` | `{docId, text, offset, context}` | `CompletionItem[]` | Tier 2, §9 |
| `lang.diagnostics` | `{docId, text, dialect}` | `Diagnostic[]` | Parser errors |
| `lang.format` | `{text, dialect, options}` | `{text}` | ScriptDom `SqlScriptGenerator` |
| `lang.signature` | `{docId, text, offset}` | `SignatureHelp` | Proc/function params |
| `lang.folding` | `{docId, text, dialect}` | `FoldingRange[]` | From the AST |
| `lang.classify` | `{text, dialect}` | `Classification` | Destructive statements, DDL, `USE`, `BEGIN TRAN` |
| `lang.forget` | `{docId}` (1–512 characters) | – | M12: the webview disposed the document's model (a closed tab, a deleted notebook cell); every engine drops what it cached for that docId (§9) |
| `plan.get` | `{sessionId, sql, mode, confirmedRun, confirmedProd, confirmedDestructive}` | `{executionId}` or `{blocked}` | Estimated or actual (§11). Guarded like `query.execute` (the same refusals, Prod first run and destructive confirm), then planned as one execution of the tab: queued behind a running query, the plan as `query.plan`, the end as `query.done`, `query.cancel` and the §7 escalation end it. An actual plan runs inside a transaction that is always rolled back (a savepoint inside an open one); refused with the reason (`unsupported`) where it could not be undone: an engine without actual plans or without savepoints inside an open transaction, text that begins or ends a transaction, a write on MySQL, SQL Server's actual mode (it comes with the run, §16), SQL Server text that sets SHOWPLAN / STATISTICS XML / PROFILE |
| `plan.parse` | `{engine, raw}` | `{plan, format}` | A plan held as text (a result cell's SQL Server showplan XML or PostgreSQL EXPLAIN (FORMAT JSON) value) read by that engine's plan parser; nothing runs on a server. Untrusted input: ≤ 10 MB, XML without DTDs or external entities and ≤ 512 elements deep, text the parser cannot read refused (`invalid`) |
| `trace.start` / `trace.stop` | `{sessionId, scope}` | `{traceId}` | XEvents, §11 |
| `debug.*` | `debug.attach {sessionId, functionOid, callSql, breakpoints?, confirmedProd, confirmedDestructive}` | `{debugId}` or `{blocked}` | PostgreSQL pldebugger only, §11. The call is caller SQL on a target connection of its own: guarded like `query.execute` before anything opens |
| `history.list` / `history.search` | `{profileId?, database?, limit?, beforeId?}` / `{text, profileId?, limit?}` | `HistoryEntry[]` | Newest first, at most 2,000; FTS5 prefix terms for search. L17: each entry's `sql` is a preview — its first 500 characters (`sqlTruncated`, `sqlLength`) |
| `history.get` | `{id}` | `HistoryEntry` | L17: one entry with its whole text (`historyGone` once retention removed it) |
| `history.retention` / `history.setRetention` | – / `{days, maxEntries}` | `{days, maxEntries}` | L17: what history keeps (default 90 days and 50,000 entries; 1–3,650 days, 1,000–1,000,000 entries, `invalid` outside), §8 |
| `settings.get` / `settings.set` | | | `setting` table |
| `snippet.list` | – | `Snippet[]` | Built-in snippets (read-only, in code) then the user's (`snippet` table) |
| `snippet.save` | `{snippet}` | `Snippet` | Validated in the sidecar: title ≤ 200 chars, prefix `[A-Za-z0-9_]{1,40}` unique ignoring case per dialect (`snippetPrefixTaken`, with the field), body ≤ 256 KB UTF-8, ≤ 20 tags, ≤ 5,000 snippets; built-ins refuse (`snippetReadOnly`) |
| `snippet.delete` | `{id}` | – | Built-ins refuse |
| `monitor.start` | `{profileId, intervalMs?, panes?, databaseFilter?}` | `{monitorId, intervalMs, panes, engine}` | Server activity monitor (Phase B): one connection of its own (`Querithm Monitor`, read-only settings), ≤ 8 per sidecar, interval 1–30 s; data arrives as `monitor.snapshot` |
| `monitor.stop` / `pause` / `resume` / `setPanes` / `setInterval` / `setTopQueries` | `{monitorId, …}` | – | Applied on the next tick, no restart; nothing is sent to the server while paused |
| `monitor.texts` | `{monitorId, ids}` (≤ 500 content-hash ids) | `{texts, missing}` | Statement texts the webview evicted or missed (texts travel once in snapshots) |
| `monitor.killPreview` / `monitor.kill` | `{monitorId, key}` / `{monitorId, target, mode, confirmedKill, confirmedProd, confirmedLongRollback, expectedStatementStart?}` | `KillPreview` / `KillResult` | Guarded in the sidecar: target in the latest snapshot, exact identity re-checked on the server, Querithm's own and system sessions refused, Prod and long-rollback confirms recomputed; audit log without text, login or host |
| `monitor.requestDetail` / `monitor.diagnose` / `monitor.waitInfo` | `{monitorId, key}` / `{engine, waitType}` | `RequestDetail` / `Diagnosis` / `WaitInfo` | Interactive jobs (one pending per kind, 5 s queue limit); wait descriptions from the offline dictionary |
| `monitor.deadlocks` / `monitor.deadlockXml` | `{monitorId, fromMs?, toMs?}` / `{monitorId, eventId, confirmedText}` | `DeadlocksResult` / `MonitorFileResult` | Read-only (`system_health`; never creates an event session); the .xdl is written under `<data dir>/exports` |
| `monitor.history` / `monitor.historySettings` / `monitor.export` | `{monitorId, seq?}` / `{monitorId, minutes}` / `{monitorId, fromSeq?, toSeq?, includeText}` | `HistoryResult` / – / `MonitorFileResult` | In-memory history (1–60 min, ≤ 50 MB); export without text, login or host unless asked, under `<data dir>/exports` |
| `script.*` | | | Local script files via Rust fs plugin, not sidecar |
| `bench.synthetic` | `{rows, columns}` | `{executionId}` | The bench harness's synthetic result stream (metrics 11–13); registered only in a sidecar started with `QUERITHM_BENCH=1` (L10) |

**Deadlines (M5, 2026-11-03).** The webview sends no `$/cancelRequest`, and the drivers' command timeout defaults to none
(§13 rule 8), so a handler that waits on a server has a deadline of its own and then fails with `timeout` ("… did not
finish within 60 s: the server did not answer …"): 60 s (`InteractiveDeadline`) for session state, capabilities, switching
database, `tx.begin`, `data.rowTokens` (its execution-lock wait included), `meta.databases` / `definition` / `dependencies` /
`script` / `rowCounts`, `dataCompare.start`, `trace.start` and `debug.attach` / breakpoints / source — these answer in
milliseconds to seconds on a working server, so waiting longer means a lock or a server that stopped answering; 30 min
(`LongDeadline`) for `schemaCompare.run` / `exportModel`, `refactor.findUsages` / `previewRename`, which may first read every
definition of a large database (each schema-compare catalog read also has a 300 s command timeout). The token cancels
the driver's command; a catalog read on a borrowed SQL Server connection still going 5 s later has its connection closed
(SqlClient waits for the server's acknowledgement for good) and the pool discards it; the RPC answers at most 10 s after
the deadline either way. `tx.commit` / `tx.rollback` have **no RPC deadline** (8a review): a large rollback or a commit
waiting for a synchronous standby legitimately takes minutes, and a COMMIT cut short has an unknown outcome — PostgreSQL may
have rolled it back, and a retried COMMIT there succeeds with "no transaction in progress". They are bounded by
`SessionManager.TxEndLimit` (30 min) and end sooner when the tab is force-closed, disconnected, the engine restarts or the
app quits (`Session.Closing`). What a stopped or lost COMMIT reports is in §7 Transactions. `import.inspect` (a local file;
`import.cancel`), `debug.step` (ends when the target does or on `debug.detach`) and `tx.commit` / `tx.rollback` keep their
own bound; `ArchitectureRulesTests` lists them, and fails on any other handler without a
deadline. A deadline-bounded read on the tab's **pinned** connection (definition, dependencies, script, row counts,
capabilities, compare models, `data.rowTokens`) never silently ends the user's transaction (8a review,
`SessionManager.PinnedReadAsync`): where a failed or cancelled statement aborts the transaction and savepoints exist
(PostgreSQL, 25P02) it runs inside `SAVEPOINT querithm_read` … `RELEASE` and is rolled back to on any failure — the
savepoint commands have the cleanup bound, never the deadline; after a read that failed inside the savepoint, or was
cancelled on any engine (SQL Server with XACT_ABORT ON rolls back on the attention), the state is read again and sent
(`session.stateChanged`), and a transaction that ended or a connection that broke fails the RPC with `txLost` instead of
`timeout`. A savepoint that cannot be set (the connection busy with the tab's own query, a transaction already failed)
leaves the read as before, with no state read on a connection Querithm may not hold. Long work with a progress view and a cancel of its own (`query.*`, `import.*`, `dataCompare.*`, `meta.refresh`)
is unchanged.

**Notifications** (sidecar → webview, JSON-RPC notifications relayed by Rust as the Tauri event
`sidecar://notify`): `import.progress`, `dataCompare.progress`, `query.queued`, `query.started`, `query.messages` (M13:
`{executionId, messages[]}`, a run's server info/print/error lines with line numbers, coalesced — see below; the
single-message `query.message` stays in the contract and the webview reads both), `query.resultSet` (`{executionId, resultIndex, schema,
rowCountHint}`), `query.plan`, `query.done` (`{executionId, affectedRows, elapsedMs,
serverElapsedMs?, error?}`), `session.stateChanged`, `meta.updated`, `trace.event`,
`debug.event`, `monitor.snapshot` (full session rows each tick, new texts only), `monitor.stopped`, `session.blocking`
(the own-tab blocking banner), `dataPlane.state` (`{connected}`: the data plane lost or regained its reader),
`system.noticeAdded` (a `SystemNotice` met after start — history retention's first deletion — waiting in `system.notices`), `auth.prompt`
(`{message, verificationUrl?, userCode?}`: a device-code or browser sign-in waiting for the user, §6).

**Server-message floods (M13, 2026-11-03).** A run's messages go through its `MessageBatcher`: a message after a quiet
100 ms goes out at once (a `RAISERROR … WITH NOWAIT` progress line still shows while the batch runs); during a flood
the rest wait at most 100 ms or until 500 have gathered, and the run flushes them before its next `query.resultSet`,
`query.plan` and `query.done`, so the ledger keeps the order they happened in (statements that each return a result set,
such as MySQL SELECTs with a warning each, therefore send one batch per result set: never more notifications than result
sets plus one). A run forwards at most 20,000 server
messages (PRINT / RAISERROR, NOTICE, MySQL warnings, row-limit notes, a tool's lines); the rest are counted and one
warning before `query.done` says how many were not shown. Querithm's own lines (a cancel that did not stop, a lost
connection) are never capped. The webview appends a batch in place (O(batch), no copy of the log), keeps at most
20,100 lines per run, and sets the editor's error markers once per batch that holds an error. The host's notification
queue holds at most 10,000 notifications waiting for stdout; past that (the shell stopped reading) message batches,
progress, monitor snapshots and trace events are dropped and counted in the log, and lifecycle notifications are always
queued, so no run is left without its end. SQLite and DuckDB send no server messages.

**Data plane: Arrow IPC stream over the local socket.** One socket, multiplexed. Frame =
`[u32 length][u8 kind][16-byte executionId][u16 resultIndex][payload]` (little-endian; `length`
counts the bytes after itself). Kinds: `0x01` Arrow schema, `0x02` Arrow record batch (≤ 10,000
rows or ≤ 4 MB, whichever first), `0x03` end-of-result-set (payload: `u8 truncated`), `0x04`
end-of-execution. Each `0x01`/`0x02` payload is a self-contained Arrow IPC stream (schema
message + at most one batch), so the webview can decode any frame independently. Rust reads
frames and hands each one to the webview through a Tauri command that returns
`tauri::ipc::Response::new(Vec<u8>)` when the webview calls `results_next(executionId)`; Rust
buffers at most 8 batches per execution and applies backpressure to the sidecar by pausing
socket reads. The webview decodes with apache-arrow's `RecordBatchReader` and appends to the
grid's data source.

**Framing is never torn (2026-11-02, H2).** The socket is shared by every run, so a frame is atomic on it: the run's
token decides only whether a frame starts; once its first byte may be out, a cancel gives it 3 s (`FrameGrace`, shorter
than the cancel escalation's 5 s) and then the sidecar drops the client instead of leaving part of a frame, as it does
for any write that fails part-way (the run ends cancelled, or fails "the results channel … broke … Querithm is
reconnecting the results channel"; a run that finds no reader in the 30 s after such a drop is told the channel is
reconnecting, not to restart). The shell sees the pipe close and reconnects (below); later frames wait for the new
connection, the frames already written arrive whole. **Timing invariant (8a review):** a run's end-of-execution frame (and
a stream's end) waits `FrameDrainLimit` = 35 s (a cancelled run's: `CancelGrace`, 5 s, keeping §14 #15b's
`query.done` within 10 s of Cancel), then `FrameGrace`, before the client every tab shares is dropped, and
`FrameDrainLimit + FrameGrace > STALE_CONSUMER + 5 s` (35 + 3 > 30 + 5): a frame stuck behind the shell's one reader,
paused on another run's full queue, outlasts that pause instead of failing every tab's frames (it was 10 s + 3 s). A user's
Cancel of a frame mid-write is the one case allowed to drop sooner — `FrameGrace < CancelGrace` (3 s < 5 s), so the run
still ends inside the escalation's first step. `DataPlaneTimingTests` reads `STALE_CONSUMER` from `src-tauri/src/ipc.rs`
and `scripts/dev-bridge-relay.mjs` and asserts both relations, so changing either side (a shorter `STALE_CONSUMER` keeps
them) cannot silently break it. A
frame's `length` is at most 256 MiB (`FrameLimits.MaxLength` / `MAX_FRAME_LEN`, the same value on both sides): batches
flush at 4 MB, so one exceeds that only by its last row, whose cells are capped (text 1 M characters, binary 64 KB); a
data-compare batch flushes at 1,000 diff rows or 4 MB as well (its text is not capped, so only one row of very long values
comes near it). The sidecar fails a run
whose frame would be larger before writing a byte ("select fewer or shorter columns"); the shell
treats a larger or shorter length as a stream that lost its framing — nothing is allocated, the connection closes, it
reconnects. The `resultIndex` is a u16: a run streams at most 65,535 result sets (0xFFFF stays unused); past that its
statements still run and Cancel still ends it, but later result sets are skipped with one warning instead of wrapping
onto earlier ones (L13).

**A run nobody reads never holds the reader (2026-11-02, H3).** The shell reads the socket on one task, so a full queue
whose consumer stopped polling would stall every tab's results. A queue whose consumer has not called `results_next` for
30 s while it is full is dropped until its end-of-execution frame (logged); `results_next` long-polls for 20 s and a
reading consumer calls it again at once, so only a consumer that is gone reaches the bound. `results_next` answers a
dropped or already-ended run with the shell-made frame `0xFE` ("gone"), and the webview's loop ends with the reason.
`results_discard` drops a run's buffered and later frames (an entry its end removes); for a run whose end already
arrived it leaves nothing behind. The webview discards whenever a consumer goes before its stream ended: a disposed run,
a closed Data Compare or import preview (which also closes the execution — that cancels a compare's producer — and
cancels the compare), a frame that does not decode, the import wizard's DDL (§7 Import). The dev bridge's relay
(`scripts/dev-bridge-relay.mjs`, used by `scripts/dev-bridge.mjs` for the browser dev mode and the UI smoke) has the same
constants and rules — the length cap that ends the connection, 8 batches, the 30 s guard, `0xFE`, no entry left by a
discard after the end — and its test reads the constants out of `ipc.rs`. R1-3: any entry nobody asked for a frame for
30 s goes, full or not (a vanished consumer whose queue never filled, an end never read, a dropped run whose end never
came), swept at most every 7.5 s on a push or a poll and remembered as ended; and `Relay::clear` (the engine went) starts
a new generation, so the killed engine's reader, still draining its socket, makes no entry again. R1-4: the 19-byte
header is read before the payload, and a kind outside 0x01–0x04 is lost framing like a bad length (nothing allocated,
reconnect). R1-5: the relay logs only after its lock is released.

**When nobody reads the data plane** (the shell never connected, or its pipe closed): a frame waits at most 5 s for a
reader, then the write fails with `DataPlaneUnavailableException` and later writes fail at once until a reader attaches.
The run ends `failed` with the reason ("the results channel … is not connected"), runs no later batch, and the
sidecar sends `dataPlane.state {connected: false}`; the webview shows a banner with **Restart engine**, cleared by
`{connected: true}` when a reader attaches. Best-effort writes (the end-of-execution frame) are bounded at 10 s even
with a reader attached that stopped draining. The shell keeps trying to connect for 60 s and reconnects when the pipe
closes while the sidecar lives (the dev bridge does the same).

**Type mapping rules (sidecar side):** `decimal`/`numeric` → Arrow Decimal128 (PostgreSQL
`numeric` without typmod → Utf8); `datetime2`/`timestamptz` → Timestamp(µs, UTC) plus the
original offset in a sibling column when `datetimeoffset`; `varbinary`/`bytea` → Binary truncated
at 64 KB with a flag column `<col>__truncated`; `xml`/`json` → Utf8; `nvarchar(max)` →
LargeUtf8 truncated at 1 MB with `<col>__truncated`; `sql_variant`/unknown → Utf8 via
`ToString()`. **Dynamically typed columns (Phase D0f):** a provider marks a column whose reported type is not a
guarantee (`ColumnHint.Dynamic`; SQLite declared types and affinity) and it gets one stable schema for the whole result —
Utf8 with lossless text (integers in invariant decimal, reals in round-trip form including `NaN`/`Infinity`, text as is,
blobs in lowercase hex), the 1 MB truncation flag, and each cell's storage class in a hidden UInt8 sibling
`<col>__class` (0 null, 1 integer, 2 real, 3 text, 4 blob; field metadata `q:dynamic`, `ResultColumn.Dynamic` /
`ClassColumn`). Per-value classes travel only in Arrow, never in JSON DTOs; a later row of another class never changes the
schema. Grid edits lock such a column, and a result whose key is one is read-only. SQL Server and PostgreSQL report stable
types and are unchanged. **Numbers sent as text (M11, 2026-11-03):** a number no Arrow type here holds exactly travels as
exact text in a Utf8 column with field metadata `q:numeric` (`1`): PostgreSQL `numeric` without typmod (`NaN`,
`±Infinity` included) or with precision > 38, DuckDB DECIMAL / HUGEINT / UHUGEINT / UBIGINT / USMALLINT, MySQL BIGINT /
SMALLINT UNSIGNED, BIT and DECIMAL(p > 38) — every non-dynamic text column whose CLR type is decimal, ulong, ushort,
BigInteger, Int128 or UInt128. The webview gives it the number kind (right-aligned, charted, an Excel number within 15
digits; JSON keeps the exact string) and sorts it by a string key built from the text (sign, exponent, digits; no float
conversion; −Infinity < negatives < 0 < positives < Infinity < NaN as PostgreSQL orders them). Decimal128 cells sort by
their unscaled BigInt (exact; one scale per column), no longer through `Number`. Float32 cells show the shortest
decimal that reads back as the same float (`0.1`, not `0.10000000149011612`), in the grid, copy and export alike (L12);
the search costs 560–1,174 ns per value (up to 9 `toPrecision` calls; 1 M values, Node 24 on the dev laptop), so answers
are kept in a 16,384-slot direct-mapped cache: a redraw of 1,000 visible REAL cells costs 98 ns per cell, a value never
seen the same as before (W9, `src/results/model.ts`);
a MySQL / MariaDB TIME shows its sign once and hours past 24 (`-02:30:00`, `838:59:59.999999`, L11). The full value of a truncated cell is fetched on demand with `query.cell`, which
answers only while the execution is still open; otherwise it returns `unavailable`.

**Versioning:** `ContractInfo.Version` is a constant; `system.ping` returns it; the webview
refuses to run against a mismatch.

## 6. Connections, sessions and switching

A query tab owns exactly one **session**; a session is bound to one (profile, database) and
borrows **physical connections** from a per-profile pool. Switching tabs switches sessions, which
costs nothing because nothing is reconnected.

| Object | Identity | Lifetime | Holds |
|---|---|---|---|
| `Profile` | profileId | SQLite | host, port, auth kind, user, TLS settings, optional SSH tunnel (bastion host, port, user, password/key file), environment (Local/Dev/Test/Staging/Prod), color, group; passwords and key passphrases only in keyring |
| `Pool` | profileId | While any session is open + 5 min idle | 0..N physical connections, max 8 per profile (setting) |
| `Session` | sessionId | While its tab is open | profileId, current database, txState, pinned connection (nullable), execution queue |
| `Execution` | executionId | Until result sets are closed | reader, cancellation token, timing, result schemas |

**Rules**

1. `session.open` is lazy: nothing is dialed until the first `query.execute`, `meta.*` or
   `session.state` call.
2. A session with `txState == none` holds no connection between executions; it borrows one from
   the pool per execution and returns it.
3. `tx.begin` pins a physical connection to the session until commit/rollback. Pinned
   connections are never shared. (An active Trace also pins, so the SPID filter stays valid.)
4. Two sessions on the same profile run queries concurrently on different physical connections.
   Pool exhaustion queues the execution and reports `query.queued` to the UI.
5. Closing a tab with an open transaction prompts: Commit / Roll back / Cancel (Cancel is the default and keeps the
   tab). Never silently rolls back, and a transaction is never left open without its tab; a commit or rollback that
   fails keeps the tab. Closing a tab whose query is still running asks first ("Cancel the running query?", Keep tab
   is the default), cancels, waits for the run to stop and reads the session state again before rule 5 applies;
   bulk closes keep running and open-transaction tabs and say how many they kept (§16). An **unknown** transaction state
   (`TX ?`: the server could not say) counts as open everywhere (M9): tab close, bulk close, disconnect, switching
   connection or database, the update dialog, the restart banner and quitting ask about it ("may have an open
   transaction"), and the sidecar refuses `session.close` of an unknown-state session unless forced, as for an open one.
   A COMMIT or ROLLBACK the user asked for that succeeds but leaves the state unreadable closes the pinned connection
   (the server ends whatever is left) and the state is none, so a tab is never stuck at `TX ?` (not for a session-owned
   connection, whose in-memory database would go with it). Grid edits and imports read an unknown state on the connection
   first and write nothing when it stays unknown (a transaction of their own would end or nest into the user's).
6. Pool connections are validated with a 1-byte roundtrip (`SELECT 1`) only if idle for more
   than 30 s.
7. Every physical open reads the connection's own server identity (`SessionKey`: spid + `login_time` / pid +
   `backend_start`) before it is handed out, so the activity monitor can recognise Querithm's connections. A failed
   connection fails the open and frees the slot. When the server refuses the read itself on a working connection (an
   explicit `DENY` on `sys.dm_exec_sessions`, `pg_stat_activity` revoked, a compatible server without the view or column,
   no own row returned), a tab or dedicated connection is kept unidentified and serves queries, and the monitor refuses
   every kill while one is open (`querithmUnidentified`, §16); the monitor's own connections must identify themselves,
   so a monitor on such a server stops with the reason.

**Connection ownership (Phase D0d).** A provider declares per profile who owns its connections (`LifetimeFor`): **pooled**
(servers: the rules above) or **session-owned** (file and memory databases): each session opens one connection on first
use — through a one-slot pool of its own, so probing and the lifecycle invariants below hold — and keeps it pinned until
the tab closes, transaction or not. Nothing else borrows it: helpers (definitions, scripting, metadata refresh, compare
reads) take the session's execution lock (`SessionManager.LeaseAsync`), so they wait for a running query instead of sharing
the connection. Closing the tab follows the same lock: once the close is accepted it cancels the leases (`SessionLease.Token`)
and the session's runs (the §7 escalation), then takes the lock before it closes the connection — at most
`CloseWaitLimit` (15 s, longer than the escalation), after which it closes anyway and logs it. A pinned server connection
closes the same way. The connection never closes under a command still reading on it: DuckDB keeps the database, and on
Windows its file lock, until that reader goes, and the file could not be opened again. A session-owned connection that is lost is discarded once; an in-memory database is then marked lost and
every later use says so (`memoryLost`) — no helper or reconnect ever opens a fresh, empty in-memory database in its place
(`OpenDedicatedAsync` refuses memory profiles). A file database without a tab is read by one short dedicated connection.

**Physical connection lifecycle** (`Querithm.Core/Sessions/Pool.cs`; every transition is a compare-and-set under the
pool lock, every I/O runs outside it)

| From → to | Trigger | Rule |
|---|---|---|
| (none) → Probing | a borrower takes a free slot | open + identity read outside the lock; failure → slot released, nothing registered; a read the server refuses on a working connection → registered unidentified (rule 7) |
| Idle → Probing | a borrower picks an idle connection | validation (idle > 30 s) and re-identification run outside the lock; a probing connection is invisible to other borrowers |
| Probing → Leased | probe succeeded | `LeasedBy` = the session id |
| Probing → Idle | probe cancelled, connection still open | returned like any other connection, never dropped |
| Leased → Idle / next borrower | `ReturnAsync` (not pinned): the transaction state is read first, outside the lock; `Return` only where the caller just read it as none (after an execution) | handed straight to the first queued waiter, else idle; returning twice is a no-op |
| Leased (pinned) | open transaction, active Trace | stays leased to its session between executions; never handed to anyone |
| any → Discarded | broken on return, returned inside an open or unknown transaction or with an unreadable state (Phase D0b), a closed session left a transaction open, pool closed | disposed once, slot freed once (idempotent) |
| shutdown | `CloseAllAsync` | queued borrowers fail `poolClosed`; idle connections disposed; leased ones on return |

Invariants (tests in `ConnectionLifecycleTests` with fakes that detect overlap and I/O under the lock, and in
`ConnectionLifecycleIntegrationTests` on both engines): one command at a time per physical connection; no database I/O
under the pool lock; a probing or leased connection is never handed to another borrower; no double dispose and no double
slot release; a queued borrower that is cancelled (or reaches its queue bound) after being granted passes the slot or
connection on; a cancel never rolls back the user's transaction and keeps the session on its pinned connection (a
server-side rollback, SQL Server `XACT_ABORT ON`, is detected by the post-execution transaction read and reported);
a connection lost while pinned is discarded once and the user is told. The monitor's own connections (monitor, kill
control) are dedicated, never pooled, and a kill never runs on a tab's connection.

**Database switching per engine**

| Engine | Mechanism | Cost | UI promise |
|---|---|---|---|
| SQL Server | `SqlConnection.ChangeDatabase()` on the borrowed connection; pool key stays the profile | One `USE` roundtrip | "instant" is allowed |
| PostgreSQL | A database is a separate connection string; pool key becomes (profile, database) | Full connect (TLS, auth) on first use, then pooled | Show a spinner the first time; never say instant |
| MySQL / MariaDB (Phase D2) | COM_INIT_DB (`ChangeDatabase`) on the borrowed connection; `USE db` in a script also switches the tab | One roundtrip | Like SQL Server |
| SQLite (Phase D1) | None: a connection is one database file (`main`, plus the session's `temp`); `session.changeDatabase` refuses with the reason | — | The database picker shows `main` only |

A session's database is shown in the tab header and the status bar. Changing it is an explicit
action (dropdown, or `USE` typed in the editor, which the tier-1 scanner detects and offers to
apply to the session). Executing a batch that contains `USE x` changes the session's database
for subsequent batches and updates the header.

**Environment safety**

- `environment == Prod` gives the tab a 2 px band in the profile color (fitted to 3:1 on every tab surface) and the
  status bar its top edge; the default Prod color is red. Other environments have no band: on a tab the database name
  carries the environment color (§12, §16).
- First execution on a Prod target per app launch shows a confirm dialog with the server and
  database names. The target is the profile as stored now — environment, engine, host, port, file, default database —
  and the tab's database (L1, 2026-11-03): a profile edited to Prod guards its open tabs at once, a changed host or
  another database asks again, and a tab opened as Prod stays guarded after the profile is marked otherwise, until it is
  opened again (its connections still go where the Prod profile pointed).
- Statements classified destructive (DELETE/UPDATE without WHERE, DROP, TRUNCATE, ALTER, MERGE … THEN DELETE on Prod)
  are detected by ScriptDom before execution and require a second confirm. Detection is on the
  AST, not regex. Dynamic SQL counts too (L5): `EXEC('…')` / `sp_executesql N'…'` of literal text is classified as a
  script of its own, and text built at run time (`EXEC (@sql)`), PostgreSQL / DuckDB / MySQL `EXECUTE` of a statement
  not prepared in the same text, `PREPARE … FROM @var` and a DO block's `EXECUTE format(…)` cannot be checked, so they
  count as destructive (`dynamicSql`); `PREPARE … AS / FROM 'literal'` classifies the stored statement (MySQL's literal
  with a charset introducer, `_utf8mb4'…'`, included), as do MariaDB's `EXECUTE IMMEDIATE 'literal'` and SQL Server's
  legacy `sp_sqlexec 'literal'`; `sp_MSforeachtable` / `sp_MSforeachdb` run a command template once per table or
  database and always count as `dynamicSql` (8a review). T-SQL literal dynamic SQL is parsed without the parse cache, so
  a script of many `EXEC('…')` never pushes the editors' parses out (`TSqlParsing.ParseUncached`; the text cache holds as
  many entries as the document cache, 16). SQLite has no SQL-level dynamic SQL. The same guard covers query.execute, plan.get and debug.attach (§7, §13 rule 7).
- The connection manager groups profiles by group name; a group can set a default environment
  and color.

## 7. Query execution pipeline

Every execution is a state machine running on the sidecar's thread pool; the UI only ever sends
two messages (`execute`, `cancel`) and reads frames.

**States:** `Queued → Running → Streaming(resultIndex) → Done | Failed | Cancelled`.
Transitions are published as `session.stateChanged` and `query.*` notifications. A run is `Cancelled` only when the cancel
stopped work still to do (M10): requested while a statement was still running — even one whose driver then returns
normally, as MySQL's `KILL QUERY` makes `SELECT SLEEP(n)` return 1 — or before a later statement. One that lands after the
last statement completed (while the run reads the batch's warnings, restores session options or reads the state) leaves
it `Done`, or `Failed` with its first error.

**Steps in `ExecutionService.RunAsync`**

1. Resolve the session; if `txState == open` use the pinned connection, else borrow from the
   pool.
2. Split the text into batches on `GO` (SQL Server only, ScriptDom batch separator; never a regex
   over string literals). Each batch is one driver command; the whole set is one execution.
3. Build the command: `CommandTimeout` from the profile (default 0 = none),
   `CommandBehavior.SequentialAccess`, and for SQL Server `SET STATISTICS IO, TIME ON` when the
   option is set.
4. Register `query.cancel` → `CancellationTokenSource.Cancel()` plus `SqlCommand.Cancel()` (TDS
   Attention) / `NpgsqlCommand.Cancel()` (CancelRequest). Both are out-of-band and work
   mid-result-set. **Cancel always ends the run** (closing the tab and the idle watchdog take the same path):
   - after 5 s a run still going has its connection closed where the driver allows it
     (`IDbProvider.CloseAbortsCommand`: SQL Server only — SqlClient has no cancellation timeout of its own, and a
     server that never acknowledges the Attention would hold the run forever at the default command timeout of 0).
     The connection is claimed under the run's gate and closed on its own task, never under a lock; the run discards
     it, and the lost-connection path tells the user an open transaction is gone. PostgreSQL and MySQL break such a
     connection themselves after their driver's `CancellationTimeout` (closing them from outside is not an abort:
     Npgsql's Close drains the command, MySqlConnector's breaks the pending call); SQLite and DuckDB interrupt
     in-process and are never closed mid-command.
   - after 10 s a run still going is told as it is (8a review — "Restart the engine" there was wrong for runs that end by
     themselves): one past its statements and finishing its cleanup (a rollback, restoring options, the state read, each
     under `Cleanup.Limit`) gets an info line that the tab frees itself within 60 s; one still in its driver call a warning
     that it may end by itself (the drivers give up on an unanswered cancel) and that a restart frees it otherwise; only a
     run still going 60 s later (`StuckLimit`, 70 s after Cancel) is reported as stuck (error) with the way out. 15 s after
     Cancel the webview shows a warning banner offering **Restart engine** ("it may still end by itself within a minute").
   - a run cancelled while queued behind another one ends at once and leaves the running one's state alone.
5. Subscribe to `SqlConnection.InfoMessage` / `NpgsqlConnection.Notice` and forward each, with severity, line number and
   procedure name, through the run's coalescing batcher as `query.messages` (§5, M13).
6. `ExecuteReaderAsync`; for each result set: emit schema frame, read rows into an Apache.Arrow
   `RecordBatch` builder, flush every 10,000 rows or 4 MB, emit `0x03` on `NextResult()`.
7. On completion emit `query.done` with `RecordsAffected`, client elapsed, and server elapsed
   parsed from `STATISTICS TIME` messages when present. Never present client elapsed as server
   time.
8. Dispose reader, command, and return the connection unless pinned. On any exception after a
   transaction was pinned, keep it pinned and report `txState` so the user decides.

**Guards (§13 rule 7).** `query.execute`, `plan.get` and `debug.attach` run caller SQL, so all three call one guard
(`QueryGuard.Check`) before anything runs: the classifier's refusal (file and configuration policy, a hard error), then on
Prod the first-run confirm (`GuardReason.ProdFirstRun`) and the destructive confirm (`Destructive`; unparsable text
counts as destructive), each answered `{blocked}` and sent again with the confirmation. Querithm-generated writes use
`QueryGuard.Tool` (`ProdWrite` every time). Prod is read from the profile as stored now and the tab's own snapshot — the
stricter of the two — and the first-run confirm belongs to a target, not only a profile (§6). Cleanup that runs after a
cancel (rollbacks, restoring session options, the state reads after a run) is bounded by `Cleanup.Limit` (60 s): a server
that stopped answering cannot hold a run, a tab or a pooled connection for good; when it fires the connection is treated
as lost.

**Row fetch limits.** Default `maxRows = 50,000` per result set, editable per tab and settable to
unlimited (`0`; the sidecar refuses a negative limit). When the limit is hit the sidecar stops reading, emits `0x03` with `truncated = true`,
and the grid footer shows "50,000 of ? rows". Unlimited mode spills to a temp Arrow file on disk
when a result set exceeds 512 MB in memory, and the grid reads slices from the file (not built yet, §16).

**Transactions.** Autocommit is the default. `tx.begin` is explicit (toolbar, or `BEGIN TRAN`
detected by ScriptDom with a prompt to pin). The top bar shows `TX` with its elapsed time next to
Commit / Rollback (its border goes to full strength after 5 minutes; Commit / Rollback wait while a
query runs); an unknown state shows `TX ?` with both; the status bar notes the pinned connection.
Every begin, commit, rollback, savepoint and state read — `tx.*`, grid edits, import savepoints and the generic
INSERT loader, data compare's read-only reads, actual plans (§11) — goes through the provider's
`ITransactionControl` (Phase D0b); its `TransactionTraits` record each engine's rules (autocommit default, DDL implicit
commits, savepoints and release, read-only transactions, whether a failed statement aborts the block). A commit is strict:
when the server already ended the transaction it fails (SQL Server 3902) and the state is read again, so it is never shown
as committed; a rollback is a no-op when nothing is open. A COMMIT that is stopped (`TxEndLimit`, the tab closing) or
whose connection drops never says "try again" (8a review): the state is read again; still open → "stopped before it ran:
the transaction is still open"; ended, unreadable or connection gone → `txOutcomeUnknown` ("whether the transaction
committed is unknown: the server may have rolled it back. Check the data …"; the tab shows none, `TX ?` or none). The
session remembers it (`Session.CommitOutcomeUnknown`, cleared by BEGIN and by a COMMIT / ROLLBACK that ran): the next
COMMIT first reads the state — as does any COMMIT while the state is unknown — and one that finds no transaction fails with
`txNone` ("no transaction was open … the COMMIT that was stopped earlier either committed it or the server rolled it
back") instead of succeeding, as PostgreSQL's warning-only and MySQL's silent COMMIT outside a transaction would. A
ROLLBACK that is stopped reports success when the state reads none, "still open" when open, and otherwise asks to roll
back again or close the tab; one whose connection drops says the server rolled back. If the engine itself stops during a
COMMIT, the webview says the outcome is unknown rather than showing the transport error. Executing `COMMIT`/`ROLLBACK` text also updates txState by inspecting connection state after the
batch (SQL Server: `@@TRANCOUNT` on the pinned connection after each batch; PostgreSQL:
`NpgsqlConnection` transaction status).

**Parameterized execution.** The editor recognizes `@name` (T-SQL) and `$1`/`:name`
(PostgreSQL) that are not declared in the script; the run dialog collects values and passes them
as driver parameters, never by string substitution.

**Grid editing (Phase 8, M1).** `query.resultSet` carries `edit: ResultEditInfo`, decided in the
sidecar from the provider's exact column origins and the cached schema: editable only when every
table column comes from one base table and the result holds its primary key (else a NOT NULL unique
index); otherwise read-only with the reason on the grid's badge. Edits are staged in the webview
(`src/results/staging.ts`: cell edits, new rows, deletions, undo/redo) and never sent on blur.
Lost updates are caught with the engine's row version — PostgreSQL `xmin`, SQL Server a
`rowversion` column — read lazily for edited rows (`data.rowTokens`, which also checks that the
displayed values still hold); without one, the original values of the changed columns are compared
as typed parameters (float, LOB, xml/json and 7-digit time values are not compared, and the preview
says so). `data.applyPatch` re-validates the table and columns against the sidecar's own metadata
(the webview names no table), builds parameterized statements (deletes, updates, inserts), runs
them as one execution through the same lifecycle as `query.execute` (queueing, pinned connection,
history, notifications) in one transaction — or a savepoint inside an open one — reading SQL
Server's `@@ROWCOUNT` straight after each statement (trigger DML inflates `RecordsAffected`) and
generated keys through `OUTPUT … INTO` a table variable / `RETURNING`. Any statement matching no row
rolls everything back and returns the server's current rows as conflicts. On Prod every such write
needs confirmation (`GuardReason.ProdWrite`, not only the first run) and deletes the destructive
confirm, enforced in the sidecar. Apply is all or nothing whatever ends it (H5): a cancel (Cancel interrupts the running
statement as it does a query's), a lost connection or any other exception rolls back to the savepoint, or out of the
patch's own transaction, before the error goes back (`cancelled`: "nothing was written"); a savepoint the server already
dropped (a doomed SQL Server transaction, SQLite after an interrupted write) takes the whole transaction and the tab is
told; when no rollback can run the connection is closed and the run reports it lost (the server rolls back). The COMMIT
runs under `Cleanup.Limit`, not the run's token: once every statement ran, a cancel is too late to change anything. A
COMMIT the server refuses on a working connection reports nothing written (`applyFailed`); one cut short by a lost
connection reports `outcomeUnknown`, and the webview then refuses to apply that staging again until the query is re-run,
so its inserts are never sent twice. In the webview one Apply per staging is in flight (H6): a second Ctrl+S, a key repeat
or the button joins the first instead of writing again. Editing stays open meanwhile, and a successful Apply commits
exactly what its patch carried (W2): a cell edited again while the patch was on its way keeps its newer value staged
over the written one, a new row added meanwhile stays new, other edits and deletions stay staged, and a sent new row
edited meanwhile keeps those edits on the row it became; a failed Apply leaves everything staged. An engine without savepoints (DuckDB) refuses grid edits and
imports inside an open transaction up front with the reason (L15).

**Import (Phase 8, M3).** Explorer → database → Import data…, table → Import data into this table…,
or Ctrl+Alt+I. The file is picked with the shell's dialog: Rust (`src-tauri/src/files.rs`) keeps the
path and hands the webview a token; the IPC relay refuses a `filePath` the webview names (a null is dropped), refuses
`import.inspect` / `import.run` without a token that resolves, and injects the token's path (M1), and a token runs one import only: `import.run` holds it until
the reply, and only a reply with a `jobId` spends it, so the confirmed retry after a Production guard
block still works (§13 rule 6 holds for the sidecar too; the dev bridge mirrors this with an upload). `FileInspector` detects the format (CSV,
TSV, JSON array, NDJSON, XLSX via `System.IO.Compression` + `XmlReader`, no package), the encoding
(BOM, strict UTF-8, else Latin-1), the delimiter and the header, then profiles **every** row in one
streaming pass: narrowest type per column on a lattice (Int32 → Int64 → Decimal → Double, Date →
DateTime; leading zeros stay text, only ISO dates count), max length, NULLs with their first rows,
HyperLogLog distinct estimate. The target check runs against the sidecar's cached metadata: a new
table gets an editable CREATE TABLE; an existing one gets per-column verdicts (fits / widen with the
ALTER / mismatch / NULL into NOT NULL / missing / required but unmapped). All DDL runs through the
normal guarded `query.execute` first (its frames discarded in the shell; the wizard waits at most 10 minutes for each
statement, then cancels it and says so, and an engine that stops meanwhile ends the wait with "whether it took effect
is unknown"). The load runs as one execution (history, guards — Prod asks
every time) with the provider's `IBulkLoader`: SQL Server `SqlBulkCopy` (streaming, name mappings,
the user's options, its own `SqlTransaction`; per-chunk transactions in batches mode so the committed
count is exact), PostgreSQL `COPY … FROM STDIN (FORMAT csv)` (the server's input functions parse every
type), and a generic multi-row INSERT for engines without a native path. Values that do not convert
reject their row into a reject log (`<data dir>/imports/*.rejected.csv`, reason per row); the load
stops after the user's limit (default 0 = first bad row). Cancel rolls the transaction back (to the service's savepoint inside the
user's transaction); when that rollback cannot run the connection is closed, as for a grid Apply (H5). Every COMMIT a loader
sends of its own (one transaction, or each batch) runs under `BulkTransaction.InCommit`: one the server refuses on a working
connection is a failure that wrote nothing (that batch); one that finds its transaction already gone — SQLite and DuckDB
check before sending and throw `NoTransactionOpenException` (an interrupted statement rolled it back) — sent nothing and
says so ("nothing was written"; 8a review: it read as an unknown outcome, and grid Apply's COMMIT is treated the same);
one cut short — the connection lost, a cancel — may or may not
have committed, so `import.progress` ends with `errorCode: "outcomeUnknown"` (the code grid Apply uses) and the reason, and
the wizard will not import into that table again in this run of the app until the user ticks that they checked it
(`src/import/uncertainImports.ts`; a later import into it that succeeds clears it), so a blind retry cannot load the rows
twice.

**Import limits (L8, `ImportLimits`).** Profiling reads every row before anything loads, so a crafted file is refused at
`import.inspect` (`importFile`, the limit named) instead of filling memory: at most 4,096 columns (CSV / TSV fields per
record, distinct JSON keys, XLSX columns holding a value; SQL Server allows 1,024 per table, 30,000 wide, PostgreSQL
1,600, MySQL 4,096, InnoDB 1,017, SQLite 2,000, DuckDB more — a file may be wider than its target, unmapped columns are
skipped, so the widest common limit bounds the profile), 64 Mi characters per value (8a review: it was the grid's 1 Mi
text limit, which refused real files such as a megabyte's base64; profiling holds one record at a time, a value at the
limit is 128 MB as a .NET string, and past it MySQL's default `max_allowed_packet` of 64 MB refuses the value anyway; an
unterminated CSV quote stops there and the message names the line it opened on), 128 Mi characters per record (twice the
value limit; a CSV record, an NDJSON line, a JSON array's object), and the preview's 200 rows within 16 M characters — a
value longer than the grid's 1 Mi characters is previewed cut there, as the grid would show it, and imported whole. An .xlsx
has at most 10,000 archive entries; each part is read only up to the size its archive declares (more is refused), a
worksheet up to 4 GB uncompressed (streamed), the shared strings (kept in memory) up to a sixteenth of the memory the
runtime may use, between 128 MB and 512 MB (512 MB on the 14 GB §14 machine; a larger workbook is refused with "save the
sheet as CSV … and import that"), other parts up to 64 MB, and a part of
16 MB or more that expands over 200 times its compressed size is refused as a decompression bomb. Empty formatted cells
(often out to column XFD) add no width. A JSON key first seen at row N counts rows 1…N−1 as NULLs at once instead of
replaying them.

**Grid wiring (webview).** `ResultsPanel` holds one Arrow table per result set and feeds Glide's
`getCellContent` by `(row, col)` lookup on the Arrow vectors; no row objects are materialized.
Sorting and filtering operate on an index array over the Arrow table (client side, up to 1M
rows). Export to CSV/JSON runs in a web worker over the same Arrow data.

## 8. Metadata cache and local store

One SQLite database at the OS app-data path (`%APPDATA%/querithm/store.db`,
`~/.local/share/querithm/store.db`, `~/Library/Application Support/querithm/store.db`), WAL
mode, owned by the sidecar. Autocomplete and the explorer read only this file; they never wait on
a server. The schema is in `sidecar/Querithm.Core/Store/Schema.sql` and matches the original
spec's tables: `profile` (no password column, ever), `schema_snapshot`, `obj`, `col`, `param`,
`fk`, `idx`, `definition_cache`, `history` + `history_fts` (FTS5), `script`, `setting`, and `snippet`
(the snippet library, user data never logged).

**Schema versions.** `PRAGMA user_version` is `LocalStore.Version` (6). Opening a file written by an
older build runs the migration steps first (v1 → v2: `obj.parent_object_id` added and every
`schema_snapshot` marked `stale`, so the next refresh lists triggers; v2 → v3: the `snippet` table; v3 → v4:
`profile.location` and `profile.file_grant_id`, NULL — a server — for every existing profile, Phase D0a; v4 → v5:
`history.preview` and `history.sql_length`, NULL in older rows, L17; v5 → v6: `obj.no_rollback_engine` and
`idx.not_key` for grid editing, every `schema_snapshot` marked `empty` so its next refresh is a full one that fills them), then
`Schema.sql`, all in one transaction: a crash mid-migration leaves the file at its old version and the
next open runs the steps again. A file from a newer build keeps its version.

**Upgrades, damage, two instances (M22).** The transaction is `BEGIN IMMEDIATE` and `user_version` is read inside it, so
a second app instance opening the same old file waits for the write lock (busy timeout, then the driver's retry) and
then sees the new version: one migration runs. Before the first step on an existing file, a copy of it as the older
build left it — WAL content included (`VACUUM INTO` on a second connection while the migrating one holds the lock
unwritten) — is kept as `store.db.v<old>.bak` (0600), the newest two kept; a copy that cannot be made is logged and the
upgrade goes ahead. At start (`LocalStore.OpenOrRecover`) a file SQLite reports damaged — `SQLITE_CORRUPT` /
`SQLITE_NOTADB`, or an open or upgrade that fails some other way (not busy, locked, full, read-only, I/O, permission)
followed by a failing `PRAGMA quick_check` — is moved aside with its -wal / -shm / -journal as
`store.db.corrupt-<UTC time>` and a fresh store is made; the webview tells the user once (`system.notices` →
banner: profiles and history were reset, keyring passwords untouched, the kept file's name). quick_check does not run on
a good open: it reads every page (measured 52 ms for a 29 MB store with a warm cache), which the cold start has no room
for. **Downgrade rule:** an older build keeps working on a newer file only while every step since its version is
additive — new tables, new nullable columns, data it may ignore; a change that renames, drops or redefines what an older
build reads needs a new file (and a new rule here), never an in-place step. **A downgrade is never damage (8a review):**
an open that fails on a file whose `user_version` is newer than the build's (read through SQLite, else from the header's
bytes 60–63), or with a schema that does not parse ("malformed database schema" is `SQLITE_CORRUPT` too), throws
`StoreRefusedException` and leaves the file exactly as it is — no `.corrupt-` copy; the host runs that session on an empty
in-memory store and says why once (`system.notices` code `storeRefused`: update Querithm, or move store.db aside; nothing
made in this session is saved). Busy / locked (another connection holding the file exclusively), read-only and I/O
failures are thrown as before and never set aside (`StoreRecoveryTests`).

**History (L17).** Every run's text (secrets masked, L4) goes to `history` with its first 500 characters as `preview`
and its length; `history.list` / `history.search` (the history dialog asks for 300) send the preview — an older build's
rows are cut on read — and the dialog fetches one entry's whole text with `history.get` when it is opened. The FTS index
holds whole texts. Retention (`history.retention` / `history.setRetention`, the `history.retention` setting, edited in the
history dialog): entries older than 90 days, and the oldest beyond 50,000, are deleted by default (1–3,650 days,
1,000–1,000,000 entries). A pass runs 30 s after the sidecar starts, after every 500 runs and when the setting changes, on
the thread pool, one pass at a time, deleting 1,000 rows per statement (each its own short write transaction; the delete
trigger keeps the index in step). The first pass that deletes anything on a store — on the first start after an upgrade,
every entry past 90 days — raises one `historyPruned` notice naming the setting (8a review: it went without a word); it
waits in `system.notices` and `system.noticeAdded` tells the webview to take it now (a banner, or a toast when a banner
already shows); never again on that store (`history.retentionNoticed`).

**Secrets an older build kept (8a review).** Rows written before L4 kept a typed password as is, and L4's first mask
`'***'` was itself a literal (re-running `ALTER ROLE app PASSWORD '***'` from history set the password to `***`). The v5
step re-masks every history row with its engine's rules (`LocalStore.HistoryRedactor`: the row's profile's engine; every
engine's rules in turn when the profile is gone), the error text too, and keeps `history_fts` in step (its terms held the
secrets: each changed row is deleted from and re-added to the index, then the index is optimized), with `secure_delete` on
for the step so the old text is overwritten in the file. It reads every history row once, at that upgrade only; a failure
leaves the rows as they were (a savepoint) and the upgrade goes ahead. **The copy made before the upgrade
(`store.db.v<N>.bak`) keeps the old content as it was** — by design it is the file the older build left, so it can be put
back — until a later upgrade's copy replaces it (the newest two are kept); the WAL may hold old pages until SQLite's next
checkpoint.

**Triggers in the snapshot.** DML triggers are objects of kind `trigger` whose `parent_object_id` (in
the DTO `SnapObject.parentObjectId`) is their table or view. SQL Server: `sys.objects` types `TR`/`TA`
(database DDL triggers are not in `sys.objects` and stay out); PostgreSQL: `pg_trigger` rows that are
not `tgisinternal` (the FK triggers), marker `(pg_trigger.xmin << 32) | trigger function's xmin`, so
`CREATE OR REPLACE TRIGGER` and a replaced function both count as a change; definition
`pg_get_triggerdef`. The trigger function stays a `func`. Triggers are listed in the explorer under
their table, and are left out of every name lookup (completion, refactor targets), since SQL never
names them and a PostgreSQL trigger may share its table's name.

**Refresh algorithm (`MetadataRefresher`)**

1. `meta.snapshot` returns the cached rows immediately and marks the snapshot `stale` if
   `refreshed_at` is older than 10 minutes or the session just connected. The background refresh starts only when the
   profile can be opened unattended (`SessionManager.CanOpen`, Phase D0a): a remembered secret for a password/token sign-in;
   always for Windows/Kerberos (before the first open and after a sidecar restart too); a browser or device-code sign-in only
   after the user signed in once in this sidecar; a file database with its injected grant; never for a memory database.
2. A background job per (profile, database) — or per (profile, database, session) for a session's own view — fetches
   `modify_date` for all objects in one query. **Session scope (Phase D0d):** `meta.snapshot`, `meta.refresh`, `meta.objects`,
   `meta.objectDetails`, `meta.search`, `lang.complete` and `lang.signature` take an optional `sessionId`, which must be a live
   session of the profile. An in-memory database's snapshot is that session's alone; objects a provider marks
   `SessionLocal` (temporary tables) go to the session's view, laid over the shared snapshot; both live in an in-memory SQLite
   store (`SchemaCache.Ephemeral`), never on disk, and are dropped when the session closes (a refresh still running then
   writes nothing). `meta.updated` carries `sessionId` for a session's view. A session-owned refresh runs on the session's own
   connection after its execution; while that session's transaction is open or unknown it is deferred (the shared snapshot
   is marked stale, never given uncommitted objects) and runs once the transaction ends (`TransactionEnded`).
   SQLite (Phase D1): one query over `main.sqlite_schema` and `temp.sqlite_schema`; the marker hashes each object's SQL plus,
   for a table, its indexes' and triggers' SQL (foreign keys are in the table's SQL); ids are stable hashes of (schema, type,
   name); `temp` objects are session-local; details come from `pragma_table_xinfo` / `pragma_index_list` / `pragma_index_info` /
   `pragma_foreign_key_list` in one query each. Server engines, as before:
   SQL Server: `sys.objects` joined to `sys.schemas`; PostgreSQL: `pg_class`/`pg_proc` with
   `pg_namespace`, using `xmin` as the change marker since PostgreSQL has no modify date.
3. Objects whose `modify_date`/`xmin` changed, were added, or were removed are diffed; only those
   have their columns, params, indexes and FKs re-fetched (one batched query per table, chunked
   by 500 object ids).
4. Writes happen in one SQLite transaction; `meta.updated` fires after commit with the count of
   changed objects. The webview invalidates only the affected completion entries.
5. After any DDL executed through the app (ScriptDom classifies the batch), the refresher runs
   immediately for that database.
6. Full refresh (user action) truncates the metadata tables for that (profile, database) and
   reloads.

**Lazy explorer.** The object tree loads one level per expand; a database with 20,000 tables
shows the Tables node with a count and renders children in a virtualized list. Nothing is fetched
from the server on expand unless the snapshot is `error`.

**Password retention.** Profiles store `passwordRetention` (`once`, `session`, `duration`, `forever`) and optional `passwordRetentionMinutes`, never a password. New profiles default to `once`; absent policy on legacy profiles means `forever`. Rust keeps `once` and `session` passwords in memory: `once` lasts until explicit server disconnect or app exit, and `session` until app exit. `duration` uses an OS-keyring entry under `<profileId>:timed`, containing the password and an absolute expiry timestamp, so durations such as 7 days survive app restarts. Expiry is checked before every credential use against wall time and, within the process, monotonic elapsed time; expired vault entries are removed when accessed. The UI offers minute/hour/day presets and custom units. Reading or testing a credential does not extend its deadline. The editor and sign-in dialog share these options. Changing to connection/app-session retention deletes the persistent keyring entry. “Forget” removes both indefinite and timed vault entries. Expiry requires a password for new sessions, while existing sessions may continue using their pool credentials. SSH secrets retain their separate keyring setting.

**Secrets.** The sidecar never sees a password at rest: Rust reads its memory cache or the keyring at `session.open`
time and passes the secret in the RPC params over stdio, which is process-local. The sidecar
holds credentials in memory for active sessions and connection pools, and clears temporary credentials when the last session closes.

## 9. Autocomplete engine

Two tiers answer every keystroke: tier 1 in the webview answers in under 1 ms from a cached
snapshot; tier 2 in the sidecar refines with a real AST. The popup never waits for tier 2; tier 2
results replace tier 1 items in place if they arrive while the popup is open.

**Tier 1: `src/editor/completion/scanner.ts` (webview, synchronous).** A hand-written tolerant
tokenizer for the active dialect (keywords, identifiers incl. `[bracketed]` and `"quoted"`,
strings, comments, numbers, operators, `@variables`, `$n` params). From the token stream it
computes a `CursorContext` (clause, statementStart, tablesInScope, ctes, variables, qualifier,
prefix, execTarget).

Rules: statement boundaries are `;`, `GO`, and a statement-starting keyword at depth 0;
`tablesInScope` is collected from the whole enclosing statement (so `SELECT o.| FROM Orders o`
works); `WITH name (cols) AS (...)` registers a CTE; after `EXEC name` the context is `execArgs`;
the qualifier is resolved against aliases, then CTEs, then schemas, then databases.

| Context | Items offered |
|---|---|
| from, join, update, insertInto, delete | tables, views, TVFs, CTEs, synonyms; schema-qualified unless `dbo` |
| select, where, on, groupBy, having, orderBy, set with no qualifier | columns of all tablesInScope (alias-prefixed when more than one table), aliases, variables, scalar functions, keywords |
| any clause with qualifier = alias | that table's columns only |
| on | columns, plus FK-based join condition snippets `a.Id = b.ParentId` |
| exec | procedures, sorted by recently used |
| execArgs | the target proc's parameters as `@name =` snippets with type hints |
| declare | types |
| use | databases |
| insertColumns / values | columns of the INSERT target; a `VALUES (...)` placeholder snippet |

Ranking: exact prefix > camel-hump match (`cId` → `CustomerId`) > substring; within a rank,
columns of the nearest table first, then usage count. Keywords are never ranked above schema
objects when the context expects an object.

**Tier 2: `Querithm.Intelligence.TSql` (sidecar, async, debounced 120 ms).** `lang.complete`
parses with ScriptDom (error recovery), walks the AST to the fragment containing the offset, and
resolves CTE and derived-table projections, `SELECT *` expansions, temp tables and table
variables, proc parameter types for `EXEC`, and scope inside a procedure body (parameters, local
variables, cursors). Parse results are cached per docId and reused for diagnostics, signature,
folding and the destructive classifier. M12 (2026-11-03): the full ScriptDom parse (AST and token stream) is kept for at
most 16 documents, least recently used out first, plus 8 by text alone (classification of a run right after the editor
parsed it): a 10,000-line procedure's parse retains 24 MiB (`GC.GetTotalMemory`, this machine), and the cache only pays
off for the documents being edited now; before, every tab or cell ever opened kept its parse. The lexed `DocModel`
(256 documents) and the PostgreSQL-family caches (`PgDocument`, `DocumentScan`, 64 each, shared by PostgreSQL, MySQL,
SQLite and DuckDB) keep their bounds. When the webview disposes a model (a closed tab, a deleted notebook cell) it sends
`lang.forget {docId}`, and every engine drops that document from all of these at once. A parse of a 10,000-line proc must complete under 100 ms;
if not, tier 2 parses only the enclosing batch.

**Monaco wiring.** Completion provider (trigger chars `. @ ( ,` and space) returns tier 1
synchronously with `incomplete: true`, then re-triggers when tier 2 items differ; signature help →
`lang.signature`; definition → read-only tab from `meta.definition`; hover from the snapshot;
diagnostics via `setModelMarkers` debounced 300 ms; Monarch tokenizer per dialect; folding from
tier 2's AST.

**PostgreSQL** uses the same tier 1 scanner with a `pg` dialect table (quoted identifiers, `$$`
bodies, `::` casts). Tier 2 (`Querithm.Intelligence.PgSql/Completion`, `CompletionTier =
"tier1+tier2"`) is **scope-aware completion on `PgTokenizer` tokens, not a full parse** — there is no
PostgreSQL parser in the sidecar, so diagnostics stay empty and the server reports syntax errors on
execution. It resolves:

- statement scopes (`PgScopeBuilder`): FROM / JOIN / `,` sources with aliases, visible inside the
  SELECT level that declares them (correlated and LATERAL subqueries see outer ones, sibling subqueries
  do not); CTEs with explicit `(cols)` or inferred projection columns (`*` and `t.*` expanded, `AS`
  and bare aliases, `::` and `CAST` types, `count`/`sum`/… result types); derived tables and `VALUES`
  with alias column lists; set-returning functions (snapshot columns or an `AS x(a int, …)` list);
  INSERT / UPDATE / DELETE / MERGE targets (their columns for `SET`, `RETURNING` and data-modifying CTEs);
- inside `$tag$` bodies (`PlpgsqlBlockScanner`, nested bodies included): the CREATE FUNCTION /
  PROCEDURE header's parameters (named, or `$n`) and `RETURNS TABLE` columns, `DECLARE` variables with
  types (`%TYPE` resolved, cursors), record fields from `FOR rec IN query`, `SELECT … INTO rec`,
  `%ROWTYPE` and a table's row type, and `NEW` / `OLD` / `TG_*` in trigger functions. Block scoping is
  approximated: a name counts from its declaration to the end of the body. An unterminated `$$` (while
  typing) runs to the end of the document;
- temp tables the script creates (`CREATE TEMP TABLE` with columns, `LIKE`, or `AS query`), anywhere
  before the cursor; `INSERT INTO t (|` column lists (already listed ones left out); named arguments
  `p => ` for `CALL proc(|` and function calls; signature help with overloads and the active (or named)
  parameter.

Columns are alias-prefixed when several sources are in scope, like tier 1; names that need it are
quoted (mixed case, reserved words). Anything it cannot place answers nothing, and tier 1 alone stands.
Metric 27 gates its latency.

## 10. Stored procedure workflows and object explorer

**Open for edit.** Explorer → Modify fetches `meta.definition`, rewrites `CREATE` to
`CREATE OR ALTER` (SQL Server 2016 SP1+, else `ALTER`) / `CREATE OR REPLACE` (PostgreSQL), and
opens a tab with `boundObject = {profileId, database, objectId, definitionHash}`.

**Apply (Ctrl+Shift+S).** (1) Fetch the current server definition and compare its hash with
`boundObject.definitionHash`; if different, open the DiffEditor (server vs. yours) and require an
explicit Overwrite. (2) Run the script as one execution on the tab's session; on success refresh
metadata for that object and update `definitionHash`. (3) On a compile error, map the server's
line number to the editor (batch start line + reported line) and set a marker.

**Compare.** Any two definitions open in the DiffEditor, whitespace-insensitive by default.
Script as CREATE/ALTER/DROP+CREATE/EXEC goes through `IObjectScripter` (SMO first, catalog
fallback). Script as EXEC puts one parameter per line, with a comment giving its type and whether it is
required: SQL Server reads defaults from the definition's ScriptDom AST (`sys.parameters.has_default_value`
is always 0 for T-SQL; status omitted for encrypted modules); PostgreSQL writes `CALL`/`SELECT * FROM`
in named notation (positional when an argument is unnamed or VARIADIC) and marks the trailing
`pronargdefaults` inputs optional.

**Execute a procedure.** A parameter form generated from `param` rows produces
`DECLARE @out ...; EXEC schema.proc @p1 = ..., @out = @out OUTPUT; SELECT @out;` in a new tab.

**Dependencies.** `meta.dependencies` returns *uses* and *used by* from
`sys.sql_expression_dependencies` (SQL Server) and `pg_depend` + `pg_rewrite` (PostgreSQL).

**Object explorer.** Server → database → {Tables, Views, Procedures, Functions, Types, Sequences,
Synonyms} → object → {Columns, Keys, Indexes, Parameters, Triggers}; no "Databases" level, and the
`isSystem` databases other than the profile's default fold into a "System databases" folder (PostgreSQL's
`postgres` is flagged system, but is usually the default). A flat database lists the four main categories
always and the others when they hold objects; objects in the engine's default schema (`dbo` / `public`)
show their bare name, others `schema.name`. The tree lists the servers in use — connected, connecting
or failed — and the pinned profiles (the Connect dialog's pin list; grey when not connected); the
other saved profiles get no row (the header's Connect… opens them), and "Show all saved profiles" (the
⋯ View menu) lists them all (§16). A connect the user starts
opens the server one level (its databases); a background reconnect (startup, engine restart) restores
the expansion saved in the workspace file, by name (§16), and nothing opens deeper by itself. Each
server row shows its state in place: a spinner while connecting (the cached subtree stays through an
engine restart), "Failed" with the reason, Reconnect and Edit profile… one level down, a grey name with
a Connect action when not connected; a click selects and never dials, Enter / double-click / the caret
connects. Prod shows the word and a shield, never a tint (the top bar is the loud place, §12). Database
rows show loading, refreshing, "cached hh:mm" after a failed refresh over cached objects, and an
OFFLINE / RESTORING state that does not expand. Search is scoped to the active query tab's database
and names it (environment, server › database) before anything is typed; there is no fallback to
another server: no tab, no database, a server away, a catalog still loading each say so. Matches are
`schema.name` substrings, name-prefix first, grouped by kind with the true count ("TABLES · FIRST 300
OF 686 · Show all"); Enter opens the first result, Ctrl+Enter inserts its qualified name, ↓ moves into
the results, "Reveal in tree" opens its path. One kind filter (a menu in the search field; Synonyms
unavailable on PostgreSQL) applies to tree and search. Typing inside a folder of objects filters that
folder in place ("Tables 6 / 686"); elsewhere typing jumps to the next row with that prefix. Context
menus open at the pointer or on Shift+F10 / the Menu key, name their target in a header line
("PROD · Primary Billing › SalesDB · dbo.Orders"), put the default action first and bold, group the
rest (open, Script as ▸ — CREATE, ALTER, DROP and CREATE, DROP, INSERT / UPDATE / DELETE, EXECUTE /
CALL, ENABLE / DISABLE — insert / copy, analyze, tools, Refresh "db"), and show an action the row cannot
take with the reason (Debug on SQL Server, Rename on a synonym, server tools while not connected,
ENABLE / DISABLE of a PostgreSQL trigger on a view). Script tabs are titled by the verb ("DROP
dbo.Orders.sql"). Drag a table inserts `schema.name`, Alt-drag (or Ctrl+Alt+Enter, or dragging its
Columns folder) a column list, a column its name. When the server row of a deep row scrolls away, one
sticky row shows the path ("⛨ PROD Primary Billing › SalesDB › Tables"). Servers are ordered Local →
Dev → Test → Staging → Prod, pinned first within an environment, and are never reordered by state;
"Group by environment" puts them under collapsible environment rows. A database's context menu toggles "Group by schema",
remembered per profile and database: Database → schema (engine default first) → non-empty
categories → objects without the schema prefix.

**ER diagrams (Phase 8, M2).** Explorer → table → View ER diagram (1-hop / 2-hop / all connected,
`+` expands a card's hidden neighbours), database or schema → View ER diagram (schema filter,
compact cards with key columns only, the default above 150 tables), and Ctrl+Shift+E (the tables of
the statement at the cursor, from the tier-1 scanner). Built entirely from the webview's
`SchemaSnapshot` (`src/erd/erdLayout.ts`), no server roundtrip; React Flow + dagre, laid out in a web
worker above 100 tables, `onlyRenderVisibleElements`. Edges run from the FK column's row to the
referenced key's row (dashed when a FK column allows NULL); hovering one highlights both columns.
Shift+Click cards → Generate SELECT: shortest FK paths (fewest joins, then fewest nullable FK
columns) grown into one tree from the first table, aliased and quoted per dialect; tables that no
FK path connects are refused (never a cross join). Export draws a pure SVG from the layout (no
`<foreignObject>`, which WebKitGTK cannot rasterise reliably) and rasterises it for PNG.

**Schema compare (Phase 8, M5).** Explorer → database or schema → Schema compare…, or Ctrl+Shift+D
(the tab's database as the source). Each side is a live database (optionally one schema; two schemas
are matched by name and the source is rewritten into the target's schema on the token stream) or a
saved `.qschema.json` model (picked through the shell; the relay injects `filePath` into the side, as
for imports). Each provider's `ISchemaCompareDialect` reads an engine-neutral model from its catalog
in one batch (tables, columns with defaults/identity/computed/collation, keys, checks, indexes, FKs,
views, routines, triggers, sequences, types, PostgreSQL enums/domains/extensions) and lists what it
leaves out. `SchemaComparer` (Core) classes every object Source only / Target only / Different /
Identical with tags (Columns, Indexes, Constraints, Body, WhitespaceOnly, Options): expressions and
bodies are compared on the dialect's tokens (whitespace, comments, keyword case, redundant outer
parentheses and the CREATE / OR ALTER / OR REPLACE header ignored), server-named constraints are matched
by shape, and renames are never inferred — a dropped and an added column of one type are offered as a
possible rename, otherwise drop + add. `MigrationScriptBuilder` orders the checked items: FKs that are
removed or touch altered columns, removed modules and blocking dependents (schema-bound modules on SQL
Server, any recorded dependent on PostgreSQL; dependents first), schemas/types/sequences, tables
(computed columns over an altered column are dropped and re-added; SQL Server drops and recreates the
keys, checks, indexes and defaults on it), keys and indexes, FKs, then modules in dependency order
(Kahn), including the dependents it dropped. Drops and type changes are left out unless "Include
data-loss changes" is ticked. SQL Server wraps it in `SET XACT_ABORT ON; BEGIN TRAN` and follows every
`GO` batch with `IF @@ERROR <> 0 OR @@TRANCOUNT = 0 BEGIN IF @@TRANCOUNT > 0 ROLLBACK; SET NOEXEC ON;
END`, ending `COMMIT; SET NOEXEC OFF`; PostgreSQL uses one `BEGIN … COMMIT`. The script is only text:
**Open in query tab** puts it in a tab bound to the target, so the usual Prod and destructive guards
apply when it runs. Comparing also fills `definition_cache` for the database (`DefinitionFetcher`,
batch `GetDefinitionsAsync`; encrypted modules are recorded as unavailable).

**Data compare (Phase 8, M6).** Explorer → table → Compare data…, database → Data compare…, or the
palette. Each side is a table (resolved against the sidecar's own snapshot) or one read-only query (SQL
Server: a single `SelectStatement` without INTO on the ScriptDom AST; PostgreSQL: one statement inside a
`READ ONLY` transaction, so even a data-modifying CTE is refused by the server). Columns pair by name
(case-insensitive across engines); the key is the source's primary key unless the user names columns.
Both sides are read at once on two pool connections, every column through `IDataEditDialect.ValueText`
(the grid's text form), ordered by the key in an order the comparer reproduces: text under
`Latin1_General_100_BIN2` / `COLLATE "C"` compared by code point (trailing spaces ignored where the
engine pads), GUIDs by their canonical text (so `uniqueidentifier` and `uuid` line up), numbers and
instants by value. `DataComparer` merge-joins the two streams; a side whose keys repeat, are NULL or
arrive out of that order stops the compare with a reason instead of reporting false pairs. Values are
equal after per-type normalisation (decimal scale, bit/boolean, GUID and hex case, char padding, float
epsilon, time tolerance — options). Identical rows are only counted; differences (first 50,000 by
default) stream to the grid as Arrow batches: a visible `diff` column (≠ / + source / − target), hidden
`_diff_kind` (u8) and `_diff_mask` (bitmap over the compared columns), the key, then `col` / `col__target`
pairs, lit per cell. The sync script (deletes, updates of changed writable columns, multi-row inserts with
`IDENTITY_INSERT` / `OVERRIDING SYSTEM VALUE` when an identity is copied) opens in a query tab bound to the
target, where the usual guards apply.

**Find usages and rename (Phase 8, M7).** Shift+F12 / F2 in the editor, or Explorer → object → Find
usages / Rename…. `refactor.symbolAt` asks the provider's `IReferenceFinder` what is at the cursor:
`TSqlReferenceFinder` walks the ScriptDom AST with query scopes (FROM, DML targets, CTE names per
statement) so aliases and unqualified columns resolve through the snapshot; `PgReferenceFinder` does
the same on `PgTokenizer` tokens, function bodies included. A local symbol (variable, parameter, CTE,
alias) is renamed in the editor only. Find usages first fills `definition_cache` (`DefinitionFetcher`),
then scans every view and routine that names the target as a whole word (a column: also its table, since
the finder resolves a column only through its table in the same statement) in parallel; texts over 100
KB are cut into statement chunks like the diagnostics and only the chunks that name it (or may build
dynamic SQL) are parsed; scans are cached per definition and target. Every hit is `exact` or
`possible` (an unqualified column several tables in scope could own; any unqualified name inside a
PL/pgSQL body, which may be a variable). Modules that build SQL at run time or are encrypted are listed
as "cannot be checked"; PostgreSQL views and `BEGIN ATOMIC` functions are stored parsed and follow a
rename by themselves (views keep their output column names, which the preview says). The rename
preview rewrites the accepted hits (quoting kept), and the script — M5's guarded wrapper — on SQL
Server drops what blocks `sp_rename` (computed columns, checks and filtered indexes that use the column,
schema-bound modules), renames, recreates them with the new name and `CREATE OR ALTER`s every rewritten
module; on PostgreSQL it is `ALTER … RENAME` plus `CREATE OR REPLACE` for text-bodied routines. It opens
in a query tab on the database; open tabs get their edits as one undo step.

**Formatting.** `lang.format` uses ScriptDom's script generator with settings (keyword case,
indent, comma position, newline before JOIN). PostgreSQL uses sql-formatter in the webview.
Format on save is off by default.

**Local scripts.** Tabs autosave to `app-data/scripts/<tabId>.sql` every 2 s after a change and
restore on launch with their bound profile and database (tab sessions are not connected until
used). The Object Explorer's servers (profile ids only) are saved with the workspace and reconnected
in the background after launch, without prompting (§16). Save As writes a user file through the Rust
fs plugin. M23: each recovery copy and `workspace.json` is written to a temporary file of its own
(`<name>.<ms>-<n>.tmp`) and renamed into place, so a crash or another writer never leaves a cut file (a temporary a crash
left goes after an hour); the workspace that parsed at start is kept as `workspace.json.bak`, read when the file does not
parse (the user is told). Scripts no saved tab names are offered back with **Recover N unsaved scripts** (new tabs; the old
copies are then removed; empty ones go at once). A failed write shows a warning toast, at most once a minute; a rename
is first tried again 3 times 100 ms apart (an antivirus or the indexer briefly holding the file on Windows). W5: a second
window (not the workspace owner) keeps `scripts/<window>.live` — `{at, ids}` — fresh every 60 s and lists each tab in it
before that tab's first copy is written; the owner never offers (or removes) a copy a beat younger than 3 minutes lists,
removes older beats (a window that crashed: its copies are offered as usual), and a second window removes its beat when
it quits, so its copies are offered at the next owner's start. Same folder, so no new fs permission.

## 11. Execution plans, trace and debugging

The app ships one real step debugger (PostgreSQL) and one trace tool (SQL Server), and the UI
never calls the second one a debugger.

| Engine | Estimated | Actual | Parse to |
|---|---|---|---|
| SQL Server | `SET SHOWPLAN_XML ON` on a borrowed connection, run, `OFF` (a connection whose `OFF` fails is closed, never pooled with SHOWPLAN on); text that sets SHOWPLAN_XML / _ALL / _TEXT or STATISTICS XML / PROFILE itself is refused (ScriptDom `PredicateSetStatement` / `SetStatisticsStatement`; unparsable text with a SET token too), since the batches after it would run | `SET STATISTICS XML ON` while the query runs (`query.execute` with `actualPlan`); the plan arrives as an extra result set after each statement. `plan.get` refuses the actual mode with that way (§16) | ShowPlanXML → PlanNode tree |
| PostgreSQL | `EXPLAIN (FORMAT JSON, VERBOSE, COSTS)` | `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` inside a transaction that is always rolled back — inside the tab's open transaction a `SAVEPOINT querithm_plan`, rolled back to and released, so the user's work and `TxState` stay; a failed (aborted) transaction refuses the savepoint and the plan says why. The user is asked first: the statement runs | JSON → PlanNode tree |
| MySQL / MariaDB (Phase D2) | `EXPLAIN FORMAT=JSON` per statement: JSON v1 (MySQL 8.x, MariaDB) and v2 (MySQL 9's default operator tree) are both read; format `mysql-json` (raw = `[{text, plan}]`, plan as the server returned it) | SELECT and TABLE statements only (a write or DDL could not be undone — refused with the reason): `EXPLAIN ANALYZE FORMAT=JSON` (v2) on MySQL 9, `EXPLAIN ANALYZE FORMAT=TREE` text on 8.0.18+, `ANALYZE FORMAT=JSON` on MariaDB | v1: query blocks, nested loops, ordering/grouping steps and table accesses; v2 / TREE: operators and their inputs (costs cumulative, actual rows and times per loop × loops); MariaDB `r_rows` / `r_loops` / `r_*_time_ms` → PlanNode tree |
| SQLite (Phase D1) | `EXPLAIN QUERY PLAN` per statement; rows (id, parent, detail) as JSON, format `sqlite-eqp` (also what `plan.parse` reads) | none (refused with the reason before anything runs) | id/parent → PlanNode tree; the detail text is kept opaque (no costs or row estimates) |
| DuckDB (Phase D3) | `EXPLAIN (FORMAT JSON)` per statement, format `duckdb-json` | `EXPLAIN (ANALYZE, FORMAT JSON)` inside `BEGIN TRANSACTION` … `ROLLBACK`, so the statement's changes are undone; DuckDB has no savepoints, so inside an open transaction it is refused with the reason (the estimated plan still works). The user is asked first | operators with `operator_cardinality` / `operator_timing` → PlanNode tree |

**plan.get is guarded and is an execution (H1, M5, 2026-11-03).** The text goes through the query guard (§7: refusals,
Prod first run, destructive statements — for the estimated plan too) and then runs as one execution of the tab, so it
queues behind a running query, shows in the tab like a run (Messages, the plan pane, history for actual plans with a
note that they were rolled back) and Cancel ends it through the §7 escalation. Core, not the provider, wraps an actual
plan: `BEGIN` … `ROLLBACK` through the provider's `ITransactionControl`, or `SAVEPOINT querithm_plan` when the tab has a
transaction open (Unknown counts as open); text that begins or ends a transaction itself is refused, and an engine
without savepoints refuses inside an open transaction. When the rollback fails or takes longer than `Cleanup.Limit`, the
connection is closed (the server rolls back) and the tab is told, so the plan's changes never stay in a pooled
connection or in the user's transaction. A provider can refuse before anything runs (`IPlanProvider.Refusal`): SQLite's
actual mode, MySQL / MariaDB writes, SQL Server's actual mode and its SHOWPLAN / STATISTICS escapes. The debugger's
`debug.attach` call goes through the same guard.

`PlanNode = {id, operator, physicalOp, estRows, actualRows?, estCost, subtreeCost, elapsedMs?,
warnings[], properties{}, children[]}` is engine-neutral, rendered with React Flow + dagre
left-to-right, edge thickness by rows, badges for the top 3 subtree costs, warnings for
MissingIndex, implicit conversions and spills, and a properties panel.

**SQL Server: Trace (Extended Events).** `trace.start({sessionId, scope: 'spid'})` creates
`querithm_trace_<spid>` with `sp_statement_starting`, `sp_statement_completed`,
`sql_statement_completed`, `error_reported`, `module_end`, filtered on the session's `@@SPID`,
target `ring_buffer` (max 4 MB). The sidecar polls `sys.dm_xe_session_targets` every 500 ms and
emits `trace.event` rows `{ts, procName, line, offsetStart, offsetEnd, durationUs, rowCount,
error?}`. `trace.stop` drops the session; the sidecar drops any `querithm_trace_*` it owns on
shutdown. Requires `ALTER ANY EVENT SESSION`; if denied, Trace is disabled with the reason.
Variable values are **not** available in Trace; the context menu offers *Insert PRINT/SELECT for
@var at cursor*.

**PostgreSQL: Debugger (pldebugger).** Shown only if `pldbgapi` is installed. Two connections:
a *target* that runs the function and a *control* that drives it: `pldbg_create_listener`,
`pldbg_oid_debug`, `pldbg_wait_for_target`, `pldbg_set_breakpoint`/`pldbg_drop_breakpoint`,
`pldbg_step_over`/`pldbg_step_into`/`pldbg_continue`/`pldbg_abort_target`,
`pldbg_get_variables`/`pldbg_get_stack`/`pldbg_get_source`. The debugger UI is engine-neutral
in `src/debug/`.

**Query statistics.** `SET STATISTICS IO, TIME ON` messages are parsed into a per-statement table
(logical reads, scans, CPU ms, elapsed ms) under Messages.

## 12. Frontend architecture

Dockview workspace; per-tab state in one Zustand slice keyed by tab id, so switching tabs is a
pointer change.

| Region | Default | Contents |
|---|---|---|
| Top bar, 40 px | Always | Logo · back / forward · New query ▾ · launch strip (environment badge, engine, connection › database joined to Run ▾ / Cancel) · Explain ▾ · transaction (Begin Tx, or `TX mm:ss` · Commit · Rollback) · search · history · settings · update |
| Left dock, 280 px (200–600), docked / auto-hide / closed | Explorer | Header, 40 px so its bottom line continues the tab strip's (Connect…, View ⋯, pushpin, close) · search scoped to the active tab's database with a kind filter and its scope line · the virtualized tree (§10) |
| Center group | Query tabs | One model per tab in the group's shared editor (`setModel`, below); line 1 = markers (TX pill, spinner, lock) · script verb · name without `.sql`; line 2 = engine mark · database in the environment color fitted to every tab surface, never cut (the tab's minimum width grows to the whole name, past 184 px if it must; owner revision 2026-11-04) · server muted, cut with "…" when there is no room, no environment word (the color, Prod's band, the tooltip, the top bar and the accessible name say it); a disconnected tab leads line 2 with a plug and "disconnected" (`src/app/TabIdentity.tsx`) |
| Center group | Notebook tabs | A `.sqlbook` notebook (`TabState.notebook`, `src/notebook/`): a query tab whose panel shows cells instead of one editor, with the same header, session, top-bar pickers, Run / Cancel, transaction and close rules; saved with the workspace like a query tab |
| Center group | Tool tabs | ER diagram, schema compare, data compare, activity monitor (`src/store/toolTabs.ts`): the same two lines with a kind icon (compare tabs read source → target, the band follows the target), the close slot and the tab menu; they open where query tabs open, count in the strip order, "≡", Ctrl+P, Ctrl+Tab, Alt+N and Ctrl+PageUp/PageDown; not persisted; while one is active no query tab is active |
| Bottom panel of the active tab, 35 % (12–85 %), collapsible, maximizable, docked under or right of the editor | Results / Messages / Plan ┃ Runtime / Usages | One panel per query tab; it follows the tab (see Bottom panel below) |
| Status bar, 24 px | Always | server · database · server address · TX state · execution timer · row count · sidecar health (plain text; the 4 px top edge in the environment color). The address is the profile's host or IP as typed, never resolved, with the port only when it is not the engine's default and "· SSH" when tunnelled; Prod and Staging show it at ≥ 1280 px, others at ≥ 1440 px; narrower, it is in the connection's tooltip. File and in-memory databases have none. The tab tooltip carries the same "Server …" line (compare tabs: source → target) (`src/connections/serverAddress.ts`) |

The top bar is two WAI-ARIA toolbars (one Tab stop each, arrow keys inside). Each state is loud in one
place: the top bar carries the environment badge (solid only for prod), the database and connection
pickers, what Run will do (`+plan` while the actual plan is included, `Continue` while the debugger is
paused) and the transaction clock; the tab strip and the status bar repeat them quietly. Hovering or
focusing Run, or an item of its menu, marks in the editor exactly the text it would run (one Monaco
decoration, the environment color on prod; `src/editor/runScope.ts`). Run and Cancel are one element
that ignores pointer clicks for 400 ms after it swaps, so a double-click cannot run and then cancel.
Comment / uncomment live in the editor's right-click menu, on their shortcuts and in the palette.
Below 1440 px labels drop, never functions; the environment, Run / Cancel and the transaction never
collapse.

The Explorer behaves like a Visual Studio tool window. Its title bar has a pushpin (docked ↔
auto-hide) and a close button. In auto-hide it collapses to a vertical side tab with one mark per server
in use, shaped by state (a dot in the environment color when connected, a ring while connecting, a
warning sign when it failed; the tab's name gives the counts); hovering (optional) or clicking the tab
slides the panel out over the workspace without resizing it. A hover-opened flyout closes when the
pointer leaves; a clicked one closes when focus leaves, on Esc or on a click elsewhere. Closing
remembers auto-hide, so Ctrl+B restores the same mode. The Explorer stays mounted in every state, so
its tree and search survive. The mode persists in `ui.settings`, the width (200–600 px, dragged or the
focusable edge: ←/→ 16 px, Home / End) and the expansion in the workspace file; in a window too narrow for the dock
and a 480 px workspace the dock gives way, and the stored width returns when the window widens. The tree is a WAI-ARIA
tree with one Tab stop (F6 reaches it between the top bar and the editor): the selected row is
`aria-activedescendant` and always rendered; ↑/↓, Home/End, PgUp/PgDn move, → opens or enters a level,
← closes or goes to the parent, Enter is the double-click action (a double-click on a folder opens it once), and
in the tree Ctrl+Enter, Ctrl+Alt+Enter, F2, Shift+F12, F5 (refresh) and Ctrl+C act on the row instead of the app
shortcuts (`EXPLORER_TREE_KEYS`); in the search box only Ctrl+Enter and Ctrl+Alt+Enter do (`EXPLORER_SEARCH_KEYS`,
`src/app/commands.ts`), so F5 there still runs the query. While a server reconnects (engine restart) its cached tree
stays, and every session-bound action on it says it is waiting instead of dialing a second session; `sessionFor`
waits for a connect in flight. Rows are 24 px in blocks of 16: scrolling within a block
changes nothing React sees, and a block change renders only the rows that enter (§14 metric 33). A
selected row is `--q-row-sel` + `.q-hl` with a 2 px accent bar (status text keeps 4.5:1 there, which it
does not on `--q-active`), and environment labels are fitted to every row surface (`readableOnAll`).
Below 260 px the title shortens, state words drop (icons stay) and the scope line's Reconnect becomes an icon
button; below 240 px the indent narrows to 8 px and the ACTIVE pill drops (the active database keeps its bold name
and accent icon); from 400 px the Connect action of a grey row is always shown. The scope line under the search box
reads "in ENV server › database" (both names truncate, the full scope is its tooltip), and a tab without a
connection says so rather than "no query tab". Keys and indexes of an object are read once per catalog record: a
refresh that changed the object reads them again, a status-only update does not.

The editor tab strip (`src/app/QueryTab.tsx`, `ToolTab.tsx`, `TabIdentity.tsx`, `hiddenTabs.ts`, `stripKeyboard.ts`)
is Dockview's, styled from its classes, so switching tabs re-renders no tab header (§14 metric 7b; a header
subscribes only to the fields it draws). A tab is as wide as its first line needs plus 38 px, 120–184 px (150 px
while disconnected, pinned tabs 128 px); line 2 is a CSS size container, so it never widens a tab, and it drops the
engine mark below 112 px. The active tab of the focused group is the Explorer's selection (`--q-row-sel` + 2 px accent
bar), the shown tab of an unfocused split group gets a 2 px `--q-line-strong` bar, other tabs are transparent with one
1 × 18 px separator. Long names are cut in the middle keeping their last 7 characters (the database its last 5); a
script verb is never cut and DROP, DROP and CREATE, DELETE and TRUNCATE are drawn in `--q-err`; two tabs with the same
name and line 2 add their file's folder. An open transaction is the strip's one loud mark: a solid
`--q-warn-solid` pill "TX" → "TX 12m" → "TX 2h", aged by one 30 s clock only the pills subscribe to. The close slot is
24 × 20 at the end of line 1, so line 2 runs the tab's full width: the modified dot at rest, × on hover, keyboard focus
and the active clean tab. A tab cut to its width lends the slot's room to its name, which then runs to the tab's end
and fades under a hover's ×; an active, modified, pinned or renaming tab keeps the room. Pinned tabs stay at the left
while the strip scrolls (up to 40 % of it). The strip ends in an always-present 48 px button: "≡ N" lists the tabs out
of view, and a TX pill (goes to that tab) and a running count report hidden tabs that hold a transaction or run a query;
24 px fades mark the side that hides tabs, and the active tab is scrolled into view after every resize.

Each strip is a WAI-ARIA tablist with one Tab stop (the tab it shows) and manual activation: ←/→, Home/End move focus,
Enter/Space activate, Delete closes (guarded, §6 rule 5), F2 renames, Shift+F10 or the Menu key opens the tab menu,
Esc returns to the editor; F6 reaches the strip between the Explorer and the editor. The accessible name is
"fix_invoice_totals, Production, Primary Billing, database Billing, open transaction" (running, disconnected,
read-only, modified and pinned too), the transaction's age its description. The tab menu (`src/app/TabMenu.tsx`) names
its target with a `ServerChip`, puts Close first, counts its bulk closes and says why one is unavailable, and offers
Move ▸ and Split right. Ctrl+P is a modal dialog with a combobox over a listbox, 88 px from the top, that starts on the
previous tab and searches the tabs' SQL (first 4 KB) and SPIDs as well, with each tab's first statement as a third
line; Ctrl+Tab says "3 of 30" and reads the highlighted tab through a live region. Both draw rows with the tab's own
identity, colors fitted to their surfaces. One polite status region (`src/store/announce.ts`, ≤ 1 message a second, a
burst read as its first 5 messages and "… and N more")
reports bulk closes and, unless turned off in Settings, what happens out of sight: a background query finished, a
server disconnected, a transaction left open when a tool tab takes over.

Untitled tabs take a verb-first name from their first statement at their first run ("UPDATE Invoice", read from
tokens; once, never over a name the user gave; a setting turns it off). Go to definition activates a tab already showing
the object; otherwise the strip's preview tab (italic, "Preview") shows it, replacing the previous preview, until it is
kept by double-click or pin. Recently closed tabs are capped by size (8 MB of SQL); a bulk close is one "Reopen 29 tabs"
entry, and reopened tabs go back to their strip and position.

**Bottom panel** (`src/app/BottomPanel.tsx`, `panelKit.tsx`, `RuntimePanel.tsx`, `src/results/`; redesigned 2026-11-01
by a seven-persona panel, merged winner: One Frame + Statement Ledger). Every view uses the same rows, set in `panelKit`:
header 30 px · ledger and tools 28 px · notice 24 px (only when needed) · body · footer 22 px; toolbars never wrap
(below 1100 px the ledger and the tools take a row each, below 760 px the ledger folds into one chip with the first
error chip pinned beside it and secondary actions move into ⋯ menus). The header is a WAI-ARIA tablist of five fixed
views — Results, Messages, Plan (about the shown run) ┃ Runtime, Usages (about the tab) — with SVG icons and text
badges (result sets, "✕ 2" errors, plans, "● Live" trace, "Paused" debugger, usage hits); views never appear, move or
switch on their own. Runtime holds Trace (SQL Server Extended Events) and Debug (PostgreSQL pldebugger) behind a
Trace | Debug switch; the side an engine cannot use keeps its place, says why and offers what the engine has (Trace →
Debug or Explain › Actual, Debug → Trace). The header's right end holds the run picker (the 3 runs the tab keeps;
an older run opens read-only, its notice says so, Edit is off with the reason), dock right / under (`panelDock`,
saved), maximize (the editor keeps a 28 px strip with its first line and Restore) and hide. The separator is a
focusable `role="separator"`: arrows ±5 %, Home/End to the limits, Enter hides.
The **ledger** (`src/results/ledger.ts`) is one chip per result set and per error, in the order they happened (each
result set records how many messages had arrived when it began, `ExecutionView.setAfter`), shared by Results and
Messages; a set chip shows its rows, a "limit"/"stopped" state and the change since the previous run of the same SQL
(+4, only for complete, untruncated sets of identical text). Plan's chips are the plan's statements. Ctrl+click (or the
chip menu, Shift+F10, or the palette's "Compare the shown result set with another") shows a second result set — of
this run or any kept run — beside the first, read-only; differing cells are tinted and the footer counts them (first
2,000 rows, by position). **Errors never take the view away**: earlier result sets stay, and the error reaches the user
through the red chip, the notice line, the Messages badge, the editor marker, the status bar and one announcement per
run (polite, assertive when a statement failed; focus never moves). A run without result sets shows its summary in
Results (row counts, PRINT output, or its error card). The **notice line** holds, in order, an older-run snapshot, the
run's first error and a row-limit cut ("Fetch all rows"; Copy and Export of a cut set warn that they hold the fetched
rows only); errors and the limit are added when the run ends, never while rows stream, so the line never moves the grid
mid-scroll; it can be dismissed until the next run. The **Results tools** are Grid | Chart, the filter with its column
scope as a prefix, Edit (or Read-only with the reason: an older run, an interrupted set, a closed session, the
sidecar's reason), Copy (split: with headers, IN list, Markdown) and Export. Edit is per result set (found by its
sidecar index, never its position) and ends when another set, run or the chart is shown: coming back starts read-only.
**Edit mode replaces the tool row** and keeps the filter: the environment as a word (Prod solid), a 3 px environment
rule, counts, then a fixed order that never hides a button — + Row, Duplicate (Ctrl+D: selected rows as new rows; key
columns are never copied: DEFAULT when they have one, else empty for the user to fill), Delete rows, undo/redo, ⋯
(Duplicate, Revert selected rows, Discard all, Review SQL), Apply, Cancel. An unavailable button stays in place,
`aria-disabled` with its reason as the tooltip (Apply with nothing staged or invalid values; all of them while an Apply
runs, whoever started it). Cancel is the one way out (there is no separate Done: it would only repeat Cancel): it
leaves at once when nothing is staged and otherwise asks "Discard N staged changes…?" (Keep editing is the default).
Apply keeps the grid in edit mode, so edit → Apply → edit needs no new Edit click. On Prod and Stage one "Review & apply to
PROD…" button always shows the SQL first (`applyChanges`; the sidecar's ProdWrite guard still decides); the ledger is
locked while changes are staged. The
**footer** describes the shown result set (rows, "212 of 1,234" when filtered, streaming, row limit, incomplete; the
Arrow batch count is its tooltip) and the selection statistics next to the data: Count · NULL · Distinct · Sum · Avg ·
Min · Max, "≈" when sampled; run timing lives in the status bar only. **Messages** is the run's log in arrival order with a
"Result set N began" marker where each set started, All | Errors, Copy all (split: errors only), Clear; STATISTICS IO /
TIME and Diagnostics collapse to one summary line each (other engines say why and point to Explain › Actual); logs over
400 entries render a window that moves in blocks of 50. **Plan** has Graph | Table: the table is a treegrid of
operators (↑/↓/Home/End, Enter opens the inspector) with #1–#3 rank badges and misestimates in words ("34× more rows
than estimated"); the graph's flame emoji became a "#1" badge and skewed edges are labelled. The panel's chrome
(ledger counts, badges, footer) follows a streaming result at most every 250 ms (`useThrottledVersion`); the grid
follows every batch itself (§14 metric 35).

Monaco models are created once per tab and disposed on close; switching tabs calls `editor.setModel()` on a shared
editor instance — one per visible Dockview group (normally one; a split adds one, `src/editor/editors.ts`, §16), never
one per tab.

| Action | Shortcut |
|---|---|
| Execute selection or all | F5, Ctrl+Enter |
| Execute current statement | Ctrl+Shift+Enter |
| Cancel | Alt+Break, Ctrl+Shift+C |
| New tab on same connection | Ctrl+N |
| Command palette | Ctrl+Shift+P |
| Go to object (search) | Ctrl+T |
| Change database | Ctrl+U |
| Change connection | Ctrl+Shift+U |
| Estimated plan / actual plan | Ctrl+L / Ctrl+M |
| Format | Ctrl+Shift+F |
| Toggle results | Ctrl+R |
| Show the shown result set as a grid / as a chart | Ctrl+1 / Ctrl+2 |
| Notebook (focus in the notebook): run the focused cell, run it and move to the next, run every cell | F5 or Ctrl+Enter, Shift+Enter, Shift+F5 |
| Toggle object explorer / show and search it | Ctrl+B / F8 |
| Explorer (tree focus): open / close a level, default action, menu | → / ←, Enter, Shift+F10 |
| Explorer (tree focus): insert name / column list, rename, find usages, refresh, copy name | Ctrl+Enter / Ctrl+Alt+Enter, F2, Shift+F12, F5, Ctrl+C |
| Explorer (search focus): open the first result / insert its name / its column list | Enter / Ctrl+Enter / Ctrl+Alt+Enter |
| Apply procedure | Ctrl+Shift+S |
| Grid (editable result): apply staged changes / NULL / new row / delete rows / undo / redo | Ctrl+S / Ctrl+0 / Alt+Insert / Ctrl+Delete / Ctrl+Z / Ctrl+Y (grid focus only) |
| ER diagram (current statement's tables, else the database) | Ctrl+Shift+E |
| Import data from a file | Ctrl+Alt+I |
| Schema compare (the tab's database as the source) | Ctrl+Shift+D |
| Find usages (object, column or local name at the cursor) | Shift+F12 |
| Rename (previewed; local names in the editor) | F2 |
| Go to definition | F12 |
| Go back / go forward (tab switches and cursor jumps of more than 10 lines; also mouse back/forward buttons and the top bar arrows) | Alt+Left / Alt+Right (macOS: Ctrl+- / Ctrl+Shift+-) |
| Move focus: top bar → Explorer → tab strip → editor → results (the keyboard way out of the editor, where Tab indents) | F6 / Shift+F6 |
| Maximize or restore the results panel | Ctrl+Shift+R |
| Results panel (focus in the panel): next / previous result set or error · next / previous kept run | Alt+PageDown / Alt+PageUp · Ctrl+Alt+PageDown / Ctrl+Alt+PageUp |
| Next / previous error line of the shown run (the ledger shows it) | F4 / Shift+F4 |
| Open tabs: search titles, connections, files, SQL and SPIDs | Ctrl+P |
| Recent tabs (hold Ctrl) / next and previous tab / tab N | Ctrl+Tab, Ctrl+Shift+Tab / Ctrl+PageDown, Ctrl+PageUp / Alt+1–9, Alt+0 |
| Move a tab left / right (the focused tab, else the shown one) | Ctrl+Shift+PageUp / Ctrl+Shift+PageDown |
| Tab strip (tab focus): move focus, open, close, rename, menu, back to the editor | ←/→, Home/End, Enter/Space, Delete, F2, Shift+F10, Esc |

**Settings** (`src/settings/`, loaded on first open; Ctrl+, , the gear, or the palette's "Keyboard shortcuts",
"About Querithm" and "Check for updates…") is an 880 × 560 modal that shrinks to the 960 × 560 window. A page rail on
the left is a vertical tablist (selection follows focus, the Explorer's selection style) with eight pages: Appearance,
Editor, Formatting, Query execution, Tabs & recovery, Object Explorer, Keyboard shortcuts, About; Settings reopens on
the page used last in the session. One search box covers every page: matching rows replace the page, grouped by page,
the rail counts them, and Esc clears a search before it closes the dialog. Every preference is declared once in
`src/settings/registry.ts` (page, group, label, one-line description, control, range, engine, dependency, warning);
a test fails when a stored setting is neither a row nor listed as saved state. A row is label and description on the
left, the control on the right: native inputs only (a switch is a checkbox with `role="switch"`, three-way choices are
radio groups drawn as segments). A setting that acts on SQL Server only carries a "SQL Server" chip and says what to
use on PostgreSQL; a row that depends on another stays focusable (`aria-disabled`) and says why; risky values (no row
limit or more than 1,000,000 rows, recovery or update checks off) carry an amber note. A changed row shows a dot and a
reset button; "Restore defaults" resets one page and never touches pins or per-database grouping. Changes apply at
once; the footer says whether they were saved, and a failed save offers Retry (it is also retried when the engine
restarts). Numbers are drafts until valid and in range; nothing empty or out of range is ever stored. The store cleans
what it reads back from `store.db` (types, choices, ranges, unusable shortcuts) and, when it could not read the saved
settings at all, writes nothing until a later read succeeds, so defaults never overwrite them.
Keyboard shortcuts lists every command, bound or not, by category, searchable by name or key. A key cap is a button:
Enter records a replacement, Delete removes it, "+" adds a key; while recording, Esc cancels and Tab leaves without
recording. A user shortcut needs Ctrl, Alt or (macOS) Control, or a function key; Tab, Esc, Alt+F4, Cmd+Q, Ctrl+A/C/V/X/Y/Z
and Ctrl+K (the editor's chords) are refused with the reason, and a shortcut another command uses is offered for
Reassign instead of being duplicated (the first match would otherwise win silently). About shows the version the shell
reports (`app_info`; the dev bridge reads the same `tauri.conf.json` version, and a test keeps `package.json`,
`tauri.conf.json` and `Cargo.toml` equal), the platform and engine contract, "Copy version info" (version, platform and
contract only), the update status with the time of the last check, and the credit "Made by Taosif" with the copyright
line. The credit appears on this page only: never in the top bar, status bar, window title, logs, exports or copies.

**Toasts, the banner and screen readers** (2026-11-03, M20). Toasts and the banner reach assistive technology through the
one Announcer (`src/store/announce.ts`), not a live region of their own: errors and warnings are assertive and prefixed
"Error:" / "Warning:", info and success polite; a new banner (or new banner text) is read at once, assertively. A toast
stays 5 s (info, success), 10 s (warning) or 15 s (error), plus 50 ms for each character past 80, at most 30 s
(`toastDuration`); its time stops while the pointer is over the toasts or focus is inside them and at least 2 s is left
when it resumes. Every toast has a Dismiss button (a click on a toast that selects no text dismisses it too; Enter on
Dismiss moves focus to the next toast). Five toasts at most; the same message while it is shown is counted on that
toast ("×3") and its time starts over instead of stacking, and the Announcer drops a message already waiting or read
less than a second ago, so a burst is read once. The toast list is a `region` named "Notifications"; the banner wraps
instead of overflowing its 30 px row. Form validation is not a toast: the profile editor shows a field's problem under
it (`aria-invalid`, `aria-describedby`), focuses the first one, and shows a refusal the sidecar ties to a field
(`invalidProfile` with `data.field`) there too.

**Interface scale** (2026-11-03, L26). Appearance ▸ Display ▸ Interface scale, 90–150 % (default 100), also Ctrl+= /
Ctrl+Shift+= and Ctrl+- (steps of 10 % on multiples of 10) and Ctrl+0 (back to 100 %), announced. It sets the webview's
page zoom (`getCurrentWebview().setZoom`, `src/app/uiScale.ts`), not CSS `zoom`: page zoom keeps
`getBoundingClientRect`, pointer coordinates and fixed-position popups in one coordinate space in WebView2 and WebKitGTK,
so Monaco, the grid's hit-testing and anchored menus stay right; CSS `zoom` on the root would mix zoomed and unzoomed
coordinates (positions read from `getBoundingClientRect` and written back to fixed popups in Chromium, pointer
positions in WebKit). WebView2 reports the zoom in `devicePixelRatio`, so the Glide grid and charts draw at the zoomed resolution;
WebKit does not, so there `devicePixelRatio` reads as the screen's ratio times the scale (the grid sizes its canvas by
`Math.ceil(devicePixelRatio)`). 100 % makes no call at start. W8: the settings load only after the engine answers, so the
scale last applied is also kept in the webview's own storage (`localStorage` `querithm.uiScale`, a number) and applied in
`main.tsx` before the first render; the setting stays the source of truth and is applied once loaded (never its default
before), so the window no longer jumps from 100 % to the saved scale. Ctrl+0 inside an editable grid stays "set NULL"
(`GRID_KEYS`); while a dialog is open the keys do nothing (dialogs own the keyboard). The layout's 960 × 560 minimum is in
CSS pixels: above 100 % the window needs proportionally more (1200 × 700 at 125 %), which the row says. Known gaps, not
measured on a real window in this change: Monaco keeps the ratio it read at start for its minimap and overview ruler
under WebKit (they may look soft there; the text is DOM and stays sharp), and at a scale other than 100 % its pixel-ratio
monitor — a `(resolution: <devicePixelRatio>dppx)` media query, which the patched ratio no longer matches — does not see a
move to a monitor with another ratio until the window reloads (§16); German-layout Ctrl++ (the + key) is not
bound (the palette's "Interface scale: larger" and the Settings row work everywhere); the dev bridge keeps the setting but applies nothing (use the browser's zoom from its menu); no forced-colors
handling yet. The results grid is a `group` named "Results: result set 2, 1,204 rows" (" of N" when filtered, "so far"
while rows stream), changed by batches and filters, never by scrolling. The chart and notebook Export menus are the
app's `DropMenu` (↑/↓/Home/End, Esc returns focus to the button). The UI smoke test has no accessibility pass: a
dependency-free check (every button and field has a name) is a follow-up, since adding untested assertions to it was out
of scope here.

**Result charts** (`src/charts/`, Phase C1). The Results toolbar's Grid | Chart switch (Ctrl+1 / Ctrl+2) shows the
shown result set as a chart; the choice and the chart's spec (`{kind, x, y[], series?, agg, bins?}`, columns by name)
live in the tab per result index and are saved with the workspace, never the data; a spec read back is validated and a
spec whose columns are gone falls back to the one inferred from the column types (date + numbers → line, text + number
→ bar, two numbers → scatter, one number → histogram, text alone → bar of row counts). Bar, line, area, scatter, donut
and histogram; aggregation none / sum / count / avg / min / max; a series column splits values into series. The data
never reaches React: `ChartFeed` forwards the result's batches (only the spec's columns, as Arrow IPC, in ≤ 8 ms slices)
to `chartData.worker.ts`, which aggregates, bins and down-samples (LTTB per line, first/last/extremes kept; 1.5 points per
pixel of plot width, 512–10,000; scatter by stride; bars keep the top 200 categories + Other; 8 series at most, 3 on a
scatter); while rows stream the worker recomputes at most every 250 ms, and once more at the end. The status line says
what the chart does not show: partial data (the incomplete marker), the row limit, rows skipped for NULL, down-sampling,
folded categories and series, repeated categories without aggregation, and a precision warning when a bigint or
decimal is beyond 2^53. Lines and areas draw on uPlot (drag to zoom x, double-click resets, a hover readout); bars,
donuts and histograms on an in-house canvas (`src/charts/draw/`) with hover tooltips written as text. Series colors are
`--q-chart-1..8` in a fixed order (validated for colour-blind separation in both modes) plus a neutral Other; text uses
the text tokens; two or more series have a legend; the Grid view is the chart's table. Export: PNG (title, plot,
legend) and the chart's data as CSV through the save dialog.

**Cell viewer** (`src/results/viewers/`, Phase C2). Double-clicking or pressing Enter on a cell in a read-only grid opens
a 400 px side panel next to the grid (Esc or ✕ closes it; re-sorting closes it, since its row moved). The header gives the
column, its type and the row. A truncated cell's full value comes from `query.cell`; when the execution is closed, the
panel shows what the grid holds and says so. JSON (json/jsonb columns, or text shaped `{…}`/`[…]`) is formatted in a web
worker by a scanner that keeps numbers and strings exactly as written (cap 10 MB; deeper than 512 levels or bigger is
shown raw with the reason). XML (xml columns, or text shaped `<…>`) is formatted with DOMParser as `application/xml`.
Both are drawn as a windowed line list with fold toggles, Fold/Unfold, and a case-insensitive search (Enter /
Shift+Enter, hit and line counts) that unfolds blocks hiding a hit. A malformed value is shown raw with the parser's
error. A SQL Server showplan (`<ShowPlanXML>`) or a PostgreSQL EXPLAIN (FORMAT JSON) value offers **Open as plan**
(`plan.parse`): the plan is added at the front of that execution's plans and the Plan panel opens, so the results stay.
Binary values show their size and a hex dump (first 256 KB); PNG, JPEG and GIF, recognised by their first bytes only,
also get an image preview. Long text has Wrap and Copy. Edit mode keeps the cell editor dialog. Values never become
HTML: text is React text, and an image is an `<img>` with a `data:` URL of the recognised type.

**Notebooks** (`src/notebook/`, Phase C3/C4). New ▾ → New notebook, Open notebook… (`.sqlbook`), Import Jupyter
notebook…. A `.sqlbook` is versioned JSON (`{format: "querithm.sqlbook", version: 1, connection: {profileId, database} |
null, cells: [{id, kind: sql | markdown, source, chart?, view?, output?}]}`), validated on open as untrusted input:
≤ 20 MB, ≤ 2,000 cells, a cell ≤ 1 MB, an unknown version refused with the reason, bad cells named, unknown fields
ignored, unreadable charts or outputs dropped with a notice, a missing or repeated id replaced. A connection profile
that is not on this computer opens the notebook disconnected with "pick a connection". Results are user data: "Save
results in file" (off by default) stores each SQL cell's first result set as text, at most 1,000 rows × 50 columns,
values ≤ 4,096 characters, and the notebook then shows a warning. Open and Save use the shell's dialog + fs plugins on
the picked path, as script files do; a failed save keeps the tab modified and says the recovery copy is intact. The
recovery copy is `<data dir>/querithm/scripts/<tab>.sqlbook`, written 2 s after a change and restored on the next
launch like a tab's script; Ctrl+Shift+T reopens a closed notebook from its document. One session per notebook, opened
on the first run; every cell is one `query.execute` through the same guarded path as a query tab (`executeOnTab`:
parameters, the Prod first-run and destructive confirms the sidecar asks for). Run cell (F5 / Ctrl+Enter or the cell's
Run), Shift+Enter runs and moves on, Run all (Shift+F5 in the notebook, or the toolbar) runs the SQL cells top to bottom
and shows edited text cells formatted. The queue stops on Cancel (the running cell and the rest), on a cell the user did
not let run, and — Settings → Query execution → "Run all stops at the first failing cell", on by default — on a cell
that failed (state failed or cancelled, an error, or an error message: SQL Server's batch goes on past a statement-level
error, the error still stops the notebook); cells not reached say why. The focused SQL cell borrows the group's shared
editor (`attachEditor` with the cell's model, compact: no minimap, no line numbers; the full form returns when a query
tab takes the editor back); the other SQL cells are Monaco's colourised text on the editor's own background, so the
syntax themes' contrast checks cover them. Cell models are owned by the notebook tab (`createModel(..., owner)`), so
completion, signature help and diagnostics read its connection. Each SQL cell shows what its run did (rows, time, the
first errors, the other messages on request) and its result as a compact grid (≤ 300 px) or a chart (the same chart
view, its spec saved in the cell). Text cells render with marked: raw HTML is escaped — every text token, whatever
marked's raw-block state (`<kbd>`, `<code>`, `<pre>`, `<script>` no longer let the text after them through, M4) — images show their alt text (no
request leaves the app), only http(s) and mailto links stay links, and clicking one copies its address (the webview
never navigates). Export ▾ writes a static Markdown or HTML page — cell text, results as tables (first 100 rows), charts
as inline PNG — the HTML with its own CSP (no script, `data:` images only, `form-action` / `base-uri` / `object-src` /
`frame-src 'none'`) and every value escaped. Jupyter import takes
`%%sql` code cells (the magic line, which may hold a connection string, is dropped) and markdown cells, skips and counts
the rest, and never reads outputs; nbformat 4 only, same size limits.

Theme tokens are CSS variables defined once for dark and light; Monaco's editor chrome is generated from
the same variables. SQL token colors come from the syntax theme chosen in Settings → Appearance → Syntax colors
(`src/editor/syntaxTheme.ts`, named on the document by `data-syntax`; Standard is the default and an unknown id falls
back to it, including the High contrast, Classic and Minimal ids removed on 2026-11-01). Six themes ship, each with a
dark and a light palette: Standard and Color-blind safe, picked by a five-expert panel, and Neon, Forest, Ember and
Ocean, picked by a seven-designer panel that gave each palette its own keyword hue so no two themes look alike; those
four also keep keyword and comment ≥ 8 from every role under protan, deutan and tritan simulation, so commented-out
SQL never reads as live code. Every syntax theme keeps every token
color, comments included, at 4.5:1 on the editor, the current line, a selection on each and both diff tints; key token
pairs (keyword/name, string/name, variable/type, …) stay ≥ 15 CIEDE2000 apart in normal vision and ≥ 8 under protan,
deutan and tritan simulation, and every pair ≥ 12; a theme type may promise more (`syntaxTheme.test.ts`). A syntax
theme changes token colors and font styles only, never backgrounds or UI chrome: names in the UI use `--q-ident` and
the completion list's matched characters `--q-suggest-match`. Only `( )` take bracket-pair colors (three per theme,
cycling by depth); in T-SQL `[ ]` quote names and are not bracket pairs, so the `]]` escape is never an "unexpected
bracket". Accent color only for active tab, focus rings and the primary button;
on a tab, the environment color is the database name's color and Prod's band only. Text tokens (`--q-fg`, `--q-muted`,
`--q-accent`, `--q-ok`, `--q-warn`, `--q-err`) reach WCAG AA 4.5:1 on every surface and on their own
≤ 15 % tint; a solid fill under white text uses `--q-accent-solid` / `--q-err-solid`; `--q-faint` is
reserved for disabled, placeholder and decorative text (dimmed data — a paused monitor, a grid's dimmed cells — uses
`--q-muted`, which stays readable). Scrollbar thumbs use `--q-line-strong` (3:1 on every surface; the only sign a
panel scrolls), a disabled danger button is a plain button, and a server-bound dialog names its target with the
shared `ServerChip` ("ENV server › database", `src/app/EnvTag.tsx`). A highlighted row (selection, focus, or an
error / warning tint) carries `.q-hl`, which remaps muted text inside it to `--q-muted-hl`; colors
that sit on a tab surface or under white text go through `readableOnSurface` / `fillForWhite`
(`src/app/tabStyle.ts`). Control boundaries and the focus ring reach 3:1 (WCAG 1.4.11): fields and
the launch strip use `--q-line-strong`, Run / Cancel the full `--q-ok` / `--q-err`, and `--q-line`
stays distinct from `--q-hover` so a bordered control keeps its outline on hover.
`src/theme/__tests__/contrast.test.ts` checks the tokens (text 4.5:1, boundaries 3:1), every `.q-btn`
variant (at rest and on hover) and every fill + text class string in the components; the UI smoke audits each screenshot in both themes for covered, overlapping or clipped
controls, popups cut off by their host, lines through controls, and spilling, wrapped or unreadable labels (`scripts/bench/layout-check.mjs`).
The bottom panel's rows clip what overflows them (28 px), so a popup opened from one renders outside it (`DropMenu`, or a
portal with fixed position): the smoke opens every row popup on Results, Messages and Plan and audits it, and the panel
tests check where each popup sits (`openRowPopups`).

Render errors (M17, `src/app/ErrorBoundary.tsx`): every Dockview panel (query, notebook and tool tabs, and their
headers), every bottom-panel view and the Explorer has its own error boundary — "This panel failed" with **Reload panel**
(mounts it again) and **Copy details** in place of the panel, the rest of the shell untouched; the root boundary shows
"Querithm hit an error" with **Reload window**. A bottom-panel run view (Results, Messages, Plan) that failed tries again
by itself on another run (W7: its boundary resets when the shown run changes; a working view is never remounted by it).
Each caught error goes to the sidecar log (`system.reportError`, at most 20 per 10 minutes, a rolling window, capped and
scrubbed by the sidecar): where, the error's type and message with quoted text masked (a value or
SQL a parser echoed), stack frames and React's component stack.

Webview performance rules: no React re-render on grid scroll or editor keystroke; Arrow batches
appended to a ref with the grid told `rows += n`; `meta.updated` patches the snapshot map in
place; all IPC through `src/ipc/client.ts`, which logs any call over 100 ms in dev builds; Monaco
ships the SQL languages only.

Result streams (2026-11-02, M6): `src/ipc/frames.ts` keeps every poll loop by execution id until its end frame. Disposing
a run (a tab closing mid-stream, a run pushed out of the tab's last three — Explain included —, a notebook cell cleared
or deleted) stops its loop and discards its frames in the shell (§5). An engine restart stops every loop without
discarding (the shell's buffers went with the engine) and marks each run still going failed with "its outcome is
unknown: a statement that was running may or may not have completed" (the banner names the tabs); the rows that arrived
stay, marked interrupted. A loop still open 30 s after its run's `query.done` lost its end frame and is stopped too; the
loop starts before notifications that arrived ahead of the view are applied, so an early `query.done` arms that timer as
well (W3). A data compare's run is a view under its tool tab's id (W6): a restart marks it failed like any run, the
banner names the compare, and the tab says it was interrupted (Compare again) instead of "Reading…".

## 13. Security rules

Build-time rules that must not be relaxed; each has a test in `Querithm.Tests/Security`.

1. **Timed and indefinite secrets live only in the OS keyring; connection/app-session passwords stay in memory** (service `querithm`, account `<profileId>`, timed credentials under `<profileId>:timed`; an SSH password or key passphrase under `<profileId>:ssh`). SQLite,
   logs, crash dumps, autosaved scripts and exported settings never contain a password, token or
   full connection string. A log scrubber replaces `Password=...`, `pwd=...`,
   `Authorization: ...` and any value equal to a known secret with `***`. Deleting a profile deletes every one of its
   keyring entries in the shell, after the sidecar closed its sessions (L2). A secret typed in SQL (`ALTER LOGIN … PASSWORD`,
   `IDENTIFIED BY`, `CREATE SECRET … SECRET`, a credential's `SECRET`, …) is masked as `<redacted>` on the engine's tokens
   before query history or the monitor's text store keep the text (`IDialectService.RedactSecrets`, L4) — not a literal in
   any engine (`<` cannot begin a value), so a statement re-run from history fails with a syntax error instead of setting
   the password to the mask (8a review: `'***'` was a literal; `RedactedTextFileEngineTests` / `RedactedTextIntegrationTests`
   run the masked text on every engine); history rows an older build kept are re-masked by the v5 step (§8), while the
   pre-upgrade copy `store.db.v<N>.bak` keeps them as they were; scripts the user saves are left as typed. On Unix the data folder is 0700 and `store.db` (with -wal / -shm) and the logs are 0600, narrowed at
   start when an older build or the umask left them wider. Npgsql data sources are keyed by profile and connection string
   without the password, which a password provider sends at each open, and are dropped with the profile's pools — each
   disposed only once the last connection made from it has closed (L19, 8a review).
2. **TLS on by default.** SQL Server: `Encrypt=Strict` where supported (2022+), else
   `Encrypt=Mandatory`; `TrustServerCertificate=false` unless toggled per profile with a warning.
   PostgreSQL: `SSL Mode=Require` default, `VerifyFull` offered with a CA path. MySQL / MariaDB (Phase D2): `SslMode=VerifyFull`
   by default (MariaDB 11.4+ verifies its auto-generated certificate with the password); `Required`, which encrypts without
   checking the certificate, is an explicit per-profile downgrade. A pasted connection string changes the TLS mode only
   when it names the keyword (any spelling the driver accepts: `Encrypt` / `TrustServerCertificate`, `SSL Mode` /
   `sslmode=` in a URI, `SslMode` / `SSL Mode`); an absent one keeps the profile's mode (M3: MySqlConnector's builder
   reads an absent `SslMode` as Preferred, which silently turned VerifyFull into Prefer). A named setting weaker than the
   default (`Encrypt=False`, `TrustServerCertificate=True`, `SSL Mode=Disable/Allow/Prefer`, `SslMode=None/Preferred/
   Required/VerifyCA`) is applied — the user asked for it — and the parse result's `notes` say what it gives up.
3. **Auth kinds in v1:** SQL login, Windows integrated (Windows only), Kerberos, Microsoft Entra
   ID (interactive, device code, access token), PostgreSQL password/SCRAM. Entra interactive uses
   the system browser. `none` (Phase D0a) is reserved for file and memory databases: never a fallback (`sql` stays the
   default), refused by the server engines, and no vault entry is read for it. SQLite (Phase D1) takes `none` only:
   no secret, no TLS (a local file), no SSH.
4. **Sidecar socket is authenticated.** First frame carries the launch token; the socket path is
   user-private (0600 in a 0700 folder of its own / current-user pipe), and the shell sends the token only to the engine it
   spawned (peer uid and pid / the pipe server's pid, §4, L3). The sidecar binds no TCP port, with one exception (§16):
   an SSH tunnel's forward on `127.0.0.1:<ephemeral>`, which drops every connection whose client socket
   does not belong to the sidecar process (Linux `/proc`, Windows TCP table; elsewhere a per-open connect window).
5. **Webview CSP:** `default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; connect-src ipc:
   http://ipc.localhost; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;
   worker-src 'self' blob:; form-action 'none'; base-uri 'none'; object-src 'none'; frame-src 'none'` (the dev CSP adds
   only the dev server). No remote scripts, no CDNs; Monaco is bundled. Content the user did not write (notebook
   markdown, `.ipynb` cells, release notes) never becomes live HTML: markdown escapes every text token (M4), and the
   last four directives keep an injected form, base or frame inert even if a renderer slips.
6. **Tauri capabilities** are the minimum set: `dialog`, `fs` scoped to user-picked paths and the files the webview
   itself keeps under `$DATA/querithm` — write/read `scripts/*` (tab recovery copies, `workspace.json`), remove and rename
   `scripts/*` only (rename: the atomic write, M23), list `scripts` (the recovery offer), write `bench/*` (the app
   bench's results), read `exports/*` (the sidecar's monitor exports), mkdir `scripts` and `bench`; `write-file`
   (binary) has no scope of its own, only the path the save dialog returned. No webview scope reaches the sidecar's
   private state — `store.db*` (its `.v<N>.bak` and `.corrupt-*` copies included), `known_hosts`, `logs/`, `duckdb/`,
   the shell's `grants.json` and `instance.lock` — and the single-file sidecar's native libraries are extracted under the
   shell's local app-data folder (`<app local data dir>/native/<build>`, H7), never under `$DATA`; `SecurityTests` /
   `WebviewBoundarySecurityTests` parse `default.json` for this. Beyond `core:default` the webview holds only
   `core:webview:allow-set-webview-zoom` (its own page zoom, the Interface scale setting, L26). No `http` plugin. The updater runs in Rust: the webview reaches it only through the app
   commands `update_check` / `update_download` / `update_install` and holds no `updater:*` permission.
   **Database files (Phase D0c, owner decision 2026-10-31):** the sidecar writes a user file only through a ReadWrite
   database grant given in a dialog Rust shows, limited to that database and its engine-managed journal/WAL/SHM files.
   A database file is picked with the shell's own dialog (`db_pick_file`), which registers a grant — canonical path,
   file identity (device + inode / volume + file index), access mode, profile, consent — in
   `<app config dir>/grants.json`, a folder no webview fs scope reaches; the webview holds only the grant id and the file
   name. Grants are read-only by default; "Allow writes to this file" is a dialog the shell shows itself
   (`db_grant_write`), so the answer never passes through the webview. A grant belongs to the profile that saved it
   (duplicating a profile never carries it), is deleted with the profile or by `grant.revoke`, and is never exported.
   The webview gets no fs scope on database files. Every other kind of file keeps the rule above: picked files are
   read-only and the sidecar's outputs go under `<data dir>`.
7. **Destructive and production guards** are enforced in the sidecar, not only in the UI:
   `query.execute`, `plan.get` and `debug.attach` carry `confirmedDestructive: true` (and `confirmedProd`) only after
   the user confirmed; the sidecar re-classifies with one guard for all three and refuses if the flag is missing on a
   Prod profile — Prod as the profile is stored now (or as the tab opened it, whichever is Prod). Dynamic SQL and MERGE
   deletes count as destructive (§6).
8. **Timeouts:** connect 15 s default, command unlimited by default; both per profile. Interactive RPCs that wait on a
   server have a deadline instead (60 s; schema compare, find usages, rename preview 30 min; COMMIT / ROLLBACK none — 30 min
   and the tab's close, since a deadline-cut COMMIT has an unknown outcome, §5), and cleanup after a cancel a 60 s bound
   (`Cleanup.Limit`, §7).
9. **Resource cleanup:** an Execution's and a Session's resources are owned by their services, which are
   `IAsyncDisposable` (the types themselves are not; §16 "Execution and Session lifetimes"): every run ends in
   `ExecutionService`'s `finally` — its connection back to the pool, pinned or discarded, the session lock released,
   `query.done` sent — and closing it, its tab or the watchdog cancels it through the one cancel path (§7 step 4);
   `SessionManager.CloseAsync` cancels the session's work (`Session.Closing`), waits (bounded) for its lock and discards
   its pinned or owned connection; both services cancel and close what they own at shutdown. A watchdog cancels a run
   whose rows were not consumed for 10 minutes and says so in that tab, drops a finished run's cell buffer after 10 idle
   minutes and forgets the run after 12 hours.
10. **Updates are signed**; the release pipeline fails on high-severity `cargo audit` /
    `dotnet list package --vulnerable` findings. Every update package is signed with the minisign key
    (`TAURI_SIGNING_PRIVATE_KEY`) and bound to its version (`--app-version`); the app trusts only the public key in
    `tauri.conf.json` and sets `requireSignedVersion`, so a tampered manifest cannot downgrade it.
    `scripts/release-manifest.mjs` verifies every signature, key id and version before `latest.json` is published.
    The manifest URL is HTTPS-only; release notes are untrusted and shown as plain text.
    The key reaches only the release workflow's `sign` job, which runs nothing but the Tauri CLI from the lockfile
    (install scripts off); the build job, which runs vite, tsc and every crate build script, builds without updater
    artifacts and never sees it. A tag releases only a commit on `main`, after the audits above, the trimmed sidecar's
    `--self-test` and `cargo test`; only the highest version becomes the releases repo's "latest".
11. **Third-party licenses** inventoried in `THIRD-PARTY-NOTICES.md` at build; SMO's
    redistribution terms confirmed before the first public release.
12. **DuckDB file and configuration policy (Phase D3).** Every DuckDB instance is configured before any user SQL, in the
    order DuckDB requires: extension autoinstall / autoload / community / unsigned off, persistent secrets off, extension
    and secret directories pointed at a folder under `<data dir>` that is never created, temp directory and a spill cap
    under `<data dir>/duckdb/tmp/<pid>`, memory capped at a quarter of physical memory, empty `allowed_directories` /
    `allowed_paths`, `enable_external_access=false`, `lock_configuration=true` — then verified, or the connection is
    refused. Files open by grant identity: one Data Source string and one access mode per file while any connection is
    open; a second grant in another mode, or another file whose path differs only in case (DuckDB.NET's instance cache
    ignores case), is refused. An opened file whose views or macros call a refused function is not opened. The classifier
    also refuses what the locked engine would still allow or what no grant covers: INSTALL / LOAD, COPY (TO and FROM),
    EXPORT / IMPORT DATABASE, ATTACH / DETACH, CREATE / DROP SECRET, SET / RESET of anything but schema / search_path,
    every PRAGMA but the read-only ones, and calls to logging, profiling, parser, raw-pointer and SQL-text functions
    (`enable_logging` with a path aborts the process; `PRAGMA enable_profiling` prints query text to stderr; the
    progress bar prints to stdout). **stdout is JSON-RPC only:** the Host moves JSON-RPC to a private duplicate of
    descriptor 1 and points descriptor 1 at stderr, so a native write cannot corrupt a message (`StdoutGuardTests`).

## 14. Performance gates and benchmark procedure

Fixture: SQL Server 2022 and PostgreSQL 16 in Docker; generated schema of 600 tables, 9,000
columns, 400 procedures, 150 views, 300 FKs, 200 triggers (`scripts/bench/gen-schema`); a 12,000-line stored
procedure with 40 CTEs and 6 MERGE statements; a 10,000-line PL/pgSQL function (RETURNS TABLE, DECLARE, FOR … IN
record loops, a temp table); a 1,000,000-row, 40-column result query. 5 runs, median and p95.

| # | Metric | How measured | Initial target |
|---|---|---|---|
| 1 | Cold start to interactive editor | process launch → first Monaco keystroke accepted | ≤ 1.5 s median |
| 2 | Idle RSS after start, no connections | Tauri + webview + sidecar RSS | ≤ 180 MB |
| 3 | Typing latency in the 12k-line proc | onDidChangeModelContent → next paint | p95 ≤ 16 ms |
| 4 | Tier-1 completion latency | keystroke → popup items ready | p95 ≤ 5 ms |
| 5 | Tier-2 completion latency | `lang.complete` roundtrip, 12k-line proc | p95 ≤ 150 ms |
| 6 | Full parse of 12k-line proc | `lang.diagnostics` | ≤ 100 ms |
| 7 | Tab switch between connected sessions | click → editor + results painted | p95 ≤ 50 ms |
| 7b | Tab switch with 30 tabs open | `pnpm bench` metric `7b`: the first ↔ the last tab of the strip, 50 samples, click → editor + results painted | p95 ≤ 50 ms |
| 8 | DB switch, SQL Server, existing connection | `session.changeDatabase` → header updated | ≤ 1 roundtrip + 20 ms |
| 9 | Metadata snapshot load from SQLite | `meta.snapshot` for the fixture db | ≤ 80 ms |
| 10 | Incremental refresh with 10 changed objects | `meta.refresh` → `meta.updated` | ≤ 2 s |
| 11 | 1M-row result: time to first batch painted | execute → first rows visible | ≤ 300 ms + server time |
| 11b | Execute → first batch, cold pool connection | `pnpm bench` engine metric `11cold`: `SELECT 1` as the first execute of a session on a new profile (TCP, TLS, login and the identity read every physical open does, §6 rule 7), 15 per engine, median, slower engine | ≤ 300 ms |
| 12 | 1M-row result: scroll frame time | Glide scroll p95 | ≤ 16 ms |
| 13 | 1M-row result: peak webview memory delta | before → after full fetch | ≤ 600 MB |
| 14 | UI frame drops with 5 concurrent 60 s queries | typing + scrolling during execution | 0 frames > 32 ms |
| 15 | Cancel latency | `query.cancel` → `query.done(cancelled)` | ≤ 500 ms |
| 15b | Cancel with a failing dependency | `CancelRecoveryTests` (fake server that ignores the cancel, data plane with no reader or not draining, run queued or still connecting, grid / import write) and `CancelEscalationIntegrationTests` (`QUERITHM_IT=1`: SQL Server close measured on the fixture; PostgreSQL / MySQL never closed) | `query.done` ≤ 10 s after Cancel whatever the dependency does (≤ 5 s + close on SQL Server; SqlClient's Close 24 ms, parked call ended 36 ms); otherwise a line in Messages at 10 s saying what the run waits for, a Restart engine banner at 15 s, and an error at 70 s if it still has not ended |
| 16 | Sidecar orphan check | kill the app with SIGKILL / taskkill | 0 sidecar processes after 5 s |
| 17 | Installer size | per platform; the Linux AppImage as `17.appimage` | ≤ 120 MB; AppImage ≤ 150 MB (§16) |
| 18 | ER diagram 2-hop neighbourhood on the fixture | subgraph + dagre layout, main thread | ≤ 30 ms |
| 19 | ER diagram whole schema, compact | subgraph + layout in the web worker | ≤ 1 s |
| 20 | Grid edits: DML preview for 100 staged cells | `data.previewPatch` roundtrip | ≤ 15 ms |
| 21 | Schema compare of the fixture database with itself (600 tables, 400 procedures) | `schemaCompare.run` roundtrip, both sides read, slower of SQL Server / PostgreSQL | ≤ 2 s |
| 22 | Data compare 100k × 10 against a copy with 1,000 changed rows | `dataCompare.start` → final `dataCompare.progress`, slower engine, server time included; sidecar RSS delta | ≤ 1.5 s; ≤ 64 MB |
| 24 | Find usages of a column every fixture module mentions (~550 cached modules, the 12k-line procedure included) + 5 open tabs | `refactor.findUsages` roundtrip for a target not searched before, definitions cached | ≤ 120 ms |
| 23 | Import 100k × 10 CSV into a new table | `import.run` → `import.progress` done, slower of SQL Server / PostgreSQL | ≤ 2 s |
| 26 | Update check at startup | delay of the first `update_check` after boot, asserted with fake timers in `src/store/__tests__/updates.test.ts` (bench runs disable checks, so metric 1 cannot observe them) | ≥ 10 s; no network request during cold start |
| 25 | Warm cursor-pair lookup on the 12k-line procedure | `node scripts/bench/editor-review.mjs <output-directory>`; 200 samples after one cold call, cold cost reported separately | p95 ≤ 5 ms |
| 28 | Activity snapshot, 500 sessions | `QUERITHM_BENCH_MONITOR=1 dotnet test --filter MonitorBench` (server query, sidecar processing, CPU, lag, payload; idle, and active churn generated inside the server containers — `pgbench -C` / `sqlcmd` loops — so the CPU figure is the sidecar's own) + `pnpm bench` metric `28ui` / `28first` (webview apply) | server query ≤ 100 ms, sidecar ≤ 10 ms, UI update ≤ 16 ms, sidecar CPU ≤ 2 % at 2 s, `LagMs` p95 ≤ 200 ms; payload ≤ 512 KB per tick without new texts, first snapshot ≤ 2.5 MB, first apply ≤ 50 ms |
| 29 | Monitor history, 10 min at 2 s with 500 sessions | `MonitorBench`: 300 synthetic ticks of 500 sessions with changing text, heap measured after a full GC | memory ≤ 50 MB, scrub to any tick ≤ 50 ms |
| 30 | Deadlock history, 200 events (SQL Server) | `MonitorBench` seeds 200 real deadlocks one at a time (simultaneous ones share one report); `pnpm bench` metrics `30list` / `30graph` in the webview | server read ≤ 2 s, parse ≤ 200 ms total (≤ 5 ms median per event); first paint of the list ≤ 100 ms after the events arrive; graph of a 10-process deadlock ≤ 50 ms |
| 31 | Top queries refresh, 50,000-entry plan cache (SQL Server) / full `pg_stat_statements` (PG) | `MonitorBench` fills the cache, then times incremental and full reads | incremental ≤ 300 ms SQL Server, ≤ 100 ms PG; full ≤ 1.5 s SQL Server; sidecar delta + top-100 ≤ 20 ms |
| 32 | Scheduler overhead | `MonitorBench`, every Phase B pane at default intervals, idle server | ≤ 1 % sidecar CPU |
| 11cold.sqlite, 15.sqlite, 9.sqlite, 10.sqlite, 5.sqlite, 20.sqlite, 23.sqlite | Phase D1 per-engine ids (SQLite) | `pnpm bench -- --sqlite` (or any engine run): temp-dir files with the grant injected by the harness; 11cold = a new tab's first execute (open, hardening, `SELECT 1`), 15 samples; 15 = interrupt of a recursive CTE; 9 / 10 on 600 tables × 15 columns, 150 views, 200 triggers | as #11b, #15, #9, #10 — first run 2026-10-31 (`2026-10-31-05-28-15-linux.json`): 1.9 ms, 1.1 ms, 47.2 ms, 23.3 ms. 5 / 20 / 23 (2026-10-31, `2026-10-31-15-19-41-linux.json`): 23.6 ms p95, 3.4 ms, 563 ms — #5 is tier 2 after `a.` at 60 places in a 10,000-line script of joins over the fixture (as #5), #20 as #20 on a 100-row keyed table, #23 the #23 CSV, median of 3; before the profiled fixes they read 179 ms (statements re-split per call) and 7,792 ms (multi-row INSERTs, `2026-10-31-15-12-53-linux.json`) |
| 11cold.duckdb, 9.duckdb, 10.duckdb, 15.duckdb, 23.duckdb, 5.duckdb, 20.duckdb | Phase D3 per-engine ids (DuckDB) | `pnpm bench -- --duckdb` (or any engine run): temp-dir files with the grant injected by the harness, each an empty file that becomes a new database; 11cold = a new tab's first execute (create, open, policy, `SELECT 1`), 15 samples; 9 / 10 on 600 tables × 15 columns, 150 views (10 = 10 views replaced); 15 = interrupt of `count(*)` over `range(1e11)`; 23 = the #23 CSV into a new table through the appender, median of 3 | as #11b, #9, #10, #15, #23 — 2026-10-31 (`2026-10-31-13-12-21-linux.json`): 81.3 ms, 54.5 ms, 71.2 ms, 1.8 ms, 788 ms (batched INSERTs: 24,090 ms, `2026-10-31-13-07-01-linux.json`). 5 / 20 as the SQLite ids (`2026-10-31-15-33-37-linux.json`): 57.7 ms p95 (264 ms before the USE / SET schema scan was kept per document), 4.5 ms |
| 11cold.mysql, 8.mysql, 15.mysql, 9.mysql, 10.mysql, 5.mysql, 20.mysql, 21.mysql, 22.mysql, 23.mysql, 24.mysql | Phase D2 per-engine ids (MySQL 8.4) | `pnpm bench -- --mysql` (or any engine run), the fixture's `mysql84` with TLS Required: 11cold = `SELECT 1` as a new profile's first execute (TCP, TLS, login, identity read), 15 samples; 8 = COM_INIT_DB switch there and back; 15 = KILL QUERY of `SELECT SLEEP(30)` after 200 ms; 9 / 10 on 600 tables × 15 columns, 150 views, 200 triggers (10 = 10 views replaced) | as #11b, #8, #15, #9, #10 — first run 2026-10-31 (`2026-10-31-08-55-37-linux.json`): 11.6 ms, 2.4 ms, 14.3 ms, 33.2 ms, 119 ms. 5 / 20 as the SQLite ids, 21 = the fixture database compared with itself on two sessions, 22 = 100k × 10 against a copy with 1,000 changed rows (sidecar RSS delta as #22), 23 = the #23 CSV, median of 3, 24 = find usages of `id` (every fixture view and trigger names it) + 5 tabs (`2026-10-31-15-50-45-linux.json`): 53.7 ms p95, 3.4 ms, 324 ms, 341 ms, **3,940 ms (over the 2 s target: the measured baseline the owner accepted, §16 "MySQL / MariaDB provider")**, 14.3 ms. 28 / 31 / 32 on MySQL (`MonitorBench`, the mysql84 service runs with `--max-connections=700`; `2026-10-31-16-35-38-monitor-linux.json`): 28 idle (503 sessions) server query 22.2 ms median / 25.5 p95, sidecar 0.20 / 0.23 ms, tick ≈ 141 KB, first snapshot 141 KB, lag p95 1 ms, CPU 0.27 %; active (507 sessions, churn of ≈ 960 connections/s inside the container plus a blocked UPDATE) 29.4 / 32.8 ms, 0.19 / 0.25 ms, CPU 0.35 %; 31 with the digest table full (9,999 of 10,000) — MySQL reads it whole on every refresh, so only full reads: 54.3 ms median / 89.1 p95, delta + top-100 6.8 / 17.2 ms (PostgreSQL's targets, §14 names none for MySQL); 32 = the idle 28 case, 0.27 % sidecar CPU. 29 is synthetic (no provider) and covers MySQL rows as they are; 30 does not apply (MySQL's deadlock history is degraded with its reason) |
| 27 | Tier-2 completion latency, PostgreSQL, 10k-line PL/pgSQL function | `lang.complete` roundtrip at 60 positions in the function (record fields, temp-table and CTE columns, variables), first 5 dropped | p95 ≤ 150 ms |
| 33 | Object Explorer on a synthetic database of 25,000 objects (20,000 tables in one schema, 2,000 views, 3,000 procedures) | `pnpm bench` metrics `33expand` / `33scroll` / `33patch` / `33search` / `33group` (`window.__bench.explorerTree`, the mounted tree in the built webview): expand the 20,000-table folder → next frame; 240 scroll steps of 37 px, one per frame (as #12); a metadata patch to another database while the folder is open; a search keystroke (pure match + group + sort, warm; the cold first call reported); Group by schema → next frame | expand ≤ 50 ms; scroll p95 ≤ refresh + 1.5 ms with ≤ 1 tree render per 16 rows; patch ≤ 33 ms; search p95 ≤ 16 ms; group ≤ 50 ms |
| 35 | Results panel chrome while rows stream | `useThrottledVersion.test.tsx` (1,000 batch notifications over ~100 ms) and `BottomPanel.test.tsx` (100 notifications): renders of the ledger / badges / footer, Vitest with fake timers | ≤ 1 chrome render per 250 ms of streaming (≤ 3 for the 1,000-batch burst), the last state always shown |
| 36 | Server-message flood: one run of 100,000 PRINT (SQL Server) / RAISE NOTICE (PostgreSQL) messages (M13) | `pnpm bench` engine metrics `36` (execute → `query.done`, median of 3, slower engine, server time included) and `36notify` (notifications the sidecar sent for one run, the most of either engine); UI metric `36frames` (`window.__bench.messageFlood`: the run in the fixture tab with Messages open, frames longer than 32 ms until it ends) | `36` ≤ 5 s; `36notify` ≤ 300 (one per message before M13); `36frames` 0 (2026-11-03: 19 of 195 frames over 32 ms, worst 50 ms, while each `query.messages` batch re-listed the whole log and formatted the time of every shown line again — 13 ms of main thread per batch in a Chrome trace, plus 22–30 ms GC callbacks freeing the per-call ICU formatters; the log is now extended per batch and lines already shown are not rendered again — 2.3 ms per batch, 0 of 202 frames, worst 16.8 ms) |
| 34 | Chart a 1M-row result: line with two series | `pnpm bench` metrics `34` / `34frames` (`window.__bench.chartToggle`): a 1M × 40 synthetic result fully in a tab, then Grid → Chart as `CreatedAt5` × `Qty1`, `Amount3`; one warm-up toggle, 3 measured: toggle → the computed chart painted (median), and every frame interval meanwhile | first paint ≤ 500 ms; 0 frames > 32 ms (aggregation and down-sampling in the worker) |

**Last verified per OS** (`bench/results/`; engine metrics drive the sidecar, UI metrics run the built
webview in headless Chrome through the dev bridge):

- **Linux** — 2026-10-24 (`2026-10-24-15-34-41-linux.json`, Ryzen 7 4700U): all 23 recorded metrics (1–24) within
  target; WebKitGTK gate resolved (§16). Metric 27 has no Linux run yet, and the
  WebKitGTK gate (metrics 3, 4, 12 with `WEBKIT_DISABLE_COMPOSITING_MODE` set and unset) was not re-run
  in Phase A. 2026-10-29 (`2026-10-29-20-01-20-linux.json`, headless Chrome, after the Explorer redesign): every
  recorded metric within target, none regressed; metric 27 75 ms p95; metric 33 first run — expand 25.4 ms, scroll
  p95 16.7 ms at a 16.7 ms refresh (0 long frames, 23 tree renders in 240 steps), patch 18.1 ms, search p95 14.4 ms
  (one sample; the other eleven 1.7–3.9 ms, cold call 7.2 ms), group by schema 16.7 ms (in the same dev-build profile the
  grouping took 1,116 ms before the redesign and 16.5 ms after). The WebKitGTK gate was not re-run.
  2026-10-31 (`2026-10-31-03-47-09-linux.json`, headless Chrome, Phase C): every metric within target; metric 34 first
  paint 465 ms (median of 3), 0 frames > 32 ms. The run before it (`2026-10-31-03-44-20-linux.json`, a video playing
  and a compiler running on the machine) read #4 5.3 ms and #9 48.5 ms; the re-run read 4.6 ms and 34.4 ms. #24 read
  92.7 ms (+27 % over a low 73.2 ms in the run before; 85–95 ms is its usual range here, Phase B notes) — find usages
  is untouched by Phase C. The WebKitGTK gate was not re-run.
  2026-10-31 (`2026-10-31-16-17-53-linux.json`, a quiet machine after a reboot, Phase D fourth pass): every metric within
  target except 23.mysql (§16 measured baseline); #4 4.2 ms, #34 470.8 ms and #14 0 frames, which missed under load in the
  second pass, pass again. The flagged #11, #24 and #9.sqlite regressions and then #9 read 183.6 ms, 85.3 ms, 52.1 ms and
  57.5 ms on re-runs of the same code (`2026-10-31-16-23-34`, `-16-23-49`, `-16-25-14`), within each metric's range here.
- **Windows** — 2026-10-27 (`2026-10-27-05-45-28-windows.json`, i5-1135G7, Docker Desktop): metrics 3,
  5, 6, 8, 9 (19 ms), 10 (25 ms), 12, 13, 14, 15, 18, 19, 20, 21, 24 and 27 (22 ms p95) within target;
  **over target: 1 (1,964 ms), 4 (12.3 ms), 7 (84.7 ms), 11 (753 ms), 22 (2,518 ms), 23 (2,903 ms)** — open.
  The paths behind them were not changed in Phase A, and work that touches no server or UI framework is
  about 2× slower on this machine than on the Linux one (the in-process 1M × 40 synthetic stream 6.2 s
  vs 3.3 s; metric 4 times the pure tier-1 `complete()` function), so they still need a profile on
  Windows before any change. Native WebView2 (the shipped app, `--app`) was not measured.

Additional editor-review probe: `node scripts/bench/editor-review.mjs <output-directory>` writes
CPU-profile evidence and cold/warm cursor-pair timings for the same `bigProc('mssql')` fixture.
Warm cursor-pair lookup target: p95 ≤ 5 ms over 200 samples after one cold call; report cold
cost separately. Cursor decorations wait 75 ms after the last cursor event; the complete typing
path must still pass metric 3. This Node probe does not replace native-webview measurements.

Harness: `scripts/bench/run.ts` drives the app through a Tauri test hook (`QUERITHM_BENCH=1`
exposes `window.__bench`; `?bench` only in the browser under the dev bridge) and the sidecar through its RPC (`bench.*`
exists only in a sidecar launched with `QUERITHM_BENCH=1`, L10); results go to
`bench/results/<date>-<os>.json` and are compared with the previous run; a regression over 20 %
fails. Linux gate: metrics 3, 4, 12 are run under WebKitGTK with
`WEBKIT_DISABLE_COMPOSITING_MODE` unset and set.

## 15. Build milestones and rules for the coding agent

Rules: (1) read §2, §4, §5, §13 before writing code; (2) no library outside the §2 table without
adding a row; (3) no SQL, parser or driver code in Rust, no window/file-dialog/keyring code in
.NET; (4) rows cross IPC only as Arrow frames; (5) every RPC method gets a C# DTO, regenerate
`contract.ts`, never edit it by hand; (6) query-text features are built on tokens or the ScriptDom
AST, never regex over raw text; (7) server-capability features check at runtime and degrade with
a visible reason; (8) tests first for §13; (9) keep startup lazy; (10) when stuck between two
designs, choose the one that keeps `src-tauri` smaller.

1. **Scaffold and IPC.** Repo layout, Tauri window with Dockview, one Monaco tab, stub sidecar
   answering `system.ping`, a hard-coded 1M-row Arrow result streamed into Glide, sidecar
   lifecycle with orphan prevention, `pnpm dev`/`pnpm build`, bench metrics 1, 2, 11, 12, 13, 16.
2. **Profiles and sessions.** Profile CRUD, keyring secrets, connection test, groups and
   environments; Session/Pool/Execution for SQL Server; execute, cancel, tx, changeDatabase,
   messages, multiple result sets, GO splitting; grid copy/sort/filter/CSV/row-limit footer;
   status bar and tab header. Metrics 7, 8, 14, 15.
3. **Metadata and tier-1 completion.** SQLite schema and MetadataRefresher; explorer with lazy
   levels and search; tier-1 scanner and completion tested against 60 snippets; hover and
   go-to-definition. Metrics 4, 9, 10.
4. **Tier-2 intelligence and procedures.** ScriptDom complete/diagnostics/signature/format,
   folding, destructive classifier, temp-table and CTE resolution; procedure
   open/apply/compare/execute and dependencies behind `IObjectScripter`; script autosave/restore.
   Metrics 3, 5, 6.
5. **Plans, statistics and Trace.** SQL Server estimated/actual plans in React Flow; STATISTICS
   IO/TIME table; XEvents Trace with timeline and line highlighting; permission checks.
6. **PostgreSQL provider.** Same interfaces: pooled-per-database sessions, cancel, notices,
   metadata, EXPLAIN plans, tier-1 pg dialect, pldebugger adapter with the engine-neutral debug UI.
7. **Hardening and release.** Security tests, log scrubber, updater, signed installers, CI bench,
   THIRD-PARTY-NOTICES, Linux WebKitGTK gate resolved.
8. **Depth.** History with FTS search, parameterized run dialog, Excel export, FK-aware join
   snippets, PL/pgSQL tier 2, more providers (MySQL first).

**Definition of done:** milestones 1–7 complete, all 17 metrics within target on Windows and
Linux, and a real 10,000-line production stored procedure edited, applied and traced end to end
without a UI stall.

## 16. Deviations from the original spec

| Item | Original | Here | Reason |
|---|---|---|---|
| Name | SqlForge / `sqlforge` | Querithm / `querithm` | Product name chosen by the owner |
| Sidecar spawn | tauri-plugin-shell | `std::process` from Rust (no shell plugin, no `shell:*` capability) | Needs the raw child handle for the Job Object and main-thread spawn for PDEATHSIG; smaller capability surface (§15 rule 10) |
| Package pins | SqlClient 5.2+, ScriptDom 161+, SMO 170+ | 6.1.x, 180.x, 181.x | Current releases satisfying the original minimums |
| Data-plane payload | Arrow stream | each `0x01`/`0x02` payload is a self-contained Arrow IPC stream | Frames decode independently; schema bytes are negligible |
| Explorer source | SQLite per expand | the webview's in-memory `SchemaSnapshot` (already loaded for tier 1) per expand; `meta.objectDetails` for keys/indexes | Same data, no extra roundtrip |
| `query.cell` | server-side cursor | full values of truncated cells kept in a bounded per-execution side buffer (64 MB) | Equivalent behaviour without holding a server cursor open |
| `nvarchar(max)` encoding | LargeUtf8 | Utf8 (still truncated at 1 MB with `<col>__truncated`) | Apache.Arrow C# 18 had no LargeUtf8 builder (23 has one; Utf8 is kept): batches are ≤ 4 MB so 32-bit offsets suffice, and the wire format stays what the webview reader decodes today |
| PostgreSQL execution | one Npgsql command per script | one statement per batch, stop at the first error | Npgsql reports error positions per statement; later statements in a failed transaction cannot run anyway |
| PostgreSQL tx state | `NpgsqlConnection` status | `SAVEPOINT` probe after each execution | Npgsql exposes no public transaction status; the probe is exact (25P01 outside a block) |
| Diagnostics on large texts | one ScriptDom parse | statement chunks parsed in parallel and cached by text (`ChunkedDiagnostics`) | A full ScriptDom parse of the 12k-line fixture costs 200–400 ms; chunks meet metric 6 (≈45 ms, ≈10 ms after an edit) |
| Tier-2 completion | full-document parse | statement-at-cursor parse with repairs + cached declarations | Same reason; metric 5 ≈ 11–35 ms p95 |
| Frame-time metrics (3, 7, 12) | "≤ 16 ms" | measured as work inside the frame (3, 7) and "no frame longer than one refresh" (12) | rAF intervals cannot go below one refresh (16.7 ms at 60 Hz) |
| Metric 2 (idle RSS ≤ 180 MB) | target | **measured baseline, Linux/WebKitGTK release build: ≈ 590 MB RSS / ≈ 380 MB PSS** (web process ≈ 310 MB, UI process ≈ 180 MB, sidecar ≈ 70 MB) | WebKitGTK's own processes exceed the target before the app loads; §14 replaces initial targets with measured baselines |
| Linux WebKitGTK gate (§14) | open | **resolved: keep compositing on, full decorations** — typing 7 ms, tier-1 1 ms, scroll within one refresh under WebKitGTK 2.52 | Disabling compositing removes vsync-locked rendering and made typing slower |
| Updater (§13 rule 10) | Tauri updater | `tauri-plugin-updater`, driven from Rust (`src-tauri/src/updater.rs`). The app asks `releases/latest/download/latest.json` of the public releases repo 10 s after start and every 6 h (Settings → About → "Check for updates automatically", on by default; "Check for updates" checks now and reports in place, with the time of the last answer). A newer release shows **Update** right of Settings; one click downloads and verifies the package, saves every tab, stops the sidecar, installs and relaunches. Windows installs passively (NSIS or MSI, whichever is installed); macOS replaces the `.app`; Linux updates AppImage, `.deb` and `.rpm` (the package manager asks for the administrator password). Offline or failed checks are silent; failed downloads/installs keep the current version running and restart the sidecar. Optional release notes: `docs/release-notes/<tag>.md` (plain text in the dialog, and on the release page) | The only network-capable plugin, an exception to offline-first: it never sends user data (a GET of a static manifest and the package), the webview gets no network permission, and it can be turned off |
| Release hosting | — | installers are not published from the code repo (private apart from short public windows); `release.yml` publishes the installers (stable names, `SHA256SUMS.txt`) as a Release of the public `dev-taosif-net/querithm-releases` repo, whose GitHub Pages site is the download page | Private-repo Release assets cannot be downloaded publicly; the cross-repo token (`RELEASES_REPO_TOKEN`) is a fine-grained PAT limited to that one repo |
| Startup warm-up | — | ScriptDom/completion JIT warm-up on the first `session.open` | Keeps startup lazy (§15 rule 9) while removing the ~0.5 s first-completion stall |
| Completion tier label (§9) | status bar labels the IntelliSense tier | no label (`session.capabilities` still reports `tier1+tier2` for both engines) | Removed at the owner's request; the status bar keeps the §12 contents (server, database, TX, timer, rows, sidecar health) |
| Environments | Dev / Staging / Prod | Local / Dev / Test / Staging / Prod (enum values appended; stored as text) | Matches the usual promotion path; only Prod adds guards (§6) |
| Execution watchdog (§13 rule 9) | closes executions idle 10 min | idle executions give back their truncated-cell buffer after 10 min and are closed after 12 h; the small per-result edit record stays | Grid edits must still resolve their table after a coffee break; the memory that matters (cells) is still freed |
| Prod guard for generated writes (§6) | first execution per launch | grid patches (and later imports, syncs) ask every time (`GuardReason.ProdWrite`); deletes also need the destructive confirm | A generated write is not a statement the user typed and re-read |
| Unknown stored values (Phase D0a) | — | a profile whose stored engine, sign-in or location this build cannot read (a newer build wrote it), or whose engine has no provider, is listed with `unsupported` and never opened, tested or overwritten; a snippet of an unknown dialect is not offered (it stays in the table). Enum text is parsed strictly (names only; no numbers or flag lists) | The old fallbacks read such a profile as a SQL Server login and such a snippet as T-SQL |
| Provider features (Phase D0a) | every provider implements every interface member | `IDbProvider.FeatureRefusal(Feature)` declares what a provider does not implement (with the reason); the mandatory members (`Metadata`, `Plans`, `Scripter`, `DataEdit`, `Import`) stay non-null and are guarded by these flags rather than made nullable. `session.capabilities` carries the flags and every RPC refuses with the same reason (`FeatureGuard`) | Nullable members would put a null check at every call site; one flag per feature keeps the refusal reason in one place and lets the UI disable the action with it |
| Transaction control (Phase D0b) | provider SQL strings for `tx.*`, a second set in the edit dialect, ADO.NET transactions in compare reads and loaders, literal BEGIN/ROLLBACK in PostgreSQL plans | one `ITransactionControl` per provider (SQL text for SQL Server and PostgreSQL, whose drivers follow transactions begun in SQL); SQL Server's commit is the strict `COMMIT TRANSACTION` (was `IF @@TRANCOUNT > 0 COMMIT`); the native `SqlBulkCopy` loader keeps its own `SqlTransaction` inside the provider (it cannot join a transaction begun in T-SQL) and is the only driver-level transaction; every pool return outside an execution reads the transaction state and discards a connection left in one (on PostgreSQL the existing exact `SAVEPOINT` probe, which logs an error line on the server outside a transaction) | Three wordings of the same control disagreed (`COMMIT TRANSACTION` vs the guarded form) and helper borrows could pool a connection inside a transaction; the guarded commit reported a transaction the server had already rolled back as committed |
| Database-file grants (Phase D0c) | user-picked files are read-only; the sidecar never writes into the user's folders | a durable grant registry in the shell (`src-tauri/src/grants.rs`, `<app config dir>/grants.json`, std + serde only, file 0600 on Unix) next to the one-time import tokens (`files.rs`, unchanged); writable only after the shell's own consent dialog; the sidecar re-checks the identity around every open (Unix: the runtime's `SystemNative_Stat`, its layout checked against `File.GetUnixFileMode` and the size, failing closed; Windows: `GetFileInformationByHandle`). The import reject log, saved snapshots and monitor exports rows still hold for every other kind of file. The SSH key path (picked in the webview) and the free-text CA path stay raw paths in Phase D | Smallest design that keeps database paths out of the webview: no crate, no new capability, and a registry the webview cannot write because no fs scope reaches the config folder (narrowing the `$DATA/querithm/**` scopes or an HMAC keyed in the vault were the alternatives) |
| Session-owned connections (Phase D0d) | every session borrows from the per-profile pool per execution | file and memory databases keep one connection per tab (`ConnectionLifetime.SessionOwned`) built on the session's pinned connection; helpers serialize on the session's execution lock; session-scoped metadata (in-memory database, temporary objects) lives in an in-memory store, and DDL inside the owner's transaction defers the shared refresh until the transaction ends. A data compare whose both sides are the same session-owned session is refused (both streams would need its one connection). An import refreshes and waits for the session's own view when its target is missing (it waited on the shared snapshot, which never holds an in-memory or temporary table); a table created inside the session's open transaction is read when the transaction ends, so such an import is refused with that reason ("Commit, then import") | A memory database exists on one connection only, and a pooled borrow would open an empty one; uncommitted schema read on the owner's connection must never be published to other tabs |
| Engine branches and identifier identity (Phase D0e) | SQL Server / else-PostgreSQL branches in Core; database names lowercased in pool and cache keys | per-engine facts are provider data (`IDbProvider.Traits`: name, default database/schema/port/sign-in, sign-ins, locations, `NameMatch`); engine-specific rules that remain are exhaustive tables that refuse an engine without rules (`RefactorService.RenameRules`, `MonitorRules`, `plan.parse`, the wait dictionary, `ProfileRules.EngineName` for display); built-in snippets stay tagged by dialect. Identity keys keep exact names — pools (PostgreSQL "Sales" and "sales" are two pools), schema caches, refresh jobs, the database checks of grid edits, compare and object scripting; a SQL Server session adopts the server's spelling of its database after the first switch, so later borrows match exactly. `NameMatch` is only for matching names the user gave (refactor targets, compare column pairs): SQL Server case-insensitive (its default collations), PostgreSQL exact. The store's `obj_name … COLLATE NOCASE` index serves the search box only | A new engine must not silently take SQL Server's or PostgreSQL's behaviour, and lowercased keys merged distinct PostgreSQL databases |
| SQLite provider (Phase D1) | — | shipped hidden (`QUERITHM_DEV_ENGINES=sqlite` or the `dev.engines` setting exposed it) until its feature matrix and release gates passed; visible to everyone since 0.2.0 (owner decision 2026-11-01, after the CI `release-smoke` job passed on win-x64, linux-x64, osx-x64 and osx-arm64). The flag stays in `Program.cs` (`behindFlag`, empty) for the next engine. Every connection is hardened before user SQL (foreign keys on and verified, `trusted_schema` off, defensive mode, extension loading off, `SQLITE_LIMIT_ATTACHED = 0`, `temp_store = MEMORY`, an authorizer denying ATTACH/DETACH, `load_extension` and file-changing pragmas); the classifier refuses the same statements before they run (`Classification.Refusal`, a hard error no confirmation lifts). Files open exactly in the grant's mode (`ReadOnly` / `ReadWrite`, never `ReadWriteCreate`); a read-only open of a WAL database may create the engine's own `-wal` / `-shm` beside the file and never changes the database file. Cancel is `sqlite3_interrupt` (SqliteCommand.Cancel does nothing); an interrupted write inside a transaction rolls it back and the tab is told. The lock-wait budget is fixed at 5 s (`EngineTraits.FixedCommandTimeoutS`): Microsoft.Data.Sqlite uses the command timeout only for SQLITE_BUSY retries and would retry forever at the profile's 0. Every result column is dynamic (D0f). **Dependencies**: from the stored SQL of views and triggers on tokens (identifiers in a table position, or elsewhere unless a call, a column after '.', or an alias after AS; names case-insensitive), and a trigger uses its table. **Grid editing** (second slice): values are written as text parameters and the column's affinity converts them, as for any SQLite INSERT/UPDATE; there is no row version, so a lost update is caught by comparing the original values of the changed columns with `IS` and reported as a conflict with nothing written; a key must be proven — a declared PRIMARY KEY whose columns may hold NULL (SQLite allows that outside rowid aliases and WITHOUT ROWID tables) is not reported as primary, so such a result is read-only with the reason; BLOB columns are locked (shown as hex); integer and text keys only (`ExactKey`); inserts read the generated key with `RETURNING`; result columns map to their table through `BaseTableName` / `BaseColumnName`, and a name that exists in both `main` and `temp` gives no origin (read-only). **Import**: `SqliteBulkLoader`, one prepared single-row INSERT reused for every row, inside import.run's transaction rules (Own: one transaction; inside the user's: under the service's savepoint; batches: a transaction committed every N rows) — the generic multi-row INSERT bound 2,000 named parameters per statement and SQLite looks each name up linearly, so 100k × 10 took 7.8 s (now 0.56 s, #23.sqlite); new tables typed by affinity (INTEGER / REAL / NUMERIC / TEXT, decimals over 15 digits as TEXT); a blank cell is NULL except in a text column; `id INTEGER PRIMARY KEY` is the rowid, an identity column, so the file's ids are loaded only with "Keep identity values", as on SQL Server; a column type is never altered in place (SQLite cannot), so no widening is offered. **Completion**: tier 2 and signature help are PostgreSQL's token-scoped engine under SQLite's rules (`SqliteCompletion`: SQLite tokens, trigger-aware statements, "x" / [x] / `x` quoting, keywords quoted on insert, default schema main, no named arguments); SQLite stores no routines, so signature help offers the bundled library's built-in functions, each checked against `pragma_function_list` / `pragma_module_list` (json_each / json_tree are in neither and are run instead). Schema/data compare, find usages and the monitor degrade with the provider's reason, which both `session.capabilities` and the refused RPC carry | The plan's D1 order: core execution, safety and metadata first, the write paths with their own transaction tests next |
| MySQL / MariaDB provider (Phase D2) | — | shipped hidden behind the dev setting until 0.2.0 (as SQLite, above). One provider, two products (`ProductInfo` from the server version). Connections set `AllowUserVariables=true` and always `AllowLoadLocalInfile=false` (profile extras never reach the driver string; with verified TLS MySqlConnector would open any file the server names); `LOAD DATA LOCAL` is refused by the classifier. Transaction state: MariaDB `@@in_transaction`, MySQL its ACTIVE transaction event in performance_schema where that event is recorded (performance_schema on, the transaction instrument and the events_transactions_current consumers enabled, the thread instrumented — probed once per connection, M9); without it a savepoint probe (with autocommit on, SAVEPOINT outside a transaction does nothing and RELEASE then fails with 1305), and unknown — so pinned — with autocommit off or when neither can be read; DDL commits implicitly (`TransactionTraits.DdlCommitsImplicitly`). Catalog markers are SHA-256 hashes the server computes over columns, indexes, foreign keys and triggers (UPDATE_TIME is not reliable; MySQL 9 removed MD5()); routine bodies come from SHOW CREATE on demand; `group_concat_max_len` is raised at connect so they are never truncated. Server warnings and notes reach the Messages tab once: MySqlConnector raises InfoMessage for statements without a result set, SHOW WARNINGS after each batch covers SELECTs, and what the driver already raised is left out of the second. Temporal, BIT and spatial values travel as text (zero dates stay `0000-00-00`); unsigned BIGINT and DECIMAL beyond 28 digits as exact text. **Grid editing** (second slice): result columns map to their table through the protocol's org_table / org_name (exact; one table under two aliases — a self-join — gives no origins); keys are the PRIMARY index or a NOT NULL unique index; values are parameters the server converts, except binary (the grid's `0x…` → bytes), BIT (a whole number) and booleans (`TINYINT(1)`, `true`/`false` accepted), and integer ranges (unsigned included), numbers, dates, datetimes and years are checked in the sidecar first so a bad value is an error before anything is written; no row version, so lost updates are caught by comparing the original values of the changed columns with `<=>` (text compares with the column's collation; float, TIME, JSON, spatial and binary over 64 bytes are not compared and never keys); row counts are rows matched (MySqlConnector `UseAffectedRows=false`), so an unchanged value is not a conflict; there is no RETURNING, so an insert is followed by a SELECT of the new row by its given key values or `LAST_INSERT_ID()` for the AUTO_INCREMENT column, and a key filled any other way is not returned rather than guessed; spatial columns are locked; `data.rowTokens` joins the keys as a UNION ALL (MariaDB has no column list on a derived table). **Import**: `MySqlInsertLoader`, multi-row INSERTs of up to 1,000 rows / about 4 MB with each value converted once for its column's parsed type (never `LOAD DATA LOCAL`), in import.run's transaction rules; the generic loader converted every value twice and parsed the type per value (4.97 s for #23.mysql, now 3.94 s). #23.mysql stays over its 2 s target as a measured baseline: the same 100k rows generated inside the server take 0.88 s and the client-side conversion about 1.1 s; a server-side prepared 1,000-row INSERT measured 2.6–2.8 s against 3.2–3.5 s in the same profile, not enough to take; the target needs `LOAD DATA LOCAL` from an in-memory stream, which stays off (§13): **owner decision (2026-10-31): 3.94 s is accepted as the measured baseline**; new tables are InnoDB, typed from the file (`tinyint(1)`, `decimal(p,s)` up to 65/30 else text, `datetime(n)`, `varchar(255)` / TEXT types by size, offsets kept as `varchar(64)`), a text key column becomes `varchar(768)`; a widening is suggested as `MODIFY COLUMN`, with a note that MODIFY restates the whole column. Sign-in: `caching_sha2_password` full authentication (empty server cache) needs TLS — MySqlConnector also sends the password to a loopback address in clear, as the plan allows, and refuses a plain non-loopback connection (tested against the container's own address); its message is said in Querithm's terms ("turn TLS on", why it looks intermittent), as is a MariaDB ed25519 / parsec account (the driver's plugin package is not shipped). SSH: through the bastion to each product, a dropped tunnel reopened on the next connection, a Unix-socket host refused for tunnels. **Activity monitor**: session identity is (connection id, performance_schema THREAD_ID) — a connection id is not reused until it wraps and the thread id exposes one that was; MySQL reads sessions with the running top-level statement (`events_statements_current`), the InnoDB transaction (`INNODB_TRX`), blocking from `data_lock_waits` (exact) and `metadata_locks` (approximate: any granted lock on the object, said so on the row), locks from `data_locks` + `metadata_locks`, waits from `events_waits_summary_global_by_event_name` (enabled instruments only), top queries from statement digests (rows examined in the logical-reads column; no CPU), metrics from SHOW GLOBAL STATUS (queries/s, buffer-pool hit ratio, running / connected threads, idle in transaction, InnoDB deadlocks where reported; no CPU). Cancel (KILL QUERY, needs the running statement's EVENT_ID) and terminate (KILL CONNECTION) re-check the identity in the same batch: the KILL is a prepared statement built on the server from the thread row that still matches — nothing from the request is spliced in — and "DO 0" when nothing matches. MariaDB (performance_schema off by default) reads `information_schema.PROCESSLIST` and `INNODB_LOCK_WAITS`; the server then hides the thread id, so a session key is the connection id with an empty instance (the contract's "server hides it" case: the monitor still tells its own sessions apart, since a connection id names one live connection) and kills — which need the instance to rule out a reused id — are refused with the reason; locks, waits and top queries are degraded with theirs. I/O and deadlock history are degraded (only the latest deadlock exists, in SHOW ENGINE INNODB STATUS). Actual plans run only for SELECT and TABLE statements (§11). Fixtures: MySQL 8.4.13, MySQL 9.7.4 and MariaDB 11.8.9 as one theory, plus MySQL 8.4.13 initialized with `lower_case_table_names=1` (names stored in lower case; a differently cased query still edits its table) next to the case-sensitive default where `Sales` / `sales` and `T` / `t` stay distinct identities. **Data compare**: text keys are ordered by their UTF-8 bytes (`CAST(CONVERT(x USING utf8mb4) AS BINARY)`: code-point order, no padding, no case or accent folding — the order the .NET merge uses, whatever the column's collation), CHAR as padded text, TIME / JSON / spatial never keys; a compared query must be one SELECT / TABLE / VALUES without INTO and runs in START TRANSACTION READ ONLY; the sync script is one START TRANSACTION … COMMIT, explicit AUTO_INCREMENT values are inserted as they are, generated columns are left out. **Schema compare**: a database is one schema (`ISchemaCompareDialect.DatabaseIsSchema`: two databases compare object by object without the user naming schemas); tables from information_schema (integer display widths dropped, defaults turned into SQL on both products — MySQL reports string defaults unquoted — with ON UPDATE kept on the default, collation only where it differs from the table's), every non-primary index (unique ones included) as its MySQL key-part definition (prefix lengths, DESC, functional parts), checks with NOT ENFORCED, FKs; views from information_schema.VIEWS (fully qualified — SHOW CREATE VIEW drops the view's own database), routines and triggers from SHOW CREATE with the DEFINER account removed and the name (and a trigger's table) qualified; dependencies from the names a definition mentions (MySQL records none). Column changes are one MODIFY COLUMN with the whole target definition (the script notes a COMMENT is not kept); constraints are dropped by kind (PRIMARY KEY, FOREIGN KEY, CONSTRAINT for checks); routines and triggers are dropped and created under a client DELIMITER, views CREATE OR REPLACE; routines cannot be renamed (refused with the reason). DDL commits implicitly, so the script and its header say it is not one transaction. Not compared, and listed: events, sequences (MariaDB), partitioning, table options and comments. **Find usages / rename**: `MySqlReferenceFinder` on tokens (FROM / JOIN / UPDATE / INSERT / REPLACE / TABLE with aliases, calls, a trigger's ON table and NEW / OLD columns); bare table names resolve in the definition's own database (a tab's: its database), table names compare exactly and column / routine names case-insensitively; inside a stored program a bare column may be a variable (possible), PREPARE … FROM marks dynamic SQL; no definition follows a rename by itself, so a table rename is RENAME TABLE plus every rewritten view, routine and trigger (routines and triggers dropped and created — `RenameRules.RecreateDropsFirst`), and a routine rename is refused with the reason; a new name that is a reserved word is backticked. **Dependencies**: no catalog records them, so every view / routine / trigger definition from information_schema is scanned with the reference finder (real references, not matching words), and a trigger uses its table. **Modify → Apply**: the editable definition of a view is CREATE OR REPLACE, of a routine or trigger DROP … IF EXISTS plus the CREATE under a DELIMITER. **Completion**: tier 2 and signature help (stored routines from the snapshot) are PostgreSQL's engine under MySQL's rules (`MySqlCompletion`: DELIMITER-aware statements, backticks — and "x" under ANSI_QUOTES —, reserved words quoted, the default schema is the database of the last `USE` before the cursor and that database's snapshot is read; no named arguments). `MySqlSplitter` lexes lazily from each statement start (it re-tokenized the rest of the text for every statement: a 10,000-statement script never finished in 10 minutes) | The plan's D2 order: connection, safety, execution and metadata first |
| DuckDB provider (Phase D3) | — | shipped hidden behind the dev setting (`QUERITHM_DEV_ENGINES=duckDb`) until 0.2.0 (as SQLite, above). A granted file (read-only unless writes were allowed) or a private in-memory database, one connection per tab, under the §13 rule 12 policy (instances by grant identity; settings locked; definitions audited at open). An **empty** granted file with writes allowed becomes a new database (DuckDB refuses a 0-byte file): a fresh database made under app data is written into the same file, so its identity still matches; read-only, it is refused with the reason. **Intelligence**: `DuckDbTokenizer` is `PgTokenizer` plus `$name` parameters (parity tests for `//`, `==`, `->`, `::`, struct / list literals, E'' strings, dollar quoting, nested comments); statements split on `;` only (DuckDB rejects two statements without one); tier-1 completion with DuckDB 1.5.6's reserved and type/function keywords; tier 2 and signature help are PostgreSQL's token-scoped engine under `PgCompletionRules` (names compare case-insensitively and keep their case, dollar-quoted text is a string, the default schema is the one the last `USE [catalog.]schema` / `SET schema | search_path` before the cursor chose; the PostgreSQL rules are unchanged). **Values**: the driver rounds DECIMAL beyond 28 digits and reports no precision, refuses INTERVAL with months, gives TIMETZ a date and fails on a NULL inside a list of numbers, so DECIMAL (exact, scale digits kept), HUGEINT, INTERVAL, TIMETZ, LIST / ARRAY / STRUCT / MAP travel as DuckDB-literal text (`[1, NULL]`, `{'a': 1}`, `{'k'=1}`; lists of numbers re-read as lists of nullable numbers); BLOB as Binary (the encoder now reads any Stream-typed column through GetStream); TIMESTAMP_NS at the driver's 100 ns ticks; UNION columns and infinite dates / timestamps fail the result with "cast it to VARCHAR" (the driver cannot read them). **Origins**: the driver reports no base columns, so they are predicted from tokens for `SELECT item, … FROM one_table [alias] [WHERE / ORDER BY / LIMIT]` and `FROM one_table` (items `*`, `alias.*`, columns with aliases; other items are columns without an origin); anything else stays read-only with the analyzer's reason. **Editing**: declared PRIMARY KEY only; values as VARCHAR parameters cast to the catalog type; no row version, original values compared typed (floats and nested types not compared, never keys; nested columns locked). **Import**: the appender (100k × 10 in 788 ms vs 24 s with batched INSERTs); each value converted to the column's type in the sidecar, a value that does not convert rejects its row, missing columns get defaults; a table with a list / struct / map / union / interval / enum / bit column is loaded with one prepared cast INSERT per row. **Transactions**: state from `txid_current()` twice; no savepoints; a failed statement aborts the transaction until ROLLBACK. **Plans**: `EXPLAIN (FORMAT JSON)` / `(ANALYZE, FORMAT JSON)` → `PlanTree` ("duckdb-json"); DuckDB 1.5.6 prints `{"result": "error"}` for some actual plans (count(*) answered from a file table's statistics), reported as such. **Metadata**: duckdb_tables / views / indexes / sequences / functions (macros as functions, table macros as table functions; overloads one object) of the session's catalog plus `temp` (session-local, schema "temp"); a sequence's marker ignores its START (rewritten as values are drawn); user types are listed (marker over type oid, logical type and labels; an ENUM scripts from its labels, any other type from `typeof(NULL::name)` — DuckDB stores no SQL for types). Not available, each with a reason: schema / data compare, find usages / rename, monitor, trace, debugger, ChangeDatabase, SSH. **Packaging**: the single-file sidecar extracts native libraries on first run — Rust sets `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to `<data dir>/native` (since 2026-11-03 `<app local data dir>/native/<build>`, H7, below; compiled, with the shell's 37 Rust unit tests, on 2026-10-31 against GTK / WebKit dev packages extracted without root); first `system.ping` +185 ms on the first run only (1,105 vs 920 ms; warm 826 vs 825 ms, linux-x64 trimmed, this machine). Sidecar single file: linux-x64 68.0 MB (was 44.7), win-x64 45.4 MB, osx-arm64 50.5 MB, osx-x64 53.6 MB — a macOS publish keeps only its RID's slice of the universal libduckdb.dylib (117.0 → 57.2 / 59.7 MB; `sidecar/build/ThinMachO.targets`, the byte copy `lipo -thin` makes, so it thins on any build OS; `MachOThinTests`; osx-arm64 was 69.4 MB unthinned). Installers: next row. Grid cell input: TINYINT is signed on MySQL and DuckDB (the shared 0–255 range was SQL Server's; a MySQL regression test pins it) | The plan's D3 table and file policy |
| Installer size with DuckDB (Phase D3) | metric 17: ≤ 120 MB per platform | **Owner decision (2026-10-31): the Linux AppImage has its own budget, ≤ 150 MB (`17.appimage`; release.yml gates it apart); every other package stays ≤ 120 MB.** The Linux AppImage was measured by rebuilding the released v0.1.5 AppImage's squashfs (zstd level 15, 128 KiB blocks; that reproduces the released image within 3 KB) with this build's sidecar: 143.4 MB (v0.1.5: 119.5 MB). Keeping the sidecar uncompressed inside gives 137.8 MB and a stripped libduckdb.so saves 1.5 MB more — still over. The AppImage carries WebKitGTK, JavaScriptCore and ICU that every other package takes from the OS; projected from the v0.1.5 packages plus the measured sidecar growth (not built here): deb / rpm about 66 MB, Windows installers about 43 MB, dmg about 48 MB | The plan: revise packaging explicitly, never raise the target silently or download drivers at runtime |
| Webview engine handling (Phase D0e/D0g) | engine and dialect decisions as `=== "pg"` ternaries with SQL Server as the else | one table per fact, `src/engines.ts` (`ENGINES: Record<Engine, …>`, `DIALECTS: Record<Dialect, …>`, `assertNever`): adding an engine to the generated contract fails the build at every decision. The profile editor offers what `system.engines` exposes (falling back to SQL Server and PostgreSQL); a file engine shows Location (database file / in-memory), "Pick file…" (the shell's grant) and "Allow writes…" (the shell's own dialog); "Duplicate profile…" never carries a file grant. An in-memory profile is shown connected without a session of the Explorer's own ("each tab has its own database"). Dynamically typed columns (D0f) sort NULL < numbers < text < blob, export numbers as JSON numbers only when exact, chart only when every cell is numeric (otherwise a visible note), and the cell viewer decodes blobs from hex. Snapshot keys and the active-database mark use exact names. Tools opened on a local engine (ER diagram, schema / data compare, import) bind the session of a query tab on that database (the active one first) and read that session's snapshot while it lives; afterwards a file database falls back to the shared snapshot and an in-memory one shows nothing, with the reason; a compare the provider refuses shows its reason with Compare disabled. A rename to a reserved word is quoted in every dialect | A new engine must not silently become SQL Server in the webview either |
| Import reject log | `<file>.rejected.csv` next to the source | `<data dir>/imports/<time>-<file>.rejected.csv`, path shown in the wizard | The sidecar never writes into the user's folders (read-only access to the picked file) |
| PostgreSQL import of JSON/XLSX | `BeginBinaryImport` | text `COPY … (FORMAT csv)` for every source | The server's input functions parse every type (enums, arrays, json) without a per-type binary writer; same speed class (metric 23) |
| SQL Server import inside an open transaction | `SqlBulkCopy` | multi-row INSERT under a savepoint | `SqlBulkCopy` cannot join a transaction begun with T-SQL ("Unexpected existing transaction") |
| Import NULL markers | — | empty fields and the text NULL read as NULL by default (option) | Matches what SSMS and most exports write |
| Grid edit concurrency | token read with the query | token read lazily on the first edit of a row, together with a check that the displayed values still hold | Plain SELECTs stay as fast as before; the value check closes the gap between query and first edit |
| SSH tunnel transport (§13 rule 4) | no TCP listener | SSH.NET local forward on `127.0.0.1:0`; each accepted connection must come from a socket this process owns, else it is closed before a channel opens (fail closed); macOS and other OSes fall back to a connect window held only while one of our drivers opens | The drivers can only dial TCP; SSH.NET exposes no stream-level forward; doing SSH in Rust breaks §15 rule 10 |
| SSH host keys | — | `<data dir>/known_hosts` (SHA-256 fingerprints); unknown and changed keys are refused until the user trusts them in a dialog; agent auth and jump chains not supported yet (visible reason) | Never trust on first use silently |
| PostgreSQL `VerifyFull` through SSH | — | degrades to `VerifyCa` with a note (the certificate is still checked against the CA); SQL Server keeps checking the real name via `HostNameInCertificate` | Npgsql checks the name it dialed (127.0.0.1) and has no override |
| SSH targets | — | SQL Server named instances / named pipes and PostgreSQL failover lists / Unix sockets are refused with a reason | One forward carries one fixed host:port |
| Schema compare model | SMO per object (SQL Server), catalog queries (PostgreSQL) | catalog views in one batch on both engines | SMO object-by-object scripting of the 600-table fixture takes far longer than metric 21 allows; the catalog reads the same properties |
| Module identity | exact hash + token hash | exact text after the CREATE / OR ALTER / OR REPLACE header, then tokens | SQL Server stores the header as typed, so every module a migration created would otherwise differ from its source |
| PostgreSQL enum values | `ALTER TYPE … ADD VALUE` after COMMIT before PG 12 | before BEGIN on every version (`ADD VALUE IF NOT EXISTS`); schema compare needs PostgreSQL 12+ (visible reason) | A value added inside a transaction cannot be used in it, and later statements (defaults, checks) may use it; adding is idempotent |
| Saved snapshots | snapshot file vs live | `.qschema.json` model files saved through the shell, picked with a token; a file target compares but refuses a migration | The sidecar never writes into user folders, and a file is not a server to migrate |
| Possible renames | user confirms | only renames the comparer offered (same type, unambiguous pairing) are accepted from the webview | The sidecar never trusts names from the webview |
| Data compare GUID keys | server byte order or hash-partitioned mode | ordered by canonical text on both engines | `CONVERT(char(36), g)` and `g::text` sort alike, so one merge join serves every engine pairing without temp files |
| Data compare values | per-type readers | every column read as the grid's text (`ValueText`), normalised per type in .NET | One reader for every type and engine; the same text feeds the diff grid and the sync script's literals |
| Data compare reads | the session's isolation level | two pool connections of the sides' profiles (default isolation); the result shows a consistency hint | Two readers must stream at once, which one session connection cannot do |
| Data sync scope | the script can cover all differences | the script covers the kept differences (`maxDiffRows`, default 50,000) and says so when there were more | The script is one RPC string; re-running the compare after applying it picks up the rest |
| Reference finders | in the dialect service | `IDbProvider.References` (`IReferenceFinder`), implemented in the intelligence projects | Find usages needs the snapshot context and the M5 script dialect, not the completion service |
| SQL Server column rename | `sp_rename` leaves computed columns, checks and filtered indexes to recreate | SQL Server refuses the rename while they exist (measured on 2022); the script drops them, renames, and re-adds them rewritten | Same end state; the measured server behaviour decides the order |
| Find usages scope | catalog dependencies ∪ token search ∪ tabs | cached definitions of views, routines and DML triggers (whole-word pre-filter) ∪ tabs; a trigger whose definition the login cannot see (no VIEW DEFINITION, encrypted) is listed as unchecked with that reason | The cache is complete once filled and the catalog cannot see dynamic or unqualified references |
| Local rename | Monaco rename widget | a small rename dialog applying the spans as one undoable edit | One F2 path for local and schema symbols; the dialog decides which one applies |
| GROUP BY projection suggestions | heuristic extraction of arbitrary projection expressions | direct column references only, in the current SELECT scope | Aggregate, window, scalar and user-defined expressions require semantic analysis; the tolerant scanner must not emit malformed SQL or promise a complete GROUP BY for unsupported expressions. |
| Cursor pair decorations | scan the document synchronously on every cursor event | debounce 75 ms, cache balanced pairs per token-stream version, cancel on model change/disposal | CPU profiling on the §14 large-procedure fixture identified repeated block scanning as the dominant cursor-matching cost; cold tokenization is still covered by the typing gate. |
| Grid quick statistics / formatted clipboard | inspect up to 50k cells after enumerating every selected row; silently approximate totals | bound iteration to 50k cells without a full row-index array; show total selection count and sampling label; mark sum/average approximate; IN/Markdown copies above 50k cells refuse with an export suggestion | Preserves responsiveness and distinguishes sampled calculations from full-selection totals. Text identifiers are not numeric data. |
| Aborted results | prune incomplete/empty results on error notifications | prune only empty unfinished sets after Arrow end-of-execution; preserve completed empty sets and partial rows, marking partial rows incomplete | Control notifications and data frames have independent delivery timing. Error notifications must not discard results still arriving. |
| Connections across restarts | nothing connected on launch; servers appear in the explorer once used | `workspace.json` keeps the explorer's server list (profile ids only); after the tabs restore, each reconnects in the background with `connect(id, {quiet: true})`, which never prompts: a password missing from the keyring, a rejected password or an SSH host-key/secret question leaves the server in the explorer with an error and a Connect item. Tab sessions stay lazy | The owner wants the workspace back as it was left. Startup is not blocked (nothing awaited) and no dialog opens at launch. Explicitly disconnected servers are not in the list and stay disconnected |
| PostgreSQL tier 2 (§9) | "refines with a real AST" | scope-aware completion on `PgTokenizer` tokens (statement scopes, PL/pgSQL declarations, temp tables), no full parse and no diagnostics | There is no PostgreSQL parser in the sidecar and adding one would be a new dependency; the token scopes answer the contexts the T-SQL tier covers (in-process, re-tokenized after an edit, 10k-line function: median 50–67 ms over 3 runs, Release, Windows; metric 27 measures the RPC roundtrip) |
| Trigger "Modify" on PostgreSQL | open the trigger | opens the trigger *function* (found through `meta.dependencies` → the trigger's `uses` function); Script as ALTER gives `CREATE OR REPLACE TRIGGER` from PostgreSQL 14 (drop and create before, and always for constraint triggers, which have no `OR REPLACE`). The clones of a partitioned table's trigger on its partitions are not listed: only the parent can be changed | The body of a PostgreSQL trigger is its function; the trigger itself only binds events, columns and WHEN to it |
| Trigger enable / disable | — | Script ENABLE / Script DISABLE generate `ENABLE\|DISABLE TRIGGER … ON …` (SQL Server) or `ALTER TABLE … ENABLE\|DISABLE TRIGGER …` (PostgreSQL) into a new tab; nothing runs from the explorer. A PostgreSQL trigger on a view is refused with the reason (`ALTER TABLE` does not accept views) | Every change goes through the guarded `query.execute` path (§13 rule 7) |
| Renames through triggers | — | SQL Server trigger bodies are rewritten (`inserted` / `deleted` resolve to the trigger's table, `IF UPDATE(col)` counts); PostgreSQL triggers are listed as followed by the server (`UPDATE OF` attnums, the WHEN tree, table and function are stored by oid). `NEW.col` / `OLD.col` inside a PL/pgSQL trigger *function* are not found: the function is not tied to one table | Tokens cannot tell which table a trigger function will run for; the preview names what was checked |
| Snippet body editor (A3) | — | the snippet dialog borrows the shared editor (`borrowEditor`: temporary model, then the tab's model, view state and read-only flag back); bodies use Monaco snippet syntax. With no query tab open, the editor is created for the dialog and the first tab's group takes it over | §12: never a second Monaco instance |
| Snippet prefixes | `prefix UNIQUE` | unique ignoring case per dialect, for user snippets and built-ins alike (a dialect-free snippet overlaps every dialect); checked in the save transaction, not by an index | Typing a prefix must pick one snippet |
| Integration / bench fixture host | `localhost` | `QUERITHM_IT_MSSQL_HOST` overrides the SQL Server fixture's host for the integration tests and `pnpm bench`, `QUERITHM_IT_PG_HOST` the PostgreSQL one for the integration tests (default `localhost`; on the Windows dev box a PostgreSQL connect via localhost took 2.3 s, via 127.0.0.1 41 ms) | On a Windows dev machine `localhost` did not reach Docker's loopback-published 14333 through SqlClient while `127.0.0.1` / `::1` did; the default is unchanged |
| Monitor payload (Phase B) | Arrow for row sets | `monitor.snapshot` is JSON: the full session list every tick (≤ 1,000 rows), statement texts once (content-hash `TextId`, 4 KB previews; `monitor.texts` refetches) | A few hundred small rows per tick that the webview diffs by `SessionKey`; metric 28 bounds the payload. Query result rows still travel only as Arrow |
| Monitor panes | `MonitorPanes` `[Flags]` enum | `MonitorPane[]` (one camelCase name per pane); `queryStore` / `health` reserved and refused | The contract generator emits enums as string unions and the wire format writes names, so a flags value would not round-trip |
| Own-session marker | `ActivityRow.OwnTabId` | `OwnSessionId` (the webview maps it to its tab) | The sidecar knows sessions, not tabs |
| SQL Server `KILL` | parameterised statements only | `KILL <n>` with a validated integer literal (1–32767) inside a parameterised identity check (`IF EXISTS(… login_time = @start AND is_user_process = 1) KILL n`) | `KILL` takes no variable; the literal is built only from a parsed integer, never from client text (K22) |
| Monitor on Prod | Prod guards on every write | the monitor connects to Prod profiles without a prompt; its connection is read-only by settings (READ UNCOMMITTED, `LOCK_TIMEOUT 2000`, `DEADLOCK_PRIORITY LOW` / PG `statement_timeout 5s`, `lock_timeout 2s`); cancel and terminate on Prod need the typed confirm (`GuardReason.ProdWrite`) | Watching changes nothing; killing does |
| Per-profile connection budget | pool max 8 per profile | a monitor adds 1 dedicated connection, plus 1 kill control connection only during a kill (closed after); the own-tab banner check borrows 1 pool connection (inside the cap) for one query per 10 s, waiting ≤ 1 s in the queue | A kill must work while the pool is exhausted by blocked tabs (C3), so the control connection is outside the cap; measured by M14 |
| Identity read on open | — | every physical open reads its own `SessionKey` (one extra roundtrip per open, not per execution); measured on the Linux fixture (localhost) as +1.9 ms on a cold pool connection's execute → first batch: SQL Server 15.5 → 17.4 ms (+12 %), PostgreSQL 6.8 → 8.7 ms (+28 %), 100 samples each against the pre-Phase-B sidecar (`11cold`) | The monitor must recognise Querithm's own connections and refuse to kill them. It cannot be folded into another round trip (opens make none) or deferred past the hand-out (the registration window, K8/K9); pooled connections pay it once |
| Identity read the server refuses | a failed identity read fails the open | a read the server refuses on a working connection (SQL Server 229/230/207/208/297/300 or no own row; PG 42501/42P01/42703/42883 or no own row) keeps a tab's or dedicated connection, unidentified; every kill is refused while one is open (`querithmUnidentified`, on every server, as for a reconnect); the banner tells that tab it cannot check it; a monitor whose own read is refused stops with the reason | Failing the open made every query fail on such a server for a monitor-only safeguard (graceful degradation, CLAUDE.md §2); refusing kills keeps the safeguard |
| Querithm's connections in the monitor | kill any session | rows of Querithm's own connections (tab pool, dedicated, monitor, control) are labelled and never killable from the monitor; cancel or disconnect them from their tab | Killing a tab's connection from the monitor would silently end the user's own transaction |
| PG identity without `pg_read_all_stats` | kill other roles' backends with `pg_signal_backend` | such rows have an empty `Instance` + `IdentityHidden` reason and are refused at the RPC boundary | The server hides `backend_start`, so the exact identity re-check cannot run (B0 row 4) |
| Rollback detection after `KILL` | `command = 'KILLED/ROLLBACK'` | request `status = 'rollback'`, or a target this monitor terminated whose session row remains (STATUSONLY 6109 for the percentage) | Measured on SQL Server 2022: a killed sleeping session has no request row at all (B0 row 2) |
| Monitor connection retry | driver default reconnect | `ConnectRetryCount = 0` on monitor connections; the monitor reconnects itself with back-off 1 s → 30 s and probes again | A silent driver reconnect would change the monitor's identity under the probe results |
| Activity provider shape | stateless pane methods on `IActivityProvider` | `ProbeAsync` returns an `IActivitySession` bound to one monitor connection (probe result + caches, one `TickAsync` round trip for sessions, waits, metrics and I/O) | No capability outlives the connection it was probed on; one round trip per tick |
| Closed session with an open transaction | pin until the user decides | if the tab was closed while its batch opened a transaction, the connection is discarded (the server rolls back) | Nobody can commit or roll back for a session that no longer exists; pinning would leak the connection |
| Server-side rollback on cancel | — | when a cancelled or failed run started inside an open transaction and the server ended it (SQL Server `XACT_ABORT ON`), the session unpins and warns "the transaction that was open before this run has ended on the server" | Querithm never rolls back on cancel, but the user must learn that their transaction is gone (B0 row 3) |
| Monitor exports | save dialog writes the file | the sidecar writes exports under `<data dir>/exports` (`.partial` then rename, free name); the webview reads that file with the `$DATA/querithm/exports/*` read scope and saves it where the user picks; the dev bridge serves the read as `export_read`, scoped to the exports folder | The sidecar never writes into user folders; no new capability |
| Monitor benchmarks | `scripts/bench/run.mjs` | metrics 28 (sidecar part) – 32 run as opt-in xUnit benches (`QUERITHM_BENCH_MONITOR=1`, `Integration/MonitorBench.cs`) writing `bench/results/<date>-monitor-<os>.json`; the webview part of 28 (`28ui`, `28first`) is in `pnpm bench` | They need hundreds of real server connections and seeded deadlocks, which the integration fixture already manages |
| Trimmed-build self-test | — | `querithm-sidecar --self-test` loads the embedded wait dictionary and prints its counts, then exits | W10 proves the dictionary loads under `TrimMode=partial` in the published build |
| Banner check pool wait | 1 s bounded borrow | the 1 s bound covers only the wait for a full pool (`poolBusy`), never opening the check connection | On the PostgreSQL fixture an open took longer than 1 s, so every cycle was cancelled and the banner never checked (regression test O14) |
| SQL Server transaction DMVs during a rollback | join `sys.dm_tran_session_transactions` per tick | guarded by a `transaction_state = 7` check in the same batch (`@rb` startup filter); transaction ages shown as unavailable with the reason while a rollback runs; the kill preview is "unknown" then (long-rollback confirm) | The DMV blocks on `MSQL_XACT_MGR_MUTEX` until a terminated idle session's rollback ends and a client cancel is not processed meanwhile (plan B0 row 9); unguarded, the monitor froze for the rollback's length |
| SQL Server error mode on monitor connections | the provider's connection settings | `IActivityProvider.PrepareMonitorConnection` turns `FireInfoMessageEventOnUserErrors` off on monitor and kill control connections; the banner check turns it off around its query and restores it | Query tabs stream user errors as messages; on monitor connections that made a denied DMV read look like an empty success and a refused `KILL` look like a termination (plan B0 row 10) |
| Secondary monitor job cut at its time limit | — | the locks, top-queries and deadlock panes switch to `error` with "did not finish within its time limit … retrying" when their job is cancelled (time limit or pre-empted by a kill) | The last result must not keep looking current (L7) |
| Kill of a session that ended after the snapshot | — | `KillPreview.SessionEnded`: the kill returns `gone` ("already ended; nothing was sent") instead of asking for the long-rollback confirm | The server-side preview found no row and read as "risk unknown" (K5) |
| Timing gates in the test suite | parallel with everything | `Tier2CompletionTests` runs in the `timing` collection (parallelization disabled); the 150 ms budget is unchanged | With the monitor's integration tests in parallel, its p95 rose from < 150 ms (alone) to 245 ms on the dev box: CPU contention, not the completion path |
| Own sessions without VIEW SERVER STATE (SQL Server) | own sessions with their text | own rows without text, marked "query text requires VIEW SERVER STATE"; request detail gives the same reason | `dm_exec_sql_text` / `dm_exec_connections` are denied (300) even for the login's own session (plan B0 row 4) |
| Identity after a driver reconnect | re-read when the pooled connection is next borrowed | re-read right after every run whose connection token changed; the monitor refuses kills while any of Querithm's connections reconnected and is not identified again (`querithmReconnecting`) | Between the reconnect and the next borrow the tab's session looked foreign and was killable (plan B0 row 12) |
| SQL Server top-queries flush | detected at the next 30th (full) read | an incremental read that sees the plan cache below 10 % of its size at the last full read does a full read at once | A flush was reported 150–300 s late (plan B0 row 13) |
| Monitor side panel after its session ended | — | the detail / diagnosis stays, with "Session N has ended; this is what it showed when you opened it" | Stale content must say it is stale (selection tied to the exact `SessionKey`, U2) |
| Monitor history storage | in-memory diffs of snapshot objects | each tick held as Deflate-compressed JSON (rows, diffs, the rest of the snapshot), keyframe every 10 ticks; the 50 MB cap counts the compressed bytes | Measured (metric 29, 500 sessions changing every tick): object graphs took 56 MB for 187 of 300 ticks and the estimate undercounted; compressed: 300 ticks in 14 MB, scrub p95 9 ms. Covered by the trimmed `--self-test` |
| Deadlock polling (SQL Server) | poll the ring buffer and the event files | the 30 s poll reads the `system_health` file being written once per monitor connection (the `event_file` target names it), then only the events after its last position (file + offset), across rollovers; two failed or cut first reads switch it to the ring buffer with the reason; the Deadlocks pane does a full read; every file read is bounded by an 8 s command timeout (then ring buffer only, with the reason); the ring buffer is read only when the files cannot be | A full read costs in proportion to the files' size (5.7 s for 30 MB on the fixture; files may total 1 GB) and must not recur every 30 s; the ring buffer alone misses the newest events once its XML hits the 4 MB cap (plan B0 row 15) |
| PostgreSQL wait profile cost | `pg_wait_sampling` every tick when installed | the profile read is timed separately; after three reads over 50 ms the waits come from the sessions (tick-sampled) until the monitor reconnects, with the reason in the pane | With `profile_pid = on` the profile keeps every past backend: 121k entries / 241 ms per read after churn (plan B0 row 16) |
| Top-queries tracking memory | per-entry deltas, bounded server cost | each tracked statement is a 128-bit key and seven counters (the sidecar digests the raw SQL Server plan handle + offsets — `HASHBYTES` on the server made the full read 1.7× slower; hash and text key only for entries that ran since the cutoff); at most 200,000 entries, beyond which the pane stops with the reason; since-start totals for at most 10,000 groups | The soak's heap grew 40.5 MB in 11 min: the tracker kept every cached statement with three handle strings (~800 B; 151 B now, measured) — plan B0 row 17 |
| Text store sizes | 4 MB LRU of unreferenced texts | sizes are heap bytes: UTF-16 characters plus 360 B per text | UTF-8 length understated a short text ~6× (78 B counted, 451 B held), so the cap bounded ~23 MB — plan B0 row 18 |
| Monitor command timeout | jobs bounded by the scheduler's cancel | monitor and kill control connections also carry a driver command timeout of 15 s (`ActivityMonitor.CommandBackstopS`), above every job's own limit (tick 5 s, deadlocks 10 s, kill 10 s); the banner check's query has 5 s | A token cancel makes SqlClient send only an attention; a server that never acknowledges it (half-open link, B0 row 9) held the job — and stop, reconnect and kill preemption behind it — indefinitely. A driver timeout breaks the connection after its attention / cancellation wait |
| Monitor connect bound | — | open, identity, session settings and probe together are bounded by 2 × the profile's connect timeout + 15 s (45 s by default), then the normal back-off | A server that accepted the login and then stalled kept the monitor "connecting" forever with every kill refused; the bound is never shorter than a healthy open through an SSH tunnel (the O14 lesson) |
| Monitor reconnect after a rejected sign-in | reconnect with back-off 1 s → 30 s | a sign-in the server rejects (SQL Server 18456 / 18488 / 18452, PostgreSQL 28P01 / 28000, SSH authentication) stops the monitor with the reason instead; an unreachable or failing-over server (e.g. 4060, 57P03, 53300) is still retried; the back-off restarts at 1 s only after a tick succeeded | Retrying with the same remembered secret cannot succeed and, overnight after a password rotation, made ~120 failed logins an hour (account lockout); a server that accepts and then drops the monitor must back off too |
| Monitors and profile edits | — | saving a profile stops its monitors ("start the monitor again to use the new settings") | A monitor kept the profile it started with (host, options) while kills read the environment fresh: after an edit it watched — and could kill on — the old server without that server's Prod confirmation |
| Own-tab banner and a running monitor | a running monitor's snapshot answers | only a monitor whose latest read covered every session (no database filter, not truncated) within 2 × its interval + 5 s answers; otherwise the shared 10 s check runs. A filtered or truncated snapshot never clears a tab's banner | A paused, reconnecting or timing-out monitor sends no rows, and a filtered one cannot see every tab, yet each silenced the banner |
| Secondary monitor jobs after a late tick | at most one due secondary job after each tick | a secondary job already due when a tick ends runs before the next tick even if that tick is already due; one that becomes due later waits for the tick | Tick-first starved every secondary job when a tick took longer than the interval (C8: none ran for ~130 s); secondary-first in every case delayed ticks by a whole deadlock read (C7: lag p95 1.7 s) |
| Rollback tracking after a terminate | per monitor | shared by the profile's monitors (`MonitorService`), pruned only by a read of every session | A second or restarted monitor did not know a killed sleeping session was still rolling back and sent `KILL` again, which the server accepts silently |
| Staged monitor exports | — | staged files (`.json`, `.xdl`, `.partial`) in `<data dir>/exports` older than 10 minutes are deleted at sidecar start (in the background), every 5 minutes and before each export; links are never followed, and nothing is written to or deleted through an exports folder that is itself a link. The webview reads the staged file before it opens the save dialog, so the sweep never removes a file mid-save | The webview cannot delete the staged copy after the save (no remove capability), and a cancelled save left it too: deadlock exports always hold query text, logins and hosts (§13 user data) |
| Own-tab banner after a rejected sign-in | the shared check retries next cycle | a sign-in the server rejects (the monitor's codes, plus a changed or unknown SSH host key) pauses the profile's check until its connections are rebuilt (a new secret, a profile edit, a disconnect: `SessionManager.CredentialEpoch`), and the tabs are told once; opening the check's connection is bounded at 45 s (the queue wait stays 1 s) | Retrying every 10 s with the same secret made ~360 failed logins an hour while a tab kept a transaction open after a password change (account lockout) |
| Blocking tree for a pile-up | each blocked session under each of its blockers, fully expanded | a session's own victims are expanded once, under its shallowest occurrence (breadth-first from the head blockers); its other occurrences are shown, marked "blocked sessions shown under session N", and not descended into; at most 5,000 items, then a truncation row | PostgreSQL reports soft blocks: on a hot row waiter k is blocked by every waiter ahead of it, so expanding every path is exponential (30 waiters: ~2^28 rows, rebuilt every tick) |
| New-deadlock toast | deadlocks newer than the monitor's start (client clock) | the first completed deadlock read (the server's retained history) is history; every later new event toasts once | Comparing the server's event time with the webview's clock missed or invented toasts under clock skew (S11 rule: durations and times from the server) |
| Deadlock ring buffer size check | 5 MB per event, checked before buffering | the ring-buffer target's XML is read whole and each event is checked against 5 MB while it is split out; the event-file path filters sizes on the server | The server caps the ring buffer's output at ~4 MB, so the read is bounded by the server; only a hostile server could send more, and then at most once per read |
| Deadlock ring buffer read | read with every deadlock read | read only when the event files cannot be read (failed, cut, given up, or no `event_file` target) | The files hold every event the ring buffer holds. Its full XML (2.1 M characters) cost ~43 MB of allocation and ~0.7 s per read through SqlClient's async path (~2,000 pooled char buffers rented as its packets arrive), on every 30 s poll per monitor; afterwards a poll with nothing new took 12–31 ms and ~18 KB. The pooled buffers and large-object-heap gaps they left were the soak's heap swings |
| Top bar / status bar split | status bar shows `TX OPEN` with elapsed time and holds the database picker; top bar has separate Comment / Uncomment / Estimated / Actual Plan buttons | the top bar owns the environment badge, the connection › database pickers joined to Run ▾, Explain ▾ (estimated plan; actual plan and the SQL Server include-toggle in its menu) and the transaction clock; the status bar keeps the 4 px environment edge and plain text; comment commands moved to the editor's right-click menu | A five-expert UI review: the same state shown loudly in three places stops being seen, and Run sat ~1,000 px from the target it runs against. Every command keeps its shortcut and palette entry |
| Explorer server list | "saved profiles appear disconnected until used" (§10); the code listed connected servers only, and Disconnect removed the row | the servers in use (connected, connecting, failed) and the pinned profiles, nothing for the other saved profiles (the header's Connect… opens them; an empty tree still says "No servers in use · N saved connections"); ⋯ → "Show all saved profiles" lists every profile; an unpinned server leaves the tree on Disconnect | The owner keeps ~100 profiles and works on one for weeks: 99 grey rows would bury it. Pinning (shared with the Connect dialog) keeps the few that matter in view. A "N saved connections · Connect…" row after the servers (v0.2.0) was dropped on 2026-11-02 (owner decision): it repeated the header's Connect… |
| Explorer levels | Server → Databases → database | Server → database; system databases other than the profile's default in a "System databases" folder | One level less on every path (6 → 5 deep at 280 px); `postgres` is flagged system by the PostgreSQL provider but is usually the working database |
| Explorer expansion | React state, lost on restart; keys by object id; a newly connected server expanded down to its default database | expansion saved in the workspace file by name (`s:` / `db:` / `c:` / `o:<kind>:<schema>.<name>[(arg types)]`, the 2,000 most recent keys; a name collision, e.g. a PostgreSQL function with OUT arguments beside one without, appends the object id); a user connect opens one level, a background reconnect restores the saved expansion; a restored database reads its catalog only once it is in view | Owner decision (follows SSMS / ADS / DBeaver); names survive DROP and CREATE and engine restarts, ids do not |
| Explorer search scope | the active tab's database, else the first connected server's default database | the active tab's database only; no tab, no database, a server away or a catalog still loading say so | The fallback searched (and Enter ran Select top 1000 on) a server the user had not chosen, possibly Prod |
| Explorer keys | app shortcuts everywhere outside the editor | in the tree, Ctrl+Enter, Ctrl+Alt+Enter, F2, Shift+F12, F5 and Ctrl+C act on the row (F5 refreshes there, as in SSMS); in the search box only Ctrl+Enter and Ctrl+Alt+Enter (insert the first result) | Ctrl+Enter in the search box ran the active tab's query instead of inserting the name; F5 in the search box or on a header button must still run the query (taken there, it did nothing and the webview could reload) |
| Explorer selection color | `--q-active` for every selected row (§12) | `--q-row-sel` (accent 12 % over the panel) + `.q-hl` + a 2 px accent bar | Error, warning and accent status text on `--q-active` is 3.7–3.8:1 (dark); on `--q-row-sel` it is 5.2–6.8:1 in both themes |
| Script tab titles | `<name>.sql` | `<VERB> <name>.sql`, the schema kept unless it is the engine's default (`dbo`, `public`): DROP, DROP and CREATE, CREATE, ALTER, EXEC, ENABLE, DISABLE, INSERT / UPDATE / DELETE; Select top 1000 keeps `<name>.sql`; the execute dialog writes `EXEC <name>.sql` | A DROP script must not look like a CREATE one before it is run; the default schema only widened the tab (tab strip owner revision 1) |
| Environment on a tab | §12: environment word (Prod with a shield) · profile · database in the env color; §6: Prod paints the tab header plus a 2 px border, every environment its color on the tab's top border | no environment word on line 2 of a tab, in Ctrl+P or Ctrl+Tab rows; the database name is drawn in the environment color (fitted to 4.5:1 on every tab surface) and the server muted; only Prod has a band (2 px, 3:1 on every surface); the word stays in the tooltip, the top bar, the tab menu header, the close dialogs and the accessible name, and Ctrl+P still matches "prod" | The owner found the word under each tab name repetitive (tab strip owner revision 2). Trade-off: on the strip Local, Dev, Test and Stage now differ by color alone (WCAG 1.4.1); Prod is still told apart by its band, and every other place names the environment |
| Active tab surface | `--q-active` fill (1.88:1 / 1.36:1 against inactive tabs) | the Explorer's selection: `--q-row-sel` + a 2 px `--q-accent` bar (5.68 / 5.22:1); inactive tabs transparent on `--q-panel`; the shown tab of an unfocused split group a 2 px `--q-line-strong` bar | Non-text contrast (WCAG 1.4.11) and one "selected" look across the app (tab strip panel D1, D2) |
| Tab width | 128–210 px, every tab shrinking alike | as wide as line 1 needs + 38 px, 120–184 px; 150 px while disconnected; pinned 128 px | Tabs were wider than their names (owner revision 1); "disconnected" must never be cut |
| Tab close slot | 24 × 24 at the tab's right end, as high as both lines (D12) | 24 × 20 at the end of line 1; line 2 runs the tab's full width; a cut tab that is inactive and unmodified lends the slot's room to its name and draws a hover's × over the name's end, with a 10 px fade; active, modified, pinned and renaming tabs keep the room | The empty column under the × cut line 2 (database and server) on every tab (owner revision 3, 2026-11-02). 20 px high, not 24: a 24 px slot centred on line 1 would cover the top of line 2 |
| Closing a tab (§6 rule 5) | Commit / Rollback / Keep open; Commit the primary button; a running query cancelled silently on close; bulk closes stop at the first transaction | Keep open removed (it removed the tab and left the transaction open with nothing to end it); Cancel is the default; a running tab asks first (Keep tab default) and waits for the cancel; middle-click does nothing on running or open-transaction tabs; bulk closes keep them and report "Closed 9; kept 2: 1 running, 1 open transaction" | Verified data-loss paths (tab strip panel D20, D21) |
| Recently closed tabs | the last 20 | capped by size (8 MB of SQL), a bulk close one "Reopen N tabs" entry, reopened tabs back in their strip and position | A count cap dropped small tabs to keep huge ones and split a bulk close into 20 unrelated entries (D28) |
| Go to definition | a new read-only tab each time | the tab already showing the object is activated; otherwise one preview tab per strip (italic, "Preview"), replaced by the next and announced, kept by double-click or pin | Repeated jumps filled the strip with read-only tabs (D25) |
| Untitled tab names | `SQLQueryN.sql` until renamed or saved | at its first run an untitled tab takes a verb-first name from its first statement (tokens), once; never over a user's name; a setting turns it off | Thirty "SQLQueryNN" tabs could not be told apart (D24) |
| Tool tabs in the strip | opened at the end; outside "≡", Ctrl+P, Ctrl+Tab, Alt+N and Ctrl+PageUp/PageDown; no menu | first after the pinned tabs, in every strip list and key, with the tab menu and the disconnected state | They were second-class (D16, D17) |
| Settings (§12) | one 720 px dialog: a grid of fields, a flat list of checkboxes, a 224 px shortcut list | an 880 × 560 modal with eight pages in a vertical tablist, one search over every page, rows declared in `src/settings/registry.ts`, an About page (version, update status, credit "Made by Taosif"); still a modal, not an editor tab | A five-expert review (2026-10-30): nothing was grouped or explained, engine-only options were unlabelled, and the version was nowhere to be seen. A modal keeps the global shortcut dispatcher off while a shortcut is being recorded and keeps the query tab visible for a live font preview |
| Settings persistence | saved settings merged over the defaults; a failed read or write ignored | values read back are cleaned (types, choices, ranges, unusable shortcuts); after a failed read nothing is written until a read succeeds; a failed write is shown with Retry and retried when the engine restarts; pending changes are flushed on close and before an update restarts the app | A failed read at start left the defaults in memory, and the next change wrote them over every saved setting; an emptied number field stored 0 (font size 0, unlimited rows) |
| User shortcuts | any key recorded as typed (`e.key`) | recorded with the dispatcher's own `eventToShortcut`; a shortcut needs Ctrl, Alt or (macOS) Control, or a function key; Tab, Esc, Alt+F4, Cmd+Q, Ctrl+A/C/V/X/Y/Z and Ctrl+K are refused; a key another command uses is offered for Reassign; every command can be bound, each of its keys separately | Recorded arrows, Shift+digit and Cmd never matched the dispatcher; Esc while recording closed Settings; Tab or a bare letter bound as a shortcut trapped the keyboard; 24 of 70 commands could not be bound; duplicates silently disabled one command |
| Modal dialogs | plain overlays | every `Modal` is a WAI-ARIA dialog: `role="dialog"`, `aria-modal`, named by its title, focus moves in, Tab wraps (left to Monaco inside an editor), focus returns to the opener on close, and an `onEscape` hook lets an inner layer (a search box) take Escape first | Screen readers did not know a dialog was open, and Ctrl+, from the editor left focus behind the overlay |
| Unlimited row limit spill (§7) | spill to a temp Arrow file past 512 MB | not built: "No limit" keeps every fetched row in memory, so Settings warns for no limit and for limits over 1,000,000 rows | Found in the 2026-10-30 Settings review (no spill code in the sidecar); the warning stands until the spill exists |
| Syntax colors (§12) | Monaco token colors generated from the `--q-syn-*` CSS variables; one Catppuccin-based palette | per-theme palettes and font styles as data in `src/editor/syntaxTheme.ts`, chosen in Settings; `--q-ident` and `--q-suggest-match` replace the UI's `--q-syn-*` aliases | Settings previews every theme at once and Monaco rules carry font styles, which CSS variables cannot; a syntax theme must not change UI chrome; the old palette fell below 4.5:1 on selected text (light) and merged keyword/type under protan simulation (ΔE 1.1) |
| Chart down-sampling (Phase C1) | LTTB, at most 10,000 points per line | LTTB to 1.5 points per pixel of plot width, 512–10,000 (the worker gets the count with each computation; a width change recomputes without re-sending batches) | Measured (headless Chrome, metric 34): the browser's raster of a stroked polyline grows with its segments — two 20,000-point lines held one frame 433 ms, two 2,800-point lines 17–50 ms. Points beyond the pixels draw nothing new |
| Line width of a dense line (Phase C1) | 2 px lines (mark spec) | a down-sampled line is 1 px; every other line 2 px | A down-sampled line fills its pixel columns either way. Measured on metric 34: 35–62 ms frames at 2 px (5 runs with legend, axes and cursor each turned off showed the series stroke alone causes it), 20–22 ms at 1 px; 0 frames > 32 ms after |
| Notebook tab (Phase C3) | a notebook panel | a query tab with `notebook: true` and its own Dockview panel (`src/app/NotebookTabPanel.tsx`), not a tool tab | One session per notebook, the top bar's connection / database pickers, Run / Cancel, the transaction rules on close and sidecar-restart handling all come from the query-tab path unchanged; tool tabs have none of them and are not persisted |
| Notebook recovery copy (Phase C3) | `<appDataDir>/querithm/notebooks/` | `<data dir>/querithm/scripts/<tab>.sqlbook`, beside the query tabs' scripts | The capability may remove files under `$DATA/querithm/scripts/*` only; a second folder would need a new `fs:allow-remove` scope (§13 rule 6) for no gain |
| Notebook size check on open (Phase C3) | file ≤ 20 MB checked before reading | checked on the text after reading (UTF-8 bytes); the browser picker checks the file's size first | The fs capability has no `stat`; reading a user-picked file is what Open already does |
| Saved notebook outputs (Phase C3) | ≤ 1,000 rows × 50 columns per cell as text | the cell's first result set only, values cut at 4,096 characters | A file holding every result set of a multi-statement cell would be mostly data the notebook does not show; the cut keeps the file bounded |
| Markdown links in notebooks (Phase C3) | open via Tauri only on click | a click copies the address to the clipboard (toast); the webview never navigates | The shell has no URL opener; adding one is a new plugin and capability (§13 rule 6, §15 rule 10) for a convenience |
| Run all key (Phase C3) | Shift+F5 | Shift+F5 runs all while focus is in a notebook (`NOTEBOOK_KEYS`), stops the debugger elsewhere; F5 / Ctrl+Enter run the focused cell through the app's Run | Shift+F5 already belongs to the debugger; a key scoped to the notebook keeps both, as the grid and the Explorer own theirs |
| Notebook export tables (Phase C4) | results in the export | the first 100 rows of each SQL cell's first result set, then "First 100 of N rows" | An exported page is for reading; the CSV / Excel exports carry whole results |
| Cell image preview (Phase C2) | rendered via a blob URL | `<img>` with a `data:` URL whose MIME comes from the PNG/JPEG/GIF signature | The CSP's `img-src 'self' data:` has no `blob:`; widening the CSP buys nothing, since a `data:` image in `<img>` is just as inert |
| Open as plan (Phase C2) | a new synthetic execution, like the estimated plan | the parsed plan is added at the front of the cell's own execution (`batchIndex −1`, at most 5) and the bottom panel switches to Plan | Replacing the active execution would drop the grid and the viewer the plan was opened from; Results brings them back |
| Bottom panel ledger (2026-11-01) | one chip per statement, with statement numbers, line and time; "selection follows the fix" after a re-run; "Not run" chips | one chip per result set and per error, in arrival order, from what the webview already receives (no contract change); errors carry their editor line, result sets their index; no per-set time; the same chip index is kept after a re-run; no "Not run" | Statement identity needs the sidecar to tag every result set and message with its statement index and editor line (a `Querithm.Contract` change, and SQL Server statements inside one batch have no line today); until then a chip names what it can know without guessing |
| Bottom panel compare marks (2026-11-01) | a Δ glyph in each differing cell | the grid's existing per-cell tint (`tone: "changed"`) plus the footer's count of differing cells and the compared pane's caption | Glide draws cells on canvas; a glyph inside every cell needs a custom renderer, while the tint, the count and the caption already say it without color alone |
| Bottom panel type glyphs (2026-11-01) | a type glyph (#, Aa, ◷) in every column header | not added: headers keep their PK / FK / NULL markers and the type in the tooltip | The header's icon slot already carries the key and nullability markers; a second icon crowds a 100 px column |
| Bottom panel error notice in Messages (2026-11-01) | the notice line on every run view | Messages skips the error entry (its log lists the error itself); older-run and row-limit entries still show | The same text twice on one screen |
| Bench and smoke ports | 1420 / 1421 | `pnpm bench` honours `QUERITHM_UI_PORT` / `QUERITHM_BRIDGE_PORT` as the UI smoke does (default unchanged) | With a dev server on 1420 / 1421 the bench measured that server's webview and sidecar |
| Cancel escalation (2026-11-01) | `query.cancel` is a driver-level cancel (§7 step 4) | a run still going 5 s after Cancel has its connection closed on SQL Server (`IDbProvider.CloseAbortsCommand`), is reported in Messages at 10 s (cleanup or driver call, said as it is; stuck for good at 70 s, 8a review), and the webview offers Restart engine at 15 s | A server that never acknowledges the TDS Attention held the run — and its tab, session lock and pinned connection — forever at the default command timeout of 0 (§16 "Monitor command timeout" saw the same on the monitor). Measured on the fixture: SqlClient's Close returns in 24 ms and fails the parked call in 36 ms; Npgsql's Close blocked over 25 s (it drains the command) and MySqlConnector's left a NullReferenceException in the pending call, so those rely on their drivers' CancellationTimeout instead |
| Data plane without a reader (2026-11-01) | sidecar writes await the reader (backpressure, §5) | a frame waits at most 5 s for a reader to attach, then fails the run with the reason (`dataPlane.state` tells the webview); the end-of-execution frame is bounded at 10 s (35 s since the 8a review, past the shell's `STALE_CONSUMER`; a cancelled run's 5 s, §5); the shell connects for 60 s (was 15 s) and reconnects a closed pipe | A dev sidecar that took 18 s to listen left the bridge without a data plane: every run's end frame waited forever with no token, so the tab spun and Cancel did nothing (seen 2026-11-01, memory dump of the stuck sidecar) |
| Frames never torn on the data plane (2026-11-02) | frame writes carry the run's token; the shell reads any `u32` length | the run's token decides only whether a frame starts; a started frame gets 3 s after a cancel (`DataPlaneServer.FrameGrace`), then — like any write that fails part-way — the client is dropped and the shell reconnects (end-of-run frames wait 35 s first, past the shell's `STALE_CONSUMER`, 8a review); a frame's length is capped at 256 MiB on both sides (`FrameLimits.MaxLength`, `MAX_FRAME_LEN`): the sidecar fails a larger one before writing it, the shell treats one as lost framing (no allocation, reconnect) | A cancel while the shell applied backpressure left part of a 4 MB frame on the shared socket (the 2026-11-02 audit's scratch run received two whole frames and 255,831 bytes of a third, still connected; `CancelRecoveryTests` reproduces it): every later frame was misread and the shell could allocate up to 4 GiB from a bogus length (`panic = "abort"`). The cap leaves 64× the 4 MB flush for the last row's capped cells |
| Undrained result queues (2026-11-02) | Rust pauses the socket while one execution's queue is full | a full queue whose consumer has not polled for 30 s is dropped until its end (`STALE_CONSUMER`); `results_next` answers a dropped or ended run with the shell-made frame `0xFE`; `results_discard` after a run's end leaves no entry; the webview discards on every consumer that goes before its end (closed compare / preview, disposed run, a frame that does not decode, the import wizard's DDL) and a compare's producer stops with its execution | Closing a running Data Compare stopped polling but never discarded, and its producer wrote with a token `query.close` never cancelled: its 8-batch queue filled, the shell's only socket reader waited on it, and no tab got results until the engine restarted. 30 s is past the 20 s long poll, so a reading consumer is never cut off |
| Result streams after a restart (2026-11-02) | — | the webview keeps every poll loop by execution id; disposing a run stops its loop, an engine restart stops all of them and marks runs still going failed ("outcome unknown"), and a loop open 30 s after its `query.done` is stopped | Loops for disposed runs, closed tabs, cleared notebook cells and runs of a dead engine polled every 20 s forever and kept their rows; a DML statement in flight at the crash was shown as still running, never as of unknown outcome |
| macOS builds paused (2026-11-02), resumed (2026-11-03) | release.yml builds and publishes osx-x64 and osx-arm64; CI tests and smoke-tests the sidecar on macOS | Paused 2026-11-02 (macOS runners cost too much on a private repository); **resumed with the fresh start**: CI and release build macOS again. GitHub-hosted runners are free only while the repository is public; a CI or release run while it is private bills macOS minutes at the macOS rate | Owner decision |
| plan.get (2026-11-03, H1 / M5) | `plan.get {sessionId, sql, mode}` → `{plan, raw}`; actual plans checked only `confirmedRun`; PostgreSQL wrapped `BEGIN`/`ROLLBACK` itself | `{executionId}` or `{blocked}`: guarded like `query.execute` (estimated plans included), run as one execution (plan as `query.plan`, Cancel and the escalation end it); Core wraps an actual plan in a transaction it rolls back, a savepoint inside an open one; refused with the reason where that cannot hold (DuckDB inside an open transaction: no savepoints); the undo covers the BEGIN itself, so a cancel landing during BEGIN still rolls back (a SAVEPOINT counts only once it exists: rolling back to one never made would abort a PostgreSQL transaction) (8a review) | An actual plan executes caller SQL: DuckDB's committed in autocommit while the dialog promised a rollback, PostgreSQL's ROLLBACK discarded the tab's open transaction, and neither had Prod / destructive guards or a way to cancel |
| SQL Server actual plan through plan.get (2026-11-03) | `SET STATISTICS XML ON` via plan.get | refused with the reason ("turn on Include actual plan … and execute the query"); the actual plan comes with `query.execute` (`actualPlan`), the only path the UI ever used; the estimated plan refuses text that sets SHOWPLAN or STATISTICS XML / PROFILE | No caller needed it (UI, notebook and bench use the run; one integration test moved to the run), and SQL Server cannot undo every statement (DDL outside a transaction, XACT_ABORT dooming the user's) — refusing is safer than a second guarded path |
| RPC deadlines (2026-11-03, M5) | handlers ran on `CancellationToken.None`, "bounded by the driver's timeouts" | interactive server RPCs end with `timeout` after 60 s (`InteractiveDeadline`), long reads without a cancel RPC after 30 min (`LongDeadline`); cleanup after a cancel has `Cleanup.Limit` (60 s); `ArchitectureRulesTests` lists the handlers with their own bound — `tx.commit` / `tx.rollback` among them (8a review: `TxEndLimit` 30 min plus the session's close; a deadline-cut COMMIT said "try again" over an unknown outcome, and a ROLLBACK over 60 s failed while it still held the session) | The command timeout defaults to 0 (none) on SQL Server, PostgreSQL and MySQL, so a locked object or a silent server held the call and its pooled connection for good |
| Execution and Session lifetimes (2026-11-03, 8a review) | every Execution and Session `IAsyncDisposable` (§13 rule 9, CLAUDE.md §4) | neither type is: `ExecutionService` and `SessionManager` own their resources and are `IAsyncDisposable`. An `Execution` is the record of one run (token, the connection it holds under its gate, result-set records grid edits resolve against, cell buffer) and outlives the run until query.close, its tab's close or the watchdog; the run's `finally` returns or discards its connection, releases the session lock and sends `query.done`, and `Close` cancels it and clears its buffer. A `Session` holds no connection of its own unless pinned or session-owned; `CloseAsync` cancels its work, waits for its lock (bounded) and discards that connection; the services' `DisposeAsync` cancels every run and session and closes the pools | Disposing either object outside its service would race the run's own `finally` (a connection closed under a command) or the close's lock; one owner per resource, each path ending in that owner's `finally` / `CloseAsync`, is what the rule asks for |
| Reads on the pinned connection (2026-11-03, 8a review) | catalog reads and row tokens ran on the user's pinned connection unguarded; a deadline cancel there ran as a plain statement | inside `SAVEPOINT querithm_read` … `RELEASE` on PostgreSQL (rolled back to on failure or cancel), the state re-read after a stopped read on every engine, `txLost` when the transaction ended; one extra round trip per guarded read (SAVEPOINT, RELEASE), only while a transaction is open | On PostgreSQL the deadline's cancel (57014) aborted the user's transaction (25P02 on every later statement) with nothing said; on SQL Server with XACT_ABORT ON it rolled the transaction back |
| Closing the window (2026-11-03, H4) | `RunEvent::Exit` sends `system.shutdown` and the sidecar rolls back; nothing asks | the shell holds the main window's close request (`CloseRequested`, and `ExitRequested` without a code while the window is open) and the webview settles every tab as tab close does, confirms unsaved work and writes what waits on its debounce, then answers; the window closes anyway after 3 s without an acknowledgement or on a second request while no dialog shows; two new app commands (`app_close_ack`, `app_close_answer`), **no new capability or permission** (no `core:window:*`: the webview never closes the window itself); an update install allows the close before it restarts | Quitting dropped open transactions, staged grid edits and the last 2 s of typed SQL without a prompt; a hung or crashed webview must never keep the window open |
| Unknown transaction state (2026-11-03, M9) | "unknown" handled like "none" by tab close, bulk close, disconnect, the restart banner, the update dialog and `session.close`; MySQL read an empty performance_schema table as "none" | unknown counts as open in every prompt and at `session.close` (refused unless forced); a successful COMMIT / ROLLBACK that leaves the state unreadable discards the pinned connection; MySQL probes its transaction instrumentation once per connection and, without it, reads the state with a savepoint probe (the audit asked for "unknown" there: the probe can tell, and "unknown" would pin every tab and refuse every grid edit on servers with performance_schema off), unknown only with autocommit off or when neither can be read. Round trips per state read (after each run, and a pool return whose state is not known): MariaDB one (`@@in_transaction`); MySQL instrumented one (its transaction event) plus the instrumentation probe once per connection; MySQL uninstrumented two — `SELECT @@autocommit`, then `SAVEPOINT …; RELEASE SAVEPOINT …` as one command (8a review: was three; autocommit off answers unknown after the first, without a savepoint that would open a transaction). `MySqlTxProbeIntegrationTests` turns the `events_transactions_current` consumer off on MySQL 8.4 and proves open / none both ways | A connection holding a live transaction could go back to the pool, and closing a tab could silently roll back possibly open work |
| Grid Apply cancel and double apply (2026-11-03, H5 / H6 / L15) | a cancelled Apply skipped its rollback; the COMMIT ran on the run's token; no in-flight guard | rollback on every exit before the commit, connection closed when the rollback cannot run; COMMIT under `Cleanup.Limit`; `outcomeUnknown` / `applyFailed` / `cancelled` say what was written; one Apply per staging in flight; DuckDB refuses inside an open transaction up front. No patch id for the sidecar to de-duplicate (the in-flight guard and `outcomeUnknown` cover the cases found; a second webview would need it) | The "atomic" Apply could leave half a patch in a transaction the user did not open, and a retry or a second Ctrl+S duplicated inserts |
| Cancelled after completion (2026-11-03, M10) | Cancelled decided from the token at the end of the run, its first error dropped | Cancelled only when the cancel stopped work still to do (requested while a statement ran — even one the driver then returns normally, MySQL `KILL QUERY` on `SLEEP` — or before a later one); a run that stopped on an internal timeout that is not the user's cancel is Failed | Work that ran (and auto-committed) was reported as cancelled |
| Prod first-run confirm (2026-11-03, L1) | per profile per launch, from the tab's profile snapshot | per target (stored environment, engine, host, port, file, default database, the tab's database); Prod is the stricter of the stored profile and the tab's snapshot; the stored profile the guard reads is kept in memory and dropped on every save / delete (`ProfileStore.Changed`, an epoch so a racing read never keeps a stale copy), so `query.execute` reads no store.db on the RPC thread (8a review: it read it 1–3 times per run) | An edit to Prod did not guard open tabs, and a confirm given for one host survived a change to another |
| Native library extraction (2026-11-03, H7) | `DOTNET_BUNDLE_EXTRACT_BASE_DIR` = `<data dir>/querithm/native` (Phase D3); webview fs write / mkdir on `$DATA/querithm/**` | extracted under `<app local data dir>/native/<version>-<sidecar size>-<mtime>` (Windows `%LOCALAPPDATA%`, so nothing roams); the webview's fs scopes name only its own files (§13 rule 6). At startup, off the startup path, the shell removes the build folders it marked (`.querithm-native`) whose marker is a day old and not locked — each shell holds a shared lock on its build's marker for its life and the sweep takes it exclusively first, so a build still running elsewhere keeps its folder however long it runs (R1-2; the age alone decides where the file system cannot lock) — and `<data dir>/querithm/native` (a real directory only) a week after a newer shell first saw it (`<app local data dir>/native/.legacy-seen`), and only when it can first be renamed away (Windows refuses while a running 0.2.0 engine has a library in it loaded; 0.2.0 takes no lock and loads DuckDB lazily); symlinks are never followed | Script in the webview could replace a native library the next sidecar loads (persistent native code) or plant `known_hosts` entries; Roaming profiles copied the libraries around |
| Control-plane member names (2026-11-03, M1) | `JsonSerializerDefaults.Web` (case-insensitive) for every JSON the sidecar reads | the RPC formatter binds names exactly (`Json.CreateRpc`); stored settings, `store.db` columns and `.qschema.json` files keep the case-insensitive reader (`Json.Create`). Unknown members are still ignored, not refused (`UnmappedMemberHandling` stays default): the shell refuses the dangerous names itself, and refusing every unknown member would fail any caller that sends an extra field (bench scripts, older UI state spread into params) without a gain | The shell strips and injects by exact name; a case-insensitive bind let `FilePath` from the webview reach the import |
| SSH host-key trust (2026-11-03, M2) | `ssh.trustHostKey` records the key it is given | only a key a bastion presented to this sidecar in the last 15 minutes (`PresentedHostKeys`, 32 entries, exact host / port / algorithm / fingerprint, used once) | The webview could pre-trust an attacker's bastion key. `profile.test` still reuses the saved password for an edited, unsaved profile (owner decision 2026-11-03) |
| Dialog Enter key (2026-11-03, M19) | Enter confirms the dialog | Enter on a focused button, link, select, checkbox, option or tab activates that control; danger dialogs, the update dialog and Execute procedure are `alertdialog`s that open on Cancel (Later) and confirm with Enter only from a text field | Enter on a focused Cancel confirmed, and a danger dialog opened on itself, so Enter right after it opened disconnected, deleted or installed |
| Data-plane socket (2026-11-03, L3) | `$XDG_RUNTIME_DIR/querithm-<pid>.sock` (macOS `$TMPDIR`), `/tmp` when unset; the client sends the token to whatever listens | a fresh 0700 folder `querithm-<random>` of the shell's own (in `$XDG_RUNTIME_DIR` only when that is the user's private folder, else in the temp folder; a link, another user's or a group/other-open folder is never used; the path stays under 100 bytes), removed at exit; the token goes only to a peer with this uid and the child's pid (`peer_cred`), on Windows to the pipe server with the child's pid (`GetNamedPipeServerProcessId`; windows-sys feature `Win32_System_Pipes` added, no new crate), and the pipe name has a random part | A predictable path in `/tmp` let another local user bind it first and receive the launch token. The sidecar still deletes and binds the path it is given (the harnesses pass their own) |
| Profile delete (2026-11-03, L2) | sidecar forgets secrets and pools; the webview deletes keyring entries afterwards, errors ignored; sessions stay open | the sidecar closes the profile's sessions (all or none, `profileBusy` while one has an open or unknown transaction or a running query) and the UI settles each tab first as tab close does; the shell deletes `<id>`, `<id>:timed`, `<id>:ssh`, `<id>:ssh:timed` and the memory cache after the delete and reports a failure (`secretsNotDeleted`); the webview deletes no keyring entry itself | A deleted profile kept working sessions, and a keyring the webview could not reach kept its password silently |
| Secrets in kept query text (2026-11-03, L4) | history keeps the statement as typed | the engine's tokens decide which string literals are secrets (secret-named words and variables — PASSWORD, OLD_PASSWORD, SECRET, @rmtpassword, PWD … — with or without `=`; MySQL / MariaDB IDENTIFIED BY / AS / USING, REPLACE, SET PASSWORD; DuckDB KEY_ID, TOKEN, SESSION_TOKEN, CONNECTION_STRING; PostgreSQL CONNECTION; SQLite PRAGMA key / ATTACH … KEY; the passphrase / password functions and SQL Server's security procedures) and mask them as `<redacted>` (8a review: was `'***'`, a runnable literal) in history and the monitor's text store; the history's error drops them too; the v5 store step re-masks rows an older build kept (its pre-upgrade `.bak` keeps them). A text with none of the trigger fragments is not tokenized | A password typed in ALTER LOGIN sat in `store.db` and the monitor. Saved scripts and autosave copies are the user's own text and stay as typed |
| Data folder permissions (2026-11-03, L4) | default umask | Unix: `<data dir>/querithm` and `logs/` 0700, `store.db` (+ -wal / -shm) and log files 0600, narrowed at start; Windows unchanged (the profile folder's ACL) | History, snapshots and logs are user data |
| Names in generated comments (2026-11-03, L6) | names interpolated as is (some with `\n` replaced) | `CommentText.Safe` for every name, label or file name inside `--` or `/* */` of a migration, sync, refactor, grid-change, import or scripting output: CR, LF, NEL, U+2028/2029 and other controls become spaces, `*/` and `/*` are split. PostgreSQL / DuckDB tokens end a line comment at CR as the servers do | A table named `x⏎DROP TABLE …` turned the rest of its name into a statement of the script |
| Literals in previews and scripts (2026-11-03, L7) | PostgreSQL `'…'`; SQL Server numbers checked with `double.TryParse` | PostgreSQL text with a backslash is written `E'…'` with backslashes doubled (one reading whatever `standard_conforming_strings` is); SQL Server writes a number bare only in a strict ASCII grammar (exponent for float / real only; for decimal / numeric / money an exponent form such as `1E5` is written out exactly as the plain number, digit by digit, and the apply's parameter reads it the same — 8a review: `'1E5'` failed as varchar to numeric), anything else — NaN, Infinity, other digits — quoted; MySQL (hex for a backslash), SQLite and DuckDB already met both | A backslash ended the literal early on a server with `standard_conforming_strings=off`; NaN went bare into the script |
| Rename to a keyword (2026-11-03, L9) | SQL Server / PostgreSQL left a regular new name bare | SQL Server brackets a name ScriptDom's lexer does not read as a plain identifier (reserved keywords); PostgreSQL quotes reserved, type/function-name and column-name keywords and any name not lower case; MySQL already did; SQLite and DuckDB have no rename (no reference finder) | `user`, `order`, `select` came out bare and broke the rewritten definitions |
| Bench hooks and engine override in release (2026-11-03, L10) | `bench.synthetic` always registered; `?bench` honoured in the shell; `QUERITHM_SIDECAR` honoured by every shell | `bench.*` is registered only when the sidecar starts with `QUERITHM_BENCH=1` (set by `scripts/bench/run.mjs`, the dev bridge under `pnpm bench` / smoke, and the app bench); `?bench` works in the browser only; `QUERITHM_SIDECAR` only in a debug shell (the harnesses use it for the dev bridge, never for the release shell). Hover markdown escapes server names and is untrusted, without HTML | A release run answered a million-row synthetic stream to any caller, and could be pointed at another engine binary that receives every injected secret |
| Npgsql data sources (2026-11-03, L19) | one per connection string (password included), kept forever | keyed by profile id and the connection string without its password (8a review: the profile joined the key, so two profiles with the same settings never replace each other's entry); the password is not in the source at all — a password provider sends the entry's current one at each open, so a changed password or a refreshed Entra token (new every time) updates the entry instead of building a source per token; `IDbProvider.ForgetConnections` drops a profile's sources with its pools (disconnect, delete, a changed secret or setting); at most 64, least recently used first; a dropped source is disposed only when its last connection closes (a lease per connection, released on close or a failed open — disposing it under an open connection failed that connection's next command) | Every password ever typed stayed in a dictionary key for the process's life |
| Render errors (2026-11-03, M17) | none: one render exception blanked the window | an error boundary per Dockview panel and tab header, per bottom-panel view and around the Explorer, plus a root boundary; reports go to the sidecar log through `system.reportError` (quoted text masked, fields capped, 20 per 10 minutes per window, 50 logged per sidecar) | One panel's bug no longer takes the app down, and it leaves a trace on the user's machine |
| Engine that does not start (2026-11-03, M18) | `sidecar::start(...)?` in setup: the app aborted with no message | the window opens; the supervisor keeps `failed` and the reason (`sidecar_status.detail`); the boot screen shows it with **Restart engine** instead of the in-app banner, since without the engine there are no profiles or settings to show a shell around; a mid-session failure keeps the banner. A panic hook writes a crash record to the shell log | An antivirus quarantine of the unsigned sidecar made the app vanish silently |
| Logs (2026-11-03, M21) | one `sidecar-YYYYMMDD.log`, the newest 7 kept, decided at start, no size cap; relayed sidecar stderr to `eprintln!` only | sidecar `sidecar-YYYYMMDD-<pid>.log` (per process) and shell `shell-YYYYMMDD.log` (shared, O_APPEND), 5 MB parts, files of the last 7 days, at most 14 per kind; release sidecars run with `QUERITHM_LOG_STDERR=0`; crash handlers in both processes; `system.logTail` and Settings → About "Copy diagnostics" | Two instances overwrote each other's lines in the shared sidecar file (.NET tracks its own write offset); Windows release builds discarded stderr; a log storm had no bound |
| Damaged or old store (2026-11-03, M22) | an unreadable `store.db` failed every start; no copy before a migration | `LocalStore.OpenOrRecover` sets a damaged file aside and starts fresh (told once via `system.notices`); `store.db.v<N>.bak` before a migration (newest 2); `BEGIN IMMEDIATE` for the migration; quick_check only after a failed open (52 ms on a 29 MB store, warm cache) | Profiles and history were unreachable with no way out; an upgrade had no way back |
| Toast timing and screen readers (2026-11-03, M20) | toasts dismissed after 4 s (8 s for errors), a click on the toast closed it, no live region | the Announcer reads toasts (assertive for errors and warnings) and new banners; 5 / 10 / 15 s plus 50 ms a character past 80, at most 30 s, paused while hovered or focused; a Dismiss button; identical toasts joined (§12) | WCAG 2.2.1 (timing adjustable) and 4.1.3 (status messages): errors vanished before a slow reader finished them and were never heard |
| Profile address check (2026-11-03, L25) | any port and host reached the driver; the editor's Host and Port had no label; errors were toasts | `ProfileRules.Check` refuses a port outside 1–65535 and a host (or SSH host) longer than 1,024 characters or with control characters, for every server engine, at `profile.save` (`invalidProfile` with `data.field`), `profile.test` and `session.open`; the editor labels Host and Port and shows each problem at its field; an empty host is sent as none, which every server provider takes as `localhost` (W9: the editor had required one, so a profile saved without a host could not be saved or tested again) | A port of 0 or 70000 failed only at connect time (and a negative one became a huge `uint` for MySQL); an empty port is still the engine's default and an empty host the driver's |
| Interface scale (2026-11-03, L26) | no UI zoom; `zoomHotkeysEnabled` off | the webview's page zoom from a setting (90–150 %) and Ctrl+= / Ctrl+- / Ctrl+0; capability `core:webview:allow-set-webview-zoom` added (the only permission beyond `core:default` and the dialog / fs set); on WebKit `devicePixelRatio` is reported times the scale | `zoomHotkeysEnabled` injects a window-wide Ctrl+0 / Ctrl+wheel listener that would reset the zoom where the grid's Ctrl+0 sets NULL and keeps no saved level; CSS `zoom` on the root breaks popup positions (Chromium) or pointer coordinates (WebKit). Gaps in §12 |
| Restart banner wording (2026-11-03, L32) | "Connections were restored; nothing was lost." whenever no transaction or in-memory database was lost | the banner also names interrupted tool runs, stopped traces and debugging sessions, and says connected tabs get a new session (temporary tables and session settings gone); "no work was lost" only when no tab had a session or a running query | A killed statement, a temp table or a running trace was lost while the banner said nothing was |
| Product description (2026-11-03, L32) | "Lightweight SQL IDE for SQL Server and PostgreSQL" | "SQL IDE for SQL Server, PostgreSQL, MySQL / MariaDB, SQLite and DuckDB" in `tauri.conf.json`, `package.json` and `Cargo.toml` | Three more engines ship, and "lightweight" had no measurement behind it (CLAUDE.md §4) |
| Autosave and two windows (2026-11-03, M23) | `workspace.json` and scripts written in place, errors ignored; a second window restored and rewrote the same tabs | temp file + rename (`fs:allow-rename` on `$DATA/querithm/scripts/*`), `workspace.json.bak`, "Recover N unsaved scripts" (`fs:allow-read-dir` on `$DATA/querithm/scripts` only), a rate-limited warning; the first instance owns the workspace (`instance.lock`, std `File::try_lock`, hence `rust-version` 1.89) and a later one starts with a new tab — no single-instance plugin (owner decision: several instances stay allowed) | A crash mid-write or a parse failure silently emptied the restore; two windows overwrote each other's tabs and scripts |
| Server messages (2026-11-03, M13) | one `query.message` notification per server message into an unbounded queue; the webview copied the whole log per message (to its 20,000 cap) and rescanned it per error | `query.messages` batches (at once after a quiet 100 ms, else every 100 ms or 500 messages, flushed before each result set, plan and end); 20,000 server messages per run, then one "N more … were not shown" line; the host's queue bounded at 10,000 (detail kinds dropped past it, lifecycle never); the webview appends in place and marks errors once per batch (§5) | A PRINT / RAISERROR loop or thousands of failing statements cost one notification and one O(n) copy each (O(n²) per run); §14 #36 measures the flood |
| Stdin writer (2026-11-03, M8) | each RPC wrote its line to the sidecar's stdin under a `std::sync::Mutex` inside the async command | a writer thread fed by a bounded queue; a line not written within 10 s is failed, a write stuck 10 s refuses new calls, and the ping shares the queue (§4 step 3) | A sidecar that stopped draining stdin with more than a pipe's 64 KB pending blocked tokio workers in `write_all`, so the health check could not run and every other command starved. Per call this adds one channel send and a thread wake-up on the typing / completion path (§14 #5) |
| Integration tests without the fixture (2026-11-03, M16) | gated tests began with `if (!TestHost.IntegrationEnabled) return;` and reported Passed | `[IntegrationFact]` / `[IntegrationTheory]` (`sidecar/Querithm.Tests/IntegrationAttributes.cs`) set `Skip` unless `QUERITHM_IT=1` (plus `QUERITHM_IT_LONG` / `_DISRUPTIVE`, `QUERITHM_PERF`, `QUERITHM_SOAK_MINUTES` where a test needs them, `LinuxOnly` for container addresses); `MonitorBench` uses `[OptInTheory("QUERITHM_BENCH_MONITOR")]`. `dotnet test` without the fixture reports 256 skipped entries — the 370 cases that used to pass without running (a skipped theory is one entry) — and no test that ran is skipped | A skipped test that reads as passed overstates what was checked; xunit 2.9.3 has no dynamic skip and no package is added |
| Fixture credentials (2026-11-03, L33) | the fixture passwords repeated in the bench, smoke and loader scripts, `TestSupport.cs` and the SSH tests | only in `scripts/bench/docker`: `compose.yml` (the engines' passwords; the bastion's as the `ssh` service's build arg `SSH_PASSWORD`, which `sshd.Dockerfile` takes as `ARG`) and `ssh/test_rsa_pass.passphrase`; JS reads them with `scripts/fixture/fixture-secrets.mjs`, the tests with `FixtureSecrets` (or `QUERITHM_IT_<MSSQL\|PG\|MYSQL\|SSH>_PASSWORD`); `FixtureSecretTests` fails if a value appears anywhere else in the repository | CLAUDE.md §3: only the documented fixture files may hold them |
| DuckDB open gate (2026-11-03, L20) | one process-wide gate around every DuckDB open, configure and audit | a gate per file entry: opens of one file wait for each other (the first configures and locks the instance the next inherits), other files and `:memory:` (a database per connection, no shared DuckDB.NET instance) open meanwhile; the entry is reserved under a short lock before the open, so the one-mode-per-file and case-variant refusals hold for opens in flight | A slow open (a large file's audit, a slow disk) of one file held every DuckDB tab, in-memory ones included (CLAUDE.md: no lock across a driver call it does not need) |
| Data compare sizes (2026-11-03, L21) | diff batches of 1,000 rows whatever their size; up to 4 kept comparisons × up to 1,000,000 kept differences | a batch flushes at 1,000 rows or 4 MB (`ResultSetEncoder.MaxBatchBytes`, as query batches); each comparison keeps differences up to 64 MB (estimated managed bytes, `DefaultKeptBytes`, 256 MB for the 4 kept) or Keep, whichever first; the rest are counted, the progress says which limit stopped it (`kept`, `truncatedNote`) and the sync script covers what was kept | One compare of long values could reach the 256 MiB frame cap, and 4 × 1M kept rows of text could hold gigabytes |
| Import commit outcome (2026-11-03) | a COMMIT of the import's own that failed was reported like any error | a COMMIT cut short reports `errorCode: "outcomeUnknown"` and the wizard refuses a blind retry into that table (§7 Import); a COMMIT that found its transaction already rolled back (SQLite / DuckDB `NoTransactionOpenException`, 8a review) is a known "nothing was written"; `LoaderCommitFileEngineTests` covers both native loaders' commit paths | A retry after a commit that did go through loads the rows twice |
| Import limits (2026-11-03, L8) | profiling read whatever the file held (whole shared strings, an unterminated quote to the end of the file, a profiler per JSON key with every earlier row replayed) | the caps in §7 Import, each refused with the limit named (8a review: 64 Mi characters per value and 128 Mi per record instead of the grid's 1 Mi and 64 Mi; shared strings sized to memory, 128–512 MB, with CSV as the way out; tests scale the limits down per flow, `ImportLimits.Scale`) | One crafted file could exhaust the sidecar's memory during inspection |
| History retention and previews (2026-11-03, L17) | history kept forever; list and search returned whole texts | 90 days / 50,000 entries by default (§8, history dialog), passes in the background; previews of 500 characters, `history.get` for one whole text; store v5; the first pass that deletes entries says so once, naming the setting (`historyPruned` notice, `system.noticeAdded`, contract 1.18.0, 8a review) | The store grew without bound and the history dialog received up to 300 whole scripts per keystroke |
| Keyring cache lock (2026-11-03, L22) | one async mutex over the credential cache, held across the OS keyring call | a slot per account: the map's lock is held only to find a slot; a slot's lock is held across that account's keyring calls, so two callers of one account share one read (one unlock prompt) and other accounts never wait for it | An unlock prompt left open held every connection's credential lookup |
| Monaco editors (2026-11-03, L23) | one shared Monaco instance, `setModel` on tab switch | one shared editor per visible Dockview group (normally one), `setModel` on tab switch; models per tab, disposed on close (§12) | A split shows two tabs at once; each visible group needs its own editor, still never one per tab |
| Private Monaco API (2026-11-03, L23) | — | the one internal read (`CodeEditorWidget._contextKeyService`, key `suggestWidgetVisible`: Monaco 0.52 has no public way to ask whether the suggest widget is open) lives in `src/editor/monacoInternals.ts`, answers false when it is missing, and its test fails on the bundled Monaco sources when a rename lands | Tier-2 completion merges late items into an open list; an upgrade must not break it silently |
| Trim warnings (2026-11-03, L23) | `SuppressTrimAnalysisWarnings=true` hid every trim warning, new ones included | still suppressed in the build (SMO, SqlClient, StreamJsonRpc warn and are rooted, TrimMode=partial unchanged); CI's linux-x64 release-smoke publishes once with them shown and fails when there are more than `scripts/trim-warnings-baseline.json` lists (45 on 2026-11-03, positions and machine paths normalised; `node scripts/trim-warnings.mjs --update` after a justified change) | Failing on all existing warnings is not possible without replacing those libraries; a count that may only fall keeps regressions visible |
| Dev bridge relay (2026-11-03) | `scripts/dev-bridge.mjs` buffered frames of any length, paused at 8 batches with no stale guard, never answered `0xFE`, left an entry on discard after the end | `scripts/dev-bridge-relay.mjs` with `ipc.rs`'s constants and rules (§5); its test checks the constants against `ipc.rs` | The UI smoke and the browser dev mode ran a relay the app does not have |
| Quitting never dead-ends (2026-11-03, W1 / W4 / R1-8) | a failed COMMIT / ROLLBACK, or an engine that was down, kept the window open at every close; a query that did not stop in 10 s quit without the staged-edits prompt; a close during an update install exited mid-install | with the engine `dead` / `failed` no transaction prompt (the tabs' sessions are dropped and the loss named on `failed`); a failed COMMIT / ROLLBACK asks **Quit anyway (the server rolls back)**; staged grid edits of a query that will not stop are named before they go; a close while an update installs asks first (Keep waiting / Quit anyway) — in the webview, so `src-tauri` gains nothing | Only killing the process got out of the first; the others lost work or could leave a half-written AppImage |
| Interface scale at start (2026-11-03, W8) | the setting (store.db, through the engine) applied once loaded | the scale last applied is also kept in the webview's `localStorage` (`querithm.uiScale`, one number, not user data) and applied before the first render; the setting stays the source of truth. Known gap: under WebKit the patched `devicePixelRatio` and `(resolution)` media queries disagree at a scale other than 100 %, so Monaco misses a move to a monitor with another ratio until reload (minimap and overview ruler only) | The window jumped from 100 % to the saved scale once the engine answered; making the two agree would mean patching `matchMedia` |
| Second windows' recovery copies (2026-11-03, W5) | the owner offered every copy no saved tab named | a second window keeps `scripts/<window>.live` fresh (60 s; listed before a copy is written) and removes it when it quits; the owner skips copies a beat younger than 3 min lists and removes older beats | The next owner offered a running second window's copies as orphans and Recover deleted them; a subfolder would need a new fs scope, a beat file in the same folder needs none |
| Engine stdin deadline at start (2026-11-03, R1-1) | the 10 s write deadline and the stall refusal from the first request | armed by the engine's first reply | A first start that extracts natives or is scanned took over 15 s: restored tabs' requests failed "not sent" and later calls `sidecarDown` while the status said running |
| Fresh start (2026-11-03) | one repository and version line from the first commit | **Owner decision:** the code moved to a new repository with a single starting commit and the version restarts at 0.1.0, with a new updater signing key; the earlier private line (0.1.0–0.2.0) is retired and its releases were withdrawn. Version numbers before this row ("since 0.2.0", "builds up to 0.2.0") refer to that earlier line. Earlier installs do not update to the new line (different key, lower version): they are uninstalled and the new build installed | Publish the source without the earlier history; the repository switches between public and private, so the controls in §13 must hold in both states |
| minimatch advisory (2026-11-04) | no known-vulnerable package | `minimatch` is forced to 11.x by a pnpm override (`pnpm-workspace.yaml`) although linaria (glide-data-grid → linaria) asks for ^9: GHSA-g3hv-75wg-m249 (moderate, glob-pattern amplification) is fixed only in 11.0.1. linaria calls only `new Minimatch(pattern).match(path)`, which 11.x keeps (CommonJS build included); it runs at build time and is not in the webview bundle | Revisit, and drop the override, when glide-data-grid moves to a linaria that asks for 11.x |
| glib advisory (2026-11-04) | no known-vulnerable package | `glib` 0.18.5 (GHSA for `VariantStrIter` `Iterator` / `DoubleEndedIterator` unsoundness, moderate; fixed in 0.20) stays: tauri 2.12 / wry 0.57 / tao / muda pin `gtk` ^0.18 (Linux GTK3), and glib 0.20 does not link with it. Checked: no crate in `src-tauri`'s dependency tree calls `Variant::array_iter_str` / `VariantStrIter` (the only path to the unsound code), and Querithm's Rust code does not use glib | Upgrade when tauri / wry move past gtk 0.18; until then the alert is not reachable |
| Grid-edit audit (2026-11-04) | editability from driver / AST column origins and the cached keys alone | (1) result indices live on the execution, so a SQL Server batch that fails after streaming a set never hands its index to the next GO batch; (2) `IDbProvider.ResultEditRefusal` names shapes whose origins cannot name the row shown — a table read twice (self-join, subquery, CTE) or a set operator — from ScriptDom (SQL Server) or the engine's tokens (`ResultShape`: PostgreSQL, SQLite, DuckDB; never a regular expression), and SQL Server refuses an unqualified name that exists in several schemas instead of assuming `dbo`; (3) a MySQL / MariaDB table whose engine has no transactions (`information_schema.ENGINES.TRANSACTIONS`) is read-only; (4) a partial / filtered, expression, disabled or invalid unique index is never a row key; (5) SQLite row checks send at most 500 keys per statement (`IDataEditDialect.MaxRelationRows`). The two catalog flags are `internal` properties of `SnapObject` / `IndexInfo` (Contract `InternalsVisibleTo` Core and Tests), persisted in store v6 | Each could make an edit update another row than the one shown, or report "nothing was written" after writing; the flags are sidecar-only, so the UI contract (`contract.ts`) is unchanged. The shape check is conservative: a table named twice anywhere in the statement refuses editing even where the outer rows would be exact |

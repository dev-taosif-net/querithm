# Local security audit — 2026-11-05

Scope: the dev working tree, including the connection/security fixes already in progress. Tests used fake providers,
local protocol fixtures, and hostile requests to the application's own RPC boundary. No external service was attacked.
This is a bounded audit, not a claim that every vulnerability has been found.

## Findings fixed

| Finding | Reproduction | Fix |
| --- | --- | --- |
| Saved credentials could follow a forged raw `profile.save` to a different endpoint | Store database and SSH secrets, call `profile.save` directly with an attacker host, then open the profile. The dev bridge injected both old secrets. | The native shell reads the previous profile from the trusted sidecar and retires credentials before forwarding endpoint changes. Saves, deletes and credential injection for one profile are serialized. The dev bridge mirrors these checks. |
| Stale dedicated connections and queued pool opens could send a new endpoint's password to the old host | Open a dedicated connection from an old profile snapshot after changing its host. Separately, wake a queued pool borrower and change the profile before its physical open. Reproduced for SQL Server, PostgreSQL and MySQL. | All remembered-credential opens capture credentials, validate their epoch and stored endpoint, and reject stale snapshots. Credentials are retired before a new endpoint is published. Monitor overrides are applied after validation. Existing pinned transactions keep their authenticated connection. |
| Fragmented malformed RPC IDs could terminate the control channel | Split a numeric request ID across `ReadOnlySequence` segments and send scalar `params`. The error formatter tried to write an empty raw ID and threw. Split string IDs lost the caller's ID. | Both numeric and string IDs are read from the complete token, including multi-segment values. Regression cases split numeric, string and escaped-string IDs at every interior boundary. |
| Public keyring commands could overwrite internal timed credential envelopes | Call `secret_set` with `victim:timed` and plain text. The account was accepted, corrupting the envelope the shell expects to deserialize. | Public accounts allow a 1–54 character ASCII profile ID with an optional single `:ssh` suffix. Internal `:timed` accounts, repeated suffixes, paths and control characters are rejected. Internal timed storage and migration still work. |
| PostgreSQL Unix socket paths lost their case during credential endpoint comparison | Compare `/tmp/ProdSocket` with `/tmp/prodsocket`, or differently cased abstract socket names. The checks treated them as the same endpoint. | Frontend, native shell, dev bridge and sidecar preserve case for filesystem/abstract socket names, including socket entries in host lists. DNS hostnames remain case insensitive. |

## Verification and limits

Regression tests live alongside the existing login/session, profile RPC fuzz, shell keyring, and dev-bridge suites.
Run the usual frontend typecheck/tests and `dotnet test sidecar --no-restore` to exercise them. The new Rust guard
module has its own unit tests, including endpoint binding, reserved accounts and per-profile concurrency.

The normal `cargo test --locked --offline --manifest-path src-tauri/Cargo.toml` build could not finish in this
environment because GTK development packages (`gdk-3.0.pc`) are missing. The actual credential guard module and
keyring logic passed a temporary headless Rust harness; the keyring copy omitted only Tauri command attributes and
used its existing test-only OS-vault implementation. This does not validate Tauri command wiring or real OS keyrings.
A complete native build and platform keyring tests remain necessary in an environment with the documented native
dependencies. The backend suite also skips tests requiring unavailable integration fixtures or published artifacts.

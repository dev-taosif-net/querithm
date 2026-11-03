# Security policy

## Supported versions

Querithm is pre-1.0. Security fixes land on `main` and ship in the next release; only the latest release is
supported.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately through GitHub's
[private vulnerability reporting](../../security/advisories/new) on this repository (Security tab → "Report a
vulnerability"). The repository is public only for limited periods, and reporting is open only while it is public.

Include what you can: affected version or commit, platform, steps to reproduce, and the impact you expect. You
should get an acknowledgement within a week; we will keep you updated while a fix is prepared and credit you in the
advisory unless you prefer otherwise.

## Scope

In scope: the desktop app (Tauri shell, UI, .NET sidecar), its IPC between those parts, credential storage, and the
release/installer pipeline.

Out of scope: the benchmark/test Docker fixture under `scripts/bench/docker/` (its credentials are deliberately
public, development-only values) and issues that require an already-compromised local user account.

The security rules the code follows are in [docs/ARCHITECTURE.md §13](docs/ARCHITECTURE.md#13-security-rules).

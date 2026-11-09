# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

> Status: Querithm has applied to the SignPath Foundation open-source program. Windows installers are signed from the
> first release after the application is approved; until then they are unsigned and Windows shows a SmartScreen prompt.

## What is signed

| Platform | Files | Signature |
| --- | --- | --- |
| Windows x64 | `Querithm-windows-x64.msi`, `Querithm-windows-x64-setup.exe` | Authenticode, through SignPath (certificate issued to SignPath Foundation) |
| All platforms | in-app update packages | minisign; the app accepts only updates signed with the key whose public half is in `src-tauri/tauri.conf.json` |

Only Querithm's own release artifacts are signed. Third-party components are shipped as their publishers released
them; they are listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## How a release is signed

1. A maintainer pushes a `vX.Y.Z` tag on `main`, after CI has passed on that commit.
2. The `release` workflow (`.github/workflows/release.yml`) builds every installer on GitHub-hosted runners from that tag.
   No installer is built or signed on a personal machine.
3. The unsigned Windows installers go to SignPath as a signing request from that workflow run. SignPath checks that
   the request comes from this repository's GitHub Actions and signs it under the release signing policy after an
   approver accepts the request.
4. The signed installers and their update signatures are published as a release of
   [querithm-releases](https://github.com/dev-taosif-net/querithm-releases).

## Team roles

| Role | Members |
| --- | --- |
| Committers and reviewers | [dev-taosif-net](https://github.com/dev-taosif-net) |
| Approvers | [dev-taosif-net](https://github.com/dev-taosif-net) |

Every member uses multi-factor authentication on GitHub and SignPath. Changes reach `main` only through the
maintainer; branch and tag rules block everyone else.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user
or the person installing or operating it. It connects only to:

- the database servers, SSH bastions and identity providers (Microsoft Entra ID) the user configures;
- `github.com/dev-taosif-net/querithm-releases`, to check for and download updates. The check runs shortly after start
  and every 6 hours; it sends no user data and can be turned off in Settings (automatic update check).

Query results, history, scripts and schema contents stay on the device. Passwords and keys stay in the operating
system's keyring.

## Reporting a problem

To report a signed file that looks wrong, or a security issue, use this repository's private vulnerability reporting
(Security → Report a vulnerability).

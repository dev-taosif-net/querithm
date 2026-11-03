#!/usr/bin/env bash
# Run once while online. Afterwards the app builds, tests and benchmarks with no network:
#   QUERITHM_OFFLINE=1 pnpm build     (pnpm --offline, dotnet --no-restore, cargo --offline)
# The running app never needs the network except to reach your databases (and Microsoft Entra, if used).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
echo "== pnpm store"
(cd "$root" && pnpm install --frozen-lockfile)
echo "== NuGet packages for every release RID (runtime packs, ILLink)"
dotnet restore "$root/sidecar/Querithm.Sidecar.slnx" -p:PublishTrimmed=true -v q --nologo
dotnet restore "$root/sidecar/Querithm.Host" -p:PublishTrimmed=true -v q --nologo
echo "== Rust crates"
(cd "$root/src-tauri" && cargo fetch)
echo "== Docker fixture images (tests and benchmarks)"
docker compose -f "$root/scripts/bench/docker/compose.yml" pull mssql || true
docker compose -f "$root/scripts/bench/docker/compose.yml" build postgres || true
echo
echo "Done. Note: Tauri's AppImage bundler downloads linuxdeploy on its first run; do one online 'pnpm build'"
echo "to cache it in ~/.cache/tauri, or build offline with: pnpm tauri build --bundles deb,rpm"

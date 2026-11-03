#!/usr/bin/env bash
# dotnet publish per RID -> src-tauri/binaries/querithm-sidecar-<target-triple>  (§3)
#   --debug    framework-dependent single file (fast; needs the .NET 10 runtime, which the SDK provides)
#   --release  self-contained, trimmed (TrimMode=partial), single file
#   --rid R    publish for another RID (win-x64, linux-x64, osx-x64, osx-arm64)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
mode=debug
rid=""
norestore=""
[[ "${QUERITHM_OFFLINE:-}" == "1" ]] && norestore="--no-restore"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --debug) mode=debug ;;
    --release) mode=release ;;
    --rid) rid="$2"; shift ;;
    --offline) norestore="--no-restore" ;;
  esac
  shift
done
if [[ -z "$rid" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64) rid=linux-arm64 ;;
    Darwin-arm64) rid=osx-arm64 ;;
    Darwin-x86_64) rid=osx-x64 ;;
    *) echo "unsupported host $(uname -s)-$(uname -m)"; exit 1 ;;
  esac
fi
case "$rid" in
  linux-x64) triple=x86_64-unknown-linux-gnu ;;
  linux-arm64) triple=aarch64-unknown-linux-gnu ;;
  osx-x64) triple=x86_64-apple-darwin ;;
  osx-arm64) triple=aarch64-apple-darwin ;;
  win-x64) triple=x86_64-pc-windows-msvc ;;
  *) echo "unknown rid $rid"; exit 1 ;;
esac
ext=""; [[ "$rid" == win-* ]] && ext=".exe"
out="$root/sidecar/Querithm.Host/bin/publish/$rid-$mode"
if [[ "$mode" == release ]]; then
  dotnet publish "$root/sidecar/Querithm.Host" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:PublishTrimmed=true $norestore -o "$out" -v q --nologo
else
  dotnet publish "$root/sidecar/Querithm.Host" -c Debug -r "$rid" --self-contained false \
    -p:PublishSingleFile=true -p:PublishTrimmed=false $norestore -o "$out" -v q --nologo
fi
mkdir -p "$root/src-tauri/binaries"
cp "$out/querithm-sidecar$ext" "$root/src-tauri/binaries/querithm-sidecar-$triple$ext"
echo "sidecar -> src-tauri/binaries/querithm-sidecar-$triple$ext ($(du -h "$out/querithm-sidecar$ext" | cut -f1))"

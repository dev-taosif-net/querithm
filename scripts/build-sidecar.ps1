# dotnet publish per RID -> src-tauri/binaries/querithm-sidecar-<target-triple>.exe  (§3)
param([switch]$debug, [switch]$release, [string]$rid = "win-x64")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$mode = if ($release) { "release" } else { "debug" }
$triples = @{ "win-x64" = "x86_64-pc-windows-msvc"; "linux-x64" = "x86_64-unknown-linux-gnu"; "osx-x64" = "x86_64-apple-darwin"; "osx-arm64" = "aarch64-apple-darwin" }
$triple = $triples[$rid]
$ext = if ($rid -like "win-*") { ".exe" } else { "" }
$out = Join-Path $root "sidecar/Querithm.Host/bin/publish/$rid-$mode"
if ($mode -eq "release") {
  dotnet publish "$root/sidecar/Querithm.Host" -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -o $out -v q --nologo
} else {
  dotnet publish "$root/sidecar/Querithm.Host" -c Debug -r $rid --self-contained false -p:PublishSingleFile=true -p:PublishTrimmed=false -o $out -v q --nologo
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
New-Item -ItemType Directory -Force -Path "$root/src-tauri/binaries" | Out-Null
Copy-Item "$out/querithm-sidecar$ext" "$root/src-tauri/binaries/querithm-sidecar-$triple$ext" -Force
Write-Host "sidecar -> src-tauri/binaries/querithm-sidecar-$triple$ext"

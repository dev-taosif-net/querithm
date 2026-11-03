# C# DTOs (sidecar/Querithm.Contract) -> src/ipc/contract.ts  (§5)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
dotnet run --project "$root/sidecar/Querithm.ContractGen" -c Release -- "$root/src/ipc/contract.ts"

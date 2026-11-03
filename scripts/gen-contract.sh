#!/usr/bin/env bash
# C# DTOs (sidecar/Querithm.Contract) -> src/ipc/contract.ts  (§5)
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet run --project "$root/sidecar/Querithm.ContractGen" -c Release -- "$root/src/ipc/contract.ts"

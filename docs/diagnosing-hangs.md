# Diagnosing a hang

A tab that keeps spinning, a Cancel that does nothing, a dialog that never answers. Find where the sidecar is
waiting before changing code: a guess at the cause cost a long detour on 2026-11-01, and the memory dump found it in
minutes.

## 1. Which sidecar

```powershell
Get-Process querithm-sidecar | Select-Object Id, StartTime, Path
Get-CimInstance Win32_Process -Filter "Name='querithm-sidecar.exe'" | Select-Object ProcessId, ParentProcessId
```

The installed app's sidecar lives under `%LOCALAPPDATA%\Querithm`; a dev one under
`sidecar\Querithm.Host\bin\Debug`, started by `node scripts/dev-bridge.mjs`. Its log is in `<data dir>/logs/`
(`%APPDATA%\querithm` for the app, `~/.local/share/querithm-dev` for the bridge). Each run logs
`Execution <id> started … / cancel requested … / ended <state>` (ids only, never SQL), and the data plane logs
`Data plane client connected` when the shell or bridge attaches — a sidecar without that line has no reader for rows.

## 2. Where it waits

Install the diagnostics tools into a scratch folder (not globally) and take a dump:

```bash
dotnet tool install dotnet-dump --tool-path <scratch>/tools
<scratch>/tools/dotnet-dump collect -p <pid> --type Full -o <scratch>/sidecar.dmp
<scratch>/tools/dotnet-dump analyze <scratch>/sidecar.dmp -c "dumpasync" -c "exit"
```

`dumpasync` lists every parked async chain: a stuck run shows as `ExecutionService+<RunAsync>` under whatever it
awaits (a driver read, `DataPlaneServer+<WriteFrameAsync>`, a pool wait). `dotnet-stack report -p <pid>` shows threads
blocked synchronously (a lock, a driver's `Close()`), which `dumpasync` does not.

A full dump holds process memory — query results, connection details. Keep it in a scratch folder, never attach it
to an issue, and delete it when done.

## 3. Then

- Reproduce it in a test that fails before the fix: `CancelRecoveryTests` for anything around Cancel (the fake
  provider can ignore a cancel, stall the data plane or block an open), `CancelEscalationIntegrationTests` with
  `QUERITHM_IT=1` for driver behaviour.
- A new `CancellationToken.None` is refused by `ArchitectureRulesTests` unless listed with what bounds it.
- Measure driver behaviour instead of assuming it: `Close()` is an abort for SqlClient but not for Npgsql or
  MySqlConnector (`IDbProvider.CloseAbortsCommand`).

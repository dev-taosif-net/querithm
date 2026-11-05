# S07 — SSH and failure recovery

**Goal:** Oracle survives every failure CLAUDE.md §4 lists, and SSH tunnels work or degrade with a reason.

**Prerequisites:** D4a done.

## Scope

1. **SSH:** `RouteThrough` / `TunnelTarget` to the listener host/port; TCPS hostname / DN check still uses the server
   name through the tunnel.
2. **Listener redirects:** RAC SCAN, shared server and `REDIRECT` packets send the client to another host the tunnel does
   not cover → detect (connection target ≠ tunnel target, or the driver error) and refuse with a reason naming the
   redirect. Fixture: verify single-instance works; document that RAC is not testable on Free and cover redirect
   detection with a unit test on the driver error.
3. **Failure paths (one test each):**
   - Connection drop mid-result (kill the session from an admin connection: `ALTER SYSTEM KILL SESSION`) → clear
     message, tab reusable after reconnect, no pinned connection.
   - Cancel then reuse; server that never acknowledges cancel (block the port) → escalation ends the run.
   - Sidecar restart: profiles replayed, open Oracle transaction reported as lost.
   - SSH tunnel loss mid-query.
   - Statement timeout (`CommandTimeout`).
   - ORA-03113 / ORA-03114 / ORA-12537 mapped to "connection lost" messages.
4. **No unbounded waits:** every new await carries a token; `ArchitectureRulesTests` stays green.

## Tests
`Integration/OracleSshAuthTests.cs`, `Integration/OracleRecoveryTests.cs`, `CancelRecoveryTests` Oracle case.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar --filter Oracle` · `CancelRecoveryTests`.

## Done when
Every failure above leaves no orphaned connection and shows a clear message; SSH row in the feature matrix filled.

# S03 — Provider core: connect, TLS, auth, transactions, cancel

**Goal:** a hidden Oracle profile connects securely, runs statements, owns its transactions (D0b) and cancels cleanly.

**Prerequisites:** S01, S02. Owner answer to O1 or take the default.

## Scope

1. **Profile / connection string** (`OracleProvider.BuildConnectionString`, `ParseConnectionString`, `ValidateProfile`):
   - Easy Connect: host, port (default 1521 / 2484 for TCPS), service name. SID only through an explicit option that
     builds `CONNECT_DATA=(SID=…)`.
   - TCPS on by default with server certificate + DN validation; plain TCP is an explicit, warned per-profile downgrade
     (§13 rule 2).
   - `TNS_ADMIN` / `sqlnet.ora` / `tnsnames.ora` ignored (O1); connection-string extras cannot re-enable them or set
     wallet/OS auth.
   - Wallet and external auth: refused with reason (deferred).
2. **Auth:** password from keyring only. Add every new secret keyword (`Password`, `Proxy Password`, wallet keys) to
   the log scrubber (§13 rule 1) and `SecretRedaction`. Security tests first.
3. **Open / validate / product:** `OpenAsync` with the caller's token; `ServerVersion`; `Product` (19c / 21c / 23ai,
   edition); refuse < 19c with a readable reason; `ValidateAsync`.
4. **Identity read:** `SYS_CONTEXT('USERENV','SID')` always; `SERIAL#` + `LOGON_TIME` from `V$SESSION` when granted,
   else the §16 unidentified degradation (`SessionKey`, §6 rule 7).
5. **Transactions (D0b, `OracleTransactions : ITransactionControl`):** `BeginTransaction` turns off autocommit and
   adopts an already-started implicit transaction; autocommit outside explicit transactions; SQL-issued
   `COMMIT`/`ROLLBACK` tracked from the classifier; DDL implicit commit resets state; failed / ambiguous commit
   reported. Never use a PL/SQL `BEGIN` block as transaction begin.
6. **Cancel:** `OracleCommand.Cancel()` + `CommandTimeout`; escalation path joins §7 step 4 (hard close if the server
   never acknowledges); connection reusable after cancel. Add a `CancelRecoveryTests` case.
7. **Errors:** `TranslateException` maps `OracleException` (ORA-xxxxx code, position → line/column).
8. **Databases:** `ChangeDatabaseAsync` refused with reason (service = target); `ListDatabasesAsync` returns the single
   service/PDB; `CurrentDatabase` = service name.

## Tests
Unit: connection-string builder (TCPS default, downgrade flag, SID option, extras injection refused, TNS_ADMIN
ignored), scrubber keys, `TranslateException`. Security: secrets never in logs / store.db / exported profile; TLS
default. Integration (`QUERITHM_IT=1`): connect by service; wrong password → readable error; TCPS with untrusted CA →
readable error; begin → DML → rollback leaves no rows (checked from an independent connection); commit; DDL inside
an explicit transaction ends it; cancel a `DBMS_SESSION.SLEEP(30)` then reuse the connection; identity read with and
without `V$SESSION` grant.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar --filter Oracle` · Security suite green.

## Done when
`query.execute` on a hidden Oracle profile returns results for simple types; transaction and cancel tests green.

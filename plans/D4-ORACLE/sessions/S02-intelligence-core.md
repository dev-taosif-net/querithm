# S02 — Intelligence: tokenizer, PL/SQL splitter, classifier

**Goal:** `Querithm.Intelligence.Oracle` can tokenize, split and classify Oracle scripts without a server.
No regex over SQL (CLAUDE.md §2) — everything on tokens.

**Prerequisites:** S01 (projects exist). Owner answer to O5 or take the default.

## Scope

1. **`OracleTokenizer`:** `"quoted"` identifiers (case-exact), unquoted (fold to upper), `'…'` with `''` escape,
   alternative quoting `q'[…]'`, `q'{…}'`, `q'(…)'`, `q'<…>'`, `q'!…!'` (any delimiter), `N'…'`/`nq'…'`,
   `--` and `/* */` comments, optimizer hints `/*+ … */`, bind variables `:name` / `:1`, `@dblink` after names.
2. **`OracleSplitter`:**
   - Plain SQL ends at `;` or at `/` alone on a line; the `;` is **not** part of the text sent to the driver.
   - PL/SQL units — `DECLARE`/`BEGIN` blocks, `CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE]` `PROCEDURE`,
     `FUNCTION`, `PACKAGE`, `PACKAGE BODY`, `TRIGGER`, `TYPE`, `TYPE BODY`, `LIBRARY` — end only at `/` alone on a
     line (or end of script); keep the final `END;`.
   - `CREATE TYPE x AS OBJECT (…);` without body vs. with methods; `CREATE TRIGGER … COMPOUND TRIGGER`.
   - SQL\*Plus client commands (`SET`, `PROMPT`, `@file`, `SPOOL`, `EXEC`, `DEFINE`, `&var`): recognise them as
     client commands; `SET SERVEROUTPUT ON/OFF` becomes a DBMS_OUTPUT toggle (O5), others a refusal with reason.
   - Folding ranges from the splitter (as `SqliteIntelligenceTests` does).
3. **Classifier** (`OracleLanguageService`):
   - Destructive: `DROP` (incl. `… PURGE`, `CASCADE CONSTRAINTS`), `TRUNCATE`, `DELETE`/`UPDATE` without `WHERE`,
     `MERGE` (conservative), `ALTER TABLE … DROP`, `FLASHBACK TABLE … TO BEFORE DROP` (write), `PURGE RECYCLEBIN`.
   - Anonymous blocks and `EXECUTE IMMEDIATE` / `DBMS_SQL`: dynamic SQL → conservative "may write" (same rule as
     `DynamicSqlClassifierTests`).
   - Transaction statements: `COMMIT`, `ROLLBACK [TO SAVEPOINT]`, `SAVEPOINT`, `SET TRANSACTION`; DDL → implicit commit flag.
   - Schema switch: `ALTER SESSION SET CURRENT_SCHEMA = x` → completion context, not database switch.
   - DDL detection for §8 refresh.
4. **Keywords** (`OracleKeywords`): reserved + keyword lists from the Oracle 19c/23ai SQL reference `V$RESERVED_WORDS`
   — generate from the fixture once, commit the list with its source query in a comment.
5. **Identifier quoting service** for Oracle (`"x"`, `""` escape, upper-case fold rule) wired into the D0e identity.

## Tests (`OracleIntelligenceTests.cs`, no server)
Tokenizer: every `q'` delimiter, nested quotes, hints, `:new` vs bind. Splitter: mixed SQL + PL/SQL script, package
spec + body, trigger with `:NEW`, `/` inside a string/comment, missing final `/`, `END;` kept, `;` stripped.
Classifier: each destructive form, `MERGE`, dynamic SQL in a block, DDL implicit-commit, `CURRENT_SCHEMA`.
Security: sidecar re-classification refuses a destructive Oracle statement without `confirmedDestructive` (test first,
`Security/`).

## Gate
`pnpm test`.

## Done when
All above tests green; splitter output for the fixture's sample scripts executes unchanged in S03/S04.

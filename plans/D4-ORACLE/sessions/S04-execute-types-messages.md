# S04 — Execute path: Arrow types, LOBs, DBMS_OUTPUT, parameters

**Goal:** every Oracle column type reaches the grid losslessly as Arrow (§5, D0f stable schemas); messages and ad-hoc
parameters work.

**Prerequisites:** S03.

## Scope

1. **Type mapping** (`GetColumnHint`, `ReadAsString`, encoder hints), stable per result (D0f):
   - `NUMBER(p,s)` with p ≤ 38 → Decimal128; `NUMBER` without precision / scale and `FLOAT` → exact decimal text
     (fallback) — never `double`, never silent overflow. `INTEGER` → `NUMBER(38)` rules.
   - `BINARY_FLOAT`/`BINARY_DOUBLE` → Float32/Float64 (incl. NaN / ±Inf).
   - `DATE` (has a time part!) → Timestamp; `TIMESTAMP(n)` (up to 9 digits) → Timestamp ns or exact text;
     `TIMESTAMP WITH TIME ZONE` (region names, not only offsets) → text that keeps the zone;
     `WITH LOCAL TIME ZONE` → session-zone rule, documented.
   - `INTERVAL YEAR TO MONTH` / `DAY TO SECOND` → interval or exact text.
   - `RAW`, `LONG RAW`, `BLOB` → Binary; `CLOB`/`NCLOB`/`LONG` → Utf8; `ROWID`/`UROWID` → text; `JSON` (21c+),
     `XMLTYPE`, `SDO_GEOMETRY`, object/collection types → text with a note or refusal reason.
   - LOBs: streamed in chunks with the existing large-value limits; cancel during a LOB read stops it.
   - `''` arrives as NULL — no special casing, but a test pins it.
2. **Fetch tuning:** `FetchSize` / `InitialLOBFetchSize` chosen by measurement (metric 11b) — measure, don't assume.
3. **DBMS_OUTPUT** (`AfterBatchMessagesAsync`): enabled per session when the user toggles it (or `SET SERVEROUTPUT ON`,
   O5); read back with `DBMS_OUTPUT.GET_LINES` after each batch; bounded (existing message-flood limits,
   `MessageFloodTests`).
4. **Ad-hoc parameters** (§7 syntax): `:name` / `:1` collected from tokens, excluding `:NEW`/`:OLD` in trigger bodies and
   anything inside strings/comments/`q'` literals; bound by name (`BindByName = true`).
5. **Warnings / compile errors:** `CREATE PROCEDURE` with errors succeeds with ORA-24344 → read `USER_ERRORS` and
   surface line/column messages.
6. **Implicit result sets** (`DBMS_SQL.RETURN_RESULT`, 12c+): returned as extra result sets, or refused with reason.

## Tests
Unit: type-mapping table, parameter collection. Integration: one row of every type above round-trips through the
encoder (`Oracle_types_arrive_typed_or_as_exact_text`); NUMBER(38) max and 40-digit unconstrained NUMBER exact;
TIMESTAMP WITH TIME ZONE region names; 50 MB CLOB streamed and cancelled mid-stream then connection reused;
DBMS_OUTPUT on/off and flood limit; compile error messages; `:a` binds; `:NEW` not collected; implicit results.

## Gate
`pnpm test` · `QUERITHM_IT=1 dotnet test sidecar --filter Oracle` · `pnpm bench` metric 11b for Oracle recorded (new
per-engine id `11b.oracle`).

## Done when
Typed grid output for every listed type, no lossy conversion, messages and parameters working.

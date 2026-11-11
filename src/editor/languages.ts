// Monarch tokenizers per dialect (§9 Monaco wiring). Language ids come from DIALECTS (engines.ts); LANGUAGES below holds
// one entry per dialect, so a new dialect cannot compile without its editor language.
import type * as Monaco from "monaco-editor/esm/vs/editor/editor.api";
import type { Dialect } from "../ipc/contract";

export const TSQL_KEYWORDS = `ADD ALL ALTER AND ANY APPLY AS ASC AUTHORIZATION BACKUP BEGIN BETWEEN BREAK BROWSE BULK BY CASCADE CASE CATCH CHECK
CHECKPOINT CLOSE CLUSTERED COALESCE COLLATE COLUMN COMMIT COMPUTE CONSTRAINT CONTAINS CONTAINSTABLE CONTINUE CONVERT CREATE CROSS CURRENT
CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER CURSOR DATABASE DBCC DEALLOCATE DECLARE DEFAULT DELETE DENY DESC DISK DISTINCT
DISTRIBUTED DOUBLE DROP DUMP ELSE END ERRLVL ESCAPE EXCEPT EXEC EXECUTE EXISTS EXIT EXTERNAL FETCH FILE FILLFACTOR FOR FOREIGN FREETEXT
FREETEXTTABLE FROM FULL FUNCTION GO GOTO GRANT GROUP HAVING HOLDLOCK IDENTITY IDENTITY_INSERT IDENTITYCOL IF IN INDEX INNER INSERT
INTERSECT INTO IS JOIN KEY KILL LEFT LIKE LINENO LOAD MERGE NATIONAL NOCHECK NOCOUNT NOLOCK NONCLUSTERED NOT NULL NULLIF OF OFF OFFSETS OFFSET ON OPEN
OPENDATASOURCE OPENQUERY OPENROWSET OPENXML OPTION OR ORDER OUTER OUTPUT OVER PERCENT PIVOT PLAN PRECISION PRIMARY PRINT PROC PROCEDURE
PUBLIC RAISERROR READ READTEXT RECONFIGURE REFERENCES REPLICATION RESTORE RESTRICT RETURN RETURNS REVERT REVOKE RIGHT ROLLBACK ROWCOUNT
ROWGUIDCOL RULE SAVE SCHEMA SECURITYAUDIT SELECT SEMANTICKEYPHRASETABLE SEMANTICSIMILARITYDETAILSTABLE SEMANTICSIMILARITYTABLE SESSION_USER
SET SETUSER SHUTDOWN SOME STATISTICS SYSTEM_USER TABLE TABLESAMPLE TEXTSIZE THEN THROW TO TOP TRAN TRANSACTION TRIGGER TRUNCATE TRY
TRY_CONVERT TSEQUAL UNION UNIQUE UNPIVOT UPDATE UPDATETEXT USE USER VALUES VARYING VIEW WAITFOR WHEN WHERE WHILE WITH WITHIN WRITETEXT
MATCHED SOURCE TARGET ROWS ONLY NEXT FIRST LAST PARTITION RANGE PRECEDING FOLLOWING UNBOUNDED NOWAIT READONLY XACT_ABORT ANSI_NULLS
QUOTED_IDENTIFIER SCHEMABINDING ENCRYPTION RECOMPILE INCLUDE`.split(/\s+/);

export const TSQL_TYPES = `bigint binary bit char cursor date datetime datetime2 datetimeoffset decimal float geography geometry hierarchyid image int
money nchar ntext numeric nvarchar real rowversion smalldatetime smallint smallmoney sql_variant sysname table text time timestamp tinyint
uniqueidentifier varbinary varchar xml max`.split(/\s+/);

export const TSQL_FUNCTIONS = `ABS ACOS APP_NAME ASCII ASIN ATAN ATN2 AVG CAST CEILING CHARINDEX CHECKSUM CHOOSE CONCAT CONCAT_WS COS COT COUNT COUNT_BIG
CUME_DIST DATALENGTH DATEADD DATEDIFF DATEDIFF_BIG DATEFROMPARTS DATENAME DATEPART DAY DB_ID DB_NAME DENSE_RANK DIFFERENCE EOMONTH ERROR_LINE
ERROR_MESSAGE ERROR_NUMBER ERROR_PROCEDURE ERROR_SEVERITY ERROR_STATE EXP FIRST_VALUE FLOOR FORMAT GETDATE GETUTCDATE GREATEST GROUPING HOST_NAME IIF
ISDATE ISJSON ISNULL ISNUMERIC JSON_MODIFY JSON_QUERY JSON_VALUE LAG LAST_VALUE LEAD LEAST LEFT LEN LOG LOG10 LOWER LTRIM MAX MIN MONTH NEWID
NEWSEQUENTIALID NTILE OBJECT_ID OBJECT_NAME OBJECT_DEFINITION PARSE PATINDEX PERCENT_RANK PI POWER QUOTENAME RAND RANK REPLACE REPLICATE REVERSE RIGHT
ROUND ROW_NUMBER RTRIM SCHEMA_NAME SCOPE_IDENTITY SERVERPROPERTY SIGN SIN SOUNDEX SPACE SQRT SQUARE STDEV STR STRING_AGG STRING_SPLIT STUFF SUBSTRING
SUM SUSER_NAME SUSER_SNAME SWITCHOFFSET SYSDATETIME SYSDATETIMEOFFSET SYSUTCDATETIME TAN TODATETIMEOFFSET TRANSLATE TRIM TRY_CAST TRY_PARSE UNICODE
UPPER USER_NAME VAR YEAR OPENJSON`.split(/\s+/);

export const PG_KEYWORDS = `ABORT ALL ALTER ANALYZE AND ANY ARRAY AS ASC ASYMMETRIC BEGIN BETWEEN BOTH BY CALL CASCADE CASE CAST CHECK COLLATE COLUMN
COMMENT COMMIT CONCURRENTLY CONFLICT CONSTRAINT COPY CREATE CROSS CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP CURRENT_USER CURSOR DECLARE DEFAULT
DEFERRABLE DELETE DESC DISTINCT DO DROP ELSE ELSIF END EXCEPT EXCEPTION EXECUTE EXISTS EXPLAIN EXTENSION FALSE FETCH FILTER FOR FOREIGN FROM FULL
FUNCTION GRANT GROUP HAVING IF ILIKE IN INDEX INHERITS INNER INSERT INTERSECT INTO IS ISNULL JOIN LANGUAGE LATERAL LEADING LEFT LIKE LIMIT LOOP
MATERIALIZED NATURAL NOT NOTHING NOTNULL NULL NULLS OF OFFSET ON ONLY OR ORDER OUT OUTER OVER OVERLAPS PARTITION PERFORM PLACING PRIMARY PROCEDURE
RAISE REFERENCES REFRESH RETURN RETURNING RETURNS REVOKE RIGHT ROLLBACK ROW ROWS SCHEMA SELECT SEQUENCE SESSION_USER SET SIMILAR SOME START STRICT
SYMMETRIC TABLE TABLESPACE THEN TO TRAILING TRANSACTION TRIGGER TRUE TRUNCATE TYPE UNION UNIQUE UNLOGGED UPDATE USER USING VACUUM VALUES VARIADIC
VERBOSE VIEW VOLATILE STABLE IMMUTABLE WHEN WHERE WINDOW WITH WITHOUT RECURSIVE NOTICE WARNING INFO DEBUG LOG ELSEIF FOREACH WHILE EXIT CONTINUE
GET DIAGNOSTICS FOUND QUERY NEW OLD SECURITY DEFINER INVOKER PARALLEL SAFE COST`.split(/\s+/);

export const PG_TYPES = `bigint bigserial bit boolean bool box bytea char character cidr circle date decimal double float4 float8 inet int int2 int4 int8
integer interval json jsonb line lseg macaddr money numeric path point polygon precision real record serial smallint smallserial text time timestamp
timestamptz timetz tsquery tsvector uuid varchar varying xml void trigger regclass oid name`.split(/\s+/);

export const PG_FUNCTIONS = `abs array_agg array_length avg btrim ceil coalesce concat count current_setting date_part date_trunc dense_rank exists extract
first_value floor format generate_series greatest jsonb_agg jsonb_build_object json_agg lag lead least left length lower lpad ltrim max min now
nullif percent_rank rank regexp_replace regexp_matches repeat replace right round row_number rpad rtrim split_part string_agg strpos substr substring
sum to_char to_date to_json to_jsonb to_number to_timestamp trim unnest upper`.split(/\s+/);

export function tsqlLanguage(): Monaco.languages.IMonarchLanguage {
  return {
    defaultToken: "",
    ignoreCase: true,
    tokenPostfix: ".tsql",
    keywords: TSQL_KEYWORDS.map((k) => k.toLowerCase()),
    typeKeywords: TSQL_TYPES,
    builtinFunctions: TSQL_FUNCTIONS.map((k) => k.toLowerCase()),
    operators: ["=", ">", "<", "<=", ">=", "<>", "!=", "!<", "!>", "+", "-", "*", "/", "%", "&", "|", "^", "~", "+=", "-=", "*=", "/=", "%="],
    brackets: [
      { open: "(", close: ")", token: "delimiter.parenthesis" },
      { open: "[", close: "]", token: "delimiter.square" },
    ],
    tokenizer: {
      root: [
        { include: "@comments" },
        { include: "@whitespace" },
        [/^\s*go\b(\s+\d+)?\s*$/i, "keyword.batch"],
        [/@@[\w]+/, "predefined"],
        [/@[\w$#]+/, "variable"],
        [/#{1,2}[\w$#@]+/, "identifier.temp"],
        [/N?'/, { token: "string.quote", next: "@string" }],
        [/\[/, { token: "identifier.quote", next: "@bracketed" }],
        [/"/, { token: "identifier.quote", next: "@quoted" }],
        [/0x[0-9a-f]*/i, "number.hex"],
        [/[$]?[0-9]+(\.[0-9]*)?([eE][+-]?[0-9]+)?/, "number"],
        [/\.[0-9]+([eE][+-]?[0-9]+)?/, "number"],
        [/[;,.]/, "delimiter"],
        [/[()]/, "@brackets"],
        [
          /[\w$#]+/,
          {
            cases: {
              "@keywords": "keyword",
              "@typeKeywords": "type",
              "@builtinFunctions": "predefined",
              "@default": "identifier",
            },
          },
        ],
        [/[<>=!%&+\-*/|~^]+/, "operator"],
      ],
      whitespace: [[/\s+/, "white"]],
      comments: [
        [/--+.*/, "comment"],
        [/\/\*/, { token: "comment.quote", next: "@comment" }],
      ],
      comment: [
        [/[^*/]+/, "comment"],
        [/\/\*/, { token: "comment.quote", next: "@push" }],
        [/\*\//, { token: "comment.quote", next: "@pop" }],
        [/./, "comment"],
      ],
      string: [
        [/[^']+/, "string"],
        [/''/, "string"],
        [/'/, { token: "string.quote", next: "@pop" }],
      ],
      bracketed: [
        [/[^\]]+/, "identifier"],
        [/\]\]/, "identifier"],
        [/\]/, { token: "identifier.quote", next: "@pop" }],
      ],
      quoted: [
        [/[^"]+/, "identifier"],
        [/""/, "identifier"],
        [/"/, { token: "identifier.quote", next: "@pop" }],
      ],
    },
  } as Monaco.languages.IMonarchLanguage;
}

export function pgLanguage(): Monaco.languages.IMonarchLanguage {
  return {
    defaultToken: "",
    ignoreCase: true,
    tokenPostfix: ".pgsql",
    keywords: PG_KEYWORDS.map((k) => k.toLowerCase()),
    typeKeywords: PG_TYPES,
    builtinFunctions: PG_FUNCTIONS,
    operators: [],
    tokenizer: {
      root: [
        { include: "@comments" },
        [/\s+/, "white"],
        [/\$([A-Za-z_]\w*)?\$/, { token: "string.dollar", next: "@dollar.$0" }],
        [/\$\d+/, "variable"],
        [/::\s*[\w"]+(\[\])?/, "type"],
        [/:[A-Za-z_]\w*/, "variable"],
        [/[eE]'/, { token: "string.quote", next: "@estring" }],
        [/'/, { token: "string.quote", next: "@string" }],
        [/"/, { token: "identifier.quote", next: "@quoted" }],
        [/[0-9]+(\.[0-9]*)?([eE][+-]?[0-9]+)?/, "number"],
        [/[;,.]/, "delimiter"],
        [/[()[\]]/, "@brackets"],
        [
          /[A-Za-z_][\w$]*/,
          {
            cases: {
              "@keywords": "keyword",
              "@typeKeywords": "type",
              "@builtinFunctions": "predefined",
              "@default": "identifier",
            },
          },
        ],
        [/[<>=!%&+\-*/|~^#@?]+/, "operator"],
      ],
      comments: [
        [/--+.*/, "comment"],
        [/\/\*/, { token: "comment.quote", next: "@comment" }],
      ],
      comment: [
        [/[^*/]+/, "comment"],
        [/\/\*/, { token: "comment.quote", next: "@push" }],
        [/\*\//, { token: "comment.quote", next: "@pop" }],
        [/./, "comment"],
      ],
      string: [
        [/[^']+/, "string"],
        [/''/, "string"],
        [/'/, { token: "string.quote", next: "@pop" }],
      ],
      estring: [
        [/[^'\\]+/, "string"],
        [/\\./, "string.escape"],
        [/''/, "string"],
        [/'/, { token: "string.quote", next: "@pop" }],
      ],
      quoted: [
        [/[^"]+/, "identifier"],
        [/""/, "identifier"],
        [/"/, { token: "identifier.quote", next: "@pop" }],
      ],
      // Dollar-quoted bodies: highlight the PL/pgSQL inside, end at the matching tag.
      dollar: [
        [
          /\$([A-Za-z_]\w*)?\$/,
          { cases: { "$0==$S2": { token: "string.dollar", next: "@pop" }, "@default": "string" } },
        ],
        { include: "@comments" },
        [/'/, { token: "string.quote", next: "@string" }],
        [/"/, { token: "identifier.quote", next: "@quoted" }],
        [/[0-9]+(\.[0-9]*)?/, "number"],
        [
          /[A-Za-z_][\w$]*/,
          { cases: { "@keywords": "keyword", "@typeKeywords": "type", "@builtinFunctions": "predefined", "@default": "identifier" } },
        ],
        [/./, ""],
      ],
    },
  } as Monaco.languages.IMonarchLanguage;
}

export const SQLITE_KEYWORDS = `ABORT ACTION ADD AFTER ALL ALTER ALWAYS ANALYZE AND AS ASC ATTACH AUTOINCREMENT BEFORE BEGIN BETWEEN BY CASCADE CASE
CAST CHECK COLLATE COLUMN COMMIT CONFLICT CONSTRAINT CREATE CROSS CURRENT CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP DATABASE DEFAULT DEFERRABLE
DEFERRED DELETE DESC DETACH DISTINCT DO DROP EACH ELSE END ESCAPE EXCEPT EXCLUDE EXCLUSIVE EXISTS EXPLAIN FAIL FILTER FIRST FOLLOWING FOR FOREIGN
FROM FULL GENERATED GLOB GROUP GROUPS HAVING IF IGNORE IMMEDIATE IN INDEX INDEXED INITIALLY INNER INSERT INSTEAD INTERSECT INTO IS ISNULL JOIN KEY
LAST LEFT LIKE LIMIT MATCH MATERIALIZED NATURAL NO NOT NOTHING NOTNULL NULL NULLS OF OFFSET ON OR ORDER OTHERS OUTER OVER PARTITION PLAN PRAGMA
PRECEDING PRIMARY QUERY RAISE RANGE RECURSIVE REFERENCES REGEXP REINDEX RELEASE RENAME REPLACE RESTRICT RETURNING RIGHT ROLLBACK ROW ROWS ROWID
SAVEPOINT SELECT SET STRICT TABLE TEMP TEMPORARY THEN TIES TO TRANSACTION TRIGGER UNBOUNDED UNION UNIQUE UPDATE USING VACUUM VALUES VIEW VIRTUAL
WHEN WHERE WINDOW WITH WITHOUT`.split(/\s+/);

/** SQLite's storage classes and affinity type names (a column may name any type; these are the ones with meaning). */
export const SQLITE_TYPES = `INTEGER REAL TEXT BLOB NUMERIC ANY INT BOOLEAN DATE DATETIME VARCHAR DECIMAL DOUBLE FLOAT`.split(/\s+/);

export const SQLITE_FUNCTIONS = `abs avg changes char coalesce count date datetime format glob group_concat hex ifnull iif instr json json_array
json_each json_extract json_group_array json_group_object json_object json_tree julianday lag last_insert_rowid lead length like lower ltrim max min
nullif printf quote random randomblob rank replace round row_number rtrim sign strftime substr substring sum time total trim typeof unhex unicode
unixepoch upper zeroblob`.split(/\s+/);

export const MYSQL_KEYWORDS = `ACCESSIBLE ADD AFTER ALGORITHM ALL ALTER ANALYZE AND AS ASC AUTO_INCREMENT BEFORE BEGIN BETWEEN BOTH BY CALL CASCADE CASE
CHANGE CHARACTER CHARSET CHECK COLLATE COLUMN COMMENT COMMIT CONDITION CONSTRAINT CONTINUE CREATE CROSS CURRENT_DATE CURRENT_TIME CURRENT_TIMESTAMP
CURRENT_USER CURSOR DATABASE DATABASES DECLARE DEFAULT DEFINER DELAYED DELETE DELIMITER DESC DESCRIBE DETERMINISTIC DISTINCT DIV DO DROP DUAL DUPLICATE
EACH ELSE ELSEIF END ENGINE ENUM ESCAPED EVENT EXCEPT EXISTS EXIT EXPLAIN FALSE FETCH FIRST FOR FORCE FOREIGN FORMAT FROM FULLTEXT FUNCTION GRANT
GROUP HANDLER HAVING IF IGNORE IN INDEX INFILE INNER INOUT INSERT INTERSECT INTERVAL INTO IS ITERATE JOIN KEY KEYS KILL LAST LATERAL LEADING LEAVE
LEFT LIKE LIMIT LINES LOAD LOCK LOOP MOD MODIFY NATURAL NOT NULL OF OFFSET ON OPTIMIZE OR ORDER OUT OUTER OVER PARTITION PREPARE PRIMARY PROCEDURE
RANGE READS RECURSIVE REFERENCES REGEXP RELEASE RENAME REPEAT REPLACE RESIGNAL RETURN RETURNS REVOKE RIGHT RLIKE ROLLBACK ROW ROWS SAVEPOINT
SCHEMA SECURITY SELECT SEPARATOR SET SHOW SIGNAL SQL SQLEXCEPTION SQLSTATE SQLWARNING START STRAIGHT_JOIN TABLE TEMPORARY TERMINATED THEN TO
TRAILING TRANSACTION TRIGGER TRUE TRUNCATE UNDO UNION UNIQUE UNLOCK UNSIGNED UNTIL UPDATE USE USING VALUES VIEW WHEN WHERE WHILE WINDOW WITH XOR
ZEROFILL`.split(/\s+/);

export const MYSQL_TYPES = `bigint binary bit blob bool boolean char date datetime dec decimal double enum float geometry int integer json longblob
longtext mediumblob mediumint mediumtext numeric point real set smallint text time timestamp tinyblob tinyint tinytext varbinary varchar year`.split(/\s+/);

export const MYSQL_FUNCTIONS = `ABS AVG CAST CEIL CHAR_LENGTH COALESCE CONCAT CONCAT_WS CONVERT COUNT CURDATE CURTIME DATABASE DATE_ADD DATE_FORMAT
DATE_SUB DATEDIFF DAY DENSE_RANK EXTRACT FIRST_VALUE FLOOR FOUND_ROWS GROUP_CONCAT IFNULL INSTR JSON_ARRAY JSON_ARRAYAGG JSON_CONTAINS JSON_EXTRACT
JSON_OBJECT JSON_OBJECTAGG JSON_SET JSON_UNQUOTE LAG LAST_INSERT_ID LAST_VALUE LEAD LEFT LENGTH LOCATE LOWER LPAD LTRIM MAX MIN MONTH NOW NTILE
NULLIF POWER RAND RANK REPLACE RIGHT ROUND ROW_COUNT ROW_NUMBER RPAD RTRIM SQRT STR_TO_DATE SUBSTRING SUBSTRING_INDEX SUM SYSDATE TIMESTAMPDIFF
TRIM UPPER USER UUID YEAR`.split(/\s+/);

const wordCases = { cases: { "@keywords": "keyword", "@typeKeywords": "type", "@builtinFunctions": "predefined", "@default": "identifier" } };

/** SQLite: '…' strings, "…" / […] / `…` names, X'…' blobs, `?` `?NNN` `:name` `@name` `$name` parameters. */
export function sqliteLanguage(): Monaco.languages.IMonarchLanguage {
  return {
    defaultToken: "",
    ignoreCase: true,
    tokenPostfix: ".sqlite",
    keywords: SQLITE_KEYWORDS.map((k) => k.toLowerCase()),
    typeKeywords: SQLITE_TYPES.map((k) => k.toLowerCase()),
    builtinFunctions: SQLITE_FUNCTIONS,
    operators: [],
    tokenizer: {
      root: [
        { include: "@comments" },
        [/\s+/, "white"],
        [/\?\d*/, "variable"],
        [/[:@$][A-Za-z_]\w*/, "variable"],
        [/[xX]'/, { token: "string.quote", next: "@blob" }],
        [/'/, { token: "string.quote", next: "@string" }],
        [/"/, { token: "identifier.quote", next: "@quoted" }],
        [/`/, { token: "identifier.quote", next: "@backticked" }],
        [/\[/, { token: "identifier.quote", next: "@bracketed" }],
        [/0x[0-9a-f]+/i, "number.hex"],
        [/[0-9]+(\.[0-9]*)?([eE][+-]?[0-9]+)?/, "number"],
        [/\.[0-9]+([eE][+-]?[0-9]+)?/, "number"],
        [/[;,.]/, "delimiter"],
        [/[()]/, "@brackets"],
        [/[A-Za-z_][\w$]*/, wordCases],
        [/->>|->|\|\||==|!=|<>|<=|>=|<<|>>|[<>=!%&+\-*/|~]/, "operator"],
      ],
      comments: [
        [/--.*/, "comment"],
        [/\/\*/, { token: "comment.quote", next: "@comment" }],
      ],
      // SQLite block comments do not nest.
      comment: [
        [/[^*]+/, "comment"],
        [/\*\//, { token: "comment.quote", next: "@pop" }],
        [/\*/, "comment"],
      ],
      string: [
        [/[^']+/, "string"],
        [/''/, "string"],
        [/'/, { token: "string.quote", next: "@pop" }],
      ],
      blob: [
        [/[0-9a-fA-F]+/, "number.hex"],
        [/'/, { token: "string.quote", next: "@pop" }],
        [/[^']+/, "invalid"],
      ],
      quoted: [
        [/[^"]+/, "identifier"],
        [/""/, "identifier"],
        [/"/, { token: "identifier.quote", next: "@pop" }],
      ],
      backticked: [
        [/[^`]+/, "identifier"],
        [/``/, "identifier"],
        [/`/, { token: "identifier.quote", next: "@pop" }],
      ],
      bracketed: [
        [/[^\]]+/, "identifier"],
        [/\]/, { token: "identifier.quote", next: "@pop" }],
      ],
    },
  } as Monaco.languages.IMonarchLanguage;
}

/**
 * MySQL / MariaDB: `…` names, '…' and "…" strings with backslash escapes, `#` and `-- ` comments, @user and
 * @@system variables. Executable comments (/*! … *\/, /*M! … *\/) hold code but are painted as comments for now.
 */
export function mysqlLanguage(): Monaco.languages.IMonarchLanguage {
  return {
    defaultToken: "",
    ignoreCase: true,
    tokenPostfix: ".mysql",
    keywords: MYSQL_KEYWORDS.map((k) => k.toLowerCase()),
    typeKeywords: MYSQL_TYPES,
    builtinFunctions: MYSQL_FUNCTIONS.map((k) => k.toLowerCase()),
    operators: [],
    tokenizer: {
      root: [
        { include: "@comments" },
        [/\s+/, "white"],
        [/@@[\w$.]+/, "predefined"],
        [/@[\w$.]+/, "variable"],
        [/[xXbBnN]'/, { token: "string.quote", next: "@string" }],
        [/'/, { token: "string.quote", next: "@string" }],
        [/"/, { token: "string.quote", next: "@dstring" }],
        [/`/, { token: "identifier.quote", next: "@backticked" }],
        [/0x[0-9a-f]+/i, "number.hex"],
        [/0b[01]+/, "number"],
        [/[0-9]+(\.[0-9]*)?([eE][+-]?[0-9]+)?/, "number"],
        [/\.[0-9]+([eE][+-]?[0-9]+)?/, "number"],
        [/[;,.]/, "delimiter"],
        [/[()]/, "@brackets"],
        [/[A-Za-z_$][\w$]*/, wordCases],
        [/<=>|->>|->|:=|\|\||&&|<=|>=|<>|!=|<<|>>|[<>=!%&+\-*/|~^]/, "operator"],
      ],
      comments: [
        [/--(?=\s|$).*/, "comment"],
        [/#.*/, "comment"],
        [/\/\*/, { token: "comment.quote", next: "@comment" }],
      ],
      // MySQL block comments do not nest.
      comment: [
        [/[^*]+/, "comment"],
        [/\*\//, { token: "comment.quote", next: "@pop" }],
        [/\*/, "comment"],
      ],
      string: [
        [/[^'\\]+/, "string"],
        [/\\./, "string.escape"],
        [/''/, "string"],
        [/'/, { token: "string.quote", next: "@pop" }],
      ],
      dstring: [
        [/[^"\\]+/, "string"],
        [/\\./, "string.escape"],
        [/""/, "string"],
        [/"/, { token: "string.quote", next: "@pop" }],
      ],
      backticked: [
        [/[^`]+/, "identifier"],
        [/``/, "identifier"],
        [/`/, { token: "identifier.quote", next: "@pop" }],
      ],
    },
  } as Monaco.languages.IMonarchLanguage;
}

const sharedConfiguration: Monaco.languages.LanguageConfiguration = {
  comments: { lineComment: "--", blockComment: ["/*", "*/"] },
  autoClosingPairs: [
    { open: "[", close: "]" },
    { open: "(", close: ")" },
    { open: "'", close: "'", notIn: ["string", "comment"] },
    { open: '"', close: '"', notIn: ["string"] },
  ],
  surroundingPairs: [
    { open: "[", close: "]" },
    { open: "(", close: ")" },
    { open: "'", close: "'" },
    { open: '"', close: '"' },
  ],
  wordPattern: /(-?\d*\.\d\w*)|([^`~!%^&*()\-=+[{\]}\\|;:'",.<>/?\s]+)/g,
};

/**
 * T-SQL: `[ ]` quote a name (`[Order]`, `[a]]b]`), they never nest, so they are not bracket pairs. As pairs, the `]]`
 * escape became an unexpected closing bracket painted error red, whatever `colorizedBracketPairs` said
 * (monaco bracketPairsTree.js). Auto-close and surround still work from the shared lists.
 */
export const tsqlLanguageConfiguration: Monaco.languages.LanguageConfiguration = {
  ...sharedConfiguration,
  brackets: [["(", ")"]],
  colorizedBracketPairs: [["(", ")"]],
};

/** SQLite: `[ ]` and backticks quote names, like T-SQL's brackets; only `( )` pair. */
export const sqliteLanguageConfiguration: Monaco.languages.LanguageConfiguration = {
  ...sharedConfiguration,
  brackets: [["(", ")"]],
  colorizedBracketPairs: [["(", ")"]],
};

/** MySQL: `#` also starts a line comment, but the toggle uses `-- `; backticks quote names. */
export const mysqlLanguageConfiguration: Monaco.languages.LanguageConfiguration = {
  ...sharedConfiguration,
  comments: { lineComment: "-- ", blockComment: ["/*", "*/"] },
  autoClosingPairs: [...(sharedConfiguration.autoClosingPairs ?? []), { open: "`", close: "`", notIn: ["string", "comment"] }],
  surroundingPairs: [...(sharedConfiguration.surroundingPairs ?? []), { open: "`", close: "`" }],
  brackets: [["(", ")"]],
  colorizedBracketPairs: [["(", ")"]],
};

/** PostgreSQL: `[ ]` index and build arrays, so they match; only `( )` take the syntax theme's bracket colors. */
export const pgLanguageConfiguration: Monaco.languages.LanguageConfiguration = {
  ...sharedConfiguration,
  brackets: [
    ["[", "]"],
    ["(", ")"],
  ],
  colorizedBracketPairs: [["(", ")"]],
};

/** DuckDB: `[ ]` build lists and `{ }` structs, so they match; only `( )` take the syntax theme's bracket colors. */
export const duckdbLanguageConfiguration: Monaco.languages.LanguageConfiguration = {
  ...sharedConfiguration,
  brackets: [
    ["[", "]"],
    ["{", "}"],
    ["(", ")"],
  ],
  colorizedBracketPairs: [["(", ")"]],
};

export const DUCKDB_KEYWORDS = `ABORT ALL ALTER ANALYZE AND ANTI ANY ARRAY AS ASC ASOF AT ATTACH BEGIN BETWEEN BOTH BY CALL CASCADE CASE CAST CHECK
CHECKPOINT COLLATE COLUMN COLUMNS COMMENT COMMIT CONSTRAINT COPY CREATE CROSS CUBE DEFAULT DELETE DESC DESCRIBE DETACH DISTINCT
DO DROP ELSE END EXCEPT EXCLUDE EXISTS EXPLAIN EXPORT FALSE FETCH FILTER FIRST FOR FOREIGN FROM FULL FUNCTION GLOB GROUP
GROUPING HAVING IF ILIKE IMPORT IN INDEX INNER INSERT INSTALL INTERSECT INTO IS JOIN KEY LAMBDA LAST LATERAL LEFT LIKE
LIMIT LOAD MACRO NATURAL NOT NULL NULLS OFFSET ON ONLY OR ORDER OUTER OVER PARTITION PIVOT POSITIONAL PRAGMA PRIMARY
QUALIFY RANGE RECURSIVE REFERENCES REPLACE RESET RETURNING RIGHT ROLLBACK ROLLUP ROW ROWS SAMPLE SCHEMA SELECT SEMI
SEQUENCE SET SETS SHOW SIMILAR SOME SUMMARIZE TABLE TEMP TEMPORARY THEN TO TRANSACTION TRUE TRUNCATE TYPE UNION UNIQUE
UNPIVOT UPDATE USE USING VACUUM VALUES VIEW WHEN WHERE WINDOW WITH WITHIN`.split(/\s+/);

export const DUCKDB_TYPES = `bigint bit blob bool boolean bpchar bytea char date datetime decimal double enum float float4 float8 hugeint int int1
int2 int4 int8 int128 integer interval json list long map numeric real short signed smallint string struct text time
timestamp timestamptz timestamp_ns timestamp_ms timestamp_s timetz tinyint ubigint uhugeint uinteger union usmallint
utinyint uuid varchar varint`.split(/\s+/);

export const DUCKDB_FUNCTIONS = `abs age any_value approx_count_distinct arg_max arg_min array_agg avg ceil coalesce concat concat_ws contains count
current_date current_timestamp date_add date_diff date_part date_trunc dense_rank ends_with epoch first first_value floor
format from_json gen_random_uuid generate_series greatest hash ifnull json_extract json_extract_string lag last last_value
lead least length list list_contains list_filter list_sort list_transform list_value lower make_date map max md5 median
min mode now ntile nullif position power printf quantile_cont random range rank regexp_extract regexp_matches
regexp_replace replace round row_number split_part sqrt starts_with strftime string_agg string_split strptime struct_pack
substring sum to_json trim try_cast typeof unnest upper`.split(/\s+/);

/**
 * DuckDB: PostgreSQL's lexical forms (nested comments, E'' strings, $tag$ strings — plain strings here, not code) plus
 * `$name` and `?` parameters; `{ }` are struct literals.
 */
export function duckdbLanguage(): Monaco.languages.IMonarchLanguage {
  return {
    defaultToken: "",
    ignoreCase: true,
    tokenPostfix: ".duckdb",
    keywords: DUCKDB_KEYWORDS.map((k) => k.toLowerCase()),
    typeKeywords: DUCKDB_TYPES,
    builtinFunctions: DUCKDB_FUNCTIONS,
    operators: [],
    tokenizer: {
      root: [
        { include: "@comments" },
        [/\s+/, "white"],
        [/\$([A-Za-z_]\w*)?\$/, { token: "string.dollar", next: "@dollar.$0" }],
        [/\$\d+/, "variable"],
        [/\$[A-Za-z_]\w*/, "variable"],
        [/\?/, "variable"],
        [/::\s*[\w"]+(\[\])?/, "type"],
        [/[eE]'/, { token: "string.quote", next: "@estring" }],
        [/'/, { token: "string.quote", next: "@string" }],
        [/"/, { token: "identifier.quote", next: "@quoted" }],
        [/0[xX][0-9a-fA-F]+/, "number"],
        [/[0-9]+(\.[0-9]*)?([eE][+-]?[0-9]+)?/, "number"],
        [/[;,.:]/, "delimiter"],
        [/[()[\]{}]/, "@brackets"],
        [/[A-Za-z_][\w$]*/, wordCases],
        [/[<>=!%&+\-*/|~^#@]+/, "operator"],
      ],
      comments: [
        [/--+.*/, "comment"],
        [/\/\*/, { token: "comment.quote", next: "@comment" }],
      ],
      comment: [
        [/[^*/]+/, "comment"],
        [/\/\*/, { token: "comment.quote", next: "@push" }],
        [/\*\//, { token: "comment.quote", next: "@pop" }],
        [/./, "comment"],
      ],
      string: [
        [/[^']+/, "string"],
        [/''/, "string"],
        [/'/, { token: "string.quote", next: "@pop" }],
      ],
      estring: [
        [/[^'\\]+/, "string"],
        [/\\./, "string.escape"],
        [/''/, "string"],
        [/'/, { token: "string.quote", next: "@pop" }],
      ],
      quoted: [
        [/[^"]+/, "identifier"],
        [/""/, "identifier"],
        [/"/, { token: "identifier.quote", next: "@pop" }],
      ],
      dollar: [
        [/\$([A-Za-z_]\w*)?\$/, { cases: { "$0==$S2": { token: "string.dollar", next: "@pop" }, "@default": "string" } }],
        [/[^$]+/, "string"],
        [/./, "string"],
      ],
    },
  } as Monaco.languages.IMonarchLanguage;
}

/**
 * Oracle (D4 S06): the SQL and PL/SQL reserved words (the sidecar's OracleKeywords, which completion quotes) plus the
 * keywords of the statements and PL/SQL blocks people write.
 */
export const ORACLE_KEYWORDS = `ACCESS ADD ALL ALTER AND ANY AS ASC AUDIT BETWEEN BY CHAR CHECK CLUSTER COLUMN COMMENT COMPRESS CONNECT CREATE
CURRENT DATE DECIMAL DEFAULT DELETE DESC DISTINCT DROP ELSE EXCEPT EXCLUSIVE EXISTS FILE FLOAT FOR FROM GRANT GROUP HAVING IDENTIFIED
IMMEDIATE IN INCREMENT INDEX INITIAL INSERT INTEGER INTERSECT INTO IS LEVEL LIKE LOCK LONG MAXEXTENTS MINUS MLSLABEL MODE MODIFY
NOAUDIT NOCOMPRESS NOT NOWAIT NULL NUMBER OF OFFLINE ON ONLINE OPTION OR ORDER PCTFREE PRIOR PUBLIC RAW RENAME RESOURCE REVOKE ROW
ROWID ROWNUM ROWS SELECT SESSION SET SHARE SIZE SMALLINT START SUCCESSFUL SYNONYM SYSDATE TABLE THEN TO TRIGGER UID UNION UNIQUE
UPDATE USER VALIDATE VALUES VARCHAR VARCHAR2 VIEW WHENEVER WHERE WITH
BEGIN CASE CURSOR DECLARE END EXCEPTION FETCH FUNCTION GOTO LOOP OPEN PACKAGE PROCEDURE RAISE RETURN TYPE WHEN WHILE ELSIF EXIT
IF OUT PRAGMA RECORD SUBTYPE
BODY BULK COLLECT COMMIT CONSTANT CONSTRAINT CONTINUE CROSS DEFINER EXECUTE FIRST FOREIGN FORALL FULL INNER JOIN KEY LEFT LIMIT
MERGE MATCHED NATURAL NEXT NOCOPY NULLS OFFSET ONLY OTHERS OUTER OVER PARTITION PIPELINED PIPE PRIMARY REFERENCES REPLACE RETURNING
REVERSE RIGHT ROLLBACK ROWTYPE SAVEPOINT SEQUENCE TRUNCATE USING EDITIONABLE NONEDITIONABLE AUTHID AUTONOMOUS_TRANSACTION
EXCEPTION_INIT SERIALLY_REUSABLE DETERMINISTIC RESULT_CACHE PARALLEL_ENABLE COMPOUND INSTEAD EACH BEFORE AFTER STATEMENT NEW OLD
REFERENCING FOLLOWS ENABLE DISABLE PURGE CASCADE CONSTRAINTS FLASHBACK EXPLAIN PLAN MATERIALIZED YEAR MONTH DAY HOUR MINUTE SECOND
ZONE LOCAL TIME SAMPLE PIVOT UNPIVOT LATERAL APPLY TIES PERCENT SIBLINGS NOCYCLE KEEP RANGE PRECEDING FOLLOWING UNBOUNDED`.split(/\s+/);

export const ORACLE_TYPES = `binary_double binary_float binary_integer blob boolean char clob date dec decimal double float int integer interval
long nchar nclob number numeric nvarchar2 pls_integer natural naturaln positive positiven signtype simple_integer raw real rowid
smallint timestamp urowid varchar varchar2 xmltype json vector sys_refcursor bfile anydata`.split(/\s+/);

export const ORACLE_FUNCTIONS = `abs acos add_months ascii asin atan avg bitand cast ceil chr coalesce concat cos count cume_dist current_date
current_timestamp dbtimezone decode dense_rank dump empty_blob empty_clob exp extract first_value floor from_tz greatest initcap instr
json_arrayagg json_object json_objectagg json_query json_table json_value lag last_day last_value lead least length listagg ln
localtimestamp log lower lpad ltrim max median min mod months_between nanvl new_time next_day nls_lower nls_upper ntile nullif
numtodsinterval numtoyminterval nvl nvl2 percent_rank power rank ratio_to_report rawtohex regexp_count regexp_instr regexp_like
regexp_replace regexp_substr remainder replace round row_number rpad rtrim sessiontimezone sign sin soundex sqrt stddev substr sum
sys_context sys_extract_utc sys_guid systimestamp tan to_binary_double to_char to_clob to_date to_dsinterval to_number to_timestamp
to_timestamp_tz to_yminterval translate treat trim trunc tz_offset upper userenv variance vsize width_bucket xmlagg xmlelement`.split(/\s+/);

/** Oracle `q'X…X'` delimiters that close with their partner, and the state each opens. */
const ORACLE_Q_PAIRS: [string, string, string][] = [["[", "]", "qBracket"], ["{", "}", "qBrace"], ["(", ")", "qParen"], ["<", ">", "qAngle"]];

const escapeRegExp = (c: string) => c.replace(/[\\^$.*+?()[\]{}|/-]/g, "\\$&");

/**
 * Oracle (D4 S06), the lexical forms of the sidecar's OracleTokenizer: '' strings, N'' and alternative quoting
 * q'X…X' / nq'X…X' (any delimiter; [ { ( < close with their partner; a ' alone may stand inside), "" names (no escape),
 * -- and non-nesting block comments with /*+ hints *\/ and --+ hints, :name / :1 / :"name" binds (:= is an operator),
 * name@link database links, <<labels>>, and a / alone on a line ending a PL/SQL unit.
 */
export function oracleLanguage(): Monaco.languages.IMonarchLanguage {
  const qStates: Record<string, Monaco.languages.IMonarchLanguageRule[]> = {};
  for (const [, close, state] of ORACLE_Q_PAIRS) {
    qStates[state] = [
      [new RegExp(`${escapeRegExp(close)}'`), { token: "string.quote", next: "@pop" }],
      [new RegExp(`[^${escapeRegExp(close)}]+`), "string"],
      [/./, "string"],
    ];
  }
  return {
    defaultToken: "",
    ignoreCase: true,
    tokenPostfix: ".oracle",
    keywords: ORACLE_KEYWORDS.map((k) => k.toLowerCase()),
    typeKeywords: ORACLE_TYPES,
    builtinFunctions: ORACLE_FUNCTIONS,
    operators: [],
    tokenizer: {
      root: [
        [/^\s*\/\s*$/, "keyword.batch"],
        { include: "@comments" },
        [/\s+/, "white"],
        ...ORACLE_Q_PAIRS.map(([open, , state]): Monaco.languages.IMonarchLanguageRule =>
          [new RegExp(`n?q'${escapeRegExp(open)}`), { token: "string.quote", next: `@${state}` }]),
        // Any other delimiter: the state carries it ('.' has a state of its own: a state name splits on dots).
        [/n?q'\./, { token: "string.quote", next: "@qDot" }],
        [/n?q'(\S)/, { token: "string.quote", next: "@qAny.$1" }],
        [/n?'/, { token: "string.quote", next: "@string" }],
        [/"/, { token: "identifier.quote", next: "@quoted" }],
        [/:"[^"]*"?/, "variable"],
        [/:(?!=)[A-Za-z0-9_$#]+/, "variable"],
        [/<<[A-Za-z][\w$#]*>>/, "identifier.label"],
        [/[0-9]+(\.(?!\.)[0-9]*)?([eE][+-]?[0-9]+)?[fFdD]?/, "number"],
        [/\.[0-9]+([eE][+-]?[0-9]+)?[fFdD]?/, "number"],
        [/[;,.]/, "delimiter"],
        [/[()]/, "@brackets"],
        [/@[A-Za-z][\w$#.]*/, "identifier.link"],
        [/[A-Za-z][\w$#]*/, wordCases],
        [/\|\||:=|=>|\.\.|\*\*|<>|!=|\^=|~=|<=|>=|[<>=+\-*/%@!^~&|]/, "operator"],
      ],
      comments: [
        [/--\+.*/, "comment.hint"],
        [/--.*/, "comment"],
        [/\/\*\+/, { token: "comment.hint", next: "@hint" }],
        [/\/\*/, { token: "comment.quote", next: "@comment" }],
      ],
      comment: [
        [/[^*]+/, "comment"],
        [/\*\//, { token: "comment.quote", next: "@pop" }],
        [/\*/, "comment"],
      ],
      hint: [
        [/[^*]+/, "comment.hint"],
        [/\*\//, { token: "comment.hint", next: "@pop" }],
        [/\*/, "comment.hint"],
      ],
      string: [
        [/[^']+/, "string"],
        [/''/, "string"],
        [/'/, { token: "string.quote", next: "@pop" }],
      ],
      quoted: [
        [/[^"]+/, "identifier"],
        [/"/, { token: "identifier.quote", next: "@pop" }],
      ],
      ...qStates,
      qDot: [
        [/\.'/, { token: "string.quote", next: "@pop" }],
        [/[^.]+/, "string"],
        [/./, "string"],
      ],
      qAny: [
        [/(\S)'/, { cases: { "$1==$S2": { token: "string.quote", next: "@pop" }, "@default": "string" } }],
        [/./, "string"],
      ],
    },
  } as Monaco.languages.IMonarchLanguage;
}

/** Oracle: "" quote names (never nested, so not pairs); only ( ) pair. Folding is the sidecar splitter's, per statement and PL/SQL unit. */
export const oracleLanguageConfiguration: Monaco.languages.LanguageConfiguration = {
  ...sharedConfiguration,
  brackets: [["(", ")"]],
  colorizedBracketPairs: [["(", ")"]],
};

export interface EditorLanguage {
  /** Names Monaco shows for the language, and the file extensions it claims. */
  aliases: string[];
  extensions: string[];
  tokens: () => Monaco.languages.IMonarchLanguage;
  configuration: Monaco.languages.LanguageConfiguration;
  /** The word lists the highlighter knows (auto-alias never picks one of them). */
  words: string[][];
}

export const LANGUAGES: Record<Dialect, EditorLanguage> = {
  tsql: {
    aliases: ["T-SQL", "tsql"], extensions: [".sql"], tokens: tsqlLanguage, configuration: tsqlLanguageConfiguration,
    words: [TSQL_KEYWORDS, TSQL_TYPES, TSQL_FUNCTIONS],
  },
  pg: {
    aliases: ["PostgreSQL", "pgsql"], extensions: [".psql", ".pgsql"], tokens: pgLanguage, configuration: pgLanguageConfiguration,
    words: [PG_KEYWORDS, PG_TYPES, PG_FUNCTIONS],
  },
  // No file extensions: .sql stays T-SQL's claim; a tab takes its language from its connection.
  sqlite: {
    aliases: ["SQLite", "sqlite"], extensions: [], tokens: sqliteLanguage, configuration: sqliteLanguageConfiguration,
    words: [SQLITE_KEYWORDS, SQLITE_TYPES, SQLITE_FUNCTIONS],
  },
  mySql: {
    aliases: ["MySQL", "mysql"], extensions: [], tokens: mysqlLanguage, configuration: mysqlLanguageConfiguration,
    words: [MYSQL_KEYWORDS, MYSQL_TYPES, MYSQL_FUNCTIONS],
  },
  duckDb: {
    aliases: ["DuckDB", "duckdb"], extensions: [], tokens: duckdbLanguage, configuration: duckdbLanguageConfiguration,
    words: [DUCKDB_KEYWORDS, DUCKDB_TYPES, DUCKDB_FUNCTIONS],
  },
  oracle: {
    aliases: ["Oracle", "oracle"], extensions: [], tokens: oracleLanguage, configuration: oracleLanguageConfiguration,
    words: [ORACLE_KEYWORDS, ORACLE_TYPES, ORACLE_FUNCTIONS],
  },
};

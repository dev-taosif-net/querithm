-- T1 corpus, SQLite: every Monarch rule of sqliteLanguage() fires at least once (highlightGolden.test.ts checks it).
SELECT ?, ?7, :name, @name, $name, X'0aFF', x'zz', "dq""x", `bt``x`, [br x], 0x1F, 1.5e3, .5, 10., 'it''s'
FROM t WHERE a->>'$.k' = b->'c' AND c || d AND e == f AND g << 2 AND h >> 1 AND i <> j AND k != l AND m <= n AND o >= p;
SELECT TRUE, FALSE, NOT TRUE, a IS NOT NULL, b GLOB 'x*', c REGEXP 'y', d MATCH 'z', e ISNULL, f NOTNULL, (g & 1) | ~h % 2;
/* not /* nested * still comment */ SELECT 1;
/* across
   lines */
CREATE TEMP TABLE IF NOT EXISTS main.t (id INTEGER PRIMARY KEY, b BLOB, r REAL) WITHOUT ROWID;
SELECT group_concat(name, ','), json_extract(doc, '$.a'), typeof(x) FROM main.t;

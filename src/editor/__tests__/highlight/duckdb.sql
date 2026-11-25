-- T1 corpus, DuckDB: every Monarch rule of duckdbLanguage() fires at least once (highlightGolden.test.ts checks it).
SELECT $1, $name, ?, a::INTEGER, b :: "T"[], c::"Mixed", E'esc\n''q', 'it''s', "dq""x", 0x1F, 0XAB, 1.5e3, 10., {'k': 1}, [1, 2];
SELECT a:b, a @> b, c ->> d, e || f, g <= h, (i & 1) | ~j ^ k % 2 # 1, a ILIKE 'x', b GLOB 'y', c SIMILAR TO 'z' FROM t;
SELECT $tag$ body $other$ still $ string $tag$, $$ plain $$, TRUE, FALSE, NULL IS NOT NULL;
SELECT $$ across
lines $ $$;
/* outer /* nested */ * / still */ SELECT 1;
/* across
   lines */
FROM read_parquet('x.parquet') QUALIFY row_number() OVER (PARTITION BY a ORDER BY b) = 1;

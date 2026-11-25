-- T1 corpus, MySQL: every Monarch rule of mysqlLanguage() fires at least once (highlightGolden.test.ts checks it).
SELECT @@session.sql_mode, @@version, @user_var, ?, x'4D', B'101', N'nat', 'it''s \'esc\'', "dq \"esc\" ""x""", `bt``x`,
       0x1F, 0b101, 1.5e3, .5, 10., a <=> b, j->>'$.k', j->'$.k', @v := 1, a || b, a && b, a << 2, b >> 1, 5 DIV 2, 5 MOD 2;
SELECT a XOR b, c RLIKE 'x', d REGEXP 'y', e IS NOT NULL, NOT TRUE, FALSE, f <= g, h >= i, j <> k, l != m, (n & 1) | ~o ^ p % 2;
SELECT a FROM t # hash comment
-- dash comment
--not-a-comment
/* not /* nested * still comment */ SELECT 1;
/*! executable
   across lines */
SELECT $dollar_name, sch.t.c FROM sch.t WHERE id = ? LIMIT ?, ?;

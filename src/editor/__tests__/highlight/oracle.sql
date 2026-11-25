-- T1 corpus, Oracle: every Monarch rule of oracleLanguage() fires at least once (highlightGolden.test.ts checks it).
SELECT q'[it's]', q'[a]b]', q'{a}b}', q'(a)b)', q'<a>b>', q'!it's!', nq'#x#', q'.a.b'c.', n'nat', 'it''s', "Name", :name, :1, :"Mixed"
FROM emp@hq.example e WHERE e.a = :b AND x := 1 AND y => 2 AND 1..10 AND 2 ** 3 AND a <> b AND c != d AND e ^= f AND g ~= h;
SELECT 1.5e3f, 2D, .5d, 10., 3 || 4, a <= b, c >= d, e % f, @g, !h, ~i & j | k, TRUE, FALSE, NULL, l IS NOT NULL FROM dual;
SELECT /*+ FULL(e) * hint */ e.id, q'[multi
line]' FROM emp e --+ ordered
/* plain * comment */ WHERE 1 = 1 -- trailing
/* across
   lines */;
<<outer>> BEGIN
  NULL;
END;
/
  /
SELECT :"open
FROM dual;
